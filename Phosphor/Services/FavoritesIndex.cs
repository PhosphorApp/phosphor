using System.Text.Json;

namespace Phosphor;

/// <summary>
/// A single favorited item in the host-level aggregated index. Lightweight display record captured
/// at star-time so the global Favorites tile renders instantly, with no round-trip to any source.
/// Playback resolves lazily via the owning source's <c>IFavoritable.GetFavorite(ItemId)</c>.
/// </summary>
public sealed class FavoriteEntry
{
    /// <summary>The owning plug-in source instance id. Empty/"youtube" for the built-in YouTube source.</summary>
    public string SourceInstanceId { get; set; } = "";
    /// <summary>The source-native item id (video id, channel id, rating key, …).</summary>
    public string ItemId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? ThumbnailUrl { get; set; }
    public double? DurationSeconds { get; set; }
    public bool IsAudioOnly { get; set; }
    public bool IsLiveStream { get; set; }
    /// <summary>True when this favorite is a container (artist/album) that expands to tracks on play.</summary>
    public bool IsContainer { get; set; }
    /// <summary>Display label for the owning source (e.g. "SiriusXM", "Vimeo"), for a source tag.</summary>
    public string SourceLabel { get; set; } = "";
    /// <summary>When it was favorited (newest-first ordering in the aggregated view).</summary>
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Composite identity: a favorite is unique per (source instance, item id).</summary>
    public string Key => $"{SourceInstanceId}\u0000{ItemId}";
}

/// <summary>
/// A single row in the user's manual Favorites order: either a real favorite entry, or a layout
/// marker (separator / line break). Markers only apply in Custom-order mode and are round-tripped
/// through <c>favorites-order.json</c> as sentinel tokens.
/// </summary>
public sealed class FavoriteOrderRow
{
    /// <summary>The favorite when this is a real row; null for markers.</summary>
    public FavoriteEntry? Entry { get; init; }
    public bool IsSeparator { get; init; }
    public bool IsLineBreak { get; init; }
}

