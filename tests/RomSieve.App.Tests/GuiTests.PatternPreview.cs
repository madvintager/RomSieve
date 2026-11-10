using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using RomSieve.App.ViewModels;
using RomSieve.App.Views;

namespace RomSieve.App.Tests;

/// <summary>
/// Directory pattern of the directories tab: field, "Insert Variable" and "Presets" menus, live preview, and the
/// hint of the transfer mode.
/// </summary>
public sealed partial class GuiTests
{
    [Fact]
    public async Task PatternArea_ShouldShowTheMenusAndTheLivePreview()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.CurrentStep = WorkflowStep.Output;
                Settle(window);

                // The former preset and variable buttons are gone: two menus remain
                window.FindControl<DropDownButton>("BtnInsertVariable")!.IsEffectivelyVisible.Should().BeTrue();
                window.FindControl<DropDownButton>("BtnPatternPresets")!.IsEffectivelyVisible.Should().BeTrue();
                window.FindControl<CheckBox>("ChkFlatDestination").Should().BeNull("the output at the root is a choice of the organization");

                // Built-in examples, one line each, under their caption
                var caption = window.FindControl<TextBlock>("LblPreviewCaption")!;
                caption.Text.Should().Be(vm.Localization.Tr("tab1.preview_builtin_no_source"));
                var lines = window.FindControl<ItemsControl>("ListPatternPreview")!.GetVisualDescendants().OfType<TextBlock>().ToList();
                lines.Select(l => l.Text).Should().Equal(vm.Directories.Preview.Examples.Select(e => e.TargetPath));
                lines.Should().OnlyContain(l => l.TextTrimming == TextTrimming.LeadingCharacterEllipsis);
                window.FindControl<TextBlock>("LblPreviewWarning")!.IsVisible.Should().BeFalse();

                // An invalid pattern is reported under the examples
                vm.Directories.Pattern = "{title}/{foo}/";
                vm.Directories.Preview.Refresh();
                Settle(window);
                window.FindControl<TextBlock>("LblPreviewWarning")!.IsEffectivelyVisible.Should().BeTrue();

                // At the root, a choice in sight above the pattern, no longer an entry of the presets menu: the field is
                // greyed but keeps the pattern, and a note says so
                var presets = window.FindControl<DropDownButton>("BtnPatternPresets")!;
                presets.Flyout!.ShowAt(presets);
                Settle(window);
                window.FindControl<MenuItem>("MenuFlatDestination").Should().BeNull();
                window.FindControl<MenuItem>("MenuPresetTosec")!.Header.Should().Be(vm.Localization.Tr("tab1.preset_tosec"));
                presets.Flyout.Hide();
                Settle(window);
                var byPattern = window.FindControl<RadioButton>("RadioLayoutPattern")!;
                var atRoot = window.FindControl<RadioButton>("RadioLayoutRoot")!;
                byPattern.IsChecked.Should().BeTrue();
                atRoot.Content.Should().Be(vm.Localization.Tr("tab1.chk_flat_destination"));
                BoundsIn(window, atRoot).Bottom.Should().BeLessThan(BoundsIn(window, window.FindControl<TextBox>("EditPattern")!).Top,
                    "the choice comes before the pattern it greys out");
                atRoot.IsChecked = true;
                Settle(window);
                byPattern.IsChecked.Should().BeFalse();
                vm.Directories.FlatDestination.Should().BeTrue();
                var editPattern = window.FindControl<TextBox>("EditPattern")!;
                editPattern.IsEffectivelyEnabled.Should().BeFalse();
                editPattern.Text.Should().Be("{title}/{foo}/");
                window.FindControl<DropDownButton>("BtnInsertVariable")!.IsEffectivelyEnabled.Should().BeFalse();
                window.FindControl<DropDownButton>("BtnPatternPresets")!.IsEffectivelyEnabled.Should().BeTrue("a preset leaves the root");
                window.FindControl<TextBlock>("LblFlatDestinationNote")!.IsEffectivelyVisible.Should().BeTrue();
                window.FindControl<CheckBox>("ChkRestoreArticles")!.IsEffectivelyEnabled.Should().BeTrue("the subfolder of a multi-disk game is named after its title");

