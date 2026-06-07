using Color = System.Windows.Media.Color;

namespace Phosphor;

/// <summary>
/// Shared color-space utilities for blob visualizations.
/// </summary>
public static class ColorHelper
{
    /// <summary>
    /// Converts HSV (Hue, Saturation, Value) to a WPF <see cref="Color"/>.
    /// Unlike HSL, the Value channel runs from black (0) to full-intensity color (1)
    /// without ever approaching white, so colors stay saturated across the full range.
    /// </summary>
    /// <param name="h">Hue in degrees (0–360).</param>
    /// <param name="s">Saturation (0–1). 0 = grey, 1 = fully saturated.</param>
    /// <param name="v">Value / brightness (0–1). 0 = black, 1 = full color.</param>
    public static Color HsvToColor(double h, double s, double v)
    {
        h = ((h % 360.0) + 360.0) % 360.0;
        s = Math.Clamp(s, 0.0, 1.0);
        v = Math.Clamp(v, 0.0, 1.0);

        double c = v * s;
        double x = c * (1.0 - Math.Abs((h / 60.0) % 2.0 - 1.0));
        double m = v - c;

        double r, g, b;
        if (h < 60)       { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else              { r = c; g = 0; b = x; }

        return Color.FromRgb(
            (byte)Math.Clamp((r + m) * 255, 0, 255),
            (byte)Math.Clamp((g + m) * 255, 0, 255),
            (byte)Math.Clamp((b + m) * 255, 0, 255));
    }
}
