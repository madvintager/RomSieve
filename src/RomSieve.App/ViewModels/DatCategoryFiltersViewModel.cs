using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Categories of the No-Intro and Redump DATs in the Filters page (<c>Games</c>, <c>Add-Ons</c>, <c>Applications</c>...):
/// those of the chosen DAT with their number of games once it is read (<see cref="ShowDat"/>), otherwise those the DATs
/// use (<see cref="DatCategory.Known"/>), and the excluded categories none of them has. The option stays in
/// <see cref="NoIntroFiltersViewModel"/>: this ViewModel only presents it, as what to keep (the opposite of
/// <c>nointro_excluded_dat_categories</c>).
/// </summary>
public sealed partial class DatCategoryFiltersViewModel : ViewModelBase
{
    private readonly ILocalizationService _localization;
    private IReadOnlyDictionary<string, int>? _datCounts;

    public DatCategoryFiltersViewModel(ILocalizationService localization, NoIntroFiltersViewModel noIntro)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        NoIntro = noIntro ?? throw new ArgumentNullException(nameof(noIntro));
        NoIntro.PropertyChanged += OnNoIntroPropertyChanged;
        RebuildCategories();
    }

    public NoIntroFiltersViewModel NoIntro { get; }

    /// <summary>
    /// Categories offered: those of the DAT read (in the order of <see cref="DatCategory.Known"/>, then alphabetical),
    /// or every known category, followed by the excluded ones not listed.
    /// </summary>
    public ObservableCollection<DatCategoryOptionViewModel> Categories { get; } = [];

    /// <summary>What the list shows: the categories of the DAT read, a DAT without any, or the known categories.</summary>
    public string StatusText => _localization.Tr(
        _datCounts is null ? "filters.dat_categories_status_known"
        : _datCounts.Count == 0 ? "filters.dat_categories_status_none"
        : "filters.dat_categories_status_dat");

    /// <summary>Tooltip of the title of the section: what the categories do, and how a game is filed by them.</summary>
    public string Tooltip =>
        _localization.Tr("filters.tooltip_dat_categories") + Environment.NewLine + Environment.NewLine +
        _localization.Tr("filters.dat_categories_hint");

    /// <summary>
    /// Shows the number of games of each category of the DAT read (<see cref="DirectoriesViewModel.DatCategoryCounts"/>):
    /// <c>null</c> when not known (no DAT, a lot), the known categories being then offered.
    /// </summary>
    public void ShowDat(IReadOnlyDictionary<string, int>? counts)
    {
        _datCounts = counts;
        RebuildCategories();
        OnPropertyChanged(nameof(StatusText));
    }

    public void RefreshTranslations()
    {
        foreach (var category in Categories)
        {
            category.NotifyLabelChanged();
        }

        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Tooltip));
    }

    /// <summary>Keeps the games of every category: nothing is excluded by category any more.</summary>
    [RelayCommand(CanExecute = nameof(CanKeepAll))]
    private void KeepAll() => NoIntro.ExcludedDatCategories = [];

    private bool CanKeepAll() => NoIntro.ExcludedDatCategories.Count > 0;

    /// <summary>Excludes every listed category, to check again only those to keep.</summary>
    [RelayCommand(CanExecute = nameof(CanExcludeAll))]
    private void ExcludeAll() =>
        NoIntro.ExcludedDatCategories = Categories.Select(c => c.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();

    private bool CanExcludeAll() => Categories.Any(c => c.IsKept);

    internal bool IsKept(string category) => !NoIntro.ExcludedDatCategories.Contains(category, StringComparer.OrdinalIgnoreCase);

    internal void SetKept(string category, bool isKept)
    {
        var excluded = NoIntro.ExcludedDatCategories.Where(c => !string.Equals(c, category, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!isKept)
        {
            excluded.Add(category);
        }

        NoIntro.ExcludedDatCategories = excluded.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal string LabelOf(string category) =>
        _datCounts is not null && _datCounts.TryGetValue(category, out var count)
            ? _localization.Tr("filters.dat_category_with_count", ("name", category), ("count", count))
            : category;

    private void RebuildCategories()
    {
        var offered = _datCounts is { Count: > 0 } counts
            ? counts.Keys
                .OrderBy(KnownRank)
                .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : DatCategory.Known.ToList();
        var names = offered
            .Concat(NoIntro.ExcludedDatCategories.Order(StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The labels carry the number of games of the DAT: rebuilt with it
        Categories.Clear();
        foreach (var name in names)
        {
            Categories.Add(new DatCategoryOptionViewModel(this, name));
        }

        KeepAllCommand.NotifyCanExecuteChanged();
        ExcludeAllCommand.NotifyCanExecuteChanged();
    }

    // Rank of a category in the known order; those the known list does not have come after
    private static int KnownRank(string category)
    {
        for (var i = 0; i < DatCategory.Known.Count; i++)
        {
            if (string.Equals(DatCategory.Known[i], category, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return DatCategory.Known.Count;
    }

    private void OnNoIntroPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NoIntroFiltersViewModel.ExcludedDatCategories))
        {
            return;
        }

        // An excluded category no list has must stay in sight, to be kept again
        if (NoIntro.ExcludedDatCategories.Any(c => !Categories.Any(o => string.Equals(o.Name, c, StringComparison.OrdinalIgnoreCase))))
        {
            RebuildCategories();
            return;
        }

        foreach (var category in Categories)
        {
            category.NotifyKeptChanged();
        }

        KeepAllCommand.NotifyCanExecuteChanged();
        ExcludeAllCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>A category of the DATs in the DAT categories section: checked when its games are kept.</summary>
public sealed class DatCategoryOptionViewModel : ObservableObject
{
    private readonly DatCategoryFiltersViewModel _owner;

    public DatCategoryOptionViewModel(DatCategoryFiltersViewModel owner, string name)
    {
        _owner = owner;
        Name = name;
    }

    public string Name { get; }

    /// <summary>Name of the category, with its number of games when a DAT is read.</summary>
    public string Label => _owner.LabelOf(Name);

    public bool IsKept
    {
        get => _owner.IsKept(Name);
        set => _owner.SetKept(Name, value);
    }

    internal void NotifyKeptChanged() => OnPropertyChanged(nameof(IsKept));

    internal void NotifyLabelChanged() => OnPropertyChanged(nameof(Label));
}
