using System.Text.Json;

namespace Phosphor;

public class WindowLayout
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsExpanded { get; set; }
}

public class AppSettings
{
    public WindowLayout Playfield { get; set; } = new() { Left = 0, Top = 0, Width = 600, Height = 800 };
    public WindowLayout Backglass { get; set; } = new() { Left = 620, Top = 0, Width = 800, Height = 600 };
    public WindowLayout Dmd { get; set; } = new() { Left = 1440, Top = 0, Width = 800, Height = 600 };
    public PlayfieldMode PlayfieldDisplayMode { get; set; } = PlayfieldMode.Screensaver;
    public string PlayfieldStaticImagePath { get; set; } = "";
    public string PlayfieldVideoPath { get; set; } = "";
    public bool ShowVideoInfo { get; set; }
    public bool ShowBackglass { get; set; } = true;
    public bool ShowPlayfield { get; set; } = true;
    public bool ShowTopper { get; set; }
    public WindowLayout Topper { get; set; } = new() { Left = 0, Top = 0, Width = 800, Height = 300 };
    public WindowLayout? SettingsWindowLayout { get; set; }
    public bool ResizableWindows { get; set; } = true;
    public bool SetCursorOnLaunch { get; set; }
    public bool MoveCursorToSettings { get; set; }
    public bool CheckWindowsOnStartup { get; set; } = true;
    public bool CacheEnabled { get; set; }
    public double CacheMaxSizeGb { get; set; } = 5.0;
    public int CacheMaxClipLengthMinutes { get; set; }
    public CacheMode CacheMode { get; set; } = CacheMode.Playlists;
    public bool ThumbnailCacheEnabled { get; set; } = true;
    public double ThumbnailCacheMaxSizeMb { get; set; } = 500;
    public bool CategoryCacheEnabled { get; set; } = true;
    public int CategoryCacheMaxAgeHours { get; set; } = 168;
    public bool YtPlaylistCacheEnabled { get; set; } = true;
    public int YtPlaylistCacheMaxAgeHours { get; set; } = 168;
    public bool PlexPlaylistCacheEnabled { get; set; } = true;
    public int PlexPlaylistCacheMaxAgeHours { get; set; } = 168;
    public KeyBindings KeyBindings { get; set; } = new();
    public int ResultColumns { get; set; } = 2;
    public int ResultFontSizeModifier { get; set; }
    public double ScreensaverIntensity { get; set; } = 0.50;
    public double ScreensaverSpeed { get; set; } = 1.0;
    public bool DmdScreensaver { get; set; }
    public bool DmdScreensaverDimEnabled { get; set; }
    public int DmdScreensaverDimOpacity { get; set; } = 80;
    public int DmdScreensaverDimTimeoutSeconds { get; set; } = 60;
    public bool DmdScreensaverDimDarkBlobs { get; set; } = true;
    public DmdSwapMode DmdSwapTarget { get; set; }
    public bool ApplyDefaultDmdOnSwap { get; set; }
    public bool BackglassLogoDimEnabled { get; set; }
    public int BackglassLogoDimOpacity { get; set; } = 80;
    public int BackglassLogoDimTimeoutSeconds { get; set; } = 60;
    public LogoColorMode LogoColorMode { get; set; }
    public bool BackglassAudioOnly { get; set; }
    public bool LogoSpin { get; set; } = true;
    public bool LogoShadow { get; set; } = true;
    public LogoRingsMode LogoRings { get; set; } = LogoRingsMode.Standard;
    public int LogoRingsBrightness { get; set; } = 25;
    public int LogoBrightness { get; set; } = 100;
    public double TopperDistortion { get; set; }
    public double TopperScreenScaling { get; set; }
    public bool TopperLogoSpin { get; set; } = true;
    public bool TopperLogoShadow { get; set; } = true;
    public LogoRingsMode TopperLogoRings { get; set; } = LogoRingsMode.Standard;
    public int TopperLogoRingsBrightness { get; set; } = 25;
    public int TopperLogoBrightness { get; set; } = 100;
    public LogoColorMode TopperLogoColorMode { get; set; }
    public bool ShowStatusText { get; set; } = true;
    public int HideCursorTimeoutSeconds { get; set; } = 15;
    public int OledSleepDefeatSeconds { get; set; }
    public int OledSleepDefeatDurationSeconds { get; set; } = 5;
    public int OledSleepDefeatIntensity { get; set; } = 80;
    public bool PlayfieldPulseDominantBlobs { get; set; }
    public BlobPattern PlayfieldBlobPattern { get; set; } = BlobPattern.Random;
    public int PlayfieldBlobCount { get; set; } = 10;
    public int PlayfieldBlobSizeOffset { get; set; } = 10;
    public int PlayfieldRotation { get; set; } = 270;
    public BlobPattern BackglassBlobPattern { get; set; } = BlobPattern.RoughClockwise;
    public int BackglassBlobCount { get; set; } = 6;
    public int BackglassBlobSizeOffset { get; set; } = 10;
    public BlobPattern TopperBlobPattern { get; set; } = BlobPattern.Random;
    public int TopperBlobCount { get; set; } = 4;
    public int TopperBlobSizeOffset { get; set; } = 10;
    public bool ReactiveBlobsPlayfield { get; set; }
    public bool ReactiveBlobsBackglass { get; set; }
    public bool ReactiveBlobsTopper { get; set; }
    public bool ReactiveBlobsDmd { get; set; }
    public bool ReactiveProjectM { get; set; } = true;
    public double ReactivityThreshold { get; set; } = 0.10;
    public int ReactiveSpeedMs { get; set; } = 120;
    public double ReactiveOverdrive { get; set; } = 1.0;
    public BlobPattern DmdBlobPattern { get; set; } = BlobPattern.Random;
    public int DmdBlobCount { get; set; } = 6;
    public int DmdBlobSizeOffset { get; set; } = 10;
    public bool ExcludeMandelbrotFromRandom { get; set; } = true;
    public int MandelbrotTickIntervalMs { get; set; }
    public bool MandelbrotUseScreenRate { get; set; }
    public double MandelbrotRenderScale { get; set; } = 0.6;
    public bool MandelbrotAdaptiveIterations { get; set; } = true;
    public int MandelbrotMaxIterations { get; set; } = 256;
    public int MandelbrotUseGpu { get; set; }
    public double MandelbrotPerturbation { get; set; }
    public bool MandelbrotDiscovery { get; set; }
    public double MandelbrotDimming { get; set; }
    public bool MandelbrotHistogramColoring { get; set; }
    /// <summary>0 = Off, 1 = Random per target, 2 = Slow spin.</summary>
    public int MandelbrotRotation { get; set; }
    /// <summary>Color scheme: 0 = Psychedelic, 1 = Ocean, 2 = Ember, 3 = Midnight, 4 = Forest.</summary>
    public int MandelbrotColorScheme { get; set; }
    public bool ExcludeProjectMFromRandom { get; set; } = true;

