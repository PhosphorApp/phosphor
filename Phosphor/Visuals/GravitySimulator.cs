using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;

namespace Phosphor;

/// <summary>
/// N-body gravitational simulation. Bodies attract each other, merge on aligned
/// collisions, and split on opposing collisions. A soft boundary force keeps
/// bodies on-screen. Dust particles are injected when population drops too low.
/// </summary>
public sealed class GravitySimulator : IDisposable
{
    // --- Tuning constants ---
    private const double Softening = 12.0;         // softening length to prevent singularities
    private const double SofteningSq = Softening * Softening;
    private const double BoundaryMargin = 40.0;    // px from edge where force kicks in
    private const double BoundaryStrength = 600.0; // force pushing bodies back on-screen
    private const double RepulsionRadiiFactor = 3.0;  // repulsion activates within this × combined radii
    private const double PierceProbability = 0.15;  // chance a qualifying impact pierces instead of merging
    private const double MergeAlignmentThreshold = 0.3;  // dot product threshold (>0 = same-ish direction)
    private const double SplitSpeedThreshold = 120.0;    // relative speed above which opposing collisions split
    private const double MassRatioMergeThreshold = 4.0;  // if larger/smaller mass ratio exceeds this, always merge
    private const int MaxBodiesDefault = 100;
    private const double DustSize = 6.0;
    private const double MaxDt = 0.033;            // clamp dt to ~30fps minimum
    private const double MaxVelocity = 400.0;       // clamp velocity magnitude to prevent teleporting
    private const double DampingPerSecond = 0.92;     // fraction of velocity retained per second (time-based, frame-rate independent)
    private const double SizeLerpSpeed = 4.0;        // how fast merge size animates (per second)
    private const double PostMergeMinSpeed = 30.0;    // minimum speed after a merge to prevent dead stops
    private const double PerturbationBase = 8.0;      // base tangential acceleration per unit of OrbitalPerturbation
    private const double CometTrailChance = 0.75;       // probability that dust injection spawns a comet trail instead
    private const double DiagDriftRadiusX = 25.0;       // horizontal drift radius for OLED burn-in prevention
    private const double DiagDriftRadiusY = 20.0;       // vertical drift radius
    private const double DiagDriftPeriodSec = 120.0;    // seconds for one full elliptical loop

    // Black hole lifecycle
    private const double BlackHoleMinLifeSec = 12.0;    // minimum time before a capped hole enters the quasar phase
    private const double BlackHoleMaxLifeSec = 30.0;    // hard cap on black-hole lifetime regardless of size
    private const double QuasarDurationSec = 6.0;       // length of the quasar (polar jet + fade) end-state
    private const double QuasarJetIntervalSec = 0.25;   // time between jet ejections during the quasar phase
    private const double SpaghettiRadiusFactor = 3.0;   // lensing/stretch reach as a multiple of black-hole radius
    private const double SpaghettiMaxStretch = 2.2;     // max radial elongation applied to an infalling blob

    private readonly Canvas _canvas;
    private readonly List<FrameworkElement> _blobs;
    private readonly List<BlobState> _states;
    private readonly List<SolidColorBrush> _brushes;
    private readonly List<RadialGradientBrush> _gradBrushes;
    private readonly double _intensity;
    private readonly double _speedMultiplier;
    private readonly int _minBodies;
    private readonly int _maxBodies;
    private readonly bool _useBitmapCache;
    private int _dustCooldown;
    private readonly Random _rng = new();

    // --- Cached per-frame arrays (avoid O(N²) dependency-property reads) ---
    private double[] _posX = [];   // center X of each body
    private double[] _posY = [];   // center Y of each body
    private double[] _radii = [];  // half-width of each body
    private double[] _masses = []; // mass (width²) of each body
    private int _cachedCount;      // body count at time of cache (safe upper bound for array access)
    private readonly Stopwatch _stopwatch = new();
    private long _lastTickTicks;
    private bool _running;
    private int _consecutiveFrameErrors;
    private const int MaxConsecutiveFrameErrors = 5;

    // --- Diagnostics ---
    private System.Windows.Controls.TextBlock? _diagLabel;
    private System.Windows.Controls.Panel? _diagParent;

    // --- Camera roam state ---
    private ScaleTransform? _cameraScale;
    private RotateTransform? _cameraRotate;
    private TranslateTransform? _cameraTranslate;
    private double _cameraZoom = 1.0;
    private double _cameraAngle = 0.0;
    private double _cameraOffsetX = 0.0;
    private double _cameraOffsetY = 0.0;
    private double _cameraTargetAngle = 0.0;
    private double _cameraDriftTimer = 0.0;
    private const double SimSpaceMultiplier = 3.0;    // sim space is 3× canvas dimensions
    private const double CameraMinZoom = 1.0 / SimSpaceMultiplier;

    /// <summary>Maximum camera zoom-in factor. Default 1.3.</summary>
    public static double CameraMaxZoom { get; set; } = 1.3;

    /// <summary>Interval (seconds) between random rotation drift target picks. Default 20.</summary>
    public static double CameraDriftIntervalSec { get; set; } = 20.0;

    /// <summary>Per-second convergence for zoom. Lower = slower/gentler zoom. Default 0.1.</summary>
    public static double CameraZoomLerpSpeed { get; set; } = 0.1;

    /// <summary>Per-second convergence for pan (center-of-mass follow). Very slow to be barely perceptible. Default 0.02.</summary>
    public static double CameraPanLerpSpeed { get; set; } = 0.02;

    /// <summary>Per-second convergence for rotation drift. Default 0.3.</summary>
    public static double CameraDriftLerpSpeed { get; set; } = 0.3;

    /// <summary>Fraction of total mass to keep in frame when computing zoom. Default 0.80.</summary>
    public static double CameraMassFraction { get; set; } = 0.80;

    /// <summary>Duration (seconds) of the color fade when two bodies merge. 0 = instant. Default 0.5.</summary>
    public static double MergeColorFadeSec { get; set; } = 2.5;

    /// <summary>Duration (seconds) of the color fade-in for spawned fragments (split/pierce/supernova/comet). 0 = instant. Default 0.3.</summary>
    public static double SpawnColorFadeSec { get; set; } = 0.3;

    /// <summary>Very slow global hue drift applied to each body's own color, in degrees per second. 0 = off. Default 1.0 (full rotation every 6 minutes).</summary>
    public static double HueDriftDegreesPerSec { get; set; } = 1.0;

    /// <summary>Radial gradient mid-stop offset (0–1). Lower = softer/fuzzier edge falloff. Default 0.55.</summary>
    public static double GradientMidStop { get; set; } = 0.55;

    /// <summary>Radial gradient mid-stop alpha (0–255). Lower = more translucent. Default 200.</summary>
    public static byte GradientMidAlpha { get; set; } = 200;

    /// <summary>Extra scale applied to a body when it collides, as a fraction (0 = off). Default 0.25 (+25%).</summary>
    public static double CollisionPulseScale { get; set; } = 0.25;

    /// <summary>Duration (seconds) of the collision pulse decay. Default 0.35.</summary>
    public static double CollisionPulseSec { get; set; } = 0.35;

    public GravitySimulator(
        List<FrameworkElement> blobs,
        List<BlobState> states,
        List<SolidColorBrush> brushes,
        List<RadialGradientBrush> gradBrushes,
        Canvas canvas,
        double intensity,
        double speedMultiplier,
        bool useBitmapCache = true)
    {
        _blobs = blobs;
        _states = states;
        _maxBodies = Math.Max(10, (int)(MaxBodiesDefault * GravityBlobPattern.BlobMultiplier));
        double densityFactor = GravityBlobPattern.Density switch { 0 => 1.0 / 3, 2 => 1.0 / 1.5, _ => 1.0 / 2 };
        _minBodies = Math.Max(5, (int)(_maxBodies * densityFactor));
        _brushes = brushes;
        _gradBrushes = gradBrushes;
        _canvas = canvas;
        _intensity = intensity;
        _speedMultiplier = Math.Max(0.1, speedMultiplier);
        _useBitmapCache = useBitmapCache;

        // Set up camera roam
        if (GravityBlobPattern.CameraRoam)
        {
            // Disable clipping so the camera can zoom out to reveal the full sim space
            canvas.ClipToBounds = false;
            _cameraTranslate = new TranslateTransform(0, 0);
            _cameraScale = new ScaleTransform(1.0, 1.0, canvas.ActualWidth * 0.5, canvas.ActualHeight * 0.5);
            _cameraRotate = new RotateTransform(0.0, canvas.ActualWidth * 0.5, canvas.ActualHeight * 0.5);
            var group = new TransformGroup();
            group.Children.Add(_cameraTranslate);
            group.Children.Add(_cameraScale);
            group.Children.Add(_cameraRotate);
            canvas.RenderTransform = group;
            _cameraDriftTimer = 0;
            PickDriftTarget();
        }

        // Clear any WPF animations so we can set positions directly
        for (int i = 0; i < _blobs.Count; i++)
        {
            _blobs[i].BeginAnimation(Canvas.LeftProperty, null);
            _blobs[i].BeginAnimation(Canvas.TopProperty, null);
            if (i < _states.Count && _states[i].BaseOpacity <= 0)
                _states[i].BaseOpacity = _blobs[i].Opacity;
        }
    }

