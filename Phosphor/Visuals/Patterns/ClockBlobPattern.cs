using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace Phosphor;

/// <summary>
/// Clock visualization with two modes controlled by <see cref="ClockMode"/>:
/// <list type="bullet">
/// <item><b>Analog</b> – 12 dim marker blobs at hour positions (aligned to the logo
/// ring radius). Brighter blobs for hour (inner ring), minute (marker ring), and
/// second (outer ring) hands that animate smoothly around the face.</item>
/// <item><b>Digital</b> – "HH:MM:SS" rendered as a 5×7 dot-matrix of small blobs,
/// updated once per second. Font size controlled by <see cref="DigitalSize"/>.</item>
/// </list>
/// </summary>
public sealed class ClockBlobPattern : IBlobPattern
{
    // ─── Static tuning (set from AppSettings before pattern creation) ─

    /// <summary>0 = Analog, 1 = Digital dot-matrix.</summary>
    public static int ClockMode { get; set; }

    /// <summary>Clock brightness override (0.05–1.0).</summary>
    public static double Brightness { get; set; } = 0.5;

    /// <summary>Digital font size scale (1–20, 10 = 100%).</summary>
    public static int DigitalSize { get; set; } = 10;

    /// <summary>True for 24-hour format, false for 12-hour with AM/PM.</summary>
    public static bool Use24Hour { get; set; } = true;

    /// <summary>Analog blob size index: 0=Smallest, 1=Small, 2=Medium, 3=Large, 4=Largest.</summary>
    public static int AnalogSize { get; set; } = 2;

    /// <summary>0 = Modern (clean), 1 = Traditional (hand trail blobs).</summary>
    public static int AnalogStyle { get; set; }

    // ─── Instance fields ─────────────────────────────────────────────

    private readonly Canvas _canvas;
    private readonly Random _rng;
    private readonly double _brightness;
    private readonly double _sizeMultiplier;
    private readonly double _digitalSizeMultiplier;
    private readonly bool _isDigital;

    private readonly double _maxOrbitRadius;
    private readonly List<FrameworkElement> _blobs = new();
    private readonly List<SolidColorBrush> _brushes = new();
    private readonly List<RadialGradientBrush> _gradBrushes = new();

    private DispatcherTimer? _timer;
    private bool _disposed;

    // Analog-specific
    private Ellipse? _secondBlob;
    private Ellipse? _minuteBlob;
    private Ellipse? _hourBlob;
    private readonly List<Ellipse> _markerBlobs = new();
    private readonly List<Ellipse> _hourTrailBlobs = new();
    private readonly List<Ellipse> _minuteTrailBlobs = new();

    // Digital-specific
    private readonly List<Ellipse> _dotBlobs = new();
    private string _lastTimeString = "";

    public BlobPattern PatternType => BlobPattern.Clock;
    public bool ManagesOwnColors => false;
    public IReadOnlyList<FrameworkElement> Blobs => _blobs;
    public IReadOnlyList<SolidColorBrush> Brushes => _brushes;
    public IReadOnlyList<RadialGradientBrush> GradientBrushes => _gradBrushes;

    public ClockBlobPattern(BlobPatternConfig config)
    {
        _canvas = config.Canvas;
        _rng = config.Rng;
        _brightness = Math.Clamp(Brightness * 1.15, 0.05, 1.0);
        _sizeMultiplier = Math.Clamp(config.BlobSizeOffset, 1, 20) / 10.0;
        _digitalSizeMultiplier = 0.55 + (Math.Clamp(DigitalSize, 5, 100) - 5) * 0.50 / 95.0;
        _isDigital = ClockMode == 1;
        _maxOrbitRadius = config.MaxOrbitRadius;
    }

    // ─── IBlobPattern lifecycle ───────────────────────────────────────

    public void Enter(Action onComplete)
    {
        if (_disposed) { onComplete(); return; }

        if (_isDigital)
            CreateDigital();
        else
            CreateAnalog();

        // Fade all blobs in
        foreach (var blob in _blobs)
        {
            double target = blob.Opacity;
            bool isDot = _isDigital && _dotBlobs.Contains(blob);
            blob.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
            {
                From = 0,
                To = target,
                Duration = TimeSpan.FromSeconds(0.8),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                // Digital dots need FillBehavior.Stop so UpdateDigital() can set Opacity directly
                FillBehavior = isDot ? FillBehavior.Stop : FillBehavior.HoldEnd,
            });
        }

