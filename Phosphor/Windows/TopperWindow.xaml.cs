using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using WpfMedia = System.Windows.Media;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace Phosphor;

public partial class TopperWindow : JukeboxWindow
{
    private readonly Random _rng = new();
    private readonly DispatcherTimer _colorTimer;
    private double _hueOffset;
    private bool _animStarted;
    private double _blobIntensity = 0.5;
    private double _blobSpeedMultiplier = 1.0;
    private bool _logoSpin = true;
    private LogoRingsMode _logoRings = LogoRingsMode.Standard;
    private string _logoText = "\u2022 PHOSPHOR \u2022 PHOSPHOR ";
    private double _distortion;
    private BlobPattern _blobPattern = BlobPattern.Random;
    private BlobPattern _blobPatternSetting = BlobPattern.Random;
    private bool _transitioning;
    private IBlobPattern? _currentPattern;
    private AudioReactiveService? _audioReactive;
    private double _reactiveHueBoost;
    private int _blobCount = 4;
    private int _blobSizeOffset;
    private bool _morphColors;

    public TopperWindow()
    {
        _colorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _colorTimer.Tick += ColorCycleBlobs;

        InitializeComponent();

        Loaded += (_, _) => StartAnimation();
    }

    public void SetBlobCount(int count)
    {
        _blobCount = Math.Clamp(count, 0, 100);
        if (!_animStarted) return;

        _currentPattern?.Dispose();
        _currentPattern = null;

        var pattern = _blobPattern;
        _currentPattern = BlobTransition.Create(pattern, MakeConfig());
        _currentPattern.Enter(() => { });
    }

    public void SetBlobSizeOffset(int offset)
    {
        int clamped = Math.Clamp(offset, 1, 20);
        bool changed = clamped != _blobSizeOffset;
        _blobSizeOffset = clamped;
        if (!_animStarted || !changed) return;

        _currentPattern?.Dispose();
        _currentPattern = null;

        var pattern = _blobPattern;
        _currentPattern = BlobTransition.Create(pattern, MakeConfig());
        _currentPattern.Enter(() => { });
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

        _currentPattern.ApplyAudioReactive(data, _blobIntensity, _audioReactive?.ReactiveSpeedMs ?? 120);
        _reactiveHueBoost = data.Treble * 90.0;
    }