                // A preset goes back to the folders, as does the other choice
                vm.Directories.PresetNoIntroRegionCommand.Execute(null);
                Settle(window);
                byPattern.IsChecked.Should().BeTrue();
                editPattern.IsEffectivelyEnabled.Should().BeTrue();
                atRoot.IsChecked = true;
                byPattern.IsChecked = true;
                Settle(window);
                vm.Directories.FlatDestination.Should().BeFalse();
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task VariableMenu_ShouldInsertAtTheCaretOfThePatternField()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.CurrentStep = WorkflowStep.Output;
                Settle(window);
                var editPattern = window.FindControl<TextBox>("EditPattern")!;

                // The user places the caret after "{publisher}/"
                editPattern.Focus();
                editPattern.CaretIndex = "{publisher}/".Length;
                Settle(window);
                vm.Directories.PatternCaretIndex.Should().Be("{publisher}/".Length);

                // The menu entry inserts its variable there; once the menu is closed, the field gets the focus back,
                // the caret after the variable
                var insert = window.FindControl<DropDownButton>("BtnInsertVariable")!;
                insert.Focus();
                insert.Flyout!.ShowAt(insert);
                Settle(window);
                var country = window.FindControl<MenuItem>("MenuVariableCountry")!;
                country.Header.Should().Be("Pays");
                ToolTip.GetTip(country).Should().Be(vm.Localization.Tr("var.tooltip_country"));
                country.Command!.Execute(null);
                insert.Flyout.Hide();
                Settle(window);

                editPattern.Text.Should().Be("{publisher}/{country}{year}/{title}/");
                editPattern.IsFocused.Should().BeTrue();
                editPattern.CaretIndex.Should().Be("{publisher}/{country}".Length);

                // The variables of TOSEC names follow the effective convention: greyed out, never hidden, their
                // tooltip shown on the disabled entry
                var publisher = window.FindControl<MenuItem>("MenuVariablePublisher")!;
                publisher.IsEnabled.Should().BeTrue();
                vm.Directories.SelectedNamingConvention = Core.Models.NamingConvention.NoIntro;
                insert.Flyout.ShowAt(insert);
                Settle(window);
                publisher.IsVisible.Should().BeTrue();
                publisher.IsEnabled.Should().BeFalse();
                ToolTip.GetShowOnDisabled(publisher).Should().BeTrue();
                ToolTip.GetTip(publisher).Should().Be(
                    $"{vm.Localization.Tr("var.tooltip_publisher")}\n{vm.Localization.Tr("var.hint_tosec_only")}");
                window.FindControl<MenuItem>("MenuVariableCountry")!.IsEnabled.Should().BeTrue();
                window.FindControl<MenuItem>("MenuVariableGenre")!.IsVisible.Should().BeTrue();
                window.FindControl<MenuItem>("MenuVariableGenre")!.IsEnabled.Should().BeFalse();
                insert.Flyout.Hide();
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task VariableMenu_ShouldHaveAnEntryPerVariable_ThatInsertsIt()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.CurrentStep = WorkflowStep.Output;
                Settle(window);
                var insert = window.FindControl<DropDownButton>("BtnInsertVariable")!;
                insert.Flyout!.ShowAt(insert);
                Settle(window);

                // An entry bound to the wrong index would insert another variable than the one it names
                var entries = new (string Name, string Tag)[]
                {
                    ("MenuVariableTitle", "{title}"), ("MenuVariableLetter", "{letter}"),
                    ("MenuVariableCountry", "{country}"), ("MenuVariableLanguage", "{language}"),
                    ("MenuVariableVersion", "{version}"), ("MenuVariablePublisher", "{publisher}"),
                    ("MenuVariableYear", "{year}"), ("MenuVariableSystem", "{system}"),
                    ("MenuVariableChipset", "{chipset}"), ("MenuVariableVideo", "{video}"),
                    ("MenuVariableDumpStatus", "{dump_status}"), ("MenuVariableGenre", "{genre}"),
                    ("MenuVariableCategory", "{category}"), ("MenuVariableDat", "{dat}")
                };
                entries.Select(e => e.Tag).Should().BeEquivalentTo(vm.Directories.PatternVariables.Select(v => v.Tag));
                foreach (var (name, tag) in entries)
                {
                    var variable = vm.Directories.PatternVariables.Single(v => v.Tag == tag);
                    var entry = window.FindControl<MenuItem>(name);
                    entry.Should().NotBeNull(name);
                    entry!.Header.Should().Be(variable.Label, name);
                    entry.Command.Should().BeSameAs(variable.InsertCommand, name);
                }

