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
    private const double CometTrailChance = 1.0;       // probability that dust injection spawns a comet trail instead
    private const double DiagDriftRadiusX = 25.0;       // horizontal drift radius for OLED burn-in prevention
    private const double DiagDriftRadiusY = 20.0;       // vertical drift radius
    private const double DiagDriftPeriodSec = 120.0;    // seconds for one full elliptical loop

    private readonly Canvas _canvas;
    private readonly List<FrameworkElement> _blobs;
    private readonly List<BlobState> _states;
    private readonly List<SolidColorBrush> _brushes;
    private readonly List<RadialGradientBrush> _gradBrushes;
    private readonly double _intensity;
    private readonly double _speedMultiplier;
    private readonly int _minBodies;
    private readonly int _maxBodies;
    private int _dustCooldown;
    private readonly Random _rng = new();

    // --- Cached per-frame arrays (avoid O(N²) dependency-property reads) ---
    private double[] _posX = [];   // center X of each body
    private double[] _posY = [];   // center Y of each body
    private double[] _radii = [];  // half-width of each body
    private double[] _masses = []; // mass (width²) of each body
    private readonly Stopwatch _stopwatch = new();
    private long _lastTickTicks;
    private bool _running;

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
    private const double CameraMaxZoom = 1.3;
    private const double CameraDriftIntervalSec = 20.0;
    private const double CameraZoomLerpSpeed = 0.5;   // per-second convergence for zoom
    private const double CameraDriftLerpSpeed = 0.3;   // per-second convergence for rotation/pan
    private const double CameraMassFraction = 0.80;    // fraction of total mass to keep in frame

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
        _maxBodies = Math.Max(10, (int)(MaxBodiesDefault * GravityBlobPattern.BlobMultiplier));
        _minBodies = Math.Max(5, blobs.Count / 3);
        _brushes = brushes;
        _gradBrushes = gradBrushes;
        _canvas = canvas;
        _intensity = intensity;
        _speedMultiplier = Math.Max(0.1, speedMultiplier);

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
            double pertStr = GravityBlobPattern.OrbitalPerturbation;
            if (pertStr > 0 && cdist > 1.0)
            {
                // Tangent direction (perpendicular to center vector)
                double tx = -cdy / cdist;
                double ty = cdx / cdist;
                // Strength scales with distance from center and setting
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
        }

        // --- Collision detection: merge or split ---
        ProcessCollisions(cw, ch);

        // --- Dust injection if population is low ---
        if (_blobs.Count < _minBodies && _dustCooldown <= 0)
        {
            if (_rng.NextDouble() < CometTrailChance && _blobs.Count >= 3)
                InjectCometTrail();
            else
                InjectDust(cw, ch);
            _dustCooldown = 10;
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
        int count = Math.Min(_blobs.Count, _states.Count);
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
        double driftLerp = 1.0 - Math.Pow(1.0 - CameraDriftLerpSpeed, dt);

        _cameraZoom += (targetZoom - _cameraZoom) * zoomLerp;
        _cameraAngle += (_cameraTargetAngle - _cameraAngle) * driftLerp;
        _cameraOffsetX += (targetOffsetX - _cameraOffsetX) * zoomLerp;
        _cameraOffsetY += (targetOffsetY - _cameraOffsetY) * zoomLerp;

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
        int count = Math.Min(_blobs.Count, _states.Count);

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
        // Track which bodies already participated in a collision this frame
        // to prevent chain-merge cascades that cause popping/teleporting.
        Span<bool> collided = stackalloc bool[_blobs.Count];

        // Iterate backwards so removals don't invalidate indices
        for (int i = _blobs.Count - 1; i >= 0 && i < _states.Count; i--)
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

                    // Check if the survivor exceeds the supernova threshold
                    double supernovaThreshold = GravityBlobPattern.SupernovaMass;
                    if (supernovaThreshold > 0 && j < _blobs.Count && _blobs[j].Width >= supernovaThreshold)
                    {
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
        // Pick one of the top 20% largest bodies
        int count = _blobs.Count;
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

            CreateBody(fx - size * 0.5, fy - size * 0.5, size, tvx, tvy, Color.FromRgb(dr, dg, db));
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
                Math.Cos(angle) * speed, Math.Sin(angle) * speed, fragColor);
            _states[^1].MergeImmunity = 1.5; // longer immunity so fragments spread out
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
