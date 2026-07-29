namespace Phosphor.Playback;

/// <summary>
/// The window-side surface a playback engine needs from its host window, decoupled from any
/// Backglass-specific idle/logo/blob visuals. Lets a playback engine drive the "media on screen vs.
/// back to idle" transitions and report failures without knowing which window it lives in — the
/// prerequisite for a second, independent player (e.g. on the Topper) that must NOT inherit the
/// Backglass's idle-overlay behavior.
///
/// Phase 0 note: <see cref="BackglassWindow"/> implements this by forwarding to its existing
/// <c>HideIdleForJukeboxVideo</c> / <c>ShowIdleBackground</c> / <c>DetachVideoView</c> methods, so
/// there is zero behavior change — this only establishes the boundary.
/// </summary>
public interface IPlaybackHost
{
    /// <summary>
    /// A jukebox video track is now on screen: hide the host's idle background (blob/logo/ambient)
    /// so the video surface is the paramount layer. Corresponds to <c>HideIdleForJukeboxVideo</c>.
    /// </summary>
    void EnterMediaMode();

    /// <summary>
    /// Playback stopped or an audio-only track started with no video on screen: restore the host's
    /// idle background (blob/logo overlay or ambient content). Corresponds to <c>ShowIdleBackground</c>.
    /// </summary>
    void ReturnToIdle();

    /// <summary>
    /// Removes the video view from the host's visual tree so it can use GPU-accelerated rendering
    /// for the idle overlay. Corresponds to <c>DetachVideoView</c>.
    /// </summary>
    void DetachVideoView();

    /// <summary>
    /// Reports a playback failure to the host so it can surface status/recover as appropriate.
    /// </summary>
    void ReportPlaybackFailed(string message);

    /// <summary>Pauses the currently-playing media (gapless or VLC). Marshals to the host thread.</summary>
    void Pause();

    /// <summary>Resumes paused media (gapless or VLC). Marshals to the host thread.</summary>
    void Resume();

    /// <summary>Sets playback volume (0–100). Applies to gapless and VLC. Marshals to the host thread.</summary>
    void SetVolume(int volume);

    /// <summary>Starts playback of the item with the given id (the engine resolves/streams it).</summary>
    void Play(string videoId);

    /// <summary>Stops playback and returns the surface to idle.</summary>
    void Stop();

    /// <summary>Seeks the current media to the given position (ms).</summary>
    void Seek(long timeMs);

    // ── Host-thread marshalling ──
    // The playback orchestration runs on the host window's STA/dispatcher thread. JukeboxPlayer is
    // thread-agnostic, so it marshals onto the host thread through these members instead of holding a
    // Dispatcher directly.

    /// <summary>True when the caller is already on the host's dispatcher thread.</summary>
    bool CheckHostAccess();

    /// <summary>Queues <paramref name="action"/> to run asynchronously on the host's dispatcher thread.</summary>
    void BeginInvokeOnHost(Action action);

    /// <summary>Runs <paramref name="action"/> on the host's dispatcher thread and awaits completion.</summary>
    Task InvokeOnHostAsync(Action action);

    // ── View-transition callbacks used by the playback orchestration ──
    // The orchestration (stop / seek / play) drives these window-owned visuals/timers. They are grouped
    // here so JukeboxPlayer can run the flow without knowing which window it lives in. All are invoked
    // on the host thread (the orchestration marshals via BeginInvokeOnHost).

    /// <summary>Starts the idle blob color-cycle timer (idle animation resumes).</summary>
    void StartColorCycle();

    /// <summary>Stops the idle blob color-cycle timer (no point cycling under a video).</summary>
    void StopColorCycle();

    /// <summary>Starts the playback position/duration write-back timer.</summary>
    void StartPositionTimer();

    /// <summary>Stops the playback position/duration write-back timer.</summary>
    void StopPositionTimer();

    /// <summary>Stops the video-info polling timer.</summary>
    void StopInfoTimer();

    /// <summary>Cancels/clears any pending delayed idle-overlay reveal from a transition.</summary>
    void CancelTransitionOverlay();

    /// <summary>Clears the video-info readout (e.g. resolution text) on stop.</summary>
    void ClearVideoInfo();

    /// <summary>Resets the logo-dim state back to its idle appearance.</summary>
    void ResetLogoDimIdle();

    /// <summary>Starts the video-info polling for a cached (local-file) source at the given resolution.</summary>
    void StartVideoInfoPollingCached(string resolution);

    // ── Video-surface callbacks (the video view stays window-owned under Option A) ──

    /// <summary>
    /// Prepares the surface for a new play at the top of the flow: stops the blob color-cycle, cancels
    /// any pending transition-overlay reveal, and — if a previous track's video view is still attached —
    /// detaches it and schedules the delayed idle-overlay reveal for a slow transition.
    /// </summary>
    void BeginPlayTransition();

    /// <summary>Creates the video surface (if needed) and leaves it hidden until the first frame.</summary>
    void EnsureVideoSurfaceHidden();

    /// <summary>Hides the video surface without detaching it (audio-only / gapless: no video on screen).</summary>
    void HideVideoSurface();

    /// <summary>
    /// Runs the window's first-video-frame view work: cancels the pending overlay reveal, reveals the
    /// video surface + drag hooks, hides the idle overlay, and stops the blob color-cycle.
    /// </summary>
    void OnFirstVideoFrame();

    /// <summary>Starts the video-info polling for a streaming (non-cached) source at the given resolution.</summary>
    void StartVideoInfoPolling(string resolution);

    /// <summary>Notifies listeners (e.g. the DMD window) that playback has started, so it can reclaim focus.</summary>
    void NotifyDmdPlaybackStarted();

    /// <summary>
    /// Creates the PCM gapless audio player, wiring its track-advance / finished callbacks to the
    /// host's view + view-model (window-owned because those callbacks touch idle visuals and the VM).
    /// </summary>
    Phosphor.Audio.GaplessAudioPlayer CreateGaplessPlayer();
}
