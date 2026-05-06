namespace VpinJukebox;

/// <summary>
/// Computes a Mandelbrot reference orbit at a center point using arbitrary-precision
/// arithmetic (<see cref="BigFloat"/>), then stores the orbit as double arrays for
/// consumption by the perturbation iteration on CPU or GPU.
///
/// Perturbation theory: instead of iterating z = z² + c for every pixel, we compute
/// one "reference orbit" Z_n at the center, then each pixel computes only its delta
/// δ_n from the reference: δ_{n+1} = 2·Z_n·δ_n + δ_n² + Δc, where Δc = c_pixel - c_center.
/// Since Δc is tiny (screen-space offset / zoom), standard float/double precision suffices
/// even at extreme zoom depths.
/// </summary>
internal sealed class MandelbrotReferenceOrbit
{
    /// <summary>Real parts of the reference orbit Z_n (as double, for delta iteration).</summary>
    public double[] Zr { get; }

    /// <summary>Imaginary parts of the reference orbit Z_n (as double, for delta iteration).</summary>
    public double[] Zi { get; }

    /// <summary>Number of valid entries in the orbit (may be less than array length if it escaped).</summary>
    public int Length { get; }

    /// <summary>Whether the reference orbit escaped (true) or remained bounded (false).</summary>
    public bool Escaped { get; }

    /// <summary>The iteration at which the reference escaped, or -1 if it didn't.</summary>
    public int EscapeIteration { get; }

    private MandelbrotReferenceOrbit(double[] zr, double[] zi, int length, bool escaped, int escapeIter)
    {
        Zr = zr;
        Zi = zi;
        Length = length;
        Escaped = escaped;
        EscapeIteration = escapeIter;
    }

    /// <summary>
    /// Compute the reference orbit at (centerRe, centerIm) using BigFloat precision.
    /// </summary>
    /// <param name="centerRe">Real part of the center point.</param>
    /// <param name="centerIm">Imaginary part of the center point.</param>
    /// <param name="maxIter">Maximum iteration count.</param>
    /// <param name="precision">BigFloat precision in bits (default 128).</param>
    public static MandelbrotReferenceOrbit Compute(double centerRe, double centerIm, int maxIter, int precision = 128)
    {
        BigFloat.Precision = precision;

        BigFloat cr = centerRe;
        BigFloat ci = centerIm;
        BigFloat zr = BigFloat.Zero;
        BigFloat zi = BigFloat.Zero;

        var orbitRe = new double[maxIter];
        var orbitIm = new double[maxIter];
        int length = maxIter;
        bool escaped = false;
        int escapeIter = -1;

        for (int i = 0; i < maxIter; i++)
        {
            // Store current Z_n as double for delta iteration
            double zrD = zr.ToDouble();
            double ziD = zi.ToDouble();
            orbitRe[i] = zrD;
            orbitIm[i] = ziD;

            // Bailout check
            if (zrD * zrD + ziD * ziD > 65536.0)
            {
                escaped = true;
                escapeIter = i;
                length = i + 1;
                break;
            }

            // z = z² + c using BigFloat
            BigFloat zr2 = zr * zr;
            BigFloat zi2 = zi * zi;
            BigFloat newZi = zr.Times2() * zi + ci;
            BigFloat newZr = zr2 - zi2 + cr;
            zr = newZr;
            zi = newZi;
        }

        return new MandelbrotReferenceOrbit(orbitRe, orbitIm, length, escaped, escapeIter);
    }

    /// <summary>
    /// Compute a smooth iteration value for a single pixel using perturbation from this reference orbit.
    /// </summary>
    /// <param name="deltaCr">Real offset from center: c_pixel_re - center_re.</param>
    /// <param name="deltaCi">Imaginary offset from center: c_pixel_im - center_im.</param>
    /// <param name="maxIter">Max iterations (should match orbit computation).</param>
    /// <returns>Smooth iteration count, or -1 for interior points.</returns>
    /// <summary>
    /// Compute a smooth iteration value for a single pixel using perturbation from this reference orbit.
    /// </summary>
    /// <param name="deltaCr">Real offset from center: c_pixel_re - center_re.</param>
    /// <param name="deltaCi">Imaginary offset from center: c_pixel_im - center_im.</param>
    /// <param name="maxIter">Max iterations (should match orbit computation).</param>
    /// <param name="centerRe">Center real coordinate (for glitch fallback).</param>
    /// <param name="centerIm">Center imaginary coordinate (for glitch fallback).</param>
    /// <returns>Smooth iteration count, or -1 for interior points.</returns>
    public double Iterate(double deltaCr, double deltaCi, int maxIter, double centerRe, double centerIm)
    {
        double dr = 0, di = 0; // δ_n starts at 0
        int limit = Math.Min(maxIter, Length);

        for (int i = 0; i < limit; i++)
        {
            double refZr = Zr[i];
            double refZi = Zi[i];

            // δ_{n+1} = 2·Z_n·δ_n + δ_n² + Δc
            double newDr = 2.0 * (refZr * dr - refZi * di) + dr * dr - di * di + deltaCr;
            double newDi = 2.0 * (refZr * di + refZi * dr) + 2.0 * dr * di + deltaCi;
            dr = newDr;
            di = newDi;

            // Full value is Z_n + δ_n
            double fullR = refZr + dr;
            double fullI = refZi + di;
            double mag2 = fullR * fullR + fullI * fullI;

            if (mag2 > 65536.0)
            {
                double log_zn = Math.Log(mag2) * 0.5;
                double nu = Math.Log(log_zn / Log2) / Log2;
                return i + 1.0 - nu;
            }

            // Glitch detection: if delta magnitude overwhelms the reference,
            // fall back to standard iteration with the reconstructed c.
            double refMag2 = refZr * refZr + refZi * refZi;
            double deltaMag2 = dr * dr + di * di;
            if (refMag2 > 1e-6 && deltaMag2 > refMag2 * 1e6)
            {
                double cr = centerRe + deltaCr;
                double ci = centerIm + deltaCi;
                return FallbackIterate(fullR, fullI, cr, ci, i, maxIter);
            }
        }

        return -1.0; // inside the set
    }

    private static readonly double Log2 = Math.Log(2.0);

    /// <summary>
    /// When a glitch is detected, continue with standard iteration from the current z value
    /// using the pixel's actual c coordinate.
    /// </summary>
    private static double FallbackIterate(double zr, double zi, double cr, double ci, int startIter, int maxIter)
    {
        double zr2 = zr * zr;
        double zi2 = zi * zi;

        for (int i = startIter; i < maxIter; i++)
        {
            if (zr2 + zi2 > 65536.0)
            {
                double log_zn = Math.Log(zr2 + zi2) * 0.5;
                double nu = Math.Log(log_zn / Log2) / Log2;
                return i + 1.0 - nu;
            }

            zi = 2.0 * zr * zi + ci;
            zr = zr2 - zi2 + cr;
            zr2 = zr * zr;
            zi2 = zi * zi;
        }

        return -1.0;
    }

    /// <summary>
    /// Pack the reference orbit into an interleaved float array suitable for GPU upload.
    /// Format: [Zr0, Zi0, Zr1, Zi1, ...] as float (32-bit).
    /// </summary>
    public float[] ToInterleavedFloats()
    {
        var result = new float[Length * 2];
        for (int i = 0; i < Length; i++)
        {
            result[i * 2] = (float)Zr[i];
            result[i * 2 + 1] = (float)Zi[i];
        }
        return result;
    }
}
