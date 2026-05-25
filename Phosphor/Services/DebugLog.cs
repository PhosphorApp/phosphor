using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace Phosphor;

/// <summary>
/// Centralized debug logger. Writes to Phosphor_Debug_yyyyMMdd.log when enabled.
/// All file I/O happens on a single background thread so callers (including the
/// VLC audio callback thread and the WASAPI render thread) are never blocked.
/// </summary>
public static class DebugLog
{
    public static bool Enabled { get; set; }

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
        if (!Enabled) return;

        // Format on the calling thread (cheap, allocation-only) so timestamps
        // reflect when the event happened, not when the writer drained it.
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";

        // Bounded enqueue — if we're already at the cap, drop the oldest entry
        // to keep producers wait-free. This guarantees we never stall the audio
        // path even under pathological I/O conditions.
        if (_queue.Count >= MaxQueuedEntries)
        {
            _queue.TryDequeue(out _);
            Interlocked.Increment(ref _droppedCount);
        }

        _queue.Enqueue(line);
        EnsureWriterStarted();
        _signal.Set();
    }

    public static void Log(string category, string message) =>
        Log($"[{category}] {message}");

    public static void LogException(string context, Exception? ex)
    {
        if (!Enabled || ex == null) return;
        Log($"[EXCEPTION] [{context}] {ex.GetType().Name}: {ex.Message}");
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
