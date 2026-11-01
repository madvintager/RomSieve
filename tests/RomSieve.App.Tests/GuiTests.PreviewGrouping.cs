using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using RomSieve.App.ViewModels;
using RomSieve.App.Views;
using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.App.Tests;

/// <summary>
/// View grouped by game of the preview table in the real window: switch button, game headers and their chevron,
/// keyboard, "forced" badge, context menu of a header, sort by the column headers, 50 000 rows, texts not trimmed.
/// </summary>
public sealed partial class GuiTests
{
    private static readonly string[] GroupedVersions = ["US", "EU", "FR"];

    /// <summary>
    /// <paramref name="gameCount"/> games of three versions each (US, EU, FR): the US version won 1G1R, the others are
    /// excluded. When <paramref name="forcedGame"/> is set, the FR version of that game was chosen by hand.
    /// </summary>
    private List<ProcessItem> CreateGroupedItemSet(int gameCount, int? forcedGame = null)
    {
        var parser = new TosecParser();
        var items = new List<ProcessItem>(gameCount * GroupedVersions.Length);
        for (var game = gameCount - 1; game >= 0; game--)
        {
            foreach (var version in GroupedVersions)
            {
                var name = $"Game {game:00000} (1990)(Publisher {game % 50})({version}).adf";
                var isWinner = forcedGame == game ? version == "FR" : version == "US";
                items.Add(new ProcessItem
                {
                    SourcePath = Path.Combine(_tempDir, "src", name),
                    FileSize = 1000 + game,
                    Parsed = parser.ParseFilename(name),
                    Status = isWinner ? FileStatus.Valid : FileStatus.Excluded,
                    Is1G1RWinner = isWinner,
                    StatusReason = isWinner
                        ? LocalizedMessage.Create(forcedGame == game ? "reason.1g1r_winner_manual" : "reason.1g1r_winner")
                        : LocalizedMessage.Create("reason.1g1r_discarded", ("winner", "?"), ("reason", "?")),
                    FinalTargetPath = Path.Combine(_tempDir, "dst", $"Publisher {game % 50}", name)
                });
            }
        }

        return items;
    }