                insert.Flyout.Hide();
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task PatchedPatternRow_ShouldBeGreyedOutWithoutPatchFolder_AndShowItsLineInThePreview()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.CurrentStep = WorkflowStep.Output;
                Settle(window);
                var edit = window.FindControl<TextBox>("EditPatchedPattern")!;
                var insert = window.FindControl<DropDownButton>("BtnInsertPatchedVariable")!;
                var presets = window.FindControl<DropDownButton>("BtnPatchedPatternPresets")!;
                var line = window.FindControl<TextBlock>("LblPreviewPatched")!;

                // Under the main pattern, above the other options of the tree, which share a line
                BoundsIn(window, edit).Top.Should().BeGreaterThan(BoundsIn(window, window.FindControl<TextBox>("EditPattern")!).Bottom);
                BoundsIn(window, edit).Bottom.Should().BeLessThan(BoundsIn(window, window.FindControl<CheckBox>("ChkRestoreArticles")!).Top);
                window.FindControl<WrapPanel>("PanelTreeOptions")!.Children.Select(c => c.Name)
                    .Should().Equal(["ChkPatchOnly", "ChkRestoreArticles", "ChkFolderPerDat", "ChkDatFolderMirror"], "writing the patched versions only comes first, under their folder");
                edit.Watermark.Should().Be(vm.Localization.Tr("tab1.patched_pattern_placeholder"));

                // Without a folder of the patches: greyed out, never hidden, the tooltip saying when it applies; no line
                // in the preview, since nothing patched would be written
                foreach (var control in new Control[] { edit, insert, presets })
                {
                    control.IsEffectivelyVisible.Should().BeTrue(control.Name);
                    control.IsEffectivelyEnabled.Should().BeFalse(control.Name);
                    ToolTip.GetShowOnDisabled(control).Should().BeTrue(control.Name);
                }

                ToolTip.GetTip(edit).Should().Be(vm.Localization.Tr("tab1.tooltip_patched_pattern"));
                line.IsVisible.Should().BeFalse();

                vm.Directories.PatchDir = Path.Combine(_tempDir, "patches");
                Settle(window);
                edit.IsEffectivelyEnabled.Should().BeTrue();
                insert.IsEffectivelyEnabled.Should().BeTrue();
                line.IsEffectivelyVisible.Should().BeTrue();
                line.Text.Should().Be(vm.Directories.Preview.PatchedExampleText);
                line.TextTrimming.Should().Be(TextTrimming.LeadingCharacterEllipsis);

                // A preset of its menu fills the field
                presets.Flyout!.ShowAt(presets);
                Settle(window);
                var subfolder = window.FindControl<MenuItem>("MenuPresetPatchedSubfolder")!;
                subfolder.Header.Should().Be(vm.Localization.Tr("tab1.preset_patched_subfolder"));
                ToolTip.GetTip(subfolder).Should().Be(vm.Localization.Tr("tab1.tooltip_preset_patched_subfolder"));
                window.FindControl<MenuItem>("MenuPresetPatchedNextToOrigin")!.Command.Should().BeSameAs(vm.Directories.PresetPatchedNextToOriginCommand);
                window.FindControl<MenuItem>("MenuPresetPatchedRoot")!.Command.Should().BeSameAs(vm.Directories.PresetPatchedRootCommand);
                subfolder.Command!.Execute(null);
                presets.Flyout.Hide();
                vm.Directories.Preview.Refresh();
                Settle(window);
                edit.Text.Should().Be("{origin_folder}/Patchs");
                line.Text.Should().Contain(Path.Combine("Patchs", "Lemmings (1991)(Psygnosis)(FR)[!][T-En].adf"));

