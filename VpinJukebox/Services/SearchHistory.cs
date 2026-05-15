using System.Text.Json;

namespace VpinJukebox;

public class SearchHistory
{
    private static readonly string HistoryPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "search_history.json");

    private readonly List<string> _searches = new();
    private const int MaxEntries = 128;

    public IReadOnlyList<string> Searches => _searches;

    public void Add(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;

        // Remove duplicates, then insert at top
        _searches.RemoveAll(s => s.Equals(query, StringComparison.OrdinalIgnoreCase));
        _searches.Insert(0, query);

        while (_searches.Count > MaxEntries)
            _searches.RemoveAt(_searches.Count - 1);

        Save();
    }

    public void Clear()
    {
        _searches.Clear();
        Save();
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_searches, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(HistoryPath, json);
        }
        catch { }
    }

    public static SearchHistory Load()
    {
        var history = new SearchHistory();
        if (!File.Exists(HistoryPath)) return history;

        try
        {
            var json = File.ReadAllText(HistoryPath);
            var items = JsonSerializer.Deserialize<List<string>>(json);
            if (items != null)
                history._searches.AddRange((items.Count > MaxEntries ? items.Take(MaxEntries) : items).Distinct(StringComparer.OrdinalIgnoreCase));
        }
        catch { }

        return history;
    }
}
