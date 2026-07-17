using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Phosphor.Video;
using Phosphor.Search;

namespace Phosphor;

public partial class JukeboxViewModel : ObservableObject
{
    private SearchEngineKind _searchEngineKind = SearchEngineKind.YoutubeExplode;
    private ISearchEngine _searchEngine = new YoutubeExplodeSearchEngine();
    private readonly PlayHistory _history;
    private readonly PlaylistManager _playlists;
    private readonly SearchHistory _searchHistory;
    private VideoCache? _cache;
    private PrefetchCache? _prefetch;
    private readonly PlexService _plex = new();

    // ── Plug-in sources (the source path — YouTube and Plex run through the registry) ──
    private Phosphor.Plugins.SourceRegistry? _sourceRegistry;
    private bool _pluginsDiscovered;
    // Pre-fetched root-category tiles for generic IBrowsable plug-in sources (local-folder, future
    // Jellyfin, …), keyed by instance id. Built after each registry build; read by RebuildCategories.
    private readonly List<Category> _pluginBrowseTiles = new();

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
    /// The <c>TypeId</c> of the currently-selected search source (e.g. "youtube", "plex"), or
    /// <c>null</c> if unresolved. Lets the UI tailor hints per source type without knowing instance
    /// ids (Plex is multi-instance). YouTube is the implicit default when nothing is selected.
    /// </summary>
    public string? ActiveSearchSourceTypeId =>
        (_activeSearchSourceId != null ? _sourceRegistry?.ByInstance(_activeSearchSourceId) : _sourceRegistry?.YouTube)
            ?.TypeId
        ?? (_activeSearchSourceId == null ? Phosphor.Plugins.YouTube.YouTubeSourceProvider.YouTubeTypeId : null);

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
    /// Updates the active YouTube engine tool (yt-dlp) and returns a user-facing status line.
    /// Routes through the plug-in source's <c>IUpdatable</c> when the source supports updating;
    /// otherwise falls back to the legacy <see cref="Video.YtDlpUpdater"/>.
    /// </summary>
    public async Task<string> UpdatePluginEngineOrLegacyAsync(CancellationToken ct = default)
    {
        if (_sourceRegistry?.YouTube is Phosphor.Plugin.Abstractions.IUpdatable u && u.SupportsUpdate)
        {
            var result = await u.UpdateAsync(ct);
            return result.DisplayString;
        }

        var legacy = await new Video.YtDlpUpdater().UpdateAsync(ct);
        return legacy.ToDisplayString();
    }

