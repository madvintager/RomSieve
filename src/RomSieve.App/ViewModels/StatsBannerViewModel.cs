using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Clickable chip in the statistics banner (<c>Total</c>, <c>Kept</c>, <c>Excluded</c>, <c>Non-conforming</c>, <c>Duplicates</c>).
/// </summary>
public sealed partial class StatFilterChipViewModel : ViewModelBase
{
    private readonly Action<string> _onToggle;

    public StatFilterChipViewModel(
        string key,
        string text,
        string tooltip,
        bool isChecked,
        Action<string> onToggle)
    {
        Key = key;
        _text = text;
        _tooltip = tooltip;
        _isChecked = isChecked;
        _onToggle = onToggle;
    }

    public string Key { get; }

    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private string _tooltip;

    [ObservableProperty]
    private bool _isChecked;

    [RelayCommand]
    public void Click() => _onToggle(Key);

    [RelayCommand]
    private void Toggle() => _onToggle(Key);
}

/// <summary>
/// ViewModel for the post-simulation / post-sort statistics banner with interactive,
/// combinable status filter chips.
/// </summary>
public sealed partial class StatsBannerViewModel : ViewModelBase
{
    private readonly ILocalizationService _localization;
    private readonly HashSet<string> _activeFilters = new(StringComparer.OrdinalIgnoreCase);
    private ProcessStats _lastStats = new();
    private TransferMode _lastTransferMode = TransferMode.Copy;
    private bool _preserveArchives;
    private IReadOnlyList<OversizedOutputFile> _oversizedFiles = [];

    /// <summary>
    /// Files that a sort of the checked games writes (<see cref="ShowSelection"/>), or <see langword="null"/> while
    /// every game is checked: the volume written and the FAT32 warning are then those of the whole run.
    /// </summary>
    private IReadOnlyCollection<ProcessItem>? _selectionItems;

    /// <summary>Number of files named in the tooltip of the FAT32 warning; the others are only counted.</summary>
    public const int MaxListedOversizedFiles = 10;

    public StatsBannerViewModel(ILocalizationService localization)
    {
        _localization = localization;

        TotalChip = new StatFilterChipViewModel(
            "total",
            _localization.Tr("stats.total", ("count", 0)),
            _localization.Tr("stats.tooltip_filter_total"),
            isChecked: true,
            OnChipToggled);

        ValidChip = new StatFilterChipViewModel(
            "valid",
            _localization.Tr("stats.valid", ("count", 0)),
            _localization.Tr("stats.tooltip_filter_status"),
            isChecked: false,
            OnChipToggled);

        ExcludedChip = new StatFilterChipViewModel(
            "excluded",
            _localization.Tr("stats.excluded", ("count", 0)),
            _localization.Tr("stats.tooltip_filter_status"),
            isChecked: false,
            OnChipToggled);

        UnmatchedChip = new StatFilterChipViewModel(
            "unmatched",
            _localization.Tr("stats.unmatched", ("count", 0)),
            _localization.Tr("stats.tooltip_filter_status"),
            isChecked: false,
            OnChipToggled);

        DuplicateChip = new StatFilterChipViewModel(
            "duplicate",
            _localization.Tr("stats.duplicates", ("count", 0)),
            _localization.Tr("stats.tooltip_filter_status"),
            isChecked: false,
            OnChipToggled);

        ChangedChip = new StatFilterChipViewModel(
            ChangedFilterKey,
            _localization.Tr("stats.changed", ("count", 0)),
            _localization.Tr("stats.tooltip_filter_changed"),
            isChecked: false,
            OnChipToggled);

        SelectionChip = new StatFilterChipViewModel(
            SelectionFilterKey,
            _localization.Tr("stats.selection", ("count", 0)),
            _localization.Tr("stats.tooltip_filter_selection"),
            isChecked: false,
            OnChipToggled);

        _volumeText = FormatVolume(0);
        _volumeTooltip = _localization.Tr("panel4.tooltip_volume");
        _datStatsTooltip = _localization.Tr("panel4.tooltip_dat_stats");
    }

    /// <summary>
    /// Key of the chip showing only the rows changed since the previous simulation. It combines with the status
    /// chips (rows changed and of one of the selected statuses) and is not a status itself.
    /// </summary>
    public const string ChangedFilterKey = "changed";

