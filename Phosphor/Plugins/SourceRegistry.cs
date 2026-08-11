using System.Net.Http;
using Phosphor.Plugin.Abstractions;
using Phosphor.Plugins.Host;

namespace Phosphor.Plugins;

/// <summary>
/// Runtime registry of configured plug-in source instances. Builds every configured source
/// (YouTube, Plex, and any discovered third-party source) from the app's current settings,
/// initializes each with a per-instance <see cref="PluginHost"/>, and exposes lookups by instance
/// id and by capability.
/// </summary>
/// <remarks>
/// The registry is the source path for the app: the VM dispatches all source discovery/playback
/// through the configured plug-in instances and their capabilities.
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
        _sources.FirstOrDefault(s => s.TypeId == KnownSourceTypeIds.YouTube);

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
    /// call again to reconcile after settings change. Unchanged instances are <b>kept alive</b> (no
    /// dispose/rebuild), so applying settings never tears down an active source — e.g. an in-flight
    /// live stream whose local proxy would otherwise be killed. Instances whose config changed are
    /// reconfigured in place via <see cref="IPhosphorSource.ApplySettings"/>; only removed,
    /// type-changed, or newly-disabled instances are disposed. Disabled configs are skipped. Unknown
    /// provider type ids are logged and ignored.
    /// </summary>
    /// <returns>
    /// A <see cref="ReconcileResult"/> whose <see cref="ReconcileResult.UnchangedInstanceIds"/> lists
    /// the instances carried over untouched (same type + identical config), so the caller can reuse
    /// already-fetched, source-derived state (e.g. browse tiles) instead of re-querying them.
    /// </returns>
    public async Task<ReconcileResult> BuildAsync(IEnumerable<PluginInstanceConfig> configs, CancellationToken ct = default)
    {
        // Index the surviving live instances by id so we can reuse them.
        var existing = _sources.ToDictionary(s => s.InstanceId, StringComparer.Ordinal);

        var rebuilt = new List<IPhosphorSource>();
        var newConfigs = new Dictionary<string, PluginInstanceConfig>(StringComparer.Ordinal);
        var keptIds = new HashSet<string>(StringComparer.Ordinal);
        var unchangedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var cfg in configs)
        {
            if (!cfg.Enabled) continue;

            var provider = CreateProvider(cfg.TypeId);
            if (provider == null)
            {
                DebugLog.Log(LogLevel.Warning, "SourceRegistry", $"Unknown provider type '{cfg.TypeId}' — skipping instance '{cfg.InstanceId}'");
                continue;
            }

            // Reuse a live instance when the type matches: keep it as-is if the config is unchanged,
            // otherwise reconfigure it in place. This preserves any state/connections it holds.
            if (existing.TryGetValue(cfg.InstanceId, out var live) &&
                string.Equals(live.TypeId, cfg.TypeId, StringComparison.Ordinal))
            {
                var hadConfig = _configs.TryGetValue(cfg.InstanceId, out var prev);
                if (!hadConfig || !prev!.ConfigEquals(cfg))
                {
                    if (!string.IsNullOrEmpty(cfg.DisplayName))
                        live.DisplayName = cfg.DisplayName!;
                    try { live.ApplySettings(cfg.Settings); }
                    catch (Exception ex) { DebugLog.LogException($"SourceRegistry reconfigure '{cfg.InstanceId}'", ex); }
                }
                else
                {
                    unchangedIds.Add(cfg.InstanceId);
                }
                rebuilt.Add(live);
                newConfigs[cfg.InstanceId] = cfg;
                keptIds.Add(cfg.InstanceId);
                continue;
            }

            var source = provider.CreateInstance(cfg.InstanceId, cfg.Settings);
            if (!string.IsNullOrEmpty(cfg.DisplayName))
                source.DisplayName = cfg.DisplayName!;
            newConfigs[cfg.InstanceId] = cfg;
            await InitializeAsync(source, ct);
            rebuilt.Add(source);
        }

        // Dispose only the instances that were NOT carried over (removed, disabled, or type-changed).
        var toDispose = _sources.Where(s => !keptIds.Contains(s.InstanceId)).ToList();
        await DisposeSourcesAsync(toDispose);

        _sources.Clear();
        _sources.AddRange(rebuilt);
        _configs.Clear();
        foreach (var kv in newConfigs) _configs[kv.Key] = kv.Value;

        return new ReconcileResult(unchangedIds);
    }

    /// <summary>
    /// Creates the provider for a type id: all sources (YouTube, Plex, and third parties) are
    /// discovered from the <c>plugins/</c> folder (<see cref="DiscoveredProviders"/>).
    /// Returns <c>null</c> for an unknown type id.
    /// </summary>
    private IPhosphorSourceProvider? CreateProvider(string typeId) => DiscoveredProviders.Get(typeId);

    private async Task InitializeAsync(IPhosphorSource source, CancellationToken ct)
    {
        var host = new PluginHost(source.InstanceId, _http);
        await source.InitializeAsync(host, ct);
    }

    /// <summary>
    /// Disposes each source in <paramref name="sources"/> that opts into teardown
    /// (<see cref="IAsyncDisposable"/> or <see cref="IDisposable"/>), so a reconcile or app shutdown
    /// releases any connections, watchers, or timers a source holds. Defensive: a faulty source's
    /// dispose never aborts the sweep.
    /// </summary>
    private static async Task DisposeSourcesAsync(IEnumerable<IPhosphorSource> sources)
    {
        foreach (var source in sources)
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
        await DisposeSourcesAsync(_sources);
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

/// <summary>Outcome of a <see cref="SourceRegistry.BuildAsync"/> reconcile.</summary>
/// <param name="UnchangedInstanceIds">
/// Instances that were carried over untouched (same type and identical config), so any already-fetched
/// source-derived state (e.g. browse tiles) for them can be reused instead of re-querying the source.
/// </param>
public sealed record ReconcileResult(IReadOnlyCollection<string> UnchangedInstanceIds);
