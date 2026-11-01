using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using RomSieve.App.ViewModels;
using RomSieve.App.Views;
using RomSieve.Core.Models;

namespace RomSieve.App.Tests;

/// <summary>
/// Languages, regions and 1G1R tab in the real window: every 1G1R option in one place, no arbitration order selector,
/// chips of the language and country lists (remove, context menu, keyboard, search list).
/// </summary>
public sealed partial class GuiTests
{
    /// <summary>The chips of a list in their order (the realized containers keep their creation order after a move).</summary>
    private static List<Border> Chips(Window window, string listName)
    {
        var list = window.FindControl<ItemsControl>(listName)!;
        return list.GetRealizedContainers()
            .OrderBy(list.IndexFromContainer)
            .Select(c => c.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("priority-chip")))
            .ToList();
    }

    [Fact]
    public async Task OneGameOneRomOptions_ShouldAllBeInTheLanguagesTab()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.CurrentStep = WorkflowStep.Languages;
                vm.Priorities.OneGameOneRom = false;
                Settle(window);

                // No arbitration order selector any more, and the TOSEC options left the filters tab
                window.FindControl<ComboBox>("ComboPriorityMode").Should().BeNull();
                window.FindControl<StackPanel>("GrpFilter1G1R").Should().BeNull();

                var languagesTab = window.FindControl<CheckBox>("ChkOneGameOneRom")!.FindLogicalAncestorOfType<TabItem>();
                var ignore = window.FindControl<CheckBox>("ChkIgnoreModifications")!;
                var controls = new Control[] { window.FindControl<Button>("BtnTiebreakOrder")!, ignore };
                // Turned off, the 1G1R card keeps its settings in sight, greyed out, and says what is kept instead
                var card = window.FindControl<Border>("Card1G1R")!;
                var hint = window.FindControl<TextBlock>("LblOneGameOneRomHint")!;
                hint.Text.Should().StartWith("Désactivé : toutes les versions qui passent les filtres sont gardées.");
                foreach (var control in controls)
                {
                    control.FindLogicalAncestorOfType<TabItem>().Should().BeSameAs(languagesTab, $"{control.Name} is a 1G1R option");
                    control.GetLogicalAncestors().Should().Contain(card, $"{control.Name} is in the 1G1R card");
                    control.IsEffectivelyVisible.Should().BeTrue($"{control.Name} stays in sight");
                    control.IsEffectivelyEnabled.Should().BeFalse($"{control.Name} only applies to the 1G1R");
                }

                // Their labels are greyed out with them
                var label = window.FindControl<Grid>("Grp1G1ROptions")!.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("form-label"));
                var muted = (ISolidColorBrush)window.FindResource(window.ActualThemeVariant, "TextMutedBrush")!;
                ((ISolidColorBrush)label.Foreground!).Color.Should().Be(muted.Color);

                // The TOSEC-only option is under a title that says so
                window.FindControl<StackPanel>("Grp1G1RTosecOptions")!.Children.OfType<TextBlock>().First().Text.Should().Be("Noms TOSEC");
                ToolTip.GetTip(ignore)!.ToString().Should().Contain("TOSEC");

                // Turning it on changes nothing in the layout of the page
                var chips = window.FindControl<ItemsControl>("ChipsLanguages")!.FindAncestorOfType<ScrollViewer>()!;
                var chipsHeight = chips.Bounds.Height;
                var cardBounds = card.Bounds;
                vm.Priorities.OneGameOneRom = true;
                Settle(window);
                hint.Text.Should().StartWith("Garde la meilleure version de chaque jeu");
                card.Bounds.Should().Be(cardBounds);
                ((ISolidColorBrush)label.Foreground!).Color.Should().NotBe(muted.Color, "turned on, the labels are read as usual");
                chips.Bounds.Height.Should().Be(chipsHeight, "the chips keep their room");
                controls.Should().OnlyContain(c => c.IsEffectivelyVisible && c.IsEffectivelyEnabled);
                ignore.IsChecked = true;
                vm.TosecFilters.IgnoreModificationsInPriority.Should().BeTrue();

                // The TOSEC options only apply while the convention is TOSEC or still unknown: otherwise they stay in sight,
                // greyed, a sentence saying why, so that the page keeps its layout
                var tosecOptions = window.FindControl<StackPanel>("Grp1G1RTosecOptions")!;
                var tosecGrid = window.FindControl<Grid>("GridTosecOptions")!;
                var inactive = window.FindControl<TextBlock>("LblTosecOptionsInactive")!;
                inactive.IsEffectivelyVisible.Should().BeFalse();
                vm.Directories.SelectedNamingConvention = NamingConvention.NoIntro;
                Settle(window);
                tosecOptions.IsEffectivelyVisible.Should().BeTrue();
                tosecGrid.IsEffectivelyEnabled.Should().BeFalse("No-Intro names have no TOSEC tags");
                inactive.IsEffectivelyVisible.Should().BeTrue();
                vm.TosecFilters.IgnoreModificationsInPriority.Should().BeTrue("greyed, the option keeps its value");
                vm.Directories.SelectedNamingConvention = NamingConvention.Tosec;
                Settle(window);
                tosecGrid.IsEffectivelyEnabled.Should().BeTrue();
                inactive.IsEffectivelyVisible.Should().BeFalse();

                // The order of the criteria says which of the language and the country comes first
                var summary = window.FindControl<TextBlock>("LblRegionModeSummary")!;
                summary.Text.Should().Be("La langue l'emporte sur le pays.");
                vm.Priorities.TosecCriteriaOrder.SetOrder(TiebreakOrder.Default(NamingConvention.Tosec, RegionPriorityMode.CountryFirst), notify: true);
                Settle(window);
                vm.Priorities.RegionPriorityMode.Should().Be(RegionPriorityMode.CountryFirst);
                summary.Text.Should().Be("Le pays l'emporte sur la langue.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task PriorityChips_ShouldBeRemovedAndReorderedFromTheChip()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.Priorities.Apply1G1RPreset("france");
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.CurrentStep = WorkflowStep.Languages;
                Settle(window);
                var languages = vm.Priorities.LanguagePriority;

                var chips = Chips(window, "ChipsLanguages");
                chips.Should().HaveCount(3);
                ToolTip.GetTip(chips[0])!.ToString().Should().Contain("Glissez");
                chips[0].GetVisualDescendants().OfType<TextBlock>().First().Text.Should().Be("1");

                // The cross removes the chip
                var remove = chips[2].GetVisualDescendants().OfType<Button>().Single();
                ToolTip.GetTip(remove)!.ToString().Should().Be("Retirer de la liste");
                remove.Command!.Execute(null);
                Settle(window);
                languages.GetPriorityCodes().Should().Equal("fr", "m");
                Chips(window, "ChipsLanguages").Should().HaveCount(2);

                // The context menu moves it, with translated entries
                chips = Chips(window, "ChipsLanguages");
                var menu = chips[0].ContextMenu!;
                menu.Open(chips[0]);
                Settle(window);
                var entries = menu.Items.OfType<MenuItem>().ToList();
                entries.Select(m => m.Header).Should().Equal("En premier", "Monter", "Descendre", "Retirer");
                entries[1].IsEnabled.Should().BeFalse("the first chip cannot move up");
                entries[2].Command!.Execute(null);
                menu.Close();
                Settle(window);
                languages.GetPriorityCodes().Should().Equal("m", "fr");

                // Ctrl+arrows on the focused chip, Delete removes it
                chips = Chips(window, "ChipsLanguages");
                chips[1].RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Left, KeyModifiers = KeyModifiers.Control });
                Settle(window);
                languages.GetPriorityCodes().Should().Equal("fr", "m");
                Chips(window, "ChipsLanguages")[1].RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete });
                Settle(window);
                languages.GetPriorityCodes().Should().Equal("fr");
                window.FindControl<TextBlock>("LblLanguagesEmpty")!.IsVisible.Should().BeFalse();

                languages.ResetDefaults();
                Settle(window);
                window.FindControl<TextBlock>("LblLanguagesEmpty")!.IsVisible.Should().BeTrue();

                vm.UiLocale = "en";
                vm.Priorities.Apply1G1RPreset("usa");
                Settle(window);
                chips = Chips(window, "ChipsLanguages");
                menu = chips[0].ContextMenu!;
                menu.Open(chips[0]);
                Settle(window);
                menu.Items.OfType<MenuItem>().Select(m => m.Header).Should().Equal("Move to first", "Move up", "Move down", "Remove");
                menu.Close();
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task AddButton_ShouldSearchAndAddACountry()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.Priorities.CountryPriority.SetPriorityCodes(["FR"]);
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.CurrentStep = WorkflowStep.Languages;
                Settle(window);
                var button = window.FindControl<DropDownButton>("BtnAddCountry")!;
                button.Content.Should().Be("Ajouter un pays");
                button.Flyout!.ShowAt(button);
                Settle(window);

                var search = window.FindControl<TextBox>("EditCountrySearch")!;
                ToolTip.GetTip(search)!.ToString().Should().NotBeNullOrWhiteSpace();
                search.Text = "Japon";
                Settle(window);
                var results = window.FindControl<ItemsControl>("ListAvailableCountries")!;
                results.ItemCount.Should().Be(1);
                var result = results.GetRealizedContainers().Single().GetVisualDescendants().OfType<Button>().Single();
                ContentText(result).Should().Be("Japon JP");
                result.Command!.Execute(null);
                Settle(window);

                vm.Priorities.CountryPriority.GetPriorityCodes().Should().Equal("FR", "JP");
                results.ItemCount.Should().Be(0, "an added country leaves the results");
                button.Flyout.Hide();
                Settle(window);
                Chips(window, "ChipsCountries").Should().HaveCount(2);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The options and chips of the languages tab are not cut, in every language. Measured with the real fonts only
    /// (ROMSIEVE_HEADLESS_SKIA=1).
    /// </summary>
    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("es")]
    public async Task LanguagesTab_Texts_ShouldNotBeCutInAnyLanguage(string locale)
    {
        if (Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") != "1")
        {
            return;
        }

        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.UiLocale = locale;
            vm.Priorities.Apply1G1RPreset("europe");
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.CurrentStep = WorkflowStep.Languages;
                Settle(window);
                var tabs = window.FindControl<Panel>("StepContent")!;
                var right = tabs.TranslatePoint(new Point(tabs.Bounds.Width, 0), window)!.Value.X;
                var controls = new[]
                    {
                        "ChkOneGameOneRom", "BtnTiebreakOrder", "LblRegionModeSummary",
                        "ChkIgnoreModifications", "ChkFilterLanguages", "ChkPriorityFallback", "ChkFilterCountries",
                        "ChkCountryFallback", "BtnAddLanguage", "BtnLangReset", "BtnAddCountry", "BtnCountryReset"
                    }
                    .Select(n => window.FindControl<Control>(n)!)
                    .Concat(Chips(window, "ChipsLanguages"))
                    .Concat(Chips(window, "ChipsCountries"));
                foreach (var control in controls)
                {
                    var name = control.Name ?? (control.DataContext as PriorityItemViewModel)?.Code;
                    var end = control.TranslatePoint(new Point(control.Bounds.Width, 0), window)!.Value.X;
                    end.Should().BeLessThanOrEqualTo(right, $"{name} fits in the tab ({locale})");

                    // The desired size includes the margin, which the bounds leave out
                    control.Measure(Size.Infinity);
                    var desired = control.DesiredSize.Width - control.Margin.Left - control.Margin.Right;
                    desired.Should().BeLessThanOrEqualTo(control.Bounds.Width + 1, $"{name} is not cut ({locale})");
                    control.InvalidateMeasure();
                }
            }
            finally
            {
                window.Close();
            }
        });
    }
}
