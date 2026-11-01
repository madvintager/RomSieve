using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using RomSieve.App.ViewModels;
using RomSieve.App.Views;
using RomSieve.Core.Models;

namespace RomSieve.App.Tests;

/// <summary>
/// Partial sort in the window: menu joined to the sort button, its entry for the rows shown with what it writes or why
/// it is not available, and texts that fit in every language.
/// </summary>
public sealed partial class GuiTests
{
    /// <summary>
    /// A view model whose source holds three games, and their simulation computed off the UI thread: awaiting a run
    /// there while a window is shown corrupts the font cache of the headless platform.
    /// </summary>
    private async Task<(MainViewModel Vm, SortConfig Config, SortExecutionResult Simulation)> CreatePartialSortSetupAsync()
    {
        var source = Path.Combine(_tempDir, "partial_src");
        Directory.CreateDirectory(source);
        foreach (var name in new[] { "Game A (1990)(Ocean).adf", "Game B (1991)(Ocean).adf", "Game C (1992)(Ocean).adf" })
        {
            File.WriteAllText(Path.Combine(source, name), name);
        }

        var vm = CreateViewModel();
        vm.Directories.SourceDir = source;
        vm.Directories.DestinationDir = Path.Combine(_tempDir, "partial_dst");
        var config = vm.BuildConfigUnchecked();
        var simulation = await new RomSieve.Core.Tests.Fixtures.CoreTestServices("fr").Processor.ExecuteAsync(config, isDryRun: true);
        return (vm, config, simulation);
    }

