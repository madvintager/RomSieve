using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using RomSieve.App.ViewModels;
using RomSieve.App.Views;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// The patches in the window: the card of the Collection step, always shown, the option of the Output step greyed out
/// without a patch folder, and the chip and the count of the banner, shown only when the run has some.
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

                // Writing the patched versions only is a choice of the output: under their folder, in the Output step
                var option = window.FindControl<CheckBox>("ChkPatchOnly")!;
                option.GetLogicalAncestors().OfType<Control>().Select(c => c.Name).Should().Contain(["CardPattern", "PageOutput"])
                    .And.NotContain("PageCollection");
                vm.Navigation.CurrentStep = WorkflowStep.Output;
                Settle(window);
                option.IsEffectivelyVisible.Should().BeTrue();
                vm.Directories.PatchDir = string.Empty;
                Settle(window);
                option.IsEffectivelyEnabled.Should().BeFalse("the option has no effect without a patch folder");
                ToolTip.GetShowOnDisabled(option).Should().BeTrue("its tooltip says when it applies");

                vm.Directories.PatchDir = Path.Combine(_tempDir, "patches");
                Settle(window);
                option.IsEffectivelyEnabled.Should().BeTrue();

                // The window of the patches opens from the card once a simulation found patches
                var manage = window.FindControl<Button>("BtnManagePatches")!;
                manage.Command.Should().BeSameAs(vm.Patches.OpenCommand);
                manage.IsEffectivelyEnabled.Should().BeFalse("no simulation found patches yet");
                ToolTip.GetShowOnDisabled(manage).Should().BeTrue("its tooltip says when it opens");
                option.IsChecked = true;
                vm.Directories.PatchOnly.Should().BeTrue();

                // The box next to the title turns the patches off: the folder stays, editable, and the option greys out
                var apply = window.FindControl<CheckBox>("ChkApplyPatches")!;
                apply.IsChecked.Should().BeTrue();
                apply.IsChecked = false;
                Settle(window);
                vm.Directories.ApplyPatches.Should().BeFalse();
                option.IsEffectivelyEnabled.Should().BeFalse("nothing patched is written while the patches are off");
                manage.IsEnabled.Should().BeFalse("no patch is read while the patches are off");
                option.IsChecked.Should().BeTrue("the option keeps its value");
                window.FindControl<TextBox>("EditPatchDir")!.IsEffectivelyEnabled.Should().BeTrue();
                window.FindControl<TextBox>("EditPatchedPattern")!.IsEffectivelyEnabled.Should().BeFalse();
                apply.IsChecked = true;
                Settle(window);
                option.IsEffectivelyEnabled.Should().BeTrue();
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
                window.FindControl<Button>("BtnSeePatches")!.IsEffectivelyVisible.Should().BeFalse();

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

        // A patch removed: its row goes; the release the 1G1R kept stays replaced, by the translation of its game now
        File.Delete(Path.Combine(patches, "Game (USA) (Hack).ups"));
        await vm.Execution.SimulateAsync();
        vm.AllPreviewItems.Should().NotContain(r => r.SourceName == "Game (USA) (Hack).ups");
        Row(vm, "Game (USA).sfc").Item.Status.Should().Be(FileStatus.Excluded);
        Row(vm, "Game (USA).sfc").Item.StatusReason!.Args["patches"].Should().Be("Game (Japan) (T-En).bps");
        Row(vm, "Game (USA).sfc").Change.Should().Be(PreviewRowChange.Modified);

        // The option unchecked: the release kept is written again, beside the patched version
        vm.Directories.PatchOnly = false;
        await vm.Execution.SimulateAsync();
        Row(vm, "Game (USA).sfc").Item.Status.Should().Be(FileStatus.Valid);
        Row(vm, "Game (USA).sfc").Change.Should().Be(PreviewRowChange.Modified);
    }

    /// <summary>
    /// An IPS without CRC32: the banner opens the window of the patches, the ROM proposed is recorded for the patch,
    /// which keeps its name, and « Simulate again » applies it, the window open on its results. The texts of the window are not cut in any language at its minimum
    /// size (measured with the real fonts only, ROMSIEVE_HEADLESS_SKIA=1).
    /// </summary>
    [Fact]
    public async Task PatchesWindow_FromTheBanner_ShouldApplyAnIpsToItsRomAndSimulateAgain()
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
                        var apply = patchesWindow.FindControl<Button>("BtnPatchApply")!;
                        var again = patchesWindow.FindControl<Button>("BtnPatchesSimulateAgain")!;
                        apply.IsEffectivelyEnabled.Should().BeTrue("a ROM is proposed");
                        again.IsEffectivelyEnabled.Should().BeFalse("no choice is made");
                        ToolTip.GetShowOnDisabled(again).Should().BeTrue();

                        if (Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") == "1")
                        {
                            patchesWindow.Width = patchesWindow.MinWidth;
                            patchesWindow.Height = patchesWindow.MinHeight;
                            foreach (var locale in new[] { "fr", "en", "es" })
                            {
                                vm.UiLocale = locale;
                                Settle(patchesWindow);
                                foreach (var name in new[] { "BtnPatchesSimulateAgain", "BtnPatchesClose", "BtnPatchApply", "BtnPatchIgnore" })
                                {
                                    AssertNotTrimmed(patchesWindow.FindControl<Control>(name)!, $"{name} ({locale})");
                                }
                            }

                            vm.UiLocale = "fr";
                        }

                        apply.Content.Should().Be("Appliquer à cette ROM");
                        patchesWindow.FindControl<Button>("BtnPatchWriteName")!.IsEffectivelyVisible.Should().BeFalse("the patch is applied to no ROM");
                        CaptureLotStep(patchesWindow, "patches");
                        await shown.ApplyCommand.ExecuteAsync(null);
                        Settle(patchesWindow);
                        again.IsEffectivelyEnabled.Should().BeTrue();
                        patchesWindow.FindControl<Button>("BtnPatchUndo")!.IsEffectivelyVisible.Should().BeTrue();
                        patchesWindow.FindControl<Button>("BtnPatchAutomatic")!.IsEffectivelyVisible.Should().BeTrue();
                        patchesWindow.FindControl<Button>("BtnPatchWriteName")!.IsEffectivelyVisible.Should().BeTrue("for other tools");
                        patchesWindow.FindControl<TextBlock>("LblPatchesModified")!.Text.Should().Be("1 patch modifié");

                        // A new simulation, the window open: it shows its results, on the same patch
                        var row = shown.SelectedRow!;
                        await shown.SimulateAgainCommand.ExecuteAsync(null);
                        Settle(patchesWindow);
                        patchesWindow.IsVisible.Should().BeTrue("the window stays open");
                        shown.SelectedRow.Should().NotBeSameAs(row, "the window shows the new simulation");
                        shown.SelectedRow!.Name.Should().Be("Game (T-Fr).ips");
                        shown.SelectedRow.Source.Should().Be(PatchCrcSource.Link);
                        shown.SelectedRow.HasPatchedName.Should().BeTrue();
                        again.IsEffectivelyEnabled.Should().BeFalse("nothing is left to simulate");
                        patchesWindow.FindControl<TextBlock>("LblPatchesModified")!.Text.Should().BeEmpty();
                        vm.AllPreviewItems.Should().ContainSingle(r => r.IsPatched, "the table shows the new simulation too");
                    }
                    finally
                    {
                        patchesWindow.Close();
                    }
                };

                await vm.Patches.OpenCommand.ExecuteAsync(null);
                Directory.GetFiles(patches).Select(Path.GetFileName).Should().Equal("Game (T-Fr).ips");
                vm.AllPreviewItems.Should().ContainSingle(r => r.IsPatched, "the new simulation follows the choice");
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
    /// without recording anything; and, with the real fonts only (ROMSIEVE_HEADLESS_SKIA=1), no text cut at the minimum
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
                shown.Rows.Select(r => r.State).Should().Contain([PatchRowState.ToMatch, PatchRowState.Applied, PatchRowState.Unfixable]);

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

                // Each ROM proposed shows its folder in the source and its CRC32, with its full path in the tooltip
                var origins = patchesWindow.FindControl<ListBox>("ListPatchOrigins")!;
                var usaCrc = $"{PatchTestEncoder.Crc(usa):X8}";
                var worldCrc = $"{PatchTestEncoder.Crc(world):X8}";
                origins.GetVisualDescendants().OfType<TextBlock>().Should().Contain(t => t.Text == "usa" && t.IsEffectivelyVisible, "its folder in the source");
                origins.GetVisualDescendants().OfType<TextBlock>().Should().Contain(t => t.Text == usaCrc && t.IsEffectivelyVisible, "its CRC32");
                origins.GetVisualDescendants().OfType<DockPanel>().Should().Contain(d => Equals(ToolTip.GetTip(d), Path.Combine(source, "usa", "Game (USA).sfc")));

                // Ctrl+F, a search, Enter: the results show the CRC32 instead of the path (kept in the tooltip); the first
                // file found is chosen, nothing is recorded
                var row = shown.SelectedRow!;
                row.Name.Should().Be("Game (T-Fr).ips");
                patchesWindow.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
                Settle(patchesWindow);
                var search = patchesWindow.FindControl<TextBox>("EditPatchOriginSearch")!;
                patchesWindow.FocusManager!.GetFocusedElement().Should().BeSameAs(search);
                patchesWindow.KeyTextInput("world");
                Settle(patchesWindow);
                shown.OriginSearchText.Should().Be("world");
                var results = patchesWindow.FindControl<ListBox>("ListPatchOriginResults")!;
                results.IsEffectivelyVisible.Should().BeTrue();
                var worldPath = Path.Combine(source, "Other (World).sfc");
                results.GetVisualDescendants().OfType<TextBlock>().Should().Contain(t => t.Text == worldCrc && t.IsEffectivelyVisible, "the search shows the CRC32");
                results.GetVisualDescendants().OfType<TextBlock>().Should().NotContain(t => t.Text == worldPath, "the full path is no longer shown in the row");
                results.GetVisualDescendants().OfType<Grid>().Should().Contain(g => Equals(ToolTip.GetTip(g), worldPath), "the full path stays in the tooltip");
                patchesWindow.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                await row.FormsLoading;
                Settle(patchesWindow);
                row.SelectedOrigin!.Label.Should().Be("Other (World).sfc");
                row.Link.Should().BeNull("Enter never records anything");
                shown.HasModified.Should().BeFalse();

                if (Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") == "1")
                {
                    patchesWindow.Width = patchesWindow.MinWidth;
                    patchesWindow.Height = patchesWindow.MinHeight;
                    Settle(patchesWindow);
                    foreach (var tb in origins.GetVisualDescendants().OfType<TextBlock>().Concat(results.GetVisualDescendants().OfType<TextBlock>())
                                 .Where(t => t.IsEffectivelyVisible && (t.Text == usaCrc || t.Text == worldCrc || t.Text == "Retenu")))
                    {
                        AssertNotTrimmed(tb, $"origin CRC32 or status '{tb.Text}'");
                    }

                    // A choice made, to show « Back to the automatic choice », « Undo » and the ROM written in the name
                    await shown.ApplyCommand.ExecuteAsync(null);
                    foreach (var locale in new[] { "fr", "en", "es" })
                    {
                        vm.UiLocale = locale;
                        Settle(patchesWindow);
                        foreach (var name in new[]
                        {
                            "BtnPatchesClose", "BtnPatchesSimulateAgain", "BtnPatchApply", "BtnPatchIgnore", "BtnPatchAutomatic", "BtnPatchUndo",
                            "BtnPatchWriteName"
                        })
                        {
                            AssertNotTrimmed(patchesWindow.FindControl<Control>(name)!, $"{name} ({locale})");
                        }

                        var headers = list.GetVisualDescendants().OfType<Control>()
                            .Where(c => c is Button { IsEffectivelyVisible: true } || (c is TextBlock { FontWeight: FontWeight.Bold } t && t.DataContext is PatchGroupViewModel))
                            .ToList();
                        headers.OfType<Button>().Should().HaveCount(1, "only the patches to match have an action");
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
    /// An IPS applied from the CRC32 of its name: the table opens the window of the patches on it, « Do not apply this
    /// patch » sets it aside without renaming it, and closing the window simulates again, which no longer applies it, nor
    /// counts it left out.
    /// </summary>
    [Fact]
    public async Task PatchesWindow_FromTheTable_ShouldSetAnIpsAside()
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
                var link = window.FindControl<Button>("BtnSeePatches")!;
                link.IsEffectivelyVisible.Should().BeTrue("without patches left out, the banner opens the patches");
                window.FindControl<TextBlock>("LblSeePatches")!.Text.Should().Be("Voir les patchs");

                vm.SelectedPreviewItem = vm.AllPreviewItems.Single(r => r.IsPatched);
                vm.CanOpenSelectedPatch.Should().BeTrue();
                vm.OpenSelectedPatchText.Should().Be("Changer la ROM de ce patch…");
                vm.CanIgnoreSelectedPatch.Should().BeTrue();
                vm.CanOpenPreviewContextMenu.Should().BeTrue();
                vm.SelectedPreviewItem = vm.AllPreviewItems.Single(r => !r.IsPatched);
                vm.CanOpenSelectedPatch.Should().BeFalse("only a patched version has a patch");
                vm.CanIgnoreSelectedPatch.Should().BeFalse();
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
                        var ignore = patchesWindow.FindControl<Button>("BtnPatchIgnore")!;
                        ignore.IsEffectivelyEnabled.Should().BeTrue();
                        ToolTip.GetShowOnDisabled(ignore).Should().BeTrue();
                        patchesWindow.FindControl<Button>("BtnPatchApply")!.IsEffectivelyEnabled.Should().BeFalse("its name already designates that ROM");

                        await shown.IgnoreCommand.ExecuteAsync(null);
                        Settle(patchesWindow);
                        ignore.IsEffectivelyEnabled.Should().BeFalse();
                        patchesWindow.FindControl<Button>("BtnPatchesSimulateAgain")!.IsEffectivelyEnabled.Should().BeTrue();
                    }
                    finally
                    {
                        // Closed with a choice not simulated yet: the simulation runs again
                        patchesWindow.Close();
                    }
                };

                await vm.OpenSelectedPatchCommand.ExecuteAsync(null);
                File.Exists(Path.Combine(patches, patchName)).Should().BeTrue("the patch is not renamed");
                vm.AllPreviewItems.Should().NotContain(r => r.IsPatched, "the new simulation no longer applies the patch");
                vm.StatsBanner.IsUnappliedPatchesVisible.Should().BeFalse("a patch set aside is not left out");
                link.IsEffectivelyVisible.Should().BeTrue("the patch set aside is still in the window");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// CRC32 and status columns stay left-aligned across kept, excluded and duplicate ROMs, both on initial open and
    /// after switching patches or searching, and fit without clipping in every language.
    /// </summary>
    [Fact]
    public async Task PatchesWindow_OriginsAndSearchResults_ShouldLeftAlignCrc32AndStatus_AfterSwitchingPatch()
    {
        var source = Path.Combine(_tempDir, "align_src");
        var patches = Path.Combine(_tempDir, "align_patches");
        Directory.CreateDirectory(Path.Combine(source, "backup"));
        Directory.CreateDirectory(patches);

        var romA1 = System.Text.Encoding.ASCII.GetBytes(new string('A', 4096));
        var romA2 = System.Text.Encoding.ASCII.GetBytes(new string('B', 4096));
        var romB1 = System.Text.Encoding.ASCII.GetBytes(new string('C', 4096));
        var romB2 = System.Text.Encoding.ASCII.GetBytes(new string('D', 4096));
        File.WriteAllBytes(Path.Combine(source, "Alpha (Europe).sfc"), romA1);
        File.WriteAllBytes(Path.Combine(source, "Alpha (Japan).sfc"), romA2);
        File.WriteAllBytes(Path.Combine(source, "Bravo (Europe).sfc"), romB1);
        File.WriteAllBytes(Path.Combine(source, "backup", "Bravo (Europe).sfc"), romB1);
        File.WriteAllBytes(Path.Combine(source, "Bravo (Japan).sfc"), romB2);

        var dat = Path.Combine(_tempDir, "align.dat");
        File.WriteAllText(dat, $"""
            <?xml version="1.0"?><datafile><header><name>Nintendo - SNES</name><homepage>No-Intro</homepage></header>
            <game name="Alpha"><description>Alpha</description><rom name="Alpha (Europe).sfc" size="4096" crc="{PatchTestEncoder.Crc(romA1):x8}"/></game>
            <game name="Alpha (Japan)" cloneof="Alpha"><description>Alpha (Japan)</description><rom name="Alpha (Japan).sfc" size="4096" crc="{PatchTestEncoder.Crc(romA2):x8}"/></game>
            <game name="Bravo"><description>Bravo</description><rom name="Bravo (Europe).sfc" size="4096" crc="{PatchTestEncoder.Crc(romB1):x8}"/></game>
            <game name="Bravo (Japan)" cloneof="Bravo"><description>Bravo (Japan)</description><rom name="Bravo (Japan).sfc" size="4096" crc="{PatchTestEncoder.Crc(romB2):x8}"/></game>
            </datafile>
            """);

        // Two IPS patches whose titles propose kept, excluded and duplicate ROMs
        var dummy = System.Text.Encoding.ASCII.GetBytes(new string('Z', 4096));
        File.WriteAllBytes(Path.Combine(patches, "Alpha (T-Fr).ips"), PatchTestEncoder.IpsDiff(dummy, [.. dummy, .. "Fr"u8.ToArray()]));
        File.WriteAllBytes(Path.Combine(patches, "Bravo (T-Fr).ips"), PatchTestEncoder.IpsDiff(dummy, [.. dummy, .. "Fr2"u8.ToArray()]));

        var vm = CreateViewModel();
        vm.Directories.SourceDir = source;
        vm.Directories.DestinationDir = Path.Combine(_tempDir, "align_dst");
        vm.Directories.DatFilePath = dat;
        vm.Directories.PatchDir = patches;
        vm.Priorities.CountriesText = "Europe";
        await vm.Execution.SimulateAsync();

        await RunOnUiThreadAsync(async () =>
        {
            var shown = vm.Patches;

            void AssertAligned(PatchesWindow patchesWindow, ListBox box, string context, int minRows = 2)
            {
                var rows = box.GetVisualDescendants().OfType<ListBoxItem>().Where(i => i.IsEffectivelyVisible).ToList();
                rows.Should().HaveCountGreaterOrEqualTo(minRows, context);

                var crcBlocks = rows.Select(r => r.GetVisualDescendants().OfType<TextBlock>()
                    .First(t => t.Classes.Contains("crc") && t.IsEffectivelyVisible)).ToList();
                var crcX = crcBlocks.Select(t => BoundsIn(patchesWindow, t).Left).ToList();
                crcX.Distinct().Should().HaveCount(1, $"{context}: CRCs should be left-aligned ({string.Join(", ", crcX)})");

                var statusBlocks = rows.Select(r =>
                {
                    var model = (PatchOriginOptionViewModel)r.DataContext!;
                    return r.GetVisualDescendants().OfType<TextBlock>()
                        .First(t => t.Classes.Contains("origin-status") && t.Text == model.StatusLabel && t.IsEffectivelyVisible);
                }).ToList();
                var statusX = statusBlocks.Select(t => BoundsIn(patchesWindow, t).Left).ToList();
                statusX.Distinct().Should().HaveCount(1, $"{context}: status labels should be left-aligned ({string.Join(", ", statusX)})");

                if (Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") == "1")
                {
                    foreach (var block in crcBlocks.Concat(statusBlocks))
                    {
                        AssertNotTrimmed(block, $"{context} ({block.Text})");
                    }
                }
            }

            shown.Load();
            await Task.WhenAll(shown.Rows.Select(r => r.FormsLoading));
            var initialWindow = new PatchesWindow { DataContext = shown };
            initialWindow.Show();
            try
            {
                Settle(initialWindow);
                var origins = initialWindow.FindControl<ListBox>("ListPatchOrigins")!;
                var results = initialWindow.FindControl<ListBox>("ListPatchOriginResults")!;

                AssertAligned(initialWindow, origins, "origins initially");

                // Switch from the first patch to the second patch (contains Valid, Duplicate and Excluded)
                shown.SelectedRow = shown.Rows[1];
                Settle(initialWindow);
                AssertAligned(initialWindow, origins, "origins after switching patch", minRows: 3);

                // Verify the search results list with Valid, Duplicate and Excluded
                shown.OriginSearchText = "a";
                Settle(initialWindow);
                AssertAligned(initialWindow, results, "search results", minRows: 3);
            }
            finally
            {
                initialWindow.Close();
            }

            // Verify column widths fit aligned columns and every FileStatus label when opened in each UI language
            foreach (var locale in new[] { "en", "es", "fr" })
            {
                vm.UiLocale = locale;
                shown.Load();
                await Task.WhenAll(shown.Rows.Select(r => r.FormsLoading));
                var localizedWindow = new PatchesWindow { DataContext = shown };
                localizedWindow.Show();
                try
                {
                    shown.SelectedRow = shown.Rows[1];
                    shown.OriginSearchText = "a";
                    Settle(localizedWindow);
                    var origins = localizedWindow.FindControl<ListBox>("ListPatchOrigins")!;
                    var results = localizedWindow.FindControl<ListBox>("ListPatchOriginResults")!;
                    AssertAligned(localizedWindow, origins, $"origins ({locale})", minRows: 3);
                    AssertAligned(localizedWindow, results, $"search results ({locale})", minRows: 3);

                    if (Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") == "1")
                    {
                        var sampleStatus = origins.GetVisualDescendants().OfType<TextBlock>()
                            .First(t => t.Classes.Contains("origin-status") && t.IsEffectivelyVisible);
                        foreach (var status in Enum.GetValues<FileStatus>())
                        {
                            var label = shown.I18n[PreviewItemViewModel.GetStatusLabelKey(status)];
                            AssertTextFits(sampleStatus, label, $"status '{label}' ({status}, {locale})");
                        }
                    }
                }
                finally
                {
                    localizedWindow.Close();
                }
            }
        });
    }
}
