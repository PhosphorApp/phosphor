using Phosphor.Plugin.Abstractions;
using Phosphor.Plugins.Loader;

namespace Phosphor.Plugins;

/// <summary>
/// Process-wide registry of source providers discovered by <see cref="PluginLoader"/> from the
/// <c>plugins/</c> folder. Populated once at startup (<see cref="Initialize"/>) and then consulted
/// by <see cref="SourceRegistry"/> and <see cref="PluginSettingsFactory"/>. Every source — YouTube
/// and Plex included — is a discovered plug-in now; there are no statically-referenced built-ins.
/// </summary>
public static class DiscoveredProviders
{
    private static readonly Dictionary<string, IPhosphorSourceProvider> _providers =
        new(StringComparer.Ordinal);
    private static IReadOnlyList<LoadedPlugin> _lastResults = [];

    /// <summary>
    /// Runs discovery once and caches the compatible providers. <paramref name="reservedTypeIds"/>
    /// are type ids a plug-in may not shadow (normally empty now that all sources are discovered).
    /// Idempotent-safe to call again (it re-scans and replaces the cache).
    /// </summary>
    public static void Initialize(IEnumerable<string> reservedTypeIds, string? baseDirectory = null)
    {
        var reserved = new HashSet<string>(reservedTypeIds, StringComparer.Ordinal);
        _lastResults = PluginLoader.DiscoverProviders(baseDirectory);
        _providers.Clear();

        foreach (var plugin in _lastResults)
        {
            if (!plugin.IsLoaded || plugin.Provider is null) continue;

            var typeId = plugin.Provider.TypeId;
            if (reserved.Contains(typeId))
            {
                DebugLog.Log(LogLevel.Warning, "PluginLoader", $"Ignoring plug-in '{typeId}' — shadows a reserved type id.");
                continue;
            }
            if (!_providers.TryAdd(typeId, plugin.Provider))
                DebugLog.Log(LogLevel.Warning, "PluginLoader", $"Ignoring duplicate plug-in type id '{typeId}'.");
            else
                WarnOnMissingRequiredTools(plugin.Provider, baseDirectory);
        }

        LogLoadedPluginSummary();
    }

    /// <summary>
    /// Emits a concise per-plug-in load summary (assembly + contract version, or the skip reason) so a
    /// hand-dropped DLL or a contract-incompatible plug-in is visible in the debug log. Mirrors what the
    /// About tab's "LOADED PLUG-INS" panel shows.
    /// </summary>
    private static void LogLoadedPluginSummary()
    {
        foreach (var plugin in _lastResults)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(plugin.AssemblyPath);
            if (string.IsNullOrEmpty(name)) name = plugin.TypeId;

            if (plugin.IsLoaded)
            {
                var contract = plugin.Provider?.ApiVersion?.ToString() ?? "?";
                DebugLog.Log(LogLevel.Info, "PluginLoader", $"Loaded plug-in {name} (contract {contract}).");
            }
            else
            {
                DebugLog.Log(LogLevel.Warning, "PluginLoader", $"Skipped plug-in {name}: {plugin.Error}");
            }
        }
    }

    /// <summary>
    /// Logs a clear startup warning for each declared <see cref="IPhosphorSourceProvider.RequiredTools"/>
    /// that is missing from the host's tool folder. Validation/visibility only — the plug-in still
    /// loads (a missing tool surfaces here rather than as a confusing play-time failure).
    /// </summary>
    private static void WarnOnMissingRequiredTools(IPhosphorSourceProvider provider, string? baseDirectory)
    {
        foreach (var tool in provider.RequiredTools)
        {
            if (string.IsNullOrWhiteSpace(tool)) continue;
            if (!ToolExists(tool, baseDirectory))
                DebugLog.Log(LogLevel.Warning, "PluginLoader",
                    $"Plug-in '{provider.TypeId}' declares required tool '{tool}', which is missing — " +
                    "the source may fail at runtime.");
        }
    }

    /// <summary>
    /// Mirrors <c>PluginHost.GetToolPath</c>'s resolution (a bundled <c>&lt;tool&gt;.exe</c> next to the
    /// app) so validation matches what the plug-in will actually see at runtime.
    /// </summary>
    private static bool ToolExists(string toolName, string? baseDirectory)
    {
        var exe = toolName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? toolName : toolName + ".exe";
        var path = System.IO.Path.Combine(baseDirectory ?? AppContext.BaseDirectory, exe);
        return System.IO.File.Exists(path);
    }

    /// <summary>Returns the discovered provider for a type id, or <c>null</c>.</summary>
    public static IPhosphorSourceProvider? Get(string typeId) =>
        _providers.TryGetValue(typeId, out var p) ? p : null;

    /// <summary>All discovered, compatible providers.</summary>
    public static IReadOnlyCollection<IPhosphorSourceProvider> All => _providers.Values;

    /// <summary>The raw results of the last discovery pass (including rejected plug-ins), for diagnostics.</summary>
    public static IReadOnlyList<LoadedPlugin> LastResults => _lastResults;
}
