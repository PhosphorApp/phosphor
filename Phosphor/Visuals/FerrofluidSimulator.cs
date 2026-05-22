using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Phosphor;

/// <summary>
/// Physics simulator for the Ferrofluid Cluster pattern.
/// Blobs feel a gravitational pull toward the canvas center (the "magnetic core").
/// Audio reactivity modulates the simulation:
///   • Bass increases mutual attraction, pulling blobs into a tight vibrating mass.
///   • Beat triggers an "explosion" that scatters blobs outward before gravity re-coalesces them.
///   • Treble shoots individual blobs outward along sharp bristle paths.
/// When no audio data is available, a built-in timer drives periodic spikes and pulses.
/// </summary>
public sealed class FerrofluidSimulator : IDisposable
{
    private readonly List<FrameworkElement> _blobs;
    private readonly List<BlobState> _states;
    private readonly Canvas _canvas;
    private readonly Random _rng = new();
    private readonly Stopwatch _stopwatch = new();
    private long _lastTickTicks;
    private bool _running;

    // --- Tunable physics properties ---

    /// <summary>Base gravitational pull toward center (px/s²).</summary>
    public static double CoreGravity { get; set; } = 280.0;

    /// <summary>Base blob-to-blob attraction strength.</summary>
    public static double MutualAttraction { get; set; } = 40.0;

    /// <summary>Velocity damping per tick (0–1, lower = more friction).</summary>
    public static double Damping { get; set; } = 0.97;

    /// <summary>Close-range repulsion to prevent blob overlap.</summary>
    public static double SoftBodyRepulsion { get; set; } = 800.0;

    /// <summary>Maximum blob speed (px/s).</summary>
    public static double MaxSpeed { get; set; } = 900.0;

    /// <summary>Bass threshold required for a beat to trigger an explosion.</summary>
    public static double ExplosionBassThreshold { get; set; } = 0.2;

    /// <summary>Base explosion impulse force.</summary>
    public static double ExplosionForceBase { get; set; } = 800.0;

    /// <summary>Additional explosion force scaled by bass energy.</summary>
    public static double ExplosionForceBassScale { get; set; } = 1200.0;

    /// <summary>How long (seconds) reduced gravity persists after an explosion.</summary>
    public static double ExplosionDuration { get; set; } = 0.8;

    /// <summary>Treble level threshold to trigger bristle ejections.</summary>
    public static double BristleTrebleThreshold { get; set; } = 0.3;

    /// <summary>Base bristle ejection force.</summary>
    public static double BristleForceBase { get; set; } = 150.0;

    /// <summary>Additional bristle force scaled by treble energy.</summary>
    public static double BristleForceTrebleScale { get; set; } = 350.0;

    private const double MinSeparation = 2.0;        // prevent division-by-zero in attraction

    // --- Audio-reactive state ---
    private float _bass;
    private float _treble;
    private bool _isBeat;
    private double _explodeCooldown;                  // seconds remaining in explosion state

    // --- Timer-driven fallback when no audio ---
    private double _fallbackTimer;
    private const double FallbackPulsePeriod = 4.0;   // seconds between synthetic pulses
    private const double FallbackBristlePeriod = 2.5;
    private double _bristleTimer;