    public void SetBlobPattern(BlobPattern pattern)
    {
        _transitioning = false;
        _blobPatternSetting = pattern;

        if (pattern == BlobPattern.RandomPerSong)
            pattern = BlobTransition.CurrentRandomPattern;

        _blobPattern = pattern;

        // If the canvas isn't laid out yet
        // StartAnimation will create the blobs once Loaded fires.
        if (BlobCanvas.ActualWidth < 1 || BlobCanvas.ActualHeight < 1)
            return;

        _currentPattern?.Dispose();
        _currentPattern = BlobTransition.Create(pattern, MakeConfig());
        _currentPattern.Enter(() => { });
        _animStarted = true;
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

    public void ApplyProjectMTuning()
    {
        if (_blobPattern == BlobPattern.ProjectM && _currentPattern is ProjectMPattern pm)
            pm.ApplyTuningSettings();
    }

    /// <summary>
    /// If the pattern is RandomPerSong, smoothly transition to a new random pattern.
    /// </summary>
    public void OnSongChanged()
    {
        if (_blobPatternSetting != BlobPattern.RandomPerSong || _transitioning || _currentPattern == null)
            return;

        _transitioning = true;

        _currentPattern.Exit(() =>
        {
            var newPattern = BlobTransition.CurrentRandomPattern;
            DebugLog.Log("Topper", $"Transition {_blobPattern} -> {newPattern} blob pattern");
            _blobPattern = newPattern;

            _currentPattern?.Dispose();
            _currentPattern = BlobTransition.Create(newPattern, MakeConfig());
            _currentPattern.Enter(() =>
            {
                _transitioning = false;
            });
        });
    }

    private BlobPatternConfig MakeConfig() => new()
    {
        Canvas = BlobCanvas,
        BlobCount = _blobCount,
        Intensity = _blobIntensity,
        SpeedMultiplier = _blobSpeedMultiplier,
        Rng = _rng,
        BlobSizeFactory = r => 180 + r.NextDouble() * 240,
        BlobSizeOffset = _blobSizeOffset,
        UseBitmapCache = false,
    };

    private void StartAnimation()
    {
        if (!_animStarted)
        {
            _animStarted = true;

            var pattern = _blobPattern;
            _currentPattern = BlobTransition.Create(pattern, MakeConfig());
            _currentPattern.Enter(() => { });
        }

        _colorTimer.Start();

        DrawRecordOverlay(RecordOverlay, _logoRings);
        DrawCircularTitle(TitleCanvas, _logoSpin);
    }

    public void SetDistortion(double distortion)
    {
        _distortion = Math.Clamp(distortion, -1.0, 1.0);
        ApplyDistortion();
    }

    private void ApplyDistortion()
    {
        double scaleX = _distortion > 0 ? 1.0 + _distortion : 1.0;
        double scaleY = _distortion < 0 ? 1.0 - _distortion : 1.0;

        // Apply scale to the non-rotating wrapper so it stays axis-aligned
        // with the monitor while the title canvas spins inside it.
        DistortionContainer.RenderTransform = new WpfMedia.ScaleTransform(scaleX, scaleY);
    }

    public void SetLogoSpin(bool spin)
    {
        _logoSpin = spin;
        if (_animStarted)
            DrawCircularTitle(TitleCanvas, _logoSpin);
    }

    public void SetLogoRings(LogoRingsMode mode)
    {
        _logoRings = mode;
        if (_animStarted)
            DrawRecordOverlay(RecordOverlay, _logoRings);
    }

    public void SetLogoRingsBrightness(int percent)
    {
        _recordRingsBrightness = Math.Clamp(percent / 100.0, 0.0, 1.0);
        if (_animStarted)
            DrawRecordOverlay(RecordOverlay, _logoRings);
    }

    public void SetLogoText(string text)
    {
        _logoText = text;
        if (_animStarted)
            DrawCircularTitle(TitleCanvas, _logoSpin);
    }



    private void ColorCycleBlobs(object? sender, EventArgs e)
    {
        var brushes = _currentPattern?.Brushes;
        var gradBrushes = _currentPattern?.GradientBrushes;
        if (brushes == null || brushes.Count == 0) return;

        _hueOffset += 1.0;
        double lightness = 0.15 + _blobIntensity * 0.7;
        for (int i = 0; i < brushes.Count; i++)
        {
            double hue = (_hueOffset + _reactiveHueBoost + i * 60.0) % 360.0;
            var color = HslToColor(hue, 0.7, lightness);
            brushes[i].Color = color;
            if (gradBrushes != null && i < gradBrushes.Count)
            {
                var stops = gradBrushes[i].GradientStops;
                if (stops.Count >= 2)
                {
                    stops[0].Color = WpfColor.FromArgb(255, color.R, color.G, color.B);
                    stops[1].Color = WpfColor.FromArgb(120, color.R, color.G, color.B);
                }
            }
        }
    }

    private static WpfColor HslToColor(double h, double s, double l)
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

        return WpfColor.FromRgb(
            (byte)((r + m) * 255),
            (byte)((g + m) * 255),
            (byte)((b + m) * 255));
    }

    private static LogoRingsMode _recordRingsMode = LogoRingsMode.Standard;
    private static double _recordRingsBrightness = 1.0;

    private static void DrawRecordOverlay(System.Windows.Controls.Canvas canvas, LogoRingsMode ringsMode)
    {
        _recordRingsMode = ringsMode;
        canvas.Children.Clear();
        canvas.CacheMode = null;
        canvas.SizeChanged -= OnRecordCanvasSizeChanged;
        canvas.SizeChanged += OnRecordCanvasSizeChanged;
        RedrawRecord(canvas);
    }