    // Matrix tuning
    public bool MatrixColorCycling { get; set; } = true;
    public bool MatrixInfiniteZoom { get; set; }
    public double MatrixZoomRate { get; set; } = 0.05;

    // Clock tuning
    /// <summary>0 = Analog, 1 = Digital dot-matrix.</summary>
    public int ClockMode { get; set; }
    /// <summary>Clock brightness override (0.05–1.0). Overrides global blob intensity for the clock visual.</summary>
    public double ClockBrightness { get; set; } = 0.5;
    /// <summary>Digital clock font size scale in 10% increments (1–20). 10 = 100% default.</summary>
    public int ClockDigitalSize { get; set; } = 35;
    public bool ClockUse24Hour { get; set; } = true;
    public int ClockAnalogSize { get; set; } = 2;
    /// <summary>0 = Modern, 1 = Traditional (with hand trail blobs).</summary>
    public int ClockAnalogStyle { get; set; }

    // Game of Life tuning
    public int GameOfLifeCellSize { get; set; } = 5;
    public int GameOfLifeTickIntervalMs { get; set; } = 100;
    public bool GameOfLifeUseScreenRate { get; set; }
    public int GameOfLifeFadeGenerations { get; set; } = 6;
    public int GameOfLifeHeatBoost { get; set; } = 60;
    public int GameOfLifeDensity { get; set; } = 5;
    public bool GameOfLifeCameraRoam { get; set; } = true;
    public double GameOfLifeCameraMaxZoom { get; set; } = 1.6;
    public int GameOfLifeCameraOverscan { get; set; } = 50;
    /// <summary>Multiplier on camera pan/zoom/rotation animation speed. 1.0 = default. Range 0.1–3.0.</summary>
    public double GameOfLifeCameraSpeed { get; set; } = 1.0;
    /// <summary>Restart the Game of Life simulation whenever a new track starts.</summary>
    public bool GameOfLifeRestartOnTrackChange { get; set; } = false;
    public int GameOfLifeScalingMode { get; set; } // 0 = NearestNeighbor, 1 = Fant
    /// <summary>Color model for births. 0 = Genetic (blend of parent colors), 1 = EraBanded (current global rotating hue).</summary>
    public int GameOfLifeColorMode { get; set; } // 0 = Genetic, 1 = GeneticVivid, 2 = EraBanded
    /// <summary>Cellular-automaton rule engine. 0 = Conway, 1 = Brian's Brain, 2 = Star Wars.</summary>
    public int GameOfLifeRulesEngine { get; set; } // 0 = Conway, 1 = BriansBrain, 2 = StarWars
    /// <summary>
    /// Multiplier on the EraBanded hue rotation speed. 1.0 = original ~60s full
    /// ROYGBIV cycle. Higher = faster, finer bands. Only used in EraBanded color mode.
    /// </summary>
    public double GameOfLifeEraBandedHueSpeed { get; set; } = 1.0;
    /// <summary>
    /// When true, periodically nudges or disrupts small still-life / oscillator patterns
    /// (blinkers, blocks, beacons) so the simulation keeps evolving rather than locking
    /// into static repeating shapes. Off by default for Conway purists.
    /// </summary>
    public bool GameOfLifeAntiStagnation { get; set; } = false;
    /// <summary>
    /// Aggressiveness of anti-stagnation interventions (1–10). 5 = subtle (default),
    /// 1 = barely noticeable, 10 = constant churn. Only used when
    /// <see cref="GameOfLifeAntiStagnation"/> is true.
    /// </summary>
    public int GameOfLifeAntiStagnationIntensity { get; set; } = 5;
    /// <summary>
    /// Custom B/S rule string, e.g. "B3/S23" for Conway, "B36/S23" for HighLife.
    /// Parsed into birth/survival bitmasks at runtime. Default is Conway.
    /// </summary>
    public string GameOfLifeCustomRule { get; set; } = "B3/S23";
    /// <summary>
    /// Bitmask of allowed ROYGBIV seed color bands (bits 0–6 = R,O,Y,G,B,I,V).
    /// 0 or 0x7F = all colors enabled. Used in Genetic color modes only.
    /// </summary>
    public int GameOfLifeSeedColorMask { get; set; } = 0x7F;

