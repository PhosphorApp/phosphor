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

            var caps = new List<string>();
            if (s is ITextSearchCapable) caps.Add("Search");
            if (s is IPlaylistChannelDiscovery) caps.Add("Playlists/Channels");
            if (s is IBrowsable) caps.Add("Browse");
            if (s is IPagedBrowsable) caps.Add("Paged browse");
            if (s is IPlayableResolver) caps.Add("Playback");
            if (s is IDownloadable) caps.Add("Download/Cache");
            if (s is IConfigurable) caps.Add("Setup actions");

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
