using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;

namespace Phosphor;

/// <summary>
/// Conway's Game of Life visualization rendered to a <see cref="WriteableBitmap"/>.
/// Each living cell carries a color; offspring inherit a blend of their parents.
/// Audio beats inject new cells inversely proportional to population density.
/// Dying cells fade out over several generations; rapidly reproducing areas brighten.
/// </summary>
public sealed class GameOfLifePattern : BlobPatternBase
{
    /// <summary>Cell size in pixels (each cell is a square block). Default 5.</summary>
    public static int CellSize { get; set; } = 5;

    /// <summary>Simulation tick interval in milliseconds. Default 100.</summary>
    public static int TickIntervalMs { get; set; } = 100;

    /// <summary>Number of generations a dying cell takes to fully fade out. Default 6.</summary>
    public static int FadeGenerations { get; set; } = 6;

    /// <summary>Brightness boost added to newly born cells (0–100). Default 60.</summary>
    public static int HeatBoost { get; set; } = 60;

    /// <summary>Injection density (1–10). 5 = default, lower = sparser, higher = more crowded.</summary>
    public static int Density { get; set; } = 5;

    /// <summary>Whether camera roam is enabled. Default true.</summary>
    public static bool CameraRoam { get; set; } = true;

    /// <summary>Maximum zoom level for camera roam (1.2–3.0). Default 1.6.</summary>
    public static double CameraMaxZoom { get; set; } = 1.6;

    /// <summary>Percentage of extra grid beyond the visible area (0–100). Default 50.</summary>
    public static int CameraOverscan { get; set; } = 50;

    /// <summary>Bitmap scaling mode used when upscaling cells to screen size. Default NearestNeighbor.</summary>
    public static BitmapScalingMode ScalingMode { get; set; } = BitmapScalingMode.NearestNeighbor;

    /// <summary>Fraction of grid edge to keep clear when placing seed clusters (0.0–0.5).</summary>
    private const double PlacementMargin = 0.05;

    public override BlobPattern PatternType => BlobPattern.GameOfLife;
    public override bool ManagesOwnColors => true;

    private WriteableBitmap? _bitmap;
    private Image? _image;
    private DispatcherTimer? _timer;

    private int _gridW, _gridH;

    // Per-cell state: color RGB (0 = dead), age (generations alive), fade (countdown when dying)
    private uint[] _colorCurrent = [];   // packed BGRA — 0 means dead
    private uint[] _colorNext = [];
    private ushort[] _age = [];            // how many generations this cell has been alive
    private byte[] _fade = [];           // remaining fade-out ticks (>0 = recently died, still rendering)
    private uint[] _fadeColor = [];      // color at moment of death, for fade rendering

    // Audio state
    private bool _pendingBeat;
    private float _lastLevel;
    private float _bassAccumulator;  // builds up from sustained bass to trigger injection

    // Pulse state — remaining frames of brightness boost from PulseDominantColor
    private int _pulseFramesRemaining;
    private RoygbivColor _pulseBand;
    private const int PulseFrameCount = 6;
    private const uint PulseBrightnessBoost = 80;
    private DateTime _lastPulseTime = DateTime.MinValue;
    private static readonly TimeSpan PulseCooldown = TimeSpan.FromSeconds(8);

    // Generation counter for periodic injection when audio is off
    private int _generationCount;
    private bool _audioReactiveActive;

    // Frame timing diagnostics (baseline single-threaded; matches GoL-MT branch logger)
    private readonly System.Diagnostics.Stopwatch _frameSw = new();
    private const int FrameTimingLogInterval = 1000;

    // Camera roam state — continuous exploration model
    private enum CameraState { Settling, Exploring }
    private CameraState _cameraState = CameraState.Settling;
    private int _cameraRetargetTicks;       // ticks until we pick a new wander target
    private double _cameraZoom = 1.0;       // current zoom level
    private double _cameraTargetZoom = 1.0;
    private double _cameraPanX, _cameraPanY; // current pan offset in pixels
    private double _cameraTargetX, _cameraTargetY;
    private double _cameraAngle;            // current rotation in degrees
    private double _cameraTargetAngle;
    private double _cameraDriftX, _cameraDriftY; // very slow constant drift velocity
    private double _cameraDriftAngle;       // very slow rotational drift
    private ScaleTransform? _scaleTransform;
    private TranslateTransform? _translateTransform;
    private RotateTransform? _rotateTransform;
    private double _displayW, _displayH;    // visible canvas size
    private double _overscanW, _overscanH;  // total image size (display + overscan)

