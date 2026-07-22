namespace Phosphor.Plugin.Abstractions;

/// <summary>
/// A configured <em>instance</em> of a source — the object the host actually talks to.
/// "Plex @ home" and "Plex @ friend" are two <see cref="IPhosphorSource"/>s produced by
/// the same <see cref="IPhosphorSourceProvider"/>. Capabilities (search, browse, resolve,
/// download, interactive config) are expressed by <em>additionally</em> implementing the
/// optional capability interfaces in this assembly, so a source advertises only what it
/// can actually do.
/// </summary>
/// <remarks>
/// The source is a pure data producer: the host calls in and receives plain data back.
/// A source must never assume a UI thread, touch WPF, or call back into the host beyond
/// the services handed to it via <see cref="IPluginHost"/> (see the threading notes in
/// dev_docs/PLUGIN_ARCHITECTURE_ANALYSIS.md).
/// </remarks>
public interface IPhosphorSource
{
    /// <summary>Stable id unique to this configured instance (host-assigned).</summary>
    string InstanceId { get; }

    /// <summary>The <see cref="IPhosphorSourceProvider.TypeId"/> that created this instance.</summary>
    string TypeId { get; }

    /// <summary>User-editable label shown in the UI, e.g. "Home Plex". Defaults sensibly.</summary>
    string DisplayName { get; set; }

    /// <summary>True when the current settings are sufficient to operate (e.g. Plex has a server + token).</summary>
    bool IsConfigured { get; }

    /// <summary>Whether the user has enabled this instance. Disabled sources are ignored by the host.</summary>
    bool IsEnabled { get; set; }

    /// <summary>
    /// One-time async initialization. The host supplies shared services (logging, HTTP,
    /// cache paths, credential store, progress reporting) via <paramref name="host"/>.
    /// </summary>
    Task InitializeAsync(IPluginHost host, CancellationToken ct = default);

    /// <summary>
    /// Applies a (possibly updated) settings blob. Called after the user edits settings.
    /// The plug-in owns the meaning of the keys it declared in its schema.
    /// </summary>
    void ApplySettings(IReadOnlyDictionary<string, string?> values);
}
