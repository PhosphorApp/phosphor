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

    /// <summary>Maximum zoom level for camera roam (1.2–5.0). Default 1.6.</summary>
    public static double CameraMaxZoom { get; set; } = 1.6;

    /// <summary>Percentage of extra grid beyond the visible area (0–100). Default 50.</summary>
    public static int CameraOverscan { get; set; } = 50;

    /// <summary>Multiplier on camera pan/zoom/rotation animation speed (0.1–3.0). 1.0 = default.</summary>
    public static double CameraSpeed { get; set; } = 1.0;

    /// <summary>When true, restart the Game of Life simulation whenever a new track starts.</summary>
    public static bool RestartOnTrackChange { get; set; } = false;

    /// <summary>Bitmap scaling mode used when upscaling cells to screen size. Default NearestNeighbor.</summary>
    public static BitmapScalingMode ScalingMode { get; set; } = BitmapScalingMode.NearestNeighbor;

    /// <summary>
    /// Color model for new births.
    /// <list type="bullet">
    /// <item><c>Genetic</c> — inherit a blended RGB average of the three live parents.
    /// Produces collisions between regions that birth new mixed colors (e.g. red+yellow → orange).</item>
    /// <item><c>EraBanded</c> — take the simulation's current rotating-hue value. Survivors keep
    /// their birth color until death, so regions visually band by age ("color geology"). This
    /// mode unlocks future bitboard optimizations because color is no longer derived from neighbors.</item>
    /// </list>
    /// </summary>
    public enum ColorModeKind { Genetic = 0, EraBanded = 1 }

    /// <summary>Selected color model for births. Default <see cref="ColorModeKind.Genetic"/>.</summary>
    public static ColorModeKind ColorMode { get; set; } = ColorModeKind.Genetic;

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
    // Age is clamped at 3 — only the "<= 2" test in RenderRow (newborn brightness boost)
    // ever inspects it, so a single byte (vs. ushort) halves this array's memory bandwidth.
    private byte[] _age = [];              // how many generations this cell has been alive (clamped to 3)
    private byte[] _fade = [];           // remaining fade-out ticks (>0 = recently died, still rendering)
    private uint[] _fadeColor = [];      // color at moment of death, for fade rendering

    // --- Bitboard state (EraBanded mode only) ---------------------------------
    // Packed alive/dead bits, row-major. Bit b of word (y * _wordsPerRow + w)
    // represents the cell at column (w * 64 + b). Bits at positions past
    // (_gridW - 1) in the last word per row are ghost bits, always kept 0
    // via _lastWordMask. Allocated/used only when ColorMode == EraBanded.
    private ulong[] _aliveCurrent = [];
    private ulong[] _aliveNext = [];
    private int _wordsPerRow;            // ceil(_gridW / 64)
    private int _lastBit;                // (_gridW - 1) % 64 — last real bit in lastWord
    private ulong _lastWordMask;         // bits 0.._lastBit set, rest zero

    // Pool of per-thread (births, alive) sector counter buffers, reused
    // across Parallel.For calls in StepSimulationBitboard. Avoids the
    // 2 * sizeof(int[64]) allocation per worker per frame, which at
    // ~200+ fps produces measurable Gen0 pressure.
    private readonly System.Collections.Concurrent.ConcurrentBag<(int[] births, int[] alive)> _sectorBufferPool = new();

    // Reused buffer of stagnant cell indices, refilled in InjectCells.
    // Avoids a per-beat List<int> allocation on grids with hundreds of stagnant cells.
    private readonly List<int> _stagnantCells = new();

    // EraBanded color state: a global rotating hue used as the birth color for
    // every cell born this tick (and for seed/injection cells too). Survivors
    // keep their original birth color until death, so regions visually band by
    // age. Updated once per OnTick in UpdateBirthColor().
    private uint _currentBirthColor = 0xFFFFFFFF;
    private const double HueRotationPeriodMs = 60_000.0; // full ROYGBIV cycle in 60s

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

    // Frame timing diagnostics. Skip an initial warmup window (seeding,
    // JIT, first beat injections), then average across a measurement window
    // to smooth out beat-driven injections and dominant-brush throttling
    // cycles that make single-frame snapshots noisy. Logs once when the
    // window completes, then stops measuring (no per-frame overhead after).
    private readonly System.Diagnostics.Stopwatch _frameSw = new();
    private const int FrameTimingWarmupFrames = 500;
    private const int FrameTimingWindowFrames = 1000;
    private double _frameTimeSumMs;
    private double _frameTimeMinMs = double.MaxValue;
    private double _frameTimeMaxMs;
    private int _frameTimeSamples;
    private double[] _frameTimeSamplesMs = new double[FrameTimingWindowFrames];
    private bool _frameTimingReported;

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

    // Smoothed (EWMA) sector heat — averaged across recent frames so persistent
    // blooms dominate over single-frame noise. Updated every step in StepSimulation.
    private readonly double[] _sectorHeat = new double[SectorCountX * SectorCountY];
    // Smoothing factor: heat = heat*(1-α) + currentScore*α. Lower = more inertia.
    private const double SectorHeatAlpha = 0.08;

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
        _age = new byte[totalCells];
        _fade = new byte[totalCells];
        _fadeColor = new uint[totalCells];
        _sectorBirths = new int[SectorCountX * SectorCountY];
        _sectorAlive = new int[SectorCountX * SectorCountY];
        _snapshotA = new bool[totalCells];
        _snapshotB = new bool[totalCells];
        _snapshotGen = 0;
        _snapshotReady = false;

        // Bitboard layout: one ulong per 64 cells, padded per-row.
        // Allocated for both modes (cheap — 8 bytes per 64 cells = 1MB per 8M cells)
        // so we can switch modes at runtime without re-allocating.
        _wordsPerRow = (_gridW + 63) >> 6;
        _lastBit = (_gridW - 1) & 63;
        _lastWordMask = _lastBit == 63 ? ulong.MaxValue : ((1UL << (_lastBit + 1)) - 1);
        _aliveCurrent = new ulong[_wordsPerRow * _gridH];
        _aliveNext = new ulong[_wordsPerRow * _gridH];
        // Clear pool — grid size may have changed, old buffers are still
        // sized correctly (sectorCount is constant) but drop them anyway
        // to avoid retaining references after a settings restart.
        while (_sectorBufferPool.TryTake(out _)) { }

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

        // EraBanded mode: every seed cell shares the current rotating-hue color
        // (cells born at time T wear color(T)). Refresh once here since SeedGrid
        // runs before the first OnTick.
        bool useEraBanded = ColorMode == ColorModeKind.EraBanded;
        if (useEraBanded) UpdateBirthColor();

        for (int s = 0; s < count; s++)
        {
            uint packed;
            if (useEraBanded)
            {
                packed = _currentBirthColor;
            }
            else
            {
                double hue = _rng.NextDouble() * 360.0;
                packed = PackColor(HslToColor(hue, 0.9, 0.6));
            }

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
                    // Keep bitboard in sync so the EraBanded step sees the seed.
                    _aliveCurrent[y * _wordsPerRow + (x >> 6)] |= 1UL << (x & 63);
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

        // Advance the EraBanded rotating birth color once per tick so all
        // births this generation (Conway births, beat injections, periodic
        // injections) share a single hue — the defining property of the mode.
        UpdateBirthColor();

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
        if (!_frameTimingReported && _generationCount > FrameTimingWarmupFrames)
        {
            double ms = _frameSw.Elapsed.TotalMilliseconds;
            int i = _frameTimeSamples;
            _frameTimeSamplesMs[i] = ms;
            _frameTimeSumMs += ms;
            if (ms < _frameTimeMinMs) _frameTimeMinMs = ms;
            if (ms > _frameTimeMaxMs) _frameTimeMaxMs = ms;
            _frameTimeSamples = i + 1;

            if (_frameTimeSamples >= FrameTimingWindowFrames)
            {
                double mean = _frameTimeSumMs / _frameTimeSamples;
                // p95 via in-place sort (one-shot, ~1000 doubles, trivial cost).
                Array.Sort(_frameTimeSamplesMs, 0, _frameTimeSamples);
                double p95 = _frameTimeSamplesMs[(int)(_frameTimeSamples * 0.95)];
                double median = _frameTimeSamplesMs[_frameTimeSamples / 2];
                DebugLog.Log($"[GoL] Timing window frames {FrameTimingWarmupFrames + 1}–{FrameTimingWarmupFrames + FrameTimingWindowFrames}: " +
                    $"mean={mean:F2} ms  median={median:F2} ms  min={_frameTimeMinMs:F2} ms  max={_frameTimeMaxMs:F2} ms  p95={p95:F2} ms  " +
                    $"grid={_gridW}x{_gridH} ({_gridW * _gridH} cells)  mode={ColorMode}");
                _frameTimingReported = true;
                // Free the sample buffer — we're done measuring for this session.
                _frameTimeSamplesMs = Array.Empty<double>();
            }
        }
    }

    // Cells/row threshold above which we parallelize sim + render. Below this,
    // thread-pool dispatch overhead exceeds the work (e.g. cellSize=5 on 1080p
    // is ~83k cells — already worth it; cellSize=10 is ~21k — not).
    private const int ParallelCellThreshold = 50_000;

    /// <summary>
    /// Recompute <see cref="_currentBirthColor"/> from wall-clock time. The hue
    /// completes a full 360° rotation every <see cref="HueRotationPeriodMs"/>
    /// milliseconds. Called once per tick (and at seed time before the first
    /// tick) so every new cell born in the same tick gets exactly the same
    /// color in EraBanded mode.
    /// </summary>
    private void UpdateBirthColor()
    {
        double phase = (Environment.TickCount64 % (long)HueRotationPeriodMs) / HueRotationPeriodMs;
        double hue = phase * 360.0;
        _currentBirthColor = PackColor(HslToColor(hue, 0.9, 0.6));
    }

    private void StepSimulation()
    {
        // EraBanded mode uses a dedicated bitboard step: all live cells share
        // one global color per tick, so we don't need the per-cell parent-color
        // average that Genetic mode does. The bitboard step computes neighbor
        // counts 64 cells at a time with bit-parallel adders. See
        // PERFORMANCE_NOTES.md "Bitboard Simulation + EraBanded Color Mode".
        if (ColorMode == ColorModeKind.EraBanded)
        {
            StepSimulationBitboard();
            return;
        }

        // Capture the static ColorMode + per-tick birth color into locals so the
        // inner loop's birth branch can use them without re-reading statics.
        bool useEraBanded = false;
        uint eraBandedColor = _currentBirthColor;

        int w = _gridW, h = _gridH;
        int totalCells = w * h;
        // _colorNext is fully overwritten by StepRow (every cell writes either a color or 0),
        // so an explicit Array.Clear here is dead work — saves ~4MB of zeroing on a 2M-cell grid.
        Array.Clear(_sectorBirths);
        Array.Clear(_sectorAlive);
        int sectorW = Math.Max(1, w / SectorCountX);
        int sectorH = Math.Max(1, h / SectorCountY);
        int sectorCount = SectorCountX * SectorCountY;

        if (totalCells >= ParallelCellThreshold)
        {
            // Parallelize over rows. Each row writes only to its own indices
            // in _colorNext/_age/_fade/_fadeColor — disjoint, no locks needed.
            // Sector counters are accumulated thread-locally and merged once
            // per worker in localFinally.
            object mergeLock = new();
            Parallel.For(0, h,
                () => (births: new int[sectorCount], alive: new int[sectorCount]),
                (y, _, local) =>
                {
                    StepRow(y, w, h, sectorW, sectorH, local.births, local.alive, useEraBanded, eraBandedColor);
                    return local;
                },
                local =>
                {
                    lock (mergeLock)
                    {
                        for (int i = 0; i < sectorCount; i++)
                        {
                            _sectorBirths[i] += local.births[i];
                            _sectorAlive[i] += local.alive[i];
                        }
                    }
                });
        }
        else
        {
            for (int y = 0; y < h; y++)
                StepRow(y, w, h, sectorW, sectorH, _sectorBirths, _sectorAlive, useEraBanded, eraBandedColor);
        }

        // Swap buffers
        (_colorCurrent, _colorNext) = (_colorNext, _colorCurrent);

        // Update smoothed sector heat (EWMA). Persistent blooms accumulate weight;
        // fading regions decay. Drives camera target selection in PickCameraTarget.
        int sectorCount2 = SectorCountX * SectorCountY;
        for (int i = 0; i < sectorCount2; i++)
        {
            double instant = _sectorBirths[i] * 3 + _sectorAlive[i];
            _sectorHeat[i] = _sectorHeat[i] * (1.0 - SectorHeatAlpha) + instant * SectorHeatAlpha;
        }

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

    // ---------------------------------------------------------------------
    // Bitboard step (EraBanded only)
    // ---------------------------------------------------------------------
    // Layout: _aliveCurrent[y * _wordsPerRow + w] holds 64 cells, bit b at
    // column (w * 64 + b). Bits beyond _lastBit in the last word per row are
    // ghost bits; we mask them off in the final aliveNew per word.
    //
    // Per word we compute 9 shifted neighbor bit-vectors (uL/uM/uR, mL/mR,
    // dL/dM/dR), sum them via half/full adders into 4 bit-planes b0..b3
    // representing per-cell neighbor count (0..8), then apply Conway:
    //   aliveNext = (count==3) | (alive & (count==2 | count==3))
    // Births / deaths / survivors are then iterated as set bits in their
    // respective masks (sparse, O(active cells) per word), keeping the
    // color/age/fade arrays in sync.
    //
    // Toroidal wrap: column 0's left neighbor is column gridW-1, and column
    // gridW-1's right neighbor is column 0. The leftmost word handles wrap
    // for the left-shift carry-in; the rightmost (last) word handles wrap
    // for the right-shift carry-in. Interior words use pure word-to-word
    // carries with no wrap branches.

    private void StepSimulationBitboard()
    {
        int w = _gridW, h = _gridH;
        int totalCells = w * h;
        int wpr = _wordsPerRow;
        ulong lastMask = _lastWordMask;
        int lastWordIdx = wpr - 1;
        uint birthColor = _currentBirthColor;
        byte fadeStart = (byte)Math.Clamp(FadeGenerations, 1, 255);

        // NOTE: _colorNext clear and fade pre-decay are done per-row inside
        // StepRowBitboard so they run in parallel and stay hot in L1/L2.
        // The previous serial Array.Clear (32 MB) + fade pre-decay (16 MB)
        // were the dominant cost at 4K, drowning out the bitboard's compute
        // win. See PERFORMANCE_NOTES.md for the diagnosis.

        Array.Clear(_sectorBirths);
        Array.Clear(_sectorAlive);
        int sectorW = Math.Max(1, w / SectorCountX);
        int sectorH = Math.Max(1, h / SectorCountY);
        int sectorCount = SectorCountX * SectorCountY;
        int sectorCx = SectorCountX - 1;

        if (totalCells >= ParallelCellThreshold)
        {
            object mergeLock = new();
            Parallel.For(0, h,
                () =>
                {
                    // Pull a pre-allocated buffer pair from the pool, or
                    // allocate one on first use. Cleared here since the
                    // pool stores them in whatever state localFinally left.
                    if (_sectorBufferPool.TryTake(out var buf))
                    {
                        Array.Clear(buf.births);
                        Array.Clear(buf.alive);
                        return buf;
                    }
                    return (births: new int[sectorCount], alive: new int[sectorCount]);
                },
                (y, _, local) =>
                {
                    StepRowBitboard(y, w, h, wpr, lastWordIdx, lastMask,
                        sectorW, sectorH, sectorCx,
                        birthColor, fadeStart,
                        local.births, local.alive);
                    return local;
                },
                local =>
                {
                    lock (mergeLock)
                    {
                        for (int i = 0; i < sectorCount; i++)
                        {
                            _sectorBirths[i] += local.births[i];
                            _sectorAlive[i] += local.alive[i];
                        }
                    }
                    _sectorBufferPool.Add(local);
                });
        }
        else
        {
            for (int y = 0; y < h; y++)
                StepRowBitboard(y, w, h, wpr, lastWordIdx, lastMask,
                    sectorW, sectorH, sectorCx,
                    birthColor, fadeStart,
                    _sectorBirths, _sectorAlive);
        }

        (_colorCurrent, _colorNext) = (_colorNext, _colorCurrent);
        (_aliveCurrent, _aliveNext) = (_aliveNext, _aliveCurrent);

        for (int i = 0; i < sectorCount; i++)
        {
            double instant = _sectorBirths[i] * 3 + _sectorAlive[i];
            _sectorHeat[i] = _sectorHeat[i] * (1.0 - SectorHeatAlpha) + instant * SectorHeatAlpha;
        }

        _snapshotGen++;
        if (_snapshotGen >= SnapshotInterval)
        {
            _snapshotGen = 0;
            (_snapshotA, _snapshotB) = (_snapshotB, _snapshotA);
            for (int i = 0; i < totalCells; i++)
                _snapshotB[i] = _colorCurrent[i] != 0;
            _snapshotReady = true;
        }
    }

    /// <summary>
    /// Bitboard step for one row. Computes neighbor counts 64 cells at a
    /// time with bit-parallel half/full adders, then iterates the births /
    /// deaths / survivors masks sparsely to keep color/age/fade in sync.
    /// Writes only to this row's slice of <see cref="_aliveNext"/>,
    /// <see cref="_colorNext"/>, <see cref="_age"/>, <see cref="_fade"/>,
    /// and <see cref="_fadeColor"/> — safe to parallelize across rows.
    /// </summary>
    private void StepRowBitboard(int y, int w, int h, int wpr, int lastWordIdx, ulong lastMask,
        int sectorW, int sectorH, int sectorCx,
        uint birthColor, byte fadeStart,
        int[] sectorBirths, int[] sectorAlive)
    {
        int yUp = y == 0 ? h - 1 : y - 1;
        int yDn = y == h - 1 ? 0 : y + 1;
        int sy = Math.Min(y / sectorH, SectorCountY - 1);
        int sectorRowBase = sy * SectorCountX;

        int rowBase = y * w;
        int upWordBase = yUp * wpr;
        int midWordBase = y * wpr;
        int dnWordBase = yDn * wpr;

        // Per-row pre-passes (parallelized via the caller's Parallel.For):
        // 1) Clear this row's slice of _colorNext so dead-stay-dead cells
        //    read as 0 after the swap. Sparse births/survivors below
        //    overwrite the live cells. 15 KB for 4K width — hot in L1.
        // 2) Decrement fade counters for any cell with fade > 0. The death
        //    branch below resets to fadeStart for just-died cells, so the
        //    net effect matches the scalar path's per-cell fade-- semantics.
        Array.Clear(_colorNext, rowBase, w);
        for (int x = 0; x < w; x++)
        {
            byte f = _fade[rowBase + x];
            if (f > 0) _fade[rowBase + x] = (byte)(f - 1);
        }

        // Toroidal wrap carries for the leftmost word's left-shift come from
        // bit (gridW-1) of the row, i.e. bit _lastBit of the last word.
        int lastBit = (w - 1) & 63;
        ulong upWrapLeft = (_aliveCurrent[upWordBase + lastWordIdx] >> lastBit) & 1UL;
        ulong midWrapLeft = (_aliveCurrent[midWordBase + lastWordIdx] >> lastBit) & 1UL;
        ulong dnWrapLeft = (_aliveCurrent[dnWordBase + lastWordIdx] >> lastBit) & 1UL;

        // Toroidal wrap carries for the last word's right-shift come from
        // bit 0 of the first word per row.
        ulong upWrapRight = _aliveCurrent[upWordBase] & 1UL;
        ulong midWrapRight = _aliveCurrent[midWordBase] & 1UL;
        ulong dnWrapRight = _aliveCurrent[dnWordBase] & 1UL;

        for (int wi = 0; wi < wpr; wi++)
        {
            ulong upMid = _aliveCurrent[upWordBase + wi];
            ulong midMid = _aliveCurrent[midWordBase + wi];
            ulong dnMid = _aliveCurrent[dnWordBase + wi];

            // Left-shifted neighbor row (bit b receives original column c-1).
            ulong upL, midL, dnL;
            if (wi == 0)
            {
                upL = (upMid << 1) | upWrapLeft;
                midL = (midMid << 1) | midWrapLeft;
                dnL = (dnMid << 1) | dnWrapLeft;
            }
            else
            {
                upL = (upMid << 1) | (_aliveCurrent[upWordBase + wi - 1] >> 63);
                midL = (midMid << 1) | (_aliveCurrent[midWordBase + wi - 1] >> 63);
                dnL = (dnMid << 1) | (_aliveCurrent[dnWordBase + wi - 1] >> 63);
            }

            // Right-shifted neighbor row (bit b receives original column c+1).
            ulong upR, midR, dnR;
            if (wi == lastWordIdx)
            {
                upR = (upMid >> 1) | (upWrapRight << lastBit);
                midR = (midMid >> 1) | (midWrapRight << lastBit);
                dnR = (dnMid >> 1) | (dnWrapRight << lastBit);
            }
            else
            {
                upR = (upMid >> 1) | (_aliveCurrent[upWordBase + wi + 1] << 63);
                midR = (midMid >> 1) | (_aliveCurrent[midWordBase + wi + 1] << 63);
                dnR = (dnMid >> 1) | (_aliveCurrent[dnWordBase + wi + 1] << 63);
            }

            // Sum the three bits of the upper row (0..3, 2 bits).
            ulong uXor = upL ^ upMid;
            ulong uLo = uXor ^ upR;
            ulong uHi = (upL & upMid) | (upR & uXor);

            // Sum the two side bits of the middle row (0..2, 2 bits).
            ulong mLo = midL ^ midR;
            ulong mHi = midL & midR;

            // Sum the three bits of the lower row (0..3, 2 bits).
            ulong dXor = dnL ^ dnMid;
            ulong dLo = dXor ^ dnR;
            ulong dHi = (dnL & dnMid) | (dnR & dXor);

            // Combine the three 2-bit partials. Low bits first.
            ulong loXor = uLo ^ mLo;
            ulong b0 = loXor ^ dLo;
            ulong cLo = (uLo & mLo) | (dLo & loXor);

            // High bits sum + carry-in from low bits.
            ulong hiXor = uHi ^ mHi;
            ulong sHi = hiXor ^ dHi;
            ulong cHi = (uHi & mHi) | (dHi & hiXor);

            ulong b1 = sHi ^ cLo;
            ulong cHi2 = sHi & cLo;

            ulong b2 = cHi ^ cHi2;
            ulong b3 = cHi & cHi2;

            // Conway B3/S23: alive iff count==3, or alive and count in {2,3}.
            // count in {2,3} <=> b3=0, b2=0, b1=1. count==3 also has b0=1.
            ulong twoOrThree = b1 & ~b2 & ~b3;
            ulong three = twoOrThree & b0;
            ulong aliveNew = three | (midMid & twoOrThree);

            if (wi == lastWordIdx) aliveNew &= lastMask;

            _aliveNext[midWordBase + wi] = aliveNew;

            int xBase = wi << 6;

            // Births: dead -> alive. Use shared per-tick rotating hue.
            ulong births = aliveNew & ~midMid;
            while (births != 0)
            {
                int b = System.Numerics.BitOperations.TrailingZeroCount(births);
                births &= births - 1;
                int x = xBase + b;
                int idx = rowBase + x;
                _colorNext[idx] = birthColor;
                _age[idx] = 1;
                _fade[idx] = 0;
                int si = Math.Min(x / sectorW, sectorCx) + sectorRowBase;
                sectorBirths[si]++;
            }

            // Deaths: alive -> dead. Start fade-out from cell's last color.
            ulong deaths = midMid & ~aliveNew;
            while (deaths != 0)
            {
                int b = System.Numerics.BitOperations.TrailingZeroCount(deaths);
                deaths &= deaths - 1;
                int x = xBase + b;
                int idx = rowBase + x;
                _fadeColor[idx] = _colorCurrent[idx];
                _fade[idx] = fadeStart;
                _age[idx] = 0;
                // _colorNext[idx] already 0 from Array.Clear at step start.
            }

            // Survivors: alive -> alive. Carry color forward, bump age.
            ulong survivors = midMid & aliveNew;
            while (survivors != 0)
            {
                int b = System.Numerics.BitOperations.TrailingZeroCount(survivors);
                survivors &= survivors - 1;
                int x = xBase + b;
                int idx = rowBase + x;
                _colorNext[idx] = _colorCurrent[idx];
                if (_age[idx] < 3) _age[idx]++;
                _fade[idx] = 0;
                int si = Math.Min(x / sectorW, sectorCx) + sectorRowBase;
                sectorAlive[si]++;
            }
        }
    }

    /// <summary>
    /// Process one row of the Conway step. Writes only to its own row's
    /// indices in _colorNext/_age/_fade/_fadeColor — safe to call concurrently
    /// from multiple threads provided each row is owned by one worker.
    /// Sector counters are accumulated into the caller-supplied arrays so
    /// each thread can use its own buffers (merged in StepSimulation).
    /// </summary>
    private void StepRow(int y, int w, int h, int sectorW, int sectorH, int[] sectorBirths, int[] sectorAlive, bool useEraBanded, uint eraBandedColor)
    {
        int sy = Math.Min(y / sectorH, SectorCountY - 1);
        int yUp = y == 0 ? h - 1 : y - 1;
        int yDn = y == h - 1 ? 0 : y + 1;
        int rowBase = y * w;
        int upBase = yUp * w;
        int dnBase = yDn * w;
        int sectorRowBase = sy * SectorCountX;
        byte fadeStart = (byte)Math.Clamp(FadeGenerations, 1, 255);
        int sectorCx = SectorCountX - 1;

        // Edge column x=0 (left wraps to w-1)
        ProcessCell(0, w - 1, w > 1 ? 1 : 0, rowBase, upBase, dnBase, sectorW, sectorCx, sectorRowBase, fadeStart, sectorBirths, sectorAlive, useEraBanded, eraBandedColor);

        // Interior columns — no wrap branches; xL = x-1, xR = x+1 are always valid.
        // Most cells live here, so eliminating two branches per cell is a real win.
        for (int x = 1; x < w - 1; x++)
            ProcessCell(x, x - 1, x + 1, rowBase, upBase, dnBase, sectorW, sectorCx, sectorRowBase, fadeStart, sectorBirths, sectorAlive, useEraBanded, eraBandedColor);

        // Edge column x=w-1 (right wraps to 0). Skip when w==1 (already handled above).
        if (w > 1)
            ProcessCell(w - 1, w - 2, 0, rowBase, upBase, dnBase, sectorW, sectorCx, sectorRowBase, fadeStart, sectorBirths, sectorAlive, useEraBanded, eraBandedColor);
    }

    /// <summary>
    /// Inner Conway step for a single cell. Inlined into the StepRow loop by JIT.
    /// xL/xR are pre-resolved column indices so the inner loop has no wrap branches.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void ProcessCell(int x, int xL, int xR, int rowBase, int upBase, int dnBase,
        int sectorW, int sectorCx, int sectorRowBase, byte fadeStart, int[] sectorBirths, int[] sectorAlive,
        bool useEraBanded, uint eraBandedColor)
    {
        int idx = rowBase + x;

        int neighbors = 0;
        uint rSum = 0, gSum = 0, bSum = 0;

        // Eight neighbors, inlined (faster than a dx/dy loop and avoids
        // the dx==0&&dy==0 branch on the hot path).
        uint nc;
        nc = _colorCurrent[upBase + xL]; if (nc != 0) { neighbors++; rSum += (nc >> 16) & 0xFF; gSum += (nc >> 8) & 0xFF; bSum += nc & 0xFF; }
        nc = _colorCurrent[upBase + x ]; if (nc != 0) { neighbors++; rSum += (nc >> 16) & 0xFF; gSum += (nc >> 8) & 0xFF; bSum += nc & 0xFF; }
        nc = _colorCurrent[upBase + xR]; if (nc != 0) { neighbors++; rSum += (nc >> 16) & 0xFF; gSum += (nc >> 8) & 0xFF; bSum += nc & 0xFF; }
        nc = _colorCurrent[rowBase + xL]; if (nc != 0) { neighbors++; rSum += (nc >> 16) & 0xFF; gSum += (nc >> 8) & 0xFF; bSum += nc & 0xFF; }
        nc = _colorCurrent[rowBase + xR]; if (nc != 0) { neighbors++; rSum += (nc >> 16) & 0xFF; gSum += (nc >> 8) & 0xFF; bSum += nc & 0xFF; }
        nc = _colorCurrent[dnBase + xL]; if (nc != 0) { neighbors++; rSum += (nc >> 16) & 0xFF; gSum += (nc >> 8) & 0xFF; bSum += nc & 0xFF; }
        nc = _colorCurrent[dnBase + x ]; if (nc != 0) { neighbors++; rSum += (nc >> 16) & 0xFF; gSum += (nc >> 8) & 0xFF; bSum += nc & 0xFF; }
        nc = _colorCurrent[dnBase + xR]; if (nc != 0) { neighbors++; rSum += (nc >> 16) & 0xFF; gSum += (nc >> 8) & 0xFF; bSum += nc & 0xFF; }

        uint self = _colorCurrent[idx];

        if (self != 0)
        {
            if (neighbors == 2 || neighbors == 3)
            {
                _colorNext[idx] = self;
                // Age is clamped at 3 — anything ≥ 3 produces the same RenderRow result.
                if (_age[idx] < 3) _age[idx]++;
                _fade[idx] = 0;
                int si = Math.Min(x / sectorW, sectorCx) + sectorRowBase;
                sectorAlive[si]++;
            }
            else
            {
                _colorNext[idx] = 0;
                _fade[idx] = fadeStart;
                _fadeColor[idx] = self;
                _age[idx] = 0;
            }
        }
        else
        {
            if (neighbors == 3)
            {
                // EraBanded mode: every birth this tick uses the same rotating-hue
                // color, so regions visually band by age. Genetic mode: average
                // the three live parents' RGB (the current default behavior).
                uint birthColor;
                if (useEraBanded)
                {
                    birthColor = eraBandedColor;
                }
                else
                {
                    uint r = rSum / 3, g = gSum / 3, b = bSum / 3;
                    birthColor = 0xFF000000 | (r << 16) | (g << 8) | b;
                }
                _colorNext[idx] = birthColor;
                _age[idx] = 1;
                _fade[idx] = 0;
                int bi = Math.Min(x / sectorW, sectorCx) + sectorRowBase;
                sectorBirths[bi]++;
            }
            else
            {
                _colorNext[idx] = 0;
                if (_fade[idx] > 0) _fade[idx]--;
            }
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
            int w = _gridW, h = _gridH;
            int totalCells = w * h;
            IntPtr backBuffer = _bitmap.BackBuffer;

            if (totalCells >= ParallelCellThreshold)
            {
                // Each row writes disjoint pixels — safe to parallelize. Color
                // sums are accumulated thread-locally and merged in localFinally.
                object mergeLock = new();
                Parallel.For(0, h,
                    () => (r: 0L, g: 0L, b: 0L, n: 0),
                    (y, _, local) =>
                    {
                        RenderRow(y, w, backBuffer, pulsing, ref local.r, ref local.g, ref local.b, ref local.n);
                        return local;
                    },
                    local =>
                    {
                        lock (mergeLock)
                        {
                            totalR += local.r;
                            totalG += local.g;
                            totalB += local.b;
                            aliveCount += local.n;
                        }
                    });
            }
            else
            {
                for (int y = 0; y < h; y++)
                    RenderRow(y, w, backBuffer, pulsing, ref totalR, ref totalG, ref totalB, ref aliveCount);
            }

            _bitmap.AddDirtyRect(new Int32Rect(0, 0, _gridW, _gridH));
        }
        finally
        {
            _bitmap.Unlock();
        }

        // Update the dummy brush with the dominant on-screen color so the
        // color cycling timer can detect dominant band changes for DOF.
        // When camera roam is active and zoomed in, only sample the visible
        // grid region so DOF reflects what the user actually sees.
        //
        // Throttle to roughly once every ~2 seconds of wall-clock time. DOF
        // band-change events have a multi-second cooldown of their own, so
        // running the stride-2 visible-region scan + 8-bucket histogram at
        // anything close to the frame rate is wasted work.
        //
        // TickIntervalMs ranges 1–100 ms (user slider), or matches screen
        // refresh when GameOfLifeUseScreenRate is on (~7 ms on a 144 Hz
        // display, ~4 ms on 240 Hz). Compute interval from the actual tick
        // rate so the wall-clock cadence stays constant: e.g. 20 ticks at
        // 100 ms, ~285 ticks at 7 ms, 2000 ticks at 1 ms.
        int tickMs = Math.Max(1, TickIntervalMs);
        int interval = Math.Max(1, 2000 / tickMs);
        if (_generationCount % interval == 0)
            UpdateDominantBrush();
    }

    /// <summary>
    /// Scans live cells (whole grid, or only the visible region when camera
    /// roam is zoomed in) and sets <c>_brushes[0]</c> to the average color of
    /// the dominant ROYGBIV band. This drives the playfield's dominant-color
    /// detection and DOF lighting.
    /// </summary>
    private void UpdateDominantBrush()
    {
        if (_brushes.Count == 0) return;

        int x0 = 0, y0 = 0, x1 = _gridW, y1 = _gridH;
        if (CameraRoam && _cameraZoom > 1.05)
            (x0, y0, x1, y1) = GetVisibleGridRect();

        Span<int> bandCount = stackalloc int[8];
        Span<long> bandR = stackalloc long[8];
        Span<long> bandG = stackalloc long[8];
        Span<long> bandB = stackalloc long[8];

        // Stride 2 in both axes — dominant-band stats don't need every cell.
        const int stride = 2;
        for (int y = y0; y < y1; y += stride)
        {
            int rowBase = y * _gridW;
            for (int x = x0; x < x1; x += stride)
            {
                uint c = _colorCurrent[rowBase + x];
                if (c == 0) continue;
                uint cr = (c >> 16) & 0xFF;
                uint cg = (c >> 8) & 0xFF;
                uint cb = c & 0xFF;
                int band = ClassifyBand(cr, cg, cb);
                bandCount[band]++;
                bandR[band] += cr;
                bandG[band] += cg;
                bandB[band] += cb;
            }
        }

        int bestBand = 0, bestCount = 0;
        for (int b = 0; b < 8; b++)
            if (bandCount[b] > bestCount) { bestCount = bandCount[b]; bestBand = b; }

        if (bestCount > 0)
        {
            byte ar = (byte)(bandR[bestBand] / bestCount);
            byte ag = (byte)(bandG[bestBand] / bestCount);
            byte ab = (byte)(bandB[bestBand] / bestCount);
            _brushes[0].Color = Color.FromRgb(ar, ag, ab);
        }
    }

    /// <summary>
    /// Returns the axis-aligned bounding box (in grid cell indices) of the
    /// portion of the overscanned image currently visible on the display,
    /// given the camera's zoom, pan, and rotation. Half-open: [x0,x1) × [y0,y1).
    /// </summary>
    private (int x0, int y0, int x1, int y1) GetVisibleGridRect()
    {
        double zoom = Math.Max(0.001, _cameraZoom);
        double inv = 1.0 / zoom;
        double rad = _cameraAngle * Math.PI / 180.0;
        double cs = Math.Cos(rad), sn = Math.Sin(rad);
        double halfW = _displayW * 0.5;
        double halfH = _displayH * 0.5;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        // For each display-rect corner, invert the camera transform to get the
        // corresponding image-relative coordinate (origin at image center).
        // Forward: display = pan + zoom * R(angle) * imageRel
        // Inverse: imageRel = (1/zoom) * R(-angle) * (display - pan)
        for (int i = 0; i < 4; i++)
        {
            double dx = ((i & 1) == 0 ? -halfW : halfW);
            double dy = ((i & 2) == 0 ? -halfH : halfH);
            double ux = dx - _cameraPanX;
            double uy = dy - _cameraPanY;
            double rx = (cs * ux + sn * uy) * inv;
            double ry = (-sn * ux + cs * uy) * inv;
            if (rx < minX) minX = rx;
            if (ry < minY) minY = ry;
            if (rx > maxX) maxX = rx;
            if (ry > maxY) maxY = ry;
        }

        int cellSize = Math.Max(1, CellSize);
        int gx0 = (int)Math.Floor((minX + _overscanW * 0.5) / cellSize);
        int gy0 = (int)Math.Floor((minY + _overscanH * 0.5) / cellSize);
        int gx1 = (int)Math.Ceiling((maxX + _overscanW * 0.5) / cellSize);
        int gy1 = (int)Math.Ceiling((maxY + _overscanH * 0.5) / cellSize);

        gx0 = Math.Clamp(gx0, 0, _gridW);
        gy0 = Math.Clamp(gy0, 0, _gridH);
        gx1 = Math.Clamp(gx1, 0, _gridW);
        gy1 = Math.Clamp(gy1, 0, _gridH);
        if (gx1 <= gx0) gx1 = Math.Min(_gridW, gx0 + 1);
        if (gy1 <= gy0) gy1 = Math.Min(_gridH, gy0 + 1);
        return (gx0, gy0, gx1, gy1);
    }

    /// <summary>
    /// Fast integer ROYGBIV classification matching <see cref="CellMatchesBand"/>.
    /// Returns the band index (cast of <see cref="RoygbivColor"/>).
    /// </summary>
    private static int ClassifyBand(uint r, uint g, uint b)
    {
        int ri = (int)r, gi = (int)g, bi = (int)b;
        int max = Math.Max(ri, Math.Max(gi, bi));
        int min = Math.Min(ri, Math.Min(gi, bi));
        int delta = max - min;

        if (delta < 20 && max > 150) return (int)RoygbivColor.White;
        if (delta < 5) return (int)RoygbivColor.White;

        int hue;
        if (max == ri) hue = 60 * (gi - bi) / delta;
        else if (max == gi) hue = 60 * (bi - ri) / delta + 120;
        else hue = 60 * (ri - gi) / delta + 240;
        if (hue < 0) hue += 360;

        return (uint)hue switch
        {
            < 30 => (int)RoygbivColor.Red,
            < 60 => (int)RoygbivColor.Orange,
            < 90 => (int)RoygbivColor.Yellow,
            < 180 => (int)RoygbivColor.Green,
            < 210 => (int)RoygbivColor.Blue,
            < 270 => (int)RoygbivColor.Indigo,
            < 330 => (int)RoygbivColor.Violet,
            _ => (int)RoygbivColor.Red,
        };
    }

    /// <summary>
    /// Render one row of the bitmap. Writes only into its own row's pixels —
    /// safe to call concurrently from multiple threads. Color sums and alive
    /// count are accumulated through caller-supplied refs (which point at
    /// thread-local storage when parallelized).
    /// </summary>
    private unsafe void RenderRow(int y, int w, IntPtr backBuffer, bool pulsing,
        ref long totalR, ref long totalG, ref long totalB, ref int aliveCount)
    {
        uint* pixels = (uint*)backBuffer;
        int fadeMax = Math.Max(1, FadeGenerations);
        uint heat = (uint)Math.Clamp(HeatBoost, 0, 255);
        int rowBase = y * w;

        for (int x = 0; x < w; x++)
        {
            int i = rowBase + x;
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

                uint boost = _age[i] <= 2 ? heat : 0u;
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

    // ─── Camera Roam ──────────────────────────────────────────

    private void UpdateCamera()
    {
        if (!CameraRoam || _scaleTransform == null || _translateTransform == null || _rotateTransform == null)
            return;

        double maxZoom = Math.Clamp(CameraMaxZoom, 1.1, 5.0);
        double speed = Math.Clamp(CameraSpeed, 0.1, 3.0);

        // Always apply a very slow constant drift — gives the "floating in space" feel.
        // Drift is scaled by the speed multiplier so "Glacial" really crawls and "Fast" zips.
        _cameraTargetX += _cameraDriftX * speed;
        _cameraTargetY += _cameraDriftY * speed;
        _cameraTargetAngle += _cameraDriftAngle * speed;
        ClampCameraTarget();

        switch (_cameraState)
        {
            case CameraState.Settling:
                // Initial settle: slowly zoom in to a first target
                _cameraZoom = Lerp(_cameraZoom, _cameraTargetZoom, 0.00525 * speed);
                _cameraPanX = Lerp(_cameraPanX, _cameraTargetX, 0.00525 * speed);
                _cameraPanY = Lerp(_cameraPanY, _cameraTargetY, 0.00525 * speed);
                _cameraAngle = Lerp(_cameraAngle, _cameraTargetAngle, 0.00375 * speed);

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
                _cameraZoom = Lerp(_cameraZoom, _cameraTargetZoom, 0.00375 * speed);
                _cameraPanX = Lerp(_cameraPanX, _cameraTargetX, 0.00375 * speed);
                _cameraPanY = Lerp(_cameraPanY, _cameraTargetY, 0.00375 * speed);
                _cameraAngle = Lerp(_cameraAngle, _cameraTargetAngle, 0.002625 * speed);

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
    /// Pick a new camera wander target. Uses the smoothed <see cref="_sectorHeat"/>
    /// map (EWMA across recent frames) so persistent blooms dominate over
    /// single-frame noise. Selection is heat²-weighted random, then the actual
    /// centroid of live cells inside the chosen sector is computed so the
    /// camera lands on the bloom rather than at the sector midpoint.
    /// </summary>
    private void PickCameraTarget(double maxZoom)
    {
        int totalSectors = SectorCountX * SectorCountY;

        // Heat² weighting strongly biases toward the hottest sectors but keeps
        // a chance of visiting secondary blooms. Add a small uniform floor so
        // dead grids still pick *something* without dividing by zero.
        Span<double> weights = stackalloc double[totalSectors];
        double weightSum = 0;
        double maxHeat = 0;
        for (int i = 0; i < totalSectors; i++)
            if (_sectorHeat[i] > maxHeat) maxHeat = _sectorHeat[i];

        double floor = Math.Max(1e-6, maxHeat * 0.02); // 2% floor relative to peak
        for (int i = 0; i < totalSectors; i++)
        {
            double h = _sectorHeat[i] + floor;
            double w = h * h;
            weights[i] = w;
            weightSum += w;
        }

        // Weighted random pick
        double r = _rng.NextDouble() * weightSum;
        int bestIdx = 0;
        double acc = 0;
        for (int i = 0; i < totalSectors; i++)
        {
            acc += weights[i];
            if (r <= acc) { bestIdx = i; break; }
        }

        // Compute actual centroid of live cells inside the chosen sector so we
        // aim at the bloom, not the sector midpoint. Fall back to the midpoint
        // if the sector is empty (which can happen with EWMA inertia).
        int sectorX = bestIdx % SectorCountX;
        int sectorY = bestIdx / SectorCountX;
        double fracX = (sectorX + 0.5) / SectorCountX;
        double fracY = (sectorY + 0.5) / SectorCountY;
        ComputeSectorCentroid(sectorX, sectorY, ref fracX, ref fracY);

        // Gently vary zoom — wander between ~60% and 100% of max zoom so it stays interesting
        double minWander = 1.0 + (maxZoom - 1.0) * 0.5;
        _cameraTargetZoom = minWander + _rng.NextDouble() * (maxZoom - minWander);

        // Pan offset: move the target sector toward the viewport center
        double imgCenterFracX = fracX - 0.5; // -0.5..+0.5
        double imgCenterFracY = fracY - 0.5;
        _cameraTargetX = -imgCenterFracX * _overscanW * (_cameraTargetZoom - 1.0) * 0.8;
        _cameraTargetY = -imgCenterFracY * _overscanH * (_cameraTargetZoom - 1.0) * 0.8;

        // Compute the maximum safe rotation angle given zoom, overscan, and pan offset.
        double marginX = Math.Max(0, (_overscanW * _cameraTargetZoom - _displayW) / 2.0 - Math.Abs(_cameraTargetX));
        double marginY = Math.Max(0, (_overscanH * _cameraTargetZoom - _displayH) / 2.0 - Math.Abs(_cameraTargetY));
        double minMargin = Math.Min(marginX, marginY);
        double diagonal = Math.Sqrt(_displayW * _displayW + _displayH * _displayH) / 2.0;
        double maxAngle = diagonal > 0 ? Math.Asin(Math.Clamp(minMargin / diagonal, 0, 1)) * (180.0 / Math.PI) : 0;

        _cameraTargetAngle = (_rng.NextDouble() - 0.5) * 2.0 * maxAngle;

        // Set a new gentle drift direction — keeps the camera floating between retargets
        _cameraDriftX = (_rng.NextDouble() - 0.5) * 0.165;
        _cameraDriftY = (_rng.NextDouble() - 0.5) * 0.165;
        _cameraDriftAngle = (_rng.NextDouble() - 0.5) * 0.009 * Math.Max(1.0, maxAngle / 15.0);

        ClampCameraTarget();
    }

    /// <summary>
    /// Find the weighted centroid of live cells inside a single sector and
    /// convert it to a fractional position on the image (0..1). Falls back to
    /// the sector midpoint (preserving the input values) if the sector has no
    /// live cells. Sampled (every Nth cell) so the cost stays trivial even on
    /// 2M+ cell grids — accuracy doesn't need to be pixel-perfect for camera aim.
    /// </summary>
    private void ComputeSectorCentroid(int sectorX, int sectorY, ref double fracX, ref double fracY)
    {
        int w = _gridW, h = _gridH;
        if (w == 0 || h == 0) return;

        int sectorW = Math.Max(1, w / SectorCountX);
        int sectorH = Math.Max(1, h / SectorCountY);
        int x0 = sectorX * sectorW;
        int y0 = sectorY * sectorH;
        int x1 = sectorX == SectorCountX - 1 ? w : Math.Min(w, x0 + sectorW);
        int y1 = sectorY == SectorCountY - 1 ? h : Math.Min(h, y0 + sectorH);

        // Stride to keep this under ~4k samples per sector regardless of grid size
        int cellsInSector = (x1 - x0) * (y1 - y0);
        int stride = Math.Max(1, (int)Math.Sqrt(cellsInSector / 4096.0));

        long sumX = 0, sumY = 0, count = 0;
        for (int y = y0; y < y1; y += stride)
        {
            int rowBase = y * w;
            for (int x = x0; x < x1; x += stride)
            {
                if (_colorCurrent[rowBase + x] != 0)
                {
                    sumX += x;
                    sumY += y;
                    count++;
                }
            }
        }

        if (count > 0)
        {
            fracX = (sumX / (double)count) / w;
            fracY = (sumY / (double)count) / h;
        }
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
        // Single pass: count population AND collect stagnant cells (alive in both
        // snapshots) so we don't walk the full grid twice on big simulations.
        int totalCells = _gridW * _gridH;
        int alive = 0;
        _stagnantCells.Clear();
        if (_snapshotReady)
        {
            for (int i = 0; i < totalCells; i++)
            {
                if (_colorCurrent[i] != 0) alive++;
                if (_snapshotA[i] && _snapshotB[i]) _stagnantCells.Add(i);
            }
        }
        else
        {
            for (int i = 0; i < totalCells; i++)
                if (_colorCurrent[i] != 0) alive++;
        }

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

        // Use a hue based on current time for variety (Genetic mode). In
        // EraBanded mode every cluster this tick shares the global birth color.
        bool useEraBanded = ColorMode == ColorModeKind.EraBanded;
        double baseHue = (Environment.TickCount64 / 50.0) % 360.0;

        int marginX = Math.Max(2, (int)(_gridW * PlacementMargin));
        int marginY = Math.Max(2, (int)(_gridH * PlacementMargin));

        // When camera roam is active, bias 70% of injections toward the visible viewport
        bool biasToViewport = CameraRoam && _cameraZoom > 1.05;

        // Stagnant cell list was populated in the alive-count pass above.
        bool hasStagnant = _stagnantCells.Count > 0;

        for (int s = 0; s < clustersToAdd; s++)
        {
            uint packed;
            if (useEraBanded)
            {
                packed = _currentBirthColor;
            }
            else
            {
                double hue = (baseHue + _rng.NextDouble() * 60.0 - 30.0) % 360.0;
                if (hue < 0) hue += 360.0;
                var color = HslToColor(hue, 0.9, 0.6);
                packed = PackColor(color);
            }

            int cx, cy;

            // 60% chance to target a stagnant cell when available
            if (hasStagnant && _rng.NextDouble() < 0.6)
            {
                int pick = _stagnantCells[_rng.Next(_stagnantCells.Count)];
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
                    // Keep bitboard in sync so the EraBanded step sees the injection.
                    _aliveCurrent[y * _wordsPerRow + (x >> 6)] |= 1UL << (x & 63);
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
