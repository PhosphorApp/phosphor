using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WpfMedia = System.Windows.Media;
using LibVLC = LibVLCSharp.Shared.LibVLC;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using VlcMedia = LibVLCSharp.Shared.Media;

namespace Phosphor;

/// <summary>
/// Topper ambient content engine. The topper has no jukebox video player of its own,
/// so — like the playfield — the selected content (static image, single video, video
/// folders, or a synchronized Pinup playlist clip) occupies the whole window. When a
/// media mode is active the blob/logo overlay (<see cref="DistortionContainer"/>) is
/// hidden so the media shows cleanly; Screensaver mode shows the blobs + spinning logo.
///
/// This uses a dedicated LibVLC + MediaPlayer instance (NOT the app's shared one) and a
/// VideoView that is attached only while in a video mode so its WinForms-hosted HWND
/// doesn't force software rendering for the GPU-accelerated blob screensaver — mirroring
/// the playfield implementation.
/// </summary>
public partial class TopperWindow
{
    // ── Topper ambient LibVLC (dedicated, separate from any other player) ──
    private LibVLC? _ambientVlc;
    private VlcMediaPlayer? _ambientPlayer;
    private LibVLCSharp.WPF.VideoView? _ambientVideoView;
    private System.Windows.Controls.Border? _ambientFadeOverlay;
    private Task? _ambientVlcInitTask;

    private PlayfieldMode _contentMode = PlayfieldMode.Screensaver;
    private string? _videoPath;
    private string? _ambientImagePath;
    private string? _playingVideoPath;

    private bool _videoMode;
    private bool _folderMode;
    private bool _pinupMode;

    // Audio (muted by default — ambient video is a silent background loop).
    private bool _videoAudioEnabled;
    private int _videoVolume = 50;

    // Folder mode.
    private string[] _videoFolders = [];
    private VideoFolderPlayMode _folderPlayMode = VideoFolderPlayMode.Random;
    private int _folderMinDurationSec = 15;
    private int _folderMaxDurationSec; // 0 = no maximum
    private string[]? _mostRecentFiles;
    private int _mostRecentIndex;

    // Pinup mode: driven externally by the PinupSyncCoordinator (owned by the DMD) so all
    // screens advance in lockstep. The canonical playfield glob is re-pointed to this
    // screen's mapped media folder (see _pinupFolder / SetPinupFolder).
    private string? _pinupCurrentPath;
    private string _pinupFolder = "Topper";

    // Transition / timing.
    private DispatcherTimer? _videoPositionTimer;
    private bool _videoTransitioning;
    private DateTime _clipStartUtc;
    private const int VideoTransitionFadeMs = 400;

    private static readonly string[] _videoExtensions =
        [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".webm"];

    /// <summary>Folder mode is the only self-driven multi-clip mode.</summary>
    private bool MultiClipMode => _folderMode;

    /// <summary>The active single-clip source: the coordinator-supplied Pinup clip in Pinup
    /// mode, otherwise the single Video file. Folder mode advances through a list instead.</summary>
    private string? CurrentSingleFilePath => _pinupMode ? _pinupCurrentPath : _videoPath;

    // ── Public API (called via TopperProxy on the topper dispatcher) ──

    /// <summary>Sets the ambient display mode and switches between the blob/logo overlay and media.</summary>
    public void SetMode(PlayfieldMode mode)
    {
        _contentMode = mode;
        StaticImage.Visibility = Visibility.Collapsed;

        bool enteringVideo = mode is PlayfieldMode.Video or PlayfieldMode.VideoFolders or PlayfieldMode.PinupPlaylist;

        // Leaving (or not entering) video: stop playback and remove the VLC HWND so the
        // blob screensaver keeps GPU-accelerated rendering.
        if (!enteringVideo && _videoMode)
        {
            _videoMode = false;
            StopVideoPlayback();
        }

        bool visible = IsVisible;

        switch (mode)
        {
            case PlayfieldMode.Blank:
                ShowOverlay(false);
                break;

            case PlayfieldMode.Screensaver:
                ShowOverlay(true);
                break;

            case PlayfieldMode.StaticImage:
                ShowOverlay(false);
                StaticImage.Visibility = Visibility.Visible;
                break;

            case PlayfieldMode.Video:
                ShowOverlay(false);
                _videoMode = true;
                _folderMode = false;
                _pinupMode = false;
                if (visible)
                    StartVideoPlayback();
                break;

            case PlayfieldMode.VideoFolders:
                ShowOverlay(false);
                _videoMode = true;
                _folderMode = true;
                _pinupMode = false;
                if (visible)
                    StartVideoPlayback();
                break;

            case PlayfieldMode.PinupPlaylist:
                ShowOverlay(false);
                _videoMode = true;
                _folderMode = false;
                _pinupMode = true;
                if (visible)
                    StartVideoPlayback();
                break;
        }
    }

