using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using RomSieve.App.ViewModels;
using RomSieve.App.Views;
using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.App.Tests;

/// <summary>
/// Layout of the steps: each page shows its whole content at the default size of the window and keeps it within its
/// width at the smallest size; the navigation and the footer are never cut.
/// </summary>
public sealed partial class GuiTests
{
    private static readonly WorkflowStep[] SettingsSteps =
        [WorkflowStep.Collection, WorkflowStep.Filters, WorkflowStep.Languages, WorkflowStep.Output];

    private static string PageName(WorkflowStep step) => $"Page{step}";

    /// <summary>
    /// With the MAME convention, the Filters step shows the MAME card above the shared options: the finer MAME filters
    /// open in drop-down panels, so that the whole page needs no scrolling at the default size, in any language.
    /// </summary>
    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("es")]
    public async Task FiltersStep_WithMame_ShouldNeedNoScrolling(string locale)
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.UiLocale = locale;
            var window = new MainWindow(vm) { Width = 1280, Height = 700 };
            window.Show();
            try
            {
                vm.Directories.SelectedNamingConvention = NamingConvention.Mame;
                vm.Navigation.CurrentStep = WorkflowStep.Filters;

                // With its longest content: the sentence saying no mechanical machine of the DAT reaches the emulation
                var catalog = new DatCatalog();
                catalog.AddGame(new DatGame
                {
                    Name = "3lilpigs",
                    IsMechanical = true,
                    DriverStatus = MameDriverStatus.Preliminary,
                    Roms = [new DatRomEntry("3lilpigs.bin", 16, "12345678")]
                });
                vm.Filters.ShowMameDat(MameDatSummary.FromCatalog(catalog));
                vm.Filters.KeepMameMechanical = true;
                vm.Filters.KeepMameDriverPreliminary = false;
                Settle(window);

                var page = window.FindControl<Control>("PageFilters")!;
                window.FindControl<Border>("GrpFilterMame")!.IsEffectivelyVisible.Should().BeTrue();
                window.FindControl<TextBlock>("LblMameEmulationWarning")!.IsEffectivelyVisible.Should().BeTrue();
                var scroller = page.GetVisualDescendants().OfType<ScrollViewer>().First();
                scroller.Extent.Width.Should().BeLessThanOrEqualTo(scroller.Viewport.Width + 1, "the page never scrolls sideways");

                // The height depends on how many boxes of the content group share a row: measured with the real fonts
                // only, the stand-in font of the session putting about one box per row
                if (Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") == "1")
                {
                    scroller.Extent.Height.Should().BeLessThanOrEqualTo(scroller.Viewport.Height + 1, $"the MAME filters need no scrolling ({locale})");
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// With the MAME convention, the Collection step also offers the folder of the CHDs: it fits in the page, which needs
    /// no scrolling at the default size, and its label is not cut in any language (measured with Skia only).
    /// </summary>
    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("es")]
    public async Task CollectionStep_WithMame_ShouldShowTheChdFolderAndTheCategoryFileWithoutCuttingThem(string locale)
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.UiLocale = locale;
            var window = new MainWindow(vm) { Width = 1280, Height = 700 };
            window.Show();
            try
            {
                vm.Navigation.CurrentStep = WorkflowStep.Collection;
                Settle(window);
                // The MAME files are always in sight; a sentence says they are only read with a MAME DAT
                window.FindControl<TextBox>("EditChdDir")!.IsEffectivelyVisible.Should().BeTrue();
                window.FindControl<TextBlock>("LblMameFilesInactive")!.IsEffectivelyVisible.Should().BeTrue();

                vm.Directories.SelectedNamingConvention = NamingConvention.Mame;
                Settle(window);
                window.FindControl<TextBlock>("LblMameFilesInactive")!.IsEffectivelyVisible.Should().BeFalse();
                window.FindControl<Border>("CardMameFiles")!.Classes.Should().Contain("mame", "in the colours of the MAME convention");

                var page = window.FindControl<Control>("PageCollection")!;
                var pageRight = BoundsIn(window, page).Right;
                foreach (var name in new[]
                {
                    "LblChdDir", "EditChdDir", "BtnBrowseChdDir",
                    "LblMameCategoryFile", "EditMameCategoryFile", "BtnBrowseMameCategoryFile",
                    "LblMameRatingFile", "EditMameRatingFile", "BtnBrowseMameRatingFile"
                })
                {
                    var control = window.FindControl<Control>(name)!;
                    control.IsEffectivelyVisible.Should().BeTrue(name);
                    BoundsIn(window, control).Right.Should().BeLessThanOrEqualTo(pageRight + 1, $"{name} fits ({locale})");
                }

                var scroller = page.GetVisualDescendants().OfType<ScrollViewer>().First();
                scroller.Extent.Height.Should().BeLessThanOrEqualTo(scroller.Viewport.Height + 1, "the step needs no scrolling at the default size");

                if (Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") == "1")
                {
                    foreach (var label in new[] { "LblChdDir", "LblMameCategoryFile", "LblMameRatingFile" }.Select(n => window.FindControl<TextBlock>(n)!))
                    {
                        label.Measure(Size.Infinity);
                        label.DesiredSize.Width.Should().BeLessThanOrEqualTo(label.Bounds.Width + 1, $"{label.Name} is not cut ({locale})");
                    }
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(1280, 700, true)]
    [InlineData(960, 580, false)]
    public async Task EachSettingsStep_ShouldShowItsContentWithoutCuttingIt(int width, int height, bool isDefaultSize)
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            // With the 1G1R on, its card shows all its settings (the TOSEC ones too, the convention being unknown)
            vm.Priorities.OneGameOneRom = true;
            var window = new MainWindow(vm) { Width = width, Height = height };
            window.Show();
            try
            {
                foreach (var step in SettingsSteps)
                {
                    vm.Navigation.CurrentStep = step;
                    Settle(window);

                    var page = window.FindControl<Control>(PageName(step))!;
                    page.IsEffectivelyVisible.Should().BeTrue($"{step} is shown");
                    foreach (var other in Enum.GetValues<WorkflowStep>().Where(s => s != step))
                    {
                        window.FindControl<Control>(PageName(other))!.IsVisible.Should().BeFalse($"only {step} is shown");
                    }

                    // Nothing goes beyond the right edge of the page
                    var pageRight = BoundsIn(window, page).Right;
                    foreach (var control in page.GetLogicalDescendants().OfType<Control>()
                                 .Where(c => !string.IsNullOrEmpty(c.Name) && c.IsEffectivelyVisible && c.Bounds.Width > 0))
                    {
                        BoundsIn(window, control).Right.Should().BeLessThanOrEqualTo(pageRight + 1, $"{control.Name} fits in {step} ({width}×{height})");
                    }

                    if (page.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroller && step != WorkflowStep.Languages)
                    {
                        scroller.Extent.Width.Should().BeLessThanOrEqualTo(scroller.Viewport.Width + 1, $"{step} never scrolls sideways");
                        if (isDefaultSize)
                        {
                            scroller.Extent.Height.Should().BeLessThanOrEqualTo(scroller.Viewport.Height + 1, $"{step} needs no scrolling at the default size");
                        }
                    }
                }

                // The chips of the languages step take the height left
                vm.Navigation.CurrentStep = WorkflowStep.Languages;
                Settle(window);
                window.FindControl<ItemsControl>("ChipsLanguages")!.FindAncestorOfType<ScrollViewer>()!.Bounds.Height
                    .Should().BeGreaterThan(40, "the chips keep room to be read and reordered");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ResultsStep_ShouldGiveTheTableTheFullHeight()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm) { Width = 960, Height = 580 };
            window.Show();
            try
            {
                vm.Navigation.ShowResults();
                Settle(window);

                window.FindControl<Control>("PageResults")!.IsEffectivelyVisible.Should().BeTrue();
                window.FindControl<Control>("PageCollection")!.IsVisible.Should().BeFalse();
                window.FindControl<DataGrid>("TablePreview")!.Bounds.Height.Should().BeGreaterThan(200, "the results have the height of the window");

                // The footer stays within the window, whatever the step
                var right = window.ClientSize.Width;
                foreach (var name in new[] { "BtnSimulate", "BtnStart", "BtnUndo", "BtnExportMenu" })
                {
                    BoundsIn(window, window.FindControl<Control>(name)!).Right.Should().BeLessThanOrEqualTo(right, $"{name} is in the window");
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Labels and state lines of the navigation, buttons of the footer and of the results: never cut in any language.
    /// Measured with the real fonts only (ROMSIEVE_HEADLESS_SKIA=1).
    /// </summary>
    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("es")]
    public async Task NavigationAndFooter_ShouldNotBeCut(string locale)
    {
        if (Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") != "1")
        {
            return;
        }

        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.UiLocale = locale;
            // The longest state lines: a source that does not exist, a destination inside it
            vm.Directories.SourceDir = Path.Combine(_tempDir, "missing source");
            vm.Directories.DestinationDir = Path.Combine(_tempDir, "missing source", "sorted");
            var window = new MainWindow(vm) { Width = 960, Height = 580 };
            window.Show();
            try
            {
                foreach (var step in new[] { WorkflowStep.Languages, WorkflowStep.Results })
                {
                    vm.Navigation.CurrentStep = step;
                    Settle(window);

                    var texts = window.FindControl<StackPanel>("NavigationPanel")!.GetVisualDescendants().OfType<TextBlock>()
                        .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))
                        .ToList();
                    texts.Should().NotBeEmpty();
                    foreach (var text in texts)
                    {
                        AssertNotTrimmed(text, $"\"{text.Text}\" ({locale}, {step})");
                    }

                    var buttons = new[] { "BtnSimulate", "BtnStart", "BtnUndo", "BtnExportMenu", "BtnBackToSettings" }
                        .Select(n => window.FindControl<Control>(n)!)
                        .Where(c => c.IsEffectivelyVisible);
                    foreach (var button in buttons)
                    {
                        AssertNotTrimmed(button, $"{button.Name} ({locale}, {step})");
                    }
                }

                // A DAT without a source: the information line of the Collection step
                var dat = Path.Combine(_tempDir, "catalog.dat");
                File.WriteAllText(dat, "clrmamepro ( name \"Test\" )");
                vm.Directories.SourceDir = string.Empty;
                vm.Directories.DatFilePath = dat;
                Settle(window);
                var datOnly = window.FindControl<StackPanel>("NavigationPanel")!.GetVisualDescendants().OfType<TextBlock>()
                    .Single(t => t.IsEffectivelyVisible && t.Text == vm.Navigation.CollectionEntry.IndicatorText);
                vm.Navigation.CollectionEntry.IndicatorTone.Should().Be(StepIndicatorTone.Info);
                AssertNotTrimmed(datOnly, $"\"{datOnly.Text}\" ({locale})");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Every message of the progress row fits its label of fixed width, in every language, with five-digit counts (and
    /// a short file name: a long one is trimmed, whole in the tooltip): a message of the lot of DATs was cut, and the
    /// label tells the step of the run.
    /// </summary>
    [Fact]
    public async Task ProgressRow_StatusMessages_ShouldFitTheirLabel()
    {
        if (Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") != "1")
        {
            return;
        }

        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                Settle(window);
                var label = window.FindControl<TextBlock>("LblStatusMsg")!;
                foreach (var (locale, catalog) in LocalizationService.Translations)
                {
                    foreach (var (key, text) in catalog.Where(e => e.Key.StartsWith("progress.", StringComparison.Ordinal)
                                                                   || e.Key.StartsWith("panel4.status_", StringComparison.Ordinal)))
                    {
                        var message = System.Text.RegularExpressions.Regex.Replace(text, @"\{\w+\}", "99999");
                        var probe = new TextBlock { Text = message, FontSize = label.FontSize, FontFamily = label.FontFamily, FontWeight = label.FontWeight };
                        probe.Measure(Size.Infinity);
                        probe.DesiredSize.Width.Should().BeLessThanOrEqualTo(label.Width, $"\"{message}\" ({locale}, {key}) is not cut");
                    }
                }
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    private static void AssertNotTrimmed(Control control, string because)
    {
        // The desired size includes the margin, which the bounds leave out
        control.Measure(Size.Infinity);
        var desired = control.DesiredSize.Width - control.Margin.Left - control.Margin.Right;
        desired.Should().BeLessThanOrEqualTo(control.Bounds.Width + 1, $"{because} is not cut");
        control.InvalidateMeasure();
    }

    /// <summary>
    /// The Collection step in the order of the need: the source, then the DAT with the convention it detects, then the
    /// optional MAME files with the MAME convention only. The fields of every frame line up.
    /// </summary>
    [Fact]
    public async Task CollectionStep_ShouldGroupItsFieldsInFramesInTheOrderOfTheNeed()
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
                var source = window.FindControl<Border>("CardSource")!;
                var dat = window.FindControl<Border>("CardDat")!;
                var mame = window.FindControl<Border>("CardMameFiles")!;
                BoundsIn(window, source).Bottom.Should().BeLessThan(BoundsIn(window, dat).Top, "the source comes first");
                window.FindControl<ComboBox>("ComboConvention")!.GetLogicalAncestors().Should().Contain(dat, "the convention sits with the DAT that detects it");
                window.FindControl<Border>("LblConventionBadge")!.GetLogicalAncestors().Should().Contain(dat);
                mame.IsEffectivelyVisible.Should().BeTrue("the MAME files are always in sight, so that a value kept for MAME is seen");
                mame.Classes.Should().NotContain("mame");
                BoundsIn(window, dat).Bottom.Should().BeLessThan(BoundsIn(window, mame).Top, "the MAME files come last");

                // Without a DAT, a sentence says what that changes
                var noDat = window.FindControl<TextBlock>("LblNoDatHint")!;
                noDat.IsEffectivelyVisible.Should().BeTrue();
                vm.Directories.DatFilePath = Path.Combine(_tempDir, "missing.dat");
                Settle(window);
                noDat.IsEffectivelyVisible.Should().BeFalse();

                vm.Directories.SelectedNamingConvention = NamingConvention.Mame;
                Settle(window);
                mame.IsEffectivelyVisible.Should().BeTrue();
                BoundsIn(window, dat).Bottom.Should().BeLessThan(BoundsIn(window, mame).Top);
                window.FindControl<TextBox>("EditChdDir")!.GetLogicalAncestors().Should().Contain(mame);

                // The fields of the frames share one column of labels
                var left = BoundsIn(window, window.FindControl<TextBox>("EditSource")!).Left;
                foreach (var name in new[] { "EditDat", "ComboConvention", "EditChdDir", "EditMameCategoryFile", "EditMameRatingFile" })
                {
                    BoundsIn(window, window.FindControl<Control>(name)!).Left.Should().BeApproximately(left, 1, $"{name} lines up with the source");
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The Output step in frames: the destination, then how the files are written and the multi-disk games side by
    /// side, then the directory tree with its preview. The fields of the full-width frames line up.
    /// </summary>
    [Fact]
    public async Task OutputStep_ShouldGroupItsFieldsInFrames()
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
                var destination = BoundsIn(window, window.FindControl<Border>("CardDestination")!);
                var writing = BoundsIn(window, window.FindControl<Border>("CardWriting")!);
                var multidisk = BoundsIn(window, window.FindControl<Border>("CardMultidisk")!);
                var pattern = window.FindControl<Border>("CardPattern")!;
                destination.Bottom.Should().BeLessThan(writing.Top);
                writing.Top.Should().BeApproximately(multidisk.Top, 1, "the writing and the multi-disk games are side by side");
                writing.Right.Should().BeLessThan(multidisk.Left);
                writing.Bottom.Should().BeLessThan(BoundsIn(window, pattern).Top);

                foreach (var name in new[] { "ComboTransferMode", "ComboConflicts", "ChkVerifyCopies" })
                {
                    BoundsIn(window, window.FindControl<Control>(name)!).Right.Should().BeLessThanOrEqualTo(writing.Right, $"{name} is in its frame");
                }

                foreach (var name in new[] { "ChkSubfolderMultidisk", "ChkGenerateM3u", "ComboM3uPlacement" })
                {
                    BoundsIn(window, window.FindControl<Control>(name)!).Left.Should().BeGreaterThan(multidisk.Left, $"{name} is in its frame");
                }

                window.FindControl<StackPanel>("PanelPatternPreview")!.GetLogicalAncestors().Should().Contain(pattern, "the preview is in the directory tree frame");
                window.FindControl<TextBlock>("LblAppendHint")!.IsEffectivelyVisible.Should().BeTrue("the destination says what happens when it is not completed");
                BoundsIn(window, window.FindControl<TextBox>("EditPattern")!).Left
                    .Should().BeApproximately(BoundsIn(window, window.FindControl<TextBox>("EditDest")!).Left, 1, "the pattern lines up with the destination");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Every file or folder field ends with a cross while it holds a path: a click empties it, as emptying it by hand
    /// would, with every effect of the change (the DAT summary, the files of the MAME filters).
    /// </summary>
    [Fact]
    public async Task FileAndFolderFields_ShouldOfferACrossThatEmptiesThem()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.Directories.SelectedNamingConvention = NamingConvention.Mame;
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                var fields = new (WorkflowStep Step, string Field, string Cross, Action<string> Set, Func<string> Get)[]
                {
                    (WorkflowStep.Collection, "EditSource", "BtnClearSource", v => vm.Directories.SourceDir = v, () => vm.Directories.SourceDir),
                    (WorkflowStep.Collection, "EditDat", "BtnClearDat", v => vm.Directories.DatFilePath = v, () => vm.Directories.DatFilePath),
                    (WorkflowStep.Collection, "EditChdDir", "BtnClearChdDir", v => vm.Directories.ChdDir = v, () => vm.Directories.ChdDir),
                    (WorkflowStep.Collection, "EditMameCategoryFile", "BtnClearMameCategoryFile", v => vm.Directories.MameCategoryFile = v, () => vm.Directories.MameCategoryFile),
                    (WorkflowStep.Collection, "EditMameRatingFile", "BtnClearMameRatingFile", v => vm.Directories.MameRatingFile = v, () => vm.Directories.MameRatingFile),
                    (WorkflowStep.Output, "EditDest", "BtnClearDest", v => vm.Directories.DestinationDir = v, () => vm.Directories.DestinationDir)
                };
                foreach (var (step, fieldName, crossName, set, get) in fields)
                {
                    vm.Navigation.CurrentStep = step;
                    set(string.Empty);
                    Settle(window);
                    var field = window.FindControl<TextBox>(fieldName)!;
                    var cross = window.FindControl<Button>(crossName)!;
                    cross.IsEffectivelyVisible.Should().BeFalse($"{fieldName} is empty");

                    set(Path.Combine(_tempDir, "somewhere"));
                    Settle(window);
                    cross.IsEffectivelyVisible.Should().BeTrue($"{fieldName} holds a path");
                    ((string?)ToolTip.GetTip(cross)).Should().NotBeNullOrWhiteSpace($"{crossName} says what it does");
                    var crossBox = BoundsIn(window, cross);
                    var fieldBox = BoundsIn(window, field);
                    crossBox.Right.Should().BeLessThanOrEqualTo(fieldBox.Right, $"{crossName} is inside {fieldName}");
                    crossBox.Left.Should().BeGreaterThan(fieldBox.Center.X, $"{crossName} is at the end of {fieldName}");

                    cross.Command!.Execute(null);
                    Settle(window);
                    get().Should().BeEmpty();
                    field.Text.Should().BeEmpty();
                    cross.IsEffectivelyVisible.Should().BeFalse();
                }

                // Cleared, the DAT gives way to the sentence that says what that changes
                vm.Navigation.CurrentStep = WorkflowStep.Collection;
                Settle(window);
                window.FindControl<TextBlock>("LblNoDatHint")!.IsEffectivelyVisible.Should().BeTrue();
                vm.Filters.Ratings.RatingsText.Should().Be("Notes : aucun fichier");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The quarantine of the purges of a destination of RomSieve shows in the Destination frame, with the button that
    /// empties it; it stays hidden without a quarantine. Its texts are not cut (measured with Skia only).
    /// </summary>
    [Fact]
    public async Task QuarantineOfTheDestination_ShouldShowInTheDestinationFrame()
    {
        await RunOnUiThreadAsync(async () =>
        {
            var destination = Path.Combine(_tempDir, "purged-destination");
            Directory.CreateDirectory(Path.Combine(destination, DestinationGuard.QuarantineDirectoryName, "20260101-000000"));
            await File.WriteAllTextAsync(Path.Combine(destination, DestinationGuard.QuarantineDirectoryName, "20260101-000000", "old.adf"), "old");
            DestinationGuard.WriteMarker(destination);
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.CurrentStep = WorkflowStep.Output;
                Settle(window);
                var panel = window.FindControl<WrapPanel>("PanelQuarantine")!;
                panel.IsEffectivelyVisible.Should().BeFalse("there is no quarantine without a destination");

                // An empty quarantine is shown too, with its button greyed, so that the user knows it before needing it
                var empty = Path.Combine(_tempDir, "new-destination");
                Directory.CreateDirectory(empty);
                await vm.Quarantine.LoadAsync(empty);
                Settle(window);
                panel.IsEffectivelyVisible.Should().BeTrue();
                window.FindControl<TextBlock>("LblQuarantine")!.Text.Should().Be("Quarantaine des purges (_purged) : vide.");
                window.FindControl<Button>("BtnEmptyQuarantine")!.IsEffectivelyEnabled.Should().BeFalse();
                if (Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") == "1")
                {
                    AssertNotTrimmed(window.FindControl<TextBlock>("LblQuarantine")!, "LblQuarantine");
                }

                await vm.Quarantine.LoadAsync(destination);
                Settle(window);
                panel.IsEffectivelyVisible.Should().BeTrue();
                panel.GetLogicalAncestors().Should().Contain(window.FindControl<Border>("CardDestination")!);
                window.FindControl<TextBlock>("LblQuarantine")!.Text.Should().Contain("1 purge(s), 1 fichier(s)");
                var button = window.FindControl<Button>("BtnEmptyQuarantine")!;
                button.IsEffectivelyEnabled.Should().BeTrue();
                ((string?)ToolTip.GetTip(button)).Should().NotBeNullOrWhiteSpace();

                if (Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") == "1")
                {
                    foreach (var control in new Control[] { window.FindControl<TextBlock>("LblQuarantine")!, button })
                    {
                        AssertNotTrimmed(control, control.Name!);
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
    public async Task OutputPanels_ShouldCenterEachControlOnItsLine()
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
                AssertCenteredOnTheirLines(window, "ComboTransferMode", "ComboConflicts");

                // The verification of the copies is a visible option of the output, out of the advanced panel
                window.FindControl<CheckBox>("ChkVerifyCopies")!.IsEffectivelyVisible.Should().BeTrue();
                window.FindControl<ComboBox>("ComboConflicts")!.FindAncestorOfType<WrapPanel>()!.Children
                    .Select(c => c.Name).Should().Equal("ComboConflicts");

                // The advanced options sit on the line of the scan option of the collection, which takes no extra height
                vm.Navigation.CurrentStep = WorkflowStep.Collection;
                Settle(window);
                AssertCenteredOnTheirLines(window, "ChkRecursive");
                window.FindControl<CheckBox>("ChkRecursive")!.FindAncestorOfType<WrapPanel>()!.Children
                    .Select(c => c.Name).Should().Equal("ChkRecursive", "BtnAdvancedDirectories");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>The controls of the wrap panel of each named control are centered on their line.</summary>
    private static void AssertCenteredOnTheirLines(Window window, params string[] firstControls)
    {
        foreach (var first in firstControls.Select(n => window.FindControl<Control>(n)!))
        {
            var panel = first.FindAncestorOfType<WrapPanel>()!;
            var children = panel.Children.Where(c => c.IsVisible).ToList();
            foreach (var child in children)
            {
                var line = children.Where(c => c.Bounds.Top < child.Bounds.Bottom && child.Bounds.Top < c.Bounds.Bottom).ToList();
                var lineCenter = (line.Min(c => c.Bounds.Top) + line.Max(c => c.Bounds.Bottom)) / 2;
                child.Bounds.Center.Y.Should().BeApproximately(lineCenter, 1, $"{child.Name ?? child.GetType().Name} is on that line");
            }
        }
    }

    [Theory]
    [InlineData("LblMode", "ComboTransferMode")]
    [InlineData("LblConflicts", "ComboConflicts")]
    public async Task TransferModeLabels_ShouldFaceTheirControls(string labelName, string controlName)
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

                // The label faces the first line of its controls, even when they wrap on several lines
                var label = window.FindControl<TextBlock>(labelName)!;
                var control = window.FindControl<Control>(controlName)!;
                var labelCenter = label.TranslatePoint(new Point(0, label.Bounds.Height / 2), window)!.Value.Y;
                var controlCenter = control.TranslatePoint(new Point(0, control.Bounds.Height / 2), window)!.Value.Y;

                labelCenter.Should().BeApproximately(controlCenter, 1, $"{labelName} faces {controlName}");
            }
            finally
            {
                window.Close();
            }
        });
    }
}
