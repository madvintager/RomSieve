using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.App.ViewModels;

/// <summary>
/// A DAT of a lot in the list to check of the collection step: its name, its convention, its path in the folder of DATs,
/// and whether the sort uses it.
/// </summary>
public sealed partial class DatLotEntryViewModel : ObservableObject
{
    private readonly Action<DatLotEntryViewModel> _changed;

    public DatLotEntryViewModel(DatFolderEntry entry, string conventionLabel, bool isIncluded, Action<DatLotEntryViewModel> changed)
    {
        Name = entry.Name;
        RelativePath = entry.RelativePath.Replace('\\', '/');
        Convention = entry.Convention;
        _conventionLabel = conventionLabel;
        _isIncluded = isIncluded;
        _changed = changed;
    }

    public string Name { get; }

    /// <summary>Path of the DAT in the folder of DATs, with <c>/</c> for separator.</summary>
    public string RelativePath { get; }

    public NamingConvention Convention { get; }

    /// <summary>Tone of the convention, as the convention badge names it (<c>Badge*ForegroundBrush</c>).</summary>
    public string ConventionTone => DatLotViewModel.ToneOf(Convention);

    [ObservableProperty]
    private string _conventionLabel;

    /// <summary>Whether the sort uses the DAT (unchecked: in <c>excluded_dats</c>).</summary>
    [ObservableProperty]
    private bool _isIncluded;

    partial void OnIsIncludedChanged(bool value) => _changed(this);
}

/// <summary>
/// The DATs of a lot (a folder of No-Intro, Redump or TOSEC DATs), read from their headers alone
/// (<see cref="IDatService.ReadDatFolderPreview"/>), as a list to check with a search: the DATs unchecked are those
/// <c>excluded_dats</c> names. Tells the summary of the lot (number of DATs, conventions, DATs checked) and the convention
/// most of the DATs checked follow.
/// </summary>
public sealed partial class DatLotViewModel : ViewModelBase
{
    private readonly ILocalizationService _localization;
    private List<DatLotEntryViewModel> _entries = [];
    private bool _applying;

    public DatLotViewModel(ILocalizationService localization)
    {
        _localization = localization;
    }

    /// <summary>Raised when the user checks or unchecks DATs: <see cref="ExcludedNames"/> tells which are left out.</summary>
    public event EventHandler? ExcludedChanged;

    /// <summary>Every DAT of the lot, in the order of their paths.</summary>
    public IReadOnlyList<DatLotEntryViewModel> Entries => _entries;

    /// <summary>The DATs whose name, path or convention holds the search.</summary>
    public BulkObservableCollection<DatLotEntryViewModel> FilteredEntries { get; } = [];

    /// <summary>Whether the DAT chosen is a lot: the list is only shown for one.</summary>
    [ObservableProperty]
    private bool _isLot;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>DATs checked out of all of them.</summary>
    [ObservableProperty]
    private string _countText = string.Empty;

    partial void OnSearchTextChanged(string value) => Refilter();

    /// <summary>Names of the DATs unchecked, in the order of the lot.</summary>
    public IReadOnlyList<string> ExcludedNames => _entries.Where(e => !e.IsIncluded).Select(e => e.Name).ToList();

    public int IncludedCount => _entries.Count(e => e.IsIncluded);

    /// <summary>Shows the DATs of a lot, those named by <paramref name="excluded"/> unchecked (case ignored).</summary>
    public void Show(IReadOnlyList<DatFolderEntry> entries, IEnumerable<string> excluded)
    {
        var left = new HashSet<string>(excluded, StringComparer.OrdinalIgnoreCase);
        _entries = entries
            .Select(entry => new DatLotEntryViewModel(entry, ConventionLabel(entry.Convention), !left.Contains(entry.Name), OnEntryChanged))
            .ToList();
        IsLot = true;
        Refilter();
        UpdateCount();
    }

