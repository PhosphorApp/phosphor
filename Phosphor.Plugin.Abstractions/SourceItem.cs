namespace Phosphor.Plugin.Abstractions;

/// <summary>
/// A playable/browsable item produced by a source (a video, track, playlist row, etc.).
/// This is the plug-in-boundary shape; the host maps it onto its own UI/player model.
/// Source-private data (Plex rating keys, hub keys, YouTube ids, …) rides along in
/// <see cref="SourceState"/> and is opaque to the host — it is handed back to the source
/// on resolve/browse so the source never has to re-derive it.
/// </summary>
public sealed class SourceItem
{
    /// <summary>The <see cref="IPhosphorSource.InstanceId"/> that produced this item.</summary>
    public required string SourceInstanceId { get; init; }

    /// <summary>Stable id within the source. Opaque to the host.</summary>
    public required string ItemId { get; init; }

    public string Title { get; init; } = "";
    public string? Subtitle { get; init; }
    public string? ThumbnailUrl { get; init; }

    /// <summary>When true, this item has no video track (e.g. a music track).</summary>
    public bool IsAudioOnly { get; init; }

    /// <summary>
    /// True when this item is a continuous <em>live</em> stream with no fixed duration or seekable
    /// timeline (e.g. a radio channel). Lets the host render it as a "tuner"-style entry and skip
    /// duration/progress affordances. Defaults to <c>false</c>.
    /// </summary>
    public bool IsLiveStream { get; init; }

    /// <summary>
    /// When true, this item is a container the user drills into rather than plays
    /// (e.g. a Plex artist/album). The host calls <see cref="IBrowsable"/> to expand it.
    /// </summary>
    public bool IsContainer { get; init; }

    public TimeSpan? Duration { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>Native chapter markers, when the source exposes them.</summary>
    public IReadOnlyList<ChapterMarker>? Chapters { get; init; }

    /// <summary>
    /// Plug-in-private payload the host stores and hands back unchanged (rating keys,
    /// hub keys, etc.). Opaque to the host.
    /// </summary>
    public object? SourceState { get; init; }
}

/// <summary>A single chapter marker within an item.</summary>
public sealed record ChapterMarker(string Title, TimeSpan Start, TimeSpan? End = null);
