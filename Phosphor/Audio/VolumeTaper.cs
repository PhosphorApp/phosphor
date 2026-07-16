namespace Phosphor;

/// <summary>
/// Converts a 0–100 UI slider position into a perceptual playback gain.
///
/// LibVLC's <c>MediaPlayer.Volume</c> applies its own steep (roughly cubic) internal
/// attenuation, so a linear slider→volume mapping collapses almost the entire audible
/// range into the top few percent of the travel — mid-slider is nearly inaudible and small
/// movements near the top swing loudness wildly. To make the slider behave like the Windows
/// volume mixer (gradual, ~half at half travel), we apply a SQUARE-ROOT taper that BOOSTS
/// the mid-range before handing the value to the engine: <c>(slider/100)^0.5</c>. This
/// flattens the top of the curve and spreads usable control across the whole slider.
/// 0 stays silent, 100 stays unity. Used by every audio path (backglass main video, gapless
/// audio-only, and the playfield/backglass/topper ambient players) for consistent feel.
/// </summary>
public static class VolumeTaper
{
    /// <summary>
    /// Taper exponent. &lt;1 flattens the curve (boosts mid-range, gentler top); 1 is linear;
    /// &gt;1 steepens it. 0.5 (square root) approximates the Windows mixer feel given LibVLC's
    /// steep internal volume response. Lower this toward ~0.4 for an even gentler top.
    /// </summary>
    private const double Exponent = 0.5;

    /// <summary>
    /// Maps a 0–100 slider position to a LibVLC volume (0–100) via the taper.
    /// </summary>
    public static int VlcVolume(int sliderPercent)
    {
        double s = Math.Clamp(sliderPercent, 0, 100) / 100.0;
        return (int)Math.Round(Math.Pow(s, Exponent) * 100.0);
    }

    /// <summary>
    /// Maps a 0–100 slider position to a linear amplitude multiplier (0.0–1.0) via the taper,
    /// for per-sample software mixing (the gapless PCM path).
    /// </summary>
    public static float Amplitude(int sliderPercent)
    {
        double s = Math.Clamp(sliderPercent, 0, 100) / 100.0;
        return (float)Math.Pow(s, Exponent);
    }
}

