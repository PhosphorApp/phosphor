using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Phosphor;

/// <summary>
/// Per-blob orbital state used by non-random blob patterns.
/// </summary>
public class BlobState
{
    public double Angle { get; set; }
    public double OrbitRadius { get; set; }
    public bool Clockwise { get; set; } = true;
    public double SpeedFactor { get; set; } = 1.0;
    public double AccumulatedAngle { get; set; }
    public double MaxOrbitRadius { get; set; }
    public double MinOrbitRadius { get; set; } = 50;
    /// <summary>Ellipse stretch factor for Rough patterns (0 = circle, up to ~0.6).</summary>
    public double Eccentricity { get; set; }
    /// <summary>Rotation angle of the ellipse major axis (radians).</summary>
    public double EllipseAngle { get; set; }
    /// <summary>Lava Lamp phase
    public int Phase { get; set; }
    /// <summary>Original blob size at creation, used as reference for Lava Lamp scaling.</summary>
    public double BaseSize { get; set; }
    /// <summary>Original blob opacity, used as reference for Lava Lamp dimming.</summary>
    public double BaseOpacity { get; set; }
    /// <summary>Bounce pattern: horizontal velocity in pixels/second.</summary>
    public double VelocityX { get; set; }
    /// <summary>Bounce pattern: vertical velocity in pixels/second.</summary>
    public double VelocityY { get; set; }
    /// <summary>Rough pattern: orbit center X offset from canvas center (fraction of canvas width).</summary>
    public double CenterOffsetX { get; set; }
    /// <summary>Rough pattern: orbit center Y offset from canvas center (fraction of canvas height).</summary>
    public double CenterOffsetY { get; set; }
    /// <summary>Fractal pattern: which ring/layer this blob belongs to.</summary>
    public int Ring { get; set; }
    /// <summary>Fractal pattern: index within the ring.</summary>
    public int RingIndex { get; set; }
    /// <summary>Fractal pattern: total blobs in this ring.</summary>
    public int RingCount { get; set; }
    /// <summary>Fractal pattern: current global rotation offset (radians).</summary>
    public double GlobalRotation { get; set; }
    /// <summary>Fractal pattern: per-ring spin rate multiplier (alternating directions).</summary>
    public double RingSpinRate { get; set; }
    /// <summary>Fractal pattern: pulsation phase offset.</summary>
    public double PulsePhase { get; set; }
    /// <summary>FractalBox pattern: per-element random self-spin rate multiplier.</summary>
    public double SelfSpinRate { get; set; }
    /// <summary>Cached RotateTransform reference to avoid searching TransformGroup each tick.</summary>
    public System.Windows.Media.RotateTransform? CachedRotateTransform { get; set; }
    /// <summary>Cached ScaleTransform reference to avoid searching TransformGroup each tick.</summary>
    public System.Windows.Media.ScaleTransform? CachedScaleTransform { get; set; }
    /// <summary>Bounce pattern: seconds remaining on collision flash (0 = no flash).</summary>
    public double FlashRemaining { get; set; }
    /// <summary>Gravity pattern: seconds of immunity from merging after pierce/split.</summary>
    public double MergeImmunity { get; set; }
    /// <summary>Gravity pattern: reference to the parent body this particle is gravitationally immune from (comet trail). Null = normal gravity.</summary>
    public System.Windows.FrameworkElement? GravityImmuneFrom { get; set; }
    /// <summary>Gravity pattern: target X to lerp toward after a merge (NaN = none).</summary>
    public double MergeTargetX { get; set; } = double.NaN;
    /// <summary>Gravity pattern: target Y to lerp toward after a merge (NaN = none).</summary>
    public double MergeTargetY { get; set; } = double.NaN;
    /// <summary>Gravity pattern: target size to lerp toward after a merge (NaN = none).</summary>
    public double MergeTargetSize { get; set; } = double.NaN;
}

