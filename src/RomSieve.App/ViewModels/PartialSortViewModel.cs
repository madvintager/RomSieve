using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Partial sort from the table, as a part of the last simulation, which must still match the options:
/// <list type="bullet">
/// <item>the checked games of the rows shown (search and status chips applied, wherever the table is scrolled), where
/// only the rows the sort writes (kept or non-conforming) choose their game, with the MAME sets they need; without a
/// filter, every checked game;</item>
/// <item>the games checked in the table: after a simulation, every game the sort would write is checked, so that the
/// boxes always say what the sort button sorts; unchecking a file leaves out its game, unchecking a header all the
/// files of its game.</item>
/// </list>
/// A game is sorted as a whole (all its disks, tracks and playlist). While every game is checked, the sort is the
/// ordinary full sort (which may purge); as soon as one is unchecked, it is a partial sort. The games unchecked by the
/// user are kept by their selection key (<see cref="PreviewItemViewModel.SelectionUnitKey"/>), so they survive a
/// differential simulation, whose new games arrive checked, and they are never saved: a selection is not an option.
/// </summary>
public sealed partial class PartialSortViewModel : ViewModelBase
{
    private readonly ILocalizationService _localization;
    private readonly ExecutionViewModel _execution;
    private readonly PreviewTableViewModel _table;

    /// <summary>Selection units unchecked by the user: every other unit the sort writes is checked.</summary>
    private readonly HashSet<string> _uncheckedUnits = new(StringComparer.Ordinal);

    /// <summary>Selection units checked in the table: those the sort writes, minus <see cref="_uncheckedUnits"/>.</summary>
    private readonly HashSet<string> _checkedUnits = new(StringComparer.Ordinal);

    /// <summary>Files of each selection unit of the table, rebuilt by <see cref="Refresh"/>.</summary>
    private Dictionary<string, List<PreviewItemViewModel>> _rowsByUnit = new(StringComparer.Ordinal);

    /// <summary>Units of which the sort writes at least one file: only they can be checked.</summary>
    private HashSet<string> _writtenUnits = new(StringComparer.Ordinal);

    /// <summary>
    /// Units not checked but sorted all the same, as MAME sets a checked game needs (its BIOS, its devices), with the short
    /// name of the set that needs each: their box is checked and locked.
    /// </summary>
    private Dictionary<string, IReadOnlyList<string>> _requiredUnits = new(StringComparer.Ordinal);

    /// <summary>Whether the rows show their place in a partial sort, as last written by <see cref="UpdateRowCheck"/>.</summary>
    private bool _rowsShowSelection;

    /// <summary>Whether the rows of the table or their statuses changed since <see cref="IndexRows"/>.</summary>
    private bool _rowsChanged = true;

    /// <summary>Rows selected in the grid (Ctrl / Shift click), which the menu and the space bar check at once.</summary>
    private IReadOnlyList<PreviewRowViewModel> _gridSelection = [];

    private PartialSortRequest? _displayedRequest;
    private PartialSortRequest? _checkedRequest;

    public PartialSortViewModel(ILocalizationService localization, ExecutionViewModel execution, PreviewTableViewModel table)
    {
        _localization = localization;
        _execution = execution;
        _table = table;
        _displayedSummaryText = string.Empty;
        _checkedSummaryText = string.Empty;
        _checkedCountText = string.Empty;
    }

    /// <summary>Whether the checked games of the rows shown can be sorted on their own: see <see cref="Refresh"/>.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SortDisplayedCommand))]
    private bool _canSortDisplayed;

    /// <summary>Number of checked games and non-conforming files of the rows shown, with the MAME sets they need.</summary>
    [ObservableProperty]
    private int _displayedUnitCount;

    /// <summary>What a sort of the checked games of the rows shown writes, or why it is not available.</summary>
    [ObservableProperty]
    private string _displayedSummaryText;

    /// <summary>Whether the checked games can be sorted: a current simulation, and a file to write.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SortCheckedCommand))]
    private bool _canSortChecked;

    /// <summary>Number of checked games and non-conforming files.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCheckedUnits))]
    private int _checkedUnitCount;

    /// <summary>
    /// Counter shown above the table: the number of checked games, of the MAME sets sorted with them because they need
    /// them, and of those the filters hide.
    /// </summary>
    [ObservableProperty]
    private string _checkedCountText;

