using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using Button = System.Windows.Controls.Button;

namespace VpinJukebox;

public class CategoryVisibilityItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    public string SearchTerm { get; set; } = "";
    public bool IsVisible { get; set; } = true;
    /// <summary>
    /// True for reserved/special categories (e.g. History) whose search term should not be edited.
    /// </summary>
    public bool IsSpecial { get; set; }
    public bool IsSeparator { get; set; }
    public bool IsLineBreak { get; set; }
    public bool IsPlex { get; set; }
    public string? PlexLibraryKey { get; set; }
    public string? PlexLibraryType { get; set; }
    public bool PlexHubsEnabled { get; set; }
    public bool PlexPlaylistsEnabled { get; set; }
    /// <summary>
    /// The search term when the settings window was opened, used to detect changes.
    /// </summary>
    public string OriginalSearchTerm { get; set; } = "";
}

public partial class SettingsWindow : JukeboxWindow
{
    private readonly AppSettings _settings;
    private readonly List<KeyBindingEntry> _entries;
    private Button? _rebindingButton;
    private bool _rebindingCabinet;
    private DirectInputPoller? _dinputPoller;
    private BackglassProxy? _backglassProxy;
    private PlayfieldProxy? _playfieldProxy;
    private TopperWindow? _topperWindow;
    private double _originalIntensity;
    private double _originalSpeed;
    private double _originalDistortion;
    private BlobPattern _originalPlayfieldBlobPattern;
    private int _originalPlayfieldBlobCount;
    private int _originalPlayfieldBlobSizeOffset;
    private int _originalPlayfieldRotation;
    private BlobPattern _originalBackglassBlobPattern;
    private int _originalBackglassBlobCount;
    private int _originalBackglassBlobSizeOffset;
    private BlobPattern _originalTopperBlobPattern;
    private int _originalTopperBlobCount;
    private int _originalTopperBlobSizeOffset;
    private BlobPattern _originalDmdBlobPattern;
    private int _originalDmdBlobCount;
    private int _originalDmdBlobSizeOffset;
    private int _originalDmdRotation;
    private bool _originalReactiveBlobs;
    private bool _originalReactiveProjectM;
    private double _originalReactivityThreshold;
    private int _originalReactiveSpeedMs;
    private double _originalReactiveOverdrive;
    private string _originalTitleText;
    private string _originalLogoText;
    private bool _originalLogoSpin;
    private LogoRingsMode _originalLogoRings;
    private bool _originalLogoMorphColor;
    private int _originalMandelbrotUseGpu;
    private bool _originalMandelbrotAdaptiveIterations;
    private int _originalMandelbrotMaxIterations;
    private int _originalMandelbrotMaxHz;
    private double _originalMandelbrotRenderScale;
    private double _originalMandelbrotPerturbation;
    private bool _originalMandelbrotDiscovery;
    private double _originalMandelbrotDimming;
    private double _originalProjectMPresetDuration;
    private double _originalProjectMSoftCut;
    private bool _originalProjectMHardCut;
    private float _originalProjectMBeatSensitivity;
    private int _originalProjectMMeshSize;
    private double _originalProjectMRenderScale;
    private string _originalProjectMPresetPath;
    private string _originalProjectMTexturePath;
    private List<string> _originalProjectMEnabledFolders;
    private bool _originalProjectMSoftwareRender;
    private readonly ObservableCollection<PlexLibraryMapping> _plexLibraries = new();
    private readonly List<CategoryVisibilityItem> _categoryVisibilityItems = new();
    private bool _originalDofEnabled;
    private bool _originalDofColorBand;
    private bool _originalDofPresetChanged;
    private string _originalDofRomName;
    private DofClient? _testDofClient;
    private DofClient? _sharedDofClient;
    private bool _testModeActive;
    private DirectInputPoller? _testDInputPoller;

    public PlayfieldMode SelectedPlayfieldMode { get; private set; }
    public bool Saved { get; private set; }
    public bool WindowsReset { get; private set; }

    public bool SpeedChanged =>
        Math.Abs(_settings.ScreensaverSpeed - _originalSpeed) > 0.001;

    public bool PlayfieldBlobsChanged =>
        SpeedChanged ||
        _settings.PlayfieldBlobPattern != _originalPlayfieldBlobPattern ||
        _settings.PlayfieldBlobCount != _originalPlayfieldBlobCount ||
        _settings.PlayfieldBlobSizeOffset != _originalPlayfieldBlobSizeOffset;

    public bool PlayfieldRotationChanged =>
        _settings.PlayfieldRotation != _originalPlayfieldRotation;

    public bool BackglassBlobsChanged =>
        SpeedChanged ||
        _settings.BackglassBlobPattern != _originalBackglassBlobPattern ||
        _settings.BackglassBlobCount != _originalBackglassBlobCount ||
        _settings.BackglassBlobSizeOffset != _originalBackglassBlobSizeOffset;

    public bool TopperBlobsChanged =>
        SpeedChanged ||
        _settings.TopperBlobPattern != _originalTopperBlobPattern ||
        _settings.TopperBlobCount != _originalTopperBlobCount ||
        _settings.TopperBlobSizeOffset != _originalTopperBlobSizeOffset;

    public bool DmdBlobsChanged =>
        SpeedChanged ||
        _settings.DmdBlobPattern != _originalDmdBlobPattern ||
        _settings.DmdBlobCount != _originalDmdBlobCount ||
        _settings.DmdBlobSizeOffset != _originalDmdBlobSizeOffset;

    public bool DmdRotationChanged =>
        _settings.DmdRotation != _originalDmdRotation;

    public bool ReactiveBlobsChanged =>
        _settings.ReactiveBlobs != _originalReactiveBlobs ||
        _settings.ReactiveProjectM != _originalReactiveProjectM ||
        Math.Abs(_settings.ReactivityThreshold - _originalReactivityThreshold) > 0.001 ||
        _settings.ReactiveSpeedMs != _originalReactiveSpeedMs ||
        Math.Abs(_settings.ReactiveOverdrive - _originalReactiveOverdrive) > 0.001;

    public bool LogoChanged =>
        _settings.LogoText != _originalLogoText ||
        _settings.LogoSpin != _originalLogoSpin ||
        _settings.LogoRings != _originalLogoRings ||
        _settings.BackglassLogoMorphColor != _originalLogoMorphColor;

    public bool DofSettingsChanged =>
        _settings.DofEnabled != _originalDofEnabled ||
        _settings.DofColorBand != _originalDofColorBand ||
        _settings.DofPresetChanged != _originalDofPresetChanged ||
        _settings.DofRomName != _originalDofRomName;

    public bool ProjectMSettingsChanged =>
        ProjectMRestartRequired || ProjectMTuningChanged;

    /// <summary>
    /// True when structural settings changed that require a full ProjectM restart
    /// (render scale, preset/texture paths, enabled folders).
    /// </summary>
    public bool ProjectMRestartRequired =>
        Math.Abs(_settings.ProjectMRenderScale - _originalProjectMRenderScale) > 0.001 ||
        _settings.ProjectMPresetPath != _originalProjectMPresetPath ||
        _settings.ProjectMTexturePath != _originalProjectMTexturePath ||
        !_settings.ProjectMEnabledFolders.SequenceEqual(_originalProjectMEnabledFolders) ||
        _settings.ProjectMSoftwareRender != _originalProjectMSoftwareRender;

    /// <summary>
    /// True when only tuning parameters changed that can be applied in-place
    /// (duration, soft cut, hard cut, beat sensitivity, mesh size).
    /// </summary>
    public bool ProjectMTuningChanged =>
        _settings.ProjectMMeshSize != _originalProjectMMeshSize ||
        Math.Abs(_settings.ProjectMPresetDuration - _originalProjectMPresetDuration) > 0.001 ||
        Math.Abs(_settings.ProjectMSoftCutDuration - _originalProjectMSoftCut) > 0.001 ||
        _settings.ProjectMHardCutEnabled != _originalProjectMHardCut ||
        Math.Abs(_settings.ProjectMBeatSensitivity - _originalProjectMBeatSensitivity) > 0.001;

    public bool MandelbrotSettingsChanged =>
        _settings.MandelbrotUseGpu != _originalMandelbrotUseGpu ||
        _settings.MandelbrotAdaptiveIterations != _originalMandelbrotAdaptiveIterations ||
        _settings.MandelbrotMaxIterations != _originalMandelbrotMaxIterations ||
        _settings.MandelbrotMaxHz != _originalMandelbrotMaxHz ||
        Math.Abs(_settings.MandelbrotRenderScale - _originalMandelbrotRenderScale) > 0.001 ||
        Math.Abs(_settings.MandelbrotPerturbation - _originalMandelbrotPerturbation) > 0.001 ||
        _settings.MandelbrotDiscovery != _originalMandelbrotDiscovery ||
        Math.Abs(_settings.MandelbrotDimming - _originalMandelbrotDimming) > 0.001;

    public event Action? SettingsApplied;

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        _entries = settings.KeyBindings.ToEntries();

        InitializeComponent();

        SetResizable(settings.ResizableWindows);

        // Version info
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var asmVersion = asm.GetName().Version;
        VersionText.Text = asmVersion is not null
            ? $"v{asmVersion.Major}.{asmVersion.Minor}.{asmVersion.Build}.{asmVersion.Revision}"
            : "v0.0";

        BindingsList.ItemsSource = _entries;

        switch (settings.PlayfieldDisplayMode)
        {
            case PlayfieldMode.Blank: RbBlank.IsChecked = true; break;
            case PlayfieldMode.Screensaver: RbScreensaver.IsChecked = true; break;
            case PlayfieldMode.StaticImage: RbStatic.IsChecked = true; break;
            case PlayfieldMode.Video: RbVideo.IsChecked = true; break;
        }

        // OLED Sleep Defeat
        CbOledSleepDefeat.Items.Add("Off");
        for (int s = 10; s <= 120; s += 10)
            CbOledSleepDefeat.Items.Add($"{s} seconds");
        CbOledSleepDefeat.SelectedIndex = Math.Clamp(settings.OledSleepDefeatSeconds / 10, 0, 12);

        for (int d = 1; d <= 20; d++)
            CbOledSleepDuration.Items.Add($"{d} seconds");
        CbOledSleepDuration.SelectedIndex = Math.Clamp(settings.OledSleepDefeatDurationSeconds - 1, 0, 19);

        SliderOledIntensity.Value = settings.OledSleepDefeatIntensity;

        CbPulseDominantBlobs.IsChecked = settings.PlayfieldPulseDominantBlobs;

        TbStaticImagePath.Text = settings.PlayfieldStaticImagePath;
        TbVideoPath.Text = settings.PlayfieldVideoPath;

        CbShowVideoInfo.IsChecked = settings.ShowVideoInfo;
        CbResizableWindows.IsChecked = settings.ResizableWindows;
        CbSetCursorOnLaunch.IsChecked = settings.SetCursorOnLaunch;
        CbMoveCursorToSettings.IsChecked = settings.MoveCursorToSettings;
        CbCheckWindowsOnStartup.IsChecked = settings.CheckWindowsOnStartup;
        CbShowBackglass.IsChecked = settings.ShowBackglass;
        CbShowPlayfield.IsChecked = settings.ShowPlayfield;
        CbShowTopper.IsChecked = settings.ShowTopper;
        CbAutoPlayQueue.IsChecked = settings.AutoPlayQueueOnStart;
        TbStartupDittiPath.Text = settings.StartupDittiPath;
        CbEnableStartupDitti.IsChecked = settings.EnableStartupDitti;
        CbDofEnabled.IsChecked = settings.DofEnabled;
        TbDofRomName.Text = settings.DofRomName;
        CbDofSimulator.IsChecked = settings.DofSimulator;
        CbDofColorBand.IsChecked = settings.DofColorBand;
        CbDofPresetChanged.IsChecked = settings.DofPresetChanged;

