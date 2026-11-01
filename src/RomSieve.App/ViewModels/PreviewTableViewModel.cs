using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Action descriptor for preview table context menu item.
/// </summary>
public sealed record PreviewContextMenuAction(string Name, string Text, Action Trigger);

/// <summary>
/// ViewModel managing the 10-column simulation preview table, real-time search filtering,
/// column sorting, contextual menu actions, and in-memory 1G1R overrides.
/// </summary>
public sealed partial class PreviewTableViewModel : ViewModelBase
{
    private readonly ILocalizationService _localization;
    private readonly IRomFilterService _filterService;
    private readonly ISortProcessorService _processorService;
    private readonly Func<bool> _getIsRunning;
    private readonly Func<SortConfig?> _getBuildConfig;
    private readonly Func<DatCatalog?> _getDatCatalog;
    private readonly Func<ProcessStats?> _getStats;
    private readonly Func<IReadOnlySet<string>> _getActiveStatusFilters;
    private readonly Action<IReadOnlyList<ProcessItem>, ProcessStats> _onStatsUpdated;
    private readonly Action<LogLevel, LocalizedMessage> _log;
    private readonly Action _onEnsure1G1REnabled;
    private readonly Action _onResetStatusFilters;
    private readonly Action _onNotifyExportsCanExecute;

    private readonly Dictionary<string, string> _manual1G1RWinners = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownGameTitlesSet = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folded games of the view grouped by game, kept from a simulation to the next one of the same source.</summary>
    private readonly HashSet<string> _collapsedGroupKeys = new(StringComparer.Ordinal);

    /// <summary>Headers of the games shown grouped, kept by key so that a refresh keeps the same rows.</summary>
    private readonly Dictionary<string, PreviewGroupHeaderViewModel> _groupHeaders = new(StringComparer.Ordinal);

    /// <summary>Group of the files without an identified game, always shown last.</summary>
    public const string UnidentifiedGroupKey = "\u0000unidentified";

    public static readonly IReadOnlySet<int> SortablePreviewColumns = new HashSet<int> { 0, 2, 3, 4, 5, 6, 7, 9, 10 };

    /// <summary>
    /// Names of the 11 columns in <c>config.json</c>, by index. The column of related files came last so that the
    /// indexes of the others did not change; the view shows it after the disk column.
    /// </summary>
    public static readonly IReadOnlyList<string> ColumnIds =
        ["source", "crc32", "title", "disk", "publisher", "date", "languages", "country", "target", "status", "related"];

    /// <summary>Index of the column of related files (tracks, CHDs).</summary>
    public const int RelatedColumnIndex = 10;

    /// <summary>Columns that cannot be hidden: without them, a row cannot be identified.</summary>
    private static readonly IReadOnlySet<string> AlwaysVisibleColumnIds = new HashSet<string> { "source", "status" };

    /// <summary>
    /// Columns hidden by default, rarely useful to decide: CRC32, publisher and disk; and the number of related files,
    /// which the badge next to the source name already shows.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultHiddenColumnIds = ["crc32", "disk", "publisher", "related"];

    public PreviewTableViewModel(
        ILocalizationService localization,
        IRomFilterService filterService,
        ISortProcessorService processorService,
        Func<bool> getIsRunning,
        Func<SortConfig?> getBuildConfig,
        Func<DatCatalog?> getDatCatalog,
        Func<ProcessStats?> getStats,
        Func<IReadOnlySet<string>> getActiveStatusFilters,
        Action<IReadOnlyList<ProcessItem>, ProcessStats> onStatsUpdated,
        Action<LogLevel, LocalizedMessage> log,
        Action onEnsure1G1REnabled,
        Action onResetStatusFilters,
        Action onNotifyExportsCanExecute)
    {
        _localization = localization;
        _filterService = filterService;
        _processorService = processorService;
        _getIsRunning = getIsRunning;
        _getBuildConfig = getBuildConfig;
        _getDatCatalog = getDatCatalog;
        _getStats = getStats;
        _getActiveStatusFilters = getActiveStatusFilters;
        _onStatsUpdated = onStatsUpdated;
        _log = log;
        _onEnsure1G1REnabled = onEnsure1G1REnabled;
        _onResetStatusFilters = onResetStatusFilters;
        _onNotifyExportsCanExecute = onNotifyExportsCanExecute;

        Columns = ColumnIds
            .Select((id, index) => new PreviewColumnViewModel(index, id, canHide: !AlwaysVisibleColumnIds.Contains(id)))
            .ToList();
        HideableColumns = Columns.Where(c => c.CanHide).ToList();
        SetHiddenColumnIds(null);

        RebuildColumnHeaders();
    }

    // =========================================================================
    // Columns shown, detail panel
    // =========================================================================

    /// <summary>The 11 columns of the table, by index, with their header and visibility.</summary>
    public IReadOnlyList<PreviewColumnViewModel> Columns { get; }

    /// <summary>Columns offered in the columns menu (all but the source name and the status).</summary>
    public IReadOnlyList<PreviewColumnViewModel> HideableColumns { get; }

    /// <summary>Names of the hidden columns, saved in <c>config.json</c> as a state of the interface.</summary>
    public IReadOnlyList<string> HiddenColumnIds => Columns.Where(c => !c.IsVisible).Select(c => c.Id).ToList();

    /// <summary>
    /// Hides the named columns and shows the others; <see langword="null"/> restores the default columns. Unknown
    /// names, and those of the columns that are always shown, are ignored. A column missing from
    /// <paramref name="knownIds"/>, the columns of the version that saved the list (every column when
    /// <see langword="null"/>), was added since: it takes its default visibility.
    /// </summary>
    public void SetHiddenColumnIds(IEnumerable<string>? hiddenIds, IEnumerable<string>? knownIds = null)
    {
        var hidden = new HashSet<string>(hiddenIds ?? DefaultHiddenColumnIds, StringComparer.OrdinalIgnoreCase);
        var known = hiddenIds is null || knownIds is null
            ? null
            : new HashSet<string>(knownIds, StringComparer.OrdinalIgnoreCase);
        foreach (var column in Columns)
        {
            var isHidden = known is not null && !known.Contains(column.Id)
                ? DefaultHiddenColumnIds.Contains(column.Id)
                : hidden.Contains(column.Id);
            column.IsVisible = !column.CanHide || !isHidden;
        }
    }

    [RelayCommand]
    public void ResetColumns() => SetHiddenColumnIds(null);

    /// <summary>
    /// Whether the detail panel of the selected row is unfolded below the table. Folded by default: unfolded, it takes
    /// rows from a table that is short at the default size of the window.
    /// </summary>
    [ObservableProperty]
    private bool _isDetailPanelExpanded;

