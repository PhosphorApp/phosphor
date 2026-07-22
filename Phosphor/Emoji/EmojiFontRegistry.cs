using System.Collections.Generic;
using System.IO;

namespace Phosphor.EmojiIcons;

/// <summary>
/// The selectable color emoji font sets bundled with Phosphor. Used only when
/// <see cref="Phosphor.Models.IconStyle.Color"/> is active. The chosen set is
/// persisted via <c>AppSettings.DmdEmojiFontSet</c>.
/// </summary>
public enum EmojiFontSet
{
    Twemoji,
    OpenMoji,
    FluentFlat,
    SegoeSystem,
    Noto,
}

/// <summary>
/// Static metadata describing an emoji font: its <see cref="EmojiFontSet"/> key,
/// the user-facing display name, and the font file name. Bundled fonts live in the
/// <c>EmojiFonts</c> folder next to the executable; when <paramref name="IsSystemFont"/>
/// is true the file is resolved from the OS Fonts folder instead (e.g. Segoe UI Emoji,
/// which is what the legacy Emoji.Wpf renderer used).
/// </summary>
public sealed record EmojiFontInfo(EmojiFontSet Set, string DisplayName, string FileName, bool IsSystemFont = false);

/// <summary>
/// Central registry mapping each <see cref="EmojiFontSet"/> to its bundled font
/// file and display metadata. This is the single source of truth for which fonts
/// exist and where their files live, so the settings UI and the renderer stay in
/// sync.
/// </summary>
public static class EmojiFontRegistry
{
    /// <summary>Subfolder (relative to the executable) that holds the bundled .ttf files.</summary>
    public const string FontFolderName = "EmojiFonts";

    private static readonly IReadOnlyList<EmojiFontInfo> _all =
    [
        new(EmojiFontSet.SegoeSystem, "Segoe (Windows)", "seguiemj.ttf", IsSystemFont: true),
        new(EmojiFontSet.Twemoji, "Twemoji", "TwemojiMozilla.ttf"),
        new(EmojiFontSet.OpenMoji, "OpenMoji", "OpenMoji-color.ttf"),
        new(EmojiFontSet.Noto, "Noto Color", "NotoColorEmoji.ttf"),
        new(EmojiFontSet.FluentFlat, "Fluent Flat", "FluentEmojiFlat.ttf"),
    ];

    /// <summary>All registered emoji font sets, in display order.</summary>
    public static IReadOnlyList<EmojiFontInfo> All => _all;

    /// <summary>Looks up the metadata for a given set, falling back to Twemoji if unknown.</summary>
    public static EmojiFontInfo Get(EmojiFontSet set)
    {
        foreach (var info in _all)
        {
            if (info.Set == set)
                return info;
        }
        return _all[0];
    }

    /// <summary>Returns the absolute path to the font file for the given set.</summary>
    public static string GetFontPath(EmojiFontSet set)
    {
        var info = Get(set);
        if (info.IsSystemFont)
        {
            var fontsDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Fonts);
            return Path.Combine(fontsDir, info.FileName);
        }
        return Path.Combine(System.AppContext.BaseDirectory, FontFolderName, info.FileName);
    }
}
