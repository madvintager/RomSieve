using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.App.ViewModels;

/// <summary>One example of the live preview: a source file name and the path where the sort would write it.</summary>
public sealed record PatternPreviewExample(string SourceName, string TargetPath, string Tooltip);

/// <summary>
/// Live preview of the directory pattern: the paths where the sort would write a few files, computed by the code of
/// the sort itself (<see cref="INoIntroRedumpParser.ParseRomFilename"/> then <see cref="IPathOrganizerService.BuildTargetPath"/>
/// on the <see cref="SortConfig"/> of the current options), so that the preview cannot differ from the sort.
/// The examples are the first conforming files of the source, found by a bounded listing outside the UI thread
/// (names only: no CRC, no archive opened), or built-in examples of the convention when the source has none. With a
/// folder of the patches, a line shows where a patched version of the first example would go, in place of the last
/// example (<see cref="PatchedExampleText"/>, the pattern of the patched versions).
/// </summary>
public sealed partial class PatternPreviewViewModel : ViewModelBase
{
    /// <summary>Number of lines of the preview: the examples, the last one giving way to the line of the patched version.</summary>
    public const int ExampleCount = 3;

    /// <summary>Number of source files read at most to find the examples.</summary>
    public const int MaxListedFiles = 200;

    /// <summary>Delay after the last keystroke in the pattern or the source before the preview is refreshed.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(250);

    private static readonly string[] TosecExamples =
    [
        "Lemmings (1991)(Psygnosis)(FR)[!].adf",
        "Another World (1991)(Delphine)(Disk 1 of 2).adf",
        "Turrican II - The Final Fight (1991)(Rainbow Arts).adf"
    ];

    private static readonly string[] NoIntroExamples =
    [
        "Super Mario World (USA).sfc",
        "Legend of Zelda, The - A Link to the Past (Europe).sfc",
        "Sonic The Hedgehog (USA, Europe).md"
    ];

    private static readonly string[] RedumpExamples =
    [
        "Final Fantasy VII (Europe) (Disc 1).cue",
        "Crash Bandicoot (Europe, Australia).cue",
        "Tomb Raider (France).cue"
    ];

    // A MAME set is named by its short name: its game is only known from the DAT, so its examples are machines described
    // as in a MAME DAT, with their category in catlist.ini
    private static readonly (string Name, string Description, string Year, string Manufacturer, string Category)[] MameExamples =
    [
        ("sf2.zip", "Street Fighter II: The World Warrior (World 910522)", "1991", "Capcom", "Arcade: Fighter / Versus"),
        ("pacman.zip", "Pac-Man (Midway)", "1980", "Namco (Midway license)", "Arcade: Maze / Collect"),
        ("mslug.zip", "Metal Slug - Super Vehicle-001", "1996", "Nazca", "Arcade: Platform / Shooter Scrolling")
    ];

    // Software of a MAME software list, in the folder of their list (megadriv/sonic.zip), where MAME looks for them
    private static readonly (string Name, string Description, string Year, string Publisher)[] SoftwareListExamples =
    [
        ("megadriv/sonic.zip", "Sonic The Hedgehog (Europe, USA)", "1991", "Sega"),
        ("megadriv/sor2.zip", "Streets of Rage 2 (Europe)", "1993", "Sega"),
        ("megadriv/gunstar.zip", "Gunstar Heroes (Europe)", "1993", "Sega")
    ];

    /// <summary>Options of the directories tab that change the produced paths (the pattern is debounced apart).</summary>
    private static readonly HashSet<string> PathOptionProperties = new(StringComparer.Ordinal)
    {
        nameof(DirectoriesViewModel.SelectedNamingConvention),
        nameof(DirectoriesViewModel.DetectedDatConvention),
        nameof(DirectoriesViewModel.DestinationDir),
        nameof(DirectoriesViewModel.FlatDestination),
        nameof(DirectoriesViewModel.RestoreArticles),
        nameof(DirectoriesViewModel.SubfolderMultidisk),
        nameof(DirectoriesViewModel.GenerateM3u),
        nameof(DirectoriesViewModel.SelectedM3uPlacement),
        nameof(DirectoriesViewModel.FolderPerDiscGame),
        nameof(DirectoriesViewModel.Frontend),
        nameof(DirectoriesViewModel.SelectedTransferMode),
        nameof(DirectoriesViewModel.DatHeaderName),
        nameof(DirectoriesViewModel.DatLot),
        nameof(DirectoriesViewModel.FolderPerDat),
        nameof(DirectoriesViewModel.DatFolderMirror),
        nameof(DirectoriesViewModel.PatchDir),
        nameof(DirectoriesViewModel.ApplyPatches)
    };

