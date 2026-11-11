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
/// The title of a group in the list of the patches, with its count and, for the patches to match, « Accept all the
/// suggestions ».
/// </summary>
public sealed class PatchGroupViewModel(PatchesViewModel owner, PatchGroupKind kind) : ObservableObject
{
    public PatchGroupKind Kind { get; } = kind;

    public string Title => owner.GroupTitle(Kind);

    /// <summary>Patches the action of the group would show in its summary.</summary>
    public int BatchCount => owner.BatchEntriesOf(Kind).Count();

    public bool HasBatch => BatchCount > 0;

    public string BatchLabel => owner.Tr("patches.batch_accept", ("count", BatchCount));

    public string BatchTooltip => owner.Tr("patches.tooltip_batch_accept");

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

/// <summary>A patch in the summary of « Accept all the suggestions »: the ROM it would apply to, and its box.</summary>
public sealed partial class PatchBatchEntryViewModel(PatchesViewModel owner, PatchRowViewModel row, ProcessItem origin, uint crc32)
    : ObservableObject
{
    public PatchRowViewModel Row { get; } = row;

    public ProcessItem Origin { get; } = origin;

    public uint Crc32 { get; } = crc32;

    public string Name => Row.Name;

    public string OriginLabel { get; } = origin.DisplayName;

    /// <summary>Whether the patch is to be applied to its ROM: never checked beforehand.</summary>
    [ObservableProperty]
    private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => owner.OnBatchCheckedChanged();
}

/// <summary>
/// The window of the patches (<c>PatchesWindow</c>), opened from the banner or the table after a simulation: every patch
/// of the last run, grouped by state (to match, applied, back to the automatic choice, set aside, unfixable), and the
/// detail of the patch selected: where it stands, the ROMs proposed then those of a search, the form of the ROM when it
/// has two, and direct actions. « Apply to this ROM » and « Do not apply this patch » record the choice of the user
/// (<see cref="IPatchLinkService"/>), « Back to the automatic choice » forgets it: none changes a file. « Write the ROM in
/// the file name » renames an IPS of the disk for other tools (<see cref="IPatchNamingService"/>). « Undo » takes back
/// the last change of the patch selected. A suggestion is never taken alone: nothing is recorded without a click, and the
/// summary of « Accept all the suggestions » checks nothing beforehand. « Simulate again » runs a new simulation, which
/// follows the choices, and shows its results in the window, which stays open; closing the window with choices not
/// simulated yet simulates again too, so that the table always shows what the choices give.
/// </summary>
public sealed partial class PatchesViewModel : ViewModelBase
{
    /// <summary>Most identified files the search shows.</summary>
    public const int MaxSearchResults = 50;

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

    /// <summary>Every patch of the window, in the order of the last run: left out, applied, set aside.</summary>
    public BulkObservableCollection<PatchRowViewModel> Rows { get; } = [];

    /// <summary>The list shown: the title of each group that has patches (<see cref="PatchGroupViewModel"/>), then its patches.</summary>
    public BulkObservableCollection<object> ListItems { get; } = [];

    // -------------------------------------------------------------------------
    // The banner
    // -------------------------------------------------------------------------

    /// <summary>The patches of the last run: applied, left out or set aside.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpenLinkVisible))]
    private int _patchCount;

    /// <summary>
    /// Whether the banner shows its link « See the patches »: a run with patches, none left out (the text of the patches
    /// left out opens the window then).
    /// </summary>
    public bool IsOpenLinkVisible => PatchCount > 0 && (_lastStats()?.UnappliedPatches.Count ?? 0) == 0;

    /// <summary>Counts again what the last run gives to the window; called when a run ends or starts, and when its results change.</summary>
    public void Refresh()
    {
        PatchCount = _isRunning() ? 0 : CountPatches();
        OnPropertyChanged(nameof(IsOpenLinkVisible));
        OpenCommand.NotifyCanExecuteChanged();
    }

    private int CountPatches()
    {
        var stats = _lastStats();
        return PatchedVersions(_lastItems()).Count() + (stats?.UnappliedPatches.Count ?? 0) + (stats?.IgnoredPatches.Count ?? 0);
    }

    private bool CanOpen() => !_isRunning() && CountPatches() > 0;

    /// <summary>Opens the window on the last run, from the banner or the Collection step.</summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        Load();
        await ShowAsync();
    }

    /// <summary>
    /// Shows the window, then simulates again when choices made in it are not simulated yet: the table always shows
    /// what the choices give, as the sort would.
    /// </summary>
    private async Task ShowAsync()
    {
        await _dialogService.ShowPatchesAsync(this);
        if (HasModified && !_isRunning())
        {
            await _simulate();
        }
    }

