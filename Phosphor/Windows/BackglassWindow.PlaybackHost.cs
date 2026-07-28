using Phosphor.Playback;

namespace Phosphor;

/// <summary>
/// Phase 0 seam: <see cref="BackglassWindow"/> implements <see cref="IPlaybackHost"/> by forwarding
/// to its existing idle/video-surface methods. This establishes the boundary a playback engine will
/// talk through without moving any logic or changing behavior.
/// </summary>
public partial class BackglassWindow : IPlaybackHost
{
    // The playback engine driven by this window. Phase 0: skeleton only (holds the host seam);
    // playback logic migrates into it in a later step. Created lazily so the seam is exercised
    // without changing behavior.
    private JukeboxPlayer? _jukeboxPlayer;

    /// <summary>The playback engine for this window, created against this window as its host.</summary>
    private JukeboxPlayer JukeboxPlayer => _jukeboxPlayer ??= new JukeboxPlayer(this);

    void IPlaybackHost.EnterMediaMode() => HideIdleForJukeboxVideo();

    void IPlaybackHost.ReturnToIdle() => ShowIdleBackground();

    void IPlaybackHost.DetachVideoView() => DetachVideoView();

    void IPlaybackHost.ReportPlaybackFailed(string message)
    {
        if (DataContext is JukeboxViewModel vm)
            vm.StatusText = message;
    }

    void IPlaybackHost.Pause() => Dispatcher.BeginInvoke(() =>
    {
        if (_usingGaplessPlayer && _gaplessPlayer != null)
            _gaplessPlayer.Pause();
        else
            EnsureVlcInitialized().SetPause(true);
    });

    void IPlaybackHost.Resume() => Dispatcher.BeginInvoke(() =>
    {
        if (_usingGaplessPlayer && _gaplessPlayer != null)
            _gaplessPlayer.Resume();
        else
            EnsureVlcInitialized().SetPause(false);
    });

    void IPlaybackHost.SetVolume(int volume) => Dispatcher.BeginInvoke(() =>
    {
        if (_usingGaplessPlayer && _gaplessPlayer != null)
            _gaplessPlayer.SetVolume(volume);
        EnsureVlcInitialized().Volume = VolumeTaper.VlcVolume(volume);
        DebugLog.Log(LogLevel.Trace, "Volume", $"Volume set to {volume}");
    });
}
