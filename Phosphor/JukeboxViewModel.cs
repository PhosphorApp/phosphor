using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Phosphor.Video;

namespace Phosphor;

public partial class JukeboxViewModel : ObservableObject
{
    private SearchEngineKind _searchEngineKind = SearchEngineKind.YoutubeExplode;
    private readonly PlayHistory _history;
    private readonly PlaylistManager _playlists;
    private readonly FavoritesIndex _favoritesIndex = new();
    private readonly SearchHistory _searchHistory;
    private VideoCache? _cache;
    private PrefetchCache? _prefetch;

    // ── Plug-in sources (the source path — YouTube and Plex run through the registry) ──
    private Phosphor.Plugins.SourceRegistry? _sourceRegistry;
    private bool _pluginsDiscovered;

    // Tracks the in-flight registry build so callers that depend on registry-derived wiring (e.g. the
    // cache's DownloadOverride, which is set inside BuildSourceRegistryAsync) can await it instead of
    // racing it at startup. Completed by default so awaiting is a no-op once the registry is built.
    // BuildSourceRegistryAsync is fire-and-forget from App startup, so the first cacheable play can
    // otherwise arrive before DownloadOverride is wired and be silently dropped.
    private Task _sourceRegistryReady = Task.CompletedTask;
    // Pre-fetched root-category tiles for generic IBrowsable plug-in sources (local-folder, future
    // Jellyfin, …), keyed by instance id. Built after each registry build; read by RebuildCategories.
    private readonly List<Category> _pluginBrowseTiles = new();

    // Tracks the in-flight background fetch that populates _pluginBrowseTiles with live SourceState.
    // Tiles render instantly from persisted categories.json; a drill-in that happens before this
    // completes awaits it (see BrowsePluginCategoryAsync). Completed task by default so awaiting is a
    // no-op when no fetch is in progress.
    private Task _pluginTilesReady = Task.CompletedTask;

    // ── Search source selection (the ad-hoc search box only; tiles stay source-bound) ──
    /// <summary>Searchable sources for the search-box dropdown (rebuilt after each registry build).</summary>
    public ObservableCollection<SearchSourceOption> SearchSources { get; } = new();

    private string? _activeSearchSourceId;
    /// <summary>
    /// The instance id the search box targets. Defaults to YouTube. Only steers the ad-hoc search
    /// box — genre/live-playlist tiles and AutoDJ are source-bound and ignore this.
    /// </summary>
    public string? ActiveSearchSourceId
    {
        get => _activeSearchSourceId;
        set => SetProperty(ref _activeSearchSourceId, value);
    }

    /// <summary>
    /// The active search source's self-authored query-syntax hint (see
    /// <see cref="Phosphor.Plugin.Abstractions.ISearchHintProvider"/>), or <c>null</c> if the source
    /// advertises none. Lets the UI surface source-specific hints without hard-coding per-source
    /// strings or type-id comparisons.
    /// </summary>
    public string? ActiveSearchSourceHint =>
        (_activeSearchSourceId != null ? _sourceRegistry?.ByInstance(_activeSearchSourceId) : _sourceRegistry?.YouTube)
            is Phosphor.Plugin.Abstractions.ISearchHintProvider hinter
            ? hinter.SearchHint
            : null;

    /// <summary>
    /// The source AutoDJ uses to find/queue similar tracks (from settings). <c>null</c>/empty =
    /// YouTube. A stop-gap steering knob until a richer AutoDJ model exists.
    /// </summary>
    public string? AutoDjProviderId { get; set; }

    /// <summary>
    /// Read-only summaries of the configured plug-in sources (for the Plug-ins settings tab).
    /// Empty when the registry hasn't been built.
    /// </summary>
    public IReadOnlyList<Phosphor.Plugins.SourceSummary> DescribePluginSources() =>
        _sourceRegistry?.DescribeSources() ?? [];

    /// <summary>
    /// Whether a loaded source currently exposes an updatable engine tool (e.g. yt-dlp). Reflects
    /// live registry state so the app-level "Update now" control can enable/disable accurately.
    /// </summary>
    public bool IsEngineToolUpdatable =>
        _sourceRegistry?
            .WithCapability<Phosphor.Plugin.Abstractions.IUpdatable>()
            .Any(u => u.SupportsUpdate) ?? false;

    /// <summary>
    /// Raised after <see cref="BuildSourceRegistryAsync"/> finishes rebuilding the source registry,
    /// so UI (e.g. the Settings window's yt-dlp strip) can refresh state that depends on which
    /// sources are currently loaded. Marshal to the UI thread in the handler as needed.
    /// </summary>
    public event Action? SourceRegistryRebuilt;

    /// <summary>
    /// Updates the shared engine tool (yt-dlp) and returns a user-facing status line. yt-dlp is an
    /// app-wide dependency any source may use, so this routes through the first loaded source that
    /// exposes the generic <c>IUpdatable</c> capability with <c>SupportsUpdate == true</c> — not the
    /// YouTube source specifically. Returns a not-supported message when no such source is loaded.
    /// </summary>
    public async Task<string> UpdateEngineToolAsync(CancellationToken ct = default)
    {
        var updatable = _sourceRegistry?
            .WithCapability<Phosphor.Plugin.Abstractions.IUpdatable>()
            .FirstOrDefault(u => u.SupportsUpdate);
        if (updatable != null)
        {
            var result = await updatable.UpdateAsync(ct);
            return result.DisplayString;
        }

        return "Update not supported by the active engine";
    }

