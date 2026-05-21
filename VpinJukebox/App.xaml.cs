using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using NAudio.CoreAudioApi;

namespace VpinJukebox;

public partial class App : Application
{
    private AppSettings _settings = null!;
    private PlayfieldProxy? _playfieldProxy;
    private Thread? _playfieldThread;
    private BackglassProxy? _backglassProxy;
    private Thread? _backglassThread;
    private TopperWindow? _topperWindow;
    private DmdWindow _dmdWindow = null!;
    private LibVLC? _sharedVlc;
    private Task<LibVLC?>? _sharedVlcTask;
    private System.Windows.Media.MediaPlayer? _dittiPlayer;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        var splash = new SplashScreen("splash.png");
        splash.Show(autoClose: false);

        // Global exception handlers
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        _settings = AppSettings.Load();
        DebugLog.Enabled = _settings.DebugLogging;
        RenderPerformanceMonitor.Start();
        var appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        DebugLog.Log("App", $"Application starting - v{appVersion}");
        DebugLog.Log("App", "Loading settings complete");

        // Pre-initialize a shared LibVLC instance on a background thread.
        // Both the startup ditti and backglass reuse this single instance.
        _sharedVlcTask = Task.Run(() =>
        {
            try
            {
                DebugLog.Log("App", "Pre-initializing shared LibVLC...");
                var vlc = new LibVLC("--no-video-title-show", "--network-caching=3000", "--http-reconnect");
                DebugLog.Log("App", "Shared LibVLC pre-initialized");
                return vlc;
            }
            catch (Exception ex)
            {
                DebugLog.Log("App", $"Shared LibVLC pre-init failed: {ex.Message}");
                return (LibVLC?)null;
            }
        });
        var viewModel = new JukeboxViewModel();
        viewModel.SetupCache(_settings.CacheEnabled, _settings.CacheMaxSizeGb, _settings.CacheMaxClipLengthMinutes);
        viewModel.SetupPrefetch(_settings.PrefetchEnabled);
        viewModel.SetupThumbnailCache(_settings.ThumbnailCacheEnabled, _settings.ThumbnailCacheMaxSizeMb);
        viewModel.SetupCategoryCache(_settings.CategoryCacheEnabled, _settings.CategoryCacheMaxAgeHours);
        viewModel.SetupYtPlaylistCache(_settings.YtPlaylistCacheEnabled, _settings.YtPlaylistCacheMaxAgeHours);
        viewModel.SetupPlexPlaylistCache(_settings.PlexPlaylistCacheEnabled, _settings.PlexPlaylistCacheMaxAgeHours);
        ThumbnailCacheConverter.Cache = viewModel.ThumbnailCache;
        viewModel.VideoQuality = _settings.VideoQuality;
        viewModel.StereoAudio = _settings.StereoAudio;
        viewModel.CacheMode = _settings.CacheMode;
        viewModel.Volume = _settings.Volume;
        viewModel.RepeatEnabled = _settings.RepeatEnabled;
        viewModel.AutoDjEnabled = _settings.AutoDjEnabled;
        if (!string.IsNullOrWhiteSpace(_settings.PlexServerUrl) && !string.IsNullOrWhiteSpace(_settings.PlexToken))
            viewModel.ConfigurePlex(_settings.PlexServerUrl, _settings.PlexToken, _settings.PlexLibraries, _settings.PlexStereoAudio);

        // Create and show DMD first — it's the primary window
        _dmdWindow = new DmdWindow { DataContext = viewModel };
        _dmdWindow.SetAppSettings(_settings);
        _dmdWindow.ApplyEarlySettings(_settings);
        _dmdWindow.CheckWindowPositionOnStartup = _settings.CheckWindowsOnStartup;
        _dmdWindow.ApplyLayout(_settings.Dmd);
        _dmdWindow.Show();
        DebugLog.Log("App", "DmdWindow shown");

        // DMD is the main window — closing it exits the app
        MainWindow = _dmdWindow;
        _dmdWindow.Closed += OnMainWindowClosed;

        // Defer construction of remaining windows so DMD appears immediately.
        // Keep splash visible until all windows are shown.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            DebugLog.Log("App", "Deferred startup: begin");
            _backglassProxy = CreateBackglassOnOwnThread(viewModel);
            _playfieldProxy = CreatePlayfieldOnOwnThread();
            _topperWindow = new TopperWindow { DataContext = viewModel };

            // Wire up video playback
            _backglassProxy.AttachViewModel(viewModel);

            // Give all windows access to settings for exit key handling
            _backglassProxy.SetAppSettings(_settings);
            _playfieldProxy.SetAppSettings(_settings);
            _topperWindow.SetAppSettings(_settings);

