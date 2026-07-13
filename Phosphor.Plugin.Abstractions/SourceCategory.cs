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

    /// <summary>Stable id within the source. Opaque to the host.</summary>
    public required string CategoryId { get; init; }

    public string Title { get; init; } = "";
    public string? ThumbnailUrl { get; init; }

    /// <summary>
    /// True when this node has further sub-categories (vs. only leaf items). The host may
    /// use this to decide whether to keep drilling. When unknown, leave false and let the
    /// browse result speak for itself.
    /// </summary>
    public bool HasSubCategories { get; init; }

    /// <summary>Plug-in-private payload handed back on browse. Opaque to the host.</summary>
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
