using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.VisualTree;
using RomSieve.App.ViewModels;
using RomSieve.App.Views;
using RomSieve.Core.Models;

namespace RomSieve.App.Tests;

/// <summary>
/// Filters tab in the real window: convention selector, shared categories with the tag of the shown convention,
/// options of one convention only, dump quality level and its detail in a drop-down panel.
/// </summary>
public sealed partial class GuiTests
{
    /// <summary>Text of a check box or radio button, whose label may be a text block of runs (label and tag).</summary>
    private static string? ContentText(ContentControl control) => control.Content switch
    {
        string text => text,
        TextBlock { Inlines.Count: > 0 } block => string.Concat(block.Inlines.OfType<Run>().Select(r => r.Text)),
        TextBlock block => block.Text,
        _ => control.Content?.ToString()
    };

    /// <summary>
    /// Amiga the collection is sorted for: a list of the TOSEC options, under the multi-disc group, with a sentence that
    /// says what the chosen machine excludes; the No-Intro / Redump options do not show it.
    /// </summary>
    [Fact]
    public async Task FiltersTab_AmigaMachine_ShouldBeATosecOption()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.CurrentStep = WorkflowStep.Filters;
                Settle(window);

                var combo = window.FindControl<ComboBox>("ComboAmigaTarget")!;
                var hint = window.FindControl<TextBlock>("LblAmigaTargetHint")!;
                combo.IsEffectivelyVisible.Should().BeTrue();
                combo.ItemCount.Should().Be(4);
                combo.SelectedIndex.Should().Be(0, "every machine by default");
                hint.Text.Should().Be(vm.Localization.Tr("filters.amiga_hint_any"));

                combo.SelectedIndex = 3;
                Settle(window);
                vm.TosecFilters.AmigaTarget.Should().Be(AmigaTarget.Aga);
                hint.Text.Should().Be(vm.Localization.Tr("filters.amiga_hint_aga"));

                vm.TosecFilters.AmigaTarget = AmigaTarget.Ocs;
                Settle(window);
                combo.SelectedIndex.Should().Be(1);

                vm.Filters.IsNoIntroShown = true;
                Settle(window);
                combo.IsEffectivelyVisible.Should().BeFalse("a TOSEC option");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The tag after the label of a box ("(Translated)") is lighter than the label while the box is enabled, and greyed
    /// out with it when it is disabled: a color set on the tag itself kept it readable next to a greyed-out label.
    /// </summary>
    [Fact]
    public async Task FiltersTab_TagOfADisabledBox_ShouldBeGreyedOutWithItsLabel()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Directories.SelectedNamingConvention = NamingConvention.Mame;
                vm.Navigation.CurrentStep = WorkflowStep.Filters;
                Settle(window);
                window.TryFindResource("HintForegroundBrush", window.ActualThemeVariant, out var hint).Should().BeTrue();

                static (Run Label, Run Tag) Runs(CheckBox box)
                {
                    var runs = ((TextBlock)box.Content!).Inlines!.OfType<Run>().ToList();
                    return (runs[0], runs[^1]);
                }

                // Translations: a MAME set is never one, so the box is greyed out
                var translations = window.FindControl<CheckBox>("ChkKeepTranslations")!;
                translations.IsEffectivelyEnabled.Should().BeFalse();
                var (label, tag) = Runs(translations);
                tag.Text.Should().Be("(Translated)");
                tag.Foreground.Should().BeSameAs(label.Foreground);
                tag.Foreground.Should().NotBeSameAs(hint);

                var demos = window.FindControl<CheckBox>("ChkKeepDemos")!;
                demos.IsEffectivelyEnabled.Should().BeTrue();
                (label, tag) = Runs(demos);
                tag.Foreground.Should().BeSameAs(hint);
                label.Foreground.Should().NotBeSameAs(hint);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task FiltersTab_ShouldShowTheOptionsOfTheShownConvention()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.CurrentStep = WorkflowStep.Filters;
                Settle(window);

                var tosec = window.FindControl<RadioButton>("RadioFiltersTosec")!;
                var noIntro = window.FindControl<RadioButton>("RadioFiltersNoIntro")!;
                var translations = window.FindControl<CheckBox>("ChkKeepTranslations")!;
                var pd = window.FindControl<CheckBox>("ChkKeepPd")!;
                var pirate = window.FindControl<CheckBox>("ChkKeepPirate")!;
                var smart = window.FindControl<CheckBox>("ChkSmartMultidisk")!;

                tosec.IsChecked.Should().BeTrue();
                // Worded as what to keep: a checked box keeps its category
                window.FindControl<TextBlock>("LblFilterContent")!.Text.Should().Be("Contenu à garder");
                ContentText(translations).Should().Be("Traductions [tr]");
                translations.IsChecked.Should().BeTrue("translations are kept by default");
                var modifications = window.FindControl<CheckBox>("ChkKeepModifications")!;
                ContentText(modifications).Should().Be("Versions modifiées [cr] [t] [h] [p] [f] [m]");
                modifications.IsChecked.Should().BeFalse("modified versions are excluded by default");
                pd.IsEffectivelyVisible.Should().BeTrue();
                smart.IsEffectivelyVisible.Should().BeTrue();
                pirate.IsEffectivelyVisible.Should().BeFalse();

