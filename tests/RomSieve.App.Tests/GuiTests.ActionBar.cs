using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using RomSieve.App.Controls;
using RomSieve.App.Views;
using RomSieve.Core.Models;

namespace RomSieve.App.Tests;

/// <summary>
/// Action bar of the execution panel: main button that simulates or stops, sort put forward after a simulation of the
/// current options, exports gathered in one menu, progress shown only while an operation runs.
/// </summary>
public sealed partial class GuiTests
{
    private static DockPanel ActionBar(Window window) =>
        (DockPanel)window.FindControl<Button>("BtnSimulate")!.GetVisualParent()!.GetVisualParent()!;

    [Fact]
    public async Task ActionBar_ShouldGatherTheExportsInOneMenuAndLeaveTheLogClearingToTheLogTab()
    {
        await RunOnUiThreadAsync(() =>
        {
            var window = new MainWindow(CreateViewModel());
            window.Show();
            try
            {
                Settle(window);
                var bar = ActionBar(window);
                var barButtons = bar.GetVisualDescendants().OfType<Button>().Select(b => b.Name).ToList();

                barButtons.Should().Contain(["BtnSimulate", "BtnStart", "BtnUndo", "BtnExportMenu"]);
                barButtons.Should().NotContain(["BtnClearLog", "BtnStop", "BtnExport", "BtnExportDat", "BtnExportMissList"]);
                window.FindControl<Button>("BtnStop").Should().BeNull("the main button stops a running operation");

                // The log is cleared from its own tab, under a name that cannot be mistaken for the purge of the destination
                var clearLog = window.FindControl<Button>("BtnClearLog")!;
                clearLog.Content.Should().Be("Effacer les messages");
                clearLog.FindLogicalAncestorOfType<TabItem>()!.Header.Should().Be("Journal des opérations");
                bar.GetVisualDescendants().OfType<TextBlock>().Should().NotContain(t => t.Text == "Purger le journal");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ExportMenu_ShouldKeepTheConditionsAndTooltipsOfEachExport()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                Settle(window);
                var menuButton = window.FindControl<DropDownButton>("BtnExportMenu")!;
                menuButton.IsEnabled.Should().BeFalse("no export is possible before a run");
                ToolTip.GetTip(menuButton).Should().Be(vm.Localization.Tr("panel4.tooltip_export_menu"));

                var items = CreateLargeItemSet().Take(20).ToList();
                vm.Execution.CompleteRunForTests(new SortExecutionResult(
                    new ProcessStats { TotalScanned = items.Count, ProcessedCount = items.Count }, items, [], null));
                Settle(window);
                menuButton.IsEnabled.Should().BeTrue();

                menuButton.Flyout!.ShowAt(menuButton);
                Settle(window);
                var report = window.FindControl<MenuItem>("MenuExportReport")!;
                var dat = window.FindControl<MenuItem>("MenuExportDat")!;
                var missList = window.FindControl<MenuItem>("MenuExportMissList")!;
                var catalogDat = window.FindControl<MenuItem>("MenuExportCatalogDat")!;

                report.Header.Should().Be("Rapport (JSON, CSV)...");
                dat.Header.Should().Be("DAT de votre collection...");
                catalogDat.Header.Should().Be("DAT filtré du catalogue (sans source)...");

                // The results of the last run first, then, apart, the DAT alone
                ((MenuFlyout)menuButton.Flyout).Items.Select(i => i is MenuItem m ? m.Name : i?.GetType().Name)
                    .Should().Equal("MenuExportReport", "MenuExportDat", "MenuExportMissList", nameof(Separator), "MenuExportCatalogDat");
                ToolTip.GetTip(catalogDat).Should().Be(vm.Localization.Tr("panel4.tooltip_export_catalog_dat"));
                catalogDat.Command.Should().BeSameAs(vm.ExportCatalogDatCommand);
                catalogDat.IsEffectivelyEnabled.Should().BeFalse("no DAT is chosen");
                missList.Header.Should().Be("Miss List (TXT, CSV, fixdat)...");
                ToolTip.GetTip(report).Should().Be(vm.Localization.Tr("panel4.tooltip_export_report"));
                ToolTip.GetTip(dat).Should().Be(vm.Localization.Tr("panel4.tooltip_export_dat"));
                ToolTip.GetTip(missList).Should().Be(vm.Localization.Tr("panel4.tooltip_export_miss_list"));

                // Same commands, same conditions: no DAT was loaded, so there is no Miss List
                report.Command.Should().BeSameAs(vm.ExportReportCommand);
                dat.Command.Should().BeSameAs(vm.ExportDatCommand);
                missList.Command.Should().BeSameAs(vm.ExportMissListCommand);
                report.IsEffectivelyEnabled.Should().BeTrue();
                dat.IsEffectivelyEnabled.Should().BeTrue("the run kept files");
                missList.IsEffectivelyEnabled.Should().BeFalse("the run had no DAT");

                vm.UiLocale = "en";
                report.Header.Should().Be("Report (JSON, CSV)...");
                ToolTip.GetTip(report).Should().Be(vm.Localization.Tr("panel4.tooltip_export_report"));
                menuButton.Content.Should().Be("Export");
                menuButton.Flyout.Hide();
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ExportMenu_WithADatAndNoRun_ShouldOfferOnlyTheFilteredDatOfTheCatalog()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                Settle(window);
                var menuButton = window.FindControl<DropDownButton>("BtnExportMenu")!;
                menuButton.IsEnabled.Should().BeFalse();

                vm.Directories.DatFilePath = Path.Combine(Path.GetTempPath(), "catalog.xml");
                Settle(window);
                menuButton.IsEnabled.Should().BeTrue("the filtered DAT of the catalog needs no run");

                menuButton.Flyout!.ShowAt(menuButton);
                Settle(window);
                window.FindControl<MenuItem>("MenuExportCatalogDat")!.IsEffectivelyEnabled.Should().BeTrue();
                window.FindControl<MenuItem>("MenuExportReport")!.IsEffectivelyEnabled.Should().BeFalse();
                window.FindControl<MenuItem>("MenuExportDat")!.IsEffectivelyEnabled.Should().BeFalse();
                window.FindControl<MenuItem>("MenuExportMissList")!.IsEffectivelyEnabled.Should().BeFalse();
                menuButton.Flyout.Hide();

                vm.Directories.DatFilePath = string.Empty;
                Settle(window);
                menuButton.IsEnabled.Should().BeFalse();
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task MainButton_ShouldStopARunningOperation_AndShowTheProgressOnlyMeanwhile()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm) { Height = 960 };
            window.Show();
            try
            {
                vm.Navigation.ShowResults();
                Settle(window);
                var main = window.FindControl<Button>("BtnSimulate")!;
                var progressRow = window.FindControl<Grid>("ProgressRow")!;
                var table = window.FindControl<DataGrid>("TablePreview")!;
                var tableHeightAtRest = table.Bounds.Height;

                main.Content.Should().Be("Simuler (Dry-Run)");
                main.Classes.Should().NotContain("stop");
                progressRow.IsVisible.Should().BeFalse("the progress is useless when nothing runs");
                window.FindControl<ProgressBar>("ProgressBar")!.IsEffectivelyVisible.Should().BeFalse();

                vm.Execution.IsPipelineRunning = true;
                vm.Execution.IsRunning = true;
                Settle(window);
                main.Content.Should().Be("Arrêter");
                main.Classes.Should().Contain("stop");
                main.Command.Should().BeSameAs(vm.Execution.PrimaryActionCommand);
                main.IsEffectivelyEnabled.Should().BeTrue("the running operation can be stopped");
                ToolTip.GetTip(main).Should().Be(vm.Localization.Tr("panel4.tooltip_stop"));
                progressRow.IsVisible.Should().BeTrue();
                table.Bounds.Height.Should().BeLessThan(tableHeightAtRest, "the progress takes its room from the table");

                vm.Execution.IsRunning = false;
                vm.Execution.IsPipelineRunning = false;
                Settle(window);
                main.Content.Should().Be("Simuler (Dry-Run)");
                main.Classes.Should().NotContain("stop");
                progressRow.IsVisible.Should().BeFalse();
                table.Bounds.Height.Should().Be(tableHeightAtRest, "the room of the progress goes back to the table");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task StartButton_ShouldBePutForwardOnlyAfterASimulationOfTheCurrentOptions()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var destination = Path.Combine(_tempDir, "dst");
            vm.Directories.SourceDir = Path.Combine(_tempDir, "src");
            vm.Directories.DestinationDir = destination;
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                Settle(window);
                var start = window.FindControl<Button>("BtnStart")!;
                var summary = window.FindControl<TextBlock>("LblSortSummary")!;
                start.Classes.Should().Contain("secondary-action").And.NotContain("primary");
                start.IsEffectivelyEnabled.Should().BeTrue("the sort stays available without a simulation");
                summary.IsVisible.Should().BeFalse();
                window.FindControl<Button>("BtnUndo")!.Classes.Should().Contain("secondary-action");

                var items = CreateLargeItemSet().Take(10).ToList();
                var stats = new ProcessStats { TotalScanned = 10, ProcessedCount = 10, ValidCount = 6, ValidBytes = 3 * 1024 * 1024 };
                vm.Execution.PlayRunForTests(vm.BuildConfigUnchecked(), isDryRun: true, new SortExecutionResult(stats, items, [], null));
                Settle(window);

                start.Classes.Should().Contain("primary");
                summary.IsVisible.Should().BeTrue();
                summary.Text.Should().Be($"Copier 6 fichiers (3.00 Mo) vers {destination}");
                ToolTip.GetTip(summary)!.ToString().Should().StartWith(summary.Text);

                vm.Directories.IsRecursive = !vm.Directories.IsRecursive;
                Settle(window);
                start.Classes.Should().NotContain("primary", "the options changed since the simulation");
                summary.IsVisible.Should().BeFalse();
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The first simulation hides the progress row in the layout pass that shows its statistics: the chips and totals
    /// of the banner, wider than before, were then left at their former width (cut numbers) until the next simulation.
    /// </summary>
    [Fact]
    public async Task StatsBanner_StatisticsShownWhenTheProgressRowHides_ShouldNotBeCut()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.ShowResults();
                Settle(window);
                var progressRow = window.FindControl<Grid>("ProgressRow")!;
                vm.StatsBanner.Reset();
                progressRow.IsVisible = true;
                Settle(window);

                // As at the end of a first simulation: the progress row hides as the statistics appear
                progressRow.IsVisible = false;
                vm.StatsBanner.UpdateStats(new ProcessStats { TotalScanned = 12_345, ValidCount = 12_345, ExcludedCount = 1_234, TotalBytes = 123_456_789_012 });
                Settle(window);

                foreach (var name in new[] { "ChipTotal", "ChipValid", "ChipExcluded", "LblVolume" })
                {
                    var control = window.FindControl<Control>(name)!;
                    control.DesiredSize.Width.Should().BeGreaterThan(0);
                    control.Bounds.Width.Should().BeApproximately(control.DesiredSize.Width, 0.5, $"{name} is arranged at its new width");
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The banner of a partial sort, the fullest there is: large numbers, the chips of the changed rows and of the
    /// selection, the DAT completion and the volume of the selection. At the default width, the totals no longer fit
    /// beside the chips: they go to a second row instead of being cut (as they were, the volume of the selection
    /// losing its end). The ordinary banner of a simulation stays on one row. Measured with the real fonts only
    /// (ROMSIEVE_HEADLESS_SKIA=1).
    /// </summary>
    [Fact]
    public async Task StatsBanner_TooWideForOneRow_ShouldPutTheTotalsUnderTheChips()
    {
        if (Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") != "1")
        {
            return;
        }

        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.ShowResults();
                var banner = window.FindControl<TwoSidedWrapPanel>("PanelStatsBanner")!;
                var chips = window.FindControl<Control>("ChipTotal")!.GetVisualParent<StackPanel>()!;
                var totals = window.FindControl<Control>("LblVolume")!.GetVisualParent<StackPanel>()!;
                var cases =
                    from locale in vm.Localization.GetAvailableLocales().Select(l => l.Code)
                    from isPartial in new[] { false, true }
                    select (locale, isPartial);
                foreach (var (locale, isPartial) in cases)
                {
                    // Set after the language: its change refreshes the selection of the table, which has none
                    vm.UiLocale = locale;
                    vm.StatsBanner.UpdateStats(new ProcessStats
                    {
                        TotalScanned = 16_402,
                        ValidCount = 5_616,
                        ExcludedCount = 10_786,
                        TotalBytes = 1_218_634_563_092,
                        DatTotalRoms = 366_360,
                        DatMatchedCount = 366_360
                    });
                    vm.StatsBanner.SetChangedRowCount(isPartial ? 1_234 : null);
                    vm.StatsBanner.SetSelectedUnitCount(isPartial ? 1_234 : null);
                    vm.StatsBanner.ShowSelection(isPartial
                        ? [new ProcessItem { SourcePath = "005.zip", FileSize = 23_088_000, Parsed = new ParsedRomName { RawFilename = "005.zip", Title = "005" } }]
                        : null);
                    Settle(window);

                    window.FindControl<Control>("ChipSelection")!.IsVisible.Should().Be(isPartial);
                    foreach (var side in new[] { chips, totals })
                    {
                        side.DesiredSize.Width.Should().BeLessThan(banner.Bounds.Width, $"each side fits in the banner ({locale})");
                        side.Bounds.Width.Should().BeApproximately(side.DesiredSize.Width, 0.5, $"nothing is cut ({locale})");
                    }

                    if (banner.IsWrapped)
                    {
                        totals.Bounds.Top.Should().BeGreaterThanOrEqualTo(chips.Bounds.Bottom, $"the totals are under the chips ({locale})");
                        totals.Bounds.Right.Should().BeApproximately(banner.Bounds.Width, 0.5, $"the totals stay on the right ({locale})");
                    }
                    else
                    {
                        chips.Bounds.Right.Should().BeLessThan(totals.Bounds.Left, $"the chips and the totals do not overlap ({locale})");
                    }

                    banner.IsWrapped.Should().Be(isPartial, $"only the fullest banner needs two rows ({locale})");
                }
            }
            finally
            {
                window.Close();
            }
        });
    }
}
