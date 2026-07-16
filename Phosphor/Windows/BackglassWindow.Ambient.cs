using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using WpfMedia = System.Windows.Media;

namespace Phosphor;

/// <summary>
/// Backglass ambient content engine. Provides idle/ambient background content
/// (static image, single video, video folders, Pinup playlist) that renders in a
/// dedicated layer BENEATH the jukebox video player. The jukebox player always
/// takes priority: ambient content is shown only while idle or during audio-only
/// tracks, and is paused/hidden whenever a jukebox video track is on screen.
///
/// This uses a SEPARATE LibVLC + MediaPlayer instance from the jukebox player so
/// the two never interfere. This separation is deliberate — a future feature will
/// sync the playfield and backglass Pinup videos, which requires the ambient video
/// to be independently controllable.
/// </summary>
public partial class BackglassWindow
{
    // ── Ambient LibVLC (dedicated, separate from the jukebox player) ──
    private LibVLC? _ambientVlc;
    private MediaPlayer? _ambientPlayer;
    private LibVLCSharp.WPF.VideoView? _ambientVideoView;
    private System.Windows.Controls.Border? _ambientFadeOverlay;
    private Task? _ambientVlcInitTask;

    private PlayfieldMode _ambientMode = PlayfieldMode.Screensaver;
    private string? _ambientVideoPath;
    private string? _ambientImagePath;
    private string? _ambientPlayingPath;

    // When a jukebox video track is on screen, the ambient layer must yield.
    private bool _jukeboxVideoActive;

    // Audio (muted by default — ambient video is a silent background loop).
    private bool _ambientAudioEnabled;
    private int _ambientVolume = 50;

    // Folder mode.
    private string[] _ambientFolders = [];
    private VideoFolderPlayMode _ambientFolderPlayMode = VideoFolderPlayMode.Random;
    private int _ambientFolderMinDurationSec = 15;
    private int _ambientFolderMaxDurationSec; // 0 = no maximum
    private string[]? _ambientMostRecentFiles;
    private int _ambientMostRecentIndex;

    // Pinup mode: driven externally by the PinupSyncCoordinator (owned by the DMD) so all
    // screens advance in lockstep. The backglass does NOT shuffle or run its own advance
    // timer — the coordinator selects the current game and pushes its resolved backglass
    // file via SetBackglassPinupCurrentFile. Playback is a seamless single-clip loop.
    private string? _ambientPinupCurrentPath;
    private const string BackglassScreenFolder = "BackGlass";
    // Configurable Pinup media folder (default "BackGlass"); the coordinator's canonical
    // playfield glob is re-pointed to this folder before resolving the actual file.
    private string _ambientPinupFolder = BackglassScreenFolder;

    // Transition / timing.
    private DispatcherTimer? _ambientPositionTimer;
    private bool _ambientTransitioning;
    private DateTime _ambientClipStartUtc;
    private const int AmbientFadeMs = 400;

    private static readonly string[] _ambientVideoExtensions =
        [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".webm"];

    // Folder mode is the only self-driven multi-clip mode; Pinup is coordinator-driven
    // single-clip (like single-file Video mode).
    private bool AmbientMultiClipMode =>
        _ambientMode is PlayfieldMode.VideoFolders;

    private bool AmbientIsVideoMode =>
        _ambientMode is PlayfieldMode.Video or PlayfieldMode.VideoFolders or PlayfieldMode.PinupPlaylist;

    // ── Public API (called via BackglassProxy on the backglass dispatcher) ──

    /// <summary>Sets the ambient display mode and refreshes the idle background.</summary>
    public void SetBackglassMode(PlayfieldMode mode)
    {
        _ambientMode = mode;
        RefreshAmbient();
    }

    public void SetBackglassStaticImage(string? path)
    {
        _ambientImagePath = string.IsNullOrWhiteSpace(path) ? null : path;
        if (_ambientMode == PlayfieldMode.StaticImage)
            RefreshAmbient();
    }

    public void SetBackglassVideoPath(string? path)
    {
        _ambientVideoPath = !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
        if (_ambientMode == PlayfieldMode.Video)
            RefreshAmbient();
    }

