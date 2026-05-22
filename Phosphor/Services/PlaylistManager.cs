using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phosphor;

public enum PlaylistKind
{
    Static,
    Live
}

public class Playlist
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    public PlaylistKind Kind { get; set; } = PlaylistKind.Static;
    public string SearchTerm { get; set; } = "";
    public List<VideoItem> Videos { get; set; } = new();
    public int SortOrder { get; set; }
    public override string ToString() => Name;
}

public class PlaylistManager
{
    private static readonly string PlaylistsPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "playlists.json");

    private readonly List<Playlist> _playlists = new();

    public IReadOnlyList<Playlist> Playlists => _playlists;

    public PlaylistManager()
    {
        Load();
        // Ensure Favorites always exists
        if (_playlists.All(p => p.Name != "Favorites"))
            _playlists.Insert(0, new Playlist { Name = "Favorites" });
    }

    public Playlist GetOrCreate(string name, string icon = "")
    {
        var existing = _playlists.FirstOrDefault(p => p.Name == name);
        if (existing != null) return existing;

        var playlist = new Playlist { Name = name, Icon = icon };
        _playlists.Add(playlist);
        Save();
        return playlist;
    }

    public Playlist CreateLivePlaylist(string name, string searchTerm, string icon = "")
    {
        var existing = _playlists.FirstOrDefault(p => p.Name == name);
        if (existing != null) return existing;

        var playlist = new Playlist { Name = name, Kind = PlaylistKind.Live, SearchTerm = searchTerm, Icon = icon };
        _playlists.Add(playlist);
        Save();
        return playlist;
    }

    public void AddToPlaylist(string playlistName, VideoItem item)
    {
        var playlist = GetOrCreate(playlistName);
        if (playlist.Kind == PlaylistKind.Live)
            return; // cannot add directly to live playlists
        if (playlist.Videos.Any(v => v.VideoId == item.VideoId))
            return; // already in playlist
        playlist.Videos.Add(item);
        Save();
    }

    public void RemoveFromPlaylist(string playlistName, VideoItem item)
    {
        var playlist = _playlists.FirstOrDefault(p => p.Name == playlistName);
        if (playlist == null) return;
        playlist.Videos.RemoveAll(v => v.VideoId == item.VideoId);
        Save();
    }

    public bool IsInPlaylist(string playlistName, string videoId)
    {
        var playlist = _playlists.FirstOrDefault(p => p.Name == playlistName);
        return playlist?.Videos.Any(v => v.VideoId == videoId) ?? false;
    }

    public void DeletePlaylist(string name)
    {
        if (name == "Favorites") return; // can't delete Favorites
        _playlists.RemoveAll(p => p.Name == name);
        Save();
    }

    public void RenamePlaylist(string oldName, string newName)
    {
        if (oldName == "Favorites") return;
        var playlist = _playlists.FirstOrDefault(p => p.Name == oldName);
        if (playlist != null)
        {
            playlist.Name = newName;
            Save();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_playlists, new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            });
            File.WriteAllText(PlaylistsPath, json);
        }
        catch { }
    }

    private void Load()
    {
        if (!File.Exists(PlaylistsPath)) return;
        try
        {
            var json = File.ReadAllText(PlaylistsPath);
            var items = JsonSerializer.Deserialize<List<Playlist>>(json, new JsonSerializerOptions
            {
                Converters = { new JsonStringEnumConverter() }
            });
            if (items != null)
            {
                // Migrate: assign IDs to playlists that don't have one
                bool needsSave = false;
                foreach (var p in items)
                {
                    if (string.IsNullOrEmpty(p.Id))
                    {
                        p.Id = Guid.NewGuid().ToString("N");
                        needsSave = true;
                    }
                }
                _playlists.AddRange(items);
                if (needsSave) Save();
            }
        }
        catch { }
    }
}
