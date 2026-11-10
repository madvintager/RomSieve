using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.App.ViewModels;

/// <summary>An identified file a patch may be meant for, in the list of the ROMs of a patch or in the search.</summary>
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

/// <summary>Where a patch stands: what its name (or its readme) does with it, as far as the window knows.</summary>
public enum PatchRowState
{
    /// <summary>Applied to no ROM, and a name that designates one would fix it: an IPS without CRC32 in its name.</summary>
    ToMatch,

    /// <summary>Applied to the ROM the CRC32 of its name designates.</summary>
    AppliedByName,

    /// <summary>Applied to the ROM its readme designates.</summary>
    AppliedByNote,

    /// <summary>Applied to no ROM, and no name would fix it: a UPS or a BPS, a missing ROM, a patch unreadable.</summary>
    Unfixable
}

/// <summary>The groups of the list of the patches, in their order.</summary>
public enum PatchGroupKind
{
    ToMatch,
    Applied,
    Unfixable
}

/// <summary>
/// A patch of the window of the patches: where it stands, the ROMs it may be meant for and the form of the chosen one,
/// what applying it would rename, and the renamings done in the window, each undone in turn.
/// </summary>
public sealed partial class PatchRowViewModel : ObservableObject
{
    private readonly PatchesViewModel _owner;

    // Forms already known for an origin: the two a readme gave, or those read once
    private readonly Dictionary<ProcessItem, IReadOnlyList<PatchOriginSuggestion>> _knownForms = new(ReferenceEqualityComparer.Instance);

    // The renamings done in the window, the last on top, with where the patch stood before each
    private readonly Stack<(PatchRenameResult Renaming, Snapshot Before)> _history = new();

    private sealed record Snapshot(PatchFile File, PatchRowState State, ProcessItem? Origin, uint? Crc32);

    internal PatchRowViewModel(
        PatchesViewModel owner,
        PatchFile patch,
        PatchRowState state,
        LocalizedMessage? reason,
        string location,
        ProcessItem? currentOrigin = null,
        uint? currentCrc32 = null,
        PatchFile? note = null)
    {
        _owner = owner;
        OriginalFile = patch;
        _file = patch;
        _state = state;
        Reason = reason;
        Location = location;
        CurrentOrigin = currentOrigin;
        CurrentCrc32 = currentCrc32;
        Note = note;
    }

    /// <summary>The patch when the window opened.</summary>
    public PatchFile OriginalFile { get; }

    /// <summary>The patch as it is now: its name changes with each renaming.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(IsModified))]
    private PatchFile _file;

    public string Name => File.DisplayName;

