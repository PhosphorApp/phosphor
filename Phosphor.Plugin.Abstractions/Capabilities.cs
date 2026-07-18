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
/// A set of structured search filters parsed from the query box (e.g. <c>min:</c>/<c>max:</c>
/// duration, <c>library:</c> scope). Complements the free-text <see cref="ITextSearchCapable"/>
/// path: sources that can push these filters down to their backend (see
/// <see cref="IFilterableSearch"/>) do so server-side; the host still applies any filter the
/// source didn't claim client-side. All members are optional — an all-null instance means "no
/// structured filters, plain text search".
/// </summary>
/// <param name="MinDuration">Lower bound on item length, or <c>null</c> for no minimum.</param>
/// <param name="MaxDuration">Upper bound on item length, or <c>null</c> for no maximum.</param>
/// <param name="Library">A library/section name to scope the search to, or <c>null</c> for all.</param>
public sealed record SearchFilters(
    TimeSpan? MinDuration = null,
    TimeSpan? MaxDuration = null,
    string? Library = null)
{
    /// <summary>True when at least one filter is set.</summary>
    public bool HasAny => MinDuration != null || MaxDuration != null || !string.IsNullOrWhiteSpace(Library);

    /// <summary>True when a duration bound (min and/or max) is set.</summary>
    public bool HasDuration => MinDuration != null || MaxDuration != null;
}

/// <summary>
/// Capability: free-text search with structured filters pushed down to the source's backend
/// (e.g. Plex filtering by <c>duration</c> and library section server-side, so large libraries
/// aren't scanned client-side). Complements <see cref="ITextSearchCapable"/>: a source implements
/// both, and the host prefers this overload when the parsed query carries filters. The result
/// reports which filters the source actually applied, so the host can apply the remainder itself.
/// </summary>
public interface IFilterableSearch
{
    /// <summary>
    /// Searches for <paramref name="query"/> with the given <paramref name="filters"/> applied at
    /// the source. Returns the (incrementally-yielded) matches plus the subset of
    /// <paramref name="filters"/> the source honored, so the host skips re-applying those and still
    /// applies any it didn't.
    /// </summary>
    FilteredSearchResult SearchFiltered(string query, SearchFilters filters, CancellationToken ct = default);
}

/// <summary>
/// Result of an <see cref="IFilterableSearch.SearchFiltered"/> call: the matching items and the
/// filters the source applied server-side.
/// </summary>
/// <param name="Items">The matching items, yielded incrementally.</param>
/// <param name="Applied">The subset of the requested filters the source honored at its backend.</param>
public sealed record FilteredSearchResult(
    IAsyncEnumerable<SourceItem> Items,
    SearchFilters Applied);

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
/// Marker capability: this source's <see cref="IPlayableResolver.ResolveAsync"/> is <em>expensive</em>
/// (e.g. it shells out to yt-dlp per item), so the host must NOT resolve streams eagerly while
/// building search/browse results. Instead it carries the <see cref="SourceItem"/> and resolves it
/// lazily at play time — the same deferral live streams get, but without live semantics (finite,
/// seekable). Sources with cheap, long-lived direct URLs (Plex, Jellyfin, local files) do NOT
/// implement this: eager resolution there gives an immediately playable URL. Implemented alongside
/// <see cref="IPlayableResolver"/>. YouTube uses the equivalent built-in deferral.
/// </summary>
public interface IDeferredStreamResolution
{
}

/// <summary>
/// Capability: the source can supply a stable, pre-loadable audio stream for an item ahead of
/// playback, enabling gapless transitions (the host primes the next track's stream before the
/// current one ends). Implemented by sources whose audio items carry a long-lived, direct stream
/// URL (e.g. Plex music tracks); sources that resolve short-lived URLs per play (e.g. YouTube)
/// do not implement it. Synchronous because a capable source already holds the URL — a future
/// source needing IO to obtain it could add an async overload.
/// </summary>
public interface IGaplessCapable
{
    /// <summary>
    /// Returns a stable, pre-loadable stream URL for <paramref name="item"/> suitable for gapless
    /// priming, or <c>null</c> if the item isn't eligible (e.g. not audio-only, or no direct URL).
    /// </summary>
    string? GetGaplessStreamUrl(SourceItem item);
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

/// <summary>
/// Capability: verify the source's configuration actually works (reachability + auth), beyond the
/// static <see cref="IPhosphorSource.IsConfigured"/> "fields are filled in" check. Implemented by
/// sources that talk to a server (e.g. Plex verifies the URL/token and counts libraries). Lets the
/// settings UI offer a "Test connection" button with a clear ✓/✗ result during setup.
/// </summary>
public interface IConnectionTestable
{
    /// <summary>
    /// Attempts a lightweight round-trip against the source's current settings and reports the
    /// outcome. Must not throw for expected failures (unreachable host, bad credentials) — return a
    /// failed <see cref="ConnectionTestResult"/> with a human-readable message instead.
    /// </summary>
    Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default);
}

/// <summary>Outcome of an <see cref="IConnectionTestable.TestConnectionAsync"/> attempt.</summary>
/// <param name="Success">True when the source reached its backend and authenticated.</param>
/// <param name="Message">A concise, user-facing status line (e.g. "Connected — 3 libraries" or "401 Unauthorized").</param>
/// <param name="Latency">Optional round-trip time, for display.</param>
public sealed record ConnectionTestResult(bool Success, string Message, TimeSpan? Latency = null);

