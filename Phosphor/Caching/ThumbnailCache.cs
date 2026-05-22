using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

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

        if (File.Exists(filePath))
        {
            // Touch file to mark as recently used
            try { File.SetLastAccessTimeUtc(filePath, DateTime.UtcNow); } catch { }
            DebugLog.Log("ThumbnailCache", $"Hit: {fileName}");
            return filePath;
        }

        try
        {
            var data = await Http.GetByteArrayAsync(url, ct);
            await File.WriteAllBytesAsync(filePath, data, ct);
            DebugLog.Log("ThumbnailCache", $"Stored: {fileName} ({data.Length / 1024}KB)");
            Prune();
            return filePath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns a cached file path if available, without downloading.
    /// </summary>
    public string? TryGet(string url)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(url))
            return null;

        var filePath = Path.Combine(CacheDir, GetFileName(url));
        return File.Exists(filePath) ? filePath : null;
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
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        var ext = GetExtension(url);
        return $"{hash}{ext}";
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
