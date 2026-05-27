using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Phosphor;

/// <summary>
/// Tron-style light cycle simulation for blob screensaver pattern.
/// Blobs move in cardinal directions, leave light trails, and explode
/// on trail collisions. Destroyed blobs respawn off-screen after a delay.
/// </summary>
public sealed class LightCycleSimulator : IDisposable
{
    private readonly record struct TrailSegment(double X1, double Y1, double X2, double Y2, int Owner);

    /// <summary>
    /// Spatial index for axis-aligned trail segments. Horizontal segments (Y1≈Y2)
    /// are bucketed by Y; vertical segments (X1≈X2) by X. Point queries only check
    /// buckets within <c>margin</c>, reducing collision checks from O(totalSegments)
    /// to O(segmentsInNearbyBuckets).
    /// </summary>
    private sealed class SegmentGrid
    {
        private readonly double _bucketSize;
        // Horizontal segments keyed by Y-bucket; vertical by X-bucket.
        private readonly Dictionary<int, List<TrailSegment>> _hBuckets = new();
        private readonly Dictionary<int, List<TrailSegment>> _vBuckets = new();

        public SegmentGrid(double bucketSize) => _bucketSize = Math.Max(1.0, bucketSize);

        private int Bucket(double v) => (int)Math.Floor(v / _bucketSize);

        public void Add(TrailSegment seg)
        {
            bool isHorizontal = Math.Abs(seg.Y1 - seg.Y2) < 1;
            if (isHorizontal)
            {
                int b = Bucket(seg.Y1);
                if (!_hBuckets.TryGetValue(b, out var list))
                    _hBuckets[b] = list = new List<TrailSegment>();
                list.Add(seg);
            }
            else
            {
                int b = Bucket(seg.X1);
                if (!_vBuckets.TryGetValue(b, out var list))
                    _vBuckets[b] = list = new List<TrailSegment>();
                list.Add(seg);
            }
        }

        public void RemoveAll(int owner)
        {
            RemoveOwner(_hBuckets, owner);
            RemoveOwner(_vBuckets, owner);
        }

        private static void RemoveOwner(Dictionary<int, List<TrailSegment>> buckets, int owner)
        {
            // Iterate all buckets and remove segments matching owner.
            // Only called on cycle death — infrequent.
            foreach (var kvp in buckets)
                kvp.Value.RemoveAll(s => s.Owner == owner);
        }