                // A shared category acts on the option of the shown convention
                translations.IsChecked = false;
                vm.TosecFilters.ExcludeTranslations.Should().BeTrue();
                vm.NoIntroFilters.ExcludeTranslations.Should().BeFalse();

                noIntro.IsChecked = true;
                Settle(window);
                vm.Filters.IsNoIntroShown.Should().BeTrue();
                tosec.IsChecked.Should().BeFalse();
                translations.IsChecked.Should().BeTrue();
                ContentText(translations).Should().Be("Traductions (Translated)");
                ToolTip.GetTip(translations).Should().Be(vm.Localization.Tr("filters.tooltip_keep_nointro_translations"));
                pirate.IsEffectivelyVisible.Should().BeTrue();
                pirate.IsChecked = false;
                vm.NoIntroFilters.ExcludePirate.Should().BeTrue();
                window.FindControl<CheckBox>("ChkKeepRereleases")!.IsEffectivelyVisible.Should().BeTrue();
                pd.IsEffectivelyVisible.Should().BeFalse();
                smart.IsEffectivelyVisible.Should().BeFalse();

                // The MAME card is always there with the No-Intro / Redump options, reduced to its title and a sentence
                // saying when it applies; its options only come with the MAME convention
                var mameGroup = window.FindControl<Border>("GrpFilterMame")!;
                var mameBody = window.FindControl<StackPanel>("PanelMameBody")!;
                var multidisk = window.FindControl<StackPanel>("ColFilterMultidisk")!;
                mameGroup.IsEffectivelyVisible.Should().BeTrue("the MAME machines are known before a MAME DAT is chosen");
                mameBody.IsEffectivelyVisible.Should().BeFalse();
                mameGroup.Classes.Should().NotContain("mame", "not in the colours of the MAME convention while it is not in use");
                window.FindControl<TextBlock>("LblFilterMame")!.Inlines!.OfType<Run>().Last().Text
                    .Should().Be(vm.Localization.Tr("filters.mame_card_inactive_hint"));
                multidisk.IsEffectivelyVisible.Should().BeTrue();
                noIntro.Content.Should().Be("No-Intro / Redump");
                vm.Directories.SelectedNamingConvention = NamingConvention.Mame;
                Settle(window);
                noIntro.IsChecked.Should().BeTrue();
                noIntro.Content.Should().Be("No-Intro / Redump / MAME", "the selector says the MAME sets use these options");
                mameGroup.IsEffectivelyVisible.Should().BeTrue();
                mameBody.IsEffectivelyVisible.Should().BeTrue();
                mameGroup.Classes.Should().Contain("mame");
                multidisk.IsEffectivelyVisible.Should().BeFalse("a MAME run has no multi-disc games, its complete sets option is in the card");

                // The dump quality has no effect on a MAME set: greyed, a sentence says why, the alternate sets are in the card
                var dumps = window.FindControl<StackPanel>("ColFilterDumps")!;
                var contentColumn = window.FindControl<StackPanel>("ColFilterContent")!;
                dumps.IsEffectivelyVisible.Should().BeTrue();
                window.FindControl<StackPanel>("GrpDumpQuality")!.IsEffectivelyEnabled.Should().BeFalse();
                window.FindControl<TextBlock>("LblDumpsNoEffect")!.IsEffectivelyVisible.Should().BeTrue();
                Grid.GetColumn(contentColumn).Should().Be(2);
                Grid.GetColumnSpan(contentColumn).Should().Be(3, "the content to keep takes the room of the multi-disc group");
                var keepAlternates = window.FindControl<CheckBox>("ChkKeepMameAlternateSets")!;
                keepAlternates.IsChecked.Should().BeTrue("every machine is kept by default");
                keepAlternates.IsChecked = false;
                vm.NoIntroFilters.ExcludeMameAlternateSets.Should().BeTrue();
                vm.NoIntroFilters.ExcludeAlternate.Should().BeFalse("the No-Intro / Redump alternates do not change");
                keepAlternates.IsChecked = true;

                // The categories a MAME description never carries are greyed, their tooltip saying why; the others name its words
                var unlicensed = window.FindControl<CheckBox>("ChkKeepUnl")!;
                var rereleases = window.FindControl<CheckBox>("ChkKeepRereleases")!;
                foreach (var box in new[] { translations, unlicensed, rereleases })
                {
                    box.IsEffectivelyVisible.Should().BeTrue(box.Name);
                    box.IsEffectivelyEnabled.Should().BeFalse(box.Name);
                    ToolTip.GetShowOnDisabled(box).Should().BeTrue(box.Name);
                    ToolTip.GetTip(box).Should().Be(vm.Localization.Tr("filters.tooltip_no_effect_mame_category"), box.Name);
                }
                pirate.IsEffectivelyVisible.Should().BeTrue();
                ContentText(pirate).Should().Be("Versions pirates (bootleg)");
                ToolTip.GetTip(pirate).Should().Be(vm.Localization.Tr("filters.tooltip_keep_mame_pirate"));
                ((string)ToolTip.GetTip(pirate)!).Should().Contain("(Pirate)", "the setting also applies to No-Intro / Redump");
                var shared = window.FindControl<TextBlock>("LblFilterContentShared")!;
                shared.IsEffectivelyVisible.Should().BeTrue();
                shared.Text.Should().Be("Réglages communs à No-Intro, Redump et MAME");
                ContentText(window.FindControl<CheckBox>("ChkKeepModifications")!).Should().Be("Versions modifiées (hack)");
                ContentText(window.FindControl<CheckBox>("ChkKeepDev")!).Should().Be("Versions de développement (prototype, beta, location test)");
                ContentText(window.FindControl<CheckBox>("ChkKeepDemos")!).Should().Be("Démos (demo)");