    /// <summary>Whether the window can open on the patch of a patched version, whatever its format.</summary>
    public bool CanOpenFor(PatchedVersion? version) => version is not null && !_isRunning();

    /// <summary>
    /// The entry of the context menu of the table for a patched version: the ROM of an IPS may change; a UPS or a BPS
    /// records its own, and is only shown.
    /// </summary>
    public string OpenForLabel(PatchedVersion? version) =>
        Tr(version?.Format is PatchFormat.Ups or PatchFormat.Bps ? "panel4.ctx_show_patch" : "panel4.ctx_open_patch");

    /// <summary>Opens the window on the patch of a patched version of the table, selected.</summary>
    public async Task OpenForAsync(PatchedVersion? version)
    {
        if (version is null || !CanOpenFor(version))
        {
            return;
        }

        Load();
        SelectedRow = Rows.FirstOrDefault(r => r.File == version.File) ?? SelectedRow;
        await ShowAsync();
    }

    /// <summary>Whether the patch of a patched version can be set aside from the table.</summary>
    public bool CanIgnoreFor(PatchedVersion? version) => version is { Key.Sha1: not null } && !_isRunning() && !IsBusy;

    /// <summary>
    /// Sets aside the patch of a patched version of the table, then simulates again, so that the table no longer shows
    /// it. The window of the patches takes it back (« Back to the automatic choice »).
    /// </summary>
    public async Task IgnoreForAsync(PatchedVersion? version)
    {
        if (version is null || !CanIgnoreFor(version))
        {
            return;
        }

        var link = PatchLink.Ignore(version.File.DisplayName);
        IsBusy = true;
        try
        {
            if (!await _links.UpdateAsync([new(version.Key, link)], _log))
            {
                return;
            }

            LogChoice(version.File.DisplayName, link);
        }
        finally
        {
            IsBusy = false;
        }

        await _simulate();
    }

    // -------------------------------------------------------------------------
    // Simulating again, the window open
    // -------------------------------------------------------------------------

    /// <summary>A simulation started by the window runs: every action waits for it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModifiedText))]
    private bool _isSimulating;

    private bool CanSimulateAgain() => HasModified && !IsBusy && !_isRunning();

    /// <summary>
    /// Runs the simulation again, which follows the choices made, and shows its results in the window, which stays open
    /// on the same patch.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSimulateAgain))]
    private async Task SimulateAgainAsync()
    {
        var selected = SelectedRow;
        IsBusy = true;
        IsSimulating = true;
        try
        {
            await _simulate();
        }
        finally
        {
            IsSimulating = false;
            IsBusy = false;
        }

        Load();
        if (selected is not null)
        {
            // The same patch, known by its content; a patch that could not be read, by its file
            SelectedRow = Rows.FirstOrDefault(r => selected.Key is { } key ? r.Key == key : r.File == selected.File) ?? SelectedRow;
        }
    }

    // -------------------------------------------------------------------------
    // The list
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fills the window from the last run: its patches left out, its patches applied, then those set aside, each with the
    /// choice recorded for it now (a choice made by the command line since the run included).
    /// </summary>
    public void Load()
    {
        var config = _lastConfig();
        _patchDir = config?.PatchDir ?? string.Empty;
        SourceDir = config?.SourceDir ?? string.Empty;
        _detectCopierHeaders = config?.Filters.DetectCopierHeaders ?? true;
        var items = _lastItems();
        var stats = _lastStats();
        var links = _links.Load().Links;
        PatchLink? LinkOf(PatchKey? key) => key is { } k ? links.GetValueOrDefault(k) : null;

        _searchable = [.. items
            .Where(it => PatchMatcher.IsPatchOrigin(it) && it.RelatedItems.Count == 0 && !it.IsMultiMedia)
            .OrderBy(it => it.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(it => Option(it))];

        var rows = new List<PatchRowViewModel>();
        foreach (var unapplied in stats?.UnappliedPatches ?? [])
        {
            var isIps = unapplied.Key is not null && unapplied.Format is PatchFormat.Ips or PatchFormat.Ips32;
            var row = new PatchRowViewModel(
                this,
                unapplied.File,
                unapplied.Key,
                unapplied.Format,
                isIps ? PatchRowState.ToMatch : PatchRowState.Unfixable,
                LocationOf(unapplied.File),
                LinkOf(unapplied.Key),
                unapplied.Reason);
            if (isIps)
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

                    row.AddOrigin(origin, forms.Select(f => f.Reason).OfType<LocalizedMessage>().FirstOrDefault() is { } why ? Format(why) : null);
                }

                row.SelectedOrigin = row.Origins.FirstOrDefault();
            }

            rows.Add(row);
        }

