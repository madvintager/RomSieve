using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RomSieve.App.ViewModels;
using RomSieve.App.Views;
using RomSieve.Core.Models;

namespace RomSieve.App.Tests;

/// <summary>
/// Differential simulations: a new simulation of the same source updates the rows in place, marks the rows new or
/// changed since the previous one, and a real sort or another source starts from an empty table.
/// </summary>
public sealed partial class GuiTests
{
    private const string Alpha = "Alpha (1990)(Ocean).adf";
    private const string Cracked = "Beta (1991)(Ocean)[cr].adf";
    private const string Gamma = "Gamma (1992)(Psygnosis).adf";
    private const string Junk = "junk.bin";

    private (MainViewModel Vm, string Source, string Destination) CreateDifferentialSetup()
    {
        var source = Path.Combine(_tempDir, "diff_src");
        var destination = Path.Combine(_tempDir, "diff_dst");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        foreach (var name in new[] { Alpha, Cracked, Gamma, Junk })
        {
            File.WriteAllText(Path.Combine(source, name), name);
        }

        var vm = CreateViewModel();
        vm.Directories.SourceDir = source;
        vm.Directories.DestinationDir = destination;
        return (vm, source, destination);
    }

    private static PreviewItemViewModel Row(MainViewModel vm, string sourceName) =>
        vm.AllPreviewItems.Single(r => r.SourceName == sourceName);

    private static List<NotifyCollectionChangedEventArgs> CaptureRowEvents(MainViewModel vm, Func<Task> action)
    {
        var events = new List<NotifyCollectionChangedEventArgs>();
        void Handler(object? sender, NotifyCollectionChangedEventArgs e) => events.Add(e);
        vm.FilteredPreviewItems.CollectionChanged += Handler;
        try
        {
            action().GetAwaiter().GetResult();
        }
        finally
        {
            vm.FilteredPreviewItems.CollectionChanged -= Handler;
        }

        return events;
    }

    [Fact]
    public async Task FirstSimulation_ShouldNotCompareWithAnything()
    {
        var (vm, _, _) = CreateDifferentialSetup();

        await vm.Execution.SimulateAsync();

        vm.AllPreviewItems.Should().HaveCount(4).And.OnlyContain(r => r.Change == PreviewRowChange.None);
        vm.PreviewTable.HasComparison.Should().BeFalse();
        vm.StatsBanner.IsChangedChipVisible.Should().BeFalse();
    }

    [Fact]
    public async Task SameSimulationAgain_ShouldKeepTheRowsWithoutAnyNotification()
    {
        var (vm, _, _) = CreateDifferentialSetup();
        await vm.Execution.SimulateAsync();
        var rowsBefore = vm.FilteredPreviewItems.ToList();
        vm.PreviewTable.SelectedPreviewItem = Row(vm, Gamma);

        var events = CaptureRowEvents(vm, vm.Execution.SimulateAsync);

        events.Should().BeEmpty("the visible rows are the same objects in the same order");
        vm.FilteredPreviewItems.Should().Equal(rowsBefore, ReferenceEquals);
        vm.PreviewTable.SelectedPreviewItem.Should().BeSameAs(Row(vm, Gamma));
        vm.AllPreviewItems.Should().OnlyContain(r => r.Change == PreviewRowChange.None);
        vm.StatsBanner.IsChangedChipVisible.Should().BeTrue();
        vm.StatsBanner.ChangedChip.Text.Should().EndWith("0");
        vm.AllPreviewItems.Select(r => r.Item).ToHashSet(ReferenceEqualityComparer.Instance)
            .SetEquals(vm.Execution.LastItems).Should().BeTrue("the rows show the items of the new simulation");
    }

    [Fact]
    public async Task ChangedFilter_ShouldMarkTheRowsWhoseStatusChanged()
    {
        var (vm, _, _) = CreateDifferentialSetup();
        await vm.Execution.SimulateAsync();
        var cracked = Row(vm, Cracked);
        cracked.Item.Status.Should().Be(FileStatus.Excluded);

        vm.TosecFilters.ExcludeModifications = false;
        await vm.Execution.SimulateAsync();

        Row(vm, Cracked).Should().BeSameAs(cracked, "the row of a file still present is kept");
        cracked.Item.Status.Should().Be(FileStatus.Valid);
        cracked.Change.Should().Be(PreviewRowChange.Modified);
        cracked.ChangeDescription.Should().Contain("Exclu");
        cracked.TooltipText.Should().EndWith(cracked.ChangeDescription);
        vm.AllPreviewItems.Where(r => r != cracked).Should().OnlyContain(r => r.Change == PreviewRowChange.None);
        vm.StatsBanner.ChangedChip.Text.Should().EndWith("1");

        // The chip shows the changed rows only, combined with the status chips
        vm.StatsBanner.ToggleChip(StatsBannerViewModel.ChangedFilterKey);
        vm.FilteredPreviewItems.Should().Equal([cracked], ReferenceEquals);
        vm.StatsBanner.ToggleChip("excluded");
        vm.FilteredPreviewItems.Should().BeEmpty();
        vm.StatsBanner.ToggleChip("total");
        vm.FilteredPreviewItems.Should().HaveCount(4);

        // The description follows a language switch
        vm.UiLocale = "en";
        cracked.ChangeDescription.Should().StartWith("Changed since the previous simulation").And.Contain("Excluded");
    }

