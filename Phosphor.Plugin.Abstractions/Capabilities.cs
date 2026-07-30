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
/// Capability: a source-authored search-box hint. Implemented by sources that want to advertise
/// their query grammar (e.g. YouTube's <c>channel:</c>/<c>playlist:</c>, Plex's
/// <c>library:</c>/<c>min:</c>/<c>max:</c>) so the host can surface it beneath the search box
/// without hard-coding per-source strings. Sources that don't implement this simply show no hint.
/// </summary>
public interface ISearchHintProvider
{
    /// <summary>
    /// A short hint describing this source's query syntax, shown next to the search box. May be
    /// <c>null</c>/empty to show nothing.
    /// </summary>
    string? SearchHint { get; }
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
/// One user-defined saved-search category ("tile"): a display <see cref="Name"/>, a recommended
/// <see cref="Icon"/> glyph, and the <see cref="SearchTerm"/> the host runs (through its normal
/// query grammar) when the tile is opened. <see cref="Id"/> is stable so the host can key a
/// persisted per-source tile to it.
/// </summary>
/// <param name="Id">Stable id within the source (used to key the host tile).</param>
/// <param name="Name">Display name of the tile (e.g. "Rock").</param>
/// <param name="Icon">Recommended glyph; the host persists it and lets the user override it.</param>
/// <param name="SearchTerm">The search term run when the tile is opened (host query grammar).</param>
public sealed record SavedSearchCategory(string Id, string Name, string Icon, string SearchTerm);

/// <summary>
/// Capability: the source contributes user-defined <em>saved-search category tiles</em> to the home
/// screen. Unlike <see cref="IBrowsable"/> (a navigable tree), each category is just a stored search
/// term the host runs through its own query grammar (playlist:/channel:/min:/max:) bound to this
/// source. YouTube — the original baked-in provider whose genre tiles (Rock/Pop/…) were historically
/// host-owned — implements this so those tiles live with the plug-in. The host syncs them as
/// source-bound tiles (preserving user ordering/glyph/visibility) and opens one by running its
/// <see cref="SavedSearchCategory.SearchTerm"/> against this source.
/// </summary>
public interface ISavedSearchCategories
{
    /// <summary>The source's current saved-search categories, in the source's preferred order.</summary>
    IReadOnlyList<SavedSearchCategory> GetSavedSearchCategories();
}

/// <summary>
/// Capability: the user can edit the source's saved-search categories (add/rename/retarget/delete
/// and reorder). Extends <see cref="ISavedSearchCategories"/>: the host renders a row editor
/// (glyph / name / search-term per row) and hands the edited list back so the source persists it.
/// Since only the source knows its settings-blob shape, it translates the edited list into an
/// updated settings dictionary the host merges into the instance config.
/// </summary>
public interface IEditableSavedSearchCategories : ISavedSearchCategories
{
    /// <summary>
    /// Translates an edited category list into an updated settings blob (merged over
    /// <paramref name="currentSettings"/>). The source assigns/preserves stable ids and ordering.
    /// The host persists the returned dictionary as the instance's settings.
    /// </summary>
    IReadOnlyDictionary<string, string?> ApplySavedSearchCategories(
        IReadOnlyList<SavedSearchCategory> categories,
        IReadOnlyDictionary<string, string?> currentSettings);

    /// <summary>The plug-in's built-in default categories, for a "restore defaults" affordance.</summary>
    IReadOnlyList<SavedSearchCategory> GetDefaultSavedSearchCategories();
}

/// <summary>
/// A source's advice on how the host should cache its <em>result pages</em> (the paged
/// search/browse results behind category tiles and live playlists), independent of the raw-stream
/// disk cache governed by <see cref="IDownloadable"/>. Lets a source say "my results are stable,
/// cache them" (YouTube, Plex) or "my results are ephemeral, don't" (a live/Twitch-style feed).
/// </summary>
/// <param name="Cache">Whether the host should cache this source's result pages.</param>
/// <param name="MaxAgeHours">
/// Max age of a cached page before it's considered stale, when <paramref name="Cache"/> is true.
/// <c>null</c> means "use the host default". Ignored when <paramref name="Cache"/> is false.
/// </param>
public sealed record ResultCachePolicy(bool Cache, int? MaxAgeHours = null);

/// <summary>
/// Capability: the source advises the host on caching its result pages (see
/// <see cref="ResultCachePolicy"/>). Optional — a source that doesn't implement this gets the host's
/// default result-cache behavior. Note this governs only the host-managed result cache; a source is
/// always free to micro-manage its own internal caching invisibly (and surface any knobs via its own
/// settings) without implementing this.
/// </summary>
public interface IResultCachePolicy
{
    /// <summary>How the host should cache this source's result pages.</summary>
    ResultCachePolicy GetResultCachePolicy();
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
    /// The scope MUST be resolvable from <see cref="SourceCategory.CategoryId"/> alone (durable),
    /// since the host may replay a persisted scope (e.g. a saved live playlist) whose
    /// <see cref="SourceCategory.SourceState"/> is <c>null</c>.
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

/// <summary>
/// Optional capability: rebuild a fully-playable <see cref="SourceItem"/> from just a persisted
/// <see cref="SourceItem.ItemId"/>, with no prior in-memory browse state. The host uses this to
/// re-resolve items whose opaque <see cref="SourceItem.SourceState"/> did not survive being persisted
/// (e.g. a live-stream queue entry restored from disk after a restart). Unlike
/// <see cref="IFavoritable.GetFavorite"/> this is not limited to favorited items — any id the source
/// recognizes can be rebuilt. Cheap by design: the source constructs it from the id alone (e.g. a
/// Twitch live row's id is the channel login, so it rebuilds the channel's <em>current</em> live feed).
/// Returns <c>null</c> when the source can no longer produce the item (e.g. the channel is offline).
/// </summary>
public interface IReplayableById
{
    /// <summary>
    /// Rebuilds a playable <see cref="SourceItem"/> for <paramref name="itemId"/>, or <c>null</c> if
    /// the source can no longer produce it. For live sources this reflects what is live <em>now</em>,
    /// not whatever was live when the id was first captured.
    /// </summary>
    SourceItem? RebuildPlayable(string itemId);
}

/// <summary>How the host should treat a "Play all" action on a container.</summary>
public enum ContainerPlayAll
{
    /// <summary>Queue every leaf and play from the first — the default (e.g. an album played whole).</summary>
    QueueAll,

