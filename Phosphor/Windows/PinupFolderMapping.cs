namespace Phosphor;

/// <summary>
/// Helpers for the configurable Pinup window→media-folder mapping. The
/// <see cref="PinupSyncCoordinator"/> drives all screens off the canonical playfield glob
/// (…\Playfield\&lt;base&gt;.*); each follower re-points that glob to its mapped media
/// sub-folder (e.g. "BackGlass", "Topper", "Menu", "DMD", "Loading") before resolving the
/// actual file. The default map keeps each window on its own same-named folder, matching
/// the prior hardcoded behavior.
/// </summary>
public static class PinupFolderMapping
{
    /// <summary>The canonical folder segment the coordinator's globs are built against.</summary>
    public const string PlayfieldFolderToken = "Playfield";

    /// <summary>
    /// The selectable media folder options. The option name doubles as the on-disk
    /// media sub-folder name (case as it appears under a game's DirMedia).
    /// </summary>
    public static readonly string[] FolderOptions =
        ["Playfield", "BackGlass", "Topper", "Menu", "DMD", "Loading"];

    /// <summary>The windows that can have a Pinup folder mapping.</summary>
    public static readonly string[] WindowNames =
        ["Playfield", "Backglass", "Topper"];

    /// <summary>
    /// Re-points a canonical playfield glob (…\Playfield\Game.*) to the given media
    /// <paramref name="folder"/> (…\&lt;folder&gt;\Game.*). Returns the original glob when it
    /// contains no Playfield segment, or when the folder is the playfield token.
    /// </summary>
    public static string? RepointToFolder(string? playfieldGlob, string folder)
    {
        if (string.IsNullOrWhiteSpace(playfieldGlob))
            return null;
        if (string.IsNullOrWhiteSpace(folder) ||
            string.Equals(folder, PlayfieldFolderToken, StringComparison.OrdinalIgnoreCase))
            return playfieldGlob;

        return playfieldGlob.Replace(
            "\\" + PlayfieldFolderToken + "\\", "\\" + folder + "\\",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the mapped folder for <paramref name="windowName"/> from the map, falling back
    /// to the window's default same-named folder (Backglass→BackGlass) when unmapped.
    /// </summary>
    public static string GetFolder(IReadOnlyDictionary<string, string>? map, string windowName)
    {
        if (map != null && map.TryGetValue(windowName, out var folder) && !string.IsNullOrWhiteSpace(folder))
            return folder;
        return windowName switch
        {
            "Backglass" => "BackGlass",
            _ => windowName,
        };
    }
}