    /// <summary>
    /// Key of the chip showing only the rows a partial sort of the checked games writes. Like
    /// <see cref="ChangedFilterKey"/>, it combines with the status chips and is not a status itself.
    /// </summary>
    public const string SelectionFilterKey = "selection";

    /// <summary>
    /// Event triggered when the active set of status filters changes.
    /// An empty set means "show all statuses" (Total active).
    /// </summary>
    public event EventHandler<IReadOnlySet<string>>? FilterChanged;

    public StatFilterChipViewModel TotalChip { get; }
    public StatFilterChipViewModel ValidChip { get; }
    public StatFilterChipViewModel ExcludedChip { get; }
    public StatFilterChipViewModel UnmatchedChip { get; }
    public StatFilterChipViewModel DuplicateChip { get; }

    /// <summary>Chip of the rows changed since the previous simulation, visible after a differential simulation.</summary>
    public StatFilterChipViewModel ChangedChip { get; }

    private int? _changedRowCount;

    /// <summary>Whether the last simulation was compared with the previous one, which shows <see cref="ChangedChip"/>.</summary>
    [ObservableProperty]
    private bool _isChangedChipVisible;

    /// <summary>
    /// Shows the number of rows changed since the previous simulation, or hides the chip (and its filter)
    /// when <paramref name="count"/> is <see langword="null"/>.
    /// </summary>
    public void SetChangedRowCount(int? count)
    {
        _changedRowCount = count;
        IsChangedChipVisible = count.HasValue;
        if (!count.HasValue && _activeFilters.Remove(ChangedFilterKey))
        {
            SyncChipCheckedStates();
            FilterChanged?.Invoke(this, new HashSet<string>(_activeFilters, StringComparer.OrdinalIgnoreCase));
        }

        RefreshLabelsFromLastStats();
    }

    /// <summary>Chip of the checked games, visible while some games the sort writes are unchecked.</summary>
    public StatFilterChipViewModel SelectionChip { get; }

    private int? _selectedUnitCount;

    /// <summary>Whether some games are unchecked in the table, which shows <see cref="SelectionChip"/>.</summary>
    [ObservableProperty]
    private bool _isSelectionChipVisible;

