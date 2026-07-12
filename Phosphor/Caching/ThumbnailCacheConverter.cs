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
            var cachedPath = cache.TryGet(url);
            if (cachedPath != null)
            {
                if (_decodedCache.TryGetValue(cachedPath, out var cachedBi))
                    return cachedBi;

                // Decode synchronously from disk at a downscaled size and return it now.
                // A 160px decode is cheap and produces a small texture, so it does NOT
                // stall the shared render thread the way handing WPF a raw full-size
                // image did.
                var localBi = CreateLocalBitmapImage(cachedPath);
                if (localBi != null)
                {
                    _decodedCache[cachedPath] = localBi;
                    return localBi;
                }
            }
            else
            {
                // Not on disk yet — start caching it for next time.
                _ = cache.GetOrDownloadAsync(url);
            }
        }

        // Cache miss (or caching disabled): load directly from the remote URL, but
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
