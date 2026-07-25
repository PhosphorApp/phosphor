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

    // Generic plug-in source tile fields (when set, this entry is a root tile for a plug-in source).
    // Two flavors share these fields, distinguished by whether a SearchTerm is present:
    //   • Browse tile (IBrowsable — Plex, local-folder, …): no SearchTerm; drills into a tree. The
    //     opaque browse SourceState is recovered at runtime by matching (SourceInstanceId, SourceCategoryId).
    //   • Saved-search tile (ISavedSearchCategories — YouTube genre tiles): carries a SearchTerm the
    //     host runs against the bound source. Name/Icon/SearchTerm are plug-in-authoritative (refreshed
    //     on sync); the host owns only SortOrder + IsVisible.
    public string? SourceInstanceId { get; set; }
    public string? SourceCategoryId { get; set; }
    public string? SourceTypeId { get; set; }

    /// <summary>A generic browse (IBrowsable) source root tile: bound to a source, no saved search.</summary>
    public bool IsGenericSource => SourceInstanceId != null && string.IsNullOrEmpty(SearchTerm);

    /// <summary>A saved-search source tile (ISavedSearchCategories): bound to a source, runs a stored search.</summary>
    public bool IsSavedSearchSource => SourceInstanceId != null && !string.IsNullOrEmpty(SearchTerm);

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
            DebugLog.Log(LogLevel.Warning, "GenreCategoryStore", $"Failed to load categories.json: {ex.Message}");
        }

        return [];
    }

    /// <summary>One root tile for a generic IBrowsable plug-in source.</summary>
    public sealed record SourceTile(string InstanceId, string CategoryId, string DisplayName, string Icon, string TypeId);

    /// <summary>One saved-search tile for an ISavedSearchCategories plug-in source (e.g. a YouTube genre tile).</summary>
    public sealed record SavedSearchTile(string InstanceId, string CategoryId, string DisplayName, string Icon, string SearchTerm, string TypeId);

    /// <summary>
    /// Syncs generic plug-in source root tiles (Plex, local-folder, future Jellyfin, …) into the
    /// entry list: prune entries whose (instance, category) is no longer present, preserve user
    /// customizations (icon/name/position/visibility) for survivors, and add new tiles. Keyed by
    /// (SourceInstanceId, SourceCategoryId).
    /// </summary>
    public static void SyncSourceTiles(List<GenreCategoryEntry> entries, IReadOnlyList<SourceTile> tiles)
    {
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
    /// Syncs saved-search plug-in source tiles (e.g. YouTube genre tiles) into the entry list. The
    /// plug-in owns each tile's <em>existence</em> and its <see cref="GenreCategoryEntry.SearchTerm"/>
    /// (the search criteria are edited in the plug-in's category editor, not the DMD/Appearance tab);
    /// the host owns the display glyph (<see cref="GenreCategoryEntry.Icon"/>), <c>Name</c>,
    /// <c>SortOrder</c> and <c>IsVisible</c>, which are preserved for survivors (DMD/Appearance is the
    /// override authority). New tiles seed Name/Icon from the plug-in default. Keyed by
    /// (SourceInstanceId, SourceCategoryId); prunes tiles no longer present; appends new ones.
    /// </summary>
    public static void SyncSavedSearchTiles(List<GenreCategoryEntry> entries, IReadOnlyList<SavedSearchTile> tiles)
    {
        var validPairs = new HashSet<(string, string)>(
            tiles.Select(t => (t.InstanceId, t.CategoryId)));

        // Prune saved-search entries no longer backed by a live tile.
        entries.RemoveAll(e => e.IsSavedSearchSource
            && !validPairs.Contains((e.SourceInstanceId!, e.SourceCategoryId ?? "")));

        int nextSort = entries.Count == 0 ? 0 : entries.Max(e => e.SortOrder) + 1;

        foreach (var t in tiles)
        {
            var existing = entries.FirstOrDefault(e =>
                e.SourceInstanceId == t.InstanceId && (e.SourceCategoryId ?? "") == t.CategoryId);
            if (existing != null)
            {
                // Plug-in owns SearchTerm; host-owned Name/Icon/SortOrder/IsVisible are preserved.
                existing.SearchTerm = t.SearchTerm;
                existing.SourceTypeId = t.TypeId;
            }
            else
            {
                entries.Add(new GenreCategoryEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = t.DisplayName,
                    Icon = t.Icon,
                    SearchTerm = t.SearchTerm,
                    SourceInstanceId = t.InstanceId,
                    SourceCategoryId = t.CategoryId,
                    SourceTypeId = t.TypeId,
                    IsVisible = true,
                    SortOrder = nextSort++,
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
            DebugLog.Log(LogLevel.Warning, "GenreCategoryStore", $"Failed to save categories.json: {ex.Message}");
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
                DebugLog.Log(LogLevel.Warning, "GenreCategoryStore", $"Failed to save categories.json: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Invalidates the in-memory cache, forcing the next Load to read from disk.
    /// </summary>
    public static void InvalidateCache() => _cachedEntries = null;
}
