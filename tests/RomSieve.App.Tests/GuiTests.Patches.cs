using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using RomSieve.App.ViewModels;
using RomSieve.App.Views;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// The patches in the window: the card of the Collection step, always shown, whose option is greyed out without a
/// patch folder, and the chip and the count of the banner, shown only when the run has some.
/// </summary>
public sealed partial class GuiTests
{
    [Fact]
    public async Task PatchCard_ShouldBeShownWhateverTheConvention_AndGreyTheOptionWithoutAFolder()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.CurrentStep = WorkflowStep.Collection;
                foreach (var convention in new[] { NamingConvention.Auto, NamingConvention.Mame, NamingConvention.Tosec })
                {
                    vm.Directories.SelectedNamingConvention = convention;
                    Settle(window);
                    window.FindControl<Border>("CardPatches")!.IsEffectivelyVisible.Should().BeTrue($"nothing hides with {convention}");
                }

                var option = window.FindControl<CheckBox>("ChkPatchOnly")!;
                vm.Directories.PatchDir = string.Empty;
                Settle(window);
                option.IsEffectivelyEnabled.Should().BeFalse("the option has no effect without a patch folder");
                ToolTip.GetShowOnDisabled(option).Should().BeTrue("its tooltip says when it applies");

                vm.Directories.PatchDir = Path.Combine(_tempDir, "patches");
                Settle(window);
                option.IsEffectivelyEnabled.Should().BeTrue();
                option.IsChecked = true;
                vm.Directories.PatchOnly.Should().BeTrue();
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Banner_ShouldShowThePatchedChipAndTheUnappliedPatchesOnlyWhenTheRunHasSome()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.CurrentStep = WorkflowStep.Results;
                vm.StatsBanner.UpdateStats(new ProcessStats { TotalScanned = 1, ValidCount = 1 });
                Settle(window);
                window.FindControl<ToggleButton>("ChipPatched")!.IsEffectivelyVisible.Should().BeFalse();
                window.FindControl<Button>("BtnUnappliedPatches")!.IsEffectivelyVisible.Should().BeFalse();
                window.FindControl<Button>("BtnPatchesToName")!.IsEffectivelyVisible.Should().BeFalse();