    public void Start()
    {
        _stopwatch.Restart();
        _lastTickTicks = _stopwatch.ElapsedTicks;
        _running = true;

        if (GravityBlobPattern.ShowDiagnostics)
        {
            _diagLabel = new System.Windows.Controls.TextBlock
            {
                FontSize = 16,
                Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Margin = new Thickness(12, 0, 0, 12),
            };
            // Add to parent panel (Grid) so it's not affected by camera transform
            _diagParent = _canvas.Parent as System.Windows.Controls.Panel;
            if (_diagParent != null)
            {
                System.Windows.Controls.Panel.SetZIndex(_diagLabel, 9999);
                _diagParent.Children.Add(_diagLabel);
            }
            else
            {
                // Fallback: add to canvas (will move with camera)
                Canvas.SetLeft(_diagLabel, 12);
                Canvas.SetBottom(_diagLabel, 12);
                _canvas.Children.Add(_diagLabel);
            }
        }

        CompositionTarget.Rendering += OnRendering;
    }

    public void Dispose()
    {
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
        _stopwatch.Stop();

        if (_diagLabel != null)
        {
            if (_diagParent != null)
                _diagParent.Children.Remove(_diagLabel);
            else
                _canvas.Children.Remove(_diagLabel);
            _diagLabel = null;
            _diagParent = null;
        }

        // Reset camera transform
        if (_cameraScale != null)
        {
            _canvas.RenderTransform = null;
            _canvas.ClipToBounds = true;
            _cameraScale = null;
            _cameraRotate = null;
            _cameraTranslate = null;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_running) return;
        try
        {
            RenderFrame();
            _consecutiveFrameErrors = 0; // a clean frame resets the failure streak
        }
        catch (Exception ex)
        {
            _consecutiveFrameErrors++;
            DebugLog.LogException(
                $"GravitySimulator.RenderFrame (frame error {_consecutiveFrameErrors}/{MaxConsecutiveFrameErrors}, bodies={_blobs.Count})",
                ex);

            if (_consecutiveFrameErrors >= MaxConsecutiveFrameErrors)
            {
                DebugLog.Log(LogLevel.Warning, "GravitySimulator",
                    "Too many consecutive frame errors — restarting the gravity simulation.");
                RestartSimulation();
            }
        }
    }

    /// <summary>
    /// Tears the simulation down and rebuilds a fresh field. Used as a last-resort recovery
    /// when the render loop hits repeated unrecoverable errors.
    /// </summary>
    private void RestartSimulation()
    {
        try
        {
            _consecutiveFrameErrors = 0;

            // Remove all current bodies from the canvas and clear state.
            for (int i = _blobs.Count - 1; i >= 0; i--)
            {
                if (i < _blobs.Count)
                {
                    if (_blobs[i] is Shape sh) sh.Effect = null;
                    _canvas.Children.Remove(_blobs[i]);
                }
            }
            _blobs.Clear();
            _states.Clear();
            _brushes.Clear();
            _gradBrushes.Clear();
            _cachedCount = 0;
            _dustCooldown = 0;

            // Reset the physics caches so stale bounds can't be reused.
            _posX = [];
            _posY = [];
            _radii = [];
            _masses = [];

            // Reseed a fresh field via the normal dust-injection path.
            double cw = Math.Max(1, _canvas.ActualWidth);
            double ch = Math.Max(1, _canvas.ActualHeight);
            int seed = Math.Max(_minBodies, 12);
            for (int i = 0; i < seed && _blobs.Count < _maxBodies; i++)
                InjectDust(cw, ch);

            _lastTickTicks = _stopwatch.ElapsedTicks;
        }
        catch (Exception ex)
        {
            // If even the restart fails, stop the loop to avoid a crash storm.
            DebugLog.LogException("GravitySimulator.RestartSimulation (giving up, stopping loop)", ex);
            _running = false;
        }
    }

    private void RenderFrame()
    {
        long nowTicks = _stopwatch.ElapsedTicks;
        double dt = Math.Min((double)(nowTicks - _lastTickTicks) / Stopwatch.Frequency, MaxDt);
        _lastTickTicks = nowTicks;
        if (dt <= 0) return;

        int count = Math.Min(_blobs.Count, _states.Count);
        double cw = Math.Max(1, _canvas.ActualWidth);
        double ch = Math.Max(1, _canvas.ActualHeight);

        // --- Cache positions, radii, masses from WPF elements (one read per body) ---
        if (_posX.Length < count)
        {
            _posX = new double[count + 64];
            _posY = new double[count + 64];
            _radii = new double[count + 64];
            _masses = new double[count + 64];
        }
        for (int i = 0; i < count; i++)
        {
            double r = _blobs[i].Width * 0.5;
            _posX[i] = Canvas.GetLeft(_blobs[i]) + r;
            _posY[i] = Canvas.GetTop(_blobs[i]) + r;
            _radii[i] = r;
            _masses[i] = _blobs[i].Width * _blobs[i].Width;
        }
        _cachedCount = count;

        // --- Compute gravitational accelerations ---
        // Store accelerations in temporary arrays to avoid N² position reads
        Span<double> ax = stackalloc double[count];
        Span<double> ay = stackalloc double[count];

        for (int i = 0; i < count; i++)
        {
            double xi = _posX[i];
            double yi = _posY[i];
            double mi = _masses[i];

            for (int j = i + 1; j < count; j++)
            {
                // Skip gravitational interaction if either body is immune from the other (comet trail)
                if (_states[i].GravityImmuneFrom == _blobs[j] || _states[j].GravityImmuneFrom == _blobs[i])
                    continue;

                double xj = _posX[j];
                double yj = _posY[j];
                double mj = _masses[j];

                double dx = xj - xi;
                double dy = yj - yi;
                double distSq = dx * dx + dy * dy + SofteningSq;
                double dist = Math.Sqrt(distSq);
                double force = GravityBlobPattern.GravityG * mi * mj / distSq;

                // Close-range repulsion to encourage stable orbits
                double ri = _radii[i];
                double rj = _radii[j];
                double repulsionDist = (ri + rj) * RepulsionRadiiFactor;
                if (dist < repulsionDist)
                {
                    // Ramps from 0 at repulsionDist to full at contact
                    double t = 1.0 - dist / repulsionDist;
                    force -= GravityBlobPattern.GravityG * mi * mj / distSq * GravityBlobPattern.OrbitRepulsion * t * t;
                }

                double fx = force * dx / dist;
                double fy = force * dy / dist;

                // a = F/m
                ax[i] += fx / mi;
                ay[i] += fy / mi;
                ax[j] -= fx / mj;
                ay[j] -= fy / mj;
            }

            // Soft boundary force
            ApplyBoundaryForce(xi, yi, cw, ch, ref ax[i], ref ay[i]);

            // Gentle central gravity — nudge bodies toward canvas center
            double cdx = cw * 0.5 - xi;
            double cdy = ch * 0.5 - yi;
            double cdist = Math.Sqrt(cdx * cdx + cdy * cdy + 1.0);
            ax[i] += GravityBlobPattern.CentralGravity * cdx / cdist;
            ay[i] += GravityBlobPattern.CentralGravity * cdy / cdist;

            // Continuous orbital perturbation: tangential nudge perpendicular to
            // the center vector to keep bodies swirling instead of falling static.
            // Signed: negative swirls clockwise, positive counter-clockwise.
            double pertStr = GravityBlobPattern.OrbitalPerturbation;
            if (pertStr != 0 && cdist > 1.0)
            {
                // Tangent direction (perpendicular to center vector)
                double tx = -cdy / cdist;
                double ty = cdx / cdist;
                // Strength (and direction, via sign) scales with distance and setting
                double pertForce = PerturbationBase * pertStr * Math.Min(cdist / (cw * 0.3), 1.0);
                ax[i] += tx * pertForce;
                ay[i] += ty * pertForce;
            }
        }

        // --- Integrate velocities and positions (Euler) ---
        for (int i = 0; i < count; i++)
        {
            var s = _states[i];
            s.VelocityX += ax[i] * dt * _speedMultiplier;
            s.VelocityY += ay[i] * dt * _speedMultiplier;

            // Time-based drag (frame-rate independent): retain DampingPerSecond of velocity each second
            double damping = Math.Pow(DampingPerSecond, dt);
            s.VelocityX *= damping;
            s.VelocityY *= damping;

            // Clamp velocity magnitude
            double speed = Math.Sqrt(s.VelocityX * s.VelocityX + s.VelocityY * s.VelocityY);
            if (speed > MaxVelocity)
            {
                double scale = MaxVelocity / speed;
                s.VelocityX *= scale;
                s.VelocityY *= scale;
            }

            double x = _posX[i] - _radii[i] + s.VelocityX * dt;
            double y = _posY[i] - _radii[i] + s.VelocityY * dt;

            Canvas.SetLeft(_blobs[i], x);
            Canvas.SetTop(_blobs[i], y);

            // Update cache for collision detection
            _posX[i] = x + _radii[i];
            _posY[i] = y + _radii[i];

            // Animated size growth after merge
            if (!double.IsNaN(s.MergeTargetSize))
            {
                double curSize = _blobs[i].Width;
                double lerpSize = Math.Min(1.0, SizeLerpSpeed * dt);
                double newSize = curSize + (s.MergeTargetSize - curSize) * lerpSize;
                _blobs[i].Width = newSize;
                _blobs[i].Height = newSize;
                _radii[i] = newSize * 0.5;
                _masses[i] = newSize * newSize;

                if (Math.Abs(s.MergeTargetSize - newSize) < 0.5)
                {
                    _blobs[i].Width = s.MergeTargetSize;
                    _blobs[i].Height = s.MergeTargetSize;
                    _radii[i] = s.MergeTargetSize * 0.5;
                    _masses[i] = s.MergeTargetSize * s.MergeTargetSize;
                    s.MergeTargetSize = double.NaN;
                }
            }

            // Decay merge immunity
            if (s.MergeImmunity > 0)
            {
                s.MergeImmunity = Math.Max(0, s.MergeImmunity - dt);
                if (s.MergeImmunity <= 0)
                    s.GravityImmuneFrom = null;
            }

            // Animated color fade after a merge (black holes keep their black gradient)
            if (s.ColorFadeRemaining > 0 && !s.IsBlackHole)
            {
                s.ColorFadeRemaining = Math.Max(0, s.ColorFadeRemaining - dt);
                double t = s.ColorFadeDuration > 0
                    ? 1.0 - (s.ColorFadeRemaining / s.ColorFadeDuration)
                    : 1.0;
                Color from = s.ColorFadeFrom;
                Color to = s.ColorFadeTo;
                byte cr = (byte)(from.R + (to.R - from.R) * t);
                byte cg = (byte)(from.G + (to.G - from.G) * t);
                byte cb = (byte)(from.B + (to.B - from.B) * t);
                Color cur = Color.FromRgb(cr, cg, cb);
                RecolorGradient(i, cur);
            }

            // Collision pulse: brief scale bump that eases back to 1.0
            if (s.CollisionPulseRemaining > 0 && CollisionPulseSec > 0)
            {
                s.CollisionPulseRemaining = Math.Max(0, s.CollisionPulseRemaining - dt);
                double p = s.CollisionPulseRemaining / CollisionPulseSec; // 1 -> 0
                double scale = 1.0 + CollisionPulseScale * p;
                if (_blobs[i].RenderTransform is not ScaleTransform pst)
                {
                    pst = new ScaleTransform(scale, scale);
                    _blobs[i].RenderTransform = pst;
                }
                else
                {
                    pst.ScaleX = scale;
                    pst.ScaleY = scale;
                }
            }

            // Black hole lifecycle (growth cap, aging, quasar end-state)
            if (s.IsBlackHole)
                UpdateBlackHole(i, dt);
        }

        // --- Process deferred removals (e.g. black holes that finished their quasar) ---
        for (int i = _states.Count - 1; i >= 0; i--)
        {
            if (i < _states.Count && _states[i].PendingRemoval)
                RemoveBody(i);
        }

        // --- Collision detection: merge or split ---
        ProcessCollisions(cw, ch);

        // --- Dust injection if population is low ---
        if (_blobs.Count < _minBodies && _dustCooldown <= 0)
        {
            // Scale injection burst with deficit: more missing = more injected per cycle
            int deficit = _minBodies - _blobs.Count;
            int toAdd = Math.Clamp(deficit / 10, 1, 5);
            for (int d = 0; d < toAdd && _blobs.Count < _maxBodies; d++)
            {
                if (_rng.NextDouble() < CometTrailChance && _blobs.Count >= 3)
                    InjectCometTrail();
                else
                    InjectDust(cw, ch);
            }
            _dustCooldown = 8;
        }
        else if (_dustCooldown > 0)
        {
            _dustCooldown--;
        }

        // --- Collapse explosion: if only 1 body left, it explodes into many small bodies ---
        if (_blobs.Count == 1)
            ExplodeLastBody(cw, ch);

        // --- Camera roam ---
        UpdateCamera(dt);

        // --- Diagnostic overlay (independent of camera roam) ---
        UpdateDiagnostics();

        // --- Very slow global hue drift (owns color across playfield + backglass) ---
        UpdateHueDrift(dt, count);

        // --- Spaghettification: stretch blobs falling toward any black hole ---
        UpdateSpaghettification();
    }

