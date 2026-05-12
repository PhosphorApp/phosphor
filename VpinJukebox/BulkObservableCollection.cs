using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Threading;

namespace VpinJukebox;

/// <summary>
/// An ObservableCollection that supports bulk replacement with a single Reset notification,
/// avoiding per-item UI layout passes in WPF.
/// </summary>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// Replaces all items with the given list, raising only a single
    /// <see cref="NotifyCollectionChangedAction.Reset"/> notification.
    /// The notification is dispatched at <see cref="DispatcherPriority.Background"/>
    /// so that pending cross-thread work can drain first, avoiding intermittent
    /// UI-thread contention during layout.
    /// </summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);

        // Defer the Reset notification so the dispatcher can process pending
        // cross-thread work (e.g., BeginInvoke calls from other window threads)
        // before WPF triggers a full layout pass for this collection.
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        });
    }
}
