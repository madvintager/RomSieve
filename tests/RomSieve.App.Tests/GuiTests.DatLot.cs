using Avalonia.Headless;
using Avalonia.Controls;
using RomSieve.App.ViewModels;
using RomSieve.App.Views;

namespace RomSieve.App.Tests;

/// <summary>
/// A lot of DATs in the window: the subfolders option, greyed for a DAT file; the button that opens the DATs to check,
/// greyed without a lot, and their window (search and its cross, buttons); the folder options of the output step, the mirror greyed without a folder per DAT; the variable of the DAT;
/// the column of the DAT in the columns menu and the filter by DAT of the table, greyed without a lot.
/// </summary>
public sealed partial class GuiTests
{
    private string WriteGuiLot()
    {
        var folder = Path.Combine(_tempDir, "gui_lot");
        Directory.CreateDirectory(folder);
        foreach (var (file, name) in new[] { ("gb.dat", "Nintendo - Game Boy"), ("gbc.dat", "Nintendo - Game Boy Color") })
        {
            File.WriteAllText(Path.Combine(folder, file), $"""
                <?xml version="1.0"?>
                <datafile><header><name>{name}</name><homepage>No-Intro</homepage></header>
                <game name="Tetris (World)"><description>Tetris (World)</description><rom name="Tetris (World).gb" size="4" crc="12345678"/></game></datafile>
                """);
        }

        return folder;
    }

    // With the captures of the themes (ROMSIEVE_THEME_CAPTURE_DIR, real fonts): the steps of a lot, to look at them
    private static void CaptureLotStep(Window window, string step)
    {
        if (Environment.GetEnvironmentVariable("ROMSIEVE_THEME_CAPTURE_DIR") is { Length: > 0 } outputDir)
        {
            Directory.CreateDirectory(outputDir);
            window.CaptureRenderedFrame()!.Save(Path.Combine(outputDir, $"romsieve_lot_{step}.png"));
        }
    }

