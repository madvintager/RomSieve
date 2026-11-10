using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RomSieve.App.Abstractions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.App.ViewModels;

/// <summary>
/// The title of a group in the list of the patches, with its count and, for two groups, an action on all its patches:
/// « Accept all the suggestions » for the patches to match, « Write their ROM in their name » for the IPS applied thanks
/// to their readme.
/// </summary>
public sealed class PatchGroupViewModel(PatchesViewModel owner, PatchGroupKind kind) : ObservableObject
{
    public PatchGroupKind Kind { get; } = kind;

    public string Title => owner.GroupTitle(Kind);

    /// <summary>Patches the action of the group would show in its summary.</summary>
    public int BatchCount => owner.BatchEntriesOf(Kind).Count();

    public bool HasBatch => BatchCount > 0;

    public string BatchLabel => Kind == PatchGroupKind.ToMatch
        ? owner.Tr("patches.batch_accept", ("count", BatchCount))
        : owner.Tr("patches.batch_note", ("count", BatchCount));

    public string BatchTooltip => owner.Tr(Kind == PatchGroupKind.ToMatch ? "patches.tooltip_batch_accept" : "patches.tooltip_batch_note");

    public IAsyncRelayCommand<PatchGroupKind> BatchCommand => owner.OpenBatchCommand;

    internal void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(BatchCount));
        OnPropertyChanged(nameof(HasBatch));
        OnPropertyChanged(nameof(BatchLabel));
        OnPropertyChanged(nameof(BatchTooltip));
    }
}

/// <summary>A patch in the summary of an action on a group: the ROM it would designate, its new name, and its box.</summary>
public sealed partial class PatchBatchEntryViewModel(PatchesViewModel owner, PatchRowViewModel row, ProcessItem origin, uint crc32, string newName)
    : ObservableObject
{
    public PatchRowViewModel Row { get; } = row;

    public ProcessItem Origin { get; } = origin;

    public uint Crc32 { get; } = crc32;

    public string Name => Row.Name;

    public string OriginLabel { get; } = origin.DisplayName;

    public string NewName { get; } = newName;

    /// <summary>Whether the patch is to be renamed: never checked beforehand.</summary>
    [ObservableProperty]
    private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => owner.OnBatchCheckedChanged();
}

