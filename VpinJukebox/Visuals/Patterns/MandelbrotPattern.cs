using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VpinJukebox;

/// <summary>
/// Mandelbrot set visualization: continuously zooms into interesting boundary
/// regions with smooth palette rotation. Renders to a <see cref="WriteableBitmap"/>
/// on a background thread and displays it as a single <see cref="System.Windows.Controls.Image"/>
/// element in the canvas.
/// </summary>
public sealed class MandelbrotPattern : BlobPatternBase
{
    /// <summary>Hard ceiling for palette array size — must be ≥ any possible MaxIterations value.</summary>
    private const int PaletteCeiling = 8192;

    /// <summary>
    /// User-configurable max iteration count (64–8192, default 256).
    /// Set from <see cref="AppSettings.MandelbrotMaxIterations"/>.
    /// </summary>
    public static int MaxIterations { get; set; } = 256;

    /// <summary>
    /// Random perturbation radius applied to each zoom target coordinate.
    /// 0 = exact curated coordinate, 1.0 = maximum displacement (~0.01 in complex plane).
    /// Set from <see cref="AppSettings.MandelbrotPerturbation"/>.
    /// </summary>
    public static double Perturbation { get; set; }

    /// <summary>
    /// When true, algorithmically discovers novel boundary points at startup
    /// instead of (or in addition to) using the curated library.
    /// Set from <see cref="AppSettings.MandelbrotDiscovery"/>.
    /// </summary>
    public static bool Discovery { get; set; }

    /// <summary>
    /// Dimming overlay opacity (0.0 = no dimming, 1.0 = fully black).
    /// Set from <see cref="AppSettings.MandelbrotDimming"/>.
    /// </summary>
    public static double Dimming { get; set; }

    /// <summary>
    /// When true, applies per-frame histogram equalization on the CPU path so
    /// the full palette is always used regardless of how narrow the iteration
    /// range is in the visible frame. Currently CPU-only — the GPU path always
    /// uses cyclic banding. Default off (cyclic banding alone fixes most washout).
    /// </summary>
    public static bool HistogramColoring { get; set; }

    /// <summary>
    /// Number of escape iterations per full palette cycle. A full hue cycle
    /// every ~24 iterations gives the classic concentric "color bands" look
    /// without washing out at any zoom depth.
    /// </summary>
    /// <summary>
    /// One full palette cycle every N iterations. Smaller = more contrast on
    /// small iteration deltas (mini-brot halos pop), larger = smoother gradients.
    /// Must match the constant in <c>MandelbrotGpuRenderer</c>'s shader.
    /// </summary>
    private const double IterationsPerCycle = 12.0;

    /// <summary>How the rendered view is rotated relative to the screen axes.</summary>
    public enum RotationModeKind
    {
        /// <summary>No rotation — view is always axis-aligned (default).</summary>
        Off = 0,
        /// <summary>Pick a random angle once per zoom target.</summary>
        RandomPerTarget = 1,
        /// <summary>Continuously rotate the view at a slow, fixed rate.</summary>
        SlowSpin = 2,
    }

    /// <summary>
    /// View rotation mode. Rotation is baked into the iteration sampling itself
    /// (no post-rotated bitmap), so there are never any black corners regardless
    /// of angle. Set from <see cref="AppSettings.MandelbrotRotation"/>.
    /// </summary>
    public static RotationModeKind RotationMode { get; set; }

    /// <summary>Slow-spin rotation speed in radians per second.</summary>
    private const double SlowSpinRadPerSec = 0.05; // ~one full rotation every ~2 minutes

    /// <summary>Current view rotation angle in radians (applied to the sampling basis).</summary>
    private double _viewAngle;

    private WriteableBitmap? _bitmap;
    private System.Windows.Controls.Image? _image;
    private System.Windows.Shapes.Rectangle? _dimOverlay;
    private DispatcherTimer? _resizeDebounceTimer;
    private int _pixelWidth;
    private int _pixelHeight;

    // Iteration cache for palette-only re-coloring
    private double[]? _iterCache;         // smooth iteration per pixel (negative = inside set)
    private double _lastRenderZoom;       // zoom level when _iterCache was computed
    private double _lastRenderCenterRe;
    private double _lastRenderCenterIm;
    private int _lastRenderMaxIter;       // maxIter when _iterCache was computed

    // Zoom state
    private double _targetRe;           // fixed zoom destination
    private double _targetIm;
    private double _centerRe;            // actual render center (spirals around target)
    private double _centerIm;
    private double _zoom = 1.0;         // current magnification (higher = more zoomed)
    // Zoom speed is expressed as a continuous rate (fraction per second) and
    // integrated against frame dt so vsync jitter / GC pauses don't show up as
    // visible speed wobble. 0.15/sec ≈ the previous 0.0025/frame at 60 Hz.
    private double _baseZoomSpeed = 0.135; // fraction per second
    private double _zoomSpeed;
    private bool _interiorHeavy;        // true when the visible frame is almost entirely inside the set
    private double _boringBoost = 1.0;  // zoom-speed multiplier (smoothed each frame)
    private double _boringBoostTarget = 1.0; // raw target from MeasureBoringSparse
    private double _paletteOffset;

    // Spiral drift — subtle orbit around the zoom target
    private double _spiralAngle;
    private const double SpiralSpeed = 0.25;       // radians per second
    private const double SpiralBaseRadius = 0.02;   // radius in complex plane at zoom=1

    private const double BlurRadius = 0.75;  // very subtle softening; smooth coloring handles the rest

    // Boring-frame tracking — abandon the target if the visible region stays
    // within roughly one palette cycle (so it's effectively a single color)
    // or is mostly interior, for too long.
    private int _boringFrameCount;
    private const int MaxBoringFrames = 180; // ~3 seconds at 60fps before abandoning target
    private double _targetAgeSeconds;        // elapsed seconds since this target started
    private const double MinTargetAgeSeconds = 8.0; // don't abandon a target before this

    // Audio reactive modifiers (palette + brightness only — zoom speed is not
    // audio-reactive on purpose so the continuous zoom feels smooth).
    private double _audioPaletteBoost;
    private double _audioBrightnessBoost;

    // Rendering
    private readonly Stopwatch _stopwatch = new();
    private long _lastFrameTicks;
    private long _lastRenderTicks;
    private bool _rendering;
    private readonly byte[] _palette = new byte[PaletteCeiling * 4]; // BGRA per iteration
    private readonly long _minFrameIntervalTicks; // 0 = unlimited (vsync)

