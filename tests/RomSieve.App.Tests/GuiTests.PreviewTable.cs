using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RomSieve.App.ViewModels;
using RomSieve.App.Views;
using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.App.Tests;

/// <summary>
/// Preview table with a large collection (50 000 rows) in the real window: reloads, filters and sorts replace the
/// visible rows with a single notification, and give the same rows as before.
/// </summary>
public sealed partial class GuiTests
{
    private const int LargeRowCount = 50_000;

    private static readonly FileStatus[] LargeStatuses = [FileStatus.Valid, FileStatus.Valid, FileStatus.Excluded, FileStatus.Duplicate, FileStatus.Unmatched];

    private List<ProcessItem> CreateLargeItemSet()
    {
        var parser = new TosecParser();
        string[] countries = ["US", "EU", "FR", "DE", "JP"];
        var items = new List<ProcessItem>(LargeRowCount);

        // Built in reverse order, so that the default sort on the source name has work to do
        for (var i = LargeRowCount - 1; i >= 0; i--)
        {
            var name = $"Game {i:00000} (19{80 + i % 20})(Publisher {i % 50})({countries[i % 5]}).adf";
            items.Add(new ProcessItem
            {
                SourcePath = Path.Combine(_tempDir, "src", name),
                FileSize = 1000 + i,
                Parsed = parser.ParseFilename(name),
                Status = LargeStatuses[i % LargeStatuses.Length],
                FinalTargetPath = Path.Combine(_tempDir, "dst", $"Publisher {i % 50}", name)
            });
        }

        return items;
    }

