namespace Phosphor;

public class Category
{
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    public string SearchTerm { get; set; } = "";
    public bool IsSeparator { get; set; }
    public bool IsLineBreak { get; set; }
    public bool IsPlaylist { get; set; }
    public bool IsNewPlaylist { get; set; }
    public string? PlexLibraryKey { get; set; }
    public string? PlexLibraryType { get; set; }
    /// <summary>The Plex source instance this tile belongs to (multi-server). Null = legacy/first server.</summary>
    public string? PlexInstanceId { get; set; }
    public string? PlexHubKey { get; set; }
    public string? PlexHubType { get; set; }
    public string? PlexPlaylistKey { get; set; }
    public bool IsPlexHubList { get; set; }
    public bool IsPlexPlaylistList { get; set; }
    public bool IsPlex => PlexLibraryKey != null;
    public bool IsPlexHub => PlexHubKey != null;
    public bool IsPlexPlaylist => PlexPlaylistKey != null;

    // ── Generic plug-in browse tiles (any IBrowsable source: local-folder, future Jellyfin, …) ──
    /// <summary>The plug-in source instance id this tile browses, when it's a generic plug-in tile.</summary>
    public string? SourceInstanceId { get; set; }
    /// <summary>The opaque <c>SourceCategory.CategoryId</c> handed back to the source on browse.</summary>
    public string? SourceCategoryId { get; set; }
    /// <summary>The opaque <c>SourceCategory.SourceState</c> handed back to the source on browse.</summary>
    public object? SourceState { get; set; }
    /// <summary>True when this tile expands via a plug-in source's <c>IBrowsable.BrowseAsync</c>.</summary>
    public bool IsPluginBrowse { get; set; }
}
