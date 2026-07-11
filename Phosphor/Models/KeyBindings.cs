using Key = System.Windows.Input.Key;

namespace Phosphor;

/// <summary>
/// Logical actions that can be triggered by cabinet buttons.
/// </summary>
public enum JukeboxAction
{
    NavLeft,
    NavRight,
    NavUp,
    NavDown,
    Select,
    Back,
    Skip,
    StopPlayback,
    FavToggle,
    OpenSettings,
    QueueSelected,
    ToggleAutoDj,
    SeekForward,
    SeekBack,
    ExitApp,
    Pause,
    CreatePlaylistFromQueue,
    ToggleShuffle,
    ToggleRepeat,
    FocusSearch,
    Home,
    TogglePlayStop,
    OpenPresetBrowser,
    ToggleResizableWindows,
}

/// <summary>
/// A single action binding with a primary keyboard key and an optional
/// secondary "cabinet button" — either a keyboard key or a DirectInput joystick button.
/// </summary>
public class ActionBinding
{
    public Key PrimaryKey { get; set; } = Key.None;
    public Key CabinetButton { get; set; } = Key.None;

    /// <summary>DirectInput device instance GUID (null = not bound to DInput).</summary>
    public Guid? CabinetDInputDeviceGuid { get; set; }
    /// <summary>DirectInput button index on the device (null = not bound).</summary>
    public int? CabinetDInputButton { get; set; }

    public bool HasDInputBinding => CabinetDInputDeviceGuid.HasValue && CabinetDInputButton.HasValue;
}

/// <summary>
/// Maps keyboard keys to jukebox actions. Each action has a primary key
/// and an optional secondary cabinet button. Persisted in settings.json.
/// </summary>
public class KeyBindings
{
    public ActionBinding NavLeft { get; set; } = new() { PrimaryKey = Key.LeftShift, CabinetButton = Key.None };
    public ActionBinding NavRight { get; set; } = new() { PrimaryKey = Key.RightShift, CabinetButton = Key.None };
    public ActionBinding NavUp { get; set; } = new() { PrimaryKey = Key.Up, CabinetButton = Key.None };
    public ActionBinding NavDown { get; set; } = new() { PrimaryKey = Key.Down, CabinetButton = Key.None };
    public ActionBinding Select { get; set; } = new() { PrimaryKey = Key.Enter, CabinetButton = Key.None };
    public ActionBinding Back { get; set; } = new() { PrimaryKey = Key.Back, CabinetButton = Key.None };
    public ActionBinding Skip { get; set; } = new() { PrimaryKey = Key.N, CabinetButton = Key.None };
    public ActionBinding StopPlayback { get; set; } = new() { PrimaryKey = Key.S, CabinetButton = Key.None };
    public ActionBinding FavToggle { get; set; } = new() { PrimaryKey = Key.F, CabinetButton = Key.None };
    public ActionBinding OpenSettings { get; set; } = new() { PrimaryKey = Key.F10, CabinetButton = Key.None };
    public ActionBinding QueueSelected { get; set; } = new() { PrimaryKey = Key.Q, CabinetButton = Key.None };
    public ActionBinding ToggleAutoDj { get; set; } = new() { PrimaryKey = Key.D, CabinetButton = Key.None };
    public ActionBinding SeekForward { get; set; } = new() { PrimaryKey = Key.OemCloseBrackets, CabinetButton = Key.None };
    public ActionBinding SeekBack { get; set; } = new() { PrimaryKey = Key.OemOpenBrackets, CabinetButton = Key.None };
    public ActionBinding ExitApp { get; set; } = new() { PrimaryKey = Key.Escape, CabinetButton = Key.None };
    public ActionBinding Pause { get; set; } = new() { PrimaryKey = Key.OemPeriod, CabinetButton = Key.None };
    public ActionBinding CreatePlaylistFromQueue { get; set; } = new() { PrimaryKey = Key.P, CabinetButton = Key.None };
    public ActionBinding ToggleShuffle { get; set; } = new() { PrimaryKey = Key.H, CabinetButton = Key.None };
    public ActionBinding ToggleRepeat { get; set; } = new() { PrimaryKey = Key.R, CabinetButton = Key.None };
    public ActionBinding FocusSearch { get; set; } = new() { PrimaryKey = Key.OemQuestion, CabinetButton = Key.None };
    public ActionBinding Home { get; set; } = new() { PrimaryKey = Key.Home, CabinetButton = Key.None };
    public ActionBinding TogglePlayStop { get; set; } = new() { PrimaryKey = Key.Space, CabinetButton = Key.None };
    public ActionBinding OpenPresetBrowser { get; set; } = new() { PrimaryKey = Key.B, CabinetButton = Key.None };
    public ActionBinding ToggleResizableWindows { get; set; } = new() { PrimaryKey = Key.None, CabinetButton = Key.None };

