namespace Phosphor.Playback;

using System.Collections.Specialized;
using System.ComponentModel;

/// <summary>
/// Owns the "active queue follows the active player" forwarding plumbing that previously lived on
/// <c>JukeboxViewModel</c> as a PropertyChanged/CollectionChanged switchboard. It subscribes to the
/// currently-active <see cref="PlayerQueue"/> and translates its change notifications into the VM's
/// active-* projection property names, raised through an injected callback. Re-pointing at a new queue
/// (on an active-player switch) unsubscribes the previous one and raises the full projection set.
///
/// This keeps the VM's active-* members as thin getters while the subscription bookkeeping lives here.
/// </summary>
public sealed class ActiveQueueForwarder
{
    // The VM projection property names this forwarder raises. Kept as constants so the mapping is
    // self-documenting and decoupled from the VM's member reflection.
    public const string ActiveQueue = "ActiveQueue";
    public const string ActiveCurrentQueueItem = "ActiveCurrentQueueItem";
    public const string HasActiveQueueItems = "HasActiveQueueItems";
    public const string ActiveQueueCountText = "ActiveQueueCountText";
    public const string ActiveRepeatEnabled = "ActiveRepeatEnabled";
    public const string ActiveAutoDjEnabled = "ActiveAutoDjEnabled";

    private readonly Action<string> _raise;
    private PlayerQueue? _subscribed;

    /// <summary>
    /// Creates the forwarder with the callback used to raise a VM projection property by name
    /// (typically the VM's <c>OnPropertyChanged</c>).
    /// </summary>
    public ActiveQueueForwarder(Action<string> raise) => _raise = raise;

    /// <summary>
    /// Re-points the forwarder at <paramref name="queue"/> (the newly-active player's queue),
    /// unsubscribing the previous queue first, then raises the full active-* projection so the panel
    /// refreshes for the switch.
    /// </summary>
    public void SwitchTo(PlayerQueue queue)
    {
        if (_subscribed != null)
        {
            _subscribed.PropertyChanged -= OnQueuePropertyChanged;
            _subscribed.Queue.CollectionChanged -= OnQueueCollectionChanged;
        }
        _subscribed = queue;
        _subscribed.PropertyChanged += OnQueuePropertyChanged;
        _subscribed.Queue.CollectionChanged += OnQueueCollectionChanged;
        RaiseAll();
    }

    /// <summary>Raises PropertyChanged for every active-queue projection member (used on active switch).</summary>
    public void RaiseAll()
    {
        _raise(ActiveQueue);
        _raise(ActiveCurrentQueueItem);
        _raise(HasActiveQueueItems);
        _raise(ActiveQueueCountText);
        _raise(ActiveRepeatEnabled);
        _raise(ActiveAutoDjEnabled);
    }

    private void OnQueuePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerQueue.CurrentQueueItem):
                _raise(ActiveCurrentQueueItem);
                break;
            case nameof(PlayerQueue.RepeatEnabled):
                _raise(ActiveRepeatEnabled);
                break;
            case nameof(PlayerQueue.AutoDjEnabled):
                _raise(ActiveAutoDjEnabled);
                break;
            case nameof(PlayerQueue.HasQueueItems):
                _raise(HasActiveQueueItems);
                _raise(ActiveQueueCountText);
                break;
        }
    }

    private void OnQueueCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _raise(HasActiveQueueItems);
        _raise(ActiveQueueCountText);
        _raise(ActiveCurrentQueueItem);
    }
}
