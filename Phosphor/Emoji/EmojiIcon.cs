using System.Windows;
using System.Windows.Controls;
using Brush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using ImageBrush = System.Windows.Media.ImageBrush;
using Stretch = System.Windows.Media.Stretch;
using WpfImage = System.Windows.Controls.Image;
using WpfRectangle = System.Windows.Shapes.Rectangle;
using WpfHAlign = System.Windows.HorizontalAlignment;
using WpfVAlign = System.Windows.VerticalAlignment;

namespace Phosphor.EmojiIcons;

/// <summary>
/// A self-contained control that renders a single emoji/icon string according to the
/// active <see cref="IconStyle"/>:
/// <list type="bullet">
/// <item><b>Default</b> — the raw string via a <see cref="TextBlock"/> (OS emoji font).</item>
/// <item><b>Color</b> — a colored image rasterized from the selected <see cref="EmojiFontSet"/>.</item>
/// <item><b>Themed</b> — the glyph shape filled with <see cref="TintBrush"/>.</item>
/// </list>
/// Whenever the selected font has no glyph for the string (e.g. non-emoji symbols such as
/// the New Playlist '＋'), it automatically falls back to the <see cref="TextBlock"/>, so no
/// icon is ever blank. This unifies the DMD category-tile and result-row rendering that used
/// to rely on multiple swapped DataTemplates.
/// </summary>
public sealed class EmojiIcon : ContentControl
{
    public static readonly DependencyProperty EmojiProperty = DependencyProperty.Register(
        nameof(Emoji), typeof(string), typeof(EmojiIcon),
        new FrameworkPropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty IconStyleProperty = DependencyProperty.Register(
        nameof(IconStyle), typeof(IconStyle), typeof(EmojiIcon),
        new FrameworkPropertyMetadata(IconStyle.Color, OnVisualChanged));

    public static readonly DependencyProperty FontSetProperty = DependencyProperty.Register(
        nameof(FontSet), typeof(EmojiFontSet), typeof(EmojiIcon),
        new FrameworkPropertyMetadata(EmojiFontSet.SegoeSystem, OnVisualChanged));

    public static readonly DependencyProperty TintBrushProperty = DependencyProperty.Register(
        nameof(TintBrush), typeof(Brush), typeof(EmojiIcon),
        new FrameworkPropertyMetadata(WpfBrushes.White, OnVisualChanged));

    public static readonly DependencyProperty SilhouetteBrushProperty = DependencyProperty.Register(
        nameof(SilhouetteBrush), typeof(Brush), typeof(EmojiIcon),
        new FrameworkPropertyMetadata(WpfBrushes.Gainsboro, OnVisualChanged));

    public static readonly DependencyProperty DisplaySizeProperty = DependencyProperty.Register(
        nameof(DisplaySize), typeof(double), typeof(EmojiIcon),
        new FrameworkPropertyMetadata(24.0, OnVisualChanged));

    public string? Emoji
    {
        get => (string?)GetValue(EmojiProperty);
        set => SetValue(EmojiProperty, value);
    }

    public IconStyle IconStyle
    {
        get => (IconStyle)GetValue(IconStyleProperty);
        set => SetValue(IconStyleProperty, value);
    }

    public EmojiFontSet FontSet
    {
        get => (EmojiFontSet)GetValue(FontSetProperty);
        set => SetValue(FontSetProperty, value);
    }

    public Brush TintBrush
    {
        get => (Brush)GetValue(TintBrushProperty);
        set => SetValue(TintBrushProperty, value);
    }

    /// <summary>Fill used for the Silhouette style (flat single-tone glyph shape).</summary>
    public Brush SilhouetteBrush
    {
        get => (Brush)GetValue(SilhouetteBrushProperty);
        set => SetValue(SilhouetteBrushProperty, value);
    }

    public double DisplaySize
    {
        get => (double)GetValue(DisplaySizeProperty);
        set => SetValue(DisplaySizeProperty, value);
    }

    public EmojiIcon()
    {
        HorizontalContentAlignment = WpfHAlign.Center;
        VerticalContentAlignment = WpfVAlign.Center;
        Rebuild();
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((EmojiIcon)d).Rebuild();

    private void Rebuild()
    {
        var emoji = Emoji;
        if (string.IsNullOrEmpty(emoji))
        {
            Content = null;
            return;
        }

        double size = DisplaySize;
        // Render at 2x for crispness on high-DPI displays.
        int pixelSize = (int)System.Math.Ceiling(size * 2);

        // Color: the glyph as-is. Desaturated: the glyph rendered to grayscale.
        if (IconStyle == IconStyle.Color || IconStyle == IconStyle.Desaturated)
        {
            bool gray = IconStyle == IconStyle.Desaturated;
            var img = EmojiRenderer.GetImage(emoji, FontSet, pixelSize, gray);
            if (img != null)
            {
                Content = new WpfImage
                {
                    Source = img,
                    Width = size,
                    Height = size,
                    Stretch = Stretch.Uniform,
                    SnapsToDevicePixels = true,
                };
                return;
            }
            // Glyph missing in this font — fall back to text below.
        }
        else
        {
            // Silhouette: flat single-tone (gray) fill of the glyph shape.
            // Themed: same, filled with the accent (theme) brush.
            var mask = EmojiRenderer.GetImage(emoji, FontSet, pixelSize);
            if (mask != null)
            {
                var fill = IconStyle == IconStyle.Themed ? TintBrush : SilhouetteBrush;
                Content = new WpfRectangle
                {
                    Width = size,
                    Height = size,
                    Fill = fill,
                    OpacityMask = new ImageBrush(mask) { Stretch = Stretch.Uniform },
                    SnapsToDevicePixels = true,
                };
                return;
            }
            // Glyph missing — fall back to text below.
        }

        // Fallback when the font lacks the glyph (e.g. the non-emoji New Playlist '＋'):
        // render the raw string as text, honoring the tint for Themed/Silhouette styles.
        var textFallback = new TextBlock
        {
            Text = emoji,
            FontSize = size,
            HorizontalAlignment = WpfHAlign.Center,
            VerticalAlignment = WpfVAlign.Center,
            TextAlignment = TextAlignment.Center,
        };
        if (IconStyle == IconStyle.Themed)
            textFallback.Foreground = TintBrush;
        else if (IconStyle == IconStyle.Silhouette)
            textFallback.Foreground = SilhouetteBrush;
        Content = textFallback;
    }
}
