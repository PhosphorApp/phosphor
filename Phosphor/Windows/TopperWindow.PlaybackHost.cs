using System.Windows;
using System.Windows.Threading;
using Phosphor.Playback;
using LibVLC = LibVLCSharp.Shared.LibVLC;

namespace Phosphor;

/// <summary>
/// Phase 1: the Topper's jukebox player (Player 2) seam. <see cref="TopperWindow"/> implements
/// <see cref="IPlaybackHost"/> so it can host a second, independent <see cref="JukeboxPlayer"/> +
/// <see cref="MediaEngine"/> — mirroring the Backglass (<c>BackglassWindow.PlaybackHost.cs</c>) but
/// mapping the "media on screen vs. back to idle" transitions onto the Topper's AMBIENT takeover /
/// yield (blob overlay + ambient VLC), NOT the Backglass's idle/logo visuals.
///
/// The jukebox video surface is a dedicated <c>_jukeboxVideoView</c> bound to this player's own
/// <see cref="MediaEngine.MediaPlayer"/> — separate from the ambient <c>_ambientVlc</c>/
/// <c>_ambientPlayer</c> pipeline. Both players share one LibVLC but own separate MediaPlayers.
///
/// Pass-1 limitation: now-playing scrubber state (PlaybackPosition/Duration) is still a single set on
/// the VM bound to Player 1, so the Topper's position/info write-back callbacks are intentionally no-ops
/// here to avoid clobbering Player 1's scrubber. Per-player now-playing state arrives in Phase 2.
/// </summary>
public partial class TopperWindow : IPlaybackHost
{
    // The Topper's jukebox player (Player 2). Created lazily against this window as its host.
    private JukeboxPlayer? _jukeboxPlayer;

    /// <summary>The Topper's jukebox playback engine (Player 2), hosted by this window.</summary>
    private JukeboxPlayer JukeboxPlayer => _jukeboxPlayer ??= new JukeboxPlayer(this);

    private MediaEngine JukeboxEngine => JukeboxPlayer.Engine;

    // Dedicated jukebox video surface (separate from the ambient VideoView).
    private LibVLCSharp.WPF.VideoView? _jukeboxVideoView;

    /// <summary>
    /// Accepts the app's shared LibVLC task so the Topper's jukebox engine reuses the single
    /// plugin-scan cost, exactly like the Backglass. Called before the engine initializes.
    /// </summary>
    public void SetSharedVlcTask(Task<LibVLC?>? task) => JukeboxEngine.SetSharedVlcTask(task);

    /// <summary>
    /// Binds the Topper's jukebox player to <see cref="JukeboxViewModel.Player2"/> and kicks off
    /// background LibVLC init (mirrors the Backglass's <c>AttachViewModel</c> + Loaded init).
    /// </summary>
    public void AttachJukeboxViewModel(JukeboxViewModel vm)
    {
        JukeboxPlayer.Attach(vm, vm.Player2);
        JukeboxEngine.InitTask = Task.Run(() => JukeboxEngine.InitializeCore(Dispatcher));
    }

    /// <summary>Sets whether the Topper's jukebox player treats tracks as audio-only.</summary>
    public void SetJukeboxAudioOnly(bool audioOnly) => _jukeboxAudioOnly = audioOnly;
    private bool _jukeboxAudioOnly;

    // ── Jukebox video surface (dedicated; mirrors BackglassWindow.EnsureVideoView / DetachVideoView) ──

    private LibVLCSharp.WPF.VideoView EnsureJukeboxVideoView()
    {
        if (_jukeboxVideoView != null)
            return _jukeboxVideoView;

        _jukeboxVideoView = new LibVLCSharp.WPF.VideoView
        {
            Background = System.Windows.Media.Brushes.Black,
            MediaPlayer = JukeboxEngine.EnsureInitialized(Dispatcher),
            Visibility = Visibility.Hidden,
            Focusable = false,
        };
        System.Windows.Controls.Panel.SetZIndex(_jukeboxVideoView, 2);
        Root.Children.Insert(0, _jukeboxVideoView);
        return _jukeboxVideoView;
    }

    private void DetachJukeboxVideoView()
    {
        if (_jukeboxVideoView != null)
        {
            Root.Children.Remove(_jukeboxVideoView);
            _jukeboxVideoView = null;
        }
    }

    // ── IPlaybackHost: media-mode transitions (mapped onto ambient takeover / yield) ──

    void IPlaybackHost.EnterMediaMode() => EnterJukeboxMediaMode();

    void IPlaybackHost.ReturnToIdle() => ReturnToAmbientMode();