    /// <summary>Hue spread within each seed color band (0 = exact center, 60 = full width). Default 60.</summary>
    public int GameOfLifeHueSpread { get; set; } = 60;

    /// <summary>Seed spread mode (0 = Clustered, 1 = Scattered, 2 = Full). Default 0.</summary>
    public int GameOfLifeSeedSpread { get; set; } = 0;

    // Gravity tuning
    /// <summary>Gravitational constant (100–800). Default 400.</summary>
    public int GravityG { get; set; } = 400;
    /// <summary>Close-range orbit repulsion strength (0–6). 0 = off.</summary>
    public double GravityOrbitRepulsion { get; set; } = 3.0;
    /// <summary>Central gravity pull toward canvas center (2–30). Default 6.</summary>
    public double GravityCentralGravity { get; set; } = 6.0;
    /// <summary>Continuous orbital perturbation strength (0–10). Keeps bodies swirling after merges. 0 = off, 3 = default.</summary>
    public double GravityOrbitalPerturbation { get; set; } = 3.0;
    /// <summary>Whether camera roam is enabled for the Gravity visualization.</summary>
    public bool GravityCameraRoam { get; set; } = true;
    /// <summary>Whether to restart the Gravity simulation when a new track starts.</summary>
    public bool GravityRestartOnTrackChange { get; set; }
    /// <summary>Blob count multiplier for Gravity (0.5–10). Scales max body count. Default 1.0.</summary>
    public double GravityBlobMultiplier { get; set; } = 1.0;
    /// <summary>Whether to show diagnostic overlay (zoom, body count) on Gravity visualization.</summary>
    public bool GravityShowDiagnostics { get; set; }
    /// <summary>Supernova mass threshold as diameter in pixels (60–400). When a merged body reaches this size it explodes. 0 = disabled. Default 150.</summary>
    public double GravitySupernovaMass { get; set; } = 150.0;
    /// <summary>Universe density (0=Low, 1=Medium, 2=High). Controls how aggressively new bodies are injected. Default 1 (Medium).</summary>
    public int GravityDensity { get; set; } = 1;

    // Ferrofluid tuning
    public double FerrofluidCoreGravity { get; set; } = 280.0;
    public double FerrofluidMutualAttraction { get; set; } = 40.0;
    public double FerrofluidDamping { get; set; } = 0.97;
    public double FerrofluidExplosionForce { get; set; } = 800.0;
    public double FerrofluidExplosionDuration { get; set; } = 0.8;
    public double FerrofluidBristleForce { get; set; } = 150.0;
    public double FerrofluidMaxSpeed { get; set; } = 900.0;
    public double FerrofluidExplosionBassThreshold { get; set; } = 0.2;
    public double FerrofluidBristleTrebleThreshold { get; set; } = 0.3;