    public FerrofluidSimulator(List<FrameworkElement> blobs, List<BlobState> states,
        Canvas canvas, double speedMultiplier = 1.0)
    {
        _blobs = blobs;
        _states = states;
        _canvas = canvas;

        // Clear WPF animations so we can set positions directly
        for (int i = 0; i < _blobs.Count && i < _states.Count; i++)
        {
            _blobs[i].BeginAnimation(Canvas.LeftProperty, null);
            _blobs[i].BeginAnimation(Canvas.TopProperty, null);
            if (_states[i].BaseOpacity <= 0)
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

    public void Stop()
    {
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
        _stopwatch.Stop();
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Feed current audio data into the simulator. Called externally by the pattern's
    /// <see cref="FerrofluidClusterPattern.ApplyAudioReactive"/> override.
    /// </summary>
    public void SetAudioData(float bass, float treble, bool isBeat)
    {
        _bass = bass;
        _treble = treble;
        _isBeat = isBeat;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_running) return;
        long nowTicks = _stopwatch.ElapsedTicks;
        double dt = Math.Min((double)(nowTicks - _lastTickTicks) / Stopwatch.Frequency, 0.05);
        _lastTickTicks = nowTicks;
        if (dt <= 0) return;

        double cw = Math.Max(1, _canvas.ActualWidth);
        double ch = Math.Max(1, _canvas.ActualHeight);
        double cx = cw / 2;
        double cy = ch / 2;

        // --- Determine effective audio values (use fallback timer if silent) ---
        float bass = _bass;
        float treble = _treble;
        bool isBeat = _isBeat;
        _isBeat = false; // consume beat

        bool audioSilent = bass < 0.01f && treble < 0.01f;
        if (audioSilent)
        {
            _fallbackTimer += dt;
            _bristleTimer += dt;

            // Synthetic pulse: simulate bass swell
            double pulsePhase = (_fallbackTimer % FallbackPulsePeriod) / FallbackPulsePeriod;
            bass = (float)(0.15 + 0.35 * Math.Sin(pulsePhase * Math.PI * 2));

            // Synthetic bristle: periodic treble spike
            if (_bristleTimer >= FallbackBristlePeriod)
            {
                _bristleTimer -= FallbackBristlePeriod;
                treble = 0.7f;
            }
            else
            {
                treble = 0.05f;
            }

            // Synthetic beat every ~4s
            if (_fallbackTimer >= FallbackPulsePeriod)
            {
                _fallbackTimer -= FallbackPulsePeriod;
                isBeat = true;
            }
        }

        // --- Handle explosion state ---
        if (isBeat && bass > ExplosionBassThreshold)
        {
            _explodeCooldown = ExplosionDuration;
            // Apply strong outward impulse to all blobs
            for (int i = 0; i < _blobs.Count && i < _states.Count; i++)
            {
                double bx = Canvas.GetLeft(_blobs[i]) + _blobs[i].Width * 0.5;
                double by = Canvas.GetTop(_blobs[i]) + _blobs[i].Height * 0.5;
                double dx = bx - cx;
                double dy = by - cy;
                double dist = Math.Max(5, Math.Sqrt(dx * dx + dy * dy));
                // Strong explosion — scale with bass and add random scatter for variety
                double explosionForce = ExplosionForceBase + bass * ExplosionForceBassScale;
                double scatter = 0.8 + _rng.NextDouble() * 0.4; // 0.8–1.2x per blob
                _states[i].VelocityX += (dx / dist) * explosionForce * scatter;
                _states[i].VelocityY += (dy / dist) * explosionForce * scatter;
            }
        }

        if (_explodeCooldown > 0)
            _explodeCooldown -= dt;

        // --- Treble bristles: shoot random blobs outward ---
        if (treble > BristleTrebleThreshold)
        {
            int bristleCount = Math.Max(1, (int)(treble * 3));
            for (int b = 0; b < bristleCount && _blobs.Count > 0; b++)
            {
                int idx = _rng.Next(_blobs.Count);
                if (idx >= _states.Count) continue;
                double bx = Canvas.GetLeft(_blobs[idx]) + _blobs[idx].Width * 0.5;
                double by = Canvas.GetTop(_blobs[idx]) + _blobs[idx].Height * 0.5;
                double dx = bx - cx;
                double dy = by - cy;
                double dist = Math.Max(5, Math.Sqrt(dx * dx + dy * dy));
                double bristleForce = BristleForceBase + treble * BristleForceTrebleScale;
                _states[idx].VelocityX += (dx / dist) * bristleForce;
                _states[idx].VelocityY += (dy / dist) * bristleForce;
            }
        }

        bool exploding = _explodeCooldown > 0;

        // --- Per-blob physics ---
        for (int i = 0; i < _blobs.Count && i < _states.Count; i++)
        {
            var s = _states[i];
            double bx = Canvas.GetLeft(_blobs[i]) + _blobs[i].Width * 0.5;
            double by = Canvas.GetTop(_blobs[i]) + _blobs[i].Height * 0.5;

            // Central gravity (stronger with bass, weaker during explosion)
            double dx = cx - bx;
            double dy = cy - by;
            double dist = Math.Max(MinSeparation, Math.Sqrt(dx * dx + dy * dy));
            double gravityStrength = CoreGravity * (1.0 + bass * 2.5);
            if (exploding) gravityStrength *= 0.15; // reduced during explosion
            s.VelocityX += (dx / dist) * gravityStrength * dt;
            s.VelocityY += (dy / dist) * gravityStrength * dt;

            // Mutual attraction to other blobs (increases with bass)
            double mutualScale = MutualAttraction * (1.0 + bass * 4.0);
            if (exploding) mutualScale *= 0.1;
            for (int j = 0; j < _blobs.Count && j < _states.Count; j++)
            {
                if (i == j) continue;
                double ox = Canvas.GetLeft(_blobs[j]) + _blobs[j].Width * 0.5;
                double oy = Canvas.GetTop(_blobs[j]) + _blobs[j].Height * 0.5;
                double adx = ox - bx;
                double ady = oy - by;
                double adist = Math.Max(MinSeparation, Math.Sqrt(adx * adx + ady * ady));
                // Attraction falls off with distance
                double force = mutualScale / (1.0 + adist * 0.02);
                s.VelocityX += (adx / adist) * force * dt;
                s.VelocityY += (ady / adist) * force * dt;
            }
        }

        // --- Soft-body repulsion (prevent blobs from overlapping) ---
        for (int i = 0; i < _blobs.Count && i < _states.Count; i++)
        {
            for (int j = i + 1; j < _blobs.Count && j < _states.Count; j++)
            {
                double x1 = Canvas.GetLeft(_blobs[i]) + _blobs[i].Width * 0.5;
                double y1 = Canvas.GetTop(_blobs[i]) + _blobs[i].Height * 0.5;
                double x2 = Canvas.GetLeft(_blobs[j]) + _blobs[j].Width * 0.5;
                double y2 = Canvas.GetTop(_blobs[j]) + _blobs[j].Height * 0.5;

                double r1 = _blobs[i].Width * 0.35;
                double r2 = _blobs[j].Width * 0.35;
                double minDist = r1 + r2;

                double rdx = x2 - x1;
                double rdy = y2 - y1;
                double rdistSq = rdx * rdx + rdy * rdy;

                if (rdistSq < minDist * minDist && rdistSq > 0.01)
                {
                    double rdist = Math.Sqrt(rdistSq);
                    double nx = rdx / rdist;
                    double ny = rdy / rdist;
                    double overlap = minDist - rdist;
                    double repulse = SoftBodyRepulsion * overlap * dt;

                    _states[i].VelocityX -= nx * repulse;
                    _states[i].VelocityY -= ny * repulse;
                    _states[j].VelocityX += nx * repulse;
                    _states[j].VelocityY += ny * repulse;
                }
            }
        }

        // --- Apply velocity, damping, and clamp ---
        for (int i = 0; i < _blobs.Count && i < _states.Count; i++)
        {
            var s = _states[i];

            // Damping
            s.VelocityX *= Damping;
            s.VelocityY *= Damping;

            // Clamp speed
            double speed = Math.Sqrt(s.VelocityX * s.VelocityX + s.VelocityY * s.VelocityY);
            if (speed > MaxSpeed)
            {
                double scale = MaxSpeed / speed;
                s.VelocityX *= scale;
                s.VelocityY *= scale;
            }

            // Move
            double x = Canvas.GetLeft(_blobs[i]) + s.VelocityX * dt;
            double y = Canvas.GetTop(_blobs[i]) + s.VelocityY * dt;

            double bw = _blobs[i].Width;

            // Soft wall containment — reflect with heavy damping
            if (x < -bw * 0.5)
            {
                x = -bw * 0.5;
                s.VelocityX = Math.Abs(s.VelocityX) * 0.4;
            }
            else if (x + bw * 1.5 > cw)
            {
                x = cw - bw * 1.5;
                s.VelocityX = -Math.Abs(s.VelocityX) * 0.4;
            }

            if (y < -bw * 0.5)
            {
                y = -bw * 0.5;
                s.VelocityY = Math.Abs(s.VelocityY) * 0.4;
            }
            else if (y + bw * 1.5 > ch)
            {
                y = ch - bw * 1.5;
                s.VelocityY = -Math.Abs(s.VelocityY) * 0.4;
            }

            Canvas.SetLeft(_blobs[i], x);
            Canvas.SetTop(_blobs[i], y);
        }
    }
}
