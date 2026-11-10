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
                        patchesWindow.FindControl<ListBox>("ListPatches")!.ItemCount.Should().Be(2, "the title of its group, then the patch");
                        patchesWindow.FindControl<ListBox>("ListPatchOrigins")!.SelectedItem.Should().BeSameAs(shown.Rows[0].Origins[0]);
                        patchesWindow.FindControl<ListBox>("ListPatchForms")!.IsEffectivelyVisible.Should().BeFalse("the ROM has a single form");
                        patchesWindow.FindControl<StackPanel>("PanelPatchPreview")!.IsEffectivelyVisible.Should().BeTrue();
                        var apply = patchesWindow.FindControl<Button>("BtnPatchApply")!;
                        var again = patchesWindow.FindControl<Button>("BtnPatchesSimulateAgain")!;
                        apply.IsEffectivelyEnabled.Should().BeTrue("a ROM is proposed");
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
                                foreach (var name in new[] { "BtnPatchesSimulateAgain", "BtnPatchesClose", "BtnPatchApply", "BtnPatchDetach" })
                                {
                                    AssertNotTrimmed(patchesWindow.FindControl<Control>(name)!, $"{name} ({locale})");
                                }
                            }

                            vm.UiLocale = "fr";
                        }

                        apply.Content.Should().Be("Appliquer à cette ROM");
                        CaptureLotStep(patchesWindow, "patches");
                        await shown.ApplyCommand.ExecuteAsync(null);
                        Settle(patchesWindow);
                        again.IsEffectivelyEnabled.Should().BeTrue();
                        patchesWindow.FindControl<Button>("BtnPatchUndo")!.IsEffectivelyVisible.Should().BeTrue();
                        patchesWindow.FindControl<TextBlock>("LblPatchesModified")!.Text.Should().Be("1 patch modifié");
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

    /// <summary>
    /// The window of the patches with a patch of each kind: the marks of their states in the colors of the theme, light
    /// and dark; the folder under each ROM proposed; Ctrl+F to the search, and Enter there taking the first file found
    /// without renaming anything; and, with the real fonts only (ROMSIEVE_HEADLESS_SKIA=1), no text cut at the minimum
    /// size in any language: the buttons of the detail, of the groups, of the summary and of the footer, and the titles.
    /// </summary>
    [Fact]
    public async Task PatchesWindow_ShouldFollowTheThemeAndTheKeyboard_AndFitItsTextsInEveryLanguage()
    {
        var source = Path.Combine(_tempDir, "layout_src");
        var patches = Path.Combine(_tempDir, "layout_folder");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(patches, "hack"));
        var usa = System.Text.Encoding.ASCII.GetBytes(new string('U', 4096));
        var world = System.Text.Encoding.ASCII.GetBytes(new string('W', 4096));
        Directory.CreateDirectory(Path.Combine(source, "usa"));
        File.WriteAllBytes(Path.Combine(source, "usa", "Game (USA).sfc"), usa);
        File.WriteAllBytes(Path.Combine(source, "Other (World).sfc"), world);
        var dat = Path.Combine(_tempDir, "layout_snes.dat");
        File.WriteAllText(dat, $"""
            <?xml version="1.0"?><datafile><header><name>Nintendo - SNES</name><homepage>No-Intro</homepage></header>
            <game name="Game (USA)"><description>Game (USA)</description><rom name="Game (USA).sfc" size="4096" crc="{PatchTestEncoder.Crc(usa):x8}"/></game>
            <game name="Other (World)"><description>Other (World)</description><rom name="Other (World).sfc" size="4096" crc="{PatchTestEncoder.Crc(world):x8}"/></game></datafile>
            """);
        File.WriteAllBytes(Path.Combine(patches, "Game (T-Fr).ips"), PatchTestEncoder.IpsDiff(usa, [.. usa, .. "Fr"u8.ToArray()]));
        File.WriteAllBytes(Path.Combine(patches, "Other (Hack).ips"), PatchTestEncoder.IpsDiff(world, [.. world, .. "Hack"u8.ToArray()]));
        File.WriteAllBytes(Path.Combine(patches, "hack", "Hack.ips"), PatchTestEncoder.IpsDiff(usa, [.. usa, .. "Hk"u8.ToArray()]));
        File.WriteAllText(Path.Combine(patches, "hack", "readme.txt"), $"CRC32: {PatchTestEncoder.Crc(usa):X8}");
        File.WriteAllBytes(Path.Combine(patches, "Orphan.bps"), PatchTestEncoder.BpsLinear(new byte[64], new byte[65]));

        var vm = CreateViewModel();
        vm.Directories.SourceDir = source;
        vm.Directories.DestinationDir = Path.Combine(_tempDir, "layout_dst");
        vm.Directories.DatFilePath = dat;
        vm.Directories.PatchDir = patches;
        await vm.Execution.SimulateAsync();

        await RunOnUiThreadAsync(async () =>
        {
            var shown = vm.Patches;
            shown.Load();
            await Task.WhenAll(shown.Rows.Select(r => r.FormsLoading));
            var patchesWindow = new PatchesWindow { DataContext = shown };
            patchesWindow.Show();
            try
            {
                Settle(patchesWindow);
                shown.Rows.Select(r => r.State).Should().Contain([PatchRowState.ToMatch, PatchRowState.AppliedByNote, PatchRowState.Unfixable]);

                // The marks of the states, in the colors of the badges of the table, light then dark
                var list = patchesWindow.FindControl<ListBox>("ListPatches")!;
                TextBlock Mark(string state) => list.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("mark") && t.Classes.Contains(state));
                ((ISolidColorBrush)Mark("toMatch").Foreground!).Color.Should().Be(PaletteColor(ThemeVariant.Light, "StatusExcludedBadgeForegroundBrush"));
                ((ISolidColorBrush)Mark("applied").Foreground!).Color.Should().Be(PaletteColor(ThemeVariant.Light, "StatusValidBadgeForegroundBrush"));
                ((ISolidColorBrush)Mark("unfixable").Foreground!).Color.Should().Be(PaletteColor(ThemeVariant.Light, "StatusDuplicateBadgeForegroundBrush"));
                MainWindow.ApplyTheme(UiTheme.Dark);
                Settle(patchesWindow);
                ((ISolidColorBrush)Mark("applied").Foreground!).Color.Should().Be(PaletteColor(ThemeVariant.Dark, "StatusValidBadgeForegroundBrush"));
                MainWindow.ApplyTheme(UiTheme.Light);
                Settle(patchesWindow);

                // Each ROM proposed shows its folder in the source
                var origins = patchesWindow.FindControl<ListBox>("ListPatchOrigins")!;
                origins.GetVisualDescendants().OfType<TextBlock>().Should().Contain(t => t.Text == "usa" && t.IsEffectivelyVisible, "its folder in the source");

                // Ctrl+F, a search, Enter: the first file found is chosen, nothing is renamed
                var row = shown.SelectedRow!;
                row.Name.Should().Be("Game (T-Fr).ips");
                patchesWindow.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
                Settle(patchesWindow);
                var search = patchesWindow.FindControl<TextBox>("EditPatchOriginSearch")!;
                patchesWindow.FocusManager!.GetFocusedElement().Should().BeSameAs(search);
                patchesWindow.KeyTextInput("world");
                Settle(patchesWindow);
                shown.OriginSearchText.Should().Be("world");
                patchesWindow.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                await row.FormsLoading;
                Settle(patchesWindow);
                row.SelectedOrigin!.Label.Should().Be("Other (World).sfc");
                File.Exists(Path.Combine(patches, "Game (T-Fr).ips")).Should().BeTrue("Enter never renames anything");
                shown.HasModified.Should().BeFalse();

                if (Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") == "1")
                {
                    // A patch renamed, to show « Undo »
                    await shown.ApplyCommand.ExecuteAsync(null);
                    patchesWindow.Width = patchesWindow.MinWidth;
                    patchesWindow.Height = patchesWindow.MinHeight;
                    foreach (var locale in new[] { "fr", "en", "es" })
                    {
                        vm.UiLocale = locale;
                        Settle(patchesWindow);
                        foreach (var name in new[] { "BtnPatchesClose", "BtnPatchesSimulateAgain", "BtnPatchApply", "BtnPatchDetach", "BtnPatchUndo" })
                        {
                            AssertNotTrimmed(patchesWindow.FindControl<Control>(name)!, $"{name} ({locale})");
                        }

                        var headers = list.GetVisualDescendants().OfType<Control>()
                            .Where(c => c is Button { IsEffectivelyVisible: true } || (c is TextBlock { FontWeight: FontWeight.Bold } t && t.DataContext is PatchGroupViewModel))
                            .ToList();
                        headers.OfType<Button>().Should().HaveCount(2, "the patches to match and those applied by a readme have an action");
                        headers.OfType<TextBlock>().Should().HaveCount(3, "three groups have patches");
                        foreach (var control in headers)
                        {
                            AssertNotTrimmed(control, $"a title of group or its button ({locale})");
                        }

                        await shown.OpenBatchCommand.ExecuteAsync(PatchGroupKind.ToMatch);
                        Settle(patchesWindow);
                        foreach (var name in new[] { "BtnPatchBatchCheckAll", "BtnPatchBatchApply", "BtnPatchBatchBack" })
                        {
                            AssertNotTrimmed(patchesWindow.FindControl<Control>(name)!, $"{name} ({locale})");
                        }

                        shown.CloseBatchCommand.Execute(null);
                    }

                    vm.UiLocale = "fr";
                }
            }
            finally
            {
                // The theme belongs to the application, shared by every test
                MainWindow.ApplyTheme(UiTheme.Light);
                patchesWindow.Close();
            }
        });
    }

    /// <summary>
    /// Going from a patch to another and back: each keeps the ROM and the form chosen for it. The lists of origins and of
    /// forms write an empty choice when the selected patch changes, which used to leave the patch without its ROM.
    /// </summary>
    [Fact]
    public async Task PatchesWindow_GoingBackToAPatch_ShouldKeepItsRomAndForm()
    {
        var source = Path.Combine(_tempDir, "back_src");
        var patches = Path.Combine(_tempDir, "back_folder");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(patches);
        var usa = System.Text.Encoding.ASCII.GetBytes(new string('U', 4096));
        var world = System.Text.Encoding.ASCII.GetBytes(new string('W', 4096));
        File.WriteAllBytes(Path.Combine(source, "Game (USA).sfc"), usa);
        File.WriteAllBytes(Path.Combine(source, "Other (World).sfc"), world);
        var dat = Path.Combine(_tempDir, "back_snes.dat");
        File.WriteAllText(dat, $"""
            <?xml version="1.0"?><datafile><header><name>Nintendo - SNES</name><homepage>No-Intro</homepage></header>
            <game name="Game (USA)"><description>Game (USA)</description><rom name="Game (USA).sfc" size="4096" crc="{PatchTestEncoder.Crc(usa):x8}"/></game>
            <game name="Other (World)"><description>Other (World)</description><rom name="Other (World).sfc" size="4096" crc="{PatchTestEncoder.Crc(world):x8}"/></game></datafile>
            """);
        File.WriteAllBytes(Path.Combine(patches, "Game (T-Fr).ips"), PatchTestEncoder.IpsDiff(usa, [.. usa, .. "Fr"u8.ToArray()]));
        File.WriteAllBytes(Path.Combine(patches, "Other (Hack).ips"), PatchTestEncoder.IpsDiff(world, [.. world, .. "Hack"u8.ToArray()]));

        var dialogs = new HeadlessDialogService();
        var vm = CreateViewModel(dialogs);
        vm.Directories.SourceDir = source;
        vm.Directories.DestinationDir = Path.Combine(_tempDir, "back_dst");
        vm.Directories.DatFilePath = dat;
        vm.Directories.PatchDir = patches;
        await vm.Execution.SimulateAsync();

        await RunOnUiThreadAsync(async () =>
        {
            var shown = vm.Patches;
            shown.Load();
            await Task.WhenAll(shown.Rows.Select(r => r.FormsLoading));
            var patchesWindow = new PatchesWindow { DataContext = shown };
            patchesWindow.Show();
            try
            {
                Settle(patchesWindow);
                var origin = patchesWindow.FindControl<ListBox>("ListPatchOrigins")!;
                var first = shown.Rows[0];
                var second = shown.Rows[1];
                foreach (var row in new[] { second, first, second, first })
                {
                    shown.SelectedRow = row;
                    Settle(patchesWindow);
                    await row.FormsLoading;
                    Settle(patchesWindow);
                    row.SelectedOrigin.Should().NotBeNull($"{row.Name} keeps its ROM");
                    row.SelectedForm.Should().NotBeNull($"{row.Name} keeps its form");
                    origin.SelectedItem.Should().BeSameAs(row.SelectedOrigin);
                }

                first.SelectedOrigin!.Label.Should().Be("Game (USA).sfc");
                second.SelectedOrigin!.Label.Should().Be("Other (World).sfc");
                first.CanApply.Should().BeTrue();
                second.CanApply.Should().BeTrue();
            }
            finally
            {
                patchesWindow.Close();
            }
        });
    }

    /// <summary>
    /// An IPS applied from the CRC32 of its name: the table opens the window of the patches on it, « Stop applying to
    /// this ROM » removes the CRC32 from its name, and « Simulate again » proposes it again among the patches left out.
    /// </summary>
    [Fact]
    public async Task PatchesWindow_FromTheTable_ShouldStopApplyingAnIpsToItsRom()
    {
        var source = Path.Combine(_tempDir, "detach_src");
        var patches = Path.Combine(_tempDir, "detach_folder");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(patches);
        var usa = System.Text.Encoding.ASCII.GetBytes(new string('U', 4096));
        File.WriteAllBytes(Path.Combine(source, "Game (USA).sfc"), usa);
        var dat = Path.Combine(_tempDir, "detach_snes.dat");
        File.WriteAllText(dat, $"""
            <?xml version="1.0"?><datafile><header><name>Nintendo - SNES</name><homepage>No-Intro</homepage></header>
            <game name="Game (USA)"><description>Game (USA)</description><rom name="Game (USA).sfc" size="4096" crc="{PatchTestEncoder.Crc(usa):x8}"/></game></datafile>
            """);
        var patchName = $"Game (T-Fr) [{PatchTestEncoder.Crc(usa):x8}].ips";
        File.WriteAllBytes(Path.Combine(patches, patchName), PatchTestEncoder.IpsDiff(usa, [.. usa, .. "Fr"u8.ToArray()]));

        var dialogs = new HeadlessDialogService();
        var vm = CreateViewModel(dialogs);
        vm.Directories.SourceDir = source;
        vm.Directories.DestinationDir = Path.Combine(_tempDir, "detach_dst");
        vm.Directories.DatFilePath = dat;
        vm.Directories.PatchDir = patches;
        await vm.Execution.SimulateAsync();

        await RunOnUiThreadAsync(async () =>
        {
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.CurrentStep = WorkflowStep.Results;
                Settle(window);
                var link = window.FindControl<Button>("BtnPatchesToName")!;
                link.IsEffectivelyVisible.Should().BeTrue("the banner opens the patches applied from their name");
                window.FindControl<TextBlock>("LblPatchesToName")!.Text.Should().Be("Voir les patchs");

                vm.SelectedPreviewItem = vm.AllPreviewItems.Single(r => r.IsPatched);
                vm.CanOpenSelectedPatch.Should().BeTrue();
                vm.CanOpenPreviewContextMenu.Should().BeTrue();
                vm.SelectedPreviewItem = vm.AllPreviewItems.Single(r => !r.IsPatched);
                vm.CanOpenSelectedPatch.Should().BeFalse("only a patched version has a patch");
                vm.SelectedPreviewItem = vm.AllPreviewItems.Single(r => r.IsPatched);

                dialogs.OnShowPatches = async shown =>
                {
                    var patchesWindow = new PatchesWindow { DataContext = shown };
                    patchesWindow.Show(window);
                    try
                    {
                        await Task.WhenAll(shown.Rows.Select(r => r.FormsLoading));
                        Settle(patchesWindow);
                        shown.SelectedRow!.Name.Should().Be(patchName);
                        var detach = patchesWindow.FindControl<Button>("BtnPatchDetach")!;
                        detach.IsEffectivelyEnabled.Should().BeTrue();
                        ToolTip.GetShowOnDisabled(detach).Should().BeTrue();
                        patchesWindow.FindControl<Button>("BtnPatchApply")!.IsEffectivelyEnabled.Should().BeFalse("its name already designates that ROM");

                        await shown.DetachCommand.ExecuteAsync(null);
                        Settle(patchesWindow);
                        detach.IsEffectivelyEnabled.Should().BeFalse();
                        patchesWindow.FindControl<Button>("BtnPatchesSimulateAgain")!.IsEffectivelyEnabled.Should().BeTrue();
                    }
                    finally
                    {
                        patchesWindow.Close();
                    }

                    return true;
                };

                await vm.OpenSelectedPatchCommand.ExecuteAsync(null);
                File.Exists(Path.Combine(patches, "Game (T-Fr).ips")).Should().BeTrue();
                vm.AllPreviewItems.Should().NotContain(r => r.IsPatched, "the new simulation no longer applies the patch");
                vm.StatsBanner.IsUnappliedPatchesVisible.Should().BeTrue("it is proposed again");
            }
            finally
            {
                window.Close();
            }
        });
    }
}
