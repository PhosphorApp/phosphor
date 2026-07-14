using System.Text.Json;
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
}
