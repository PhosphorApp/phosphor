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
/// The registry is the source path for the app: the VM dispatches YouTube and Plex
/// discovery/playback through the configured instances and their capabilities.
/// </remarks>
public sealed class SourceRegistry : IAsyncDisposable
{
    private readonly List<IPhosphorSource> _sources = [];
    // Tracks the config each source was built from, so the settings UI can report the actual
    // configured values (not just schema defaults).
    private readonly Dictionary<string, PluginInstanceConfig> _configs = new(StringComparer.Ordinal);
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

    /// <summary>
    /// The per-instance caching policy for a source: <c>null</c> = "use the capability default"
    /// (cache when the source implements <c>IDownloadable</c>); <c>true</c>/<c>false</c> forces it.
    /// Returns <c>null</c> for unknown instances.
    /// </summary>
    public bool? CachingPolicy(string instanceId) =>
        _configs.TryGetValue(instanceId, out var cfg) ? cfg.AllowCaching : null;

    /// <summary>Enumerates sources implementing a given capability.</summary>
    public IEnumerable<T> WithCapability<T>() where T : class =>
        _sources.OfType<T>();

    /// <summary>
    /// Returns a read-only description of each configured source for the settings UI: identity,
    /// configured/enabled state, the capabilities it supports, and its declarative settings schema.
    /// </summary>
    public IReadOnlyList<SourceSummary> DescribeSources()
    {
        var list = new List<SourceSummary>();
        foreach (var s in _sources)
        {
            var provider = CreateProvider(s.TypeId);
            var schema = provider?.GetSettingsSchema() ?? [];
            _configs.TryGetValue(s.InstanceId, out var cfg);

            var caps = PluginSettingsFactory.DescribeCapabilities(s);

            // Report each schema field's ACTUAL configured value (from the instance's settings),
            // falling back to the schema default only when the key isn't set. Secrets are masked.
            var fields = new List<SourceSettingValue>();
            foreach (var d in schema)
            {
                string? raw = cfg?.Settings != null && cfg.Settings.TryGetValue(d.Key, out var v)
                    ? v
                    : d.DefaultValue;
                var display = d.Secret
                    ? (string.IsNullOrEmpty(raw) ? "" : "••••••")
                    : (raw ?? "");
                fields.Add(new SourceSettingValue(d.Key, d.Label, display, d.Secret));
            }

            list.Add(new SourceSummary(
                s.TypeId, s.InstanceId, s.DisplayName, provider?.Description, s.IsConfigured, s.IsEnabled, caps, fields));
        }
        return list;
    }

    /// <summary>
    /// Builds and initializes the source instances from the given per-instance configs. Safe to
    /// call again to rebuild after settings change; existing instances are discarded. Disabled
    /// configs are skipped. Unknown provider type ids are logged and ignored.
    /// </summary>
    public async Task BuildAsync(IEnumerable<PluginInstanceConfig> configs, CancellationToken ct = default)
    {
        await DisposeSourcesAsync();
        _sources.Clear();
        _configs.Clear();

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
            _configs[cfg.InstanceId] = cfg;
            await AddAsync(source, ct);
        }
    }

    /// <summary>
    /// Creates the provider for a type id: built-in YouTube/Plex first, then any third-party
    /// provider discovered from the <c>plugins/</c> folder (<see cref="DiscoveredProviders"/>).
    /// Returns <c>null</c> for an unknown type id.
    /// </summary>
    private IPhosphorSourceProvider? CreateProvider(string typeId) => typeId switch
    {
        YouTubeSourceProvider.YouTubeTypeId => new YouTubeSourceProvider(_http),
        PlexSourceProvider.PlexTypeId => new PlexSourceProvider(),
        _ => DiscoveredProviders.Get(typeId),
    };

    private async Task AddAsync(IPhosphorSource source, CancellationToken ct)
    {
        var host = new PluginHost(source.InstanceId, _http);
        await source.InitializeAsync(host, ct);
        _sources.Add(source);
    }

    /// <summary>
    /// Disposes each current source that opts into teardown (<see cref="IAsyncDisposable"/> or
    /// <see cref="IDisposable"/>), so a rebuild or app shutdown releases any connections, watchers,
    /// or timers a source holds. Defensive: a faulty source's dispose never aborts the sweep.
    /// </summary>
    private async Task DisposeSourcesAsync()
    {
        foreach (var source in _sources)
        {
            try
            {
                switch (source)
                {
                    case IAsyncDisposable ad:
                        await ad.DisposeAsync();
                        break;
                    case IDisposable d:
                        d.Dispose();
                        break;
                }
            }
            catch (Exception ex)
            {
                DebugLog.LogException($"SourceRegistry dispose '{source.InstanceId}'", ex);
            }
        }
    }

    /// <summary>Disposes all live sources. Call when the registry is being replaced or the app exits.</summary>
    public async ValueTask DisposeAsync()
    {
        await DisposeSourcesAsync();
        _sources.Clear();
        _configs.Clear();
    }
}

/// <summary>Read-only description of a configured source instance for the settings UI.</summary>
public sealed record SourceSummary(
    string TypeId,
    string InstanceId,
    string DisplayName,
    string? Description,
    bool IsConfigured,
    bool IsEnabled,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<SourceSettingValue> Settings);

/// <summary>One setting field with its actual configured display value (secrets already masked).</summary>
public sealed record SourceSettingValue(string Key, string Label, string DisplayValue, bool Secret);
