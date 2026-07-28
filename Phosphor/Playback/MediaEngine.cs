using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace Phosphor.Playback;

/// <summary>
/// Owns the raw LibVLC engine for one player: the shared <see cref="LibVLC"/> instance, this player's
/// dedicated <see cref="MediaPlayer"/>, the background-init handshake, the last-resolved stream context
/// (used to re-open on a failed seek), and the live-stream elapsed clock.
///
/// Phase 0.6 slice 1: this is a cohesion extraction — <see cref="BackglassWindow"/> still creates and
/// drives it, and the higher-level play/seek orchestration stays in the window for now (reaching VLC
/// via <see cref="MediaPlayer"/>). Later slices peel that orchestration across the seam so a second
/// player (Topper) can own an independent engine. The window subscribes to <see cref="EndReached"/> /
/// <see cref="Buffering"/> so its view/VM handlers stay where they are.
/// </summary>
public sealed class MediaEngine
{
    private LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;
    private Task<LibVLC?>? _sharedVlcTask;

    /// <summary>The shared LibVLC instance (may be app-provided). Null until initialized.</summary>
    public LibVLC? LibVLC => _libVLC;

    /// <summary>This player's dedicated MediaPlayer. Null until initialized.</summary>
    public MediaPlayer? MediaPlayer => _mediaPlayer;

    /// <summary>The background LibVLC init task (started by the window in Loaded), or null.</summary>
    public Task? InitTask { get; set; }

    // ── Last stream context (populated on play; used to re-open at :start-time on a failed seek) ──
    public string? LastPlayingVideoId { get; set; }
    public string? LastVideoStreamUrl { get; set; }
    public string? LastAudioStreamUrl { get; set; }
    public string? LastMuxedStreamUrl { get; set; }
    public string? LastLocalFilePath { get; set; }

    /// <summary>Wall-clock start of the current live stream (for elapsed-since-start), or null.</summary>
    public DateTime? LiveStartUtc { get; set; }

    /// <summary>Raised on the VLC <see cref="MediaPlayer.EndReached"/> event (wired on the dispatcher).</summary>
    public event EventHandler? EndReached;

    /// <summary>Raised on the VLC <see cref="MediaPlayer.Buffering"/> event.</summary>
    public event EventHandler<MediaPlayerBufferingEventArgs>? Buffering;

    /// <summary>
    /// Accepts a shared LibVLC instance from the application so all consumers reuse a single
    /// plugin-scan cost. Must be called before <see cref="InitializeCore"/>.
    /// </summary>
    public void SetSharedVlc(LibVLC? vlc)
    {
        if (vlc != null)
            _libVLC = vlc;
    }

    /// <summary>Accepts a task that will produce the shared LibVLC instance (awaited by the window's init).</summary>
    public void SetSharedVlcTask(Task<LibVLC?>? task) => _sharedVlcTask = task;

    /// <summary>
    /// Core LibVLC + MediaPlayer creation. Thread-safe; called once from either the background init
    /// task or synchronously as a fallback. Reuses a shared LibVLC if one was provided. Wires
    /// EndReached on the supplied <paramref name="dispatcher"/> so its handler can touch UI.
    /// </summary>
    public void InitializeCore(Dispatcher dispatcher)
    {
        // If the app provided a shared-VLC task, adopt its result instead of spinning up a second
        // LibVLC (the window awaits this off-thread before calling us).
        if (_sharedVlcTask != null && _libVLC == null)
        {
            try
            {
                var shared = _sharedVlcTask.GetAwaiter().GetResult();
                if (shared != null)
                    _libVLC = shared;
            }
            catch { /* fall through to fresh instance */ }
        }

        var vlc = _libVLC ?? new LibVLC("--no-video-title-show", "--network-caching=3000", "--http-reconnect");
        var mp = new MediaPlayer(vlc);
        // Stop VLC from grabbing mouse/keyboard on its video HWND so events pass through to the
        // hosting WinForms panel (enables the window's drag/resize hooks).
        mp.EnableMouseInput = false;
        mp.EnableKeyInput = false;
        // Wire EndReached on the window's dispatcher so the handler can touch UI.
        dispatcher.Invoke(() => mp.EndReached += (s, e) => EndReached?.Invoke(s, e));
        // Clear chapter-seek spinner once VLC finishes buffering.
        mp.Buffering += (s, e) => Buffering?.Invoke(s, e);
        _libVLC = vlc;
        _mediaPlayer = mp;
    }

    /// <summary>
    /// Returns the MediaPlayer, waiting for background initialization if needed. Called from the
    /// window's dispatcher thread; pumps messages while waiting so the UI stays responsive.
    /// </summary>
    public MediaPlayer EnsureInitialized(Dispatcher dispatcher)
    {
        if (_mediaPlayer != null)
            return _mediaPlayer;

        // Background init may still be running — wait for it, pumping dispatcher messages.
        if (InitTask != null && !InitTask.IsCompleted)
        {
            var frame = new DispatcherFrame();
            InitTask.ContinueWith(_ => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }

        // If background init didn't run (shouldn't happen), init synchronously.
        if (_mediaPlayer == null)
            InitializeCore(dispatcher);

        return _mediaPlayer!;
    }

    /// <summary>
    /// Swaps in a pre-primed <see cref="MediaPlayer"/> (gapless transition), rewiring the engine's
    /// EndReached forwarding to the new player. Returns the old player so the caller can dispose it.
    /// </summary>
    public MediaPlayer? SwapMediaPlayer(MediaPlayer next)
    {
        var old = _mediaPlayer;
        next.EndReached += (s, e) => EndReached?.Invoke(s, e);
        _mediaPlayer = next;
        return old;
    }
}
