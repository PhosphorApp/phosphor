using System.Net.Http;
using Phosphor.Plugin.Abstractions;
using Phosphor.Plugins.Host;
using Phosphor.Plugins.Plex;
using Phosphor.Plugins.YouTube;

namespace Phosphor.Plugins;

/// <summary>
/// Runtime registry of configured plug-in source instances. Builds the in-box YouTube and
/// Plex sources from the app's current settings, initializes each with a per-instance
/// <see cref="PluginHost"/>, and exposes lookups by instance id and by capability.
/// </summary>
/// <remarks>
/// Phase 4 runs the registry <em>alongside</em> the legacy VM engines rather than replacing
/// them — adoption is gated by <c>AppSettings.UsePluginSources</c> and limited to narrow
/// paths. Later phases migrate the VM's dispatch onto the registry and retire the legacy
/// branches.
/// </remarks>
public sealed class SourceRegistry
{
    private readonly List<IPhosphorSource> _sources = [];
    private readonly HttpClient _http;

    public SourceRegistry(HttpClient http)
    {
        _http = http;
    }

    /// <summary>All configured, enabled sources.</summary>
    public IReadOnlyList<IPhosphorSource> Sources => _sources;

    /// <summary>The single YouTube instance, if configured.</summary>
    public IPhosphorSource? YouTube =>
        _sources.FirstOrDefault(s => s.TypeId == YouTubeSourceProvider.YouTubeTypeId);

    /// <summary>All configured Plex instances (may be more than one).</summary>
    public IEnumerable<IPhosphorSource> PlexInstances =>
        _sources.Where(s => s.TypeId == PlexSourceProvider.PlexTypeId);

    /// <summary>Finds a source by instance id.</summary>
    public IPhosphorSource? ByInstance(string instanceId) =>
        _sources.FirstOrDefault(s => s.InstanceId == instanceId);

    /// <summary>Enumerates sources implementing a given capability.</summary>
    public IEnumerable<T> WithCapability<T>() where T : class =>
        _sources.OfType<T>();

    /// <summary>
    /// Builds and initializes the source instances from the given per-instance configs. Safe to
    /// call again to rebuild after settings change; existing instances are discarded. Disabled
    /// configs are skipped. Unknown provider type ids are logged and ignored.
    /// </summary>
    public async Task BuildAsync(IEnumerable<PluginInstanceConfig> configs, CancellationToken ct = default)
    {
        _sources.Clear();

        foreach (var cfg in configs)
        {
            if (!cfg.Enabled) continue;

            var provider = CreateProvider(cfg.TypeId);
            if (provider == null)
            {
                DebugLog.Log("SourceRegistry", $"Unknown provider type '{cfg.TypeId}' — skipping instance '{cfg.InstanceId}'");
                continue;
            }

            var source = provider.CreateInstance(cfg.InstanceId, cfg.Settings);
            if (!string.IsNullOrEmpty(cfg.DisplayName))
                source.DisplayName = cfg.DisplayName!;
            await AddAsync(source, ct);
        }
    }

    /// <summary>
    /// Creates the in-box provider for a type id. This is the single registry of known providers;
    /// when the dynamic loader lands, discovered providers are added here alongside the built-ins.
    /// </summary>
    private IPhosphorSourceProvider? CreateProvider(string typeId) => typeId switch
    {
        YouTubeSourceProvider.YouTubeTypeId => new YouTubeSourceProvider(_http),
        PlexSourceProvider.PlexTypeId => new PlexSourceProvider(),
        _ => null,
    };

    private async Task AddAsync(IPhosphorSource source, CancellationToken ct)
    {
        var host = new PluginHost(source.InstanceId, _http);
        await source.InitializeAsync(host, ct);
        _sources.Add(source);
    }
}
