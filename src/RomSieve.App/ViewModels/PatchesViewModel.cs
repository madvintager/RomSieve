using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RomSieve.App.Abstractions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.App.ViewModels;

/// <summary>An identified file a patch may be meant for, in the list of the origins of a row.</summary>
public sealed class PatchOriginOptionViewModel(ProcessItem origin)
{
    public ProcessItem Origin { get; } = origin;

    public string Label { get; } = origin.DisplayName;

    /// <summary>Where the file is: its archive, or its path.</summary>
    public string Location { get; } = !string.IsNullOrEmpty(origin.ArchivePath) ? origin.ArchivePath : origin.SourcePath;

    /// <summary>Name normalized for the search, once.</summary>
    internal string SearchName { get; } = SearchQuery.Normalize(origin.DisplayName);
}

/// <summary>A form of the data of an origin, with or without its copier header: the CRC32 to write in the name of the patch.</summary>
public sealed class PatchFormOptionViewModel(PatchOriginSuggestion form, string label)
{
    public PatchOriginSuggestion Form { get; } = form;

    public string Label { get; } = label;
}

/// <summary>Why a patch is in the window: left out by the last simulation, or applied thanks to its readme.</summary>
public enum PatchRowKind
{
    Unapplied,
    AppliedByNote
}

/// <summary>
/// A patch of the window of the patches: why it is there, the origins it may be meant for and the form of the chosen one,
/// whether it is to be renamed, and what became of it.
/// </summary>
public sealed partial class PatchRowViewModel : ObservableObject
{
    private readonly PatchesViewModel _owner;

    // Forms already known for an origin: the two a readme gave, or those read once
    private readonly Dictionary<ProcessItem, IReadOnlyList<PatchOriginSuggestion>> _knownForms = new(ReferenceEqualityComparer.Instance);

    internal PatchRowViewModel(
        PatchesViewModel owner,
        PatchFile patch,
        PatchRowKind kind,
        string status,
        bool canChoose,
        string location)
    {
        _owner = owner;
        Patch = patch;
        Kind = kind;
        StatusText = status;
        CanChoose = canChoose;
        Location = location;
    }

    public PatchFile Patch { get; }

    public string Name => Patch.DisplayName;

    /// <summary>Its folder in the patch folder, or its archive.</summary>
    public string Location { get; }

    public PatchRowKind Kind { get; }

    /// <summary>The reason it was not applied, or the readme it was applied thanks to.</summary>
    public string StatusText { get; }

    /// <summary>Whether the user may choose its origin: an IPS whose name gives no CRC32.</summary>
    public bool CanChoose { get; }

    /// <summary>Whether it lies in an archive, which is never modified: its CRC32 is only shown.</summary>
    public bool IsInArchive => Patch.ArchiveMember is not null;

