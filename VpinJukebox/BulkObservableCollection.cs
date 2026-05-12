using System.Collections.ObjectModel;
using System.Collections.Specialized;

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
    /// </summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
