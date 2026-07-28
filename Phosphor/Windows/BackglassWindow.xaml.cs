using System.Windows;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Phosphor.Audio;
using WpfMedia = System.Windows.Media;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace Phosphor;

public partial class BackglassWindow : JukeboxWindow
{
    // The raw LibVLC engine for this window's player (VLC lifecycle, last-stream context, live clock).
    // Phase 0.6 slice 1: state lives here; the field-named properties below delegate to it so the
    // window's existing engine/orchestration code keeps compiling unchanged while ownership moves.
    private readonly Phosphor.Playback.MediaEngine _engine = new();

    private LibVLC? _libVLC { get => _engine.LibVLC; set => _engine.SetSharedVlc(value); }
    private MediaPlayer? _mediaPlayer => _engine.MediaPlayer;
    private readonly Random _rng = new();
    private readonly DispatcherTimer _colorTimer;
    // Debounces IdleCanvas resize so the blob pattern is rebuilt (re-centered) only
    // once dragging settles, avoiding a storm of rebuilds while the window is resized.
    private readonly DispatcherTimer _resizeDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private DispatcherTimer? _positionTimer;
    // Wall-clock start of the current live stream, used to show elapsed time (LibVLC's Time reflects
    // the live DVR window, not elapsed-since-start). Null when not playing a live stream.
    private DateTime? _liveStartUtc { get => _engine.LiveStartUtc; set => _engine.LiveStartUtc = value; }
    private double _hueOffset;
    private bool _idleAnimStarted;
    private bool _showVideoInfo;
    private DispatcherTimer? _infoTimer;
    private double _blobIntensity = 0.5;
    private double _blobSpeedMultiplier = 1.0;
    private double _brightnessBoost = 1.0;
    private bool _logoSpin = true;
    private LogoRingsMode _logoRings = LogoRingsMode.Standard;
    private BlobPattern _blobPattern = BlobPattern.Random;
    private BlobPattern _blobPatternSetting = BlobPattern.Random;
    private bool _transitioning;
    private IBlobPattern? _currentPattern;
    // True when _currentPattern was created against a valid (non-zero) IdleCanvas size.
    // When the app starts in a media/Pinup ambient mode the IdleCanvas is collapsed
    // (0-size), so a pattern built then clusters the blobs in the corner; this flag lets
    // the Screensaver transition rebuild ONLY in that case, preserving continuity when
    // returning from a video whose canvas was already correctly sized.
    private bool _idlePatternLaidOut;
    private AudioReactiveService? _audioReactive;
    private double _reactiveHueBoost;
    private int _blobCount = 6;
    private int _blobSizeOffset;
    private LibVLCSharp.WPF.VideoView? _videoView;
    private bool _logoDimEnabled;
    private double _logoDimOpacity;
    private double _logoBrightness = 1.0;
    private bool _isLogoDimmed;
    private readonly DispatcherTimer _logoDimTimer = new();
    private bool _logoMorphEnabled;
    private bool _audioOnly;
    private CancellationTokenSource? _playCts;
    private readonly DispatcherTimer _morphTimer = new();
    private Task? _vlcInitTask { get => _engine.InitTask; set => _engine.InitTask = value; }
    private readonly DispatcherTimer _expandButtonHideTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    // ── Gapless playback (state lives in MediaEngine; delegated here so existing code compiles) ──
    private MediaPlayer? _nextMediaPlayer { get => _engine.NextMediaPlayer; set => _engine.NextMediaPlayer = value; }
    private string? _nextGaplessVideoId { get => _engine.NextGaplessVideoId; set => _engine.NextGaplessVideoId = value; }
    private bool _gaplessPrimed { get => _engine.GaplessPrimed; set => _engine.GaplessPrimed = value; }

    // ── PCM gapless playback ──
    private GaplessAudioPlayer? _gaplessPlayer { get => _engine.GaplessPlayer; set => _engine.GaplessPlayer = value; }
    private bool _usingGaplessPlayer { get => _engine.UsingGaplessPlayer; set => _engine.UsingGaplessPlayer = value; }

    // ── Last YouTube/HTTP stream context (used for re-opening on failed seek) ──
    // These are populated whenever we kick off playback so that OnSeekRequested can
    // rebuild a Media with ":start-time=<seconds>" without re-querying the manifest.
    // _lastLocalFilePath is set for cached/prefetched playback (re-open uses Time/Position
    // since seeking always works on local files). Backed by the MediaEngine (slice 1).
    private string? _lastPlayingVideoId { get => _engine.LastPlayingVideoId; set => _engine.LastPlayingVideoId = value; }
    private string? _lastVideoStreamUrl { get => _engine.LastVideoStreamUrl; set => _engine.LastVideoStreamUrl = value; }
    private string? _lastAudioStreamUrl { get => _engine.LastAudioStreamUrl; set => _engine.LastAudioStreamUrl = value; }
    private string? _lastMuxedStreamUrl { get => _engine.LastMuxedStreamUrl; set => _engine.LastMuxedStreamUrl = value; }
    private string? _lastLocalFilePath { get => _engine.LastLocalFilePath; set => _engine.LastLocalFilePath = value; }

    // ── Seek verification cancellation ──
    // A new seek request cancels any in-flight verification from the previous seek so
    // that the older verification can't "restore" a position over the new target.
    private CancellationTokenSource? _seekVerifyCts;

    // ── Transition idle-overlay reveal timer ──
    // During a track-to-track transition we delay showing the idle (logo/blob) overlay
    // by a short window so cached/prefetched transitions (which Vout in 100-300ms)
    // remain a clean black-to-video swap. Only slower buffering transitions reveal the
    // overlay, avoiding a jarring blob-screen blip on fast transitions.
    private DispatcherTimer? _transitionOverlayTimer;
    private const int TransitionOverlayDelayMs = 600;

    // Default budget for the first-frame / audio-start watchdog. Finite media (YouTube, Plex/Jellyfin
    // on-demand) uses this; slow-starting live streams (Plex/Jellyfin Live TV) request a longer budget
    // via ResolvedStream.StartupTimeout → VideoItem.StartupTimeout so they aren't killed before their
    // first HLS segment (tuner + transcode spin-up) appears.
    private const int DefaultFirstFrameTimeoutMs = 10000;

    /// <summary>
    /// The first-frame watchdog budget (ms) for the item now starting: the source-supplied
    /// <see cref="VideoItem.StartupTimeout"/> hint when present (slow-starting live streams), otherwise
    /// the standard <see cref="DefaultFirstFrameTimeoutMs"/> for finite media.
    /// </summary>
    private static int FirstFrameTimeoutMs(JukeboxViewModel? vm)
    {
        if (vm?.CurrentlyPlaying?.StartupTimeout is { } budget && budget > TimeSpan.Zero)
            return (int)Math.Min(budget.TotalMilliseconds, int.MaxValue);
        return DefaultFirstFrameTimeoutMs;
    }

    public MediaPlayer MediaPlayer => EnsureVlcInitialized();

    /// <summary>
    /// Returns the MediaPlayer, waiting for background initialization if needed.
    /// Called from the backglass dispatcher thread; pumps messages while waiting
    /// so the UI stays responsive.
    /// </summary>
    private MediaPlayer EnsureVlcInitialized()
    {
        return _engine.EnsureInitialized(Dispatcher);
    }

    /// <summary>
    /// Accepts a shared LibVLC instance from the application so all
    /// consumers reuse a single plugin-scan cost.
    /// Must be called before <see cref="InitializeVlcCore"/>.
    /// </summary>
    public void SetSharedVlc(LibVLC? vlc)
    {
        _engine.SetSharedVlc(vlc);
    }

    /// <summary>
    /// Accepts a task that will produce the shared LibVLC instance. The window's
    /// background init task (started in Loaded) awaits this without blocking the
    /// caller, so app startup doesn't have to wait for plugin scanning to finish.
    /// </summary>
    public void SetSharedVlcTask(Task<LibVLC?>? task)
    {
        _engine.SetSharedVlcTask(task);
    }

    /// <summary>
    /// Core LibVLC + MediaPlayer creation. Thread-safe; called once from
    /// either the background init task or synchronously as a fallback.
    /// Reuses a shared LibVLC instance if one was provided via <see cref="SetSharedVlc"/>.
    /// </summary>
    private void InitializeVlcCore()
    {
        _engine.InitializeCore(Dispatcher);
    }

    /// <summary>
    /// Raised after playback has started so the DMD window can reclaim focus.
    /// </summary>
    public event Action? PlaybackStarted;
    public event Action<string>? VideoInfoChanged;

    /// <summary>
    /// Raised when logo morph colors change, with (titleColor, recordColor).
    /// </summary>
    public event Action<WpfColor, WpfColor>? LogoColorsMorphed;

    /// <summary>
    /// Raised when logo colors are reset to defaults.
    /// </summary>
    public event Action? LogoColorsReset;

    /// <summary>
    /// Creates the VLC VideoView and inserts it into the visual tree.
    /// Called only when playback is about to start so the WinForms HWND
    /// doesn't force software rendering during idle animations.
    /// </summary>
    private LibVLCSharp.WPF.VideoView EnsureVideoView()
    {
        if (_videoView != null)
            return _videoView;

        _videoView = new LibVLCSharp.WPF.VideoView
        {
            Background = WpfMedia.Brushes.Black,
            MediaPlayer = EnsureVlcInitialized(),
            Visibility = Visibility.Hidden,
            Focusable = false,
        };
        System.Windows.Controls.Panel.SetZIndex(_videoView, 0);
        RootGrid.Children.Insert(0, _videoView);
        HookVideoViewForDrag();
        return _videoView;
    }

    /// <summary>
    /// Removes the VideoView from the visual tree so WPF can use
    /// GPU-accelerated rendering for the idle overlay.
    /// </summary>
    private void DetachVideoView()
    {
        if (_videoView != null)
        {
            RootGrid.Children.Remove(_videoView);
            _videoView = null;
        }
    }

    public BackglassWindow()
    {
        _colorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _colorTimer.Tick += ColorCycleBlobs;

        // The MediaEngine owns the VLC player and raises EndReached/Buffering; keep the window's
        // existing handlers by subscribing them here (they still touch view/VM state on the window).
        _engine.EndReached += OnMediaEnded;
        _engine.Buffering += OnMediaBuffering;

        _resizeDebounceTimer.Tick += OnResizeDebounceTick;

        _logoDimTimer.Tick += LogoDimTimer_Tick;
        _morphTimer.Tick += MorphTimer_Tick;
        _expandButtonHideTimer.Tick += (_, _) =>
        {
            _expandButtonHideTimer.Stop();
            if (ExpandButton != null)
                ExpandButton.Visibility = Visibility.Collapsed;
            if (MoveDisplayButton != null)
                MoveDisplayButton.Visibility = Visibility.Collapsed;
        };

        // Prevent this window from stealing focus when the VLC VideoView
        // (WinForms-hosted HWND) is inserted into the visual tree.
        ShowActivated = false;

        InitializeComponent();

        // The Backglass is intentionally non-activating (WS_EX_NOACTIVATE), so its
        // Activated event never fires on mouse interaction. Re-reveal the auto-hiding
        // expand controls on any pointer movement over the WPF surface instead.
        PreviewMouseMove += (_, _) => RevealExpandButton();

        IsVisibleChanged += OnIsVisibleChanged;

        SourceInitialized += (_, _) =>
        {
            // Add WS_EX_NOACTIVATE so the WinForms-hosted VLC surface
            // cannot pull activation away from the DMD control window.
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            const int GWL_EXSTYLE = -20;
            const int WS_EX_NOACTIVATE = 0x08000000;
            var ex = GetWindowLong(handle, GWL_EXSTYLE);
            SetWindowLong(handle, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE);
        };

        Loaded += (_, _) =>
        {
            StartIdleAnimation();

            // PROFILING: monitor backglass dispatcher stalls.
            // Hooks CompositionTarget.Rendering on the backglass thread and
            // logs any gap larger than the threshold below. Normal vsync at
            // 72 FPS produces ~14 ms gaps; anything materially larger means
            // the dispatcher was blocked, which can freeze animation values
            // and produce the visible "hitch + catch-up" symptom on slow
            // morphs even when PresentMon shows healthy frame times.
            // Debug-only: the Rendering hook fires every vsync on the UI
            // thread, so we keep it out of Release builds.
#if DEBUG
            StartBackglassStallMonitor();
#endif

            // Initialize LibVLC on a background thread so the window
            // appears immediately without the ~17% startup cost.
            // If the user hits play before this completes,
            // EnsureVlcInitialized will wait with dispatcher pumping.
            // The engine's InitializeCore adopts the shared-VLC task result internally.
            _engine.InitTask = Task.Run(() => _engine.InitializeCore(Dispatcher));
        };
    }