        // The patches applied: their ROM first, with its forms, so that the user may choose another one for an IPS
        foreach (var item in items.Where(it => it.Patch is not null).DistinctBy(it => it.Patch!.File))
        {
            var version = item.Patch!;
            var row = new PatchRowViewModel(
                this,
                version.File,
                version.Key,
                version.Format,
                PatchRowState.Applied,
                LocationOf(version.File),
                LinkOf(version.Key),
                source: version.CrcSource,
                currentOrigin: version.Origin,
                currentCrc32: version.SourceCrc32,
                note: version.Note,
                patchedName: item.TargetFilename);
            if (version.CrcSource == PatchCrcSource.Notes)
            {
                // The readme gave a single form: the one it applies to, the other refused when both are given
                row.KnowForms(version.Origin, [new PatchOriginSuggestion(version.Origin, version.SourceCrc32, version.SourceOffset)]);
            }

            row.AddOrigin(version.Origin);
            if (row.IsIps)
            {
                row.SelectedOrigin = row.Origins[0];
            }

            rows.Add(row);
        }

        foreach (var ignored in stats?.IgnoredPatches ?? [])
        {
            rows.Add(new PatchRowViewModel(
                this,
                ignored.File,
                ignored.Key,
                ignored.Format,
                PatchRowState.Ignored,
                LocationOf(ignored.File),
                LinkOf(ignored.Key) ?? PatchLink.Ignore(ignored.File.DisplayName)));
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

    /// <summary>The patched versions of the run, one per patch.</summary>
    private static IEnumerable<PatchedVersion> PatchedVersions(IEnumerable<ProcessItem> items) =>
        items.Select(it => it.Patch).OfType<PatchedVersion>().DistinctBy(p => p.File);

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
            PatchGroupKind.Automatic => Tr("patches.group_automatic", ("count", count)),
            PatchGroupKind.Ignored => Tr("patches.group_ignored", ("count", count)),
            _ => Tr("patches.group_unfixable", ("count", count))
        };
    }

    // -------------------------------------------------------------------------
    // The patch selected
    // -------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRow), nameof(CanSearch), nameof(SelectedListItem))]
    [NotifyCanExecuteChangedFor(
        nameof(ApplyCommand), nameof(IgnoreCommand), nameof(RestoreAutomaticCommand), nameof(WriteNameCommand), nameof(ClearNameCommand),
        nameof(UndoCommand))]
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
            : _searchable.Where(o => query.Matches(o.SearchName, o.SearchCrc32)).Take(MaxSearchResults));
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
    /// Enter in the search: the first file found becomes the choice of the patch. Enter never records anything, so that a
    /// search ended by Enter cannot apply a patch by accident.
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

    /// <summary>A choice, a renaming or its undoing is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSearch))]
    [NotifyCanExecuteChangedFor(
        nameof(ApplyCommand), nameof(IgnoreCommand), nameof(RestoreAutomaticCommand), nameof(WriteNameCommand), nameof(ClearNameCommand),
        nameof(UndoCommand),
        nameof(ApplyBatchCommand), nameof(OpenBatchCommand), nameof(SimulateAgainCommand))]
    private bool _isBusy;

    private bool CanApply() => !IsBusy && SelectedRow is { CanApply: true };

    /// <summary>Records the ROM chosen for the selected patch: the next simulation applies it to that ROM.</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (SelectedRow is { SelectedForm: { } form, SelectedOrigin: { } origin } row)
        {
            await ChooseAsync([(row, LinkTo(row, origin.Origin, form.Form.Crc32), origin.Origin)]);
        }
    }

    private static PatchLink LinkTo(PatchRowViewModel row, ProcessItem origin, uint crc32) =>
        PatchLink.ToOrigin(row.Name, crc32, origin.DisplayName);

    private bool CanIgnore() => !IsBusy && SelectedRow is { CanIgnore: true };

    /// <summary>Sets the selected patch aside: the next simulation does not apply it, whatever designates its ROM.</summary>
    [RelayCommand(CanExecute = nameof(CanIgnore))]
    private async Task IgnoreAsync()
    {
        if (SelectedRow is { } row)
        {
            await ChooseAsync([(row, PatchLink.Ignore(row.Name), null)]);
        }
    }

    private bool CanRestoreAutomatic() => !IsBusy && SelectedRow is { CanRestoreAutomatic: true };

    /// <summary>Forgets the choice recorded for the selected patch: the next simulation applies it as its files say.</summary>
    [RelayCommand(CanExecute = nameof(CanRestoreAutomatic))]
    private async Task RestoreAutomaticAsync()
    {
        if (SelectedRow is { } row)
        {
            await ChooseAsync([(row, null, null)]);
        }
    }

    private bool CanWriteName() => !IsBusy && SelectedRow is { CanWriteName: true };

    /// <summary>Writes in the name of the selected IPS the CRC32 of the ROM it is applied to, for other tools.</summary>
    [RelayCommand(CanExecute = nameof(CanWriteName))]
    private async Task WriteNameAsync()
    {
        if (SelectedRow is not { CurrentCrc32: { } crc } row)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = (await _naming.RenameAsync(_patchDir, [new PatchRename(row.File, crc)], _log)).Single();
            if (result.IsRenamed)
            {
                row.Renamed(result);
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

    private bool CanClearName() => !IsBusy && SelectedRow is { CanClearName: true };

    /// <summary>
    /// Removes from the name of the selected IPS the ROM it designates, for other tools: the inverse of
    /// <see cref="WriteNameAsync"/>, also after a new simulation, when « Undo » no longer can. What RomSieve does with the
    /// patch does not change: a patch only its name applied first gets the choice of the user for the same ROM and form.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanClearName))]
    private async Task ClearNameAsync()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (row is { State: PatchRowState.Applied, Source: PatchCrcSource.FileName, Link: null, CurrentOrigin: { } origin, CurrentCrc32: { } crc })
            {
                var link = LinkTo(row, origin, crc);
                if (await SaveAsync([(row, link)]) is { } refusal)
                {
                    row.ResultText = refusal;
                    return;
                }

                LogChoice(row.Name, link);
                row.Chosen(link, origin);
            }

            var result = (await _naming.RenameAsync(_patchDir, [new PatchRename(row.File, null)], _log)).Single();
            if (result.IsRenamed)
            {
                row.Renamed(result);
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

    private bool CanUndo() => !IsBusy && SelectedRow is { CanUndo: true };

    /// <summary>Takes back the last change of the selected patch in the window: its choice, or its name.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (SelectedRow is not { CanUndo: true } row)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (row.LastRenaming is { } renaming)
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
            else if (await SaveAsync([(row, row.LinkBeforeLastChange)]) is { } refusal)
            {
                row.ResultText = refusal;
            }
            else
            {
                LogChoice(row.Name, row.LinkBeforeLastChange);
                row.Undone();
            }
        }
        finally
        {
            IsBusy = false;
            AfterChange();
        }
    }

    /// <summary>Records a choice for each patch (none: the automatic choice), then shows where each one stands.</summary>
    private async Task ChooseAsync(IReadOnlyList<(PatchRowViewModel Row, PatchLink? Link, ProcessItem? Origin)> choices)
    {
        IsBusy = true;
        try
        {
            var refusal = await SaveAsync([.. choices.Select(c => (c.Row, c.Link))]);
            foreach (var (row, link, origin) in choices)
            {
                if (refusal is not null)
                {
                    row.ResultText = refusal;
                    continue;
                }

                LogChoice(row.Name, link);
                row.Chosen(link, origin);
            }
        }
        finally
        {
            IsBusy = false;
            AfterChange();
        }
    }

    /// <summary>Writes choices in one step; the reason of a refusal, or <see langword="null"/> once written.</summary>
    private async Task<string?> SaveAsync(IReadOnlyList<(PatchRowViewModel Row, PatchLink? Link)> choices)
    {
        LocalizedMessage? refusal = null;
        var saved = await _links.UpdateAsync(
            [.. choices.Select(c => new KeyValuePair<PatchKey, PatchLink?>(c.Row.Key!.Value, c.Link))],
            (level, message) =>
            {
                _log(level, message);
                if (level == LogLevel.Error)
                {
                    refusal = message;
                }
            });
        return saved ? null : _localization.Format(refusal ?? LocalizedMessage.Create("patches.choice_not_saved"));
    }

    /// <summary>A line of the log for each choice recorded, so that the log says what changed.</summary>
    private void LogChoice(string patchName, PatchLink? link) =>
        _log(LogLevel.Info, LocalizedMessage.Create(
            "log.patch_choice",
            ("name", patchName),
            ("choice", link?.Kind switch
            {
                PatchLinkKind.Origin => LocalizedMessage.Create("patches.choice_origin", ("origin", link.OriginName ?? string.Empty)),
                PatchLinkKind.Ignored => LocalizedMessage.Create("patches.choice_ignored"),
                _ => LocalizedMessage.Create("patches.choice_automatic")
            })));

    /// <summary>What a change does to the window: the groups, the footer and the actions.</summary>
    private void AfterChange()
    {
        Regroup();
        NotifyFooter();
        NotifyActions();
    }

    private void NotifyActions()
    {
        ApplyCommand.NotifyCanExecuteChanged();
        IgnoreCommand.NotifyCanExecuteChanged();
        RestoreAutomaticCommand.NotifyCanExecuteChanged();
        WriteNameCommand.NotifyCanExecuteChanged();
        ClearNameCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSearch));
    }

    // -------------------------------------------------------------------------
    // « Accept all the suggestions »
    // -------------------------------------------------------------------------

    /// <summary>The summary of « Accept all the suggestions » replaces the detail of the patch selected.</summary>
    [ObservableProperty]
    private bool _isBatchMode;

    public BulkObservableCollection<PatchBatchEntryViewModel> BatchEntries { get; } = [];

    public int BatchCheckedCount => BatchEntries.Count(e => e.IsChecked);

    public string ApplyBatchLabel => Tr("patches.btn_batch_apply", ("count", BatchCheckedCount));

    /// <summary>The patches « Accept all the suggestions » would apply: those to match, with the ROM chosen for each.</summary>
    internal IEnumerable<(PatchRowViewModel Row, ProcessItem Origin, uint Crc32)> BatchEntriesOf(PatchGroupKind kind) =>
        kind != PatchGroupKind.ToMatch
            ? []
            : Rows
                .Where(r => r.State == PatchRowState.ToMatch && r.CanChoose && r.SelectedOrigin is not null && r.SelectedForm is not null)
                .Select(r => (r, r.SelectedOrigin!.Origin, r.SelectedForm!.Form.Crc32));

    /// <summary>Shows the summary of « Accept all the suggestions », nothing checked.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenBatch))]
    private async Task OpenBatchAsync(PatchGroupKind kind)
    {
        // The forms of the ROMs proposed are read when the window opens
        await Task.WhenAll(Rows.Select(r => r.FormsLoading));
        BatchEntries.ReplaceAll(BatchEntriesOf(kind).Select(e => new PatchBatchEntryViewModel(this, e.Row, e.Origin, e.Crc32)));
        IsBatchMode = true;
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

    /// <summary>Records the ROM of each patch checked in the summary, in one step, then goes back to the detail.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyBatch))]
    private async Task ApplyBatchAsync()
    {
        var entries = BatchEntries.Where(e => e.IsChecked).ToList();
        await ChooseAsync([.. entries.Select(e => (e.Row, (PatchLink?)LinkTo(e.Row, e.Origin, e.Crc32), (ProcessItem?)e.Origin))]);
        CloseBatch();
    }

    /// <summary>Back to the detail of the patch selected, nothing recorded.</summary>
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

    /// <summary>Patches whose choice or name changed since the window opened, or since its last simulation.</summary>
    public int ModifiedCount => Rows.Count(r => r.IsModified);

    public bool HasModified => ModifiedCount > 0;

    public string ModifiedText =>
        IsSimulating ? Tr("patches.simulating")
        : HasModified ? _localization.Format(LocalizedMessage.Count("patches.modified", ModifiedCount))
        : string.Empty;

    private void NotifyFooter()
    {
        OnPropertyChanged(nameof(ModifiedCount));
        OnPropertyChanged(nameof(HasModified));
        OnPropertyChanged(nameof(ModifiedText));
        SimulateAgainCommand.NotifyCanExecuteChanged();
    }

    // -------------------------------------------------------------------------
    // Helpers of the rows
    // -------------------------------------------------------------------------

    internal string Tr(string key, params (string Name, object? Value)[] args) => _localization.Tr(key, args);

    internal string Format(LocalizedMessage message) => _localization.Format(message);

    internal string NewName(string name, uint crc) => _naming.NameWithCrc(name, crc);

    internal string? ClearedName(string name) => _naming.NameWithoutCrc(name);

    /// <summary>
    /// A file of the last simulation as a ROM to choose, with its status as the table shows it (label and reason), and
    /// why it is proposed.
    /// </summary>
    internal PatchOriginOptionViewModel Option(ProcessItem origin, string? why = null)
    {
        var label = Tr(origin.Status switch
        {
            FileStatus.Valid => "status.valid",
            FileStatus.Excluded => "status.excluded",
            FileStatus.Duplicate => "status.duplicate",
            FileStatus.Unmatched => "status.unmatched",
            _ => "status.error"
        });
        var reason = origin.StatusReason is { } statusReason ? Format(statusReason) : string.Empty;
        return new PatchOriginOptionViewModel(origin, SourceDir, (label, reason), why);
    }

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
