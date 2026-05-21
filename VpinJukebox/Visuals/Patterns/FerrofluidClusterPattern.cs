using System.Windows.Controls;

namespace VpinJukebox;

/// <summary>
/// Ferrofluid Cluster pattern: blobs coalesce toward a central magnetic core.
/// Audio reactivity drives the ferrofluid behavior — bass pulls blobs tight,
/// beats cause explosive scattering, and treble shoots bristle spikes outward.
/// Without audio, a built-in timer provides gentle organic pulsing.
/// </summary>
public sealed class FerrofluidClusterPattern : BlobPatternBase
{
    private FerrofluidSimulator? _simulator;

    public override BlobPattern PatternType => BlobPattern.FerrofluidCluster;

    public FerrofluidClusterPattern(BlobPatternConfig config)
        : base(config) { }

    protected override void CreateBlobs()
    {
        base.CreateBlobs();

        // Position blobs scattered around center
        double w = Math.Max(200, _canvas.ActualWidth);
        double h = Math.Max(200, _canvas.ActualHeight);
        for (int i = 0; i < _blobs.Count && i < _states.Count; i++)
        {
            var (bx, by) = BlobMotion.GetInitialPosition(
                _states[i], BlobPattern.FerrofluidCluster, w, h, _blobs[i].Width, _rng);
            _blobs[i].BeginAnimation(Canvas.LeftProperty, null);
            _blobs[i].BeginAnimation(Canvas.TopProperty, null);
            Canvas.SetLeft(_blobs[i], bx);
            Canvas.SetTop(_blobs[i], by);
        }
    }

    protected override void StartMotion()
    {
        _simulator = new FerrofluidSimulator(_blobs, _states, _canvas, _speedMultiplier);
        _simulator.Start();
    }

    protected override void StopMotion()
    {
        _simulator?.Dispose();
        _simulator = null;
    }

    /// <summary>
    /// Override to feed audio data into the ferrofluid simulator and apply
    /// standard size/opacity pulsing from the base class.
    /// </summary>
    public override void ApplyAudioReactive(AudioReactiveData data, double baseIntensity, double reactiveSpeedMs)
    {
        _simulator?.SetAudioData(data.Bass, data.Treble, data.IsBeat);
        base.ApplyAudioReactive(data, baseIntensity, reactiveSpeedMs);
    }
}
