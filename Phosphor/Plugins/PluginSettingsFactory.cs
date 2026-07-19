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
    /// <summary>
    /// Produces the default seed of plug-in instances for a fresh install (or an older settings file
    /// with no <c>PluginInstances</c>): a single YouTube instance with schema defaults. Plex and other
    /// sources are added by the user via the Plug-ins tab — there are no flat fields to migrate from
    /// (the General→Video and Plex tabs were retired).
    /// </summary>
    public static List<PluginInstanceConfig> FromAppSettings(AppSettings settings)
    {
        return new List<PluginInstanceConfig>
        {
            // ── YouTube (single instance, schema defaults) ──
            new()
            {
                TypeId = YouTubeSourceProvider.YouTubeTypeId,
                InstanceId = "youtube",
                DisplayName = "YouTube",
                Enabled = true,
                Settings = new Dictionary<string, string?>
                {
                    [YouTubeSourceProvider.KeySearchEngine] = SearchEngineKind.YoutubeExplode.ToString(),
                    [YouTubeSourceProvider.KeyVideoEngine] = VideoEngineKind.YoutubeExplode.ToString(),
                    [YouTubeSourceProvider.KeyVideoQuality] = VideoQualityPreference.High.ToString(),
                    [YouTubeSourceProvider.KeyPreferStereo] = bool.TrueString,
                },
            },
        };
    }

    /// <summary>
    /// Reads the YouTube instance's playback config (engine/quality/stereo) from a persisted
    /// <see cref="PluginInstanceConfig"/> list — the single source of truth now that the flat
    /// General→Video settings are retired. Missing keys/instance fall back to sensible defaults.
    /// </summary>
    public static (SearchEngineKind Search, VideoEngineKind Video, VideoQualityPreference Quality, bool PreferStereo)
        ReadYouTubePlayback(IEnumerable<PluginInstanceConfig> instances)
    {
        var yt = instances.FirstOrDefault(c => c.TypeId == YouTubeSourceProvider.YouTubeTypeId);
        var s = yt?.Settings;

        string? Get(string key) => s != null && s.TryGetValue(key, out var v) ? v : null;

        var search = Enum.TryParse<SearchEngineKind>(Get(YouTubeSourceProvider.KeySearchEngine), out var se)
            ? se : SearchEngineKind.YoutubeExplode;
        var video = Enum.TryParse<VideoEngineKind>(Get(YouTubeSourceProvider.KeyVideoEngine), out var ve)
            ? ve : VideoEngineKind.YoutubeExplode;
        var quality = Enum.TryParse<VideoQualityPreference>(Get(YouTubeSourceProvider.KeyVideoQuality), out var vq)
            ? vq : VideoQualityPreference.High;
        var stereo = !bool.TryParse(Get(YouTubeSourceProvider.KeyPreferStereo), out var st) || st;

        return (search, video, quality, stereo);
    }

    /// <summary>
    /// Returns provider metadata for a type id — display name, description, whether multiple
    /// instances are allowed, whether the provider is experimental, and the settings schema — for the
    /// settings UI to render editable fields without a live registry. Returns null for an unknown type id.
    /// </summary>
    public static (string DisplayName, string? Description, bool SupportsMultipleInstances, bool IsExperimental, IReadOnlyList<PluginSettingDescriptor> Schema)? DescribeProvider(string typeId)
    {
        var p = CreateProvider(typeId);
        return p == null
            ? null
            : (p.DisplayName, p.Description, p.SupportsMultipleInstances,
               p is Phosphor.Plugin.Abstractions.IExperimental, p.GetSettingsSchema());
    }

    /// <summary>
    /// Returns the setting keys a provider declares as secret (masked, DPAPI-encryptable) — i.e. any
    /// descriptor whose <see cref="PluginSettingDescriptor.Type"/> is <see cref="PluginSettingType.Secret"/>
    /// or whose <see cref="PluginSettingDescriptor.Secret"/> flag is set. Used by <c>AppSettings</c> to
    /// know which values in an instance's settings blob to encrypt/decrypt at the persistence boundary.
    /// Returns an empty set for unknown providers.
    /// </summary>
    public static IReadOnlyCollection<string> SecretKeysFor(string typeId)
    {
        var info = DescribeProvider(typeId);
        if (info == null) return [];
        return info.Value.Schema
            .Where(d => d.Type == PluginSettingType.Secret || d.Secret)
            .Select(d => d.Key)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Provider type ids that can be added by the user (i.e. support multiple instances).</summary>
    public static IReadOnlyList<(string TypeId, string DisplayName)> AddableProviders()
    {
        var list = new List<(string TypeId, string DisplayName)>
        {
            (PlexSourceProvider.PlexTypeId, CreateProvider(PlexSourceProvider.PlexTypeId)!.DisplayName),
        };
        // Any discovered third-party provider can be added by the user (single- or multi-instance).
        foreach (var p in DiscoveredProviders.All)
            list.Add((p.TypeId, p.DisplayName));
        return list;
    }

    private static IPhosphorSourceProvider? CreateProvider(string typeId) => typeId switch
    {
        YouTubeSourceProvider.YouTubeTypeId => new YouTubeSourceProvider(),
        PlexSourceProvider.PlexTypeId => new PlexSourceProvider(),
        _ => DiscoveredProviders.Get(typeId),
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
            _ => DiscoveredProviders.Get(cfg.TypeId),
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
        if (source is IScopedSearchable) caps.Add("In-view search");
        if (source is IPlaylistChannelDiscovery) caps.Add("Playlists/Channels");
        if (source is IBrowsable) caps.Add("Browse");
        if (source is IPagedBrowsable) caps.Add("Paged browse");
        if (source is IPlayableResolver) caps.Add("Playback");
        if (source is IDownloadable) caps.Add("Download/Cache");
        if (source is IGaplessCapable) caps.Add("Gapless");
        if (source is IUpdatable) caps.Add("Self-update");
        if (source is IConnectionTestable) caps.Add("Connection test");
        if (source is IRefreshable) caps.Add("Rescan/catalog");
        if (source is IConfigurable) caps.Add("Setup actions");
        return caps;
    }
}
