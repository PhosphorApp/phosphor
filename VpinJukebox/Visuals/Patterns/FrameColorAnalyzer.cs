using System.Runtime.InteropServices;

namespace VpinJukebox;

/// <summary>
/// Analyzes the current OpenGL framebuffer to determine the dominant ROYGBIV color band.
/// Must be called while the GL context is current (after <c>projectm_opengl_render_frame</c>).
/// </summary>
internal static class FrameColorAnalyzer
{
    /// <summary>
    /// Minimum per-channel sum (R+G+B) for a pixel to be considered "lit" and included
    /// in color/brightness averaging. Pixels at or below this threshold are treated as
    /// background black and excluded so that dark backgrounds don't dilute the brightness
    /// of the actual visible content.
    /// </summary>
    private const int BlackPixelThreshold = 30; // ~4% of max 765 (255*3)

    /// <summary>
    /// Reads the current framebuffer and returns the dominant <see cref="RoygbivColor"/>
    /// by computing the average pixel color and mapping its hue to a color band.
    /// Near-black pixels are excluded so brightness reflects visible content only.
    /// </summary>
    /// <param name="width">Framebuffer width in pixels.</param>
    /// <param name="height">Framebuffer height in pixels.</param>
    /// <param name="sampleStep">
    /// Sampling stride — every Nth pixel in each axis. Higher = faster, lower = more accurate.
    /// Default 4 samples 1/16th of all pixels.
    /// </param>
    public static ColorAnalysis GetDominantColorBand(int width, int height, int sampleStep = 4)
    {
        int pixelCount = width * height;
        byte[] pixels = new byte[pixelCount * 4];

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            ProjectMInterop.glReadPixels(0, 0, width, height,
                ProjectMInterop.GL_RGBA, ProjectMInterop.GL_UNSIGNED_BYTE,
                handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }

        long totalR = 0, totalG = 0, totalB = 0;
        int samples = 0;

        for (int y = 0; y < height; y += sampleStep)
        {
            int rowOffset = y * width * 4;
            for (int x = 0; x < width; x += sampleStep)
            {
                int i = rowOffset + x * 4;
                byte r = pixels[i];
                byte g = pixels[i + 1];
                byte b = pixels[i + 2];

                // Skip near-black pixels so dark backgrounds don't dilute brightness
                if (r + g + b <= BlackPixelThreshold)
                    continue;

                totalR += r;
                totalG += g;
                totalB += b;
                samples++;
            }
        }

        if (samples == 0)
            return new ColorAnalysis(RoygbivColor.Red, 0f);

        double avgR = (double)totalR / samples / 255.0;
        double avgG = (double)totalG / samples / 255.0;
        double avgB = (double)totalB / samples / 255.0;

        RgbToHsb(avgR, avgG, avgB, out double hue, out double saturation, out double brightness);
        return RoygbivHelper.Analyze(hue, saturation, brightness);
    }

    private static void RgbToHsb(double r, double g, double b, out double hue, out double saturation, out double brightness)
    {
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        brightness = max;
        saturation = max < 0.001 ? 0 : delta / max;

        if (delta < 0.001)
        {
            hue = 0;
            return;
        }

        if (max == r)
            hue = 60.0 * (((g - b) / delta) % 6.0);
        else if (max == g)
            hue = 60.0 * (((b - r) / delta) + 2.0);
        else
            hue = 60.0 * (((r - g) / delta) + 4.0);

        hue = ((hue % 360.0) + 360.0) % 360.0;
    }
}
