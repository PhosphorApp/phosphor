using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using Button = System.Windows.Controls.Button;
using Phosphor.Video;

namespace Phosphor;

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
    public bool IsPlaylist { get; set; }
    public string PlaylistId { get; set; } = "";
    public int SortOrder { get; set; }
    public string? PlexLibraryKey { get; set; }
    public string? PlexLibraryType { get; set; }
    public string? PlexInstanceId { get; set; }
    public bool PlexHubsEnabled { get; set; }
    public bool PlexPlaylistsEnabled { get; set; }
    // Generic plug-in source tile identity (round-tripped so sort/visibility persist for these too).
    public string? SourceInstanceId { get; set; }
    public string? SourceCategoryId { get; set; }
    public string? SourceTypeId { get; set; }
    public bool IsGenericSource { get; set; }
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
    private TopperProxy? _topperProxy;
    private double _originalPlayfieldIntensity;
    private double _originalPlayfieldSpeed;
    private double _originalBackglassIntensity;
    private double _originalBackglassSpeed;
    private double _originalTopperIntensity;
    private double _originalTopperSpeed;
    private double _originalDmdIntensity;
    private double _originalDmdSpeed;
    private double _originalDistortion;
    private double _originalScreenScaling;
    private BlobPattern _originalPlayfieldBlobPattern;
    private int _originalPlayfieldBlobCount;
    private int _originalPlayfieldBlobSizeOffset;
    private int _originalPlayfieldRotation;
    private bool _originalPlayfieldApplyOrientationToVideos;
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
    private bool _originalReactiveBlobsPlayfield;
    private bool _originalReactiveBlobsBackglass;
    private bool _originalReactiveBlobsTopper;
    private bool _originalReactiveBlobsDmd;
    private bool _originalReactiveProjectM;
    private double _originalReactivityThreshold;
    private int _originalReactiveSpeedMs;
    private double _originalReactiveOverdrive;
    private string _originalTitleText;
    private string _originalLogoText;
    private bool _originalLogoBehindVisuals;
    private bool _originalLogoSpin;
    private bool _originalLogoShadow;
    private LogoRingsMode _originalLogoRings;
    private int _originalLogoRingsBrightness;
    private int _originalLogoBrightness = 100;
    private LogoColorMode _originalLogoColorMode;
    private bool _originalTopperLogoSpin;
    private bool _originalTopperLogoShadow;
    private LogoRingsMode _originalTopperLogoRings;
    private int _originalTopperLogoRingsBrightness;
    private int _originalTopperLogoBrightness = 100;
    private LogoColorMode _originalTopperLogoColorMode;
    private int _originalMandelbrotUseGpu;
    private bool _originalMandelbrotAdaptiveIterations;
    private int _originalMandelbrotMaxIterations;
    private int _originalMandelbrotTickIntervalMs;
    private bool _originalMandelbrotUseScreenRate;
    private double _originalMandelbrotRenderScale;
    private double _originalMandelbrotPerturbation;
    private bool _originalMandelbrotDiscovery;
    private double _originalMandelbrotDimming;
    private bool _originalMandelbrotHistogramColoring;
    private int _originalMandelbrotRotation;
    private int _originalMandelbrotColorScheme;
    private int _originalGameOfLifeCellSize;
    private int _originalGameOfLifeTickIntervalMs;
    private bool _originalGameOfLifeUseScreenRate;
    private int _originalGameOfLifeFadeGenerations;
    private int _originalGameOfLifeHeatBoost;
    private int _originalGameOfLifeDensity;
    private bool _originalGameOfLifeCameraRoam;
    private double _originalGameOfLifeCameraMaxZoom;
    private int _originalGameOfLifeCameraOverscan;
    private double _originalGameOfLifeCameraSpeed;
    private bool _originalGameOfLifeRestartOnTrackChange;
    private int _originalGameOfLifeColorMode;
    private int _originalGameOfLifeRulesEngine;
    private double _originalGameOfLifeEraBandedHueSpeed;
    private string _originalGameOfLifeCustomRule = "B3/S23";
    private int _originalGameOfLifeSeedColorMask = 0x7F;
    private int _originalGameOfLifeHueSpread = 60;
    private int _originalGameOfLifeSeedSpread = 0;
    private bool _originalGameOfLifeBloom;
    private int _originalGameOfLifeBloomRadius = 3;
    private int _originalGameOfLifeBloomIntensity = 6;
    private int _originalGameOfLifeBirthGenerations;
    private double _originalGravityBlobMultiplier = 1.0;
    private bool _originalGravityCameraRoam = false;
    private bool _originalGravityShowDiagnostics = false;
    private int _originalGravityDensity = 1;
    private int _originalClockMode;
    private double _originalClockBrightness = 0.5;
    private int _originalClockDigitalSize = 10;
    private bool _originalClockUse24Hour = true;
    private int _originalClockAnalogSize = 2;
    private int _originalClockAnalogStyle;
    private bool _suppressBsCheckboxSync;

    /// <summary>Named B/S rule presets for the dropdown.</summary>
    private static readonly (string Name, string Rule)[] BsPresets =
    [
        ("Conway (B3/S23)", "B3/S23"),
        ("HighLife (B36/S23)", "B36/S23"),
        ("Day & Night (B3678/S34678)", "B3678/S34678"),
        ("Seeds (B2/S)", "B2/S"),
        ("Replicator (B1357/S1357)", "B1357/S1357"),
        ("Diamoeba (B35678/S5678)", "B35678/S5678"),
        ("Life Without Death (B3/S012345678)", "B3/S012345678"),
        ("Coral (B3/S45678)", "B3/S45678"),
        ("Custom", ""),
    ];
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
    private readonly List<CategoryVisibilityItem> _categoryVisibilityItems = new();
    private readonly ObservableCollection<PinupPlaylist> _pinupPlaylists = new();
    private readonly ObservableCollection<PinupPlaylist> _pinupActive = new();
    private readonly ObservableCollection<PinupPlaylist> _pinupInactive = new();
    private PinupSettings _pinupSettings = new();
    private PlaylistManager? _playlistManager;
    private bool _originalDofEnabled;
    private bool _originalDofColorBand;
    private bool _originalDofPresetChanged;
    private string _originalDofRomName;
    private DofClient? _testDofClient;
    private DofClient? _sharedDofClient;
    private bool _testModeActive;
    private DirectInputPoller? _testDInputPoller;

    public PlayfieldMode SelectedPlayfieldMode { get; private set; }
    public PlayfieldMode SelectedBackglassMode { get; private set; }
    public PlayfieldMode SelectedTopperMode { get; private set; }
    public bool Saved { get; private set; }
    public bool WindowsReset { get; private set; }

    public bool SpeedChanged =>
        Math.Abs(_settings.PlayfieldSpeed - _originalPlayfieldSpeed) > 0.001 ||
        Math.Abs(_settings.BackglassSpeed - _originalBackglassSpeed) > 0.001 ||
        Math.Abs(_settings.TopperSpeed - _originalTopperSpeed) > 0.001 ||
        Math.Abs(_settings.DmdSpeed - _originalDmdSpeed) > 0.001 ||
        Math.Abs(_settings.PlayfieldIntensity - _originalPlayfieldIntensity) > 0.001 ||
        Math.Abs(_settings.BackglassIntensity - _originalBackglassIntensity) > 0.001 ||
        Math.Abs(_settings.TopperIntensity - _originalTopperIntensity) > 0.001 ||
        Math.Abs(_settings.DmdIntensity - _originalDmdIntensity) > 0.001;

    public bool PlayfieldBlobsChanged =>
        Math.Abs(_settings.PlayfieldIntensity - _originalPlayfieldIntensity) > 0.001 ||
        Math.Abs(_settings.PlayfieldSpeed - _originalPlayfieldSpeed) > 0.001 ||
        _settings.PlayfieldBlobPattern != _originalPlayfieldBlobPattern ||
        _settings.PlayfieldBlobCount != _originalPlayfieldBlobCount ||
        _settings.PlayfieldBlobSizeOffset != _originalPlayfieldBlobSizeOffset;

    public bool PlayfieldRotationChanged =>
        _settings.PlayfieldRotation != _originalPlayfieldRotation;

    public bool PlayfieldApplyOrientationToVideosChanged =>
        _settings.PlayfieldApplyOrientationToVideos != _originalPlayfieldApplyOrientationToVideos;

    public bool BackglassBlobsChanged =>
        Math.Abs(_settings.BackglassIntensity - _originalBackglassIntensity) > 0.001 ||
        Math.Abs(_settings.BackglassSpeed - _originalBackglassSpeed) > 0.001 ||
        _settings.BackglassBlobPattern != _originalBackglassBlobPattern ||
        _settings.BackglassBlobCount != _originalBackglassBlobCount ||
        _settings.BackglassBlobSizeOffset != _originalBackglassBlobSizeOffset;

    public bool TopperBlobsChanged =>
        Math.Abs(_settings.TopperIntensity - _originalTopperIntensity) > 0.001 ||
        Math.Abs(_settings.TopperSpeed - _originalTopperSpeed) > 0.001 ||
        _settings.TopperBlobPattern != _originalTopperBlobPattern ||
        _settings.TopperBlobCount != _originalTopperBlobCount ||
        _settings.TopperBlobSizeOffset != _originalTopperBlobSizeOffset;

    public bool DmdBlobsChanged =>
        Math.Abs(_settings.DmdIntensity - _originalDmdIntensity) > 0.001 ||
        Math.Abs(_settings.DmdSpeed - _originalDmdSpeed) > 0.001 ||
        _settings.DmdBlobPattern != _originalDmdBlobPattern ||
        _settings.DmdBlobCount != _originalDmdBlobCount ||
        _settings.DmdBlobSizeOffset != _originalDmdBlobSizeOffset;

    public bool DmdRotationChanged =>
        _settings.DmdRotation != _originalDmdRotation;

    public bool ReactiveBlobsChanged =>
        _settings.ReactiveBlobsPlayfield != _originalReactiveBlobsPlayfield ||
        _settings.ReactiveBlobsBackglass != _originalReactiveBlobsBackglass ||
        _settings.ReactiveBlobsTopper != _originalReactiveBlobsTopper ||
        _settings.ReactiveBlobsDmd != _originalReactiveBlobsDmd ||
        _settings.ReactiveProjectM != _originalReactiveProjectM ||
        Math.Abs(_settings.ReactivityThreshold - _originalReactivityThreshold) > 0.001 ||
        _settings.ReactiveSpeedMs != _originalReactiveSpeedMs ||
        Math.Abs(_settings.ReactiveOverdrive - _originalReactiveOverdrive) > 0.001;

    public bool LogoChanged =>
        _settings.LogoText != _originalLogoText ||
        _settings.LogoBehindVisuals != _originalLogoBehindVisuals ||
        _settings.LogoSpin != _originalLogoSpin ||
        _settings.LogoShadow != _originalLogoShadow ||
        _settings.LogoRings != _originalLogoRings ||
        _settings.LogoRingsBrightness != _originalLogoRingsBrightness ||
        _settings.LogoBrightness != _originalLogoBrightness ||
        _settings.LogoColorMode != _originalLogoColorMode ||
        _settings.TopperLogoSpin != _originalTopperLogoSpin ||
        _settings.TopperLogoShadow != _originalTopperLogoShadow ||
        _settings.TopperLogoRings != _originalTopperLogoRings ||
        _settings.TopperLogoRingsBrightness != _originalTopperLogoRingsBrightness ||
        _settings.TopperLogoBrightness != _originalTopperLogoBrightness ||
        _settings.TopperLogoColorMode != _originalTopperLogoColorMode;

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
        _settings.MandelbrotTickIntervalMs != _originalMandelbrotTickIntervalMs ||
        _settings.MandelbrotUseScreenRate != _originalMandelbrotUseScreenRate ||
        Math.Abs(_settings.MandelbrotRenderScale - _originalMandelbrotRenderScale) > 0.001 ||
        Math.Abs(_settings.MandelbrotPerturbation - _originalMandelbrotPerturbation) > 0.001 ||
        _settings.MandelbrotDiscovery != _originalMandelbrotDiscovery ||
        Math.Abs(_settings.MandelbrotDimming - _originalMandelbrotDimming) > 0.001 ||
        _settings.MandelbrotHistogramColoring != _originalMandelbrotHistogramColoring ||
        _settings.MandelbrotRotation != _originalMandelbrotRotation ||
        _settings.MandelbrotColorScheme != _originalMandelbrotColorScheme;

    public bool GameOfLifeSettingsChanged =>
        _settings.GameOfLifeCellSize != _originalGameOfLifeCellSize ||
        _settings.GameOfLifeTickIntervalMs != _originalGameOfLifeTickIntervalMs ||
        _settings.GameOfLifeUseScreenRate != _originalGameOfLifeUseScreenRate ||
        _settings.GameOfLifeFadeGenerations != _originalGameOfLifeFadeGenerations ||
        _settings.GameOfLifeHeatBoost != _originalGameOfLifeHeatBoost ||
        _settings.GameOfLifeDensity != _originalGameOfLifeDensity ||
        _settings.GameOfLifeCameraRoam != _originalGameOfLifeCameraRoam ||
        _settings.GameOfLifeCameraOverscan != _originalGameOfLifeCameraOverscan ||
        _settings.GameOfLifeCameraSpeed != _originalGameOfLifeCameraSpeed ||
        _settings.GameOfLifeRestartOnTrackChange != _originalGameOfLifeRestartOnTrackChange ||
        _settings.GameOfLifeColorMode != _originalGameOfLifeColorMode ||
        _settings.GameOfLifeRulesEngine != _originalGameOfLifeRulesEngine ||
        _settings.GameOfLifeEraBandedHueSpeed != _originalGameOfLifeEraBandedHueSpeed ||
        _settings.GameOfLifeCustomRule != _originalGameOfLifeCustomRule ||
        _settings.GameOfLifeSeedColorMask != _originalGameOfLifeSeedColorMask ||
        _settings.GameOfLifeHueSpread != _originalGameOfLifeHueSpread ||
        _settings.GameOfLifeSeedSpread != _originalGameOfLifeSeedSpread ||
        _settings.GameOfLifeBloom != _originalGameOfLifeBloom ||
        _settings.GameOfLifeBloomRadius != _originalGameOfLifeBloomRadius ||
        _settings.GameOfLifeBloomIntensity != _originalGameOfLifeBloomIntensity ||
        _settings.GameOfLifeBirthGenerations != _originalGameOfLifeBirthGenerations;

    /// <summary>
    /// True when gravity settings that require a simulation restart have changed
    /// (blob multiplier or camera roam, which are baked into the constructor).
    /// </summary>
    public bool GravitySettingsChanged =>
        Math.Abs(_settings.GravityBlobMultiplier - _originalGravityBlobMultiplier) > 0.01 ||
        _settings.GravityCameraRoam != _originalGravityCameraRoam ||
        _settings.GravityShowDiagnostics != _originalGravityShowDiagnostics ||
        _settings.GravityDensity != _originalGravityDensity;

    /// <summary>
    /// True when clock tuning settings have changed and require a pattern restart.
    /// </summary>
    public bool ClockSettingsChanged =>
        _settings.ClockMode != _originalClockMode ||
        Math.Abs(_settings.ClockBrightness - _originalClockBrightness) > 0.01 ||
        _settings.ClockDigitalSize != _originalClockDigitalSize ||
        _settings.ClockUse24Hour != _originalClockUse24Hour ||
        _settings.ClockAnalogSize != _originalClockAnalogSize ||
        _settings.ClockAnalogStyle != _originalClockAnalogStyle;

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
            case PlayfieldMode.VideoFolders: RbVideoFolders.IsChecked = true; break;
            case PlayfieldMode.PinupPlaylist: RbPinupPlaylist.IsChecked = true; break;
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

        // Playfield video audio (applies to all video modes)
        CbVideoAudioEnabled.IsChecked = settings.PlayfieldVideoAudioEnabled;
        SliderVideoAudioVolume.Value = Math.Clamp(settings.PlayfieldVideoAudioVolume, 0, 100);
        UpdateVideoAudioControls();

        // Pinup Popper integration (persisted separately in pinup_integration.json)
        _pinupSettings = PinupSettings.Load();
        TbPopperDbPath.Text = _pinupSettings.PopperDbPath;
        _pinupPlaylists.Clear();
        foreach (var pl in _pinupSettings.Playlists)
            _pinupPlaylists.Add(pl);
        PinupActiveListView.ItemsSource = _pinupActive;
        PinupInactiveListView.ItemsSource = _pinupInactive;
        RefreshPinupColumns();
        UpdatePinupPlaylistStatus();

        // Pinup clip duration (5–300s, step 5) — shared across all screens.
        SliderPinupClipDuration.Value =
            Math.Clamp(settings.PinupClipDurationSeconds, 5, 300);
        UpdatePinupClipDurationLabel();

        // Pinup window→media-folder mapping.
        _pinupFolderMapRows.Clear();
        foreach (var window in PinupFolderMapping.WindowNames)
        {
            _pinupFolderMapRows.Add(new PinupFolderMapRow
            {
                WindowName = window,
                Folder = PinupFolderMapping.GetFolder(settings.PinupFolderMap, window),
            });
        }
        PinupFolderMapList.ItemsSource = _pinupFolderMapRows;

        // Populate playfield video folders list
        _playfieldVideoFolders.Clear();
        foreach (var f in settings.PlayfieldVideoFolders)
            if (!string.IsNullOrWhiteSpace(f))
                _playfieldVideoFolders.Add(f);
        LbPlayfieldVideoFolders.ItemsSource = _playfieldVideoFolders;

        // Video folder play mode
        CbVideoFolderPlayMode.Items.Clear();
        CbVideoFolderPlayMode.Items.Add("Random");
        CbVideoFolderPlayMode.Items.Add("Most Recent First");
        CbVideoFolderPlayMode.SelectedIndex =
            settings.PlayfieldVideoFolderPlayMode == VideoFolderPlayMode.MostRecentFirst ? 1 : 0;

        // Min duration (5–300s, step 5)
        SliderVideoFolderMinDuration.Value =
            Math.Clamp(settings.PlayfieldVideoFolderMinDurationSeconds, 5, 300);
        // Max duration (10–600s, step 10; 0 = No Maximum, represented by the 610 tick)
        SliderVideoFolderMaxDuration.Value = settings.PlayfieldVideoFolderMaxDurationSeconds <= 0
            ? 610
            : Math.Clamp(settings.PlayfieldVideoFolderMaxDurationSeconds, 10, 600);
        UpdateVideoFolderDurationLabels();

        CbShowVideoInfo.IsChecked = settings.ShowVideoInfo;
        CbResizableWindows.IsChecked = settings.ResizableWindows;
        CbSetCursorOnLaunch.IsChecked = settings.SetCursorOnLaunch;
        CbMoveCursorToSettings.IsChecked = settings.MoveCursorToSettings;
        CbCheckWindowsOnStartup.IsChecked = settings.CheckWindowsOnStartup;
        CbShowBackglass.IsChecked = settings.ShowBackglass;
        CbShowPlayfield.IsChecked = settings.ShowPlayfield;
        CbShowTopper.IsChecked = settings.ShowTopper;
        CbAutoPlayQueue.IsChecked = settings.AutoPlayQueueOnStart;
        // Populate startup ditti list (migrate legacy single path if present)
        _startupDittiPaths.Clear();
        foreach (var p in settings.StartupDittiPaths)
            if (!string.IsNullOrWhiteSpace(p))
                _startupDittiPaths.Add(p);
        if (_startupDittiPaths.Count == 0 && !string.IsNullOrWhiteSpace(settings.StartupDittiPath))
            _startupDittiPaths.Add(settings.StartupDittiPath);
        LbStartupDittiPaths.ItemsSource = _startupDittiPaths;
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
        SliderScreenScaling.Value = settings.TopperScreenScaling * 100;
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

        switch (settings.LogoColorMode)
        {
            case LogoColorMode.SlowMorph: RbLogoColorMorph.IsChecked = true; break;
            case LogoColorMode.Reactive: RbLogoColorReactive.IsChecked = true; break;
            default: RbLogoColorOff.IsChecked = true; break;
        }
        CbBackglassAudioOnly.IsChecked = settings.BackglassAudioOnly;

        // ── Backglass ambient content (independent of the playfield) ──
        switch (settings.BackglassDisplayMode)
        {
            case PlayfieldMode.Blank: RbBgBlank.IsChecked = true; break;
            case PlayfieldMode.Screensaver: RbBgScreensaver.IsChecked = true; break;
            case PlayfieldMode.StaticImage: RbBgStatic.IsChecked = true; break;
            case PlayfieldMode.Video: RbBgVideo.IsChecked = true; break;
            case PlayfieldMode.VideoFolders: RbBgVideoFolders.IsChecked = true; break;
            case PlayfieldMode.PinupPlaylist: RbBgPinupPlaylist.IsChecked = true; break;
        }
        TbBgStaticImagePath.Text = settings.BackglassStaticImagePath;
        TbBgVideoPath.Text = settings.BackglassVideoPath;
        CbBgVideoAudioEnabled.IsChecked = settings.BackglassVideoAudioEnabled;
        SliderBgVideoAudioVolume.Value = Math.Clamp(settings.BackglassVideoAudioVolume, 0, 100);
        UpdateBackglassVideoAudioControls();

        // Populate backglass video folders list
        _backglassVideoFolders.Clear();
        foreach (var f in settings.BackglassVideoFolders)
            if (!string.IsNullOrWhiteSpace(f))
                _backglassVideoFolders.Add(f);
        LbBackglassVideoFolders.ItemsSource = _backglassVideoFolders;

        CbBgVideoFolderPlayMode.Items.Clear();
        CbBgVideoFolderPlayMode.Items.Add("Random");
        CbBgVideoFolderPlayMode.Items.Add("Most Recent First");
        CbBgVideoFolderPlayMode.SelectedIndex =
            settings.BackglassVideoFolderPlayMode == VideoFolderPlayMode.MostRecentFirst ? 1 : 0;

        SliderBgVideoFolderMinDuration.Value =
            Math.Clamp(settings.BackglassVideoFolderMinDurationSeconds, 5, 300);
        SliderBgVideoFolderMaxDuration.Value = settings.BackglassVideoFolderMaxDurationSeconds <= 0
            ? VideoFolderMaxNoLimitTick
            : Math.Clamp(settings.BackglassVideoFolderMaxDurationSeconds, 10, 600);
        UpdateBackglassVideoFolderDurationLabels();

        // ── Topper ambient content (independent of the playfield/backglass) ──
        switch (settings.TopperDisplayMode)
        {
            case PlayfieldMode.Blank: RbTpBlank.IsChecked = true; break;
            case PlayfieldMode.Screensaver: RbTpScreensaver.IsChecked = true; break;
            case PlayfieldMode.StaticImage: RbTpStatic.IsChecked = true; break;
            case PlayfieldMode.Video: RbTpVideo.IsChecked = true; break;
            case PlayfieldMode.VideoFolders: RbTpVideoFolders.IsChecked = true; break;
            case PlayfieldMode.PinupPlaylist: RbTpPinupPlaylist.IsChecked = true; break;
        }
        TbTpStaticImagePath.Text = settings.TopperStaticImagePath;
        TbTpVideoPath.Text = settings.TopperVideoPath;
        CbTpVideoAudioEnabled.IsChecked = settings.TopperVideoAudioEnabled;
        SliderTpVideoAudioVolume.Value = Math.Clamp(settings.TopperVideoAudioVolume, 0, 100);
        UpdateTopperVideoAudioControls();

        _topperVideoFolders.Clear();
        foreach (var f in settings.TopperVideoFolders)
            if (!string.IsNullOrWhiteSpace(f))
                _topperVideoFolders.Add(f);
        LbTopperVideoFolders.ItemsSource = _topperVideoFolders;

        CbTpVideoFolderPlayMode.Items.Clear();
        CbTpVideoFolderPlayMode.Items.Add("Random");
        CbTpVideoFolderPlayMode.Items.Add("Most Recent First");
        CbTpVideoFolderPlayMode.SelectedIndex =
            settings.TopperVideoFolderPlayMode == VideoFolderPlayMode.MostRecentFirst ? 1 : 0;

        SliderTpVideoFolderMinDuration.Value =
            Math.Clamp(settings.TopperVideoFolderMinDurationSeconds, 5, 300);
        SliderTpVideoFolderMaxDuration.Value = settings.TopperVideoFolderMaxDurationSeconds <= 0
            ? VideoFolderMaxNoLimitTick
            : Math.Clamp(settings.TopperVideoFolderMaxDurationSeconds, 10, 600);
        UpdateTopperVideoFolderDurationLabels();

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
        SliderLogoBehind.Value = settings.LogoBehindVisuals ? 0 : 1;
        CbLogoSpin.IsChecked = settings.LogoSpin;
        CbLogoShadow.IsChecked = settings.LogoShadow;

        SliderLogoRings.Value = (int)settings.LogoRings;
        SliderRingBrightness.Value = settings.LogoRingsBrightness;
        SliderLogoBrightness.Value = settings.LogoBrightness;

        // Topper logo settings
        CbTopperLogoSpin.IsChecked = settings.TopperLogoSpin;
        CbTopperLogoShadow.IsChecked = settings.TopperLogoShadow;
        SliderTopperLogoRings.Value = (int)settings.TopperLogoRings;
        SliderTopperRingBrightness.Value = settings.TopperLogoRingsBrightness;
        SliderTopperLogoBrightness.Value = settings.TopperLogoBrightness;

        switch (settings.TopperLogoColorMode)
        {
            case LogoColorMode.SlowMorph: RbTopperLogoColorMorph.IsChecked = true; break;
            case LogoColorMode.Reactive: RbTopperLogoColorReactive.IsChecked = true; break;
            default: RbTopperLogoColorOff.IsChecked = true; break;
        }

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
                BlobPattern.FerrofluidCluster => "Ferrofluid",
                BlobPattern.GameOfLife => "Game of Life",
                BlobPattern.Clock => "Clock",
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
        CbApplyOrientationToVideos.IsChecked = settings.PlayfieldApplyOrientationToVideos;


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
        CbMandelbrotUseScreenRate.IsChecked = settings.MandelbrotUseScreenRate;
        SliderMandelbrotTickInterval.Value = settings.MandelbrotTickIntervalMs;
        TxtMandelbrotTickInterval.Text = settings.MandelbrotTickIntervalMs == 0 ? "Unlimited" : $"{settings.MandelbrotTickIntervalMs} ms";
        UpdateMandelbrotTickIntervalVisibility();
        SliderMandelbrotRenderScale.Value = settings.MandelbrotRenderScale * 100;
        TxtMandelbrotRenderScale.Text = $"{(int)(settings.MandelbrotRenderScale * 100)}%";
        SliderMandelbrotPerturbation.Value = settings.MandelbrotPerturbation * 100;
        TxtMandelbrotPerturbation.Text = settings.MandelbrotPerturbation == 0 ? "Off" : $"{(int)(settings.MandelbrotPerturbation * 100)}%";
        CbMandelbrotDiscovery.IsChecked = settings.MandelbrotDiscovery;
        SliderMandelbrotDimming.Value = settings.MandelbrotDimming * 100;
        TxtMandelbrotDimming.Text = settings.MandelbrotDimming == 0 ? "Off" : $"{(int)(settings.MandelbrotDimming * 100)}%";
        CbMandelbrotHistogramColoring.IsChecked = settings.MandelbrotHistogramColoring;
        if (CbMandelbrotRotation.Items.Count == 0)
        {
            CbMandelbrotRotation.Items.Add("Off");
            CbMandelbrotRotation.Items.Add("Random per target");
            CbMandelbrotRotation.Items.Add("Slow spin");
        }
        CbMandelbrotRotation.SelectedIndex = Math.Clamp(settings.MandelbrotRotation, 0, 2);
        if (CbMandelbrotColorScheme.Items.Count == 0)
        {
            CbMandelbrotColorScheme.Items.Add("Psychedelic");
            CbMandelbrotColorScheme.Items.Add("Ocean");
            CbMandelbrotColorScheme.Items.Add("Ember");
            CbMandelbrotColorScheme.Items.Add("Midnight");
            CbMandelbrotColorScheme.Items.Add("Forest");
        }
        CbMandelbrotColorScheme.SelectedIndex = Math.Clamp(settings.MandelbrotColorScheme, 0, 4);
        UpdateMandelbrotTuningVisibility();

        // Ferrofluid tuning
        SliderFerrofluidCoreGravity.Value = settings.FerrofluidCoreGravity;
        TxtFerrofluidCoreGravity.Text = $"{(int)settings.FerrofluidCoreGravity}";
        SliderFerrofluidMutualAttraction.Value = settings.FerrofluidMutualAttraction;
        TxtFerrofluidMutualAttraction.Text = $"{(int)settings.FerrofluidMutualAttraction}";
        SliderFerrofluidDamping.Value = settings.FerrofluidDamping * 100;
        TxtFerrofluidDamping.Text = $"{(int)(settings.FerrofluidDamping * 100)}%";
        SliderFerrofluidExplosionForce.Value = settings.FerrofluidExplosionForce;
        TxtFerrofluidExplosionForce.Text = $"{(int)settings.FerrofluidExplosionForce}";
        SliderFerrofluidExplosionDuration.Value = settings.FerrofluidExplosionDuration * 10;
        TxtFerrofluidExplosionDuration.Text = $"{settings.FerrofluidExplosionDuration:F1}s";
        SliderFerrofluidBristleForce.Value = settings.FerrofluidBristleForce;
        TxtFerrofluidBristleForce.Text = $"{(int)settings.FerrofluidBristleForce}";
        SliderFerrofluidMaxSpeed.Value = settings.FerrofluidMaxSpeed;
        TxtFerrofluidMaxSpeed.Text = $"{(int)settings.FerrofluidMaxSpeed}";
        SliderFerrofluidExplosionBassThreshold.Value = settings.FerrofluidExplosionBassThreshold * 100;
        TxtFerrofluidExplosionBassThreshold.Text = $"{(int)(settings.FerrofluidExplosionBassThreshold * 100)}%";
        SliderFerrofluidBristleTrebleThreshold.Value = settings.FerrofluidBristleTrebleThreshold * 100;
        TxtFerrofluidBristleTrebleThreshold.Text = $"{(int)(settings.FerrofluidBristleTrebleThreshold * 100)}%";
        UpdateFerrofluidTuningVisibility();

        // Matrix tuning
        CbMatrixColorCycling.IsChecked = settings.MatrixColorCycling;
        CbMatrixInfiniteZoom.IsChecked = settings.MatrixInfiniteZoom;
        SliderMatrixZoomRate.Value = settings.MatrixZoomRate;
        MatrixZoomRateValueText.Text = settings.MatrixZoomRate.ToString("F2");
        SliderMatrixMaxTrails.Value = settings.MatrixMaxTrails;
        MatrixMaxTrailsValueText.Text = settings.MatrixMaxTrails.ToString();
        CbMatrixDisableBlur.IsChecked = settings.MatrixDisableBlur;
        UpdateMatrixTuningVisibility();

        // Game of Life tuning
        SliderGameOfLifeCellSize.Value = settings.GameOfLifeCellSize;
        TxtGameOfLifeCellSize.Text = $"{settings.GameOfLifeCellSize} px";
        CbGameOfLifeUseScreenRate.IsChecked = settings.GameOfLifeUseScreenRate;
        SliderGameOfLifeTickInterval.Value = settings.GameOfLifeTickIntervalMs;
        TxtGameOfLifeTickInterval.Text = $"{settings.GameOfLifeTickIntervalMs} ms";
        UpdateGameOfLifeTickIntervalVisibility();
        SliderGameOfLifeFadeGenerations.Value = settings.GameOfLifeFadeGenerations;
        TxtGameOfLifeFadeGenerations.Text = settings.GameOfLifeFadeGenerations == 0 ? "Off" : $"{settings.GameOfLifeFadeGenerations}";
        SliderGameOfLifeHeatBoost.Value = settings.GameOfLifeHeatBoost;
        TxtGameOfLifeHeatBoost.Text = settings.GameOfLifeHeatBoost == 0 ? "Off" : $"{settings.GameOfLifeHeatBoost}";
        SliderGameOfLifeDensity.Value = settings.GameOfLifeDensity;
        TxtGameOfLifeDensity.Text = $"{settings.GameOfLifeDensity}";
        CbGameOfLifeCameraRoam.IsChecked = settings.GameOfLifeCameraRoam;
        SliderGameOfLifeCameraMaxZoom.Value = settings.GameOfLifeCameraMaxZoom;
        TxtGameOfLifeCameraMaxZoom.Text = $"{settings.GameOfLifeCameraMaxZoom:F1}x";
        SliderGameOfLifeCameraOverscan.Value = settings.GameOfLifeCameraOverscan;
        TxtGameOfLifeCameraOverscan.Text = $"{settings.GameOfLifeCameraOverscan}%";
        SliderGameOfLifeCameraSpeed.Value = settings.GameOfLifeCameraSpeed;
        TxtGameOfLifeCameraSpeed.Text = FormatCameraSpeed(settings.GameOfLifeCameraSpeed);
        CbGameOfLifeRestartOnTrackChange.IsChecked = settings.GameOfLifeRestartOnTrackChange;
        CbGameOfLifeAntiStagnation.IsChecked = settings.GameOfLifeAntiStagnation;
        SliderGameOfLifeAntiStagnationIntensity.Value = Math.Clamp(settings.GameOfLifeAntiStagnationIntensity, 1, 10);
        TxtGameOfLifeAntiStagnationIntensity.Text = $"{Math.Clamp(settings.GameOfLifeAntiStagnationIntensity, 1, 10)}";
        CbGameOfLifeScalingMode.Items.Clear();
        CbGameOfLifeScalingMode.Items.Add("Nearest Neighbor");
        CbGameOfLifeScalingMode.Items.Add("Smooth (Fant)");
        CbGameOfLifeScalingMode.Items.Add("Blocky (aliased)");
        CbGameOfLifeScalingMode.SelectedIndex = Math.Clamp(settings.GameOfLifeScalingMode, 0, 2);
        CbGameOfLifeColorMode.Items.Clear();
        CbGameOfLifeColorMode.Items.Add("Genetic Blend (parents)");
        CbGameOfLifeColorMode.Items.Add("Genetic Vivid (re-saturated)");
        CbGameOfLifeColorMode.Items.Add("Era-Banded (rotating hue)");
        CbGameOfLifeColorMode.SelectedIndex = Math.Clamp(settings.GameOfLifeColorMode, 0, 2);
        LoadSeedColorCheckboxes(settings.GameOfLifeSeedColorMask);
        CbGameOfLifeHueSpread.Items.Clear();
        foreach (int deg in new[] { 0, 15, 30, 45, 60 })
            CbGameOfLifeHueSpread.Items.Add($"{deg}°");
        CbGameOfLifeHueSpread.SelectedIndex = settings.GameOfLifeHueSpread switch
        {
            0 => 0, <= 15 => 1, <= 30 => 2, <= 45 => 3, _ => 4
        };
        CbGameOfLifeSeedSpread.Items.Clear();
        CbGameOfLifeSeedSpread.Items.Add("Clustered");
        CbGameOfLifeSeedSpread.Items.Add("Scattered");
        CbGameOfLifeSeedSpread.Items.Add("Full");
        CbGameOfLifeSeedSpread.SelectedIndex = Math.Clamp(settings.GameOfLifeSeedSpread, 0, 2);
        SliderGameOfLifeBirthGenerations.Value = Math.Clamp(settings.GameOfLifeBirthGenerations, 0, 8);
        TxtGameOfLifeBirthGenerations.Text = settings.GameOfLifeBirthGenerations == 0
            ? "Off"
            : $"{Math.Clamp(settings.GameOfLifeBirthGenerations, 0, 8)}";
        CbGameOfLifeBloom.IsChecked = settings.GameOfLifeBloom;
        SliderGameOfLifeBloomIntensity.Value = Math.Clamp(settings.GameOfLifeBloomIntensity, 1, 10);
        TxtGameOfLifeBloomIntensity.Text = $"{Math.Clamp(settings.GameOfLifeBloomIntensity, 1, 10) * 10}%";
        SliderGameOfLifeBloomRadius.Value = Math.Clamp(settings.GameOfLifeBloomRadius, 1, 10);
        TxtGameOfLifeBloomRadius.Text = $"{Math.Clamp(settings.GameOfLifeBloomRadius, 1, 10)}×";
        UpdateGameOfLifeBloomVisibility();
        CbGameOfLifeRulesEngine.Items.Clear();
        CbGameOfLifeRulesEngine.Items.Add("Conway (B3/S23)");
        CbGameOfLifeRulesEngine.Items.Add("Brian's Brain (B2/S/refractory)");
        CbGameOfLifeRulesEngine.Items.Add("Star Wars (B2/S345/4)");
        CbGameOfLifeRulesEngine.SelectedIndex = Math.Clamp(settings.GameOfLifeRulesEngine, 0, 2);
        // B/S rule preset dropdown
        CbGameOfLifeRulePreset.Items.Clear();
        foreach (var (name, _) in BsPresets)
            CbGameOfLifeRulePreset.Items.Add(name);
        LoadBsCheckboxesFromRule(settings.GameOfLifeCustomRule);
        double huSpeed = Math.Clamp(settings.GameOfLifeEraBandedHueSpeed, 0.1, 10.0);
        SliderGameOfLifeEraBandedHueSpeed.Value = huSpeed;
        TxtGameOfLifeEraBandedHueSpeed.Text = $"{huSpeed:F1}×";
        UpdateGameOfLifeCameraVisibility();
        UpdateGameOfLifeTuningVisibility();
        UpdateGameOfLifeRulesVisibility();
        UpdateSeedColorVisibility();

        // Gravity tuning
        SliderGravityG.Value = settings.GravityG;
        TxtGravityG.Text = $"{settings.GravityG}";
        CbGravityCameraRoam.IsChecked = settings.GravityCameraRoam;
        SliderGravityOrbitRepulsion.Value = settings.GravityOrbitRepulsion;
        TxtGravityOrbitRepulsion.Text = $"{settings.GravityOrbitRepulsion:F1}";
        SliderGravityCentralGravity.Value = settings.GravityCentralGravity;
        TxtGravityCentralGravity.Text = $"{(int)settings.GravityCentralGravity}";
        SliderGravityOrbitalPerturbation.Value = settings.GravityOrbitalPerturbation;
        TxtGravityOrbitalPerturbation.Text = $"{settings.GravityOrbitalPerturbation:F1}";
        CbGravityRestartOnTrackChange.IsChecked = settings.GravityRestartOnTrackChange;
        SliderGravityBlobMultiplier.Value = settings.GravityBlobMultiplier;
        TxtGravityBlobMultiplier.Text = $"{settings.GravityBlobMultiplier:F1}x";
        CbGravityShowDiagnostics.IsChecked = settings.GravityShowDiagnostics;
        SliderGravitySupernovaMass.Value = settings.GravitySupernovaMass;
        TxtGravitySupernovaMass.Text = settings.GravitySupernovaMass < 10 ? "Off" : $"{(int)settings.GravitySupernovaMass}px";
        SliderGravityDensity.Value = settings.GravityDensity;
        TxtGravityDensity.Text = settings.GravityDensity switch { 0 => "Low", 2 => "High", _ => "Medium" };
        UpdateGravityTuningVisibility();

        // Clock tuning
        CbClockMode.Items.Add("Analog");
        CbClockMode.Items.Add("Digital");
        CbClockMode.SelectedIndex = settings.ClockMode;
        SliderClockBrightness.Value = settings.ClockBrightness * 100;
        TxtClockBrightness.Text = $"{(int)(settings.ClockBrightness * 100)}%";
        SliderClockDigitalSize.Value = settings.ClockDigitalSize;
        TxtClockDigitalSize.Text = $"{settings.ClockDigitalSize}%";
        SliderClockHourFormat.Value = settings.ClockUse24Hour ? 1 : 0;
        TxtClockHourFormat.Text = settings.ClockUse24Hour ? "24-hour" : "12-hour";
        SliderClockAnalogSize.Value = Math.Clamp(settings.ClockAnalogSize, 0, 4);
        TxtClockAnalogSize.Text = AnalogSizeLabel(settings.ClockAnalogSize);
        CbClockAnalogStyle.SelectedIndex = Math.Clamp(settings.ClockAnalogStyle, 0, 1);
        UpdateClockTuningVisibility();

        CbReactiveBlobsPlayfield.IsChecked = settings.ReactiveBlobsPlayfield;
        CbReactiveBlobsBackglass.IsChecked = settings.ReactiveBlobsBackglass;
        CbReactiveBlobsTopper.IsChecked = settings.ReactiveBlobsTopper;
        CbReactiveBlobsDmd.IsChecked = settings.ReactiveBlobsDmd;
        CbReactiveProjectM.IsChecked = settings.ReactiveProjectM;
        SliderReactivityThreshold.Value = settings.ReactivityThreshold * 100;
        SliderReactiveSpeed.Value = settings.ReactiveSpeedMs;
        SliderReactiveOverdrive.Value = settings.ReactiveOverdrive * 10;

        foreach (var style in new[] { "Default", "Colorful" })
            CbIconStyle.Items.Add(style);
        CbIconStyle.SelectedIndex = (int)settings.DmdIconStyle;

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
        SliderNowPlayingAreaSize.Value = settings.DmdNowPlayingAreaSizeModifier;
        SliderGenreIconSize.Value = settings.DmdGenreIconSizeModifier;
        SliderGenreIconSpacing.Value = settings.DmdGenreIconSpacingModifier;
        SliderGenreIconPadding.Value = settings.DmdGenreIconPaddingModifier;
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
                PlexInstanceId = entry.PlexInstanceId,
                PlexHubsEnabled = entry.PlexHubsEnabled,
                PlexPlaylistsEnabled = entry.PlexPlaylistsEnabled,
                SourceInstanceId = entry.SourceInstanceId,
                SourceCategoryId = entry.SourceCategoryId,
                SourceTypeId = entry.SourceTypeId,
                IsGenericSource = entry.IsGenericSource,
                SortOrder = entry.SortOrder
            });
        }

        // Sort categories by SortOrder (playlists will be merged later via SetPlaylistManager)
        var sorted = _categoryVisibilityItems.OrderBy(i => i.SortOrder).ToList();
        _categoryVisibilityItems.Clear();
        _categoryVisibilityItems.AddRange(sorted);

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
        CbPreemptiveCache.IsChecked = settings.PreemptiveCache;
        CbPurgeCacheOnShutdown.IsChecked = settings.PurgeCacheOnShutdown;
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

        // Per-screen intensity/speed settings (slider values match AppSettings directly: intensity 5-80 as %, speed 0.2-5.0)
        SliderIntensityPlayfield.Value = settings.PlayfieldIntensity * 100;
        TxtIntensityPlayfield.Text = $"{(int)(settings.PlayfieldIntensity * 100)}%";
        SliderSpeedPlayfield.Value = settings.PlayfieldSpeed;
        TxtSpeedPlayfield.Text = $"{settings.PlayfieldSpeed:F1}×";
        SliderIntensityBackglass.Value = settings.BackglassIntensity * 100;
        TxtIntensityBackglass.Text = $"{(int)(settings.BackglassIntensity * 100)}%";
        SliderSpeedBackglass.Value = settings.BackglassSpeed;
        TxtSpeedBackglass.Text = $"{settings.BackglassSpeed:F1}×";
        SliderIntensityTopper.Value = settings.TopperIntensity * 100;
        TxtIntensityTopper.Text = $"{(int)(settings.TopperIntensity * 100)}%";
        SliderSpeedTopper.Value = settings.TopperSpeed;
        TxtSpeedTopper.Text = $"{settings.TopperSpeed:F1}×";
        SliderIntensityDmd.Value = settings.DmdIntensity * 100;
        TxtIntensityDmd.Text = $"{(int)(settings.DmdIntensity * 100)}%";
        SliderSpeedDmd.Value = settings.DmdSpeed;
        TxtSpeedDmd.Text = $"{settings.DmdSpeed:F1}×";

        // Store original values for cancel revert
        _originalPlayfieldIntensity = settings.PlayfieldIntensity;
        _originalPlayfieldSpeed = settings.PlayfieldSpeed;
        _originalBackglassIntensity = settings.BackglassIntensity;
        _originalBackglassSpeed = settings.BackglassSpeed;
        _originalTopperIntensity = settings.TopperIntensity;
        _originalTopperSpeed = settings.TopperSpeed;
        _originalDmdIntensity = settings.DmdIntensity;
        _originalDmdSpeed = settings.DmdSpeed;
        _originalDistortion = settings.TopperDistortion;
        _originalScreenScaling = settings.TopperScreenScaling;
        _originalPlayfieldBlobPattern = settings.PlayfieldBlobPattern;
        _originalPlayfieldBlobCount = settings.PlayfieldBlobCount;
        _originalPlayfieldBlobSizeOffset = settings.PlayfieldBlobSizeOffset;
        _originalPlayfieldRotation = settings.PlayfieldRotation;
        _originalPlayfieldApplyOrientationToVideos = settings.PlayfieldApplyOrientationToVideos;
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
        _originalReactiveBlobsPlayfield = settings.ReactiveBlobsPlayfield;
        _originalReactiveBlobsBackglass = settings.ReactiveBlobsBackglass;
        _originalReactiveBlobsTopper = settings.ReactiveBlobsTopper;
        _originalReactiveBlobsDmd = settings.ReactiveBlobsDmd;
        _originalReactiveProjectM = settings.ReactiveProjectM;
        _originalReactivityThreshold = settings.ReactivityThreshold;
        _originalReactiveSpeedMs = settings.ReactiveSpeedMs;
        _originalReactiveOverdrive = settings.ReactiveOverdrive;
        _originalTitleText = settings.TitleText;
        _originalLogoText = settings.LogoText;
        _originalLogoBehindVisuals = settings.LogoBehindVisuals;
        _originalLogoSpin = settings.LogoSpin;
        _originalLogoShadow = settings.LogoShadow;
        _originalLogoRings = settings.LogoRings;
        _originalLogoRingsBrightness = settings.LogoRingsBrightness;
        _originalLogoBrightness = settings.LogoBrightness;
        _originalLogoColorMode = settings.LogoColorMode;
        _originalTopperLogoSpin = settings.TopperLogoSpin;
        _originalTopperLogoShadow = settings.TopperLogoShadow;
        _originalTopperLogoRings = settings.TopperLogoRings;
        _originalTopperLogoRingsBrightness = settings.TopperLogoRingsBrightness;
        _originalTopperLogoBrightness = settings.TopperLogoBrightness;
        _originalTopperLogoColorMode = settings.TopperLogoColorMode;
        _originalMandelbrotUseGpu = settings.MandelbrotUseGpu;
        _originalMandelbrotAdaptiveIterations = settings.MandelbrotAdaptiveIterations;
        _originalMandelbrotMaxIterations = settings.MandelbrotMaxIterations;
        _originalMandelbrotTickIntervalMs = settings.MandelbrotTickIntervalMs;
        _originalMandelbrotUseScreenRate = settings.MandelbrotUseScreenRate;
        _originalMandelbrotRenderScale = settings.MandelbrotRenderScale;
        _originalMandelbrotPerturbation = settings.MandelbrotPerturbation;
        _originalMandelbrotDiscovery = settings.MandelbrotDiscovery;
        _originalMandelbrotDimming = settings.MandelbrotDimming;
        _originalMandelbrotHistogramColoring = settings.MandelbrotHistogramColoring;
        _originalMandelbrotRotation = settings.MandelbrotRotation;
        _originalMandelbrotColorScheme = settings.MandelbrotColorScheme;
        _originalGameOfLifeCellSize = settings.GameOfLifeCellSize;
        _originalGameOfLifeTickIntervalMs = settings.GameOfLifeTickIntervalMs;
        _originalGameOfLifeUseScreenRate = settings.GameOfLifeUseScreenRate;
        _originalGameOfLifeFadeGenerations = settings.GameOfLifeFadeGenerations;
        _originalGameOfLifeHeatBoost = settings.GameOfLifeHeatBoost;
        _originalGameOfLifeDensity = settings.GameOfLifeDensity;
        _originalGameOfLifeCameraRoam = settings.GameOfLifeCameraRoam;
        _originalGameOfLifeCameraMaxZoom = settings.GameOfLifeCameraMaxZoom;
        _originalGameOfLifeCameraOverscan = settings.GameOfLifeCameraOverscan;
        _originalGameOfLifeCameraSpeed = settings.GameOfLifeCameraSpeed;
        _originalGameOfLifeRestartOnTrackChange = settings.GameOfLifeRestartOnTrackChange;
        _originalGameOfLifeColorMode = settings.GameOfLifeColorMode;
        _originalGameOfLifeRulesEngine = settings.GameOfLifeRulesEngine;
        _originalGameOfLifeEraBandedHueSpeed = settings.GameOfLifeEraBandedHueSpeed;
        _originalGameOfLifeCustomRule = settings.GameOfLifeCustomRule ?? "B3/S23";
        _originalGameOfLifeSeedColorMask = settings.GameOfLifeSeedColorMask;
        _originalGameOfLifeHueSpread = settings.GameOfLifeHueSpread;
        _originalGameOfLifeSeedSpread = settings.GameOfLifeSeedSpread;
        _originalGameOfLifeBloom = settings.GameOfLifeBloom;
        _originalGameOfLifeBloomRadius = settings.GameOfLifeBloomRadius;
        _originalGameOfLifeBloomIntensity = settings.GameOfLifeBloomIntensity;
        _originalGameOfLifeBirthGenerations = settings.GameOfLifeBirthGenerations;
        _originalGravityBlobMultiplier = settings.GravityBlobMultiplier;
        _originalGravityCameraRoam = settings.GravityCameraRoam;
        _originalGravityShowDiagnostics = settings.GravityShowDiagnostics;
        _originalGravityDensity = settings.GravityDensity;
        _originalClockMode = settings.ClockMode;
        _originalClockBrightness = settings.ClockBrightness;
        _originalClockDigitalSize = settings.ClockDigitalSize;
        _originalClockUse24Hour = settings.ClockUse24Hour;
        _originalClockAnalogSize = settings.ClockAnalogSize;
        _originalClockAnalogStyle = settings.ClockAnalogStyle;
        _originalProjectMPresetDuration = settings.ProjectMPresetDuration;
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

        // Network
        SliderNetworkCaching.Value = settings.NetworkCachingMs;
        SliderLiveCaching.Value = settings.LiveCachingMs;
        SliderFileCaching.Value = settings.FileCachingMs;
        CbHttpReconnect.IsChecked = settings.HttpReconnect;
        SliderNetworkTimeout.Value = settings.NetworkTimeoutSeconds;
        NetworkCachingValueText.Text = settings.NetworkCachingMs.ToString();
        LiveCachingValueText.Text = settings.LiveCachingMs.ToString();
        FileCachingValueText.Text = settings.FileCachingMs.ToString();
        NetworkTimeoutValueText.Text = settings.NetworkTimeoutSeconds.ToString();

        // Playback (gapless is app-owned; engine/quality/stereo moved to the Plug-ins tab).
        CbPlexGapless.IsChecked = settings.PlexGaplessPlayback;

        // Populate the Plug-ins tab from the live source registry once the window is loaded
        // (Owner/DataContext is available by then). Same timing for the AutoDJ provider list.
        Loaded += (_, _) =>
        {
            PopulatePluginSourcesTab();
            PopulateAutoDjProvider(settings);
        };

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
            // Refresh monitor info if About tab is already selected on open
            if (SettingsTabs.SelectedItem is System.Windows.Controls.TabItem tab &&
                tab.Header is string header && header == "ABOUT")
                RefreshMonitorInfo();
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

    public void SetTopperProxy(TopperProxy? topper)
    {
        _topperProxy = topper;
    }

    public void SetDofClient(DofClient? dofClient)
    {
        _sharedDofClient = dofClient;
    }

    public void SetPlayfieldProxy(PlayfieldProxy? proxy)
    {
        _playfieldProxy = proxy;
    }

    public void SetPlaylistManager(PlaylistManager? manager)
    {
        _playlistManager = manager;
        if (manager == null) return;

        // Merge playlists into the category list for unified sorting
        foreach (var pl in manager.Playlists)
        {
            var defaultIcon = pl.Name == "Favorites" ? "⭐" : pl.Kind == PlaylistKind.Live ? "🔎" : "📋";
            var icon = string.IsNullOrEmpty(pl.Icon) ? defaultIcon : pl.Icon;
            _categoryVisibilityItems.Add(new CategoryVisibilityItem
            {
                Id = pl.Id,
                Name = pl.Name,
                Icon = icon,
                SearchTerm = "",
                OriginalSearchTerm = "",
                IsVisible = true,
                IsPlaylist = true,
                PlaylistId = pl.Id,
                SortOrder = pl.SortOrder,
                IsSpecial = pl.Name == "Favorites",
            });
        }

        // Re-sort all items by SortOrder so playlists interleave with categories
        var sorted = _categoryVisibilityItems.OrderBy(i => i.SortOrder).ToList();
        _categoryVisibilityItems.Clear();
        _categoryVisibilityItems.AddRange(sorted);
        CategoryListView.ItemsSource = null;
        CategoryListView.ItemsSource = _categoryVisibilityItems;
        UpdateCategoryVisibilityText();
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

    private readonly ObservableCollection<string> _startupDittiPaths = new();

    private void AddStartupDitti_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Startup Ditti Audio",
            Filter = "Audio files|*.mp3;*.m4a;*.ogg;*.wav;*.flac;*.wma;*.aac|All files|*.*",
            Multiselect = true
        };
        // Seed initial directory from an existing entry if any
        if (_startupDittiPaths.Count > 0)
        {
            var first = _startupDittiPaths[0];
            var resolved = System.IO.Path.IsPathRooted(first)
                ? first
                : System.IO.Path.Combine(AppContext.BaseDirectory, first);
            var dir = System.IO.Path.GetDirectoryName(resolved);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }
        if (dlg.ShowDialog(this) != true) return;

        foreach (var file in dlg.FileNames)
        {
            var stored = MakePortableDittiPath(file);
            if (!_startupDittiPaths.Contains(stored))
                _startupDittiPaths.Add(stored);
        }
    }

    private void RemoveStartupDitti_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.DataContext is string path)
            _startupDittiPaths.Remove(path);
    }

    /// <summary>
    /// If <paramref name="fullPath"/> lives inside the executing app's base directory
    /// (or any subfolder of it), returns a relative path so settings are portable.
    /// Otherwise returns the original absolute path.
    /// </summary>
    private static string MakePortableDittiPath(string fullPath)
    {
        try
        {
            var baseDir = System.IO.Path.GetFullPath(AppContext.BaseDirectory)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
                + System.IO.Path.DirectorySeparatorChar;
            var full = System.IO.Path.GetFullPath(fullPath);
            if (full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                return full.Substring(baseDir.Length);
        }
        catch { }
        return fullPath;
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

    private void ClearStaticImage_Click(object sender, RoutedEventArgs e)
    {
        TbStaticImagePath.Text = "";
    }

    private void ClearVideo_Click(object sender, RoutedEventArgs e)
    {
        TbVideoPath.Text = "";
    }

    // ── Backglass ambient content handlers (independent of the playfield) ──
    private readonly ObservableCollection<string> _backglassVideoFolders = new();

    private readonly ObservableCollection<string> _topperVideoFolders = new();

    /// <summary>Bindable row for the Pinup window→media-folder mapping list.</summary>
    private sealed class PinupFolderMapRow
    {
        public string WindowName { get; init; } = "";
        public IReadOnlyList<string> FolderOptions { get; } = PinupFolderMapping.FolderOptions;
        public string Folder { get; set; } = "";
    }

    private readonly ObservableCollection<PinupFolderMapRow> _pinupFolderMapRows = new();

    // ── Topper ambient content ──────────────────────────────────────────

    private void BrowseTopperStaticImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Topper Image",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff|All files|*.*"
        };
        if (!string.IsNullOrWhiteSpace(TbTpStaticImagePath.Text))
        {
            var dir = System.IO.Path.GetDirectoryName(TbTpStaticImagePath.Text);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }
        if (dlg.ShowDialog(this) == true)
            TbTpStaticImagePath.Text = dlg.FileName;
    }

    private void ClearTopperStaticImage_Click(object sender, RoutedEventArgs e)
    {
        TbTpStaticImagePath.Text = "";
    }

    private void BrowseTopperVideo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Topper Video",
            Filter = "Video files|*.mp4;*.avi;*.wmv;*.mkv;*.mov|All files|*.*"
        };
        if (!string.IsNullOrWhiteSpace(TbTpVideoPath.Text))
        {
            var dir = System.IO.Path.GetDirectoryName(TbTpVideoPath.Text);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }
        if (dlg.ShowDialog(this) == true)
            TbTpVideoPath.Text = dlg.FileName;
    }

    private void ClearTopperVideo_Click(object sender, RoutedEventArgs e)
    {
        TbTpVideoPath.Text = "";
    }

    private void AddTopperVideoFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Topper Video Folder",
            Multiselect = true
        };
        string? seed = null;
        if (_topperVideoFolders.Count > 0)
        {
            var first = _topperVideoFolders[0];
            seed = System.IO.Path.IsPathRooted(first)
                ? first
                : System.IO.Path.Combine(AppContext.BaseDirectory, first);
        }
        else if (System.IO.Directory.Exists(AppSettings.DefaultTopperVideoFolder))
        {
            seed = AppSettings.DefaultTopperVideoFolder;
        }
        if (!string.IsNullOrEmpty(seed) && System.IO.Directory.Exists(seed))
            dlg.InitialDirectory = seed;

        if (dlg.ShowDialog(this) != true) return;

        foreach (var folder in dlg.FolderNames)
        {
            var stored = MakePortableDittiPath(folder);
            if (!_topperVideoFolders.Contains(stored))
                _topperVideoFolders.Add(stored);
        }
    }

    private void RemoveTopperVideoFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.DataContext is string path)
            _topperVideoFolders.Remove(path);
    }

    private void SliderTpVideoFolderMinDuration_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SliderTpVideoFolderMaxDuration != null)
        {
            double max = SliderTpVideoFolderMaxDuration.Value;
            if (max < VideoFolderMaxNoLimitTick && max < e.NewValue)
                SliderTpVideoFolderMaxDuration.Value = Math.Min(VideoFolderMaxNoLimitTick, e.NewValue);
        }
        UpdateTopperVideoFolderDurationLabels();
    }

    private void SliderTpVideoFolderMaxDuration_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SliderTpVideoFolderMinDuration != null &&
            e.NewValue < VideoFolderMaxNoLimitTick && e.NewValue < SliderTpVideoFolderMinDuration.Value)
        {
            SliderTpVideoFolderMaxDuration.Value = SliderTpVideoFolderMinDuration.Value;
            return;
        }
        UpdateTopperVideoFolderDurationLabels();
    }

    private void UpdateTopperVideoFolderDurationLabels()
    {
        if (TxtTpVideoFolderMinDuration != null)
            TxtTpVideoFolderMinDuration.Text = $"{(int)SliderTpVideoFolderMinDuration.Value}s";
        if (TxtTpVideoFolderMaxDuration != null)
        {
            TxtTpVideoFolderMaxDuration.Text = SliderTpVideoFolderMaxDuration.Value >= VideoFolderMaxNoLimitTick
                ? "No Maximum"
                : $"{(int)SliderTpVideoFolderMaxDuration.Value}s";
        }
    }

    private void CbTpVideoAudioEnabled_Changed(object sender, RoutedEventArgs e)
    {
        UpdateTopperVideoAudioControls();
    }

    private void SliderTpVideoAudioVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateTopperVideoAudioControls();
    }

    private void UpdateTopperVideoAudioControls()
    {
        if (TxtTpVideoAudioVolume != null)
            TxtTpVideoAudioVolume.Text = $"{(int)SliderTpVideoAudioVolume.Value}%";
        if (SliderTpVideoAudioVolume != null)
            SliderTpVideoAudioVolume.IsEnabled = CbTpVideoAudioEnabled.IsChecked == true;
    }

    private void BrowseBackglassStaticImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Backglass Image",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff|All files|*.*"
        };
        if (!string.IsNullOrWhiteSpace(TbBgStaticImagePath.Text))
        {
            var dir = System.IO.Path.GetDirectoryName(TbBgStaticImagePath.Text);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }
        if (dlg.ShowDialog(this) == true)
            TbBgStaticImagePath.Text = dlg.FileName;
    }

    private void ClearBackglassStaticImage_Click(object sender, RoutedEventArgs e)
    {
        TbBgStaticImagePath.Text = "";
    }

    private void BrowseBackglassVideo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Backglass Video",
            Filter = "Video files|*.mp4;*.avi;*.wmv;*.mkv;*.mov|All files|*.*"
        };
        if (!string.IsNullOrWhiteSpace(TbBgVideoPath.Text))
        {
            var dir = System.IO.Path.GetDirectoryName(TbBgVideoPath.Text);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }
        if (dlg.ShowDialog(this) == true)
            TbBgVideoPath.Text = dlg.FileName;
    }

    private void ClearBackglassVideo_Click(object sender, RoutedEventArgs e)
    {
        TbBgVideoPath.Text = "";
    }

    private void AddBackglassVideoFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Backglass Video Folder",
            Multiselect = true
        };
        string? seed = null;
        if (_backglassVideoFolders.Count > 0)
        {
            var first = _backglassVideoFolders[0];
            seed = System.IO.Path.IsPathRooted(first)
                ? first
                : System.IO.Path.Combine(AppContext.BaseDirectory, first);
        }
        else if (System.IO.Directory.Exists(AppSettings.DefaultBackglassVideoFolder))
        {
            seed = AppSettings.DefaultBackglassVideoFolder;
        }
        if (!string.IsNullOrEmpty(seed) && System.IO.Directory.Exists(seed))
            dlg.InitialDirectory = seed;

        if (dlg.ShowDialog(this) != true) return;

        foreach (var folder in dlg.FolderNames)
        {
            var stored = MakePortableDittiPath(folder);
            if (!_backglassVideoFolders.Contains(stored))
                _backglassVideoFolders.Add(stored);
        }
    }

    private void RemoveBackglassVideoFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.DataContext is string path)
            _backglassVideoFolders.Remove(path);
    }

    private void SliderBgVideoFolderMinDuration_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SliderBgVideoFolderMaxDuration != null)
        {
            double max = SliderBgVideoFolderMaxDuration.Value;
            if (max < VideoFolderMaxNoLimitTick && max < e.NewValue)
                SliderBgVideoFolderMaxDuration.Value = Math.Min(VideoFolderMaxNoLimitTick, e.NewValue);
        }
        UpdateBackglassVideoFolderDurationLabels();
    }

    private void SliderBgVideoFolderMaxDuration_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SliderBgVideoFolderMinDuration != null &&
            e.NewValue < VideoFolderMaxNoLimitTick && e.NewValue < SliderBgVideoFolderMinDuration.Value)
        {
            SliderBgVideoFolderMaxDuration.Value = SliderBgVideoFolderMinDuration.Value;
            return;
        }
        UpdateBackglassVideoFolderDurationLabels();
    }

    private void UpdateBackglassVideoFolderDurationLabels()
    {
        if (TxtBgVideoFolderMinDuration != null)
            TxtBgVideoFolderMinDuration.Text = $"{(int)SliderBgVideoFolderMinDuration.Value}s";
        if (TxtBgVideoFolderMaxDuration != null)
        {
            TxtBgVideoFolderMaxDuration.Text = SliderBgVideoFolderMaxDuration.Value >= VideoFolderMaxNoLimitTick
                ? "No Maximum"
                : $"{(int)SliderBgVideoFolderMaxDuration.Value}s";
        }
    }

    private void CbBgVideoAudioEnabled_Changed(object sender, RoutedEventArgs e)
    {
        UpdateBackglassVideoAudioControls();
    }

    private void SliderBgVideoAudioVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateBackglassVideoAudioControls();
    }

    private void UpdateBackglassVideoAudioControls()
    {
        if (TxtBgVideoAudioVolume != null)
            TxtBgVideoAudioVolume.Text = $"{(int)SliderBgVideoAudioVolume.Value}%";
        if (SliderBgVideoAudioVolume != null)
            SliderBgVideoAudioVolume.IsEnabled = CbBgVideoAudioEnabled.IsChecked == true;
    }

    private void BrowsePopperDb_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Pinup Popper Database",
            Filter = "Pinup Popper database|PUPDatabase.db|Database files|*.db|All files|*.*"
        };
        var current = TbPopperDbPath.Text;
        if (!string.IsNullOrWhiteSpace(current))
        {
            var dir = System.IO.Path.GetDirectoryName(current);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }
        else if (System.IO.File.Exists(PinupSettings.DefaultPopperDbPath))
        {
            dlg.InitialDirectory = System.IO.Path.GetDirectoryName(PinupSettings.DefaultPopperDbPath);
        }
        if (dlg.ShowDialog(this) == true)
        {
            TbPopperDbPath.Text = dlg.FileName;
            LoadPinupPlaylistsFromDb();
        }
    }

    private void ClearPopperDb_Click(object sender, RoutedEventArgs e)
    {
        TbPopperDbPath.Text = "";
    }

    private void RefreshPinupPlaylists_Click(object sender, RoutedEventArgs e)
    {
        LoadPinupPlaylistsFromDb();
    }

    /// <summary>
    /// Loads visible playlists from the selected Popper database, syncing them against the
    /// currently-shown list (remove missing, add new unchecked, match by PlayListID), then
    /// rebuilds the resolved game list on a background thread and shows it in the debug box.
    /// </summary>
    private void LoadPinupPlaylistsFromDb()
    {
        var dbPath = TbPopperDbPath.Text;
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            PinupPlaylistStatus.Text = "Select a Pinup Popper database to load playlists.";
            return;
        }

        try
        {
            // Fold the currently-shown items back into _pinupSettings so SyncPlaylists can
            // preserve enabled flags by PlayListID.
            _pinupSettings.Playlists = new List<PinupPlaylist>(_pinupPlaylists);
            var live = PinupDatabase.GetVisiblePlaylists(dbPath);
            _pinupSettings.SyncPlaylists(live);

            _pinupPlaylists.Clear();
            foreach (var pl in _pinupSettings.Playlists)
                _pinupPlaylists.Add(pl);
            RefreshPinupColumns();
            UpdatePinupPlaylistStatus();

            RebuildPinupGamesAsync(dbPath);
        }
        catch (Exception ex)
        {
            DebugLog.Log("Pinup", $"Failed to load playlists: {ex.Message}");
            PinupPlaylistStatus.Text = $"Could not read playlists: {ex.Message}";
        }
    }

    /// <summary>
    /// Builds the de-duped game list from the enabled playlists on a background thread and
    /// reports the resolved count in the status label.
    /// </summary>
    private void RebuildPinupGamesAsync(string dbPath)
    {
        var enabled = _pinupPlaylists.Where(p => p.Enabled).ToList();
        if (enabled.Count == 0)
        {
            _pinupSettings.Games = new List<PinupGame>();
            UpdatePinupPlaylistStatus();
            return;
        }

        Task.Run(() => PinupDatabase.BuildGameList(dbPath, enabled))
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    var msg = t.Exception?.GetBaseException().Message ?? "unknown error";
                    DebugLog.Log("Pinup", $"BuildGameList failed: {msg}");
                    PinupPlaylistStatus.Text = $"Could not read games: {msg}";
                    return;
                }

                var games = t.Result;
                _pinupSettings.Games = games;
                int total = _pinupPlaylists.Count;
                int enabledCount = _pinupPlaylists.Count(p => p.Enabled);
                PinupPlaylistStatus.Text =
                    $"{total} playlist(s) loaded, {enabledCount} enabled, {games.Count} game(s) resolved.";
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void UpdatePinupPlaylistStatus()
    {
        int total = _pinupPlaylists.Count;
        int enabled = _pinupPlaylists.Count(p => p.Enabled);
        PinupPlaylistStatus.Text = total == 0
            ? "No playlists loaded."
            : $"{total} playlist(s) loaded, {enabled} enabled.";
    }

    /// <summary>
    /// Rebuilds the Active/Inactive column collections from the master playlist list,
    /// each ordered by DisplayOrder. Called after any activate/deactivate or reload.
    /// </summary>
    private void RefreshPinupColumns()
    {
        _pinupActive.Clear();
        foreach (var pl in _pinupPlaylists.Where(p => p.Enabled).OrderBy(p => p.DisplayOrder))
            _pinupActive.Add(pl);

        _pinupInactive.Clear();
        foreach (var pl in _pinupPlaylists.Where(p => !p.Enabled).OrderBy(p => p.DisplayOrder))
            _pinupInactive.Add(pl);
    }

    private void PinupActivate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.DataContext is PinupPlaylist pl)
        {
            pl.Enabled = true;
            RefreshPinupColumns();
            UpdatePinupPlaylistStatus();
            RebuildPinupGamesIfPossible();
        }
    }

    private void PinupDeactivate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.DataContext is PinupPlaylist pl)
        {
            pl.Enabled = false;
            RefreshPinupColumns();
            UpdatePinupPlaylistStatus();
            RebuildPinupGamesIfPossible();
        }
    }

    /// <summary>Rebuilds the resolved game list if a database is configured.</summary>
    private void RebuildPinupGamesIfPossible()
    {
        var dbPath = TbPopperDbPath.Text;
        if (!string.IsNullOrWhiteSpace(dbPath))
            RebuildPinupGamesAsync(dbPath);
    }


    private readonly ObservableCollection<string> _playfieldVideoFolders = new();

    private void AddVideoFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Playfield Video Folder",
            Multiselect = true
        };
        // Seed initial directory: an existing entry, else the default vPinball
        // PlayField folder when it exists on disk.
        string? seed = null;
        if (_playfieldVideoFolders.Count > 0)
        {
            var first = _playfieldVideoFolders[0];
            seed = System.IO.Path.IsPathRooted(first)
                ? first
                : System.IO.Path.Combine(AppContext.BaseDirectory, first);
        }
        else if (System.IO.Directory.Exists(AppSettings.DefaultPlayfieldVideoFolder))
        {
            seed = AppSettings.DefaultPlayfieldVideoFolder;
        }
        if (!string.IsNullOrEmpty(seed) && System.IO.Directory.Exists(seed))
            dlg.InitialDirectory = seed;

        if (dlg.ShowDialog(this) != true) return;

        foreach (var folder in dlg.FolderNames)
        {
            var stored = MakePortableDittiPath(folder);
            if (!_playfieldVideoFolders.Contains(stored))
                _playfieldVideoFolders.Add(stored);
        }
    }

    private void RemoveVideoFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.DataContext is string path)
            _playfieldVideoFolders.Remove(path);
    }

    private const int VideoFolderMaxNoLimitTick = 610;

    private void SliderVideoFolderMinDuration_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Ensure Max stays >= Min (unless Max is "No Maximum").
        if (SliderVideoFolderMaxDuration != null)
        {
            double max = SliderVideoFolderMaxDuration.Value;
            if (max < VideoFolderMaxNoLimitTick && max < e.NewValue)
                SliderVideoFolderMaxDuration.Value = Math.Min(VideoFolderMaxNoLimitTick, e.NewValue);
        }
        UpdateVideoFolderDurationLabels();
    }

    private void SliderVideoFolderMaxDuration_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Don't allow Max below Min (unless it's the "No Maximum" tick).
        if (SliderVideoFolderMinDuration != null &&
            e.NewValue < VideoFolderMaxNoLimitTick && e.NewValue < SliderVideoFolderMinDuration.Value)
        {
            SliderVideoFolderMaxDuration.Value = SliderVideoFolderMinDuration.Value;
            return; // this reassignment re-enters and updates the labels
        }
        UpdateVideoFolderDurationLabels();
    }

    private void UpdateVideoFolderDurationLabels()
    {
        if (TxtVideoFolderMinDuration != null)
            TxtVideoFolderMinDuration.Text = $"{(int)SliderVideoFolderMinDuration.Value}s";
        if (TxtVideoFolderMaxDuration != null)
        {
            TxtVideoFolderMaxDuration.Text = SliderVideoFolderMaxDuration.Value >= VideoFolderMaxNoLimitTick
                ? "No Maximum"
                : $"{(int)SliderVideoFolderMaxDuration.Value}s";
        }
    }

    private void CbVideoAudioEnabled_Changed(object sender, RoutedEventArgs e)
    {
        UpdateVideoAudioControls();
    }

    private void SliderVideoAudioVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateVideoAudioControls();
    }

    /// <summary>Updates the volume label and enables the slider only when audio is on.</summary>
    private void UpdateVideoAudioControls()
    {
        if (TxtVideoAudioVolume != null)
            TxtVideoAudioVolume.Text = $"{(int)SliderVideoAudioVolume.Value}%";
        if (SliderVideoAudioVolume != null)
            SliderVideoAudioVolume.IsEnabled = CbVideoAudioEnabled.IsChecked == true;
    }

    private void SliderPinupClipDuration_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdatePinupClipDurationLabel();
    }

    private void UpdatePinupClipDurationLabel()
    {
        if (TxtPinupClipDuration != null)
            TxtPinupClipDuration.Text = $"{(int)SliderPinupClipDuration.Value}s";
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

        // Refresh monitor info when the About tab is selected
        if (SettingsTabs.SelectedItem is System.Windows.Controls.TabItem tab &&
            tab.Header is string header && header == "ABOUT")
            RefreshMonitorInfo();
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
        _topperProxy?.ResetPosition(1, 1, 800, 300);
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
        _topperProxy?.SetDistortion(e.NewValue / 100.0);
    }

    private void SliderScreenScaling_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ScreenScalingValueText != null)
            ScreenScalingValueText.Text = $"{e.NewValue / 100.0:F2}";
        _topperProxy?.SetScreenScaling(e.NewValue / 100.0);
    }

    private void SliderMatrixZoomRate_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MatrixZoomRateValueText != null)
            MatrixZoomRateValueText.Text = $"{e.NewValue:F2}";
    }

    private void SliderMatrixMaxTrails_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MatrixMaxTrailsValueText != null)
            MatrixMaxTrailsValueText.Text = $"{(int)e.NewValue}";
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

    private void SliderLogoBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LogoBrightnessValueText != null)
            LogoBrightnessValueText.Text = $"{(int)e.NewValue}%";
    }

    private void SliderRingBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RingBrightnessValueText != null)
            RingBrightnessValueText.Text = $"{(int)e.NewValue}%";
    }

    private void SliderTopperLogoBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TopperLogoBrightnessValueText != null)
            TopperLogoBrightnessValueText.Text = $"{(int)e.NewValue}%";
    }

    private void SliderTopperLogoRings_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // No live preview needed; value is read on save
    }

    private void SliderTopperRingBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TopperRingBrightnessValueText != null)
            TopperRingBrightnessValueText.Text = $"{(int)e.NewValue}%";
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
        string text = val == 10 ? "Default" : $"{val * 10}%";
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

    private void CbMandelbrotUseScreenRate_Changed(object sender, RoutedEventArgs e)
    {
        UpdateMandelbrotTickIntervalVisibility();
    }

    private void UpdateMandelbrotTickIntervalVisibility()
    {
        if (SliderMandelbrotTickInterval == null) return;
        bool useScreen = CbMandelbrotUseScreenRate.IsChecked == true;
        SliderMandelbrotTickInterval.IsEnabled = !useScreen;

        if (SliderMandelbrotTickInterval.Parent is FrameworkElement sliderRow)
            sliderRow.Visibility = useScreen ? Visibility.Collapsed : Visibility.Visible;

        if (TxtMandelbrotTickInterval != null)
            TxtMandelbrotTickInterval.Text = useScreen ? "(auto)" : ((int)SliderMandelbrotTickInterval.Value == 0 ? "Unlimited" : $"{(int)SliderMandelbrotTickInterval.Value} ms");
    }

    private void SliderMandelbrotTickInterval_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtMandelbrotTickInterval != null && CbMandelbrotUseScreenRate?.IsChecked != true)
            TxtMandelbrotTickInterval.Text = (int)e.NewValue == 0 ? "Unlimited" : $"{(int)e.NewValue} ms";
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
        UpdateFerrofluidTuningVisibility();
        UpdateMatrixTuningVisibility();
        UpdateGameOfLifeTuningVisibility();
        UpdateGravityTuningVisibility();
        UpdateClockTuningVisibility();
        UpdateBlobCountSliderStates();
        UpdateRandomTuningVisibility();
    }

    /// <summary>
    /// Hides blob-count and blob-size sliders and their labels for patterns that don't use blobs (ProjectM, Mandelbrot).
    /// </summary>
    private void UpdateBlobCountSliderStates()
    {
        SetSlidersForPattern(CbBlobPatternPlayfield, PanelBlobCountPlayfield, PanelBlobSizePlayfield, PanelSpeedPlayfield);
        SetSlidersForPattern(CbBlobPatternBackglass, PanelBlobCountBackglass, PanelBlobSizeBackglass, PanelSpeedBackglass);
        SetSlidersForPattern(CbBlobPatternTopper, PanelBlobCountTopper, PanelBlobSizeTopper, PanelSpeedTopper);
        SetSlidersForPattern(CbBlobPatternDmd, PanelBlobCountDmd, PanelBlobSizeDmd, PanelSpeedDmd);

        static void SetSlidersForPattern(System.Windows.Controls.ComboBox? cb,
            FrameworkElement? countPanel, FrameworkElement? sizePanel, FrameworkElement? speedPanel)
        {
            if (cb == null) return;
            var name = cb.SelectedItem as string ?? "";
            bool hideAll = name == "ProjectM" || name == "Mandelbrot"
                || name == "Clock";
            bool hideSize = hideAll || name == "Game of Life";
            bool hideSpeed = name == "Game of Life" || name == "Clock"
                || name == "Ferrofluid" || name == "ProjectM";
            if (countPanel != null) countPanel.Visibility = hideAll ? Visibility.Hidden : Visibility.Visible;
            if (sizePanel != null) sizePanel.Visibility = hideSize ? Visibility.Hidden : Visibility.Visible;
            if (speedPanel != null) speedPanel.Visibility = hideSpeed ? Visibility.Hidden : Visibility.Visible;
        }
    }

    private void SliderPerScreenIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not System.Windows.Controls.Slider slider) return;
        var label = slider.Name switch
        {
            nameof(SliderIntensityPlayfield) => TxtIntensityPlayfield,
            nameof(SliderIntensityBackglass) => TxtIntensityBackglass,
            nameof(SliderIntensityTopper) => TxtIntensityTopper,
            nameof(SliderIntensityDmd) => TxtIntensityDmd,
            _ => null
        };
        if (label != null) label.Text = $"{(int)e.NewValue}%";

        double intensity = e.NewValue / 100.0;
        switch (slider.Name)
        {
            case nameof(SliderIntensityPlayfield):
                _playfieldProxy?.SetScreensaverSettings(intensity, SliderSpeedPlayfield.Value);
                break;
            case nameof(SliderIntensityBackglass):
                _backglassProxy?.SetScreensaverSettings(intensity, SliderSpeedBackglass.Value);
                break;
            case nameof(SliderIntensityTopper):
                _topperProxy?.SetScreensaverSettings(intensity, SliderSpeedTopper.Value);
                break;
        }
    }

    private void SliderPerScreenSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not System.Windows.Controls.Slider slider) return;
        var label = slider.Name switch
        {
            nameof(SliderSpeedPlayfield) => TxtSpeedPlayfield,
            nameof(SliderSpeedBackglass) => TxtSpeedBackglass,
            nameof(SliderSpeedTopper) => TxtSpeedTopper,
            nameof(SliderSpeedDmd) => TxtSpeedDmd,
            _ => null
        };
        if (label != null) label.Text = $"{e.NewValue:F1}×";

        double speed = e.NewValue;
        switch (slider.Name)
        {
            case nameof(SliderSpeedPlayfield):
                _playfieldProxy?.SetScreensaverSettings(SliderIntensityPlayfield.Value / 100.0, speed);
                break;
            case nameof(SliderSpeedBackglass):
                _backglassProxy?.SetScreensaverSettings(SliderIntensityBackglass.Value / 100.0, speed);
                break;
            case nameof(SliderSpeedTopper):
                _topperProxy?.SetScreensaverSettings(SliderIntensityTopper.Value / 100.0, speed);
                break;
        }
    }

    private void UpdateRandomTuningVisibility()
    {
        if (PanelRandomTuning == null) return;

        bool anyRandom = false;
        foreach (var cb in new[] { CbBlobPatternPlayfield, CbBlobPatternBackglass, CbBlobPatternTopper, CbBlobPatternDmd })
        {
            if (cb?.SelectedItem is string name && (name == "Random" || name == "Random Per Song"))
            {
                anyRandom = true;
                break;
            }
        }

        PanelRandomTuning.Visibility = anyRandom ? Visibility.Visible : Visibility.Collapsed;
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

    private void UpdateFerrofluidTuningVisibility()
    {
        if (PanelFerrofluidTuning == null) return;

        bool anyFerrofluid = false;
        foreach (var cb in new[] { CbBlobPatternPlayfield, CbBlobPatternBackglass, CbBlobPatternTopper, CbBlobPatternDmd })
        {
            if (cb?.SelectedItem is string name && name == "Ferrofluid")
            {
                anyFerrofluid = true;
                break;
            }
        }

        PanelFerrofluidTuning.Visibility = anyFerrofluid ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateMatrixTuningVisibility()
    {
        if (PanelMatrixTuning == null) return;

        bool anyMatrix = false;
        foreach (var cb in new[] { CbBlobPatternPlayfield, CbBlobPatternBackglass, CbBlobPatternTopper, CbBlobPatternDmd })
        {
            if (cb?.SelectedItem is string name && name == "Matrix")
            {
                anyMatrix = true;
                break;
            }
        }

        PanelMatrixTuning.Visibility = anyMatrix ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateGameOfLifeTuningVisibility()
    {
        if (PanelGameOfLifeTuning == null) return;

        bool anyGameOfLife = false;
        foreach (var cb in new[] { CbBlobPatternPlayfield, CbBlobPatternBackglass, CbBlobPatternTopper, CbBlobPatternDmd })
        {
            if (cb?.SelectedItem is string name && name == "Game of Life")
            {
                anyGameOfLife = true;
                break;
            }
        }

        PanelGameOfLifeTuning.Visibility = anyGameOfLife ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateGravityTuningVisibility()
    {
        if (PanelGravityTuning == null) return;

        bool anyGravity = false;
        foreach (var cb in new[] { CbBlobPatternPlayfield, CbBlobPatternBackglass, CbBlobPatternTopper, CbBlobPatternDmd })
        {
            if (cb?.SelectedItem is string name && name == "Gravity")
            {
                anyGravity = true;
                break;
            }
        }

        PanelGravityTuning.Visibility = anyGravity ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateClockTuningVisibility()
    {
        if (PanelClockTuning == null) return;

        bool anyClock = false;
        foreach (var cb in new[] { CbBlobPatternPlayfield, CbBlobPatternBackglass, CbBlobPatternTopper, CbBlobPatternDmd })
        {
            if (cb?.SelectedItem is string name && name == "Clock")
            {
                anyClock = true;
                break;
            }
        }

        PanelClockTuning.Visibility = anyClock ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CbClockMode_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // No-op; tracked via change-detection.
    }

    private void SliderClockBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtClockBrightness != null)
            TxtClockBrightness.Text = $"{(int)e.NewValue}%";
    }

    private void SliderClockDigitalSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtClockDigitalSize != null)
            TxtClockDigitalSize.Text = $"{(int)e.NewValue}%";
    }

    private void SliderClockHourFormat_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtClockHourFormat != null)
            TxtClockHourFormat.Text = e.NewValue >= 0.5 ? "24-hour" : "12-hour";
    }

    private void CbClockAnalogStyle_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // No-op; tracked via change-detection.
    }

    private void SliderClockAnalogSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtClockAnalogSize != null)
            TxtClockAnalogSize.Text = AnalogSizeLabel((int)e.NewValue);
    }

    private static string AnalogSizeLabel(int v) => v switch
    {
        0 => "Smallest",
        1 => "Small",
        3 => "Large",
        4 => "Largest",
        _ => "Medium",
    };

    private void SliderGravityG_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtGravityG != null)
            TxtGravityG.Text = $"{(int)e.NewValue}";
    }

    private void CbGravityCameraRoam_Changed(object sender, RoutedEventArgs e)
    {
        // No-op; tracked via change-detection.
    }

    private void SliderGravityOrbitRepulsion_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtGravityOrbitRepulsion != null)
            TxtGravityOrbitRepulsion.Text = $"{e.NewValue:F1}";
    }

    private void SliderGravityCentralGravity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtGravityCentralGravity != null)
            TxtGravityCentralGravity.Text = $"{(int)e.NewValue}";
    }

    private void SliderGravityOrbitalPerturbation_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtGravityOrbitalPerturbation != null)
            TxtGravityOrbitalPerturbation.Text = $"{e.NewValue:F1}";
    }

    private void CbGravityRestartOnTrackChange_Changed(object sender, RoutedEventArgs e)
    {
        // No-op; tracked via change-detection.
    }

    private void SliderGravityBlobMultiplier_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtGravityBlobMultiplier != null)
            TxtGravityBlobMultiplier.Text = $"{e.NewValue:F1}x";
    }

    private void SliderGravitySupernovaMass_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtGravitySupernovaMass != null)
            TxtGravitySupernovaMass.Text = e.NewValue < 10 ? "Off" : $"{(int)e.NewValue}px";
    }

    private void SliderGravityDensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtGravityDensity != null)
            TxtGravityDensity.Text = (int)e.NewValue switch { 0 => "Low", 2 => "High", _ => "Medium" };
    }

    /// <summary>
    /// Toggle visibility of Conway-only controls (anti-stagnation, color mode
    /// chooser) based on the selected rules engine. Non-Conway rules force
    /// EraBanded color mode at runtime, so their color chooser is hidden.
    /// </summary>
    private void UpdateGameOfLifeRulesVisibility()
    {
        // Rules engine is temporarily forced to Conway (see docs/GameOfLifeRules.md),
        // so the Conway-only controls are always visible regardless of any
        // persisted GameOfLifeRulesEngine value from a previous session.
        if (PanelGameOfLifeConwayOnly != null)
            PanelGameOfLifeConwayOnly.Visibility = Visibility.Visible;
        if (PanelGameOfLifeColorMode != null)
            PanelGameOfLifeColorMode.Visibility = Visibility.Visible;
    }

    private void CbGameOfLifeRulesEngine_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateGameOfLifeRulesVisibility();
    }

    private void CbGameOfLifeRulePreset_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressBsCheckboxSync) return;
        int idx = CbGameOfLifeRulePreset.SelectedIndex;
        if (idx < 0 || idx >= BsPresets.Length) return;
        var (_, rule) = BsPresets[idx];
        if (!string.IsNullOrEmpty(rule))
        {
            _suppressBsCheckboxSync = true;
            SetBsCheckboxes(rule);
            _suppressBsCheckboxSync = false;

            // Auto-select recommended seed spread for known presets
            if (CbGameOfLifeSeedSpread != null)
            {
                CbGameOfLifeSeedSpread.SelectedIndex = rule.ToUpperInvariant() switch
                {
                    "B2/S" => 1,                  // Seeds: scattered
                    "B1357/S1357" => 1,            // Replicator: scattered
                    "B3/S45678" => 2,              // Coral: full
                    "B3678/S34678" => 2,           // Day & Night: full
                    "B35678/S5678" => 2,           // Diamoeba: full
                    "B3/S012345678" => 0,          // Life Without Death: clustered
                    _ => 0,                        // Conway, HighLife: clustered
                };
            }
        }
        UpdateRuleLabel();
    }

    private void BirthSurvivalCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressBsCheckboxSync) return;
        // When user manually changes a checkbox, switch preset to "Custom"
        // if the current checkboxes don't match any preset.
        string current = BuildRuleStringFromCheckboxes();
        _suppressBsCheckboxSync = true;
        int matchIdx = -1;
        for (int i = 0; i < BsPresets.Length - 1; i++) // skip "Custom" entry
        {
            if (BsPresets[i].Rule.Equals(current, StringComparison.OrdinalIgnoreCase))
            { matchIdx = i; break; }
        }
        CbGameOfLifeRulePreset.SelectedIndex = matchIdx >= 0 ? matchIdx : BsPresets.Length - 1;
        _suppressBsCheckboxSync = false;
        UpdateRuleLabel();
    }

    private void LoadBsCheckboxesFromRule(string? rule)
    {
        rule ??= "B3/S23";
        _suppressBsCheckboxSync = true;
        SetBsCheckboxes(rule);
        // Find matching preset
        int matchIdx = BsPresets.Length - 1; // default to Custom
        for (int i = 0; i < BsPresets.Length - 1; i++)
        {
            if (BsPresets[i].Rule.Equals(rule, StringComparison.OrdinalIgnoreCase))
            { matchIdx = i; break; }
        }
        CbGameOfLifeRulePreset.SelectedIndex = matchIdx;
        _suppressBsCheckboxSync = false;
        UpdateRuleLabel();
    }

    private void SetBsCheckboxes(string rule)
    {
        var (b, s) = GameOfLifePattern.ParseRule(rule);
        System.Windows.Controls.CheckBox[] birthCbs = [CbBirth0, CbBirth1, CbBirth2, CbBirth3, CbBirth4, CbBirth5, CbBirth6, CbBirth7, CbBirth8];
        System.Windows.Controls.CheckBox[] survCbs = [CbSurvive0, CbSurvive1, CbSurvive2, CbSurvive3, CbSurvive4, CbSurvive5, CbSurvive6, CbSurvive7, CbSurvive8];
        for (int i = 0; i <= 8; i++)
        {
            birthCbs[i].IsChecked = (b & (1 << i)) != 0;
            survCbs[i].IsChecked = (s & (1 << i)) != 0;
        }
    }

    private string BuildRuleStringFromCheckboxes()
    {
        System.Windows.Controls.CheckBox[] birthCbs = [CbBirth0, CbBirth1, CbBirth2, CbBirth3, CbBirth4, CbBirth5, CbBirth6, CbBirth7, CbBirth8];
        System.Windows.Controls.CheckBox[] survCbs = [CbSurvive0, CbSurvive1, CbSurvive2, CbSurvive3, CbSurvive4, CbSurvive5, CbSurvive6, CbSurvive7, CbSurvive8];
        int b = 0, s = 0;
        for (int i = 0; i <= 8; i++)
        {
            if (birthCbs[i].IsChecked == true) b |= 1 << i;
            if (survCbs[i].IsChecked == true) s |= 1 << i;
        }
        return GameOfLifePattern.FormatRule(b, s);
    }

    private void UpdateRuleLabel()
    {
        if (TxtCurrentRule != null)
            TxtCurrentRule.Text = BuildRuleStringFromCheckboxes();
    }

    private void SliderGameOfLifeEraBandedHueSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtGameOfLifeEraBandedHueSpeed != null)
            TxtGameOfLifeEraBandedHueSpeed.Text = $"{e.NewValue:F1}×";
    }

    private void SliderGameOfLifeCellSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtGameOfLifeCellSize != null)
            TxtGameOfLifeCellSize.Text = $"{(int)e.NewValue} px";
    }

    private void CbGameOfLifeUseScreenRate_Changed(object sender, RoutedEventArgs e)
    {
        UpdateGameOfLifeTickIntervalVisibility();
    }

    private void UpdateGameOfLifeTickIntervalVisibility()
    {
        if (SliderGameOfLifeTickInterval == null) return;
        bool useScreen = CbGameOfLifeUseScreenRate.IsChecked == true;
        SliderGameOfLifeTickInterval.IsEnabled = !useScreen;

        // Hide the entire slider row when using screen rate
        if (SliderGameOfLifeTickInterval.Parent is FrameworkElement sliderRow)
            sliderRow.Visibility = useScreen ? Visibility.Collapsed : Visibility.Visible;

        if (TxtGameOfLifeTickInterval != null)
            TxtGameOfLifeTickInterval.Text = useScreen ? "(auto)" : $"{(int)SliderGameOfLifeTickInterval.Value} ms";
    }

    private void SliderGameOfLifeTickInterval_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtGameOfLifeTickInterval != null && CbGameOfLifeUseScreenRate?.IsChecked != true)
            TxtGameOfLifeTickInterval.Text = $"{(int)e.NewValue} ms";
    }

    private void SliderGameOfLifeFadeGenerations_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtGameOfLifeFadeGenerations != null)
            TxtGameOfLifeFadeGenerations.Text = (int)e.NewValue == 0 ? "Off" : $"{(int)e.NewValue}";
    }

    private void SliderGameOfLifeHeatBoost_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtGameOfLifeHeatBoost != null)
            TxtGameOfLifeHeatBoost.Text = (int)e.NewValue == 0 ? "Off" : $"{(int)e.NewValue}";
    }

    private void SliderGameOfLifeBirthGenerations_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtGameOfLifeBirthGenerations != null)
            TxtGameOfLifeBirthGenerations.Text = (int)e.NewValue == 0 ? "Off" : $"{(int)e.NewValue}";
    }

    private void CbGameOfLifeBloom_Changed(object sender, RoutedEventArgs e)
    {
        UpdateGameOfLifeBloomVisibility();
    }

    private void SliderGameOfLifeBloomIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtGameOfLifeBloomIntensity != null)
            TxtGameOfLifeBloomIntensity.Text = $"{(int)e.NewValue * 10}%";
    }

    private void SliderGameOfLifeBloomRadius_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtGameOfLifeBloomRadius != null)
            TxtGameOfLifeBloomRadius.Text = $"{(int)e.NewValue}×";
    }

    /// <summary>
    /// Show the Bloom Intensity / Radius rows only when Bloom is enabled — they
    /// have no effect otherwise, so hiding reduces visual clutter in the panel.
    /// </summary>
    private void UpdateGameOfLifeBloomVisibility()
    {
        bool on = CbGameOfLifeBloom?.IsChecked == true;
        if (PanelGameOfLifeBloomIntensity != null)
            PanelGameOfLifeBloomIntensity.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (PanelGameOfLifeBloomRadius != null)
            PanelGameOfLifeBloomRadius.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SliderGameOfLifeDensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtGameOfLifeDensity != null)
            TxtGameOfLifeDensity.Text = $"{(int)e.NewValue}";
    }

    private void CbGameOfLifeCameraRoam_Changed(object sender, RoutedEventArgs e)
    {
        UpdateGameOfLifeCameraVisibility();
    }

    private void CbGameOfLifeRestartOnTrackChange_Changed(object sender, RoutedEventArgs e)
    {
        // No-op; tracked via originals/change-detection.
    }

    private void CbGameOfLifeAntiStagnation_Changed(object sender, RoutedEventArgs e)
    {
        // No-op; tracked via originals/change-detection. Excluded from
        // GameOfLifeSettingsChanged on purpose so toggling it live doesn't
        // reseed the field — the new flag takes effect on the next tick.
    }

    private void SliderGameOfLifeAntiStagnationIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtGameOfLifeAntiStagnationIntensity != null)
            TxtGameOfLifeAntiStagnationIntensity.Text = $"{(int)e.NewValue}";
    }

    private void SliderGameOfLifeCameraMaxZoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtGameOfLifeCameraMaxZoom != null)
            TxtGameOfLifeCameraMaxZoom.Text = $"{e.NewValue:F1}x";
    }

    private void SliderGameOfLifeCameraOverscan_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtGameOfLifeCameraOverscan != null)
            TxtGameOfLifeCameraOverscan.Text = $"{(int)e.NewValue}%";
    }

    private void SliderGameOfLifeCameraSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtGameOfLifeCameraSpeed != null)
            TxtGameOfLifeCameraSpeed.Text = FormatCameraSpeed(e.NewValue);
    }

    private static string FormatCameraSpeed(double v) => v switch
    {
        <= 0.25 => $"{v:F1}x (glacial)",
        <= 0.75 => $"{v:F1}x (slow)",
        <  1.25 => $"{v:F1}x (default)",
        <  2.0  => $"{v:F1}x (brisk)",
        _       => $"{v:F1}x (fast)",
    };

    private void CbGameOfLifeScalingMode_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { }

    private void CbGameOfLifeSeedSpread_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { }

    private void CbGameOfLifeColorMode_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateSeedColorVisibility();
    }

    private void SeedColorCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        // No-op; tracked via change-detection on save.
    }

    private void CbGameOfLifeHueSpread_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // No-op; tracked via change-detection on save.
    }

    private void UpdateSeedColorVisibility()
    {
        if (PanelGameOfLifeSeedColors == null) return;
        // Seed color restriction only applies to Genetic modes (index 0 and 1), not EraBanded (2)
        bool genetic = CbGameOfLifeColorMode.SelectedIndex < 2;
        PanelGameOfLifeSeedColors.Visibility = genetic ? Visibility.Visible : Visibility.Collapsed;
        if (PanelGameOfLifeHueSpread != null)
            PanelGameOfLifeHueSpread.Visibility = genetic ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadSeedColorCheckboxes(int mask)
    {
        if (mask == 0) mask = 0x7F; // treat 0 as all-enabled
        System.Windows.Controls.CheckBox[] cbs = [CbSeedRed, CbSeedOrange, CbSeedYellow, CbSeedGreen, CbSeedBlue, CbSeedIndigo, CbSeedViolet];
        for (int i = 0; i < 7; i++)
            cbs[i].IsChecked = (mask & (1 << i)) != 0;
    }

    private int BuildSeedColorMask()
    {
        System.Windows.Controls.CheckBox[] cbs = [CbSeedRed, CbSeedOrange, CbSeedYellow, CbSeedGreen, CbSeedBlue, CbSeedIndigo, CbSeedViolet];
        int mask = 0;
        for (int i = 0; i < 7; i++)
            if (cbs[i].IsChecked == true) mask |= 1 << i;
        return mask == 0 ? 0x7F : mask; // all unchecked = all enabled
    }

    private void UpdateGameOfLifeCameraVisibility()
    {
        if (PanelGameOfLifeCameraSettings != null)
            PanelGameOfLifeCameraSettings.Visibility = CbGameOfLifeCameraRoam.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SliderFerrofluidCoreGravity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtFerrofluidCoreGravity != null)
            TxtFerrofluidCoreGravity.Text = $"{(int)e.NewValue}";
    }

    private void SliderFerrofluidMutualAttraction_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtFerrofluidMutualAttraction != null)
            TxtFerrofluidMutualAttraction.Text = $"{(int)e.NewValue}";
    }

    private void SliderFerrofluidDamping_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtFerrofluidDamping != null)
            TxtFerrofluidDamping.Text = $"{(int)e.NewValue}%";
    }

    private void SliderFerrofluidExplosionForce_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtFerrofluidExplosionForce != null)
            TxtFerrofluidExplosionForce.Text = $"{(int)e.NewValue}";
    }

    private void SliderFerrofluidExplosionDuration_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtFerrofluidExplosionDuration != null)
            TxtFerrofluidExplosionDuration.Text = $"{e.NewValue / 10.0:F1}s";
    }

    private void SliderFerrofluidBristleForce_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtFerrofluidBristleForce != null)
            TxtFerrofluidBristleForce.Text = $"{(int)e.NewValue}";
    }

    private void SliderFerrofluidMaxSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtFerrofluidMaxSpeed != null)
            TxtFerrofluidMaxSpeed.Text = $"{(int)e.NewValue}";
    }

    private void SliderFerrofluidExplosionBassThreshold_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtFerrofluidExplosionBassThreshold != null)
            TxtFerrofluidExplosionBassThreshold.Text = $"{(int)e.NewValue}%";
    }

    private void SliderFerrofluidBristleTrebleThreshold_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtFerrofluidBristleTrebleThreshold != null)
            TxtFerrofluidBristleTrebleThreshold.Text = $"{(int)e.NewValue}%";
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
        else if (RbVideoFolders.IsChecked == true)
            _settings.PlayfieldDisplayMode = PlayfieldMode.VideoFolders;
        else if (RbPinupPlaylist.IsChecked == true)
            _settings.PlayfieldDisplayMode = PlayfieldMode.PinupPlaylist;
        else
            _settings.PlayfieldDisplayMode = PlayfieldMode.StaticImage;

        _settings.PlayfieldPulseDominantBlobs = CbPulseDominantBlobs.IsChecked == true;
        _settings.OledSleepDefeatSeconds = CbOledSleepDefeat.SelectedIndex * 10;
        _settings.OledSleepDefeatDurationSeconds = CbOledSleepDuration.SelectedIndex + 1;
        _settings.OledSleepDefeatIntensity = (int)SliderOledIntensity.Value;
        SelectedPlayfieldMode = _settings.PlayfieldDisplayMode;
        _settings.PlayfieldStaticImagePath = TbStaticImagePath.Text;
        _settings.PlayfieldVideoPath = TbVideoPath.Text;
        _settings.PlayfieldVideoFolders = new List<string>(_playfieldVideoFolders);
        _settings.PlayfieldVideoFolderPlayMode = CbVideoFolderPlayMode.SelectedIndex == 1
            ? VideoFolderPlayMode.MostRecentFirst
            : VideoFolderPlayMode.Random;
        _settings.PlayfieldVideoFolderMinDurationSeconds = (int)SliderVideoFolderMinDuration.Value;
        // The 610 tick means "No Maximum" -> store 0.
        _settings.PlayfieldVideoFolderMaxDurationSeconds =
            SliderVideoFolderMaxDuration.Value >= VideoFolderMaxNoLimitTick
                ? 0
                : (int)SliderVideoFolderMaxDuration.Value;

        _settings.PinupClipDurationSeconds = (int)SliderPinupClipDuration.Value;

        // Pinup window→media-folder mapping.
        _settings.PinupFolderMap = _pinupFolderMapRows
            .ToDictionary(r => r.WindowName, r => r.Folder, StringComparer.Ordinal);

        _settings.PlayfieldVideoAudioEnabled = CbVideoAudioEnabled.IsChecked == true;
        _settings.PlayfieldVideoAudioVolume = (int)SliderVideoAudioVolume.Value;

        // Persist Pinup Popper integration settings to pinup_integration.json (separate
        // file to avoid bloating settings.json on large Pinup installs).
        _pinupSettings.PopperDbPath = TbPopperDbPath.Text;
        _pinupSettings.Playlists = new List<PinupPlaylist>(_pinupPlaylists);
        _pinupSettings.Save();

        _settings.ShowVideoInfo = CbShowVideoInfo.IsChecked == true;
        _settings.ResizableWindows = CbResizableWindows.IsChecked == true;
        _settings.SetCursorOnLaunch = CbSetCursorOnLaunch.IsChecked == true;
        _settings.MoveCursorToSettings = CbMoveCursorToSettings.IsChecked == true;
        _settings.CheckWindowsOnStartup = CbCheckWindowsOnStartup.IsChecked == true;
        _settings.ShowBackglass = CbShowBackglass.IsChecked == true;
        _settings.ShowPlayfield = CbShowPlayfield.IsChecked == true;
        _settings.ShowTopper = CbShowTopper.IsChecked == true;
        _settings.AutoPlayQueueOnStart = CbAutoPlayQueue.IsChecked == true;
        _settings.StartupDittiPaths = new List<string>(_startupDittiPaths);
        _settings.StartupDittiPath = ""; // legacy field cleared; data lives in StartupDittiPaths
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
        _settings.LogoColorMode = RbLogoColorReactive.IsChecked == true ? LogoColorMode.Reactive
            : RbLogoColorMorph.IsChecked == true ? LogoColorMode.SlowMorph
            : LogoColorMode.Off;
        _settings.BackglassAudioOnly = CbBackglassAudioOnly.IsChecked == true;

        // ── Backglass ambient content (independent of the playfield) ──
        if (RbBgBlank.IsChecked == true)
            _settings.BackglassDisplayMode = PlayfieldMode.Blank;
        else if (RbBgScreensaver.IsChecked == true)
            _settings.BackglassDisplayMode = PlayfieldMode.Screensaver;
        else if (RbBgVideo.IsChecked == true)
            _settings.BackglassDisplayMode = PlayfieldMode.Video;
        else if (RbBgVideoFolders.IsChecked == true)
            _settings.BackglassDisplayMode = PlayfieldMode.VideoFolders;
        else if (RbBgPinupPlaylist.IsChecked == true)
            _settings.BackglassDisplayMode = PlayfieldMode.PinupPlaylist;
        else
            _settings.BackglassDisplayMode = PlayfieldMode.StaticImage;
        SelectedBackglassMode = _settings.BackglassDisplayMode;

        _settings.BackglassStaticImagePath = TbBgStaticImagePath.Text;
        _settings.BackglassVideoPath = TbBgVideoPath.Text;
        _settings.BackglassVideoFolders = new List<string>(_backglassVideoFolders);
        _settings.BackglassVideoFolderPlayMode = CbBgVideoFolderPlayMode.SelectedIndex == 1
            ? VideoFolderPlayMode.MostRecentFirst
            : VideoFolderPlayMode.Random;
        _settings.BackglassVideoFolderMinDurationSeconds = (int)SliderBgVideoFolderMinDuration.Value;
        _settings.BackglassVideoFolderMaxDurationSeconds =
            SliderBgVideoFolderMaxDuration.Value >= VideoFolderMaxNoLimitTick
                ? 0
                : (int)SliderBgVideoFolderMaxDuration.Value;

        _settings.BackglassVideoAudioVolume = (int)SliderBgVideoAudioVolume.Value;

        // ── Topper ambient content (independent of the playfield/backglass) ──
        if (RbTpBlank.IsChecked == true)
            _settings.TopperDisplayMode = PlayfieldMode.Blank;
        else if (RbTpScreensaver.IsChecked == true)
            _settings.TopperDisplayMode = PlayfieldMode.Screensaver;
        else if (RbTpVideo.IsChecked == true)
            _settings.TopperDisplayMode = PlayfieldMode.Video;
        else if (RbTpVideoFolders.IsChecked == true)
            _settings.TopperDisplayMode = PlayfieldMode.VideoFolders;
        else if (RbTpPinupPlaylist.IsChecked == true)
            _settings.TopperDisplayMode = PlayfieldMode.PinupPlaylist;
        else if (RbTpStatic.IsChecked == true)
            _settings.TopperDisplayMode = PlayfieldMode.StaticImage;
        else
            _settings.TopperDisplayMode = PlayfieldMode.Screensaver;
        SelectedTopperMode = _settings.TopperDisplayMode;

        _settings.TopperStaticImagePath = TbTpStaticImagePath.Text;
        _settings.TopperVideoPath = TbTpVideoPath.Text;
        _settings.TopperVideoFolders = new List<string>(_topperVideoFolders);
        _settings.TopperVideoFolderPlayMode = CbTpVideoFolderPlayMode.SelectedIndex == 1
            ? VideoFolderPlayMode.MostRecentFirst
            : VideoFolderPlayMode.Random;
        _settings.TopperVideoFolderMinDurationSeconds = (int)SliderTpVideoFolderMinDuration.Value;
        _settings.TopperVideoFolderMaxDurationSeconds =
            SliderTpVideoFolderMaxDuration.Value >= VideoFolderMaxNoLimitTick
                ? 0
                : (int)SliderTpVideoFolderMaxDuration.Value;
        _settings.TopperVideoAudioEnabled = CbTpVideoAudioEnabled.IsChecked == true;
        _settings.TopperVideoAudioVolume = (int)SliderTpVideoAudioVolume.Value;

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
        _settings.DmdIconStyle = (IconStyle)CbIconStyle.SelectedIndex;
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
        _settings.DmdNowPlayingAreaSizeModifier = Math.Clamp((int)SliderNowPlayingAreaSize.Value, -4, 12);
        _settings.DmdGenreIconSizeModifier = Math.Clamp((int)SliderGenreIconSize.Value, -12, 24);
        _settings.DmdGenreIconSpacingModifier = Math.Clamp((int)SliderGenreIconSpacing.Value, -8, 8);
        _settings.DmdGenreIconPaddingModifier = Math.Clamp((int)SliderGenreIconPadding.Value, -8, 8);
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

        // Assign sequential SortOrder based on current list position
        for (int i = 0; i < _categoryVisibilityItems.Count; i++)
            _categoryVisibilityItems[i].SortOrder = i;

        // Persist category changes (icon, search term, visibility, sort order) to categories.json
        GenreCategoryStore.SaveInBackground(_categoryVisibilityItems
            .Where(i => !i.IsPlaylist)
            .Select(i => new GenreCategoryEntry
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
            PlexInstanceId = i.PlexInstanceId,
            PlexHubsEnabled = i.PlexHubsEnabled,
            PlexPlaylistsEnabled = i.PlexPlaylistsEnabled,
            SourceInstanceId = i.SourceInstanceId,
            SourceCategoryId = i.SourceCategoryId,
            SourceTypeId = i.SourceTypeId,
            SortOrder = i.SortOrder
        }).ToList());

        // Persist playlist sort orders (and icon/name edits)
        if (_playlistManager != null)
        {
            foreach (var item in _categoryVisibilityItems.Where(i => i.IsPlaylist))
            {
                var pl = _playlistManager.Playlists.FirstOrDefault(p => p.Id == item.PlaylistId);
                if (pl != null)
                {
                    pl.SortOrder = item.SortOrder;
                    pl.Icon = item.Icon;
                    pl.Name = item.Name;
                }
            }
            _playlistManager.Save();
        }
        _LogStep("GenreCategoryStore.Save");
        _settings.ShowStatusText = CbShowStatusText.IsChecked == true;
        var cursorTimeoutValues = new[] { -1, 0, 5, 10, 15, 30, 45, 60, 120, 180, 240, 300, 360, 420, 480, 540, 600 };
        _settings.HideCursorTimeoutSeconds = CbHideCursorTimeout.SelectedIndex >= 0 && CbHideCursorTimeout.SelectedIndex < cursorTimeoutValues.Length
            ? cursorTimeoutValues[CbHideCursorTimeout.SelectedIndex] : 15;
        _settings.CacheEnabled = CbCacheEnabled.IsChecked == true;
        _settings.PreemptiveCache = CbPreemptiveCache.IsChecked == true;
        _settings.PurgeCacheOnShutdown = CbPurgeCacheOnShutdown.IsChecked == true;
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
        // Harvest the editable Plug-ins tab into settings.PluginInstances (the plug-in path's config).
        HarvestPluginSourcesTab();
        if (CbResultColumns.SelectedItem is int cols)
            _settings.ResultColumns = cols;
        _settings.ResultFontSizeModifier = Math.Clamp((int)SliderResultFontSize.Value, -12, 12);
        _settings.PlayfieldIntensity = SliderIntensityPlayfield.Value / 100.0;
        _settings.PlayfieldSpeed = SliderSpeedPlayfield.Value;
        _settings.BackglassIntensity = SliderIntensityBackglass.Value / 100.0;
        _settings.BackglassSpeed = SliderSpeedBackglass.Value;
        _settings.TopperIntensity = SliderIntensityTopper.Value / 100.0;
        _settings.TopperSpeed = SliderSpeedTopper.Value;
        _settings.DmdIntensity = SliderIntensityDmd.Value / 100.0;
        _settings.DmdSpeed = SliderSpeedDmd.Value;
        _settings.TitleText = TbTitleText.Text;
        _settings.LogoText = TbLogoText.Text;
        _settings.LogoBehindVisuals = SliderLogoBehind.Value == 0;
        _settings.LogoSpin = CbLogoSpin.IsChecked == true;
        _settings.LogoShadow = CbLogoShadow.IsChecked == true;
        _settings.LogoRings = (LogoRingsMode)(int)SliderLogoRings.Value;
        _settings.LogoRingsBrightness = (int)SliderRingBrightness.Value;
        _settings.LogoBrightness = (int)SliderLogoBrightness.Value;
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
                BlobPattern.FerrofluidCluster => "Ferrofluid",
                BlobPattern.GameOfLife => "Game of Life",
                BlobPattern.Clock => "Clock",
                BlobPattern.RandomPerSong => "Random Per Song",
                _ => p.ToString()
            })
            .ToList();
        _settings.PlayfieldBlobPattern = CbBlobPatternPlayfield.SelectedIndex >= 0 ? blobPatternsSorted[CbBlobPatternPlayfield.SelectedIndex] : BlobPattern.Random;
        _settings.PlayfieldBlobCount = (int)SliderBlobCountPlayfield.Value;
        _settings.PlayfieldBlobSizeOffset = (int)SliderBlobSizePlayfield.Value;
        _settings.PlayfieldRotation = CbPlayfieldRotation.SelectedIndex switch { 1 => 90, 2 => 180, 3 => 270, _ => 0 };
        _settings.PlayfieldApplyOrientationToVideos = CbApplyOrientationToVideos.IsChecked == true;
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
        _settings.MandelbrotUseScreenRate = CbMandelbrotUseScreenRate.IsChecked == true;
        _settings.MandelbrotTickIntervalMs = (int)SliderMandelbrotTickInterval.Value;
        _settings.MandelbrotRenderScale = SliderMandelbrotRenderScale.Value / 100.0;
        _settings.MandelbrotPerturbation = SliderMandelbrotPerturbation.Value / 100.0;
        _settings.MandelbrotDiscovery = CbMandelbrotDiscovery.IsChecked == true;
        _settings.MandelbrotDimming = SliderMandelbrotDimming.Value / 100.0;
        _settings.MandelbrotHistogramColoring = CbMandelbrotHistogramColoring.IsChecked == true;
        _settings.MandelbrotRotation = Math.Max(0, CbMandelbrotRotation.SelectedIndex);
        _settings.MandelbrotColorScheme = Math.Max(0, CbMandelbrotColorScheme.SelectedIndex);
        _settings.FerrofluidCoreGravity = SliderFerrofluidCoreGravity.Value;
        _settings.FerrofluidMutualAttraction = SliderFerrofluidMutualAttraction.Value;
        _settings.FerrofluidDamping = SliderFerrofluidDamping.Value / 100.0;
        _settings.FerrofluidExplosionForce = SliderFerrofluidExplosionForce.Value;
        _settings.FerrofluidExplosionDuration = SliderFerrofluidExplosionDuration.Value / 10.0;
        _settings.FerrofluidBristleForce = SliderFerrofluidBristleForce.Value;
        _settings.FerrofluidMaxSpeed = SliderFerrofluidMaxSpeed.Value;
        _settings.FerrofluidExplosionBassThreshold = SliderFerrofluidExplosionBassThreshold.Value / 100.0;
        _settings.FerrofluidBristleTrebleThreshold = SliderFerrofluidBristleTrebleThreshold.Value / 100.0;
        _settings.MatrixColorCycling = CbMatrixColorCycling.IsChecked == true;
        _settings.MatrixInfiniteZoom = CbMatrixInfiniteZoom.IsChecked == true;
        _settings.MatrixZoomRate = SliderMatrixZoomRate.Value;
        _settings.MatrixMaxTrails = (int)SliderMatrixMaxTrails.Value;
        _settings.MatrixDisableBlur = CbMatrixDisableBlur.IsChecked == true;
        _settings.GameOfLifeCellSize = (int)SliderGameOfLifeCellSize.Value;
        _settings.GameOfLifeUseScreenRate = CbGameOfLifeUseScreenRate.IsChecked == true;
        _settings.GameOfLifeTickIntervalMs = (int)SliderGameOfLifeTickInterval.Value;
        _settings.GameOfLifeFadeGenerations = (int)SliderGameOfLifeFadeGenerations.Value;
        _settings.GameOfLifeHeatBoost = (int)SliderGameOfLifeHeatBoost.Value;
        _settings.GameOfLifeDensity = (int)SliderGameOfLifeDensity.Value;
        _settings.GameOfLifeCameraRoam = CbGameOfLifeCameraRoam.IsChecked == true;
        _settings.GameOfLifeCameraMaxZoom = SliderGameOfLifeCameraMaxZoom.Value;
        _settings.GameOfLifeCameraOverscan = (int)SliderGameOfLifeCameraOverscan.Value;
        _settings.GameOfLifeCameraSpeed = SliderGameOfLifeCameraSpeed.Value;
        _settings.GameOfLifeRestartOnTrackChange = CbGameOfLifeRestartOnTrackChange.IsChecked == true;
        _settings.GameOfLifeAntiStagnation = CbGameOfLifeAntiStagnation.IsChecked == true;
        _settings.GameOfLifeAntiStagnationIntensity = (int)SliderGameOfLifeAntiStagnationIntensity.Value;
        _settings.GameOfLifeScalingMode = CbGameOfLifeScalingMode.SelectedIndex;
        _settings.GameOfLifeColorMode = CbGameOfLifeColorMode.SelectedIndex;
        _settings.GameOfLifeSeedColorMask = BuildSeedColorMask();
        _settings.GameOfLifeHueSpread = CbGameOfLifeHueSpread.SelectedIndex switch
        {
            0 => 0, 1 => 15, 2 => 30, 3 => 45, _ => 60
        };
        _settings.GameOfLifeSeedSpread = Math.Clamp(CbGameOfLifeSeedSpread.SelectedIndex, 0, 2);
        _settings.GameOfLifeBloom = CbGameOfLifeBloom.IsChecked == true;
        _settings.GameOfLifeBloomRadius = (int)SliderGameOfLifeBloomRadius.Value;
        _settings.GameOfLifeBloomIntensity = (int)SliderGameOfLifeBloomIntensity.Value;
        _settings.GameOfLifeBirthGenerations = (int)SliderGameOfLifeBirthGenerations.Value;
        _settings.GravityG = (int)SliderGravityG.Value;
        _settings.GravityOrbitRepulsion = SliderGravityOrbitRepulsion.Value;
        _settings.GravityCentralGravity = SliderGravityCentralGravity.Value;
        _settings.GravityOrbitalPerturbation = SliderGravityOrbitalPerturbation.Value;
        _settings.GravityCameraRoam = CbGravityCameraRoam.IsChecked == true;
        _settings.GravityRestartOnTrackChange = CbGravityRestartOnTrackChange.IsChecked == true;
        _settings.GravityBlobMultiplier = SliderGravityBlobMultiplier.Value;
        _settings.GravityShowDiagnostics = CbGravityShowDiagnostics.IsChecked == true;
        _settings.GravitySupernovaMass = SliderGravitySupernovaMass.Value;
        _settings.GravityDensity = (int)SliderGravityDensity.Value;
        _settings.ClockMode = CbClockMode.SelectedIndex >= 0 ? CbClockMode.SelectedIndex : 0;
        _settings.ClockBrightness = SliderClockBrightness.Value / 100.0;
        _settings.ClockDigitalSize = (int)SliderClockDigitalSize.Value;
        _settings.ClockUse24Hour = SliderClockHourFormat.Value >= 0.5;
        _settings.ClockAnalogSize = (int)SliderClockAnalogSize.Value;
        _settings.ClockAnalogStyle = CbClockAnalogStyle.SelectedIndex >= 0 ? CbClockAnalogStyle.SelectedIndex : 0;
        _settings.GameOfLifeRulesEngine = Math.Max(0, CbGameOfLifeRulesEngine.SelectedIndex);
        _settings.GameOfLifeEraBandedHueSpeed = SliderGameOfLifeEraBandedHueSpeed.Value;
        _settings.GameOfLifeCustomRule = BuildRuleStringFromCheckboxes();
        _settings.ReactiveBlobsPlayfield = CbReactiveBlobsPlayfield.IsChecked == true;
        _settings.ReactiveBlobsBackglass = CbReactiveBlobsBackglass.IsChecked == true;
        _settings.ReactiveBlobsTopper = CbReactiveBlobsTopper.IsChecked == true;
        _settings.ReactiveBlobsDmd = CbReactiveBlobsDmd.IsChecked == true;
        _settings.ReactiveProjectM = CbReactiveProjectM.IsChecked == true;
        _settings.ReactivityThreshold = SliderReactivityThreshold.Value / 100.0;
        _settings.ReactiveSpeedMs = (int)SliderReactiveSpeed.Value;
        _settings.ReactiveOverdrive = SliderReactiveOverdrive.Value / 10.0;
        _settings.TopperDistortion = SliderDistortion.Value / 100.0;
        _settings.TopperScreenScaling = SliderScreenScaling.Value / 100.0;
        _settings.TopperLogoSpin = CbTopperLogoSpin.IsChecked == true;
        _settings.TopperLogoShadow = CbTopperLogoShadow.IsChecked == true;
        _settings.TopperLogoRings = (LogoRingsMode)(int)SliderTopperLogoRings.Value;
        _settings.TopperLogoRingsBrightness = (int)SliderTopperRingBrightness.Value;
        _settings.TopperLogoBrightness = (int)SliderTopperLogoBrightness.Value;
        _settings.TopperLogoColorMode = RbTopperLogoColorReactive.IsChecked == true ? LogoColorMode.Reactive
            : RbTopperLogoColorMorph.IsChecked == true ? LogoColorMode.SlowMorph
            : LogoColorMode.Off;
        _settings.NetworkCachingMs = (int)SliderNetworkCaching.Value;
        _settings.LiveCachingMs = (int)SliderLiveCaching.Value;
        _settings.FileCachingMs = (int)SliderFileCaching.Value;
        _settings.HttpReconnect = CbHttpReconnect.IsChecked == true;
        _settings.NetworkTimeoutSeconds = (int)SliderNetworkTimeout.Value;
        _settings.PlexGaplessPlayback = CbPlexGapless.IsChecked == true;
        _settings.AutoDjProviderId = CbAutoDjProvider.SelectedValue as string;
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
        _originalPlayfieldIntensity = _settings.PlayfieldIntensity;
        _originalPlayfieldSpeed = _settings.PlayfieldSpeed;
        _originalBackglassIntensity = _settings.BackglassIntensity;
        _originalBackglassSpeed = _settings.BackglassSpeed;
        _originalTopperIntensity = _settings.TopperIntensity;
        _originalTopperSpeed = _settings.TopperSpeed;
        _originalDmdIntensity = _settings.DmdIntensity;
        _originalDmdSpeed = _settings.DmdSpeed;
        _originalDistortion = _settings.TopperDistortion;
        _originalScreenScaling = _settings.TopperScreenScaling;
        _originalPlayfieldBlobPattern = _settings.PlayfieldBlobPattern;
        _originalPlayfieldBlobCount = _settings.PlayfieldBlobCount;
        _originalPlayfieldBlobSizeOffset = _settings.PlayfieldBlobSizeOffset;
        _originalPlayfieldRotation = _settings.PlayfieldRotation;
        _originalPlayfieldApplyOrientationToVideos = _settings.PlayfieldApplyOrientationToVideos;
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
        _originalReactiveBlobsPlayfield = _settings.ReactiveBlobsPlayfield;
        _originalReactiveBlobsBackglass = _settings.ReactiveBlobsBackglass;
        _originalReactiveBlobsTopper = _settings.ReactiveBlobsTopper;
        _originalReactiveBlobsDmd = _settings.ReactiveBlobsDmd;
        _originalReactiveProjectM = _settings.ReactiveProjectM;
        _originalReactivityThreshold = _settings.ReactivityThreshold;
        _originalReactiveSpeedMs = _settings.ReactiveSpeedMs;
        _originalReactiveOverdrive = _settings.ReactiveOverdrive;
        _originalTitleText = _settings.TitleText;
        _originalLogoText = _settings.LogoText;
        _originalLogoBehindVisuals = _settings.LogoBehindVisuals;
        _originalLogoSpin = _settings.LogoSpin;
        _originalLogoShadow = _settings.LogoShadow;
        _originalLogoRings = _settings.LogoRings;
        _originalLogoRingsBrightness = _settings.LogoRingsBrightness;
        _originalLogoBrightness = _settings.LogoBrightness;
        _originalLogoColorMode = _settings.LogoColorMode;
        _originalTopperLogoSpin = _settings.TopperLogoSpin;
        _originalTopperLogoShadow = _settings.TopperLogoShadow;
        _originalTopperLogoRings = _settings.TopperLogoRings;
        _originalTopperLogoRingsBrightness = _settings.TopperLogoRingsBrightness;
        _originalTopperLogoBrightness = _settings.TopperLogoBrightness;
        _originalTopperLogoColorMode = _settings.TopperLogoColorMode;
        _originalMandelbrotUseGpu = _settings.MandelbrotUseGpu;
        _originalMandelbrotAdaptiveIterations = _settings.MandelbrotAdaptiveIterations;
        _originalMandelbrotMaxIterations = _settings.MandelbrotMaxIterations;
        _originalMandelbrotTickIntervalMs = _settings.MandelbrotTickIntervalMs;
        _originalMandelbrotUseScreenRate = _settings.MandelbrotUseScreenRate;
        _originalMandelbrotRenderScale = _settings.MandelbrotRenderScale;
        _originalMandelbrotPerturbation = _settings.MandelbrotPerturbation;
        _originalMandelbrotDiscovery = _settings.MandelbrotDiscovery;
        _originalMandelbrotDimming = _settings.MandelbrotDimming;
        _originalMandelbrotHistogramColoring = _settings.MandelbrotHistogramColoring;
        _originalMandelbrotRotation = _settings.MandelbrotRotation;
        _originalMandelbrotColorScheme = _settings.MandelbrotColorScheme;
        _originalGameOfLifeCellSize = _settings.GameOfLifeCellSize;
        _originalGameOfLifeTickIntervalMs = _settings.GameOfLifeTickIntervalMs;
        _originalGameOfLifeUseScreenRate = _settings.GameOfLifeUseScreenRate;
        _originalGameOfLifeFadeGenerations = _settings.GameOfLifeFadeGenerations;
        _originalGameOfLifeHeatBoost = _settings.GameOfLifeHeatBoost;
        _originalGameOfLifeDensity = _settings.GameOfLifeDensity;
        _originalGameOfLifeCameraRoam = _settings.GameOfLifeCameraRoam;
        _originalGameOfLifeCameraMaxZoom = _settings.GameOfLifeCameraMaxZoom;
        _originalGameOfLifeCameraOverscan = _settings.GameOfLifeCameraOverscan;
        _originalGameOfLifeCameraSpeed = _settings.GameOfLifeCameraSpeed;
        _originalGameOfLifeRestartOnTrackChange = _settings.GameOfLifeRestartOnTrackChange;
        _originalGameOfLifeColorMode = _settings.GameOfLifeColorMode;
        _originalGameOfLifeRulesEngine = _settings.GameOfLifeRulesEngine;
        _originalGameOfLifeEraBandedHueSpeed = _settings.GameOfLifeEraBandedHueSpeed;
        _originalGameOfLifeCustomRule = _settings.GameOfLifeCustomRule ?? "B3/S23";
        _originalGameOfLifeSeedColorMask = _settings.GameOfLifeSeedColorMask;
        _originalGameOfLifeHueSpread = _settings.GameOfLifeHueSpread;
        _originalGameOfLifeSeedSpread = _settings.GameOfLifeSeedSpread;
        _originalGameOfLifeBloom = _settings.GameOfLifeBloom;
        _originalGameOfLifeBloomRadius = _settings.GameOfLifeBloomRadius;
        _originalGameOfLifeBloomIntensity = _settings.GameOfLifeBloomIntensity;
        _originalGameOfLifeBirthGenerations = _settings.GameOfLifeBirthGenerations;
        _originalGravityBlobMultiplier = _settings.GravityBlobMultiplier;
        _originalGravityCameraRoam = _settings.GravityCameraRoam;
        _originalGravityShowDiagnostics = _settings.GravityShowDiagnostics;
        _originalGravityDensity = _settings.GravityDensity;
        _originalClockMode = _settings.ClockMode;
        _originalClockBrightness = _settings.ClockBrightness;
        _originalClockDigitalSize = _settings.ClockDigitalSize;
        _originalClockUse24Hour = _settings.ClockUse24Hour;
        _originalClockAnalogSize = _settings.ClockAnalogSize;
        _originalClockAnalogStyle = _settings.ClockAnalogStyle;
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

    private void SliderNetworkTimeout_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (NetworkTimeoutValueText != null)
            NetworkTimeoutValueText.Text = ((int)e.NewValue).ToString();
    }

    // Working copy of the plug-in instance configs the tab edits, plus a map from each editor
    // control to (instanceId, settingKey) so values can be harvested back on save. DisplayName and
    // Enabled controls are tracked separately.
    private readonly List<Phosphor.Plugins.PluginInstanceConfig> _pluginWorkingConfigs = new();
    private readonly List<(System.Windows.Controls.Control Control, string InstanceId, string Key)> _pluginFieldControls = new();
    private readonly Dictionary<string, System.Windows.Controls.TextBox> _pluginDisplayNameBoxes = new();
    private readonly Dictionary<string, System.Windows.Controls.CheckBox> _pluginEnabledBoxes = new();
    // Per-instance caching-policy selector (Default / Always / Never → AllowCaching null/true/false).
    private readonly Dictionary<string, System.Windows.Controls.ComboBox> _pluginCachingBoxes = new();

    // Custom field harvesters for editors that don't fit the standard control switch (e.g. the
    // FolderPath/AllowMultiple list editor). Each returns the field's current value on save.
    private readonly List<(string InstanceId, string Key, Func<string?> GetValue)> _pluginCustomFieldGetters = new();

    // Sentinel used to pre-fill a secret field so it looks populated without exposing the real
    // value. On harvest, a field still equal to the sentinel is left unchanged.
    private const string SecretSentinel = "\u0001\u0001SECRET-UNCHANGED\u0001\u0001";

    // Shared HttpClient for transient sources built to invoke config actions.
    private readonly System.Net.Http.HttpClient _pluginHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    // Inline Plex-library editor state, keyed by instance id: the added libraries (rendered as a
    // list with per-library Hubs/Playlists) and a lazily-fetched cache of all server libraries
    // (for the "add" dropdown). Serialized back into the instance's "libraries" setting on change.
    private readonly Dictionary<string, List<PlexLibraryMapping>> _pluginLibraryState = new();
    private readonly Dictionary<string, List<PlexLibraryMapping>> _pluginLibraryAvailable = new();

    // Standardized editor sizing so text/combo/password fields line up.
    private const double EditorHeight = 28;
    private const double EditorMinWidth = 320;
    private static readonly Thickness EditorPadding = new(6, 2, 6, 2);
    private static readonly Thickness RowMargin = new(0, 3, 0, 3);

    /// <summary>
    /// Populates the AutoDJ provider dropdown from the VM's searchable sources, selecting the saved
    /// provider (or YouTube by default). Runs after Load so Owner/DataContext is available.
    /// </summary>
    private void PopulateAutoDjProvider(AppSettings settings)
    {
        if (Owner?.DataContext is not JukeboxViewModel vm) return;
        CbAutoDjProvider.ItemsSource = vm.SearchSources;
        CbAutoDjProvider.SelectedValue = string.IsNullOrEmpty(settings.AutoDjProviderId)
            ? vm.SearchSources.FirstOrDefault()?.InstanceId
            : settings.AutoDjProviderId;
    }

    /// <summary>
    /// Populates the Plug-ins tab with editable controls over a working copy of
    /// <c>settings.PluginInstances</c>. Values are harvested back and persisted on save
    /// (<see cref="HarvestPluginSourcesTab"/>).
    /// </summary>
    private void PopulatePluginSourcesTab()
    {
        if (PanelPluginSources == null) return;
        PanelPluginSources.Children.Clear();
        _pluginFieldControls.Clear();
        _pluginDisplayNameBoxes.Clear();
        _pluginEnabledBoxes.Clear();
        _pluginCachingBoxes.Clear();
        _pluginCustomFieldGetters.Clear();
        // Re-parse the inline Plex-library editor state from the (rebuilt) working configs so an
        // add/remove is reflected instead of a stale cached list.
        _pluginLibraryState.Clear();

        // ── Encryption toggle (top of tab) — app-level, but only plug-in secrets use it, so it
        // lives here where the portability caveat is most relevant. ──
        {
            var dimBrush = (System.Windows.Media.Brush)FindResource("TextDimBrush");
            var textBrush = (System.Windows.Media.Brush)FindResource("TextBrush");
            var encBox = new System.Windows.Controls.CheckBox
            {
                Content = "Encrypt sensitive settings (API tokens, passwords)",
                IsChecked = _settings.EncryptSecrets,
                Foreground = textBrush,
                Margin = new Thickness(0, 0, 0, 2),
            };
            encBox.Checked += (_, _) => _settings.EncryptSecrets = true;
            encBox.Unchecked += (_, _) => _settings.EncryptSecrets = false;
            PanelPluginSources.Children.Add(encBox);
            PanelPluginSources.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Encrypts secret plug-in settings at rest using Windows DPAPI, tied to your "
                     + "Windows user account on this PC. The settings file is then no longer portable "
                     + "to another machine or user. Leave off to keep secrets as plain text.",
                Foreground = dimBrush,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14),
            });
        }

        // Edit a working copy so cancelling the dialog doesn't mutate settings.
        _pluginWorkingConfigs.Clear();
        foreach (var c in _settings.PluginInstances)
        {
            _pluginWorkingConfigs.Add(new Phosphor.Plugins.PluginInstanceConfig
            {
                TypeId = c.TypeId,
                InstanceId = c.InstanceId,
                DisplayName = c.DisplayName,
                Enabled = c.Enabled,
                Settings = new Dictionary<string, string?>(c.Settings),
                AllowCaching = c.AllowCaching,
            });
        }

        if (_pluginWorkingConfigs.Count == 0)
        {
            if (PluginSourcesEmptyText != null)
                PluginSourcesEmptyText.Visibility = Visibility.Visible;
            return;
        }
        if (PluginSourcesEmptyText != null)
            PluginSourcesEmptyText.Visibility = Visibility.Collapsed;

        var accent = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var text = (System.Windows.Media.Brush)FindResource("TextBrush");
        var dim = (System.Windows.Media.Brush)FindResource("TextDimBrush");
        var surface2 = (System.Windows.Media.Brush)FindResource("Surface2Brush");

        foreach (var cfg in _pluginWorkingConfigs)
        {
            var info = Phosphor.Plugins.PluginSettingsFactory.DescribeProvider(cfg.TypeId);
            var typeName = info?.DisplayName ?? cfg.TypeId;

            var card = new System.Windows.Controls.Border
            {
                BorderBrush = accent,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 8, 0, 14),
                Margin = new Thickness(0, 0, 0, 12),
            };
            var panel = new System.Windows.Controls.StackPanel();

            // ── Header row: title left, Enabled + Remove right-justified ──
            var headerGrid = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 4) };
            headerGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });

            var title = new System.Windows.Controls.TextBlock
            {
                Text = $"{typeName}  ({cfg.TypeId})",
                Foreground = accent,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            System.Windows.Controls.Grid.SetColumn(title, 0);
            headerGrid.Children.Add(title);

            var enabledBox = new System.Windows.Controls.CheckBox
            {
                Content = "Enabled",
                IsChecked = cfg.Enabled,
                Foreground = text,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 10, 0),
            };
            _pluginEnabledBoxes[cfg.InstanceId] = enabledBox;
            System.Windows.Controls.Grid.SetColumn(enabledBox, 1);
            headerGrid.Children.Add(enabledBox);

            // Remove button — only for providers that support multiple instances.
            if (info?.SupportsMultipleInstances == true)
            {
                var removeBtn = new System.Windows.Controls.Button
                {
                    Content = "🗑 Remove",
                    Padding = new Thickness(6, 2, 6, 2),
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var removeId = cfg.InstanceId;
                removeBtn.Click += (_, _) => RemovePluginInstance(removeId);
                System.Windows.Controls.Grid.SetColumn(removeBtn, 2);
                headerGrid.Children.Add(removeBtn);
            }
            panel.Children.Add(headerGrid);

            // Description
            if (!string.IsNullOrWhiteSpace(info?.Description))
            {
                panel.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = info!.Value.Description,
                    Foreground = dim,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 8),
                });
            }

            // Supported capabilities (human-readable list of the interfaces the source implements).
            var capSource = Phosphor.Plugins.PluginSettingsFactory.BuildTransientSource(cfg, _pluginHttp);
            if (capSource != null)
            {
                var caps = Phosphor.Plugins.PluginSettingsFactory.DescribeCapabilities(capSource);
                if (caps.Count > 0)
                {
                    panel.Children.Add(new System.Windows.Controls.TextBlock
                    {
                        Text = "Supports: " + string.Join(", ", caps),
                        Foreground = dim,
                        FontSize = 11,
                        FontStyle = FontStyles.Italic,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 8),
                    });
                }

                // ── "Test connection" — for sources that support it (e.g. Plex). ──
                if (capSource is Phosphor.Plugin.Abstractions.IConnectionTestable)
                {
                    var testInstanceId = cfg.InstanceId;
                    var testRow = new System.Windows.Controls.StackPanel
                    {
                        Orientation = System.Windows.Controls.Orientation.Horizontal,
                        Margin = new Thickness(0, 0, 0, 8),
                    };
                    var testBtn = new System.Windows.Controls.Button
                    {
                        Content = "Test connection", Padding = new Thickness(8, 3, 8, 3),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    var testResult = new System.Windows.Controls.TextBlock
                    {
                        Foreground = dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(10, 0, 0, 0), TextWrapping = TextWrapping.Wrap,
                    };
                    testBtn.Click += async (_, _) =>
                    {
                        await TestPluginConnectionAsync(testInstanceId, testBtn, testResult);
                    };
                    testRow.Children.Add(testBtn);
                    testRow.Children.Add(testResult);
                    panel.Children.Add(testRow);
                }

                // ── "Manage hidden channels…" — for sources that support hiding (e.g. SiriusXM). ──
                if (capSource is Phosphor.Plugin.Abstractions.IHideable)
                {
                    var hideInstanceId = cfg.InstanceId;
                    var hideRow = new System.Windows.Controls.StackPanel
                    {
                        Orientation = System.Windows.Controls.Orientation.Horizontal,
                        Margin = new Thickness(0, 0, 0, 8),
                    };
                    var hideBtn = new System.Windows.Controls.Button
                    {
                        Content = "Manage hidden channels…", Padding = new Thickness(8, 3, 8, 3),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    var hideResult = new System.Windows.Controls.TextBlock
                    {
                        Foreground = dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(10, 0, 0, 0), TextWrapping = TextWrapping.Wrap,
                    };
                    hideBtn.Click += (_, _) => ManageHiddenItems(hideInstanceId, hideResult);
                    hideRow.Children.Add(hideBtn);
                    hideRow.Children.Add(hideResult);
                    panel.Children.Add(hideRow);
                }

                // ── "Update engine" — for sources whose backing tool can self-update (e.g. yt-dlp). ──
                if (capSource is Phosphor.Plugin.Abstractions.IUpdatable { SupportsUpdate: true })
                {
                    var updateRow = new System.Windows.Controls.StackPanel
                    {
                        Orientation = System.Windows.Controls.Orientation.Horizontal,
                        Margin = new Thickness(0, 0, 0, 4),
                    };
                    var updateBtn = new System.Windows.Controls.Button
                    {
                        Content = "Update engine", Padding = new Thickness(8, 3, 8, 3),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    var updateResult = new System.Windows.Controls.TextBlock
                    {
                        Foreground = dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(10, 0, 0, 0), TextWrapping = TextWrapping.Wrap,
                    };
                    updateBtn.Click += async (_, _) =>
                    {
                        await UpdatePluginEngineAsync(updateBtn, updateResult);
                    };
                    updateRow.Children.Add(updateBtn);
                    updateRow.Children.Add(updateResult);
                    panel.Children.Add(updateRow);

                    // Auto-update-on-startup toggle (persists to AppSettings.YtDlpAutoUpdate).
                    var autoUpdate = new System.Windows.Controls.CheckBox
                    {
                        Content = "Automatically check for updates on startup",
                        IsChecked = _settings.YtDlpAutoUpdate,
                        Foreground = dim, FontSize = 11,
                        Margin = new Thickness(0, 0, 0, 8),
                    };
                    autoUpdate.Checked += (_, _) => _settings.YtDlpAutoUpdate = true;
                    autoUpdate.Unchecked += (_, _) => _settings.YtDlpAutoUpdate = false;
                    panel.Children.Add(autoUpdate);
                }

                // ── "Rescan library" — for sources that build a catalog from backing content
                // (e.g. a local-folder source, or Plex "Update Libraries"). ──
                if (capSource is Phosphor.Plugin.Abstractions.IRefreshable)
                {
                    var rescanInstanceId = cfg.InstanceId;
                    var rescanRow = new System.Windows.Controls.StackPanel
                    {
                        Orientation = System.Windows.Controls.Orientation.Horizontal,
                        Margin = new Thickness(0, 0, 0, 4),
                    };
                    var rescanBtn = new System.Windows.Controls.Button
                    {
                        Content = "Rescan library", Padding = new Thickness(8, 3, 8, 3),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    // Small indeterminate spinner (a per-folder ProgressBar looks broken for a
                    // single-folder source, which jumps straight from 0 to 1). Mirrors the little
                    // now-playing spinner. Hidden until a rescan is running.
                    var rescanSpinner = CreateSmallSpinner();
                    rescanSpinner.Visibility = Visibility.Collapsed;
                    var rescanResult = new System.Windows.Controls.TextBlock
                    {
                        Foreground = dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(10, 0, 0, 0), TextWrapping = TextWrapping.Wrap,
                    };
                    rescanBtn.Click += async (_, _) =>
                    {
                        await RescanPluginLibraryAsync(rescanInstanceId, rescanBtn, rescanSpinner, rescanResult);
                    };
                    rescanRow.Children.Add(rescanBtn);
                    rescanRow.Children.Add(rescanSpinner);
                    rescanRow.Children.Add(rescanResult);
                    panel.Children.Add(rescanRow);
                }

                // The transient built for capability display is no longer needed — dispose it so any
                // resources it opened during construction are released.
                DisposeTransientSource(capSource);
            }

            // ── Settings table: column 0 = label, column 1 = editor ──
            var grid = new System.Windows.Controls.Grid();
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(130) });
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });

            // Display name row
            var nameBox = new System.Windows.Controls.TextBox
            {
                Text = cfg.DisplayName ?? typeName,
                Foreground = text,
                Background = surface2,
                Height = EditorHeight,
                Padding = EditorPadding,
                MinWidth = EditorMinWidth,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            };
            _pluginDisplayNameBoxes[cfg.InstanceId] = nameBox;
            AddSettingRow(grid, "Display name", null, nameBox, text, dim);

            // Declarative settings fields
            var schema = info?.Schema ?? [];
            foreach (var d in schema)
            {
                cfg.Settings.TryGetValue(d.Key, out var current);
                current ??= d.DefaultValue;

                // The Plex "libraries" field gets an inline editor (dropdown + Add, and a list of
                // added libraries with Hubs/Playlists + Remove) instead of a raw text field.
                if (d.Key == "libraries")
                {
                    BuildInlineLibraryEditor(grid, cfg, d, text, dim, surface2, accent);
                    continue;
                }

                // Multi-valued settings (e.g. a list of folders) render an add/remove list editor,
                // storing the rows as newline-joined text. FolderPath rows use a folder picker.
                if (d.AllowMultiple)
                {
                    BuildMultiValueEditor(grid, cfg, d, current, text, dim, surface2);
                    continue;
                }

                // Single folder path: text field + a "Browse…" folder picker.
                if (d.Type == Phosphor.Plugin.Abstractions.PluginSettingType.FolderPath)
                {
                    BuildFolderPathEditor(grid, cfg, d, current, text, dim, surface2);
                    continue;
                }

                System.Windows.Controls.Control editor = d.Type switch
                {
                    Phosphor.Plugin.Abstractions.PluginSettingType.Bool => new System.Windows.Controls.CheckBox
                    {
                        IsChecked = bool.TryParse(current, out var b) && b,
                        Foreground = text,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    },
                    Phosphor.Plugin.Abstractions.PluginSettingType.Enum => MakeEnumCombo(d, current, text, surface2),
                    Phosphor.Plugin.Abstractions.PluginSettingType.Secret => new System.Windows.Controls.PasswordBox
                    {
                        // Pre-fill with a sentinel so it looks populated (dots) when a secret exists.
                        Password = string.IsNullOrEmpty(current) ? "" : SecretSentinel,
                        Foreground = text,
                        Background = surface2,
                        Height = EditorHeight,
                        Padding = EditorPadding,
                        MinWidth = EditorMinWidth,
                        VerticalContentAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    },
                    _ => new System.Windows.Controls.TextBox
                    {
                        Text = current ?? "", Foreground = text,
                        Background = surface2,
                        Height = EditorHeight,
                        Padding = EditorPadding,
                        MinWidth = EditorMinWidth,
                        VerticalContentAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    },
                };
                if (!string.IsNullOrWhiteSpace(d.HelpText))
                    editor.ToolTip = d.HelpText;

                _pluginFieldControls.Add((editor, cfg.InstanceId, d.Key));
                AddSettingRow(grid, d.Label, d.HelpText, editor, text, dim);
            }

            // ── Caching policy selector — only meaningful for sources that can download/cache.
            // Non-downloadable sources (e.g. Plex, which streams live) have nothing to configure. ──
            if (capSource is Phosphor.Plugin.Abstractions.IDownloadable)
            {
                var cachingCombo = new System.Windows.Controls.ComboBox
                {
                    Foreground = text, Background = surface2, Height = EditorHeight,
                    MinWidth = EditorMinWidth,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                };
                cachingCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "Cache (default)", Tag = "default" });
                cachingCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "Never cache", Tag = "false" });
                // true (force-on) collapses to the default for a downloadable source, so map it to "Cache".
                cachingCombo.SelectedIndex = cfg.AllowCaching == false ? 1 : 0;
                cachingCombo.ToolTip = "Whether videos from this source are downloaded to the disk cache.";
                _pluginCachingBoxes[cfg.InstanceId] = cachingCombo;
                AddSettingRow(grid, "Caching", cachingCombo.ToolTip as string, cachingCombo, text, dim);
            }

            panel.Children.Add(grid);

            // ── Interactive config actions (generic) — the Plex "browse libraries" action is
            // rendered inline above, so skip it here to avoid a duplicate popup button. ──
            var transient = Phosphor.Plugins.PluginSettingsFactory.BuildTransientSource(cfg, _pluginHttp);
            if (transient is Phosphor.Plugin.Abstractions.IConfigurable configurable)
            {
                foreach (var action in configurable.GetConfigActions())
                {
                    if (action.Id == Phosphor.Plugins.Plex.PlexSourceProvider.ActionBrowseLibraries)
                        continue;
                    var actionRow = new System.Windows.Controls.StackPanel
                    {
                        Orientation = System.Windows.Controls.Orientation.Horizontal,
                        Margin = new Thickness(0, 6, 0, 0),
                    };
                    var actionBtn = new System.Windows.Controls.Button
                    {
                        Content = action.Label,
                        Padding = new Thickness(8, 3, 8, 3),
                    };
                    if (!string.IsNullOrWhiteSpace(action.Description))
                        actionBtn.ToolTip = action.Description;
                    var instId = cfg.InstanceId;
                    var actId = action.Id;
                    actionBtn.Click += async (_, _) => await InvokePluginConfigActionAsync(instId, actId);
                    actionRow.Children.Add(actionBtn);
                    panel.Children.Add(actionRow);
                }
            }
            DisposeTransientSource(transient);

            card.Child = panel;
            PanelPluginSources.Children.Add(card);
        }

        // ── "Add source" row for multi-instance providers ──
        var addable = Phosphor.Plugins.PluginSettingsFactory.AddableProviders();
        if (addable.Count > 0)
        {
            var addRow = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(0, 8, 0, 0),
            };
            addRow.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Add source:", Foreground = dim, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            });
            var addCombo = new System.Windows.Controls.ComboBox
            {
                Height = EditorHeight, MinWidth = 160, VerticalContentAlignment = VerticalAlignment.Center,
            };
            foreach (var (typeId, dn) in addable)
                addCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = dn, Tag = typeId });
            addCombo.SelectedIndex = 0;
            addRow.Children.Add(addCombo);

            var addBtn = new System.Windows.Controls.Button
            {
                Content = "＋ Add", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(8, 0, 0, 0),
            };
            addBtn.Click += (_, _) =>
            {
                if (addCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag is string typeId)
                    AddPluginInstance(typeId);
            };
            addRow.Children.Add(addBtn);
            PanelPluginSources.Children.Add(addRow);
        }
    }

    /// <summary>Adds a new instance of a multi-instance provider and re-renders the tab.</summary>
    private void AddPluginInstance(string typeId)
    {
        HarvestPluginSourcesTab(); // preserve current edits before re-render

        var info = Phosphor.Plugins.PluginSettingsFactory.DescribeProvider(typeId);
        var baseName = info?.DisplayName ?? typeId;

        // Unique instance id: typeId, typeId-2, typeId-3, …
        var existing = new HashSet<string>(_settings.PluginInstances.Select(c => c.InstanceId), StringComparer.OrdinalIgnoreCase);
        var instanceId = typeId;
        for (int n = 2; existing.Contains(instanceId); n++)
            instanceId = $"{typeId}-{n}";

        // Count existing instances of this type for a friendly default name.
        int typeCount = _settings.PluginInstances.Count(c => c.TypeId == typeId);
        var displayName = typeCount > 0 ? $"{baseName} {typeCount + 1}" : baseName;

        _settings.PluginInstances.Add(new Phosphor.Plugins.PluginInstanceConfig
        {
            TypeId = typeId,
            InstanceId = instanceId,
            DisplayName = displayName,
            Enabled = true,
            Settings = new Dictionary<string, string?>(),
        });

        PopulatePluginSourcesTab();
    }

    /// <summary>Removes an instance and re-renders the tab.</summary>
    private void RemovePluginInstance(string instanceId)
    {
        HarvestPluginSourcesTab(); // preserve current edits before re-render
        _settings.PluginInstances.RemoveAll(c => c.InstanceId == instanceId);
        PopulatePluginSourcesTab();
    }

    /// <summary>
    /// Parses the added libraries for an instance from its "libraries" setting into the in-memory
    /// editor state (once per instance per tab session).
    /// </summary>
    private List<PlexLibraryMapping> GetInstanceLibraries(Phosphor.Plugins.PluginInstanceConfig cfg)
    {
        if (_pluginLibraryState.TryGetValue(cfg.InstanceId, out var libs))
            return libs;

        libs = new List<PlexLibraryMapping>();
        if (cfg.Settings.TryGetValue("libraries", out var json) && !string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<List<PlexLibraryMapping>>(json);
                if (parsed != null) libs = parsed;
            }
            catch { /* ignore malformed */ }
        }
        _pluginLibraryState[cfg.InstanceId] = libs;
        return libs;
    }

    /// <summary>Serializes an instance's in-memory library list back into its "libraries" setting.</summary>
    private void SaveInstanceLibraries(string instanceId)
    {
        var cfg = _pluginWorkingConfigs.FirstOrDefault(c => c.InstanceId == instanceId);
        if (cfg == null || !_pluginLibraryState.TryGetValue(instanceId, out var libs)) return;
        cfg.Settings["libraries"] = System.Text.Json.JsonSerializer.Serialize(libs);
    }

    /// <summary>
    /// Renders a single <c>FolderPath</c> setting: a read-only-ish text box plus a "Browse…" button
    /// that opens a folder picker. Harvests via a custom getter (newline is irrelevant for one path).
    /// </summary>
    private void BuildFolderPathEditor(
        System.Windows.Controls.Grid grid, Phosphor.Plugins.PluginInstanceConfig cfg,
        Phosphor.Plugin.Abstractions.PluginSettingDescriptor d, string? current,
        System.Windows.Media.Brush text, System.Windows.Media.Brush dim, System.Windows.Media.Brush surface2)
    {
        var row = new System.Windows.Controls.DockPanel();
        var browseBtn = new System.Windows.Controls.Button
        {
            Content = "Browse…", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(8, 0, 0, 0),
        };
        System.Windows.Controls.DockPanel.SetDock(browseBtn, System.Windows.Controls.Dock.Right);
        var box = new System.Windows.Controls.TextBox
        {
            Text = current ?? "", Foreground = text, Background = surface2, Height = EditorHeight,
            Padding = EditorPadding, VerticalContentAlignment = VerticalAlignment.Center,
        };
        browseBtn.Click += (_, _) =>
        {
            var picked = PickFolder(box.Text);
            if (picked != null) box.Text = picked;
        };
        row.Children.Add(browseBtn);
        row.Children.Add(box);

        if (!string.IsNullOrWhiteSpace(d.HelpText)) box.ToolTip = d.HelpText;
        _pluginCustomFieldGetters.Add((cfg.InstanceId, d.Key, () => box.Text));
        AddSettingRow(grid, d.Label, d.HelpText, row, text, dim);
    }

    /// <summary>
    /// Renders a multi-valued setting as an add/remove list editor: each configured value is a row
    /// (with a Remove button), plus an "Add" affordance. For <c>FolderPath</c> the Add opens a folder
    /// picker; otherwise it adds an editable text row. Values are harvested newline-joined.
    /// </summary>
    private void BuildMultiValueEditor(
        System.Windows.Controls.Grid grid, Phosphor.Plugins.PluginInstanceConfig cfg,
        Phosphor.Plugin.Abstractions.PluginSettingDescriptor d, string? current,
        System.Windows.Media.Brush text, System.Windows.Media.Brush dim, System.Windows.Media.Brush surface2)
    {
        var isFolder = d.Type == Phosphor.Plugin.Abstractions.PluginSettingType.FolderPath;

        // Backing list of the current values (one per non-empty line).
        var values = new System.Collections.ObjectModel.ObservableCollection<string>(
            (current ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var container = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 2, 0, 0) };

        var listPanel = new System.Windows.Controls.StackPanel();
        container.Children.Add(listPanel);

        void Rebuild()
        {
            listPanel.Children.Clear();
            if (values.Count == 0)
            {
                listPanel.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = isFolder ? "No folders added yet." : "No entries yet.",
                    Foreground = dim, FontSize = 11, Margin = new Thickness(0, 0, 0, 4),
                });
            }
            for (int i = 0; i < values.Count; i++)
            {
                int index = i;
                var rowDock = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var removeBtn = new System.Windows.Controls.Button
                {
                    Content = "✕", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(8, 0, 0, 0), FontSize = 12,
                };
                System.Windows.Controls.DockPanel.SetDock(removeBtn, System.Windows.Controls.Dock.Right);
                removeBtn.Click += (_, _) => { values.RemoveAt(index); Rebuild(); };
                rowDock.Children.Add(removeBtn);

                var valueBox = new System.Windows.Controls.TextBox
                {
                    Text = values[index], Foreground = text, Background = surface2, Height = EditorHeight,
                    Padding = EditorPadding, VerticalContentAlignment = VerticalAlignment.Center,
                    IsReadOnly = isFolder, // folders are set via the picker; free text otherwise
                };
                valueBox.TextChanged += (_, _) => values[index] = valueBox.Text;
                rowDock.Children.Add(valueBox);
                listPanel.Children.Add(rowDock);
            }
        }
        Rebuild();

        var addBtn = new System.Windows.Controls.Button
        {
            Content = isFolder ? "＋ Add folder…" : "＋ Add",
            Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
        };
        addBtn.Click += (_, _) =>
        {
            if (isFolder)
            {
                var picked = PickFolder(null);
                if (picked != null && !values.Contains(picked, StringComparer.OrdinalIgnoreCase))
                {
                    values.Add(picked);
                    Rebuild();
                }
            }
            else
            {
                values.Add("");
                Rebuild();
            }
        };
        container.Children.Add(addBtn);

        _pluginCustomFieldGetters.Add((cfg.InstanceId, d.Key,
            () => string.Join("\n", values.Where(v => !string.IsNullOrWhiteSpace(v)))));
        AddSettingRow(grid, d.Label, d.HelpText, container, text, dim);
    }

    /// <summary>Opens a folder picker seeded with <paramref name="initial"/>; returns the chosen path or null.</summary>
    private static string? PickFolder(string? initial)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog();
        if (!string.IsNullOrWhiteSpace(initial) && System.IO.Directory.Exists(initial))
            dlg.SelectedPath = initial;
        return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
    }

    /// <summary>
    /// Renders the inline Plex-library editor into the settings grid: an "add library" dropdown +
    /// button, and a list of added libraries each with Hubs/Playlists checkboxes and a Remove
    /// button. Mirrors the legacy Plex tab (no popup, cabinet-friendly).
    /// </summary>
    private void BuildInlineLibraryEditor(
        System.Windows.Controls.Grid grid, Phosphor.Plugins.PluginInstanceConfig cfg,
        Phosphor.Plugin.Abstractions.PluginSettingDescriptor d,
        System.Windows.Media.Brush text, System.Windows.Media.Brush dim,
        System.Windows.Media.Brush surface2, System.Windows.Media.Brush accent)
    {
        var added = GetInstanceLibraries(cfg);
        var instId = cfg.InstanceId;

        // Container for the added-libraries list (spans both columns, below the add row).
        var container = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 4, 0, 0) };

        // ── Add row: dropdown of not-yet-added libraries + Add button (rendered as the editor in
        // column 1, with a "Libraries" label in column 0 so the whole thing is one line). ──
        var addRow = new System.Windows.Controls.DockPanel();
        var addBtn = new System.Windows.Controls.Button
        {
            Content = "＋ Add", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(8, 0, 0, 0),
        };
        System.Windows.Controls.DockPanel.SetDock(addBtn, System.Windows.Controls.Dock.Right);
        var combo = new System.Windows.Controls.ComboBox
        {
            Foreground = text, Background = surface2, Height = EditorHeight,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        // Prevent the parent ScrollViewer from scrolling when the selection changes: WPF raises
        // RequestBringIntoView on selection, which bubbles up and moves the settings scroll position.
        combo.RequestBringIntoView += (_, e) => e.Handled = true;

        // Populate the dropdown from the available cache (if already fetched), excluding added.
        void RefreshCombo()
        {
            combo.Items.Clear();
            var addedKeys = new HashSet<string>(added.Select(l => l.Key));
            if (_pluginLibraryAvailable.TryGetValue(instId, out var avail))
            {
                foreach (var lib in avail.Where(l => !addedKeys.Contains(l.Key)))
                    combo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = $"{lib.Title} ({lib.Type})", Tag = lib });
                combo.Text = combo.Items.Count > 0 ? "" : "All libraries added";
            }
            else
            {
                combo.Text = "Click to load libraries…";
            }
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }
        RefreshCombo();

        // Lazily fetch on first dropdown open.
        combo.DropDownOpened += async (_, _) =>
        {
            if (!_pluginLibraryAvailable.ContainsKey(instId))
            {
                combo.Text = "Loading…";
                await FetchAvailableLibrariesAsync(instId);
                RefreshCombo();
                combo.IsDropDownOpen = true;
            }
        };

        addBtn.Click += (_, _) =>
        {
            if (combo.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag is PlexLibraryMapping lib
                && !added.Any(l => l.Key == lib.Key))
            {
                // Mutate + save the library list first, THEN harvest, so the addition reaches
                // _settings.PluginInstances (Populate rebuilds the working configs from there).
                added.Add(new PlexLibraryMapping { Key = lib.Key, Title = lib.Title, Type = lib.Type });
                SaveInstanceLibraries(instId);
                HarvestPluginSourcesTab();
                PopulatePluginSourcesTab();
            }
        };
        addRow.Children.Add(addBtn);
        addRow.Children.Add(combo);
        AddSettingRow(grid, "Libraries", "Add a library to show as a browsable tile.", addRow, text, dim);

        // ── Added libraries list: Title + Hubs + Playlists + Remove ──
        if (added.Count == 0)
        {
            container.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "No libraries added yet.", Foreground = dim, FontSize = 11,
            });
        }
        foreach (var lib in added)
        {
            var libKey = lib.Key;
            var row = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 3, 0, 3) };

            var removeBtn = new System.Windows.Controls.Button
            {
                Content = "✕", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(8, 0, 0, 0), FontSize = 12,
            };
            System.Windows.Controls.DockPanel.SetDock(removeBtn, System.Windows.Controls.Dock.Right);
            removeBtn.Click += (_, _) =>
            {
                // Update the library list first, THEN harvest, so the removal is included when the
                // working configs are pushed to _settings.PluginInstances (Populate rebuilds from there).
                added.RemoveAll(l => l.Key == libKey);
                SaveInstanceLibraries(instId);
                HarvestPluginSourcesTab();
                PopulatePluginSourcesTab();
            };
            row.Children.Add(removeBtn);

            var playlistsCb = new System.Windows.Controls.CheckBox
            {
                Content = "Playlists", IsChecked = lib.PlaylistsEnabled, Foreground = dim,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
                ToolTip = "Show a Playlists tile",
            };
            playlistsCb.Checked += (_, _) => { lib.PlaylistsEnabled = true; SaveInstanceLibraries(instId); };
            playlistsCb.Unchecked += (_, _) => { lib.PlaylistsEnabled = false; SaveInstanceLibraries(instId); };
            System.Windows.Controls.DockPanel.SetDock(playlistsCb, System.Windows.Controls.Dock.Right);
            row.Children.Add(playlistsCb);

            var hubsCb = new System.Windows.Controls.CheckBox
            {
                Content = "Hubs", IsChecked = lib.HubsEnabled, Foreground = dim,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
                ToolTip = "Show a Hubs tile (Recently Added, etc.)",
            };
            hubsCb.Checked += (_, _) => { lib.HubsEnabled = true; SaveInstanceLibraries(instId); };
            hubsCb.Unchecked += (_, _) => { lib.HubsEnabled = false; SaveInstanceLibraries(instId); };
            System.Windows.Controls.DockPanel.SetDock(hubsCb, System.Windows.Controls.Dock.Right);
            row.Children.Add(hubsCb);

            row.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = $"{lib.Title} ({lib.Type})", Foreground = text,
                VerticalAlignment = VerticalAlignment.Center,
            });
            container.Children.Add(row);
        }

        // Add the container spanning both columns.
        int gridRow = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
        System.Windows.Controls.Grid.SetRow(container, gridRow);
        System.Windows.Controls.Grid.SetColumn(container, 0);
        System.Windows.Controls.Grid.SetColumnSpan(container, 2);
        container.Margin = new Thickness(0, 6, 0, 0);
        grid.Children.Add(container);
    }

    /// <summary>
    /// Lazily fetches the full library list from the server for the "add" dropdown, using the
    /// instance's current (harvested) URL/token. Cached per instance for the tab session.
    /// </summary>
    private async Task<List<PlexLibraryMapping>> FetchAvailableLibrariesAsync(string instanceId)
    {
        if (_pluginLibraryAvailable.TryGetValue(instanceId, out var cached))
            return cached;

        HarvestPluginSourcesTab();
        var cfg = _settings.PluginInstances.FirstOrDefault(c => c.InstanceId == instanceId);
        var libs = new List<PlexLibraryMapping>();
        if (cfg != null)
        {
            var source = Phosphor.Plugins.PluginSettingsFactory.BuildTransientSource(cfg, _pluginHttp);
            if (source is Phosphor.Plugin.Abstractions.IConfigurable configurable)
            {
                try
                {
                    var sel = await configurable.InvokeConfigActionAsync(Phosphor.Plugins.Plex.PlexSourceProvider.ActionBrowseLibraries);
                    // Options carry Id = library key, Label = "Title (Type)". Recover Title/Type.
                    foreach (var o in sel.Options)
                    {
                        var label = o.Label;
                        string title = label, type = "";
                        var lp = label.LastIndexOf(" (", StringComparison.Ordinal);
                        if (lp >= 0 && label.EndsWith(")"))
                        {
                            title = label[..lp];
                            type = label[(lp + 2)..^1];
                        }
                        libs.Add(new PlexLibraryMapping { Key = o.Id, Title = title, Type = type });
                    }
                }
                catch { /* fetch failure handled by caller (empty list) */ }
            }
        }
        _pluginLibraryAvailable[instanceId] = libs;
        return libs;
    }

    /// <summary>
    /// Runs a source's <see cref="Phosphor.Plugin.Abstractions.IConnectionTestable"/> check using the
    /// current (harvested) settings and shows the ✓/✗ result inline. Builds and disposes a transient
    /// source for the one-off test.
    /// </summary>
    private async Task TestPluginConnectionAsync(
        string instanceId,
        System.Windows.Controls.Button button,
        System.Windows.Controls.TextBlock result)
    {
        HarvestPluginSourcesTab();
        var cfg = _settings.PluginInstances.FirstOrDefault(c => c.InstanceId == instanceId);
        if (cfg == null) return;

        var source = Phosphor.Plugins.PluginSettingsFactory.BuildTransientSource(cfg, _pluginHttp);
        if (source is not Phosphor.Plugin.Abstractions.IConnectionTestable testable)
        {
            DisposeTransientSource(source);
            return;
        }

        button.IsEnabled = false;
        result.Text = "Testing…";
        result.Foreground = System.Windows.Media.Brushes.Gray;
        try
        {
            if (source is Phosphor.Plugin.Abstractions.IPhosphorSource ps)
                await ps.InitializeAsync(new Phosphor.Plugins.Host.PluginHost(cfg.InstanceId, _pluginHttp));

            var r = await testable.TestConnectionAsync();
            var latency = r.Latency is { } l ? $" ({l.TotalMilliseconds:F0} ms)" : "";
            result.Text = (r.Success ? "✓ " : "✗ ") + r.Message + latency;
            result.Foreground = r.Success
                ? System.Windows.Media.Brushes.MediumSeaGreen
                : System.Windows.Media.Brushes.IndianRed;
        }
        catch (Exception ex)
        {
            result.Text = "✗ " + ex.Message;
            result.Foreground = System.Windows.Media.Brushes.IndianRed;
        }
        finally
        {
            button.IsEnabled = true;
            DisposeTransientSource(source);
        }
    }

    /// <summary>
    /// Opens the "manage hidden items" dual-list modal for an <see cref="Phosphor.Plugin.Abstractions.IHideable"/>
    /// source: two side-by-side Extended-multi-select lists (Visible ⇄ Hidden) with move buttons and a
    /// "Hide sports teams" quick action. Persists via <c>SetHidden</c> and reports a summary.
    /// </summary>
    private void ManageHiddenItems(string instanceId, System.Windows.Controls.TextBlock result)
    {
        HarvestPluginSourcesTab();
        var cfg = _settings.PluginInstances.FirstOrDefault(c => c.InstanceId == instanceId);
        if (cfg == null) return;

        var source = Phosphor.Plugins.PluginSettingsFactory.BuildTransientSource(cfg, _pluginHttp);
        if (source is not Phosphor.Plugin.Abstractions.IHideable hideable)
        {
            DisposeTransientSource(source);
            return;
        }

        try
        {
            if (source is Phosphor.Plugin.Abstractions.IPhosphorSource ps)
                ps.InitializeAsync(new Phosphor.Plugins.Host.PluginHost(cfg.InstanceId, _pluginHttp)).GetAwaiter().GetResult();

            var all = hideable.GetHideableItems();
            if (all.Count == 0)
            {
                result.Text = "No channels to manage yet — open the source once to load its lineup.";
                result.Foreground = (System.Windows.Media.Brush)FindResource("TextDimBrush");
                return;
            }
            var hidden = new HashSet<string>(hideable.GetHiddenIds(), StringComparer.Ordinal);

            var dlg = new Phosphor.Windows.ManageHiddenWindow(all, hidden) { Owner = this };
            dlg.ShowDialog();
            if (!dlg.Applied) return;
            var nowHidden = dlg.HiddenIds;

            // Apply as a full diff: hide the new set, unhide everything else.
            var toHide = nowHidden.ToList();
            var toShow = all.Select(i => i.Id).Where(id => !nowHidden.Contains(id)).ToList();
            hideable.SetHidden(toHide, true);
            hideable.SetHidden(toShow, false);

            result.Text = $"{nowHidden.Count} channel(s) hidden. Reopen the source to see the change.";
            result.Foreground = (System.Windows.Media.Brush)FindResource("TextDimBrush");
        }
        catch (Exception ex)
        {
            result.Text = "✗ " + ex.Message;
            result.Foreground = System.Windows.Media.Brushes.IndianRed;
        }
        finally
        {
            DisposeTransientSource(source);
        }
    }

    /// <summary>
    /// Runs a source's <see cref="Phosphor.Plugin.Abstractions.IRefreshable"/> rescan using the
    /// current (harvested) settings, showing a progress bar and the result inline. Builds and
    /// disposes a transient source for the pass (the source persists its own catalog to disk).
    /// </summary>
    private async Task RescanPluginLibraryAsync(
        string instanceId,
        System.Windows.Controls.Button button,
        System.Windows.FrameworkElement spinner,
        System.Windows.Controls.TextBlock result)
    {
        HarvestPluginSourcesTab();
        var cfg = _settings.PluginInstances.FirstOrDefault(c => c.InstanceId == instanceId);
        if (cfg == null) return;

        var source = Phosphor.Plugins.PluginSettingsFactory.BuildTransientSource(cfg, _pluginHttp);
        if (source is not Phosphor.Plugin.Abstractions.IRefreshable refreshable)
        {
            DisposeTransientSource(source);
            return;
        }
        if (!refreshable.CanRefresh)
        {
            result.Text = "Nothing to rescan (no folders configured).";
            result.Foreground = System.Windows.Media.Brushes.Gray;
            DisposeTransientSource(source);
            return;
        }

        button.IsEnabled = false;
        StartSpinner(spinner);
        result.Text = "Scanning…";
        result.Foreground = System.Windows.Media.Brushes.Gray;

        var progress = new Progress<Phosphor.Plugin.Abstractions.RefreshProgress>(p =>
        {
            // A spinner conveys "busy" without a misleading fraction; surface the current item text.
            if (!string.IsNullOrEmpty(p.CurrentItem))
                result.Text = "Scanning: " + p.CurrentItem;
        });

        try
        {
            if (source is Phosphor.Plugin.Abstractions.IPhosphorSource ps)
                await ps.InitializeAsync(new Phosphor.Plugins.Host.PluginHost(cfg.InstanceId, _pluginHttp));

            var r = await refreshable.RefreshAsync(progress);
            result.Text = (r.Success ? "✓ " : "✗ ") + r.Message;
            result.Foreground = r.Success
                ? System.Windows.Media.Brushes.MediumSeaGreen
                : System.Windows.Media.Brushes.IndianRed;
        }
        catch (Exception ex)
        {
            result.Text = "✗ " + ex.Message;
            result.Foreground = System.Windows.Media.Brushes.IndianRed;
        }
        finally
        {
            StopSpinner(spinner);
            button.IsEnabled = true;
            DisposeTransientSource(source);
        }
    }

    /// <summary>
    /// Builds a small (16px) indeterminate spinner — an accent ring with an orbiting dot — for
    /// inline "busy" affordances (e.g. a library rescan). Start/stop its rotation with
    /// <see cref="StartSpinner"/>/<see cref="StopSpinner"/>. Mirrors the now-playing spinner.
    /// </summary>
    private System.Windows.Controls.Canvas CreateSmallSpinner()
    {
        var accent = (System.Windows.Media.Brush)FindResource("AccentBrush");
        const double size = 16;
        var canvas = new System.Windows.Controls.Canvas
        {
            Width = size, Height = size, Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = new System.Windows.Media.RotateTransform(0),
        };
        canvas.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = size, Height = size, Stroke = accent, StrokeThickness = 2,
            Fill = System.Windows.Media.Brushes.Transparent, Opacity = 0.35,
        });
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 5, Height = 5, Fill = accent,
        };
        System.Windows.Controls.Canvas.SetLeft(dot, size / 2 - 2.5);
        System.Windows.Controls.Canvas.SetTop(dot, -1);
        canvas.Children.Add(dot);
        return canvas;
    }

    private static void StartSpinner(System.Windows.FrameworkElement spinner)
    {
        spinner.Visibility = Visibility.Visible;
        if (spinner.RenderTransform is not System.Windows.Media.RotateTransform rt)
        {
            rt = new System.Windows.Media.RotateTransform(0);
            spinner.RenderTransform = rt;
            spinner.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        }
        var spin = new System.Windows.Media.Animation.DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1)))
        {
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
        };
        rt.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, spin);
    }

    private static void StopSpinner(System.Windows.FrameworkElement spinner)
    {
        if (spinner.RenderTransform is System.Windows.Media.RotateTransform rt)
            rt.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
        spinner.Visibility = Visibility.Collapsed;
    }

    /// <summary>Disposes a transient UI-built source (best-effort) so it releases any resources.</summary>
    private static void DisposeTransientSource(Phosphor.Plugin.Abstractions.IPhosphorSource? source)
    {
        try
        {
            switch (source)
            {
                case IAsyncDisposable ad: _ = ad.DisposeAsync(); break;
                case IDisposable d: d.Dispose(); break;
            }
        }
        catch { /* best-effort teardown */ }
    }

    /// <summary>
    /// Runs the live source's self-update (<see cref="Phosphor.Plugin.Abstractions.IUpdatable"/>,
    /// e.g. yt-dlp) via the VM and shows the result inline. Targets the active engine, not a
    /// transient, so the running app picks up the new version.
    /// </summary>
    private async Task UpdatePluginEngineAsync(
        System.Windows.Controls.Button button,
        System.Windows.Controls.TextBlock result)
    {
        button.IsEnabled = false;
        result.Text = "Checking…";
        result.Foreground = System.Windows.Media.Brushes.Gray;
        try
        {
            var vm = Owner?.DataContext as JukeboxViewModel;
            var status = vm != null
                ? await vm.UpdatePluginEngineOrLegacyAsync()
                : (await new YtDlpUpdater().UpdateAsync()).ToDisplayString();
            result.Text = status;
            result.Foreground = System.Windows.Media.Brushes.Gray;
            _settings.YtDlpLastUpdateCheck = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            result.Text = $"Update failed: {ex.Message}";
            result.Foreground = System.Windows.Media.Brushes.IndianRed;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    /// <summary>
    /// Invokes an <see cref="Phosphor.Plugin.Abstractions.IConfigurable"/> action for an instance
    /// (e.g. Plex "browse libraries"): harvests current edits, builds a transient source from the
    /// instance config, runs the action, shows a checkbox selection dialog, applies the result via
    /// the source, and merges the returned settings back into the instance. Re-renders the tab.
    /// </summary>
    private async Task InvokePluginConfigActionAsync(string instanceId, string actionId)
    {
        HarvestPluginSourcesTab();
        var cfg = _settings.PluginInstances.FirstOrDefault(c => c.InstanceId == instanceId);
        if (cfg == null) return;

        var source = Phosphor.Plugins.PluginSettingsFactory.BuildTransientSource(cfg, _pluginHttp);
        if (source is not Phosphor.Plugin.Abstractions.IConfigurable configurable) return;

        try
        {
            var selection = await configurable.InvokeConfigActionAsync(actionId);
            if (selection.Options.Count == 0)
            {
                MessageBox.Show(this, "Nothing to configure (no items returned).", "Phosphor",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var chosen = ShowConfigSelectionDialog(selection);
            if (chosen == null) return; // cancelled

            var updated = await configurable.ApplyConfigActionAsync(actionId, chosen, cfg.Settings);
            cfg.Settings = new Dictionary<string, string?>(updated);
            PopulatePluginSourcesTab();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Action failed: {ex.Message}", "Phosphor",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Shows a checkbox-list dialog for a <see cref="Phosphor.Plugin.Abstractions.ConfigSelection"/>,
    /// rendering each option with any indented sub-option checkboxes. Returns the per-option results
    /// (selected + chosen sub-option ids), or null if cancelled.
    /// </summary>
    private List<Phosphor.Plugin.Abstractions.ConfigOptionResult>? ShowConfigSelectionDialog(
        Phosphor.Plugin.Abstractions.ConfigSelection selection)
    {
        var dlg = new Window
        {
            Title = selection.Title ?? "Select",
            Owner = this,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 640,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush"),
        };
        var text = (System.Windows.Media.Brush)FindResource("TextBrush");
        var dim = (System.Windows.Media.Brush)FindResource("TextDimBrush");

        var root = new System.Windows.Controls.DockPanel { Margin = new Thickness(12) };

        // Buttons docked at the bottom so the list scrolls independently.
        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        System.Windows.Controls.DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Bottom);

        var list = new System.Windows.Controls.StackPanel();
        var rows = new List<(System.Windows.Controls.CheckBox Main, string OptId,
            List<(System.Windows.Controls.CheckBox Box, string SubId)> Subs)>();

        foreach (var opt in selection.Options)
        {
            var main = new System.Windows.Controls.CheckBox
            {
                Content = opt.Label, IsChecked = opt.IsSelected, Foreground = text,
                Margin = new Thickness(0, 5, 0, 2), FontWeight = FontWeights.SemiBold,
            };
            list.Children.Add(main);

            var subBoxes = new List<(System.Windows.Controls.CheckBox, string)>();
            foreach (var sub in opt.SubOptions ?? [])
            {
                var subCb = new System.Windows.Controls.CheckBox
                {
                    Content = sub.Label, IsChecked = sub.IsSelected, Foreground = dim,
                    Margin = new Thickness(22, 1, 0, 1), FontSize = 12,
                };
                subBoxes.Add((subCb, sub.Id));
                list.Children.Add(subCb);
            }
            rows.Add((main, opt.Id, subBoxes));
        }

        bool ok = false;
        var okBtn = new System.Windows.Controls.Button { Content = "OK", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        okBtn.Click += (_, _) => { ok = true; dlg.Close(); };
        var cancelBtn = new System.Windows.Controls.Button { Content = "Cancel", Padding = new Thickness(12, 3, 12, 3), IsCancel = true };
        cancelBtn.Click += (_, _) => dlg.Close();
        buttons.Children.Add(okBtn);
        buttons.Children.Add(cancelBtn);

        root.Children.Add(buttons);
        root.Children.Add(new System.Windows.Controls.ScrollViewer
        {
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            Content = list,
        });

        dlg.Content = root;
        dlg.ShowDialog();

        if (!ok) return null;
        return rows.Select(r => new Phosphor.Plugin.Abstractions.ConfigOptionResult(
            r.OptId,
            r.Main.IsChecked == true,
            r.Subs.Where(s => s.Box.IsChecked == true).Select(s => s.SubId).ToList()))
            .ToList();
    }

    /// <summary>Adds a two-column row (label | editor) to a settings grid.</summary>
    private static void AddSettingRow(
        System.Windows.Controls.Grid grid, string label, string? helpText,
        System.Windows.UIElement editor, System.Windows.Media.Brush text, System.Windows.Media.Brush dim)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });

        var labelBlock = new System.Windows.Controls.TextBlock
        {
            Text = label,
            Foreground = text,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 10, 0),
        };
        if (!string.IsNullOrWhiteSpace(helpText))
            labelBlock.ToolTip = helpText;
        System.Windows.Controls.Grid.SetRow(labelBlock, row);
        System.Windows.Controls.Grid.SetColumn(labelBlock, 0);
        grid.Children.Add(labelBlock);

        if (editor is System.Windows.FrameworkElement fe)
            fe.Margin = RowMargin;
        System.Windows.Controls.Grid.SetRow(editor, row);
        System.Windows.Controls.Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
    }

    private System.Windows.Controls.ComboBox MakeEnumCombo(
        Phosphor.Plugin.Abstractions.PluginSettingDescriptor d, string? current,
        System.Windows.Media.Brush text, System.Windows.Media.Brush background)
    {
        var combo = new System.Windows.Controls.ComboBox
        {
            Foreground = text,
            Background = background,
            Height = EditorHeight,
            Padding = EditorPadding,
            MinWidth = EditorMinWidth,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
        };
        foreach (var v in d.EnumValues ?? [])
            combo.Items.Add(v);
        if (current != null && combo.Items.Contains(current))
            combo.SelectedItem = current;
        else if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
        return combo;
    }

    /// <summary>
    /// Harvests the editable Plug-ins tab controls back into the working configs and writes them to
    /// <c>_settings.PluginInstances</c>. Secrets are only overwritten when the user typed a value.
    /// </summary>
    private void HarvestPluginSourcesTab()
    {
        if (_pluginWorkingConfigs.Count == 0) return;

        foreach (var cfg in _pluginWorkingConfigs)
        {
            if (_pluginEnabledBoxes.TryGetValue(cfg.InstanceId, out var en))
                cfg.Enabled = en.IsChecked == true;
            if (_pluginDisplayNameBoxes.TryGetValue(cfg.InstanceId, out var nb))
                cfg.DisplayName = string.IsNullOrWhiteSpace(nb.Text) ? null : nb.Text.Trim();
            if (_pluginCachingBoxes.TryGetValue(cfg.InstanceId, out var cache))
                cfg.AllowCaching = ((cache.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string) switch
                {
                    "true" => true,
                    "false" => false,
                    _ => null,
                };
        }

        foreach (var (control, instanceId, key) in _pluginFieldControls)
        {
            var cfg = _pluginWorkingConfigs.FirstOrDefault(c => c.InstanceId == instanceId);
            if (cfg == null) continue;

            switch (control)
            {
                case System.Windows.Controls.PasswordBox pb:
                    // Overwrite the stored secret only when the user actually changed it (a value
                    // still equal to the pre-filled sentinel means "unchanged").
                    if (!string.IsNullOrEmpty(pb.Password) && pb.Password != SecretSentinel)
                        cfg.Settings[key] = pb.Password;
                    break;
                case System.Windows.Controls.CheckBox cb:
                    cfg.Settings[key] = (cb.IsChecked == true).ToString();
                    break;
                case System.Windows.Controls.ComboBox combo:
                    cfg.Settings[key] = combo.SelectedItem?.ToString() ?? "";
                    break;
                case System.Windows.Controls.TextBox tb:
                    cfg.Settings[key] = tb.Text;
                    break;
            }
        }

        // Custom editors (folder-path / multi-value list) harvest via their getter.
        foreach (var (instanceId, key, getValue) in _pluginCustomFieldGetters)
        {
            var cfg = _pluginWorkingConfigs.FirstOrDefault(c => c.InstanceId == instanceId);
            if (cfg == null) continue;
            cfg.Settings[key] = getValue();
        }

        _settings.PluginInstances = _pluginWorkingConfigs
            .Select(c => new Phosphor.Plugins.PluginInstanceConfig
            {
                TypeId = c.TypeId,
                InstanceId = c.InstanceId,
                DisplayName = c.DisplayName,
                Enabled = c.Enabled,
                Settings = new Dictionary<string, string?>(c.Settings),
                AllowCaching = c.AllowCaching,
            })
            .ToList();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DisableTestMode();
        Saved = false;
        StopDInputCapture();
        if (_testDofClient != null && _testDofClient != _sharedDofClient)
            _testDofClient.Dispose();
        _testDofClient = null;
        _backglassProxy?.SetScreensaverSettings(_originalBackglassIntensity, _originalBackglassSpeed);
        _playfieldProxy?.SetScreensaverSettings(_originalPlayfieldIntensity, _originalPlayfieldSpeed);
        _topperProxy?.SetScreensaverSettings(_originalTopperIntensity, _originalTopperSpeed);
        _topperProxy?.SetDistortion(_originalDistortion);
        _topperProxy?.SetScreenScaling(_originalScreenScaling);
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
                    Content = DmdWindow.MakeIconContent(emoji, 20, _settings.DmdIconStyle),
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
        if (item.IsSpecial || item.IsPlex || item.IsPlaylist) return;

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

    private void RefreshMonitorInfo()
    {
        if (PanelMonitorInfo == null) return;
        PanelMonitorInfo.Children.Clear();

        // Gather window-to-monitor mappings
        var windowsByDevice = new Dictionary<string, List<string>>();
        void MapWindow(string name, Func<(nint hwnd, double w, double h)?> getInfo)
        {
            try
            {
                var info = getInfo();
                if (info == null || info.Value.hwnd == nint.Zero) return;
                var screen = System.Windows.Forms.Screen.FromHandle(info.Value.hwnd);
                var device = screen.DeviceName;
                if (!windowsByDevice.TryGetValue(device, out var list))
                {
                    list = new List<string>();
                    windowsByDevice[device] = list;
                }
                list.Add($"{name} ({info.Value.w:0}×{info.Value.h:0})");
            }
            catch { /* cross-thread or disposed — skip */ }
        }

        // Playfield and Backglass run on their own threads — must Invoke to read HWND/size
        if (_playfieldProxy != null)
            MapWindow("Playfield", () => _playfieldProxy.Dispatcher.Invoke(() =>
            {
                var w = _playfieldProxy.Window;
                if (!w.IsVisible) return ((nint, double, double)?)null;
                return (new System.Windows.Interop.WindowInteropHelper(w).Handle, w.ActualWidth, w.ActualHeight);
            }));

        if (_backglassProxy != null)
            MapWindow("Backglass", () => _backglassProxy.Dispatcher.Invoke(() =>
            {
                var w = _backglassProxy.Window;
                if (!w.IsVisible) return ((nint, double, double)?)null;
                return (new System.Windows.Interop.WindowInteropHelper(w).Handle, w.ActualWidth, w.ActualHeight);
            }));

        if (_topperProxy != null)
            MapWindow("Topper", () => _topperProxy.GetWindowInfo());

        if (Application.Current.MainWindow is JukeboxWindow dmd && dmd.IsVisible)
            MapWindow("DMD", () =>
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(dmd).Handle;
                return (hwnd, dmd.ActualWidth, dmd.ActualHeight);
            });

        // Enumerate all monitors — use DEVMODE for accurate resolution
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var device = screen.DeviceName;
            var dm = new DEVMODEW_About { dmSize = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODEW_About>() };
            EnumDisplaySettingsW(device, -1, ref dm);
            int hz = (int)dm.dmDisplayFrequency;
            int resW = (int)dm.dmPelsWidth;
            int resH = (int)dm.dmPelsHeight;
            string primary = screen.Primary ? " (primary)" : "";

            var header = new System.Windows.Controls.TextBlock
            {
                Text = $"{device}{primary} — {resW}×{resH} @ {hz} Hz",
                Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0),
            };
            PanelMonitorInfo.Children.Add(header);

            if (windowsByDevice.TryGetValue(device, out var windows))
            {
                foreach (var w in windows)
                {
                    PanelMonitorInfo.Children.Add(new System.Windows.Controls.TextBlock
                    {
                        Text = $"    └ {w}",
                        Foreground = (System.Windows.Media.Brush)FindResource("TextDimBrush"),
                        FontSize = 10,
                        Margin = new Thickness(0, 1, 0, 0),
                    });
                }
            }
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct DEVMODEW_About
    {
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? lpszDeviceName, int iModeNum, ref DEVMODEW_About lpDevMode);
}
