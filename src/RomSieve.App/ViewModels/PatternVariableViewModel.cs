using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Entry of the "Insert a variable" menu of the directory pattern (<c>{title}</c>, <c>{publisher}</c>, etc.).
/// </summary>
public sealed partial class PatternVariableViewModel : ViewModelBase
{
    private readonly Action<string> _onInsert;

    public PatternVariableViewModel(
        string tag,
        string labelKey,
        string label,
        string tooltipKey,
        string tooltip,
        bool isTosecOnly,
        Action<string> onInsert)
    {
        Tag = tag;
        LabelKey = labelKey;
        _label = label;
        TooltipKey = tooltipKey;
        _tooltip = tooltip;
        IsTosecOnly = isTosecOnly;
        _onInsert = onInsert;
    }

    /// <summary>Variable tag (e.g. <c>"{title}"</c>, <c>"{publisher}"</c>).</summary>
    public string Tag { get; }

    /// <summary>Translation key for the label.</summary>
    public string LabelKey { get; }

    /// <summary>Translation key for the tooltip.</summary>
    public string TooltipKey { get; }

    /// <summary>Whether the variable only has a value in TOSEC names (publisher, year, system...).</summary>
    public bool IsTosecOnly { get; }

    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private string _tooltip;

    /// <summary>Inserts the variable at the caret of the pattern.</summary>
    [RelayCommand]
    public void Insert() => _onInsert(Tag);
}
