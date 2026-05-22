using System.Text.Json;

namespace Phosphor;

/// <summary>
/// Entry in categories.json representing a user-configurable genre category.
/// </summary>
public class GenreCategoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    public string SearchTerm { get; set; } = "";
    public bool IsVisible { get; set; } = true;
    public bool IsSeparator { get; set; }
    public bool IsLineBreak { get; set; }

    // Plex fields (when set, this entry represents a Plex library tile)
    public string? PlexLibraryKey { get; set; }
    public string? PlexLibraryType { get; set; }
    public bool PlexHubsEnabled { get; set; }
    public bool PlexPlaylistsEnabled { get; set; }
    public bool IsPlex => PlexLibraryKey != null;
    public int SortOrder { get; set; }
}

/// <summary>
/// Loads and saves genre categories from categories.json.
/// The file is expected to ship with the application.
/// </summary>
public static class GenreCategoryStore
{
    private static readonly string FilePath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "categories.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    // In-memory cache to avoid re-reading the file immediately after Save,
    // which can stall for seconds due to Windows Defender real-time scanning.
    private static List<GenreCategoryEntry>? _cachedEntries;

    /// <summary>
    /// Loads categories from categories.json. Returns an empty list if the file is missing or invalid.
    /// </summary>
    public static List<GenreCategoryEntry> Load()
    {
        if (_cachedEntries != null)
            return _cachedEntries;

        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var entries = JsonSerializer.Deserialize<List<GenreCategoryEntry>>(json, JsonOptions);
                if (entries != null && entries.Count > 0)
                {
                    // Back-fill IDs for entries migrated from older versions
                    bool needsSave = false;
                    foreach (var e in entries)
                    {
                        if (string.IsNullOrEmpty(e.Id))
                        {
                            e.Id = Guid.NewGuid().ToString("N");
                            needsSave = true;
                        }
                    }
                    if (needsSave)
                        Save(entries);
                    _cachedEntries = entries;
                    return entries;
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Log("GenreCategoryStore", $"Failed to load categories.json: {ex.Message}");
        }

        return [];
    }

    /// <summary>
    /// Ensures the genre category list contains entries for all current Plex libraries
    /// and removes entries for libraries that no longer exist. Preserves user customizations
    /// (icon, position) for existing entries. New entries are appended at the end.
    /// </summary>
    public static void SyncPlexLibraries(List<GenreCategoryEntry> entries, IReadOnlyList<PlexLibraryMapping> libraries)
    {
        // Build set of current library keys
        var currentKeys = new HashSet<string>(libraries.Select(l => l.Key));

        // Remove entries for libraries that no longer exist
        entries.RemoveAll(e => e.IsPlex && !currentKeys.Contains(e.PlexLibraryKey!));

        // Add/update entries for each library
        foreach (var lib in libraries)
        {
            var existing = entries.FirstOrDefault(e => e.PlexLibraryKey == lib.Key);
            if (existing != null)
            {
                // Update title and hub/playlist flags (user may have toggled these)
                existing.Name = $"Plex {lib.Title}";
                existing.PlexHubsEnabled = lib.HubsEnabled;
                existing.PlexPlaylistsEnabled = lib.PlaylistsEnabled;
                existing.PlexLibraryType = lib.Type;
            }
            else
            {
                entries.Add(new GenreCategoryEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = $"Plex {lib.Title}",
                    Icon = "\U0001f7e0",
                    PlexLibraryKey = lib.Key,
                    PlexLibraryType = lib.Type,
                    PlexHubsEnabled = lib.HubsEnabled,
                    PlexPlaylistsEnabled = lib.PlaylistsEnabled,
                    IsVisible = true
                });
            }
        }
    }

    /// <summary>
    /// Saves the category list to categories.json.
    /// </summary>
    public static void Save(List<GenreCategoryEntry> entries)
    {
        try
        {
            var json = JsonSerializer.Serialize(entries, JsonOptions);
            File.WriteAllText(FilePath, json);
            _cachedEntries = entries;
        }
        catch (Exception ex)
        {
            _cachedEntries = null;
            DebugLog.Log("GenreCategoryStore", $"Failed to save categories.json: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates the in-memory cache immediately and writes to disk on a background thread.
    /// </summary>
    public static void SaveInBackground(List<GenreCategoryEntry> entries)
    {
        _cachedEntries = entries;
        var json = JsonSerializer.Serialize(entries, JsonOptions);
        Task.Run(() =>
        {
            try
            {
                File.WriteAllText(FilePath, json);
            }
            catch (Exception ex)
            {
                DebugLog.Log("GenreCategoryStore", $"Failed to save categories.json: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Invalidates the in-memory cache, forcing the next Load to read from disk.
    /// </summary>
    public static void InvalidateCache() => _cachedEntries = null;
}