    [Fact]
    public async Task FolderOfDatsBeingRead_ShouldShowItsProgress_AndGreyOutTheSimulationAndTheSort()
    {
        await RunOnUiThreadAsync(async () =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Directories.SourceDir = _tempDir;
                vm.Directories.DatFilePath = WriteGuiLot();
                await vm.Directories.DatLoadTask;
                Settle(window);

                var panel = window.FindControl<StackPanel>("PanelDatFolderRead")!;
                var bar = window.FindControl<ProgressBar>("ProgressDatFolderRead")!;
                var simulate = window.FindControl<Button>("BtnSimulate")!;
                var start = window.FindControl<Button>("BtnStart")!;
                panel.IsVisible.Should().BeFalse("the folder is read");
                simulate.IsEffectivelyEnabled.Should().BeTrue();
                ToolTip.GetShowOnDisabled(simulate).Should().BeTrue();
                ToolTip.GetShowOnDisabled(start).Should().BeTrue();

                // The state of a read under way (its course is tested by DatFolderReadingTests)
                vm.Directories.IsReadingDatFolder = true;
                vm.Directories.DatFolderReadTotal = 0;
                Settle(window);
                panel.IsVisible.Should().BeTrue();
                bar.IsIndeterminate.Should().BeTrue("the number of DATs is not known while the folder is listed");
                simulate.IsEffectivelyEnabled.Should().BeFalse();
                start.IsEffectivelyEnabled.Should().BeFalse();
                window.FindControl<DropDownButton>("BtnPartialSortMenu")!.IsEffectivelyEnabled.Should().BeFalse();
                var wait = vm.I18n["panel4.tooltip_wait_dat_folder"];
                ToolTip.GetTip(simulate).Should().Be(wait);
                ToolTip.GetTip(start).Should().Be(wait);

                vm.Directories.DatFolderReadTotal = 40;
                vm.Directories.DatFolderReadCount = 10;
                Settle(window);
                bar.IsIndeterminate.Should().BeFalse();
                (bar.Maximum, bar.Value).Should().Be((40, 10));
                window.FindControl<TextBlock>("LblDatFolderRead")!.Text.Should().Be("10 / 40 DAT lus");

                vm.Directories.IsReadingDatFolder = false;
                Settle(window);
                panel.IsVisible.Should().BeFalse();
                simulate.IsEffectivelyEnabled.Should().BeTrue();
                start.IsEffectivelyEnabled.Should().BeTrue();
                ToolTip.GetTip(simulate).Should().Be(vm.I18n["panel4.tooltip_simulate"]);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task DatLot_ShouldOpenItsDatsToCheck_WithTheFolderOptionsOfAFolderOfDats()
    {
        await RunOnUiThreadAsync(async () =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                var recursive = window.FindControl<CheckBox>("ChkDatRecursive")!;
                var choose = window.FindControl<Button>("BtnChooseLotDats")!;
                recursive.IsEnabled.Should().BeFalse("without a DAT, there is no folder to read");
                choose.IsVisible.Should().BeTrue("nothing is hidden according to the DAT");
                choose.IsEnabled.Should().BeFalse();
                ToolTip.GetShowOnDisabled(recursive).Should().BeTrue();
                ToolTip.GetShowOnDisabled(choose).Should().BeTrue();

                vm.Directories.DatFilePath = WriteGuiLot();
                await vm.Directories.DatLoadTask;
                Settle(window);

                recursive.IsEnabled.Should().BeTrue();
                choose.IsEnabled.Should().BeTrue();
                choose.Command.Should().BeSameAs(vm.Directories.ChooseLotDatsCommand);
                window.FindControl<TextBlock>("LblDatInfo")!.Text.Should().Be("Lot de 2 DAT (2 No-Intro), 2 cochés");
                CaptureLotStep(window, "collection");

                // The window of the lot, shown without its modal wait (see AGENTS.App.md)
                var lotWindow = new DatLotWindow { DataContext = vm.Directories.DatLot };
                lotWindow.Show(window);
                try
                {
                    Settle(lotWindow);
                    lotWindow.FindControl<ListBox>("ListDatLot")!.ItemCount.Should().Be(2);
                    lotWindow.FindControl<TextBlock>("LblDatLotCount")!.Text.Should().Be("2 DAT cochés sur 2");
                    var check = lotWindow.FindControl<Button>("BtnDatLotCheckShown")!;
                    var uncheck = lotWindow.FindControl<Button>("BtnDatLotUncheckShown")!;
                    (check.Content, uncheck.Content).Should().Be(("Tout cocher", "Tout décocher"));
                    var shownChoice = lotWindow.FindControl<ComboBox>("ComboDatLotShown")!;
                    shownChoice.ItemCount.Should().Be(3);
                    vm.Directories.DatLot.Entries[0].IsIncluded = false;
                    shownChoice.SelectedIndex = 2;
                    Settle(lotWindow);
                    vm.Directories.DatLot.Shown.Should().Be(DatLotShown.Unchecked);
                    lotWindow.FindControl<ListBox>("ListDatLot")!.ItemCount.Should().Be(1);
                    check.Content.Should().Be("Cocher les affichés");
                    check.Command!.Execute(null);
                    shownChoice.SelectedIndex = 0;
                    Settle(lotWindow);
                    vm.Directories.DatLot.Shown.Should().Be(DatLotShown.All);
                    vm.Directories.ExcludedDats.Should().BeEmpty();

                    var clear = lotWindow.FindControl<Button>("BtnClearDatLotSearch")!;
                    clear.IsVisible.Should().BeFalse("there is no search to clear");
                    var search = lotWindow.FindControl<TextBox>("EditDatLotSearch")!;
                    search.Text = "nintendo";
                    Settle(lotWindow);
                    clear.IsVisible.Should().BeTrue();
                    clear.Command!.Execute(null);
                    Settle(lotWindow);
                    search.Text.Should().BeEmpty();
                    clear.IsVisible.Should().BeFalse();
                    search.Text = "color";
                    Settle(lotWindow);
                    lotWindow.FindControl<ListBox>("ListDatLot")!.ItemCount.Should().Be(1);
                    (check.Content, uncheck.Content).Should().Be(("Cocher les affichés", "Décocher les affichés"));
                    uncheck.Command!.Execute(null);
                    vm.Directories.ExcludedDats.Should().Equal("Nintendo - Game Boy Color");
                    vm.Directories.DatLot.SearchText = string.Empty;
                    Settle(lotWindow);
                    CaptureLotStep(lotWindow, "dats");
                    uncheck.Command!.Execute(null);
                    vm.Directories.ExcludedDats.Should().Equal("Nintendo - Game Boy", "Nintendo - Game Boy Color");
                    window.FindControl<TextBlock>("LblDatInfo")!.Text.Should().Be("Lot de 2 DAT (2 No-Intro), 0 cochés");
                }
                finally
                {
                    lotWindow.Close();
                }

                vm.Navigation.CurrentStep = WorkflowStep.Output;
                Settle(window);
                var folderPerDat = window.FindControl<CheckBox>("ChkFolderPerDat")!;
                var mirror = window.FindControl<CheckBox>("ChkDatFolderMirror")!;
                folderPerDat.IsEnabled.Should().BeTrue();
                folderPerDat.IsChecked.Should().BeTrue();
                mirror.IsEnabled.Should().BeTrue();
                CaptureLotStep(window, "output");
                folderPerDat.IsChecked = false;
                Settle(window);
                mirror.IsEnabled.Should().BeFalse("the mirror only acts with a folder per DAT");

                var insert = window.FindControl<DropDownButton>("BtnInsertVariable")!;
                insert.Flyout!.ShowAt(insert);
                Settle(window);
                window.FindControl<MenuItem>("MenuVariableDat")!.IsEnabled.Should().BeTrue();
                insert.Flyout.Hide();

                vm.Directories.DatFilePath = Path.Combine(_tempDir, "gui_lot", "gb.dat");
                await vm.Directories.DatLoadTask;
                Settle(window);
                folderPerDat.IsEnabled.Should().BeFalse("a single DAT has no folder of its own");
                mirror.IsEnabled.Should().BeFalse();
                vm.Navigation.CurrentStep = WorkflowStep.Collection;
                Settle(window);
                recursive.IsEnabled.Should().BeFalse();
                choose.IsEnabled.Should().BeFalse("a DAT file has no DATs to choose");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task DatFilterOfTheTable_ShouldBeGreyedWithoutALot_AndTheColumnOfTheDatInTheMenu()
    {
        await RunOnUiThreadAsync(() =>
        {
            var (vm, window) = ShowFinishedRun(CreateLargeItemSet().Take(5).ToList(), streamRows: false);
            try
            {
                var filter = window.FindControl<ComboBox>("ComboDatFilter")!;
                filter.IsEnabled.Should().BeFalse("the files of the run have no DAT of a lot");
                ToolTip.GetShowOnDisabled(filter).Should().BeTrue();

                var menuButton = window.FindControl<DropDownButton>("BtnColumns")!;
                menuButton.Flyout!.ShowAt(menuButton);
                Settle(window);
                var datColumn = window.FindControl<MenuItem>("MenuColumnDat")!;
                datColumn.Header.Should().Be("DAT");
                datColumn.IsChecked.Should().BeFalse();
                datColumn.IsChecked = true;
                Settle(window);
                window.FindControl<DataGrid>("TablePreview")!.Columns[PreviewTableViewModel.DatColumnIndex].IsVisible.Should().BeTrue();
                menuButton.Flyout.Hide();
                vm.PreviewTable.ResetColumns();
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    // Sized to the DAT shown, or pushed by the counter of the checked games, the list moved at each choice
    // (measured with ROMSIEVE_HEADLESS_SKIA=1)
    [Fact]
    public async Task DatFilterOfTheTable_ChoosingADat_ShouldNotMoveTheBar()
    {
        if (Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") != "1")
        {
            return;
        }

        await RunOnUiThreadAsync(() =>
        {
            var items = CreateLargeItemSet().Take(4).ToList();
            items[0].DatName = "GB";
            items[1].DatName = "Commodore C64 - Games - Collections - [D64] - Very Long Name Of A DAT Of A Lot";
            items[2].DatName = items[3].DatName = "GBC";
            var (vm, window) = ShowFinishedRun(items, streamRows: false);
            try
            {
                var filter = window.FindControl<ComboBox>("ComboDatFilter")!;
                filter.IsEnabled.Should().BeTrue();
                string[] steady = ["ComboDatFilter", "BtnGroupByGame", "BtnColumns"];
                var bounds = steady.ToDictionary(name => name, name => BoundsIn(window, window.FindControl<Control>(name)!));

                // The counter of the checked games grows with the games the DAT hides: left of the list, it only takes
                // room from the search box, and the list does not move
                foreach (var option in vm.PreviewTable.DatFilterOptions.ToList())
                {
                    vm.PreviewTable.SelectedDatFilter = option;
                    Settle(window);
                    foreach (var name in steady)
                    {
                        BoundsIn(window, window.FindControl<Control>(name)!).Should().Be(bounds[name], $"choosing {option.Label} should not move {name}");
                    }
                }

                window.FindControl<TextBlock>("LblCheckedCount")!.Text.Should().Contain("masqué", "the last DAT hides the other games");

                vm.PreviewTable.SelectedDatFilter = vm.PreviewTable.DatFilterOptions[0];
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }
}