    /// <summary>Shows the window with the results of a finished run of <paramref name="items"/>.</summary>
    private (MainViewModel Vm, MainWindow Window) ShowFinishedRun(IReadOnlyList<ProcessItem> items, bool streamRows)
    {
        var vm = CreateViewModel();
        vm.Directories.DestinationDir = Path.Combine(_tempDir, "dst");
        // Built as in production: replacing the DataContext afterwards would leave the drop-down lists without selection
        var window = new MainWindow(vm);
        window.Show();

        // A run shows the results from its start
        vm.Navigation.ShowResults();
        if (streamRows)
        {
            foreach (var item in items)
            {
                vm.ProcessSingleItemForPreview(item);
            }
        }

        vm.Execution.CompleteRunForTests(new SortExecutionResult(
            new ProcessStats { TotalScanned = items.Count, ProcessedCount = items.Count }, items, [], null));
        Settle(window);
        return (vm, window);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Counts the notifications of the rows of the table raised by <paramref name="action"/>.</summary>
    private static List<NotifyCollectionChangedAction> CaptureRowNotifications(MainViewModel vm, Action action)
    {
        var notifications = new List<NotifyCollectionChangedAction>();
        void Handler(object? sender, NotifyCollectionChangedEventArgs e) => notifications.Add(e.Action);
        vm.PreviewTable.DisplayedRows.CollectionChanged += Handler;
        try
        {
            action();
        }
        finally
        {
            vm.PreviewTable.DisplayedRows.CollectionChanged -= Handler;
        }

        return notifications;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargePreview_FinishedRun_ShouldShowEveryRowSortedBySourceName(bool streamRows)
    {
        await RunOnUiThreadAsync(() =>
        {
            var items = CreateLargeItemSet();
            var sw = Stopwatch.StartNew();
            var (vm, window) = ShowFinishedRun(items, streamRows);
            sw.Stop();

            vm.FilteredPreviewItems.Should().HaveCount(LargeRowCount);
            vm.FilteredPreviewItems[0].SourceName.Should().StartWith("Game 00000");
            vm.FilteredPreviewItems[^1].SourceName.Should().StartWith("Game 49999");
            vm.PreviewKnownTitles.Should().HaveCount(LargeRowCount).And.BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
            window.FindControl<DataGrid>("TablePreview")!.ItemsSource.Should().BeSameAs(vm.PreviewTable.DisplayedRows);

            // Loose bound: about 25 s for a reload in one block before the bulk notifications, under 1 s since
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(streamRows ? 30 : 10));
            window.Close();
        });
    }

    [Fact]
    public async Task LargePreview_ChipsSearchAndSort_ShouldReplaceTheRowsWithASingleNotification()
    {
        await RunOnUiThreadAsync(() =>
        {
            var (vm, window) = ShowFinishedRun(CreateLargeItemSet(), streamRows: false);

            CaptureRowNotifications(vm, () => vm.StatsBanner.ToggleChip("excluded"))
                .Should().Equal(NotifyCollectionChangedAction.Reset);
            vm.FilteredPreviewItems.Should().HaveCount(LargeRowCount / LargeStatuses.Length)
                .And.OnlyContain(r => r.Item.Status == FileStatus.Excluded);

            CaptureRowNotifications(vm, () => vm.StatsBanner.ToggleChip("excluded"))
                .Should().Equal(NotifyCollectionChangedAction.Reset);
            vm.FilteredPreviewItems.Should().HaveCount(LargeRowCount);

            CaptureRowNotifications(vm, () => vm.PreviewSearchText = "game 0123")
                .Should().Equal(NotifyCollectionChangedAction.Reset);
            vm.FilteredPreviewItems.Select(r => r.SourceName[..10]).Should().Equal(
                Enumerable.Range(1230, 10).Select(i => $"Game {i:00000}"));

            CaptureRowNotifications(vm, () => vm.PreviewSearchText = string.Empty)
                .Should().Equal(NotifyCollectionChangedAction.Reset);

            CaptureRowNotifications(vm, () => vm.PreviewTable.SortPreviewByColumn(0))
                .Should().Equal(NotifyCollectionChangedAction.Reset);
            vm.FilteredPreviewItems[0].SourceName.Should().StartWith("Game 49999", "a second click sorts in descending order");
            Settle(window);
            window.Close();
        });
    }

    [Fact]
    public async Task LargePreview_RefreshWithTheSameRows_ShouldKeepTheRowsAndTheSelection()
    {
        await RunOnUiThreadAsync(() =>
        {
            var (vm, window) = ShowFinishedRun(CreateLargeItemSet(), streamRows: false);
            var table = window.FindControl<DataGrid>("TablePreview")!;
            var selected = vm.FilteredPreviewItems[1234];
            table.SelectedItem = selected;
            Settle(window);
            vm.PreviewTable.SelectedPreviewItem.Should().BeSameAs(selected);

            // Forcing a 1G1R winner refreshes the table, whose rows stay the same
            CaptureRowNotifications(vm, () => vm.PreviewTable.Force1G1RWinner(selected)).Should().BeEmpty();
            Settle(window);
            table.SelectedItem.Should().BeSameAs(selected);

            // A filter that keeps the selected row selects it again after the reload
            CaptureRowNotifications(vm, () => vm.StatsBanner.ToggleChip(selected.StatusValue))
                .Should().Equal(NotifyCollectionChangedAction.Reset);
            Settle(window);
            vm.PreviewTable.SelectedPreviewItem.Should().BeSameAs(selected);
            table.SelectedItem.Should().BeSameAs(selected);
            window.Close();
        });
    }

    [Fact]
    public async Task SearchBox_Typing_ShouldSuggestTheKnownTitles()
    {
        await RunOnUiThreadAsync(() =>
        {
            var (vm, window) = ShowFinishedRun(CreateLargeItemSet().TakeLast(500).ToList(), streamRows: true);
            var searchBox = window.FindControl<AutoCompleteBox>("EditSearchPreview")!;

            searchBox.Focus();
            window.KeyTextInput("game 0012");
            Settle(window);

            vm.PreviewSearchText.Should().Be("game 0012");
            searchBox.IsDropDownOpen.Should().BeTrue();
            vm.FilteredPreviewItems.Select(r => r.SourceName[..10]).Should().Equal(
                Enumerable.Range(120, 10).Select(i => $"Game {i:00000}"));
            window.Close();
        });
    }

    [Fact]
    public async Task PreviewTable_CellTooltips_ShouldNeverCoverThePointer()
    {
        await RunOnUiThreadAsync(() =>
        {
            var (vm, window) = ShowFinishedRun(CreateLargeItemSet().Take(200).ToList(), streamRows: false);
            var grid = window.FindControl<DataGrid>("TablePreview")!;
            var cells = grid.GetVisualDescendants().OfType<DataGridCell>().Where(c => c.IsVisible).ToList();
            cells.Should().NotBeEmpty();

            foreach (var cell in cells)
            {
                ToolTip.GetTip(cell).Should().Be(((PreviewItemViewModel)cell.DataContext!).TooltipText);
                // Anchored on the cell without offset: flipped above it near the bottom of the screen, it stays clear
                // of the cell (the offset is not reversed by the flip), so it never covers the pointer
                ToolTip.GetPlacement(cell).Should().Be(PlacementMode.BottomEdgeAlignedLeft);
                ToolTip.GetVerticalOffset(cell).Should().Be(0);
                ToolTip.GetBetweenShowDelay(cell).Should().Be(0, "moving from cell to cell must not reopen a tooltip at once");
            }

            grid.GetVisualDescendants().OfType<DataGridRow>().Should().OnlyContain(r => ToolTip.GetTip(r) == null);
            vm.FilteredPreviewItems.Should().NotBeEmpty();
            window.Close();
        });
    }

    [Fact]
    public async Task PreviewTable_MouseWheel_ShouldKeepRowTooltipsClosedUntilScrollingStops()
    {
        await RunOnUiThreadAsync(() =>
        {
            var (_, window) = ShowFinishedRun(CreateLargeItemSet().Take(200).ToList(), streamRows: false);
            var grid = window.FindControl<DataGrid>("TablePreview")!;
            var cell = grid.GetVisualDescendants().OfType<DataGridCell>().First(c => c.IsVisible);
            ToolTip.SetIsOpen(cell, true);
            var center = grid.TranslatePoint(new Point(grid.Bounds.Width / 2, grid.Bounds.Height / 2), window)!.Value;
            window.MouseWheel(center, new Vector(0, -1));
            Dispatcher.UIThread.RunJobs();

            // While the wheel turns, no row tooltip opens: one popup per row made the cursor flicker and lost wheel steps
            ToolTip.GetIsOpen(cell).Should().BeFalse();
            ToolTip.GetServiceEnabled(cell).Should().BeFalse();

            // Lets the dispatcher run its timers until the tooltips are due back
            var frame = new DispatcherFrame();
            DispatcherTimer.RunOnce(() => frame.Continue = false, MainWindow.PreviewTooltipResumeDelay + TimeSpan.FromMilliseconds(300));
            Dispatcher.UIThread.PushFrame(frame);
            ToolTip.GetServiceEnabled(cell).Should().BeTrue();
            window.Close();
        });
    }
}
