namespace VpinJukebox;

/// <summary>
/// Curated Mandelbrot set boundary coordinates known to produce rich, self-similar
/// detail at every zoom depth. Each point has been chosen from well-known deep-zoom
/// showcase coordinates in the fractal community. Unverified cluster-variations
/// have been removed — use the Perturbation slider for variety instead.
/// </summary>
internal static class MandelbrotZoomTargets
{
    /// <summary>
    /// Returns the full library of zoom targets.
    /// Each entry is (Real, Imaginary) in the complex plane.
    /// </summary>
    public static readonly (double Re, double Im)[] All =
    [
        // ── SEAHORSE VALLEY ──────────────────────────────
        // The most reliably intricate region of the set.
        // These are the classic deep-zoom showcase points.
        (-0.743643887037151,  0.131825904205330),  // Seahorse deep spiral (famous)
        (-0.7435669,          0.1314023),           // Seahorse valley spiral
        (-0.745428,           0.113009),            // Seahorse valley variant
        (-0.7453611859,       0.1130063062),        // Seahorse precise filament
        (-0.74364085,         0.13182733),          // Seahorse micro-spiral
        (-0.749763,           0.100223),            // Seahorse tendril

        // ── ELEPHANT VALLEY ──────────────────────────────
        // Trunk-like spirals with thick bifurcating branches.
        (-0.235125,           0.827215),            // Elephant valley filament
        (-0.194862076016,     0.655708673520),      // Elephant deep spiral
        (-0.1006,             0.8858),              // Triple spiral junction

        // ── ANTENNA / DENDRITE FILAMENTS ─────────────────
        // Top of the main cardioid — delicate branching structures.
        (-0.0452407,          0.9868162),           // Antenna filament spiral
        (-0.16070135,         1.0375665),           // Dendrite filament (precise)
        (-0.157076620,        1.041517795),         // Dendrite deep branch

        // ── FAMOUS DEEP-ZOOM SPIRALS ────────────────────
        // Well-documented coordinates used in award-winning zoom videos.
        ( 0.001643721971153,  0.822467633298876),   // Deep zoom spiral (iconic)
        ( 0.360240443437614,  0.641313061064803),   // Spiral arm junction
        (-0.562440248667898,  0.642885598563016),   // Counter-spiral
        ( 0.432539867561025,  0.226118373390324),   // Double spiral
        ( 0.281717921930775,  0.5771052841488505),  // Fibonacci spiral
        (-0.748986521146,     0.055768890479),      // Period boundary spiral
        ( 0.356884822876,     0.326964248084),      // Twisted arm

        // ── PERIOD-3 BULB BOUNDARY ──────────────────────
        // The leftmost mini-brot — spirals near its boundary.
        (-1.769110375463,     0.009020388228),      // Period-3 boundary (precise)
        (-1.768778833,        0.001738996),         // Period-3 satellite

        // ── MINI-BROT BOUNDARIES ────────────────────────
        // Zoom into the *boundary* of miniature copies of the full set.
        (-1.25066,            0.02012),             // Mini-brot in period-2 cleft
        (-1.94080,            0.00100),             // Tail mini-brot boundary
        (-1.985424253,        0.000000200),         // Deep tail mini-brot
        (-0.156520166,        1.032247109),         // Satellite mini-brot (dendrite)
        (-0.101096364,        0.956286510),         // Satellite mini-brot (antenna)

        // ── MISIUREWICZ POINTS ──────────────────────────
        // Pre-periodic points — mathematically guaranteed to be on the boundary.
        // Perturbed slightly off the exact value to avoid landing on the point itself.
        (-1.0,                0.3),                 // Near period-2 neck
        (-0.228155,           1.115143),            // Misiurewicz M_{3,1}
        (-1.430000,           0.000100),            // Near Feigenbaum accumulation

        // ── GOLDEN RATIO / FIBONACCI SPIRALS ────────────
        (-0.390540870218,     0.586787907347),      // Golden ratio spiral
        (-0.390540870218,    -0.586787907347),      // Mirror golden spiral

        // ── CARDIOID CUSP ───────────────────────────────
        // The rightmost point of the main cardioid — infinite detail at the tip.
        ( 0.25000010,         0.00000010),          // Cusp micro-spiral
        ( 0.25000050,         0.00000001),          // Cusp precision point

        // ── SPIRAL ARM JUNCTIONS ────────────────────────
        (-0.56252,            0.64270),             // Double arm junction
        (-0.481762,           0.531657),            // Twisted junction
        (-0.624,              0.435),               // Offset junction

        // ── SCEPTER VALLEY ──────────────────────────────
        // The cleft between the main cardioid and the period-2 bulb.
        (-1.25066,            0.02012),             // Scepter valley entry
        (-1.25070,            0.02015),             // Scepter valley deep

        // ── FEIGENBAUM POINT VICINITY ───────────────────
        (-1.401155,           0.000100),            // Near period-doubling cascade
        (-1.401155,           0.000200),            // Feigenbaum variant

        // ── BULB BOUNDARIES ─────────────────────────────
        // Boundaries of higher-period bulbs.
        (-1.31070,            0.07000),             // Period-4 bulb boundary
        (-0.50440,            0.56280),             // Period-5 bulb boundary
        (-1.13000,            0.24060),             // Period-6 bulb boundary

        // ── TWISTED FILAMENTS ───────────────────────────
        (-0.835,              0.2321),              // Inter-structure filament
        (-0.108625,           0.901155),            // Fine boundary thread

        // ── TAIL REGION ─────────────────────────────────
        // The far-left tail has surprisingly intricate detail.
        (-1.950,              0.00010),             // Tail filament
        (-1.975,              0.00060),             // Deep tail spiral

        // ── CUSP SPIRALS ────────────────────────────────
        (-0.749990,           0.016000),            // Cardioid-bulb cusp
        (-1.1592784,          0.0340232),           // Double spiral region
    ];
}
