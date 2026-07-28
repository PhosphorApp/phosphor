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
}
