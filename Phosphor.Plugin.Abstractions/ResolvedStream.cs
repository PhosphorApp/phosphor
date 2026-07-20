namespace Phosphor.Plugin.Abstractions;

/// <summary>
/// How a resolved stream's <see cref="ResolvedStream.PrimaryUri"/> should be interpreted.
/// The transport is deliberately open-ended: YouTube and Plex return short-lived HTTP(S)
/// URLs today, but a local-folder source resolves to a file path, and future sources may
/// use other schemes (e.g. custom protocols the host feeds straight to the media engine).
/// The host inspects this to decide how to hand the URI to the player / cache pipeline.
/// </summary>
public enum StreamTransport
{
    /// <summary>An HTTP or HTTPS URL (typically short-lived / IP-bound; resolve fresh per play).</summary>
    Http,

    /// <summary>A path to a local file already present on disk.</summary>
    File,

    /// <summary>
    /// Any other URI/MRL the host should pass through to the media engine verbatim
    /// (e.g. an rtsp/smb/custom scheme). The host makes no assumptions about it.
    /// </summary>
    Other,
}

/// <summary>Layout of the resolved media: separate tracks, one muxed stream, or audio only.</summary>
public enum StreamLayout
{
    /// <summary>Separate video-only + audio-only sources (audio attached as a slave track).</summary>
    SeparateVideoAudio,

    /// <summary>A single source carrying both video and audio.</summary>
    Muxed,

    /// <summary>Audio only (no video).</summary>
    AudioOnly,
}

/// <summary>
/// A resolved, playable stream description returned by <see cref="IPlayableResolver"/>.
/// <see cref="Transport"/> tells the host <em>how</em> to consume the URIs (HTTP URL, file
/// path, or other), keeping the contract agnostic to where the media lives. For
/// <see cref="StreamLayout.SeparateVideoAudio"/>, <see cref="PrimaryUri"/> is the video and
/// <see cref="AudioSlaveUri"/> is the audio to attach; otherwise <see cref="AudioSlaveUri"/>
/// is <c>null</c>.
/// </summary>
public sealed record ResolvedStream(
    StreamTransport Transport,
    StreamLayout Layout,
    string PrimaryUri,
    string? AudioSlaveUri = null,
    string? Resolution = null)
{
    /// <summary>
    /// Optional per-stream request headers (e.g. cookies, referer) the host should apply
    /// when the transport is <see cref="StreamTransport.Http"/>. Ignored for other transports.
    /// </summary>
    public IReadOnlyDictionary<string, string>? HttpHeaders { get; init; }

    /// <summary>
    /// True when this is a continuous <em>live</em> stream with no fixed duration or seekable
    /// timeline (e.g. a radio channel). The host treats these specially: no progress/seek UI, no
    /// duration, and no playlist auto-advance (the stream never "ends"). Defaults to <c>false</c>
    /// for ordinary finite media.
    /// </summary>
    public bool IsLiveStream { get; init; }

    /// <summary>
    /// Optional short, human-readable audio-selection tag for the status bar (e.g. " (Stereo)",
    /// " (Surround)"), reflecting the audio stream the source chose while resolving. <c>null</c>/empty
    /// when there is nothing noteworthy to show. Lets a source surface its stereo/surround decision
    /// without the host knowing source specifics.
    /// </summary>
    public string? AudioTag { get; init; }
}
