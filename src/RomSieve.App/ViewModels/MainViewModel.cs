using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RomSieve.App.Abstractions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using static RomSieve.Core.Services.ConfigDictionaryReader;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Observable indexer allowing XAML views to dynamically bind to translated strings (<c>{Binding I18n[key]}</c>).
/// </summary>
public sealed class UiStringsIndexer(ILocalizationService localization) : ObservableObject
{
    public string this[string key] => localization.Tr(key);

    public void NotifyUpdated()
    {
        OnPropertyChanged(string.Empty);
        OnPropertyChanged("Item");
        OnPropertyChanged("Item[]");
    }
}

/// <summary>
/// Main ViewModel of the RomSieve application (MVVM with CommunityToolkit.Mvvm).
/// </summary>
public sealed partial class MainViewModel : ViewModelBase
{
    /// <summary>Sortable columns of the preview table (0, 2, 3, 4, 5, 6, 7, 9, 10).</summary>
    public static readonly IReadOnlySet<int> SortablePreviewColumns = PreviewTableViewModel.SortablePreviewColumns;

    private readonly ISortProcessorService _processorService;
    private readonly IDatService _datService;
    private readonly IRomFilterService _filterService;
    private readonly INoIntroRedumpParser _noIntroParser;
    private readonly IConfigurationService _configService;
    private readonly ILocalizationService _localization;
    private readonly IDialogService _dialogService;
    private readonly IOperationJournalService _journalService;

    private string? _configFilePath;

    // An unreadable configuration file that could not be copied either: the default settings never replace it
    private string? _unpreservedConfigPath;

    public MainViewModel(
        ISortProcessorService processorService,
        IDatService datService,
        IRomFilterService filterService,
        INoIntroRedumpParser noIntroParser,
        IPathOrganizerService pathOrganizer,
        IMameDescriptionParser mameParser,
        IConfigurationService configService,
        ILocalizationService localization,
        IOperationJournalService journalService,
        IUpdateCheckService updateService,
        IDialogService dialogService,
        string? configFilePath = null)
    {
        _processorService = processorService ?? throw new ArgumentNullException(nameof(processorService));
        _datService = datService ?? throw new ArgumentNullException(nameof(datService));
        _filterService = filterService ?? throw new ArgumentNullException(nameof(filterService));
        _noIntroParser = noIntroParser ?? throw new ArgumentNullException(nameof(noIntroParser));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _journalService = journalService ?? throw new ArgumentNullException(nameof(journalService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _configFilePath = configFilePath;

        _windowTitle = _localization.Tr("app.window_title", ("version", ProductVersion.Current));

        StatsBanner = new StatsBannerViewModel(_localization);
        Log = new LogViewModel(_localization);
        TosecFilters = new TosecFiltersViewModel(_localization);
        NoIntroFilters = new NoIntroFiltersViewModel();
        Filters = new FiltersViewModel(_localization, TosecFilters, NoIntroFilters);
        Priorities = new PrioritiesViewModel(_localization, TosecFilters);
        Directories = new DirectoriesViewModel(
            _localization,
            _dialogService,
            _datService,
            _noIntroParser,
            pathOrganizer ?? throw new ArgumentNullException(nameof(pathOrganizer)),
            mameParser ?? throw new ArgumentNullException(nameof(mameParser)));
        Directories.EffectiveConventionChanged += OnEffectiveConventionChanged;
        Quarantine = new QuarantineViewModel(_localization, _dialogService, Log);

        // The genres of the category filters come from the category file, the ratings from the rating file, read again
        // when they change
        Directories.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DirectoriesViewModel.MameCategoryFile))
            {
                _ = Filters.Categories.LoadAsync(Directories.MameCategoryFile);
            }
            else if (e.PropertyName == nameof(DirectoriesViewModel.MameRatingFile))
            {
                _ = Filters.Ratings.LoadAsync(Directories.MameRatingFile);
            }
            else if (e.PropertyName == nameof(DirectoriesViewModel.DestinationDir))
            {
                _ = Quarantine.LoadAsync(Directories.DestinationDir);
            }
            else if (e.PropertyName == nameof(DirectoriesViewModel.DatMameSummary))
            {
                Filters.ShowMameDat(Directories.DatMameSummary);
            }
            else if (e.PropertyName == nameof(DirectoriesViewModel.DatFilePath) && Export is not null)
            {
                // The filtered DAT of the catalog only needs a DAT file
                NotifyExportCommandsCanExecuteChanged();
            }
        };
        I18n = new UiStringsIndexer(_localization);

        Execution = new ExecutionViewModel(
            _processorService,
            _journalService,
            _dialogService,
            _localization,
            Directories,
            Log,
            BuildConfigUnchecked,
            CaptureSettings,
            () => SaveConfigurationAsync(_configFilePath),
            ShowResults,
            RunLogFile.GetLogDirectory(_configFilePath ?? _configService.GetDefaultConfigPath()));

