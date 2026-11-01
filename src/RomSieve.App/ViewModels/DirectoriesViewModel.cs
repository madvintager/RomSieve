using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RomSieve.App.Abstractions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

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
        TimeSpan? previewDebounce = null)
    {
        _localization = localization;
        _dialogService = dialogService;
        _datService = datService;
        _noIntroParser = noIntroParser;
        _datInfoText = string.Empty;

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
            (M3uPlacementMode.Subfolder, "tab1.m3u_placement_subfolder"));
        HashingParallelismOptions = new LocalizedOptionList<int>(
            localization,
            (1, "tab1.hashing_parallelism_1"),
            (2, "tab1.hashing_parallelism_2"),
            (4, "tab1.hashing_parallelism_4"),
            (8, "tab1.hashing_parallelism_8"),
            (SortConfig.AutoHashingParallelism, "tab1.hashing_parallelism_auto"));

        PatternVariables =
        [
            .. CommonPatternVariableSpecs.Select(spec => CreatePatternVariable(spec, isTosecOnly: false)),
            .. TosecPatternVariableSpecs.Select(spec => CreatePatternVariable(spec, isTosecOnly: true)),
            .. MamePatternVariableSpecs.Select(spec => CreatePatternVariable(spec, isTosecOnly: false))
        ];
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
        Preview = new PatternPreviewViewModel(localization, noIntroParser, pathOrganizer, mameParser, this, previewDebounce);
    }

    /// <summary>Live preview of the paths produced by the pattern.</summary>
    public PatternPreviewViewModel Preview { get; }

    /// <summary>
    /// Advanced options of the collection step (archives, SHA-1, CHD by name: how the files are identified), shown in a
    /// drop-down panel. The verification of the copies is a visible option of the output step, and the place of the
    /// playlists stays next to the option that enables it.
    /// </summary>
    public AdvancedOptionsViewModel Advanced { get; }

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
    public LocalizedOptionList<int> HashingParallelismOptions { get; }

    /// <summary>
    /// Entries of the "Insert a variable" menu: the variables of every convention, then those of TOSEC names, shown
    /// when the effective convention is TOSEC or not known yet (<see cref="AreTosecVariablesShown"/>).
    /// </summary>
    public IReadOnlyList<PatternVariableViewModel> PatternVariables { get; }

    /// <summary>
    /// Caret position in the pattern field, where a variable is inserted; -1 when the user has not placed it since
    /// the pattern was last replaced (a variable is then added as a new folder at the end).
    /// </summary>
    public int PatternCaretIndex { get; set; } = -1;

    /// <summary>Convention that the settings steps follow: the selected one, or the one detected from the DAT in Auto mode.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreTosecVariablesShown), nameof(IsMameConvention), nameof(AreMameVariablesShown))]
    private NamingConvention _effectiveConvention = NamingConvention.Auto;

    /// <summary>Folder of the CHDs of a MAME collection, read in addition to the source; empty: none.</summary>
    [ObservableProperty]
    private string _chdDir = string.Empty;

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

    /// <summary>Whether the variables of the MAME categories are offered.</summary>
    public bool AreMameVariablesShown => EffectiveConvention == NamingConvention.Mame;

    /// <summary>Whether the variables of TOSEC names are offered: they have no value in No-Intro and Redump names.</summary>
    public bool AreTosecVariablesShown => EffectiveConvention is NamingConvention.Auto or NamingConvention.Tosec;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTransferCopy))]
    [NotifyPropertyChangedFor(nameof(IsTransferMove))]
    [NotifyPropertyChangedFor(nameof(IsTransferHardLink))]
    [NotifyPropertyChangedFor(nameof(IsTransferSymbolicLink))]
    [NotifyPropertyChangedFor(nameof(IsTransferZip))]
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

    [ObservableProperty]
    private CollisionMode _selectedCollisionMode = CollisionMode.Rename;

    [ObservableProperty]
    private bool _verifyCopies;

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

    public bool IsPatternEditingEnabled => !IsRunning && !FlatDestination;
    public bool IsM3uPlacementEnabled => !IsRunning && GenerateM3u && SubfolderMultidisk;

    protected override void OnRunningStateChanged()
    {
        OnPropertyChanged(nameof(IsPatternEditingEnabled));
        OnPropertyChanged(nameof(IsM3uPlacementEnabled));
    }

    partial void OnSelectedNamingConventionChanged(NamingConvention value) => UpdateEffectiveConvention();

    partial void OnDatFilePathChanged(string value) => UpdateDatStatusInfo(value);

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
            new(_localization.Tr("dialog.browse_dat_filter"), ["dat", "xml"]),
            FileDialogFilter.AllFiles(_localization)
        ];

        var file = await _dialogService.PickOpenFileAsync(_localization.Tr("dialog.browse_dat_title"), filters, startDir);
        if (!string.IsNullOrEmpty(file))
        {
            DatFilePath = file;
        }
    }

    /// <summary>Chooses a folder of DATs: the DATs of several MAME software lists, sorted together.</summary>
    [RelayCommand]
    public async Task BrowseDatFolderAsync()
    {
        var currentPath = DatFilePath?.Trim() ?? string.Empty;
        var startDir = Directory.Exists(currentPath) ? currentPath : Path.GetDirectoryName(currentPath) ?? string.Empty;
        var folder = await _dialogService.PickFolderAsync(_localization.Tr("dialog.browse_dat_folder_title"), startDir);
        if (!string.IsNullOrEmpty(folder))
        {
            DatFilePath = folder;
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

        var current = Pattern ?? string.Empty;
        var caret = PatternCaretIndex;
        if (caret < 0 || caret > current.Length)
        {
            var prefix = current.Length == 0 || current.EndsWith('/') ? current : $"{current}/";
            Pattern = $"{prefix}{tag}/";
            PatternCaretIndex = prefix.Length + tag.Length;
            return;
        }

        Pattern = current.Insert(caret, tag);
        PatternCaretIndex = caret + tag.Length;
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
    /// for MAME, is then read off the UI thread for its summary (<see cref="DatLoadTask"/>).
    /// </summary>
    private void UpdateDatStatusInfo(string pathStr)
    {
        var version = ++_datLoadVersion;
        _datLoadCts?.Cancel();
        _datLoadCts?.Dispose();
        _datLoadCts = null;
        DatLoadTask = Task.CompletedTask;

        var cleanPath = pathStr?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(cleanPath))
        {
            ShowDatInfo(null, "Neutral", null, DatCloneRelations.NoDat);
            return;
        }

        // A folder holds the DATs of several MAME software lists
        if (!File.Exists(cleanPath) && !Directory.Exists(cleanPath))
        {
            ShowDatInfo(LocalizedMessage.Create("tab1.dat_info_error"), "Error", null, DatCloneRelations.Unknown);
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

        ShowDatInfo(
            LocalizedMessage.Create("tab1.dat_info_reading", ("name", DatDisplayName(preview, cleanPath))),
            "Neutral",
            DetectConvention(preview),
            // The first games only prove that the DAT has clones: their absence waits for the whole DAT
            TiebreakApplicability.GetCloneRelations(preview) == DatCloneRelations.Present ? DatCloneRelations.Present : DatCloneRelations.Unknown);

        _datLoadCts = new CancellationTokenSource();
        DatLoadTask = LoadDatSummaryAsync(cleanPath, version, _datLoadCts.Token);
    }

    private async Task LoadDatSummaryAsync(string path, int version, CancellationToken cancellationToken)
    {
        DatCatalog? catalog = null;
        MameDatSummary? mameSummary = null;
        Exception? error = null;
        try
        {
            (catalog, mameSummary) = await Task.Run(async () =>
            {
                var read = await _datService.ParseDatFileAsync(path, cancellationToken);
                return (read, MameDatSummary.FromCatalog(read));
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
            mameSummary);
    }

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
        MameDatSummary? mameSummary = null)
    {
        _datInfoMessage = message;
        DatCloneRelations = cloneRelations;
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

    private PatternVariableViewModel CreatePatternVariable((string Tag, string LabelKey, string TipKey) spec, bool isTosecOnly) =>
        new(spec.Tag, spec.LabelKey, _localization.Tr(spec.LabelKey), spec.TipKey, _localization.Tr(spec.TipKey), isTosecOnly, InsertVariableAtCaret);

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
        settings.ChdDir = ChdDir;
        settings.MameCategoryFile = MameCategoryFile;
        settings.MameRatingFile = MameRatingFile;
        settings.NamingConvention = SelectedNamingConvention;
        settings.Recursive = IsRecursive;
        settings.ScanArchives = ScanArchives;
        settings.AppendToDestination = AppendToDestination;
        settings.TransferMode = SelectedTransferMode;
        settings.CollisionMode = SelectedCollisionMode;
        settings.VerifyCopies = VerifyCopies;
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
    }

    public override void ReadFrom(SortSettings settings)
    {
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

        ChdDir = settings.ChdDir;
        MameCategoryFile = settings.MameCategoryFile;
        MameRatingFile = settings.MameRatingFile;
        SelectedNamingConvention = settings.NamingConvention;
        IsRecursive = settings.Recursive;
        ScanArchives = settings.ScanArchives;
        AppendToDestination = settings.AppendToDestination;
        SelectedTransferMode = settings.TransferMode;
        SelectedCollisionMode = settings.CollisionMode;
        VerifyCopies = settings.VerifyCopies;
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
    }

    public override void RefreshTranslations()
    {
        NamingConventionOptions.RefreshTranslations();
        CollisionModeOptions.RefreshTranslations();
        M3uPlacementOptions.RefreshTranslations();
        HashingParallelismOptions.RefreshTranslations();
        Advanced.RefreshTranslations();

        foreach (var variable in PatternVariables)
        {
            variable.Label = _localization.Tr(variable.LabelKey);
            variable.Tooltip = _localization.Tr(variable.TooltipKey);
        }

        foreach (var option in TransferModeOptions)
        {
            option.RefreshTranslations();
        }

        TransferModeHint = TransferModeHintFor(SelectedTransferMode);
        DatInfoText = _datInfoMessage is null ? string.Empty : _localization.Format(_datInfoMessage);
        Preview.Refresh();
    }
}