/// <summary>
/// Shared blob retargeting logic for all screensaver windows.
/// </summary>
public static class BlobMotion
{
    public static void Retarget(
        FrameworkElement blob,
        BlobState state,
        BlobPattern pattern,
        double canvasWidth,
        double canvasHeight,
        double speedMultiplier,
        Random rng,
        Action<FrameworkElement> onCompleted)
    {
        double w = Math.Max(1, canvasWidth);
        double h = Math.Max(1, canvasHeight);

        double stateSpeedFactor = 1.0;
        if (pattern != BlobPattern.Random)
            stateSpeedFactor = state.SpeedFactor;

        var baseDuration = 10 + rng.NextDouble() * 15;
        var durationSec = baseDuration / Math.Max(0.1, speedMultiplier * stateSpeedFactor);

        double toX, toY;

        switch (pattern)
        {
            case BlobPattern.PerfectClockwise:
            case BlobPattern.PerfectMixed:
            {
                // Advance angle in small steps for smooth circular arcs
                double sweep = (10 + rng.NextDouble() * 10) * (Math.PI / 180);
                if (!state.Clockwise) sweep = -sweep;
                state.Angle += sweep;
                state.AccumulatedAngle += Math.Abs(sweep);

                // When a full orbit completes, drift radius in or out by up to 2%
                if (state.AccumulatedAngle >= Math.PI * 2)
                {
                    state.AccumulatedAngle -= Math.PI * 2;
                    bool moveIn = rng.NextDouble() < 0.5;
                    double driftPct = 0.01 + rng.NextDouble() * 0.01;
                    if (moveIn)
                    {
                        state.OrbitRadius *= (1.0 - driftPct);
                        state.SpeedFactor *= (1.0 + driftPct);
                        if (state.OrbitRadius < state.MinOrbitRadius)
                        {
                            state.OrbitRadius = state.MinOrbitRadius;
                        }
                    }
                    else
                    {
                        state.OrbitRadius *= (1.0 + driftPct);
                        state.SpeedFactor *= (1.0 - driftPct);
                        if (state.OrbitRadius > state.MaxOrbitRadius)
                        {
                            state.OrbitRadius = state.MaxOrbitRadius;
                        }
                    }
                }

                double cx = w / 2;
                double cy = h / 2;
                double aspectX = w / Math.Min(w, h);
                double aspectY = h / Math.Min(w, h);
                toX = cx + Math.Cos(state.Angle) * state.OrbitRadius * aspectX - blob.Width * 0.5;
                toY = cy + Math.Sin(state.Angle) * state.OrbitRadius * aspectY - blob.Height * 0.5;

                // Duration scales with orbit radius: inner blobs ~40% faster, mid ~20% faster, outer baseline
                // radiusRatio 0.3 ? factor 0.60, radiusRatio 0.65 ? factor 0.80, radiusRatio 1.0 ? factor 1.0
                double radiusRatio = state.OrbitRadius / Math.Max(1, state.MaxOrbitRadius);
                double radiusFactor = 0.6 + 0.4 * radiusRatio;
                double arcDur = (2.5 + rng.NextDouble() * 2.0) * radiusFactor / Math.Max(0.1, speedMultiplier * stateSpeedFactor);
                var pAnimX = new DoubleAnimation
                {
                    To = toX,
                    Duration = TimeSpan.FromSeconds(arcDur),
                };
                var pAnimY = new DoubleAnimation
                {
                    To = toY,
                    Duration = TimeSpan.FromSeconds(arcDur),
                };
                pAnimX.Completed += (_, _) => onCompleted(blob);
                blob.BeginAnimation(Canvas.LeftProperty, pAnimX);
                blob.BeginAnimation(Canvas.TopProperty, pAnimY);
                return;
            }

            case BlobPattern.RoughClockwise:
            case BlobPattern.RoughMixed:
            {
                // Very small consistent sweep for ultra-smooth arcs (3-5�)
                double sweep = (3 + rng.NextDouble() * 2) * (Math.PI / 180);
                if (!state.Clockwise) sweep = -sweep;
                state.Angle += sweep;
                state.AccumulatedAngle += Math.Abs(sweep);

                // Gradual parameter evolution every full orbit
                if (state.AccumulatedAngle >= Math.PI * 2)
                {
                    state.AccumulatedAngle -= Math.PI * 2;
                    // 2-4% radius change
                    double driftPct = 0.02 + rng.NextDouble() * 0.02;
                    bool moveIn = rng.NextDouble() < 0.5;
                    if (moveIn)
                    {
                        state.OrbitRadius *= (1.0 - driftPct);
                        if (state.OrbitRadius < state.MinOrbitRadius)
                            state.OrbitRadius = state.MinOrbitRadius;
                    }
                    else
                    {
                        state.OrbitRadius *= (1.0 + driftPct);
                        if (state.OrbitRadius > state.MaxOrbitRadius)
                            state.OrbitRadius = state.MaxOrbitRadius;
                    }

                    // Gentle ellipse shape evolution
                    state.Eccentricity = Math.Clamp(
                        state.Eccentricity + (rng.NextDouble() - 0.5) * 0.04, 0.15, 0.75);
                    state.EllipseAngle += (rng.NextDouble() - 0.5) * 0.12;
                }

                // Elliptical orbit radius at current angle
                double cosRel = Math.Cos(state.Angle - state.EllipseAngle);
                double sinRel = Math.Sin(state.Angle - state.EllipseAngle);
                double a = state.OrbitRadius * (1.0 + state.Eccentricity);
                double b = state.OrbitRadius * (1.0 - state.Eccentricity);
                double ellipseR = (a * b) / Math.Sqrt(b * b * cosRel * cosRel + a * a * sinRel * sinRel);

                double cx = w / 2;
                double cy = h / 2;
                double aspectX = w / Math.Min(w, h);
                double aspectY = h / Math.Min(w, h);
                toX = cx + Math.Cos(state.Angle) * ellipseR * aspectX - blob.Width * 0.5;
                toY = cy + Math.Sin(state.Angle) * ellipseR * aspectY - blob.Height * 0.5;

                // Kepler's 2nd law: duration ? distance� (purely multiplicative)
                // This makes speed ? 1/distance: fast near center, slow far out
                double distRatio = ellipseR / Math.Max(1, state.OrbitRadius);
                double arcDur = 1.5 * distRatio * distRatio / Math.Max(0.1, speedMultiplier * stateSpeedFactor);
                var rAnimX = new DoubleAnimation
                {
                    To = toX,
                    Duration = TimeSpan.FromSeconds(arcDur),
                };
                var rAnimY = new DoubleAnimation
                {
                    To = toY,
                    Duration = TimeSpan.FromSeconds(arcDur),
                };
                rAnimX.Completed += (_, _) => onCompleted(blob);
                blob.BeginAnimation(Canvas.LeftProperty, rAnimX);
                blob.BeginAnimation(Canvas.TopProperty, rAnimY);
                return;
            }

            case BlobPattern.Rainfall:
            {
                double blobSize = Math.Max(blob.Width, blob.Height);
                // Top-to-bottom rainfall (screen rotation handles orientation)
                toX = Canvas.GetLeft(blob);
                toY = h + blobSize;
                // Base fall speed increased by 20% (24→28.8, 56→67.2 px/sec)
                durationSec = (h + blobSize * 2) / (28.8 + 67.2 * state.SpeedFactor)
                              / Math.Max(0.1, speedMultiplier);

                var rainAnimX = new DoubleAnimation
                {
                    To = toX,
                    Duration = TimeSpan.FromSeconds(durationSec),
                };
                var rainAnimY = new DoubleAnimation
                {
                    To = toY,
                    Duration = TimeSpan.FromSeconds(durationSec),
                };

                rainAnimX.Completed += (_, _) =>
                {
                    // Clear held animations so Canvas.SetLeft/SetTop take effect
                    blob.BeginAnimation(Canvas.LeftProperty, null);
                    blob.BeginAnimation(Canvas.TopProperty, null);

                    // Respawn above/left of the visible area with slight size and speed variation
                    double baseSize = state.BaseSize > 0 ? state.BaseSize : blob.Width;
                    double sizeChange = 1.0 + (rng.NextDouble() - 0.5) * 0.1;
                    double newSize = Math.Clamp(baseSize * sizeChange, 20, 1200);
                    blob.Width = newSize;
                    blob.Height = newSize;
                    state.SpeedFactor = Math.Clamp(
                        state.SpeedFactor + (rng.NextDouble() - 0.5) * 0.15, 0.3, 1.0);

                    Canvas.SetLeft(blob, rng.NextDouble() * w - newSize * 0.3);
                    Canvas.SetTop(blob, -newSize);

                    onCompleted(blob);
                };

                blob.BeginAnimation(Canvas.LeftProperty, rainAnimX);
                blob.BeginAnimation(Canvas.TopProperty, rainAnimY);
                return;
            }

            case BlobPattern.LavaLamp:
            {
                double curX = Canvas.GetLeft(blob);
                double curY = Canvas.GetTop(blob);
                double blobSize = blob.Width;
                double stepDuration;

                // Capture base values on first call
                if (state.BaseSize <= 0) state.BaseSize = blobSize;
                if (state.BaseOpacity <= 0) state.BaseOpacity = blob.Opacity;

                double along = curY;
                double across = curX;
                double lengthAxis = h;
                double crossAxis = w;

                double targetAlong, targetAcross, targetSize;

                switch (state.Phase)
                {
                    case 0: // Sinking � fall toward the bottom, grow slightly
                    {
                        targetAlong = lengthAxis * (0.71 + rng.NextDouble() * 0.21);
                        double drift = (rng.NextDouble() - 0.5) * crossAxis * 0.12;
                        double clampMin = -blobSize * 0.3;
                        double clampMax = Math.Max(clampMin, crossAxis - blobSize * 0.7);
                        targetAcross = Math.Clamp(across + drift, clampMin, clampMax);
                        stepDuration = (7.7 + rng.NextDouble() * 6.6) / Math.Max(0.1, speedMultiplier);

                        // Grow toward 130% of base size (eased over the step, not snapped)
                        targetSize = Math.Min(blobSize * 1.10, state.BaseSize * 1.3);

                        // Restore opacity as blob sinks
                        blob.Opacity = Math.Min(blob.Opacity + 0.02, state.BaseOpacity);

                        // Transition to rising once we reach the bottom zone
                        if (along >= lengthAxis * 0.68)
                            state.Phase = 1;
                        break;
                    }

                    case 1: // Rising � float upward, drift left/right, shrink and dim
                    {
                        double rise = lengthAxis * (0.08 + rng.NextDouble() * 0.12);
                        double drift = (rng.NextDouble() - 0.5) * crossAxis * 0.25;
                        targetAlong = along - rise;
                        double clampMin2 = -blobSize * 0.3;
                        double clampMax2 = Math.Max(clampMin2, crossAxis - blobSize * 0.7);
                        targetAcross = Math.Clamp(across + drift, clampMin2, clampMax2);
                        stepDuration = (6.6 + rng.NextDouble() * 6.6) / Math.Max(0.1, speedMultiplier);

                        // Shrink toward 70% of base size (eased over the step, not snapped)
                        targetSize = Math.Max(blobSize * 0.90, state.BaseSize * 0.7);

                        // Dim slightly as it rises
                        blob.Opacity = Math.Max(blob.Opacity - 0.015, state.BaseOpacity * 0.4);

                        // Transition to cresting near the top
                        if (along <= lengthAxis * 0.18)
                            state.Phase = 2;
                        break;
                    }

                    default: // Phase 2 � Cresting � slow down, arc over, begin falling
                    {
                        double drift = (rng.NextDouble() - 0.5) * crossAxis * 0.15;
                        double clampMin3 = -blobSize * 0.3;
                        double clampMax3 = Math.Max(clampMin3, crossAxis - blobSize * 0.7);
                        targetAcross = Math.Clamp(across + drift, clampMin3, clampMax3);
                        // Sine-wave crest: move down gently
                        targetAlong = along + lengthAxis * (0.05 + rng.NextDouble() * 0.08);
                        stepDuration = (8.8 + rng.NextDouble() * 6.6) / Math.Max(0.1, speedMultiplier);

                        // Hold current size through the crest
                        targetSize = blobSize;

                        // Transition back to sinking
                        state.Phase = 0;
                        break;
                    }
                }

                // Map logical coords back to X/Y
                toX = targetAcross;
                toY = targetAlong;

                // Position animates from the top-left corner. Because the blob grows
                // or shrinks over the step, compensate the target corner so the blob's
                // CENTER lands where intended — otherwise the size change shifts the
                // corner and reads as a small jump at each step boundary.
                double sizeDelta = targetSize - blobSize;
                toX -= sizeDelta * 0.5;
                toY -= sizeDelta * 0.5;

                var lavaAnimX = new DoubleAnimation
                {
                    To = toX,
                    Duration = TimeSpan.FromSeconds(stepDuration),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                var lavaAnimY = new DoubleAnimation
                {
                    To = toY,
                    Duration = TimeSpan.FromSeconds(stepDuration),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };

                // Ease Width/Height across the step so the blob doesn't pop a few
                // percent instantly at each retarget (the pop also shifts the
                // top-left corner, which is what read as a small jump).
                var lavaAnimW = new DoubleAnimation
                {
                    To = targetSize,
                    Duration = TimeSpan.FromSeconds(stepDuration),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                var lavaAnimH = new DoubleAnimation
                {
                    To = targetSize,
                    Duration = TimeSpan.FromSeconds(stepDuration),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };

                lavaAnimX.Completed += (_, _) =>
                {
                    // Set base values BEFORE clearing the animations. Clearing first
                    // makes WPF momentarily revert the property to the step's start
                    // value, which can render as a single-frame snap — most visible on
                    // slow phase-transition steps. Setting the base first means the
                    // revert is a no-op.
                    Canvas.SetLeft(blob, toX);
                    Canvas.SetTop(blob, toY);
                    blob.Width = targetSize;
                    blob.Height = targetSize;
                    blob.BeginAnimation(Canvas.LeftProperty, null);
                    blob.BeginAnimation(Canvas.TopProperty, null);
                    blob.BeginAnimation(FrameworkElement.WidthProperty, null);
                    blob.BeginAnimation(FrameworkElement.HeightProperty, null);
                    onCompleted(blob);
                };

                blob.BeginAnimation(Canvas.LeftProperty, lavaAnimX);
                blob.BeginAnimation(Canvas.TopProperty, lavaAnimY);
                blob.BeginAnimation(FrameworkElement.WidthProperty, lavaAnimW);
                blob.BeginAnimation(FrameworkElement.HeightProperty, lavaAnimH);
                return;
            }

            case BlobPattern.Bounce:
                // Bounce physics handled by BounceSimulator — no-op here
                return;

            case BlobPattern.Gravity:
                // Gravity physics handled by GravitySimulator — no-op here
                return;

            case BlobPattern.LightCycle:
                // Light Cycle physics handled by LightCycleSimulator � no-op here
                return;

            case BlobPattern.Fractal:
            case BlobPattern.FractalBox:
            {
                double cx = w / 2;
                double cy = h / 2;
                double aspectX = w / Math.Min(w, h);
                double aspectY = h / Math.Min(w, h);

                // Sweep per step � larger = fewer retargets/sec, better perf at high counts
                double sweepRad = 6.0 * (Math.PI / 180) * state.RingSpinRate;
                state.GlobalRotation += sweepRad;

                // Pulsation: radius breathes �12% on a slow sine wave
                state.PulsePhase += 0.09;
                double pulseFactor = 1.0 + 0.12 * Math.Sin(state.PulsePhase);

                // Position within the ring
                double slotAngle = (2 * Math.PI / state.RingCount) * state.RingIndex + state.GlobalRotation;
                double r = state.OrbitRadius * pulseFactor;

                toX = cx + Math.Cos(slotAngle) * r * aspectX - blob.Width * 0.5;
                toY = cy + Math.Sin(slotAngle) * r * aspectY - blob.Height * 0.5;

                // Animate the element's self-rotation smoothly instead of snapping
                double baseSpinMultiplier = state.Ring % 2 == 0 ? 3 : -2;
                double spinRate = state.SelfSpinRate != 0 ? state.SelfSpinRate : baseSpinMultiplier;
                double selfSpinDeg = state.GlobalRotation * (180 / Math.PI) * spinRate;

                // Find or create the RotateTransform � cached after first lookup
                var rt = state.CachedRotateTransform;
                if (rt == null)
                {
                    if (blob.RenderTransform is System.Windows.Media.RotateTransform directRt)
                    {
                        rt = directRt;
                    }
                    else if (blob.RenderTransform is System.Windows.Media.TransformGroup tg)
                    {
                        for (int c = 0; c < tg.Children.Count; c++)
                        {
                            if (tg.Children[c] is System.Windows.Media.RotateTransform groupRt)
                            {
                                rt = groupRt;
                                break;
                            }
                        }
                    }
                    if (rt == null)
                    {
                        rt = new System.Windows.Media.RotateTransform(0, blob.Width / 2, blob.Height / 2);
                        blob.RenderTransform = rt;
                    }
                    state.CachedRotateTransform = rt;
                }

                // Snapshot current animated values, clear old animations, then set
                // base values before starting new ones � prevents single-frame snaps
                // where WPF briefly shows the stale base value between animations.
                double curX = (double)blob.GetValue(Canvas.LeftProperty);
                double curY = (double)blob.GetValue(Canvas.TopProperty);
                double curAngle = rt.Angle;
                if (double.IsNaN(curX)) curX = toX;
                if (double.IsNaN(curY)) curY = toY;

                blob.BeginAnimation(Canvas.LeftProperty, null);
                blob.BeginAnimation(Canvas.TopProperty, null);
                rt.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);

                Canvas.SetLeft(blob, curX);
                Canvas.SetTop(blob, curY);
                rt.Angle = curAngle;

                // Consistent duration per step � no random variance � keeps all blobs in sync
                double arcDur = 3.6 / Math.Max(0.1, speedMultiplier * Math.Abs(state.RingSpinRate));

                var fAnimX = new DoubleAnimation
                {
                    From = curX,
                    To = toX,
                    Duration = TimeSpan.FromSeconds(arcDur),
                };
                var fAnimY = new DoubleAnimation
                {
                    From = curY,
                    To = toY,
                    Duration = TimeSpan.FromSeconds(arcDur),
                };
                var fAnimR = new DoubleAnimation
                {
                    From = curAngle,
                    To = selfSpinDeg,
                    Duration = TimeSpan.FromSeconds(arcDur),
                };
                fAnimX.Completed += (_, _) => onCompleted(blob);
                blob.BeginAnimation(Canvas.LeftProperty, fAnimX);
                blob.BeginAnimation(Canvas.TopProperty, fAnimY);
                rt.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, fAnimR);
                return;
            }

            default: // Random
            {
                double overshoot = Math.Max(w, h) * 0.2;
                toX = rng.NextDouble() * (w + overshoot * 2) - overshoot - blob.Width * 0.5;
                toY = rng.NextDouble() * (h + overshoot * 2) - overshoot - blob.Height * 0.5;
                break;
            }
        }

        var animX = new DoubleAnimation
        {
            To = toX,
            Duration = TimeSpan.FromSeconds(durationSec),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        var animY = new DoubleAnimation
        {
            To = toY,
            Duration = TimeSpan.FromSeconds(durationSec + rng.NextDouble() * 4),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        animX.Completed += (_, _) => onCompleted(blob);

        blob.BeginAnimation(Canvas.LeftProperty, animX);
        blob.BeginAnimation(Canvas.TopProperty, animY);
    }

    /// <summary>
    /// Initialize blob states for a set of blobs given a pattern and canvas size.
    /// </summary>
    public static List<BlobState> CreateStates(int count, BlobPattern pattern, double canvasWidth, double canvasHeight, Random rng, double maxOrbitRadius = 0, double speedMultiplier = 1.0)
    {
        var states = new List<BlobState>(count);
        double defaultMax = Math.Min(canvasWidth, canvasHeight) * 0.35;
        double maxRadius = maxOrbitRadius > 0 ? maxOrbitRadius : defaultMax;

        for (int i = 0; i < count; i++)
        {
            bool clockwise = pattern switch
            {
                BlobPattern.PerfectMixed or BlobPattern.RoughMixed => rng.NextDouble() < 0.5,
                BlobPattern.PerfectClockwise or BlobPattern.RoughClockwise => true,
                _ => true
            };

            if (pattern == BlobPattern.Rainfall)
            {
                states.Add(new BlobState
                {
                    SpeedFactor = 0.3 + rng.NextDouble() * 0.7,
                });
                continue;
            }

            if (pattern == BlobPattern.LavaLamp)
            {
                int phase = rng.Next(3);
                states.Add(new BlobState
                {
                    Phase = phase,
                    SpeedFactor = 0.6 + rng.NextDouble() * 0.4,
                });
                continue;
            }

            if (pattern == BlobPattern.FerrofluidCluster)
            {
                // Start with gentle outward velocity from center
                double angle = rng.NextDouble() * Math.PI * 2;
                double speed = 20 + rng.NextDouble() * 40;
                states.Add(new BlobState
                {
                    VelocityX = Math.Cos(angle) * speed,
                    VelocityY = Math.Sin(angle) * speed,
                });
                continue;
            }

            if (pattern == BlobPattern.Bounce)
            {
                double angle = rng.NextDouble() * Math.PI * 2;
                double diag = Math.Sqrt(canvasWidth * canvasWidth + canvasHeight * canvasHeight);
                // Speed so a blob crosses the diagonal in about 25 seconds
                double traverseTime = 32 + rng.NextDouble() * 10.0;
                double speed = diag / traverseTime * Math.Max(0.1, speedMultiplier) * 0.8;
                states.Add(new BlobState
                {
                    VelocityX = Math.Cos(angle) * speed,
                    VelocityY = Math.Sin(angle) * speed,
                });
                continue;
            }

            if (pattern == BlobPattern.Gravity)
            {
                // Position angle from center (computed later in GetInitialPosition)
                // but we need it now to bias velocity outward/orbital.
                double posAngle = rng.NextDouble() * Math.PI * 2;
                double speed = 30 + rng.NextDouble() * 50;

                // Mix of tangential (orbital) + slight outward + random jitter.
                // tangentialAngle is perpendicular to the radial direction;
                // randomly CW or CCW for variety.
                double tangentialAngle = posAngle + (rng.NextDouble() < 0.5 ? Math.PI / 2 : -Math.PI / 2);
                double outwardFraction = 0.15 + rng.NextDouble() * 0.15; // 15-30% outward
                double jitter = (rng.NextDouble() - 0.5) * 0.4;          // ±20% random twist

                double vx = (Math.Cos(tangentialAngle) * (1.0 - outwardFraction)
                           + Math.Cos(posAngle) * outwardFraction
                           + jitter) * speed;
                double vy = (Math.Sin(tangentialAngle) * (1.0 - outwardFraction)
                           + Math.Sin(posAngle) * outwardFraction
                           + jitter) * speed;

                states.Add(new BlobState
                {
                    Angle = posAngle, // stash for GetInitialPosition
                    VelocityX = vx * Math.Max(0.1, speedMultiplier),
                    VelocityY = vy * Math.Max(0.1, speedMultiplier),
                });
                continue;
            }

            if (pattern == BlobPattern.LightCycle)
            {
                // Direction: 0=right, 1=down, 2=left, 3=up
                int dir = rng.Next(4);
                double baseSpeed = Math.Max(canvasWidth, canvasHeight) / (8.0 + rng.NextDouble() * 4.0) * Math.Max(0.1, speedMultiplier);
                states.Add(new BlobState
                {
                    Phase = dir,
                    SpeedFactor = baseSpeed,
                });
                continue;
            }

            if (pattern == BlobPattern.Fractal || pattern == BlobPattern.FractalBox)
            {
                // Handled after the loop
                continue;
            }

            bool isRough = pattern is BlobPattern.RoughClockwise or BlobPattern.RoughMixed;
            states.Add(new BlobState
            {
                Angle = rng.NextDouble() * Math.PI * 2,
                OrbitRadius = maxRadius * (0.3 + rng.NextDouble() * 0.7),
                Clockwise = clockwise,
                MaxOrbitRadius = maxRadius,
                Eccentricity = isRough ? 0.30 + rng.NextDouble() * 0.40 : 0,
                EllipseAngle = isRough ? rng.NextDouble() * Math.PI * 2 : 0,
            });
        }

        if (pattern == BlobPattern.Fractal || pattern == BlobPattern.FractalBox)
        {
            // Distribute blobs into concentric hexagonal rings: 1 center + rings of 6, 12, 18...
            states.Clear();
            var ringAssignments = new List<(int ring, int index, int ringCount, double radius)>();
            int remaining = count;
            int ringNum = 0;

            if (remaining > 0) { ringAssignments.Add((0, 0, 1, 0)); remaining--; ringNum++; }

            while (remaining > 0)
            {
                int ringSlots = 6 * ringNum;
                int actual = Math.Min(ringSlots, remaining);
                double ringRadius = maxRadius * (0.2 + 0.8 * ringNum / Math.Max(1, count / 6.0));
                ringRadius = Math.Min(ringRadius, maxRadius);
                for (int j = 0; j < actual; j++)
                    ringAssignments.Add((ringNum, j, actual, ringRadius));
                remaining -= actual;
                ringNum++;
            }

            for (int idx = 0; idx < count; idx++)
            {
                var (ring, index, ringCount, radius) = ringAssignments[idx];
                double spinRate = (ring % 2 == 0 ? 1.0 : -1.0) * (1.0 + 0.5 / Math.Max(1, ring));

                // FractalBox: per-element random self-spin for visual variety
                double selfSpin = 0;
                if (pattern == BlobPattern.FractalBox)
                {
                    double direction = rng.NextDouble() < 0.5 ? 1.0 : -1.0;
                    selfSpin = direction * (1.5 + rng.NextDouble() * 3.0);
                }

                states.Add(new BlobState
                {
                    Ring = ring,
                    RingIndex = index,
                    RingCount = ringCount,
                    OrbitRadius = radius,
                    MaxOrbitRadius = maxRadius,
                    GlobalRotation = 0,
                    RingSpinRate = spinRate,
                    PulsePhase = rng.NextDouble() * Math.PI * 2,
                    Angle = (2 * Math.PI / ringCount) * index,
                    SelfSpinRate = selfSpin,
                });
            }
        }

        return states;
    }

    /// <summary>
    /// Returns the initial canvas position
    /// For non-orbital patterns, returns a random position.
    /// </summary>
    public static (double x, double y) GetInitialPosition(
        BlobState state, BlobPattern pattern,
        double canvasWidth, double canvasHeight, double blobSize, Random rng)
    {
        if (pattern == BlobPattern.FerrofluidCluster)
        {
            // Start scattered around center within ~40% of canvas
            double cx = canvasWidth / 2;
            double cy = canvasHeight / 2;
            double spread = Math.Min(canvasWidth, canvasHeight) * 0.4;
            double angle = rng.NextDouble() * Math.PI * 2;
            double radius = rng.NextDouble() * spread;
            return (cx + Math.Cos(angle) * radius - blobSize * 0.5,
                    cy + Math.Sin(angle) * radius - blobSize * 0.5);
        }

        if (pattern == BlobPattern.Bounce)
        {
            return (rng.NextDouble() * (canvasWidth - blobSize),
                    rng.NextDouble() * (canvasHeight - blobSize));
        }

        if (pattern == BlobPattern.Gravity)
        {
            // Use the angle stashed during CreateStates so velocity direction
            // is coherent with placement (orbital tangent makes sense).
            double cx = canvasWidth / 2;
            double cy = canvasHeight / 2;
            double spread = Math.Min(canvasWidth, canvasHeight) * 0.75;
            double angle = state.Angle; // set in CreateStates
            double radius = rng.NextDouble() * spread;
            return (cx + Math.Cos(angle) * radius - blobSize * 0.5,
                    cy + Math.Sin(angle) * radius - blobSize * 0.5);
        }

        if (pattern == BlobPattern.LightCycle)
        {
            // Start off-screen based on initial direction
            return state.Phase switch
            {
                0 => (-blobSize, rng.NextDouble() * canvasHeight),        // moving right, start left
                1 => (rng.NextDouble() * canvasWidth, -blobSize),          // moving down, start top
                2 => (canvasWidth + blobSize, rng.NextDouble() * canvasHeight), // moving left, start right
                _ => (rng.NextDouble() * canvasWidth, canvasHeight + blobSize), // moving up, start bottom
            };
        }

        if (pattern == BlobPattern.Rainfall)
        {
            // Stagger blobs vertically so they don't all start at the top
            // and cause expensive overlapping alpha-blend compositing.
            double staggerY = -blobSize - rng.NextDouble() * canvasHeight;
            return (rng.NextDouble() * canvasWidth - blobSize * 0.3, staggerY);
        }

        if (pattern == BlobPattern.LavaLamp)
        {
            double across = rng.NextDouble() * canvasWidth - blobSize * 0.3;
            double along = state.Phase switch
            {
                0 => canvasHeight * (0.2 + rng.NextDouble() * 0.3),
                1 => canvasHeight * (0.5 + rng.NextDouble() * 0.25),
                _ => canvasHeight * (0.1 + rng.NextDouble() * 0.15),
            };
            state.BaseSize = blobSize;
            return (across, along);
        }

        if (pattern is BlobPattern.PerfectClockwise or BlobPattern.PerfectMixed)
        {
            double cx = canvasWidth / 2;
            double cy = canvasHeight / 2;
            double aspectX = canvasWidth / Math.Min(canvasWidth, canvasHeight);
            double aspectY = canvasHeight / Math.Min(canvasWidth, canvasHeight);
            double x = cx + Math.Cos(state.Angle) * state.OrbitRadius * aspectX - blobSize * 0.5;
            double y = cy + Math.Sin(state.Angle) * state.OrbitRadius * aspectY - blobSize * 0.5;
            return (x, y);
        }

        if (pattern is BlobPattern.RoughClockwise or BlobPattern.RoughMixed)
        {
            double cx = canvasWidth / 2;
            double cy = canvasHeight / 2;
            double cosRel = Math.Cos(state.Angle - state.EllipseAngle);
            double sinRel = Math.Sin(state.Angle - state.EllipseAngle);
            double a = state.OrbitRadius * (1.0 + state.Eccentricity);
            double b = state.OrbitRadius * (1.0 - state.Eccentricity);
            double ellipseR = (a * b) / Math.Sqrt(b * b * cosRel * cosRel + a * a * sinRel * sinRel);
            double aspectX = canvasWidth / Math.Min(canvasWidth, canvasHeight);
            double aspectY = canvasHeight / Math.Min(canvasWidth, canvasHeight);
            double x = cx + Math.Cos(state.Angle) * ellipseR * aspectX - blobSize * 0.5;
            double y = cy + Math.Sin(state.Angle) * ellipseR * aspectY - blobSize * 0.5;
            return (x, y);
        }

        if (pattern == BlobPattern.Fractal || pattern == BlobPattern.FractalBox)
        {
            double cx = canvasWidth / 2;
            double cy = canvasHeight / 2;
            double aspectX = canvasWidth / Math.Min(canvasWidth, canvasHeight);
            double aspectY = canvasHeight / Math.Min(canvasWidth, canvasHeight);
            double slotAngle = (2 * Math.PI / state.RingCount) * state.RingIndex;
            double x = cx + Math.Cos(slotAngle) * state.OrbitRadius * aspectX - blobSize * 0.5;
            double y = cy + Math.Sin(slotAngle) * state.OrbitRadius * aspectY - blobSize * 0.5;
            return (x, y);
        }

        return (rng.NextDouble() * canvasWidth - blobSize * 0.3,
                rng.NextDouble() * canvasHeight - blobSize * 0.3);
    }
}