    protected override void UpdateExpandButtonVisibility(bool isActive)
    {
        if (ExpandButton == null) return;
        if (isActive)
        {
            ExpandButton.Visibility = Visibility.Visible;
            if (MoveDisplayButton != null)
                MoveDisplayButton.Visibility = MoveViewerButtonsAllowed ? Visibility.Visible : Visibility.Collapsed;
            _expandButtonHideTimer.Stop();
            _expandButtonHideTimer.Start();
        }
        else
        {
            _expandButtonHideTimer.Stop();
            ExpandButton.Visibility = Visibility.Collapsed;
            if (MoveDisplayButton != null) MoveDisplayButton.Visibility = Visibility.Collapsed;
        }
    }

    public override void ForceHideExpandButton()
    {
        _expandButtonHideTimer.Stop();
        if (ExpandButton != null)
            ExpandButton.Visibility = Visibility.Collapsed;
        if (MoveDisplayButton != null)
            MoveDisplayButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Re-shows the auto-hiding expand/move controls and restarts the hide timer.
    /// Driven by pointer movement over the Backglass because the window is
    /// non-activating and never fires <see cref="System.Windows.Window.Activated"/>.
    /// </summary>
    private void RevealExpandButton()
    {
        if (ExpandButton == null) return;
        ExpandButton.Visibility = Visibility.Visible;
        if (MoveDisplayButton != null)
            MoveDisplayButton.Visibility = MoveViewerButtonsAllowed ? Visibility.Visible : Visibility.Collapsed;
        _expandButtonHideTimer.Stop();
        _expandButtonHideTimer.Start();
    }

    private void HookVideoViewForDrag()
    {
        if (_videoView == null) return;
        // The VLC VideoView hosts a WinForms control for video rendering.
        // WPF mouse events don't reach through the airspace boundary, so we
        // hook the WinForms child's MouseDown to initiate a Win32-level drag.
        var host = FindVisualChild<System.Windows.Forms.Integration.WindowsFormsHost>(_videoView);
        if (host?.Child is System.Windows.Forms.Control child)
        {
            // Prevent white flash � WinForms default BackColor is white
            child.BackColor = System.Drawing.Color.Black;

            child.MouseDown += (_, me) =>
            {
                if (me.Button == System.Windows.Forms.MouseButtons.Left)
                    BeginDragOrResizeFromChild(me.X, me.Y);
            };

            // Show sizing cursors near the edges so the user can tell the window is
            // resizable even while a video covers the client area.
            child.MouseMove += (_, me) =>
            {
                child.Cursor = GetChildResizeCursor(me.X, me.Y);
                // WPF PreviewMouseMove doesn't fire over the WinForms video surface,
                // so reveal the auto-hiding expand/move controls from here too.
                Dispatcher.BeginInvoke(RevealExpandButton);
            };
        }
    }

    private static T? FindVisualChild<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    public void AttachViewModel(JukeboxViewModel vm)
    {
        // Bind this window's playback engine to Player 1's command channel (context → player → host).
        // JukeboxPlayer now owns the play / stop / seek / pause / resume / volume handlers (it
        // subscribes them in Attach and forwards to this window via IPlaybackHost). The play/stop/seek
        // engine bodies still live in this window (OnPlayRequested/OnStopRequested/OnSeekRequested) and
        // relocate into JukeboxPlayer in a later increment.
        JukeboxPlayer.Attach(vm.Player1);

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _positionTimer.Tick += (_, _) =>
        {
            if (DataContext is not JukeboxViewModel v || v.IsSeeking) return;

            // PCM gapless mode
            if (_usingGaplessPlayer && _gaplessPlayer != null)
            {
                v.PlaybackDuration = Math.Max(1, _gaplessPlayer.DurationMs);
                v.PlaybackPosition = Math.Max(0, _gaplessPlayer.PositionMs);

                // Prime next track ~10 seconds before end
                var remaining = _gaplessPlayer.DurationMs - _gaplessPlayer.PositionMs;
                if (_gaplessPlayer.DurationMs > 0 && remaining > 0 && remaining <= 10_000 && !_gaplessPrimed)
                    PrepareGaplessNext(v);
                return;
            }

            if (_mediaPlayer == null) return;

            // Live streams (e.g. SiriusXM): LibVLC's Time is the position within the live DVR window
            // (SXM buffers ~2 min), which makes elapsed jump to ~2:20 at start. Instead, count elapsed
            // from a wall-clock stamp taken when playback began, and report no fixed duration.
            if (v.CurrentlyPlaying?.IsLiveStream == true)
            {
                if (_liveStartUtc == null) _liveStartUtc = DateTime.UtcNow;
                v.PlaybackDuration = 1; // no seekable duration
                v.PlaybackPosition = Math.Max(0, (DateTime.UtcNow - _liveStartUtc.Value).TotalMilliseconds);
                return;
            }
            _liveStartUtc = null;

            // LibVLC reports Length = 0 for some transcoded/chunked HTTP streams (e.g. a Jellyfin
            // HLS transcode), which would pin the scrub bar "at the end". Fall back to the item's
            // known duration (Jellyfin RunTimeTicks, Plex duration, …) so time/seek still work.
            long lengthMs = _mediaPlayer.Length;
            if (lengthMs <= 0 && v.CurrentlyPlaying?.Duration is { } known && known > TimeSpan.Zero)
                lengthMs = (long)known.TotalMilliseconds;

            v.PlaybackDuration = Math.Max(1, lengthMs);
            v.PlaybackPosition = Math.Max(0, _mediaPlayer.Time);

            // Prefetch next track when within 30 seconds of the end
            var remaining2 = lengthMs - _mediaPlayer.Time;
            if (lengthMs > 0 && remaining2 > 0 && remaining2 <= 30_000)
                v.PrefetchNextTrack();

            // Gapless: prime the next audio-only Plex track ~5 seconds before end
            if (lengthMs > 0 && remaining2 > 0 && remaining2 <= 5_000 && !_gaplessPrimed)
                PrepareGaplessNext(v);
        };
    }

    public void SetAudioOnly(bool audioOnly) => _audioOnly = audioOnly;

    /// <summary>
    /// Pre-loads the next Plex audio-only track for gapless transition.
    /// Uses the PCM queue player when in gapless PCM mode, otherwise falls back
    /// to the legacy dual-MediaPlayer approach.
    /// </summary>
    private void PrepareGaplessNext(JukeboxViewModel vm)
    {
        var nextTrack = vm.GetNextGaplessTrack();
        if (nextTrack == null || _libVLC == null) return;
        if (_nextGaplessVideoId == nextTrack.VideoId) return; // already preparing this one

        _gaplessPrimed = true;
        _nextGaplessVideoId = nextTrack.VideoId;

        // PCM gapless mode: prime on the idle decoder
        if (_usingGaplessPlayer && _gaplessPlayer != null)
        {
            _gaplessPlayer.PrimeNext(new Uri(nextTrack.StreamUrl!));
            return;
        }

        // Legacy dual-MediaPlayer approach
        var oldNext = _nextMediaPlayer;
        if (oldNext != null)
        {
            _nextMediaPlayer = null;
            Task.Run(() => { try { oldNext.Stop(); oldNext.Dispose(); } catch { } });
        }

        var mp = new MediaPlayer(_libVLC);
        mp.EnableMouseInput = false;
        mp.EnableKeyInput = false;
        mp.Volume = _mediaPlayer?.Volume ?? 100;
        var media = new Media(_libVLC, new Uri(nextTrack.StreamUrl!));

        // Start playback then immediately pause — this forces VLC to connect,
        // buffer, and decode the first frame so Play() later is instant.
        mp.Play(media);
        mp.SetPause(true);

        _nextMediaPlayer = mp;
        DebugLog.Log(LogLevel.Debug, "Gapless", $"Primed next track: {nextTrack.Title} ({nextTrack.VideoId})");
    }

    /// <summary>
    /// Resets gapless state (e.g. when playback is stopped or a non-gapless transition occurs).
    /// </summary>
    private void DisposeGaplessNext()
    {
        _engine.DisposeGaplessNext();
    }

    /// <summary>
    /// Creates and wires a GaplessAudioPlayer for PCM-queue gapless playback.
    /// </summary>
    private GaplessAudioPlayer CreateGaplessPlayer()
    {
        var player = new GaplessAudioPlayer(_libVLC!);
        player.TrackAdvanced += hasNext =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (DataContext is JukeboxViewModel vm && hasNext)
                {
                    vm.AdvanceQueueGapless();
                    DebugLog.Log(LogLevel.Debug, "GaplessPCM", "Track advanced via PCM queue");

                    // Notify listeners (DmdWindow.OnPlaybackStartedTransition) so
                    // per-track-change features (RandomPerSong transitions, Game of
                    // Life "Reset on Track", Gravity restart, etc.) fire on gapless
                    // PCM transitions too — the non-gapless playback paths above
                    // already raise PlaybackStarted at track start.
                    PlaybackStarted?.Invoke();
                }
            });
        };
        player.PlaybackFinished += () =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                DebugLog.Log(LogLevel.Info, "GaplessPCM", "Playback finished (all tracks drained)");
                _usingGaplessPlayer = false;
                _positionTimer?.Stop();

                if (DataContext is JukeboxViewModel vm && vm.HasNextTrack)
                    vm.PlayNext();
                else
                {
                    ShowIdleBackground();
                    _colorTimer.Start();
                    ResetLogoDimIdle();
                    if (DataContext is JukeboxViewModel vm2)
                        vm2.PlayNext(); // will set "Queue finished"
                }
            });
        };
        return player;
    }

    /// <summary>
    /// Stops the PCM gapless player if active.
    /// </summary>
    private void StopGaplessPlayer()
    {
        _engine.StopGaplessPlayer();
    }

    private static void ApplyNetworkOptions(Media media, JukeboxViewModel? vm)
    {
        int networkCache = vm?.NetworkCachingMs ?? 2000;
        int liveCache = vm?.LiveCachingMs ?? 1000;
        int fileCache = vm?.FileCachingMs ?? 300;
        bool reconnect = vm?.HttpReconnect ?? true;

        media.AddOption($":network-caching={networkCache}");
        media.AddOption($":live-caching={liveCache}");
        media.AddOption($":file-caching={fileCache}");
        if (reconnect)
            media.AddOption(":http-reconnect");

        // NOTE: We intentionally do NOT set :input-fast-seek. The VLC docs warn it
        // "may cause errors when seeking forward in a stream because the demuxer may
        // not be at a keyframe." For long YouTube videos this manifests as the
        // decoder freezing on a non-keyframe after a forward scrub — Time stays
        // frozen at the seek target, video restarts from 0, and Play/Pause stop
        // responding. The slower default behavior fails more cleanly (Time drops to
        // a small value) and our verification can detect and recover.
    }

    private async void OnPlayRequested(string videoId)
    {
        // VM events fire on the main UI thread � marshal to our thread
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => OnPlayRequested(videoId));
            return;
        }

        // Cancel any in-flight play operation
        _playCts?.Cancel();
        StopGaplessPlayer();
        DisposeGaplessNext();
        var cts = _playCts = new CancellationTokenSource();
        var ct = cts.Token;

        // Any in-flight seek verification from the previous track is now stale.
        // Cancel it so it can't restore an old position into a brand-new player.
        _seekVerifyCts?.Cancel();

        // Reset re-open context — populated below for the source we actually use.
        _lastPlayingVideoId = videoId;
        _lastVideoStreamUrl = null;
        _lastAudioStreamUrl = null;
        _lastMuxedStreamUrl = null;
        _lastLocalFilePath = null;
        // Fresh live-stream elapsed clock (restamped on the first position tick).
        _liveStartUtc = null;

        // Wait for background LibVLC initialization to complete if it's still
        // in flight (e.g. user hit play within the first few seconds of launch).
        // Without this, the request would silently drop and leave the UI stuck
        // showing "Playing: <title>" with nothing actually happening.
        if (_mediaPlayer == null && _vlcInitTask != null)
        {
            try { await _vlcInitTask.WaitAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch { /* fall through to null check below */ }
        }

        if (_libVLC == null || _mediaPlayer == null) return;

        try
            {
                _colorTimer.Stop();

                // Cancel any pending delayed-overlay reveal from a previous transition
                _transitionOverlayTimer?.Stop();
                _transitionOverlayTimer = null;

                // During transitions (video view still attached from previous track),
                // detach the old video view BEFORE stopping. This removes the WinForms
                // HWND from the visual tree so VLC's surface clear isn't visible — the
                // black Grid background shows through instead of a white flash.
                bool isTransition = _videoView != null;
                if (isTransition)
                {
                    DetachVideoView();

                    // Schedule a delayed reveal of the idle overlay. Cached / prefetched
                    // transitions typically Vout within 100-300ms, so the timer fires
                    // *after* the overlay is no longer needed and OnVout cancels it —
                    // the user sees a clean black-to-video swap with no blob-screen blip.
                    // Only slower buffering transitions (>600ms) reach the timer tick and
                    // reveal the overlay, which then animates until the new video appears.
                    _transitionOverlayTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(TransitionOverlayDelayMs)
                    };
                    _transitionOverlayTimer.Tick += (_, _) =>
                    {
                        _transitionOverlayTimer?.Stop();
                        _transitionOverlayTimer = null;
                        // Only reveal if video still hasn't appeared (videoView is hidden)
                        if (_videoView == null || _videoView.Visibility != Visibility.Visible)
                        {
                            ShowIdleBackground();
                            _colorTimer.Start();
                        }
                    };
                    _transitionOverlayTimer.Start();
                }

            // Ensure the media player is fully stopped before starting new playback.
            // This is critical when called from OnMediaEnded (via PlayNext) because
            // LibVLC's EndReached leaves the player in Ended state � calling Play
            // without Stop first can silently fail.
            if (_mediaPlayer.State != VLCState.Stopped)
            {
                await Task.Run(() => _mediaPlayer.Stop());
            }

            if (ct.IsCancellationRequested) return;

            // Non-null once a streaming (non-cached) source starts. Doubles as the
            // "is streaming" signal for the overlay + seekability diagnostics; the
            // string is the "WxH" resolution ("" for audio-only).
            string? streamingResolution = null;
            string? cachedResolution = null;

            // Wait for first video output before revealing the video surface
            var voutTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnVout(object? s, MediaPlayerVoutEventArgs a)
            {
                _mediaPlayer.Vout -= OnVout;
                Dispatcher.BeginInvoke(() =>
                {
                    // Cancel the pending overlay reveal — video is ready, no need to flash blobs.
                    _transitionOverlayTimer?.Stop();
                    _transitionOverlayTimer = null;

                    if (_videoView != null)
                    {
                        _videoView.Visibility = Visibility.Visible;
                        HookVideoViewForDrag();
                    }
                    // Hide idle overlay once video is rendering (in case it was
                    // briefly shown during a slow transition)
                    HideIdleForJukeboxVideo();
                    // Stop the blob color cycle now that the overlay is hidden — no
                    // point burning CPU on an invisible surface during playback.
                    _colorTimer.Stop();
                });
                voutTcs.TrySetResult();
            }
            _mediaPlayer.Vout += OnVout;

            // Create a fresh VideoView, hidden until VLC has a frame ready.
            var videoView = EnsureVideoView();
            videoView.Visibility = Visibility.Hidden;

            var vm = DataContext as JukeboxViewModel;

            // Apply the VM's volume once VLC actually starts playing. libVLC ignores volume set
            // before a track is playing, and _mediaPlayer.Volume defaults to 0/-1 until a
            // VolumeChanged event has fired — so without this, direct-stream/local playback is
            // silent on a fresh start (the gapless PCM path applies volume separately). One-shot
            // per play; re-subscribed on each call.
            if (vm != null)
            {
                void OnPlayingApplyVolume(object? s, EventArgs a)
                {
                    _mediaPlayer.Playing -= OnPlayingApplyVolume;
                    try { _mediaPlayer.Volume = VolumeTaper.VlcVolume(vm.Volume); } catch { /* player may be tearing down */ }
                }
                _mediaPlayer.Playing += OnPlayingApplyVolume;
            }

            // Reset scrubber and duration for the transition; leave volume untouched
            if (vm != null)
            {
                vm.PlaybackPosition = 0;
                vm.PlaybackDuration = 1;
            }

            // Check if this item is audio-only (e.g. Plex music track)
            bool isAudioOnly = _audioOnly || (vm?.CurrentlyPlaying?.IsAudioOnly == true);

            // ── PCM gapless path (sources that can supply a stable pre-loadable audio stream) ──
            if (isAudioOnly && vm?.GaplessPlayback == true
                && vm.CurrentlyPlaying is { } gaplessItem
                && vm.TryGetGaplessStreamUrl(gaplessItem) is { } gaplessUrl)
            {
                _mediaPlayer.Vout -= OnVout;
                if (_videoView != null)
                    _videoView.Visibility = Visibility.Hidden;

                // Lazily create the gapless player
                _gaplessPlayer ??= CreateGaplessPlayer();
                _usingGaplessPlayer = true;
                _gaplessPrimed = false;
                _nextGaplessVideoId = null;

                // Play via PCM queue engine (blocking wait handled internally).
                // Use the VM's volume — _mediaPlayer.Volume may be 0 or -1 if no
                // VolumeChanged event has fired yet.
                int vol = vm.Volume;
                await Task.Run(() => _gaplessPlayer.Play(new Uri(gaplessUrl), vol));

                if (ct.IsCancellationRequested) { _gaplessPlayer.Stop(); _usingGaplessPlayer = false; return; }

                ShowIdleBackground();
                _colorTimer.Start();
                _positionTimer?.Start();
                PlaybackStarted?.Invoke();
                vm.NotifyPlaybackStarted();
                DebugLog.Log(LogLevel.Debug, "GaplessPCM", $"Playing via PCM queue: {vm.CurrentlyPlaying.Title}");
                return;
            }

            // Plex or other direct-stream source
            if (vm?.CurrentlyPlaying?.StreamUrl is { } streamUrl)
            {
                var media = new Media(_libVLC, new Uri(streamUrl));

                // Separate video+audio (yt-dlp SeparateVideoAudio, e.g. Vimeo/Dailymotion): attach the
                // audio-slave URL so the video-only primary actually has sound.
                if (vm.CurrentlyPlaying.AudioStreamUrl is { Length: > 0 } audioSlaveUrl)
                    media.AddSlave(MediaSlaveType.Audio, 4, new Uri(audioSlaveUrl));

                // HLS transcode streams need extra buffering for reliable cold-start
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

                // Remember the source so a failed in-place seek can re-open at :start-time.
                _lastMuxedStreamUrl = streamUrl;

                _mediaPlayer.Play(media);
            }
            // Check local cache first (main cache, then prefetch cache)
            else
            {
                var cached = !isAudioOnly
                    ? (vm?.Cache?.TryGet(videoId) ?? vm?.Prefetch?.TryConsume(videoId))
                    : null;

                if (cached != null)
                {
                    // Play from local muxed file � instant, no buffering, seekable
                    vm?.SetStatusPrefix("Cached");
                    vm?.SetCurrentFromCache(true);
                    DebugLog.Log(LogLevel.Debug, "Play", $"Cached playback: {cached.FilePath}");
                    var media = new Media(_libVLC, new Uri(cached.FilePath));
                    _lastLocalFilePath = cached.FilePath;
                    _mediaPlayer.Play(media);
                    cachedResolution = cached.Resolution;

                    // Restore cached chapters to the playing item
                    if (cached.Chapters is { Count: > 0 } && vm?.CurrentlyPlaying is { } cp && cp.Chapters == null)
                    {
                        cp.Chapters = cached.Chapters;
                        DebugLog.Log(LogLevel.Trace, "Chapters", $"Restored {cached.Chapters.Count} chapters from cache");
                        vm.NotifyCachedChaptersRestored();
                    }
                }
                else
                {
                    var quality = vm?.VideoQuality ?? VideoQualityPreference.High;
                    var stereo = vm?.StereoAudio ?? false;

                    // Route through the VM so the plug-in source path is honored. The helper
                    // returns plain data (no UI/dispatcher), so awaiting it from this window's own
                    // thread is safe. Without a VM there is no source to resolve — nothing to play.
                    var streams = vm != null
                        ? await vm.ResolveStreamsViaPluginOrLegacy(videoId, quality, stereo, isAudioOnly, ct)
                        : null;
                    if (ct.IsCancellationRequested) { _mediaPlayer.Vout -= OnVout; return; }

                    if (streams == null)
                    {
                        _mediaPlayer.Vout -= OnVout;
                        (DataContext as JukeboxViewModel)?.NotifyPlaybackStarted();
                        return;
                    }

                    switch (streams.Kind)
                    {
                        case Phosphor.Video.VideoStreamKind.AudioOnly:
                        {
                            // Audio-only mode — stream only audio, no video download
                            var media = new Media(_libVLC, new Uri(streams.PrimaryUrl));
                            ApplyNetworkOptions(media, vm);
                            _lastAudioStreamUrl = streams.PrimaryUrl;
                            _mediaPlayer.Play(media);
                            break;
                        }
                        case Phosphor.Video.VideoStreamKind.SeparateVideoAudio:
                        {
                            // Feed video as primary, audio as slave input
                            var media = new Media(_libVLC, new Uri(streams.PrimaryUrl));
                            media.AddSlave(MediaSlaveType.Audio, 4, new Uri(streams.AudioSlaveUrl!));
                            ApplyNetworkOptions(media, vm);
                            _lastVideoStreamUrl = streams.PrimaryUrl;
                            _lastAudioStreamUrl = streams.AudioSlaveUrl;
                            _mediaPlayer.Play(media);
                            streamingResolution = streams.Resolution;
                            break;
                        }
                        default: // Muxed
                        {
                            // Fallback to muxed if separate streams aren't available
                            var media = new Media(_libVLC, new Uri(streams.PrimaryUrl));
                            ApplyNetworkOptions(media, vm);
                            _lastMuxedStreamUrl = streams.PrimaryUrl;
                            _mediaPlayer.Play(media);
                            streamingResolution = streams.Resolution;
                            break;
                        }
                    }
                }
            }

            if (isAudioOnly)
            {
                // Audio-only: keep idle screen visible, skip waiting for video frame
                _mediaPlayer.Vout -= OnVout;
                if (_videoView != null)
                    _videoView.Visibility = Visibility.Hidden;

                // Wait up to 10s for VLC to actually start playing the audio stream
                var playingTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnPlaying(object? s, EventArgs a)
                {
                    _mediaPlayer.Playing -= OnPlaying;
                    playingTcs.TrySetResult();
                }
                _mediaPlayer.Playing += OnPlaying;

                var audioCompleted = await Task.WhenAny(playingTcs.Task, Task.Delay(FirstFrameTimeoutMs(vm)));
                _mediaPlayer.Playing -= OnPlaying;

                if (ct.IsCancellationRequested) return;

                if (audioCompleted != playingTcs.Task)
                {
                    // Timed out � server likely unreachable
                    await Task.Run(() => _mediaPlayer.Stop());
                    DetachVideoView();
                    ShowIdleBackground();
                    _colorTimer.Start();
                    if (DataContext is JukeboxViewModel vmAoTimeout)
                    {
                        vmAoTimeout.StatusText = "Playback failed: server unreachable or stream timed out";
                        vmAoTimeout.NotifyPlaybackFailed(vmAoTimeout.CurrentlyPlaying);
                        vmAoTimeout.CurrentlyPlaying = null;
                        vmAoTimeout.NotifyPlaybackStarted();
                    }
                    return;
                }

                ShowIdleBackground();
                _colorTimer.Start();
                _positionTimer?.Start();
                PlaybackStarted?.Invoke();
                if (DataContext is JukeboxViewModel vmAo)
                    vmAo.NotifyPlaybackStarted();
                return;
            }

            // Wait up to 10s for first video frame
            var completed = await Task.WhenAny(voutTcs.Task, Task.Delay(FirstFrameTimeoutMs(vm)));
            _mediaPlayer.Vout -= OnVout;

            if (ct.IsCancellationRequested) return;

            if (completed != voutTcs.Task)
            {
                // Timed out waiting for video � server likely unreachable
                await Task.Run(() => _mediaPlayer.Stop());
                DetachVideoView();
                ShowIdleBackground();
                _colorTimer.Start();
                if (DataContext is JukeboxViewModel vmTimeout)
                {
                    vmTimeout.StatusText = "Playback failed: server unreachable or stream timed out";
                    vmTimeout.NotifyPlaybackFailed(vmTimeout.CurrentlyPlaying);
                    vmTimeout.CurrentlyPlaying = null;
                    vmTimeout.NotifyPlaybackStarted();
                }
                return;
            }

            HideIdleForJukeboxVideo();

            if (streamingResolution != null)
                StartVideoInfoPolling(streamingResolution);
            else if (cachedResolution != null)
                StartVideoInfoPollingCached(cachedResolution);
            _positionTimer?.Start();

            // Log seekability diagnostics for streaming (non-cached) playback
            if (streamingResolution != null)
            {
                var seekable = _mediaPlayer.IsSeekable;
                var length = _mediaPlayer.Length;
                DebugLog.Log(LogLevel.Debug, "Play", $"Streaming playback started | Seekable={seekable} Length={length}ms | Note: seeking may be unreliable for progressive YouTube streams (no seek index until fully downloaded)");
            }

            // Notify so the DMD window can reclaim focus
            PlaybackStarted?.Invoke();

            // Allow new play requests now that playback is established
            if (DataContext is JukeboxViewModel vm2)
                vm2.NotifyPlaybackStarted();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Play was cancelled by stop or a new play request � silently bail out
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Playback error: {ex.Message}");
            DetachVideoView();
            ShowIdleBackground();
            _colorTimer.Start();
            if (DataContext is JukeboxViewModel vmErr)
                vmErr.NotifyPlaybackStarted();
        }
    }

    private void OnSeekRequested(long timeMs)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // PCM gapless mode: seek via the gapless player
            if (_usingGaplessPlayer && _gaplessPlayer != null)
            {
                _gaplessPlayer.Seek(timeMs);
                DebugLog.Log(LogLevel.Trace, "Seek", $"PCM gapless seek to {timeMs}ms");
                return;
            }

            if (_mediaPlayer == null) return;
            var length = _mediaPlayer.Length;
            var seekable = _mediaPlayer.IsSeekable;
            DebugLog.Log(LogLevel.Trace, "Seek", $"Requested: {timeMs}ms | State={_mediaPlayer.State} Length={length} Time={_mediaPlayer.Time} Seekable={seekable}");

            if (length <= 0)
            {
                DebugLog.Log(LogLevel.Trace, "Seek", "Skipped: Length <= 0");
                return;
            }

            var timeBefore = _mediaPlayer.Time;
            var targetMs = Math.Clamp(timeMs, 0, length);
            var userRequestedStart = targetMs < 3000; // explicit seek to beginning

            // Cancel any pending verification from a previous seek; otherwise an older
            // task could fire after a newer scrub and "restore" the wrong position.
            _seekVerifyCts?.Cancel();
            var verifyCts = _seekVerifyCts = new CancellationTokenSource();
            var verifyCt = verifyCts.Token;

            // If the source is a local file (cached / prefetched), seeks always work —
            // skip the in-place attempt and the verification dance.
            bool isLocalSource = !string.IsNullOrEmpty(_lastLocalFilePath);

            // The current track may have started as a live stream while its cached
            // (downloaded + remuxed) copy finished in the background. Live streams scrub
            // unreliably, so if a ready cache now exists for this video, switch to it
            // seamlessly and resume at the scrub target — the user never notices because
            // playback is already interrupted by the scrub. Skip in audio-only mode: the
            // cache holds full video files and we must not introduce video mid-scrub.
            if (!isLocalSource)
            {
                var vmForCache = DataContext as JukeboxViewModel;
                bool isAudioOnly = _audioOnly || (vmForCache?.CurrentlyPlaying?.IsAudioOnly == true);
                var cached = !isAudioOnly && !string.IsNullOrEmpty(_lastPlayingVideoId)
                    ? vmForCache?.Cache?.TryGet(_lastPlayingVideoId!)
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
                _mediaPlayer.Time = targetMs;
            }

            // For local files we're done.
            if (isLocalSource) return;

            // For HTTP streams reported as not-seekable, there is no in-place recovery
            // path that works reliably. Restart from the beginning so the player ends
            // in a known good state.
            if (!seekable)
            {
                DebugLog.Log(LogLevel.Warning, "Seek", "IsSeekable=false — restarting playback from the beginning (use transient caching for reliable scrubbing)");

                var vm = DataContext as JukeboxViewModel;
                var videoIdToRestart = _lastPlayingVideoId;
                if (vm != null && !string.IsNullOrEmpty(videoIdToRestart))
                {
                    vm.SetStatusPrefix("Seek failed — restarted");
                    vm.PlaybackPosition = 0;
                    OnPlayRequested(videoIdToRestart);
                }
                return;
            }

            // VLC echoes back whatever we set to Time — it does NOT reflect where the
            // stream actually landed. Worse, on a wedged demuxer (e.g. forward seek to
            // a non-keyframe in a YouTube webm/vp9 stream), Time can stay frozen at the
            // value we wrote while playback has actually restarted from 0 (or stalled).
            //
            // Detection strategy: two complementary signals, whichever proves health first.
            //   1. BUFFERING activity. A healthy seek causes VLC to fire Buffering events
            //      almost immediately (0→100% as the new chunk loads). A wedged player
            //      fires nothing because VLC thinks it's still happily playing. We hook
            //      Buffering for the verification window and check if any tick arrived.
            //   2. TIME PROGRESS. A real playing decoder advances ~750ms in 750ms wall
            //      time; a wedged player reports the same number twice.
            //
            // Fast path: if we see buffering AND time advances within ~800ms, the seek
            // worked — exit early. Slow path: if neither signal appears by ~1500ms, the
            // seek wedged — restart playback from the beginning.
            //
            // Recovery: we tried multiple in-place strategies (Position retry, Play(newMedia)
            // on the same player, swapping to a fresh MediaPlayer with :start-time, force
            // Time= on the fresh player) — all failed for problem YouTube progressive
            // streams. The only reliable recovery is a full restart. Users who need
            // reliable scrubbing on long YouTube videos should enable transient caching
            // in settings — that downloads + remuxes the file, after which all seeks are
            // file-based and always work.
            const int fastCheckDelayMs = 800;   // by this point a healthy seek shows life
            const int finalCheckDelayMs = 700;  // additional wait if fast check inconclusive
            const long MinHealthyProgressMs = 100;

            // Subscribe to Buffering events so we can detect activity without polling.
            int bufferingTickCount = 0;
            void OnBufferingTick(object? s, LibVLCSharp.Shared.MediaPlayerBufferingEventArgs e)
            {
                System.Threading.Interlocked.Increment(ref bufferingTickCount);
            }
            _mediaPlayer.Buffering += OnBufferingTick;

            _ = Task.Run(async () =>
            {
                try
                {
                    // Fast check at ~800ms — most healthy seeks show life by now
                    await Task.Delay(fastCheckDelayMs, verifyCt);

                    long sample1 = 0;
                    await Dispatcher.InvokeAsync(() => { if (_mediaPlayer != null) sample1 = _mediaPlayer.Time; });

                    int bufferTicksFast = System.Threading.Interlocked.CompareExchange(ref bufferingTickCount, 0, 0);
                    bool sawBuffering = bufferTicksFast > 0;
                    bool nearTargetFast = Math.Abs(sample1 - targetMs) <= Math.Max(5000L, (long)(length * 0.02));

                    DebugLog.Log(LogLevel.Trace, "Seek", $"Fast check ({fastCheckDelayMs}ms): sample1={sample1} bufferTicks={bufferTicksFast} sawBuffering={sawBuffering} nearTargetFast={nearTargetFast}");

                    // Confirm with a second sample to ensure time is actually advancing.
                    await Task.Delay(finalCheckDelayMs, verifyCt);
                    long sample2 = 0;
                    await Dispatcher.InvokeAsync(() => { if (_mediaPlayer != null) sample2 = _mediaPlayer.Time; });

                    if (verifyCt.IsCancellationRequested) return;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (_mediaPlayer == null) return;

                        var nearTargetTolerance = Math.Max(5000L, (long)(length * 0.02));
                        long progress = sample2 - sample1;
                        bool advancing = progress >= MinHealthyProgressMs;
                        bool nearTarget = Math.Abs(sample2 - targetMs) <= nearTargetTolerance;

                        // Healthy if Time is at the right spot AND either we saw buffering
                        // activity OR Time is still advancing. Both signals are sufficient.
                        bool seekHealthy = nearTarget && (sawBuffering || advancing);

                        DebugLog.Log(LogLevel.Trace, "Seek", $"Verify: sample2={sample2} progress={progress}ms (was {timeBefore}, target {targetMs}) nearTarget={nearTarget} advancing={advancing} sawBuffering={sawBuffering} healthy={seekHealthy}");

                        if (seekHealthy) return;

                        // Wedge confirmed. Bail out cleanly: stop and restart from the
                        // beginning so the user has a known, controllable state.
                        DebugLog.Log(LogLevel.Warning, "Seek", "Seek failed — restarting playback from the beginning (use transient caching for reliable scrubbing)");

                        var vm = DataContext as JukeboxViewModel;
                        var videoIdToRestart = _lastPlayingVideoId;
                        if (vm == null || string.IsNullOrEmpty(videoIdToRestart)) return;

                        vm.SetStatusPrefix("Seek failed — restarted");
                        vm.PlaybackPosition = 0;

                        // Re-fire the play request via the normal code path. This rebuilds
                        // the stream from scratch and ends up in a fully healthy state.
                        OnPlayRequested(videoIdToRestart);
                    });
                }
                catch (OperationCanceledException) { /* superseded by a newer seek */ }
                catch (Exception ex)
                {
                    DebugLog.LogException("Seek/Verify", ex);
                }
                finally
                {
                    // Always detach our buffering probe — both on success and failure.
                    // Marshal to dispatcher since events live on it.
                    try
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (_mediaPlayer != null)
                                _mediaPlayer.Buffering -= OnBufferingTick;
                        });
                    }
                    catch { /* shutting down */ }
                }
            }, verifyCt);
        });
    }

    /// <summary>
    /// Seamlessly swaps the currently-playing live stream for its now-ready cached
    /// (downloaded + remuxed) local file, resuming at <paramref name="targetMs"/>.
    /// The cached .mkv has proper cue points, so all subsequent seeks are file-based
    /// and reliable. Uses ":start-time" so VLC begins decoding at the scrub target
    /// rather than replaying from the beginning.
    /// </summary>
    private void SwitchToCachedFileAndSeek(CachedVideo cached, long targetMs)
    {
        if (_mediaPlayer == null || _libVLC == null) return;

        // A cache switch supersedes any in-flight seek verification from the live stream.
        _seekVerifyCts?.Cancel();

        var vm = DataContext as JukeboxViewModel;

        try
        {
            var media = new Media(_libVLC, new Uri(cached.FilePath));

            // Begin decoding at the scrub target (seconds). Clamp so we never pass a
            // negative or absurd value if Length was briefly misreported.
            var startSeconds = Math.Max(0, targetMs) / 1000.0;
            media.AddOption($":start-time={startSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");

            // Preserve a paused scrub: if the user scrubbed while paused, re-apply pause
            // once VLC reaches the playing state (SetPause before then is ignored).
            bool wasPaused = vm?.IsPaused == true;
            if (wasPaused)
            {
                void OnPlayingReapplyPause(object? s, EventArgs a)
                {
                    _mediaPlayer.Playing -= OnPlayingReapplyPause;
                    _mediaPlayer.SetPause(true);
                }
                _mediaPlayer.Playing += OnPlayingReapplyPause;
            }

            _mediaPlayer.Play(media);

            // From now on this track is a local file — future seeks skip the live-stream
            // verification path and just set Time directly.
            _lastLocalFilePath = cached.FilePath;
            _lastVideoStreamUrl = null;
            _lastAudioStreamUrl = null;
            _lastMuxedStreamUrl = null;

            // The track is now served from the local cache — reflect that in the source label.
            vm?.SetCurrentFromCache(true);

            // Reflect the seek target immediately so the scrubber doesn't snap back.
            if (vm != null)
                vm.PlaybackPosition = targetMs;

            // Restore cached chapters if the playing item doesn't have them yet.
            if (cached.Chapters is { Count: > 0 } && vm?.CurrentlyPlaying is { } cp && cp.Chapters == null)
            {
                cp.Chapters = cached.Chapters;
                vm.NotifyCachedChaptersRestored();
            }

            StartVideoInfoPollingCached(cached.Resolution);
        }
        catch (Exception ex)
        {
            DebugLog.LogException("Seek/CacheSwitch", ex);
        }
    }

    private void OnStopRequested()
    {
        // Cancel any in-flight play operation so it doesn't resume after stop
        _playCts?.Cancel();
        // Cancel any pending seek verification / re-open
        _seekVerifyCts?.Cancel();

        Dispatcher.BeginInvoke(async () =>
        {
            _positionTimer?.Stop();
            _infoTimer?.Stop();
            // Cancel any pending delayed-overlay reveal from a transition
            _transitionOverlayTimer?.Stop();
            _transitionOverlayTimer = null;
            VideoInfoChanged?.Invoke("");

            // Stop PCM gapless player if active
            StopGaplessPlayer();

            // Detach the VideoView BEFORE stopping so the WinForms HWND is
            // removed from the visual tree first — this prevents VLC's video
            // output thread from waiting on UI-thread window messages while
            // Stop() blocks, which would cause a deadlock.
            DetachVideoView();
            DisposeGaplessNext();

            // Stop on a background thread to avoid blocking the dispatcher
            // (same pattern used in OnPlayRequested and other call sites).
            if (_mediaPlayer != null)
                await Task.Run(() => _mediaPlayer.Stop());

            ShowIdleBackground();
            _colorTimer.Start();
            ResetLogoDimIdle();
        });
    }

    private void OnMediaBuffering(object? sender, LibVLCSharp.Shared.MediaPlayerBufferingEventArgs e)
    {
        if (e.Cache >= 100f)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (DataContext is JukeboxViewModel vm && vm.PlayTransitioning)
                    vm.NotifyPlaybackStarted();
            });
        }
    }

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        if (_mediaPlayer == null) return;
        DebugLog.Log(LogLevel.Debug, "MediaEnded", $"EndReached fired | State={_mediaPlayer.State} Time={_mediaPlayer.Time} Length={_mediaPlayer.Length} Pos={_mediaPlayer.Position:F4}");
        Dispatcher.BeginInvoke(() =>
        {
            DebugLog.Log(LogLevel.Trace, "MediaEnded", "Processing on dispatcher");
            _positionTimer?.Stop();
            _infoTimer?.Stop();
            VideoInfoChanged?.Invoke("");

            // Live streams (e.g. SiriusXM) have no natural end — an EndReached means the stream
            // dropped. Don't auto-advance the queue; just log it (lean v1). Reconnect/robustness
            // is a later refinement.
            if (DataContext is JukeboxViewModel liveVm && liveVm.CurrentlyPlaying?.IsLiveStream == true)
            {
                DebugLog.Log(LogLevel.Info, "MediaEnded", "Live stream ended (dropped) — not auto-advancing.");
                return;
            }

            if (DataContext is JukeboxViewModel vm && vm.HasNextTrack)
            {
                // Gapless: if a next player is primed, swap it in immediately
                if (_nextMediaPlayer != null && _gaplessPrimed)
                {
                    DebugLog.Log(LogLevel.Debug, "Gapless", "Swapping to primed next player");

                    // Swap the player reference (engine rewires EndReached) and resume the pre-loaded track.
                    var oldPlayer = _engine.SwapMediaPlayer(_nextMediaPlayer);
                    _mediaPlayer!.SetPause(false);

                    _nextMediaPlayer = null;
                    _gaplessPrimed = false;
                    _nextGaplessVideoId = null;

                    // Stop and dispose the old player in the background
                    if (oldPlayer != null)
                    {
                        Task.Run(() => { try { oldPlayer.Stop(); oldPlayer.Dispose(); } catch { } });
                    }

                    // Advance the queue without triggering a new play request
                    vm.AdvanceQueueGapless();

                    // If the new track is audio-only (e.g. Plex music), the video
                    // surface from the previous track would just sit there black —
                    // detach it and show the idle overlay (logo + blobs). Mirrors
                    // the audio-only branch in OnPlayRequested.
                    bool nextIsAudioOnly = _audioOnly || (vm.CurrentlyPlaying?.IsAudioOnly == true);
                    if (nextIsAudioOnly)
                    {
                        DetachVideoView();
                        ShowIdleBackground();
                        _colorTimer.Start();
                    }

                    _positionTimer?.Start();

                    return;
                }

                // Next track available — keep video view attached to avoid idle screen flash.
                // OnPlayRequested will reuse or recreate it as needed.
                vm.PlayNext();
            }
            else
            {
                // Queue finished — show idle screen
                DisposeGaplessNext();
                DetachVideoView();
                ShowIdleBackground();
                _colorTimer.Start();
                ResetLogoDimIdle();
                if (DataContext is JukeboxViewModel vm2)
                    vm2.PlayNext(); // will set CurrentlyPlaying = null / "Queue finished"
            }
        });
    }

    public void SetShowVideoInfo(bool show)
    {
        _showVideoInfo = show;
        if (!show)
        {
            _infoTimer?.Stop();
        }
    }

    public void SetScreensaverSettings(double intensity, double speed)
    {
        double newIntensity = Math.Clamp(intensity, 0.05, 1.0);
        bool intensityChanged = Math.Abs(newIntensity - _blobIntensity) > 0.001;
        _blobIntensity = newIntensity;
        _blobSpeedMultiplier = Math.Clamp(speed, 0.1, 5.0);

        if (intensityChanged && _currentPattern != null)
        {
            foreach (var blob in _currentPattern.Blobs)
                blob.Opacity = Math.Min(1.0, _blobIntensity + _rng.NextDouble() * 0.1);
        }
    }

    /// <summary>
    /// Sets a brightness multiplier for the backglass screensaver.
    /// 1.0 = default, 1.1 = 10% brighter, etc.
    /// </summary>
    public void SetBrightnessBoost(double boost)
    {
        _brightnessBoost = Math.Clamp(boost, 0.5, 2.0);
    }

    public void SetReactiveAudio(AudioReactiveService? service)
    {
        if (_audioReactive != null)
            _audioReactive.Updated -= OnAudioUpdated;

        _audioReactive = service;

        if (_audioReactive != null)
            _audioReactive.Updated += OnAudioUpdated;
        else
            _currentPattern?.ResetAudioReactive(_blobIntensity);
    }

    private void OnAudioUpdated(AudioReactiveData data)
    {
        if (_currentPattern == null) return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnAudioUpdated(data));
            return;
        }

        _currentPattern.ApplyAudioReactive(data, _blobIntensity, _audioReactive?.ReactiveSpeedMs ?? 120);
        _reactiveHueBoost = data.Treble * 90.0;
    }

    private void StartVideoInfoPolling(string resolution)
    {
        if (!_showVideoInfo) return;

        VideoInfoChanged?.Invoke(resolution);

        _infoTimer?.Stop();
        int attempts = 0;
        _infoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _infoTimer.Tick += (_, _) =>
        {
            attempts++;
            if (_mediaPlayer == null) return;
            float fps = _mediaPlayer.Fps;
            if (fps > 0 || attempts >= 10)
            {
                var parts = new List<string> { resolution };
                if (fps > 0) parts.Add($"{fps:F1} fps");
                string codec = GetVideoCodec();
                if (!string.IsNullOrEmpty(codec)) parts.Add(codec);
                VideoInfoChanged?.Invoke(string.Join(" | ", parts));
                _infoTimer?.Stop();
            }
        };
        _infoTimer.Start();
    }

    private void StartVideoInfoPollingCached(string resolution)
    {
        if (!_showVideoInfo) return;

        VideoInfoChanged?.Invoke($"{resolution} | cached");

        _infoTimer?.Stop();
        int attempts = 0;
        _infoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _infoTimer.Tick += (_, _) =>
        {
            attempts++;
            if (_mediaPlayer == null) return;
            float fps = _mediaPlayer.Fps;
            if (fps > 0 || attempts >= 10)
            {
                var parts = new List<string> { resolution };
                if (fps > 0) parts.Add($"{fps:F1} fps");
                parts.Add("cached");
                VideoInfoChanged?.Invoke(string.Join(" | ", parts));
                _infoTimer?.Stop();
            }
        };
        _infoTimer.Start();
    }

    private string GetVideoCodec()
    {
        if (_mediaPlayer?.Media == null) return "";
        foreach (var track in _mediaPlayer.Media.Tracks)
        {
            if (track.TrackType == TrackType.Video && track.Codec > 0)
            {
                return new string(new[]
                {
                    (char)(track.Codec & 0xFF),
                    (char)((track.Codec >> 8) & 0xFF),
                    (char)((track.Codec >> 16) & 0xFF),
                    (char)((track.Codec >> 24) & 0xFF)
                }).Trim('\0').ToUpperInvariant();
            }
        }
        return "";
    }

    private void ToggleExpand_Click(object sender, RoutedEventArgs e)
    {
        ToggleExpand();
    }

    private void MoveDisplay_Click(object sender, RoutedEventArgs e)
    {
        MoveToNextDisplay();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Detach VLC events first to prevent callbacks during teardown
        if (_mediaPlayer != null)
            _mediaPlayer.EndReached -= OnMediaEnded;
        DisposeGaplessNext();
        DisposeAmbientVlc();
        _gaplessPlayer?.Dispose();
        _gaplessPlayer = null;
        _colorTimer.Stop();
        _positionTimer?.Stop();
        _infoTimer?.Stop();

        // Stop and dispose the MediaPlayer on a background thread to avoid
        // deadlocking the UI thread (VLC's EndReached fires on a VLC thread
        // and may be waiting for Dispatcher access while Stop() blocks here).
        // The shared LibVLC instance is owned by the App and disposed at exit.
        var mp = _mediaPlayer;
        if (mp != null)
        {
            Task.Run(() =>
            {
                try { mp.Stop(); } catch { }
                mp.Dispose();
            }).Wait(TimeSpan.FromSeconds(5));
        }

        base.OnClosed(e);
    }

    private BlobPatternConfig MakeConfig()
    {
        double w = Math.Max(200, IdleCanvas.ActualWidth);
        double h = Math.Max(200, IdleCanvas.ActualHeight);
        double recordRadius = Math.Min(w, h) * 0.45;

        return new BlobPatternConfig
        {
            Canvas = IdleCanvas,
            BlobCount = _blobCount,
            Intensity = _blobIntensity,
            SpeedMultiplier = _blobSpeedMultiplier,
            Rng = _rng,
            BlobSizeFactory = r => 220 + r.NextDouble() * 280,
            BlobSizeOffset = _blobSizeOffset,
            MaxOrbitRadius = recordRadius,
            UseBitmapCache = false,
        };
    }

    private void StartIdleAnimation()
    {
        if (!_idleAnimStarted)
        {
            _idleAnimStarted = true;

            IdleCanvas.SizeChanged -= OnIdleCanvasSizeChanged;
            IdleCanvas.SizeChanged += OnIdleCanvasSizeChanged;

            _currentPattern = BlobTransition.Create(_blobPattern, MakeConfig());
            _currentPattern.Enter(() => { });
            // Record whether this was built against a real canvas. If the app started
            // in a media/Pinup ambient mode the IdleCanvas is collapsed (0-size), so the
            // pattern is clustered and must be rebuilt when Screensaver mode is entered.
            _idlePatternLaidOut = IdleCanvas.ActualWidth >= 1 && IdleCanvas.ActualHeight >= 1;
        }

        _colorTimer.Start();

        DrawRecordOverlay(RecordOverlay, _logoRings);
        DrawCircularTitle(TitleCanvas, _logoSpin);
    }

    /// <summary>
    /// When the IdleCanvas is resized (e.g. the window is dragged to a new size), the blob
    /// pattern needs to be rebuilt so its elements re-center against the new dimensions.
    /// The logo/record overlays recenter continuously via their own SizeChanged handlers,
    /// but rebuilding the whole pattern on every intermediate resize event would be wasteful,
    /// so the rebuild is debounced until the resize settles.
    /// </summary>
    private void OnIdleCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_idleAnimStarted || IdleCanvas.ActualWidth < 1 || IdleCanvas.ActualHeight < 1)
            return;

        // Self-rendering patterns handle their own resize via canvas SizeChanged.
        if (_blobPattern is BlobPattern.ProjectM or BlobPattern.Mandelbrot)
            return;

        _resizeDebounceTimer.Stop();
        _resizeDebounceTimer.Start();
    }

    private void OnResizeDebounceTick(object? sender, EventArgs e)
    {
        _resizeDebounceTimer.Stop();

        if (!_idleAnimStarted || IdleCanvas.ActualWidth < 1 || IdleCanvas.ActualHeight < 1)
            return;

        if (_blobPattern is BlobPattern.ProjectM or BlobPattern.Mandelbrot)
            return;

        _currentPattern?.Dispose();
        _currentPattern = BlobTransition.Create(_blobPattern, MakeConfig());
        _currentPattern.Enter(() => { });
        _idlePatternLaidOut = true;
    }

    /// <summary>
    /// Rebuilds the idle blob pattern against the current IdleCanvas size, but ONLY when the
    /// current pattern was created against a 0-size canvas (e.g. the app started in a media/
    /// Pinup ambient mode, which clusters the blobs in the top-left corner). When the pattern
    /// was already laid out at a valid size — the normal case when returning from a video —
    /// nothing is rebuilt, so the running pattern reappears seamlessly (continuity). If the
    /// canvas isn't laid out yet, the check/rebuild is deferred to Loaded priority.
    /// </summary>
    private void RestartIdleBlobs()
    {
        if (!_idleAnimStarted || !IsVisible || _idlePatternLaidOut)
            return;

        void Rebuild()
        {
            if (_ambientMode != PlayfieldMode.Screensaver || !IsVisible || _idlePatternLaidOut)
                return;
            if (IdleCanvas.ActualWidth < 1 || IdleCanvas.ActualHeight < 1)
                return; // still no valid size — leave for a later entry
            _currentPattern?.Dispose();
            _currentPattern = BlobTransition.Create(_blobPattern, MakeConfig());
            _currentPattern.Enter(() => { });
            _idlePatternLaidOut = true;
            _colorTimer.Start();
        }

        if (IdleCanvas.ActualWidth >= 1 && IdleCanvas.ActualHeight >= 1)
            Rebuild();
        else
            Dispatcher.BeginInvoke(new Action(Rebuild), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Suspends the idle blob screensaver's render loop while it is hidden behind a jukebox
    /// video, freeing CPU/GPU (self-rendering patterns like Game of Life / ProjectM keep
    /// burning cycles otherwise). Visual state is preserved so <see cref="ResumeIdleBlobs"/>
    /// continues seamlessly. Also stops the color-cycle timer. No-op for patterns that
    /// don't implement <see cref="IPausable"/>'s pausing (they keep running, which is cheap).
    /// </summary>
    private void PauseIdleBlobs()
    {
        if (_currentPattern is IPausable p && !p.IsPaused)
        {
            p.Pause();
            _colorTimer.Stop();
        }
    }

    /// <summary>
    /// Resumes a previously-paused idle blob screensaver from its frozen state (no rebuild,
    /// no fly-in). Restarts the color-cycle timer. Safe to call when nothing was paused.
    /// </summary>
    private void ResumeIdleBlobs()
    {
        if (_currentPattern is IPausable p && p.IsPaused)
        {
            p.Resume();
            _colorTimer.Start();
        }
    }

    /// <summary>
    /// Pauses only the idle blob screensaver when the window is hidden so its
    /// self-rendering render loop (Game of Life, ProjectM) stops consuming
    /// CPU/GPU. Any playing video/audio is intentionally left running — a user
    /// may want to hear the backglass media without seeing it. Resumes the
    /// screensaver on show, but only if the window is currently in idle mode
    /// (IdleOverlay visible), not mid-video.
    /// </summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not bool visible || !_idleAnimStarted)
            return;

        if (visible)
        {
            if (IdleOverlay.Visibility == Visibility.Visible)
            {
                if (_currentPattern == null)
                {
                    _currentPattern = BlobTransition.Create(_blobPattern, MakeConfig());
                    _currentPattern.Enter(() => { });
                    _idlePatternLaidOut = IdleCanvas.ActualWidth >= 1 && IdleCanvas.ActualHeight >= 1;
                }
                _colorTimer.Start();
            }
            // Resume ambient video playback (deferred while hidden).
            if (AmbientIsVideoMode && !_jukeboxVideoActive)
                RefreshAmbient();
        }
        else
        {
            _colorTimer.Stop();
            _currentPattern?.Dispose();
            _currentPattern = null;
            // Pause ambient video so a looping clip doesn't keep decoding while hidden.
            if (AmbientIsVideoMode)
                PauseAmbientVideo();
        }
    }

    /// <summary>
    /// Shows the idle background: the blob/logo IdleOverlay when the ambient mode is
    /// Screensaver, otherwise the ambient content layer (image/video/folders/pinup).
    /// Called wherever playback stops or an audio-only track starts. Also tells the
    /// ambient engine that no jukebox video is on screen so it can (re)start.
    /// </summary>
    private void ShowIdleBackground()
    {
        // No jukebox video is on screen now — let ambient content take over.
        SetJukeboxVideoActive(false);
        // Screensaver mode keeps the classic blob/logo overlay visible; other ambient
        // modes hide it so the ambient layer (beneath) shows through.
        IdleOverlay.Visibility = AmbientReplacesIdleOverlay
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>
    /// Hides all idle background content because a jukebox video track is now on
    /// screen (paramount layer). Collapses the IdleOverlay and pauses/hides ambient.
    /// </summary>
    private void HideIdleForJukeboxVideo()
    {
        IdleOverlay.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
        SetJukeboxVideoActive(true);
    }

    public void SetLogoSpin(bool spin)
    {
        _logoSpin = spin;
        if (_idleAnimStarted)
        {
            DrawCircularTitle(TitleCanvas, _logoSpin);
        }
    }

    public void SetLogoShadow(bool enabled)
    {
        if (_logoShadow == enabled) return;
        _logoShadow = enabled;
        if (_idleAnimStarted)
            DrawCircularTitle(TitleCanvas, _logoSpin);
    }

    public void SetLogoRings(LogoRingsMode mode)
    {
        _logoRings = mode;
        if (_idleAnimStarted)
        {
            DrawRecordOverlay(RecordOverlay, _logoRings);
        }
    }

    public void SetLogoRingsBrightness(int percent)
    {
        _recordRingsBrightness = Math.Clamp(percent / 100.0, 0.0, 1.0);
        if (_idleAnimStarted)
        {
            DrawRecordOverlay(RecordOverlay, _logoRings);
        }
    }

    public void SetLogoBrightness(int percent)
    {
        _logoBrightness = Math.Clamp(percent / 100.0, 0.0, 1.0);
        if (!_isLogoDimmed)
        {
            TitleCanvas.Opacity = _logoBrightness;
        }
    }

    public void SetLogoBehindVisuals(bool behind)
    {
        int blobZ = behind ? 2 : 0;
        int recordZ = behind ? 0 : 1;
        int titleZ = behind ? 1 : 2;
        System.Windows.Controls.Panel.SetZIndex(IdleCanvas, blobZ);
        System.Windows.Controls.Panel.SetZIndex(RecordOverlay, recordZ);
        System.Windows.Controls.Panel.SetZIndex(TitleCanvas, titleZ);
    }

    public void SetBlobPattern(BlobPattern pattern)
    {
        _transitioning = false;
        _blobPatternSetting = pattern;

        if (pattern == BlobPattern.RandomPerSong)
            pattern = BlobTransition.CurrentRandomPattern;

        _blobPattern = pattern;

        // If the canvas isn't laid out yet
        // StartIdleAnimation will create the blobs once Loaded fires.
        if (IdleCanvas.ActualWidth < 1 || IdleCanvas.ActualHeight < 1)
            return;

        _currentPattern?.Dispose();
        _currentPattern = BlobTransition.Create(pattern, MakeConfig());
        _currentPattern.Enter(() => { });
        _idleAnimStarted = true;
        _idlePatternLaidOut = true;
    }

    /// <summary>
    /// Restarts the current pattern if it is Mandelbrot, so that changed static settings take effect.
    /// </summary>
    public void RestartMandelbrot()
    {
        if (_blobPattern == BlobPattern.Mandelbrot)
            SetBlobPattern(_blobPatternSetting);
    }

    /// <summary>
    /// Restarts the current pattern if it is ProjectM, so that changed static settings take effect.
    /// </summary>
    public void RestartProjectM()
    {
        if (_blobPattern == BlobPattern.ProjectM)
            SetBlobPattern(_blobPatternSetting);
    }

    /// <summary>
    /// Restarts the current pattern if it is Game of Life, so that changed static settings take effect.
    /// </summary>
    public void RestartGameOfLife()
    {
        if (_blobPattern == BlobPattern.GameOfLife)
            SetBlobPattern(_blobPatternSetting);
    }

    /// <summary>
    /// Soft-resets the Game of Life simulation in place with a blur-out / blur-in
    /// transition. Used for track-change resets, where the visible cell size and
    /// bitmap dimensions haven't changed and we just want a fresh seed under a
    /// smooth crossfade instead of tearing down and rebuilding the pattern.
    /// </summary>
    public void RestartGameOfLifeWithBlurTransition()
    {
        if (_blobPattern == BlobPattern.GameOfLife && _currentPattern is GameOfLifePattern gol)
            gol.RestartWithBlurTransition();
    }

    /// <summary>
    /// Restarts the current pattern if it is Gravity, so that a fresh simulation begins.
    /// </summary>
    public void RestartGravity()
    {
        if (_blobPattern == BlobPattern.Gravity)
            SetBlobPattern(_blobPatternSetting);
    }

    /// <summary>
    /// Restarts the current pattern if it is Clock, so that changed tuning takes effect.
    /// </summary>
    public void RestartClock()
    {
        if (_blobPattern == BlobPattern.Clock)
            SetBlobPattern(_blobPatternSetting);
    }

    public void ApplyProjectMTuning()
    {
        if (_blobPattern == BlobPattern.ProjectM && _currentPattern is ProjectMPattern pm)
            pm.ApplyTuningSettings();
    }

    /// <summary>
    /// If the pattern is RandomPerSong, smoothly transition to a new random pattern.
    /// </summary>
    public void OnSongChanged()
    {
        if (_blobPatternSetting != BlobPattern.RandomPerSong || _transitioning || _currentPattern == null)
            return;

        _transitioning = true;

        _currentPattern.Exit(() =>
        {
            var newPattern = BlobTransition.CurrentRandomPattern;
            DebugLog.Log(LogLevel.Trace, "Backglass", $"Transition {_blobPattern} -> {newPattern} blob pattern");
            _blobPattern = newPattern;

            _currentPattern?.Dispose();
            _currentPattern = BlobTransition.Create(newPattern, MakeConfig());
            _currentPattern.Enter(() =>
            {
                _transitioning = false;
            });
        });
    }

    public void SetBlobCount(int count)
    {
        _blobCount = Math.Clamp(count, 0, 100);
        if (!_idleAnimStarted) return;

        _currentPattern?.Dispose();
        _currentPattern = BlobTransition.Create(_blobPattern, MakeConfig());
        _currentPattern.Enter(() => { });
    }

    public void SetBlobSizeOffset(int offset)
    {
        int clamped = Math.Clamp(offset, 1, 20);
        bool changed = clamped != _blobSizeOffset;
        _blobSizeOffset = clamped;
        if (!_idleAnimStarted || !changed) return;

        _currentPattern?.Dispose();
        _currentPattern = BlobTransition.Create(_blobPattern, MakeConfig());
        _currentPattern.Enter(() => { });
    }

    public void SetLogoDim(bool enabled, int opacityPercent, int timeoutSeconds)
    {
        _logoDimEnabled = enabled;
        _logoDimOpacity = Math.Clamp(opacityPercent / 100.0, 0.0, 1.0);
        _logoDimTimer.Stop();

        if (enabled)
        {
            _logoDimTimer.Interval = TimeSpan.FromSeconds(Math.Max(10, timeoutSeconds));
            if (IdleOverlay.Visibility == Visibility.Visible)
                _logoDimTimer.Start();
        }
        else
        {
            UndimLogo();
        }
    }

    private void LogoDimTimer_Tick(object? sender, EventArgs e)
    {
        _logoDimTimer.Stop();
        if (!_logoDimEnabled || _isLogoDimmed) return;

        _isLogoDimmed = true;

        // Fade logo elements to target opacity over 1 second
        double textTarget = _logoBrightness * _logoDimOpacity;
        double ringsTarget = _logoDimOpacity;
        RecordOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = ringsTarget,
            Duration = TimeSpan.FromSeconds(1),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        });
        TitleCanvas.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = textTarget,
            Duration = TimeSpan.FromSeconds(1),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    private void ResetLogoDimIdle()
    {
        if (_isLogoDimmed)
            UndimLogo();

        if (_logoDimEnabled)
        {
            _logoDimTimer.Stop();
            _logoDimTimer.Start();
        }
    }

    private void UndimLogo()
    {
        _isLogoDimmed = false;
        RecordOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = 1.0,
            Duration = TimeSpan.FromSeconds(0.5),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        });
        TitleCanvas.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = _logoBrightness,
            Duration = TimeSpan.FromSeconds(0.5),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    public void SetLogoMorphColor(LogoColorMode mode)
    {
        _logoMorphEnabled = mode == LogoColorMode.SlowMorph;
        _morphColors = mode != LogoColorMode.Off;
        _morphTimer.Stop();

        // Redraw with non-frozen (or frozen) brushes as appropriate
        if (_idleAnimStarted)
        {
            DrawRecordOverlay(RecordOverlay, _logoRings);
            DrawCircularTitle(TitleCanvas, _logoSpin);
        }

        if (mode == LogoColorMode.SlowMorph)
        {
            ScheduleNextMorph();
        }
        else if (mode == LogoColorMode.Off)
        {
            ResetLogoColors();
            LogoColorsReset?.Invoke();
        }
        // Reactive mode: colors are driven externally via MorphLogoToColor
    }

    /// <summary>
    /// Morphs the logo to the hue corresponding to the given ROYGBIV color band.
    /// Used by reactive logo mode when the dominant playfield color changes.
    /// </summary>
    public void MorphLogoToColor(RoygbivColor color)
    {
        if (!_morphColors) return;

        // PROFILING: capture UI-thread time and GC pressure for the morph call.
        // Hypothesis under test: visible logo hitching is caused by a brief
        // UI-thread stall (per-brush ColorAnimation construction + 50+ brush
        // BeginAnimation calls + transient allocations) rather than dropped
        // presents. Remove once investigation is complete.
#if DEBUG
        var __sw = System.Diagnostics.Stopwatch.StartNew();
        int __gc0Before = GC.CollectionCount(0);
        int __gc1Before = GC.CollectionCount(1);
        int __gc2Before = GC.CollectionCount(2);
#endif

        double hue = color switch
        {
            RoygbivColor.Red => 0,
            RoygbivColor.Orange => 30,
            RoygbivColor.Yellow => 60,
            RoygbivColor.Green => 120,
            RoygbivColor.Blue => 210,
            RoygbivColor.Indigo => 240,
            RoygbivColor.Violet => 280,
            RoygbivColor.White => 200,
            _ => 0
        };

        var titleColor = ColorHelper.HsvToColor(hue, 0.8, 0.75);
        var recordColor = ColorHelper.HsvToColor((hue + 30) % 360, 0.75, 0.7);

        var duration = TimeSpan.FromSeconds(2);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };

        // CacheMode is intentionally left in place during the morph. Toggling it
        // off and back on caused visible hitches at the animation boundaries
        // (cache release + later re-rasterization). Leaving BitmapCache active
        // means WPF re-rasterizes the subtree per frame during the ~2s animation,
        // which is negligible for the small logo subtree.

        // Strip composition state during the morph to avoid per-frame blur re-raster
        // (mirrors TopperWindow so both logos morph in lockstep).
        SuspendCompositionForMorph(duration);

        // Animate the single shared title brush once (all glyphs reference it).
        int __titleAnims = 0, __recordAnims = 0;
        if (_titleBrush is { IsFrozen: false } titleBrush)
        {
            var titleAnim = new ColorAnimation
            {
                To = WpfColor.FromArgb(180, titleColor.R, titleColor.G, titleColor.B),
                Duration = duration,
                EasingFunction = ease
            };
            titleBrush.BeginAnimation(WpfMedia.SolidColorBrush.ColorProperty, titleAnim);
            __titleAnims++;
        }

        foreach (var child in RecordOverlay.Children)
        {
            if (child is Ellipse ellipse)
            {
                if (ellipse.Fill is WpfMedia.SolidColorBrush fill && !fill.IsFrozen)
                {
                    byte alpha = fill.Color.A;
                    if (alpha > 0)
                    {
                        var anim = new ColorAnimation
                        {
                            To = WpfColor.FromArgb(alpha, recordColor.R, recordColor.G, recordColor.B),
                            Duration = duration,
                            EasingFunction = ease
                        };
                        fill.BeginAnimation(WpfMedia.SolidColorBrush.ColorProperty, anim);
                        __recordAnims++;
                    }
                }
                if (ellipse.Stroke is WpfMedia.SolidColorBrush stroke && !stroke.IsFrozen)
                {
                    byte alpha = stroke.Color.A;
                    var anim = new ColorAnimation
                    {
                        To = WpfColor.FromArgb(alpha, recordColor.R, recordColor.G, recordColor.B),
                        Duration = duration,
                        EasingFunction = ease
                    };
                    stroke.BeginAnimation(WpfMedia.SolidColorBrush.ColorProperty, anim);
                    __recordAnims++;
                }
            }
        }

        LogoColorsMorphed?.Invoke(titleColor, recordColor);

        // PROFILING
