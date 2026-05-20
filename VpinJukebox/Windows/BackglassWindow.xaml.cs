using System.Windows;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;
using WpfMedia = System.Windows.Media;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace VpinJukebox;

public partial class BackglassWindow : JukeboxWindow
{
    private LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;
    private readonly YoutubeClient _youtube = new();
    private readonly Random _rng = new();
    private readonly DispatcherTimer _colorTimer;
    private DispatcherTimer? _positionTimer;
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
    private AudioReactiveService? _audioReactive;
    private double[]? _baseBlobSizes;
    private double _reactiveHueBoost;
    private int _blobCount = 6;
    private int _blobSizeOffset;
    private LibVLCSharp.WPF.VideoView? _videoView;
    private bool _logoDimEnabled;
    private double _logoDimOpacity;
    private bool _isLogoDimmed;
    private readonly DispatcherTimer _logoDimTimer = new();
    private bool _logoMorphEnabled;
    private bool _audioOnly;
    private CancellationTokenSource? _playCts;
    private readonly DispatcherTimer _morphTimer = new();
    private readonly DispatcherTimer _morphCacheRestoreTimer = new();
    private Task? _vlcInitTask;
    private Task<LibVLC?>? _sharedVlcTask;
    private readonly DispatcherTimer _expandButtonHideTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public MediaPlayer MediaPlayer => EnsureVlcInitialized();

    /// <summary>
    /// Returns the MediaPlayer, waiting for background initialization if needed.
    /// Called from the backglass dispatcher thread; pumps messages while waiting
    /// so the UI stays responsive.
    /// </summary>
    private MediaPlayer EnsureVlcInitialized()
    {
        if (_mediaPlayer != null)
            return _mediaPlayer;

        // Background init may still be running — wait for it
        if (_vlcInitTask != null && !_vlcInitTask.IsCompleted)
        {
            // Pump dispatcher messages so the window doesn't freeze
            var frame = new DispatcherFrame();
            _vlcInitTask.ContinueWith(_ => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }

        // If background init didn't run (shouldn't happen), init synchronously
        if (_mediaPlayer == null)
            InitializeVlcCore();

        return _mediaPlayer!;
    }

    /// <summary>
    /// Accepts a shared LibVLC instance from the application so all
    /// consumers reuse a single plugin-scan cost.
    /// Must be called before <see cref="InitializeVlcCore"/>.
    /// </summary>
    public void SetSharedVlc(LibVLC? vlc)
    {
        if (vlc != null)
            _libVLC = vlc;
    }

    /// <summary>
    /// Accepts a task that will produce the shared LibVLC instance. The window's
    /// background init task (started in Loaded) awaits this without blocking the
    /// caller, so app startup doesn't have to wait for plugin scanning to finish.
    /// </summary>
    public void SetSharedVlcTask(Task<LibVLC?>? task)
    {
        _sharedVlcTask = task;
    }

    /// <summary>
    /// Core LibVLC + MediaPlayer creation. Thread-safe; called once from
    /// either the background init task or synchronously as a fallback.
    /// Reuses a shared LibVLC instance if one was provided via <see cref="SetSharedVlc"/>.
    /// </summary>
    private void InitializeVlcCore()
    {
        var vlc = _libVLC ?? new LibVLC("--no-video-title-show", "--network-caching=3000", "--http-reconnect");
        var mp = new MediaPlayer(vlc);
        // Wire EndReached on our dispatcher so the handler can touch UI
        Dispatcher.Invoke(() => mp.EndReached += OnMediaEnded);
        _libVLC = vlc;
        _mediaPlayer = mp;
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
        _colorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _colorTimer.Tick += ColorCycleBlobs;

        _logoDimTimer.Tick += LogoDimTimer_Tick;
        _morphTimer.Tick += MorphTimer_Tick;
        _morphCacheRestoreTimer.Tick += MorphCacheRestore_Tick;
        _expandButtonHideTimer.Tick += (_, _) =>
        {
            _expandButtonHideTimer.Stop();
            if (ExpandButton != null)
                ExpandButton.Visibility = Visibility.Collapsed;
        };

        // Prevent this window from stealing focus when the VLC VideoView
        // (WinForms-hosted HWND) is inserted into the visual tree.
        ShowActivated = false;

        InitializeComponent();

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

            // Initialize LibVLC on a background thread so the window
            // appears immediately without the ~17% startup cost.
            // If the user hits play before this completes,
            // EnsureVlcInitialized will wait with dispatcher pumping.
            _vlcInitTask = Task.Run(() =>
            {
                // If app provided a shared-VLC task, wait for it here (off-thread)
                // and reuse the result instead of spinning up a second LibVLC.
                if (_sharedVlcTask != null && _libVLC == null)
                {
                    try
                    {
                        var shared = _sharedVlcTask.GetAwaiter().GetResult();
                        if (shared != null)
                            _libVLC = shared;
                    }
                    catch { }
                }
                InitializeVlcCore();
            });
        };
    }

