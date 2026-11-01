using System.Text.Json;
using RomSieve.App;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// Partial sort of the rows shown (<see cref="PartialSortViewModel"/>): availability, summary, confirmation, a
/// destination completed and never purged, and a selection that is never saved.
/// </summary>
public sealed class PartialSortViewModelTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly HeadlessDialogService _dialogs = new();
    private readonly MainViewModel _vm;
    private readonly string _configPath;
    private readonly string _destination;

    public PartialSortViewModelTests()
    {
        _configPath = _temp.WriteText(Path.Combine("config", "config.json"), "{}");
        _temp.WriteText(Path.Combine("src", "Game A (1990)(Ocean).adf"), "AAAA");
        _temp.WriteText(Path.Combine("src", "Game A (1990)(Ocean)[b].adf"), "BAD");
        _temp.WriteText(Path.Combine("src", "Game B (1991)(Ocean).adf"), "BBBBBB");
        _temp.WriteText(Path.Combine("src", "Game C (1992)(Ocean).adf"), "CC");
        _destination = Path.Combine(_temp.Path, "dst");
        _vm = TestMainViewModel.Create(_dialogs, _configPath);
        _vm.Directories.SourceDir = Path.Combine(_temp.Path, "src");
        _vm.Directories.DestinationDir = _destination;
        _vm.Directories.Pattern = string.Empty;
        _vm.UiLocale = "en";
    }

    public void Dispose() => _temp.Dispose();

    private PartialSortViewModel PartialSort => _vm.PartialSort;

    private IEnumerable<string> DestinationFiles() =>
        Directory.Exists(_destination)
            ? Directory.EnumerateFiles(_destination, "*", SearchOption.AllDirectories)
                .Where(f => !DestinationGuard.IsGuardPath(f))
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)!
            : [];

    [Fact]
    public async Task SortDisplayed_ShouldNeedACurrentSimulationAndACheckedGameShown()
    {
        PartialSort.CanSortDisplayed.Should().BeFalse();
        PartialSort.DisplayedSummaryText.Should().Be("Run a simulation of the current options first.");

        // Without a filter, the rows shown are every checked game: here the whole simulation
        await _vm.Execution.SimulateAsync();
        PartialSort.CanSortDisplayed.Should().BeTrue("every game is checked and shown");
        PartialSort.DisplayedUnitCount.Should().Be(3);
        PartialSort.DisplayedSummaryText.Should().Be($"Copy 3 games (3 files) (12 B) to {_destination}");

        _vm.PreviewTable.PreviewSearchText = "Game A";
        PartialSort.CanSortDisplayed.Should().BeTrue();
        PartialSort.SortDisplayedCommand.CanExecute(null).Should().BeTrue();
        PartialSort.DisplayedUnitCount.Should().Be(1);
        PartialSort.DisplayedSummaryText.Should().Be($"Copy 1 game (1 file) (4 B) to {_destination}, a part of the simulation");

        // A changed option makes the table out of date
        _vm.TosecFilters.ExcludeBadDumps = false;
        PartialSort.CanSortDisplayed.Should().BeFalse();
        PartialSort.SortDisplayedCommand.CanExecute(null).Should().BeFalse();
        PartialSort.DisplayedSummaryText.Should().Be("Run a simulation of the current options first.");

        _vm.TosecFilters.ExcludeBadDumps = true;
        PartialSort.CanSortDisplayed.Should().BeTrue("the simulated options are back");

        _vm.UiLocale = "fr";
        PartialSort.DisplayedSummaryText.Should().Be($"Copier 1 jeu (1 fichier) (4 o) vers {_destination}, une partie de la simulation");
    }

    /// <summary>
    /// The checked games shown, wherever the status chips and the scroll of the table stand: with every row shown
    /// ("Total"), the games checked by hand, not the whole simulation; unchecked games shown choose nothing.
    /// </summary>
    [Fact]
    public async Task SortDisplayed_ShouldSortTheCheckedGamesOfTheRowsShown()
    {
        await _vm.Execution.SimulateAsync();
        var rows = _vm.PreviewTable.AllPreviewItems;
        PartialSort.SetChecked(rows, false);
        PartialSort.CanSortDisplayed.Should().BeFalse();
        PartialSort.DisplayedSummaryText.Should().Be("No checked game among the rows shown.");

        PartialSort.SetChecked(rows.Where(r => r.SourceName.StartsWith("Game B", StringComparison.Ordinal)), true);
        PartialSort.CanSortDisplayed.Should().BeTrue("every row is shown, and Game B is checked");
        PartialSort.DisplayedUnitCount.Should().Be(1);
        PartialSort.DisplayedSummaryText.Should().Be($"Copy 1 game (1 file) (6 B) to {_destination}, a part of the simulation");

        // A filter that hides the checked game leaves nothing to sort
        _vm.PreviewTable.PreviewSearchText = "Game C";
        PartialSort.CanSortDisplayed.Should().BeFalse();

        _vm.PreviewTable.PreviewSearchText = string.Empty;
        await PartialSort.SortDisplayedCommand.ExecuteAsync(null);
        DestinationFiles().Should().Equal("Game B (1991)(Ocean).adf");
    }

    [Fact]
    public async Task SortDisplayed_ShouldOnlyCountTheRowsTheSortWrites()
    {
        await _vm.Execution.SimulateAsync();
        _vm.PreviewTable.AllPreviewItems.Should().Contain(r => r.Item.Status == FileStatus.Excluded);

        // Only the excluded bad dump of Game A is shown: it does not choose its game
        _vm.StatsBanner.ToggleChip("excluded");

        PartialSort.CanSortDisplayed.Should().BeFalse();
        PartialSort.DisplayedSummaryText.Should().Be("No checked game among the rows shown.");
    }

    [Fact]
    public async Task SortDisplayed_ShouldConfirmThenSortOnlyTheGamesShown()
    {
        await _vm.Execution.SimulateAsync();
        _vm.PreviewTable.PreviewSearchText = "Game";
        _vm.StatsBanner.ToggleChip("valid");
        _vm.PreviewTable.PreviewSearchText = "Game B";

        await PartialSort.SortDisplayedCommand.ExecuteAsync(null);

        var confirmation = _dialogs.ShownMessages.Should().ContainSingle(m => m.Level == "CONFIRM").Subject;
        confirmation.Title.Should().Be("Partial Sort Confirmation");
        confirmation.Message.Should().Contain($"Copy 1 game (1 file) (6 B) to {_destination}, a part of the simulation");
        DestinationFiles().Should().Equal("Game B (1991)(Ocean).adf");
        _vm.Execution.LastStats!.NotSelectedCount.Should().Be(2);
        _vm.Execution.IsSimulationCurrent.Should().BeFalse("a real sort is not the reference of the next one");
        _vm.Log.Entries.Should().Contain(e => e.Source != null && e.Source.Key == "log.operation_completed_summary_partial");
    }

    [Fact]
    public async Task SortDisplayed_DeclinedConfirmation_ShouldWriteNothing()
    {
        await _vm.Execution.SimulateAsync();
        _vm.PreviewTable.PreviewSearchText = "Game C";
        _dialogs.NextConfirmationResult = false;

        await PartialSort.SortDisplayedCommand.ExecuteAsync(null);

        Directory.Exists(_destination).Should().BeFalse();
        _vm.Execution.IsSimulationCurrent.Should().BeTrue();
        PartialSort.CanSortDisplayed.Should().BeTrue();
    }

    [Fact]
    public async Task SortDisplayed_ANonEmptyDestination_ShouldBeCompletedWithoutAnyPurge()
    {
        var existing = _temp.WriteText(Path.Combine("dst", "existing.adf"), "kept");
        _vm.Directories.AppendToDestination = false;
        await _vm.Execution.SimulateAsync();
        _vm.PreviewTable.PreviewSearchText = "Game C";

        await PartialSort.SortDisplayedCommand.ExecuteAsync(null);

        // A single confirmation, which says the destination is completed: never the purge one
        _dialogs.ShownMessages.Should().ContainSingle(m => m.Level == "CONFIRM")
            .Which.Message.Should().Contain("adds to it");
        File.ReadAllText(existing).Should().Be("kept");
        Directory.Exists(Path.Combine(_destination, "_purged")).Should().BeFalse();
        DestinationFiles().Should().Equal("Game C (1992)(Ocean).adf", "existing.adf");
    }

    [Fact]
    public async Task DisplayedSelection_ShouldSurviveADifferentialSimulation()
    {
        await _vm.Execution.SimulateAsync();
        _vm.PreviewTable.PreviewSearchText = "Game B";
        var before = PartialSort.DisplayedSelection!.Units.Select(u => u.Key).ToList();

        await _vm.Execution.SimulateAsync();

        _vm.PreviewTable.HasComparison.Should().BeTrue("the second simulation of the source is differential");
        PartialSort.CanSortDisplayed.Should().BeTrue();
        PartialSort.DisplayedSelection!.Units.Select(u => u.Key).Should().Equal(before);
    }

    [Fact]
    public async Task Selection_ShouldNeverBeSavedNorChangeTheSimulationFingerprint()
    {
        await _vm.Execution.SimulateAsync();
        await _vm.SaveConfigurationAsync();
        var configBefore = await File.ReadAllTextAsync(_configPath);
        var profileBefore = _temp.GetFilePath("before.json");
        _dialogs.NextPickedSaveFile = profileBefore;
        await _vm.Profiles.SaveProfileCommand.ExecuteAsync(null);

        _vm.PreviewTable.PreviewSearchText = "Game A";
        PartialSort.CanSortDisplayed.Should().BeTrue();
        _vm.Execution.IsSimulationCurrent.Should().BeTrue("choosing rows is not an option");

        await _vm.SaveConfigurationAsync();
        var profileAfter = _temp.GetFilePath("after.json");
        _dialogs.NextPickedSaveFile = profileAfter;
        await _vm.Profiles.SaveProfileCommand.ExecuteAsync(null);

        (await File.ReadAllTextAsync(_configPath)).Should().Be(configBefore);
        (await File.ReadAllTextAsync(profileAfter)).Should().Be(await File.ReadAllTextAsync(profileBefore));
        using var config = JsonDocument.Parse(configBefore);
        config.RootElement.EnumerateObject().Select(p => p.Name).Should().NotContain(n => n.Contains("selection", StringComparison.Ordinal));
    }

    // =========================================================================
    // Games checked in the table: all of them after a simulation
    // =========================================================================

    private PreviewItemViewModel Row(string name) =>
        _vm.PreviewTable.AllPreviewItems.Single(r => r.SourceName == name);

    [Fact]
    public async Task AfterASimulation_EveryGameTheSortWritesShouldBeChecked()
    {
        await _vm.Execution.SimulateAsync();

        _vm.PreviewTable.AllPreviewItems.Where(r => r.Item.Status != FileStatus.Excluded)
            .Should().OnlyContain(r => r.IsSortChecked && r.CanBeSortChecked);
        PartialSort.IsSelectionAvailable.Should().BeTrue();
        PartialSort.IsPartialSelection.Should().BeFalse();
        PartialSort.CheckedCountText.Should().Be("3 games selected");
        PartialSort.AreShownRowsChecked.Should().BeTrue();

        // Every game checked: the ordinary sort of the whole simulation
        PartialSort.StartButtonText.Should().Be("Start Sorting");
        PartialSort.FooterSummaryText.Should().Be(_vm.Execution.SortSummaryText);
        _vm.StatsBanner.VolumeText.Should().Be("Volume: 15 B → 12 B");
    }

    [Fact]
    public async Task UncheckingAGame_ShouldSortOnlyTheCheckedOnes()
    {
        await _vm.Execution.SimulateAsync();
        var gameA = Row("Game A (1990)(Ocean).adf");

        gameA.IsSortChecked = false;
        PartialSort.ToggleRowCheck(gameA);

        gameA.IsSortChecked.Should().BeFalse();
        PartialSort.IsPartialSelection.Should().BeTrue();
        PartialSort.CheckedCountText.Should().Be("2 games selected");
        PartialSort.StartButtonText.Should().Be("Sort Selection");
        PartialSort.IsStartPutForward.Should().BeTrue();
        PartialSort.FooterSummaryText.Should().Be($"Copy 2 games (2 files) (8 B) to {_destination}, a part of the simulation");
        _vm.StatsBanner.VolumeText.Should().Be("Volume: 15 B → 8 B (selection)");
        _vm.StatsBanner.VolumeTooltip.Should().StartWith("Left: the size of all the scanned files. Right: what sorting the checked games");

        await PartialSort.StartCommand.ExecuteAsync(null);

        DestinationFiles().Should().Equal("Game B (1991)(Ocean).adf", "Game C (1992)(Ocean).adf");
        _dialogs.ShownMessages.Should().ContainSingle(m => m.Level == "CONFIRM").Which.Title.Should().Be("Partial Sort Confirmation");

        // The rows now show the results of the real sort, which cannot be chosen for another sort
        PartialSort.StartButtonText.Should().Be("Start Sorting");
        PartialSort.IsSelectionAvailable.Should().BeFalse();
        _vm.PreviewTable.AllPreviewItems.Should().OnlyContain(r => !r.CanBeSortChecked && !r.IsSortChecked);
    }

    [Fact]
    public async Task UncheckingEveryGame_ShouldLeaveNothingToSort()
    {
        await _vm.Execution.SimulateAsync();

        PartialSort.AreShownRowsChecked = false;

        _vm.PreviewTable.AllPreviewItems.Should().OnlyContain(r => !r.IsSortChecked);
        PartialSort.CheckedCountText.Should().Be("No game selected");
        PartialSort.StartButtonText.Should().Be("Sort Selection");
        PartialSort.StartCommand.CanExecute(null).Should().BeFalse();
        PartialSort.IsStartPutForward.Should().BeFalse();
        PartialSort.FooterSummaryText.Should().Be("Check games in the table.");
        _vm.StatsBanner.VolumeText.Should().Be("Volume: 15 B → 0.00 MB (selection)");
    }

    [Fact]
    public async Task CheckingEveryGameAgain_ShouldBeTheOrdinarySortOfTheWholeSimulation()
    {
        await _vm.Execution.SimulateAsync();
        PartialSort.SetChecked([Row("Game B (1991)(Ocean).adf")], false);
        PartialSort.IsPartialSelection.Should().BeTrue();

        PartialSort.SetChecked([Row("Game B (1991)(Ocean).adf")], true);

        PartialSort.IsPartialSelection.Should().BeFalse();
        PartialSort.StartButtonText.Should().Be("Start Sorting");
        _vm.StatsBanner.VolumeText.Should().Be("Volume: 15 B → 12 B");
        await PartialSort.StartCommand.ExecuteAsync(null);

        DestinationFiles().Should().Equal("Game A (1990)(Ocean).adf", "Game B (1991)(Ocean).adf", "Game C (1992)(Ocean).adf");
        _dialogs.ShownMessages.Should().ContainSingle(m => m.Level == "CONFIRM").Which.Title.Should().NotBe("Partial Sort Confirmation");
    }

    [Fact]
    public async Task AGameTheSortDoesNotWrite_ShouldNotBeCheckable()
    {
        _temp.WriteText(Path.Combine("src", "Game D (1993)(Ocean)[b].adf"), "D");
        await _vm.Execution.SimulateAsync();
        var onlyBadDump = Row("Game D (1993)(Ocean)[b].adf");

        onlyBadDump.CanBeSortChecked.Should().BeFalse();
        onlyBadDump.IsSortChecked.Should().BeFalse();
        PartialSort.SetChecked([onlyBadDump], true);

        onlyBadDump.IsSortChecked.Should().BeFalse();
        PartialSort.IsPartialSelection.Should().BeFalse("a game the sort does not write is not left out");
    }

    [Fact]
    public async Task AnExcludedVersion_ShouldNeverBeCheckedWithTheKeptReleaseOfItsGame()
    {
        await _vm.Execution.SimulateAsync();
        var kept = Row("Game A (1990)(Ocean).adf");
        var badDump = Row("Game A (1990)(Ocean)[b].adf");
        badDump.Item.Status.Should().Be(FileStatus.Excluded);

        // The sort does not write the excluded version: its box stays empty, whatever its game
        badDump.CanBeSortChecked.Should().BeFalse();
        badDump.IsSortChecked.Should().BeFalse();

        PartialSort.SetChecked([kept], false);
        PartialSort.SetChecked([kept], true);
        kept.IsSortChecked.Should().BeTrue();
        badDump.IsSortChecked.Should().BeFalse("only the kept release is sorted");

        // Checking or unchecking the excluded version does nothing to its game
        PartialSort.SetChecked([badDump], false);
        kept.IsSortChecked.Should().BeTrue();
        PartialSort.IsPartialSelection.Should().BeFalse();

        // In the view grouped by game, the header follows the kept release only
        _vm.PreviewTable.IsGroupedByGame = true;
        var header = _vm.PreviewTable.GroupHeaders.Single(h => h.GameTitle == "Game A");
        header.IsSortChecked.Should().BeTrue();
        PartialSort.SetChecked([header], false);
        kept.IsSortChecked.Should().BeFalse();
        PartialSort.SetChecked([header], true);
        kept.IsSortChecked.Should().BeTrue();
        badDump.IsSortChecked.Should().BeFalse();
    }

    [Fact]
    public async Task OutdatedSimulation_ShouldNotSortTheCheckedGames()
    {
        await _vm.Execution.SimulateAsync();
        PartialSort.SetChecked([Row("Game A (1990)(Ocean).adf")], false);

        _vm.TosecFilters.ExcludeBadDumps = false;

        PartialSort.StartCommand.CanExecute(null).Should().BeFalse("the checked games come from an outdated simulation");
        PartialSort.IsStartPutForward.Should().BeFalse();
        PartialSort.FooterSummaryText.Should().Be("Run a simulation of the current options first.");
        PartialSort.CheckedUnitCount.Should().Be(2, "the checks stay until the next simulation");
    }

    [Fact]
    public async Task GridSelection_ShouldBeCheckedAndUncheckedAtOnce()
    {
        await _vm.Execution.SimulateAsync();
        PartialSort.UncheckGridSelectionCommand.CanExecute(null).Should().BeFalse();
        PartialSort.SetGridSelection([Row("Game B (1991)(Ocean).adf"), Row("Game C (1992)(Ocean).adf")]);

        PartialSort.UncheckGridSelectionCommand.Execute(null);
        PartialSort.CheckedCountText.Should().Be("1 game selected");

        // The space bar checks rows that are not all checked, and unchecks them otherwise
        PartialSort.ToggleGridSelection();
        PartialSort.CheckedUnitCount.Should().Be(3);
        PartialSort.ToggleGridSelection();
        PartialSort.CheckedUnitCount.Should().Be(1);
        PartialSort.CheckGridSelectionCommand.Execute(null);
        PartialSort.IsPartialSelection.Should().BeFalse();
    }

    [Fact]
    public async Task GameHeader_ShouldCheckAllTheFilesOfItsGame()
    {
        _temp.WriteText(Path.Combine("src", "notes.txt"), "N");
        _temp.WriteText(Path.Combine("src", "readme.txt"), "R");
        await _vm.Execution.SimulateAsync();
        _vm.PreviewTable.IsGroupedByGame = true;
        var gameA = _vm.PreviewTable.GroupHeaders.Single(h => h.GameTitle == "Game A");
        var unidentified = _vm.PreviewTable.GroupHeaders.Single(h => h.IsUnidentified);
        gameA.IsSortChecked.Should().BeTrue();
        unidentified.IsSortChecked.Should().BeTrue();
        PartialSort.CheckedCountText.Should().Be("5 games or files selected");

        PartialSort.SetChecked([gameA], false);
        gameA.IsSortChecked.Should().BeFalse();
        gameA.Files.Should().OnlyContain(f => !f.IsSortChecked);

        // The files without a game are each their own unit: their header unchecks them all
        PartialSort.SetChecked([unidentified], false);
        PartialSort.CheckedCountText.Should().Be("2 games selected");

        PartialSort.SetChecked([Row("readme.txt")], true);
        unidentified.IsSortChecked.Should().BeFalse("one of its files is still unchecked");
        PartialSort.CheckedCountText.Should().Be("3 games or files selected");
    }

    /// <summary>
    /// A file whose name gives its game but that the DAT does not know is isolated in <c>_unmatched</c>, apart from its
    /// game: it has its own box, so that unchecking it leaves the kept release of its game checked, and the reverse. The
    /// header of the game still checks and unchecks all its files.
    /// </summary>
    [Fact]
    public async Task NonConformingFileOfAGame_ShouldBeCheckedApartFromTheKeptReleaseOfItsGame()
    {
        _temp.WriteText(Path.Combine("src", "Game A (1990)(Ocean)[a].adf"), "ALT");
        _vm.Directories.DatFilePath = _temp.WriteText("catalog.xml", """
            <?xml version="1.0"?>
            <datafile>
              <header><name>Commodore Amiga - Games - [ADF]</name><description>TOSEC</description></header>
              <game name="Game A (1990)(Ocean)"><rom name="Game A (1990)(Ocean).adf" size="4" crc="9b0d08f1"/></game>
              <game name="Game B (1991)(Ocean)"><rom name="Game B (1991)(Ocean).adf" size="6" crc="c9ffc5ce"/></game>
              <game name="Game C (1992)(Ocean)"><rom name="Game C (1992)(Ocean).adf" size="2" crc="75581e13"/></game>
            </datafile>
            """);
        await _vm.Execution.SimulateAsync();

        var kept = Row("Game A (1990)(Ocean).adf");
        var alternate = Row("Game A (1990)(Ocean)[a].adf");
        var badDump = Row("Game A (1990)(Ocean)[b].adf");
        kept.Item.Status.Should().Be(FileStatus.Valid);
        alternate.Item.Status.Should().Be(FileStatus.Unmatched);
        alternate.Item.Parsed.IsConform.Should().BeTrue();
        alternate.SelectionUnitKey.Should().NotBe(kept.SelectionUnitKey);
        alternate.IsSortChecked.Should().BeTrue();
        PartialSort.CheckedCountText.Should().Be("5 games or files selected");

        PartialSort.SetChecked([alternate, badDump], false);
        kept.IsSortChecked.Should().BeTrue();
        alternate.IsSortChecked.Should().BeFalse();
        PartialSort.CheckedCountText.Should().Be("3 games selected");

        PartialSort.SetChecked([kept], false);
        PartialSort.SetChecked([alternate], true);
        alternate.IsSortChecked.Should().BeTrue();
        kept.IsSortChecked.Should().BeFalse();
        badDump.IsSortChecked.Should().BeFalse();

        _vm.PreviewTable.IsGroupedByGame = true;
        var gameA = _vm.PreviewTable.GroupHeaders.Single(h => h.GameTitle == "Game A");
        gameA.IsSortChecked.Should().BeFalse("two of its files are unchecked");
        PartialSort.SetChecked([gameA], true);
        gameA.Files.Should().OnlyContain(f => f.IsSortChecked);
        PartialSort.SetChecked([gameA], false);
        gameA.Files.Should().OnlyContain(f => !f.IsSortChecked);

        PartialSort.SetChecked([alternate], true);
        await PartialSort.StartCommand.ExecuteAsync(null);
        DestinationFiles().Should().Equal("Game A (1990)(Ocean)[a].adf", "Game B (1991)(Ocean).adf", "Game C (1992)(Ocean).adf");
        File.Exists(Path.Combine(_destination, PathOrganizerService.UnmatchedFolderName, "Game A (1990)(Ocean)[a].adf")).Should().BeTrue();
    }

    /// <summary>
    /// Non-conforming files left in the source (isolation in <c>_unmatched</c> off): the sort does not write them, so
    /// their box stays empty, they are neither counted nor sorted, and the summary leaves them out.
    /// </summary>
    [Fact]
    public async Task NonConformingFilesNotIsolated_ShouldNotBeCheckable()
    {
        _temp.WriteText(Path.Combine("src", "readme.txt"), "R");
        _vm.Directories.IsolateUnmatched = false;
        await _vm.Execution.SimulateAsync();

        var readme = Row("readme.txt");
        readme.Item.Status.Should().Be(FileStatus.Unmatched);
        readme.CanBeSortChecked.Should().BeFalse();
        readme.IsSortChecked.Should().BeFalse();
        PartialSort.CheckedCountText.Should().Be("3 games selected");
        _vm.Execution.SortSummaryText.Should().Be($"Copy 3 files (12 B) to {_destination}");

        await PartialSort.StartCommand.ExecuteAsync(null);
        DestinationFiles().Should().Equal("Game A (1990)(Ocean).adf", "Game B (1991)(Ocean).adf", "Game C (1992)(Ocean).adf");
    }

    [Fact]
    public async Task UncheckedGames_ShouldSurviveADifferentialSimulation_WhoseNewGamesAreChecked()
    {
        await _vm.Execution.SimulateAsync();
        PartialSort.SetChecked([Row("Game B (1991)(Ocean).adf")], false);
        _temp.WriteText(Path.Combine("src", "Game E (1994)(Ocean).adf"), "EEEEE");

        await _vm.Execution.SimulateAsync();

        _vm.PreviewTable.HasComparison.Should().BeTrue();
        Row("Game B (1991)(Ocean).adf").IsSortChecked.Should().BeFalse();
        Row("Game E (1994)(Ocean).adf").IsSortChecked.Should().BeTrue("a new game arrives checked");
        PartialSort.CheckedCountText.Should().Be("3 games selected");
        PartialSort.CheckedSelection!.Units.Should().HaveCount(3);

        // Another source starts again with every game checked
        _vm.Directories.SourceDir = Path.Combine(_temp.Path, "other");
        PartialSort.IsPartialSelection.Should().BeFalse();
    }

    [Fact]
    public async Task UncheckedGames_ShouldNeverBeSaved()
    {
        await _vm.Execution.SimulateAsync();
        await _vm.SaveConfigurationAsync();
        var configBefore = await File.ReadAllTextAsync(_configPath);
        var profileBefore = _temp.GetFilePath("before.json");
        _dialogs.NextPickedSaveFile = profileBefore;
        await _vm.Profiles.SaveProfileCommand.ExecuteAsync(null);

        PartialSort.SetChecked([Row("Game C (1992)(Ocean).adf")], false);
        _vm.Execution.IsSimulationCurrent.Should().BeTrue();
        await _vm.SaveConfigurationAsync();
        var profileAfter = _temp.GetFilePath("after.json");
        _dialogs.NextPickedSaveFile = profileAfter;
        await _vm.Profiles.SaveProfileCommand.ExecuteAsync(null);

        (await File.ReadAllTextAsync(_configPath)).Should().Be(configBefore);
        (await File.ReadAllTextAsync(profileAfter)).Should().Be(await File.ReadAllTextAsync(profileBefore));
    }

    [Fact]
    public async Task CheckAllBox_ShouldCheckAndUncheckTheGamesOfTheRowsShown()
    {
        await _vm.Execution.SimulateAsync();
        PartialSort.AreShownRowsChecked.Should().BeTrue();
        PartialSort.CanCheckShownRows.Should().BeTrue();

        _vm.PreviewTable.PreviewSearchText = "Game A";
        PartialSort.AreShownRowsChecked = false;
        PartialSort.CheckedCountText.Should().Be("2 games selected, 2 of them hidden");
        PartialSort.AreShownRowsChecked.Should().BeFalse();

        // With every row shown, only some games are checked
        _vm.PreviewTable.PreviewSearchText = string.Empty;
        PartialSort.AreShownRowsChecked.Should().BeNull();

        PartialSort.AreShownRowsChecked = false;
        PartialSort.HasCheckedUnits.Should().BeFalse();
        PartialSort.AreShownRowsChecked = true;
        PartialSort.CheckedCountText.Should().Be("3 games selected");
        PartialSort.IsPartialSelection.Should().BeFalse();
    }

    [Fact]
    public async Task StatsBanner_Fat32Warning_ShouldOnlyCountTheCheckedGames()
    {
        await _vm.Execution.SimulateAsync();
        _vm.StatsBanner.IsFat32WarningVisible.Should().BeFalse();
        Row("Game B (1991)(Ocean).adf").Item.FileSize = Fat32Limit.MaxFileSize + 1;

        PartialSort.SetChecked([Row("Game A (1990)(Ocean).adf")], false);
        _vm.StatsBanner.IsFat32WarningVisible.Should().BeTrue("the large file is in the selection");
        _vm.StatsBanner.Fat32WarningTooltip.Should().Contain("Game B (1991)(Ocean).adf");

        PartialSort.SetChecked([Row("Game B (1991)(Ocean).adf")], false);
        _vm.StatsBanner.IsFat32WarningVisible.Should().BeFalse();

        _vm.UiLocale = "fr";
        _vm.StatsBanner.VolumeText.Should().Be("Volume : 15 o → 2 o (sélection)");
    }

    [Fact]
    public async Task DatOfTheCollection_ShouldFollowTheCheckBoxes()
    {
        await _vm.Execution.SimulateAsync();
        var path = Path.Combine(_temp.Path, "collection.xml");
        _dialogs.NextPickedSaveFile = path;

        // Every game checked: the whole simulation
        await _vm.ExportDatAsync();
        File.ReadAllText(path).Should().Contain("Game A (1990)(Ocean).adf")
            .And.Contain("Game B (1991)(Ocean).adf")
            .And.Contain("Game C (1992)(Ocean).adf");

        // Some games unchecked: only the checked ones, as the sort of the selection
        PartialSort.SetChecked([Row("Game A (1990)(Ocean).adf"), Row("Game C (1992)(Ocean).adf")], false);
        await _vm.ExportDatAsync();
        File.ReadAllText(path).Should().Contain("Game B (1991)(Ocean).adf")
            .And.NotContain("Game A (1990)(Ocean)")
            .And.NotContain("Game C (1992)(Ocean)");

        // No game checked: nothing is written, and the warning says why
        File.Delete(path);
        PartialSort.AreShownRowsChecked = false;
        await _vm.ExportDatAsync();
        File.Exists(path).Should().BeFalse();
        _dialogs.ShownMessages.Should().ContainSingle(m => m.Level == "WARN")
            .Which.Message.Should().Be(_vm.I18n["dialog.export_dat_no_checked_roms"]);
    }

    [Fact]
    public async Task CheckedCount_ShouldSayHowManyCheckedGamesTheFiltersHide()
    {
        await _vm.Execution.SimulateAsync();
        PartialSort.CheckedCountText.Should().Be("3 games selected");

        _vm.PreviewTable.PreviewSearchText = "Game B";
        PartialSort.CheckedCountText.Should().Be("3 games selected, 2 of them hidden");

        // "Check All" only acts on the rows shown: the hidden games stay checked
        PartialSort.AreShownRowsChecked = false;
        PartialSort.CheckedCountText.Should().Be("2 games selected, 2 of them hidden");

        _vm.PreviewTable.PreviewSearchText = "Game A";
        PartialSort.CheckedCountText.Should().Be("2 games selected, 1 of them hidden");
        _vm.UiLocale = "fr";
        PartialSort.CheckedCountText.Should().Be("2 jeux sélectionnés, dont 1 masqué");
        _vm.UiLocale = "en";

        _vm.PreviewTable.PreviewSearchText = string.Empty;
        PartialSort.CheckedCountText.Should().Be("2 games selected");
    }

    [Fact]
    public async Task SelectionChip_ShouldCountTheCheckedGamesAndShowOnlyTheirRows()
    {
        await _vm.Execution.SimulateAsync();
        _vm.StatsBanner.IsSelectionChipVisible.Should().BeFalse("every game is checked");

        PartialSort.SetChecked([Row("Game A (1990)(Ocean).adf")], false);
        _vm.StatsBanner.IsSelectionChipVisible.Should().BeTrue();
        _vm.StatsBanner.SelectionChip.Text.Should().Be("Selection: 2");
        _vm.StatsBanner.ValidChip.Text.Should().Be("Kept: 3", "the status of a row does not depend on the selection");

        _vm.StatsBanner.ToggleChip(StatsBannerViewModel.SelectionFilterKey);
        _vm.StatsBanner.SelectionChip.IsChecked.Should().BeTrue();
        _vm.PreviewTable.FilteredPreviewItems.Select(r => r.SourceName)
            .Should().BeEquivalentTo("Game B (1991)(Ocean).adf", "Game C (1992)(Ocean).adf");

        // Combined with a status: the excluded bad dump is never checked
        _vm.StatsBanner.ToggleChip("excluded");
        _vm.PreviewTable.FilteredPreviewItems.Should().BeEmpty();
        _vm.StatsBanner.ToggleChip("excluded");

        // A row unchecked while the filter is on stays shown, so that it can be checked again
        PartialSort.SetChecked([Row("Game B (1991)(Ocean).adf")], false);
        _vm.StatsBanner.SelectionChip.Text.Should().Be("Selection: 1");
        _vm.PreviewTable.FilteredPreviewItems.Should().HaveCount(2);

        _vm.UiLocale = "fr";
        _vm.StatsBanner.SelectionChip.Text.Should().Be("Sélection : 1");
        _vm.UiLocale = "en";

        // Every game checked again: the chip and its filter go away
        PartialSort.SetChecked(_vm.PreviewTable.AllPreviewItems.Where(r => r.CanBeSortChecked).ToList(), true);
        _vm.StatsBanner.IsSelectionChipVisible.Should().BeFalse();
        _vm.StatsBanner.ActiveFilters.Should().BeEmpty();
        _vm.StatsBanner.TotalChip.IsChecked.Should().BeTrue();
        _vm.PreviewTable.FilteredPreviewItems.Should().HaveCount(4);
    }
}
