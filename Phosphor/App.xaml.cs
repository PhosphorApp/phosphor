using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using NAudio.CoreAudioApi;

namespace Phosphor;

public partial class App : Application
{
    private AppSettings _settings = null!;
    private PlayfieldProxy? _playfieldProxy;
    private Thread? _playfieldThread;
    private BackglassProxy? _backglassProxy;
    private Thread? _backglassThread;
    private TopperProxy? _topperProxy;
    private Thread? _topperThread;
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
        DebugLog.MinimumLevel = _settings.DebugLogLevel;
        RenderPerformanceMonitor.Start();
        var appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        DebugLog.Log(LogLevel.Info, "App", $"Application starting - v{appVersion}");
        DebugLog.Log(LogLevel.Info, "App", "Loading settings complete");

        // Pre-initialize a shared LibVLC instance on a background thread.
        // Both the startup ditti and backglass reuse this single instance.
        _sharedVlcTask = Task.Run(() =>
        {
            try
            {
                DebugLog.Log(LogLevel.Info, "App", "Pre-initializing shared LibVLC...");
                var vlc = new LibVLC("--no-video-title-show", "--network-caching=3000", "--http-reconnect");
                DebugLog.Log(LogLevel.Info, "App", "Shared LibVLC pre-initialized");
                return vlc;
            }
            catch (Exception ex)
            {
                DebugLog.Log(LogLevel.Warning, "App", $"Shared LibVLC pre-init failed: {ex.Message}");
                return (LibVLC?)null;
            }
        });
        var viewModel = new JukeboxViewModel();
        // Engine/quality/stereo come from the YouTube plug-in config (single source of truth). Seed
        // the instance list first so a fresh install still has YouTube defaults to read.
        if (_settings.PluginInstances.Count == 0)
            _settings.PluginInstances = Phosphor.Plugins.PluginSettingsFactory.FromAppSettings(_settings);
        var ytPlayback = Phosphor.Plugins.PluginSettingsFactory.ReadYouTubePlayback(_settings.PluginInstances);
        viewModel.SetVideoEngine(ytPlayback.Video);
        viewModel.SetSearchEngine(ytPlayback.Search);
        viewModel.SetupCache(_settings.CacheEnabled, _settings.CacheMaxSizeGb, _settings.CacheMaxClipLengthMinutes);
        viewModel.SetupPrefetch(_settings.PrefetchEnabled);
        viewModel.SetupThumbnailCache(_settings.ThumbnailCacheEnabled, _settings.ThumbnailCacheMaxSizeMb);
        viewModel.SetupResultCache(_settings.ResultCacheEnabled, _settings.ResultCacheMaxAgeHours);
        CachedImage.Cache = viewModel.ThumbnailCache;
        viewModel.VideoQuality = ytPlayback.Quality;
        viewModel.StereoAudio = ytPlayback.PreferStereo;
        viewModel.PreemptiveCache = _settings.PreemptiveCache;
        viewModel.GaplessPlayback = _settings.GaplessPlayback;
        viewModel.AutoDjProviderId = _settings.AutoDjProviderId;
        viewModel.Volume = _settings.Volume;
        viewModel.RepeatEnabled = _settings.RepeatEnabled;
        viewModel.AutoDjEnabled = _settings.AutoDjEnabled;
        viewModel.SetNetworkTimeout(_settings.NetworkTimeoutSeconds);
        // Configure Plex + its category tiles from the plug-in instance configs.
        viewModel.ConfigurePlexFromSettings(_settings);

        // Build the plug-in source registry — the source path for all discovery/playback.
        // Fire-and-forget: failure is logged and simply leaves the registry empty.
        _ = viewModel.BuildSourceRegistryAsync(_settings);

        MaybeAutoUpdateYtDlp(viewModel);

