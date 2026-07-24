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
    public string AudioTag { get; set; } = "";

    // Source link + favorite affordance. Without these a cache-restored row loses its star (the
    // action row's CanFavorite gate) and its owning-source link, so favoriting silently disappears.
    public string? SourceInstanceId { get; set; }
    public bool CanFavorite { get; set; }
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

    /// <summary>
    /// Cache-format stamp. Bumps when the cached item shape or app build changes so stale entries
    /// written by a previous version (or before a settings reset) are discarded on load instead of
    /// replayed — which otherwise surfaces as rows missing derived state (e.g. no favorite star).
    /// </summary>
    public string SchemaVersion { get; set; } = "";
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

    // Current cache-format stamp. Bump when CachedVideoItem's shape or restore semantics change so
    // older on-disk entries are treated as a miss. Includes the app build version so an update also
    // invalidates stale pages automatically.
    private static readonly string CurrentSchemaVersion =
        "v2+" + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0");

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
                DebugLog.Log(LogLevel.Info, "ResultCache", $"Invalidated cache file: {path}");
            }
        }
        catch (Exception ex)
        {
            DebugLog.Log(LogLevel.Warning, "ResultCache", $"Failed to invalidate cache file {path}: {ex.Message}");
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

            // Discard entries written by a different cache format / app build (e.g. before an update
            // or a settings reset). Replaying them yields rows missing derived state (no favorite
            // star, wrong source link), so treat a stamp mismatch as a miss and delete the file.
            if (data.Metadata.SchemaVersion != CurrentSchemaVersion)
            {
                DebugLog.Log(LogLevel.Info, "ResultCache", $"Stale schema ('{data.Metadata.SchemaVersion}' != '{CurrentSchemaVersion}'): {key}");
                try { File.Delete(path); } catch { }
                return null;
            }

            // Check expiry
            if ((DateTime.UtcNow - data.Metadata.CachedAtUtc).TotalHours > MaxAgeHours)
            {
                DebugLog.Log(LogLevel.Trace, "ResultCache", $"Expired: {key} (age {(DateTime.UtcNow - data.Metadata.CachedAtUtc).TotalHours:F1}h > {MaxAgeHours}h)");
                try { File.Delete(path); } catch { }
                return null;
            }

            var page = data.Pages.FirstOrDefault(p => p.PageIndex == pageIndex);
            if (page == null)
            {
                DebugLog.Log(LogLevel.Trace, "ResultCache", $"Miss (page {pageIndex} not found): {key}");
                return null;
            }

            isLastPage = page.IsLastPage;
            DebugLog.Log(LogLevel.Trace, "ResultCache", $"Hit: {key} page {pageIndex} ({page.Items.Count} items)");
            return page.Items.Select(ToVideoItem).ToList();
        }
        catch (Exception ex)
        {
            DebugLog.Log(LogLevel.Warning, "ResultCache", $"Failed to read cache for {key}: {ex.Message}");
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
                // Don't merge new pages into a stale-format file (that would re-stamp old pages as
                // current). Start fresh when the existing stamp doesn't match.
                if (data.Metadata.SchemaVersion != CurrentSchemaVersion)
                    data = new CachedResultFile();
            }
            else
            {
                data = new CachedResultFile();
            }

            data.Metadata.Source = _filePrefix.TrimEnd('_');
            data.Metadata.Name = key;
            data.Metadata.CachedAtUtc = DateTime.UtcNow;
            data.Metadata.SchemaVersion = CurrentSchemaVersion;

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
            DebugLog.Log(LogLevel.Trace, "ResultCache", $"Stored: {key} page {pageIndex} ({items.Count} items)");
        }
        catch (Exception ex)
        {
            DebugLog.Log(LogLevel.Warning, "ResultCache", $"Store error for {key}: {ex.Message}");
        }
    }

    /// <summary>
    /// Delete all cached files matching this cache's prefix.
    /// </summary>
    public void Purge()
    {
        DebugLog.Log(LogLevel.Info, "ResultCache", $"Purging cache files with prefix '{_filePrefix}'");
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
            DebugLog.Log(LogLevel.Warning, "ResultCache", $"Purge error: {ex.Message}");
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
        AudioTag = v.AudioTag,
        SourceInstanceId = v.SourceInstanceId,
        CanFavorite = v.CanFavorite,
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
        AudioTag = c.AudioTag,
        SourceInstanceId = c.SourceInstanceId,
        CanFavorite = c.CanFavorite,
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