/// <summary>
/// Capability: rescan the source's backing content and rebuild its searchable catalog/index — a
/// local-folder source re-walking its media directories, a Plex "Update Libraries", etc. This is
/// about the <em>content</em>, and is deliberately distinct from <see cref="IUpdatable"/> (which
/// updates the source's software/engine, not what it indexes). The host surfaces a "Rescan" action
/// with progress; the source owns the walk and the catalog it builds.
/// </summary>
public interface IRefreshable
{
    /// <summary>Whether a rescan can run right now (e.g. at least one folder configured/reachable).</summary>
    bool CanRefresh { get; }

    /// <summary>
    /// Rescans the backing content and rebuilds the catalog. Reports coarse progress for a UI bar.
    /// Must not throw for expected failures (missing folder, permission denied) — return a failed
    /// <see cref="RefreshResult"/> with a human-readable message instead.
    /// </summary>
    Task<RefreshResult> RefreshAsync(
        IProgress<RefreshProgress>? progress = null, CancellationToken ct = default);
}

/// <summary>Coarse progress for an <see cref="IRefreshable.RefreshAsync"/> pass.</summary>
/// <param name="Fraction">Completion in [0, 1], or a value &lt; 0 when indeterminate.</param>
/// <param name="CurrentItem">Optional label for what's being scanned (e.g. a file or folder name).</param>
public sealed record RefreshProgress(double Fraction, string? CurrentItem = null);

/// <summary>Outcome of an <see cref="IRefreshable.RefreshAsync"/> pass.</summary>
/// <param name="Success">True when the rescan completed and the catalog was rebuilt.</param>
/// <param name="ItemCount">Total items in the catalog after the rescan.</param>
/// <param name="Message">A concise, user-facing status line (e.g. "Scanned 1,204 files in 3 folders").</param>
public sealed record RefreshResult(bool Success, int ItemCount, string Message);

/// <summary>
/// Capability: search <em>within</em> a specific browse node the user is currently viewing (a Plex
/// library, a folder, a Jellyfin collection), rather than the whole source. Complements
/// <see cref="ITextSearchCapable"/> (which is source-wide). Returns a <see cref="BrowseResult"/> so
/// results can be a mix of drill-in sub-categories and playable leaf items — e.g. a Plex music
/// library search surfaces matching artists and albums as containers plus matching tracks as leaves.
/// </summary>
public interface IScopedSearchable
{
    /// <summary>
    /// Searches inside <paramref name="node"/> (the browse category currently open) for
    /// <paramref name="query"/>. The source owns how it interprets the scope + query (e.g. a Plex
    /// music library fans out across artist/album/track and merges the matches). Must not throw for
    /// expected failures — return an empty <see cref="BrowseResult"/> instead.
    /// </summary>
    Task<BrowseResult> SearchInCategoryAsync(SourceCategory node, string query, CancellationToken ct = default);
}

/// <summary>
/// Capability: the user can mark a source's items as favorites (e.g. pin a SiriusXM channel so it
/// floats to the top). Optional and generic — any source may adopt it (Plex albums, YouTube
/// channels, …). The host renders a star toggle on an item's row <em>only</em> when the owning
/// source implements this, and a source typically surfaces a "Favorites" node in its browse tree.
/// The source owns persistence (its own instance dir); the host just calls in.
/// </summary>
public interface IFavoritable
{
    /// <summary>True when <paramref name="itemId"/> (an <see cref="SourceItem.ItemId"/>) is favorited.</summary>
    bool IsFavorite(string itemId);

    /// <summary>Marks/unmarks <paramref name="itemId"/> as a favorite and persists the change.</summary>
    void SetFavorite(string itemId, bool favorite);

    /// <summary>The currently favorited item ids, for building a "Favorites" view.</summary>
    IReadOnlyCollection<string> GetFavoriteIds();

    /// <summary>
    /// Rebuilds a playable <see cref="SourceItem"/> (with its opaque <see cref="SourceItem.SourceState"/>)
    /// for a favorited <paramref name="itemId"/>, or <c>null</c> if the source can no longer produce it.
    /// Lets a host-level aggregated "Favorites" view — which stores only lightweight display records —
    /// hand playback back to the owning source without re-browsing. Cheap: sources build it from data
    /// they already hold (a cached channel, a stored favorite record, or just the id).
    /// </summary>
    SourceItem? GetFavorite(string itemId);
}

/// <summary>One item a source exposes for the host's hide-management UI.</summary>
/// <param name="Id">The item's stable id (an <see cref="SourceItem.ItemId"/>).</param>
/// <param name="Label">A user-facing label (e.g. "37 · Octane").</param>
/// <param name="Group">Optional top-level grouping (e.g. a super-group like "Music"). May be null.</param>
/// <param name="SubGroup">Optional second-level grouping (e.g. a category like "Country"). May be null.</param>
public sealed record HideableItem(string Id, string Label, string? Group = null, string? SubGroup = null);

/// <summary>
/// Capability: the user can hide specific items from a source's browse lists (e.g. suppress the
/// hundreds of SiriusXM sports-team channels). Optional and generic — any source may adopt it. The
/// host offers a "manage hidden items" affordance <em>only</em> when the source implements this, and
/// the source is responsible for persisting the hidden set and excluding hidden items from its own
/// browse results. Bulk <see cref="SetHidden"/> supports block/multi-select edits efficiently.
/// </summary>
public interface IHideable
{
    /// <summary>All items eligible to be hidden (the full set the manage-UI presents).</summary>
    IReadOnlyList<HideableItem> GetHideableItems();

    /// <summary>The ids currently hidden.</summary>
    IReadOnlyCollection<string> GetHiddenIds();

    /// <summary>Hides or unhides a batch of ids at once, and persists the change.</summary>
    void SetHidden(IReadOnlyCollection<string> itemIds, bool hidden);
}
