namespace VpinJukebox;

/// <summary>
/// Tags describing the visual character and rendering behavior of a zoom
/// target. Used by <see cref="MandelbrotZoomTargets.PickWeighted(System.Random)"/>
/// so the random picker can favor visually rich regions and (eventually) by the
/// rotation/audio-reactive code so the right behavior is chosen per target.
/// </summary>
[System.Flags]
internal enum MandelbrotTargetTags
{
    None              = 0,
    /// <summary>Tightly winding spiral structures.</summary>
    Spiral            = 1 << 0,
    /// <summary>Tree-like branching dendrite filaments.</summary>
    Dendrite          = 1 << 1,
    /// <summary>Thin thread-like filaments.</summary>
    Filament          = 1 << 2,
    /// <summary>Contains miniature copies of the full Mandelbrot set.</summary>
    MiniBrot          = 1 << 3,
    /// <summary>Multiple arms meeting at a point.</summary>
    Junction          = 1 << 4,
    /// <summary>Sharp cusp / pinch-point geometry.</summary>
    Cusp              = 1 << 5,
    /// <summary>Symmetric multi-arm "starfish" or pinwheel shapes.</summary>
    Symmetric         = 1 << 6,
    /// <summary>Sharp repeating sawtooth boundaries.</summary>
    Sawtooth          = 1 << 7,
    /// <summary>Julia-set-like braided self-similarity.</summary>
    JuliaLike         = 1 << 8,
    /// <summary>Curling tendril or "shepherd's crook".</summary>
    Tendril           = 1 << 9,
    /// <summary>Coordinate sits on the cardioid / a bulb itself; the
    /// perturbation slider must be non-zero to find boundary detail nearby.</summary>
    NeedsPerturbation = 1 << 10,
    /// <summary>Coordinates verified to give crisp detail at very deep zoom
    /// (? 1e10). These are good "show off" picks.</summary>
    DeepZoomFriendly  = 1 << 11,
    /// <summary>Looks especially good with the rotation visualization mode.</summary>
    RotationFriendly  = 1 << 12,
}

/// <summary>
/// Quality tier for weighted random selection.
/// </summary>
internal enum MandelbrotTargetQuality
{
    /// <summary>Reliable, well-loved coordinate. Picked at standard frequency.</summary>
    Standard = 0,
    /// <summary>Award-winning showcase coordinate. Picked twice as often as Standard.</summary>
    Premium = 1,
    /// <summary>Experimental / niche. Picked half as often as Standard.</summary>
    Experimental = 2,
}

/// <summary>One curated zoom target with metadata.</summary>
internal readonly record struct MandelbrotTarget(
    double Re,
    double Im,
    string Name,
    MandelbrotTargetTags Tags,
    MandelbrotTargetQuality Quality = MandelbrotTargetQuality.Standard);

/// <summary>
/// Curated Mandelbrot set boundary coordinates known to produce rich, self-similar
/// detail at every zoom depth. Each point has been chosen from well-known deep-zoom
/// showcase coordinates in the fractal community. Unverified cluster-variations
/// have been removed — use the Perturbation slider for variety instead.
/// </summary>
internal static class MandelbrotZoomTargets
{
    /// <summary>
    /// The full library of curated targets with metadata. Order is grouped by
    /// region for readability; the picker is unaffected by order.
    /// </summary>
    public static readonly MandelbrotTarget[] AllDetailed = BuildLibrary();

    /// <summary>
    /// Backward-compatible flat array of (Re, Im) pairs. Same content as
    /// <see cref="AllDetailed"/>.
    /// </summary>
    public static readonly (double Re, double Im)[] All = BuildCompatArray();

