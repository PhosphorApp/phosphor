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
    /// 0.9.0 — added <see cref="IConnectionTestable"/> (optional "test connection" capability).
    /// 0.8.0 — nested config sub-options (<c>ConfigOption.SubOptions</c>).
    /// </remarks>
    public static readonly Version Current = new(0, 9, 0);
}
