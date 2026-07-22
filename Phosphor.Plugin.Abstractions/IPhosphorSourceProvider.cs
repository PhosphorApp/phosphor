namespace Phosphor.Plugin.Abstractions;

/// <summary>
/// A plug-in <em>type</em> (the thing a DLL exports) and factory for configured
/// <see cref="IPhosphorSource"/> instances. Providers are discovered once (statically
/// referenced in-box, or scanned from the plug-ins folder later) and describe their
/// identity, settings schema, and whether they support multiple configured instances.
/// </summary>
/// <remarks>
/// The provider/instance split exists because some sources need more than one live
/// configuration at a time (e.g. two Plex servers). A provider with
/// <see cref="SupportsMultipleInstances"/> == <c>true</c> can be asked to create several
/// independent <see cref="IPhosphorSource"/>s, each with its own settings and id.
/// </remarks>
public interface IPhosphorSourceProvider
{
    /// <summary>Stable type id, e.g. "youtube", "plex", "jellyfin". Never localized.</summary>
    string TypeId { get; }

    /// <summary>Human-friendly name for the settings UI, e.g. "Plex".</summary>
    string DisplayName { get; }

    /// <summary>
    /// Optional author-provided description shown in the settings UI: setup instructions, a link to
    /// documentation, notes, etc. May be <c>null</c> or empty. Purely informational — the host
    /// renders it verbatim and makes no assumptions about its content or length.
    /// </summary>
    string? Description => null;

    /// <summary>
    /// Optional declaration that this source needs an external account or subscription to work (e.g.
    /// Vimeo requires an API token tied to a Vimeo account). Purely advisory metadata: the host
    /// surfaces a badge and a "Requires … Sign up: &lt;url&gt;" line in the Plug-ins settings tab so
    /// users know upfront that setup involves signing up somewhere. <c>null</c> (the default) means the
    /// source works with no account. This is distinct from a <see cref="PluginSettingType.Secret"/>
    /// field, which only says "a credential is stored," not "you must go create one, possibly paid."
    /// </summary>
    AccountRequirement? Account => null;

    /// <summary>
    /// The contract version this plug-in was built against. The host compares this to
    /// <see cref="PluginApi.Current"/> and refuses to load incompatible plug-ins.
    /// </summary>
    Version ApiVersion { get; }

    /// <summary>
    /// True when the user may configure more than one instance of this source
    /// (e.g. two Plex servers). Single-instance sources (YouTube) return false.
    /// </summary>
    bool SupportsMultipleInstances { get; }

    /// <summary>
    /// Logical names of host-bundled native tools this source needs at runtime (e.g.
    /// <c>"yt-dlp"</c>, <c>"ffmpeg"</c>), resolved by the host via
    /// <see cref="IPluginHost.GetToolPath"/>. Declaration is for load-time validation and
    /// visibility only: the host warns when a declared tool is missing (a clear startup
    /// diagnostic instead of a play-time failure) and can see which sources share a tool.
    /// The host does NOT acquire or provision tools from this list. Defaults to none.
    /// </summary>
    IReadOnlyList<string> RequiredTools => [];

    /// <summary>
    /// Declarative settings schema the host renders as a standard form (Tier 1 config).
    /// Interactive config (Tier 2, e.g. "browse libraries") is exposed per-instance via
    /// <see cref="IConfigurable"/>.
    /// </summary>
    IReadOnlyList<PluginSettingDescriptor> GetSettingsSchema();

    /// <summary>
    /// Creates a configured, independent source instance. The host owns
    /// <paramref name="instanceId"/> (stable, unique per configured instance) and the
    /// persisted <paramref name="settings"/> blob; the plug-in owns their meaning.
    /// </summary>
    IPhosphorSource CreateInstance(string instanceId, IReadOnlyDictionary<string, string?> settings);
}

/// <summary>
/// Advisory metadata describing an external account/subscription a source needs. The host renders it
/// as a badge plus a one-line notice (e.g. "Requires a free Vimeo account. Sign up: &lt;url&gt;");
/// it changes no behavior. The plug-in supplies the data, the host owns the presentation.
/// </summary>
/// <param name="Summary">
/// Short human-readable requirement, e.g. "a free Vimeo account" or "a paid Plex Pass". The host
/// composes this into "Requires {Summary}." so phrase it as a noun phrase without leading "Requires".
/// </param>
/// <param name="SignupUrl">Optional URL where the user can create the account; rendered as a link.</param>
/// <param name="IsPaid">True when the account/subscription costs money, so the host can say so upfront.</param>
public sealed record AccountRequirement(string Summary, string? SignupUrl = null, bool IsPaid = false);
