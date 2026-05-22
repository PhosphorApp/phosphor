using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace Phosphor;

/// <summary>
/// Orbital blob patterns: PerfectClockwise, PerfectMixed, RoughClockwise, RoughMixed.
/// Blobs orbit the center of the canvas in smooth arcs.
/// </summary>
public sealed class OrbitalBlobPattern : BlobPatternBase
{
    private readonly BlobPattern _variant;

    public override BlobPattern PatternType => _variant;

    public OrbitalBlobPattern(BlobPatternConfig config, BlobPattern variant)
        : base(config)
    {
        _variant = variant;
    }

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

        BlobMotion.Retarget(blob, _states[idx], _variant,
            _canvas.ActualWidth, _canvas.ActualHeight, _speedMultiplier, _rng,
            b => RetargetBlob(b));
    }
}
