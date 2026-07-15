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

    // ── Generic plug-in browse tiles (any IBrowsable source: Plex, local-folder, future Jellyfin, …) ──
    /// <summary>The plug-in source instance id this tile browses, when it's a generic plug-in tile.</summary>
    public string? SourceInstanceId { get; set; }
    /// <summary>The opaque <c>SourceCategory.CategoryId</c> handed back to the source on browse.</summary>
    public string? SourceCategoryId { get; set; }
    /// <summary>The opaque <c>SourceCategory.SourceState</c> handed back to the source on browse.</summary>
    public object? SourceState { get; set; }
    /// <summary>True when this tile expands via a plug-in source's <c>IBrowsable.BrowseAsync</c>.</summary>
    public bool IsPluginBrowse { get; set; }
}
