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
}