                // Showing the TOSEC options, then the No-Intro / Redump ones again, keeps the MAME presentation
                vm.Filters.IsTosecShown = true;
                Settle(window);
                translations.IsEffectivelyEnabled.Should().BeTrue();
                mameGroup.IsEffectivelyVisible.Should().BeFalse("the TOSEC options are shown: the MAME sets follow the No-Intro / Redump ones");
                vm.Filters.IsNoIntroShown = true;
                Settle(window);
                translations.IsEffectivelyEnabled.Should().BeFalse();
                ContentText(pirate).Should().Be("Versions pirates (bootleg)");
                var content = window.FindControl<WrapPanel>("GrpFilterContent")!;
                mameGroup.TranslatePoint(new Point(0, mameGroup.Bounds.Height), content)!.Value.Y
                    .Should().BeLessThanOrEqualTo(0, "the MAME card comes before the shared options");

                // The complete sets option is in the MAME card, worded for MAME
                var mameComplete = window.FindControl<CheckBox>("ChkMameCompleteSetsOnly")!;
                mameComplete.IsChecked.Should().BeFalse();
                mameComplete.IsChecked = true;
                vm.NoIntroFilters.CompleteMultimediaSetsOnly.Should().BeTrue();
                vm.TosecFilters.CompleteMultidiskSetsOnly.Should().BeFalse();
                mameComplete.IsChecked = false;
                var keepBios = window.FindControl<CheckBox>("ChkKeepMameBios")!;
                keepBios.IsChecked.Should().BeTrue("every machine is kept by default");
                keepBios.IsChecked = false;
                vm.NoIntroFilters.ExcludeMameBios.Should().BeTrue();
                // Emulation: a box per driver status, any combination, and the playable machines
                var good = window.FindControl<CheckBox>("ChkKeepMameDriverGood")!;
                var imperfect = window.FindControl<CheckBox>("ChkKeepMameDriverImperfect")!;
                var preliminary = window.FindControl<CheckBox>("ChkKeepMameDriverPreliminary")!;
                var playable = window.FindControl<CheckBox>("ChkMameKeepPlayable")!;
                var warning = window.FindControl<TextBlock>("LblMameEmulationWarning")!;
                new[] { good, imperfect, preliminary }.Should().OnlyContain(c => c.IsChecked == true, "every machine is kept by default");
                new[] { good, imperfect, preliminary }.Select(c => c.Content).Should().Equal("Bonne", "Imparfaite", "Préliminaire");
                playable.IsEffectivelyEnabled.Should().BeFalse("with every status kept, the playable machines change nothing");
                good.IsChecked = false;
                imperfect.IsChecked = false;
                Settle(window);
                vm.NoIntroFilters.MameDriverStatuses.Should().Be(MameDriverStatuses.Preliminary, "only the machines that do not work yet");
                playable.IsEffectivelyEnabled.Should().BeTrue();
                warning.IsEffectivelyVisible.Should().BeFalse();
                preliminary.IsChecked = false;
                Settle(window);
                warning.IsEffectivelyVisible.Should().BeTrue("no status is kept");
                warning.Text.Should().Be("Aucun statut coché : seules les machines sans statut sont gardées.");
                playable.IsChecked = true;
                Settle(window);
                vm.NoIntroFilters.MameKeepPlayable.Should().BeTrue();
                warning.IsEffectivelyVisible.Should().BeFalse("the playable machines are kept");
                vm.NoIntroFilters.MameDriverStatuses = MameDriverStatuses.Good;
                Settle(window);
                good.IsChecked.Should().BeTrue();
                imperfect.IsChecked.Should().BeFalse();
                var keepSound = window.FindControl<CheckBox>("ChkKeepMameUnemulatedSound")!;
                keepSound.IsChecked.Should().BeTrue("every machine is kept by default");
                keepSound.IsChecked = false;
                vm.NoIntroFilters.ExcludeMameUnemulatedSound.Should().BeTrue();
                var keepArtwork = window.FindControl<CheckBox>("ChkKeepMameRequiresArtwork")!;
                keepArtwork.IsChecked.Should().BeTrue("every machine is kept by default");
                keepArtwork.IsChecked = false;
                vm.NoIntroFilters.ExcludeMameRequiresArtwork.Should().BeTrue();

