using System.IO;

namespace Phosphor;

/// <summary>
/// Shared helper that loads the Pinup Popper integration data (playlists + resolved games)
/// on a background thread and pushes the resulting playfield video file list to the
/// <see cref="PlayfieldProxy"/>. Used both at app startup and on live settings-apply so the
/// heavy DB work never runs on the UI thread.
/// </summary>
public static class PinupPlaylistLoader
{
    /// <summary>
    /// On a low-priority background task: loads <see cref="PinupSettings"/>, syncs playlists
    /// against the live database, rebuilds the resolved game list, persists it, and applies the
    /// resolved playfield video files + durations to <paramref name="playfield"/>. Does nothing
    /// unless the Pinup Playlist feature is active (playfield shown, mode == PinupPlaylist).
    /// </summary>
    public static void LoadAndApplyAsync(AppSettings settings, PlayfieldProxy? playfield)
    {
        if (playfield == null)
            return;
        if (!settings.ShowPlayfield || settings.PlayfieldDisplayMode != PlayfieldMode.PinupPlaylist)
            return;

        // Always push the current duration options (cheap, no DB needed).
        playfield.SetPinupOptions(
            settings.PlayfieldPinupMinDurationSeconds,
            settings.PlayfieldPinupMaxDurationSeconds);

        System.Threading.Tasks.Task.Factory.StartNew(() =>
        {
            try
            {
                var pinup = PinupSettings.Load();
                if (string.IsNullOrWhiteSpace(pinup.PopperDbPath) ||
                    !File.Exists(pinup.PopperDbPath) ||
                    pinup.Playlists.Count == 0)
                {
                    DebugLog.Log("Pinup", "Load skipped: no DB path or playlists configured.");
                    return;
                }

                var live = PinupDatabase.GetVisiblePlaylists(pinup.PopperDbPath);
                pinup.SyncPlaylists(live);
                pinup.Games = PinupDatabase.BuildGameList(
                    pinup.PopperDbPath, pinup.Playlists.Where(p => p.Enabled));
                pinup.Save();

                DebugLog.Log("Pinup",
                    $"Load complete: {pinup.Playlists.Count} playlists, {pinup.Games.Count} games.");

                var globs = pinup.Games
                    .Select(g => g.PlayfieldVideoFilename)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
                playfield.SetPinupFiles(globs);
            }
            catch (Exception ex)
            {
                DebugLog.Log("Pinup", $"Load failed: {ex.Message}");
            }
        }, System.Threading.CancellationToken.None,
           System.Threading.Tasks.TaskCreationOptions.LongRunning,
           System.Threading.Tasks.TaskScheduler.Default);
    }
}