    /// <summary>
    /// Try to resolve a key press to a logical action.
    /// Checks both primary key and cabinet button for each action.
    /// </summary>
    public bool TryGetAction(Key key, out JukeboxAction action)
    {
        if (Matches(NavLeft, key)) { action = JukeboxAction.NavLeft; return true; }
        if (Matches(NavRight, key)) { action = JukeboxAction.NavRight; return true; }
        if (Matches(NavUp, key)) { action = JukeboxAction.NavUp; return true; }
        if (Matches(NavDown, key)) { action = JukeboxAction.NavDown; return true; }
        if (Matches(Select, key)) { action = JukeboxAction.Select; return true; }
        if (Matches(Back, key)) { action = JukeboxAction.Back; return true; }
        if (Matches(Skip, key)) { action = JukeboxAction.Skip; return true; }
        if (Matches(StopPlayback, key)) { action = JukeboxAction.StopPlayback; return true; }
        if (Matches(FavToggle, key)) { action = JukeboxAction.FavToggle; return true; }
        if (Matches(OpenSettings, key)) { action = JukeboxAction.OpenSettings; return true; }
        if (Matches(QueueSelected, key)) { action = JukeboxAction.QueueSelected; return true; }
        if (Matches(ToggleAutoDj, key)) { action = JukeboxAction.ToggleAutoDj; return true; }
        if (Matches(SeekForward, key)) { action = JukeboxAction.SeekForward; return true; }
        if (Matches(SeekBack, key)) { action = JukeboxAction.SeekBack; return true; }
        if (Matches(ExitApp, key)) { action = JukeboxAction.ExitApp; return true; }
        if (Matches(Pause, key)) { action = JukeboxAction.Pause; return true; }
        if (Matches(CreatePlaylistFromQueue, key)) { action = JukeboxAction.CreatePlaylistFromQueue; return true; }
        if (Matches(ToggleShuffle, key)) { action = JukeboxAction.ToggleShuffle; return true; }
        if (Matches(ToggleRepeat, key)) { action = JukeboxAction.ToggleRepeat; return true; }
        if (Matches(FocusSearch, key)) { action = JukeboxAction.FocusSearch; return true; }
        if (Matches(Home, key)) { action = JukeboxAction.Home; return true; }
        if (Matches(TogglePlayStop, key)) { action = JukeboxAction.TogglePlayStop; return true; }
        if (Matches(OpenPresetBrowser, key)) { action = JukeboxAction.OpenPresetBrowser; return true; }
        if (Matches(ToggleResizableWindows, key)) { action = JukeboxAction.ToggleResizableWindows; return true; }

        action = default;
        return false;
    }

