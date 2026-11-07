using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RomSieve.App.Abstractions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Implementation of <see cref="IProgress{T}"/> executing the callback synchronously
/// on the calling thread (which preserves the UI <see cref="SynchronizationContext"/> during <c>ExecuteAsync</c>).
/// </summary>
internal sealed class ImmediateProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

/// <summary>
/// Simulation and sort execution: validations and confirmations, progress, cancellation, results of the
/// last run and undo of the last real sort.
/// </summary>
public sealed partial class ExecutionViewModel : ViewModelBase
{
    private readonly ISortProcessorService _processorService;
    private readonly IOperationJournalService _journalService;
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localization;
    private readonly DirectoriesViewModel _directories;
    private readonly LogViewModel _log;
    private readonly Func<SortConfig> _buildConfig;
    private readonly Func<SortSettings> _captureSettings;
    private readonly Func<Task> _saveConfiguration;
    private readonly Action<int> _showResults;
    private readonly string _runLogDirectory;

    private CancellationTokenSource? _operationCts;

    /// <summary>Options of the running operation, recorded when it starts (see <see cref="SettingsFingerprint"/>).</summary>
    private string? _runSettingsFingerprint;

    /// <summary>Options of the last finished simulation, while it is the reference of the next sort.</summary>
    private string? _simulatedSettingsFingerprint;

    /// <summary>Configuration of the last finished simulation, which the summary of the next sort describes.</summary>
    private SortConfig? _simulatedConfig;
    private List<ProcessItem> _lastItems = [];
    private List<DatRomEntry> _lastMissingRoms = [];

