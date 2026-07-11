using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using LibVLC = LibVLCSharp.Shared.LibVLC;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using VlcMedia = LibVLCSharp.Shared.Media;

namespace Phosphor;

public partial class PlayfieldWindow : JukeboxWindow
{
    private readonly DispatcherTimer _colorTimer;
    private readonly Random _rng = new();
    private double _hueOffset;
    private double _blobIntensity = 0.5;
    private double _blobSpeedMultiplier = 1.0;
    private BlobPattern _blobPattern = BlobPattern.Random;
    private BlobPattern _blobPatternSetting = BlobPattern.Random;
    private bool _transitioning;
    private IBlobPattern? _currentPattern;
    private AudioReactiveService? _audioReactive;
    private double _reactiveHueBoost;
    private int _blobCount = 10;
    private int _blobSizeOffset;
    private bool _blobsInitialized;
    private bool _screensaverActive;
    private double[] _blobHueOffsets = [];
    private ColorAnalysis? _lastColorAnalysis;
    private bool _pulseDominantBlobs;
    private DateTime _lastPulseTime;
    private DateTime _patternStartTime;
    private DispatcherTimer? _oledDefeatTimer;
    private DispatcherTimer? _oledDefeatRevertTimer;
    private int _oledDefeatIntervalSeconds;
    private double _savedIntensity;
    private double _oledDefeatIntensity = 0.8;
    private double _brightnessBoost = 1.1;
    private string? _videoPath;

    // ── Playfield video (LibVLC) ──
    // Owns a dedicated LibVLC instance (NOT the app's shared one) because the
    // video is rotated via instance-level transform args (--video-filter), which
    // must be baked into the instance at creation. The VideoView is attached only
    // while in Video mode so its WinForms-hosted HWND doesn't force software
    // rendering for the GPU-accelerated blob screensaver.
    private LibVLC? _libVLC;
    private VlcMediaPlayer? _mediaPlayer;
    private LibVLCSharp.WPF.VideoView? _videoView;
    private Border? _videoFadeOverlay;
    private Task? _vlcInitTask;
    private bool _videoMode;
    private string? _playingVideoPath;
    private int _videoRotation;
    // Raw orientation from settings (applied to the WPF LayoutTransform for the
    // screensaver/image). Video rotation is derived from this only when
    // _applyOrientationToVideos is true.
    private int _rotationDegrees;
    private bool _applyOrientationToVideos = true;
    // Playfield video audio: muted by default (silent ambient loop). When enabled, the
    // player is unmuted and set to _videoVolume. Stored so it's reapplied when the VLC
    // instance is rebuilt (e.g. on rotation change).
    private bool _videoAudioEnabled;
    private int _videoVolume = 50;
    // Folder-mode crossfade: a position timer starts the fade-to-black slightly
    // BEFORE a clip ends (while it's still rendering) so the dip is actually
    // visible; _videoTransitioning guards against the timer, EndReached, and the
    // Vout teardown event all firing during the same swap.
    private DispatcherTimer? _videoPositionTimer;
    private bool _videoTransitioning;
    private const int VideoTransitionFadeMs = 400;
    // Video Folders mode: a random file from a random folder plays, advancing to a
    // new random file when each clip ends (EndReached). Empty => single-file mode.
    private string[] _videoFolders = [];
    private bool _folderMode;
    private static readonly string[] _videoExtensions =
        [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".webm"];
    // Folder-mode playback options (set via SetVideoFolderOptions).
    private VideoFolderPlayMode _folderPlayMode = VideoFolderPlayMode.Random;
    private int _folderMinDurationSec = 15;
    private int _folderMaxDurationSec;              // 0 = no maximum
    // Most-Recent-First ordering: the full file list is enumerated + sorted ONCE
    // and cached (heavy folders may hold thousands of files); rebuilt only when
    // folders or play mode change. _mostRecentIndex walks it, wrapping at the end.
    private string[]? _mostRecentFiles;
    private int _mostRecentIndex;
    // Wall-clock start of the current clip, used to measure elapsed on-screen time
    // (robust under seamless input-repeat looping where mp.Time resets each loop).
    private DateTime _clipStartUtc;

    // Pinup Playlist mode: like folder mode, but the file list is pre-resolved from the
    // Pinup Popper database (each game's PlayfieldVideoFilename glob resolved to a real
    // video file) and randomized once, then walked in order. Reuses the folder-mode
    // crossfade/position-timer playback pipeline.
    private bool _pinupMode;
    private string[] _pinupFiles = [];
    private int _pinupIndex;
    private int _pinupMinDurationSec = 15;
    private int _pinupMaxDurationSec;               // 0 = no maximum

    //added to try to prevent window from stealing focus
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const nint MA_NOACTIVATE = 3;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_CHAR = 0x0102;

    /// <summary>
    /// Set this to the DMD window's HWND to forward all keystrokes there.
    /// </summary>
    public IntPtr DmdWindowHandle { get; set; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, nint wParam, nint lParam);

    /// <summary>
    /// Raised when the predominant blob color band changes (based on blob 0's hue).
    /// </summary>
    public event Action<ColorAnalysis>? BlobColorBandChanged;

