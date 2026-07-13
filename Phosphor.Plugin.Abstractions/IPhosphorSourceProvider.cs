namespace Phosphor.Plugin.Abstractions;

/// <summary>
/// A plug-in <em>type</em> (the thing a DLL exports) and factory for configured
/// <see cref="IPhosphorSource"/> instances. Providers are discovered once (statically
/// referenced in-box, or scanned from the plug-ins folder later) and describe their
/// identity, settings schema, and whether they support multiple configured instances.
/// </summary>
/// <remarks>
/// The provider/instance split exists because some sources need more than one live
/// configuration at a time (e.g. two Plex servers). A provider with
/// <see cref="SupportsMultipleInstances"/> == <c>true</c> can be asked to create several
/// independent <see cref="IPhosphorSource"/>s, each with its own settings and id.
/// </remarks>
public interface IPhosphorSourceProvider
{
    /// <summary>Stable type id, e.g. "youtube", "plex", "jellyfin". Never localized.</summary>
    string TypeId { get; }

    /// <summary>Human-friendly name for the settings UI, e.g. "Plex".</summary>
    string DisplayName { get; }

    /// <summary>
    /// The contract version this plug-in was built against. The host compares this to
    /// <see cref="PluginApi.Current"/> and refuses to load incompatible plug-ins.
    /// </summary>
    Version ApiVersion { get; }

    /// <summary>
    /// True when the user may configure more than one instance of this source
    /// (e.g. two Plex servers). Single-instance sources (YouTube) return false.
    /// </summary>
    bool SupportsMultipleInstances { get; }

    /// <summary>
    /// Declarative settings schema the host renders as a standard form (Tier 1 config).
    /// Interactive config (Tier 2, e.g. "browse libraries") is exposed per-instance via
    /// <see cref="IConfigurable"/>.
    /// </summary>
    IReadOnlyList<PluginSettingDescriptor> GetSettingsSchema();

    /// <summary>
    /// Creates a configured, independent source instance. The host owns
    /// <paramref name="instanceId"/> (stable, unique per configured instance) and the
    /// persisted <paramref name="settings"/> blob; the plug-in owns their meaning.
    /// </summary>
    IPhosphorSource CreateInstance(string instanceId, IReadOnlyDictionary<string, string?> settings);
}
