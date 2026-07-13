namespace Phosphor.Plugin.Abstractions;

/// <summary>Field type for a declarative (Tier 1) plug-in setting the host renders as a form.</summary>
public enum PluginSettingType
{
    Text,
    Secret,   // rendered masked; stored via the host credential store, not the plain blob
    Bool,
    Number,
    Enum,
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
}

/// <summary>An interactive configuration action a source exposes (Tier 2 config).</summary>
public sealed record ConfigAction(string Id, string Label, string? Description = null);

/// <summary>A single option in a <see cref="ConfigSelection"/>.</summary>
public sealed record ConfigOption(string Id, string Label, bool IsSelected = false);

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
}
