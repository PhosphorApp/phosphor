using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace VpinJukebox;

/// <summary>
/// Lava lamp pattern: blobs sink, rise, and crest in a slow, organic cycle.
/// </summary>
public sealed class LavaLampBlobPattern : BlobPatternBase
{
    public override BlobPattern PatternType => BlobPattern.LavaLamp;

    public LavaLampBlobPattern(BlobPatternConfig config)
        : base(config) { }

    protected override void StartMotion()
    {
        foreach (var blob in _blobs)
            RetargetBlob(blob);
    }

    protected override void StopMotion() { }

    private void RetargetBlob(FrameworkElement blob)
    {
        if (_disposed) return;

        int idx = _blobs.IndexOf(blob);
        if (idx < 0 || idx >= _states.Count) return;

        BlobMotion.Retarget(blob, _states[idx], BlobPattern.LavaLamp,
            _canvas.ActualWidth, _canvas.ActualHeight, _speedMultiplier, _rng,
            b => RetargetBlob(b));
    }
}
