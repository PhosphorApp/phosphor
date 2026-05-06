using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

    private WriteableBitmap? _bitmap;
    private System.Windows.Controls.Image? _image;
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
    private double _baseZoomSpeed = 0.0027; // zoom factor per frame
    private double _zoomSpeed;
    private double _boringBoost = 1.0;      // multiplier when frame is near-uniform (1.0 = normal)
    private double _paletteOffset;
    private double _paletteSpeed = 0.5;

    // Spiral drift — subtle orbit around the zoom target
    private double _spiralAngle;
    private const double SpiralSpeed = 0.25;       // radians per second
    private const double SpiralBaseRadius = 0.02;   // radius in complex plane at zoom=1

    private const double BlurRadius = 0.75;  // very subtle softening; smooth coloring handles the rest

    // Boring-frame tracking — abandon targets that stay uniform too long
    private int _boringFrameCount;
    private const int MaxBoringFrames = 240; // ~4 seconds at 60fps before abandoning target

    // Audio reactive modifiers
    private double _audioZoomBoost = 1.1;
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
        _zoomSpeed = _baseZoomSpeed;
        _minFrameIntervalTicks = MandelbrotMaxHz > 0
            ? Stopwatch.Frequency / MandelbrotMaxHz
            : 0;
        BuildPalette(0);
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
            var dimOverlay = new System.Windows.Shapes.Rectangle
            {
                Width = w,
                Height = h,
                Fill = new SolidColorBrush(Colors.Black),
                Opacity = Math.Clamp(Dimming, 0.0, 0.9),
                IsHitTestVisible = false,
            };
            _canvas.Children.Add(dimOverlay);
        }
    }

    public override void Enter(Action onComplete)
    {
        if (_disposed) { onComplete(); return; }

        CreateBlobs();

        if (_image == null) { onComplete(); return; }

        double w = _canvas.ActualWidth;
        double h = _canvas.ActualHeight;
        if (w <= 0 || h <= 0) { onComplete(); return; }

        // Render the first frame immediately so the fade-in isn't blank
        RenderFrame(0);

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

        // Bass drives zoom speed boost (1x–3x normal)
        _audioZoomBoost = 1.0 + data.Bass * 2.0;
        if (data.IsBeat) _audioZoomBoost = Math.Min(_audioZoomBoost + 0.5, 3.5);

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

        // Advance zoom — boost speed when the frame is near-uniform to skip boring bands
        _zoomSpeed = _baseZoomSpeed * Math.Max(0.1, _speedMultiplier) * _audioZoomBoost * _boringBoost;
        _zoom *= (1.0 + _zoomSpeed);

        // Keep center locked on the zoom target (no spiral drift)
        _centerRe = _targetRe;
        _centerIm = _targetIm;

        // Track boring frames — if we've been in a color desert too long, abandon this target
        if (_boringBoost > 2.0)
            _boringFrameCount++;
        else
            _boringFrameCount = 0;

        double maxZoom = (_gpuRenderer != null && _gpuRenderer.IsAvailable) ? MaxZoomGpu : MaxZoomCpu;
        bool shouldReset = _zoom > maxZoom || _boringFrameCount > MaxBoringFrames;

        if (shouldReset)
        {
            _zoom = 1.0;
            _boringBoost = 1.0;
            _boringFrameCount = 0;
            _referenceOrbit = null; // force recomputation for new target
            var (re, im) = PickTarget(_rng);
            _targetRe = re;
            _targetIm = im;
            _centerRe = _targetRe;
            _centerIm = _targetIm;
            _spiralAngle = _rng.NextDouble() * Math.PI * 2;
        }

        // Advance palette
        _paletteOffset += (_paletteSpeed + _audioPaletteBoost) * dt * 60.0;
        BuildPalette(_paletteOffset);

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
        // The shader uses delta iteration from the reference orbit
        _gpuRenderer.RenderFrame(
            _centerRe, _centerIm, _zoom,
            maxIter, _paletteOffset, brightBoost);

        // Detect boring frames on GPU path by sampling a few points on CPU.
        _boringBoost = MeasureBoringBoostSparse(_centerRe, _centerIm, _zoom, _pixelWidth, _pixelHeight, maxIter);
    }

    /// <summary>
    /// Lightweight boring-frame detection for the GPU path: computes a small number
    /// of Mandelbrot iterations on CPU to measure the iteration spread without
    /// reading back the GPU framebuffer.
    /// </summary>
    private static double MeasureBoringBoostSparse(double centerRe, double centerIm, double zoom, int w, int h, int maxIter)
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

        if (sampleCount == 0) return 1.0;
        double insideRatio = (double)insideCount / sampleCount;
        if (insideRatio > 0.95) return 8.0;
        if (insideCount == sampleCount) return 8.0;

        double spread = max - min;
        if (spread < 2.0) return 6.0;
        if (spread < 5.0) return 4.0;
        if (spread < 10.0) return 2.0;
        return 1.0;
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
    /// multiplier: 1.0 when detail is rich, up to 8.0 when the frame is nearly
    /// uniform (a "color desert"). This causes the animation to rush through
    /// boring zoom bands instead of dwelling on them.
    /// </summary>
    private static double MeasureBoringBoost(double[] iterCache, int w, int h)
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
                if (v < 0)
                {
                    insideCount++;
                }
                else
                {
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }
        }

        if (sampleCount == 0) return 1.0;

        // If almost all pixels are inside the set (black), boost hard
        double insideRatio = (double)insideCount / sampleCount;
        if (insideRatio > 0.95) return 8.0;

        // If all sampled pixels are inside, boost hard
        if (insideCount == sampleCount) return 8.0;

        // Measure the spread of escape-iteration values across the frame.
        // A large spread means rich detail; a tiny spread means uniform color.
        double spread = max - min;
        if (spread < 2.0) return 6.0;   // nearly identical iterations → very boring
        if (spread < 5.0) return 4.0;   // low detail
        if (spread < 10.0) return 2.0;  // mild
        return 1.0;                      // rich detail — normal speed
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
            // Compute pixel offsets as deltas from center (perturbation theory)
            double deltaReMin = -scale * aspect * 0.5;
            double deltaImMin = -scale * 0.5;

            var orbit = _referenceOrbit!;
            double cRe = _centerRe;
            double cIm = _centerIm;

            Parallel.For(0, h, y =>
            {
                double deltaCi = deltaImMin + y * imStep;
                int rowStart = y * w;
                for (int x = 0; x < w; x++)
                {
                    double deltaCr = deltaReMin + x * reStep;
                    _iterCache[rowStart + x] = orbit.Iterate(deltaCr, deltaCi, maxIter, cRe, cIm);
                }
            });

            _lastRenderZoom = _zoom;
            _lastRenderCenterRe = _centerRe;
            _lastRenderCenterIm = _centerIm;
            _lastRenderMaxIter = maxIter;

            // Detect near-uniform frames
            // to push through them quickly. Sample a grid of pixels and measure
            // the spread of iteration values; low spread = boring.
            _boringBoost = MeasureBoringBoost(_iterCache, w, h);
        }

        // Color from cache — cheap, runs every frame for palette animation.
        // Use logarithmic mapping so local contrast is preserved regardless of
        // the max-iteration ceiling: log(iter) / log(maxIter) → [0, 1] → palette.
        var cache = _iterCache!;
        int cachedMaxIter = Math.Max(2, _lastRenderMaxIter);
        double logMaxIter = Math.Log(cachedMaxIter);
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
                    double t = Math.Log(Math.Max(smoothIter, 1.0)) / logMaxIter;
                    double scaled = t * (PaletteCeiling - 1);
                    double clamped = Math.Max(0, Math.Min(scaled, PaletteCeiling - 1.001));
                    int idx0 = (int)clamped;
                    int idx1 = Math.Min(idx0 + 1, PaletteCeiling - 1);
                    double frac = clamped - idx0;

                    int p0 = idx0 * 4;
                    int p1 = idx1 * 4;
                    byte b = (byte)(_palette[p0]     + frac * (_palette[p1]     - _palette[p0]));
                    byte g = (byte)(_palette[p0 + 1] + frac * (_palette[p1 + 1] - _palette[p0 + 1]));
                    byte r = (byte)(_palette[p0 + 2] + frac * (_palette[p1 + 2] - _palette[p0 + 2]));

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
    /// Build a rotating HSL-based palette. Offset rotates the hue wheel.
    /// </summary>
    private void BuildPalette(double offset)
    {
        for (int i = 0; i < PaletteCeiling; i++)
        {
            // Map iteration to hue with offset for rotation
            double t = (double)i / PaletteCeiling;
            double hue = (t * 360.0 + offset) % 360.0;
            if (hue < 0) hue += 360.0;

            // Vary lightness: dark near 0 iterations, bright in mid-range, dark near max
            double lightness = 0.1 + 0.7 * Math.Sin(t * Math.PI);
            double saturation = 0.85;

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
        _gpuRenderer?.Dispose();
        _gpuRenderer = null;
        if (_image != null)
        {
            _image.BeginAnimation(UIElement.OpacityProperty, null);
            _canvas.Children.Remove(_image);
            _image.Source = null;
            _image = null;
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

        // Pick from the curated library
        var targets = MandelbrotZoomTargets.All;
        var (re, im) = targets[rng.Next(targets.Length)];

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
            // Quick probe: if the perturbed point is clearly interior or clearly exterior,
            // walk it back toward the original until it's on the boundary.
            re = NudgeToBoundary(re, im, targets[rng.Next(targets.Length)].Re, targets[rng.Next(targets.Length)].Im, rng);
        }

        return (re, im);
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
