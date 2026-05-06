using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Key = System.Windows.Input.Key;
using Keyboard = System.Windows.Input.Keyboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Mouse = System.Windows.Input.Mouse;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using TextBox = System.Windows.Controls.TextBox;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace VpinJukebox;

public partial class DmdWindow : JukeboxWindow
{
    private AppSettings? _appSettings;
    private PlayfieldProxy? _playfieldProxy;
    private BackglassProxy? _backglassProxy;
    private TopperWindow? _topperWindow;
    private DirectInputPoller? _dinputPoller;
    private int _resultColumns = 2;
    private int _resultFontSize = 20;
    private bool _showVideoInfo;
    private QueuePosition _queuePosition = QueuePosition.Right;

    // Queue drag-reorder state
    private WpfPoint _queueDragStart;
    private bool _queueDragInProgress;

    // Screensaver fields
    private readonly Random _ssRng = new();
    private readonly DispatcherTimer _ssColorTimer;
    private double _ssHueOffset;
    private double _ssBlobIntensity = 0.25;
    private double _ssBlobSpeedMultiplier = 1.0;
    private BlobPattern _ssBlobPattern = BlobPattern.Random;
    private BlobPattern _ssBlobPatternSetting = BlobPattern.Random;
    private bool _ssTransitioning;
    private IBlobPattern? _ssCurrentPattern;
    private int _ssBlobCount = 6;
    private int _ssDarkBlobStart = -1; // index where dark blobs begin
    private AudioReactiveService? _audioReactive;

    // Dim screensaver fields
    private readonly DispatcherTimer _dimIdleTimer;
    private bool _dimScreensaverEnabled;
    private double _dimOpacity = 0.8;
    private bool _isDimmed;
    private bool _dimDarkBlobsEnabled = true;
    private bool _swapPlayfieldDmdOnDim;
    private bool _applyDefaultDmdOnSwap;
    private bool _isSwapped;
    private bool _applyingSwapLayout;
    // Pre-swap DMD settings saved for restore
    private int _preSwapResultColumns;
    private int _preSwapResultFontSizeModifier;
    private int _preSwapDmdRotation;
    private QueuePosition _preSwapQueuePosition;
    private int _preSwapPlayButtonSizeModifier;
    private int _preSwapQueueButtonSizeModifier;
    private int _preSwapGenreIconSizeModifier;
    private int _preSwapTrackButtonSizeModifier;
    private int _preSwapPlayfieldRotation;
    private int _preSwapQueueFontSizeModifier;
    private double _preSwapQueueSplitterSize;
    private WpfPoint _lastMousePos;

    // Mouse cursor auto-hide fields
    private readonly DispatcherTimer _cursorIdleTimer;
    private bool _cursorHidden;
    private bool _cursorHideSuppressed;
    private PresetBrowserWindow? _presetBrowserWindow;

    // DOF fields
    private DofClient? _dofClient;
    private int _lastDofColorNumber = -1;
    private bool _dofColorBandEnabled;
    private bool _dofStartupEnabled;
    private DateTime _lastDofColorChangeTime;
    private readonly SemaphoreSlim _dofStartLock = new(1, 1);

