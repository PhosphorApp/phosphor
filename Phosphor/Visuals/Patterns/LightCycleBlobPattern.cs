using System.Windows.Controls;

namespace Phosphor;

/// <summary>
/// Tron-style light cycle pattern. Delegates simulation to <see cref="LightCycleSimulator"/>
/// which manages its own trail/grid canvas layers and modifies blob sizes and gradients.
/// On exit, the simulator is disposed (removing trails/grid) and blobs fly out at their
/// current (small) size — the next pattern creates fresh blobs at the correct size.
/// </summary>
public sealed class LightCycleBlobPattern : BlobPatternBase
{
    private LightCycleSimulator? _simulator;

    public override BlobPattern PatternType => BlobPattern.LightCycle;

    public LightCycleBlobPattern(BlobPatternConfig config)
        : base(config) { }

    protected override void CreateBlobs()
    {
        // LightCycleSimulator resizes blobs and modifies gradients itself during Start(),
        // so we create standard blobs and let the simulator transform them.
        base.CreateBlobs();

        // Position blobs at their off-screen starting positions
        double w = Math.Max(200, _canvas.ActualWidth);
        double h = Math.Max(200, _canvas.ActualHeight);
        for (int i = 0; i < _blobs.Count && i < _states.Count; i++)
        {
            var (bx, by) = BlobMotion.GetInitialPosition(
                _states[i], BlobPattern.LightCycle, w, h, _blobs[i].Width, _rng);
            _blobs[i].BeginAnimation(Canvas.LeftProperty, null);
            _blobs[i].BeginAnimation(Canvas.TopProperty, null);
            Canvas.SetLeft(_blobs[i], bx);
            Canvas.SetTop(_blobs[i], by);
        }
    }

    protected override void StartMotion()
    {
        _simulator = new LightCycleSimulator(_blobs, _states, _canvas, _speedMultiplier, _sizeMultiplier);
        _simulator.Start();
    }

    protected override void StopMotion()
    {
        _simulator?.Dispose();
        _simulator = null;
    }

    /// <summary>
    /// LightCycle's exit: dispose simulator first (removes trail/grid layers),
    /// then do the standard fly-out at whatever size the blobs currently are.
    /// </summary>
    public override void Exit(Action onComplete)
    {
        // Dispose simulator to remove trail/grid layers before fly-out
        _simulator?.Dispose();
        _simulator = null;

        base.Exit(onComplete);
    }
}