/// <summary>
/// The window of the patches (<c>PatchesWindow</c>), opened from the banner or the table after a simulation. A list
/// grouped by state (to match, applied, unfixable) and the detail of the patch selected: the ROMs proposed then those of
/// a search, the form of the ROM when it has two, what applying would rename, and direct actions: « Apply to this ROM »
/// (the CRC32 of the ROM written in the name of the patch, replacing the one it gave), « Stop applying to this ROM » (the
/// CRC32 removed) and « Undo » (the old name back), through <see cref="IPatchNamingService"/>, never in an archive. A
/// suggestion is never taken alone: nothing is renamed without a click, and the summary of an action on a group checks
/// nothing beforehand. « Close and simulate again » runs a new simulation, which takes the patches modified into account.
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
    private readonly IPatchLinkService _links;
    private readonly Func<IReadOnlyList<ProcessItem>> _lastItems;
    private readonly Func<ProcessStats?> _lastStats;
    private readonly Func<SortConfig?> _lastConfig;
    private readonly Func<bool> _isRunning;
    private readonly Func<Task> _simulate;
    private readonly Action<LogLevel, LocalizedMessage> _log;
    private readonly Dictionary<PatchGroupKind, PatchGroupViewModel> _groups;

    private List<PatchOriginOptionViewModel> _searchable = [];
    private string _patchDir = string.Empty;

    /// <summary>The source of the last run: the folders of the ROMs are shown in it.</summary>
    internal string SourceDir { get; private set; } = string.Empty;
    private bool _detectCopierHeaders = true;

    public PatchesViewModel(
        ILocalizationService localization,
        IDialogService dialogService,
        IPatchNamingService naming,
        IPatchLinkService links,
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
        _links = links;
        _lastItems = lastItems;
        _lastStats = lastStats;
        _lastConfig = lastConfig;
        _isRunning = isRunning;
        _simulate = simulate;
        _log = log;
        I18n = new UiStringsIndexer(localization);
        _groups = Enum.GetValues<PatchGroupKind>().ToDictionary(k => k, k => new PatchGroupViewModel(this, k));
    }

    /// <summary>Texts of the window, retranslated at a change of language.</summary>
    public UiStringsIndexer I18n { get; }

    /// <summary>Every patch of the window, in the order of the last run: left out, applied by a readme, applied by their name.</summary>
    public BulkObservableCollection<PatchRowViewModel> Rows { get; } = [];

    /// <summary>The list shown: the title of each group that has patches (<see cref="PatchGroupViewModel"/>), then its patches.</summary>
    public BulkObservableCollection<object> ListItems { get; } = [];

    // -------------------------------------------------------------------------
    // The banner
    // -------------------------------------------------------------------------

    /// <summary>The IPS on the disk applied thanks to their readme in the last run, whose name could give the CRC32.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNamingLinkVisible))]
    private int _noteMatchedCount;

    /// <summary>The IPS on the disk applied from the CRC32 of their name in the last run, which may change ROM.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNamingLinkVisible))]
    private int _nameMatchedCount;

    /// <summary>
    /// Whether the banner shows its link to the window without patches left out, whose own text opens it: the IPS to
    /// name, or else the patches applied from their name.
    /// </summary>
    public bool IsNamingLinkVisible =>
        (NoteMatchedCount > 0 || NameMatchedCount > 0) && (_lastStats()?.UnappliedPatches.Count ?? 0) == 0;

    public string NamingLinkText => NoteMatchedCount > 0
        ? _localization.Format(LocalizedMessage.Count("stats.patches_to_name", NoteMatchedCount))
        : Tr("stats.patches_open");

    public string NamingLinkTooltip => Tr(NoteMatchedCount > 0 ? "stats.tooltip_patches_to_name" : "stats.tooltip_patches_open");

    /// <summary>Counts again what the last run gives to the window; called when a run ends or starts, and when its results change.</summary>
    public void Refresh()
    {
        NoteMatchedCount = _isRunning() ? 0 : NoteMatchedItems(_lastItems()).Count(p => p.File.ArchiveMember is null);
        NameMatchedCount = _isRunning() ? 0 : NameMatchedItems(_lastItems()).Count();
        OnPropertyChanged(nameof(IsNamingLinkVisible));
        OnPropertyChanged(nameof(NamingLinkText));
        OnPropertyChanged(nameof(NamingLinkTooltip));
        OpenCommand.NotifyCanExecuteChanged();
    }

    private bool CanOpen() =>
        !_isRunning()
        && ((_lastStats()?.UnappliedPatches.Count ?? 0) > 0 || NoteMatchedItems(_lastItems()).Any() || NameMatchedItems(_lastItems()).Any());

    /// <summary>Opens the window on the last run; closed by « Close and simulate again », runs a new simulation.</summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        Load();
        if (await _dialogService.ShowPatchesAsync(this) && !_isRunning())
        {
            await _simulate();
        }
    }

    /// <summary>
    /// Whether the window holds the patch of a patched version: an IPS applied thanks to its readme, or from its name on
    /// the disk. A UPS or a BPS records its ROM, which nothing can change.
    /// </summary>
    public bool CanOpenFor(PatchedVersion? version) =>
        version is not null && !_isRunning()
        && (version.CrcSource == PatchCrcSource.Notes
            || (version.CrcSource == PatchCrcSource.FileName && version.File.ArchiveMember is null));

    /// <summary>Opens the window on the patch of a patched version of the table, selected.</summary>
    public async Task OpenForAsync(PatchedVersion? version)
    {
        if (version is null || !CanOpenFor(version))
        {
            return;
        }

        Load();
        SelectedRow = Rows.FirstOrDefault(r => r.File == version.File) ?? SelectedRow;
        if (await _dialogService.ShowPatchesAsync(this) && !_isRunning())
        {
            await _simulate();
        }
    }

    // -------------------------------------------------------------------------
    // The list
    // -------------------------------------------------------------------------

    /// <summary>Fills the window from the last run: its patches left out, then the IPS applied by a readme, then by their name.</summary>
    public void Load()
    {
        var config = _lastConfig();
        _patchDir = config?.PatchDir ?? string.Empty;
        SourceDir = config?.SourceDir ?? string.Empty;
        _detectCopierHeaders = config?.Filters.DetectCopierHeaders ?? true;
        var items = _lastItems();
        _searchable = [.. items
            .Where(it => PatchMatcher.IsPatchOrigin(it) && it.RelatedItems.Count == 0 && !it.IsMultiMedia)
            .OrderBy(it => it.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(it => new PatchOriginOptionViewModel(it, SourceDir))];

        var rows = new List<PatchRowViewModel>();
        foreach (var unapplied in _lastStats()?.UnappliedPatches ?? [])
        {
            var canChoose = ChoosableReasons.Contains(unapplied.Reason.Key) && !PatchMatcher.NameGivesCrc(unapplied.File.DisplayName);
            var row = new PatchRowViewModel(
                this,
                unapplied.File,
                canChoose ? PatchRowState.ToMatch : PatchRowState.Unfixable,
                unapplied.Reason,
                LocationOf(unapplied.File));
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

                    row.AddOrigin(origin);
                }

                row.SelectedOrigin = row.Origins.FirstOrDefault();
            }

            rows.Add(row);
        }

        foreach (var version in NoteMatchedItems(items))
        {
            var row = new PatchRowViewModel(
                this,
                version.File,
                PatchRowState.AppliedByNote,
                null,
                LocationOf(version.File),
                version.Origin,
                version.SourceCrc32,
                version.Note);
            row.KnowForms(version.Origin, [new PatchOriginSuggestion(version.Origin, version.SourceCrc32, version.SourceOffset)]);
            row.AddOrigin(version.Origin);
            row.SelectedOrigin = row.Origins[0];
            rows.Add(row);
        }

        // The IPS applied from their name: their ROM first, with its forms, so that the user may choose another one
        foreach (var version in NameMatchedItems(items))
        {
            var row = new PatchRowViewModel(
                this,
                version.File,
                PatchRowState.AppliedByName,
                null,
                LocationOf(version.File),
                version.Origin,
                version.SourceCrc32);
            row.AddOrigin(version.Origin);
            row.SelectedOrigin = row.Origins[0];
            rows.Add(row);
        }

        // The action on the patches to match counts those whose ROM is chosen: their forms are read after the window opens
        foreach (var row in rows)
        {
            row.PropertyChanged += OnRowPropertyChanged;
        }

        Rows.ReplaceAll(rows);
        IsBatchMode = false;
        BatchEntries.ReplaceAll([]);
        OriginSearchText = string.Empty;
        Regroup();
        SelectedRow = ListItems.OfType<PatchRowViewModel>().FirstOrDefault();
        NotifyFooter();
    }

    /// <summary>The patched versions of the run whose CRC32 a readme gave, one per patch.</summary>
    private static IEnumerable<PatchedVersion> NoteMatchedItems(IEnumerable<ProcessItem> items) =>
        items.Select(it => it.Patch)
            .OfType<PatchedVersion>()
            .Where(p => p.CrcSource == PatchCrcSource.Notes)
            .DistinctBy(p => p.File);

    /// <summary>
    /// The patched versions of the run whose CRC32 the name of their IPS gave, on the disk, one per patch: a patch in an
    /// archive is never renamed, so nothing could be done for it.
    /// </summary>
    private static IEnumerable<PatchedVersion> NameMatchedItems(IEnumerable<ProcessItem> items) =>
        items.Select(it => it.Patch)
            .OfType<PatchedVersion>()
            .Where(p => p.CrcSource == PatchCrcSource.FileName && p.File.ArchiveMember is null)
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

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PatchRowViewModel.SelectedForm))
        {
            _groups[PatchGroupKind.ToMatch].Refresh();
        }
    }

    /// <summary>Lays the list out again: each group that has patches, its title then its patches; the selection stays.</summary>
    private void Regroup()
    {
        var items = new List<object>();
        foreach (var kind in Enum.GetValues<PatchGroupKind>())
        {
            var group = _groups[kind];
            var rows = Rows.Where(r => r.Group == kind).ToList();
            if (rows.Count > 0)
            {
                items.Add(group);
                items.AddRange(rows);
            }

            group.Refresh();
        }

        var selected = SelectedRow;
        ListItems.ReplaceAll(items);
        if (selected is not null)
        {
            OnPropertyChanged(nameof(SelectedListItem));
        }
    }

    internal string GroupTitle(PatchGroupKind kind)
    {
        var count = Rows.Count(r => r.Group == kind);
        return kind switch
        {
            PatchGroupKind.ToMatch => Tr("patches.group_to_match", ("count", count)),
            PatchGroupKind.Applied => Tr("patches.group_applied", ("count", count)),
            _ => Tr("patches.group_unfixable", ("count", count))
        };
    }

    // -------------------------------------------------------------------------
    // The patch selected
    // -------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRow), nameof(CanSearch), nameof(SelectedListItem))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(DetachCommand), nameof(UndoCommand))]
    private PatchRowViewModel? _selectedRow;

    public bool HasSelectedRow => SelectedRow is not null;

    /// <summary>The item selected in the list: a patch; a title of group is not selected, the patch selected stays.</summary>
    public object? SelectedListItem
    {
        get => SelectedRow;
        set
        {
            if (value is PatchRowViewModel row)
            {
                SelectedRow = row;
            }
            else
            {
                // Back to the patch selected: a title is never selected
                OnPropertyChanged();
            }
        }
    }

    partial void OnSelectedRowChanged(PatchRowViewModel? oldValue, PatchRowViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnSelectedRowPropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnSelectedRowPropertyChanged;
        }

        OriginSearchText = string.Empty;
        OriginSearchResults.ReplaceAll([]);
        OnPropertyChanged(nameof(HasSearchResults));
    }

    private void OnSelectedRowPropertyChanged(object? sender, PropertyChangedEventArgs e) => NotifyActions();

    /// <summary>Whether the search can give a ROM to the selected patch.</summary>
    public bool CanSearch => SelectedRow is { CanChoose: true } && !IsBusy;

    /// <summary>A search among the identified files of the last simulation, for the selected patch.</summary>
    [ObservableProperty]
    private string _originSearchText = string.Empty;

    public BulkObservableCollection<PatchOriginOptionViewModel> OriginSearchResults { get; } = [];

    public bool HasSearchResults => OriginSearchResults.Count > 0;

    /// <summary>A file found by the search: chosen, it joins the ROMs of the selected patch and becomes its choice.</summary>
    [ObservableProperty]
    private PatchOriginOptionViewModel? _selectedSearchResult;

    partial void OnOriginSearchTextChanged(string value)
    {
        var query = SearchQuery.Parse(value);
        OriginSearchResults.ReplaceAll(query.IsEmpty || !CanSearch
            ? []
            : _searchable.Where(o => query.Matches(o.SearchName)).Take(MaxSearchResults));
        OnPropertyChanged(nameof(HasSearchResults));
    }

    partial void OnSelectedSearchResultChanged(PatchOriginOptionViewModel? value)
    {
        if (value is not null && CanSearch)
        {
            SelectedRow!.Choose(value.Origin);
        }
    }

    /// <summary>
    /// Enter in the search: the first file found becomes the choice of the patch. Enter never renames anything, so that a
    /// search ended by Enter cannot rename a patch by accident.
    /// </summary>
    [RelayCommand]
    private void ChooseFirstSearchResult()
    {
        if (OriginSearchResults.Count > 0)
        {
            SelectedSearchResult = OriginSearchResults[0];
        }
    }

    // -------------------------------------------------------------------------
    // The actions on the patch selected
    // -------------------------------------------------------------------------

    /// <summary>A renaming (or its undoing) is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSearch))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(DetachCommand), nameof(UndoCommand), nameof(ApplyBatchCommand), nameof(OpenBatchCommand))]
    private bool _isBusy;

    private bool CanApply() => !IsBusy && SelectedRow is { CanApply: true };

    /// <summary>Writes the CRC32 of the ROM chosen in the name of the selected patch.</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (SelectedRow is { SelectedForm: { } form, SelectedOrigin: { } origin } row)
        {
            await RenameAsync([(row, new PatchRename(row.File, form.Form.Crc32), origin.Origin)]);
        }
    }

    private bool CanDetach() => !IsBusy && SelectedRow is { CanDetach: true };

    /// <summary>
    /// Stops applying the selected patch to its ROM: the CRC32 its name gives is removed, and the next simulation
    /// proposes it again. The patched version a sort already wrote stays where it is.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDetach))]
    private async Task DetachAsync()
    {
        if (SelectedRow is { } row)
        {
            await RenameAsync([(row, new PatchRename(row.File, null), null)]);
        }
    }

    private bool CanUndo() => !IsBusy && SelectedRow is { CanUndo: true };

    /// <summary>Gives back to the selected patch the name it had before its last renaming in the window.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (SelectedRow is not { LastRenaming: { } renaming } row)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _naming.UndoAsync(_patchDir, renaming, _log);
            if (result.IsUndone)
            {
                row.Undone();
            }
            else
            {
                row.ResultText = _localization.Format(result.Refusal!);
            }
        }
        finally
        {
            IsBusy = false;
            AfterChange();
        }
    }

    /// <summary>Renames patches, each with the ROM its name will designate (none to detach it), and says what became of each.</summary>
    private async Task RenameAsync(IReadOnlyList<(PatchRowViewModel Row, PatchRename Rename, ProcessItem? Origin)> renames)
    {
        IsBusy = true;
        try
        {
            var results = await _naming.RenameAsync(_patchDir, [.. renames.Select(r => r.Rename)], _log);
            for (var i = 0; i < renames.Count; i++)
            {
                if (results[i].IsRenamed)
                {
                    renames[i].Row.Renamed(results[i], renames[i].Origin);
                }
                else
                {
                    renames[i].Row.ResultText = _localization.Format(results[i].Refusal!);
                }
            }
        }
        finally
        {
            IsBusy = false;
            AfterChange();
        }
    }

    /// <summary>What a renaming changes: the groups, the footer and the actions.</summary>
    private void AfterChange()
    {
        Regroup();
        NotifyFooter();
        NotifyActions();
    }

    private void NotifyActions()
    {
        ApplyCommand.NotifyCanExecuteChanged();
        DetachCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSearch));
    }

    // -------------------------------------------------------------------------
    // The actions on a group
    // -------------------------------------------------------------------------

    /// <summary>The summary of an action on a group replaces the detail of the patch selected.</summary>
    [ObservableProperty]
    private bool _isBatchMode;

    [ObservableProperty]
    private PatchGroupKind _batchKind;

    public string BatchTitle => Tr(BatchKind == PatchGroupKind.ToMatch ? "patches.batch_title_accept" : "patches.batch_title_note");

    public BulkObservableCollection<PatchBatchEntryViewModel> BatchEntries { get; } = [];

    public int BatchCheckedCount => BatchEntries.Count(e => e.IsChecked);

    public string ApplyBatchLabel => Tr("patches.btn_batch_apply", ("count", BatchCheckedCount));

    /// <summary>
    /// The patches an action on a group would rename: those to match, on the disk, with the ROM chosen for each; or the
    /// IPS applied thanks to their readme, on the disk, with the ROM of their readme.
    /// </summary>
    internal IEnumerable<(PatchRowViewModel Row, ProcessItem Origin, uint Crc32)> BatchEntriesOf(PatchGroupKind kind) => kind switch
    {
        PatchGroupKind.ToMatch => Rows
            .Where(r => r.State == PatchRowState.ToMatch && !r.IsInArchive && r.SelectedOrigin is not null && r.SelectedForm is not null)
            .Select(r => (r, r.SelectedOrigin!.Origin, r.SelectedForm!.Form.Crc32)),
        PatchGroupKind.Applied => Rows
            .Where(r => r.State == PatchRowState.AppliedByNote && !r.IsInArchive && r.CurrentOrigin is not null && r.CurrentCrc32 is not null)
            .Select(r => (r, r.CurrentOrigin!, r.CurrentCrc32!.Value)),
        _ => []
    };

    /// <summary>Shows the summary of an action on a group, nothing checked.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenBatch))]
    private async Task OpenBatchAsync(PatchGroupKind kind)
    {
        // The forms of the ROMs proposed are read when the window opens
        await Task.WhenAll(Rows.Select(r => r.FormsLoading));
        BatchKind = kind;
        BatchEntries.ReplaceAll(BatchEntriesOf(kind).Select(e => new PatchBatchEntryViewModel(this, e.Row, e.Origin, e.Crc32, NewName(e.Row.Name, e.Crc32))));
        IsBatchMode = true;
        OnPropertyChanged(nameof(BatchTitle));
        OnBatchCheckedChanged();
    }

    private bool CanOpenBatch(PatchGroupKind kind) => !IsBusy;

    /// <summary>Checks every patch of the summary.</summary>
    [RelayCommand]
    private void CheckAllBatch()
    {
        foreach (var entry in BatchEntries)
        {
            entry.IsChecked = true;
        }
    }

    private bool CanApplyBatch() => !IsBusy && BatchCheckedCount > 0;

    /// <summary>Renames the patches checked in the summary, then goes back to the detail.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyBatch))]
    private async Task ApplyBatchAsync()
    {
        var entries = BatchEntries.Where(e => e.IsChecked).ToList();
        await RenameAsync([.. entries.Select(e => (e.Row, new PatchRename(e.Row.File, e.Crc32), (ProcessItem?)e.Origin))]);
        CloseBatch();
    }

    /// <summary>Back to the detail of the patch selected, nothing renamed.</summary>
    [RelayCommand]
    private void CloseBatch()
    {
        IsBatchMode = false;
        BatchEntries.ReplaceAll([]);
        OnBatchCheckedChanged();
    }

    internal void OnBatchCheckedChanged()
    {
        OnPropertyChanged(nameof(BatchCheckedCount));
        OnPropertyChanged(nameof(ApplyBatchLabel));
        ApplyBatchCommand.NotifyCanExecuteChanged();
    }

    // -------------------------------------------------------------------------
    // The footer
    // -------------------------------------------------------------------------

    /// <summary>Patches whose name changed since the window opened.</summary>
    public int ModifiedCount => Rows.Count(r => r.IsModified);

    public bool HasModified => ModifiedCount > 0;

    public string ModifiedText => HasModified ? _localization.Format(LocalizedMessage.Count("patches.modified", ModifiedCount)) : string.Empty;

    private void NotifyFooter()
    {
        OnPropertyChanged(nameof(ModifiedCount));
        OnPropertyChanged(nameof(HasModified));
        OnPropertyChanged(nameof(ModifiedText));
    }

    // -------------------------------------------------------------------------
    // Helpers of the rows
    // -------------------------------------------------------------------------

    internal string Tr(string key, params (string Name, object? Value)[] args) => _localization.Tr(key, args);

    internal string Format(LocalizedMessage message) => _localization.Format(message);

    internal string NewName(string name, uint crc) => _naming.NameWithCrc(name, crc);

    internal Task<IReadOnlyList<PatchOriginSuggestion>> ReadFormsAsync(ProcessItem origin) =>
        _links.GetOriginFormsAsync(origin, _detectCopierHeaders);

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
        OnPropertyChanged(nameof(NamingLinkTooltip));
        OnPropertyChanged(nameof(BatchTitle));
        OnPropertyChanged(nameof(ApplyBatchLabel));
        NotifyFooter();
        foreach (var group in _groups.Values)
        {
            group.Refresh();
        }

        foreach (var row in Rows)
        {
            row.NotifyState();
        }
    }
}
