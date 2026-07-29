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
    /// True when this item plays as audio by default but a <em>video</em> version likely exists
    /// elsewhere (e.g. an iHeart "video podcast" episode whose audio the API serves directly, while
    /// the video lives on YouTube). The host may surface an optional "watch video" affordance that
    /// opportunistically resolves the video via <see cref="VideoSearchQuery"/>, falling back to this
    /// item's audio when no match is found. Purely additive — default playback is unaffected.
    /// </summary>
    public bool HasVideoAlternative { get; init; }

    /// <summary>
    /// A best-effort search query the host can run against its video source (YouTube) to find the
    /// video version when <see cref="HasVideoAlternative"/> is set (e.g. <c>"Show Name" Episode Title</c>).
    /// Null when the source can't supply one.
    /// </summary>
    public string? VideoSearchQuery { get; init; }

    /// <summary>
    /// Whether this item is actually playable. Defaults to <c>true</c>. A source may surface an item
    /// it knows it cannot resolve (e.g. a SoundCloud track it has previously seen fail with DRM) so
    /// the user still sees it in results — set this to <c>false</c> and the host renders the row as
    /// unplayable (action buttons removed, a "no entry" indicator shown) rather than hiding it.
    /// Containers (<see cref="IsContainer"/>) are always considered playable/openable.
    /// </summary>
    public bool IsPlayable { get; init; } = true;

    /// <summary>
    /// True when this item is a continuous <em>live</em> stream with no fixed duration or seekable
    /// timeline (e.g. a radio channel). Lets the host render it as a "tuner"-style entry and skip
    /// duration/progress affordances. Defaults to <c>false</c>.
    /// </summary>
    public bool IsLiveStream { get; init; }

    /// <summary>
    /// True when the host should decorate this item's thumbnail with a small "live" indicator (a red
    /// corner dot) to call out that it is a <em>currently-broadcasting</em> stream among finite items
    /// (e.g. a Twitch channel's live feed shown atop its VODs). Purely a display hint and deliberately
    /// distinct from <see cref="IsLiveStream"/>: a source whose items are <em>all</em> live (e.g. a
    /// radio service) should leave this <c>false</c> so it doesn't badge everything. Defaults to
    /// <c>false</c>. The source decides when to set it (and may gate it behind its own setting).
    /// </summary>
    public bool ShowLiveBadge { get; init; }

    /// <summary>
    /// True when the host should decorate this item's thumbnail with a small "unavailable" indicator
    /// (a ⊘ corner badge) because a previous play attempt failed. Unlike <see cref="IsPlayable"/> =
    /// <c>false</c> (which renders the row as permanently unplayable and removes its action buttons),
    /// this is a soft, <em>retryable</em> hint: the row stays fully playable so the user can try again,
    /// and the badge is cleared on the next successful play. Ideal for sources whose failures are often
    /// transient (e.g. IPTV streams that are geo-blocked or temporarily offline). Defaults to
    /// <c>false</c>. The source owns the decision and its persistence.
    /// </summary>
    public bool ShowUnavailableBadge { get; init; }

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
    /// hub keys, etc.). Opaque to the host. Held only for the lifetime of the in-memory item — it is
    /// NOT persisted, so do not rely on it surviving a restart (see <see cref="SourceStateToken"/>).
    /// </summary>
    public object? SourceState { get; init; }

    /// <summary>
    /// A <em>durable</em>, source-serialized handle to this item's private identity (e.g. Plex's rating
    /// key). Unlike <see cref="SourceState"/> — a live object that does not survive serialization — this
    /// is a plain string the host persists (e.g. in <c>queue.json</c>) and hands back verbatim on later
    /// per-item round-trips (e.g. <c>IPlayableResolver.GetMetadataAsync</c> for on-demand chapters), so a
    /// queued item still resolves its source identity after a restart. Opaque to the host; the source
    /// owns the format. Null when the source needs no durable identity beyond <see cref="ItemId"/>.
    /// </summary>
    public string? SourceStateToken { get; init; }
}

/// <summary>A single chapter marker within an item.</summary>
public sealed record ChapterMarker(string Title, TimeSpan Start, TimeSpan? End = null);
