using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.Loader;

/// <summary>
/// Discovers source plug-ins by scanning a <c>plugins/</c> folder for assemblies that export
/// <see cref="IPhosphorSourceProvider"/> implementations. Every source ships as a plug-in here
/// (YouTube, Plex, and third-party sources alike) — there are no statically-referenced built-ins.
/// Each subfolder holds exactly one plug-in and, by convention, names its provider assembly
/// <c>Phosphor.Plugins.&lt;FolderName&gt;.dll</c>; when present, that assembly is loaded directly and
/// its private dependencies are skipped (see <see cref="PluginAssemblyPrefix"/>).
/// </summary>
/// <remarks>
/// Each plug-in assembly is loaded into its own <see cref="AssemblyLoadContext"/> so a bad or
/// incompatible plug-in is isolated (its failure is logged and skipped, never fatal to startup).
/// Contexts are non-collectible and retained for the process lifetime — plug-ins load once at
/// startup and their private dependencies load lazily on first use, so the context must stay alive.
/// The shared contract assembly (<c>Phosphor.Plugin.Abstractions</c>) is deliberately
/// resolved from the host's already-loaded copy — never from the plug-in's own folder — so the
/// provider type the plug-in implements unifies with the host's <see cref="IPhosphorSourceProvider"/>
/// (otherwise casts across the boundary would fail).
/// </remarks>
public static class PluginLoader
{
    /// <summary>The subfolder (under the app base directory) scanned for plug-ins.</summary>
    public const string PluginsFolderName = "plugins";

    /// <summary>
    /// Prefix of the conventional entry-point assembly for a plug-in folder: a folder named
    /// <c>&lt;Name&gt;</c> is expected to contain <c>Phosphor.Plugins.&lt;Name&gt;.dll</c> as its
    /// provider assembly (e.g. <c>YouTube/Phosphor.Plugins.YouTube.dll</c>). When that file exists,
    /// discovery loads only it and skips the folder's private dependencies — a plug-in folder holds
    /// exactly one plug-in. Folders that don't follow the convention fall back to scanning every DLL.
    /// </summary>
    public const string PluginAssemblyPrefix = "Phosphor.Plugins.";

    // Strong references to every loaded plug-in context, kept for the life of the process so a
    // context is never garbage-collected/unloaded while its provider (and lazily-loaded private
    // dependencies) are still in use.
    private static readonly List<PluginLoadContext> _retainedContexts = [];

    /// <summary>
    /// Scans the plug-ins folder and returns a descriptor for each discovered provider (loaded or
    /// rejected). Never throws — per-assembly failures are captured on the descriptor.
    /// </summary>
    /// <param name="baseDirectory">App base directory; defaults to <see cref="AppContext.BaseDirectory"/>.</param>
    public static IReadOnlyList<LoadedPlugin> DiscoverProviders(string? baseDirectory = null)
    {
        var root = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, PluginsFolderName);
        var results = new List<LoadedPlugin>();

        if (!Directory.Exists(root))
        {
            DebugLog.Log(LogLevel.Info, "PluginLoader", $"No plug-ins folder at '{root}' — skipping dynamic load.");
            return results;
        }

        // Each plug-in lives in its own subfolder holding exactly one plug-in:
        // plugins/<Name>/Phosphor.Plugins.<Name>.dll (+ its private deps).
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            // Fast path: honour the naming convention and load only the entry-point assembly,
            // skipping a reflection scan of the folder's private dependencies. This matters once a
            // plug-in ships several DLLs (e.g. YouTube ships AngleSharp/YoutubeExplode alongside it).
            var conventionalDll = Path.Combine(dir, PluginAssemblyPrefix + Path.GetFileName(dir) + ".dll");
            if (File.Exists(conventionalDll))
            {
                TryLoadAssembly(conventionalDll, results);
                continue;
            }

            // Fallback: folder doesn't follow the convention — scan every DLL as before.
            foreach (var dll in Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
            {
                // Skip the shared contract if a plug-in mistakenly shipped its own copy.
                if (string.Equals(Path.GetFileNameWithoutExtension(dll),
                        typeof(IPhosphorSourceProvider).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase))
                    continue;

                TryLoadAssembly(dll, results);
            }
        }

