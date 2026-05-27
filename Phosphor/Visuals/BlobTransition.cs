namespace Phosphor;

/// <summary>
/// Helper for RandomPerSong transitions: picks random patterns and creates
/// pattern instances. The actual enter/exit animation is owned by each
/// <see cref="IBlobPattern"/> implementation.
/// </summary>
public static class BlobTransition
{
    private static readonly BlobPattern[] ConcretePatterns =
    [
        BlobPattern.RoughClockwise,
        BlobPattern.PerfectClockwise,
        BlobPattern.RoughMixed,
        BlobPattern.PerfectMixed,
        BlobPattern.Rainfall,
        BlobPattern.LavaLamp,
        BlobPattern.Bounce,
        BlobPattern.LightCycle,
        BlobPattern.Fractal,
        BlobPattern.FractalBox,
        BlobPattern.Mandelbrot,
        BlobPattern.ProjectM,
        BlobPattern.FerrofluidCluster,
        BlobPattern.Matrix,
    ];

    /// <summary>
    /// Whether to exclude ProjectM from random pattern selection.
    /// Set from <see cref="AppSettings.ExcludeProjectMFromRandom"/>.
    /// </summary>
    public static bool ExcludeProjectMFromRandom { get; set; } = true;

    /// <summary>
    /// The current globally-shared random pattern used by all windows when
    /// their blob pattern is set to <see cref="BlobPattern.RandomPerSong"/>.
    /// </summary>
    public static BlobPattern CurrentRandomPattern { get; set; } = ConcretePatterns[Random.Shared.Next(ConcretePatterns.Length)];

    /// <summary>
    /// Whether to exclude Mandelbrot from random pattern selection due to its high performance cost.
    /// Set from <see cref="AppSettings.ExcludeMandelbrotFromRandom"/>.
    /// </summary>
    public static bool ExcludeMandelbrotFromRandom { get; set; } = true;

    /// <summary>
    /// Pick a random concrete pattern, optionally excluding one.
    /// </summary>
    public static BlobPattern PickRandom(Random rng, BlobPattern? exclude = null)
    {
        // Use stack-based filtering to avoid LINQ/array allocations
        Span<BlobPattern> buf = stackalloc BlobPattern[ConcretePatterns.Length];
        int count = 0;
        foreach (var p in ConcretePatterns)
        {
            if (exclude.HasValue && p == exclude.Value) continue;
            if (ExcludeMandelbrotFromRandom && p == BlobPattern.Mandelbrot) continue;
            if (ExcludeProjectMFromRandom && p == BlobPattern.ProjectM) continue;
            buf[count++] = p;
        }
        return buf[rng.Next(count)];
    }

    /// <summary>
    /// Create an <see cref="IBlobPattern"/> for the given pattern type and config.
    /// </summary>
    public static IBlobPattern Create(BlobPattern pattern, BlobPatternConfig config) => pattern switch
    {
        BlobPattern.PerfectClockwise or BlobPattern.PerfectMixed
            or BlobPattern.RoughClockwise or BlobPattern.RoughMixed
            => new OrbitalBlobPattern(config, pattern),

        BlobPattern.Rainfall => new RainfallBlobPattern(config),
        BlobPattern.LavaLamp => new LavaLampBlobPattern(config),
        BlobPattern.Fractal => new FractalBlobPattern(config),
        BlobPattern.FractalBox => new FractalBoxPattern(config),
        BlobPattern.Mandelbrot => new MandelbrotPattern(config),
        BlobPattern.ProjectM => new ProjectMPattern(config),
        BlobPattern.Bounce => new BounceBlobPattern(config),
        BlobPattern.FerrofluidCluster => new FerrofluidClusterPattern(config),
        BlobPattern.LightCycle => new LightCycleBlobPattern(config),
        BlobPattern.Matrix => new MatrixBlobPattern(config),
        _ => new RandomBlobPattern(config),
    };
}
