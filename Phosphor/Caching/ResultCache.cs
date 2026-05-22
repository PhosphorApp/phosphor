using System.IO;
using System.Text.Json;

namespace Phosphor;

/// <summary>
/// A single cached page of search results.
/// </summary>
public class CachedResultPage
{
    public int PageIndex { get; set; }
    public List<CachedVideoItem> Items { get; set; } = [];
    public bool IsLastPage { get; set; }
}

/// <summary>
/// Serializable subset of VideoItem for result caching.
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
public class CachedResultMetadata
{
    public string Source { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime CachedAtUtc { get; set; }
    public int TotalPages { get; set; }
}

/// <summary>
/// Caches search results (categories, playlists, etc.) to disk so subsequent loads are instant.
/// Files are stored in a cache folder with a configurable file prefix to separate different
/// result types within the same directory (e.g. "c_" for categories, "pl_" for playlists).
/// Pagination is handled by storing pages inside the cache file.
/// </summary>
public sealed class ResultCache
{
    private readonly string _cacheDir;
    private readonly string _filePrefix;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    public bool Enabled { get; set; }
    public int MaxAgeHours { get; set; }

    /// <summary>
    /// Deletes the cache file for a specific key from the given cache directory.
    /// </summary>
    public static void InvalidateCacheFile(string key, string filePrefix = "c_", string? cacheSubDir = null)
    {
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, cacheSubDir ?? "yt_cache");
        var safe = string.Concat(key.Select(c =>
            char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_')).ToLowerInvariant();
        var path = Path.Combine(dir, filePrefix + safe + ".json");
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                DebugLog.Log("ResultCache", $"Invalidated cache file: {path}");
            }
        }
        catch (Exception ex)
        {
            DebugLog.Log("ResultCache", $"Failed to invalidate cache file {path}: {ex.Message}");
        }
    }

    public ResultCache(bool enabled, int maxAgeHours, string filePrefix = "c_", string? cacheSubDir = null)
    {
        Enabled = enabled;
        MaxAgeHours = maxAgeHours;
        _filePrefix = filePrefix;
        _cacheDir = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, cacheSubDir ?? "yt_cache");
    }

    public void UpdateSettings(bool enabled, int maxAgeHours)
    {
        Enabled = enabled;
        MaxAgeHours = maxAgeHours;
    }

    /// <summary>
    /// Builds a sanitized cache file path for the given key.
    /// </summary>
    private string GetCachePath(string key)
    {
        var safe = string.Concat(key.Select(c =>
            char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_')).ToLowerInvariant();
        return Path.Combine(_cacheDir, _filePrefix + safe + ".json");
    }

    /// <summary>
    /// Try to load a cached page. Returns null if not cached or expired.
    /// </summary>
    public List<VideoItem>? TryGetPage(string key, int pageIndex, out bool isLastPage)
    {
        isLastPage = false;
        if (!Enabled) return null;

        var path = GetCachePath(key);
        if (!File.Exists(path)) return null;

        try
        {
            var data = JsonSerializer.Deserialize<CachedResultFile>(File.ReadAllText(path), JsonOptions);
            if (data == null) return null;

            // Check expiry
            if ((DateTime.UtcNow - data.Metadata.CachedAtUtc).TotalHours > MaxAgeHours)
            {
                DebugLog.Log("ResultCache", $"Expired: {key} (age {(DateTime.UtcNow - data.Metadata.CachedAtUtc).TotalHours:F1}h > {MaxAgeHours}h)");
                try { File.Delete(path); } catch { }
                return null;
            }

            var page = data.Pages.FirstOrDefault(p => p.PageIndex == pageIndex);
            if (page == null)
            {
                DebugLog.Log("ResultCache", $"Miss (page {pageIndex} not found): {key}");
                return null;
            }

            isLastPage = page.IsLastPage;
            DebugLog.Log("ResultCache", $"Hit: {key} page {pageIndex} ({page.Items.Count} items)");
            return page.Items.Select(ToVideoItem).ToList();
        }
        catch (Exception ex)
        {
            DebugLog.Log("ResultCache", $"Failed to read cache for {key}: {ex.Message}");
            try { File.Delete(path); } catch { }
            return null;
        }
    }

    /// <summary>
    /// Store a page of results in the cache. Merges with existing pages.
    /// </summary>
    public void StorePage(string key, int pageIndex, IReadOnlyList<VideoItem> items, bool isLastPage)
    {
        if (!Enabled) return;

        try
        {
            Directory.CreateDirectory(_cacheDir);
            var path = GetCachePath(key);

            CachedResultFile data;
            if (File.Exists(path))
            {
                data = JsonSerializer.Deserialize<CachedResultFile>(File.ReadAllText(path), JsonOptions)
                       ?? new CachedResultFile();
            }
            else
            {
                data = new CachedResultFile();
            }

            data.Metadata.Source = _filePrefix.TrimEnd('_');
            data.Metadata.Name = key;
            data.Metadata.CachedAtUtc = DateTime.UtcNow;

            // Replace or add the page
            data.Pages.RemoveAll(p => p.PageIndex == pageIndex);
            data.Pages.Add(new CachedResultPage
            {
                PageIndex = pageIndex,
                IsLastPage = isLastPage,
                Items = items.Select(ToCached).ToList()
            });

            data.Metadata.TotalPages = data.Pages.Count;
            File.WriteAllText(path, JsonSerializer.Serialize(data, JsonOptions));
            DebugLog.Log("ResultCache", $"Stored: {key} page {pageIndex} ({items.Count} items)");
        }
        catch (Exception ex)
        {
            DebugLog.Log("ResultCache", $"Store error for {key}: {ex.Message}");
        }
    }

    /// <summary>
    /// Delete all cached files matching this cache's prefix.
    /// </summary>
    public void Purge()
    {
        DebugLog.Log("ResultCache", $"Purging cache files with prefix '{_filePrefix}'");
        try
        {
            if (!Directory.Exists(_cacheDir)) return;
            foreach (var file in Directory.EnumerateFiles(_cacheDir, _filePrefix + "*.json"))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Log("ResultCache", $"Purge error: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the total size of cached files matching this cache's prefix in bytes.
    /// </summary>
    public long GetSizeBytes()
    {
        try
        {
            if (!Directory.Exists(_cacheDir)) return 0;
            return new DirectoryInfo(_cacheDir)
                .EnumerateFiles(_filePrefix + "*.json", SearchOption.TopDirectoryOnly)
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
/// Root object serialized to each result cache JSON file.
/// </summary>
public class CachedResultFile
{
    public CachedResultMetadata Metadata { get; set; } = new();
    public List<CachedResultPage> Pages { get; set; } = [];
}