    public void SetBackglassVideoFolders(IReadOnlyList<string>? folders)
    {
        _ambientFolders = (folders ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(AmbientResolveFolder)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _ambientMostRecentFiles = null;
        _ambientMostRecentIndex = 0;
        if (_ambientMode == PlayfieldMode.VideoFolders)
            RefreshAmbient();
    }

    public void SetBackglassVideoFolderOptions(VideoFolderPlayMode playMode, int minDurationSec, int maxDurationSec)
    {
        if (_ambientFolderPlayMode != playMode)
        {
            _ambientFolderPlayMode = playMode;
            _ambientMostRecentFiles = null;
            _ambientMostRecentIndex = 0;
        }
        _ambientFolderMinDurationSec = Math.Max(0, minDurationSec);
        _ambientFolderMaxDurationSec = maxDurationSec <= 0 ? 0 : Math.Max(maxDurationSec, _ambientFolderMinDurationSec);
    }

    /// <summary>
    /// Plays a specific Pinup game clip supplied by the <see cref="PinupSyncCoordinator"/>.
    /// <paramref name="canonicalPlayfieldGlob"/> is the canonical playfield glob
    /// (…\Playfield\&lt;base&gt;.*); it is re-pointed to the backglass media folder
    /// (…\BackGlass\&lt;base&gt;.*) and resolved to a real file (extension-agnostic). The clip
    /// loops seamlessly until the coordinator supplies the next game. Shows black if the
    /// backglass has no matching file.
    /// </summary>
    public void SetBackglassPinupCurrentFile(string? canonicalPlayfieldGlob)
    {
        var backglassGlob = RepointToBackglass(canonicalPlayfieldGlob);
        var file = string.IsNullOrWhiteSpace(backglassGlob)
            ? null
            : AmbientResolvePinupGlob(backglassGlob);

        _ambientPinupCurrentPath = file;
        if (file == null)
        {
            DebugLog.Log("BgPinup", $"No backglass video for: {canonicalPlayfieldGlob}");
            if (_ambientMode == PlayfieldMode.PinupPlaylist && !_jukeboxVideoActive)
                StopAmbientVideo();
            return;
        }

        if (_ambientMode != PlayfieldMode.PinupPlaylist || _jukeboxVideoActive)
            return;

        // If a clip is already playing, cross-dip to black to mask the swap (and any
        // input-repeat loop seam), then play the new clip behind the black overlay;
        // OnAmbientVout fades it back in. Otherwise start fresh (overlay begins black
        // and fades in on the first frame).
        var mp = _ambientPlayer;
        if (mp != null && _ambientVideoView != null && mp.IsPlaying)
            SwapAmbientPinupClip();
        else
            RefreshAmbient();
    }

    /// <summary>
    /// Transitions to the coordinator-supplied Pinup clip stored in
    /// <see cref="_ambientPinupCurrentPath"/> with a fade-to-black → swap → fade-in, matching
    /// the folder-mode seam handling so the loop seam and hard cut aren't visible.
    /// </summary>
    private void SwapAmbientPinupClip()
    {
        var mp = _ambientPlayer;
        if (mp == null)
        {
            RefreshAmbient();
            return;
        }

        _ambientTransitioning = true;
        StopAmbientPositionTimer();
        FadeAmbientOverlay(1.0, AmbientFadeMs, () =>
        {
            if (_ambientMode != PlayfieldMode.PinupPlaylist || _jukeboxVideoActive || _ambientPlayer == null)
            {
                _ambientTransitioning = false;
                return;
            }
            // Play the new clip behind the fully-black overlay; OnAmbientVout clears
            // _ambientTransitioning and fades back in when the first frame arrives.
            PlayAmbientMedia(_ambientPlayer);
        });
    }

    /// <summary>
    /// Re-points a canonical playfield glob (…\Playfield\Game.*) to the backglass's mapped
    /// media folder (…\&lt;folder&gt;\Game.*). Returns the original when no Playfield segment
    /// is present.
    /// </summary>
    private string? RepointToBackglass(string? playfieldGlob) =>
        PinupFolderMapping.RepointToFolder(playfieldGlob, _ambientPinupFolder);

    private const string PlayfieldFolderToken = "Playfield";

    /// <summary>
    /// Sets the Pinup media sub-folder the backglass pulls its coordinated clips from. The
    /// canonical playfield glob is re-pointed to this folder (extension-agnostic resolve).
    /// </summary>
    public void SetBackglassPinupFolder(string folder)
    {
        if (!string.IsNullOrWhiteSpace(folder))
            _ambientPinupFolder = folder;
    }

    public void SetBackglassVideoAudio(bool enabled, int volume)
    {
        _ambientAudioEnabled = enabled;
        _ambientVolume = Math.Clamp(volume, 0, 100);
        ApplyAmbientAudio();
    }

    /// <summary>
    /// Notifies the ambient engine whether a jukebox video track is currently on
    /// screen. When true the ambient layer yields (hidden + paused); when false the
    /// configured ambient background is shown again. Called from the jukebox
    /// playback pipeline (see BackglassWindow play/idle transitions).
    /// </summary>
    public void SetJukeboxVideoActive(bool active)
    {
        if (_jukeboxVideoActive == active)
            return;
        _jukeboxVideoActive = active;
        RefreshAmbient();
    }

    // ── State orchestration ──

    /// <summary>
    /// Applies the current ambient state: decides which idle background (if any) is
    /// visible based on the mode and whether a jukebox video is active. Screensaver
    /// and Blank are handled by the existing IdleOverlay; Image/Video/Folders/Pinup
    /// use the dedicated AmbientLayer.
    /// </summary>
    private void RefreshAmbient()
    {
        // Jukebox video is paramount: hide/pause all ambient content while it plays.
        if (_jukeboxVideoActive)
        {
            AmbientImage.Visibility = Visibility.Collapsed;
            AmbientLayer.Visibility = Visibility.Collapsed;
            PauseAmbientVideo();
            return;
        }

        switch (_ambientMode)
        {
            case PlayfieldMode.Blank:
                // Pure black background: collapse both the blob overlay and ambient layer.
                IdleOverlay.Visibility = Visibility.Collapsed;
                AmbientImage.Visibility = Visibility.Collapsed;
                AmbientLayer.Visibility = Visibility.Collapsed;
                StopAmbientVideo();
                break;

            case PlayfieldMode.Screensaver:
                // Classic idle: the blob/logo IdleOverlay. Ensure the ambient layer is
                // torn down so it doesn't linger, and the overlay is shown.
                AmbientImage.Visibility = Visibility.Collapsed;
                AmbientLayer.Visibility = Visibility.Collapsed;
                StopAmbientVideo();
                IdleOverlay.Visibility = Visibility.Visible;
                break;

            case PlayfieldMode.StaticImage:
                // Ambient image replaces the blob/logo overlay — collapse it so the
                // image (on the lower layer) is visible.
                IdleOverlay.Visibility = Visibility.Collapsed;
                StopAmbientVideo();
                ShowAmbientImage();
                break;

            case PlayfieldMode.Video:
            case PlayfieldMode.VideoFolders:
            case PlayfieldMode.PinupPlaylist:
                // Ambient video replaces the blob/logo overlay — collapse it so the
                // video (on the lower layer) is visible.
                IdleOverlay.Visibility = Visibility.Collapsed;
                AmbientImage.Visibility = Visibility.Collapsed;
                AmbientLayer.Visibility = Visibility.Visible;
                StartAmbientVideo();
                break;
        }
    }

    /// <summary>True when the configured ambient mode replaces the default IdleOverlay.</summary>
    private bool AmbientReplacesIdleOverlay =>
        _ambientMode is PlayfieldMode.Blank or PlayfieldMode.StaticImage
            or PlayfieldMode.Video or PlayfieldMode.VideoFolders or PlayfieldMode.PinupPlaylist;

    private void ShowAmbientImage()
    {
        if (string.IsNullOrWhiteSpace(_ambientImagePath) || !File.Exists(_ambientImagePath))
        {
            AmbientImage.Source = null;
            AmbientImage.Visibility = Visibility.Collapsed;
            AmbientLayer.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(_ambientImagePath, UriKind.Absolute);
            bmp.EndInit();
            AmbientImage.Source = bmp;
            AmbientImage.Visibility = Visibility.Visible;
            AmbientLayer.Visibility = Visibility.Visible;
        }
        catch
        {
            AmbientImage.Source = null;
            AmbientImage.Visibility = Visibility.Collapsed;
            AmbientLayer.Visibility = Visibility.Collapsed;
        }
    }

    // ── Ambient LibVLC lifecycle ──

    private MediaPlayer? EnsureAmbientVlc()
    {
        if (_ambientPlayer != null)
            return _ambientPlayer;

        if (_ambientVlcInitTask == null)
            _ambientVlcInitTask = Task.Run(InitializeAmbientVlc);

        if (!_ambientVlcInitTask.IsCompleted)
        {
            var frame = new DispatcherFrame();
            _ambientVlcInitTask.ContinueWith(_ => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }

        if (_ambientPlayer == null)
            InitializeAmbientVlc();

        return _ambientPlayer;
    }

    private void InitializeAmbientVlc()
    {
        if (_ambientPlayer != null)
            return;

        // --aout=directsound: apply Volume/Mute as a per-stream software gain on this
        // instance's own DirectSound secondary buffer, instead of the default mmdevice
        // backend which writes to the shared process-wide Windows mixer session (that
        // path muted/attenuated the backglass main jukebox audio too). Keeps a single
        // mixer entry for the app while making the backglass ambient volume independent.
        var vlc = new LibVLC("--no-video-title-show", "--aout=directsound");
        var mp = new MediaPlayer(vlc) { Mute = true };
        // Stop VLC from grabbing mouse/keyboard on its video HWND so events pass
        // through to the hosting WinForms panel (enables our drag/resize hooks).
        mp.EnableMouseInput = false;
        mp.EnableKeyInput = false;
        mp.Vout += OnAmbientVout;
        mp.EndReached += OnAmbientEndReached;

        _ambientVlc = vlc;
        _ambientPlayer = mp;
        ApplyAmbientAudio();
    }

    private void ApplyAmbientAudio()
    {
        var mp = _ambientPlayer;
        if (mp == null)
            return;
        try
        {
            mp.Mute = !_ambientAudioEnabled;
            mp.Volume = _ambientAudioEnabled ? VolumeTaper.VlcVolume(_ambientVolume) : 0;
        }
        catch { /* volume can be rejected before a vout exists; reapplied on next play */ }
    }

    private LibVLCSharp.WPF.VideoView? EnsureAmbientVideoView()
    {
        if (_ambientVideoView != null)
            return _ambientVideoView;

        var mp = EnsureAmbientVlc();
        if (mp == null)
            return null;

        _ambientVideoView = new LibVLCSharp.WPF.VideoView
        {
            Background = WpfMedia.Brushes.Black,
            MediaPlayer = mp,
            Visibility = Visibility.Hidden,
            Focusable = false,
        };

        _ambientFadeOverlay = new System.Windows.Controls.Border
        {
            Background = WpfMedia.Brushes.Black,
            Opacity = 1.0,
            IsHitTestVisible = false,
        };
        _ambientVideoView.Content = _ambientFadeOverlay;

        System.Windows.Controls.Panel.SetZIndex(_ambientVideoView, 0);
        // Insert BELOW the AmbientImage so the image (when used) stays on top.
        AmbientLayer.Children.Insert(0, _ambientVideoView);
        HookAmbientVideoViewForDrag();
        return _ambientVideoView;
    }

    /// <summary>
    /// Hooks the ambient VideoView's WinForms child so the backglass can still be moved and
    /// resized while ambient video covers the client area (the airspace HWND swallows WPF
    /// mouse input). Mirrors the jukebox VideoView drag hook.
    /// </summary>
    private void HookAmbientVideoViewForDrag()
    {
        if (_ambientVideoView == null) return;

        void Hook()
        {
            var host = FindVisualChild<System.Windows.Forms.Integration.WindowsFormsHost>(_ambientVideoView!);
            if (host?.Child is System.Windows.Forms.Control child)
            {
                child.BackColor = System.Drawing.Color.Black;
                child.MouseDown += (_, me) =>
                {
                    if (me.Button == System.Windows.Forms.MouseButtons.Left)
                        BeginDragOrResizeFromChild(me.X, me.Y);
                };
                child.MouseMove += (_, me) =>
                {
                    child.Cursor = GetChildResizeCursor(me.X, me.Y);
                };
            }
        }

        if (_ambientVideoView.IsLoaded)
            Hook();
        else
            _ambientVideoView.Loaded += (_, _) => Hook();
    }

    private void DetachAmbientVideoView()
    {
        if (_ambientVideoView != null)
        {
            _ambientVideoView.Loaded -= OnAmbientVideoViewLoaded;
            _ambientVideoView.Content = null;
            _ambientFadeOverlay = null;
            AmbientLayer.Children.Remove(_ambientVideoView);
            _ambientVideoView = null;
        }
    }

    // ── Ambient playback ──

    private void StartAmbientVideo()
    {
        if (!IsVisible)
            return; // deferred until the window becomes visible (OnIsVisibleChanged)

        if (_ambientMode == PlayfieldMode.VideoFolders)
        {
            if (_ambientFolders.Length == 0) return;
        }
        else if (_ambientMode == PlayfieldMode.PinupPlaylist)
        {
            if (string.IsNullOrWhiteSpace(_ambientPinupCurrentPath) || !File.Exists(_ambientPinupCurrentPath)) return;
        }
        else if (string.IsNullOrWhiteSpace(_ambientVideoPath) || !File.Exists(_ambientVideoPath))
        {
            return;
        }

        var mp = EnsureAmbientVlc();
        if (mp == null || _ambientVlc == null)
            return;

        var view = EnsureAmbientVideoView();
        if (view == null)
            return;

        // Single-file / pinup mode: if the same file is already looping, just show it.
        if (!AmbientMultiClipMode && mp.IsPlaying &&
            string.Equals(_ambientPlayingPath, CurrentAmbientSingleFilePath, StringComparison.OrdinalIgnoreCase))
        {
            view.Visibility = Visibility.Visible;
            return;
        }

        view.Visibility = Visibility.Hidden;

        if (view.IsLoaded)
        {
            PlayAmbientMedia(mp);
        }
        else
        {
            view.Loaded -= OnAmbientVideoViewLoaded;
            view.Loaded += OnAmbientVideoViewLoaded;
        }
    }

    private void OnAmbientVideoViewLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is LibVLCSharp.WPF.VideoView v)
            v.Loaded -= OnAmbientVideoViewLoaded;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (AmbientIsVideoMode && !_jukeboxVideoActive &&
                _ambientPlayer != null && _ambientVideoView != null && _ambientVideoView.IsLoaded)
                PlayAmbientMedia(_ambientPlayer);
        }), DispatcherPriority.Loaded);
    }

    private void PlayAmbientMedia(MediaPlayer mp)
    {
        if (_ambientVlc == null)
            return;

        if (AmbientMultiClipMode)
        {
            var pick = PickNextAmbientVideoFile(_ambientPlayingPath);
            if (pick == null)
            {
                _ambientTransitioning = false;
                return;
            }

            var media = new Media(_ambientVlc, new Uri(pick));
            media.AddOption(":input-repeat=65535");
            mp.Play(media);
            _ambientPlayingPath = pick;
            _ambientClipStartUtc = DateTime.UtcNow;
            ApplyAmbientAudio();
            return;
        }

        // Single-file mode (single Video, or a coordinator-supplied Pinup clip).
        var path = CurrentAmbientSingleFilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        var single = new Media(_ambientVlc, new Uri(path));
        single.AddOption(":input-repeat=65535");
        mp.Play(single);
        _ambientPlayingPath = path;
        ApplyAmbientAudio();
    }

    /// <summary>
    /// The active single-clip source: the Pinup coordinator-supplied clip when in Pinup
    /// mode, otherwise the single ambient Video file.
    /// </summary>
    private string? CurrentAmbientSingleFilePath =>
        _ambientMode == PlayfieldMode.PinupPlaylist ? _ambientPinupCurrentPath : _ambientVideoPath;

    private void OnAmbientEndReached(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!AmbientMultiClipMode || _jukeboxVideoActive || _ambientPlayer == null)
                return;
            StopAmbientPositionTimer();
            if (_ambientTransitioning)
                return;

            _ambientTransitioning = true;
            if (_ambientFadeOverlay != null)
            {
                _ambientFadeOverlay.BeginAnimation(UIElement.OpacityProperty, null);
                _ambientFadeOverlay.Opacity = 1.0;
            }
            PlayAmbientMedia(_ambientPlayer);
        }));
    }

    private void OnAmbientVout(object? sender, LibVLCSharp.Shared.MediaPlayerVoutEventArgs e)
    {
        if (e.Count <= 0)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (!AmbientIsVideoMode || _jukeboxVideoActive || _ambientVideoView == null)
                return;
            _ambientTransitioning = false;
            _ambientVideoView.Visibility = Visibility.Visible;
            FadeAmbientOverlay(0.0, AmbientFadeMs);
            StartAmbientPositionTimer();
        });
    }

    private void FadeAmbientOverlay(double targetOpacity, int durationMs, Action? onCompleted = null)
    {
        var overlay = _ambientFadeOverlay;
        if (overlay == null)
        {
            onCompleted?.Invoke();
            return;
        }

        var anim = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        anim.Completed += (_, _) =>
        {
            overlay.BeginAnimation(UIElement.OpacityProperty, null);
            overlay.Opacity = targetOpacity;
            onCompleted?.Invoke();
        };
        overlay.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>Pauses ambient video (used while a jukebox video track is on screen).</summary>
    private void PauseAmbientVideo()
    {
        StopAmbientPositionTimer();
        var mp = _ambientPlayer;
        if (mp != null && mp.IsPlaying)
        {
            try { mp.SetPause(true); } catch { }
        }
        if (_ambientVideoView != null)
            _ambientVideoView.Visibility = Visibility.Hidden;
    }

    /// <summary>Fully stops ambient playback and tears down the ambient VideoView.</summary>
    private void StopAmbientVideo()
    {
        StopAmbientPositionTimer();
        _ambientTransitioning = false;
        _ambientPlayingPath = null;
        var mp = _ambientPlayer;
        if (mp != null)
            Task.Run(() => { try { mp.Stop(); } catch { } });
        DetachAmbientVideoView();
    }

    // ── Position timer (folder/pinup min/max durations) ──

    private void StartAmbientPositionTimer()
    {
        StopAmbientPositionTimer();
        if (!AmbientMultiClipMode)
            return;

        _ambientPositionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _ambientPositionTimer.Tick += AmbientPositionTimer_Tick;
        _ambientPositionTimer.Start();
    }

    private void StopAmbientPositionTimer()
    {
        if (_ambientPositionTimer != null)
        {
            _ambientPositionTimer.Stop();
            _ambientPositionTimer.Tick -= AmbientPositionTimer_Tick;
            _ambientPositionTimer = null;
        }
    }

    private void AmbientPositionTimer_Tick(object? sender, EventArgs e)
    {
        var mp = _ambientPlayer;
        if (mp == null || _ambientTransitioning || !AmbientMultiClipMode || _jukeboxVideoActive)
            return;

        double elapsedMs = (DateTime.UtcNow - _ambientClipStartUtc).TotalMilliseconds;
        if (elapsedMs <= 0)
            return;

        double targetMs = ComputeAmbientClipTargetMs(mp);
        if (targetMs <= 0)
            return;

        double trigger = targetMs - AmbientFadeMs;
        if (elapsedMs >= Math.Max(0, trigger))
            BeginAmbientTransition();
    }

    private double ComputeAmbientClipTargetMs(MediaPlayer mp)
    {
        int minSec = _ambientFolderMinDurationSec;
        int maxSec = _ambientFolderMaxDurationSec;
        double minMs = minSec * 1000.0;
        double maxMs = maxSec > 0 ? maxSec * 1000.0 : double.PositiveInfinity;

        long clipLenMs = mp.Length;
        double targetMs;
        if (clipLenMs > 0)
        {
            double loops = Math.Max(1, Math.Ceiling(minMs / clipLenMs));
            targetMs = loops * clipLenMs;
        }
        else
        {
            targetMs = minMs;
        }

        if (targetMs > maxMs)
            targetMs = maxMs;

        return targetMs;
    }

    private void BeginAmbientTransition()
    {
        if (_ambientTransitioning || !AmbientMultiClipMode || _jukeboxVideoActive || _ambientPlayer == null)
            return;

        _ambientTransitioning = true;
        StopAmbientPositionTimer();

        FadeAmbientOverlay(1.0, AmbientFadeMs, () =>
        {
            if (!AmbientMultiClipMode || _jukeboxVideoActive || _ambientPlayer == null)
            {
                _ambientTransitioning = false;
                return;
            }
            PlayAmbientMedia(_ambientPlayer);
        });
    }

    // ── File selection helpers (mirror the playfield engine) ──

    private static string AmbientResolveFolder(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

    private string? AmbientResolvePinupGlob(string glob)
    {
        try
        {
            var dir = Path.GetDirectoryName(glob);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return null;

            var pattern = Path.GetFileName(glob);
            foreach (var f in Directory.EnumerateFiles(dir, pattern))
            {
                if (_ambientVideoExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    return f;
            }
        }
        catch { /* unreadable directory — treat as a miss */ }
        return null;
    }

    private string? PickNextAmbientVideoFile(string? avoid)
    {
        if (_ambientFolderPlayMode == VideoFolderPlayMode.MostRecentFirst)
            return PickAmbientMostRecentFile();
        return PickAmbientRandomFile(avoid);
    }

    private string[] GetAmbientMostRecentFiles()
    {
        if (_ambientMostRecentFiles != null)
            return _ambientMostRecentFiles;

        var all = new List<(string Path, DateTime Modified)>();
        foreach (var folder in _ambientFolders)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(folder))
                {
                    if (_ambientVideoExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    {
                        DateTime modified;
                        try { modified = File.GetLastWriteTimeUtc(f); }
                        catch { modified = DateTime.MinValue; }
                        all.Add((f, modified));
                    }
                }
            }
            catch { /* skip unreadable folder */ }
        }

        _ambientMostRecentFiles = all
            .OrderByDescending(t => t.Modified)
            .Select(t => t.Path)
            .ToArray();
        _ambientMostRecentIndex = 0;
        return _ambientMostRecentFiles;
    }

    private string? PickAmbientMostRecentFile()
    {
        var files = GetAmbientMostRecentFiles();
        if (files.Length == 0)
            return null;

        if (_ambientMostRecentIndex >= files.Length)
            _ambientMostRecentIndex = 0;

        for (int scanned = 0; scanned < files.Length; scanned++)
        {
            var candidate = files[_ambientMostRecentIndex];
            _ambientMostRecentIndex++;
            if (_ambientMostRecentIndex >= files.Length)
                _ambientMostRecentIndex = 0;

            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private string? PickAmbientRandomFile(string? avoid)
    {
        if (_ambientFolders.Length == 0)
            return null;

        for (int attempt = 0; attempt < _ambientFolders.Length; attempt++)
        {
            var folder = _ambientFolders[_rng.Next(_ambientFolders.Length)];
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(folder)
                    .Where(f => _ambientVideoExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .ToArray();
            }
            catch
            {
                continue;
            }

            if (files.Length == 0)
                continue;

            if (files.Length == 1)
                return files[0];

            for (int pick = 0; pick < 6; pick++)
            {
                var candidate = files[_rng.Next(files.Length)];
                if (!string.Equals(candidate, avoid, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            return files[_rng.Next(files.Length)];
        }
        return null;
    }

    /// <summary>Disposes the ambient LibVLC/MediaPlayer. Call from OnClosed.</summary>
    private void DisposeAmbientVlc()
    {
        StopAmbientPositionTimer();
        DetachAmbientVideoView();

        var mp = _ambientPlayer;
        var vlc = _ambientVlc;
        _ambientPlayer = null;
        _ambientVlc = null;
        _ambientVlcInitTask = null;

        if (mp != null)
        {
            mp.Vout -= OnAmbientVout;
            mp.EndReached -= OnAmbientEndReached;
        }

        if (mp != null || vlc != null)
        {
            Task.Run(() =>
            {
                try { mp?.Stop(); } catch { }
                try { mp?.Dispose(); } catch { }
                try { vlc?.Dispose(); } catch { }
            });
        }
    }
}
