namespace Phosphor.Plugins;

/// <summary>
/// A configured instance of a plug-in source, decoupled from the concrete <see cref="AppSettings"/>
/// schema. The <see cref="SourceRegistry"/> builds live sources from a collection of these, so the
/// registry no longer reaches into app-settings fields directly. Today these are derived from the
/// flat settings by <see cref="PluginSettingsFactory"/>; once the generic Plug-ins settings UI
/// exists, they become the persisted, user-editable configuration (enabling multiple instances of
/// a source, e.g. two Plex servers).
/// </summary>
public sealed class PluginInstanceConfig
{
    /// <summary>The provider type id this instance belongs to (e.g. "youtube", "plex").</summary>
    public string TypeId { get; set; } = "";

    /// <summary>Stable id unique to this configured instance (e.g. "youtube", "plex", "plex-2").</summary>
    public string InstanceId { get; set; } = "";

    /// <summary>User-facing label (e.g. "YouTube", "Home Plex"). Optional; the source may default it.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Whether this instance is active. Disabled instances are skipped by the registry.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The declarative settings blob for this instance (keys are provider-defined).</summary>
    public Dictionary<string, string?> Settings { get; set; } = new();

    /// <summary>
    /// Caching policy for this instance. <c>null</c> means "use the capability default" (cache when
    /// the source implements <c>IDownloadable</c>); <c>true</c>/<c>false</c> lets the user force it
    /// on/off. Consumed by <c>JukeboxViewModel.IsItemCacheable</c> via
    /// <c>SourceRegistry.CachingPolicy</c>, and editable in the Plug-ins settings tab.
    /// </summary>
    public bool? AllowCaching { get; set; }

    /// <summary>
    /// True when this config is behaviorally identical to <paramref name="other"/> — same type,
    /// display name, enabled/caching flags, and settings blob. The <see cref="SourceRegistry"/> uses
    /// this to decide whether a live source instance can be kept as-is (avoiding a needless
    /// dispose/rebuild that would tear down active connections — e.g. an in-flight live stream).
    /// </summary>
    public bool ConfigEquals(PluginInstanceConfig other)
    {
        if (other is null) return false;
        if (!string.Equals(TypeId, other.TypeId, StringComparison.Ordinal)) return false;
        if (!string.Equals(DisplayName ?? "", other.DisplayName ?? "", StringComparison.Ordinal)) return false;
        if (Enabled != other.Enabled) return false;
        if (AllowCaching != other.AllowCaching) return false;
        return SettingsEqual(Settings, other.Settings);
    }

    private static bool SettingsEqual(Dictionary<string, string?> a, Dictionary<string, string?> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var bv)) return false;
            if (!string.Equals(kv.Value ?? "", bv ?? "", StringComparison.Ordinal)) return false;
        }
        return true;
    }
}
