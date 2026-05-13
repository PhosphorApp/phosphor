using System.IO;
using System.Text.Json;

namespace VpinJukebox;

/// <summary>
/// A single entry in the preset monitor log, recording presets that rendered black.
/// </summary>
public class ProjectMPresetMonitorEntry
{
    public DateTime Timestamp { get; set; }
    public string PresetPath { get; set; } = "";
    public string Action { get; set; } = "";
    public double TopAvgLuminance { get; set; }
}

/// <summary>
/// Persists a log of projectM presets that were detected as rendering black frames.
/// Written to <c>logs/ProjectMPresetMonitor.json</c>.
/// </summary>
public static class ProjectMPresetMonitorLog
{
    private const int MaxEntries = 1024;

    private static readonly string LogDir = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "logs");

    private static readonly string LogPath = Path.Combine(LogDir, "ProjectMPresetMonitor.json");

    private static readonly object _lock = new();
    private static List<ProjectMPresetMonitorEntry>? _entries;

    private static List<ProjectMPresetMonitorEntry> Entries
    {
        get
        {
            _entries ??= Load();
            return _entries;
        }
    }

    public static void Add(string presetRelativePath, string action, double topAvgLuminance = 0)
    {
        lock (_lock)
        {
            Entries.Add(new ProjectMPresetMonitorEntry
            {
                Timestamp = DateTime.Now,
                PresetPath = presetRelativePath,
                Action = action,
                TopAvgLuminance = Math.Round(topAvgLuminance, 3),
            });

            while (Entries.Count > MaxEntries)
                Entries.RemoveAt(0);

            Save();
        }
    }

    public static List<ProjectMPresetMonitorEntry> GetEntries()
    {
        lock (_lock)
        {
            return new List<ProjectMPresetMonitorEntry>(Entries);
        }
    }

    private static List<ProjectMPresetMonitorEntry> Load()
    {
        try
        {
            if (File.Exists(LogPath))
            {
                var json = File.ReadAllText(LogPath);
                return JsonSerializer.Deserialize<List<ProjectMPresetMonitorEntry>>(json) ?? [];
            }
        }
        catch { }
        return [];
    }

    private static void Save()
    {
        try
        {
            if (!Directory.Exists(LogDir))
                Directory.CreateDirectory(LogDir);
            var json = JsonSerializer.Serialize(Entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(LogPath, json);
        }
        catch { }
    }
}
