using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phosphor;

/// <summary>
/// A single Pinup Popper playlist row (from the POPPER SQLite database), plus a
/// user-controlled <see cref="Enabled"/> flag that Phosphor uses to decide whether
/// the playlist participates in the forthcoming Pinup integration feature.
/// </summary>
public class PinupPlaylist
{
    /// <summary>PlayListID from the POPPER database (stable identity for merging enabled state).</summary>
    public int PlayListID { get; set; }
    /// <summary>PlayDisplay from the POPPER database — the human-readable playlist name.</summary>
    public string Name { get; set; } = "";
    /// <summary>DisplayOrder from the POPPER database (used to order the list).</summary>
    public int DisplayOrder { get; set; }
    /// <summary>PlayListType from the POPPER database (0 or 1).</summary>
    public int PlayListType { get; set; }
    /// <summary>PlayListSQL from the POPPER database (custom query for SQL-backed playlists).</summary>
    public string PlayListSQL { get; set; } = "";
    /// <summary>Whether the user has enabled this playlist for the Pinup integration feature.</summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// A single game/emulator entry resolved from the checked Pinup playlists. Built at
/// runtime from the POPPER database (games change frequently) and never persisted.
/// </summary>
public class PinupGame
{
    /// <summary>DirMedia from the Emulators table (media root for the game's emulator).</summary>
    public string DirMedia { get; set; } = "";
    /// <summary>Computed playfield video path glob (DirMedia\Playfield\&lt;gamefile-no-ext&gt;.*).</summary>
    public string PlayfieldVideoFilename { get; set; } = "";
    public int EmuId { get; set; }
    public string GameFileName { get; set; } = "";
    public int GameId { get; set; }
    public string GameDisplay { get; set; } = "";
}

/// <summary>
/// Pinup Popper integration settings. Persisted separately from <see cref="AppSettings"/>
/// (in <c>pinup_integration.json</c>) so large Pinup installations don't bloat the main
/// settings file.
/// </summary>
public class PinupSettings
{
    /// <summary>Full path to the user's Pinup Popper database (PUPDatabase.db).</summary>
    public string PopperDbPath { get; set; } = "";

    /// <summary>
    /// Cached list of visible playlists (with per-playlist enabled state). Refreshed from
    /// the database on demand; enabled flags are merged by <see cref="PinupPlaylist.PlayListID"/>.
    /// </summary>
    public List<PinupPlaylist> Playlists { get; set; } = [];

    /// <summary>
    /// Runtime-built list of games resolved from the checked playlists. Not persisted
    /// (games change frequently and are rebuilt on startup/refresh). This is the list of
    /// playfield media files the Pinup Playlist feature ultimately plays from.
    /// </summary>
    [JsonIgnore]
    public List<PinupGame> Games { get; set; } = [];

    /// <summary>
    /// Default POPPER database path suggested when it exists on disk (standard vPinball install).
    /// </summary>
    public const string DefaultPopperDbPath = @"C:\vPinball\PinUPSystem\PUPDatabase.db";

    private static readonly string SettingsPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "pinup_integration.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static PinupSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<PinupSettings>(json);
                if (loaded != null)
                    return loaded;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Log(LogLevel.Warning, "PinupSettings", $"Load failed: {ex.Message}");
        }

        var fresh = new PinupSettings();
        if (File.Exists(DefaultPopperDbPath))
            fresh.PopperDbPath = DefaultPopperDbPath;
        return fresh;
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                File.WriteAllText(SettingsPath, json);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(100);
            }
            catch (IOException ex)
            {
                DebugLog.Log(LogLevel.Warning, "PinupSettings", $"Save failed after retries: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Reconciles the persisted <see cref="Playlists"/> against the live set of visible
    /// playlists from the database. Matching is done solely on
    /// <see cref="PinupPlaylist.PlayListID"/>:
    /// <list type="bullet">
    /// <item>Playlists no longer present in <paramref name="live"/> are removed.</item>
    /// <item>New playlists are added (unchecked).</item>
    /// <item>Existing playlists keep their <see cref="PinupPlaylist.Enabled"/> flag but
    /// refresh Name/DisplayOrder/PlayListType/PlayListSQL from the live row.</item>
    /// </list>
    /// The resulting list is ordered by <see cref="PinupPlaylist.DisplayOrder"/>.
    /// </summary>
    public void SyncPlaylists(List<PinupPlaylist> live)
    {
        var enabledById = Playlists.ToDictionary(p => p.PlayListID, p => p.Enabled);
        var merged = new List<PinupPlaylist>();
        foreach (var row in live)
        {
            if (enabledById.TryGetValue(row.PlayListID, out var wasEnabled))
                row.Enabled = wasEnabled;
            merged.Add(row);
        }
        Playlists = merged.OrderBy(p => p.DisplayOrder).ToList();
    }
}
