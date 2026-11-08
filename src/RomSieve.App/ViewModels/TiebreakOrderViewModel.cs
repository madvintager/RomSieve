using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.App.ViewModels;

/// <summary>A criterion of the 1G1R cascade in the reorderable list, with its rank and the preference it compares to.</summary>
public sealed partial class TiebreakCriterionItemViewModel : ViewModelBase
{
    private readonly Action<TiebreakCriterion>? _showDecidedGames;

    /// <param name="criterion">The criterion.</param>
    /// <param name="convention">Convention of the order that lists it, whose names its description quotes.</param>
    /// <param name="preference">Preference the criterion compares the releases to, set on its line, or <c>null</c>.</param>
    /// <param name="showDecidedGames">Shows the games the criterion decided in the table of the results.</param>
    public TiebreakCriterionItemViewModel(
        TiebreakCriterion criterion,
        NamingConvention convention = NamingConvention.Tosec,
        TiebreakPreferenceViewModel? preference = null,
        Action<TiebreakCriterion>? showDecidedGames = null)
    {
        Criterion = criterion;
        Convention = convention;
        Preference = preference;
        _showDecidedGames = showDecidedGames;
    }

    public TiebreakCriterion Criterion { get; }

    public NamingConvention Convention { get; }

    /// <summary>Preference the criterion compares the releases to, set on its line, or <c>null</c>.</summary>
    public TiebreakPreferenceViewModel? Preference { get; }

    public bool HasPreference => Preference is not null;

    /// <summary>Translation key of the criterion name.</summary>
    public string LabelKey => $"tiebreak.{Criterion.ToConfigString()}";

    /// <summary>
    /// Translation key of what the criterion compares: that of the convention for the dump and the original release,
    /// whose marks differ from one convention to the other, and for the emulation status of a software (its support by
    /// MAME, not the driver of a machine).
    /// </summary>
    public string DescriptionKey => Criterion is TiebreakCriterion.GoodDump or TiebreakCriterion.OriginalRelease
                                    || (Criterion == TiebreakCriterion.DriverStatus && Convention == NamingConvention.MameSoftwareList)
        ? $"tiebreak.tooltip_{Criterion.ToConfigString()}_{Convention.ToConfigString()}"
        : $"tiebreak.tooltip_{Criterion.ToConfigString()}";

    /// <summary>What the criterion compares, translated.</summary>
    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>Why the criterion has no effect on the current collection (<see cref="TiebreakApplicability"/>), or <c>null</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoEffect))]
    private LocalizedMessage? _noEffectReason;

    /// <summary>Whether the criterion is shown greyed out: it cannot separate two releases of the current collection.</summary>
    public bool HasNoEffect => NoEffectReason is not null;

    /// <summary>Why the criterion has no effect, translated, or nothing.</summary>
    [ObservableProperty]
    private string? _noEffectText;

    /// <summary>Games of the last simulation in which the criterion set the winner apart, or <c>null</c> without a current simulation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDecisions), nameof(DecidedNothing))]
    [NotifyCanExecuteChangedFor(nameof(ShowDecidedGamesCommand))]
    private int? _decidedGames;

    private bool CanShowDecidedGames => DecidedGames > 0 && _showDecidedGames is not null;

    /// <summary>Shows in the table of the results only the games the criterion decided.</summary>
    [RelayCommand(CanExecute = nameof(CanShowDecidedGames))]
    private void ShowDecidedGames() => _showDecidedGames?.Invoke(Criterion);

    /// <summary>Whether the count of the decided games is shown next to the criterion.</summary>
    public bool HasDecisions => DecidedGames.HasValue;

    /// <summary>Whether the criterion decided no game of the last simulation: its count is greyed out.</summary>
    public bool DecidedNothing => DecidedGames == 0;

    /// <summary>Tooltip of the criterion: what it compares, why it has no effect, then what it decided in the last simulation.</summary>
    [ObservableProperty]
    private string? _toolTipText;
}

/// <summary>
/// Reorderable order of the 1G1R criteria of a convention (Languages, regions and 1G1R tab), which only lists the
/// criteria that can separate two of its releases. The final tiebreak on the name is not listed: it always comes last.
/// The region mode of <see cref="PrioritiesViewModel"/> is kept consistent with the orders of TOSEC, No-Intro and Redump.
/// </summary>
public sealed partial class TiebreakOrderViewModel : ViewModelBase
{
    private readonly ILocalizationService _localization;
    private readonly Func<IReadOnlyList<TiebreakCriterion>> _getDefaultOrder;
    private readonly Func<TiebreakCriterion, TiebreakPreferenceViewModel?>? _preferenceOf;

