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

        // Guard against zero/negative volume passed in before the user has changed it
        if (volumePercent <= 0) volumePercent = 100;
        _volume = Math.Clamp(volumePercent / 100f, 0f, 1f);
        DebugLog.Log("GaplessPCM", $"Play: volume={volumePercent} ({_volume:F2})");
        _activeDecoder = _decoderA;
        _pendingDecoder = _decoderB;
        _activeDecoder.Reset();
        _pendingDecoder.Reset();

        _activeDecoder.Start(streamUri);

        // Wait briefly for VLC to start producing samples before opening output
        SpinWait.SpinUntil(() => !_activeDecoder.Queue.IsEmpty || _activeDecoder.IsFinished, 3000);

        _output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 50);
        _output.Init(_mixer);
        // NOTE: do NOT set _output.Volume — that writes to Windows' per-app mixer slider.
        // Volume is applied in GaplessMixer.Read() via _volume instead.
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
        // Volume is applied per-sample in GaplessMixer.Read()
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
        // Max leading/trailing silence to trim per track: 4096 samples per channel
        // (~93ms at 44.1kHz). Covers AAC priming (~48ms) and MP3 LAME priming (~25ms)
        // without eating into legitimate fade-ins/outs.
        private const int MaxSilenceTrimSamplesPerChannel = 4096;

        public string Name { get; }
        public ConcurrentQueue<float[]> Queue { get; } = new();
        public volatile bool IsFinished;
        public volatile bool IsStarted;
        public bool DrainSignaled => _drainSignaled;
        private long _drainedSamples;
        private long _durationMs;
        private MediaPlayer? _player;
        private Media? _media;
        private readonly LibVLC _libVLC;
        private readonly ManualResetEventSlim _queueGate = new(true);
        private volatile int _queuedChunks;

        // Silence trim state
        private bool _seenAudio;
        private int _leadingTrimBudgetShorts; // remaining S16 samples (interleaved) we may still trim from the head
        private int _leadingTrimmedShorts;    // total trimmed from head, for logging
        private long _producedFloats;         // total float samples (interleaved) ever enqueued
        private long _playableFloatLimit = long.MaxValue; // mixer stops dequeuing once consumed >= this
        private long _consumedFloats;         // total float samples handed to NAudio

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
            _drainSignaled = false;
            _seenAudio = false;
            _leadingTrimBudgetShorts = MaxSilenceTrimSamplesPerChannel * Channels;
            _leadingTrimmedShorts = 0;
            Interlocked.Exchange(ref _producedFloats, 0);
            Interlocked.Exchange(ref _consumedFloats, 0);
            _playableFloatLimit = long.MaxValue;
            _callbackLogCount = 0;
            Interlocked.Exchange(ref _drainedSamples, 0);
            Interlocked.Exchange(ref _durationMs, 0);

            var player = new MediaPlayer(_libVLC);

            // Pin delegates to prevent GC collection (VLC holds native pointers to these)
            _playCb = OnAudioPlay;
            _pauseCb = OnAudioPause;
            _resumeCb = OnAudioResume;
            _flushCb = OnAudioFlush;
            _drainCb = OnAudioDrain;

            player.SetAudioFormat("S16N", SampleRate, Channels);
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

        /// <summary>Called by the mixer with the number of float samples actually written to NAudio.</summary>
        public void NotifyConsumed(int floatSampleCount)
        {
            Interlocked.Add(ref _consumedFloats, floatSampleCount);
        }

        /// <summary>
        /// True once drain has been signaled and the mixer has consumed all
        /// non-silent (trimmed) samples. Indicates the track is musically done.
        /// </summary>
        public bool ReachedPlayableEnd =>
            _drainSignaled && Interlocked.Read(ref _consumedFloats) >= _playableFloatLimit;

        /// <summary>Remaining playable float samples (interleaved) the mixer should still drain.</summary>
        public long RemainingPlayableFloats =>
            Math.Max(0, _playableFloatLimit - Interlocked.Read(ref _consumedFloats));

        private volatile int _callbackLogCount;

        private void OnAudioPlay(IntPtr data, IntPtr samples, uint count, long pts)
        {
            // VLC delivers S16N: signed 16-bit native-endian samples, interleaved
            int totalShorts = (int)count * Channels;
            var shortBuffer = new short[totalShorts];
            Marshal.Copy(samples, shortBuffer, 0, totalShorts);

            // Leading silence trim: drop bit-exact zero samples from the head of the
            // first chunks (AAC/MP3 encoder priming). Cap by _leadingTrimBudgetShorts
            // so legitimate quiet intros aren't eaten.
            int startOffset = 0;
            if (!_seenAudio && _leadingTrimBudgetShorts > 0)
            {
                int maxScan = Math.Min(totalShorts, _leadingTrimBudgetShorts);
                while (startOffset < maxScan && shortBuffer[startOffset] == 0)
                    startOffset++;

                _leadingTrimBudgetShorts -= startOffset;
                _leadingTrimmedShorts += startOffset;

                if (startOffset < totalShorts)
                {
                    _seenAudio = true;
                    if (_leadingTrimmedShorts > 0)
                        DebugLog.Log("GaplessPCM", $"Decoder {Name} trimmed {_leadingTrimmedShorts / Channels} leading silent samples ({_leadingTrimmedShorts * 1000L / (SampleRate * Channels)}ms)");
                }
                else if (_leadingTrimBudgetShorts <= 0)
                {
                    // Budget exhausted without finding audio — stop trimming
                    _seenAudio = true;
                    DebugLog.Log("GaplessPCM", $"Decoder {Name} leading trim budget exhausted at {_leadingTrimmedShorts / Channels} samples");
                }
            }

            // Align startOffset to a channel boundary so we don't swap L/R
            startOffset -= startOffset % Channels;

            int usableShorts = totalShorts - startOffset;
            if (usableShorts <= 0)
                return; // entire chunk was trimmed

            // Convert to float32 [-1.0, 1.0] for NAudio
            var buffer = new float[usableShorts];
            const float scale = 1.0f / 32768.0f;
            for (int i = 0; i < usableShorts; i++)
                buffer[i] = shortBuffer[startOffset + i] * scale;

            if (_callbackLogCount < 3)
            {
                int n = Interlocked.Increment(ref _callbackLogCount);
                float peak = 0;
                for (int i = 0; i < usableShorts; i++)
                {
                    float v = Math.Abs(buffer[i]);
                    if (v > peak) peak = v;
                }
                DebugLog.Log("GaplessPCM", $"Decoder {Name} cb#{n}: count={count} peak={peak:F4} s16[0]={shortBuffer[startOffset]} s16[1]={(usableShorts > 1 ? shortBuffer[startOffset + 1] : (short)0)}");
            }

            if (Interlocked.Increment(ref _queuedChunks) >= MaxQueuedChunks)
            {
                _queueGate.Reset();
                _queueGate.Wait(1000);
            }

            Interlocked.Add(ref _producedFloats, usableShorts);
            Queue.Enqueue(buffer);
        }

        private void OnAudioPause(IntPtr data, long pts) { }
        private void OnAudioResume(IntPtr data, long pts) { }

        private volatile bool _drainSignaled;

        private void OnAudioFlush(IntPtr data, long pts)
        {
            // VLC calls Flush at end-of-stream right after Drain — this would
            // throw away the final ~2 seconds of buffered PCM. Ignore flushes
            // that occur after drain; they're the EOS teardown, not a real
            // seek/reset where we'd want to discard buffered audio.
            if (_drainSignaled)
            {
                DebugLog.Log("GaplessPCM", $"Decoder {Name} ignoring post-drain flush (preserving {_queuedChunks} buffered chunks)");
                return;
            }

            DebugLog.Log("GaplessPCM", $"Decoder {Name} OnAudioFlush (queue had {_queuedChunks} chunks)");
            while (Queue.TryDequeue(out _)) { }
            _queuedChunks = 0;
            _queueGate.Set();
        }

        private void OnAudioDrain(IntPtr data)
        {
            DebugLog.Log("GaplessPCM", $"Decoder {Name} OnAudioDrain (queue has {_queuedChunks} chunks)");

            // Compute trailing silence trim: snapshot the queue (no more chunks will be
            // enqueued after drain), walk from the tail counting bit-exact zero floats,
            // and set the playable limit so the mixer stops consuming before that point.
            // Capped by MaxSilenceTrimSamplesPerChannel to protect legitimate fade-outs.
            var snapshot = Queue.ToArray();
            long maxTrimFloats = (long)MaxSilenceTrimSamplesPerChannel * Channels;
            long trailingZeroFloats = 0;
            for (int i = snapshot.Length - 1; i >= 0 && trailingZeroFloats < maxTrimFloats; i--)
            {
                var chunk = snapshot[i];
                int j = chunk.Length - 1;
                while (j >= 0 && chunk[j] == 0f && trailingZeroFloats < maxTrimFloats)
                {
                    trailingZeroFloats++;
                    j--;
                }
                if (j >= 0) break; // hit a non-zero sample in this chunk
            }

            // Align to channel boundary so we don't truncate one channel of a stereo pair
            trailingZeroFloats -= trailingZeroFloats % Channels;

            long produced = Interlocked.Read(ref _producedFloats);
            _playableFloatLimit = produced - trailingZeroFloats;

            if (trailingZeroFloats > 0)
                DebugLog.Log("GaplessPCM", $"Decoder {Name} trimming {trailingZeroFloats / Channels} trailing silent samples ({trailingZeroFloats * 1000L / (SampleRate * Channels)}ms)");

            _drainSignaled = true;
            // Don't set IsFinished here — wait until the mixer has drained our
            // queue. The mixer detects empty-queue + drain to switch tracks.
        }

        private void OnEndReached(object? sender, EventArgs e)
        {
            DebugLog.Log("GaplessPCM", $"Decoder {Name} EndReached (queue has {_queuedChunks} chunks, drained={_drainSignaled})");
            // Don't set IsFinished here either — wait for the mixer to fully
            // drain the queue before declaring this decoder finished.
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
            int writeStart = floatsWritten;

            while (floatsWritten < floatsNeeded)
            {
                // If we've consumed all the playable (non-trailing-silence) samples,
                // treat the track as done even if the queue still has zero-samples.
                if (_owner._activeDecoder.ReachedPlayableEnd && _currentChunk == null)
                {
                    _owner._activeDecoder.IsFinished = true;

                    if (_owner.TryAdvanceToNext())
                    {
                        _currentChunk = null;
                        _currentOffset = 0;
                        continue;
                    }

                    floatSpan.Slice(floatsWritten).Clear();
                    ApplyVolume(floatSpan.Slice(writeStart, floatsWritten - writeStart));
                    return 0;
                }

                if (_currentChunk != null && _currentOffset < _currentChunk.Length)
                {
                    int available = _currentChunk.Length - _currentOffset;
                    int toCopy = Math.Min(available, floatsNeeded - floatsWritten);

                    // Clamp to remaining playable samples (trailing-silence trim)
                    long remainingPlayable = _owner._activeDecoder.RemainingPlayableFloats;
                    if (remainingPlayable < toCopy)
                        toCopy = (int)remainingPlayable;

                    if (toCopy > 0)
                    {
                        _currentChunk.AsSpan(_currentOffset, toCopy).CopyTo(floatSpan.Slice(floatsWritten));
                        floatsWritten += toCopy;
                        _currentOffset += toCopy;
                        _owner._activeDecoder.NotifyConsumed(toCopy);
                    }

                    if (_currentOffset >= _currentChunk.Length || toCopy == 0)
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
                if (_owner._activeDecoder.IsFinished || _owner._activeDecoder.DrainSignaled)
                {
                    // Mark finished so subsequent reads short-circuit
                    _owner._activeDecoder.IsFinished = true;

                    if (_owner.TryAdvanceToNext())
                    {
                        _currentChunk = null;
                        _currentOffset = 0;
                        continue;
                    }

                    // Nothing more to play
                    floatSpan.Slice(floatsWritten).Clear();
                    ApplyVolume(floatSpan.Slice(writeStart, floatsWritten - writeStart));
                    return 0;
                }

                // Brief underrun — fill with silence
                int remaining = floatsNeeded - floatsWritten;
                floatSpan.Slice(floatsWritten, remaining).Clear();
                floatsWritten += remaining;
            }

            ApplyVolume(floatSpan);
            return floatsWritten * sizeof(float);
        }

        private void ApplyVolume(Span<float> samples)
        {
            float vol = _owner._volume;
            if (vol >= 0.999f) return;
            for (int i = 0; i < samples.Length; i++)
                samples[i] *= vol;
        }
    }
}