using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Phosphor;

public class RangedObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotification;

    /// <summary>
    /// Clears the collection and adds all items, raising only a single Reset notification.
    /// </summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        _suppressNotification = true;
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);
        _suppressNotification = false;
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
    }

    /// <summary>
    /// Inserts all items at the start in order, raising only a single Reset notification
    /// (WPF collection views reject multi-item Add notifications).
    /// </summary>
    public void InsertRangeAtStart(IEnumerable<T> items)
    {
        _suppressNotification = true;
        int index = 0;
        foreach (var item in items)
            Items.Insert(index++, item);
        _suppressNotification = false;
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotification)
            base.OnCollectionChanged(e);
    }
}
