namespace Phosphor.Plugin.Abstractions;

/// <summary>
/// Versioning for the plug-in contract. A plug-in reports the API version it was
/// built against (<see cref="IPhosphorSourceProvider.ApiVersion"/>); the host compares
/// it to <see cref="Current"/> and rejects incompatible plug-ins gracefully rather than
/// letting a mismatch crash the process.
/// </summary>
public static class PluginApi
{
    /// <summary>The contract version this build of the abstractions defines.</summary>
    /// <remarks>
    /// 0.12.0 — added <c>SourceCategory.Icon</c> + <see cref="IScopedSearchable"/> (search within a browse node).
    /// 0.11.0 — added <c>PluginSettingType.FolderPath</c> + <c>PluginSettingDescriptor.AllowMultiple</c>.
    /// 0.10.0 — added <see cref="IRefreshable"/> (rescan content / rebuild catalog).
    /// 0.9.0 — added <see cref="IConnectionTestable"/> (optional "test connection" capability).
    /// 0.8.0 — nested config sub-options (<c>ConfigOption.SubOptions</c>).
    /// </remarks>
    public static readonly Version Current = new(0, 12, 0);

    /// <summary>
    /// The oldest contract version this host still accepts. A plug-in built against an older
    /// contract than this is rejected (a capability it relies on may have changed shape). While the
    /// contract is pre-1.0 and evolving, this tracks <see cref="Current"/>; once it stabilizes, this
    /// can lag behind so older plug-ins keep loading.
    /// </summary>
    public static readonly Version MinimumSupported = new(0, 12, 0);

    /// <summary>
    /// Whether a plug-in built against <paramref name="pluginApiVersion"/> is compatible with this
    /// host: it must not be newer than <see cref="Current"/> (host predates the plug-in's contract)
    /// nor older than <see cref="MinimumSupported"/> (contract drifted too far). Compares by
    /// major.minor only — patch bumps are always additive/compatible.
    /// </summary>
    public static bool IsCompatible(Version pluginApiVersion)
    {
        var plugin = new Version(pluginApiVersion.Major, pluginApiVersion.Minor);
        var current = new Version(Current.Major, Current.Minor);
        var minimum = new Version(MinimumSupported.Major, MinimumSupported.Minor);
        return plugin >= minimum && plugin <= current;
    }
}
