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
        context.AddPauseRequested(OnPauseRequested);
        context.AddResumeRequested(OnResumeRequested);
        context.AddVolumeChanged(OnVolumeChanged);
    }

    /// <summary>Unsubscribes this player's owned handlers from the current context.</summary>
    public void Detach()
    {
        if (Context == null) return;
        Context.RemovePauseRequested(OnPauseRequested);
        Context.RemoveResumeRequested(OnResumeRequested);
        Context.RemoveVolumeChanged(OnVolumeChanged);
        Context = null;
    }

    private void OnPauseRequested() => _host.Pause();
    private void OnResumeRequested() => _host.Resume();
    private void OnVolumeChanged(int volume) => _host.SetVolume(volume);
}

