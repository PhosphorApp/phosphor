namespace Phosphor.Playback;

using LibVLCSharp.Shared;
using Phosphor;
using LogLevel = Phosphor.LogLevel;

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
    /// The view-model this player uses as its model (now-playing state + shared services like the
    /// video cache). Option X: the controller holds the model directly. Phase 2 later moves per-player
    /// now-playing state into <see cref="PlayerContext"/>, at which point these reads migrate there.
    /// </summary>
    public JukeboxViewModel? Model { get; private set; }

    /// <summary>
    /// Binds this player to a view-model: stores it as the model and subscribes the command handlers
    /// this player owns (play / stop / seek / pause / resume / volume) on the VM's player context.
    /// </summary>
    public void Attach(JukeboxViewModel vm)
    {
        if (Context != null)
            Detach();

        Model = vm;
        Context = vm.Player1;
        Context.AddPlayRequested(OnPlayRequested);
        Context.AddStopRequested(OnStopRequested);
        Context.AddSeekRequested(OnSeekRequested);
        Context.AddPauseRequested(OnPauseRequested);
        Context.AddResumeRequested(OnResumeRequested);
        Context.AddVolumeChanged(OnVolumeChanged);
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
        Model = null;
    }

    private void OnPlayRequested(string videoId) => _host.Play(videoId);
    private void OnStopRequested() => Stop();
    private void OnSeekRequested(long timeMs) => Seek(timeMs);
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

    /// <summary>
    /// Seeks the current media to <paramref name="timeMs"/>. Relocated from BackglassWindow: handles the
    /// PCM-gapless path, cached-file switch, in-place seek + verification (wedge detection), and
    /// restart-on-failure. Drives the shared <see cref="MediaEngine"/> and reads/writes the model
    /// (<see cref="Model"/>) directly; view transitions go through <see cref="IPlaybackHost"/>.
    /// </summary>
    public void Seek(long timeMs)
    {
        _host.BeginInvokeOnHost(() =>
        {
            var mediaPlayer = Engine.MediaPlayer;

            // PCM gapless mode: seek via the gapless player
            if (Engine.UsingGaplessPlayer && Engine.GaplessPlayer != null)
            {
                Engine.GaplessPlayer.Seek(timeMs);
                DebugLog.Log(LogLevel.Trace, "Seek", $"PCM gapless seek to {timeMs}ms");
                return;
            }

            if (mediaPlayer == null) return;
            var length = mediaPlayer.Length;
            var seekable = mediaPlayer.IsSeekable;
            DebugLog.Log(LogLevel.Trace, "Seek", $"Requested: {timeMs}ms | State={mediaPlayer.State} Length={length} Time={mediaPlayer.Time} Seekable={seekable}");

            if (length <= 0)
            {
                DebugLog.Log(LogLevel.Trace, "Seek", "Skipped: Length <= 0");
                return;
            }

            var timeBefore = mediaPlayer.Time;
            var targetMs = Math.Clamp(timeMs, 0, length);

            // Cancel any pending verification from a previous seek; otherwise an older
            // task could fire after a newer scrub and "restore" the wrong position.
            Engine.SeekVerifyCts?.Cancel();
            var verifyCts = Engine.SeekVerifyCts = new CancellationTokenSource();
            var verifyCt = verifyCts.Token;

            // If the source is a local file (cached / prefetched), seeks always work —
            // skip the in-place attempt and the verification dance.
            bool isLocalSource = !string.IsNullOrEmpty(Engine.LastLocalFilePath);

            // The current track may have started as a live stream while its cached
            // (downloaded + remuxed) copy finished in the background. Live streams scrub
            // unreliably, so if a ready cache now exists for this video, switch to it
            // seamlessly and resume at the scrub target. Skip in audio-only mode.
            if (!isLocalSource)
            {
                var vmForCache = Model;
                bool isAudioOnly = vmForCache?.CurrentlyPlaying?.IsAudioOnly == true;
                var cached = !isAudioOnly && !string.IsNullOrEmpty(Engine.LastPlayingVideoId)
                    ? vmForCache?.Cache?.TryGet(Engine.LastPlayingVideoId!)
                    : null;
                if (cached != null)
                {
                    DebugLog.Log(LogLevel.Debug, "Seek", $"Cache ready — switching from live stream to cached file for reliable scrub: {cached.FilePath}");
                    SwitchToCachedFileAndSeek(cached, targetMs);
                    return;
                }
            }

            if (isLocalSource || seekable)
            {
                mediaPlayer.Time = targetMs;
            }

            // For local files we're done.
            if (isLocalSource) return;

            // For HTTP streams reported as not-seekable, there is no in-place recovery
            // path that works reliably. Restart from the beginning so the player ends
            // in a known good state.
            if (!seekable)
            {
                DebugLog.Log(LogLevel.Warning, "Seek", "IsSeekable=false — restarting playback from the beginning (use transient caching for reliable scrubbing)");

                var vm = Model;
                var videoIdToRestart = Engine.LastPlayingVideoId;
                if (vm != null && !string.IsNullOrEmpty(videoIdToRestart))
                {
                    vm.SetStatusPrefix("Seek failed — restarted");
                    vm.PlaybackPosition = 0;
                    _host.Play(videoIdToRestart);
                }
                return;
            }

            // VLC echoes back whatever we set to Time — it does NOT reflect where the
            // stream actually landed. Detection uses two complementary signals (buffering
            // activity + time progress); if neither appears, the seek wedged — restart.
            const int fastCheckDelayMs = 800;
            const int finalCheckDelayMs = 700;
            const long MinHealthyProgressMs = 100;

            // Subscribe to Buffering events so we can detect activity without polling.
            int bufferingTickCount = 0;
            void OnBufferingTick(object? s, MediaPlayerBufferingEventArgs e)
            {
                System.Threading.Interlocked.Increment(ref bufferingTickCount);
            }
            mediaPlayer.Buffering += OnBufferingTick;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(fastCheckDelayMs, verifyCt);

                    long sample1 = 0;
                    await _host.InvokeOnHostAsync(() => { if (Engine.MediaPlayer != null) sample1 = Engine.MediaPlayer.Time; });

                    int bufferTicksFast = System.Threading.Interlocked.CompareExchange(ref bufferingTickCount, 0, 0);
                    bool sawBuffering = bufferTicksFast > 0;
                    bool nearTargetFast = Math.Abs(sample1 - targetMs) <= Math.Max(5000L, (long)(length * 0.02));

                    DebugLog.Log(LogLevel.Trace, "Seek", $"Fast check ({fastCheckDelayMs}ms): sample1={sample1} bufferTicks={bufferTicksFast} sawBuffering={sawBuffering} nearTargetFast={nearTargetFast}");

                    await Task.Delay(finalCheckDelayMs, verifyCt);
                    long sample2 = 0;
                    await _host.InvokeOnHostAsync(() => { if (Engine.MediaPlayer != null) sample2 = Engine.MediaPlayer.Time; });

                    if (verifyCt.IsCancellationRequested) return;

                    await _host.InvokeOnHostAsync(() =>
                    {
                        if (Engine.MediaPlayer == null) return;

                        var nearTargetTolerance = Math.Max(5000L, (long)(length * 0.02));
                        long progress = sample2 - sample1;
                        bool advancing = progress >= MinHealthyProgressMs;
                        bool nearTarget = Math.Abs(sample2 - targetMs) <= nearTargetTolerance;

                        bool seekHealthy = nearTarget && (sawBuffering || advancing);

                        DebugLog.Log(LogLevel.Trace, "Seek", $"Verify: sample2={sample2} progress={progress}ms (was {timeBefore}, target {targetMs}) nearTarget={nearTarget} advancing={advancing} sawBuffering={sawBuffering} healthy={seekHealthy}");

                        if (seekHealthy) return;

                        DebugLog.Log(LogLevel.Warning, "Seek", "Seek failed — restarting playback from the beginning (use transient caching for reliable scrubbing)");

                        var vm = Model;
                        var videoIdToRestart = Engine.LastPlayingVideoId;
                        if (vm == null || string.IsNullOrEmpty(videoIdToRestart)) return;

                        vm.SetStatusPrefix("Seek failed — restarted");
                        vm.PlaybackPosition = 0;
                        _host.Play(videoIdToRestart);
                    });
                }
                catch (OperationCanceledException) { /* superseded by a newer seek */ }
                catch (Exception ex)
                {
                    DebugLog.LogException("Seek/Verify", ex);
                }
                finally
                {
                    // Always detach our buffering probe. Marshal to the host since events live on it.
                    try
                    {
                        await _host.InvokeOnHostAsync(() =>
                        {
                            if (Engine.MediaPlayer != null)
                                Engine.MediaPlayer.Buffering -= OnBufferingTick;
                        });
                    }
                    catch { /* shutting down */ }
                }
            }, verifyCt);
        });
    }

    /// <summary>
    /// Seamlessly swaps the currently-playing live stream for its now-ready cached (downloaded +
    /// remuxed) local file, resuming at <paramref name="targetMs"/> via ":start-time". Runs on the
    /// host thread (called from within <see cref="Seek"/>).
    /// </summary>
    private void SwitchToCachedFileAndSeek(CachedVideo cached, long targetMs)
    {
        var mediaPlayer = Engine.MediaPlayer;
        var libVLC = Engine.LibVLC;
        if (mediaPlayer == null || libVLC == null) return;

        // A cache switch supersedes any in-flight seek verification from the live stream.
        Engine.SeekVerifyCts?.Cancel();

        var vm = Model;

        try
        {
            var media = new Media(libVLC, new Uri(cached.FilePath));

            var startSeconds = Math.Max(0, targetMs) / 1000.0;
            media.AddOption($":start-time={startSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");

            // Preserve a paused scrub: re-apply pause once VLC reaches the playing state.
            bool wasPaused = vm?.IsPaused == true;
            if (wasPaused)
            {
                void OnPlayingReapplyPause(object? s, EventArgs a)
                {
                    mediaPlayer.Playing -= OnPlayingReapplyPause;
                    mediaPlayer.SetPause(true);
                }
                mediaPlayer.Playing += OnPlayingReapplyPause;
            }

            mediaPlayer.Play(media);

            // From now on this track is a local file — future seeks skip the verification path.
            Engine.LastLocalFilePath = cached.FilePath;
            Engine.LastVideoStreamUrl = null;
            Engine.LastAudioStreamUrl = null;
            Engine.LastMuxedStreamUrl = null;

            vm?.SetCurrentFromCache(true);

            if (vm != null)
                vm.PlaybackPosition = targetMs;

            if (cached.Chapters is { Count: > 0 } && vm?.CurrentlyPlaying is { } cp && cp.Chapters == null)
            {
                cp.Chapters = cached.Chapters;
                vm.NotifyCachedChaptersRestored();
            }

            _host.StartVideoInfoPollingCached(cached.Resolution);
        }
        catch (Exception ex)
        {
            DebugLog.LogException("Seek/CacheSwitch", ex);
        }
    }
}