                // Everything at the root greys out the main pattern only, as its note says
                vm.Directories.FlatDestination = true;
                Settle(window);
                window.FindControl<TextBox>("EditPattern")!.IsEffectivelyEnabled.Should().BeFalse();
                edit.IsEffectivelyEnabled.Should().BeTrue("the pattern of the patched versions applies at the root too");
                insert.IsEffectivelyEnabled.Should().BeTrue();
                window.FindControl<TextBlock>("LblFlatDestinationNote")!.Text.Should().Be(vm.Localization.Tr("tab1.flat_destination_note"))
                    .And.Contain("versions patchées");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task PatchedVariableMenu_ShouldHaveAnEntryPerVariable_AndInsertAtTheCaretOfItsField()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.Directories.PatchDir = Path.Combine(_tempDir, "patches");
            vm.Directories.PatchedPattern = "Patchs/{title}";
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.CurrentStep = WorkflowStep.Output;
                Settle(window);
                var edit = window.FindControl<TextBox>("EditPatchedPattern")!;
                edit.Focus();
                edit.CaretIndex = "Patchs/".Length;
                Settle(window);
                vm.Directories.PatchedPatternCaretIndex.Should().Be("Patchs/".Length);
                vm.Directories.PatternCaretIndex.Should().Be(-1, "the caret of the main pattern is its own");

                var insert = window.FindControl<DropDownButton>("BtnInsertPatchedVariable")!;
                insert.Focus();
                insert.Flyout!.ShowAt(insert);
                Settle(window);

                // An entry bound to the wrong index would insert another variable than the one it names
                var names = new[]
                {
                    "Title", "Letter", "Country", "Language", "Version", "Publisher", "Year", "System", "Chipset", "Video",
                    "DumpStatus", "Genre", "Category", "Dat", "OriginFolder", "DatFolder"
                };
                names.Should().HaveCount(vm.Directories.PatchedPatternVariables.Count);
                for (var i = 0; i < names.Length; i++)
                {
                    var variable = vm.Directories.PatchedPatternVariables[i];
                    var entry = window.FindControl<MenuItem>($"MenuPatchedVariable{names[i]}");
                    entry.Should().NotBeNull(names[i]);
                    entry!.Header.Should().Be(variable.Label, names[i]);
                    entry.Command.Should().BeSameAs(variable.InsertCommand, names[i]);
                }

                var datFolder = window.FindControl<MenuItem>("MenuPatchedVariableDatFolder")!;
                datFolder.IsEnabled.Should().BeFalse("without a lot of DATs");
                ToolTip.GetShowOnDisabled(datFolder).Should().BeTrue();

                // The entry inserts its variable at the caret; once the menu is closed, the field gets the focus back
                window.FindControl<MenuItem>("MenuPatchedVariableOriginFolder")!.Command!.Execute(null);
                insert.Flyout.Hide();
                Settle(window);
                edit.Text.Should().Be("Patchs/{origin_folder}{title}");
                edit.IsFocused.Should().BeTrue();
                edit.CaretIndex.Should().Be("Patchs/{origin_folder}".Length);
                vm.Directories.Pattern.Should().Be(Core.Models.SortSettings.DefaultPattern);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task TransferModes_ShouldBeAListThatSaysWhatEachModeDoes()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.CurrentStep = WorkflowStep.Output;
                Settle(window);
                window.FindControl<Border>("CardWriting")!.GetVisualDescendants().OfType<RadioButton>()
                    .Should().BeEmpty("the modes are a list, not radio buttons");
                var combo = window.FindControl<ComboBox>("ComboTransferMode")!;
                var hint = window.FindControl<TextBlock>("LblTransferModeHint")!;
                hint.Text.Should().Be(vm.Localization.Tr("tab1.mode_hint_copy"));

                // Each entry: its name, the sentence of what it does, and the details in its tooltip
                vm.Directories.TransferModeOptions.Select(o => o.Value).Should().Equal(Enum.GetValues<Core.Models.TransferMode>());
                var zip = vm.Directories.TransferModeOptions.Single(o => o.Value == Core.Models.TransferMode.Zip);
                zip.Label.Should().Be("ZIP");
                zip.Description.Should().Be(vm.Localization.Tr("tab1.mode_hint_zip"));
                zip.Details.Should().Be(vm.Localization.Tr("tab1.tooltip_mode_zip"));