    public double ProjectMPresetDuration { get; set; } = 30.0;
    public double ProjectMSoftCutDuration { get; set; } = 3.0;
    public bool ProjectMHardCutEnabled { get; set; } = true;
    public bool ProjectMNewVisualOnTrackChange { get; set; }
    public int ProjectMMeshSize { get; set; } = 48;
    public double ProjectMRenderScale { get; set; } = 0.5;
    public float ProjectMBeatSensitivity { get; set; } = 1.0f;
    public string ProjectMPresetPath { get; set; } = "";
    public string ProjectMTexturePath { get; set; } = "";
    public List<string> ProjectMEnabledFolders { get; set; } = new();
    public double ProjectMColorSampleDelaySeconds { get; set; } = 1.0;
    public bool ProjectMPresetHistoryEnabled { get; set; } = true;
    /// <summary>
    /// 0 = off, 1 = log black presets, 2 = log and move to Deactivated folder.
    /// </summary>
    public int ProjectMPresetMonitor { get; set; }
    /// <summary>
    /// When true, projectM renders via the software readback path (glReadPixels + WriteableBitmap).
    /// When false, attempts the zero-copy D3D9/GL shared-surface path. The shared-surface path is
    /// faster but renders black on some NVIDIA + WPF D3DImage configurations.
    /// </summary>
    public bool ProjectMSoftwareRender { get; set; } = true;
    public int ProjectMPresetMonitorBlackHits { get; set; } = 5;
    public double ProjectMPresetMonitorIntervalSeconds { get; set; } = 2.5;
    public double ProjectMPresetMonitorPercentile { get; set; } = 5.0;
    public double ProjectMPresetMonitorThreshold { get; set; } = 4.0;
    /// <summary>
    /// When true, saves a PNG snapshot of each confirmed black frame for diagnostics.
    /// </summary>
    public bool ProjectMPresetMonitorSaveBlackFrame { get; set; }
    /// <summary>
    /// When true, saves a PNG snapshot of each color-sampled frame for diagnostics.
    /// </summary>
    public bool ProjectMSaveColorSampleFrame { get; set; }
    public IconStyle DmdIconStyle { get; set; } = IconStyle.Default;
    public int DmdRotation { get; set; }
    public QueuePosition DmdQueuePosition { get; set; } = QueuePosition.Right;
    public int DmdNowPlayingAreaSizeModifier { get; set; }
    public int DmdPlayButtonSizeModifier { get; set; }
    public int DmdGenreIconSizeModifier { get; set; }
    public int DmdGenreIconSpacingModifier { get; set; }
    public int DmdGenreIconPaddingModifier { get; set; }
    public int DmdTrackButtonSizeModifier { get; set; }
    public MinorButtonLocation DmdMinorButtonLocation { get; set; } = MinorButtonLocation.Queue;
    public int DmdHeaderSizeModifier { get; set; }
    public int DmdSearchBarSizeModifier { get; set; }
    public int DmdSearchResultsNavSizeModifier { get; set; }
    public int QueueFontSizeModifier { get; set; }
    public int DmdQueueButtonSizeModifier { get; set; }
    public double DmdQueueSplitterSize { get; set; } = -1;
    public string TitleText { get; set; } = "\uD83C\uDFB5 PHOSPHOR";
    public string LogoText { get; set; } = "\u2022 PHOSPHOR \u2022 PHOSPHOR ";
    public bool PrefetchEnabled { get; set; } = true;
    public VideoQualityPreference VideoQuality { get; set; } = VideoQualityPreference.High;
    public bool StereoAudio { get; set; } = true;
    public int Volume { get; set; } = 100;

    // Network
    public int NetworkCachingMs { get; set; } = 2000;
    public int LiveCachingMs { get; set; } = 1000;
    public int FileCachingMs { get; set; } = 300;
    public bool HttpReconnect { get; set; } = true;