    /// <param name="localization">Translations of the criteria.</param>
    /// <param name="convention">Convention of the order.</param>
    /// <param name="getDefaultOrder">Current default order, complete (that of the region mode).</param>
    /// <param name="preferenceOf">Preference that a criterion compares the releases to, or <c>null</c>.</param>
    public TiebreakOrderViewModel(
        ILocalizationService localization,
        NamingConvention convention,
        Func<IReadOnlyList<TiebreakCriterion>> getDefaultOrder,
        Func<TiebreakCriterion, TiebreakPreferenceViewModel?>? preferenceOf = null)
    {
        _localization = localization;
        Convention = convention;
        _getDefaultOrder = getDefaultOrder;
        _preferenceOf = preferenceOf;
        LeftOutCriteria = Enum.GetValues<TiebreakCriterion>()
            .Where(c => c is not TiebreakCriterion.None and not TiebreakCriterion.Name && !TiebreakOrder.CriteriaOf(convention).Contains(c))
            .ToList();
        SetOrder(getDefaultOrder(), notify: false);
    }

    /// <summary>Convention whose releases follow the order.</summary>
    public NamingConvention Convention { get; }

    /// <summary>Whether the panel of the orders shows this one (<see cref="PrioritiesViewModel.ShownOrderConvention"/>).</summary>
    [ObservableProperty]
    private bool _isShown;

    /// <summary>Raised when the user changes the order (not when it is set by the configuration or the region mode).</summary>
    public event EventHandler<IReadOnlyList<TiebreakCriterion>>? OrderChanged;

    /// <summary>Raised when the user asks for the games a criterion decided in the last simulation.</summary>
    public event EventHandler<TiebreakCriterion>? DecidedGamesRequested;