    /// <summary>
    /// Shows the number of checked games (and non-conforming files) of a partial sort, or hides the chip (and its
    /// filter) when <paramref name="count"/> is <see langword="null"/>: every game is then checked.
    /// </summary>
    public void SetSelectedUnitCount(int? count)
    {
        if (count == _selectedUnitCount)
        {
            return;
        }

        _selectedUnitCount = count;
        IsSelectionChipVisible = count.HasValue;
        SelectionChip.Text = _localization.Tr("stats.selection", ("count", count ?? 0));
        if (!count.HasValue && _activeFilters.Remove(SelectionFilterKey))
        {
            SyncChipCheckedStates();
            FilterChanged?.Invoke(this, new HashSet<string>(_activeFilters, StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>Whether a filter key is a status (and not the chip of the changed rows or of the selection).</summary>
    public static bool IsStatusFilterKey(string key) =>
        !string.Equals(key, ChangedFilterKey, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(key, SelectionFilterKey, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<StatFilterChipViewModel> StatusChips =>
        [ValidChip, ExcludedChip, UnmatchedChip, DuplicateChip];

    /// <summary>Set of filtered statuses (<c>"valid"</c>, <c>"excluded"</c>, <c>"unmatched"</c>, <c>"duplicate"</c>).</summary>
    public IReadOnlySet<string> ActiveFilters => _activeFilters;

    [ObservableProperty]
    private string _volumeText;

    [ObservableProperty]
    private string _volumeTooltip;

    /// <summary>Warning shown when files written by the sort are too large for FAT32 (<see cref="Fat32Limit"/>).</summary>
    [ObservableProperty]
    private bool _isFat32WarningVisible;

    [ObservableProperty]
    private string _fat32WarningText = string.Empty;

    [ObservableProperty]
    private string _fat32WarningTooltip = string.Empty;

    /// <summary>
    /// Kept files that were already in the destination before the sort, and are not written again
    /// (<see cref="ProcessStats.AlreadyInDestinationCount"/>): shown apart, since they are counted as duplicates.
    /// </summary>
    [ObservableProperty]
    private bool _isAlreadyInDestinationVisible;

    [ObservableProperty]
    private string _alreadyInDestinationText = string.Empty;

    [ObservableProperty]
    private string _alreadyInDestinationTooltip = string.Empty;

    [ObservableProperty]
    private string _datStatsText = string.Empty;

    /// <summary>Tooltip of the DAT completion; for a lot, what was found of each DAT with a game found, and how many have none.</summary>
    [ObservableProperty]
    private string _datStatsTooltip = string.Empty;

    /// <summary>DATs of a lot named in the tooltip of the completion; the others are only counted.</summary>
    internal const int TooltipDatCount = 15;

    [ObservableProperty]
    private bool _isDatStatsVisible;

    /// <summary>
    /// Toggles a filter chip by key (<c>"total"</c>, <c>"valid"</c>, <c>"excluded"</c>, <c>"unmatched"</c>, <c>"duplicate"</c>).
    /// </summary>
    [RelayCommand]
    public void ToggleChip(string key) => OnChipToggled(key);

    /// <summary>
    /// Resets only the active status filters without resetting counters to zero.
    /// </summary>
    [RelayCommand]
    public void ResetFilters()
    {
        _activeFilters.Clear();
        SyncChipCheckedStates();
        FilterChanged?.Invoke(this, new HashSet<string>(_activeFilters, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Updates counters, volume and DAT completion rate from a <see cref="ProcessStats"/>. The transfer mode is the
    /// one of the run: the volume written by a ZIP sort is only an upper bound, and links take almost no space.
    /// </summary>
    public void UpdateStats(
        ProcessStats stats,
        TransferMode transferMode = TransferMode.Copy,
        IReadOnlyList<OversizedOutputFile>? oversizedFiles = null,
        bool preserveArchives = false)
    {
        _lastStats = stats ?? new ProcessStats();
        _lastTransferMode = transferMode;
        _preserveArchives = preserveArchives;
        _oversizedFiles = oversizedFiles ?? [];
        RefreshLabelsFromLastStats();
    }

    /// <summary>
    /// Volume written and FAT32 warning of a partial sort: <paramref name="writtenItems"/> are the files that the
    /// sort of the checked games writes (maybe none), <see langword="null"/> to show those of the whole run again.
    /// </summary>
    public void ShowSelection(IReadOnlyCollection<ProcessItem>? writtenItems)
    {
        if (writtenItems is null && _selectionItems is null)
        {
            return;
        }

        _selectionItems = writtenItems;
        RefreshVolume();
        RefreshFat32Warning();
    }

    /// <summary>
    /// Resets all counters and filters to zero before a new run.
    /// </summary>
    public void Reset()
    {
        _lastStats = new ProcessStats();
        _lastTransferMode = TransferMode.Copy;
        _preserveArchives = false;
        _oversizedFiles = [];
        _selectionItems = null;
        _activeFilters.Clear();
        _changedRowCount = null;
        IsChangedChipVisible = false;
        _selectedUnitCount = null;
        IsSelectionChipVisible = false;
        SyncChipCheckedStates();
        RefreshLabelsFromLastStats();
    }

    /// <summary>
    /// Updates translated chip labels when the UI language changes.
    /// </summary>
    public void RefreshTranslations()
    {
        TotalChip.Tooltip = _localization.Tr("stats.tooltip_filter_total");
        ValidChip.Tooltip = _localization.Tr("stats.tooltip_filter_status");
        ExcludedChip.Tooltip = _localization.Tr("stats.tooltip_filter_status");
        UnmatchedChip.Tooltip = _localization.Tr("stats.tooltip_filter_status");
        DuplicateChip.Tooltip = _localization.Tr("stats.tooltip_filter_status");
        ChangedChip.Tooltip = _localization.Tr("stats.tooltip_filter_changed");
        SelectionChip.Tooltip = _localization.Tr("stats.tooltip_filter_selection");
        RefreshLabelsFromLastStats();
    }

    /// <summary>Label of the banner with only the total volume of the scanned files (before any run).</summary>
    public string FormatVolume(long sizeBytes) => _localization.Tr("stats.volume", ("total", FormatSize(sizeBytes)));

    /// <summary>Size in binary units, in the units of the current language (<see cref="SizeFormatting.Format"/>).</summary>
    public string FormatSize(long sizeBytes) => SizeFormatting.Format(_localization, sizeBytes);

    /// <summary>
    /// Volume label and tooltip: before a run, the total volume only; after it, the total volume of the scanned files
    /// and what the sort writes to the destination (<see cref="ProcessStats.OutputBytes"/>), to know whether the
    /// sorted collection fits on a given medium.
    /// </summary>
    private void RefreshVolume()
    {
        if (_lastStats.TotalScanned == 0)
        {
            VolumeText = FormatVolume(_lastStats.TotalBytes);
            VolumeTooltip = _localization.Tr("panel4.tooltip_volume");
            return;
        }

        var isZip = _lastTransferMode == TransferMode.Zip;
        var isSelection = _selectionItems is not null;
        var output = _selectionItems?.Sum(it => it.FileSize + it.RelatedItems.Sum(r => r.FileSize)) ?? _lastStats.OutputBytes;
        var key = (isSelection, isZip) switch
        {
            (true, true) => "stats.volume_output_selection_max",
            (true, false) => "stats.volume_output_selection",
            (false, true) => "stats.volume_output_max",
            _ => "stats.volume_output"
        };
        VolumeText = _localization.Tr(
            key,
            ("total", FormatSize(_lastStats.TotalBytes)),
            ("output", FormatSize(output)));

        var tooltip = _localization.Tr(
            isSelection ? "stats.tooltip_volume_selection" : "stats.tooltip_volume_output",
            ("bytes", FormatExactBytes(output)));
        var modeNote = _lastTransferMode switch
        {
            TransferMode.Zip => _localization.Tr("stats.tooltip_volume_zip"),
            TransferMode.HardLink or TransferMode.SymbolicLink => _localization.Tr("stats.tooltip_volume_links"),
            _ => null
        };
        VolumeTooltip = modeNote is null ? tooltip : tooltip + Environment.NewLine + modeNote;
    }

    /// <summary>
    /// FAT32 warning: number of written files of 4 GB or more, and in the tooltip the limit, the largest files and,
    /// in ZIP mode, the reminder that the archives are measured before compression.
    /// </summary>
    private void RefreshFat32Warning()
    {
        var oversizedFiles = _selectionItems is null
            ? _oversizedFiles
            : Fat32Limit.FindOversizedOutputFiles(_selectionItems, _lastTransferMode, _preserveArchives);
        var count = oversizedFiles.Count;
        IsFat32WarningVisible = count > 0;
        if (count == 0)
        {
            Fat32WarningText = string.Empty;
            Fat32WarningTooltip = string.Empty;
            return;
        }

        Fat32WarningText = count == 1
            ? _localization.Tr("stats.fat32_single")
            : _localization.Tr("stats.fat32_count", ("count", count));

        var lines = new List<string> { _localization.Tr("stats.tooltip_fat32") };
        if (oversizedFiles.Any(f => f.IsUpperBound))
        {
            lines.Add(_localization.Tr("stats.tooltip_fat32_zip"));
        }

        lines.AddRange(oversizedFiles
            .Take(MaxListedOversizedFiles)
            .Select(f => _localization.Tr("stats.tooltip_fat32_file", ("name", f.Name), ("size", FormatSize(f.Size)))));
        if (count > MaxListedOversizedFiles)
        {
            lines.Add(_localization.Tr("stats.tooltip_fat32_more", ("count", count - MaxListedOversizedFiles)));
        }

        Fat32WarningTooltip = string.Join(Environment.NewLine, lines);
    }

    /// <summary>Exact byte count with the digit grouping of the interface language.</summary>
    private string FormatExactBytes(long bytes)
    {
        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(_localization.CurrentLocale);
        }
        catch (CultureNotFoundException)
        {
            culture = CultureInfo.InvariantCulture;
        }

        return bytes.ToString("N0", culture);
    }

    private void OnChipToggled(string key)
    {
        if (string.Equals(key, "total", StringComparison.OrdinalIgnoreCase))
        {
            _activeFilters.Clear();
        }
        else
        {
            if (!_activeFilters.Remove(key))
            {
                _activeFilters.Add(key.ToLowerInvariant());
            }

            // Selecting every status is the same as selecting none (the "changed" and "selection" chips are not statuses)
            var statusCount = _activeFilters.Count(IsStatusFilterKey);
            if (statusCount == StatusChips.Count)
            {
                _activeFilters.RemoveWhere(IsStatusFilterKey);
            }
        }

        SyncChipCheckedStates();
        FilterChanged?.Invoke(this, new HashSet<string>(_activeFilters, StringComparer.OrdinalIgnoreCase));
    }

    private void SyncChipCheckedStates()
    {
        TotalChip.IsChecked = _activeFilters.Count == 0;
        ValidChip.IsChecked = _activeFilters.Contains("valid");
        ExcludedChip.IsChecked = _activeFilters.Contains("excluded");
        UnmatchedChip.IsChecked = _activeFilters.Contains("unmatched");
        DuplicateChip.IsChecked = _activeFilters.Contains("duplicate");
        ChangedChip.IsChecked = _activeFilters.Contains(ChangedFilterKey);
        SelectionChip.IsChecked = _activeFilters.Contains(SelectionFilterKey);
    }

    private void RefreshLabelsFromLastStats()
    {
        TotalChip.Text = _localization.Tr("stats.total", ("count", _lastStats.TotalScanned));
        ValidChip.Text = _localization.Tr("stats.valid", ("count", _lastStats.ValidCount));
        ExcludedChip.Text = _localization.Tr("stats.excluded", ("count", _lastStats.ExcludedCount));
        UnmatchedChip.Text = _localization.Tr("stats.unmatched", ("count", _lastStats.UnmatchedCount));
        DuplicateChip.Text = _localization.Tr("stats.duplicates", ("count", _lastStats.DuplicateCount));
        ChangedChip.Text = _localization.Tr("stats.changed", ("count", _changedRowCount ?? 0));
        SelectionChip.Text = _localization.Tr("stats.selection", ("count", _selectedUnitCount ?? 0));
        RefreshVolume();
        RefreshFat32Warning();

        var alreadyInDestination = _lastStats.AlreadyInDestinationCount;
        IsAlreadyInDestinationVisible = alreadyInDestination > 0;
        AlreadyInDestinationText = alreadyInDestination > 0
            ? _localization.Tr("stats.already_in_destination", ("count", alreadyInDestination))
            : string.Empty;
        AlreadyInDestinationTooltip = alreadyInDestination > 0 ? _localization.Tr("stats.tooltip_already_in_destination") : string.Empty;

        if (_lastStats.DatTotalRoms > 0)
        {
            // Games designated by the name of a CHD are present but not verified: they are shown apart
            var pct = _lastStats.DatCompletionPct.ToString("F1", CultureInfo.InvariantCulture);
            DatStatsText = _lastStats.DatNameMatchedCount > 0
                ? _localization.Tr(
                    "stats.dat_completion_with_names",
                    ("pct", pct),
                    ("matched", _lastStats.DatMatchedCount + _lastStats.DatNameMatchedCount),
                    ("total", _lastStats.DatTotalRoms),
                    ("names", _lastStats.DatNameMatchedCount))
                : _localization.Tr(
                    "stats.dat_completion",
                    ("pct", pct),
                    ("matched", _lastStats.DatMatchedCount),
                    ("total", _lastStats.DatTotalRoms));

            IsDatStatsVisible = true;
            DatStatsTooltip = DescribeDats(_lastStats.PerDat);
        }
        else
        {
            DatStatsText = string.Empty;
            DatStatsTooltip = DescribeDats(null);
            IsDatStatsVisible = false;
        }
    }

    // What the completion means, then, for a lot, a line per DAT with a game found and the number of the others
    private string DescribeDats(IReadOnlyList<DatLotStats>? perDat)
    {
        var tooltip = _localization.Tr("panel4.tooltip_dat_stats");
        if (perDat is null)
        {
            return tooltip;
        }

        var lines = new List<string> { tooltip, string.Empty };
        var found = perDat.Where(d => d.GamesFound > 0).ToList();
        lines.AddRange(found.Take(TooltipDatCount).Select(d => _localization.Tr(
            "stats.tooltip_dat_lot_line",
            ("dat", d.Name),
            ("pct", d.CompletionPct.ToString("F1", CultureInfo.InvariantCulture)),
            ("matched", d.MatchedCount + d.NameMatchedCount),
            ("total", d.TotalRoms),
            ("games", d.GamesFound))));
        if (found.Count > TooltipDatCount)
        {
            lines.Add(_localization.Tr("stats.tooltip_dat_lot_more", ("count", found.Count - TooltipDatCount)));
        }

        if (perDat.Count(d => d.GamesFound == 0) is var nothing and > 0)
        {
            lines.Add(_localization.Tr("stats.tooltip_dat_lot_nothing", ("count", nothing)));
        }

        return string.Join(Environment.NewLine, lines);
    }
}