    /// <summary>No lot: the DAT is a file, a folder of software lists, or none.</summary>
    public void Clear()
    {
        _entries = [];
        IsLot = false;
        FilteredEntries.ReplaceAll([]);
        UpdateCount();
    }

    /// <summary>Checks the DATs as <paramref name="excluded"/> says (a configuration or a profile loaded), without raising <see cref="ExcludedChanged"/>.</summary>
    public void ApplyExcluded(IEnumerable<string> excluded)
    {
        var left = new HashSet<string>(excluded, StringComparer.OrdinalIgnoreCase);
        _applying = true;
        try
        {
            foreach (var entry in _entries)
            {
                entry.IsIncluded = !left.Contains(entry.Name);
            }
        }
        finally
        {
            _applying = false;
        }

        UpdateCount();
    }

    /// <summary>Checks every DAT the search shows.</summary>
    [RelayCommand]
    private void IncludeShown() => SetShown(true);

    /// <summary>Unchecks every DAT the search shows.</summary>
    [RelayCommand]
    private void ExcludeShown() => SetShown(false);

    private void SetShown(bool included)
    {
        _applying = true;
        try
        {
            foreach (var entry in FilteredEntries)
            {
                entry.IsIncluded = included;
            }
        }
        finally
        {
            _applying = false;
        }

        UpdateCount();
        ExcludedChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnEntryChanged(DatLotEntryViewModel entry)
    {
        if (_applying)
        {
            return;
        }

        UpdateCount();
        ExcludedChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Refilter()
    {
        var search = SearchText.Trim();
        FilteredEntries.ReplaceAll(search.Length == 0
            ? _entries
            : _entries.Where(e =>
                e.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.RelativePath.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.ConventionLabel.Contains(search, StringComparison.OrdinalIgnoreCase)));
    }

    private void UpdateCount() =>
        CountText = IsLot ? _localization.Tr("tab1.dat_lot_count", ("checked", IncludedCount), ("count", _entries.Count)) : string.Empty;

    /// <summary>
    /// Convention most of the DATs checked follow (the first of the lot on a tie), which the filters and the pattern take
    /// as the convention of the lot; <see cref="NamingConvention.Auto"/> without a DAT checked.
    /// </summary>
    public NamingConvention MainConvention =>
        _entries.Where(e => e.IsIncluded)
            .GroupBy(e => e.Convention)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault(NamingConvention.Auto);

    /// <summary>Whether a DAT checked follows <paramref name="convention"/>.</summary>
    public bool Includes(NamingConvention convention) => _entries.Any(e => e.IsIncluded && e.Convention == convention);

    /// <summary>
    /// Summary of the lot: its DATs, how many follow each convention, how many are checked. Rebuilt at each change of
    /// language, as the names of the conventions are.
    /// </summary>
    public string Summary()
    {
        var conventions = string.Join(", ", _entries
            .GroupBy(e => e.Convention)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {ConventionLabel(g.Key)}"));
        return _localization.Tr(
            "tab1.dat_info_lot",
            ("count", _entries.Count),
            ("conventions", conventions),
            ("checked", IncludedCount));
    }

    public void RefreshTranslations()
    {
        foreach (var entry in _entries)
        {
            entry.ConventionLabel = ConventionLabel(entry.Convention);
        }

        UpdateCount();
    }

    private string ConventionLabel(NamingConvention convention) => _localization.Tr(convention switch
    {
        NamingConvention.Tosec => "tab1.badge_tosec",
        NamingConvention.NoIntro => "tab1.badge_nointro",
        NamingConvention.Redump => "tab1.badge_redump",
        _ => "tab1.dat_lot_convention_unknown"
    });

    /// <summary>Tone of a convention, as the convention badge names it.</summary>
    internal static string ToneOf(NamingConvention convention) => convention switch
    {
        NamingConvention.Tosec => "Tosec",
        NamingConvention.NoIntro => "NoIntro",
        NamingConvention.Redump => "Redump",
        _ => "Auto"
    };
}