        /// <summary>
        /// Check if point (px,py) is within <paramref name="margin"/> of any
        /// committed segment, excluding segments owned by <paramref name="selfIdx"/>
        /// that are the last segment of the given <paramref name="selfLastSeg"/>.
        /// </summary>
        public bool Query(double px, double py, double margin, int selfIdx,
            TrailSegment? selfLastSeg, out int hitOwner)
        {
            hitOwner = -1;
            int bLow = Bucket(py - margin);
            int bHigh = Bucket(py + margin);
            // Check horizontal segments whose Y is near py
            for (int b = bLow; b <= bHigh; b++)
            {
                if (!_hBuckets.TryGetValue(b, out var list)) continue;
                foreach (var seg in list)
                {
                    if (seg.Owner == selfIdx && selfLastSeg.HasValue &&
                        seg == selfLastSeg.Value)
                        continue;
                    if (PointNearAxisAligned(px, py, seg, margin))
                    {
                        hitOwner = seg.Owner;
                        return true;
                    }
                }
            }

            bLow = Bucket(px - margin);
            bHigh = Bucket(px + margin);
            // Check vertical segments whose X is near px
            for (int b = bLow; b <= bHigh; b++)
            {
                if (!_vBuckets.TryGetValue(b, out var list)) continue;
                foreach (var seg in list)
                {
                    if (seg.Owner == selfIdx && selfLastSeg.HasValue &&
                        seg == selfLastSeg.Value)
                        continue;
                    if (PointNearAxisAligned(px, py, seg, margin))
                    {
                        hitOwner = seg.Owner;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Fast point-near-segment for axis-aligned segments. Avoids the general
        /// projection math by exploiting H/V alignment.
        /// </summary>
        private static bool PointNearAxisAligned(double px, double py, TrailSegment seg, double margin)
        {
            double marginSq = margin * margin;
            if (Math.Abs(seg.Y1 - seg.Y2) < 1) // horizontal
            {
                double dy = py - seg.Y1;
                if (dy * dy > marginSq) return false;
                double minX = Math.Min(seg.X1, seg.X2);
                double maxX = Math.Max(seg.X1, seg.X2);
                double cx = Math.Clamp(px, minX, maxX);
                double dx = px - cx;
                return dx * dx + dy * dy < marginSq;
            }
            else // vertical
            {
                double dx = px - seg.X1;
                if (dx * dx > marginSq) return false;
                double minY = Math.Min(seg.Y1, seg.Y2);
                double maxY = Math.Max(seg.Y1, seg.Y2);
                double cy = Math.Clamp(py, minY, maxY);
                double dy = py - cy;
                return dx * dx + dy * dy < marginSq;
            }
        }
    }

    private sealed class CycleState
    {
        public bool Alive = true;
        public double X, Y;
        /// <summary>Direction: 0=right, 1=down, 2=left, 3=up</summary>
        public int Direction;
        public double Speed;
        public double TrailStartX, TrailStartY;
        public readonly List<Line> TrailLines = new();
        public readonly List<UIElement> CornerPatches = new();
        public readonly List<TrailSegment> Segments = new();
        public double RespawnTimer;
        public double TurnCooldown;
        /// <summary>Ticks until the next random turn attempt.</summary>
        public double NextTurnIn;
        /// <summary>The continuously-updated line from last turn to current position.</summary>
        public Line? LiveLine;
        /// <summary>Brush used by the live line, updated each tick to match blob color.</summary>
        public SolidColorBrush? LiveBrush;
    }

    private readonly List<FrameworkElement> _blobs;
    private readonly List<BlobState> _blobStates;
    private readonly Canvas _canvas;
    private readonly Canvas _trailLayer;
    private readonly Canvas _gridLayer;
    private readonly Random _rng = new();
    private readonly List<CycleState> _cycles = new();
    private readonly Stopwatch _stopwatch = new();
    private long _lastTickTicks;
    private bool _running;
    private readonly double _speedMultiplier;
    private double _gridOffsetX;
    private double _gridOffsetY;
    private double _gridLastWidth;
    private double _gridLastHeight;
    private readonly TranslateTransform _gridTransform = new();
    private SegmentGrid? _segmentGrid;

    private const double TrailThicknessBase = 12.0;
    private readonly double _sizeMultiplier;
    private const double TrailLayerOpacity = 0.25;
    private const double GridSpacing = 25.0;
    private const double GridOpacity = 0.04;       // 100% opaque
    private const double GridLineThickness = 2.0;
    private const double GridDriftSpeed = 0.2;     // pixels per second
    private const double RespawnDelay = 2.5;      // seconds before respawn
    private const double TrailFadeDuration = 0.7;  // seconds for trail shrink-out
    private const double MinTurnInterval = 0.6;    // minimum seconds between turns
    private const double TurnCooldownTime = 0.3;   // seconds after a turn before another is allowed
    private double TrailThickness => TrailThicknessBase * _sizeMultiplier;
    private double CollisionMargin => TrailThickness + 2;   // prevent any visual trail overlap

    public LightCycleSimulator(List<FrameworkElement> blobs, List<BlobState> states, Canvas canvas, double speedMultiplier = 1.0, double sizeMultiplier = 1.0)
    {
        _blobs = blobs;
        _blobStates = states;
        _canvas = canvas;
        _speedMultiplier = Math.Max(0.1, speedMultiplier);
        _sizeMultiplier = sizeMultiplier;

        // Aqua grid background — drifts slowly for burn-in protection
        _gridLayer = new Canvas
        {
            Opacity = GridOpacity,
            IsHitTestVisible = false,
        };
        _canvas.Children.Insert(0, _gridLayer);

        // Shared trail layer: all trail elements are fully opaque inside,
        // the single container Opacity avoids overlap artifacts at corners.
        _trailLayer = new Canvas
        {
            Opacity = TrailLayerOpacity,
            IsHitTestVisible = false,
        };
        _canvas.Children.Insert(1, _trailLayer);

        for (int i = 0; i < _blobs.Count && i < _blobStates.Count; i++)
        {
            _blobs[i].BeginAnimation(Canvas.LeftProperty, null);
            _blobs[i].BeginAnimation(Canvas.TopProperty, null);

            // Size blob to 6x trail thickness
            double newSize = TrailThickness * 6.0;
            _blobs[i].Width = newSize;
            _blobs[i].Height = newSize;
            _blobs[i].Opacity = 1.0;

            // Tight gradient so the blob is small and solid
            if ((_blobs[i] as Shape)?.Fill is RadialGradientBrush rgb)
            {
                rgb.RadiusX = 0.35;
                rgb.RadiusY = 0.35;
                if (rgb.GradientStops.Count >= 3)
                {
                    rgb.GradientStops[0].Color = System.Windows.Media.Color.FromArgb(255, rgb.GradientStops[0].Color.R, rgb.GradientStops[0].Color.G, rgb.GradientStops[0].Color.B);
                    rgb.GradientStops[1].Offset = 0.7;
                    rgb.GradientStops[2].Offset = 1.0;
                }
            }

            _blobStates[i].BaseOpacity = 1.0;

            double x = Canvas.GetLeft(_blobs[i]) + newSize * 0.5;
            double y = Canvas.GetTop(_blobs[i]) + newSize * 0.5;

            // Snap to integer coords so movement stays perfectly axis-aligned
            x = Math.Round(x);
            y = Math.Round(y);

            var cycle = new CycleState
            {
                X = x,
                Y = y,
                Direction = (((_blobStates[i].Phase % 4) + 4) % 4),
                Speed = _blobStates[i].SpeedFactor,
                TrailStartX = x,
                TrailStartY = y,
                NextTurnIn = 1.5 + _rng.NextDouble() * 3.0,
            };
            _cycles.Add(cycle);

            // Create the live trail line immediately and store it on the cycle
            Canvas.SetLeft(_blobs[i], x - newSize * 0.5);
            Canvas.SetTop(_blobs[i], y - newSize * 0.5);

            var liveBrush = new SolidColorBrush(Colors.Cyan);
            cycle.LiveBrush = liveBrush;
            cycle.LiveLine = new Line
            {
                X1 = x, Y1 = y, X2 = x, Y2 = y,
                Stroke = liveBrush,
                StrokeThickness = TrailThickness,
                Opacity = 1.0,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true,
                StrokeStartLineCap = PenLineCap.Flat,
                StrokeEndLineCap = PenLineCap.Flat,
            };
            _trailLayer.Children.Add(cycle.LiveLine);
        }

        _gridLayer.RenderTransform = _gridTransform;
    }

    public void Start()
    {
        _segmentGrid = new SegmentGrid(CollisionMargin);
        _stopwatch.Restart();
        _lastTickTicks = _stopwatch.ElapsedTicks;
        _running = true;
        CompositionTarget.Rendering += OnRendering;
    }

    public void Stop()
    {
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
        _stopwatch.Stop();
    }

    public void Dispose()
    {
        Stop();

        // Remove grid and trail layers
        _canvas.Children.Remove(_gridLayer);
        _canvas.Children.Remove(_trailLayer);
    }

    private static (double dx, double dy) DirVector(int dir) => dir switch
    {
        0 => (1, 0),
        1 => (0, 1),
        2 => (-1, 0),
        _ => (0, -1),
    };

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_running) return;
        long nowTicks = _stopwatch.ElapsedTicks;
        double dt = Math.Min((double)(nowTicks - _lastTickTicks) / Stopwatch.Frequency, 0.05);
        _lastTickTicks = nowTicks;
        if (dt <= 0) return;

        double cw = Math.Max(1, _canvas.ActualWidth);
        double ch = Math.Max(1, _canvas.ActualHeight);

        UpdateGrid(dt, cw, ch);

        for (int i = 0; i < _cycles.Count && i < _blobs.Count; i++)
        {
            var c = _cycles[i];

            if (!c.Alive)
            {
                c.RespawnTimer -= dt;
                if (c.RespawnTimer <= 0)
                    Respawn(i, cw, ch);
                continue;
            }

            // Decrease cooldowns
            c.TurnCooldown = Math.Max(0, c.TurnCooldown - dt);
            c.NextTurnIn -= dt;

            var (dx, dy) = DirVector(c.Direction);
            double move = c.Speed * dt;
            // Hard-lock to cardinal direction: only move on one axis
            double newX, newY;
            if (dx != 0)
            {
                newX = c.X + dx * move;
                newY = c.TrailStartY; // lock Y to the value set at last turn
            }
            else
            {
                newX = c.TrailStartX; // lock X to the value set at last turn
                newY = c.Y + dy * move;
            }

            // Check if about to hit a trail — try to turn to avoid
            if (c.TurnCooldown <= 0 && WouldHitTrail(i, newX, newY))
            {
                int turned = TryTurn(i, c, cw, ch, dt);
                if (turned >= 0)
                {
                    // Snap position before committing so the trail corner is clean
                    c.X = Math.Round(c.X);
                    c.Y = Math.Round(c.Y);
                    CommitTrailSegment(i, c);
                    c.Direction = turned;
                    c.TurnCooldown = TurnCooldownTime;
                    c.NextTurnIn = 1.0 + _rng.NextDouble() * 2.5;
                    (dx, dy) = DirVector(c.Direction);
                    if (dx != 0)
                    {
                        newX = c.X + dx * move;
                        newY = c.TrailStartY;
                    }
                    else
                    {
                        newX = c.TrailStartX;
                        newY = c.Y + dy * move;
                    }

                    // If still hitting a trail after turning, destroy
                    if (WouldHitTrail(i, newX, newY))
                    {
                        DestroyCycle(i);
                        continue;
                    }
                }
                else
                {
                    // No safe turn — destroy
                    DestroyCycle(i);
                    continue;
                }
            }

            // Random turns for variety
            if (c.NextTurnIn <= 0 && c.TurnCooldown <= 0)
            {
                int turnDir = PickRandomTurn(i, c, cw, ch, dt);
                if (turnDir >= 0)
                {
                    c.X = Math.Round(c.X);
                    c.Y = Math.Round(c.Y);
                    CommitTrailSegment(i, c);
                    c.Direction = turnDir;
                    c.TurnCooldown = TurnCooldownTime;

                    // Recompute newX/newY for the new direction
                    (dx, dy) = DirVector(c.Direction);
                    if (dx != 0)
                    {
                        newX = c.X + dx * move;
                        newY = c.TrailStartY;
                    }
                    else
                    {
                        newX = c.TrailStartX;
                        newY = c.Y + dy * move;
                    }
                }
                c.NextTurnIn = MinTurnInterval + _rng.NextDouble() * 3.0;
            }

            // Check actual trail collision at new position
            if (CheckTrailCollision(i, newX, newY, out int hitOwner))
            {
                DestroyCycle(i);
                // Head-on: if the other cycle is at approximately the same spot, destroy it too
                if (hitOwner >= 0 && hitOwner < _cycles.Count && _cycles[hitOwner].Alive)
                {
                    double ox = _cycles[hitOwner].X;
                    double oy = _cycles[hitOwner].Y;
                    double dist = Math.Sqrt((newX - ox) * (newX - ox) + (newY - oy) * (newY - oy));
                    double blobR = TrailThickness + CollisionMargin;
                    if (dist < blobR)
                        DestroyCycle(hitOwner);
                }
                continue;
            }

            // Snap cross-axis to integer to prevent sub-pixel drift
            if (dx != 0)
            {
                c.X = newX;
                c.Y = Math.Round(c.TrailStartY);
            }
            else
            {
                c.X = Math.Round(c.TrailStartX);
                c.Y = newY;
            }

            // Update blob position
            Canvas.SetLeft(_blobs[i], c.X - _blobs[i].Width * 0.5);
            Canvas.SetTop(_blobs[i], c.Y - _blobs[i].Height * 0.5);

            // Update live trail line directly
            if (c.LiveLine != null)
            {
                c.LiveLine.X1 = Math.Round(c.TrailStartX);
                c.LiveLine.Y1 = Math.Round(c.TrailStartY);
                c.LiveLine.X2 = Math.Round(c.X);
                c.LiveLine.Y2 = Math.Round(c.Y);

                // Sync live line color with blob's current color (set by color cycling timer)
                if (c.LiveBrush != null && (_blobs[i] as Shape)?.Fill is RadialGradientBrush liveGb && liveGb.GradientStops.Count > 0)
                    c.LiveBrush.Color = liveGb.GradientStops[0].Color;
            }

            // Off-screen check — if fully off screen, respawn
            double margin = _blobs[i].Width;
            if (c.X < -margin || c.X > cw + margin || c.Y < -margin || c.Y > ch + margin)
            {
                DestroyCycle(i);
            }
        }

        // Check blob-blob head-on collisions
        for (int i = 0; i < _cycles.Count; i++)
        {
            if (!_cycles[i].Alive) continue;
            for (int j = i + 1; j < _cycles.Count; j++)
            {
                if (!_cycles[j].Alive) continue;
                double dist = Math.Sqrt(
                    (_cycles[i].X - _cycles[j].X) * (_cycles[i].X - _cycles[j].X) +
                    (_cycles[i].Y - _cycles[j].Y) * (_cycles[i].Y - _cycles[j].Y));
                double minDist = TrailThickness + CollisionMargin;
                if (dist < minDist)
                {
                    DestroyCycle(i);
                    DestroyCycle(j);
                }
            }
        }
    }

    private void CommitTrailSegment(int idx, CycleState c)
    {
        double sx = c.TrailStartX, sy = c.TrailStartY;
        double ex = c.X, ey = c.Y;
        if (Math.Abs(sx - ex) < 1 && Math.Abs(sy - ey) < 1) return;

        c.Segments.Add(new TrailSegment(sx, sy, ex, ey, idx));
        _segmentGrid?.Add(new TrailSegment(sx, sy, ex, ey, idx));

        var brush = GetTrailBrush(idx);

        var clonedBrush = brush.Clone();
        clonedBrush.Freeze();
        var line = new Line
        {
            X1 = sx, Y1 = sy, X2 = ex, Y2 = ey,
            Stroke = clonedBrush,
            StrokeThickness = TrailThickness,
            Opacity = 1.0,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true,
            StrokeStartLineCap = PenLineCap.Flat,
            StrokeEndLineCap = PenLineCap.Flat,
        };
        c.TrailLines.Add(line);
        _trailLayer.Children.Add(line);

        // Place a corner patch at the turn point to fill the gap between segments
        double half = TrailThickness * 0.5;
        var corner = new System.Windows.Shapes.Rectangle
        {
            Width = TrailThickness,
            Height = TrailThickness,
            Fill = clonedBrush, // reuse same frozen brush
            Opacity = 1.0,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true,
        };
        Canvas.SetLeft(corner, Math.Round(ex) - half);
        Canvas.SetTop(corner, Math.Round(ey) - half);
        c.CornerPatches.Add(corner);
        _trailLayer.Children.Add(corner);

        c.TrailStartX = Math.Round(c.X);
        c.TrailStartY = Math.Round(c.Y);
    }

    private SolidColorBrush GetTrailBrush(int idx)
    {
        return (_blobs[idx] as Shape)?.Fill is RadialGradientBrush gb
            ? new SolidColorBrush(gb.GradientStops.Count > 0 ? gb.GradientStops[0].Color : Colors.Cyan)
            : ((_blobs[idx] as Shape)?.Fill as SolidColorBrush ?? new SolidColorBrush(Colors.Cyan));
    }

    private bool WouldHitTrail(int selfIdx, double x, double y)
    {
        return CheckTrailCollision(selfIdx, x, y, out _);
    }

    private bool CheckTrailCollision(int selfIdx, double x, double y, out int hitOwner)
    {
        hitOwner = -1;

        // Check committed segments via spatial grid — O(nearby) instead of O(all).
        var c = _cycles[selfIdx];
        TrailSegment? lastSeg = c.Segments.Count > 0 ? c.Segments[^1] : null;
        if (_segmentGrid != null && _segmentGrid.Query(x, y, CollisionMargin, selfIdx, lastSeg, out hitOwner))
            return true;

        // Check live segments of other cycles (one per cycle — always small)
        for (int i = 0; i < _cycles.Count; i++)
        {
            if (i == selfIdx) continue;
            var other = _cycles[i];
            if (!other.Alive) continue;
            if (PointNearSegment(x, y, other.TrailStartX, other.TrailStartY, other.X, other.Y, CollisionMargin))
            {
                hitOwner = i;
                return true;
            }
        }
        return false;
    }

    private static bool PointNearSegment(double px, double py, double x1, double y1, double x2, double y2, double margin)
    {
        double dx = x2 - x1, dy = y2 - y1;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1) return false;

        double t = Math.Clamp(((px - x1) * dx + (py - y1) * dy) / lenSq, 0, 1);
        double cx = x1 + t * dx, cy = y1 + t * dy;
        double distSq = (px - cx) * (px - cx) + (py - cy) * (py - cy);
        return distSq < margin * margin;
    }

