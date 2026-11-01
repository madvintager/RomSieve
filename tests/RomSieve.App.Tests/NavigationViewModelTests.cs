using System.Text.Json;
using RomSieve.App;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// Navigation by steps: pages shown, way back to the settings, state lines of the steps, results shown when a run
/// starts, and the first page, always the collection.
/// </summary>
public sealed class NavigationViewModelTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly HeadlessDialogService _dialogs = new();
    private readonly string _configPath;
    private readonly string _source;
    private readonly string _destination;
    private readonly MainViewModel _vm;

    public NavigationViewModelTests()
    {
        _configPath = _temp.WriteText(Path.Combine("config", "config.json"), "{}");
        _temp.WriteText(Path.Combine("src", "Game A (1990)(Ocean).adf"), "AAAA");
        _temp.WriteText(Path.Combine("src", "Game B (1991)(Ocean).adf"), "BBBBBB");
        _source = Path.Combine(_temp.Path, "src");
        _destination = Path.Combine(_temp.Path, "dst");
        _vm = TestMainViewModel.Create(_dialogs, _configPath);
    }

    public void Dispose() => _temp.Dispose();

    private NavigationViewModel Navigation => _vm.Navigation;

    [Fact]
    public void Steps_ShouldShowOnePageAtATimeAndMarkTheirEntry()
    {
        Navigation.CurrentStep.Should().Be(WorkflowStep.Collection, "a first launch starts with the collection");
        Navigation.IsCollectionShown.Should().BeTrue();
        Navigation.CollectionEntry.IsSelectedStep.Should().BeTrue();

        Navigation.LanguagesEntry.SelectCommand.Execute(null);
        Navigation.CurrentStep.Should().Be(WorkflowStep.Languages);
        new[] { Navigation.IsCollectionShown, Navigation.IsFiltersShown, Navigation.IsLanguagesShown, Navigation.IsOutputShown, Navigation.IsResultsShown }
            .Should().Equal(false, false, true, false, false);
        Navigation.LanguagesEntry.IsSelectedStep.Should().BeTrue();
        Navigation.SelectionEntry.IsSelectedGroup.Should().BeTrue("the group of the shown sub-step is emphasized");
        Navigation.SelectionEntry.IsSelectedStep.Should().BeFalse("only the sub-step is marked");
        Navigation.CollectionEntry.IsCurrent.Should().BeFalse();

        // The group always leads to its first sub-step, whichever was shown last
        Navigation.OutputEntry.SelectCommand.Execute(null);
        Navigation.SelectionEntry.IsSelectedGroup.Should().BeFalse();
        Navigation.SelectionEntry.SelectCommand.Execute(null);
        Navigation.CurrentStep.Should().Be(WorkflowStep.Filters);
    }

    [Fact]
    public void Results_ShouldLeadBackToTheLastSettingsStep()
    {
        Navigation.CurrentStep = WorkflowStep.Output;
        Navigation.ShowResults();
        Navigation.IsResultsShown.Should().BeTrue();
        Navigation.IsSettingsShown.Should().BeFalse();
        Navigation.LastSettingsStep.Should().Be(WorkflowStep.Output);

        Navigation.ReturnToSettingsCommand.Execute(null);
        Navigation.CurrentStep.Should().Be(WorkflowStep.Output);
    }

    [Fact]
    public void CollectionState_ShouldTellAMissingSourceOrDat()
    {
        Navigation.CollectionEntry.IndicatorTone.Should().Be(StepIndicatorTone.Warning);
        Navigation.CollectionEntry.IndicatorText.Should().Be("Source à choisir");

        _vm.Directories.SourceDir = Path.Combine(_temp.Path, "nowhere");
        Navigation.CollectionEntry.IndicatorText.Should().Be("Source introuvable");

        _vm.Directories.SourceDir = _source;
        Navigation.CollectionEntry.HasIndicator.Should().BeFalse("the source exists and no DAT is required");

        _vm.Directories.DatFilePath = Path.Combine(_temp.Path, "missing.dat");
        Navigation.CollectionEntry.IndicatorText.Should().Be("DAT introuvable");

        _vm.UiLocale = "en";
        Navigation.CollectionEntry.IndicatorText.Should().Be("DAT not found");
        Navigation.CollectionEntry.Label.Should().Be("Collection");
        Navigation.LanguagesEntry.Label.Should().Be("Languages and 1G1R");
    }

    [Fact]
    public void CollectionState_WithADatButNoSource_ShouldSayWhatTheDatAloneAllows()
    {
        Navigation.CollectionEntry.IndicatorTooltip.Should().BeNull();

        // A DAT alone is enough for the filtered DAT of the catalog: an information, not a warning
        _vm.Directories.DatFilePath = _temp.WriteText("catalog.dat", "clrmamepro ( name \"Test\" )");
        Navigation.CollectionEntry.IndicatorTone.Should().Be(StepIndicatorTone.Info);
        Navigation.CollectionEntry.IndicatorText.Should().Be("DAT seul, sans source");
        Navigation.CollectionEntry.IndicatorTooltip.Should().Be(
            "Sans source, seul le DAT filtré du catalogue peut être exporté (menu Exporter). Choisissez une source pour simuler ou trier.");

        _vm.UiLocale = "en";
        Navigation.CollectionEntry.IndicatorText.Should().Be("DAT only, no source");
        Navigation.CollectionEntry.IndicatorTooltip.Should().StartWith("Without a source,");
        _vm.UiLocale = "fr";

        // A DAT that cannot be read allows nothing
        _vm.Directories.DatFilePath = Path.Combine(_temp.Path, "missing.dat");
        Navigation.CollectionEntry.IndicatorTone.Should().Be(StepIndicatorTone.Warning);
        Navigation.CollectionEntry.IndicatorText.Should().Be("DAT introuvable");
        Navigation.CollectionEntry.IndicatorTooltip.Should().BeNull();

        _vm.Directories.DatFilePath = string.Empty;
        Navigation.CollectionEntry.IndicatorText.Should().Be("Source à choisir");

        // With a source, the line only tells what is wrong
        _vm.Directories.DatFilePath = Path.Combine(_temp.Path, "catalog.dat");
        _vm.Directories.SourceDir = _source;
        Navigation.CollectionEntry.HasIndicator.Should().BeFalse();
    }

    [Fact]
    public void CollectionState_ShouldNoticeASourceMountedAfterItWasTyped()
    {
        // A network share mounted on demand appears without its path changing
        var share = Path.Combine(_temp.Path, "share");
        _vm.Directories.SourceDir = share;
        Navigation.CollectionEntry.IndicatorText.Should().Be("Source introuvable");

        Directory.CreateDirectory(share);
        Navigation.CurrentStep = WorkflowStep.Output;
        Navigation.CollectionEntry.HasIndicator.Should().BeFalse("changing step checks the source again");

        Directory.Delete(share);
        Navigation.RefreshIndicators();
        Navigation.CollectionEntry.IndicatorText.Should().Be("Source introuvable", "the window checks it again when activated");
    }

    [Fact]
    public void OutputState_ShouldTellAMissingOrOverlappingDestination()
    {
        _vm.Directories.SourceDir = _source;
        Navigation.OutputEntry.IndicatorText.Should().Be("Destination à choisir");

        // Same rule as the check made before any run (SortConfigValidator)
        _vm.Directories.DestinationDir = Path.Combine(_source, "sorted");
        _vm.Directories.IsRecursive = true;
        Navigation.OutputEntry.IndicatorTone.Should().Be(StepIndicatorTone.Warning);
        Navigation.OutputEntry.IndicatorText.Should().Be("Chevauche la source");

        _vm.Directories.IsRecursive = false;
        Navigation.OutputEntry.HasIndicator.Should().BeFalse("a destination inside a source scanned without its subfolders is allowed");

        _vm.Directories.DestinationDir = _destination;
        Navigation.OutputEntry.HasIndicator.Should().BeFalse();
    }

    [Fact]
    public async Task Simulation_ShouldShowTheResultsAndTellWhenTheyAreOutOfDate()
    {
        _vm.Directories.SourceDir = _source;
        _vm.Directories.DestinationDir = _destination;
        Navigation.CurrentStep = WorkflowStep.Filters;
        Navigation.ResultsEntry.HasIndicator.Should().BeFalse("nothing ran yet");

        await _vm.Execution.SimulateAsync();

        Navigation.CurrentStep.Should().Be(WorkflowStep.Results);
        _vm.ActiveBottomTabIndex.Should().Be(0, "a simulation shows its table");
        Navigation.ResultsEntry.IndicatorTone.Should().Be(StepIndicatorTone.Info);
        Navigation.ResultsEntry.IndicatorText.Should().Be("2 fichiers");

        // Back to the settings: the results stay, and say when they no longer describe the options
        Navigation.ReturnToSettingsCommand.Execute(null);
        Navigation.CurrentStep.Should().Be(WorkflowStep.Filters);
        _vm.TosecFilters.ExcludeDemos = !_vm.TosecFilters.ExcludeDemos;
        Navigation.ResultsEntry.IndicatorTone.Should().Be(StepIndicatorTone.Warning);
        Navigation.ResultsEntry.IndicatorText.Should().Be("2 fichiers, périmés");
        Navigation.CurrentStep.Should().Be(WorkflowStep.Filters, "nothing leaves the settings by itself");
    }

    [Fact]
    public async Task RealSortAndUndo_ShouldShowTheLog()
    {
        _vm.Directories.SourceDir = _source;
        _vm.Directories.DestinationDir = _destination;

        await _vm.Execution.StartSortAsync();
        Navigation.CurrentStep.Should().Be(WorkflowStep.Results);
        _vm.ActiveBottomTabIndex.Should().Be(1, "a real sort shows its log");
        Navigation.ResultsEntry.IndicatorTone.Should().Be(StepIndicatorTone.Info, "the results of a real sort are never out of date");

        Navigation.CurrentStep = WorkflowStep.Collection;
        _vm.ActiveBottomTabIndex = 0;
        _vm.Execution.CanUndoLastSort.Should().BeTrue();
        await _vm.Execution.UndoLastSortAsync();
        Navigation.CurrentStep.Should().Be(WorkflowStep.Results, "the undo writes its report in the log");
        _vm.ActiveBottomTabIndex.Should().Be(1);
    }

    [Fact]
    public async Task RefusedSort_ShouldStayOnTheSettings()
    {
        _vm.Directories.SourceDir = _source;
        _vm.Directories.DestinationDir = _destination;
        Navigation.CurrentStep = WorkflowStep.Output;
        _dialogs.NextConfirmationResults.Enqueue(false);

        await _vm.Execution.StartSortAsync();

        Navigation.CurrentStep.Should().Be(WorkflowStep.Output, "nothing started");
    }

    [Theory]
    [InlineData(WorkflowStep.Filters)]
    [InlineData(WorkflowStep.Languages)]
    [InlineData(WorkflowStep.Output)]
    public async Task ShownStep_ShouldNotBeSaved_TheApplicationOpensOnTheCollection(WorkflowStep step)
    {
        Navigation.CurrentStep = step;
        await _vm.SaveConfigurationAsync();

        using (var saved = JsonDocument.Parse(await File.ReadAllTextAsync(_configPath)))
        {
            saved.RootElement.TryGetProperty("active_step", out _).Should().BeFalse();
        }

        TestMainViewModel.Create(new HeadlessDialogService(), _configPath)
            .Navigation.CurrentStep.Should().Be(WorkflowStep.Collection, "the first page is always the same");
    }

    [Fact]
    public void SelectionEntry_ShouldAlwaysLeadToTheFilters()
    {
        Navigation.LanguagesEntry.SelectCommand.Execute(null);
        Navigation.OutputEntry.SelectCommand.Execute(null);

        Navigation.SelectionEntry.SelectCommand.Execute(null);

        Navigation.CurrentStep.Should().Be(WorkflowStep.Filters, "the languages were the last selection step shown");
    }
}
