namespace Phosphor.Playback;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

/// <summary>
/// The per-player play queue + its navigation cursor and persistence. Phase 3: each player
/// (Backglass = Player 1, Topper = Player 2) owns its own <see cref="PlayerQueue"/> so the two
/// windows queue and advance independently. This skeleton (Stage A, slice 1) holds only the queue
/// STATE — the <see cref="Queue"/> collection, the <see cref="QueueIndex"/> cursor and its derived
/// getters, and the per-queue Repeat / AutoDJ toggles. Navigation (PlayNext / advance / shuffle /
/// AutoDJ fill / prefetch), and persistence to a per-queue json path, are relocated onto this type
/// in later slices.
///
/// The VM's existing queue members become thin delegators to <c>Player1</c>'s queue (the same proven
/// pattern as the Phase 2 now-playing delegation) so all current XAML bindings and <c>queue.json</c>
/// persistence keep working unchanged while the 2nd player is off.
/// </summary>
public sealed class PlayerQueue : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    /// <summary>
    /// A short human name for this queue's owner (e.g. "Backglass" / "Topper"). Shown in the queue
    /// title bar in place of the item count when the second player is enabled.
    /// </summary>
    public string OwnerName { get; }

    /// <summary>Absolute path of this queue's persistence file (e.g. queue.json / queue_topper.json).</summary>
    private readonly string _persistPath;

    public PlayerQueue(string ownerName, string persistPath)
    {
        OwnerName = ownerName;
        _persistPath = persistPath;
    }

    /// <summary>The ordered play queue for this player.</summary>
    public ObservableCollection<VideoItem> Queue { get; } = new();

    private int _queueIndex = -1;
    /// <summary>
    /// Index of the currently playing item in the queue. -1 means nothing is playing from the queue.
    /// </summary>
    public int QueueIndex
    {
        get => _queueIndex;
        set
        {
            if (SetProperty(ref _queueIndex, value))
            {
                if (value >= 0)
                    LastKnownQueueIndex = value;
                OnPropertyChanged(nameof(CurrentQueueItem));
                OnPropertyChanged(nameof(HasNextTrack));
            }
        }
    }

    /// <summary>Remembers the last non-negative queue index for persistence across sessions.</summary>
    public int LastKnownQueueIndex { get; private set; } = -1;

    /// <summary>The queue item currently being played, or null if nothing is playing.</summary>
    public VideoItem? CurrentQueueItem => _queueIndex >= 0 && _queueIndex < Queue.Count ? Queue[_queueIndex] : null;

    /// <summary>True when the queue has at least one item.</summary>
    public bool HasQueueItems => Queue.Count > 0;

    private bool _repeatEnabled;
    public bool RepeatEnabled
    {
        get => _repeatEnabled;
        set
        {
            if (SetProperty(ref _repeatEnabled, value))
            {
                OnPropertyChanged(nameof(HasNextTrack));
                RepeatEnabledChanged?.Invoke(value);
            }
        }
    }

    public event Action<bool>? RepeatEnabledChanged;

    private bool _autoDjEnabled;
    public bool AutoDjEnabled
    {
        get => _autoDjEnabled;
        set
        {
            if (SetProperty(ref _autoDjEnabled, value))
            {
                OnPropertyChanged(nameof(HasNextTrack));
                AutoDjEnabledChanged?.Invoke(value);
            }
        }
    }

    public event Action<bool>? AutoDjEnabledChanged;

    /// <summary>True while an AutoDJ refill is in flight for this queue (guards re-entrancy per player).</summary>
    public bool IsAutoDjFilling { get; set; }

    /// <summary>
    /// Returns true if there is a next track available to play (considering repeat and AutoDJ). Used
    /// by the host to decide whether to show the idle screen between tracks.
    /// </summary>
    public bool HasNextTrack
    {
        get
        {
            if (Queue.Count == 0) return false;
            int nextIndex = _queueIndex + 1;
            if (nextIndex < Queue.Count) return true;
            if (_repeatEnabled && Queue.Count > 0) return true;
            if (_autoDjEnabled) return true;
            return false;
        }
    }

    /// <summary>Raises change notifications for the derived count/next-track getters after the
    /// <see cref="Queue"/> collection is mutated (add/remove/clear).</summary>
    public void NotifyQueueContentsChanged()
    {
        OnPropertyChanged(nameof(HasQueueItems));
        OnPropertyChanged(nameof(HasNextTrack));
        OnPropertyChanged(nameof(CurrentQueueItem));
    }

    // ── Persistence ──

    /// <summary>
    /// Persists the current queue to this queue's json path. Called on every collection change and on
    /// exit so metadata enriched during the session (upload date, accurate duration, chapters populated
    /// on play) survives a restart — the per-item enrichment does not raise <see cref="Queue"/>'s
    /// CollectionChanged, so it is not otherwise re-saved.
    /// </summary>
    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(SanitizeForPersist(Queue), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_persistPath, json);
        }
        catch { }
    }

    /// <summary>
    /// Produces the queue list to persist, stripping the ephemeral resolved URLs from live-stream
    /// items so an expired URL is never written to disk. On restore those items re-resolve from their
    /// id at play time; persisting the URL would make the player hand VLC a dead link and surface a
    /// misleading "stream timed out".
    /// </summary>
    private static List<VideoItem> SanitizeForPersist(IEnumerable<VideoItem> queue)
    {
        var list = new List<VideoItem>();
        foreach (var item in queue)
        {
            if (item.IsLiveStream && (item.StreamUrl != null || item.AudioStreamUrl != null))
            {
                var copy = item.ShallowCopy();
                copy.StreamUrl = null;
                copy.AudioStreamUrl = null;
                list.Add(copy);
            }
            else
            {
                list.Add(item);
            }
        }
        return list;
    }

    /// <summary>Loads the queue from this queue's json path (no-op if the file does not exist).</summary>
    public void Load()
    {
        if (!File.Exists(_persistPath)) return;
        try
        {
            var json = File.ReadAllText(_persistPath);
            var items = JsonSerializer.Deserialize<List<VideoItem>>(json);
            if (items != null)
                foreach (var item in items)
                    Queue.Add(item);
        }
        catch { }
    }
}
