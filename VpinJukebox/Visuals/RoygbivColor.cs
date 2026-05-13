namespace VpinJukebox;

public enum RoygbivColor
{
    Red,        // 0°–29°, 330°–360°
    Orange,     // 30°–59°
    Yellow,     // 60°–89°
    Green,      // 90°–149°
    Blue,       // 150°–209°
    Indigo,     // 210°–269°
    Violet,     // 270°–329°
    White       // Low saturation, high brightness
}

/// <summary>
/// Result of color analysis containing the classified color band and relative brightness (0.0–1.0).
/// </summary>
/// <param name="SelfRendering">True when the analysis comes from a self-rendering pattern (ProjectM, Mandelbrot) rather than blob animation.</param>
public readonly record struct ColorAnalysis(RoygbivColor Color, float Brightness, double TopAvgLuminance = 0, bool SelfRendering = false);

public static class RoygbivHelper
{
    private const double SaturationThreshold = 0.15;
    private const double BrightnessThreshold = 0.60;

    /// <summary>
    /// Classifies a color from HSB components and returns both the color band and brightness.
    /// </summary>
    public static ColorAnalysis Analyze(double hue, double saturation, double brightness)
    {
        var color = saturation < SaturationThreshold && brightness > BrightnessThreshold
            ? RoygbivColor.White
            : FromHue(hue);

        return new ColorAnalysis(color, (float)brightness, 0);
    }

    public static RoygbivColor FromHue(double hue)
    {
        hue = ((hue % 360.0) + 360.0) % 360.0;
        return hue switch
        {
            < 30 => RoygbivColor.Red,
            < 60 => RoygbivColor.Orange,
            < 90 => RoygbivColor.Yellow,
            < 150 => RoygbivColor.Green,
            < 210 => RoygbivColor.Blue,
            < 270 => RoygbivColor.Indigo,
            < 330 => RoygbivColor.Violet,
            _ => RoygbivColor.Red
        };
    }

    /// <summary>
    /// DOF element number for a given color band (E120–E127).
    /// </summary>
    public static int ToDofNumber(this RoygbivColor color) => 120 + (int)color;
}