        // Create and show DMD first — it's the primary window
        _dmdWindow = new DmdWindow { DataContext = viewModel };
        _dmdWindow.SetAppSettings(_settings);
        _dmdWindow.ApplyEarlySettings(_settings);
        _dmdWindow.CheckWindowPositionOnStartup = _settings.CheckWindowsOnStartup;
        _dmdWindow.ApplyLayout(_settings.Dmd);
        _dmdWindow.Show();
        DebugLog.Log(LogLevel.Info, "App", "DmdWindow shown");

        // DMD is the main window — closing it exits the app
        MainWindow = _dmdWindow;
        _dmdWindow.Closed += OnMainWindowClosed;

        // Defer construction of remaining windows so DMD appears immediately.
        // Keep splash visible until all windows are shown.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            DebugLog.Log(LogLevel.Info, "App", "Deferred startup: begin");
            _backglassProxy = CreateBackglassOnOwnThread(viewModel);
            _playfieldProxy = CreatePlayfieldOnOwnThread();
            _topperProxy = CreateTopperOnOwnThread(viewModel);

            // Wire up video playback
            _backglassProxy.AttachViewModel(viewModel);

            // Give all windows access to settings for exit key handling
            _backglassProxy.SetAppSettings(_settings);
            _playfieldProxy.SetAppSettings(_settings);
            _topperProxy.SetAppSettings(_settings);

            // Give DMD access to settings and other windows
            _dmdWindow.SetAppContext(_settings, _playfieldProxy, _backglassProxy, _topperProxy);

            // Apply resizable AFTER SetAppContext so all window references are set
            _dmdWindow.ApplyResizable(_settings.ResizableWindows);

            // Apply saved layouts
            _backglassProxy.CheckWindowPositionOnStartup = _settings.CheckWindowsOnStartup;
            _backglassProxy.ApplyLayout(_settings.Backglass);
            _playfieldProxy.CheckWindowPositionOnStartup = _settings.CheckWindowsOnStartup;
            _playfieldProxy.ApplyLayout(_settings.Playfield);
            _topperProxy.CheckWindowPositionOnStartup = _settings.CheckWindowsOnStartup;
            _topperProxy.ApplyLayout(_settings.Topper);

            // Set playfield mode
            _playfieldProxy.SetStaticImage(_settings.PlayfieldStaticImagePath);
            _playfieldProxy.SetVideoPath(_settings.PlayfieldVideoPath);
            _playfieldProxy.SetVideoFolders(_settings.PlayfieldVideoFolders);
            _playfieldProxy.SetVideoFolderOptions(
                _settings.PlayfieldVideoFolderPlayMode,
                _settings.PlayfieldVideoFolderMinDurationSeconds,
                _settings.PlayfieldVideoFolderMaxDurationSeconds);
            _playfieldProxy.SetVideoAudio(
                _settings.PlayfieldVideoAudioEnabled,
                _settings.PlayfieldVideoAudioVolume);
            _playfieldProxy.SetMode(_settings.PlayfieldDisplayMode);

            // Set backglass ambient content (independent of the playfield)
            _backglassProxy.SetBackglassStaticImage(_settings.BackglassStaticImagePath);
            _backglassProxy.SetBackglassVideoPath(_settings.BackglassVideoPath);
            _backglassProxy.SetBackglassVideoFolders(_settings.BackglassVideoFolders);
            _backglassProxy.SetBackglassVideoFolderOptions(
                _settings.BackglassVideoFolderPlayMode,
                _settings.BackglassVideoFolderMinDurationSeconds,
                _settings.BackglassVideoFolderMaxDurationSeconds);
            _backglassProxy.SetBackglassVideoAudio(
                _settings.BackglassVideoAudioEnabled,
                _settings.BackglassVideoAudioVolume);
            _backglassProxy.SetBackglassMode(_settings.BackglassDisplayMode);

