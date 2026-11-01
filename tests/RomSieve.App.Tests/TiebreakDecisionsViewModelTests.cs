using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// Order of the 1G1R criteria: after a simulation of the current options, each criterion shows the games it decided,
/// kept up to date by a winner forced by hand and hidden as soon as an option changes.
/// </summary>
public sealed class TiebreakDecisionsViewModelTests : IDisposable
{
    private readonly TempDirectoryFixture _tmp = new();
    private readonly MainViewModel _vm;

    public TiebreakDecisionsViewModelTests()
    {
        foreach (var name in new[]
                 {
                     "Alpha (USA).sfc", "Alpha (Japan).sfc",
                     "Bravo (USA) (v1.0).sfc", "Bravo (USA) (v1.1).sfc",
                     "Charlie (USA).sfc", "Charlie (Japan).sfc",
                     "Delta (USA).sfc"
                 })
        {
            _tmp.WriteText(Path.Combine("src", name), name);
        }

        _vm = TestMainViewModel.Create(new HeadlessDialogService(), _tmp.WriteText(Path.Combine("config", "config.json"), "{}"));
        _vm.UiLocale = "en";
        _vm.Directories.SourceDir = Path.Combine(_tmp.Path, "src");
        _vm.Directories.DestinationDir = _tmp.GetFilePath("dst");
        _vm.Directories.SelectedNamingConvention = NamingConvention.NoIntro;
        _vm.Priorities.OneGameOneRom = true;
        _vm.Priorities.LanguagePriority.SetPriorityCodes(["en"]);
    }

    public void Dispose() => _tmp.Dispose();

    private TiebreakOrderViewModel Order => _vm.Priorities.NoIntroCriteriaOrder;

    private int? Decided(TiebreakCriterion criterion) => Order.Items.Single(i => i.Criterion == criterion).DecidedGames;

    [Fact]
    public void BeforeAnySimulation_NoCountShouldBeShown()
    {
        Order.HasDecisions.Should().BeFalse();
        Order.Items.Should().OnlyContain(i => i.DecidedGames == null && !i.HasDecisions);
    }

    [Fact]
    public async Task Simulation_ShouldShowTheGamesEachCriterionDecided()
    {
        await _vm.Execution.SimulateAsync();

        Order.HasDecisions.Should().BeTrue();
        _vm.Priorities.MameCriteriaOrder.HasDecisions.Should().BeTrue();
        Decided(TiebreakCriterion.LanguageRank).Should().Be(2);
        Decided(TiebreakCriterion.Version).Should().Be(1);
        Decided(TiebreakCriterion.Revision).Should().Be(0);
        var language = Order.Items.Single(i => i.Criterion == TiebreakCriterion.LanguageRank);
        language.ToolTipText.Should().Be("The release in the highest-ranked of your priority languages.\nDecided 2 games in the last simulation.");
        language.DecidedNothing.Should().BeFalse();
        Order.Items.Single(i => i.Criterion == TiebreakCriterion.Revision).DecidedNothing.Should().BeTrue();
    }

    [Fact]
    public async Task CriterionWithoutEffect_ShouldSayWhyThenWhatItDecided()
    {
        await _vm.Execution.SimulateAsync();

        // Without a DAT, no version is known as a clone
        Order.Items.Single(i => i.Criterion == TiebreakCriterion.ParentClone).ToolTipText.Should().Be(
            "The release the DAT names as the parent rather than a clone.\n"
            + "No effect on this collection: without a DAT, no version is known as a clone.\nDecided no game in the last simulation.");
    }

    [Fact]
    public async Task ForcedWinner_ShouldUpdateTheCounts()
    {
        await _vm.Execution.SimulateAsync();

        _vm.PreviewTable.Force1G1RWinner(_vm.PreviewTable.AllPreviewItems.Single(r => r.SourceName == "Alpha (Japan).sfc"));

        _vm.Execution.IsSimulationCurrent.Should().BeTrue("a winner forced by hand is not an option of the sort");
        Decided(TiebreakCriterion.LanguageRank).Should().Be(1, "the forced winner of Alpha was chosen by no criterion");
    }

    [Fact]
    public async Task ChangedOption_ShouldHideTheCountsUntilTheSimulatedOptionsAreBack()
    {
        await _vm.Execution.SimulateAsync();

        Order.SelectedItem = Order.Items.Single(i => i.Criterion == TiebreakCriterion.Version);
        Order.MoveUp();
        Order.HasDecisions.Should().BeFalse("the counts described another order");
        Decided(TiebreakCriterion.Version).Should().BeNull();

        Order.MoveDown();
        Order.HasDecisions.Should().BeTrue();
        Decided(TiebreakCriterion.Version).Should().Be(1);
    }

    private TiebreakCriterionItemViewModel Item(TiebreakCriterion criterion) => Order.Items.Single(i => i.Criterion == criterion);

