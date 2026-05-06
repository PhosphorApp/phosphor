namespace VpinJukebox;

public enum RoygbivColor
{
    Red,        // 0°–29°, 330°–360°
    Orange,     // 30°–59°
    Yellow,     // 60°–89°
    Green,      // 90°–149°
    Blue,       // 150°–209°
    Indigo,     // 210°–269°
    Violet      // 270°–329°
}

public static class RoygbivHelper
{
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
    /// DOF element number for a given color band (E120–E126).
    /// </summary>
    public static int ToDofNumber(this RoygbivColor color) => 120 + (int)color;
}
