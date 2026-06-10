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
    /// <summary>
    /// VideoIds whose backing files should be deleted on the next <see cref="PurgeTransient"/>
    /// (called from app shutdown). Populated by <see cref="MarkTransient"/> when a cache
    /// entry is created opportunistically for a session-only purpose (e.g. enabling reliable
    /// scrubbing of an uncached YouTube video).
    /// </summary>
    private readonly HashSet<string> _transientIds = new();
    /// <summary>
    /// VideoIds that have been (or are being) cached via a *persistent* code path this
    /// session — i.e. <see cref="CacheVideoAsync"/> was called with allowDisabled=false.
    /// These are protected from <see cref="PurgeTransient"/> even if a transient cache
    /// job also raced for the same id (e.g. CacheMode=Everything plus a mid-track scrub).
    /// </summary>
    private readonly HashSet<string> _persistentIds = new();
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
            return new CachedVideo(filePath, entry.Resolution, entry.Chapters);
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

    public async Task CacheVideoAsync(string videoId, VideoQualityPreference quality = VideoQualityPreference.High, bool preferStereo = false, TimeSpan? duration = null, List<ChapterMarker>? chapters = null, string? title = null, CancellationToken ct = default, bool allowDisabled = false)
    {
        if (!_enabled && !allowDisabled) return;
        if (!IsWithinClipLengthLimit(duration)) return;

        // Track persistent intent so a racing transient job (e.g. user scrubs while
        // CacheMode=Everything is downloading) can't cause PurgeTransient to remove
        // this entry on exit. Persistent intent always wins over transient.
        if (!allowDisabled)
        {
            lock (_lock)
            {
                _persistentIds.Add(videoId);
            }
        }

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
            var titleSuffix = !string.IsNullOrWhiteSpace(title) ? $"_{SanitizeFileName(title)}" : "";
            var muxedFile = $"{videoId}{titleSuffix}.mkv";
            var muxedPath = Path.Combine(CacheDir, muxedFile);

            // Write chapters XML if available
            string? chaptersPath = null;
            if (chapters != null && chapters.Count > 0)
                chaptersPath = WriteChaptersMetadata(videoId, chapters);

            var muxed = await MuxWithFfmpegAsync(videoPath, audioPath, muxedPath, chaptersPath, ct);

            // Remove intermediate files
            try { File.Delete(videoPath); } catch { }
            try { File.Delete(audioPath); } catch { }
            if (chaptersPath != null) try { File.Delete(chaptersPath); } catch { }

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
                    LastAccessed = DateTime.UtcNow,
                    Chapters = chapters
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
    /// Updates chapter data for a cached video entry.
    /// </summary>
    public void UpdateChapters(string videoId, List<ChapterMarker>? chapters)
    {
        if (chapters == null || chapters.Count == 0) return;
        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(e => e.VideoId == videoId);
            if (entry != null && entry.Chapters == null)
            {
                entry.Chapters = chapters;
                SaveIndex();
                DebugLog.Log("VideoCache", $"Stored {chapters.Count} chapters for {videoId}");
            }
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
            await CacheVideoAsync(video.VideoId, duration: video.Duration, chapters: video.Chapters, title: video.Title, ct: ct);
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

    /// <summary>
    /// Marks a videoId as "transient" so that its on-disk cache entry (if any) will be
    /// deleted by the next call to <see cref="PurgeTransient"/> (typically at app exit).
    /// Safe to call before or after the corresponding <see cref="CacheVideoAsync"/> finishes.
    /// No-op when the videoId has already been requested via a persistent cache path —
    /// persistent intent always wins so a mid-track scrub during CacheMode=Everything
    /// (or for a playlist item) does not cause the entry to be purged on exit.
    /// </summary>
    public void MarkTransient(string videoId)
    {
        if (string.IsNullOrEmpty(videoId)) return;
        lock (_lock)
        {
            if (_persistentIds.Contains(videoId))
            {
                DebugLog.Log("VideoCache", $"MarkTransient skipped for {videoId}: already marked persistent this session");
                return;
            }
            _transientIds.Add(videoId);
        }
    }

    /// <summary>
    /// Deletes all cache entries (and on-disk files) previously marked via <see cref="MarkTransient"/>.
    /// Intended to be called once at app shutdown. Non-transient entries, and any entries that
    /// were also touched by a persistent cache path this session, are left alone.
    /// </summary>
    public void PurgeTransient()
    {
        List<CacheEntry> toRemove;
        lock (_lock)
        {
            if (_transientIds.Count == 0) return;

            // Defense in depth: even if MarkTransient was called, never purge a videoId that
            // was also requested via a persistent path (CacheMode=Everything, playlist add, etc.).
            toRemove = _entries
                .Where(e => _transientIds.Contains(e.VideoId) && !_persistentIds.Contains(e.VideoId))
                .ToList();

            int skipped = _transientIds.Count(id => _persistentIds.Contains(id));
            if (skipped > 0)
                DebugLog.Log("VideoCache", $"PurgeTransient: keeping {skipped} entries that were also marked persistent");

            if (toRemove.Count == 0)
            {
                _transientIds.Clear();
                return;
            }

            DebugLog.Log("VideoCache", $"Purging {toRemove.Count} transient entries on exit");
            foreach (var entry in toRemove)
            {
                var filePath = Path.Combine(CacheDir, entry.FileName);
                try { if (File.Exists(filePath)) File.Delete(filePath); }
                catch (Exception ex) { DebugLog.Log("VideoCache", $"Transient delete failed for {entry.VideoId}: {ex.Message}"); }
                _entries.Remove(entry);
            }
            _transientIds.Clear();
            SaveIndex();
        }
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
    private static async Task<bool> MuxWithFfmpegAsync(string videoPath, string audioPath, string outputPath, string? chaptersPath, CancellationToken ct)
    {
        try
        {
            var ffmpegName = "ffmpeg";
            var localFfmpeg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(localFfmpeg))
                ffmpegName = localFfmpeg;

            var args = chaptersPath != null
                ? $"-i \"{videoPath}\" -i \"{audioPath}\" -f ffmetadata -i \"{chaptersPath}\" -map_metadata 2 -c copy -y \"{outputPath}\""
                : $"-i \"{videoPath}\" -i \"{audioPath}\" -c copy -y \"{outputPath}\"";

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpegName,
                Arguments = args,
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
                DebugLog.Log("VideoCache", $"ffmpeg exited with code {proc.ExitCode}: {stderr[..Math.Min(stderr.Length, 1000)]}");
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

    /// <summary>
    /// Writes an FFMETADATA1 chapters file for use with ffmpeg muxing.
    /// Returns the file path, or null if no chapters are provided.
    /// </summary>
    private static string? WriteChaptersMetadata(string videoId, List<ChapterMarker> chapters)
    {
        if (chapters == null || chapters.Count == 0)
            return null;

        var path = Path.Combine(CacheDir, $"{videoId}_chapters.txt");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(";FFMETADATA1");

        foreach (var ch in chapters)
        {
            sb.AppendLine("[CHAPTER]");
            sb.AppendLine("TIMEBASE=1/1000");
            sb.AppendLine($"START={(long)ch.StartTime.TotalMilliseconds}");
            sb.AppendLine($"END={(long)ch.EndTime.TotalMilliseconds}");
            sb.AppendLine($"title={EscapeFfmetadata(ch.Title)}");
        }

        File.WriteAllText(path, sb.ToString(), System.Text.Encoding.UTF8);
        DebugLog.Log("VideoCache", $"Wrote {chapters.Count} chapters to {Path.GetFileName(path)}");
        return path;
    }

    /// <summary>
    /// Escapes special characters for FFMETADATA1 format.
    /// </summary>
    private static string EscapeFfmetadata(string value) =>
        value.Replace("\\", "\\\\").Replace("=", "\\=").Replace(";", "\\;").Replace("#", "\\#").Replace("\n", "\\\n");

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());

        // Collapse runs of underscores/spaces into a single underscore
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, @"[_\s]{2,}", "_");

        // Limit length to avoid path issues
        if (sanitized.Length > 80)
            sanitized = sanitized[..80];

        // Trim trailing dots and spaces (Windows silently strips them, causing path mismatches)
        sanitized = sanitized.TrimEnd('.', ' ', '_');

        // Guard against empty or Windows-reserved names
        if (string.IsNullOrWhiteSpace(sanitized))
            return "untitled";

        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
            "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
        };
        if (reserved.Contains(sanitized))
            sanitized = $"_{sanitized}";

        return sanitized;
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
    public List<ChapterMarker>? Chapters { get; set; }
}

public record CachedVideo(string FilePath, string Resolution, List<ChapterMarker>? Chapters = null);