        // Validate DOF bridge availability
        if (!DofClient.IsBridgeAvailable())
        {
            CbDofEnabled.IsChecked = false;
            CbDofEnabled.IsEnabled = false;
            DofStatusText.Text = "DofBridge not found. Ensure x64/DofBridge.exe or x86/DofBridge.exe is present.";
            DofStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0x33, 0x33));
        }
        else
        {
            CbDofEnabled.Checked += (_, _) =>
            {
                if (!DofClient.IsBridgeAvailable())
                {
                    CbDofEnabled.IsChecked = false;
                    DofStatusText.Text = "DofBridge not found.";
                }
            };
        }
        SliderDistortion.Value = settings.TopperDistortion * 100;
        CbDmdScreensaver.IsChecked = settings.DmdScreensaver;
        // Backglass logo dim
        CbBackglassLogoDim.IsChecked = settings.BackglassLogoDimEnabled;
        for (int pct2 = 0; pct2 <= 100; pct2 += 5)
            CbBackglassDimOpacity.Items.Add($"{pct2}%");
        CbBackglassDimOpacity.SelectedIndex = Math.Clamp(settings.BackglassLogoDimOpacity / 5, 0, 20);
        var bgTimeouts = new (int seconds, string label)[]
        {
            (10, "10 seconds"), (15, "15 seconds"), (20, "20 seconds"), (30, "30 seconds"),
            (45, "45 seconds"), (60, "1 minute"), (90, "1.5 minutes"), (120, "2 minutes"),
            (3 * 60, "3 minutes"), (4 * 60, "4 minutes"), (5 * 60, "5 minutes"),
            (6 * 60, "6 minutes"), (7 * 60, "7 minutes"), (8 * 60, "8 minutes"),
            (9 * 60, "9 minutes"), (10 * 60, "10 minutes")
        };
        int selectedBgTimeoutIndex = 5;
        for (int i = 0; i < bgTimeouts.Length; i++)
        {
            CbBackglassDimTimeout.Items.Add(bgTimeouts[i].label);
            if (bgTimeouts[i].seconds == settings.BackglassLogoDimTimeoutSeconds)
                selectedBgTimeoutIndex = i;
        }
        CbBackglassDimTimeout.SelectedIndex = selectedBgTimeoutIndex;

        CbBackglassMorphColor.IsChecked = settings.BackglassLogoMorphColor;
        CbBackglassAudioOnly.IsChecked = settings.BackglassAudioOnly;

        CbDmdScreensaverDim.IsChecked = settings.DmdScreensaverDimEnabled;
        CbDmdDimDarkBlobs.IsChecked = settings.DmdScreensaverDimDarkBlobs;
        switch (settings.DmdSwapTarget)
        {
            case DmdSwapMode.Playfield: RbSwapPlayfield.IsChecked = true; break;
            case DmdSwapMode.Backglass: RbSwapBackglass.IsChecked = true; break;
            default: RbSwapOff.IsChecked = true; break;
        }
        CbApplyDefaultDmdOnSwap.IsChecked = settings.ApplyDefaultDmdOnSwap;

        // Dim opacity dropdown
        for (int pct = 0; pct <= 100; pct += 5)
            CbDmdDimOpacity.Items.Add($"{pct}%");
        CbDmdDimOpacity.SelectedIndex = Math.Clamp(settings.DmdScreensaverDimOpacity / 5, 0, 20);

        // Dim timeout dropdown
        var timeouts = new (int seconds, string label)[]
        {
            (10, "10 seconds"), (15, "15 seconds"), (20, "20 seconds"), (30, "30 seconds"),
            (45, "45 seconds"), (60, "1 minute"), (90, "1.5 minutes"), (120, "2 minutes"),
            (3 * 60, "3 minutes"), (4 * 60, "4 minutes"), (5 * 60, "5 minutes"),
            (6 * 60, "6 minutes"), (7 * 60, "7 minutes"), (8 * 60, "8 minutes"),
            (9 * 60, "9 minutes"), (10 * 60, "10 minutes")
        };
        int selectedTimeoutIndex = 5; // default 1 minute
        for (int i = 0; i < timeouts.Length; i++)
        {
            CbDmdDimTimeout.Items.Add(timeouts[i].label);
            if (timeouts[i].seconds == settings.DmdScreensaverDimTimeoutSeconds)
                selectedTimeoutIndex = i;
        }
        CbDmdDimTimeout.SelectedIndex = selectedTimeoutIndex;

        TbTitleText.Text = settings.TitleText;
        TbLogoText.Text = settings.LogoText;
        CbLogoSpin.IsChecked = settings.LogoSpin;

        SliderLogoRings.Value = (int)settings.LogoRings;

        // Blob pattern per screen (alphabetized)
        var blobPatterns = Enum.GetValues<BlobPattern>()
            .Select(p => (Pattern: p, Name: p switch
            {
                BlobPattern.Random => "Random",
                BlobPattern.RoughClockwise => "Eccentric (Clockwise)",
                BlobPattern.PerfectClockwise => "Orbital (Clockwise)",
                BlobPattern.RoughMixed => "Eccentric (Mixed)",
                BlobPattern.PerfectMixed => "Orbital (Mixed)",
                BlobPattern.Rainfall => "Rainfall",
                BlobPattern.LavaLamp => "Lava Lamp",
                BlobPattern.Bounce => "Bounce",
                BlobPattern.LightCycle => "Light Cycle",
                BlobPattern.FractalBox => "Fractal Box",
                BlobPattern.Mandelbrot => "Mandelbrot",
                BlobPattern.RandomPerSong => "Random Per Song",
                _ => p.ToString()
            }))
            .OrderBy(p => p.Name)
            .ToList();
        foreach (var cb in new[] { CbBlobPatternPlayfield, CbBlobPatternBackglass, CbBlobPatternTopper, CbBlobPatternDmd })
            foreach (var p in blobPatterns)
                cb.Items.Add(p.Name);
        CbBlobPatternPlayfield.SelectedIndex = blobPatterns.FindIndex(p => p.Pattern == settings.PlayfieldBlobPattern);
        CbBlobPatternBackglass.SelectedIndex = blobPatterns.FindIndex(p => p.Pattern == settings.BackglassBlobPattern);
        CbBlobPatternTopper.SelectedIndex = blobPatterns.FindIndex(p => p.Pattern == settings.TopperBlobPattern);
        CbBlobPatternDmd.SelectedIndex = blobPatterns.FindIndex(p => p.Pattern == settings.DmdBlobPattern);
        SliderBlobCountPlayfield.Value = settings.PlayfieldBlobCount;
        SliderBlobSizePlayfield.Value = settings.PlayfieldBlobSizeOffset;

        foreach (var rot in new[] { "0°", "90°", "180°", "270°" })
            CbPlayfieldRotation.Items.Add(rot);
        CbPlayfieldRotation.SelectedIndex = settings.PlayfieldRotation switch { 90 => 1, 180 => 2, 270 => 3, _ => 0 };


        SliderBlobCountBackglass.Value = settings.BackglassBlobCount;
        SliderBlobSizeBackglass.Value = settings.BackglassBlobSizeOffset;
        SliderBlobCountTopper.Value = settings.TopperBlobCount;
        SliderBlobSizeTopper.Value = settings.TopperBlobSizeOffset;
        SliderBlobCountDmd.Value = settings.DmdBlobCount;
        SliderBlobSizeDmd.Value = settings.DmdBlobSizeOffset;
        CbExcludeMandelbrot.IsChecked = settings.ExcludeMandelbrotFromRandom;
        CbExcludeProjectM.IsChecked = settings.ExcludeProjectMFromRandom;
        UpdateBlobCountSliderStates();

        // ProjectM tuning
        SliderProjectMPresetDuration.Value = PresetDurationToIndex(settings.ProjectMPresetDuration);
        TxtProjectMPresetDuration.Text = FormatPresetDuration((int)settings.ProjectMPresetDuration);
        SliderProjectMSoftCut.Value = settings.ProjectMSoftCutDuration;
        TxtProjectMSoftCut.Text = $"{(int)settings.ProjectMSoftCutDuration}s";
        SliderProjectMBeatSensitivity.Value = settings.ProjectMBeatSensitivity;
        TxtProjectMBeatSensitivity.Text = $"{settings.ProjectMBeatSensitivity:F1}";
        CbProjectMHardCut.IsChecked = settings.ProjectMHardCutEnabled;
        CbProjectMNewVisualOnTrackChange.IsChecked = settings.ProjectMNewVisualOnTrackChange;
        CbProjectMCompatibilityRenderer.IsChecked = settings.ProjectMSoftwareRender;
        TbProjectMActiveRenderer.Text = ProjectMRenderer.ActiveRenderPath != null
            ? $"Active: {ProjectMRenderer.ActiveRenderPath}"
            : "Active: (not yet initialized)";
        SliderProjectMRenderScale.Value = settings.ProjectMRenderScale * 100;
        TxtProjectMRenderScale.Text = $"{(int)(settings.ProjectMRenderScale * 100)}%";
        switch (settings.ProjectMPresetMonitor)
        {
            case 1: RbPresetMonitorSkip.IsChecked = true; break;
            case 2: RbPresetMonitorDeactivate.IsChecked = true; break;
            default: RbPresetMonitorOff.IsChecked = true; break;
        }
        PopulateProjectMFolderTree(settings);
        UpdateProjectMTuningVisibility();

        // Mandelbrot tuning
        CbMandelbrotUseGpu.IsChecked = settings.MandelbrotUseGpu == 1;
        CbMandelbrotAdaptiveIterations.IsChecked = settings.MandelbrotAdaptiveIterations;
        SliderMandelbrotMaxIterations.Value = settings.MandelbrotMaxIterations;
        TxtMandelbrotMaxIterations.Text = $"{settings.MandelbrotMaxIterations}";
        SliderMandelbrotMaxHz.Value = settings.MandelbrotMaxHz;
        TxtMandelbrotMaxHz.Text = settings.MandelbrotMaxHz == 0 ? "Unlimited" : $"{settings.MandelbrotMaxHz} Hz";
        SliderMandelbrotRenderScale.Value = settings.MandelbrotRenderScale * 100;
        TxtMandelbrotRenderScale.Text = $"{(int)(settings.MandelbrotRenderScale * 100)}%";
        SliderMandelbrotPerturbation.Value = settings.MandelbrotPerturbation * 100;
        TxtMandelbrotPerturbation.Text = settings.MandelbrotPerturbation == 0 ? "Off" : $"{(int)(settings.MandelbrotPerturbation * 100)}%";
        CbMandelbrotDiscovery.IsChecked = settings.MandelbrotDiscovery;
        SliderMandelbrotDimming.Value = settings.MandelbrotDimming * 100;
        TxtMandelbrotDimming.Text = settings.MandelbrotDimming == 0 ? "Off" : $"{(int)(settings.MandelbrotDimming * 100)}%";
        UpdateMandelbrotTuningVisibility();

        CbReactiveBlobs.IsChecked = settings.ReactiveBlobs;
        CbReactiveProjectM.IsChecked = settings.ReactiveProjectM;
        SliderReactivityThreshold.Value = settings.ReactivityThreshold * 100;
        SliderReactiveSpeed.Value = settings.ReactiveSpeedMs;
        SliderReactiveOverdrive.Value = settings.ReactiveOverdrive * 10;

        foreach (var rot in new[] { "0°", "90°", "180°", "270°" })
            CbDmdRotation.Items.Add(rot);
        CbDmdRotation.SelectedIndex = settings.DmdRotation switch { 90 => 1, 180 => 2, 270 => 3, _ => 0 };

        foreach (var pos in new[] { "Right", "Bottom" })
            CbQueuePosition.Items.Add(pos);
        CbQueuePosition.SelectedIndex = (int)settings.DmdQueuePosition;

        SliderHeaderSize.Value = settings.DmdHeaderSizeModifier;
        SliderSearchBarSize.Value = settings.DmdSearchBarSizeModifier;
        SliderSearchResultsNavSize.Value = settings.DmdSearchResultsNavSizeModifier;
        SliderQueueFontSize.Value = settings.QueueFontSizeModifier;
        SliderQueueButtonSize.Value = settings.DmdQueueButtonSizeModifier;
        SliderPlayButtonSize.Value = settings.DmdPlayButtonSizeModifier;
        SliderGenreIconSize.Value = settings.DmdGenreIconSizeModifier;
        SliderTrackButtonSize.Value = settings.DmdTrackButtonSizeModifier;

        foreach (var loc in new[] { "Playbar", "Queue" })
            CbMinorButtonLocation.Items.Add(loc);
        CbMinorButtonLocation.SelectedIndex = (int)settings.DmdMinorButtonLocation;

        // Category visibility
        foreach (var entry in GenreCategoryStore.Load())
        {
            _categoryVisibilityItems.Add(new CategoryVisibilityItem
            {
                Id = entry.Id,
                Name = entry.Name,
                Icon = entry.Icon,
                SearchTerm = entry.SearchTerm,
                OriginalSearchTerm = entry.SearchTerm,
                IsVisible = !settings.HiddenCategories.Contains(entry.Name),
                IsSpecial = entry.Name == "History",
                IsSeparator = entry.IsSeparator,
                IsLineBreak = entry.IsLineBreak,
                IsPlex = entry.IsPlex,
                PlexLibraryKey = entry.PlexLibraryKey,
                PlexLibraryType = entry.PlexLibraryType,
                PlexHubsEnabled = entry.PlexHubsEnabled,
                PlexPlaylistsEnabled = entry.PlexPlaylistsEnabled
            });
        }
        CategoryListView.ItemsSource = _categoryVisibilityItems;
        UpdateCategoryVisibilityText();

        // Hide cursor timeout dropdown
        var cursorTimeouts = new (string label, int seconds)[] {
            ("Never", -1), ("Immediately", 0), ("5 seconds", 5), ("10 seconds", 10),
            ("15 seconds", 15), ("30 seconds", 30), ("45 seconds", 45), ("60 seconds", 60),
            ("2 minutes", 120), ("3 minutes", 180), ("4 minutes", 240), ("5 minutes", 300),
            ("6 minutes", 360), ("7 minutes", 420), ("8 minutes", 480), ("9 minutes", 540), ("10 minutes", 600)
        };
        foreach (var t in cursorTimeouts)
            CbHideCursorTimeout.Items.Add(t.label);
        CbHideCursorTimeout.SelectedIndex = 0;
        for (int i = 0; i < cursorTimeouts.Length; i++)
            if (cursorTimeouts[i].seconds == settings.HideCursorTimeoutSeconds)
            { CbHideCursorTimeout.SelectedIndex = i; break; }

        CbShowStatusText.IsChecked = settings.ShowStatusText;
        CbCacheEnabled.IsChecked = settings.CacheEnabled;
        CbPrefetchEnabled.IsChecked = settings.PrefetchEnabled;
        var cacheSizeOptions = new (string label, double gb)[] { ("1 GB", 1), ("2 GB", 2), ("5 GB", 5), ("10 GB", 10), ("25 GB", 25), ("50 GB", 50), ("100 GB", 100), ("250 GB", 250), ("500 GB", 500), ("1 TB", 1024), ("2 TB", 2048), ("5 TB", 5120), ("Unlimited", 0) };
        int selectedCacheSizeIndex = 2; // default 5 GB
        for (int ci = 0; ci < cacheSizeOptions.Length; ci++)
        {
            CbCacheMaxSize.Items.Add(cacheSizeOptions[ci].label);
            if (Math.Abs(cacheSizeOptions[ci].gb - settings.CacheMaxSizeGb) < 0.1)
                selectedCacheSizeIndex = ci;
        }
        CbCacheMaxSize.SelectedIndex = selectedCacheSizeIndex;

        // Cache mode dropdown
        CbCacheMode.Items.Add("Cache playlists");
        CbCacheMode.Items.Add("Cache everything");
        CbCacheMode.SelectedIndex = (int)settings.CacheMode;

        // Max clip length dropdown (0 = No limit, then 1-30 minutes)
        CbCacheMaxClipLength.Items.Add("No limit");
        for (int m = 1; m <= 30; m++)
            CbCacheMaxClipLength.Items.Add(m == 1 ? "1 minute" : $"{m} minutes");
        CbCacheMaxClipLength.SelectedIndex = Math.Clamp(settings.CacheMaxClipLengthMinutes, 0, 30);

        // Thumbnail cache
        CbThumbnailCacheEnabled.IsChecked = settings.ThumbnailCacheEnabled;
        var thumbSizeOptions = new (string label, double mb)[] { ("250 MB", 250), ("500 MB", 500), ("1 GB", 1024), ("2 GB", 2048), ("5 GB", 5120) };
        int selectedThumbIndex = 1; // default 500 MB
        for (int ti = 0; ti < thumbSizeOptions.Length; ti++)
        {
            CbThumbnailCacheMaxSize.Items.Add(thumbSizeOptions[ti].label);
            if (Math.Abs(thumbSizeOptions[ti].mb - settings.ThumbnailCacheMaxSizeMb) < 1)
                selectedThumbIndex = ti;
        }
        CbThumbnailCacheMaxSize.SelectedIndex = selectedThumbIndex;

        // Category cache
        CbCategoryCacheEnabled.IsChecked = settings.CategoryCacheEnabled;
        var ageOptions = new (string label, int hours)[]
        {
            ("1 hour", 1), ("2 hours", 2), ("4 hours", 4), ("6 hours", 6), ("12 hours", 12),
            ("1 day", 24), ("2 days", 48), ("3 days", 72), ("5 days", 120), ("7 days", 168),
            ("14 days", 336), ("21 days", 504), ("30 days", 720),
            ("2 months", 1440), ("3 months", 2160), ("4 months", 2880),
            ("5 months", 3600), ("6 months", 4320)
        };
        int selectedAgeIndex = 9; // default 7 days
        for (int ai = 0; ai < ageOptions.Length; ai++)
        {
            CbCategoryCacheMaxAge.Items.Add(ageOptions[ai].label);
            if (ageOptions[ai].hours == settings.CategoryCacheMaxAgeHours)
                selectedAgeIndex = ai;
        }
        CbCategoryCacheMaxAge.SelectedIndex = selectedAgeIndex;

        // YouTube playlist cache
        CbYtPlaylistCacheEnabled.IsChecked = settings.YtPlaylistCacheEnabled;
        int selectedYtPlAgeIndex = 9;
        for (int ai = 0; ai < ageOptions.Length; ai++)
        {
            CbYtPlaylistCacheMaxAge.Items.Add(ageOptions[ai].label);
            if (ageOptions[ai].hours == settings.YtPlaylistCacheMaxAgeHours)
                selectedYtPlAgeIndex = ai;
        }
        CbYtPlaylistCacheMaxAge.SelectedIndex = selectedYtPlAgeIndex;

        // Plex playlist cache
        CbPlexPlaylistCacheEnabled.IsChecked = settings.PlexPlaylistCacheEnabled;
        int selectedPlexAgeIndex = 9;
        for (int ai = 0; ai < ageOptions.Length; ai++)
        {
            CbPlexPlaylistCacheMaxAge.Items.Add(ageOptions[ai].label);
            if (ageOptions[ai].hours == settings.PlexPlaylistCacheMaxAgeHours)
                selectedPlexAgeIndex = ai;
        }
        CbPlexPlaylistCacheMaxAge.SelectedIndex = selectedPlexAgeIndex;

        // Debug
        CbDebugLogging.IsChecked = settings.DebugLogging;
        UpdateDebugLogPathText();
        UpdateCrashLogStatus();

        // Track list settings
        foreach (var c in new[] { 1, 2, 3, 4 })
            CbResultColumns.Items.Add(c);
        CbResultColumns.SelectedItem = settings.ResultColumns;

        SliderResultFontSize.Value = settings.ResultFontSizeModifier;

        // Screensaver settings (slider values: intensity 5-80 maps to 0.05-0.80, speed 1-50 maps to 0.1-5.0)
        SliderIntensity.Value = settings.ScreensaverIntensity * 100;
        SliderSpeed.Value = settings.ScreensaverSpeed * 10;
        IntensityValueText.Text = $"{(int)(settings.ScreensaverIntensity * 100)}%";
        SpeedValueText.Text = $"{settings.ScreensaverSpeed:F1}×";

        // Store original values for cancel revert
        _originalIntensity = settings.ScreensaverIntensity;
        _originalSpeed = settings.ScreensaverSpeed;
        _originalDistortion = settings.TopperDistortion;
        _originalPlayfieldBlobPattern = settings.PlayfieldBlobPattern;
        _originalPlayfieldBlobCount = settings.PlayfieldBlobCount;
        _originalPlayfieldBlobSizeOffset = settings.PlayfieldBlobSizeOffset;
        _originalPlayfieldRotation = settings.PlayfieldRotation;
        _originalBackglassBlobPattern = settings.BackglassBlobPattern;
        _originalBackglassBlobCount = settings.BackglassBlobCount;
        _originalBackglassBlobSizeOffset = settings.BackglassBlobSizeOffset;
        _originalTopperBlobPattern = settings.TopperBlobPattern;
        _originalTopperBlobCount = settings.TopperBlobCount;
        _originalTopperBlobSizeOffset = settings.TopperBlobSizeOffset;
        _originalDmdBlobPattern = settings.DmdBlobPattern;
        _originalDmdBlobCount = settings.DmdBlobCount;
        _originalDmdBlobSizeOffset = settings.DmdBlobSizeOffset;
        _originalDmdRotation = settings.DmdRotation;
        _originalReactiveBlobs = settings.ReactiveBlobs;
        _originalReactiveProjectM = settings.ReactiveProjectM;
        _originalReactivityThreshold = settings.ReactivityThreshold;
        _originalReactiveSpeedMs = settings.ReactiveSpeedMs;
        _originalReactiveOverdrive = settings.ReactiveOverdrive;
        _originalTitleText = settings.TitleText;
        _originalLogoText = settings.LogoText;
        _originalLogoSpin = settings.LogoSpin;
        _originalLogoRings = settings.LogoRings;
        _originalLogoMorphColor = settings.BackglassLogoMorphColor;
        _originalMandelbrotUseGpu = settings.MandelbrotUseGpu;
        _originalMandelbrotAdaptiveIterations = settings.MandelbrotAdaptiveIterations;
        _originalMandelbrotMaxIterations = settings.MandelbrotMaxIterations;
        _originalMandelbrotMaxHz = settings.MandelbrotMaxHz;
        _originalMandelbrotRenderScale = settings.MandelbrotRenderScale;
        _originalMandelbrotPerturbation = settings.MandelbrotPerturbation;
        _originalMandelbrotDiscovery = settings.MandelbrotDiscovery;
        _originalMandelbrotDimming = settings.MandelbrotDimming;
        _originalProjectMPresetDuration = settings.ProjectMPresetDuration;
        _originalProjectMSoftCut = settings.ProjectMSoftCutDuration;
        _originalProjectMHardCut = settings.ProjectMHardCutEnabled;
        _originalProjectMBeatSensitivity = settings.ProjectMBeatSensitivity;
        _originalProjectMMeshSize = settings.ProjectMMeshSize;
        _originalProjectMRenderScale = settings.ProjectMRenderScale;
        _originalProjectMPresetPath = settings.ProjectMPresetPath;
        _originalProjectMTexturePath = settings.ProjectMTexturePath;
        _originalProjectMEnabledFolders = new List<string>(settings.ProjectMEnabledFolders);
        _originalProjectMSoftwareRender = settings.ProjectMSoftwareRender;
        _originalDofEnabled = settings.DofEnabled;
        _originalDofColorBand = settings.DofColorBand;
        _originalDofPresetChanged = settings.DofPresetChanged;
        _originalDofRomName = settings.DofRomName;
        CbVideoQuality.Items.Add("Low (480p)");
        CbVideoQuality.Items.Add("Medium (720p)");
        CbVideoQuality.Items.Add("High (1080p)");
        CbVideoQuality.Items.Add("Max (4k)");
        CbVideoQuality.SelectedIndex = (int)settings.VideoQuality;
        UpdateQualityHint(settings.VideoQuality);
        CbStereoAudio.IsChecked = settings.StereoAudio;

        // Network
        SliderNetworkCaching.Value = settings.NetworkCachingMs;
        SliderLiveCaching.Value = settings.LiveCachingMs;
        SliderFileCaching.Value = settings.FileCachingMs;
        CbHttpReconnect.IsChecked = settings.HttpReconnect;
        NetworkCachingValueText.Text = settings.NetworkCachingMs.ToString();
        LiveCachingValueText.Text = settings.LiveCachingMs.ToString();
        FileCachingValueText.Text = settings.FileCachingMs.ToString();

        // Plex
        TbPlexUrl.Text = settings.PlexServerUrl;
        TbPlexToken.Text = settings.PlexToken;
        CbPlexStereo.IsChecked = settings.PlexStereoAudio;
        foreach (var lib in settings.PlexLibraries)
            _plexLibraries.Add(new PlexLibraryMapping { Key = lib.Key, Title = lib.Title, Type = lib.Type, HubsEnabled = lib.HubsEnabled, PlaylistsEnabled = lib.PlaylistsEnabled });
        PlexLibraryList.ItemsSource = _plexLibraries;

        // Auto-load Plex libraries if configured
        if (!string.IsNullOrWhiteSpace(settings.PlexServerUrl) && !string.IsNullOrWhiteSpace(settings.PlexToken))
            Loaded += async (_, _) => await TryLoadPlexLibrariesAsync();

        HistoryCountText.Text = $"{settings.KeyBindings.ToEntries().Count} bindings configured";

        // Restore saved window position if it's visible on a current monitor
        if (settings.SettingsWindowLayout is { } layout)
        {
            Left = layout.Left;
            Top = layout.Top;
            EnsureVisibleOnScreen();
        }

        Closing += (_, _) =>
        {
            SaveAllScrollOffsets();
            SettingsWindowState.LastTabIndex = SettingsTabs.SelectedIndex;

            if (_testDofClient != null && _testDofClient != _sharedDofClient)
                _testDofClient.Dispose();
            _testDofClient = null;
            _settings.SettingsWindowLayout = new WindowLayout
            {
                Left = Left,
                Top = Top,
                Width = Width,
                Height = Height
            };
            _ = _settings.SaveAsync();
        };

        // Restore last tab and scroll position
        SettingsTabs.SelectedIndex = SettingsWindowState.LastTabIndex;

        SettingsTabs.SelectionChanged += (_, e) =>
        {
            if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is System.Windows.Controls.TabItem oldTab)
            {
                int oldIndex = SettingsTabs.Items.IndexOf(oldTab);
                var sv = GetTabScrollViewer(oldTab);
                if (sv != null)
                    SettingsWindowState.ScrollOffsets[oldIndex] = sv.VerticalOffset;
            }

            SettingsWindowState.LastTabIndex = SettingsTabs.SelectedIndex;
            RestoreScrollOffset(SettingsTabs.SelectedIndex);
        };

        Loaded += (_, _) =>
        {
            RestoreScrollOffset(SettingsTabs.SelectedIndex);
        };
    }

    public void SetBackglassProxy(BackglassProxy? backglass)
    {
        _backglassProxy = backglass;
    }

    private void SaveAllScrollOffsets()
    {
        for (int i = 0; i < SettingsTabs.Items.Count; i++)
        {
            if (SettingsTabs.Items[i] is System.Windows.Controls.TabItem tab)
            {
                var sv = GetTabScrollViewer(tab);
                if (sv is not null)
                    SettingsWindowState.ScrollOffsets[i] = sv.VerticalOffset;
            }
        }
    }

    private void RestoreScrollOffset(int tabIndex)
    {
        if (tabIndex < 0 || tabIndex >= SettingsTabs.Items.Count) return;
        if (SettingsTabs.Items[tabIndex] is not System.Windows.Controls.TabItem tab) return;

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            var sv = GetTabScrollViewer(tab);
            if (sv is not null && SettingsWindowState.ScrollOffsets.TryGetValue(tabIndex, out var offset))
                sv.ScrollToVerticalOffset(offset);
        });
    }

    private static System.Windows.Controls.ScrollViewer? GetTabScrollViewer(System.Windows.Controls.TabItem tab)
    {
        // The ScrollViewer is the direct Content of the TabItem in XAML,
        // not a visual child of the TabItem (WPF hosts tab content in the
        // TabControl's ContentPresenter, not under the TabItem visual tree).
        if (tab.Content is System.Windows.Controls.ScrollViewer sv)
            return sv;

        // For tabs whose content is a Border or other container, walk the
        // logical content tree to find a nested ScrollViewer.
        if (tab.Content is DependencyObject content)
            return FindScrollViewerInVisualTree(content);

        return null;
    }

    private static System.Windows.Controls.ScrollViewer? FindScrollViewerInVisualTree(DependencyObject parent)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is System.Windows.Controls.ScrollViewer sv)
                return sv;
            var result = FindScrollViewerInVisualTree(child);
            if (result is not null)
                return result;
        }
        return null;
    }

    public void SetTopperWindow(TopperWindow? topper)
    {
        _topperWindow = topper;
    }

    public void SetDofClient(DofClient? dofClient)
    {
        _sharedDofClient = dofClient;
    }

    public void SetPlayfieldProxy(PlayfieldProxy? proxy)
    {
        _playfieldProxy = proxy;
    }

    private void BrowseStaticImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Playfield Image",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff|All files|*.*"
        };
        if (!string.IsNullOrWhiteSpace(TbStaticImagePath.Text))
        {
            var dir = System.IO.Path.GetDirectoryName(TbStaticImagePath.Text);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }
        if (dlg.ShowDialog(this) == true)
            TbStaticImagePath.Text = dlg.FileName;
    }

    private void BrowseStartupDitti_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Startup Ditti Audio",
            Filter = "Audio files|*.mp3;*.m4a;*.ogg;*.wav;*.flac;*.wma;*.aac|All files|*.*"
        };
        if (!string.IsNullOrWhiteSpace(TbStartupDittiPath.Text))
        {
            var dir = System.IO.Path.GetDirectoryName(TbStartupDittiPath.Text);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }
        if (dlg.ShowDialog(this) == true)
            TbStartupDittiPath.Text = dlg.FileName;
    }

    private void BrowseVideo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Playfield Video",
            Filter = "Video files|*.mp4;*.avi;*.wmv;*.mkv;*.mov|All files|*.*"
        };
        if (!string.IsNullOrWhiteSpace(TbVideoPath.Text))
        {
            var dir = System.IO.Path.GetDirectoryName(TbVideoPath.Text);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }
        if (dlg.ShowDialog(this) == true)
            TbVideoPath.Text = dlg.FileName;
    }

    public void SetHistoryCount(int count)
    {
        HistoryCountText.Text = $"{count} items in history";
    }

    public void SetCacheSize(long bytes)
    {
        double mb = bytes / 1024.0 / 1024.0;
        CacheSizeText.Text = mb >= 1024
            ? $"({mb / 1024:F1} GB used)"
            : $"({mb:F0} MB used)";
    }

    public void SetThumbnailCacheSize(long bytes)
    {
        double mb = bytes / 1024.0 / 1024.0;
        ThumbnailCacheSizeText.Text = mb >= 1024
            ? $"({mb / 1024:F1} GB used)"
            : $"({mb:F0} MB used)";
    }

    public void SetCategoryCacheSize(long bytes)
    {
        double kb = bytes / 1024.0;
        CategoryCacheSizeText.Text = kb >= 1024
            ? $"({kb / 1024:F1} MB used)"
            : $"({kb:F0} KB used)";
    }

    public void SetYtPlaylistCacheSize(long bytes)
    {
        double kb = bytes / 1024.0;
        YtPlaylistCacheSizeText.Text = kb >= 1024
            ? $"({kb / 1024:F1} MB used)"
            : $"({kb:F0} KB used)";
    }

    public void SetPlexPlaylistCacheSize(long bytes)
    {
        double kb = bytes / 1024.0;
        PlexPlaylistCacheSizeText.Text = kb >= 1024
            ? $"({kb / 1024:F1} MB used)"
            : $"({kb:F0} KB used)";
    }

    private void PurgeVideoCache_Click(object sender, RoutedEventArgs e)
    {
        if (DarkConfirmDialog.Confirm("Purge Video Cache", "Are you sure you want to purge the video cache?", this)
            && Owner?.DataContext is JukeboxViewModel vm)
        {
            vm.Cache?.Purge();
            SetCacheSize(0);
        }
    }

    private void PurgeThumbnailCache_Click(object sender, RoutedEventArgs e)
    {
        if (DarkConfirmDialog.Confirm("Purge Thumbnail Cache", "Are you sure you want to purge the thumbnail cache?", this)
            && Owner?.DataContext is JukeboxViewModel vm)
        {
            vm.ThumbnailCache?.Purge();
            SetThumbnailCacheSize(0);
        }
    }

    private void PurgeCategoryCache_Click(object sender, RoutedEventArgs e)
    {
        if (DarkConfirmDialog.Confirm("Purge Category Cache", "Are you sure you want to purge the category cache?", this)
            && Owner?.DataContext is JukeboxViewModel vm)
        {
            vm.CategoryCache?.Purge();
            SetCategoryCacheSize(0);
        }
    }

    private void PurgeYtPlaylistCache_Click(object sender, RoutedEventArgs e)
    {
        if (DarkConfirmDialog.Confirm("Purge YouTube Playlist Cache", "Are you sure you want to purge the YouTube playlist cache?", this)
            && Owner?.DataContext is JukeboxViewModel vm)
        {
            vm.YtPlaylistCache?.Purge();
            SetYtPlaylistCacheSize(0);
        }
    }

    private void PurgePlexPlaylistCache_Click(object sender, RoutedEventArgs e)
    {
        if (DarkConfirmDialog.Confirm("Purge Plex Playlist Cache", "Are you sure you want to purge the Plex playlist cache?", this)
            && Owner?.DataContext is JukeboxViewModel vm)
        {
            vm.PlexPlaylistCache?.Purge();
            SetPlexPlaylistCacheSize(0);
        }
    }

    private void UpdateDebugLogPathText()
    {
        var filename = $"Phosphor_Debug_{DateTime.Now:yyyyMMdd}.log";
        DebugLogPathText.Text = $"Writes to logs/{filename}";
    }

    private void OpenDebugLog_Click(object sender, RoutedEventArgs e)
    {
        var filename = $"Phosphor_Debug_{DateTime.Now:yyyyMMdd}.log";
        var logsDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        var path = System.IO.Path.Combine(logsDir, filename);
        if (System.IO.File.Exists(path))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\""
            });
        }
        else
        {
            System.IO.Directory.CreateDirectory(logsDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{logsDir}\""
            });
        }
    }

    private string GetCrashLogPath() =>
        System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "crash.log");

    private void UpdateCrashLogStatus()
    {
        var path = GetCrashLogPath();
        if (System.IO.File.Exists(path))
        {
            var info = new System.IO.FileInfo(path);
            CrashLogStatusText.Text = $"crash.log ({info.Length / 1024.0:F1} KB, {info.LastWriteTime:g})";
            BtnOpenCrashLog.Visibility = Visibility.Visible;
            BtnDeleteCrashLog.Visibility = Visibility.Visible;
        }
        else
        {
            CrashLogStatusText.Text = "No crash file.";
            BtnOpenCrashLog.Visibility = Visibility.Collapsed;
            BtnDeleteCrashLog.Visibility = Visibility.Collapsed;
        }
    }

    private void OpenCrashLog_Click(object sender, RoutedEventArgs e)
    {
        var path = GetCrashLogPath();
        if (!System.IO.File.Exists(path)) return;

        try
        {
            var content = System.IO.File.ReadAllText(path);
            var win = new JukeboxWindow
            {
                Title = "Crash Log",
                Width = 700,
                Height = 500,
                Background = (System.Windows.Media.Brush)FindResource("BgBrush"),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };
            var tb = new System.Windows.Controls.TextBox
            {
                Text = content,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush"),
                Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 11,
                Margin = new Thickness(8)
            };
            var grid = new System.Windows.Controls.Grid();
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            System.Windows.Controls.Grid.SetRow(tb, 0);
            grid.Children.Add(tb);
            var closeBtn = new System.Windows.Controls.Button
            {
                Content = "Close",
                Padding = new Thickness(20, 8, 20, 8),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(8)
            };
            closeBtn.Click += (_, _) => win.Close();
            System.Windows.Controls.Grid.SetRow(closeBtn, 1);
            grid.Children.Add(closeBtn);
            win.Content = grid;
            win.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to read crash log: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteCrashLog_Click(object sender, RoutedEventArgs e)
    {
        var path = GetCrashLogPath();
        if (!System.IO.File.Exists(path)) return;

        try
        {
            System.IO.File.Delete(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to delete crash log: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        UpdateCrashLogStatus();
    }

    private void RebindPrimaryKey_Click(object sender, RoutedEventArgs e)
    {
        StartRebind(sender as Button, isPrimary: true);
    }

    private void RebindCabinetButton_Click(object sender, RoutedEventArgs e)
    {
        StartRebind(sender as Button, isPrimary: false);
    }

    private void ClearPrimaryKey_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is KeyBindingEntry entry)
        {
            entry.PrimaryKey = Key.None;
            BindingsList.Items.Refresh();
        }
    }

    private void ClearCabinetButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is KeyBindingEntry entry)
        {
            entry.CabinetButton = Key.None;
            entry.ClearDInputBinding();
            BindingsList.Items.Refresh();
        }
    }

    private void TestModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (TestModeToggle.IsChecked != true) return;

        _testModeActive = true;
        PreviewKeyDown += OnTestModeKey;
        PreviewKeyUp += OnTestModeKeyUp;
        TestModeStatusText.Text = "Press a key…";

        // Pause DmdWindow's DInput poller and start our own for test mode
        (Owner as DmdWindow)?.PauseDirectInput();
        _testDInputPoller?.Dispose();
        _testDInputPoller = new DirectInputPoller();
        _testDInputPoller.ButtonPressed += OnTestModeDInputButton;
        _testDInputPoller.Start();
    }

    private void TestModeToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_testModeActive)
            DisableTestMode();
    }

    private void DisableTestMode()
    {
        _testModeActive = false;
        TestModeToggle.IsChecked = false;
        PreviewKeyDown -= OnTestModeKey;
        PreviewKeyUp -= OnTestModeKeyUp;
        TestModeStatusText.Text = "";
        ClearTestHighlight();

        // Stop test DInput poller and resume DmdWindow's
        if (_testDInputPoller != null)
        {
            _testDInputPoller.ButtonPressed -= OnTestModeDInputButton;
            _testDInputPoller.Dispose();
            _testDInputPoller = null;
        }
        (Owner as DmdWindow)?.ResumeDirectInput();
    }

    private void SettingsTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Only respond to actual tab changes, not ListView selection bubbling up
        if (e.OriginalSource != sender) return;
        if (_testModeActive)
            DisableTestMode();
    }

    private void OnTestModeKey(object sender, KeyEventArgs e)
    {
        if (_rebindingButton != null) return; // rebind in progress, ignore

        var pressed = e.Key == Key.System ? e.SystemKey : e.Key;

        // Find matching entry
        KeyBindingEntry? matched = null;
        foreach (var entry in _entries)
        {
            if ((entry.PrimaryKey != Key.None && entry.PrimaryKey == pressed) ||
                (entry.CabinetButton != Key.None && entry.CabinetButton == pressed))
            {
                matched = entry;
                break;
            }
        }

        ClearTestHighlight();

        if (matched != null)
        {
            TestModeStatusText.Text = $"KEY:\n{pressed}\n({matched.DisplayName})";
            BindingsList.SelectedItem = matched;

            // Highlight the row
            BindingsList.UpdateLayout();
            if (BindingsList.ItemContainerGenerator.ContainerFromItem(matched)
                is System.Windows.Controls.ListViewItem lvi)
            {
                lvi.Background = (System.Windows.Media.Brush)FindResource("AccentBrush");
                lvi.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
            }
        }
        else
        {
            TestModeStatusText.Text = $"KEY:\n{pressed}\n(unbound)";
        }

        e.Handled = true;
    }

    private void OnTestModeKeyUp(object sender, KeyEventArgs e)
    {
        ClearTestHighlight();
        TestModeStatusText.Text = "Press a key…";
        e.Handled = true;
    }

    private void OnTestModeDInputButton(Guid deviceGuid, int buttonIndex)
    {
        // Find matching entry by DInput binding
        KeyBindingEntry? matched = null;
        foreach (var entry in _entries)
        {
            if (entry.HasDInputBinding &&
                entry.CabinetDInputDeviceGuid == deviceGuid &&
                entry.CabinetDInputButton == buttonIndex)
            {
                matched = entry;
                break;
            }
        }

        ClearTestHighlight();

        var buttonLabel = $"Joy Btn {buttonIndex + 1}";
        if (matched != null)
        {
            TestModeStatusText.Text = $"KEY:\n{buttonLabel}\n({matched.DisplayName})";
            BindingsList.SelectedItem = matched;

            BindingsList.UpdateLayout();
            if (BindingsList.ItemContainerGenerator.ContainerFromItem(matched)
                is System.Windows.Controls.ListViewItem lvi)
            {
                lvi.Background = (System.Windows.Media.Brush)FindResource("AccentBrush");
                lvi.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
            }
        }
        else
        {
            TestModeStatusText.Text = $"KEY:\n{buttonLabel}\n(unbound)";
        }
    }

    private void ClearTestHighlight()
    {
        foreach (var item in _entries)
        {
            if (BindingsList.ItemContainerGenerator.ContainerFromItem(item)
                is System.Windows.Controls.ListViewItem lvi)
            {
                lvi.Background = System.Windows.Media.Brushes.Transparent;
                lvi.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
            }
        }
        BindingsList.SelectedItem = null;
    }

    private void StartRebind(Button? btn, bool isPrimary)
    {
        if (btn == null) return;

        _rebindingButton = btn;
        _rebindingCabinet = !isPrimary;
        btn.Content = isPrimary ? "Press a key..." : "Press key or button...";
        btn.Background = (System.Windows.Media.Brush)FindResource("AccentBrush");

        PreviewKeyDown += OnCaptureKey;

        if (_rebindingCabinet)
            StartDInputCapture();
    }

    private void StartDInputCapture()
    {
        _dinputPoller?.Dispose();
        _dinputPoller = new DirectInputPoller();
        _dinputPoller.ButtonPressed += OnDInputButtonCaptured;
        _dinputPoller.Start();
    }

    private void StopDInputCapture()
    {
        if (_dinputPoller != null)
        {
            _dinputPoller.ButtonPressed -= OnDInputButtonCaptured;
            _dinputPoller.Dispose();
            _dinputPoller = null;
        }
    }

    private void OnDInputButtonCaptured(Guid deviceGuid, int buttonIndex)
    {
        // This fires on the dispatcher thread (DispatcherTimer)
        PreviewKeyDown -= OnCaptureKey;
        StopDInputCapture();

        if (_rebindingButton?.Tag is not KeyBindingEntry entry) return;

        entry.SetDInputBinding(deviceGuid, buttonIndex);

        _rebindingButton.Content = entry.CabinetButtonDisplay;
        _rebindingButton.Background = (System.Windows.Media.Brush)FindResource("Surface2Brush");
        _rebindingButton = null;
    }

    private void OnCaptureKey(object sender, KeyEventArgs e)
    {
        PreviewKeyDown -= OnCaptureKey;
        StopDInputCapture();

        if (_rebindingButton?.Tag is not KeyBindingEntry entry) return;

        var captured = e.Key == Key.System ? e.SystemKey : e.Key;

        if (_rebindingCabinet)
        {
            entry.CabinetButton = captured;
            entry.ClearDInputBinding();
        }
        else
            entry.PrimaryKey = captured;

        _rebindingButton.Content = _rebindingCabinet ? entry.CabinetButtonDisplay : entry.PrimaryKeyDisplay;
        _rebindingButton.Background = (System.Windows.Media.Brush)FindResource("Surface2Brush");
        _rebindingButton = null;
        e.Handled = true;
    }

    private void PurgeHistory_Click(object sender, RoutedEventArgs e)
    {
        if (DarkConfirmDialog.Confirm("Purge History", "Are you sure you want to clear all play history?", this)
            && Owner?.DataContext is JukeboxViewModel vm)
        {
            vm.PurgeHistory();
            HistoryCountText.Text = "0 items in history";
        }
    }

    private void ClearSearchHistory_Click(object sender, RoutedEventArgs e)
    {
        if (DarkConfirmDialog.Confirm("Clear Search History", "Are you sure you want to clear search history?", this)
            && Owner?.DataContext is JukeboxViewModel vm)
            vm.ClearSearchHistory();
    }

    private void ResetWindows_Click(object sender, RoutedEventArgs e)
    {
        WindowsReset = true;
    }

    private void ResetDmdWindow_Click(object sender, RoutedEventArgs e)
    {
        (Owner as JukeboxWindow)?.ResetPosition(1, 1, 800, 600);
    }

    private void ResetBackglassWindow_Click(object sender, RoutedEventArgs e)
    {
        _backglassProxy?.ResetPosition(1, 1, 800, 600);
    }

    private void ResetPlayfieldWindow_Click(object sender, RoutedEventArgs e)
    {
        _playfieldProxy?.ResetPosition(1, 1, 600, 800);
    }

    private void ResetTopperWindow_Click(object sender, RoutedEventArgs e)
    {
        _topperWindow?.ResetPosition(1, 1, 800, 300);
    }

    private void SliderIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IntensityValueText != null)
            IntensityValueText.Text = $"{(int)e.NewValue}%";
        if (SliderSpeed == null) return;
        _backglassProxy?.SetScreensaverSettings(e.NewValue / 100.0, SliderSpeed.Value / 10.0);
        _playfieldProxy?.SetScreensaverSettings(e.NewValue / 100.0, SliderSpeed.Value / 10.0);
        _topperWindow?.SetScreensaverSettings(e.NewValue / 100.0, SliderSpeed.Value / 10.0);
    }

    private void SliderSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SpeedValueText != null)
            SpeedValueText.Text = $"{e.NewValue / 10.0:F1}×";
    }

    private void BtnTitleTextDefault_Click(object sender, RoutedEventArgs e)
    {
        TbTitleText.Text = "\uD83C\uDFB5 PHOSPHOR";
    }

    private void BtnLogoTextDefault_Click(object sender, RoutedEventArgs e)
    {
        TbLogoText.Text = "\u2022 PHOSPHOR \u2022 PHOSPHOR ";
    }

    private void SliderDistortion_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DistortionValueText != null)
            DistortionValueText.Text = $"{e.NewValue / 100.0:F2}";
        _topperWindow?.SetDistortion(e.NewValue / 100.0);
    }

    private void SliderReactivityThreshold_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtReactivityThreshold != null)
            TxtReactivityThreshold.Text = $"{(int)e.NewValue}%";
    }

    private void SliderReactiveSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtReactiveSpeed != null)
            TxtReactiveSpeed.Text = $"{(int)e.NewValue}ms";
    }

    private void SliderReactiveOverdrive_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtReactiveOverdrive != null)
            TxtReactiveOverdrive.Text = $"{e.NewValue / 10.0:F1}×";
    }

    private void SliderLogoRings_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // No live preview needed; value is read on save
    }

    private void SliderOledIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtOledIntensity != null)
            TxtOledIntensity.Text = $"{(int)e.NewValue}%";
    }

    private void SliderBlobCountPlayfield_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtBlobCountPlayfield != null)
            TxtBlobCountPlayfield.Text = $"{(int)e.NewValue}";
    }

    private void SliderBlobCountBackglass_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtBlobCountBackglass != null)
            TxtBlobCountBackglass.Text = $"{(int)e.NewValue}";
    }

    private void SliderBlobCountTopper_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtBlobCountTopper != null)
            TxtBlobCountTopper.Text = $"{(int)e.NewValue}";
    }

    private void SliderBlobCountDmd_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtBlobCountDmd != null)
            TxtBlobCountDmd.Text = $"{(int)e.NewValue}";
    }

    private void SliderBlobSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var slider = (System.Windows.Controls.Slider)sender;
        string name = slider.Name;
        int val = (int)e.NewValue;
        string text = val == 0 ? "Default" : $"{val * 5:+#;-#}%";
        System.Windows.Controls.TextBlock? label = name switch
        {
            "SliderBlobSizePlayfield" => TxtBlobSizePlayfield,
            "SliderBlobSizeBackglass" => TxtBlobSizeBackglass,
            "SliderBlobSizeTopper" => TxtBlobSizeTopper,
            "SliderBlobSizeDmd" => TxtBlobSizeDmd,
            _ => null
        };
        if (label != null) label.Text = text;
    }

    private void SliderMandelbrotMaxHz_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtMandelbrotMaxHz != null)
            TxtMandelbrotMaxHz.Text = (int)e.NewValue == 0 ? "Unlimited" : $"{(int)e.NewValue} Hz";
    }

    private void SliderMandelbrotMaxIterations_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtMandelbrotMaxIterations != null)
            TxtMandelbrotMaxIterations.Text = $"{(int)e.NewValue}";
    }

    private void SliderMandelbrotRenderScale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtMandelbrotRenderScale != null)
            TxtMandelbrotRenderScale.Text = $"{(int)e.NewValue}%";
    }

    private void SliderMandelbrotPerturbation_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtMandelbrotPerturbation != null)
            TxtMandelbrotPerturbation.Text = (int)e.NewValue == 0 ? "Off" : $"{(int)e.NewValue}%";
    }

    private void SliderMandelbrotDimming_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtMandelbrotDimming != null)
            TxtMandelbrotDimming.Text = (int)e.NewValue == 0 ? "Off" : $"{(int)e.NewValue}%";
    }

    private void CbBlobPattern_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateMandelbrotTuningVisibility();
        UpdateProjectMTuningVisibility();
        UpdateBlobCountSliderStates();
    }

    /// <summary>
    /// Hides blob-count and blob-size sliders and their labels for patterns that don't use blobs (ProjectM, Mandelbrot).
    /// </summary>
    private void UpdateBlobCountSliderStates()
    {
        SetSlidersForPattern(CbBlobPatternPlayfield, SliderBlobCountPlayfield, TxtBlobCountPlayfield, SliderBlobSizePlayfield, TxtBlobSizePlayfield);
        SetSlidersForPattern(CbBlobPatternBackglass, SliderBlobCountBackglass, TxtBlobCountBackglass, SliderBlobSizeBackglass, TxtBlobSizeBackglass);
        SetSlidersForPattern(CbBlobPatternTopper, SliderBlobCountTopper, TxtBlobCountTopper, SliderBlobSizeTopper, TxtBlobSizeTopper);
        SetSlidersForPattern(CbBlobPatternDmd, SliderBlobCountDmd, TxtBlobCountDmd, SliderBlobSizeDmd, TxtBlobSizeDmd);

        static void SetSlidersForPattern(System.Windows.Controls.ComboBox? cb, System.Windows.Controls.Slider? countSlider, System.Windows.Controls.TextBlock? countLabel,
            System.Windows.Controls.Slider? sizeSlider, System.Windows.Controls.TextBlock? sizeLabel)
        {
            if (cb == null || countSlider == null) return;
            var name = cb.SelectedItem as string ?? "";
            var visible = name != "ProjectM" && name != "Mandelbrot";
            var vis = visible ? Visibility.Visible : Visibility.Collapsed;
            countSlider.Visibility = vis;
            if (countLabel != null) countLabel.Visibility = vis;
            if (sizeSlider != null) sizeSlider.Visibility = vis;
            if (sizeLabel != null) sizeLabel.Visibility = vis;
        }
    }

    private void UpdateMandelbrotTuningVisibility()
    {
        if (PanelMandelbrotTuning == null) return;

        bool anyMandelbrot = false;
        foreach (var cb in new[] { CbBlobPatternPlayfield, CbBlobPatternBackglass, CbBlobPatternTopper, CbBlobPatternDmd })
        {
            if (cb?.SelectedItem is string name && name == "Mandelbrot")
            {
                anyMandelbrot = true;
                break;
            }
        }

        PanelMandelbrotTuning.Visibility = anyMandelbrot ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateProjectMTuningVisibility()
    {
        if (PanelProjectMTuning == null) return;

        bool anyProjectM = false;
        foreach (var cb in new[] { CbBlobPatternPlayfield, CbBlobPatternBackglass, CbBlobPatternTopper, CbBlobPatternDmd })
        {
            if (cb?.SelectedItem is string name && name == "ProjectM")
            {
                anyProjectM = true;
                break;
            }
        }

        PanelProjectMTuning.Visibility = anyProjectM ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PopulateProjectMFolderTree(AppSettings settings)
    {
        TvProjectMFolders.Items.Clear();

        var presetPath = !string.IsNullOrEmpty(settings.ProjectMPresetPath)
            ? settings.ProjectMPresetPath
            : System.IO.Path.Combine(AppContext.BaseDirectory, "Presets", "ProjectM");

        if (!System.IO.Directory.Exists(presetPath)) return;

        var enabledSet = new HashSet<string>(settings.ProjectMEnabledFolders ?? [], StringComparer.OrdinalIgnoreCase);
        bool allEnabled = enabledSet.Count == 0; // empty = all enabled

        foreach (var topDir in System.IO.Directory.GetDirectories(presetPath).OrderBy(d => d))
        {
            var topName = System.IO.Path.GetFileName(topDir);

            // Never show the Deactivated or Deleted folders in the enabled-folders tree
            if (topName.Equals("Deactivated", StringComparison.OrdinalIgnoreCase) ||
                topName.Equals("Deleted", StringComparison.OrdinalIgnoreCase))
                continue;
            var topItem = new System.Windows.Controls.TreeViewItem
            {
                Foreground = FindResource("TextBrush") as System.Windows.Media.Brush,
            };

            var topCheck = new System.Windows.Controls.CheckBox
            {
                Content = topName,
                Foreground = FindResource("TextBrush") as System.Windows.Media.Brush,
                Tag = topName,
                IsChecked = allEnabled || enabledSet.Contains(topName),
            };

            // When a top-level checkbox is toggled, toggle all children
            topCheck.Checked += (s, e) => SetChildCheckboxes(topItem, true);
            topCheck.Unchecked += (s, e) => SetChildCheckboxes(topItem, false);

            topItem.Header = topCheck;

            // Add sub-folders
            foreach (var subDir in System.IO.Directory.GetDirectories(topDir).OrderBy(d => d))
            {
                var subName = System.IO.Path.GetFileName(subDir);
                var relativePath = $"{topName}\\{subName}";
                int presetCount = System.IO.Directory.GetFiles(subDir, "*.milk").Length;

                var subCheck = new System.Windows.Controls.CheckBox
                {
                    Content = $"{subName} ({presetCount})",
                    Foreground = FindResource("TextBrush") as System.Windows.Media.Brush,
                    Tag = relativePath,
                    IsChecked = allEnabled || enabledSet.Contains(topName) || enabledSet.Contains(relativePath),
                    Margin = new Thickness(0, 2, 0, 0),
                };

                topItem.Items.Add(new System.Windows.Controls.TreeViewItem { Header = subCheck });
            }

            TvProjectMFolders.Items.Add(topItem);
        }
    }

    private static void SetChildCheckboxes(System.Windows.Controls.TreeViewItem parent, bool isChecked)
    {
        foreach (var child in parent.Items.OfType<System.Windows.Controls.TreeViewItem>())
        {
            if (child.Header is System.Windows.Controls.CheckBox cb)
                cb.IsChecked = isChecked;
        }
    }

    private List<string> CollectProjectMEnabledFolders()
    {
        var result = new List<string>();
        foreach (var topItem in TvProjectMFolders.Items.OfType<System.Windows.Controls.TreeViewItem>())
        {
            if (topItem.Header is System.Windows.Controls.CheckBox topCb && topCb.IsChecked == true)
            {
                // If all children are checked, just include the top-level folder
                bool allChildrenChecked = topItem.Items.OfType<System.Windows.Controls.TreeViewItem>()
                    .All(c => c.Header is System.Windows.Controls.CheckBox cb && cb.IsChecked == true);

                if (allChildrenChecked || topItem.Items.Count == 0)
                {
                    result.Add((string)topCb.Tag);
                }
                else
                {
                    // Include only checked sub-folders
                    foreach (var child in topItem.Items.OfType<System.Windows.Controls.TreeViewItem>())
                    {
                        if (child.Header is System.Windows.Controls.CheckBox cb && cb.IsChecked == true)
                            result.Add((string)cb.Tag);
                    }
                }
            }
        }
        return result;
    }

    private static readonly int[] PresetDurationSteps = { 5, 10, 15, 20, 25, 30, 45, 60, 120, 300, 600, 900, 1200, 1800, 3600, 7200, 14400, 43200, 86400 };

    private static int PresetDurationFromIndex(int index) =>
        index >= 0 && index < PresetDurationSteps.Length ? PresetDurationSteps[index] : 30;

    private static int PresetDurationToIndex(double seconds)
    {
        int best = 0;
        double bestDist = double.MaxValue;
        for (int i = 0; i < PresetDurationSteps.Length; i++)
        {
            double dist = Math.Abs(PresetDurationSteps[i] - seconds);
            if (dist < bestDist) { bestDist = dist; best = i; }
        }
        return best;
    }

    private static string FormatPresetDuration(int seconds) =>
        seconds >= 3600 ? $"{seconds / 3600}hr" : seconds >= 60 ? $"{seconds / 60}m" : $"{seconds}s";

    private void SliderProjectMPresetDuration_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtProjectMPresetDuration != null)
            TxtProjectMPresetDuration.Text = FormatPresetDuration(PresetDurationFromIndex((int)e.NewValue));
    }

    private void SliderProjectMSoftCut_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtProjectMSoftCut != null)
            TxtProjectMSoftCut.Text = $"{(int)e.NewValue}s";
    }

    private void SliderProjectMBeatSensitivity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtProjectMBeatSensitivity != null)
            TxtProjectMBeatSensitivity.Text = $"{e.NewValue:F1}";
    }

    private void SliderProjectMRenderScale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtProjectMRenderScale != null)
            TxtProjectMRenderScale.Text = $"{(int)e.NewValue}%";
    }

    private void RbPresetMonitor_Checked(object sender, RoutedEventArgs e)
    {
        // No additional logic needed — value is read at save time
    }

    private void BtnPresetBrowser_Click(object sender, RoutedEventArgs e)
    {
        var presetPath = !string.IsNullOrEmpty(_settings.ProjectMPresetPath)
            ? _settings.ProjectMPresetPath
            : System.IO.Path.Combine(AppContext.BaseDirectory, "Presets", "ProjectM");

        var browser = new PresetBrowserWindow(presetPath, _playfieldProxy);
        browser.Owner = this;
        browser.ShowDialog();

        // Refresh the folder tree after changes
        PopulateProjectMFolderTree(_settings);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplySettings();
        }
        catch (Exception ex)
        {
            DebugLog.Log("Settings", $"Apply_Click failed: {ex}");
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        DisableTestMode();
        try
        {
            ApplySettings();
        }
        catch (Exception ex)
        {
            DebugLog.Log("Settings", $"Save_Click ApplySettings failed: {ex}");
        }

        try
        {
            Close();
        }
        catch (Exception ex)
        {
            DebugLog.Log("Settings", $"Save_Click Close failed: {ex}");
        }
    }

    private void ApplySettings()
    {
        var _sw = System.Diagnostics.Stopwatch.StartNew();
        void _LogStep(string step) { DebugLog.Log("ApplySettings", $"{step}: {_sw.ElapsedMilliseconds}ms"); _sw.Restart(); }

        foreach (var entry in _entries)
            _settings.KeyBindings.ApplyEntry(entry);
        _LogStep("KeyBindings");

        if (RbBlank.IsChecked == true)
            _settings.PlayfieldDisplayMode = PlayfieldMode.Blank;
        else if (RbScreensaver.IsChecked == true)
            _settings.PlayfieldDisplayMode = PlayfieldMode.Screensaver;
        else if (RbVideo.IsChecked == true)
            _settings.PlayfieldDisplayMode = PlayfieldMode.Video;
        else
            _settings.PlayfieldDisplayMode = PlayfieldMode.StaticImage;

        _settings.PlayfieldPulseDominantBlobs = CbPulseDominantBlobs.IsChecked == true;
        _settings.OledSleepDefeatSeconds = CbOledSleepDefeat.SelectedIndex * 10;
        _settings.OledSleepDefeatDurationSeconds = CbOledSleepDuration.SelectedIndex + 1;
        _settings.OledSleepDefeatIntensity = (int)SliderOledIntensity.Value;
        SelectedPlayfieldMode = _settings.PlayfieldDisplayMode;
        _settings.PlayfieldStaticImagePath = TbStaticImagePath.Text;
        _settings.PlayfieldVideoPath = TbVideoPath.Text;
        _settings.ShowVideoInfo = CbShowVideoInfo.IsChecked == true;
        _settings.ResizableWindows = CbResizableWindows.IsChecked == true;
        _settings.SetCursorOnLaunch = CbSetCursorOnLaunch.IsChecked == true;
        _settings.MoveCursorToSettings = CbMoveCursorToSettings.IsChecked == true;
        _settings.CheckWindowsOnStartup = CbCheckWindowsOnStartup.IsChecked == true;
        _settings.ShowBackglass = CbShowBackglass.IsChecked == true;
        _settings.ShowPlayfield = CbShowPlayfield.IsChecked == true;
        _settings.ShowTopper = CbShowTopper.IsChecked == true;
        _settings.AutoPlayQueueOnStart = CbAutoPlayQueue.IsChecked == true;
        _settings.StartupDittiPath = TbStartupDittiPath.Text;
        _settings.EnableStartupDitti = CbEnableStartupDitti.IsChecked == true;
        _settings.DofEnabled = CbDofEnabled.IsChecked == true;
        _settings.DofRomName = string.IsNullOrWhiteSpace(TbDofRomName.Text) ? "vpinjukebox" : TbDofRomName.Text.Trim();
        _settings.DofSimulator = CbDofSimulator.IsChecked == true;
        _settings.DofColorBand = CbDofColorBand.IsChecked == true;
        _settings.DofPresetChanged = CbDofPresetChanged.IsChecked == true;
        _settings.DmdScreensaver = CbDmdScreensaver.IsChecked == true;
        _settings.BackglassLogoDimEnabled = CbBackglassLogoDim.IsChecked == true;
        _settings.BackglassLogoDimOpacity = CbBackglassDimOpacity.SelectedIndex * 5;
        var bgTimeoutValues = new[] { 10, 15, 20, 30, 45, 60, 90, 120, 180, 240, 300, 360, 420, 480, 540, 600 };
        _settings.BackglassLogoDimTimeoutSeconds = CbBackglassDimTimeout.SelectedIndex >= 0 && CbBackglassDimTimeout.SelectedIndex < bgTimeoutValues.Length
            ? bgTimeoutValues[CbBackglassDimTimeout.SelectedIndex] : 60;
        _settings.BackglassLogoMorphColor = CbBackglassMorphColor.IsChecked == true;
        _settings.BackglassAudioOnly = CbBackglassAudioOnly.IsChecked == true;
        _settings.DmdScreensaverDimEnabled = CbDmdScreensaverDim.IsChecked == true;
        _settings.DmdScreensaverDimDarkBlobs = CbDmdDimDarkBlobs.IsChecked == true;
        _settings.DmdSwapTarget = RbSwapBackglass.IsChecked == true ? DmdSwapMode.Backglass
            : RbSwapPlayfield.IsChecked == true ? DmdSwapMode.Playfield
            : DmdSwapMode.Off;
        _settings.ApplyDefaultDmdOnSwap = CbApplyDefaultDmdOnSwap.IsChecked == true;
        _settings.DmdScreensaverDimOpacity = CbDmdDimOpacity.SelectedIndex * 5;
        var timeoutValues = new[] { 10, 15, 20, 30, 45, 60, 90, 120, 180, 240, 300, 360, 420, 480, 540, 600 };
        _settings.DmdScreensaverDimTimeoutSeconds = CbDmdDimTimeout.SelectedIndex >= 0 && CbDmdDimTimeout.SelectedIndex < timeoutValues.Length
            ? timeoutValues[CbDmdDimTimeout.SelectedIndex] : 60;
        _settings.DmdRotation = CbDmdRotation.SelectedIndex switch { 1 => 90, 2 => 180, 3 => 270, _ => 0 };
        var newQueuePos = (QueuePosition)CbQueuePosition.SelectedIndex;
        if (newQueuePos != _settings.DmdQueuePosition)
            _settings.DmdQueueSplitterSize = -1; // Reset splitter when position changes
        _settings.DmdQueuePosition = newQueuePos;
        _settings.DmdHeaderSizeModifier = Math.Clamp((int)SliderHeaderSize.Value, -4, 10);
        _settings.DmdSearchBarSizeModifier = Math.Clamp((int)SliderSearchBarSize.Value, -4, 8);
        _settings.DmdSearchResultsNavSizeModifier = Math.Clamp((int)SliderSearchResultsNavSize.Value, -2, 8);
        _settings.QueueFontSizeModifier = Math.Clamp((int)SliderQueueFontSize.Value, -12, 24);
        _settings.DmdQueueButtonSizeModifier = Math.Clamp((int)SliderQueueButtonSize.Value, -12, 24);
        _settings.DmdPlayButtonSizeModifier = Math.Clamp((int)SliderPlayButtonSize.Value, -12, 36);
        _settings.DmdGenreIconSizeModifier = Math.Clamp((int)SliderGenreIconSize.Value, -12, 24);
        _settings.DmdTrackButtonSizeModifier = Math.Clamp((int)SliderTrackButtonSize.Value, -12, 24);
        _settings.DmdMinorButtonLocation = (MinorButtonLocation)Math.Clamp(CbMinorButtonLocation.SelectedIndex, 0, 1);
        _settings.HiddenCategories = _categoryVisibilityItems
            .Where(i => !i.IsVisible)
            .Select(i => i.Name)
            .ToList();
        _LogStep("HiddenCategories");
        // Invalidate playlist cache for categories whose search term changed
        foreach (var item in _categoryVisibilityItems)
        {
            if (item.SearchTerm != item.OriginalSearchTerm && !string.IsNullOrEmpty(item.Id))
            {
                ResultCache.InvalidateCacheFile(item.Id);
                item.OriginalSearchTerm = item.SearchTerm;
            }
        }
        _LogStep("InvalidateCache");
        // Persist category changes (icon, search term, visibility) to categories.json
        GenreCategoryStore.SaveInBackground(_categoryVisibilityItems.Select(i => new GenreCategoryEntry
        {
            Id = i.Id,
            Name = i.Name,
            Icon = i.Icon,
            SearchTerm = i.SearchTerm,
            IsVisible = i.IsVisible,
            IsSeparator = i.IsSeparator,
            IsLineBreak = i.IsLineBreak,
            PlexLibraryKey = i.PlexLibraryKey,
            PlexLibraryType = i.PlexLibraryType,
            PlexHubsEnabled = i.PlexHubsEnabled,
            PlexPlaylistsEnabled = i.PlexPlaylistsEnabled
        }).ToList());
        _LogStep("GenreCategoryStore.Save");
        _settings.ShowStatusText = CbShowStatusText.IsChecked == true;
        var cursorTimeoutValues = new[] { -1, 0, 5, 10, 15, 30, 45, 60, 120, 180, 240, 300, 360, 420, 480, 540, 600 };
        _settings.HideCursorTimeoutSeconds = CbHideCursorTimeout.SelectedIndex >= 0 && CbHideCursorTimeout.SelectedIndex < cursorTimeoutValues.Length
            ? cursorTimeoutValues[CbHideCursorTimeout.SelectedIndex] : 15;
        _settings.CacheEnabled = CbCacheEnabled.IsChecked == true;
        _settings.PrefetchEnabled = CbPrefetchEnabled.IsChecked == true;
        _settings.CacheMode = (CacheMode)Math.Clamp(CbCacheMode.SelectedIndex, 0, 1);
        var cacheSizeValues = new double[] { 1, 2, 5, 10, 25, 50, 100, 250, 500, 0 };
        _settings.CacheMaxSizeGb = CbCacheMaxSize.SelectedIndex >= 0 && CbCacheMaxSize.SelectedIndex < cacheSizeValues.Length
            ? cacheSizeValues[CbCacheMaxSize.SelectedIndex] : 5;
        _settings.CacheMaxClipLengthMinutes = CbCacheMaxClipLength.SelectedIndex;
        _settings.ThumbnailCacheEnabled = CbThumbnailCacheEnabled.IsChecked == true;
        var thumbSizeValues = new double[] { 250, 500, 1024, 2048, 5120 };
        _settings.ThumbnailCacheMaxSizeMb = CbThumbnailCacheMaxSize.SelectedIndex >= 0 && CbThumbnailCacheMaxSize.SelectedIndex < thumbSizeValues.Length
            ? thumbSizeValues[CbThumbnailCacheMaxSize.SelectedIndex] : 500;
        _settings.CategoryCacheEnabled = CbCategoryCacheEnabled.IsChecked == true;
        var ageValues = new[] { 1, 2, 4, 6, 12, 24, 48, 72, 120, 168, 336, 504, 720, 1440, 2160, 2880, 3600, 4320 };
        _settings.CategoryCacheMaxAgeHours = CbCategoryCacheMaxAge.SelectedIndex >= 0 && CbCategoryCacheMaxAge.SelectedIndex < ageValues.Length
            ? ageValues[CbCategoryCacheMaxAge.SelectedIndex] : 168;
        _settings.YtPlaylistCacheEnabled = CbYtPlaylistCacheEnabled.IsChecked == true;
        _settings.YtPlaylistCacheMaxAgeHours = CbYtPlaylistCacheMaxAge.SelectedIndex >= 0 && CbYtPlaylistCacheMaxAge.SelectedIndex < ageValues.Length
            ? ageValues[CbYtPlaylistCacheMaxAge.SelectedIndex] : 168;
        _settings.PlexPlaylistCacheEnabled = CbPlexPlaylistCacheEnabled.IsChecked == true;
        _settings.PlexPlaylistCacheMaxAgeHours = CbPlexPlaylistCacheMaxAge.SelectedIndex >= 0 && CbPlexPlaylistCacheMaxAge.SelectedIndex < ageValues.Length
            ? ageValues[CbPlexPlaylistCacheMaxAge.SelectedIndex] : 168;
        _settings.DebugLogging = CbDebugLogging.IsChecked == true;
        DebugLog.Enabled = _settings.DebugLogging;
        if (CbResultColumns.SelectedItem is int cols)
            _settings.ResultColumns = cols;
        _settings.ResultFontSizeModifier = Math.Clamp((int)SliderResultFontSize.Value, -12, 12);
        _settings.ScreensaverIntensity = SliderIntensity.Value / 100.0;
        _settings.ScreensaverSpeed = SliderSpeed.Value / 10.0;
        _settings.TitleText = TbTitleText.Text;
        _settings.LogoText = TbLogoText.Text;
        _settings.LogoSpin = CbLogoSpin.IsChecked == true;
        _settings.LogoRings = (LogoRingsMode)(int)SliderLogoRings.Value;
        var blobPatternsSorted = Enum.GetValues<BlobPattern>()
            .OrderBy(p => p switch
            {
                BlobPattern.Random => "Random",
                BlobPattern.RoughClockwise => "Eccentric (Clockwise)",
                BlobPattern.PerfectClockwise => "Orbital (Clockwise)",
                BlobPattern.RoughMixed => "Eccentric (Mixed)",
                BlobPattern.PerfectMixed => "Orbital (Mixed)",
                BlobPattern.Rainfall => "Rainfall",
                BlobPattern.LavaLamp => "Lava Lamp",
                BlobPattern.Bounce => "Bounce",
                BlobPattern.LightCycle => "Light Cycle",
                BlobPattern.FractalBox => "Fractal Box",
                BlobPattern.Mandelbrot => "Mandelbrot",
                BlobPattern.RandomPerSong => "Random Per Song",
                _ => p.ToString()
            })
            .ToList();
        _settings.PlayfieldBlobPattern = CbBlobPatternPlayfield.SelectedIndex >= 0 ? blobPatternsSorted[CbBlobPatternPlayfield.SelectedIndex] : BlobPattern.Random;
        _settings.PlayfieldBlobCount = (int)SliderBlobCountPlayfield.Value;
        _settings.PlayfieldBlobSizeOffset = (int)SliderBlobSizePlayfield.Value;
        _settings.PlayfieldRotation = CbPlayfieldRotation.SelectedIndex switch { 1 => 90, 2 => 180, 3 => 270, _ => 0 };
        _settings.BackglassBlobPattern = CbBlobPatternBackglass.SelectedIndex >= 0 ? blobPatternsSorted[CbBlobPatternBackglass.SelectedIndex] : BlobPattern.Random;
        _settings.BackglassBlobCount = (int)SliderBlobCountBackglass.Value;
        _settings.BackglassBlobSizeOffset = (int)SliderBlobSizeBackglass.Value;
        _settings.TopperBlobPattern = CbBlobPatternTopper.SelectedIndex >= 0 ? blobPatternsSorted[CbBlobPatternTopper.SelectedIndex] : BlobPattern.Random;
        _settings.TopperBlobCount = (int)SliderBlobCountTopper.Value;
        _settings.TopperBlobSizeOffset = (int)SliderBlobSizeTopper.Value;
        _settings.DmdBlobPattern = CbBlobPatternDmd.SelectedIndex >= 0 ? blobPatternsSorted[CbBlobPatternDmd.SelectedIndex] : BlobPattern.Random;
        _settings.DmdBlobCount = (int)SliderBlobCountDmd.Value;
        _settings.DmdBlobSizeOffset = (int)SliderBlobSizeDmd.Value;
        _settings.ExcludeMandelbrotFromRandom = CbExcludeMandelbrot.IsChecked == true;
        _settings.ExcludeProjectMFromRandom = CbExcludeProjectM.IsChecked == true;
        _settings.ProjectMPresetDuration = PresetDurationFromIndex((int)SliderProjectMPresetDuration.Value);
        _settings.ProjectMSoftCutDuration = SliderProjectMSoftCut.Value;
        _settings.ProjectMHardCutEnabled = CbProjectMHardCut.IsChecked == true;
        _settings.ProjectMNewVisualOnTrackChange = CbProjectMNewVisualOnTrackChange.IsChecked == true;
        _settings.ProjectMSoftwareRender = CbProjectMCompatibilityRenderer.IsChecked == true;
        _settings.ProjectMBeatSensitivity = (float)SliderProjectMBeatSensitivity.Value;
        _settings.ProjectMRenderScale = SliderProjectMRenderScale.Value / 100.0;
        _settings.ProjectMPresetMonitor = RbPresetMonitorDeactivate.IsChecked == true ? 2
            : RbPresetMonitorSkip.IsChecked == true ? 1 : 0;
        _settings.ProjectMEnabledFolders = CollectProjectMEnabledFolders();
        _settings.MandelbrotUseGpu = CbMandelbrotUseGpu.IsChecked == true ? 1 : 0;
        _settings.MandelbrotAdaptiveIterations = CbMandelbrotAdaptiveIterations.IsChecked == true;
        _settings.MandelbrotMaxIterations = (int)SliderMandelbrotMaxIterations.Value;
        _settings.MandelbrotMaxHz = (int)SliderMandelbrotMaxHz.Value;
        _settings.MandelbrotRenderScale = SliderMandelbrotRenderScale.Value / 100.0;
        _settings.MandelbrotPerturbation = SliderMandelbrotPerturbation.Value / 100.0;
        _settings.MandelbrotDiscovery = CbMandelbrotDiscovery.IsChecked == true;
        _settings.MandelbrotDimming = SliderMandelbrotDimming.Value / 100.0;
        _settings.ReactiveBlobs = CbReactiveBlobs.IsChecked == true;
        _settings.ReactiveProjectM = CbReactiveProjectM.IsChecked == true;
        _settings.ReactivityThreshold = SliderReactivityThreshold.Value / 100.0;
        _settings.ReactiveSpeedMs = (int)SliderReactiveSpeed.Value;
        _settings.ReactiveOverdrive = SliderReactiveOverdrive.Value / 10.0;
        _settings.TopperDistortion = SliderDistortion.Value / 100.0;
        _settings.VideoQuality = (VideoQualityPreference)CbVideoQuality.SelectedIndex;
        _settings.StereoAudio = CbStereoAudio.IsChecked == true;
        _settings.NetworkCachingMs = (int)SliderNetworkCaching.Value;
        _settings.LiveCachingMs = (int)SliderLiveCaching.Value;
        _settings.FileCachingMs = (int)SliderFileCaching.Value;
        _settings.HttpReconnect = CbHttpReconnect.IsChecked == true;
        _settings.PlexServerUrl = TbPlexUrl.Text.Trim();
        _settings.PlexToken = TbPlexToken.Text.Trim();
        _settings.PlexStereoAudio = CbPlexStereo.IsChecked == true;
        _settings.PlexLibraries = _plexLibraries.ToList();
        _LogStep("AllSettings");
        Saved = true;
        _ = _settings.SaveAsync();

        _LogStep("SaveAsync");
        // Fire event before updating originals so change-detection properties
        // still reflect what actually changed during this apply cycle
        try
        {
            SettingsApplied?.Invoke();
            _LogStep("SettingsApplied event");
        }
        catch (Exception ex)
        {
            DebugLog.Log("Settings", $"SettingsApplied handler failed: {ex}");
        }

        // Update originals so Cancel won't revert applied changes
        _originalIntensity = _settings.ScreensaverIntensity;
        _originalSpeed = _settings.ScreensaverSpeed;
        _originalDistortion = _settings.TopperDistortion;
        _originalPlayfieldBlobPattern = _settings.PlayfieldBlobPattern;
        _originalPlayfieldBlobCount = _settings.PlayfieldBlobCount;
        _originalPlayfieldBlobSizeOffset = _settings.PlayfieldBlobSizeOffset;
        _originalPlayfieldRotation = _settings.PlayfieldRotation;
        _originalBackglassBlobPattern = _settings.BackglassBlobPattern;
        _originalBackglassBlobCount = _settings.BackglassBlobCount;
        _originalBackglassBlobSizeOffset = _settings.BackglassBlobSizeOffset;
        _originalTopperBlobPattern = _settings.TopperBlobPattern;
        _originalTopperBlobCount = _settings.TopperBlobCount;
        _originalTopperBlobSizeOffset = _settings.TopperBlobSizeOffset;
        _originalDmdBlobPattern = _settings.DmdBlobPattern;
        _originalDmdBlobCount = _settings.DmdBlobCount;
        _originalDmdBlobSizeOffset = _settings.DmdBlobSizeOffset;
        _originalDmdRotation = _settings.DmdRotation;
        _originalReactiveBlobs = _settings.ReactiveBlobs;
        _originalReactiveProjectM = _settings.ReactiveProjectM;
        _originalReactivityThreshold = _settings.ReactivityThreshold;
        _originalReactiveSpeedMs = _settings.ReactiveSpeedMs;
        _originalReactiveOverdrive = _settings.ReactiveOverdrive;
        _originalTitleText = _settings.TitleText;
        _originalLogoText = _settings.LogoText;
        _originalLogoSpin = _settings.LogoSpin;
        _originalLogoRings = _settings.LogoRings;
        _originalLogoMorphColor = _settings.BackglassLogoMorphColor;
        _originalMandelbrotUseGpu = _settings.MandelbrotUseGpu;
        _originalMandelbrotAdaptiveIterations = _settings.MandelbrotAdaptiveIterations;
        _originalMandelbrotMaxIterations = _settings.MandelbrotMaxIterations;
        _originalMandelbrotMaxHz = _settings.MandelbrotMaxHz;
        _originalMandelbrotRenderScale = _settings.MandelbrotRenderScale;
        _originalMandelbrotPerturbation = _settings.MandelbrotPerturbation;
        _originalMandelbrotDiscovery = _settings.MandelbrotDiscovery;
        _originalMandelbrotDimming = _settings.MandelbrotDimming;
        _originalProjectMPresetDuration = _settings.ProjectMPresetDuration;
        _originalProjectMSoftCut = _settings.ProjectMSoftCutDuration;
        _originalProjectMHardCut = _settings.ProjectMHardCutEnabled;
        _originalProjectMBeatSensitivity = _settings.ProjectMBeatSensitivity;
        _originalProjectMMeshSize = _settings.ProjectMMeshSize;
        _originalProjectMRenderScale = _settings.ProjectMRenderScale;
        _originalProjectMPresetPath = _settings.ProjectMPresetPath;
        _originalProjectMTexturePath = _settings.ProjectMTexturePath;
        _originalProjectMEnabledFolders = new List<string>(_settings.ProjectMEnabledFolders);
        _originalProjectMSoftwareRender = _settings.ProjectMSoftwareRender;
        UpdateActiveRendererLabel();
        _originalDofEnabled = _settings.DofEnabled;
        _originalDofColorBand = _settings.DofColorBand;
        _originalDofPresetChanged = _settings.DofPresetChanged;
        _originalDofRomName = _settings.DofRomName;
        _LogStep("UpdateOriginals");
    }

    private void UpdateActiveRendererLabel()
    {
        // Update immediately with current value, then schedule a delayed
        // refresh to capture the result after an async renderer restart.
        void Update() => TbProjectMActiveRenderer.Text = ProjectMRenderer.ActiveRenderPath != null
            ? $"Active: {ProjectMRenderer.ActiveRenderPath}"
            : "Active: (not yet initialized)";
        Update();
        _ = Dispatcher.InvokeAsync(async () => { await System.Threading.Tasks.Task.Delay(2000); Update(); },
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void CbVideoQuality_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CbVideoQuality.SelectedIndex >= 0)
            UpdateQualityHint((VideoQualityPreference)CbVideoQuality.SelectedIndex);
    }

    private void UpdateQualityHint(VideoQualityPreference pref)
    {
        if (QualityHintText == null) return;
        QualityHintText.Text = pref switch
        {
            VideoQualityPreference.Low => "Up to 480p — fastest, lowest bandwidth",
            VideoQualityPreference.Medium => "Up to 720p — balanced",
            VideoQualityPreference.High => "Up to 1080p — high quality",
            _ => "Up to 4k — highest quality"
        };
    }

    private void SliderNetworkCaching_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (NetworkCachingValueText != null)
            NetworkCachingValueText.Text = ((int)e.NewValue).ToString();
    }

    private void SliderLiveCaching_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LiveCachingValueText != null)
            LiveCachingValueText.Text = ((int)e.NewValue).ToString();
    }

    private void SliderFileCaching_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FileCachingValueText != null)
            FileCachingValueText.Text = ((int)e.NewValue).ToString();
    }

    private async void PlexTest_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var plex = new PlexService();
            plex.Configure(TbPlexUrl.Text.Trim(), TbPlexToken.Text.Trim());
            PlexStatusText.Text = "Testing...";
            var ok = await plex.TestConnectionAsync();
            PlexStatusText.Text = ok ? "✓ Connected" : "✗ Connection failed";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            PlexStatusText.Text = "✗ Connection failed (server unreachable)";
        }
    }

    private async void PlexLoadLibraries_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var plex = new PlexService();
            plex.Configure(TbPlexUrl.Text.Trim(), TbPlexToken.Text.Trim());
            PlexStatusText.Text = "Loading...";

            var libs = await plex.GetLibrariesAsync();
            CbPlexLibrary.ItemsSource = libs;
            if (libs.Count > 0)
            {
                CbPlexLibrary.SelectedItem = libs[0];
                PlexStatusText.Text = $"{libs.Count} libraries found";
            }
            else
            {
                PlexStatusText.Text = "No libraries found";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            PlexStatusText.Text = "✗ Connection failed (server unreachable)";
        }
    }

    private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private async Task TryLoadPlexLibrariesAsync()
    {
        try
        {
            var plex = new PlexService();
            plex.Configure(TbPlexUrl.Text.Trim(), TbPlexToken.Text.Trim());
            var libs = await plex.GetLibrariesAsync();
            CbPlexLibrary.ItemsSource = libs;
            if (libs.Count > 0)
                CbPlexLibrary.SelectedItem = libs[0];
        }
        catch
        {
            // Fail silently
        }
    }

    private void PlexAddLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (CbPlexLibrary.SelectedItem is not PlexLibrary selected) return;
        if (_plexLibraries.Any(l => l.Key == selected.Key)) return;
        _plexLibraries.Add(new PlexLibraryMapping { Key = selected.Key, Title = selected.Title, Type = selected.Type });
        PlexStatusText.Text = $"Added: {selected.Title}";
    }

    private void PlexRemoveLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PlexLibraryMapping mapping })
            _plexLibraries.Remove(mapping);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DisableTestMode();
        Saved = false;
        StopDInputCapture();
        if (_testDofClient != null && _testDofClient != _sharedDofClient)
            _testDofClient.Dispose();
        _testDofClient = null;
        _backglassProxy?.SetScreensaverSettings(_originalIntensity, _originalSpeed);
        _playfieldProxy?.SetScreensaverSettings(_originalIntensity, _originalSpeed);
        _topperWindow?.SetDistortion(_originalDistortion);
        Close();
    }

    private void CategoryVisibility_Changed(object sender, RoutedEventArgs e)
    {
        UpdateCategoryVisibilityText();
    }

    private void CategoryIcon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.DataContext is not CategoryVisibilityItem item)
            return;

        var popup = new System.Windows.Controls.Primitives.Popup
        {
            StaysOpen = false,
            PlacementTarget = btn,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        var surfaceBrush = (System.Windows.Media.Brush)FindResource("SurfaceBrush");
        var textBrush = (System.Windows.Media.Brush)FindResource("TextBrush");
        var accentBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");

        var outerStack = new System.Windows.Controls.StackPanel();

        // Search box for filtering icons
        var searchBox = new System.Windows.Controls.TextBox
        {
            Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush"),
            Foreground = textBrush,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness = new Thickness(1),
            FontSize = 12,
            Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(0, 0, 0, 4),
        };

        var wrapPanel = new System.Windows.Controls.WrapPanel();
        var scrollViewer = new System.Windows.Controls.ScrollViewer
        {
            Content = wrapPanel,
            MaxHeight = 250,
            MaxWidth = 320,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
        };

        void PopulateIcons(string filterText)
        {
            wrapPanel.Children.Clear();
            var shown = new HashSet<string>();

            void AddIcon(string emoji, bool isSuggested)
            {
                if (!shown.Add(emoji)) return;
                var iconBtn = new System.Windows.Controls.Button
                {
                    Content = emoji,
                    FontSize = 20,
                    Width = 36,
                    Height = 36,
                    Margin = new Thickness(2),
                    Padding = new Thickness(0),
                    Background = isSuggested ? accentBrush : surfaceBrush,
                    Foreground = textBrush,
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x44, 0x44, 0x44)),
                    BorderThickness = new Thickness(1),
                    Cursor = System.Windows.Input.Cursors.Hand,
                };
                iconBtn.Click += (_, _) =>
                {
                    item.Icon = emoji;
                    btn.Content = emoji;
                    popup.IsOpen = false;
                };
                wrapPanel.Children.Add(iconBtn);
            }

            // Keyword-matched suggestions first (from search box or category name)
            var searchText = string.IsNullOrWhiteSpace(filterText) ? item.Name : filterText;
            var suggestions = DmdWindow.SuggestIcons(searchText);
            foreach (var s in suggestions)
                AddIcon(s, true);

            // Then all emoji from the keyword dictionary
            var allEmoji = DmdWindow.GetEmojiKeywords();
            foreach (var emoji in allEmoji.Keys)
                AddIcon(emoji, false);
        }

        // Debounce search updates
        var debounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            PopulateIcons(searchBox.Text);
        };
        searchBox.TextChanged += (_, _) =>
        {
            debounce.Stop();
            debounce.Start();
        };
        popup.Closed += (_, _) => debounce.Stop();

        PopulateIcons("");

        outerStack.Children.Add(searchBox);
        outerStack.Children.Add(scrollViewer);

        var border = new System.Windows.Controls.Border
        {
            Background = (System.Windows.Media.Brush)FindResource("Surface2Brush"),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6),
            Child = outerStack,
        };

        popup.Child = border;
        popup.IsOpen = true;
        searchBox.Focus();
    }

    private void CategoryAdd_Click(object sender, RoutedEventArgs e)
    {
        var newItem = new CategoryVisibilityItem
        {
            Name = "New Category",
            Icon = "📋",
            SearchTerm = "",
            IsVisible = true,
            IsSpecial = false,
        };
        _categoryVisibilityItems.Add(newItem);
        CategoryListView.ItemsSource = null;
        CategoryListView.ItemsSource = _categoryVisibilityItems;
        CategoryListView.ScrollIntoView(newItem);
        UpdateCategoryVisibilityText();
    }

    private void CategoryAddSeparator_Click(object sender, RoutedEventArgs e)
    {
        var newItem = new CategoryVisibilityItem
        {
            Name = "",
            Icon = "",
            SearchTerm = "",
            IsVisible = true,
            IsSeparator = true,
        };
        _categoryVisibilityItems.Add(newItem);
        CategoryListView.ItemsSource = null;
        CategoryListView.ItemsSource = _categoryVisibilityItems;
        CategoryListView.ScrollIntoView(newItem);
        UpdateCategoryVisibilityText();
    }

    private void CategoryAddLineBreak_Click(object sender, RoutedEventArgs e)
    {
        var newItem = new CategoryVisibilityItem
        {
            Name = "",
            Icon = "",
            SearchTerm = "",
            IsVisible = true,
            IsLineBreak = true,
        };
        _categoryVisibilityItems.Add(newItem);
        CategoryListView.ItemsSource = null;
        CategoryListView.ItemsSource = _categoryVisibilityItems;
        CategoryListView.ScrollIntoView(newItem);
        UpdateCategoryVisibilityText();
    }

    private void CategoryRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.DataContext is not CategoryVisibilityItem item)
            return;
        if (item.IsSpecial || item.IsPlex) return;

        _categoryVisibilityItems.Remove(item);
        CategoryListView.ItemsSource = null;
        CategoryListView.ItemsSource = _categoryVisibilityItems;
        UpdateCategoryVisibilityText();
    }

    private void CategoryMoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.DataContext is not CategoryVisibilityItem item)
            return;
        var index = _categoryVisibilityItems.IndexOf(item);
        if (index <= 0) return;
        _categoryVisibilityItems.RemoveAt(index);
        _categoryVisibilityItems.Insert(index - 1, item);
        CategoryListView.ItemsSource = null;
        CategoryListView.ItemsSource = _categoryVisibilityItems;
    }

    private void CategoryMoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.DataContext is not CategoryVisibilityItem item)
            return;
        var index = _categoryVisibilityItems.IndexOf(item);
        if (index < 0 || index >= _categoryVisibilityItems.Count - 1) return;
        _categoryVisibilityItems.RemoveAt(index);
        _categoryVisibilityItems.Insert(index + 1, item);
        CategoryListView.ItemsSource = null;
        CategoryListView.ItemsSource = _categoryVisibilityItems;
    }

    private void SaveDefaultSettings_Click(object sender, RoutedEventArgs e)
    {
        _settings.SaveDefaults();
        DefaultSettingsSavedText.Visibility = System.Windows.Visibility.Visible;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (s, args) => { timer.Stop(); DefaultSettingsSavedText.Visibility = System.Windows.Visibility.Collapsed; };
        timer.Start();
    }

    private void UpdateCategoryVisibilityText()
    {
        int visible = _categoryVisibilityItems.Count(i => i.IsVisible && !i.IsSeparator && !i.IsLineBreak);
        int total = _categoryVisibilityItems.Count(i => !i.IsSeparator && !i.IsLineBreak);
        CategoryVisibilitySummary.Text = $"{visible}/{total} categories visible. Icon, name, and search term can be modified.";
    }

    private async void DofTestSend_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TbDofTestValue.Text.Trim(), out var value))
            value = 255;
        await SendDofTestTrigger(value);
    }

    private async Task SendDofTestTrigger(int value)
    {
        try
        {
            // Reuse the shared DOF client from the main window if available
                if (_sharedDofClient?.IsConnected == true)
                {
                    _testDofClient = _sharedDofClient;
                }

                if (_testDofClient == null || !_testDofClient.IsConnected)
                {
                    // Only create a new client if no shared client is available
                    if (_testDofClient != null && _testDofClient != _sharedDofClient)
                        _testDofClient.Dispose();
                    _testDofClient = new DofClient();
                    var romName = string.IsNullOrWhiteSpace(TbDofRomName.Text) ? "vpinjukebox" : TbDofRomName.Text.Trim();
                    DofTestStatusText.Text = "Starting bridge...";
                    if (!await _testDofClient.StartAsync(romName))
                    {
                        DofTestStatusText.Text = "✗ Failed to start bridge";
                        DofStatusText.Text = "Bridge not found or failed to connect.";
                        return;
                    }
                    DofStatusText.Text = "✓ Bridge connected";
                }

            var elementText = TbDofTestElement.Text.Trim();
            var element = elementText.Length > 0 ? elementText[0] : 'E';

            if (!int.TryParse(TbDofTestNumber.Text.Trim(), out var number))
                number = 101;

            _testDofClient.Trigger(element, number, value);
            DofTestStatusText.Text = $"Sent {element}{number} = {value}";
        }
        catch (Exception ex)
        {
            DofTestStatusText.Text = $"✗ {ex.Message}";
        }
    }
}
