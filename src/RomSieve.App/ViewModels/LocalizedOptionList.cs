using System.Collections.ObjectModel;
using RomSieve.Core.Abstractions;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Options of a drop-down list whose labels are translation keys, declared once and translated again
/// on a language switch (<see cref="RefreshTranslations"/>).
/// </summary>
public sealed class LocalizedOptionList<T> : ObservableCollection<SelectOptionItem<T>>
{
    private readonly ILocalizationService _localization;
    private readonly (T Value, string LabelKey)[] _options;

    public LocalizedOptionList(ILocalizationService localization, params (T Value, string LabelKey)[] options)
    {
        _localization = localization;
        _options = options;
        foreach (var (value, labelKey) in options)
        {
            Add(new SelectOptionItem<T>(value, localization.Tr(labelKey)));
        }
    }

    public void RefreshTranslations()
    {
        for (var i = 0; i < _options.Length; i++)
        {
            this[i].Label = _localization.Tr(_options[i].LabelKey);
        }
    }
}
