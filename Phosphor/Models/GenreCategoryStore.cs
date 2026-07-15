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
    /// <summary>The Plex source instance this entry belongs to (multi-server). Null = legacy/first server.</summary>
    public string? PlexInstanceId { get; set; }
    public bool PlexHubsEnabled { get; set; }
    public bool PlexPlaylistsEnabled { get; set; }
    public bool IsPlex => PlexLibraryKey != null;

    // Generic plug-in source tile fields (when set, this entry is a root tile for an IBrowsable
    // plug-in source — local-folder, future Jellyfin, …). Only serializable identity is persisted;
    // the opaque browse SourceState is recovered at runtime from the live registry by matching
    // (SourceInstanceId, SourceCategoryId).
    public string? SourceInstanceId { get; set; }
    public string? SourceCategoryId { get; set; }
    public string? SourceTypeId { get; set; }
    public bool IsGenericSource => SourceInstanceId != null;

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

    /// <summary>One root tile for a generic IBrowsable plug-in source.</summary>
    public sealed record SourceTile(string InstanceId, string CategoryId, string DisplayName, string Icon, string TypeId);

    /// <summary>
    /// Syncs generic plug-in source root tiles (Plex, local-folder, future Jellyfin, …) into the
    /// entry list: prune entries whose (instance, category) is no longer present, preserve user
    /// customizations (icon/name/position/visibility) for survivors, and add new tiles. Keyed by
    /// (SourceInstanceId, SourceCategoryId). Also one-time-prunes legacy bespoke Plex tile entries.
    /// </summary>
    public static void SyncSourceTiles(List<GenreCategoryEntry> entries, IReadOnlyList<SourceTile> tiles)
    {
        // One-time cleanup: legacy bespoke Plex tile entries (IsPlex, no SourceInstanceId) are
        // superseded by generic source tiles. Prune them so they don't linger as dead entries.
        entries.RemoveAll(e => e.IsPlex && !e.IsGenericSource);

        var validPairs = new HashSet<(string, string)>(
            tiles.Select(t => (t.InstanceId, t.CategoryId)));

        // Prune generic-source entries no longer backed by a live tile.
        entries.RemoveAll(e => e.IsGenericSource
            && !validPairs.Contains((e.SourceInstanceId!, e.SourceCategoryId ?? "")));

        foreach (var t in tiles)
        {
            var existing = entries.FirstOrDefault(e =>
                e.SourceInstanceId == t.InstanceId && (e.SourceCategoryId ?? "") == t.CategoryId);
            if (existing != null)
            {
                // Preserve user icon/name/position/visibility; keep type id current.
                existing.SourceTypeId = t.TypeId;
            }
            else
            {
                entries.Add(new GenreCategoryEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = t.DisplayName,
                    Icon = t.Icon,
                    SourceInstanceId = t.InstanceId,
                    SourceCategoryId = t.CategoryId,
                    SourceTypeId = t.TypeId,
                    IsVisible = true,
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
