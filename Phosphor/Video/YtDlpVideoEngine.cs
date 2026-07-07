using System.Diagnostics;

namespace Phosphor.Video;

/// <summary>
/// <see cref="IVideoEngine"/> backed by the external <c>yt-dlp.exe</c>.
/// </summary>
/// <remarks>
/// Phase 3 scope: the <b>download</b> path (used by <c>VideoCache</c> /
/// <c>PrefetchCache</c>) is native yt-dlp — it downloads separate best video-only and
/// audio-only streams into the destination dir, and the caches mux them exactly as
/// before (the seam contract is unchanged). <see cref="ResolveStreamsAsync"/> (live
/// playback) delegates to <see cref="YoutubeExplodeVideoEngine"/> for now; native
/// yt-dlp live resolution (<c>-g</c>) arrives in Phase 4.
/// </remarks>
public sealed class YtDlpVideoEngine : IVideoEngine
{
    private readonly string _ytDlpPath;
    private readonly YoutubeExplodeVideoEngine _liveFallback = new();

    public YtDlpVideoEngine(string? ytDlpPath = null)
    {
        _ytDlpPath = ytDlpPath ?? ResolveYtDlpPath();
    }

    /// <summary>
    /// Locates <c>yt-dlp.exe</c> next to the app (copied via csproj, like ffmpeg.exe),
    /// falling back to whatever is on PATH.
    /// </summary>
    public static string ResolveYtDlpPath()
    {
        var local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yt-dlp.exe");
        return File.Exists(local) ? local : "yt-dlp";
    }

    /// <summary>
    /// Live playback resolution is delegated to YoutubeExplode in Phase 3.
    /// Phase 4 replaces this with native yt-dlp <c>-g</c> URL resolution.
    /// </summary>
    public Task<VideoStreams?> ResolveStreamsAsync(
        string videoId,
        VideoQualityPreference quality,
        bool preferStereo,
        bool audioOnly,
        CancellationToken ct = default)
        => _liveFallback.ResolveStreamsAsync(videoId, quality, preferStereo, audioOnly, ct);

    public async Task<VideoDownload?> DownloadStreamsAsync(
        string videoId,
        VideoQualityPreference quality,
        bool preferStereo,
        string destinationDir,
        CancellationToken ct = default)
    {
        var url = ToWatchUrl(videoId);

        // Download best video-only and best audio-only streams separately, mirroring the
        // YoutubeExplode engine's output shape so the caches mux exactly as before.
        var videoFormat = $"bv*{HeightCap(quality)}";
        var audioFormat = preferStereo ? "ba[audio_channels<=2]/ba" : "ba";

        var videoPath = await DownloadOneAsync(url, videoFormat,
            Path.Combine(destinationDir, "%(id)s_video.%(ext)s"), ct);
        if (videoPath == null) return null;

        var audioPath = await DownloadOneAsync(url, audioFormat,
            Path.Combine(destinationDir, "%(id)s_audio.%(ext)s"), ct);
        if (audioPath == null)
        {
            TryDelete(videoPath);
            return null;
        }

        var resolution = await GetResolutionAsync(url, videoFormat, ct);

        return new VideoDownload(
            videoPath,
            audioPath,
            GetExtension(videoPath),
            GetExtension(audioPath),
            resolution);
    }

    // ── yt-dlp invocations ──

    /// <summary>
    /// Downloads a single selected format and returns the exact final file path
    /// (<c>--print after_move:filepath</c>), or null on failure.
    /// </summary>
    private async Task<string?> DownloadOneAsync(string url, string format, string outputTemplate, CancellationToken ct)
    {
        var (exitCode, stdout, stderr) = await RunAsync(new[]
        {
            "--no-warnings",
            "-f", format,
            "-o", outputTemplate,
            "--print", "after_move:filepath",
            "--no-simulate",
            url,
        }, ct);

        if (exitCode != 0)
        {
            DebugLog.Log("YtDlpVideoEngine", $"download failed ({exitCode}) fmt={format}: {Trim(stderr)}");
            return null;
        }

        var path = stdout.Trim();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            DebugLog.Log("YtDlpVideoEngine", $"download produced no file for fmt={format}");
            return null;
        }

        return path;
    }

    /// <summary>Resolves the "WxH" resolution of the selected video format (no download).</summary>
    private async Task<string> GetResolutionAsync(string url, string videoFormat, CancellationToken ct)
    {
        var (exitCode, stdout, _) = await RunAsync(new[]
        {
            "--no-warnings",
            "-f", videoFormat,
            "--print", "%(width)sx%(height)s",
            url,
        }, ct);

        var res = stdout.Trim();
        return exitCode == 0 && res.Contains('x') ? res : "";
    }

    private async Task<(int exitCode, string stdout, string stderr)> RunAsync(
        IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ytDlpPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }

    // ── helpers ──

    /// <summary>Maps the quality ceiling onto a yt-dlp height filter (mirrors StreamSelector).</summary>
    private static string HeightCap(VideoQualityPreference pref) => pref switch
    {
        VideoQualityPreference.Low => "[height<=480]",
        VideoQualityPreference.Medium => "[height<=720]",
        VideoQualityPreference.High => "[height<=1080]",
        _ => "", // Max — no cap
    };

    private static string GetExtension(string path)
        => Path.GetExtension(path).TrimStart('.');

    private static string ToWatchUrl(string videoId)
        => videoId.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? videoId
            : $"https://www.youtube.com/watch?v={videoId}";

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    private static string Trim(string s)
        => s.Length <= 400 ? s : s[..400];
}
