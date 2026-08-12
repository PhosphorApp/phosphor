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
    public void Attach(JukeboxViewModel vm) => Attach(vm, vm.Player1);

    /// <summary>
    /// Binds this player to a view-model and a specific command channel. The Backglass binds to
    /// <see cref="JukeboxViewModel.Player1"/>; the Topper (Player 2) binds to
    /// <see cref="JukeboxViewModel.Player2"/> so both drive independent engines from one VM.
    /// </summary>
    public void Attach(JukeboxViewModel vm, PlayerContext context)
    {
        if (Context != null)
            Detach();

        Model = vm;
        Context = context;
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

    private void OnPlayRequested(string videoId) => Play(videoId);
    private void OnStopRequested() => Stop();
    /// <summary>
    /// Starts playback of <paramref name="videoId"/>. Relocated from BackglassWindow: runs on the host
    /// thread, drives the shared <see cref="MediaEngine"/>, reads/writes the VM via <see cref="Model"/>
    /// (Option X), and performs all view/video-surface transitions through <see cref="IPlaybackHost"/>.
    /// </summary>
    public async void Play(string videoId)
    {
        // Command events fire on the main UI thread — marshal to the host thread.
        if (!_host.CheckHostAccess())
        {
            _host.BeginInvokeOnHost(() => Play(videoId));
            return;
        }

        // Cancel any in-flight play operation.
        Engine.PlayCts?.Cancel();
        Engine.StopGaplessPlayer();
        Engine.DisposeGaplessNext();
        var cts = Engine.PlayCts = new CancellationTokenSource();
        var ct = cts.Token;

        // Any in-flight seek verification from the previous track is now stale.
        Engine.SeekVerifyCts?.Cancel();

        // Reset re-open context — populated below for the source we actually use.
        Engine.LastPlayingVideoId = videoId;
        Engine.LastVideoStreamUrl = null;
        Engine.LastAudioStreamUrl = null;
        Engine.LastMuxedStreamUrl = null;
        Engine.LastLocalFilePath = null;
        // Fresh live-stream elapsed clock (restamped on the first position tick).
        Engine.LiveStartUtc = null;

        // Wait for background LibVLC initialization to complete if it's still in flight.
        if (Engine.MediaPlayer == null && Engine.InitTask != null)
        {
            try { await Engine.InitTask.WaitAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch { /* fall through to null check below */ }
        }

        var libVLC = Engine.LibVLC;
        var mediaPlayer = Engine.MediaPlayer;
        if (libVLC == null || mediaPlayer == null) return;

        try
        {
            _host.BeginPlayTransition();

            // Ensure the media player is fully stopped before starting new playback (EndReached leaves
            // it in Ended state; calling Play without Stop first can silently fail).
            if (mediaPlayer.State != VLCState.Stopped)
                await Task.Run(() => mediaPlayer.Stop());

            if (ct.IsCancellationRequested) return;

            // Non-null once a streaming (non-cached) source starts (the "WxH" resolution; "" audio-only).
            string? streamingResolution = null;
            string? cachedResolution = null;

            var vm = Model;
            var state = Context;

            // Applies THIS PLAYER's volume to the live media player. Wrapped so it can be invoked from
            // both the Playing event and the first Vout — libVLC silently ignores/throws on volume sets
            // before its audio output object exists, which happens on a genuinely cold start (no warm
            // plugin cache). Re-asserting at first frame self-corrects a dropped Playing-time set; without
            // it, a fresh Windows per-app mixer session (new exe path) stays at its 100% default.
            void ApplyContextVolume()
            {
                if (state == null) return;
                try { mediaPlayer.Volume = VolumeTaper.VlcVolume(state.Volume); }
                catch (Exception ex) { DebugLog.Log(LogLevel.Debug, "Volume", $"Deferred volume apply skipped: {ex.Message}"); }
            }

            // Wait for first video output before revealing the video surface.
            var voutTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnVout(object? s, MediaPlayerVoutEventArgs a)
            {
                mediaPlayer.Vout -= OnVout;
                // Re-assert volume now that the audio output reliably exists (covers a lost cold-start set).
                ApplyContextVolume();
                _host.BeginInvokeOnHost(() => _host.OnFirstVideoFrame());
                voutTcs.TrySetResult();
            }
            mediaPlayer.Vout += OnVout;

            // Create a fresh video surface, hidden until VLC has a frame ready.
            _host.EnsureVideoSurfaceHidden();

            // Apply THIS PLAYER's volume once VLC actually starts playing (libVLC ignores volume set
            // before a track is playing). One-shot per play. Using the per-player context volume (not the
            // shared VM volume) is what makes per-window audio balance work.
            if (state != null)
            {
                void OnPlayingApplyVolume(object? s, EventArgs a)
                {
                    mediaPlayer.Playing -= OnPlayingApplyVolume;
                    ApplyContextVolume();
                }
                mediaPlayer.Playing += OnPlayingApplyVolume;

                // Reset scrubber and duration for the transition; leave volume untouched.
                state.PlaybackPosition = 0;
                state.PlaybackDuration = 1;
            }

            // Check if this item is audio-only (e.g. Plex music track) OR the host's "stream audio only"
            // setting is on for this player (per-player Context.AudioOnly) — in which case we play the
            // audio but never bring the video surface on screen, keeping the idle/screensaver visuals.
            bool isAudioOnly = state?.CurrentlyPlaying?.IsAudioOnly == true || state?.AudioOnly == true;

            // ── PCM gapless path (sources that can supply a stable pre-loadable audio stream) ──
            if (isAudioOnly && vm?.GaplessPlayback == true
                && state?.CurrentlyPlaying is { } gaplessItem
                && vm.TryGetGaplessStreamUrl(gaplessItem) is { } gaplessUrl)
            {
                mediaPlayer.Vout -= OnVout;
                _host.HideVideoSurface();

                // Lazily create the gapless player (window owns creation — it wires view/VM callbacks).
                Engine.GaplessPlayer ??= _host.CreateGaplessPlayer();
                Engine.UsingGaplessPlayer = true;
                Engine.GaplessPrimed = false;
                Engine.NextGaplessVideoId = null;

                int vol = state.Volume;
                var gp = Engine.GaplessPlayer;
                await Task.Run(() => gp.Play(new Uri(gaplessUrl), vol));

                if (ct.IsCancellationRequested) { gp.Stop(); Engine.UsingGaplessPlayer = false; return; }

                _host.ReturnToIdle();
                _host.StartColorCycle();
                _host.StartPositionTimer();
                _host.NotifyDmdPlaybackStarted();
                vm.NotifyPlaybackStarted(Context);
                DebugLog.Log(LogLevel.Debug, "GaplessPCM", $"Playing via PCM queue: {state.CurrentlyPlaying.Title}");
                return;
            }

            // Plex or other direct-stream source.
            if (state?.CurrentlyPlaying?.StreamUrl is { } streamUrl)
            {
                var media = new Media(libVLC, new Uri(streamUrl));

                // Separate video+audio (yt-dlp SeparateVideoAudio): attach the audio-slave URL.
                if (state.CurrentlyPlaying.AudioStreamUrl is { Length: > 0 } audioSlaveUrl)
                    media.AddSlave(MediaSlaveType.Audio, 4, new Uri(audioSlaveUrl));

                // HLS transcode streams need extra buffering for reliable cold-start.
                if (streamUrl.Contains("transcode", StringComparison.OrdinalIgnoreCase))
                {
                    media.AddOption(":network-caching=5000");
                    media.AddOption(":live-caching=5000");
                    media.AddOption(":adaptive-logic=lowest");
                    media.AddOption(":clock-jitter=0");
                    media.AddOption(":clock-synchro=0");
                    media.AddOption(":http-reconnect");
                    media.AddOption(":sout-mux-caching=5000");
                }

                Engine.LastMuxedStreamUrl = streamUrl;
                mediaPlayer.Play(media);
            }
            // Check local cache first (main cache, then prefetch cache).
            else
            {
                var cached = !isAudioOnly
                    ? (vm?.Cache?.TryGet(videoId) ?? vm?.Prefetch?.TryConsume(videoId))
                    : null;

                if (cached != null)
                {
                    // Play from local muxed file — instant, no buffering, seekable.
                    vm?.SetStatusPrefix("Cached");
                    state?.SetCurrentFromCache(true);
                    DebugLog.Log(LogLevel.Debug, "Play", $"Cached playback: {cached.FilePath}");
                    var media = new Media(libVLC, new Uri(cached.FilePath));
                    Engine.LastLocalFilePath = cached.FilePath;
                    mediaPlayer.Play(media);
                    cachedResolution = cached.Resolution;

                    if (cached.Chapters is { Count: > 0 } && state?.CurrentlyPlaying is { } cp && cp.Chapters == null)
                    {
                        cp.Chapters = cached.Chapters;
                        DebugLog.Log(LogLevel.Trace, "Chapters", $"Restored {cached.Chapters.Count} chapters from cache");
                        state.NotifyCachedChaptersRestored();
                    }
                }
                else
                {
                    var quality = vm?.VideoQuality ?? VideoQualityPreference.High;
                    var stereo = vm?.StereoAudio ?? false;

                    var streams = vm != null
                        ? await vm.ResolveStreamsViaPluginOrLegacy(videoId, quality, stereo, isAudioOnly, ct)
                        : null;
                    if (ct.IsCancellationRequested) { mediaPlayer.Vout -= OnVout; return; }

                    if (streams == null)
                    {
                        mediaPlayer.Vout -= OnVout;
                        Model?.NotifyPlaybackStarted(Context);
                        return;
                    }

                    switch (streams.Kind)
                    {
                        case Phosphor.Video.VideoStreamKind.AudioOnly:
                        {
                            var media = new Media(libVLC, new Uri(streams.PrimaryUrl));
                            MediaEngine.ApplyNetworkOptions(media, vm);
                            Engine.LastAudioStreamUrl = streams.PrimaryUrl;
                            mediaPlayer.Play(media);
                            break;
                        }
                        case Phosphor.Video.VideoStreamKind.SeparateVideoAudio:
                        {
                            var media = new Media(libVLC, new Uri(streams.PrimaryUrl));
                            media.AddSlave(MediaSlaveType.Audio, 4, new Uri(streams.AudioSlaveUrl!));
                            MediaEngine.ApplyNetworkOptions(media, vm);
                            Engine.LastVideoStreamUrl = streams.PrimaryUrl;
                            Engine.LastAudioStreamUrl = streams.AudioSlaveUrl;
                            mediaPlayer.Play(media);
                            streamingResolution = streams.Resolution;
                            break;
                        }
                        default: // Muxed
                        {
                            var media = new Media(libVLC, new Uri(streams.PrimaryUrl));
                            MediaEngine.ApplyNetworkOptions(media, vm);
                            Engine.LastMuxedStreamUrl = streams.PrimaryUrl;
                            mediaPlayer.Play(media);
                            streamingResolution = streams.Resolution;
                            break;
                        }
                    }
                }
            }

            if (isAudioOnly)
            {
                // Audio-only: keep idle screen visible, skip waiting for a video frame.
                mediaPlayer.Vout -= OnVout;
                _host.HideVideoSurface();

                var playingTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnPlaying(object? s, EventArgs a)
                {
                    mediaPlayer.Playing -= OnPlaying;
                    playingTcs.TrySetResult();
                }
                mediaPlayer.Playing += OnPlaying;

                var audioCompleted = await Task.WhenAny(playingTcs.Task, Task.Delay(MediaEngine.FirstFrameTimeoutMs(vm)));
                mediaPlayer.Playing -= OnPlaying;

                if (ct.IsCancellationRequested) return;

                if (audioCompleted != playingTcs.Task)
                {
                    // Timed out — server likely unreachable.
                    await Task.Run(() => mediaPlayer.Stop());
                    _host.DetachVideoView();
                    _host.ReturnToIdle();
                    _host.StartColorCycle();
                    if (Model is { } vmAoTimeout)
                    {
                        vmAoTimeout.StatusText = "Playback failed: server unreachable or stream timed out";
                        vmAoTimeout.NotifyPlaybackFailed(state?.CurrentlyPlaying);
                        if (state != null) state.CurrentlyPlaying = null;
                        vmAoTimeout.NotifyPlaybackStarted(Context);
                    }
                    return;
                }

                _host.ReturnToIdle();
                _host.StartColorCycle();
                _host.StartPositionTimer();
                _host.NotifyDmdPlaybackStarted();
                Model?.NotifyPlaybackStarted(Context);
                return;
            }

            // Wait for the first video frame.
            var completed = await Task.WhenAny(voutTcs.Task, Task.Delay(MediaEngine.FirstFrameTimeoutMs(vm)));
            mediaPlayer.Vout -= OnVout;

            if (ct.IsCancellationRequested) return;

            if (completed != voutTcs.Task)
            {
                // Timed out waiting for video — server likely unreachable.
                await Task.Run(() => mediaPlayer.Stop());
                _host.DetachVideoView();
                _host.ReturnToIdle();
                _host.StartColorCycle();
                if (Model is { } vmTimeout)
                {
                    vmTimeout.StatusText = "Playback failed: server unreachable or stream timed out";
                    vmTimeout.NotifyPlaybackFailed(state?.CurrentlyPlaying);
                    if (state != null) state.CurrentlyPlaying = null;
                    vmTimeout.NotifyPlaybackStarted(Context);
                }
                return;
            }

            _host.EnterMediaMode();

            if (streamingResolution != null)
                _host.StartVideoInfoPolling(streamingResolution);
            else if (cachedResolution != null)
                _host.StartVideoInfoPollingCached(cachedResolution);
            _host.StartPositionTimer();

            // Log seekability diagnostics for streaming (non-cached) playback.
            if (streamingResolution != null)
            {
                var seekable = mediaPlayer.IsSeekable;
                var length = mediaPlayer.Length;
                DebugLog.Log(LogLevel.Debug, "Play", $"Streaming playback started | Seekable={seekable} Length={length}ms | Note: seeking may be unreliable for progressive YouTube streams (no seek index until fully downloaded)");
            }

            // Notify so the DMD window can reclaim focus.
            _host.NotifyDmdPlaybackStarted();
            Model?.NotifyPlaybackStarted(Context);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Play was cancelled by stop or a new play request — silently bail out.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Playback error: {ex.Message}");
            _host.DetachVideoView();
            _host.ReturnToIdle();
            _host.StartColorCycle();
            Model?.NotifyPlaybackStarted(Context);
        }
    }

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
                bool isAudioOnly = Context?.CurrentlyPlaying?.IsAudioOnly == true || Context?.AudioOnly == true;
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
                    if (Context != null) Context.PlaybackPosition = 0;
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
                        if (Context != null) Context.PlaybackPosition = 0;
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

        var state = Context;

        try
        {
            var media = new Media(libVLC, new Uri(cached.FilePath));

            var startSeconds = Math.Max(0, targetMs) / 1000.0;
            media.AddOption($":start-time={startSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");

            // Preserve a paused scrub: re-apply pause once VLC reaches the playing state.
            bool wasPaused = state?.IsPaused == true;
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

            state?.SetCurrentFromCache(true);

            if (state != null)
                state.PlaybackPosition = targetMs;

            if (cached.Chapters is { Count: > 0 } && state?.CurrentlyPlaying is { } cp && cp.Chapters == null)
            {
                cp.Chapters = cached.Chapters;
                state.NotifyCachedChaptersRestored();
            }

            _host.StartVideoInfoPollingCached(cached.Resolution);
        }
        catch (Exception ex)
        {
            DebugLog.LogException("Seek/CacheSwitch", ex);
        }
    }
}

