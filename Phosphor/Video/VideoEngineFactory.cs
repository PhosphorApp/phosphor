namespace Phosphor.Video;

/// <summary>
/// Creates the configured <see cref="IVideoEngine"/> implementation. This is the
/// single switch point between YoutubeExplode and yt-dlp for the video path.
/// </summary>
public static class VideoEngineFactory
{
    public static IVideoEngine Create(VideoEngineKind kind) => kind switch
    {
        VideoEngineKind.YtDlp => new YtDlpVideoEngine(),
        _ => new YoutubeExplodeVideoEngine(),
    };
}