    /// <summary>
    /// Queue and play only the <em>first</em> leaf — the right behavior for a recency feed rather than
    /// a curated set (e.g. a Twitch channel: play what's live now, else the most recent VOD, not the
    /// entire back-catalog).
    /// </summary>
    PlayLatestOnly,

    /// <summary>
    /// The container has no meaningful "Play all" — it is a pure grouping/navigation node whose
    /// children are themselves containers (e.g. a Podcast Index <em>category</em> of shows, or a
    /// Twitch <em>game/category</em> of channels). The host offers browse/drill-in only and hides the
    /// play affordance, so the user can't accidentally play one arbitrary leaf from deep in the tree.
    /// </summary>
    None,
}

/// <summary>
/// Optional capability: a source declares how "Play all" should behave for its containers, letting a
/// recency feed (e.g. a Twitch channel) play only the latest item instead of queueing everything.
/// Sources that don't implement this default to <see cref="ContainerPlayAll.QueueAll"/> — the
/// album-style "queue the whole thing" behavior — so existing sources are unaffected.
/// </summary>
public interface IContainerPlayPolicy
{
    /// <summary>Returns the "Play all" behavior for <paramref name="container"/>.</summary>
    ContainerPlayAll GetPlayAllBehavior(SourceItem container);

    /// <summary>
    /// Optional short verb label for the container's play button/tooltip (e.g. "Play latest"), or
    /// <c>null</c> to use the host default ("Play all"). Lets the affordance read honestly when the
    /// behavior isn't "queue everything".
    /// </summary>
    string? PlayAllLabel(SourceItem container) => null;
}

/// <summary>
/// A snapshot of a favorited item the host hands to a source at star-time, so the source can persist
/// enough to rebuild it later via <see cref="IFavoritable.GetFavorite"/> — including
/// <see cref="IsContainer"/> artists/albums (whose <see cref="ContainerState"/> carries the opaque
/// browse node). Sources that resolve by id need little; those needing more keep the whole record.
/// </summary>
/// <param name="ItemId">The source-native id (an <see cref="SourceItem.ItemId"/>).</param>
/// <param name="Title">Display title.</param>
/// <param name="Subtitle">Optional subtitle (e.g. album artist).</param>
/// <param name="ThumbnailUrl">Optional artwork URL.</param>
/// <param name="Duration">Optional duration (leaves only).</param>
/// <param name="IsAudioOnly">Whether the item is audio-only.</param>
/// <param name="IsContainer">Whether this is a container (artist/album) that expands to tracks.</param>
/// <param name="ContainerState">For containers, the opaque browse node (a <see cref="SourceCategory.SourceState"/>).</param>
public sealed record FavoriteCapture(
    string ItemId,
    string Title,
    string? Subtitle,
    string? ThumbnailUrl,
    TimeSpan? Duration,
    bool IsAudioOnly,
    bool IsContainer,
    object? ContainerState);

/// <summary>
/// Optional companion to <see cref="IFavoritable"/>: the host calls <see cref="RememberFavorite"/> at
/// star-time with a <see cref="FavoriteCapture"/> so the source can persist display + node data and
/// later rebuild the item in <see cref="IFavoritable.GetFavorite"/> — the clean way to support
/// favoriting containers (artist/album). Sources that can rebuild purely from an id need not implement
/// this; those that need display/container state do.
/// </summary>
public interface IFavoriteCapture
{
    void RememberFavorite(FavoriteCapture item);
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

/// <summary>
/// Marker capability: this source is <em>experimental</em> — it may be incomplete, unreliable, or
/// have significant known limitations (e.g. SoundCloud, where much major-label content is DRM-locked
/// and unplayable). Implemented on the <see cref="IPhosphorSourceProvider"/> (the type, not the
/// instance). The host surfaces an "Experimental" badge/warning next to the source in the Plug-ins
/// settings tab so users know what to expect. Purely advisory — it changes no behavior.
/// </summary>
public interface IExperimental
{
}

/// <summary>
/// Why a playback attempt failed, so a source told about the failure can decide whether to remember
/// it. The distinction is deliberate: only a <see cref="Unresolvable"/> failure is safe to persist as
/// "this item can't be played" — a <see cref="Transient"/> failure (network blip, timeout, temporary
/// outage) must NOT mark an item permanently unplayable.
/// </summary>
public enum PlaybackFailureKind
{
    /// <summary>
    /// A definitive, item-intrinsic failure: the item cannot be resolved to a playable stream no
    /// matter how many times we retry (e.g. DRM-protected, removed, region-locked with no stream).
    /// Safe for a source to persist as permanently unplayable.
    /// </summary>
    Unresolvable,