    private readonly ILocalizationService _localization;
    private readonly INoIntroRedumpParser _parser;
    private readonly IPathOrganizerService _organizer;
    private readonly IMameDescriptionParser _mameParser;
    private readonly IFrontendPlatformService _frontends;
    private readonly DirectoriesViewModel _directories;
    private readonly TimeSpan _debounce;

    private IReadOnlyList<(string Name, string Path)> _sourceFiles = [];
    private CancellationTokenSource? _patternDebounce;
    private CancellationTokenSource? _sourceListing;
    private Task _pendingPattern = Task.CompletedTask;
    private Task _pendingSource = Task.CompletedTask;

    public PatternPreviewViewModel(
        ILocalizationService localization,
        INoIntroRedumpParser parser,
        IPathOrganizerService organizer,
        IMameDescriptionParser mameParser,
        IFrontendPlatformService frontends,
        DirectoriesViewModel directories,
        TimeSpan? debounce = null)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _organizer = organizer ?? throw new ArgumentNullException(nameof(organizer));
        _mameParser = mameParser ?? throw new ArgumentNullException(nameof(mameParser));
        _frontends = frontends ?? throw new ArgumentNullException(nameof(frontends));
        _directories = directories ?? throw new ArgumentNullException(nameof(directories));
        _debounce = debounce ?? DefaultDebounce;

