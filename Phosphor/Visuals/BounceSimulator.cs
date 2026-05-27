using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Phosphor;

/// <summary>
/// Physics-based bounce simulation for blob screensaver pattern.
/// Blobs move freely, bounce off walls (pool-table style) and off each other
/// with elastic 2D collisions. Mass is proportional to blob area.
/// </summary>
public sealed class BounceSimulator : IDisposable
{
    private readonly List<FrameworkElement> _blobs;
    private readonly List<BlobState> _states;
    private readonly Canvas _canvas;
    private readonly Random _rng = new();
    private readonly Stopwatch _stopwatch = new();
    private long _lastTickTicks;
    private bool _running;
    private double _minSpeed = 60.0;
    private double _maxSpeed = 400.0;
    private const double Gravity = 5.0;       // subtle downward pull (px/s²)
    private const double Restitution = 1.02;   // >1 = blobs gain tiny energy on collision
    private const double WallRestitution = 0.98;

    public BounceSimulator(List<FrameworkElement> blobs, List<BlobState> states, Canvas canvas, double speedMultiplier = 1.0)
    {
        _blobs = blobs;
        _states = states;
        _canvas = canvas;

        double sm = Math.Max(0.1, speedMultiplier);
        double diag = Math.Sqrt(canvas.ActualWidth * canvas.ActualWidth + canvas.ActualHeight * canvas.ActualHeight);
        // Min ~half the slowest initial speed, max ~double the fastest
        _minSpeed = Math.Max(30, diag / 93.3) * sm;
        _maxSpeed = Math.Max(200, diag / 17.8) * sm;

        // Clear any WPF animations so we can set positions directly
        for (int i = 0; i < _blobs.Count && i < _states.Count; i++)
        {
            _blobs[i].BeginAnimation(Canvas.LeftProperty, null);
            _blobs[i].BeginAnimation(Canvas.TopProperty, null);

            // Ensure BaseOpacity is captured so collision flashes restore correctly
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

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_running) return;
        long nowTicks = _stopwatch.ElapsedTicks;
        double dt = Math.Min((double)(nowTicks - _lastTickTicks) / Stopwatch.Frequency, 0.05);
        _lastTickTicks = nowTicks;
        if (dt <= 0) return;

        double cw = Math.Max(1, _canvas.ActualWidth);
        double ch = Math.Max(1, _canvas.ActualHeight);

        // Update velocities and positions
        for (int i = 0; i < _blobs.Count && i < _states.Count; i++)
        {
            var s = _states[i];

            // Apply subtle gravity
            s.VelocityY += Gravity * dt;

            // Move
            double x = Canvas.GetLeft(_blobs[i]) + s.VelocityX * dt;
            double y = Canvas.GetTop(_blobs[i]) + s.VelocityY * dt;

            double r = _blobs[i].Width * 0.2; // core radius for collision
            double bw = _blobs[i].Width;

            // Wall bounces (pool table style — reflect velocity component)
            if (x + bw * 0.5 - r < 0)
            {
                x = r - bw * 0.5;
                s.VelocityX = Math.Abs(s.VelocityX) * WallRestitution;
                AddSpin(s);
            }
            else if (x + bw * 0.5 + r > cw)
            {
                x = cw - r - bw * 0.5;
                s.VelocityX = -Math.Abs(s.VelocityX) * WallRestitution;
                AddSpin(s);
            }

            if (y + bw * 0.5 - r < 0)
            {
                y = r - bw * 0.5;
                s.VelocityY = Math.Abs(s.VelocityY) * WallRestitution;
                AddSpin(s);
            }
            else if (y + bw * 0.5 + r > ch)
            {
                y = ch - r - bw * 0.5;
                s.VelocityY = -Math.Abs(s.VelocityY) * WallRestitution;
                AddSpin(s);
            }

            Canvas.SetLeft(_blobs[i], x);
            Canvas.SetTop(_blobs[i], y);
        }

        // Blob-blob elastic collisions
        for (int i = 0; i < _blobs.Count && i < _states.Count; i++)
        {
            for (int j = i + 1; j < _blobs.Count && j < _states.Count; j++)
            {
                double x1 = Canvas.GetLeft(_blobs[i]) + _blobs[i].Width * 0.5;
                double y1 = Canvas.GetTop(_blobs[i]) + _blobs[i].Height * 0.5;
                double x2 = Canvas.GetLeft(_blobs[j]) + _blobs[j].Width * 0.5;
                double y2 = Canvas.GetTop(_blobs[j]) + _blobs[j].Height * 0.5;

                double r1 = _blobs[i].Width * 0.2;
                double r2 = _blobs[j].Width * 0.2;
                double minDist = r1 + r2;

                double dx = x2 - x1;
                double dy = y2 - y1;
                double distSq = dx * dx + dy * dy;

                if (distSq < minDist * minDist && distSq > 0.01)
                {
                    double dist = Math.Sqrt(distSq);
                    double nx = dx / dist;
                    double ny = dy / dist;

                    var s1 = _states[i];
                    var s2 = _states[j];

                    // Mass proportional to area (width²)
                    double m1 = _blobs[i].Width * _blobs[i].Width;
                    double m2 = _blobs[j].Width * _blobs[j].Width;

                    // Relative velocity along collision normal
                    double dvx = s1.VelocityX - s2.VelocityX;
                    double dvy = s1.VelocityY - s2.VelocityY;
                    double dvn = dvx * nx + dvy * ny;

                    // Only resolve if blobs are approaching
                    if (dvn > 0)
                    {
                        double impulse = (2.0 * dvn) / (m1 + m2) * Restitution;

                        s1.VelocityX -= impulse * m2 * nx;
                        s1.VelocityY -= impulse * m2 * ny;
                        s2.VelocityX += impulse * m1 * nx;
                        s2.VelocityY += impulse * m1 * ny;

                        // Separate overlapping blobs
                        double overlap = minDist - dist;
                        double sep = overlap * 0.5 + 0.5;
                        Canvas.SetLeft(_blobs[i], Canvas.GetLeft(_blobs[i]) - nx * sep);
                        Canvas.SetTop(_blobs[i], Canvas.GetTop(_blobs[i]) - ny * sep);
                        Canvas.SetLeft(_blobs[j], Canvas.GetLeft(_blobs[j]) + nx * sep);
                        Canvas.SetTop(_blobs[j], Canvas.GetTop(_blobs[j]) + ny * sep);

                        // Collision flash — brief opacity pulse
                        FlashBlob(_blobs[i], _states[i]);
                        FlashBlob(_blobs[j], _states[j]);
                    }
                }
            }
        }

        // Enforce min/max speed
        for (int i = 0; i < _states.Count; i++)
        {
            var s = _states[i];
            double speed = Math.Sqrt(s.VelocityX * s.VelocityX + s.VelocityY * s.VelocityY);
            if (speed < _minSpeed && speed > 0.01)
            {
                double boost = _minSpeed / speed;
                s.VelocityX *= boost;
                s.VelocityY *= boost;
            }
            else if (speed > _maxSpeed)
            {
                double damp = _maxSpeed / speed;
                s.VelocityX *= damp;
                s.VelocityY *= damp;
            }

            // Decay collision flash
            if (s.FlashRemaining > 0 && i < _blobs.Count)
            {
                s.FlashRemaining -= dt;
                if (s.FlashRemaining <= 0)
                {
                    s.FlashRemaining = 0;
                    _blobs[i].Opacity = s.BaseOpacity;
                }
            }
        }
    }

    /// <summary>
    /// Add a small random perpendicular nudge on wall bounce for variety.
    /// </summary>
    private void AddSpin(BlobState s)
    {
        s.VelocityX += (_rng.NextDouble() - 0.5) * 8;
        s.VelocityY += (_rng.NextDouble() - 0.5) * 8;
    }

    /// <summary>
    /// Brief opacity flash on collision for visual feedback.
    /// Sets a timer on the blob state that the render loop decays — no DispatcherTimer needed.
    /// </summary>
    private static void FlashBlob(FrameworkElement blob, BlobState state)
    {
        blob.Opacity = Math.Min(1.0, state.BaseOpacity + 0.3);
        state.FlashRemaining = 0.08; // seconds
    }
}