    // ── Jukebox media-mode takeover / yield (Player 2) ──
    // When the Topper's jukebox player (Player 2) plays a video track it must take over the whole
    // window from ambient content, then yield back when playback stops or an audio-only track plays.
    // These hooks are the Topper's IPlaybackHost EnterMediaMode/ReturnToIdle mapping — they suspend
    // the ambient pipeline (blob overlay, ambient VLC video) and later restore the configured mode.

    private bool _jukeboxMediaActive;

    /// <summary>
    /// A jukebox (Player 2) video track is now on screen: hide the blob overlay and stop ambient
    /// video so the jukebox video surface is the only visible layer.
    /// </summary>
    internal void EnterJukeboxMediaMode()
    {
        _jukeboxMediaActive = true;
        ShowOverlay(false);
        StaticImage.Visibility = Visibility.Collapsed;
        if (_videoMode)
            StopVideoPlayback();
    }

    /// <summary>
    /// Jukebox (Player 2) playback stopped or went audio-only: restore the configured ambient mode
    /// (blob screensaver / image / video / folders / pinup) that was active before takeover.
    /// </summary>
    internal void ReturnToAmbientMode()
    {
        if (!_jukeboxMediaActive)
            return;
        _jukeboxMediaActive = false;
        // Re-apply the configured content mode to restore ambient visuals.
        SetMode(_contentMode);
    }

    /// <summary>Shows or hides the blob/logo overlay (the DistortionContainer). When hidden
    /// don't keep consuming CPU/GPU behind the media; it resumes when shown again.</summary>
    private void ShowOverlay(bool show)
    {
        DistortionContainer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
            ResumeScreensaver();
        else
            PauseScreensaver();
    }