    /// <param name="buildConfig">Builds the configuration of a run from the settings steps, without dialog.</param>
    /// <param name="captureSettings">Reads the options of the settings steps, to know whether they changed since the last simulation.</param>
    /// <param name="saveConfiguration">Saves <c>config.json</c> before a real sort.</param>
    /// <param name="showResults">Shows the results, on the preview table (0) or on the log (1).</param>
    public ExecutionViewModel(
        ISortProcessorService processorService,
        IOperationJournalService journalService,
        IDialogService dialogService,
        ILocalizationService localization,
        DirectoriesViewModel directories,
        LogViewModel log,
        Func<SortConfig> buildConfig,
        Func<SortSettings> captureSettings,
        Func<Task> saveConfiguration,
        Action<int> showResults,
        string runLogDirectory)
    {
        _processorService = processorService;
        _journalService = journalService;
        _dialogService = dialogService;
        _localization = localization;
        _directories = directories;
        _log = log;
        _buildConfig = buildConfig;
        _captureSettings = captureSettings;
        _saveConfiguration = saveConfiguration;
        _showResults = showResults;
        _runLogDirectory = runLogDirectory;

        _statusSource = LocalizedMessage.Create("panel4.status_ready");
        _rawStatusMessage = localization.Format(_statusSource);
        _statusMessage = _rawStatusMessage;
        _countsText = localization.Tr("panel4.status_files", ("current", 0), ("total", 0));
        _etaText = localization.Tr("panel4.eta_unknown");

        // A folder of DATs being read holds back the simulation and the sort, until its DATs are known
        directories.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DirectoriesViewModel.IsReadingDatFolder))
            {
                OnPropertyChanged(nameof(IsReadingDatFolder));
                OnPropertyChanged(nameof(CanStartOperation));
                OnPropertyChanged(nameof(CanRunPrimaryAction));
                OnPropertyChanged(nameof(PrimaryActionTooltip));
                PrimaryActionCommand.NotifyCanExecuteChanged();
                SimulateCommand.NotifyCanExecuteChanged();
                StartSortCommand.NotifyCanExecuteChanged();
            }
        };
    }

    /// <summary>Raised when a run starts, once the results of the previous one are cleared.</summary>
    public event EventHandler? RunStarting;

    /// <summary>Raised when a run ends normally, once its results and status are recorded.</summary>
    public event EventHandler<SortExecutionResult>? RunFinished;

    /// <summary>Raised when a run is cancelled; <see cref="LastItems"/> holds the items processed so far.</summary>
    public event EventHandler? RunCancelled;

    /// <summary>Raised when a run stops on an error, before the error is shown.</summary>
    public event EventHandler? RunFailed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartOperation))]
    [NotifyPropertyChangedFor(nameof(CanStopOperation))]
    [NotifyPropertyChangedFor(nameof(CanUndoLastSort))]
    [NotifyPropertyChangedFor(nameof(CanRunPrimaryAction))]
    [NotifyPropertyChangedFor(nameof(IsSortSummaryVisible))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryActionCommand))]
    [NotifyCanExecuteChangedFor(nameof(SimulateCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartSortCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoLastSortCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isDryRunOperation = true;

    /// <summary>
    /// Whether a simulation or a sort is running, which the main button stops. An undo also sets
    /// <see cref="IsRunning"/>, but cannot be stopped.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryActionText))]
    [NotifyPropertyChangedFor(nameof(PrimaryActionTooltip))]
    [NotifyPropertyChangedFor(nameof(CanRunPrimaryAction))]
    [NotifyPropertyChangedFor(nameof(IsProgressVisible))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryActionCommand))]
    private bool _isPipelineRunning;

    /// <summary>Whether the user asked to stop the running operation, which ends at the next checkpoint.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunPrimaryAction))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryActionCommand))]
    private bool _isStopRequested;

    /// <summary>
    /// Whether the last finished simulation still matches the options of the settings steps: the sort would then
    /// do what the table shows, and the view puts the sort button forward with <see cref="SortSummaryText"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSortSummaryVisible))]
    private bool _isSimulationCurrent;

    /// <summary>What the sort will write, from the last simulation (mode, files kept, volume, destination).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortSummaryTooltip))]
    private string _sortSummaryText = string.Empty;

    [ObservableProperty]
    private int _progressPercentage;

    [ObservableProperty]
    private string _statusMessage;

    /// <summary>Message of <see cref="StatusMessage"/>, translated again when the language changes.</summary>
    private LocalizedMessage _statusSource;

    [ObservableProperty]
    private string _rawStatusMessage;

    [ObservableProperty]
    private string _countsText;

    [ObservableProperty]
    private string _etaText;

    /// <summary>Whether the operation journal holds a real sort that has not been undone yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUndoLastSort))]
    [NotifyCanExecuteChangedFor(nameof(UndoLastSortCommand))]
    private bool _hasUndoableSort;

    /// <summary>
    /// Whether a simulation or a sort can start: nothing runs, and no folder of DATs is being read
    /// (<see cref="DirectoriesViewModel.IsReadingDatFolder"/>), whose DATs to check are not known yet.
    /// </summary>
    public bool CanStartOperation => !IsRunning && !IsReadingDatFolder;
    /// <summary>Whether a folder of DATs is being read (<see cref="DirectoriesViewModel.IsReadingDatFolder"/>).</summary>
    public bool IsReadingDatFolder => _directories.IsReadingDatFolder;

    public bool CanStopOperation => IsRunning;
    public bool CanUndoLastSort => !IsRunning && HasUndoableSort;

    /// <summary>The main button simulates while nothing runs, and stops a running simulation or sort.</summary>
    public bool CanRunPrimaryAction => IsPipelineRunning ? !IsStopRequested : CanStartOperation;

    /// <summary>Label of the main button: simulate, or stop while a simulation or a sort runs.</summary>
    public string PrimaryActionText =>
        _localization.Tr(IsPipelineRunning ? "panel4.btn_stop" : "panel4.btn_simulate");

    public string PrimaryActionTooltip =>
        _localization.Tr(IsPipelineRunning ? "panel4.tooltip_stop"
            : IsReadingDatFolder ? "panel4.tooltip_wait_dat_folder"
            : "panel4.tooltip_simulate");

    /// <summary>The progress bar, the counters and the remaining time are only shown while an operation runs.</summary>
    public bool IsProgressVisible => IsPipelineRunning;

    public bool IsSortSummaryVisible => IsSimulationCurrent && !IsRunning && !string.IsNullOrEmpty(SortSummaryText);

    /// <summary>The whole summary (the label may be trimmed), and what it stands for.</summary>
    public string SortSummaryTooltip =>
        $"{SortSummaryText}{Environment.NewLine}{_localization.Tr("panel4.tooltip_sort_summary")}";

    public ProcessStats? LastStats { get; private set; }
    public DatCatalog? LastDatCatalog { get; private set; }
    public IReadOnlyList<DatRomEntry> LastMissingRoms => _lastMissingRoms;
    public IReadOnlyList<ProcessItem> LastItems => _lastItems;
    public SortConfig? LastExecutedConfig { get; private set; }

    /// <summary>Destination root of the rows of the preview table.</summary>
    public string PreviewDestinationRoot => LastExecutedConfig?.DestinationDir ?? _directories.DestinationDir;

    partial void OnIsRunningChanged(bool value)
    {
        if (!value)
        {
            RefreshUndoAvailability();
            _whenIdle?.TrySetResult();
            _whenIdle = null;
        }
    }

    // =========================================================================
    // Closing the window during an operation
    // =========================================================================

    /// <summary>Completed when the running operation ends, for those who wait for it (<see cref="WhenIdleAsync"/>).</summary>
    private TaskCompletionSource? _whenIdle;

    /// <summary>
    /// Whether a real sort or the undo of a sort is running: both write to the disk, and ending the process in their
    /// middle would leave the file being written. A simulation writes nothing.
    /// </summary>
    public bool IsWritingToDisk => IsRunning && (!IsPipelineRunning || !IsDryRunOperation);

    /// <summary>Completes once no operation runs, at once when none does.</summary>
    public Task WhenIdleAsync()
    {
        if (!IsRunning)
        {
            return Task.CompletedTask;
        }

        _whenIdle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _whenIdle.Task;
    }

    /// <summary>
    /// When the window is closed during a real sort or an undo (<see cref="IsWritingToDisk"/>): asks whether to close
    /// once it has ended, then stops the sort, which removes the file being written; an undo cannot be stopped and goes on
    /// to its end. Without <paramref name="ask"/> (the system shutting down), nothing is asked. Returns whether the window
    /// is to close once the operation has ended (<see cref="WhenIdleAsync"/>).
    /// </summary>
    public async Task<bool> ConfirmCloseWhileWritingAsync(bool ask)
    {
        if (ask && !await _dialogService.ShowConfirmationAsync(
                _localization.Tr("dialog.close_while_writing_title"),
                _localization.Tr(IsPipelineRunning ? "dialog.close_while_sorting_html" : "dialog.close_while_undoing_html")))
        {
            return false;
        }

        if (IsPipelineRunning)
        {
            Stop();
        }

        return true;
    }

    /// <summary>Replaces the results after an in-memory 1G1R recalculation of the preview table.</summary>
    public void UpdateResults(IEnumerable<ProcessItem> items, ProcessStats stats)
    {
        _lastItems = items.ToList();
        LastStats = stats;
        RefreshSimulationState();
    }

    // =========================================================================
    // Last simulation as the reference of the sort
    // =========================================================================

    /// <summary>
    /// Compares the options of the settings steps with those of the last finished simulation: called whenever an
    /// option changes. Going back to the simulated options makes the simulation current again.
    /// </summary>
    public void RefreshSimulationState()
    {
        if (IsRunning)
        {
            return;
        }

        IsSimulationCurrent = _simulatedSettingsFingerprint is not null
                              && string.Equals(_simulatedSettingsFingerprint, SettingsFingerprint(), StringComparison.Ordinal);
        UpdateSortSummary();
    }

    /// <summary>A real sort, an undo, a cancelled or failed run: the table no longer shows what a sort would do.</summary>
    private void ForgetSimulation()
    {
        _simulatedSettingsFingerprint = null;
        _simulatedConfig = null;
        IsSimulationCurrent = false;
        UpdateSortSummary();
    }

    /// <summary>The options of the settings steps, serialized as in <c>config.json</c>: two equal fingerprints give the same run.</summary>
    private string SettingsFingerprint()
    {
        var data = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        SortSettingsSerializer.Write(_captureSettings(), data);
        return JsonSerializer.Serialize(data);
    }

    /// <summary>
    /// Builds <see cref="SortSummaryText"/>: the transfer mode, the number of files kept (and of non-conforming files
    /// isolated in <c>_unmatched</c>), the volume written (before compression in ZIP mode) and the destination.
    /// </summary>
    private void UpdateSortSummary()
    {
        if (!IsSimulationCurrent || _simulatedConfig is not { } config || LastStats is not { } stats)
        {
            SortSummaryText = string.Empty;
            OnPropertyChanged(nameof(IsSortSummaryVisible));
            return;
        }

        SortSummaryText = BuildSummary(config, stats);
        OnPropertyChanged(nameof(IsSortSummaryVisible));
    }

    /// <summary>
    /// What a partial sort of the last simulation writes: <paramref name="written"/> are the files of the selection the
    /// simulation kept or isolated (<c>SelectionFilter.GetWrittenItems</c>), from <paramref name="gameCount"/> games
    /// chosen and the MAME sets they need, named by <paramref name="requiredSets"/> (BIOS, devices). <c>null</c> when the
    /// simulation is not the reference of the sort.
    /// </summary>
    public string? BuildPartialSortSummary(
        IReadOnlyCollection<ProcessItem> written, int gameCount, IReadOnlyList<string>? requiredSets = null, bool isPartOfSimulation = true)
    {
        if (!IsSimulationCurrent || _simulatedConfig is not { } config)
        {
            return null;
        }

        var stats = _processorService.RecomputeStats(written);
        var summary = BuildSummary(config, stats, gameCount, requiredSets, isPartOfSimulation);
        var oversized = Fat32Limit.FindOversizedOutputFiles(written, config.TransferMode).Count;
        if (oversized > 0)
        {
            summary = _localization.Tr("panel4.summary_partial_fat32", ("summary", summary), ("count", oversized));
        }

        return FindSpaceShortfall(config, written) is { } shortfall
            ? _localization.Tr("panel4.summary_partial_space", ("summary", summary), ("available", SizeFormatting.Format(_localization, shortfall.AvailableBytes)))
            : summary;
    }

    /// <summary>Test hook replacing the room left on the volume of the destination (<see cref="DestinationSpace.GetAvailableBytes"/>).</summary>
    internal Func<string, long?>? AvailableBytesOf { get; set; }

    /// <summary>
    /// The shortfall of room of the destination for the files of the simulation that a sort writes
    /// (<paramref name="written"/>), or <c>null</c>.
    /// </summary>
    private SpaceShortfall? FindSpaceShortfall(SortConfig config, IEnumerable<ProcessItem> written)
    {
        var volumeRoots = VolumeLocator.GetVolumeRoots();
        return DestinationSpace.FindShortfall(
            DestinationSpace.GetRequiredBytes(written, config, volumeRoots),
            AvailableBytesOf?.Invoke(config.DestinationDir) ?? DestinationSpace.GetAvailableBytes(config.DestinationDir, volumeRoots),
            config.DestinationDir);
    }

    /// <summary>
    /// <paramref name="message"/> of the confirmation of a sort, followed by a warning when the destination lacks room
    /// for what the last simulation writes, while it is the reference of the sort.
    /// </summary>
    private string WithSpaceWarning(SortConfig config, string message) =>
        IsSimulationCurrent && FindSpaceShortfall(config, _lastItems) is { } shortfall
            ? message + "<br><br>" + _localization.Format(shortfall.ToMessage("dialog.destination_space_short_html"))
            : message;

    /// <summary>
    /// Sentence of <see cref="SortSummaryText"/>; with <paramref name="partialGameCount"/>, that of a partial sort,
    /// which names its games and the MAME sets they need (<paramref name="requiredSets"/>), and says it is a part of the
    /// simulation unless it covers all of it (<paramref name="isPartOfSimulation"/>).
    /// </summary>
    private string BuildSummary(
        SortConfig config, ProcessStats stats, int? partialGameCount = null, IReadOnlyList<string>? requiredSets = null, bool isPartOfSimulation = true)
    {
        var destination = config.DestinationDir;
        // The non-conforming files left in the source (isolation off) are not written
        var unmatched = stats.IsolatedUnmatchedCount;
        if (stats.ValidCount + unmatched == 0)
        {
            return _localization.Tr("panel4.summary_nothing", ("dest", destination));
        }

        var files = stats.ValidCount == 1
            ? _localization.Tr("panel4.summary_one_file")
            : _localization.Tr("panel4.summary_files", ("count", stats.ValidCount));
        if (unmatched > 0)
        {
            files = unmatched == 1
                ? _localization.Tr("panel4.summary_plus_one_unmatched", ("files", files))
                : _localization.Tr("panel4.summary_plus_unmatched", ("files", files), ("unmatched", unmatched));
        }

        var isPartial = partialGameCount is not null && isPartOfSimulation;
        var key = config.TransferMode switch
        {
            TransferMode.Move => isPartial ? "panel4.summary_move_partial" : "panel4.summary_move",
            TransferMode.HardLink => isPartial ? "panel4.summary_hardlink_partial" : "panel4.summary_hardlink",
            TransferMode.SymbolicLink => isPartial ? "panel4.summary_symlink_partial" : "panel4.summary_symlink",
            TransferMode.Zip => isPartial ? "panel4.summary_zip_partial" : "panel4.summary_zip",
            _ => isPartial ? "panel4.summary_copy_partial" : "panel4.summary_copy"
        };
        if (partialGameCount is > 0 and var gameCount)
        {
            var games = gameCount == 1
                ? _localization.Tr("panel4.summary_one_game")
                : _localization.Tr("panel4.summary_games", ("count", gameCount));
            if (requiredSets is { Count: > 0 })
            {
                games = _localization.Tr("panel4.summary_games_and_required", ("games", games), ("required", DescribeRequiredSets(requiredSets)));
            }

            files = _localization.Tr("panel4.summary_games_files", ("games", games), ("files", files));
        }

        return _localization.Tr(
            key,
            ("files", files),
            ("size", SizeFormatting.Format(_localization, stats.OutputBytes)),
            ("dest", destination));
    }

    /// <summary>
    /// The MAME sets a partial sort adds to the games chosen: named up to three ("the required set neogeo"), counted
    /// beyond.
    /// </summary>
    private string DescribeRequiredSets(IReadOnlyList<string> requiredSets) => requiredSets.Count switch
    {
        1 => _localization.Tr("panel4.summary_one_required_set", ("name", requiredSets[0])),
        <= 3 => _localization.Tr("panel4.summary_required_sets", ("names", string.Join(", ", requiredSets))),
        _ => _localization.Tr("panel4.summary_required_set_count", ("count", requiredSets.Count))
    };

    // =========================================================================
    // Commands: simulation, sort execution and cancellation
    // =========================================================================

    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    public Task SimulateAsync() => ExecuteOperationAsync(isDryRun: true);

    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    public Task StartSortAsync() => ExecuteOperationAsync(isDryRun: false);

    /// <summary>
    /// Sorts a part of the last simulation (<see cref="PartialSortRequest"/>), which must still be current: the whole
    /// collection is resolved again, only the chosen games are transferred, and the destination is never purged.
    /// </summary>
    public Task StartPartialSortAsync(PartialSortRequest request) => ExecuteOperationAsync(isDryRun: false, request);

    [RelayCommand(CanExecute = nameof(CanStopOperation))]
    public void Stop()
    {
        if (_operationCts is not null && !_operationCts.IsCancellationRequested)
        {
            IsStopRequested = IsPipelineRunning;
            SetStatusMessage(LocalizedMessage.Create("panel4.status_stopping"));
            _operationCts.Cancel();
        }
    }

    /// <summary>
    /// Main button: starts a simulation, or stops the running simulation or sort. Concurrent executions are allowed,
    /// since the button must stay usable while the simulation it started runs.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunPrimaryAction), AllowConcurrentExecutions = true)]
    public Task PrimaryActionAsync()
    {
        if (IsPipelineRunning)
        {
            Stop();
            return Task.CompletedTask;
        }

        return SimulateAsync();
    }

    /// <summary>Synchronous form of <see cref="TryBuildConfigAsync"/>.</summary>
    public SortConfig? BuildConfig() =>
        TryBuildConfigAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Validates the directory fields and builds the <see cref="SortConfig"/>. Returns <c>null</c> and displays
    /// a warning if the source or destination is missing or if the DAT file is not found.
    /// </summary>
    public async Task<SortConfig?> TryBuildConfigAsync()
    {
        var srcText = _directories.SourceDir?.Trim() ?? string.Empty;
        var destText = _directories.DestinationDir?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(srcText))
        {
            await _dialogService.ShowWarningAsync(
                _localization.Tr("dialog.error_title"),
                _localization.Tr("dialog.error_no_source"));
            return null;
        }

        if (string.IsNullOrEmpty(destText))
        {
            await _dialogService.ShowWarningAsync(
                _localization.Tr("dialog.error_title"),
                _localization.Tr("dialog.error_no_dest"));
            return null;
        }

        var datText = _directories.DatFilePath?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(datText) && !File.Exists(datText) && !Directory.Exists(datText))
        {
            await _dialogService.ShowWarningAsync(
                _localization.Tr("dialog.error_title"),
                _localization.Tr("dialog.error_dat_not_found", ("path", datText)));
            return null;
        }

        return _buildConfig();
    }

    public async Task ExecuteOperationAsync(bool isDryRun, PartialSortRequest? partial = null)
    {
        // A partial sort chooses among the rows of the last simulation: they must still be what the sort would do
        // Started while a folder of DATs is read (the buttons are greyed then): the run waits for its DATs
        if (_directories.IsReadingDatFolder)
        {
            await _directories.DatLoadTask;
            if (_directories.IsReadingDatFolder)
            {
                return;
            }
        }

        if (IsRunning || (partial is not null && (isDryRun || !IsSimulationCurrent)))
        {
            return;
        }

        var config = await TryBuildConfigAsync();
        if (config is null)
        {
            return;
        }

        if (!Directory.Exists(config.SourceDir))
        {
            await _dialogService.ShowWarningAsync(
                _localization.Tr("dialog.error_title"),
                _localization.Tr("log.source_dir_not_found", ("path", config.SourceDir)));
            return;
        }

        // The CHD folder only matters to a MAME run: the effective convention says so (that of the DAT, read at its choice)
        var overlapKey = SortConfigValidator.GetErrorKey(config, isMameRun: _directories.IsMameConvention);
        if (overlapKey is not null)
        {
            await _dialogService.ShowWarningAsync(
                _localization.Tr("dialog.error_title"),
                _localization.Tr(overlapKey));
            return;
        }

        if (!isDryRun)
        {
            if (!await (partial is null ? ConfirmRealSortAsync(config) : ConfirmPartialSortAsync(config, partial)))
            {
                return;
            }

            await _saveConfiguration();
            _showResults(1);
        }
        else
        {
            _showResults(0);
        }

        LastExecutedConfig = config;
        IsDryRunOperation = isDryRun;
        _runSettingsFingerprint = SettingsFingerprint();
        ForgetSimulation();
        IsStopRequested = false;
        IsPipelineRunning = true;
        IsRunning = true;
        _lastItems.Clear();
        _lastMissingRoms.Clear();
        LastDatCatalog = null;
        LastStats = null;
        BeginRunLog(LocalizedMessage.Create(isDryRun ? "action.dry_run" : "action.sort_execution"));
        RunStarting?.Invoke(this, EventArgs.Empty);

        ProgressPercentage = 0;
        // "Ready" would say that nothing runs, while listing the files of a large source takes minutes
        SetStatusMessage(LocalizedMessage.Create(isDryRun ? "panel4.status_starting_dry_run" : "panel4.status_starting_sort"));
        CountsText = _localization.Tr("panel4.status_files", ("current", 0), ("total", 0));
        EtaText = _localization.Tr("panel4.eta_unknown");

        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        var token = _operationCts.Token;

        // Without an Avalonia application (ViewModel tests), the UI dispatcher is not read at all: its first read creates
        // it, and created before the application it would be a fallback dispatcher without a run loop
        var useUiDispatcher = Application.Current is not null && Dispatcher.UIThread.CheckAccess();
        var progressSw = Stopwatch.StartNew();
        var lastProgressReportMs = -1000L;
        var lastReportedPct = -1;
        var pendingProgress = new ConcurrentQueue<SortProgress>();
        var progressPostPending = 0;

        var pendingLogs = new ConcurrentQueue<LogMessage>();
        var logPostPending = 0;
        var processedItemsSoFar = new ConcurrentQueue<ProcessItem>();

        void FlushPendingProgress()
        {
            Volatile.Write(ref progressPostPending, 0);
            while (pendingProgress.TryDequeue(out var p))
            {
                OnProgressReported(p);
            }
        }

        void FlushPendingLogs()
        {
            Volatile.Write(ref logPostPending, 0);
            var batch = new List<LogMessage>();
            while (pendingLogs.TryDequeue(out var msg))
            {
                batch.Add(msg);
            }

            _log.AppendBatch(batch);
        }

        var progressReporter = new ImmediateProgress<SortProgress>(p =>
        {
            var newPct = p.TotalCount > 0
                ? Math.Clamp((int)((double)p.ProcessedCount / p.TotalCount * 100.0), 0, 100)
                : 0;
            var nowMs = progressSw.ElapsedMilliseconds;
            var isBoundary = p.ProcessedCount <= 1 || (p.TotalCount > 0 && p.ProcessedCount >= p.TotalCount);
            if (!isBoundary && newPct == lastReportedPct && nowMs - lastProgressReportMs < 25)
            {
                return;
            }

            lastProgressReportMs = nowMs;
            lastReportedPct = newPct;

            if (useUiDispatcher)
            {
                pendingProgress.Enqueue(p);
                if (Interlocked.Exchange(ref progressPostPending, 1) == 0)
                {
                    Dispatcher.UIThread.Post(FlushPendingProgress, DispatcherPriority.Background);
                }
            }
            else
            {
                OnProgressReported(p);
            }
        });

        var logReporter = new ImmediateProgress<LogMessage>(msg =>
        {
            if (useUiDispatcher)
            {
                pendingLogs.Enqueue(msg);
                if (Interlocked.Exchange(ref logPostPending, 1) == 0)
                {
                    Dispatcher.UIThread.Post(FlushPendingLogs, DispatcherPriority.Background);
                }
            }
            else
            {
                _log.Append(msg.Level, msg.Message);
            }
        });

        var itemReporter = new ImmediateProgress<ProcessItem>(item =>
        {
            processedItemsSoFar.Enqueue(item);
        });

        try
        {
            var result = useUiDispatcher
                ? await Task.Run(
                    () => _processorService.ExecuteAsync(
                        config,
                        isDryRun: isDryRun,
                        progress: progressReporter,
                        logProgress: logReporter,
                        itemProcessed: itemReporter,
                        cancellationToken: token,
                        selection: partial?.Selection),
                    token)
                : await _processorService.ExecuteAsync(
                    config,
                    isDryRun: isDryRun,
                    progress: progressReporter,
                    logProgress: logReporter,
                    itemProcessed: itemReporter,
                    cancellationToken: token,
                    selection: partial?.Selection);

            if (useUiDispatcher)
            {
                FlushPendingLogs();
                FlushPendingProgress();
            }

            if (token.IsCancellationRequested)
            {
                _lastItems = processedItemsSoFar.ToList();
                OnOperationCancelled();
                return;
            }

            OnOperationFinished(result);

            // A sort that stopped before its end (full destination, journal that cannot be written) says why at once:
            // the log alone could go unnoticed
            if (result.StopReason is { } stopReason)
            {
                await _dialogService.ShowWarningAsync(_localization.Tr("dialog.sort_stopped_title"), _localization.Format(stopReason));
            }
        }
        catch (OperationCanceledException)
        {
            if (useUiDispatcher)
            {
                FlushPendingLogs();
                FlushPendingProgress();
            }

            _lastItems = processedItemsSoFar.ToList();
            OnOperationCancelled();
        }
        catch (Exception ex)
        {
            if (useUiDispatcher)
            {
                FlushPendingLogs();
                FlushPendingProgress();
            }

            await OnOperationErrorAsync(ex.Message);
        }
        finally
        {
            EndRunLog();
            IsRunning = false;
            IsPipelineRunning = false;
            IsStopRequested = false;
            RefreshSimulationState();
        }
    }

    /// <summary>
    /// Starts writing the log of the operation that begins, <paramref name="title"/>, to a file of the folder of the run
    /// logs (<see cref="RunLogFile"/>): the log panel only keeps its last entries.
    /// </summary>
    private void BeginRunLog(LocalizedMessage title)
    {
        EndRunLog();
        _log.RunFile = RunLogFile.Create(_runLogDirectory, _localization.Format(title));
    }

    /// <summary>Closes the log file of the operation that ends, whose path the log panel then gives.</summary>
    private void EndRunLog()
    {
        if (_log.RunFile is not { } file)
        {
            return;
        }

        _log.RunFile = null;
        file.Dispose();
        _log.Append(LogLevel.Info, LocalizedMessage.Create("log.run_log_written", ("path", file.Path)));
    }

    /// <summary>
    /// Asks for the confirmation of a real sort; a non-empty destination is either completed or purged (moved to the
    /// quarantine). Purging a destination that RomSieve did not mark asks for a second confirmation.
    /// </summary>
    private async Task<bool> ConfirmRealSortAsync(SortConfig config)
    {
        var modeName = GetTransferModeDisplayName(config.TransferMode);
        var destination = _processorService.InspectDestination(config.DestinationDir);
        if (destination.HasContent)
        {
            if (config.AppendToDestination)
            {
                var warnMsg = WithSpaceWarning(config, WithOverwriteWarning(config.CollisionMode, _localization.Tr(
                    "dialog.dest_not_empty_append_html",
                    ("dest_dir", config.DestinationDir),
                    ("source_dir", config.SourceDir),
                    ("mode_name", modeName),
                    ("collision_mode", GetCollisionModeDisplayName(config.CollisionMode)))));

                var confirmAppend = await _dialogService.ShowConfirmationAsync(
                    _localization.Tr("dialog.dest_not_empty_append_title"),
                    warnMsg);

                if (!confirmAppend)
                {
                    _log.Append(LogLevel.Warn, LocalizedMessage.Create("log.cancelled_dest_not_empty_append"));
                    return false;
                }

                config.PurgeDestination = false;
            }
            else
            {
                var warnMsg = WithSpaceWarning(config, _localization.Tr(
                    "dialog.dest_not_empty_html",
                    ("dest_dir", config.DestinationDir),
                    ("source_dir", config.SourceDir),
                    ("mode_name", modeName)));

                var confirmPurge = await _dialogService.ShowConfirmationAsync(
                    _localization.Tr("dialog.dest_not_empty_title"),
                    warnMsg);

                if (!confirmPurge)
                {
                    _log.Append(LogLevel.Warn, LocalizedMessage.Create("log.cancelled_dest_not_empty"));
                    return false;
                }

                if (destination.IsForeign && !await _dialogService.ShowConfirmationAsync(
                        _localization.Tr("dialog.dest_foreign_title"),
                        _localization.Tr("dialog.dest_foreign_html", ("dest_dir", config.DestinationDir))))
                {
                    _log.Append(LogLevel.Warn, LocalizedMessage.Create("log.cancelled_dest_foreign"));
                    return false;
                }

                config.PurgeDestination = true;
            }

            return true;
        }

        var confirmMsg = WithSpaceWarning(config, WithOverwriteWarning(config.CollisionMode, _localization.Tr(
            "dialog.confirm_sort_html",
            ("source_dir", config.SourceDir),
            ("dest_dir", config.DestinationDir),
            ("mode_name", modeName),
            ("collision_mode", GetCollisionModeDisplayName(config.CollisionMode)))));

        return await _dialogService.ShowConfirmationAsync(
            _localization.Tr("dialog.confirm_sort_title"),
            confirmMsg);
    }

    /// <summary>
    /// Asks for the confirmation of a partial sort, which describes the selection. A partial sort always completes
    /// the destination, even when the purge is chosen: the files of the destination outside the selection stay.
    /// </summary>
    private async Task<bool> ConfirmPartialSortAsync(SortConfig config, PartialSortRequest partial)
    {
        config.PurgeDestination = false;
        var destination = _processorService.InspectDestination(config.DestinationDir);
        var confirmed = await _dialogService.ShowConfirmationAsync(
            _localization.Tr("dialog.confirm_partial_sort_title"),
            WithOverwriteWarning(config.CollisionMode, _localization.Tr(
                destination.HasContent ? "dialog.confirm_partial_sort_not_empty_html" : "dialog.confirm_partial_sort_html",
                ("summary", partial.SummaryText),
                ("source_dir", config.SourceDir),
                ("collision_mode", GetCollisionModeDisplayName(config.CollisionMode)))));
        if (!confirmed)
        {
            _log.Append(LogLevel.Warn, LocalizedMessage.Create("log.cancelled_partial_sort"));
        }

        return confirmed;
    }

    // =========================================================================
    // Undo of the last real sort
    // =========================================================================

    /// <summary>
    /// Re-reads the operation journal to know whether a sort can be undone.
    /// </summary>
    public void RefreshUndoAvailability() =>
        HasUndoableSort = _journalService.FindLastUndoableJournal() is not null;

    /// <summary>
    /// Undoes the last real sort recorded in the operation journal, after confirmation.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUndoLastSort))]
    public async Task UndoLastSortAsync()
    {
        if (IsRunning)
        {
            return;
        }

        var title = _localization.Tr("undo.title");
        var last = _journalService.FindLastUndoableJournal();
        if (last is null)
        {
            HasUndoableSort = false;
            await _dialogService.ShowInfoAsync(title, _localization.Tr("undo.nothing_to_undo"));
            return;
        }

        // After an undo that left operations to retry, only they are replayed
        var confirmed = await _dialogService.ShowConfirmationAsync(
            title,
            _localization.Tr(
                last.IsUndoToResume ? "undo.confirm_resume_html" : "undo.confirm_html",
                ("date", last.StartedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)),
                ("count", last.OperationsToUndo),
                ("mode_name", GetTransferModeDisplayName(last.TransferMode)),
                ("source_dir", last.SourceDir),
                ("dest_dir", last.DestinationDir)));
        if (!confirmed)
        {
            return;
        }

        // The undo puts files back in the source: the last simulation no longer describes it
        ForgetSimulation();
        _showResults(1);
        BeginRunLog(LocalizedMessage.Create("undo.title"));
        IsRunning = true;
        try
        {
            var report = await Task.Run(() => _journalService.UndoAsync(last.Path));
            foreach (var issue in report.Issues)
            {
                var detail = string.IsNullOrEmpty(issue.Detail) ? string.Empty : $" ({issue.Detail})";
                _log.Append(LogLevel.Warn, LocalizedMessage.Create("log.labeled", ("label", LocalizedMessage.Create(GetUndoIssueKey(issue.Kind))), ("detail", issue.Operation.TargetPath + detail)));
            }

            _log.Append(
                report.Issues.Count == 0 ? LogLevel.Ok : LogLevel.Warn,
                LocalizedMessage.Create("log.labeled", ("label", LocalizedMessage.Create("undo.log_completed")), ("detail", $"{report.UndoneCount}/{report.TotalOperations}")));

            var message = _localization.Tr(
                "undo.report_html",
                ("undone", report.UndoneCount),
                ("total", report.TotalOperations),
                ("issues", report.Issues.Count));
            if (report.RetryableCount > 0)
            {
                message += _localization.Tr("undo.report_retry_html", ("count", report.RetryableCount), ("undo", _localization.Tr("panel4.btn_undo")));
            }

            if (report.Issues.Count == 0)
            {
                await _dialogService.ShowInfoAsync(title, message);
            }
            else
            {
                await _dialogService.ShowWarningAsync(title, message);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _log.Append(LogLevel.Error, LocalizedMessage.Create("log.labeled", ("label", LocalizedMessage.Create("undo.log_failed")), ("detail", ex.Message)));
            await _dialogService.ShowErrorAsync(title, _localization.Tr("undo.failed", ("error", ex.Message)));
        }
        finally
        {
            EndRunLog();
            IsRunning = false;
        }
    }

    internal static string GetUndoIssueKey(UndoIssueKind kind) => kind.ToMessageKey();

    // =========================================================================
    // Progress and end of an operation
    // =========================================================================

    public void ReportProgress(int current, int total, long currentBytes, long totalBytes, double etaSec, LocalizedMessage msg)
    {
        OnProgressReported(new SortProgress(current, total, currentBytes, totalBytes, etaSec, msg));
    }

    private void OnProgressReported(SortProgress progress)
    {
        ProgressPercentage = progress.TotalCount > 0
            ? Math.Clamp((int)((double)progress.ProcessedCount / progress.TotalCount * 100.0), 0, 100)
            : 0;

        // The steps reported until the pipeline sees the cancellation would hide that the stop was heard
        if (!IsStopRequested)
        {
            SetStatusMessage(progress.Message);
        }

        var curFormatted = progress.ProcessedCount.ToString("N0", CultureInfo.InvariantCulture).Replace(",", " ");
        var totFormatted = progress.TotalCount.ToString("N0", CultureInfo.InvariantCulture).Replace(",", " ");
        CountsText = _localization.Tr("panel4.status_files", ("current", curFormatted), ("total", totFormatted));

        if (progress.EtaSeconds > 0 && progress.ProcessedCount < progress.TotalCount)
        {
            var totalSecs = Math.Max(1, (int)progress.EtaSeconds);
            var mins = totalSecs / 60;
            var secs = totalSecs % 60;
            EtaText = _localization.Tr("panel4.eta_value", ("m", $"{mins:D2}"), ("s", $"{secs:D2}"));
        }
        else
        {
            EtaText = _localization.Tr("panel4.eta_unknown");
        }
    }

    private void SetStatusMessage(LocalizedMessage message)
    {
        _statusSource = message;
        RawStatusMessage = _localization.Format(message);
        // Never cut here: the label trims what its width cannot hold (a long file name), with the whole message in its
        // tooltip, and every message fits it with its counts (GuiTests.ProgressRow_StatusMessages_ShouldFitTheirLabel);
        // a count of characters cut messages the label held
        StatusMessage = RawStatusMessage;
    }

    /// <summary>Test hook: finishes a run with <paramref name="result"/>, as a completed simulation does.</summary>
    internal void CompleteRunForTests(SortExecutionResult result) => OnOperationFinished(result);

    /// <summary>
    /// Test hook: plays a whole run of <paramref name="config"/> whose <paramref name="result"/> was computed
    /// beforehand (start, then end of the run), without running the pipeline on the UI thread.
    /// </summary>
    internal void PlayRunForTests(SortConfig config, bool isDryRun, SortExecutionResult result)
    {
        LastExecutedConfig = config;
        IsDryRunOperation = isDryRun;
        _runSettingsFingerprint = SettingsFingerprint();
        _lastItems.Clear();
        RunStarting?.Invoke(this, EventArgs.Empty);
        OnOperationFinished(result);
        RefreshSimulationState();
    }

    /// <summary>
    /// Test hook: takes the current options of the settings steps as those of the last simulation, as if it had been run with
    /// them (screenshots that show sample paths instead of the temporary folders of the test).
    /// </summary>
    internal void AdoptCurrentSettingsAsSimulatedForTests()
    {
        _simulatedSettingsFingerprint = SettingsFingerprint();
        _simulatedConfig = _buildConfig();
        RefreshSimulationState();
    }

    private void OnOperationFinished(SortExecutionResult result)
    {
        LastStats = result.Stats;
        _lastItems = result.Items.ToList();
        _lastMissingRoms = result.MissingRoms.ToList();
        LastDatCatalog = result.DatCatalog;

        // Only a finished simulation is the reference of the next sort; a real sort changed the source and destination
        if (IsDryRunOperation && LastExecutedConfig is not null && _runSettingsFingerprint is not null)
        {
            _simulatedSettingsFingerprint = _runSettingsFingerprint;
            _simulatedConfig = LastExecutedConfig;
        }
        else
        {
            ForgetSimulation();
        }

        ProgressPercentage = 100;
        SetStatusMessage(LocalizedMessage.Create("panel4.status_finished"));
        var curFormatted = result.Stats.ProcessedCount.ToString("N0", CultureInfo.InvariantCulture).Replace(",", " ");
        var totFormatted = result.Stats.TotalScanned.ToString("N0", CultureInfo.InvariantCulture).Replace(",", " ");
        CountsText = _localization.Tr("panel4.status_files", ("current", curFormatted), ("total", totFormatted));
        EtaText = _localization.Tr("panel4.eta_value", ("m", "00"), ("s", "00"));

        RunFinished?.Invoke(this, result);
    }
    private void OnOperationCancelled()
    {
        ForgetSimulation();
        SetStatusMessage(LocalizedMessage.Create("panel4.status_cancelled"));
        EtaText = _localization.Tr("panel4.eta_unknown");
        RunCancelled?.Invoke(this, EventArgs.Empty);
    }
    private async Task OnOperationErrorAsync(string errorMessage)
    {
        ForgetSimulation();
        SetStatusMessage(LocalizedMessage.Create("panel4.status_error"));
        RunFailed?.Invoke(this, EventArgs.Empty);
        _log.Append(LogLevel.Error, LocalizedMessage.Verbatim(errorMessage));
        await _dialogService.ShowErrorAsync(
            _localization.Tr("dialog.error_title"),
            _localization.Tr("dialog.worker_error_msg", ("error", errorMessage)));
    }

    /// <summary>
    /// Translates the status texts again: the current step of a running operation, the other texts once it is over.
    /// </summary>
    public void RefreshTranslations()
    {
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(PrimaryActionTooltip));
        SetStatusMessage(_statusSource);
        if (IsRunning)
        {
            return;
        }

        UpdateSortSummary();

        var curFormatted = (LastStats?.ProcessedCount ?? 0).ToString("N0", CultureInfo.InvariantCulture).Replace(",", " ");
        var totFormatted = (LastStats?.TotalScanned ?? 0).ToString("N0", CultureInfo.InvariantCulture).Replace(",", " ");
        CountsText = _localization.Tr("panel4.status_files", ("current", curFormatted), ("total", totFormatted));
        EtaText = LastStats is not null && LastStats.TotalScanned > 0 && LastStats.ProcessedCount >= LastStats.TotalScanned
            ? _localization.Tr("panel4.eta_value", ("m", "00"), ("s", "00"))
            : _localization.Tr("panel4.eta_unknown");
    }

    /// <summary>The conflict handling as the Output step names it.</summary>
    private string GetCollisionModeDisplayName(CollisionMode mode) =>
        mode switch
        {
            CollisionMode.Skip => _localization.Tr("tab1.collision_skip"),
            CollisionMode.Overwrite => _localization.Tr("tab1.collision_overwrite"),
            _ => _localization.Tr("tab1.collision_rename")
        };

    /// <summary>
    /// Adds to the confirmation of a sort that overwrites conflicting files that the undo cannot give them back: a
    /// replaced file is not kept.
    /// </summary>
    private string WithOverwriteWarning(CollisionMode mode, string message) =>
        mode == CollisionMode.Overwrite
            ? message + "<br><br>" + _localization.Tr("dialog.overwrite_not_undoable_html", ("undo", _localization.Tr("panel4.btn_undo")))
            : message;

    private string GetTransferModeDisplayName(TransferMode mode) =>
        mode switch
        {
            TransferMode.Move => _localization.Tr("mode.move"),
            TransferMode.HardLink => _localization.Tr("mode.hardlink"),
            TransferMode.SymbolicLink => _localization.Tr("mode.symlink"),
            TransferMode.Zip => _localization.Tr("mode.zip"),
            _ => _localization.Tr("mode.copy")
        };
}
