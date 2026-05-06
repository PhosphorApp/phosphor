using System.IO;

namespace VpinJukebox;

/// <summary>
/// Centralized debug logger. Writes to VPinJukebox_Debug_yyyyMMdd.log when enabled.
/// </summary>
public static class DebugLog
{
    public static bool Enabled { get; set; }

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
        $"VPinJukebox_Debug_{DateTime.Now:yyyyMMdd}.log");

    public static void Log(string message)
    {
        if (!Enabled) return;
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch { }
    }

    public static void Log(string category, string message) =>
        Log($"[{category}] {message}");

    public static void LogException(string context, Exception? ex)
    {
        if (!Enabled || ex == null) return;
        Log($"[EXCEPTION] [{context}] {ex.GetType().Name}: {ex.Message}");
    }
}