    public DmdWindow()
    {
        this.Icon = BitmapFrame.Create(new Uri("pack://application:,,,/app.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        _ssColorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _ssColorTimer.Tick += ScreensaverColorCycle;

        _dimIdleTimer = new DispatcherTimer();
        _dimIdleTimer.Tick += DimIdleTimer_Tick;

        _cursorIdleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _cursorIdleTimer.Tick += (_, _) => HideMouseCursor();
        _cursorIdleTimer.Start();

        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += (_, _) => ResetDimIdle();
        PreviewMouseWheel += (_, _) => ResetDimIdle();
        PreviewTextInput += (_, _) => ResetDimIdle();
        PreviewMouseMove += (_, e) =>
        {
            var pos = e.GetPosition(this);
            if (Math.Abs(pos.X - _lastMousePos.X) > 3 || Math.Abs(pos.Y - _lastMousePos.Y) > 3)
            {
                _lastMousePos = pos;
                ShowMouseCursor();
                ResetDimIdle();
            }
        };
        Closing += OnWindowClosing;
        Loaded += (_, _) =>
        {
            WireScrubBar();
            WirePlaylistPicker();
            WireVolumeSlider();
            InitializeNavRing();
            WirePlayButtonQueueSelection();
            WireQueueSplitter();
            if (SearchBox.Template.FindName("PART_EditableTextBox", SearchBox) is TextBox editBox)
                editBox.TextChanged += (_, _) => UpdateSearchPlaceholder();
            if (DataContext is JukeboxViewModel vmLoaded)
            {
                vmLoaded.PropertyChanged += (_, args) => { if (args.PropertyName == "SearchQuery") Dispatcher.BeginInvoke(UpdateSearchPlaceholder); };
                vmLoaded.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(JukeboxViewModel.PlayTransitioning))
                        _ = _dofClient?.TriggerAsync('E', 110, vmLoaded.PlayTransitioning ? 1 : 0);
                };
                vmLoaded.SearchResults.CollectionChanged += (_, args) =>
                {
                    if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                    {
                        _resultsIndex = -1;
                        _inActionMode = false;
                        var sv = FindVisualChildren<ScrollViewer>(ResultsList).FirstOrDefault();
                        sv?.ScrollToTop();
                    }

                    // Force the VirtualizingWrapPanel to re-measure after items arrive,
                    // fixes invisible items on first load
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                    {
                        var panel = FindVisualChildren<WpfToolkit.Controls.VirtualizingWrapPanel>(ResultsList).FirstOrDefault();
                        panel?.InvalidateMeasure();
                    });
                };
            }
            ResultsList.SizeChanged += (_, _) => UpdateResultItemWidth();
            QueueList.ItemContainerGenerator.StatusChanged += (_, _) =>
            {
                if (QueueList.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateQueueDeleteButtonPosition);
            };
        };
    }

    /// <summary>
    /// Ensures the DOF bridge is shut down. Safe to call multiple times.
    /// Called from App shutdown as a fallback in case the async closing handler didn't complete.
    /// </summary>
    public void ShutdownDof()
    {
        _dofClient?.Dispose();
        _dofClient = null;
    }

    private bool _closingHandled;

    private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_closingHandled)
        {
            e.Cancel = true;
            _closingHandled = true;

            // Revert swap before closing to ensure temporary settings are not persisted
            if (_isSwapped && _playfieldProxy != null)
            {
                if (_applyDefaultDmdOnSwap && _appSettings != null)
                {
                    _appSettings.QueueFontSizeModifier = _preSwapQueueFontSizeModifier;
                    _appSettings.DmdQueueSplitterSize = _preSwapQueueSplitterSize;
                    _playfieldProxy.SetRotation(_preSwapPlayfieldRotation);
                }
                _isSwapped = false;
                SwapWithPlayfield();
            }

            // Close any owned modal dialogs so WM_CLOSE isn't blocked
            foreach (Window owned in OwnedWindows)
            {
                owned.Close();
            }

            // Stop playback and dispose DirectInput poller for clean shutdown
            if (DataContext is JukeboxViewModel vm)
                vm.StopPlaybackCommand.Execute(null);

            _ssColorTimer.Stop();
            _dimIdleTimer.Stop();
            _cursorIdleTimer.Stop();
            ShowMouseCursor();
            _dinputPoller?.Dispose();
            _dinputPoller = null;
            _audioReactive?.Dispose();
            _audioReactive = null;
            if (_dofClient?.IsConnected == true)
            {
                try
                {
                    if (_lastDofColorNumber >= 0)
                        await _dofClient.TriggerAsync('E', _lastDofColorNumber, 0);
                }
                catch { /* best-effort */ }
            }
            await (_dofClient?.DisposeAsync() ?? ValueTask.CompletedTask);
            _dofClient = null;

            Dispatcher.BeginInvoke(Close);
        }
    }

    private bool _scrubDragging;

    private void WireScrubBar()
    {
        ScrubBar.AddHandler(System.Windows.Controls.Primitives.Thumb.DragStartedEvent,
            new System.Windows.Controls.Primitives.DragStartedEventHandler((_, _) =>
            {
                _scrubDragging = true;
                if (DataContext is JukeboxViewModel vm)
                {
                    DebugLog.Log("ScrubBar", $"DragStarted | Value={ScrubBar.Value} Max={ScrubBar.Maximum} IsSeeking→true");
                    vm.IsSeeking = true;
                }
            }));
        ScrubBar.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
            new System.Windows.Controls.Primitives.DragCompletedEventHandler((_, _) =>
            {
                _scrubDragging = false;
                if (DataContext is JukeboxViewModel vm)
                {
                    DebugLog.Log("ScrubBar", $"DragCompleted | SeekTo={(long)ScrubBar.Value} Duration={vm.PlaybackDuration} Position={vm.PlaybackPosition}");
                    vm.SeekTo((long)ScrubBar.Value);
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                    {
                        vm.IsSeeking = false;
                        DebugLog.Log("ScrubBar", "IsSeeking→false");
                    });
                }
            }));

        // Handle click-to-seek (IsMoveToPointEnabled) which doesn't fire Thumb drag events
        ScrubBar.PreviewMouseUp += (_, e) =>
        {
            if (_scrubDragging) return; // handled by DragCompleted
            if (DataContext is JukeboxViewModel vm && vm.IsPlaying)
            {
                vm.SeekTo((long)ScrubBar.Value);
            }
        };
    }

    private void WirePlaylistPicker()
    {
        PlaylistPicker.SelectionChanged += (_, _) =>
        {
            if (PlaylistPicker.SelectedValue is string name
                && DataContext is JukeboxViewModel vm
                && !string.IsNullOrEmpty(name))
            {
                var cat = vm.Categories.FirstOrDefault(c => c.IsPlaylist && c.Name == name);
                if (cat != null)
                    vm.SelectCategoryCommand.Execute(cat);
            }
        };
    }

    private void WireVolumeSlider()
    {
        // Persist volume changes to settings
        if (DataContext is JukeboxViewModel vm)
        {
            vm.VolumeChanged += v =>
            {
                if (_appSettings != null)
                {
                    _appSettings.Volume = v;
                    _appSettings.Save();
                }
            };
        }
    }

    private void WirePlayButtonQueueSelection()
    {
        // When Play is pressed and nothing is playing, start from the selected queue item
        StartStopButton.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (DataContext is JukeboxViewModel vm && !vm.IsPlaying && !vm.IsPaused
                && QueueList.SelectedIndex >= 0 && QueueList.SelectedIndex < vm.Queue.Count)
            {
                e.Handled = true;
                vm.PlayFromQueueIndex(QueueList.SelectedIndex);
            }
        };
    }

    private void WireQueueSplitter()
    {
        QueueSplitter.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
            new System.Windows.Controls.Primitives.DragCompletedEventHandler((_, _) =>
            {
                if (_appSettings == null) return;
                // Save the queue panel's actual size after the user drags the splitter
                if (_queuePosition == QueuePosition.Right)
                    _appSettings.DmdQueueSplitterSize = QueueBorder.ActualWidth;
                else
                    _appSettings.DmdQueueSplitterSize = QueueBorder.ActualHeight;
                _appSettings.Save();
            }));
    }

    private void AddToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not VideoItem item) return;
        if (DataContext is not JukeboxViewModel vm) return;

        var selected = ShowPlaylistPickerDialog(vm);
        if (selected != null)
        {
            vm.ActivePlaylistName = selected;
            vm.AddToPlaylistCommand.Execute(item);
        }
    }

    private System.Windows.Window CreateDarkDialog(string title, double width = 300, double height = 200)
    {
        var bgBrush = (System.Windows.Media.Brush)FindResource("BgBrush");
        var textBrush = (System.Windows.Media.Brush)FindResource("TextBrush");
        var dimBrush = (System.Windows.Media.Brush)FindResource("TextDimBrush");
        var borderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x44, 0x44, 0x44));

        // Suppress cursor auto-hide while modal is open
        _cursorIdleTimer.Stop();
        _cursorHideSuppressed = true;
        ShowMouseCursor();

        var dialog = new System.Windows.Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            Owner = this,
            Width = width,
            SizeToContent = SizeToContent.Height,
            MaxHeight = height,
            ResizeMode = ResizeMode.NoResize,
        };

        dialog.Closed += (_, _) =>
        {
            _cursorHideSuppressed = false;
            ApplyCursorHideTimeout();
        };

        var border = new System.Windows.Controls.Border
        {
            Background = bgBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
        };

        // Title bar with close button
        var titleBar = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        titleBar.MouseLeftButtonDown += (_, _) => dialog.DragMove();
        var closeBtn = new System.Windows.Controls.Button
        {
            Content = "✕",
            Padding = new Thickness(4, 2, 4, 2),
            FontSize = 11,
            Background = System.Windows.Media.Brushes.Transparent,
            Foreground = dimBrush,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };
        closeBtn.Click += (_, _) => { dialog.DialogResult = false; dialog.Close(); };
        DockPanel.SetDock(closeBtn, System.Windows.Controls.Dock.Right);
        var titleText = new System.Windows.Controls.TextBlock
        {
            Text = title,
            Foreground = textBrush,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleBar.Children.Add(closeBtn);
        titleBar.Children.Add(titleText);

        var contentStack = new System.Windows.Controls.StackPanel();
        contentStack.Children.Add(titleBar);

        border.Child = contentStack;
        dialog.Content = border;
        dialog.Tag = contentStack; // store for callers to add content

        return dialog;
    }

    private string? ShowPlaylistPickerDialog(JukeboxViewModel vm)
    {
        var dialog = CreateDarkDialog("Add to Playlist", 280, 300);
        var stack = (System.Windows.Controls.StackPanel)dialog.Tag!;
        var surfaceBrush = (System.Windows.Media.Brush)FindResource("SurfaceBrush");
        var textBrush = (System.Windows.Media.Brush)FindResource("TextBrush");

        var listBox = new System.Windows.Controls.ListBox
        {
            Background = surfaceBrush,
            Foreground = textBrush,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            FontSize = 13,
            MaxHeight = 200,
        };

        var accentBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");

        string? result = null;
        foreach (var pl in vm.StaticPlaylists)
            listBox.Items.Add(pl.Name);

        void AcceptSelection()
        {
            if (listBox.SelectedItem is string name)
            {
                result = name;
                dialog.DialogResult = true;
                dialog.Close();
            }
        }

        listBox.MouseDoubleClick += (_, _) => AcceptSelection();

        var selectBtn = new System.Windows.Controls.Button
        {
            Content = "Select",
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 14, 0, 0),
            Background = accentBrush,
            Foreground = textBrush,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        };
        selectBtn.Click += (_, _) => AcceptSelection();

        stack.Children.Add(listBox);
        stack.Children.Add(selectBtn);
        return dialog.ShowDialog() == true ? result : null;
    }

    /// <summary>
    /// Apply layout-affecting settings synchronously before the window is shown
    /// to prevent visual jittering as settings are applied later.
    /// </summary>
    public void ApplyEarlySettings(AppSettings settings)
    {
        _appSettings = settings;
        SetDmdRotation(settings.DmdRotation);
        SetQueuePosition(settings.DmdQueuePosition);
        ApplyTrackListSettings(settings.ResultColumns, settings.ResultFontSizeModifier);
        SetPlayButtonSize(settings.DmdPlayButtonSizeModifier);
        SetQueueButtonSize(settings.DmdQueueButtonSizeModifier);
        SetGenreIconSize(settings.DmdGenreIconSizeModifier);
        SetTrackButtonSize(settings.DmdTrackButtonSizeModifier);
        SetMinorButtonLocation(settings.DmdMinorButtonLocation);
        SetShowStatusText(settings.ShowStatusText);
        TitleTextBlock.Text = settings.TitleText;
    }

    public void SetAppContext(AppSettings settings, PlayfieldProxy playfieldProxy, BackglassProxy backglassProxy, TopperWindow topperWindow)
    {
        DebugLog.Log("DMD", "SetAppContext: begin");
        _appSettings = settings;
        ApplyCursorHideTimeout();
        _playfieldProxy = playfieldProxy;
        _backglassProxy = backglassProxy;
        _topperWindow = topperWindow;

        // Wire mouse/key events from other windows to reset DMD dim idle
        // Must dispatch to each window's owning thread to avoid cross-thread access
        var dmdHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        playfieldProxy.Dispatcher.BeginInvoke(() =>
        {
            playfieldProxy.Window.DmdWindowHandle = dmdHandle;
            WireDimIdleEvents(playfieldProxy.Window);
        });
        backglassProxy.Dispatcher.BeginInvoke(() => WireDimIdleEvents(backglassProxy.Window));
        WireDimIdleEvents(topperWindow);

        _backglassProxy.PlaybackStarted += () => Dispatcher.BeginInvoke(() =>
        {
            Activate();
            OnPlaybackStartedTransition();
        });
        _backglassProxy.SetShowVideoInfo(settings.ShowVideoInfo);
        _backglassProxy.VideoInfoChanged += OnVideoInfoChanged;
        _showVideoInfo = settings.ShowVideoInfo;
        _backglassProxy.SetScreensaverSettings(settings.ScreensaverIntensity, settings.ScreensaverSpeed);
        _backglassProxy.SetLogoText(settings.LogoText);
        _backglassProxy.SetLogoSpin(settings.LogoSpin);
        _backglassProxy.SetLogoRings(settings.LogoRings);
        _playfieldProxy.SetScreensaverSettings(settings.ScreensaverIntensity, settings.ScreensaverSpeed);
        _playfieldProxy.SetOledSleepDefeat(settings.OledSleepDefeatSeconds, settings.OledSleepDefeatDurationSeconds, settings.OledSleepDefeatIntensity);
        _topperWindow.SetScreensaverSettings(settings.ScreensaverIntensity, settings.ScreensaverSpeed);
        _topperWindow.SetLogoSpin(settings.LogoSpin);
        _topperWindow.SetLogoRings(settings.LogoRings);
        _topperWindow.SetDistortion(settings.TopperDistortion);
        BlobTransition.ExcludeMandelbrotFromRandom = settings.ExcludeMandelbrotFromRandom;
        MandelbrotPattern.MandelbrotMaxHz = settings.MandelbrotMaxHz;
        MandelbrotPattern.RenderScale = Math.Clamp(settings.MandelbrotRenderScale, 0.2, 1.0);
        MandelbrotPattern.AdaptiveIterations = settings.MandelbrotAdaptiveIterations;
        MandelbrotPattern.MaxIterations = Math.Clamp(settings.MandelbrotMaxIterations, 64, 8192);
        MandelbrotPattern.UseGpu = settings.MandelbrotUseGpu == 1;
        MandelbrotPattern.Perturbation = settings.MandelbrotPerturbation;
        MandelbrotPattern.Discovery = settings.MandelbrotDiscovery;
        MandelbrotPattern.Dimming = settings.MandelbrotDimming;
        BlobTransition.ExcludeProjectMFromRandom = settings.ExcludeProjectMFromRandom;
        ProjectMRenderer.PresetDuration = settings.ProjectMPresetDuration;
        ProjectMRenderer.SoftCutDuration = settings.ProjectMSoftCutDuration;
        ProjectMRenderer.HardCutEnabled = settings.ProjectMHardCutEnabled;
        ProjectMRenderer.MeshSize = (uint)Math.Clamp(settings.ProjectMMeshSize, 16, 128);
        ProjectMRenderer.BeatSensitivity = Math.Clamp(settings.ProjectMBeatSensitivity, 0f, 3f);
        ProjectMRenderer.RenderScale = Math.Clamp(settings.ProjectMRenderScale, 0.25, 1.0);
        ProjectMRenderer.PresetPath = !string.IsNullOrEmpty(settings.ProjectMPresetPath)
            ? settings.ProjectMPresetPath
            : System.IO.Path.Combine(AppContext.BaseDirectory, "Presets", "ProjectM");
        ProjectMRenderer.TexturePath = !string.IsNullOrEmpty(settings.ProjectMTexturePath)
            ? settings.ProjectMTexturePath
            : System.IO.Path.Combine(AppContext.BaseDirectory, "presets", "textures");
        ProjectMRenderer.EnabledFolders = settings.ProjectMEnabledFolders;
        ProjectMRenderer.ColorSampleDelaySeconds = Math.Clamp(settings.ProjectMColorSampleDelaySeconds, 0.1, 30.0);
        ProjectMRenderer.PresetMonitorMode = Math.Clamp(settings.ProjectMPresetMonitor, 0, 2);
        ProjectMRenderer.BlackCheckRequiredHits = Math.Clamp(settings.ProjectMPresetMonitorBlackHits, 1, 20);
        ProjectMRenderer.BlackCheckIntervalSeconds = Math.Clamp(settings.ProjectMPresetMonitorIntervalSeconds, 0.5, 30.0);
        ProjectMRenderer.BlackCheckPercentile = Math.Clamp(settings.ProjectMPresetMonitorPercentile, 1.0, 50.0);
        if (settings.ExcludeMandelbrotFromRandom && BlobTransition.CurrentRandomPattern == BlobPattern.Mandelbrot)
            BlobTransition.CurrentRandomPattern = BlobTransition.PickRandom(new Random());
        _backglassProxy.SetBlobCount(settings.BackglassBlobCount);
        _backglassProxy.SetBlobPattern(settings.BackglassBlobPattern);
        _backglassProxy.SetLogoDim(settings.BackglassLogoDimEnabled, settings.BackglassLogoDimOpacity, settings.BackglassLogoDimTimeoutSeconds);
        _backglassProxy.SetLogoMorphColor(settings.BackglassLogoMorphColor);
        _backglassProxy.SetAudioOnly(settings.BackglassAudioOnly);
        if (settings.ShowTopper)
            _topperWindow.SetLogoMorphColor(settings.BackglassLogoMorphColor);
        _backglassProxy.LogoColorsMorphed += (titleColor, recordColor) =>
        {
            if (_appSettings?.ShowTopper == true)
                _topperWindow?.Dispatcher.BeginInvoke(() => _topperWindow.ApplyMorphColors(titleColor, recordColor));
        };
        _backglassProxy.LogoColorsReset += () =>
        {
            _topperWindow?.Dispatcher.BeginInvoke(() => _topperWindow.ApplyResetColors());
        };
        _playfieldProxy.SetBlobCount(settings.PlayfieldBlobCount);
        _playfieldProxy.SetBlobPattern(settings.PlayfieldBlobPattern);
        _playfieldProxy.SetRotation(settings.PlayfieldRotation);
        _topperWindow.SetBlobCount(settings.TopperBlobCount);
        _topperWindow.SetBlobPattern(settings.TopperBlobPattern);
        SetBlobCount(settings.DmdBlobCount);
        SetBlobPattern(settings.DmdBlobPattern);
        SetDmdScreensaver(settings.DmdScreensaver);
        SetDmdScreensaverDim(settings.DmdScreensaverDimEnabled, settings.DmdScreensaverDimOpacity, settings.DmdScreensaverDimTimeoutSeconds, settings.DmdScreensaverDimDarkBlobs, settings.SwapPlayfieldDmdOnDim, settings.ApplyDefaultDmdOnSwap);
        SetScreensaverSettings(settings.ScreensaverIntensity, settings.ScreensaverSpeed);
        ApplyReactiveBlobs(settings.ReactiveBlobs);

        // DOF color band — subscribe to playfield blob color changes
        _playfieldProxy.BlobColorBandChanged += OnPlayfieldColorBandChanged;
        ApplyDofStartup(settings.DofEnabled, settings.DofColorBand && settings.ShowPlayfield);
        

        if (DataContext is JukeboxViewModel vm)
        {
            vm.SetHiddenCategories(settings.HiddenCategories);
            vm.NewPlaylistRequested += () => NewPlaylist_Click(this, new RoutedEventArgs());
        }

        if (DataContext is JukeboxViewModel vm2b)
            vm2b.PlexDrillDownRequested += HandlePlexDrillDown;

        StartDirectInputPoller();
        DebugLog.Log("DMD", "SetAppContext: complete");
    }

    private void StartDirectInputPoller()
    {
        _dinputPoller?.Dispose();
        _dinputPoller = new DirectInputPoller();
        _dinputPoller.ButtonPressed += OnDInputButtonPressed;
        _dinputPoller.Start();
    }

    public void RestartDirectInputPoller() => StartDirectInputPoller();

    private void OnDInputButtonPressed(Guid deviceGuid, int buttonIndex)
    {
        // DirectInput fires on a background thread; marshal everything to the UI thread.
        Dispatcher.BeginInvoke(() =>
        {
            HideMouseCursor();
            ResetDimIdle();
            var bindings = _appSettings?.KeyBindings ?? new KeyBindings();
            if (!bindings.TryGetAction(deviceGuid, buttonIndex, out var action))
                return;

            if (DataContext is not JukeboxViewModel vm)
                return;

            switch (action)
            {
                case JukeboxAction.NavLeft:
                    if (_inActionMode) NavigateActionButtons(-1, vm);
                    else NavigateSelection(-1, vm);
                    break;
                case JukeboxAction.NavUp:
                    if (_inActionMode) { ExitActionMode(); NavigateSelection(-1, vm); }
                    else NavigateSelection(-1, vm);
                    break;
                case JukeboxAction.NavRight:
                    if (_inActionMode) NavigateActionButtons(1, vm);
                    else NavigateSelection(1, vm);
                    break;
                case JukeboxAction.NavDown:
                    if (_inActionMode) { ExitActionMode(); NavigateSelection(1, vm); }
                    else NavigateSelection(1, vm);
                    break;
                case JukeboxAction.Select:
                    ActivateSelection(vm);
                    break;
                case JukeboxAction.Back:
                    if (_inActionMode) ExitActionMode();
                    else if (vm.IsViewingPlexMusic) { Dispatcher.BeginInvoke(async () => { if (!await vm.PlexDrillBackAsync()) { vm.ShowCategoryListCommand.Execute(null); ApplyNavHighlight(vm); } }); }
                    else if (vm.IsViewingPlexHubOrPlaylist) { vm.PlexHubGoBack(); Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ApplyNavHighlight(vm)); }
                    else { vm.ShowCategoryListCommand.Execute(null); Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ApplyNavHighlight(vm)); }
                    break;
                case JukeboxAction.Skip:
                    vm.SkipCommand.Execute(null);
                    break;
                case JukeboxAction.StopPlayback:
                    vm.StopPlaybackCommand.Execute(null);
                    break;
                case JukeboxAction.FavToggle:
                    AddToPlaylistSelected(vm);
                    break;
                case JukeboxAction.QueueSelected:
                    QueueSelected(vm);
                    break;
                case JukeboxAction.ToggleAutoDj:
                    vm.ToggleAutoDjCommand.Execute(null);
                    break;
                case JukeboxAction.SeekForward:
                    vm.SeekForwardCommand.Execute(null);
                    break;
                case JukeboxAction.SeekBack:
                    vm.SeekBackCommand.Execute(null);
                    break;
                case JukeboxAction.OpenSettings:
                    OpenSettings();
                    break;
                case JukeboxAction.OpenPresetBrowser:
                    OpenPresetBrowser();
                    break;
                case JukeboxAction.ExitApp:
                    vm.StopPlaybackCommand.Execute(null);
                    Close();
                    break;
                case JukeboxAction.Pause:
                    vm.TogglePauseResumeCommand.Execute(null);
                    break;
                case JukeboxAction.TogglePlayStop:
                    {
                        VideoItem? fallback = (_resultsIndex >= 0 && _resultsIndex < vm.SearchResults.Count) ? vm.SearchResults[_resultsIndex] : null;
                        vm.TogglePlayStopCommand.Execute(fallback);
                    }
                    break;
                case JukeboxAction.CreatePlaylistFromQueue:
                    vm.CreatePlaylistFromQueueCommand.Execute(null);
                    break;
                case JukeboxAction.ToggleShuffle:
                    vm.ShuffleQueueCommand.Execute(null);
                    break;
                case JukeboxAction.ToggleRepeat:
                    vm.ToggleRepeatCommand.Execute(null);
                    break;
                case JukeboxAction.FocusSearch:
                    Dispatcher.BeginInvoke(() => { var tb = SearchBox.Template.FindName("PART_EditableTextBox", SearchBox) as TextBox; tb?.Focus(); });
                    break;
                case JukeboxAction.Home:
                    vm.ShowCategoryListCommand.Execute(null);
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ApplyNavHighlight(vm));
                    break;
            }
        });
    }

    private void ToggleExpand_Click(object sender, RoutedEventArgs e) => ToggleExpand();

    private void CloseApp_Click(object sender, RoutedEventArgs e) => Close();

    protected override void UpdateExpandButtonVisibility(bool isActive)
    {
        // Always keep fullscreen and settings buttons visible on DMD, but subtle when inactive
        if (FullscreenButton != null)
        {
            FullscreenButton.Visibility = Visibility.Visible;
            FullscreenButton.Opacity = 0.50;
        }
        if (SettingsButtonCtrl != null)
        {
            SettingsButtonCtrl.Visibility = Visibility.Visible;
            SettingsButtonCtrl.Opacity = 0.50;
        }
        if (CloseAppButton != null)
        {
            CloseAppButton.Visibility = Visibility.Visible;
            CloseAppButton.Opacity = 0.50;
        }

        // When the DMD receives focus, force-hide expand buttons on all satellite windows
        if (isActive)
        {
            _backglassProxy?.ForceHideExpandButton();
            _playfieldProxy?.ForceHideExpandButton();
            _topperWindow?.Dispatcher.BeginInvoke(() => _topperWindow.ForceHideExpandButton());
        }
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is JukeboxViewModel vm)
        {
            // Force sync from ComboBox — Text binding may be stale after dropdown selection
            vm.SearchQuery = SearchBox.Text.Trim();
            vm.SearchCommand.Execute(null);
            ResultsList.Focus();
            SaveLivePlaylistButton.IsEnabled = !string.IsNullOrWhiteSpace(SearchBox.Text);
        }
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is JukeboxViewModel vm)
        {
            vm.SearchQuery = SearchBox.Text.Trim();
            vm.SearchCommand.Execute(null);
            ResultsList.Focus();
            SaveLivePlaylistButton.IsEnabled = !string.IsNullOrWhiteSpace(SearchBox.Text);
            e.Handled = true;
        }
    }

    private void SearchBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox cb
            && cb.SelectedItem is string selected
            && DataContext is JukeboxViewModel vm)
        {
            vm.SearchQuery = selected.Trim();
        }
        UpdateSearchPlaceholder();
    }

    private void SearchBox_FocusChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
            SearchPlaceholder.Visibility = Visibility.Collapsed;
        else
            UpdateSearchPlaceholder();
    }

    private void UpdateSearchPlaceholder()
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) && !SearchBox.IsKeyboardFocusWithin
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ── Plex music drill-down handlers ──

    private void PlexBack_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is JukeboxViewModel vm)
        {
            Dispatcher.BeginInvoke(async () =>
            {
                if (!await vm.PlexDrillBackAsync())
                    vm.ShowCategoryListCommand.Execute(null);
            });
        }
    }

    private void PlexHubBack_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not JukeboxViewModel vm) return;
        vm.PlexHubGoBack();
    }

    private void PlexSearchMode_Changed(object sender, RoutedEventArgs e)
    {
        if (DataContext is not JukeboxViewModel vm) return;

        if (PlexSearchArtist?.IsChecked == true)
            vm.PlexSearchMode = PlexSearchMode.Artist;
        else if (PlexSearchAlbum?.IsChecked == true)
            vm.PlexSearchMode = PlexSearchMode.Album;
        else if (PlexSearchTrack?.IsChecked == true)
            vm.PlexSearchMode = PlexSearchMode.Track;
    }

    /// <summary>
    /// Handle clicks on Plex artist/album/hub/playlist items for drill-down navigation.
    /// </summary>
    private void HandlePlexDrillDown(VideoItem item)
    {
        if (DataContext is not JukeboxViewModel vm) return;

        if (item.PlexItemType == PlexItemType.Artist && item.PlexRatingKey != null)
            _ = vm.PlexDrillIntoArtistAsync(item.PlexRatingKey, item.Title);
        else if (item.PlexItemType == PlexItemType.Album && item.PlexRatingKey != null)
            _ = vm.PlexDrillIntoAlbumAsync(item.PlexRatingKey, item.Title);
        else if (item.PlexItemType == PlexItemType.Hub && item.PlexHubKey != null)
            _ = vm.BrowsePlexHubContentAsync(item.PlexHubKey, item.PlexHubType, item.Title);
        else if (item.PlexItemType == PlexItemType.Playlist && item.PlexRatingKey != null)
            _ = vm.BrowsePlexPlaylistContentAsync(item.PlexRatingKey, item.Title);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Always reset dim idle on any key press, even if the key is handled below
        ResetDimIdle();

        // When a TextBox has focus, only intercept non-printable shortcuts
        // (F-keys, Escape, modifiers-only like Shift). Let all letter/number keys through for typing.
        if (Keyboard.FocusedElement is TextBox)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (!IsNonPrintableShortcut(key))
                return;
        }

        {
            var bindings = _appSettings?.KeyBindings ?? new KeyBindings();
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (!bindings.TryGetAction(key, out var action))
                return;

            if (DataContext is not JukeboxViewModel vm)
                return;

            e.Handled = true;

            switch (action)
            {
                case JukeboxAction.NavLeft:
                    if (_inActionMode)
                        NavigateActionButtons(-1, vm);
                    else
                        NavigateSelection(-1, vm);
                    break;

                case JukeboxAction.NavUp:
                    if (_inActionMode)
                        { ExitActionMode(); NavigateSelection(-1, vm); }
                    else
                        NavigateSelection(-1, vm);
                    break;

                case JukeboxAction.NavRight:
                    if (_inActionMode)
                        NavigateActionButtons(1, vm);
                    else
                        NavigateSelection(1, vm);
                    break;

                case JukeboxAction.NavDown:
                    if (_inActionMode)
                        { ExitActionMode(); NavigateSelection(1, vm); }
                    else
                        NavigateSelection(1, vm);
                    break;

                case JukeboxAction.Select:
                    ActivateSelection(vm);
                    break;

                case JukeboxAction.Back:
                    if (_inActionMode)
                    {
                        ExitActionMode();
                    }
                    else if (vm.IsViewingPlexMusic)
                    {
                        Dispatcher.BeginInvoke(async () =>
                        {
                            if (!await vm.PlexDrillBackAsync())
                            {
                                vm.ShowCategoryListCommand.Execute(null);
                                ApplyNavHighlight(vm);
                            }
                        });
                    }
                    else if (vm.IsViewingPlexHubOrPlaylist)
                    {
                        vm.PlexHubGoBack();
                        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => ApplyNavHighlight(vm));
                    }
                    else
                    {
                        vm.ShowCategoryListCommand.Execute(null);
                        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => ApplyNavHighlight(vm));
                    }
                    break;

                case JukeboxAction.Skip:
                    vm.SkipCommand.Execute(null);
                    break;

                case JukeboxAction.StopPlayback:
                    vm.StopPlaybackCommand.Execute(null);
                    break;

                case JukeboxAction.FavToggle:
                    AddToPlaylistSelected(vm);
                    break;

                case JukeboxAction.QueueSelected:
                    QueueSelected(vm);
                    break;

                case JukeboxAction.ToggleAutoDj:
                    vm.ToggleAutoDjCommand.Execute(null);
                    break;

                case JukeboxAction.SeekForward:
                    vm.SeekForwardCommand.Execute(null);
                    break;

                case JukeboxAction.SeekBack:
                    vm.SeekBackCommand.Execute(null);
                    break;

                case JukeboxAction.OpenSettings:
                    OpenSettings();
                    break;

                case JukeboxAction.OpenPresetBrowser:
                    OpenPresetBrowser();
                    break;

                case JukeboxAction.ExitApp:
                    vm.StopPlaybackCommand.Execute(null);
                    Close();
                    break;

                case JukeboxAction.Pause:
                    vm.TogglePauseResumeCommand.Execute(null);
                    break;

                case JukeboxAction.TogglePlayStop:
                    {
                        VideoItem? fallback = (_resultsIndex >= 0 && _resultsIndex < vm.SearchResults.Count) ? vm.SearchResults[_resultsIndex] : null;
                        vm.TogglePlayStopCommand.Execute(fallback);
                    }
                    break;

                case JukeboxAction.CreatePlaylistFromQueue:
                    vm.CreatePlaylistFromQueueCommand.Execute(null);
                    break;

                case JukeboxAction.ToggleShuffle:
                    vm.ShuffleQueueCommand.Execute(null);
                    break;

                case JukeboxAction.ToggleRepeat:
                    vm.ToggleRepeatCommand.Execute(null);
                    break;

                case JukeboxAction.FocusSearch:
                    Dispatcher.BeginInvoke(() => { var tb = SearchBox.Template.FindName("PART_EditableTextBox", SearchBox) as TextBox; tb?.Focus(); });
                    break;

                case JukeboxAction.Home:
                    _resultsIndex = -1;
                    _inActionMode = false;
                    vm.ShowCategoryListCommand.Execute(null);
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => ApplyNavHighlight(vm));
                    break;
            }
        }
    }

    /// <summary>
    /// Returns true if this key is a non-printable key that should be intercepted
    /// even when a TextBox has focus (F-keys, Escape, arrow keys, etc.)
    /// </summary>
    private static bool IsNonPrintableShortcut(Key key) =>
        key is Key.Escape or Key.F1 or Key.F2 or Key.F3 or Key.F4 or Key.F5
            or Key.F6 or Key.F7 or Key.F8 or Key.F9 or Key.F10 or Key.F11 or Key.F12
            or Key.Up or Key.Down or Key.Left or Key.Right
            or Key.LeftShift or Key.RightShift
            or Key.LeftCtrl or Key.RightCtrl;

    // ── Nav ring: flat ordered list of all navigable elements ──

    private int _navIndex;
    private int _resultsIndex = -1;
    private FrameworkElement? _highlightedElement;

    // ── Action-button sub-navigation ──
    private bool _inActionMode;
    private int _actionButtonIndex;
    private List<System.Windows.Controls.Button>? _actionButtons;
    private System.Windows.Controls.Button? _highlightedActionButton;

    private static readonly DropShadowEffect NavHighlightEffect = new()
    {
        Color = Colors.DodgerBlue,
        ShadowDepth = 0,
        BlurRadius = 15,
        Opacity = 0.9
    };

    private void InitializeNavRing()
    {
        _navIndex = 0;
        if (DataContext is JukeboxViewModel vm && vm.ShowCategories)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => ApplyNavHighlight(vm));
    }

    /// <summary>
    /// Builds the ordered list of navigable elements for the home (category) screen.
    /// Order: Categories → DJ → Start/Stop → Skip → Queue items → Settings → Fullscreen → SearchBox → Home → Search → New Playlist
    /// </summary>
    private List<NavEntry> BuildNavRing(JukeboxViewModel vm)
    {
        var ring = new List<NavEntry>();

        // Categories
        for (int i = 0; i < vm.Categories.Count; i++)
        {
            var catIndex = i;
            var cat = vm.Categories[catIndex];
            var el = GetCategoryElement(catIndex);
            if (cat.IsNewPlaylist)
                ring.Add(new NavEntry(el, cat.Name, () => NewPlaylist_Click(this, new RoutedEventArgs())));
            else
                ring.Add(new NavEntry(el, cat.Name, () => vm.SelectCategoryCommand.Execute(cat)));
        }

        // Now playing controls
        ring.Add(new NavEntry(DjButton, "DJ", () => DjButton.Command?.Execute(DjButton.CommandParameter)));
        ring.Add(new NavEntry(StartStopButton, "Start/Stop", () => StartStopButton.Command?.Execute(StartStopButton.CommandParameter)));
        if (SkipButton.Visibility == Visibility.Visible)
            ring.Add(new NavEntry(SkipButton, "Skip", () => SkipButton.Command?.Execute(SkipButton.CommandParameter)));

        // Queue items
        for (int i = 0; i < vm.Queue.Count; i++)
        {
            var qIdx = i;
            ring.Add(new NavEntry(null, $"Queue: {vm.Queue[qIdx].Title}",
                () => { /* selecting a queue item does nothing special */ }));
        }

        // Toolbar
        ring.Add(new NavEntry(SettingsButtonCtrl, "Settings", OpenSettings));
        ring.Add(new NavEntry(FullscreenButton, "Fullscreen", ToggleExpand));
        ring.Add(new NavEntry(SearchBox, "Search Box", () => { var tb = SearchBox.Template.FindName("PART_EditableTextBox", SearchBox) as TextBox; tb?.Focus(); }));
        ring.Add(new NavEntry(HomeButton, "Home", () => HomeButton.Command?.Execute(HomeButton.CommandParameter)));
        ring.Add(new NavEntry(SearchButtonCtrl, "Search", () => SearchButton_Click(SearchButtonCtrl, new RoutedEventArgs())));

        return ring;
    }

    private record NavEntry(FrameworkElement? Element, string Label, Action Activate);

    private void NavigateSelection(int delta, JukeboxViewModel vm)
    {
        if (vm.ShowCategories)
        {
            var ring = BuildNavRing(vm);
            if (ring.Count == 0) return;
            ClearNavHighlight();
            _navIndex = ((_navIndex + delta) % ring.Count + ring.Count) % ring.Count;
            ApplyNavHighlight(vm);
        }
        else if (_inActionMode)
        {
            // Left/Right within action buttons
            NavigateActionButtons(delta, vm);
        }
        else
        {
            int count = vm.SearchResults.Count;
            if (count == 0) return;
            _resultsIndex += delta;

            if (_resultsIndex >= count)
            {
                if (vm.CanLoadMore && !vm.IsSearching)
                    vm.LoadMoreResultsCommand.Execute(null);
                _resultsIndex = count - 1;
            }
            else if (_resultsIndex < 0)
            {
                _resultsIndex = 0;
            }

            ResultsList.SelectedIndex = _resultsIndex;
            ResultsList.ScrollIntoView(ResultsList.SelectedItem);
        }
    }

    private void ActivateSelection(JukeboxViewModel vm)
    {
        if (vm.ShowCategories)
        {
            var ring = BuildNavRing(vm);
            if (_navIndex >= 0 && _navIndex < ring.Count)
            {
                ClearNavHighlight();
                ring[_navIndex].Activate();
            }
        }
        else if (_inActionMode)
        {
            // Activate the highlighted action button
            ActivateActionButton();
        }
        else
        {
            // For Plex artists/albums/hubs/playlists, select triggers drill-down directly
            if (_resultsIndex >= 0 && _resultsIndex < vm.SearchResults.Count)
            {
                var item = vm.SearchResults[_resultsIndex];
                if (item.PlexItemType is PlexItemType.Artist or PlexItemType.Album or PlexItemType.Hub or PlexItemType.Playlist)
                {
                    vm.PlayNowCommand.Execute(item);
                    return;
                }
                EnterActionMode(vm);
            }
        }
    }

    private void QueueSelected(JukeboxViewModel vm)
    {
        if (!vm.ShowCategories && _resultsIndex >= 0 && _resultsIndex < vm.SearchResults.Count)
            vm.AddToQueueCommand.Execute(vm.SearchResults[_resultsIndex]);
    }

    private void AddToPlaylistSelected(JukeboxViewModel vm)
    {
        if (!vm.ShowCategories && _resultsIndex >= 0 && _resultsIndex < vm.SearchResults.Count)
            vm.AddToPlaylistCommand.Execute(vm.SearchResults[_resultsIndex]);
    }

    private void ApplyNavHighlight(JukeboxViewModel vm)
    {
        var ring = BuildNavRing(vm);
        if (_navIndex < 0 || _navIndex >= ring.Count) return;

        var entry = ring[_navIndex];
        vm.StatusText = $"▸ {entry.Label}";

        if (entry.Element != null)
        {
            entry.Element.Effect = NavHighlightEffect;
            _highlightedElement = entry.Element;
            entry.Element.BringIntoView();
        }
    }

    private void ClearNavHighlight()
    {
        if (_highlightedElement != null)
        {
            _highlightedElement.Effect = null;
            _highlightedElement = null;
        }
    }

    // ── Action-button sub-navigation for result items ──

    private void EnterActionMode(JukeboxViewModel vm)
    {
        var container = ResultsList.ItemContainerGenerator.ContainerFromIndex(_resultsIndex) as FrameworkElement;
        if (container == null) return;

        // Collect all visible buttons in the action StackPanel
        var buttons = FindVisualChildren<System.Windows.Controls.Button>(container)
            .Where(b => b.Visibility == Visibility.Visible
                     && b.Parent is StackPanel sp
                     && sp.Orientation == System.Windows.Controls.Orientation.Horizontal)
            .ToList();

        if (buttons.Count == 0) return;

        _inActionMode = true;
        _actionButtons = buttons;
        _actionButtonIndex = 0;
        ApplyActionHighlight(vm);
    }

    private void ExitActionMode()
    {
        ClearActionHighlight();
        _inActionMode = false;
        _actionButtons = null;
    }

    private void NavigateActionButtons(int delta, JukeboxViewModel vm)
    {
        if (_actionButtons == null || _actionButtons.Count == 0) return;

        int newIndex = _actionButtonIndex + delta;

        // Left past first button or Right past last → exit action mode
        if (newIndex < 0 || newIndex >= _actionButtons.Count)
        {
            ExitActionMode();
            return;
        }

        ClearActionHighlight();
        _actionButtonIndex = newIndex;
        ApplyActionHighlight(vm);
    }

    private void ActivateActionButton()
    {
        if (_actionButtons == null || _actionButtonIndex < 0 || _actionButtonIndex >= _actionButtons.Count)
            return;

        var btn = _actionButtons[_actionButtonIndex];

        // Some buttons use Command, others use Click handlers
        if (btn.Command != null && btn.Command.CanExecute(btn.CommandParameter))
        {
            btn.Command.Execute(btn.CommandParameter);
        }
        else
        {
            // Raise the Click event for buttons with code-behind handlers (e.g. AddToPlaylist_Click)
            btn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        }

        ExitActionMode();
    }

    private void ApplyActionHighlight(JukeboxViewModel vm)
    {
        if (_actionButtons == null || _actionButtonIndex < 0 || _actionButtonIndex >= _actionButtons.Count)
            return;

        var btn = _actionButtons[_actionButtonIndex];
        btn.Effect = NavHighlightEffect;
        _highlightedActionButton = btn;
        vm.StatusText = $"▸ {btn.ToolTip ?? btn.Content}";
    }

    private void ClearActionHighlight()
    {
        if (_highlightedActionButton != null)
        {
            _highlightedActionButton.Effect = null;
            _highlightedActionButton = null;
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) yield return t;
            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    private FrameworkElement? GetCategoryElement(int index)
    {
        var container = CategoryList.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
        if (container == null) return null;
        return FindVisualChild<System.Windows.Controls.Button>(container) ?? container;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    private void ResultsList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Auto-load more when scrolled near the bottom
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 50)
        {
            if (DataContext is JukeboxViewModel vm && vm.CanLoadMore && !vm.IsSearching)
                vm.LoadMoreResultsCommand.Execute(null);
        }
    }

    private void ResultsList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not JukeboxViewModel vm) return;
        var item = GetDataContextAtPoint<VideoItem>(ResultsList, e.GetPosition(ResultsList));
        if (item == null) return;

        vm.PlayNowCommand.Execute(item);
    }

    private void ApplyReactiveBlobs(bool enabled)
    {
        if (enabled)
        {
            if (_audioReactive == null)
            {
                _audioReactive = new AudioReactiveService();
                _audioReactive.Start();
            }
            _audioReactive.ReactivityThreshold = (float)_appSettings.ReactivityThreshold;
            _audioReactive.ReactiveSpeedMs = _appSettings.ReactiveSpeedMs;
            _audioReactive.Overdrive = (float)_appSettings.ReactiveOverdrive;
            _playfieldProxy?.SetReactiveAudio(_audioReactive);
            _backglassProxy?.SetReactiveAudio(_audioReactive);
            _topperWindow?.SetReactiveAudio(_audioReactive);
        }
        else
        {
            _playfieldProxy?.SetReactiveAudio(null);
            _backglassProxy?.SetReactiveAudio(null);
            _topperWindow?.SetReactiveAudio(null);
            _audioReactive?.Dispose();
            _audioReactive = null;
        }
    }

    public void ApplyTrackListSettings(int columns, int fontSizeModifier)
    {
        int newColumns = Math.Clamp(columns, 1, 4);
        int newFontSize = Math.Clamp(20 + fontSizeModifier, 8, 32);
        if (newColumns == _resultColumns && newFontSize == _resultFontSize)
            return;

        _resultColumns = newColumns;
        _resultFontSize = newFontSize;
        Resources["ResultTitleFontSize"] = (double)_resultFontSize;
        Resources["ResultDetailFontSize"] = (double)Math.Max(9, _resultFontSize - 2);
        Resources["NowPlayingTitleFontSize"] = (double)(_resultFontSize + 4);
        int queueMod = _appSettings?.QueueFontSizeModifier ?? 0;
        Resources["QueueFontSize"] = (double)Math.Clamp(20 + (_queuePosition == QueuePosition.Right ? -2 : 2) + queueMod, 8, 44);
        UpdateQueueThumbnailHeight();
        UpdateResultThumbnailHeight();
        UpdateResultItemWidth();
    }

    private void UpdateQueueThumbnailHeight()
    {
        double queueFontSize = (double)Resources["QueueFontSize"];
        bool isRight = _queuePosition == QueuePosition.Right;

        var ft = new FormattedText(
            "Xg", System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            queueFontSize, System.Windows.Media.Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        if (isRight)
        {
            // Two lines: title + detail (detail font is smaller)
            double detailFontSize = Math.Max(9, queueFontSize - 4);
            var ft2 = new FormattedText(
                "Xg", System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                detailFontSize, System.Windows.Media.Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double h = ft.Height + ft2.Height + 1;
            Resources["QueueThumbnailHeight"] = h;
            Resources["QueueThumbnailWidth"] = h;
        }
        else
        {
            // One line
            double h = ft.Height;
            Resources["QueueThumbnailHeight"] = h;
            Resources["QueueThumbnailWidth"] = h;
        }
    }

    private void UpdateResultThumbnailHeight()
    {
        double titleFontSize = (double)Resources["ResultTitleFontSize"];
        double detailFontSize = (double)Resources["ResultDetailFontSize"];

        var ft1 = new FormattedText(
            "Xg", System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            titleFontSize, System.Windows.Media.Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var ft2 = new FormattedText(
            "Xg", System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            detailFontSize, System.Windows.Media.Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        double h = ft1.Height + ft2.Height + 1;
        Resources["ResultThumbnailHeight"] = h;
    }

    private void UpdateResultItemWidth()
    {
        if (_resultColumns <= 1)
        {
            // Single column — clear width constraint
            ResultsList.ItemContainerStyle = MakeItemContainerStyle(double.NaN);
            return;
        }

        double availableWidth = ResultsList.ActualWidth
            - SystemParameters.VerticalScrollBarWidth - 4;
        if (availableWidth <= 0) return;

        double itemWidth = Math.Floor(availableWidth / _resultColumns);
        ResultsList.ItemContainerStyle = MakeItemContainerStyle(itemWidth);
    }

    private Style MakeItemContainerStyle(double width)
    {
        var style = new Style(typeof(System.Windows.Controls.ListBoxItem));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.HorizontalContentAlignmentProperty, System.Windows.HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(0)));
        if (!double.IsNaN(width))
            style.Setters.Add(new Setter(WidthProperty, width));
        return style;
    }

    private void OpenPresetBrowser()
    {
        if (_appSettings == null || _playfieldProxy == null) return;

        // If already open, just bring it to front
        if (_presetBrowserWindow != null)
        {
            _presetBrowserWindow.Activate();
            return;
        }

        var presetPath = !string.IsNullOrEmpty(_appSettings.ProjectMPresetPath)
            ? _appSettings.ProjectMPresetPath
            : System.IO.Path.Combine(AppContext.BaseDirectory, "Presets", "ProjectM");

        // Suppress cursor auto-hide while preset browser is open
        _cursorIdleTimer.Stop();
        _cursorHideSuppressed = true;
        ShowMouseCursor();

        var browser = new PresetBrowserWindow(presetPath, _playfieldProxy);
        browser.Owner = this;
        _presetBrowserWindow = browser;

        browser.Closed += (_, _) =>
        {
            _presetBrowserWindow = null;
            _cursorHideSuppressed = false;
            ApplyCursorHideTimeout();
            Activate();
        };

        browser.Show();
    }

    private async void OpenSettings()
    {
        if (_appSettings == null) return;

        // Pause dim idle timer and show cursor while settings is open
        _dimIdleTimer.Stop();
        _cursorIdleTimer.Stop();
        _cursorHideSuppressed = true;
        ShowMouseCursor();

        var settingsWindow = new SettingsWindow(_appSettings);
        settingsWindow.Owner = this;
        settingsWindow.SetBackglassProxy(_backglassProxy);
        settingsWindow.SetPlayfieldProxy(_playfieldProxy);
        settingsWindow.SetTopperWindow(_topperWindow);
        settingsWindow.SetDofClient(_dofClient);
        if (DataContext is JukeboxViewModel vm2)
        {
            settingsWindow.SetHistoryCount(vm2.HistoryCount);
            settingsWindow.SetCacheSize(vm2.Cache?.GetTotalSizeBytes() ?? 0);
            settingsWindow.SetThumbnailCacheSize(vm2.ThumbnailCache?.GetTotalSizeBytes() ?? 0);
            settingsWindow.SetPlaylistCacheSize(vm2.PlaylistCache?.GetSizeBytes() ?? 0);
            settingsWindow.SetPlexPlaylistCacheSize(vm2.PlexPlaylistCache?.GetSizeBytes() ?? 0);
        }
        settingsWindow.SettingsApplied += async () =>
        {
            try
            {
                await ApplySettingsFromWindow(settingsWindow);
                _dimIdleTimer.Stop();
            }
            catch (Exception ex)
            {
                DebugLog.Log("Settings", $"SettingsApplied handler failed: {ex}");
            }
        };
        settingsWindow.ShowDialog();

        Activate();

        if (settingsWindow.Saved)
        {
            try
            {
                await ApplySettingsFromWindow(settingsWindow);
            }
            catch (Exception ex)
            {
                DebugLog.Log("Settings", $"ApplySettingsFromWindow (post-close) failed: {ex}");
            }
        }

        // Resume idle timers
        _cursorHideSuppressed = false;
        if (_dimScreensaverEnabled)
        {
            _dimIdleTimer.Stop();
            _dimIdleTimer.Start();
        }
        ApplyCursorHideTimeout();
       
    }

    private void OnVideoInfoChanged(string info)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_showVideoInfo && !string.IsNullOrEmpty(info))
            {
                VideoInfoText.Text = $"[{info}]";
                VideoInfoText.Visibility = Visibility.Visible;
            }
            else
            {
                VideoInfoText.Visibility = Visibility.Collapsed;
            }
        });
    }

    private async Task ApplySettingsFromWindow(SettingsWindow settingsWindow)
    {
        if (_appSettings == null) return;

        _playfieldProxy?.SetStaticImage(_appSettings.PlayfieldStaticImagePath);
        _playfieldProxy?.SetVideoPath(_appSettings.PlayfieldVideoPath);
        _playfieldProxy?.SetMode(settingsWindow.SelectedPlayfieldMode);
        _showVideoInfo = _appSettings.ShowVideoInfo;
        _backglassProxy?.SetShowVideoInfo(_appSettings.ShowVideoInfo);
        if (!_showVideoInfo) { VideoInfoText.Visibility = Visibility.Collapsed; VideoInfoText.Text = ""; }
        TitleTextBlock.Text = _appSettings.TitleText;
        _backglassProxy?.SetScreensaverSettings(_appSettings.ScreensaverIntensity, _appSettings.ScreensaverSpeed);
        if (settingsWindow.LogoChanged)
        {
            _backglassProxy?.SetLogoText(_appSettings.LogoText);
            _backglassProxy?.SetLogoSpin(_appSettings.LogoSpin);
            _backglassProxy?.SetLogoRings(_appSettings.LogoRings);
        }
        _playfieldProxy?.SetScreensaverSettings(_appSettings.ScreensaverIntensity, _appSettings.ScreensaverSpeed);
        _playfieldProxy?.SetOledSleepDefeat(_appSettings.OledSleepDefeatSeconds, _appSettings.OledSleepDefeatDurationSeconds, _appSettings.OledSleepDefeatIntensity);
        _topperWindow?.SetScreensaverSettings(_appSettings.ScreensaverIntensity, _appSettings.ScreensaverSpeed);
        if (settingsWindow.LogoChanged)
        {
            _topperWindow?.SetLogoText(_appSettings.LogoText);
            _topperWindow?.SetLogoSpin(_appSettings.LogoSpin);
            _topperWindow?.SetLogoRings(_appSettings.LogoRings);
        }
        _topperWindow?.SetDistortion(_appSettings.TopperDistortion);
        _backglassProxy?.SetLogoDim(_appSettings.BackglassLogoDimEnabled, _appSettings.BackglassLogoDimOpacity, _appSettings.BackglassLogoDimTimeoutSeconds);
        if (settingsWindow.LogoChanged)
        {
            _backglassProxy?.SetLogoMorphColor(_appSettings.BackglassLogoMorphColor);
        }
        _backglassProxy?.SetAudioOnly(_appSettings.BackglassAudioOnly);
        if (settingsWindow.LogoChanged)
        {
            if (_appSettings.ShowTopper)
                _topperWindow?.SetLogoMorphColor(_appSettings.BackglassLogoMorphColor);
            else
                _topperWindow?.SetLogoMorphColor(false);
        }
        if (settingsWindow.BackglassBlobsChanged)
        {
            _backglassProxy?.SetBlobPattern(_appSettings.BackglassBlobPattern);
            _backglassProxy?.SetBlobCount(_appSettings.BackglassBlobCount);
        }
        BlobTransition.ExcludeMandelbrotFromRandom = _appSettings.ExcludeMandelbrotFromRandom;
        MandelbrotPattern.MandelbrotMaxHz = _appSettings.MandelbrotMaxHz;
        MandelbrotPattern.RenderScale = Math.Clamp(_appSettings.MandelbrotRenderScale, 0.2, 1.0);
        MandelbrotPattern.AdaptiveIterations = _appSettings.MandelbrotAdaptiveIterations;
        MandelbrotPattern.MaxIterations = Math.Clamp(_appSettings.MandelbrotMaxIterations, 64, 8192);
        MandelbrotPattern.UseGpu = _appSettings.MandelbrotUseGpu == 1;
        MandelbrotPattern.Dimming = _appSettings.MandelbrotDimming;
        if (settingsWindow.MandelbrotSettingsChanged)
        {
            _playfieldProxy?.RestartMandelbrot();
            _backglassProxy?.RestartMandelbrot();
            _topperWindow?.Dispatcher.BeginInvoke(() => _topperWindow.RestartMandelbrot());
            RestartMandelbrot();
        }
        BlobTransition.ExcludeProjectMFromRandom = _appSettings.ExcludeProjectMFromRandom;
        ProjectMRenderer.PresetDuration = _appSettings.ProjectMPresetDuration;
        ProjectMRenderer.SoftCutDuration = _appSettings.ProjectMSoftCutDuration;
        ProjectMRenderer.HardCutEnabled = _appSettings.ProjectMHardCutEnabled;
        ProjectMRenderer.MeshSize = (uint)Math.Clamp(_appSettings.ProjectMMeshSize, 16, 128);
        ProjectMRenderer.BeatSensitivity = Math.Clamp(_appSettings.ProjectMBeatSensitivity, 0f, 3f);
        ProjectMRenderer.RenderScale = Math.Clamp(_appSettings.ProjectMRenderScale, 0.25, 1.0);
        ProjectMRenderer.PresetPath = !string.IsNullOrEmpty(_appSettings.ProjectMPresetPath)
            ? _appSettings.ProjectMPresetPath
            : System.IO.Path.Combine(AppContext.BaseDirectory, "Presets", "ProjectM");
        ProjectMRenderer.TexturePath = !string.IsNullOrEmpty(_appSettings.ProjectMTexturePath)
            ? _appSettings.ProjectMTexturePath
            : System.IO.Path.Combine(AppContext.BaseDirectory, "presets", "textures");
        ProjectMRenderer.EnabledFolders = _appSettings.ProjectMEnabledFolders;
        ProjectMRenderer.ColorSampleDelaySeconds = Math.Clamp(_appSettings.ProjectMColorSampleDelaySeconds, 0.1, 30.0);
        ProjectMRenderer.BlackCheckRequiredHits = Math.Clamp(_appSettings.ProjectMPresetMonitorBlackHits, 1, 20);
        ProjectMRenderer.BlackCheckIntervalSeconds = Math.Clamp(_appSettings.ProjectMPresetMonitorIntervalSeconds, 0.5, 30.0);
        if (settingsWindow.ProjectMSettingsChanged)
        {
            _playfieldProxy?.RestartProjectM();
            _backglassProxy?.RestartProjectM();
            _topperWindow?.Dispatcher.BeginInvoke(() => _topperWindow.RestartProjectM());
            RestartProjectM();
        }
        if (settingsWindow.PlayfieldBlobsChanged)
        {
            _playfieldProxy?.SetBlobCount(_appSettings.PlayfieldBlobCount);
            _playfieldProxy?.SetBlobPattern(_appSettings.PlayfieldBlobPattern);
        }
        if (settingsWindow.PlayfieldRotationChanged)
            _playfieldProxy?.SetRotation(_appSettings.PlayfieldRotation);
        if (settingsWindow.TopperBlobsChanged)
        {
            _topperWindow?.SetBlobCount(_appSettings.TopperBlobCount);
            _topperWindow?.SetBlobPattern(_appSettings.TopperBlobPattern);
        }
        if (settingsWindow.DmdBlobsChanged)
        {
            SetBlobCount(_appSettings.DmdBlobCount);
            SetBlobPattern(_appSettings.DmdBlobPattern);
        }
        if (settingsWindow.DmdRotationChanged)
            SetDmdRotation(_appSettings.DmdRotation);
        SetQueuePosition(_appSettings.DmdQueuePosition);
        if (_appSettings.DmdScreensaver != (ScreensaverCanvas.Visibility == Visibility.Visible))
            SetDmdScreensaver(_appSettings.DmdScreensaver);
        SetDmdScreensaverDim(_appSettings.DmdScreensaverDimEnabled, _appSettings.DmdScreensaverDimOpacity, _appSettings.DmdScreensaverDimTimeoutSeconds, _appSettings.DmdScreensaverDimDarkBlobs, _appSettings.SwapPlayfieldDmdOnDim, _appSettings.ApplyDefaultDmdOnSwap);
        SetScreensaverSettings(_appSettings.ScreensaverIntensity, _appSettings.ScreensaverSpeed);
        if (settingsWindow.ReactiveBlobsChanged)
            ApplyReactiveBlobs(_appSettings.ReactiveBlobs);
        ApplyResizable(_appSettings.ResizableWindows);

        // Show/hide backglass window
        if (_appSettings.ShowBackglass)
            _backglassProxy?.Show();
        else
            _backglassProxy?.Hide();

        // Show/hide playfield window
        if (_appSettings.ShowPlayfield)
            _playfieldProxy?.Show();
        else
            _playfieldProxy?.Hide();

        // Show/hide topper window
        if (_appSettings.ShowTopper)
            _topperWindow?.Show();
        else
            _topperWindow?.Hide();

        // Update cache settings
        if (DataContext is JukeboxViewModel vm)
        {
            vm.SetHiddenCategories(_appSettings.HiddenCategories);
            vm.Cache?.UpdateSettings(_appSettings.CacheEnabled, _appSettings.CacheMaxSizeGb, _appSettings.CacheMaxClipLengthMinutes);
            vm.SetupPrefetch(_appSettings.PrefetchEnabled);
            vm.SetupThumbnailCache(_appSettings.ThumbnailCacheEnabled, _appSettings.ThumbnailCacheMaxSizeMb);
            vm.SetupPlaylistCache(_appSettings.PlaylistCacheEnabled, _appSettings.PlaylistCacheMaxAgeHours);
            vm.SetupPlexPlaylistCache(_appSettings.PlexPlaylistCacheEnabled, _appSettings.PlexPlaylistCacheMaxAgeHours);
            ThumbnailCacheConverter.Cache = vm.ThumbnailCache;
            vm.VideoQuality = _appSettings.VideoQuality;
            vm.StereoAudio = _appSettings.StereoAudio;
            vm.NetworkCachingMs = _appSettings.NetworkCachingMs;
            vm.LiveCachingMs = _appSettings.LiveCachingMs;
            vm.FileCachingMs = _appSettings.FileCachingMs;
            vm.HttpReconnect = _appSettings.HttpReconnect;
            vm.CacheMode = _appSettings.CacheMode;
            if (!string.IsNullOrWhiteSpace(_appSettings.PlexServerUrl) && !string.IsNullOrWhiteSpace(_appSettings.PlexToken))
                vm.ConfigurePlex(_appSettings.PlexServerUrl, _appSettings.PlexToken, _appSettings.PlexLibraries, _appSettings.PlexStereoAudio);
        }

        if (settingsWindow.WindowsReset)
            ResetAllWindows();

        // Only touch DOF if settings actually changed
        if (settingsWindow.DofSettingsChanged)
        {
            ApplyDofStartup(_appSettings.DofEnabled, _appSettings.DofColorBand && _appSettings.ShowPlayfield);

            // If DOF fully disabled and bridge is running, shut it down
            if (!_appSettings.DofEnabled && _dofClient != null)
            {
                await _dofClient.DisposeAsync();
                _dofClient = null;
            }
        }

        ApplyTrackListSettings(_appSettings.ResultColumns, _appSettings.ResultFontSizeModifier);
        SetPlayButtonSize(_appSettings.DmdPlayButtonSizeModifier);
        SetQueueButtonSize(_appSettings.DmdQueueButtonSizeModifier);
        SetGenreIconSize(_appSettings.DmdGenreIconSizeModifier);
        SetTrackButtonSize(_appSettings.DmdTrackButtonSizeModifier);
        SetMinorButtonLocation(_appSettings.DmdMinorButtonLocation);
        SetShowStatusText(_appSettings.ShowStatusText);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>
    /// Ensures the DOF bridge process is running and connected asynchronously. Returns true if ready.
    /// </summary>
    private async Task<bool> EnsureDofStartedAsync()
    {
        await _dofStartLock.WaitAsync();
        try
        {
            if (_dofClient?.IsConnected == true)
                return true;

            if (_dofClient != null)
                await _dofClient.DisposeAsync();
            _dofClient = new DofClient();
            var romName = _appSettings?.DofRomName ?? "vpinjukebox";
            var simulatorMode = _appSettings?.DofSimulator == true;
            if (!await _dofClient.StartAsync(romName, simulatorMode))
            {
                DebugLog.Log("[DOF]", "Failed to start bridge");
                _dofClient = null;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Log("[DOF]", $"Failed to start bridge: {ex.Message}");
            _dofClient = null;
            return false;
        }
        finally
        {
            _dofStartLock.Release();
        }
    }

    private async void ApplyDofStartup(bool enabled, bool colorBandEnabled = false)
    {
        _dofStartupEnabled = enabled;

        if (enabled)
        {
            bool wasAlreadyConnected = _dofClient?.IsConnected == true;

            try
            {
                if (await EnsureDofStartedAsync())
                {
                    if (!wasAlreadyConnected)
                        _ = _dofClient!.TriggerPulseAsync('E', 111);
                }
                else
                    _dofStartupEnabled = false;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[DOF]", $"Startup trigger failed: {ex.Message}");
            }

            // Delay color band activation so startup effects have time to run (skip if already running)
            if (_dofStartupEnabled && colorBandEnabled)
            {
                if (!wasAlreadyConnected)
                    await Task.Delay(1000);
                ApplyDofColorBand(true);
            }
            else
            {
                ApplyDofColorBand(false);
            }
        }
        else
        {
            ApplyDofColorBand(false);
        }
    }

    private async void ApplyDofColorBand(bool enabled)
    {
        _dofColorBandEnabled = enabled;

        if (enabled)
        {
            if (!await EnsureDofStartedAsync())
                _dofColorBandEnabled = false;
        }
        else
        {
            // Turn off any active color band trigger
            if (_dofClient?.IsConnected == true && _lastDofColorNumber >= 0)
            {
                _dofClient.TriggerAsync('E', _lastDofColorNumber, 0);
                _lastDofColorNumber = -1;
            }
        }
    }

    private void OnPlayfieldColorBandChanged(RoygbivColor band)
    {
        // This fires on the playfield's dispatcher thread — marshal to our thread
        Dispatcher.BeginInvoke(() =>
        {
            if (!_dofColorBandEnabled || _dofClient?.IsConnected != true)
                return;

            int newNumber = band.ToDofNumber();
            if (newNumber == _lastDofColorNumber)
                return;

            // Throttle color changes — effects need time to run
            var now = DateTime.UtcNow;
            if ((now - _lastDofColorChangeTime).TotalMilliseconds < 2000)
                return;
            _lastDofColorChangeTime = now;

            // Turn off previous color
            if (_lastDofColorNumber >= 0)
                _dofClient.TriggerAsync('E', _lastDofColorNumber, 0);

            // Turn on new color
            _dofClient.TriggerAsync('E', newNumber, 1);
            _lastDofColorNumber = newNumber;
        });
    }

    public void SetPlayButtonSize(int modifier)
    {
        double fontSize = 28 + modifier;
        // Button dimension = font size + padding to keep icons centered with consistent hit targets
        double buttonDim = fontSize + 24;
        Resources["PlayButtonFontSize"] = fontSize;
        Resources["PlayButtonSize"] = buttonDim;

        // Row 2 (secondary) buttons track row 1 minus an offset
        const double smallButtonSizeOffset = 22;
        const double smallButtonFontOffset = 12; // smaller than size offset = less padding, larger icons
        Resources["SmallButtonFontSize"] = Math.Max(4, fontSize - smallButtonFontOffset);
        Resources["SmallButtonSize"] = Math.Max(8, buttonDim - smallButtonSizeOffset);
    }

    public void SetQueueButtonSize(int modifier)
    {
        double fontSize = 14 + modifier;
        double buttonDim = fontSize + 8;
        Resources["QueueButtonFontSize"] = Math.Max(4, fontSize);
        Resources["QueueButtonSize"] = Math.Max(8, buttonDim);
        Resources["QueueHeaderFontSize"] = Math.Max(4, fontSize);
    }

    public void SetGenreIconSize(int modifier)
    {
        double scale = 1.0 + modifier / 20.0;
        Resources["GenreButtonWidth"] = 175.0 * scale;
        Resources["GenreButtonHeight"] = 90.0 * scale;
        Resources["GenreIconFontSize"] = 32.0 * scale;
        Resources["GenreLabelFontSize"] = 13.0 * scale;
    }

    public void SetTrackButtonSize(int modifier)
    {
        double fontSize = Math.Clamp(18 + modifier, 8, 42);
        double padding = Math.Max(4, 12 + modifier / 2.0);
        Resources["TrackButtonFontSize"] = fontSize;
        Resources["TrackButtonPadding"] = new Thickness(padding, padding / 2, padding, padding / 2);
    }

    public void SetMinorButtonLocation(MinorButtonLocation location)
    {
        if (location == MinorButtonLocation.Queue)
        {
            // Guard: if buttons are already in the queue header, nothing to do
            if (DjButton.Parent == QueueHeaderPanel)
                return;

            // Hide seek buttons and the entire MinorButtonsPanel in the playbar
            SeekBackMinorButton.Visibility = Visibility.Collapsed;
            SeekForwardMinorButton.Visibility = Visibility.Collapsed;
            MinorButtonsPanel.Visibility = Visibility.Collapsed;

            // Insert DJ, Repeat, Shuffle into the queue header, right-docked so they
            // sit next to the Playlist/Clear buttons (right-aligned).
            // Right-docked items render right-to-left in child order, so:
            //   0:Clear  1:Playlist  2:pipe  3:Shuffle  4:Repeat  5:DJ
            // gives visual left-to-right: DJ Repeat Shuffle | Playlist Clear

            // First insert buttons in forward order at index 2 (Shuffle, Repeat, DJ)
            foreach (var btn in new[] { ShuffleButton, RepeatButton, DjButton })
            {
                var parent = btn.Parent as System.Windows.Controls.Panel;
                parent?.Children.Remove(btn);
                DockPanel.SetDock(btn, System.Windows.Controls.Dock.Right);
                btn.Margin = new Thickness(2, 0, 2, 0);
                QueueHeaderPanel.Children.Insert(2, btn);
            }

            // Then insert pipe separator at index 2 (pushes buttons to 3,4,5)
            var pipe = new System.Windows.Controls.TextBlock
            {
                Text = "|",
                Foreground = (System.Windows.Media.Brush)FindResource("TextDimBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
                Tag = "QueuePipeSeparator"
            };
            pipe.SetBinding(VisibilityProperty,
                new System.Windows.Data.Binding("HasQueueItems")
                {
                    Converter = (System.Windows.Data.IValueConverter)FindResource("BoolVis")
                });
            DockPanel.SetDock(pipe, System.Windows.Controls.Dock.Right);
            QueueHeaderPanel.Children.Insert(2, pipe);

            // Shorten queue header buttons to icon-only and match DJ/Repeat/Shuffle style
            QueueClearButton.Content = "✕";
            QueuePlaylistButton.Content = "\U0001F4CB";

            // Apply uniform style to all queue header buttons
            foreach (var btn in new[] { DjButton, RepeatButton, ShuffleButton, QueuePlaylistButton, QueueClearButton })
            {
                btn.ClearValue(System.Windows.Controls.Button.BackgroundProperty);
                btn.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextBrush");
                btn.BorderThickness = new Thickness(0);
                btn.Padding = new Thickness(0);
                btn.Margin = new Thickness(2, 0, 2, 0);
                btn.SetResourceReference(System.Windows.Controls.Button.WidthProperty, "QueueButtonSize");
                btn.SetResourceReference(System.Windows.Controls.Button.HeightProperty, "QueueButtonSize");
                btn.SetResourceReference(System.Windows.Controls.Button.FontSizeProperty, "QueueButtonFontSize");
            }
        }
        else
        {
            // Remove pipe separator if present
            for (int i = QueueHeaderPanel.Children.Count - 1; i >= 0; i--)
            {
                if (QueueHeaderPanel.Children[i] is System.Windows.Controls.TextBlock tb && tb.Tag as string == "QueuePipeSeparator")
                {
                    QueueHeaderPanel.Children.RemoveAt(i);
                    break;
                }
            }

            // Move DJ, Repeat, Shuffle back into MinorButtonsPanel if they were moved out
            foreach (var btn in new[] { DjButton, RepeatButton, ShuffleButton })
            {
                var parent = btn.Parent as System.Windows.Controls.Panel;
                if (parent == QueueHeaderPanel)
                    parent.Children.Remove(btn);
            }

            // Rebuild MinorButtonsPanel order: SeekBack, DJ, Repeat, Shuffle, SeekForward
            MinorButtonsPanel.Children.Clear();
            SeekBackMinorButton.Visibility = Visibility.Visible;
            SeekForwardMinorButton.Visibility = Visibility.Visible;
            foreach (var btn in new FrameworkElement[] { SeekBackMinorButton, DjButton, RepeatButton, ShuffleButton, SeekForwardMinorButton })
            {
                var p = btn.Parent as System.Windows.Controls.Panel;
                p?.Children.Remove(btn);
                btn.Margin = new Thickness(3, 0, 3, 0);
                MinorButtonsPanel.Children.Add(btn);
            }
            MinorButtonsPanel.Visibility = Visibility.Visible;

            // Restore queue header button text and original style
            QueueClearButton.Content = "✕ Clear";
            QueuePlaylistButton.Content = "\U0001F4CB Playlist";
            foreach (var btn in new[] { QueuePlaylistButton, QueueClearButton })
            {
                btn.FontSize = 11;
                btn.Padding = new Thickness(4, 1, 4, 1);
                btn.Background = System.Windows.Media.Brushes.Transparent;
                btn.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextDimBrush");
                btn.BorderThickness = new Thickness(0);
                btn.ClearValue(System.Windows.Controls.Button.WidthProperty);
                btn.ClearValue(System.Windows.Controls.Button.HeightProperty);
                btn.Margin = new Thickness(0);
            }
        }
    }

    public void SetShowStatusText(bool visible)
    {
        StatusBarText.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetDmdRotation(int degrees)
    {
        degrees = degrees switch { 90 => 90, 180 => 180, 270 => 270, _ => 0 };
        if (Content is FrameworkElement root)
            root.LayoutTransform = degrees == 0 ? Transform.Identity : new RotateTransform(degrees);
    }

    public void SetBlobCount(int count)
    {
        _ssBlobCount = Math.Clamp(count, 0, 25);
        if (ScreensaverCanvas.Visibility == Visibility.Visible)
        {
            CreateScreensaverBlobs();
            _ssColorTimer.Start();
        }
    }

    public void SetDmdScreensaver(bool enabled)
    {
        _ssColorTimer.Stop();

        if (enabled)
        {
            ScreensaverCanvas.Visibility = Visibility.Visible;
            if (ScreensaverCanvas.ActualWidth > 0 && ScreensaverCanvas.ActualHeight > 0)
            {
                CreateScreensaverBlobs();
            }
            else
            {
                // Defer until the canvas has a valid size after layout
                void handler(object? s, EventArgs a)
                {
                    ScreensaverCanvas.SizeChanged -= handler;
                    CreateScreensaverBlobs();
                }
                ScreensaverCanvas.SizeChanged += handler;
            }
            _ssColorTimer.Start();
        }
        else
        {
            ScreensaverCanvas.Visibility = Visibility.Collapsed;
            _ssCurrentPattern?.Dispose();
            _ssCurrentPattern = null;
            _ssDarkBlobStart = -1;
        }
    }

    public void SetDmdScreensaverDim(bool enabled, int opacityPercent, int timeoutSeconds, bool darkBlobs = true, bool swapPlayfieldDmd = false, bool applyDefaultDmd = false)
    {
        _dimScreensaverEnabled = enabled;
        _dimDarkBlobsEnabled = darkBlobs;
        _swapPlayfieldDmdOnDim = swapPlayfieldDmd;
        _applyDefaultDmdOnSwap = applyDefaultDmd;
        _dimOpacity = Math.Clamp(opacityPercent / 100.0, 0.0, 1.0);
        _dimIdleTimer.Stop();

        if (enabled)
        {
            _dimIdleTimer.Interval = TimeSpan.FromSeconds(Math.Max(10, timeoutSeconds));
            _dimIdleTimer.Start();
        }
        else
        {
            UndimScreen();
        }
    }

    private void DimIdleTimer_Tick(object? sender, EventArgs e)
    {
        _dimIdleTimer.Stop();
        if (!_dimScreensaverEnabled || _isDimmed) return;

        _isDimmed = true;

        // Ensure screensaver blobs exist
        var ssBlobs = _ssCurrentPattern?.Blobs;
        if ((ssBlobs == null || ssBlobs.Count == 0) && ScreensaverCanvas.ActualWidth > 0)
        {
            CreateScreensaverBlobs();
            ssBlobs = _ssCurrentPattern?.Blobs;
        }
        if (!_ssColorTimer.IsEnabled) _ssColorTimer.Start();

        // Hide colored blobs
        if (ssBlobs != null)
        {
            for (int i = 0; i < ssBlobs.Count; i++)
            {
                if (_ssDarkBlobStart < 0 || i < _ssDarkBlobStart)
                    ssBlobs[i].Visibility = Visibility.Collapsed;
            }
        }

        // Move dark blobs to the overlay canvas above the dim layer
        if (_dimDarkBlobsEnabled && _ssDarkBlobStart >= 0 && ssBlobs != null)
        {
            DimBlobCanvas.Visibility = Visibility.Visible;
            for (int i = _ssDarkBlobStart; i < ssBlobs.Count; i++)
            {
                var blob = ssBlobs[i];
                double left = Canvas.GetLeft(blob);
                double top = Canvas.GetTop(blob);
                ScreensaverCanvas.Children.Remove(blob);
                Canvas.SetLeft(blob, left);
                Canvas.SetTop(blob, top);
                DimBlobCanvas.Children.Add(blob);
            }
        }

        // Animate dim overlay in
        DimOverlay.Visibility = Visibility.Visible;
        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = _dimOpacity,
            Duration = TimeSpan.FromSeconds(1.5),
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
        };

        // After dim completes, swap Playfield and DMD window positions if enabled
        anim.Completed += (_, _) =>
        {
            if (_swapPlayfieldDmdOnDim && _playfieldProxy != null && _appSettings?.ShowPlayfield == true && !_isSwapped)
            {
                _isSwapped = true;
                _applyingSwapLayout = true;

                // Animate blur IN at current positions, then swap once fully blurred
                AnimateApplyBlur(35, 0.5);
                _playfieldProxy.AnimateApplyBlur(35, 0.5, () =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        // Apply default DMD settings temporarily
                        if (_applyDefaultDmdOnSwap && _appSettings != null)
                        {
                            _preSwapResultColumns = _appSettings.ResultColumns;
                            _preSwapResultFontSizeModifier = _appSettings.ResultFontSizeModifier;
                            _preSwapDmdRotation = _appSettings.DmdRotation;
                            _preSwapQueuePosition = _appSettings.DmdQueuePosition;
                            _preSwapPlayButtonSizeModifier = _appSettings.DmdPlayButtonSizeModifier;
                            _preSwapQueueButtonSizeModifier = _appSettings.DmdQueueButtonSizeModifier;
                            _preSwapGenreIconSizeModifier = _appSettings.DmdGenreIconSizeModifier;
                            _preSwapTrackButtonSizeModifier = _appSettings.DmdTrackButtonSizeModifier;
                            _preSwapQueueFontSizeModifier = _appSettings.QueueFontSizeModifier;
                            _preSwapQueueSplitterSize = _appSettings.DmdQueueSplitterSize;
                            _preSwapPlayfieldRotation = _appSettings.PlayfieldRotation;

                            var d = AppSettings.Defaults;
                            ApplyTrackListSettings(d.ResultColumns, d.ResultFontSizeModifier);
                            SetDmdRotation(d.DmdRotation);
                            SetPlayButtonSize(d.DmdPlayButtonSizeModifier);
                            SetQueueButtonSize(d.DmdQueueButtonSizeModifier);
                            SetGenreIconSize(d.DmdGenreIconSizeModifier);
                            SetTrackButtonSize(d.DmdTrackButtonSizeModifier);
                            _appSettings.QueueFontSizeModifier = d.QueueFontSizeModifier;
                            _appSettings.DmdQueueSplitterSize = d.DmdQueueSplitterSize;
                            SetQueuePosition(d.DmdQueuePosition);
                            _playfieldProxy!.SetRotation(d.PlayfieldRotation);
                        }

                        // Swap while both windows are fully blurred
                        SwapWithPlayfield();

                        // Defer de-blur so windows fully settle at new positions
                        var fadeInTimer = new DispatcherTimer(DispatcherPriority.Normal)
                        {
                            Interval = TimeSpan.FromMilliseconds(100)
                        };
                        fadeInTimer.Tick += (_, _) =>
                        {
                            fadeInTimer.Stop();
                            AnimateRemoveBlur(0.8);
                            _playfieldProxy!.AnimateRemoveBlur(0.8, () =>
                            {
                                Dispatcher.BeginInvoke(() =>
                                {
                                    _applyingSwapLayout = false;
                                    Activate();
                                    HideMouseCursor();
                                    _playfieldProxy!.HideCursor();
                                });
                            });
                        };
                        fadeInTimer.Start();
                    });
                });
            }
        };

        DimOverlay.BeginAnimation(OpacityProperty, anim);
    }

    private void ResetDimIdle()
    {
        if (_applyingSwapLayout) return;

        if (_isDimmed)
            UndimScreen();

        if (_dimScreensaverEnabled)
        {
            _dimIdleTimer.Stop();
            _dimIdleTimer.Start();
        }
    }

    private void WireDimIdleEvents(Window window)
    {
        WpfPoint lastPos = default;
        window.PreviewMouseMove += (_, e) =>
        {
            var pos = e.GetPosition(window);
            if (Math.Abs(pos.X - lastPos.X) > 3 || Math.Abs(pos.Y - lastPos.Y) > 3)
            {
                lastPos = pos;
                Dispatcher.BeginInvoke(() =>
                {
                    ShowMouseCursor();
                    ResetDimIdle();
                });
            }
        };
        window.PreviewMouseDown += (_, _) => Dispatcher.BeginInvoke(ResetDimIdle);
        window.PreviewKeyDown += (_, _) => Dispatcher.BeginInvoke(ResetDimIdle);
    }

    private void UndimScreen()
    {
        if (!_isDimmed && DimOverlay.Visibility == Visibility.Collapsed) return;

        _isDimmed = false;

        // Move dark blobs back to ScreensaverCanvas
        var ssBlobs = _ssCurrentPattern?.Blobs;
        if (_ssDarkBlobStart >= 0 && ssBlobs != null)
        {
            for (int i = _ssDarkBlobStart; i < ssBlobs.Count; i++)
            {
                var blob = ssBlobs[i];
                if (blob.Parent == DimBlobCanvas)
                {
                    double left = Canvas.GetLeft(blob);
                    double top = Canvas.GetTop(blob);
                    DimBlobCanvas.Children.Remove(blob);
                    Canvas.SetLeft(blob, left);
                    Canvas.SetTop(blob, top);
                    ScreensaverCanvas.Children.Add(blob);
                    System.Windows.Controls.Panel.SetZIndex(blob, -1);
                }
            }
            DimBlobCanvas.Visibility = Visibility.Collapsed;
        }

        // Restore colored blobs
        if (ssBlobs != null)
        {
            for (int i = 0; i < ssBlobs.Count; i++)
            {
                if (_ssDarkBlobStart < 0 || i < _ssDarkBlobStart)
                    ssBlobs[i].Visibility = Visibility.Visible;
            }
        }

        // If DMD screensaver is not enabled, hide the background canvas and stop timer
        if (_appSettings != null && !_appSettings.DmdScreensaver)
        {
            ScreensaverCanvas.Visibility = Visibility.Collapsed;
            _ssColorTimer.Stop();
        }

        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromSeconds(0.5),
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
        };
        anim.Completed += (_, _) =>
        {
            DimOverlay.Visibility = Visibility.Collapsed;
        };
        DimOverlay.BeginAnimation(OpacityProperty, anim);

        // Swap back Playfield and DMD window positions
        if (_isSwapped && _playfieldProxy != null)
        {
            _applyingSwapLayout = true;

            // Animate blur IN at current (swapped) positions, then swap once fully blurred
            AnimateApplyBlur(35, 0.5);
            _playfieldProxy.AnimateApplyBlur(35, 0.5, () =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    // Restore original DMD settings before swapping back
                    if (_applyDefaultDmdOnSwap && _appSettings != null)
                    {
                        _appSettings.QueueFontSizeModifier = _preSwapQueueFontSizeModifier;
                        _appSettings.DmdQueueSplitterSize = _preSwapQueueSplitterSize;
                        ApplyTrackListSettings(_preSwapResultColumns, _preSwapResultFontSizeModifier);
                        SetDmdRotation(_preSwapDmdRotation);
                        SetPlayButtonSize(_preSwapPlayButtonSizeModifier);
                        SetQueueButtonSize(_preSwapQueueButtonSizeModifier);
                        SetGenreIconSize(_preSwapGenreIconSizeModifier);
                        SetTrackButtonSize(_preSwapTrackButtonSizeModifier);
                        SetQueuePosition(_preSwapQueuePosition);
                        _playfieldProxy!.SetRotation(_preSwapPlayfieldRotation);
                    }

                    _isSwapped = false;
                    SwapWithPlayfield();

                    // Defer de-blur so windows fully settle at new positions
                    var fadeInTimer = new DispatcherTimer(DispatcherPriority.Normal)
                    {
                        Interval = TimeSpan.FromMilliseconds(100)
                    };
                    fadeInTimer.Tick += (_, _) =>
                    {
                        fadeInTimer.Stop();
                        AnimateRemoveBlur(0.8);
                        _playfieldProxy!.AnimateRemoveBlur(0.8, () =>
                        {
                            Dispatcher.BeginInvoke(() =>
                            {
                                _applyingSwapLayout = false;
                                Activate();
                                _playfieldProxy!.ShowCursor();
                            });
                        });
                    };
                    fadeInTimer.Start();
                });
            });
        }
    }

    private static void SwapWindowPositions(Window a, Window b)
    {
        var aLeft = a.Left;
        var aTop = a.Top;
        var aWidth = a.Width;
        var aHeight = a.Height;

        a.Left = b.Left;
        a.Top = b.Top;
        a.Width = b.Width;
        a.Height = b.Height;

        b.Left = aLeft;
        b.Top = aTop;
        b.Width = aWidth;
        b.Height = aHeight;
    }

    /// <summary>
    /// Cross-thread swap of DMD and playfield window positions.
    /// Must be called on the DMD thread.
    /// </summary>
    private void SwapWithPlayfield()
    {
        if (_playfieldProxy == null) return;

        // Read playfield bounds (synchronous cross-thread call)
        var pf = _playfieldProxy.GetBounds();

        // Set playfield to DMD's current bounds
        _playfieldProxy.SetBounds(Left, Top, Width, Height);

        // Set DMD to playfield's old bounds
        Left = pf.Left;
        Top = pf.Top;
        Width = pf.Width;
        Height = pf.Height;
    }

    private void FadeDmdToBlack(double durationSeconds, Action? onCompleted = null)
    {
        SwapFadeOverlay.Visibility = Visibility.Visible;
        var anim = new DoubleAnimation
        {
            To = 1.0,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        if (onCompleted != null)
            anim.Completed += (_, _) => onCompleted();
        SwapFadeOverlay.BeginAnimation(OpacityProperty, anim);
    }

    private void FadeDmdFromBlack(double durationSeconds, Action? onCompleted = null)
    {
        var anim = new DoubleAnimation
        {
            To = 0.0,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        anim.Completed += (_, _) =>
        {
            SwapFadeOverlay.Visibility = Visibility.Collapsed;
            onCompleted?.Invoke();
        };
        SwapFadeOverlay.BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>
    /// Applies a blur effect to the DMD window's root content to mask swap transitions.
    /// </summary>
    private void ApplySwapBlur(double radius = 35)
    {
        if (Content is FrameworkElement root)
            root.Effect = new BlurEffect { Radius = radius, RenderingBias = RenderingBias.Performance };
    }

    /// <summary>
    /// Animates a blur effect onto the DMD window's root content, then invokes a callback when complete.
    /// </summary>
    private void AnimateApplyBlur(double targetRadius, double durationSeconds, Action? onCompleted = null)
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

    /// <summary>
    /// Animates the blur away from the DMD window, revealing sharp content.
    /// </summary>
    private void AnimateRemoveBlur(double durationSeconds, Action? onCompleted = null)
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

    public void SetScreensaverSettings(double intensity, double speed)
    {
        double newIntensity = Math.Clamp(intensity, 0.05, 0.8);
        bool intensityChanged = Math.Abs(newIntensity - _ssBlobIntensity) > 0.001;
        _ssBlobIntensity = newIntensity;
        _ssBlobSpeedMultiplier = Math.Clamp(speed, 0.1, 5.0);

        if (intensityChanged && _ssCurrentPattern != null)
        {
            foreach (var blob in _ssCurrentPattern.Blobs)
                blob.Opacity = _ssBlobIntensity + _ssRng.NextDouble() * 0.1;
        }
    }

    private BlobPatternConfig MakeSsConfig()
    {
        double w = Math.Max(200, ScreensaverCanvas.ActualWidth);
        double h = Math.Max(200, ScreensaverCanvas.ActualHeight);
        const double referenceArea = 175_000.0;
        double blobScale = Math.Max(1.0, Math.Sqrt(w * h / referenceArea));

        int darkBlobCount = 4;
        return new BlobPatternConfig
        {
            Canvas = ScreensaverCanvas,
            BlobCount = _ssBlobCount + darkBlobCount,
            Intensity = _ssBlobIntensity,
            SpeedMultiplier = _ssBlobSpeedMultiplier,
            Rng = _ssRng,
            BlobSizeFactory = r => (250 + r.NextDouble() * 375) * blobScale,
            UseBitmapCache = false,
        };
    }

    private void CreateScreensaverBlobs()
    {
        int darkBlobCount = 4;
        _ssDarkBlobStart = _ssBlobCount;

        _ssCurrentPattern?.Dispose();
        _ssCurrentPattern = BlobTransition.Create(_ssBlobPattern, MakeSsConfig());
        _ssCurrentPattern.Enter(() =>
        {
            // After enter, make the last N blobs dark
            var blobs = _ssCurrentPattern?.Blobs;
            var gradBrushes = _ssCurrentPattern?.GradientBrushes;
            if (blobs == null) return;

            for (int i = _ssDarkBlobStart; i < blobs.Count; i++)
            {
                blobs[i].Opacity = 0.75 + _ssRng.NextDouble() * 0.1;
                System.Windows.Controls.Panel.SetZIndex(blobs[i], -1);

                // Adjust dark blob size
                double w = Math.Max(200, ScreensaverCanvas.ActualWidth);
                double h = Math.Max(200, ScreensaverCanvas.ActualHeight);
                const double referenceArea = 175_000.0;
                double blobScale = Math.Max(1.0, Math.Sqrt(w * h / referenceArea));
                double size = (195 + _ssRng.NextDouble() * 228) * blobScale;
                blobs[i].Width = size;
                blobs[i].Height = size;

                // Dark gradient
                if (gradBrushes != null && i < gradBrushes.Count)
                {
                    var stops = gradBrushes[i].GradientStops;
                    if (stops.Count >= 2)
                    {
                        stops[0].Color = WpfColor.FromArgb(255, 0, 0, 0);
                        stops[1].Color = WpfColor.FromArgb(200, 0, 0, 0);
                        stops[1].Offset = 0.5;
                    }
                }
            }
        });
    }

    private void ScreensaverColorCycle(object? sender, EventArgs e)
    {
        var brushes = _ssCurrentPattern?.Brushes;
        var gradBrushes = _ssCurrentPattern?.GradientBrushes;
        if (brushes == null || brushes.Count == 0) return;

        _ssHueOffset += 0.3;

        for (int i = 0; i < brushes.Count; i++)
        {
            // Skip dark blobs — they stay black
            if (_ssDarkBlobStart >= 0 && i >= _ssDarkBlobStart)
                continue;

            double hue = (_ssHueOffset + i * 60.0) % 360.0;
            var color = HslToColor(hue, 0.7, 0.15 + _ssBlobIntensity * 0.7);
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

    public void SetBlobPattern(BlobPattern pattern)
    {
        _ssTransitioning = false;
        _ssBlobPatternSetting = pattern;

        if (pattern == BlobPattern.RandomPerSong)
            pattern = BlobTransition.CurrentRandomPattern;

        _ssBlobPattern = pattern;

        // During initial setup, SetDmdScreensaver will create the blobs.
        // Only recreate if the screensaver is already visible.
        if (ScreensaverCanvas.Visibility != Visibility.Visible)
            return;

        _ssCurrentPattern?.Dispose();
        _ssCurrentPattern = BlobTransition.Create(pattern, MakeSsConfig());
        _ssCurrentPattern.Enter(() => { });
    }

    /// <summary>
    /// Restarts the current pattern if it is Mandelbrot, so that changed static settings take effect.
    /// </summary>
    private void RestartMandelbrot()
    {
        if (_ssBlobPattern == BlobPattern.Mandelbrot)
            SetBlobPattern(_ssBlobPatternSetting);
    }

    /// <summary>
    /// Restarts the current pattern if it is ProjectM, so that changed static settings take effect.
    /// </summary>
    private void RestartProjectM()
    {
        if (_ssBlobPattern == BlobPattern.ProjectM)
            SetBlobPattern(_ssBlobPatternSetting);
    }

    /// <summary>
    /// Called when a new song starts playing. Triggers RandomPerSong transitions on all windows.
    /// </summary>
    private void OnPlaybackStartedTransition()
    {
        // Pick a single random pattern that all RandomPerSong windows will share
        BlobTransition.CurrentRandomPattern = BlobTransition.PickRandom(_ssRng, exclude: BlobTransition.CurrentRandomPattern);

        _playfieldProxy?.OnSongChanged();
        _backglassProxy?.OnSongChanged();
        _topperWindow?.Dispatcher.BeginInvoke(() => _topperWindow.OnSongChanged());

        // DMD's own blobs
        if (_ssBlobPatternSetting != BlobPattern.RandomPerSong || _ssTransitioning || _ssCurrentPattern == null)
            return;

        _ssTransitioning = true;

        _ssCurrentPattern.Exit(() =>
        {
            var newPattern = BlobTransition.CurrentRandomPattern;
            DebugLog.Log("DMD", $"Transition {_ssBlobPattern} -> {newPattern} blob pattern");
            _ssBlobPattern = newPattern;

            _ssCurrentPattern?.Dispose();
            _ssCurrentPattern = BlobTransition.Create(newPattern, MakeSsConfig());
            _ssCurrentPattern.Enter(() =>
            {
                _ssTransitioning = false;
            });
        });
    }

    private void HideMouseCursor()
    {
        if (_cursorHidden || _cursorHideSuppressed) return;
        _cursorHidden = true;
        _cursorIdleTimer.Stop();
        Mouse.OverrideCursor = System.Windows.Input.Cursors.None;
        _playfieldProxy?.HideCursor();
        _backglassProxy?.HideCursor();
        _topperWindow?.Dispatcher.BeginInvoke(() =>
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.None);
        SetTitleBarButtonsVisibility(Visibility.Collapsed);
    }

    private void ShowMouseCursor()
    {
        _cursorHidden = false;
        Mouse.OverrideCursor = null;
        _playfieldProxy?.ShowCursor();
        _backglassProxy?.ShowCursor();
        _topperWindow?.Dispatcher.BeginInvoke(() =>
            System.Windows.Input.Mouse.OverrideCursor = null);
        _cursorIdleTimer.Stop();
        if (!_cursorHideSuppressed && _appSettings.HideCursorTimeoutSeconds > 0)
            _cursorIdleTimer.Start();
        SetTitleBarButtonsVisibility(Visibility.Visible);
    }

    private void SetTitleBarButtonsVisibility(Visibility visibility)
    {
        if (FullscreenButton != null) FullscreenButton.Visibility = visibility;
        if (SettingsButtonCtrl != null) SettingsButtonCtrl.Visibility = visibility;
        if (CloseAppButton != null) CloseAppButton.Visibility = visibility;
    }

    private void ApplyCursorHideTimeout()
    {
        _cursorIdleTimer.Stop();
        ShowMouseCursor();
        if (_appSettings.HideCursorTimeoutSeconds > 0)
        {
            _cursorIdleTimer.Interval = TimeSpan.FromSeconds(_appSettings.HideCursorTimeoutSeconds);
            _cursorIdleTimer.Start();
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

    public void ApplyResizable(bool resizable)
    {
        SetResizable(resizable);
        _playfieldProxy?.SetResizable(resizable);
        _backglassProxy?.SetResizable(resizable);
        _topperWindow?.SetResizable(resizable);
    }

    private void ResetAllWindows()
    {
        ResetPosition(1440, 0, 800, 600);
        _backglassProxy?.ResetPosition(620, 0, 800, 600);
        _playfieldProxy?.ResetPosition(0, 0, 600, 800);
        _topperWindow?.ResetPosition(0, 0, 800, 300);
    }

    private static readonly string[] PlaylistIconChoices =
    [
        "📋", "🎸", "🥁", "🎷", "🎹", "🎺", "🎤", "🎧",
        "🔥", "🌟", "🎉", "🌛", "⚡", "💀", "🌈", "🏝",
        "👾", "🛸", "🤠", "❤", "🎶", "🎵", "🔊", "⭐",
        "🎬", "📡", "⏰", "📈", "🔒", "💡",
    ];

    private static Dictionary<string, string[]>? _emojiKeywords;

    /// <summary>
    /// Loads the emoji keyword dictionary from emoji_keywords.json.
    /// Falls back to an empty dictionary if the file is missing or invalid.
    /// </summary>
    private static Dictionary<string, string[]> GetEmojiKeywords()
    {
        if (_emojiKeywords != null)
            return _emojiKeywords;

        var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "emoji_keywords.json");
        try
        {
            if (System.IO.File.Exists(path))
            {
                var json = System.IO.File.ReadAllText(path);
                _emojiKeywords = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string[]>>(json) ?? [];
            }
            else
            {
                _emojiKeywords = [];
            }
        }
        catch (Exception ex)
        {
            DebugLog.Log("EmojiKeywords", $"Failed to load emoji_keywords.json: {ex.Message}");
            _emojiKeywords = [];
        }
        return _emojiKeywords;
    }

    /// <summary>
    /// Returns emoji that match any word in the given text, ordered by number of keyword matches (best first).
    /// </summary>
    private static List<string> SuggestIcons(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var keywords = GetEmojiKeywords();
        if (keywords.Count == 0)
            return [];

        var words = text.Split([' ', '-', '_', '.', ','], StringSplitOptions.RemoveEmptyEntries);
        var scores = new Dictionary<string, int>();

        foreach (var (emoji, kws) in keywords)
        {
            int matchCount = 0;
            foreach (var word in words)
            {
                foreach (var kw in kws)
                {
                    if (word.Contains(kw, StringComparison.OrdinalIgnoreCase)
                        || kw.Contains(word, StringComparison.OrdinalIgnoreCase))
                    {
                        matchCount++;
                        break;
                    }
                }
            }
            if (matchCount > 0)
                scores[emoji] = matchCount;
        }

        return scores
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToList();
    }

    /// <summary>
    /// Populates the icon grid with icons matching the text, plus all default icons.
    /// Suggested matches appear first with a highlight.
    /// </summary>
    private static void UpdateIconGrid(WrapPanel iconGrid, System.Windows.Controls.ComboBox iconPicker, string text,
        System.Windows.Media.Brush surfaceBrush, System.Windows.Media.Brush textBrush, System.Windows.Media.Brush accentBrush)
    {
        iconGrid.Children.Clear();
        var suggestions = SuggestIcons(text);
        var shown = new HashSet<string>();

        void AddIconButton(string emoji, bool isSuggested)
        {
            if (!shown.Add(emoji)) return;
            var btn = new System.Windows.Controls.Button
            {
                Content = emoji,
                FontSize = 20,
                Width = 38,
                Height = 38,
                Margin = new Thickness(2),
                Padding = new Thickness(0),
                Background = isSuggested ? accentBrush : surfaceBrush,
                Foreground = textBrush,
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0x44, 0x44, 0x44)),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = emoji,
            };
            btn.Click += (_, _) =>
            {
                // Add to dropdown if not already present
                if (iconPicker.Items.IndexOf(emoji) < 0)
                    iconPicker.Items.Insert(0, emoji);
                iconPicker.SelectedItem = emoji;
            };
            iconGrid.Children.Add(btn);
        }

        // Suggestions first
        foreach (var s in suggestions)
            AddIconButton(s, true);

        // Then all default icons
        foreach (var icon in PlaylistIconChoices)
            AddIconButton(icon, false);
    }

    /// <summary>
    /// Shows a dark-themed "New Playlist" dialog with a live-updating icon grid. Returns (name, icon), or null if cancelled.
    /// </summary>
    private (string Name, string Icon)? ShowNewPlaylistDialog(string defaultName = "My Playlist")
    {
        var dialog = CreateDarkDialog("New Playlist", 380, 460);
        var stack = (System.Windows.Controls.StackPanel)dialog.Tag!;
        var surfaceBrush = (System.Windows.Media.Brush)FindResource("SurfaceBrush");
        var textBrush = (System.Windows.Media.Brush)FindResource("TextBrush");
        var accentBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");

        var label = new System.Windows.Controls.TextBlock
        {
            Text = "Playlist Icon and Name",
            Foreground = textBrush,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 6)
        };

        var inputRow = new DockPanel();

        var iconPicker = new System.Windows.Controls.ComboBox
        {
            Background = surfaceBrush,
            Foreground = textBrush,
            BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            FontSize = 22,
            Width = 86,
            Padding = new Thickness(4),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            IsEditable = false,
            ToolTip = "Playlist icon",
        };
        foreach (var icon in PlaylistIconChoices)
            iconPicker.Items.Add(icon);
        iconPicker.SelectedIndex = 0;
        DockPanel.SetDock(iconPicker, System.Windows.Controls.Dock.Left);

        var input = new TextBox
        {
            Text = defaultName,
            Background = surfaceBrush,
            Foreground = textBrush,
            BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0x33, 0x33, 0x33)),
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 13,
            CaretBrush = textBrush,
        };
        input.SelectAll();

        inputRow.Children.Add(iconPicker);
        inputRow.Children.Add(input);

        // Live-updating icon grid
        var iconGrid = new WrapPanel
        {
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
        };
        var iconScroll = new ScrollViewer
        {
            Content = iconGrid,
            MaxHeight = 200,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 4, 0, 0),
        };

        // Debounce icon grid updates so rapid typing doesn't cause jerkiness
        var iconDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        iconDebounce.Tick += (_, _) =>
        {
            iconDebounce.Stop();
            UpdateIconGrid(iconGrid, iconPicker, input.Text, surfaceBrush, textBrush, accentBrush);
        };
        input.TextChanged += (_, _) =>
        {
            iconDebounce.Stop();
            iconDebounce.Start();
        };
        dialog.Closed += (_, _) => iconDebounce.Stop();

        var ok = new System.Windows.Controls.Button
        {
            Content = "Create",
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 14, 0, 0),
            Background = accentBrush,
            Foreground = textBrush,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };
        ok.Click += (_, _) => { dialog.DialogResult = true; dialog.Close(); };
        input.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter) { dialog.DialogResult = true; dialog.Close(); }
        };

        stack.Children.Add(label);
        stack.Children.Add(inputRow);
        stack.Children.Add(iconScroll);
        stack.Children.Add(ok);

        dialog.ContentRendered += (_, _) =>
        {
            input.Focus();
            // Populate initial icon grid
            UpdateIconGrid(iconGrid, iconPicker, input.Text, surfaceBrush, textBrush, accentBrush);
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(input.Text))
        {
            var selectedIcon = iconPicker.SelectedItem as string;
            if (string.IsNullOrEmpty(selectedIcon) || selectedIcon == "──")
                selectedIcon = "📋";
            return (input.Text, selectedIcon);
        }
        return null;
    }

    private void NewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not JukeboxViewModel vm) return;

        var result = ShowNewPlaylistDialog(vm.CurrentlyPlaying?.Title ?? "My Playlist");
        if (result != null)
            vm.CreatePlaylistWithIcon(result.Value.Name, result.Value.Icon);
    }

    private void SaveLivePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not JukeboxViewModel vm) return;
        if (string.IsNullOrWhiteSpace(vm.SearchQuery)) return;

        var result = ShowNewPlaylistDialog(vm.SearchQuery);
        if (result != null)
            vm.CreateLivePlaylistWithIcon(result.Value.Name, result.Value.Icon);
    }

    private void MakePlaylistFromQueue_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not JukeboxViewModel vm) return;
        if (vm.Queue.Count == 0) return;

        var result = ShowNewPlaylistDialog("My Playlist");
        if (result == null) return;

        vm.CreatePlaylistWithIcon(result.Value.Name, result.Value.Icon);
        foreach (var item in vm.Queue)
            vm.AddToPlaylistCommand.Execute(item);

        vm.StatusText = $"Created playlist '{result.Value.Name}' with {vm.Queue.Count} items";
    }

    private void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not JukeboxViewModel vm) return;
        var name = vm.ActivePlaylistName;
        if (string.IsNullOrWhiteSpace(name) || name == "Favorites") return;

        var dialog = CreateDarkDialog($"Delete '{name}'?", 320, 200);
        var stack = (System.Windows.Controls.StackPanel)dialog.Tag!;
        var textBrush = (System.Windows.Media.Brush)FindResource("TextBrush");

        var label = new System.Windows.Controls.TextBlock
        {
            Text = $"Are you sure you want to delete the playlist \"{name}\"?",
            Foreground = textBrush,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        };

        var buttonPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        var deleteBtn = new System.Windows.Controls.Button
        {
            Content = "Delete",
            Padding = new Thickness(16, 8, 16, 8),
            Background = new SolidColorBrush(WpfColor.FromRgb(0xCC, 0x33, 0x33)),
            Foreground = textBrush,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        deleteBtn.Click += (_, _) => { dialog.DialogResult = true; dialog.Close(); };
        var cancelBtn = new System.Windows.Controls.Button
        {
            Content = "Cancel",
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(8, 0, 0, 0),
            Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush"),
            Foreground = textBrush,
            FontSize = 13,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        cancelBtn.Click += (_, _) => { dialog.DialogResult = false; dialog.Close(); };

        buttonPanel.Children.Add(deleteBtn);
        buttonPanel.Children.Add(cancelBtn);
        stack.Children.Add(label);
        stack.Children.Add(buttonPanel);

        if (dialog.ShowDialog() == true)
        {
            vm.DeletePlaylistCommand.Execute(name);
            vm.ShowCategoryListCommand.Execute(null);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ApplyNavHighlight(vm));
        }
    }

    public void SetQueuePosition(QueuePosition position)
    {
        if (_queuePosition == position)
            return;

        _queuePosition = position;

        if (position == QueuePosition.Right)
        {
            // Side-by-side: 3 columns in ContentQueueGrid
            ContentQueueGrid.RowDefinitions.Clear();
            ContentQueueGrid.ColumnDefinitions.Clear();
            ContentQueueGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MinWidth = 100 });
            ContentQueueGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var savedSize = _appSettings?.DmdQueueSplitterSize ?? -1;
            var queueCol = savedSize > 0
                ? new ColumnDefinition { Width = new GridLength(savedSize, GridUnitType.Pixel), MinWidth = 60 }
                : new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 60 };
            ContentQueueGrid.ColumnDefinitions.Add(queueCol);

            Grid.SetRow(QueueSplitter, 0); Grid.SetColumn(QueueSplitter, 1);
            QueueSplitter.Width = 6; QueueSplitter.Height = double.NaN;
            QueueSplitter.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            QueueSplitter.VerticalAlignment = VerticalAlignment.Stretch;
            QueueSplitter.Cursor = System.Windows.Input.Cursors.SizeWE;
            QueueSplitter.ResizeDirection = System.Windows.Controls.GridResizeDirection.Columns;

            Grid.SetRow(QueueBorder, 0); Grid.SetColumn(QueueBorder, 2);
        }
        else
        {
            // Stacked: 3 rows in ContentQueueGrid
            ContentQueueGrid.ColumnDefinitions.Clear();
            ContentQueueGrid.RowDefinitions.Clear();
            ContentQueueGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 60 });
            ContentQueueGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var savedSize = _appSettings?.DmdQueueSplitterSize ?? -1;
            var queueRow = savedSize > 0
                ? new RowDefinition { Height = new GridLength(savedSize, GridUnitType.Pixel), MinHeight = 60 }
                : new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 60 };
            ContentQueueGrid.RowDefinitions.Add(queueRow);

            Grid.SetRow(QueueSplitter, 1); Grid.SetColumn(QueueSplitter, 0);
            QueueSplitter.Height = 6; QueueSplitter.Width = double.NaN;
            QueueSplitter.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            QueueSplitter.VerticalAlignment = VerticalAlignment.Center;
            QueueSplitter.Cursor = System.Windows.Input.Cursors.SizeNS;
            QueueSplitter.ResizeDirection = System.Windows.Controls.GridResizeDirection.Rows;

            Grid.SetRow(QueueBorder, 2); Grid.SetColumn(QueueBorder, 0);
        }

        // Update thumbnail height and delete button position
        UpdateQueueThumbnailHeight();
        UpdateQueueDeleteButtonPosition();

        // Apply item padding — right position gets extra 2px between items
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, System.Windows.Media.Brushes.Transparent));
        var padding = position == QueuePosition.Right ? new Thickness(0, 2, 0, 2) : new Thickness(0);
        itemStyle.Setters.Add(new Setter(PaddingProperty, padding));

        // Preserve the current-item highlight trigger
        var trigger = new DataTrigger { Value = "True" };
        trigger.Binding = new System.Windows.Data.MultiBinding
        {
            Converter = (System.Windows.Data.IMultiValueConverter)FindResource("IsCurrentQueueItemConverter"),
            Bindings =
            {
                new System.Windows.Data.Binding(),
                new System.Windows.Data.Binding("DataContext.CurrentQueueItem") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(System.Windows.Controls.ListBox), 1) }
            }
        };
        trigger.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, new SolidColorBrush(WpfColor.FromRgb(0x2A, 0x2A, 0x2A))));
        itemStyle.Triggers.Add(trigger);
        QueueList.ItemContainerStyle = itemStyle;
    }

    private void UpdateQueueDeleteButtonPosition()
    {
        // Delete button is always on the left for both positions
        var dock = System.Windows.Controls.Dock.Left;

        bool isRight = _queuePosition == QueuePosition.Right;
        double queueFontSize = (double)Resources["QueueFontSize"];
        // For right position: detail is a second line, font size 2 smaller than QueueFontSize
        double detailFontSize = isRight
            ? Math.Max(9, queueFontSize - 4)
            : queueFontSize;

        for (int i = 0; i < QueueList.Items.Count; i++)
        {
            var container = QueueList.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
            if (container == null) continue;
            foreach (var grid in FindVisualChildren<Grid>(container))
            {
                if (grid.Name == "QueueThumbnailGrid")
                    DockPanel.SetDock(grid, dock);
            }
            foreach (var tb in FindVisualChildren<System.Windows.Controls.TextBlock>(container))
            {
                if (tb.Name == "QueueItemDetail")
                {
                    // Second-line detail: visible only for right position
                    tb.Visibility = isRight ? Visibility.Visible : Visibility.Collapsed;
                    tb.FontSize = detailFontSize;
                }
                else if (tb.Name == "QueueItemDetailInline")
                {
                    // Inline detail: visible only for bottom position
                    tb.Visibility = isRight ? Visibility.Collapsed : Visibility.Visible;
                }
            }
        }
    }

    // ── Queue drag-reorder ──

    private void QueueList_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _queueDragStart = e.GetPosition(QueueList);
        _queueDragInProgress = false;
    }

    private void QueueList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;

        var pos = e.GetPosition(QueueList);
        if (Math.Abs(pos.X - _queueDragStart.X) < 4 && Math.Abs(pos.Y - _queueDragStart.Y) < 4)
            return;

        if (_queueDragInProgress) return;

        var item = GetDataContextAtPoint<VideoItem>(QueueList, _queueDragStart);
        if (item == null) return;

        _queueDragInProgress = true;
        DragDrop.DoDragDrop(QueueList, item, System.Windows.DragDropEffects.Move);
        _queueDragInProgress = false;
    }

    private void QueueList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not JukeboxViewModel vm) return;
        var item = GetDataContextAtPoint<VideoItem>(QueueList, e.GetPosition(QueueList));
        if (item == null) return;

        int index = vm.Queue.IndexOf(item);
        if (index >= 0)
            vm.PlayFromQueueIndex(index);
    }

    private void QueueList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(VideoItem))) return;
        if (DataContext is not JukeboxViewModel vm) return;

        var draggedItem = (VideoItem)e.Data.GetData(typeof(VideoItem))!;
        var targetItem = GetDataContextAtPoint<VideoItem>(QueueList, e.GetPosition(QueueList));

        int oldIndex = vm.Queue.IndexOf(draggedItem);
        int newIndex = targetItem != null ? vm.Queue.IndexOf(targetItem) : vm.Queue.Count - 1;

        if (oldIndex < 0 || oldIndex == newIndex) return;

        vm.Queue.Move(oldIndex, newIndex);

        // Keep QueueIndex tracking the currently playing item after reorder
        if (vm.QueueIndex == oldIndex)
            vm.QueueIndex = newIndex;
        else if (oldIndex < vm.QueueIndex && newIndex >= vm.QueueIndex)
            vm.QueueIndex--;
        else if (oldIndex > vm.QueueIndex && newIndex <= vm.QueueIndex)
            vm.QueueIndex++;

        // Force queue index labels to refresh after reorder
        RefreshQueueIndices();
    }

    private void RefreshQueueIndices()
    {
        for (int i = 0; i < QueueList.Items.Count; i++)
        {
            var container = QueueList.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
            if (container == null) continue;
            foreach (var tb in FindVisualChildren<System.Windows.Controls.TextBlock>(container))
            {
                var expr = tb.GetBindingExpression(System.Windows.Controls.TextBlock.TextProperty);
                if (expr == null)
                {
                    var multiExpr = System.Windows.Data.BindingOperations.GetMultiBindingExpression(tb, System.Windows.Controls.TextBlock.TextProperty);
                    multiExpr?.UpdateTarget();
                }
            }
        }
    }

    private static T? GetDataContextAtPoint<T>(System.Windows.Controls.ListBox listBox, WpfPoint point) where T : class
    {
        var element = listBox.InputHitTest(point) as DependencyObject;
        while (element != null)
        {
            if (element is ListBoxItem lbi)
                return lbi.DataContext as T;
            // Run/Inline elements are not Visual — walk up via LogicalTreeHelper first
            if (element is not System.Windows.Media.Visual and not System.Windows.Media.Media3D.Visual3D)
                element = LogicalTreeHelper.GetParent(element);
            else
                element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }
}