    /// <summary>Tooltip of the counter, which names the MAME sets sorted with the checked games.</summary>
    [ObservableProperty]
    private string _checkedCountTooltip = string.Empty;

    /// <summary>What a sort of the checked games writes, or why it is not available.</summary>
    [ObservableProperty]
    private string _checkedSummaryText;

    public bool HasCheckedUnits => CheckedUnitCount > 0;

    /// <summary>
    /// Whether some games the sort writes are unchecked: the sort button then sorts the checked ones only (a partial
    /// sort, maybe of nothing), the banner and the sentence of the footer describe them.
    /// </summary>
    public bool IsPartialSelection => _writtenUnits.Count > 0 && _checkedUnits.Count < _writtenUnits.Count;

    /// <summary>Whether the table shows a simulation whose games can be checked (the bar above the table).</summary>
    public bool IsSelectionAvailable => _writtenUnits.Count > 0;

    /// <summary>
    /// Recomputes the rows that can be checked and the two selections: called when the rows shown, the results or the
    /// state of the simulation change. Both need a simulation of the current options and a checked game: the second one
    /// among the rows shown.
    /// </summary>
    public void Refresh()
    {
        _displayedRequest = null;
        _checkedRequest = null;
        // Nothing is sorted while a run goes on, or while a folder of DATs is read
        if (!_execution.CanStartOperation)
        {
            CanSortDisplayed = false;
            CanSortChecked = false;
            NotifyFooterChanged();
            return;
        }

        if (_rowsChanged)
        {
            IndexRows();
        }
        else
        {
            RefreshHeaders();
        }

        RefreshDisplayed();
        RefreshChecked();
    }

    /// <summary>The rows of the table, or their statuses, changed: the next <see cref="Refresh"/> indexes them again.</summary>
    public void MarkRowsChanged() => _rowsChanged = true;

    private void IndexRows()
    {
        _rowsChanged = false;
        var rowsByUnit = new Dictionary<string, List<PreviewItemViewModel>>(StringComparer.Ordinal);
        var writtenUnits = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in _table.AllPreviewItems)
        {
            var unitKey = row.SelectionUnitKey;
            if (!rowsByUnit.TryGetValue(unitKey, out var rows))
            {
                rows = [];
                rowsByUnit[unitKey] = rows;
            }

            rows.Add(row);
            if (SelectionFilter.IsWritten(row.Item))
            {
                writtenUnits.Add(unitKey);
            }
        }

        // The rows of a real sort are its results: they cannot be chosen for another sort
        if (!_execution.IsDryRunOperation)
        {
            writtenUnits.Clear();
        }

        _rowsByUnit = rowsByUnit;
        _writtenUnits = writtenUnits;
        UpdateCheckedUnits();
        _rowsShowSelection = IsPartialSelection;
        foreach (var row in _table.AllPreviewItems)
        {
            UpdateRowCheck(row);
        }

