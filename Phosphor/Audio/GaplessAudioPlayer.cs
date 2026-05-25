using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using NAudio.Wave;

namespace Phosphor.Audio;

/// <summary>
/// Gapless audio player for Plex audio-only tracks. Uses two LibVLC MediaPlayers
/// as decode-only engines via SetAudioCallbacks, dumping raw PCM into memory queues.
/// A single NAudio WasapiOut reads from a GaplessMixer (IWaveProvider) that drains
/// the active queue and seamlessly switches to the next when a track ends.
/// </summary>
public sealed class GaplessAudioPlayer : IDisposable
{
    // Audio format: 44100 Hz, stereo, float32 (IEEE)
    private const int SampleRate = 44100;
    private const int Channels = 2;
    private static readonly WaveFormat OutputFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    // Cap each queue at ~5 seconds of audio to prevent runaway memory usage
    private const int MaxQueuedChunks = (SampleRate * Channels * sizeof(float) * 5) / 4096 + 1;

    private readonly LibVLC _libVLC;
    private readonly Decoder _decoderA;
    private readonly Decoder _decoderB;
    private Decoder _activeDecoder;
    private Decoder _pendingDecoder;
    private readonly GaplessMixer _mixer;
    private WasapiOut? _output;
    private float _volume = 1.0f;
    private bool _isPaused;
    private bool _disposed;

    /// <summary>
    /// Fired on a background thread when the active track finishes and the mixer
    /// switches to the next queue. The BackglassWindow should call AdvanceQueueGapless.
    /// If no next track was primed, this signals end-of-playback.
    /// </summary>
    public event Action<bool>? TrackAdvanced; // bool hasNext

    /// <summary>
    /// Fired when all queued audio has been drained and no next track is available.
    /// </summary>
    public event Action? PlaybackFinished;

    public GaplessAudioPlayer(LibVLC libVLC)
    {
        _libVLC = libVLC;
        _decoderA = new Decoder(libVLC, "A");
        _decoderB = new Decoder(libVLC, "B");
        _activeDecoder = _decoderA;
        _pendingDecoder = _decoderB;
        _mixer = new GaplessMixer(this);
    }

    /// <summary>Current playback position in milliseconds (estimated from drained samples).</summary>
    public long PositionMs => _activeDecoder.DrainedMs;

    /// <summary>Track duration in milliseconds (from VLC metadata).</summary>
    public long DurationMs => _activeDecoder.DurationMs;

    /// <summary>Whether playback is currently active.</summary>
    public bool IsPlaying => _output != null && !_disposed;