            // Give DMD access to settings and other windows
            _dmdWindow.SetAppContext(_settings, _playfieldProxy, _backglassProxy, _topperWindow);

            // Apply resizable AFTER SetAppContext so all window references are set
            _dmdWindow.ApplyResizable(_settings.ResizableWindows);

            // Apply saved layouts
            _backglassProxy.CheckWindowPositionOnStartup = _settings.CheckWindowsOnStartup;
            _backglassProxy.ApplyLayout(_settings.Backglass);
            _playfieldProxy.CheckWindowPositionOnStartup = _settings.CheckWindowsOnStartup;
            _playfieldProxy.ApplyLayout(_settings.Playfield);
            _topperWindow.CheckWindowPositionOnStartup = _settings.CheckWindowsOnStartup;
            _topperWindow.ApplyLayout(_settings.Topper);

            // Set playfield mode
            _playfieldProxy.SetStaticImage(_settings.PlayfieldStaticImagePath);
            _playfieldProxy.SetVideoPath(_settings.PlayfieldVideoPath);
            _playfieldProxy.SetMode(_settings.PlayfieldDisplayMode);

            // Always initialize backglass so its visual tree and media player are ready.
            // If hidden on launch, briefly show off-screen then hide to ensure initialization.
            if (!_settings.ShowBackglass)
            {
                _backglassProxy.InitializeHidden(_settings.Backglass);
            }
            else
            {
                _backglassProxy.Show();
            }

            if (_settings.ShowPlayfield)
                _playfieldProxy.Show();

            if (_settings.ShowTopper)
                _topperWindow.Show();

            // All windows are now visible — fade out splash
            splash.Close(TimeSpan.FromMilliseconds(300));

