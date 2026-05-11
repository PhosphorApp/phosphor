using System.IO;
using System.Text.Json;

namespace VpinJukebox;

/// <summary>
/// A single cached page of playlist/search results.
/// </summary>
public class CachedPlaylistPage
{
    public int PageIndex { get; set; }
    public List<CachedVideoItem> Items { get; set; } = [];
    public bool IsLastPage { get; set; }
}

/// <summary>
/// Serializable subset of VideoItem for playlist caching.
/// </summary>
public class CachedVideoItem
{
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string ThumbnailUrl { get; set; } = "";
    public string VideoId { get; set; } = "";
    public double? DurationSeconds { get; set; }
    public string? StreamUrl { get; set; }
    public bool IsAudioOnly { get; set; }
    public string? PlexRatingKey { get; set; }
    public PlexAudioStream PlexAudioStream { get; set; }
    public PlexItemType PlexItemType { get; set; }
}

/// <summary>
/// Metadata envelope stored alongside cached pages.
/// </summary>
public class CachedPlaylistMetadata
{
    public string Source { get; set; } = "";
    public string PlaylistName { get; set; } = "";
    public DateTime CachedAtUtc { get; set; }
    public int TotalPages { get; set; }
}

/// <summary>
/// Caches playlist/category search results to disk so subsequent loads are instant.
/// Files are stored in the cache folder (e.g. yt_pl_cache), named {source}_{playlist}.json.
/// Pagination is handled by storing pages inside the cache file.
/// </summary>
public sealed class PlaylistCache
{
    private readonly string _cacheDir;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    public bool Enabled { get; set; }
    public int MaxAgeHours { get; set; }