        PreviewTable = new PreviewTableViewModel(
            _localization,
            _filterService,
            _processorService,
            () => Execution.IsRunning,
            () => BuildConfigUnchecked(),
            () => Execution.LastDatCatalog,
            () => Execution.LastStats,
            () => StatsBanner.ActiveFilters,
            (newItems, newStats) =>
            {
                Execution.UpdateResults(newItems, newStats);
                UpdateStatsBanner(newItems, newStats);
                RefreshTiebreakDecisions();

                // Created just after the table; only called once the window runs
                PartialSort?.MarkRowsChanged();
                PartialSort?.Refresh();
            },
            Log.Append,
            () =>
            {
                if (!Priorities.OneGameOneRom)
                {
                    Priorities.OneGameOneRom = true;
                }
            },
            () =>
            {
                if (StatsBanner.ActiveFilters.Count > 0)
                {
                    StatsBanner.ResetFilters();
                }
            },
            () => NotifyExportCommandsCanExecuteChanged());

        PreviewTable.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null)
            {
                OnPropertyChanged(e.PropertyName);
            }
        };

        PartialSort = new PartialSortViewModel(_localization, Execution, PreviewTable);

        Export = new ExportViewModel(
            _processorService,
            _dialogService,
            _localization,
            () => Execution.LastItems,
            () => Execution.LastStats,
            () => Execution.LastDatCatalog,
            () => Execution.LastMissingRoms,
            () => !Execution.IsRunning && AllPreviewItems.Count > 0,
            () => !Execution.IsRunning && AllPreviewItems.Any(r => r.Item.Status == FileStatus.Valid),
            () => !Execution.IsRunning && Execution.LastMissingRoms.Count > 0 && Execution.LastDatCatalog is not null,
            Log.Append,

            // The manual 1G1R choices name files of the table: they do not apply to the games of the catalog
            () => SortSettingsSerializer.ToSortConfig(CaptureSettings()),
            () => !Execution.IsRunning && !Directories.IsReadingDatFolder && !string.IsNullOrWhiteSpace(Directories.DatFilePath),

            // While some games are unchecked, the DAT of the collection describes what the sort of the selection writes
            () => PartialSort.CheckedWrittenItems,

            // MAME software lists and a lot of DATs export a folder of DATs; a folder (or an archive) of DATs that are not
            // software lists is a lot
            () => Directories.EffectiveConvention == NamingConvention.MameSoftwareList || Directories.HoldsSeveralDats,
            () => Directories.EffectiveConvention != NamingConvention.MameSoftwareList && Directories.HoldsSeveralDats);

        Profiles = new ProfilesViewModel(
            _configService,
            _dialogService,
            _localization,
            () => _configFilePath ?? _configService.GetDefaultConfigPath(),
            CaptureSettings,
            ApplyProfileSettings,
            () => Execution.IsRunning,
            Log.Append);

        Updates = new UpdateCheckerViewModel(
            updateService ?? throw new ArgumentNullException(nameof(updateService)),
            _dialogService,
            _localization,
            () => SaveConfigurationAsync(_configFilePath));

        Preferences = new PreferencesViewModel(_localization, _dialogService, I18n, Updates, Directories);

        Navigation = new NavigationViewModel(
            _localization,
            Directories,
            Execution,
            () => PreviewTable.AllPreviewItems.Count);
        Preferences.PropertyChanged += OnPreferencesPropertyChanged;

        Execution.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ExecutionViewModel.IsRunning))
            {
                OnIsRunningChanged(Execution.IsRunning);
            }

            if (e.PropertyName == nameof(ExecutionViewModel.IsSimulationCurrent))
            {
                RefreshTiebreakDecisions();
            }

            if (e.PropertyName is nameof(ExecutionViewModel.IsRunning)
                or nameof(ExecutionViewModel.IsSimulationCurrent)
                or nameof(ExecutionViewModel.SortSummaryText))
            {
                // A run or a recalculation changes the statuses of the rows without changing the rows themselves
                PartialSort.MarkRowsChanged();
                PartialSort.Refresh();
            }
            else if (e.PropertyName == nameof(ExecutionViewModel.IsReadingDatFolder))
            {
                // A folder of DATs being read holds back the sorts of the footer and the export of the catalog
                PartialSort.Refresh();
                NotifyExportCommandsCanExecuteChanged();
            }
        };

        // The rows shown are those a partial sort of the displayed rows transfers; a new layout may also show new
        // headers of games, whose check box follows the checked files
        PreviewTable.RowsRefreshed += (_, _) => PartialSort.Refresh();
        PreviewTable.AllPreviewItems.CollectionChanged += (_, _) => PartialSort.MarkRowsChanged();

        // While games are checked, the banner shows what their sort writes, not the whole run
        // and its chip counts the checked games
        PartialSort.CheckedSelectionChanged += (_, _) =>
        {
            StatsBanner.ShowSelection(PartialSort.CheckedWrittenItems);
            StatsBanner.SetSelectedUnitCount(PartialSort.IsPartialSelection ? PartialSort.CheckedUnitCount : null);
        };
        Execution.RunStarting += OnRunStarting;
        Execution.RunFinished += OnRunFinished;
        Execution.RunCancelled += OnRunCancelled;
        Execution.RunFailed += OnRunFailed;

        StatsBanner.FilterChanged += OnStatsBannerFilterChanged;
        WatchSettingsForSimulationState();

        // The chipset criterion only separates the versions of a game for an AGA Amiga
        TosecFilters.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TosecFiltersViewModel.AmigaTarget))
            {
                Priorities.ShowAmigaTarget(TosecFilters.AmigaTarget);
            }
        };
        Priorities.ShowAmigaTarget(TosecFilters.AmigaTarget);

        // A count of decided games next to a 1G1R criterion opens the table on those games
        foreach (var order in Priorities.CriteriaOrders)
        {
            order.DecidedGamesRequested += (_, criterion) => ShowGamesDecidedBy(criterion);
        }

        // A new source invalidates the manual 1G1R choices of the previous one, and its next simulation
        // starts from scratch instead of being compared with the rows of the previous source
        Directories.SourceDirChangedByUser += (_, _) => ResetPreviewForNewSource();

        PreviewTable.RebuildColumnHeaders();
        Directories.RefreshConventionState();

        if (!string.IsNullOrWhiteSpace(_configFilePath) && File.Exists(_configFilePath))
        {
            LoadConfigurationAsync(_configFilePath).GetAwaiter().GetResult();
        }

        Execution.RefreshUndoAvailability();
        PartialSort.Refresh();
    }

    // =========================================================================
    // Collections & Sub-ViewModels
    // =========================================================================

    public ILocalizationService Localization => _localization;
    public IDialogService DialogService => _dialogService;
    public UiStringsIndexer I18n { get; }

    public StatsBannerViewModel StatsBanner { get; }
    public TosecFiltersViewModel TosecFilters { get; }
    public NoIntroFiltersViewModel NoIntroFilters { get; }

    /// <summary>Filters tab: the options of <see cref="TosecFilters"/> or <see cref="NoIntroFilters"/>, one convention at a time.</summary>
    public FiltersViewModel Filters { get; }
    public PrioritiesViewModel Priorities { get; }
    public DirectoriesViewModel Directories { get; }
    public ExportViewModel Export { get; }
    public PreviewTableViewModel PreviewTable { get; }
    public ExecutionViewModel Execution { get; }

    /// <summary>Partial sort of the rows shown in the table (and, later, of the games chosen in it).</summary>
    public PartialSortViewModel PartialSort { get; }
    public LogViewModel Log { get; }

    /// <summary>Quarantine of the purges of the destination: its size, and a command that empties it for good.</summary>
    public QuarantineViewModel Quarantine { get; }

    /// <summary>Named configuration profiles, shared with the command line.</summary>
    public ProfilesViewModel Profiles { get; }

    /// <summary>Checks for new releases on GitHub, manually or at startup.</summary>
    public UpdateCheckerViewModel Updates { get; }

    /// <summary>Preferences window: language, theme, update checks, CRC computation, About section.</summary>
    public PreferencesViewModel Preferences { get; }

    /// <summary>Steps of the main window: collection, selection (filters, languages), output, results.</summary>
    public NavigationViewModel Navigation { get; }

    public ObservableCollection<SelectOptionItem<string>> AvailableLocaleOptions => Preferences.AvailableLocaleOptions;

    /// <summary>Localized headers of the 10 preview table columns.</summary>
    public ObservableCollection<string> PreviewColumnHeaders => PreviewTable.PreviewColumnHeaders;

    /// <summary>Tooltips for the 10 preview table columns (sorting indication on sortable columns).</summary>
    public ObservableCollection<string> PreviewColumnTooltips => PreviewTable.PreviewColumnTooltips;

    /// <summary>Complete set of rows from the last simulation or sort run.</summary>
    public ObservableCollection<PreviewItemViewModel> AllPreviewItems => PreviewTable.AllPreviewItems;

    /// <summary>Visible rows after applying status filters (<see cref="StatsBanner"/>) and search (<see cref="PreviewSearchText"/>).</summary>
    public ObservableCollection<PreviewItemViewModel> FilteredPreviewItems => PreviewTable.FilteredPreviewItems;

    /// <summary>Sorted list of unique game titles for search bar autocompletion.</summary>
    public ObservableCollection<string> PreviewKnownTitles => PreviewTable.PreviewKnownTitles;

    /// <summary>Supplies the search bar suggestions when the user types, instead of a bound list.</summary>
    public Func<string?, CancellationToken, Task<IEnumerable<object>>> PopulatePreviewTitleSuggestions =>
        PreviewTable.PopulateTitleSuggestionsAsync;

    /// <summary>Active 1G1R manual overrides in session (<c>CanonicalGameKey → DisplayName</c>).</summary>
    public IReadOnlyDictionary<string, string> Manual1G1RWinners => PreviewTable.Manual1G1RWinners;

    public bool HasManual1G1ROverrides => PreviewTable.HasManual1G1ROverrides;

    public string ClearAll1G1ROverridesMenuText => PreviewTable.ClearAll1G1ROverridesMenuText;

    public string IsolateGameMenuText => PreviewTable.IsolateGameMenuText;

    public bool CanForce1G1RWinner => PreviewTable.CanForce1G1RWinner;

    public bool CanResetSelected1G1ROverride => PreviewTable.CanResetSelected1G1ROverride;

    public bool CanIsolateSelectedGame => PreviewTable.CanIsolateSelectedGame;

    public bool HasPreviewSearchFilter => PreviewTable.HasPreviewSearchFilter;

    public bool CanShowPreviewContextMenuSeparator => PreviewTable.CanShowPreviewContextMenuSeparator;

    public bool CanOpenPreviewContextMenu => PreviewTable.CanOpenPreviewContextMenu;

    // =========================================================================
    // Window state and top bar
    // =========================================================================

    /// <summary>Language of the interface, chosen in the preferences window.</summary>
    public string UiLocale
    {
        get => Preferences.UiLocale;
        set => Preferences.UiLocale = value;
    }

    /// <summary>Color theme of the interface, chosen in the preferences window; the view applies it to the application.</summary>
    public UiTheme UiTheme
    {
        get => Preferences.UiTheme;
        set => Preferences.UiTheme = value;
    }

    public LocalizedOptionList<UiTheme> UiThemeOptions => Preferences.UiThemeOptions;

    [ObservableProperty]
    private string _windowTitle;

    /// <summary>Tab of the results: the preview table (0) or the operations log (1), saved as <c>active_bottom_tab</c>.</summary>
    [ObservableProperty]
    private int _activeBottomTabIndex;

    /// <summary>Convention shown in the filters tab (0: TOSEC, 1: No-Intro and Redump), saved as <c>filter_subtab_index</c>.</summary>
    public int FilterSubtabIndex
    {
        get => Filters.ShownConventionIndex;
        set => Filters.ShownConventionIndex = value;
    }

    [ObservableProperty]
    private int _windowWidth = 1280;

    [ObservableProperty]
    private int _windowHeight = 700;

    public int MinimumWindowWidth => 960;
    public int MinimumWindowHeight => 580;

    // =========================================================================
    // Panel 4: Execution, Progress Bar, Preview Table & Log
    // =========================================================================

    public string PreviewSearchText
    {
        get => PreviewTable.PreviewSearchText;
        set
        {
            if (PreviewTable.PreviewSearchText != value)
            {
                PreviewTable.PreviewSearchText = value;
                if (!string.IsNullOrEmpty(value) && ActiveBottomTabIndex != 0)
                {
                    ActiveBottomTabIndex = 0;
                }
            }
        }
    }

    public PreviewItemViewModel? SelectedPreviewItem
    {
        get => PreviewTable.SelectedPreviewItem;
        set => PreviewTable.SelectedPreviewItem = value;
    }

    public int? CurrentSortColumn
    {
        get => PreviewTable.CurrentSortColumn;
        set => PreviewTable.CurrentSortColumn = value;
    }

    public bool IsSortAscending
    {
        get => PreviewTable.IsSortAscending;
        set => PreviewTable.IsSortAscending = value;
    }

    public bool IsSortIndicatorShown => PreviewTable.IsSortIndicatorShown;

    public bool CanExportReport => Export.CanExportReport;
    public bool CanExportDat => Export.CanExportDat;
    public bool CanExportMissList => Export.CanExportMissList;

    // =========================================================================
    // Property change callbacks ([ObservableProperty])
    // =========================================================================

    /// <summary>A language chosen in the preferences applies to the whole interface at once.</summary>
    private void OnPreferencesPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PreferencesViewModel.UiLocale))
        {
            if (!string.IsNullOrWhiteSpace(Preferences.UiLocale))
            {
                _localization.SetLocale(Preferences.UiLocale);
                RefreshAllTranslations();
            }

            OnPropertyChanged(nameof(UiLocale));
        }
        else if (e.PropertyName == nameof(PreferencesViewModel.UiTheme))
        {
            OnPropertyChanged(nameof(UiTheme));
        }
    }

    /// <summary>Locks the settings steps and refreshes the dependent commands while an operation runs.</summary>
    private void OnIsRunningChanged(bool value)
    {
        TosecFilters.IsRunning = value;
        NoIntroFilters.IsRunning = value;
        Priorities.IsRunning = value;
        Directories.IsRunning = value;
        Quarantine.IsRunning = value;
        if (!value)
        {
            // A real sort may have purged the destination, and an undo put its quarantine back
            _ = Quarantine.RefreshAsync();
        }

        PreviewTable.Notify1G1ROverrideStateChanged();
        NotifyExportCommandsCanExecuteChanged();
        Profiles.NotifyCommandsCanExecuteChanged();
    }

    /// <summary>
    /// Any change of an option tells the execution panel whether the last simulation still describes the next sort.
    /// The ordered lists of the priorities tab raise their own events.
    /// </summary>
    private void WatchSettingsForSimulationState()
    {
        void Refresh() => Execution.RefreshSimulationState();
        foreach (var section in new ConfigSectionViewModel[] { Directories, TosecFilters, NoIntroFilters, Priorities })
        {
            section.PropertyChanged += (_, _) => Refresh();
        }

        Priorities.LanguagePriority.PriorityChanged += (_, _) => Refresh();
        Priorities.CountryPriority.PriorityChanged += (_, _) => Refresh();
        foreach (var order in Priorities.CriteriaOrders)
        {
            order.OrderChanged += (_, _) => Refresh();
        }
    }

    /// <summary>The manual 1G1R choices and the reference simulation belong to the previous source.</summary>
    private void ResetPreviewForNewSource()
    {
        if (PreviewTable.HasManual1G1ROverrides)
        {
            PreviewTable.ClearAll1G1ROverrides();
        }

        PreviewTable.ForgetPreviousSimulation();

        // The games checked in the rows of another source are not chosen in this one
        PartialSort.Forget();
    }
    // =========================================================================
    // Commands: In-memory 1G1R Recalculation & Preview Table Context Menu
    // Forwarded to PreviewTableViewModel
    // =========================================================================

    public IRelayCommand<PreviewItemViewModel?> Force1G1RWinnerCommand => PreviewTable.Force1G1RWinnerCommand;
    public IRelayCommand<PreviewRowViewModel?> Reset1G1ROverrideForGameCommand => PreviewTable.Reset1G1ROverrideForGameCommand;
    public IRelayCommand ClearAll1G1ROverridesCommand => PreviewTable.ClearAll1G1ROverridesCommand;
    public IRelayCommand<PreviewRowViewModel?> IsolateGameInPreviewCommand => PreviewTable.IsolateGameInPreviewCommand;
    public IRelayCommand ClearPreviewSearchCommand => PreviewTable.ClearPreviewSearchCommand;
    public IRelayCommand<int> SortPreviewByColumnCommand => PreviewTable.SortPreviewByColumnCommand;

    public void Force1G1RWinner(PreviewItemViewModel? row) => PreviewTable.Force1G1RWinner(row);
    public void Reset1G1ROverrideForGame(PreviewRowViewModel? row) => PreviewTable.Reset1G1ROverrideForGame(row);
    public void ClearAll1G1ROverrides() => PreviewTable.ClearAll1G1ROverrides();
    public void IsolateGameInPreview(PreviewRowViewModel? row) => PreviewTable.IsolateGameInPreview(row);
    public void ClearPreviewSearch() => PreviewTable.ClearPreviewSearch();
    public void SortPreviewByColumn(int columnIndex) => PreviewTable.SortPreviewByColumn(columnIndex);
    public bool IsPreviewRowHidden(int rowIndex) => PreviewTable.IsPreviewRowHidden(rowIndex);
    public string GetRowStatus(int rowIndex) => PreviewTable.GetRowStatus(rowIndex);
    public IReadOnlyList<PreviewContextMenuAction> BuildPreviewContextMenuForRow(int rowIndex) => PreviewTable.BuildPreviewContextMenuForRow(rowIndex);
    public IReadOnlyList<PreviewContextMenuAction> BuildPreviewContextMenu(PreviewRowViewModel? row) => PreviewTable.BuildPreviewContextMenu(row);
    public static string NormalizeSearchText(string? text) => PreviewTableViewModel.NormalizeSearchText(text);

    // =========================================================================
    // Commands: Exports (JSON/CSV Report, DAT File, Miss List, Log)
    // =========================================================================

    public IAsyncRelayCommand ExportReportCommand => Export.ExportReportCommand;
    public IAsyncRelayCommand ExportDatCommand => Export.ExportDatCommand;
    public IAsyncRelayCommand ExportMissListCommand => Export.ExportMissListCommand;
    public IAsyncRelayCommand ExportCatalogDatCommand => Export.ExportCatalogDatCommand;

    public Task ExportReportAsync() => Export.ExportReportAsync();
    public Task ExportDatAsync() => Export.ExportDatAsync();
    public Task ExportMissListAsync() => Export.ExportMissListAsync();

    // =========================================================================
    // SortConfig Construction & JSON Persistence (config.json)
    // =========================================================================

    /// <summary>
    /// Constructs a <see cref="SortConfig"/> without modal dialog boxes (used for in-memory 1G1R recalculation).
    /// </summary>
    public SortConfig BuildConfigUnchecked() =>
        SortSettingsSerializer.ToSortConfig(CaptureSettings(), PreviewTable.Manual1G1RWinners);

    /// <summary>Collects the options of the configuration sections.</summary>
    public SortSettings CaptureSettings()
    {
        var settings = new SortSettings();
        Directories.WriteTo(settings);
        TosecFilters.WriteTo(settings);
        NoIntroFilters.WriteTo(settings);
        Priorities.WriteTo(settings);
        return settings;
    }

    /// <summary>Shows the options of a profile; a profile with another source is a change of source.</summary>
    private void ApplyProfileSettings(SortSettings settings)
    {
        var sourceChanged = !string.Equals(Directories.SourceDir.Trim(), settings.SourceDir.Trim(), StringComparison.Ordinal);
        ApplySettings(settings);
        Directories.RefreshConventionState();
        if (sourceChanged)
        {
            ResetPreviewForNewSource();
        }
    }

    /// <summary>Shows the options in the configuration sections.</summary>
    public void ApplySettings(SortSettings settings)
    {
        Directories.ReadFrom(settings);
        TosecFilters.ReadFrom(settings);
        NoIntroFilters.ReadFrom(settings);
        Priorities.ReadFrom(settings);
    }

    /// <summary>
    /// Exports the full state of the ViewModel as a serializable dictionary compatible with <c>config.json</c>.
    /// </summary>
    public Dictionary<string, object?> ToConfigurationDictionary()
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ui_locale"] = _localization.CurrentLocale,
            ["ui_theme"] = UiTheme.ToConfigString(),
            ["filter_subtab_index"] = FilterSubtabIndex,

            // UI State
            ["active_bottom_tab"] = ActiveBottomTabIndex,
            ["window_width"] = WindowWidth,
            ["window_height"] = WindowHeight,
            ["preview_hidden_columns"] = PreviewTable.HiddenColumnIds.ToList(),
            // Columns known by this version: a column added later takes its default visibility
            ["preview_columns"] = PreviewTableViewModel.ColumnIds.ToList(),
            ["preview_detail_expanded"] = PreviewTable.IsDetailPanelExpanded,
            ["preview_grouped_by_game"] = PreviewTable.IsGroupedByGame
        };

        Updates.WriteTo(data);

        SortSettingsSerializer.Write(CaptureSettings(), data);
        return data;
    }

    /// <summary>
    /// Applies a configuration dictionary loaded from <c>config.json</c>.
    /// </summary>
    public void ApplyConfigurationDictionary(IReadOnlyDictionary<string, object?> data)
    {
        if (data is null || data.Count == 0)
        {
            return;
        }

        if (TryGetString(data, "ui_locale", out var savedLocale) && !string.IsNullOrWhiteSpace(savedLocale))
        {
            UiLocale = savedLocale;
        }

        if (TryGetString(data, "ui_theme", out var savedTheme))
        {
            UiTheme = EnumExtensions.ParseUiTheme(savedTheme);
        }

        Updates.ReadFrom(data);

        // A missing option keeps its current value
        ApplySettings(SortSettingsSerializer.Read(data, CaptureSettings()));

        if (TryGetInt(data, "filter_subtab_index", out var fSub)) FilterSubtabIndex = fSub;

        // UI State
        if (TryGetInt(data, "active_bottom_tab", out var bottomTab) && bottomTab is >= 0 and < 2)
        {
            ActiveBottomTabIndex = bottomTab;
        }

        if (TryGetInt(data, "window_width", out var w) && w >= 800) WindowWidth = w;
        if (TryGetInt(data, "window_height", out var h) && h >= 500) WindowHeight = h;

        // A missing or unreadable list keeps the default columns; unknown column names are ignored
        PreviewTable.SetHiddenColumnIds(
            TryGetStringList(data, "preview_hidden_columns", out var hiddenColumns) ? hiddenColumns : null,
            TryGetStringList(data, "preview_columns", out var knownColumns) ? knownColumns : null);
        if (TryGetBool(data, "preview_detail_expanded", out var detailExpanded))
        {
            PreviewTable.IsDetailPanelExpanded = detailExpanded;
        }

        if (TryGetBool(data, "preview_grouped_by_game", out var groupedByGame))
        {
            PreviewTable.IsGroupedByGame = groupedByGame;
        }

        Directories.RefreshConventionState();
    }

    [RelayCommand]
    public async Task SaveConfigurationAsync(string? path = null)
    {
        var target = path ?? _configFilePath;
        if (target is not null && target == _unpreservedConfigPath)
        {
            return;
        }

        var data = ToConfigurationDictionary();
        await _configService.SaveConfigToFileAsync(data, target);
    }

    [RelayCommand]
    public async Task LoadConfigurationAsync(string? path = null)
    {
        var configPath = path ?? _configFilePath;
        var data = await _configService.LoadConfigFromFileAsync(configPath);
        if (data is null)
        {
            // The default settings replace an unreadable file at the next save: a copy keeps what it held
            if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath))
            {
                if (_configService.PreserveUnreadableConfig(configPath) is { } copy)
                {
                    Log.Append(LogLevel.Warn, LocalizedMessage.Create("config.unreadable_preserved", ("path", configPath), ("copy", copy)));
                }
                else
                {
                    _unpreservedConfigPath = configPath;
                    Log.Append(LogLevel.Warn, LocalizedMessage.Create("config.unreadable_kept", ("path", configPath)));
                }
            }
        }
        else
        {
            if (ConfigSchema.DescribeNewerVersion(data, configPath ?? string.Empty) is { } warning)
            {
                Log.Append(LogLevel.Warn, warning);
            }

            ApplyConfigurationDictionary(data);
        }
    }

    // =========================================================================
    // Internal Logic: Preview, I18n & Tabs
    // =========================================================================

    public void ProcessSingleItemForPreview(ProcessItem item) =>
        OnItemProcessed(item);

    private void OnItemProcessed(ProcessItem item)
    {
        // A differential simulation keeps showing the previous rows until it is merged into them
        if (!PreviewTable.IsDifferentialRunInProgress)
        {
            PreviewTable.AddPreviewItem(item, Execution.PreviewDestinationRoot);
        }
    }

    private void OnRunStarting(object? sender, EventArgs e)
    {
        // A run shows its rows or its log as they come; going back to the settings meanwhile is not undone
        Navigation.ShowResults();

        // A new simulation of the same source is compared with the rows of the previous one;
        // any other run (real sort, other source, nothing to compare with) starts from an empty table
        var config = Execution.LastExecutedConfig;
        if (Execution.IsDryRunOperation && config is not null && PreviewTable.CanCompareWithPreviousSimulation(config.SourceDir))
        {
            PreviewTable.BeginDifferentialRun();
            return;
        }

        PreviewTable.ForgetPreviousSimulation();
        PreviewTable.Clear();
        PartialSort.Forget();
        StatsBanner.Reset();
    }

    private void OnRunFinished(object? sender, SortExecutionResult result)
    {
        var destRoot = Execution.PreviewDestinationRoot;
        var items = Execution.LastItems;
        if (PreviewTable.IsDifferentialRunInProgress)
        {
            // The rows keep their place and the current sort; only their content changes
            PreviewTable.ApplyDifferentialResults(destRoot, items);
        }
        else
        {
            if (PreviewTable.AllPreviewItems.Count != items.Count)
            {
                PreviewTable.RebuildAllPreviewItemsFromLastItems(destRoot, items);
            }
            else
            {
                foreach (var row in PreviewTable.AllPreviewItems)
                {
                    row.RefreshFromItem(destRoot);
                }
            }

            // Default ascending alphabetical sort on column 0 ("Original Name")
            if (PreviewTable.AllPreviewItems.Count > 0)
            {
                PreviewTable.CurrentSortColumn = 0;
                PreviewTable.IsSortAscending = true;
                PreviewTable.ApplyPreviewSorting();
            }

            PreviewTable.RefreshFilteredPreviewItems();
        }

        // Only a simulation can be the reference of the next one: a real sort changed the source
        PreviewTable.CompleteRun(Execution.IsDryRunOperation ? Execution.LastExecutedConfig?.SourceDir : null);
        UpdateStatsBanner(result.Items, result.Stats);
        StatsBanner.SetChangedRowCount(PreviewTable.HasComparison ? PreviewTable.ChangedRowCount : null);
        NotifyExportCommandsCanExecuteChanged();
        Navigation.RefreshIndicators();
        RefreshTiebreakDecisions();
    }

    /// <summary>The table of the results shows only the games <paramref name="criterion"/> decided in the last simulation.</summary>
    public void ShowGamesDecidedBy(TiebreakCriterion criterion)
    {
        PreviewTable.CriterionFilter = criterion;
        ShowResults(0);
    }

    /// <summary>
    /// Shows next to the 1G1R criteria the games each one decided in the last simulation, as long as it is that of
    /// the current options (the order of the criteria included) and kept one version per game; otherwise nothing.
    /// </summary>
    private void RefreshTiebreakDecisions()
    {
        Priorities.ShowDecisions(
            Execution.IsSimulationCurrent && Execution.LastExecutedConfig?.Filters.OneGameOneRom == true
                ? TiebreakDecisions.CountDecidedGames(Execution.LastItems, _filterService)
                : null);
    }

    /// <summary>
    /// Shows the statistics of the last run in the banner, with the transfer mode of that run (not the one selected
    /// since) and the written files too large for FAT32.
    /// </summary>
    private void UpdateStatsBanner(IReadOnlyCollection<ProcessItem> items, ProcessStats stats)
    {
        var transferMode = Execution.LastExecutedConfig?.TransferMode ?? TransferMode.Copy;
        StatsBanner.UpdateStats(stats, transferMode, Fat32Limit.FindOversizedOutputFiles(items, transferMode));
    }

    private void OnRunFailed(object? sender, EventArgs e)
    {
        // As for any failed run, the table is left empty
        PreviewTable.ForgetPreviousSimulation();
        PreviewTable.Clear();
        PartialSort.Forget();
        StatsBanner.Reset();
        Navigation.RefreshIndicators();
    }

    private void OnRunCancelled(object? sender, EventArgs e)
    {
        // A cancelled run shows the items processed so far, without comparison, and is not a reference
        var items = Execution.LastItems;
        var wasDifferential = PreviewTable.IsDifferentialRunInProgress;
        PreviewTable.ForgetPreviousSimulation();
        if (wasDifferential)
        {
            StatsBanner.Reset();
            PreviewTable.Clear();
        }

        // A cancelled run is not a reference: its rows are not those of the next simulation
        PartialSort.Forget();

        if (items.Count > 0 && PreviewTable.AllPreviewItems.Count != items.Count)
        {
            PreviewTable.RebuildAllPreviewItemsFromLastItems(Execution.PreviewDestinationRoot, items);
        }

        if (PreviewTable.AllPreviewItems.Count > 0)
        {
            PreviewTable.CurrentSortColumn = 0;
            PreviewTable.IsSortAscending = true;
            PreviewTable.ApplyPreviewSorting();
            PreviewTable.RefreshFilteredPreviewItems();
        }

        Navigation.RefreshIndicators();
    }

    private void OnStatsBannerFilterChanged(object? sender, IReadOnlySet<string> activeFilters)
    {
        PreviewTable.RefreshFilteredPreviewItems();
        if (ActiveBottomTabIndex != 0)
        {
            ActiveBottomTabIndex = 0;
        }
    }

    /// <summary>
    /// The filters tab shows the options of the effective convention, and the order of the 1G1R criteria greys out
    /// those that have no effect on its collection.
    /// </summary>
    private void OnEffectiveConventionChanged(object? sender, NamingConvention effectiveConvention)
    {
        Filters.ShowConvention(effectiveConvention);
        Priorities.ShowConvention(effectiveConvention, Directories.DatCloneRelations, Directories.DatNameTraits);
    }

    /// <summary>Shows the results, on the preview table (0) or on the operations log (1).</summary>
    private void ShowResults(int bottomTabIndex)
    {
        ActiveBottomTabIndex = bottomTabIndex;
        Navigation.ShowResults();
    }

    private void RefreshAllTranslations()
    {
        WindowTitle = _localization.Tr("app.window_title", ("version", ProductVersion.Current));
        Preferences.RefreshTranslations();
        Execution.RefreshTranslations();
        PartialSort.RefreshTranslations();
        TosecFilters.RefreshTranslations();
        Filters.RefreshTranslations();
        Priorities.RefreshTranslations();
        Quarantine.RefreshTranslations();
        Navigation.RefreshTranslations();
        PreviewTable.RebuildColumnHeaders();

        StatsBanner.RefreshTranslations();
        I18n.NotifyUpdated();
        OnPropertyChanged(nameof(I18n));
        OnPropertyChanged(nameof(ClearAll1G1ROverridesMenuText));
        OnPropertyChanged(nameof(IsolateGameMenuText));
        Directories.RefreshTranslations();

        // Status reasons are structured messages: refreshing the rows translates them in the new language
        PreviewTable.RefreshTranslations();

        // Log entries keep their structured message: they are translated again in the new language
        Log.RefreshTranslations();
        Updates.RefreshTranslations();
        Export.RefreshTranslations();
    }

    private void NotifyExportCommandsCanExecuteChanged()
    {
        Export.NotifyCommandsCanExecuteChanged();
        OnPropertyChanged(nameof(CanExportReport));
        OnPropertyChanged(nameof(CanExportDat));
        OnPropertyChanged(nameof(CanExportMissList));
    }
}