        return results;
    }

    private static void TryLoadAssembly(string dllPath, List<LoadedPlugin> results)
    {
        var alc = new PluginLoadContext(dllPath);
        // Retain a strong, process-lifetime reference to the context. Plug-ins are loaded once at
        // startup and never unloaded, and a plug-in's dependencies (e.g. TagLibSharp) load lazily
        // on first use — long after discovery. Without holding the context object alive the CLR can
        // begin unloading it, and a later dependency load throws "AssemblyLoadContext is unloading".
        _retainedContexts.Add(alc);
        try
        {
            var asm = alc.LoadFromAssemblyPath(dllPath);

            // Only assemblies that actually export a provider are interesting; a plug-in DLL with no
            // IPhosphorSourceProvider is silently ignored (it may be a private dependency).
            var providerTypes = asm.GetTypes()
                .Where(t => typeof(IPhosphorSourceProvider).IsAssignableFrom(t)
                            && t is { IsAbstract: false, IsInterface: false })
                .ToList();

            if (providerTypes.Count == 0)
                return;

            foreach (var type in providerTypes)
            {
                try
                {
                    var provider = (IPhosphorSourceProvider)Activator.CreateInstance(type)!;

                    if (!PluginApi.IsCompatible(provider.ApiVersion))
                    {
                        results.Add(LoadedPlugin.Rejected(dllPath, provider.TypeId,
                            $"Incompatible contract version {provider.ApiVersion} " +
                            $"(host supports {PluginApi.MinimumSupported}–{PluginApi.Current})."));
                        DebugLog.Log(LogLevel.Warning, "PluginLoader",
                            $"Rejected '{provider.TypeId}' from {Path.GetFileName(dllPath)}: ApiVersion {provider.ApiVersion} incompatible.");
                        continue;
                    }

                    results.Add(LoadedPlugin.Loaded(dllPath, provider));
                    DebugLog.Log(LogLevel.Info, "PluginLoader",
                        $"Loaded provider '{provider.TypeId}' ({provider.DisplayName}) from {Path.GetFileName(dllPath)}.");
                }
                catch (Exception ex)
                {
                    results.Add(LoadedPlugin.Rejected(dllPath, type.FullName ?? type.Name,
                        $"Failed to construct provider: {ex.Message}"));
                    DebugLog.LogException($"PluginLoader construct '{type.FullName}'", ex);
                }
            }
        }
        catch (Exception ex)
        {
            // Bad TFM, missing dependency, corrupt image, etc. — isolate and continue.
            results.Add(LoadedPlugin.Rejected(dllPath, Path.GetFileNameWithoutExtension(dllPath),
                $"Failed to load assembly: {ex.Message}"));
            DebugLog.LogException($"PluginLoader load '{dllPath}'", ex);
        }
    }
}

/// <summary>
/// A load context for one plug-in assembly. Resolves the plug-in's private dependencies from its
/// own folder, but delegates the shared contract assembly (and any already-loaded host assembly) to
/// the default context so contract types unify across the boundary. Non-collectible and retained by
/// <see cref="PluginLoader"/> for the process lifetime.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _pluginDir;

    public PluginLoadContext(string pluginPath) : base(isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
        _pluginDir = Path.GetDirectoryName(pluginPath)!;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // The shared contract must come from the host's default context, never a second copy.
        if (assemblyName.Name == typeof(IPhosphorSourceProvider).Assembly.GetName().Name)
            return null; // null => fall back to the default context

        // Prefer a dependency shipped alongside the plug-in (our deploy flattens private deps like
        // TagLibSharp into the plug-in folder). This is more reliable than the deps.json/NuGet-cache
        // resolution, which needs runtime probing config the flattened deployment doesn't carry.
        if (assemblyName.Name is { Length: > 0 } name)
        {
            var local = Path.Combine(_pluginDir, name + ".dll");
            if (File.Exists(local))
                return LoadFromAssemblyPath(local);
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}

/// <summary>The result of attempting to load one provider from a plug-in assembly.</summary>
public sealed class LoadedPlugin
{
    private LoadedPlugin(string assemblyPath, string typeId, IPhosphorSourceProvider? provider, string? error)
    {
        AssemblyPath = assemblyPath;
        TypeId = typeId;
        Provider = provider;
        Error = error;
    }

    /// <summary>Full path of the plug-in assembly the provider came from.</summary>
    public string AssemblyPath { get; }

    /// <summary>The provider's type id (or a best-effort identifier when load failed).</summary>
    public string TypeId { get; }

    /// <summary>The loaded provider, or <c>null</c> when <see cref="Error"/> is set.</summary>
    public IPhosphorSourceProvider? Provider { get; }

    /// <summary>A human-readable reason the plug-in was rejected, or <c>null</c> when loaded.</summary>
    public string? Error { get; }

    /// <summary>True when the provider loaded and is contract-compatible.</summary>
    public bool IsLoaded => Provider != null && Error == null;

    internal static LoadedPlugin Loaded(string path, IPhosphorSourceProvider provider)
        => new(path, provider.TypeId, provider, null);

    internal static LoadedPlugin Rejected(string path, string typeId, string error)
        => new(path, typeId, null, error);
}
