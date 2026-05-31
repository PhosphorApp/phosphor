using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;

namespace Phosphor;

/// <summary>
/// N-body gravity pattern: small flat circles attract each other via gravity,
/// merging on aligned collisions and splitting on opposing ones.
/// Delegates physics to <see cref="GravitySimulator"/>.
/// </summary>
public sealed class GravityBlobPattern : BlobPatternBase
{
    private GravitySimulator? _simulator;

    /// <summary>Gravitational constant (100–800). Default 400.</summary>
    public static int GravityG { get; set; } = 400;

    /// <summary>Close-range orbit repulsion strength (0–6). 0 = off, 3 = default.</summary>
    public static double OrbitRepulsion { get; set; } = 3.0;

    /// <summary>Whether camera roam is enabled for this visualization.</summary>
    public static bool CameraRoam { get; set; }

    /// <summary>Whether to restart the simulation when a new track starts.</summary>
    public static bool RestartOnTrackChange { get; set; }

    public override BlobPattern PatternType => BlobPattern.Gravity;

    public GravityBlobPattern(BlobPatternConfig config)
        : base(config) { }

    protected override void CreateBlobs()
    {
        double w = Math.Max(200, _canvas.ActualWidth);
        double h = Math.Max(200, _canvas.ActualHeight);

        _states = BlobMotion.CreateStates(_blobCount, PatternType, w, h, _rng,
            _maxOrbitRadius, speedMultiplier: _speedMultiplier);

        for (int i = 0; i < _blobCount; i++)
        {
            // Small circles, 10-20px, random size
            double size = (10 + _rng.NextDouble() * 10) * _sizeMultiplier;

            // Random hue per body
            Color c = RandomHue();
            var brush = new SolidColorBrush(c);
            _brushes.Add(brush);

            // Subtle gradient: solid center, soft transparent edge
            var gradBrush = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.5),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5,
                GradientStops = new GradientStopCollection
                {
                    new(c, 0.0),
                    new(c, 0.7),
                    new(Color.FromArgb(0, c.R, c.G, c.B), 1.0),
                }
            };
            _gradBrushes.Add(gradBrush);

            double opacity = _intensity + _rng.NextDouble() * 0.1;

            var blob = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = gradBrush,
                Opacity = opacity,
                RenderTransformOrigin = new Point(0.5, 0.5),
                CacheMode = _useBitmapCache ? new BitmapCache(1.0) : null,
            };

            if (i < _states.Count)
            {
                _states[i].BaseSize = size;
                _states[i].BaseOpacity = opacity;
            }

            _canvas.Children.Add(blob);
            _blobs.Add(blob);
        }

        // Position blobs
        for (int i = 0; i < _blobs.Count && i < _states.Count; i++)
        {
            var (bx, by) = BlobMotion.GetInitialPosition(
                _states[i], BlobPattern.Gravity, w, h, _blobs[i].Width, _rng);
            _blobs[i].BeginAnimation(Canvas.LeftProperty, null);
            _blobs[i].BeginAnimation(Canvas.TopProperty, null);
            Canvas.SetLeft(_blobs[i], bx);
            Canvas.SetTop(_blobs[i], by);
        }
    }

    protected override void StartMotion()
    {
        _simulator = new GravitySimulator(
            _blobs, _states, _brushes, _gradBrushes,
            _canvas, _intensity, _speedMultiplier);
        _simulator.Start();
    }

    protected override void StopMotion()
    {
        _simulator?.Dispose();
        _simulator = null;
    }

    private Color RandomHue()
    {
        double h = _rng.NextDouble() * 360.0;
        double s = 0.9, v = 1.0;
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        double m = v - c;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