    public void SetStaticImage(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            StaticImage.Source = bitmap;
            _ambientImagePath = path;
        }
        else
        {
            StaticImage.Source = null;
            _ambientImagePath = null;
        }
    }

    public void SetVideoPath(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            _videoPath = path;
            if (_videoMode && !_folderMode && !_pinupMode)
                StartVideoPlayback();
        }
        else
        {
            _videoPath = null;
            if (_videoMode && !_folderMode && !_pinupMode)
                StopVideoPlayback();
        }
    }

    public void SetVideoFolders(IReadOnlyList<string>? folders)
    {
        _videoFolders = (folders ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(ResolveFolder)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _mostRecentFiles = null;
        _mostRecentIndex = 0;

        if (_videoMode && _folderMode && _ambientPlayer == null)
            StartVideoPlayback();
    }

    public void SetVideoFolderOptions(VideoFolderPlayMode playMode, int minDurationSec, int maxDurationSec)
    {
        if (_folderPlayMode != playMode)
        {
            _folderPlayMode = playMode;
            _mostRecentFiles = null;
            _mostRecentIndex = 0;
        }
        _folderMinDurationSec = Math.Max(0, minDurationSec);
        _folderMaxDurationSec = maxDurationSec <= 0 ? 0 : Math.Max(maxDurationSec, _folderMinDurationSec);
    }

    /// <summary>
    /// Sets whether topper ambient video audio plays and at what volume (0–100). Applies to
    /// all video modes. Stored so the state survives a VLC instance rebuild.
    /// </summary>
    public void SetVideoAudio(bool enabled, int volume)
    {
        _videoAudioEnabled = enabled;
        _videoVolume = Math.Clamp(volume, 0, 100);
        ApplyAudioToPlayer();
    }

    /// <summary>
    /// Sets the Pinup media sub-folder this screen pulls its coordinated clips from. The
    /// canonical playfield glob is re-pointed to this folder (extension-agnostic resolve).
    /// </summary>
    public void SetPinupFolder(string folder)
    {
        if (!string.IsNullOrWhiteSpace(folder))
            _pinupFolder = folder;
    }

    public string PinupScreenFolder => _pinupFolder;

    /// <summary>
    /// Plays a specific Pinup game clip supplied by the <see cref="PinupSyncCoordinator"/>.
    /// <paramref name="canonicalPlayfieldGlob"/> is the canonical playfield glob
    /// (…\Playfield\&lt;base&gt;.*); it is re-pointed to this screen's mapped folder and
    /// resolved to a real file (extension-agnostic). The clip loops seamlessly until the
    /// coordinator supplies the next game. Shows black if no matching file exists.
    /// </summary>
    public void SetPinupCurrentFile(string? canonicalPlayfieldGlob)
    {
        var glob = PinupFolderMapping.RepointToFolder(canonicalPlayfieldGlob, _pinupFolder);
        var file = string.IsNullOrWhiteSpace(glob) ? null : ResolvePinupGlob(glob);

        _pinupCurrentPath = file;
        if (file == null)
        {
            DebugLog.Log(LogLevel.Warning, "TopperPinup", $"No topper video for: {canonicalPlayfieldGlob}");
            if (_videoMode && _pinupMode)
                StopVideoPlayback();
            return;
        }

        if (!_videoMode || !_pinupMode)
            return;

        var mp = _ambientPlayer;
        if (mp != null && _ambientVideoView != null && mp.IsPlaying)
            SwapPinupClip();
        else
            StartVideoPlayback();
    }

    private void SwapPinupClip()
    {
        var mp = _ambientPlayer;
        if (mp == null)
        {
            StartVideoPlayback();
            return;
        }

        _videoTransitioning = true;
        StopVideoPositionTimer();
        FadeVideoOverlay(1.0, VideoTransitionFadeMs, () =>
        {
            if (!_videoMode || !_pinupMode || _ambientPlayer == null)
            {
                _videoTransitioning = false;
                return;
            }
            PlayCurrentMedia(_ambientPlayer);
        });
    }

    // ── Playback pipeline (mirrors PlayfieldWindow) ──

    private void StartVideoPlayback()
    {
        if (_folderMode)
        {
            if (_videoFolders.Length == 0)
                return;
        }
        else if (_pinupMode)
        {
            if (string.IsNullOrWhiteSpace(_pinupCurrentPath) || !File.Exists(_pinupCurrentPath))
                return;
        }
        else if (string.IsNullOrWhiteSpace(_videoPath) || !File.Exists(_videoPath))
        {
            return;
        }

        var mp = EnsureVlcInitialized();
        if (mp == null || _ambientVlc == null)
            return;

        var view = EnsureVideoView();
        if (view == null)
            return;

        if (!MultiClipMode && mp.IsPlaying &&
            string.Equals(_playingVideoPath, CurrentSingleFilePath, StringComparison.OrdinalIgnoreCase))
        {
            view.Visibility = Visibility.Visible;
            return;
        }

        view.Visibility = Visibility.Hidden;

        if (view.IsLoaded)
        {
            PlayCurrentMedia(mp);
        }
        else
        {
            view.Loaded -= OnVideoViewLoaded;
            view.Loaded += OnVideoViewLoaded;
        }
    }

    private void OnVideoViewLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is LibVLCSharp.WPF.VideoView v)
            v.Loaded -= OnVideoViewLoaded;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_videoMode && _ambientPlayer != null && _ambientVideoView != null && _ambientVideoView.IsLoaded)
                PlayCurrentMedia(_ambientPlayer);
        }), DispatcherPriority.Loaded);
    }

    private void PlayCurrentMedia(VlcMediaPlayer mp)
    {
        if (_ambientVlc == null)
            return;

        if (MultiClipMode)
        {
            var pick = PickNextVideoFile(_playingVideoPath);
            if (pick == null)
            {
                _videoTransitioning = false;
                return;
            }

            var media = new VlcMedia(_ambientVlc, new Uri(pick));
            media.AddOption(":input-repeat=65535");
            mp.Play(media);
            _playingVideoPath = pick;
            _clipStartUtc = DateTime.UtcNow;
            ApplyAudioToPlayer();
            return;
        }

        var path = CurrentSingleFilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        var single = new VlcMedia(_ambientVlc, new Uri(path));
        single.AddOption(":input-repeat=65535");
        mp.Play(single);
        _playingVideoPath = path;
        ApplyAudioToPlayer();
    }

    private void OnVideoEndReached(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_videoMode || !MultiClipMode || _ambientPlayer == null)
                return;

            StopVideoPositionTimer();
            if (_videoTransitioning)
                return;

            _videoTransitioning = true;
            if (_ambientFadeOverlay != null)
            {
                _ambientFadeOverlay.BeginAnimation(UIElement.OpacityProperty, null);
                _ambientFadeOverlay.Opacity = 1.0;
            }
            PlayCurrentMedia(_ambientPlayer);
        }));
    }

    private void StopVideoPlayback()
    {
        StopVideoPositionTimer();
        _videoTransitioning = false;
        _playingVideoPath = null;
        var mp = _ambientPlayer;
        if (mp != null)
            Task.Run(() => { try { mp.Stop(); } catch { } });
        DetachVideoView();
    }

    // ── VLC lifecycle ──

    private VlcMediaPlayer? EnsureVlcInitialized()
    {
        if (_ambientPlayer != null)
            return _ambientPlayer;

        if (_ambientVlcInitTask == null)
            _ambientVlcInitTask = Task.Run(InitializeVlcCore);

        if (!_ambientVlcInitTask.IsCompleted)
        {
            var frame = new DispatcherFrame();
            _ambientVlcInitTask.ContinueWith(_ => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }

        if (_ambientPlayer == null)
            InitializeVlcCore();

        return _ambientPlayer;
    }

    private void InitializeVlcCore()
    {
        if (_ambientPlayer != null)
            return;

        // --aout=directsound: apply Volume/Mute as a per-stream software gain on this
        // instance's own DirectSound secondary buffer, instead of the default mmdevice
        // backend which writes to the shared process-wide Windows mixer session (that
        // path muted/attenuated the backglass main audio too). Keeps a single mixer
        // entry for the app while making the topper ambient volume independent.
        var vlc = new LibVLC("--no-video-title-show", "--aout=directsound");
        var mp = new VlcMediaPlayer(vlc) { Mute = true };
        mp.EnableMouseInput = false;
        mp.EnableKeyInput = false;
        mp.Vout += OnVideoVout;
        mp.EndReached += OnVideoEndReached;

        _ambientVlc = vlc;
        _ambientPlayer = mp;
        ApplyAudioToPlayer();
    }

    private void ApplyAudioToPlayer()
    {
        var mp = _ambientPlayer;
        if (mp == null)
            return;
        try
        {
            mp.Mute = !_videoAudioEnabled;
            mp.Volume = _videoAudioEnabled ? VolumeTaper.VlcVolume(_videoVolume) : 0;
        }
        catch { /* volume can be rejected before a vout exists; reapplied on next play */ }
    }

    private LibVLCSharp.WPF.VideoView? EnsureVideoView()
    {
        if (_ambientVideoView != null)
            return _ambientVideoView;

        var mp = EnsureVlcInitialized();
        if (mp == null)
            return null;

        _ambientVideoView = new LibVLCSharp.WPF.VideoView
        {
            Background = System.Windows.Media.Brushes.Black,
            MediaPlayer = mp,
            Visibility = Visibility.Hidden,
            Focusable = false,
        };

        _ambientFadeOverlay = new Border
        {
            Background = System.Windows.Media.Brushes.Black,
            Opacity = 1.0,
            IsHitTestVisible = false,
        };
        _ambientVideoView.Content = _ambientFadeOverlay;

        System.Windows.Controls.Panel.SetZIndex(_ambientVideoView, 0);
        Root.Children.Insert(0, _ambientVideoView);
        HookVideoViewForDrag();
        return _ambientVideoView;
    }

    private void HookVideoViewForDrag()
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

    private void DetachVideoView()
    {
        if (_ambientVideoView != null)
        {
            _ambientVideoView.Loaded -= OnVideoViewLoaded;
            _ambientVideoView.Content = null;
            _ambientFadeOverlay = null;
            Root.Children.Remove(_ambientVideoView);
            _ambientVideoView = null;
        }
    }

    private void OnVideoVout(object? sender, LibVLCSharp.Shared.MediaPlayerVoutEventArgs e)
    {
        if (e.Count <= 0)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (!_videoMode || _ambientVideoView == null)
                return;
            _videoTransitioning = false;
            _ambientVideoView.Visibility = Visibility.Visible;
            FadeVideoOverlay(0.0, VideoTransitionFadeMs);
            StartVideoPositionTimer();
        });
    }

    private void FadeVideoOverlay(double targetOpacity, int durationMs, Action? onCompleted = null)
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

    // ── Folder-mode timing (mirrors PlayfieldWindow) ──

    private void StartVideoPositionTimer()
    {
        StopVideoPositionTimer();
        if (!MultiClipMode)
            return;

        _videoPositionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _videoPositionTimer.Tick += VideoPositionTimer_Tick;
        _videoPositionTimer.Start();
    }

    private void StopVideoPositionTimer()
    {
        if (_videoPositionTimer != null)
        {
            _videoPositionTimer.Stop();
            _videoPositionTimer.Tick -= VideoPositionTimer_Tick;
            _videoPositionTimer = null;
        }
    }

    private void VideoPositionTimer_Tick(object? sender, EventArgs e)
    {
        var mp = _ambientPlayer;
        if (mp == null || _videoTransitioning || !MultiClipMode || !_videoMode)
            return;

        double elapsedMs = (DateTime.UtcNow - _clipStartUtc).TotalMilliseconds;
        if (elapsedMs <= 0)
            return;

        double targetMs = ComputeClipTargetMs(mp);
        if (targetMs <= 0)
            return;

        double trigger = targetMs - VideoTransitionFadeMs;
        if (elapsedMs >= Math.Max(0, trigger))
            BeginVideoTransition();
    }

    private double ComputeClipTargetMs(VlcMediaPlayer mp)
    {
        int minSec = _folderMinDurationSec;
        int maxSec = _folderMaxDurationSec;
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

    private void BeginVideoTransition()
    {
        if (_videoTransitioning || !_videoMode || !MultiClipMode || _ambientPlayer == null)
            return;

        _videoTransitioning = true;
        StopVideoPositionTimer();

        FadeVideoOverlay(1.0, VideoTransitionFadeMs, () =>
        {
            if (!_videoMode || !MultiClipMode || _ambientPlayer == null)
            {
                _videoTransitioning = false;
                return;
            }
            PlayCurrentMedia(_ambientPlayer);
        });
    }

    // ── Folder file selection (mirrors PlayfieldWindow) ──

    private static string ResolveFolder(string path) =>
        System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(AppContext.BaseDirectory, path);

    private string? PickNextVideoFile(string? avoid)
    {
        if (_folderPlayMode == VideoFolderPlayMode.MostRecentFirst)
            return PickMostRecentFile();
        return PickRandomVideoFile(avoid);
    }

    private string? PickMostRecentFile()
    {
        var files = GetMostRecentFiles();
        if (files.Length == 0)
            return null;

        if (_mostRecentIndex >= files.Length)
            _mostRecentIndex = 0;

        for (int scanned = 0; scanned < files.Length; scanned++)
        {
            var candidate = files[_mostRecentIndex];
            _mostRecentIndex++;
            if (_mostRecentIndex >= files.Length)
                _mostRecentIndex = 0;

            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private string[] GetMostRecentFiles()
    {
        if (_mostRecentFiles != null)
            return _mostRecentFiles;

        var all = new List<(string Path, DateTime Modified)>();
        foreach (var folder in _videoFolders)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(folder))
                {
                    if (_videoExtensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
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

        _mostRecentFiles = all
            .OrderByDescending(t => t.Modified)
            .Select(t => t.Path)
            .ToArray();
        _mostRecentIndex = 0;
        return _mostRecentFiles;
    }

    private string? PickRandomVideoFile(string? avoid)
    {
        if (_videoFolders.Length == 0)
            return null;

        for (int attempt = 0; attempt < _videoFolders.Length; attempt++)
        {
            var folder = _videoFolders[_rng.Next(_videoFolders.Length)];
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(folder)
                    .Where(f => _videoExtensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
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

    /// <summary>
    /// Resolves a Pinup glob (a path ending in a filename with a <c>.*</c> extension
    /// wildcard) to the first existing video file with a supported extension. Returns null
    /// if the directory or a playable file is missing.
    /// </summary>
    private static string? ResolvePinupGlob(string glob)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(glob);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return null;

            var pattern = System.IO.Path.GetFileName(glob);
            foreach (var f in Directory.EnumerateFiles(dir, pattern))
            {
                if (_videoExtensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    return f;
            }
        }
        catch { /* unreadable directory — treat as a miss */ }
        return null;
    }

    // ── IPinupFollower (see TopperProxy for the proxying implementation) ──

    /// <summary>
    /// Disposes the ambient LibVLC instance + player. Called from OnClosed.
    /// </summary>
    private void DisposeAmbientVlc()
    {
        var mp = _ambientPlayer;
        var vlc = _ambientVlc;
        if (mp != null)
        {
            mp.Vout -= OnVideoVout;
            mp.EndReached -= OnVideoEndReached;
        }
        _ambientPlayer = null;
        _ambientVlc = null;

        if (mp != null || vlc != null)
        {
            Task.Run(() =>
            {
                try { mp?.Stop(); } catch { }
                try { mp?.Dispose(); } catch { }
                try { vlc?.Dispose(); } catch { }
            }).Wait(TimeSpan.FromSeconds(5));
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        StopVideoPositionTimer();
        DisposeAmbientVlc();
        base.OnClosed(e);
    }
}