    /// <summary>
    /// Builds (or rebuilds) the plug-in <see cref="Phosphor.Plugins.SourceRegistry"/> from the
    /// given settings. The registry is the source path for YouTube and Plex discovery/playback.
    /// </summary>
    public async Task BuildSourceRegistryAsync(AppSettings settings)
    {
        // Publish a fresh "registry ready" gate so cache/play paths that depend on registry-derived
        // wiring (DownloadOverride) can await THIS build rather than racing it. Completed in the
        // finally below regardless of success/failure so awaiters never hang.
        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sourceRegistryReady = readyTcs.Task;
        try
        {
        // Discover third-party plug-ins from the plugins/ folder once per app run (built-in type ids
        // are reserved so a plug-in can't shadow YouTube/Plex). Cheap to guard; the scan touches disk.
        if (!_pluginsDiscovered)
        {
            // No reserved type ids — YouTube and Plex are now discovered plug-ins like the rest.
            Phosphor.Plugins.DiscoveredProviders.Initialize(System.Array.Empty<string>());
            _pluginsDiscovered = true;
        }

        // Dispose the previous registry so its sources release any connections/watchers/timers
        // before we replace them (this method runs on every settings save).
        var previous = _sourceRegistry;

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(NetworkTimeoutSeconds) };
        var registry = new Phosphor.Plugins.SourceRegistry(http);
        try
        {
            // The persisted PluginInstances list is the source of truth for the plug-in path and is
            // edited via the Plug-ins settings tab. Seed it once from the flat Plex/engine fields for
            // first run / older settings files (a one-time migration); thereafter user edits persist.
            if (settings.PluginInstances.Count == 0)
                settings.PluginInstances = Phosphor.Plugins.PluginSettingsFactory.FromAppSettings(settings);

            await registry.BuildAsync(settings.PluginInstances);
            _sourceRegistry = registry;
            DebugLog.Log(LogLevel.Info, "SourceRegistry", $"Built {registry.Sources.Count} source(s)");
            WireCacheDownloadOverride();

            // Render tiles immediately from the persisted categories.json entries (no network wait),
            // then kick off the background fetch that recovers each tile's live SourceState and
            // reconciles new/removed tiles when it completes.
            await RunOnUiAsync(RebuildCategories);
            BuildPluginBrowseTiles(registry);
            BuildSearchSources(registry);
        }
        catch (Exception ex)
        {
            DebugLog.LogException("SourceRegistry build", ex);
            _sourceRegistry = null;
        }

        if (previous != null)
        {
            try { await previous.DisposeAsync(); }
            catch (Exception ex) { DebugLog.LogException("SourceRegistry dispose (previous)", ex); }
        }

        try { SourceRegistryRebuilt?.Invoke(); }
        catch (Exception ex) { DebugLog.LogException("SourceRegistryRebuilt handlers", ex); }
        }
        finally
        {
            readyTcs.TrySetResult();
        }
    }

    /// <summary>
    /// Kicks off the background fetch of root-category tiles for every generic <c>IBrowsable</c>
    /// plug-in source (local-folder, Jellyfin, Emby, …), storing the in-flight task in
    /// <see cref="_pluginTilesReady"/>. Returns immediately so the home screen renders from the
    /// persisted categories.json tiles WITHOUT waiting on the network — the fetch reconciles live
    /// tiles (recovering each tile's opaque <c>SourceState</c>) when it completes. A drill-in that
    /// happens before completion awaits <see cref="_pluginTilesReady"/> (see
    /// <see cref="BrowsePluginCategoryAsync"/>). Built-in YouTube is skipped (not browsable). Failures
    /// per source are logged and skipped so one bad plug-in never blocks the home screen.
    /// </summary>
    private void BuildPluginBrowseTiles(Phosphor.Plugins.SourceRegistry registry)
    {
        _pluginTilesReady = FetchAndReconcilePluginTilesAsync(registry);
    }

    /// <summary>
    /// The background body behind <see cref="BuildPluginBrowseTiles"/>: fetches each browsable
    /// source's root categories in parallel, populates <see cref="_pluginBrowseTiles"/> with live
    /// <c>SourceState</c>, syncs the tiles into the persisted genre entries, and rebuilds the category
    /// list so tiles pick up their live state.
    /// </summary>
    private async Task FetchAndReconcilePluginTilesAsync(Phosphor.Plugins.SourceRegistry registry)
    {
        // Each browsable source's GetRootCategoriesAsync does a network round-trip (auth + list
        // libraries) for media servers. Running them sequentially makes startup wait for the SUM of
        // every server's latency (Plex + Emby + Jellyfin + …). Fetch all sources in PARALLEL instead,
        // so total time ≈ the slowest single source. Results are reassembled in registry order below
        // so tile ordering stays deterministic regardless of which server responds first.
        var browsableSources = registry.Sources
            .Where(s => s.TypeId != Phosphor.Plugins.KnownSourceTypeIds.YouTube
                        && s is Phosphor.Plugin.Abstractions.IBrowsable)
            .ToList();

        async Task<List<Category>> FetchTilesAsync(Phosphor.Plugin.Abstractions.IPhosphorSource source)
        {
            var sourceTilesList = new List<Category>();
            try
            {
                var browsable = (Phosphor.Plugin.Abstractions.IBrowsable)source;
                await foreach (var cat in browsable.GetRootCategoriesAsync())
                {
                    sourceTilesList.Add(new Category
                    {
                        Name = cat.Title,
                        Icon = string.IsNullOrWhiteSpace(cat.Icon) ? "📁" : cat.Icon!,
                        IsPluginBrowse = true,
                        SourceInstanceId = source.InstanceId,
                        SourceCategoryId = cat.CategoryId,
                        SourceState = cat.SourceState,
                    });
                }
            }
            catch (Exception ex)
            {
                DebugLog.LogException($"Plugin browse tiles '{source.InstanceId}'", ex);
            }
            return sourceTilesList;
        }

        var perSourceResults = await Task.WhenAll(browsableSources.Select(FetchTilesAsync));

        // Flatten in source order for deterministic tile ordering.
        var tiles = new List<Category>();
        foreach (var result in perSourceResults)
            tiles.AddRange(result);

        _pluginBrowseTiles.Clear();
        _pluginBrowseTiles.AddRange(tiles);
        DebugLog.Log(LogLevel.Info, "SourceRegistry", $"Built {tiles.Count} plug-in browse tile(s).");

        // Sync these root tiles into the persisted genre-category entries so they participate in the
        // unified sort/visibility model (like Plex tiles). Prunes stale, preserves user customization.
        var sourceTiles = tiles
            .Select(t => new GenreCategoryStore.SourceTile(
                t.SourceInstanceId!, t.SourceCategoryId ?? t.Name, t.Name, t.Icon,
                registry.ByInstance(t.SourceInstanceId!)?.TypeId ?? ""))
            .ToList();
        GenreCategoryStore.SyncSourceTiles(_genreCategories, sourceTiles);

        // Sync saved-search source tiles (YouTube genre tiles) from every ISavedSearchCategories
        // source. Unlike browse tiles these carry a stored search term; the plug-in owns
        // name/icon/term, the host owns order/visibility.
        var savedSearchTiles = new List<GenreCategoryStore.SavedSearchTile>();
        foreach (var src in registry.Sources.OfType<Phosphor.Plugin.Abstractions.ISavedSearchCategories>())
        {
            var owner = (Phosphor.Plugin.Abstractions.IPhosphorSource)src;
            foreach (var c in src.GetSavedSearchCategories())
                savedSearchTiles.Add(new GenreCategoryStore.SavedSearchTile(
                    owner.InstanceId, c.Id, c.Name, c.Icon, c.SearchTerm, owner.TypeId));
        }
        GenreCategoryStore.SyncSavedSearchTiles(_genreCategories, savedSearchTiles);

        GenreCategoryStore.SaveInBackground(_genreCategories);

        // Tiles were synced into the genre entries — rebuild so they appear (sorted/hideable).
        await RunOnUiAsync(RebuildCategories);
    }

    /// <summary>
    /// Rebuilds the search-box source list from every <c>ITextSearchCapable</c> source, keeping
    /// YouTube first as the default. Preserves the current selection if it still resolves; otherwise
    /// falls back to YouTube. UI-bound collection, so mutate on the dispatcher.
    /// </summary>
    private void BuildSearchSources(Phosphor.Plugins.SourceRegistry registry)
    {
        var options = registry.Sources
            .Where(s => s is Phosphor.Plugin.Abstractions.ITextSearchCapable)
            .Select(s => new SearchSourceOption(s.InstanceId, s.DisplayName))
            // YouTube first (the default), then the rest by display name.
            .OrderByDescending(o => o.InstanceId == Phosphor.Plugins.KnownSourceTypeIds.YouTube)
            .ThenBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        void Apply()
        {
            SearchSources.Clear();
            foreach (var o in options) SearchSources.Add(o);

            // Keep the current selection if still valid; else default to YouTube (or first available).
            if (_activeSearchSourceId == null || options.All(o => o.InstanceId != _activeSearchSourceId))
                ActiveSearchSourceId = options.FirstOrDefault()?.InstanceId;
        }

        _ = RunOnUiAsync(Apply);
    }

    /// <summary>
    /// Expands a generic plug-in browse tile by calling its source's <c>IBrowsable.BrowseAsync</c>
    /// and rendering the returned items in the results list. Leaf items carry a resolved playable
    /// <c>StreamUrl</c> (via the source's <c>IPlayableResolver</c>) so the existing player path plays
    /// them directly — no source-specific playback wiring needed.
    /// </summary>
    /// <summary>
    /// Enters a generic plug-in browse from a home-screen tile: resets the navigation stack to this
    /// root node and renders it. Sub-categories become drill-in container items; leaf items play.
    /// </summary>
    private async Task BrowsePluginCategoryAsync(Category category)
    {
        _browseStack.Clear();

        // The tile may have been rendered from persisted categories.json before the background fetch
        // recovered its opaque SourceState (see BuildPluginBrowseTiles). If so, wait for that fetch —
        // showing the center-DMD loading indicator — then recover the live SourceState. This keeps
        // drill-in correct (e.g. Emby music roots carry a MusicLevel in SourceState that selects the
        // entity-browse path; without it the browse would degrade to a raw folder listing).
        var sourceState = category.SourceState;
        if (sourceState == null && !_pluginTilesReady.IsCompleted)
        {
            IsSearching = true;
            StatusText = $"Loading {category.Name}...";
            try
            {
                await _pluginTilesReady;
            }
            finally
            {
                IsSearching = false;
            }
        }

        // Re-resolve the live SourceState from the (now-populated) tile list when the tile came in
        // without it. If it's still null (source removed/unreachable), fall back to browsing from the
        // persisted CategoryId — sources resolve a root node from its id alone (state?.ItemId ?? id).
        if (sourceState == null)
        {
            var live = _pluginBrowseTiles.FirstOrDefault(t =>
                t.SourceInstanceId == category.SourceInstanceId
                && (t.SourceCategoryId ?? t.Name) == (category.SourceCategoryId ?? ""));
            sourceState = live?.SourceState;
        }

        var root = new BrowseNode(
            category.Name,
            category.SourceInstanceId!,
            category.SourceCategoryId ?? category.Name,
            sourceState,
            category.Icon);
        await EnterBrowseNodeAsync(root, pushOntoStack: true);
    }

    /// <summary>
    /// Drills into a generic browse container item (pushes its node) — invoked when the user
    /// activates an <see cref="VideoItem.IsGenericContainer"/> result.
    /// </summary>
    public Task DrillIntoGenericContainerAsync(VideoItem item)
    {
        var node = new BrowseNode(
            item.Title,
            item.GenericSourceInstanceId!,
            item.GenericCategoryId ?? item.Title,
            item.GenericSourceState,
            item.ContainerIcon);
        return EnterBrowseNodeAsync(node, pushOntoStack: true);
    }

    /// <summary>Navigates one level back in the generic browse stack; returns false if already at root.</summary>
    public async Task<bool> GenericBrowseBackAsync()
    {
        if (_browseStack.Count <= 1) return false;
        _browseStack.RemoveAt(_browseStack.Count - 1);  // pop current
        var parent = _browseStack[^1];
        await EnterBrowseNodeAsync(parent, pushOntoStack: false);
        return true;
    }

    /// <summary>
    /// Core generic browse: calls the source's <c>IBrowsable.BrowseAsync</c> for <paramref name="node"/>,
    /// renders sub-categories as drill-in container items and leaf items (with resolved StreamUrl) for
    /// playback, and maintains the nav stack + breadcrumb. Source-agnostic — no per-source logic.
    /// </summary>
    private async Task EnterBrowseNodeAsync(BrowseNode node, bool pushOntoStack)
    {
        var source = _sourceRegistry?.ByInstance(node.SourceInstanceId);
        if (source is not Phosphor.Plugin.Abstractions.IBrowsable browsable)
        {
            StatusText = "This source can't be browsed.";
            return;
        }

        _searchCts.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        IsSearching = true;
        IsGenericBrowsing = true;
        // We've navigated into a generic browse tree (e.g. drilled from the Favorites tile into an
        // artist). Leave "playlist view" so container activation drills in (default) rather than
        // playing — the aggregated Favorites tile is a playlist view, but its drilled-in nodes are not.
        IsViewingPlaylist = false;
        StatusText = $"Loading {node.Title}...";
        SearchResults.Clear();

        try
        {
            var sourceCategory = new Phosphor.Plugin.Abstractions.SourceCategory
            {
                SourceInstanceId = node.SourceInstanceId,
                CategoryId = node.CategoryId,
                Title = node.Title,
                SourceState = node.SourceState,
            };

            // A frame carrying a SearchQuery is a scoped-search result set (not a plain browse) —
            // re-run the in-view search so Back navigates into it like any other level.
            if (node.SearchQuery is { Length: > 0 } query
                && source is Phosphor.Plugin.Abstractions.IScopedSearchable scopedSource)
            {
                var searchResult = await scopedSource.SearchInCategoryAsync(sourceCategory, query, ct);
                if (ct.IsCancellationRequested) return;

                if (pushOntoStack) _browseStack.Add(node);
                UpdateBrowseBreadcrumb();
                RaiseSearchScopeChanged();

                _genericPaged = null;
                CanLoadMore = false;
                foreach (var cat in searchResult.Categories)
                    SearchResults.Add(ToGenericContainerItem(cat, node.Icon, source as Phosphor.Plugin.Abstractions.IFavoritable));
                var searchResolver = source as Phosphor.Plugin.Abstractions.IPlayableResolver;
                foreach (var leaf in searchResult.Items)
                    await AddResolvedLeafAsync(leaf, searchResolver, ct);

                StatusText = $"{SearchResults.Count} result(s) for \"{query}\" in {node.Title}";
                return;
            }

            var result = await browsable.BrowseAsync(sourceCategory, ct);
            if (ct.IsCancellationRequested) return;

            if (pushOntoStack) _browseStack.Add(node);
            UpdateBrowseBreadcrumb();
            RaiseSearchScopeChanged();

            // Sub-categories first (drill-in containers), then leaf items (playable).
            foreach (var cat in result.Categories)
                SearchResults.Add(ToGenericContainerItem(cat, node.Icon, source as Phosphor.Plugin.Abstractions.IFavoritable));

            var resolver = source as Phosphor.Plugin.Abstractions.IPlayableResolver;

            // Leaf items. If BrowseAsync already returned leaves (e.g. an album's tracks), render
            // those directly. Otherwise, if the source is paginated, drive leaves through the paged
            // path so large collections lazy-load on "load more". A node yields leaves one way or the
            // other — never both — so this avoids rendering the same items twice.
            if (result.Items.Count > 0)
            {
                _genericPaged = null;
                foreach (var item in result.Items)
                    await AddResolvedLeafAsync(item, resolver, ct);
                CanLoadMore = false;
                StatusText = $"{SearchResults.Count} item(s) in {node.Title}";
            }
            else if (source is Phosphor.Plugin.Abstractions.IPagedBrowsable paged)
            {
                _genericPaged = paged;
                _genericPagedCategory = sourceCategory;
                _genericPagedResolver = resolver;
                _genericPagedOffset = 0;
                _genericPagedTotal = int.MaxValue;
                await LoadMoreGenericPageAsync();
            }
            else
            {
                _genericPaged = null;
                CanLoadMore = false;
                StatusText = $"{SearchResults.Count} item(s) in {node.Title}";
            }
        }
        catch (Exception ex)
        {
            DebugLog.LogException($"Plugin browse '{node.SourceInstanceId}'", ex);
            StatusText = "Failed to load this source.";
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>Maps a browse sub-category into a drill-in container <see cref="VideoItem"/>.
    /// Sub-categories without their own <see cref="Phosphor.Plugin.Abstractions.SourceCategory.Icon"/>
    /// inherit <paramref name="parentIcon"/> so grouping tiles (e.g. Hubs/Playlists) take on the
    /// parent library's personality.</summary>
    private static VideoItem ToGenericContainerItem(
        Phosphor.Plugin.Abstractions.SourceCategory cat, string? parentIcon = null,
        Phosphor.Plugin.Abstractions.IFavoritable? fav = null) => new()
    {
        Title = cat.Title,
        ThumbnailUrl = cat.ThumbnailUrl ?? "",
        VideoId = cat.CategoryId,
        IsGenericContainer = true,
        ContainerIcon = string.IsNullOrWhiteSpace(cat.Icon) ? parentIcon : cat.Icon,
        GenericSourceInstanceId = cat.SourceInstanceId,
        GenericSourceState = cat.SourceState,
        GenericCategoryId = cat.CategoryId,
        CanFavorite = fav != null,
        IsFavorite = fav?.IsFavorite(cat.CategoryId) ?? false,
    };

    /// <summary>
    /// Maps a leaf <see cref="SourceItem"/> that is actually a browsable container
    /// (<see cref="SourceItem.IsContainer"/> — e.g. a Plex artist/album returned inside a hub,
    /// playlist, or search result) into a drill-in container <see cref="VideoItem"/>. Carries the
    /// item's opaque <c>SourceState</c> so the source resolves the node on drill-in.
    /// </summary>
    private static VideoItem ToContainerLeafItem(
        Phosphor.Plugin.Abstractions.SourceItem item,
        Phosphor.Plugin.Abstractions.IFavoritable? fav = null) => new()
    {
        Title = item.Title,
        Author = item.Subtitle ?? "",
        ThumbnailUrl = item.ThumbnailUrl ?? "",
        VideoId = item.ItemId,
        IsGenericContainer = true,
        GenericSourceInstanceId = item.SourceInstanceId,
        GenericSourceState = item.SourceState,
        GenericCategoryId = item.ItemId,
        CanFavorite = fav != null,
        IsFavorite = fav?.IsFavorite(item.ItemId) ?? false,
    };

    // ── Generic paged browse state ──
    private Phosphor.Plugin.Abstractions.IPagedBrowsable? _genericPaged;
    private Phosphor.Plugin.Abstractions.SourceCategory? _genericPagedCategory;
    private Phosphor.Plugin.Abstractions.IPlayableResolver? _genericPagedResolver;
    private int _genericPagedOffset;
    private int _genericPagedTotal;

    /// <summary>Resolves a leaf <see cref="SourceItem"/> to a playable <see cref="VideoItem"/> and adds it.</summary>
    private async Task AddResolvedLeafAsync(
        Phosphor.Plugin.Abstractions.SourceItem item,
        Phosphor.Plugin.Abstractions.IPlayableResolver? resolver,
        CancellationToken ct)
    {
        // A "leaf" flagged IsContainer is really a browsable node (e.g. a Plex artist/album returned
        // inside a hub/playlist/search) — render it as a drill-in container, not a playable row.
        if (item.IsContainer)
        {
            SearchResults.Add(ToContainerLeafItem(item, resolver as Phosphor.Plugin.Abstractions.IFavoritable));
            return;
        }

        SearchResults.Add(await ResolveLeafAsync(item, resolver, ct));
    }

    /// <summary>
    /// Resolves a playable leaf <see cref="SourceItem"/> into a <see cref="VideoItem"/> with its
    /// <c>StreamUrl</c> populated (via the source's <see cref="IPlayableResolver"/>), without adding
    /// it anywhere. Shared by the browse render path and the container-expander (queue/playlist).
    /// </summary>
    private static async Task<VideoItem> ResolveLeafAsync(
        Phosphor.Plugin.Abstractions.SourceItem item,
        Phosphor.Plugin.Abstractions.IPlayableResolver? resolver,
        CancellationToken ct)
    {
        var vi = ToVideoItem(item);
        // Carry the opaque plug-in state so generic consumers (e.g. IFavoriteCapture) can hand it
        // back to the owning source to rebuild the item without a host-specific type.
        vi.GenericSourceInstanceId ??= item.SourceInstanceId;
        vi.GenericSourceState ??= item.SourceState;
        // Resolve a playable URL now (local files are a cheap path check); the player checks
        // VideoItem.StreamUrl first and plays it directly. EXCEPTIONS resolved lazily at play time:
        //  • live streams (e.g. SiriusXM radio) — eager resolve would fire one authenticated
        //    round-trip per channel (hundreds);
        //  • sources marked IDeferredStreamResolution (e.g. Vimeo via yt-dlp) — eager resolve would
        //    fire one expensive yt-dlp probe per browse row.
        var deferResolve = resolver is Phosphor.Plugin.Abstractions.IDeferredStreamResolution;
        if (resolver != null && !item.IsLiveStream && !deferResolve)
        {
            try
            {
                var stream = await resolver.ResolveAsync(
                    item, new Phosphor.Plugin.Abstractions.PlaybackPreferences(), ct);
                if (stream != null)
                {
                    vi.StreamUrl = stream.PrimaryUri;
                    vi.AudioStreamUrl = stream.Layout == Phosphor.Plugin.Abstractions.StreamLayout.SeparateVideoAudio
                        ? stream.AudioSlaveUri : null;
                    if (stream.IsLiveStream) vi.IsLiveStream = true;
                    if (stream.StartupTimeout is { } sto) vi.StartupTimeout = sto;
                    if (!string.IsNullOrEmpty(stream.AudioTag)) vi.AudioTag = stream.AudioTag!;
                }
            }
            catch (Exception ex)
            {
                DebugLog.LogException($"Plugin resolve '{item.ItemId}'", ex);
            }
        }
        else if (item.IsLiveStream)
        {
            // Defer resolution to play time; keep the SourceItem so PlayNow can resolve it.
            vi.IsLiveStream = true;
            vi.PendingLiveSourceItem = item;
        }
        else if (deferResolve)
        {
            // Finite item, but resolve is expensive — defer to play time (no live semantics).
            vi.PendingResolveSourceItem = item;
        }
        vi.IsAudioOnly = item.IsAudioOnly;
        vi.SourceInstanceId ??= item.SourceInstanceId;
        // Favorite state, when the owning source supports it (star toggle shows only then).
        if (resolver is Phosphor.Plugin.Abstractions.IFavoritable fav)
        {
            vi.CanFavorite = true;
            vi.IsFavorite = fav.IsFavorite(item.ItemId);
        }
        return vi;
    }

    /// <summary>
    /// Recursively expands a generic browse container (a <see cref="VideoItem.IsGenericContainer"/>
    /// item — e.g. a Plex artist or album) into its playable leaf <see cref="VideoItem"/>s by
    /// browsing it via the source's <see cref="IBrowsable"/>/<see cref="IPagedBrowsable"/> capability.
    /// Source-agnostic: an artist expands through its albums to every track. Used by queue/playlist
    /// add so a container never lands in the queue as an un-playable row. Capped defensively.
    /// </summary>
    private async Task<List<VideoItem>> ExpandContainerToLeavesAsync(VideoItem container, CancellationToken ct)
    {
        var leaves = new List<VideoItem>();
        var source = _sourceRegistry?.ByInstance(container.GenericSourceInstanceId ?? "");
        if (source is not Phosphor.Plugin.Abstractions.IBrowsable browsable)
            return leaves;

        var resolver = source as Phosphor.Plugin.Abstractions.IPlayableResolver;
        var node = new Phosphor.Plugin.Abstractions.SourceCategory
        {
            SourceInstanceId = container.GenericSourceInstanceId!,
            CategoryId = container.GenericCategoryId ?? "",
            Title = container.Title,
            SourceState = container.GenericSourceState,
        };

        await ExpandNodeAsync(node, browsable, resolver, leaves, depth: 0, ct);
        return leaves;
    }

    private async Task ExpandNodeAsync(
        Phosphor.Plugin.Abstractions.SourceCategory node,
        Phosphor.Plugin.Abstractions.IBrowsable browsable,
        Phosphor.Plugin.Abstractions.IPlayableResolver? resolver,
        List<VideoItem> leaves,
        int depth,
        CancellationToken ct)
    {
        const int MaxDepth = 4;      // artist → album → track is 2; headroom for other shapes
        const int MaxLeaves = 2000;  // safety cap so a huge library can't run away
        if (depth > MaxDepth || leaves.Count >= MaxLeaves || ct.IsCancellationRequested) return;

        var result = await browsable.BrowseAsync(node, ct);

        // Playable leaves at this level. If none came back inline and the source pages, pull pages.
        var items = result.Items;
        if (items.Count == 0 && browsable is Phosphor.Plugin.Abstractions.IPagedBrowsable paged)
        {
            int offset = 0, total = int.MaxValue;
            var buffer = new List<Phosphor.Plugin.Abstractions.SourceItem>();
            while (offset < total && leaves.Count + buffer.Count < MaxLeaves && !ct.IsCancellationRequested)
            {
                var page = await paged.BrowsePageAsync(node, offset, SearchPageSize, ct);
                total = page.TotalSize;
                if (page.Items.Count == 0) break;
                buffer.AddRange(page.Items);
                offset += page.Items.Count;
            }
            items = buffer;
        }

        foreach (var item in items)
        {
            if (leaves.Count >= MaxLeaves) return;
            if (item.IsContainer)
            {
                var child = new Phosphor.Plugin.Abstractions.SourceCategory
                {
                    SourceInstanceId = item.SourceInstanceId,
                    CategoryId = item.ItemId,
                    Title = item.Title,
                    SourceState = item.SourceState,
                };
                await ExpandNodeAsync(child, browsable, resolver, leaves, depth + 1, ct);
            }
            else
            {
                leaves.Add(await ResolveLeafAsync(item, resolver, ct));
            }
        }

        // Sub-categories (e.g. an artist's albums) — recurse into each.
        foreach (var cat in result.Categories)
        {
            if (leaves.Count >= MaxLeaves) return;
            await ExpandNodeAsync(cat, browsable, resolver, leaves, depth + 1, ct);
        }
    }

    /// <summary>Loads the next page of leaf items for the active generic paged browse node.</summary>
    private async Task LoadMoreGenericPageAsync()
    {
        if (_genericPaged is null || _genericPagedCategory is null || _isLoadingMore) return;
        if (_genericPagedOffset >= _genericPagedTotal) { CanLoadMore = false; return; }

        _isLoadingMore = true;
        var token = _searchCts.Token;
        try
        {
            var page = await _genericPaged.BrowsePageAsync(
                _genericPagedCategory, _genericPagedOffset, SearchPageSize, token);
            if (token.IsCancellationRequested) return;

            _genericPagedTotal = page.TotalSize;
            foreach (var item in page.Items)
                await AddResolvedLeafAsync(item, _genericPagedResolver, token);

            _genericPagedOffset += page.Items.Count;
            // Stop if the source reports no more, or returned an empty page (defensive against
            // a source that under-reports TotalSize).
            bool hasMore = page.Items.Count > 0 && _genericPagedOffset < _genericPagedTotal;
            CanLoadMore = hasMore;
            StatusText = hasMore
                ? $"Showing {SearchResults.Count} item(s) — scroll for more"
                : $"Showing all {SearchResults.Count} item(s)";
        }
        catch (Exception ex)
        {
            DebugLog.LogException("Generic browse pagination", ex);
            CanLoadMore = false;
        }
        finally
        {
            _isLoadingMore = false;
        }
    }

    // ── Genre categories (loaded from categories.json) ──
    private List<GenreCategoryEntry> _genreCategories = [];

    /// <summary>
    /// Returns the loaded genre category entries (for use in settings UI).
    /// </summary>
    public IReadOnlyList<GenreCategoryEntry> GenreCategories => _genreCategories;

    /// <summary>
    /// Returns the playlist manager instance (for use in settings UI).
    /// </summary>
    public PlaylistManager PlaylistManager => _playlists;

    /// <summary>
    /// Returns the names of all built-in genre categories (for use in settings UI).
    /// </summary>
    public IReadOnlyList<string> AllGenreCategoryNames => _genreCategories.Select(c => c.Name).ToList();

    public void SetHiddenCategories(IEnumerable<string> hidden)
    {
        var hiddenSet = new HashSet<string>(hidden, StringComparer.OrdinalIgnoreCase);
        _hiddenPlaylistNames = hiddenSet;
        foreach (var entry in _genreCategories)
            entry.IsVisible = !hiddenSet.Contains(entry.Name);
        GenreCategoryStore.Save(_genreCategories);
        RebuildCategories();
    }

    // Playlist tiles (Favorites, saved playlists) hidden via the visibility checkboxes. Genre
    // categories carry their own IsVisible flag; playlists are filtered here since they're rebuilt
    // from the PlaylistManager each time.
    private HashSet<string> _hiddenPlaylistNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reloads genre categories from the persisted categories.json file and rebuilds the UI.
    /// Call this after the settings window has saved category edits (name, icon, additions, removals).
    /// </summary>
    public void ReloadGenreCategories()
    {
        _genreCategories = GenreCategoryStore.Load();
        RebuildCategories();
    }

    // ── Plex ──

    /// <summary>
    /// Ensures the plug-in instance list is seeded (fresh installs) and refreshes the category
    /// tiles. Plex itself flows entirely through the plug-in source registry now — this no longer
    /// wires up any Plex service directly.
    /// </summary>
    public void ConfigurePlexFromSettings(AppSettings settings, bool skipRebuild = false)
    {
        // Seed the instance list on first run so a fresh install still gets Plex tiles
        // from the migrated flat fields (matches BuildSourceRegistryAsync's one-time seed).
        if (settings.PluginInstances.Count == 0)
            settings.PluginInstances = Phosphor.Plugins.PluginSettingsFactory.FromAppSettings(settings);

        // Plex now flows entirely through the plug-in source registry: browse tiles come from the
        // generic browse path (BuildPluginBrowseTilesAsync → SyncSourceTiles), and
        // playback/expansion/chapters/gapless route through the Plex source's capabilities. This
        // method only ensures instances are seeded and refreshes the category tiles.
        if (!skipRebuild)
            RebuildCategories();
    }

    // ── Categories (playlists + genres, rebuilt dynamically) ──
    public BulkObservableCollection<Category> Categories { get; } = new();

    // ── State ──
    private string _searchQuery = "";
    public string SearchQuery
    {
        get => _searchQuery;
        set => SetProperty(ref _searchQuery, value);
    }

    private bool _isSearching;
    public bool IsSearching
    {
        get => _isSearching;
        set => SetProperty(ref _isSearching, value);
    }

    /// <summary>
    /// Guards against rapid successive play requests (e.g. double-click race).
    /// Set before dispatching a play request, cleared once the player signals playback started.
    /// </summary>
    private bool _playTransitioning;
    public bool PlayTransitioning
    {
        get => _playTransitioning;
        internal set => SetProperty(ref _playTransitioning, value);
    }

    /// <summary>
    /// The current item awaiting a background cache download, deferred until playback is confirmed
    /// (see <see cref="NotifyPlaybackStarted"/>). Deferring avoids the background download contending
    /// with stream resolution and tripping the first-frame watchdog on long videos.
    /// </summary>
    private VideoItem? _pendingCacheItem;

    /// <summary>
    /// Call from the player host once playback has actually started (video output received)
    /// to allow the next play request to proceed.
    /// </summary>
    public void NotifyPlaybackStarted()
    {
        PlayTransitioning = false;
        _statusPrefixCts?.Cancel();
        StatusPrefix = "";

        // Now that playback is confirmed, kick off the deferred background cache download for the
        // current item (see _pendingCacheItem). Starting it here — rather than at play dispatch —
        // keeps the second yt-dlp/HTTP fetch from starving the first-frame path on long videos.
        var toCache = _pendingCacheItem;
        _pendingCacheItem = null;
        if (toCache != null && _cache is { Enabled: true } && IsItemCacheable(toCache))
            _ = SafeFireAndForget(CacheAfterRegistryReadyAsync(toCache));

        // Self-healing badges: a successful play clears any "unavailable" mark the owning source kept
        // for this item (e.g. an IPTV channel that was previously geo-blocked/offline). Report it and
        // clear the live row's badge so the ⊘ disappears immediately.
        var playing = CurrentlyPlaying;
        if (playing is { ShowUnavailableBadge: true } && SourceForItem(playing) is Phosphor.Plugin.Abstractions.IPlaybackSuccessReportable ok)
        {
            try
            {
                if (ok.ReportPlaybackSuccess(playing.VideoId))
                    playing.ShowUnavailableBadge = false;
            }
            catch (Exception ex)
            {
                DebugLog.LogException($"ReportPlaybackSuccess '{playing.VideoId}'", ex);
            }
        }
    }

    /// <summary>
    /// Called by the player windows when a live/stream item fails to start (e.g. a timeout waiting for
    /// the first frame — common for dead or geo-blocked IPTV channels). Reports the failure to the
    /// owning source (as <see cref="Phosphor.Plugin.Abstractions.PlaybackFailureKind.Transient"/>,
    /// since a stream timeout is environmental, not proof the item is permanently dead). A source that
    /// tracks soft failures badges the row via <see cref="VideoItem.ShowUnavailableBadge"/> — the item
    /// stays playable so the user can retry, and a later success clears it. No-ops for sources that
    /// don't implement <see cref="Phosphor.Plugin.Abstractions.IPlaybackReportable"/>.
    /// </summary>
    public void NotifyPlaybackFailed(VideoItem? item)
    {
        if (item is null) return;
        // A failed start should not trigger the deferred background cache (the failure path calls this
        // then NotifyPlaybackStarted). Clear the pending item so we don't cache something that didn't play.
        if (ReferenceEquals(_pendingCacheItem, item))
            _pendingCacheItem = null;
        if (SourceForItem(item) is not Phosphor.Plugin.Abstractions.IPlaybackReportable reportable) return;
        try
        {
            reportable.ReportPlaybackFailure(item.VideoId, Phosphor.Plugin.Abstractions.PlaybackFailureKind.Transient);
            // The source decides whether to remember it; reflect its badge on the live row. We badge
            // when the owning source also tracks successes (so it can self-heal the badge on retry).
            if (ShouldBadgeUnavailable(item))
                item.ShowUnavailableBadge = true;
        }
        catch (Exception ex)
        {
            DebugLog.LogException($"NotifyPlaybackFailed '{item.VideoId}'", ex);
        }
    }

    // A source that wants the row badged exposes it through a fresh SourceItem; but to avoid a rebuild
    // round-trip on the hot failure path we simply badge the row when the owning source is one that
    // tracks soft failures (IPlaybackSuccessReportable ⇒ it can also clear it). This keeps the badge
    // in lockstep with the source's own persisted set without an extra query.
    private bool ShouldBadgeUnavailable(VideoItem item)
        => SourceForItem(item) is Phosphor.Plugin.Abstractions.IPlaybackSuccessReportable;

    /// <summary>
    /// Tells the outgoing item's source to release any stateful resource it opened for it (e.g. a Plex
    /// Live TV tuner session). Best-effort and non-blocking: a source implementing
    /// <see cref="Phosphor.Plugin.Abstractions.IPlaybackStoppable"/> must not throw, but we still guard
    /// and never let teardown disturb the play/stop flow.
    /// </summary>
    private void ReleasePlaybackFor(VideoItem item)
    {
        if (SourceForItem(item) is not Phosphor.Plugin.Abstractions.IPlaybackStoppable stoppable) return;
        try
        {
            stoppable.ReleasePlayback(item.VideoId);
        }
        catch (Exception ex)
        {
            DebugLog.LogException($"ReleasePlayback '{item.VideoId}'", ex);
        }
    }

    private VideoItem? _currentlyPlaying;
    public VideoItem? CurrentlyPlaying
    {
        get => _currentlyPlaying;
        set
        {
            var outgoing = _currentlyPlaying;
            if (SetProperty(ref _currentlyPlaying, value))
                {
                    // Release any stateful resource the outgoing item's source was holding (e.g. a Plex
                    // Live TV tuner session). Fire-and-forget, best-effort — the single choke point for
                    // stop / skip / track-change, so a source never leaks a held tuner/session.
                    if (outgoing is not null && !ReferenceEquals(outgoing, value))
                        ReleasePlaybackFor(outgoing);

                    IsPaused = false;
                    _lastChapterIndex = -1;
                    CurrentChapterName = "";
                    ChapterTickPositions = [];
                    _isCurrentFromCache = false;
                    OnPropertyChanged(nameof(IsPlaying));
                    OnPropertyChanged(nameof(CanStartOrStop));
                    OnPropertyChanged(nameof(ShouldSnapToChapters));
                    OnPropertyChanged(nameof(NowPlayingTitle));
                    OnPropertyChanged(nameof(NowPlayingSourceText));
                    OnPropertyChanged(nameof(IsLiveStream));
                    OnPropertyChanged(nameof(PlaybackTimeText));
                }
        }
    }

    /// <summary>
    /// Fractional positions (0.0–1.0) of chapter markers relative to total duration,
    /// used to render tick marks on the scrub bar.
    /// </summary>
    private List<double> _chapterTickPositions = [];
    public List<double> ChapterTickPositions
    {
        get => _chapterTickPositions;
        private set => SetProperty(ref _chapterTickPositions, value);
    }

    private void UpdateChapterTickPositions()
    {
        var chapters = _currentlyPlaying?.Chapters;
        var duration = _playbackDuration;
        DebugLog.Log(LogLevel.Trace, "Chapters", $"UpdateChapterTickPositions: chapters={chapters?.Count ?? 0} duration={duration}");
        if (chapters == null || chapters.Count == 0 || duration <= 1)
        {
            ChapterTickPositions = [];
            return;
        }
        var ticks = chapters
            .Select(c => c.StartTime.TotalMilliseconds / duration)
            .Where(p => p > 0 && p < 1)
            .ToList();
        DebugLog.Log(LogLevel.Trace, "Chapters", $"Tick positions: [{string.Join(", ", ticks.Select(t => t.ToString("F3")))}]");
        ChapterTickPositions = ticks;
    }

    /// <summary>
    /// Called when chapters are restored from cache on a currently playing item.
    /// </summary>
    public void NotifyCachedChaptersRestored()
    {
        UpdateChapterTickPositions();
        OnPropertyChanged(nameof(ShouldSnapToChapters));
        UpdateCurrentChapter();
    }

    /// <summary>
    /// Name of the chapter currently playing, or empty if no chapters.
    /// </summary>
    private string _currentChapterName = "";
    public string CurrentChapterName
    {
        get => _currentChapterName;
        private set
        {
            if (SetProperty(ref _currentChapterName, value))
                OnPropertyChanged(nameof(NowPlayingTitle));
        }
    }

    /// <summary>
    /// Display title for the now-playing area. Appends the chapter name when available,
    /// and the upload date (in parentheses) when known.
    /// </summary>
    public string NowPlayingTitle
    {
        get
        {
            var title = _currentlyPlaying?.Title ?? "Nothing playing";
            if (!string.IsNullOrEmpty(_currentChapterName))
                title = $"{title} \u2014 {_currentChapterName}";

            var dateText = _currentlyPlaying?.UploadDateText;
            if (!string.IsNullOrEmpty(dateText))
                title = $"{title} ({dateText})";

            return title;
        }
    }

    /// <summary>
    /// Tracks whether the currently-playing item was served from the local cache.
    /// Set by the player window when it begins playback; reset whenever
    /// <see cref="CurrentlyPlaying"/> changes.
    /// </summary>
    private bool _isCurrentFromCache;

    /// <summary>
    /// Called by the player window to indicate whether the current item is playing
    /// from the local cache, so the now-playing area can reflect the source.
    /// </summary>
    public void SetCurrentFromCache(bool fromCache)
    {
        if (_isCurrentFromCache == fromCache) return;
        _isCurrentFromCache = fromCache;
        OnPropertyChanged(nameof(NowPlayingSourceText));
    }

    /// <summary>
    /// Short source annotation for the now-playing label, e.g. "(from YouTube)",
    /// "(from Plex)", or "(from YouTube - Cached)". Empty when nothing is playing or
    /// the source is not a known streaming source (e.g. the local startup ditti).
    /// </summary>
    public string NowPlayingSourceText
    {
        get
        {
            if (_currentlyPlaying == null) return "";

            // Prefer the owning plug-in source's display name (source-agnostic), falling back to the
            // legacy YouTube id-shape heuristic for items without a source link.
            string? source = null;
            if (_currentlyPlaying.SourceInstanceId is { Length: > 0 } id)
                source = _sourceRegistry?.ByInstance(id)?.DisplayName;
            source ??= _currentlyPlaying switch
            {
                { IsYouTube: true } => "YouTube",
                _ => null
            };
            if (source == null)
                return "";

            return _isCurrentFromCache ? $"(from {source} - Cached)" : $"(from {source})";
        }
    }

    private int _lastChapterIndex = -1;

    /// <summary>
    /// Updates CurrentChapterName based on playback position. Called from PlaybackPosition setter.
    /// </summary>
    private void UpdateCurrentChapter()
    {
        var chapters = _currentlyPlaying?.Chapters;
        if (chapters == null || chapters.Count == 0 || _playbackDuration <= 1)
        {
            if (_lastChapterIndex != -1)
            {
                _lastChapterIndex = -1;
                CurrentChapterName = "";
            }
            return;
        }

        int idx = GetCurrentChapterIndex(chapters);
        if (idx != _lastChapterIndex)
        {
            _lastChapterIndex = idx;
            CurrentChapterName = chapters[idx].Title;
        }
    }

    /// <summary>
    /// True when the current item has enough chapters (3+) that the scrub bar should snap to chapter boundaries.
    /// </summary>
    public bool ShouldSnapToChapters => (_currentlyPlaying?.Chapters?.Count ?? 0) >= 3 && _playbackDuration > 1;

    public bool IsPlaying => CurrentlyPlaying != null;

    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        set => SetProperty(ref _isPaused, value);
    }

    /// <summary>
    /// True when the Start/Stop button should be enabled: either something is playing (stop)
    /// or there are items in the queue (start).
    /// </summary>
    public bool CanStartOrStop => IsPlaying || Queue.Count > 0;

    public bool HasQueueItems => Queue.Count > 0;

    public string QueueCountText => Queue.Count > 0 ? $"({Queue.Count} {(Queue.Count == 1 ? "item" : "items")})" : "";

    private string _statusText = "Select a category or search";
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(DisplayStatusText));
                // Gate the classifier on logging being enabled: it's cheap (a few substring scans at
                // user-action frequency), but computing a level only to discard it when logging is off
                // is pure waste. Skip it in the common (disabled) case.
                if (DebugLog.Enabled)
                    DebugLog.Log(ClassifyStatusLevel(value), "Status", value);
            }
        }
    }

    // Status text is user-facing and mixes routine messages ("Playing: …", "Loading …") with genuine
    // problems ("Playback failed …", "… unreachable"). Rather than reclassify every StatusText writer,
    // infer a level from the message at this single choke point: failure-ish wording → Warning, else
    // Info. Keeps status noise out of the default Debug view only for the routine cases while still
    // surfacing failures.
    private static LogLevel ClassifyStatusLevel(string? message)
    {
        if (string.IsNullOrEmpty(message)) return LogLevel.Info;
        // Case-insensitive scan for failure indicators. Intentionally conservative — false negatives
        // just log at Info (harmless); we don't want routine text mislabeled as Warning.
        foreach (var kw in StatusWarningKeywords)
            if (message.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return LogLevel.Warning;
        return LogLevel.Info;
    }

    private static readonly string[] StatusWarningKeywords =
        ["fail", "error", "unreachable", "timed out", "timeout", "unavailable", "can't", "cannot",
         "unable", "no playable", "not found", "denied", "invalid", "refused"];

    private string _statusPrefix = "";
    private CancellationTokenSource? _statusPrefixCts;

    private string StatusPrefix
    {
        get => _statusPrefix;
        set
        {
            if (SetProperty(ref _statusPrefix, value))
                OnPropertyChanged(nameof(DisplayStatusText));
        }
    }

    public string DisplayStatusText => string.IsNullOrEmpty(_statusPrefix) ? _statusText : $"{_statusPrefix}{_statusText}";

    /// <summary>
    /// Shows a bracketed prefix before the status text that auto-clears after 5 seconds.
    /// </summary>
    public void SetStatusPrefix(string prefix, int clearAfterMs = 5000)
    {
        _statusPrefixCts?.Cancel();
        StatusPrefix = $"[{prefix}] ";
        var cts = new CancellationTokenSource();
        _statusPrefixCts = cts;
        _ = ClearStatusPrefixAfterDelay(clearAfterMs, cts.Token);
    }

    private async Task ClearStatusPrefixAfterDelay(int delayMs, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delayMs, ct);
            StatusPrefix = "";
        }
        catch (OperationCanceledException) { }
    }

    private string _activeCategory = "";
    public string ActiveCategory
    {
        get => _activeCategory;
        set => SetProperty(ref _activeCategory, value);
    }

    private bool _showCategories = true;
    public bool ShowCategories
    {
        get => _showCategories;
        set => SetProperty(ref _showCategories, value);
    }

    private bool _isStartupHintVisible;
    /// <summary>
    /// Drives the one-time "Initial setup" hint card on the DMD screen. Seeded from
    /// <see cref="AppSettings.ShowStartupHint"/> at launch and cleared when the user dismisses it.
    /// </summary>
    public bool IsStartupHintVisible
    {
        get => _isStartupHintVisible;
        set => SetProperty(ref _isStartupHintVisible, value);
    }

    private bool _isViewingPlaylist;
    public bool IsViewingPlaylist
    {
        get => _isViewingPlaylist;
        set
        {
            if (SetProperty(ref _isViewingPlaylist, value))
            {
                OnPropertyChanged(nameof(IsViewingStaticPlaylist));
                OnPropertyChanged(nameof(IsViewingFavorites));
                OnPropertyChanged(nameof(ShowRemoveFromPlaylist));
                OnPropertyChanged(nameof(IsFavoritesGroupedView));
            }
        }
    }

    private bool _repeatEnabled;
    public bool RepeatEnabled
    {
        get => _repeatEnabled;
        set
        {
            if (SetProperty(ref _repeatEnabled, value))
                RepeatEnabledChanged?.Invoke(value);
        }
    }

    public event Action<bool>? RepeatEnabledChanged;

    private int _queueIndex = -1;
    /// <summary>
    /// Index of the currently playing item in the queue. -1 means nothing is playing from the queue.
    /// </summary>
    public int QueueIndex
    {
        get => _queueIndex;
        set
        {
            if (SetProperty(ref _queueIndex, value))
            {
                if (value >= 0)
                    LastKnownQueueIndex = value;
                OnPropertyChanged(nameof(CurrentQueueItem));
            }
        }
    }

    /// <summary>
    /// Remembers the last non-negative queue index for persistence across sessions.
    /// </summary>
    public int LastKnownQueueIndex { get; private set; } = -1;

    /// <summary>
    /// The queue item currently being played, or null if nothing is playing.
    /// </summary>
    public VideoItem? CurrentQueueItem => _queueIndex >= 0 && _queueIndex < Queue.Count ? Queue[_queueIndex] : null;

    private bool _autoDjEnabled;
    public bool AutoDjEnabled
    {
        get => _autoDjEnabled;
        set
        {
            if (SetProperty(ref _autoDjEnabled, value))
            {
                if (value)
                    _ = SafeFireAndForget(AutoDjFillQueue());
                AutoDjEnabledChanged?.Invoke(value);
            }
        }
    }

    public event Action<bool>? AutoDjEnabledChanged;

    private bool _isAutoDjFilling;

    // ── Favorites view (aggregated tile): two independent axes, persisted in AppSettings and mirrored
    //    by the quick dropdowns on the Favorites view + the Settings → DMD → Favorites section. ──
    private FavoritesGrouping _favoritesGrouping = FavoritesGrouping.None;
    public FavoritesGrouping FavoritesGrouping
    {
        get => _favoritesGrouping;
        set { if (SetProperty(ref _favoritesGrouping, value)) OnFavoritesViewChanged(); }
    }

    private FavoritesSort _favoritesSort = FavoritesSort.RecentlyAdded;
    public FavoritesSort FavoritesSort
    {
        get => _favoritesSort;
        set { if (SetProperty(ref _favoritesSort, value)) OnFavoritesViewChanged(); }
    }

    /// <summary>Re-renders the Favorites tile when a view axis changes (only if it's currently shown).</summary>
    private void OnFavoritesViewChanged()
    {
        FavoritesViewChanged?.Invoke();
        OnPropertyChanged(nameof(IsFavoritesGroupedView));
        OnPropertyChanged(nameof(IsFavoritesCustomOrder));
        if (IsViewingFavorites)
            SearchResults.ReplaceAll(BuildAggregatedFavorites());
    }

    /// <summary>Raised when a favorites view axis changes, so the host can persist settings.</summary>
    public event Action? FavoritesViewChanged;

    /// <summary>True while the aggregated Favorites tile is the active view (drives the quick dropdowns).</summary>
    public bool IsViewingFavorites => IsViewingStaticPlaylist && ActivePlaylistName == "Favorites";

    /// <summary>
    /// True when a per-row "Remove from Playlist" button should show: any static playlist EXCEPT the
    /// aggregated Favorites view, where the star toggle already removes the favorite (the ✕ would be
    /// redundant with un-starring).
    /// </summary>
    public bool ShowRemoveFromPlaylist => IsViewingStaticPlaylist && !IsViewingFavorites;

    /// <summary>
    /// True when the Favorites view is grouped by provider — the DMD swaps the results panel to a
    /// vertical stack (full-width headers + rows) instead of the multi-column wrap panel.
    /// </summary>
    public bool IsFavoritesGroupedView => IsViewingFavorites && FavoritesGrouping == FavoritesGrouping.Provider;

    /// <summary>True when the aggregated Favorites view is in user-defined manual order (drag-to-reorder).</summary>
    public bool IsFavoritesCustomOrder => IsViewingFavorites && FavoritesGrouping == FavoritesGrouping.Custom;

    /// <summary>
    /// Persists the current manual order of the Favorites view after a drag-reorder. Reads the visible
    /// rows' (source, id) keys and writes them through the index (a discrete user action). Removed
    /// favorites are pruned and new ones appended by the index itself.
    /// </summary>
    public void PersistFavoritesCustomOrder()
    {
        if (!IsFavoritesCustomOrder) return;
        var keys = SearchResults
            .Where(r => !r.IsHeader)
            .Select(r =>
                r.IsSeparator ? FavoritesIndex.SeparatorMarker
                : r.IsLineBreak ? FavoritesIndex.LineBreakMarker
                : FavoritesIndex.MakeKey(r.SourceInstanceId ?? "", r.VideoId));
        _favoritesIndex.SetCustomOrder(keys);
    }

    /// <summary>The shared host-level favorites index (exposed so Settings can edit the manual order).</summary>
    public FavoritesIndex FavoritesIndex => _favoritesIndex;

    /// <summary>Rebuilds the Favorites tile now if it is the active view (e.g. after Settings edits the order).</summary>
    public void RefreshFavoritesViewIfActive()
    {
        if (IsViewingFavorites)
            SearchResults.ReplaceAll(BuildAggregatedFavorites());
    }

    // ── Active playlist (for "Add to Playlist" default target) ──
    private string _activePlaylistName = "Favorites";
    private string? _activePlaylistId;
    public string ActivePlaylistName
    {
        get => _activePlaylistName;
        set
        {
            if (SetProperty(ref _activePlaylistName, value) && IsViewingPlaylist)
                LoadPlaylist(value);
            OnPropertyChanged(nameof(IsViewingFavorites));
            OnPropertyChanged(nameof(ShowRemoveFromPlaylist));
            OnPropertyChanged(nameof(IsFavoritesGroupedView));
        }
    }

    private void LoadPlaylist(string name)
    {
        var playlist = _playlists.Playlists.FirstOrDefault(p => p.Name == name);
        SearchResults.ReplaceAll(playlist?.Videos ?? []);
        ActiveCategory = name;
        StatusText = $"{SearchResults.Count} videos in {name}";
    }

    public RangedObservableCollection<VideoItem> SearchResults { get; } = new();
    public ObservableCollection<VideoItem> Queue { get; } = new();

    // ── Search history for ComboBox ──
    public ObservableCollection<string> SearchSuggestions { get; } = new();
    public IReadOnlyList<string> AllSearchHistory => _searchHistory.Searches;

    public event Action<string>? PlayRequested;
    public event Action? StopRequested;
    public event Action? PauseRequested;
    public event Action? ResumeRequested;
    public event Action<long>? SeekRequested;

    private double _playbackPosition;
    public double PlaybackPosition
    {
        get => _playbackPosition;
        set
        {
            if (SetProperty(ref _playbackPosition, value))
            {
                OnPropertyChanged(nameof(PlaybackTimeText));
                UpdateCurrentChapter();
            }
        }
    }

    private double _playbackDuration = 1;
    public double PlaybackDuration
    {
        get => _playbackDuration;
        set
        {
            if (SetProperty(ref _playbackDuration, value))
            {
                OnPropertyChanged(nameof(PlaybackTimeText));
                UpdateChapterTickPositions();
            }
        }
    }

    private bool _isSeeking;
    public bool IsSeeking { get => _isSeeking; set => SetProperty(ref _isSeeking, value); }

    private int _volume = 100;
    public int Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, Math.Clamp(value, 0, 100)))
                VolumeChanged?.Invoke(_volume);
        }
    }

    public event Action<int>? VolumeChanged;

    public string PlaybackTimeText
    {
        get
        {
            // Live streams have no fixed duration — show elapsed-since-start against "*".
            if (_currentlyPlaying?.IsLiveStream == true)
            {
                var elapsed = TimeSpan.FromMilliseconds(Math.Max(0, PlaybackPosition));
                var lfmt = elapsed.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss";
                return $"{elapsed.ToString(lfmt)} / *";
            }
            if (PlaybackDuration <= 1) return "0:00 / 0:00";
            var pos = TimeSpan.FromMilliseconds(PlaybackPosition);
            var dur = TimeSpan.FromMilliseconds(PlaybackDuration);
            var fmt = dur.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss";
            return $"{pos.ToString(fmt)} / {dur.ToString(fmt)}";
        }
    }

    /// <summary>True when the currently-playing item is a continuous live stream (e.g. SiriusXM).
    /// The UI hides the scrub bar/duration and seek is a no-op; playback shows elapsed "M:SS / *".</summary>
    public bool IsLiveStream => _currentlyPlaying?.IsLiveStream == true;

    public void SeekTo(long timeMs)
    {
        if (IsLiveStream) return; // live streams are not seekable
        SeekRequested?.Invoke(timeMs);
    }

    [RelayCommand]
    private void SeekForward()
    {
        if (IsLiveStream) return;
        SeekRequested?.Invoke((long)PlaybackPosition + 15000);
    }

    [RelayCommand]
    private void SeekBack()
    {
        if (IsLiveStream) return;
        SeekRequested?.Invoke(Math.Max(0, (long)PlaybackPosition - 15000));
    }

    // ── Video cache ──
    public VideoCache? Cache => _cache;
    public PrefetchCache? Prefetch => _prefetch;
    private string? _prefetchingVideoId;

    // Guards against an unbounded skip loop when many consecutive tracks fail to resolve (e.g. a run
    // of DRM-protected SoundCloud tracks). Reset whenever a track resolves or the user picks a track.
    private int _consecutiveResolveFailures;
    private const int MaxConsecutiveResolveSkips = 8;

    // ── Video engine (YoutubeExplode / yt-dlp switch point) ──
    // ── Video engine ──
    // The YouTube engine (YoutubeExplode / yt-dlp) now lives entirely in the YouTube plug-in.
    // The host no longer holds an in-process engine; all resolve/download/metadata flows route
    // through the plug-in source's capabilities. The engine choice is a plug-in setting.

    /// <summary>
    /// Rebuilds the video engine from the given kind and propagates it to the caches.
    /// Safe to call at startup and on settings changes.
    /// </summary>
    public void SetVideoEngine(VideoEngineKind kind)
    {
        // Engine selection is now owned by the YouTube plug-in via its settings; nothing to do
        // host-side. Retained as a no-op so existing settings call sites keep compiling.
    }

    // ── Thumbnail cache ──
    public ThumbnailCache? ThumbnailCache { get; private set; }

    // ── Result-page cache ──
    // A single source-agnostic cache for the paginated result pages behind category tiles and live
    // playlists (keyed by the globally-unique tile/playlist id). Attaching is policy-gated per source
    // (see ShouldCacheResults) so ephemeral sources opt out.
    public ResultCache? ResultPageCache { get; private set; }

    public void SetupResultCache(bool enabled, int maxAgeHours)
    {
        if (ResultPageCache == null)
            ResultPageCache = new ResultCache(enabled, maxAgeHours, "r_", "result_cache");
        else
            ResultPageCache.UpdateSettings(enabled, maxAgeHours);
    }

    /// <summary>
    /// Whether the host should cache result pages for the given source, honoring the source's
    /// <see cref="Phosphor.Plugin.Abstractions.IResultCachePolicy"/> when it declares one. A source
    /// that opts out (e.g. an ephemeral live feed) is never cached; one that declares nothing falls
    /// back to <paramref name="defaultCache"/>. <c>null</c> instance = the YouTube-bound path.
    /// </summary>
    private bool ShouldCacheResults(string? sourceInstanceId, bool defaultCache = true)
    {
        var source = sourceInstanceId == null
            ? _sourceRegistry?.YouTube
            : _sourceRegistry?.ByInstance(sourceInstanceId);
        if (source is Phosphor.Plugin.Abstractions.IResultCachePolicy p)
            return p.GetResultCachePolicy().Cache;
        return defaultCache;
    }

    public void SetupThumbnailCache(bool enabled, double maxSizeMb)
    {
        if (ThumbnailCache == null)
            ThumbnailCache = new ThumbnailCache(enabled, maxSizeMb);
        else
            ThumbnailCache.UpdateSettings(enabled, maxSizeMb);
    }

    // ── Video quality ──
    public VideoQualityPreference VideoQuality { get; set; } = VideoQualityPreference.High;

    // ── Audio channel preference ──
    public bool StereoAudio { get; set; }

    // ── Network buffering ──
    public int NetworkCachingMs { get; set; } = 2000;
    public int LiveCachingMs { get; set; } = 1000;
    public int FileCachingMs { get; set; } = 300;
    public bool HttpReconnect { get; set; } = true;

    // ── Network timeout (host-shared HttpClient) ──
    public int NetworkTimeoutSeconds { get; private set; } = 30;

    public void SetNetworkTimeout(int seconds)
    {
        seconds = Math.Clamp(seconds, 5, 120);
        if (seconds == NetworkTimeoutSeconds) return;
        NetworkTimeoutSeconds = seconds;
        RebuildSearchEngine();
        DebugLog.Log(LogLevel.Debug, "Network", $"Timeout set to {seconds}s");
    }

    /// <summary>
    /// Rebuilds the search engine from the given kind (and current timeout) and
    /// propagates it. Safe to call at startup and on settings changes.
    /// </summary>
    public void SetSearchEngine(SearchEngineKind kind)
    {
        _searchEngineKind = kind;
        // Search backend selection is owned by the YouTube plug-in; the host only records the
        // kind to tune paging (see SearchPageSize).
    }

    private void RebuildSearchEngine()
    {
        // No host-side search engine anymore — kept as a no-op for existing call sites
        // (e.g. network-timeout changes) that used to force a rebuild.
    }

    /// <summary>
    /// Whether an item's source can produce downloadable raw streams for the disk caches. Driven by
    /// capability (the owning source implements <c>IDownloadable</c> — YouTube does, Plex does not),
    /// defaulting to cacheable only when the registry is unavailable. The per-instance
    /// <c>AllowCaching</c> policy overrides the capability default when set (<c>true</c> forces on,
    /// <c>false</c> forces off).
    /// </summary>
    private bool IsItemCacheable(VideoItem item)
    {
        if (_sourceRegistry != null)
        {
            // YouTube items have no "scheme:" prefix; Plex items are "plex:...". Route the item to
            // its source and ask whether that source supports downloading, then let the instance's
            // AllowCaching policy (if set) override that capability default.
            var source = SourceForItem(item);
            if (source == null) return false;

            var policy = _sourceRegistry.CachingPolicy(source.InstanceId);
            if (policy.HasValue) return policy.Value;

            return source is Phosphor.Plugin.Abstractions.IDownloadable;
        }

        // No registry (edge): default to cacheable; the capability path above governs normally.
        return true;
    }

    /// <summary>
    /// Caches an item after the source registry has finished building, so the cache's
    /// <c>DownloadOverride</c> is guaranteed wired (it is set inside <see cref="BuildSourceRegistryAsync"/>).
    /// Closes a startup race where an early play reached the cache trigger before the fire-and-forget
    /// registry build wired the override, causing a silent no-op. A no-op once the registry is ready.
    /// </summary>
    private async Task CacheAfterRegistryReadyAsync(VideoItem item)
    {
        try { await _sourceRegistryReady; }
        catch { /* registry build failures are logged in BuildSourceRegistryAsync */ }

        if (_cache is not { Enabled: true }) return;
        await _cache.CacheVideoAsync(item.VideoId, VideoQuality, StereoAudio, item.Duration, item.Chapters, item.Title);
    }

    /// <summary>
    /// Resolves the plug-in source a playing <see cref="VideoItem"/> belongs to via its recorded
    /// <see cref="VideoItem.SourceInstanceId"/>; falls back to YouTube for legacy items or the
    /// built-in engine path. Returns null if the registry is unavailable.
    /// </summary>
    private Phosphor.Plugin.Abstractions.IPhosphorSource? SourceForItem(VideoItem item)
    {
        if (_sourceRegistry == null) return null;
        // Prefer the explicit source link the producing source recorded; fall back to YouTube for
        // legacy items and the built-in engine.
        if (item.SourceInstanceId is { Length: > 0 } id
            && _sourceRegistry.ByInstance(id) is { } owner)
            return owner;
        return _sourceRegistry.YouTube;
    }

    /// <summary>
    /// Builds a probe <see cref="Phosphor.Plugin.Abstractions.SourceItem"/> for a playing host
    /// <see cref="VideoItem"/>, suitable for <c>IPlayableResolver.GetMetadataAsync</c>. Carries the
    /// whole <see cref="VideoItem"/> in <c>SourceState</c> (Plex reads it back for its rating key)
    /// while <c>ItemId</c> holds the id (YouTube's <c>VideoIdOf</c> falls back to it, since a
    /// <see cref="VideoItem"/> isn't a string). One shape serves every source.
    /// </summary>
    private static Phosphor.Plugin.Abstractions.SourceItem ProbeSourceItem(
        VideoItem item, string sourceInstanceId) => new()
    {
        SourceInstanceId = sourceInstanceId,
        ItemId = item.VideoId,
        Title = item.Title,
        IsAudioOnly = item.IsAudioOnly,
        SourceState = item,
    };

    /// <summary>
    /// Runs <paramref name="action"/> on the UI (dispatcher) thread. A no-op marshal when the
    /// caller is already on the UI thread, so it is safe and cheap on any path. Needed because
    /// async continuations can resume on a threadpool thread (e.g. the plug-in search path),
    /// and mutating UI-bound <see cref="ObservableCollection{T}"/>s like <see cref="Queue"/>
    /// off the dispatcher throws.
    /// </summary>
    private static async Task RunOnUiAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            await dispatcher.InvokeAsync(action);
        else
            action();
    }

    /// <summary>
    /// Free-text video search. Routes through the YouTube source's <c>ITextSearchCapable</c>
    /// capability (mapping results back to <see cref="VideoItem"/>); falls back to the legacy in-VM
    /// search engine when the registry is unavailable.
    /// </summary>
    private IAsyncEnumerable<VideoItem> SearchVideosViaPluginOrLegacy(string query)
        => SearchVideosViaPluginOrLegacy(query, null);

    /// <summary>
    /// Runs a free-text search against a specific plug-in source. <paramref name="sourceInstanceId"/>
    /// selects the <c>ITextSearchCapable</c> source: <c>null</c> means YouTube (the default and the
    /// source-bound path for tiles/AutoDJ). Falls back to YouTube, then an empty result, when the
    /// requested source is unavailable or not searchable.
    /// </summary>
    private IAsyncEnumerable<VideoItem> SearchVideosViaPluginOrLegacy(string query, string? sourceInstanceId)
    {
        // Resolve the requested source; null => YouTube.
        var source = sourceInstanceId != null ? _sourceRegistry?.ByInstance(sourceInstanceId) : _sourceRegistry?.YouTube;
        if (source is Phosphor.Plugin.Abstractions.ITextSearchCapable capable)
        {
            DebugLog.Log(LogLevel.Debug, "SourceRegistry", $"Search routed through plug-in source '{source.InstanceId}'");
            return MapPluginSearch(capable, query);
        }

        // Requested source gone/not searchable — fall back to YouTube, else an empty result.
        if (_sourceRegistry?.YouTube is Phosphor.Plugin.Abstractions.ITextSearchCapable yt)
        {
            DebugLog.Log(LogLevel.Debug, "SourceRegistry", "Search fell back to plug-in YouTube source");
            return MapPluginSearch(yt, query);
        }

        return EmptyVideoItems();
    }

    /// <summary>An empty result stream, used when no searchable source is configured.</summary>
    private static async IAsyncEnumerable<VideoItem> EmptyVideoItems()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<VideoItem> MapPluginSearch(
        Phosphor.Plugin.Abstractions.ITextSearchCapable source,
        string query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var vi in MapPluginItems(source, source.SearchAsync(query, ct), ct).WithCancellation(ct))
            yield return vi;
    }

    /// <summary>
    /// Maps a <see cref="Phosphor.Plugin.Abstractions.IFilterableSearch"/> result stream to
    /// <see cref="VideoItem"/>s, resolving playable streams eagerly for non-YouTube sources (same
    /// contract as <see cref="MapPluginSearch"/>).
    /// </summary>
    private static async IAsyncEnumerable<VideoItem> MapPluginFilteredSearch(
        Phosphor.Plugin.Abstractions.IFilterableSearch source,
        Phosphor.Plugin.Abstractions.FilteredSearchResult result,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var vi in MapPluginItems(source, result.Items, ct).WithCancellation(ct))
            yield return vi;
    }

    /// <summary>
    /// Shared search-result mapper: turns a source's <see cref="Phosphor.Plugin.Abstractions.SourceItem"/>
    /// stream into <see cref="VideoItem"/>s. YouTube resolves its stream lazily at play time (an
    /// expensive yt-dlp probe per item); any other source that can resolve (local folders, Plex, …)
    /// resolves eagerly here so the result carries a playable StreamUrl — otherwise playback falls
    /// through to the YouTube engine and fails. Mirrors the browse path.
    /// </summary>
    private static async IAsyncEnumerable<VideoItem> MapPluginItems(
        object source,
        IAsyncEnumerable<Phosphor.Plugin.Abstractions.SourceItem> items,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var resolver = source is Phosphor.Plugin.Abstractions.IPlayableResolver r
            && source is Phosphor.Plugin.Abstractions.IPhosphorSource s
            && s.TypeId != Phosphor.Plugins.KnownSourceTypeIds.YouTube
            ? r : null;

        // Sources whose resolve is expensive (yt-dlp per item, e.g. Vimeo) opt out of eager
        // resolution: we carry the SourceItem and resolve lazily at play time instead, exactly like
        // YouTube. This keeps search fast (one probe on play, not one per row).
        var deferResolve = source is Phosphor.Plugin.Abstractions.IDeferredStreamResolution;

        await foreach (var item in items.WithCancellation(ct))
        {
            // A container result (e.g. an iHeartRadio podcast show, a Plex artist/album returned by
            // search) is a drill-in, not a playable leaf — map it so the UI shows Open and drilling
            // in browses its children, rather than trying (and failing) to play it.
            if (item.IsContainer)
            {
                yield return ToContainerLeafItem(item, source as Phosphor.Plugin.Abstractions.IFavoritable);
                continue;
            }

            var vi = ToVideoItem(item);
            if (item.IsLiveStream)
            {
                // Live streams (e.g. SiriusXM channels) MUST resolve lazily at play time. Resolving
                // here would fire a round-trip per result AND mutate the source's single shared proxy,
                // leaving it pointed at whichever result resolved last (so every channel would play the
                // last one). Carry the SourceItem; PlayNow resolves it on demand.
                vi.IsLiveStream = true;
                vi.PendingLiveSourceItem = item;
                vi.IsAudioOnly = item.IsAudioOnly;
            }
            else if (deferResolve)
            {
                vi.PendingResolveSourceItem = item;
                vi.IsAudioOnly = item.IsAudioOnly;
            }
            else if (resolver != null && string.IsNullOrEmpty(vi.StreamUrl))
            {
                try
                {
                    var stream = await resolver.ResolveAsync(
                        item, new Phosphor.Plugin.Abstractions.PlaybackPreferences(), ct);
                    if (stream != null)
                    {
                        vi.StreamUrl = stream.PrimaryUri;
                        vi.AudioStreamUrl = stream.Layout == Phosphor.Plugin.Abstractions.StreamLayout.SeparateVideoAudio
                            ? stream.AudioSlaveUri : null;
                    }
                }
                catch (Exception ex)
                {
                    DebugLog.LogException($"Search resolve '{item.ItemId}'", ex);
                }
                vi.IsAudioOnly = item.IsAudioOnly;
            }

            // Star toggle: light it up when the owning source supports favorites (e.g. YouTube).
            if (source is Phosphor.Plugin.Abstractions.IFavoritable fav)
            {
                vi.CanFavorite = true;
                vi.IsFavorite = fav.IsFavorite(item.ItemId);
            }

            yield return vi;
        }
    }

    /// <summary>Maps a plug-in <see cref="Phosphor.Plugin.Abstractions.SourceItem"/> to a host
    /// <see cref="VideoItem"/>. When the source stashed a full <see cref="VideoItem"/> in
    /// <see cref="Phosphor.Plugin.Abstractions.SourceItem.SourceState"/> (as Plex does, carrying its
    /// rich <c>plex:</c> id / rating key / audio stream), it is unwrapped directly so playback and
    /// caching route correctly; otherwise the flat fields are copied (YouTube discovery shape).</summary>
    private static VideoItem ToVideoItem(Phosphor.Plugin.Abstractions.SourceItem item)
    {
        if (item.SourceState is VideoItem carried)
            return carried;

        return new VideoItem
        {
            Title = item.Title,
            Author = item.Subtitle ?? "",
            ThumbnailUrl = item.ThumbnailUrl ?? "",
            VideoId = item.ItemId,
            Duration = item.Duration,
            UploadDate = item.PublishedAt,
            SourceInstanceId = item.SourceInstanceId,
            IsAudioOnly = item.IsAudioOnly,
            IsLiveStream = item.IsLiveStream,
            ShowLiveBadge = item.ShowLiveBadge,
            ShowUnavailableBadge = item.ShowUnavailableBadge,
            IsPlayable = item.IsPlayable,
            HasVideoAlternative = item.HasVideoAlternative,
            VideoSearchQuery = item.VideoSearchQuery,
        };
    }

    private static async IAsyncEnumerable<VideoItem> MapPluginItems(
        IAsyncEnumerable<Phosphor.Plugin.Abstractions.SourceItem> source,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in source.WithCancellation(ct))
            yield return ToVideoItem(item);
    }

    /// <summary>The YouTube discovery capability if the registry is available, else null.</summary>
    private Phosphor.Plugin.Abstractions.IPlaylistChannelDiscovery? PluginDiscovery =>
        _sourceRegistry?.YouTube is Phosphor.Plugin.Abstractions.IPlaylistChannelDiscovery d
            ? d : null;

    /// <summary>Resolves a playlist id via the plug-in discovery capability, else null.</summary>
    private Task<string?> ResolvePlaylistIdViaPluginOrLegacy(string nameIdOrUrl, Action<string>? onFoundByName)
        => PluginDiscovery is { } d
            ? d.ResolvePlaylistIdAsync(nameIdOrUrl, onFoundByName)
            : Task.FromResult<string?>(null);

    /// <summary>Yields a playlist's videos via the plug-in discovery capability, else empty.</summary>
    private IAsyncEnumerable<VideoItem> GetPlaylistVideosViaPluginOrLegacy(string playlistId)
        => PluginDiscovery is { } d
            ? MapPluginItems(d.GetPlaylistItemsAsync(playlistId))
            : EmptyVideoItems();

    /// <summary>Yields a channel's uploads via the plug-in discovery capability, else empty.</summary>
    private IAsyncEnumerable<VideoItem> GetChannelUploadsViaPluginOrLegacy(string handleOrUser)
        => PluginDiscovery is { } d
            ? MapPluginItems(d.GetChannelUploadsAsync(handleOrUser))
            : EmptyVideoItems();

    /// <summary>
    /// Lightweight heuristic: does the token look like a YouTube playlist id (or a URL carrying a
    /// <c>list=</c> parameter)? Used only to decide filter-term parsing; the YouTube plug-in does
    /// the authoritative resolution. Keeps the host free of a YouTube-specific library.
    /// </summary>
    private static bool LooksLikeYouTubePlaylistId(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var m = Regex.Match(token, @"[?&]list=([\w-]+)");
        var candidate = m.Success ? m.Groups[1].Value : token;
        // Playlist ids are prefixed (PL, UU, FL, OL, RD, LL, …) and reasonably long.
        return Regex.IsMatch(candidate, @"^(PL|UU|FL|OL|RD|LL|WL|EL)[\w-]{10,}$")
            || Regex.IsMatch(candidate, @"^[\w-]{18,}$");
    }

    /// <summary>
    /// Fetches YouTube video metadata. Routes through the YouTube source's
    /// <c>IPlayableResolver.GetMetadataAsync</c> (mapping the result back to the host
    /// <see cref="Video.VideoMetadata"/>); falls back to the legacy in-VM video engine when the
    /// registry is unavailable.
    /// </summary>
    private async Task<Video.VideoMetadata?> GetYouTubeMetadataViaPluginOrLegacy(string videoId)
    {
        if (_sourceRegistry?.YouTube is Phosphor.Plugin.Abstractions.IPlayableResolver resolver)
        {
            var probe = new Phosphor.Plugin.Abstractions.SourceItem
            {
                SourceInstanceId = _sourceRegistry.YouTube!.InstanceId,
                ItemId = videoId,
                SourceState = videoId,
            };
            var meta = await resolver.GetMetadataAsync(probe);
            return meta == null ? null : MapPluginMetadata(meta);
        }

        return null;
    }

    private static Video.VideoMetadata MapPluginMetadata(Phosphor.Plugin.Abstractions.SourceMetadata m) =>
        new(
            m.Duration,
            m.Description,
            m.Chapters.Select(c => new ChapterMarker
            {
                Title = c.Title,
                StartTime = c.Start,
                EndTime = c.End ?? TimeSpan.Zero,
            }).ToList(),
            m.PublishedAt);

    /// <summary>
    /// Resolves live YouTube stream URLs for playback. Routes through the YouTube source's
    /// <c>IPlayableResolver.ResolveAsync</c> (mapping the result back to the host
    /// <see cref="Video.VideoStreams"/>); falls back to the legacy in-VM video engine when the
    /// registry is unavailable.
    /// </summary>
    /// <remarks>
    /// Returns plain data and touches no UI/dispatcher, so it is safe to await from the
    /// BackglassWindow's own thread (unlike work that must marshal to a UI thread).
    /// </remarks>
    public async Task<Video.VideoStreams?> ResolveStreamsViaPluginOrLegacy(
        string videoId, VideoQualityPreference quality, bool preferStereo, bool audioOnly, CancellationToken ct = default)
    {
        if (_sourceRegistry?.YouTube is Phosphor.Plugin.Abstractions.IPlayableResolver resolver)
        {
            var probe = new Phosphor.Plugin.Abstractions.SourceItem
            {
                SourceInstanceId = _sourceRegistry.YouTube!.InstanceId,
                ItemId = videoId,
                SourceState = videoId,
            };
            var prefs = new Phosphor.Plugin.Abstractions.PlaybackPreferences
            {
                MaxQuality = MapQualityToPlugin(quality),
                PreferStereo = preferStereo,
                AudioOnly = audioOnly,
            };
            var resolved = await resolver.ResolveAsync(probe, prefs, ct);
            DebugLog.Log(LogLevel.Debug, "SourceRegistry", "Stream resolution routed through plug-in YouTube source");
            return resolved == null ? null : MapResolvedStream(resolved);
        }

        return null;
    }

    private static Phosphor.Plugin.Abstractions.VideoQuality MapQualityToPlugin(VideoQualityPreference q) => q switch
    {
        VideoQualityPreference.Low => Phosphor.Plugin.Abstractions.VideoQuality.Low,
        VideoQualityPreference.Medium => Phosphor.Plugin.Abstractions.VideoQuality.Medium,
        VideoQualityPreference.High => Phosphor.Plugin.Abstractions.VideoQuality.High,
        _ => Phosphor.Plugin.Abstractions.VideoQuality.Max,
    };

    private static Video.VideoStreams MapResolvedStream(Phosphor.Plugin.Abstractions.ResolvedStream s)
    {
        var kind = s.Layout switch
        {
            Phosphor.Plugin.Abstractions.StreamLayout.SeparateVideoAudio => Video.VideoStreamKind.SeparateVideoAudio,
            Phosphor.Plugin.Abstractions.StreamLayout.Muxed => Video.VideoStreamKind.Muxed,
            _ => Video.VideoStreamKind.AudioOnly,
        };
        return new Video.VideoStreams(kind, s.PrimaryUri, s.AudioSlaveUri, s.Resolution ?? "");
    }

    /// <summary>
    /// Downloads raw streams for the disk caches. When the plug-in path is enabled and the
    /// YouTube source supports downloading, routes through <c>IDownloadable.DownloadAsync</c>
    /// (mapping the result back to the host <see cref="Video.VideoDownload"/>); otherwise, or if
    /// the plug-in yields an incomplete result, falls back to the legacy video engine. Returns
    /// plain data / no UI, so it is safe to await from the caches' background download threads.
    /// </summary>
    private async Task<Video.VideoDownload?> DownloadStreamsViaPluginOrLegacy(
        string videoId, VideoQualityPreference quality, bool preferStereo, string destinationDir, CancellationToken ct)
    {
        if (_sourceRegistry?.YouTube is Phosphor.Plugin.Abstractions.IDownloadable dl)
        {
            var probe = new Phosphor.Plugin.Abstractions.SourceItem
            {
                SourceInstanceId = _sourceRegistry.YouTube!.InstanceId,
                ItemId = videoId,
                SourceState = videoId,
            };
            var prefs = new Phosphor.Plugin.Abstractions.PlaybackPreferences
            {
                MaxQuality = MapQualityToPlugin(quality),
                PreferStereo = preferStereo,
            };
            var result = await dl.DownloadAsync(probe, prefs, destinationDir, null, ct);

            // The caches mux separate video+audio; use the plug-in result only when fully populated.
            if (result?.VideoFilePath is { } vp && result.AudioFilePath is { } ap)
            {
                DebugLog.Log(LogLevel.Debug, "SourceRegistry", "Stream download routed through plug-in YouTube source");
                return new Video.VideoDownload(
                    vp, ap, result.VideoContainer ?? "", result.AudioContainer ?? "", result.Resolution ?? "");
            }
        }

        return null;
    }

    /// <summary>
    /// Wires (or clears) the plug-in download override on the given caches based on whether the
    /// plug-in source path is active. Called after the caches or registry are (re)built.
    /// </summary>
    private void WireCacheDownloadOverride()
    {
        Func<string, VideoQualityPreference, bool, string, CancellationToken, Task<Video.VideoDownload?>>? over =
            (_sourceRegistry?.YouTube is Phosphor.Plugin.Abstractions.IDownloadable)
                ? DownloadStreamsViaPluginOrLegacy
                : null;

        if (_cache != null) _cache.DownloadOverride = over;
        if (_prefetch != null) _prefetch.DownloadOverride = over;
    }

    /// <summary>
    /// Number of results to fetch per "load more" page. The out-of-process yt-dlp search
    /// engine has higher per-page latency (a process spawn), so it uses a larger page to
    /// reduce how often the user hits a fetch while scrolling.
    /// </summary>
    private int SearchPageSize =>
        _searchEngineKind == SearchEngineKind.YtDlp ? 50 : 25;

    // ── Gapless playback ──
    public bool GaplessPlayback { get; set; }

    /// <summary>
    /// Returns a stable, pre-loadable gapless stream URL for <paramref name="item"/>, or null if it
    /// isn't gapless-eligible. Driven by the source's <c>IGaplessCapable</c> capability (Plex audio
    /// tracks qualify; YouTube doesn't), falling back to the legacy rule (Plex audio-only item with
    /// a StreamUrl) when the registry is unavailable. Pure/synchronous — no UI or dispatcher — so
    /// it's safe to call from BackglassWindow's own thread.
    /// </summary>
    public string? TryGetGaplessStreamUrl(VideoItem item)
    {
        if (_sourceRegistry != null)
        {
            // Route the item to its source and ask the gapless capability. The host passes the
            // pre-built stream URL via SourceState (a string) so the source never needs a host type.
            var source = SourceForItem(item);
            if (source is Phosphor.Plugin.Abstractions.IGaplessCapable g)
            {
                var probe = new Phosphor.Plugin.Abstractions.SourceItem
                {
                    SourceInstanceId = source.InstanceId,
                    ItemId = item.VideoId,
                    IsAudioOnly = item.IsAudioOnly,
                    SourceState = item.StreamUrl,
                };
                return g.GetGaplessStreamUrl(probe);
            }
            return null;
        }

        // Legacy rule (registry unavailable): any audio-only item with a pre-built stream.
        return item.IsAudioOnly && !string.IsNullOrEmpty(item.StreamUrl)
            ? item.StreamUrl
            : null;
    }

    /// <summary>
    /// Returns the next track in the queue if it is gapless-eligible (a pre-loadable audio stream),
    /// suitable for gapless pre-loading. Returns null otherwise.
    /// </summary>
    public VideoItem? GetNextGaplessTrack()
    {
        if (!GaplessPlayback || Queue.Count == 0) return null;

        int nextIdx = _queueIndex + 1;
        if (nextIdx >= Queue.Count && _repeatEnabled && Queue.Count > 0)
            nextIdx = 0;
        if (nextIdx < 0 || nextIdx >= Queue.Count) return null;

        var next = Queue[nextIdx];
        return TryGetGaplessStreamUrl(next) != null ? next : null;
    }

    public void SetupCache(bool enabled, double maxSizeGb, int maxClipLengthMinutes = 0)
    {
        // Update in place when a cache already exists so its loaded index/entries survive a settings
        // save (create only on first call). Re-newing here would discard the in-memory index and
        // effectively hide already-cached files until restart.
        if (_cache != null)
            _cache.UpdateSettings(enabled, maxSizeGb, maxClipLengthMinutes);
        else
            _cache = new VideoCache(enabled, maxSizeGb, maxClipLengthMinutes);
        WireCacheDownloadOverride();
    }

    /// <summary>
    /// When true (and the persistent video cache is enabled), the next item in the
    /// queue is preemptively downloaded and remuxed as soon as the current track
    /// starts playing. Independent of <see cref="PrefetchEnabled"/>: prefetch uses
    /// a lightweight last-second cache for instant transitions; preemptive cache
    /// writes into the persistent cache and also makes the next track fully seekable.
    /// </summary>
    public bool PreemptiveCache { get; set; }

    /// <summary>
    /// Tracks which YouTube videoIds we've already kicked off a preemptive cache
    /// job for this session, to avoid duplicate work when the same next-track is
    /// targeted multiple times (e.g. queue navigation, repeat).
    /// </summary>
    private readonly HashSet<string> _preemptiveCacheStarted = new();

    /// <summary>
    /// If <see cref="PreemptiveCache"/> is enabled and the persistent cache is on,
    /// kicks off a background <see cref="VideoCache.CacheVideoAsync"/> for the next
    /// playable queue item. Safe to call repeatedly; only the first invocation per
    /// videoId per session does any work. Skips Plex / audio-only / already-cached
    /// / already-prefetched / already-transient-cached items.
    /// </summary>
    public void KickoffPreemptiveCacheForNext()
    {
        if (!PreemptiveCache) return;
        if (_cache is not { Enabled: true }) return;

        // Determine which item is "next" in the queue (with repeat wrap-around).
        int nextIdx = _queueIndex + 1;
        if (nextIdx >= Queue.Count && _repeatEnabled && Queue.Count > 0)
            nextIdx = 0;
        if (nextIdx < 0 || nextIdx >= Queue.Count) return;

        var next = Queue[nextIdx];
        if (!IsItemCacheable(next) || next.IsAudioOnly)
        {
            if (DebugLog.Enabled)
                DebugLog.Log(LogLevel.Debug, "PreemptiveCache",
                    $"Skip next '{next.VideoId}': cacheable={IsItemCacheable(next)}, audioOnly={next.IsAudioOnly}");
            return;
        }

        var videoId = next.VideoId;
        if (string.IsNullOrEmpty(videoId)) return;

        // Skip if already cached (persistent or transient) or prefetched
        if (_cache.TryGet(videoId) != null) return;
        if (_prefetch?.TryGet(videoId) != null) return;

        // Only kick off once per session per video
        lock (_preemptiveCacheStarted)
        {
            if (!_preemptiveCacheStarted.Add(videoId)) return;
        }

        DebugLog.Log(LogLevel.Debug, "PreemptiveCache", $"Starting preemptive cache job for next track {videoId}: {next.Title}");
        // Route through the registry-ready gate: at startup the fire-and-forget registry build may not
        // have wired the cache's DownloadOverride yet, so an early preemptive kickoff would silently
        // no-op (same race as the current-item cache). Awaiting closes that race.
        _ = SafeFireAndForget(CacheAfterRegistryReadyAsync(next));
    }

    public void SetupPrefetch(bool enabled)
    {
        if (enabled)
        {
            _prefetch ??= new PrefetchCache();
            WireCacheDownloadOverride();
        }
        else
        {
            _prefetch?.PurgeAll();
            _prefetch = null;
        }
        WireCacheDownloadOverride();
    }

    /// <summary>
    /// Pre-caches the next track in the queue so playback transitions are nearly instant.
    /// Uses the lightweight PrefetchCache (independent of the main VideoCache).
    /// </summary>
    public void PrefetchNextTrack()
    {
        if (_prefetch == null) return;
        int nextIdx = _queueIndex + 1;

        // Wrap around for repeat mode
        if (nextIdx >= Queue.Count && _repeatEnabled && Queue.Count > 0)
            nextIdx = 0;

        if (nextIdx < 0 || nextIdx >= Queue.Count) return;

        var nextId = Queue[nextIdx].VideoId;
        if (nextId == _prefetchingVideoId) return;

        // Non-cacheable sources are streamed directly — no YouTube-style prefetch needed
        if (!IsItemCacheable(Queue[nextIdx])) return;

        // Skip if already in the main cache or prefetch cache
        if (_cache?.TryGet(nextId) != null) return;
        if (_prefetch.TryGet(nextId) != null) return;

        _prefetchingVideoId = nextId;
        SetStatusPrefix("Prefetching");
        // Route through the registry-ready gate so an early prefetch doesn't lose the startup race for
        // the shared DownloadOverride (same fix as the current-item / preemptive caches).
        var prefetchItem = Queue[nextIdx];
        _ = SafeFireAndForget(PrefetchAfterRegistryReadyAsync(prefetchItem));
    }

    /// <summary>
    /// Prefetches an item after the source registry has finished building, so the prefetch cache's
    /// <c>DownloadOverride</c> is guaranteed wired. Closes the same startup race as
    /// <see cref="CacheAfterRegistryReadyAsync"/>. A no-op once the registry is ready.
    /// </summary>
    private async Task PrefetchAfterRegistryReadyAsync(VideoItem item)
    {
        try { await _sourceRegistryReady; }
        catch { /* registry build failures are logged in BuildSourceRegistryAsync */ }

        if (_prefetch == null) return;
        await _prefetch.PrefetchAsync(item.VideoId, VideoQuality, StereoAudio);
    }

    // ── Duration filter ──
    /// <summary>
    /// Maximum number of items to scan when a duration filter (min:/max:) is active.
    /// Prevents runaway enumeration when few results match the filter.
    /// </summary>
    private const int MaxDurationScanCount = 500;
    private TimeSpan? _durationMin;
    private TimeSpan? _durationMax;
    private int _durationScanned;
    // Parsed "library:<name>" scope token from the search box (Plex server-side filter). Null = none.
    private string? _libraryFilter;

    // ── Pagination state ──
    private IAsyncEnumerator<VideoItem>? _searchEnumerator;
    private CancellationTokenSource _searchCts = new();
    private string _currentSearchQuery = "";
    // The source id the current search ran against (null = YouTube); captured so "save as live
    // playlist" binds the playlist to the source actually searched, not the current dropdown value.
    private string? _currentSearchSourceId;
    // When the current search is an in-library (scoped) search, the durable CategoryId + display
    // title of the scope, so "save as live playlist" can persist and later replay the scope.
    // Null when the search is source-wide (or YouTube/legacy).
    private string? _currentScopeCategoryId;
    private string? _currentScopeTitle;
    private bool _hasMoreResults;
    private bool _isLoadingMore;

    private bool _canLoadMore;
    public bool CanLoadMore
    {
        get => _canLoadMore;
        set => SetProperty(ref _canLoadMore, value);
    }

    public JukeboxViewModel()
    {
        _history = PlayHistory.Load();
        _playlists = new PlaylistManager();
        _searchHistory = SearchHistory.Load();
        _genreCategories = GenreCategoryStore.Load();
        RebuildCategories();
        RefreshSearchSuggestions();
        LoadQueue();
        Queue.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanStartOrStop));
            OnPropertyChanged(nameof(HasQueueItems));
            OnPropertyChanged(nameof(QueueCountText));
            SaveQueue();
        };
    }

    // ── Category management ──

    private bool _isViewingLivePlaylist;
    public bool IsViewingLivePlaylist
    {
        get => _isViewingLivePlaylist;
        set
        {
            if (SetProperty(ref _isViewingLivePlaylist, value))
            {
                OnPropertyChanged(nameof(IsViewingStaticPlaylist));
                OnPropertyChanged(nameof(IsViewingFavorites));
                OnPropertyChanged(nameof(ShowRemoveFromPlaylist));
                OnPropertyChanged(nameof(IsFavoritesGroupedView));
            }
        }
    }

    public bool IsViewingStaticPlaylist => IsViewingPlaylist && !IsViewingLivePlaylist;

    public void RebuildCategories()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Build sortable entries from both playlists and genre categories
        var sortable = new List<(int SortOrder, List<Category> Items)>();

        foreach (var pl in _playlists.Playlists)
        {
            if (_hiddenPlaylistNames.Contains(pl.Name)) continue;
            var defaultIcon = pl.Name == "Favorites" ? "⭐" : pl.Kind == PlaylistKind.Live ? "🔎" : "📋";
            var icon = string.IsNullOrEmpty(pl.Icon) ? defaultIcon : pl.Icon;
            sortable.Add((pl.SortOrder, new List<Category>
            {
                new() { Name = pl.Name, Icon = icon, SearchTerm = "", IsPlaylist = true }
            }));
        }

        foreach (var entry in _genreCategories)
        {
            if (entry.IsSeparator)
            {
                sortable.Add((entry.SortOrder, new List<Category> { new() { IsSeparator = true } }));
                continue;
            }
            if (entry.IsLineBreak)
            {
                sortable.Add((entry.SortOrder, new List<Category> { new() { IsLineBreak = true } }));
                continue;
            }
            if (!entry.IsVisible) continue;

            if (entry.IsGenericSource)
            {
                // Generic plug-in source root tile. The persisted entry holds all display data
                // (name/icon/ids/sort/visibility), so we render the tile IMMEDIATELY from disk — no
                // wait on a network fetch. The opaque browse SourceState is recovered from the live
                // tile list when available; if the background fetch hasn't populated it yet, the tile
                // still shows and drill-in awaits the fetch (see BrowsePluginCategoryAsync).
                var live = _pluginBrowseTiles.FirstOrDefault(t =>
                    t.SourceInstanceId == entry.SourceInstanceId
                    && (t.SourceCategoryId ?? t.Name) == (entry.SourceCategoryId ?? ""));

                sortable.Add((entry.SortOrder, new List<Category>
                {
                    new()
                    {
                        Name = entry.Name,
                        Icon = entry.Icon,
                        IsPluginBrowse = true,
                        SourceInstanceId = entry.SourceInstanceId,
                        SourceCategoryId = entry.SourceCategoryId,
                        SourceState = live?.SourceState, // null until the background fetch resolves it
                    }
                }));
            }
            else
            {
                sortable.Add((entry.SortOrder, new List<Category>
                {
                    new()
                    {
                        Name = entry.Name,
                        Icon = entry.Icon,
                        SearchTerm = entry.SearchTerm,
                        // Saved-search source tiles (YouTube genre tiles) carry their bound source so
                        // opening them routes DoSearch to that source; host-only genre entries leave
                        // this null (defaulting to the YouTube path).
                        SourceInstanceId = entry.IsSavedSearchSource ? entry.SourceInstanceId : null,
                    }
                }));
            }
        }

        // Merge by SortOrder (stable sort preserves relative order for ties)
        var items = sortable.OrderBy(s => s.SortOrder).SelectMany(s => s.Items).ToList();

        // "New Playlist" action tile at the end
        items.Add(new Category { Name = "New Playlist", Icon = "＋", IsNewPlaylist = true });

        DebugLog.Log(LogLevel.Trace, "RebuildCategories", $"Build list ({items.Count} items): {sw.ElapsedMilliseconds}ms");
        sw.Restart();

        // Batch-update: replace all items and fire a single Reset notification
        Categories.ReplaceAll(items);

        DebugLog.Log(LogLevel.Trace, "RebuildCategories", $"ReplaceAll: {sw.ElapsedMilliseconds}ms");
    }

    // ── Category browsing ──

    public event Action? NewPlaylistRequested;

    [RelayCommand]
    private async Task SelectCategoryAsync(Category? category)
    {
        if (category == null) return;

        if (category.IsNewPlaylist)
        {
            NewPlaylistRequested?.Invoke();
            return;
        }

        ActiveCategory = category.Name;

        if (category.IsPlaylist)
        {
            // The built-in "Favorites" tile is now the host-level AGGREGATED view across all sources,
            // rendered instantly from the write-through index (not the legacy playlist videos).
            if (category.Name == "Favorites")
            {
                ActivePlaylistName = category.Name;
                IsViewingPlaylist = true;
                IsViewingLivePlaylist = false;
                _hasMoreResults = false;
                CanLoadMore = false;
                SearchResults.ReplaceAll(BuildAggregatedFavorites());
                StatusText = $"{SearchResults.Count} favorite(s) across all sources";
                ShowCategories = false;
                return;
            }

            var playlist = _playlists.Playlists.FirstOrDefault(p => p.Name == category.Name);
            if (playlist?.Kind == PlaylistKind.Live)
            {
                // Live playlist — run the stored search against the source it was bound to
                // (null = YouTube for legacy playlists). Reflect that source in the dropdown so
                // "load more" / re-save stay consistent.
                ActivePlaylistName = category.Name;
                _activePlaylistId = playlist.Id;
                ActiveCategory = category.Name;
                IsViewingPlaylist = true;
                IsViewingLivePlaylist = true;
                ShowCategories = false;
                SearchQuery = playlist.SearchTerm;
                if (playlist.SourceInstanceId != null && SearchSources.Any(s => s.InstanceId == playlist.SourceInstanceId))
                    ActiveSearchSourceId = playlist.SourceInstanceId;

                // Scoped live playlist (saved from an in-library search): replay the scoped search by
                // rehydrating the browse scope from its durable ids and running the in-library search.
                if (playlist.ScopeCategoryId is { Length: > 0 } scopeId
                    && playlist.SourceInstanceId is { Length: > 0 } scopeInstance
                    && _sourceRegistry?.ByInstance(scopeInstance) is Phosphor.Plugin.Abstractions.IScopedSearchable)
                {
                    await LoadScopedLivePlaylistAsync(playlist, scopeInstance, scopeId);
                    return;
                }

                await DoSearch(playlist.SearchTerm, playlist.SourceInstanceId);
                return;
            }

            ActivePlaylistName = category.Name;
            IsViewingPlaylist = true;
            IsViewingLivePlaylist = false;
            _hasMoreResults = false;
            CanLoadMore = false;
            SearchResults.ReplaceAll(playlist?.Videos ?? []);
            StatusText = $"{SearchResults.Count} videos in {category.Name}";
            ShowCategories = false;
            return;
        }

        // Generic plug-in browse tile (Plex libraries, local-folder, future third-party sources).
        if (category.IsPluginBrowse && category.SourceInstanceId != null)
        {
            IsViewingPlaylist = false;
            _hasMoreResults = false;
            CanLoadMore = false;
            ShowCategories = false;
            await BrowsePluginCategoryAsync(category);
            return;
        }

        if (category.Name == "History")
        {
            IsViewingPlaylist = false;
            _hasMoreResults = false;
            _isHistoryBrowsing = true;
            SearchResults.Clear();
            LoadMoreHistoryResults();
            ShowCategories = false;
            return;
        }

        SearchQuery = category.SearchTerm;
        ShowCategories = false;
        await DoSearch(category.SearchTerm, category.SourceInstanceId);
    }

    /// <summary>
    /// Replays a scoped (in-library) live playlist: rebuilds the browse scope from the persisted
    /// <see cref="Playlist.SourceInstanceId"/> + <see cref="Playlist.ScopeCategoryId"/> (durable id;
    /// no in-memory <c>SourceState</c> needed — the source reconstructs the scope), then runs the
    /// stored search inside it. Falls back to a source-wide search if the scope can't be resolved.
    /// </summary>
    private async Task LoadScopedLivePlaylistAsync(Playlist playlist, string scopeInstance, string scopeId)
    {
        // Seed the browse stack with the rehydrated library-root node (SourceState = null; the source
        // rebuilds the scope from CategoryId), then run the scoped-search branch of DoSearch.
        IsGenericBrowsing = true;
        _browseStack.Clear();
        _browseStack.Add(new BrowseNode(
            playlist.ScopeTitle ?? playlist.Name,
            scopeInstance,
            scopeId,
            SourceState: null));
        await DoSearch(playlist.SearchTerm, scopeInstance);

        // The scoped search routed through the browse machinery (which set IsGenericBrowsing and
        // cleared the playlist flags). Now present the populated results AS the live playlist: this
        // restores the Queue All / Delete actions and makes Back return to the category list rather
        // than drilling back into the library's hubs.
        IsGenericBrowsing = false;
        _browseStack.Clear();
        UpdateBrowseBreadcrumb();
        IsViewingPlaylist = true;
        IsViewingLivePlaylist = true;
        ActivePlaylistName = playlist.Name;
        _activePlaylistId = playlist.Id;
        ActiveCategory = playlist.Name;
    }

    [RelayCommand]
    private async Task ShowCategoryListAsync()
    {
        ShowCategories = true;
        IsViewingPlaylist = false;
        IsViewingLivePlaylist = false;
        _isHistoryBrowsing = false;
        // Reset generic plug-in browse navigation.
        IsGenericBrowsing = false;
        _browseStack.Clear();
        _genericPaged = null;
        _genericPagedCategory = null;
        _genericPagedResolver = null;
        _genericPagedOffset = 0;
        UpdateBrowseBreadcrumb();
        ActiveCategory = "";
        SearchResults.Clear();
        CanLoadMore = false;
        _hasMoreResults = false;

        if (_searchEnumerator != null)
        {
            try { await _searchEnumerator.DisposeAsync(); }
            catch { /* enumerator may already be faulted */ }
            _searchEnumerator = null;
        }

        StatusText = "Select a category or search";
    }

    // ── Search ──

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
            return;

        // Capture query before RefreshSearchSuggestions, which modifies
        // the ComboBox ItemsSource and can clear SearchQuery via binding.
        var query = SearchQuery;

        _searchHistory.Add(query);
        RefreshSearchSuggestions();

        ShowCategories = false;
        IsViewingPlaylist = false;
        ActiveCategory = "Search";
        SearchQuery = query; // Restore in case binding cleared it
        // The ad-hoc search box honors the selected search source; tiles/live playlists don't.
        await DoSearch(query, ActiveSearchSourceId);
    }

    private void RefreshSearchSuggestions()
    {
        SearchSuggestions.Clear();
        foreach (var s in _searchHistory.Searches)
            SearchSuggestions.Add(s);
    }

    private async Task DoSearch(string query, string? sourceInstanceId = null)
    {
        // Cancel any in-progress load so the new search can proceed
        _searchCts.Cancel();
        _searchCts = new CancellationTokenSource();
        _isLoadingMore = false;

        IsSearching = true;
        StatusText = "Searching...";
        SearchResults.Clear();


        // A search from the box while browsing a source is scoped to the LIBRARY you entered
        // (the browse-stack root), not the specific node you've drilled into. Scoping to the deepest
        // node was the bug (searching "pink floyd" while inside the "Rush" artist found nothing);
        // scoping to the library root means searching "pink floyd" inside the Music library finds it,
        // and searching inside "Plex Concerts" stays within Concerts. Sources that expose in-view
        // search (IScopedSearchable, e.g. a Plex library) run the search inside that library node via
        // a scoped-search frame; sources that only search source-wide (ITextSearchCapable) route the
        // search to that source.
        if (IsGenericBrowsing && _browseStack.Count > 0)
        {
            var libraryRoot = _browseStack[0];
            var rootSource = _sourceRegistry?.ByInstance(libraryRoot.SourceInstanceId);

            if (rootSource is Phosphor.Plugin.Abstractions.IScopedSearchable)
            {
                // Reset the browse stack to the library root, then push a scoped-search frame so the
                // results are the in-library matches and Back returns to the library.
                _browseStack.Clear();
                _browseStack.Add(libraryRoot);

                // Record the scope so "save as live playlist" can persist + replay it.
                _currentSearchSourceId = libraryRoot.SourceInstanceId;
                _currentScopeCategoryId = libraryRoot.CategoryId;
                _currentScopeTitle = libraryRoot.Title;

                var searchFrame = new BrowseNode(
                    $"Search: {query}",
                    libraryRoot.SourceInstanceId,
                    libraryRoot.CategoryId,
                    libraryRoot.SourceState,
                    libraryRoot.Icon,
                    query);
                await EnterBrowseNodeAsync(searchFrame, pushOntoStack: true);
                return;
            }

            if (rootSource is Phosphor.Plugin.Abstractions.ITextSearchCapable)
            {
                // Source-wide search targeted at the browsed source (e.g. iHeartRadio, a local
                // folder). Present a flat result set, but keep the source's root tile on the stack and
                // push a display-only "Search: …" frame so the breadcrumb reads e.g.
                // "iHeartRadio › Search: hoda" and Back pops the search to return to the tile.
                sourceInstanceId = libraryRoot.SourceInstanceId;
                _browseStack.Clear();
                _browseStack.Add(libraryRoot);
                _browseStack.Add(new BrowseNode(
                    $"Search: {query}",
                    libraryRoot.SourceInstanceId,
                    libraryRoot.CategoryId,
                    libraryRoot.SourceState,
                    libraryRoot.Icon));
                _genericPaged = null;
                _genericPagedCategory = null;
                _genericPagedResolver = null;
                _genericPagedOffset = 0;
                UpdateBrowseBreadcrumb();
            }
        }

        if (_searchEnumerator != null)
        {
            try { await _searchEnumerator.DisposeAsync(); }
            catch { /* enumerator may already be faulted or in-flight */ }
            _searchEnumerator = null;
        }

        _currentSearchQuery = query;
        _currentSearchSourceId = sourceInstanceId;
        // This is a source-wide (non-scoped) search — clear any scope from a prior scoped search.
        _currentScopeCategoryId = null;
        _currentScopeTitle = null;

        // Parse and strip duration filters (min:/max:) from the query
        query = ParseDurationFilters(query);

        // Parse and strip a library: scope token (Plex server-side section filter).
        query = ParseLibraryFilter(query);

        // Check for playlist: prefix
        // Quoted form: playlist:"Classic Rock Hits" guitar → name=Classic Rock Hits, filter=guitar
        // Unquoted ID: playlist:PLxxxxxxx guitar → id=PLxxxxxxx, filter=guitar
        // Unquoted name: playlist:classic rock hits → searches "classic rock hits" as playlist name, no filter
        var playlistQuotedMatch = Regex.Match(query, @"playlist:""([^""]+)""", RegexOptions.IgnoreCase);
        var playlistMatch = !playlistQuotedMatch.Success
            ? Regex.Match(query, @"playlist:(.+)", RegexOptions.IgnoreCase)
            : null;
        // Check for channel: prefix (e.g. "godzilla channel:vpinworkshop")
        var channelMatch = Regex.Match(query, @"channel:(\S+)", RegexOptions.IgnoreCase);

        if (playlistQuotedMatch.Success)
        {
            var playlistIdOrName = playlistQuotedMatch.Groups[1].Value.Trim();
            var filterTerms = Regex.Replace(query, @"playlist:""[^""]+""", "", RegexOptions.IgnoreCase).Trim();

            try
            {
                // Resolve id / URL, or search by name (engine encapsulates the fallback).
                var playlistId = await ResolvePlaylistIdViaPluginOrLegacy(
                    playlistIdOrName, title => StatusText = $"Found playlist: {title}");
                if (playlistId == null)
                {
                    StatusText = $"Could not find playlist: {playlistIdOrName}";
                    IsSearching = false;
                    return;
                }

                var videos = GetPlaylistVideosViaPluginOrLegacy(playlistId);
                _searchEnumerator = string.IsNullOrEmpty(filterTerms)
                    ? videos.GetAsyncEnumerator()
                    : FilterVideosAsync(videos, filterTerms).GetAsyncEnumerator();
            }
            catch (Exception ex)
            {
                StatusText = $"Could not load playlist: {playlistIdOrName}";
                DebugLog.LogException("Playlist lookup", ex);
                IsSearching = false;
                return;
            }
        }
        else if (playlistMatch is { Success: true })
        {
            // Unquoted playlist: — everything after the prefix is the playlist name/ID, no filter
            var playlistIdOrName = playlistMatch.Groups[1].Value.Trim();
            var filterTerms = "";

            try
            {
                // If the token looks like a playlist id (or URL carrying one), text before
                // "playlist:" is the filter. Uses a lightweight shape check so the host needs no
                // YouTube library — the plug-in does the authoritative resolution below.
                bool parsedAsId = LooksLikeYouTubePlaylistId(playlistIdOrName);
                if (parsedAsId)
                    filterTerms = Regex.Replace(query, @"playlist:\S+", "", RegexOptions.IgnoreCase).Trim();

                var playlistId = await ResolvePlaylistIdViaPluginOrLegacy(
                    playlistIdOrName, title => StatusText = $"Found playlist: {title}");
                if (playlistId == null)
                {
                    StatusText = $"Could not find playlist: {playlistIdOrName}";
                    IsSearching = false;
                    return;
                }

                var videos = GetPlaylistVideosViaPluginOrLegacy(playlistId);
                _searchEnumerator = string.IsNullOrEmpty(filterTerms)
                    ? videos.GetAsyncEnumerator()
                    : FilterVideosAsync(videos, filterTerms).GetAsyncEnumerator();
            }
            catch (Exception ex)
            {
                StatusText = $"Could not load playlist: {playlistIdOrName}";
                DebugLog.LogException("Playlist lookup", ex);
                IsSearching = false;
                return;
            }
        }
        else if (channelMatch.Success)
        {
            var channelName = channelMatch.Groups[1].Value;
            var filterTerms = Regex.Replace(query, @"channel:\S+", "", RegexOptions.IgnoreCase).Trim();

            try
            {
                // Engine encapsulates the handle→user fallback.
                var videos = GetChannelUploadsViaPluginOrLegacy(channelName);
                _searchEnumerator = string.IsNullOrEmpty(filterTerms)
                    ? videos.GetAsyncEnumerator()
                    : FilterVideosAsync(videos, filterTerms).GetAsyncEnumerator();
            }
            catch (Exception ex)
            {
                StatusText = $"Could not find channel: {channelName}";
                DebugLog.LogException("Channel lookup", ex);
                IsSearching = false;
                return;
            }
        }
        else
        {
            // If the query carries structured filters (min:/max:/library:) and the target source can
            // apply them server-side, route through the filterable path and suppress the equivalent
            // client-side filtering for whatever the source claimed to handle.
            var filters = BuildSearchFilters();
            var filterableSource = filters.HasAny
                ? (sourceInstanceId != null ? _sourceRegistry?.ByInstance(sourceInstanceId) : _sourceRegistry?.YouTube)
                  as Phosphor.Plugin.Abstractions.IFilterableSearch
                : null;

            if (filterableSource != null)
            {
                var filtered = filterableSource.SearchFiltered(query, filters, _searchCts.Token);
                _searchEnumerator = MapPluginFilteredSearch(filterableSource, filtered).GetAsyncEnumerator();

                // The source applied these bounds server-side — don't re-scan client-side.
                if (filtered.Applied.MinDuration != null) _durationMin = null;
                if (filtered.Applied.MaxDuration != null) _durationMax = null;
            }
            else
            {
                _searchEnumerator = SearchVideosViaPluginOrLegacy(query, sourceInstanceId).GetAsyncEnumerator();
            }
        }
        _hasMoreResults = true;

        // Determine the result cache to attach from the active category or live playlist. The cache
        // is per-source and policy-driven: a source that opts out of result caching (ephemeral feeds)
        // attaches none; the YouTube-bound path (and other opt-in sources) attach the shared result
        // caches keyed by the globally-unique tile/playlist id.
        _activeResultCache = null;
        _categoryCacheName = null;
        _categoryCachePageIndex = 0;

        if (ShouldCacheResults(sourceInstanceId))
        {
            // A saved-search category tile: match by its stored search term, and (when the tile is
            // source-bound) the owning source. Host-only genre entries (legacy) carry no source id.
            var genreEntry = _genreCategories.FirstOrDefault(c =>
                !string.IsNullOrEmpty(c.SearchTerm) && c.SearchTerm == query
                && (c.SourceInstanceId ?? sourceInstanceId) == sourceInstanceId);
            if (genreEntry != null)
            {
                _categoryCacheName = genreEntry.Id;
                _activeResultCache = ResultPageCache;
            }
            else if (IsViewingLivePlaylist && !string.IsNullOrEmpty(_activePlaylistId))
            {
                _categoryCacheName = _activePlaylistId;
                _activeResultCache = ResultPageCache;
            }
        }

        await LoadMoreResults(SearchPageSize);
    }

    /// <summary>
    /// Parses min: and max: duration tokens from the search query.
    /// Accepts values like "5m", "1.5m", "1h", "0.5h", "90" (seconds).
    /// Sets _durationMin/_durationMax and returns the query with those tokens removed.
    /// </summary>
    private string ParseDurationFilters(string query)
    {
        _durationMin = null;
        _durationMax = null;
        _durationScanned = 0;

        query = Regex.Replace(query, @"min:(\S+)", m =>
        {
            _durationMin = ParseDurationValue(m.Groups[1].Value);
            return "";
        }, RegexOptions.IgnoreCase);

        query = Regex.Replace(query, @"max:(\S+)", m =>
        {
            _durationMax = ParseDurationValue(m.Groups[1].Value);
            return "";
        }, RegexOptions.IgnoreCase);

        return query.Trim();
    }

    /// <summary>
    /// Parses a <c>library:</c> scope token from the query into <see cref="_libraryFilter"/> and
    /// returns the query with that token removed. Supports a quoted name
    /// (<c>library:"Live Concerts"</c>) or a single unquoted word (<c>library:concerts</c>).
    /// </summary>
    private string ParseLibraryFilter(string query)
    {
        _libraryFilter = null;

        var quoted = Regex.Match(query, @"library:""([^""]+)""", RegexOptions.IgnoreCase);
        if (quoted.Success)
        {
            _libraryFilter = quoted.Groups[1].Value.Trim();
            return Regex.Replace(query, @"library:""[^""]+""", "", RegexOptions.IgnoreCase).Trim();
        }

        var unquoted = Regex.Match(query, @"library:(\S+)", RegexOptions.IgnoreCase);
        if (unquoted.Success)
        {
            _libraryFilter = unquoted.Groups[1].Value.Trim();
            return Regex.Replace(query, @"library:\S+", "", RegexOptions.IgnoreCase).Trim();
        }

        return query;
    }

    /// <summary>
    /// Builds a <see cref="Phosphor.Plugin.Abstractions.SearchFilters"/> from the currently-parsed
    /// duration bounds and <c>library:</c> scope. Call after <see cref="ParseDurationFilters"/> and
    /// <see cref="ParseLibraryFilter"/> have run for the active query.
    /// </summary>
    private Phosphor.Plugin.Abstractions.SearchFilters BuildSearchFilters()
        => new(_durationMin, _durationMax, _libraryFilter);

    /// <summary>
    /// Parses a duration string like "5m", "1.5h", or "90" (seconds) into a TimeSpan.
    /// </summary>
    private static TimeSpan? ParseDurationValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        value = value.Trim();

        if (value.EndsWith('h') || value.EndsWith('H'))
        {
            if (double.TryParse(value[..^1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var hours))
                return TimeSpan.FromHours(hours);
        }
        else if (value.EndsWith('m') || value.EndsWith('M'))
        {
            if (double.TryParse(value[..^1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var minutes))
                return TimeSpan.FromMinutes(minutes);
        }
        else if (value.EndsWith('s') || value.EndsWith('S'))
        {
            if (double.TryParse(value[..^1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                return TimeSpan.FromSeconds(seconds);
        }
        else if (double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var rawSeconds))
        {
            return TimeSpan.FromSeconds(rawSeconds);
        }

        return null;
    }

    /// <summary>
    /// Returns true if the video's duration passes the active min:/max: filters.
    /// Videos with no duration are excluded when a filter is active.
    /// </summary>
    private bool PassesDurationFilter(TimeSpan? duration)
    {
        if (_durationMin == null && _durationMax == null)
            return true;

        if (duration == null)
            return false;

        if (_durationMin != null && duration.Value < _durationMin.Value)
            return false;

        if (_durationMax != null && duration.Value > _durationMax.Value)
            return false;

        return true;
    }

    private static async IAsyncEnumerable<VideoItem> FilterVideosAsync(
        IAsyncEnumerable<VideoItem> source, string filter,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var terms = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        await foreach (var video in source.WithCancellation(ct))
        {
            var title = video.Title ?? "";
            if (terms.All(t => title.Contains(t, StringComparison.OrdinalIgnoreCase)))
                yield return video;
        }
    }

    // ── Scoped browsing ──

    /// <summary>
    /// Whether the current view <em>scopes</em> the search box to a specific browse context (a
    /// library/folder/collection), so the global search-source selector doesn't apply. Source-agnostic
    /// by design: today only Plex library browsing sets it, but any future scoped source (Jellyfin
    /// library, local-folder, …) should feed into this same signal rather than the UI checking a
    /// per-source flag. When a generic scoped-search capability lands (see dev_docs/PLUGIN_ARCHITECTURE_ANALYSIS.md),
    /// this becomes its natural home.
    /// </summary>
    public bool IsSearchScoped => IsGenericScopedSearchAvailable;

    /// <summary>
    /// True while browsing a generic plug-in node whose source can search — either in-view
    /// (<see cref="IScopedSearchable"/>, e.g. a Plex library) or source-wide
    /// (<see cref="ITextSearchCapable"/>, e.g. local folders). In both cases the search box is
    /// bound to that source, so the global search-source selector doesn't apply.
    /// </summary>
    private bool IsGenericScopedSearchAvailable =>
        IsGenericBrowsing && _browseStack.Count > 0
        && _sourceRegistry?.ByInstance(_browseStack[^1].SourceInstanceId)
           is Phosphor.Plugin.Abstractions.IScopedSearchable
              or Phosphor.Plugin.Abstractions.ITextSearchCapable;

    /// <summary>
    /// Whether the search-source dropdown is meaningful right now — false when the view scopes search
    /// to a context (<see cref="IsSearchScoped"/>). The UI greys the dropdown out in that case.
    /// </summary>
    public bool IsSearchSourceSelectable => !IsSearchScoped;

    /// <summary>
    /// Raises change notifications for the search-scope signals. Called after the browse stack is
    /// mutated (push/pop) since <see cref="IsGenericScopedSearchAvailable"/> reads the stack top —
    /// the <see cref="IsGenericBrowsing"/> setter alone fires too early (before the post-await push).
    /// </summary>
    private void RaiseSearchScopeChanged()
    {
        OnPropertyChanged(nameof(IsSearchScoped));
        OnPropertyChanged(nameof(IsSearchSourceSelectable));
    }

    // ── Category cache page tracking ──
    private ResultCache? _activeResultCache;
    private string? _categoryCacheName;
    private int _categoryCachePageIndex;

    // ── History pagination ──
    private const int HistoryPageSize = 50;
    private bool _isHistoryBrowsing;

    private void LoadMoreHistoryResults()
    {
        int loaded = SearchResults.Count;
        int total = _history.Entries.Count;
        var page = _history.Entries.Skip(loaded).Take(HistoryPageSize);

        foreach (var entry in page)
        {
            SearchResults.Add(new VideoItem
            {
                Title = entry.Title,
                Author = entry.Author,
                ThumbnailUrl = entry.ThumbnailUrl,
                VideoId = entry.VideoId,
            });
        }

        bool hasMore = SearchResults.Count < total;
        CanLoadMore = hasMore;
        _isHistoryBrowsing = hasMore;
        StatusText = hasMore
            ? $"Showing {SearchResults.Count} of {total} history items — scroll for more"
            : $"{total} items in history";
    }

    /// <summary>
    /// True when "Find Similar" should be hidden — for generic plug-in browse views (Plex libraries,
    /// folders, …) whose container/leaf items aren't YouTube-style "find similar" candidates.
    /// </summary>
    public bool ShouldHideFindSimilar => IsGenericBrowsing;

    // ── Generic plug-in browse navigation stack (source-agnostic drill-down + breadcrumb + back) ──
    // Each frame is one browse level; the top of the stack is the currently-displayed node. Used by
    // any IBrowsable source (local-folder, future Jellyfin, …) so drill-down/back/breadcrumb work
    // without source-specific state. Plex keeps its own path for now (retired in a later increment).
    private readonly List<BrowseNode> _browseStack = new();

    private string _browseBreadcrumb = "";
    /// <summary>Breadcrumb for the generic plug-in browse path (e.g. "Folder › Subfolder").</summary>
    public string BrowseBreadcrumb
    {
        get => _browseBreadcrumb;
        private set => SetProperty(ref _browseBreadcrumb, value);
    }

    private bool _isGenericBrowsing;
    /// <summary>True while viewing a generic plug-in browse node (enables Back + breadcrumb).</summary>
    public bool IsGenericBrowsing
    {
        get => _isGenericBrowsing;
        private set
        {
            if (SetProperty(ref _isGenericBrowsing, value))
            {
                OnPropertyChanged(nameof(IsSearchScoped));
                OnPropertyChanged(nameof(IsSearchSourceSelectable));
                OnPropertyChanged(nameof(ShouldHideFindSimilar));
            }
        }
    }

    private void UpdateBrowseBreadcrumb()
        => BrowseBreadcrumb = _browseStack.Count > 0
            ? string.Join(" › ", _browseStack.Select(n => n.Title))
            : "";

    [RelayCommand]
    private async Task LoadMoreResultsAsync()
    {
        if (IsGenericBrowsing && _genericPaged != null)
            await LoadMoreGenericPageAsync();
        else if (_isHistoryBrowsing)
            LoadMoreHistoryResults();
        else
            await LoadMoreResults(SearchPageSize);
    }

    private async Task LoadMoreResults(int count)
    {
        if (_searchEnumerator == null || !_hasMoreResults || _isLoadingMore)
            return;

        _isLoadingMore = true;
        IsSearching = true;
        var token = _searchCts.Token;

        try
        {
            // Try result cache first
            if (_activeResultCache is { Enabled: true } pc
                && _categoryCacheName != null)
            {
                var cached = pc.TryGetPage(_categoryCacheName, _categoryCachePageIndex, out var isLast);
                if (cached != null)
                {
                    foreach (var v in cached)
                    {
                        // Re-derive live favorite state from the owning source (the cached row carries
                        // CanFavorite + the source link, but IsFavorite must reflect current toggles).
                        if (v.CanFavorite && v.SourceInstanceId is { Length: > 0 } sid
                            && _sourceRegistry?.ByInstance(sid) is Phosphor.Plugin.Abstractions.IFavoritable fav)
                            v.IsFavorite = fav.IsFavorite(v.VideoId);
                        SearchResults.Add(v);
                    }
                    _categoryCachePageIndex++;
                    _hasMoreResults = !isLast;
                    CanLoadMore = _hasMoreResults;
                    StatusText = _hasMoreResults
                        ? $"Showing {SearchResults.Count} results (cached) — scroll for more"
                        : $"Showing all {SearchResults.Count} results (cached)";
                    return;
                }
            }

            int loaded = 0;
            while (loaded < count)
            {
                if (token.IsCancellationRequested)
                    break;

                bool moved;
                try
                {
                    moved = await _searchEnumerator.MoveNextAsync();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Network hiccup or disposed enumerator — stop paging
                    _hasMoreResults = false;
                    break;
                }

                if (!moved)
                {
                    _hasMoreResults = false;
                    break;
                }

                var video = _searchEnumerator.Current;
                if (video == null) continue;

                // Duration filter: skip items that don't match, with scan cap
                if (_durationMin != null || _durationMax != null)
                {
                    _durationScanned++;
                    if (!PassesDurationFilter(video.Duration))
                    {
                        if (_durationScanned >= MaxDurationScanCount)
                        {
                            _hasMoreResults = false;
                            StatusText = $"Scanned {_durationScanned} items — duration filter limit reached";
                        }
                        continue;
                    }
                }

                try
                {
                    SearchResults.Add(video);
                    loaded++;
                }
                catch
                {
                    // Skip individual items that can't be mapped
                }
            }

            CanLoadMore = _hasMoreResults;
            StatusText = _hasMoreResults
                ? $"Showing {SearchResults.Count} results — scroll for more"
                : $"Showing all {SearchResults.Count} results";

            // Store page in result cache and prefetch next page
            if (_activeResultCache is { Enabled: true } storeCache
                && _categoryCacheName != null && loaded > 0)
            {
                var pageItems = SearchResults.Skip(SearchResults.Count - loaded).Take(loaded).ToList();
                storeCache.StorePage(_categoryCacheName,
                    _categoryCachePageIndex, pageItems, !_hasMoreResults);
                _categoryCachePageIndex++;

                // Prefetch next page into cache (fetch 25 more without adding to UI)
                if (_hasMoreResults && _searchEnumerator != null)
                {
                    var prefetchItems = new List<VideoItem>();
                    int prefetched = 0;
                    while (prefetched < count)
                    {
                        if (token.IsCancellationRequested) break;

                        bool moved;
                        try { moved = await _searchEnumerator.MoveNextAsync(); }
                        catch { _hasMoreResults = false; break; }

                        if (!moved) { _hasMoreResults = false; break; }

                        var video = _searchEnumerator.Current;
                        if (video == null) continue;

                        // Apply duration filter to prefetch as well
                        if (_durationMin != null || _durationMax != null)
                        {
                            _durationScanned++;
                            if (!PassesDurationFilter(video.Duration))
                            {
                                if (_durationScanned >= MaxDurationScanCount)
                                { _hasMoreResults = false; break; }
                                continue;
                            }
                        }

                        try
                        {
                            prefetchItems.Add(video);
                            prefetched++;
                        }
                        catch { }
                    }

                    if (prefetchItems.Count > 0)
                    {
                        storeCache.StorePage(_categoryCacheName,
                            _categoryCachePageIndex, prefetchItems, !_hasMoreResults);
                        _categoryCachePageIndex++;
                        CanLoadMore = _hasMoreResults;
                        DebugLog.Log(LogLevel.Trace, "PlaylistPrefetch", $"Prefetched {prefetchItems.Count} items for next page");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Load failed: {ex.Message}";
            DebugLog.LogException("Load more results", ex);
        }
        finally
        {
            IsSearching = false;
            _isLoadingMore = false;
        }
    }

    // ── Find More Like This ──

    [RelayCommand]
    private async Task FindSimilarAsync(VideoItem? item)
    {
        if (item == null) return;

        var query = $"{item.Title} {item.Author}";
        SearchQuery = query;
        ShowCategories = false;
        IsViewingPlaylist = false;
        ActiveCategory = $"Similar to: {item.Title}";
        await DoSearch(query);
    }

    // ── Queue persistence ──

    private static readonly string QueuePath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "queue.json");

    private void SaveQueue()
    {
        try
        {
            var json = JsonSerializer.Serialize(SanitizeForPersist(Queue), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(QueuePath, json);
        }
        catch { }
    }

    /// <summary>
    /// Produces the queue list to persist, stripping the ephemeral resolved URLs from live-stream
    /// items so an expired URL is never written to disk. On restore those items re-resolve from their
    /// id at play time (see <see cref="ResolveAndPlayLiveAsync"/>); persisting the URL would make the
    /// player hand VLC a dead link and surface a misleading "stream timed out".
    /// </summary>
    private static List<VideoItem> SanitizeForPersist(IEnumerable<VideoItem> queue)
    {
        var list = new List<VideoItem>();
        foreach (var item in queue)
        {
            if (item.IsLiveStream && (item.StreamUrl != null || item.AudioStreamUrl != null))
            {
                var copy = item.ShallowCopy();
                copy.StreamUrl = null;
                copy.AudioStreamUrl = null;
                list.Add(copy);
            }
            else
            {
                list.Add(item);
            }
        }
        return list;
    }

    /// <summary>
    /// Persists the current queue to disk. Called on exit so metadata enriched during the
    /// session (upload date, accurate duration, chapters populated on play) survives a
    /// restart — the per-item enrichment does not raise <see cref="Queue"/>'s
    /// CollectionChanged, so it is not otherwise re-saved.
    /// </summary>
    public void SaveQueueState() => SaveQueue();

    private void LoadQueue()
    {
        if (!File.Exists(QueuePath)) return;
        try
        {
            var json = File.ReadAllText(QueuePath);
            var items = JsonSerializer.Deserialize<List<VideoItem>>(json);
            if (items != null)
                foreach (var item in items)
                    Queue.Add(item);
        }
        catch { }
    }

    // ── Queue & Playback ──

    [RelayCommand]
    private void ClearQueue()
    {
        Queue.Clear();
        QueueIndex = -1;
        StatusText = "Queue cleared";
    }

    private const int MaxQueueSize = 500;

    /// <summary>
    /// Plays a container (artist/album) as a unit: expands it to tracks, appends them to the queue,
    /// and plays from the first — the explicit "Play" affordance on a container row (the row's main
    /// button drills in by default). Works for both live browse containers and aggregated-favorite
    /// container rows (which rehydrate their node via the owning source's GetFavorite first).
    /// </summary>
    [RelayCommand]
    private async Task PlayContainerNow(VideoItem? item)
    {
        if (item is not { IsGenericContainer: true } || item.IsHeader) return;

        // Aggregated-favorite container carries no browse node — rehydrate it from the owning source.
        if (item.IsAggregatedFavorite)
        {
            var source = _sourceRegistry?.ByInstance(item.SourceInstanceId ?? "");
            if (source is Phosphor.Plugin.Abstractions.IFavoritable fav)
            {
                // GetFavorite may do blocking network I/O — never run it on the UI thread (a blocking
                // plug-in call there freezes the whole app and forces an ungraceful kill).
                var id = item.VideoId;
                var node = await Task.Run(() => fav.GetFavorite(id));
                if (node is { IsContainer: true })
                {
                    await PlayContainerAsync(ToContainerLeafItem(node, fav));
                    return;
                }
            }
            StatusText = $"Can't play {item.Title}: source unavailable.";
            return;
        }

        await PlayContainerAsync(item);
    }

    /// <summary>
    /// Toggles the favorite state of an item whose source supports favorites (<c>IFavoritable</c>).
    /// Routes to the owning source, flips the star, and refreshes the row. No-op for items whose
    /// source doesn't support favorites (the star isn't shown for those anyway).
    /// </summary>
    [RelayCommand]
    private void ToggleFavorite(VideoItem? item)
    {
        if (item == null || item.IsHeader || !item.CanFavorite) return;

        // Containers (Plex/Jellyfin artist/album) route via their generic browse identity; playable
        // rows route via SourceForItem. The favorited id is the row's VideoId in both cases.
        var isContainer = item.IsGenericContainer;
        var source = isContainer
            ? _sourceRegistry?.ByInstance(item.GenericSourceInstanceId ?? "")
            : SourceForItem(item);
        if (source is not Phosphor.Plugin.Abstractions.IFavoritable fav) return;

        var newState = !item.IsFavorite;
        fav.SetFavorite(item.VideoId, newState);
        item.IsFavorite = newState;

        // Plex needs the full item to replay a favorite without a server round-trip — hand it over on
        // star (leaf carries the token-bound StreamUrl; container carries its browse node identity).
        // Source-agnostic favorite capture (Plex/Jellyfin/Emby): hand the source a snapshot plus the
        // opaque browse state (container node OR leaf state) so it can rebuild the favorite on play.
        if (newState && source is Phosphor.Plugin.Abstractions.IFavoriteCapture capture)
            capture.RememberFavorite(new Phosphor.Plugin.Abstractions.FavoriteCapture(
                ItemId: item.VideoId,
                Title: item.Title,
                Subtitle: string.IsNullOrEmpty(item.Author) ? null : item.Author,
                ThumbnailUrl: string.IsNullOrEmpty(item.ThumbnailUrl) ? null : item.ThumbnailUrl,
                Duration: item.Duration,
                IsAudioOnly: item.IsAudioOnly,
                IsContainer: isContainer,
                ContainerState: item.GenericSourceState));

        // Write-through to the host-level aggregated index (drives the global Favorites tile).
        if (source is Phosphor.Plugin.Abstractions.IPhosphorSource src)
        {
            if (newState)
                _favoritesIndex.Add(new FavoriteEntry
                {
                    SourceInstanceId = src.InstanceId,
                    ItemId = item.VideoId,
                    Title = item.Title,
                    ThumbnailUrl = string.IsNullOrEmpty(item.ThumbnailUrl) ? null : item.ThumbnailUrl,
                    DurationSeconds = item.Duration?.TotalSeconds,
                    IsAudioOnly = item.IsAudioOnly,
                    IsLiveStream = item.IsLiveStream,
                    IsContainer = isContainer,
                    SourceLabel = src.DisplayName,
                });
            else
                _favoritesIndex.Remove(src.InstanceId, item.VideoId);
        }

        // If we're viewing the aggregated Favorites tile, drop an unfavorited row so it disappears live.
        if (!newState && item.IsAggregatedFavorite && IsViewingStaticPlaylist
            && ActivePlaylistName == "Favorites")
        {
            // Only remove if the row is still present — removing a stale item (e.g. a rapid
            // double-click / repeated unfavorite) can desync VirtualizingWrapPanel's size cache and
            // throw ArgumentOutOfRangeException. Guard + swallow-and-log so it never bubbles to the UI.
            if (SearchResults.Contains(item))
            {
                try
                {
                    SearchResults.Remove(item);
                }
                catch (Exception ex)
                {
                    DebugLog.LogException("ToggleFavorite: remove aggregated favorite row", ex);
                }
            }
            StatusText = $"Unfavorited: {item.Title} — {SearchResults.Count} favorite(s) remain";
            return;
        }

        StatusText = newState ? $"★ Favorited: {item.Title}" : $"Unfavorited: {item.Title}";
    }

    /// <summary>
    /// Removes a favorite identified by its composite index key (<c>sourceInstanceId\0itemId</c>) —
    /// used by the Settings ▸ DMD ▸ Favorites editor's delete button. Mirrors an un-star: unstars at
    /// the owning source (<see cref="Phosphor.Plugin.Abstractions.IFavoritable"/>) when available and
    /// removes the write-through index entry. Safe if the source is gone (index entry still removed).
    /// </summary>
    public void RemoveFavoriteByKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        var sep = key.IndexOf('\u0000');
        if (sep <= 0) return;
        var sourceInstanceId = key[..sep];
        var itemId = key[(sep + 1)..];

        if (_sourceRegistry?.ByInstance(sourceInstanceId) is Phosphor.Plugin.Abstractions.IFavoritable fav)
            fav.SetFavorite(itemId, false);

        _favoritesIndex.Remove(sourceInstanceId, itemId);
    }

    /// <summary>
    /// Builds the aggregated Favorites view (all sources) from the write-through index — instant, no
    /// per-source round-trips. Applies the user's Sort + Grouping (Settings → DMD → Favorites); a
    /// grouped view interleaves non-interactive provider header rows. Each row carries only display
    /// data + (source, id); playback rebuilds a resolvable item lazily via the owning source's
    /// <c>GetFavorite</c> (see the play path).
    /// </summary>
    private List<VideoItem> BuildAggregatedFavorites()
    {
        var grouping = this.FavoritesGrouping;
        var sort = this.FavoritesSort;

        // Custom = user-defined manual order (drag-to-reorder); ignores the Sort axis entirely.
        // Markers (separators / line breaks) are only meaningful in this mode.
        if (grouping == FavoritesGrouping.Custom)
        {
            var customRows = _favoritesIndex.AllCustomOrderedWithMarkers().Select(ToFavoriteRowOrMarker).ToList();
            EnrichFavoriteThumbnails(customRows);
            return customRows;
        }

        var entries = _favoritesIndex.All(); // index default order = recently-added first

        IEnumerable<FavoriteEntry> Sorted(IEnumerable<FavoriteEntry> src) => sort switch
        {
            FavoritesSort.Name => src.OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase),
            FavoritesSort.Source => src
                .OrderBy(e => e.SourceLabel, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => src, // RecentlyAdded — keep index order
        };

        List<VideoItem> result;
        if (grouping == FavoritesGrouping.Provider)
        {
            // Injected full-width header rows + items. Rendered as a single-column list (grouped mode
            // forces one column) so headers span and items stack beneath — the CollectionView-grouping
            // path is incompatible with the virtualizing wrap panel.
            result = new List<VideoItem>();
            foreach (var group in entries
                .GroupBy(e => e.SourceLabel)
                .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                result.Add(new VideoItem { IsHeader = true, HeaderText = group.Key });
                result.AddRange(Sorted(group).Select(ToFavoriteRow));
            }
        }
        else
        {
            // None — flat list ordered by the chosen sort.
            result = Sorted(entries).Select(ToFavoriteRow).ToList();
        }

        EnrichFavoriteThumbnails(result);
        return result;
    }

    /// <summary>
    /// Fills in missing thumbnails for aggregated-favorite rows whose owning source can resolve one
    /// lazily (e.g. a Twitch channel container has no stored thumbnail, but its live/most-recent-VOD
    /// preview does). Runs off-thread per source, updates the row live, and writes the resolved
    /// thumbnail back to the persisted index so subsequent opens are instant. For live sources the
    /// value is refreshed each open, so the tile tracks the current broadcast.
    /// </summary>
    private void EnrichFavoriteThumbnails(IEnumerable<VideoItem> rows)
    {
        var pending = rows
            .Where(r => !r.IsHeader && r.IsAggregatedFavorite
                        && !string.IsNullOrEmpty(r.SourceInstanceId)
                        && (r.IsLiveStream || r.IsGenericContainer || string.IsNullOrEmpty(r.ThumbnailUrl)))
            .ToList();
        if (pending.Count == 0) return;

        foreach (var row in pending)
        {
            var source = _sourceRegistry?.ByInstance(row.SourceInstanceId!);
            if (source is not Phosphor.Plugin.Abstractions.IReplayableById and not Phosphor.Plugin.Abstractions.IFavoritable)
                continue;

            _ = SafeFireAndForget(Task.Run(async () =>
            {
                Phosphor.Plugin.Abstractions.SourceItem? rebuilt =
                    (source as Phosphor.Plugin.Abstractions.IFavoritable)?.GetFavorite(row.VideoId)
                    ?? (source as Phosphor.Plugin.Abstractions.IReplayableById)?.RebuildPlayable(row.VideoId);
                var thumb = rebuilt?.ThumbnailUrl;
                if (string.IsNullOrEmpty(thumb) || thumb == row.ThumbnailUrl) return;

                await RunOnUiAsync(() =>
                {
                    row.ThumbnailUrl = thumb!;
                    row.NotifyPropertyChanged(nameof(VideoItem.ThumbnailUrl));
                });
                _favoritesIndex.UpdateThumbnail(row.SourceInstanceId!, row.VideoId, thumb);
            }));
        }
    }


    /// <summary>Maps an index <see cref="FavoriteEntry"/> to a display/play row for the Favorites view.</summary>
    private VideoItem ToFavoriteRow(FavoriteEntry e) => new()
    {
        Title = e.Title,
        Author = e.SourceLabel,
        ThumbnailUrl = e.ThumbnailUrl ?? "",
        VideoId = e.ItemId,
        SourceInstanceId = e.SourceInstanceId,
        Duration = e.DurationSeconds is { } s ? TimeSpan.FromSeconds(s) : null,
        IsAudioOnly = e.IsAudioOnly,
        IsLiveStream = e.IsLiveStream,
        IsAggregatedFavorite = true,
        // Containers (artist/album) expand to tracks on play; identity rehydrated via GetFavorite.
        IsGenericContainer = e.IsContainer,
        GenericSourceInstanceId = e.IsContainer ? e.SourceInstanceId : null,
        GenericCategoryId = e.IsContainer ? e.ItemId : null,
        CanFavorite = true,
        IsFavorite = true,
    };

    /// <summary>Maps a custom-order row (favorite or layout marker) to a display row.</summary>
    private VideoItem ToFavoriteRowOrMarker(FavoriteOrderRow row)
    {
        if (row.IsSeparator) return new VideoItem { IsSeparator = true };
        if (row.IsLineBreak) return new VideoItem { IsLineBreak = true };
        return ToFavoriteRow(row.Entry!);
    }

    [RelayCommand]
    private async Task AddToQueueAsync(VideoItem? item)
    {
        if (item == null || item.IsHeader) return;

        if (Queue.Count >= MaxQueueSize)
        {
            StatusText = $"Queue is full ({MaxQueueSize} items max)";
            return;
        }

        // Generic browse container (Plex artist/album, etc.) — expand to its playable tracks and
        // queue those, so the queue never holds an un-playable container row.
        if (item.IsGenericContainer)
        {
            StatusText = $"Loading tracks from {item.Title}...";
            try
            {
                var leaves = await ExpandContainerToLeavesAsync(item, _searchCts.Token);
                int added = 0;
                foreach (var track in leaves)
                {
                    if (Queue.Count >= MaxQueueSize) break;
                    Queue.Add(track);
                    added++;
                }
                StatusText = added < leaves.Count
                    ? $"Queued {added} of {leaves.Count} tracks (queue limit {MaxQueueSize})"
                    : $"Queued {leaves.Count} tracks from {item.Title}";
            }
            catch (Exception ex)
            {
                StatusText = $"Failed to queue: {ex.Message}";
                DebugLog.LogException("Queue container tracks", ex);
            }
            return;
        }

        Queue.Add(item);
        StatusText = $"Queued: {item.Title}";
    }

    private async Task<int> QueuePlayableItemsAsync(List<VideoItem> items)
    {
        int added = 0;
        foreach (var vi in items)
        {
            if (Queue.Count >= MaxQueueSize) break;

            // Containers (artist/album/hub/playlist) expand to their playable tracks via the generic
            // source-agnostic path; plain leaves queue directly.
            if (vi.IsGenericContainer)
            {
                var leaves = await ExpandContainerToLeavesAsync(vi, _searchCts.Token);
                foreach (var track in leaves)
                {
                    if (Queue.Count >= MaxQueueSize) break;
                    Queue.Add(track);
                    added++;
                }
            }
            else
            {
                Queue.Add(vi);
                added++;
            }
        }
        return added;
    }

    /// <summary>
    /// Optional "watch video" action for items that play audio by default but likely have a video
    /// version on YouTube (e.g. iHeart video-podcast episodes). Runs a best-effort first-match YouTube
    /// search from <see cref="VideoItem.VideoSearchQuery"/>; on a hit it plays the video, otherwise it
    /// silently falls back to the item's audio via <see cref="PlayNow"/>. Never guaranteed — that's why
    /// the default Play button (audio) stays the primary action.
    /// </summary>
    [RelayCommand]
    private async Task PlayVideoAlternativeAsync(VideoItem? item)
    {
        if (item == null || item.IsHeader) return;

        var query = item.VideoSearchQuery;
        if (string.IsNullOrWhiteSpace(query)
            || _sourceRegistry?.YouTube is not Phosphor.Plugin.Abstractions.ITextSearchCapable youtube)
        {
            PlayNow(item);
            return;
        }

        SetStatusPrefix("Finding video");
        StatusText = $"Looking for a video version of {item.Title}…";

        VideoItem? match = null;
        try
        {
            // Take only the first result — approximates yt-dlp's "ytsearch1" exact top match.
            await foreach (var found in youtube.SearchAsync(query!, _searchCts.Token))
            {
                match = ToVideoItem(found);
                break;
            }
        }
        catch (Exception ex)
        {
            DebugLog.LogException($"Video-alternative search '{query}'", ex);
        }

        if (match != null && !string.IsNullOrEmpty(match.VideoId))
        {
            match.Title = item.Title; // keep the podcast episode's title on screen
            PlayNow(match);
        }
        else
        {
            StatusText = $"No video found — playing audio: {item.Title}";
            PlayNow(item);
        }
    }

    [RelayCommand]
    private void PlayNow(VideoItem? item)
    {
        if (item == null || item.IsHeader) return;

        // Aggregated Favorites row (leaf OR container): carries only display data. Rebuild a resolvable
        // item via the owning source's IFavoritable.GetFavorite, then play. Checked before the generic
        // container branch so a favorited album plays (expand+queue) instead of drilling in.
        if (item.IsAggregatedFavorite)
        {
            _ = SafeFireAndForget(PlayAggregatedFavoriteAsync(item));
            return;
        }

        // Generic browse container.
        if (item.IsGenericContainer)
        {
            // In a browse view, activating a container drills into it. In a playlist view it's a
            // stored container (Plex artist/album) — expand it to tracks and play them.
            if (IsViewingPlaylist)
                _ = SafeFireAndForget(PlayContainerAsync(item));
            else
                _ = SafeFireAndForget(DrillIntoGenericContainerAsync(item));
            return;
        }

        PlayTransitioning = true;
        SetStatusPrefix("Transitioning");
        CurrentlyPlaying = item;
        StatusText = $"Playing: {item.Title}{item.AudioTag}";
        _history.Add(item);

        // Live/deferred items resolve lazily (a possibly-slow yt-dlp/proxy round-trip) BEFORE they can
        // start. Stop any current playback now so the previous track doesn't keep playing during the
        // resolve — and so a resolve that fails leaves silence, not the old track still audible.
        // Live streams ALWAYS re-resolve (their URLs are short-lived and must never be reused from a
        // cached/persisted StreamUrl), so they stop-and-resolve regardless of StreamUrl.
        if (item.IsLiveStream ||
            (item.StreamUrl == null &&
             (item.PendingLiveSourceItem != null || item.PendingResolveSourceItem != null)))
        {
            StopRequested?.Invoke();
        }

        // Live streams (e.g. Twitch, SiriusXM) resolve lazily at play time — resolve a fresh URL now,
        // then start playback. A cached/persisted StreamUrl is deliberately ignored: live URLs expire,
        // and after a restart the item is re-resolved from its id (see ResolveAndPlayLiveAsync).
        if (item.IsLiveStream)
        {
            _ = SafeFireAndForget(ResolveAndPlayLiveAsync(item));
            return;
        }

        // Deferred finite streams (e.g. Vimeo via yt-dlp) also resolve lazily at play time — one
        // yt-dlp probe now instead of one per search/browse row. Resolve then start playback.
        if (item.StreamUrl == null && item.PendingResolveSourceItem != null)
        {
            _ = SafeFireAndForget(ResolveAndPlayDeferredAsync(item));
            return;
        }

        PlayRequested?.Invoke(item.VideoId);

        // Fetch duration/chapters from the item's own source (source-agnostic). Fire-and-forget so
        // playback starts immediately; results apply to the now-playing item when they arrive.
        if (item.Chapters == null)
            _ = SafeFireAndForget(FetchChaptersViaSourceAsync(item));

        // Cache the item on playback when caching is enabled (cacheable sources only). DEFERRED until
        // playback is confirmed (NotifyPlaybackStarted): the background cache download runs a second
        // yt-dlp/HTTP fetch that, if started concurrently with stream resolution, can starve the
        // streaming path and trip the first-frame watchdog on long videos (full concerts). Recording it
        // here and kicking it off after first frame keeps startup contention off the play path. The
        // registry-ready gate + max-clip-length filter still apply inside the deferred kickoff.
        if (_cache is { Enabled: true } && IsItemCacheable(item))
        {
            _pendingCacheItem = item;
        }
        else
        {
            _pendingCacheItem = null;
            if (DebugLog.Enabled)
                DebugLog.Log(LogLevel.Debug, "VideoCache",
                    $"Not caching '{item.VideoId}': cacheEnabled={_cache?.Enabled == true}, cacheable={IsItemCacheable(item)}");
        }

        // Preemptively cache the *next* queue item as soon as this one starts —
        // gives the download a full track-length head start so the next transition
        // is effectively instant and the next track is also fully seekable.
        KickoffPreemptiveCacheForNext();
    }

    /// <summary>
    /// Resolves a live-stream item's playable URL on demand (SiriusXM channels are resolved at play
    /// time, not during browse), sets <see cref="VideoItem.StreamUrl"/>, and starts playback. The
    /// player checks <c>StreamUrl</c> first, so the URL must be set before <see cref="PlayRequested"/>.
    /// </summary>
    private async Task ResolveAndPlayLiveAsync(VideoItem item)
    {
        SetStatusPrefix("Tuning");
        var source = SourceForItem(item);
        if (source is not Phosphor.Plugin.Abstractions.IPlayableResolver resolver)
        {
            StatusText = $"Can't play {item.Title}: source unavailable.";
            PlayTransitioning = false;
            return;
        }

        // Prefer the opaque in-session SourceItem. After a restart it round-trips through JSON to a
        // JsonElement (not a SourceItem), so rebuild a fresh one from the persisted id — for a live
        // Twitch row the id is the channel login, so this resolves whatever is live NOW.
        var sourceItem = item.PendingLiveSourceItem as Phosphor.Plugin.Abstractions.SourceItem;
        if (sourceItem == null && source is Phosphor.Plugin.Abstractions.IReplayableById replayable)
        {
            var id = item.VideoId;
            sourceItem = await Task.Run(() => replayable.RebuildPlayable(id));
            if (sourceItem != null)
            {
                // Keep the rebuilt item for subsequent replays this session, and refresh the stale
                // display metadata (a live channel's current show differs from when it was queued).
                item.PendingLiveSourceItem = sourceItem;
                RefreshLiveMetadata(item, sourceItem);
            }
        }

        if (sourceItem == null)
        {
            StatusText = $"Can't tune {item.Title} — not live right now.";
            PlayTransitioning = false;
            return;
        }

        try
        {
            var stream = await resolver.ResolveAsync(
                sourceItem, new Phosphor.Plugin.Abstractions.PlaybackPreferences(), _searchCts.Token);
            if (stream?.PrimaryUri is { Length: > 0 } url)
            {
                item.StreamUrl = url;
                item.StartupTimeout = stream.StartupTimeout;
                // NB: do NOT clear PendingLiveSourceItem — live URLs expire, so the item must stay
                // re-resolvable for the next play (and the persisted StreamUrl is dropped on save).
                // Guard against the user having moved on while we were tuning.
                if (ReferenceEquals(CurrentlyPlaying, item))
                    PlayRequested?.Invoke(item.VideoId);
            }
            else
            {
                StatusText = $"Can't tune {item.Title} — stream unavailable.";
                NotifyPlaybackFailed(item);
                PlayTransitioning = false;
            }
        }
        catch (Exception ex)
        {
            DebugLog.LogException($"Live resolve '{item.VideoId}'", ex);
            StatusText = $"Can't tune {item.Title}: {ex.Message}";
            NotifyPlaybackFailed(item);
            PlayTransitioning = false;
        }
    }

    /// <summary>
    /// Refreshes a live item's display metadata (title/author/thumbnail) from a freshly-rebuilt
    /// <see cref="Phosphor.Plugin.Abstractions.SourceItem"/> so the "now playing" UI reflects the
    /// channel's <em>current</em> show rather than a stale title captured when it was queued.
    /// </summary>
    private static void RefreshLiveMetadata(
        VideoItem item, Phosphor.Plugin.Abstractions.SourceItem sourceItem)
    {
        if (!string.IsNullOrEmpty(sourceItem.Title) && sourceItem.Title != item.Title)
        {
            item.Title = sourceItem.Title;
            item.NotifyPropertyChanged(nameof(VideoItem.Title));
        }
        if (!string.IsNullOrEmpty(sourceItem.Subtitle) && sourceItem.Subtitle != item.Author)
        {
            item.Author = sourceItem.Subtitle!;
            item.NotifyPropertyChanged(nameof(VideoItem.Author));
        }
        if (!string.IsNullOrEmpty(sourceItem.ThumbnailUrl) && sourceItem.ThumbnailUrl != item.ThumbnailUrl)
        {
            item.ThumbnailUrl = sourceItem.ThumbnailUrl!;
            item.NotifyPropertyChanged(nameof(VideoItem.ThumbnailUrl));
        }
    }

    /// <summary>
    /// Resolves a deferred finite item's playable URL on demand (e.g. Vimeo, whose yt-dlp resolve is
    /// too expensive to run per search/browse row), sets <see cref="VideoItem.StreamUrl"/>, and starts
    /// playback. Mirrors <see cref="ResolveAndPlayLiveAsync"/> but without live semantics.
    /// </summary>
    private async Task ResolveAndPlayDeferredAsync(VideoItem item)
    {
        SetStatusPrefix("Resolving");
        var source = SourceForItem(item);
        if (source is not Phosphor.Plugin.Abstractions.IPlayableResolver resolver
            || item.PendingResolveSourceItem is not Phosphor.Plugin.Abstractions.SourceItem sourceItem)
        {
            StatusText = $"Can't play {item.Title}: source unavailable.";
            PlayTransitioning = false;
            return;
        }

        try
        {
            var stream = await resolver.ResolveAsync(
                sourceItem, new Phosphor.Plugin.Abstractions.PlaybackPreferences(), _searchCts.Token);
            if (stream?.PrimaryUri is { Length: > 0 } url)
            {
                _consecutiveResolveFailures = 0;
                item.StreamUrl = url;
                // Carry the separate audio-slave URL (yt-dlp SeparateVideoAudio) so the player can
                // attach it — otherwise a video-only primary plays with no audio.
                item.AudioStreamUrl = stream.Layout == Phosphor.Plugin.Abstractions.StreamLayout.SeparateVideoAudio
                    ? stream.AudioSlaveUri : null;
                item.IsAudioOnly = sourceItem.IsAudioOnly;
                item.PendingResolveSourceItem = null; // resolved
                // Guard against the user having moved on while we were resolving.
                if (ReferenceEquals(CurrentlyPlaying, item))
                    PlayRequested?.Invoke(item.VideoId);
            }
            else
            {
                // Definitive failure: the source produced no stream at all (e.g. DRM, removed).
                SkipUnresolvableDeferred(item, source, sourceItem,
                    Phosphor.Plugin.Abstractions.PlaybackFailureKind.Unresolvable,
                    $"Can't play {item.Title} — stream unavailable.");
            }
        }
        catch (Exception ex)
        {
            DebugLog.LogException($"Deferred resolve '{item.VideoId}'", ex);
            // An exception is treated as transient (network/timeout/outage) — do NOT mark unplayable.
            SkipUnresolvableDeferred(item, source, sourceItem,
                Phosphor.Plugin.Abstractions.PlaybackFailureKind.Transient,
                $"Can't play {item.Title}: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles a deferred item that couldn't be resolved (e.g. a DRM-protected or preview-only
    /// SoundCloud track): logs the reason and, when the failing track is part of <em>queue</em>
    /// playback, auto-advances to the next queued track instead of dead-stopping. An <em>ad-hoc</em>
    /// play (the user clicked Play on a browse/search row without queuing it) just stops — there's no
    /// queue flow to continue. Bounded by <see cref="MaxConsecutiveResolveSkips"/> so a long run of
    /// unresolvable queued tracks can't spin forever.
    /// <para>
    /// Also reports the failure back to the owning source when it implements
    /// <see cref="Phosphor.Plugin.Abstractions.IPlaybackReportable"/>: the source decides (based on
    /// <paramref name="kind"/>) whether the item is now permanently unplayable, and if so the row is
    /// flipped live to its unplayable state (buttons removed, indicator shown) rather than hidden.
    /// </para>
    /// </summary>
    private void SkipUnresolvableDeferred(
        VideoItem item,
        object? source,
        Phosphor.Plugin.Abstractions.SourceItem sourceItem,
        Phosphor.Plugin.Abstractions.PlaybackFailureKind kind,
        string reason)
    {
        // Let the owning source learn from the failure. It (not the host) knows why the resolve failed,
        // so it returns whether the item is now known-unplayable; only then do we flip the row live.
        if (source is Phosphor.Plugin.Abstractions.IPlaybackReportable reportable)
        {
            try
            {
                if (reportable.ReportPlaybackFailure(sourceItem.ItemId, kind))
                    item.IsPlayable = false;
            }
            catch (Exception ex)
            {
                DebugLog.LogException($"ReportPlaybackFailure '{item.VideoId}'", ex);
            }
        }

        // Whether this play is driven by the queue: the failing item is the one at the current queue
        // index. Ad-hoc plays (PlayNow directly, e.g. a browse/search row or an aggregated favorite)
        // don't move QueueIndex to the item, so this is false and we stop instead of skipping.
        bool isQueuePlayback = _queueIndex >= 0 && _queueIndex < Queue.Count
            && ReferenceEquals(Queue[_queueIndex], item);

        // Stop (don't skip) for ad-hoc plays, if the user moved on, or if there's nowhere to advance.
        if (!isQueuePlayback || !ReferenceEquals(CurrentlyPlaying, item) || !HasNextTrack)
        {
            _consecutiveResolveFailures = 0;
            StatusText = reason;
            PlayTransitioning = false;
            return;
        }

        if (++_consecutiveResolveFailures > MaxConsecutiveResolveSkips)
        {
            _consecutiveResolveFailures = 0;
            StatusText = $"{reason} Stopped after several unplayable tracks.";
            PlayTransitioning = false;
            return;
        }

        StatusText = $"{reason} Skipping…";
        PlayNext();
    }

    /// <summary>
    /// Plays an aggregated-favorite row by rebuilding a resolvable item from its owning source, then
    /// routing it through the normal play path. The index row carries only display data; the owning
    /// source's <c>GetFavorite(id)</c> restores the opaque SourceState needed to resolve/play.
    /// </summary>
    private async Task PlayAggregatedFavoriteAsync(VideoItem favRow)
    {
        SetStatusPrefix("Resolving");
        var source = _sourceRegistry?.ByInstance(favRow.SourceInstanceId ?? "");
        if (source is not Phosphor.Plugin.Abstractions.IFavoritable fav)
        {
            StatusText = $"Can't play {favRow.Title}: source unavailable.";
            return;
        }

        var sourceItem = await Task.Run(() => fav.GetFavorite(favRow.VideoId));
        if (sourceItem is null)
        {
            StatusText = $"Can't play {favRow.Title}: no longer available.";
            return;
        }

        // Container favorite (artist/album): DRILL IN to show its catalog (albums/tracks) rather than
        // play everything — a favorited artist is a shortcut to the catalog, not a "play 489 tracks"
        // action. The user then plays an album/track from there (an album's Play still queues all its
        // tracks). The node identity is rehydrated from the source's GetFavorite.
        if (sourceItem.IsContainer)
        {
            var container = ToContainerLeafItem(sourceItem, fav);
            SetStatusPrefix("");
            await DrillIntoGenericContainerAsync(container);
            return;
        }

        // Leaf favorite: rebuild a fully-playable VideoItem (resolving / deferring like browse), then play.
        var resolver = source as Phosphor.Plugin.Abstractions.IPlayableResolver;
        var vi = await ResolveLeafAsync(sourceItem, resolver, _searchCts.Token);
        PlayNow(vi);
    }

    /// <summary>
    /// Expands a stored browse container (a Plex artist/album in a playlist) to its tracks, appends
    /// them to the queue, and starts playing the first one — a container can't be played directly.
    /// </summary>
    private async Task PlayContainerAsync(VideoItem container)
    {
        StatusText = $"Loading tracks from {container.Title}...";
        List<VideoItem> leaves;
        try
        {
            leaves = await ExpandContainerToLeavesAsync(container, _searchCts.Token);
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to play {container.Title}: {ex.Message}";
            DebugLog.LogException("Play container", ex);
            return;
        }

        if (leaves.Count == 0)
        {
            StatusText = $"No playable tracks in {container.Title}";
            return;
        }

        // Source-defined "Play all" policy: a recency feed (e.g. a Twitch channel) plays only the
        // latest leaf — the live feed injected at offset 0, else the most recent VOD — rather than
        // queueing the whole back-catalog. Sources without the capability default to QueueAll.
        var source = _sourceRegistry?.ByInstance(container.GenericSourceInstanceId ?? "");
        if (source is Phosphor.Plugin.Abstractions.IContainerPlayPolicy policy)
        {
            var containerItem = new Phosphor.Plugin.Abstractions.SourceItem
            {
                SourceInstanceId = container.GenericSourceInstanceId ?? "",
                ItemId = container.GenericCategoryId ?? container.VideoId,
                Title = container.Title,
                IsContainer = true,
                SourceState = container.GenericSourceState,
            };
            if (policy.GetPlayAllBehavior(containerItem) == Phosphor.Plugin.Abstractions.ContainerPlayAll.PlayLatestOnly)
                leaves = leaves.Take(1).ToList();
        }

        int firstIndex = Queue.Count;
        foreach (var track in leaves)
        {
            if (Queue.Count >= MaxQueueSize) break;
            Queue.Add(track);
        }

        if (firstIndex < Queue.Count)
            PlayFromQueueIndex(firstIndex);
    }

    /// <summary>
    /// Returns "" for non-Plex items, "(Stereo)" for native stereo selection,
    /// "(Stereo Transcode)" for server-side downmix, or "(Surround)" otherwise.
    /// </summary>
    [RelayCommand]
    private void StopPlayback()
    {
        StopRequested?.Invoke();
        PlayTransitioning = false;
        _statusPrefixCts?.Cancel();
        StatusPrefix = "";
        CurrentlyPlaying = null;
        QueueIndex = -1;
        IsPaused = false;
        PlaybackPosition = 0;
        PlaybackDuration = 1;
        StatusText = "Playback stopped";
    }

    /// <summary>
    /// If something is playing, stop it. Otherwise start the queue
    /// (or play the given fallback item if the queue is empty).
    /// </summary>
    private int _lastPlayedQueueIndex = -1;

    /// <summary>
    /// Set to true when PlayNext() triggers a queue transition (not a direct selection).
    /// Consumed and cleared by the playback-started handler.
    /// </summary>
    public bool IsQueueTransition { get; set; }

    [RelayCommand]
    private void TogglePlayStop(VideoItem? fallbackItem)
    {
        if (IsPlaying)
        {
            _lastPlayedQueueIndex = _queueIndex;
            StopPlayback();
        }
        else if (fallbackItem != null)
        {
            PlayNow(fallbackItem);
        }
        else if (Queue.Count > 0)
        {
            int resumeIndex = _lastPlayedQueueIndex >= 0 && _lastPlayedQueueIndex < Queue.Count
                ? _lastPlayedQueueIndex
                : 0;
            _lastPlayedQueueIndex = -1;
            PlayFromQueueIndex(resumeIndex);
        }
    }

    [RelayCommand]
    private void PausePlayback()
    {
        if (!IsPlaying) return;
        if (IsPaused)
        {
            ResumePlayback();
            return;
        }
        PauseRequested?.Invoke();
        IsPaused = true;
        StatusText = "Paused";
    }

    [RelayCommand]
    private void ResumePlayback()
    {
        if (!IsPlaying || !IsPaused) return;
        ResumeRequested?.Invoke();
        IsPaused = false;
        StatusText = $"Playing: {CurrentlyPlaying?.Title}{CurrentlyPlaying?.AudioTag}";
    }

    [RelayCommand]
    private void Play()
    {
        if (IsPaused)
        {
            ResumePlayback();
        }
        else if (!IsPlaying && Queue.Count > 0)
        {
            if (_queueIndex >= 0 && _queueIndex < Queue.Count)
                PlayFromQueueIndex(_queueIndex);
            else
            {
                QueueIndex = -1; // Reset so PlayNext starts at 0
                PlayNext();
            }
        }
    }

    /// <summary>
    /// Plays the queue item at the specified index, stopping any current playback first.
    /// </summary>
    public void PlayFromQueueIndex(int index)
    {
        if (index < 0 || index >= Queue.Count) return;
        if (_playTransitioning) return;

        // Do NOT pre-emptively stop here: PlayNow -> the player handles the transition from the current
        // track (and PlayNow issues its own stop for live/deferred items that resolve first). A
        // pre-emptive StopRequested races the new play on the Backglass thread and briefly flashes the
        // idle blob overlay between tracks; the natural-end path never stops first and is clean.
        QueueIndex = index;
        PlayNow(Queue[index]);

        if (_autoDjEnabled)
            _ = SafeFireAndForget(AutoDjFillQueue());
    }

    [RelayCommand]
    private void RemoveFromQueue(VideoItem? item)
    {
        if (item == null) return;
        int idx = Queue.IndexOf(item);
        if (idx < 0) return;

        // Adjust QueueIndex if removing an item at or before current position
        if (idx < _queueIndex)
            QueueIndex--;
        else if (idx == _queueIndex)
        {
            // Removing the currently playing item — stop or advance
            Queue.Remove(item);
            if (_queueIndex >= Queue.Count)
                QueueIndex = Queue.Count > 0 ? Queue.Count - 1 : -1;
            OnPropertyChanged(nameof(CurrentQueueItem));
            return;
        }

        Queue.Remove(item);
        OnPropertyChanged(nameof(CurrentQueueItem));
    }

    [RelayCommand]
    private void Skip()
    {
        var chapters = _currentlyPlaying?.Chapters;
        if (chapters != null && chapters.Count > 0 && _playbackDuration > 1)
        {
            int currentChapter = GetCurrentChapterIndex(chapters);
            if (currentChapter < chapters.Count - 1)
            {
                // Seek to next chapter
                var nextStart = (long)chapters[currentChapter + 1].StartTime.TotalMilliseconds;
                PlayTransitioning = true;
                SeekRequested?.Invoke(nextStart);
                return;
            }
        }

        // Last chapter or no chapters — skip to next queue item. Do NOT pre-emptively stop here: a
        // track *change* is a transition, not a stop-to-idle. PlayNext -> PlayNow -> the player already
        // handles stopping the old media as part of the transition (and PlayNow issues its own stop for
        // live/deferred items that resolve before playing). A pre-emptive StopRequested races the new
        // play on the Backglass thread and briefly flashes the idle blob overlay between tracks — the
        // natural-end path (which never stops first) is clean, so mirror it.
        PlayNext();
    }

    /// <summary>
    /// Returns the index of the chapter that contains the current playback position.
    /// Falls back to the last chapter if position is beyond all chapter starts.
    /// </summary>
    private int GetCurrentChapterIndex(List<ChapterMarker> chapters)
    {
        var posMs = PlaybackPosition;
        for (int i = chapters.Count - 1; i >= 0; i--)
        {
            if (posMs >= chapters[i].StartTime.TotalMilliseconds)
                return i;
        }
        return 0;
    }

    [RelayCommand]
    private void PreviousTrack()
    {
        if (!IsPlaying) return;

        var chapters = _currentlyPlaying?.Chapters;
        if (chapters != null && chapters.Count > 0 && _playbackDuration > 1)
        {
            int currentChapter = GetCurrentChapterIndex(chapters);
            var chapterStartMs = chapters[currentChapter].StartTime.TotalMilliseconds;
            bool isNearChapterStart = (PlaybackPosition - chapterStartMs) < 10000;

            if (!isNearChapterStart)
            {
                // Restart current chapter
                PlayTransitioning = true;
                SeekRequested?.Invoke((long)chapterStartMs);
                return;
            }
            else if (currentChapter > 0)
            {
                // Jump to previous chapter
                PlayTransitioning = true;
                SeekRequested?.Invoke((long)chapters[currentChapter - 1].StartTime.TotalMilliseconds);
                return;
            }
            // First chapter and near start — fall through to previous queue item logic
        }

        // No chapters — original behavior
        if (Queue.Count == 0) return;
        int currentIdx = _queueIndex;
        bool isFirstItem = currentIdx <= 0;
        bool isBeyond10Seconds = PlaybackPosition >= 10000;

        if (isBeyond10Seconds || isFirstItem)
        {
            SeekRequested?.Invoke(0);
        }
        else
        {
            PlayFromQueueIndex(currentIdx - 1);
        }
    }

    /// <summary>
    /// Returns true if there is a next track available to play (considering repeat and AutoDJ).
    /// Used by the backglass to decide whether to show the idle screen between tracks.
    /// </summary>
    public bool HasNextTrack
    {
        get
        {
            if (Queue.Count == 0) return false;
            int nextIndex = _queueIndex + 1;
            if (nextIndex < Queue.Count) return true;
            if (_repeatEnabled && Queue.Count > 0) return true;
            if (_autoDjEnabled) return true;
            return false;
        }
    }

    public void PlayNext()
    {
        _prefetchingVideoId = null;

        if (Queue.Count == 0)
        {
            CurrentlyPlaying = null;
            QueueIndex = -1;
            StatusText = "Queue empty";
            return;
        }

        int nextIndex = _queueIndex + 1;

        if (nextIndex >= Queue.Count)
        {
            if (_repeatEnabled)
            {
                nextIndex = 0; // Wrap around to start
            }
            else
            {
                CurrentlyPlaying = null;
                StatusText = "Queue finished";
                // Keep QueueIndex pointing at the last item (stays highlighted)
                if (_autoDjEnabled)
                    _ = SafeFireAndForget(AutoDjFillQueue());
                return;
            }
        }

        IsQueueTransition = true;
        QueueIndex = nextIndex;
        PlayNow(Queue[nextIndex]);

        if (_autoDjEnabled)
            _ = SafeFireAndForget(AutoDjFillQueue());
    }

    /// <summary>
    /// Advances the queue index and updates state for a gapless transition
    /// (the BackglassWindow has already started playback on a pre-loaded MediaPlayer).
    /// </summary>
    public void AdvanceQueueGapless()
    {
        _prefetchingVideoId = null;

        int nextIndex = _queueIndex + 1;
        if (nextIndex >= Queue.Count && _repeatEnabled && Queue.Count > 0)
            nextIndex = 0;
        if (nextIndex < 0 || nextIndex >= Queue.Count) return;

        IsQueueTransition = true;
        QueueIndex = nextIndex;
        var item = Queue[nextIndex];
        CurrentlyPlaying = item;
        StatusText = $"Playing: {item.Title}{item.AudioTag}";
        _history.Add(item);
        PlayTransitioning = false;
        _statusPrefixCts?.Cancel();
        StatusPrefix = "";

        // Fetch chapters for the transitioned track from its own source (source-agnostic).
        if (item.Chapters == null)
            _ = SafeFireAndForget(FetchChaptersViaSourceAsync(item));

        if (_autoDjEnabled)
            _ = SafeFireAndForget(AutoDjFillQueue());
    }

    // ── Playlists ──

    /// <summary>
    /// All playlists (static and live) — used by the playlist picker dropdown.
    /// </summary>
    public IReadOnlyList<Playlist> AllPlaylists =>
        _playlists.Playlists.ToList();

    /// <summary>
    /// Only static playlists — used by the "Add to Playlist" picker so live playlists are excluded.
    /// </summary>
    public IReadOnlyList<Playlist> StaticPlaylists =>
        _playlists.Playlists.Where(p => p.Kind == PlaylistKind.Static).ToList();

    /// <summary>
    /// Add a video to the active playlist (or Favorites if none selected).
    /// </summary>
    [RelayCommand]
    private async Task AddToPlaylistAsync(VideoItem? item)
    {
        if (item == null) return;

        // Containers (Plex artist/album, etc.) expand to their playable tracks via the generic
        // source-agnostic path; each track is added to the playlist and optionally cached.
        if (item.IsGenericContainer)
        {
            StatusText = $"Loading tracks from {item.Title}...";
            try
            {
                var tracks = await ExpandContainerToLeavesAsync(item, _searchCts.Token);
                foreach (var track in tracks)
                {
                    _playlists.AddToPlaylist(ActivePlaylistName, track);
                    if (_cache is { Enabled: true } && IsItemCacheable(track))
                        _ = SafeFireAndForget(_cache.CacheVideoAsync(track.VideoId, duration: track.Duration, chapters: track.Chapters, title: track.Title));
                }
                StatusText = $"Added {tracks.Count} tracks from {item.Title} to {ActivePlaylistName}";
            }
            catch (Exception ex)
            {
                StatusText = $"Failed to add to playlist: {ex.Message}";
                DebugLog.LogException("Add to playlist", ex);
            }
            return;
        }

        _playlists.AddToPlaylist(ActivePlaylistName, item);
        StatusText = $"Added to {ActivePlaylistName}: {item.Title}";

        // Fetch accurate duration and save to playlist JSON
        _ = SafeFireAndForget(RefreshPlaylistItemDurationAsync(ActivePlaylistName, item));

        // Trigger background caching for playlist items (cacheable sources only)
        if (_cache is { Enabled: true } && IsItemCacheable(item))
            _ = SafeFireAndForget(_cache.CacheVideoAsync(item.VideoId, duration: item.Duration, chapters: item.Chapters, title: item.Title));
    }

    /// <summary>
    /// Remove a video from a specific playlist.
    /// </summary>
    [RelayCommand]
    private void RemoveFromPlaylist(VideoItem? item)
    {
        if (item == null) return;
        _playlists.RemoveFromPlaylist(ActivePlaylistName, item);

        // If viewing this playlist, also remove from results
        var inResults = SearchResults.FirstOrDefault(v => v.VideoId == item.VideoId);
        if (inResults != null)
            SearchResults.Remove(inResults);

        StatusText = $"Removed from {ActivePlaylistName}: {item.Title}";
    }

    [RelayCommand]
    private void CreatePlaylist(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            // Default to currently playing title or generic name
            name = CurrentlyPlaying?.Title ?? $"Playlist {_playlists.Playlists.Count}";
        }

        _playlists.GetOrCreate(name);
        ActivePlaylistName = name;
        RebuildCategories();
        StatusText = $"Created playlist: {name}";
    }

    public void CreatePlaylistWithIcon(string name, string icon)
    {
        _playlists.GetOrCreate(name, icon);
        ActivePlaylistName = name;
        RebuildCategories();
        StatusText = $"Created playlist: {name}";
    }

    [RelayCommand]
    private void CreateLivePlaylist(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(SearchQuery))
            return;

        _playlists.CreateLivePlaylist(name, SearchQuery);
        RebuildCategories();
        StatusText = $"Created live playlist: {name}";
    }

    public void CreateLivePlaylistWithIcon(string name, string icon)
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
            return;

        // Bind the playlist to the source the current search actually ran against (null = YouTube),
        // and — for an in-library (scoped) search — the durable scope, so re-opening it replays the
        // same scoped search rather than a source-wide or default query.
        _playlists.CreateLivePlaylist(name, SearchQuery, icon, _currentSearchSourceId,
            _currentScopeCategoryId, _currentScopeTitle);
        RebuildCategories();
        StatusText = $"Created live playlist: {name}";
    }

    [RelayCommand]
    private void ClearPlaylist()
    {
        var playlist = _playlists.Playlists.FirstOrDefault(p => p.Name == ActivePlaylistName);
        if (playlist == null) return;

        playlist.Videos.Clear();
        _playlists.Save();
        SearchResults.Clear();
        StatusText = $"Cleared all videos from {ActivePlaylistName}";
    }

    [RelayCommand]
    private void DeletePlaylist(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "Favorites") return;
        _playlists.DeletePlaylist(name);
        if (ActivePlaylistName == name)
            ActivePlaylistName = "Favorites";
        RebuildCategories();
        StatusText = $"Deleted playlist: {name}";
    }

    /// <summary>
    /// Queue all videos currently shown in the results list.
    /// </summary>
    [RelayCommand]
    private async Task QueueAllFromPlaylist()
    {
        if (SearchResults.Count == 0) return;

        int added = 0;
        foreach (var video in SearchResults.ToList())
        {
            if (Queue.Count >= MaxQueueSize) break;
            // A stored container (Plex artist/album in a playlist) expands to its tracks.
            if (video.IsGenericContainer)
            {
                var leaves = await ExpandContainerToLeavesAsync(video, _searchCts.Token);
                foreach (var track in leaves)
                {
                    if (Queue.Count >= MaxQueueSize) break;
                    Queue.Add(track);
                    added++;
                }
            }
            else
            {
                Queue.Add(video);
                added++;
            }
        }

        StatusText = $"Queued {added} videos from {ActiveCategory}";
    }

    /// <summary>
    /// Randomize the order of the queue.
    /// </summary>
    [RelayCommand]
    private void ShuffleQueue()
    {
        if (Queue.Count < 2) return;

        // Remember the currently playing item
        var currentItem = CurrentQueueItem;

        var items = Queue.ToList();
        Queue.Clear();

        // Fisher-Yates shuffle
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = _autoDjRng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }

        foreach (var item in items)
            Queue.Add(item);

        // Restore QueueIndex to point at the same item
        if (currentItem != null)
            QueueIndex = Queue.IndexOf(currentItem);

        StatusText = $"Shuffled {Queue.Count} items in queue";
    }

    // ── AutoDJ ──

    private const int AutoDjRefillThreshold = 5;
    private const int AutoDjBatchSize = 10;
    private readonly HashSet<string> _autoDjUsedIds = new();
    private readonly Random _autoDjRng = new();

    private async Task AutoDjFillQueue()
    {
        if (_isAutoDjFilling || !_autoDjEnabled) return;

        // Only refill when fewer than 5 items remain ahead of the current position
        int remaining = Queue.Count - Math.Max(_queueIndex, 0);
        if (remaining >= AutoDjRefillThreshold && Queue.Count > 0) return;

        _isAutoDjFilling = true;
        int targetSize = Queue.Count + AutoDjBatchSize;

        try
        {
            // Check if we're in a genre category
            var genreEntry = _genreCategories.FirstOrDefault(c =>
                c.Name == ActiveCategory && !string.IsNullOrEmpty(c.SearchTerm));

            if (genreEntry != null)
            {
                var genreCat = new Category { Name = genreEntry.Name, Icon = genreEntry.Icon, SearchTerm = genreEntry.SearchTerm };
                await AutoDjFromGenre(genreCat, targetSize);
            }
            else
                await AutoDjFromVideo(targetSize);

            if (_autoDjEnabled && Queue.Count > 0)
                StatusText = $"AutoDJ active — {Queue.Count} in queue";
        }
        finally
        {
            _isAutoDjFilling = false;
        }
    }

    private async Task AutoDjFromGenre(Category genre, int targetSize)
    {
        StatusText = $"AutoDJ: browsing {genre.Name}...";

        try
        {
            // Load a larger pool from this genre to pick randomly from
            var results = new List<VideoItem>();
            var enumerator = SearchVideosViaPluginOrLegacy(genre.SearchTerm, AutoDjProviderId).GetAsyncEnumerator();
            try
            {
                int fetched = 0;
                while (fetched < 50 && await enumerator.MoveNextAsync())
                {
                    results.Add(enumerator.Current);
                    fetched++;
                }
            }
            finally
            {
                try { await enumerator.DisposeAsync(); }
                catch { /* enumerator may be faulted */ }
            }

            // Shuffle and pick items not already queued/played
            var shuffled = results.OrderBy(_ => _autoDjRng.Next()).ToList();

            await RunOnUiAsync(() =>
            {
                foreach (var item in shuffled)
                {
                    if (Queue.Count >= targetSize) break;
                    var videoId = item.VideoId;
                    if (_autoDjUsedIds.Contains(videoId)) continue;
                    if (Queue.Any(q => q.VideoId == videoId)) continue;
                    if (CurrentlyPlaying?.VideoId == videoId) continue;

                    Queue.Add(item);
                    _autoDjUsedIds.Add(videoId);
                    StatusText = $"AutoDJ queued: {item.Title}";

                    if (CurrentlyPlaying == null)
                        PlayNext();
                }
            });
        }
        catch (Exception ex)
        {
            DebugLog.LogException("AutoDJ genre", ex);
        }
    }

    private async Task AutoDjFromVideo(int targetSize)
    {
        // Use the title of the currently playing (or most recent) track to find similar content,
        // since the channel/author name often doesn't reflect the actual music.
        string? query = CurrentlyPlaying?.Title;

        if (string.IsNullOrWhiteSpace(query))
        {
            query = _history.Entries
                .Select(e => e.Title)
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            StatusText = "AutoDJ: no track info available";
            return;
        }

        StatusText = $"AutoDJ: finding similar to {query}...";

        try
        {
            // Fetch a page of results and randomize so we don't always pick the same top results
            var pool = new List<VideoItem>();
            var enumerator = SearchVideosViaPluginOrLegacy(query, AutoDjProviderId).GetAsyncEnumerator();
            try
            {
                int fetched = 0;
                while (fetched < 50 && await enumerator.MoveNextAsync())
                {
                    pool.Add(enumerator.Current);
                    fetched++;
                }
            }
            finally
            {
                try { await enumerator.DisposeAsync(); }
                catch { /* enumerator may be faulted */ }
            }

            var shuffled = pool.OrderBy(_ => _autoDjRng.Next()).ToList();

            await RunOnUiAsync(() =>
            {
                foreach (var item in shuffled)
                {
                    if (Queue.Count >= targetSize) break;
                    var videoId = item.VideoId;
                    if (_autoDjUsedIds.Contains(videoId)) continue;
                    if (Queue.Any(q => q.VideoId == videoId)) continue;
                    if (CurrentlyPlaying?.VideoId == videoId) continue;

                    Queue.Add(item);
                    _autoDjUsedIds.Add(videoId);
                    StatusText = $"AutoDJ queued: {item.Title}";

                    if (CurrentlyPlaying == null)
                        PlayNext();
                }
            });
        }
        catch (Exception ex)
        {
            DebugLog.LogException("AutoDJ video", ex);
        }
    }

    [RelayCommand]
    private void ToggleAutoDj()
    {
        AutoDjEnabled = !AutoDjEnabled;
        if (!AutoDjEnabled)
        {
            StatusText = "AutoDJ disabled";
            _autoDjUsedIds.Clear();
        }
    }

    [RelayCommand]
    private void TogglePauseResume()
    {
        if (IsPaused)
            ResumePlayback();
        else
            PausePlayback();
    }

    [RelayCommand]
    private void CreatePlaylistFromQueue()
    {
        if (Queue.Count == 0)
        {
            StatusText = "Queue is empty";
            return;
        }

        var name = $"Queue {DateTime.Now:yyyy-MM-dd HH:mm}";
        _playlists.GetOrCreate(name);
        foreach (var item in Queue)
            _playlists.AddToPlaylist(name, item);
        ActivePlaylistName = name;
        RebuildCategories();
        StatusText = $"Created playlist \"{name}\" with {Queue.Count} tracks";
    }

    [RelayCommand]
    private void ToggleRepeat()
    {
        RepeatEnabled = !RepeatEnabled;
        StatusText = RepeatEnabled ? "Repeat enabled" : "Repeat disabled";
    }

    // ── History ──

    public void PurgeHistory()
    {
        _history.Purge();
        StatusText = "History cleared";
    }

    public void ClearSearchHistory()
    {
        _searchHistory.Clear();
        RefreshSearchSuggestions();
        StatusText = "Search history cleared";
    }

    public int HistoryCount => _history.Entries.Count;

    /// <summary>
    /// Fetch accurate duration from stream manifest for a video.
    /// </summary>
    private async Task<TimeSpan?> GetAccurateDurationAsync(string videoId)
    {
        try
        {
            var meta = await GetYouTubeMetadataViaPluginOrLegacy(videoId);
            return meta?.Duration;
        }
        catch (Exception ex)
        {
            DebugLog.LogException($"Duration fetch ({videoId})", ex);
        }
        return null;
    }

    /// <summary>
    /// Fetch metadata (duration, upload date, chapters) for a playing item from <em>its own source</em>
    /// via <c>IPlayableResolver.GetMetadataAsync</c>, and apply it to the item on the UI thread.
    /// Source-agnostic: Plex returns chapters from its rating key, YouTube returns native chapters
    /// (or ones parsed from the description inside the plug-in). Any future source that implements the
    /// capability participates for free. No-ops when the item has no source resolver. Safe to
    /// fire-and-forget after playback starts.
    /// </summary>
    private async Task FetchChaptersViaSourceAsync(VideoItem item)
    {
        try
        {
            var source = SourceForItem(item);
            Video.VideoMetadata? meta;
            if (source is Phosphor.Plugin.Abstractions.IPlayableResolver resolver)
            {
                var probe = ProbeSourceItem(item, source!.InstanceId);
                var raw = await resolver.GetMetadataAsync(probe);
                meta = raw == null ? null : MapPluginMetadata(raw);
            }
            else
            {
                // No source resolver available — nothing to enrich.
                meta = null;
            }
            if (meta == null) return;

            // The source supplies ready-to-use chapters (native, or parsed from its own metadata).
            var chapters = meta.Chapters;
            var chapterSource = meta.Chapters.Count > 0 ? "source" : "none";

            // Apply item/UI mutations on the UI thread — GetMetadataAsync may resume on a thread-pool
            // thread (yt-dlp external process), and raising PropertyChanged for bound VideoItem
            // properties off the UI thread silently fails to refresh the queue bindings.
            void Apply()
            {
                if (meta.Duration.HasValue)
                    item.Duration = meta.Duration;

                if (meta.UploadDate.HasValue)
                {
                    item.UploadDate = meta.UploadDate;
                    if (ReferenceEquals(item, _currentlyPlaying))
                        OnPropertyChanged(nameof(NowPlayingTitle));
                }

                if (chapters.Count > 0)
                {
                    item.Chapters = chapters;
                    DebugLog.Log(LogLevel.Debug, "Chapters", $"Chapters ({chapterSource}): {chapters.Count}");
                    if (ReferenceEquals(item, _currentlyPlaying))
                    {
                        UpdateChapterTickPositions();
                        OnPropertyChanged(nameof(ShouldSnapToChapters));
                        UpdateCurrentChapter();
                    }

                    // Persist chapters to video cache if the item is cached.
                    _cache?.UpdateChapters(item.VideoId, chapters);
                }
            }

            await RunOnUiAsync(Apply);
        }
        catch (Exception ex)
        {
            DebugLog.LogException($"Fetch chapters ({item.VideoId})", ex);
        }
    }

    /// <summary>
    /// Refresh the duration of a video item using stream info, optionally saving the playlist.
    /// </summary>
    private async Task RefreshDurationAsync(VideoItem item, bool savePlaylist = false)
    {
        var duration = await GetAccurateDurationAsync(item.VideoId);
        if (duration.HasValue)
        {
            item.Duration = duration;
            if (savePlaylist)
                _playlists.Save();
        }
    }

    /// <summary>
    /// Fetch accurate duration for a video in a playlist and persist it.
    /// </summary>
    private async Task RefreshPlaylistItemDurationAsync(string playlistName, VideoItem item)
    {
        var duration = await GetAccurateDurationAsync(item.VideoId);
        if (!duration.HasValue) return;

        item.Duration = duration;

        // Also update the copy stored in the playlist
        var playlist = _playlists.Playlists.FirstOrDefault(p => p.Name == playlistName);
        var stored = playlist?.Videos.FirstOrDefault(v => v.VideoId == item.VideoId);
        if (stored != null)
        {
            stored.Duration = duration;
            _playlists.Save();
        }
    }

    private static async Task SafeFireAndForget(Task task)
    {
        try { await task; }
        catch (Exception ex)
        {
            DebugLog.LogException("Fire-and-forget", ex);
        }
    }
}

/// <summary>One entry in the search-box source dropdown.</summary>
public sealed record SearchSourceOption(string InstanceId, string DisplayName)
{
    // Shown in the ComboBox.
    public override string ToString() => DisplayName;
}

/// <summary>
/// One level in the generic plug-in browse navigation stack. Carries the opaque
/// <see cref="SourceState"/> the source hands back on browse, so drill-down and back-navigation are
/// fully source-agnostic (the host never interprets it).
/// </summary>
public sealed record BrowseNode(string Title, string SourceInstanceId, string CategoryId, object? SourceState, string? Icon = null, string? SearchQuery = null);
