namespace Phosphor.Plugins;

/// <summary>
/// Host-side copies of well-known plug-in identifiers the host must reference without depending on
/// the plug-in assemblies (which load across the dynamic plug-in boundary). These string values are
/// the contract between host and plug-in and must match the plug-in's own declarations.
/// </summary>
public static class KnownSourceTypeIds
{
    /// <summary>The YouTube source type id (must match the YouTube plug-in's provider).</summary>
    public const string YouTube = "youtube";
}

/// <summary>
/// Host-side copies of the YouTube plug-in's settings keys, used when the host seeds/reads the
/// YouTube instance config from <c>AppSettings</c>. Must match the plug-in's <c>YouTubeSourceProvider</c>.
/// </summary>
public static class YouTubeSettingKeys
{
    public const string SearchEngine = "searchEngine";
    public const string VideoEngine = "videoEngine";
    public const string VideoQuality = "videoQuality";
    public const string PreferStereo = "preferStereo";
}
