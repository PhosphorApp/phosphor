namespace Phosphor.Plugin.Abstractions;

/// <summary>
/// Verbosity level a plug-in can attach to a log message via <see cref="IPluginHost.Log(LogLevel, string)"/>.
/// Mirrors the host's internal logging levels so plug-in logs participate in the same verbosity
/// filtering (see dev_docs/LOG_VERBOSITY_MIGRATION.md). The host maps these onto its own logger and
/// drops entries below the user-selected minimum.
/// </summary>
public enum LogLevel
{
    /// <summary>Very chatty per-item/per-frame diagnostics. Silent at the default verbosity.</summary>
    Trace = 0,
    /// <summary>Developer-facing detail. The historical default for untagged plug-in logs.</summary>
    Debug = 1,
    /// <summary>Notable milestones (configured, connected, source ready).</summary>
    Info = 2,
    /// <summary>Recoverable problems and fallbacks.</summary>
    Warning = 3,
    /// <summary>Failures and exceptions.</summary>
    Error = 4,
}