    /// <summary>
    /// Try to resolve a DirectInput button press to a logical action.
    /// </summary>
    public bool TryGetAction(Guid deviceGuid, int buttonIndex, out JukeboxAction action)
    {
        if (MatchesDInput(NavLeft, deviceGuid, buttonIndex)) { action = JukeboxAction.NavLeft; return true; }
        if (MatchesDInput(NavRight, deviceGuid, buttonIndex)) { action = JukeboxAction.NavRight; return true; }
        if (MatchesDInput(NavUp, deviceGuid, buttonIndex)) { action = JukeboxAction.NavUp; return true; }
        if (MatchesDInput(NavDown, deviceGuid, buttonIndex)) { action = JukeboxAction.NavDown; return true; }
        if (MatchesDInput(Select, deviceGuid, buttonIndex)) { action = JukeboxAction.Select; return true; }
        if (MatchesDInput(Back, deviceGuid, buttonIndex)) { action = JukeboxAction.Back; return true; }
        if (MatchesDInput(Skip, deviceGuid, buttonIndex)) { action = JukeboxAction.Skip; return true; }
        if (MatchesDInput(StopPlayback, deviceGuid, buttonIndex)) { action = JukeboxAction.StopPlayback; return true; }
        if (MatchesDInput(FavToggle, deviceGuid, buttonIndex)) { action = JukeboxAction.FavToggle; return true; }
        if (MatchesDInput(OpenSettings, deviceGuid, buttonIndex)) { action = JukeboxAction.OpenSettings; return true; }
        if (MatchesDInput(QueueSelected, deviceGuid, buttonIndex)) { action = JukeboxAction.QueueSelected; return true; }
        if (MatchesDInput(ToggleAutoDj, deviceGuid, buttonIndex)) { action = JukeboxAction.ToggleAutoDj; return true; }
        if (MatchesDInput(SeekForward, deviceGuid, buttonIndex)) { action = JukeboxAction.SeekForward; return true; }
        if (MatchesDInput(SeekBack, deviceGuid, buttonIndex)) { action = JukeboxAction.SeekBack; return true; }
        if (MatchesDInput(ExitApp, deviceGuid, buttonIndex)) { action = JukeboxAction.ExitApp; return true; }
        if (MatchesDInput(Pause, deviceGuid, buttonIndex)) { action = JukeboxAction.Pause; return true; }
        if (MatchesDInput(CreatePlaylistFromQueue, deviceGuid, buttonIndex)) { action = JukeboxAction.CreatePlaylistFromQueue; return true; }
        if (MatchesDInput(ToggleShuffle, deviceGuid, buttonIndex)) { action = JukeboxAction.ToggleShuffle; return true; }
        if (MatchesDInput(ToggleRepeat, deviceGuid, buttonIndex)) { action = JukeboxAction.ToggleRepeat; return true; }
        if (MatchesDInput(FocusSearch, deviceGuid, buttonIndex)) { action = JukeboxAction.FocusSearch; return true; }
        if (MatchesDInput(Home, deviceGuid, buttonIndex)) { action = JukeboxAction.Home; return true; }
        if (MatchesDInput(TogglePlayStop, deviceGuid, buttonIndex)) { action = JukeboxAction.TogglePlayStop; return true; }
        if (MatchesDInput(OpenPresetBrowser, deviceGuid, buttonIndex)) { action = JukeboxAction.OpenPresetBrowser; return true; }
        if (MatchesDInput(ToggleResizableWindows, deviceGuid, buttonIndex)) { action = JukeboxAction.ToggleResizableWindows; return true; }

        action = default;
        return false;
    }

    private static bool Matches(ActionBinding binding, Key key) =>
        (binding.PrimaryKey != Key.None && binding.PrimaryKey == key) ||
        (binding.CabinetButton != Key.None && binding.CabinetButton == key);

    private static bool MatchesDInput(ActionBinding binding, Guid deviceGuid, int buttonIndex) =>
        binding.HasDInputBinding &&
        binding.CabinetDInputDeviceGuid == deviceGuid &&
        binding.CabinetDInputButton == buttonIndex;