    /// <summary>
    /// Start playing a track. Stops any current playback first.
    /// </summary>
    public void Play(Uri streamUri, int volumePercent = 100)
    {
        Stop();

        _volume = Math.Clamp(volumePercent / 100f, 0f, 1f);
        _activeDecoder = _decoderA;
        _pendingDecoder = _decoderB;
        _activeDecoder.Reset();
        _pendingDecoder.Reset();

        _activeDecoder.Start(streamUri);

        // Wait briefly for VLC to start producing samples before opening output
        SpinWait.SpinUntil(() => !_activeDecoder.Queue.IsEmpty || _activeDecoder.IsFinished, 3000);

        _output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 50);
        _output.Init(_mixer);
        _output.Volume = _volume;
        _output.PlaybackStopped += OnOutputStopped;
        _output.Play();
        _isPaused = false;
    }

    /// <summary>
    /// Prime the next track on the idle decoder so it's ready for gapless transition.
    /// </summary>
    public void PrimeNext(Uri streamUri)
    {
        _pendingDecoder.Reset();
        _pendingDecoder.Start(streamUri);
        DebugLog.Log("GaplessPCM", $"Primed next track on decoder {_pendingDecoder.Name}");
    }

    /// <summary>
    /// Whether a next track has been primed and has data available.
    /// </summary>
    public bool HasPrimedNext => !_pendingDecoder.Queue.IsEmpty || _pendingDecoder.IsStarted;

    public void SetVolume(int volumePercent)
    {
        _volume = Math.Clamp(volumePercent / 100f, 0f, 1f);
        if (_output != null)
            _output.Volume = _volume;
    }

    public void Pause()
    {
        if (_output != null && !_isPaused)
        {
            _output.Pause();
            _isPaused = true;
        }
    }

    public void Resume()
    {
        if (_output != null && _isPaused)
        {
            _output.Play();
            _isPaused = false;
        }
    }

    public void Seek(long timeMs)
    {
        _activeDecoder.Seek(timeMs);
    }

    public void Stop()
    {
        var output = _output;
        _output = null;
        if (output != null)
        {
            output.PlaybackStopped -= OnOutputStopped;
            try { output.Stop(); } catch { }
            try { output.Dispose(); } catch { }
        }

        _decoderA.Stop();
        _decoderB.Stop();
        _isPaused = false;
    }

    private void OnOutputStopped(object? sender, StoppedEventArgs e)
    {
        PlaybackFinished?.Invoke();
    }

    /// <summary>
    /// Called by the mixer when the active queue is exhausted and the active decoder
    /// has finished. Attempts to switch to the pending decoder's queue.
    /// Returns true if switched successfully (gapless transition), false if nothing primed.
    /// </summary>
    internal bool TryAdvanceToNext()
    {
        if (_pendingDecoder.Queue.IsEmpty && !_pendingDecoder.IsStarted)
            return false;

        DebugLog.Log("GaplessPCM", $"Switching from decoder {_activeDecoder.Name} to {_pendingDecoder.Name}");

        var oldActive = _activeDecoder;
        _activeDecoder = _pendingDecoder;
        _pendingDecoder = oldActive;

        Task.Run(() => _pendingDecoder.Stop());

        TrackAdvanced?.Invoke(true);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _decoderA.Dispose();
        _decoderB.Dispose();
    }

    // ── Inner: Decoder ──

    private sealed class Decoder : IDisposable
    {
        public string Name { get; }
        public ConcurrentQueue<float[]> Queue { get; } = new();
        public volatile bool IsFinished;
        public volatile bool IsStarted;
        private long _drainedSamples;
        private long _durationMs;
        private MediaPlayer? _player;
        private Media? _media;
        private readonly LibVLC _libVLC;
        private readonly ManualResetEventSlim _queueGate = new(true);
        private volatile int _queuedChunks;

        // Pin callback delegates so the GC doesn't collect them while VLC holds native pointers
        private LibVLCSharp.Shared.MediaPlayer.LibVLCAudioPlayCb? _playCb;
        private LibVLCSharp.Shared.MediaPlayer.LibVLCAudioPauseCb? _pauseCb;
        private LibVLCSharp.Shared.MediaPlayer.LibVLCAudioResumeCb? _resumeCb;
        private LibVLCSharp.Shared.MediaPlayer.LibVLCAudioFlushCb? _flushCb;
        private LibVLCSharp.Shared.MediaPlayer.LibVLCAudioDrainCb? _drainCb;

        public long DrainedMs
        {
            get
            {
                long samples = Interlocked.Read(ref _drainedSamples);
                return samples / Channels * 1000 / SampleRate;
            }
        }

        public long DurationMs => Interlocked.Read(ref _durationMs);

        public Decoder(LibVLC libVLC, string name)
        {
            _libVLC = libVLC;
            Name = name;
        }

        public void Start(Uri streamUri)
        {
            Stop();
            IsFinished = false;
            IsStarted = true;
            _loggedFirstCallback = false;
            Interlocked.Exchange(ref _drainedSamples, 0);
            Interlocked.Exchange(ref _durationMs, 0);

            var player = new MediaPlayer(_libVLC);

            // Pin delegates to prevent GC collection (VLC holds native pointers to these)
            _playCb = OnAudioPlay;
            _pauseCb = OnAudioPause;
            _resumeCb = OnAudioResume;
            _flushCb = OnAudioFlush;
            _drainCb = OnAudioDrain;

            player.SetAudioFormat("FL32", SampleRate, Channels);
            player.SetAudioCallbacks(_playCb, _pauseCb, _resumeCb, _flushCb, _drainCb);

            DebugLog.Log("GaplessPCM", $"Decoder {Name} starting: {streamUri}");

            player.EndReached += OnEndReached;
            player.LengthChanged += OnLengthChanged;

            var media = new Media(_libVLC, streamUri);
            media.AddOption(":no-video");
            _media = media;
            _player = player;

            player.Play(media);
        }

        public void Stop()
        {
            IsStarted = false;
            IsFinished = true;
            _queueGate.Set();

            var player = _player;
            _player = null;
            if (player != null)
            {
                player.EndReached -= OnEndReached;
                player.LengthChanged -= OnLengthChanged;
                try { player.Stop(); } catch { }
                try { player.Dispose(); } catch { }
            }

            var media = _media;
            _media = null;
            media?.Dispose();

            while (Queue.TryDequeue(out _)) { }
            _queuedChunks = 0;
        }

        public void Reset()
        {
            Stop();
            IsFinished = false;
            Interlocked.Exchange(ref _drainedSamples, 0);
            Interlocked.Exchange(ref _durationMs, 0);
        }

        public void Seek(long timeMs)
        {
            while (Queue.TryDequeue(out _)) { }
            _queuedChunks = 0;
            Interlocked.Exchange(ref _drainedSamples, timeMs * SampleRate / 1000 * Channels);
            _queueGate.Set();

            if (_player != null && _player.Length > 0)
                _player.Time = Math.Clamp(timeMs, 0, _player.Length);
        }

        public void NotifyDrained(int floatSampleCount)
        {
            Interlocked.Add(ref _drainedSamples, floatSampleCount);
            int count = Interlocked.Decrement(ref _queuedChunks);
            if (count < MaxQueuedChunks)
                _queueGate.Set();
        }

        private volatile bool _loggedFirstCallback;

        private void OnAudioPlay(IntPtr data, IntPtr samples, uint count, long pts)
        {
            int totalFloats = (int)count * Channels;
            var buffer = new float[totalFloats];
            Marshal.Copy(samples, buffer, 0, totalFloats);

            if (!_loggedFirstCallback && totalFloats > 0)
            {
                _loggedFirstCallback = true;
                float peak = 0;
                for (int i = 0; i < Math.Min(totalFloats, 100); i++)
                    peak = Math.Max(peak, Math.Abs(buffer[i]));
                DebugLog.Log("GaplessPCM", $"Decoder {Name} first callback: count={count} totalFloats={totalFloats} peakSample={peak:F6} pts={pts}");
            }

            if (Interlocked.Increment(ref _queuedChunks) >= MaxQueuedChunks)
            {
                _queueGate.Reset();
                _queueGate.Wait(1000);
            }

            Queue.Enqueue(buffer);
        }

        private void OnAudioPause(IntPtr data, long pts) { }
        private void OnAudioResume(IntPtr data, long pts) { }

        private void OnAudioFlush(IntPtr data, long pts)
        {
            while (Queue.TryDequeue(out _)) { }
            _queuedChunks = 0;
            _queueGate.Set();
        }

        private void OnAudioDrain(IntPtr data) { }

        private void OnEndReached(object? sender, EventArgs e)
        {
            DebugLog.Log("GaplessPCM", $"Decoder {Name} EndReached");
            IsFinished = true;
        }

        private void OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
        {
            Interlocked.Exchange(ref _durationMs, e.Length);
        }

        public void Dispose()
        {
            Stop();
            _queueGate.Dispose();
        }
    }

    // ── Inner: GaplessMixer ──

    private sealed class GaplessMixer : IWaveProvider
    {
        private readonly GaplessAudioPlayer _owner;
        private float[]? _currentChunk;
        private int _currentOffset;

        public WaveFormat WaveFormat => OutputFormat;

        public GaplessMixer(GaplessAudioPlayer owner)
        {
            _owner = owner;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            var floatSpan = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count));
            int floatsNeeded = floatSpan.Length;
            int floatsWritten = 0;

            while (floatsWritten < floatsNeeded)
            {
                if (_currentChunk != null && _currentOffset < _currentChunk.Length)
                {
                    int available = _currentChunk.Length - _currentOffset;
                    int toCopy = Math.Min(available, floatsNeeded - floatsWritten);
                    _currentChunk.AsSpan(_currentOffset, toCopy).CopyTo(floatSpan.Slice(floatsWritten));
                    floatsWritten += toCopy;
                    _currentOffset += toCopy;

                    if (_currentOffset >= _currentChunk.Length)
                    {
                        _owner._activeDecoder.NotifyDrained(_currentChunk.Length);
                        _currentChunk = null;
                        _currentOffset = 0;
                    }
                    continue;
                }

                if (_owner._activeDecoder.Queue.TryDequeue(out var chunk))
                {
                    _currentChunk = chunk;
                    _currentOffset = 0;
                    continue;
                }

                // Active queue is empty
                if (_owner._activeDecoder.IsFinished)
                {
                    if (_owner.TryAdvanceToNext())
                    {
                        _currentChunk = null;
                        _currentOffset = 0;
                        continue;
                    }

                    // Nothing more to play
                    floatSpan.Slice(floatsWritten).Clear();
                    return 0;
                }

                // Brief underrun — fill with silence
                int remaining = floatsNeeded - floatsWritten;
                floatSpan.Slice(floatsWritten, remaining).Clear();
                floatsWritten += remaining;
            }

            return floatsWritten * sizeof(float);
        }
    }
}