    [Fact]
    public async Task PartialSortMenu_ShouldOfferTheRowsShownWithWhatTheSortWrites()
    {
        var (vm, config, simulation) = await CreatePartialSortSetupAsync();

        await RunOnUiThreadAsync(() =>
        {
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                Settle(window);
                var start = window.FindControl<Button>("BtnStart")!;
                var menuButton = window.FindControl<DropDownButton>("BtnPartialSortMenu")!;
                menuButton.GetVisualParent().Should().BeSameAs(start.GetVisualParent(), "the menu is joined to the sort button");
                ToolTip.GetTip(menuButton).Should().Be(vm.Localization.Tr("panel4.tooltip_partial_sort_menu"));
                menuButton.Classes.Should().NotContain("primary");

                menuButton.Flyout!.ShowAt(menuButton);
                Settle(window);
                var entry = window.FindControl<MenuItem>("MenuSortDisplayed")!;
                var summary = window.FindControl<TextBlock>("LblSortDisplayedSummary")!;
                entry.Command.Should().BeSameAs(vm.PartialSort.SortDisplayedCommand);
                ToolTip.GetTip(entry).Should().Be(vm.Localization.Tr("panel4.tooltip_menu_sort_displayed"));
                entry.IsEffectivelyEnabled.Should().BeFalse("nothing was simulated");
                summary.Text.Should().Be(vm.Localization.Tr("panel4.partial_needs_simulation"));
                menuButton.Flyout.Hide();

                vm.Execution.PlayRunForTests(config, isDryRun: true, simulation);
                vm.PreviewTable.PreviewSearchText = "Game B";
                Settle(window);
                menuButton.Classes.Should().Contain("primary", "the menu is put forward with the sort button");
                menuButton.Flyout.ShowAt(menuButton);
                Settle(window);

                entry.IsEffectivelyEnabled.Should().BeTrue();
                summary.Text.Should().Be(vm.PartialSort.DisplayedSummaryText)
                    .And.StartWith("Copier 1 jeu (1 fichier)")
                    .And.EndWith("une partie de la simulation");

                // Without a filter, every checked game: the whole simulation while all of them are checked
                vm.PreviewTable.PreviewSearchText = string.Empty;
                Settle(window);
                entry.IsEffectivelyEnabled.Should().BeTrue("the rows shown are all the checked games");
                summary.Text.Should().NotEndWith("une partie de la simulation", "it covers the whole simulation");
                menuButton.Flyout.Hide();
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>The sort button, its menu and the entry of the menu fit in every language (real fonts only).</summary>
    [Fact]
    public async Task PartialSortMenu_Texts_ShouldNotBeTrimmed()
    {
        if (Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") != "1")
        {
            return;
        }

        var (vm, config, simulation) = await CreatePartialSortSetupAsync();

        await RunOnUiThreadAsync(() =>
        {
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Execution.PlayRunForTests(config, isDryRun: true, simulation);
                vm.PreviewTable.PreviewSearchText = "Game B";
                var menuButton = window.FindControl<DropDownButton>("BtnPartialSortMenu")!;
                foreach (var locale in vm.Localization.GetAvailableLocales().Select(l => l.Code))
                {
                    vm.UiLocale = locale;
                    Settle(window);
                    AssertNotTrimmed(window.FindControl<Button>("BtnStart")!, $"BtnStart ({locale})");
                    AssertNotTrimmed(menuButton, $"BtnPartialSortMenu ({locale})");

                    menuButton.Flyout!.ShowAt(menuButton);
                    Settle(window);
                    var entry = window.FindControl<MenuItem>("MenuSortDisplayed")!;
                    var texts = entry.GetVisualDescendants().OfType<TextBlock>()
                        .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))
                        .ToList();
                    texts.Should().HaveCountGreaterThanOrEqualTo(2);
                    foreach (var text in texts)
                    {
                        if (text.TextWrapping == TextWrapping.NoWrap)
                        {
                            AssertNotTrimmed(text, $"\"{text.Text}\" ({locale})");
                            continue;
                        }

                        // The summary wraps instead of being cut: its whole height must be shown
                        text.Measure(new Size(text.Bounds.Width, double.PositiveInfinity));
                        text.DesiredSize.Height.Should().BeLessThanOrEqualTo(text.Bounds.Height + 1, $"\"{text.Text}\" ({locale}) is not cut");
                        text.InvalidateMeasure();
                    }

                    menuButton.Flyout.Hide();
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static CheckBox RowCheckBox(DataGridRow row) =>
        row.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "ChkRowSortCheck");

    private static void ClickCenter(Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
    }

    [Fact]
    public async Task RowCheckBoxes_ShouldLeaveOutWholeGames_AndDriveTheSortButton()
    {
        await RunOnUiThreadAsync(() =>
        {
            var (vm, window) = ShowFinishedRun(CreateGroupedItemSet(5), streamRows: false);
            try
            {
                vm.PreviewTable.IsGroupedByGame = true;
                Settle(window);
                var grid = window.FindControl<DataGrid>("TablePreview")!;
                var count = window.FindControl<TextBlock>("LblCheckedCount")!;
                var start = window.FindControl<Button>("BtnStart")!;

                // After a run, every game the sort writes is checked, and the sort button sorts them all
                count.IsVisible.Should().BeTrue();
                ToolTip.GetTip(count).Should().Be(vm.Localization.Tr("panel4.tooltip_checked_count"));
                window.FindControl<Button>("BtnClearChecked").Should().BeNull("unchecking \"Check All\" empties the selection");
                count.Text.Should().Be("5 jeux sélectionnés");
                RealizedRows(grid).Should().OnlyContain(r => RowCheckBox(r).IsChecked == RowCheckBox(r).IsEnabled);
                RealizedRows(grid).Where(r => r.DataContext is PreviewItemViewModel { Item.Status: FileStatus.Excluded })
                    .Should().NotBeEmpty().And.OnlyContain(r => RowCheckBox(r).IsChecked == false && !RowCheckBox(r).IsEnabled,
                        "an excluded version is not sorted with its game");
                start.Content.Should().Be("Démarrer le tri");
                start.Command.Should().BeSameAs(vm.PartialSort.StartCommand);

                // The check box of a header leaves out its game: the header and its three files are unchecked
                var headerRow = RealizedRows(grid).First(r => r.DataContext is PreviewGroupHeaderViewModel);
                var header = (PreviewGroupHeaderViewModel)headerRow.DataContext!;
                ToolTip.GetTip(RowCheckBox(headerRow)).Should().Be(vm.Localization.Tr("panel4.tooltip_row_check"));
                ClickCenter(window, RowCheckBox(headerRow));
                Settle(window);

                header.IsSortChecked.Should().BeFalse();
                header.Files.Should().HaveCount(3).And.OnlyContain(f => !f.IsSortChecked);
                RealizedRows(grid).Where(r => header.Files.Contains(r.DataContext)).Should().HaveCount(3)
                    .And.OnlyContain(r => RowCheckBox(r).IsChecked == false);
                count.Text.Should().Be("4 jeux sélectionnés");

                // The sort button now sorts the checked games, and its sentence says what it writes
                start.Content.Should().Be("Trier la sélection");
                ToolTip.GetTip(start).Should().Be(vm.Localization.Tr("panel4.tooltip_start_checked"));
                var footerSummary = window.FindControl<TextBlock>("LblSortSummary")!;
                footerSummary.IsVisible.Should().BeTrue();
                footerSummary.Text.Should().Be(vm.PartialSort.CheckedSummaryText);
                window.FindControl<TextBlock>("LblVolume")!.Text.Should().EndWith("(sélection)");

                // Checking one file of the game checks the whole game again: every game is checked
                var fileRow = RealizedRows(grid).First(r => header.Files.Contains(r.DataContext) && RowCheckBox(r).IsEnabled);
                ClickCenter(window, RowCheckBox(fileRow));
                Settle(window);
                header.IsSortChecked.Should().BeTrue();
                start.Content.Should().Be("Démarrer le tri");

                // Files selected with Ctrl or Shift are unchecked together with the space bar
                var files = RealizedRows(grid).Where(r => r.DataContext is PreviewItemViewModel && RowCheckBox(r).IsEnabled).Take(4).ToList();
                foreach (var row in files)
                {
                    grid.SelectedItems.Add(row.DataContext);
                }

                grid.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space });
                Settle(window);
                var games = files.Select(r => ((PreviewItemViewModel)r.DataContext!).CanonicalGameKey).Distinct().Count();
                vm.PartialSort.CheckedUnitCount.Should().Be(5 - games);

                // "Check All", partly checked, unchecks every game, then checks them all
                var checkAll = window.FindControl<CheckBox>("ChkCheckAllShown")!;
                checkAll.IsChecked.Should().BeNull();
                ClickCenter(window, checkAll);
                Settle(window);
                checkAll.IsChecked.Should().BeFalse();
                count.Text.Should().Be("Aucun jeu sélectionné");
                start.IsEffectivelyEnabled.Should().BeFalse("nothing is left to sort");
                ClickCenter(window, checkAll);
                Settle(window);
                vm.PartialSort.CheckedUnitCount.Should().Be(5);
                checkAll.IsChecked.Should().BeTrue();
                start.Content.Should().Be("Démarrer le tri");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The search bar above the table: the search box, its clear button and the buttons next to it have the same height
    /// and the same vertical centre, and the labels are centred on it.
    /// </summary>
    [Fact]
    public async Task PreviewSearchBar_ShouldAlignItsControls()
    {
        await RunOnUiThreadAsync(() =>
        {
            var (_, window) = ShowFinishedRun(CreateGroupedItemSet(3), streamRows: false);
            try
            {
                var search = window.FindControl<AutoCompleteBox>("EditSearchPreview")!;
                var box = search.GetVisualDescendants().OfType<TextBox>().First();
                var reference = BoundsIn(window, box);
                foreach (var name in new[] { "BtnClearSearchPreview", "BtnGroupByGame", "BtnColumns" })
                {
                    var bounds = BoundsIn(window, window.FindControl<Control>(name)!);
                    bounds.Height.Should().BeApproximately(reference.Height, 1, $"{name} is as high as the search box");
                    bounds.Center.Y.Should().BeApproximately(reference.Center.Y, 1, $"{name} is centred on the search box");
                }

                foreach (var name in new[] { "LblSearchPreview", "ChkCheckAllShown", "LblCheckedCount" })
                {
                    var control = window.FindControl<Control>(name)!;
                    if (control.IsEffectivelyVisible)
                    {
                        BoundsIn(window, control).Center.Y.Should().BeApproximately(reference.Center.Y, 1, $"{name} is centred on the search box");
                    }
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task PreviewContextMenu_ShouldCheckAndUncheckTheSelectedRows()
    {
        await RunOnUiThreadAsync(() =>
        {
            var (vm, window) = ShowFinishedRun(CreateGroupedItemSet(3), streamRows: false);
            try
            {
                var menu = window.FindControl<DataGrid>("TablePreview")!.ContextMenu!;
                var check = menu.Items.OfType<MenuItem>().Single(m => m.Name == "MenuCheckSelectedRows");
                var uncheck = menu.Items.OfType<MenuItem>().Single(m => m.Name == "MenuUncheckSelectedRows");
                check.Header.Should().Be("Cocher les lignes sélectionnées");
                uncheck.Header.Should().Be("Décocher les lignes sélectionnées");
                check.Command.Should().BeSameAs(vm.PartialSort.CheckGridSelectionCommand);
                uncheck.Command.Should().BeSameAs(vm.PartialSort.UncheckGridSelectionCommand);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task PartialSortMenu_ShouldOfferTheWholeSimulationWhileGamesAreUnchecked()
    {
        var (vm, config, simulation) = await CreatePartialSortSetupAsync();

        await RunOnUiThreadAsync(() =>
        {
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Execution.PlayRunForTests(config, isDryRun: true, simulation);
                Settle(window);
                var menuButton = window.FindControl<DropDownButton>("BtnPartialSortMenu")!;
                menuButton.Flyout!.ShowAt(menuButton);
                Settle(window);
                var sortAll = window.FindControl<MenuItem>("MenuSortAll")!;
                sortAll.IsVisible.Should().BeFalse("every game is checked: the sort button sorts the whole simulation");
                window.FindControl<MenuItem>("MenuSortChecked").Should().BeNull("the sort button sorts the checked games");
                menuButton.Flyout.Hide();

                vm.PartialSort.SetChecked(vm.PreviewTable.AllPreviewItems.Where(r => r.SourceName.StartsWith("Game C", StringComparison.Ordinal)), false);
                menuButton.Flyout.ShowAt(menuButton);
                Settle(window);

                var start = window.FindControl<Button>("BtnStart")!;
                start.Content.Should().Be("Trier la sélection");
                start.Classes.Should().Contain("primary");
                window.FindControl<TextBlock>("LblSortSummary")!.Text.Should().StartWith("Copier 2 jeux (2 fichiers)");
                sortAll.IsVisible.Should().BeTrue("the whole simulation is still sorted from the menu");
                sortAll.Command.Should().BeSameAs(vm.Execution.StartSortCommand);
                window.FindControl<TextBlock>("LblSortAllSummary")!.Text.Should().StartWith("Copier 3 fichiers");
                menuButton.Flyout.Hide();
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The "check all" box and the counter above the table fit in every language, with the longest counter (some
    /// checked games hidden by the search). Real fonts only.
    /// </summary>
    [Fact]
    public async Task SelectionCounter_Texts_ShouldNotBeTrimmed()
    {
        if (Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") != "1")
        {
            return;
        }

        await RunOnUiThreadAsync(() =>
        {
            var (vm, window) = ShowFinishedRun(CreateGroupedItemSet(30), streamRows: false);
            try
            {
                vm.PartialSort.SetChecked(vm.PreviewTable.AllPreviewItems.Take(3), false);
                vm.PreviewTable.PreviewSearchText = "Game 00001";
                foreach (var locale in vm.Localization.GetAvailableLocales().Select(l => l.Code))
                {
                    vm.UiLocale = locale;
                    Settle(window);
                    window.FindControl<TextBlock>("LblCheckedCount")!.Text.Should().Contain(locale == "fr" ? "masqués" : locale == "en" ? "hidden" : "ocultos");
                    foreach (var name in new[] { "ChkCheckAllShown", "LblCheckedCount", "BtnGroupByGame", "BtnColumns", "BtnStart" })
                    {
                        AssertNotTrimmed(window.FindControl<Control>(name)!, $"{name} ({locale})");
                    }

                    window.FindControl<AutoCompleteBox>("EditSearchPreview")!.Bounds.Width
                        .Should().BeGreaterThan(150, $"the search field keeps room next to the selection bar ({locale})");
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The menu joined to the sort button is painted as a whole, at rest and under the pointer: the Fluent theme
    /// paints the background of its template, so that colouring only its content left a strip of green next to a
    /// grey arrow when it was hovered.
    /// </summary>
    [Fact]
    public async Task PartialSortMenu_UnderThePointer_ShouldBePaintedAsAWhole()
    {
        var (vm, config, simulation) = await CreatePartialSortSetupAsync();

        await RunOnUiThreadAsync(() =>
        {
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Execution.PlayRunForTests(config, isDryRun: true, simulation);
                Settle(window);
                var menuButton = window.FindControl<DropDownButton>("BtnPartialSortMenu")!;
                menuButton.Classes.Should().Contain("primary");
                Color Background() => menuButton.GetVisualDescendants().OfType<Border>()
                    .Where(b => b.Bounds.Width >= menuButton.Bounds.Width - 2 && b.Background is ISolidColorBrush)
                    .Select(b => ((ISolidColorBrush)b.Background!).Color)
                    .First();
                Color Palette(string key) => ((ISolidColorBrush)window.FindResource(window.ActualThemeVariant, key)!).Color;

                Background().Should().Be(Palette("StartButtonBackgroundBrush"));

                ((IPseudoClasses)menuButton.Classes).Set(":pointerover", true);
                Settle(window);
                Background().Should().Be(Palette("StartButtonHoverBackgroundBrush"), "the whole menu button is hovered");
                ((IPseudoClasses)menuButton.Classes).Set(":pointerover", false);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
