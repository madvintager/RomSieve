using System.ComponentModel;
using RomSieve.Core.Abstractions;

namespace RomSieve.App.ViewModels;

/// <summary>Advanced option of a section: the property that holds it, its label and whether it differs from its default.</summary>
public sealed record AdvancedOption(string PropertyName, string LabelKey, Func<bool> IsChanged);

/// <summary>
/// Advanced options of a settings step, shown in a drop-down panel that does not change the layout of its page.
/// The button that opens it counts the options that differ from their default value, so that an option still acting
/// is not forgotten while the panel is closed.
/// </summary>
public sealed partial class AdvancedOptionsViewModel : ViewModelBase
{
    private readonly ILocalizationService _localization;
    private readonly IReadOnlyList<AdvancedOption> _options;

    public AdvancedOptionsViewModel(ILocalizationService localization, INotifyPropertyChanged section, params AdvancedOption[] options)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        ArgumentNullException.ThrowIfNull(section);
        _options = options;
        var watched = options.Select(o => o.PropertyName).ToHashSet(StringComparer.Ordinal);
        section.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null && watched.Contains(e.PropertyName))
            {
                RefreshHeader();
            }
        };
    }

    /// <summary>Number of advanced options that differ from their default value.</summary>
    public int ChangedCount => _options.Count(o => o.IsChanged());

    public bool HasChangedOptions => ChangedCount > 0;

    public string HeaderText => ChangedCount switch
    {
        0 => _localization.Tr("advanced.title"),
        1 => _localization.Tr("advanced.title_changed_one"),
        var count => _localization.Tr("advanced.title_changed_many", ("count", count))
    };

    /// <summary>Lists the changed options, which a closed panel hides.</summary>
    public string HeaderTooltip
    {
        get
        {
            var changed = _options.Where(o => o.IsChanged()).Select(o => OptionLabel(o.LabelKey)).ToList();
            return changed.Count == 0
                ? _localization.Tr("advanced.tooltip")
                : _localization.Tr("advanced.tooltip_changed", ("options", string.Join(", ", changed)));
        }
    }

    public void RefreshTranslations() => RefreshHeader();

    private void RefreshHeader()
    {
        OnPropertyChanged(nameof(ChangedCount));
        OnPropertyChanged(nameof(HasChangedOptions));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(HeaderTooltip));
    }

    /// <summary>The label of a list ends with a colon, which a list of names does not need.</summary>
    private string OptionLabel(string key) => _localization.Tr(key).TrimEnd().TrimEnd(':').TrimEnd();
}
