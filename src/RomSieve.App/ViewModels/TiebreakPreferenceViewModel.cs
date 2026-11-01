using CommunityToolkit.Mvvm.ComponentModel;
using RomSieve.Core.Abstractions;

namespace RomSieve.App.ViewModels;

/// <summary>A choice of the drop-down list of a <see cref="TiebreakPreferenceViewModel"/>.</summary>
public sealed partial class TiebreakPreferenceOption : ObservableObject
{
    public TiebreakPreferenceOption(object value, string labelKey)
    {
        Value = value;
        LabelKey = labelKey;
    }

    public object Value { get; }

    public string LabelKey { get; }

    [ObservableProperty]
    private string _label = string.Empty;
}

/// <summary>
/// Preference that a 1G1R criterion compares the releases to (preferred video standard, media type, single language),
/// set on the line of the criterion in its order, so that the criterion and what it prefers are seen together. The
/// option stays in the section that saves it: the preference reads and writes it through delegates, and
/// <see cref="Refresh"/> shows a change made elsewhere (configuration, profile, quick preset).
/// </summary>
public sealed partial class TiebreakPreferenceViewModel : ViewModelBase
{
    private readonly ILocalizationService _localization;
    private readonly string _toolTipKey;
    private readonly Func<object> _get;
    private readonly Action<object> _set;
    private bool _refreshing;

    private TiebreakPreferenceViewModel(
        ILocalizationService localization,
        string toolTipKey,
        Func<object> get,
        Action<object> set,
        IReadOnlyList<TiebreakPreferenceOption> options)
    {
        _localization = localization;
        _toolTipKey = toolTipKey;
        _get = get;
        _set = set;
        Options = options;
        RefreshTranslations();
        Refresh();
    }

    /// <summary>Preference of the values of <typeparamref name="T"/>, each with the translation key of its label.</summary>
    public static TiebreakPreferenceViewModel Create<T>(
        ILocalizationService localization,
        string toolTipKey,
        Func<T> get,
        Action<T> set,
        params (T Value, string LabelKey)[] options)
        where T : notnull =>
        new(localization, toolTipKey, () => get(), value => set((T)value), [.. options.Select(o => new TiebreakPreferenceOption(o.Value, o.LabelKey))]);

    public IReadOnlyList<TiebreakPreferenceOption> Options { get; }

    [ObservableProperty]
    private TiebreakPreferenceOption? _selectedOption;

    [ObservableProperty]
    private string _toolTip = string.Empty;

    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>
    /// A drop-down list bound both ways writes nothing (null) when its view goes away: only a choice changes the option.
    /// </summary>
    partial void OnSelectedOptionChanged(TiebreakPreferenceOption? value)
    {
        if (!_refreshing && value is not null)
        {
            _set(value.Value);
        }
    }

    /// <summary>Shows the current value of the option.</summary>
    public void Refresh()
    {
        _refreshing = true;
        try
        {
            var current = _get();
            SelectedOption = Options.FirstOrDefault(o => Equals(o.Value, current));
        }
        finally
        {
            _refreshing = false;
        }
    }

    public void RefreshTranslations()
    {
        foreach (var option in Options)
        {
            option.Label = _localization.Tr(option.LabelKey);
        }

        ToolTip = _localization.Tr(_toolTipKey);
    }
}
