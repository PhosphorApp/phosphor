using System.Text.Json;
using Phosphor.Plugin.Abstractions;
using Phosphor.Plugins.Plex;
using Phosphor.Plugins.YouTube;

namespace Phosphor.Plugins;

/// <summary>
/// Derives <see cref="PluginInstanceConfig"/>s from the app's flat <see cref="AppSettings"/>. This
/// is the single place the legacy settings schema is translated into plug-in instance config, so
/// <see cref="SourceRegistry"/> stays decoupled from <c>AppSettings</c>. When the generic Plug-ins
/// settings UI lands, configs become persisted/user-edited and this factory shrinks to a one-time
/// migration from the old flat fields.
/// </summary>
public static class PluginSettingsFactory
{
    public static List<PluginInstanceConfig> FromAppSettings(AppSettings settings)
    {
        var configs = new List<PluginInstanceConfig>
        {
            // ── YouTube (single instance) ──
            new()
            {
                TypeId = YouTubeSourceProvider.YouTubeTypeId,
                InstanceId = "youtube",
                DisplayName = "YouTube",
                Enabled = true,
                Settings = new Dictionary<string, string?>
                {
                    [YouTubeSourceProvider.KeySearchEngine] = settings.SearchEngine.ToString(),
                    [YouTubeSourceProvider.KeyVideoEngine] = settings.VideoEngine.ToString(),
                    [YouTubeSourceProvider.KeyVideoQuality] = settings.VideoQuality.ToString(),
                    [YouTubeSourceProvider.KeyPreferStereo] = settings.StereoAudio.ToString(),
                },
            },
        };

        // ── Plex (multi-instance capable; today the app models a single server) ──
        if (!string.IsNullOrWhiteSpace(settings.PlexServerUrl) &&
            !string.IsNullOrWhiteSpace(settings.PlexToken))
        {
            configs.Add(new PluginInstanceConfig
            {
                TypeId = PlexSourceProvider.PlexTypeId,
                InstanceId = "plex",
                DisplayName = "Plex",
                Enabled = true,
                Settings = new Dictionary<string, string?>
                {
                    [PlexSourceProvider.KeyServerUrl] = settings.PlexServerUrl,
                    [PlexSourceProvider.KeyToken] = settings.PlexToken,
                    [PlexSourceProvider.KeyStereoAudio] = settings.PlexStereoAudio.ToString(),
                    [PlexSourceProvider.KeyLibraries] = JsonSerializer.Serialize(settings.PlexLibraries),
                },
            });
        }

        return configs;
    }

    /// <summary>
    /// Returns provider metadata for a type id — display name, description, whether multiple
    /// instances are allowed, and the settings schema — for the settings UI to render editable
    /// fields without a live registry. Returns null for an unknown type id.
    /// </summary>
    public static (string DisplayName, string? Description, bool SupportsMultipleInstances, IReadOnlyList<PluginSettingDescriptor> Schema)? DescribeProvider(string typeId)
    {
        var p = CreateProvider(typeId);
        return p == null ? null : (p.DisplayName, p.Description, p.SupportsMultipleInstances, p.GetSettingsSchema());
    }

    /// <summary>Provider type ids that can be added by the user (i.e. support multiple instances).</summary>
    public static IReadOnlyList<(string TypeId, string DisplayName)> AddableProviders() =>
        new[] { PlexSourceProvider.PlexTypeId }
            .Select(id => (id, CreateProvider(id)!.DisplayName))
            .ToList();

    private static IPhosphorSourceProvider? CreateProvider(string typeId) => typeId switch
    {
        YouTubeSourceProvider.YouTubeTypeId => new YouTubeSourceProvider(),
        PlexSourceProvider.PlexTypeId => new PlexSourceProvider(),
        _ => null,
    };

    /// <summary>
    /// Builds a transient (non-registered) source instance from a config, for the settings UI to
    /// query capabilities and invoke <see cref="IConfigurable"/> actions (e.g. Plex "browse
    /// libraries") without touching the live registry. Returns null for unknown providers.
    /// </summary>
    public static IPhosphorSource? BuildTransientSource(PluginInstanceConfig cfg, System.Net.Http.HttpClient http)
    {
        var provider = cfg.TypeId switch
        {
            YouTubeSourceProvider.YouTubeTypeId => (IPhosphorSourceProvider)new YouTubeSourceProvider(http),
            PlexSourceProvider.PlexTypeId => new PlexSourceProvider(),
            _ => null,
        };
        return provider?.CreateInstance(cfg.InstanceId, cfg.Settings);
    }

    /// <summary>
    /// Returns a human-readable list of the capabilities a source implements (e.g. "Search",
    /// "Download/Cache"), for display under each source in the settings UI. Order is stable and
    /// roughly follows the discovery→playback→setup flow.
    /// </summary>
    public static IReadOnlyList<string> DescribeCapabilities(IPhosphorSource source)
    {
        var caps = new List<string>();
        if (source is ITextSearchCapable) caps.Add("Search");
        if (source is IPlaylistChannelDiscovery) caps.Add("Playlists/Channels");
        if (source is IBrowsable) caps.Add("Browse");
        if (source is IPagedBrowsable) caps.Add("Paged browse");
        if (source is IPlayableResolver) caps.Add("Playback");
        if (source is IDownloadable) caps.Add("Download/Cache");
        if (source is IGaplessCapable) caps.Add("Gapless");
        if (source is IUpdatable) caps.Add("Self-update");
        if (source is IConfigurable) caps.Add("Setup actions");
        return caps;
    }
}
