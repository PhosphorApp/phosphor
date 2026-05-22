using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using System.Windows.Threading;

namespace Phosphor;

/// <summary>
/// Audio-reactive data passed to subscribers each tick.
/// </summary>
public struct AudioReactiveData
{
    /// <summary>Overall smoothed audio level (0.0 – 1.0).</summary>
    public float Level;
    /// <summary>True for one tick when a beat/transient is detected.</summary>
    public bool IsBeat;
    /// <summary>Low-frequency (bass) energy (0.0 – 1.0). Drives size pulsing.</summary>
    public float Bass;
    /// <summary>High-frequency (treble) energy (0.0 – 1.0). Drives hue shift.</summary>
    public float Treble;
}

/// <summary>
/// Captures system audio output via WASAPI loopback and exposes a smoothed amplitude level,
/// beat detection, and FFT-derived bass/treble energy for driving reactive blob visuals.
/// </summary>
public sealed class AudioReactiveService : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private MMDevice? _audioDevice;
    private readonly DispatcherTimer _timer;
    private float _currentPeak;
    private float _smoothedLevel;
    private float _prevSmoothedLevel;
    private const float BeatThreshold = 0.12f;
    private bool _beatDetected;

    // FFT state
    private const int FftLength = 1024;
    private const int FftExponent = 10; // 2^10 = 1024
    private readonly Complex[] _fftBuffer = new Complex[FftLength];
    private int _fftPos;
    private float _currentBass;
    private float _currentTreble;
    private float _smoothedBass;
    private float _smoothedTreble;
    private int _sampleRate = 48000;

    // Raw PCM ring buffer for projectM — stores the most recent mono-mixed samples
    private static readonly object _pcmLock = new();
    private static float[]? _rawPcmBuffer;
    private static int _rawPcmLength; // valid sample count within _rawPcmBuffer
    // Reusable buffer for stereo PCM data to avoid per-callback allocations
    private float[]? _stereoPool;

    /// <summary>
    /// When true, <see cref="ConsumeRawPcm"/> returns PCM data for projectM.
    /// Set from <see cref="AppSettings.ReactiveProjectM"/>.
    /// </summary>
    public static bool ProjectMEnabled { get; set; }

    /// <summary>
    /// Consume the most recent raw PCM samples (stereo interleaved) for projectM.
    /// Returns null if <see cref="ProjectMEnabled"/> is false or no new data is available. Thread-safe.
    /// </summary>
    public static float[]? ConsumeRawPcm()
    {
        if (!ProjectMEnabled) return null;
        lock (_pcmLock)
        {
            if (_rawPcmBuffer == null) return null;
            // Copy valid portion since the buffer is reused by the capture callback
            var result = new float[_rawPcmLength];
            Array.Copy(_rawPcmBuffer, result, _rawPcmLength);
            _rawPcmBuffer = null;
            _rawPcmLength = 0;
            return result;
        }
    }

    /// <summary>Values below this fraction (0.0–1.0) are suppressed to zero. Default 0.1 (10%).</summary>
    public float ReactivityThreshold { get; set; } = 0.10f;

    /// <summary>Animation duration in milliseconds for reactive scale changes. Default 120.</summary>
    public int ReactiveSpeedMs { get; set; } = 120;

    /// <summary>Multiplier for the reactive effect strength (0.5–3.0). Default 1.0.</summary>
    public float Overdrive { get; set; } = 1.0f;

    /// <summary>Raised ~60 times/sec with reactive audio data.</summary>
    public event Action<AudioReactiveData>? Updated;

    public AudioReactiveService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _timer.Tick += OnTick;
    }

    public void Start()
    {
        if (_capture != null) return;
        try
        {
            var enumerator = new MMDeviceEnumerator();
            _audioDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _capture = new WasapiLoopbackCapture();
            _sampleRate = _capture.WaveFormat.SampleRate;
            _capture.DataAvailable += OnDataAvailable;
            _capture.StartRecording();
            _timer.Start();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AudioReactive start failed: {ex.Message}");
        }
    }

    public void Stop()
    {
        _timer.Stop();
        if (_capture != null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            try { _capture.StopRecording(); } catch { }
            _capture.Dispose();
            _capture = null;
        }
        _audioDevice = null;
        _smoothedLevel = 0;
        _currentPeak = 0;
        _smoothedBass = 0;
        _smoothedTreble = 0;
    }

    private void OnDataAvailable(object? sender, NAudio.Wave.WaveInEventArgs e)
    {
        if (e.BytesRecorded < 4) return;

        // Normalize samples by system volume so reactivity is volume-independent
        float volumeNorm = 1.0f;
        try
        {
            if (_audioDevice?.AudioEndpointVolume is { } vol)
            {
                float masterLevel = vol.MasterVolumeLevelScalar;
                if (masterLevel > 0.01f)
                    volumeNorm = 1.0f / masterLevel;
            }
        }
        catch { }

        // Compute RMS and feed FFT buffer (mono mix — average channels)
        int channels = _capture!.WaveFormat.Channels;
        int bytesPerSample = 4; // float32
        int bytesPerFrame = bytesPerSample * channels;
        float sum = 0;
        int frameCount = 0;

        for (int i = 0; i + bytesPerFrame <= e.BytesRecorded; i += bytesPerFrame)
        {
            float mono = 0;
            for (int ch = 0; ch < channels; ch++)
                mono += BitConverter.ToSingle(e.Buffer, i + ch * bytesPerSample);
            mono = mono / channels * volumeNorm;

            sum += mono * mono;
            frameCount++;

            // Fill FFT buffer
            _fftBuffer[_fftPos].X = mono * (float)FastFourierTransform.HammingWindow(_fftPos, FftLength);
            _fftBuffer[_fftPos].Y = 0;
            _fftPos++;

            if (_fftPos >= FftLength)
            {
                _fftPos = 0;
                FastFourierTransform.FFT(true, FftExponent, _fftBuffer);
                AnalyzeFrequencyBands();
            }
        }

        if (frameCount == 0) return;

        // Store raw PCM for projectM (stereo interleaved, unnormalized by volume
        // so projectM gets the actual audio signal for its own beat detection)
        if (frameCount > 0)
        {
            int needed = frameCount * 2;
            if (_stereoPool == null || _stereoPool.Length < needed)
                _stereoPool = new float[needed];
            int idx = 0;
            for (int i = 0; i + bytesPerFrame <= e.BytesRecorded && idx < needed; i += bytesPerFrame)
            {
                float left = BitConverter.ToSingle(e.Buffer, i);
                float right = channels > 1
                    ? BitConverter.ToSingle(e.Buffer, i + bytesPerSample)
                    : left;
                _stereoPool[idx++] = left;
                _stereoPool[idx++] = right;
            }
            lock (_pcmLock)
            {
                _rawPcmBuffer = _stereoPool;
                _rawPcmLength = idx;
            }
        }

        float rms = MathF.Sqrt(sum / frameCount);
        Interlocked.Exchange(ref _currentPeak, Math.Clamp(rms * 3f, 0f, 1f));
    }

    private void AnalyzeFrequencyBands()
    {
        // Frequency resolution: sampleRate / FftLength per bin
        float binHz = _sampleRate / (float)FftLength;
        int usableBins = FftLength / 2;

        // Bass: 20–250 Hz
        int bassStart = Math.Max(1, (int)(20 / binHz));
        int bassEnd = Math.Min(usableBins, (int)(250 / binHz));
        // Treble: 4000–16000 Hz
        int trebleStart = Math.Max(1, (int)(4000 / binHz));
        int trebleEnd = Math.Min(usableBins, (int)(16000 / binHz));

        float bassSum = 0;
        for (int i = bassStart; i < bassEnd; i++)
        {
            float mag = MathF.Sqrt(_fftBuffer[i].X * _fftBuffer[i].X + _fftBuffer[i].Y * _fftBuffer[i].Y);
            bassSum += mag;
        }

        float trebleSum = 0;
        for (int i = trebleStart; i < trebleEnd; i++)
        {
            float mag = MathF.Sqrt(_fftBuffer[i].X * _fftBuffer[i].X + _fftBuffer[i].Y * _fftBuffer[i].Y);
            trebleSum += mag;
        }

        int bassCount = Math.Max(1, bassEnd - bassStart);
        int trebleCount = Math.Max(1, trebleEnd - trebleStart);

        Interlocked.Exchange(ref _currentBass, Math.Clamp(bassSum / bassCount * 10f, 0f, 1f));
        Interlocked.Exchange(ref _currentTreble, Math.Clamp(trebleSum / trebleCount * 20f, 0f, 1f));
    }

    private void OnTick(object? sender, EventArgs e)
    {
        float peak = _currentPeak;
        float bass = _currentBass;
        float treble = _currentTreble;

        _prevSmoothedLevel = _smoothedLevel;
        _smoothedLevel = _smoothedLevel * 0.7f + peak * 0.3f;
        _smoothedBass = _smoothedBass * 0.6f + bass * 0.4f;
        _smoothedTreble = _smoothedTreble * 0.6f + treble * 0.4f;

        float delta = _smoothedLevel - _prevSmoothedLevel;
        _beatDetected = delta > BeatThreshold;

        float outBass = _smoothedBass < ReactivityThreshold ? 0f : _smoothedBass;
        float outTreble = _smoothedTreble < ReactivityThreshold ? 0f : _smoothedTreble;

        Updated?.Invoke(new AudioReactiveData
        {
            Level = Math.Clamp(_smoothedLevel * Overdrive, 0f, 1f),
            IsBeat = _beatDetected,
            Bass = Math.Clamp(outBass * Overdrive, 0f, 1f),
            Treble = Math.Clamp(outTreble * Overdrive, 0f, 1f),
        });
    }

    public void Dispose()
    {
        Stop();
    }
}
