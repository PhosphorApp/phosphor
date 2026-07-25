using System.IO;
using Microsoft.Data.Sqlite;

namespace Phosphor;

/// <summary>
/// Read-only access to the Pinup Popper (POPPER) SQLite database. Opens the file in
/// read-only mode with shared access so it works even while Pinup Popper is running.
/// </summary>
public static class PinupDatabase
{
    /// <summary>
    /// Maximum seconds a command will retry against a locked/busy database before failing.
    /// Kept short so a locked Pinup database (rare) degrades quickly to "no playback" rather
    /// than hanging the load task.
    /// </summary>
    private const int CommandTimeoutSeconds = 15;

    /// <summary>
    /// Loads all visible playlists from the POPPER database, ordered by DisplayOrder.
    /// Returns playlist rows with <see cref="PinupPlaylist.Enabled"/> defaulted to false
    /// (callers merge in previously-saved enabled state by PlayListID).
    /// </summary>
    /// <exception cref="FileNotFoundException">Thrown when the database file does not exist.</exception>
    public static List<PinupPlaylist> GetVisiblePlaylists(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            throw new FileNotFoundException("Pinup Popper database not found.", dbPath);

        var result = new List<PinupPlaylist>();

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = CommandTimeoutSeconds,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandTimeout = CommandTimeoutSeconds;
        cmd.CommandText = "select * from playlists where visible = 1 order by displayorder asc";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new PinupPlaylist
            {
                PlayListID = GetInt(reader, "PlayListID"),
                Name = GetString(reader, "PlayDisplay"),
                DisplayOrder = GetInt(reader, "DisplayOrder"),
                PlayListType = GetInt(reader, "PlayListType"),
                PlayListSQL = GetString(reader, "PlayListSQL"),
                Enabled = false,
            });
        }

        return result;
    }

    /// <summary>
    /// Builds a de-duped (by GameID) list of games across all enabled playlists. For each
    /// playlist, an inner "game ids" query is chosen by <see cref="PinupPlaylist.PlayListType"/>
    /// (type 1 uses <see cref="PinupPlaylist.PlayListSQL"/>; type 0 uses a playlistdetails query)
    /// and wrapped in an outer games/emulators join. Semicolons are stripped from the inner
    /// query (they would terminate the wrapped statement) while line breaks are preserved so
    /// trailing <c>--</c> comments do not swallow following SQL.
    /// </summary>
    /// <exception cref="FileNotFoundException">Thrown when the database file does not exist.</exception>
    public static List<PinupGame> BuildGameList(string dbPath, IEnumerable<PinupPlaylist> enabledPlaylists)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            throw new FileNotFoundException("Pinup Popper database not found.", dbPath);

        var byGameId = new Dictionary<int, PinupGame>();

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = CommandTimeoutSeconds,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        foreach (var playlist in enabledPlaylists)
        {
            string innerQuery = playlist.PlayListType == 1
                ? playlist.PlayListSQL ?? ""
                : $"select GameId from playlistdetails where visible=1 and playlistID={playlist.PlayListID}";

            // Strip semicolons (would terminate the wrapped statement); keep line breaks intact
            // because some lines end in "--" comments that must not merge with the next line.
            innerQuery = innerQuery.Replace(";", "");

            if (string.IsNullOrWhiteSpace(innerQuery))
                continue;

            string wrapped =
                "select coalesce(e.DirMedia,(select GlobalMediaDir from GlobalSettings limit 1)) as DirMedia, \n" +
                "coalesce(e.DirMedia || \"\\Playfield\\\" || SUBSTR(g.GameFileName, 1, LENGTH(g.GameFileName) - 4) || '.*', (select GlobalMediaDir from GlobalSettings limit 1) || \"\\Playfield\\\" || SUBSTR(g.GameFileName, 1, LENGTH(g.GameFileName) - 4) || '.*') AS PlayfieldVideoFilename,\n" +
                "g.emuid, g.gamefilename, g.gameid, g.gamedisplay from games g\n" +
                "join Emulators e on e.EMUID = g.EMUID \n" +
                "where gameid in (\n" +
                "\tselect gameid from (\n" +
                innerQuery + "\n" +
                "))";

            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandTimeout = CommandTimeoutSeconds;
                cmd.CommandText = wrapped;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    int gameId = GetInt(reader, "gameid");
                    if (byGameId.ContainsKey(gameId))
                        continue;

                    byGameId[gameId] = new PinupGame
                    {
                        DirMedia = GetString(reader, "DirMedia"),
                        PlayfieldVideoFilename = GetString(reader, "PlayfieldVideoFilename"),
                        EmuId = GetInt(reader, "emuid"),
                        GameFileName = GetString(reader, "gamefilename"),
                        GameId = gameId,
                        GameDisplay = GetString(reader, "gamedisplay"),
                    };
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log(LogLevel.Warning, "Pinup", $"Playlist {playlist.PlayListID} query failed: {ex.Message}");
            }
        }

        return byGameId.Values.ToList();
    }

    private static int GetInt(SqliteDataReader reader, string column)
    {
        try
        {
            int ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));
        }
        catch { return 0; }
    }

    private static string GetString(SqliteDataReader reader, string column)
    {
        try
        {
            int ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? "" : reader.GetValue(ordinal)?.ToString() ?? "";
        }
        catch { return ""; }
    }
}
