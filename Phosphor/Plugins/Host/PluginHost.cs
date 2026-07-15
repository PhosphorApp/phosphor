using System.IO;
using System.Net.Http;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.Host;

/// <summary>
/// Host-side implementation of <see cref="IPluginHost"/> — the single one-way door plug-in
/// sources call back through. It maps abstraction services onto the app's existing
/// facilities (<see cref="DebugLog"/>, a shared <see cref="HttpClient"/>, app-relative tool
/// and cache paths). The host owns all threading; sources never touch UI.
/// </summary>
/// <remarks>
/// Per-instance: each configured source gets its own host so logs, cache dir, and secrets
/// are scoped by instance id. The secret store is a simple in-memory dictionary for now
/// (Phase 4); a DPAPI-backed store is a later phase.
/// </remarks>
public sealed class PluginHost : IPluginHost
{
    private readonly string _instanceId;
    private readonly Dictionary<string, string?> _secrets = new(StringComparer.Ordinal);

    public PluginHost(string instanceId, HttpClient httpClient)
    {
        _instanceId = instanceId;
        HttpClient = httpClient;
        InstanceCacheDirectory = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "plugin-cache", Sanitize(instanceId));
    }

    public HttpClient HttpClient { get; }

    public string InstanceCacheDirectory { get; }

    public void Log(string message) => DebugLog.Log($"Plugin:{_instanceId}", message);

    public string? GetSecret(string key) => _secrets.TryGetValue(key, out var v) ? v : null;

    public void SetSecret(string key, string? value) => _secrets[key] = value;

    /// <summary>
    /// Resolves a host-bundled native tool by logical name (e.g. "yt-dlp", "ffmpeg"). Tools
    /// ship next to the app (copied via csproj); returns the full path when present, else null.
    /// </summary>
    public string? GetToolPath(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return null;
        var exe = toolName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? toolName : toolName + ".exe";
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, exe);
        return File.Exists(path) ? path : null;
    }

    public void ReportStatus(string message) => DebugLog.Log($"Plugin:{_instanceId}:status", message);

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s;
    }
}
