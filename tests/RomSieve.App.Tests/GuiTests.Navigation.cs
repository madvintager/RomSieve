using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using RomSieve.App.ViewModels;
using RomSieve.App.Views;
using RomSieve.Core.Models;

namespace RomSieve.App.Tests;

/// <summary>
/// Navigation by steps in the window: entries, footer shown on every step, results shown when a run starts, and a
/// preview table that keeps its state while another step is shown.
/// </summary>
public sealed partial class GuiTests
{
    private static Button NavButton(Window window, string name) =>
        window.FindControl<ContentControl>(name)!.GetVisualDescendants().OfType<Button>().First();

    [Fact]
    public async Task NavigationEntries_ShouldShowTheirPage_AndTheFooterStaysOnEveryStep()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                Settle(window);
                foreach (var (entry, step) in new[]
                         {
                             ("NavFilters", WorkflowStep.Filters), ("NavLanguages", WorkflowStep.Languages),
                             ("NavOutput", WorkflowStep.Output), ("NavResults", WorkflowStep.Results),
                             ("NavCollection", WorkflowStep.Collection)
                         })
                {
                    var button = NavButton(window, entry);
                    button.Command!.Execute(null);
                    Settle(window);

                    vm.Navigation.CurrentStep.Should().Be(step);
                    window.FindControl<Control>($"Page{step}")!.IsEffectivelyVisible.Should().BeTrue();
                    button.Classes.Should().Contain("current");
                    ToolTip.GetTip(button)?.ToString().Should().NotBeNullOrWhiteSpace($"{entry} explains its step");
                    foreach (var name in new[] { "BtnSimulate", "BtnStart", "BtnUndo", "BtnExportMenu" })
                    {
                        window.FindControl<Control>(name)!.IsEffectivelyVisible.Should().BeTrue($"{name} is reachable from {step}");
                    }
                }

                // The selection group always leads to its first sub-step, marks it and only emphasizes itself
                NavButton(window, "NavSelection").Command!.Execute(null);
                Settle(window);
                vm.Navigation.CurrentStep.Should().Be(WorkflowStep.Filters);
                NavButton(window, "NavSelection").Classes.Should().Contain("group-current").And.NotContain("current");
                NavButton(window, "NavFilters").Classes.Should().Contain("current").And.Contain("sub");
                NavButton(window, "NavLanguages").Classes.Should().NotContain("current");

                // The state line of a step under its label
                var collectionTexts = NavButton(window, "NavCollection").GetVisualDescendants().OfType<TextBlock>()
                    .Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
                collectionTexts.Should().Equal("1", "Collection", "Source à choisir");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task PlayedSimulation_ShouldShowTheResults_AndTheButtonLeadsBackToTheSettings()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.Directories.DestinationDir = Path.Combine(_tempDir, "dst");
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.CurrentStep = WorkflowStep.Languages;
                var items = CreateLargeItemSet().Take(30).ToList();
                vm.Execution.PlayRunForTests(
                    vm.BuildConfigUnchecked(),
                    isDryRun: true,
                    new SortExecutionResult(new ProcessStats { TotalScanned = items.Count, ProcessedCount = items.Count }, items, [], null));
                Settle(window);

                vm.Navigation.CurrentStep.Should().Be(WorkflowStep.Results);
                window.FindControl<DataGrid>("TablePreview")!.IsEffectivelyVisible.Should().BeTrue();
                vm.Navigation.ResultsEntry.IndicatorText.Should().Be("30 fichiers");

                var back = window.FindControl<Button>("BtnBackToSettings")!;
                back.Content.Should().Be("Modifier les réglages");
                back.Command!.Execute(null);
                Settle(window);
                vm.Navigation.CurrentStep.Should().Be(WorkflowStep.Languages);
                window.FindControl<Control>("PageLanguages")!.IsEffectivelyVisible.Should().BeTrue();
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task PreviewTable_ShouldKeepItsStateWhileAnotherStepIsShown()
    {
        await RunOnUiThreadAsync(() =>
        {
            var (vm, window) = ShowFinishedRun(CreateGroupedItemSet(60), streamRows: false);
            try
            {
                var table = vm.PreviewTable;
                var grid = window.FindControl<DataGrid>("TablePreview")!;
                table.IsGroupedByGame = true;
                table.SortPreviewByColumn(0);
                Settle(window);
                var folded = (PreviewGroupHeaderViewModel)table.DisplayedRows[0];
                table.SetGroupExpanded(folded, false);
                var selected = table.DisplayedRows[^2];
                table.SelectedRow = selected;
                grid.ScrollIntoView(selected, null);
                Settle(window);

                var scrollBar = grid.GetVisualDescendants().OfType<ScrollBar>().First(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
                var offset = scrollBar.Value;
                offset.Should().BeGreaterThan(0, "the table is scrolled down to the selected row");
                var sort = (table.CurrentSortColumn, table.IsSortAscending);
                var rows = table.DisplayedRows.ToList();

                var notifications = CaptureRowNotifications(vm, () =>
                {
                    foreach (var step in new[] { WorkflowStep.Filters, WorkflowStep.Output, WorkflowStep.Results })
                    {
                        vm.Navigation.CurrentStep = step;
                        Settle(window);
                    }
                });

                notifications.Should().BeEmpty("changing step neither reloads nor rebuilds the rows");
                window.FindControl<DataGrid>("TablePreview").Should().BeSameAs(grid, "the grid stays alive");
                table.DisplayedRows.Should().Equal(rows);
                table.SelectedRow.Should().BeSameAs(selected);
                grid.SelectedItem.Should().BeSameAs(selected);
                (table.CurrentSortColumn, table.IsSortAscending).Should().Be(sort);
                folded.IsExpanded.Should().BeFalse("the folded game stays folded");
                scrollBar.Value.Should().BeApproximately(offset, 0.5, "the table keeps its scrolling");
            }
            finally
            {
                window.Close();
            }
        });
    }
}
