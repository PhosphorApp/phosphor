using System;
using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace Phosphor.EmojiIcons;

/// <summary>
/// Renders emoji glyphs from a chosen color <see cref="EmojiFontSet"/> into frozen,
/// cross-thread-safe WPF <see cref="ImageSource"/> instances using SkiaSharp.
///
/// SkiaSharp handles the COLR/CPAL color-font format our bundled fonts use
/// (Twemoji, OpenMoji, Fluent Flat) and system Segoe UI Emoji, so a single code path
/// covers all sets. Results are cached by (emoji, set, pixelSize) because the DMD
/// renders many tiles at a handful of fixed sizes.
/// </summary>
public static class EmojiRenderer
{
    // Cache key is emoji + set + pixel size. Values are frozen so they can be used
    // from PlayfieldWindow/BackglassWindow threads as well as the main dispatcher.
    private static readonly ConcurrentDictionary<string, ImageSource?> _cache = new();

    // Typefaces are expensive to load (Fluent Color is ~84 MB), so keep one per set.
    private static readonly ConcurrentDictionary<EmojiFontSet, SKTypeface?> _typefaces = new();

    /// <summary>
    /// Returns a frozen <see cref="ImageSource"/> for the given emoji rendered from the
    /// specified font set at the requested pixel size, or <c>null</c> if the font is
    /// unavailable or the glyph cannot be rendered (caller should fall back to text).
    /// </summary>
    public static ImageSource? GetImage(string emoji, EmojiFontSet set, int pixelSize)
        => GetImage(emoji, set, pixelSize, grayscale: false);

    /// <summary>
    /// As <see cref="GetImage(string, EmojiFontSet, int)"/>, but when <paramref name="grayscale"/>
    /// is true the glyph's colors are desaturated to grayscale (internal shading preserved).
    /// </summary>
    public static ImageSource? GetImage(string emoji, EmojiFontSet set, int pixelSize, bool grayscale)
    {
        if (string.IsNullOrEmpty(emoji) || pixelSize <= 0)
            return null;

        var key = $"{(int)set}|{pixelSize}|{(grayscale ? "g" : "c")}|{emoji}";
        return _cache.GetOrAdd(key, _ => Render(emoji, set, pixelSize, grayscale));
    }

    /// <summary>
    /// Removes Unicode presentation/variation selectors (U+FE0E text, U+FE0F emoji) which are
    /// zero-width modifiers. Without OpenType shaping these would render as stray tofu boxes.
    /// </summary>
    private static string StripPresentationSelectors(string text)
    {
        if (text.IndexOf('\uFE0F') < 0 && text.IndexOf('\uFE0E') < 0)
            return text;
        return text.Replace("\uFE0F", string.Empty).Replace("\uFE0E", string.Empty);
    }

    /// <summary>
    /// Returns true if the font provides a glyph for every text element in <paramref name="text"/>
    /// (handling surrogate pairs). Non-emoji symbols such as the fullwidth plus '＋' return false,
    /// letting callers fall back to plain text rendering.
    /// </summary>
    private static bool FontHasGlyphs(SKFont font, string text)
    {
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        bool any = false;
        while (enumerator.MoveNext())
        {
            any = true;
            var element = (string)enumerator.Current;
            int cp = char.ConvertToUtf32(element, 0);
            if (!font.ContainsGlyph(cp))
                return false;
        }
        return any;
    }

    private static SKTypeface? GetTypeface(EmojiFontSet set)
    {
        return _typefaces.GetOrAdd(set, s =>
        {
            var path = EmojiFontRegistry.GetFontPath(s);
            try
            {
                if (!File.Exists(path))
                {
                    DebugLog.Log("EmojiRenderer", $"Font file missing for {s}: {path}");
                    return null;
                }
                return SKTypeface.FromFile(path);
            }
            catch (Exception ex)
            {
                DebugLog.Log("EmojiRenderer", $"Failed to load typeface {s}: {ex.Message}");
                return null;
            }
        });
    }

    private static ImageSource? Render(string emoji, EmojiFontSet set, int pixelSize, bool grayscale)
    {
        var typeface = GetTypeface(set);
        if (typeface == null)
            return null;

        // Strip presentation/variation selectors (VS-15 U+FE0E, VS-16 U+FE0F). SkiaSharp
        // does no OpenType shaping, so these zero-width modifiers would otherwise render as
        // a separate ".notdef" tofu box beside the emoji (e.g. the candle 🕯️ = U+1F56F+FE0F).
        emoji = StripPresentationSelectors(emoji);

        try
        {
            // Create the font up front so the glyph-coverage check and the actual draw
            // share it. Rendering uses ink-bounds scale-to-fit + center (font-agnostic),
            // avoiding advance/vertical metrics that are unreliable for some color fonts.
            using var measureFont = new SKFont(typeface, pixelSize);

            // If the font has no glyph for this text (e.g. non-emoji symbols like the
            // fullwidth plus '＋' used by the New Playlist tile), bail so the caller can
            // fall back to rendering the raw string as text.
            if (!FontHasGlyphs(measureFont, emoji))
                return null;

            using var paint = new SKPaint { IsAntialias = true };
            if (grayscale)
            {
                // Desaturate to grayscale while preserving internal shading/alpha.
                paint.ColorFilter = SKColorFilter.CreateColorMatrix(
                [
                    0.2126f, 0.7152f, 0.0722f, 0, 0,
                    0.2126f, 0.7152f, 0.0722f, 0, 0,
                    0.2126f, 0.7152f, 0.0722f, 0, 0,
                    0,       0,       0,       1, 0,
                ]);
            }

            // Measured ink bounds relative to the text origin (baseline at y=0).
            SKRect bounds = default;
            measureFont.MeasureText(emoji, out bounds, paint);

            var info = new SKImageInfo(pixelSize, pixelSize, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            if (bounds.Width > 0 && bounds.Height > 0)
            {
                // Leave a small margin so glyphs don't touch the edges.
                float target = pixelSize * 0.92f;
                float scale = System.Math.Min(target / bounds.Width, target / bounds.Height);

                // Draw at origin, then translate so the scaled ink box is centered.
                canvas.Save();
                canvas.Translate(pixelSize / 2f, pixelSize / 2f);
                canvas.Scale(scale);
                // Center of the ink box should map to (0,0) after the translate above.
                float cx = bounds.MidX;
                float cy = bounds.MidY;
                canvas.DrawText(emoji, -cx, -cy, SKTextAlign.Left, measureFont, paint);
                canvas.Restore();
            }
            else
            {
                // Fallback: simple centered draw (e.g. zero-bounds edge cases).
                canvas.DrawText(emoji, pixelSize / 2f, pixelSize * 0.75f, SKTextAlign.Center, measureFont, paint);
            }
            canvas.Flush();

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            if (data == null)
                return null;

            using var stream = new MemoryStream();
            data.SaveTo(stream);
            stream.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze(); // cross-thread safe for Playfield/Backglass windows
            return bitmap;
        }
        catch (Exception ex)
        {
            DebugLog.Log("EmojiRenderer", $"Render failed for '{emoji}' set={set} size={pixelSize}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Clears cached images and typefaces (e.g., for diagnostics or hot-reload).</summary>
    public static void ClearCache()
    {
        _cache.Clear();
        foreach (var tf in _typefaces.Values)
            tf?.Dispose();
        _typefaces.Clear();
    }
}