    /// <summary>Its folder in the patch folder, or its archive.</summary>
    public string Location { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Group))]
    private PatchRowState _state;

    public PatchGroupKind Group => State switch
    {
        PatchRowState.ToMatch => PatchGroupKind.ToMatch,
        PatchRowState.Unfixable => PatchGroupKind.Unfixable,
        _ => PatchGroupKind.Applied
    };

    /// <summary>Why the simulation left it out, for a patch left out.</summary>
    public LocalizedMessage? Reason { get; }

    /// <summary>The readme that gave its ROM, for a patch applied thanks to it.</summary>
    public PatchFile? Note { get; }

    /// <summary>The ROM its name (or its readme) designates, for a patch applied or renamed in the window.</summary>
    public ProcessItem? CurrentOrigin { get; private set; }

    /// <summary>The CRC32 of the form of <see cref="CurrentOrigin"/> its name (or its readme) gives.</summary>
    public uint? CurrentCrc32 { get; private set; }

    /// <summary>Whether it lies in an archive, which is never modified: its CRC32 is only shown.</summary>
    public bool IsInArchive => File.ArchiveMember is not null;

    /// <summary>Whether a ROM can be chosen for it: any IPS but those no name would fix.</summary>
    public bool CanChoose => State != PatchRowState.Unfixable;

    /// <summary>Whether its name changed since the window opened.</summary>
    public bool IsModified => File != OriginalFile;

    // -------------------------------------------------------------------------
    // The ROM and its form
    // -------------------------------------------------------------------------

    /// <summary>ROMs proposed (the suggestions of the simulation, or the ROM it is applied to), then those taken from the search.</summary>
    public ObservableCollection<PatchOriginOptionViewModel> Origins { get; } = [];

    public bool HasOrigins => Origins.Count > 0;

    private PatchOriginOptionViewModel? _selectedOrigin;

    /// <summary>
    /// The ROM chosen. A choice is never emptied from outside: a list bound to the selected patch writes an empty choice
    /// into the patch it leaves when another patch is selected, which would lose its ROM.
    /// </summary>
    public PatchOriginOptionViewModel? SelectedOrigin
    {
        get => _selectedOrigin;
        set
        {
            if (value is null || !SetProperty(ref _selectedOrigin, value))
            {
                return;
            }

            Forms.Clear();
            SetForm(null);
            OnPropertyChanged(nameof(HasSeveralForms));
            FormsLoading = LoadFormsAsync(value.Origin);
        }
    }

    /// <summary>The forms of the ROM chosen: one, or two with and without its copier header.</summary>
    public ObservableCollection<PatchFormOptionViewModel> Forms { get; } = [];

    private PatchFormOptionViewModel? _selectedForm;

    /// <summary>
    /// The form chosen of the ROM. As for <see cref="SelectedOrigin"/>, an empty choice from outside is ignored: only a
    /// new ROM empties it, until its forms are read.
    /// </summary>
    public PatchFormOptionViewModel? SelectedForm
    {
        get => _selectedForm;
        set
        {
            if (value is not null)
            {
                SetForm(value);
            }
        }
    }

    /// <summary>Whether the ROM chosen has two forms, to choose from; otherwise the form is not shown.</summary>
    public bool HasSeveralForms => Forms.Count > 1;

    /// <summary>The forms of the ROM are being read (the other form reads the file again).</summary>
    internal Task FormsLoading { get; private set; } = Task.CompletedTask;

    private void SetForm(PatchFormOptionViewModel? value)
    {
        if (SetProperty(ref _selectedForm, value, nameof(SelectedForm)))
        {
            NotifyState();
        }
    }

    internal void KnowForms(ProcessItem origin, IReadOnlyList<PatchOriginSuggestion> forms) => _knownForms[origin] = forms;

    /// <summary>Adds a ROM to its list (once), and chooses it.</summary>
    internal void Choose(ProcessItem origin)
    {
        var option = Origins.FirstOrDefault(o => ReferenceEquals(o.Origin, origin));
        if (option is null)
        {
            option = new PatchOriginOptionViewModel(origin);
            Origins.Add(option);
            OnPropertyChanged(nameof(HasOrigins));
        }

        SelectedOrigin = option;
    }

    internal void AddOrigin(ProcessItem origin)
    {
        Origins.Add(new PatchOriginOptionViewModel(origin));
        OnPropertyChanged(nameof(HasOrigins));
    }

    private async Task LoadFormsAsync(ProcessItem origin)
    {
        if (!_knownForms.TryGetValue(origin, out var forms))
        {
            forms = await _owner.ReadFormsAsync(origin);
            _knownForms[origin] = forms;
        }

        // The user may have chosen another ROM meanwhile
        if (!ReferenceEquals(SelectedOrigin?.Origin, origin))
        {
            return;
        }

        foreach (var form in forms)
        {
            Forms.Add(new PatchFormOptionViewModel(form, _owner.FormLabel(form)));
        }

        OnPropertyChanged(nameof(HasSeveralForms));

        // A patch applied keeps the form it was applied to, until the user chooses another one
        SetForm(Forms.FirstOrDefault(f => ReferenceEquals(origin, CurrentOrigin) && f.Form.Crc32 == CurrentCrc32) ?? Forms.FirstOrDefault());
    }

    // -------------------------------------------------------------------------
    // What it says
    // -------------------------------------------------------------------------

    /// <summary>The short line of the list under its name: the ROM it is applied to, or nothing.</summary>
    public string ListSummary => State switch
    {
        PatchRowState.AppliedByName => _owner.Tr("patches.row_applied", ("origin", CurrentOrigin?.DisplayName ?? string.Empty)),
        PatchRowState.AppliedByNote => _owner.Tr("patches.row_applied_note", ("origin", CurrentOrigin?.DisplayName ?? string.Empty)),
        _ => string.Empty
    };

    /// <summary>Where it stands, in plain words, for the detail.</summary>
    public string StatusText => State switch
    {
        PatchRowState.AppliedByName when _history.Count > 0 =>
            _owner.Tr("patches.state_renamed", ("origin", CurrentOrigin?.DisplayName ?? string.Empty)),
        PatchRowState.AppliedByName => _owner.Tr("patches.applied_by_name", ("origin", CurrentOrigin?.DisplayName ?? string.Empty)),
        PatchRowState.AppliedByNote => _owner.Tr(
            "patches.applied_by_note",
            ("origin", CurrentOrigin?.DisplayName ?? string.Empty),
            ("note", Note?.DisplayName ?? string.Empty)),
        PatchRowState.ToMatch when _history.Count > 0 => _owner.Tr("patches.state_detached"),
        PatchRowState.ToMatch when Reason is null || Reason.Key == "reason.patch_no_crc" => _owner.Tr("patches.state_to_match"),
        _ => Reason is null ? string.Empty : _owner.Format(Reason)
    };

    /// <summary>Whether the state is a warning (applied to no ROM) rather than a confirmation.</summary>
    public bool IsWarning => State is PatchRowState.ToMatch or PatchRowState.Unfixable;

    /// <summary>The mark of its state in the list: to match, applied, or unfixable.</summary>
    public string StateMark => State switch
    {
        PatchRowState.ToMatch => "●",
        PatchRowState.Unfixable => "✗",
        _ => "✓"
    };

    /// <summary>Why the last action was refused; empty after a success.</summary>
    [ObservableProperty]
    private string _resultText = string.Empty;

    // -------------------------------------------------------------------------
    // The actions
    // -------------------------------------------------------------------------

    /// <summary>The form chosen is the one its name already gives: renaming would change nothing.</summary>
    private bool IsCurrentFormChosen =>
        State == PatchRowState.AppliedByName && SelectedForm?.Form.Crc32 == CurrentCrc32 && ReferenceEquals(SelectedOrigin?.Origin, CurrentOrigin);

    /// <summary>The form chosen is the one its readme gives: applying writes it in its name.</summary>
    private bool IsNoteFormChosen =>
        State == PatchRowState.AppliedByNote && SelectedForm?.Form.Crc32 == CurrentCrc32 && ReferenceEquals(SelectedOrigin?.Origin, CurrentOrigin);

    /// <summary>Whether « Apply to this ROM » can rename it: a ROM chosen, on the disk, another ROM than its own.</summary>
    public bool CanApply => CanChoose && !IsInArchive && SelectedForm is not null && !IsCurrentFormChosen;

    public string ApplyLabel => _owner.Tr(IsNoteFormChosen ? "patches.btn_write_rom_in_name" : "patches.btn_apply");

    /// <summary>What « Apply to this ROM » does, or why it is greyed out.</summary>
    public string ApplyTooltip =>
        !CanChoose ? _owner.Tr("patches.tooltip_apply_unfixable")
        : IsInArchive ? _owner.Tr("patches.tooltip_apply_archive")
        : SelectedForm is null ? _owner.Tr("patches.tooltip_apply_no_origin")
        : IsCurrentFormChosen ? _owner.Tr("patches.tooltip_apply_same_origin")
        : IsNoteFormChosen ? _owner.Tr("patches.tooltip_write_rom_in_name")
        : _owner.Tr("patches.tooltip_apply");

    /// <summary>The name before and after « Apply to this ROM », when it can rename the patch.</summary>
    public string PreviewNewName => CanApply ? _owner.NewName(Name, SelectedForm!.Form.Crc32) : string.Empty;

    public bool HasPreview => CanApply;

    /// <summary>For a patch in an archive, never renamed: the CRC32 to write in its name once extracted.</summary>
    public string ArchiveHint => IsInArchive && CanChoose && SelectedForm is { } form
        ? _owner.Tr("patches.in_archive", ("crc", PatchesViewModel.FormatCrc(form.Form.Crc32)))
        : string.Empty;

    /// <summary>Whether it can stop applying to its ROM: applied from its name, on the disk.</summary>
    public bool CanDetach => State == PatchRowState.AppliedByName && !IsInArchive;

    /// <summary>What « Stop applying to this ROM » does, or why it is greyed out.</summary>
    public string DetachTooltip =>
        State == PatchRowState.AppliedByNote ? _owner.Tr("patches.tooltip_detach_note")
        : State != PatchRowState.AppliedByName ? _owner.Tr("patches.tooltip_detach_unapplied")
        : IsInArchive ? _owner.Tr("patches.tooltip_apply_archive")
        : _owner.Tr("patches.tooltip_detach");

    /// <summary>Whether a renaming done in the window can be undone.</summary>
    public bool CanUndo => _history.Count > 0;

    internal PatchRenameResult? LastRenaming => _history.Count > 0 ? _history.Peek().Renaming : null;

    /// <summary>What a renaming of the window made of the patch: its new name, and the ROM its name now designates.</summary>
    internal void Renamed(PatchRenameResult renaming, ProcessItem? origin)
    {
        _history.Push((renaming, new Snapshot(File, State, CurrentOrigin, CurrentCrc32)));
        File = new PatchFile(renaming.NewPath!);
        if (renaming.Request.Crc32 is { } crc)
        {
            State = PatchRowState.AppliedByName;
            CurrentOrigin = origin;
            CurrentCrc32 = crc;
        }
        else
        {
            State = PatchRowState.ToMatch;
            CurrentOrigin = null;
            CurrentCrc32 = null;
        }

        ResultText = string.Empty;
        NotifyState();
    }

    /// <summary>The last renaming undone: the patch takes back its name and where it stood before.</summary>
    internal void Undone()
    {
        var (_, before) = _history.Pop();
        File = before.File;
        State = before.State;
        CurrentOrigin = before.Origin;
        CurrentCrc32 = before.Crc32;
        ResultText = string.Empty;
        NotifyState();
    }

    /// <summary>Raises every text and state that depends on the choices, the state or the language.</summary>
    internal void NotifyState()
    {
        foreach (var name in new[]
        {
            nameof(CanChoose), nameof(ListSummary), nameof(StatusText), nameof(IsWarning), nameof(StateMark), nameof(CanApply), nameof(ApplyLabel),
            nameof(ApplyTooltip), nameof(PreviewNewName), nameof(HasPreview), nameof(ArchiveHint), nameof(CanDetach),
            nameof(DetachTooltip), nameof(CanUndo), nameof(IsInArchive)
        })
        {
            OnPropertyChanged(name);
        }
    }
}
