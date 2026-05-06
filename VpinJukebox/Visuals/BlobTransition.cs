namespace VpinJukebox;

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
        var candidates = ConcretePatterns.AsEnumerable();
        if (exclude.HasValue)
            candidates = candidates.Where(p => p != exclude.Value);
        if (ExcludeMandelbrotFromRandom)
            candidates = candidates.Where(p => p != BlobPattern.Mandelbrot);
        if (ExcludeProjectMFromRandom)
            candidates = candidates.Where(p => p != BlobPattern.ProjectM);
        var arr = candidates.ToArray();
        return arr[rng.Next(arr.Length)];
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
        BlobPattern.LightCycle => new LightCycleBlobPattern(config),
        _ => new RandomBlobPattern(config),
    };
}
