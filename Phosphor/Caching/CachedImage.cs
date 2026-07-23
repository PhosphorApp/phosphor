using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Image = System.Windows.Controls.Image;

namespace Phosphor;

/// <summary>
/// Attached property that loads an <see cref="Image"/>'s source through the shared
/// <see cref="ThumbnailCache"/> asynchronously and ROBUSTLY. Unlike a one-shot value converter, this
/// re-evaluates: on a cold cache miss it downloads via our own <c>HttpClient</c> (not WPF's built-in
/// WinINet loader, which fails silently and never rebinds), decodes a frozen bitmap from disk, and
/// assigns <see cref="Image.Source"/> when the file lands. A per-element generation token guards
/// against ListBox container recycling so a slow download can't paint onto a reused row.
/// </summary>
public static class CachedImage
{
    /// <summary>The shared <see cref="ThumbnailCache"/> instance, set at startup.</summary>
    public static ThumbnailCache? Cache { get; set; }

    // Decoded, frozen bitmaps keyed on path + last-write time so an in-place overwrite (a refreshed
    // volatile preview) invalidates the stale decode instead of serving it for the process lifetime.
    private static readonly ConcurrentDictionary<string, BitmapImage> _decodedCache = new();

    public static readonly DependencyProperty SourceUrlProperty =
        DependencyProperty.RegisterAttached(
            "SourceUrl", typeof(string), typeof(CachedImage),
            new PropertyMetadata(null, OnSourceUrlChanged));

    public static void SetSourceUrl(DependencyObject o, string? value) => o.SetValue(SourceUrlProperty, value);
    public static string? GetSourceUrl(DependencyObject o) => (string?)o.GetValue(SourceUrlProperty);

    // Monotonic per-element token: every url change bumps it, so an in-flight async load that resolves
    // after the element was recycled/rebound is discarded instead of overwriting the current image.
    private static readonly DependencyProperty GenerationProperty =
        DependencyProperty.RegisterAttached(
            "Generation", typeof(int), typeof(CachedImage), new PropertyMetadata(0));