                vm.StatsBanner.UpdateStats(new ProcessStats
                {
                    TotalScanned = 2,
                    ValidCount = 2,
                    PatchedCount = 1,
                    UnappliedPatches = [new UnappliedPatch(new PatchFile("orphan.bps"), LocalizedMessage.Create("reason.patch_orphan", ("crc", "12345678")))]
                });
                Settle(window);
                window.FindControl<ToggleButton>("ChipPatched")!.IsEffectivelyVisible.Should().BeTrue();
                var link = window.FindControl<Button>("BtnUnappliedPatches")!;
                link.IsEffectivelyVisible.Should().BeTrue();
                window.FindControl<TextBlock>("LblUnappliedPatches")!.Text.Should().Be("1 patch non appliqué");
                ((string?)ToolTip.GetTip(link)).Should().Contain("orphan.bps");
                link.Command.Should().BeSameAs(vm.Patches.OpenCommand, "a click opens the window of the patches");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task DifferentialSimulation_ShouldFollowThePatchesAddedAndRemoved()
    {
        // A Japanese and an American release of a game, the American one kept, and a translation of the Japanese one
        var source = Path.Combine(_tempDir, "patch_src");
        var patches = Path.Combine(_tempDir, "patch_folder");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(patches);
        var japan = System.Text.Encoding.ASCII.GetBytes(new string('J', 4096));
        var usa = System.Text.Encoding.ASCII.GetBytes(new string('U', 4096));
        File.WriteAllBytes(Path.Combine(source, "Game (Japan).sfc"), japan);
        File.WriteAllBytes(Path.Combine(source, "Game (USA).sfc"), usa);
        var dat = Path.Combine(_tempDir, "patch_snes.dat");
        File.WriteAllText(dat, $"""
            <?xml version="1.0"?><datafile><header><name>Nintendo - SNES</name><homepage>No-Intro</homepage></header>
            <game name="Game (Japan)"><description>Game (Japan)</description><rom name="Game (Japan).sfc" size="4096" crc="{PatchTestEncoder.Crc(japan):x8}"/></game>
            <game name="Game (USA)"><description>Game (USA)</description><rom name="Game (USA).sfc" size="4096" crc="{PatchTestEncoder.Crc(usa):x8}"/></game></datafile>
            """);
        File.WriteAllBytes(Path.Combine(patches, "Game (Japan) (T-En).bps"), PatchTestEncoder.BpsLinear(japan, [.. japan, .. "En"u8.ToArray()]));

        var vm = CreateViewModel();
        vm.Directories.SourceDir = source;
        vm.Directories.DestinationDir = Path.Combine(_tempDir, "patch_dst");
        vm.Directories.DatFilePath = dat;
        vm.Directories.PatchDir = patches;
        vm.Priorities.OneGameOneRom = true;
        vm.Priorities.LanguagesText = "en, ja";
        await vm.Execution.SimulateAsync();
        var translation = Row(vm, "Game (Japan) (T-En).bps");
        translation.IsPatched.Should().BeTrue();

        // The same simulation again: the row of the patched version is kept, unchanged
        await vm.Execution.SimulateAsync();
        Row(vm, "Game (Japan) (T-En).bps").Should().BeSameAs(translation);
        vm.AllPreviewItems.Should().OnlyContain(r => r.Change == PreviewRowChange.None);

        // A new patch: a new row; the option to write the patched versions only: the origin it replaces changes
        File.WriteAllBytes(Path.Combine(patches, "Game (USA) (Hack).ups"), PatchTestEncoder.Ups(usa, [.. usa, .. "Hack"u8.ToArray()]));
        vm.Directories.PatchOnly = true;
        await vm.Execution.SimulateAsync();
        Row(vm, "Game (USA) (Hack).ups").Change.Should().Be(PreviewRowChange.Added);
        Row(vm, "Game (USA).sfc").Change.Should().Be(PreviewRowChange.Modified);
        Row(vm, "Game (USA).sfc").Item.Status.Should().Be(FileStatus.Excluded);
        translation.Change.Should().Be(PreviewRowChange.None);

        // A patch removed: its row goes, and the origin it replaced is written again
        File.Delete(Path.Combine(patches, "Game (USA) (Hack).ups"));
        await vm.Execution.SimulateAsync();
        vm.AllPreviewItems.Should().NotContain(r => r.SourceName == "Game (USA) (Hack).ups");
        Row(vm, "Game (USA).sfc").Item.Status.Should().Be(FileStatus.Valid);
        Row(vm, "Game (USA).sfc").Change.Should().Be(PreviewRowChange.Modified);
    }

