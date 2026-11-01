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

                // The variables of TOSEC names follow the effective convention
                window.FindControl<MenuItem>("MenuVariablePublisher")!.IsVisible.Should().BeTrue();
                vm.Directories.SelectedNamingConvention = Core.Models.NamingConvention.NoIntro;
                insert.Flyout.ShowAt(insert);
                Settle(window);
                window.FindControl<MenuItem>("MenuVariablePublisher")!.IsVisible.Should().BeFalse();
                window.FindControl<MenuItem>("MenuVariableCountry")!.IsVisible.Should().BeTrue();
                insert.Flyout.Hide();
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
                        vm.Directories.Preview.Refresh();
                        Settle(window);
                        AssertNotCut(window, locale, "ComboTransferMode", "LblTransferModeHint", "BtnInsertVariable", "BtnPatternPresets",
                            "LblPreviewCaption", "LblPreviewWarning", "LblFlatDestinationNote", "ListPatternPreview", "RadioLayoutPattern", "RadioLayoutRoot");
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
