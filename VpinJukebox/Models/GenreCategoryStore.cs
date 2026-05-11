using System.Text.Json;

namespace VpinJukebox;

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

    /// <summary>
    /// Loads categories from categories.json. Returns an empty list if the file is missing or invalid.
    /// </summary>
    public static List<GenreCategoryEntry> Load()
    {
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
    /// Saves the category list to categories.json.
    /// </summary>
    public static void Save(List<GenreCategoryEntry> entries)
    {
        try
        {
            var json = JsonSerializer.Serialize(entries, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            DebugLog.Log("GenreCategoryStore", $"Failed to save categories.json: {ex.Message}");
        }
    }
}
