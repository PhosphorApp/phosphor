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
/// Configuration passed to blob pattern constructors. Contains all the per-window
/// settings needed to create and animate blobs without patterns needing to know
/// about the window that hosts them.
/// </summary>
public sealed class BlobPatternConfig
{
    public required Canvas Canvas { get; init; }
    public required int BlobCount { get; init; }
    public required double Intensity { get; init; }
    public required double SpeedMultiplier { get; init; }
    public required Random Rng { get; init; }

    /// <summary>
    /// Factory that produces a blob size for this window. Called once per blob.
    /// Patterns that need a specific size (LightCycle) will override it internally.
    /// </summary>
    public required Func<Random, double> BlobSizeFactory { get; init; }

    /// <summary>
    /// Optional max orbit radius override (backglass uses record radius).
    /// If 0, patterns compute their own default.
    /// </summary>
    public double MaxOrbitRadius { get; init; }

    /// <summary>
    /// Whether to apply <see cref="BitmapCache"/> to each blob. Set to false when
    /// brush colors are mutated every frame (color cycling), because the cache is
    /// invalidated on every change and the repeated re-rasterization is more
    /// expensive than not caching at all.
    /// </summary>
    public bool UseBitmapCache { get; init; } = true;

    /// <summary>
    /// Size scale in 10% increments (1 to 20). 10 = default (100%), 1 = 10%, 20 = 200%.
    /// </summary>
    public int BlobSizeOffset { get; init; }
}

/// <summary>
/// Base class for all blob patterns. Provides blob creation, fly-in/fly-out
/// animation, and cleanup. Subclasses implement <see cref="StartMotion"/> and
/// <see cref="StopMotion"/> for their pattern-specific animation loop.
/// </summary>
public abstract class BlobPatternBase : IBlobPattern, IPausable
{
    protected readonly Canvas _canvas;
    protected readonly Random _rng;
    protected readonly double _intensity;
    protected readonly double _speedMultiplier;
    protected readonly int _blobCount;
    protected readonly Func<Random, double> _sizeFactory;
    protected readonly double _maxOrbitRadius;
    protected readonly bool _useBitmapCache;
    protected readonly double _sizeMultiplier;

    protected readonly List<FrameworkElement> _blobs = new();
    protected readonly List<SolidColorBrush> _brushes = new();
    protected readonly List<RadialGradientBrush> _gradBrushes = new();
    protected List<BlobState> _states = new();
    protected bool _disposed;

    public abstract BlobPattern PatternType { get; }

    /// <inheritdoc />
    public virtual bool ManagesOwnColors => false;

    /// <summary>
    /// Pulse visual elements matching the given dominant ROYGBIV color band.
    /// Default implementation does nothing. Patterns with color-coded elements
    /// (e.g. Matrix trails) can override for visual emphasis synchronised with DOF.
    /// </summary>
    public virtual void PulseDominantColor(RoygbivColor band) { }

    public IReadOnlyList<FrameworkElement> Blobs => _blobs;
    public IReadOnlyList<SolidColorBrush> Brushes => _brushes;
    public IReadOnlyList<RadialGradientBrush> GradientBrushes => _gradBrushes;

    /// <summary>The blob states owned by this pattern.</summary>
    public IReadOnlyList<BlobState> States => _states;

    protected BlobPatternBase(BlobPatternConfig config)
    {
        _canvas = config.Canvas;
        _rng = config.Rng;
        _intensity = config.Intensity;
        _speedMultiplier = config.SpeedMultiplier;
        _blobCount = config.BlobCount;
        _sizeFactory = config.BlobSizeFactory;
        _maxOrbitRadius = config.MaxOrbitRadius;
        _useBitmapCache = config.UseBitmapCache;
        _sizeMultiplier = Math.Clamp(config.BlobSizeOffset, 1, 20) / 10.0;
    }

    /// <summary>
    /// Called after blobs are in position to start the pattern's animation loop.
    /// </summary>
    protected abstract void StartMotion();