    public ObservableCollection<TiebreakCriterionItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    private TiebreakCriterionItemViewModel? _selectedItem;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetDefaultsCommand))]
    private bool _isEnabled = true;

    public IReadOnlyList<TiebreakCriterion> Order => Items.Select(i => i.Criterion).ToList();

    // Collection and settings that the criteria are evaluated against (ShowApplicability, ShowAmigaTarget, ShowPreferences)
    private DatCloneRelations _cloneRelations = DatCloneRelations.Unknown;
    private DatNameTraits? _nameTraits;
    private AmigaTarget? _amigaTarget;
    private VideoPreference? _preferredVideo;
    private MediaPreference? _preferredMediaType;

    /// <summary>Whether a criterion of the list has no effect on the current collection: the list then says what greyed out means.</summary>
    [ObservableProperty]
    private bool _hasCriteriaWithoutEffect;

    // Games decided by each criterion in the current simulation (ShowDecisions), or null
    private IReadOnlyDictionary<TiebreakCriterion, int>? _decisions;

    /// <summary>Whether the criteria show the games they decided in the current simulation.</summary>
    [ObservableProperty]
    private bool _hasDecisions;

    /// <summary>
    /// Shows next to each criterion the games it decided in the current simulation (<see cref="RomSieve.Core.Services.TiebreakDecisions"/>),
    /// or nothing for <c>null</c>: without a simulation of the current options, a count would describe another order.
    /// </summary>
    public void ShowDecisions(IReadOnlyDictionary<TiebreakCriterion, int>? decisions)
    {
        _decisions = decisions;
        RefreshItemStates();
    }

    /// <summary>
    /// Greys out the criteria that cannot separate two releases of a collection sorted with a DAT whose relations are
    /// <paramref name="cloneRelations"/> and whose file names tell <paramref name="nameTraits"/> (<see langword="null"/>
    /// when not known). They keep their place, so that the order is the same with another DAT.
    /// </summary>
    public void ShowApplicability(DatCloneRelations cloneRelations, DatNameTraits? nameTraits = null)
    {
        _cloneRelations = cloneRelations;
        _nameTraits = nameTraits;
        RefreshItemStates();
    }

    /// <summary>
    /// Greys out the criteria that compare the releases to a preference left empty; <see langword="null"/> for a
    /// preference not known.
    /// </summary>
    public void ShowPreferences(VideoPreference? preferredVideo, MediaPreference? preferredMediaType)
    {
        _preferredVideo = preferredVideo;
        _preferredMediaType = preferredMediaType;
        RefreshItemStates();
    }

    /// <summary>
    /// Greys out the chipset criterion unless the collection is sorted for an AGA Amiga (<paramref name="target"/>), the
    /// only machine for which the AGA and OCS versions of a game are one game.
    /// </summary>
    public void ShowAmigaTarget(AmigaTarget target)
    {
        _amigaTarget = target;
        RefreshItemStates();
    }

    private void RefreshItemStates()
    {
        foreach (var item in Items)
        {
            item.NoEffectReason = TiebreakApplicability.GetNoEffectReason(
                item.Criterion, Convention, _cloneRelations, _amigaTarget, _preferredVideo, _preferredMediaType, _nameTraits);
            item.NoEffectText = item.NoEffectReason is { } reason ? _localization.Format(reason) : null;
            item.DecidedGames = _decisions is null ? null : _decisions.GetValueOrDefault(item.Criterion);
            var decidedText = item.DecidedGames switch
            {
                null => null,
                0 => _localization.Tr("tab3.tooltip_tiebreak_decided_none"),
                1 => _localization.Tr("tab3.tooltip_tiebreak_decided_one"),
                var count => _localization.Tr("tab3.tooltip_tiebreak_decided", ("count", count))
            };
            item.ToolTipText = string.Join('\n', new[] { item.Description, item.NoEffectText, decidedText }.Where(t => !string.IsNullOrEmpty(t)));
        }

        HasCriteriaWithoutEffect = Items.Any(i => i.HasNoEffect);
        HasDecisions = _decisions is not null;
    }

    /// <summary>
    /// Criteria of the other conventions, which cannot separate two releases of this one and are not listed: named
    /// under the list, so that a criterion is never hidden without a word.
    /// </summary>
    public IReadOnlyList<TiebreakCriterion> LeftOutCriteria { get; }

    /// <summary>Sentence that names the criteria left out of the order (<see cref="LeftOutCriteria"/>).</summary>
    [ObservableProperty]
    private string _leftOutText = string.Empty;

    /// <summary>Whether the order is the current default order.</summary>
    public bool IsDefaultOrder => Order.SequenceEqual(_getDefaultOrder());

    /// <summary>Shows <paramref name="order"/>, repaired by <see cref="TiebreakOrder.Normalize"/>, and keeps the selected criterion.</summary>
    public void SetOrder(IEnumerable<TiebreakCriterion> order, bool notify = false)
    {
        var selected = SelectedItem?.Criterion;
        var normalized = TiebreakOrder.Normalize(order, _getDefaultOrder());
        if (normalized.SequenceEqual(Order))
        {
            return;
        }

        Items.Clear();
        foreach (var criterion in normalized)
        {
            Items.Add(new TiebreakCriterionItemViewModel(
                criterion, Convention, _preferenceOf?.Invoke(criterion), c => DecidedGamesRequested?.Invoke(this, c)));
        }

        RefreshTranslations();
        SelectedItem = Items.FirstOrDefault(i => i.Criterion == selected);
        OnPropertyChanged(nameof(IsDefaultOrder));
        if (notify)
        {
            OrderChanged?.Invoke(this, Order);
        }
    }

    private bool CanMoveUp => IsEnabled && SelectedItem is not null && Items.IndexOf(SelectedItem) > 0;
    private bool CanMoveDown => IsEnabled && SelectedItem is not null && Items.IndexOf(SelectedItem) < Items.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    public void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    public void MoveDown() => Move(1);

    [RelayCommand(CanExecute = nameof(IsEnabled))]
    public void ResetDefaults() => SetOrder(_getDefaultOrder(), notify: true);

    private void Move(int offset)
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        var order = Order.ToList();
        var index = order.IndexOf(item.Criterion);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= order.Count)
        {
            return;
        }

        (order[index], order[target]) = (order[target], order[index]);
        SetOrder(order, notify: true);
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    public void RefreshTranslations()
    {
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].Text = $"{i + 1}. {_localization.Tr(Items[i].LabelKey)}";
            Items[i].Description = _localization.Tr(Items[i].DescriptionKey);
        }

        LeftOutText = _localization.Tr(
            "tab3.tiebreak_left_out",
            ("convention", _localization.Tr($"tab3.tiebreak_convention_{Convention.ToConfigString()}")),
            ("criteria", string.Join(", ", LeftOutCriteria.Select(c => _localization.Tr($"tiebreak.{c.ToConfigString()}")))));

        RefreshItemStates();
    }
}