/// <summary>
/// Host-level, write-through favorites index aggregated across ALL sources. Updated on every
/// star/unstar (a reliable, discrete user action), so the global Favorites tile can render every
/// favorite immediately from one local file — no per-provider round-trips, works offline. Per-source
/// <c>IFavoritable</c> stores remain the source of truth for "is favorited"; this is a denormalized
/// display cache. Deliberately independent of <c>PlaylistManager</c> (the legacy Favorites playlist
/// is retired — no migration).
/// </summary>
public sealed class FavoritesIndex
{
    private static readonly string IndexPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "favorites-index.json");

    private static readonly string OrderPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "favorites-order.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Sentinel tokens for layout markers stored inline in the custom-order list. Chosen to never
    // collide with a real favorite key (which is "sourceInstanceId\u0000itemId").
    private const string SeparatorToken = "\u0001__SEP__";
    private const string LineBreakToken = "\u0001__LB__";

    private static bool IsSeparatorToken(string s) => s == SeparatorToken;
    private static bool IsLineBreakToken(string s) => s == LineBreakToken;
    private static bool IsMarkerToken(string s) => IsSeparatorToken(s) || IsLineBreakToken(s);

    private readonly object _gate = new();
    private readonly Dictionary<string, FavoriteEntry> _entries = new(StringComparer.Ordinal);

    // User-defined manual order (Grouping = Custom): ordered list of favorite keys. Keys that no longer
    // resolve to an entry are ignored on read; new/unordered favorites are appended newest-first.
    private List<string> _customOrder = new();

    public FavoritesIndex()
    {
        Load();
        LoadOrder();
    }

    /// <summary>Composite identity for a favorite: unique per (source instance, item id).</summary>
    public static string MakeKey(string sourceInstanceId, string itemId) =>
        $"{sourceInstanceId}\u0000{itemId}";

    /// <summary>All favorites, newest-first.</summary>
    public IReadOnlyList<FavoriteEntry> All()
    {
        lock (_gate) return _entries.Values.OrderByDescending(e => e.AddedAt).ToList();
    }

    /// <summary>
    /// All favorites in the user's saved manual order (Grouping = Custom). Ordered keys come first (in
    /// saved order), then any not-yet-ordered favorites newest-first. Stale keys are skipped.
    /// </summary>
    public IReadOnlyList<FavoriteEntry> AllCustomOrdered()
    {
        lock (_gate)
        {
            var result = new List<FavoriteEntry>(_entries.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in _customOrder)
                if (seen.Add(key) && _entries.TryGetValue(key, out var e))
                    result.Add(e);
            foreach (var e in _entries.Values.OrderByDescending(e => e.AddedAt))
                if (seen.Add(e.Key))
                    result.Add(e);
            return result;
        }
    }

    /// <summary>
    /// All favorites in the user's saved manual order (Grouping = Custom), interleaved with any
    /// layout markers (separators / line breaks). Ordered keys come first (in saved order), then any
    /// not-yet-ordered favorites newest-first. Stale keys are skipped; markers are preserved as-is.
    /// </summary>
    public IReadOnlyList<FavoriteOrderRow> AllCustomOrderedWithMarkers()
    {
        lock (_gate)
        {
            var result = new List<FavoriteOrderRow>(_entries.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in _customOrder)
            {
                if (IsSeparatorToken(key)) { result.Add(new FavoriteOrderRow { IsSeparator = true }); continue; }
                if (IsLineBreakToken(key)) { result.Add(new FavoriteOrderRow { IsLineBreak = true }); continue; }
                if (seen.Add(key) && _entries.TryGetValue(key, out var e))
                    result.Add(new FavoriteOrderRow { Entry = e });
            }
            foreach (var e in _entries.Values.OrderByDescending(e => e.AddedAt))
                if (seen.Add(e.Key))
                    result.Add(new FavoriteOrderRow { Entry = e });
            return result;
        }
    }

    /// <summary>
    /// Persists a new manual order from the given keys (write-through, a discrete user action). Keys
    /// not matching a current favorite are dropped; favorites missing from the list are appended
    /// newest-first so the stored order always covers every favorite.
    /// </summary>
    public void SetCustomOrder(IEnumerable<string> keys)
    {
        lock (_gate)
        {
            var order = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                if (IsMarkerToken(key)) { order.Add(key); continue; }
                if (_entries.ContainsKey(key) && seen.Add(key))
                    order.Add(key);
            }
            foreach (var e in _entries.Values.OrderByDescending(e => e.AddedAt))
                if (seen.Add(e.Key))
                    order.Add(e.Key);
            _customOrder = order;
            SaveOrder();
        }
    }

    /// <summary>The sentinel token used to persist a separator marker in the custom order.</summary>
    public static string SeparatorMarker => SeparatorToken;
    /// <summary>The sentinel token used to persist a line-break marker in the custom order.</summary>
    public static string LineBreakMarker => LineBreakToken;
    /// <summary>True when the given key is a layout marker sentinel (separator or line break).</summary>
    public static bool IsMarker(string key) => IsMarkerToken(key);
    /// <summary>True when the given key is a separator sentinel.</summary>
    public static bool IsSeparator(string key) => IsSeparatorToken(key);
    /// <summary>True when the given key is a line-break sentinel.</summary>
    public static bool IsLineBreak(string key) => IsLineBreakToken(key);

    public int Count { get { lock (_gate) return _entries.Count; } }

    public bool Contains(string sourceInstanceId, string itemId)
    {
        var key = $"{sourceInstanceId}\u0000{itemId}";
        lock (_gate) return _entries.ContainsKey(key);
    }

    /// <summary>Adds/updates an entry and persists. Idempotent on the (source, item) key.</summary>
    public void Add(FavoriteEntry entry)
    {
        if (string.IsNullOrEmpty(entry.ItemId)) return;
        lock (_gate)
        {
            // Preserve original AddedAt when re-adding an existing key.
            if (_entries.TryGetValue(entry.Key, out var existing))
                entry.AddedAt = existing.AddedAt;
            _entries[entry.Key] = entry;
            Save();
        }
    }

    /// <summary>
    /// Updates a stored entry's thumbnail (e.g. after a source lazily resolves a channel's live/VOD
    /// preview) and persists. No-op if the key is absent or the value is unchanged.
    /// </summary>
    public void UpdateThumbnail(string sourceInstanceId, string itemId, string? thumbnailUrl)
    {
        var key = $"{sourceInstanceId}\u0000{itemId}";
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var e) && e.ThumbnailUrl != thumbnailUrl)
            {
                e.ThumbnailUrl = thumbnailUrl;
                Save();
            }
        }
    }

    /// <summary>Removes an entry and persists. No-op if absent.</summary>
    public void Remove(string sourceInstanceId, string itemId)
    {
        var key = $"{sourceInstanceId}\u0000{itemId}";
        lock (_gate)
        {
            if (_entries.Remove(key)) Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(IndexPath)) return;
            var list = JsonSerializer.Deserialize<List<FavoriteEntry>>(File.ReadAllText(IndexPath));
            if (list is null) return;
            lock (_gate)
            {
                _entries.Clear();
                foreach (var e in list)
                    if (!string.IsNullOrEmpty(e.ItemId))
                        _entries[e.Key] = e;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Log(LogLevel.Warning, "FavoritesIndex", $"Load failed: {ex.Message}");
        }
    }

    // Caller holds _gate.
    private void Save()
    {
        try
        {
            var list = _entries.Values.OrderByDescending(e => e.AddedAt).ToList();
            File.WriteAllText(IndexPath, JsonSerializer.Serialize(list, JsonOptions));
        }
        catch (Exception ex)
        {
            DebugLog.Log(LogLevel.Warning, "FavoritesIndex", $"Save failed: {ex.Message}");
        }
    }

    private void LoadOrder()
    {
        try
        {
            if (!File.Exists(OrderPath)) return;
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(OrderPath));
            if (list is null) return;
            lock (_gate) _customOrder = list;
        }
        catch (Exception ex)
        {
            DebugLog.Log(LogLevel.Warning, "FavoritesIndex", $"LoadOrder failed: {ex.Message}");
        }
    }

    // Caller holds _gate.
    private void SaveOrder()
    {
        try
        {
            File.WriteAllText(OrderPath, JsonSerializer.Serialize(_customOrder, JsonOptions));
        }
        catch (Exception ex)
        {
            DebugLog.Log(LogLevel.Warning, "FavoritesIndex", $"SaveOrder failed: {ex.Message}");
        }
    }
}