    [Fact]
    public async Task ChangedPriority_ShouldMarkARowWhoseReasonAloneChanged()
    {
        // Three releases of one game: the one in the first language wins, the two others are excluded by the 1G1R
        var source = Path.Combine(_tempDir, "reason_src");
        Directory.CreateDirectory(source);
        string[] releases = ["Omega (1990)(Ocean)(de).adf", "Omega (1990)(Ocean)(en).adf", "Omega (1990)(Ocean)(fr).adf"];
        foreach (var name in releases)
        {
            File.WriteAllText(Path.Combine(source, name), name);
        }

        var vm = CreateViewModel();
        vm.Directories.SourceDir = source;
        vm.Directories.DestinationDir = Path.Combine(_tempDir, "reason_dst");
        vm.Priorities.OneGameOneRom = true;
        vm.Priorities.LanguagesText = "en, fr, de";
        await vm.Execution.SimulateAsync();
        var german = Row(vm, releases[0]);
        german.Item.Status.Should().Be(FileStatus.Excluded);
        var reasonBefore = german.StatusReasonText;

        // The French release now wins: the German one stays excluded, with the same target, for another reason
        vm.Priorities.LanguagesText = "fr, en, de";
        await vm.Execution.SimulateAsync();

        german.Item.Status.Should().Be(FileStatus.Excluded);
        german.StatusReasonText.Should().NotBe(reasonBefore);
        german.Change.Should().Be(PreviewRowChange.Modified, "its reason changed");
        german.ChangeDescription.Should().StartWith("Raison changée depuis la simulation précédente").And.Contain(reasonBefore);
        Row(vm, releases[1]).Change.Should().Be(PreviewRowChange.Modified);
        Row(vm, releases[2]).Change.Should().Be(PreviewRowChange.Modified);
        Row(vm, releases[2]).ChangeDescription.Should().StartWith("Modifié depuis la simulation précédente", "its status changed");

        // The same simulation again changes nothing, reasons included
        await vm.Execution.SimulateAsync();
        vm.AllPreviewItems.Should().OnlyContain(r => r.Change == PreviewRowChange.None);

        // A change of language is no change of reason: the description is translated, the row stays marked as it was
        vm.Priorities.LanguagesText = "en, fr, de";
        await vm.Execution.SimulateAsync();
        vm.UiLocale = "en";
        german.ChangeDescription.Should().StartWith("Reason changed since the previous simulation");
    }

    [Fact]
    public async Task AddedAndRemovedFiles_ShouldInsertAndRemoveTheirRowsOnly()
    {
        var (vm, source, _) = CreateDifferentialSetup();
        await vm.Execution.SimulateAsync();
        var gamma = Row(vm, Gamma);

        File.Delete(Path.Combine(source, Alpha));
        File.WriteAllText(Path.Combine(source, "Delta (1993)(Ocean).adf"), "delta");
        var events = CaptureRowEvents(vm, vm.Execution.SimulateAsync);

        events.Select(e => e.Action).Should().BeEquivalentTo([NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add]);
        vm.AllPreviewItems.Should().NotContain(r => r.SourceName == Alpha);
        Row(vm, "Delta (1993)(Ocean).adf").Change.Should().Be(PreviewRowChange.Added);
        Row(vm, Gamma).Should().BeSameAs(gamma);
        gamma.Change.Should().Be(PreviewRowChange.None);
        vm.FilteredPreviewItems.Select(r => r.SourceName).Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
        vm.PreviewKnownTitles.Should().Contain("Delta").And.NotContain("Alpha");
    }

    [Fact]
    public async Task DifferentialSimulation_ShouldKeepTheSortChosenByTheUser()
    {
        var (vm, _, destination) = CreateDifferentialSetup();
        await vm.Execution.SimulateAsync();
        vm.PreviewTable.SortPreviewByColumn(2);
        vm.PreviewTable.SortPreviewByColumn(2);
        var order = vm.FilteredPreviewItems.Select(r => r.SourceName).ToList();

        // A new destination changes every target: every placed row is modified, the order stays
        vm.Directories.DestinationDir = destination + "_other";
        await vm.Execution.SimulateAsync();

        vm.PreviewTable.CurrentSortColumn.Should().Be(2);
        vm.PreviewTable.IsSortAscending.Should().BeFalse();
        vm.FilteredPreviewItems.Select(r => r.SourceName).Should().Equal(order);
        vm.AllPreviewItems.Should().OnlyContain(r => r.Change == PreviewRowChange.Modified);
    }