        RefreshHeaders();
    }

    /// <summary>
    /// Check box of a file: checked when its game is checked, or when checked games need its set (then locked, with a
    /// tooltip that names them). While some games are unchecked, the row also says whether it is sorted, and why.
    /// </summary>
    private void UpdateRowCheck(PreviewItemViewModel row)
    {
        var requiredBy = RequiredBy(row);
        row.CanBeSortChecked = IsCheckable(row) && requiredBy is null;
        row.IsSortChecked = IsCheckedRow(row);
        row.SortCheckTooltip = RequiredTooltip(requiredBy);
        row.SetSelection(!IsPartialSelection || !IsCheckable(row)
            ? null
            : requiredBy is not null
                ? requiredBy.Count == 1
                    ? LocalizedMessage.Create("preview.selection_required", ("set", requiredBy[0]))
                    : LocalizedMessage.Create("preview.selection_required_many", ("names", MameSetDependencies.FormatNames(requiredBy)))
                : LocalizedMessage.Create(row.IsSortChecked ? "preview.selection_checked" : "preview.selection_unchecked"));
    }

    /// <summary>Tooltip of a check box: how to choose the game, or the checked games that need its set.</summary>
    private string RequiredTooltip(IReadOnlyList<string>? requiredBy) => requiredBy switch
    {
        null => _localization.Tr("panel4.tooltip_row_check"),
        [var set] => _localization.Tr("panel4.tooltip_row_required", ("set", set)),
        _ => _localization.Format(LocalizedMessage.Create(
            "panel4.tooltip_row_required_many",
            ("names", MameSetDependencies.FormatNames(requiredBy))))
    };

    /// <summary>
    /// Sets that need the set of a file, when the file is not checked but sorted all the same (a BIOS, a device of
    /// checked games); otherwise <see langword="null"/>.
    /// </summary>
    private IReadOnlyList<string>? RequiredBy(PreviewItemViewModel row) =>
        IsCheckable(row) && !_checkedUnits.Contains(row.SelectionUnitKey)
            ? _requiredUnits.GetValueOrDefault(row.SelectionUnitKey)
            : null;

    /// <summary>Whether the game of a file is sorted: checked, or needed by a checked game.</summary>
    private bool IsSortedUnit(string unitKey) => _checkedUnits.Contains(unitKey) || _requiredUnits.ContainsKey(unitKey);

    /// <summary>
    /// Whether the box of a file can be checked: the sort writes it (kept, or non-conforming and isolated). Another version of its
    /// game, excluded, is not sorted with it: its box stays empty, and it does not choose its game.
    /// </summary>
    private bool IsCheckable(PreviewItemViewModel row) =>
        SelectionFilter.IsWritten(row.Item) && _writtenUnits.Contains(row.SelectionUnitKey);

    /// <summary>Whether the box of a file is checked: it is written, and its game is checked or needed by a checked game.</summary>
    private bool IsCheckedRow(PreviewItemViewModel row) =>
        IsCheckable(row) && IsSortedUnit(row.SelectionUnitKey);

    /// <summary>Every unit the sort writes is checked, except those the user unchecked.</summary>
    private void UpdateCheckedUnits()
    {
        _checkedUnits.Clear();
        _checkedUnits.UnionWith(_writtenUnits);
        _checkedUnits.ExceptWith(_uncheckedUnits);
    }

    /// <summary>
    /// Check box of the headers of games: checked when all its files that can be are (or are needed by a checked game),
    /// locked when they all are needed by a checked game.
    /// </summary>
    public void RefreshHeaders()
    {
        foreach (var header in _table.GroupHeaders)
        {
            var checkable = header.Files.Where(IsCheckable).ToList();
            var requiredBy = checkable.Select(RequiredBy).ToList();
            header.CanBeSortChecked = requiredBy.Any(r => r is null);
            header.IsSortChecked = checkable.Count > 0 && checkable.All(f => IsSortedUnit(f.SelectionUnitKey));
            header.SortCheckTooltip = header.CanBeSortChecked || requiredBy.Count == 0
                ? _localization.Tr("panel4.tooltip_row_check")
                : RequiredTooltip(requiredBy[0]);
        }
    }

    private void RefreshDisplayed()
    {
        if (!_execution.IsSimulationCurrent || _table.AllPreviewItems.Count == 0)
        {
            SetDisplayedUnavailable("panel4.partial_needs_simulation");
            return;
        }

        // The checked games among the rows shown (search and status chips applied), wherever the table is scrolled; the
        // MAME sets they need come with them, but a required set shown alone is not a game chosen by itself
        var chosen = _table.FilteredPreviewItems
            .Where(r => SelectionFilter.IsWritten(r.Item) && _checkedUnits.Contains(r.SelectionUnitKey))
            .Select(r => r.Item)
            .ToList();
        if (chosen.Count == 0 || BuildRequest(chosen) is not { } request)
        {
            SetDisplayedUnavailable("panel4.partial_nothing_shown");
            return;
        }

        _displayedRequest = request;
        DisplayedUnitCount = request.Selection.Count;
        DisplayedSummaryText = request.SummaryText;
        CanSortDisplayed = true;
    }

    private void RefreshChecked()
    {
        // The MAME sets the checked games need are sorted with them: their rows are checked and locked
        var checkedSelection = IsPartialSelection
            ? CreateSelection(_checkedUnits.SelectMany(u => _rowsByUnit.GetValueOrDefault(u) ?? []).Select(r => r.Item).ToList())
            : default;
        var previousRequired = _requiredUnits;
        _requiredUnits = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var unit in checkedSelection.Selection?.Units ?? [])
        {
            if (unit.RequiredBy is { } requiredBy)
            {
                _requiredUnits[unit.Key] = requiredBy;
            }
        }
        if (_rowsShowSelection != IsPartialSelection)
        {
            // The first game unchecked, or the last one checked again: every row says whether it is sorted, or no more
            _rowsShowSelection = IsPartialSelection;
            foreach (var row in _table.AllPreviewItems)
            {
                UpdateRowCheck(row);
            }
        }
        else
        {
            foreach (var unitKey in previousRequired.Keys.Concat(_requiredUnits.Keys).Distinct(StringComparer.Ordinal))
            {
                foreach (var row in _rowsByUnit.GetValueOrDefault(unitKey) ?? [])
                {
                    UpdateRowCheck(row);
                }
            }
        }
        if (previousRequired.Count > 0 || _requiredUnits.Count > 0)
        {
            RefreshHeaders();
        }

        CheckedUnitCount = _checkedUnits.Count;
        var gameCount = _checkedUnits.Count(SelectionFilter.IsGameUnitKey);
        CheckedCountText = (gameCount, _checkedUnits.Count) switch
        {
            (_, 0) => _localization.Tr("panel4.checked_none"),
            (1, 1) => _localization.Tr("panel4.checked_one_game"),
            (0, 1) => _localization.Tr("panel4.checked_one_file"),
            var (games, count) when games == count => _localization.Tr("panel4.checked_games", ("count", count)),
            var (_, count) => _localization.Tr("panel4.checked_units", ("count", count))
        };

        // Games hidden by the search or the status filters stay checked: the counter says how many, since "Check All"
        // only acts on the rows shown
        // (a game whose only rows shown are excluded versions has no checked box in sight: it is hidden)
        var shownUnits = _table.FilteredPreviewItems
            .Where(IsCheckable)
            .Select(r => r.SelectionUnitKey)
            .ToHashSet(StringComparer.Ordinal);
        var requiredNames = checkedSelection.Selection is { } current
            ? RequiredSetNames(current, checkedSelection.Written, checkedSelection.UnitKeyOf)
            : [];
        if (requiredNames.Count > 0)
        {
            CheckedCountText = requiredNames.Count == 1
                ? _localization.Tr("panel4.checked_plus_one_required", ("selected", CheckedCountText))
                : _localization.Tr("panel4.checked_plus_required", ("selected", CheckedCountText), ("count", requiredNames.Count));
        }
        CheckedCountTooltip = requiredNames.Count == 0
            ? _localization.Tr("panel4.tooltip_checked_count")
            : _localization.Tr("panel4.tooltip_checked_count") + Environment.NewLine +
              _localization.Tr("panel4.tooltip_checked_required", ("names", string.Join(", ", requiredNames)));

        var hiddenCount = _checkedUnits.Count(u => !shownUnits.Contains(u));
        if (hiddenCount > 0)
        {
            CheckedCountText = hiddenCount == 1
                ? _localization.Tr("panel4.checked_with_one_hidden", ("selected", CheckedCountText))
                : _localization.Tr("panel4.checked_with_hidden", ("selected", CheckedCountText), ("hidden", hiddenCount));
        }

        RefreshShownRowsState();
        RefreshCheckedRequest();
        NotifyFooterChanged();

        OnPropertyChanged(nameof(IsPartialSelection));
        OnPropertyChanged(nameof(IsSelectionAvailable));
        CheckedWrittenItems = checkedSelection.Selection is not null ? checkedSelection.Written : null;
        CheckedSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Files of the table that a sort of the checked games writes (kept or non-conforming), maybe none, while some
    /// games are unchecked; <see langword="null"/> while every game is checked. The statistics banner shows their
    /// volume and the FAT32 warning for them.
    /// </summary>
    public IReadOnlyCollection<ProcessItem>? CheckedWrittenItems { get; private set; }

    /// <summary>Raised when the checked games, or what their sort writes, may have changed.</summary>
    public event EventHandler? CheckedSelectionChanged;

    private void RefreshCheckedRequest()
    {
        if (_checkedUnits.Count == 0)
        {
            SetCheckedUnavailable("panel4.partial_nothing_checked");
            return;
        }

        if (!_execution.IsSimulationCurrent)
        {
            SetCheckedUnavailable("panel4.partial_needs_simulation");
            return;
        }

        var chosen = _checkedUnits.SelectMany(u => _rowsByUnit[u]).Select(r => r.Item).ToList();
        _checkedRequest = BuildRequest(chosen);
        if (_checkedRequest is null)
        {
            SetCheckedUnavailable("panel4.partial_needs_simulation");
            return;
        }

        CheckedSummaryText = _checkedRequest.SummaryText;
        CanSortChecked = true;
    }

    /// <summary>
    /// Partial sort of the games of <paramref name="chosen"/>, with its summary; <c>null</c> when it would write nothing,
    /// or, with <paramref name="requireStrictPart"/>, everything the full sort would write.
    /// </summary>
    private PartialSortRequest? BuildRequest(IReadOnlyList<ProcessItem> chosen)
    {
        var (selection, simulated, written, unitKeyOf) = CreateSelection(chosen);
        if (written.Count == 0)
        {
            return null;
        }

        // The games chosen, and apart the MAME sets they need; the sentence says it is a part of the simulation only when
        // it leaves something out
        var gameCount = selection.Units.Count(u => SelectionFilter.IsGameUnitKey(u.Key) && !u.IsRequiredSet);
        var isPart = written.Count < simulated.Count(SelectionFilter.IsWritten);
        return _execution.BuildPartialSortSummary(written, gameCount, RequiredSetNames(selection, written, unitKeyOf), isPart) is { } summary
            ? new PartialSortRequest(selection, summary)
            : null;
    }

    /// <summary>
    /// Selection of the games of <paramref name="chosen"/> (with the MAME sets they need), the files of the simulation,
    /// the files a sort of the selection writes, and the selection unit of a file.
    /// </summary>
    private (SortSelection Selection, List<ProcessItem> Simulated, IReadOnlyList<ProcessItem> Written, Func<ProcessItem, string> UnitKeyOf) CreateSelection(
        IReadOnlyList<ProcessItem> chosen)
    {
        var unitKeys = new Dictionary<ProcessItem, string>(_table.AllPreviewItems.Count, ReferenceEqualityComparer.Instance);
        foreach (var row in _table.AllPreviewItems)
        {
            unitKeys[row.Item] = row.SelectionUnitKey;
        }

        string UnitKeyOf(ProcessItem item) =>
            unitKeys.TryGetValue(item, out var key) ? key : SelectionFilter.GetFileUnitKey(item);

        var simulated = _table.AllPreviewItems.Select(r => r.Item).ToList();
        var selection = SelectionFilter.Create(simulated, chosen, UnitKeyOf);
        return (selection, simulated, SelectionFilter.GetWrittenItems(simulated, selection, UnitKeyOf), UnitKeyOf);
    }

    /// <summary>Short names of the MAME sets a selection adds to the games chosen, because they need them.</summary>
    private static IReadOnlyList<string> RequiredSetNames(SortSelection selection, IEnumerable<ProcessItem> written, Func<ProcessItem, string> unitKeyOf)
    {
        var requiredKeys = selection.Units.Where(u => u.IsRequiredSet).Select(u => u.Key).ToHashSet(StringComparer.Ordinal);
        return requiredKeys.Count == 0
            ? []
            : written
                .Where(it => requiredKeys.Contains(unitKeyOf(it)))
                .Select(it => it.MameSet?.GameName ?? it.DisplayName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    private void SetDisplayedUnavailable(string reasonKey)
    {
        CanSortDisplayed = false;
        DisplayedUnitCount = 0;
        DisplayedSummaryText = _localization.Tr(reasonKey);
    }

    private void SetCheckedUnavailable(string reasonKey)
    {
        CanSortChecked = false;
        CheckedSummaryText = _localization.Tr(reasonKey);
    }

    // =========================================================================
    // Sort button of the footer: the checked games when there are some, otherwise the whole simulation
    // =========================================================================

    /// <summary>Label of the sort button: "Sort Selection" while some games are unchecked, "Start Sorting" otherwise.</summary>
    public string StartButtonText => _localization.Tr(IsPartialSelection ? "panel4.btn_sort_checked" : "panel4.btn_start");

    public string StartButtonTooltip => _localization.Tr(
        _execution.IsReadingDatFolder ? "panel4.tooltip_wait_dat_folder"
        : IsPartialSelection ? "panel4.tooltip_start_checked"
        : "panel4.tooltip_start");

    /// <summary>Whether the sort button is put forward: after a simulation of the current options that it can sort.</summary>
    public bool IsStartPutForward => _execution.IsSimulationCurrent && (!IsPartialSelection || CanSortChecked);

    /// <summary>
    /// Sentence next to the sort button: what it writes. While some games are unchecked, that of the checked ones (or
    /// why they cannot be sorted); otherwise that of the whole simulation.
    /// </summary>
    public string FooterSummaryText => IsPartialSelection ? CheckedSummaryText : _execution.SortSummaryText;

    public bool IsFooterSummaryVisible => IsPartialSelection
        ? !_execution.IsRunning && !string.IsNullOrEmpty(CheckedSummaryText)
        : _execution.IsSortSummaryVisible;

    public string FooterSummaryTooltip => IsPartialSelection
        ? $"{CheckedSummaryText}{Environment.NewLine}{_localization.Tr("panel4.tooltip_start_checked")}"
        : _execution.SortSummaryTooltip;

    private bool CanStart() => _execution.CanStartOperation && (!IsPartialSelection || CanSortChecked);

    /// <summary>
    /// Sort button of the footer: sorts the checked games while some are unchecked, otherwise the whole simulation
    /// (the ordinary sort, which may purge the destination).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    public Task StartAsync() => IsPartialSelection ? SortCheckedAsync() : _execution.StartSortAsync();

    private void NotifyFooterChanged()
    {
        OnPropertyChanged(nameof(StartButtonText));
        OnPropertyChanged(nameof(StartButtonTooltip));
        OnPropertyChanged(nameof(IsStartPutForward));
        OnPropertyChanged(nameof(FooterSummaryText));
        OnPropertyChanged(nameof(IsFooterSummaryVisible));
        OnPropertyChanged(nameof(FooterSummaryTooltip));
        StartCommand.NotifyCanExecuteChanged();
    }

    // =========================================================================
    // Checking games in the table
    // =========================================================================

    private bool? _areShownRowsChecked = false;

    /// <summary>
    /// "Check all" box: whether the games of the rows shown are all checked (<see langword="true"/>), none of them
    /// (<see langword="false"/>), or some (<see langword="null"/>). Setting it checks or unchecks them all.
    /// </summary>
    public bool? AreShownRowsChecked
    {
        get => _areShownRowsChecked;
        set
        {
            if (value is { } isChecked)
            {
                SetChecked(_table.FilteredPreviewItems.ToList(), isChecked);
            }

            // The box shows the state of the rows, even when nothing could be checked
            OnPropertyChanged();
        }
    }

    /// <summary>Whether a row shown can be checked, which enables the "check all" box.</summary>
    [ObservableProperty]
    private bool _canCheckShownRows;

    private void RefreshShownRowsState()
    {
        var shownUnits = _table.FilteredPreviewItems
            .Where(IsCheckable)
            .Select(r => r.SelectionUnitKey)
            .ToHashSet(StringComparer.Ordinal);
        CanCheckShownRows = shownUnits.Count > 0;
        var checkedCount = shownUnits.Count(IsSortedUnit);
        bool? state = checkedCount == 0 ? false : checkedCount == shownUnits.Count ? true : null;
        if (state != _areShownRowsChecked)
        {
            _areShownRowsChecked = state;
            OnPropertyChanged(nameof(AreShownRowsChecked));
        }
    }

    /// <summary>
    /// Check box of a row, already toggled by the view: checks or unchecks the game of a file, or all the files of the
    /// game of a header.
    /// </summary>
    [RelayCommand]
    public void ToggleRowCheck(PreviewRowViewModel? row)
    {
        if (row is not null)
        {
            SetChecked([row], row.IsSortChecked);
        }
    }

    /// <summary>
    /// Checks or unchecks the games of <paramref name="rows"/>: files, or headers for all their files. Only the files
    /// the sort writes choose their game: an excluded version does nothing.
    /// </summary>
    public void SetChecked(IEnumerable<PreviewRowViewModel> rows, bool isChecked)
    {
        var units = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            IEnumerable<PreviewItemViewModel> files = row switch
            {
                PreviewGroupHeaderViewModel header => header.Files,
                PreviewItemViewModel file => [file],
                _ => []
            };
            units.UnionWith(files.Where(IsCheckable).Select(f => f.SelectionUnitKey));
        }

        foreach (var unitKey in units)
        {
            if (isChecked)
            {
                _uncheckedUnits.Remove(unitKey);
                _checkedUnits.Add(unitKey);
            }
            else
            {
                _uncheckedUnits.Add(unitKey);
                _checkedUnits.Remove(unitKey);
            }

            foreach (var file in _rowsByUnit.GetValueOrDefault(unitKey) ?? [])
            {
                UpdateRowCheck(file);
            }
        }

        // A row that cannot be checked goes back to its state
        foreach (var row in rows)
        {
            if (row is PreviewItemViewModel file)
            {
                UpdateRowCheck(file);
            }
        }

        RefreshHeaders();
        _checkedRequest = null;
        RefreshChecked();

        // The sort of the checked games shown follows the boxes too
        _displayedRequest = null;
        RefreshDisplayed();
    }

    /// <summary>Rows selected in the grid, given by the view at each change of its selection.</summary>
    public void SetGridSelection(IEnumerable<PreviewRowViewModel> rows)
    {
        _gridSelection = rows.ToList();
        CheckGridSelectionCommand.NotifyCanExecuteChanged();
        UncheckGridSelectionCommand.NotifyCanExecuteChanged();
    }

    public bool HasGridSelection => _gridSelection.Count > 0;

    /// <summary>Checks the games of the rows selected in the grid.</summary>
    [RelayCommand(CanExecute = nameof(HasGridSelection))]
    public void CheckGridSelection() => SetChecked(_gridSelection, true);

    /// <summary>Unchecks the games of the rows selected in the grid.</summary>
    [RelayCommand(CanExecute = nameof(HasGridSelection))]
    public void UncheckGridSelection() => SetChecked(_gridSelection, false);

    /// <summary>
    /// Space bar on the grid: unchecks the selected rows when they are all checked, otherwise checks them.
    /// </summary>
    public void ToggleGridSelection()
    {
        var checkable = _gridSelection.Where(r => r.CanBeSortChecked).ToList();
        if (checkable.Count > 0)
        {
            SetChecked(checkable, !checkable.All(r => r.IsSortChecked));
        }
    }

    /// <summary>
    /// Forgets the games unchecked by the user: the table is emptied (another source, a real sort, a failed run), and
    /// every game of the next simulation is checked.
    /// </summary>
    public void Forget()
    {
        _uncheckedUnits.Clear();
        _checkedUnits.Clear();
        _gridSelection = [];
        MarkRowsChanged();
        Refresh();
    }

    // =========================================================================
    // Sorts
    // =========================================================================

    /// <summary>Sorts the checked games of the rows shown, after a confirmation that describes them.</summary>
    [RelayCommand(CanExecute = nameof(CanSortDisplayed))]
    public Task SortDisplayedAsync()
    {
        Refresh();
        return _displayedRequest is { } request ? _execution.StartPartialSortAsync(request) : Task.CompletedTask;
    }

    /// <summary>Sorts the checked games, after a confirmation that describes them.</summary>
    [RelayCommand(CanExecute = nameof(CanSortChecked))]
    public Task SortCheckedAsync()
    {
        Refresh();
        return _checkedRequest is { } request ? _execution.StartPartialSortAsync(request) : Task.CompletedTask;
    }

    /// <summary>The selection the next sort of the rows shown would transfer, for the tests.</summary>
    internal SortSelection? DisplayedSelection => _displayedRequest?.Selection;

    /// <summary>The selection the next sort of the checked games would transfer, for the tests.</summary>
    internal SortSelection? CheckedSelection => _checkedRequest?.Selection;

    public void RefreshTranslations()
    {
        // The tooltips of the check boxes are written in every row
        MarkRowsChanged();
        Refresh();
        NotifyFooterChanged();
    }
}
