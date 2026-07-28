using System.Windows;
using System.Windows.Threading;
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

    // Play / Stop / Seek forward to the engine methods that still live in the window (they are the
    // VLC/gapless engine itself, not thin forwarders). JukeboxPlayer owns the SUBSCRIPTION; the engine
    // bodies relocate in a later increment.
    // Play/Stop/Seek are all relocated into JukeboxPlayer; the window no longer implements the bodies.
    void IPlaybackHost.Play(string videoId) => JukeboxPlayer.Play(videoId);

    // Stop is now relocated into JukeboxPlayer (pilot slice); the window no longer implements the body.
    void IPlaybackHost.Stop() => JukeboxPlayer.Stop();

    // Seek is relocated into JukeboxPlayer; the window no longer implements the body.
    void IPlaybackHost.Seek(long timeMs) => JukeboxPlayer.Seek(timeMs);

    bool IPlaybackHost.CheckHostAccess() => Dispatcher.CheckAccess();

    void IPlaybackHost.BeginInvokeOnHost(Action action) => Dispatcher.BeginInvoke(action);

    Task IPlaybackHost.InvokeOnHostAsync(Action action) => Dispatcher.InvokeAsync(action).Task;

    // ── View-transition callbacks (forward to existing window members) ──
    void IPlaybackHost.StartColorCycle() => _colorTimer.Start();
    void IPlaybackHost.StopColorCycle() => _colorTimer.Stop();
    void IPlaybackHost.StartPositionTimer() => _positionTimer?.Start();
    void IPlaybackHost.StopPositionTimer() => _positionTimer?.Stop();
    void IPlaybackHost.StopInfoTimer() => _infoTimer?.Stop();

    void IPlaybackHost.CancelTransitionOverlay()
    {
        _transitionOverlayTimer?.Stop();
        _transitionOverlayTimer = null;
    }

    void IPlaybackHost.ClearVideoInfo() => VideoInfoChanged?.Invoke("");

    void IPlaybackHost.ResetLogoDimIdle() => ResetLogoDimIdle();

    void IPlaybackHost.StartVideoInfoPollingCached(string resolution) => StartVideoInfoPollingCached(resolution);

    // ── Video-surface callbacks (view stays window-owned) ──

    void IPlaybackHost.BeginPlayTransition()
    {
        _colorTimer.Stop();

        // Cancel any pending delayed-overlay reveal from a previous transition.
        _transitionOverlayTimer?.Stop();
        _transitionOverlayTimer = null;

        // During transitions (video view still attached from previous track), detach the old video view
        // BEFORE stopping so VLC's surface clear isn't visible — the black Grid shows through instead of
        // a white flash. Then schedule a delayed idle-overlay reveal: fast (cached/prefetched) transitions
        // Vout within 100-300ms and OnVout cancels this timer for a clean black-to-video swap; only slower
        // buffering transitions (>600ms) reach the tick and reveal the overlay.
        bool isTransition = _videoView != null;
        if (isTransition)
        {
            DetachVideoView();

            _transitionOverlayTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(TransitionOverlayDelayMs)
            };
            _transitionOverlayTimer.Tick += (_, _) =>
            {
                _transitionOverlayTimer?.Stop();
                _transitionOverlayTimer = null;
                if (_videoView == null || _videoView.Visibility != Visibility.Visible)
                {
                    ShowIdleBackground();
                    _colorTimer.Start();
                }
            };
            _transitionOverlayTimer.Start();
        }
    }

    void IPlaybackHost.EnsureVideoSurfaceHidden()
    {
        var videoView = EnsureVideoView();
        videoView.Visibility = Visibility.Hidden;
    }

    void IPlaybackHost.HideVideoSurface()
    {
        if (_videoView != null)
            _videoView.Visibility = Visibility.Hidden;
    }

    void IPlaybackHost.OnFirstVideoFrame()
    {
        // Cancel the pending overlay reveal — video is ready, no need to flash blobs.
        _transitionOverlayTimer?.Stop();
        _transitionOverlayTimer = null;

        if (_videoView != null)
        {
            _videoView.Visibility = Visibility.Visible;
            HookVideoViewForDrag();
        }
        // Hide idle overlay once video is rendering (in case it was briefly shown during a slow transition).
        HideIdleForJukeboxVideo();
        // Stop the blob color cycle now that the overlay is hidden.
        _colorTimer.Stop();
    }

    void IPlaybackHost.StartVideoInfoPolling(string resolution) => StartVideoInfoPolling(resolution);

    void IPlaybackHost.NotifyDmdPlaybackStarted() => PlaybackStarted?.Invoke();

    Phosphor.Audio.GaplessAudioPlayer IPlaybackHost.CreateGaplessPlayer() => CreateGaplessPlayer();
}