    /// <summary>
    /// An IPS without CRC32: the banner opens the window of the patches, the patch is renamed with the CRC32 of the ROM
    /// proposed, and « Simulate again » applies it. The texts of the window are not cut in any language at its minimum
    /// size (measured with the real fonts only, ROMSIEVE_HEADLESS_SKIA=1).
    /// </summary>
    [Fact]
    public async Task PatchesWindow_FromTheBanner_ShouldRenameAnIpsAndSimulateAgain()
    {
        var source = Path.Combine(_tempDir, "ips_src");
        var patches = Path.Combine(_tempDir, "ips_folder");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(patches);
        var usa = System.Text.Encoding.ASCII.GetBytes(new string('U', 4096));
        File.WriteAllBytes(Path.Combine(source, "Game (USA).sfc"), usa);
        var dat = Path.Combine(_tempDir, "ips_snes.dat");
        File.WriteAllText(dat, $"""
            <?xml version="1.0"?><datafile><header><name>Nintendo - SNES</name><homepage>No-Intro</homepage></header>
            <game name="Game (USA)"><description>Game (USA)</description><rom name="Game (USA).sfc" size="4096" crc="{PatchTestEncoder.Crc(usa):x8}"/></game></datafile>
            """);
        File.WriteAllBytes(Path.Combine(patches, "Game (T-Fr).ips"), PatchTestEncoder.IpsDiff(usa, [.. usa, .. "Fr"u8.ToArray()]));

        var dialogs = new HeadlessDialogService();
        var vm = CreateViewModel(dialogs);
        vm.Directories.SourceDir = source;
        vm.Directories.DestinationDir = Path.Combine(_tempDir, "ips_dst");
        vm.Directories.DatFilePath = dat;
        vm.Directories.PatchDir = patches;
        await vm.Execution.SimulateAsync();
        vm.AllPreviewItems.Should().NotContain(r => r.IsPatched);

        await RunOnUiThreadAsync(async () =>
        {
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.CurrentStep = WorkflowStep.Results;
                Settle(window);
                var link = window.FindControl<Button>("BtnUnappliedPatches")!;
                link.IsEffectivelyVisible.Should().BeTrue();
                link.Command!.CanExecute(null).Should().BeTrue();

                dialogs.OnShowPatches = async shown =>
                {
                    // The window, shown without its modal wait (see AGENTS.App.md)
                    var patchesWindow = new PatchesWindow { DataContext = shown };
                    patchesWindow.Show(window);
                    try
                    {
                        await Task.WhenAll(shown.Rows.Select(r => r.FormsLoading));
                        Settle(patchesWindow);
                        patchesWindow.FindControl<ListBox>("ListPatches")!.ItemCount.Should().Be(1);
                        patchesWindow.FindControl<ComboBox>("ComboPatchOrigin")!.SelectedItem.Should().BeSameAs(shown.Rows[0].Origins[0]);
                        patchesWindow.FindControl<ComboBox>("ComboPatchForm")!.IsEnabled.Should().BeFalse("the ROM has a single form");
                        var rename = patchesWindow.FindControl<Button>("BtnPatchesRename")!;
                        var again = patchesWindow.FindControl<Button>("BtnPatchesSimulateAgain")!;
                        rename.IsEffectivelyEnabled.Should().BeFalse("no patch is checked");
                        again.IsEffectivelyEnabled.Should().BeFalse("no patch is renamed");
                        ToolTip.GetShowOnDisabled(again).Should().BeTrue();

                        if (Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") == "1")
                        {
                            patchesWindow.Width = patchesWindow.MinWidth;
                            patchesWindow.Height = patchesWindow.MinHeight;
                            foreach (var locale in new[] { "fr", "en", "es" })
                            {
                                vm.UiLocale = locale;
                                Settle(patchesWindow);
                                foreach (var name in new[] { "BtnPatchesCheckNoteMatched", "BtnPatchesRename", "BtnPatchesSimulateAgain", "BtnPatchesClose", "BtnPatchChooseFound" })
                                {
                                    AssertNotTrimmed(patchesWindow.FindControl<Control>(name)!, $"{name} ({locale})");
                                }
                            }

                            vm.UiLocale = "fr";
                        }

                        shown.Rows[0].IsChecked = true;
                        Settle(patchesWindow);
                        rename.Content.Should().Be("Renommer (1)");
                        CaptureLotStep(patchesWindow, "patches");
                        await shown.RenameCommand.ExecuteAsync(null);
                        Settle(patchesWindow);
                        again.IsEffectivelyEnabled.Should().BeTrue();
                    }
                    finally
                    {
                        patchesWindow.Close();
                    }

                    return true;
                };

                await vm.Patches.OpenCommand.ExecuteAsync(null);
                File.Exists(Path.Combine(patches, $"Game (T-Fr) [{PatchTestEncoder.Crc(usa):x8}].ips")).Should().BeTrue();
                vm.AllPreviewItems.Should().ContainSingle(r => r.IsPatched, "the new simulation applies the patch renamed");
                vm.StatsBanner.IsUnappliedPatchesVisible.Should().BeFalse();
            }
            finally
            {
                window.Close();
            }
        });
    }
}