    private int TryTurn(int idx, CycleState c, double cw, double ch, double dt)
    {
        // Try both perpendicular directions, prefer the one with more open space
        int left = (c.Direction + 3) % 4;
        int right = (c.Direction + 1) % 4;

        double lookAhead = c.Speed * 0.5; // half-second look-ahead
        bool leftOk = !WouldHitTrailInDir(idx, c.X, c.Y, left, lookAhead, cw, ch);
        bool rightOk = !WouldHitTrailInDir(idx, c.X, c.Y, right, lookAhead, cw, ch);

        if (leftOk && rightOk)
            return _rng.NextDouble() < 0.5 ? left : right;
        if (leftOk) return left;
        if (rightOk) return right;
        return -1; // no safe turn
    }

    private int PickRandomTurn(int idx, CycleState c, double cw, double ch, double dt)
    {
        int left = (c.Direction + 3) % 4;
        int right = (c.Direction + 1) % 4;
        int pick = _rng.NextDouble() < 0.5 ? left : right;

        double lookAhead = c.Speed * 0.3;
        if (!WouldHitTrailInDir(idx, c.X, c.Y, pick, lookAhead, cw, ch))
            return pick;
        return -1;
    }

    private bool WouldHitTrailInDir(int selfIdx, double x, double y, int dir, double distance, double cw, double ch)
    {
        var (dx, dy) = DirVector(dir);
        double testX = x + dx * distance;
        double testY = y + dy * distance;

        // Would go off-screen?
        if (testX < 0 || testX > cw || testY < 0 || testY > ch)
            return true;

        return WouldHitTrail(selfIdx, testX, testY);
    }