    protected override void UpdateExpandButtonVisibility(bool isActive)
    {
        if (ExpandButton == null) return;
        if (isActive)
        {
            ExpandButton.Visibility = Visibility.Visible;
            _expandButtonHideTimer.Stop();
            _expandButtonHideTimer.Start();
        }
        else
        {
            _expandButtonHideTimer.Stop();
            ExpandButton.Visibility = Visibility.Collapsed;
        }
    }

    public override void ForceHideExpandButton()
    {
        _expandButtonHideTimer.Stop();
        if (ExpandButton != null)
            ExpandButton.Visibility = Visibility.Collapsed;
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
            // Prevent white flash — WinForms default BackColor is white
            child.BackColor = System.Drawing.Color.Black;

            child.MouseDown += (_, me) =>
            {
                if (me.Button == System.Windows.Forms.MouseButtons.Left)
                    BeginDragMove();
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
        vm.PlayRequested += OnPlayRequested;
        vm.StopRequested += OnStopRequested;
        vm.SeekRequested += OnSeekRequested;
        vm.PauseRequested += () => Dispatcher.BeginInvoke(() => EnsureVlcInitialized().SetPause(true));
        vm.ResumeRequested += () => Dispatcher.BeginInvoke(() => EnsureVlcInitialized().SetPause(false));
        vm.VolumeChanged += v => Dispatcher.BeginInvoke(() =>
        {
            EnsureVlcInitialized().Volume = v;
            DebugLog.Log("Volume", $"VLC volume set to {v}");
        });

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _positionTimer.Tick += (_, _) =>
        {
            if (DataContext is JukeboxViewModel v && !v.IsSeeking)
            {
                v.PlaybackDuration = Math.Max(1, _mediaPlayer.Length);
                v.PlaybackPosition = Math.Max(0, _mediaPlayer.Time);

                // Prefetch next track when within 30 seconds of the end
                var remaining = _mediaPlayer.Length - _mediaPlayer.Time;
                if (_mediaPlayer.Length > 0 && remaining > 0 && remaining <= 30_000)
                    v.PrefetchNextTrack();
            }
        };
    }

    public void SetAudioOnly(bool audioOnly) => _audioOnly = audioOnly;

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
    }

