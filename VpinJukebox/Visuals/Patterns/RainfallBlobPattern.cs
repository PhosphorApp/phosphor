using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace VpinJukebox;

/// <summary>
/// Rainfall pattern: blobs fall from top to bottom and respawn at the top.
/// </summary>
public sealed class RainfallBlobPattern : BlobPatternBase
{
    public override BlobPattern PatternType => BlobPattern.Rainfall;

    public RainfallBlobPattern(BlobPatternConfig config)
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

        BlobMotion.Retarget(blob, _states[idx], BlobPattern.Rainfall,
            _canvas.ActualWidth, _canvas.ActualHeight, _speedMultiplier, _rng,
            b => RetargetBlob(b));
    }
}
