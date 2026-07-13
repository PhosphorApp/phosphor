namespace Phosphor.Plugin.Abstractions;

/// <summary>
/// The caller's playback preferences passed to resolve/download. Sources honor what they
/// can and ignore the rest.
/// </summary>
public sealed record PlaybackPreferences
{
    /// <summary>Quality ceiling the source should not exceed when picking a stream.</summary>
    public VideoQuality MaxQuality { get; init; } = VideoQuality.High;

    /// <summary>When true, avoid surround audio tracks in favor of stereo.</summary>
    public bool PreferStereo { get; init; }

    /// <summary>When true, resolve an audio-only stream even for a video item.</summary>
    public bool AudioOnly { get; init; }
}

/// <summary>Coarse quality ceiling, mapped by each source to its own format ladder.</summary>
public enum VideoQuality
{
    Low,
    Medium,
    High,
    Max,
}

/// <summary>
/// Raw downloaded media for the host's disk cache, produced by <see cref="IDownloadable"/>.
/// When the source produced separate tracks, both <see cref="VideoFilePath"/> and
/// <see cref="AudioFilePath"/> are set and the host muxes them; for a muxed/audio-only
/// download only the relevant path is set. Containers are reported so the host can name /
/// remux correctly.
/// </summary>
public sealed record SourceDownload
{
    public string? VideoFilePath { get; init; }
    public string? AudioFilePath { get; init; }
    public string? VideoContainer { get; init; }
    public string? AudioContainer { get; init; }
    public string? Resolution { get; init; }
}

/// <summary>Item metadata fetched on demand (never during search) for enrichment.</summary>
public sealed record SourceMetadata(
    TimeSpan? Duration,
    string? Description,
    IReadOnlyList<ChapterMarker> Chapters,
    DateTimeOffset? PublishedAt = null);
