using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RomSieve.App.Abstractions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Directories tab: naming convention and its badge, DAT file, source and destination, transfer and collision
/// modes, directory pattern with its variable and preset menus and its live preview, and multi-disk playlists.
/// </summary>
public sealed partial class DirectoriesViewModel : ConfigSectionViewModel
{
    public const string DefaultPattern = SortSettings.DefaultPattern;
    public const string NoIntroRegionPattern = "{country}/{title}/";
    public const string NoIntroLetterPattern = "{letter}/{title}/";

    /// <summary>A folder per Amiga chipset (AGA, OCS-AGA, OCS-ECS), then by letter.</summary>
    public const string AmigaChipsetPattern = "{chipset}/{letter}/{title}/";

    private static readonly (string Tag, string LabelKey, string TipKey)[] CommonPatternVariableSpecs =
    [
        ("{title}", "var.title", "var.tooltip_title"),
        ("{letter}", "var.letter", "var.tooltip_letter"),
        ("{country}", "var.country", "var.tooltip_country"),
        ("{language}", "var.language", "var.tooltip_language"),
        ("{version}", "var.version", "var.tooltip_version")
    ];

    private static readonly (string Tag, string LabelKey, string TipKey)[] TosecPatternVariableSpecs =
    [
        ("{publisher}", "var.publisher", "var.tooltip_publisher"),
        ("{year}", "var.year", "var.tooltip_year"),
        ("{system}", "var.system", "var.tooltip_system"),
        ("{chipset}", "var.chipset", "var.tooltip_chipset"),
        ("{video}", "var.video", "var.tooltip_video"),
        ("{dump_status}", "var.dump_status", "var.tooltip_dump_status")
    ];

    // Category of a MAME machine, from the category file
    private static readonly (string Tag, string LabelKey, string TipKey)[] MamePatternVariableSpecs =
    [
        ("{genre}", "var.genre", "var.tooltip_genre"),
        ("{category}", "var.category", "var.tooltip_category")
    ];

    // The DAT of a file: last, so that the menu keeps the index of the other variables
    private static readonly (string Tag, string LabelKey, string TipKey)[] DatPatternVariableSpecs =
    [
        ("{dat}", "var.dat", "var.tooltip_dat")
    ];

    // Folders of the original ROM, in the pattern of the patched versions only: after the variables of the main pattern
    private static readonly (string Tag, string LabelKey, string TipKey, PatternVariableScope Scope)[] FolderPatternVariableSpecs =
    [
        ("{origin_folder}", "var.origin_folder", "var.tooltip_origin_folder", PatternVariableScope.All),
        ("{dat_folder}", "var.dat_folder", "var.tooltip_dat_folder", PatternVariableScope.DatFolder)
    ];

    private readonly ILocalizationService _localization;
    private readonly IDialogService _dialogService;
    private readonly IDatService _datService;
    private readonly INoIntroRedumpParser _noIntroParser;
    private bool _suppressSourceDirChangeReset;

    public DirectoriesViewModel(
        ILocalizationService localization,
        IDialogService dialogService,
        IDatService datService,
        INoIntroRedumpParser noIntroParser,
        IPathOrganizerService pathOrganizer,
        IMameDescriptionParser mameParser,
        IFrontendPlatformService frontends,
        TimeSpan? previewDebounce = null)
    {
        _localization = localization;
        _dialogService = dialogService;
        _datService = datService;
        _noIntroParser = noIntroParser;
        _datInfoText = string.Empty;
        DatLot = new DatLotViewModel(localization, frontends, pathOrganizer);
        DatLot.ExcludedChanged += (_, _) =>
        {
            _syncingLot = true;
            try
            {
                ExcludedDats = DatLot.ExcludedNames;
            }
            finally
            {
                _syncingLot = false;
            }

            ShowLotInfo();
        };

        NamingConventionOptions = new LocalizedOptionList<NamingConvention>(
            localization,
            (NamingConvention.Auto, "tab1.convention_auto"),
            (NamingConvention.Tosec, "tab1.convention_tosec"),
            (NamingConvention.NoIntro, "tab1.convention_nointro"),
            (NamingConvention.Redump, "tab1.convention_redump"),
            (NamingConvention.Mame, "tab1.convention_mame"),
            (NamingConvention.MameSoftwareList, "tab1.convention_softlist"));
        CollisionModeOptions = new LocalizedOptionList<CollisionMode>(
            localization,
            (CollisionMode.Rename, "tab1.collision_rename"),
            (CollisionMode.Skip, "tab1.collision_skip"),
            (CollisionMode.Overwrite, "tab1.collision_overwrite"));
        M3uPlacementOptions = new LocalizedOptionList<M3uPlacementMode>(
            localization,
            (M3uPlacementMode.Parent, "tab1.m3u_placement_parent"),
            (M3uPlacementMode.ParentHidden, "tab1.m3u_placement_parent_hidden"),
            (M3uPlacementMode.Subfolder, "tab1.m3u_placement_subfolder"),
            (M3uPlacementMode.GameFolder, "tab1.m3u_placement_game_folder"));
        FrontendOptions = new LocalizedOptionList<FrontendTarget>(
            localization,
            (FrontendTarget.None, "tab1.frontend_none"),
            (FrontendTarget.Batocera, "tab1.frontend_batocera"),
            (FrontendTarget.RetroPie, "tab1.frontend_retropie"),
            (FrontendTarget.EsDe, "tab1.frontend_es_de"),
            (FrontendTarget.Recalbox, "tab1.frontend_recalbox"),
            (FrontendTarget.MiSTer, "tab1.frontend_mister"));
        HashingParallelismOptions = new LocalizedOptionList<int>(
            localization,
            (1, "tab1.hashing_parallelism_1"),
            (2, "tab1.hashing_parallelism_2"),
            (4, "tab1.hashing_parallelism_4"),
            (8, "tab1.hashing_parallelism_8"),
            (SortConfig.AutoHashingParallelism, "tab1.hashing_parallelism_auto"));

        PatternVariables = CreatePatternVariables(InsertVariableAtCaret);
        PatchedPatternVariables =
        [
            .. CreatePatternVariables(InsertPatchedVariableAtCaret),
            .. FolderPatternVariableSpecs.Select(spec => CreatePatternVariable((spec.Tag, spec.LabelKey, spec.TipKey), spec.Scope, InsertPatchedVariableAtCaret))
        ];
        UpdatePatternVariables();
        TransferModeOptions =
        [
            new(localization, TransferMode.Copy, "tab1.radio_copy", "tab1.mode_hint_copy", "tab1.tooltip_mode_copy"),
            new(localization, TransferMode.Move, "tab1.radio_move", "tab1.mode_hint_move", "tab1.tooltip_mode_move"),
            new(localization, TransferMode.HardLink, "tab1.radio_hardlink", "tab1.mode_hint_hardlink", "tab1.tooltip_mode_hardlink"),
            new(localization, TransferMode.SymbolicLink, "tab1.radio_symlink", "tab1.mode_hint_symlink", "tab1.tooltip_mode_symlink"),
            new(localization, TransferMode.Zip, "tab1.radio_zip", "tab1.mode_hint_zip", "tab1.tooltip_mode_zip")
        ];
        _transferModeHint = TransferModeHintFor(SelectedTransferMode);

        var defaults = new SortSettings();
        Advanced = new AdvancedOptionsViewModel(
            localization,
            this,
            new AdvancedOption(nameof(ScanArchives), "tab1.chk_scan_archives", () => ScanArchives != defaults.ScanArchives),
            new AdvancedOption(nameof(VerifySha1), "tab1.chk_verify_sha1", () => VerifySha1 != defaults.VerifySha1),
            new AdvancedOption(nameof(VerifyArchiveContents), "tab1.chk_verify_archive_contents", () => VerifyArchiveContents != defaults.VerifyArchiveContents),
            new AdvancedOption(nameof(MatchChdByName), "tab1.chk_match_chd_by_name", () => MatchChdByName != defaults.MatchChdByName));

        // Last: the preview reads the options above
        Preview = new PatternPreviewViewModel(localization, noIntroParser, pathOrganizer, mameParser, frontends, this, previewDebounce);
    }

    /// <summary>Live preview of the paths produced by the pattern.</summary>
    public PatternPreviewViewModel Preview { get; }

