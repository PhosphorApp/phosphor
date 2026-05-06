namespace VpinJukebox;

/// <summary>
/// Holds in-memory (non-persisted) state for the settings window
/// so the same tab and scroll position are restored when re-opened.
/// </summary>
internal static class SettingsWindowState
{
    public static int LastTabIndex { get; set; }
    public static Dictionary<int, double> ScrollOffsets { get; } = new();
}
