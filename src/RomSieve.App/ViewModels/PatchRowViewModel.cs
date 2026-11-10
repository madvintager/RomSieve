using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.App.ViewModels;

/// <summary>
/// An identified file a patch may be meant for, in the list of the ROMs of a patch or in the search, with what the
/// simulation made of it (<paramref name="status"/>: kept, excluded, duplicate, and why) and why it is proposed
/// (<paramref name="reason"/>), both already translated: the window is modal, the language cannot change while it is
/// open. A file left out by the 1G1R or a filter may be proposed: a translation is often made for a release the 1G1R
/// leaves out.
/// </summary>
public sealed class PatchOriginOptionViewModel(ProcessItem origin, string sourceDir, (string Label, string Reason) status, string? reason = null)
{
    public ProcessItem Origin { get; } = origin;

    public string Label { get; } = origin.DisplayName;

    /// <summary>Why the simulation proposes it (<see cref="PatchOriginSuggestion.Reason"/>); empty for a ROM found by the search.</summary>
    public string Reason { get; } = reason ?? string.Empty;

    public bool HasReason => Reason.Length > 0;

    /// <summary>Its status in the last simulation, as the table shows it: kept, excluded or duplicate.</summary>
    public string StatusLabel { get; } = status.Label;

    /// <summary>Why the simulation gave it that status, for the tooltip of <see cref="StatusLabel"/>.</summary>
    public string StatusReason { get; } = status.Reason;

    /// <summary>The tooltip of <see cref="StatusLabel"/>: none without a reason.</summary>
    public string? StatusTip => StatusReason.Length > 0 ? StatusReason : null;

    /// <summary>Where the file is: its archive, or its path.</summary>
    public string Location { get; } = !string.IsNullOrEmpty(origin.ArchivePath) ? origin.ArchivePath : origin.SourcePath;

    /// <summary>
    /// Its archive, or the folder of the file, in the source of the run, shown under its name: two files of the same name
    /// tell apart. Empty for a file at the root of the source.
    /// </summary>
    public string Folder { get; } = FolderIn(
        !string.IsNullOrEmpty(origin.ArchivePath) ? origin.ArchivePath : Path.GetDirectoryName(origin.SourcePath) ?? string.Empty,
        sourceDir);

    public bool HasFolder => Folder.Length > 0;

    private static string FolderIn(string folder, string sourceDir)
    {
        if (string.IsNullOrEmpty(sourceDir) || string.IsNullOrEmpty(folder))
        {
            return folder;
        }

        var relative = Path.GetRelativePath(sourceDir, folder);
        return relative == "." ? string.Empty
            : relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? folder
            : relative;
    }

    /// <summary>Name normalized for the search, once.</summary>
    internal string SearchName { get; } = SearchQuery.Normalize(origin.DisplayName);
}

/// <summary>A form of the data of an origin, with or without its copier header: the one the patch applies to.</summary>
public sealed class PatchFormOptionViewModel(PatchOriginSuggestion form, string label)
{
    public PatchOriginSuggestion Form { get; } = form;

    public string Label { get; } = label;
}

/// <summary>Where a patch stands, as far as the window knows: after the last run, then after the choices made in it.</summary>
public enum PatchRowState
{
    /// <summary>Applied to no ROM, and a ROM may be chosen for it: an IPS or an IPS32.</summary>
    ToMatch,

    /// <summary>Applied to a ROM (<see cref="PatchRowViewModel.Source"/> says what designates it).</summary>
    Applied,

    /// <summary>Set aside by the user: applied to no ROM, whatever designates one.</summary>
    Ignored,

    /// <summary>A choice of the user forgotten in the window: the next simulation tells what the patch does.</summary>
    Automatic,

    /// <summary>Applied to no ROM, and none can be chosen: a UPS or a BPS whose ROM is missing, a patch unreadable.</summary>
    Unfixable
}

/// <summary>The groups of the list of the patches, in their order.</summary>
public enum PatchGroupKind
{
    ToMatch,
    Applied,
    Automatic,
    Ignored,
    Unfixable
}

