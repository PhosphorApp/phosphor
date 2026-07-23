using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Phosphor;

/// <summary>
/// Caches thumbnail images (PNG, JPG, etc.) from YouTube and other sources
/// to a local disk folder. Prunes oldest files when size limit is exceeded.
/// </summary>
public class ThumbnailCache
{
    private static readonly string CacheDir = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "thumbnail_cache");

    private static readonly HttpClient Http = new();

    private long _maxBytes;
    private bool _enabled;

    public bool Enabled => _enabled;

    public ThumbnailCache(bool enabled, double maxSizeMb)
    {
        _enabled = enabled;
        _maxBytes = (long)(maxSizeMb * 1024 * 1024);
        Directory.CreateDirectory(CacheDir);
    }

    public void UpdateSettings(bool enabled, double maxSizeMb)
    {
        _enabled = enabled;
        _maxBytes = (long)(maxSizeMb * 1024 * 1024);

        if (!enabled)
            Purge();
        else
            Prune();
    }

    /// <summary>
    /// Returns a local file path for the given thumbnail URL, downloading it if necessary.
    /// Returns null if caching is disabled or the download fails.
    /// </summary>
    public async Task<string?> GetOrDownloadAsync(string url, CancellationToken ct = default)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(url))
            return null;

        var fileName = GetFileName(url);
        var filePath = Path.Combine(CacheDir, fileName);
        var volatileWindow = VolatileBucket(url);

        if (File.Exists(filePath) && IsFresh(filePath, volatileWindow))
        {
            // Touch file to mark as recently used
            try { File.SetLastAccessTimeUtc(filePath, DateTime.UtcNow); } catch { }
            DebugLog.Log("ThumbnailCache", $"Hit: {fileName}");
            return filePath;
        }

        try
        {
            var data = await Http.GetByteArrayAsync(url, ct);

            // Reject tiny placeholder frames (e.g. Twitch's ~1KB black "starting soon" preview served
            // in the first moments of a broadcast) for volatile URLs: don't cache, so the next refresh
            // retries and picks up the real frame once it exists. Immutable URLs are always kept.
            if (volatileWindow != null && data.Length < MinVolatileBytes)
            {
                DebugLog.Log("ThumbnailCache", $"Skipped tiny volatile thumb ({data.Length}B): {fileName}");
                return null;
            }

            await File.WriteAllBytesAsync(filePath, data, ct);
            DebugLog.Log("ThumbnailCache", $"Stored: {fileName} ({data.Length / 1024}KB) bucket={volatileWindow?.ToString() ?? "none"} url={Truncate(StripVolatileToken(url), 120)}");
            Prune();
            return filePath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns a cached file path if available and still fresh, without downloading.
    /// </summary>
    public string? TryGet(string url)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(url))
            return null;

        var filePath = Path.Combine(CacheDir, GetFileName(url));
        return File.Exists(filePath) && IsFresh(filePath, VolatileBucket(url)) ? filePath : null;
    }

    /// <summary>
    /// Returns a cached file path if one exists on disk, <em>regardless of freshness</em>, plus whether
    /// it is stale (a volatile frame whose window has rolled over) and so should be refreshed in the
    /// background. Enables stale-while-revalidate: the UI keeps showing the last-good frame instead of
    /// flashing a raw network load (which can be a black placeholder) while a refresh runs.
    /// </summary>
    public (string? Path, bool NeedsRefresh) TryGetStale(string url)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(url))
            return (null, false);

        var filePath = Path.Combine(CacheDir, GetFileName(url));
        if (!File.Exists(filePath))
            return (null, true); // nothing cached → caller should fetch
        return (filePath, !IsFresh(filePath, VolatileBucket(url)));
    }

    public void Purge()
    {
        DebugLog.Log("ThumbnailCache", "Purging all thumbnails");
        try
        {
            if (Directory.Exists(CacheDir))
            {
                foreach (var file in Directory.GetFiles(CacheDir))
                {
                    try { File.Delete(file); }
                    catch { /* file in use */ }
                }
            }
        }
        catch { /* best effort */ }
    }

    public long GetTotalSizeBytes()
    {
        try
        {
            if (!Directory.Exists(CacheDir)) return 0;
            return Directory.GetFiles(CacheDir)
                .Sum(f => new FileInfo(f).Length);
        }
        catch { return 0; }
    }

    /// <summary>
    /// Removes oldest-accessed files until total size is under the limit.
    /// </summary>
    public void Prune()
    {
        try
        {
            if (!Directory.Exists(CacheDir)) return;

            var files = new DirectoryInfo(CacheDir).GetFiles()
                .OrderBy(f => f.LastAccessTimeUtc)
                .ToList();

            var totalSize = files.Sum(f => f.Length);
            if (totalSize <= _maxBytes) return;

            foreach (var file in files)
            {
                if (totalSize <= _maxBytes) break;
                try
                {
                    totalSize -= file.Length;
                    DebugLog.Log("ThumbnailCache", $"Pruned: {file.Name} ({file.Length / 1024}KB)");
                    file.Delete();
                }
                catch { /* file in use */ }
            }
        }
        catch { /* best effort */ }
    }

    private static string GetFileName(string url)
    {
        // Hash the STABLE url (minus the volatile "_pb" cache-buster) so a channel's live preview maps
        // to ONE file that overwrites in place each window — instead of piling up a new file per bucket.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(StripVolatileToken(url))));
        var ext = GetExtension(url);
        return $"{hash}{ext}";
    }

    // ── Volatile (time-bucketed) thumbnail support ───────────────────────────────
    // Sources that serve a stable URL with mutating bytes (e.g. Twitch live previewImageURL) append a
    // "_pb={bucket}" token that rolls over every couple minutes. We key the file on the stable URL but
    // only serve it while the current window's bucket matches, so a stale/blank frame self-heals.

    private const long MinVolatileBytes = 2 * 1024; // frames smaller than this are treated as placeholders

    private static readonly Regex VolatileTokenRegex =
        new(@"[?&]_pb=(\d+)", RegexOptions.Compiled);

    /// <summary>Returns the volatile bucket value if the URL carries a "_pb" token, else null.</summary>
    private static long? VolatileBucket(string url)
    {
        var m = VolatileTokenRegex.Match(url);
        return m.Success && long.TryParse(m.Groups[1].Value, out var b) ? b : null;
    }

    /// <summary>Removes the "_pb" cache-buster so the filename is stable across buckets.</summary>
    private static string StripVolatileToken(string url) =>
        VolatileTokenRegex.Replace(url, "").TrimEnd('?', '&');

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// A cached file is fresh when the URL is immutable (no bucket) OR the file was written within the
    /// current volatile window (its mtime maps to the same bucket the caller is asking for). This makes
    /// a stable-named volatile file behave as if it expires when its window rolls over.
    /// </summary>
    private static bool IsFresh(string filePath, long? bucket)
    {
        if (bucket == null) return true; // immutable URL — always fresh
        try
        {
            var writtenBucket = new DateTimeOffset(File.GetLastWriteTimeUtc(filePath)).ToUnixTimeSeconds() / 120;
            return writtenBucket >= bucket.Value;
        }
        catch { return false; }
    }

    private static string GetExtension(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var ext = Path.GetExtension(path);
            if (ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif")
                return ext;
        }
        catch { }
        return ".jpg";
    }
}
