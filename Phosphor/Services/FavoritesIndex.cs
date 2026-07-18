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

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly Dictionary<string, FavoriteEntry> _entries = new(StringComparer.Ordinal);

    public FavoritesIndex() => Load();

    /// <summary>All favorites, newest-first.</summary>
    public IReadOnlyList<FavoriteEntry> All()
    {
        lock (_gate) return _entries.Values.OrderByDescending(e => e.AddedAt).ToList();
    }

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
            DebugLog.Log("FavoritesIndex", $"Load failed: {ex.Message}");
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
            DebugLog.Log("FavoritesIndex", $"Save failed: {ex.Message}");
        }
    }
}
