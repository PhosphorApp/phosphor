using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phosphor.YtDlp;

// ─────────────────────────────────────────────────────────────────────────────
//  PHASE 1 SPIKE — THROWAWAY. Not wired into the app. Safe to delete.
//
//  Proves the "Option B" (direct process invocation) path for yt-dlp:
//    • shells out to yt-dlp.exe (mirrors PrefetchCache.MuxWithFfmpegAsync)
//    • parses --dump-single-json into the neutral DTOs from the analysis doc
//      (Appendix A: MediaFormat / VideoMetadata / ChapterMarker)
//    • resolves playable stream URLs via -g (what VLC's Media/AddSlave consume)
//
//  This is the seed for a future YtDlpVideoEngine : IVideoEngine. It intentionally
//  does NOT depend on any app state so it can be exercised in isolation and cannot
//  affect normal startup/playback. See YT-DLP_MIGRATION_ANALYSIS.md.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Neutral format DTO — the engine-agnostic replacement for YoutubeExplode's
/// *StreamInfo types (see analysis Appendix A). Only carries the fields the
/// current StreamSelector policy + video overlay actually use.
/// </summary>
public sealed record SpikeMediaFormat(
    string FormatId,
    string? Url,
    int Width,
    int Height,
    long BitrateBps,
    string VideoCodec,
    string AudioCodec,
    string Container);

/// <summary>Metadata the search/metadata engine would surface (Videos.GetAsync today).</summary>
public sealed record SpikeVideoMetadata(
    string Id,
    string Title,
    string Uploader,
    TimeSpan? Duration,
    string? Description,
    IReadOnlyList<ChapterMarker> Chapters,
    IReadOnlyList<SpikeMediaFormat> Formats);

/// <summary>
/// Throwaway direct-process yt-dlp resolver used to validate feasibility (Phase 1).
/// </summary>
public sealed class YtDlpSpike
{
    private readonly string _ytDlpPath;

    public YtDlpSpike(string? ytDlpPath = null)
    {
        _ytDlpPath = ytDlpPath ?? ResolveYtDlpPath();
    }

    /// <summary>
    /// Locate yt-dlp.exe next to the app (copied via csproj, like ffmpeg.exe),
    /// falling back to whatever is on PATH.
    /// </summary>
    public static string ResolveYtDlpPath()
    {
        var local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yt-dlp.exe");
        return File.Exists(local) ? local : "yt-dlp";
    }

    /// <summary>
    /// Runs <c>--dump-single-json</c> and projects the result into neutral DTOs.
    /// Mirrors the future IVideoEngine metadata + format-listing responsibility.
    /// </summary>
    public async Task<SpikeVideoMetadata?> GetMetadataAsync(string videoId, CancellationToken ct = default)
    {
        var url = ToWatchUrl(videoId);
        var (exitCode, stdout, stderr) = await RunAsync(
            new[] { "--no-warnings", "--dump-single-json", url }, ct);

        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            DebugLog.Log("YtDlpSpike", $"dump-single-json failed ({exitCode}): {Trim(stderr)}");
            return null;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<YtDlpInfoJson>(stdout);
            if (dto == null) return null;

            var chapters = (dto.Chapters ?? new List<YtDlpChapterJson>())
                .Select(c => new ChapterMarker
                {
                    Title = c.Title ?? "",
                    StartTime = TimeSpan.FromSeconds(c.StartTime ?? 0),
                    EndTime = TimeSpan.FromSeconds(c.EndTime ?? 0),
                })
                .ToList();

            var formats = (dto.Formats ?? new List<YtDlpFormatJson>())
                .Select(MapFormat)
                .ToList();

            return new SpikeVideoMetadata(
                dto.Id ?? videoId,
                dto.Title ?? "",
                dto.Uploader ?? "",
                dto.Duration is > 0 ? TimeSpan.FromSeconds(dto.Duration.Value) : null,
                dto.Description,
                chapters,
                formats);
        }
        catch (Exception ex)
        {
            DebugLog.Log("YtDlpSpike", $"JSON parse failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Runs <c>-g</c> with a format expression and returns the resolved playable
    /// URL(s). For "bv*+ba" this yields two URLs: [video, audio] — exactly the
    /// pair BackglassWindow feeds to Media + AddSlave. URLs are short-lived and
    /// IP-bound, so callers must resolve fresh per play (never persist).
    /// </summary>
    public async Task<IReadOnlyList<string>> ResolvePlayableUrlsAsync(
        string videoId, string formatExpr = "bv*+ba/b", CancellationToken ct = default)
    {
        var (exitCode, stdout, stderr) = await RunAsync(
            new[] { "--no-warnings", "-f", formatExpr, "-g", ToWatchUrl(videoId) }, ct);

        if (exitCode != 0)
        {
            DebugLog.Log("YtDlpSpike", $"-g failed ({exitCode}): {Trim(stderr)}");
            return Array.Empty<string>();
        }

        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    // ── process plumbing (mirrors PrefetchCache.MuxWithFfmpegAsync) ──

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

    private static SpikeMediaFormat MapFormat(YtDlpFormatJson f)
    {
        // yt-dlp reports bitrates in kbps (tbr/vbr/abr). Prefer tbr, else vbr+abr.
        double kbps = f.Tbr ?? ((f.Vbr ?? 0) + (f.Abr ?? 0));
        return new SpikeMediaFormat(
            FormatId: f.FormatId ?? "",
            Url: f.Url,
            Width: f.Width ?? 0,
            Height: f.Height ?? 0,
            BitrateBps: (long)(kbps * 1000),
            VideoCodec: NormalizeCodec(f.Vcodec),
            AudioCodec: NormalizeCodec(f.Acodec),
            Container: f.Ext ?? "");
    }

    private static string NormalizeCodec(string? codec)
        => string.IsNullOrEmpty(codec) || codec == "none" ? "" : codec;

    private static string ToWatchUrl(string videoId)
        => videoId.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? videoId
            : $"https://www.youtube.com/watch?v={videoId}";

    private static string Trim(string s)
        => s.Length <= 400 ? s : s[..400];

    // ── JSON shapes (subset of yt-dlp --dump-single-json) ──

    private sealed class YtDlpInfoJson
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("uploader")] public string? Uploader { get; set; }
        [JsonPropertyName("duration")] public double? Duration { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("chapters")] public List<YtDlpChapterJson>? Chapters { get; set; }
        [JsonPropertyName("formats")] public List<YtDlpFormatJson>? Formats { get; set; }
    }

    private sealed class YtDlpChapterJson
    {
        [JsonPropertyName("start_time")] public double? StartTime { get; set; }
        [JsonPropertyName("end_time")] public double? EndTime { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
    }

    private sealed class YtDlpFormatJson
    {
        [JsonPropertyName("format_id")] public string? FormatId { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("ext")] public string? Ext { get; set; }
        [JsonPropertyName("width")] public int? Width { get; set; }
        [JsonPropertyName("height")] public int? Height { get; set; }
        [JsonPropertyName("vcodec")] public string? Vcodec { get; set; }
        [JsonPropertyName("acodec")] public string? Acodec { get; set; }
        [JsonPropertyName("tbr")] public double? Tbr { get; set; }
        [JsonPropertyName("vbr")] public double? Vbr { get; set; }
        [JsonPropertyName("abr")] public double? Abr { get; set; }
    }
}
