using System.Text.Json;

namespace VpinJukebox;

public class HistoryEntry
{
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string ThumbnailUrl { get; set; } = "";
    public string VideoId { get; set; } = "";
    public DateTime PlayedAt { get; set; }
    public string PlayedAtDisplay => PlayedAt.ToString("g");
}

public class PlayHistory
{
    private const int MaxEntries = 1000;

    private static readonly string HistoryPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "history.json");

    private readonly List<HistoryEntry> _entries = new();

    public IReadOnlyList<HistoryEntry> Entries => _entries;

    public void Add(VideoItem item)
    {
        _entries.Insert(0, new HistoryEntry
        {
            Title = item.Title,
            Author = item.Author,
            ThumbnailUrl = item.ThumbnailUrl,
            VideoId = item.VideoId,
            PlayedAt = DateTime.Now
        });

        // Keep a reasonable max
        while (_entries.Count > MaxEntries)
            _entries.RemoveAt(_entries.Count - 1);

        Save();
    }

    public void Purge()
    {
        _entries.Clear();
        Save();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(HistoryPath, json);
        }
        catch { }
    }

    public static PlayHistory Load()
    {
        var history = new PlayHistory();
        if (!File.Exists(HistoryPath)) return history;

        try
        {
            var json = File.ReadAllText(HistoryPath);
            var items = JsonSerializer.Deserialize<List<HistoryEntry>>(json);
            if (items != null)
            {
                // Trim to max on load in case file grew beyond limit
                history._entries.AddRange(items.Count > MaxEntries ? items.Take(MaxEntries) : items);
            }
        }
        catch { }

        return history;
    }
}
