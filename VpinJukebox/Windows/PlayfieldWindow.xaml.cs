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

namespace VpinJukebox;

public partial class PlayfieldWindow : JukeboxWindow
{
    private readonly DispatcherTimer _colorTimer;
    private readonly Random _rng = new();
    private double _hueOffset;
    private double _blobIntensity = 0.25;
    private double _blobSpeedMultiplier = 1.0;
    private BlobPattern _blobPattern = BlobPattern.Random;
    private BlobPattern _blobPatternSetting = BlobPattern.Random;
    private bool _transitioning;
    private IBlobPattern? _currentPattern;
    private AudioReactiveService? _audioReactive;
    private double[]? _baseBlobSizes;
    private double _reactiveHueBoost;
    private int _blobCount = 10;
    private bool _blobsInitialized;
    private double[] _blobHueOffsets = [];
    private RoygbivColor? _lastColorBand;
    private DispatcherTimer? _oledDefeatTimer;
    private DispatcherTimer? _oledDefeatRevertTimer;
    private int _oledDefeatIntervalSeconds;
    private double _savedIntensity;
    private double _oledDefeatIntensity = 0.8;
    private double _brightnessBoost = 1.1;

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
    public event Action<RoygbivColor>? BlobColorBandChanged;

    public PlayfieldWindow()
    {
        InitializeComponent();

        _colorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
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
        if (!_blobsInitialized && ScreensaverCanvas.ActualWidth > 0)
        {
            CreateBlobs();
        }

        _colorTimer.Start();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_blobsInitialized || ScreensaverCanvas.ActualWidth <= 0)
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
            UseBitmapCache = false,
        };
    }

    private void CreateBlobs()
    {
        _baseBlobSizes = null;
        _blobHueOffsets = [];

        _currentPattern?.Dispose();
        _currentPattern = BlobTransition.Create(_blobPattern, MakeConfig());
        SubscribeProjectMColorBand();
        _currentPattern.Enter(() => { });

        _blobsInitialized = true;
    }

    private void AnimateBlobs(object? sender, EventArgs e)
    {
        var brushes = _currentPattern?.Brushes;
        var gradBrushes = _currentPattern?.GradientBrushes;
        if (brushes == null || brushes.Count == 0) return;

        _hueOffset += 0.3;

        // Ensure per-blob random hue offsets exist
        if (_blobHueOffsets.Length != brushes.Count)
            _blobHueOffsets = Enumerable.Range(0, brushes.Count).Select(_ => _rng.NextDouble() * 360.0).ToArray();

        Span<int> bandCounts = stackalloc int[7];
        for (int i = 0; i < brushes.Count; i++)
        {
            double hue = (_hueOffset + _reactiveHueBoost + _blobHueOffsets[i]) % 360.0;
            double lightness = Math.Clamp((0.15 + _blobIntensity * 0.7) * _brightnessBoost, 0.0, 1.0);
            var color = HslToColor(hue, 0.7, lightness);
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
            bandCounts[(int)RoygbivHelper.FromHue(hue)]++;
        }

        // Detect dominant color band (mode) across all blobs and notify on change
        if (brushes.Count > 0)
        {
            int maxCount = 0;
            var modeBand = RoygbivColor.Red;
            for (int b = 0; b < 7; b++)
            {
                if (bandCounts[b] > maxCount)
                {
                    maxCount = bandCounts[b];
                    modeBand = (RoygbivColor)b;
                }
            }
            if (modeBand != _lastColorBand)
            {
                _lastColorBand = modeBand;
                BlobColorBandChanged?.Invoke(modeBand);
            }
        }
    }

    private static Color HslToColor(double h, double s, double l)
    {
        double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
        double x = c * (1.0 - Math.Abs((h / 60.0) % 2.0 - 1.0));
        double m = l - c / 2.0;

        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        return Color.FromRgb(
            (byte)((r + m) * 255),
            (byte)((g + m) * 255),
            (byte)((b + m) * 255));
    }

    public void SetScreensaverSettings(double intensity, double speed)
    {
        double newIntensity = Math.Clamp(intensity, 0.05, 0.8);
        bool intensityChanged = Math.Abs(newIntensity - _blobIntensity) > 0.001;
        _blobIntensity = newIntensity;
        _blobSpeedMultiplier = Math.Clamp(speed, 0.1, 5.0);

        if (intensityChanged && _currentPattern != null)
        {
            foreach (var blob in _currentPattern.Blobs)
                blob.Opacity = _blobIntensity + _rng.NextDouble() * 0.1;
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

    public void SetReactiveAudio(AudioReactiveService? service)
    {
        if (_audioReactive != null)
            _audioReactive.Updated -= OnAudioUpdated;

        _audioReactive = service;
        _baseBlobSizes = null;

        if (_audioReactive != null)
            _audioReactive.Updated += OnAudioUpdated;
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
        int clamped = Math.Clamp(count, 0, 25);
        bool changed = clamped != _blobCount;
        _blobCount = clamped;

        // During initial setup, just store the value — SetBlobPattern will
        // create the pattern with the correct count via MakeConfig().
        if (!_blobsInitialized || !changed)
            return;

        // Defer creation until the canvas has a real size
        if (ScreensaverCanvas.ActualWidth <= 0 || ScreensaverCanvas.ActualHeight <= 0)
            return;

        _baseBlobSizes = null;
        _currentPattern?.Dispose();
        _currentPattern = BlobTransition.Create(_blobPattern, MakeConfig());
        SubscribeProjectMColorBand();
        _currentPattern.Enter(() => { });
        _colorTimer.Start();
    }

    public void SetBlobPattern(BlobPattern pattern)
    {
        _transitioning = false;
        _blobPatternSetting = pattern;

        if (pattern == BlobPattern.RandomPerSong)
            pattern = BlobTransition.CurrentRandomPattern;

        _blobPattern = pattern;
        _baseBlobSizes = null;

        // If the canvas isn't laid out yet, just store the pattern —
        // OnLoaded/CreateBlobs will create the blobs once Loaded fires.
        if (ScreensaverCanvas.ActualWidth < 1 || ScreensaverCanvas.ActualHeight < 1)
            return;

        _currentPattern?.Dispose();
        _currentPattern = BlobTransition.Create(pattern, MakeConfig());
        SubscribeProjectMColorBand();
        _currentPattern.Enter(() => { });
        _blobsInitialized = true;
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
            pmPattern.Renderer.ColorBandChanged += band =>
            {
                if (band != _lastColorBand)
                {
                    _lastColorBand = band;
                    BlobColorBandChanged?.Invoke(band);
                }
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
    /// If the pattern is RandomPerSong, smoothly transition to a new random pattern.
    /// Called when a new song starts playing.
    /// </summary>
    public void OnSongChanged()
    {
        if (_blobPatternSetting != BlobPattern.RandomPerSong || _transitioning || _currentPattern == null)
            return;

        _transitioning = true;
        _baseBlobSizes = null;

        _currentPattern.Exit(() =>
        {
            var newPattern = BlobTransition.CurrentRandomPattern;
            DebugLog.Log("Playfield", $"Transition {_blobPattern} -> {newPattern} blob pattern");
            _blobPattern = newPattern;

            _currentPattern?.Dispose();
            _currentPattern = BlobTransition.Create(newPattern, MakeConfig());
            SubscribeProjectMColorBand();
            _currentPattern.Enter(() =>
            {
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

    public void SetVideoPath(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            VideoPlayer.Source = new Uri(path, UriKind.Absolute);
        }
        else
        {
            VideoPlayer.Source = null;
        }
    }

    private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        VideoPlayer.Position = TimeSpan.Zero;
        VideoPlayer.Play();
    }

    public void SetMode(PlayfieldMode mode)
    {
        _colorTimer.Stop();
        StaticImage.Visibility = Visibility.Collapsed;
        VideoPlayer.Visibility = Visibility.Collapsed;
        VideoPlayer.Stop();

        switch (mode)
        {
            case PlayfieldMode.Blank:
                ScreensaverCanvas.Visibility = Visibility.Collapsed;
                break;

            case PlayfieldMode.Screensaver:
                ScreensaverCanvas.Visibility = Visibility.Visible;
                _colorTimer.Start();
                break;

            case PlayfieldMode.StaticImage:
                ScreensaverCanvas.Visibility = Visibility.Collapsed;
                StaticImage.Visibility = Visibility.Visible;
                break;

            case PlayfieldMode.Video:
                ScreensaverCanvas.Visibility = Visibility.Collapsed;
                VideoPlayer.Visibility = Visibility.Visible;
                if (VideoPlayer.Source != null)
                {
                    VideoPlayer.Position = TimeSpan.Zero;
                    VideoPlayer.Play();
                }
                break;
        }
    }

    public void SetRotation(int degrees)
    {
        degrees = degrees switch { 90 => 90, 180 => 180, 270 => 270, _ => 0 };
        if (Content is FrameworkElement root)
            root.LayoutTransform = degrees == 0 ? Transform.Identity : new RotateTransform(degrees);
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
}
