using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Observable collection whose content can be replaced at once with a single <c>Reset</c> notification,
/// instead of one notification per removed and added item: a bound DataGrid then refreshes its rows once.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public BulkObservableCollection()
    {
    }

    public BulkObservableCollection(IEnumerable<T> items)
        : base(items)
    {
    }

    /// <summary>Replaces every item with <paramref name="items"/>, raising a single <c>Reset</c> notification.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        var newItems = items as IReadOnlyCollection<T> ?? items.ToList();
        if (Items.Count == 0 && newItems.Count == 0)
        {
            return;
        }

        Items.Clear();
        if (Items is List<T> list)
        {
            list.AddRange(newItems);
        }
        else
        {
            foreach (var item in newItems)
            {
                Items.Add(item);
            }
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