    /// <summary>
    /// Picks a target using quality-weighted random selection: Premium targets
    /// are weighted 2×, Standard 1×, Experimental 0.5×. The result still has
    /// good variety because every quality tier has many entries.
    /// </summary>
    public static MandelbrotTarget PickWeighted(System.Random rng)
    {
        double totalWeight = 0;
        for (int i = 0; i < AllDetailed.Length; i++)
            totalWeight += WeightFor(AllDetailed[i].Quality);

        double pick = rng.NextDouble() * totalWeight;
        for (int i = 0; i < AllDetailed.Length; i++)
        {
            pick -= WeightFor(AllDetailed[i].Quality);
            if (pick <= 0) return AllDetailed[i];
        }
        return AllDetailed[AllDetailed.Length - 1];
    }

    private static double WeightFor(MandelbrotTargetQuality q) => q switch
    {
        MandelbrotTargetQuality.Premium => 2.0,
        MandelbrotTargetQuality.Experimental => 0.5,
        _ => 1.0,
    };

    private static (double, double)[] BuildCompatArray()
    {
        var arr = new (double, double)[AllDetailed.Length];
        for (int i = 0; i < AllDetailed.Length; i++)
            arr[i] = (AllDetailed[i].Re, AllDetailed[i].Im);
        return arr;
    }