    private async void OnPlayRequested(string videoId)
    {
        // VM events fire on the main UI thread — marshal to our thread
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnPlayRequested(videoId));
            return;
        }

        // Cancel any in-flight play operation
        _playCts?.Cancel();
        var cts = _playCts = new CancellationTokenSource();
        var ct = cts.Token;

        try
        {
            _colorTimer.Stop();

            // During transitions (video view still attached from previous track),
            // detach the old video view BEFORE stopping. This removes the WinForms
            // HWND from the visual tree so VLC's surface clear isn't visible — the
            // black Grid background shows through instead of a white flash.
            bool isTransition = _videoView != null;
            if (isTransition)
                DetachVideoView();

            // Ensure the media player is fully stopped before starting new playback.
            // This is critical when called from OnMediaEnded (via PlayNext) because
            // LibVLC's EndReached leaves the player in Ended state — calling Play
            // without Stop first can silently fail.
            if (_mediaPlayer.State != VLCState.Stopped)
            {
                await Task.Run(() => _mediaPlayer.Stop());
            }

            if (ct.IsCancellationRequested) return;

            IStreamInfo? infoForOverlay = null;
            string? cachedResolution = null;

            // Wait for first video output before revealing the video surface
            var voutTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnVout(object? s, MediaPlayerVoutEventArgs a)
            {
                _mediaPlayer.Vout -= OnVout;
                Dispatcher.BeginInvoke(() =>
                {
                    if (_videoView != null)
                    {
                        _videoView.Visibility = Visibility.Visible;
                        HookVideoViewForDrag();
                    }
                    // Hide idle overlay once video is rendering (in case it was
                    // briefly shown during a slow transition)
                    IdleOverlay.Visibility = Visibility.Collapsed;
                });
                voutTcs.TrySetResult();
            }
            _mediaPlayer.Vout += OnVout;

            // Create a fresh VideoView, hidden until VLC has a frame ready.
            var videoView = EnsureVideoView();
            videoView.Visibility = Visibility.Hidden;

            var vm = DataContext as JukeboxViewModel;

            // Reset scrubber and duration for the transition; leave volume untouched
            if (vm != null)
            {
                vm.PlaybackPosition = 0;
                vm.PlaybackDuration = 1;
            }

            // Check if this item is audio-only (e.g. Plex music track)
            bool isAudioOnly = _audioOnly || (vm?.CurrentlyPlaying?.IsAudioOnly == true);

            // Plex or other direct-stream source
            if (vm?.CurrentlyPlaying?.StreamUrl is { } streamUrl)
            {
                var media = new Media(_libVLC, new Uri(streamUrl));

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
                    // Play from local muxed file — instant, no buffering, seekable
                    vm?.SetStatusPrefix("Cached");
                    DebugLog.Log("Play", $"Cached playback: {cached.FilePath}");
                    var media = new Media(_libVLC, new Uri(cached.FilePath));
                    _mediaPlayer.Play(media);
                    cachedResolution = cached.Resolution;
                }
                else
                {
                    var manifest = await _youtube.Videos.Streams.GetManifestAsync(videoId);
                    if (ct.IsCancellationRequested) { _mediaPlayer.Vout -= OnVout; return; }
                    var quality = vm?.VideoQuality ?? VideoQualityPreference.High;

                    var stereo = vm?.StereoAudio ?? false;
                    var audioStream = StreamSelector.SelectAudio(manifest, stereo);

                    if (isAudioOnly && audioStream != null)
                    {
                        // Audio-only mode — stream only audio, no video download
                        var media = new Media(_libVLC, new Uri(audioStream.Url));
                        ApplyNetworkOptions(media, vm);
                        _mediaPlayer.Play(media);
                    }
                    else
                    {
                        var videoStream = StreamSelector.SelectVideo(manifest, quality);

                        if (videoStream != null && audioStream != null)
                        {
                            // Feed video as primary, audio as slave input
                            var media = new Media(_libVLC, new Uri(videoStream.Url));
                            media.AddSlave(MediaSlaveType.Audio, 4, new Uri(audioStream.Url));
                            ApplyNetworkOptions(media, vm);
                            _mediaPlayer.Play(media);
                            infoForOverlay = videoStream;
                        }
                        else
                        {
                            // Fallback to muxed if separate streams aren't available
                            var muxed = StreamSelector.SelectMuxed(manifest, quality);
                            if (muxed == null) { _mediaPlayer.Vout -= OnVout; (DataContext as JukeboxViewModel)?.NotifyPlaybackStarted(); return; }
                            var media = new Media(_libVLC, new Uri(muxed.Url));
                            ApplyNetworkOptions(media, vm);
                            _mediaPlayer.Play(media);
                            infoForOverlay = muxed;
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

                var audioCompleted = await Task.WhenAny(playingTcs.Task, Task.Delay(10000));
                _mediaPlayer.Playing -= OnPlaying;

                if (ct.IsCancellationRequested) return;

                if (audioCompleted != playingTcs.Task)
                {
                    // Timed out — server likely unreachable
                    await Task.Run(() => _mediaPlayer.Stop());
                    DetachVideoView();
                    IdleOverlay.Visibility = Visibility.Visible;
                    _colorTimer.Start();
                    if (DataContext is JukeboxViewModel vmAoTimeout)
                    {
                        vmAoTimeout.StatusText = "Playback failed: server unreachable or stream timed out";
                        vmAoTimeout.CurrentlyPlaying = null;
                        vmAoTimeout.NotifyPlaybackStarted();
                    }
                    return;
                }

                IdleOverlay.Visibility = Visibility.Visible;
                _colorTimer.Start();
                _positionTimer?.Start();
                PlaybackStarted?.Invoke();
                if (DataContext is JukeboxViewModel vmAo)
                    vmAo.NotifyPlaybackStarted();
                return;
            }

            // Wait up to 10s for first video frame
            var completed = await Task.WhenAny(voutTcs.Task, Task.Delay(10000));
            _mediaPlayer.Vout -= OnVout;

            if (ct.IsCancellationRequested) return;

            if (completed != voutTcs.Task)
            {
                // Timed out waiting for video — server likely unreachable
                await Task.Run(() => _mediaPlayer.Stop());
                DetachVideoView();
                IdleOverlay.Visibility = Visibility.Visible;
                _colorTimer.Start();
                if (DataContext is JukeboxViewModel vmTimeout)
                {
                    vmTimeout.StatusText = "Playback failed: server unreachable or stream timed out";
                    vmTimeout.CurrentlyPlaying = null;
                    vmTimeout.NotifyPlaybackStarted();
                }
                return;
            }

            IdleOverlay.Visibility = Visibility.Collapsed;

            if (infoForOverlay != null)
                StartVideoInfoPolling(infoForOverlay);
            else if (cachedResolution != null)
                StartVideoInfoPollingCached(cachedResolution);
            _positionTimer?.Start();

            // Notify so the DMD window can reclaim focus
            PlaybackStarted?.Invoke();

            // Allow new play requests now that playback is established
            if (DataContext is JukeboxViewModel vm2)
                vm2.NotifyPlaybackStarted();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Play was cancelled by stop or a new play request — silently bail out
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Playback error: {ex.Message}");
            DetachVideoView();
            IdleOverlay.Visibility = Visibility.Visible;
            _colorTimer.Start();
            if (DataContext is JukeboxViewModel vmErr)
                vmErr.NotifyPlaybackStarted();
        }
    }

    private void OnSeekRequested(long timeMs)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var length = _mediaPlayer.Length;
            DebugLog.Log("Seek", $"Requested: {timeMs}ms | State={_mediaPlayer.State} Length={length} Time={_mediaPlayer.Time}");

            if (length > 0)
            {
                _mediaPlayer.Time = Math.Clamp(timeMs, 0, length);

                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                {
                    DebugLog.Log("Seek", $"After: State={_mediaPlayer.State} Time={_mediaPlayer.Time}");
                });
            }
            else
            {
                DebugLog.Log("Seek", "Skipped: Length <= 0");
            }
        });
    }

    private void OnStopRequested()
    {
        // Cancel any in-flight play operation so it doesn't resume after stop
        _playCts?.Cancel();

        Dispatcher.BeginInvoke(async () =>
        {
            _positionTimer?.Stop();
            _infoTimer?.Stop();
            VideoInfoChanged?.Invoke("");

            // Detach the VideoView BEFORE stopping so the WinForms HWND is
            // removed from the visual tree first — this prevents VLC's video
            // output thread from waiting on UI-thread window messages while
            // Stop() blocks, which would cause a deadlock.
            DetachVideoView();

            // Stop on a background thread to avoid blocking the dispatcher
            // (same pattern used in OnPlayRequested and other call sites).
            if (_mediaPlayer != null)
                await Task.Run(() => _mediaPlayer.Stop());

            IdleOverlay.Visibility = Visibility.Visible;
            _colorTimer.Start();
            ResetLogoDimIdle();
        });
    }

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        DebugLog.Log("MediaEnded", $"EndReached fired | State={_mediaPlayer.State} Time={_mediaPlayer.Time} Length={_mediaPlayer.Length} Pos={_mediaPlayer.Position:F4}");
        Dispatcher.BeginInvoke(() =>
        {
            DebugLog.Log("MediaEnded", "Processing on dispatcher");
            _positionTimer?.Stop();
            _infoTimer?.Stop();
            VideoInfoChanged?.Invoke("");

            if (DataContext is JukeboxViewModel vm && vm.HasNextTrack)
            {
                // Next track available — keep video view attached to avoid idle screen flash.
                // OnPlayRequested will reuse or recreate it as needed.
                vm.PlayNext();
            }
            else
            {
                // Queue finished — show idle screen
                DetachVideoView();
                IdleOverlay.Visibility = Visibility.Visible;
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
        double newIntensity = Math.Clamp(intensity, 0.05, 0.8);
        bool intensityChanged = Math.Abs(newIntensity - _blobIntensity) > 0.001;
        _blobIntensity = newIntensity;
        _blobSpeedMultiplier = Math.Clamp(speed, 0.1, 5.0);

        if (intensityChanged && _currentPattern != null)
        {
            foreach (var blob in _currentPattern.Blobs)
                blob.Opacity = _blobIntensity + _rng.NextDouble() * 0.1;
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
        _baseBlobSizes = null;

        if (_audioReactive != null)
            _audioReactive.Updated += OnAudioUpdated;
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

    private void StartVideoInfoPolling(IStreamInfo streamInfo)
    {
        if (!_showVideoInfo) return;

        string resolution = streamInfo is IVideoStreamInfo vs
            ? $"{vs.VideoResolution.Width}x{vs.VideoResolution.Height}"
            : "?";

        VideoInfoChanged?.Invoke(resolution);

        _infoTimer?.Stop();
        int attempts = 0;
        _infoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _infoTimer.Tick += (_, _) =>
        {
            attempts++;
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
        if (_mediaPlayer.Media == null) return "";
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

    protected override void OnClosed(EventArgs e)
    {
        // Detach VLC events first to prevent callbacks during teardown
        if (_mediaPlayer != null)
            _mediaPlayer.EndReached -= OnMediaEnded;
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

            _currentPattern = BlobTransition.Create(_blobPattern, MakeConfig());
            _currentPattern.Enter(() => { });
        }

        _colorTimer.Start();

        DrawRecordOverlay(RecordOverlay, _logoRings);
        DrawCircularTitle(TitleCanvas, _logoSpin);
    }

    public void SetLogoSpin(bool spin)
    {
        _logoSpin = spin;
        if (_idleAnimStarted)
        {
            DrawCircularTitle(TitleCanvas, _logoSpin);
        }
    }

    public void SetLogoRings(LogoRingsMode mode)
    {
        _logoRings = mode;
        if (_idleAnimStarted)
        {
            DrawRecordOverlay(RecordOverlay, _logoRings);
        }
    }

    public void SetBlobPattern(BlobPattern pattern)
    {
        _transitioning = false;
        _blobPatternSetting = pattern;

        if (pattern == BlobPattern.RandomPerSong)
            pattern = BlobTransition.CurrentRandomPattern;

        _blobPattern = pattern;
        _baseBlobSizes = null;

        // If the canvas isn't laid out yet, just store the pattern —
        // StartIdleAnimation will create the blobs once Loaded fires.
        if (IdleCanvas.ActualWidth < 1 || IdleCanvas.ActualHeight < 1)
            return;

        _currentPattern?.Dispose();
        _currentPattern = BlobTransition.Create(pattern, MakeConfig());
        _currentPattern.Enter(() => { });
        _idleAnimStarted = true;
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
        _baseBlobSizes = null;

        _currentPattern.Exit(() =>
        {
            var newPattern = BlobTransition.CurrentRandomPattern;
            DebugLog.Log("Backglass", $"Transition {_blobPattern} -> {newPattern} blob pattern");
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

        _baseBlobSizes = null;
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

        _baseBlobSizes = null;
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

        // Fade logo elements (record overlay + title canvas) to target opacity over 1 second
        double targetOpacity = _logoDimOpacity;
        var anim = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = TimeSpan.FromSeconds(1),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        RecordOverlay.BeginAnimation(OpacityProperty, anim);
        TitleCanvas.BeginAnimation(OpacityProperty, anim);
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
        var anim = new DoubleAnimation
        {
            To = 1.0,
            Duration = TimeSpan.FromSeconds(0.5),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        RecordOverlay.BeginAnimation(OpacityProperty, anim);
        TitleCanvas.BeginAnimation(OpacityProperty, anim);
    }

    public void SetLogoMorphColor(bool enabled)
    {
        _logoMorphEnabled = enabled;
        _morphColors = enabled;
        _morphTimer.Stop();

        // Redraw with non-frozen (or frozen) brushes as appropriate
        if (_idleAnimStarted)
        {
            DrawRecordOverlay(RecordOverlay, _logoRings);
            DrawCircularTitle(TitleCanvas, _logoSpin);
        }

        if (enabled)
        {
            ScheduleNextMorph();
        }
        else
        {
            ResetLogoColors();
            LogoColorsReset?.Invoke();
        }
    }

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
        double titleHue = _rng.NextDouble() * 360;
        double recordHue = _rng.NextDouble() * 360;
        var titleColor = HslToColor(titleHue, 0.7, 0.55);
        var recordColor = HslToColor(recordHue, 0.6, 0.5);

        var duration = TimeSpan.FromSeconds(1);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };

        // Suspend bitmap caching so animated brush changes render properly
        RecordOverlay.CacheMode = null;
        TitleCanvas.CacheMode = null;

        // Animate title text brushes
        foreach (var child in TitleCanvas.Children)
        {
            if (child is System.Windows.Controls.TextBlock tb
                && tb.Foreground is WpfMedia.SolidColorBrush brush
                && !brush.IsFrozen)
            {
                var anim = new ColorAnimation
                {
                    To = WpfColor.FromArgb(180, titleColor.R, titleColor.G, titleColor.B),
                    Duration = duration,
                    EasingFunction = ease
                };
                brush.BeginAnimation(WpfMedia.SolidColorBrush.ColorProperty, anim);
            }
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
                }
            }
        }

        // Restore bitmap caching after the animation duration
        _morphCacheRestoreTimer.Stop();
        _morphCacheRestoreTimer.Interval = duration + TimeSpan.FromMilliseconds(100);
        _morphCacheRestoreTimer.Start();

        LogoColorsMorphed?.Invoke(titleColor, recordColor);
    }

    private void MorphCacheRestore_Tick(object? sender, EventArgs e)
    {
        _morphCacheRestoreTimer.Stop();
        RecordOverlay.CacheMode = new WpfMedia.BitmapCache(1.0);
        TitleCanvas.CacheMode = new WpfMedia.BitmapCache(1.0);
    }

    private void ResetLogoColors()
    {
        var duration = TimeSpan.FromSeconds(2);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        var defaultTitle = WpfColor.FromArgb(180, 0x88, 0xCC, 0xFF);

        RecordOverlay.CacheMode = null;
        TitleCanvas.CacheMode = null;

        foreach (var child in TitleCanvas.Children)
        {
            if (child is System.Windows.Controls.TextBlock tb
                && tb.Foreground is WpfMedia.SolidColorBrush brush
                && !brush.IsFrozen)
            {
                var anim = new ColorAnimation { To = defaultTitle, Duration = duration, EasingFunction = ease };
                brush.BeginAnimation(WpfMedia.SolidColorBrush.ColorProperty, anim);
            }
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

        _morphCacheRestoreTimer.Stop();
        _morphCacheRestoreTimer.Interval = duration + TimeSpan.FromMilliseconds(100);
        _morphCacheRestoreTimer.Start();
    }

    private void ColorCycleBlobs(object? sender, EventArgs e)
    {
        var brushes = _currentPattern?.Brushes;
        var gradBrushes = _currentPattern?.GradientBrushes;
        if (brushes == null || brushes.Count == 0) return;

        _hueOffset += 0.6;
        double lightness = Math.Clamp((0.15 + _blobIntensity * 0.7) * _brightnessBoost, 0.0, 1.0);
        for (int i = 0; i < brushes.Count; i++)
        {
            double hue = (_hueOffset + _reactiveHueBoost + i * 60.0) % 360.0;
            var color = HslToColor(hue, 0.7, lightness);
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

    private static WpfColor HslToColor(double h, double s, double l)
    {
        double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
        double x = c * (1.0 - Math.Abs((h / 60.0) % 2.0 - 1.0));
        double m = l - c / 2.0;

        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        return WpfColor.FromRgb(
            (byte)((r + m) * 255),
            (byte)((g + m) * 255),
            (byte)((b + m) * 255));
    }

    private static LogoRingsMode _recordRingsMode = LogoRingsMode.Standard;

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

        // Center hole
        var hole = new Ellipse
        {
            Width = holeR * 2, Height = holeR * 2,
            Fill = new WpfMedia.SolidColorBrush(WpfColor.FromArgb(18, 255, 255, 255)),
        };
        System.Windows.Controls.Canvas.SetLeft(hole, cx - holeR);
        System.Windows.Controls.Canvas.SetTop(hole, cy - holeR);
        canvas.Children.Add(hole);

        // Label area (solid subtle disc)
        var label = new Ellipse
        {
            Width = labelR * 2, Height = labelR * 2,
            Stroke = new WpfMedia.SolidColorBrush(WpfColor.FromArgb(10, 255, 255, 255)),
            StrokeThickness = 1,
            Fill = new WpfMedia.SolidColorBrush(WpfColor.FromArgb(6, 255, 255, 255)),
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
                byte alpha = (byte)(5 + (r - grooveStart) / (maxR - grooveStart) * 8);
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
            Stroke = new WpfMedia.SolidColorBrush(WpfColor.FromArgb(15, 255, 255, 255)),
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
    private static string _logoText = "\u2022 VPIN JUKEBOX \u2022 VPIN JUKEBOX ";

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
        var font = new WpfMedia.FontFamily("Segoe UI");

        // When not spinning, rotate the starting angle so the two bullet
        // characters sit at 9 o'clock and 3 o'clock (horizontal).
        // The bullets are at index 0 and 14 in the 28-char string.
        // Default layout starts at -90° (12 o'clock). Bullet 0 is at -90°,
        // bullet 14 is at -90 + 14*step. We want bullet 0 at 180° (9 o'clock)
        // so offset = 180 - (-90) = +270.
        double startAngle = _titleSpin ? -90.0 : -90.0 + 270.0;

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

            double x = cx + radius * Math.Cos(angleRad);
            double y = cy + radius * Math.Sin(angleRad);

            tb.RenderTransform = new WpfMedia.RotateTransform(angleDeg + 90);
            System.Windows.Controls.Canvas.SetLeft(tb, x - charW / 2);
            System.Windows.Controls.Canvas.SetTop(tb, y - charH / 2);
            canvas.Children.Add(tb);
        }

        canvas.CacheMode = new WpfMedia.BitmapCache(1.0);

        if (_titleSpin)
        {
            var rotate = new WpfMedia.RotateTransform(0, cx, cy);
            canvas.RenderTransform = rotate;
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(60))
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