    /// <summary>
    /// Faux gravitational "spaghettification": blobs within a black hole's lensing radius are
    /// stretched radially (elongated toward the hole, squeezed perpendicular) with the effect
    /// ramping up the closer they get. Applied via each blob's RenderTransform; cleared when a
    /// blob leaves the radius or when no black holes exist. Skips black holes themselves and
    /// blobs currently running a collision pulse (which owns the RenderTransform).
    /// </summary>
    private void UpdateSpaghettification()
    {
        // Bound to the cached physics arrays (_posX/_radii) — bodies spawned after this
        // frame's snapshot aren't in those arrays yet and are handled next frame.
        int count = Math.Min(_cachedCount, Math.Min(_posX.Length,
            Math.Min(_blobs.Count, _states.Count)));
        if (count <= 0) return;

        // Collect active black holes (usually 0-2). Cheap linear scan.
        Span<int> holes = stackalloc int[Math.Min(count, 8)];
        int holeCount = 0;
        for (int i = 0; i < count && holeCount < holes.Length; i++)
        {
            if (_states[i].IsBlackHole && !_states[i].QuasarActive)
                holes[holeCount++] = i;
        }

        for (int i = 0; i < count; i++)
        {
            var s = _states[i];
            if (s.IsBlackHole) continue;
            // Don't fight the brief collision-pulse scale transform.
            if (s.CollisionPulseRemaining > 0) continue;

            double bestStretch = 1.0;
            double dirX = 0, dirY = 0;

            for (int h = 0; h < holeCount; h++)
            {
                int hi = holes[h];
                double reach = _radii[hi] * SpaghettiRadiusFactor;
                double dx = _posX[i] - _posX[hi];
                double dy = _posY[i] - _posY[hi];
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist >= reach || dist < 0.01) continue;

                // Ramp 0 (at reach) → 1 (at the event horizon), eased.
                double horizon = _radii[hi];
                double t = 1.0 - Math.Clamp((dist - horizon) / Math.Max(reach - horizon, 1.0), 0.0, 1.0);
                double stretch = 1.0 + (SpaghettiMaxStretch - 1.0) * (t * t);
                if (stretch > bestStretch)
                {
                    bestStretch = stretch;
                    dirX = dx / dist;
                    dirY = dy / dist;
                }
            }

            if (bestStretch > 1.001)
            {
                // Elongate along the radial (toward hole) axis, squeeze perpendicular to
                // conserve rough area. Orient the scale using a rotation to the radial angle.
                double angle = Math.Atan2(dirY, dirX) * 180.0 / Math.PI;
                double inv = 1.0 / Math.Sqrt(bestStretch);
                var group = new TransformGroup();
                group.Children.Add(new ScaleTransform(bestStretch, inv));
                group.Children.Add(new RotateTransform(angle));
                _blobs[i].RenderTransform = group;
                s.IsSpaghettified = true;
            }
            else if (s.IsSpaghettified)
            {
                // Left the lensing zone — clear the stretch.
                _blobs[i].RenderTransform = null;
                s.IsSpaghettified = false;
            }
        }
    }

    /// <summary>
    /// Rotates each body's own color hue by a tiny amount per frame for a slow,
    /// barely-perceptible color morph. Uses authoritative HSV stored on the body's state
    /// (seeded once from its current color) so drift never round-trips through quantized
    /// RGB — which previously caused a one-way brightness decay that slowly darkened
    /// long-lived blobs to near-black. Preserves each stop's alpha and skips bodies
    /// with an active color fade so merge/spawn transitions aren't disturbed.
    /// </summary>
    private void UpdateHueDrift(double dt, int count)
    {
        if (HueDriftDegreesPerSec <= 0) return;

        double deltaHue = HueDriftDegreesPerSec * dt;
        for (int i = 0; i < count && i < _brushes.Count && i < _states.Count; i++)
        {
            var st = _states[i];

            // Don't fight an in-progress merge/spawn fade.
            if (st.ColorFadeRemaining > 0)
            {
                // A fade is authoritative; re-seed from its current color when it ends.
                st.DriftInitialized = false;
                continue;
            }

            // Black holes own their (black) appearance — never hue-drift them.
            if (st.IsBlackHole) continue;

            // Seed authoritative HSV from the current color exactly once.
            if (!st.DriftInitialized)
            {
                var (h0, s0, v0) = RgbToHsv(_brushes[i].Color);
                st.DriftHue = h0;
                st.DriftSat = s0;
                st.DriftVal = v0;
                st.DriftInitialized = true;
            }

            st.DriftHue += deltaHue;
            if (st.DriftHue >= 360.0) st.DriftHue -= 360.0;
            else if (st.DriftHue < 0) st.DriftHue += 360.0;

            RecolorGradient(i, HsvToRgb(st.DriftHue, st.DriftSat, st.DriftVal));
        }
    }

    private void UpdateCamera(double dt)
    {
        if (_cameraScale == null || _cameraRotate == null || _cameraTranslate == null) return;

        double cw = Math.Max(1, _canvas.ActualWidth);
        double ch = Math.Max(1, _canvas.ActualHeight);
        double halfW = cw * 0.5;
        double halfH = ch * 0.5;

        // --- Compute bounding box of the innermost 80% of mass ---
        double totalMass = 0;
        int count = _cachedCount;
        for (int i = 0; i < count; i++)
            totalMass += _masses[i];

        if (totalMass < 0.01 || count == 0) return;

        // Center of mass
        double massCx = 0, massCy = 0;
        for (int i = 0; i < count; i++)
        {
            double m = _masses[i];
            massCx += _posX[i] * m;
            massCy += _posY[i] * m;
        }
        massCx /= totalMass;
        massCy /= totalMass;

        // Sort bodies by distance from center of mass, accumulate mass inward
        // to find the bounding box that contains CameraMassFraction of total mass.
        // Use a simple index sort to avoid allocations each frame.
        Span<int> sortIdx = stackalloc int[count];
        Span<double> distFromCm = stackalloc double[count];
        for (int i = 0; i < count; i++)
        {
            sortIdx[i] = i;
            double dx = _posX[i] - massCx;
            double dy = _posY[i] - massCy;
            distFromCm[i] = dx * dx + dy * dy;
        }
        // Simple insertion sort (fast for small N, no allocations)
        for (int i = 1; i < count; i++)
        {
            int key = sortIdx[i];
            double keyDist = distFromCm[key];
            int j = i - 1;
            while (j >= 0 && distFromCm[sortIdx[j]] > keyDist)
            {
                sortIdx[j + 1] = sortIdx[j];
                j--;
            }
            sortIdx[j + 1] = key;
        }

        // Accumulate mass from nearest to farthest, build bounding box
        double accMass = 0;
        double massThreshold = totalMass * CameraMassFraction;
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        for (int k = 0; k < count; k++)
        {
            int idx = sortIdx[k];
            double r = _radii[idx];
            minX = Math.Min(minX, _posX[idx] - r);
            maxX = Math.Max(maxX, _posX[idx] + r);
            minY = Math.Min(minY, _posY[idx] - r);
            maxY = Math.Max(maxY, _posY[idx] + r);
            accMass += _masses[idx];
            if (accMass >= massThreshold) break;
        }

        double extentX = Math.Max((maxX - minX) * 0.5, 50) * 1.1;
        double extentY = Math.Max((maxY - minY) * 0.5, 50) * 1.1;
        double frameCx = (minX + maxX) * 0.5;
        double frameCy = (minY + maxY) * 0.5;

        // Compute zoom to fit the extent in the viewport
        double zoomX = halfW / extentX;
        double zoomY = halfH / extentY;
        double targetZoom = Math.Clamp(Math.Min(zoomX, zoomY), CameraMinZoom, CameraMaxZoom);

        // Compute offset to center the framed region in the viewport
        double targetOffsetX = (halfW - frameCx) * _cameraZoom;
        double targetOffsetY = (halfH - frameCy) * _cameraZoom;

        // --- Gentle random drift for rotation ---
        _cameraDriftTimer -= dt;
        if (_cameraDriftTimer <= 0)
        {
            PickDriftTarget();
            _cameraDriftTimer = CameraDriftIntervalSec + _rng.NextDouble() * 10.0;
        }

        // --- Lerp toward targets ---
        double zoomLerp = 1.0 - Math.Pow(1.0 - CameraZoomLerpSpeed, dt);
        double panLerp = 1.0 - Math.Pow(1.0 - CameraPanLerpSpeed, dt);
        double driftLerp = 1.0 - Math.Pow(1.0 - CameraDriftLerpSpeed, dt);

        _cameraZoom += (targetZoom - _cameraZoom) * zoomLerp;
        _cameraAngle += (_cameraTargetAngle - _cameraAngle) * driftLerp;
        _cameraOffsetX += (targetOffsetX - _cameraOffsetX) * panLerp;
        _cameraOffsetY += (targetOffsetY - _cameraOffsetY) * panLerp;

        // Apply
        _cameraScale.ScaleX = _cameraZoom;
        _cameraScale.ScaleY = _cameraZoom;
        _cameraScale.CenterX = halfW;
        _cameraScale.CenterY = halfH;
        _cameraRotate.Angle = _cameraAngle;
        _cameraRotate.CenterX = halfW;
        _cameraRotate.CenterY = halfH;
        _cameraTranslate.X = _cameraOffsetX;
        _cameraTranslate.Y = _cameraOffsetY;
    }

    private void UpdateDiagnostics()
    {
        if (_diagLabel == null) return;

        double cw = Math.Max(1, _canvas.ActualWidth);
        double ch = Math.Max(1, _canvas.ActualHeight);
        double halfW = cw * 0.5;
        double halfH = ch * 0.5;
        int count = _cachedCount;

        // Count bodies outside the current viewport
        int offScreen = 0;
        for (int i = 0; i < count; i++)
        {
            // Transform body center through camera: scale around canvas center + translate
            double bx = (_posX[i] - halfW) * _cameraZoom + halfW + _cameraOffsetX;
            double by = (_posY[i] - halfH) * _cameraZoom + halfH + _cameraOffsetY;
            if (bx < -_radii[i] || bx > cw + _radii[i] || by < -_radii[i] || by > ch + _radii[i])
                offScreen++;
        }
        _diagLabel.Text = $"Zoom: {_cameraZoom:F2}x  Bodies: {count}/{offScreen} off";

        // Color diagnostic text with the current dominant hue from the brushes
        if (_brushes.Count > 0)
        {
            var c = _brushes[0].Color;
            _diagLabel.Foreground = new SolidColorBrush(Color.FromArgb(200, c.R, c.G, c.B));
        }

        // Slow elliptical drift to prevent OLED burn-in
        double elapsed = _stopwatch.Elapsed.TotalSeconds;
        double angle = elapsed * (2.0 * Math.PI / DiagDriftPeriodSec);
        double driftX = DiagDriftRadiusX + Math.Cos(angle) * DiagDriftRadiusX;
        double driftY = DiagDriftRadiusY + Math.Sin(angle) * DiagDriftRadiusY;
        _diagLabel.Margin = new Thickness(12 + driftX, 0, 0, 12 + driftY);
    }

    private void PickDriftTarget()
    {
        // Small random rotation increments for subtle drift
        _cameraTargetAngle = _cameraAngle + (_rng.NextDouble() - 0.5) * 8.0;
    }

    private void ProcessCollisions(double cw, double ch)
    {
        // Bound all iteration and cached-array access to the snapshot taken at the start of
        // the frame. Merges can spawn new bodies (supernova/black-hole/quasar) mid-loop, which
        // grows _blobs/_states beyond the cached physics arrays (_posX etc.) — iterating over
        // the live _blobs.Count would then overrun those arrays. New bodies are handled next frame.
        int limit = Math.Min(_cachedCount, Math.Min(_posX.Length,
            Math.Min(_blobs.Count, _states.Count)));
        if (limit <= 1) return;

        // Track which bodies already participated in a collision this frame
        // to prevent chain-merge cascades that cause popping/teleporting.
        Span<bool> collided = stackalloc bool[limit];

        // Iterate backwards so removals don't invalidate indices
        for (int i = limit - 1; i >= 0 && i < _states.Count; i--)
        {
            if (collided[i]) continue;
            for (int j = i - 1; j >= 0 && j < _states.Count; j--)
            {
                if (collided[j]) continue;
                if (i >= _blobs.Count || j >= _blobs.Count) continue;

                double x1 = _posX[i];
                double y1 = _posY[i];
                double x2 = _posX[j];
                double y2 = _posY[j];

                double r1 = _radii[i];
                double r2 = _radii[j];
                double minDist = r1 + r2;

                double dx = x2 - x1;
                double dy = y2 - y1;
                double distSq = dx * dx + dy * dy;

                if (distSq >= minDist * minDist || distSq < 0.01) continue;

                // Immunity flag: either body recently pierced/split
                bool eitherImmune = _states[i].MergeImmunity > 0 || _states[j].MergeImmunity > 0;

                double dist = Math.Sqrt(distSq);
                double nx = dx / dist;
                double ny = dy / dist;

                var s1 = _states[i];
                var s2 = _states[j];

                // Relative velocity
                double dvx = s1.VelocityX - s2.VelocityX;
                double dvy = s1.VelocityY - s2.VelocityY;
                double relSpeed = Math.Sqrt(dvx * dvx + dvy * dvy);

                // Alignment: dot product of velocity directions
                double dot = 0;
                if (relSpeed > 0.01)
                {
                    // Dot product of individual velocity unit vectors
                    double sp1 = Math.Sqrt(s1.VelocityX * s1.VelocityX + s1.VelocityY * s1.VelocityY);
                    double sp2 = Math.Sqrt(s2.VelocityX * s2.VelocityX + s2.VelocityY * s2.VelocityY);
                    if (sp1 > 0.01 && sp2 > 0.01)
                    {
                        dot = (s1.VelocityX * s2.VelocityX + s1.VelocityY * s2.VelocityY) / (sp1 * sp2);
                    }
                }

                // Mass ratio: large body absorbs small body regardless of angle
                double mi = MassOf(_blobs[i]);
                double mj = MassOf(_blobs[j]);
                double massRatio = Math.Max(mi, mj) / Math.Max(Math.Min(mi, mj), 0.01);

                // Black holes never split, pierce, or get absorbed — they always merge
                // and always survive. If the black hole is body i, merge into it (i as
                // survivor); otherwise j already survives.
                bool iIsHole = _states[i].IsBlackHole;
                bool jIsHole = _states[j].IsBlackHole;
                if (iIsHole || jIsHole)
                {
                    collided[i] = true; collided[j] = true;
                    if (iIsHole)
                        MergeBodies(j, i); // i survives (the black hole)
                    else
                        MergeBodies(i, j); // j survives (the black hole)
                    break;
                }

                // Immunity suppresses pierce and split but still allows merge and bounce.
                // This prevents the feedback loop where pierce → immunity → gravity return → pierce again.
                bool shouldPierce = !eitherImmune
                                    && relSpeed > SplitSpeedThreshold
                                    && massRatio >= MassRatioMergeThreshold
                                    && _blobs.Count + 1 <= _maxBodies
                                    && _rng.NextDouble() < PierceProbability;

                bool shouldSplit = !eitherImmune && !shouldPierce
                                   && relSpeed > SplitSpeedThreshold && dot < -MergeAlignmentThreshold
                                   && massRatio < MassRatioMergeThreshold
                                   && _blobs.Count + 2 <= _maxBodies;
                if (shouldPierce)
                {
                    collided[i] = true; collided[j] = true;
                    PierceBody(i, j, nx, ny, cw, ch);
                    break;
                }
                else if (shouldSplit)
                {
                    collided[i] = true; collided[j] = true;
                    SplitBody(i, j, nx, ny, cw, ch);
                    break;
                }
                else
                {
                    // Default: always merge — no elastic bounce so collisions
                    // look like galaxies merging rather than billiard balls.
                    collided[i] = true; collided[j] = true;
                    MergeBodies(i, j);

                    // Brief scale pulse on the survivor as a visual cue of the interaction.
                    if (CollisionPulseScale > 0 && j < _states.Count)
                        _states[j].CollisionPulseRemaining = CollisionPulseSec;

                    // Check if the survivor exceeds the supernova threshold. If so, it
                    // either explodes (supernova) or collapses into a black hole (50/50).
                    // A body that is already a black hole never re-triggers this.
                    double supernovaThreshold = GravityBlobPattern.SupernovaMass;
                    if (supernovaThreshold > 0 && j < _blobs.Count && j < _states.Count
                        && !_states[j].IsBlackHole && _blobs[j].Width >= supernovaThreshold)
                    {
                        if (_rng.NextDouble() < 0.5)
                            BecomeBlackHole(j);
                        else
                            SupernovaExplode(j, cw, ch);
                    }
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Merge body i into body j (j survives). Area-preserving radius.
    /// Momentum-conserving velocity.
    /// </summary>
    private void MergeBodies(int i, int j)
    {
        double r1 = _radii[i];
        double r2 = _radii[j];
        double m1 = _masses[i];
        double m2 = _masses[j];
        double totalMass = m1 + m2;

        // Reposition survivor to mass-weighted center to prevent visual jumping
        double x1 = _posX[i];
        double y1 = _posY[i];
        double x2 = _posX[j];
        double y2 = _posY[j];
        double cx = (x1 * m1 + x2 * m2) / totalMass;
        double cy = (y1 * m1 + y2 * m2) / totalMass;

        // Momentum-conserving velocity
        _states[j].VelocityX = (m1 * _states[i].VelocityX + m2 * _states[j].VelocityX) / totalMass;
        _states[j].VelocityY = (m1 * _states[i].VelocityY + m2 * _states[j].VelocityY) / totalMass;

        // Ensure survivor doesn't go dead — apply minimum speed if momentum cancelled out
        double postSpeed = Math.Sqrt(_states[j].VelocityX * _states[j].VelocityX + _states[j].VelocityY * _states[j].VelocityY);
        if (postSpeed < PostMergeMinSpeed)
        {
            // Nudge in a random direction
            double angle = _rng.NextDouble() * Math.PI * 2.0;
            _states[j].VelocityX += Math.Cos(angle) * PostMergeMinSpeed;
            _states[j].VelocityY += Math.Sin(angle) * PostMergeMinSpeed;
        }

        // Area-preserving new radius — animate toward target size instead of snapping
        double newRadius = Math.Sqrt(r1 * r1 + r2 * r2);
        double newSize = newRadius * 2.0;
        // A black hole grows but never past its size cap (keeps the event horizon bounded).
        if (_states[j].IsBlackHole && _states[j].BlackHoleMaxSize > 0)
            newSize = Math.Min(newSize, _states[j].BlackHoleMaxSize);
        _states[j].MergeTargetSize = newSize;
        _states[j].BaseSize = newSize;

        // Position survivor at the center of mass immediately.
        // (Both bodies were already overlapping at collision time, so the jump is minimal.
        // The old lerp-toward-fixed-position approach acted as a brake, killing momentum.)
        Canvas.SetLeft(_blobs[j], cx - _blobs[j].Width * 0.5);
        Canvas.SetTop(_blobs[j], cy - _blobs[j].Height * 0.5);

        // Mass-weighted color blend
        BlendColor(i, j, m1, m2, totalMass);

        // Remove body i
        RemoveBody(i);
    }

    /// <summary>
    /// Split: the smaller body shatters into 2-3 fragments; the larger body bounces.
    /// </summary>
    private void SplitBody(int i, int j, double nx, double ny, double cw, double ch)
    {
        // Determine which is smaller
        int smaller = _blobs[i].Width <= _blobs[j].Width ? i : j;
        int larger = smaller == i ? j : i;

        double smallRadius = _blobs[smaller].Width * 0.5;
        if (smallRadius < DustSize + 2)
        {
            // Too small to split — just bounce
            return;
        }

        double area = smallRadius * smallRadius;
        int fragments = _rng.Next(2, 4); // 2 or 3
        double fragRadius = Math.Sqrt(area / fragments);
        double fragSize = Math.Max(DustSize, fragRadius * 2.0);

        double cx = Canvas.GetLeft(_blobs[smaller]) + _blobs[smaller].Width * 0.5;
        double cy = Canvas.GetTop(_blobs[smaller]) + _blobs[smaller].Height * 0.5;
        double baseAngle = Math.Atan2(ny, nx);

        // Capture parent color before removal
        Color parentColor = _brushes[smaller].Color;

        // Remove the smaller body first
        var sState = _states[smaller];
        double svx = sState.VelocityX;
        double svy = sState.VelocityY;
        RemoveBody(smaller);

        // Adjust larger index if needed after removal
        if (larger > smaller) larger--;

        // Elastic bounce on the larger body
        var ls = _states[larger];
        ls.VelocityX = -ls.VelocityX * 0.8;
        ls.VelocityY = -ls.VelocityY * 0.8;

        // Create fragments with parent's color
        for (int f = 0; f < fragments && _blobs.Count < _maxBodies; f++)
        {
            double angle = baseAngle + (f - fragments / 2.0) * 0.8 + (_rng.NextDouble() - 0.5) * 0.4;
            double speed = 60 + _rng.NextDouble() * 80;
            CreateBody(
                cx + Math.Cos(angle) * fragSize - fragSize * 0.5,
                cy + Math.Sin(angle) * fragSize - fragSize * 0.5,
                fragSize,
                svx * 0.3 + Math.Cos(angle) * speed,
                svy * 0.3 + Math.Sin(angle) * speed,
                parentColor);
            _states[^1].MergeImmunity = 1.0;
        }
    }

    /// <summary>
    /// Pierce: a fast small body punches through a large one, continuing on the
    /// other side at slightly reduced speed. The large body loses a chunk of mass
    /// which spawns as a new fragment travelling in a similar direction to the bullet.
    /// </summary>
    private void PierceBody(int i, int j, double nx, double ny, double cw, double ch)
    {
        int smaller = _blobs[i].Width <= _blobs[j].Width ? i : j;
        int larger = smaller == i ? j : i;

        double bigRadius = _blobs[larger].Width * 0.5;
        double smallRadius = _blobs[smaller].Width * 0.5;

        // Mass stolen is proportional to the bullet's mass (10-20% of the big body,
        // clamped so we don't obliterate it)
        double stolenFraction = Math.Clamp(MassOf(_blobs[smaller]) / MassOf(_blobs[larger]) * 0.5, 0.05, 0.15);
        double stolenArea = bigRadius * bigRadius * stolenFraction;
        double fragRadius = Math.Sqrt(stolenArea);
        double fragSize = Math.Max(DustSize, fragRadius * 2.0);

        // Shrink the big body
        double newBigArea = bigRadius * bigRadius * (1.0 - stolenFraction);
        double newBigRadius = Math.Sqrt(newBigArea);
        double newBigSize = Math.Max(DustSize * 2, newBigRadius * 2.0);
        _blobs[larger].Width = newBigSize;
        _blobs[larger].Height = newBigSize;
        _states[larger].BaseSize = newBigSize;

        // Bullet continues at ~80% speed
        var bs = _states[smaller];
        bs.VelocityX *= 0.8;
        bs.VelocityY *= 0.8;
        bs.MergeImmunity = 1.0;

        // Teleport bullet past the big body's far edge so it doesn't overlap
        double bulletAngle = Math.Atan2(bs.VelocityY, bs.VelocityX);
        double bigCx = Canvas.GetLeft(_blobs[larger]) + _blobs[larger].Width * 0.5;
        double bigCy = Canvas.GetTop(_blobs[larger]) + _blobs[larger].Height * 0.5;
        double clearance = newBigRadius + smallRadius + 4.0;
        Canvas.SetLeft(_blobs[smaller], bigCx + Math.Cos(bulletAngle) * clearance - smallRadius);
        Canvas.SetTop(_blobs[smaller], bigCy + Math.Sin(bulletAngle) * clearance - smallRadius);

        // Large body gets a small nudge from the impact
        var ls = _states[larger];
        double impulse = MassOf(_blobs[smaller]) * 0.15 / Math.Max(MassOf(_blobs[larger]), 1.0);
        ls.VelocityX += bs.VelocityX * impulse;
        ls.VelocityY += bs.VelocityY * impulse;

        // Each fragment's area is half the stolen mass
        double fragAreaEach = stolenArea * 0.5;
        double fragRadiusEach = Math.Sqrt(fragAreaEach);
        double fragSizeEach = Math.Max(DustSize, fragRadiusEach * 2.0);

        // Spawn two fragments at ±15° from bullet trajectory, placed outside the big body
        double fragSpeed = Math.Sqrt(bs.VelocityX * bs.VelocityX + bs.VelocityY * bs.VelocityY) * 1.2;
        Color bigColor = _brushes[larger].Color;
        double sprayAngle = 15.0 * Math.PI / 180.0;

        for (int f = 0; f < 2 && _blobs.Count < _maxBodies; f++)
        {
            double angle = bulletAngle + (f == 0 ? sprayAngle : -sprayAngle);
            double spawnDist = newBigRadius + fragSizeEach + 4.0;
            CreateBody(
                bigCx + Math.Cos(angle) * spawnDist - fragSizeEach * 0.5,
                bigCy + Math.Sin(angle) * spawnDist - fragSizeEach * 0.5,
                fragSizeEach,
                Math.Cos(angle) * fragSpeed,
                Math.Sin(angle) * fragSpeed,
                bigColor);
            _states[^1].MergeImmunity = 1.0;
        }
    }

    private void InjectDust(double cw, double ch)
    {
        // When zoomed out, the visible area is larger than the canvas.
        // Spawn dust just outside the visible viewport so it drifts in naturally.
        double invZoom = _cameraZoom > 0.01 ? 1.0 / _cameraZoom : 1.0;
        double halfW = cw * 0.5;
        double halfH = ch * 0.5;
        // Visible extents in canvas coordinates, centered on canvas center + camera offset
        double visCx = halfW - _cameraOffsetX * invZoom;
        double visCy = halfH - _cameraOffsetY * invZoom;
        double visW = cw * invZoom;
        double visH = ch * invZoom;
        double left = visCx - visW * 0.5;
        double top = visCy - visH * 0.5;
        double right = visCx + visW * 0.5;
        double bottom = visCy + visH * 0.5;

        double size = DustSize + _rng.NextDouble() * 4;
        double x, y, vx, vy;
        int edge = _rng.Next(4);
        double speed = 20 + _rng.NextDouble() * 40;
        switch (edge)
        {
            case 0: x = left + _rng.NextDouble() * visW; y = top; vx = (_rng.NextDouble() - 0.5) * speed; vy = speed; break;
            case 1: x = left + _rng.NextDouble() * visW; y = bottom; vx = (_rng.NextDouble() - 0.5) * speed; vy = -speed; break;
            case 2: x = left; y = top + _rng.NextDouble() * visH; vx = speed; vy = (_rng.NextDouble() - 0.5) * speed; break;
            default: x = right; y = top + _rng.NextDouble() * visH; vx = -speed; vy = (_rng.NextDouble() - 0.5) * speed; break;
        }

        CreateBody(x - size * 0.5, y - size * 0.5, size, vx, vy);
    }

    /// <summary>
    /// Spawns 2-3 tiny dust particles behind one of the largest bodies,
    /// moving in the same direction but slower — a comet trail effect.
    /// </summary>
    private void InjectCometTrail()
    {
        // Pick one of the top 20% largest bodies (use _cachedCount — safe bound for cached arrays)
        int count = _cachedCount;
        if (count < 3) return;

        int topN = Math.Max(1, count / 5);
        // Find indices of the largest bodies by radius
        Span<int> candidates = stackalloc int[Math.Min(topN, 20)];
        Span<double> candidateRadii = stackalloc double[candidates.Length];
        candidateRadii.Fill(0);

        for (int i = 0; i < count; i++)
        {
            double r = _radii[i];
            for (int c = 0; c < candidates.Length; c++)
            {
                if (r > candidateRadii[c])
                {
                    // Shift down
                    for (int s = candidates.Length - 1; s > c; s--)
                    {
                        candidates[s] = candidates[s - 1];
                        candidateRadii[s] = candidateRadii[s - 1];
                    }
                    candidates[c] = i;
                    candidateRadii[c] = r;
                    break;
                }
            }
        }

        int parentIdx = candidates[_rng.Next(Math.Min(topN, candidates.Length))];
        if (parentIdx >= _blobs.Count || parentIdx >= _states.Count) return;
        var ps = _states[parentIdx];
        // Read current position directly from canvas (cached values may be stale after collisions)
        double px = Canvas.GetLeft(_blobs[parentIdx]) + _blobs[parentIdx].Width * 0.5;
        double py = Canvas.GetTop(_blobs[parentIdx]) + _blobs[parentIdx].Height * 0.5;
        double pr = _blobs[parentIdx].Width * 0.5;
        Color parentColor = _brushes[parentIdx].Color;

        // Velocity direction of the parent
        double speed = Math.Sqrt(ps.VelocityX * ps.VelocityX + ps.VelocityY * ps.VelocityY);
        if (speed < 1.0) return; // stationary body, skip

        double dirX = ps.VelocityX / speed;
        double dirY = ps.VelocityY / speed;
        // Perpendicular vector (consistent handedness)
        double perpX = -dirY;
        double perpY = dirX;

        int trailCount = 2 + _rng.Next(2); // 2-3 particles
        for (int i = 0; i < trailCount && _blobs.Count < _maxBodies; i++)
        {
            double size = DustSize * (0.5 + _rng.NextDouble() * 0.5);
            // Spawn behind the parent (opposite of velocity direction)
            double dist = pr + 4 + _rng.NextDouble() * pr * 0.5 + i * (size + 2);
            double perpJitter = (_rng.NextDouble() - 0.5) * pr * 0.3;
            double fx = px - dirX * dist + perpX * perpJitter;
            double fy = py - dirY * dist + perpY * perpJitter;

            // Same direction as parent but 5-30% of the speed, with outward bias
            double trailSpeed = speed * (0.05 + _rng.NextDouble() * 0.25);

            // Bias trail velocity away from canvas center to prevent quick inward fall
            double canvasCx = Math.Max(1, _canvas.ActualWidth) * 0.5;
            double canvasCy = Math.Max(1, _canvas.ActualHeight) * 0.5;
            double outX = fx - canvasCx;
            double outY = fy - canvasCy;
            double outLen = Math.Sqrt(outX * outX + outY * outY);
            if (outLen > 1.0)
            {
                outX /= outLen;
                outY /= outLen;
            }
            double outwardBias = trailSpeed * 4; // x% trail speed pushed outward

            double tvx = dirX * trailSpeed + outX * outwardBias;
            double tvy = dirY * trailSpeed + outY * outwardBias;

            // Slightly dimmer version of parent color
            byte dr = (byte)Math.Clamp(parentColor.R + _rng.Next(-20, 10), 0, 255);
            byte dg = (byte)Math.Clamp(parentColor.G + _rng.Next(-20, 10), 0, 255);
            byte db = (byte)Math.Clamp(parentColor.B + _rng.Next(-20, 10), 0, 255);

            CreateBody(fx - size * 0.5, fy - size * 0.5, size, tvx, tvy, Color.FromRgb(dr, dg, db), fadeFrom: parentColor);
            _states[^1].MergeImmunity = 4.0; // long immunity so trail particles visibly separate
            _states[^1].GravityImmuneFrom = _blobs[parentIdx]; // ignore gravity from parent
        }
    }

    /// <summary>
    /// When only one body remains, it "explodes" — removed and replaced by a burst
    /// of small bodies scattered across the canvas with random velocities.
    /// </summary>
    private void ExplodeLastBody(double cw, double ch)
    {
        // Remove the sole survivor
        RemoveBody(0);

        // Spawn a fresh field of small bodies spread across the canvas
        int spawnCount = _rng.Next(20, 31);
        double spread = Math.Min(cw, ch) * 0.75;
        double cx = cw * 0.5;
        double cy = ch * 0.5;

        for (int i = 0; i < spawnCount && _blobs.Count < _maxBodies; i++)
        {
            double angle = _rng.NextDouble() * Math.PI * 2.0;
            double radius = _rng.NextDouble() * spread * 0.5;
            double x = cx + Math.Cos(angle) * radius;
            double y = cy + Math.Sin(angle) * radius;
            double size = DustSize + _rng.NextDouble() * 6;
            double speed = 30 + _rng.NextDouble() * 60;
            double vAngle = _rng.NextDouble() * Math.PI * 2.0;
            CreateBody(x - size * 0.5, y - size * 0.5, size,
                Math.Cos(vAngle) * speed, Math.Sin(vAngle) * speed);
        }
    }

    /// <summary>
    /// Converts an oversized body into a black hole in place: an opaque black disc with a
    /// soft fuzzy edge, a faint accretion glow rim, and a blur. It keeps absorbing bodies
    /// (always the survivor, immune to split/pierce) and grows until it reaches its size cap
    /// (supernova diameter × <see cref="GravityBlobPattern.BlackHoleMaxSizeFactor"/>), then
    /// enters the quasar phase.
    /// </summary>
    private void BecomeBlackHole(int idx)
    {
        if (idx < 0 || idx >= _blobs.Count || idx >= _states.Count) return;

        var st = _states[idx];
        st.IsBlackHole = true;
        st.BlackHoleAgeSec = 0;
        // Cancel any color fade started by the merge that triggered this collapse —
        // otherwise the fade tick would keep recoloring the black gradient (making it
        // show the blended merge color instead of black).
        st.ColorFadeRemaining = 0;
        st.ColorFadeDuration = 0;
        // Cap = supernova diameter × factor (1.1–2.0), never smaller than the current size.
        double factor = Math.Clamp(GravityBlobPattern.BlackHoleMaxSizeFactor, 1.1, 2.0);
        double capFromFactor = GravityBlobPattern.SupernovaMass * factor;
        st.BlackHoleMaxSize = Math.Max(_blobs[idx].Width, capFromFactor);
        st.QuasarActive = false;
        st.QuasarRemaining = 0;
        st.MergeImmunity = 0;
        st.GravityImmuneFrom = null;

        // A black hole is fully opaque — never let the background show through the core.
        _blobs[idx].Opacity = 1.0;
        _blobs[idx].RenderTransform = null; // clear any leftover collision-pulse scale

        ApplyBlackHoleAppearance(idx);
    }

    /// <summary>Builds the black-hole disc brush: opaque black core, faint accretion glow rim, transparent edge.</summary>
    private void ApplyBlackHoleAppearance(int idx)
    {
        if (idx >= _blobs.Count || idx >= _gradBrushes.Count) return;

        // Accretion glow color — a warm, dim ring just inside the rim.
        var glow = Color.FromArgb(90, 255, 130, 40);
        // Faux gravitational-lens ring — a thin, cool bright band just outside the event
        // horizon that (with the blur) reads as light bending around the hole. Purely cosmetic.
        var lensBright = Color.FromArgb(70, 180, 205, 255);
        var lensDark = Color.FromArgb(120, 0, 0, 0);

        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
            GradientStops = new GradientStopCollection
            {
                new(Color.FromArgb(255, 0, 0, 0), 0.0),    // solid black core
                new(Color.FromArgb(255, 0, 0, 0), 0.88),   // pure black through nearly the whole disc
                new(lensDark, 0.905),                       // dark refraction gap at the horizon
                new(lensBright, 0.925),                     // bright lensing ring (bent light)
                new(lensDark, 0.94),                        // darken again — the far side of the lens
                new(glow, 0.965),                           // thin, dim warm accretion glow
                new(Color.FromArgb(0, 255, 150, 60), 1.0),  // soft fuzzy fade to transparent
            }
        };
        _gradBrushes[idx] = brush;

        if (_blobs[idx] is Shape shape)
        {
            shape.Fill = brush;
            shape.Effect = new System.Windows.Media.Effects.BlurEffect
            {
                Radius = 9,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
            };
        }
        // Keep the solid brush black so any color-source reads are consistent.
        if (idx < _brushes.Count)
            _brushes[idx].Color = Color.FromArgb(_brushes[idx].Color.A, 0, 0, 0);
    }

    /// <summary>
    /// Advances a black hole's lifecycle each frame: it grows (via merges) up to its size
    /// cap, ages, and once it reaches the cap OR its max lifetime it enters the quasar
    /// phase — ejecting blob pairs from its poles while fading out, then is removed.
    /// </summary>
    private void UpdateBlackHole(int i, double dt)
    {
        if (i >= _states.Count || i >= _blobs.Count) return;
        var s = _states[i];
        s.BlackHoleAgeSec += dt;

        if (!s.QuasarActive)
        {
            double size = _blobs[i].Width;
            bool reachedCap = size >= s.BlackHoleMaxSize - 0.5 && double.IsNaN(s.MergeTargetSize);
            bool tooOld = s.BlackHoleAgeSec >= BlackHoleMaxLifeSec;
            bool oldEnough = s.BlackHoleAgeSec >= BlackHoleMinLifeSec;

            if ((reachedCap && oldEnough) || tooOld)
            {
                s.QuasarActive = true;
                s.QuasarRemaining = QuasarDurationSec;
                s.QuasarJetCooldown = 0;
            }
            return;
        }

        // --- Quasar phase: eject polar jets and fade the disc out ---
        s.QuasarRemaining -= dt;

        s.QuasarJetCooldown -= dt;
        if (s.QuasarRemaining > QuasarDurationSec * 0.25 && s.QuasarJetCooldown <= 0)
        {
            EjectQuasarJets(i);
            s.QuasarJetCooldown = QuasarJetIntervalSec;
        }

        // Fade the disc out over the quasar duration.
        double fade = Math.Clamp(s.QuasarRemaining / QuasarDurationSec, 0.0, 1.0);
        _blobs[i].Opacity = fade;

        if (s.QuasarRemaining <= 0)
        {
            if (_blobs[i] is Shape shp) shp.Effect = null;
            // Defer the actual removal to after the integrate loop to avoid mutating
            // the body lists mid-iteration (which caused an index-out-of-range crash).
            s.PendingRemoval = true;
        }
    }

    /// <summary>
    /// Quasar jet ejection: spits a pair of glowing blobs from the black hole's north
    /// and south poles (straight up/down, since bodies are circular). Ejected blobs get
    /// merge immunity so they escape the well and resettle naturally into the field.
    /// </summary>
    private void EjectQuasarJets(int idx)
    {
        if (idx >= _blobs.Count || idx >= _states.Count) return;

        double cx = _posX[idx];
        double cy = _posY[idx];
        double r = _radii[idx];

        // Bright, hot jet color (blue-white with slight variation).
        double hue = 190 + _rng.NextDouble() * 40; // cyan→blue

        // Erupt a burst from each pole per tick to form a dramatic twin-jet eruption.
        const int perPole = 14;
        for (int p = 0; p < 2; p++)
        {
            double dir = p == 0 ? -1.0 : 1.0;
            for (int k = 0; k < perPole && _blobs.Count < _maxBodies; k++)
            {
                double size = DustSize * (0.8 + _rng.NextDouble() * 1.0);
                double speed = 240 + _rng.NextDouble() * 260;

                // Narrow cone around the pole: mostly vertical with a small horizontal spread.
                double spread = (_rng.NextDouble() - 0.5) * 0.5; // ±~0.25 rad from vertical
                double vx = Math.Sin(spread) * speed;
                double vy = dir * Math.Cos(spread) * speed;

                // Spawn just outside the event horizon along the pole, with jitter.
                double jitterX = (_rng.NextDouble() - 0.5) * r * 0.4;
                double fx = cx + jitterX;
                double fy = cy + dir * (r + size + _rng.NextDouble() * r * 0.3);

                double jHue = (hue + _rng.Next(-15, 15) + 360) % 360;
                Color jetColor = HsvToRgb(jHue, 0.4 + _rng.NextDouble() * 0.3, 1.0);

                CreateBody(fx - size * 0.5, fy - size * 0.5, size, vx, vy, jetColor);
                var js = _states[^1];
                js.MergeImmunity = 2.0;             // escape the well before it can re-absorb
                js.GravityImmuneFrom = _blobs[idx]; // ignore the hole's gravity briefly
            }
        }
    }

    /// <summary>
    /// Supernova: a body that exceeds the supernova mass threshold explodes
    /// into a burst of fragments expelled outward from its position.
    /// </summary>
    private void SupernovaExplode(int idx, double cw, double ch)
    {
        double bx = _posX[idx];
        double by = _posY[idx];
        double oldRadius = _radii[idx];
        Color color = _brushes[idx].Color;

        // Remove the supernova body
        RemoveBody(idx);

        // Number of fragments scales with original size, 12–30
        int fragCount = Math.Clamp((int)(oldRadius * 0.6), 12, 30);
        double baseFragRadius = Math.Max(DustSize * 0.5, oldRadius / Math.Sqrt(fragCount));

        for (int i = 0; i < fragCount && _blobs.Count < _maxBodies; i++)
        {
            double angle = _rng.NextDouble() * Math.PI * 2.0;
            double speed = 80 + _rng.NextDouble() * 200;
            double sizeVariation = 0.5 + _rng.NextDouble();
            double fragSize = Math.Max(DustSize, baseFragRadius * 2.0 * sizeVariation);
            double spawnDist = oldRadius * 0.3 + _rng.NextDouble() * oldRadius * 0.4;
            double fx = bx + Math.Cos(angle) * spawnDist;
            double fy = by + Math.Sin(angle) * spawnDist;

            // Slight hue variation from parent color
            byte dr = (byte)Math.Clamp(color.R + _rng.Next(-30, 31), 0, 255);
            byte dg = (byte)Math.Clamp(color.G + _rng.Next(-30, 31), 0, 255);
            byte db = (byte)Math.Clamp(color.B + _rng.Next(-30, 31), 0, 255);
            Color fragColor = Color.FromRgb(dr, dg, db);

            CreateBody(fx - fragSize * 0.5, fy - fragSize * 0.5, fragSize,
                Math.Cos(angle) * speed, Math.Sin(angle) * speed, fragColor, fadeFrom: color);
            _states[^1].MergeImmunity = 1.5; // longer immunity so fragments spread out
        }
    }

    private void CreateBody(double x, double y, double size, double vx, double vy, Color? color = null, Color? fadeFrom = null)
    {
        Color c = color ?? RandomHue();

        // Optionally start the body at a "from" color and fade to its final color,
        // so fragments spawned by split/pierce/supernova don't pop in abruptly.
        Color startColor = (SpawnColorFadeSec > 0 && fadeFrom.HasValue) ? fadeFrom.Value : c;
        var brush = new SolidColorBrush(startColor);
        var gradBrush = MakeSubtleGradient(startColor);

        double opacity = _intensity + _rng.NextDouble() * 0.1;

        var blob = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = gradBrush,
            Opacity = opacity,
            RenderTransformOrigin = new Point(0.5, 0.5),
            CacheMode = _useBitmapCache ? new BitmapCache(1.0) : null,
        };

        Canvas.SetLeft(blob, x);
        Canvas.SetTop(blob, y);
        _canvas.Children.Add(blob);

        _blobs.Add(blob);
        _brushes.Add(brush);
        _gradBrushes.Add(gradBrush);
        var state = new BlobState
        {
            VelocityX = vx,
            VelocityY = vy,
            BaseSize = size,
            BaseOpacity = opacity,
        };
        if (SpawnColorFadeSec > 0 && fadeFrom.HasValue && startColor != c)
        {
            state.ColorFadeFrom = startColor;
            state.ColorFadeTo = c;
            state.ColorFadeDuration = SpawnColorFadeSec;
            state.ColorFadeRemaining = SpawnColorFadeSec;
        }
        _states.Add(state);
    }

    /// <summary>
    /// Mass-weighted RGB blend of two bodies' colors. Updates the surviving body's brushes.
    /// </summary>
    private void BlendColor(int src, int dst, double mSrc, double mDst, double totalMass)
    {
        // Black holes stay black — never blend absorbed colors into them.
        if (dst < _states.Count && _states[dst].IsBlackHole) return;

        Color c1 = _brushes[src].Color;
        Color c2 = _brushes[dst].Color;
        byte r = (byte)((c1.R * mSrc + c2.R * mDst) / totalMass);
        byte g = (byte)((c1.G * mSrc + c2.G * mDst) / totalMass);
        byte b = (byte)((c1.B * mSrc + c2.B * mDst) / totalMass);
        Color blended = Color.FromRgb(r, g, b);

        if (MergeColorFadeSec <= 0 || dst >= _states.Count)
        {
            RecolorGradient(dst, blended);
            if (dst < _states.Count) _states[dst].DriftInitialized = false;
            return;
        }

        // Start a smooth color fade from the surviving body's current color to the blend.
        var st = _states[dst];
        st.ColorFadeFrom = c2;
        st.ColorFadeTo = blended;
        st.ColorFadeDuration = MergeColorFadeSec;
        st.ColorFadeRemaining = MergeColorFadeSec;
    }

    /// <summary>
    /// Creates a subtle radial gradient — solid center fading to transparent at the edge.
    /// Just enough softness to avoid a hard vector look. Shared by the pattern's initial
    /// blob creation and the simulator's spawned bodies so both stay in sync.
    /// </summary>
    public static RadialGradientBrush MakeSubtleGradient(Color c)
    {
        double mid = Math.Clamp(GradientMidStop, 0.01, 0.99);
        return new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
            GradientStops = new GradientStopCollection
            {
                new(c, 0.0),
                new(Color.FromArgb(GradientMidAlpha, c.R, c.G, c.B), mid),
                new(Color.FromArgb(0, c.R, c.G, c.B), 1.0),
            }
        };
    }

    /// <summary>
    /// Recolors the RGB of an existing gradient brush's stops in place, preserving each
    /// stop's current alpha. This lets the simulator update a blob's color without
    /// resetting the alpha profile that another owner (e.g. the backglass color cycler)
    /// may have set, and avoids allocating a new brush every frame during color fades.
    /// </summary>
    private void RecolorGradient(int idx, Color rgb)
    {
        // Keep the solid brush (used as the color source elsewhere) in sync.
        var solid = _brushes[idx];
        solid.Color = Color.FromArgb(solid.Color.A, rgb.R, rgb.G, rgb.B);

        var stops = _gradBrushes[idx].GradientStops;
        for (int s = 0; s < stops.Count; s++)
        {
            byte a = stops[s].Color.A;
            stops[s].Color = Color.FromArgb(a, rgb.R, rgb.G, rgb.B);
        }
    }

    private Color RandomHue()
    {
        // HSV with full saturation/value, random hue
        double h = _rng.NextDouble() * 360.0;
        return HsvToRgb(h, 0.9, 1.0);
    }

    private static (double h, double s, double v) RgbToHsv(Color color)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double h = 0;
        if (delta > 1e-6)
        {
            if (max == r) h = 60.0 * (((g - b) / delta) % 6.0);
            else if (max == g) h = 60.0 * (((b - r) / delta) + 2.0);
            else h = 60.0 * (((r - g) / delta) + 4.0);
        }
        if (h < 0) h += 360.0;

        double s = max <= 1e-6 ? 0 : delta / max;
        double v = max;
        return (h, s, v);
    }

    private static Color HsvToRgb(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        double m = v - c;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private void RemoveBody(int index)
    {
        _canvas.Children.Remove(_blobs[index]);
        _blobs.RemoveAt(index);
        _brushes.RemoveAt(index);
        _gradBrushes.RemoveAt(index);
        _states.RemoveAt(index);
    }

    private static void ApplyBoundaryForce(double x, double y, double cw, double ch, ref double ax, ref double ay)
    {
        // Simulation space is SimSpaceMultiplier × canvas, centered on the canvas center.
        // Bodies can range from -cw to 2*cw (for 3× total width) and similarly for height.
        double simHalfW = cw * SimSpaceMultiplier * 0.5;
        double simHalfH = ch * SimSpaceMultiplier * 0.5;
        double cx = cw * 0.5;
        double cy = ch * 0.5;

        // Distance from center, relative to sim half-extent
        double dx = x - cx;
        double dy = y - cy;
        double ratioX = Math.Abs(dx) / simHalfW;
        double ratioY = Math.Abs(dy) / simHalfH;

        // Force ramps from 0 at 80% of sim edge to full at 100%
        const double onset = 0.8;
        if (ratioX > onset)
        {
            double t = Math.Min(1.0, (ratioX - onset) / (1.0 - onset));
            ax -= Math.Sign(dx) * BoundaryStrength * t * t;
        }
        if (ratioY > onset)
        {
            double t = Math.Min(1.0, (ratioY - onset) / (1.0 - onset));
            ay -= Math.Sign(dy) * BoundaryStrength * t * t;
        }
    }

    private static double MassOf(FrameworkElement blob) => blob.Width * blob.Width;
}
