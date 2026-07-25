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

    /// <summary>Zoom growth rate per second (default 5%). Set from <see cref="AppSettings.MatrixZoomRate"/>.</summary>
    public static double ZoomRate { get; set; } = 0.05;

    /// <summary>
    /// Maximum trail TextBlocks on the canvas to prevent GPU resource exhaustion.
    /// Tunable via <see cref="AppSettings.MatrixMaxTrails"/> for per-hardware tuning.
    /// Default 1500.
    /// </summary>
    public static int MaxTrails { get; set; } = 1500;

    /// <summary>
    /// When true, the per-layer <see cref="BlurEffect"/> is skipped at layer creation.
    /// Trades the soft halo for a significant GPU cost reduction on lower-end hardware.
    /// Tunable via <see cref="AppSettings.MatrixDisableBlur"/>. Default false.
    /// </summary>
    public static bool DisableBlur { get; set; }

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
        public BlurEffect? Blur;
        /// <summary>Count of trail elements parented to this layer's canvas.</summary>
        public int TrailCount;
        public int LeaderCount; // number of leaders currently on this layer
        /// <summary>Per-tick cached visible-bottom in layer-local coords; populated by OnTick.</summary>
        public double CachedVisBottom;
    }

    private readonly List<ZoomLayer> _layers = new();
    private ZoomLayer? _activeLayer;
    private bool _wasZooming; // tracks previous InfiniteZoom state for runtime toggle

    /// <summary>Seconds between dominant hue shifts (adjustable).</summary>
    private const double ColorCycleIntervalSeconds = 10.0;

    /// <summary>Current dominant hue used for trail colors and DOF band detection.</summary>
    private double _dominantHue;

    /// <summary>Accumulator for the colour cycle timer.</summary>
    private double _hueTimer;

    // Half-width Katakana U+FF66–U+FF9F and Arabic numerals U+0030–U+0039
    private static readonly char[] MatrixChars = BuildCharSet();
    // Pre-built single-character strings, parallel to MatrixChars, so per-tick
    // text changes don't allocate a new string from char.ToString().
    private static readonly string[] MatrixCharStrings = BuildCharStringSet();

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

    private static string[] BuildCharStringSet()
    {
        var arr = new string[MatrixChars.Length];
        for (int i = 0; i < MatrixChars.Length; i++)
            arr[i] = MatrixChars[i].ToString();
        return arr;
    }

    // Single shared FontFamily — constructing "Consolas" per element is surprisingly
    // expensive due to font-collection resolution and shows up under hot spawn churn.
    private static readonly FontFamily ConsolasFamily = new("Consolas");

    private DispatcherTimer? _timer;
    private readonly List<MatrixColumn> _columns = new();
    // Live trails. Index 0 is oldest (for capacity eviction); expired trails are
    // removed via swap-and-pop (O(1)) instead of List.RemoveAt(i) (O(n)).
    private readonly List<TrailChar> _trails = new();
    private double _canvasW, _canvasH;

    // Pre-frozen brushes for trail characters (avoids per-frame allocations).
    private static readonly SolidColorBrush WhiteBrush = CreateFrozen(Colors.White);
    private static readonly SolidColorBrush ClassicGreenBrush = CreateFrozen(Color.FromRgb(0, 255, 70));

    // Pools to avoid GC churn at the trail/brush spawn rate.
    // Trail TextBlocks: reset and re-parented instead of allocated each spawn.
    private readonly Stack<TextBlock> _trailElementPool = new();
    // Per-trail non-frozen brushes: needed so PulseDominantColor can animate
    // individual trails (the lightning-bolt stagger). Pool them so we don't
    // allocate ~1500 SolidColorBrush instances per refresh cycle.
    private readonly Stack<SolidColorBrush> _trailBrushPool = new();
    // Reusable buffer for PickNonOverlappingX — avoids allocating per respawn.
    private readonly List<double> _occupiedScratch = new();

    private static SolidColorBrush CreateFrozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
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
        public Color TrailColor;            // color locked at column spawn for all trails in this life
        public double StartDelay;           // seconds to wait before column begins falling
    }

    private sealed class TrailChar
    {
        public TextBlock Element = null!;
        public double Lifetime;     // seconds remaining
        public double MaxLifetime;
        public double CharChangeAccum;
        public ZoomLayer? Layer; // layer this trail lives on
        public MatrixColumn? Column; // column that spawned this trail (for column-wise pulse)
    }

    public MatrixBlobPattern(BlobPatternConfig config) : base(config) { }

    private ZoomLayer CreateZoomLayer()
    {
        var canvas = new Canvas
        {
            Width = _canvasW,
            Height = _canvasH,
        };
        BlurEffect? blur = null;
        if (!DisableBlur)
        {
            blur = new BlurEffect { Radius = 1.3 };
            canvas.Effect = blur;
        }
        var layer = new ZoomLayer
        {
            Canvas = canvas,
            Transform = new ScaleTransform(1.0, 1.0, _canvasW / 2, _canvasH / 2),
        };
        layer.Blur = blur;
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
        _wasZooming = InfiniteZoom;

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

            // Stagger initial starts over n seconds so columns cascade in
            col.StartDelay = _rng.NextDouble() * 3 + (i/10);
            col.Leader.Opacity = 0;

            if (i < _states.Count)
            {
                _states[i].BaseSize = fontSize;
                _states[i].BaseOpacity = 1.0;
            }
        }
    }

    private TextBlock CreateLeader(double fontSize)
    {
        // Consolas is monospace; pre-sizing the TextBlock lets WPF skip
        // measure/arrange on every per-tick Text change.
        double w = fontSize * 0.62;
        double h = fontSize * 1.25;
        var tb = new TextBlock
        {
            Text = RandomCharString(),
            FontSize = fontSize,
            FontFamily = ConsolasFamily,
            FontWeight = FontWeights.Bold,
            Foreground = WhiteBrush,
            Opacity = 1.0,
            Width = w,
            Height = h,
            TextAlignment = TextAlignment.Center,
            // No per-leader BlurEffect — the parent ZoomLayer Canvas already has one,
            // and stacking shader passes per element is expensive on GPU.
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
        col.Leader.Width = effectiveFontSize * 0.62;
        col.Leader.Height = effectiveFontSize * 1.25;

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

        col.Leader.Opacity = _intensity;
        col.Leader.Text = RandomCharString();
        // Lock the trail color for this column's entire life
        if (ColorCycling && col.Index < _brushes.Count)
            col.TrailColor = _brushes[col.Index].Color;
        else
            col.TrailColor = Color.FromRgb(0, 255, 70);
        Canvas.SetLeft(col.Leader, col.X);
        Canvas.SetTop(col.Leader, col.Y);
    }

    private double PickNonOverlappingX(MatrixColumn self, double left, double width)
    {
        double selfSize = self.Leader.FontSize;
        double selfScale = (InfiniteZoom && self.Layer != null) ? self.Layer.Scale : 1.0;
        double selfCenter = _canvasW / 2;
        double yThreshold = _canvasH * 0.25;
        double candidateScreenSize = selfSize * selfScale;

        // Collect screen-space X positions of active columns near the top
        var occupied = _occupiedScratch;
        occupied.Clear();
        foreach (var other in _columns)
        {
            if (other == self) continue;
            double otherScale = (InfiniteZoom && other.Layer != null) ? other.Layer.Scale : 1.0;
            double otherScreenY = (other.Y - _canvasH / 2) * otherScale + _canvasH / 2;
            if (otherScreenY > yThreshold) continue;
            double otherScreenX = (other.X - selfCenter) * otherScale + selfCenter;
            occupied.Add(otherScreenX);
        }

        // Divide canvas into bands sized roughly by font width so band count
        // adapts to the canvas dimensions and character size.
        int bandCount = Math.Max(4, (int)(width / Math.Max(8, candidateScreenSize)));
        double bandW = width / bandCount;
        double bestCandidate = left + _rng.NextDouble() * width;
        double bestScore = double.MinValue;
        // Shuffle band order so ties don't always favor the left
        var bandOrder = Enumerable.Range(0, bandCount).OrderBy(_ => _rng.Next()).ToList();
        foreach (int b in bandOrder)
        {
            double candidate = left + (b + _rng.NextDouble()) * bandW;
            double candidateScreen = (candidate - selfCenter) * selfScale + selfCenter;
            double minDist = double.MaxValue;
            foreach (double ox in occupied)
            {
                double d = Math.Abs(ox - candidateScreen);
                if (d < minDist) minDist = d;
            }
            if (occupied.Count == 0) return candidate;
            if (minDist >= candidateScreenSize) return candidate; // clear hit
            if (minDist > bestScore)
            {
                bestScore = minDist;
                bestCandidate = candidate;
            }
        }
        return bestCandidate;
    }

    private char RandomChar() => MatrixChars[_rng.Next(MatrixChars.Length)];
    private string RandomCharString() => MatrixCharStrings[_rng.Next(MatrixCharStrings.Length)];

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
            // If zoom was just enabled at runtime, restore transforms
            if (!_wasZooming)
            {
                foreach (var layer in _layers)
                {
                    layer.Scale = 1.0;
                    layer.Canvas.RenderTransform = layer.Transform;
                    layer.Transform.ScaleX = 1.0;
                    layer.Transform.ScaleY = 1.0;
                }
                _wasZooming = true;
            }

            foreach (var layer in _layers)
            {
                layer.Scale += layer.Scale * ZoomRate * dt;
                layer.Transform.ScaleX = layer.Scale;
                layer.Transform.ScaleY = layer.Scale;
                layer.Transform.CenterX = _canvasW / 2;
                layer.Transform.CenterY = _canvasH / 2;

                // Counter-scale blur so it doesn't magnify with zoom
                if (layer.Blur != null)
                    layer.Blur.Radius = 1.3 / layer.Scale;
            }

            // Rotate: when active layer exceeds threshold, create a new one
            if (_activeLayer != null && _activeLayer.Scale >= LayerRotateThreshold)
                _activeLayer = CreateZoomLayer();

            // Recycle empty layers (no leaders, no trails)
            for (int i = _layers.Count - 1; i >= 0; i--)
            {
                var layer = _layers[i];
                if (layer != _activeLayer && layer.LeaderCount == 0 && layer.TrailCount == 0)
                {
                    _canvas.Children.Remove(layer.Canvas);
                    _layers.RemoveAt(i);
                }
            }
        }
        else if (_wasZooming)
        {
            // Zoom was just disabled — reset transforms
            foreach (var layer in _layers)
            {
                layer.Scale = 1.0;
                layer.Canvas.RenderTransform = null;
            }
            _wasZooming = false;
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
                DebugLog.Log(LogLevel.Trace, "Matrix", $"Color cycle: dominantHue={_dominantHue:F0}° band={RoygbivHelper.FromHue(_dominantHue)}");
            }

            // Spread each blob's brush in a tight range around the dominant hue
            // so they all fall in the same ROYGBIV band for clear DOF detection.
            for (int i = 0; i < _brushes.Count; i++)
            {
                double hue = (_dominantHue + i * 3.0) % 360.0;
                _brushes[i].Color = ColorHelper.HsvToColor(hue, 0.9, 0.8);
            }
        }

        // Update columns (leaders)
        // Cache canvas-center math and per-layer visBottom (constant across columns this tick).
        double halfH = _canvasH * 0.5;
        // Use a small dictionary keyed by layer to avoid recomputing visBottom per column.
        // We only have 1–3 layers in practice, so a List<(layer, value)> would also work,
        // but reading from a per-layer field cached on ZoomLayer is even cheaper.
        foreach (var layer in _layers)
        {
            double ls = InfiniteZoom ? layer.Scale : 1.0;
            layer.CachedVisBottom = halfH + halfH / ls;
        }

        foreach (var col in _columns)
        {
            // Wait out initial stagger delay before the column begins falling
            if (col.StartDelay > 0)
            {
                col.StartDelay -= dt;
                if (col.StartDelay > 0)
                    continue;
                // Delay just expired — make leader visible
                col.Leader.Opacity = _intensity;
                col.StartDelay = 0;
            }

            double prevY = col.Y;
            col.Y += col.Speed * dt;
            Canvas.SetTop(col.Leader, col.Y);

            // Change leader character rapidly
            col.CharChangeAccum += dt;
            if (col.CharChangeAccum > 0.05)
            {
                col.CharChangeAccum = 0;
                col.Leader.Text = RandomCharString();
            }

            // Spawn trail character only after moving enough to avoid overlap
            double moved = col.Y - prevY;
            col.DistanceSinceTrail += moved;
            double curFontSize = col.Leader.FontSize;
            double spacing = curFontSize * 0.95;
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
                double visBottom = col.Layer?.CachedVisBottom ?? (halfH + halfH);
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
                ReleaseTrail(trail);
                // Swap-and-pop: O(1) removal at arbitrary index.
                int last = _trails.Count - 1;
                if (i != last) _trails[i] = _trails[last];
                _trails.RemoveAt(last);
                continue;
            }

            // Fade based on remaining lifetime, scaled by intensity setting
            double frac = Math.Clamp(trail.Lifetime / trail.MaxLifetime, 0, 1);
            trail.Element.Opacity = frac * _intensity;

            // Trail characters change infrequently
            trail.CharChangeAccum += dt;
            if (trail.CharChangeAccum > 1.5 + _rng.NextDouble() * 1.5)
            {
                trail.CharChangeAccum = 0;
                trail.Element.Text = RandomCharString();
            }
        }
    }

    /// <summary>
    /// Remove a trail from the canvas and return its element + brush to their pools.
    /// </summary>
    private void ReleaseTrail(TrailChar trail)
    {
        var tb = trail.Element;
        trail.Layer?.Canvas.Children.Remove(tb);
        if (trail.Layer != null) trail.Layer.TrailCount--;

        // Reclaim the brush if it's pool-eligible (non-frozen per-trail brush).
        // The classic-green frozen brush is shared and must not be pooled.
        if (tb.Foreground is SolidColorBrush sb && !sb.IsFrozen)
        {
            // Cancel any active animation so the brush returns to a clean state.
            sb.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _trailBrushPool.Push(sb);
        }
        // Detach brush before pooling the element so the next consumer assigns its own.
        tb.ClearValue(TextBlock.ForegroundProperty);
        _trailElementPool.Push(tb);
    }

    private void SpawnTrail(MatrixColumn col, double x, double y, double fontSize, int columnIndex)
    {
        // Evict approximately-oldest trail when at capacity. Swap-and-pop is used
        // in the per-tick expiration loop so strict FIFO order isn't preserved,
        // but eviction at capacity is rare (steady state), so this is acceptable.
        while (_trails.Count >= MaxTrails)
        {
            var oldest = _trails[0];
            ReleaseTrail(oldest);
            // Swap-and-pop: O(1) removal of head.
            int last = _trails.Count - 1;
            if (last > 0) _trails[0] = _trails[last];
            _trails.RemoveAt(last);
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

        // Use the column's locked trail color (set at spawn) for uniform columns.
        // A per-trail non-frozen SolidColorBrush is needed so PulseDominantColor can
        // animate trails independently (staggered/lingering flash). We pool these
        // brushes to avoid allocating ~1500 per refresh cycle. Frozen brushes
        // would prevent animation; sharing live brushes across trails would make
        // PulseDominantColor's per-trail stagger flash in unison (losing the
        // "lightning bolt travel" look).
        SolidColorBrush brush;
        if (ColorCycling)
        {
            if (_trailBrushPool.Count > 0)
            {
                brush = _trailBrushPool.Pop();
                brush.Color = col.TrailColor;
            }
            else
            {
                brush = new SolidColorBrush(col.TrailColor);
            }
        }
        else
        {
            brush = ClassicGreenBrush; // frozen; non-cycling trails never pulse
        }

        // Reuse a pooled TextBlock when available; otherwise allocate one.
        TextBlock tb;
        if (_trailElementPool.Count > 0)
        {
            tb = _trailElementPool.Pop();
            tb.Text = RandomCharString();
            tb.FontSize = fontSize;
            tb.Width = fontSize * 0.62;
            tb.Height = fontSize * 1.25;
            tb.Foreground = brush;
            tb.Opacity = _intensity;
        }
        else
        {
            tb = new TextBlock
            {
                Text = RandomCharString(),
                FontSize = fontSize,
                FontFamily = ConsolasFamily,
                FontWeight = FontWeights.Bold,
                Foreground = brush,
                Opacity = _intensity,
                Width = fontSize * 0.62,
                Height = fontSize * 1.25,
                TextAlignment = TextAlignment.Center,
                // No CacheMode: trail Text + Opacity change every tick, which invalidates
                // the bitmap cache each frame and is more expensive than not caching.
            };
        }

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
            Column = col,
        };
        layer.TrailCount++;
        _trails.Add(trail);
    }

    protected override void CleanupCanvas()
    {
        foreach (var trail in _trails)
            trail.Layer?.Canvas.Children.Remove(trail.Element);
        _trails.Clear();
        // Drop pooled trail elements/brushes — they're tied to the canvas instance.
        _trailElementPool.Clear();
        _trailBrushPool.Clear();
        foreach (var layer in _layers)
        {
            layer.TrailCount = 0;
            _canvas.Children.Remove(layer.Canvas);
        }
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

    public override void ResetAudioReactive(double baseIntensity)
    {
        if (_disposed || _columns.Count == 0) return;
        foreach (var col in _columns)
        {
            if (col.Leader.RenderTransform is ScaleTransform st)
            {
                st.ScaleX = 1.0;
                st.ScaleY = 1.0;
            }
        }
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

        DebugLog.Log(LogLevel.Trace, "Matrix", $"PulseDominantColor: band={band}, trails={_trails.Count}");

        const int flashMs = 100;
        const int settleMs = 1500;

        // Strategy: flash a SMALL NUMBER OF FULL COLUMNS rather than scattering
        // the flash across many columns. Flashing 100 trails spread across 30+
        // columns produces a visually weak "sparkle" of 2-3 chars per column;
        // flashing every trail in 2-3 columns produces a clear vertical lightning
        // bolt that reads instantly. Same render cost, far better signal.
        //
        // Also keeps render-thread pressure bounded: capped total trails pulsed
        // and staggered start times across a small window so WPF sees a smooth
        // ramp of new animation clocks instead of a single tick spike.
        const int maxColumns = 3;
        const int maxTrailsHardCap = 150; // safety net if columns are huge
        const int staggerMs = 120;

        // NOTE: no per-trail hue/band match check. The pulse fires only on
        // dominant-band changes, but at that moment all currently-live trails
        // are still painted in the *previous* band's color (they captured
        // their color at spawn). Filtering by current trail hue here would
        // reject every visible trail and the flash would be invisible.

        // Group visible animatable trails by column and rank columns by total
        // visible "weight" (sum of opacity) so we pick the most-on-screen ones.
        var columnGroups = _trails
            .Where(t => t.Column != null
                        && t.Element.Foreground is SolidColorBrush b
                        && !b.IsFrozen
                        && t.Element.Opacity > 0.1)
            .GroupBy(t => t.Column!)
            .Select(g => new { Column = g.Key, Trails = g.ToList(), Weight = g.Sum(x => x.Element.Opacity) })
            .OrderByDescending(g => g.Weight)
            .Take(maxColumns)
            .ToList();

        // Flatten to ordered trail list, top column first.
        var candidates = columnGroups.SelectMany(g => g.Trails).Take(maxTrailsHardCap).ToList();

        int pulsed = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            var trail = candidates[i];
            var brush = (SolidColorBrush)trail.Element.Foreground;
            var c = brush.Color;

            // Brighten toward white using the trail's actual current hue so the
            // peak color reads as a brightened version of what's on screen.
            double hue = ColorToHue(c.R, c.G, c.B);
            var bright = ColorHelper.HsvToColor(hue, 0.2, 1.0);

            double startOffset = candidates.Count > 1
                ? (double)i / (candidates.Count - 1) * staggerMs
                : 0;

            var flash = new ColorAnimationUsingKeyFrames
            {
                BeginTime = TimeSpan.FromMilliseconds(startOffset),
            };
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

        DebugLog.Log(LogLevel.Trace, "Matrix", $"PulseDominantColor: pulsed {pulsed} trails across {columnGroups.Count} columns (max {maxColumns} cols, staggered {staggerMs}ms)");
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
