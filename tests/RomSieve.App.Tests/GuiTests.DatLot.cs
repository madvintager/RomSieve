using Avalonia.Headless;
using Avalonia.Controls;
using RomSieve.App.ViewModels;
using RomSieve.App.Views;

namespace RomSieve.App.Tests;

/// <summary>
/// A lot of DATs in the window: the subfolders option, greyed for a DAT file; the list of the DATs to check, shown for a
/// lot only; the folder options of the output step, the mirror greyed without a folder per DAT; the variable of the DAT;
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
    public async Task DatLot_ShouldBeShownToCheck_WithTheFolderOptionsOfAFolderOfDats()
    {
        await RunOnUiThreadAsync(async () =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                var recursive = window.FindControl<CheckBox>("ChkDatRecursive")!;
                var panel = window.FindControl<Border>("PanelDatLot")!;
                recursive.IsEnabled.Should().BeFalse("without a DAT, there is no folder to read");
                panel.IsVisible.Should().BeFalse();
                ToolTip.GetShowOnDisabled(recursive).Should().BeTrue();

                vm.Directories.DatFilePath = WriteGuiLot();
                await vm.Directories.DatLoadTask;
                Settle(window);

                recursive.IsEnabled.Should().BeTrue();
                panel.IsVisible.Should().BeTrue();
                var list = window.FindControl<ListBox>("ListDatLot")!;
                list.ItemCount.Should().Be(2);
                window.FindControl<TextBlock>("LblDatLotCount")!.Text.Should().Be("2 DAT cochés sur 2");
                CaptureLotStep(window, "collection");
                window.FindControl<Button>("BtnDatLotUncheckShown")!.Command!.Execute(null);
                vm.Directories.ExcludedDats.Should().Equal("Nintendo - Game Boy", "Nintendo - Game Boy Color");

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
                panel.IsVisible.Should().BeFalse();
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
}
