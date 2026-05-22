namespace Phosphor;

/// <summary>
/// Arbitrary-precision floating-point number for Mandelbrot reference orbit computation.
/// Internally stores a <see cref="System.Numerics.BigInteger"/> mantissa and a base-2 exponent.
/// Precision is configurable via <see cref="Precision"/> (number of mantissa bits).
/// 
/// This is intentionally minimal — only the operations needed for z = z² + c are implemented.
/// </summary>
internal readonly struct BigFloat
{
    /// <summary>Number of mantissa bits to retain after each operation.</summary>
    public static int Precision { get; set; } = 128;

    private readonly System.Numerics.BigInteger _mantissa;
    private readonly int _exponent; // value = mantissa * 2^exponent

    public BigFloat(System.Numerics.BigInteger mantissa, int exponent)
    {
        _mantissa = mantissa;
        _exponent = exponent;
    }

    public static BigFloat Zero => new(0, 0);

    public static implicit operator BigFloat(double value)
    {
        if (value == 0) return Zero;
        // Decompose double into mantissa × 2^exp
        long bits = BitConverter.DoubleToInt64Bits(value);
        bool negative = (bits >> 63) != 0;
        int rawExp = (int)((bits >> 52) & 0x7FF);
        long frac = bits & 0x000FFFFFFFFFFFFF;

        if (rawExp == 0)
        {
            // Subnormal
            rawExp = 1;
        }
        else
        {
            frac |= 1L << 52; // implicit leading 1
        }

        // value = frac * 2^(rawExp - 1023 - 52)
        var mantissa = new System.Numerics.BigInteger(frac);
        int exponent = rawExp - 1023 - 52;
        if (negative) mantissa = -mantissa;

        return new BigFloat(mantissa, exponent).Normalize();
    }

    public double ToDouble()
    {
        if (_mantissa.IsZero) return 0.0;
        // Convert back: value = mantissa * 2^exponent
        double m = (double)_mantissa;
        return m * Math.Pow(2.0, _exponent);
    }

    private BigFloat Normalize()
    {
        if (_mantissa.IsZero) return Zero;

        var abs = System.Numerics.BigInteger.Abs(_mantissa);
        int bits = (int)abs.GetBitLength();

        if (bits > Precision)
        {
            int shift = bits - Precision;
            return new BigFloat(_mantissa >> shift, _exponent + shift);
        }

        return this;
    }

    /// <summary>Align two BigFloats to the same exponent for add/subtract.</summary>
    private static void Align(in BigFloat a, in BigFloat b,
        out System.Numerics.BigInteger aM, out System.Numerics.BigInteger bM, out int exp)
    {
        if (a._exponent < b._exponent)
        {
            int shift = b._exponent - a._exponent;
            aM = a._mantissa;
            bM = b._mantissa << shift;
            exp = a._exponent;
        }
        else
        {
            int shift = a._exponent - b._exponent;
            aM = a._mantissa << shift;
            bM = b._mantissa;
            exp = b._exponent;
        }
    }

    public static BigFloat operator +(BigFloat a, BigFloat b)
    {
        if (a._mantissa.IsZero) return b;
        if (b._mantissa.IsZero) return a;
        Align(a, b, out var aM, out var bM, out int exp);
        return new BigFloat(aM + bM, exp).Normalize();
    }

    public static BigFloat operator -(BigFloat a, BigFloat b)
    {
        if (b._mantissa.IsZero) return a;
        Align(a, b, out var aM, out var bM, out int exp);
        return new BigFloat(aM - bM, exp).Normalize();
    }

    public static BigFloat operator *(BigFloat a, BigFloat b)
    {
        return new BigFloat(a._mantissa * b._mantissa, a._exponent + b._exponent).Normalize();
    }

    public static BigFloat operator -(BigFloat a)
    {
        return new BigFloat(-a._mantissa, a._exponent);
    }

    /// <summary>Multiply by 2 (exact, no precision loss).</summary>
    public BigFloat Times2() => new(_mantissa, _exponent + 1);

    /// <summary>Returns mantissa × 2^exponent > threshold² (for bailout check).</summary>
    public bool MagnitudeSquaredExceeds(double threshold)
    {
        return ToDouble() > threshold;
    }
}