    private static void OnSourceUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image img) return;

        var gen = (int)img.GetValue(GenerationProperty) + 1;
        img.SetValue(GenerationProperty, gen);

        var url = e.NewValue as string;
        if (string.IsNullOrWhiteSpace(url))
        {
            // Clear the local value so Style/DataTrigger fallbacks (e.g. default_thumb.png) can apply.
            img.ClearValue(Image.SourceProperty);
            return;
        }

        var cache = Cache;
        DebugLog.Log(LogLevel.Trace, "CachedImage", $"OnChanged url='{Trim(url)}' cache={(cache == null ? "null" : cache.Enabled ? "on" : "off")}");

        // Fast path: serve any on-disk frame (fresh or stale) synchronously so the tile paints now.
        if (cache != null && cache.Enabled)
        {
            var (cachedPath, needsRefresh) = cache.TryGetStale(url);
            if (cachedPath != null)
            {
                var bmp = LoadFrozen(cachedPath);
                if (bmp != null)
                {
                    DebugLog.Log(LogLevel.Trace, "CachedImage", $"Sync disk hit frozen={bmp.IsFrozen} '{Trim(url)}'");
                    img.Source = bmp;
                    if (needsRefresh) _ = RefreshStaleAsync(img, url, gen, cache);
                    return;
                }
                DebugLog.Log(LogLevel.Warning, "CachedImage", $"Disk decode FAILED '{cachedPath}'");
            }
        }

        // Cold miss (or unreadable file): download via our HttpClient, then decode frozen from disk.
        _ = LoadAsync(img, url, gen, cache);
    }

    private static string Trim(string s) => s.Length <= 60 ? s : s[..60] + "…";

    private static async Task LoadAsync(Image img, string url, int gen, ThumbnailCache? cache)
    {
        try
        {
            if (cache != null && cache.Enabled)
            {
                var path = await cache.GetOrDownloadAsync(url).ConfigureAwait(false);
                var bmp = path != null ? LoadFrozen(path) : null;
                if (bmp != null)
                {
                    DebugLog.Log(LogLevel.Trace, "CachedImage", $"Async disk load frozen={bmp.IsFrozen} '{Trim(url)}'");
                    ApplyIfCurrent(img, bmp, gen);
                    return;
                }
            }

            // Cache disabled, or the disk download/decode failed: fall back to WPF's own UriSource
            // loader ON THE UI THREAD. It handles auth cookies, redirects, relative and pack URIs that
            // a raw HttpClient can't — and an unfrozen bitmap is legal when created on the UI thread.
            DebugLog.Log(LogLevel.Trace, "CachedImage", $"Fallback to UI UriSource '{Trim(url)}'");
            ApplyRemoteOnUi(img, url, gen);
        }
        catch (Exception ex)
        {
            DebugLog.Log(LogLevel.Warning, "CachedImage", $"LoadAsync error '{Trim(url)}': {ex.Message}");
        }
    }

    // Loads via WPF's UriSource on the UI thread, matching the original converter's remote behavior.
    private static void ApplyRemoteOnUi(Image img, string url, int gen)
    {
        void Load()
        {
            if ((int)img.GetValue(GenerationProperty) != gen) return;
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnDemand;
                bi.DecodePixelWidth = 160;
                bi.UriSource = new Uri(url, UriKind.RelativeOrAbsolute);
                bi.EndInit();
                img.Source = bi;
            }
            catch (Exception ex)
            {
                DebugLog.Log(LogLevel.Warning, "CachedImage", $"UriSource load failed '{Trim(url)}': {ex.Message}");
            }
        }

        if (img.Dispatcher.CheckAccess()) Load();
        else img.Dispatcher.BeginInvoke((Action)Load);
    }

    private static async Task RefreshStaleAsync(Image img, string url, int gen, ThumbnailCache cache)
    {
        try
        {
            var path = await cache.GetOrDownloadAsync(url).ConfigureAwait(false);
            if (path == null) return;
            var bmp = LoadFrozen(path);
            if (bmp != null) ApplyIfCurrent(img, bmp, gen);
        }
        catch { /* best effort */ }
    }

    private static void ApplyIfCurrent(Image img, BitmapImage bmp, int gen)
    {
        // Only frozen bitmaps may cross threads; assigning an unfrozen one to Image.Source on the UI
        // thread throws a cross-thread VerifyAccess. Every producer freezes, but guard defensively.
        if (!bmp.IsFrozen) return;

        void Assign()
        {
            var current = (int)img.GetValue(GenerationProperty);
            if (current == gen)
                img.Source = bmp;
            else
                DebugLog.Log(LogLevel.Trace, "CachedImage", $"Skipped stale assign gen={gen} current={current}");
        }

        if (img.Dispatcher.CheckAccess()) Assign();
        else img.Dispatcher.BeginInvoke((Action)Assign);
    }

    private static BitmapImage? LoadFrozen(string filePath)
    {
        var key = DecodeKey(filePath);
        if (key != null && _decodedCache.TryGetValue(key, out var cached))
            return cached;

        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            // Decode from the file URI (not a byte StreamSource): the WIC WebP codec — which YouTube
            // now serves under .jpg URLs — fails the stream-decode path ("Value cannot be null,
            // Parameter 'key'") but decodes fine from a file URI. OnLoad reads fully at EndInit so no
            // file lock lingers (in-place overwrites of volatile thumbs still work).
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bi.DecodePixelWidth = 160;
            bi.UriSource = new Uri(filePath, UriKind.Absolute);
            bi.EndInit();
            bi.Freeze();
            if (key != null) _decodedCache[key] = bi;
            return bi;
        }
        catch
        {
            return null;
        }
    }

    // FROZEN bitmap safe to hand to the UI thread.
    private static string? DecodeKey(string filePath)
    {
        try { return $"{filePath}|{File.GetLastWriteTimeUtc(filePath).Ticks}"; }
        catch { return null; }
    }
}
