using System.IO;
using System.Text.Json;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace Phosphor;

/// <summary>
/// Manages a local disk cache of downloaded video/audio streams for playlist videos.
/// Downloads separate video and audio streams, then muxes them into a single seekable
/// .mkv file using ffmpeg. Evicts oldest files when the configured size limit is exceeded.
/// </summary>
public class VideoCache
{
    private static readonly string CacheDir = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "cache");

    private static readonly string IndexPath = Path.Combine(CacheDir, "index.json");

    private readonly YoutubeClient _youtube = new();
    private readonly object _lock = new();
    private List<CacheEntry> _entries = new();
    private long _maxBytes;
    private bool _enabled;
    private int _maxClipLengthMinutes;

    public bool Enabled => _enabled;

    public VideoCache(bool enabled, double maxSizeGb, int maxClipLengthMinutes = 0)
    {
        _enabled = enabled;
        _maxBytes = (long)(maxSizeGb * 1024 * 1024 * 1024);
        _maxClipLengthMinutes = maxClipLengthMinutes;
        Directory.CreateDirectory(CacheDir);
        LoadIndex();
    }

    public void UpdateSettings(bool enabled, double maxSizeGb, int maxClipLengthMinutes = 0)
    {
        _enabled = enabled;
        _maxBytes = (long)(maxSizeGb * 1024 * 1024 * 1024);
        _maxClipLengthMinutes = maxClipLengthMinutes;

        if (enabled)
            Evict();
    }

    /// <summary>
    /// Returns cached file paths for a video if available, or null if not cached.
    /// </summary>
    public CachedVideo? TryGet(string videoId)
    {
        lock (_lock)
        {
            if (!_enabled && !_entries.Any())
                return null;

            var entry = _entries.FirstOrDefault(e => e.VideoId == videoId);
            if (entry == null)
            {
                DebugLog.Log("VideoCache", $"Miss: {videoId}");
                return null;
            }

            var filePath = Path.Combine(CacheDir, entry.FileName);

            if (!File.Exists(filePath))
            {
                DebugLog.Log("VideoCache", $"Stale entry removed (file missing): {videoId}");
                _entries.Remove(entry);
                SaveIndex();
                return null;
            }

            entry.LastAccessed = DateTime.UtcNow;
            SaveIndex();

            DebugLog.Log("VideoCache", $"Hit: {videoId} ({entry.Resolution}, {entry.SizeBytes / 1024 / 1024}MB)");
            return new CachedVideo(filePath, entry.Resolution);
        }
    }


    /// <summary>
    /// Download and cache a video's streams in the background.
    /// </summary>
    /// <summary>
    /// Returns true if the given duration is within the configured max clip length for caching.
    /// </summary>
    public bool IsWithinClipLengthLimit(TimeSpan? duration)
    {
        if (_maxClipLengthMinutes <= 0) return true;
        if (duration == null) return true; // unknown duration — allow caching
        return duration.Value.TotalMinutes <= _maxClipLengthMinutes;
    }

    public async Task CacheVideoAsync(string videoId, VideoQualityPreference quality = VideoQualityPreference.High, bool preferStereo = false, TimeSpan? duration = null, CancellationToken ct = default)
    {
        if (!_enabled) return;
        if (!IsWithinClipLengthLimit(duration)) return;

        // Already cached?
        lock (_lock)
        {
            if (_entries.Any(e => e.VideoId == videoId))
                return;
        }

        try
        {
            var manifest = await _youtube.Videos.Streams.GetManifestAsync(videoId, ct);
            var videoStream = StreamSelector.SelectVideo(manifest, quality);
            var audioStream = StreamSelector.SelectAudio(manifest, preferStereo);

            if (videoStream == null || audioStream == null) return;

            var videoFile = $"{videoId}_video.{videoStream.Container.Name}";
            var audioFile = $"{videoId}_audio.{audioStream.Container.Name}";
            var videoPath = Path.Combine(CacheDir, videoFile);
            var audioPath = Path.Combine(CacheDir, audioFile);

            await _youtube.Videos.Streams.DownloadAsync(videoStream, videoPath, cancellationToken: ct);
            await _youtube.Videos.Streams.DownloadAsync(audioStream, audioPath, cancellationToken: ct);

            var resolution = $"{videoStream.VideoResolution.Width}x{videoStream.VideoResolution.Height}";

            // Mux video+audio into a single .mkv with proper cue points for seeking.
            // Raw WebM streams from YouTube lack cue points, making them unseekable.
            var muxedFile = $"{videoId}.mkv";
            var muxedPath = Path.Combine(CacheDir, muxedFile);
            var muxed = await MuxWithFfmpegAsync(videoPath, audioPath, muxedPath, ct);

            // Remove intermediate files
            try { File.Delete(videoPath); } catch { }
            try { File.Delete(audioPath); } catch { }

            if (!muxed)
            {
                DebugLog.Log("VideoCache", $"Mux failed for {videoId} — ffmpeg may not be installed");
                try { File.Delete(muxedPath); } catch { }
                return;
            }

            var totalSize = new FileInfo(muxedPath).Length;

            lock (_lock)
            {
                _entries.Add(new CacheEntry
                {
                    VideoId = videoId,
                    FileName = muxedFile,
                    SizeBytes = totalSize,
                    Resolution = resolution,
                    CachedAt = DateTime.UtcNow,
                    LastAccessed = DateTime.UtcNow
                });
                SaveIndex();
                Evict();
            }

            DebugLog.Log("VideoCache", $"Stored: {videoId} ({totalSize / 1024 / 1024}MB, {resolution})");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DebugLog.Log("VideoCache", $"Download failed for {videoId}: {ex.Message}");
            // Clean up partial files
            CleanPartialFiles(videoId);
        }
    }

    /// <summary>
    /// Queue background caching for all videos in a playlist.
    /// </summary>
    public async Task CachePlaylistAsync(IEnumerable<VideoItem> videos, CancellationToken ct = default)
    {
        foreach (var video in videos)
        {
            if (ct.IsCancellationRequested) break;
            await CacheVideoAsync(video.VideoId, duration: video.Duration, ct: ct);
        }
    }

    public void Purge()
    {
        lock (_lock)
        {
            DebugLog.Log("VideoCache", $"Purging {_entries.Count} entries");
            _entries.Clear();
            SaveIndex();
        }

        try
        {
            if (Directory.Exists(CacheDir))
            {
                foreach (var file in Directory.GetFiles(CacheDir))
                {
                    if (!file.EndsWith("index.json", StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(file); }
                        catch { /* file in use */ }
                    }
                }
            }
        }
        catch { /* best effort */ }
    }

    public long GetTotalSizeBytes()
    {
        lock (_lock)
            return _entries.Sum(e => e.SizeBytes);
    }

    private void Evict()
    {
        // Must be called under _lock
        if (_maxBytes == 0) return; // Unlimited
        var totalSize = _entries.Sum(e => e.SizeBytes);
        if (totalSize <= _maxBytes) return;

        DebugLog.Log("VideoCache", $"Evicting: {totalSize / 1024 / 1024}MB exceeds {_maxBytes / 1024 / 1024}MB limit");

        // Sort by oldest accessed first
        var sorted = _entries.OrderBy(e => e.LastAccessed).ToList();

        foreach (var entry in sorted)
        {
            if (totalSize <= _maxBytes) break;

            var filePath = Path.Combine(CacheDir, entry.FileName);
            try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }

            totalSize -= entry.SizeBytes;
            _entries.Remove(entry);
            DebugLog.Log("VideoCache", $"Evicted: {entry.VideoId} ({entry.SizeBytes / 1024 / 1024}MB)");
        }

        SaveIndex();
    }

    private void CleanPartialFiles(string videoId)
    {
        try
        {
            foreach (var file in Directory.GetFiles(CacheDir)
                         .Where(f => Path.GetFileName(f).StartsWith(videoId, StringComparison.OrdinalIgnoreCase)))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Mux separate video and audio files into a single MKV container with
    /// proper cue points using ffmpeg. Returns true on success.
    /// </summary>
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
            if (proc == null)
            {
                DebugLog.Log("VideoCache", "ffmpeg process failed to start");
                return false;
            }

            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
            {
                DebugLog.Log("VideoCache", $"ffmpeg exited with code {proc.ExitCode}: {stderr[..Math.Min(stderr.Length, 500)]}");
                try { File.Delete(outputPath); } catch { }
                return false;
            }

            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        }
        catch (Exception ex)
        {
            DebugLog.Log("VideoCache", $"ffmpeg mux failed: {ex.Message}");
            try { File.Delete(outputPath); } catch { }
            return false;
        }
    }

    private void LoadIndex()
    {
        try
        {
            if (File.Exists(IndexPath))
            {
                var json = File.ReadAllText(IndexPath);
                _entries = JsonSerializer.Deserialize<List<CacheEntry>>(json) ?? new();

                if (_entries.Count > 0)
                    VerifyIndex();
            }
        }
        catch
        {
            _entries = new();
        }
    }

    /// <summary>
    /// Removes index entries whose backing files no longer exist on disk.
    /// Logs each removal to the debug log.
    /// </summary>
    private void VerifyIndex()
    {
        var removed = new List<CacheEntry>();

        foreach (var entry in _entries)
        {
            var filePath = Path.Combine(CacheDir, entry.FileName);
            if (!File.Exists(filePath))
                removed.Add(entry);
        }

        if (removed.Count == 0)
            return;

        foreach (var entry in removed)
        {
            _entries.Remove(entry);
            DebugLog.Log("VideoCache", $"Index verification: removed missing file entry '{entry.FileName}' (VideoId: {entry.VideoId})");
        }

        DebugLog.Log("VideoCache", $"Index verification complete: {removed.Count} stale entry(ies) removed");
        SaveIndex();
    }

    private void SaveIndex()
    {
        try
        {
            var json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(IndexPath, json);
        }
        catch { }
    }
}

public class CacheEntry
{
    public string VideoId { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Resolution { get; set; } = "";
    public DateTime CachedAt { get; set; }
    public DateTime LastAccessed { get; set; }
}

public record CachedVideo(string FilePath, string Resolution);