            // Ensure the DMD window (main UI) has focus after all windows are shown
            _dmdWindow.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                _dmdWindow.Activate();
                _dmdWindow.Focus();
            });

            // Restore last queue position
            int restoreIndex = _settings.LastQueueIndex;
            if (restoreIndex >= 0 && restoreIndex < viewModel.Queue.Count)
            {
                if (_settings.AutoPlayQueueOnStart)
                {
                    DebugLog.Log("App", $"Deferred startup: auto-playing queue from index {restoreIndex}");
                    viewModel.PlayFromQueueIndex(restoreIndex);
                }
                else
                {
                    viewModel.QueueIndex = restoreIndex;
                }
            }
            else if (_settings.AutoPlayQueueOnStart && viewModel.Queue.Count > 0)
            {
                DebugLog.Log("App", "Deferred startup: auto-playing queue from start");
                viewModel.PlayCommand.Execute(null);
            }

            // Play startup ditti if enabled and auto-play queue is not active
            PlayStartupDitti(viewModel);

            LogWindowsAudioLevel("Startup");
            DebugLog.Log("App", "Deferred startup complete");
        });
    }

    /// <summary>
    /// Creates the <see cref="PlayfieldWindow"/> on a dedicated STA thread so its
    /// animations and rendering never block the main UI thread.
    /// </summary>
    private PlayfieldProxy CreatePlayfieldOnOwnThread()
    {
        PlayfieldWindow? window = null;
        var ready = new ManualResetEventSlim(false);

        Exception? bgException = null;
        _playfieldThread = new Thread(() =>
        {
            try
            {
                // Ensure pack:// URI scheme is available on this thread
                _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
                window = new PlayfieldWindow();
            }
            catch (Exception ex)
            {
                bgException = ex;
            }
            finally
            {
                ready.Set();
            }
            if (window != null)
                Dispatcher.Run();
        });

        _playfieldThread.SetApartmentState(ApartmentState.STA);
        _playfieldThread.IsBackground = true;
        _playfieldThread.Name = "PlayfieldUI";
        _playfieldThread.Start();

        WaitWithDispatcherPump(ready);
        if (bgException != null)
        {
            DebugLog.LogException("PlayfieldThread", bgException);
            throw new InvalidOperationException("Failed to create PlayfieldWindow on background thread.", bgException);
        }
        return new PlayfieldProxy(window!);
    }

    /// <summary>
    /// Creates the <see cref="BackglassWindow"/> on a dedicated STA thread so its
    /// LibVLC rendering and blob animations never block the main UI thread.
    /// </summary>
    private BackglassProxy CreateBackglassOnOwnThread(JukeboxViewModel viewModel)
    {
        BackglassWindow? window = null;
        var ready = new ManualResetEventSlim(false);

        Exception? bgException = null;
        _backglassThread = new Thread(() =>
        {
            try
            {
                // Ensure pack:// URI scheme is available on this thread
                _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
                window = new BackglassWindow();
                window.SetSharedVlcTask(_sharedVlcTask);
                window.DataContext = viewModel;
            }
            catch (Exception ex)
            {
                bgException = ex;
            }
            finally
            {
                ready.Set();
            }
            if (window != null)
                Dispatcher.Run();
        });

        _backglassThread.SetApartmentState(ApartmentState.STA);
        _backglassThread.IsBackground = true;
        _backglassThread.Name = "BackglassUI";
        _backglassThread.Start();

        WaitWithDispatcherPump(ready);
        if (bgException != null)
        {
            DebugLog.LogException("BackglassThread", bgException);
            throw new InvalidOperationException("Failed to create BackglassWindow on background thread.", bgException);
        }
        return new BackglassProxy(window!);
    }

    /// <summary>
    /// Waits for the signal while pumping the main dispatcher, preventing deadlocks
    /// when background-thread window constructors need to access Application.Resources
    /// owned by the main thread.
    /// </summary>
    private static void WaitWithDispatcherPump(ManualResetEventSlim ready)
    {
        var frame = new DispatcherFrame();
        Task.Run(() =>
        {
            ready.Wait();
            frame.Continue = false;
        });
        Dispatcher.PushFrame(frame);
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        LogWindowsAudioLevel("Shutdown");
        DebugLog.Log("App", "Application shutting down");

        // Ensure DOF bridge is shut down (fallback if async closing didn't complete)
        _dmdWindow.ShutdownDof();

        // Save layouts (proxies may be null if window closed before deferred startup)
        _dmdWindow.SaveLayout(_settings.Dmd);
        _backglassProxy?.SaveLayout(_settings.Backglass);
        _playfieldProxy?.SaveLayout(_settings.Playfield);
        _topperWindow?.SaveLayout(_settings.Topper);
        if (_dmdWindow.DataContext is JukeboxViewModel vmSettings)
        {
            _settings.RepeatEnabled = vmSettings.RepeatEnabled;
            _settings.AutoDjEnabled = vmSettings.AutoDjEnabled;
            _settings.LastQueueIndex = vmSettings.LastKnownQueueIndex;
        }
        _settings.Save();

        // Stop startup ditti if still playing
        DisposeStartupDitti();

        // Wait for shared VLC task if it never completed
        if (_sharedVlcTask != null)
        {
            try { _sharedVlc = _sharedVlcTask.GetAwaiter().GetResult(); }
            catch { }
            _sharedVlcTask = null;
        }

        // Prune thumbnail cache on exit
        if (_dmdWindow.DataContext is JukeboxViewModel vm)
            vm.ThumbnailCache?.Prune();

        // Close other windows
        _backglassProxy?.Close();
        _backglassProxy?.ShutdownDispatcher();
        _backglassThread?.Join(TimeSpan.FromSeconds(3));
        _playfieldProxy?.Close();
        _playfieldProxy?.ShutdownDispatcher();
        _playfieldThread?.Join(TimeSpan.FromSeconds(3));
        _topperWindow?.Close();

        // Dispose the shared LibVLC instance last, after all consumers are done
        try { _sharedVlc?.Dispose(); } catch { }
        _sharedVlc = null;

        Shutdown();
    }

    private void PlayStartupDitti(JukeboxViewModel viewModel)
    {
        DebugLog.Log("Ditti", $"PlayStartupDitti called: Enabled={_settings.EnableStartupDitti}, Path='{_settings.StartupDittiPath}', AutoPlay={_settings.AutoPlayQueueOnStart}, QueueCount={viewModel.Queue.Count}");
        if (!_settings.EnableStartupDitti) { DebugLog.Log("Ditti", "Skipped: not enabled"); return; }
        if (string.IsNullOrWhiteSpace(_settings.StartupDittiPath)) { DebugLog.Log("Ditti", "Skipped: no path"); return; }
        if (!System.IO.File.Exists(_settings.StartupDittiPath)) { DebugLog.Log("Ditti", $"Skipped: file not found: {_settings.StartupDittiPath}"); return; }
        if (_settings.AutoPlayQueueOnStart && viewModel.Queue.Count > 0) { DebugLog.Log("Ditti", "Skipped: auto-play queue active"); return; }

        StartDittiPlayback(viewModel);
    }

    private void StartDittiPlayback(JukeboxViewModel viewModel)
    {
        try
        {
            _dittiPlayer = new System.Windows.Media.MediaPlayer();
            _dittiPlayer.Volume = viewModel.Volume / 100.0; // WPF volume is 0.0–1.0

            // Show in Now Playing
            viewModel.CurrentlyPlaying = new VideoItem { Title = "Startup Ditti", VideoId = "ditti:startup" };

            // Stop ditti when real playback starts or user presses stop
            void clearDitti()
            {
                cleanupDittiEvents();
                if (viewModel.CurrentlyPlaying?.VideoId == "ditti:startup")
                    viewModel.CurrentlyPlaying = null;
                DisposeStartupDitti();
            }
            void onPlay(string _) => clearDitti();
            void onStop() => clearDitti();
            void cleanupDittiEvents() { viewModel.PlayRequested -= onPlay; viewModel.StopRequested -= onStop; }
            viewModel.PlayRequested += onPlay;
            viewModel.StopRequested += onStop;

            // Clear Now Playing when ditti finishes naturally
            _dittiPlayer.MediaEnded += (_, _) =>
            {
                cleanupDittiEvents();
                Dispatcher.BeginInvoke(() =>
                {
                    if (viewModel.CurrentlyPlaying?.VideoId == "ditti:startup")
                        viewModel.CurrentlyPlaying = null;
                    DisposeStartupDitti();
                });
            };

            // Log failures gracefully (e.g. unsupported format)
            _dittiPlayer.MediaFailed += (_, args) =>
            {
                DebugLog.Log("Ditti", $"WPF MediaPlayer failed: {args.ErrorException?.Message}");
                cleanupDittiEvents();
                Dispatcher.BeginInvoke(() =>
                {
                    if (viewModel.CurrentlyPlaying?.VideoId == "ditti:startup")
                        viewModel.CurrentlyPlaying = null;
                    DisposeStartupDitti();
                });
            };

            _dittiPlayer.Open(new Uri(_settings.StartupDittiPath, UriKind.Absolute));
            _dittiPlayer.Play();
            DebugLog.Log("Ditti", $"Startup ditti playing (WPF MediaPlayer): {_settings.StartupDittiPath}");
        }
        catch (Exception ex)
        {
            DebugLog.Log("Ditti", $"Startup ditti failed: {ex.Message}");
            DisposeStartupDitti();
        }
    }

    private void DisposeStartupDitti()
    {
        try
        {
            _dittiPlayer?.Stop();
            _dittiPlayer?.Close();
        }
        catch { }
        _dittiPlayer = null;
    }

    /// <summary>
    /// Returns the shared LibVLC instance, waiting for background init if needed.
    /// </summary>
    private LibVLC? EnsureSharedVlc()
    {
        if (_sharedVlc != null)
            return _sharedVlc;

        if (_sharedVlcTask != null)
        {
            _sharedVlc = _sharedVlcTask.GetAwaiter().GetResult();
            _sharedVlcTask = null;
        }

        return _sharedVlc;
    }

    private static void LogWindowsAudioLevel(string context)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            var pid = Environment.ProcessId;
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session.GetProcessID == pid)
                {
                    var vol = session.SimpleAudioVolume.Volume;
                    var muted = session.SimpleAudioVolume.Mute;
                    DebugLog.Log("WinVolume", $"{context}: Per-app volume={vol:P0}, Muted={muted}");
                    return;
                }
            }
            DebugLog.Log("WinVolume", $"{context}: No audio session found for this process");
        }
        catch (Exception ex)
        {
            DebugLog.Log("WinVolume", $"{context}: Failed to query per-app volume: {ex.Message}");
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Suppress transient async/enumerator exceptions from YoutubeExplode
        if (e.Exception is InvalidOperationException
            && e.Exception.StackTrace?.Contains("YoutubeExplode") == true)
        {
            System.Diagnostics.Debug.WriteLine($"[Suppressed] {e.Exception.Message}");
            e.Handled = true;
            return;
        }

        DebugLog.LogException("UI Thread", e.Exception);
        ShowCrashDialog("UI Thread Exception", e.Exception);
        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        DebugLog.LogException("Background Task", e.Exception.Flatten());
        ShowCrashDialog("Background Task Exception", e.Exception.Flatten());
        e.SetObserved();
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        DebugLog.LogException("Fatal", e.ExceptionObject as Exception);
        ShowCrashDialog("Fatal Exception", e.ExceptionObject as Exception);
    }

    private static void ShowCrashDialog(string context, Exception? ex)
    {
        var message = $"[{context}]\n\n{ex?.GetType().Name}: {ex?.Message}\n\n{ex?.StackTrace}";
        System.Diagnostics.Debug.WriteLine(message);

        try
        {
            var logDir = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "logs");
            System.IO.Directory.CreateDirectory(logDir);
            var logPath = System.IO.Path.Combine(logDir, "crash.log");
            System.IO.File.AppendAllText(logPath,
                $"\n\n=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n{message}");

            MessageBox.Show(message, $"VpinJukebox — {context}",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* last resort — nothing we can do */ }
    }
}
