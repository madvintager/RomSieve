using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RomSieve.App.ViewModels;

/// <summary>Names that give a pattern variable its value.</summary>
public enum PatternVariableScope
{
    /// <summary>Names of every convention.</summary>
    All,

    /// <summary>TOSEC names only: publisher, year, system...</summary>
    Tosec,

    /// <summary>MAME machines only, from the category file.</summary>
    Mame
}

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
        PatternVariableScope scope,
        Action<string> onInsert)
    {
        Tag = tag;
        LabelKey = labelKey;
        _label = label;
        TooltipKey = tooltipKey;
        _tooltip = tooltip;
        Scope = scope;
        _onInsert = onInsert;
    }

    /// <summary>Variable tag (e.g. <c>"{title}"</c>, <c>"{publisher}"</c>).</summary>
    public string Tag { get; }

    /// <summary>Translation key for the label.</summary>
    public string LabelKey { get; }

    /// <summary>Translation key for the tooltip.</summary>
    public string TooltipKey { get; }

    /// <summary>Names that give the variable its value.</summary>
    public PatternVariableScope Scope { get; }

    /// <summary>Whether the variable only has a value in TOSEC names (publisher, year, system...).</summary>
    public bool IsTosecOnly => Scope == PatternVariableScope.Tosec;

    [ObservableProperty]
    private string _label;

    /// <summary>What the variable inserts, and when it has no value with the effective convention.</summary>
    [ObservableProperty]
    private string _tooltip;

    /// <summary>
    /// Whether the variable has a value with the effective convention. The entry stays in the menu, greyed out
    /// otherwise, its tooltip saying when it applies: nothing is hidden according to the convention.
    /// </summary>
    [ObservableProperty]
    private bool _isApplicable = true;

    /// <summary>Inserts the variable at the caret of the pattern.</summary>
    [RelayCommand]
    public void Insert() => _onInsert(Tag);
}
