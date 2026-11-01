using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using RomSieve.App.ViewModels;
using RomSieve.App.Views;
using RomSieve.Core.Models;

namespace RomSieve.App.Tests;

/// <summary>
/// Light and dark themes: same palette keys in both variants, readable contrasts, hot switch of every color
/// (palette resources and colors computed from a tone) and persistence of the choice.
/// </summary>
public sealed partial class GuiTests
{
    private static ResourceDictionary PaletteVariant(ThemeVariant variant)
    {
        // The compiled App.xaml merges the palette itself (not a ResourceInclude)
        var palette = Application.Current!.Resources.MergedDictionaries
            .Select(d => d as ResourceDictionary ?? (d as ResourceInclude)?.Loaded as ResourceDictionary)
            .Single(d => d is not null && d.ThemeDictionaries.ContainsKey(ThemeVariant.Dark))!;
        return (ResourceDictionary)palette.ThemeDictionaries[variant];
    }

    private static Color PaletteColor(ThemeVariant variant, string key) =>
        ((ISolidColorBrush)PaletteVariant(variant)[key]!).Color;

    /// <summary>WCAG 2 contrast ratio of two opaque colors (1 to 21).</summary>
    private static double Contrast(Color a, Color b)
    {
        static double Luminance(Color c)
        {
            static double Channel(byte v)
            {
                var s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }

        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Fact]
    public async Task Palette_ShouldDefineTheSameKeysInBothVariants()
    {
        await RunOnUiThreadAsync(() =>
        {
            var light = PaletteVariant(ThemeVariant.Light).Keys.Cast<string>().ToHashSet();
            var dark = PaletteVariant(ThemeVariant.Dark).Keys.Cast<string>().ToHashSet();

            light.Should().NotBeEmpty();
            dark.Should().BeEquivalentTo(light);
        });
    }

    public static TheoryData<string, string, string, double> ContrastPairs()
    {
        var data = new TheoryData<string, string, string, double>();
        foreach (var variant in new[] { "Light", "Dark" })
        {
            // Body text: WCAG AA for normal text
            foreach (var background in new[] { "WindowBackgroundBrush", "PanelBackgroundBrush", "SurfaceBrush", "HeaderBackgroundBrush" })
            {
                data.Add(variant, "TextPrimaryBrush", background, 4.5);
            }

            data.Add(variant, "TabForegroundBrush", "TabBackgroundBrush", 4.5);
            data.Add(variant, "SecondaryButtonForegroundBrush", "SecondaryButtonBackgroundBrush", 4.5);
            foreach (var tone in new[] { "Auto", "Tosec", "NoIntro", "Redump", "Mame" })
            {
                data.Add(variant, $"Badge{tone}ForegroundBrush", $"Badge{tone}BackgroundBrush", 4.5);
            }

            // Status badges of the preview table: small text on its own background, WCAG AA for normal text
            foreach (var status in Enum.GetNames<FileStatus>())
            {
                data.Add(variant, $"Status{status}BadgeForegroundBrush", $"Status{status}BadgeBackgroundBrush", 4.5);
            }

            // Badge of a 1G1R winner chosen by hand, next to the status badge
            data.Add(variant, "ForcedBadgeForegroundBrush", "ForcedBadgeBackgroundBrush", 4.5);

            // Badge of a MAME set kept because a kept set needs it, next to the status badge
            data.Add(variant, "RequiredBadgeForegroundBrush", "RequiredBadgeBackgroundBrush", 4.5);

            // Badge of the files transferred with a row, next to its source name
            data.Add(variant, "RelatedBadgeForegroundBrush", "RelatedBadgeBackgroundBrush", 4.5);

            // Status colors and bold chips: WCAG AA for large or bold text and graphical objects

            foreach (var chip in new[] { "Total", "Valid", "Excluded", "Unmatched", "Duplicates", "Changed", "Selection" })
            {
                data.Add(variant, $"Chip{chip}Brush", "SurfaceBrush", 3.0);
                data.Add(variant, "ChipCheckedForegroundBrush", chip == "Total" ? "ChipTotalCheckedBackgroundBrush" : $"Chip{chip}Brush", 3.0);
            }

            foreach (var button in new[] { "Simulate", "Start", "Stop" })
            {
                data.Add(variant, "OnAccentForegroundBrush", $"{button}ButtonBackgroundBrush", 3.0);
            }

            data.Add(variant, "OnAccentForegroundBrush", "StopButtonHoverBackgroundBrush", 3.0);

            data.Add(variant, "SizeWarningForegroundBrush", "StatsBannerBackgroundBrush", 4.5);
            data.Add(variant, "DatInfoOkForegroundBrush", "PanelBackgroundBrush", 3.0);
            data.Add(variant, "DatInfoErrorForegroundBrush", "PanelBackgroundBrush", 3.0);
        }

        foreach (var variant in new[] { "Light", "Dark" })
        {
            // The log is a dark panel in both themes: its level colors are light in both
            foreach (var level in new[] { "OK", "SKIP", "UNMATCHED", "INFO", "WARN", "ERROR", "PLANNED", "Default" })
            {
                data.Add(variant, $"LogLevel{level}Brush", "LogBackgroundBrush", 4.5);
            }

            data.Add(variant, "DatInfoNeutralForegroundBrush", "PanelBackgroundBrush", 4.5);

            // Hints and the warning of the pattern preview: small text, WCAG AA
            foreach (var background in new[] { "PanelBackgroundBrush", "WindowBackgroundBrush" })
            {
                data.Add(variant, "HintForegroundBrush", background, 4.5);
                data.Add(variant, "PatternWarningForegroundBrush", background, 4.5);
            }
            data.Add(variant, "UpdateBannerForegroundBrush", "UpdateBannerBackgroundBrush", 4.5);

            // Entries of the navigation and the state line of a step, on the window and on the selected entry
            foreach (var background in new[] { "WindowBackgroundBrush", "NavHoverBackgroundBrush", "NavSelectedBackgroundBrush" })
            {
                data.Add(variant, "NavForegroundBrush", background, 4.5);
                data.Add(variant, "NavSelectedForegroundBrush", background, 4.5);
                foreach (var tone in Enum.GetNames<StepIndicatorTone>())
                {
                    data.Add(variant, $"Step{tone}IndicatorBrush", background, 4.5);
                }
            }

            data.Add(variant, "NavSelectedMarkerBrush", "NavSelectedBackgroundBrush", 3.0);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ContrastPairs))]
    public async Task Palette_ShouldKeepTextReadable(string variantName, string foreground, string background, double minimum)
    {
        await RunOnUiThreadAsync(() =>
        {
            var variant = variantName == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

            Contrast(PaletteColor(variant, foreground), PaletteColor(variant, background))
                .Should().BeGreaterThanOrEqualTo(minimum, $"{foreground} on {background} ({variantName})");
        });
    }