    void IPlaybackHost.DetachVideoView() => DetachJukeboxVideoView();

    void IPlaybackHost.ReportPlaybackFailed(string message)
    {
        if (DataContext is JukeboxViewModel vm)
            vm.StatusText = message;
    }

    // ── IPlaybackHost: transport (gapless/VLC via the engine) ──

    void IPlaybackHost.Pause() => Dispatcher.BeginInvoke(() =>
    {
        if (JukeboxEngine.UsingGaplessPlayer && JukeboxEngine.GaplessPlayer != null)
            JukeboxEngine.GaplessPlayer.Pause();
        else
            JukeboxEngine.EnsureInitialized(Dispatcher).SetPause(true);
    });

    void IPlaybackHost.Resume() => Dispatcher.BeginInvoke(() =>
    {
        if (JukeboxEngine.UsingGaplessPlayer && JukeboxEngine.GaplessPlayer != null)
            JukeboxEngine.GaplessPlayer.Resume();
        else
            JukeboxEngine.EnsureInitialized(Dispatcher).SetPause(false);
    });

    void IPlaybackHost.SetVolume(int volume) => Dispatcher.BeginInvoke(() =>
    {
        if (JukeboxEngine.UsingGaplessPlayer && JukeboxEngine.GaplessPlayer != null)
            JukeboxEngine.GaplessPlayer.SetVolume(volume);
        JukeboxEngine.EnsureInitialized(Dispatcher).Volume = VolumeTaper.VlcVolume(volume);
    });

    void IPlaybackHost.Play(string videoId) => JukeboxPlayer.Play(videoId);
    void IPlaybackHost.Stop() => JukeboxPlayer.Stop();
    void IPlaybackHost.Seek(long timeMs) => JukeboxPlayer.Seek(timeMs);

    // ── IPlaybackHost: host-thread marshalling ──

    bool IPlaybackHost.CheckHostAccess() => Dispatcher.CheckAccess();
    void IPlaybackHost.BeginInvokeOnHost(Action action) => Dispatcher.BeginInvoke(action);
    Task IPlaybackHost.InvokeOnHostAsync(Action action) => Dispatcher.InvokeAsync(action).Task;

    // ── IPlaybackHost: view-transition callbacks ──
    // The Topper has no idle blob color-cycle tied to jukebox playback, and (pass 1) must not write
    // back scrubber/info state to the shared VM. These are therefore intentionally minimal/no-op.

    void IPlaybackHost.StartColorCycle() { }
    void IPlaybackHost.StopColorCycle() { }
    void IPlaybackHost.StartPositionTimer() { }
    void IPlaybackHost.StopPositionTimer() { }
    void IPlaybackHost.StopInfoTimer() { }
    void IPlaybackHost.CancelTransitionOverlay() { }
    void IPlaybackHost.ClearVideoInfo() { }
    void IPlaybackHost.ResetLogoDimIdle() { }
    void IPlaybackHost.StartVideoInfoPollingCached(string resolution) { }
    void IPlaybackHost.StartVideoInfoPolling(string resolution) { }

    // ── IPlaybackHost: video-surface callbacks ──

    void IPlaybackHost.BeginPlayTransition()
    {
        // If a previous jukebox track's video view is still attached, detach it before the new play.
        if (_jukeboxVideoView != null)
            DetachJukeboxVideoView();
    }

    void IPlaybackHost.EnsureVideoSurfaceHidden()
    {
        var view = EnsureJukeboxVideoView();
        view.Visibility = Visibility.Hidden;
    }

    void IPlaybackHost.HideVideoSurface()
    {
        if (_jukeboxVideoView != null)
            _jukeboxVideoView.Visibility = Visibility.Hidden;
    }

    void IPlaybackHost.OnFirstVideoFrame()
    {
        // Jukebox video is ready: take over the window from ambient and reveal the surface.
        EnterJukeboxMediaMode();
        if (_jukeboxVideoView != null)
            _jukeboxVideoView.Visibility = Visibility.Visible;
    }

    void IPlaybackHost.NotifyDmdPlaybackStarted() { }

    Phosphor.Audio.GaplessAudioPlayer IPlaybackHost.CreateGaplessPlayer()
    {
        var player = new Phosphor.Audio.GaplessAudioPlayer(JukeboxEngine.LibVLC!);
        // Pass 1: no queue/track-advance on Player 2 (queue stays on Player 1). Finished → yield to ambient.
        player.PlaybackFinished += () => Dispatcher.BeginInvoke(() =>
        {
            JukeboxEngine.UsingGaplessPlayer = false;
            ReturnToAmbientMode();
        });
        return player;
    }
}
