using CommunityToolkit.Mvvm.ComponentModel;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Column of the preview table: its translated header and whether it is shown. The source name and the status are
/// always shown; the user hides the other columns from the columns menu.
/// </summary>
public sealed partial class PreviewColumnViewModel(int index, string id, bool canHide) : ViewModelBase
{
    /// <summary>Position of the column in the table (0..9), as in <see cref="PreviewItemViewModel"/>'s indexer.</summary>
    public int Index { get; } = index;

    /// <summary>Name of the column in <c>config.json</c> (<c>preview_hidden_columns</c>).</summary>
    public string Id { get; } = id;

    public bool CanHide { get; } = canHide;

    [ObservableProperty]
    private string _header = string.Empty;

    [ObservableProperty]
    private bool _isVisible = true;

    partial void OnIsVisibleChanged(bool value)
    {
        if (!value && !CanHide)
        {
            IsVisible = true;
        }
    }
}