    /// <summary>
    /// Called before exit animation to stop the pattern's animation loop.
    /// </summary>
    protected abstract void StopMotion();

    // Cached easing function for audio reactive animations (avoids allocation per tick).
    // Frozen so it can be shared across threads (multiple windows on different dispatchers).
    protected static readonly QuadraticEase _reactiveEase = CreateFrozenEase();
    private static QuadraticEase CreateFrozenEase()
    {
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    /// <inheritdoc />
    public virtual void ApplyAudioReactive(AudioReactiveData data, double baseIntensity, double reactiveSpeedMs)
    {
        if (_disposed || _blobs.Count == 0) return;

        float intensity = Math.Clamp(data.Bass * 1.5f + (data.IsBeat ? 0.25f : 0f), 0f, 1f);

        double targetScale = 1.0 + data.Bass * 0.85;
        if (data.IsBeat) targetScale += 0.15;
        targetScale = Math.Min(targetScale, 1.8);

        // Lerp factor derived from reactiveSpeedMs so the smoothing adapts to
        // the user's setting. Higher reactiveSpeedMs → slower/smoother easing.
        // At 120 ms and ~16 ms tick interval this gives ~0.13 per tick — smooth
        // on 240 Hz monitors while still tracking beats tightly.
        double lerpFactor = Math.Clamp(16.0 / Math.Max(1.0, reactiveSpeedMs), 0.05, 1.0);

        for (int i = 0; i < _blobs.Count; i++)
        {
            var blob = _blobs[i];

            if (blob.RenderTransform is not ScaleTransform st)
            {
                st = new ScaleTransform(1.0, 1.0);
                blob.RenderTransform = st;
            }

            // Cancel any in-flight WPF animation once (from a previous code path).
            if (st.HasAnimatedProperties)
            {
                st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }

            // Exponential lerp toward target — the compositor interpolates the
            // DP value at the monitor's native refresh rate, so this reads smooth
            // even on 120/144/240 Hz displays without allocating DoubleAnimations.
            st.ScaleX += (targetScale - st.ScaleX) * lerpFactor;
            st.ScaleY += (targetScale - st.ScaleY) * lerpFactor;

            double blobBase = i < _states.Count && _states[i].BaseOpacity > 0
                ? _states[i].BaseOpacity
                : baseIntensity;
            blob.Opacity = blobBase + intensity * 0.21;
        }
    }

    /// <inheritdoc />
    public virtual void ResetAudioReactive(double baseIntensity)
    {
        if (_disposed || _blobs.Count == 0) return;

        for (int i = 0; i < _blobs.Count; i++)
        {
            var blob = _blobs[i];
            if (blob.RenderTransform is ScaleTransform st)
            {
                st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                st.ScaleX = 1.0;
                st.ScaleY = 1.0;
            }
            blob.Opacity = i < _states.Count && _states[i].BaseOpacity > 0
                ? _states[i].BaseOpacity
                : baseIntensity;
        }
    }

    /// <summary>
    /// Creates blobs and their states. Subclasses can override to customize sizing,
    /// opacity, gradients, or to add extra canvas elements (trails, grids, etc.).
    /// Base implementation creates standard gradient-filled ellipses.
    /// </summary>
    protected virtual void CreateBlobs()
    {
        double w = Math.Max(200, _canvas.ActualWidth);
        double h = Math.Max(200, _canvas.ActualHeight);

        _states = BlobMotion.CreateStates(_blobCount, PatternType, w, h, _rng,
            _maxOrbitRadius, speedMultiplier: _speedMultiplier);

        for (int i = 0; i < _blobCount; i++)
        {
            double size = _sizeFactory(_rng) * _sizeMultiplier;
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

            double opacity = _intensity + _rng.NextDouble() * 0.1;

            var blob = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = gradBrush,
                Opacity = opacity,
                RenderTransformOrigin = new Point(0.5, 0.5),
                CacheMode = _useBitmapCache ? new BitmapCache(0.5) : null,
            };

            if (i < _states.Count)
            {
                _states[i].BaseSize = size;
                _states[i].BaseOpacity = opacity;
            }

            _canvas.Children.Add(blob);
            _blobs.Add(blob);
        }
    }