    // Plex integration
    public string PlexServerUrl { get; set; } = "";
    public string PlexToken { get; set; } = "";
    public List<PlexLibraryMapping> PlexLibraries { get; set; } = [];
    public bool PlexStereoAudio { get; set; }
    public bool PlexGaplessPlayback { get; set; }
    public bool RepeatEnabled { get; set; }
    public bool AutoDjEnabled { get; set; }
    public bool AutoPlayQueueOnStart { get; set; }
    /// <summary>
    /// Legacy single-path setting. Retained for backward compatibility with older
    /// settings files. When loaded, it is merged into <see cref="StartupDittiPaths"/>
    /// and cleared on next save.
    /// </summary>
    public string StartupDittiPath { get; set; } = "";
    /// <summary>
    /// List of audio file paths to use as startup ditties. A random entry is chosen
    /// each launch. Paths may be relative (resolved against the app base directory,
    /// allowing portable settings) or absolute.
    /// </summary>
    public List<string> StartupDittiPaths { get; set; } = [];
    public bool EnableStartupDitti { get; set; }
    public int LastQueueIndex { get; set; } = -1;
    public bool DofEnabled { get; set; }
    public bool DofSimulator { get; set; }
    public string DofRomName { get; set; } = "vpinjukebox";
    public bool DofColorBand { get; set; }
    public bool DofPresetChanged { get; set; }
    public bool DebugLogging { get; set; }

    /// <summary>
    /// Genre/category names that have been hidden by the user from the DMD home screen.
    /// </summary>
    public List<string> HiddenCategories { get; set; } = [];

    private static readonly string SettingsPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "settings.json");

    private static readonly string DefaultSettingsPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "default_settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// The default settings loaded from default_settings.json (or code defaults if the file is missing).
    /// Always available for reference by other features.
    /// </summary>
    public static AppSettings Defaults { get; private set; } = new();

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                File.WriteAllText(SettingsPath, json);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(100);
            }
            catch (IOException ex)
            {
                DebugLog.Log("Settings", $"Save failed after retries: {ex.Message}");
            }
        }
    }

    public Task SaveAsync()
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        return Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await File.WriteAllTextAsync(SettingsPath, json).ConfigureAwait(false);
                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    DebugLog.Log("Settings", $"SaveAsync failed after retries: {ex.Message}");
                }
            }
        });
    }

    public void SaveDefaults()
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(DefaultSettingsPath, json);
        Defaults = LoadDefaults();
    }

    public static AppSettings Load()
    {
        Defaults = LoadDefaults();

        if (File.Exists(SettingsPath))
        {
            try
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json) ?? Defaults;
                // Migrate legacy single StartupDittiPath into the list-based StartupDittiPaths
                if (!string.IsNullOrWhiteSpace(loaded.StartupDittiPath) &&
                    !loaded.StartupDittiPaths.Contains(loaded.StartupDittiPath))
                {
                    loaded.StartupDittiPaths.Insert(0, loaded.StartupDittiPath);
                }
                loaded.StartupDittiPath = "";
                return loaded;
            }
            catch
            {
                return Defaults;
            }
        }

        return Defaults;
    }

    private static AppSettings LoadDefaults()
    {
        if (!File.Exists(DefaultSettingsPath))
            return new AppSettings();

        try
        {
            var json = File.ReadAllText(DefaultSettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }
}

public enum PlayfieldMode
{
    Blank,
    Screensaver,
    StaticImage,
    Video
}

public enum VideoQualityPreference
{
    Low,    // up to 480p
    Medium, // up to 720p
    High,   // up to 1080p
    Max     // best available
}

public enum LogoRingsMode
{
    Off,
    Reduced,
    Standard
}

public enum BlobPattern
{
    Random,
    RoughClockwise,
    PerfectClockwise,
    RoughMixed,
    PerfectMixed,
    Rainfall,
    LavaLamp,
    Bounce,
    LightCycle,
    Fractal,
    FractalBox,
    Mandelbrot,
    ProjectM,
    FerrofluidCluster,
    Matrix,
    GameOfLife,
    Gravity,
    Clock,
    RandomPerSong
}

public enum QueuePosition
{
    Right,
    Bottom
}

public enum MinorButtonLocation
{
    Playbar,
    Queue
}

public enum PlayButtonSize
{
    ExtraSmall,
    Small,
    Normal,
    Large,
    ExtraLarge
}

public enum DmdSwapMode
{
    Off,
    Playfield,
    Backglass
}

public enum LogoColorMode
{
    Off,
    SlowMorph,
    Reactive
}

public enum CacheMode
{
    Playlists,
    Everything
}

public enum IconStyle
{
    Default,
    Colorful
}

/// <summary>
/// Maps a Plex library to a category tile.
/// </summary>
public class PlexLibraryMapping
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Type { get; set; } = "";
    public bool HubsEnabled { get; set; }
    public bool PlaylistsEnabled { get; set; }
}
