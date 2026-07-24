using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace Phosphor;

/// <summary>
/// Severity/verbosity level for a log entry. Entries below <see cref="DebugLog.MinimumLevel"/> are
/// dropped cheaply at the call site (before formatting), so verbose <see cref="Trace"/> diagnostics
/// can stay permanently in the code and only surface when the user raises the level.
/// </summary>
public enum LogLevel
{
    Trace = 0,   // very chatty per-frame/per-item diagnostics (e.g. thumbnail loads)
    Debug = 1,   // developer-facing detail (the historical default)
    Info = 2,    // notable milestones (status, cache stores/invalidations)
    Warning = 3, // recoverable problems (decode fallback, load miss)
    Error = 4,   // failures / exceptions
}

/// <summary>
/// Centralized debug logger. Writes to Phosphor_Debug_yyyyMMdd.log when enabled.
/// All file I/O happens on a single background thread so callers (including the
/// VLC audio callback thread and the WASAPI render thread) are never blocked.
/// </summary>
public static class DebugLog
{
    public static bool Enabled { get; set; }

    /// <summary>
    /// Minimum level that is written. Entries below this are discarded at the call site before any
    /// string formatting. Defaults to <see cref="LogLevel.Debug"/> to preserve historical behavior
    /// (legacy category/message calls log at Debug).
    /// </summary>
    public static LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

    // Bounded queue: if logging falls behind, drop oldest rather than block producers
    // or grow unbounded. 10K entries is ~minutes of normal logging.
    private const int MaxQueuedEntries = 10_000;
    private static readonly ConcurrentQueue<string> _queue = new();
    private static readonly ManualResetEventSlim _signal = new(false);
    private static readonly object _writerLock = new();
    private static Thread? _writerThread;
    private static volatile bool _shutdown;
    private static long _droppedCount;

    private static string LogDirectory
    {
        get
        {
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string LogPath => Path.Combine(
        LogDirectory,
        $"Phosphor_Debug_{DateTime.Now:yyyyMMdd}.log");

    public static void Log(string message)
    {
        // Legacy/untagged call: filtered at Debug severity but labeled [GENERIC] (not [Debug]) so a
        // post-hoc scan can distinguish "not yet migrated to an explicit level" from a deliberate Debug.
        if (!Enabled || LogLevel.Debug < MinimumLevel) return;
        Enqueue(Format(GenericTag, message));
    }

    public static void Log(string category, string message) =>
        Log($"[{category}] {message}");

    // ── Level-aware overloads ────────────────────────────────────────────────────
    // Existing string/string-string calls keep logging at Debug severity but are labeled [GENERIC]
    // (see above). New call sites pass an explicit level, which is stamped into the line as [Level];
    // Trace lines stay silent at the default MinimumLevel and only appear when raised.

    public static void Log(LogLevel level, string message)
    {
        if (!Enabled || level < MinimumLevel) return;
        Enqueue(Format(LevelTag(level), message));
    }

    public static void Log(LogLevel level, string category, string message) =>
        Log(level, $"[{category}] {message}");

    public static void LogException(string context, Exception? ex)
    {
        if (!Enabled || ex == null || LogLevel.Error < MinimumLevel) return;
        Log(LogLevel.Error, $"[EXCEPTION] [{context}] {ex.GetType().Name}: {ex.Message}");
    }

    // Label used for legacy/untagged calls so unmigrated log sites are easy to spot/grep.
    private const string GenericTag = "GENERIC";

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "Trace",
        LogLevel.Debug => "Debug",
        LogLevel.Info => "Info",
        LogLevel.Warning => "Warning",
        LogLevel.Error => "Error",
        _ => GenericTag,
    };

    // Single formatting point: timestamp + level/status tag + message. Formatting happens on the
    // calling thread (cheap, allocation-only) so the timestamp reflects when the event happened.
    private static string Format(string tag, string message) =>
        $"[{DateTime.Now:HH:mm:ss.fff}] [{tag}] {message}";

    // Bounded enqueue — if we're already at the cap, drop the oldest entry to keep producers
    // wait-free. This guarantees we never stall the audio path under pathological I/O.
    private static void Enqueue(string line)
    {
        if (_queue.Count >= MaxQueuedEntries)
        {
            _queue.TryDequeue(out _);
            Interlocked.Increment(ref _droppedCount);
        }

        _queue.Enqueue(line);
        EnsureWriterStarted();
        _signal.Set();
    }

    private static void EnsureWriterStarted()
    {
        if (_writerThread != null) return;
        lock (_writerLock)
        {
            if (_writerThread != null) return;
            _writerThread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "DebugLog-Writer"
            };
            _writerThread.Start();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
        }
    }

    private static void WriterLoop()
    {
        var buffer = new StringBuilder(8192);
        while (!_shutdown)
        {
            _signal.Wait(TimeSpan.FromMilliseconds(500));
            _signal.Reset();
            DrainOnce(buffer);
        }
        // Final drain at shutdown
        DrainOnce(buffer);
    }

    private static void DrainOnce(StringBuilder buffer)
    {
        if (_queue.IsEmpty) return;

        buffer.Clear();
        while (_queue.TryDequeue(out var line))
        {
            buffer.Append(line).Append(Environment.NewLine);
            // Batch up to ~64KB per write to amortize file I/O cost
            if (buffer.Length >= 65_536) break;
        }

        long dropped = Interlocked.Exchange(ref _droppedCount, 0);
        if (dropped > 0)
            buffer.Append($"[{DateTime.Now:HH:mm:ss.fff}] [DebugLog] WARNING: dropped {dropped} log entries (queue overflow)").Append(Environment.NewLine);

        if (buffer.Length == 0) return;

        try
        {
            File.AppendAllText(LogPath, buffer.ToString());
        }
        catch
        {
            // Swallow — never let a logging failure propagate.
        }
    }

    private static void Shutdown()
    {
        _shutdown = true;
        _signal.Set();
        try { _writerThread?.Join(TimeSpan.FromSeconds(2)); } catch { }
    }
}
