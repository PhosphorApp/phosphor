using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Videos;

namespace Phosphor;

public partial class JukeboxViewModel : ObservableObject
{
    private readonly YoutubeClient _youtube = new();
    private readonly PlayHistory _history;
    private readonly PlaylistManager _playlists;
    private readonly SearchHistory _searchHistory;
    private VideoCache? _cache;
    private PrefetchCache? _prefetch;
    private readonly PlexService _plex = new();

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
    private List<PlexLibraryMapping> _plexLibraries = [];
    private bool _plexStereoAudio;
    private List<VideoItem> _plexHubCategories = [];
    private string _activeHubParentName = "";

    public void ConfigurePlex(string serverUrl, string token, List<PlexLibraryMapping> libraries, bool stereoAudio = false, bool skipRebuild = false)
    {
        _plex.Configure(serverUrl, token, stereoAudio);
        _plexStereoAudio = stereoAudio;
        _plexLibraries = libraries;
        GenreCategoryStore.SyncPlexLibraries(_genreCategories, _plexLibraries);
        GenreCategoryStore.SaveInBackground(_genreCategories);
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
        private set => SetProperty(ref _playTransitioning, value);
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
                    OnPropertyChanged(nameof(IsPlaying));
                    OnPropertyChanged(nameof(CanStartOrStop));
                }
        }
    }

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

    /// <summary>
    /// Raised when a non-playable Plex item (artist/album) is activated and needs drill-down handling.
    /// </summary>
    public event Action<VideoItem>? PlexDrillDownRequested;

    private double _playbackPosition;
    public double PlaybackPosition
    {
        get => _playbackPosition;
        set
        {
            if (SetProperty(ref _playbackPosition, value))
                OnPropertyChanged(nameof(PlaybackTimeText));
        }
    }

    private double _playbackDuration = 1;
    public double PlaybackDuration
    {
        get => _playbackDuration;
        set
        {
            if (SetProperty(ref _playbackDuration, value))
                OnPropertyChanged(nameof(PlaybackTimeText));
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
            if (PlaybackDuration <= 1) return "0:00 / 0:00";
            var pos = TimeSpan.FromMilliseconds(PlaybackPosition);
            var dur = TimeSpan.FromMilliseconds(PlaybackDuration);
            var fmt = dur.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss";
            return $"{pos.ToString(fmt)} / {dur.ToString(fmt)}";
        }
    }

    public void SeekTo(long timeMs) => SeekRequested?.Invoke(timeMs);

    [RelayCommand]
    private void SeekForward() => SeekRequested?.Invoke((long)PlaybackPosition + 15000);

    [RelayCommand]
    private void SeekBack() => SeekRequested?.Invoke(Math.Max(0, (long)PlaybackPosition - 15000));

    // ── Video cache ──
    public VideoCache? Cache => _cache;
    public PrefetchCache? Prefetch => _prefetch;
    private string? _prefetchingVideoId;

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

    // ── Gapless playback ──
    public bool GaplessPlayback { get; set; }

    /// <summary>
    /// Returns the next track in the queue if it is an audio-only Plex track with a StreamUrl,
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
        if (next.IsPlex && next.IsAudioOnly && !string.IsNullOrEmpty(next.StreamUrl))
            return next;

        return null;
    }

    // ── Cache mode ──
    public CacheMode CacheMode { get; set; } = CacheMode.Playlists;

    public void SetupCache(bool enabled, double maxSizeGb, int maxClipLengthMinutes = 0)
    {
        _cache = new VideoCache(enabled, maxSizeGb, maxClipLengthMinutes);
    }

    public void SetupPrefetch(bool enabled)
    {
        if (enabled)
            _prefetch ??= new PrefetchCache();
        else
        {
            _prefetch?.PurgeAll();
            _prefetch = null;
        }
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

        // Plex items are streamed directly — no YouTube prefetch needed
        if (Queue[nextIdx].IsPlex) return;

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

    // ── Pagination state ──
    private IAsyncEnumerator<IVideo>? _searchEnumerator;
    private CancellationTokenSource _searchCts = new();
    private string _currentSearchQuery = "";
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

            if (entry.IsPlex)
            {
                var group = new List<Category>();
                group.Add(new Category { Name = entry.Name, Icon = entry.Icon, PlexLibraryKey = entry.PlexLibraryKey, PlexLibraryType = entry.PlexLibraryType });

                var title = entry.Name.StartsWith("Plex ") ? entry.Name[5..] : entry.Name;
                if (entry.PlexHubsEnabled)
                    group.Add(new Category { Name = $"{title}: Hubs", Icon = "📡", PlexLibraryKey = entry.PlexLibraryKey, PlexLibraryType = entry.PlexLibraryType, IsPlexHubList = true });

                if (entry.PlexPlaylistsEnabled)
                    group.Add(new Category { Name = $"{title}: Playlists", Icon = "📋", PlexLibraryKey = entry.PlexLibraryKey, PlexLibraryType = entry.PlexLibraryType, IsPlexPlaylistList = true });

                sortable.Add((entry.SortOrder, group));
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
                // Live playlist — run the stored search
                ActivePlaylistName = category.Name;
                _activePlaylistId = playlist.Id;
                ActiveCategory = category.Name;
                IsViewingPlaylist = true;
                IsViewingLivePlaylist = true;
                ShowCategories = false;
                SearchQuery = playlist.SearchTerm;
                await DoSearch(playlist.SearchTerm);
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

        if (category.IsPlexHub && _plex.IsConfigured)
        {
            IsViewingPlaylist = false;
            _hasMoreResults = false;
            CanLoadMore = false;
            ShowCategories = false;
            IsPlexBrowsing = false;
            await BrowsePlexHubContentAsync(category.PlexHubKey!, category.PlexHubType, category.Name);
            return;
        }

        if (category.IsPlexPlaylist && _plex.IsConfigured)
        {
            IsViewingPlaylist = false;
            _hasMoreResults = false;
            CanLoadMore = false;
            ShowCategories = false;
            IsPlexBrowsing = false;
            await BrowsePlexPlaylistContentAsync(category.PlexPlaylistKey!, category.Name);
            return;
        }

        if (category.IsPlexHubList && _plex.IsConfigured)
        {
            IsViewingPlaylist = false;
            _hasMoreResults = false;
            CanLoadMore = false;
            ShowCategories = false;
            IsPlexBrowsing = false;
            await BrowsePlexHubListAsync(category.PlexLibraryKey!, category.PlexLibraryType, category.Name);
            return;
        }

        if (category.IsPlexPlaylistList && _plex.IsConfigured)
        {
            IsViewingPlaylist = false;
            _hasMoreResults = false;
            CanLoadMore = false;
            ShowCategories = false;
            IsPlexBrowsing = false;
            await BrowsePlexPlaylistListAsync(category.PlexLibraryType, category.Name);
            return;
        }

        if (category.IsPlex && _plex.IsConfigured)
        {
            IsViewingPlaylist = false;
            _hasMoreResults = false;
            CanLoadMore = false;
            ShowCategories = false;
            await BrowsePlexLibraryAsync(category.PlexLibraryKey!, category.PlexLibraryType, category.Name);
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
        _isPlexHubBrowsing = false;
        _isPlexPlaylistBrowsing = false;
        _isHistoryBrowsing = false;
        IsViewingPlexMusic = false;
        IsViewingPlexHubOrPlaylist = false;
        _activePlexHubKey = null;
        _activePlexPlaylistKey = null;
        _plexHubCategories.Clear();
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

    /// <summary>
    /// Navigate back from hub/playlist content or sub-list.
    /// </summary>
    public void PlexHubGoBack()
    {
        if (_isPlexHubBrowsing || _isPlexPlaylistBrowsing)
        {
            // Viewing hub/playlist contents — go back to the parent list
            _isPlexHubBrowsing = false;
            _isPlexPlaylistBrowsing = false;
            _activePlexHubKey = null;
            _activePlexPlaylistKey = null;
            CanLoadMore = false;
            PlexHubBreadcrumb = _activeHubParentName;

            // Re-run the parent hub/playlist list query
            if (_plexHubCategories.Count > 0)
            {
                SearchResults.ReplaceAll(_plexHubCategories);
                StatusText = $"{_plexHubCategories.Count} items";
            }
            else
            {
                ShowCategoryListCommand.Execute(null);
            }
        }
        else
        {
            // Viewing sub-list or unknown state — go home
            ShowCategoryListCommand.Execute(null);
        }
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
        await DoSearch(query);
    }

    private void RefreshSearchSuggestions()
    {
        SearchSuggestions.Clear();
        foreach (var s in _searchHistory.Searches)
            SearchSuggestions.Add(s);
    }

    private async Task DoSearch(string query)
    {
        // Cancel any in-progress load so the new search can proceed
        _searchCts.Cancel();
        _searchCts = new CancellationTokenSource();
        _isLoadingMore = false;

        IsSearching = true;
        StatusText = "Searching...";
        SearchResults.Clear();

        // If currently browsing a Plex library, search within that library instead of YouTube
        if (_isPlexBrowsing && _plex.IsConfigured && !string.IsNullOrEmpty(_activePlexLibraryKey))
        {
            // Clear drill-down breadcrumb — search results may span multiple artists/albums
            _plexDrillArtistKey = null;
            _plexDrillArtistName = null;
            _plexDrillAlbumKey = null;
            _plexDrillAlbumName = null;
            UpdatePlexBreadcrumb();

            try
            {
                var searchType = _activePlexLibraryType == "artist" ? _plexSearchMode : (PlexSearchMode?)null;
                var results = await _plex.SearchLibraryAsync(
                    _activePlexLibraryKey, query, _activePlexLibraryType, searchType, _searchCts.Token);

                foreach (var v in results)
                    SearchResults.Add(v);

                CanLoadMore = false;
                _hasMoreResults = false;
                StatusText = $"{SearchResults.Count} Plex results for \"{query}\"";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                StatusText = $"Plex search error: {ex.Message}";
                DebugLog.LogException("Plex search", ex);
            }
            finally
            {
                IsSearching = false;
            }
            return;
        }

        if (_searchEnumerator != null)
        {
            try { await _searchEnumerator.DisposeAsync(); }
            catch { /* enumerator may already be faulted or in-flight */ }
            _searchEnumerator = null;
        }

        _currentSearchQuery = query;

        // Parse and strip duration filters (min:/max:) from the query
        query = ParseDurationFilters(query);

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
                // Try as a direct playlist ID first (e.g. PLxxxxxxx or URL)
                var playlistId = playlistIdOrName;
                try
                {
                    // YoutubeExplode can parse playlist IDs from URLs or raw IDs
                    var resolved = YoutubeExplode.Playlists.PlaylistId.Parse(playlistIdOrName);
                    playlistId = resolved.Value;
                }
                catch
                {
                    // If it doesn't parse as an ID, search for the playlist by name
                    var found = false;
                    await foreach (var result in _youtube.Search.GetPlaylistsAsync(playlistIdOrName))
                    {
                        playlistId = result.Id.Value;
                        StatusText = $"Found playlist: {result.Title}";
                        found = true;
                        break;
                    }
                    if (!found)
                    {
                        StatusText = $"Could not find playlist: {playlistIdOrName}";
                        IsSearching = false;
                        return;
                    }
                }

                var videos = AsVideos(_youtube.Playlists.GetVideosAsync(playlistId));
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
                var playlistId = playlistIdOrName;
                try
                {
                    var resolved = YoutubeExplode.Playlists.PlaylistId.Parse(playlistIdOrName);
                    playlistId = resolved.Value;
                    // If it parsed as an ID, remaining text before "playlist:" is filter
                    filterTerms = Regex.Replace(query, @"playlist:\S+", "", RegexOptions.IgnoreCase).Trim();
                }
                catch
                {
                    var found = false;
                    await foreach (var result in _youtube.Search.GetPlaylistsAsync(playlistIdOrName))
                    {
                        playlistId = result.Id.Value;
                        StatusText = $"Found playlist: {result.Title}";
                        found = true;
                        break;
                    }
                    if (!found)
                    {
                        StatusText = $"Could not find playlist: {playlistIdOrName}";
                        IsSearching = false;
                        return;
                    }
                }

                var videos = AsVideos(_youtube.Playlists.GetVideosAsync(playlistId));
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
                var channel = await _youtube.Channels.GetByHandleAsync(channelName);
                var videos = AsVideos(_youtube.Channels.GetUploadsAsync(channel.Id));

                _searchEnumerator = string.IsNullOrEmpty(filterTerms)
                    ? videos.GetAsyncEnumerator()
                    : FilterVideosAsync(videos, filterTerms).GetAsyncEnumerator();
            }
            catch
            {
                // If handle lookup fails, try as a user name
                try
                {
                    var channel = await _youtube.Channels.GetByUserAsync(channelName);
                    var videos = AsVideos(_youtube.Channels.GetUploadsAsync(channel.Id));

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
        }
        else
        {
            _searchEnumerator = AsVideos(_youtube.Search.GetVideosAsync(query)).GetAsyncEnumerator();
        }

        _hasMoreResults = true;

        // Determine cache key from active category or live playlist
        _activeResultCache = null;
        _categoryCacheName = null;
        _categoryCachePageIndex = 0;
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

        await LoadMoreResults(25);
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

    private static async IAsyncEnumerable<IVideo> FilterVideosAsync(
        IAsyncEnumerable<IVideo> source, string filter,
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

    private static async IAsyncEnumerable<IVideo> AsVideos<T>(
        IAsyncEnumerable<T> source,
        [EnumeratorCancellation] CancellationToken ct = default) where T : IVideo
    {
        await foreach (var item in source.WithCancellation(ct))
            yield return item;
    }

    // ── Plex browsing ──

    private const int PlexPageSize = 20;
    private int _plexTotalSize;
    private bool _isPlexBrowsing;
    public bool IsPlexBrowsing
    {
        get => _isPlexBrowsing;
        private set => SetProperty(ref _isPlexBrowsing, value);
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

    private string _activePlexLibraryKey = "";

    private string? _activePlexLibraryType;

    // Active hub/playlist browsing state for pagination
    private string? _activePlexHubKey;
    private string? _activePlexHubType;
    private string? _activePlexPlaylistKey;
    private string? _activePlexPlaylistName;
    private int _plexPlaylistCachePageIndex;
    private string? _activePlexLibraryName;
    private int _plexLibraryCachePageIndex;
    private bool _isPlexHubBrowsing;
    private bool _isPlexPlaylistBrowsing;

    // ── Plex music drill-down state ──
    private string? _plexDrillArtistKey;
    private string? _plexDrillArtistName;
    private string? _plexDrillAlbumKey;
    private string? _plexDrillAlbumName;

    private PlexSearchMode _plexSearchMode = PlexSearchMode.Artist;
    public PlexSearchMode PlexSearchMode
    {
        get => _plexSearchMode;
        set
        {
            SetProperty(ref _plexSearchMode, value);
        }
    }

    private bool _isViewingPlexMusic;
    public bool IsViewingPlexMusic
    {
        get => _isViewingPlexMusic;
        set
        {
            if (SetProperty(ref _isViewingPlexMusic, value))
                OnPropertyChanged(nameof(ShouldHideFindSimilar));
        }
    }

    private bool _isViewingPlexHubOrPlaylist;
    public bool IsViewingPlexHubOrPlaylist
    {
        get => _isViewingPlexHubOrPlaylist;
        set
        {
            if (SetProperty(ref _isViewingPlexHubOrPlaylist, value))
                OnPropertyChanged(nameof(ShouldHideFindSimilar));
        }
    }

    /// <summary>
    /// True when "Find Similar" should be hidden (Plex music, hub, or playlist views).
    /// </summary>
    public bool ShouldHideFindSimilar => IsViewingPlexMusic || IsViewingPlexHubOrPlaylist;

    private string _plexHubBreadcrumb = "";
    public string PlexHubBreadcrumb
    {
        get => _plexHubBreadcrumb;
        set => SetProperty(ref _plexHubBreadcrumb, value);
    }

    private string _plexBreadcrumb = "";
    public string PlexBreadcrumb
    {
        get => _plexBreadcrumb;
        set => SetProperty(ref _plexBreadcrumb, value);
    }

    private async Task BrowsePlexLibraryAsync(string libraryKey, string? libraryType = null, string? displayName = null)
    {
        _searchCts.Cancel();
        _searchCts = new CancellationTokenSource();

        IsSearching = true;
        StatusText = "Loading Plex library...";
        SearchResults.Clear();
        _plexTotalSize = 0;
        _activePlexLibraryKey = libraryKey;
        _activePlexLibraryType = libraryType;
        IsPlexBrowsing = true;
        _activePlexLibraryName = displayName;
        _plexLibraryCachePageIndex = 0;

        // Reset drill-down state
        _plexDrillArtistKey = null;
        _plexDrillArtistName = null;
        _plexDrillAlbumKey = null;
        _plexDrillAlbumName = null;

        if (libraryType == "artist")
        {
            IsViewingPlexMusic = true;
            PlexSearchMode = PlexSearchMode.Artist;
            UpdatePlexBreadcrumb();
        }
        else
        {
            IsViewingPlexMusic = false;
        }

        await LoadMorePlexResultsAsync();
    }

    public async Task BrowsePlexHubContentAsync(string hubKey, string? hubType, string displayName)
    {
        _searchCts.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        IsSearching = true;
        StatusText = $"Loading {displayName}...";
        SearchResults.Clear();
        IsPlexBrowsing = false;
        _isPlexHubBrowsing = true;
        _isPlexPlaylistBrowsing = false;
        _activePlexHubKey = hubKey;
        _activePlexHubType = hubType;
        _activePlexPlaylistKey = null;
        IsViewingPlexMusic = false;
        IsViewingPlexHubOrPlaylist = true;
        PlexHubBreadcrumb = $"{_activeHubParentName} › {displayName}";
        ShowCategories = false;
        _plexTotalSize = 0;

        try
        {
            var page = await _plex.GetHubItemsPageAsync(hubKey, hubType ?? "", 0, PlexPageSize, token);
            if (token.IsCancellationRequested) return;

            _plexTotalSize = page.TotalSize;
            foreach (var v in page.Items)
                SearchResults.Add(v);

            bool hasMore = SearchResults.Count < _plexTotalSize;
            CanLoadMore = hasMore;
            StatusText = hasMore
                ? $"Showing {SearchResults.Count} of {_plexTotalSize} items in {displayName} — scroll for more"
                : $"{SearchResults.Count} items in {displayName}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = $"Plex error: {ex.Message}";
            DebugLog.LogException("Plex hub content", ex);
            CanLoadMore = false;
        }
        finally
        {
            IsSearching = false;
        }
    }

    private async Task BrowsePlexHubListAsync(string libraryKey, string? libraryType, string displayName)
    {
        _searchCts.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        _activeHubParentName = displayName;
        IsViewingPlexHubOrPlaylist = true;
        PlexHubBreadcrumb = displayName;
        IsSearching = true;
        StatusText = "Loading hubs...";
        SearchResults.Clear();
        ShowCategories = false;
        CanLoadMore = false;
        _isPlexHubBrowsing = false;
        _isPlexPlaylistBrowsing = false;
        _activePlexHubKey = null;
        _activePlexPlaylistKey = null;
        _plexHubCategories.Clear();

        try
        {
            var hubs = await _plex.GetLibraryHubsAsync(libraryKey, token);
            if (token.IsCancellationRequested) return;

            foreach (var hub in hubs)
            {
                var vi = new VideoItem
                {
                    Title = hub.Title,
                    Author = $"{hub.Size} items",
                    PlexItemType = PlexItemType.Hub,
                    PlexHubKey = hub.HubKey,
                    PlexHubType = hub.Type,
                    VideoId = $"plex:hub:{hub.HubKey}",
                };
                SearchResults.Add(vi);
                _plexHubCategories.Add(vi);
            }

            StatusText = $"{hubs.Count} hubs available";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = $"Plex error: {ex.Message}";
            DebugLog.LogException("Plex hub list", ex);
        }
        finally
        {
            IsSearching = false;
        }
    }

    private async Task BrowsePlexPlaylistListAsync(string? libraryType, string displayName)
    {
        _searchCts.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        _activeHubParentName = displayName;
        IsViewingPlexHubOrPlaylist = true;
        PlexHubBreadcrumb = displayName;
        IsSearching = true;
        StatusText = "Loading playlists...";
        SearchResults.Clear();
        ShowCategories = false;
        CanLoadMore = false;
        _isPlexHubBrowsing = false;
        _isPlexPlaylistBrowsing = false;
        _activePlexHubKey = null;
        _activePlexPlaylistKey = null;
        _plexHubCategories.Clear();

        try
        {
            var playlistType = libraryType == "artist" ? "audio" : "video";
            var playlists = await _plex.GetPlaylistsAsync(playlistType, token);
            if (token.IsCancellationRequested) return;

            foreach (var pl in playlists)
            {
                var vi = new VideoItem
                {
                    Title = pl.Title,
                    Author = $"{pl.LeafCount} items{(pl.Smart ? " · Smart" : "")}",
                    PlexItemType = PlexItemType.Playlist,
                    PlexRatingKey = pl.RatingKey,
                    VideoId = $"plex:playlist:{pl.RatingKey}",
                    ThumbnailUrl = pl.Thumb,
                };
                SearchResults.Add(vi);
                _plexHubCategories.Add(vi);
            }

            StatusText = $"{playlists.Count} playlists available";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = $"Plex error: {ex.Message}";
            DebugLog.LogException("Plex playlist list", ex);
        }
        finally
        {
            IsSearching = false;
        }
    }

    public async Task BrowsePlexPlaylistContentAsync(string ratingKey, string displayName)
    {
        _searchCts.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        IsSearching = true;
        StatusText = $"Loading {displayName}...";
        SearchResults.Clear();
        IsPlexBrowsing = false;
        _isPlexHubBrowsing = false;
        _isPlexPlaylistBrowsing = true;
        _activePlexPlaylistKey = ratingKey;
        _activePlexPlaylistName = displayName;
        _plexPlaylistCachePageIndex = 0;
        _activePlexHubKey = null;
        IsViewingPlexHubOrPlaylist = true;
        PlexHubBreadcrumb = $"{_activeHubParentName} › {displayName}";
        ShowCategories = false;
        _plexTotalSize = 0;

        try
        {
            // Try plex playlist cache first
            if (PlexPlaylistCache is { Enabled: true } ppc)
            {
                var cached = ppc.TryGetPage(displayName, 0, out var isLast);
                if (cached != null)
                {
                    foreach (var v in cached)
                        SearchResults.Add(v);
                    _plexPlaylistCachePageIndex = 1;
                    CanLoadMore = !isLast;
                    StatusText = isLast
                        ? $"{SearchResults.Count} items in {displayName} (cached)"
                        : $"Showing {SearchResults.Count} items in {displayName} (cached) — scroll for more";
                    return;
                }
            }

            var page = await _plex.GetPlaylistItemsPageAsync(ratingKey, 0, PlexPageSize, token);
            if (token.IsCancellationRequested) return;

            _plexTotalSize = page.TotalSize;
            foreach (var v in page.Items)
                SearchResults.Add(v);

            // Store in cache
            bool hasMore = SearchResults.Count < _plexTotalSize;
            if (PlexPlaylistCache is { Enabled: true } storeCache)
                storeCache.StorePage(displayName, 0, page.Items, !hasMore);
            _plexPlaylistCachePageIndex = 1;

            CanLoadMore = hasMore;
            StatusText = hasMore
                ? $"Showing {SearchResults.Count} of {_plexTotalSize} items in {displayName} — scroll for more"
                : $"{SearchResults.Count} items in {displayName}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = $"Plex error: {ex.Message}";
            DebugLog.LogException("Plex playlist content", ex);
            CanLoadMore = false;
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>
    /// Drill into a Plex artist
    /// </summary>
    public async Task PlexDrillIntoArtistAsync(string ratingKey, string artistName)
    {
        _searchCts.Cancel();
        _searchCts = new CancellationTokenSource();

        _plexDrillArtistKey = ratingKey;
        _plexDrillArtistName = artistName;
        _plexDrillAlbumKey = null;
        _plexDrillAlbumName = null;
        UpdatePlexBreadcrumb();

        IsSearching = true;
        StatusText = $"Loading albums by {artistName}...";
        SearchResults.Clear();

        try
        {
            var items = await _plex.GetChildrenAsync(ratingKey, PlexItemType.Album, _searchCts.Token);
            foreach (var v in items)
                SearchResults.Add(v);
            CanLoadMore = false;
            IsPlexBrowsing = true;
            StatusText = $"{SearchResults.Count} albums by {artistName}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = $"Plex error: {ex.Message}"; DebugLog.LogException("Plex drill artist", ex); }
        finally { IsSearching = false; }
    }

    /// <summary>
    /// Drill into a Plex album
    /// </summary>
    public async Task PlexDrillIntoAlbumAsync(string ratingKey, string albumName)
    {
        _searchCts.Cancel();
        _searchCts = new CancellationTokenSource();

        _plexDrillAlbumKey = ratingKey;
        _plexDrillAlbumName = albumName;
        UpdatePlexBreadcrumb();

        IsSearching = true;
        StatusText = $"Loading tracks from {albumName}...";
        SearchResults.Clear();

        try
        {
            var items = await _plex.GetChildrenAsync(ratingKey, PlexItemType.Track, _searchCts.Token);
            foreach (var v in items)
                SearchResults.Add(v);
            CanLoadMore = false;
            IsPlexBrowsing = true;
            StatusText = $"{SearchResults.Count} tracks on {albumName}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = $"Plex error: {ex.Message}"; DebugLog.LogException("Plex drill album", ex); }
        finally { IsSearching = false; }
    }

    /// <summary>
    /// Navigate back one level
    /// Returns true if navigation was handled, false if already at the top.
    /// </summary>
    public async Task<bool> PlexDrillBackAsync()
    {
        if (_plexDrillAlbumKey != null)
        {
            // Go back to album list for this artist
            _plexDrillAlbumKey = null;
            _plexDrillAlbumName = null;
            UpdatePlexBreadcrumb();
            if (_plexDrillArtistKey != null)
                await PlexDrillIntoArtistAsync(_plexDrillArtistKey, _plexDrillArtistName ?? "");
            return true;
        }

        if (_plexDrillArtistKey != null)
        {
            // Go back to artist list
            _plexDrillArtistKey = null;
            _plexDrillArtistName = null;
            UpdatePlexBreadcrumb();
            SearchResults.Clear();
            _plexTotalSize = 0;
            IsPlexBrowsing = true;
            _isLoadingMore = false;
            await LoadMorePlexResultsAsync();
            return true;
        }

        return false;
    }

    private void UpdatePlexBreadcrumb()
    {
        var parts = new List<string>();
        if (_plexDrillArtistName != null)
            parts.Add(_plexDrillArtistName);
        if (_plexDrillAlbumName != null)
            parts.Add(_plexDrillAlbumName);
        PlexBreadcrumb = parts.Count > 0 ? string.Join(" › ", parts) : "";
    }

    private async Task LoadMorePlexResultsAsync()
    {
        if (!_isPlexBrowsing || _isLoadingMore) return;

        // Drill-down views are not paginated — skip if we're inside an artist or album
        if (_plexDrillArtistKey != null || _plexDrillAlbumKey != null) return;

        _isLoadingMore = true;
        IsSearching = true;
        var token = _searchCts.Token;

        try
        {
            // Try plex playlist cache first
            if (PlexPlaylistCache is { Enabled: true } ppc && _activePlexLibraryName != null)
            {
                var cached = ppc.TryGetPage(_activePlexLibraryName, _plexLibraryCachePageIndex, out var isLast);
                if (cached != null)
                {
                    foreach (var v in cached)
                        SearchResults.Add(v);
                    _plexLibraryCachePageIndex++;
                    CanLoadMore = !isLast;
                    IsPlexBrowsing = !isLast;
                    StatusText = isLast
                        ? $"Showing all {SearchResults.Count} Plex items (cached)"
                        : $"Showing {SearchResults.Count} Plex items (cached) — scroll for more";
                    return;
                }
            }

            // Music libraries at top level: show artists instead of tracks
            var browseType = (_activePlexLibraryType == "artist" && _plexDrillArtistKey == null)
                ? "artist" : _activePlexLibraryType;

            var page = await _plex.GetLibraryVideosPageAsync(
                _activePlexLibraryKey, SearchResults.Count, PlexPageSize, browseType, token);

            if (token.IsCancellationRequested) return;

            _plexTotalSize = page.TotalSize;

            foreach (var v in page.Items)
                SearchResults.Add(v);

            bool hasMore = SearchResults.Count < _plexTotalSize;

            // Store in cache
            if (PlexPlaylistCache is { Enabled: true } storeCache && _activePlexLibraryName != null)
                storeCache.StorePage(_activePlexLibraryName, _plexLibraryCachePageIndex, page.Items, !hasMore);
            _plexLibraryCachePageIndex++;

            CanLoadMore = hasMore;
            IsPlexBrowsing = hasMore;
            StatusText = hasMore
                ? $"Showing {SearchResults.Count} of {_plexTotalSize} Plex items — scroll for more"
                : $"Showing all {SearchResults.Count} Plex items";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = $"Plex error: {ex.Message}";
            DebugLog.LogException("Plex library browse", ex);
            IsPlexBrowsing = false;
            CanLoadMore = false;
        }
        finally
        {
            IsSearching = false;
            _isLoadingMore = false;
        }
    }

    public async Task<List<PlexLibrary>> GetPlexLibrariesAsync()
    {
        if (!_plex.IsConfigured) return [];
        try { return await _plex.GetLibrariesAsync(); }
        catch { return []; }
    }

    private async Task LoadMorePlexHubResultsAsync()
    {
        if (!_isPlexHubBrowsing || _isLoadingMore || _activePlexHubKey == null) return;

        _isLoadingMore = true;
        IsSearching = true;
        var token = _searchCts.Token;

        try
        {
            var page = await _plex.GetHubItemsPageAsync(
                _activePlexHubKey, _activePlexHubType ?? "", SearchResults.Count, PlexPageSize, token);
            if (token.IsCancellationRequested) return;

            _plexTotalSize = page.TotalSize;
            foreach (var v in page.Items)
                SearchResults.Add(v);

            bool hasMore = SearchResults.Count < _plexTotalSize;
            CanLoadMore = hasMore;
            StatusText = hasMore
                ? $"Showing {SearchResults.Count} of {_plexTotalSize} items — scroll for more"
                : $"Showing all {SearchResults.Count} items";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = $"Plex error: {ex.Message}";
            DebugLog.LogException("Plex hub pagination", ex);
            CanLoadMore = false;
        }
        finally
        {
            IsSearching = false;
            _isLoadingMore = false;
        }
    }

    private async Task LoadMorePlexPlaylistResultsAsync()
    {
        if (!_isPlexPlaylistBrowsing || _isLoadingMore || _activePlexPlaylistKey == null) return;

        _isLoadingMore = true;
        IsSearching = true;
        var token = _searchCts.Token;

        try
        {
            // Try plex playlist cache first
            if (PlexPlaylistCache is { Enabled: true } ppc && _activePlexPlaylistName != null)
            {
                var cached = ppc.TryGetPage(_activePlexPlaylistName, _plexPlaylistCachePageIndex, out var isLast);
                if (cached != null)
                {
                    foreach (var v in cached)
                        SearchResults.Add(v);
                    _plexPlaylistCachePageIndex++;
                    CanLoadMore = !isLast;
                    StatusText = isLast
                        ? $"Showing all {SearchResults.Count} items (cached)"
                        : $"Showing {SearchResults.Count} items (cached) — scroll for more";
                    return;
                }
            }

            var page = await _plex.GetPlaylistItemsPageAsync(
                _activePlexPlaylistKey, SearchResults.Count, PlexPageSize, token);
            if (token.IsCancellationRequested) return;

            _plexTotalSize = page.TotalSize;
            foreach (var v in page.Items)
                SearchResults.Add(v);

            bool hasMore = SearchResults.Count < _plexTotalSize;

            // Store in cache
            if (PlexPlaylistCache is { Enabled: true } storeCache && _activePlexPlaylistName != null)
                storeCache.StorePage(_activePlexPlaylistName, _plexPlaylistCachePageIndex, page.Items, !hasMore);
            _plexPlaylistCachePageIndex++;

            CanLoadMore = hasMore;
            StatusText = hasMore
                ? $"Showing {SearchResults.Count} of {_plexTotalSize} items — scroll for more"
                : $"Showing all {SearchResults.Count} items";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = $"Plex error: {ex.Message}";
            DebugLog.LogException("Plex playlist pagination", ex);
            CanLoadMore = false;
        }
        finally
        {
            IsSearching = false;
            _isLoadingMore = false;
        }
    }

    [RelayCommand]
    private async Task LoadMoreResultsAsync()
    {
        if (_isHistoryBrowsing)
            LoadMoreHistoryResults();
        else if (_isPlexHubBrowsing)
            await LoadMorePlexHubResultsAsync();
        else if (_isPlexPlaylistBrowsing)
            await LoadMorePlexPlaylistResultsAsync();
        else if (_isPlexBrowsing)
            await LoadMorePlexResultsAsync();
        else
            await LoadMoreResults(25);
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
                    SearchResults.Add(new VideoItem
                    {
                        Title = video.Title ?? "",
                        Author = video.Author?.ChannelTitle ?? "",
                        ThumbnailUrl = video.Thumbnails?.GetWithHighestResolution()?.Url ?? "",
                        VideoId = video.Id,
                        Duration = video.Duration
                    });
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
                            prefetchItems.Add(new VideoItem
                            {
                                Title = video.Title ?? "",
                                Author = video.Author?.ChannelTitle ?? "",
                                ThumbnailUrl = video.Thumbnails?.GetWithHighestResolution()?.Url ?? "",
                                VideoId = video.Id,
                                Duration = video.Duration
                            });
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

    [RelayCommand]
    private async Task AddToQueueAsync(VideoItem? item)
    {
        if (item == null) return;

        if (Queue.Count >= MaxQueueSize)
        {
            StatusText = $"Queue is full ({MaxQueueSize} items max)";
            return;
        }

        // For Plex artists/albums, fetch all tracks and queue them
        if (item.PlexItemType is PlexItemType.Artist or PlexItemType.Album
            && item.PlexRatingKey != null && _plex.IsConfigured)
        {
            StatusText = $"Loading tracks from {item.Title}...";
            try
            {
                var tracks = await _plex.GetAllTracksAsync(item.PlexRatingKey, item.PlexItemType);
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
                var tracks = await _plex.GetAllTracksAsync(vi.PlexRatingKey, vi.PlexItemType);
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

        // Non-playable Plex items trigger drill-down via event
        if (item.PlexItemType is PlexItemType.Artist or PlexItemType.Album or PlexItemType.Hub or PlexItemType.Playlist)
        {
            PlexDrillDownRequested?.Invoke(item);
            return;
        }

        PlayTransitioning = true;
        SetStatusPrefix("Transitioning");
        CurrentlyPlaying = item;
        var audioTag = GetPlexAudioTag(item);
        StatusText = $"Playing: {item.Title}{audioTag}";
        _history.Add(item);
        PlayRequested?.Invoke(item.VideoId);

        // Refresh duration from stream info for accuracy (YouTube only)
        if (!item.IsPlex)
            _ = SafeFireAndForget(RefreshDurationAsync(item));

        // Cache on playback when mode is Everything (YouTube only)
        if (_cache is { Enabled: true } && CacheMode == CacheMode.Everything && !item.IsPlex)
            _ = SafeFireAndForget(_cache.CacheVideoAsync(item.VideoId, VideoQuality, StereoAudio, item.Duration));
    }

    /// <summary>
    /// Derives an audio stream tag for the status bar by inspecting the item's StreamUrl.
    /// Returns "" for non-Plex items, "(Stereo)" for native stereo selection,
    /// "(Stereo Transcode)" for server-side downmix, or "(Surround)" otherwise.
    /// </summary>
    private string GetPlexAudioTag(VideoItem item)
    {
        if (!item.IsPlex)
            return "";

        return item.PlexAudioStream switch
        {
            PlexAudioStream.Stereo => " (Stereo)",
            PlexAudioStream.StereoTranscode => " (Stereo Transcode)",
            PlexAudioStream.Surround => " (Surround)",
            _ => ""
        };
    }

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
        var audioTag = CurrentlyPlaying != null ? GetPlexAudioTag(CurrentlyPlaying) : "";
        StatusText = $"Playing: {CurrentlyPlaying?.Title}{audioTag}";
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
        if (IsPlaying)
            StopRequested?.Invoke();
        PlayNext();
    }

    [RelayCommand]
    private void PreviousTrack()
    {
        if (!IsPlaying || Queue.Count == 0) return;

        int currentIdx = _queueIndex;
        bool isFirstItem = currentIdx <= 0;
        bool isBeyond10Seconds = PlaybackPosition >= 10000; // 10 seconds in ms

        if (isBeyond10Seconds || isFirstItem)
        {
            // Restart current track
            SeekRequested?.Invoke(0);
        }
        else
        {
            // Skip to previous track
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
        var audioTag = GetPlexAudioTag(item);
        StatusText = $"Playing: {item.Title}{audioTag}";
        _history.Add(item);
        PlayTransitioning = false;
        _statusPrefixCts?.Cancel();
        StatusPrefix = "";

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
                var tracks = await _plex.GetAllTracksAsync(item.PlexRatingKey, item.PlexItemType);
                foreach (var track in tracks)
                {
                    _playlists.AddToPlaylist(ActivePlaylistName, track);
                    if (_cache is { Enabled: true } && !track.IsPlex)
                        _ = SafeFireAndForget(_cache.CacheVideoAsync(track.VideoId, duration: track.Duration));
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

        // Trigger background caching for playlist items (YouTube only)
        if (_cache is { Enabled: true } && !item.IsPlex)
            _ = SafeFireAndForget(_cache.CacheVideoAsync(item.VideoId, duration: item.Duration));
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

        _playlists.CreateLivePlaylist(name, SearchQuery, icon);
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
    private void QueueAllFromPlaylist()
    {
        if (SearchResults.Count == 0) return;

        foreach (var video in SearchResults)
            Queue.Add(video);

        StatusText = $"Queued {SearchResults.Count} videos from {ActiveCategory}";
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
            var results = new List<YoutubeExplode.Search.VideoSearchResult>();
            var enumerator = _youtube.Search.GetVideosAsync(genre.SearchTerm).GetAsyncEnumerator();
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

            foreach (var video in shuffled)
            {
                if (Queue.Count >= targetSize) break;
                var videoId = video.Id.Value;
                if (_autoDjUsedIds.Contains(videoId)) continue;
                if (Queue.Any(q => q.VideoId == videoId)) continue;
                if (CurrentlyPlaying?.VideoId == videoId) continue;

                var item = new VideoItem
                {
                    Title = video.Title,
                    Author = video.Author.ChannelTitle,
                    ThumbnailUrl = video.Thumbnails.GetWithHighestResolution()?.Url ?? "",
                    VideoId = videoId,
                    Duration = video.Duration
                };

                Queue.Add(item);
                _autoDjUsedIds.Add(videoId);
                StatusText = $"AutoDJ queued: {item.Title}";

                if (CurrentlyPlaying == null)
                    PlayNext();
            }
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
            var pool = new List<YoutubeExplode.Search.VideoSearchResult>();
            var enumerator = _youtube.Search.GetVideosAsync(query).GetAsyncEnumerator();
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

            foreach (var video in shuffled)
            {
                if (Queue.Count >= targetSize) break;
                var videoId = video.Id.Value;
                if (_autoDjUsedIds.Contains(videoId)) continue;
                if (Queue.Any(q => q.VideoId == videoId)) continue;
                if (CurrentlyPlaying?.VideoId == videoId) continue;

                var item = new VideoItem
                {
                    Title = video.Title,
                    Author = video.Author.ChannelTitle,
                    ThumbnailUrl = video.Thumbnails.GetWithHighestResolution()?.Url ?? "",
                    VideoId = videoId,
                    Duration = video.Duration
                };

                Queue.Add(item);
                _autoDjUsedIds.Add(videoId);
                StatusText = $"AutoDJ queued: {item.Title}";

                if (CurrentlyPlaying == null)
                    PlayNext();
            }
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
            var video = await _youtube.Videos.GetAsync(videoId);
            return video.Duration;
        }
        catch (Exception ex)
        {
            DebugLog.LogException($"Duration fetch ({videoId})", ex);
        }
        return null;
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