                combo.IsDropDownOpen = true;
                Settle(window);
                var shown = combo.GetLogicalDescendants().OfType<ComboBoxItem>().Select(i => i.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList()).ToList();
                shown.Should().HaveCount(5);
                shown[4].Should().Equal("ZIP", vm.Localization.Tr("tab1.mode_hint_zip"));
                combo.IsDropDownOpen = false;

                // Choosing a mode in the list sets it, and the sentence under the list follows
                combo.SelectedIndex = 4;
                Settle(window);
                vm.Directories.SelectedTransferMode.Should().Be(Core.Models.TransferMode.Zip);
                hint.Text.Should().Be(vm.Localization.Tr("tab1.mode_hint_zip"));
                vm.Directories.IsTransferMove = true;
                Settle(window);
                combo.SelectedItem.Should().Be(vm.Directories.TransferModeOptions[1]);

                // The closed list shows the name only; the sentence sits under it, aligned with it
                combo.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text)
                    .Should().NotContain(vm.Localization.Tr("tab1.mode_hint_move"));
                BoundsIn(window, hint).Top.Should().BeGreaterThanOrEqualTo(BoundsIn(window, combo).Bottom - 1);
                BoundsIn(window, hint).Left.Should().BeApproximately(BoundsIn(window, combo).Left, 1);

                // Translated with the interface
                vm.UiLocale = "en";
                vm.Directories.TransferModeOptions[1].Label.Should().Be("Move");
                vm.Directories.TransferModeOptions[1].Description.Should().Be("Files leave the source (instant on the same volume).");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The texts of the pattern area and the hints of the transfer modes are not cut, in every language. Measured
    /// with the real fonts only (ROMSIEVE_HEADLESS_SKIA=1).
    /// </summary>
    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("es")]
    public async Task PatternArea_Texts_ShouldNotBeCutInAnyLanguage(string locale)
    {
        if (Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") != "1")
        {
            return;
        }

        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.UiLocale = locale;
            vm.Directories.SourceDir = Path.Combine(_tempDir, "no_such_source");
            vm.Directories.PatchDir = Path.Combine(_tempDir, "patches");
            vm.Navigation.CurrentStep = WorkflowStep.Output;
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                foreach (var mode in Enum.GetValues<Core.Models.TransferMode>())
                {
                    vm.Directories.SelectedTransferMode = mode;
                    foreach (var flat in new[] { false, true })
                    {
                        vm.Directories.FlatDestination = flat;
                        vm.Directories.Pattern = flat ? Core.Models.SortSettings.DefaultPattern : "{title}/{unknown_variable}/{";
                        // A refused pattern of the patched versions adds the longest warning
                        vm.Directories.PatchedPattern = flat ? "{origin_folder}/Patchs" : "_purged/{title}";
                        vm.Directories.Preview.Refresh();
                        Settle(window);
                        AssertNotCut(window, locale, "ComboTransferMode", "LblTransferModeHint", "BtnInsertVariable", "BtnPatternPresets",
                            "LblPreviewCaption", "LblPreviewWarning", "LblFlatDestinationNote", "ListPatternPreview", "RadioLayoutPattern", "RadioLayoutRoot",
                            "BtnInsertPatchedVariable", "BtnPatchedPatternPresets", "ChkFolderPerDat", "ChkDatFolderMirror");
                    }
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Each control ends inside its column (the right column of the tab), and, unless it wraps on purpose, is as
    /// wide as its content.
    /// </summary>
    private static void AssertNotCut(Window window, string locale, params string[] names)
    {
        var tabRight = BoundsIn(window, window.FindControl<Panel>("StepContent")!).Right;
        foreach (var name in names)
        {
            var control = window.FindControl<Control>(name)!;
            if (!control.IsEffectivelyVisible)
            {
                continue;
            }

            BoundsIn(window, control).Right.Should().BeLessThanOrEqualTo(tabRight, $"{name} fits in the tab ({locale})");
            if (control is TextBlock { TextWrapping: TextWrapping.Wrap } or ItemsControl)
            {
                continue;
            }

            // The desired size counts the margin, which the bounds leave out
            control.Measure(Size.Infinity);
            (control.DesiredSize.Width - control.Margin.Left - control.Margin.Right)
                .Should().BeLessThanOrEqualTo(control.Bounds.Width + 1, $"{name} is not cut ({locale})");
            control.InvalidateMeasure();
        }
    }
}