    /// <summary>
    /// Advanced options of the collection step (archives, SHA-1, CHD by name: how the files are identified), shown in a
    /// drop-down panel. The verification of the copies is a visible option of the output step, and the place of the
    /// playlists stays next to the option that enables it.
    /// </summary>
    public AdvancedOptionsViewModel Advanced { get; }

    /// <summary>The DATs of a lot (a folder of No-Intro, Redump or TOSEC DATs), to check; empty for any other DAT.</summary>
    public DatLotViewModel DatLot { get; }

    /// <summary>Header name of the DAT chosen (the name of the folder for a folder of DATs), the value of <c>{dat}</c>; empty without a DAT.</summary>
    [ObservableProperty]
    private string _datHeaderName = string.Empty;

    /// <summary>
    /// The DAT the examples of the preview of the pattern are sorted with: for a lot, its first DAT checked of the
    /// convention of the examples (with its folder, mirrored or not); the DAT chosen otherwise; none without a DAT.
    /// </summary>
    internal DatPlacement? PreviewDatPlacement(NamingConvention convention)
    {
        if (DatLot.IsLot)
        {
            var entry = DatLot.Entries.Where(e => e.IsIncluded).OrderBy(e => e.Convention == convention ? 0 : 1).FirstOrDefault();
            return entry is null
                ? null
                : new DatPlacement(entry.Name, Path.GetDirectoryName(entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)), IsLotMember: true);
        }