            // Set topper ambient content (independent of the playfield/backglass)
            _topperProxy.SetStaticImage(_settings.TopperStaticImagePath);
            _topperProxy.SetVideoPath(_settings.TopperVideoPath);
            _topperProxy.SetVideoFolders(_settings.TopperVideoFolders);
            _topperProxy.SetVideoFolderOptions(
                _settings.TopperVideoFolderPlayMode,
                _settings.TopperVideoFolderMinDurationSeconds,
                _settings.TopperVideoFolderMaxDurationSeconds);
            _topperProxy.SetVideoAudio(
                _settings.TopperVideoAudioEnabled,
                _settings.TopperVideoAudioVolume);
            _topperProxy.SetMode(_settings.TopperDisplayMode);

            // Apply the Pinup window→media-folder mapping to each follower.
            _playfieldProxy.SetPinupFolder(PinupFolderMapping.GetFolder(_settings.PinupFolderMap, "Playfield"));
            _backglassProxy.SetPinupFolder(PinupFolderMapping.GetFolder(_settings.PinupFolderMap, "Backglass"));
            _topperProxy.SetPinupFolder(PinupFolderMapping.GetFolder(_settings.PinupFolderMap, "Topper"));
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
                _topperProxy.Show();

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
                    DebugLog.Log(LogLevel.Info, "App", $"Deferred startup: auto-playing queue from index {restoreIndex}");
                    viewModel.PlayFromQueueIndex(restoreIndex);
                }
                else
                {
                    viewModel.QueueIndex = restoreIndex;
                }
            }
            else if (_settings.AutoPlayQueueOnStart && viewModel.Queue.Count > 0)
            {
                DebugLog.Log(LogLevel.Info, "App", "Deferred startup: auto-playing queue from start");
                viewModel.PlayCommand.Execute(null);
            }

            // Play startup ditti if enabled and auto-play queue is not active
            PlayStartupDitti(viewModel);

            LogWindowsAudioLevel("Startup");
            DebugLog.Log(LogLevel.Info, "App", "Deferred startup complete");

            // Low-priority: start the Pinup sync coordinator (DMD-owned) after all windows
            // are up. It registers every screen in Pinup mode and drives them in lockstep;
            // only does work when the Pinup Playlist feature is active on some screen.
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => _dmdWindow.StartPinupSync()));
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
    /// Creates the <see cref="TopperWindow"/> on a dedicated STA thread so its
    /// animations never block the main UI thread.
    /// </summary>
    private TopperProxy CreateTopperOnOwnThread(JukeboxViewModel viewModel)
    {
        TopperWindow? window = null;
        var ready = new ManualResetEventSlim(false);

        Exception? bgException = null;
        _topperThread = new Thread(() =>
        {
            try
            {
                _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
                window = new TopperWindow { DataContext = viewModel };
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

        _topperThread.SetApartmentState(ApartmentState.STA);
        _topperThread.IsBackground = true;
        _topperThread.Name = "TopperUI";
        _topperThread.Start();

        WaitWithDispatcherPump(ready);
        if (bgException != null)
        {
            DebugLog.LogException("TopperThread", bgException);
            throw new InvalidOperationException("Failed to create TopperWindow on background thread.", bgException);
        }
        return new TopperProxy(window!);
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
        DebugLog.Log(LogLevel.Info, "App", "Application shutting down");

        // Ensure DOF bridge is shut down (fallback if async closing didn't complete)
        _dmdWindow.ShutdownDof();

        // Save layouts (proxies may be null if window closed before deferred startup)
        _dmdWindow.SaveLayout(_settings.Dmd);
        _backglassProxy?.SaveLayout(_settings.Backglass);
        _playfieldProxy?.SaveLayout(_settings.Playfield);
        _topperProxy?.SaveLayout(_settings.Topper);
        if (_dmdWindow.DataContext is JukeboxViewModel vmSettings)
        {
            _settings.RepeatEnabled = vmSettings.RepeatEnabled;
            _settings.AutoDjEnabled = vmSettings.AutoDjEnabled;
            _settings.LastQueueIndex = vmSettings.LastKnownQueueIndex;
            // Persist the queue on exit so metadata enriched during the session
            // (upload date, accurate duration, chapters) survives a restart.
            vmSettings.SaveQueueState();
        }
        _settings.Save();

        // Flush deferred preset history logs to disk
        try { ProjectMPresetLog.Flush(); } catch { }
        try { ProjectMPresetMonitorLog.Flush(); } catch { }

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

        // Purge the video cache on exit if the user has opted in. Pairs with
        // PreemptiveCache for "instant scrub during the session, clean disk at exit" behavior.
        if (_settings.PurgeCacheOnShutdown && _dmdWindow.DataContext is JukeboxViewModel vmCache)
            vmCache.Cache?.Purge();

        // Close other windows
        _backglassProxy?.Close();
        _backglassProxy?.ShutdownDispatcher();
        _backglassThread?.Join(TimeSpan.FromSeconds(3));
        _playfieldProxy?.Close();
        _playfieldProxy?.ShutdownDispatcher();
        _playfieldThread?.Join(TimeSpan.FromSeconds(3));
        _topperProxy?.Close();
        _topperProxy?.ShutdownDispatcher();
        _topperThread?.Join(TimeSpan.FromSeconds(3));

        // Dispose the shared LibVLC instance last, after all consumers are done
        try { _sharedVlc?.Dispose(); } catch { }
        _sharedVlc = null;

        Shutdown();
    }

    /// <summary>
    /// If yt-dlp auto-update is enabled, runs a throttled self-update in the background (at most
    /// once per week). yt-dlp is an app-wide tool, so this is intentionally agnostic to which
    /// plug-in uses it — the update routes through the first loaded source exposing the
    /// <c>IUpdatable</c> capability and no-ops harmlessly if none does. Fire-and-forget: never
    /// blocks startup and swallows failures. The last-check timestamp is persisted on exit.
    /// </summary>
    private void MaybeAutoUpdateYtDlp(JukeboxViewModel viewModel)
    {
        if (!_settings.YtDlpAutoUpdate)
        {
            DebugLog.Log(LogLevel.Debug, "YtDlpUpdater", "Startup auto-update skipped: auto-update disabled");
            return;
        }

        var sinceLast = DateTime.UtcNow - _settings.YtDlpLastUpdateCheck;
        if (sinceLast < TimeSpan.FromDays(7))
        {
            DebugLog.Log(LogLevel.Debug, "YtDlpUpdater",
                $"Startup auto-update skipped: throttled (last check {sinceLast.TotalDays:F1} days ago, min 7). " +
                "Pressing the manual update button also resets this timer.");
            return;
        }

        _settings.YtDlpLastUpdateCheck = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            try
            {
                // Routes through the first loaded source exposing the IUpdatable capability.
                var status = await viewModel.UpdateEngineToolAsync();
                DebugLog.Log(LogLevel.Info, "YtDlpUpdater", $"Startup auto-update: {status}");
            }
            catch (Exception ex)
            {
                DebugLog.Log(LogLevel.Warning, "YtDlpUpdater", $"Startup auto-update failed: {ex.Message}");
            }
        });
    }

    private void PlayStartupDitti(JukeboxViewModel viewModel)
    {
        var paths = _settings.StartupDittiPaths ?? new List<string>();
        // Honor legacy single-path setting if list is empty
        if (paths.Count == 0 && !string.IsNullOrWhiteSpace(_settings.StartupDittiPath))
            paths = new List<string> { _settings.StartupDittiPath };

        DebugLog.Log(LogLevel.Debug, "Ditti", $"PlayStartupDitti called: Enabled={_settings.EnableStartupDitti}, Count={paths.Count}, AutoPlay={_settings.AutoPlayQueueOnStart}, QueueCount={viewModel.Queue.Count}");
        if (!_settings.EnableStartupDitti) { DebugLog.Log(LogLevel.Debug, "Ditti", "Skipped: not enabled"); return; }
        if (paths.Count == 0) { DebugLog.Log(LogLevel.Debug, "Ditti", "Skipped: no paths"); return; }
        if (_settings.AutoPlayQueueOnStart && viewModel.Queue.Count > 0) { DebugLog.Log(LogLevel.Debug, "Ditti", "Skipped: auto-play queue active"); return; }

        // Resolve relative paths against base directory; filter to ones that exist
        var resolved = new List<string>();
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var full = System.IO.Path.IsPathRooted(p)
                ? p
                : System.IO.Path.Combine(AppContext.BaseDirectory, p);
            if (System.IO.File.Exists(full))
                resolved.Add(full);
            else
                DebugLog.Log(LogLevel.Debug, "Ditti", $"Skipping missing file: {p}");
        }
        if (resolved.Count == 0) { DebugLog.Log(LogLevel.Debug, "Ditti", "Skipped: no existing files"); return; }

        var dittiPath = resolved[Random.Shared.Next(resolved.Count)];
        StartDittiPlayback(viewModel, dittiPath);
    }

    private void StartDittiPlayback(JukeboxViewModel viewModel, string dittiPath)
    {
        try
        {
            _dittiPlayer = new System.Windows.Media.MediaPlayer();
            _dittiPlayer.Volume = viewModel.Volume / 100.0; // WPF volume is 0.0–1.0

            // Show in Now Playing
            viewModel.CurrentlyPlaying = new VideoItem { Title = "Startup Ditti", VideoId = "ditti:startup" };

            // Track volume changes from the slider
            void onVolumeChanged(int v) { if (_dittiPlayer != null) _dittiPlayer.Volume = v / 100.0; }
            viewModel.VolumeChanged += onVolumeChanged;

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
            void cleanupDittiEvents() { viewModel.PlayRequested -= onPlay; viewModel.StopRequested -= onStop; viewModel.VolumeChanged -= onVolumeChanged; }
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
                DebugLog.Log(LogLevel.Warning, "Ditti", $"WPF MediaPlayer failed: {args.ErrorException?.Message}");
                cleanupDittiEvents();
                Dispatcher.BeginInvoke(() =>
                {
                    if (viewModel.CurrentlyPlaying?.VideoId == "ditti:startup")
                        viewModel.CurrentlyPlaying = null;
                    DisposeStartupDitti();
                });
            };

            _dittiPlayer.Open(new Uri(dittiPath, UriKind.Absolute));
            _dittiPlayer.Play();
            DebugLog.Log(LogLevel.Debug, "Ditti", $"Startup ditti playing (WPF MediaPlayer): {dittiPath}");
        }
        catch (Exception ex)
        {
            DebugLog.Log(LogLevel.Warning, "Ditti", $"Startup ditti failed: {ex.Message}");
            if (viewModel.CurrentlyPlaying?.VideoId == "ditti:startup")
                viewModel.CurrentlyPlaying = null;
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
                    DebugLog.Log(LogLevel.Trace, "WinVolume", $"{context}: Per-app volume={vol:P0}, Muted={muted}");
                    return;
                }
            }
            DebugLog.Log(LogLevel.Debug, "WinVolume", $"{context}: No audio session found for this process");
        }
        catch (Exception ex)
        {
            DebugLog.Log(LogLevel.Warning, "WinVolume", $"{context}: Failed to query per-app volume: {ex.Message}");
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

    private static int _crashDialogShown;

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

            // Show at most ONE crash dialog for the process lifetime. A cascading failure
            // (e.g. a cross-thread collection edit that then throws on every render tick)
            // would otherwise open an unbounded storm of dialogs. Every exception is still
            // logged above; we just stop stacking popups once things have clearly gone wrong.
            if (System.Threading.Interlocked.Exchange(ref _crashDialogShown, 1) == 0)
            {
                MessageBox.Show(message, $"Phosphor — {context}",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch { /* last resort — nothing we can do */ }
    }
}