#if DEBUG
        __sw.Stop();
        int __gc0 = GC.CollectionCount(0) - __gc0Before;
        int __gc1 = GC.CollectionCount(1) - __gc1Before;
        int __gc2 = GC.CollectionCount(2) - __gc2Before;
        DebugLog.Log(LogLevel.Trace, "PERF.LogoMorph",
            $"MorphLogoToColor color={color} elapsedMs={__sw.Elapsed.TotalMilliseconds:F2} " +
            $"titleAnims={__titleAnims} recordAnims={__recordAnims} shadow={_logoShadow} " +
            $"gc0={__gc0} gc1={__gc1} gc2={__gc2}");
#endif
    }

    // ── PROFILING: Backglass dispatcher stall monitor ───────────────────
    //
    // Hooks CompositionTarget.Rendering on the backglass UI thread. Each
    // render tick (~14 ms at 72 FPS on a 144 Hz display) we measure the
    // gap since the previous tick. A gap materially larger than the vsync
    // interval indicates the backglass dispatcher was blocked — either by
    // a long handler on the dispatcher itself, a blocking proxy call from
    // the main thread, VLC interop, or a layout/measure storm. While
    // blocked, animation values stop interpolating, which manifests as
    // the visible "freeze, then catch up" symptom on slow morphs.
    //
    // Debug-only: CompositionTarget.Rendering fires every vsync on the UI
    // thread, so we exclude this entire block from Release builds.