    /// <summary>Origins proposed (the suggestions of the simulation), then those the user took from the search.</summary>
    public ObservableCollection<PatchOriginOptionViewModel> Origins { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheck), nameof(NewNameText), nameof(CheckTooltip))]
    private PatchOriginOptionViewModel? _selectedOrigin;

    /// <summary>The forms of the origin chosen: one, or two with and without its copier header.</summary>
    public ObservableCollection<PatchFormOptionViewModel> Forms { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheck), nameof(NewNameText), nameof(CheckTooltip))]
    private PatchFormOptionViewModel? _selectedForm;

    /// <summary>Whether the origin chosen has two forms, to choose from.</summary>
    public bool HasSeveralForms => Forms.Count > 1;

    /// <summary>Whether the patch is to be renamed.</summary>
    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheck), nameof(CheckTooltip))]
    private bool _isRenamed;

    /// <summary>What became of the last renaming: its new name, or why it was left as it was.</summary>
    [ObservableProperty]
    private string _resultText = string.Empty;

    /// <summary>The forms of the origin are being read (the other form reads the file again).</summary>
    internal Task FormsLoading { get; private set; } = Task.CompletedTask;

    /// <summary>Whether the patch can be checked: on the disk, a form of its origin chosen, not renamed yet.</summary>
    public bool CanCheck => (CanChoose || Kind == PatchRowKind.AppliedByNote) && !IsInArchive && SelectedForm is not null && !IsRenamed;

    /// <summary>The name the patch would take, or what to do for a patch in an archive; empty without a form chosen.</summary>
    public string NewNameText => SelectedForm is not { } form
        ? string.Empty
        : IsInArchive
            ? _owner.Tr("patches.in_archive", ("crc", PatchesViewModel.FormatCrc(form.Form.Crc32)))
            : _owner.Tr("patches.new_name", ("name", _owner.NewName(this, form.Form.Crc32)));

    /// <summary>Why the box is greyed out, or what it does.</summary>
    public string CheckTooltip =>
        IsRenamed ? ResultText
        : IsInArchive ? _owner.Tr("patches.tooltip_check_archive")
        : !CanChoose && Kind != PatchRowKind.AppliedByNote ? _owner.Tr("patches.tooltip_check_no_choice")
        : SelectedForm is null ? _owner.Tr("patches.tooltip_check_no_origin")
        : _owner.Tr("patches.tooltip_check");

    partial void OnIsCheckedChanged(bool value) => _owner.OnRowCheckedChanged();

    partial void OnSelectedOriginChanged(PatchOriginOptionViewModel? value)
    {
        Forms.Clear();
        SelectedForm = null;
        OnPropertyChanged(nameof(HasSeveralForms));
        if (value is not null)
        {
            FormsLoading = LoadFormsAsync(value.Origin);
        }
    }

    internal void KnowForms(ProcessItem origin, IReadOnlyList<PatchOriginSuggestion> forms) => _knownForms[origin] = forms;

    /// <summary>Adds an origin to the list (once), and chooses it.</summary>
    internal void Choose(ProcessItem origin)
    {
        var option = Origins.FirstOrDefault(o => ReferenceEquals(o.Origin, origin));
        if (option is null)
        {
            option = new PatchOriginOptionViewModel(origin);
            Origins.Add(option);
        }

        SelectedOrigin = option;
    }

    private async Task LoadFormsAsync(ProcessItem origin)
    {
        if (!_knownForms.TryGetValue(origin, out var forms))
        {
            forms = await _owner.ReadFormsAsync(origin);
            _knownForms[origin] = forms;
        }

        // The user may have chosen another origin meanwhile
        if (!ReferenceEquals(SelectedOrigin?.Origin, origin))
        {
            return;
        }

        foreach (var form in forms)
        {
            Forms.Add(new PatchFormOptionViewModel(form, _owner.FormLabel(form)));
        }

        OnPropertyChanged(nameof(HasSeveralForms));
        SelectedForm = Forms.FirstOrDefault();
    }

    internal void MarkRenamed(string newName)
    {
        IsChecked = false;
        IsRenamed = true;
        ResultText = _owner.Tr("patches.renamed", ("name", newName));
        OnPropertyChanged(nameof(CheckTooltip));
    }
}

/// <summary>
/// The window of the patches (<c>PatchesWindow</c>), opened from the banner after a simulation: the patches it left out,
/// and the IPS it applied thanks to their readme. For an IPS whose name gives no CRC32, the user chooses its origin among
/// the suggestions of the simulation or by a search among the identified files, and the form of its data; the patches
/// checked are renamed with that CRC32 (<see cref="IPatchNamingService"/>), never in an archive. A suggestion is never
/// taken alone: the user checks each patch. Closing with « Simulate again » runs a new simulation, which applies them.
/// </summary>
public sealed partial class PatchesViewModel : ViewModelBase
{
    /// <summary>Most identified files the search shows.</summary>
    public const int MaxSearchResults = 50;

    /// <summary>Reasons of the IPS whose name gives no CRC32, which a CRC32 in their name can fix.</summary>
    private static readonly HashSet<string> ChoosableReasons =
    [
        "reason.patch_no_crc",
        "reason.patch_header_form_ambiguous",
        "reason.patch_ambiguous_note",
        "reason.patch_orphan_note"
    ];