    public virtual void Enter(Action onComplete)
    {
        if (_disposed) { onComplete(); return; }

        CreateBlobs();

        if (_blobs.Count == 0) { StartMotion(); onComplete(); return; }

        double w = _canvas.ActualWidth;
        double h = _canvas.ActualHeight;

        // Canvas not laid out yet — skip fly-in and motion until canvas is ready
        if (w <= 0 || h <= 0) { onComplete(); return; }
        double cx = w / 2;
        double cy = h / 2;
        double maxDim = Math.Max(w, h);

        int count = Math.Min(_blobs.Count, _states.Count);
        // Stagger per blob — shrink for large counts so fly-in stays under ~3 seconds total
        double staggerPerBlob = count > 1 ? Math.Min(0.03, 1.95 / (count - 1)) : 0;
        double maxStagger = (count - 1) * staggerPerBlob;
        const double durationSec = 0.8;
        double totalDuration = durationSec + maxStagger;

        var destinations = new (double x, double y)[count];

        for (int i = 0; i < count; i++)
        {
            var blob = _blobs[i];
            var state = _states[i];

            var (destX, destY) = BlobMotion.GetInitialPosition(
                state, PatternType, w, h, blob.Width, _rng);

            // Guard against NaN from degenerate positions
            if (double.IsNaN(destX)) destX = w / 2 - blob.Width / 2;
            if (double.IsNaN(destY)) destY = h / 2 - blob.Height / 2;

            destinations[i] = (destX, destY);

            // Start off-screen from opposite side of destination
            double angle = Math.Atan2(destY + blob.Height / 2 - cy, destX + blob.Width / 2 - cx);
            double startX = cx + Math.Cos(angle + Math.PI) * maxDim * 1.2 - blob.Width / 2;
            double startY = cy + Math.Sin(angle + Math.PI) * maxDim * 1.2 - blob.Height / 2;

            double savedOpacity = blob.Opacity;
            Canvas.SetLeft(blob, startX);
            Canvas.SetTop(blob, startY);
            blob.Opacity = 0.1;

            double stagger = i * staggerPerBlob;

            blob.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation
            {
                To = destX,
                Duration = TimeSpan.FromSeconds(durationSec),
                BeginTime = TimeSpan.FromSeconds(stagger),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
            blob.BeginAnimation(Canvas.TopProperty, new DoubleAnimation
            {
                To = destY,
                Duration = TimeSpan.FromSeconds(durationSec),
                BeginTime = TimeSpan.FromSeconds(stagger),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
            blob.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
            {
                To = savedOpacity,
                Duration = TimeSpan.FromSeconds(durationSec),
                BeginTime = TimeSpan.FromSeconds(stagger),
            });
        }

        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromSeconds(totalDuration + 0.05),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_disposed) { onComplete(); return; }

            for (int i = 0; i < count; i++)
            {
                var blob = _blobs[i];
                // Set base values before clearing animations so WPF doesn't
                // revert to the pre-animation start position for a frame.
                Canvas.SetLeft(blob, destinations[i].x);
                Canvas.SetTop(blob, destinations[i].y);
                if (i < _states.Count)
                    blob.Opacity = _states[i].BaseOpacity > 0 ? _states[i].BaseOpacity : _intensity;
                blob.BeginAnimation(Canvas.LeftProperty, null);
                blob.BeginAnimation(Canvas.TopProperty, null);
                blob.BeginAnimation(UIElement.OpacityProperty, null);
            }

            StartMotion();
            onComplete();
        };
        timer.Start();
    }