    /// <summary>
    /// Returns all bindings as a list for the settings UI.
    /// </summary>
    public List<KeyBindingEntry> ToEntries() =>
    [
        // Navigation
        new("Navigate Left", nameof(NavLeft), NavLeft),
        new("Navigate Right", nameof(NavRight), NavRight),
        new("Navigate Up", nameof(NavUp), NavUp),
        new("Navigate Down", nameof(NavDown), NavDown),
        new("Select / Play", nameof(Select), Select),
        new("Toggle Play/Stop", nameof(TogglePlayStop), TogglePlayStop),
        new("Back", nameof(Back), Back),
        new("Home", nameof(Home), Home),
        new("Focus Search", nameof(FocusSearch), FocusSearch),
        // Playback controls
        new("Pause / Resume", nameof(Pause), Pause),
        new("Stop Playback", nameof(StopPlayback), StopPlayback),
        new("Skip Track", nameof(Skip), Skip),
        new("Seek Forward 15s", nameof(SeekForward), SeekForward),
        new("Seek Back 15s", nameof(SeekBack), SeekBack),
        new("Toggle AutoDJ", nameof(ToggleAutoDj), ToggleAutoDj),
        new("Toggle Shuffle", nameof(ToggleShuffle), ToggleShuffle),
        new("Toggle Repeat", nameof(ToggleRepeat), ToggleRepeat),
        // Queue & playlists
        new("Queue Selected", nameof(QueueSelected), QueueSelected),
        new("Add to Playlist", nameof(FavToggle), FavToggle),
        new("Save Queue", nameof(CreatePlaylistFromQueue), CreatePlaylistFromQueue),
        // App
        new("Open Settings", nameof(OpenSettings), OpenSettings),
        new("Preset Browser", nameof(OpenPresetBrowser), OpenPresetBrowser),
        new("Toggle Resizable Windows", nameof(ToggleResizableWindows), ToggleResizableWindows),
        new("Exit App", nameof(ExitApp), ExitApp),
    ];

    public void ApplyEntry(KeyBindingEntry entry)
    {
        var prop = GetType().GetProperty(entry.PropertyName);
        if (prop?.GetValue(this) is ActionBinding binding)
        {
            binding.PrimaryKey = entry.PrimaryKey;
            binding.CabinetButton = entry.CabinetButton;
            binding.CabinetDInputDeviceGuid = entry.CabinetDInputDeviceGuid;
            binding.CabinetDInputButton = entry.CabinetDInputButton;
        }
    }
}

public class KeyBindingEntry
{
    public string DisplayName { get; set; }
    public string PropertyName { get; set; }
    public Key PrimaryKey { get; set; }
    public Key CabinetButton { get; set; }
    public Guid? CabinetDInputDeviceGuid { get; set; }
    public int? CabinetDInputButton { get; set; }
    public string PrimaryKeyDisplay => PrimaryKey == Key.None ? "(none)" : PrimaryKey.ToString();

    public string CabinetButtonDisplay
    {
        get
        {
            if (CabinetDInputDeviceGuid.HasValue && CabinetDInputButton.HasValue)
                return $"Joy Btn {CabinetDInputButton.Value + 1}";
            return CabinetButton == Key.None ? "(none)" : CabinetButton.ToString();
        }
    }

    public bool HasDInputBinding => CabinetDInputDeviceGuid.HasValue && CabinetDInputButton.HasValue;

    public void SetDInputBinding(Guid deviceGuid, int buttonIndex)
    {
        CabinetDInputDeviceGuid = deviceGuid;
        CabinetDInputButton = buttonIndex;
        CabinetButton = Key.None; // clear keyboard binding when DInput is set
    }

    public void ClearDInputBinding()
    {
        CabinetDInputDeviceGuid = null;
        CabinetDInputButton = null;
    }

    public KeyBindingEntry(string displayName, string propertyName, ActionBinding binding)
    {
        DisplayName = displayName;
        PropertyName = propertyName;
        PrimaryKey = binding.PrimaryKey;
        CabinetButton = binding.CabinetButton;
        CabinetDInputDeviceGuid = binding.CabinetDInputDeviceGuid;
        CabinetDInputButton = binding.CabinetDInputButton;
    }
}