/// <summary>
/// A patch of the window of the patches: where it stands, the ROMs it may be meant for and the form of the chosen one, the
/// choice of the user recorded for it (<see cref="PatchLink"/>), and the changes made in the window (a choice, or its ROM
/// written in its name), each undone in turn.
/// </summary>
public sealed partial class PatchRowViewModel : ObservableObject
{
    private readonly PatchesViewModel _owner;

    // Forms already known for an origin: the one a readme gave, or those read once
    private readonly Dictionary<ProcessItem, IReadOnlyList<PatchOriginSuggestion>> _knownForms = new(ReferenceEqualityComparer.Instance);

    // The changes made in the window, the last on top: a choice (no renaming), or a renaming, with where the patch stood before
    private readonly Stack<(PatchRenameResult? Renaming, Snapshot Before)> _history = new();

    /// <summary>Where the patch stands: what a change replaces, and what undoing it gives back.</summary>
    private sealed record Snapshot(
        PatchFile File,
        PatchRowState State,
        PatchCrcSource? Source,
        ProcessItem? Origin,
        uint? Crc32,
        PatchLink? Link,
        LocalizedMessage? Reason,
        string? PatchedName);

    // Where the patch stood when the window opened, given back when the user forgets a choice made in the window
    private readonly Snapshot _opening;

    internal PatchRowViewModel(
        PatchesViewModel owner,
        PatchFile patch,
        PatchKey? key,
        PatchFormat? format,
        PatchRowState state,
        string location,
        PatchLink? link,
        LocalizedMessage? reason = null,
        PatchCrcSource? source = null,
        ProcessItem? currentOrigin = null,
        uint? currentCrc32 = null,
        PatchFile? note = null,
        string? patchedName = null)
    {
        _owner = owner;
        OriginalFile = patch;
        _file = patch;
        Key = key;
        Format = format;
        _state = state;
        Location = location;
        OriginalLink = link;
        Link = link;
        Reason = reason;
        Source = source;
        CurrentOrigin = currentOrigin;
        CurrentCrc32 = currentCrc32;
        Note = note;
        PatchedName = patchedName;
        _opening = Take();
    }

    /// <summary>The patch when the window opened.</summary>
    public PatchFile OriginalFile { get; }

    /// <summary>The patch as it is now: its name changes when its ROM is written in it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(IsModified))]
    private PatchFile _file;

    public string Name => File.DisplayName;

    /// <summary>What identifies it in the choices; <see langword="null"/> for a patch that could not be read.</summary>
    public PatchKey? Key { get; }

    /// <summary>Its format; <see langword="null"/> for a patch that could not be read.</summary>
    public PatchFormat? Format { get; }

