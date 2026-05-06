using System.IO;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace VpinJukebox;

/// <summary>
/// A lightweight prefetch cache that downloads the next track's streams to disk
/// for instant playback transitions. Keeps at most one video cached and purges
/// files as soon as they are consumed or no longer needed.
/// </summary>
public class PrefetchCache
{
    private static readonly string PrefetchDir = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "prefetch");

    private readonly YoutubeClient _youtube = new();
    private readonly object _lock = new();

    private string? _cachedVideoId;
    private string? _filePath;
    private string? _resolution;
    private CancellationTokenSource? _downloadCts;

    public PrefetchCache()
    {
        Directory.CreateDirectory(PrefetchDir);
        PurgeAll();
    }

    /// <summary>
    /// Returns prefetched file paths if available for this video ID, then removes from cache.
    /// </summary>
    public CachedVideo? TryConsume(string videoId)
    {
        lock (_lock)
        {
            if (_cachedVideoId != videoId) return null;
            if (_filePath == null || !File.Exists(_filePath)) return null;

            var result = new CachedVideo(_filePath, _resolution ?? "");
            DebugLog.Log("PrefetchCache", $"Consumed: {videoId} ({_resolution})");

            _cachedVideoId = null;
            _filePath = null;
            _resolution = null;

            return result;
        }
    }

    /// <summary>
    /// Returns prefetched file paths without consuming (for cache lookup without removal).
    /// </summary>
    public CachedVideo? TryGet(string videoId)
    {
        lock (_lock)
        {
            if (_cachedVideoId != videoId) return null;
            if (_filePath == null || !File.Exists(_filePath)) return null;
            return new CachedVideo(_filePath, _resolution ?? "");
        }
    }

    /// <summary>
    /// Downloads and caches streams for a video. Cancels any in-progress download first.
    /// </summary>
    public async Task PrefetchAsync(string videoId, VideoQualityPreference quality = VideoQualityPreference.High, bool preferStereo = false, CancellationToken ct = default)
    {
        // Already have this one?
        lock (_lock)
        {
            if (_cachedVideoId == videoId) return;
        }

        // Cancel previous download
        _downloadCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _downloadCts = cts;

        try
        {
            var manifest = await _youtube.Videos.Streams.GetManifestAsync(videoId, cts.Token);
            var videoStream = StreamSelector.SelectVideo(manifest, quality);
            var audioStream = StreamSelector.SelectAudio(manifest, preferStereo);
            if (videoStream == null || audioStream == null) return;

            var videoFile = Path.Combine(PrefetchDir, $"{videoId}_video.{videoStream.Container.Name}");
            var audioFile = Path.Combine(PrefetchDir, $"{videoId}_audio.{audioStream.Container.Name}");

            await _youtube.Videos.Streams.DownloadAsync(videoStream, videoFile, cancellationToken: cts.Token);
            await _youtube.Videos.Streams.DownloadAsync(audioStream, audioFile, cancellationToken: cts.Token);

            var resolution = $"{videoStream.VideoResolution.Width}x{videoStream.VideoResolution.Height}";

            // Mux into a single seekable .mkv
            var muxedFile = Path.Combine(PrefetchDir, $"{videoId}.mkv");
            var muxed = await MuxWithFfmpegAsync(videoFile, audioFile, muxedFile, cts.Token);

            // Remove intermediate files
            try { File.Delete(videoFile); } catch { }
            try { File.Delete(audioFile); } catch { }

            if (!muxed)
            {
                DebugLog.Log("PrefetchCache", $"Mux failed for {videoId}");
                try { File.Delete(muxedFile); } catch { }
                return;
            }

            lock (_lock)
            {
                PurgeFilesExcept(videoId);

                _cachedVideoId = videoId;
                _filePath = muxedFile;
                _resolution = resolution;
            }

            DebugLog.Log("PrefetchCache", $"Ready: {videoId} ({resolution})");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DebugLog.Log("PrefetchCache", $"Failed for {videoId}: {ex.Message}");
            CleanFiles(videoId);
        }
    }

    /// <summary>
    /// Purge all prefetch files (e.g. on shutdown or when disabling).
    /// </summary>
    public void PurgeAll()
    {
        lock (_lock)
        {
            _downloadCts?.Cancel();
            _cachedVideoId = null;
            _filePath = null;
            _resolution = null;
        }

        DeleteAllFiles();
    }

    /// <summary>
    /// Remove old prefetch files, keeping only files for the specified video ID.
    /// </summary>
    private void PurgeFilesExcept(string keepVideoId)
    {
        try
        {
            foreach (var file in Directory.GetFiles(PrefetchDir))
            {
                if (!Path.GetFileName(file).StartsWith(keepVideoId, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch { }
    }

    private void CleanFiles(string videoId)
    {
        try
        {
            foreach (var file in Directory.GetFiles(PrefetchDir)
                         .Where(f => Path.GetFileName(f).StartsWith(videoId, StringComparison.OrdinalIgnoreCase)))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }

    private static void DeleteAllFiles()
    {
        try
        {
            if (Directory.Exists(PrefetchDir))
            {
                foreach (var file in Directory.GetFiles(PrefetchDir))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch { }
    }

    private static async Task<bool> MuxWithFfmpegAsync(string videoPath, string audioPath, string outputPath, CancellationToken ct)
    {
        try
        {
            var ffmpegName = "ffmpeg";
            var localFfmpeg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(localFfmpeg))
                ffmpegName = localFfmpeg;

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpegName,
                Arguments = $"-i \"{videoPath}\" -i \"{audioPath}\" -c copy -y \"{outputPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return false;

            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
            {
                DebugLog.Log("PrefetchCache", $"ffmpeg exited with code {proc.ExitCode}: {stderr[..Math.Min(stderr.Length, 500)]}");
                try { File.Delete(outputPath); } catch { }
                return false;
            }

            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        }
        catch (Exception ex)
        {
            DebugLog.Log("PrefetchCache", $"ffmpeg mux failed: {ex.Message}");
            try { File.Delete(outputPath); } catch { }
            return false;
        }
    }
}
