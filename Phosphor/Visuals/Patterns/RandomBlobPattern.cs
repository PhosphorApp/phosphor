using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Phosphor;

/// <summary>
/// Random blob pattern: blobs drift around randomly with no specific orbital structure.
/// </summary>
public sealed class RandomBlobPattern : BlobPatternBase
{
    public override BlobPattern PatternType => BlobPattern.Random;

    public RandomBlobPattern(BlobPatternConfig config)
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

        double w = Math.Max(1, _canvas.ActualWidth);
        double h = Math.Max(1, _canvas.ActualHeight);
        double overshoot = Math.Max(w, h) * 0.2;

        var baseDuration = 10 + _rng.NextDouble() * 15;
        var durationSec = baseDuration / Math.Max(0.1, _speedMultiplier);

        var animX = new DoubleAnimation
        {
            To = _rng.NextDouble() * (w + overshoot * 2) - overshoot - blob.Width * 0.5,
            Duration = TimeSpan.FromSeconds(durationSec),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        var animY = new DoubleAnimation
        {
            To = _rng.NextDouble() * (h + overshoot * 2) - overshoot - blob.Height * 0.5,
            Duration = TimeSpan.FromSeconds(durationSec + _rng.NextDouble() * 4),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        animX.Completed += (_, _) => RetargetBlob(blob);

        blob.BeginAnimation(Canvas.LeftProperty, animX);
        blob.BeginAnimation(Canvas.TopProperty, animY);
    }
}
