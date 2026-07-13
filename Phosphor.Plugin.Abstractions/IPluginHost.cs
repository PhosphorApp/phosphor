namespace Phosphor.Plugin.Abstractions;

/// <summary>
/// Services the host hands to a source at initialization so the plug-in never references
/// host internals directly. This is the one-way door the plug-in may call back through;
/// the host owns all threading/marshalling behind these members (see the threading notes
/// in PLUGIN_ARCHITECTURE_ANALYSIS.md).
/// </summary>
public interface IPluginHost
{
    /// <summary>Structured logging routed into the host's diagnostics log.</summary>
    void Log(string message);

    /// <summary>
    /// A shared <see cref="HttpClient"/> for the plug-in to use. Supplied by the host so
    /// connection pooling and defaults are consistent; the plug-in must not dispose it.
    /// </summary>
    HttpClient HttpClient { get; }

    /// <summary>
    /// A per-instance directory the plug-in may use for its own cache/state. Created by the
    /// host and safe to write to.
    /// </summary>
    string InstanceCacheDirectory { get; }

    /// <summary>Retrieves a stored secret (e.g. an API token) by key for this instance, or null.</summary>
    string? GetSecret(string key);

    /// <summary>Persists a secret for this instance via the host's credential store (e.g. DPAPI).</summary>
    void SetSecret(string key, string? value);

    /// <summary>
    /// Reports a human-readable status message for the UI (e.g. "Found playlist: X").
    /// The host marshals this onto the UI thread; the plug-in must not assume a thread.
    /// </summary>
    void ReportStatus(string message);
}
