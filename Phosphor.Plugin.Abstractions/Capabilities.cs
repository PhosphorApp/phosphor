namespace Phosphor.Plugin.Abstractions;

/// <summary>
/// Capability: free-text search. Implemented by sources that can answer a query string
/// (YouTube, Plex per-library). Results are yielded incrementally to preserve live
/// pagination in the UI. The source owns the mapping from its native result type to
/// <see cref="SourceItem"/>.
/// </summary>
public interface ITextSearchCapable
{
    IAsyncEnumerable<SourceItem> SearchAsync(string query, CancellationToken ct = default);
}

/// <summary>
/// Capability: hierarchical browsing. Implemented by sources with a navigable tree
/// (Plex libraries → artists → albums → tracks; a local-folder source; …). The host asks
/// for root categories, then expands one node at a time.
/// </summary>
public interface IBrowsable
{
    /// <summary>The top-level categories for this source.</summary>
    IAsyncEnumerable<SourceCategory> GetRootCategoriesAsync(CancellationToken ct = default);

    /// <summary>Expands one category into its sub-categories and/or leaf items.</summary>
    Task<BrowseResult> BrowseAsync(SourceCategory category, CancellationToken ct = default);
}

/// <summary>
/// Capability: resolve an item into a playable stream for live playback. The returned
/// <see cref="ResolvedStream"/> may be HTTP, a file path, or another transport
/// (see <see cref="StreamTransport"/>), so the host stays agnostic to where media lives.
/// HTTP URLs are typically short-lived — callers resolve fresh per play and must not persist.
/// </summary>
public interface IPlayableResolver
{
    Task<ResolvedStream?> ResolveAsync(SourceItem item, PlaybackPreferences prefs, CancellationToken ct = default);

    /// <summary>
    /// Fetches on-demand metadata (duration, description, native chapters). Called on play /
    /// enrichment, never during search. Returns <c>null</c> if the lookup failed.
    /// </summary>
    Task<SourceMetadata?> GetMetadataAsync(SourceItem item, CancellationToken ct = default);
}

/// <summary>
/// Capability: download raw streams into the host's disk cache. Optional — a source that
/// only supports live playback need not implement it. The source writes raw files into
/// <c>destinationDir</c> and reports their paths/containers; the host muxes/indexes/evicts.
/// </summary>
public interface IDownloadable
{
    Task<SourceDownload?> DownloadAsync(
        SourceItem item,
        PlaybackPreferences prefs,
        string destinationDir,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}
