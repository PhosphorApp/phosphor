using System.IO;

namespace Phosphor;

/// <summary>
/// Shared helper that loads the Pinup Popper integration data (playlists + resolved games)
/// on a background thread and returns the canonical playfield video globs. The
/// <see cref="PinupSyncCoordinator"/> consumes this list to drive all screens in sync; each
/// follower re-points the playfield glob to its own screen folder. Kept off the UI thread
/// because the DB work can be heavy on large Pinup installations.
/// </summary>
public static class PinupPlaylistLoader
{
    /// <summary>
    /// On a low-priority background task: loads <see cref="PinupSettings"/>, syncs playlists
    /// against the live database, rebuilds the resolved game list, persists it, and invokes
    /// <paramref name="onLoaded"/> with the canonical playfield globs (…\Playfield\&lt;base&gt;.*).
    /// The callback runs on the background thread — marshal to a dispatcher if needed. Invokes
    /// with an empty list when no DB/playlists are configured.
    /// </summary>
    public static void LoadGamesAsync(Action<IReadOnlyList<string>> onLoaded)
    {
        System.Threading.Tasks.Task.Factory.StartNew(() =>
        {
            try
            {
                var pinup = PinupSettings.Load();
                if (string.IsNullOrWhiteSpace(pinup.PopperDbPath) ||
                    !File.Exists(pinup.PopperDbPath) ||
                    pinup.Playlists.Count == 0)
                {
                    DebugLog.Log(LogLevel.Debug, "Pinup", "Load skipped: no DB path or playlists configured.");
                    onLoaded(Array.Empty<string>());
                    return;
                }

                var live = PinupDatabase.GetVisiblePlaylists(pinup.PopperDbPath);
                pinup.SyncPlaylists(live);
                pinup.Games = PinupDatabase.BuildGameList(
                    pinup.PopperDbPath, pinup.Playlists.Where(p => p.Enabled));
                pinup.Save();

                DebugLog.Log(LogLevel.Info, "Pinup",
                    $"Load complete: {pinup.Playlists.Count} playlists, {pinup.Games.Count} games.");

                var globs = pinup.Games
                    .Select(g => g.PlayfieldVideoFilename)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
                onLoaded(globs);
            }
            catch (Exception ex)
            {
                DebugLog.Log(LogLevel.Warning, "Pinup", $"Load failed: {ex.Message}");
                onLoaded(Array.Empty<string>());
            }
        }, System.Threading.CancellationToken.None,
           System.Threading.Tasks.TaskCreationOptions.LongRunning,
           System.Threading.Tasks.TaskScheduler.Default);
    }
}
