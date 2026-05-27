using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace Phosphor;

/// <summary>
/// Matrix rain pattern: falling characters with green fading trails,
/// inspired by the iconic digital rain from "The Matrix".
/// </summary>
public sealed class MatrixBlobPattern : BlobPatternBase
{
    public override BlobPattern PatternType => BlobPattern.Matrix;
    public override bool ManagesOwnColors => ColorCycling;

    /// <summary>
    /// When true, each column's trail uses its per-blob cycling color.
    /// When false, all trails use classic green.
    /// Set from <see cref="AppSettings.MatrixColorCycling"/>.
    /// </summary>
    public static bool ColorCycling { get; set; } = true;

    /// <summary>
    /// When true, a continuously growing canvas scale creates an infinite
    /// zoom-in effect. New spawns are compensated so they appear normal-sized.
    /// Set from <see cref="AppSettings.MatrixInfiniteZoom"/>.
    /// </summary>
    public static bool InfiniteZoom { get; set; }

    /// <summary>Zoom growth rate per second (5% = gentle continuous zoom).</summary>
    private const double ZoomRate = 0.05;

    /// <summary>When the active layer's scale exceeds this, a new layer is promoted.</summary>
    private const double LayerRotateThreshold = 2.0;

    /// <summary>
    /// A Canvas child of the root canvas with its own ScaleTransform.
    /// Leaders and trails live on a layer; when the layer zooms past the
    /// threshold a new layer takes over for new spawns while old layers
    /// continue zooming until all their elements expire.
    /// </summary>
    private sealed class ZoomLayer
    {
        public Canvas Canvas = null!;
        public ScaleTransform Transform = null!;
        public double Scale = 1.0;
        public BlurEffect Blur = null!;
        public readonly List<TrailChar> Trails = new();
        public int LeaderCount; // number of leaders currently on this layer
    }

    private readonly List<ZoomLayer> _layers = new();
    private ZoomLayer? _activeLayer;

    /// <summary>Seconds between dominant hue shifts (adjustable).</summary>
    private const double ColorCycleIntervalSeconds = 10.0;

    /// <summary>Current dominant hue used for trail colors and DOF band detection.</summary>
    private double _dominantHue;

    /// <summary>Accumulator for the colour cycle timer.</summary>
    private double _hueTimer;

    // Half-width Katakana U+FF66–U+FF9F and Arabic numerals U+0030–U+0039
    private static readonly char[] MatrixChars = BuildCharSet();

    private static char[] BuildCharSet()
    {
        var chars = new char[(0xFF9F - 0xFF66 + 1) + 10];
        int idx = 0;
        for (int c = 0xFF66; c <= 0xFF9F; c++)
            chars[idx++] = (char)c;
        for (int c = '0'; c <= '9'; c++)
            chars[idx++] = (char)c;
        return chars;
    }

    private DispatcherTimer? _timer;
    private readonly List<MatrixColumn> _columns = new();
    private readonly List<TrailChar> _trails = new();
    private double _canvasW, _canvasH;

    // Pre-frozen brushes for trail characters (avoids per-frame allocations).
    private static readonly SolidColorBrush WhiteBrush = CreateFrozen(Colors.White);
    private static readonly SolidColorBrush TrailBrush = CreateFrozen(Color.FromRgb(0, 255, 70));
    private static readonly BitmapCache SharedTrailCache = CreateFrozenCache();

    /// <summary>Maximum trail TextBlocks on the canvas to prevent GPU resource exhaustion.</summary>
    private const int MaxTrails = 1500;

    private static SolidColorBrush CreateFrozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static BitmapCache CreateFrozenCache()
    {
        var c = new BitmapCache(1.0);
        c.Freeze();
        return c;
    }

    private sealed class MatrixColumn
    {
        public TextBlock Leader = null!;
        public double X;
        public double Y;
        public double Speed;        // pixels per second
        public double FontSize;
        public double FadeOutY;     // Y at which leader starts fading (double.MaxValue = reaches bottom)
        public double CharChangeAccum;
        public bool FadingOut;
        public double DistanceSinceTrail; // pixels moved since last trail char
        public int Index;                   // index into _brushes for trail color
        public ZoomLayer? Layer;            // layer this leader currently lives on
    }

