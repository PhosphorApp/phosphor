using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Phosphor;

/// <summary>
/// WPF value converter that intercepts thumbnail URL bindings and serves
/// cached local files when available. On cache miss, it downloads in the
/// background and returns the original URL for WPF to load from the network.
/// Once downloaded, it forces the binding to refresh so the cached file is used.
/// </summary>
public class ThumbnailCacheConverter : IValueConverter
{
    /// <summary>
    /// The shared ThumbnailCache instance, set at startup.
    /// </summary>
    public static ThumbnailCache? Cache { get; set; }

    // In-memory cache of already-decoded, frozen BitmapImages keyed by file path.
    private static readonly ConcurrentDictionary<string, BitmapImage> _decodedCache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url))
            return null;

        var cache = Cache;

        // Fast path: already decoded in memory
        if (cache != null && cache.Enabled)
        {
            // Stale-while-revalidate: serve ANY on-disk frame (fresh or stale) so the UI never flashes
            // a raw network load — which, for a volatile Twitch preview, can be a black placeholder. A
            // stale frame triggers a background refresh that only overwrites once a real frame arrives.
            var (cachedPath, needsRefresh) = cache.TryGetStale(url);
            if (cachedPath != null)
            {
                if (needsRefresh)
                {
                    DebugLog.Log("ThumbnailCache", $"Stale-serve + refresh: {System.IO.Path.GetFileName(cachedPath)}");
                    _ = cache.GetOrDownloadAsync(url);
                }

                // Key the decoded-image cache on path + last-write time so an in-place overwrite
                // (a volatile live preview refreshed in its stable-named file) invalidates the old
                // decode instead of serving a stale frame for the process lifetime.
                var decodeKey = DecodeKey(cachedPath);
                if (decodeKey != null && _decodedCache.TryGetValue(decodeKey, out var cachedBi))
                    return cachedBi;

                // Decode synchronously from disk at a downscaled size and return it now.
                // A 160px decode is cheap and produces a small texture, so it does NOT
                // stall the shared render thread the way handing WPF a raw full-size
                // image did.
                var localBi = CreateLocalBitmapImage(cachedPath);
                if (localBi != null)
                {
                    if (decodeKey != null) _decodedCache[decodeKey] = localBi;
                    return localBi;
                }
            }
            else
            {
                // Nothing cached at all — start caching it for next time. We still fall through to a
                // remote load below so SOMETHING shows on this first appearance.
                DebugLog.Log("ThumbnailCache", $"Miss (no file), remote-load: {url}");
                _ = cache.GetOrDownloadAsync(url);
            }
        }

        // No cached frame exists yet (or caching disabled): load directly from the remote URL, but
        // decode at a downscaled size so WPF only uploads a small texture to the
        // shared render thread. This is what prevents the large-Plex-thumbnail
        // stutter while still always returning something displayable.
        return CreateRemoteBitmapImage(url);
    }

    private static BitmapImage? CreateRemoteBitmapImage(string url)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnDemand;
            bi.DecodePixelWidth = 160;
            bi.UriSource = new Uri(url, UriKind.Absolute);
            bi.EndInit();
            return bi;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>
    /// A decoded-cache key that changes when the file is overwritten in place (path + last-write time),
    /// so a refreshed volatile thumbnail isn't masked by a stale in-memory decode. Null if the file
    /// can't be stat'd (caller then falls back to a fresh decode).
    /// </summary>
    private static string? DecodeKey(string filePath)
    {
        try { return $"{filePath}|{File.GetLastWriteTimeUtc(filePath).Ticks}"; }
        catch { return null; }
    }

    private static BitmapImage? CreateLocalBitmapImage(string filePath)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bi.DecodePixelWidth = 160;
            bi.StreamSource = new MemoryStream(bytes);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            return null;
        }
    }
}