        return string.IsNullOrWhiteSpace(DatHeaderName) ? null : new DatPlacement(DatHeaderName);
    }

    // The DATs checked are being written to ExcludedDats, which must not check them again
    private bool _syncingLot;

    /// <summary>
    /// Whether the DAT chosen is a folder: its subfolders may then be read (<see cref="DatRecursive"/>), and the folder
    /// options of the output step apply to a lot.
    /// </summary>
    public bool IsDatFolder => !string.IsNullOrWhiteSpace(DatFilePath) && Directory.Exists(DatFilePath.Trim());

    /// <summary>
    /// Whether the DAT chosen is read as a folder of DATs (<see cref="IDatService.HoldsSeveralDats"/>): a folder, or an
    /// archive that does not hold exactly one DAT. The folder options of the output step then apply to a lot. Known once
    /// the path is chosen, without reading the archive again at each binding.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDatFolderMirrorEnabled))]
    [NotifyPropertyChangedFor(nameof(IsFolderPerDatEnabled))]
    private bool _holdsSeveralDats;

    /// <summary>Raised when the user changes the source directory (not while a configuration is being loaded).</summary>
    public event EventHandler? SourceDirChangedByUser;

    /// <summary>
    /// Raised each time the effective convention (selected, or detected from the DAT in Auto mode) is evaluated:
    /// the filters tab shows the options of that convention.
    /// </summary>
    public event EventHandler<NamingConvention>? EffectiveConventionChanged;

    public LocalizedOptionList<NamingConvention> NamingConventionOptions { get; }
    /// <summary>Transfer modes of the list, each with the sentence that says what it does.</summary>
    public IReadOnlyList<TransferModeOptionViewModel> TransferModeOptions { get; }

    public LocalizedOptionList<CollisionMode> CollisionModeOptions { get; }
    public LocalizedOptionList<M3uPlacementMode> M3uPlacementOptions { get; }

    /// <summary>Frontends the destination may be organized for, none first.</summary>
    public LocalizedOptionList<FrontendTarget> FrontendOptions { get; }
    public LocalizedOptionList<int> HashingParallelismOptions { get; }

    /// <summary>
    /// Entries of the "Insert a variable" menu: the variables of every convention, then those of TOSEC names, then those
    /// of the MAME categories. All are always shown; those without value with the effective convention are greyed out
    /// (<see cref="PatternVariableViewModel.IsApplicable"/>, <see cref="AreTosecVariablesApplicable"/>,
    /// <see cref="AreMameVariablesApplicable"/>).
    /// </summary>
    public IReadOnlyList<PatternVariableViewModel> PatternVariables { get; }

    /// <summary>
    /// Caret position in the pattern field, where a variable is inserted; -1 when the user has not placed it since
    /// the pattern was last replaced (a variable is then added as a new folder at the end).
    /// </summary>
    public int PatternCaretIndex { get; set; } = -1;

    /// <summary>
    /// Entries of the "Insert a variable" menu of the pattern of the patched versions: those of the main pattern, in the
    /// same order, then <c>{origin_folder}</c> and <c>{dat_folder}</c>, which this pattern alone knows.
    /// </summary>
    public IReadOnlyList<PatternVariableViewModel> PatchedPatternVariables { get; }

    /// <summary>Caret position in the field of the pattern of the patched versions (see <see cref="PatternCaretIndex"/>).</summary>
    public int PatchedPatternCaretIndex { get; set; } = -1;

    /// <summary>Convention that the settings steps follow: the selected one, or the one detected from the DAT in Auto mode.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreTosecVariablesApplicable), nameof(IsMameConvention), nameof(AreMameVariablesApplicable))]
    private NamingConvention _effectiveConvention = NamingConvention.Auto;

    /// <summary>Folder of the CHDs of a MAME collection, read in addition to the source; empty: none.</summary>
    [ObservableProperty]
    private string _chdDir = string.Empty;

    /// <summary>
    /// Folder of the patches (IPS, IPS32, UPS, BPS), read in addition to the source: each patch whose original ROM is
    /// identified gives a patched version. Empty: none. Always shown, whatever the convention.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPatchDir), nameof(UsesPatches), nameof(IsPatchedPatternEnabled))]
    private string _patchDir = string.Empty;

    /// <summary>
    /// Whether the patches of <see cref="PatchDir"/> are applied (<see cref="SortConfig.ApplyPatches"/>): unchecked, the
    /// folder stays in the settings for later, and the options of the patched versions are greyed out. Checked again
    /// when a folder is chosen with Browse.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesPatches), nameof(IsPatchedPatternEnabled))]
    private bool _applyPatches = true;

    /// <summary>Whether only the patched versions are written, not the original ROMs they replace.</summary>
    [ObservableProperty]
    private bool _patchOnly;

    /// <summary>
    /// Pattern of the folders of the patched versions, from the root of the destination; empty: next to their original
    /// ROM (<see cref="SortConfig.PatchedPattern"/>).
    /// </summary>
    [ObservableProperty]
    private string _patchedPattern = string.Empty;

    /// <summary>Whether a patch folder is given.</summary>
    public bool HasPatchDir => !string.IsNullOrWhiteSpace(PatchDir);

    /// <summary>
    /// Whether the run applies patches: a patch folder is given and <see cref="ApplyPatches"/> is checked
    /// (<see cref="SortConfig.UsesPatches"/>). The option to write the patched versions only is greyed out otherwise.
    /// </summary>
    public bool UsesPatches => ApplyPatches && HasPatchDir;

    /// <summary>
    /// Whether the pattern of the patched versions can be edited: only when patches are applied, since nothing patched
    /// is written otherwise. Greyed out, it keeps its value, and its tooltip says when it applies.
    /// </summary>
    public bool IsPatchedPatternEnabled => !IsRunning && UsesPatches;

    /// <summary>
    /// Whether the effective convention is MAME: the MAME files (CHD folder, categories, ratings) are then read, and the
    /// CHD folder checked against the destination. They are always shown, kept for the next MAME sort otherwise.
    /// </summary>
    public bool IsMameConvention => EffectiveConvention == NamingConvention.Mame;

    /// <summary>Category file of the MAME machines (catver.ini, catlist.ini, genre.ini); empty: none.</summary>
    [ObservableProperty]
    private string _mameCategoryFile = string.Empty;

    /// <summary>Rating file of the MAME machines (bestgames.ini); empty: none.</summary>
    [ObservableProperty]
    private string _mameRatingFile = string.Empty;

    /// <summary>Whether the variables of the MAME categories have a value: only for MAME machines.</summary>
    public bool AreMameVariablesApplicable => EffectiveConvention == NamingConvention.Mame;

    /// <summary>
    /// Whether the variables of TOSEC names have a value: the effective convention is TOSEC or not known yet. They have
    /// none in No-Intro and Redump names, nor for MAME.
    /// </summary>
    public bool AreTosecVariablesApplicable =>
        EffectiveConvention is NamingConvention.Auto or NamingConvention.Tosec || DatLot.Includes(NamingConvention.Tosec);

    /// <summary>What the selected transfer mode does, shown under the modes.</summary>
    [ObservableProperty]
    private string _transferModeHint;

    [ObservableProperty]
    private NamingConvention _selectedNamingConvention = NamingConvention.Auto;

    [ObservableProperty]
    private NamingConvention? _detectedDatConvention;

    /// <summary>
    /// Parent / clone relations of the chosen DAT: present as soon as its first games have a clone, otherwise known once
    /// the whole DAT is read. Set before <see cref="EffectiveConventionChanged"/> is raised.
    /// </summary>
    [ObservableProperty]
    private DatCloneRelations _datCloneRelations = DatCloneRelations.NoDat;

    // What the names of the files of the chosen DAT tell, once the whole DAT is read; null before, without a DAT, or for
    // a lot (its DATs are read by their headers only)
    private DatNameTraits? _datNameTraitsRead;

    /// <summary>
    /// What the names of the files of the chosen DAT tell the 1G1R criteria read from a name
    /// (<see cref="TiebreakApplicability.GetDatNameTraits"/>), once the whole DAT is read; <see langword="null"/> when not
    /// known. With <see cref="MatchChdByName"/>, a CHD keeps its own extension: the formats of the DAT are then not
    /// claimed. Set before <see cref="EffectiveConventionChanged"/> is raised.
    /// </summary>
    public DatNameTraits? DatNameTraits =>
        _datNameTraitsRead is { } traits && MatchChdByName ? traits with { HasOneMediaFormat = false } : _datNameTraitsRead;

    /// <summary>
    /// Mechanical machines of the chosen DAT, known once the whole DAT is read; <c>null</c> before, without a DAT, or
    /// when the DAT has none.
    /// </summary>
    [ObservableProperty]
    private MameDatSummary? _datMameSummary;

    [ObservableProperty]
    private string _conventionBadgeText = string.Empty;

    /// <summary>Theme tone of the convention badge: <c>Auto</c>, <c>Tosec</c>, <c>NoIntro</c> or <c>Redump</c>.</summary>
    [ObservableProperty]
    private string _conventionBadgeTone = "Auto";

    [ObservableProperty]
    private string _datFilePath = string.Empty;

    [ObservableProperty]
    private string _datInfoText;

    /// <summary>Theme tone of the DAT summary: <c>Neutral</c>, <c>Ok</c> or <c>Error</c>.</summary>
    [ObservableProperty]
    private string _datInfoTone = "Neutral";

    /// <summary>
    /// Whether a folder of DATs is being read for its DATs (<see cref="DatLot"/>): the simulation and the sort wait for
    /// its end, since the DATs of the lot and their boxes are not known yet. The DAT can still be changed, which drops
    /// the read; a single DAT, read for its summary only, never sets it.
    /// </summary>
    [ObservableProperty]
    private bool _isReadingDatFolder;

    /// <summary>DATs of the folder read so far (<see cref="IsReadingDatFolder"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DatFolderReadText))]
    [NotifyPropertyChangedFor(nameof(IsDatFolderReadCountKnown))]
    private int _datFolderReadCount;

    /// <summary>DATs of the folder being read, 0 while the folder is listed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DatFolderReadText))]
    [NotifyPropertyChangedFor(nameof(IsDatFolderReadCountKnown))]
    private int _datFolderReadTotal;

    /// <summary>Whether the number of DATs of the folder is known: until then, the progress bar runs without a value.</summary>
    public bool IsDatFolderReadCountKnown => DatFolderReadTotal > 0;

    /// <summary>The DATs read out of the DATs of the folder, next to the progress bar; empty while the folder is listed.</summary>
    public string DatFolderReadText => DatFolderReadTotal > 0
        ? _localization.Tr("tab1.dat_folder_read_count", ("read", DatFolderReadCount), ("total", DatFolderReadTotal))
        : string.Empty;

    [ObservableProperty]
    private string _sourceDir = string.Empty;

    [ObservableProperty]
    private string _destinationDir = string.Empty;

    [ObservableProperty]
    private bool _isRecursive = true;

    [ObservableProperty]
    private bool _scanArchives = true;

    [ObservableProperty]
    private bool _appendToDestination;

    /// <summary>Number of files hashed at once, or <see cref="SortConfig.AutoHashingParallelism"/>.</summary>
    [ObservableProperty]
    private int _hashingParallelism = 1;

    /// <summary>Whether the SHA-1 provided by the DAT is checked for the files identified by CRC32.</summary>
    [ObservableProperty]
    private bool _verifySha1;

    /// <summary>Whether the data of every archive member is checked against the CRC32 its archive records.</summary>
    [ObservableProperty]
    private bool _verifyArchiveContents;

    /// <summary>Whether a CHD is matched with the DAT game of the same name when the DAT describes tracks (Redump).</summary>
    [ObservableProperty]
    private bool _matchChdByName;

    /// <summary>Whether a folder of DATs is read with its subfolders.</summary>
    [ObservableProperty]
    private bool _datRecursive;

    /// <summary>Header names of the DATs of a lot left out of the sort; replaced as a whole on each change.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _excludedDats = [];

    /// <summary>Whether each DAT of a lot gets a folder of its own in the destination.</summary>
    [ObservableProperty]
    private bool _folderPerDat = true;

    /// <summary>Whether the folder of each DAT of a lot is placed under the folders of its path.</summary>
    [ObservableProperty]
    private bool _datFolderMirror;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTransferCopy))]
    [NotifyPropertyChangedFor(nameof(IsTransferMove))]
    [NotifyPropertyChangedFor(nameof(IsTransferHardLink))]
    [NotifyPropertyChangedFor(nameof(IsTransferSymbolicLink))]
    [NotifyPropertyChangedFor(nameof(IsTransferZip))]
    [NotifyPropertyChangedFor(nameof(CanPreserveArchives))]
    private TransferMode _selectedTransferMode = TransferMode.Copy;

    partial void OnSelectedTransferModeChanged(TransferMode value) => TransferModeHint = TransferModeHintFor(value);

    public bool IsTransferCopy
    {
        get => SelectedTransferMode == TransferMode.Copy;
        set { if (value) SelectedTransferMode = TransferMode.Copy; }
    }

    public bool IsTransferMove
    {
        get => SelectedTransferMode == TransferMode.Move;
        set { if (value) SelectedTransferMode = TransferMode.Move; }
    }

    public bool IsTransferHardLink
    {
        get => SelectedTransferMode == TransferMode.HardLink;
        set { if (value) SelectedTransferMode = TransferMode.HardLink; }
    }

    public bool IsTransferSymbolicLink
    {
        get => SelectedTransferMode == TransferMode.SymbolicLink;
        set { if (value) SelectedTransferMode = TransferMode.SymbolicLink; }
    }

    public bool IsTransferZip
    {
        get => SelectedTransferMode == TransferMode.Zip;
        set { if (value) SelectedTransferMode = TransferMode.Zip; }
    }

    public bool CanPreserveArchives => SelectedTransferMode != TransferMode.Zip;

    [ObservableProperty]
    private CollisionMode _selectedCollisionMode = CollisionMode.Rename;

    [ObservableProperty]
    private bool _verifyCopies;

    [ObservableProperty]
    private bool _preserveArchives;

    /// <summary>Whether the sort writes the non-conforming files in <c>_unmatched</c> of the destination.</summary>
    [ObservableProperty]
    private bool _isolateUnmatched = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPatternEditingEnabled))]
    [NotifyPropertyChangedFor(nameof(IsPatternLayout))]
    [NotifyPropertyChangedFor(nameof(IsRootLayout))]
    private bool _flatDestination;

    /// <summary>
    /// Choices of the organization of the destination, as radio buttons: folders from the pattern, or everything at the
    /// root (<see cref="FlatDestination"/>, saved apart from the pattern). Setting false is ignored, as the other choice
    /// sets it.
    /// </summary>
    public bool IsPatternLayout
    {
        get => !FlatDestination;
        set
        {
            if (value)
            {
                FlatDestination = false;
            }
        }
    }

    public bool IsRootLayout
    {
        get => FlatDestination;
        set
        {
            if (value)
            {
                FlatDestination = true;
            }
        }
    }

    [ObservableProperty]
    private string _pattern = DefaultPattern;

    [ObservableProperty]
    private bool _restoreArticles = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsM3uPlacementEnabled))]
    private bool _subfolderMultidisk = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsM3uPlacementEnabled))]
    private bool _generateM3u;

    [ObservableProperty]
    private M3uPlacementMode _selectedM3uPlacement = M3uPlacementMode.Parent;

    /// <summary>Every game on a disc image in a folder of its own (<see cref="SortConfig.FolderPerDiscGame"/>).</summary>
    [ObservableProperty]
    private bool _folderPerDiscGame;

    /// <summary>Frontend the destination is organized for (<see cref="SortConfig.Frontend"/>).</summary>
    [ObservableProperty]
    private FrontendTarget _frontend;

    public bool IsPatternEditingEnabled => !IsRunning && !FlatDestination;
    public bool IsM3uPlacementEnabled => !IsRunning && GenerateM3u && SubfolderMultidisk;

    protected override void OnRunningStateChanged()
    {
        OnPropertyChanged(nameof(IsPatternEditingEnabled));
        OnPropertyChanged(nameof(IsPatchedPatternEnabled));
        OnPropertyChanged(nameof(IsM3uPlacementEnabled));
    }

    partial void OnSelectedNamingConventionChanged(NamingConvention value) => UpdateEffectiveConvention();

    // A CHD matched by its name keeps its own extension: the 1G1R criteria of the formats are greyed out again, or no more
    partial void OnMatchChdByNameChanged(bool value)
    {
        if (_datNameTraitsRead is not null)
        {
            UpdateEffectiveConvention();
        }
    }

    partial void OnDatFilePathChanged(string value)
    {
        OnPropertyChanged(nameof(IsDatFolder));
        HoldsSeveralDats = _datService.HoldsSeveralDats(value.Trim());
        UpdateDatStatusInfo(value);
        UpdatePatternVariables();
    }

    // The mirror only acts with a folder per DAT, as {dat_folder}
    partial void OnFolderPerDatChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDatFolderMirrorEnabled));
        UpdatePatternVariables();
    }

    /// <summary>
    /// Whether the folders of the DATs may be mirrored: a folder or an archive of DATs, each with a folder of its own,
    /// without a frontend, whose folder of the platform takes the place of these folders.
    /// </summary>
    public bool IsDatFolderMirrorEnabled => HoldsSeveralDats && FolderPerDat && Frontend == FrontendTarget.None;

    /// <summary>Whether each DAT of a lot may have a folder of its own: a lot, without a frontend.</summary>
    public bool IsFolderPerDatEnabled => HoldsSeveralDats && Frontend == FrontendTarget.None;

    /// <summary>Tooltip of « A folder per DAT », which says why it has no effect with a frontend.</summary>
    public string FolderPerDatTooltip => _localization.Tr(Frontend == FrontendTarget.None
        ? "tab1.tooltip_folder_per_dat"
        : "tab1.tooltip_folder_per_dat_frontend");

    /// <summary>Tooltip of « Mirror the folders of the DATs », which says why it has no effect with a frontend.</summary>
    public string DatFolderMirrorTooltip => _localization.Tr(Frontend == FrontendTarget.None
        ? "tab1.tooltip_dat_folder_mirror"
        : "tab1.tooltip_folder_per_dat_frontend");

    // A frontend puts each DAT under the folder of its platform: the folders of the DATs no longer apply, {dat_folder}
    // gives that folder, and the window of the lot shows it. Chosen by the user, it offers its recommended settings
    partial void OnFrontendChanged(FrontendTarget value)
    {
        OnPropertyChanged(nameof(IsFolderPerDatEnabled));
        OnPropertyChanged(nameof(IsDatFolderMirrorEnabled));
        OnPropertyChanged(nameof(FolderPerDatTooltip));
        OnPropertyChanged(nameof(DatFolderMirrorTooltip));
        UpdatePatternVariables();
        DatLot.ShowFrontend(value);
        if (!_readingSettings)
        {
            RecommendationTask = OfferRecommendedSettingsAsync(value);
        }
    }

    // A configuration or a profile being read: its frontend comes with its own settings, which are not questioned
    private bool _readingSettings;

    /// <summary>The offer of the recommended settings of the last frontend chosen by the user (for the tests).</summary>
    internal Task RecommendationTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Offers the output settings <paramref name="frontend"/> reads best (<see cref="FrontendRecommendation.For"/>),
    /// listing those that would change; nothing when none would. Accepted, they are applied; refused, the frontend stays
    /// chosen with the settings as they are. The pattern, the filters and the transfer mode are never touched.
    /// </summary>
    private async Task OfferRecommendedSettingsAsync(FrontendTarget frontend)
    {
        if (FrontendRecommendation.For(frontend) is not { } recommended)
        {
            return;
        }

        var on = _localization.Tr("dialog.frontend_settings_on");
        var off = _localization.Tr("dialog.frontend_settings_off");
        var changes = new List<string>();
        void Change(string settingKey, string value) =>
            changes.Add(_localization.Tr("dialog.frontend_settings_change", ("setting", _localization.Tr(settingKey)), ("value", value)));

        if (recommended.FlatDestination is { } flat && flat != FlatDestination)
        {
            Change("tab1.lbl_layout", _localization.Tr(flat ? "tab1.chk_flat_destination" : "tab1.layout_pattern"));
        }

        if (recommended.SubfolderMultidisk != SubfolderMultidisk)
        {
            Change("tab1.chk_subfolder_multidisk", recommended.SubfolderMultidisk ? on : off);
        }

        if (recommended.GenerateM3u != GenerateM3u)
        {
            Change("tab1.chk_generate_m3u", recommended.GenerateM3u ? on : off);
        }

        // The placement only acts on playlists of games in a subfolder
        var placementApplies = recommended.GenerateM3u && recommended.SubfolderMultidisk;
        if (placementApplies && recommended.M3uPlacement is { } placement && placement != SelectedM3uPlacement)
        {
            Change("tab1.lbl_m3u_placement", M3uPlacementOptions.First(o => o.Value == placement).Label);
        }

        if (recommended.FolderPerDiscGame != FolderPerDiscGame)
        {
            Change("tab1.chk_folder_per_disc_game", recommended.FolderPerDiscGame ? on : off);
        }

        if (changes.Count == 0)
        {
            return;
        }

        var accepted = await _dialogService.ShowConfirmationAsync(
            _localization.Tr("dialog.frontend_settings_title", ("frontend", frontend.DisplayName())),
            _localization.Tr("dialog.frontend_settings_message", ("frontend", frontend.DisplayName()), ("changes", string.Join(Environment.NewLine, changes))));

        // The user may have chosen another frontend while the question was shown
        if (!accepted || Frontend != frontend)
        {
            return;
        }

        if (recommended.FlatDestination is { } flatDestination)
        {
            FlatDestination = flatDestination;
        }

        SubfolderMultidisk = recommended.SubfolderMultidisk;
        GenerateM3u = recommended.GenerateM3u;
        if (placementApplies && recommended.M3uPlacement is { } m3uPlacement)
        {
            SelectedM3uPlacement = m3uPlacement;
        }

        FolderPerDiscGame = recommended.FolderPerDiscGame;
    }

    // The subfolders change the DATs of the lot
    partial void OnDatRecursiveChanged(bool value)
    {
        if (IsDatFolder)
        {
            UpdateDatStatusInfo(DatFilePath);
        }
    }

    // A configuration or a profile loaded checks the DATs it names
    partial void OnExcludedDatsChanged(IReadOnlyList<string> value)
    {
        if (!_syncingLot && DatLot.IsLot)
        {
            DatLot.ApplyExcluded(value);
            ShowLotInfo();
        }
    }

    partial void OnSourceDirChanged(string value)
    {
        if (!_suppressSourceDirChangeReset)
        {
            SourceDirChangedByUser?.Invoke(this, EventArgs.Empty);
        }
    }


    // =========================================================================
    // Commands: file and folder pickers
    // =========================================================================

    [RelayCommand]
    public async Task BrowseSourceAsync()
    {
        var startDir = SourceDir?.Trim() ?? string.Empty;
        var folder = await _dialogService.PickFolderAsync(_localization.Tr("dialog.browse_source_title"), startDir);
        if (!string.IsNullOrEmpty(folder))
        {
            SourceDir = folder;
        }
    }

    [RelayCommand]
    public async Task BrowseChdDirAsync()
    {
        var startDir = ChdDir?.Trim() ?? string.Empty;
        var folder = await _dialogService.PickFolderAsync(_localization.Tr("dialog.browse_chd_title"), startDir);
        if (!string.IsNullOrEmpty(folder))
        {
            ChdDir = folder;
        }
    }

    [RelayCommand]
    public async Task BrowsePatchDirAsync()
    {
        var startDir = PatchDir?.Trim() ?? string.Empty;
        var folder = await _dialogService.PickFolderAsync(_localization.Tr("dialog.browse_patch_title"), startDir);
        if (!string.IsNullOrEmpty(folder))
        {
            // Choosing a folder of patches asks for them
            PatchDir = folder;
            ApplyPatches = true;
        }
    }

    [RelayCommand]
    public async Task BrowseMameCategoryFileAsync()
    {
        var current = MameCategoryFile?.Trim() ?? string.Empty;
        var startDir = string.IsNullOrEmpty(current) ? string.Empty : Path.GetDirectoryName(current) ?? string.Empty;
        FileDialogFilter[] filters =
        [
            new(_localization.Tr("dialog.browse_categories_filter"), ["ini"]),
            FileDialogFilter.AllFiles(_localization)
        ];

        var file = await _dialogService.PickOpenFileAsync(_localization.Tr("dialog.browse_categories_title"), filters, startDir);
        if (!string.IsNullOrEmpty(file))
        {
            MameCategoryFile = file;
        }
    }

    [RelayCommand]
    public async Task BrowseMameRatingFileAsync()
    {
        var current = MameRatingFile?.Trim() ?? string.Empty;
        var startDir = string.IsNullOrEmpty(current) ? string.Empty : Path.GetDirectoryName(current) ?? string.Empty;
        FileDialogFilter[] filters =
        [
            new(_localization.Tr("dialog.browse_ratings_filter"), ["ini"]),
            FileDialogFilter.AllFiles(_localization)
        ];

        var file = await _dialogService.PickOpenFileAsync(_localization.Tr("dialog.browse_ratings_title"), filters, startDir);
        if (!string.IsNullOrEmpty(file))
        {
            MameRatingFile = file;
        }
    }

    [RelayCommand]
    public async Task BrowseDestinationAsync()
    {
        var startDir = DestinationDir?.Trim() ?? string.Empty;
        var folder = await _dialogService.PickFolderAsync(_localization.Tr("dialog.browse_dest_title"), startDir);
        if (!string.IsNullOrEmpty(folder))
        {
            DestinationDir = folder;
        }
    }

    [RelayCommand]
    public async Task BrowseDatAsync()
    {
        var currentPath = DatFilePath?.Trim() ?? string.Empty;
        var startDir = string.Empty;
        if (!string.IsNullOrEmpty(currentPath))
        {
            if (Directory.Exists(currentPath))
            {
                startDir = currentPath;
            }
            else
            {
                var parent = Path.GetDirectoryName(currentPath);
                startDir = !string.IsNullOrEmpty(parent) ? parent : currentPath;
            }
        }

        FileDialogFilter[] filters =
        [
            new(_localization.Tr("dialog.browse_dat_filter"), ["dat", "xml", "zip", "7z", "rar"]),
            FileDialogFilter.AllFiles(_localization)
        ];

        var file = await _dialogService.PickOpenFileAsync(_localization.Tr("dialog.browse_dat_title"), filters, startDir);
        if (string.IsNullOrEmpty(file) || file == DatFilePath)
        {
            return;
        }

        // A new archive of several DATs is a lot, whose DATs open to check once read, as for a folder
        await BrowseToDatAsync(file);
    }

    /// <summary>
    /// Chooses a folder of DATs: a lot of No-Intro, Redump or TOSEC DATs, or the DATs of several MAME software lists,
    /// sorted together. A new lot opens its DATs to check, once read, all of them unchecked (not the same folder chosen
    /// again, nor a lot a configuration, a profile or the subfolders option reads, which keep their boxes).
    /// </summary>
    [RelayCommand]
    public async Task BrowseDatFolderAsync()
    {
        var currentPath = DatFilePath?.Trim() ?? string.Empty;
        var startDir = Directory.Exists(currentPath) ? currentPath : Path.GetDirectoryName(currentPath) ?? string.Empty;
        var folder = await _dialogService.PickFolderAsync(_localization.Tr("dialog.browse_dat_folder_title"), startDir);
        if (string.IsNullOrEmpty(folder) || folder == DatFilePath)
        {
            return;
        }

        await BrowseToDatAsync(folder);
    }

    // A DAT browsed to is being read: its lot opens below, not at the end of its read (LoadDatFolderAsync)
    private bool _browsingToDat;

    /// <summary>
    /// Reads the DAT the user just browsed to; a lot then opens its DATs, none of them checked: a pack holds hundreds of
    /// DATs, of which the user picks those to sort with. Cancel leaves them unchecked, as they were at the opening.
    /// </summary>
    private async Task BrowseToDatAsync(string path)
    {
        _browsingToDat = true;
        try
        {
            DatFilePath = path;
            await DatLoadTask;
        }
        finally
        {
            _browsingToDat = false;
        }

        if (DatLot.IsLot && DatFilePath == path)
        {
            ExcludedDats = DatLot.Entries.Select(e => e.Name).ToList();
            await ChooseLotDatsAsync();
        }
    }

    /// <summary>
    /// Shows the DATs of the lot to check, all of them (no search, no choice of the DATs shown). The boxes change the
    /// lot at once; Cancel puts back the DATs left out at the opening.
    /// </summary>
    [RelayCommand]
    public async Task ChooseLotDatsAsync()
    {
        if (!DatLot.IsLot)
        {
            return;
        }

        var excludedAtOpening = ExcludedDats;
        DatLot.ShowEverything();
        if (!await _dialogService.ShowDatLotAsync(DatLot))
        {
            // The same list when nothing changed: no change, so no notification
            ExcludedDats = excludedAtOpening;
        }
    }

    // Clearing a field is the same as emptying it by hand: every effect of the change follows (for the source, a change
    // by the user, which forgets the manual 1G1R choices)

    [RelayCommand]
    public void ClearDat()
    {
        DatFilePath = string.Empty;
    }

    [RelayCommand]
    public void ClearSource() => SourceDir = string.Empty;

    [RelayCommand]
    public void ClearDestination() => DestinationDir = string.Empty;

    [RelayCommand]
    public void ClearChdDir() => ChdDir = string.Empty;

    [RelayCommand]
    public void ClearPatchDir() => PatchDir = string.Empty;

    [RelayCommand]
    public void ClearMameCategoryFile() => MameCategoryFile = string.Empty;

    [RelayCommand]
    public void ClearMameRatingFile() => MameRatingFile = string.Empty;

    // =========================================================================
    // Commands: pattern templates and variables
    // =========================================================================

    [RelayCommand]
    public void ResetPattern() => ApplyPreset(DefaultPattern);

    [RelayCommand]
    public void PresetNoIntroRegion() => ApplyPreset(NoIntroRegionPattern);

    [RelayCommand]
    public void PresetNoIntroLetter() => ApplyPreset(NoIntroLetterPattern);

    [RelayCommand]
    public void PresetAmigaChipset() => ApplyPreset(AmigaChipsetPattern);

    /// <summary>
    /// Applies a preset pattern, which leaves the output at the root of the destination. The option "At the root"
    /// is <see cref="FlatDestination"/>: it disables the pattern without clearing it, since both are saved apart.
    /// </summary>
    private void ApplyPreset(string pattern)
    {
        FlatDestination = false;
        Pattern = pattern;
        PatternCaretIndex = -1;
    }

    /// <summary>
    /// Inserts a variable or segment directly at the end of the <see cref="Pattern"/> mask.
    /// </summary>
    public void InsertVariable(string variable)
    {
        if (string.IsNullOrEmpty(variable))
        {
            return;
        }

        Pattern = $"{Pattern}{variable}";
    }

    /// <summary>
    /// Inserts a variable at the caret of the pattern field (<see cref="PatternCaretIndex"/>), and places the caret
    /// after it. Without a caret placed by the user, the variable becomes a new folder at the end of the pattern.
    /// </summary>
    public void InsertVariableAtCaret(string tag)
    {
        if (string.IsNullOrEmpty(tag) || !IsPatternEditingEnabled)
        {
            return;
        }

        (Pattern, PatternCaretIndex) = InsertAtCaret(Pattern, PatternCaretIndex, tag);
    }

    /// <summary>
    /// Inserts a variable at the caret of the field of the pattern of the patched versions
    /// (<see cref="PatchedPatternCaretIndex"/>), as <see cref="InsertVariableAtCaret"/> does in the main pattern.
    /// </summary>
    public void InsertPatchedVariableAtCaret(string tag)
    {
        if (string.IsNullOrEmpty(tag) || !IsPatchedPatternEnabled)
        {
            return;
        }

        (PatchedPattern, PatchedPatternCaretIndex) = InsertAtCaret(PatchedPattern, PatchedPatternCaretIndex, tag);
    }

    /// <summary>
    /// A pattern with <paramref name="tag"/> inserted at <paramref name="caret"/>, and the caret after it; without a caret
    /// in the pattern, the tag becomes a new folder at its end.
    /// </summary>
    private static (string Pattern, int Caret) InsertAtCaret(string? pattern, int caret, string tag)
    {
        var current = pattern ?? string.Empty;
        if (caret < 0 || caret > current.Length)
        {
            var prefix = current.Length == 0 || current.EndsWith('/') ? current : $"{current}/";
            return ($"{prefix}{tag}/", prefix.Length + tag.Length);
        }

        return (current.Insert(caret, tag), caret + tag.Length);
    }

    // =========================================================================
    // Commands: presets of the pattern of the patched versions
    // =========================================================================

    /// <summary>The patched versions next to their original ROM: an empty pattern.</summary>
    [RelayCommand]
    public void PresetPatchedNextToOrigin() => ApplyPatchedPreset(string.Empty);

    /// <summary>A subfolder of the folder of the original ROM: <c>{origin_folder}/Patches</c>, the folder name translated.</summary>
    [RelayCommand]
    public void PresetPatchedSubfolder() =>
        ApplyPatchedPreset($"{{{PathOrganizerService.OriginFolderVariable}}}/{_localization.Tr("tab1.patched_folder_name")}");

    /// <summary>A folder at the root of the destination, by DAT then by title: <c>Patches/{dat}/{title}</c>.</summary>
    [RelayCommand]
    public void PresetPatchedRoot() => ApplyPatchedPreset($"{_localization.Tr("tab1.patched_folder_name")}/{{dat}}/{{title}}");

    private void ApplyPatchedPreset(string pattern)
    {
        PatchedPattern = pattern;
        PatchedPatternCaretIndex = -1;
    }

    // =========================================================================
    // DAT information and convention badge
    // =========================================================================

    public void UpdateConventionBadge(NamingConvention? detectedConvention = null)
    {
        if (detectedConvention.HasValue)
        {
            DetectedDatConvention = detectedConvention.Value;
        }

        UpdateEffectiveConvention();
    }

    /// <summary>
    /// Updates the variable buttons and the convention badge after a configuration was loaded, without switching steps.
    /// </summary>
    public void RefreshConventionState()
    {
        UpdateEffectiveConvention();
    }

    // Read of the DAT under way: a DAT chosen again before its end wins, the older read is dropped
    private int _datLoadVersion;
    private CancellationTokenSource? _datLoadCts;

    // Message of the DAT information line, translated again when the language changes (the DAT is not read again)
    private LocalizedMessage? _datInfoMessage;

    /// <summary>Read of the whole DAT for its summary (completed when there is nothing to read): awaited by the tests.</summary>
    internal Task DatLoadTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Shows what the DAT is at once, from its header and its first games (<see cref="IDatService.ReadDatPreview"/>):
    /// the convention badge and the MAME frames appear as soon as it is chosen. The whole DAT, hundreds of megabytes
    /// for MAME, is then read off the UI thread for its summary (<see cref="DatLoadTask"/>). A folder is read off the UI
    /// thread from the start (<see cref="LoadDatFolderAsync"/>).
    /// </summary>
    private void UpdateDatStatusInfo(string pathStr)
    {
        var version = ++_datLoadVersion;
        _datLoadCts?.Cancel();
        _datLoadCts?.Dispose();
        _datLoadCts = null;
        DatLoadTask = Task.CompletedTask;
        IsReadingDatFolder = false;

        var cleanPath = pathStr?.Trim() ?? string.Empty;
        DatHeaderName = string.Empty;
        if (string.IsNullOrEmpty(cleanPath))
        {
            DatLot.Clear();
            ShowDatInfo(null, "Neutral", null, DatCloneRelations.NoDat);
            return;
        }

        // A folder holds the DATs of several MAME software lists
        if (!File.Exists(cleanPath) && !Directory.Exists(cleanPath))
        {
            DatLot.Clear();
            ShowDatInfo(LocalizedMessage.Create("tab1.dat_info_error"), "Error", null, DatCloneRelations.Unknown);
            return;
        }

        DatLot.Clear();

        // A folder (or an archive) of DATs, a lot or the DATs of MAME software lists, is listed and read off the UI
        // thread: it may hold thousands of DATs, or be on the network. It is named after itself (an archive without its
        // extension), whatever its first DAT says.
        if (HoldsSeveralDats)
        {
            var folderName = Directory.Exists(cleanPath)
                ? Path.GetFileName(Path.TrimEndingDirectorySeparator(cleanPath))
                : Path.GetFileNameWithoutExtension(cleanPath);
            DatHeaderName = folderName;
            ShowDatInfo(LocalizedMessage.Create("tab1.dat_info_reading", ("name", folderName)), "Neutral", null, DatCloneRelations.Unknown);
            DatFolderReadCount = 0;
            DatFolderReadTotal = 0;
            IsReadingDatFolder = true;
            _datLoadCts = new CancellationTokenSource();
            DatLoadTask = LoadDatFolderAsync(cleanPath, DatRecursive, version, _datLoadCts.Token);
            return;
        }

        DatCatalog preview;
        try
        {
            preview = _datService.ReadDatPreview(cleanPath);
        }
        catch (Exception ex)
        {
            ShowDatInfo(DatErrorMessage(ex), "Error", null, DatCloneRelations.Unknown);
            return;
        }

        DatHeaderName = DatDisplayName(preview, cleanPath);
        ShowDatSummaryReading(preview, cleanPath);
        _datLoadCts = new CancellationTokenSource();
        DatLoadTask = LoadDatSummaryAsync(cleanPath, version, _datLoadCts.Token);
    }

    // What the first games of a DAT read whole for its summary tell, until its end
    private void ShowDatSummaryReading(DatCatalog preview, string path) =>
        ShowDatInfo(
            LocalizedMessage.Create("tab1.dat_info_reading", ("name", DatDisplayName(preview, path))),
            "Neutral",
            DetectConvention(preview),
            // The first games only prove that the DAT has clones: their absence waits for the whole DAT
            TiebreakApplicability.GetCloneRelations(preview) == DatCloneRelations.Present ? DatCloneRelations.Present : DatCloneRelations.Unknown);

    private async Task LoadDatSummaryAsync(string path, int version, CancellationToken cancellationToken)
    {
        DatCatalog? catalog = null;
        MameDatSummary? mameSummary = null;
        DatNameTraits? nameTraits = null;
        Exception? error = null;
        try
        {
            (catalog, mameSummary, nameTraits) = await Task.Run(async () =>
            {
                var read = await _datService.ParseDatFileAsync(path, cancellationToken);
                return (read, MameDatSummary.FromCatalog(read), TiebreakApplicability.GetDatNameTraits(read));
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (version != _datLoadVersion)
        {
            return;
        }

        if (catalog is null)
        {
            ShowDatInfo(DatErrorMessage(error), "Error", null, DatCloneRelations.Unknown);
            return;
        }

        // The whole DAT has the last word on the convention, should its first games have said otherwise
        ShowDatInfo(
            catalog.SoftwareLists is { } lists
                ? LocalizedMessage.Create(
                    "tab1.dat_info_summary_lists",
                    ("name", DatDisplayName(catalog, path)),
                    ("lists", lists.Count),
                    ("games", catalog.Games.Count),
                    ("count", catalog.Entries.Count))
                : LocalizedMessage.Create(
                    "tab1.dat_info_summary",
                    ("name", DatDisplayName(catalog, path)),
                    ("version", !string.IsNullOrEmpty(catalog.Header.Version) ? catalog.Header.Version : "N/A"),
                    ("count", catalog.Entries.Count),
                    ("games", catalog.Games.Count)),
            "Ok",
            DetectConvention(catalog),
            TiebreakApplicability.GetCloneRelations(catalog),
            mameSummary,
            nameTraits);
    }

    /// <summary>
    /// A folder of DATs, off the UI thread: its first DAT tells whether it holds MAME software lists, read whole for their
    /// summary (<see cref="LoadDatSummaryAsync"/>); otherwise it is a lot, whose DATs are read by their headers only
    /// (<see cref="IDatService.ReadDatFolderPreview"/>, its progress shown), refused as a sort would refuse them
    /// (<see cref="Core.Services.DatService.CheckDatFolder"/>), then shown to check with the summary of the lot.
    /// </summary>
    private async Task LoadDatFolderAsync(string path, bool recursive, int version, CancellationToken cancellationToken)
    {
        // Created on the UI thread, which hears the reports; a report of an older read, or one late after the end, is dropped
        var progress = new Progress<DatFolderReadProgress>(report =>
        {
            if (version == _datLoadVersion && IsReadingDatFolder)
            {
                DatFolderReadTotal = report.Total;
                DatFolderReadCount = report.Read;
            }
        });

        DatCatalog? preview = null;
        IReadOnlyList<DatFolderEntry>? entries = null;
        Exception? error = null;
        try
        {
            (preview, entries) = await Task.Run(() =>
            {
                var first = _datService.ReadDatPreview(path);
                if (first.Header.Convention == NamingConvention.MameSoftwareList)
                {
                    return (first, (IReadOnlyList<DatFolderEntry>?)null);
                }

                var read = _datService.ReadDatFolderPreview(path, recursive, progress, cancellationToken);
                Core.Services.DatService.CheckDatFolder(path, read);
                return (first, read);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (version != _datLoadVersion)
        {
            return;
        }

        IsReadingDatFolder = false;
        if (preview is null)
        {
            DatHeaderName = string.Empty;
            ShowDatInfo(DatErrorMessage(error), "Error", null, DatCloneRelations.Unknown);
            return;
        }

        if (entries is null)
        {
            ShowDatSummaryReading(preview, path);
            await LoadDatSummaryAsync(path, version, cancellationToken);
            return;
        }

        DatLot.Show(entries, ExcludedDats);
        ShowLotInfo();

        // A lot of the configuration or of a profile without a DAT checked cannot be sorted: its DATs open to be chosen,
        // as those of a lot browsed to (which opens them itself)
        if (DatLot.IncludedCount == 0 && !_browsingToDat)
        {
            await ChooseLotDatsAsync();
        }
    }

    // Summary of the lot and convention of most of its DATs checked, again after a DAT is checked or unchecked
    private void ShowLotInfo()
    {
        if (!DatLot.IsLot)
        {
            return;
        }

        var main = DatLot.MainConvention;
        ShowDatInfo(
            LocalizedMessage.Verbatim(DatLot.Summary()),
            DatLot.IncludedCount == 0 ? "Error" : "Ok",
            main == NamingConvention.Auto ? null : main,
            DatCloneRelations.Unknown);
        _datInfoIsLot = true;
        OnPropertyChanged(nameof(DatLot));
        OnPropertyChanged(nameof(AreTosecVariablesApplicable));
        UpdatePatternVariables();
    }

    // The summary of a lot is rebuilt at a change of language, the names of its conventions with it
    private bool _datInfoIsLot;

    // Why a DAT cannot be read: the reason of a folder of DATs, translated, or the message of the error
    private static LocalizedMessage DatErrorMessage(Exception? error) =>
        error is DatFolderException folderError
            ? folderError.Reason
            : LocalizedMessage.Verbatim($"({error?.Message})");

    private static string DatDisplayName(DatCatalog catalog, string path) =>
        !string.IsNullOrEmpty(catalog.Header.Name) ? catalog.Header.Name : Path.GetFileName(path);

    private NamingConvention DetectConvention(DatCatalog catalog)
    {
        var headerHint = $"{catalog.Header.Name} {catalog.Header.Description} {catalog.Header.Comment} {catalog.Header.Homepage} {catalog.Header.Url}";
        var sampleRom = catalog.Entries.FirstOrDefault()?.Name ?? string.Empty;
        // A MAME DAT is recognized from its machines, the DAT of a software list from its software (their publisher):
        // their ROM names say nothing
        return catalog.Header.Convention.IsMameSets()
            ? catalog.Header.Convention
            : _noIntroParser.DetectNamingConvention(sampleRom, headerText: headerHint);
    }

    private void ShowDatInfo(
        LocalizedMessage? message,
        string tone,
        NamingConvention? detected,
        DatCloneRelations cloneRelations,
        MameDatSummary? mameSummary = null,
        DatNameTraits? nameTraits = null)
    {
        _datInfoMessage = message;
        _datInfoIsLot = false;
        DatCloneRelations = cloneRelations;
        _datNameTraitsRead = nameTraits;
        DatMameSummary = mameSummary;
        DatInfoText = message is null ? string.Empty : _localization.Format(message);
        DatInfoTone = tone;
        DetectedDatConvention = detected;
        UpdateEffectiveConvention();
    }

    private void UpdateEffectiveConvention()
    {
        var current = SelectedNamingConvention;
        if (current != NamingConvention.Auto)
        {
            if (current == NamingConvention.Tosec)
            {
                ConventionBadgeText = _localization.Tr("tab1.badge_tosec");
                ConventionBadgeTone = "Tosec";
            }
            else if (current == NamingConvention.NoIntro)
            {
                ConventionBadgeText = _localization.Tr("tab1.badge_nointro");
                ConventionBadgeTone = "NoIntro";
            }
            else if (current == NamingConvention.Redump)
            {
                ConventionBadgeText = _localization.Tr("tab1.badge_redump");
                ConventionBadgeTone = "Redump";
            }
            else if (current == NamingConvention.Mame)
            {
                ConventionBadgeText = _localization.Tr("tab1.badge_mame");
                ConventionBadgeTone = "Mame";
            }
            else if (current == NamingConvention.MameSoftwareList)
            {
                ConventionBadgeText = _localization.Tr("tab1.badge_softlist");
                ConventionBadgeTone = "Mame";
            }
        }
        else
        {
            if (DetectedDatConvention == NamingConvention.NoIntro)
            {
                ConventionBadgeText = _localization.Tr("tab1.badge_nointro_detected");
                ConventionBadgeTone = "NoIntro";
            }
            else if (DetectedDatConvention == NamingConvention.Redump)
            {
                ConventionBadgeText = _localization.Tr("tab1.badge_redump_detected");
                ConventionBadgeTone = "Redump";
            }
            else if (DetectedDatConvention == NamingConvention.Tosec)
            {
                ConventionBadgeText = _localization.Tr("tab1.badge_tosec_detected");
                ConventionBadgeTone = "Tosec";
            }
            else if (DetectedDatConvention == NamingConvention.Mame)
            {
                ConventionBadgeText = _localization.Tr("tab1.badge_mame_detected");
                ConventionBadgeTone = "Mame";
            }
            else if (DetectedDatConvention == NamingConvention.MameSoftwareList)
            {
                ConventionBadgeText = _localization.Tr("tab1.badge_softlist_detected");
                ConventionBadgeTone = "Mame";
            }
            else
            {
                ConventionBadgeText = _localization.Tr("tab1.badge_auto");
                ConventionBadgeTone = "Auto";
            }
        }

        var effectiveConvention = current == NamingConvention.Auto && DetectedDatConvention.HasValue
            ? DetectedDatConvention.Value
            : current;
        EffectiveConvention = effectiveConvention;
        EffectiveConventionChanged?.Invoke(this, effectiveConvention);
    }

    // =========================================================================
    // Pattern variables and transfer mode hint
    // =========================================================================

    /// <summary>The variables of the main pattern, in the order of its menu, inserted by <paramref name="onInsert"/>.</summary>
    private List<PatternVariableViewModel> CreatePatternVariables(Action<string> onInsert) =>
    [
        .. CommonPatternVariableSpecs.Select(spec => CreatePatternVariable(spec, PatternVariableScope.All, onInsert)),
        .. TosecPatternVariableSpecs.Select(spec => CreatePatternVariable(spec, PatternVariableScope.Tosec, onInsert)),
        .. MamePatternVariableSpecs.Select(spec => CreatePatternVariable(spec, PatternVariableScope.Mame, onInsert)),
        .. DatPatternVariableSpecs.Select(spec => CreatePatternVariable(spec, PatternVariableScope.Dat, onInsert))
    ];

    private PatternVariableViewModel CreatePatternVariable(
        (string Tag, string LabelKey, string TipKey) spec, PatternVariableScope scope, Action<string> onInsert) =>
        new(spec.Tag, spec.LabelKey, _localization.Tr(spec.LabelKey), spec.TipKey, _localization.Tr(spec.TipKey), scope, onInsert);

    partial void OnEffectiveConventionChanged(NamingConvention value) => UpdatePatternVariables();

    /// <summary>
    /// Greys out the variables without value with the effective convention, their tooltip then saying when they apply.
    /// </summary>
    private void UpdatePatternVariables()
    {
        foreach (var variable in PatternVariables.Concat(PatchedPatternVariables))
        {
            var (isApplicable, hintKey) = variable.Scope switch
            {
                PatternVariableScope.Tosec => (AreTosecVariablesApplicable, "var.hint_tosec_only"),
                PatternVariableScope.Mame => (AreMameVariablesApplicable, "var.hint_mame_only"),
                PatternVariableScope.Dat => (!string.IsNullOrWhiteSpace(DatFilePath), "var.hint_dat_only"),
                PatternVariableScope.DatFolder => (
                    Frontend == FrontendTarget.None ? HoldsSeveralDats && FolderPerDat : !string.IsNullOrWhiteSpace(DatFilePath),
                    "var.hint_dat_folder_only"),
                _ => (true, null)
            };
            variable.IsApplicable = isApplicable;
            var tooltip = _localization.Tr(variable.TooltipKey);
            variable.Tooltip = isApplicable || hintKey is null ? tooltip : $"{tooltip}\n{_localization.Tr(hintKey)}";
        }
    }

    private string TransferModeHintFor(TransferMode mode) =>
        TransferModeOptions.FirstOrDefault(o => o.Value == mode)?.Description ?? TransferModeOptions[0].Description;

    // =========================================================================
    // Configuration
    // =========================================================================

    public override void WriteTo(SortSettings settings)
    {
        settings.SourceDir = SourceDir;
        settings.DestinationDir = DestinationDir;
        settings.DatFile = DatFilePath;
        settings.DatRecursive = DatRecursive;
        settings.ExcludedDats = ExcludedDats.ToList();
        settings.FolderPerDat = FolderPerDat;
        settings.DatFolderMirror = DatFolderMirror;
        settings.ChdDir = ChdDir;
        settings.PatchDir = PatchDir;
        settings.ApplyPatches = ApplyPatches;
        settings.PatchOnly = PatchOnly;
        settings.PatchedPattern = PatchedPattern;
        settings.MameCategoryFile = MameCategoryFile;
        settings.MameRatingFile = MameRatingFile;
        settings.NamingConvention = SelectedNamingConvention;
        settings.Recursive = IsRecursive;
        settings.ScanArchives = ScanArchives;
        settings.AppendToDestination = AppendToDestination;
        settings.TransferMode = SelectedTransferMode;
        settings.CollisionMode = SelectedCollisionMode;
        settings.VerifyCopies = VerifyCopies;
        settings.PreserveArchives = PreserveArchives;
        settings.IsolateUnmatched = IsolateUnmatched;
        settings.HashingParallelism = HashingParallelism;
        settings.VerifySha1 = VerifySha1;
        settings.VerifyArchiveContents = VerifyArchiveContents;
        settings.MatchChdByName = MatchChdByName;
        settings.FlatDestination = FlatDestination;
        settings.Pattern = Pattern;
        settings.RestoreArticles = RestoreArticles;
        settings.SubfolderMultidisk = SubfolderMultidisk;
        settings.GenerateM3u = GenerateM3u;
        settings.M3uPlacement = SelectedM3uPlacement;
        settings.FolderPerDiscGame = FolderPerDiscGame;
        settings.Frontend = Frontend;
    }

    public override void ReadFrom(SortSettings settings)
    {
        _readingSettings = true;
        try
        {
            ReadSettings(settings);
        }
        finally
        {
            _readingSettings = false;
        }
    }

    private void ReadSettings(SortSettings settings)
    {
        // How a folder of DATs is read, before the DAT whose summary depends on it
        DatRecursive = settings.DatRecursive;

        // Loading a configuration is not a user change of the source directory
        _suppressSourceDirChangeReset = true;
        try
        {
            SourceDir = settings.SourceDir;
            DestinationDir = settings.DestinationDir;
            DatFilePath = settings.DatFile;
        }
        finally
        {
            _suppressSourceDirChangeReset = false;
        }

        ExcludedDats = settings.ExcludedDats.ToList();
        FolderPerDat = settings.FolderPerDat;
        DatFolderMirror = settings.DatFolderMirror;
        ChdDir = settings.ChdDir;
        PatchDir = settings.PatchDir;
        ApplyPatches = settings.ApplyPatches;
        PatchOnly = settings.PatchOnly;
        PatchedPattern = settings.PatchedPattern;
        PatchedPatternCaretIndex = -1;
        MameCategoryFile = settings.MameCategoryFile;
        MameRatingFile = settings.MameRatingFile;
        SelectedNamingConvention = settings.NamingConvention;
        IsRecursive = settings.Recursive;
        ScanArchives = settings.ScanArchives;
        AppendToDestination = settings.AppendToDestination;
        SelectedTransferMode = settings.TransferMode;
        SelectedCollisionMode = settings.CollisionMode;
        VerifyCopies = settings.VerifyCopies;
        PreserveArchives = settings.PreserveArchives;
        IsolateUnmatched = settings.IsolateUnmatched;
        HashingParallelism = settings.HashingParallelism;
        VerifySha1 = settings.VerifySha1;
        VerifyArchiveContents = settings.VerifyArchiveContents;
        MatchChdByName = settings.MatchChdByName;
        Pattern = settings.Pattern;
        PatternCaretIndex = -1;
        FlatDestination = settings.FlatDestination;
        RestoreArticles = settings.RestoreArticles;
        SubfolderMultidisk = settings.SubfolderMultidisk;
        GenerateM3u = settings.GenerateM3u;
        SelectedM3uPlacement = settings.M3uPlacement;
        FolderPerDiscGame = settings.FolderPerDiscGame;
        Frontend = settings.Frontend;
    }

    public override void RefreshTranslations()
    {
        OnPropertyChanged(nameof(FolderPerDatTooltip));
        OnPropertyChanged(nameof(DatFolderMirrorTooltip));
        NamingConventionOptions.RefreshTranslations();
        CollisionModeOptions.RefreshTranslations();
        M3uPlacementOptions.RefreshTranslations();
        FrontendOptions.RefreshTranslations();
        HashingParallelismOptions.RefreshTranslations();
        Advanced.RefreshTranslations();

        foreach (var variable in PatternVariables.Concat(PatchedPatternVariables))
        {
            variable.Label = _localization.Tr(variable.LabelKey);
        }

        UpdatePatternVariables();

        foreach (var option in TransferModeOptions)
        {
            option.RefreshTranslations();
        }

        TransferModeHint = TransferModeHintFor(SelectedTransferMode);
        DatLot.RefreshTranslations();
        if (_datInfoIsLot)
        {
            ShowLotInfo();
        }
        else
        {
            DatInfoText = _datInfoMessage is null ? string.Empty : _localization.Format(_datInfoMessage);
        }

        Preview.Refresh();
    }
}
