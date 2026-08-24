using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AIMediaWorker.Subtitle;

public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var added = false;
        CheckReentrancy();
        foreach (var item in items)
        {
            Items.Add(item);
            added = true;
        }

        if (!added) return;
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
