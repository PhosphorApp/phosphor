using System.IO;
using System.Text.Json;

namespace VpinJukebox;

/// <summary>
/// A single entry in the ProjectM preset history log.
/// </summary>
public class ProjectMPresetLogEntry
{
    public DateTime Timestamp { get; set; }
    public string PresetPath { get; set; } = "";
    public string CutType { get; set; } = "";
}

/// <summary>
/// Persists the most recent ProjectM preset transitions to a JSON file.
/// Only automatic transitions are logged (not manual preview selections).
/// </summary>
public static class ProjectMPresetLog
{
    private const int MaxEntries = 1024;

    private static readonly string LogPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "projectm_preset_history.json");

    public static bool Enabled { get; set; }

    private static readonly object _lock = new();
    private static List<ProjectMPresetLogEntry>? _entries;

    private static List<ProjectMPresetLogEntry> Entries
    {
        get
        {
            if (_entries == null)
                _entries = Load();
            return _entries;
        }
    }

    public static void Add(string presetRelativePath, string cutType)
    {
        if (!Enabled)
            return;

        lock (_lock)
        {
            Entries.Add(new ProjectMPresetLogEntry
            {
                Timestamp = DateTime.Now,
                PresetPath = presetRelativePath,
                CutType = cutType,
            });

            // Trim to max
            while (Entries.Count > MaxEntries)
                Entries.RemoveAt(0);

            Save();
        }
    }

    public static List<ProjectMPresetLogEntry> GetEntries()
    {
        lock (_lock)
        {
            return new List<ProjectMPresetLogEntry>(Entries);
        }
    }

    private static List<ProjectMPresetLogEntry> Load()
    {
        try
        {
            if (File.Exists(LogPath))
            {
                var json = File.ReadAllText(LogPath);
                return JsonSerializer.Deserialize<List<ProjectMPresetLogEntry>>(json) ?? [];
            }
        }
        catch { }
        return [];
    }

    private static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(Entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(LogPath, json);
        }
        catch { }
    }
}