    /// <summary>
    /// Builds (or rebuilds) the plug-in <see cref="Phosphor.Plugins.SourceRegistry"/> from the
    /// given settings. The registry is the source path for YouTube and Plex discovery/playback.
    /// </summary>
    public async Task BuildSourceRegistryAsync(AppSettings settings)
    {
        // Discover third-party plug-ins from the plugins/ folder once per app run (built-in type ids
        // are reserved so a plug-in can't shadow YouTube/Plex). Cheap to guard; the scan touches disk.
        if (!_pluginsDiscovered)
        {
            Phosphor.Plugins.DiscoveredProviders.Initialize(new[]
            {
                Phosphor.Plugins.YouTube.YouTubeSourceProvider.YouTubeTypeId,
                Phosphor.Plugins.Plex.PlexSourceProvider.PlexTypeId,
            });
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
            DebugLog.Log("SourceRegistry", $"Built {registry.Sources.Count} source(s)");
            WireCacheDownloadOverride();
            await BuildPluginBrowseTilesAsync(registry);
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
    }

    /// <summary>
    /// Pre-fetches root-category tiles for every generic <c>IBrowsable</c> plug-in source (local-folder,
    /// future Jellyfin, …) into <see cref="_pluginBrowseTiles"/>, so the synchronous
    /// <see cref="RebuildCategories"/> can emit them without an async call. Built-in YouTube/Plex are
    /// skipped — YouTube isn't browsable and Plex has its own tile path (GenreCategoryStore). Failures
    /// per source are logged and skipped so one bad plug-in never blocks the home screen.
    /// </summary>
    private async Task BuildPluginBrowseTilesAsync(Phosphor.Plugins.SourceRegistry registry)
    {
        // Each browsable source's GetRootCategoriesAsync does a network round-trip (auth + list
        // libraries) for media servers. Running them sequentially makes startup wait for the SUM of
        // every server's latency (Plex + Emby + Jellyfin + …). Fetch all sources in PARALLEL instead,
        // so total time ≈ the slowest single source. Results are reassembled in registry order below
        // so tile ordering stays deterministic regardless of which server responds first.
        var browsableSources = registry.Sources
            .Where(s => s.TypeId != Phosphor.Plugins.YouTube.YouTubeSourceProvider.YouTubeTypeId
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
        DebugLog.Log("SourceRegistry", $"Built {tiles.Count} plug-in browse tile(s).");

        // Sync these root tiles into the persisted genre-category entries so they participate in the
        // unified sort/visibility model (like Plex tiles). Prunes stale, preserves user customization.
        var sourceTiles = tiles
            .Select(t => new GenreCategoryStore.SourceTile(
                t.SourceInstanceId!, t.SourceCategoryId ?? t.Name, t.Name, t.Icon,
                registry.ByInstance(t.SourceInstanceId!)?.TypeId ?? ""))
            .ToList();
        GenreCategoryStore.SyncSourceTiles(_genreCategories, sourceTiles);
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
            .OrderByDescending(o => o.InstanceId == Phosphor.Plugins.YouTube.YouTubeSourceProvider.YouTubeTypeId)
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
        var root = new BrowseNode(
            category.Name,
            category.SourceInstanceId!,
            category.SourceCategoryId ?? category.Name,
            category.SourceState,
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
                    SearchResults.Add(ToGenericContainerItem(cat, node.Icon));
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
                SearchResults.Add(ToGenericContainerItem(cat, node.Icon));

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
        Phosphor.Plugin.Abstractions.SourceCategory cat, string? parentIcon = null) => new()
    {
        Title = cat.Title,
        ThumbnailUrl = cat.ThumbnailUrl ?? "",
        VideoId = cat.CategoryId,
        IsGenericContainer = true,
        ContainerIcon = string.IsNullOrWhiteSpace(cat.Icon) ? parentIcon : cat.Icon,
        GenericSourceInstanceId = cat.SourceInstanceId,
        GenericSourceState = cat.SourceState,
        GenericCategoryId = cat.CategoryId,
    };

    /// <summary>
    /// Maps a leaf <see cref="SourceItem"/> that is actually a browsable container
    /// (<see cref="SourceItem.IsContainer"/> — e.g. a Plex artist/album returned inside a hub,
    /// playlist, or search result) into a drill-in container <see cref="VideoItem"/>. Carries the
    /// item's opaque <c>SourceState</c> so the source resolves the node on drill-in.
    /// </summary>
    private static VideoItem ToContainerLeafItem(Phosphor.Plugin.Abstractions.SourceItem item) => new()
    {
        Title = item.Title,
        Author = item.Subtitle ?? "",
        ThumbnailUrl = item.ThumbnailUrl ?? "",
        VideoId = item.ItemId,
        IsGenericContainer = true,
        GenericSourceInstanceId = item.SourceInstanceId,
        GenericSourceState = item.SourceState,
        GenericCategoryId = item.ItemId,
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
            SearchResults.Add(ToContainerLeafItem(item));
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
        // Resolve a playable URL now (local files are a cheap path check); the player checks
        // VideoItem.StreamUrl first and plays it directly. EXCEPTION: live streams (e.g. SiriusXM
        // radio) are resolved lazily at play time — eagerly resolving every browse leaf would fire
        // one authenticated round-trip per channel (hundreds), so we only carry the flag here and
        // resolve on demand in the play path.
        if (resolver != null && !item.IsLiveStream)
        {
            try
            {
                var stream = await resolver.ResolveAsync(
                    item, new Phosphor.Plugin.Abstractions.PlaybackPreferences(), ct);
                if (stream != null)
                {
                    vi.StreamUrl = stream.PrimaryUri;
                    if (stream.IsLiveStream) vi.IsLiveStream = true;
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
        foreach (var entry in _genreCategories)
            entry.IsVisible = !hiddenSet.Contains(entry.Name);
        GenreCategoryStore.Save(_genreCategories);
        RebuildCategories();
    }

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
    private bool _plexStereoAudio;

    /// <summary>
    /// Configures Plex (and its category tiles) from settings. Tiles are built from <em>all</em>
    /// enabled Plex instances in <see cref="AppSettings.PluginInstances"/> (each tile tagged with its
    /// instance id); a per-instance <see cref="PlexService"/> cache is built so browse operations
    /// target the right server, and the single <c>_plex</c> service is configured from the first
    /// enabled instance as a default fallback.
    /// </summary>
    public void ConfigurePlexFromSettings(AppSettings settings, bool skipRebuild = false)
    {
        // Seed the instance list on first run so a fresh install still gets Plex tiles
        // from the migrated flat fields (matches BuildSourceRegistryAsync's one-time seed).
        if (settings.PluginInstances.Count == 0)
            settings.PluginInstances = Phosphor.Plugins.PluginSettingsFactory.FromAppSettings(settings);

        var plexInstances = settings.PluginInstances
            .Where(c => c.Enabled && c.TypeId == Phosphor.Plugins.Plex.PlexSourceProvider.PlexTypeId)
            .Where(c => !string.IsNullOrWhiteSpace(GetSetting(c, Phosphor.Plugins.Plex.PlexSourceProvider.KeyServerUrl))
                     && !string.IsNullOrWhiteSpace(GetSetting(c, Phosphor.Plugins.Plex.PlexSourceProvider.KeyToken)))
            .ToList();

        // Configure the single legacy _plex service from the first enabled instance (used as a
        // default fallback; per-instance browse uses _plexServiceByInstance below).
        var first = plexInstances.FirstOrDefault();
        if (first != null)
        {
            _plex.Configure(
                GetSetting(first, Phosphor.Plugins.Plex.PlexSourceProvider.KeyServerUrl) ?? "",
                GetSetting(first, Phosphor.Plugins.Plex.PlexSourceProvider.KeyToken) ?? "",
                bool.TryParse(GetSetting(first, Phosphor.Plugins.Plex.PlexSourceProvider.KeyStereoAudio), out var s) && s);
            _plexStereoAudio = bool.TryParse(GetSetting(first, Phosphor.Plugins.Plex.PlexSourceProvider.KeyStereoAudio), out var s2) && s2;
        }

        // Build a per-instance PlexService for each enabled instance so multi-server browse
        // (hub/playlist lists, in-library search, GetAllTracks, chapters) targets the right server.
        _plexServiceByInstance.Clear();
        foreach (var c in plexInstances)
        {
            var svc = new PlexService();
            svc.Configure(
                GetSetting(c, Phosphor.Plugins.Plex.PlexSourceProvider.KeyServerUrl) ?? "",
                GetSetting(c, Phosphor.Plugins.Plex.PlexSourceProvider.KeyToken) ?? "",
                bool.TryParse(GetSetting(c, Phosphor.Plugins.Plex.PlexSourceProvider.KeyStereoAudio), out var cs) && cs);
            _plexServiceByInstance[c.InstanceId] = svc;
        }

        // Plex home tiles are no longer synced here — Plex flows through the generic browse path
        // (BuildPluginBrowseTilesAsync → SyncSourceTiles), one tile per library like any other
        // IBrowsable source. This method only wires up the PlexService instances used for
        // playback/chapters/gapless.
        if (!skipRebuild)
            RebuildCategories();
    }

    private static string? GetSetting(Phosphor.Plugins.PluginInstanceConfig cfg, string key)
        => cfg.Settings.TryGetValue(key, out var v) ? v : null;

    /// <summary>
    /// Returns the <see cref="PlexService"/> for the currently-active browse instance (multi-server).
    /// On the plug-in path this is the per-instance service configured in
    /// <see cref="ConfigurePlexFromSettings"/>; falls back to the single legacy <c>_plex</c> when
    /// there is no active/keyed instance (single-server or flag-off).
    /// </summary>
    private PlexService ActivePlex =>
        _activePlexInstanceId != null && _plexServiceByInstance.TryGetValue(_activePlexInstanceId, out var svc)
            ? svc
            : _plex;

    /// <summary>
    /// Resolves the plug-in Plex source for the current browse session (multi-server). Prefers the
    /// instance keyed by <see cref="_activePlexInstanceId"/>; falls back to the first Plex instance
    /// so single-server / not-yet-scoped browses behave exactly as before.
    /// </summary>
    private Phosphor.Plugin.Abstractions.IPhosphorSource? ActivePlexSource =>
        (_activePlexInstanceId != null ? _sourceRegistry?.ByInstance(_activePlexInstanceId) : null)
            ?? _sourceRegistry?.PlexInstances.FirstOrDefault();

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
    /// Call from the player host once playback has actually started (video output received)
    /// to allow the next play request to proceed.
    /// </summary>
    public void NotifyPlaybackStarted()
    {
        PlayTransitioning = false;
        _statusPrefixCts?.Cancel();
        StatusPrefix = "";
    }

    private VideoItem? _currentlyPlaying;
    public VideoItem? CurrentlyPlaying
    {
        get => _currentlyPlaying;
        set
        {
            if (SetProperty(ref _currentlyPlaying, value))
                {
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
        DebugLog.Log("Chapters", $"UpdateChapterTickPositions: chapters={chapters?.Count ?? 0} duration={duration}");
        if (chapters == null || chapters.Count == 0 || duration <= 1)
        {
            ChapterTickPositions = [];
            return;
        }
        var ticks = chapters
            .Select(c => c.StartTime.TotalMilliseconds / duration)
            .Where(p => p > 0 && p < 1)
            .ToList();
        DebugLog.Log("Chapters", $"Tick positions: [{string.Join(", ", ticks.Select(t => t.ToString("F3")))}]");
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
            // legacy Plex/YouTube id-shape heuristic for items without a source link.
            string? source = null;
            if (_currentlyPlaying.SourceInstanceId is { Length: > 0 } id)
                source = _sourceRegistry?.ByInstance(id)?.DisplayName;
            source ??= _currentlyPlaying switch
            {
                { IsPlex: true } => "Plex",
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
                DebugLog.Log("Status", value);
            }
        }
    }

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

    private bool _isViewingPlaylist;
    public bool IsViewingPlaylist
    {
        get => _isViewingPlaylist;
        set
        {
            if (SetProperty(ref _isViewingPlaylist, value))
                OnPropertyChanged(nameof(IsViewingStaticPlaylist));
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

    // ── Video engine (YoutubeExplode / yt-dlp switch point) ──
    private IVideoEngine _videoEngine = new YoutubeExplodeVideoEngine();

    /// <summary>
    /// The active video engine used to resolve/download YouTube streams. Defaults to
    /// YoutubeExplode; swapped via <see cref="SetVideoEngine"/> from settings.
    /// </summary>
    public IVideoEngine VideoEngine => _videoEngine;

    /// <summary>
    /// Rebuilds the video engine from the given kind and propagates it to the caches.
    /// Safe to call at startup and on settings changes.
    /// </summary>
    public void SetVideoEngine(VideoEngineKind kind)
    {
        _videoEngine = VideoEngineFactory.Create(kind);
        if (_cache != null) _cache.VideoEngine = _videoEngine;
        if (_prefetch != null) _prefetch.VideoEngine = _videoEngine;
    }

    // ── Thumbnail cache ──
    public ThumbnailCache? ThumbnailCache { get; private set; }

    // ── Category cache ──
    public ResultCache? CategoryCache { get; private set; }
    public ResultCache? YtPlaylistCache { get; private set; }
    public ResultCache? PlexPlaylistCache { get; private set; }

    public void SetupCategoryCache(bool enabled, int maxAgeHours)
    {
        if (CategoryCache == null)
            CategoryCache = new ResultCache(enabled, maxAgeHours, "c_");
        else
            CategoryCache.UpdateSettings(enabled, maxAgeHours);
    }

    public void SetupYtPlaylistCache(bool enabled, int maxAgeHours)
    {
        if (YtPlaylistCache == null)
            YtPlaylistCache = new ResultCache(enabled, maxAgeHours, "pl_");
        else
            YtPlaylistCache.UpdateSettings(enabled, maxAgeHours);
    }

    public void SetupPlexPlaylistCache(bool enabled, int maxAgeHours)
    {
        if (PlexPlaylistCache == null)
            PlexPlaylistCache = new ResultCache(enabled, maxAgeHours, "plex_", "plex_cache");
        else
            PlexPlaylistCache.UpdateSettings(enabled, maxAgeHours);
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
        DebugLog.Log("Network", $"Timeout set to {seconds}s");
    }

    /// <summary>
    /// Rebuilds the search engine from the given kind (and current timeout) and
    /// propagates it. Safe to call at startup and on settings changes.
    /// </summary>
    public void SetSearchEngine(SearchEngineKind kind)
    {
        _searchEngineKind = kind;
        RebuildSearchEngine();
    }

    private void RebuildSearchEngine()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(NetworkTimeoutSeconds) };
        _searchEngine = SearchEngineFactory.Create(_searchEngineKind, http);
    }

    /// <summary>
    /// Whether an item's source can produce downloadable raw streams for the disk caches. Driven by
    /// capability (the owning source implements <c>IDownloadable</c> — YouTube does, Plex does not),
    /// falling back to the legacy rule (<c>!IsPlex</c>) when the registry is unavailable. The
    /// per-instance <c>AllowCaching</c> policy overrides the capability default when set
    /// (<c>true</c> forces on, <c>false</c> forces off).
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

        return !item.IsPlex;
    }

    /// <summary>
    /// Resolves the plug-in source a playing <see cref="VideoItem"/> belongs to. Plex items
    /// (<c>plex:</c> ids) route to the active Plex instance; everything else routes to YouTube.
    /// Mirrors the source lookup used by <see cref="IsItemCacheable"/>. Returns null if the
    /// registry is unavailable.
    /// </summary>
    private Phosphor.Plugin.Abstractions.IPhosphorSource? SourceForItem(VideoItem item)
    {
        if (_sourceRegistry == null) return null;
        // Prefer the explicit source link when the producing source recorded it; fall back to the
        // id-shape heuristic (plex: → Plex, else YouTube) for legacy items and the built-in engine.
        if (item.SourceInstanceId is { Length: > 0 } id
            && _sourceRegistry.ByInstance(id) is { } owner)
            return owner;
        return item.IsPlex ? ActivePlexSource : _sourceRegistry.YouTube;
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
    /// source-bound path for tiles/AutoDJ). Falls back to YouTube, then the legacy engine, when the
    /// requested source is unavailable or not searchable.
    /// </summary>
    private IAsyncEnumerable<VideoItem> SearchVideosViaPluginOrLegacy(string query, string? sourceInstanceId)
    {
        // Resolve the requested source; null => YouTube.
        var source = sourceInstanceId != null ? _sourceRegistry?.ByInstance(sourceInstanceId) : _sourceRegistry?.YouTube;
        if (source is Phosphor.Plugin.Abstractions.ITextSearchCapable capable)
        {
            DebugLog.Log("SourceRegistry", $"Search routed through plug-in source '{source.InstanceId}'");
            return MapPluginSearch(capable, query);
        }

        // Requested source gone/not searchable — fall back to YouTube, then the legacy engine.
        if (_sourceRegistry?.YouTube is Phosphor.Plugin.Abstractions.ITextSearchCapable yt)
        {
            DebugLog.Log("SourceRegistry", "Search fell back to plug-in YouTube source");
            return MapPluginSearch(yt, query);
        }

        return _searchEngine.SearchVideosAsync(query);
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
            && s.TypeId != Phosphor.Plugins.YouTube.YouTubeSourceProvider.YouTubeTypeId
            ? r : null;

        await foreach (var item in items.WithCancellation(ct))
        {
            var vi = ToVideoItem(item);
            if (resolver != null && string.IsNullOrEmpty(vi.StreamUrl))
            {
                try
                {
                    var stream = await resolver.ResolveAsync(
                        item, new Phosphor.Plugin.Abstractions.PlaybackPreferences(), ct);
                    if (stream != null) vi.StreamUrl = stream.PrimaryUri;
                }
                catch (Exception ex)
                {
                    DebugLog.LogException($"Search resolve '{item.ItemId}'", ex);
                }
                vi.IsAudioOnly = item.IsAudioOnly;
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
            SourceInstanceId = item.SourceInstanceId,
            IsAudioOnly = item.IsAudioOnly,
            IsLiveStream = item.IsLiveStream,
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

    /// <summary>Resolves a playlist id via the plug-in discovery capability, else the legacy engine.</summary>
    private Task<string?> ResolvePlaylistIdViaPluginOrLegacy(string nameIdOrUrl, Action<string>? onFoundByName)
        => PluginDiscovery is { } d
            ? d.ResolvePlaylistIdAsync(nameIdOrUrl, onFoundByName)
            : _searchEngine.ResolvePlaylistIdAsync(nameIdOrUrl, onFoundByName);

    /// <summary>Yields a playlist's videos via the plug-in discovery capability, else the legacy engine.</summary>
    private IAsyncEnumerable<VideoItem> GetPlaylistVideosViaPluginOrLegacy(string playlistId)
        => PluginDiscovery is { } d
            ? MapPluginItems(d.GetPlaylistItemsAsync(playlistId))
            : _searchEngine.GetPlaylistVideosAsync(playlistId);

    /// <summary>Yields a channel's uploads via the plug-in discovery capability, else the legacy engine.</summary>
    private IAsyncEnumerable<VideoItem> GetChannelUploadsViaPluginOrLegacy(string handleOrUser)
        => PluginDiscovery is { } d
            ? MapPluginItems(d.GetChannelUploadsAsync(handleOrUser))
            : _searchEngine.GetChannelUploadsAsync(handleOrUser);

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

        return await _videoEngine.GetMetadataAsync(videoId);
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
            DebugLog.Log("SourceRegistry", "Stream resolution routed through plug-in YouTube source");
            return resolved == null ? null : MapResolvedStream(resolved);
        }

        return await _videoEngine.ResolveStreamsAsync(videoId, quality, preferStereo, audioOnly, ct);
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

            // The caches mux separate video+audio; use the plug-in result only when fully
            // populated, otherwise fall through to the legacy engine so caching never breaks.
            if (result?.VideoFilePath is { } vp && result.AudioFilePath is { } ap)
            {
                DebugLog.Log("SourceRegistry", "Stream download routed through plug-in YouTube source");
                return new Video.VideoDownload(
                    vp, ap, result.VideoContainer ?? "", result.AudioContainer ?? "", result.Resolution ?? "");
            }
        }

        return await _videoEngine.DownloadStreamsAsync(videoId, quality, preferStereo, destinationDir, ct);
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
    /// Drills into a Plex container (artist→albums or album→tracks). Routes through the active
    /// instance's <c>IBrowsable.BrowseAsync</c> (converting the result back to
    /// <see cref="VideoItem"/>s); falls back to the legacy <c>_plex.GetChildrenAsync</c> when the
    /// registry is unavailable.
    /// </summary>
    private async Task<List<VideoItem>> PlexBrowseChildrenViaPluginOrLegacy(
        string ratingKey, PlexItemType childType, CancellationToken ct)
    {
        var plex = ActivePlexSource;
        if (plex is Phosphor.Plugin.Abstractions.IBrowsable browsable)
        {
            // The parent node kind is one level above the requested children: album-children
            // hang off an Artist node, track-children off an Album node.
            var parentKind = childType == PlexItemType.Track
                ? Phosphor.Plugins.Plex.PlexNodeKind.Album
                : Phosphor.Plugins.Plex.PlexNodeKind.Artist;

            var category = new Phosphor.Plugin.Abstractions.SourceCategory
            {
                SourceInstanceId = plex.InstanceId,
                CategoryId = ratingKey,
                SourceState = new Phosphor.Plugins.Plex.PlexNode(parentKind, ratingKey),
            };

            var result = await browsable.BrowseAsync(category, ct);
            DebugLog.Log("SourceRegistry", $"Plex drill routed through plug-in ({childType} children)");

            var mapped = new List<VideoItem>();
            mapped.AddRange(result.Categories.Select(Phosphor.Plugins.Plex.PlexMappings.ToContainerVideoItem));
            mapped.AddRange(result.Items.Select(Phosphor.Plugins.Plex.PlexMappings.ToVideoItem));
            return mapped;
        }

        return await _plex.GetChildrenAsync(ratingKey, childType, ct);
    }

    /// <summary>
    /// Shared core for the paginated Plex browse helpers. Wraps <paramref name="node"/> in a
    /// <see cref="Phosphor.Plugin.Abstractions.SourceCategory"/> and routes through the active
    /// instance's <c>IPagedBrowsable.BrowsePageAsync</c> (unwrapping items back to
    /// <see cref="VideoItem"/>); falls back to <paramref name="legacy"/> when the registry is
    /// unavailable. Returns the page's items and total size so callers' "load more" logic is
    /// unchanged.
    /// </summary>
    private async Task<(List<VideoItem> Items, int TotalSize)> PlexBrowsePageViaPluginOrLegacy(
        Phosphor.Plugins.Plex.PlexNode node, int offset, int count,
        Func<Task<PlexPage>> legacy, CancellationToken ct = default)
    {
        var plex = ActivePlexSource;
        if (plex is Phosphor.Plugin.Abstractions.IPagedBrowsable paged)
        {
            var category = new Phosphor.Plugin.Abstractions.SourceCategory
            {
                SourceInstanceId = plex.InstanceId,
                CategoryId = node.Key,
                SourceState = node,
            };

            var page = await paged.BrowsePageAsync(category, offset, count, ct);
            DebugLog.Log("SourceRegistry", $"Plex {node.Kind} page routed through plug-in (offset={offset})");
            var items = page.Items.Select(Phosphor.Plugins.Plex.PlexMappings.ToVideoItem).ToList();
            return (items, page.TotalSize);
        }

        var result = await legacy();
        return (result.Items, result.TotalSize);
    }

    /// <summary>Fetches one page of a Plex hub's items (plug-in or legacy). See <see cref="PlexBrowsePageViaPluginOrLegacy"/>.</summary>
    private Task<(List<VideoItem> Items, int TotalSize)> PlexBrowseHubPageViaPluginOrLegacy(
        string hubKey, string hubType, int offset, int count, CancellationToken ct)
        => PlexBrowsePageViaPluginOrLegacy(
            new Phosphor.Plugins.Plex.PlexNode(Phosphor.Plugins.Plex.PlexNodeKind.Hub, hubKey, hubType),
            offset, count,
            () => _plex.GetHubItemsPageAsync(hubKey, hubType, offset, count, ct), ct);

    /// <summary>Fetches one page of a Plex library's items (artists at music top-level, else videos).</summary>
    private Task<(List<VideoItem> Items, int TotalSize)> PlexBrowseLibraryPageViaPluginOrLegacy(
        string libraryKey, string? browseType, int offset, int count, CancellationToken ct)
        => PlexBrowsePageViaPluginOrLegacy(
            new Phosphor.Plugins.Plex.PlexNode(Phosphor.Plugins.Plex.PlexNodeKind.Library, libraryKey, browseType),
            offset, count,
            () => _plex.GetLibraryVideosPageAsync(libraryKey, offset, count, browseType, ct), ct);

    /// <summary>Fetches one page of a Plex playlist's items (plug-in or legacy).</summary>
    private Task<(List<VideoItem> Items, int TotalSize)> PlexBrowsePlaylistPageViaPluginOrLegacy(
        string playlistKey, int offset, int count, CancellationToken ct)
        => PlexBrowsePageViaPluginOrLegacy(
            new Phosphor.Plugins.Plex.PlexNode(Phosphor.Plugins.Plex.PlexNodeKind.Playlist, playlistKey),
            offset, count,
            () => _plex.GetPlaylistItemsPageAsync(playlistKey, offset, count, ct), ct);

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
            // Route the item to its source and ask the gapless capability. Plex reads the carried
            // VideoItem from SourceState, so wrap it accordingly.
            var source = SourceForItem(item);
            if (source is Phosphor.Plugin.Abstractions.IGaplessCapable g)
            {
                var probe = new Phosphor.Plugin.Abstractions.SourceItem
                {
                    SourceInstanceId = source.InstanceId,
                    ItemId = item.VideoId,
                    IsAudioOnly = item.IsAudioOnly,
                    SourceState = item,
                };
                return g.GetGaplessStreamUrl(probe);
            }
            return null;
        }

        // Legacy rule.
        return item.IsPlex && item.IsAudioOnly && !string.IsNullOrEmpty(item.StreamUrl)
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

    // ── Cache mode ──
    public CacheMode CacheMode { get; set; } = CacheMode.Playlists;

    public void SetupCache(bool enabled, double maxSizeGb, int maxClipLengthMinutes = 0)
    {
        _cache = new VideoCache(enabled, maxSizeGb, maxClipLengthMinutes) { VideoEngine = _videoEngine };
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
        if (!IsItemCacheable(next) || next.IsAudioOnly) return;

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

        DebugLog.Log("PreemptiveCache", $"Starting preemptive cache job for next track {videoId}: {next.Title}");
        _ = SafeFireAndForget(_cache.CacheVideoAsync(
            videoId,
            VideoQuality,
            StereoAudio,
            next.Duration,
            next.Chapters,
            next.Title));
    }

    public void SetupPrefetch(bool enabled)
    {
        if (enabled)
        {
            _prefetch ??= new PrefetchCache();
            _prefetch.VideoEngine = _videoEngine;
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
        _ = SafeFireAndForget(_prefetch.PrefetchAsync(nextId, VideoQuality, StereoAudio));
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
                OnPropertyChanged(nameof(IsViewingStaticPlaylist));
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
            // Legacy Plex tile entries (pre-generic-path) are ignored — Plex now renders through the
            // generic source path (IsGenericSource). They're pruned from disk on next source build.
            if (entry.IsPlex && !entry.IsGenericSource) continue;

            if (entry.IsGenericSource)
            {
                // Generic plug-in source root tile — recover the opaque SourceState from the live
                // tile list (persisted entry holds only serializable identity + sort/visibility).
                var live = _pluginBrowseTiles.FirstOrDefault(t =>
                    t.SourceInstanceId == entry.SourceInstanceId
                    && (t.SourceCategoryId ?? t.Name) == (entry.SourceCategoryId ?? ""));
                if (live == null) continue; // source not currently available — skip its tile

                sortable.Add((entry.SortOrder, new List<Category>
                {
                    new()
                    {
                        Name = entry.Name,
                        Icon = entry.Icon,
                        IsPluginBrowse = true,
                        SourceInstanceId = entry.SourceInstanceId,
                        SourceCategoryId = entry.SourceCategoryId,
                        SourceState = live.SourceState,
                    }
                }));
            }
            else
            {
                sortable.Add((entry.SortOrder, new List<Category>
                {
                    new() { Name = entry.Name, Icon = entry.Icon, SearchTerm = entry.SearchTerm }
                }));
            }
        }

        // Merge by SortOrder (stable sort preserves relative order for ties)
        var items = sortable.OrderBy(s => s.SortOrder).SelectMany(s => s.Items).ToList();

        // "New Playlist" action tile at the end
        items.Add(new Category { Name = "New Playlist", Icon = "＋", IsNewPlaylist = true });

        DebugLog.Log("RebuildCategories", $"Build list ({items.Count} items): {sw.ElapsedMilliseconds}ms");
        sw.Restart();

        // Batch-update: replace all items and fire a single Reset notification
        Categories.ReplaceAll(items);

        DebugLog.Log("RebuildCategories", $"ReplaceAll: {sw.ElapsedMilliseconds}ms");
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
            IsPlexBrowsing = false;
            SearchResults.Clear();
            LoadMoreHistoryResults();
            ShowCategories = false;
            return;
        }

        SearchQuery = category.SearchTerm;
        ShowCategories = false;
        await DoSearch(category.SearchTerm);
    }

    [RelayCommand]
    private async Task ShowCategoryListAsync()
    {
        ShowCategories = true;
        IsViewingPlaylist = false;
        IsViewingLivePlaylist = false;
        IsPlexBrowsing = false;
        _isHistoryBrowsing = false;
        _activePlexInstanceId = null;
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

        // If currently browsing a generic plug-in node whose source supports in-view search,
        // push a scoped-search frame onto the browse stack (so drilling into a result and pressing
        // Back returns to the results). EnterBrowseNodeAsync runs the search for a frame that carries
        // a SearchQuery. The frame keeps the current node's identity but overrides its title/query.
        if (IsGenericBrowsing && _browseStack.Count > 0
            && _sourceRegistry?.ByInstance(_browseStack[^1].SourceInstanceId)
               is Phosphor.Plugin.Abstractions.IScopedSearchable)
        {
            var current = _browseStack[^1];
            var searchFrame = new BrowseNode(
                $"Search: {query}",
                current.SourceInstanceId,
                current.CategoryId,
                current.SourceState,
                current.Icon,
                query);
            await EnterBrowseNodeAsync(searchFrame, pushOntoStack: true);
            return;
        }

        // Browsing a generic node whose source is searchable source-wide (ITextSearchCapable but not
        // IScopedSearchable, e.g. local folders): route the search to THAT source, not YouTube, and
        // keep results in the flat list. The source resolves its own StreamUrl (MapPluginSearch).
        if (IsGenericBrowsing && _browseStack.Count > 0
            && _sourceRegistry?.ByInstance(_browseStack[^1].SourceInstanceId)
               is Phosphor.Plugin.Abstractions.ITextSearchCapable)
        {
            sourceInstanceId = _browseStack[^1].SourceInstanceId;
        }

        if (_searchEnumerator != null)
        {
            try { await _searchEnumerator.DisposeAsync(); }
            catch { /* enumerator may already be faulted or in-flight */ }
            _searchEnumerator = null;
        }

        _currentSearchQuery = query;
        _currentSearchSourceId = sourceInstanceId;

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
                // If the token parses as an id, text before "playlist:" is the filter.
                bool parsedAsId = false;
                try { YoutubeExplode.Playlists.PlaylistId.Parse(playlistIdOrName); parsedAsId = true; }
                catch { /* treat as a name to search */ }
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

        // Determine cache key from active category or live playlist. Only the YouTube-bound path
        // (tiles/live playlists, sourceInstanceId == null) uses these caches — an ad-hoc search
        // against another source must not attach a YouTube-shaped category/playlist cache.
        _activeResultCache = null;
        _categoryCacheName = null;
        _categoryCachePageIndex = 0;
        if (sourceInstanceId == null)
        {
            var genreEntry = _genreCategories.FirstOrDefault(c =>
                !string.IsNullOrEmpty(c.SearchTerm) && c.SearchTerm == query);
            if (genreEntry != null)
            {
                _categoryCacheName = genreEntry.Id;
                _activeResultCache = CategoryCache;
            }
            else if (IsViewingLivePlaylist && !string.IsNullOrEmpty(_activePlaylistId))
            {
                _categoryCacheName = _activePlaylistId;
                _activeResultCache = YtPlaylistCache;
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

    // ── Plex browsing ──

    private bool _isPlexBrowsing;
    public bool IsPlexBrowsing
    {
        get => _isPlexBrowsing;
        private set
        {
            if (SetProperty(ref _isPlexBrowsing, value))
            {
                OnPropertyChanged(nameof(IsSearchScoped));
                OnPropertyChanged(nameof(IsSearchSourceSelectable));
            }
        }
    }

    /// <summary>
    /// Whether the current view <em>scopes</em> the search box to a specific browse context (a
    /// library/folder/collection), so the global search-source selector doesn't apply. Source-agnostic
    /// by design: today only Plex library browsing sets it, but any future scoped source (Jellyfin
    /// library, local-folder, …) should feed into this same signal rather than the UI checking a
    /// per-source flag. When a generic scoped-search capability lands (see PLUGIN_ARCHITECTURE_ANALYSIS.md),
    /// this becomes its natural home.
    /// </summary>
    public bool IsSearchScoped => IsPlexBrowsing || IsGenericScopedSearchAvailable;

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

    // The Plex instance id the current browse session targets (multi-server). Null = first/legacy.
    private string? _activePlexInstanceId;

    // Per-instance PlexService cache for legacy-style calls (GetAllTracks, chapters, gapless) that
    // the plug-in path still routes through a PlexService. Configured from each instance's settings
    // so multi-server calls hit the right server.
    private readonly Dictionary<string, PlexService> _plexServiceByInstance = new(StringComparer.Ordinal);

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
                        SearchResults.Add(v);
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
                        DebugLog.Log("PlaylistPrefetch", $"Prefetched {prefetchItems.Count} items for next page");
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
            var json = JsonSerializer.Serialize(Queue.ToList(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(QueuePath, json);
        }
        catch { }
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
    /// Toggles the favorite state of an item whose source supports favorites (<c>IFavoritable</c>).
    /// Routes to the owning source, flips the star, and refreshes the row. No-op for items whose
    /// source doesn't support favorites (the star isn't shown for those anyway).
    /// </summary>
    [RelayCommand]
    private void ToggleFavorite(VideoItem? item)
    {
        if (item == null || !item.CanFavorite) return;
        var source = SourceForItem(item);
        if (source is not Phosphor.Plugin.Abstractions.IFavoritable fav) return;

        var newState = !item.IsFavorite;
        fav.SetFavorite(item.VideoId, newState);
        item.IsFavorite = newState;
        StatusText = newState ? $"★ Favorited: {item.Title}" : $"Unfavorited: {item.Title}";
    }

    [RelayCommand]
    private async Task AddToQueueAsync(VideoItem? item)
    {
        if (item == null) return;

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

        // For Plex artists/albums, fetch all tracks and queue them
        if (item.PlexItemType is PlexItemType.Artist or PlexItemType.Album
            && item.PlexRatingKey != null && _plex.IsConfigured)
        {
            StatusText = $"Loading tracks from {item.Title}...";
            try
            {
                var tracks = await ActivePlex.GetAllTracksAsync(item.PlexRatingKey, item.PlexItemType);
                int added = 0;
                foreach (var track in tracks)
                {
                    if (Queue.Count >= MaxQueueSize) break;
                    Queue.Add(track);
                    added++;
                }
                StatusText = added < tracks.Count
                    ? $"Queued {added} of {tracks.Count} tracks (queue limit {MaxQueueSize})"
                    : $"Queued {tracks.Count} tracks from {item.Title}";
            }
            catch (Exception ex)
            {
                StatusText = $"Failed to queue: {ex.Message}";
                DebugLog.LogException("Queue Plex tracks", ex);
            }
            return;
        }

        // For Plex hubs
        if (item.PlexItemType == PlexItemType.Hub
            && item.PlexHubKey != null && _plex.IsConfigured)
        {
            StatusText = $"Loading items from {item.Title}...";
            try
            {
                var items = await _plex.GetHubItemsAsync(item.PlexHubKey, item.PlexHubType ?? "");
                int added = await QueuePlayableItemsAsync(items);
                StatusText = $"Queued {added} items from {item.Title}";
            }
            catch (Exception ex)
            {
                StatusText = $"Failed to queue: {ex.Message}";
                DebugLog.LogException("Queue Plex hub items", ex);
            }
            return;
        }

        // For Plex playlists
        if (item.PlexItemType == PlexItemType.Playlist
            && item.PlexRatingKey != null && _plex.IsConfigured)
        {
            StatusText = $"Loading items from {item.Title}...";
            try
            {
                var items = await _plex.GetPlaylistItemsAsync(item.PlexRatingKey);
                int added = 0;
                foreach (var track in items)
                {
                    if (Queue.Count >= MaxQueueSize) break;
                    Queue.Add(track);
                    added++;
                }
                StatusText = added < items.Count
                    ? $"Queued {added} of {items.Count} items (queue limit {MaxQueueSize})"
                    : $"Queued {items.Count} items from {item.Title}";
            }
            catch (Exception ex)
            {
                StatusText = $"Failed to queue: {ex.Message}";
                DebugLog.LogException("Queue Plex playlist items", ex);
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

            // For artists/albums in hub results, expand to tracks
            if (vi.PlexItemType is PlexItemType.Artist or PlexItemType.Album
                && vi.PlexRatingKey != null && _plex.IsConfigured)
            {
                var tracks = await ActivePlex.GetAllTracksAsync(vi.PlexRatingKey, vi.PlexItemType);
                foreach (var track in tracks)
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

    [RelayCommand]
    private void PlayNow(VideoItem? item)
    {
        if (item == null) return;

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

        // Live streams (e.g. SiriusXM) resolve lazily at play time — resolve the local proxy URL
        // now, then start playback. Everything else already has its StreamUrl (or plays by id).
        if (item.IsLiveStream && item.StreamUrl == null && item.PendingLiveSourceItem != null)
        {
            _ = SafeFireAndForget(ResolveAndPlayLiveAsync(item));
            return;
        }

        PlayRequested?.Invoke(item.VideoId);

        // Fetch duration/chapters from the item's own source (source-agnostic). Fire-and-forget so
        // playback starts immediately; results apply to the now-playing item when they arrive.
        if (item.Chapters == null)
            _ = SafeFireAndForget(FetchChaptersViaSourceAsync(item));

        // Cache on playback when mode is Everything (cacheable sources only)
        if (_cache is { Enabled: true } && CacheMode == CacheMode.Everything && IsItemCacheable(item))
            _ = SafeFireAndForget(_cache.CacheVideoAsync(item.VideoId, VideoQuality, StereoAudio, item.Duration, item.Chapters, item.Title));

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
        if (source is not Phosphor.Plugin.Abstractions.IPlayableResolver resolver
            || item.PendingLiveSourceItem is not Phosphor.Plugin.Abstractions.SourceItem sourceItem)
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
                item.StreamUrl = url;
                item.PendingLiveSourceItem = null; // resolved
                // Guard against the user having moved on while we were tuning.
                if (ReferenceEquals(CurrentlyPlaying, item))
                    PlayRequested?.Invoke(item.VideoId);
            }
            else
            {
                StatusText = $"Can't tune {item.Title} — stream unavailable.";
                PlayTransitioning = false;
            }
        }
        catch (Exception ex)
        {
            DebugLog.LogException($"Live resolve '{item.VideoId}'", ex);
            StatusText = $"Can't tune {item.Title}: {ex.Message}";
            PlayTransitioning = false;
        }
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

        if (IsPlaying)
            StopRequested?.Invoke();

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

        // Last chapter or no chapters — skip to next queue item
        if (IsPlaying)
            StopRequested?.Invoke();
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

        // For Plex artists/albums, fetch all tracks and add them to the playlist
        if (item.PlexItemType is PlexItemType.Artist or PlexItemType.Album
            && item.PlexRatingKey != null && _plex.IsConfigured)
        {
            StatusText = $"Loading tracks from {item.Title}...";
            try
            {
                var tracks = await ActivePlex.GetAllTracksAsync(item.PlexRatingKey, item.PlexItemType);
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
        // so re-opening it queries that source rather than the default.
        _playlists.CreateLivePlaylist(name, SearchQuery, icon, _currentSearchSourceId);
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
    /// Source-agnostic: Plex returns chapters from its rating key, YouTube returns native yt-dlp
    /// chapters (or, when it has none, we fall back to parsing the description). Any future source that
    /// implements the capability participates for free. Falls back to the legacy engine when no source
    /// resolver is available. Safe to fire-and-forget after playback starts.
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
                // Registry unavailable — legacy in-VM engine (YouTube only).
                meta = await _videoEngine.GetMetadataAsync(item.VideoId);
            }
            if (meta == null) return;

            // Native chapters take precedence; when the source reported none, fall back to parsing a
            // description (YouTube-style). Non-YouTube sources simply have no description → no-op.
            var chapters = meta.Chapters.Count > 0
                ? meta.Chapters
                : ParseYouTubeChapters(meta.Description ?? "", meta.Duration);
            var chapterSource = meta.Chapters.Count > 0 ? "native" : "description";

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
                    DebugLog.Log("Chapters", $"Chapters ({chapterSource}): {chapters.Count}");
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
    /// Parses chapter markers from a YouTube video description.
    /// Looks for lines starting with timestamps like "0:00", "1:23:45", etc.
    /// </summary>
    private static List<ChapterMarker> ParseYouTubeChapters(string description, TimeSpan? totalDuration)
    {
        var chapters = new List<ChapterMarker>();
        if (string.IsNullOrWhiteSpace(description)) return chapters;

        // Match lines like "0:00 Intro" or "1:23:45 - Song Name" or "(0:00) Title"
        var regex = new System.Text.RegularExpressions.Regex(
            @"(?:^|\()\s*(\d{1,2}:\d{2}(?::\d{2})?)\s*(?:\)?\s*[-–—]?\s*)(.+)",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        foreach (System.Text.RegularExpressions.Match match in regex.Matches(description))
        {
            var timeParts = match.Groups[1].Value.Split(':');
            TimeSpan ts;
            if (timeParts.Length == 3)
                ts = new TimeSpan(int.Parse(timeParts[0]), int.Parse(timeParts[1]), int.Parse(timeParts[2]));
            else
                ts = new TimeSpan(0, int.Parse(timeParts[0]), int.Parse(timeParts[1]));

            var title = match.Groups[2].Value.Trim();

            chapters.Add(new ChapterMarker
            {
                Title = title,
                StartTime = ts,
                EndTime = TimeSpan.Zero // filled below
            });
        }

        // Fill EndTime from next chapter's StartTime
        for (int i = 0; i < chapters.Count - 1; i++)
            chapters[i].EndTime = chapters[i + 1].StartTime;
        if (chapters.Count > 0 && totalDuration.HasValue)
            chapters[^1].EndTime = totalDuration.Value;

        return chapters;
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
