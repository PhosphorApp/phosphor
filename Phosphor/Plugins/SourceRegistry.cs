using System.Net.Http;
using System.Text.Json;
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
    /// Builds and initializes the source instances from the given app settings. Safe to call
    /// again to rebuild after settings change; existing instances are discarded.
    /// </summary>
    public async Task BuildAsync(AppSettings settings, CancellationToken ct = default)
    {
        _sources.Clear();

        // ── YouTube (single instance) ──
        var ytProvider = new YouTubeSourceProvider(_http);
        var ytSettings = new Dictionary<string, string?>
        {
            [YouTubeSourceProvider.KeySearchEngine] = settings.SearchEngine.ToString(),
            [YouTubeSourceProvider.KeyVideoEngine] = settings.VideoEngine.ToString(),
            [YouTubeSourceProvider.KeyVideoQuality] = settings.VideoQuality.ToString(),
            [YouTubeSourceProvider.KeyPreferStereo] = settings.StereoAudio.ToString(),
        };
        await AddAsync(ytProvider.CreateInstance("youtube", ytSettings), ct);

        // ── Plex (multi-instance; today the app models a single server) ──
        if (!string.IsNullOrWhiteSpace(settings.PlexServerUrl) &&
            !string.IsNullOrWhiteSpace(settings.PlexToken))
        {
            var plexProvider = new PlexSourceProvider();
            var plexSettings = new Dictionary<string, string?>
            {
                [PlexSourceProvider.KeyServerUrl] = settings.PlexServerUrl,
                [PlexSourceProvider.KeyToken] = settings.PlexToken,
                [PlexSourceProvider.KeyStereoAudio] = settings.PlexStereoAudio.ToString(),
                [PlexSourceProvider.KeyLibraries] = JsonSerializer.Serialize(settings.PlexLibraries),
            };
            await AddAsync(plexProvider.CreateInstance("plex", plexSettings), ct);
        }
    }

    private async Task AddAsync(IPhosphorSource source, CancellationToken ct)
    {
        var host = new PluginHost(source.InstanceId, _http);
        await source.InitializeAsync(host, ct);
        _sources.Add(source);
    }
}