    private static void OnRecordCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.Canvas c)
            RedrawRecord(c);
    }

    private static void RedrawRecord(System.Windows.Controls.Canvas canvas)
    {
        canvas.Children.Clear();
        double w = canvas.ActualWidth;
        double h = canvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double cx = w / 2;
        double cy = h / 2;
        double maxR = Math.Min(w, h) * 0.40;

        double holeR = maxR * 0.04;
        double labelR = maxR * 0.22;
        double grooveStart = maxR * 0.28;

        double b = _recordRingsBrightness * 4.0;

        var hole = new Ellipse
        {
            Width = holeR * 2, Height = holeR * 2,
            Fill = new WpfMedia.SolidColorBrush(WpfColor.FromArgb((byte)Math.Clamp(18 * b, 0, 255), 255, 255, 255)),
        };
        System.Windows.Controls.Canvas.SetLeft(hole, cx - holeR);
        System.Windows.Controls.Canvas.SetTop(hole, cy - holeR);
        canvas.Children.Add(hole);

        var label = new Ellipse
        {
            Width = labelR * 2, Height = labelR * 2,
            Stroke = new WpfMedia.SolidColorBrush(WpfColor.FromArgb((byte)Math.Clamp(10 * b, 0, 255), 255, 255, 255)),
            StrokeThickness = 1,
            Fill = new WpfMedia.SolidColorBrush(WpfColor.FromArgb((byte)Math.Clamp(6 * b, 0, 255), 255, 255, 255)),
        };
        System.Windows.Controls.Canvas.SetLeft(label, cx - labelR);
        System.Windows.Controls.Canvas.SetTop(label, cy - labelR);
        canvas.Children.Add(label);

        if (_recordRingsMode != LogoRingsMode.Off)
        {
            double spacing = _recordRingsMode == LogoRingsMode.Reduced ? 12.0 : 4.0;
            for (double r = grooveStart; r <= maxR; r += spacing)
            {
                byte alpha = (byte)Math.Clamp((5 + (r - grooveStart) / (maxR - grooveStart) * 8) * b, 0, 255);
                var ring = new Ellipse
                {
                    Width = r * 2, Height = r * 2,
                    Stroke = new WpfMedia.SolidColorBrush(WpfColor.FromArgb(alpha, 255, 255, 255)),
                    StrokeThickness = 0.5,
                    Fill = WpfMedia.Brushes.Transparent,
                };
                System.Windows.Controls.Canvas.SetLeft(ring, cx - r);
                System.Windows.Controls.Canvas.SetTop(ring, cy - r);
                canvas.Children.Add(ring);
            }
        }

        var rim = new Ellipse
        {
            Width = maxR * 2, Height = maxR * 2,
            Stroke = new WpfMedia.SolidColorBrush(WpfColor.FromArgb((byte)Math.Clamp(15 * b, 0, 255), 255, 255, 255)),
            StrokeThickness = 1.5,
            Fill = WpfMedia.Brushes.Transparent,
        };
        System.Windows.Controls.Canvas.SetLeft(rim, cx - maxR);
        System.Windows.Controls.Canvas.SetTop(rim, cy - maxR);
        canvas.Children.Add(rim);

        canvas.CacheMode = new WpfMedia.BitmapCache(1.0);
    }

    private void DrawCircularTitle(System.Windows.Controls.Canvas canvas, bool spin)
    {
        canvas.Children.Clear();
        canvas.RenderTransform = null;
        canvas.SizeChanged -= OnTitleCanvasSizeChanged;
        canvas.SizeChanged += OnTitleCanvasSizeChanged;
        RedrawCircularTitle(canvas, spin);
    }

    private void OnTitleCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.Canvas c)
            RedrawCircularTitle(c, _logoSpin);
    }

    private void RedrawCircularTitle(System.Windows.Controls.Canvas canvas, bool spin)
    {
        canvas.Children.Clear();
        canvas.RenderTransform = null;
        double w = canvas.ActualWidth;
        double h = canvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double cx = w / 2;
        double cy = h / 2;
        double radius = Math.Min(w, h) * 0.40 * 0.18 + 60;

        var text = _logoText;
        double fontSize = Math.Max(10, Math.Min(w, h) * 0.024);

        double angleStep = 360.0 / text.Length;

        var brush = new WpfMedia.SolidColorBrush(WpfColor.FromArgb(180, 0x88, 0xCC, 0xFF));
        if (!_morphColors) brush.Freeze();
        var font = new WpfMedia.FontFamily("Segoe UI");

        double startAngle = spin ? -90.0 : -90.0 + 270.0;

        for (int i = 0; i < text.Length; i++)
        {
            double angleDeg = startAngle + i * angleStep;
            double angleRad = angleDeg * Math.PI / 180.0;

            var tb = new System.Windows.Controls.TextBlock
            {
                Text = text[i].ToString(),
                FontSize = fontSize,
                FontWeight = FontWeights.Bold,
                FontFamily = font,
                Foreground = brush,
                RenderTransformOrigin = new WpfPoint(0.5, 0.5),
            };

            tb.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            double charW = tb.DesiredSize.Width;
            double charH = tb.DesiredSize.Height;

            double x = cx + radius * Math.Cos(angleRad);
            double y = cy + radius * Math.Sin(angleRad);

            tb.RenderTransform = new WpfMedia.RotateTransform(angleDeg + 90);
            System.Windows.Controls.Canvas.SetLeft(tb, x - charW / 2);
            System.Windows.Controls.Canvas.SetTop(tb, y - charH / 2);
            canvas.Children.Add(tb);
        }

        canvas.CacheMode = new WpfMedia.BitmapCache(1.0);

        if (spin)
        {
            var rotate = new WpfMedia.RotateTransform(0, cx, cy);
            canvas.RenderTransform = rotate;
            var spinAnim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(60))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            rotate.BeginAnimation(WpfMedia.RotateTransform.AngleProperty, spinAnim);
        }
    }

    public void SetLogoMorphColor(LogoColorMode mode)
    {
        _morphColors = mode != LogoColorMode.Off;

        if (_animStarted)
        {
            DrawRecordOverlay(RecordOverlay, _logoRings);
            DrawCircularTitle(TitleCanvas, _logoSpin);
        }

        if (mode == LogoColorMode.Off)
            ApplyResetColors();
    }

    /// <summary>
    /// Morphs the topper logo to a specific ROYGBIV color band (reactive mode).
    /// </summary>
    public void MorphLogoToColor(RoygbivColor color)
    {
        if (!_morphColors) return;

        double hue = color switch
        {
            RoygbivColor.Red => 0,
            RoygbivColor.Orange => 30,
            RoygbivColor.Yellow => 60,
            RoygbivColor.Green => 120,
            RoygbivColor.Blue => 210,
            RoygbivColor.Indigo => 240,
            RoygbivColor.Violet => 280,
            RoygbivColor.White => 200,
            _ => 0
        };

        var titleColor = HslToColor(hue, 0.7, 0.55);
        var recordColor = HslToColor((hue + 30) % 360, 0.6, 0.5);
        ApplyMorphColors(titleColor, recordColor);
    }

    /// <summary>
    /// Applies morph colors received from the backglass window.
    /// </summary>
    public void ApplyMorphColors(WpfColor titleColor, WpfColor recordColor)
    {
        if (!_morphColors) return;

        var duration = TimeSpan.FromSeconds(1);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };

        // CacheMode is intentionally left in place during the morph; toggling
        // it caused visible hitches at the animation boundaries.

        foreach (var child in TitleCanvas.Children)
        {
            if (child is System.Windows.Controls.TextBlock tb
                && tb.Foreground is WpfMedia.SolidColorBrush brush
                && !brush.IsFrozen)
            {
                var anim = new ColorAnimation
                {
                    To = WpfColor.FromArgb(180, titleColor.R, titleColor.G, titleColor.B),
                    Duration = duration,
                    EasingFunction = ease
                };
                brush.BeginAnimation(WpfMedia.SolidColorBrush.ColorProperty, anim);
            }
        }

        foreach (var child in RecordOverlay.Children)
        {
            if (child is Ellipse ellipse)
            {
                if (ellipse.Fill is WpfMedia.SolidColorBrush fill && !fill.IsFrozen)
                {
                    byte alpha = fill.Color.A;
                    if (alpha > 0)
                    {
                        var anim = new ColorAnimation
                        {
                            To = WpfColor.FromArgb(alpha, recordColor.R, recordColor.G, recordColor.B),
                            Duration = duration,
                            EasingFunction = ease
                        };
                        fill.BeginAnimation(WpfMedia.SolidColorBrush.ColorProperty, anim);
                    }
                }
                if (ellipse.Stroke is WpfMedia.SolidColorBrush stroke && !stroke.IsFrozen)
                {
                    byte alpha = stroke.Color.A;
                    var anim = new ColorAnimation
                    {
                        To = WpfColor.FromArgb(alpha, recordColor.R, recordColor.G, recordColor.B),
                        Duration = duration,
                        EasingFunction = ease
                    };
                    stroke.BeginAnimation(WpfMedia.SolidColorBrush.ColorProperty, anim);
                }
            }
        }
    }

    /// <summary>
    /// Resets logo colors to defaults, synchronized from the backglass.
    /// </summary>
    public void ApplyResetColors()
    {
        if (!_animStarted) return;

        var duration = TimeSpan.FromSeconds(2);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        var defaultTitle = WpfColor.FromArgb(180, 0x88, 0xCC, 0xFF);

        foreach (var child in TitleCanvas.Children)
        {
            if (child is System.Windows.Controls.TextBlock tb
                && tb.Foreground is WpfMedia.SolidColorBrush brush
                && !brush.IsFrozen)
            {
                var anim = new ColorAnimation { To = defaultTitle, Duration = duration, EasingFunction = ease };
                brush.BeginAnimation(WpfMedia.SolidColorBrush.ColorProperty, anim);
            }
        }

        foreach (var child in RecordOverlay.Children)
        {
            if (child is Ellipse ellipse)
            {
                if (ellipse.Fill is WpfMedia.SolidColorBrush fill && !fill.IsFrozen)
                {
                    byte alpha = fill.Color.A;
                    if (alpha > 0)
                    {
                        var anim = new ColorAnimation { To = WpfColor.FromArgb(alpha, 255, 255, 255), Duration = duration, EasingFunction = ease };
                        fill.BeginAnimation(WpfMedia.SolidColorBrush.ColorProperty, anim);
                    }
                }
                if (ellipse.Stroke is WpfMedia.SolidColorBrush stroke && !stroke.IsFrozen)
                {
                    byte alpha = stroke.Color.A;
                    var anim = new ColorAnimation { To = WpfColor.FromArgb(alpha, 255, 255, 255), Duration = duration, EasingFunction = ease };
                    stroke.BeginAnimation(WpfMedia.SolidColorBrush.ColorProperty, anim);
                }
            }
        }
    }

    private void ToggleExpand_Click(object sender, RoutedEventArgs e) => ToggleExpand();
}
