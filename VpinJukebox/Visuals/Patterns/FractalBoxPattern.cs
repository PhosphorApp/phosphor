using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using WpfShapes = System.Windows.Shapes;

namespace VpinJukebox;

/// <summary>
/// Fractal box pattern: rectangles (stroked outlines) arranged in concentric
/// spinning rings with pulsation — identical motion to <see cref="FractalBlobPattern"/>
/// but using rectangle outlines instead of gradient-filled ellipses.
/// </summary>
public sealed class FractalBoxPattern : BlobPatternBase
{
    private const double BaseBlurRadius = 3.0;
    private const double MaxReactiveBlurRadius = 25.0;

    public override BlobPattern PatternType => BlobPattern.FractalBox;

    public FractalBoxPattern(BlobPatternConfig config)
        : base(config) { }

    protected override void CreateBlobs()
    {
        double w = Math.Max(200, _canvas.ActualWidth);
        double h = Math.Max(200, _canvas.ActualHeight);

        _states = BlobMotion.CreateStates(_blobCount, PatternType, w, h, _rng,
            _maxOrbitRadius, speedMultiplier: _speedMultiplier);

        // Base box size is ~1/10 of the screen width, with ±30% random variation
        double baseBoxSize = w / 10.0;

        for (int i = 0; i < _blobCount; i++)
        {
            double size = baseBoxSize * (0.7 + _rng.NextDouble() * 0.7);
            var brush = new SolidColorBrush(Colors.Black);
            _brushes.Add(brush);

            // Gradient brush kept for color-cycling compatibility
            var gradBrush = new RadialGradientBrush
            {
                GradientOrigin = new System.Windows.Point(0.5, 0.5),
                Center = new System.Windows.Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5,
                GradientStops = new GradientStopCollection
                {
                    new(System.Windows.Media.Color.FromArgb(255, 0, 0, 0), 0.0),
                    new(System.Windows.Media.Color.FromArgb(120, 0, 0, 0), 0.4),
                    new(System.Windows.Media.Color.FromArgb(0, 0, 0, 0), 1.0),
                }
            };
            _gradBrushes.Add(gradBrush);

            double opacity = _intensity + _rng.NextDouble() * 0.1;
            double strokeThickness = 2.0 + size * 0.008;

            // 10% smaller than standard blob size
            //size *= 0.9;

            var scaleTransform = new ScaleTransform(1.0, 1.0);
            var rotateTransform = new RotateTransform(0, size / 2, size / 2);
            var transformGroup = new TransformGroup();
            transformGroup.Children.Add(scaleTransform);
            transformGroup.Children.Add(rotateTransform);

            var rect = new WpfShapes.Rectangle
            {
                Width = size,
                Height = size,
                Fill = System.Windows.Media.Brushes.Transparent,
                Stroke = brush,
                StrokeThickness = strokeThickness,
                Opacity = opacity,
                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                RenderTransform = transformGroup,
                CacheMode = _useBitmapCache ? new BitmapCache(0.5) : null,
            };

            if (i < _states.Count)
            {
                _states[i].BaseSize = size;
                _states[i].BaseOpacity = opacity;
            }

            _canvas.Children.Add(rect);
            _blobs.Add(rect);
        }
    }

    /// <inheritdoc />
    public override void ApplyAudioReactive(AudioReactiveData data, double baseIntensity, double reactiveSpeedMs)
    {
        if (_disposed || _blobs.Count == 0) return;

        float intensity = Math.Clamp(data.Bass * 1.5f + (data.IsBeat ? 0.25f : 0f), 0f, 1f);
        double blurRadius = BaseBlurRadius + intensity * (MaxReactiveBlurRadius - BaseBlurRadius);
        if (data.IsBeat) blurRadius = Math.Min(blurRadius + 5.0, MaxReactiveBlurRadius);

        double scale = 1.0 + data.Bass * 0.35;
        if (data.IsBeat) scale += 0.08;
        scale = Math.Min(scale, 1.4);

        var dur = TimeSpan.FromMilliseconds(reactiveSpeedMs);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        for (int i = 0; i < _blobs.Count; i++)
        {
            var blob = _blobs[i];
            blob.Opacity = baseIntensity + intensity * 0.25;

            if (blurRadius > 0.5)
            {
                if (blob.Effect is not BlurEffect blur)
                {
                    blur = new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
                    blob.Effect = blur;
                }
                blur.BeginAnimation(BlurEffect.RadiusProperty,
                    new DoubleAnimation(blurRadius, dur) { EasingFunction = ease });
            }
            else if (blob.Effect is BlurEffect)
            {
                blob.Effect = null;
            }

            // Pulse scale via the ScaleTransform in the TransformGroup
            if (blob.RenderTransform is TransformGroup tg
                && tg.Children.OfType<ScaleTransform>().FirstOrDefault() is { } st)
            {
                st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, dur) { EasingFunction = ease });
                st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, dur) { EasingFunction = ease });
            }
        }
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

        BlobMotion.Retarget(blob, _states[idx], BlobPattern.FractalBox,
            _canvas.ActualWidth, _canvas.ActualHeight, _speedMultiplier, _rng,
            b => RetargetBlob(b));
    }
}
