using System.Runtime.InteropServices;

namespace VpinJukebox;

/// <summary>
/// Analyzes the current OpenGL framebuffer to determine the dominant ROYGBIV color band.
/// Must be called while the GL context is current (after <c>projectm_opengl_render_frame</c>).
/// </summary>
internal static class FrameColorAnalyzer
{
    /// <summary>
    /// Reads the current framebuffer and returns the dominant <see cref="RoygbivColor"/>
    /// by computing the average pixel color and mapping its hue to a color band.
    /// </summary>
    /// <param name="width">Framebuffer width in pixels.</param>
    /// <param name="height">Framebuffer height in pixels.</param>
    /// <param name="sampleStep">
    /// Sampling stride — every Nth pixel in each axis. Higher = faster, lower = more accurate.
    /// Default 4 samples 1/16th of all pixels.
    /// </param>
    public static RoygbivColor GetDominantColorBand(int width, int height, int sampleStep = 4)
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
                totalR += pixels[i];
                totalG += pixels[i + 1];
                totalB += pixels[i + 2];
                samples++;
            }
        }

        if (samples == 0)
            return RoygbivColor.Red;

        double avgR = (double)totalR / samples / 255.0;
        double avgG = (double)totalG / samples / 255.0;
        double avgB = (double)totalB / samples / 255.0;

        double hue = RgbToHue(avgR, avgG, avgB);
        return RoygbivHelper.FromHue(hue);
    }

    private static double RgbToHue(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        if (delta < 0.001)
            return 0; // achromatic

        double hue;
        if (max == r)
            hue = 60.0 * (((g - b) / delta) % 6.0);
        else if (max == g)
            hue = 60.0 * (((b - r) / delta) + 2.0);
        else
            hue = 60.0 * (((r - g) / delta) + 4.0);

        return ((hue % 360.0) + 360.0) % 360.0;
    }
}
