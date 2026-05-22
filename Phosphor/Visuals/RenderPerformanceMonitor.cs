using System.Diagnostics;
using System.Windows.Media;

namespace Phosphor;

/// <summary>
/// Lightweight monitor that tracks WPF render frame rate via CompositionTarget.Rendering.
/// Call <see cref="Start"/> once at app startup; query <see cref="CurrentFps"/> or
/// <see cref="IsStrained"/> from any pattern to adapt visual quality.
/// </summary>
public static class RenderPerformanceMonitor
{
    private static readonly Stopwatch _stopwatch = new();
    private static long _lastTicks;
    private static double _fps;
    private static int _frameCount;
    private static long _windowStartTicks;
    private static bool _running;

    /// <summary>Rolling average FPS over the last measurement window.</summary>
    public static double CurrentFps => _fps;

    /// <summary>True when FPS has dropped below the strain threshold.</summary>
    public static bool IsStrained => _fps > 0 && _fps < StrainThresholdFps;

    /// <summary>FPS below this value is considered strained. Default 30.</summary>
    public static double StrainThresholdFps { get; set; } = 30.0;

    /// <summary>Measurement window in milliseconds. FPS is recalculated every window. Default 1000ms.</summary>
    public static int MeasurementWindowMs { get; set; } = 1000;

    public static void Start()
    {
        if (_running) return;
        _running = true;
        _stopwatch.Start();
        _windowStartTicks = _stopwatch.ElapsedTicks;
        _lastTicks = _windowStartTicks;
        _frameCount = 0;
        CompositionTarget.Rendering += OnRendering;
    }

    public static void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
        _stopwatch.Stop();
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        long now = _stopwatch.ElapsedTicks;
        _frameCount++;

        double elapsedMs = (now - _windowStartTicks) * 1000.0 / Stopwatch.Frequency;
        if (elapsedMs >= MeasurementWindowMs)
        {
            _fps = _frameCount / (elapsedMs / 1000.0);
            _frameCount = 0;
            _windowStartTicks = now;
        }

        _lastTicks = now;
    }
}
