using System.Net.Http;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.YouTube;

/// <summary>
/// Provider (type + factory) for the in-box YouTube source. Single-instance: there is only
/// one YouTube. Exposes the YoutubeExplode-vs-yt-dlp engine choice and playback preferences
/// as declarative settings the host renders in the generic Plug-ins tab; the source itself
/// makes the actual engine determination internally.
/// </summary>
public sealed class YouTubeSourceProvider : IPhosphorSourceProvider
{
    public const string YouTubeTypeId = "youtube";

    public const string KeySearchEngine = "searchEngine";
    public const string KeyVideoEngine = "videoEngine";
    public const string KeyVideoQuality = "videoQuality";
    public const string KeyPreferStereo = "preferStereo";

    private readonly HttpClient? _http;

    public YouTubeSourceProvider(HttpClient? http = null) => _http = http;

    public string TypeId => YouTubeTypeId;
    public string DisplayName => "YouTube";
    public Version ApiVersion => PluginApi.Current;

    /// <summary>Only one YouTube instance makes sense.</summary>
    public bool SupportsMultipleInstances => false;

    public IReadOnlyList<PluginSettingDescriptor> GetSettingsSchema() =>
    [
        new(KeySearchEngine, "Search engine", PluginSettingType.Enum, DefaultValue: "YoutubeExplode",
            HelpText: "Backend used for search/discovery.")
        {
            EnumValues = ["YoutubeExplode", "YtDlp"],
        },
        new(KeyVideoEngine, "Video engine", PluginSettingType.Enum, DefaultValue: "YoutubeExplode",
            HelpText: "Backend used to resolve/download streams. Falls back automatically if unavailable.")
        {
            EnumValues = ["YoutubeExplode", "YtDlp"],
        },
        new(KeyVideoQuality, "Video quality", PluginSettingType.Enum, DefaultValue: "High")
        {
            EnumValues = ["Low", "Medium", "High", "Max"],
        },
        new(KeyPreferStereo, "Prefer stereo audio", PluginSettingType.Bool, DefaultValue: "true",
            HelpText: "Avoid surround tracks in favor of stereo."),
    ];

    public IPhosphorSource CreateInstance(string instanceId, IReadOnlyDictionary<string, string?> settings)
        => new YouTubeSource(instanceId, settings, _http);
}