    // Perturbation theory: reference orbit computed at center using BigFloat,
    // then each pixel computes only its delta from the reference using double/float.
    // This allows zooming far beyond native double/float precision.
    private MandelbrotReferenceOrbit? _referenceOrbit;
    private double _lastOrbitZoom;       // zoom when the reference orbit was last computed
    private double _lastOrbitCenterRe;
    private double _lastOrbitCenterIm;
    private int _lastOrbitMaxIter;

    // Max zoom — with perturbation theory, both paths can go much deeper.
    // CPU: limited by BigFloat precision (128-bit mantissa → ~1e38).
    // GPU: limited by float delta precision, but deltas are small → ~1e13.
    private const double MaxZoomCpu = 1e30;
    private const double MaxZoomGpu = 1e13;

    public override BlobPattern PatternType => BlobPattern.Mandelbrot;

    public MandelbrotPattern(BlobPatternConfig config)
        : base(config)
    {
        // Pick a random zoom target
        var (re, im) = PickTarget(_rng);
        _targetRe = re;
        _targetIm = im;
        _centerRe = _targetRe;
        _centerIm = _targetIm;
        _spiralAngle = _rng.NextDouble() * Math.PI * 2;
        _viewAngle = PickInitialViewAngle(_rng);
        _zoomSpeed = _baseZoomSpeed;
        _minFrameIntervalTicks = MandelbrotMaxHz > 0
            ? Stopwatch.Frequency / MandelbrotMaxHz
            : 0;
        BuildPalette();
    }

    /// <summary>
    /// Maximum render frame rate for Mandelbrot (Hz). 0 = unlimited (follows monitor vsync).
    /// Set from <see cref="AppSettings.MandelbrotMaxHz"/>.
    /// </summary>
    public static int MandelbrotMaxHz { get; set; }

    /// <summary>
    /// Render resolution multiplier (0.2–1.0). Lower = fewer pixels = faster.
    /// Set from <see cref="AppSettings.MandelbrotRenderScale"/>.
    /// </summary>
    public static double RenderScale { get; set; } = 0.6;

    /// <summary>
    /// When true, iteration count scales with zoom depth — fewer iterations at low zoom.
    /// Set from <see cref="AppSettings.MandelbrotAdaptiveIterations"/>.
    /// </summary>
    public static bool AdaptiveIterations { get; set; } = true;

    /// <summary>
    /// Whether to use GPU-accelerated rendering (1 = GPU, 0 = CPU).
    /// Falls back to CPU automatically if GPU initialization fails.
    /// Set from <see cref="AppSettings.MandelbrotUseGpu"/>.
    /// </summary>
    public static bool UseGpu { get; set; }

    // GPU renderer — null when using CPU path
    private MandelbrotGpuRenderer? _gpuRenderer;

    protected override void CreateBlobs()
    {
        double w = Math.Max(200, _canvas.ActualWidth);
        double h = Math.Max(200, _canvas.ActualHeight);

        _pixelWidth = Math.Max(1, (int)(w * RenderScale));
        _pixelHeight = Math.Max(1, (int)(h * RenderScale));

        // Try GPU path if enabled
        ImageSource imageSource;
        if (UseGpu)
        {
            _gpuRenderer = new MandelbrotGpuRenderer();
            if (_gpuRenderer.Initialize(_pixelWidth, _pixelHeight) && _gpuRenderer.ImageSource != null)
            {
                imageSource = _gpuRenderer.ImageSource;
                LogGpu($"GPU renderer initialized successfully ({_pixelWidth}x{_pixelHeight})");
            }
            else
            {
                // GPU init failed — fall back to CPU
                LogGpu($"GPU init FAILED — falling back to CPU. Init result: {_gpuRenderer.IsAvailable}, ImageSource: {_gpuRenderer.ImageSource != null}");
                _gpuRenderer.Dispose();
                _gpuRenderer = null;
                _bitmap = new WriteableBitmap(_pixelWidth, _pixelHeight, 96, 96, PixelFormats.Bgra32, null);
                imageSource = _bitmap;
            }
        }
        else
        {
            LogGpu("Using CPU renderer (MandelbrotUseGpu=0)");
            _bitmap = new WriteableBitmap(_pixelWidth, _pixelHeight, 96, 96, PixelFormats.Bgra32, null);
            imageSource = _bitmap;
        }

        _image = new System.Windows.Controls.Image
        {
            Width = w,
            Height = h,
            Source = imageSource,
            Stretch = Stretch.Fill,
            Opacity = 0, // Start invisible for fade-in
            Effect = new System.Windows.Media.Effects.BlurEffect
            {
                Radius = BlurRadius,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
            },
            };

        // Add a dummy brush/gradBrush so color cycling code doesn't crash on index access
        _brushes.Add(new SolidColorBrush(Colors.Black));
        _gradBrushes.Add(new RadialGradientBrush());

        _canvas.Children.Add(_image);
        _blobs.Add(_image);

        // Dimming overlay — a semi-transparent black layer on top
        if (Dimming > 0.001)
        {
            _dimOverlay = new System.Windows.Shapes.Rectangle
            {
                Width = w,
                Height = h,
                Fill = new SolidColorBrush(Colors.Black),
                Opacity = Math.Clamp(Dimming, 0.0, 0.9),
                IsHitTestVisible = false,
            };
            _canvas.Children.Add(_dimOverlay);
        }

        // Subscribe to canvas resize so we can rebuild the bitmap/GPU resources.
        _canvas.SizeChanged += OnCanvasSizeChanged;
    }

