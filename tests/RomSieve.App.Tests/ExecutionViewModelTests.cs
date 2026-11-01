using System.ComponentModel;
using RomSieve.App;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// Execution panel: labels, main button (simulate, or stop a running operation), sort put forward after a simulation of
/// the current options, and summary of what the sort will write.
/// </summary>
public sealed class ExecutionViewModelTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly HeadlessDialogService _dialogs = new();
    private readonly MainViewModel _vm;
    private readonly string _destination;

    public ExecutionViewModelTests()
    {
        var config = _temp.WriteText(Path.Combine("config", "config.json"), "{}");
        _temp.WriteText(Path.Combine("src", "Game A (1990)(Ocean).adf"), "AAAA");
        _temp.WriteText(Path.Combine("src", "Game B (1991)(Ocean).adf"), "BBBBBB");
        _destination = Path.Combine(_temp.Path, "dst");
        _vm = TestMainViewModel.Create(_dialogs, config);
        _vm.Directories.SourceDir = Path.Combine(_temp.Path, "src");
        _vm.Directories.DestinationDir = _destination;
        _vm.UiLocale = "en";
    }

    public void Dispose() => _temp.Dispose();

    private ExecutionViewModel Execution => _vm.Execution;

    [Fact]
    public void UndoIssueKinds_ShouldEachHaveADistinctLabelInBothLanguages()
    {
        var keys = Enum.GetValues<UndoIssueKind>().Select(ExecutionViewModel.GetUndoIssueKey).ToList();

        keys.Should().OnlyHaveUniqueItems();
        foreach (var key in keys.Append("undo.log_completed").Append("undo.log_failed"))
        {
            LocalizationService.Translations["fr"].Should().ContainKey(key);
            LocalizationService.Translations["en"].Should().ContainKey(key);
        }
    }

    [Fact]
    public void AtRest_MainButtonShouldSimulateAndProgressShouldBeHidden()
    {
        Execution.PrimaryActionText.Should().Be("Simulate (Dry-Run)");
        Execution.PrimaryActionTooltip.Should().Be(_vm.Localization.Tr("panel4.tooltip_simulate"));
        Execution.CanRunPrimaryAction.Should().BeTrue();
        Execution.PrimaryActionCommand.CanExecute(null).Should().BeTrue();
        Execution.IsProgressVisible.Should().BeFalse();
        Execution.IsSimulationCurrent.Should().BeFalse("nothing was simulated yet");
        Execution.IsSortSummaryVisible.Should().BeFalse();

        _vm.UiLocale = "fr";
        Execution.PrimaryActionText.Should().Be("Simuler (Dry-Run)");
    }

    [Fact]
    public async Task MainButton_ShouldSimulateThenPutTheSortForwardWithItsSummary()
    {
        await Execution.PrimaryActionCommand.ExecuteAsync(null);

        Execution.LastStats!.ValidCount.Should().Be(2);
        Execution.IsDryRunOperation.Should().BeTrue();
        Execution.IsSimulationCurrent.Should().BeTrue();
        Execution.IsSortSummaryVisible.Should().BeTrue();
        Execution.SortSummaryText.Should().Be($"Copy 2 files (10 B) to {_destination}");
        Execution.SortSummaryTooltip.Should().StartWith(Execution.SortSummaryText).And.Contain("Start Sorting");
        Execution.IsProgressVisible.Should().BeFalse("the progress is only shown while an operation runs");
        Directory.Exists(_destination).Should().BeFalse("a simulation writes nothing");

        // The summary follows the language
        _vm.UiLocale = "fr";
        Execution.SortSummaryText.Should().Be($"Copier 2 fichiers (10 o) vers {_destination}");
    }

    [Fact]
    public async Task MainButton_WhileARunIsInProgress_ShouldStopIt()
    {
        for (var i = 0; i < 20; i++)
        {
            _temp.WriteText(Path.Combine("src", $"Game {i:00} (1990)(Ocean).adf"), new string('x', i + 1));
        }

        var duringRun = new List<(string Text, bool CanRun, bool ProgressVisible)>();
        var stopRequested = false;
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            // The pipeline reports its progress on its own thread: the first message after the start stops the run
            if (stopRequested || !Execution.IsPipelineRunning || e.PropertyName != nameof(ExecutionViewModel.RawStatusMessage))
            {
                return;
            }

            duringRun.Add((Execution.PrimaryActionText, Execution.CanRunPrimaryAction, Execution.IsProgressVisible));
            Execution.PrimaryActionCommand.Execute(null);
            stopRequested = Execution.IsStopRequested;
            duringRun.Add((Execution.PrimaryActionText, Execution.CanRunPrimaryAction, Execution.IsProgressVisible));
        }

        Execution.PropertyChanged += OnChanged;
        try
        {
            await Execution.PrimaryActionCommand.ExecuteAsync(null);
        }
        finally
        {
            Execution.PropertyChanged -= OnChanged;
        }

        stopRequested.Should().BeTrue();
        duringRun[0].Should().Be(("Stop", true, true), "the main button stops a running simulation");
        duringRun[^1].Should().Be(("Stop", false, true), "a stop already requested cannot be requested again");
        Execution.StatusMessage.Should().Be("Operation cancelled.");
        Execution.IsPipelineRunning.Should().BeFalse();
        Execution.IsStopRequested.Should().BeFalse();
        Execution.PrimaryActionText.Should().Be("Simulate (Dry-Run)");
        Execution.IsProgressVisible.Should().BeFalse();
        Execution.IsSimulationCurrent.Should().BeFalse("a cancelled simulation does not describe the sort");
    }

    [Fact]
    public async Task StatusWhileARunIsInProgress_ShouldNameItsStepAndNeverSayReady()
    {
        var duringRun = new List<string>();
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (Execution.IsPipelineRunning && e.PropertyName == nameof(ExecutionViewModel.RawStatusMessage))
            {
                duringRun.Add(Execution.RawStatusMessage);
            }
        }

        Execution.PropertyChanged += OnChanged;
        try
        {
            await Execution.PrimaryActionCommand.ExecuteAsync(null);
        }
        finally
        {
            Execution.PropertyChanged -= OnChanged;
        }

        duringRun.Should().NotContain("Ready.", "the listing of a large source takes minutes, and nothing would seem to run");
        duringRun.Should().ContainInOrder(
            "Starting the simulation...",
            "Looking for the source files...",
            "Selecting the versions...",
            "Computing the target paths...");
        duringRun.Should().Contain(m => m.StartsWith("Analyzing: ", StringComparison.Ordinal));
        Execution.StatusMessage.Should().Be("Completed.");
    }

    [Fact]
    public void CurrentStep_ShouldFollowTheLanguage()
    {
        Execution.ReportProgress(0, 0, 0, 0, 0.0, LocalizedMessage.Create("progress.listing_files_count", ("count", 1234)));
        Execution.StatusMessage.Should().Be("Looking for files: 1234 found");

        _vm.UiLocale = "fr";

        Execution.StatusMessage.Should().Be("Recherche des fichiers : 1234 trouvés");
        Execution.CountsText.Should().Be("Fichiers : 0 / 0");
    }

    [Fact]
    public async Task ChangingAnOption_ShouldHideTheSummaryUntilTheSimulatedOptionsAreBack()
    {
        await Execution.SimulateAsync();
        Execution.IsSimulationCurrent.Should().BeTrue();

        _vm.TosecFilters.ExcludeBadDumps = !_vm.TosecFilters.ExcludeBadDumps;
        Execution.IsSimulationCurrent.Should().BeFalse();
        Execution.IsSortSummaryVisible.Should().BeFalse();

        _vm.TosecFilters.ExcludeBadDumps = !_vm.TosecFilters.ExcludeBadDumps;
        Execution.IsSimulationCurrent.Should().BeTrue("the options are those of the simulation again");

        _vm.Directories.DestinationDir = Path.Combine(_temp.Path, "other");
        Execution.IsSimulationCurrent.Should().BeFalse();
        _vm.Directories.DestinationDir = _destination;
        Execution.IsSimulationCurrent.Should().BeTrue();

        // The ordered lists of the priorities tab are options too
        var criteria = _vm.Priorities.TosecCriteriaOrder;
        criteria.SelectedItem = criteria.Items[^2];
        criteria.MoveDown();
        Execution.IsSimulationCurrent.Should().BeFalse();
    }

    [Fact]
    public async Task ChangingTheLanguagePriority_ShouldHideTheSummary()
    {
        await Execution.SimulateAsync();
        Execution.IsSimulationCurrent.Should().BeTrue();

        var language = _vm.Priorities.LanguagePriority;
        language.AddSelected(language.AvailableItems.First());

        Execution.IsSimulationCurrent.Should().BeFalse();
    }

    [Theory]
    [InlineData(TransferMode.Move, "Move 2 files (10 B) to {0}")]
    [InlineData(TransferMode.HardLink, "Create hard links for 2 files (10 B) in {0}")]
    [InlineData(TransferMode.SymbolicLink, "Create symbolic links for 2 files (10 B) in {0}")]
    [InlineData(TransferMode.Zip, "Compress 2 files (10 B max.) into ZIP archives in {0}")]
    public async Task Summary_ShouldNameTheTransferMode(TransferMode mode, string expected)
    {
        _vm.Directories.SelectedTransferMode = mode;

        await Execution.SimulateAsync();

        Execution.SortSummaryText.Should().Be(string.Format(expected, _destination));
    }

    [Fact]
    public async Task Summary_ShouldCountTheNonConformingFilesWrittenToUnmatched()
    {
        _temp.WriteText(Path.Combine("src", "readme.txt"), "123");

        await Execution.SimulateAsync();

        Execution.LastStats!.UnmatchedCount.Should().Be(1);
        Execution.SortSummaryText.Should().Be($"Copy 2 files + 1 non-conforming (13 B) to {_destination}");

        _temp.WriteText(Path.Combine("src", "notes.txt"), "45");
        await Execution.SimulateAsync();
        Execution.SortSummaryText.Should().Be($"Copy 2 files + 2 non-conforming (15 B) to {_destination}");
    }

    [Fact]
    public async Task Summary_ShouldHandleASingleFileAndNoFile()
    {
        File.Delete(Path.Combine(_temp.Path, "src", "Game B (1991)(Ocean).adf"));
        await Execution.SimulateAsync();
        Execution.SortSummaryText.Should().Be($"Copy 1 file (4 B) to {_destination}");

        File.Delete(Path.Combine(_temp.Path, "src", "Game A (1990)(Ocean).adf"));
        await Execution.SimulateAsync();
        Execution.SortSummaryText.Should().Be($"No file to write to {_destination}");
    }

    [Fact]
    public async Task RealSort_ShouldNoLongerPutTheSortForward()
    {
        await Execution.SimulateAsync();
        Execution.IsSimulationCurrent.Should().BeTrue();

        await Execution.StartSortAsync();

        Execution.IsDryRunOperation.Should().BeFalse();
        Directory.EnumerateFiles(_destination, "Game A*.adf", SearchOption.AllDirectories).Should().ContainSingle();
        Execution.IsSimulationCurrent.Should().BeFalse("the sort changed the destination: the simulation is out of date");
        Execution.IsSortSummaryVisible.Should().BeFalse();
    }

    [Fact]
    public async Task SortStartedWithoutSimulation_ShouldKeepItsConfirmation()
    {
        Execution.IsSimulationCurrent.Should().BeFalse();
        _dialogs.NextConfirmationResults.Enqueue(false);

        await Execution.StartSortAsync();

        _dialogs.ShownMessages.Should().ContainSingle(m => m.Level == "CONFIRM");
        Directory.Exists(_destination).Should().BeFalse("the confirmation was refused");
    }

    // =========================================================================
    // Closing the window during an operation
    // =========================================================================

    private string[] DestinationRoms() =>
        Directory.Exists(_destination) ? [.. Directory.EnumerateFiles(_destination, "*.adf", SearchOption.AllDirectories)] : [];

    [Fact]
    public async Task CloseDuringASort_ShouldAskThenStopTheSortAndWaitForItsEnd()
    {
        for (var i = 0; i < 20; i++)
        {
            _temp.WriteText(Path.Combine("src", $"Game {i:00} (1990)(Ocean).adf"), new string('x', i + 1));
        }

        var isWriting = false;
        Task<bool>? close = null;
        Task? whenIdle = null;
        var starting = _vm.Localization.Tr("panel4.status_starting_sort");
        var triggered = 0;
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            // The first progress message of the pipeline, once the sort can be stopped; its threads may report at once
            if (!Execution.IsPipelineRunning || e.PropertyName != nameof(ExecutionViewModel.RawStatusMessage)
                || Execution.RawStatusMessage == starting || Interlocked.Exchange(ref triggered, 1) == 1)
            {
                return;
            }

            isWriting = Execution.IsWritingToDisk;
            whenIdle = Execution.WhenIdleAsync();
            close = Execution.ConfirmCloseWhileWritingAsync(ask: true);
        }

        Execution.PropertyChanged += OnChanged;
        try
        {
            await Execution.StartSortAsync();
        }
        finally
        {
            Execution.PropertyChanged -= OnChanged;
        }

        isWriting.Should().BeTrue("a real sort writes to the disk");
        (await close!).Should().BeTrue();
        _dialogs.ShownMessages.Should().ContainSingle(m => m.Title == "Close RomSieve")
            .Which.Message.Should().Contain("Stop the sort and close RomSieve?");
        whenIdle!.IsCompleted.Should().BeTrue("the window closes once the sort has ended");
        Execution.StatusMessage.Should().Be("Operation cancelled.", "the sort was stopped");
        DestinationRoms().Should().HaveCountLessThan(22, "the sort stopped before its end");
        Execution.IsWritingToDisk.Should().BeFalse();
        Execution.WhenIdleAsync().IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task CloseDuringAnUndo_ShouldWaitForItsEndWithoutStoppingIt()
    {
        await Execution.StartSortAsync();
        DestinationRoms().Should().HaveCount(2);

        var isWriting = false;
        Task<bool>? close = null;
        Task? whenIdle = null;
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            // The undo starts
            if (close is null && e.PropertyName == nameof(ExecutionViewModel.IsRunning) && Execution.IsRunning)
            {
                isWriting = Execution.IsWritingToDisk;
                whenIdle = Execution.WhenIdleAsync();
                close = Execution.ConfirmCloseWhileWritingAsync(ask: true);
            }
        }

        Execution.PropertyChanged += OnChanged;
        try
        {
            await Execution.UndoLastSortAsync();
        }
        finally
        {
            Execution.PropertyChanged -= OnChanged;
        }

        isWriting.Should().BeTrue("an undo writes to the disk");
        (await close!).Should().BeTrue();
        _dialogs.ShownMessages.Should().ContainSingle(m => m.Title == "Close RomSieve")
            .Which.Message.Should().Contain("Close RomSieve at the end of the undo?");
        whenIdle!.IsCompleted.Should().BeTrue();
        Execution.IsStopRequested.Should().BeFalse("an undo cannot be stopped");
        DestinationRoms().Should().BeEmpty("the undo went on to its end");
    }

    [Fact]
    public async Task EachRun_ShouldWriteItsWholeLogToAFileNextToTheConfiguration()
    {
        await Execution.SimulateAsync();
        await Execution.StartSortAsync();
        await Execution.UndoLastSortAsync();

        var logs = Directory.GetFiles(Path.Combine(_temp.Path, "config", "logs"), "run-*.log").Order(StringComparer.Ordinal).ToList();
        logs.Should().HaveCount(3, "a simulation, a sort and an undo");
        var simulation = File.ReadAllLines(logs[0]);
        simulation[0].Should().Contain("Simulation (Dry-Run)");
        simulation.Should().Contain(line => line.Contains("[PLANNED]") && line.Contains("Game A (1990)(Ocean).adf"));
        File.ReadAllLines(logs[1])[0].Should().Contain("Sort Execution");
        File.ReadAllLines(logs[2])[0].Should().Contain("Undo Last Sort");
        _vm.Log.Entries.Should().Contain(e => e.CleanMessage == $"Full log of this run: {logs[2]}");
        _vm.Log.RunFile.Should().BeNull("no run is going on");
    }

    [Fact]
    public void LogFile_ShouldKeepTheEntriesThePanelDrops()
    {
        var log = new LogViewModel(_vm.Localization);
        var file = Path.Combine(_temp.Path, "panel.log");
        using (log.RunFile = Core.Services.RunLogFile.Create(_temp.Path, "Test"))
        {
            for (var i = 0; i < LogViewModel.MaxEntries; i++)
            {
                log.Append(LogLevel.Info, LocalizedMessage.Verbatim($"line {i}"));
            }

            log.AppendBatch([new LogMessage(LogLevel.Warn, LocalizedMessage.Verbatim("batched"))]);
            file = log.RunFile!.Path;
        }

        log.Entries.Should().HaveCount(LogViewModel.MaxEntries);
        var lines = File.ReadAllLines(file);
        lines.Should().HaveCount(LogViewModel.MaxEntries + 2, "the title, every line and the batch");
        lines[1].Should().EndWith("[INFO] line 0");
        lines[^1].Should().EndWith("[WARN] batched");
    }

    [Fact]
    public async Task SortConfirmation_ShouldSayWhenTheDestinationLacksRoom()
    {
        await Execution.SimulateAsync();
        Execution.AvailableBytesOf = _ => 4;

        var partialSummary = Execution.BuildPartialSortSummary(Execution.LastItems, gameCount: 2);
        await Execution.StartSortAsync();

        partialSummary.Should().EndWith("Room left in the destination: only 4 B");
        _dialogs.ShownMessages.Should().Contain(m => m.Level == "CONFIRM")
            .Which.Message.Should().Contain("<b>Not enough room:</b> the destination has only 4 B left, and this sort may write up to 10 B there.");
    }

    [Fact]
    public async Task SortConfirmation_WithRoomEnough_ShouldNotWarn()
    {
        await Execution.SimulateAsync();
        Execution.AvailableBytesOf = _ => 10;

        Execution.BuildPartialSortSummary(Execution.LastItems, gameCount: 2).Should().NotContain("Room left");
        await Execution.StartSortAsync();

        _dialogs.ShownMessages.Should().Contain(m => m.Level == "CONFIRM")
            .Which.Message.Should().NotContain("Not enough room");
    }

    [Fact]
    public async Task PartialUndo_ShouldSayWhatCanBeRetriedAndBeResumed()
    {
        _vm.Directories.SelectedTransferMode = TransferMode.Move;
        await Execution.StartSortAsync();
        var gameA = Path.Combine(_temp.Path, "src", "Game A (1990)(Ocean).adf");
        File.WriteAllText(gameA, "in the way");

        await Execution.UndoLastSortAsync();

        _dialogs.ShownMessages.Should().Contain(m => m.Level == "WARN" && m.Title == "Undo Last Sort")
            .Which.Message.Should().Contain("1 of these operations can be retried");
        Execution.HasUndoableSort.Should().BeTrue("the operation left can be retried");

        File.Delete(gameA);
        _dialogs.ShownMessages.Clear();
        await Execution.UndoLastSortAsync();

        _dialogs.ShownMessages.Should().Contain(m => m.Level == "CONFIRM")
            .Which.Message.Should().Contain("Resume the undo").And.Contain("Operations left to undo: 1");
        _dialogs.ShownMessages.Should().Contain(m => m.Level == "INFO")
            .Which.Message.Should().Contain("Operations undone: 1 / 1");
        File.ReadAllText(gameA).Should().Be("AAAA");
        Execution.HasUndoableSort.Should().BeFalse();
    }

    [Fact]
    public async Task Simulation_ShouldNotHoldTheWindow()
    {
        var isWriting = true;
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (Execution.IsPipelineRunning && e.PropertyName == nameof(ExecutionViewModel.RawStatusMessage))
            {
                isWriting = Execution.IsWritingToDisk;
            }
        }

        Execution.PropertyChanged += OnChanged;
        try
        {
            await Execution.SimulateAsync();
        }
        finally
        {
            Execution.PropertyChanged -= OnChanged;
        }

        isWriting.Should().BeFalse("a simulation writes nothing: closing the window ends it at once");
    }
}
