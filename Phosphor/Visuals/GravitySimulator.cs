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
    private const double BoundaryStrength = 200.0; // force pushing bodies back on-screen
    private const double CentralGravity = 4.0;     // very gentle pull toward canvas center
    private const double RepulsionRadiiFactor = 3.0;  // repulsion activates within this × combined radii
    private const double PierceProbability = 0.25;  // chance a qualifying impact pierces instead of merging
    private const double MergeAlignmentThreshold = 0.3;  // dot product threshold (>0 = same-ish direction)
    private const double SplitSpeedThreshold = 120.0;    // relative speed above which opposing collisions split
    private const double MassRatioMergeThreshold = 4.0;  // if larger/smaller mass ratio exceeds this, always merge
    private const int MaxBodies = 100;
    private const double DustSize = 6.0;           // px radius of injected dust particles
    private const double MaxDt = 0.033;            // clamp dt to ~30fps minimum

    private readonly Canvas _canvas;
    private readonly List<FrameworkElement> _blobs;
    private readonly List<BlobState> _states;
    private readonly List<SolidColorBrush> _brushes;
    private readonly List<RadialGradientBrush> _gradBrushes;
    private readonly double _intensity;
    private readonly double _speedMultiplier;
    private readonly int _minBodies;
    private readonly Random _rng = new();
    private readonly Stopwatch _stopwatch = new();
    private long _lastTickTicks;
    private bool _running;

    // --- Camera roam state ---
    private ScaleTransform? _cameraScale;
    private RotateTransform? _cameraRotate;
    private double _cameraZoom = 1.0;
    private double _cameraAngle = 0.0;
    private double _cameraTargetZoom = 1.0;
    private double _cameraTargetAngle = 0.0;
    private double _cameraRetargetTimer = 0.0;
    private const double CameraMinZoom = 0.85;
    private const double CameraMaxZoom = 1.15;
    private const double CameraRetargetIntervalSec = 20.0; // pick new target every ~20s
    private const double CameraLerpSpeed = 0.3; // very slow convergence

    public GravitySimulator(
        List<FrameworkElement> blobs,
        List<BlobState> states,
        List<SolidColorBrush> brushes,
        List<RadialGradientBrush> gradBrushes,
        Canvas canvas,
        double intensity,
        double speedMultiplier)
    {
        _blobs = blobs;
        _states = states;
        _minBodies = Math.Max(10, blobs.Count / 2);
        _brushes = brushes;
        _gradBrushes = gradBrushes;
        _canvas = canvas;
        _intensity = intensity;
        _speedMultiplier = Math.Max(0.1, speedMultiplier);

        // Set up camera roam
        if (GravityBlobPattern.CameraRoam)
        {
            _cameraScale = new ScaleTransform(1.0, 1.0, canvas.ActualWidth * 0.5, canvas.ActualHeight * 0.5);
            _cameraRotate = new RotateTransform(0.0, canvas.ActualWidth * 0.5, canvas.ActualHeight * 0.5);
            var group = new TransformGroup();
            group.Children.Add(_cameraScale);
            group.Children.Add(_cameraRotate);
            canvas.RenderTransform = group;
            _cameraRetargetTimer = 0; // pick first target immediately
            PickCameraTarget();
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
        CompositionTarget.Rendering += OnRendering;
    }

    public void Dispose()
    {
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
        _stopwatch.Stop();

        // Reset camera transform
        if (_cameraScale != null)
        {
            _canvas.RenderTransform = null;
            _cameraScale = null;
            _cameraRotate = null;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_running) return;
        long nowTicks = _stopwatch.ElapsedTicks;
        double dt = Math.Min((double)(nowTicks - _lastTickTicks) / Stopwatch.Frequency, MaxDt);
        _lastTickTicks = nowTicks;
        if (dt <= 0) return;

        int count = Math.Min(_blobs.Count, _states.Count);
        double cw = Math.Max(1, _canvas.ActualWidth);
        double ch = Math.Max(1, _canvas.ActualHeight);

        // --- Compute gravitational accelerations ---
        // Store accelerations in temporary arrays to avoid N² position reads
        Span<double> ax = stackalloc double[count];
        Span<double> ay = stackalloc double[count];

        for (int i = 0; i < count; i++)
        {
            double xi = Canvas.GetLeft(_blobs[i]) + _blobs[i].Width * 0.5;
            double yi = Canvas.GetTop(_blobs[i]) + _blobs[i].Height * 0.5;
            double mi = MassOf(_blobs[i]);

            for (int j = i + 1; j < count; j++)
            {
                double xj = Canvas.GetLeft(_blobs[j]) + _blobs[j].Width * 0.5;
                double yj = Canvas.GetTop(_blobs[j]) + _blobs[j].Height * 0.5;
                double mj = MassOf(_blobs[j]);

                double dx = xj - xi;
                double dy = yj - yi;
                double distSq = dx * dx + dy * dy + SofteningSq;
                double dist = Math.Sqrt(distSq);
                double force = GravityBlobPattern.GravityG * mi * mj / distSq;

                // Close-range repulsion to encourage stable orbits
                double ri = _blobs[i].Width * 0.5;
                double rj = _blobs[j].Width * 0.5;
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
            ax[i] += CentralGravity * cdx / cdist;
            ay[i] += CentralGravity * cdy / cdist;
        }

        // --- Integrate velocities and positions (Euler) ---
        for (int i = 0; i < count; i++)
        {
            var s = _states[i];
            s.VelocityX += ax[i] * dt * _speedMultiplier;
            s.VelocityY += ay[i] * dt * _speedMultiplier;

            double x = Canvas.GetLeft(_blobs[i]) + s.VelocityX * dt;
            double y = Canvas.GetTop(_blobs[i]) + s.VelocityY * dt;

            Canvas.SetLeft(_blobs[i], x);
            Canvas.SetTop(_blobs[i], y);

            // Smooth merge lerp: glide toward center-of-mass target
            if (!double.IsNaN(s.MergeTargetX))
            {
                double curX = Canvas.GetLeft(_blobs[i]);
                double curY = Canvas.GetTop(_blobs[i]);
                double lerpFactor = Math.Min(1.0, 8.0 * dt); // ~8x/sec convergence
                double newX = curX + (s.MergeTargetX - curX) * lerpFactor;
                double newY = curY + (s.MergeTargetY - curY) * lerpFactor;
                Canvas.SetLeft(_blobs[i], newX);
                Canvas.SetTop(_blobs[i], newY);

                // Clear target once close enough
                if (Math.Abs(s.MergeTargetX - newX) < 0.5 && Math.Abs(s.MergeTargetY - newY) < 0.5)
                {
                    s.MergeTargetX = double.NaN;
                    s.MergeTargetY = double.NaN;
                }
            }

            // Decay merge immunity
            if (s.MergeImmunity > 0)
                s.MergeImmunity = Math.Max(0, s.MergeImmunity - dt);
        }

        // --- Collision detection: merge or split ---
        ProcessCollisions(cw, ch);

        // --- Dust injection if population is low ---
        if (_blobs.Count < _minBodies)
        {
            int toAdd = Math.Min(3, _minBodies - _blobs.Count);
            for (int d = 0; d < toAdd; d++)
                InjectDust(cw, ch);
        }

        // --- Collapse explosion: if only 1 body left, it explodes into many small bodies ---
        if (_blobs.Count == 1)
            ExplodeLastBody(cw, ch);

        // --- Camera roam ---
        UpdateCamera(dt);
    }

    private void UpdateCamera(double dt)
    {
        if (_cameraScale == null || _cameraRotate == null) return;

        _cameraRetargetTimer -= dt;
        if (_cameraRetargetTimer <= 0)
        {
            PickCameraTarget();
            _cameraRetargetTimer = CameraRetargetIntervalSec + _rng.NextDouble() * 10.0;
        }

        // Very slow exponential lerp toward targets
        double lerpFactor = 1.0 - Math.Pow(1.0 - CameraLerpSpeed, dt);
        _cameraZoom += (_cameraTargetZoom - _cameraZoom) * lerpFactor;
        _cameraAngle += (_cameraTargetAngle - _cameraAngle) * lerpFactor;

        _cameraScale.ScaleX = _cameraZoom;
        _cameraScale.ScaleY = _cameraZoom;
        _cameraRotate.Angle = _cameraAngle;
    }

    private void PickCameraTarget()
    {
        _cameraTargetZoom = CameraMinZoom + _rng.NextDouble() * (CameraMaxZoom - CameraMinZoom);
        // Full 360° rotation range, but small increments from current angle
        _cameraTargetAngle = _cameraAngle + (_rng.NextDouble() - 0.5) * 30.0;
    }

    private void ProcessCollisions(double cw, double ch)
    {
        // Iterate backwards so removals don't invalidate indices
        for (int i = _blobs.Count - 1; i >= 0 && i < _states.Count; i--)
        {
            for (int j = i - 1; j >= 0 && j < _states.Count; j--)
            {
                if (i >= _blobs.Count || j >= _blobs.Count) continue;

                double x1 = Canvas.GetLeft(_blobs[i]) + _blobs[i].Width * 0.5;
                double y1 = Canvas.GetTop(_blobs[i]) + _blobs[i].Height * 0.5;
                double x2 = Canvas.GetLeft(_blobs[j]) + _blobs[j].Width * 0.5;
                double y2 = Canvas.GetTop(_blobs[j]) + _blobs[j].Height * 0.5;

                double r1 = _blobs[i].Width * 0.5;
                double r2 = _blobs[j].Width * 0.5;
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

                // Immunity suppresses pierce and split but still allows merge and bounce.
                // This prevents the feedback loop where pierce → immunity → gravity return → pierce again.
                bool shouldPierce = !eitherImmune
                                    && relSpeed > SplitSpeedThreshold
                                    && massRatio >= MassRatioMergeThreshold
                                    && _blobs.Count + 1 <= MaxBodies
                                    && _rng.NextDouble() < PierceProbability;

                bool shouldSplit = !eitherImmune && !shouldPierce
                                   && relSpeed > SplitSpeedThreshold && dot < -MergeAlignmentThreshold
                                   && massRatio < MassRatioMergeThreshold
                                   && _blobs.Count + 2 <= MaxBodies;
                bool shouldMerge = !shouldSplit && !shouldPierce
                                   && (dot > MergeAlignmentThreshold
                                   || relSpeed < SplitSpeedThreshold * 0.5
                                   || massRatio >= MassRatioMergeThreshold);

                if (shouldPierce)
                {
                    PierceBody(i, j, nx, ny, cw, ch);
                    break;
                }
                else if (shouldMerge)
                {
                    MergeBodies(i, j);
                    break;
                }
                else if (shouldSplit)
                {
                    SplitBody(i, j, nx, ny, cw, ch);
                    break;
                }
                else
                {
                    // Elastic bounce (like BounceSimulator)
                    double m1 = MassOf(_blobs[i]);
                    double m2 = MassOf(_blobs[j]);
                    double dvn = dvx * nx + dvy * ny;
                    if (dvn > 0)
                    {
                        double impulse = (2.0 * dvn) / (m1 + m2);
                        s1.VelocityX -= impulse * m2 * nx;
                        s1.VelocityY -= impulse * m2 * ny;
                        s2.VelocityX += impulse * m1 * nx;
                        s2.VelocityY += impulse * m1 * ny;

                        // Separate overlap
                        double overlap = minDist - dist;
                        double sep = overlap * 0.5 + 0.5;
                        Canvas.SetLeft(_blobs[i], Canvas.GetLeft(_blobs[i]) - nx * sep);
                        Canvas.SetTop(_blobs[i], Canvas.GetTop(_blobs[i]) - ny * sep);
                        Canvas.SetLeft(_blobs[j], Canvas.GetLeft(_blobs[j]) + nx * sep);
                        Canvas.SetTop(_blobs[j], Canvas.GetTop(_blobs[j]) + ny * sep);
                    }
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
        double r1 = _blobs[i].Width * 0.5;
        double r2 = _blobs[j].Width * 0.5;
        double m1 = MassOf(_blobs[i]);
        double m2 = MassOf(_blobs[j]);
        double totalMass = m1 + m2;

        // Reposition survivor to mass-weighted center to prevent visual jumping
        double x1 = Canvas.GetLeft(_blobs[i]) + _blobs[i].Width * 0.5;
        double y1 = Canvas.GetTop(_blobs[i]) + _blobs[i].Height * 0.5;
        double x2 = Canvas.GetLeft(_blobs[j]) + _blobs[j].Width * 0.5;
        double y2 = Canvas.GetTop(_blobs[j]) + _blobs[j].Height * 0.5;
        double cx = (x1 * m1 + x2 * m2) / totalMass;
        double cy = (y1 * m1 + y2 * m2) / totalMass;

        // Momentum-conserving velocity
        _states[j].VelocityX = (m1 * _states[i].VelocityX + m2 * _states[j].VelocityX) / totalMass;
        _states[j].VelocityY = (m1 * _states[i].VelocityY + m2 * _states[j].VelocityY) / totalMass;

        // Area-preserving new radius
        double newRadius = Math.Sqrt(r1 * r1 + r2 * r2);
        double newSize = newRadius * 2.0;
        _blobs[j].Width = newSize;
        _blobs[j].Height = newSize;
        _states[j].BaseSize = newSize;

        // Smooth merge: lerp survivor toward center of mass over several frames
        // instead of teleporting (prevents visual jumping).
        _states[j].MergeTargetX = cx - newSize * 0.5;
        _states[j].MergeTargetY = cy - newSize * 0.5;

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
        for (int f = 0; f < fragments && _blobs.Count < MaxBodies; f++)
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

        for (int f = 0; f < 2 && _blobs.Count < MaxBodies; f++)
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
        // Inject at a random edge position
        double size = DustSize + _rng.NextDouble() * 4;
        double x, y, vx, vy;
        int edge = _rng.Next(4);
        double speed = 20 + _rng.NextDouble() * 40;
        switch (edge)
        {
            case 0: x = _rng.NextDouble() * cw; y = 0; vx = (_rng.NextDouble() - 0.5) * speed; vy = speed; break;
            case 1: x = _rng.NextDouble() * cw; y = ch; vx = (_rng.NextDouble() - 0.5) * speed; vy = -speed; break;
            case 2: x = 0; y = _rng.NextDouble() * ch; vx = speed; vy = (_rng.NextDouble() - 0.5) * speed; break;
            default: x = cw; y = _rng.NextDouble() * ch; vx = -speed; vy = (_rng.NextDouble() - 0.5) * speed; break;
        }

        CreateBody(x - size * 0.5, y - size * 0.5, size, vx, vy);
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

        for (int i = 0; i < spawnCount && _blobs.Count < MaxBodies; i++)
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

    private void CreateBody(double x, double y, double size, double vx, double vy, Color? color = null)
    {
        Color c = color ?? RandomHue();
        var brush = new SolidColorBrush(c);
        var gradBrush = MakeSubtleGradient(c);

        double opacity = _intensity + _rng.NextDouble() * 0.1;

        var blob = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = gradBrush,
            Opacity = opacity,
            RenderTransformOrigin = new Point(0.5, 0.5),
            CacheMode = new BitmapCache(1.0),
        };

        Canvas.SetLeft(blob, x);
        Canvas.SetTop(blob, y);
        _canvas.Children.Add(blob);

        _blobs.Add(blob);
        _brushes.Add(brush);
        _gradBrushes.Add(gradBrush);
        _states.Add(new BlobState
        {
            VelocityX = vx,
            VelocityY = vy,
            BaseSize = size,
            BaseOpacity = opacity,
        });
    }

    /// <summary>
    /// Mass-weighted RGB blend of two bodies' colors. Updates the surviving body's brushes.
    /// </summary>
    private void BlendColor(int src, int dst, double mSrc, double mDst, double totalMass)
    {
        Color c1 = _brushes[src].Color;
        Color c2 = _brushes[dst].Color;
        byte r = (byte)((c1.R * mSrc + c2.R * mDst) / totalMass);
        byte g = (byte)((c1.G * mSrc + c2.G * mDst) / totalMass);
        byte b = (byte)((c1.B * mSrc + c2.B * mDst) / totalMass);
        Color blended = Color.FromRgb(r, g, b);

        _brushes[dst].Color = blended;
        var newGrad = MakeSubtleGradient(blended);
        _gradBrushes[dst] = newGrad;
        _blobs[dst].SetValue(Shape.FillProperty, newGrad);
    }

    /// <summary>
    /// Creates a subtle radial gradient — solid center fading to transparent at the edge.
    /// Just enough softness to avoid a hard vector look.
    /// </summary>
    private static RadialGradientBrush MakeSubtleGradient(Color c)
    {
        return new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
            GradientStops = new GradientStopCollection
            {
                new(c, 0.0),
                new(c, 0.7),
                new(Color.FromArgb(0, c.R, c.G, c.B), 1.0),
            }
        };
    }

    private Color RandomHue()
    {
        // HSV with full saturation/value, random hue
        double h = _rng.NextDouble() * 360.0;
        return HsvToRgb(h, 0.9, 1.0);
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
        // Boundary extends to 2× canvas dimensions. Force ramps up linearly
        // starting at the canvas edge (0 at edge, full strength at 2× boundary).
        double bw = cw; // boundary depth = full canvas width beyond each edge
        double bh = ch;

        if (x < 0) ax += BoundaryStrength * Math.Min(1.0, -x / bw);
        else if (x > cw) ax -= BoundaryStrength * Math.Min(1.0, (x - cw) / bw);
        else if (x < BoundaryMargin) ax += BoundaryStrength * 0.3 * (1.0 - x / BoundaryMargin);
        else if (x > cw - BoundaryMargin) ax -= BoundaryStrength * 0.3 * (1.0 - (cw - x) / BoundaryMargin);

        if (y < 0) ay += BoundaryStrength * Math.Min(1.0, -y / bh);
        else if (y > ch) ay -= BoundaryStrength * Math.Min(1.0, (y - ch) / bh);
        else if (y < BoundaryMargin) ay += BoundaryStrength * 0.3 * (1.0 - y / BoundaryMargin);
        else if (y > ch - BoundaryMargin) ay -= BoundaryStrength * 0.3 * (1.0 - (ch - y) / BoundaryMargin);
    }

    private static double MassOf(FrameworkElement blob) => blob.Width * blob.Width;
}
