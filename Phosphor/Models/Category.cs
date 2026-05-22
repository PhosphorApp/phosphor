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
    public string? PlexHubKey { get; set; }
    public string? PlexHubType { get; set; }
    public string? PlexPlaylistKey { get; set; }
    public bool IsPlexHubList { get; set; }
    public bool IsPlexPlaylistList { get; set; }
    public bool IsPlex => PlexLibraryKey != null;
    public bool IsPlexHub => PlexHubKey != null;
    public bool IsPlexPlaylist => PlexPlaylistKey != null;
}