    /// <summary>
    /// A transient/environmental failure (network error, timeout, temporary server outage). The item
    /// may well play later — a source must NOT mark it permanently unplayable on this.
    /// </summary>
    Transient,
}

/// <summary>
/// Capability: the host can report back to a source that one of its items failed to play, so the
/// source can learn from it (e.g. persist a "known unplayable" set and surface those rows as
/// unplayable on future search/browse). Optional — implemented by sources that benefit from
/// remembering failures (e.g. SoundCloud's lazy DRM discovery). The <see cref="PlaybackFailureKind"/>
/// tells the source whether the failure is safe to persist (<see cref="PlaybackFailureKind.Unresolvable"/>)
/// or transient and must be ignored for persistence. The source owns the decision and its storage;
/// the host just informs it.
/// </summary>
public interface IPlaybackReportable
{
    /// <summary>
    /// Informs the source that <paramref name="itemId"/> (a <see cref="SourceItem.ItemId"/>) failed to
    /// play. The source decides what to do with it based on <paramref name="kind"/> — typically
    /// persisting only <see cref="PlaybackFailureKind.Unresolvable"/> failures — and returns whether it
    /// now considers the item <em>permanently unplayable</em>. The host uses the return value to flip
    /// the live row to its unplayable state (only when <c>true</c>), so the source stays the authority
    /// on definitiveness (it, not the host, saw why the resolve failed). Must not throw.
    /// </summary>
    /// <returns><c>true</c> if the item is now known-unplayable and the row should render as such.</returns>
    bool ReportPlaybackFailure(string itemId, PlaybackFailureKind kind);
}

/// <summary>
/// Capability: the host can report back to a source that one of its items <em>started playing
/// successfully</em>. The companion to <see cref="IPlaybackReportable"/>: a source that remembers
/// soft/retryable failures (e.g. an IPTV channel that was geo-blocked or offline, badged with
/// <see cref="SourceItem.ShowUnavailableBadge"/>) uses this to <em>clear</em> that state once the item
/// plays again, so the badge is self-healing. Optional and additive — sources whose failures are
/// permanent (e.g. SoundCloud DRM) need not implement it. The source returns whether the item's
/// display state changed (e.g. an "unavailable" badge was cleared) so the host can refresh the live row.
/// </summary>
public interface IPlaybackSuccessReportable
{
    /// <summary>
    /// Informs the source that <paramref name="itemId"/> (a <see cref="SourceItem.ItemId"/>) played
    /// successfully. The source clears any remembered soft-failure state for it. Must not throw.
    /// </summary>
    /// <returns><c>true</c> if the item's display state changed (e.g. a badge was cleared) and the row should refresh.</returns>
    bool ReportPlaybackSuccess(string itemId);
}

/// <summary>
/// Capability: the host tells a source when playback of one of its items <em>stops</em>, so the
/// source can release any server-side or hardware resources it opened while resolving that item.
/// Most sources need nothing here — a resolved HTTP URL is stateless and the server frees it when the
/// client disconnects. But some sources hold a live, stateful resource that must be <em>explicitly</em>
/// torn down: e.g. Plex Live TV opens a tuner-holding transcode session that keeps a physical tuner
/// busy until it is stopped. Think of this as an <see cref="System.IDisposable"/> scoped to a single
/// playing item: the host calls it exactly once when the item stops or is replaced by another.
/// </summary>
/// <remarks>
/// The host invokes <see cref="ReleasePlayback"/> for the <em>outgoing</em> item whenever the
/// currently-playing item changes (stop, skip, track transition) or the app shuts down. It is
/// best-effort and fire-and-forget from the host's side; implementations must not throw and should
/// return quickly (do any slow network teardown without blocking the caller). A source that opens
/// nothing stateful should not implement this interface.
/// </remarks>
public interface IPlaybackStoppable
{
    /// <summary>
    /// Informs the source that playback of <paramref name="itemId"/> (a <see cref="SourceItem.ItemId"/>)
    /// has stopped and any resources held for it should be released. Called at most once per play, for
    /// the outgoing item, on stop / skip / track-change / shutdown. Must not throw.
    /// </summary>
    void ReleasePlayback(string itemId);
}