    private sealed class TrailChar
    {
        public TextBlock Element = null!;
        public double Lifetime;     // seconds remaining
        public double MaxLifetime;
        public double CharChangeAccum;
        public ZoomLayer? Layer; // layer this trail lives on
    }

    public MatrixBlobPattern(BlobPatternConfig config) : base(config) { }

    private ZoomLayer CreateZoomLayer()
    {
        var layer = new ZoomLayer
        {
            Canvas = new Canvas
            {
                Width = _canvasW,
                Height = _canvasH,
                Effect = new BlurEffect { Radius = 1.3 },
            },
            Transform = new ScaleTransform(1.0, 1.0, _canvasW / 2, _canvasH / 2),
        };
        layer.Blur = (BlurEffect)layer.Canvas.Effect;
        layer.Canvas.RenderTransform = layer.Transform;
        // Insert new layers at the back (index 0) so older, more-zoomed
        // layers render in front — matching the "camera into the rain" depth.
        _canvas.Children.Insert(0, layer.Canvas);
        _layers.Add(layer);
        return layer;
    }

    protected override void CreateBlobs()
    {
        _canvasW = Math.Max(200, _canvas.ActualWidth);
        _canvasH = Math.Max(200, _canvas.ActualHeight);
        _dominantHue = 120.0; // start at classic Matrix green
        _hueTimer = 0;

        // When not zooming, we still use a single layer but without scaling.
        // When zooming, layers rotate as each one zooms past the threshold.
        _activeLayer = CreateZoomLayer();

        // If not zooming, remove the scale transform so it has no effect
        if (!InfiniteZoom)
            _activeLayer.Canvas.RenderTransform = null;

        for (int i = 0; i < _blobCount; i++)
        {
            double fontSize = _sizeFactory(_rng) * _sizeMultiplier * 0.15;
            fontSize = Math.Clamp(fontSize, 8, 40);

            var tb = CreateLeader(fontSize);
            _activeLayer.Canvas.Children.Add(tb);
            _blobs.Add(tb);

            var brush = new SolidColorBrush(Color.FromRgb(0, 255, 70));
            _brushes.Add(brush);
            _gradBrushes.Add(new RadialGradientBrush());

            var col = new MatrixColumn
            {
                Leader = tb,
                FontSize = fontSize,
                Index = i,
                Layer = _activeLayer,
            };
            _activeLayer.LeaderCount++;
            _columns.Add(col);
            SpawnColumn(col);

            if (i < _states.Count)
            {
                _states[i].BaseSize = fontSize;
                _states[i].BaseOpacity = 1.0;
            }
        }
    }

    private TextBlock CreateLeader(double fontSize)
    {
        var tb = new TextBlock
        {
            Text = RandomChar().ToString(),
            FontSize = fontSize,
            FontFamily = new FontFamily("Consolas"),
            FontWeight = FontWeights.Bold,
            Foreground = WhiteBrush,
            Opacity = 1.0,
            Effect = new BlurEffect { Radius = 2.5 },
        };
        System.Windows.Controls.Panel.SetZIndex(tb, 1000);
        return tb;
    }

    private void SpawnColumn(MatrixColumn col)
    {
        var layer = _activeLayer!;
        double s = InfiniteZoom ? layer.Scale : 1.0;
        double inv = 1.0 / s;

        // Move leader to the active layer if it's on a different one
        if (col.Layer != layer)
        {
            col.Layer?.Canvas.Children.Remove(col.Leader);
            if (col.Layer != null) col.Layer.LeaderCount--;
            layer.Canvas.Children.Add(col.Leader);
            layer.LeaderCount++;
            col.Layer = layer;
        }

        double effectiveFontSize = col.FontSize * inv;
        col.Leader.FontSize = effectiveFontSize;

        // Spawn within the visible rect of the active layer
        double vw = _canvasW * inv;
        double vh = _canvasH * inv;
        double vx = (_canvasW - vw) / 2;
        double vy = (_canvasH - vh) / 2;

        col.X = PickNonOverlappingX(col, vx, vw);
        col.Y = vy - effectiveFontSize;
        col.Speed = (106 + _rng.NextDouble() * 265) * Math.Max(0.1, _speedMultiplier) * inv;
        col.FadingOut = false;
        col.CharChangeAccum = 0;
        col.DistanceSinceTrail = 0;

        // Some columns fade out before reaching the bottom (30% chance)
        if (_rng.NextDouble() < 0.3)
            col.FadeOutY = vy + vh * (0.3 + _rng.NextDouble() * 0.5);
        else
            col.FadeOutY = double.MaxValue;

        col.Leader.Opacity = 1.0;
        col.Leader.Text = RandomChar().ToString();
        if (InfiniteZoom && col.Leader.Effect is BlurEffect leaderBlur)
            leaderBlur.Radius = 2.5 / s;
        Canvas.SetLeft(col.Leader, col.X);
        Canvas.SetTop(col.Leader, col.Y);
    }