                // The effective convention picks the shown one
                vm.Directories.SelectedNamingConvention = NamingConvention.Tosec;
                Settle(window);
                tosec.IsChecked.Should().BeTrue();
                translations.IsChecked.Should().BeFalse();
                mameGroup.IsEffectivelyVisible.Should().BeFalse();
                multidisk.IsEffectivelyVisible.Should().BeTrue();
                noIntro.Content.Should().Be("No-Intro / Redump");
                dumps.IsEffectivelyVisible.Should().BeTrue();
                Grid.GetColumn(contentColumn).Should().Be(2);

                // Out of MAME, the No-Intro / Redump options come back with their tags
                vm.Directories.SelectedNamingConvention = NamingConvention.NoIntro;
                Settle(window);
                translations.IsEffectivelyVisible.Should().BeTrue();
                window.FindControl<CheckBox>("ChkKeepUnl")!.IsEffectivelyVisible.Should().BeTrue();
                window.FindControl<CheckBox>("ChkKeepRereleases")!.IsEffectivelyVisible.Should().BeTrue();
                ContentText(pirate).Should().Be("Versions pirates (Pirate)");
                ((string)ToolTip.GetTip(pirate)!).Should().Contain("MAME", "the setting also applies to the MAME sets");
                shared.IsEffectivelyVisible.Should().BeTrue();
                vm.Filters.IsTosecShown = true;
                Settle(window);
                shared.IsEffectivelyVisible.Should().BeFalse("the TOSEC options are their own");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task DumpQualityLevel_ShouldBeOneChoiceWithItsDetailInADropDownPanel()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.WindowHeight = 820;
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.CurrentStep = WorkflowStep.Filters;
                Settle(window);

                var all = window.FindControl<RadioButton>("RadioDumpLevelAll")!;
                var noBad = window.FindControl<RadioButton>("RadioDumpLevelNoBad")!;
                var verified = window.FindControl<RadioButton>("RadioDumpLevelVerified")!;
                var custom = window.FindControl<RadioButton>("RadioDumpLevelCustom")!;
                noBad.IsChecked.Should().BeTrue();
                custom.IsVisible.Should().BeFalse();
                ContentText(verified).Should().Be("Dumps vérifiés uniquement [!]");

                all.IsChecked = true;
                Settle(window);
                vm.TosecFilters.ExcludeBadDumps.Should().BeFalse();
                vm.TosecFilters.ExcludeVirus.Should().BeFalse();
                noBad.IsChecked.Should().BeFalse();

                // The detail opens over the tab, without changing its height
                var tabs = window.FindControl<Panel>("StepContent")!;
                var heightBefore = tabs.Bounds.Height;
                var button = window.FindControl<DropDownButton>("BtnDumpDetails")!;
                button.Flyout!.ShowAt(button);
                Settle(window);
                var excludeBad = window.FindControl<CheckBox>("ChkExcludeBad")!;
                excludeBad.IsEffectivelyVisible.Should().BeTrue();
                ContentText(excludeBad).Should().Be("Exclure les mauvais dumps [b]");
                window.FindControl<CheckBox>("ChkNoIntroExcludeBad")!.IsEffectivelyVisible.Should().BeFalse();
                foreach (var name in new[] { "ChkExcludeBad", "ChkExcludeOverUnder", "ChkExcludeVirus", "ChkExcludeAlt" })
                {
                    ToolTip.GetTip(window.FindControl<CheckBox>(name)!)?.ToString().Should().NotBeNullOrWhiteSpace($"{name} explains itself");
                }

                // A combination that matches no level checks the "Custom" choice, shown for it only
                excludeBad.IsChecked = true;
                Settle(window);
                all.IsChecked.Should().BeFalse();
                noBad.IsChecked.Should().BeFalse();
                verified.IsChecked.Should().BeFalse();
                custom.IsVisible.Should().BeTrue();
                custom.IsChecked.Should().BeTrue();
                custom.Content.Should().Be("Personnalisé");
                ToolTip.GetTip(custom)?.ToString().Should().NotBeNullOrWhiteSpace();
                tabs.Bounds.Height.Should().Be(heightBefore);
                button.Flyout.Hide();
                Settle(window);

                // Verified only greys out the exclusions it implies, and the "Custom" choice goes away
                verified.IsChecked = true;
                Settle(window);
                vm.TosecFilters.DumpsVerifiedOnly.Should().BeTrue();
                custom.IsVisible.Should().BeFalse();
                custom.IsChecked.Should().BeFalse();
                button.Flyout.ShowAt(button);
                Settle(window);
                excludeBad.IsEnabled.Should().BeFalse();
                button.Flyout.Hide();
                Settle(window);
                tabs.Bounds.Height.Should().Be(heightBefore);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task VerifiedOnlyLevel_ShouldOnlyBeShownForTosec()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.CurrentStep = WorkflowStep.Filters;
                var verified = window.FindControl<RadioButton>("RadioDumpLevelVerified")!;
                verified.IsEffectivelyVisible.Should().BeTrue("a TOSEC name marks its verified dumps with [!]");
                ToolTip.GetTip(verified).Should().Be(vm.Localization.Tr("tab2.tooltip_verified_only"));