#if DEBUG
    private DateTime _lastRenderTick = DateTime.MinValue;
    private const double StallThresholdMs = 30.0;

    private void StartBackglassStallMonitor()
    {
        WpfMedia.CompositionTarget.Rendering += OnBackglassRenderTick;
    }

    private void OnBackglassRenderTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        if (_lastRenderTick != DateTime.MinValue)
        {
            double gapMs = (now - _lastRenderTick).TotalMilliseconds;
            if (gapMs > StallThresholdMs)
            {
                DebugLog.Log(LogLevel.Warning, "PERF.BackglassStall",
                    $"render gap {gapMs:F1}ms (threshold {StallThresholdMs:F0}ms)");
            }
        }
        _lastRenderTick = now;
    }
#endif

    private void ScheduleNextMorph()
    {
        _morphTimer.Interval = TimeSpan.FromSeconds(20 + _rng.NextDouble() * 20);
        _morphTimer.Start();
    }

    private void MorphTimer_Tick(object? sender, EventArgs e)
    {
        _morphTimer.Stop();
        if (!_logoMorphEnabled) return;

        MorphLogoColors();
        ScheduleNextMorph();
    }

    private void MorphLogoColors()
    {
#if DEBUG
        var __sw = System.Diagnostics.Stopwatch.StartNew();
#endif
        double titleHue = _rng.NextDouble() * 360;
        double recordHue = _rng.NextDouble() * 360;
        var titleColor = ColorHelper.HsvToColor(titleHue, 0.8, 0.75);
        var recordColor = ColorHelper.HsvToColor(recordHue, 0.75, 0.7);

        var duration = TimeSpan.FromSeconds(1);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        int __titleAnims = 0, __recordAnims = 0;

        // Strip composition state during the morph to avoid per-frame blur re-raster
        // (mirrors TopperWindow so both logos morph in lockstep).
        SuspendCompositionForMorph(duration);

        // Animate the single shared title brush once (all glyphs reference it).
        if (_titleBrush is { IsFrozen: false } titleBrush)
        {
            var titleAnim = new ColorAnimation
            {
                To = WpfColor.FromArgb(180, titleColor.R, titleColor.G, titleColor.B),
                Duration = duration,
                EasingFunction = ease
            };
            titleBrush.BeginAnimation(WpfMedia.SolidColorBrush.ColorProperty, titleAnim);
            __titleAnims++;
        }

        // Animate record overlay brushes (preserve original alpha)
        foreach (var child in RecordOverlay.Children)
        {
            if (child is Ellipse ellipse)
            {
                if (ellipse.Fill is WpfMedia.SolidColorBrush fill && !fill.IsFrozen)
                {
                    byte alpha = fill.Color.A;
                    if (alpha > 0)
                    {
                        var anim = new ColorAnimation
                        {
                            To = WpfColor.FromArgb(alpha, recordColor.R, recordColor.G, recordColor.B),
                            Duration = duration,
                            EasingFunction = ease
                        };
                        fill.BeginAnimation(WpfMedia.SolidColorBrush.ColorProperty, anim);
                        __recordAnims++;
                    }
                }
                if (ellipse.Stroke is WpfMedia.SolidColorBrush stroke && !stroke.IsFrozen)
                {
                    byte alpha = stroke.Color.A;
                    var anim = new ColorAnimation
                    {
                        To = WpfColor.FromArgb(alpha, recordColor.R, recordColor.G, recordColor.B),
                        Duration = duration,
                        EasingFunction = ease
                    };
                    stroke.BeginAnimation(WpfMedia.SolidColorBrush.ColorProperty, anim);
                    __recordAnims++;
                }
            }
        }

        LogoColorsMorphed?.Invoke(titleColor, recordColor);
#if DEBUG
        __sw.Stop();
        DebugLog.Log(LogLevel.Trace, "PERF.LogoMorph",
            $"MorphLogoColors elapsedMs={__sw.Elapsed.TotalMilliseconds:F2} " +
            $"titleAnims={__titleAnims} recordAnims={__recordAnims} shadow={_logoShadow}");
#endif
    }

    private void ResetLogoColors()
    {
        var duration = TimeSpan.FromSeconds(2);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        var defaultTitle = WpfColor.FromArgb(180, 0x88, 0xCC, 0xFF);

        // Animate the single shared title brush once (all glyphs reference it).
        if (_titleBrush is { IsFrozen: false } titleBrush)
        {
            var titleAnim = new ColorAnimation { To = defaultTitle, Duration = duration, EasingFunction = ease };
            titleBrush.BeginAnimation(WpfMedia.SolidColorBrush.ColorProperty, titleAnim);
        }

        foreach (var child in RecordOverlay.Children)
        {
            if (child is Ellipse ellipse)
            {
                if (ellipse.Fill is WpfMedia.SolidColorBrush fill && !fill.IsFrozen)
                {
                    byte alpha = fill.Color.A;
                    if (alpha > 0)
                    {
                        var anim = new ColorAnimation { To = WpfColor.FromArgb(alpha, 255, 255, 255), Duration = duration, EasingFunction = ease };
                        fill.BeginAnimation(WpfMedia.SolidColorBrush.ColorProperty, anim);
                    }
                }
                if (ellipse.Stroke is WpfMedia.SolidColorBrush stroke && !stroke.IsFrozen)
                {
                    byte alpha = stroke.Color.A;
                    var anim = new ColorAnimation { To = WpfColor.FromArgb(alpha, 255, 255, 255), Duration = duration, EasingFunction = ease };
                    stroke.BeginAnimation(WpfMedia.SolidColorBrush.ColorProperty, anim);
                }
            }
        }
    }

    private void ColorCycleBlobs(object? sender, EventArgs e)
    {
        // The Gravity pattern owns its own color (physics-driven hues + slow hue
        // drift in the simulator), so don't overwrite it here.
        if (_currentPattern?.PatternType == BlobPattern.Gravity) return;

        var brushes = _currentPattern?.Brushes;
        var gradBrushes = _currentPattern?.GradientBrushes;
        if (brushes == null || brushes.Count == 0) return;

        _hueOffset += 1.0;
        double value = Math.Clamp((0.15 + _blobIntensity * 0.85) * _brightnessBoost, 0.0, 1.0);
        for (int i = 0; i < brushes.Count; i++)
        {
            double hue = (_hueOffset + _reactiveHueBoost + i * 60.0) % 360.0;
            var color = ColorHelper.HsvToColor(hue, 0.9, value);
            brushes[i].Color = color;

            if (gradBrushes != null && i < gradBrushes.Count)
            {
                var stops = gradBrushes[i].GradientStops;
                if (stops.Count >= 2)
                {
                    stops[0].Color = WpfColor.FromArgb(255, color.R, color.G, color.B);
                    stops[1].Color = WpfColor.FromArgb(120, color.R, color.G, color.B);
                }
            }
        }
    }

    private static LogoRingsMode _recordRingsMode = LogoRingsMode.Standard;
    private static double _recordRingsBrightness = 1.0;

    private static void DrawRecordOverlay(System.Windows.Controls.Canvas canvas, LogoRingsMode ringsMode)
    {
        _recordRingsMode = ringsMode;
        canvas.Children.Clear();
        canvas.CacheMode = null;
        canvas.SizeChanged -= OnRecordCanvasSizeChanged;
        canvas.SizeChanged += OnRecordCanvasSizeChanged;
        RedrawRecord(canvas);
    }

    private static void OnRecordCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.Canvas c)
            RedrawRecord(c);
    }

    private static void RedrawRecord(System.Windows.Controls.Canvas canvas)
    {
        canvas.Children.Clear();
        double w = canvas.ActualWidth;
        double h = canvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double cx = w / 2;
        double cy = h / 2;
        double maxR = Math.Min(w, h) * 0.45;

        double holeR = maxR * 0.04;
        double labelR = maxR * 0.22;
        double grooveStart = maxR * 0.28;

        double b = _recordRingsBrightness * 4.0;

        // Center hole
        var hole = new Ellipse
        {
            Width = holeR * 2, Height = holeR * 2,
            Fill = new WpfMedia.SolidColorBrush(WpfColor.FromArgb((byte)Math.Clamp(18 * b, 0, 255), 255, 255, 255)),
        };
        System.Windows.Controls.Canvas.SetLeft(hole, cx - holeR);
        System.Windows.Controls.Canvas.SetTop(hole, cy - holeR);
        canvas.Children.Add(hole);

        // Label area (solid subtle disc)
        var label = new Ellipse
        {
            Width = labelR * 2, Height = labelR * 2,
            Stroke = new WpfMedia.SolidColorBrush(WpfColor.FromArgb((byte)Math.Clamp(10 * b, 0, 255), 255, 255, 255)),
            StrokeThickness = 1,
            Fill = new WpfMedia.SolidColorBrush(WpfColor.FromArgb((byte)Math.Clamp(6 * b, 0, 255), 255, 255, 255)),
        };
        System.Windows.Controls.Canvas.SetLeft(label, cx - labelR);
        System.Windows.Controls.Canvas.SetTop(label, cy - labelR);
        canvas.Children.Add(label);

        // Concentric groove rings
        if (_recordRingsMode != LogoRingsMode.Off)
        {
            double spacing = _recordRingsMode == LogoRingsMode.Reduced ? 12.0 : 4.0;
            for (double r = grooveStart; r <= maxR; r += spacing)
            {
                byte alpha = (byte)Math.Clamp((5 + (r - grooveStart) / (maxR - grooveStart) * 8) * b, 0, 255);
                var ring = new Ellipse
                {
                    Width = r * 2, Height = r * 2,
                    Stroke = new WpfMedia.SolidColorBrush(WpfColor.FromArgb(alpha, 255, 255, 255)),
                    StrokeThickness = 0.5,
                    Fill = WpfMedia.Brushes.Transparent,
                };
                System.Windows.Controls.Canvas.SetLeft(ring, cx - r);
                System.Windows.Controls.Canvas.SetTop(ring, cy - r);
                canvas.Children.Add(ring);
            }
        }

        // Outer rim
        var rim = new Ellipse
        {
            Width = maxR * 2, Height = maxR * 2,
            Stroke = new WpfMedia.SolidColorBrush(WpfColor.FromArgb((byte)Math.Clamp(15 * b, 0, 255), 255, 255, 255)),
            StrokeThickness = 1.5,
            Fill = WpfMedia.Brushes.Transparent,
        };
        System.Windows.Controls.Canvas.SetLeft(rim, cx - maxR);
        System.Windows.Controls.Canvas.SetTop(rim, cy - maxR);
        canvas.Children.Add(rim);

        canvas.CacheMode = new WpfMedia.BitmapCache(1.0);
    }

    private static bool _titleSpin = true;
    private static bool _morphColors;
    private static bool _logoShadow = false;
    private static string _logoText = "\u2022 VPIN JUKEBOX \u2022 VPIN JUKEBOX ";
    private static System.Windows.Controls.Canvas? _titleInnerCanvas;
    // Shared brush used by all title glyphs — captured so morphs animate it once
    // instead of redundantly per glyph (all 28 TextBlocks share this instance).
    private static WpfMedia.SolidColorBrush? _titleBrush;
    // Morph composition-state suspension (mirrors TopperWindow.RunMorph so the two
    // windows stay in lockstep). While a morph runs, the DropShadowEffect + BitmapCache
    // on the title inner canvas (and the record overlay cache) are stripped so the
    // render thread doesn't re-rasterize the blurred cached surface every frame — the
    // main source of morph hitching. Ref-counted so overlapping morphs don't restore early.
    private int _activeMorphs;
    private WpfMedia.Effects.Effect? _savedTitleEffect;
    private WpfMedia.CacheMode? _savedTitleCache;
    private WpfMedia.CacheMode? _savedRecordCache;

    /// <summary>
    /// Strips the expensive composition state (drop shadow + bitmap caches) for the
    /// duration of a color morph, then restores it. Mirrors TopperWindow.RunMorph exactly
    /// (immediate strip, ref-counted, restore at exactly <paramref name="duration"/>) so
    /// the backglass and topper logos morph in visual lockstep.
    /// </summary>
    private void SuspendCompositionForMorph(TimeSpan duration)
    {
        if (_activeMorphs == 0)
        {
            if (_titleInnerCanvas != null)
            {
                _savedTitleEffect = _titleInnerCanvas.Effect;
                _savedTitleCache = _titleInnerCanvas.CacheMode;
                _titleInnerCanvas.Effect = null;
                _titleInnerCanvas.CacheMode = null;
            }
            _savedRecordCache = RecordOverlay.CacheMode;
            RecordOverlay.CacheMode = null;
        }
        _activeMorphs++;

        // Schedule restore. A one-shot DispatcherTimer avoids relying on
        // ColorAnimation.Completed, which fires per-brush and would restore too early
        // if any single brush finished before the rest. Ref-counted so overlapping
        // morphs (rapid song changes) don't restore until the last one ends.
        var restoreTimer = new DispatcherTimer { Interval = duration };
        restoreTimer.Tick += (_, _) =>
        {
            restoreTimer.Stop();
            if (--_activeMorphs > 0) return;

            if (_titleInnerCanvas != null)
            {
                _titleInnerCanvas.Effect = _savedTitleEffect;
                _titleInnerCanvas.CacheMode = _savedTitleCache;
            }
            RecordOverlay.CacheMode = _savedRecordCache;
            _savedTitleEffect = null;
            _savedTitleCache = null;
            _savedRecordCache = null;
        };
        restoreTimer.Start();
    }

    public void SetLogoText(string text)
    {
        _logoText = text;
        if (_idleAnimStarted)
            DrawCircularTitle(TitleCanvas, _logoSpin);
    }

    private static void DrawCircularTitle(System.Windows.Controls.Canvas canvas, bool spin)
    {
        _titleSpin = spin;
        canvas.Children.Clear();
        canvas.RenderTransform = null;
        canvas.SizeChanged -= OnTitleCanvasSizeChanged;
        canvas.SizeChanged += OnTitleCanvasSizeChanged;
        RedrawCircularTitle(canvas);
    }

    private static void OnTitleCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.Canvas c)
            RedrawCircularTitle(c);
    }

    private static void RedrawCircularTitle(System.Windows.Controls.Canvas canvas)
    {
        canvas.Children.Clear();
        canvas.RenderTransform = null;
        canvas.Effect = null;
        canvas.CacheMode = null;
        double w = canvas.ActualWidth;
        double h = canvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double cx = w / 2;
        double cy = h / 2;
        double radius = Math.Min(w, h) * 0.45 * 0.18 + 75;

        var text = _logoText;
        double fontSize = Math.Max(12, Math.Min(w, h) * 0.028);

        double angleStep = 360.0 / text.Length;

        var brush = new WpfMedia.SolidColorBrush(WpfColor.FromArgb(180, 0x88, 0xCC, 0xFF));
        if (!_morphColors) brush.Freeze();
        _titleBrush = brush.IsFrozen ? null : brush;
        var font = new WpfMedia.FontFamily("Segoe UI");

        // When not spinning, rotate the starting angle so the two bullet
        // characters sit at 9 o'clock and 3 o'clock (horizontal).
        // The bullets are at index 0 and 14 in the 28-char string.
        // Default layout starts at -90� (12 o'clock). Bullet 0 is at -90�,
        // bullet 14 is at -90 + 14*step. We want bullet 0 at 180� (9 o'clock)
        // so offset = 180 - (-90) = +270.
        double startAngle = _titleSpin ? -90.0 : -90.0 + 270.0;

        // Inner canvas holds the text + shadow.  When the shadow is enabled we also
        // bitmap-cache it so the per-frame spin is a pure GPU transform on the cached
        // texture instead of re-rasterizing the blur kernel.  The canvas is sized to
        // *just* the text ring (plus shadow padding) and centered — not the full
        // window — to keep the cached surface (and the per-invalidation cost during
        // morphs and resizes) as small as possible.
        //
        // When the shadow is disabled there is no benefit to caching: text glyphs
        // composite trivially on the GPU each frame, and skipping the cache avoids
        // a post-morph re-raster spike.
        double innerSize = radius * 2 + (_logoShadow ? 32 : 8) + fontSize * 2;
        var inner = new System.Windows.Controls.Canvas
        {
            Width = innerSize,
            Height = innerSize,
            Effect = _logoShadow
                ? new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = WpfColor.FromRgb(0, 0, 0),
                    BlurRadius = 7,
                    ShadowDepth = 2,
                    Opacity = 0.9,
                    RenderingBias = RenderingBias.Performance,
                }
                : null,
            CacheMode = _logoShadow ? new WpfMedia.BitmapCache(1.0) : null,
        };
        System.Windows.Controls.Canvas.SetLeft(inner, cx - innerSize / 2);
        System.Windows.Controls.Canvas.SetTop(inner, cy - innerSize / 2);
        _titleInnerCanvas = inner;

        // Re-center the per-character math inside the smaller inner canvas.
        double icx = innerSize / 2;
        double icy = innerSize / 2;

        for (int i = 0; i < text.Length; i++)
        {
            double angleDeg = startAngle + i * angleStep;
            double angleRad = angleDeg * Math.PI / 180.0;

            var tb = new System.Windows.Controls.TextBlock
            {
                Text = text[i].ToString(),
                FontSize = fontSize,
                FontWeight = FontWeights.Bold,
                FontFamily = font,
                Foreground = brush,
                RenderTransformOrigin = new WpfPoint(0.5, 0.5),
            };

            // Measure each character to get its actual size for precise centering
            tb.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            double charW = tb.DesiredSize.Width;
            double charH = tb.DesiredSize.Height;

            double x = icx + radius * Math.Cos(angleRad);
            double y = icy + radius * Math.Sin(angleRad);

            tb.RenderTransform = new WpfMedia.RotateTransform(angleDeg + 90);
            System.Windows.Controls.Canvas.SetLeft(tb, x - charW / 2);
            System.Windows.Controls.Canvas.SetTop(tb, y - charH / 2);
            inner.Children.Add(tb);
        }

        canvas.Children.Add(inner);

        if (_titleSpin)
        {
            double spinAngle = (DateTime.UtcNow - SpinEpoch).TotalSeconds / SpinDurationSeconds * 360.0 % 360.0;
            var rotate = new WpfMedia.RotateTransform(spinAngle, cx, cy);
            canvas.RenderTransform = rotate;
            var spin = new DoubleAnimation(spinAngle, spinAngle + 360, TimeSpan.FromSeconds(SpinDurationSeconds))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            rotate.BeginAnimation(WpfMedia.RotateTransform.AngleProperty, spin);
        }
    }

    public void AnimateApplyBlur(double targetRadius, double durationSeconds, Action? onCompleted = null)
    {
        if (Content is not FrameworkElement root) { onCompleted?.Invoke(); return; }

        var blur = new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
        root.Effect = blur;

        var anim = new DoubleAnimation
        {
            To = targetRadius,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        anim.Completed += (_, _) => onCompleted?.Invoke();
        blur.BeginAnimation(BlurEffect.RadiusProperty, anim);
    }

    public void AnimateRemoveBlur(double durationSeconds, Action? onCompleted = null)
    {
        if (Content is not FrameworkElement root || root.Effect is not BlurEffect blur)
        {
            onCompleted?.Invoke();
            return;
        }

        var anim = new DoubleAnimation
        {
            To = 0.0,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        anim.Completed += (_, _) =>
        {
            root.Effect = null;
            onCompleted?.Invoke();
        };
        blur.BeginAnimation(BlurEffect.RadiusProperty, anim);
    }
}
