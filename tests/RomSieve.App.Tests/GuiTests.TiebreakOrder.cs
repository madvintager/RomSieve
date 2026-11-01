using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Headless;
using Avalonia.VisualTree;
using RomSieve.App.ViewModels;
using RomSieve.App.Views;
using RomSieve.Core.Models;

namespace RomSieve.App.Tests;

/// <summary>Order of the 1G1R criteria in the real window: a drop-down panel that leaves the priority lists their width.</summary>
public sealed partial class GuiTests
{
    [Fact]
    public async Task TiebreakOrderPanel_ShouldShowTheCriteriaAndFollowThe1G1ROption()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow { DataContext = vm };
            window.Show();
            vm.Navigation.CurrentStep = WorkflowStep.Languages;
            Settle(window);

            var button = window.FindControl<Button>("BtnTiebreakOrder")!;
            button.IsEffectivelyVisible.Should().BeTrue("the 1G1R card always shows its settings");
            button.IsEffectivelyEnabled.Should().BeFalse("the order only applies to the 1G1R");

            vm.Priorities.OneGameOneRom = true;
            Settle(window);
            button.IsEffectivelyEnabled.Should().BeTrue();

            button.Flyout!.ShowAt(button);
            Settle(window);
            var list = window.FindControl<ListBox>("ListTiebreakOrder")!;
            list.ItemCount.Should().Be(TiebreakOrder.CriteriaOf(NamingConvention.Tosec).Count);
            window.FindControl<RadioButton>("RadioTiebreakTosec")!.IsChecked.Should().BeTrue();

            list.SelectedIndex = 5;
            Settle(window);
            vm.Priorities.TosecCriteriaOrder.SelectedItem!.Criterion.Should().Be(TiebreakCriterion.CountryRank);
            window.FindControl<Button>("BtnTiebreakMoveUp")!.Command!.Execute(null);
            Settle(window);
            vm.Priorities.TosecCriteriaOrder.Order[4].Should().Be(TiebreakCriterion.CountryRank);

            // Another convention shows its own order in the same list
            window.FindControl<RadioButton>("RadioTiebreakMame")!.IsChecked = true;
            Settle(window);
            vm.Priorities.ShownCriteriaOrder.Should().BeSameAs(vm.Priorities.MameCriteriaOrder);
            list.ItemCount.Should().Be(TiebreakOrder.CriteriaOf(NamingConvention.Mame).Count);

