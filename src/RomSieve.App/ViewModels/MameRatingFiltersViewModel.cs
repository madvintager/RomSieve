using System.Collections.ObjectModel;
using System.Globalization;
using System.ComponentModel;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Rating filters and size budget of the MAME machines in the Filters page: the lowest rating to keep and the unrated
/// machines, from the rating file chosen in the Collection step (read off the UI thread, <see cref="LoadAsync"/>), and
/// the most gigabytes the kept sets may take, the best rated first. The options stay in
/// <see cref="NoIntroFiltersViewModel"/>, which saves them with the other MAME options: this ViewModel only presents them.
/// </summary>
public sealed class MameRatingFiltersViewModel : ViewModelBase
{
    /// <summary>Choices of the lowest rating out of 100, 0 for any: the lower bounds of the ranges of bestgames.ini.</summary>
    public static readonly IReadOnlyList<int> RatingChoices = [0, 50, 60, 70, 80, 90];

    /// <summary>Budget offered when the limit is turned on: the size of a common memory card.</summary>
    public const double DefaultBudgetGb = 64;

    private readonly ILocalizationService _localization;
    private MameRatings? _ratings;
    private string _loadedPath = string.Empty;
    private bool _isUnreadable;
    private int _loadVersion;
    private double _lastBudgetGb = DefaultBudgetGb;

