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

    /// <summary>Central gravity pull toward canvas center (2–30). Default 6.</summary>
    public static double CentralGravity { get; set; } = 6.0;

    /// <summary>Continuous orbital perturbation (-10 to 10). Tangential nudge that keeps bodies swirling; sign sets direction (negative = clockwise), 0 = off. Default -3.</summary>
    public static double OrbitalPerturbation { get; set; } = -3.0;

    /// <summary>Whether camera roam is enabled for this visualization.</summary>
    public static bool CameraRoam { get; set; }

    /// <summary>Whether to restart the simulation when a new track starts.</summary>
    public static bool RestartOnTrackChange { get; set; }

    /// <summary>Blob count multiplier (0.5–10). Scales the max body count for the simulation.</summary>
    public static double BlobMultiplier { get; set; } = 1.0;

    /// <summary>Whether to show diagnostic overlay (zoom, body count) on screen.</summary>
    public static bool ShowDiagnostics { get; set; }

    /// <summary>Supernova threshold as diameter in pixels (60–400). 0 = disabled.</summary>
    public static double SupernovaMass { get; set; } = 150.0;

    /// <summary>When a body hits the supernova threshold it has a 50/50 chance to instead collapse
    /// into a black hole, which keeps absorbing until it reaches this multiple of the supernova
    /// diameter (1.1–2.0), then ends as a quasar. Ensures a black hole is never smaller than a
    /// supernova. Default 1.6.</summary>
    public static double BlackHoleMaxSizeFactor { get; set; } = 1.6;

    /// <summary>Universe density (0=Low, 1=Medium, 2=High). Controls dust injection threshold.</summary>
    public static int Density { get; set; } = 1;

    public override BlobPattern PatternType => BlobPattern.Gravity;

    public GravityBlobPattern(BlobPatternConfig config)
        : base(config) { }

    protected override void CreateBlobs()
    {
        double w = Math.Max(200, _canvas.ActualWidth);
        double h = Math.Max(200, _canvas.ActualHeight);

        // Scale initial blob count by the multiplier setting
        int scaledCount = Math.Max(5, (int)(_blobCount * BlobMultiplier));

        _states = BlobMotion.CreateStates(scaledCount, PatternType, w, h, _rng,
            _maxOrbitRadius, speedMultiplier: _speedMultiplier);

        for (int i = 0; i < scaledCount; i++)
        {
            // Small circles, 10-20px, random size
            double size = (10 + _rng.NextDouble() * 10) * _sizeMultiplier;

            // Random hue per body
            Color c = RandomHue();
            var brush = new SolidColorBrush(c);
            _brushes.Add(brush);

            // Subtle gradient: solid center, soft transparent edge (shared with simulator)
            var gradBrush = GravitySimulator.MakeSubtleGradient(c);
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
            _canvas, _intensity, _speedMultiplier, _useBitmapCache);
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