            button.Flyout.Hide();
            window.Close();
        });
    }

    [Fact]
    public async Task TiebreakOrderPanel_ShouldGreyOutTheCriteriaWithoutEffectOnTheCollection()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.Priorities.OneGameOneRom = true;
            var window = new MainWindow { DataContext = vm };
            window.Show();
            vm.Navigation.CurrentStep = WorkflowStep.Languages;
            vm.Priorities.ShowConvention(NamingConvention.NoIntro, DatCloneRelations.Present);
            Settle(window);

            var button = window.FindControl<Button>("BtnTiebreakOrder")!;
            button.Flyout!.ShowAt(button);
            Settle(window);
            var list = window.FindControl<ListBox>("ListTiebreakOrder")!;
            var texts = list.GetRealizedContainers()
                .Select(c => ((ListBoxItem)c).GetVisualDescendants().OfType<TextBlock>().First())
                .ToDictionary(t => ((TiebreakCriterionItemViewModel)t.DataContext!).Criterion);

            // Without a preferred video standard, its criterion separates nothing
            texts[TiebreakCriterion.VideoStandard].Classes.Should().Contain("no-effect");
            ToolTip.GetTip((Grid)texts[TiebreakCriterion.VideoStandard].Parent!).Should().Be(
                vm.I18n["tiebreak.tooltip_video"] + "\n" + vm.I18n["tiebreak.no_effect_no_preference"]);
            texts[TiebreakCriterion.LanguageRank].Classes.Should().NotContain("no-effect");
            texts.Should().NotContainKey(TiebreakCriterion.Date, "No-Intro names carry no date: the order of No-Intro has no date criterion");
            window.FindControl<TextBlock>("LblTiebreakNoEffectNote")!.Opacity.Should().Be(1);

            // A preference chosen on the line of the criterion gives it an effect
            var preference = list.GetRealizedContainers()
                .Select(c => ((ListBoxItem)c).GetVisualDescendants().OfType<ComboBox>().Single(b => b.Name == "ComboTiebreakPreference"))
                .Single(b => ((TiebreakCriterionItemViewModel)b.DataContext!).Criterion == TiebreakCriterion.VideoStandard);
            preference.IsEffectivelyVisible.Should().BeTrue();
            preference.SelectedIndex = 1;
            Settle(window);
            vm.Priorities.PreferredVideo.Should().Be(VideoPreference.Pal);
            texts[TiebreakCriterion.VideoStandard].Classes.Should().NotContain("no-effect");
            window.FindControl<TextBlock>("LblTiebreakNoEffectNote")!.Opacity.Should().Be(0, "the note keeps its room without being seen");

            // The criteria without a preference have no list on their line
            list.GetRealizedContainers()
                .Select(c => ((ListBoxItem)c).GetVisualDescendants().OfType<ComboBox>().Single(b => b.Name == "ComboTiebreakPreference"))
                .Where(b => b.IsVisible)
                .Select(b => ((TiebreakCriterionItemViewModel)b.DataContext!).Criterion)
                .Should().BeEquivalentTo([TiebreakCriterion.VideoStandard, TiebreakCriterion.LanguageCount]);

            // A collection of unknown convention sorted for an AGA Amiga, without a DAT relation known yet: nothing is
            // greyed out
            vm.TosecFilters.AmigaTarget = AmigaTarget.Aga;
            vm.Priorities.ShowConvention(NamingConvention.Auto, DatCloneRelations.Unknown);
            Settle(window);
            texts[TiebreakCriterion.ParentClone].Classes.Should().NotContain("no-effect");
            window.FindControl<TextBlock>("LblTiebreakNoEffectNote")!.Opacity.Should().Be(0, "the note keeps its room without being seen");

            button.Flyout.Hide();
            window.Close();
        });
    }

    [Fact]
    public async Task TiebreakOrderPanel_ShouldKeepItsSizeWhateverTheOrderShown()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.Priorities.OneGameOneRom = true;
            var window = new MainWindow(vm);
            window.Show();
            vm.Navigation.CurrentStep = WorkflowStep.Languages;

            // Without a DAT, the parent / clone criterion is greyed out in some orders only, and the sentence that names
            // the criteria of the other conventions is longer for some of them
            vm.Priorities.ShowConvention(NamingConvention.Auto, DatCloneRelations.NoDat);
            var button = window.FindControl<Button>("BtnTiebreakOrder")!;
            button.Flyout!.ShowAt(button);
            Settle(window);
            var panel = window.FindControl<Grid>("GrpTiebreakOrder")!;
            var sizes = new List<(string Case, Size Size)>();
            foreach (var decisions in new[] { false, true })
            {
                vm.Priorities.ShowDecisions(decisions ? new Dictionary<TiebreakCriterion, int> { [TiebreakCriterion.LanguageRank] = 3 } : null);
                foreach (var convention in TiebreakOrder.Conventions)
                {
                    vm.Priorities.ShownOrderConvention = convention;
                    Settle(window);
                    sizes.Add(($"{convention}, decisions: {decisions}", panel.Bounds.Size));
                }
            }

            sizes.Select(s => s.Size).Distinct().Should().ContainSingle(
                "the panel keeps its size when another order is shown ({0})", string.Join("; ", sizes.Select(s => $"{s.Case} = {s.Size}")));

            // The sentences that do not apply are kept in their place, without being seen
            var leftOut = panel.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "LblTiebreakLeftOut").ToList();
            leftOut.Should().HaveCount(TiebreakOrder.Conventions.Count);
            leftOut.Where(t => t.Opacity > 0).Should().ContainSingle()
                .Which.Text.Should().Be(vm.Priorities.ShownCriteriaOrder.LeftOutText);

            button.Flyout.Hide();
            window.Close();
        });
    }

    [Fact]
    public async Task TiebreakOrderPanel_ShouldShowTheGamesEachCriterionDecided()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.Priorities.OneGameOneRom = true;
            var window = new MainWindow { DataContext = vm };
            window.Show();
            vm.Navigation.CurrentStep = WorkflowStep.Languages;
            Settle(window);

            var button = window.FindControl<Button>("BtnTiebreakOrder")!;
            button.Flyout!.ShowAt(button);
            Settle(window);
            var hint = window.FindControl<TextBlock>("LblTiebreakDecisionsHint")!;
            var note = window.FindControl<TextBlock>("LblTiebreakDecisionsNote")!;
            hint.Opacity.Should().Be(1, "without a simulation, the panel says how to get the counts");
            note.Opacity.Should().Be(0);

            vm.Priorities.ShowDecisions(new Dictionary<TiebreakCriterion, int> { [TiebreakCriterion.LanguageRank] = 132 });
            Settle(window);

            var list = window.FindControl<ListBox>("ListTiebreakOrder")!;
            var counts = list.GetRealizedContainers()
                .Select(c => ((ListBoxItem)c).GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "LblDecidedGames"))
                .ToDictionary(t => ((TiebreakCriterionItemViewModel)t.DataContext!).Criterion);
            counts[TiebreakCriterion.LanguageRank].IsEffectivelyVisible.Should().BeTrue();
            counts[TiebreakCriterion.LanguageRank].Text.Should().Be("132");
            counts[TiebreakCriterion.LanguageRank].Classes.Should().NotContain("decided-nothing");
            counts[TiebreakCriterion.Version].Text.Should().Be("0");
            counts[TiebreakCriterion.Version].Classes.Should().Contain("decided-nothing");
            note.Opacity.Should().Be(1);
            hint.Opacity.Should().Be(0);

            button.Flyout.Hide();
            window.Close();
        });
    }

    [Fact]
    public async Task TiebreakOrderPanel_ClickOnACount_ShouldShowTheDecidedGamesInTheResults()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.Priorities.OneGameOneRom = true;
            vm.Priorities.ShowDecisions(new Dictionary<TiebreakCriterion, int> { [TiebreakCriterion.LanguageRank] = 12 });
            var window = new MainWindow(vm);
            window.Show();
            vm.Navigation.CurrentStep = WorkflowStep.Languages;
            Settle(window);
            var orderButton = window.FindControl<Button>("BtnTiebreakOrder")!;
            orderButton.Flyout!.ShowAt(orderButton);
            Settle(window);

            var countButtons = window.FindControl<ListBox>("ListTiebreakOrder")!.GetRealizedContainers()
                .Select(c => c.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "BtnDecidedGames"))
                .ToDictionary(b => ((TiebreakCriterionItemViewModel)b.DataContext!).Criterion);
            countButtons[TiebreakCriterion.Version].IsEffectivelyEnabled.Should().BeFalse("the criterion decided no game");
            var languageCount = countButtons[TiebreakCriterion.LanguageRank];
            languageCount.IsEffectivelyEnabled.Should().BeTrue();
            ToolTip.GetTip(languageCount).Should().Be(vm.I18n["tab3.tooltip_tiebreak_show_games"]);

            languageCount.Bounds.Height.Should().BeGreaterThanOrEqualTo(16, "the whole height of the row is clickable");

            // A real click: its handler closing the panel before the command ran left the click without effect
            var center = languageCount.TranslatePoint(new Point(languageCount.Bounds.Width / 2, languageCount.Bounds.Height / 2), window)!.Value;
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
            Settle(window);

            orderButton.Flyout.IsOpen.Should().BeFalse("the panel closes with the page it opened from");
            vm.Navigation.CurrentStep.Should().Be(WorkflowStep.Results);
            var band = window.FindControl<Border>("PanelCriterionFilter")!;
            band.IsEffectivelyVisible.Should().BeTrue();
            window.FindControl<TextBlock>("LblCriterionFilter")!.Text.Should().Be(vm.PreviewTable.CriterionFilterText);

            window.FindControl<Button>("BtnClearCriterionFilter")!.Command!.Execute(null);
            Settle(window);
            band.IsVisible.Should().BeFalse();

            window.Close();
        });
    }

    /// <summary>
    /// The names of the criteria keep their whole width next to a count of four digits, in every language and in both
    /// orders. Measured with the real fonts only (ROMSIEVE_HEADLESS_SKIA=1).
    /// </summary>
    [Fact]
    public async Task TiebreakOrderPanel_CriteriaWithTheirCounts_ShouldNotBeTrimmed()
    {
        if (Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") != "1")
        {
            return;
        }

        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.Priorities.OneGameOneRom = true;
            vm.Priorities.ShowDecisions(Enum.GetValues<TiebreakCriterion>().ToDictionary(c => c, _ => 8888));

            // The longest choices of the lists set on the lines of their criteria
            vm.Priorities.PreferredVideo = VideoPreference.Ntsc;
            vm.TosecFilters.PreferredMediaType = MediaPreference.Hd;
            var window = new MainWindow(vm);
            window.Show();
            vm.Navigation.CurrentStep = WorkflowStep.Languages;
            var button = window.FindControl<Button>("BtnTiebreakOrder")!;
            try
            {
                foreach (var locale in vm.Localization.GetAvailableLocales().Select(l => l.Code))
                {
                    vm.UiLocale = locale;
                    foreach (var convention in TiebreakOrder.Conventions)
                    {
                        vm.Priorities.ShowConvention(convention, DatCloneRelations.Present);
                        button.Flyout!.ShowAt(button);
                        Settle(window);
                        var list = window.FindControl<ListBox>("ListTiebreakOrder")!;
                        list.ItemCount.Should().Be(TiebreakOrder.CriteriaOf(convention).Count);
                        for (var index = 0; index < list.ItemCount; index++)
                        {
                            list.ScrollIntoView(index);
                            Settle(window);
                            var container = list.ContainerFromIndex(index)!;
                            var name = container.GetVisualDescendants().OfType<TextBlock>().First();
                            AssertNotTrimmed(name, $"\"{name.Text}\" ({locale}, {convention})");

                            var preference = container.GetVisualDescendants().OfType<ComboBox>().Single(b => b.Name == "ComboTiebreakPreference");
                            if (preference.IsVisible)
                            {
                                foreach (var choice in preference.GetVisualDescendants().OfType<TextBlock>().Where(t => !string.IsNullOrEmpty(t.Text)))
                                {
                                    AssertNotTrimmed(choice, $"\"{choice.Text}\" on the line of \"{name.Text}\" ({locale})");
                                }
                            }
                        }

                        button.Flyout.Hide();
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
    public async Task PriorityChips_AtTheDefaultWindowWidth_ShouldWrapWithinTheirColumn()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.Priorities.Apply1G1RPreset("france");
            // A long list of countries takes several lines of chips, never a horizontal scroll
            vm.Priorities.CountryPriority.SetPriorityCodes(
                ["FR", "EU", "WO", "US", "GB", "CA", "AU", "JP", "DE", "ES", "IT", "NL", "SE", "BR", "KR", "AS", "CN", "PT"]);
            var window = new MainWindow { DataContext = vm, Width = vm.WindowWidth };
            window.Show();
            vm.Navigation.CurrentStep = WorkflowStep.Languages;
            Settle(window);

            foreach (var name in new[] { "ChipsLanguages", "ChipsCountries" })
            {
                var chips = window.FindControl<ItemsControl>(name)!;
                chips.Bounds.Width.Should().BeGreaterThan(300, $"{name} takes the width of its column");
                var area = BoundsIn(window, chips);
                var chipBoxes = chips.GetRealizedContainers().Select(c => BoundsIn(window, c)).ToList();
                chipBoxes.Should().HaveCount(chips.ItemCount);
                chipBoxes.Should().OnlyContain(b => b.Right <= area.Right + 0.5, $"the chips of {name} wrap inside their column");
            }

            var countryRows = window.FindControl<ItemsControl>("ChipsCountries")!.GetRealizedContainers()
                .Select(c => Math.Round(BoundsIn(window, c).Top)).Distinct().Count();
            countryRows.Should().BeGreaterThan(1);

            window.Close();
        });
    }
}