    private void DestroyCycle(int idx)
    {
        var c = _cycles[idx];
        if (!c.Alive) return;
        c.Alive = false;
        c.RespawnTimer = RespawnDelay;

        // Commit final trail segment
        CommitTrailSegment(idx, c);

        // Explosion effect on the blob
        ExplodeBlob(idx);

        // Fade out and remove all trail lines after a delay
        FadeTrails(idx, c);
    }

    private void ExplodeBlob(int idx)
    {
        var blob = _blobs[idx];
        double originalSize = blob.Width;
        double originalOpacity = _blobStates[idx].BaseOpacity;

        // Quick scale-up + fade-out
        var dur = TimeSpan.FromMilliseconds(400);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        if (blob.RenderTransform is not ScaleTransform st)
        {
            st = new ScaleTransform(1.0, 1.0);
            blob.RenderTransform = st;
            blob.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        }

        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(2.5, dur) { EasingFunction = ease });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(2.5, dur) { EasingFunction = ease });

        var fadeAnim = new DoubleAnimation(0.0, dur) { EasingFunction = ease };
        fadeAnim.Completed += (_, _) =>
        {
            blob.Opacity = 0;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            st.ScaleX = 1.0;
            st.ScaleY = 1.0;
        };
        blob.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
    }

    private void FadeTrails(int ownerIdx, CycleState c)
    {
        var dur = TimeSpan.FromSeconds(TrailFadeDuration);
        var lines = c.TrailLines.ToList();

        // Also fade the live line
        if (c.LiveLine != null)
        {
            lines.Add(c.LiveLine);
            c.LiveLine = null;
        }

        foreach (var line in lines)
        {
            var shrink = new DoubleAnimation(0.0, dur);
            var capturedLine = line;
            shrink.Completed += (_, _) =>
            {
                _trailLayer.Children.Remove(capturedLine);
            };
            line.BeginAnimation(Line.StrokeThicknessProperty, shrink);
        }

        foreach (var patch in c.CornerPatches)
        {
            var capturedPatch = patch;
            double origSize = TrailThickness;
            double centerX = Canvas.GetLeft(patch) + origSize * 0.5;
            double centerY = Canvas.GetTop(patch) + origSize * 0.5;

            var shrinkW = new DoubleAnimation(0.0, dur);
            var shrinkH = new DoubleAnimation(0.0, dur);
            shrinkH.Completed += (_, _) =>
            {
                _trailLayer.Children.Remove(capturedPatch);
            };
            patch.BeginAnimation(FrameworkElement.WidthProperty, shrinkW);
            patch.BeginAnimation(FrameworkElement.HeightProperty, shrinkH);

            // Animate position to keep patch centered as it shrinks
            Canvas.SetLeft(patch, centerX - origSize * 0.5);
            Canvas.SetTop(patch, centerY - origSize * 0.5);
            var moveLeft = new DoubleAnimation(centerX, dur);
            var moveTop = new DoubleAnimation(centerY, dur);
            patch.BeginAnimation(Canvas.LeftProperty, moveLeft);
            patch.BeginAnimation(Canvas.TopProperty, moveTop);
        }

        c.TrailLines.Clear();
        c.CornerPatches.Clear();
        _segmentGrid?.RemoveAll(ownerIdx);
        c.Segments.Clear();
    }

    private void Respawn(int idx, double cw, double ch)
    {
        var c = _cycles[idx];
        var blob = _blobs[idx];

        // Pick a random edge and direction pointing inward
        int edge = _rng.Next(4);
        c.Direction = edge; // 0=right (from left edge), etc.
        double blobSize = blob.Width;

        double x, y;
        switch (edge)
        {
            case 0: x = -blobSize; y = _rng.NextDouble() * ch; break;
            case 1: x = _rng.NextDouble() * cw; y = -blobSize; break;
            case 2: x = cw + blobSize; y = _rng.NextDouble() * ch; break;
            default: x = _rng.NextDouble() * cw; y = ch + blobSize; break;
        }

        // Snap spawn position and lock the cross-axis to integer
        double spawnX = Math.Round(x);
        double spawnY = Math.Round(y);
        c.X = spawnX;
        c.Y = spawnY;
        c.TrailStartX = spawnX;
        c.TrailStartY = spawnY;
        c.Alive = true;
        c.TurnCooldown = 0;
        c.NextTurnIn = 1.5 + _rng.NextDouble() * 3.0;
        c.Speed = Math.Max(cw, ch) / (8.0 + _rng.NextDouble() * 4.0) * _speedMultiplier;

        Canvas.SetLeft(blob, c.X - blobSize * 0.5);
        Canvas.SetTop(blob, c.Y - blobSize * 0.5);

        // Restore opacity
        blob.BeginAnimation(UIElement.OpacityProperty, null);
        blob.Opacity = _blobStates[idx].BaseOpacity;

        // Create fresh live line with updatable brush
        var liveBrush = new SolidColorBrush(Colors.Cyan);
        c.LiveBrush = liveBrush;
        c.LiveLine = new Line
        {
            X1 = c.X, Y1 = c.Y, X2 = c.X, Y2 = c.Y,
                Stroke = liveBrush,
                StrokeThickness = TrailThickness,
                Opacity = 1.0,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true,
                StrokeStartLineCap = PenLineCap.Flat,
                StrokeEndLineCap = PenLineCap.Flat,
            };
            _trailLayer.Children.Add(c.LiveLine);
    }

    private void UpdateGrid(double dt, double cw, double ch)
    {
        // Drift the grid slowly in a diagonal direction
        _gridOffsetX = (_gridOffsetX + GridDriftSpeed * dt) % GridSpacing;
        _gridOffsetY = (_gridOffsetY + GridDriftSpeed * 0.7 * dt) % GridSpacing;

        // Rebuild grid lines when canvas size changes or on first run
        if (Math.Abs(cw - _gridLastWidth) > 1 || Math.Abs(ch - _gridLastHeight) > 1
            || _gridLayer.Children.Count == 0)
        {
            RebuildGrid(cw, ch);
        }

        // Shift the entire grid layer via a translate transform
        _gridTransform.X = _gridOffsetX - GridSpacing;
        _gridTransform.Y = _gridOffsetY - GridSpacing;
    }

    private void RebuildGrid(double cw, double ch)
    {
        _gridLayer.Children.Clear();
        _gridLastWidth = cw;
        _gridLastHeight = ch;

        var gridBrush = new SolidColorBrush(Colors.Cyan);
        gridBrush.Freeze();

        // Extra lines beyond edges to cover drift offset
        double extraW = cw + GridSpacing * 2;
        double extraH = ch + GridSpacing * 2;

        // Vertical lines
        for (double x = 0; x <= extraW; x += GridSpacing)
        {
            _gridLayer.Children.Add(new Line
            {
                X1 = x, Y1 = 0, X2 = x, Y2 = extraH,
                Stroke = gridBrush,
                StrokeThickness = GridLineThickness,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true,
            });
        }

        // Horizontal lines
        for (double y = 0; y <= extraH; y += GridSpacing)
        {
            _gridLayer.Children.Add(new Line
            {
                X1 = 0, Y1 = y, X2 = extraW, Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = GridLineThickness,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true,
            });
        }
    }
}