    /// <summary>
    /// Deletes the cache file for a specific source/key combination from the default cache directory.
    /// </summary>
    public static void InvalidateCacheFile(string source, string key, string? cacheSubDir = null)
    {
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, cacheSubDir ?? "yt_pl_cache");
        var safe = string.Concat((source + "_" + key).Select(c =>
            char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_')).ToLowerInvariant();
        var path = Path.Combine(dir, safe + ".json");
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                DebugLog.Log("PlaylistCache", $"Invalidated cache file: {path}");
            }
        }
        catch (Exception ex)
        {
            DebugLog.Log("PlaylistCache", $"Failed to invalidate cache file {path}: {ex.Message}");
        }
    }

    public PlaylistCache(bool enabled, int maxAgeHours, string? cacheSubDir = null)
    {
        Enabled = enabled;
        MaxAgeHours = maxAgeHours;
        _cacheDir = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, cacheSubDir ?? "yt_pl_cache");
    }

    public void UpdateSettings(bool enabled, int maxAgeHours)
    {
        Enabled = enabled;
        MaxAgeHours = maxAgeHours;
    }

    /// <summary>
    /// Builds a sanitized cache file path for the given source and playlist name.
    /// </summary>
    private string GetCachePath(string source, string playlistName)
    {
        var safe = string.Concat((source + "_" + playlistName).Select(c =>
            char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_')).ToLowerInvariant();
        return Path.Combine(_cacheDir, safe + ".json");
    }

    /// <summary>
    /// Try to load a cached page. Returns null if not cached or expired.
    /// </summary>
    public List<VideoItem>? TryGetPage(string source, string playlistName, int pageIndex, out bool isLastPage)
    {
        isLastPage = false;
        if (!Enabled) return null;

        var path = GetCachePath(source, playlistName);
        if (!File.Exists(path)) return null;

        try
        {
            var data = JsonSerializer.Deserialize<CachedPlaylistFile>(File.ReadAllText(path), JsonOptions);
            if (data == null) return null;

            // Check expiry
            if ((DateTime.UtcNow - data.Metadata.CachedAtUtc).TotalHours > MaxAgeHours)
            {
                DebugLog.Log("PlaylistCache", $"Expired: {source}/{playlistName} (age {(DateTime.UtcNow - data.Metadata.CachedAtUtc).TotalHours:F1}h > {MaxAgeHours}h)");
                try { File.Delete(path); } catch { }
                return null;
            }

            var page = data.Pages.FirstOrDefault(p => p.PageIndex == pageIndex);
            if (page == null)
            {
                DebugLog.Log("PlaylistCache", $"Miss (page {pageIndex} not found): {source}/{playlistName}");
                return null;
            }

            isLastPage = page.IsLastPage;
            DebugLog.Log("PlaylistCache", $"Hit: {source}/{playlistName} page {pageIndex} ({page.Items.Count} items)");
            return page.Items.Select(ToVideoItem).ToList();
        }
        catch (Exception ex)
        {
            DebugLog.Log("PlaylistCache", $"Failed to read cache for {source}/{playlistName}: {ex.Message}");
            try { File.Delete(path); } catch { }
            return null;
        }
    }

    /// <summary>
    /// Store a page of results in the cache. Merges with existing pages.
    /// </summary>
    public void StorePage(string source, string playlistName, int pageIndex, IReadOnlyList<VideoItem> items, bool isLastPage)
    {
        if (!Enabled) return;

        try
        {
            Directory.CreateDirectory(_cacheDir);
            var path = GetCachePath(source, playlistName);

            CachedPlaylistFile data;
            if (File.Exists(path))
            {
                data = JsonSerializer.Deserialize<CachedPlaylistFile>(File.ReadAllText(path), JsonOptions)
                       ?? new CachedPlaylistFile();
            }
            else
            {
                data = new CachedPlaylistFile();
            }

            data.Metadata.Source = source;
            data.Metadata.PlaylistName = playlistName;
            data.Metadata.CachedAtUtc = DateTime.UtcNow;

            // Replace or add the page
            data.Pages.RemoveAll(p => p.PageIndex == pageIndex);
            data.Pages.Add(new CachedPlaylistPage
            {
                PageIndex = pageIndex,
                IsLastPage = isLastPage,
                Items = items.Select(ToCached).ToList()
            });

            data.Metadata.TotalPages = data.Pages.Count;
            File.WriteAllText(path, JsonSerializer.Serialize(data, JsonOptions));
            DebugLog.Log("PlaylistCache", $"Stored: {source}/{playlistName} page {pageIndex} ({items.Count} items)");
        }
        catch (Exception ex)
        {
            DebugLog.Log("PlaylistCache", $"Store error for {source}/{playlistName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Delete all cached playlist files.
    /// </summary>
    public void Purge()
    {
        DebugLog.Log("PlaylistCache", "Purging all playlist cache files");
        try
        {
            if (Directory.Exists(_cacheDir))
                Directory.Delete(_cacheDir, true);
        }
        catch (Exception ex)
        {
            DebugLog.Log("PlaylistCache", $"Purge error: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the total size of the playlist cache in bytes.
    /// </summary>
    public long GetSizeBytes()
    {
        try
        {
            if (!Directory.Exists(_cacheDir)) return 0;
            return new DirectoryInfo(_cacheDir)
                .EnumerateFiles("*.json", SearchOption.TopDirectoryOnly)
                .Sum(f => f.Length);
        }
        catch { return 0; }
    }

    private static CachedVideoItem ToCached(VideoItem v) => new()
    {
        Title = v.Title,
        Author = v.Author,
        ThumbnailUrl = v.ThumbnailUrl,
        VideoId = v.VideoId,
        DurationSeconds = v.Duration?.TotalSeconds,
        StreamUrl = v.StreamUrl,
        IsAudioOnly = v.IsAudioOnly,
        PlexRatingKey = v.PlexRatingKey,
        PlexAudioStream = v.PlexAudioStream,
        PlexItemType = v.PlexItemType
    };

    private static VideoItem ToVideoItem(CachedVideoItem c) => new()
    {
        Title = c.Title,
        Author = c.Author,
        ThumbnailUrl = c.ThumbnailUrl,
        VideoId = c.VideoId,
        Duration = c.DurationSeconds.HasValue ? TimeSpan.FromSeconds(c.DurationSeconds.Value) : null,
        StreamUrl = c.StreamUrl,
        IsAudioOnly = c.IsAudioOnly,
        PlexRatingKey = c.PlexRatingKey,
        PlexAudioStream = c.PlexAudioStream,
        PlexItemType = c.PlexItemType
    };
}

/// <summary>
/// Root object serialized to each playlist cache JSON file.
/// </summary>
public class CachedPlaylistFile
{
    public CachedPlaylistMetadata Metadata { get; set; } = new();
    public List<CachedPlaylistPage> Pages { get; set; } = [];
}