    [Fact]
    public async Task OtherSource_ShouldStartFromAnEmptyTable()
    {
        var (vm, source, _) = CreateDifferentialSetup();
        await vm.Execution.SimulateAsync();
        var otherSource = source + "_copy";
        Directory.CreateDirectory(otherSource);
        File.Copy(Path.Combine(source, Gamma), Path.Combine(otherSource, Gamma));

        vm.Directories.SourceDir = otherSource;
        await vm.Execution.SimulateAsync();

        vm.AllPreviewItems.Should().ContainSingle().Which.Change.Should().Be(PreviewRowChange.None);
        vm.PreviewTable.HasComparison.Should().BeFalse();
        vm.StatsBanner.IsChangedChipVisible.Should().BeFalse();

        // Coming back to the first source does not compare with a simulation of another source
        vm.Directories.SourceDir = source;
        await vm.Execution.SimulateAsync();
        vm.PreviewTable.HasComparison.Should().BeFalse();
        vm.AllPreviewItems.Should().HaveCount(4);
    }

    [Fact]
    public async Task RealSort_ShouldStartFromAnEmptyTableAndNotBeAReference()
    {
        var (vm, _, _) = CreateDifferentialSetup();
        vm.Directories.AppendToDestination = true;
        await vm.Execution.SimulateAsync();
        var rowsOfSimulation = vm.AllPreviewItems.ToList();

        await vm.Execution.StartSortAsync();

        vm.Execution.IsDryRunOperation.Should().BeFalse();
        vm.AllPreviewItems.Should().HaveCount(4).And.NotContain(r => rowsOfSimulation.Contains(r));
        vm.PreviewTable.HasComparison.Should().BeFalse();
        vm.StatsBanner.IsChangedChipVisible.Should().BeFalse();

        await vm.Execution.SimulateAsync();
        vm.PreviewTable.HasComparison.Should().BeFalse("a real sort is never the reference of the next simulation");
    }

    [Fact]
    public async Task FailedDifferentialSimulation_ShouldLeaveAnEmptyTableAndNoReference()
    {
        var (vm, _, _) = CreateDifferentialSetup();
        await vm.Execution.SimulateAsync();
        var datPath = Path.Combine(_tempDir, "broken.dat");
        File.WriteAllText(datPath, "<datafile><game name=");
        vm.Directories.DatFilePath = datPath;

        await vm.Execution.SimulateAsync();

        vm.Execution.StatusMessage.Should().NotBe(vm.I18n["panel4.status_finished"]);
        vm.AllPreviewItems.Should().BeEmpty("as after any failed run");
        vm.StatsBanner.IsChangedChipVisible.Should().BeFalse();

        vm.Directories.DatFilePath = string.Empty;
        await vm.Execution.SimulateAsync();
        vm.PreviewTable.HasComparison.Should().BeFalse();
        vm.AllPreviewItems.Should().HaveCount(4);
    }

    [Fact]
    public async Task DifferentialSimulation_InTheWindow_ShouldKeepTheSelectedRowAndShowItsMarker()
    {
        // The simulations run off the UI thread: awaiting one there while the window is shown corrupts the
        // font cache of the headless platform for the following tests
        var (vm, _, _) = CreateDifferentialSetup();
        var processor = new RomSieve.Core.Tests.Fixtures.CoreTestServices("fr").Processor;
        var firstConfig = vm.BuildConfigUnchecked();
        var first = await processor.ExecuteAsync(firstConfig, isDryRun: true);
        vm.TosecFilters.ExcludeModifications = false;
        var secondConfig = vm.BuildConfigUnchecked();
        var second = await processor.ExecuteAsync(secondConfig, isDryRun: true);

        await RunOnUiThreadAsync(() =>
        {
            var window = new MainWindow { DataContext = vm };
            window.Show();
            vm.Execution.PlayRunForTests(firstConfig, isDryRun: true, first);
            Settle(window);

            var table = window.FindControl<DataGrid>("TablePreview")!;
            var cracked = Row(vm, Cracked);
            table.SelectedItem = cracked;
            Settle(window);

            vm.Execution.PlayRunForTests(secondConfig, isDryRun: true, second);
            Settle(window);

            table.SelectedItem.Should().BeSameAs(cracked);
            cracked.Change.Should().Be(PreviewRowChange.Modified);
            var markers = window.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "RowChangeMarker" && b.IsVisible).ToList();
            markers.Should().ContainSingle().Which.DataContext.Should().BeSameAs(cracked);
            window.Close();
        });
    }

    [Fact]
    public async Task RowChangeMarkers_ShouldHaveAThemeBrushForEachChange()
    {
        await RunOnUiThreadAsync(() =>
        {
            foreach (var change in Enum.GetValues<PreviewRowChange>())
            {
                Application.Current!.TryGetResource($"RowChange{change}MarkerBrush", Application.Current.ActualThemeVariant, out _)
                    .Should().BeTrue($"the marker of {change} rows is a palette color");
            }
        });
    }
}