    public PlayfieldWindow()
    {
        InitializeComponent();

        _colorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _colorTimer.Tick += AnimateBlobs;

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;

        //added to try to prevent window from stealing focus
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;

            // Add WS_EX_NOACTIVATE so this window never steals focus
            var exStyle = GetWindowLong(handle, -20);
            SetWindowLong(handle, -20, exStyle | WS_EX_NOACTIVATE);

            // Also intercept WM_MOUSEACTIVATE to prevent activation on click
            var source = HwndSource.FromHwnd(handle);
            source?.AddHook(PlayfieldWndProc);
        };
    }

    private nint PlayfieldWndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return MA_NOACTIVATE;
        }

        // Forward keyboard messages to the DMD window
        if (DmdWindowHandle != IntPtr.Zero &&
            (msg == WM_KEYDOWN || msg == WM_KEYUP || msg == WM_SYSKEYDOWN || msg == WM_SYSKEYUP || msg == WM_CHAR))
        {
            PostMessage(DmdWindowHandle, msg, wParam, lParam);
            handled = true;
            return nint.Zero;
        }

        return nint.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Only spin up the screensaver here if it's actually the active mode. If the app
        // started in a video/image/blank mode, SetMode has already run and the canvas is
        // collapsed — creating blobs (and starting their render loop) here would leave a
        // hidden screensaver burning CPU/GPU behind the video.
        if (!_screensaverActive)
            return;

        if (!_blobsInitialized && ScreensaverCanvas.ActualWidth > 0)
        {
            CreateBlobs();
        }

        SyncColorTimer();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_blobsInitialized || ScreensaverCanvas.ActualWidth <= 0)
            return;

        // Self-rendering patterns (ProjectM, Mandelbrot) handle their own
        // resize via the canvas SizeChanged event — no need to recreate.
        if (IsSelfRenderingPattern)
            return;

        CreateBlobs();
    }

    private BlobPatternConfig MakeConfig()
    {
        double w = Math.Max(200, ScreensaverCanvas.ActualWidth);
        double h = Math.Max(200, ScreensaverCanvas.ActualHeight);
        double sizeRef = w + h;
        double baseRef = 3000.0;
        double dampen = Math.Sqrt(baseRef / Math.Max(baseRef, sizeRef));
        double sr = sizeRef * dampen;

        return new BlobPatternConfig
        {
            Canvas = ScreensaverCanvas,
            BlobCount = _blobCount,
            Intensity = _blobIntensity,
            SpeedMultiplier = _blobSpeedMultiplier,
            Rng = _rng,
            BlobSizeFactory = r => sr * (0.08 + r.NextDouble() * 0.08),
            BlobSizeOffset = _blobSizeOffset,
            UseBitmapCache = false,
        };
    }

    private void CreateBlobs()
    {
        _blobHueOffsets = [];
        _patternStartTime = DateTime.UtcNow;

        _currentPattern?.Dispose();
        _currentPattern = BlobTransition.Create(_blobPattern, MakeConfig());
        _currentPattern.Enter(() => SubscribeProjectMColorBand());

        _blobsInitialized = true;
    }

    /// <summary>
    /// Returns true when the active pattern is self-rendering (ProjectM, Mandelbrot)
    /// and does not need the color animation timer.
    /// </summary>
    private bool IsSelfRenderingPattern =>
        _blobPattern is BlobPattern.ProjectM or BlobPattern.Mandelbrot;

    /// <summary>
    /// Starts or stops the color animation timer based on whether the current
    /// pattern is blob-based (needs AnimateBlobs) or self-rendering.
    /// </summary>
    private void SyncColorTimer()
    {
        if (IsSelfRenderingPattern)
            _colorTimer.Stop();
        else
            _colorTimer.Start();
    }

    private void AnimateBlobs(object? sender, EventArgs e)
    {
        var brushes = _currentPattern?.Brushes;
        var gradBrushes = _currentPattern?.GradientBrushes;
        if (brushes == null || brushes.Count == 0) return;

        bool patternOwnsColors = _currentPattern!.ManagesOwnColors;

        if (!patternOwnsColors)
            _hueOffset += 1.0;

        // Ensure per-blob random hue offsets exist
        if (!patternOwnsColors && _blobHueOffsets.Length != brushes.Count)
            _blobHueOffsets = Enumerable.Range(0, brushes.Count).Select(_ => _rng.NextDouble() * 360.0).ToArray();

        Span<int> bandCounts = stackalloc int[8];
        float totalBrightness = 0f;
        for (int i = 0; i < brushes.Count; i++)
        {
            if (patternOwnsColors)
            {
                // Pattern sets its own brush colors — just read them for band detection
                var bc = brushes[i].Color;
                double bHue = ColorToHue(bc.R, bc.G, bc.B);
                double bSat = ColorToSaturation(bc.R, bc.G, bc.B);
                double bLit = bc.R / 255.0 * 0.299 + bc.G / 255.0 * 0.587 + bc.B / 255.0 * 0.114;
                var analysis = RoygbivHelper.Analyze(bHue, bSat, bLit);
                bandCounts[(int)analysis.Color]++;
                totalBrightness += analysis.Brightness;
            }
            else
            {
                double hue = (_hueOffset + _reactiveHueBoost + _blobHueOffsets[i]) % 360.0;
                double value = Math.Clamp((0.15 + _blobIntensity * 0.85) * _brightnessBoost, 0.0, 1.0);
                var color = ColorHelper.HsvToColor(hue, 0.9, value);
                brushes[i].Color = color;
                if (gradBrushes != null && i < gradBrushes.Count)
                {
                    var stops = gradBrushes[i].GradientStops;
                    if (stops.Count >= 2)
                    {
                        stops[0].Color = Color.FromArgb(255, color.R, color.G, color.B);
                        stops[1].Color = Color.FromArgb(120, color.R, color.G, color.B);
                    }
                }
                var analysis = RoygbivHelper.Analyze(hue, 0.9, value);
                bandCounts[(int)analysis.Color]++;
                totalBrightness += analysis.Brightness;
            }
        }

        // Detect dominant color band (mode) across all blobs and notify on change
        if (brushes.Count > 0)
        {
            int maxCount = 0;
            var modeBand = RoygbivColor.Red;
            for (int b = 0; b < 8; b++)
            {
                if (bandCounts[b] > maxCount)
                {
                    maxCount = bandCounts[b];
                    modeBand = (RoygbivColor)b;
                }
            }
            var modeAnalysis = new ColorAnalysis(modeBand, totalBrightness / brushes.Count);
            if (_lastColorAnalysis?.Color != modeAnalysis.Color)
            {
                // Suppress DOF and pulse effects during the first few seconds
                // after a pattern enters to avoid jank during fly-in animation.
                var now = DateTime.UtcNow;
                if ((now - _patternStartTime).TotalSeconds < 3)
                    return;

                _lastColorAnalysis = modeAnalysis;

                BlobColorBandChanged?.Invoke(modeAnalysis);

                // Pattern-specific pulse (e.g. Matrix trail flash) — only when
                // the "Pulse dominant blobs" setting is enabled.
                if (_pulseDominantBlobs)
                    _currentPattern?.PulseDominantColor(modeBand);

                if (_pulseDominantBlobs && (now - _lastPulseTime).TotalMilliseconds > 6000)
                {
                    _lastPulseTime = now;
                    PulseDominantBlobs(modeBand);
                }
            }
        }
    }

    /// <summary>
    /// Briefly pulses blobs whose current hue falls in the given ROYGBIV color band
    /// with a heartbeat-style double-beat animation, mirroring DOF cabinet effects.
    /// Uses opacity only when audio reactive is active (to avoid fighting ScaleTransform);
    /// uses both opacity and scale when audio reactive is off.
    /// </summary>
    private void PulseDominantBlobs(RoygbivColor dominantBand)
    {
        // ── Tuning constants ──────────────────────────────────────────
        const double opacityBoost1  = 0.32;   // first beat opacity bump
        const double opacityBoost2  = 0.20;   // second beat opacity bump
        const double scalePeak1     = 1.14;   // first beat scale factor
        const double scalePeak2     = 1.10;   // second beat scale factor
        const int    beat1Ms        = 150;    // time to first peak
        const int    dip1Ms         = 400;    // time to dip between beats
        const int    beat2Ms        = 650;    // time to second peak
        const int    settleMs       = 1200;   // time to ease back to rest
        // ──────────────────────────────────────────────────────────────

        if (_currentPattern == null || IsSelfRenderingPattern) return;

        var blobs = _currentPattern.Blobs;
        if (blobs.Count == 0 || _blobHueOffsets.Length == 0) return;

        bool includeScale = _audioReactive == null;

        for (int i = 0; i < blobs.Count && i < _blobHueOffsets.Length; i++)
        {
            double hue = (_hueOffset + _reactiveHueBoost + _blobHueOffsets[i]) % 360.0;
            var band = RoygbivHelper.FromHue(hue);
            if (band != dominantBand) continue;

            var blob = blobs[i];
            double baseOpacity = blob.Opacity;

            // Heartbeat: strong beat → dip → softer beat → ease back
            var opacityFrames = new DoubleAnimationUsingKeyFrames();
            double peak1 = Math.Min(baseOpacity + opacityBoost1, 1.0);
            double peak2 = Math.Min(baseOpacity + opacityBoost2, 1.0);
            opacityFrames.KeyFrames.Add(new EasingDoubleKeyFrame(peak1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(beat1Ms)),
                new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            opacityFrames.KeyFrames.Add(new EasingDoubleKeyFrame(baseOpacity, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(dip1Ms)),
                new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            opacityFrames.KeyFrames.Add(new EasingDoubleKeyFrame(peak2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(beat2Ms)),
                new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            opacityFrames.KeyFrames.Add(new EasingDoubleKeyFrame(baseOpacity, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(settleMs)),
                new QuadraticEase { EasingMode = EasingMode.EaseInOut }));

            var b = blob;
            var bBase = baseOpacity;
            opacityFrames.Completed += (_, _) =>
            {
                b.BeginAnimation(UIElement.OpacityProperty, null);
                b.Opacity = bBase;
            };
            blob.BeginAnimation(UIElement.OpacityProperty, opacityFrames);

            if (includeScale)
            {
                if (blob.RenderTransform is not ScaleTransform st)
                {
                    st = new ScaleTransform(1.0, 1.0);
                    blob.RenderTransform = st;
                }

                // Matching heartbeat on scale
                var scaleFrames = new DoubleAnimationUsingKeyFrames();
                scaleFrames.KeyFrames.Add(new EasingDoubleKeyFrame(scalePeak1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(beat1Ms)),
                    new QuadraticEase { EasingMode = EasingMode.EaseOut }));
                scaleFrames.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(dip1Ms)),
                    new QuadraticEase { EasingMode = EasingMode.EaseIn }));
                scaleFrames.KeyFrames.Add(new EasingDoubleKeyFrame(scalePeak2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(beat2Ms)),
                    new QuadraticEase { EasingMode = EasingMode.EaseOut }));
                scaleFrames.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(settleMs)),
                    new QuadraticEase { EasingMode = EasingMode.EaseInOut }));

                var stRef = st;
                scaleFrames.Completed += (_, _) =>
                {
                    stRef.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    stRef.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    stRef.ScaleX = 1.0;
                    stRef.ScaleY = 1.0;
                };
                st.BeginAnimation(ScaleTransform.ScaleXProperty, scaleFrames);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, scaleFrames.Clone());
            }
        }
    }

    private static double ColorToHue(byte r, byte g, byte b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double delta = max - min;
        if (delta < 0.001) return 0;
        double hue;
        if (max == rd) hue = 60.0 * (((gd - bd) / delta) % 6.0);
        else if (max == gd) hue = 60.0 * (((bd - rd) / delta) + 2.0);
        else hue = 60.0 * (((rd - gd) / delta) + 4.0);
        return ((hue % 360.0) + 360.0) % 360.0;
    }

    private static double ColorToSaturation(byte r, byte g, byte b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        if (max < 0.001) return 0;
        return (max - min) / max;
    }

    public void SetScreensaverSettings(double intensity, double speed)
    {
        double newIntensity = Math.Clamp(intensity, 0.05, 1.0);
        bool intensityChanged = Math.Abs(newIntensity - _blobIntensity) > 0.001;
        _blobIntensity = newIntensity;
        _blobSpeedMultiplier = Math.Clamp(speed, 0.1, 5.0);

        if (intensityChanged && _currentPattern != null)
        {
            foreach (var blob in _currentPattern.Blobs)
                blob.Opacity = Math.Min(1.0, _blobIntensity + _rng.NextDouble() * 0.1);
        }
    }

    /// <summary>
    /// Sets a brightness multiplier for the playfield screensaver.
    /// 1.0 = default, 1.1 = 10% brighter, etc.
    /// </summary>
    public void SetBrightnessBoost(double boost)
    {
        _brightnessBoost = Math.Clamp(boost, 0.5, 2.0);
    }

    public void SetPulseDominantBlobs(bool enabled)
    {
        _pulseDominantBlobs = enabled;
    }

    public void SetReactiveAudio(AudioReactiveService? service)
    {
        if (_audioReactive != null)
            _audioReactive.Updated -= OnAudioUpdated;

        _audioReactive = service;

        if (_audioReactive != null)
            _audioReactive.Updated += OnAudioUpdated;
        else
            _currentPattern?.ResetAudioReactive(_blobIntensity);
    }

    private void OnAudioUpdated(AudioReactiveData data)
    {
        if (_currentPattern == null) return;

        // The Updated event fires on the main UI thread's dispatcher, but our
        // blobs live on the playfield's own thread — marshal to our dispatcher.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnAudioUpdated(data));
            return;
        }

        _currentPattern.ApplyAudioReactive(data, _blobIntensity, _audioReactive?.ReactiveSpeedMs ?? 120);
        _reactiveHueBoost = data.Treble * 90.0;
    }

    public void SetBlobCount(int count)
    {
        int clamped = Math.Clamp(count, 0, 100);
        bool changed = clamped != _blobCount;
        _blobCount = clamped;

        // During initial setup, just store the value — SetBlobPattern will
        // create the pattern with the correct count via MakeConfig().
        if (!_blobsInitialized || !changed)
            return;

        // Defer creation until the canvas has a real size
        if (ScreensaverCanvas.ActualWidth <= 0 || ScreensaverCanvas.ActualHeight <= 0)
            return;

        _patternStartTime = DateTime.UtcNow;
        _currentPattern?.Dispose();
        _currentPattern = BlobTransition.Create(_blobPattern, MakeConfig());
        _currentPattern.Enter(() => SubscribeProjectMColorBand());
        SyncColorTimer();
    }

    public void SetBlobSizeOffset(int offset)
    {
        int clamped = Math.Clamp(offset, 1, 20);
        bool changed = clamped != _blobSizeOffset;
        _blobSizeOffset = clamped;

        if (!_blobsInitialized || !changed)
            return;

        if (ScreensaverCanvas.ActualWidth <= 0 || ScreensaverCanvas.ActualHeight <= 0)
            return;

        _patternStartTime = DateTime.UtcNow;
        _currentPattern?.Dispose();
        _currentPattern = BlobTransition.Create(_blobPattern, MakeConfig());
        _currentPattern.Enter(() => SubscribeProjectMColorBand());
        SyncColorTimer();
    }

    public void SetBlobPattern(BlobPattern pattern)
    {
        _transitioning = false;
        _blobPatternSetting = pattern;

        if (pattern == BlobPattern.RandomPerSong)
            pattern = BlobTransition.CurrentRandomPattern;

        _blobPattern = pattern;

        // Don't build/run a pattern while the screensaver isn't the active mode, otherwise
        // a settings-apply during video mode would resurrect a hidden pattern's render loop.
        if (!_screensaverActive)
            return;

        // If the canvas isn't laid out yet
        // OnLoaded/CreateBlobs will create the blobs once Loaded fires.
        if (ScreensaverCanvas.ActualWidth < 1 || ScreensaverCanvas.ActualHeight < 1)
            return;

        _currentPattern?.Dispose();
        _patternStartTime = DateTime.UtcNow;
        _currentPattern = BlobTransition.Create(pattern, MakeConfig());
        _currentPattern.Enter(() => SubscribeProjectMColorBand());
        _blobsInitialized = true;
        SyncColorTimer();
    }

    /// <summary>
    /// Returns the active ProjectMRenderer if the current pattern is ProjectM, otherwise null.
    /// </summary>
    public ProjectMRenderer? GetProjectMRenderer()
    {
        return (_currentPattern as ProjectMPattern)?.Renderer;
    }

    /// <summary>
    /// If the current pattern is ProjectM, subscribes to its color band event
    /// so DOF lighting can be triggered from the playfield visualization only.
    /// </summary>
    private void SubscribeProjectMColorBand()
    {
        if (_currentPattern is ProjectMPattern pmPattern && pmPattern.Renderer != null)
        {
            pmPattern.Renderer.ColorBandChanged += analysis =>
            {
                var tagged = new ColorAnalysis(analysis.Color, analysis.Brightness, analysis.TopAvgLuminance, SelfRendering: true);
                _lastColorAnalysis = tagged;
                BlobColorBandChanged?.Invoke(tagged);
            };
        }
    }

    /// <summary>
    /// Restarts the current pattern if it is Mandelbrot, so that changed static settings take effect.
    /// </summary>
    public void RestartMandelbrot()
    {
        if (_blobPattern == BlobPattern.Mandelbrot)
            SetBlobPattern(_blobPatternSetting);
    }

    /// <summary>
    /// Restarts the current pattern if it is ProjectM, so that changed static settings take effect.
    /// </summary>
    public void RestartProjectM()
    {
        if (_blobPattern == BlobPattern.ProjectM)
            SetBlobPattern(_blobPatternSetting);
    }

    /// <summary>
    /// Restarts the current pattern if it is Game of Life, so that changed static settings take effect.
    /// </summary>
    public void RestartGameOfLife()
    {
        if (_blobPattern == BlobPattern.GameOfLife)
            SetBlobPattern(_blobPatternSetting);
    }

    /// <summary>
    /// Soft-resets the Game of Life simulation in place with a blur-out / blur-in
    /// transition. Used for track-change resets, where the visible cell size and
    /// bitmap dimensions haven't changed and we just want a fresh seed under a
    /// smooth crossfade instead of tearing down and rebuilding the pattern.
    /// </summary>
    public void RestartGameOfLifeWithBlurTransition()
    {
        if (_blobPattern == BlobPattern.GameOfLife && _currentPattern is GameOfLifePattern gol)
            gol.RestartWithBlurTransition();
    }

    /// <summary>
    /// Restarts the current pattern if it is Gravity, so that a fresh simulation begins.
    /// </summary>
    public void RestartGravity()
    {
        if (_blobPattern == BlobPattern.Gravity)
            SetBlobPattern(_blobPatternSetting);
    }

    /// <summary>
    /// Restarts the current pattern if it is Clock, so that changed tuning takes effect.
    /// </summary>
    public void RestartClock()
    {
        if (_blobPattern == BlobPattern.Clock)
            SetBlobPattern(_blobPatternSetting);
    }

    public void ApplyProjectMTuning()
    {
        if (_blobPattern == BlobPattern.ProjectM && _currentPattern is ProjectMPattern pm)
            pm.ApplyTuningSettings();
    }

    /// <summary>
    /// If the pattern is RandomPerSong, smoothly transition to a new random pattern.
    /// Called when a new song starts playing.
    /// </summary>
    public void OnSongChanged()
    {
        if (_blobPatternSetting != BlobPattern.RandomPerSong || _transitioning || _currentPattern == null)
            return;

        _transitioning = true;

        _currentPattern.Exit(() =>
        {
            var newPattern = BlobTransition.CurrentRandomPattern;
            DebugLog.Log("Playfield", $"Transition {_blobPattern} -> {newPattern} blob pattern");
            _blobPattern = newPattern;

            _currentPattern?.Dispose();
            _patternStartTime = DateTime.UtcNow;
            _currentPattern = BlobTransition.Create(newPattern, MakeConfig());
            SyncColorTimer();
            _currentPattern.Enter(() =>
            {
                SubscribeProjectMColorBand();
                _transitioning = false;
            });
        });
    }

    public void SetStaticImage(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            StaticImage.Source = bitmap;
        }
        else
        {
            StaticImage.Source = null;
        }
    }

    /// <summary>
    /// Returns the MediaPlayer, lazily initializing LibVLC on first use. Called
    /// on the playfield dispatcher thread; pumps messages while waiting for the
    /// engine so the window stays responsive.
    /// </summary>
    private VlcMediaPlayer? EnsureVlcInitialized()
    {
        if (_mediaPlayer != null)
            return _mediaPlayer;

        if (_vlcInitTask == null)
            _vlcInitTask = Task.Run(InitializeVlcCore);

        if (!_vlcInitTask.IsCompleted)
        {
            // Pump dispatcher messages so the window doesn't freeze while LibVLC
            // finishes its background plugin scan.
            var frame = new DispatcherFrame();
            _vlcInitTask.ContinueWith(_ => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }

        if (_mediaPlayer == null)
            InitializeVlcCore();

        return _mediaPlayer;
    }

    /// <summary>
    /// Builds the LibVLC instance arguments, baking in the video rotation as an
    /// instance-level transform filter. In LibVLC 3.x the transform is a video
    /// output filter configured when the vout is created from these instance args
    /// (per-media :transform-type options are not honored), so rotation must live
    /// here and the instance must be rebuilt when rotation changes.
    /// </summary>
    private string[] BuildVlcArgs()
    {
        var args = new List<string> { "--no-video-title-show" };
        if (_videoRotation is 90 or 180 or 270)
        {
            args.Add("--video-filter=transform");
            // transform-type degrees are clockwise, matching WPF RotateTransform.
            args.Add($"--transform-type={_videoRotation}");
        }
        return args.ToArray();
    }

    /// <summary>
    /// Core LibVLC + MediaPlayer creation. Creates a dedicated LibVLC instance
    /// (with rotation baked into instance args) rather than reusing the app's
    /// shared one. The player is muted — the playfield video is a silent ambient
    /// loop that must not compete with the backglass music.
    /// </summary>
    private void InitializeVlcCore()
    {
        if (_mediaPlayer != null)
            return;

        var vlc = new LibVLC(BuildVlcArgs());
        var mp = new VlcMediaPlayer(vlc) { Mute = true };
        mp.Vout += OnVideoVout;
        mp.EndReached += OnVideoEndReached;

        _libVLC = vlc;
        _mediaPlayer = mp;
        ApplyAudioToPlayer();
    }

    /// <summary>
    /// Sets whether playfield video audio plays and at what volume (0–100). Applies to all
    /// video modes. Stored so the state survives a VLC instance rebuild (rotation change).
    /// </summary>
    public void SetVideoAudio(bool enabled, int volume)
    {
        _videoAudioEnabled = enabled;
        _videoVolume = Math.Clamp(volume, 0, 100);
        ApplyAudioToPlayer();
    }

    /// <summary>Applies the current audio enable/volume state to the live media player.</summary>
    private void ApplyAudioToPlayer()
    {
        var mp = _mediaPlayer;
        if (mp == null)
            return;
        try
        {
            mp.Mute = !_videoAudioEnabled;
            mp.Volume = _videoAudioEnabled ? _videoVolume : 0;
        }
        catch { /* volume can be rejected before a vout exists; reapplied on next play */ }
    }

    /// <summary>
    /// Tears down the current MediaPlayer + LibVLC instance so a fresh one can be
    /// built with new instance args (used when rotation changes). Runs the VLC
    /// disposal on a background thread to avoid blocking the dispatcher; detaches
    /// the VideoView first so it doesn't hold the old player.
    /// </summary>
    private void DisposeVlc()
    {
        StopVideoPositionTimer();
        _videoTransitioning = false;
        DetachVideoView();

        var mp = _mediaPlayer;
        var vlc = _libVLC;
        _mediaPlayer = null;
        _libVLC = null;
        _vlcInitTask = null;
        _playingVideoPath = null;

        if (mp != null)
        {
            mp.Vout -= OnVideoVout;
            mp.EndReached -= OnVideoEndReached;
        }

        if (mp != null || vlc != null)
        {
            Task.Run(() =>
            {
                try { mp?.Stop(); } catch { }
                try { mp?.Dispose(); } catch { }
                try { vlc?.Dispose(); } catch { }
            });
        }
    }

    public void SetVideoPath(string? path)
    {
        // Store the desired path only. SetMode(Video) performs the actual
        // attach/play so the common "SetVideoPath then SetMode" call sequence
        // doesn't start playback twice.
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            _videoPath = path;
            // If we're already in single-file video mode, apply immediately.
            if (_videoMode && !_folderMode)
                StartVideoPlayback();
        }
        else
        {
            _videoPath = null;
            if (_videoMode && !_folderMode)
                StopVideoPlayback();
        }
    }

    /// <summary>
    /// Sets the folders scanned in <see cref="PlayfieldMode.VideoFolders"/> mode.
    /// Paths may be relative (resolved against the app base directory) or absolute.
    /// </summary>
    public void SetVideoFolders(IReadOnlyList<string>? folders)
    {
        _videoFolders = (folders ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(ResolveFolder)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Folder set changed — invalidate the cached Most-Recent-First ordering.
        _mostRecentFiles = null;
        _mostRecentIndex = 0;

        // If we're already in folder mode and nothing is playing yet, kick it off.
        if (_videoMode && _folderMode && _mediaPlayer == null)
            StartVideoPlayback();
    }

    /// <summary>
    /// Sets folder-mode playback options: file ordering, the minimum on-screen
    /// duration (a clip loops until this elapses), and the maximum runtime cap
    /// (0 = no maximum). Changing the play mode invalidates the cached ordering.
    /// </summary>
    public void SetVideoFolderOptions(VideoFolderPlayMode playMode, int minDurationSec, int maxDurationSec)
    {
        if (_folderPlayMode != playMode)
        {
            _folderPlayMode = playMode;
            _mostRecentFiles = null; // ordering changed — rebuild lazily
            _mostRecentIndex = 0;
        }
        _folderMinDurationSec = Math.Max(0, minDurationSec);
        // 0 means no maximum; otherwise never below the minimum.
        _folderMaxDurationSec = maxDurationSec <= 0 ? 0 : Math.Max(maxDurationSec, _folderMinDurationSec);
    }

    /// <summary>
    /// Sets the resolved video files for <see cref="PlayfieldMode.PinupPlaylist"/> mode.
    /// Each entry is a glob (e.g. <c>...\Playfield\Game.*</c>) from the Pinup Popper database;
    /// it is resolved to an actual video file (mp4/mkv/etc.). Misses are skipped and logged.
    /// The resulting list is shuffled once so playback order is random.
    /// </summary>
    public void SetPinupFiles(IReadOnlyList<string>? globs)
    {
        var resolved = new List<string>();
        foreach (var glob in globs ?? [])
        {
            if (string.IsNullOrWhiteSpace(glob))
                continue;

            var file = ResolvePinupGlob(glob);
            if (file == null)
            {
                DebugLog.Log("Pinup", $"No video file found for: {glob}");
                continue;
            }
            resolved.Add(file);
        }

        // Fisher–Yates shuffle for a random playback order.
        for (int i = resolved.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (resolved[i], resolved[j]) = (resolved[j], resolved[i]);
        }

        _pinupFiles = resolved.ToArray();
        _pinupIndex = 0;
        DebugLog.Log("Pinup", $"Resolved {_pinupFiles.Length} playable file(s) from {(globs?.Count ?? 0)} game(s).");

        // If we're already in pinup mode and nothing is playing yet, kick it off.
        if (_videoMode && _pinupMode && _mediaPlayer == null)
            StartVideoPlayback();
    }

    /// <summary>
    /// Sets Pinup Playlist playback durations: the minimum on-screen time (a clip loops
    /// until this elapses) and the maximum runtime cap (0 = no maximum).
    /// </summary>
    public void SetPinupOptions(int minDurationSec, int maxDurationSec)
    {
        _pinupMinDurationSec = Math.Max(0, minDurationSec);
        _pinupMaxDurationSec = maxDurationSec <= 0 ? 0 : Math.Max(maxDurationSec, _pinupMinDurationSec);
    }

    /// <summary>
    /// Resolves a Pinup <c>PlayfieldVideoFilename</c> glob (a path ending in a filename with a
    /// <c>.*</c> extension wildcard) to the first existing video file matching a supported
    /// extension. Returns null if the directory or a playable file is missing.
    /// </summary>
    private static string? ResolvePinupGlob(string glob)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(glob);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return null;

            var pattern = System.IO.Path.GetFileName(glob); // e.g. "Game.*"
            foreach (var f in Directory.EnumerateFiles(dir, pattern))
            {
                if (_videoExtensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    return f;
            }
        }
        catch { /* unreadable directory — treat as a miss */ }
        return null;
    }

    /// <summary>
    /// Returns the next Pinup file, walking the shuffled list in order and wrapping at the
    /// end (skipping any files that have since disappeared). Returns null if none remain.
    /// </summary>
    private string? PickNextPinupFile()
    {
        if (_pinupFiles.Length == 0)
            return null;

        if (_pinupIndex >= _pinupFiles.Length)
            _pinupIndex = 0;

        for (int scanned = 0; scanned < _pinupFiles.Length; scanned++)
        {
            var candidate = _pinupFiles[_pinupIndex];
            _pinupIndex++;
            if (_pinupIndex >= _pinupFiles.Length)
                _pinupIndex = 0;

            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Builds (once) the Most-Recent-First ordering: every video file across all
    /// folders, sorted by last-write time descending. Cached until folders or the
    /// play mode change, to avoid re-enumerating potentially thousands of files.
    /// </summary>
    private string[] GetMostRecentFiles()
    {
        if (_mostRecentFiles != null)
            return _mostRecentFiles;

        var all = new List<(string Path, DateTime Modified)>();
        foreach (var folder in _videoFolders)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(folder))
                {
                    if (_videoExtensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    {
                        DateTime modified;
                        try { modified = File.GetLastWriteTimeUtc(f); }
                        catch { modified = DateTime.MinValue; }
                        all.Add((f, modified));
                    }
                }
            }
            catch { /* skip unreadable folder */ }
        }

        _mostRecentFiles = all
            .OrderByDescending(t => t.Modified)
            .Select(t => t.Path)
            .ToArray();
        _mostRecentIndex = 0;
        return _mostRecentFiles;
    }

    private static string ResolveFolder(string path) =>
        System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(AppContext.BaseDirectory, path);

    /// <summary>
    /// Picks the next video file according to the current play mode. In Random
    /// mode: a random folder (weighted per-folder) then a random file, avoiding
    /// an immediate repeat when possible. In Most-Recent-First mode: the next
    /// entry of the cached last-modified-descending list, wrapping at the end.
    /// Returns null if no playable file is found.
    /// </summary>
    private string? PickNextVideoFile(string? avoid)
    {
        if (_folderPlayMode == VideoFolderPlayMode.MostRecentFirst)
            return PickMostRecentFile();
        return PickRandomVideoFile(avoid);
    }

    private string? PickMostRecentFile()
    {
        var files = GetMostRecentFiles();
        if (files.Length == 0)
            return null;

        if (_mostRecentIndex >= files.Length)
            _mostRecentIndex = 0;

        // Walk forward, skipping any files that have since disappeared. Bounded by
        // list length so a fully-deleted list can't loop forever.
        for (int scanned = 0; scanned < files.Length; scanned++)
        {
            var candidate = files[_mostRecentIndex];
            _mostRecentIndex++;
            if (_mostRecentIndex >= files.Length)
                _mostRecentIndex = 0;

            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Picks a random video file: a random folder (weighted per-folder) then a
    /// random file within it. Avoids immediately repeating <paramref name="avoid"/>
    /// when the chosen folder has more than one candidate. Returns null if no
    /// playable file is found.
    /// </summary>
    private string? PickRandomVideoFile(string? avoid)
    {
        if (_videoFolders.Length == 0)
            return null;

        // Try a few folders in case some are empty or unreadable.
        for (int attempt = 0; attempt < _videoFolders.Length; attempt++)
        {
            var folder = _videoFolders[_rng.Next(_videoFolders.Length)];
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(folder)
                    .Where(f => _videoExtensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .ToArray();
            }
            catch
            {
                continue;
            }

            if (files.Length == 0)
                continue;

            if (files.Length == 1)
                return files[0];

            // Avoid immediate repeat when possible.
            for (int pick = 0; pick < 6; pick++)
            {
                var candidate = files[_rng.Next(files.Length)];
                if (!string.Equals(candidate, avoid, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            return files[_rng.Next(files.Length)];
        }
        return null;
    }

    public void SetMode(PlayfieldMode mode)
    {
        _colorTimer.Stop();
        StaticImage.Visibility = Visibility.Collapsed;

        bool enteringVideo = mode is PlayfieldMode.Video or PlayfieldMode.VideoFolders or PlayfieldMode.PinupPlaylist;

        // Leaving (or not entering) video: stop playback and remove the VLC
        // HWND so the blob screensaver keeps GPU-accelerated rendering.
        if (!enteringVideo && _videoMode)
        {
            _videoMode = false;
            StopVideoPlayback();
        }

        // Tear down the screensaver whenever it isn't the active mode. Self-rendering
        // patterns (Game of Life, ProjectM) drive their own render loops that keep
        // burning CPU/GPU while merely hidden, so a collapsed canvas isn't enough —
        // the pattern must actually be disposed.
        if (mode != PlayfieldMode.Screensaver)
            StopScreensaver();

        switch (mode)
        {
            case PlayfieldMode.Blank:
                ScreensaverCanvas.Visibility = Visibility.Collapsed;
                break;

            case PlayfieldMode.Screensaver:
                StartScreensaver();
                break;

            case PlayfieldMode.StaticImage:
                ScreensaverCanvas.Visibility = Visibility.Collapsed;
                StaticImage.Visibility = Visibility.Visible;
                break;

            case PlayfieldMode.Video:
                ScreensaverCanvas.Visibility = Visibility.Collapsed;
                _videoMode = true;
                _folderMode = false;
                _pinupMode = false;
                StartVideoPlayback();
                break;

            case PlayfieldMode.VideoFolders:
                ScreensaverCanvas.Visibility = Visibility.Collapsed;
                _videoMode = true;
                _folderMode = true;
                _pinupMode = false;
                StartVideoPlayback();
                break;

            case PlayfieldMode.PinupPlaylist:
                ScreensaverCanvas.Visibility = Visibility.Collapsed;
                _videoMode = true;
                _folderMode = false;
                _pinupMode = true;
                StartVideoPlayback();
                break;
        }
    }

    /// <summary>
    /// Shows the screensaver canvas and ensures a blob pattern is running. When the canvas
    /// has just become visible it may not have a layout size yet, so pattern creation is
    /// deferred to <see cref="DispatcherPriority.Loaded"/> (after the layout pass) — this is
    /// what fixes the "black screen" when switching to Screensaver from a video mode that
    /// started with the canvas collapsed.
    /// </summary>
    private void StartScreensaver()
    {
        ScreensaverCanvas.Visibility = Visibility.Visible;
        _screensaverActive = true;

        if (_blobsInitialized)
        {
            SyncColorTimer();
            return;
        }

        if (ScreensaverCanvas.ActualWidth > 0)
        {
            CreateBlobs();
            SyncColorTimer();
        }
        else
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ScreensaverCanvas.Visibility != Visibility.Visible)
                    return; // switched away again before layout completed
                if (!_blobsInitialized && ScreensaverCanvas.ActualWidth > 0)
                    CreateBlobs();
                SyncColorTimer();
            }), DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// Stops the color timer and disposes the current blob pattern so its render loop
    /// (CompositionTarget.Rendering / internal timers for self-rendering patterns) stops
    /// consuming CPU/GPU while the screensaver isn't the active mode. The pattern is
    /// recreated by <see cref="StartScreensaver"/> when the user returns to Screensaver.
    /// </summary>
    private void StopScreensaver()
    {
        _screensaverActive = false;
        _colorTimer.Stop();
        if (_currentPattern != null)
        {
            _currentPattern.Dispose();
            _currentPattern = null;
        }
        _blobsInitialized = false;
    }

    /// <summary>
    /// True when playback advances through a list of clips (folder or Pinup mode) using the
    /// crossfade/position-timer pipeline, as opposed to seamless single-file looping.
    /// </summary>
    private bool MultiClipMode => _folderMode || _pinupMode;

    /// <summary>
    /// Starts (or restarts) the playfield video via LibVLC. In single-file mode
    /// the clip loops seamlessly at the decoder level (no MediaEnded/seek stall);
    /// in folder mode a random file plays and advances on EndReached. The player
    /// is muted so it never competes with the backglass music, and the VideoView
    /// stays hidden until the first frame is decoded (Vout) so the black window
    /// background masks the initial decode ramp.
    /// </summary>
    private void StartVideoPlayback()
    {
        // Nothing to play: single-file needs a valid file; folder mode needs folders;
        // pinup mode needs at least one resolved file.
        if (_folderMode)
        {
            if (_videoFolders.Length == 0)
                return;
        }
        else if (_pinupMode)
        {
            if (_pinupFiles.Length == 0)
                return;
        }
        else if (string.IsNullOrWhiteSpace(_videoPath) || !File.Exists(_videoPath))
        {
            return;
        }

        var mp = EnsureVlcInitialized();
        if (mp == null || _libVLC == null)
            return;

        var view = EnsureVideoView();
        if (view == null)
            return;

        // Single-file mode: if the same file is already looping, just show it.
        // (Multi-clip modes always advance, so they never take this shortcut.)
        if (!MultiClipMode && mp.IsPlaying &&
            string.Equals(_playingVideoPath, _videoPath, StringComparison.OrdinalIgnoreCase))
        {
            view.Visibility = Visibility.Visible;
            return;
        }

        // Hide until first frame (revealed in OnVideoVout).
        view.Visibility = Visibility.Hidden;

        // CRITICAL: the VideoView must be Loaded (its child HWND created and
        // handed to the MediaPlayer) before Play(), otherwise libvlc has no
        // render surface and opens its own detached top-level window. When the
        // view was just inserted into the tree it hasn't loaded yet, so defer
        // the actual Play() to its Loaded event.
        if (view.IsLoaded)
        {
            PlayCurrentMedia(mp);
        }
        else
        {
            view.Loaded -= OnVideoViewLoaded;
            view.Loaded += OnVideoViewLoaded;
        }
    }

    private void OnVideoViewLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is LibVLCSharp.WPF.VideoView v)
            v.Loaded -= OnVideoViewLoaded;

        // Schedule Play after the full Loaded pass so the VideoView's own
        // handler has attached the HWND to the MediaPlayer first.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_videoMode && _mediaPlayer != null && _videoView != null && _videoView.IsLoaded)
                PlayCurrentMedia(_mediaPlayer);
        }), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Builds the looped media and starts playback. Assumes the VideoView is
    /// loaded and its HWND is bound to the MediaPlayer.
    /// </summary>
    private void PlayCurrentMedia(VlcMediaPlayer mp)
    {
        if (_libVLC == null)
            return;

        if (MultiClipMode)
        {
            // Multi-clip mode (folder or pinup): choose the next file.
            var pick = _pinupMode ? PickNextPinupFile() : PickNextVideoFile(_playingVideoPath);
            if (pick == null)
            {
                // Nothing playable — abandon the transition so a later attempt
                // isn't blocked by a stuck _videoTransitioning flag.
                _videoTransitioning = false;
                return;
            }

            var media = new VlcMedia(_libVLC, new Uri(pick));
            // Loop seamlessly at the decoder level so a clip shorter than the
            // minimum duration keeps playing without a gap. The position timer
            // decides when to advance (loop-aligned minimum, capped by maximum),
            // using wall-clock elapsed since _clipStartUtc.
            media.AddOption(":input-repeat=65535");
            mp.Play(media);
            _playingVideoPath = pick;
            _clipStartUtc = DateTime.UtcNow;
            ApplyAudioToPlayer();
            return;
        }

        if (string.IsNullOrWhiteSpace(_videoPath) || !File.Exists(_videoPath))
            return;

        var single = new VlcMedia(_libVLC, new Uri(_videoPath));
        // Seamless gapless loop of a single file (huge repeat count).
        single.AddOption(":input-repeat=65535");
        // NOTE: rotation is applied at the LibVLC *instance* level (see
        // BuildVlcArgs) because the transform video-output filter is configured
        // when the vout is created, not from per-media options.
        mp.Play(single);
        _playingVideoPath = _videoPath;
        ApplyAudioToPlayer();
    }

    /// <summary>
    /// Safety fallback for folder mode. Folder clips use :input-repeat so they
    /// normally loop internally (EndReached doesn't fire) and the position timer
    /// drives advancement. If EndReached does fire (e.g. a clip that can't loop),
    /// advance immediately behind a hard-black overlay so no garbage frame shows.
    /// Marshaled to the dispatcher (VLC forbids Play from inside the callback).
    /// </summary>
    private void OnVideoEndReached(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_videoMode || !MultiClipMode || _mediaPlayer == null)
                return;

            StopVideoPositionTimer();
            if (_videoTransitioning)
                return; // transition already handling the swap

            _videoTransitioning = true;
            if (_videoFadeOverlay != null)
            {
                _videoFadeOverlay.BeginAnimation(UIElement.OpacityProperty, null);
                _videoFadeOverlay.Opacity = 1.0; // hard black — no visible frame to fade
            }
            PlayCurrentMedia(_mediaPlayer);
        }));
    }

    /// <summary>
    /// Stops playback (on a background thread to avoid blocking the dispatcher)
    /// and removes the VideoView so blob rendering stays GPU-accelerated.
    /// </summary>
    private void StopVideoPlayback()
    {
        StopVideoPositionTimer();
        _videoTransitioning = false;
        _playingVideoPath = null;
        var mp = _mediaPlayer;
        if (mp != null)
            Task.Run(() => { try { mp.Stop(); } catch { } });
        DetachVideoView();
    }

    /// <summary>
    /// Creates the VLC VideoView and inserts it at the bottom of the Root grid.
    /// Created lazily (only when entering Video mode) so its WinForms-hosted HWND
    /// doesn't force software rendering during idle blob animations.
    /// </summary>
    private LibVLCSharp.WPF.VideoView? EnsureVideoView()
    {
        if (_videoView != null)
            return _videoView;

        var mp = EnsureVlcInitialized();
        if (mp == null)
            return null;

        _videoView = new LibVLCSharp.WPF.VideoView
        {
            Background = System.Windows.Media.Brushes.Black,
            MediaPlayer = mp,
            Visibility = Visibility.Hidden,
            Focusable = false,
        };

        // A black overlay hosted in the VideoView's floating Content layer, which
        // is the only WPF surface that reliably renders OVER the airspace video
        // HWND. We animate its opacity to dip-to-black between clips (a true
        // A→B alpha crossfade isn't possible with a single native surface).
        _videoFadeOverlay = new Border
        {
            Background = System.Windows.Media.Brushes.Black,
            Opacity = 1.0,
            IsHitTestVisible = false,
        };
        _videoView.Content = _videoFadeOverlay;

        // Bottom-most so the black background shows through until first frame,
        // and above nothing that needs to sit behind it.
        System.Windows.Controls.Panel.SetZIndex(_videoView, 0);
        Root.Children.Insert(0, _videoView);
        return _videoView;
    }

    /// <summary>
    /// Removes the VideoView from the visual tree so WPF can use GPU-accelerated
    /// rendering for the idle overlay. The MediaPlayer itself is retained (owned
    /// by this window) and disposed in OnClosed.
    /// </summary>
    private void DetachVideoView()
    {
        if (_videoView != null)
        {
            _videoView.Loaded -= OnVideoViewLoaded;
            _videoView.Content = null;
            _videoFadeOverlay = null;
            Root.Children.Remove(_videoView);
            _videoView = null;
        }
    }

    /// <summary>
    /// Fired by VLC when the video output is created (first frame, Count &gt; 0)
    /// OR torn down (clip end, Count == 0). Only the creation event reveals the
    /// view and fades in; the teardown event is ignored so it can't fight the
    /// transition fade-out. Marshaled to the playfield dispatcher.
    /// </summary>
    private void OnVideoVout(object? sender, LibVLCSharp.Shared.MediaPlayerVoutEventArgs e)
    {
        // Count > 0 = a vout was created (first frame ready).
        // Count == 0 = vout torn down at clip end — must NOT trigger a fade-in.
        if (e.Count <= 0)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (!_videoMode || _videoView == null)
                return;
            _videoTransitioning = false;
            _videoView.Visibility = Visibility.Visible;
            FadeVideoOverlay(0.0, VideoTransitionFadeMs);
            // Folder mode: arm the pre-end fade-to-black for this freshly started clip.
            StartVideoPositionTimer();
        });
    }

    /// <summary>
    /// Animates the black over-video overlay to <paramref name="targetOpacity"/>
    /// (0 = video visible, 1 = black) over the given duration. Used to dip-to-black
    /// between folder clips so the decode gap at the seam isn't visible.
    /// </summary>
    private void FadeVideoOverlay(double targetOpacity, int durationMs, Action? onCompleted = null)
    {
        var overlay = _videoFadeOverlay;
        if (overlay == null)
        {
            onCompleted?.Invoke();
            return;
        }

        var anim = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        anim.Completed += (_, _) =>
        {
            overlay.BeginAnimation(UIElement.OpacityProperty, null);
            overlay.Opacity = targetOpacity;
            onCompleted?.Invoke();
        };
        overlay.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>
    /// Starts a lightweight timer (folder mode only) that watches elapsed on-screen
    /// time and kicks off the fade-to-black when the clip reaches its target
    /// duration (loop-aligned minimum, capped by the maximum), while it is still
    /// rendering — so the dip is visible instead of fading over a torn-down vout.
    /// </summary>
    private void StartVideoPositionTimer()
    {
        StopVideoPositionTimer();
        if (!MultiClipMode)
            return;

        _videoPositionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _videoPositionTimer.Tick += VideoPositionTimer_Tick;
        _videoPositionTimer.Start();
    }

    private void StopVideoPositionTimer()
    {
        if (_videoPositionTimer != null)
        {
            _videoPositionTimer.Stop();
            _videoPositionTimer.Tick -= VideoPositionTimer_Tick;
            _videoPositionTimer = null;
        }
    }

    private void VideoPositionTimer_Tick(object? sender, EventArgs e)
    {
        var mp = _mediaPlayer;
        if (mp == null || _videoTransitioning || !MultiClipMode || !_videoMode)
            return;

        // Elapsed on-screen time measured by wall clock — robust under seamless
        // input-repeat looping (mp.Time resets to 0 on each loop).
        double elapsedMs = (DateTime.UtcNow - _clipStartUtc).TotalMilliseconds;
        if (elapsedMs <= 0)
            return;

        double targetMs = ComputeClipTargetMs(mp);
        if (targetMs <= 0)
            return;

        // Start the fade early enough that the dip-to-black COMPLETES at the target.
        double trigger = targetMs - VideoTransitionFadeMs;
        if (elapsedMs >= Math.Max(0, trigger))
            BeginVideoTransition();
    }

    /// <summary>
    /// Computes how long (ms) the current folder clip should stay on screen:
    /// the minimum duration rounded UP to a whole number of clip loops (so a
    /// clip shorter than the minimum repeats rather than cutting mid-play),
    /// then capped by the maximum duration when one is set. Falls back to the
    /// raw minimum/maximum if the clip length isn't known yet.
    /// </summary>
    private double ComputeClipTargetMs(VlcMediaPlayer mp)
    {
        int minSec = _pinupMode ? _pinupMinDurationSec : _folderMinDurationSec;
        int maxSec = _pinupMode ? _pinupMaxDurationSec : _folderMaxDurationSec;
        double minMs = minSec * 1000.0;
        double maxMs = maxSec > 0 ? maxSec * 1000.0 : double.PositiveInfinity;

        long clipLenMs = mp.Length; // one loop's length (0 until VLC knows it)
        double targetMs;
        if (clipLenMs > 0)
        {
            // Round the minimum up to a whole number of loops (at least one).
            double loops = Math.Max(1, Math.Ceiling(minMs / clipLenMs));
            targetMs = loops * clipLenMs;
        }
        else
        {
            // Length unknown — just use the minimum directly.
            targetMs = minMs;
        }

        // Apply the hard maximum cap (may cut mid-loop, which is intended).
        if (targetMs > maxMs)
            targetMs = maxMs;

        return targetMs;
    }

    /// <summary>
    /// Starts a folder-mode transition: fade to black over the still-rendering
    /// outgoing clip, then swap to the next random file behind the black overlay.
    /// The incoming clip's first frame (OnVideoVout, Count &gt; 0) fades back in.
    /// Idempotent for a given clip via <see cref="_videoTransitioning"/>.
    /// </summary>
    private void BeginVideoTransition()
    {
        if (_videoTransitioning || !_videoMode || !MultiClipMode || _mediaPlayer == null)
            return;

        _videoTransitioning = true;
        StopVideoPositionTimer();

        FadeVideoOverlay(1.0, VideoTransitionFadeMs, () =>
        {
            if (!_videoMode || !MultiClipMode || _mediaPlayer == null)
            {
                _videoTransitioning = false;
                return;
            }
            // Swap behind the fully-black overlay; OnVideoVout clears _videoTransitioning
            // and fades back in when the new clip's first frame arrives.
            PlayCurrentMedia(_mediaPlayer);
        });
    }

    public void SetRotation(int degrees)
    {
        degrees = degrees switch { 90 => 90, 180 => 180, 270 => 270, _ => 0 };
        _rotationDegrees = degrees;
        if (Content is FrameworkElement root)
            root.LayoutTransform = degrees == 0 ? Transform.Identity : new RotateTransform(degrees);

        // The VLC video surface is a native HWND that the WPF LayoutTransform
        // above can't rotate. Rotation is baked into the LibVLC instance args
        // (BuildVlcArgs), so a change requires rebuilding the instance. Dispose
        // the old one and, if a video is active, re-init + replay with the new
        // rotation; otherwise it will be built lazily on next playback.
        // When orientation is not applied to videos, the effective video
        // rotation is forced to 0 (the source is assumed pre-rotated).
        int effectiveVideoRotation = _applyOrientationToVideos ? degrees : 0;
        if (effectiveVideoRotation != _videoRotation)
        {
            _videoRotation = effectiveVideoRotation;
            bool wasVideo = _videoMode && _mediaPlayer != null;
            DisposeVlc();
            if (wasVideo)
                StartVideoPlayback();
        }
    }

    /// <summary>
    /// Controls whether the playfield orientation is applied to videos. When
    /// disabled, videos (single file, folder, and Pinup playlist) skip the
    /// rotation transform because the source is already pre-rotated to match a
    /// physically rotated monitor. The screensaver/image LayoutTransform is
    /// unaffected.
    /// </summary>
    public void SetApplyOrientationToVideos(bool apply)
    {
        if (_applyOrientationToVideos == apply)
            return;
        _applyOrientationToVideos = apply;
        // Re-evaluate the effective video rotation against the current setting.
        SetRotation(_rotationDegrees);
    }

    public void SetOledSleepDefeat(int intervalSeconds, int durationSeconds = 5, int intensityPercent = 80)
    {
        _oledDefeatTimer?.Stop();
        _oledDefeatRevertTimer?.Stop();
        _oledDefeatIntervalSeconds = intervalSeconds;
        _oledDefeatIntensity = Math.Clamp(intensityPercent / 100.0, 0.0, 1.0);

        if (intervalSeconds <= 0)
        {
            _oledDefeatTimer = null;
            _oledDefeatRevertTimer = null;
            return;
        }

        _oledDefeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(intervalSeconds) };
        _oledDefeatTimer.Tick += OledDefeat_Tick;
        _oledDefeatTimer.Start();

        int effectDuration = Math.Clamp(durationSeconds, 1, intervalSeconds - 1);
        _oledDefeatRevertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(effectDuration) };
        _oledDefeatRevertTimer.Tick += OledDefeatRevert_Tick;
    }

    private void OledDefeat_Tick(object? sender, EventArgs e)
    {
        if (_oledDefeatIntensity <= _blobIntensity)
            return;

        _savedIntensity = _blobIntensity;

        _blobIntensity = _oledDefeatIntensity;

        var fadeDuration = TimeSpan.FromMilliseconds(500);
        if (_currentPattern != null)
        {
            foreach (var blob in _currentPattern.Blobs)
            {
                var target = Math.Min(_blobIntensity + _rng.NextDouble() * 0.1, 1.0);
                var anim = new DoubleAnimation(target, fadeDuration);
                var b = blob;
                anim.Completed += (_, _) =>
                {
                    b.BeginAnimation(UIElement.OpacityProperty, null);
                    b.Opacity = target;
                };
                blob.BeginAnimation(UIElement.OpacityProperty, anim);
            }
        }

        _oledDefeatRevertTimer?.Start();
    }

    private void OledDefeatRevert_Tick(object? sender, EventArgs e)
    {
        _oledDefeatRevertTimer?.Stop();

        _blobIntensity = _savedIntensity;

        var fadeDuration = TimeSpan.FromMilliseconds(500);
        if (_currentPattern != null)
        {
            foreach (var blob in _currentPattern.Blobs)
            {
                var target = Math.Min(_blobIntensity + _rng.NextDouble() * 0.1, 1.0);
                var anim = new DoubleAnimation(target, fadeDuration);
                var b = blob;
                anim.Completed += (_, _) =>
                {
                    b.BeginAnimation(UIElement.OpacityProperty, null);
                    b.Opacity = target;
                };
                blob.BeginAnimation(UIElement.OpacityProperty, anim);
            }
        }
    }

    private void ToggleExpand_Click(object sender, RoutedEventArgs e)
    {
        ToggleExpand();
    }

    public void FadeToBlack(double durationSeconds, Action? onCompleted = null)
    {
        SwapFadeOverlay.Visibility = Visibility.Visible;
        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = 1.0,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
        };
        if (onCompleted != null)
            anim.Completed += (_, _) => onCompleted();
        SwapFadeOverlay.BeginAnimation(OpacityProperty, anim);
    }

    public void FadeFromBlack(double durationSeconds, Action? onCompleted = null)
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = 0.0,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
        };
        anim.Completed += (_, _) =>
        {
            SwapFadeOverlay.Visibility = Visibility.Collapsed;
            onCompleted?.Invoke();
        };
        SwapFadeOverlay.BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>
    /// Applies a blur effect to the playfield window's root content to mask swap transitions.
    /// </summary>
    public void ApplySwapBlur(double radius = 35)
    {
        if (Content is FrameworkElement root)
            root.Effect = new BlurEffect { Radius = radius, RenderingBias = RenderingBias.Performance };
    }

    /// <summary>
    /// Animates a blur effect onto the playfield window's root content, then invokes a callback when complete.
    /// </summary>
    public void AnimateApplyBlur(double targetRadius, double durationSeconds, Action? onCompleted = null)
    {
        if (Content is not FrameworkElement root) { onCompleted?.Invoke(); return; }

        var blur = new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
        root.Effect = blur;

        var anim = new DoubleAnimation
        {
            To = targetRadius,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        anim.Completed += (_, _) => onCompleted?.Invoke();
        blur.BeginAnimation(BlurEffect.RadiusProperty, anim);
    }

    /// <summary>
    /// Animates the blur away from the playfield window, revealing sharp content.
    /// </summary>
    public void AnimateRemoveBlur(double durationSeconds, Action? onCompleted = null)
    {
        if (Content is not FrameworkElement root || root.Effect is not BlurEffect blur)
        {
            onCompleted?.Invoke();
            return;
        }

        var anim = new DoubleAnimation
        {
            To = 0.0,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        anim.Completed += (_, _) =>
        {
            root.Effect = null;
            onCompleted?.Invoke();
        };
        blur.BeginAnimation(BlurEffect.RadiusProperty, anim);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Detach VLC events first to prevent callbacks during teardown.
        var mp = _mediaPlayer;
        var vlc = _libVLC;
        if (mp != null)
        {
            mp.Vout -= OnVideoVout;
            mp.EndReached -= OnVideoEndReached;
        }
        _mediaPlayer = null;
        _libVLC = null;

        // Stop and dispose the MediaPlayer and the dedicated LibVLC instance on a
        // background thread to avoid deadlocking the dispatcher (VLC callbacks may
        // be waiting on it while Stop() blocks here). Unlike the backglass, the
        // playfield owns its LibVLC instance, so it disposes it here.
        if (mp != null || vlc != null)
        {
            Task.Run(() =>
            {
                try { mp?.Stop(); } catch { }
                try { mp?.Dispose(); } catch { }
                try { vlc?.Dispose(); } catch { }
            }).Wait(TimeSpan.FromSeconds(5));
        }

        base.OnClosed(e);
    }
}
