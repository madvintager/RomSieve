using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using RomSieve.App.ViewModels;
using RomSieve.App.Views;
using RomSieve.Core.Models;

namespace RomSieve.App.Tests;

/// <summary>
/// Convention of the MAME software lists in the real window: the Filters page shows the No-Intro / Redump options with
/// what a software never carries greyed and the MAME card reduced to its title, and the panel of the order of the
/// criteria offers the order of the software among the others, on one line that fits in every language.
/// </summary>
public sealed partial class GuiTests
{
    [Fact]
    public async Task FiltersTab_WithASoftwareList_ShouldGreyWhatASoftwareNeverCarries()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Directories.SelectedNamingConvention = NamingConvention.MameSoftwareList;
                vm.Navigation.CurrentStep = WorkflowStep.Filters;
                Settle(window);

                window.FindControl<RadioButton>("RadioFiltersNoIntro")!.IsChecked.Should().BeTrue();
                ContentText(window.FindControl<RadioButton>("RadioFiltersNoIntro")!).Should().Be(vm.Localization.Tr("filters.convention_nointro"));

                // The MAME card stays in sight, reduced to its title and a sentence that says it is for the machines
                window.FindControl<Border>("GrpFilterMame")!.IsEffectivelyVisible.Should().BeTrue();
                window.FindControl<StackPanel>("PanelMameBody")!.IsEffectivelyVisible.Should().BeFalse();

                var noEffect = window.FindControl<TextBlock>("LblDumpsNoEffect")!;
                noEffect.IsEffectivelyVisible.Should().BeTrue();
                noEffect.Text.Should().Be(vm.Localization.Tr("filters.dumps_no_effect_softlist"));
                window.FindControl<StackPanel>("GrpDumpQuality")!.IsEnabled.Should().BeFalse();

                var unlicensed = window.FindControl<CheckBox>("ChkKeepUnl")!;
                unlicensed.IsEffectivelyVisible.Should().BeTrue();
                unlicensed.IsEnabled.Should().BeFalse();
                ToolTip.GetTip(unlicensed).Should().Be(vm.Localization.Tr("filters.tooltip_no_effect_softlist_category"));
                window.FindControl<CheckBox>("ChkKeepTranslations")!.IsEnabled.Should().BeTrue();
                window.FindControl<CheckBox>("ChkKeepPirate")!.IsEnabled.Should().BeTrue();

                // An incomplete software is left out by the option of the multi-disc group
                window.FindControl<CheckBox>("ChkCompleteSetsOnly")!.IsEffectivelyVisible.Should().BeTrue();
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task BrowseDatButton_ShouldOfferADatFileOrAFolderOfDats()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.CurrentStep = WorkflowStep.Collection;
                Settle(window);
                var button = window.FindControl<Button>("BtnBrowseDat")!;
                button.Flyout.Should().BeOfType<MenuFlyout>();
                button.Flyout!.ShowAt(button);
                Settle(window);

                var items = ((MenuFlyout)button.Flyout).Items.OfType<MenuItem>().ToList();
                items.Select(i => i.Name).Should().Equal("MenuBrowseDatFile", "MenuBrowseDatFolder");
                items[0].Header.Should().Be(vm.Localization.Tr("tab1.menu_browse_dat_file"));
                items[0].Command.Should().BeSameAs(vm.Directories.BrowseDatCommand);
                items[1].Header.Should().Be(vm.Localization.Tr("tab1.menu_browse_dat_folder"));
                items[1].Command.Should().BeSameAs(vm.Directories.BrowseDatFolderCommand);
                button.Flyout.Hide();
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task TiebreakOrderPanel_ShouldOfferTheOrderOfTheSoftwareLists()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.Priorities.OneGameOneRom = true;
            var window = new MainWindow(vm);
            window.Show();
            vm.Navigation.CurrentStep = WorkflowStep.Languages;
            var button = window.FindControl<Button>("BtnTiebreakOrder")!;
            try
            {
                vm.Priorities.ShowConvention(NamingConvention.MameSoftwareList, DatCloneRelations.None);
                button.Flyout!.ShowAt(button);
                Settle(window);

                var radio = window.FindControl<RadioButton>("RadioTiebreakSoftwareList")!;
                radio.IsChecked.Should().BeTrue();
                var list = window.FindControl<ListBox>("ListTiebreakOrder")!;
                list.ItemCount.Should().Be(TiebreakOrder.CriteriaOf(NamingConvention.MameSoftwareList).Count);

                // Without parents in the DAT (a SabreTools DAT), the parent / clone criterion is greyed
                vm.Priorities.SoftwareListCriteriaOrder.Items.Single(i => i.Criterion == TiebreakCriterion.ParentClone)
                    .HasNoEffect.Should().BeTrue();

                window.FindControl<RadioButton>("RadioTiebreakMame")!.IsChecked = true;
                Settle(window);
                vm.Priorities.ShownCriteriaOrder.Should().BeSameAs(vm.Priorities.MameCriteriaOrder);
                radio.IsChecked = true;
                Settle(window);
                vm.Priorities.ShownCriteriaOrder.Should().BeSameAs(vm.Priorities.SoftwareListCriteriaOrder);
            }
            finally
            {
                button.Flyout?.Hide();
                window.Close();
            }
        });
    }

    /// <summary>
    /// The five conventions of "Settings for:" hold on one line of the panel, none of them cut, in every language.
    /// Measured with the real fonts only (ROMSIEVE_HEADLESS_SKIA=1).
    /// </summary>
    [Fact]
    public async Task TiebreakOrderPanel_Conventions_ShouldFitOnTheirLineInEveryLanguage()
    {
        if (Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") != "1")
        {
            return;
        }

        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.Priorities.OneGameOneRom = true;
            var window = new MainWindow(vm);
            window.Show();
            vm.Navigation.CurrentStep = WorkflowStep.Languages;
            var button = window.FindControl<Button>("BtnTiebreakOrder")!;
            try
            {
                foreach (var locale in vm.Localization.GetAvailableLocales().Select(l => l.Code))
                {
                    vm.UiLocale = locale;
                    button.Flyout!.ShowAt(button);
                    Settle(window);

                    var panel = window.FindControl<Grid>("GrpTiebreakOrder")!;
                    var row = window.FindControl<StackPanel>("PanelTiebreakConvention")!;
                    var panelRight = panel.TranslatePoint(new Point(panel.Bounds.Width, 0), window)!.Value.X;
                    foreach (var radio in row.GetVisualChildren().OfType<RadioButton>())
                    {
                        var end = radio.TranslatePoint(new Point(radio.Bounds.Width, 0), window)!.Value.X;
                        end.Should().BeLessThanOrEqualTo(panelRight + 1, $"{radio.Name} fits in the panel ({locale})");
                        radio.Measure(Size.Infinity);
                        radio.DesiredSize.Width.Should().BeLessThanOrEqualTo(radio.Bounds.Width + 1, $"{radio.Name} is not cut ({locale})");
                        radio.InvalidateMeasure();
                    }

                    button.Flyout.Hide();
                    Settle(window);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }
}