    // Sector activity tracking for intelligent camera targeting
    private const int SectorCountX = 8;
    private const int SectorCountY = 8;
    private int[] _sectorBirths = new int[SectorCountX * SectorCountY];
    private int[] _sectorAlive = new int[SectorCountX * SectorCountY];

    // Stagnation detection — two snapshots taken SnapshotInterval generations apart.
    // Cells alive in both snapshots at the same position are considered stagnant
    // (still-lifes or short-period oscillators). Used to bias injection locations.
    private const int SnapshotInterval = 30;   // generations between snapshots
    private bool[] _snapshotA = [];            // older snapshot (alive = true)
    private bool[] _snapshotB = [];            // newer snapshot
    private int _snapshotGen;                  // generation counter for snapshot timing
    private bool _snapshotReady;               // true once both snapshots have been taken

    public GameOfLifePattern(BlobPatternConfig config) : base(config) { }

    public override void Enter(Action onComplete)
    {
        if (_disposed) { onComplete(); return; }

        if (!_canvas.IsLoaded || _canvas.ActualWidth <= 0 || _canvas.ActualHeight <= 0)
        {
            void DeferredEnter(object? s, RoutedEventArgs e)
            {
                _canvas.Loaded -= DeferredEnter;
                if (_disposed) { onComplete(); return; }
                _canvas.Dispatcher.BeginInvoke(new Action(() => Enter(onComplete)),
                    DispatcherPriority.Loaded);
            }
            _canvas.Loaded += DeferredEnter;
            return;
        }

        CreateBlobs();

        if (_image == null) { onComplete(); return; }

        // Render the first frame so the fade-in isn't blank
        RenderFrame();

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

    protected override void CreateBlobs()
    {
        double w = Math.Max(200, _canvas.ActualWidth);
        double h = Math.Max(200, _canvas.ActualHeight);
        _displayW = w;
        _displayH = h;

        // When camera roam is enabled, expand the grid beyond the visible area
        double overscanFrac = CameraRoam ? Math.Clamp(CameraOverscan, 0, 100) / 100.0 : 0.0;
        _overscanW = w * (1.0 + overscanFrac);
        _overscanH = h * (1.0 + overscanFrac);

        int cellSize = Math.Max(1, CellSize);
        _gridW = Math.Max(1, (int)(_overscanW / cellSize));
        _gridH = Math.Max(1, (int)(_overscanH / cellSize));

        int totalCells = _gridW * _gridH;
        _colorCurrent = new uint[totalCells];
        _colorNext = new uint[totalCells];
        _age = new ushort[totalCells];
        _fade = new byte[totalCells];
        _fadeColor = new uint[totalCells];
        _sectorBirths = new int[SectorCountX * SectorCountY];
        _sectorAlive = new int[SectorCountX * SectorCountY];
        _snapshotA = new bool[totalCells];
        _snapshotB = new bool[totalCells];
        _snapshotGen = 0;
        _snapshotReady = false;

        _bitmap = new WriteableBitmap(_gridW, _gridH, 96, 96, PixelFormats.Bgra32, null);

        _image = new Image
        {
            Width = _overscanW,
            Height = _overscanH,
            Source = _bitmap,
            Stretch = Stretch.Fill,
            Opacity = 0,
        };

        RenderOptions.SetBitmapScalingMode(_image!, ScalingMode);

        // Position oversized image so the visible center aligns with the canvas center
        double offsetX = -(_overscanW - w) / 2.0;
        double offsetY = -(_overscanH - h) / 2.0;
        Canvas.SetLeft(_image!, offsetX);
        Canvas.SetTop(_image!, offsetY);

        // Clip canvas so overscan edges are never visible
        _canvas.ClipToBounds = true;

        // Set up camera transforms
        _scaleTransform = new ScaleTransform(1.0, 1.0);
        _translateTransform = new TranslateTransform(0, 0);
        _rotateTransform = new RotateTransform(0);
        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(_scaleTransform);
        transformGroup.Children.Add(_rotateTransform);
        transformGroup.Children.Add(_translateTransform);
        _image!.RenderTransform = transformGroup;
        _image.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);

        // Reset camera state
        _cameraState = CameraState.Settling;
        _cameraZoom = 1.0;
        _cameraTargetZoom = 1.0;
        _cameraPanX = 0; _cameraPanY = 0;
        _cameraTargetX = 0; _cameraTargetY = 0;
        _cameraAngle = 0; _cameraTargetAngle = 0;
        _cameraRetargetTicks = TicksFromSeconds(3, 6);

        // Initial gentle drift
        _cameraDriftX = (_rng.NextDouble() - 0.5) * 0.22;
        _cameraDriftY = (_rng.NextDouble() - 0.5) * 0.22;
        _cameraDriftAngle = (_rng.NextDouble() - 0.5) * 0.012;

        // Dummy brush/grad so base class indexing doesn't crash
        _brushes.Add(new SolidColorBrush(Colors.Black));
        _gradBrushes.Add(new RadialGradientBrush());

        _canvas.Children.Add(_image!);
        _blobs.Add(_image!);

        SeedGrid();
    }

