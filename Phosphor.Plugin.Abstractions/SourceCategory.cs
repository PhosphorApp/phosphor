namespace Phosphor.Plugin.Abstractions;

/// <summary>
/// A node in a source's browse tree (a Plex library/hub/artist/album, a YouTube
/// playlist/channel, a local folder, …). The host renders these as navigable tiles and
/// calls <see cref="IBrowsable.BrowseAsync"/> to expand one.
/// </summary>
public sealed class SourceCategory
{
    /// <summary>The <see cref="IPhosphorSource.InstanceId"/> that produced this node.</summary>
    public required string SourceInstanceId { get; init; }

    /// <summary>
    /// Stable, DURABLE id within the source — opaque to the host but persistable. The host may store
    /// this id (e.g. a saved live playlist bound to a browse scope) and hand it back later in a
    /// reconstructed <see cref="SourceCategory"/> whose <see cref="SourceState"/> is <c>null</c>.
    /// A source MUST be able to act on a node from its <see cref="CategoryId"/> alone (browse it, or
    /// scope-search within it); <see cref="SourceState"/> is only an in-memory optimization, never
    /// the sole source of truth. Encode into the id whatever is needed to reconstruct the node.
    /// </summary>
    public required string CategoryId { get; init; }

    public string Title { get; init; } = "";
    public string? ThumbnailUrl { get; init; }

    /// <summary>
    /// Optional glyph/emoji the host shows on this node's tile (e.g. "🟠", "📡"). Lets a source theme
    /// its own tiles; the host falls back to a default when null/empty.
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>
    /// True when this node has further sub-categories (vs. only leaf items). The host may
    /// use this to decide whether to keep drilling. When unknown, leave false and let the
    /// browse result speak for itself.
    /// </summary>
    public bool HasSubCategories { get; init; }

    /// <summary>
    /// Plug-in-private payload handed back on browse, opaque to the host. An in-memory OPTIMIZATION
    /// only: it is not persisted and may be <c>null</c> when the host reconstructs a node from a
    /// stored <see cref="CategoryId"/>. A source must not depend on it being present — see
    /// <see cref="CategoryId"/>.
    /// </summary>
    public object? SourceState { get; init; }
}

/// <summary>
/// The result of expanding a <see cref="SourceCategory"/>: any nested categories plus any
/// leaf items at this level. Either list may be empty.
/// </summary>
public sealed class BrowseResult
{
    public IReadOnlyList<SourceCategory> Categories { get; init; } = [];
    public IReadOnlyList<SourceItem> Items { get; init; } = [];
}

/// <summary>
/// One page of a paginated browse (<see cref="IPagedBrowsable"/>). <see cref="Items"/> are the
/// leaf items for the requested offset window; <see cref="TotalSize"/> is the full count so the
/// caller can decide whether more pages remain (<c>offset + Items.Count &lt; TotalSize</c>).
/// Paged browse yields items only — hierarchical sub-categories use the single-shot
/// <see cref="IBrowsable.BrowseAsync"/> path.
/// </summary>
public sealed class BrowsePage
{
    public IReadOnlyList<SourceItem> Items { get; init; } = [];
    public int TotalSize { get; init; }
}
