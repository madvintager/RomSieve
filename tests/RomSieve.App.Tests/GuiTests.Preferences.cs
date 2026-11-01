using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.App.Views;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.App.Tests;

/// <summary>
/// Top bar (profile menu, preferences button), preferences window (language, theme, updates, parallelism of the CRC
/// computation, About section) and groups of advanced options in the main window.
/// </summary>
public sealed partial class GuiTests
{
    private sealed class StubUpdateCheckService : IUpdateCheckService
    {
        public int CallCount { get; private set; }

        public Task<UpdateCheckResult> CheckForUpdatesAsync(string? currentVersion = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(UpdateCheckResult.UpToDate("1.0.0"));
        }
    }

    [Fact]
    public async Task TopBar_ShouldOnlyHoldTheProfileMenuAndThePreferences()
    {
        await RunOnUiThreadAsync(() =>
        {
            var dialogs = new HeadlessDialogService();
            var vm = CreateViewModel(dialogs);
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                Settle(window);

                // The preferences have left the main window
                foreach (var name in new[] { "ComboUiLanguage", "ComboUiTheme", "ChkCheckUpdates", "BtnCheckUpdates", "ComboHashingParallelism" })
                {
                    window.FindControl<Control>(name).Should().BeNull($"{name} is in the preferences window");
                }

                var profileMenu = window.FindControl<DropDownButton>("BtnProfileMenu")!;
                profileMenu.Content.Should().Be("Profil");
                profileMenu.Flyout!.ShowAt(profileMenu);
                Settle(window);
                var save = window.FindControl<MenuItem>("MenuSaveProfile")!;
                var load = window.FindControl<MenuItem>("MenuLoadProfile")!;
                save.Header.Should().Be("Enregistrer comme profil…");
                load.Header.Should().Be("Charger un profil…");
                save.Command.Should().BeSameAs(vm.Profiles.SaveProfileCommand);
                load.Command.Should().BeSameAs(vm.Profiles.LoadProfileCommand);
                profileMenu.Flyout.Hide();

                // The preferences button opens the window through the dialog service
                var preferences = window.FindControl<Button>("BtnPreferences")!;
                preferences.Content.Should().Be("Préférences…");
                preferences.Command!.Execute(null);
                Settle(window);
                dialogs.ShownPreferences.Should().ContainSingle().Which.Should().BeSameAs(vm.Preferences);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task PreferencesWindow_LanguageAndTheme_ShouldApplyAtOnceAndBeSaved()
    {
        await RunOnUiThreadAsync(async () =>
        {
            var dialogs = new HeadlessDialogService();
            var vm = CreateViewModel(dialogs);
            var window = new MainWindow(vm);
            window.Show();
            string? preferencesTitle = null;
            dialogs.OnShowPreferences = preferences =>
            {
                // The user changes the language and the theme in the window, then closes it
                var preferencesWindow = new PreferencesWindow { DataContext = preferences };
                preferencesWindow.Show();
                Settle(preferencesWindow);
                preferencesWindow.Title.Should().Be("Préférences");
                preferencesWindow.FindControl<ComboBox>("ComboUiLanguage")!.SelectedValue = "en";
                preferencesWindow.FindControl<ComboBox>("ComboUiTheme")!.SelectedValue = UiTheme.Dark;
                Settle(preferencesWindow);
                preferencesTitle = preferencesWindow.Title;
                preferencesWindow.FindControl<Button>("BtnClosePreferences")!.Content.Should().Be("Close");
                preferencesWindow.Close();
                return Task.CompletedTask;
            };

            try
            {
                await vm.Preferences.OpenCommand.ExecuteAsync(null);
                Settle(window);

                preferencesTitle.Should().Be("Preferences", "the window follows the language at once");
                vm.UiLocale.Should().Be("en");
                vm.UiTheme.Should().Be(UiTheme.Dark);
                window.FindControl<DropDownButton>("BtnProfileMenu")!.Content.Should().Be("Profile");
                Application.Current!.RequestedThemeVariant.Should().Be(ThemeVariant.Dark);

                // Saved as before, as interface state
                await vm.SaveConfigurationAsync();
                using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(_isolatedConfigPath));
                saved.RootElement.GetProperty("ui_locale").GetString().Should().Be("en");
                saved.RootElement.GetProperty("ui_theme").GetString().Should().Be("dark");
            }
            finally
            {
                MainWindow.ApplyTheme(UiTheme.Light);
                window.Close();
            }
        });
    }

    [Fact]
    public async Task PreferencesWindow_HashingParallelism_ShouldStayASortOption()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.Execution.AdoptCurrentSettingsAsSimulatedForTests();
            vm.Execution.IsSimulationCurrent.Should().BeTrue();
            var window = new PreferencesWindow { DataContext = vm.Preferences };
            window.Show();
            try
            {
                Settle(window);
                var combo = window.FindControl<ComboBox>("ComboHashingParallelism")!;
                combo.SelectedValue = 4;

                vm.Directories.HashingParallelism.Should().Be(4);
                vm.CaptureSettings().HashingParallelism.Should().Be(4, "it is still saved in config.json and in the profiles");
                vm.BuildConfigUnchecked().HashingParallelism.Should().Be(4);
                vm.Execution.IsSimulationCurrent.Should().BeFalse("it is part of the options a simulation was run with");

                // The list follows its label on the same line
                var label = window.FindControl<Grid>("PanelHashingParallelism")!.Children.OfType<TextBlock>().Single();
                var labelCenter = label.TranslatePoint(new Point(0, label.Bounds.Height / 2), window)!.Value;
                var comboCenter = combo.TranslatePoint(new Point(0, combo.Bounds.Height / 2), window)!.Value;
                comboCenter.Y.Should().BeApproximately(labelCenter.Y, 2);
                comboCenter.X.Should().BeGreaterThan(labelCenter.X);

                // A sort option cannot change while an operation runs
                vm.Directories.IsRunning = true;
                Settle(window);
                combo.IsEffectivelyEnabled.Should().BeFalse();
                vm.Directories.IsRunning = false;
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task PreferencesWindow_UpdateOptions_ShouldDriveTheUpdateChecker()
    {
        await RunOnUiThreadAsync(async () =>
        {
            var dialogs = new HeadlessDialogService();
            var updates = new StubUpdateCheckService();
            var vm = TestMainViewModel.Create(dialogs, _isolatedConfigPath, updates);
            var window = new PreferencesWindow { DataContext = vm.Preferences };
            window.Show();
            try
            {
                Settle(window);
                var checkOnStartup = window.FindControl<CheckBox>("ChkCheckUpdates")!;
                checkOnStartup.IsChecked.Should().BeTrue();
                checkOnStartup.IsChecked = false;
                vm.Updates.CheckOnStartup.Should().BeFalse();

                var checkNow = window.FindControl<Button>("BtnCheckUpdates")!;
                checkNow.Content.Should().Be("Vérifier maintenant");
                checkNow.Command.Should().BeSameAs(vm.Updates.CheckForUpdatesCommand);
                await ((IAsyncRelayCommand)checkNow.Command!).ExecuteAsync(null);

                updates.CallCount.Should().Be(1);
                dialogs.ShownMessages.Should().ContainSingle(m => m.Level == "INFO");
                vm.Updates.IsNotificationVisible.Should().BeFalse("the release in use is the latest one");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The texts of the preferences window are not cut, in every language. Measured with the real fonts only
    /// (ROMSIEVE_HEADLESS_SKIA=1): the headless text stub has no real glyph widths.
    /// </summary>
    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("es")]
    public async Task PreferencesWindow_Texts_ShouldNotBeCutInAnyLanguage(string locale)
    {
        if (Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") != "1")
        {
            return;
        }

        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.UiLocale = locale;
            var window = new PreferencesWindow { DataContext = vm.Preferences };
            window.Show();
            try
            {
                Settle(window);
                var right = window.Bounds.Width;
                foreach (var control in new Control[]
                         {
                             window.FindControl<ComboBox>("ComboUiLanguage")!,
                             window.FindControl<ComboBox>("ComboUiTheme")!,
                             window.FindControl<CheckBox>("ChkCheckUpdates")!,
                             window.FindControl<Button>("BtnCheckUpdates")!,
                             window.FindControl<ComboBox>("ComboHashingParallelism")!,
                             window.FindControl<TextBlock>("LblHashingNote")!,
                             window.FindControl<TextBlock>("LblAboutVersion")!,
                             window.FindControl<TextBlock>("LblAboutLicense")!,
                             window.FindControl<Button>("BtnThirdPartyNotices")!
                         })
                {
                    var end = control.TranslatePoint(new Point(control.Bounds.Width, 0), window)!.Value.X;
                    end.Should().BeLessThanOrEqualTo(right, $"{control.Name} fits in the window ({locale})");

                    // A list as wide as its longest option, a text on a single line unless it wraps on purpose
                    control.Measure(Size.Infinity);
                    if (control is not TextBlock { TextWrapping: TextWrapping.Wrap })
                    {
                        control.DesiredSize.Width.Should().BeLessThanOrEqualTo(control.Bounds.Width + 1, $"{control.Name} is not cut ({locale})");
                    }

                    control.InvalidateMeasure();
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task PreferencesWindow_AboutSection_ShouldShowTheLicenseAndOpenTheThirdPartyNotices()
    {
        await RunOnUiThreadAsync(async () =>
        {
            var dialogs = new HeadlessDialogService();
            var vm = CreateViewModel(dialogs);
            var window = new PreferencesWindow { DataContext = vm.Preferences };
            window.Show();
            try
            {
                Settle(window);
                window.FindControl<TextBlock>("LblAboutVersion")!.Text.Should().Be($"RomSieve {ProductVersion.Current}");
                window.FindControl<TextBlock>("LblAboutLicense")!.Text.Should().StartWith("Logiciel libre sous licence GNU GPL v3");

                // The last section of the window, under the analysis of the files
                var button = window.FindControl<Button>("BtnThirdPartyNotices")!;
                button.Content.Should().Be("Licences des composants tiers");
                ToolTip.GetTip(button).Should().Be(
                    "Ouvre THIRD-PARTY-NOTICES.txt, livré à côté de RomSieve : les mentions de droits d'auteur et les textes des "
                    + "licences des composants qu'il contient (.NET, Avalonia, SkiaSharp, SharpCompress…).");
                BoundsIn(window, button).Top.Should().BeGreaterThan(BoundsIn(window, window.FindControl<TextBlock>("LblHashingNote")!).Bottom);
                button.Command.Should().BeSameAs(vm.Preferences.OpenThirdPartyNoticesCommand);
                await ((IAsyncRelayCommand)button.Command!).ExecuteAsync(null);
                dialogs.OpenedFiles.Should().Equal(vm.Preferences.ThirdPartyNoticesPath);

                vm.UiLocale = "en";
                Settle(window);
                button.Content.Should().Be("Third-Party Licenses");
                window.FindControl<TextBlock>("LblAboutLicense")!.Text.Should().StartWith("Free software under the GNU GPL v3");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task AdvancedOptionsButton_ShouldCountTheChangedOptionsAndOpenThemOverTheTab()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.WindowHeight = 820;
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                Settle(window);
                var button = window.FindControl<DropDownButton>("BtnAdvancedDirectories")!;
                button.Content.Should().Be("Options avancées");
                button.Classes.Should().NotContain("changed");

                // A closed panel still tells that an option acts
                vm.Directories.VerifySha1 = true;
                vm.Directories.ScanArchives = false;
                Settle(window);
                button.Content.Should().Be("Options avancées (2 modifiées)");
                button.Classes.Should().Contain("changed");
                ToolTip.GetTip(button).Should().Be(
                    "Options qui diffèrent de leur valeur par défaut : Inspecter les archives (ZIP, 7z, RAR), Vérifier le SHA-1.");

                // The panel opens over the page: the layout and the height of the page do not change
                var page = window.FindControl<Control>("PageCollection")!;
                var heightBefore = page.Bounds.Height;
                var sourceBefore = BoundsIn(window, window.FindControl<TextBox>("EditSource")!);
                button.Flyout!.ShowAt(button);
                Settle(window);
                var chdByName = window.FindControl<CheckBox>("ChkMatchChdByName")!;
                chdByName.IsEffectivelyVisible.Should().BeTrue();
                chdByName.Content.Should().Be("Identifier les CHD par leur nom");
                foreach (var name in new[] { "ChkScanArchives", "ChkVerifySha1", "ChkVerifyArchiveContents", "ChkMatchChdByName" })
                {
                    ToolTip.GetTip(window.FindControl<CheckBox>(name)!)?.ToString().Should().NotBeNullOrWhiteSpace($"{name} explains itself");
                }

                // The verification of the copies is no longer in the panel: it is a visible option of the output step
                window.FindControl<StackPanel>("PanelAdvancedDirectories")!.Children.Select(c => c.Name)
                    .Should().Equal("ChkScanArchives", "ChkVerifySha1", "ChkVerifyArchiveContents", "ChkMatchChdByName");

                chdByName.IsChecked = true;
                vm.Directories.MatchChdByName.Should().BeTrue();
                window.FindControl<CheckBox>("ChkScanArchives")!.IsChecked.Should().BeFalse();
                page.Bounds.Height.Should().Be(heightBefore);
                BoundsIn(window, window.FindControl<TextBox>("EditSource")!).Should().Be(sourceBefore);
                button.Flyout.Hide();
                Settle(window);
                page.Bounds.Height.Should().Be(heightBefore);
                button.Content.Should().Be("Options avancées (3 modifiées)");

                vm.UiLocale = "es";
                button.Content.Should().Be("Opciones avanzadas (3 modificadas)");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task LanguageAndCountryFallbacks_ShouldSitIndentedUnderTheirFilters()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.Navigation.CurrentStep = WorkflowStep.Languages;
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                Settle(window);
                foreach (var (filterName, fallbackName) in new[] { ("ChkFilterLanguages", "ChkPriorityFallback"), ("ChkFilterCountries", "ChkCountryFallback") })
                {
                    var filter = window.FindControl<CheckBox>(filterName)!;
                    var fallback = window.FindControl<CheckBox>(fallbackName)!;
                    fallback.IsEffectivelyVisible.Should().BeTrue($"{fallbackName} is shown with its filter");
                    fallback.IsEffectivelyEnabled.Should().BeFalse("it has no effect until its filter is checked");

                    // Right under its filter, indented as an option of it
                    var filterBox = BoundsIn(window, filter);
                    var fallbackBox = BoundsIn(window, fallback);
                    fallbackBox.Top.Should().BeGreaterThanOrEqualTo(filterBox.Bottom - 1, $"{fallbackName} is under {filterName}");
                    fallbackBox.Top.Should().BeLessThan(filterBox.Bottom + 8, $"{fallbackName} is right under {filterName}");
                    fallbackBox.Left.Should().BeGreaterThan(filterBox.Left + 20, $"{fallbackName} is indented under {filterName}");

                    filter.IsChecked = true;
                    Settle(window);
                    fallback.IsEffectivelyEnabled.Should().BeTrue();
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task PreferencesWindow_OpenedAndClosed_ShouldChangeNothing()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.UiLocale = "en";
            vm.Directories.DatInfoText = "kept";
            var changes = new List<string?>();
            vm.Preferences.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
            vm.Directories.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

            var window = new PreferencesWindow { DataContext = vm.Preferences };
            window.Show();
            Settle(window);
            window.Close();
            Dispatcher.UIThread.RunJobs();

            // The lists release their selection when the window closes: nothing is written back
            vm.UiLocale.Should().Be("en");
            vm.UiTheme.Should().Be(UiTheme.Light);
            vm.Directories.HashingParallelism.Should().Be(1);
            vm.Directories.DatInfoText.Should().Be("kept", "the interface was not translated again");
            changes.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task M3uPlacement_ShouldStayVisibleUnderItsOption()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                vm.Navigation.CurrentStep = WorkflowStep.Output;
                vm.Directories.GenerateM3u = true;
                Settle(window);

                var option = window.FindControl<CheckBox>("ChkGenerateM3u")!;
                var placement = window.FindControl<ComboBox>("ComboM3uPlacement")!;
                placement.IsEffectivelyVisible.Should().BeTrue("the place of the playlists is chosen where they are enabled");
                placement.IsEffectivelyEnabled.Should().BeTrue();

                // Right below its option, indented under it
                var optionBox = BoundsIn(window, option);
                var placementBox = BoundsIn(window, placement);
                placementBox.Top.Should().BeGreaterThanOrEqualTo(optionBox.Bottom - 1);
                placementBox.Top.Should().BeLessThan(optionBox.Bottom + 30);
                placementBox.Left.Should().BeGreaterThan(optionBox.Left);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