    public MameRatingFiltersViewModel(ILocalizationService localization, NoIntroFiltersViewModel noIntro)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        NoIntro = noIntro ?? throw new ArgumentNullException(nameof(noIntro));
        RatingOptions = new ObservableCollection<SelectOptionItem<int>>(RatingChoices.Select(n => new SelectOptionItem<int>(n, RatingLabel(n))));
        NoIntro.PropertyChanged += OnNoIntroPropertyChanged;
    }

    public NoIntroFiltersViewModel NoIntro { get; }

    public ObservableCollection<SelectOptionItem<int>> RatingOptions { get; }

    /// <summary>Whether a rating file is read: without it, the rating filters have nothing to work on.</summary>
    public bool HasRatings => _ratings is { Count: > 0 };

    /// <summary>Lowest rating; a value outside <see cref="RatingChoices"/> (edited <c>config.json</c>) is kept as it is.</summary>
    public int MinimumRating
    {
        get => NoIntro.MameMinimumRating;
        set => NoIntro.MameMinimumRating = Math.Clamp(value, 0, 100);
    }

    public bool KeepUnrated
    {
        get => !NoIntro.ExcludeMameUnrated;
        set => NoIntro.ExcludeMameUnrated = !value;
    }

    /// <summary>Whether the size of the kept sets is limited; turned on, it offers the last budget, 64 GB at first.</summary>
    public bool IsBudgetEnabled
    {
        get => NoIntro.MameSizeBudgetGb > 0;
        set
        {
            if (value != IsBudgetEnabled)
            {
                NoIntro.MameSizeBudgetGb = value ? _lastBudgetGb : 0;
            }
        }
    }

    /// <summary>Budget in gigabytes, for the numeric field (which needs a decimal); the last one while the limit is off.</summary>
    public decimal? BudgetGb
    {
        get => IsBudgetEnabled ? (decimal)NoIntro.MameSizeBudgetGb : (decimal)_lastBudgetGb;
        set
        {
            if (value is not { } gigabytes || gigabytes <= 0)
            {
                return;
            }

            _lastBudgetGb = (double)gigabytes;
            if (IsBudgetEnabled)
            {
                NoIntro.MameSizeBudgetGb = _lastBudgetGb;
            }
        }
    }

    /// <summary>Whether a rating option excludes machines: the ratings button is then emphasized.</summary>
    public bool IsRatingRestricted => RatingRestrictions().Count > 0;

    /// <summary>Text of the ratings button: no file, every rating, its only restriction, or their count.</summary>
    public string RatingsText =>
        HasRatings
            ? MameRefineSummary.Text(
                _localization, RatingRestrictions(), "filters.btn_mame_ratings_all", "filters.btn_mame_ratings_one", "filters.btn_mame_ratings_many")
            : _localization.Tr("filters.btn_mame_ratings_none");

    /// <summary>Tooltip of the ratings button: what the file gives, what the panel does and its restrictions.</summary>
    public string RatingsTooltip =>
        StatusText + Environment.NewLine + Environment.NewLine +
        MameRefineSummary.Tooltip(_localization, _localization.Tr("filters.tooltip_mame_ratings"), RatingRestrictions());

    /// <summary>Text of the size button: no limit, or the budget in gigabytes.</summary>
    public string BudgetText =>
        IsBudgetEnabled
            ? _localization.Tr("filters.btn_mame_budget_on", ("size", NoIntro.MameSizeBudgetGb.ToString("0.##", CultureInfo.InvariantCulture)))
            : _localization.Tr("filters.btn_mame_budget_off");

    /// <summary>What the rating file gives, or why there are no ratings.</summary>
    public string StatusText =>
        _ratings is { Count: > 0 } ratings
            ? _localization.Tr("filters.mame_ratings_loaded", ("count", ratings.Count))
            : _isUnreadable
                ? _localization.Tr("filters.mame_ratings_unreadable")
                : _loadedPath.Length > 0
                    ? _localization.Tr("filters.mame_ratings_empty")
                    : _localization.Tr("filters.mame_ratings_none");

    /// <summary>Reads the rating file off the UI thread. A file changed again before the end of the read wins.</summary>
    public async Task LoadAsync(string? path)
    {
        var version = ++_loadVersion;
        var trimmed = path?.Trim() ?? string.Empty;
        MameRatings? ratings = null;
        var isUnreadable = false;
        if (trimmed.Length > 0)
        {
            try
            {
                ratings = await Task.Run(() => File.Exists(trimmed) ? MameRatingFile.Read(trimmed) : null);
                isUnreadable = ratings is null;
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

        _ratings = ratings;
        _loadedPath = trimmed;
        _isUnreadable = isUnreadable;
        OnPropertyChanged(nameof(HasRatings));
        OnPropertyChanged(nameof(StatusText));
        NotifyRatingSummaryChanged();
    }

    public void RefreshTranslations()
    {
        for (var i = 0; i < RatingChoices.Count; i++)
        {
            RatingOptions[i].Label = RatingLabel(RatingChoices[i]);
        }

        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(BudgetText));
        NotifyRatingSummaryChanged();
    }

    /// <summary>Restrictions in force: none without a rating file, which then excludes nothing.</summary>
    private List<string> RatingRestrictions()
    {
        var restrictions = new List<string>();
        if (!HasRatings)
        {
            return restrictions;
        }

        if (NoIntro.MameMinimumRating > 0)
        {
            restrictions.Add(RatingLabel(NoIntro.MameMinimumRating));
        }

        if (NoIntro.ExcludeMameUnrated)
        {
            restrictions.Add(_localization.Tr("filters.mame_restriction_no_unrated"));
        }

        return restrictions;
    }

    private void NotifyRatingSummaryChanged()
    {
        OnPropertyChanged(nameof(IsRatingRestricted));
        OnPropertyChanged(nameof(RatingsText));
        OnPropertyChanged(nameof(RatingsTooltip));
    }

    private string RatingLabel(int rating) =>
        rating == 0
            ? _localization.Tr("filters.mame_rating_any")
            : _localization.Tr("filters.mame_rating_at_least", ("rating", rating));

    private void OnNoIntroPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NoIntroFiltersViewModel.MameMinimumRating):
                OnPropertyChanged(nameof(MinimumRating));
                NotifyRatingSummaryChanged();
                break;
            case nameof(NoIntroFiltersViewModel.ExcludeMameUnrated):
                OnPropertyChanged(nameof(KeepUnrated));
                NotifyRatingSummaryChanged();
                break;
            case nameof(NoIntroFiltersViewModel.MameSizeBudgetGb):
                if (NoIntro.MameSizeBudgetGb > 0)
                {
                    _lastBudgetGb = NoIntro.MameSizeBudgetGb;
                }
                OnPropertyChanged(nameof(IsBudgetEnabled));
                OnPropertyChanged(nameof(BudgetGb));
                OnPropertyChanged(nameof(BudgetText));
                break;
        }
    }
}
