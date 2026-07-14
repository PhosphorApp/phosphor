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
/// Capability: paginated browsing for large, flat item lists (e.g. a Plex hub, library, or
/// playlist with thousands of tracks). Complements <see cref="IBrowsable"/>: sources that can
/// serve arbitrary offset windows implement this so the host can lazily "load more" as the user
/// scrolls, instead of fetching everything up front. Offset-based to match how most media servers
/// page (a start index + count), with a total-size so the host knows when it has reached the end.
/// </summary>
public interface IPagedBrowsable
{
    /// <summary>
    /// Returns the items in the window <c>[offset, offset + count)</c> for the given category,
    /// plus the total item count. Callers page by passing an increasing <paramref name="offset"/>
    /// (typically the number of items already loaded) until they have <c>TotalSize</c> items.
    /// </summary>
    Task<BrowsePage> BrowsePageAsync(
        SourceCategory category, int offset, int count, CancellationToken ct = default);
}

/// <summary>
/// Capability: discovery of a source's playlists and channels/uploads (a YouTube-shaped
/// surface, but open to any source with the same notions). Implemented alongside
/// <see cref="ITextSearchCapable"/> by sources that expose playlists addressable by
/// id/URL/name and channels addressable by handle/user.
/// </summary>
public interface IPlaylistChannelDiscovery
{
    /// <summary>
    /// Resolves a playlist id from a raw id, URL, or a name to search for. Returns the
    /// canonical id, or <c>null</c> if a name search found nothing.
    /// <paramref name="onFoundByName"/> is invoked with the matched playlist's title when
    /// resolution happened via name search (so the host can surface "Found playlist: X").
    /// </summary>
    Task<string?> ResolvePlaylistIdAsync(
        string nameIdOrUrl, Action<string>? onFoundByName = null, CancellationToken ct = default);

    /// <summary>Incrementally yields the items of a playlist (by resolved id).</summary>
    IAsyncEnumerable<SourceItem> GetPlaylistItemsAsync(string playlistId, CancellationToken ct = default);

    /// <summary>Incrementally yields a channel's uploads (by handle or user name).</summary>
    IAsyncEnumerable<SourceItem> GetChannelUploadsAsync(string handleOrUser, CancellationToken ct = default);
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
/// Capability: the source (or its underlying engine/tool) can report its version and update itself
/// at runtime. Implemented by sources backed by an updatable external tool (e.g. yt-dlp); in-process
/// libraries that ship compiled-in report <see cref="SupportsUpdate"/> = <c>false</c>. Lets the host
/// offer an "update now" action and an optional periodic auto-update without hard-coding a specific tool.
/// </summary>
public interface IUpdatable
{
    /// <summary>Whether an update can actually be performed right now (e.g. the yt-dlp engine is active).</summary>
    bool SupportsUpdate { get; }

    /// <summary>Returns the current version string (e.g. "2026.07.04"), or <c>null</c> if unavailable.</summary>
    Task<string?> GetVersionAsync(CancellationToken ct = default);

    /// <summary>Updates to the latest version. No-ops (reports already-current) when up to date.</summary>
    Task<UpdateResult> UpdateAsync(CancellationToken ct = default);
}

/// <summary>Outcome of an <see cref="IUpdatable.UpdateAsync"/> attempt.</summary>
public enum UpdateStatus
{
    AlreadyCurrent,
    Updated,
    Failed,
    NotSupported,
}

/// <summary>Result of an update attempt, with a ready-to-display status line.</summary>
public sealed record UpdateResult(
    UpdateStatus Status,
    string? OldVersion,
    string? NewVersion,
    string? Error)
{
    /// <summary>A concise, user-facing status line for the settings UI.</summary>
    public string DisplayString => Status switch
    {
        UpdateStatus.Updated => $"Updated {OldVersion} → {NewVersion}",
        UpdateStatus.AlreadyCurrent => $"Already current ({NewVersion ?? OldVersion ?? "unknown"})",
        UpdateStatus.NotSupported => "Update not supported by the active engine",
        _ => $"Update failed{(string.IsNullOrEmpty(Error) ? "" : $": {Error}")}",
    };
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