                // A No-Intro / Redump name has no such mark: the level would keep the same files as "no bad dumps"
                vm.Filters.ShownConventionIndex = FiltersViewModel.NoIntroConventionIndex;
                Settle(window);
                verified.IsEffectivelyVisible.Should().BeFalse();
                window.FindControl<RadioButton>("RadioDumpLevelNoBad")!.IsChecked.Should().BeTrue();

                vm.Filters.IsTosecShown = true;
                Settle(window);
                verified.IsEffectivelyVisible.Should().BeTrue();
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The labels of the filters tab are not cut and its columns do not overlap, in every language and for both
    /// conventions. Measured with the real fonts only (ROMSIEVE_HEADLESS_SKIA=1).
    /// </summary>
    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("es")]
    public async Task FiltersTab_Texts_ShouldNotBeCutInAnyLanguage(string locale)
    {
        if (Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") != "1")
        {
            return;
        }

        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.UiLocale = locale;
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.CurrentStep = WorkflowStep.Filters;

                // TOSEC, No-Intro / Redump, then MAME (the No-Intro / Redump options and those of MAME machines), then
                // the MAME software lists (the No-Intro / Redump options, some greyed)
                var conventions = new Action[]
                {
                    () => vm.Filters.ShownConventionIndex = FiltersViewModel.TosecConventionIndex,
                    () => vm.Filters.ShownConventionIndex = FiltersViewModel.NoIntroConventionIndex,
                    () => vm.Directories.SelectedNamingConvention = NamingConvention.Mame,
                    () => vm.Directories.SelectedNamingConvention = NamingConvention.MameSoftwareList
                };
                foreach (var showConvention in conventions)
                {
                    showConvention();
                    Settle(window);
                    var tabs = window.FindControl<Panel>("StepContent")!;
                    var right = tabs.TranslatePoint(new Point(tabs.Bounds.Width, 0), window)!.Value.X;
                    var names = new[]
                    {
                        "RadioFiltersTosec", "RadioFiltersNoIntro", "RadioDumpLevelAll", "RadioDumpLevelNoBad",
                        "RadioDumpLevelVerified", "BtnDumpDetails", "ChkKeepModifications", "ChkKeepPirate",
                        "ChkKeepTranslations", "ChkKeepDev", "ChkKeepDemos", "ChkKeepPd", "ChkKeepUnl",
                        "ChkKeepRereleases", "ChkCompleteSetsOnly", "ChkSmartMultidisk",
                        "ChkKeepMameBios", "ChkKeepMameDevices", "ChkKeepMameMechanical", "ChkKeepMameRequiresArtwork",
                        "ChkKeepMameUnemulatedSound", "ChkKeepMameAlternateSets", "ChkKeepMameNothingToDump", "ChkKeepMameDriverGood", "ChkKeepMameDriverImperfect",
                        "ChkKeepMameDriverPreliminary", "ChkMameKeepPlayable", "ChkMameCompleteSetsOnly",
                        "BtnMameCabinet", "BtnMameControls", "BtnMameCategories", "BtnMameRatings", "BtnMameBudget",
                        "LblFilterContentShared", "ComboAmigaTarget"
                    };
                    foreach (var control in names.Select(n => window.FindControl<Control>(n)!).Where(c => c.IsEffectivelyVisible))
                    {
                        var end = control.TranslatePoint(new Point(control.Bounds.Width, 0), window)!.Value.X;
                        end.Should().BeLessThanOrEqualTo(right, $"{control.Name} fits in the tab ({locale})");

                        // A label that wraps keeps its full text; any other control is as wide as its content
                        if (control is ContentControl { Content: TextBlock { TextWrapping: TextWrapping.Wrap } })
                        {
                            continue;
                        }

                        // The desired size counts the margin, which the bounds leave out
                        control.Measure(Size.Infinity);
                        (control.DesiredSize.Width - control.Margin.Left - control.Margin.Right)
                            .Should().BeLessThanOrEqualTo(control.Bounds.Width + 1, $"{control.Name} is not cut ({locale})");
                        control.InvalidateMeasure();
                    }
                }

                // The finer MAME filters, in the drop-down panels of the "Refine" row
                var panels = new Dictionary<string, string[]>
                {
                    ["BtnMameCabinet"] = ["ComboMameOrientation", "ChkKeepMameVector", "ChkKeepMameLcd", "ComboMamePlayers", "ComboMameButtons"],
                    ["BtnMameCategories"] = ["ChkKeepMameMature", "ChkKeepMameNonArcade"],
                    ["BtnMameRatings"] = ["ComboMameMinimumRating", "ChkKeepMameUnrated"],
                    ["BtnMameBudget"] = ["ChkMameBudget", "NumMameBudget"]
                };
                foreach (var (buttonName, names) in panels)
                {
                    var button = window.FindControl<DropDownButton>(buttonName)!;
                    button.Flyout!.ShowAt(button);
                    Settle(window);
                    foreach (var control in names.Select(n => window.FindControl<Control>(n)!))
                    {
                        if (control is ContentControl { Content: TextBlock { TextWrapping: TextWrapping.Wrap } })
                        {
                            continue;
                        }

                        // The desired size counts the margin, which the bounds leave out
                        control.Measure(Size.Infinity);
                        (control.DesiredSize.Width - control.Margin.Left - control.Margin.Right)
                            .Should().BeLessThanOrEqualTo(control.Bounds.Width + 1, $"{control.Name} is not cut ({locale})");
                        control.InvalidateMeasure();
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

    /// <summary>
    /// Ratings and size budget of the MAME machines: the rating controls wait for a rating file, the budget turns on
    /// with the size of a common memory card and can be changed in its numeric field.
    /// </summary>
    [Fact]
    public async Task MameRatings_ShouldSetTheMinimumRatingAndTheBudget()
    {
        await RunOnUiThreadAsync(async () =>
        {
            var file = Path.Combine(_tempDir, "bestgames.ini");
            await File.WriteAllTextAsync(file, string.Join(Environment.NewLine, "[90 to 100 (Best Games)]", "sf2", "[80 to 90 (Very Good)]", "19xx"));
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Directories.SelectedNamingConvention = NamingConvention.Mame;
                vm.Navigation.CurrentStep = WorkflowStep.Filters;
                Settle(window);
                var ratingsButton = window.FindControl<DropDownButton>("BtnMameRatings")!;
                ratingsButton.IsEnabled.Should().BeFalse("without a rating file, there is nothing to filter");
                ratingsButton.Content.Should().Be("Notes : aucun fichier");
                ((string)ToolTip.GetTip(ratingsButton)!).Should().StartWith("Choisissez un fichier de notes (bestgames.ini) à l'étape Collection.");

                vm.Directories.MameRatingFile = file;
                await vm.Filters.Ratings.LoadAsync(file);
                Settle(window);
                ratingsButton.IsEnabled.Should().BeTrue();
                ratingsButton.Content.Should().Be("Notes : toutes");
                ratingsButton.Classes.Should().NotContain("changed");
                ratingsButton.Flyout!.ShowAt(ratingsButton);
                Settle(window);
                window.FindControl<TextBlock>("LblMameRatingsStatus")!.Text.Should().Be("2 machines notées.");
                var rating = window.FindControl<ComboBox>("ComboMameMinimumRating")!;
                var keepUnrated = window.FindControl<CheckBox>("ChkKeepMameUnrated")!;
                ((SelectOptionItem<int>)rating.SelectedItem!).Label.Should().Be("Toutes");
                rating.SelectedIndex = 3;
                Settle(window);
                ratingsButton.Content.Should().Be("Notes : 70 et plus", "the button names its only restriction");
                keepUnrated.IsChecked = false;
                Settle(window);
                vm.NoIntroFilters.MameMinimumRating.Should().Be(70);
                vm.NoIntroFilters.ExcludeMameUnrated.Should().BeTrue();
                ratingsButton.Content.Should().Be("Notes : 2 restrictions");
                ratingsButton.Classes.Should().Contain("changed", "a button whose panel excludes machines is emphasized");
                ((string)ToolTip.GetTip(ratingsButton)!).Should().EndWith("Restrictions : 70 et plus, sans jeux non notés");
                ratingsButton.Flyout.Hide();
                Settle(window);

                var budgetButton = window.FindControl<DropDownButton>("BtnMameBudget")!;
                budgetButton.Content.Should().Be("Volume : sans limite");
                budgetButton.Flyout!.ShowAt(budgetButton);
                Settle(window);
                var budget = window.FindControl<CheckBox>("ChkMameBudget")!;
                var size = window.FindControl<NumericUpDown>("NumMameBudget")!;
                budget.IsChecked.Should().BeFalse();
                size.IsEnabled.Should().BeFalse();
                budget.IsChecked = true;
                Settle(window);
                size.IsEnabled.Should().BeTrue();
                size.Value.Should().Be(64m);
                size.Value = 32.5m;
                Settle(window);
                vm.NoIntroFilters.MameSizeBudgetGb.Should().Be(32.5);
                budgetButton.Content.Should().Be("Volume : 32.5 Go au plus");
                budgetButton.Classes.Should().Contain("changed");
                budgetButton.Flyout.Hide();
                Settle(window);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Categories of the MAME machines: the category file is chosen in the Collection step, then the categories button of
    /// the MAME card opens a drop-down panel with the adults, the non-arcade machines and the genres of the file.
    /// </summary>
    [Fact]
    public async Task MameCategories_ShouldListTheGenresOfTheChosenFile()
    {
        await RunOnUiThreadAsync(async () =>
        {
            var file = Path.Combine(_tempDir, "catlist.ini");
            await File.WriteAllTextAsync(file, string.Join(Environment.NewLine,
                "[Arcade: Fighter / Versus]", "sf2", "[Arcade: Casino / Cards * Mature *]", "bikkuri", "[Computer / Home System]", "amiga"));
            var dialogs = new ConfigurableDialogService { OnPickOpenFile = (_, _, _) => file };
            var vm = CreateViewModel(dialogs);
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Directories.SelectedNamingConvention = NamingConvention.Mame;
                vm.Navigation.CurrentStep = WorkflowStep.Filters;
                Settle(window);
                var categoriesButton = window.FindControl<DropDownButton>("BtnMameCategories")!;
                categoriesButton.IsEnabled.Should().BeFalse("without a category file, there is nothing to filter");
                categoriesButton.Content.Should().Be("Catégories : aucun fichier");
                ((string)ToolTip.GetTip(categoriesButton)!)
                    .Should().StartWith("Choisissez un fichier de catégories (catver.ini, catlist.ini) à l'étape Collection.");

                // Chosen in the Collection step, the file is read off the UI thread
                vm.Navigation.CurrentStep = WorkflowStep.Collection;
                Settle(window);
                window.FindControl<Button>("BtnBrowseMameCategoryFile")!.Command!.Execute(null);
                vm.Directories.MameCategoryFile.Should().Be(file);
                await vm.Filters.Categories.LoadAsync(file);
                vm.Navigation.CurrentStep = WorkflowStep.Filters;
                Settle(window);

                categoriesButton.IsEnabled.Should().BeTrue();
                categoriesButton.Content.Should().Be("Catégories : toutes");
                categoriesButton.Flyout!.ShowAt(categoriesButton);
                Settle(window);
                var keepMature = window.FindControl<CheckBox>("ChkKeepMameMature")!;
                var keepNonArcade = window.FindControl<CheckBox>("ChkKeepMameNonArcade")!;
                keepMature.IsEnabled.Should().BeTrue();
                keepNonArcade.IsEnabled.Should().BeTrue("catlist.ini marks the arcade machines");
                window.FindControl<TextBlock>("LblMameCategoriesStatus")!.Text.Should().Be("3 machines classées en 3 genres.");
                keepMature.IsChecked = false;
                Settle(window);
                categoriesButton.Content.Should().Be("Catégories : sans machines pour adultes");
                keepNonArcade.IsChecked = false;
                vm.NoIntroFilters.ExcludeMameMature.Should().BeTrue();
                vm.NoIntroFilters.MameArcadeOnly.Should().BeTrue();

                var checkBoxes = window.FindControl<ItemsControl>("ListMameGenres")!
                    .GetVisualDescendants().OfType<CheckBox>().ToList();
                checkBoxes.Select(c => c.Content).Should().Equal("Casino", "Computer", "Fighter");
                checkBoxes[0].IsChecked = false;
                Settle(window);
                vm.NoIntroFilters.MameExcludedGenres.Should().Equal("Casino");
                categoriesButton.Content.Should().Be("Catégories : 3 restrictions");
                ((string)ToolTip.GetTip(categoriesButton)!)
                    .Should().EndWith("Restrictions : sans machines pour adultes, arcade uniquement, 2 genres sur 3");

                // Every genre at once: to keep only a few, uncheck them all, then check those to keep
                var keepAll = window.FindControl<Button>("BtnMameGenresKeepAll")!;
                var excludeAll = window.FindControl<Button>("BtnMameGenresExcludeAll")!;
                keepAll.Content.Should().Be("Tout cocher");
                excludeAll.Content.Should().Be("Tout décocher");
                excludeAll.Command!.Execute(null);
                Settle(window);
                checkBoxes.Should().OnlyContain(c => c.IsChecked == false);
                vm.NoIntroFilters.MameExcludedGenres.Should().Equal("Casino", "Computer", "Fighter");
                excludeAll.IsEffectivelyEnabled.Should().BeFalse("no genre is left to uncheck");
                keepAll.Command!.Execute(null);
                Settle(window);
                checkBoxes.Should().OnlyContain(c => c.IsChecked == true);
                vm.NoIntroFilters.MameExcludedGenres.Should().BeEmpty();
                keepAll.IsEffectivelyEnabled.Should().BeFalse("every genre is kept");

                // A file that does not mark the arcade machines greys the arcade filter out, and the panel says why
                var arcadeMissing = window.FindControl<TextBlock>("LblMameArcadeFilterMissing")!;
                arcadeMissing.IsVisible.Should().BeFalse();
                var catver = Path.Combine(_tempDir, "catver.ini");
                await File.WriteAllTextAsync(catver, "[Category]\nsf2=Fighter / Versus\n");
                await vm.Filters.Categories.LoadAsync(catver);
                Settle(window);
                keepNonArcade.IsEnabled.Should().BeFalse();
                ToolTip.GetShowOnDisabled(keepNonArcade).Should().BeTrue("the tooltip tells which file the filter needs");
                arcadeMissing.IsVisible.Should().BeTrue();
                arcadeMissing.Text.Should().StartWith("Ce fichier ne distingue pas l'arcade du reste");
                categoriesButton.Flyout.Hide();
                Settle(window);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Cabinet of the MAME machines, in the "Refine" row of the MAME card: screen, players and buttons in the drop-down
    /// panel of the cabinet button, whose text names its restrictions, and the controls of the panel in another one; both
    /// open over the page.
    /// </summary>
    [Fact]
    public async Task MameCabinet_ShouldWriteItsOptions()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.CurrentStep = WorkflowStep.Filters;
                vm.Directories.SelectedNamingConvention = NamingConvention.Mame;
                Settle(window);
                var tabs = window.FindControl<Panel>("StepContent")!;
                var heightBefore = tabs.Bounds.Height;
                var cabinetButton = window.FindControl<DropDownButton>("BtnMameCabinet")!;
                cabinetButton.IsEffectivelyVisible.Should().BeTrue();
                cabinetButton.Content.Should().Be("Borne : toutes", "every machine is kept by default");
                cabinetButton.Flyout!.ShowAt(cabinetButton);
                Settle(window);

                var orientation = window.FindControl<ComboBox>("ComboMameOrientation")!;
                ((SelectOptionItem<MameOrientationRequirement>)orientation.SelectedItem!).Label.Should().Be("Toutes orientations");
                orientation.SelectedIndex = 2;
                Settle(window);
                vm.NoIntroFilters.MameOrientation.Should().Be(MameOrientationRequirement.Vertical);
                cabinetButton.Content.Should().Be("Borne : écran vertical", "the button names its only restriction");

                var keepVector = window.FindControl<CheckBox>("ChkKeepMameVector")!;
                keepVector.IsChecked.Should().BeTrue();
                keepVector.IsChecked = false;
                vm.NoIntroFilters.ExcludeMameVector.Should().BeTrue();

                var players = window.FindControl<ComboBox>("ComboMamePlayers")!;
                var buttons = window.FindControl<ComboBox>("ComboMameButtons")!;
                players.SelectedIndex.Should().Be(0);
                ((SelectOptionItem<int>)players.SelectedItem!).Label.Should().Be("Tous");
                players.SelectedIndex = 3;
                buttons.SelectedIndex = 5;
                Settle(window);
                vm.NoIntroFilters.MameMinimumPlayers.Should().Be(4);
                vm.NoIntroFilters.MameMaximumButtons.Should().Be(6);
                cabinetButton.Content.Should().Be("Borne : 4 restrictions");
                cabinetButton.Classes.Should().Contain("changed");
                ((string)ToolTip.GetTip(cabinetButton)!)
                    .Should().EndWith("Restrictions : écran vertical, sans écran vectoriel, 4 joueurs ou plus, 6 boutons au plus");
                cabinetButton.Flyout.Hide();
                Settle(window);

                // The controls open over the page, without changing its height
                var controlsButton = window.FindControl<DropDownButton>("BtnMameControls")!;
                controlsButton.Content.Should().Be("Commandes : toutes");
                controlsButton.Flyout!.ShowAt(controlsButton);
                Settle(window);
                var checkBoxes = window.FindControl<ItemsControl>("ListMameControls")!
                    .GetVisualDescendants().OfType<CheckBox>().ToList();
                checkBoxes.Select(c => c.Content).Should().HaveCount(9).And.Contain("Pistolet");
                checkBoxes.Should().OnlyContain(c => c.IsChecked == true);
                checkBoxes.Single(c => Equals(c.Content, "Pistolet")).IsChecked = false;
                Settle(window);
                vm.NoIntroFilters.MameExcludedControls.Should().Be(MameControls.Lightgun);
                controlsButton.Content.Should().Be("Commandes : 8 sur 9");
                controlsButton.Classes.Should().Contain("changed");
                ((string)ToolTip.GetTip(controlsButton)!).Should().EndWith("Absentes du panneau : Pistolet");
                tabs.Bounds.Height.Should().Be(heightBefore);
                controlsButton.Flyout.Hide();
                Settle(window);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// "No bad dumps" chosen, then an exclusion changed in the detail: a choice stays checked ("Custom"), and putting the
    /// exclusion back checks "No bad dumps" again.
    /// </summary>
    [Fact]
    public async Task DumpQualityLevel_ChangedInTheDetail_ShouldAlwaysKeepAChoiceChecked()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.CurrentStep = WorkflowStep.Filters;
                Settle(window);
                var choices = new[] { "RadioDumpLevelAll", "RadioDumpLevelNoBad", "RadioDumpLevelVerified", "RadioDumpLevelCustom" }
                    .Select(name => window.FindControl<RadioButton>(name)!)
                    .ToList();
                string CheckedChoice() => choices.Single(c => c.IsChecked == true && c.IsEffectivelyVisible).Name!;

                choices[1].IsChecked = true;
                Settle(window);
                CheckedChoice().Should().Be("RadioDumpLevelNoBad");

                vm.TosecFilters.ExcludeAlternate = true;
                Settle(window);
                CheckedChoice().Should().Be("RadioDumpLevelCustom");

                // Clicking the checked "Custom" choice changes nothing
                choices[3].IsChecked = true;
                Settle(window);
                vm.TosecFilters.ExcludeAlternate.Should().BeTrue();

                vm.TosecFilters.ExcludeAlternate = false;
                Settle(window);
                CheckedChoice().Should().Be("RadioDumpLevelNoBad");
                choices[3].IsVisible.Should().BeFalse();
            }
            finally
            {
                window.Close();
            }
        });
    }
}
