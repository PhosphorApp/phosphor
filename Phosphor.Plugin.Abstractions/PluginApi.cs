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
    public static readonly Version Current = new(0, 6, 0);
}