    private List<string> FilteredNames() => _vm.PreviewTable.FilteredPreviewItems.Select(r => r.SourceName).Order(StringComparer.Ordinal).ToList();

    [Fact]
    public async Task DecidedCount_ShouldOpenTheResultsOnTheGamesTheCriterionDecided()
    {
        Item(TiebreakCriterion.LanguageRank).ShowDecidedGamesCommand.CanExecute(null).Should().BeFalse("nothing was simulated");
        await _vm.Execution.SimulateAsync();
        _vm.Navigation.CurrentStep = WorkflowStep.Languages;
        Item(TiebreakCriterion.Revision).ShowDecidedGamesCommand.CanExecute(null).Should().BeFalse("the criterion decided no game");

        Item(TiebreakCriterion.LanguageRank).ShowDecidedGamesCommand.Execute(null);

        _vm.Navigation.CurrentStep.Should().Be(WorkflowStep.Results);
        _vm.ActiveBottomTabIndex.Should().Be(0);
        _vm.PreviewTable.HasCriterionFilter.Should().BeTrue();
        _vm.PreviewTable.CriterionFilterText.Should().Be(
            "Only the games decided by the “Language (priority rank)” criterion are shown: the kept version and those this criterion discarded.");
        FilteredNames().Should().Equal("Alpha (Japan).sfc", "Alpha (USA).sfc", "Charlie (Japan).sfc", "Charlie (USA).sfc");

        _vm.PreviewTable.IsGroupedByGame = true;
        _vm.PreviewTable.DisplayedRows.OfType<PreviewGroupHeaderViewModel>().Should().HaveCount(Item(TiebreakCriterion.LanguageRank).DecidedGames!.Value,
            "the table shows as many games as the count");

        _vm.PreviewTable.ClearCriterionFilterCommand.Execute(null);
        _vm.PreviewTable.HasCriterionFilter.Should().BeFalse();
        _vm.PreviewTable.FilteredPreviewItems.Should().HaveCount(7);
    }

    [Fact]
    public async Task CriterionFilter_ShouldLeaveOutTheVersionsAnotherCriterionDiscarded()
    {
        // Charlie (Japan) loses to the language, Charlie (USA) to its revision, which the version criterion reads first
        _tmp.WriteText(Path.Combine("src", "Charlie (USA) (Rev 1).sfc"), "Charlie (USA) (Rev 1).sfc");
        await _vm.Execution.SimulateAsync();
        _vm.PreviewTable.AllPreviewItems.Single(r => r.SourceName == "Charlie (USA).sfc").Item.EliminatedBy.Should().Be(TiebreakCriterion.Version);

        _vm.ShowGamesDecidedBy(TiebreakCriterion.LanguageRank);
        FilteredNames().Should().Equal("Alpha (Japan).sfc", "Alpha (USA).sfc", "Charlie (Japan).sfc", "Charlie (USA) (Rev 1).sfc");

        _vm.ShowGamesDecidedBy(TiebreakCriterion.Version);
        FilteredNames().Should().Equal("Bravo (USA) (v1.0).sfc", "Bravo (USA) (v1.1).sfc", "Charlie (USA) (Rev 1).sfc", "Charlie (USA).sfc");
    }

    [Fact]
    public async Task CriterionFilter_ShouldCombineWithTheSearchAndFollowAForcedWinner()
    {
        await _vm.Execution.SimulateAsync();
        _vm.ShowGamesDecidedBy(TiebreakCriterion.LanguageRank);

        _vm.PreviewTable.PreviewSearchText = "charlie";
        FilteredNames().Should().Equal("Charlie (Japan).sfc", "Charlie (USA).sfc");
        _vm.PreviewTable.PreviewSearchText = string.Empty;

        _vm.PreviewTable.Force1G1RWinner(_vm.PreviewTable.AllPreviewItems.Single(r => r.SourceName == "Alpha (Japan).sfc"));

        FilteredNames().Should().Equal(["Charlie (Japan).sfc", "Charlie (USA).sfc"], "the language no longer decides Alpha");
    }

    [Fact]
    public async Task CriterionFilter_ShouldBeTranslatedAndForgottenWithTheRows()
    {
        await _vm.Execution.SimulateAsync();
        _vm.ShowGamesDecidedBy(TiebreakCriterion.Version);

        _vm.UiLocale = "fr";
        _vm.PreviewTable.CriterionFilterText.Should().Contain("« Version la plus récente »");

        // The table left empty (a failed run) forgets which games it showed
        _vm.PreviewTable.Clear();
        _vm.PreviewTable.HasCriterionFilter.Should().BeFalse();
    }

    [Fact]
    public async Task SimulationWithout1G1R_ShouldShowNoCount()
    {
        _vm.Priorities.OneGameOneRom = false;

        await _vm.Execution.SimulateAsync();

        _vm.Execution.IsSimulationCurrent.Should().BeTrue();
        Order.HasDecisions.Should().BeFalse();
    }
}