        _directories.PropertyChanged += OnDirectoriesPropertyChanged;
        ReloadSourceFiles(debounce: false);
    }

    /// <summary>Paths produced for the examples, in the order of the source.</summary>
    public BulkObservableCollection<PatternPreviewExample> Examples { get; } = [];

    /// <summary>Whether the examples are built-in ones, the source having no conforming file (or none being chosen).</summary>
    [ObservableProperty]
    private bool _isBuiltIn = true;

    /// <summary>Whether the source is being listed: the examples shown are the previous ones until it is done.</summary>
    [ObservableProperty]
    private bool _isListingSource;

    /// <summary>Where the examples come from.</summary>
    [ObservableProperty]
    private string _captionText = string.Empty;

    /// <summary>Problems of the pattern (unknown variable, unbalanced brace, files written in <c>_unmatched</c>), or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private string _warningText = string.Empty;

    public bool HasWarning => WarningText.Length > 0;

    /// <summary>
    /// Where a patched version of the first example would be written (<c>Patched version: path</c>), or empty: without a
    /// folder of the patches nothing patched is written, a MAME set is never patched, and a refused pattern of the
    /// patched versions writes nothing (its warning says why).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPatchedExample))]
    private string _patchedExampleText = string.Empty;

    /// <summary>The name of the patched version of the example and its path, for the tooltip of its line.</summary>
    [ObservableProperty]
    private string _patchedExampleTooltip = string.Empty;

    public bool HasPatchedExample => PatchedExampleText.Length > 0;

    /// <summary>
    /// Convention used to parse the examples, as the sort does: the convention of the loaded DAT, otherwise the
    /// selected one.
    /// </summary>
    public NamingConvention ParsingConvention =>
        _directories.DetectedDatConvention is { } datConvention && datConvention != NamingConvention.Auto
            ? datConvention
            : _directories.SelectedNamingConvention;

    /// <summary>Recomputes the examples and the warnings from the current options.</summary>
    public void Refresh()
    {
        var settings = new SortSettings();
        _directories.WriteTo(settings);
        var config = SortSettingsSerializer.ToSortConfig(settings);
        if (string.IsNullOrWhiteSpace(_directories.DestinationDir))
        {
            config.DestinationDir = _localization.Tr("tab1.preview_destination");
        }

        var convention = ParsingConvention;
        var isMame = convention == NamingConvention.Mame;
        var isSoftwareList = convention == NamingConvention.MameSoftwareList;
        var examples = new List<(string Name, ParsedRomName Parsed)>(ExampleCount);
        foreach (var (name, path) in convention.IsMameSets() ? [] : _sourceFiles)
        {
            var parsed = _parser.ParseRomFilename(name, path, 0, convention);
            if (parsed.IsConform)
            {
                examples.Add((name, parsed));
                if (examples.Count == ExampleCount)
                {
                    break;
                }
            }
        }

        IsBuiltIn = examples.Count == 0;
        if (isMame)
        {
            examples.AddRange(MameExamples.Select(example => (example.Name, _mameParser.ParseDescription(
                example.Description, example.Year, example.Manufacturer, Path.GetFileNameWithoutExtension(example.Name)) with
            {
                Machine = new MameMachineInfo(false, false, false, MameDriverStatus.Good,
                    Category: MameCategoryFile.ParseCategory(example.Category))
            })));
        }
        else if (isSoftwareList)
        {
            examples.AddRange(SoftwareListExamples.Select(example => (Path.Combine(example.Name.Split('/')), _mameParser.ParseSoftware(new DatGame
            {
                Name = Path.GetFileNameWithoutExtension(example.Name),
                Description = example.Description,
                Year = example.Year,
                Publisher = example.Publisher
            }))));
        }
        else if (IsBuiltIn)
        {
            examples.AddRange(BuiltInExamples(convention).Select(name => (name, _parser.ParseRomFilename(name, null, 0, convention))));
        }

        var warnings = new List<string>();
        var writesInUnmatched = false;
        var writesInSubfolders = false;
        var writesAboveList = false;

        // The examples of a lot go into the folder of one of its DATs, as its files would
        var dat = _directories.PreviewDatPlacement(convention);

        // The line of the patched version takes the place of the last example: the preview keeps its height, and the
        // output step needs no scrolling at its default size
        var patchedWarnings = new List<string>();
        RefreshPatchedExample(settings, config, convention, examples, dat, patchedWarnings);
        if (HasPatchedExample && examples.Count == ExampleCount)
        {
            examples.RemoveAt(examples.Count - 1);
        }

        Examples.ReplaceAll(examples.Select(example =>
        {
            var target = _organizer.BuildTargetPath(example.Parsed, example.Name, config, dat: ExampleDat(dat, example.Parsed));
            var relativeParts = Path.GetRelativePath(config.DestinationDir, target)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            writesInUnmatched |= string.Equals(relativeParts[0], PathOrganizerService.UnmatchedFolderName, StringComparison.OrdinalIgnoreCase);
            writesInSubfolders |= relativeParts.Length > 1;
            writesAboveList |= relativeParts.Length > 2;
            // A software is named by its archive, which its list puts in a folder of its own
            var source = Path.GetFileName(example.Name);
            return new PatternPreviewExample(
                source,
                target,
                _localization.Tr("tab1.preview_example_tooltip", ("source", source), ("target", target)));
        }).ToList());

        if (!settings.FlatDestination)
        {
            var check = _organizer.CheckPattern(settings.Pattern);
            if (check.UnknownVariables.Count > 0)
            {
                warnings.Add(_localization.Tr("tab1.preview_unknown_variables", ("variables", string.Join(", ", check.UnknownVariables))));
            }

            if (check.HasUnbalancedBraces)
            {
                warnings.Add(_localization.Tr("tab1.preview_unbalanced_braces"));
            }

            // The runs refuse it: emptying the quarantine would delete what the sort wrote there
            if (PathOrganizerService.StartsWithReservedFolder(settings.Pattern))
            {
                warnings.Add(_localization.Tr("tab1.preview_reserved_folder"));
            }
        }

        if (writesInUnmatched)
        {
            warnings.Add(_localization.Tr("tab1.preview_unmatched_folder", ("folder", PathOrganizerService.UnmatchedFolderName)));
        }

        warnings.AddRange(patchedWarnings);

        // MAME only looks for a set at the root of its ROM folders
        if (isMame && writesInSubfolders)
        {
            warnings.Add(_localization.Tr("tab1.preview_mame_subfolders"));
        }

        // Nor for a software anywhere but in the folder of its list, at the root of its ROM folders
        if (isSoftwareList && writesAboveList)
        {
            warnings.Add(_localization.Tr("tab1.preview_softlist_subfolders"));
        }

        WarningText = string.Join(" ", warnings);
        CaptionText = isMame
            ? _localization.Tr("tab1.preview_builtin_mame")
            : isSoftwareList
            ? _localization.Tr("tab1.preview_builtin_softlist")
            : IsListingSource
            ? _localization.Tr("tab1.preview_listing")
            : !IsBuiltIn
                ? _localization.Tr("tab1.preview_from_source")
                : string.IsNullOrWhiteSpace(_directories.SourceDir)
                    ? _localization.Tr("tab1.preview_builtin_no_source")
                    : _localization.Tr("tab1.preview_builtin_no_match");
    }

    /// <summary>
    /// The DAT of an example with the folder of its platform for the frontend chosen, as the sort gives it
    /// (<see cref="IFrontendPlatformService.Destination"/>); as it is without a frontend or without a DAT.
    /// </summary>
    private DatPlacement? ExampleDat(DatPlacement? dat, ParsedRomName parsed) =>
        dat is null || _directories.Frontend == FrontendTarget.None
            ? dat
            : dat with { PlatformFolder = _frontends.Destination(dat.Name, parsed.Convention, _directories.Frontend, parsed).Folder };

    /// <summary>
    /// The line of the patched version of the first example, built by the code of the sort (the name of
    /// <see cref="PatchMatcher.ExamplePatchedName"/>, then <see cref="IPathOrganizerService.BuildTargetPath"/> as for a
    /// patched version: never a multi-disk subfolder), and the problems of the pattern of the patched versions. Only
    /// when patches are applied (<see cref="DirectoriesViewModel.UsesPatches"/>), out of MAME: the pattern is greyed out
    /// otherwise, and nothing it says is written.
    /// </summary>
    private void RefreshPatchedExample(
        SortSettings settings,
        SortConfig config,
        NamingConvention convention,
        List<(string Name, ParsedRomName Parsed)> examples,
        DatPlacement? dat,
        List<string> warnings)
    {
        var text = string.Empty;
        var tooltip = string.Empty;
        if (_directories.UsesPatches && !convention.IsMameSets())
        {
            var pattern = settings.PatchedPattern;
            var problem = PathOrganizerService.CheckPatchedPattern(pattern);
            if (problem != PatchedPatternProblem.None)
            {
                warnings.Add(_localization.Tr(ProblemKey(problem)));
            }
            else
            {
                var check = _organizer.CheckPattern(pattern, patched: true);
                if (check.UnknownVariables.Count > 0)
                {
                    warnings.Add(_localization.Tr("tab1.preview_patched_unknown_variables", ("variables", string.Join(", ", check.UnknownVariables))));
                }

                if (check.HasUnbalancedBraces)
                {
                    warnings.Add(_localization.Tr("tab1.preview_patched_unbalanced_braces"));
                }

                if (examples.Count > 0)
                {
                    var (name, parsed) = examples[0];
                    // The convention of the name, as the sort reads it: Auto is resolved by the parser
                    var patchedName = PatchMatcher.ExamplePatchedName(name, parsed.Convention);
                    var target = _organizer.BuildTargetPath(parsed, patchedName, config, isMultiMediaOverride: false, dat: ExampleDat(dat, parsed), patched: true);
                    var first = Path.GetRelativePath(config.DestinationDir, target).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                    if (!string.IsNullOrWhiteSpace(pattern) &&
                        string.Equals(first, PathOrganizerService.UnmatchedFolderName, StringComparison.OrdinalIgnoreCase))
                    {
                        warnings.Add(_localization.Tr("tab1.preview_patched_unmatched_folder", ("folder", PathOrganizerService.UnmatchedFolderName)));
                    }

                    text = _localization.Tr("tab1.preview_patched_example", ("path", target));
                    tooltip = _localization.Tr("tab1.preview_example_tooltip", ("source", Path.GetFileName(patchedName)), ("target", target));
                }
            }
        }

        PatchedExampleText = text;
        PatchedExampleTooltip = tooltip;
    }

    /// <summary>The warning of the preview for a refused pattern of the patched versions.</summary>
    private static string ProblemKey(PatchedPatternProblem problem) => problem switch
    {
        PatchedPatternProblem.Rooted => "tab1.preview_patched_rooted",
        PatchedPatternProblem.Drive => "tab1.preview_patched_drive",
        PatchedPatternProblem.ParentSegment => "tab1.preview_patched_parent_segment",
        PatchedPatternProblem.ForbiddenCharacter => "tab1.preview_patched_forbidden_character",
        PatchedPatternProblem.ReservedFolder => "tab1.preview_patched_reserved_folder",
        _ => "tab1.preview_patched_folder_variable_not_alone"
    };

    /// <summary>Completes once the pending refreshes (debounced pattern, source listing) are done; for the tests.</summary>
    internal async Task WhenIdleAsync()
    {
        while (true)
        {
            var pattern = _pendingPattern;
            var source = _pendingSource;
            await Task.WhenAll(pattern, source).ConfigureAwait(false);
            if (ReferenceEquals(pattern, _pendingPattern) && ReferenceEquals(source, _pendingSource))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Shows the files of <paramref name="directory"/> instead of those of the source, whose listing is cancelled:
    /// for the screenshots of the guide, which show sample paths instead of their temporary folders.
    /// </summary>
    internal void ShowSourceFilesForTests(string directory)
    {
        _sourceListing?.Cancel();
        _sourceFiles = ListSourceFiles(directory, _directories.IsRecursive, CancellationToken.None);
        IsListingSource = false;
        Refresh();
    }

    private static IReadOnlyList<string> BuiltInExamples(NamingConvention convention) => convention switch
    {
        NamingConvention.NoIntro => NoIntroExamples,
        NamingConvention.Redump => RedumpExamples,
        _ => TosecExamples
    };

    private void OnDirectoriesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DirectoriesViewModel.Pattern):
            case nameof(DirectoriesViewModel.PatchedPattern):
                SchedulePatternRefresh();
                break;
            case nameof(DirectoriesViewModel.SourceDir):
                ReloadSourceFiles(debounce: true);
                break;
            case nameof(DirectoriesViewModel.IsRecursive):
                ReloadSourceFiles(debounce: false);
                break;
            case { } name when PathOptionProperties.Contains(name):
                Refresh();
                break;
        }
    }

    private void SchedulePatternRefresh()
    {
        _patternDebounce?.Cancel();
        if (_debounce <= TimeSpan.Zero)
        {
            Refresh();
            return;
        }

        var cts = _patternDebounce = new CancellationTokenSource();
        _pendingPattern = RefreshAfterDelayAsync(cts.Token);
    }

    private async Task RefreshAfterDelayAsync(CancellationToken token)
    {
        try
        {
            // Resumes on the UI thread when started from it
            await Task.Delay(_debounce, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Refresh();
    }

    /// <summary>
    /// Lists the source again: a change of source cancels the listing of the previous one, whose files are no
    /// longer used.
    /// </summary>
    private void ReloadSourceFiles(bool debounce)
    {
        _sourceListing?.Cancel();
        _sourceFiles = [];

        var sourceDir = _directories.SourceDir?.Trim() ?? string.Empty;
        if (sourceDir.Length == 0)
        {
            IsListingSource = false;
            Refresh();
            return;
        }

        var cts = _sourceListing = new CancellationTokenSource();
        IsListingSource = true;
        Refresh();
        _pendingSource = ListSourceAsync(sourceDir, _directories.IsRecursive, debounce ? _debounce : TimeSpan.Zero, cts.Token);
    }

    private async Task ListSourceAsync(string sourceDir, bool recursive, TimeSpan delay, CancellationToken token)
    {
        IReadOnlyList<(string Name, string Path)> files;
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, token);
            }

            files = await Task.Run(() => ListSourceFiles(sourceDir, recursive, token), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // An unreadable source shows the built-in examples
            files = [];
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        _sourceFiles = files;
        IsListingSource = false;
        Refresh();
    }

    /// <summary>First files of the source, by name only, in path order; the marker and the quarantine of a former destination are skipped.</summary>
    private static IReadOnlyList<(string Name, string Path)> ListSourceFiles(string sourceDir, bool recursive, CancellationToken token)
    {
        if (!Directory.Exists(sourceDir))
        {
            return [];
        }

        // The files the scan lists: the files of the system are skipped, as are the unreadable folders and the links
        var files = new List<(string Name, string Path)>();
        foreach (var path in FileTree.EnumerateFiles(sourceDir, recursive, new FileTreeSkips()))
        {
            token.ThrowIfCancellationRequested();
            if (DestinationGuard.IsGuardPath(path))
            {
                continue;
            }

            files.Add((Path.GetFileName(path), path));
            if (files.Count == MaxListedFiles)
            {
                break;
            }
        }

        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        return files;
    }
}
