using Phosphor.Plugin.Abstractions;
using Phosphor.Plugins.Loader;

namespace Phosphor.Plugins;

/// <summary>
/// Process-wide registry of third-party providers discovered by <see cref="PluginLoader"/> from the
/// <c>plugins/</c> folder. Populated once at startup (<see cref="Initialize"/>) and then consulted
/// by <see cref="SourceRegistry"/> and <see cref="PluginSettingsFactory"/> alongside the built-in
/// YouTube/Plex providers. Built-ins take precedence: a discovered provider that reuses a built-in
/// type id is ignored.
/// </summary>
public static class DiscoveredProviders
{
    private static readonly Dictionary<string, IPhosphorSourceProvider> _providers =
        new(StringComparer.Ordinal);
    private static IReadOnlyList<LoadedPlugin> _lastResults = [];

    /// <summary>
    /// Runs discovery once and caches the compatible providers. <paramref name="reservedTypeIds"/>
    /// are the built-in type ids that a plug-in may not shadow. Idempotent-safe to call again (it
    /// re-scans and replaces the cache).
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
                DebugLog.Log("PluginLoader", $"Ignoring plug-in '{typeId}' — shadows a built-in type id.");
                continue;
            }
            if (!_providers.TryAdd(typeId, plugin.Provider))
                DebugLog.Log("PluginLoader", $"Ignoring duplicate plug-in type id '{typeId}'.");
            else
                WarnOnMissingRequiredTools(plugin.Provider, baseDirectory);
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
                DebugLog.Log("PluginLoader",
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
