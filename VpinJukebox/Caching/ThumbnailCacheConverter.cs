using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace VpinJukebox;

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

    // Tracks paths currently being decoded to avoid duplicate background work.
    private static readonly ConcurrentDictionary<string, byte> _decodingInProgress = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url))
            return null;

        var cache = Cache;
        if (cache == null || !cache.Enabled)
            return new Uri(url, UriKind.Absolute);

        // Fast path: already cached on disk
        var cachedPath = cache.TryGet(url);
        if (cachedPath != null)
        {
            // Already decoded in memory — return immediately, no UI-thread work
            if (_decodedCache.TryGetValue(cachedPath, out var cached))
                return cached;

            // Decode off the UI thread for future instant hits;
            // return file URI so WPF can display it now
            _ = DecodeAndRefreshAsync(cachedPath, url);
            return new Uri(cachedPath, UriKind.Absolute);
        }

        // Cache miss: kick off background download, return URL as-is for WPF to load
        // Once downloaded, we notify the source property to re-evaluate the binding
        _ = DownloadAndRefreshAsync(url);

        return new Uri(url, UriKind.Absolute);
    }

    private static async Task DecodeAndRefreshAsync(string filePath, string url)
    {
        if (!_decodingInProgress.TryAdd(filePath, 0))
            return;

        try
        {
            var bi = await Task.Run(() => CreateLocalBitmapImage(filePath));
            if (bi != null)
            {
                _decodedCache[filePath] = bi;
                await Application.Current.Dispatcher.InvokeAsync(() => RefreshBindingsForUrl(url));
            }
        }
        finally
        {
            _decodingInProgress.TryRemove(filePath, out _);
        }
    }

    private static async Task DownloadAndRefreshAsync(string url)
    {
        var cache = Cache;
        if (cache == null) return;

        var path = await cache.GetOrDownloadAsync(url);
        if (path == null) return;

        // Pre-decode off UI thread
        var bi = await Task.Run(() => CreateLocalBitmapImage(path));
        if (bi != null)
            _decodedCache[path] = bi;

        // Force all active bindings with this URL to re-evaluate by
        // touching the ThumbnailUrl property on any VideoItem that uses it.
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            RefreshBindingsForUrl(url);
        });
    }

    /// <summary>
    /// Finds all VideoItem instances in the current view model that reference
    /// this thumbnail URL and raises PropertyChanged so bindings re-evaluate.
    /// </summary>
    private static void RefreshBindingsForUrl(string url)
    {
        var window = Application.Current.MainWindow;
        if (window?.DataContext is not JukeboxViewModel vm) return;

        foreach (var item in vm.SearchResults)
        {
            if (item.ThumbnailUrl == url)
                item.NotifyPropertyChanged(nameof(VideoItem.ThumbnailUrl));
        }
        foreach (var item in vm.Queue)
        {
            if (item.ThumbnailUrl == url)
                item.NotifyPropertyChanged(nameof(VideoItem.ThumbnailUrl));
        }
        if (vm.CurrentlyPlaying?.ThumbnailUrl == url)
            vm.CurrentlyPlaying.NotifyPropertyChanged(nameof(VideoItem.ThumbnailUrl));
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
