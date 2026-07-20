namespace Phosphor.Video;

/// <summary>
/// Host-side playback vocabulary for YouTube-style streaming. These DTOs used to live alongside
/// the video engine interface, but the engines now live in the YouTube plug-in. The host keeps
/// this vocabulary because the playback windows, caches, and the ViewModel consume it directly;
/// the ViewModel maps the plug-in contract's <c>ResolvedStream</c>/<c>SourceDownload</c>/
/// <c>SourceMetadata</c> onto these shapes at the boundary.
/// </summary>

/// <summary>Shape of a resolved live stream set.</summary>
public enum VideoStreamKind
{
    /// <summary>Separate video-only + audio-only streams (audio added as a VLC slave).</summary>
    SeparateVideoAudio,
    /// <summary>A single muxed stream carrying both video and audio.</summary>
    Muxed,
    /// <summary>Audio-only stream (no video).</summary>
    AudioOnly,
}

/// <summary>
/// Resolved playable stream URLs for live playback. For
/// <see cref="VideoStreamKind.SeparateVideoAudio"/>, <see cref="PrimaryUrl"/> is the
/// video-only URL and <see cref="AudioSlaveUrl"/> is the audio-only URL to attach as
/// a VLC slave. For <see cref="VideoStreamKind.Muxed"/> and
/// <see cref="VideoStreamKind.AudioOnly"/>, <see cref="AudioSlaveUrl"/> is <c>null</c>.
/// </summary>
public sealed record VideoStreams(
    VideoStreamKind Kind,
    string PrimaryUrl,
    string? AudioSlaveUrl,
    string Resolution);

/// <summary>
/// Raw downloaded stream files for the disk caches. When separate streams were produced,
/// <see cref="VideoFilePath"/> and <see cref="AudioFilePath"/> are both set; the caller muxes
/// them. Containers are reported so callers can name/mux correctly.
/// </summary>
public sealed record VideoDownload(
    string VideoFilePath,
    string AudioFilePath,
    string VideoContainer,
    string AudioContainer,
    string Resolution);

/// <summary>
/// Video metadata for chapter/duration enrichment. <see cref="Chapters"/> holds native markers
/// when the source exposes them; when empty, the caller falls back to parsing
/// <see cref="Description"/>. <see cref="Duration"/> and <see cref="UploadDate"/> may be null.
/// </summary>
public sealed record VideoMetadata(
    TimeSpan? Duration,
    string? Description,
    List<ChapterMarker> Chapters,
    DateTimeOffset? UploadDate = null);