    private readonly ILocalizationService _localization;
    private readonly IDialogService _dialogService;
    private readonly IPatchNamingService _naming;
    private readonly Func<IReadOnlyList<ProcessItem>> _lastItems;
    private readonly Func<ProcessStats?> _lastStats;
    private readonly Func<SortConfig?> _lastConfig;
    private readonly Func<bool> _isRunning;
    private readonly Func<Task> _simulate;
    private readonly Action<LogLevel, LocalizedMessage> _log;

    private List<PatchOriginOptionViewModel> _searchable = [];
    private string _patchDir = string.Empty;
    private bool _detectCopierHeaders = true;

    public PatchesViewModel(
        ILocalizationService localization,
        IDialogService dialogService,
        IPatchNamingService naming,
        Func<IReadOnlyList<ProcessItem>> lastItems,
        Func<ProcessStats?> lastStats,
        Func<SortConfig?> lastConfig,
        Func<bool> isRunning,
        Func<Task> simulate,
        Action<LogLevel, LocalizedMessage> log)
    {
        _localization = localization;
        _dialogService = dialogService;
        _naming = naming;
        _lastItems = lastItems;
        _lastStats = lastStats;
        _lastConfig = lastConfig;
        _isRunning = isRunning;
        _simulate = simulate;
        _log = log;
        I18n = new UiStringsIndexer(localization);
    }

    /// <summary>Texts of the window, retranslated at a change of language.</summary>
    public UiStringsIndexer I18n { get; }