    // =========================================================================
    // Observable Collections
    // =========================================================================

    // Bulk collections: a reload, a sort or a filter replaces their content with a single notification
    public BulkObservableCollection<PreviewItemViewModel> AllPreviewItems { get; } = [];
    public BulkObservableCollection<PreviewItemViewModel> FilteredPreviewItems { get; } = [];

    /// <summary>
    /// Rows of the table: the files of <see cref="FilteredPreviewItems"/> in the flat view; in the view grouped by
    /// game, the header of each game of several files followed by its files while it is unfolded.
    /// </summary>
    public BulkObservableCollection<PreviewRowViewModel> DisplayedRows { get; } = [];
    public BulkObservableCollection<string> PreviewKnownTitles { get; } = [];
    public ObservableCollection<string> PreviewColumnHeaders { get; } = [];
    public ObservableCollection<string> PreviewColumnTooltips { get; } = [];

    public IReadOnlyDictionary<string, string> Manual1G1RWinners => _manual1G1RWinners;

    /// <summary>
    /// Raised once the rows shown are laid out again (search, status chips, grouping, sort, reload), whatever the
    /// number of rows that changed; not for the rows added one by one while a run streams them.
    /// </summary>
    public event EventHandler? RowsRefreshed;

    /// <summary>Headers of the games of several files in the view grouped by game (empty in the flat view).</summary>
    public IReadOnlyCollection<PreviewGroupHeaderViewModel> GroupHeaders => _groupHeaders.Values;
    public bool HasManual1G1ROverrides => _manual1G1RWinners.Count > 0;

    // =========================================================================
    // Observable Properties
    // =========================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewSearchFilter))]
    [NotifyPropertyChangedFor(nameof(CanShowPreviewContextMenuSeparator))]
    [NotifyPropertyChangedFor(nameof(CanOpenPreviewContextMenu))]
    private string _previewSearchText = string.Empty;

    partial void OnPreviewSearchTextChanged(string value)
    {
        RefreshFilteredPreviewItems();
    }

    /// <summary>
    /// Criterion of the 1G1R cascade whose decided games alone are shown, or <c>null</c>: in each game in which it
    /// discarded a version (<see cref="ProcessItem.EliminatedBy"/>), the kept version and the versions it discarded,
    /// not those another criterion discarded. It combines with the search and the status chips.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCriterionFilter), nameof(CriterionFilterText))]
    private TiebreakCriterion? _criterionFilter;

    partial void OnCriterionFilterChanged(TiebreakCriterion? value) => RefreshFilteredPreviewItems();

    /// <summary>Whether only the games decided by a criterion are shown.</summary>
    public bool HasCriterionFilter => CriterionFilter.HasValue;

    /// <summary>What the table shows while it is filtered on a criterion.</summary>
    public string CriterionFilterText => CriterionFilter is { } criterion
        ? _localization.Tr("panel4.criterion_filter", ("criterion", _localization.Tr($"tiebreak.{criterion.ToConfigString()}")))
        : string.Empty;

    [RelayCommand]
    public void ClearCriterionFilter() => CriterionFilter = null;

    // Games decided by the criterion of the filter, read again at each refresh of the rows (a recalculation changes them)
    private HashSet<string>? _criterionGames;

