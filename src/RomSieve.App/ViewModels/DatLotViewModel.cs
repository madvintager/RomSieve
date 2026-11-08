using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.App.ViewModels;

/// <summary>
/// A DAT of a lot in the list to check of the window of the lot: its name, its convention, its path in the folder of DATs
/// (its subfolder shown, its file name in the tooltip), and whether the sort uses it.
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
        var slash = RelativePath.LastIndexOf('/');
        Folder = slash < 0 ? string.Empty : RelativePath[..slash];
        FolderLabel = slash < 0 ? string.Empty : Folder + "/";
        SearchName = SearchQuery.Normalize(Name);
        SearchPath = SearchQuery.Normalize(RelativePath);
        _isIncluded = isIncluded;
        _changed = changed;
    }

    public string Name { get; }

    /// <summary>Path of the DAT in the folder of DATs, with <c>/</c> for separator.</summary>
    public string RelativePath { get; }

    /// <summary>
    /// Subfolder of the DAT in the folder of DATs, with <c>/</c> for separator; empty at its root, where the file name
    /// would only repeat the name of the DAT.
    /// </summary>
    public string Folder { get; }

    /// <summary>
    /// <see cref="Folder"/> as the line shows it, ending with <c>/</c>: a folder named after a convention (<c>TOSEC</c> in
    /// the TOSEC pack) read as the convention shown again.
    /// </summary>
    public string FolderLabel { get; }

    public NamingConvention Convention { get; }

    /// <summary>Name normalized for the search, once.</summary>
    internal string SearchName { get; }

    /// <summary>Path normalized for the search, once.</summary>
    internal string SearchPath { get; }

    /// <summary>Tone of the convention, as the convention badge names it (<c>Badge*ForegroundBrush</c>).</summary>
    public string ConventionTone => DatLotViewModel.ToneOf(Convention);

    [ObservableProperty]
    private string _conventionLabel;

    /// <summary>Whether the sort uses the DAT (unchecked: in <c>excluded_dats</c>).</summary>
    [ObservableProperty]
    private bool _isIncluded;

    partial void OnIsIncludedChanged(bool value) => _changed(this);
}

/// <summary>Which DATs of a lot its window shows, besides the search.</summary>
public enum DatLotShown
{
    All,
    Checked,
    Unchecked
}

/// <summary>
/// The DATs of a lot (a folder of No-Intro, Redump or TOSEC DATs), read from their headers alone
/// (<see cref="IDatService.ReadDatFolderPreview"/>), as a list to check with a search, shown in a window of its own
/// (<c>DatLotWindow</c>): the DATs unchecked are those <c>excluded_dats</c> names. Tells the summary of the lot (number of
/// DATs, conventions, DATs checked) and the convention most of the DATs checked follow.
/// </summary>
public sealed partial class DatLotViewModel : ViewModelBase
{
    private readonly ILocalizationService _localization;
    private List<DatLotEntryViewModel> _entries = [];
    private bool _applying;

    public DatLotViewModel(ILocalizationService localization)
    {
        _localization = localization;
        I18n = new UiStringsIndexer(localization);
        ShownOptions = new LocalizedOptionList<DatLotShown>(
            localization,
            (DatLotShown.All, "tab1.dat_lot_shown_all"),
            (DatLotShown.Checked, "tab1.dat_lot_shown_checked"),
            (DatLotShown.Unchecked, "tab1.dat_lot_shown_unchecked"));
    }

    /// <summary>Choices of the DATs shown: all of them, those checked, those unchecked.</summary>
    public LocalizedOptionList<DatLotShown> ShownOptions { get; }

    /// <summary>
    /// DATs shown besides the search: those checked or unchecked when the list is refiltered, so that a box changed keeps
    /// its line until the next change of the search or of this choice, to be changed back.
    /// </summary>
    [ObservableProperty]
    private DatLotShown _shown = DatLotShown.All;

    /// <summary>Texts of the window of the lot, retranslated at a change of language.</summary>
    public UiStringsIndexer I18n { get; }

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

    partial void OnSearchTextChanged(string value) => OnFilterChanged();

    partial void OnShownChanged(DatLotShown value) => OnFilterChanged();

    private void OnFilterChanged()
    {
        Refilter();
        OnPropertyChanged(nameof(CheckShownLabel));
        OnPropertyChanged(nameof(UncheckShownLabel));
    }

    // Without a search nor a choice of the DATs shown, the buttons act on every DAT, and say so
    private bool IsFiltered => Shown != DatLotShown.All || !SearchQuery.Parse(SearchText).IsEmpty;

    /// <summary>Label of the button that checks the DATs shown: every DAT when none is hidden.</summary>
    public string CheckShownLabel => _localization.Tr(IsFiltered ? "tab1.dat_lot_check_shown" : "tab1.dat_lot_check_all");

    /// <summary>Label of the button that unchecks the DATs shown: every DAT when none is hidden.</summary>
    public string UncheckShownLabel => _localization.Tr(IsFiltered ? "tab1.dat_lot_uncheck_shown" : "tab1.dat_lot_uncheck_all");

    /// <summary>Opens the list on every DAT: no search, every DAT shown.</summary>
    public void ShowEverything()
    {
        SearchText = string.Empty;
        Shown = DatLotShown.All;
    }

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

    /// <summary>Empties the search: every DAT shown again.</summary>
    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

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
        var query = SearchQuery.Parse(SearchText);
        var shown = Shown;
        FilteredEntries.ReplaceAll(query.IsEmpty && shown == DatLotShown.All
            ? _entries
            : _entries.Where(e =>
                (shown == DatLotShown.All || e.IsIncluded == (shown == DatLotShown.Checked))
                && (query.IsEmpty || query.Matches(e.SearchName, e.SearchPath, SearchQuery.Normalize(e.ConventionLabel)))));
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
        ShownOptions.RefreshTranslations();
        I18n.NotifyUpdated();
        OnPropertyChanged(nameof(CheckShownLabel));
        OnPropertyChanged(nameof(UncheckShownLabel));
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