    /// <summary>The patches of the window: those left out first, then those applied thanks to their readme.</summary>
    public BulkObservableCollection<PatchRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRow), nameof(CanSearch))]
    private PatchRowViewModel? _selectedRow;

    public bool HasSelectedRow => SelectedRow is not null;

    /// <summary>Whether the search can give an origin to the selected patch.</summary>
    public bool CanSearch => SelectedRow is { CanChoose: true, IsRenamed: false };

    /// <summary>A search among the identified files of the last simulation, for the selected patch.</summary>
    [ObservableProperty]
    private string _originSearchText = string.Empty;

    public BulkObservableCollection<PatchOriginOptionViewModel> OriginSearchResults { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChooseSearchResultCommand))]
    private PatchOriginOptionViewModel? _selectedSearchResult;

    /// <summary>A renaming is running.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand))]
    private bool _isRenaming;

    /// <summary>A patch was renamed since the window opened: a new simulation applies it.</summary>
    [ObservableProperty]
    private bool _hasRenamed;

    /// <summary>Label of the button that renames the patches checked, with their number.</summary>
    public string RenameLabel => Tr("patches.btn_rename", ("count", CheckedCount));

    public int CheckedCount => Rows.Count(r => r.IsChecked && r.CanCheck);

    // -------------------------------------------------------------------------
    // The banner
    // -------------------------------------------------------------------------

    /// <summary>The IPS on the disk applied thanks to their readme in the last run, whose name could give the CRC32.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNamingLinkVisible))]
    private int _noteMatchedCount;

    /// <summary>
    /// Whether the banner shows the IPS to name: only without patches left out, whose own text opens the same window.
    /// </summary>
    public bool IsNamingLinkVisible => NoteMatchedCount > 0 && (_lastStats()?.UnappliedPatches.Count ?? 0) == 0;

    public string NamingLinkText => Tr(NoteMatchedCount == 1 ? "stats.patches_to_name_one" : "stats.patches_to_name", ("count", NoteMatchedCount));

    /// <summary>Counts again what the last run gives to the window; called when a run ends or starts, and when its results change.</summary>
    public void Refresh()
    {
        NoteMatchedCount = _isRunning() ? 0 : NoteMatchedItems(_lastItems()).Count(p => p.File.ArchiveMember is null);
        OnPropertyChanged(nameof(IsNamingLinkVisible));
        OnPropertyChanged(nameof(NamingLinkText));
        OpenCommand.NotifyCanExecuteChanged();
    }

    private bool CanOpen() =>
        !_isRunning() && ((_lastStats()?.UnappliedPatches.Count ?? 0) > 0 || NoteMatchedItems(_lastItems()).Any());

    /// <summary>Opens the window on the last run; closed by « Simulate again », runs a new simulation.</summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        Load();
        if (await _dialogService.ShowPatchesAsync(this) && !_isRunning())
        {
            await _simulate();
        }
    }

    // -------------------------------------------------------------------------
    // The rows
    // -------------------------------------------------------------------------

    /// <summary>Fills the window from the last run: its patches left out, then the IPS applied thanks to their readme.</summary>
    public void Load()
    {
        var config = _lastConfig();
        _patchDir = config?.PatchDir ?? string.Empty;
        _detectCopierHeaders = config?.Filters.DetectCopierHeaders ?? true;
        var items = _lastItems();
        _searchable = [.. items
            .Where(it => PatchMatcher.IsPatchOrigin(it) && it.RelatedItems.Count == 0 && !it.IsMultiMedia)
            .OrderBy(it => it.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(it => new PatchOriginOptionViewModel(it))];

        var rows = new List<PatchRowViewModel>();
        foreach (var unapplied in _lastStats()?.UnappliedPatches ?? [])
        {
            var canChoose = ChoosableReasons.Contains(unapplied.Reason.Key) && !PatchMatcher.NameGivesCrc(unapplied.File.DisplayName);
            var row = new PatchRowViewModel(this, unapplied.File, PatchRowKind.Unapplied, _localization.Format(unapplied.Reason), canChoose, LocationOf(unapplied.File));
            if (canChoose)
            {
                foreach (var group in unapplied.Suggestions.GroupBy(s => s.Origin, ReferenceEqualityComparer.Instance))
                {
                    var origin = (ProcessItem)group.Key!;
                    var forms = group.ToList();

                    // The two forms a readme gave are known; a single one is read again with its other form
                    if (forms.Any(f => f.WithCopierHeader is not null))
                    {
                        row.KnowForms(origin, forms);
                    }

                    row.Origins.Add(new PatchOriginOptionViewModel(origin));
                }

                row.SelectedOrigin = row.Origins.FirstOrDefault();
            }

            rows.Add(row);
        }

        foreach (var version in NoteMatchedItems(items))
        {
            var origin = version.Origin;
            var row = new PatchRowViewModel(
                this,
                version.File,
                PatchRowKind.AppliedByNote,
                Tr("patches.applied_by_note", ("origin", origin.DisplayName), ("note", version.Note?.DisplayName ?? string.Empty)),
                canChoose: false,
                LocationOf(version.File));
            row.KnowForms(origin, [new PatchOriginSuggestion(origin, version.SourceCrc32, version.SourceOffset)]);
            row.Origins.Add(new PatchOriginOptionViewModel(origin));
            row.SelectedOrigin = row.Origins[0];
            rows.Add(row);
        }

        Rows.ReplaceAll(rows);
        HasRenamed = false;
        OriginSearchText = string.Empty;
        SelectedRow = Rows.FirstOrDefault();
        OnRowCheckedChanged();
    }

    /// <summary>The patched versions of the run whose CRC32 a readme gave, one per patch.</summary>
    private static IEnumerable<PatchedVersion> NoteMatchedItems(IEnumerable<ProcessItem> items) =>
        items.Select(it => it.Patch)
            .OfType<PatchedVersion>()
            .Where(p => p.CrcSource == PatchCrcSource.Notes)
            .DistinctBy(p => p.File);

    private string LocationOf(PatchFile patch)
    {
        if (patch.ArchiveMember is not null)
        {
            return Path.GetFileName(patch.SourcePath);
        }

        var folder = Path.GetDirectoryName(patch.SourcePath) ?? string.Empty;
        if (string.IsNullOrEmpty(_patchDir))
        {
            return folder;
        }

        var relative = Path.GetRelativePath(_patchDir, folder);
        return relative == "." ? string.Empty : relative;
    }

    internal void OnRowCheckedChanged()
    {
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(RenameLabel));
        RenameCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedRowChanged(PatchRowViewModel? value)
    {
        OriginSearchText = string.Empty;
        OriginSearchResults.ReplaceAll([]);
    }

    partial void OnOriginSearchTextChanged(string value)
    {
        var query = SearchQuery.Parse(value);
        OriginSearchResults.ReplaceAll(query.IsEmpty || !CanSearch
            ? []
            : _searchable.Where(o => query.Matches(o.SearchName)).Take(MaxSearchResults));
        SelectedSearchResult = OriginSearchResults.FirstOrDefault();
    }

    private bool CanChooseSearchResult() => SelectedSearchResult is not null && CanSearch;

    /// <summary>Takes the file found by the search as the origin of the selected patch.</summary>
    [RelayCommand(CanExecute = nameof(CanChooseSearchResult))]
    private void ChooseSearchResult()
    {
        if (SelectedRow is { } row && SelectedSearchResult is { } found)
        {
            row.Choose(found.Origin);
        }
    }

    /// <summary>Checks the IPS applied thanks to their readme, so that other tools apply them too.</summary>
    [RelayCommand]
    private void CheckNoteMatched()
    {
        foreach (var row in Rows.Where(r => r.Kind == PatchRowKind.AppliedByNote && r.CanCheck))
        {
            row.IsChecked = true;
        }
    }

    private bool CanRename() => !IsRenaming && CheckedCount > 0;

    /// <summary>Writes the CRC32 of the form chosen in the name of each patch checked.</summary>
    [RelayCommand(CanExecute = nameof(CanRename))]
    private async Task RenameAsync()
    {
        var rows = Rows.Where(r => r.IsChecked && r.CanCheck).ToList();
        IsRenaming = true;
        try
        {
            var results = await _naming.RenameAsync(
                _patchDir,
                [.. rows.Select(r => new PatchRename(r.Patch, r.SelectedForm!.Form.Crc32))],
                _log);
            for (var i = 0; i < rows.Count; i++)
            {
                if (results[i].NewPath is { } newPath)
                {
                    rows[i].MarkRenamed(Path.GetFileName(newPath));
                    HasRenamed = true;
                }
                else
                {
                    rows[i].ResultText = _localization.Format(results[i].Refusal!);
                }
            }
        }
        finally
        {
            IsRenaming = false;
            OnRowCheckedChanged();
            OnPropertyChanged(nameof(CanSearch));
        }
    }

    // -------------------------------------------------------------------------
    // Helpers of the rows
    // -------------------------------------------------------------------------

    internal string Tr(string key, params (string Name, object? Value)[] args) => _localization.Tr(key, args);

    internal string NewName(PatchRowViewModel row, uint crc) => _naming.NameWithCrc(row.Patch.DisplayName, crc);

    internal Task<IReadOnlyList<PatchOriginSuggestion>> ReadFormsAsync(ProcessItem origin) =>
        _naming.GetOriginFormsAsync(origin, _detectCopierHeaders);

    internal string FormLabel(PatchOriginSuggestion form) => form.WithCopierHeader switch
    {
        true => Tr("patches.form_with_header", ("crc", FormatCrc(form.Crc32))),
        false => Tr("patches.form_without_header", ("crc", FormatCrc(form.Crc32))),
        null => Tr("patches.form_single", ("crc", FormatCrc(form.Crc32)))
    };

    internal static string FormatCrc(uint crc) => crc.ToString("X8", CultureInfo.InvariantCulture);

    public void RefreshTranslations()
    {
        I18n.NotifyUpdated();
        OnPropertyChanged(nameof(NamingLinkText));
        OnPropertyChanged(nameof(RenameLabel));
    }
}