    private void RefreshCriterionGames()
    {
        _criterionGames = CriterionFilter is { } criterion
            ? AllPreviewItems
                .Where(r => r.Item.EliminatedBy == criterion && r.Item.Status != FileStatus.Valid)
                .Select(r => r.CanonicalGameKey)
                .ToHashSet(StringComparer.Ordinal)
            : null;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsolateGameMenuText))]
    [NotifyPropertyChangedFor(nameof(CanForce1G1RWinner))]
    [NotifyPropertyChangedFor(nameof(CanResetSelected1G1ROverride))]
    [NotifyPropertyChangedFor(nameof(CanIsolateSelectedGame))]
    [NotifyPropertyChangedFor(nameof(CanShowPreviewContextMenuSeparator))]
    [NotifyPropertyChangedFor(nameof(CanOpenPreviewContextMenu))]
    [NotifyPropertyChangedFor(nameof(SelectedMameUsersText))]
    private PreviewItemViewModel? _selectedPreviewItem;

    /// <summary>
    /// Machines that take ROMs from the selected MAME set, for the detail panel: how many the DAT lists, how many are in
    /// the source and how many are kept, named. Empty for a row that is not a MAME set, or a set no machine uses.
    /// Computed for the selected row only, as it reads every row.
    /// </summary>
    public string SelectedMameUsersText =>
        SelectedPreviewItem?.Item is { MameSet: { } set } && _getDatCatalog() is { } catalog
            ? _localization.Format(DescribeMameUsers(catalog.CatalogFor(set.SoftwareList).GetMameDependents(set.GameName), AllPreviewItems.Select(r => r.Item).Where(it => it.MameSet?.SoftwareList == set.SoftwareList)))
            : string.Empty;

    /// <summary>
    /// Machines that take ROMs from a MAME set, compared with the items of a run: <c>null</c> when there are none.
    /// </summary>
    public static LocalizedMessage? DescribeMameUsers(IReadOnlyList<DatGame> users, IEnumerable<ProcessItem> items)
    {
        if (users.Count == 0)
        {
            return null;
        }

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item.MameSet is null || string.IsNullOrEmpty(item.DatGameName))
            {
                continue;
            }

            present.Add(item.DatGameName);
            if (item.Status == FileStatus.Valid)
            {
                kept.Add(item.DatGameName);
            }
        }

        var presentCount = users.Count(u => present.Contains(u.Name));
        var keptNames = users.Where(u => kept.Contains(u.Name)).Select(u => u.Name).ToList();
        if (presentCount == 0)
        {
            return LocalizedMessage.Create("preview.mame_used_by_none_present", ("count", users.Count));
        }

        return keptNames.Count == 0
            ? LocalizedMessage.Create("preview.mame_used_by_none_kept", ("count", users.Count), ("present", presentCount))
            : LocalizedMessage.Create("preview.mame_used_by",
                ("count", users.Count),
                ("present", presentCount),
                ("kept", keptNames.Count),
                ("names", MameSetDependencies.FormatNames(keptNames)));
    }

    /// <summary>Selected row of the table: a file (<see cref="SelectedPreviewItem"/>) or the header of a game.</summary>
    [ObservableProperty]
    private PreviewRowViewModel? _selectedRow;

    /// <summary>Selected header of a game, <see langword="null"/> when the selected row is a file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsolateGameMenuText))]
    [NotifyPropertyChangedFor(nameof(CanResetSelected1G1ROverride))]
    [NotifyPropertyChangedFor(nameof(CanIsolateSelectedGame))]
    [NotifyPropertyChangedFor(nameof(CanShowPreviewContextMenuSeparator))]
    [NotifyPropertyChangedFor(nameof(CanOpenPreviewContextMenu))]
    [NotifyPropertyChangedFor(nameof(IsGroupHeaderSelected))]
    [NotifyPropertyChangedFor(nameof(ToggleGroupMenuText))]
    private PreviewGroupHeaderViewModel? _selectedGroupHeader;

    partial void OnSelectedRowChanged(PreviewRowViewModel? value)
    {
        SelectedPreviewItem = value as PreviewItemViewModel;
        SelectedGroupHeader = value as PreviewGroupHeaderViewModel;
    }

    partial void OnSelectedPreviewItemChanged(PreviewItemViewModel? value)
    {
        if (value is not null)
        {
            SelectedRow = value;
        }
        else if (SelectedRow is PreviewItemViewModel)
        {
            SelectedRow = null;
        }
    }

    /// <summary>
    /// Whether the table shows the files grouped by game, the 1G1R winner first: a state of the interface, saved in
    /// <c>config.json</c> (<c>preview_grouped_by_game</c>).
    /// </summary>
    [ObservableProperty]
    private bool _isGroupedByGame;

    partial void OnIsGroupedByGameChanged(bool value) => RefreshFilteredPreviewItems();

    public bool IsGroupHeaderSelected => SelectedGroupHeader is not null;

    public string ToggleGroupMenuText => _localization.Tr(
        SelectedGroupHeader?.IsExpanded == false ? "panel4.ctx_group_expand" : "panel4.ctx_group_collapse");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSortIndicatorShown))]
    private int? _currentSortColumn;

    [ObservableProperty]
    private bool _isSortAscending = true;

    public bool IsSortIndicatorShown => CurrentSortColumn.HasValue;
    public bool HasPreviewSearchFilter => !string.IsNullOrWhiteSpace(PreviewSearchText);

    public bool CanForce1G1RWinner =>
        !_getIsRunning()
        && SelectedPreviewItem is not null
        && SelectedPreviewItem.Item.Parsed.IsConform
        && SelectedPreviewItem.Item.Status != FileStatus.Unmatched
        && SelectedPreviewItem.Item.Status != FileStatus.Error;

    public bool CanResetSelected1G1ROverride =>
        !_getIsRunning() && GetOverrideKey(SelectedRow) is { } key && _manual1G1RWinners.ContainsKey(key);

    public bool CanIsolateSelectedGame => GetGameTitle(SelectedRow) is { Length: > 0 } title && title != "-";

    public bool CanShowPreviewContextMenuSeparator =>
        (CanForce1G1RWinner || CanResetSelected1G1ROverride || HasManual1G1ROverrides)
        && (CanIsolateSelectedGame || HasPreviewSearchFilter);

    public bool CanOpenPreviewContextMenu =>
        !_getIsRunning()
        && (IsGroupHeaderSelected || CanForce1G1RWinner || CanResetSelected1G1ROverride || HasManual1G1ROverrides || CanIsolateSelectedGame || HasPreviewSearchFilter);

    public string IsolateGameMenuText
    {
        get
        {
            var title = GetGameTitle(SelectedRow);
            return !string.IsNullOrEmpty(title) && title != "-"
                ? _localization.Tr("panel4.ctx_filter_this_game", ("title", title))
                : _localization.Tr("panel4.ctx_filter_this_game", ("title", "..."));
        }
    }

    public string ClearAll1G1ROverridesMenuText =>
        _localization.Tr("panel4.ctx_clear_all_1g1r_overrides", ("count", _manual1G1RWinners.Count));

    public void Notify1G1ROverrideStateChanged()
    {
        OnPropertyChanged(nameof(HasManual1G1ROverrides));
        OnPropertyChanged(nameof(ClearAll1G1ROverridesMenuText));
        OnPropertyChanged(nameof(CanResetSelected1G1ROverride));
        OnPropertyChanged(nameof(CanShowPreviewContextMenuSeparator));
        OnPropertyChanged(nameof(CanOpenPreviewContextMenu));
    }

    // =========================================================================
    // Commands
    // =========================================================================

    [RelayCommand]
    public void Force1G1RWinner(PreviewItemViewModel? row)
    {
        if (row is null || _getIsRunning())
        {
            return;
        }

        var item = row.Item;
        if (!item.Parsed.IsConform)
        {
            return;
        }

        var canonicalKey = _filterService.SerializeCanonicalGameKey(item.Parsed);
        _manual1G1RWinners[canonicalKey] = item.DisplayName;

        _onEnsure1G1REnabled();

        RecalculateSingleGameInPreview(canonicalKey);
        Notify1G1ROverrideStateChanged();
        _log(LogLevel.Info, LocalizedMessage.Create("log.manual_1g1r_forced",
            ("title", item.Parsed.Title),
            ("name", item.DisplayName)));
    }

    [RelayCommand]
    public void Reset1G1ROverrideForGame(PreviewRowViewModel? row)
    {
        if (_getIsRunning() || GetOverrideKey(row) is not { } canonicalKey)
        {
            return;
        }

        if (_manual1G1RWinners.Remove(canonicalKey))
        {
            RecalculateSingleGameInPreview(canonicalKey);
            Notify1G1ROverrideStateChanged();
            _log(LogLevel.Info, LocalizedMessage.Create("log.manual_1g1r_reset", ("title", GetGameTitle(row) ?? string.Empty)));
        }
    }

    [RelayCommand]
    public void ClearAll1G1ROverrides()
    {
        if (_manual1G1RWinners.Count == 0 || _getIsRunning())
        {
            return;
        }

        var affectedKeys = _manual1G1RWinners.Keys.ToList();
        _manual1G1RWinners.Clear();

        foreach (var key in affectedKeys)
        {
            RecalculateSingleGameInPreview(key);
        }

        Notify1G1ROverrideStateChanged();
        _log(LogLevel.Info, LocalizedMessage.Create("log.manual_1g1r_cleared_all"));
    }

    [RelayCommand]
    public void IsolateGameInPreview(PreviewRowViewModel? row)
    {
        var title = GetGameTitle(row);
        if (!string.IsNullOrEmpty(title))
        {
            _onResetStatusFilters();
            PreviewSearchText = title;
        }
    }

    /// <summary>Folds an unfolded game, or unfolds a folded one, in the view grouped by game.</summary>
    [RelayCommand]
    public void ToggleGroup(PreviewGroupHeaderViewModel? header)
    {
        if (header is not null)
        {
            SetGroupExpanded(header, !header.IsExpanded);
        }
    }

    /// <summary>Folds or unfolds a game; a file of the game selected when it is folded leaves the selection to its header.</summary>
    public void SetGroupExpanded(PreviewGroupHeaderViewModel header, bool isExpanded)
    {
        if (header.IsExpanded == isExpanded)
        {
            return;
        }

        if (isExpanded)
        {
            _collapsedGroupKeys.Remove(header.GroupKey);
        }
        else
        {
            _collapsedGroupKeys.Add(header.GroupKey);
        }

        header.IsExpanded = isExpanded;
        OnPropertyChanged(nameof(ToggleGroupMenuText));
        RefreshFilteredPreviewItems();
    }

    /// <summary>Folds every game of several files: the table then lists the games.</summary>
    [RelayCommand]
    public void CollapseAllGroups()
    {
        foreach (var header in _groupHeaders.Values)
        {
            _collapsedGroupKeys.Add(header.GroupKey);
            header.IsExpanded = false;
        }

        OnPropertyChanged(nameof(ToggleGroupMenuText));
        RefreshFilteredPreviewItems();
    }

    /// <summary>Unfolds every game.</summary>
    [RelayCommand]
    public void ExpandAllGroups()
    {
        _collapsedGroupKeys.Clear();
        foreach (var header in _groupHeaders.Values)
        {
            header.IsExpanded = true;
        }

        OnPropertyChanged(nameof(ToggleGroupMenuText));
        RefreshFilteredPreviewItems();
    }

    [RelayCommand]
    public void ClearPreviewSearch()
    {
        PreviewSearchText = string.Empty;
    }

    [RelayCommand]
    public void SortPreviewByColumn(int columnIndex)
    {
        if (!SortablePreviewColumns.Contains(columnIndex) || AllPreviewItems.Count == 0)
        {
            return;
        }

        if (CurrentSortColumn == columnIndex)
        {
            IsSortAscending = !IsSortAscending;
        }
        else
        {
            CurrentSortColumn = columnIndex;
            IsSortAscending = true;
        }

        ApplyPreviewSorting();
        RefreshFilteredPreviewItems();
    }

    // =========================================================================
    // Context Menu Construction
    // =========================================================================

    public IReadOnlyList<PreviewContextMenuAction> BuildPreviewContextMenuForRow(int rowIndex)
    {
        if (_getIsRunning() || rowIndex < 0 || rowIndex >= AllPreviewItems.Count)
        {
            return [];
        }

        return BuildPreviewContextMenu(AllPreviewItems[rowIndex]);
    }

    public IReadOnlyList<PreviewContextMenuAction> BuildPreviewContextMenu(PreviewRowViewModel? row)
    {
        if (_getIsRunning() || row is null)
        {
            return [];
        }

        var actions = new List<PreviewContextMenuAction>();

        if (row is PreviewGroupHeaderViewModel header)
        {
            actions.Add(new PreviewContextMenuAction(
                "act_toggle_group",
                _localization.Tr(header.IsExpanded ? "panel4.ctx_group_collapse" : "panel4.ctx_group_expand"),
                () => ToggleGroup(header)));
            actions.Add(new PreviewContextMenuAction(
                "act_collapse_all_groups",
                _localization.Tr("panel4.ctx_collapse_all"),
                CollapseAllGroups));
            actions.Add(new PreviewContextMenuAction(
                "act_expand_all_groups",
                _localization.Tr("panel4.ctx_expand_all"),
                ExpandAllGroups));
        }
        else if (row is PreviewItemViewModel item && item.Item.Parsed.IsConform)
        {
            actions.Add(new PreviewContextMenuAction(
                "act_force_1g1r_winner",
                _localization.Tr("panel4.ctx_force_1g1r_winner"),
                () => Force1G1RWinner(item)));
        }

        if (GetOverrideKey(row) is { } gameKeyStr && _manual1G1RWinners.ContainsKey(gameKeyStr))
        {
            actions.Add(new PreviewContextMenuAction(
                "act_reset_1g1r_override",
                _localization.Tr("panel4.ctx_reset_1g1r_override"),
                () => Reset1G1ROverrideForGame(row)));
        }

        if (_manual1G1RWinners.Count > 0)
        {
            actions.Add(new PreviewContextMenuAction(
                "act_clear_all_1g1r_overrides",
                _localization.Tr("panel4.ctx_clear_all_1g1r_overrides", ("count", _manual1G1RWinners.Count)),
                ClearAll1G1ROverrides));
        }

        var titleVal = GetGameTitle(row) ?? string.Empty;
        if (!string.IsNullOrEmpty(titleVal))
        {
            actions.Add(new PreviewContextMenuAction(
                "act_filter_this_game",
                _localization.Tr("panel4.ctx_filter_this_game", ("title", titleVal)),
                () => IsolateGameInPreview(row)));
        }

        if (!string.IsNullOrWhiteSpace(PreviewSearchText))
        {
            actions.Add(new PreviewContextMenuAction(
                "act_clear_game_filter",
                _localization.Tr("panel4.ctx_clear_game_filter"),
                ClearPreviewSearch));
        }

        return actions;
    }

    // =========================================================================
    // Preview Table Methods
    // =========================================================================

    // =========================================================================
    // Differential simulations
    // =========================================================================

    /// <summary>Highest number of row insertions and removals applied one by one, beyond which the rows are reloaded at once.</summary>
    public const int MaxIncrementalRowEdits = 500;

    /// <summary>Source of the last finished simulation shown in the table, which a new simulation can be compared with.</summary>
    private string? _comparableSourceDir;

    /// <summary>Whether the running simulation will be merged into the current rows instead of replacing them.</summary>
    public bool IsDifferentialRunInProgress { get; private set; }

    /// <summary>Whether the rows show the changes since a previous simulation (<see cref="PreviewItemViewModel.Change"/>).</summary>
    public bool HasComparison { get; private set; }

    /// <summary>Number of rows added or modified since the previous simulation.</summary>
    public int ChangedRowCount => AllPreviewItems.Count(r => r.IsChangedSinceLastRun);

    /// <summary>
    /// Identity of a row across simulations: the source path and, for a file read from an archive, the member name.
    /// </summary>
    public static string GetRowKey(ProcessItem item) => SortSelection.GetItemKey(item);

    /// <summary>Whether a simulation of <paramref name="sourceDir"/> can be compared with the rows of the table.</summary>
    public bool CanCompareWithPreviousSimulation(string sourceDir) =>
        _comparableSourceDir is not null
        && AllPreviewItems.Count > 0
        && SortConfigValidator.GetPathOverlap(_comparableSourceDir, sourceDir, recursive: false) == PathOverlap.Identical;

    /// <summary>Starts a simulation whose results will be merged into the current rows when it finishes.</summary>
    public void BeginDifferentialRun() => IsDifferentialRunInProgress = true;

    /// <summary>Stops comparing the next simulation with the current rows (change of source, failed run...).</summary>
    public void ForgetPreviousSimulation()
    {
        _comparableSourceDir = null;
        IsDifferentialRunInProgress = false;
    }

    /// <summary>
    /// Ends a run: a finished simulation of <paramref name="simulatedSourceDir"/> becomes the reference of the next one,
    /// while a real sort (<see langword="null"/>) leaves nothing to compare with.
    /// </summary>
    public void CompleteRun(string? simulatedSourceDir)
    {
        IsDifferentialRunInProgress = false;
        _comparableSourceDir = simulatedSourceDir;
    }

    /// <summary>
    /// Merges the results of a differential simulation into the rows: the row of a file still present is bound to
    /// its new item and keeps its place, a new file gets a new row, a file no longer scanned loses its row. Rows
    /// whose status, target or reason changed, and new rows, are marked; the order of the current sort is kept.
    /// </summary>
    public void ApplyDifferentialResults(string? destRoot, IReadOnlyList<ProcessItem> items)
    {
        var existingRows = new Dictionary<string, PreviewItemViewModel>(StringComparer.Ordinal);
        foreach (var row in AllPreviewItems)
        {
            existingRows.TryAdd(GetRowKey(row.Item), row);
        }

        var rows = new List<PreviewItemViewModel>(items.Count);
        foreach (var item in items)
        {
            if (existingRows.Remove(GetRowKey(item), out var row))
            {
                var before = new PreviewRowState(row.Item.Status, row.Item.FinalTargetPath ?? row.Item.TargetPath, row.Item.StatusReason);
                row.Rebind(item, destRoot);
                var after = new PreviewRowState(item.Status, item.FinalTargetPath ?? item.TargetPath, item.StatusReason);
                row.SetChange(before.Equals(after) ? PreviewRowChange.None : PreviewRowChange.Modified, before);
            }
            else
            {
                row = new PreviewItemViewModel(item, _localization, _filterService, destRoot);
                row.SetChange(PreviewRowChange.Added);
            }

            rows.Add(row);
        }

        _knownGameTitlesSet.Clear();
        foreach (var row in rows)
        {
            var title = row.Item.Parsed.Title?.Trim();
            if (!string.IsNullOrEmpty(title) && title != "-")
            {
                _knownGameTitlesSet.Add(title);
            }
        }

        var titles = _knownGameTitlesSet.Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (!titles.SequenceEqual(PreviewKnownTitles, StringComparer.Ordinal))
        {
            PreviewKnownTitles.ReplaceAll(titles);
        }

        AllPreviewItems.ReplaceAll(rows);
        ApplyPreviewSorting();
        HasComparison = true;
        RefreshFilteredPreviewItems();
    }

    public void Clear()
    {
        IsDifferentialRunInProgress = false;
        HasComparison = false;
        AllPreviewItems.Clear();
        FilteredPreviewItems.Clear();
        DisplayedRows.Clear();
        PreviewKnownTitles.Clear();
        _knownGameTitlesSet.Clear();
        _groupHeaders.Clear();
        _collapsedGroupKeys.Clear();
        CurrentSortColumn = null;
        IsSortAscending = true;
        SelectedRow = null;
        CriterionFilter = null;
    }

    public void AddPreviewItem(ProcessItem item, string? destRoot)
    {
        var rowVm = new PreviewItemViewModel(item, _localization, _filterService, destRoot);
        AllPreviewItems.Add(rowVm);

        var title = item.Parsed.Title?.Trim();
        if (!string.IsNullOrEmpty(title) && title != "-" && _knownGameTitlesSet.Add(title))
        {
            InsertKnownTitleSorted(title);
        }

        // During a run, the rows come in the order of processing; the games are grouped once it is finished
        if (DoesRowMatchActiveFilters(rowVm))
        {
            FilteredPreviewItems.Add(rowVm);
            DisplayedRows.Add(rowVm);
        }
    }

    public void RebuildAllPreviewItemsFromLastItems(string? destRoot, IReadOnlyList<ProcessItem> lastItems)
    {
        HasComparison = false;
        _knownGameTitlesSet.Clear();

        var rows = new List<PreviewItemViewModel>(lastItems.Count);
        foreach (var item in lastItems)
        {
            rows.Add(new PreviewItemViewModel(item, _localization, _filterService, destRoot));

            var title = item.Parsed.Title?.Trim();
            if (!string.IsNullOrEmpty(title) && title != "-")
            {
                _knownGameTitlesSet.Add(title);
            }
        }

        AllPreviewItems.ReplaceAll(rows);
        PreviewKnownTitles.ReplaceAll(_knownGameTitlesSet.Order(StringComparer.OrdinalIgnoreCase).ToList());

        RefreshFilteredPreviewItems();
    }

    public void RecalculateSingleGameInPreview(string targetCanonicalKey)
    {
        if (AllPreviewItems.Count == 0)
        {
            return;
        }

        var matchingRows = AllPreviewItems
            .Where(r => r.Item.Parsed.IsConform
                        && string.Equals(r.CanonicalGameKey, targetCanonicalKey, StringComparison.Ordinal))
            .ToList();

        if (matchingRows.Count == 0)
        {
            return;
        }

        var config = _getBuildConfig();
        if (config is null)
        {
            return;
        }

        // The MAME sets the game needs (BIOS, devices) may be kept by the recalculation: their rows are refreshed too
        var gameItems = matchingRows.Select(r => r.Item).ToList();
        var allItems = AllPreviewItems.Select(r => r.Item).ToList();
        var otherMameRows = AllPreviewItems
            .Where(r => r.Item.MameSet is not null && !matchingRows.Contains(r))
            .Select(r => (Row: r, r.Item.Status))
            .ToList();
        var datCatalog = _getDatCatalog();
        _processorService.RecalculateGameItems(gameItems, config, datCatalog, allItems);

        foreach (var row in matchingRows.Concat(otherMameRows.Where(o => o.Row.Item.Status != o.Status).Select(o => o.Row)))
        {
            row.RefreshFromItem(config.DestinationDir);
        }

        var currentStats = _getStats();
        var newStats = _processorService.RecomputeStats(allItems, currentStats);
        _onStatsUpdated(allItems, newStats);

        if (CurrentSortColumn == 9 || CurrentSortColumn == 2)
        {
            ApplyPreviewSorting();
        }

        RefreshFilteredPreviewItems();
        _onNotifyExportsCanExecute();
    }

    public void ApplyPreviewSorting()
    {
        if (!CurrentSortColumn.HasValue || AllPreviewItems.Count <= 1)
        {
            return;
        }

        var col = CurrentSortColumn.Value;
        IEnumerable<PreviewItemViewModel> ordered = col switch
        {
            0 => IsSortAscending
                ? AllPreviewItems.OrderBy(r => r.SourceName, StringComparer.OrdinalIgnoreCase)
                : AllPreviewItems.OrderByDescending(r => r.SourceName, StringComparer.OrdinalIgnoreCase),
            2 => IsSortAscending
                ? AllPreviewItems.OrderBy(r => r.TitleDisplay, StringComparer.OrdinalIgnoreCase)
                : AllPreviewItems.OrderByDescending(r => r.TitleDisplay, StringComparer.OrdinalIgnoreCase),
            3 => IsSortAscending
                ? AllPreviewItems.OrderBy(r => r.DiskDisplay, StringComparer.OrdinalIgnoreCase)
                : AllPreviewItems.OrderByDescending(r => r.DiskDisplay, StringComparer.OrdinalIgnoreCase),
            4 => IsSortAscending
                ? AllPreviewItems.OrderBy(r => r.Publisher, StringComparer.OrdinalIgnoreCase)
                : AllPreviewItems.OrderByDescending(r => r.Publisher, StringComparer.OrdinalIgnoreCase),
            5 => IsSortAscending
                ? AllPreviewItems.OrderBy(r => r.Date, StringComparer.OrdinalIgnoreCase)
                : AllPreviewItems.OrderByDescending(r => r.Date, StringComparer.OrdinalIgnoreCase),
            6 => IsSortAscending
                ? AllPreviewItems.OrderBy(r => r.LanguagesDisplay, StringComparer.OrdinalIgnoreCase)
                : AllPreviewItems.OrderByDescending(r => r.LanguagesDisplay, StringComparer.OrdinalIgnoreCase),
            7 => IsSortAscending
                ? AllPreviewItems.OrderBy(r => r.CountriesDisplay, StringComparer.OrdinalIgnoreCase)
                : AllPreviewItems.OrderByDescending(r => r.CountriesDisplay, StringComparer.OrdinalIgnoreCase),
            9 => IsSortAscending
                ? AllPreviewItems.OrderBy(r => r.StatusDisplay, StringComparer.OrdinalIgnoreCase)
                : AllPreviewItems.OrderByDescending(r => r.StatusDisplay, StringComparer.OrdinalIgnoreCase),
            RelatedColumnIndex => IsSortAscending
                ? AllPreviewItems.OrderBy(r => r.RelatedCount)
                : AllPreviewItems.OrderByDescending(r => r.RelatedCount),
            _ => AllPreviewItems
        };

        AllPreviewItems.ReplaceAll(ordered.ToList());
    }

    public void RefreshFilteredPreviewItems()
    {
        var prevSelected = SelectedRow;
        RefreshCriterionGames();
        var matches = CreateRowFilter();
        var filtered = AllPreviewItems.Where(matches).ToList();
        ApplyRows(FilteredPreviewItems, filtered);

        var displayed = IsGroupedByGame ? BuildGroupedRows(filtered) : ToFlatRows(filtered);
        ApplyRows(DisplayedRows, displayed);
        RowsRefreshed?.Invoke(this, EventArgs.Empty);

        // The rows may have changed status (a recalculation, a language switch)
        OnPropertyChanged(nameof(SelectedMameUsersText));

        if (prevSelected is null)
        {
            return;
        }

        if (displayed.Contains(prevSelected))
        {
            SelectedRow = prevSelected;
        }
        else if (IsGroupedByGame
                 && prevSelected is PreviewItemViewModel item
                 && _groupHeaders.TryGetValue(GetGroupKey(item), out var header)
                 && displayed.Contains(header))
        {
            // The file is in a folded game: its header takes the selection
            SelectedRow = header;
        }
    }

    /// <summary>
    /// Turns the content of <paramref name="rows"/> into <paramref name="target"/>: nothing for the same rows (which
    /// keeps the selection and the scroll position of the table), a few insertions and removals, or a single reload.
    /// </summary>
    private static void ApplyRows<T>(BulkObservableCollection<T> rows, List<T> target)
        where T : class
    {
        if (target.Count == rows.Count && target.SequenceEqual(rows, ReferenceEqualityComparer.Instance))
        {
            return;
        }

        // A few insertions and removals keep the other rows of the table in place (and its scroll position);
        // larger changes reload the rows at once
        if (!TryApplyRowEdits(rows, target))
        {
            rows.ReplaceAll(target);
        }
    }

    private static List<PreviewRowViewModel> ToFlatRows(List<PreviewItemViewModel> filtered)
    {
        var rows = new List<PreviewRowViewModel>(filtered.Count);
        foreach (var row in filtered)
        {
            row.IsGroupMember = false;
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Game of a row in the view grouped by game: its canonical key, or the group of the files without a game.</summary>
    private static string GetGroupKey(PreviewItemViewModel row) =>
        row.Item.Parsed.IsConform ? row.CanonicalGameKey : UnidentifiedGroupKey;

    /// <summary>Key of the manual 1G1R choice of the game of a row, <see langword="null"/> for a file without a game.</summary>
    private static string? GetOverrideKey(PreviewRowViewModel? row) => row switch
    {
        PreviewGroupHeaderViewModel { IsUnidentified: false } header => header.GroupKey,
        PreviewItemViewModel item when item.Item.Parsed.IsConform => item.CanonicalGameKey,
        _ => null
    };

    /// <summary>Title of the game of a row, searched by "Show only this game".</summary>
    private static string? GetGameTitle(PreviewRowViewModel? row) => row switch
    {
        PreviewGroupHeaderViewModel header => header.GameTitle,
        PreviewItemViewModel item => item.Item.Parsed.Title?.Trim(),
        _ => null
    };

    /// <summary>Files of a game while the rows are grouped, in the order of the current sort.</summary>
    private sealed class GameGroup(string key, int firstIndex)
    {
        public string Key { get; } = key;
        public int FirstIndex { get; } = firstIndex;
        public int WinnerIndex { get; set; } = -1;
        public List<PreviewItemViewModel> Rows { get; } = [];

        /// <summary>Place of the game: that of its 1G1R winner in the current sort, otherwise that of its first file.</summary>
        public int SortIndex => WinnerIndex >= 0 ? WinnerIndex : FirstIndex;
    }

    /// <summary>
    /// Rows of the view grouped by game. The games follow the current sort of their lead file (the 1G1R winner, or
    /// the first file in that sort), the files without a game come last. In a game, the files of the winner come
    /// first (by disc number), then the others in the current sort. A game of a single file keeps a plain row; the
    /// header of a game of several files is shown while one of its files is, followed by its shown files unless it is
    /// folded. Whether a game has a header depends on all its files, so that filtering never adds or removes one.
    /// </summary>
    private List<PreviewRowViewModel> BuildGroupedRows(List<PreviewItemViewModel> filtered)
    {
        var visible = new HashSet<PreviewItemViewModel>(filtered, ReferenceEqualityComparer.Instance);
        var groups = new Dictionary<string, GameGroup>(StringComparer.Ordinal);
        var orderedGroups = new List<GameGroup>();
        for (var i = 0; i < AllPreviewItems.Count; i++)
        {
            var row = AllPreviewItems[i];
            var key = GetGroupKey(row);
            if (!groups.TryGetValue(key, out var group))
            {
                group = new GameGroup(key, i);
                groups[key] = group;
                orderedGroups.Add(group);
            }

            if (group.WinnerIndex < 0 && row.Item.Is1G1RWinner)
            {
                group.WinnerIndex = i;
            }

            group.Rows.Add(row);
        }

        orderedGroups = orderedGroups
            .OrderBy(g => g.Key == UnidentifiedGroupKey)
            .ThenBy(g => g.SortIndex)
            .ToList();

        var result = new List<PreviewRowViewModel>(filtered.Count + orderedGroups.Count);
        var usedHeaders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in orderedGroups)
        {
            if (group.Rows.Count == 1)
            {
                var single = group.Rows[0];
                single.IsGroupMember = false;
                if (visible.Contains(single))
                {
                    result.Add(single);
                }

                continue;
            }

            var rows = group.Rows
                .Where(r => r.Item.Is1G1RWinner)
                .OrderBy(r => r.DiskSortKey.DiscNum)
                .Concat(group.Rows.Where(r => !r.Item.Is1G1RWinner))
                .ToList();
            var shownRows = rows.Where(visible.Contains).ToList();

            if (!_groupHeaders.TryGetValue(group.Key, out var header))
            {
                header = new PreviewGroupHeaderViewModel(group.Key, ToggleGroup);
                _groupHeaders[group.Key] = header;
            }

            usedHeaders.Add(group.Key);
            UpdateGroupHeader(header, rows, shownRows.Count);
            foreach (var row in rows)
            {
                row.IsGroupMember = true;
            }

            if (shownRows.Count == 0)
            {
                continue;
            }

            result.Add(header);
            if (header.IsExpanded)
            {
                result.AddRange(shownRows);
            }
        }

        if (usedHeaders.Count < _groupHeaders.Count)
        {
            foreach (var key in _groupHeaders.Keys.Where(k => !usedHeaders.Contains(k)).ToList())
            {
                _groupHeaders.Remove(key);
            }
        }

        return result;
    }

    /// <summary>Shows on the header of a game its title, its number of files, its kept file and its status.</summary>
    private void UpdateGroupHeader(PreviewGroupHeaderViewModel header, List<PreviewItemViewModel> rows, int visibleCount)
    {
        var lead = rows[0];
        var winner = lead.Item.Is1G1RWinner ? lead : null;

        header.IsExpanded = !_collapsedGroupKeys.Contains(header.GroupKey);
        header.Files = rows;
        header.TotalCount = rows.Count;
        header.VisibleCount = visibleCount;
        header.GameTitle = header.IsUnidentified ? string.Empty : lead.Item.Parsed.Title?.Trim() ?? string.Empty;
        header.SourceName = header.IsUnidentified ? _localization.Tr("panel4.group_unidentified") : lead.TitleDisplay;
        header.CountText = visibleCount == rows.Count
            ? _localization.Tr("panel4.group_count", ("count", rows.Count))
            : _localization.Tr("panel4.group_count_filtered", ("visible", visibleCount), ("total", rows.Count));

        var validCount = rows.Count(r => r.Item.Status == FileStatus.Valid);
        header.TitleDisplay = header.IsUnidentified
            ? string.Empty
            : winner is not null
                ? _localization.Tr("panel4.group_winner", ("name", winner.SourceName))
                : validCount > 0
                    ? _localization.Tr("panel4.group_valid_count", ("count", validCount))
                    : _localization.Tr("panel4.group_no_winner");
        header.TargetPathDisplay = winner?.TargetPathDisplay ?? string.Empty;
        header.Status = lead.Status;
        header.StatusLabel = lead.StatusLabel;
        header.IsForcedWinner = rows.Any(r => r.IsForcedWinner);
        header.IsRequiredSet = rows.Any(r => r.IsRequiredSet);
        header.Change = rows.Any(r => r.Change == PreviewRowChange.Modified)
            ? PreviewRowChange.Modified
            : rows.Any(r => r.Change == PreviewRowChange.Added)
                ? PreviewRowChange.Added
                : PreviewRowChange.None;

        var tooltip = new List<string>(3) { _localization.Tr("panel4.tooltip_group", ("title", header.SourceName), ("count", header.CountText)) };
        if (!string.IsNullOrEmpty(header.TitleDisplay))
        {
            tooltip.Add(header.TitleDisplay);
        }

        if (header.IsForcedWinner)
        {
            tooltip.Add(_localization.Tr("panel4.tooltip_forced_winner"));
        }

        if (header.IsRequiredSet)
        {
            tooltip.Add(_localization.Tr("panel4.tooltip_required_set"));
        }

        tooltip.Add(_localization.Tr("panel4.tooltip_group_toggle"));
        header.TooltipText = string.Join(Environment.NewLine, tooltip);
    }

    /// <summary>
    /// Turns the rows into <paramref name="target"/> by removing and inserting rows one by one, when at most
    /// <see cref="MaxIncrementalRowEdits"/> edits are needed. The longest run of rows already in the order of
    /// <paramref name="target"/> stays in place; a row that moves (the winner of a game forced by hand, which comes
    /// first in its game) is removed, then inserted at its new place.
    /// </summary>
    private static bool TryApplyRowEdits<T>(BulkObservableCollection<T> current, List<T> target)
        where T : class
    {
        var targetIndex = new Dictionary<T, int>(target.Count, ReferenceEqualityComparer.Instance);
        for (var i = 0; i < target.Count; i++)
        {
            targetIndex[target[i]] = i;
        }

        var sharedRows = new List<T>();
        var sharedTargetIndexes = new List<int>();
        foreach (var row in current)
        {
            if (targetIndex.TryGetValue(row, out var index))
            {
                sharedRows.Add(row);
                sharedTargetIndexes.Add(index);
            }
        }

        var keptRows = new HashSet<T>(ReferenceEqualityComparer.Instance);
        foreach (var position in LongestIncreasingSubsequence(sharedTargetIndexes))
        {
            keptRows.Add(sharedRows[position]);
        }

        var removals = current.Count - keptRows.Count;
        var insertions = target.Count - keptRows.Count;
        if (removals + insertions > MaxIncrementalRowEdits)
        {
            return false;
        }

        for (var i = current.Count - 1; i >= 0; i--)
        {
            if (!keptRows.Contains(current[i]))
            {
                current.RemoveAt(i);
            }
        }

        for (var i = 0; i < target.Count; i++)
        {
            if (i >= current.Count || !ReferenceEquals(current[i], target[i]))
            {
                current.Insert(i, target[i]);
            }
        }

        return true;
    }

    /// <summary>Positions in <paramref name="values"/> (distinct) of one of their longest increasing subsequences.</summary>
    private static List<int> LongestIncreasingSubsequence(List<int> values)
    {
        // Patience sorting: tails[k] is the position of the smallest last value of an increasing run of length k + 1
        var tails = new List<int>();
        var previous = new int[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            int low = 0, high = tails.Count;
            while (low < high)
            {
                var mid = (low + high) >>> 1;
                if (values[tails[mid]] < values[i])
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            previous[i] = low > 0 ? tails[low - 1] : -1;
            if (low == tails.Count)
            {
                tails.Add(i);
            }
            else
            {
                tails[low] = i;
            }
        }

        var positions = new List<int>(tails.Count);
        for (var i = tails.Count > 0 ? tails[^1] : -1; i >= 0; i = previous[i])
        {
            positions.Add(i);
        }

        positions.Reverse();
        return positions;
    }

    public bool DoesRowMatchActiveFilters(PreviewItemViewModel row) => CreateRowFilter()(row);

    /// <summary>
    /// Filter of the active chips (statuses, the rows changed since the previous simulation, and the checked rows), of
    /// the games decided by a 1G1R criterion and of the search text, all read once for a whole refresh. The checked rows are read as they are at the refresh: a
    /// row unchecked while only the checked rows are shown stays shown until the next one, so it can be checked again.
    /// </summary>
    private Func<PreviewItemViewModel, bool> CreateRowFilter()
    {
        var activeFilters = _getActiveStatusFilters();
        var changedOnly = activeFilters.Contains(StatsBannerViewModel.ChangedFilterKey);
        var checkedOnly = activeFilters.Contains(StatsBannerViewModel.SelectionFilterKey);
        var statusCount = activeFilters.Count(StatsBannerViewModel.IsStatusFilterKey);
        var query = NormalizeSearchText(PreviewSearchText);
        var criterionGames = _criterionGames;
        var criterion = CriterionFilter;
        return row =>
            (statusCount == 0 || activeFilters.Contains(row.StatusValue))
            && (criterionGames is null
                || ((row.Item.Is1G1RWinner || row.Item.EliminatedBy == criterion)
                    && row.Item.Parsed.IsConform
                    && criterionGames.Contains(row.CanonicalGameKey)))
            && (!changedOnly || row.IsChangedSinceLastRun)
            && (!checkedOnly || row.IsSortChecked)
            && (string.IsNullOrEmpty(query)
                || row.SearchTitle.Contains(query, StringComparison.Ordinal)
                || row.SearchSource.Contains(query, StringComparison.Ordinal)
                || row.SearchCategory.Contains(query, StringComparison.Ordinal));
    }

    public void RebuildColumnHeaders()
    {
        string[] headerKeys =
        [
            "panel4.table_header_source",
            "panel4.table_header_crc",
            "panel4.table_header_title",
            "panel4.table_header_disk",
            "panel4.table_header_publisher",
            "panel4.table_header_date",
            "panel4.table_header_languages",
            "panel4.table_header_country",
            "panel4.table_header_target",
            "panel4.table_header_status",
            "panel4.table_header_related"
        ];

        var sortHint = _localization.Tr("panel4.table_header_sort_hint");
        PreviewColumnHeaders.Clear();
        PreviewColumnTooltips.Clear();

        for (var i = 0; i < headerKeys.Length; i++)
        {
            var headerText = _localization.Tr(headerKeys[i]);
            Columns[i].Header = headerText;
            PreviewColumnHeaders.Add(headerText);
            PreviewColumnTooltips.Add(SortablePreviewColumns.Contains(i) ? $"{headerText} ({sortHint})" : string.Empty);
        }
    }

    public void RefreshTranslations()
    {
        RebuildColumnHeaders();
        OnPropertyChanged(nameof(ClearAll1G1ROverridesMenuText));
        OnPropertyChanged(nameof(IsolateGameMenuText));
        OnPropertyChanged(nameof(CriterionFilterText));

        foreach (var row in AllPreviewItems)
        {
            row.RefreshFromItem();
        }

        if (CurrentSortColumn.HasValue)
        {
            ApplyPreviewSorting();
        }

        RefreshFilteredPreviewItems();
    }

    public bool IsPreviewRowHidden(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= AllPreviewItems.Count)
        {
            return true;
        }

        return !DoesRowMatchActiveFilters(AllPreviewItems[rowIndex]);
    }

    public string GetRowStatus(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= AllPreviewItems.Count)
        {
            return string.Empty;
        }

        return AllPreviewItems[rowIndex].StatusValue;
    }

    /// <summary>
    /// Suggestions of the search bar: the known titles, read when the user types. The search box is not bound to
    /// <see cref="PreviewKnownTitles"/>, since it would rebuild its item list on each title added during a run.
    /// </summary>
    public Task<IEnumerable<object>> PopulateTitleSuggestionsAsync(string? text, CancellationToken cancellationToken) =>
        Task.FromResult<IEnumerable<object>>(PreviewKnownTitles.ToList());

    public static string NormalizeSearchText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private void InsertKnownTitleSorted(string title)
    {
        // Binary search of the first title that is not before the new one
        int low = 0, high = PreviewKnownTitles.Count;
        while (low < high)
        {
            var mid = (low + high) >>> 1;
            if (string.Compare(PreviewKnownTitles[mid], title, StringComparison.OrdinalIgnoreCase) < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        PreviewKnownTitles.Insert(low, title);
    }
}
