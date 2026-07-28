namespace Phosphor.Playback;

/// <summary>
/// Owns the playback-only concerns currently woven into <see cref="BackglassWindow"/> (stream
/// resolution, gapless audio, seek/verify, chapters, live-stream clock, cache prefetch, position
/// write-back). It talks to its window ONLY through <see cref="IPlaybackHost"/>, so a second consumer
/// (e.g. the Topper) can drive playback without inheriting Backglass-specific idle/logo visuals.
///
/// Phase 0 status: skeleton only. It holds the host seam; playback fields/logic are migrated into it
/// method-by-method in a later step, with thin forwarders left in <see cref="BackglassWindow"/> so
/// nothing else changes.
/// </summary>
public sealed class JukeboxPlayer
{
    private readonly IPlaybackHost _host;

    public JukeboxPlayer(IPlaybackHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>The window-side surface this player drives (enter media / return to idle / etc.).</summary>
    public IPlaybackHost Host => _host;

    /// <summary>
    /// The media engine this player drives (VLC/gapless lifecycle + state). Owned by the player so a
    /// second player (Topper) gets an independent engine. During the orchestration migration the host
    /// window also references this instance (via its <c>_engine</c>) so relocated and not-yet-relocated
    /// code share one engine.
    /// </summary>
    public MediaEngine Engine { get; } = new();

    /// <summary>
    /// The command channel (play/stop/seek/…) this player reads. Phase 0: the host window still holds
    /// the engine methods and subscribes to the same context; assigning it here establishes the
    /// intended ownership (context → player → host) so a follow-up increment can move the
    /// subscriptions off the window without changing behavior.
    /// </summary>
    public PlayerContext? Context { get; private set; }

    /// <summary>
    /// Binds this player to the command channel it will drive. Subscribes the command handlers this
    /// player now owns (pause / resume / volume) and forwards them to the host. Further handlers
    /// (play / stop / seek) migrate here in later increments.
    /// </summary>
    public void Attach(PlayerContext context)
    {
        if (Context != null)
            Detach();

        Context = context;
        context.AddPlayRequested(OnPlayRequested);
        context.AddStopRequested(OnStopRequested);
        context.AddSeekRequested(OnSeekRequested);
        context.AddPauseRequested(OnPauseRequested);
        context.AddResumeRequested(OnResumeRequested);
        context.AddVolumeChanged(OnVolumeChanged);
    }

    /// <summary>Unsubscribes this player's owned handlers from the current context.</summary>
    public void Detach()
    {
        if (Context == null) return;
        Context.RemovePlayRequested(OnPlayRequested);
        Context.RemoveStopRequested(OnStopRequested);
        Context.RemoveSeekRequested(OnSeekRequested);
        Context.RemovePauseRequested(OnPauseRequested);
        Context.RemoveResumeRequested(OnResumeRequested);
        Context.RemoveVolumeChanged(OnVolumeChanged);
        Context = null;
    }

    private void OnPlayRequested(string videoId) => _host.Play(videoId);
    private void OnStopRequested() => Stop();
    private void OnSeekRequested(long timeMs) => _host.Seek(timeMs);
    private void OnPauseRequested() => _host.Pause();
    private void OnResumeRequested() => _host.Resume();
    private void OnVolumeChanged(int volume) => _host.SetVolume(volume);

    /// <summary>
    /// Stops playback and returns the surface to idle. Relocated from BackglassWindow (pilot slice):
    /// runs on the host thread, drives the shared <see cref="MediaEngine"/>, and uses
    /// <see cref="IPlaybackHost"/> callbacks for the window's idle/timer/video-surface transitions.
    /// </summary>
    public void Stop()
    {
        // Cancel any in-flight play + pending seek-verify so neither resumes after stop.
        Engine.PlayCts?.Cancel();
        Engine.SeekVerifyCts?.Cancel();

        _host.BeginInvokeOnHost(async () =>
        {
            _host.StopPositionTimer();
            _host.StopInfoTimer();
            _host.CancelTransitionOverlay();
            _host.ClearVideoInfo();

            // Stop PCM gapless player if active.
            Engine.StopGaplessPlayer();

            // Detach the VideoView BEFORE stopping so the WinForms HWND is removed from the visual tree
            // first — this prevents VLC's video output thread from waiting on UI-thread window messages
            // while Stop() blocks, which would deadlock.
            _host.DetachVideoView();
            Engine.DisposeGaplessNext();

            // Stop on a background thread to avoid blocking the dispatcher.
            if (Engine.MediaPlayer != null)
                await Task.Run(() => Engine.MediaPlayer.Stop());

            _host.ReturnToIdle();
            _host.StartColorCycle();
            _host.ResetLogoDimIdle();
        });
    }
}