    public virtual void Exit(Action onComplete)
    {
        StopMotion();

        if (_blobs.Count == 0) { CleanupCanvas(); onComplete(); return; }

        double w = _canvas.ActualWidth;
        double h = _canvas.ActualHeight;

        // Canvas not laid out — skip fly-out, just clean up immediately
        if (w <= 0 || h <= 0) { CleanupCanvas(); onComplete(); return; }

        double cx = w / 2;
        double cy = h / 2;
        double maxDim = Math.Max(w, h);

        double maxStagger = (_blobs.Count - 1) * 0.03;
        const double durationSec = 0.8;
        double totalDuration = durationSec + maxStagger;

        for (int i = 0; i < _blobs.Count; i++)
        {
            var blob = _blobs[i];

            // Snapshot the current ANIMATED position before clearing animations.
            // Canvas.GetLeft returns the base value, not the animated value.
            // We must read the animated value via GetValue on the DependencyProperty.
            double curX = (double)blob.GetValue(Canvas.LeftProperty);
            double curY = (double)blob.GetValue(Canvas.TopProperty);
            if (double.IsNaN(curX)) curX = 0;
            if (double.IsNaN(curY)) curY = 0;

            // Now clear animations and set the base value to the snapshotted position
            blob.BeginAnimation(Canvas.LeftProperty, null);
            blob.BeginAnimation(Canvas.TopProperty, null);
            if (blob.RenderTransform is RotateTransform rt)
                rt.BeginAnimation(RotateTransform.AngleProperty, null);

            Canvas.SetLeft(blob, curX);
            Canvas.SetTop(blob, curY);

            double blobCx = curX + blob.Width / 2;
            double blobCy = curY + blob.Height / 2;

            // Fly outward from center
            double angle = Math.Atan2(blobCy - cy, blobCx - cx);
            double targetX = cx + Math.Cos(angle) * (maxDim * 1.2) - blob.Width / 2;
            double targetY = cy + Math.Sin(angle) * (maxDim * 1.2) - blob.Height / 2;

            double stagger = i * 0.03;

            blob.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation
            {
                To = targetX,
                Duration = TimeSpan.FromSeconds(durationSec),
                BeginTime = TimeSpan.FromSeconds(stagger),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            });
            blob.BeginAnimation(Canvas.TopProperty, new DoubleAnimation
            {
                To = targetY,
                Duration = TimeSpan.FromSeconds(durationSec),
                BeginTime = TimeSpan.FromSeconds(stagger),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            });
            blob.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
            {
                To = 0.0,
                Duration = TimeSpan.FromSeconds(durationSec),
                BeginTime = TimeSpan.FromSeconds(stagger),
            });
        }

        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromSeconds(totalDuration + 0.05),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CleanupCanvas();
            onComplete();
        };
        timer.Start();
    }

    /// <summary>
    /// Remove all blobs and pattern-specific elements from the canvas.
    /// Subclasses can override to remove extra elements (trail layers, etc.)
    /// but must call base.
    /// </summary>
    protected virtual void CleanupCanvas()
    {
        foreach (var blob in _blobs)
        {
            blob.BeginAnimation(Canvas.LeftProperty, null);
            blob.BeginAnimation(Canvas.TopProperty, null);
            blob.BeginAnimation(UIElement.OpacityProperty, null);
            blob.RenderTransform = null;
            _canvas.Children.Remove(blob);
        }
        _blobs.Clear();
        _brushes.Clear();
        _gradBrushes.Clear();
        _states.Clear();
        _canvas.Effect = null;
    }

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopMotion();
        CleanupCanvas();
    }

    // ── IPausable ──────────────────────────────────────────────────────
    // Default: suspend/resume the pattern's motion loop via StopMotion()/StartMotion(),
    // leaving all blobs and state on the canvas so Resume continues seamlessly. This is
    // correct for every continuous-loop pattern (Game of Life, ProjectM, Mandelbrot, Matrix,
    // Gravity, …) whose StopMotion detaches its render callback. Patterns with special needs
    // (e.g. freezing WPF storyboard clocks) may override Pause/Resume.
    protected bool _paused;
    public bool IsPaused => _paused;

    public virtual void Pause()
    {
        if (_paused || _disposed) return;
        _paused = true;
        StopMotion();
    }

    public virtual void Resume()
    {
        if (!_paused || _disposed) return;
        _paused = false;
        StartMotion();
    }
}