    private static List<DataGridRow> RealizedRows(DataGrid grid) =>
        grid.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.IsVisible).OrderBy(r => r.Index).ToList();

    [Fact]
    public async Task GroupByGameButton_ShouldShowAHeaderPerGame_WithItsChevronAndCount()
    {
        await RunOnUiThreadAsync(() =>
        {
            var (vm, window) = ShowFinishedRun(CreateGroupedItemSet(20), streamRows: false);
            try
            {
                var button = window.FindControl<ToggleButton>("BtnGroupByGame")!;
                button.Content.Should().Be("Grouper par jeu");
                ToolTip.GetTip(button).Should().Be(vm.Localization.Tr("panel4.tooltip_group_by_game"));
                button.IsChecked.Should().BeFalse();

                // The label is centered vertically in the button, at the height of the one of the columns menu next to
                // it: a toggle button stretches its content by default, and its text was then drawn at the top
                var label = button.GetVisualDescendants().OfType<TextBlock>().First();
                var columns = window.FindControl<DropDownButton>("BtnColumns")!;
                var columnsLabel = columns.GetVisualDescendants().OfType<TextBlock>().First();
                label.Bounds.Height.Should().BeApproximately(columnsLabel.Bounds.Height, 0.5, "the label is not stretched");
                label.TranslatePoint(default, window)!.Value.Y.Should().BeApproximately(columnsLabel.TranslatePoint(default, window)!.Value.Y, 0.5);

                button.IsChecked = true;
                Settle(window);
                vm.PreviewTable.IsGroupedByGame.Should().BeTrue();
                var table = vm.PreviewTable;
                table.DisplayedRows.Should().HaveCount(80, "20 headers and 60 files");
                table.DisplayedRows[0].Should().BeOfType<PreviewGroupHeaderViewModel>();
                table.DisplayedRows[1].SourceName.Should().Contain("(US)", "the 1G1R winner comes first in its game");

                var grid = window.FindControl<DataGrid>("TablePreview")!;
                var rows = RealizedRows(grid);
                rows.Should().NotBeEmpty();
                foreach (var row in rows.Take(8))
                {
                    var data = (PreviewRowViewModel)row.DataContext!;
                    var chevron = row.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "BtnGroupToggle");
                    var count = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "LblGroupCount");
                    var title = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == data.SourceName);
                    chevron.IsVisible.Should().Be(data.IsGroupHeader);
                    count.IsVisible.Should().Be(data.IsGroupHeader);
                    title.Classes.Contains("group-title").Should().Be(data.IsGroupHeader);
                    if (data is PreviewGroupHeaderViewModel header)
                    {
                        count.Text.Should().Be("(3)");
                        title.FontWeight.Should().Be(FontWeight.SemiBold);
                        ToolTip.GetTip(row.GetVisualDescendants().OfType<DataGridCell>().First()).Should().Be(header.TooltipText);
                    }
                }

                // The chevron folds the game
                var firstHeader = (PreviewGroupHeaderViewModel)table.DisplayedRows[0];
                var firstChevron = rows[0].GetVisualDescendants().OfType<Button>().Single(b => b.Name == "BtnGroupToggle");
                firstChevron.Command!.Execute(firstChevron.CommandParameter);
                Settle(window);
                firstHeader.IsExpanded.Should().BeFalse();
                table.DisplayedRows.Should().HaveCount(77);
                table.DisplayedRows[1].Should().BeOfType<PreviewGroupHeaderViewModel>();

                button.IsChecked = false;
                Settle(window);
                table.DisplayedRows.Should().HaveCount(60).And.OnlyContain(r => !r.IsGroupHeader);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task GroupHeader_ArrowsAndSpace_ShouldFoldAndUnfoldTheSelectedGame()
    {
        await RunOnUiThreadAsync(() =>
        {
            var (vm, window) = ShowFinishedRun(CreateGroupedItemSet(5), streamRows: false);
            try
            {
                vm.PreviewTable.IsGroupedByGame = true;
                Settle(window);
                var grid = window.FindControl<DataGrid>("TablePreview")!;
                var header = (PreviewGroupHeaderViewModel)vm.PreviewTable.DisplayedRows[4];
                grid.SelectedItem = header;
                Settle(window);
                vm.PreviewTable.SelectedGroupHeader.Should().BeSameAs(header);

                void Press(Key key) => grid.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });

                Press(Key.Left);
                header.IsExpanded.Should().BeFalse();
                vm.PreviewTable.DisplayedRows.Should().HaveCount(17);
                Press(Key.Right);
                header.IsExpanded.Should().BeTrue();
                Press(Key.Space);
                header.IsExpanded.Should().BeFalse();
                Press(Key.Space);
                header.IsExpanded.Should().BeTrue();
                vm.PreviewTable.DisplayedRows.Should().HaveCount(20);
                Settle(window);
                grid.SelectedItem.Should().BeSameAs(header);

                // On a file, the arrows keep their usual role
                var file = (PreviewItemViewModel)vm.PreviewTable.DisplayedRows[5];
                grid.SelectedItem = file;
                Settle(window);
                Press(Key.Left);
                header.IsExpanded.Should().BeTrue();
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task RequiredMameSet_ShouldShowARequiredBadgeOnTheFileAndOnItsGame()
    {
        await RunOnUiThreadAsync(() =>
        {
            // The EU version of the first game, kept only because a kept set needs it (as a BIOS)
            var items = CreateGroupedItemSet(3);
            var required = items.Single(it => it.SourcePath.Contains("Game 00000", StringComparison.Ordinal) && it.SourcePath.Contains("(EU)", StringComparison.Ordinal));
            required.Status = FileStatus.Valid;
            required.StatusReason = LocalizedMessage.Create("reason.mame_required_set", ("set", "lresort"));
            var (vm, window) = ShowFinishedRun(items, streamRows: false);
            try
            {
                vm.PreviewTable.IsGroupedByGame = true;
                Settle(window);
                var grid = window.FindControl<DataGrid>("TablePreview")!;
                var rows = RealizedRows(grid);
                var badges = rows.ToDictionary(r => r, r => r.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "RequiredBadge"));
                var shown = badges.Where(kv => kv.Value.IsVisible).Select(kv => (PreviewRowViewModel)kv.Key.DataContext!).ToList();
                shown.Should().HaveCount(2, "the file and the header of its game");
                shown.Should().ContainSingle(r => r is PreviewGroupHeaderViewModel);
                shown.OfType<PreviewItemViewModel>().Single().Item.Should().BeSameAs(required);
                ((PreviewGroupHeaderViewModel)shown.Single(r => r is PreviewGroupHeaderViewModel)).TooltipText
                    .Should().Contain(vm.Localization.Tr("panel4.tooltip_required_set"));

                var badge = badges.Values.First(b => b.IsVisible);
                var text = (TextBlock)badge.Child!;
                text.Text.Should().Be("Requis");
                ToolTip.GetTip(badge).Should().Be(vm.Localization.Tr("panel4.tooltip_required_set"));
                ((ISolidColorBrush)badge.Background!).Color.Should().Be(PaletteColor(ThemeVariant.Light, "RequiredBadgeBackgroundBrush"));
                ((ISolidColorBrush)text.Foreground!).Color.Should().Be(PaletteColor(ThemeVariant.Light, "RequiredBadgeForegroundBrush"));

                // The badge follows the language and the theme, and fits in the status column next to the status
                // (measured with the real fonts only)
                foreach (var locale in new[] { "fr", "en", "es" })
                {
                    vm.UiLocale = locale;
                    Settle(window);
                    text.Text.Should().Be(vm.Localization.Tr("panel4.badge_required"));
                    if (Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") == "1")
                    {
                        var cell = badge.FindAncestorOfType<DataGridCell>()!;
                        var right = badge.TranslatePoint(new Point(badge.Bounds.Width, 0), cell)!.Value.X;
                        right.Should().BeLessThanOrEqualTo(cell.Bounds.Width, $"in {locale}");
                    }
                }

                vm.UiTheme = UiTheme.Dark;
                Settle(window);
                ((ISolidColorBrush)badge.Background!).Color.Should().Be(PaletteColor(ThemeVariant.Dark, "RequiredBadgeBackgroundBrush"));
            }
            finally
            {
                vm.UiTheme = UiTheme.Light;
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ForcedWinner_ShouldShowAForcedBadgeOnTheFileAndOnItsGame()
    {
        await RunOnUiThreadAsync(() =>
        {
            var (vm, window) = ShowFinishedRun(CreateGroupedItemSet(5, forcedGame: 0), streamRows: false);
            try
            {
                vm.PreviewTable.IsGroupedByGame = true;
                Settle(window);
                var grid = window.FindControl<DataGrid>("TablePreview")!;
                var rows = RealizedRows(grid);
                var header = rows[0];
                var winner = rows[1];
                ((PreviewRowViewModel)winner.DataContext!).SourceName.Should().Contain("(FR)", "the forced winner comes first");

                foreach (var row in rows.Take(8))
                {
                    var data = (PreviewRowViewModel)row.DataContext!;
                    var badge = row.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ForcedBadge");
                    badge.IsVisible.Should().Be(ReferenceEquals(row, header) || ReferenceEquals(row, winner), $"row {data.SourceName}");
                }

                var forcedBadge = header.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ForcedBadge");
                var text = (TextBlock)forcedBadge.Child!;
                text.Text.Should().Be("Forcé");
                ((ISolidColorBrush)forcedBadge.Background!).Color.Should().Be(PaletteColor(ThemeVariant.Light, "ForcedBadgeBackgroundBrush"));
                ((ISolidColorBrush)text.Foreground!).Color.Should().Be(PaletteColor(ThemeVariant.Light, "ForcedBadgeForegroundBrush"));

                // The badge follows the language and the theme
                vm.UiLocale = "en";
                vm.UiTheme = UiTheme.Dark;
                Settle(window);
                text.Text.Should().Be("Forced");
                ((ISolidColorBrush)forcedBadge.Background!).Color.Should().Be(PaletteColor(ThemeVariant.Dark, "ForcedBadgeBackgroundBrush"));
            }
            finally
            {
                vm.UiTheme = UiTheme.Light;
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ContextMenu_OfAGameHeader_ShouldFoldTheGameAndOfferNoForcing()
    {
        await RunOnUiThreadAsync(() =>
        {
            var (vm, window) = ShowFinishedRun(CreateGroupedItemSet(5), streamRows: false);
            try
            {
                var grid = window.FindControl<DataGrid>("TablePreview")!;
                var menu = grid.ContextMenu!;
                var toggle = menu.Items.OfType<MenuItem>().Single(m => m.Name == "MenuToggleGroup");
                var collapseAll = menu.Items.OfType<MenuItem>().Single(m => m.Name == "MenuCollapseAllGroups");
                var expandAll = menu.Items.OfType<MenuItem>().Single(m => m.Name == "MenuExpandAllGroups");
                var force = (MenuItem)menu.Items[0]!;

                // Flat view: no group entry
                vm.PreviewTable.SelectedPreviewItem = vm.PreviewTable.FilteredPreviewItems[0];
                toggle.IsVisible.Should().BeFalse();
                collapseAll.IsVisible.Should().BeFalse();
                expandAll.IsVisible.Should().BeFalse();
                force.IsVisible.Should().BeTrue();

                vm.PreviewTable.IsGroupedByGame = true;
                Settle(window);
                var header = (PreviewGroupHeaderViewModel)vm.PreviewTable.DisplayedRows[0];
                vm.PreviewTable.SelectedRow = header;
                vm.PreviewTable.CanOpenPreviewContextMenu.Should().BeTrue();
                toggle.IsVisible.Should().BeTrue();
                toggle.Header.Should().Be("Replier ce jeu");
                collapseAll.IsVisible.Should().BeTrue();
                collapseAll.Header.Should().Be("Replier tous les jeux");
                expandAll.IsVisible.Should().BeTrue();
                force.IsVisible.Should().BeFalse("a version is forced from its own row");

                toggle.Command!.Execute(toggle.CommandParameter);
                header.IsExpanded.Should().BeFalse();
                toggle.Header.Should().Be("Déplier ce jeu");

                collapseAll.Command!.Execute(null);
                vm.PreviewTable.DisplayedRows.Should().HaveCount(5).And.OnlyContain(r => r.IsGroupHeader);
                expandAll.Command!.Execute(null);
                vm.PreviewTable.DisplayedRows.Should().HaveCount(20);

                // A file of the grouped view keeps the menu of a file
                vm.PreviewTable.SelectedRow = vm.PreviewTable.DisplayedRows[2];
                force.IsVisible.Should().BeTrue();
                toggle.IsVisible.Should().BeFalse();
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ColumnHeaderClick_ShouldSortThroughTheViewModel_AndShowTheSortArrow()
    {
        await RunOnUiThreadAsync(() =>
        {
            var (vm, window) = ShowFinishedRun(CreateGroupedItemSet(5), streamRows: false);
            try
            {
                vm.PreviewTable.IsGroupedByGame = true;
                Settle(window);
                var grid = window.FindControl<DataGrid>("TablePreview")!;
                DataGridColumnHeader Header(int column) => grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
                    .Single(h => Equals(h.Content, grid.Columns[column].Header));

                Header(0).Classes.Contains("sort-ascending").Should().BeTrue("a finished run is sorted by the source name");

                void Click(int column)
                {
                    var header = Header(column);
                    var center = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
                    window.MouseDown(center, MouseButton.Left);
                    window.MouseUp(center, MouseButton.Left);
                    Settle(window);
                }

                Click(0);
                vm.PreviewTable.CurrentSortColumn.Should().Be(0);
                vm.PreviewTable.IsSortAscending.Should().BeFalse();
                Header(0).Classes.Contains("sort-descending").Should().BeTrue();
                Header(0).Classes.Contains("sort-ascending").Should().BeFalse();
                var icon = Header(0).GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single(p => p.Name == "SortIcon");
                icon.IsVisible.Should().BeTrue("the arrow shows the sort of the ViewModel");
                icon.Data.Should().NotBeNull();

                // Moving the pointer over the headers keeps the arrow (the header resets its own sort state)
                window.MouseMove(new Point(5, 5));
                window.MouseMove(Header(0).TranslatePoint(new Point(10, 5), window)!.Value);
                Settle(window);
                icon.IsVisible.Should().BeTrue();
                var rows = vm.PreviewTable.DisplayedRows;
                rows[0].Should().BeOfType<PreviewGroupHeaderViewModel>("the grid did not sort the headers with the files");
                rows[0].SourceName.Should().Be("Game 00004");
                rows[1].SourceName.Should().Contain("(US)", "the winner stays first in its game");

                Click(9);
                vm.PreviewTable.CurrentSortColumn.Should().Be(9);
                Header(9).Classes.Contains("sort-ascending").Should().BeTrue();
                Header(0).Classes.Contains("sort-descending").Should().BeFalse();
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task LargePreview_GroupedByGame_ShouldGroupEveryRowQuickly()
    {
        await RunOnUiThreadAsync(() =>
        {
            var items = CreateGroupedItemSet(LargeRowCount / GroupedVersions.Length);
            var (vm, window) = ShowFinishedRun(items, streamRows: false);
            try
            {
                var table = vm.PreviewTable;
                var gameCount = LargeRowCount / GroupedVersions.Length;

                var sw = Stopwatch.StartNew();
                CaptureRowNotifications(vm, () => table.IsGroupedByGame = true)
                    .Should().Equal(System.Collections.Specialized.NotifyCollectionChangedAction.Reset);
                Settle(window);
                sw.Stop();
                table.DisplayedRows.Should().HaveCount(items.Count + gameCount);
                sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));

                sw.Restart();
                CaptureRowNotifications(vm, () => vm.StatsBanner.ToggleChip("excluded"))
                    .Should().Equal(System.Collections.Specialized.NotifyCollectionChangedAction.Reset);
                table.DisplayedRows.Should().HaveCount(gameCount * 3, "each game shows its header and its two excluded files");
                CaptureRowNotifications(vm, () => vm.StatsBanner.ToggleChip("excluded"));

                CaptureRowNotifications(vm, table.CollapseAllGroups)
                    .Should().Equal(System.Collections.Specialized.NotifyCollectionChangedAction.Reset);
                table.DisplayedRows.Should().HaveCount(gameCount);

                // Unfolding a game, or forcing one of its versions, moves a few rows only
                var header = (PreviewGroupHeaderViewModel)table.DisplayedRows[1234];
                CaptureRowNotifications(vm, () => table.SetGroupExpanded(header, true))
                    .Should().OnlyContain(a => a == System.Collections.Specialized.NotifyCollectionChangedAction.Add);
                sw.Stop();
                sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The grouping button and the status cell of a forced winner (status badge, then "forced" badge) fit in their
    /// default width in every language. Measured with the real fonts only (ROMSIEVE_HEADLESS_SKIA=1).
    /// </summary>
    [Fact]
    public async Task PreviewGrouping_Texts_ShouldNotBeTrimmed()
    {
        if (Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") != "1")
        {
            return;
        }

        await RunOnUiThreadAsync(() =>
        {
            var (vm, window) = ShowFinishedRun(CreateGroupedItemSet(5, forcedGame: 0), streamRows: false);
            try
            {
                vm.PreviewTable.IsGroupedByGame = true;
                foreach (var locale in vm.Localization.GetAvailableLocales().Select(l => l.Code))
                {
                    vm.UiLocale = locale;
                    Settle(window);
                    var button = window.FindControl<ToggleButton>("BtnGroupByGame")!;
                    var buttonText = button.GetVisualDescendants().OfType<TextBlock>().First();
                    var shown = buttonText.Bounds.Width;
                    buttonText.Measure(Size.Infinity);
                    buttonText.DesiredSize.Width.Should().BeLessThanOrEqualTo(shown + 0.5, $"the grouping button ({locale}) must not be trimmed");

                    var grid = window.FindControl<DataGrid>("TablePreview")!;
                    foreach (var row in RealizedRows(grid).Take(2))
                    {
                        var badge = row.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ForcedBadge");
                        badge.IsVisible.Should().BeTrue();
                        var cell = badge.FindAncestorOfType<DataGridCell>()!;
                        var panel = (StackPanel)badge.Parent!;
                        panel.Measure(Size.Infinity);
                        (panel.DesiredSize.Width + panel.Margin.Left).Should().BeLessThanOrEqualTo(
                            cell.Bounds.Width, $"the status and forced badges ({locale}) must fit in the status column");
                    }
                }
            }
            finally
            {
                window.Close();
            }
        });
    }
}