    private double PickNonOverlappingX(MatrixColumn self, double left, double width)
    {
        // Try a few times to find an X that doesn't directly overlap another column
        for (int attempt = 0; attempt < 20; attempt++)
        {
            double candidate = left + _rng.NextDouble() * width;
            bool overlaps = false;
            foreach (var other in _columns)
            {
                if (other == self) continue;
                if (Math.Abs(other.X - candidate) < other.Leader.FontSize * 0.8)
                {
                    overlaps = true;
                    break;
                }
            }
            if (!overlaps) return candidate;
        }
        return left + _rng.NextDouble() * width;
    }

    private char RandomChar() => MatrixChars[_rng.Next(MatrixChars.Length)];

    private static Color HslToRgb(double h, double s, double l)
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

    protected override void StartMotion()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33), // ~30 fps
        };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    protected override void StopMotion()
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed) return;

        const double dt = 0.033;
        _canvasW = Math.Max(200, _canvas.ActualWidth);
        _canvasH = Math.Max(200, _canvas.ActualHeight);
        // Advance infinite zoom on all layers
        if (InfiniteZoom)
        {
            foreach (var layer in _layers)
            {
                layer.Scale += layer.Scale * ZoomRate * dt;
                layer.Transform.ScaleX = layer.Scale;
                layer.Transform.ScaleY = layer.Scale;
                layer.Transform.CenterX = _canvasW / 2;
                layer.Transform.CenterY = _canvasH / 2;

                // Counter-scale blur so it doesn't magnify with zoom
                layer.Blur.Radius = 1.3 / layer.Scale;
            }

            // Rotate: when active layer exceeds threshold, create a new one
            if (_activeLayer != null && _activeLayer.Scale >= LayerRotateThreshold)
                _activeLayer = CreateZoomLayer();

            // Recycle empty layers (no leaders, no trails)
            for (int i = _layers.Count - 1; i >= 0; i--)
            {
                var layer = _layers[i];
                if (layer != _activeLayer && layer.LeaderCount == 0 && layer.Trails.Count == 0)
                {
                    _canvas.Children.Remove(layer.Canvas);
                    _layers.RemoveAt(i);
                }
            }
        }

        // Dominant colour cycle: shift hue periodically and update _brushes
        // so the playfield's DOF colour-band detection picks up the new dominant.
        if (ColorCycling)
        {
            _hueTimer += dt;
            if (_hueTimer >= ColorCycleIntervalSeconds)
            {
                _hueTimer -= ColorCycleIntervalSeconds;
                _dominantHue = (_dominantHue + 60.0) % 360.0; // jump by 60° each cycle
                DebugLog.Log("Matrix", $"Color cycle: dominantHue={_dominantHue:F0}° band={RoygbivHelper.FromHue(_dominantHue)}");
            }

            // Spread each blob's brush in a tight range around the dominant hue
            // so they all fall in the same ROYGBIV band for clear DOF detection.
            for (int i = 0; i < _brushes.Count; i++)
            {
                double hue = (_dominantHue + i * 3.0) % 360.0;
                _brushes[i].Color = HslToRgb(hue, 0.8, 0.45);
            }
        }

        // Update columns (leaders)
        foreach (var col in _columns)
        {
            double prevY = col.Y;
            col.Y += col.Speed * dt;
            Canvas.SetTop(col.Leader, col.Y);

            // Change leader character rapidly
            col.CharChangeAccum += dt;
            if (col.CharChangeAccum > 0.05)
            {
                col.CharChangeAccum = 0;
                col.Leader.Text = RandomChar().ToString();
            }

            // Spawn trail character only after moving enough to avoid overlap
            double moved = col.Y - prevY;
            col.DistanceSinceTrail += moved;
            double curFontSize = col.Leader.FontSize;
            double spacing = curFontSize * 0.75;
            while (col.DistanceSinceTrail >= spacing && !col.FadingOut)
            {
                double trailY = col.Y - curFontSize * 0.3;
                SpawnTrail(col, col.X, trailY, curFontSize, col.Index);
                col.DistanceSinceTrail -= spacing;
            }

            // Handle fade-out before bottom
            if (col.Y >= col.FadeOutY && !col.FadingOut)
            {
                col.FadingOut = true;
            }

            if (col.FadingOut)
            {
                col.Leader.Opacity = Math.Max(0, col.Leader.Opacity - dt * 2.0);
                if (col.Leader.Opacity <= 0)
                {
                    SpawnColumn(col);
                }
            }
            else
            {
                // Visible bottom in layer-local coords: center + half-height/scale
                double ls = (InfiniteZoom && col.Layer != null) ? col.Layer.Scale : 1.0;
                double visBottom = _canvasH / 2 + (_canvasH / 2) / ls;
                if (col.Y > visBottom + col.Leader.FontSize)
                {
                    SpawnColumn(col);
                }
            }
        }

        // Update trail characters (across all layers)
        for (int i = _trails.Count - 1; i >= 0; i--)
        {
            var trail = _trails[i];
            trail.Lifetime -= dt;

            if (trail.Lifetime <= 0)
            {
                trail.Layer?.Canvas.Children.Remove(trail.Element);
                trail.Layer?.Trails.Remove(trail);
                _trails.RemoveAt(i);
                continue;
            }

            // Fade based on remaining lifetime
            double frac = Math.Clamp(trail.Lifetime / trail.MaxLifetime, 0, 1);
            trail.Element.Opacity = frac;

            // Trail characters change infrequently
            trail.CharChangeAccum += dt;
            if (trail.CharChangeAccum > 1.5 + _rng.NextDouble() * 1.5)
            {
                trail.CharChangeAccum = 0;
                trail.Element.Text = RandomChar().ToString();
            }
        }
    }

    private void SpawnTrail(MatrixColumn col, double x, double y, double fontSize, int columnIndex)
    {
        // Evict oldest trails if at capacity to prevent GPU resource exhaustion
        while (_trails.Count >= MaxTrails)
        {
            var oldest = _trails[0];
            oldest.Layer?.Canvas.Children.Remove(oldest.Element);
            oldest.Layer?.Trails.Remove(oldest);
            _trails.RemoveAt(0);
        }

        // Trail lifetime: 1/3 to 1/2 of time to traverse screen
        double screenTime = _canvasH / (120 * Math.Max(0.1, _speedMultiplier));
        double lifetime = screenTime * (0.33 + _rng.NextDouble() * 0.17);

        // Under pressure (pool > 75% full), shorten new trail lifetimes so the
        // fade reaches the tail instead of ending with an abrupt hard edge.
        double pressure = (double)_trails.Count / MaxTrails;
        if (pressure > 0.75)
        {
            double scale = 1.0 - (pressure - 0.75) * 3.0; // 1.0 at 75%, 0.25 at 100%
            lifetime *= Math.Max(0.25, scale);
        }

        // Use per-column cycling color when enabled, otherwise classic green
        SolidColorBrush brush;
        if (ColorCycling && columnIndex < _brushes.Count)
        {
            brush = new SolidColorBrush(_brushes[columnIndex].Color);
            // Not frozen — allows PulseDominantColor to animate the color
        }
        else
        {
            brush = new SolidColorBrush(Color.FromRgb(0, 255, 70));
        }

        var tb = new TextBlock
        {
            Text = RandomChar().ToString(),
            FontSize = fontSize,
            FontFamily = new FontFamily("Consolas"),
            FontWeight = FontWeights.Bold,
            Foreground = brush,
            Opacity = 1.0,
            CacheMode = SharedTrailCache,
        };

        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);

        // Add trail to the column's layer so it zooms with the layer it was born on
        var layer = col.Layer ?? _activeLayer!;
        layer.Canvas.Children.Add(tb);

        var trail = new TrailChar
        {
            Element = tb,
            Lifetime = lifetime,
            MaxLifetime = lifetime,
            CharChangeAccum = _rng.NextDouble() * 0.3,
            Layer = layer,
        };
        layer.Trails.Add(trail);
        _trails.Add(trail);
    }

    protected override void CleanupCanvas()
    {
        foreach (var trail in _trails)
            trail.Layer?.Canvas.Children.Remove(trail.Element);
        _trails.Clear();
        foreach (var layer in _layers)
            _canvas.Children.Remove(layer.Canvas);
        _layers.Clear();
        _activeLayer = null;
        base.CleanupCanvas();
    }

    public override void Enter(Action onComplete)
    {
        if (_disposed) { onComplete(); return; }

        CreateBlobs();

        if (_blobs.Count == 0) { StartMotion(); onComplete(); return; }

        double w = _canvas.ActualWidth;
        double h = _canvas.ActualHeight;
        if (w <= 0 || h <= 0) { onComplete(); return; }

        // No fly-in animation — columns start falling immediately
        StartMotion();
        onComplete();
    }

    public override void Exit(Action onComplete)
    {
        StopMotion();
        CleanupCanvas();
        onComplete();
    }

    public override void ApplyAudioReactive(AudioReactiveData data, double baseIntensity, double reactiveSpeedMs)
    {
        if (_disposed || _columns.Count == 0) return;

        if (data.IsBeat)
        {
            foreach (var col in _columns)
            {
                col.Speed *= 1.05;

                // Pulse leader scale on beat
                var leader = col.Leader;
                if (leader.RenderTransform is not ScaleTransform st)
                {
                    st = new ScaleTransform(1.0, 1.0);
                    leader.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
                    leader.RenderTransform = st;
                }
                st.ScaleX = 1.4;
                st.ScaleY = 1.4;
            }
        }
        else
        {
            // Decay scale back toward 1.0
            foreach (var col in _columns)
            {
                if (col.Leader.RenderTransform is ScaleTransform st)
                {
                    st.ScaleX = Math.Max(1.0, st.ScaleX - 0.03);
                    st.ScaleY = Math.Max(1.0, st.ScaleY - 0.03);
                }
            }
        }
    }

    /// <summary>
    /// Pulse trails whose foreground color matches the given dominant ROYGBIV band.
    /// Briefly brightens matching trails to white and fades back, creating a flash
    /// synchronised with DOF lighting. Uses Foreground color animation rather than
    /// opacity to avoid conflicting with the per-tick opacity fade.
    /// </summary>
    public override void PulseDominantColor(RoygbivColor band)
    {
        if (_disposed) return;

        DebugLog.Log("Matrix", $"PulseDominantColor: band={band}, trails={_trails.Count}");

        const int flashMs = 100;
        const int settleMs = 1500;

        int pulsed = 0;
        const int maxPulse = 400;

        foreach (var trail in _trails)
        {
            if (pulsed >= maxPulse) break;
            if (trail.Element.Foreground is not SolidColorBrush brush) continue;
            if (brush.IsFrozen) continue;

            var c = brush.Color;
            double hue = ColorToHue(c.R, c.G, c.B);
            if (RoygbivHelper.FromHue(hue) != band) continue;
            if (trail.Element.Opacity <= 0.1) continue;

            // Brighten the trail color (increase lightness toward white)
            var bright = HslToRgb(hue, 1.0, 0.85);

            var flash = new ColorAnimationUsingKeyFrames();
            flash.KeyFrames.Add(new EasingColorKeyFrame(bright,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(flashMs)),
                new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            flash.KeyFrames.Add(new EasingColorKeyFrame(c,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(settleMs)),
                new QuadraticEase { EasingMode = EasingMode.EaseInOut }));

            var b = brush;
            var orig = c;
            flash.Completed += (_, _) =>
            {
                b.BeginAnimation(SolidColorBrush.ColorProperty, null);
                b.Color = orig;
            };
            brush.BeginAnimation(SolidColorBrush.ColorProperty, flash);
            pulsed++;
        }

        DebugLog.Log("Matrix", $"PulseDominantColor: pulsed {pulsed} trails");
    }

    /// <summary>
    /// Converts RGB (0-255) to hue (0-360).
    /// </summary>
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
}