    /// <summary>
    /// Debounced canvas resize handler — rebuilds the render bitmap (CPU or GPU)
    /// at the new canvas size while preserving zoom state and reference orbit.
    /// </summary>
    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_disposed) return;

        // Update the visible Image element and dim overlay immediately so they
        // stretch to the new size; rebuild the (expensive) backing bitmap on a
        // short debounce so a drag-resize doesn't recreate D3D resources every
        // pixel of motion.
        double w = _canvas.ActualWidth;
        double h = _canvas.ActualHeight;
        if (_image != null)
        {
            _image.Width = w;
            _image.Height = h;
        }
        if (_dimOverlay != null)
        {
            _dimOverlay.Width = w;
            _dimOverlay.Height = h;
        }

        if (_resizeDebounceTimer == null)
        {
            _resizeDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(150),
            };
            _resizeDebounceTimer.Tick += (_, _) =>
            {
                _resizeDebounceTimer!.Stop();
                if (_disposed) return;
                RebuildRenderBuffers();
            };
        }
        _resizeDebounceTimer.Stop();
        _resizeDebounceTimer.Start();
    }

    /// <summary>
    /// Rebuilds the CPU bitmap or GPU renderer at the current canvas size.
    /// Preserves zoom/center/palette state so the visual continues seamlessly.
    /// </summary>
    private void RebuildRenderBuffers()
    {
        if (_disposed || _image == null) return;

        double w = Math.Max(200, _canvas.ActualWidth);
        double h = Math.Max(200, _canvas.ActualHeight);
        int newW = Math.Max(1, (int)(w * RenderScale));
        int newH = Math.Max(1, (int)(h * RenderScale));
        if (newW == _pixelWidth && newH == _pixelHeight) return;

        // Ignore tiny size changes (<3% in both dimensions). The Image element
        // uses Stretch.Fill, so a small mismatch between bitmap resolution and
        // canvas size is visually invisible — but rebuilding the GPU pipeline
        // costs ~100ms and produces a one-frame flash. WPF's startup layout
        // sometimes settles in two stages (e.g. 526→534, +1.5%) due to DPI
        // snap-to-pixel; ignoring those keeps startup smooth and doesn't hurt
        // legitimate user drag-resizes (which always exceed 3%).
        double dW = Math.Abs(newW - _pixelWidth) / (double)Math.Max(1, _pixelWidth);
        double dH = Math.Abs(newH - _pixelHeight) / (double)Math.Max(1, _pixelHeight);
        if (dW < 0.03 && dH < 0.03) return;

        _pixelWidth = newW;
        _pixelHeight = newH;

        // Iteration cache size depends on pixel count — invalidate so it gets resized.
        _iterCache = null;
        _lastRenderZoom = 0;

        if (_gpuRenderer != null)
        {
            // Tear down and recreate the GPU pipeline at the new size.
            _gpuRenderer.Dispose();
            _gpuRenderer = new MandelbrotGpuRenderer();
            if (_gpuRenderer.Initialize(_pixelWidth, _pixelHeight) && _gpuRenderer.ImageSource != null)
            {
                _image.Source = _gpuRenderer.ImageSource;
                LogGpu($"GPU renderer resized to {_pixelWidth}x{_pixelHeight}");
            }
            else
            {
                LogGpu($"GPU resize FAILED — falling back to CPU at {_pixelWidth}x{_pixelHeight}");
                _gpuRenderer.Dispose();
                _gpuRenderer = null;
                _bitmap = new WriteableBitmap(_pixelWidth, _pixelHeight, 96, 96, PixelFormats.Bgra32, null);
                _image.Source = _bitmap;
            }
        }
        else
        {
            _bitmap = new WriteableBitmap(_pixelWidth, _pixelHeight, 96, 96, PixelFormats.Bgra32, null);
            _image.Source = _bitmap;
        }

        // Render an immediate frame at the new size so the user sees the new
        // resolution right away, even if motion was paused.
        if (_rendering)
            RenderFrame(0);
    }

    public override void Enter(Action onComplete)
    {
        if (_disposed) { onComplete(); return; }

        // If the canvas hasn't been laid out yet (zero size), defer until it is.
        // This avoids initializing GPU/bitmap resources at a placeholder size and
        // then having to tear them down a moment later when WPF's startup layout
        // pass settles on the real size — which previously caused a one-frame
        // flash followed by a black gap during app startup.
        if (!_canvas.IsLoaded || _canvas.ActualWidth <= 0 || _canvas.ActualHeight <= 0)
        {
            void DeferredEnter(object? s, RoutedEventArgs e)
            {
                _canvas.Loaded -= DeferredEnter;
                if (_disposed) { onComplete(); return; }
                // Use BeginInvoke so the canvas has a chance to finish its first
                // measure/arrange pass before we sample ActualWidth/ActualHeight.
                _canvas.Dispatcher.BeginInvoke(new Action(() => Enter(onComplete)),
                    DispatcherPriority.Loaded);
            }
            _canvas.Loaded += DeferredEnter;
            return;
        }

        CreateBlobs();

        if (_image == null) { onComplete(); return; }

        double w = _canvas.ActualWidth;
        double h = _canvas.ActualHeight;
        if (w <= 0 || h <= 0) { onComplete(); return; }

        // Render the first frame immediately so the fade-in isn't blank
        RenderFrame(0);

        // Soft intro blur
        AnimateIntroBlur();

        // Fade in
        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1.0,
            Duration = TimeSpan.FromSeconds(1.0),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        fadeIn.Completed += (_, _) =>
        {
            if (_disposed) { onComplete(); return; }
            _image.BeginAnimation(UIElement.OpacityProperty, null);
            _image.Opacity = 1.0;
            StartMotion();
            onComplete();
        };
        _image.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    /// <summary>
    /// Applies a quick blur-to-sharp transition on the image to soften hard cuts.
    /// Animates the existing <see cref="BlurEffect"/> on the image from a high
    /// radius down to the baseline <see cref="BlurRadius"/>.
    /// </summary>
    private void AnimateIntroBlur()
    {
        if (_image?.Effect is not BlurEffect blur) return;
        // Stop any in-progress blur animation so the new one starts cleanly.
        blur.BeginAnimation(BlurEffect.RadiusProperty, null);
        var anim = new DoubleAnimation
        {
            From = 50.0,
            To = BlurRadius,
            Duration = TimeSpan.FromSeconds(2.5),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Completed += (_, _) =>
        {
            // Remove the animation so the property returns to its base value.
            blur.BeginAnimation(BlurEffect.RadiusProperty, null);
            blur.Radius = BlurRadius;
        };
        blur.BeginAnimation(BlurEffect.RadiusProperty, anim);
    }

    public override void Exit(Action onComplete)
    {
        StopMotion();

        if (_image == null || _blobs.Count == 0)
        {
            CleanupCanvas();
            onComplete();
            return;
        }

        double w = _canvas.ActualWidth;
        double h = _canvas.ActualHeight;
        if (w <= 0 || h <= 0) { CleanupCanvas(); onComplete(); return; }

        var fadeOut = new DoubleAnimation
        {
            To = 0.0,
            Duration = TimeSpan.FromSeconds(0.8),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fadeOut.Completed += (_, _) =>
        {
            CleanupCanvas();
            onComplete();
        };
        _image.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    protected override void StartMotion()
    {
        _stopwatch.Restart();
        _lastFrameTicks = _stopwatch.ElapsedTicks;
        _rendering = true;
        CompositionTarget.Rendering += OnRendering;
    }

    protected override void StopMotion()
    {
        _rendering = false;
        CompositionTarget.Rendering -= OnRendering;
        _stopwatch.Stop();
    }

    public override void ApplyAudioReactive(AudioReactiveData data, double baseIntensity, double reactiveSpeedMs)
    {
        if (_disposed) return;

        // Zoom speed is intentionally NOT audio-reactive — bass/beat surges made
        // the motion feel jerky and broke the smooth continuous-zoom illusion.
        // Palette rotation and brightness pulses still react to audio below.

        // Treble drives palette rotation speed
        _audioPaletteBoost = data.Treble * 3.0;

        // Bass drives a brightness pulse
        _audioBrightnessBoost = data.Bass * 0.3 + (data.IsBeat ? 0.15 : 0);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_rendering || (_bitmap == null && _gpuRenderer == null)) return;

        long nowTicks = _stopwatch.ElapsedTicks;

        // Frame rate limiter — skip render if we haven't reached the minimum interval
        if (_minFrameIntervalTicks > 0 && (nowTicks - _lastRenderTicks) < _minFrameIntervalTicks)
            return;
        _lastRenderTicks = nowTicks;

        double dt = Math.Min((double)(nowTicks - _lastFrameTicks) / Stopwatch.Frequency, 0.05);
        _lastFrameTicks = nowTicks;
        if (dt <= 0) return;

        // Advance zoom — boost speed when the visible frame is near-uniform so
        // we rush through "color deserts" instead of dwelling on them.
        // (Audio reactivity is intentionally NOT applied here so motion stays smooth.)
        // _baseZoomSpeed is per-second; multiply by dt and exponentiate so the
        // motion is frame-rate independent and immune to vsync jitter.
        // Smoothly interpolate _boringBoost toward its target so zoom
        // speed changes are gradual and never cause visible hiccups.
        // During the initial blackout period, force boost to 1.0 so a new
        // target always starts at uniform speed before boring-detection kicks in.
        double boostTarget = _targetAgeSeconds < MinTargetAgeSeconds ? 1.0 : _boringBoostTarget;
        double lerpRate = 1.0 - Math.Exp(-2.0 * dt); // ~0.5 s time constant
        _boringBoost += (boostTarget - _boringBoost) * lerpRate;
        _zoomSpeed = _baseZoomSpeed * Math.Max(0.1, _speedMultiplier) * _boringBoost;
        _zoom *= Math.Exp(_zoomSpeed * dt);

        // Keep center locked on the zoom target (no spiral drift)
        _centerRe = _targetRe;
        _centerIm = _targetIm;

        // Track sustained boring/interior frames — if we've been stuck in a
        // "color desert" too long, abandon the target.
        _targetAgeSeconds += dt;
        if (_targetAgeSeconds >= MinTargetAgeSeconds && (_interiorHeavy || _boringBoost > 4.0))
            _boringFrameCount++;
        else
            _boringFrameCount = 0;

        double maxZoom = (_gpuRenderer != null && _gpuRenderer.IsAvailable) ? MaxZoomGpu : MaxZoomCpu;
        bool shouldReset = _zoom > maxZoom || _boringFrameCount > MaxBoringFrames;

        if (shouldReset)
        {
            string reason = _zoom > maxZoom
                ? $"max zoom reached ({_zoom:E2} > {maxZoom:E2})"
                : $"boring frames ({_boringFrameCount} consecutive, boost={_boringBoost:F1}, interior={_interiorHeavy})";
            _zoom = 1.0;
            _boringFrameCount = 0;
            _boringBoost = 1.0;
            _boringBoostTarget = 1.0;
            _interiorHeavy = false;
            _targetAgeSeconds = 0;
            _referenceOrbit = null; // force recomputation for new target
            var (re, im) = PickTarget(_rng);
            _targetRe = re;
            _targetIm = im;
            DebugLog.Log($"Mandelbrot: switching to ({re:G10}, {im:G10}) because {reason}");
            _centerRe = _targetRe;
            _centerIm = _targetIm;
            _spiralAngle = _rng.NextDouble() * Math.PI * 2;
            _viewAngle = PickInitialViewAngle(_rng);
            AnimateIntroBlur();
        }

        // Advance the view rotation. SlowSpin rotates continuously; the other
        // modes leave the angle alone (it was set in the ctor / on target reset).
        if (RotationMode == RotationModeKind.SlowSpin)
        {
            _viewAngle += SlowSpinRadPerSec * dt;
            // Keep within [-2pi, 2pi] to avoid growing unbounded.
            if (_viewAngle > Math.PI * 2) _viewAngle -= Math.PI * 2;
            else if (_viewAngle < -Math.PI * 2) _viewAngle += Math.PI * 2;
        }

        // Advance palette rotation. Units are palette-index entries per second.
        // PaletteCeiling / cycleSeconds → one full color rotation every cycleSeconds.
        // Treble can speed this up.
        const double basePaletteCycleSeconds = 12.0;
        double palettePixelsPerSecond = (PaletteCeiling / basePaletteCycleSeconds)
                                        * (1.0 + _audioPaletteBoost);
        _paletteOffset += palettePixelsPerSecond * dt;
        // The palette itself never changes — rotation is applied at coloring time.

        RenderFrame(dt);
    }

    private void RenderFrame(double dt)
    {
        if (_gpuRenderer != null && _gpuRenderer.IsAvailable)
        {
            GpuRenderFrame(dt);
            return;
        }

        CpuRenderFrame(dt);
    }

    private void GpuRenderFrame(double dt)
    {
        int maxIter = GetEffectiveMaxIterations();
        double brightBoost = 1.0 + _audioBrightnessBoost;

        // Compute/update reference orbit for perturbation theory
        EnsureReferenceOrbit(maxIter);

        // Update palette on GPU
        _gpuRenderer!.UpdatePalette(_palette, PaletteCeiling);

        // Upload reference orbit to GPU
        if (_referenceOrbit != null)
        {
            var orbitData = _referenceOrbit.ToInterleavedFloats();
            _gpuRenderer.UpdateReferenceOrbit(orbitData, _referenceOrbit.Length);
        }

        // Render via shader — pass scale/aspect instead of absolute center
        // The shader uses delta iteration from the reference orbit.
        // Palette offset is normalized into [0,1) cycles for the shader.
        double normalizedOffset = _paletteOffset / PaletteCeiling;
        normalizedOffset -= Math.Floor(normalizedOffset);
        _gpuRenderer.RenderFrame(
            _centerRe, _centerIm, _zoom,
            maxIter, normalizedOffset, brightBoost,
            _viewAngle);

        // Detect boring frames on the GPU path by sampling a few points on CPU.
        var (boost, interior) = MeasureBoringSparse(_centerRe, _centerIm, _zoom, _pixelWidth, _pixelHeight, maxIter);
        _boringBoostTarget = boost;
        _interiorHeavy = interior;
    }

    /// <summary>
    /// Lightweight boring-frame detection for the GPU path. Returns a zoom-speed
    /// multiplier (1.0 = normal, up to 8.0 = rush through) and an "interior"
    /// flag that's true when the visible region is mostly inside the set.
    /// Thresholds are scaled to <see cref="IterationsPerCycle"/> — a "boring"
    /// frame is one whose iteration spread covers less than one palette cycle.
    /// </summary>
    private static (double Boost, bool Interior) MeasureBoringSparse(double centerRe, double centerIm, double zoom, int w, int h, int maxIter)
    {
        const int gridSize = 8;
        double scale = 3.0 / zoom;
        double aspect = (double)w / h;
        double reMin = centerRe - scale * aspect * 0.5;
        double imMin = centerIm - scale * 0.5;
        double reStep = scale * aspect / (gridSize - 1);
        double imStep = scale / (gridSize - 1);

        double min = double.MaxValue;
        double max = double.MinValue;
        int insideCount = 0;
        int sampleCount = 0;

        for (int gy = 0; gy < gridSize; gy++)
        {
            double ci = imMin + gy * imStep;
            for (int gx = 0; gx < gridSize; gx++)
            {
                double cr = reMin + gx * reStep;
                double v = ComputeIteration(cr, ci, maxIter);
                sampleCount++;
                if (v < 0)
                    insideCount++;
                else
                {
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }
        }

        if (sampleCount == 0) return (1.0, false);
        double insideRatio = (double)insideCount / sampleCount;
        if (insideRatio > 0.95) return (8.0, true);

        int exterior = sampleCount - insideCount;
        if (exterior < 4) return (8.0, true); // not enough escapes to judge

        return (BoostFromSpread(max - min), false);
    }

    /// <summary>
    /// Maps an iteration-spread to a zoom-speed multiplier. Calibrated so that a
    /// spread less than one palette cycle (which would render as a single color
    /// band) drives the boost high; larger spreads taper back to 1.0.
    /// </summary>
    private static double BoostFromSpread(double spread)
    {
        // Continuous curve: boost tapers smoothly from 8× (no detail) to 1×
        // (≥3 palette cycles of detail). This avoids discrete jumps that
        // caused visible zoom speed hiccups at threshold boundaries.
        double cycles = spread / IterationsPerCycle;
        if (cycles >= 3.0) return 1.0;
        if (cycles <= 0.0) return 8.0;
        // Smooth hermite interpolation from 8.0 at 0 cycles to 1.0 at 3 cycles.
        double t = cycles / 3.0; // 0..1
        double smooth = t * t * (3.0 - 2.0 * t); // smoothstep
        return 8.0 + (1.0 - 8.0) * smooth; // 8 → 1
    }

    /// <summary>
    /// Get the effective max iteration count, scaling with zoom when adaptive mode is on.
    /// At zoom=1 we use half of MaxIterations; at MaxZoom we use MaxIterations.
    /// </summary>
    private int GetEffectiveMaxIterations()
    {
        int max = Math.Clamp(MaxIterations, 64, PaletteCeiling);
        if (!AdaptiveIterations) return max;
        // At zoom=1, use a low iteration count so palette contrast stays rich.
        // Ramp up with sqrt(log(zoom)) for a steeper rise at early zoom depths
        // where detail emerges, leveling off toward max at extreme zoom.
        int floor = Math.Max(64, max / 8);
        double t = Math.Clamp(Math.Log(_zoom) / Math.Log(MaxZoomCpu), 0.0, 1.0);
        double curved = Math.Sqrt(t); // steeper rise at low zoom, gentler at deep zoom
        return floor + (int)(curved * (max - floor));
    }

    /// <summary>
    /// Ensures the reference orbit is computed/up-to-date for the current center and maxIter.
    /// The orbit only needs recomputation when the center changes or maxIter increases.
    /// Uses BigFloat for arbitrary-precision at the center point.
    /// </summary>
    private void EnsureReferenceOrbit(int maxIter)
    {
        bool needsRecompute = _referenceOrbit == null
            || maxIter > _lastOrbitMaxIter
            || _centerRe != _lastOrbitCenterRe
            || _centerIm != _lastOrbitCenterIm;

        if (!needsRecompute) return;

        // Choose precision based on zoom depth: more bits for deeper zooms
        int precision = _zoom > 1e13 ? 256 : 128;
        _referenceOrbit = MandelbrotReferenceOrbit.Compute(_centerRe, _centerIm, maxIter, precision);
        _lastOrbitZoom = _zoom;
        _lastOrbitCenterRe = _centerRe;
        _lastOrbitCenterIm = _centerIm;
        _lastOrbitMaxIter = maxIter;
    }

    /// <summary>
    /// Returns true when zoom/center have changed enough that the iteration cache is stale
    /// and a full recomputation is needed (vs. a cheap palette-only recolor).
    /// </summary>
    private bool NeedsFullRecompute()
    {
        if (_iterCache == null) return true;
        if (_lastRenderZoom == 0) return true;
        // If zoom changed by more than 0.1% or center shifted, recompute
        double zoomRatio = _zoom / _lastRenderZoom;
        if (zoomRatio < 0.999 || zoomRatio > 1.001) return true;
        if (Math.Abs(_centerRe - _lastRenderCenterRe) > 1e-15 * _zoom) return true;
        if (Math.Abs(_centerIm - _lastRenderCenterIm) > 1e-15 * _zoom) return true;
        return false;
    }

    /// <summary>
    /// Samples the iteration cache on a sparse grid and returns a zoom-speed
    /// boost (1.0 = normal, up to 8.0 = rush through) plus an "interior" flag.
    /// </summary>
    private static (double Boost, bool Interior) MeasureBoring(double[] iterCache, int w, int h)
    {
        // Sample ~256 evenly-spaced pixels
        const int gridSize = 16;
        int stepX = Math.Max(1, w / gridSize);
        int stepY = Math.Max(1, h / gridSize);

        double min = double.MaxValue;
        double max = double.MinValue;
        int insideCount = 0;
        int sampleCount = 0;

        for (int y = stepY / 2; y < h; y += stepY)
        {
            int rowStart = y * w;
            for (int x = stepX / 2; x < w; x += stepX)
            {
                double v = iterCache[rowStart + x];
                sampleCount++;
                if (v < 0) insideCount++;
                else
                {
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }
        }

        if (sampleCount == 0) return (1.0, false);
        double insideRatio = (double)insideCount / sampleCount;
        if (insideRatio > 0.95) return (8.0, true);

        int exterior = sampleCount - insideCount;
        if (exterior < 8) return (8.0, true);

        return (BoostFromSpread(max - min), false);
    }

    private void CpuRenderFrame(double dt)
    {
        if (_bitmap == null) return;

        int w = _pixelWidth;
        int h = _pixelHeight;
        int stride = w * 4;
        int totalPixels = w * h;
        var pixels = new byte[totalPixels * 4];

        int maxIter = GetEffectiveMaxIterations();
        double brightBoost = 1.0 + _audioBrightnessBoost;
        bool fullRecompute = NeedsFullRecompute();

        if (fullRecompute)
        {
            // Full recompute — expensive
            if (_iterCache == null || _iterCache.Length != totalPixels)
                _iterCache = new double[totalPixels];

            // Ensure reference orbit is up to date for perturbation theory
            EnsureReferenceOrbit(maxIter);

            double scale = 3.0 / _zoom;
            double aspect = (double)w / h;
            double reStep = scale * aspect / w;
            double imStep = scale / h;
            // Compute pixel offsets as deltas from center (perturbation theory).
            // The view rotation is baked into the sampling basis so the rendered
            // bitmap remains axis-aligned (no black corners) while the fractal
            // appears rotated.
            double cosA = Math.Cos(_viewAngle);
            double sinA = Math.Sin(_viewAngle);
            // Pixel-space basis vectors after rotation.
            double xBasisR =  cosA * reStep;
            double xBasisI =  sinA * reStep;
            double yBasisR = -sinA * imStep;
            double yBasisI =  cosA * imStep;
            // Top-left pixel maps to (-w/2, -h/2) in pixel space, then rotated.
            double halfW = w * 0.5;
            double halfH = h * 0.5;
            double originR = -halfW * xBasisR - halfH * yBasisR;
            double originI = -halfW * xBasisI - halfH * yBasisI;

            var orbit = _referenceOrbit!;
            double cRe = _centerRe;
            double cIm = _centerIm;

            Parallel.For(0, h, y =>
            {
                double rowR = originR + y * yBasisR;
                double rowI = originI + y * yBasisI;
                int rowStart = y * w;
                for (int x = 0; x < w; x++)
                {
                    double deltaCr = rowR + x * xBasisR;
                    double deltaCi = rowI + x * xBasisI;
                    _iterCache[rowStart + x] = orbit.Iterate(deltaCr, deltaCi, maxIter, cRe, cIm);
                }
            });

            _lastRenderZoom = _zoom;
            _lastRenderCenterRe = _centerRe;
            _lastRenderCenterIm = _centerIm;
            _lastRenderMaxIter = maxIter;

            // Detect frames that are essentially solid-black interior or one
            // flat color band so we can rush through and/or abandon the target.
            var (boost, interior) = MeasureBoring(_iterCache, w, h);
            _boringBoostTarget = boost;
            _interiorHeavy = interior;
        }

        // Color from cache — cheap, runs every frame for palette animation.
        // Cyclic palette mapping: each iteration band gets its own color regardless
        // of zoom depth, so colors never wash out as maxIter grows. Optionally
        // remap iteration values via per-frame histogram equalization for
        // guaranteed full-palette usage on every frame.
        var cache = _iterCache!;
        double paletteScale = (double)PaletteCeiling / IterationsPerCycle;
        double offset = _paletteOffset; // hue rotation is baked into the palette itself
        bool useHistogram = HistogramColoring;

        // Build a CDF over escaping iteration values when histogram coloring is on.
        // Bucket count chosen for cheap binning; CDF maps bucket → palette index.
        const int HistogramBuckets = 1024;
        int[]? histCdf = null;
        double histMin = 0, histMax = 0;
        if (useHistogram)
        {
            var hist = new int[HistogramBuckets];
            double localMin = double.MaxValue, localMax = double.MinValue;
            // First pass: find escape-value range
            for (int i = 0; i < cache.Length; i++)
            {
                double v = cache[i];
                if (v < 0) continue;
                if (v < localMin) localMin = v;
                if (v > localMax) localMax = v;
            }
            if (localMax > localMin)
            {
                histMin = localMin;
                histMax = localMax;
                double invRange = (HistogramBuckets - 1) / (histMax - histMin);
                for (int i = 0; i < cache.Length; i++)
                {
                    double v = cache[i];
                    if (v < 0) continue;
                    int b = (int)((v - histMin) * invRange);
                    if (b < 0) b = 0;
                    else if (b >= HistogramBuckets) b = HistogramBuckets - 1;
                    hist[b]++;
                }
                // Cumulative
                int total = 0;
                for (int i = 0; i < HistogramBuckets; i++) { total += hist[i]; hist[i] = total; }
                histCdf = hist;
            }
            else
            {
                useHistogram = false; // not enough range to equalize
            }
        }

        Parallel.For(0, h, y =>
        {
            int rowStart = y * w;
            int rowOffset = y * stride;
            for (int x = 0; x < w; x++)
            {
                double smoothIter = cache[rowStart + x];
                int px = rowOffset + x * 4;

                if (smoothIter < 0)
                {
                    pixels[px] = 0;
                    pixels[px + 1] = 0;
                    pixels[px + 2] = 0;
                    pixels[px + 3] = 255;
                }
                else
                {
                    double idxF;
                    if (useHistogram && histCdf != null)
                    {
                        // Look up rank in CDF, smoothly interpolating between buckets
                        // for the fractional part of the iteration value.
                        double bf = (smoothIter - histMin) * (HistogramBuckets - 1) / (histMax - histMin);
                        if (bf < 0) bf = 0;
                        else if (bf > HistogramBuckets - 1) bf = HistogramBuckets - 1;
                        int b0 = (int)bf;
                        int b1 = Math.Min(b0 + 1, HistogramBuckets - 1);
                        double frac = bf - b0;
                        double total = histCdf[HistogramBuckets - 1];
                        double rank = (histCdf[b0] + frac * (histCdf[b1] - histCdf[b0])) / total;
                        idxF = rank * (PaletteCeiling - 1) + offset;
                    }
                    else
                    {
                        // Cyclic banding: every IterationsPerCycle iterations = one full palette cycle.
                        idxF = smoothIter * paletteScale + offset;
                    }

                    // Wrap into [0, PaletteCeiling)
                    double wrapped = idxF - Math.Floor(idxF / PaletteCeiling) * PaletteCeiling;
                    int idx0 = (int)wrapped;
                    if (idx0 >= PaletteCeiling) idx0 = PaletteCeiling - 1;
                    int idx1 = idx0 + 1; if (idx1 >= PaletteCeiling) idx1 = 0;
                    double frac2 = wrapped - idx0;

                    int p0 = idx0 * 4;
                    int p1 = idx1 * 4;
                    byte b = (byte)(_palette[p0]     + frac2 * (_palette[p1]     - _palette[p0]));
                    byte g = (byte)(_palette[p0 + 1] + frac2 * (_palette[p1 + 1] - _palette[p0 + 1]));
                    byte r = (byte)(_palette[p0 + 2] + frac2 * (_palette[p1 + 2] - _palette[p0 + 2]));

                    if (brightBoost > 1.0)
                    {
                        r = (byte)Math.Min(255, (int)(r * brightBoost));
                        g = (byte)Math.Min(255, (int)(g * brightBoost));
                        b = (byte)Math.Min(255, (int)(b * brightBoost));
                    }

                    pixels[px] = b;
                    pixels[px + 1] = g;
                    pixels[px + 2] = r;
                    pixels[px + 3] = 255;
                }
            }
        });

        // Write to bitmap on the UI thread
        try
        {
            _bitmap.Lock();
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, _bitmap.BackBuffer, pixels.Length);
            _bitmap.AddDirtyRect(new Int32Rect(0, 0, w, h));
            _bitmap.Unlock();
        }
        catch (Exception)
        {
            // Bitmap may be disposed during shutdown
        }
    }

    private static readonly double Log2 = Math.Log(2.0);

    /// <summary>
    /// Mandelbrot iteration with smooth (continuous) coloring.
    /// Returns a continuous iteration value for escaped points, or -1 for interior.
    /// Uses bailout radius of 256 for accurate smooth coloring.
    /// </summary>
    private static double ComputeIteration(double cr, double ci, int maxIter)
    {
        double zr = 0, zi = 0;
        double zr2 = 0, zi2 = 0;

        for (int i = 0; i < maxIter; i++)
        {
            zi = 2.0 * zr * zi + ci;
            zr = zr2 - zi2 + cr;
            zr2 = zr * zr;
            zi2 = zi * zi;

            if (zr2 + zi2 > 65536.0)
            {
                double log_zn = Math.Log(zr2 + zi2) * 0.5;
                double nu = Math.Log(log_zn / Log2) / Log2;
                return i + 1.0 - nu;
            }
        }

        return -1.0; // inside the set
    }

    /// <summary>
    /// Build a static, seamlessly-tileable HSL palette. Two full hue cycles
    /// across the palette plus a full sinusoidal lightness cycle gives smooth
    /// banding without any dim "wrap seam" between index 0 and the last index.
    /// Palette rotation is applied at coloring time via _paletteOffset, so this
    /// only needs to run once.
    /// </summary>
    private void BuildPalette()
    {
        for (int i = 0; i < PaletteCeiling; i++)
        {
            double t = (double)i / PaletteCeiling;
            // Two hue cycles → richer banding without overly long color runs.
            double hue = (t * 720.0) % 360.0;
            // Full sine cycle keeps the palette tileable; min lightness 0.45 so
            // no part of the palette is so dark that frames look black.
            double lightness = 0.55 + 0.20 * Math.Sin(t * Math.PI * 2.0);
            double saturation = 0.9;

            var (r, g, b) = HslToRgb(hue, saturation, lightness);
            int idx = i * 4;
            _palette[idx] = b;     // B
            _palette[idx + 1] = g; // G
            _palette[idx + 2] = r; // R
            _palette[idx + 3] = 255;
        }
    }

    private static (byte r, byte g, byte b) HslToRgb(double h, double s, double l)
    {
        double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
        double x = c * (1.0 - Math.Abs((h / 60.0) % 2.0 - 1.0));
        double m = l - c / 2.0;

        double r, g, b;
        if (h < 60)       { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else               { r = c; g = 0; b = x; }

        return (
            (byte)Math.Clamp((int)((r + m) * 255), 0, 255),
            (byte)Math.Clamp((int)((g + m) * 255), 0, 255),
            (byte)Math.Clamp((int)((b + m) * 255), 0, 255));
    }

    private static void LogGpu(string message)
    {
        var msg = $"[Mandelbrot] {message}";
        System.Diagnostics.Debug.WriteLine(msg);
        DebugLog.Log("Mandelbrot", message);
    }

    protected override void CleanupCanvas()
    {
        StopMotion();
        _canvas.SizeChanged -= OnCanvasSizeChanged;
        _resizeDebounceTimer?.Stop();
        _resizeDebounceTimer = null;
        _gpuRenderer?.Dispose();
        _gpuRenderer = null;
        if (_image != null)
        {
            _image.BeginAnimation(UIElement.OpacityProperty, null);
            _canvas.Children.Remove(_image);
            _image.Source = null;
            _image = null;
        }
        if (_dimOverlay != null)
        {
            _canvas.Children.Remove(_dimOverlay);
            _dimOverlay = null;
        }
        _bitmap = null;
        _blobs.Clear();
        _brushes.Clear();
        _gradBrushes.Clear();
        _states.Clear();
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopMotion();
        CleanupCanvas();
    }

    // ── Target selection ──────────────────────────────────────────

    /// <summary>
    /// Returns the initial view angle for a freshly-picked target based on the
    /// current <see cref="RotationMode"/>. Off → 0; RandomPerTarget → uniform
    /// random; SlowSpin → random starting offset so concurrent windows aren't
    /// all spinning in lockstep.
    /// </summary>
    private static double PickInitialViewAngle(Random rng) => RotationMode switch
    {
        RotationModeKind.RandomPerTarget => rng.NextDouble() * Math.PI * 2,
        RotationModeKind.SlowSpin => rng.NextDouble() * Math.PI * 2,
        _ => 0.0,
    };

    /// <summary>
    /// Picks a zoom target using the curated library, optional perturbation,
    /// and optional algorithmic discovery.
    /// </summary>
    private static (double Re, double Im) PickTarget(Random rng)
    {
        // Algorithmic discovery: try to find a novel boundary point
        if (Discovery)
        {
            var discovered = DiscoverBoundaryPoint(rng);
            if (discovered.HasValue)
                return discovered.Value;
            // Fall through to curated library if discovery fails
        }

        // Pick from the curated library using quality-weighted selection.
        // Targets tagged NeedsPerturbation are skipped when Perturbation is 0
        // (they sit on the cardioid/bulb itself and only show detail when
        // displaced).
        MandelbrotTarget chosen;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            chosen = MandelbrotZoomTargets.PickWeighted(rng);
            if (Perturbation > 0 ||
                (chosen.Tags & MandelbrotTargetTags.NeedsPerturbation) == 0)
            {
                double re = chosen.Re;
                double im = chosen.Im;

                // Apply random perturbation if enabled
                if (Perturbation > 0)
                {
                    // Max displacement ~0.01 in complex plane at full perturbation.
                    // Direction is random; magnitude scales with the slider.
                    double maxRadius = 0.01 * Perturbation;
                    double angle = rng.NextDouble() * Math.PI * 2;
                    double radius = maxRadius * rng.NextDouble();
                    re += Math.Cos(angle) * radius;
                    im += Math.Sin(angle) * radius;

                    // Nudge toward the boundary if perturbation pushed us inside or far outside.
                    re = NudgeToBoundary(re, im, chosen.Re, chosen.Im, rng);
                }

                return (re, im);
            }
        }

        // Fallback (shouldn't normally hit this): just return first non-NeedsPerturbation target.
        var lib = MandelbrotZoomTargets.AllDetailed;
        for (int i = 0; i < lib.Length; i++)
        {
            if ((lib[i].Tags & MandelbrotTargetTags.NeedsPerturbation) == 0)
                return (lib[i].Re, lib[i].Im);
        }
        return (lib[0].Re, lib[0].Im);
    }

    /// <summary>
    /// If the point (re, im) is deep inside or far outside the set, binary-search
    /// along the line from (re,im) toward (origRe, origIm) to find a boundary point.
    /// Returns the adjusted Re (Im is adjusted proportionally).
    /// </summary>
    private static double NudgeToBoundary(double re, double im, double origRe, double origIm, Random rng)
    {
        const int probeIter = 256;
        double v = ComputeIteration(re, im, probeIter);

        // If it escapes with some iterations (not immediately, not interior), it's fine
        if (v >= 5 && v < probeIter * 0.9)
            return re;

        // Binary search between perturbed and original to find the boundary
        double goodRe = origRe, goodIm = origIm;
        double testRe = re, testIm = im;
        for (int i = 0; i < 20; i++)
        {
            double midRe = (goodRe + testRe) * 0.5;
            double midIm = (goodIm + testIm) * 0.5;
            double midV = ComputeIteration(midRe, midIm, probeIter);
            if (midV >= 5 && midV < probeIter * 0.9)
            {
                // Found a good boundary point
                return midRe;
            }
            if (midV < 0 || midV >= probeIter * 0.9)
            {
                // Inside or nearly inside — move toward original
                testRe = midRe;
                testIm = midIm;
            }
            else
            {
                // Escapes too fast — move toward perturbed
                goodRe = midRe;
                goodIm = midIm;
            }
        }

        return goodRe; // best effort
    }

    /// <summary>
    /// Algorithmically discovers an interesting boundary point by probing random
    /// locations and measuring iteration-count spread in a small neighborhood.
    /// Returns null if no good candidate is found within the time budget.
    /// </summary>
    private static (double Re, double Im)? DiscoverBoundaryPoint(Random rng)
    {
        // Strategy: pick random points near the Mandelbrot boundary and score them
        // by how much iteration-count variation exists in a small neighborhood.
        // High variation = intricate boundary detail = good zoom target.

        const int maxAttempts = 200;
        const int probeIter = 256;
        const int neighborhoodSize = 8;
        const double neighborhoodRadius = 0.001;
        double bestSpread = 0;
        (double Re, double Im)? bestPoint = null;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            // Generate a random point in the interesting region of the complex plane
            // The Mandelbrot set fits within |Re| < 2, |Im| < 1.5
            // Focus sampling near known-interesting Re ranges
            double re, im;
            int region = rng.Next(5);
            switch (region)
            {
                case 0: // Seahorse valley vicinity
                    re = -0.75 + (rng.NextDouble() - 0.5) * 0.05;
                    im = 0.1 + (rng.NextDouble() - 0.5) * 0.05;
                    break;
                case 1: // Antenna/dendrite region
                    re = -0.1 + (rng.NextDouble() - 0.5) * 0.2;
                    im = 0.95 + (rng.NextDouble() - 0.5) * 0.15;
                    break;
                case 2: // Period-3 bulb / tail
                    re = -1.77 + (rng.NextDouble() - 0.5) * 0.05;
                    im = (rng.NextDouble() - 0.5) * 0.02;
                    break;
                case 3: // Elephant valley
                    re = 0.3 + (rng.NextDouble() - 0.5) * 0.2;
                    im = (rng.NextDouble() - 0.5) * 0.15;
                    break;
                default: // General boundary — anywhere within the set's bounding box
                    re = -0.5 + (rng.NextDouble() - 0.5) * 3.0;
                    im = (rng.NextDouble() - 0.5) * 2.5;
                    break;
            }

            // Quick check: is this point near the boundary? (not deep inside, not far outside)
            double v = ComputeIteration(re, im, probeIter);
            if (v < 3 || (v < 0)) continue; // skip interior and very-fast escape

            // Score the neighborhood by measuring iteration spread
            double min = double.MaxValue, max = double.MinValue;
            int validSamples = 0;
            for (int n = 0; n < neighborhoodSize; n++)
            {
                double angle = n * (Math.PI * 2.0 / neighborhoodSize);
                double nr = re + Math.Cos(angle) * neighborhoodRadius;
                double ni = im + Math.Sin(angle) * neighborhoodRadius;
                double nv = ComputeIteration(nr, ni, probeIter);
                if (nv < 0) continue; // skip interior neighbors
                validSamples++;
                if (nv < min) min = nv;
                if (nv > max) max = nv;
            }

            if (validSamples < 4) continue; // need enough exterior neighbors

            double spread = max - min;
            if (spread > bestSpread)
            {
                bestSpread = spread;
                bestPoint = (re, im);
            }

            // Good enough — don't burn too much CPU
            if (bestSpread > 50) break;
        }

        // Only return if we found a reasonably interesting point
        if (bestSpread > 15)
        {
            DebugLog.Log("Mandelbrot", $"Discovered boundary point ({bestPoint!.Value.Re:F10}, {bestPoint.Value.Im:F10}) spread={bestSpread:F1}");
            return bestPoint;
        }

        DebugLog.Log("Mandelbrot", $"Discovery failed (best spread={bestSpread:F1}), falling back to curated library");
        return null;
    }
}
