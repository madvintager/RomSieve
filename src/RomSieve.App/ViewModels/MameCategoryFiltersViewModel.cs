using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Category filters of the MAME machines in the Filters page: machines for adults, machines that are not arcade
/// machines, and the genres of the category file, listed once the file is read (<see cref="LoadAsync"/>, off the UI
/// thread). The options stay in <see cref="NoIntroFiltersViewModel"/>, which saves them with the other MAME options:
/// this ViewModel only presents them, the genres as what to keep (the opposite of <c>mame_excluded_genres</c>).
/// </summary>
public sealed partial class MameCategoryFiltersViewModel : ViewModelBase
{
    private readonly ILocalizationService _localization;
    private MameCategories? _categories;
    private string _loadedPath = string.Empty;
    private bool _isUnreadable;
    private int _loadVersion;

    public MameCategoryFiltersViewModel(ILocalizationService localization, NoIntroFiltersViewModel noIntro)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        NoIntro = noIntro ?? throw new ArgumentNullException(nameof(noIntro));
        NoIntro.PropertyChanged += OnNoIntroPropertyChanged;
        RebuildGenres();
    }

    public NoIntroFiltersViewModel NoIntro { get; }

    /// <summary>Genres of the category file, and the excluded genres it does not have, in alphabetical order.</summary>
    public ObservableCollection<MameGenreOptionViewModel> Genres { get; } = [];

    /// <summary>Whether there are genres to show: those of the file, or genres excluded before.</summary>
    public bool HasGenres => Genres.Count > 0;

    /// <summary>Whether a category file is read: without it, the category filters have nothing to work on.</summary>
    public bool HasCategories => _categories is { Count: > 0 };

    /// <summary>Whether the category file tells the arcade machines from the others (<c>catlist.ini</c> does).</summary>
    public bool IsArcadeFilterAvailable => _categories?.MarksArcade == true;

    /// <summary>
    /// Whether the file is read but does not mark the arcade machines (<c>catver.ini</c>): the panel then says why the
    /// arcade filter is greyed out, and which genres to uncheck instead.
    /// </summary>
    public bool IsArcadeFilterMissing => HasCategories && !IsArcadeFilterAvailable;

    public bool KeepMature
    {
        get => !NoIntro.ExcludeMameMature;
        set => NoIntro.ExcludeMameMature = !value;
    }

    public bool KeepNonArcade
    {
        get => !NoIntro.MameArcadeOnly;
        set => NoIntro.MameArcadeOnly = !value;
    }

    /// <summary>What the category file gives, or why there are no categories.</summary>
    public string StatusText =>
        _categories is { Count: > 0 } categories
            ? _localization.Tr("filters.mame_categories_loaded", ("count", categories.Count), ("genres", categories.Genres.Count))
            : _isUnreadable
                ? _localization.Tr("filters.mame_categories_unreadable")
                : _loadedPath.Length > 0
                    ? _localization.Tr("filters.mame_categories_empty")
                    : _localization.Tr("filters.mame_categories_none");

    /// <summary>
    /// Whether the categories button opens: with a category file, or with genres excluded before, which can then be
    /// kept again.
    /// </summary>
    public bool IsAvailable => HasCategories || HasGenres;

    /// <summary>Whether a category option excludes machines: the categories button is then emphasized.</summary>
    public bool IsRestricted => Restrictions().Count > 0;

    /// <summary>Text of the categories button: no file, every category, its only restriction, or their count.</summary>
    public string SummaryText =>
        HasCategories
            ? MameRefineSummary.Text(
                _localization, Restrictions(), "filters.btn_mame_categories_all", "filters.btn_mame_categories_one", "filters.btn_mame_categories_many")
            : _localization.Tr("filters.btn_mame_categories_none");

    /// <summary>Tooltip of the categories button: what the file gives, what the panel does and its restrictions.</summary>
    public string SummaryTooltip =>
        StatusText + Environment.NewLine + Environment.NewLine +
        MameRefineSummary.Tooltip(_localization, _localization.Tr("filters.tooltip_mame_categories"), Restrictions());

    /// <summary>Text of the genres title of the panel: every genre, or how many are kept.</summary>
    public string GenresText
    {
        get
        {
            var kept = Genres.Count(g => g.IsKept);
            return kept == Genres.Count
                ? _localization.Tr("filters.mame_genres_all")
                : _localization.Tr("filters.mame_genres_some", ("count", kept), ("total", Genres.Count));
        }
    }

    /// <summary>Tooltip of the genres button, which names the excluded genres.</summary>
    public string GenresTooltip
    {
        get
        {
            var tooltip = _localization.Tr("filters.tooltip_mame_genres");
            return NoIntro.MameExcludedGenres.Count == 0
                ? tooltip
                : tooltip + Environment.NewLine + Environment.NewLine +
                  _localization.Tr("filters.mame_genres_excluded", ("names", string.Join(", ", NoIntro.MameExcludedGenres)));
        }
    }

    /// <summary>
    /// Reads the category file off the UI thread and lists its genres. A file changed again before the end of the read
    /// wins: the older read is dropped.
    /// </summary>
    public async Task LoadAsync(string? path)
    {
        var version = ++_loadVersion;
        var trimmed = path?.Trim() ?? string.Empty;
        MameCategories? categories = null;
        var isUnreadable = false;
        if (trimmed.Length > 0)
        {
            try
            {
                categories = await Task.Run(() => File.Exists(trimmed) ? MameCategoryFile.Read(trimmed) : null);
                isUnreadable = categories is null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                isUnreadable = true;
            }
        }

        if (version != _loadVersion)
        {
            return;
        }

        _categories = categories;
        _loadedPath = trimmed;
        _isUnreadable = isUnreadable;
        RebuildGenres();
        OnPropertyChanged(nameof(HasCategories));
        OnPropertyChanged(nameof(IsArcadeFilterAvailable));
        OnPropertyChanged(nameof(IsArcadeFilterMissing));
        OnPropertyChanged(nameof(StatusText));
        NotifySummaryChanged();
    }

    public void RefreshTranslations()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(GenresText));
        OnPropertyChanged(nameof(GenresTooltip));
        NotifySummaryChanged();
    }

    /// <summary>Keeps the machines of every genre: nothing is excluded by genre any more.</summary>
    [RelayCommand(CanExecute = nameof(CanKeepAllGenres))]
    private void KeepAllGenres() => NoIntro.MameExcludedGenres = [];

    private bool CanKeepAllGenres() => NoIntro.MameExcludedGenres.Count > 0;

    /// <summary>
    /// Excludes every listed genre, to check again only those to keep. The excluded genres are what is saved: a genre
    /// a later category file brings is therefore kept.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExcludeAllGenres))]
    private void ExcludeAllGenres() => NoIntro.MameExcludedGenres = Genres.Select(g => g.Name).ToList();

    private bool CanExcludeAllGenres() => Genres.Any(g => g.IsKept);

    internal bool IsGenreKept(string genre) => !NoIntro.MameExcludedGenres.Contains(genre, StringComparer.OrdinalIgnoreCase);

    internal void SetGenreKept(string genre, bool isKept)
    {
        var excluded = NoIntro.MameExcludedGenres.Where(g => !string.Equals(g, genre, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!isKept)
        {
            excluded.Add(genre);
        }

        NoIntro.MameExcludedGenres = excluded.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Restrictions in force: none without a category file, which then excludes nothing; the arcade filter only when the
    /// file marks the arcade machines.
    /// </summary>
    private List<string> Restrictions()
    {
        var restrictions = new List<string>();
        if (!HasCategories)
        {
            return restrictions;
        }

        if (NoIntro.ExcludeMameMature)
        {
            restrictions.Add(_localization.Tr("filters.mame_restriction_no_mature"));
        }

        if (NoIntro.MameArcadeOnly && IsArcadeFilterAvailable)
        {
            restrictions.Add(_localization.Tr("filters.mame_restriction_arcade_only"));
        }

        if (Genres.Any(g => !g.IsKept))
        {
            restrictions.Add(GenresText);
        }

        return restrictions;
    }

    private void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsRestricted));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(SummaryTooltip));
    }

    private void RebuildGenres()
    {
        var names = (_categories?.Genres ?? [])
            .Concat(NoIntro.MameExcludedGenres)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!names.SequenceEqual(Genres.Select(g => g.Name), StringComparer.OrdinalIgnoreCase))
        {
            Genres.Clear();
            foreach (var name in names)
            {
                Genres.Add(new MameGenreOptionViewModel(this, name));
            }
        }

        OnPropertyChanged(nameof(HasGenres));
        OnPropertyChanged(nameof(GenresText));
        OnPropertyChanged(nameof(GenresTooltip));
        KeepAllGenresCommand.NotifyCanExecuteChanged();
        ExcludeAllGenresCommand.NotifyCanExecuteChanged();
        NotifySummaryChanged();
    }

    private void OnNoIntroPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NoIntroFiltersViewModel.ExcludeMameMature):
                OnPropertyChanged(nameof(KeepMature));
                NotifySummaryChanged();
                break;
            case nameof(NoIntroFiltersViewModel.MameArcadeOnly):
                OnPropertyChanged(nameof(KeepNonArcade));
                NotifySummaryChanged();
                break;
            case nameof(NoIntroFiltersViewModel.MameExcludedGenres):
                RebuildGenres();
                foreach (var genre in Genres)
                {
                    genre.NotifyKeptChanged();
                }
                NotifySummaryChanged();
                break;
        }
    }
}

/// <summary>A genre of the category file in the genres panel of the MAME filters: checked when its machines are kept.</summary>
public sealed class MameGenreOptionViewModel : ObservableObject
{
    private readonly MameCategoryFiltersViewModel _owner;

    public MameGenreOptionViewModel(MameCategoryFiltersViewModel owner, string name)
    {
        _owner = owner;
        Name = name;
    }

    public string Name { get; }

    public bool IsKept
    {
        get => _owner.IsGenreKept(Name);
        set => _owner.SetGenreKept(Name, value);
    }

    internal void NotifyKeptChanged() => OnPropertyChanged(nameof(IsKept));
}
