using System.Windows.Controls;

namespace VpinJukebox;

/// <summary>
/// Bounce pattern: blobs move freely and bounce off walls and each other
/// with elastic 2D collisions. Delegates physics to <see cref="BounceSimulator"/>.
/// </summary>
public sealed class BounceBlobPattern : BlobPatternBase
{
    private BounceSimulator? _simulator;

    public override BlobPattern PatternType => BlobPattern.Bounce;

    public BounceBlobPattern(BlobPatternConfig config)
        : base(config) { }

    protected override void CreateBlobs()
    {
        base.CreateBlobs();

        // Position blobs at random positions (Bounce doesn't orbit)
        double w = Math.Max(200, _canvas.ActualWidth);
        double h = Math.Max(200, _canvas.ActualHeight);
        for (int i = 0; i < _blobs.Count && i < _states.Count; i++)
        {
            var (bx, by) = BlobMotion.GetInitialPosition(
                _states[i], BlobPattern.Bounce, w, h, _blobs[i].Width, _rng);
            _blobs[i].BeginAnimation(Canvas.LeftProperty, null);
            _blobs[i].BeginAnimation(Canvas.TopProperty, null);
            Canvas.SetLeft(_blobs[i], bx);
            Canvas.SetTop(_blobs[i], by);
        }
    }

    protected override void StartMotion()
    {
        _simulator = new BounceSimulator(_blobs, _states, _canvas, _speedMultiplier);
        _simulator.Start();
    }

    protected override void StopMotion()
    {
        _simulator?.Dispose();
        _simulator = null;
    }
}