    /// <summary>Its folder in the patch folder, or its archive.</summary>
    public string Location { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Group))]
    private PatchRowState _state;

    public PatchGroupKind Group => State switch
    {
        PatchRowState.ToMatch => PatchGroupKind.ToMatch,
        PatchRowState.Applied => PatchGroupKind.Applied,
        PatchRowState.Ignored => PatchGroupKind.Ignored,
        PatchRowState.Automatic => PatchGroupKind.Automatic,
        _ => PatchGroupKind.Unfixable
    };

    /// <summary>For a patch applied: what designates its ROM (what it records, its name, its readme, or the user).</summary>
    public PatchCrcSource? Source { get; private set; }

    /// <summary>Why the simulation left it out, for a patch left out.</summary>
    public LocalizedMessage? Reason { get; private set; }

    /// <summary>The readme that gave its ROM, for a patch applied thanks to it.</summary>
    public PatchFile? Note { get; }

    /// <summary>The ROM it is applied to, or that the user chose in the window.</summary>
    public ProcessItem? CurrentOrigin { get; private set; }

    /// <summary>The CRC32 of the form of <see cref="CurrentOrigin"/> it is applied to.</summary>
    public uint? CurrentCrc32 { get; private set; }

    /// <summary>The name of its patched version in the last run, as long as nothing changed it.</summary>
    public string? PatchedName { get; private set; }

    /// <summary>The choice of the user recorded for it when the window opened.</summary>
    public PatchLink? OriginalLink { get; }

    /// <summary>The choice of the user recorded for it now; <see langword="null"/>: the automatic choice.</summary>
    public PatchLink? Link { get; private set; }

    /// <summary>Whether it lies in an archive: its ROM can be chosen, but never written in its name.</summary>
    public bool IsInArchive => File.ArchiveMember is not null;

    /// <summary>Whether a ROM can be chosen for it: an IPS or an IPS32 that could be read (a UPS or a BPS checks its own).</summary>
    public bool IsIps => Key is not null && Format is PatchFormat.Ips or PatchFormat.Ips32;

    /// <summary>Whether a ROM can be chosen for it now.</summary>
    public bool CanChoose => IsIps && State != PatchRowState.Unfixable;

    /// <summary>Whether its choice or its name changed since the window opened.</summary>
    public bool IsModified => File != OriginalFile || !SameChoice(Link, OriginalLink);

    /// <summary>Two choices that make the same: the same kind and the same ROM (the names are only shown).</summary>
    internal static bool SameChoice(PatchLink? a, PatchLink? b) =>
        a is null ? b is null : b is not null && a.Kind == b.Kind && a.OriginCrc32 == b.OriginCrc32;

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
            option = _owner.Option(origin);
            Origins.Add(option);
            OnPropertyChanged(nameof(HasOrigins));
        }

        SelectedOrigin = option;
    }

    internal void AddOrigin(ProcessItem origin, string? reason = null)
    {
        Origins.Add(_owner.Option(origin, reason));
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

        // A patch applied keeps the form it is applied to, until the user chooses another one
        SetForm(Forms.FirstOrDefault(f => ReferenceEquals(origin, CurrentOrigin) && f.Form.Crc32 == CurrentCrc32) ?? Forms.FirstOrDefault());
    }

    // -------------------------------------------------------------------------
    // What it says
    // -------------------------------------------------------------------------

    private string OriginName => CurrentOrigin?.DisplayName ?? string.Empty;

    /// <summary>The short line of the list under its name: the ROM it is applied to, and what chose it.</summary>
    public string ListSummary => State != PatchRowState.Applied ? string.Empty : Source switch
    {
        PatchCrcSource.Notes => _owner.Tr("patches.row_applied_note", ("origin", OriginName)),
        PatchCrcSource.Link => _owner.Tr("patches.row_applied_link", ("origin", OriginName)),
        _ => _owner.Tr("patches.row_applied", ("origin", OriginName))
    };

    /// <summary>Where it stands, in plain words, for the detail.</summary>
    public string StatusText => State switch
    {
        PatchRowState.Applied => Source switch
        {
            PatchCrcSource.Recorded => _owner.Tr("patches.applied_by_recorded", ("origin", OriginName)),
            PatchCrcSource.Notes => _owner.Tr("patches.applied_by_note", ("origin", OriginName), ("note", Note?.DisplayName ?? string.Empty)),
            PatchCrcSource.Link => _owner.Tr("patches.applied_by_link", ("origin", OriginName)),
            _ => _owner.Tr("patches.applied_by_name", ("origin", OriginName))
        },
        PatchRowState.Ignored => _owner.Tr("patches.state_ignored"),
        PatchRowState.Automatic => _owner.Tr("patches.state_automatic"),
        PatchRowState.ToMatch when Reason is null || Reason.Key == "reason.patch_no_crc" => _owner.Tr("patches.state_to_match"),
        _ => Reason is null ? string.Empty : _owner.Format(Reason)
    };

    /// <summary>The name of its patched version, as the last run gave it; empty once a change makes it unknown.</summary>
    public string PatchedNameText => PatchedName is null ? string.Empty : _owner.Tr("patches.patched_name", ("name", PatchedName));

    public bool HasPatchedName => PatchedName is not null;

    /// <summary>The mark of its state in the list.</summary>
    public string StateMark => State switch
    {
        PatchRowState.ToMatch => "●",
        PatchRowState.Applied => "✓",
        PatchRowState.Ignored => "–",
        PatchRowState.Automatic => "↺",
        _ => "✗"
    };

    /// <summary>The states as booleans, for the colors of the theme (style classes of the view).</summary>
    public bool IsToMatch => State == PatchRowState.ToMatch;

    public bool IsApplied => State == PatchRowState.Applied;

    public bool IsUnfixable => State == PatchRowState.Unfixable;

    /// <summary>Set aside, or waiting for the next simulation: neither a confirmation nor a warning.</summary>
    public bool IsNeutral => State is PatchRowState.Ignored or PatchRowState.Automatic;

    /// <summary>Whether it lies in a folder of the patch folder, or in an archive: a patch at its root has no place to show.</summary>
    public bool HasLocation => Location.Length > 0;

    /// <summary>What a screen reader says of its line in the list: its name, and where it stands.</summary>
    public string AccessibleName => ListSummary.Length > 0 ? $"{Name} {ListSummary}" : $"{Name}, {StatusText}";

    /// <summary>Why the last action was refused; empty after a success.</summary>
    [ObservableProperty]
    private string _resultText = string.Empty;

    // -------------------------------------------------------------------------
    // The actions
    // -------------------------------------------------------------------------

    /// <summary>The ROM and the form chosen are those it is applied to: choosing them would change nothing.</summary>
    private bool IsCurrentFormChosen =>
        State == PatchRowState.Applied && SelectedForm?.Form.Crc32 == CurrentCrc32 && ReferenceEquals(SelectedOrigin?.Origin, CurrentOrigin);

    /// <summary>Whether « Apply to this ROM » can record the ROM chosen: another ROM than its own.</summary>
    public bool CanApply => CanChoose && SelectedForm is not null && !IsCurrentFormChosen;

    /// <summary>What « Apply to this ROM » does, or why it is greyed out.</summary>
    public string ApplyTooltip =>
        !CanChoose ? _owner.Tr("patches.tooltip_apply_unfixable")
        : SelectedForm is null ? _owner.Tr("patches.tooltip_apply_no_origin")
        : IsCurrentFormChosen ? _owner.Tr("patches.tooltip_apply_same_origin")
        : _owner.Tr("patches.tooltip_apply");

    /// <summary>Whether it can be set aside: any patch that could be read, not set aside yet.</summary>
    public bool CanIgnore => Key is not null && State != PatchRowState.Ignored;

    /// <summary>What « Do not apply this patch » does, or why it is greyed out.</summary>
    public string IgnoreTooltip =>
        Key is null ? _owner.Tr("patches.tooltip_no_key")
        : State == PatchRowState.Ignored ? _owner.Tr("patches.tooltip_ignore_done")
        : _owner.Tr("patches.tooltip_ignore");

    /// <summary>Whether a choice of the user is recorded for it, which « Back to the automatic choice » forgets.</summary>
    public bool CanRestoreAutomatic => Key is not null && Link is not null;

    /// <summary>
    /// The name its ROM would give it (the CRC32 of the form before its extension), for other tools: an IPS on the disk
    /// applied to a ROM its name does not give; <see langword="null"/> otherwise.
    /// </summary>
    public string? NameWithRom =>
        IsIps && !IsInArchive && State == PatchRowState.Applied && CurrentCrc32 is { } crc
        && _owner.NewName(Name, crc) is var newName && !string.Equals(newName, Name, StringComparison.OrdinalIgnoreCase)
            ? newName
            : null;

    public bool CanWriteName => NameWithRom is not null;

    public string WriteNameTooltip => NameWithRom is { } newName ? _owner.Tr("patches.tooltip_write_rom_in_name", ("name", newName)) : string.Empty;

    /// <summary>
    /// The name it would have without the ROM its name designates, for other tools: an IPS on the disk whose name gives a
    /// CRC32 (written by RomSieve or by anyone), with something left once it is removed; <see langword="null"/> otherwise.
    /// What RomSieve does with the patch does not change (<see cref="PatchesViewModel"/> records the choice first when only
    /// the name applied it).
    /// </summary>
    public string? NameWithoutRom =>
        IsIps && !IsInArchive && PatchMatcher.NameGivesCrc(Name) && _owner.ClearedName(Name) is { } newName
        && !PatchMatcher.NameGivesCrc(newName)
            ? newName
            : null;

    public bool CanClearName => NameWithoutRom is not null;

    public string ClearNameTooltip => NameWithoutRom is { } newName ? _owner.Tr("patches.tooltip_clear_rom_from_name", ("name", newName)) : string.Empty;

    /// <summary>Whether a change made in the window can be undone.</summary>
    public bool CanUndo => _history.Count > 0;

    /// <summary>The last change made in the window: a renaming, or <see langword="null"/> for a choice.</summary>
    internal PatchRenameResult? LastRenaming => _history.Count > 0 ? _history.Peek().Renaming : null;

    /// <summary>The choice the last change replaced, which undoing it records again.</summary>
    internal PatchLink? LinkBeforeLastChange => _history.Count > 0 ? _history.Peek().Before.Link : null;

    private Snapshot Take() => new(File, State, Source, CurrentOrigin, CurrentCrc32, Link, Reason, PatchedName);

    private void Restore(Snapshot snapshot)
    {
        File = snapshot.File;
        State = snapshot.State;
        Source = snapshot.Source;
        CurrentOrigin = snapshot.Origin;
        CurrentCrc32 = snapshot.Crc32;
        Link = snapshot.Link;
        Reason = snapshot.Reason;
        PatchedName = snapshot.PatchedName;
    }

    /// <summary>A choice recorded: the patch now stands as the choice says.</summary>
    internal void Chosen(PatchLink? link, ProcessItem? origin)
    {
        _history.Push((null, Take()));
        Link = link;
        PatchedName = null;
        Reason = null;
        switch (link?.Kind)
        {
            case PatchLinkKind.Origin:
                State = PatchRowState.Applied;
                Source = PatchCrcSource.Link;
                CurrentOrigin = origin;
                CurrentCrc32 = link.OriginCrc32;
                break;
            case PatchLinkKind.Ignored:
                State = PatchRowState.Ignored;
                Source = null;
                CurrentOrigin = null;
                CurrentCrc32 = null;
                break;
            default:
                // Without a choice at the opening, the window knows what the automatic choice gives: the opening state
                if (OriginalLink is null)
                {
                    Restore(_opening with { File = File });
                }
                else
                {
                    State = PatchRowState.Automatic;
                    Source = null;
                    CurrentOrigin = null;
                    CurrentCrc32 = null;
                }

                break;
        }

        ResultText = string.Empty;
        NotifyState();
    }

    /// <summary>Its ROM written in its name: its new name, nothing else changes.</summary>
    internal void Renamed(PatchRenameResult renaming)
    {
        _history.Push((renaming, Take()));
        File = new PatchFile(renaming.NewPath!);
        ResultText = string.Empty;
        NotifyState();
    }

    /// <summary>The last change undone: the patch stands where it stood before.</summary>
    internal void Undone()
    {
        var (_, before) = _history.Pop();
        Restore(before);
        ResultText = string.Empty;
        NotifyState();
    }

    /// <summary>Raises every text and state that depends on the choices, the state or the language.</summary>
    internal void NotifyState()
    {
        foreach (var name in new[]
        {
            nameof(CanChoose), nameof(ListSummary), nameof(StatusText), nameof(StateMark), nameof(IsToMatch), nameof(IsApplied),
            nameof(IsUnfixable), nameof(IsNeutral), nameof(AccessibleName), nameof(CanApply), nameof(ApplyTooltip),
            nameof(CanIgnore), nameof(IgnoreTooltip), nameof(CanRestoreAutomatic), nameof(NameWithRom), nameof(CanWriteName),
            nameof(WriteNameTooltip), nameof(NameWithoutRom), nameof(CanClearName), nameof(ClearNameTooltip), nameof(CanUndo), nameof(IsInArchive), nameof(Source), nameof(CurrentOrigin),
            nameof(Link), nameof(IsModified), nameof(PatchedNameText), nameof(HasPatchedName)
        })
        {
            OnPropertyChanged(name);
        }
    }
}