        StartTimer();
        onComplete();
    }

    public void Exit(Action onComplete)
    {
        StopTimer();

        if (_blobs.Count == 0) { Cleanup(); onComplete(); return; }

        foreach (var blob in _blobs)
        {
            blob.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromSeconds(0.6),
            });
        }

        var exitTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromSeconds(0.65),
        };
        exitTimer.Tick += (_, _) =>
        {
            exitTimer.Stop();
            Cleanup();
            onComplete();
        };
        exitTimer.Start();
    }

    public void ApplyAudioReactive(AudioReactiveData data, double baseIntensity, double reactiveSpeedMs)
    {
        if (_disposed) return;

        float beat = data.IsBeat ? 0.15f : 0f;
        double targetScale = 1.0 + data.Bass * 0.3 + beat;
        targetScale = Math.Min(targetScale, 1.4);
        double lerpFactor = Math.Clamp(16.0 / Math.Max(1.0, reactiveSpeedMs), 0.05, 1.0);

        foreach (var blob in _blobs)
        {
            if (blob.RenderTransform is not ScaleTransform st)
            {
                st = new ScaleTransform(1, 1);
                blob.RenderTransform = st;
            }
            if (st.HasAnimatedProperties)
            {
                st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }
            st.ScaleX += (targetScale - st.ScaleX) * lerpFactor;
            st.ScaleY += (targetScale - st.ScaleY) * lerpFactor;
        }
    }

    public void ResetAudioReactive(double baseIntensity)
    {
        if (_disposed) return;
        foreach (var blob in _blobs)
        {
            if (blob.RenderTransform is ScaleTransform st)
            {
                st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                st.ScaleX = 1;
                st.ScaleY = 1;
            }
        }
    }

    public void PulseDominantColor(RoygbivColor band) { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopTimer();
        Cleanup();
    }

    // ─── Analog ──────────────────────────────────────────────────────

    private void CreateAnalog()
    {
        double w = Math.Max(200, _canvas.ActualWidth);
        double h = Math.Max(200, _canvas.ActualHeight);
        double cx = w / 2;
        double cy = h / 2;

        // Use the record-overlay radius when supplied (backglass = 0.45×dim),
        // otherwise fall back to the topper default (0.40×dim).
        double radius = _maxOrbitRadius > 0 ? _maxOrbitRadius : Math.Min(w, h) * 0.40;

        // Larger blobs so they're clearly visible at this scale
        double analogScale = AnalogSize switch
        {
            0 => 0.6,
            1 => 0.8,
            3 => 1.25,
            4 => 1.5,
            _ => 1.0,
        };
        double markerSize = Math.Max(14, radius * 0.16) * analogScale;
        double handSize = Math.Max(18, radius * 0.22) * analogScale;

        // 12 dim hour markers
        for (int i = 0; i < 12; i++)
        {
            double angle = i * 30.0 * Math.PI / 180.0 - Math.PI / 2;
            double mx = cx + Math.Cos(angle) * radius - markerSize / 2;
            double my = cy + Math.Sin(angle) * radius - markerSize / 2;

            var marker = CreateBlob(markerSize, _brightness * 0.50);
            Canvas.SetLeft(marker, mx);
            Canvas.SetTop(marker, my);
            _canvas.Children.Add(marker);
            _markerBlobs.Add(marker);
        }

        // Hour hand (inner ring — 65% radius)
        _hourBlob = CreateBlob(handSize * 1.3, _brightness);
        _canvas.Children.Add(_hourBlob);

        // Minute hand (halfway between hour and second — 82.5% radius)
        _minuteBlob = CreateBlob(handSize, _brightness);
        _canvas.Children.Add(_minuteBlob);

        // Second hand (marker ring — 100% radius)
        _secondBlob = CreateBlob(handSize * 0.8, _brightness);
        _canvas.Children.Add(_secondBlob);

        // Traditional style: add trail blobs sharing the parent hand's brush
        if (AnalogStyle == 1)
        {
            int hrIdx = _blobs.IndexOf(_hourBlob);
            for (int i = 0; i < 3; i++)
            {
                var trail = CreateTrailBlob(handSize * 1.0, _brightness * 0.5, hrIdx);
                _canvas.Children.Add(trail);
                _hourTrailBlobs.Add(trail);
            }

            int minIdx = _blobs.IndexOf(_minuteBlob);
            for (int i = 0; i < 5; i++)
            {
                var trail = CreateTrailBlob(handSize * 0.8, _brightness * 0.5, minIdx);
                _canvas.Children.Add(trail);
                _minuteTrailBlobs.Add(trail);
            }
        }

        UpdateAnalogPositions();
    }

    private void UpdateAnalogPositions()
    {
        if (_disposed) return;
        double w = Math.Max(200, _canvas.ActualWidth);
        double h = Math.Max(200, _canvas.ActualHeight);
        double cx = w / 2;
        double cy = h / 2;
        double radius = _maxOrbitRadius > 0 ? _maxOrbitRadius : Math.Min(w, h) * 0.40;

        var now = DateTime.Now;
        double totalSeconds = now.Second + now.Millisecond / 1000.0;
        double totalMinutes = now.Minute + totalSeconds / 60.0;
        double totalHours = (now.Hour % 12) + totalMinutes / 60.0;

        // Second — marker ring (100% of radius)
        if (_secondBlob != null)
        {
            double secAngle = totalSeconds / 60.0 * 2 * Math.PI - Math.PI / 2;
            double secR = radius + (AnalogSize >= 3 ? 8.0 : 0.0);
            Canvas.SetLeft(_secondBlob, cx + Math.Cos(secAngle) * secR - _secondBlob.Width / 2);
            Canvas.SetTop(_secondBlob, cy + Math.Sin(secAngle) * secR - _secondBlob.Height / 2);
        }

        // Minute — halfway between hour and second (82.5% of radius)
        double minR = radius * 0.825;
        if (_minuteBlob != null)
        {
            double minAngle = totalMinutes / 60.0 * 2 * Math.PI - Math.PI / 2;
            Canvas.SetLeft(_minuteBlob, cx + Math.Cos(minAngle) * minR - _minuteBlob.Width / 2);
            Canvas.SetTop(_minuteBlob, cy + Math.Sin(minAngle) * minR - _minuteBlob.Height / 2);
        }

        // Hour — inner ring (65% of radius)
        double hrR = radius * 0.65;
        if (_hourBlob != null)
        {
            double hrAngle = totalHours / 12.0 * 2 * Math.PI - Math.PI / 2;
            Canvas.SetLeft(_hourBlob, cx + Math.Cos(hrAngle) * hrR - _hourBlob.Width / 2);
            Canvas.SetTop(_hourBlob, cy + Math.Sin(hrAngle) * hrR - _hourBlob.Height / 2);
        }

        // Traditional trail blobs
        if (_hourTrailBlobs.Count > 0)
        {
            double hrAngle = totalHours / 12.0 * 2 * Math.PI - Math.PI / 2;
            double innerRing = radius * 0.15;
            int n = _hourTrailBlobs.Count;
            double hrEnd = hrR - 5.0;
            double step = (hrEnd - innerRing) / (n + 1);
            for (int i = 0; i < n; i++)
            {
                var t = _hourTrailBlobs[i];
                double r = innerRing + step * (i + 1);
                Canvas.SetLeft(t, cx + Math.Cos(hrAngle) * r - t.Width / 2);
                Canvas.SetTop(t, cy + Math.Sin(hrAngle) * r - t.Height / 2);
            }
        }

        if (_minuteTrailBlobs.Count > 0)
        {
            double minAngle = totalMinutes / 60.0 * 2 * Math.PI - Math.PI / 2;
            double innerRing = radius * 0.15;
            int n = _minuteTrailBlobs.Count;
            double minEnd = minR - 5.0;
            double step = (minEnd - innerRing) / (n + 1);
            for (int i = 0; i < n; i++)
            {
                var t = _minuteTrailBlobs[i];
                double r = innerRing + step * (i + 1);
                Canvas.SetLeft(t, cx + Math.Cos(minAngle) * r - t.Width / 2);
                Canvas.SetTop(t, cy + Math.Sin(minAngle) * r - t.Height / 2);
            }
        }

        }

    // ─── Digital (dot-matrix) ────────────────────────────────────────

    private void CreateDigital()
    {
        double w = Math.Max(200, _canvas.ActualWidth);
        double h = Math.Max(200, _canvas.ActualHeight);

        string timeStr = FormatTime(DateTime.Now);
        const int totalRows = 7;
        int totalCols = ComputeTotalCols(timeStr);
        double dotSpacing = Math.Min(w * 0.8 / totalCols, h * 0.5 / totalRows) * _digitalSizeMultiplier;
        double dotSize = dotSpacing * 0.75;
        dotSize = Math.Max(3, dotSize);

        double totalW = totalCols * dotSpacing;
        double totalH = totalRows * dotSpacing;
        double startX = (w - totalW) / 2;
        double startY = (h - totalH) / 2;

        _lastTimeString = timeStr;

        int col = 0;
        foreach (char ch in timeStr)
        {
            var glyph = GetGlyph(ch);
            int glyphCols = glyph.GetLength(1);
            int glyphRows = glyph.GetLength(0);

            for (int r = 0; r < glyphRows && r < totalRows; r++)
            {
                for (int c = 0; c < glyphCols; c++)
                {
                    double x = startX + (col + c) * dotSpacing;
                    double y = startY + r * dotSpacing;

                    bool on = glyph[r, c];
                    var dot = CreateBlob(dotSize, on ? _brightness : 0);
                    Canvas.SetLeft(dot, x);
                    Canvas.SetTop(dot, y);
                    _canvas.Children.Add(dot);
                    _dotBlobs.Add(dot);
                }
            }

            col += glyphCols + 2; // 2-col gap
        }
    }

    private static string FormatTime(DateTime now)
    {
        return Use24Hour
            ? now.ToString("HH:mm:ss")
            : now.ToString("hh:mm:ss tt", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int ComputeTotalCols(string s)
    {
        int total = 0;
        for (int i = 0; i < s.Length; i++)
        {
            int gc = GetGlyph(s[i]).GetLength(1);
            total += gc;
            if (i < s.Length - 1) total += 2; // gap
        }
        return total;
    }

    private void UpdateDigital()
    {
        if (_disposed) return;

        string timeStr = FormatTime(DateTime.Now);
        if (timeStr == _lastTimeString) return;
        _lastTimeString = timeStr;

        int dotIdx = 0;
        foreach (char ch in timeStr)
        {
            var glyph = GetGlyph(ch);
            int glyphRows = glyph.GetLength(0);
            int glyphCols = glyph.GetLength(1);

            for (int r = 0; r < glyphRows && r < 7; r++)
            {
                for (int c = 0; c < glyphCols; c++)
                {
                    if (dotIdx < _dotBlobs.Count)
                    {
                        bool on = glyph[r, c];
                        _dotBlobs[dotIdx].Opacity = on ? _brightness : 0;
                    }
                    dotIdx++;
                }
            }
        }
    }

    // ─── Timer ───────────────────────────────────────────────────────

    private void StartTimer()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _timer.Tick += (_, _) =>
        {
            if (_isDigital)
                UpdateDigital();
            else
                UpdateAnalogPositions();
        };
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    /// <summary>Creates a trail blob that shares the brush/gradient of an existing parent blob,
    /// so external color cycling updates both atomically.</summary>
    private Ellipse CreateTrailBlob(double size, double opacity, int parentBlobIndex)
    {
        var brush = _brushes[parentBlobIndex];
        var gradBrush = _gradBrushes[parentBlobIndex];

        // Add the same brush references so the lists stay in sync with _blobs
        _brushes.Add(brush);
        _gradBrushes.Add(gradBrush);

        var blob = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = gradBrush,
            Opacity = opacity,
            RenderTransformOrigin = new Point(0.5, 0.5),
            CacheMode = new BitmapCache(0.5),
        };

        _blobs.Add(blob);
        return blob;
    }

    private Ellipse CreateBlob(double size, double opacity)
    {
        var brush = new SolidColorBrush(Colors.Black);
        _brushes.Add(brush);

        var gradBrush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
            GradientStops = new GradientStopCollection
            {
                new(Color.FromArgb(255, 0, 0, 0), 0.0),
                new(Color.FromArgb(120, 0, 0, 0), 0.4),
                new(Color.FromArgb(0, 0, 0, 0), 1.0),
            }
        };
        _gradBrushes.Add(gradBrush);

        var blob = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = gradBrush,
            Opacity = opacity,
            RenderTransformOrigin = new Point(0.5, 0.5),
            CacheMode = new BitmapCache(0.5),
        };

        _blobs.Add(blob);
        return blob;
    }

    private void Cleanup()
    {
        foreach (var blob in _blobs)
        {
            blob.BeginAnimation(UIElement.OpacityProperty, null);
            blob.RenderTransform = null;
            _canvas.Children.Remove(blob);
        }
        _blobs.Clear();
        _brushes.Clear();
        _gradBrushes.Clear();
        _markerBlobs.Clear();
        _hourTrailBlobs.Clear();
        _minuteTrailBlobs.Clear();
        _dotBlobs.Clear();
        _secondBlob = null;
        _minuteBlob = null;
        _hourBlob = null;
    }

    // ─── 5×7 Dot-Matrix Glyphs ──────────────────────────────────────

    private static bool[,] GetGlyph(char ch) => ch switch
    {
        '0' => new bool[,]
        {
            { false, true,  true,  true,  false },
            { true,  false, false, false, true  },
            { true,  false, false, true,  true  },
            { true,  false, true,  false, true  },
            { true,  true,  false, false, true  },
            { true,  false, false, false, true  },
            { false, true,  true,  true,  false },
        },
        '1' => new bool[,]
        {
            { false, false, true,  false, false },
            { false, true,  true,  false, false },
            { false, false, true,  false, false },
            { false, false, true,  false, false },
            { false, false, true,  false, false },
            { false, false, true,  false, false },
            { false, true,  true,  true,  false },
        },
        '2' => new bool[,]
        {
            { false, true,  true,  true,  false },
            { true,  false, false, false, true  },
            { false, false, false, false, true  },
            { false, false, true,  true,  false },
            { false, true,  false, false, false },
            { true,  false, false, false, false },
            { true,  true,  true,  true,  true  },
        },
        '3' => new bool[,]
        {
            { false, true,  true,  true,  false },
            { true,  false, false, false, true  },
            { false, false, false, false, true  },
            { false, false, true,  true,  false },
            { false, false, false, false, true  },
            { true,  false, false, false, true  },
            { false, true,  true,  true,  false },
        },
        '4' => new bool[,]
        {
            { false, false, false, true,  false },
            { false, false, true,  true,  false },
            { false, true,  false, true,  false },
            { true,  false, false, true,  false },
            { true,  true,  true,  true,  true  },
            { false, false, false, true,  false },
            { false, false, false, true,  false },
        },
        '5' => new bool[,]
        {
            { true,  true,  true,  true,  true  },
            { true,  false, false, false, false },
            { true,  true,  true,  true,  false },
            { false, false, false, false, true  },
            { false, false, false, false, true  },
            { true,  false, false, false, true  },
            { false, true,  true,  true,  false },
        },
        '6' => new bool[,]
        {
            { false, true,  true,  true,  false },
            { true,  false, false, false, false },
            { true,  true,  true,  true,  false },
            { true,  false, false, false, true  },
            { true,  false, false, false, true  },
            { true,  false, false, false, true  },
            { false, true,  true,  true,  false },
        },
        '7' => new bool[,]
        {
            { true,  true,  true,  true,  true  },
            { false, false, false, false, true  },
            { false, false, false, true,  false },
            { false, false, true,  false, false },
            { false, false, true,  false, false },
            { false, false, true,  false, false },
            { false, false, true,  false, false },
        },
        '8' => new bool[,]
        {
            { false, true,  true,  true,  false },
            { true,  false, false, false, true  },
            { true,  false, false, false, true  },
            { false, true,  true,  true,  false },
            { true,  false, false, false, true  },
            { true,  false, false, false, true  },
            { false, true,  true,  true,  false },
        },
        '9' => new bool[,]
        {
            { false, true,  true,  true,  false },
            { true,  false, false, false, true  },
            { true,  false, false, false, true  },
            { false, true,  true,  true,  true  },
            { false, false, false, false, true  },
            { false, false, false, false, true  },
            { false, true,  true,  true,  false },
        },
        ':' => new bool[,]
        {
            { false },
            { false },
            { true  },
            { false },
            { true  },
            { false },
            { false },
        },
        ' ' => new bool[,]
        {
            { false, false, false },
            { false, false, false },
            { false, false, false },
            { false, false, false },
            { false, false, false },
            { false, false, false },
            { false, false, false },
        },
        'A' => new bool[,]
        {
            { false, true,  true,  true,  false },
            { true,  false, false, false, true  },
            { true,  false, false, false, true  },
            { true,  true,  true,  true,  true  },
            { true,  false, false, false, true  },
            { true,  false, false, false, true  },
            { true,  false, false, false, true  },
        },
        'P' => new bool[,]
        {
            { true,  true,  true,  true,  false },
            { true,  false, false, false, true  },
            { true,  false, false, false, true  },
            { true,  true,  true,  true,  false },
            { true,  false, false, false, false },
            { true,  false, false, false, false },
            { true,  false, false, false, false },
        },
        'M' => new bool[,]
        {
            { true,  false, false, false, true  },
            { true,  true,  false, true,  true  },
            { true,  false, true,  false, true  },
            { true,  false, false, false, true  },
            { true,  false, false, false, true  },
            { true,  false, false, false, true  },
            { true,  false, false, false, true  },
        },
        _ => new bool[,]
        {
            { false, false, false, false, false },
            { false, false, false, false, false },
            { false, false, false, false, false },
            { false, false, false, false, false },
            { false, false, false, false, false },
            { false, false, false, false, false },
            { false, false, false, false, false },
        },
    };
}
