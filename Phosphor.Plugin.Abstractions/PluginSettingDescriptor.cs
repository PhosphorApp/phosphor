namespace Phosphor.Plugin.Abstractions;

/// <summary>Field type for a declarative (Tier 1) plug-in setting the host renders as a form.</summary>
public enum PluginSettingType
{
    Text,
    Secret,   // rendered masked; stored via the host credential store, not the plain blob
    Bool,
    Number,
    Enum,
    FolderPath, // rendered as a text field with a "Browse…" folder picker
}

/// <summary>
/// Declares one simple, static setting the host renders in the generic Plug-ins tab
/// (Tier 1 config). Interactive config that needs a live call (e.g. "browse libraries")
/// is exposed separately via <see cref="IConfigurable"/> (Tier 2).
/// </summary>
public sealed record PluginSettingDescriptor(
    string Key,
    string Label,
    PluginSettingType Type,
    bool Secret = false,
    string? DefaultValue = null,
    string? HelpText = null)
{
    /// <summary>Allowed values when <see cref="Type"/> is <see cref="PluginSettingType.Enum"/>.</summary>
    public IReadOnlyList<string>? EnumValues { get; init; }

    /// <summary>
    /// When true, this setting holds a <em>list</em> of values rather than one. The host renders an
    /// add/remove list editor (each row using the <see cref="Type"/>'s editor — e.g. a folder picker
    /// per row) and persists the list as newline-separated text in the single settings string. The
    /// plug-in reads the value and splits on newlines. Not for values that can themselves contain a
    /// newline (fine for paths, URLs, ids).
    /// </summary>
    public bool AllowMultiple { get; init; }
}

/// <summary>An interactive configuration action a source exposes (Tier 2 config).</summary>
public sealed record ConfigAction(string Id, string Label, string? Description = null);

/// <summary>A single option in a <see cref="ConfigSelection"/>.</summary>
/// <param name="Id">Stable option id.</param>
/// <param name="Label">Display label.</param>
/// <param name="IsSelected">Whether the option is initially selected.</param>
/// <param name="SubOptions">
/// Optional per-option sub-flags rendered as indented checkboxes under the option (e.g. a Plex
/// library's "Hubs" and "Playlists" extras). Null/empty for a plain checkbox option.
/// </param>
public sealed record ConfigOption(
    string Id,
    string Label,
    bool IsSelected = false,
    IReadOnlyList<ConfigSubOption>? SubOptions = null);

/// <summary>A sub-flag under a <see cref="ConfigOption"/> (e.g. "Hubs", "Playlists").</summary>
/// <param name="Id">Stable sub-option id.</param>
/// <param name="Label">Display label (checkbox text).</param>
/// <param name="IsSelected">Whether the sub-flag is initially selected.</param>
/// <param name="Description">Optional tooltip describing the sub-flag.</param>
public sealed record ConfigSubOption(string Id, string Label, bool IsSelected = false, string? Description = null);

/// <summary>
/// The user's decision for one <see cref="ConfigOption"/> after a config action: whether the option
/// itself is selected, plus the ids of any selected sub-options.
/// </summary>
public sealed record ConfigOptionResult(
    string OptionId,
    bool IsSelected,
    IReadOnlyList<string> SelectedSubOptionIds);

/// <summary>
/// The result of invoking a <see cref="ConfigAction"/>: a set of options the host renders
/// as a generic pick-list. The user's choices are persisted back into the instance's
/// settings blob by the host.
/// </summary>
public sealed record ConfigSelection(
    IReadOnlyList<ConfigOption> Options,
    bool AllowMultiple = true,
    string? Title = null);

/// <summary>
/// Capability: interactive configuration. Implemented by sources whose setup needs a live
/// call at config time (e.g. Plex browsing its server to list libraries the user can turn
/// into tiles). The plug-in supplies the data and logic; the host renders a generic shell
/// and persists the selection. The plug-in never touches UI.
/// </summary>
public interface IConfigurable
{
    /// <summary>The interactive actions this source exposes in its settings panel.</summary>
    IReadOnlyList<ConfigAction> GetConfigActions();

    /// <summary>Runs a config action and returns a pick-list for the host to render.</summary>
    Task<ConfigSelection> InvokeConfigActionAsync(string actionId, CancellationToken ct = default);

    /// <summary>
    /// Applies the user's selection from a config action back into this instance's settings. The
    /// source owns the translation (e.g. turning chosen library ids + their sub-flags into its rich
    /// <c>libraries</c> blob) since only it knows the settings shape. Returns the updated settings
    /// dictionary the host should persist for the instance.
    /// </summary>
    /// <param name="actionId">The action whose selection is being applied.</param>
    /// <param name="results">Per-option results (selected + chosen sub-options) from the dialog.</param>
    /// <param name="currentSettings">The instance's current settings (as edited so far).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyDictionary<string, string?>> ApplyConfigActionAsync(
        string actionId,
        IReadOnlyList<ConfigOptionResult> results,
        IReadOnlyDictionary<string, string?> currentSettings,
        CancellationToken ct = default);
}