    [Fact]
    public async Task ThemeSwitch_ShouldRecolorTheWindowAndTheToneBoundColorsAtOnce()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow { DataContext = vm };
            try
            {
                window.Show();
                vm.Directories.SelectedNamingConvention = NamingConvention.Redump;
                Settle(window);
                var badge = window.FindControl<Border>("LblConventionBadge")!;
                var badgeText = (TextBlock)badge.Child!;
                Application.Current!.ActualThemeVariant.Should().Be(ThemeVariant.Light, "the light theme stays the default");

                vm.UiTheme = UiTheme.Dark;
                Settle(window);

                Application.Current.RequestedThemeVariant.Should().Be(ThemeVariant.Dark);
                ((ISolidColorBrush)window.Background!).Color.Should().Be(PaletteColor(ThemeVariant.Dark, "WindowBackgroundBrush"));
                ((ISolidColorBrush)badge.Background!).Color.Should().Be(PaletteColor(ThemeVariant.Dark, "BadgeRedumpBackgroundBrush"));
                ((ISolidColorBrush)badgeText.Foreground!).Color.Should().Be(PaletteColor(ThemeVariant.Dark, "BadgeRedumpForegroundBrush"));

                vm.UiTheme = UiTheme.System;
                Application.Current.RequestedThemeVariant.Should().Be(ThemeVariant.Default);

                vm.UiTheme = UiTheme.Light;
                Settle(window);
                ((ISolidColorBrush)badge.Background!).Color.Should().Be(PaletteColor(ThemeVariant.Light, "BadgeRedumpBackgroundBrush"));
                ((ISolidColorBrush)badgeText.Foreground!).Color.Should().Be(PaletteColor(ThemeVariant.Light, "BadgeRedumpForegroundBrush"));
            }
            finally
            {
                // The theme belongs to the application, shared by every test
                MainWindow.ApplyTheme(UiTheme.Light);
                window.Close();
            }
        });
    }

    [Fact]
    public async Task DropDownAndToggleButtons_ShouldBePaintedLikeTheOtherButtons()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                Settle(window);
                var surface = PaletteColor(ThemeVariant.Light, "SurfaceBrush");
                ((ISolidColorBrush)window.FindControl<Button>("BtnTiebreakOrder")!.Background!).Color.Should().Be(surface);

                // A Button style does not reach them: they kept the grey of the Fluent theme and looked disabled
                var menus = window.GetLogicalDescendants().OfType<DropDownButton>()
                    .Where(button => button.Name != "BtnPartialSortMenu")
                    .ToList();
                menus.Should().Contain(button => button.Name == "BtnAddLanguage");
                menus.Should().Contain(button => button.Name == "BtnAddCountry");
                foreach (var menu in menus)
                {
                    ((ISolidColorBrush)menu.Background!).Color.Should().Be(surface, menu.Name);
                    menu.BorderThickness.Should().Be(new Thickness(1), menu.Name);
                }

                // Nor a ToggleButton: "Group by game" looked disabled while unchecked, and stands out once checked
                var group = window.FindControl<ToggleButton>("BtnGroupByGame")!;
                vm.Navigation.ShowResults();
                Settle(window);
                group.IsChecked.Should().BeFalse();
                ((ISolidColorBrush)group.Background!).Color.Should().Be(surface);
                group.BorderThickness.Should().Be(new Thickness(1));
                var presenter = group.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
                ((ISolidColorBrush)presenter.Background!).Color.Should().Be(surface);

                group.IsChecked = true;
                Settle(window);
                ((ISolidColorBrush)presenter.Background!).Color.Should().NotBe(surface, "a checked toggle button keeps its accent");
                group.IsChecked = false;
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Screenshots of both themes, for a visual review: only with ROMSIEVE_THEME_CAPTURE_DIR (output folder) and
    /// ROMSIEVE_HEADLESS_SKIA=1 (rendering).
    /// </summary>
    [Fact]
    public async Task ThemeScreenshots_WhenRequested()
    {
        var outputDir = Environment.GetEnvironmentVariable("ROMSIEVE_THEME_CAPTURE_DIR");
        if (string.IsNullOrEmpty(outputDir) || Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") != "1")
        {
            return;
        }

        var items = CreateLargeItemSet().TakeLast(40).ToList();
        await RunOnUiThreadAsync(() =>
        {
            var (vm, window) = ShowFinishedRun(items, streamRows: false);
            try
            {
                window.Width = 1280;
                window.Height = 820;
                vm.Directories.SelectedNamingConvention = NamingConvention.Tosec;

                // ROMSIEVE_CAPTURE_LOCALE captures the interface in another language (fr by default)
                var locale = Environment.GetEnvironmentVariable("ROMSIEVE_CAPTURE_LOCALE");
                if (!string.IsNullOrEmpty(locale))
                {
                    vm.UiLocale = locale;
                }

                vm.StatsBanner.UpdateStats(new ProcessStats { TotalScanned = 40, ValidCount = 16, ExcludedCount = 8, UnmatchedCount = 8, DuplicateCount = 8 });
                vm.StatsBanner.ToggleChip("valid");
                vm.StatsBanner.ToggleChip("total");
                Directory.CreateDirectory(outputDir);
                foreach (var theme in new[] { UiTheme.Light, UiTheme.Dark })
                {
                    vm.UiTheme = theme;
                    foreach (var step in Enum.GetValues<WorkflowStep>())
                    {
                        vm.Navigation.CurrentStep = step;
                        Settle(window);
                        window.CaptureRenderedFrame()!.Save(Path.Combine(outputDir, $"romsieve_{theme.ToConfigString()}_{step.ToString().ToLowerInvariant()}.png"));
                    }

                    // The filters step with the options of the other convention
                    vm.Navigation.CurrentStep = WorkflowStep.Filters;
                    vm.Filters.ShownConventionIndex = FiltersViewModel.NoIntroConventionIndex;
                    Settle(window);
                    window.CaptureRenderedFrame()!.Save(Path.Combine(outputDir, $"romsieve_{theme.ToConfigString()}_filters_nointro.png"));

                    // And with the MAME convention, whose card of MAME machines comes first
                    vm.Directories.SelectedNamingConvention = NamingConvention.Mame;
                    Settle(window);
                    window.CaptureRenderedFrame()!.Save(Path.Combine(outputDir, $"romsieve_{theme.ToConfigString()}_filters_mame.png"));
                    vm.Directories.SelectedNamingConvention = NamingConvention.Tosec;
                    vm.Filters.ShownConventionIndex = FiltersViewModel.TosecConventionIndex;
                }
            }
            finally
            {
                MainWindow.ApplyTheme(UiTheme.Light);
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData("{}", UiTheme.Light)]
    [InlineData("""{ "ui_theme": "dark" }""", UiTheme.Dark)]
    [InlineData("""{ "ui_theme": "system" }""", UiTheme.System)]
    [InlineData("""{ "ui_theme": "sepia" }""", UiTheme.Light)]
    public async Task UiTheme_ShouldBeReadAndSaved(string json, UiTheme expected)
    {
        var path = Path.Combine(_tempDir, "theme_config.json");
        await File.WriteAllTextAsync(path, json);

        var vm = CreateViewModel(configPath: path);

        vm.UiTheme.Should().Be(expected);
        await vm.SaveConfigurationAsync();
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        saved.RootElement.GetProperty("ui_theme").GetString().Should().Be(expected.ToConfigString());
    }
}