    private static MandelbrotTarget[] BuildLibrary()
    {
        const MandelbrotTargetTags Sp = MandelbrotTargetTags.Spiral;
        const MandelbrotTargetTags De = MandelbrotTargetTags.Dendrite;
        const MandelbrotTargetTags Fi = MandelbrotTargetTags.Filament;
        const MandelbrotTargetTags Mb = MandelbrotTargetTags.MiniBrot;
        const MandelbrotTargetTags Jc = MandelbrotTargetTags.Junction;
        const MandelbrotTargetTags Cu = MandelbrotTargetTags.Cusp;
        const MandelbrotTargetTags Sy = MandelbrotTargetTags.Symmetric;
        const MandelbrotTargetTags Sa = MandelbrotTargetTags.Sawtooth;
        const MandelbrotTargetTags Jl = MandelbrotTargetTags.JuliaLike;
        const MandelbrotTargetTags Te = MandelbrotTargetTags.Tendril;
        const MandelbrotTargetTags Np = MandelbrotTargetTags.NeedsPerturbation;
        const MandelbrotTargetTags Dz = MandelbrotTargetTags.DeepZoomFriendly;
        const MandelbrotTargetTags Rf = MandelbrotTargetTags.RotationFriendly;

        const MandelbrotTargetQuality Std = MandelbrotTargetQuality.Standard;
        const MandelbrotTargetQuality Pre = MandelbrotTargetQuality.Premium;
        const MandelbrotTargetQuality Exp = MandelbrotTargetQuality.Experimental;

        return
        [
            // ?? SEAHORSE VALLEY ??????????????????????????????
            new(-0.743643887037151,  0.131825904205330, "Seahorse deep spiral",       Sp | Dz | Rf,        Pre),
            new(-0.7435669,          0.1314023,         "Seahorse valley spiral",     Sp | Dz | Rf,        Pre),
            new(-0.745428,           0.113009,          "Seahorse valley variant",    Sp | Fi,             Std),
            new(-0.7453611859,       0.1130063062,      "Seahorse precise filament",  Sp | Fi | Dz,        Std),
            new(-0.74364085,         0.13182733,        "Seahorse micro-spiral",      Sp | Dz,             Std),
            new(-0.749763,           0.100223,          "Seahorse tendril",           Te | Sp,             Std),

            // ?? ELEPHANT VALLEY ??????????????????????????????
            new(-0.235125,           0.827215,          "Elephant valley filament",   Sp | Fi,             Std),
            new(-0.194862076016,     0.655708673520,    "Elephant deep spiral",       Sp | Dz | Rf,        Pre),
            new(-0.1006,             0.8858,            "Triple spiral junction",     Sp | Jc | Sy | Rf,   Pre),

            // ?? ANTENNA / DENDRITE FILAMENTS ?????????????????
            new(-0.0452407,          0.9868162,         "Antenna filament spiral",    De | Sp | Fi,        Std),
            new(-0.16070135,         1.0375665,         "Dendrite filament",          De | Fi,             Std),
            new(-0.157076620,        1.041517795,       "Dendrite deep branch",       De | Dz,             Std),

            // ?? FAMOUS DEEP-ZOOM SPIRALS ????????????????????
            new( 0.001643721971153,  0.822467633298876, "Iconic deep-zoom spiral",    Sp | Dz | Rf,        Pre),
            new( 0.360240443437614,  0.641313061064803, "Spiral arm junction",        Sp | Jc | Dz,        Pre),
            new(-0.562440248667898,  0.642885598563016, "Counter-spiral",             Sp | Dz,             Std),
            new( 0.432539867561025,  0.226118373390324, "Double spiral",              Sp | Sy | Dz,        Pre),
            new( 0.281717921930775,  0.5771052841488505,"Fibonacci spiral",           Sp | Dz | Rf,        Pre),
            new(-0.748986521146,     0.055768890479,    "Period boundary spiral",     Sp,                  Std),
            new( 0.356884822876,     0.326964248084,    "Twisted arm",                Sp,                  Std),

            // ?? PERIOD-3 BULB BOUNDARY ??????????????????????
            new(-1.769110375463,     0.009020388228,    "Period-3 boundary",          Mb | Sp | Dz,        Pre),
            new(-1.768778833,        0.001738996,       "Period-3 satellite",         Mb | Sp,             Std),

            // ?? MINI-BROT BOUNDARIES ????????????????????????
            new(-1.25066,            0.02012,           "Mini-brot in period-2 cleft",Mb | Sp,             Std),
            new(-1.94080,            0.00100,           "Tail mini-brot boundary",    Mb,                  Std),
            new(-1.985424253,        0.000000200,       "Deep tail mini-brot",        Mb | Dz,             Pre),
            new(-0.156520166,        1.032247109,       "Satellite mini-brot (dendrite)", Mb | De,         Std),
            new(-0.101096364,        0.956286510,       "Satellite mini-brot (antenna)",  Mb | De,         Std),

            // ?? MISIUREWICZ POINTS ??????????????????????????
            new(-1.0,                0.3,               "Near period-2 neck",         Np | Fi,             Exp),
            new(-0.228155,           1.115143,          "Misiurewicz M_{3,1}",        De | Fi,             Std),
            new(-1.430000,           0.000100,          "Near Feigenbaum cascade",    Mb | Sa,             Std),

            // ?? GOLDEN RATIO / FIBONACCI SPIRALS ????????????
            new(-0.390540870218,     0.586787907347,    "Golden ratio spiral",        Sp | Sy | Rf,        Pre),
            new(-0.390540870218,    -0.586787907347,    "Mirror golden spiral",       Sp | Sy | Rf,        Pre),

            // ?? CARDIOID CUSP ???????????????????????????????
            new( 0.25000010,         0.00000010,        "Cardioid cusp micro-spiral", Cu | Sp,             Std),
            new( 0.25000050,         0.00000001,        "Cardioid cusp precision",    Cu,                  Std),

            // ?? SPIRAL ARM JUNCTIONS ????????????????????????
            new(-0.56252,            0.64270,           "Double arm junction",        Sp | Jc | Sy,        Std),
            new(-0.481762,           0.531657,          "Twisted junction",           Sp | Jc,             Std),
            new(-0.624,              0.435,             "Offset junction",            Sp | Jc,             Std),

            // ?? SCEPTER VALLEY ??????????????????????????????
            new(-1.25066,            0.02012,           "Scepter valley entry",       Sp | Fi,             Std),
            new(-1.25070,            0.02015,           "Scepter valley deep",        Sp | Fi | Dz,        Std),

            // ?? FEIGENBAUM POINT VICINITY ???????????????????
            new(-1.401155,           0.000100,          "Period-doubling cascade",    Mb | Sa | Dz,        Pre),
            new(-1.401155,           0.000200,          "Feigenbaum variant",         Mb | Sa,             Std),

            // ?? HIGHER-PERIOD BULB BOUNDARIES ???????????????
            new(-1.31070,            0.07000,           "Period-4 bulb boundary",     Sp | Sy,             Std),
            new(-0.50440,            0.56280,           "Period-5 bulb boundary",     Sp | Sy,             Std),
            new(-1.13000,            0.24060,           "Period-6 bulb boundary",     Sp | Sy,             Std),

            // ?? TWISTED FILAMENTS ???????????????????????????
            new(-0.835,              0.2321,            "Inter-structure filament",   Fi,                  Std),
            new(-0.108625,           0.901155,          "Fine boundary thread",       Fi | De,             Std),

            // ?? TAIL REGION ?????????????????????????????????
            new(-1.950,              0.00010,           "Tail filament",              Fi,                  Std),
            new(-1.975,              0.00060,           "Deep tail spiral",           Sp | Dz,             Std),

            // ?? CUSP SPIRALS ????????????????????????????????
            new(-0.749990,           0.016000,          "Cardioid-bulb cusp",         Cu | Sp,             Std),
            new(-1.1592784,          0.0340232,         "Double spiral region",       Sp | Sy,             Std),

            // ?? JULIA-LIKE STRUCTURES ???????????????????????
            new(-0.7269,             0.1889,            "Julia island",               Jl | Rf,             Pre),
            new( 0.2929859127507,    0.6117848324958,   "Julia-braided triple spiral",Jl | Sp | Sy | Rf,   Pre),
            new(-1.7497219360091,    0.0000284779863,   "Period-3 Julia filaments",   Jl | Fi | Dz,        Std),

            // ?? STARFISH / PINWHEEL REGIONS ?????????????????
            new(-0.77568377,         0.13646737,        "5-arm starfish junction",    Sy | Jc | Rf,        Pre),
            new(-0.74591440,         0.11272897,        "7-arm pinwheel",             Sy | Jc | Rf,        Pre),
            new(-1.74995768370253,   0.00000003580153,  "Deep period-3 starburst",    Sy | Jc | Dz | Rf,   Pre),

            // ?? BUZZSAW / SAWTOOTH BOUNDARIES ???????????????
            new(-1.62917,            0.0203968,         "Sawtooth filament",          Sa | Fi,             Std),
            new(-0.16299896,         1.03916628,        "Dendrite sawtooth",          Sa | De,             Std),

            // ?? BENCHMARK COORDINATES ???????????????????????
            new(-1.7400623825,       0.0281753397,      "Burning ship adjacent",      Sp | Mb,             Std),
            new(-0.748,              0.1,               "Garcia/Christensen ref",     Sp | Np,             Exp),
            new(-1.99996619445451,   0.00000000000013,  "Extreme tail mini-brot",     Mb | Dz,             Pre),
            new( 0.250006,          -0.000004,          "Cardioid tip (mirrored)",    Cu,                  Std),

            // ?? SHEPHERD'S CROOK / TENDRILS ?????????????????
            new(-0.77807218,         0.12869377,        "Shepherd's crook",           Te | Rf,             Pre),
            new(-0.10109636384562,   0.95628651080914,  "Long curling tendril",       Te | De,             Std),
            new(-1.25563109,         0.38104311,        "Hooked tendril (period-4)",  Te | Sp,             Std),

            // ?? PERIOD-N BULB NECKS (need perturbation) ?????
            new(-1.25,               0.0,               "Period-2/4 neck",            Np | Cu,             Exp),
            new(-0.125,              0.6494,            "Period-3/cardioid neck",     Np | Cu,             Exp),
            new( 0.379,              0.3349,            "Period-4/cardioid neck",     Np | Cu,             Exp),
            new(-1.3107,             0.0,               "Period-4/period-2 neck",     Np | Cu,             Exp),

            // ?? SECONDARY MINI-BROT CHAINS ??????????????????
            new(-1.6735,             0.00038,           "Antenna chain mini-brot",    Mb | Sa,             Std),
            new(-1.7691105,          0.0036368,         "Smaller period-3 child",     Mb | Sp,             Std),
            new(-1.476014,           0.0,               "Period-3 child on antenna",  Mb | Np,             Exp),
        ];
    }
}
