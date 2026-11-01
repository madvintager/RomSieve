using CommunityToolkit.Mvvm.ComponentModel;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Base class for all ViewModels in the presentation layer,
/// inheriting from <see cref="ObservableObject"/> (CommunityToolkit.Mvvm).
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
}

/// <summary>
/// Localized option for dropdown lists (ComboBox).
/// </summary>
public sealed partial class SelectOptionItem<T> : ObservableObject
{
    public SelectOptionItem(T value, string label)
    {
        Value = value;
        _label = label;
    }

    public T Value { get; }

    [ObservableProperty]
    private string _label;

    public override string ToString() => Label;
}