    /// <summary>Returns a random tick count between minSeconds and maxSeconds based on current TickIntervalMs.</summary>
    private int TicksFromSeconds(double minSeconds, double maxSeconds)
    {
        double seconds = minSeconds + _rng.NextDouble() * (maxSeconds - minSeconds);
        return Math.Max(1, (int)(seconds * 1000.0 / Math.Max(16, TickIntervalMs)));
    }

    private void SeedGrid()
    {
        // Scale seed count by the overscan area ratio so visible density stays uniform
        double areaRatio = (_overscanW * _overscanH) / Math.Max(1, _displayW * _displayH);
        int count = Math.Max(1, (int)(_blobCount * areaRatio));
        int marginX = Math.Max(2, (int)(_gridW * PlacementMargin));
        int marginY = Math.Max(2, (int)(_gridH * PlacementMargin));

        for (int s = 0; s < count; s++)
        {
            double hue = _rng.NextDouble() * 360.0;
            var color = HslToColor(hue, 0.9, 0.6);
            uint packed = PackColor(color);

            int cx = _rng.Next(marginX, _gridW - marginX);
            int cy = _rng.Next(marginY, _gridH - marginY);

            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (_rng.NextDouble() < 0.5) continue;
                int x = cx + dx, y = cy + dy;
                if (x >= 0 && x < _gridW && y >= 0 && y < _gridH)
                {
                    int idx = y * _gridW + x;
                    _colorCurrent[idx] = packed;
                    _age[idx] = 1;
                }
            }
        }
    }

    protected override void StartMotion()
    {
        RenderFrame();

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(1, TickIntervalMs)),
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

        _frameSw.Restart();
        _generationCount++;

        // Inject new cells on beat
        if (_pendingBeat)
        {
            _pendingBeat = false;
            InjectCells();
        }
        else if (!_audioReactiveActive)
        {
            // When audio reactive is off, periodically inject cells to keep
            // the simulation alive. Interval scales with tick rate so it's
            // roughly every ~10 seconds regardless of TickIntervalMs.
            int injectEvery = Math.Max(10, 8000 / Math.Max(16, TickIntervalMs));
            if (_generationCount % injectEvery == 0)
                InjectCells();
        }

        StepSimulation();
        RenderFrame();
        UpdateCamera();

        _frameSw.Stop();
        if (_generationCount % FrameTimingLogInterval == 0)
            DebugLog.Log($"[GoL-ST] Frame {_generationCount}: {_frameSw.Elapsed.TotalMilliseconds:F2} ms  grid={_gridW}x{_gridH} ({_gridW * _gridH} cells)");
    }

    private void StepSimulation()
    {
        int w = _gridW, h = _gridH;
        int totalCells = w * h;
        Array.Clear(_colorNext);
        Array.Clear(_sectorBirths);
        Array.Clear(_sectorAlive);
        int sectorW = Math.Max(1, w / SectorCountX);
        int sectorH = Math.Max(1, h / SectorCountY);

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int idx = y * w + x;
            int neighbors = 0;
            uint rSum = 0, gSum = 0, bSum = 0;

            // Count live neighbors and accumulate colors
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = x + dx, ny = y + dy;

                // Wrap around edges for seamless borders
                if (nx < 0) nx = w - 1; else if (nx >= w) nx = 0;
                if (ny < 0) ny = h - 1; else if (ny >= h) ny = 0;

                uint nc = _colorCurrent[ny * w + nx];
                if (nc != 0)
                {
                    neighbors++;
                    rSum += (nc >> 16) & 0xFF;
                    gSum += (nc >> 8) & 0xFF;
                    bSum += nc & 0xFF;
                }
            }

            bool alive = _colorCurrent[idx] != 0;

            if (alive)
            {
                if (neighbors == 2 || neighbors == 3)
                {
                    // Survives — keep its color, increment age
                    _colorNext[idx] = _colorCurrent[idx];
                    _age[idx] = (ushort)Math.Min(ushort.MaxValue, _age[idx] + 1);
                    _fade[idx] = 0;
                    int si = Math.Min(x / sectorW, SectorCountX - 1) + Math.Min(y / sectorH, SectorCountY - 1) * SectorCountX;
                    _sectorAlive[si]++;
                }
                else
                {
                    // Dies — start fade
                    _colorNext[idx] = 0;
                    _fade[idx] = (byte)Math.Clamp(FadeGenerations, 1, 255);
                    _fadeColor[idx] = _colorCurrent[idx];
                    _age[idx] = 0;
                }
            }
            else
            {
                if (neighbors == 3)
                {
                    // Birth — color is average of parents
                    uint r = rSum / 3, g = gSum / 3, b = bSum / 3;
                    _colorNext[idx] = 0xFF000000 | (r << 16) | (g << 8) | b;
                    _age[idx] = 1;
                    _fade[idx] = 0;
                    int bi = Math.Min(x / sectorW, SectorCountX - 1) + Math.Min(y / sectorH, SectorCountY - 1) * SectorCountX;
                    _sectorBirths[bi]++;
                }
                else
                {
                    // Still dead — tick fade if active
                    _colorNext[idx] = 0;
                    if (_fade[idx] > 0) _fade[idx]--;
                    // age stays 0
                }
            }
        }

        // Swap buffers
        (_colorCurrent, _colorNext) = (_colorNext, _colorCurrent);

        // Stagnation snapshots: take a snapshot every SnapshotInterval generations.
        // Rotate A <- B <- current so we compare two points in time.
        _snapshotGen++;
        if (_snapshotGen >= SnapshotInterval)
        {
            _snapshotGen = 0;
            // Rotate: A gets the old B, B gets the current state
            (_snapshotA, _snapshotB) = (_snapshotB, _snapshotA);
            for (int i = 0; i < totalCells; i++)
                _snapshotB[i] = _colorCurrent[i] != 0;
            _snapshotReady = true;
        }
    }

    private void RenderFrame()
    {
        if (_bitmap == null) return;

        bool pulsing = _pulseFramesRemaining > 0;
        if (pulsing) _pulseFramesRemaining--;

        // Accumulate average color of living cells for dominant band detection
        long totalR = 0, totalG = 0, totalB = 0;
        int aliveCount = 0;

        _bitmap.Lock();
        try
        {
            unsafe
            {
                uint* pixels = (uint*)_bitmap.BackBuffer;
                int w = _gridW, h = _gridH;

                for (int i = 0; i < w * h; i++)
                {
                    uint c = _colorCurrent[i];
                    if (c != 0)
                    {
                        uint cr = (c >> 16) & 0xFF;
                        uint cg = (c >> 8) & 0xFF;
                        uint cb = c & 0xFF;
                        totalR += cr;
                        totalG += cg;
                        totalB += cb;
                        aliveCount++;

                        // Apply heat boost for young cells + pulse boost for matching band
                        uint boost = _age[i] <= 2 ? (uint)Math.Clamp(HeatBoost, 0, 255) : 0u;
                        if (pulsing && CellMatchesBand(cr, cg, cb, _pulseBand))
                            boost += PulseBrightnessBoost;

                        if (boost > 0)
                        {
                            uint r = Math.Min(255, cr + boost);
                            uint g = Math.Min(255, cg + boost);
                            uint b = Math.Min(255, cb + boost);
                            pixels[i] = 0xFF000000 | (r << 16) | (g << 8) | b;
                        }
                        else
                        {
                            pixels[i] = c;
                        }
                    }
                    else if (_fade[i] > 0)
                    {
                        uint fc = _fadeColor[i];
                        int fadeMax = Math.Max(1, FadeGenerations);
                        uint alpha = (uint)(255 * _fade[i] / fadeMax);
                        uint r = (fc >> 16) & 0xFF;
                        uint g = (fc >> 8) & 0xFF;
                        uint b = fc & 0xFF;
                        r = r * alpha / 255;
                        g = g * alpha / 255;
                        b = b * alpha / 255;
                        pixels[i] = (alpha << 24) | (r << 16) | (g << 8) | b;
                    }
                    else
                    {
                        pixels[i] = 0;
                    }
                }
            }

            _bitmap.AddDirtyRect(new Int32Rect(0, 0, _gridW, _gridH));
        }
        finally
        {
            _bitmap.Unlock();
        }

        // Update the dummy brush with the average living cell color so the
        // color cycling timer can detect dominant band changes for DOF.
        if (aliveCount > 0 && _brushes.Count > 0)
        {
            byte avgR = (byte)(totalR / aliveCount);
            byte avgG = (byte)(totalG / aliveCount);
            byte avgB = (byte)(totalB / aliveCount);
            _brushes[0].Color = Color.FromRgb(avgR, avgG, avgB);
        }
    }

    // ─── Camera Roam ──────────────────────────────────────────

    private void UpdateCamera()
    {
        if (!CameraRoam || _scaleTransform == null || _translateTransform == null || _rotateTransform == null)
            return;

        double maxZoom = Math.Clamp(CameraMaxZoom, 1.1, 3.0);

        // Always apply a very slow constant drift — gives the "floating in space" feel
        _cameraTargetX += _cameraDriftX;
        _cameraTargetY += _cameraDriftY;
        _cameraTargetAngle += _cameraDriftAngle;
        ClampCameraTarget();

        switch (_cameraState)
        {
            case CameraState.Settling:
                // Initial settle: slowly zoom in to a first target
                _cameraZoom = Lerp(_cameraZoom, _cameraTargetZoom, 0.007);
                _cameraPanX = Lerp(_cameraPanX, _cameraTargetX, 0.007);
                _cameraPanY = Lerp(_cameraPanY, _cameraTargetY, 0.007);
                _cameraAngle = Lerp(_cameraAngle, _cameraTargetAngle, 0.005);

                _cameraRetargetTicks--;
                if (_cameraRetargetTicks <= 0)
                {
                    PickCameraTarget(maxZoom);
                    _cameraState = CameraState.Exploring;
                    _cameraRetargetTicks = TicksFromSeconds(10, 25);
                }
                break;

            case CameraState.Exploring:
                // Very slowly lerp toward current target; drift keeps us moving between retargets
                _cameraZoom = Lerp(_cameraZoom, _cameraTargetZoom, 0.005);
                _cameraPanX = Lerp(_cameraPanX, _cameraTargetX, 0.005);
                _cameraPanY = Lerp(_cameraPanY, _cameraTargetY, 0.005);
                _cameraAngle = Lerp(_cameraAngle, _cameraTargetAngle, 0.0035);

                _cameraRetargetTicks--;
                if (_cameraRetargetTicks <= 0)
                {
                    PickCameraTarget(maxZoom);
                    _cameraRetargetTicks = TicksFromSeconds(10, 25);
                }
                break;
        }

        // Apply transforms
        _scaleTransform.ScaleX = _cameraZoom;
        _scaleTransform.ScaleY = _cameraZoom;
        _translateTransform.X = _cameraPanX;
        _translateTransform.Y = _cameraPanY;
        _rotateTransform.Angle = _cameraAngle;
    }

    /// <summary>
    /// Pick a new camera wander target by finding an active sector.
    /// Targets vary zoom gently and compute the maximum safe rotation
    /// given the current zoom and overscan.
    /// </summary>
    private void PickCameraTarget(double maxZoom)
    {
        // Score each sector: births × 3 + alive
        int totalSectors = SectorCountX * SectorCountY;
        int bestScore = 0;

        Span<int> scores = stackalloc int[totalSectors];
        for (int i = 0; i < totalSectors; i++)
        {
            scores[i] = _sectorBirths[i] * 3 + _sectorAlive[i];
            if (scores[i] > bestScore) bestScore = scores[i];
        }

        // Accept sectors scoring at least 40% of the best
        int threshold = Math.Max(1, bestScore * 2 / 5);
        int candidateCount = 0;
        for (int i = 0; i < totalSectors; i++)
            if (scores[i] >= threshold) candidateCount++;

        if (candidateCount == 0) candidateCount = totalSectors;

        int pick = _rng.Next(candidateCount);
        int seen = 0;
        int bestIdx = 0;
        for (int i = 0; i < totalSectors; i++)
        {
            if (scores[i] >= threshold || candidateCount == totalSectors)
            {
                if (seen == pick) { bestIdx = i; break; }
                seen++;
            }
        }

        // Convert sector to fractional position on the image (0..1)
        int sectorX = bestIdx % SectorCountX;
        int sectorY = bestIdx / SectorCountX;
        double fracX = (sectorX + 0.5) / SectorCountX; // 0..1
        double fracY = (sectorY + 0.5) / SectorCountY;

        // Gently vary zoom — wander between ~60% and 100% of max zoom so it stays interesting
        double minWander = 1.0 + (maxZoom - 1.0) * 0.5;
        _cameraTargetZoom = minWander + _rng.NextDouble() * (maxZoom - minWander);

        // Pan offset: move the target sector toward the viewport center
        double imgCenterFracX = fracX - 0.5; // -0.5..+0.5
        double imgCenterFracY = fracY - 0.5;
        _cameraTargetX = -imgCenterFracX * _overscanW * (_cameraTargetZoom - 1.0) * 0.8;
        _cameraTargetY = -imgCenterFracY * _overscanH * (_cameraTargetZoom - 1.0) * 0.8;

        // Compute the maximum safe rotation angle given zoom, overscan, and pan offset.
        // The available margin at current zoom is (overscanDim * zoom - displayDim) / 2 - |pan|.
        // Rotation by angle θ requires extra margin ≈ sin(θ) * diagonal / 2.
        // Solve for max θ: the geometry fully determines how far we can rotate.
        double marginX = Math.Max(0, (_overscanW * _cameraTargetZoom - _displayW) / 2.0 - Math.Abs(_cameraTargetX));
        double marginY = Math.Max(0, (_overscanH * _cameraTargetZoom - _displayH) / 2.0 - Math.Abs(_cameraTargetY));
        double minMargin = Math.Min(marginX, marginY);
        double diagonal = Math.Sqrt(_displayW * _displayW + _displayH * _displayH) / 2.0;
        double maxAngle = diagonal > 0 ? Math.Asin(Math.Clamp(minMargin / diagonal, 0, 1)) * (180.0 / Math.PI) : 0;

        _cameraTargetAngle = (_rng.NextDouble() - 0.5) * 2.0 * maxAngle;

        // Set a new gentle drift direction — keeps the camera floating between retargets
        _cameraDriftX = (_rng.NextDouble() - 0.5) * 0.22;
        _cameraDriftY = (_rng.NextDouble() - 0.5) * 0.22;
        // Scale rotational drift to the available rotation range — gentle at edges, freer at center
        _cameraDriftAngle = (_rng.NextDouble() - 0.5) * 0.012 * Math.Max(1.0, maxAngle / 15.0);

        ClampCameraTarget();
    }

    /// <summary>Clamp camera pan and rotation targets so the viewport stays within bounds.</summary>
    private void ClampCameraTarget()
    {
        double marginX = (_overscanW * _cameraTargetZoom - _displayW) / 2.0;
        double marginY = (_overscanH * _cameraTargetZoom - _displayH) / 2.0;
        marginX = Math.Max(0, marginX);
        marginY = Math.Max(0, marginY);
        _cameraTargetX = Math.Clamp(_cameraTargetX, -marginX, marginX);
        _cameraTargetY = Math.Clamp(_cameraTargetY, -marginY, marginY);

        // Clamp rotation to what the remaining margin supports
        double remX = Math.Max(0, marginX - Math.Abs(_cameraTargetX));
        double remY = Math.Max(0, marginY - Math.Abs(_cameraTargetY));
        double minMargin = Math.Min(remX, remY);
        double diagonal = Math.Sqrt(_displayW * _displayW + _displayH * _displayH) / 2.0;
        double maxAngle = diagonal > 0 ? Math.Asin(Math.Clamp(minMargin / diagonal, 0, 1)) * (180.0 / Math.PI) : 0;
        _cameraTargetAngle = Math.Clamp(_cameraTargetAngle, -maxAngle, maxAngle);
    }

    private static double Lerp(double current, double target, double t)
    {
        return current + (target - current) * t;
    }

    private void InjectCells()
    {
        // Count current population
        int totalCells = _gridW * _gridH;
        int alive = 0;
        for (int i = 0; i < totalCells; i++)
            if (_colorCurrent[i] != 0) alive++;

        double density = (double)alive / totalCells;

        // Inject more cells when population is low, fewer when crowded.
        // Scale by overscan area ratio so the larger grid gets proportionally more clusters.
        double densityFactor = Density / 5.0;
        double areaRatio = (_overscanW * _overscanH) / Math.Max(1, _displayW * _displayH);
        int clustersToAdd = (int)Math.Max(1, (density switch
        {
            < 0.04 => 8,
            < 0.08 => 6,
            < 0.12 => 4,
            < 0.16 => 3,
            < 0.20 => 2,
            < 0.24 => 1,
            _ => 1,
        }) * densityFactor * areaRatio);

        // Use a hue based on current time for variety
        double baseHue = (Environment.TickCount64 / 50.0) % 360.0;

        int marginX = Math.Max(2, (int)(_gridW * PlacementMargin));
        int marginY = Math.Max(2, (int)(_gridH * PlacementMargin));

        // When camera roam is active, bias 70% of injections toward the visible viewport
        bool biasToViewport = CameraRoam && _cameraZoom > 1.05;

        // Build a list of stagnant cell positions to use as injection targets.
        // Stagnant = alive in both snapshots (still-life or oscillator).
        List<int>? stagnantCells = null;
        if (_snapshotReady)
        {
            stagnantCells = new List<int>();
            int cellCount = _gridW * _gridH;
            for (int i = 0; i < cellCount; i++)
            {
                if (_snapshotA[i] && _snapshotB[i])
                    stagnantCells.Add(i);
            }
            if (stagnantCells.Count == 0)
                stagnantCells = null;
        }

        for (int s = 0; s < clustersToAdd; s++)
        {
            double hue = (baseHue + _rng.NextDouble() * 60.0 - 30.0) % 360.0;
            if (hue < 0) hue += 360.0;
            var color = HslToColor(hue, 0.9, 0.6);
            uint packed = PackColor(color);

            int cx, cy;

            // 60% chance to target a stagnant cell when available
            if (stagnantCells != null && _rng.NextDouble() < 0.6)
            {
                int pick = stagnantCells[_rng.Next(stagnantCells.Count)];
                cx = pick % _gridW;
                cy = pick / _gridW;
            }
            else if (biasToViewport && _rng.NextDouble() < 0.7)
            {
                // Place near the camera's current focus area
                int cellSize = Math.Max(1, CellSize);
                double viewCenterX = _gridW / 2.0 - _cameraPanX / cellSize;
                double viewCenterY = _gridH / 2.0 - _cameraPanY / cellSize;
                double viewRadiusX = _gridW / (_cameraZoom * 2.0);
                double viewRadiusY = _gridH / (_cameraZoom * 2.0);
                cx = (int)Math.Clamp(viewCenterX + (_rng.NextDouble() - 0.5) * 2.0 * viewRadiusX, marginX, _gridW - marginX - 1);
                cy = (int)Math.Clamp(viewCenterY + (_rng.NextDouble() - 0.5) * 2.0 * viewRadiusY, marginY, _gridH - marginY - 1);
            }
            else
            {
                cx = _rng.Next(marginX, _gridW - marginX);
                cy = _rng.Next(marginY, _gridH - marginY);
            }

            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (_rng.NextDouble() < 0.4) continue;
                int x = cx + dx, y = cy + dy;
                if (x >= 0 && x < _gridW && y >= 0 && y < _gridH)
                {
                    int idx = y * _gridW + x;
                    _colorCurrent[idx] = packed;
                    _age[idx] = 1;
                    _fade[idx] = 0;
                }
            }
        }
    }

    /// <summary>
    /// Pulses all living cells brighter for a few frames when the dominant color band changes.
    /// </summary>
    public override void PulseDominantColor(RoygbivColor band)
    {
        if (_disposed) return;

        var now = DateTime.UtcNow;
        if (now - _lastPulseTime < PulseCooldown) return;
        _lastPulseTime = now;

        _pulseBand = band;
        _pulseFramesRemaining = PulseFrameCount;
    }

    public override void ApplyAudioReactive(AudioReactiveData data, double baseIntensity, double reactiveSpeedMs)
    {
        if (_disposed) return;

        _audioReactiveActive = true;
        _lastLevel = data.Level;
        if (data.IsBeat)
            _pendingBeat = true;

        // Accumulate bass energy so quieter music still drives injection.
        // A sharp beat resets the accumulator; otherwise sustained bass
        // gradually fills it until it trips the threshold.
        if (data.IsBeat)
            _bassAccumulator = 0f;
        else
            _bassAccumulator += data.Bass * 0.08f;

        if (_bassAccumulator >= 1.0f)
        {
            _pendingBeat = true;
            _bassAccumulator = 0f;
        }

        // Gentle opacity modulation — Game of Life is a full-screen bitmap so
        // we keep it near full brightness with only a subtle bass-driven pulse.
        if (_image != null)
            _image.Opacity = Math.Clamp(0.85 + data.Bass * 0.15, 0.8, 1.0);
    }

    public override void ResetAudioReactive(double baseIntensity)
    {
        _audioReactiveActive = false;
        if (_image != null)
            _image.Opacity = 1.0;
    }

    public override void Exit(Action onComplete)
    {
        StopMotion();
        base.Exit(onComplete);
    }

    public override void Dispose()
    {
        StopMotion();
        _bitmap = null;
        _image = null;
        base.Dispose();
    }

    // ─── Helpers ──────────────────────────────────────────────

    private static uint PackColor(Color c) => 0xFF000000 | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    /// <summary>
    /// Fast check whether a cell's RGB color falls in the given ROYGBIV band.
    /// Uses integer-only max/min/delta to compute an approximate hue sector.
    /// </summary>
    private static bool CellMatchesBand(uint r, uint g, uint b, RoygbivColor band)
    {
        int ri = (int)r, gi = (int)g, bi = (int)b;
        int max = Math.Max(ri, Math.Max(gi, bi));
        int min = Math.Min(ri, Math.Min(gi, bi));
        int delta = max - min;

        // Low saturation → White band
        if (delta < 20 && max > 150)
            return band == RoygbivColor.White;
        if (delta < 5)
            return band == RoygbivColor.White;

        // Compute hue (0–360) using integer math
        int hue;
        if (max == ri)
            hue = 60 * (gi - bi) / delta;
        else if (max == gi)
            hue = 60 * (bi - ri) / delta + 120;
        else
            hue = 60 * (ri - gi) / delta + 240;
        if (hue < 0) hue += 360;

        var cellBand = (uint)hue switch
        {
            < 30 => RoygbivColor.Red,
            < 60 => RoygbivColor.Orange,
            < 90 => RoygbivColor.Yellow,
            < 180 => RoygbivColor.Green,
            < 210 => RoygbivColor.Blue,
            < 270 => RoygbivColor.Indigo,
            < 330 => RoygbivColor.Violet,
            _ => RoygbivColor.Red,
        };
        return cellBand == band;
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
            (byte)Math.Clamp((r + m) * 255, 0, 255),
            (byte)Math.Clamp((g + m) * 255, 0, 255),
            (byte)Math.Clamp((b + m) * 255, 0, 255));
    }
}
