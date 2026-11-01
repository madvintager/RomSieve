using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using FluentAssertions;
using RomSieve.App;
using RomSieve.App.Abstractions;
using RomSieve.App.Services;
using RomSieve.App.ViewModels;
using RomSieve.App.Views;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using Xunit;
using AppFileDialogFilter = RomSieve.App.Abstractions.FileDialogFilter;

namespace RomSieve.App.Tests;

/// <summary>
/// Unit and integration test suite covering <see cref="MainViewModel"/>, <see cref="MainWindow"/>,
/// <see cref="LanguagePriorityViewModel"/>, <see cref="CountryPriorityViewModel"/>,
/// <see cref="StatsBannerViewModel"/>, and <see cref="ConfigurationService"/>.
/// </summary>
public sealed partial class GuiTests : IDisposable
{
    private static Task RunOnUiThreadAsync(Action action) =>
        HeadlessTestSession.Session.Dispatch(action, System.Threading.CancellationToken.None);

    /// <summary>
    /// Runs an asynchronous test body on the UI thread and waits for its end. The session has no overload for a
    /// <see cref="Func{Task}"/>: passed as is, it is taken as a function returning a value, and the returned task ends at
    /// the first await of the body, losing what follows and its failures.
    /// </summary>
    private static Task RunOnUiThreadAsync(Func<Task> action) =>
        HeadlessTestSession.Session.Dispatch(async () =>
        {
            await action();
            return true;
        }, System.Threading.CancellationToken.None);

    private readonly string _tempDir;
    private readonly string _isolatedConfigPath;

    public GuiTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"RomSieve_GuiTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _isolatedConfigPath = Path.Combine(_tempDir, "test_config.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private MainViewModel CreateViewModel(IDialogService? dialogService = null, string? configPath = null)
    {
        return TestMainViewModel.Create(
            dialogService ?? new ConfigurableDialogService(),
            configPath ?? _isolatedConfigPath);
    }

    private sealed class ConfigurableDialogService : IDialogService
    {
        public Func<string, string?, string?>? OnPickFolder { get; set; }
        public Func<string, IReadOnlyList<RomSieve.App.Abstractions.FileDialogFilter>?, string?, string?>? OnPickOpenFile { get; set; }
        public Func<string, string, IReadOnlyList<RomSieve.App.Abstractions.FileDialogFilter>?, string?, string?>? OnPickSaveFile { get; set; }
        public bool ConfirmationResult { get; set; } = true;
        public List<(string Title, string Message)> Warnings { get; } = [];
        public List<(string Title, string? InitialDir)> PickFolderCalls { get; } = [];
        public List<(string Title, string? InitialDir)> PickOpenFileCalls { get; } = [];
        public List<(string Title, string DefaultFileName, IReadOnlyList<RomSieve.App.Abstractions.FileDialogFilter>? Filters)> PickSaveFileCalls { get; } = [];

        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null)
        {
            PickFolderCalls.Add((title, initialDirectory));
            return Task.FromResult(OnPickFolder?.Invoke(title, initialDirectory));
        }

        public Task<string?> PickOpenFileAsync(
            string title,
            IReadOnlyList<RomSieve.App.Abstractions.FileDialogFilter>? filters = null,
            string? initialDirectory = null)
        {
            PickOpenFileCalls.Add((title, initialDirectory));
            return Task.FromResult(OnPickOpenFile?.Invoke(title, filters, initialDirectory));
        }

        public Task<string?> PickSaveFileAsync(
            string title,
            string defaultFileName,
            IReadOnlyList<RomSieve.App.Abstractions.FileDialogFilter>? filters = null,
            string? initialDirectory = null)
        {
            PickSaveFileCalls.Add((title, defaultFileName, filters));
            return Task.FromResult(OnPickSaveFile?.Invoke(title, defaultFileName, filters, initialDirectory));
        }

        public Task ShowInfoAsync(string title, string message) => Task.CompletedTask;

        public Task ShowWarningAsync(string title, string message)
        {
            Warnings.Add((title, message));
            return Task.CompletedTask;
        }

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        public Task<bool> ShowConfirmationAsync(string title, string message) =>
            Task.FromResult(ConfirmationResult);

        public Task OpenUrlAsync(string url) => Task.CompletedTask;

        public Task<bool> OpenFileAsync(string path) => Task.FromResult(false);

        public Task ShowPreferencesAsync(PreferencesViewModel preferences) => Task.CompletedTask;
    }

    // =========================================================================
    // 0. XAML loading and named controls of MainWindow (Avalonia Headless)
    // =========================================================================

    [Fact]
    public async Task MainWindow_XamlLoadsAndBindsToMainViewModel()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow
            {
                DataContext = vm
            };

            window.Title.Should().Be($"RomSieve {RomSieve.Core.Services.ProductVersion.Current}");
            window.Width.Should().Be(1280);
            window.Height.Should().Be(700);
            window.MinWidth.Should().Be(960);
            window.MinHeight.Should().Be(580);

            window.FindControl<Panel>("StepContent").Should().NotBeNull();
            window.FindControl<DataGrid>("TablePreview").Should().NotBeNull();
            window.FindControl<AutoCompleteBox>("EditSearchPreview").Should().NotBeNull();
            window.FindControl<Button>("BtnClearSearchPreview").Should().NotBeNull();
            window.FindControl<ProgressBar>("ProgressBar").Should().NotBeNull();
            window.FindControl<TextBlock>("LblStatusMsg").Should().NotBeNull();
            window.FindControl<Border>("LblConventionBadge").Should().NotBeNull();
            window.FindControl<ListBox>("LogViewerList").Should().NotBeNull();
            window.FindControl<Button>("BtnUndo").Should().NotBeNull();

            var table = window.FindControl<DataGrid>("TablePreview")!;
            table.Columns.Count.Should().Be(11);
            foreach (var col in table.Columns)
            {
                col.CanUserResize.Should().BeTrue();
            }
        });
    }

    // =========================================================================
    // 1. test_main_window_init
    // =========================================================================

    [Fact]
    public void TestMainWindowInit()
    {
        var vm = CreateViewModel();

        vm.WindowTitle.Should().Be($"RomSieve {RomSieve.Core.Services.ProductVersion.Current}");
        vm.Directories.Pattern.Should().Be("{publisher}/{year}/{title}/");
        vm.Directories.IsRecursive.Should().BeTrue();
        vm.Directories.RestoreArticles.Should().BeTrue();
        vm.TosecFilters.ExcludeBadDumps.Should().BeTrue();
        vm.TosecFilters.ExcludeModifications.Should().BeTrue();
        vm.TosecFilters.ExcludeDemos.Should().BeFalse();
        vm.Directories.IsTransferCopy.Should().BeTrue();
    }

    // =========================================================================
    // 2. test_variable_insertion
    // =========================================================================

    [Fact]
    public void TestVariableInsertion()
    {
        var vm = CreateViewModel();
        vm.Directories.Pattern = string.Empty;

        vm.Directories.InsertVariable("{title}");
        vm.Directories.Pattern.Should().Be("{title}");

        vm.Directories.InsertVariable("/{year}");
        vm.Directories.Pattern.Should().Be("{title}/{year}");
    }

    // =========================================================================
    // 3. test_variable_menu_inserts_at_the_caret
    // =========================================================================

    [Fact]
    public void TestVariableMenuInsertsAtTheCaret()
    {
        var vm = CreateViewModel();
        var variables = vm.Directories.PatternVariables;

        // Variables of every convention first, then those of TOSEC names, then the MAME categories
        variables.Select(v => v.Tag).Should().Equal(
            "{title}", "{letter}", "{country}", "{language}", "{version}",
            "{publisher}", "{year}", "{system}", "{chipset}", "{video}", "{dump_status}",
            "{genre}", "{category}");
        variables.Take(5).Should().OnlyContain(v => !v.IsTosecOnly);
        variables.Skip(5).Take(6).Should().OnlyContain(v => v.IsTosecOnly);
        variables.Skip(11).Should().OnlyContain(v => !v.IsTosecOnly);
        variables[2].Label.Should().Be("Pays");

        // No caret placed by the user: the variable becomes a new folder at the end
        vm.Directories.Pattern.Should().Be("{publisher}/{year}/{title}/");
        variables[2].InsertCommand.Execute(null);
        vm.Directories.Pattern.Should().Be("{publisher}/{year}/{title}/{country}/");
        vm.Directories.PatternCaretIndex.Should().Be("{publisher}/{year}/{title}/{country}".Length);

        // At the caret: inside a segment, and the caret moves after the variable
        vm.Directories.Pattern = "{title}/";
        vm.Directories.PatternCaretIndex = "{title}".Length;
        vm.Directories.InsertVariableAtCaret(" ({year})");
        vm.Directories.Pattern.Should().Be("{title} ({year})/");
        vm.Directories.PatternCaretIndex.Should().Be("{title} ({year})".Length);

        vm.Directories.PatternCaretIndex = 0;
        variables[1].InsertCommand.Execute(null);
        vm.Directories.Pattern.Should().Be("{letter}{title} ({year})/");

        // A preset replaces the pattern and forgets the caret
        vm.Directories.ResetPatternCommand.Execute(null);
        vm.Directories.Pattern.Should().Be("{publisher}/{year}/{title}/");
        vm.Directories.PatternCaretIndex.Should().Be(-1);

        // Nothing is inserted while the pattern is disabled (output at the root)
        vm.Directories.FlatDestination = true;
        variables[0].InsertCommand.Execute(null);
        vm.Directories.Pattern.Should().Be("{publisher}/{year}/{title}/");
    }

    [Theory]
    [InlineData(NamingConvention.Auto, true)]
    [InlineData(NamingConvention.Tosec, true)]
    [InlineData(NamingConvention.NoIntro, false)]
    [InlineData(NamingConvention.Redump, false)]
    public void TosecVariables_ShouldOnlyBeOfferedForTosecOrUnknownNames(NamingConvention convention, bool shown)
    {
        var vm = CreateViewModel();
        vm.Directories.SelectedNamingConvention = convention;
        vm.Directories.AreTosecVariablesShown.Should().Be(shown);
    }

    // =========================================================================
    // 4. test_verified_only_toggle
    // =========================================================================

    [Fact]
    public void TestVerifiedOnlyToggle()
    {
        var vm = CreateViewModel();

        vm.TosecFilters.AreSubDumpFiltersEnabled.Should().BeTrue();
        vm.TosecFilters.DumpsVerifiedOnly = true;
        vm.TosecFilters.AreSubDumpFiltersEnabled.Should().BeFalse();
        vm.TosecFilters.DumpsVerifiedOnly = false;
        vm.TosecFilters.AreSubDumpFiltersEnabled.Should().BeTrue();
    }

    // =========================================================================
    // 5. test_build_config_from_ui
    // =========================================================================

    [Fact]
    public void TestBuildConfigFromUi()
    {
        var vm = CreateViewModel();
        var srcPath = Path.Combine(_tempDir, "source");
        var dstPath = Path.Combine(_tempDir, "dest");

        vm.Directories.SourceDir = srcPath;
        vm.Directories.DestinationDir = dstPath;
        vm.Directories.IsTransferMove = true;
        vm.Directories.SelectedCollisionMode = CollisionMode.Skip;

        var cfg = vm.Execution.BuildConfig();
        cfg.Should().NotBeNull();
        cfg!.SourceDir.Should().Be(srcPath);
        cfg.DestinationDir.Should().Be(dstPath);
        cfg.TransferMode.Should().Be(TransferMode.Move);
        cfg.CollisionMode.Should().Be(CollisionMode.Skip);
        cfg.AppendToDestination.Should().BeFalse();

        vm.Directories.AppendToDestination = true;
        var cfg2 = vm.Execution.BuildConfig();
        cfg2.Should().NotBeNull();
        cfg2!.AppendToDestination.Should().BeTrue();
    }

    // =========================================================================
    // 6. test_flat_destination_ui
    // =========================================================================

    [Fact]
    public void TestFlatDestinationUi()
    {
        var vm = CreateViewModel();
        vm.Directories.SourceDir = Path.Combine(_tempDir, "source");
        vm.Directories.DestinationDir = Path.Combine(_tempDir, "dest");

        vm.Directories.IsPatternEditingEnabled.Should().BeTrue();
        vm.Directories.IsPatternLayout.Should().BeTrue();
        vm.Directories.IsRootLayout = true;
        vm.Directories.FlatDestination.Should().BeTrue("the organization of the destination is two radio buttons");
        vm.Directories.IsPatternLayout.Should().BeFalse();
        vm.Directories.IsRootLayout = false;
        vm.Directories.FlatDestination.Should().BeTrue("unchecking a radio button changes nothing: the other one sets it");
        vm.Directories.IsPatternLayout = true;
        vm.Directories.FlatDestination.Should().BeFalse();
        vm.Directories.FlatDestination = true;
        vm.Directories.IsPatternEditingEnabled.Should().BeFalse();

        var cfg = vm.Execution.BuildConfig();
        cfg.Should().NotBeNull();
        cfg!.Pattern.Should().BeEmpty();
        cfg.SubfolderMultidisk.Should().BeTrue();

        vm.Directories.SubfolderMultidisk = false;
        var cfg2 = vm.Execution.BuildConfig();
        cfg2!.SubfolderMultidisk.Should().BeFalse();

        // Test m3u placement widget enable/disable logic
        vm.Directories.GenerateM3u.Should().BeFalse();
        vm.Directories.IsM3uPlacementEnabled.Should().BeFalse();
        vm.Directories.GenerateM3u = true;
        // Since subfolder_multidisk is currently False, placement is still disabled
        vm.Directories.IsM3uPlacementEnabled.Should().BeFalse();
        // When subfolder is re-enabled, placement becomes enabled
        vm.Directories.SubfolderMultidisk = true;
        vm.Directories.IsM3uPlacementEnabled.Should().BeTrue();
    }

    // =========================================================================
    // 7. test_language_ui_and_multiselect_search
    // =========================================================================

    [Fact]
    public void TestLanguageUiAndMultiselectSearch()
    {
        var i18n = new LocalizationService("fr");
        var widget = new LanguagePriorityViewModel(i18n);
        widget.AvailableItems.Count.Should().BeGreaterOrEqualTo(45);

        // Filter with "arabe"
        widget.SearchFilterText = "arabe";
        widget.FilteredAvailableItems.Should().ContainSingle();
        widget.FilteredAvailableItems[0].RankedDisplayText.Should().Contain("ar");

        // Filter with "multilingue"
        widget.SearchFilterText = "multilingue";
        widget.FilteredAvailableItems.Should().ContainSingle();
        widget.FilteredAvailableItems[0].RankedDisplayText.Should().ContainEquivalentOf("m");
    }

    // =========================================================================
    // 8. test_language_priority_ui
    // =========================================================================

    [Fact]
    public void TestLanguagePriorityUi()
    {
        var vm = CreateViewModel();
        vm.Directories.SourceDir = Path.Combine(_tempDir, "source");
        vm.Directories.DestinationDir = Path.Combine(_tempDir, "dest");

        // Initially before languages are added, filter_languages is False, fallback is disabled
        vm.Priorities.IsFallbackLanguagesEnabled.Should().BeFalse();
        vm.Priorities.FilterLanguages.Should().BeFalse();
        vm.Priorities.OneGameOneRom.Should().BeFalse();

        // Setting priority languages auto-enables filter_languages and enables fallback
        vm.Priorities.LanguagePriority.SetPriorityCodes(["fr", "en"]);
        vm.Priorities.LanguagesText = "fr, en";

        vm.Priorities.FilterLanguages.Should().BeTrue();
        vm.Priorities.IsFallbackLanguagesEnabled.Should().BeTrue();
        vm.Priorities.Are1G1ROptionsEnabled.Should().BeFalse();

        var cfg = vm.Execution.BuildConfig()!;
        cfg.Filters.FilterLanguages.Should().BeTrue();
        cfg.Filters.OneGameOneRom.Should().BeFalse();
        cfg.Filters.PriorityLanguages.Should().Equal("fr", "en");

        // Now enable 1G1R
        vm.Priorities.OneGameOneRom = true;
        vm.Priorities.Are1G1ROptionsEnabled.Should().BeTrue();

        var cfg1g1r = vm.Execution.BuildConfig()!;
        cfg1g1r.Filters.OneGameOneRom.Should().BeTrue();
        cfg1g1r.Filters.FilterLanguages.Should().BeTrue();
        cfg1g1r.Filters.PriorityLanguages.Should().Equal("fr", "en");
        cfg1g1r.Filters.FallbackUnmatchedLanguages.Should().BeTrue();
    }

    // =========================================================================
    // 9. test_language_priority_widget_drag_and_reorder
    // =========================================================================

    [Fact]
    public void TestLanguagePriorityWidgetDragAndReorder()
    {
        var sampleLangs = new List<(string Code, string Name)>
        {
            ("fr", "Français"),
            ("en", "Anglais"),
            ("de", "Allemand"),
            ("es", "Espagnol")
        };
        var widget = new LanguagePriorityViewModel(sampleLangs, ["de", "en", "fr", "es"]);

        // Initial order: de (1), en (2), fr (3), es (4)
        widget.GetPriorityCodes().Should().Equal("de", "en", "fr", "es");
        widget.PriorityItems[0].RankedDisplayText.Should().Contain("1. de");
        widget.PriorityItems[2].RankedDisplayText.Should().Contain("3. fr");

        // User drags "fr" (row 2) to the very top (row 0)
        widget.MovePriorityItem(2, 0);

        // Now "fr" is 1st!
        widget.GetPriorityCodes().Should().Equal("fr", "de", "en", "es");
        widget.PriorityItems[0].RankedDisplayText.Should().Contain("1. fr");
        widget.PriorityItems[1].RankedDisplayText.Should().Contain("2. de");

        // User drags "en" (now row 2) under "fr" (to row 1)
        widget.MovePriorityItem(2, 1);

        // Result: 1st is fr, 2nd is en, 3rd is de, 4th is es
        widget.GetPriorityCodes().Should().Equal("fr", "en", "de", "es");
        widget.PriorityItems[0].RankedDisplayText.Should().Contain("1. fr");
        widget.PriorityItems[1].RankedDisplayText.Should().Contain("2. en");
        widget.PriorityItems[2].RankedDisplayText.Should().Contain("3. de");
        widget.PriorityItems[3].RankedDisplayText.Should().Contain("4. es");

        // Test move buttons
        widget.SelectedPriorityItem = widget.PriorityItems[3]; // 'es'
        widget.MoveUp();
        widget.GetPriorityCodes().Should().Equal("fr", "en", "es", "de");

        widget.MoveToTop();
        widget.GetPriorityCodes().Should().Equal("es", "fr", "en", "de");

        // Test remove
        widget.SelectedPriorityItem = widget.PriorityItems[0]; // remove 'es'
        widget.RemoveSelected();
        widget.GetPriorityCodes().Should().Equal("fr", "en", "de");
    }

    // =========================================================================
    // 10. test_language_dual_list_selector
    // =========================================================================

    [Fact]
    public void TestLanguageDualListSelector()
    {
        var sampleLangs = new List<(string Code, string Name)>
        {
            ("fr", "Français"),
            ("en", "Anglais"),
            ("de", "Allemand"),
            ("es", "Espagnol"),
            ("it", "Italien"),
            ("ja", "Japonais")
        };
        var widget = new LanguagePriorityViewModel(sampleLangs, ["fr", "en"]);

        // Initial state: 2 chosen (fr, en), 4 available (de, es, it, ja)
        widget.GetPriorityCodes().Should().Equal("fr", "en");
        widget.PriorityItems.Count.Should().Be(2);
        widget.AvailableItems.Count.Should().Be(4);
        widget.AvailableItems.Select(x => x.Code).Should().Equal("de", "es", "it", "ja");

        // 1. Select 'de' on the left and click transfer button '▶' (AddSelectedCommand)
        widget.SelectedAvailableItem = widget.AvailableItems[0]; // 'de'
        widget.AddSelectedCommand.Execute(null);
        widget.GetPriorityCodes().Should().Equal("fr", "en", "de");
        widget.AvailableItems.Count.Should().Be(3);

        // 2. Double-click on 'ja' in list_available to transfer to chosen
        var itemJa = widget.AvailableItems.FirstOrDefault(x => x.Code == "ja");
        itemJa.Should().NotBeNull();
        widget.AddSelected(itemJa);
        widget.GetPriorityCodes().Should().Equal("fr", "en", "de", "ja");

        // 3. Select 'fr' on the right and click transfer button '◀' (RemoveSelectedCommand)
        widget.SelectedPriorityItem = widget.PriorityItems[0]; // 'fr'
        widget.RemoveSelectedCommand.Execute(null);
        widget.GetPriorityCodes().Should().Equal("en", "de", "ja");
        widget.PriorityItems[0].RankedDisplayText.Should().Contain("1. en");
        widget.PriorityItems[1].RankedDisplayText.Should().Contain("2. de");
        widget.PriorityItems[2].RankedDisplayText.Should().Contain("3. ja");

        // 'fr' must be returned to list_available
        widget.AvailableItems.Select(x => x.Code).Should().Contain("fr");

        // 4. Double click on 'de' in chosen list to return it
        var itemDe = widget.PriorityItems[1]; // 'de' is at index 1
        widget.RemoveSelected(itemDe);
        widget.GetPriorityCodes().Should().Equal("en", "ja");

        // 5. Search filter on available list
        widget.SearchFilterText = "italien";
        widget.FilteredAvailableItems.Should().ContainSingle();
        widget.FilteredAvailableItems[0].Code.Should().Be("it");

        widget.SearchFilterText = string.Empty;
        widget.FilteredAvailableItems.Count.Should().Be(widget.AvailableItems.Count);
    }

    // =========================================================================
    // 11. test_language_priority_widget_default_empty_chosen
    // =========================================================================

    [Fact]
    public void TestLanguagePriorityWidgetDefaultEmptyChosen()
    {
        var i18n = new LocalizationService("fr");
        var defaultLangs = i18n.GetDefaultLanguages();

        var widget = new LanguagePriorityViewModel(i18n);
        widget.GetPriorityCodes().Should().BeEmpty();
        widget.PriorityItems.Count.Should().Be(0);
        widget.AvailableItems.Count.Should().Be(defaultLangs.Count);

        var vm = CreateViewModel();
        vm.Priorities.LanguagePriority.GetPriorityCodes().Should().BeEmpty();
        vm.Priorities.LanguagePriority.PriorityItems.Count.Should().Be(0);
        vm.Priorities.LanguagePriority.AvailableItems.Count.Should().Be(defaultLangs.Count);
    }

    // =========================================================================
    // 12. test_main_window_priority_sync
    // =========================================================================

    [Fact]
    public void TestMainWindowPrioritySync()
    {
        var vm = CreateViewModel();

        // Programmatic change in priority widget reflects in edit_languages
        vm.Priorities.LanguagePriority.SetPriorityCodes(["fr", "en", "de"]);
        vm.Priorities.LanguagesText.Should().Be("fr, en, de");

        // User typing or pasting in edit_languages reflects in priority widget
        vm.Priorities.LanguagesText = "ja, en, fr";
        vm.Priorities.LanguagePriority.GetPriorityCodes().Should().Equal("ja", "en", "fr");
        vm.Priorities.LanguagePriority.PriorityItems[0].RankedDisplayText.Should().Contain("1. ja");
    }

    // =========================================================================
    // 13. test_tabbed_interface
    // =========================================================================

    [Fact]
    public void TestStepNavigation()
    {
        var vm = CreateViewModel();
        vm.Navigation.Entries.Select(e => e.Label).Should().Equal(
            "Collection", "Sélection", "Filtres", "Langues et 1G1R", "Sortie", "Résultats");

        foreach (var step in Enum.GetValues<WorkflowStep>())
        {
            vm.Navigation.CurrentStep = step;
            vm.Navigation.CurrentStep.Should().Be(step);
        }
    }

    // =========================================================================
    // 14. test_gui_ignore_modifications
    // =========================================================================

    [Fact]
    public void TestGuiIgnoreModifications()
    {
        var vm = CreateViewModel();
        vm.Directories.SourceDir = Path.Combine(_tempDir, "source");
        vm.Directories.DestinationDir = Path.Combine(_tempDir, "dest");

        // Initially disabled because language_priority / 1G1R is False
        vm.Priorities.Are1G1ROptionsEnabled.Should().BeFalse();
        vm.TosecFilters.IgnoreModificationsInPriority.Should().BeFalse();

        // Check language_priority (OneGameOneRom) -> chk_ignore_modifications becomes enabled
        vm.Priorities.OneGameOneRom = true;
        vm.Priorities.Are1G1ROptionsEnabled.Should().BeTrue();

        // Check ignore modifications
        vm.TosecFilters.IgnoreModificationsInPriority = true;

        var cfg = vm.Execution.BuildConfig()!;
        cfg.Filters.OneGameOneRom.Should().BeTrue();
        cfg.Filters.IgnoreModificationsInPriority.Should().BeTrue();
    }

    // =========================================================================
    // 15. test_gui_non_empty_dest_warning
    // =========================================================================

    [Fact]
    public async Task TestGuiNonEmptyDestWarning()
    {
        var src = Path.Combine(_tempDir, "gui_src");
        var dst = Path.Combine(_tempDir, "gui_dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        await File.WriteAllTextAsync(Path.Combine(src, "Game (1990)(Ocean).dsk"), "game");
        await File.WriteAllTextAsync(Path.Combine(dst, "already_here.txt"), "content");

        var dialog = new ConfigurableDialogService();
        var vm = CreateViewModel(dialog);
        vm.Directories.SourceDir = src;
        vm.Directories.DestinationDir = dst;

        // Case 1: User cancels the warning -> operation is aborted
        dialog.ConfirmationResult = false;
        await vm.Execution.StartSortAsync();
        vm.Execution.LastExecutedConfig.Should().BeNull();
        File.Exists(Path.Combine(dst, "already_here.txt")).Should().BeTrue();

        // Case 2: User confirms the warning -> purge_destination is enabled and worker executes
        dialog.ConfirmationResult = true;
        await vm.Execution.StartSortAsync();
        vm.Execution.LastExecutedConfig.Should().NotBeNull();
        vm.Execution.LastExecutedConfig!.PurgeDestination.Should().BeTrue();
    }

    [Fact]
    public async Task TestGuiDestinationParentOfSourceIsRejected()
    {
        var parent = Path.Combine(_tempDir, "gui_roms");
        var src = Path.Combine(parent, "in");
        Directory.CreateDirectory(src);
        var game = Path.Combine(src, "Game (1990)(Ocean).dsk");
        await File.WriteAllTextAsync(game, "game");

        var dialog = new ConfigurableDialogService { ConfirmationResult = true };
        var vm = CreateViewModel(dialog);
        vm.Directories.SourceDir = src;
        vm.Directories.DestinationDir = parent;

        await vm.Execution.StartSortAsync();

        vm.Execution.LastExecutedConfig.Should().BeNull();
        dialog.Warnings.Should().ContainSingle();
        File.Exists(game).Should().BeTrue();
    }

    [Fact]
    public async Task TestGuiUndoLastSortRestoresMovedFiles()
    {
        var src = Path.Combine(_tempDir, "gui_undo_src");
        var dst = Path.Combine(_tempDir, "gui_undo_dst");
        Directory.CreateDirectory(src);
        var game = Path.Combine(src, "Game (1990)(Ocean).dsk");
        await File.WriteAllTextAsync(game, "game");

        var dialog = new ConfigurableDialogService { ConfirmationResult = true };
        var vm = CreateViewModel(dialog);
        vm.Directories.SourceDir = src;
        vm.Directories.DestinationDir = dst;
        vm.Directories.IsTransferMove = true;
        vm.Execution.HasUndoableSort.Should().BeFalse();

        await vm.Execution.StartSortAsync();

        File.Exists(game).Should().BeFalse();
        vm.Execution.HasUndoableSort.Should().BeTrue();
        vm.Execution.UndoLastSortCommand.CanExecute(null).Should().BeTrue();
        Directory.EnumerateFiles(Path.Combine(_tempDir, "journals")).Should().ContainSingle();

        await vm.Execution.UndoLastSortAsync();

        File.ReadAllText(game).Should().Be("game");
        vm.Execution.HasUndoableSort.Should().BeFalse();
        vm.Execution.UndoLastSortCommand.CanExecute(null).Should().BeFalse();
        dialog.Warnings.Should().BeEmpty();
    }

    // =========================================================================
    // 16. test_gui_non_empty_dest_append_mode
    // =========================================================================

    [Fact]
    public async Task TestGuiNonEmptyDestAppendMode()
    {
        var src = Path.Combine(_tempDir, "gui_src_app");
        var dst = Path.Combine(_tempDir, "gui_dst_app");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        await File.WriteAllTextAsync(Path.Combine(src, "Game (1990)(Ocean).dsk"), "game");
        await File.WriteAllTextAsync(Path.Combine(dst, "already_here.txt"), "content");

        var dialog = new ConfigurableDialogService();
        var vm = CreateViewModel(dialog);
        vm.Directories.SourceDir = src;
        vm.Directories.DestinationDir = dst;
        vm.Directories.AppendToDestination = true;

        // Case 1: User cancels -> operation aborted
        dialog.ConfirmationResult = false;
        await vm.Execution.StartSortAsync();
        vm.Execution.LastExecutedConfig.Should().BeNull();
        File.Exists(Path.Combine(dst, "already_here.txt")).Should().BeTrue();

        // Case 2: User confirms -> worker starts with purge_destination=False and append_to_destination=True
        dialog.ConfirmationResult = true;
        await vm.Execution.StartSortAsync();
        vm.Execution.LastExecutedConfig.Should().NotBeNull();
        vm.Execution.LastExecutedConfig!.PurgeDestination.Should().BeFalse();
        vm.Execution.LastExecutedConfig.AppendToDestination.Should().BeTrue();
        File.Exists(Path.Combine(dst, "already_here.txt")).Should().BeTrue();
    }

    // =========================================================================
    // 17. test_config_manager
    // =========================================================================

    [Fact]
    public async Task TestConfigManager()
    {
        var configService = new ConfigurationService();

        // 1. Default config path is a valid path ending with config.json inside RomSieve
        var defaultPath = configService.GetDefaultConfigPath();
        Path.GetFileName(defaultPath).Should().Be("config.json");
        defaultPath.Should().Contain("RomSieve");

        // 2. Loading non-existent file returns null
        var missingFile = Path.Combine(_tempDir, "sub", "nonexistent.json");
        (await configService.LoadConfigFromFileAsync(missingFile)).Should().BeNull();

        // 3. Saving and loading valid config
        var targetFile = Path.Combine(_tempDir, "sub", "my_config.json");
        var data = new Dictionary<string, object?>
        {
            ["source_dir"] = "/path/to/src",
            ["recursive"] = false,
            ["items"] = new List<int> { 1, 2, 3 }
        };
        (await configService.SaveConfigToFileAsync(data, targetFile)).Should().BeTrue();
        File.Exists(targetFile).Should().BeTrue();

        var loaded = await configService.LoadConfigFromFileAsync(targetFile);
        loaded.Should().NotBeNull();
        loaded!["source_dir"]!.ToString().Should().Be("/path/to/src");
        var recVal = loaded["recursive"] is JsonElement je ? je.GetBoolean() : (bool)loaded["recursive"]!;
        recVal.Should().BeFalse();

        // 4. Corrupted file returns null
        await File.WriteAllTextAsync(targetFile, "{broken json");
        (await configService.LoadConfigFromFileAsync(targetFile)).Should().BeNull();
    }

    // =========================================================================
    // 18. test_main_window_configuration_persistence
    // =========================================================================

    [Fact]
    public async Task TestMainWindowConfigurationPersistence()
    {
        var cfgFile = Path.Combine(_tempDir, "test_session_config.json");

        // Step 1: Open first session and customize all tabs
        var win1 = CreateViewModel(configPath: cfgFile);
        win1.Directories.SelectedNamingConvention = NamingConvention.NoIntro;
        win1.Directories.DatFilePath = Path.Combine(_tempDir, "custom.dat");
        win1.Directories.SourceDir = Path.Combine(_tempDir, "custom_source");
        win1.Directories.DestinationDir = Path.Combine(_tempDir, "custom_dest");
        win1.Directories.IsTransferMove = true;
        win1.Directories.SelectedCollisionMode = CollisionMode.Skip;
        win1.Directories.IsRecursive = false;
        win1.Directories.ScanArchives = false;
        win1.Directories.AppendToDestination = true;
        win1.Directories.FlatDestination = true;
        win1.Directories.RestoreArticles = false;
        win1.Directories.SubfolderMultidisk = false;
        win1.Directories.GenerateM3u = true;
        win1.Directories.SelectedM3uPlacement = M3uPlacementMode.ParentHidden;
        win1.Directories.Pattern = "{title}/{year}/";

        // Tab 2 customizations
        win1.TosecFilters.DumpsVerifiedOnly = true;
        win1.TosecFilters.ExcludeModifications = true;
        win1.TosecFilters.ExcludeTranslations = true;
        win1.TosecFilters.ExcludeDevVersions = true;
        win1.TosecFilters.CompleteMultidiskSetsOnly = true;
        win1.TosecFilters.SmartMultidiskGrouping = false;
        win1.TosecFilters.ExcludeDemos = true;
        win1.NoIntroFilters.ExcludePirate = false;
        win1.NoIntroFilters.ExcludeHack = false;
        win1.NoIntroFilters.ExcludeUnlicensed = true;
        win1.FilterSubtabIndex = 1;

        // Tab 3 customizations
        win1.Priorities.OneGameOneRom = true;
        win1.Priorities.LanguagePriority.SetPriorityCodes(["fr", "en", "es"]);
        win1.Priorities.LanguagesText = "fr, en, es";
        win1.Priorities.FallbackUnmatchedLanguages = false;
        win1.Priorities.FilterCountries = true;
        win1.Priorities.CountryPriority.SetPriorityCodes(["FR", "EU", "US"]);
        win1.Priorities.CountriesText = "FR, EU, US";
        win1.Priorities.FallbackUnmatchedCountries = false;
        win1.Priorities.RegionPriorityMode = RegionPriorityMode.CountryFirst;
        win1.TosecFilters.IgnoreModificationsInPriority = true;
        win1.Priorities.PreferMonolingual = false;
        win1.Priorities.PreferredVideo = VideoPreference.Pal;
        win1.TosecFilters.PreferredMediaType = MediaPreference.Cartridge;

        // UI tab & geometry
        win1.Navigation.CurrentStep = WorkflowStep.Languages;
        win1.WindowWidth = 1200;
        win1.WindowHeight = 900;

        // Save configuration (simulating closeEvent)
        await win1.SaveConfigurationAsync();
        File.Exists(cfgFile).Should().BeTrue();

        // Step 2: Open second session with the same config file
        var win2 = CreateViewModel(configPath: cfgFile);

        // Assert Tab 1
        win2.Directories.SelectedNamingConvention.Should().Be(NamingConvention.NoIntro);
        win2.Directories.DatFilePath.Should().Be(Path.Combine(_tempDir, "custom.dat"));
        win2.Directories.SourceDir.Should().Be(Path.Combine(_tempDir, "custom_source"));
        win2.Directories.DestinationDir.Should().Be(Path.Combine(_tempDir, "custom_dest"));
        win2.Directories.IsTransferMove.Should().BeTrue();
        win2.Directories.SelectedCollisionMode.Should().Be(CollisionMode.Skip);
        win2.Directories.IsRecursive.Should().BeFalse();
        win2.Directories.ScanArchives.Should().BeFalse();
        win2.Directories.AppendToDestination.Should().BeTrue();
        win2.Directories.FlatDestination.Should().BeTrue();
        win2.Directories.RestoreArticles.Should().BeFalse();
        win2.Directories.SubfolderMultidisk.Should().BeFalse();
        win2.Directories.GenerateM3u.Should().BeTrue();
        win2.Directories.SelectedM3uPlacement.Should().Be(M3uPlacementMode.ParentHidden);
        win2.Directories.Pattern.Should().Be("{title}/{year}/");

        // Assert Tab 2
        win2.TosecFilters.DumpsVerifiedOnly.Should().BeTrue();
        win2.TosecFilters.ExcludeModifications.Should().BeTrue();
        win2.TosecFilters.ExcludeTranslations.Should().BeTrue();
        win2.TosecFilters.ExcludeDevVersions.Should().BeTrue();
        win2.TosecFilters.CompleteMultidiskSetsOnly.Should().BeTrue();
        win2.TosecFilters.SmartMultidiskGrouping.Should().BeFalse();
        win2.TosecFilters.ExcludeDemos.Should().BeTrue();
        win2.NoIntroFilters.ExcludePirate.Should().BeFalse();
        win2.NoIntroFilters.ExcludeHack.Should().BeFalse();
        win2.NoIntroFilters.ExcludeUnlicensed.Should().BeTrue();
        win2.FilterSubtabIndex.Should().Be(1);

        // Assert Tab 3
        win2.Priorities.FilterLanguages.Should().BeTrue();
        win2.Priorities.OneGameOneRom.Should().BeTrue();
        win2.Priorities.LanguagePriority.GetPriorityCodes().Should().Equal("fr", "en", "es");
        win2.Priorities.LanguagesText.Should().Be("fr, en, es");
        win2.Priorities.FallbackUnmatchedLanguages.Should().BeFalse();
        win2.Priorities.FilterCountries.Should().BeTrue();
        win2.Priorities.CountryPriority.GetPriorityCodes().Should().Equal("FR", "EU", "US");
        win2.Priorities.CountriesText.Should().Be("FR, EU, US");
        win2.Priorities.FallbackUnmatchedCountries.Should().BeFalse();
        win2.Priorities.RegionPriorityMode.Should().Be(RegionPriorityMode.CountryFirst);
        win2.TosecFilters.IgnoreModificationsInPriority.Should().BeTrue();
        win2.Priorities.PreferMonolingual.Should().BeFalse();
        win2.Priorities.PreferredVideo.Should().Be(VideoPreference.Pal);
        win2.TosecFilters.PreferredMediaType.Should().Be(MediaPreference.Cartridge);

        // Assert UI tab & geometry (the application always opens on the collection)
        win2.Navigation.CurrentStep.Should().Be(WorkflowStep.Collection);
        win2.WindowWidth.Should().Be(1200);
        win2.WindowHeight.Should().Be(900);
    }

    // =========================================================================
    // 19. test_dat_file_ui & test_dat_file_not_found_warning
    // =========================================================================

    [Fact]
    public async Task TestDatFileUi()
    {
        var vm = CreateViewModel();
        var fakeDat = Path.Combine(_tempDir, "tosec.dat");
        await File.WriteAllTextAsync(fakeDat, "clrmamepro ()");

        vm.Directories.DatFilePath = fakeDat;
        vm.Directories.SourceDir = Path.Combine(_tempDir, "source");
        vm.Directories.DestinationDir = Path.Combine(_tempDir, "dest");

        var cfg = vm.Execution.BuildConfig();
        cfg.Should().NotBeNull();
        cfg!.DatFile.Should().Be(fakeDat);
    }

    [Fact]
    public async Task TestDatFileNotFoundWarning()
    {
        var dialog = new ConfigurableDialogService();
        var vm = CreateViewModel(dialog);

        vm.Directories.DatFilePath = Path.Combine(_tempDir, "nonexistent.dat");
        vm.Directories.SourceDir = Path.Combine(_tempDir, "source");
        vm.Directories.DestinationDir = Path.Combine(_tempDir, "dest");

        var cfg = await vm.Execution.TryBuildConfigAsync();
        cfg.Should().BeNull();
        dialog.Warnings.Should().NotBeEmpty();
    }

    // =========================================================================
    // 20. test_dat_info_badge_and_miss_list_button_ui
    // =========================================================================

    [Fact]
    public async Task TestDatInfoBadgeAndMissListButtonUi()
    {
        var dialog = new ConfigurableDialogService();
        var vm = CreateViewModel(dialog);

        const string datContent = """
            clrmamepro (
            	name "Atari ST - Games"
            	version "2024-01-01"
            )
            game (
            	name "Carrier Command (1988)(Rainbird)"
            	rom ( name "Carrier Command (1988)(Rainbird).st" size 820000 crc 11223344 )
            )
            """;

        var datPath = Path.Combine(_tempDir, "atari.dat");
        await File.WriteAllTextAsync(datPath, datContent);

        // Set DAT text -> DatInfoText is updated
        vm.Directories.DatFilePath = datPath;
        await vm.Directories.DatLoadTask;
        vm.Directories.DatInfoText.Should().Contain("Atari ST - Games");
        vm.Directories.DatInfoText.Should().Contain("2024-01-01");

        // Initially CanExportMissList is disabled
        vm.CanExportMissList.Should().BeFalse();

        // Simulate run where no files match the DAT (1 missing rom)
        var src = Path.Combine(_tempDir, "src_test_miss");
        var dst = Path.Combine(_tempDir, "dst_test_miss");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        await File.WriteAllBytesAsync(Path.Combine(src, "empty.bin"), "NON_MATCHING"u8.ToArray());

        vm.Directories.SourceDir = src;
        vm.Directories.DestinationDir = dst;

        await vm.Execution.SimulateAsync();

        // Miss list export button must now be enabled
        vm.CanExportMissList.Should().BeTrue();

        // Test export miss list click
        var missOut = Path.Combine(_tempDir, "exported_miss.txt");
        dialog.OnPickSaveFile = (_, _, _, _) => missOut;
        await vm.ExportMissListAsync();

        File.Exists(missOut).Should().BeTrue();
        (await File.ReadAllTextAsync(missOut)).Should().Contain("Carrier Command");
    }

    // =========================================================================
    // 21. test_export_dat_button
    // =========================================================================

    [Fact]
    public async Task TestExportDatButton()
    {
        var dialog = new ConfigurableDialogService();
        var vm = CreateViewModel(dialog);
        vm.CanExportDat.Should().BeFalse();

        var src = Path.Combine(_tempDir, "src_test_dat");
        var dst = Path.Combine(_tempDir, "dst_test_dat");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        await File.WriteAllBytesAsync(
            Path.Combine(src, "Super Mario Bros. (1985)(Nintendo)(US).nes"),
            "NES\x1a_sample_rom"u8.ToArray());

        vm.Directories.SourceDir = src;
        vm.Directories.DestinationDir = dst;

        await vm.Execution.SimulateAsync();

        vm.CanExportDat.Should().BeTrue();

        // 1. Export as Logiqx XML
        var xmlOut = Path.Combine(_tempDir, "exported.xml");
        dialog.OnPickSaveFile = (_, _, _, _) => xmlOut;
        await vm.ExportDatAsync();

        File.Exists(xmlOut).Should().BeTrue();
        var xmlContent = await File.ReadAllTextAsync(xmlOut);
        xmlContent.Should().Contain("<datafile>");
        xmlContent.Should().Contain("Super Mario Bros. (1985)(Nintendo)(US).nes");

        // 2. Export as CLRMAMEPRO
        var cmpOut = Path.Combine(_tempDir, "exported.dat");
        dialog.OnPickSaveFile = (_, _, _, _) => cmpOut;
        await vm.ExportDatAsync();

        File.Exists(cmpOut).Should().BeTrue();
        var cmpContent = await File.ReadAllTextAsync(cmpOut);
        cmpContent.Should().Contain("clrmamepro (");
        cmpContent.Should().Contain("Super Mario Bros. (1985)(Nintendo)(US).nes");

        // 3. Verify dialog parameters (default filename and separate XML/DAT filters)
        dialog.PickSaveFileCalls.Should().HaveCount(2);
        var firstCall = dialog.PickSaveFileCalls[0];
        firstCall.DefaultFileName.Should().Be("collection_filtree.xml");
        firstCall.Filters.Should().HaveCount(2);
        firstCall.Filters![0].Extensions.Should().Equal("xml");
        firstCall.Filters[1].Extensions.Should().Equal("dat");
    }

    // =========================================================================
    // 21b. test_export_report_button & test_save_file_picker_options
    // =========================================================================

    [Fact]
    public async Task TestExportReportButton()
    {
        var dialog = new ConfigurableDialogService();
        var vm = CreateViewModel(dialog);
        vm.CanExportReport.Should().BeFalse();

        var src = Path.Combine(_tempDir, "src_test_report");
        var dst = Path.Combine(_tempDir, "dst_test_report");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        await File.WriteAllBytesAsync(
            Path.Combine(src, "Super Mario Bros. (1985)(Nintendo)(US).nes"),
            "NES\x1a_sample_rom"u8.ToArray());

        vm.Directories.SourceDir = src;
        vm.Directories.DestinationDir = dst;

        await vm.Execution.SimulateAsync();

        vm.CanExportReport.Should().BeTrue();

        // 1. Export as JSON
        var jsonOut = Path.Combine(_tempDir, "exported_report.json");
        dialog.OnPickSaveFile = (_, _, _, _) => jsonOut;
        await vm.ExportReportAsync();

        File.Exists(jsonOut).Should().BeTrue();
        var jsonContent = await File.ReadAllTextAsync(jsonOut);
        jsonContent.Should().Contain("Super Mario Bros. (1985)(Nintendo)(US).nes");
        jsonContent.Should().Contain("total_scanned");

        // 2. Export as CSV
        var csvOut = Path.Combine(_tempDir, "exported_report.csv");
        dialog.OnPickSaveFile = (_, _, _, _) => csvOut;
        await vm.ExportReportAsync();

        File.Exists(csvOut).Should().BeTrue();
        var csvContent = await File.ReadAllTextAsync(csvOut);
        csvContent.Should().Contain("source_file;archive_member;source_path");
        csvContent.Should().Contain("Super Mario Bros. (1985)(Nintendo)(US).nes");

        // 3. Export path without extension defaults to .json
        var noExtOut = Path.Combine(_tempDir, "exported_report_noext");
        dialog.OnPickSaveFile = (_, _, _, _) => noExtOut;
        await vm.ExportReportAsync();

        File.Exists(noExtOut + ".json").Should().BeTrue();

        // 4. Cancel dialog (returns null) does not throw and does nothing
        dialog.OnPickSaveFile = (_, _, _, _) => null;
        await vm.ExportReportAsync();
    }

    [Fact]
    public void TestAvaloniaDialogServiceCreateSaveFilePickerOptions()
    {
        AppFileDialogFilter[] reportFilters =
        [
            new("JSON (*.json)", ["json"]),
            new("CSV (*.csv)", ["csv"])
        ];

        var opt1 = AvaloniaDialogService.CreateSaveFilePickerOptions(
            "Export report",
            "rapport_tosec.json",
            reportFilters,
            null);

        opt1.Title.Should().Be("Export report");
        opt1.SuggestedFileName.Should().Be("rapport_tosec");
        opt1.DefaultExtension.Should().Be("json");
        opt1.ShowOverwritePrompt.Should().BeTrue();
        opt1.FileTypeChoices.Should().HaveCount(2);
        opt1.FileTypeChoices![0].Patterns.Should().Contain("*.json");
        opt1.FileTypeChoices[1].Patterns.Should().Contain("*.csv");

        AppFileDialogFilter[] missFilters =
        [
            new("Texte (*.txt)", ["txt"]),
            new("CSV (*.csv)", ["csv"])
        ];

        var opt2 = AvaloniaDialogService.CreateSaveFilePickerOptions(
            "Export miss list",
            "miss_list.txt",
            missFilters,
            null);

        opt2.SuggestedFileName.Should().Be("miss_list");
        opt2.DefaultExtension.Should().Be("txt");

        AppFileDialogFilter[] datFilters =
        [
            new("Logiqx XML DAT (*.dat *.xml)", ["dat", "xml"]),
            new("CLR-MAME-PRO DAT (*.dat)", ["dat"])
        ];

        var opt3 = AvaloniaDialogService.CreateSaveFilePickerOptions(
            "Export DAT",
            "collection_filtree.dat",
            datFilters,
            null);

        opt3.SuggestedFileName.Should().Be("collection_filtree");
        opt3.DefaultExtension.Should().Be("dat");

        // Test without extension in defaultFileName
        var opt4 = AvaloniaDialogService.CreateSaveFilePickerOptions(
            "Export report",
            "my_custom_report",
            reportFilters,
            null);

        opt4.SuggestedFileName.Should().Be("my_custom_report");
        opt4.DefaultExtension.Should().Be("json");
    }

    // =========================================================================
    // 22. test_clear_log_button & test_log_viewer_no_duplicate_bracket_tags
    // =========================================================================

    [Fact]
    public void TestClearLogButton()
    {
        var vm = CreateViewModel();
        vm.Log.Append(LogLevel.Info, LocalizedMessage.Verbatim("Initial test log message"));
        vm.Log.Text.Should().Contain("Initial test log message");

        vm.Log.ClearCommand.Execute(null);
        vm.Log.Text.Should().BeEmpty();
    }

    [Fact]
    public async Task TestLogViewerNoDuplicateBracketTags()
    {
        var vm = CreateViewModel();

        var src = Path.Combine(_tempDir, "src_log_test");
        var dst = Path.Combine(_tempDir, "dst_log_test");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        await File.WriteAllBytesAsync(Path.Combine(src, "Zombie Nation (USA).nes"), "OK_ROM"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(src, "Zoids (Japan).nes"), "SKIP_ROM"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(src, "random_unmatched.txt"), "UNMATCHED"u8.ToArray());

        vm.Directories.SourceDir = src;
        vm.Directories.DestinationDir = dst;
        vm.Priorities.Apply1G1RPreset("usa");
        vm.Priorities.FilterCountries = true;
        vm.Priorities.FallbackUnmatchedCountries = false;

        await vm.Execution.SimulateAsync();

        var plainLog = vm.Log.Text;
        // A simulation writes nothing: what it would write is PLANNED, never OK
        plainLog.Should().Contain("[PLANNED] Zombie Nation (USA).nes");
        plainLog.Should().NotContain("[OK]");
        plainLog.Should().NotContain("[PLANNED] [PLANNED]");
        plainLog.Should().NotContain("[SKIP] [SKIP]");
        plainLog.Should().NotContain("[UNMATCHED] [UNMATCHED]");

        // Also verify defensive stripping if AppendLog is called with a pre-prefixed message
        vm.Log.Clear();
        vm.Log.Append(LogLevel.Skip, LocalizedMessage.Verbatim("[SKIP] Already prefixed"));
        vm.Log.Append(LogLevel.Error, LocalizedMessage.Verbatim("[ERREUR] Transfer failure"));
        var text = vm.Log.Text;
        text.Should().NotContain("[SKIP] [SKIP]");
        text.Should().Contain("[SKIP] Already prefixed");
        text.Should().NotContain("[ERROR] [ERREUR]");
        text.Should().Contain("[ERROR] Transfer failure");
    }

    [Fact]
    public async Task ConventionBadgeAndDatInfo_ShouldTakeTheThemeColorsOfTheirTone()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var win = new MainWindow { DataContext = vm };
            win.Show();
            var badge = win.FindControl<Border>("LblConventionBadge")!;
            var badgeText = (TextBlock)badge.Child!;
            var datInfo = win.FindControl<TextBlock>("LblDatInfo")!;

            badge.Background!.ToString().Should().BeEquivalentTo("#FFF1F5F9", "Auto");
            badge.BorderBrush!.ToString().Should().BeEquivalentTo("#FFCBD5E1");
            badgeText.Foreground!.ToString().Should().BeEquivalentTo("#FF475569");

            vm.Directories.SelectedNamingConvention = NamingConvention.Redump;
            badge.Background!.ToString().Should().BeEquivalentTo("#FFF5F3FF");
            badge.BorderBrush!.ToString().Should().BeEquivalentTo("#FFDDD6FE");
            badgeText.Foreground!.ToString().Should().BeEquivalentTo("#FF6D28D9");

            vm.Directories.DatFilePath = Path.Combine(_tempDir, "missing.dat");
            datInfo.Foreground!.ToString().Should().BeEquivalentTo("#FFC62828");
            win.Close();
        });
    }

    [Fact]
    public async Task TestLogViewerColorsAndInlineFormatting()
    {
        var infoEntry = LogEntryViewModel.Create("INFO", "Démarrage de l'opération : Simulation (Dry-Run)");
        infoEntry.LevelTag.Should().Be("[INFO]");
        infoEntry.SpacedCleanMessage.Should().Be(" Démarrage de l'opération : Simulation (Dry-Run)");

        await RunOnUiThreadAsync(() =>
        {
            // Colors come from the theme palette, through the converter used by the view
            var converter = new RomSieve.App.Converters.ThemeResourceConverter();
            string Color(object tone, string key) =>
                converter.Convert(tone, typeof(Avalonia.Media.IBrush), key, System.Globalization.CultureInfo.InvariantCulture)!.ToString()!;

            Color("OK", "LogLevel*Brush|LogLevelDefaultBrush").Should().BeEquivalentTo("#FF66BB6A");
            Color("SKIP", "LogLevel*Brush|LogLevelDefaultBrush").Should().BeEquivalentTo("#FFFFA726");
            Color("UNMATCHED", "LogLevel*Brush|LogLevelDefaultBrush").Should().BeEquivalentTo("#FFCE93D8");
            Color("WARN", "LogLevel*Brush|LogLevelDefaultBrush").Should().BeEquivalentTo("#FFFFD54F");
            Color("ERROR", "LogLevel*Brush|LogLevelDefaultBrush").Should().BeEquivalentTo("#FFEF5350");
            Color("INFO", "LogLevel*Brush|LogLevelDefaultBrush").Should().BeEquivalentTo("#FF64B5F6");
            Color("DEBUG", "LogLevel*Brush|LogLevelDefaultBrush").Should().BeEquivalentTo("#FF9E9E9E");
            Color(FileStatus.Duplicate, "Status*BadgeForegroundBrush").Should().BeEquivalentTo("#FFB71C1C");
            Color("Tosec", "Badge*BackgroundBrush").Should().BeEquivalentTo("#FFEFF6FF");
            Color("Error", "DatInfo*ForegroundBrush").Should().BeEquivalentTo("#FFC62828");
        });
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var win = new MainWindow { DataContext = vm };
            var logList = win.FindControl<ListBox>("LogViewerList")!;

            logList.Background.Should().NotBeNull();
            logList.Background!.ToString().Should().BeEquivalentTo("#FF1E1E1E");
            logList.Foreground.Should().NotBeNull();
            logList.Foreground!.ToString().Should().BeEquivalentTo("#FFD4D4D4");
        });
    }

    // =========================================================================
    // 23. test_table_preview_column_resize_modes & test_table_preview_sortable_columns
    // =========================================================================

    [Fact]
    public async Task TestTablePreviewColumnResizeModes()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var win = new MainWindow { DataContext = vm };
            var table = win.FindControl<DataGrid>("TablePreview")!;

            table.Columns.Count.Should().Be(11);
            foreach (var col in table.Columns)
            {
                col.CanUserResize.Should().BeTrue();
            }
        });
    }

    [Fact]
    public void TestTablePreviewSortableColumns()
    {
        var vm = CreateViewModel();
        vm.PreviewColumnHeaders.Count.Should().Be(11);
        MainViewModel.SortablePreviewColumns.Should().BeEquivalentTo([0, 2, 3, 4, 5, 6, 7, 9, 10]);

        // Check sort hint tooltips on sortable columns
        foreach (var col in new[] { 0, 2, 3, 4, 5, 6, 7, 9, 10 })
        {
            var tooltip = vm.PreviewColumnTooltips[col].ToLowerInvariant();
            (tooltip.Contains("trier") || tooltip.Contains("sort")).Should().BeTrue();
        }

        // Tooltip should not be present on non-sortable columns 1 (CRC32) and 8 (Chemin cible)
        foreach (var col in new[] { 1, 8 })
        {
            var tooltip = vm.PreviewColumnTooltips[col].ToLowerInvariant();
            tooltip.Should().NotContain("trier");
            tooltip.Should().NotContain("sort");
        }

        // Populate 3 dummy rows
        var filterService = RomFilterService.CreateDefault();
        (string Src, string Crc, string Title, string Disk, string Pub, string Date, string Lang, string Country, string Target, string Status)[] rowsData =
        [
            ("Zool (1993)(Gremlin).adf", "11111111", "Zool", "", "Gremlin", "1993", "en", "GB", "Zool/Zool.adf", "Valid"),
            ("Alien Breed (1991)(Team17).adf", "22222222", "Alien Breed", "1 sur 2", "Team17", "1991", "en, fr, de", "EU", "Alien/Alien.adf", "Excluded"),
            ("Monkey Island (1990)(LucasArts).adf", "33333333", "Monkey Island", "2 sur 2", "LucasArts", "1990", "fr", "FR", "Monkey/Monkey.adf", "Duplicate")
        ];

        foreach (var r in rowsData)
        {
            var rowVm = new PreviewItemViewModel(
                new ProcessItem
                {
                    SourcePath = r.Src,
                    FileSize = 1024,
                    Parsed = new ParsedRomName { RawFilename = r.Src, Title = r.Title }
                },
                vm.Localization,
                filterService)
            {
                SourceName = r.Src,
                Crc32Text = r.Crc,
                TitleDisplay = r.Title,
                DiskDisplay = r.Disk,
                Publisher = r.Pub,
                Date = r.Date,
                LanguagesDisplay = r.Lang,
                CountriesDisplay = r.Country,
                TargetPathDisplay = r.Target,
                StatusDisplay = r.Status
            };
            vm.AllPreviewItems.Add(rowVm);
        }

        // Click column 1 (CRC32) -> Not sortable, should do nothing
        vm.SortPreviewByColumn(1);
        vm.IsSortIndicatorShown.Should().BeFalse();
        vm.AllPreviewItems[0][0].Should().Be("Zool (1993)(Gremlin).adf");

        // Click column 8 (Chemin cible) -> Not sortable, should do nothing
        vm.SortPreviewByColumn(8);
        vm.IsSortIndicatorShown.Should().BeFalse();
        vm.AllPreviewItems[0][0].Should().Be("Zool (1993)(Gremlin).adf");

        // Click column 0 (Nom d'origine) -> Sort Ascending
        vm.SortPreviewByColumn(0);
        vm.IsSortIndicatorShown.Should().BeTrue();
        vm.CurrentSortColumn.Should().Be(0);
        vm.IsSortAscending.Should().BeTrue();
        vm.AllPreviewItems[0][0].Should().Be("Alien Breed (1991)(Team17).adf");
        vm.AllPreviewItems[1][0].Should().Be("Monkey Island (1990)(LucasArts).adf");
        vm.AllPreviewItems[2][0].Should().Be("Zool (1993)(Gremlin).adf");

        // Click column 0 again -> Toggle to Descending
        vm.SortPreviewByColumn(0);
        vm.IsSortAscending.Should().BeFalse();
        vm.AllPreviewItems[0][0].Should().Be("Zool (1993)(Gremlin).adf");
        vm.AllPreviewItems[2][0].Should().Be("Alien Breed (1991)(Team17).adf");

        // Click column 5 (Date) -> Ascending
        vm.SortPreviewByColumn(5);
        vm.CurrentSortColumn.Should().Be(5);
        vm.IsSortAscending.Should().BeTrue();
        vm.AllPreviewItems[0][5].Should().Be("1990");
        vm.AllPreviewItems[1][5].Should().Be("1991");
        vm.AllPreviewItems[2][5].Should().Be("1993");

        // Click column 5 again -> Descending
        vm.SortPreviewByColumn(5);
        vm.IsSortAscending.Should().BeFalse();
        vm.AllPreviewItems[0][5].Should().Be("1993");
        vm.AllPreviewItems[2][5].Should().Be("1990");

        // Click column 4 (Publisher) -> Ascending
        vm.SortPreviewByColumn(4);
        vm.CurrentSortColumn.Should().Be(4);
        vm.IsSortAscending.Should().BeTrue();
        vm.AllPreviewItems[0][4].Should().Be("Gremlin");
        vm.AllPreviewItems[1][4].Should().Be("LucasArts");
        vm.AllPreviewItems[2][4].Should().Be("Team17");

        // Click column 2 (Title) -> Ascending
        vm.SortPreviewByColumn(2);
        vm.CurrentSortColumn.Should().Be(2);
        vm.IsSortAscending.Should().BeTrue();
        vm.AllPreviewItems[0][2].Should().Be("Alien Breed");

        // Click column 3 (Disk) -> Ascending
        vm.SortPreviewByColumn(3);
        vm.CurrentSortColumn.Should().Be(3);
        vm.IsSortAscending.Should().BeTrue();
        vm.AllPreviewItems[0][3].Should().Be("");
        vm.AllPreviewItems[1][3].Should().Be("1 sur 2");
        vm.AllPreviewItems[2][3].Should().Be("2 sur 2");

        // Click column 6 (Languages) -> Ascending
        vm.SortPreviewByColumn(6);
        vm.CurrentSortColumn.Should().Be(6);
        vm.IsSortAscending.Should().BeTrue();
        vm.AllPreviewItems[0][6].Should().Be("en");
        vm.AllPreviewItems[1][6].Should().Be("en, fr, de");
        vm.AllPreviewItems[2][6].Should().Be("fr");

        // Click column 7 (Country/Region) -> Ascending
        vm.SortPreviewByColumn(7);
        vm.CurrentSortColumn.Should().Be(7);
        vm.IsSortAscending.Should().BeTrue();
        vm.AllPreviewItems[0][7].Should().Be("EU");
        vm.AllPreviewItems[1][7].Should().Be("FR");
        vm.AllPreviewItems[2][7].Should().Be("GB");

        // Click column 9 (Status) -> Ascending
        vm.SortPreviewByColumn(9);
        vm.CurrentSortColumn.Should().Be(9);
        vm.IsSortAscending.Should().BeTrue();
        vm.AllPreviewItems[0][9].Should().Be("Duplicate");
        vm.AllPreviewItems[1][9].Should().Be("Excluded");
        vm.AllPreviewItems[2][9].Should().Be("Valid");
    }

    // =========================================================================
    // 24. test_table_preview_languages_and_country_columns & default_sort
    // =========================================================================

    [Fact]
    public async Task TestTablePreviewLanguagesAndCountryColumns()
    {
        var vm = CreateViewModel();
        vm.PreviewColumnHeaders[2].Should().Be("Titre");
        vm.PreviewColumnHeaders[3].Should().Be("Média");
        vm.PreviewColumnHeaders[6].Should().Be("Langues");
        vm.PreviewColumnHeaders[7].Should().Be("Pays/Région");

        var src = Path.Combine(_tempDir, "src_cols");
        var dst = Path.Combine(_tempDir, "dst_cols");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        await File.WriteAllBytesAsync(Path.Combine(src, "Asterix (Europe) (En,Fr,De).sfc"), "AST"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(src, "Zoda's Revenge (USA, Europe).nes"), "ZODA"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(src, "Settlers, The (1993)(Blue Byte)(DE-FR)(de-fr).adf"), "SET"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(src, "Shadow of the Beast (1989)(Psygnosis)(Disk 1 of 2).adf"), "SOTB1"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(src, "Shadow of the Beast (1989)(Psygnosis)(Disk 2 of 2).adf"), "SOTB2"u8.ToArray());

        vm.Directories.SourceDir = src;
        vm.Directories.DestinationDir = dst;

        await vm.Execution.SimulateAsync();

        vm.AllPreviewItems.Count.Should().Be(5);

        // Row 0: Asterix (Europe) (En,Fr,De).sfc
        vm.AllPreviewItems[0][0].Should().Be("Asterix (Europe) (En,Fr,De).sfc");
        vm.AllPreviewItems[0][3].Should().BeEmpty();
        vm.AllPreviewItems[0][6].Should().Be("en, fr, de");
        vm.AllPreviewItems[0][7].Should().Be("EU");

        // Row 1: Settlers, The (1993)(Blue Byte)(DE-FR)(de-fr).adf
        vm.AllPreviewItems[1][0].Should().Be("Settlers, The (1993)(Blue Byte)(DE-FR)(de-fr).adf");
        vm.AllPreviewItems[1][3].Should().BeEmpty();
        vm.AllPreviewItems[1][6].Should().Be("de, fr");
        vm.AllPreviewItems[1][7].Should().Be("DE, FR");

        // Row 2: Shadow of the Beast (1989)(Psygnosis)(Disk 1 of 2).adf
        vm.AllPreviewItems[2][0].Should().Be("Shadow of the Beast (1989)(Psygnosis)(Disk 1 of 2).adf");
        vm.AllPreviewItems[2][2].Should().Be("Shadow of the Beast");
        vm.AllPreviewItems[2][3].Should().Be("1 sur 2");

        // Row 3: Shadow of the Beast (1989)(Psygnosis)(Disk 2 of 2).adf
        vm.AllPreviewItems[3][0].Should().Be("Shadow of the Beast (1989)(Psygnosis)(Disk 2 of 2).adf");
        vm.AllPreviewItems[3][2].Should().Be("Shadow of the Beast");
        vm.AllPreviewItems[3][3].Should().Be("2 sur 2");

        // Row 4: Zoda's Revenge (USA, Europe).nes
        vm.AllPreviewItems[4][0].Should().Be("Zoda's Revenge (USA, Europe).nes");
        vm.AllPreviewItems[4][3].Should().BeEmpty();
        vm.AllPreviewItems[4][6].Should().Be("en");
        vm.AllPreviewItems[4][7].Should().Be("US, EU");
    }

    [Fact]
    public async Task TestTablePreviewDefaultSortOnWorkerFinished()
    {
        var vm = CreateViewModel();

        var src = Path.Combine(_tempDir, "src_sort_test");
        var dst = Path.Combine(_tempDir, "dst_sort_test");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        await File.WriteAllBytesAsync(Path.Combine(src, "Zool (1993)(Gremlin).adf"), "ZOOL"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(src, "Alien Breed (1991)(Team17).adf"), "ALIEN"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(src, "Flashback (1992)(Delphine).adf"), "FLASHBACK"u8.ToArray());

        vm.Directories.SourceDir = src;
        vm.Directories.DestinationDir = dst;

        await vm.Execution.SimulateAsync();

        vm.AllPreviewItems.Count.Should().Be(3);
        vm.IsSortIndicatorShown.Should().BeTrue();
        vm.CurrentSortColumn.Should().Be(0);
        vm.IsSortAscending.Should().BeTrue();

        vm.AllPreviewItems[0][0].Should().Be("Alien Breed (1991)(Team17).adf");
        vm.AllPreviewItems[1][0].Should().Be("Flashback (1992)(Delphine).adf");
        vm.AllPreviewItems[2][0].Should().Be("Zool (1993)(Gremlin).adf");
    }

    // =========================================================================
    // 25. test_country_priority_widget_and_1g1r_presets
    // =========================================================================

    [Fact]
    public void TestCountryPriorityWidgetAnd1G1RPresets()
    {
        var vm = CreateViewModel();
        vm.Directories.SourceDir = Path.Combine(_tempDir, "src");
        vm.Directories.DestinationDir = Path.Combine(_tempDir, "dst");

        // Default state
        vm.Priorities.CountryPriority.GetPriorityCodes().Should().BeEmpty();
        vm.Priorities.FilterCountries.Should().BeFalse();

        // Bidirectional sync with CountriesText (uk -> GB normalization)
        vm.Priorities.CountriesText = "fr, eu, us, uk";
        vm.Priorities.CountryPriority.GetPriorityCodes().Should().Equal("FR", "EU", "US", "GB");

        // Preset France
        vm.Priorities.Apply1G1RPreset("france");
        vm.Priorities.LanguagePriority.GetPriorityCodes().Should().Equal("fr", "m", "en");
        vm.Priorities.CountryPriority.GetPriorityCodes().Should().Equal("FR", "EU", "WO", "US", "GB", "CA", "AU", "JP");

        // Preset USA
        vm.Priorities.Apply1G1RPreset("usa");
        vm.Priorities.LanguagePriority.GetPriorityCodes().Should().Equal("en", "m");
        vm.Priorities.CountryPriority.GetPriorityCodes().Should().Equal("US", "WO", "CA", "EU", "GB", "AU", "JP");

        var cfg = vm.Execution.BuildConfig()!;
        cfg.Filters.PriorityCountries.Should().Equal("US", "WO", "CA", "EU", "GB", "AU", "JP");
        cfg.Filters.RegionPriorityMode.Should().Be(RegionPriorityMode.LanguageFirst);
    }

    // =========================================================================
    // 26. test_1080p_compact_geometry_and_convention_presets
    // =========================================================================

    [Fact]
    public async Task Test1080pCompactGeometryAndConventionPresets()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var win = new MainWindow { DataContext = vm };

            // Window default and minimum heights must fit on a 1080p display
            vm.WindowHeight.Should().BeLessOrEqualTo(720);
            vm.MinimumWindowHeight.Should().BeLessOrEqualTo(600);
            win.FindControl<Border>("LblConventionBadge")!.MaxHeight.Should().BeLessOrEqualTo(24);
            vm.Directories.UpdateConventionBadge(NamingConvention.NoIntro);
            vm.Directories.ConventionBadgeText.Should().Contain("No-Intro");

            // Test pattern presets for Redump/No-Intro vs TOSEC
            vm.Directories.PresetNoIntroRegionCommand.Execute(null);
            vm.Directories.Pattern.Should().Be("{country}/{title}/");

            vm.Directories.PresetNoIntroLetterCommand.Execute(null);
            vm.Directories.Pattern.Should().Be("{letter}/{title}/");

            vm.Directories.ResetPatternCommand.Execute(null);
            vm.Directories.Pattern.Should().Be("{publisher}/{year}/{title}/");

            // Switching convention shows the options of the matching convention in the filters tab
            vm.Navigation.CurrentStep = WorkflowStep.Filters;
            vm.Directories.SelectedNamingConvention = NamingConvention.Redump;
            vm.Filters.IsNoIntroShown.Should().BeTrue();
            vm.Navigation.CurrentStep.Should().Be(WorkflowStep.Filters);

            vm.Directories.SelectedNamingConvention = NamingConvention.Tosec;
            vm.Filters.IsTosecShown.Should().BeTrue();
        });
    }

    // =========================================================================
    // 27. test_stats_banner_clickable_filter_buttons
    // =========================================================================

    [Fact]
    public void TestStatsBannerClickableFilterButtons()
    {
        var vm = CreateViewModel();
        var sb = vm.StatsBanner;

        // Initial state: Total is checked, all status filter buttons are unchecked
        sb.TotalChip.IsChecked.Should().BeTrue();
        sb.ValidChip.IsChecked.Should().BeFalse();
        sb.ExcludedChip.IsChecked.Should().BeFalse();
        sb.UnmatchedChip.IsChecked.Should().BeFalse();
        sb.DuplicateChip.IsChecked.Should().BeFalse();
        sb.ActiveFilters.Should().BeEmpty();

        // Populate 4 items (1 valid, 1 excluded, 1 unmatched, 1 duplicate)
        ProcessItem[] sampleItems =
        [
            new()
            {
                SourcePath = "A_Valid.adf",
                FileSize = 1024,
                Parsed = new ParsedRomName { RawFilename = "A_Valid.adf", Title = "A Valid", IsTosecConform = true },
                Status = FileStatus.Valid
            },
            new()
            {
                SourcePath = "B_Excluded.adf",
                FileSize = 1024,
                Parsed = new ParsedRomName { RawFilename = "B_Excluded.adf", Title = "B Excluded", IsTosecConform = true },
                Status = FileStatus.Excluded,
                StatusReason = LocalizedMessage.Create("reason.bad_dump_excluded")
            },
            new()
            {
                SourcePath = "C_Unmatched.adf",
                FileSize = 1024,
                Parsed = new ParsedRomName { RawFilename = "C_Unmatched.adf", Title = "C Unmatched", IsTosecConform = false },
                Status = FileStatus.Unmatched
            },
            new()
            {
                SourcePath = "D_Duplicate.adf",
                FileSize = 1024,
                Parsed = new ParsedRomName { RawFilename = "D_Duplicate.adf", Title = "D Duplicate", IsTosecConform = true },
                Status = FileStatus.Duplicate
            }
        ];

        foreach (var it in sampleItems)
        {
            vm.ProcessSingleItemForPreview(it);
        }

        vm.AllPreviewItems.Count.Should().Be(4);
        vm.AllPreviewItems[0][9].Should().Be("Retenu");
        vm.AllPreviewItems[1][9].Should().Be("Exclu (Bad Dump [b] exclu)");
        vm.AllPreviewItems[2][9].Should().Be("Non conforme");
        vm.AllPreviewItems[3][9].Should().Be("Doublon");
        for (var r = 0; r < 4; r++)
        {
            vm.IsPreviewRowHidden(r).Should().BeFalse();
        }

        // 1. Click "Exclus" -> only row 1 (B_Excluded.adf) is visible
        sb.ExcludedChip.Click();
        sb.ExcludedChip.IsChecked.Should().BeTrue();
        sb.TotalChip.IsChecked.Should().BeFalse();
        sb.ActiveFilters.Should().BeEquivalentTo(["excluded"]);
        vm.IsPreviewRowHidden(0).Should().BeTrue();
        vm.IsPreviewRowHidden(1).Should().BeFalse();
        vm.IsPreviewRowHidden(2).Should().BeTrue();
        vm.IsPreviewRowHidden(3).Should().BeTrue();

        // 2. Click "Non conformes" while "Exclus" is still pressed -> rows 1 and 2 are visible
        sb.UnmatchedChip.Click();
        sb.ExcludedChip.IsChecked.Should().BeTrue();
        sb.UnmatchedChip.IsChecked.Should().BeTrue();
        sb.TotalChip.IsChecked.Should().BeFalse();
        sb.ActiveFilters.Should().BeEquivalentTo(["excluded", "unmatched"]);
        vm.IsPreviewRowHidden(0).Should().BeTrue();
        vm.IsPreviewRowHidden(1).Should().BeFalse();
        vm.IsPreviewRowHidden(2).Should().BeFalse();
        vm.IsPreviewRowHidden(3).Should().BeTrue();

        // 3. Click "Total" -> deactivates all other pressed buttons and shows all rows
        sb.TotalChip.Click();
        sb.TotalChip.IsChecked.Should().BeTrue();
        sb.ValidChip.IsChecked.Should().BeFalse();
        sb.ExcludedChip.IsChecked.Should().BeFalse();
        sb.UnmatchedChip.IsChecked.Should().BeFalse();
        sb.DuplicateChip.IsChecked.Should().BeFalse();
        sb.ActiveFilters.Should().BeEmpty();
        for (var r = 0; r < 4; r++)
        {
            vm.IsPreviewRowHidden(r).Should().BeFalse();
        }

        // 4. Click "Retenus", then unclick "Retenus" -> automatically re-activates "Total" and shows all rows
        sb.ValidChip.Click();
        sb.ValidChip.IsChecked.Should().BeTrue();
        sb.TotalChip.IsChecked.Should().BeFalse();
        vm.IsPreviewRowHidden(0).Should().BeFalse();
        vm.IsPreviewRowHidden(1).Should().BeTrue();

        sb.ValidChip.Click();
        sb.ValidChip.IsChecked.Should().BeFalse();
        sb.TotalChip.IsChecked.Should().BeTrue();
        for (var r = 0; r < 4; r++)
        {
            vm.IsPreviewRowHidden(r).Should().BeFalse();
        }
    }

    // =========================================================================
    // 28. test_browse_buttons_initial_directory
    // =========================================================================

    [Fact]
    public async Task TestBrowseButtonsInitialDirectory()
    {
        var dialog = new ConfigurableDialogService();
        var vm = CreateViewModel(dialog);

        // 1. When fields are empty, initial directory should be ""
        await vm.Directories.BrowseSourceAsync();
        await vm.Directories.BrowseDestinationAsync();
        await vm.Directories.BrowseDatAsync();

        dialog.PickFolderCalls[0].InitialDir.Should().BeEmpty();
        dialog.PickFolderCalls[1].InitialDir.Should().BeEmpty();
        dialog.PickOpenFileCalls[0].InitialDir.Should().BeEmpty();

        // 2. When a directory is already present in Source and Destination, and a DAT path/dir in DAT
        var srcDir = Path.Combine(_tempDir, "existing_source");
        var dstDir = Path.Combine(_tempDir, "existing_dest");
        var datDir = Path.Combine(_tempDir, "existing_dats");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir);
        Directory.CreateDirectory(datDir);
        var datFile = Path.Combine(datDir, "amiga.dat");
        await File.WriteAllTextAsync(datFile, "clrmamepro ()");

        vm.Directories.SourceDir = srcDir;
        vm.Directories.DestinationDir = dstDir;
        vm.Directories.DatFilePath = datFile;

        dialog.PickFolderCalls.Clear();
        dialog.PickOpenFileCalls.Clear();

        await vm.Directories.BrowseSourceAsync();
        await vm.Directories.BrowseDestinationAsync();
        await vm.Directories.BrowseDatAsync();

        dialog.PickFolderCalls[0].InitialDir.Should().Be(srcDir);
        dialog.PickFolderCalls[1].InitialDir.Should().Be(dstDir);
        dialog.PickOpenFileCalls[0].InitialDir.Should().Be(datDir);
    }

    // =========================================================================
    // 29. test_progress_bar_geometry_stability_and_smooth_updates
    // =========================================================================

    [Fact]
    public void TestProgressBarGeometryStabilityAndSmoothUpdates()
    {
        var vm = CreateViewModel();

        // Short filename at 1 / 3988
        vm.Execution.ReportProgress(1, 3988, 1024, 100000, 0.0, LocalizedMessage.Verbatim("Traitement : A.sfc"));
        vm.Execution.ProgressPercentage.Should().Be(0);

        // Very long filename at 50% (1994 / 3988)
        const string longMsg = "Traitement : [BIOS] Super NES CD-ROM - Boot ROM (Japan) (En) (v0.95) (Proto) (Extra Long Title).sfc";
        vm.Execution.ReportProgress(1994, 3988, 50000, 100000, 12.5, LocalizedMessage.Verbatim(longMsg));
        vm.Execution.ProgressPercentage.Should().Be(50);
        vm.Execution.RawStatusMessage.Should().Be(longMsg);
        vm.Execution.StatusMessage.Length.Should().BeLessThan(longMsg.Length);

        // Final item at 100% (3988 / 3988)
        vm.Execution.ReportProgress(3988, 3988, 100000, 100000, 0.0, LocalizedMessage.Verbatim("Traitement : Zoop (USA).sfc"));
        vm.Execution.ProgressPercentage.Should().Be(100);
    }

    // =========================================================================
    // 30. test_groupbox_titles_not_truncated
    // =========================================================================

    [Fact]
    public void TestGroupBoxTitlesNotTruncated()
    {
        var vm = CreateViewModel();
        vm.Navigation.CurrentStep = WorkflowStep.Languages;

        vm.Priorities.GroupLanguagesTitle.Should().Contain("Langues prioritaires");
        vm.Priorities.GroupCountriesTitle.Should().Contain("Pays et régions prioritaires");
    }

    // =========================================================================
    // 31. test_preview_title_search_and_autocompletion
    // =========================================================================

    [Fact]
    public async Task TestPreviewTitleSearchAndAutocompletion()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var win = new MainWindow { DataContext = vm };
            var clearBtn = win.FindControl<Button>("BtnClearSearchPreview")!;
            clearBtn.Content.Should().Be("✕");

            var searchBox = win.FindControl<AutoCompleteBox>("EditSearchPreview")!;
            searchBox.FilterMode.Should().Be(AutoCompleteFilterMode.Contains);

            ProcessItem[] sampleItems =
            [
                new()
                {
                    SourcePath = "Nitro (1990)(Psygnosis)(Disk 1 of 2).adf",
                    FileSize = 1024,
                    Parsed = new ParsedRomName { RawFilename = "Nitro (1990)(Psygnosis)(Disk 1 of 2).adf", Title = "Nitro", IsTosecConform = true },
                    Status = FileStatus.Valid
                },
                new()
                {
                    SourcePath = "Nitro (1990)(Psygnosis)[extended adf].adf",
                    FileSize = 1024,
                    Parsed = new ParsedRomName { RawFilename = "Nitro (1990)(Psygnosis)[extended adf].adf", Title = "Nitro", IsTosecConform = true },
                    Status = FileStatus.Excluded
                },
                new()
                {
                    SourcePath = "Turrican II (1991)(Rainbow Arts).adf",
                    FileSize = 1024,
                    Parsed = new ParsedRomName { RawFilename = "Turrican II (1991)(Rainbow Arts).adf", Title = "Turrican II", IsTosecConform = true },
                    Status = FileStatus.Valid
                },
                new()
                {
                    SourcePath = "Pokémon Version Rouge (France).gb",
                    FileSize = 1024,
                    Parsed = new ParsedRomName { RawFilename = "Pokémon Version Rouge (France).gb", Title = "Pokémon Version Rouge", IsTosecConform = true },
                    Status = FileStatus.Valid
                }
            ];

            foreach (var it in sampleItems)
            {
                vm.ProcessSingleItemForPreview(it);
            }

            // Completer model must contain unique sorted titles
            vm.PreviewKnownTitles.Should().Equal(
                "Nitro",
                "Pokémon Version Rouge",
                "Turrican II");

            // The search box reads these titles when the user types (it is not bound to the live list)
            searchBox.AsyncPopulator.Should().NotBeNull();
            searchBox.AsyncPopulator!("nit", default).GetAwaiter().GetResult().Should().Equal(
                "Nitro",
                "Pokémon Version Rouge",
                "Turrican II");

            // 1. Incremental search on "nit" -> only the 2 Nitro rows are visible
            vm.PreviewSearchText = "nit";
            vm.IsPreviewRowHidden(0).Should().BeFalse();
            vm.IsPreviewRowHidden(1).Should().BeFalse();
            vm.IsPreviewRowHidden(2).Should().BeTrue();
            vm.IsPreviewRowHidden(3).Should().BeTrue();

            // 2. Combine with StatsBanner "Retenus" filter -> only row 0 (VALID Nitro) is visible
            vm.StatsBanner.ValidChip.Click();
            vm.IsPreviewRowHidden(0).Should().BeFalse();
            vm.IsPreviewRowHidden(1).Should().BeTrue();
            vm.IsPreviewRowHidden(2).Should().BeTrue();
            vm.IsPreviewRowHidden(3).Should().BeTrue();

            // Reset StatsBanner to Total -> both Nitro rows visible again
            vm.StatsBanner.TotalChip.Click();
            vm.IsPreviewRowHidden(0).Should().BeFalse();
            vm.IsPreviewRowHidden(1).Should().BeFalse();
            vm.IsPreviewRowHidden(2).Should().BeTrue();
            vm.IsPreviewRowHidden(3).Should().BeTrue();

            // 3. Accent-insensitive search ("pokemon" matches "Pokémon Version Rouge")
            vm.PreviewSearchText = "pokemon";
            vm.IsPreviewRowHidden(0).Should().BeTrue();
            vm.IsPreviewRowHidden(1).Should().BeTrue();
            vm.IsPreviewRowHidden(2).Should().BeTrue();
            vm.IsPreviewRowHidden(3).Should().BeFalse();

            // 4. Clicking the "✕" button at the end of the search zone clears the filter and restores all rows
            vm.ClearPreviewSearchCommand.Execute(null);
            vm.PreviewSearchText.Should().BeEmpty();
            for (var r = 0; r < 4; r++)
            {
                vm.IsPreviewRowHidden(r).Should().BeFalse();
            }
        });
    }

    // =========================================================================
    // 32. test_nointro_exclude_rereleases_ui_and_persistence
    // =========================================================================

    [Fact]
    public async Task TestNoIntroExcludeRereleasesUiAndPersistence()
    {
        var vm = CreateViewModel();
        vm.Directories.SourceDir = Path.Combine(_tempDir, "src");
        vm.Directories.DestinationDir = Path.Combine(_tempDir, "dst");

        vm.NoIntroFilters.ExcludeRereleases.Should().BeFalse();

        var cfgDefault = vm.Execution.BuildConfig();
        cfgDefault.Should().NotBeNull();
        cfgDefault!.Filters.NoIntroExcludeRereleases.Should().BeFalse();

        vm.NoIntroFilters.ExcludeRereleases = true;
        var cfgChecked = vm.Execution.BuildConfig();
        cfgChecked.Should().NotBeNull();
        cfgChecked!.Filters.NoIntroExcludeRereleases.Should().BeTrue();

        await vm.SaveConfigurationAsync();

        var vm2 = CreateViewModel();
        vm2.NoIntroFilters.ExcludeRereleases.Should().BeTrue();
    }

    // =========================================================================
    // 33. test_table_preview_context_menu_manual_1g1r_override
    // =========================================================================

    [Fact]
    public async Task TestTablePreviewContextMenuManual1G1ROverride()
    {
        var vm = CreateViewModel();

        var src = Path.Combine(_tempDir, "src_ctx_1g1r");
        var dst = Path.Combine(_tempDir, "dst_ctx_1g1r");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        const string fFr = "Another World (1991)(Delphine)(fr).adf";
        const string fEn = "Another World (1991)(Delphine)(en).adf";
        const string fOther = "Zool (1993)(Gremlin)(en).adf";
        await File.WriteAllBytesAsync(Path.Combine(src, fFr), "AW_FR"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(src, fEn), "AW_EN"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(src, fOther), "ZOOL"u8.ToArray());

        vm.Directories.SourceDir = src;
        vm.Directories.DestinationDir = dst;
        vm.Priorities.Apply1G1RPreset("france");

        await vm.Execution.SimulateAsync();

        vm.AllPreviewItems.Count.Should().Be(3);
        // Sorted alphabetically by column 0:
        // Row 0: Another World (en) -> EXCLUDED
        // Row 1: Another World (fr) -> VALID
        // Row 2: Zool (en) -> VALID
        vm.AllPreviewItems[0][0].Should().Be(fEn);
        vm.GetRowStatus(0).Should().Be("excluded");
        vm.AllPreviewItems[1][0].Should().Be(fFr);
        vm.GetRowStatus(1).Should().Be("valid");

        // Build context menu for Row 0 (the excluded English version)
        var menuRow0 = vm.BuildPreviewContextMenuForRow(0);
        var actionsByName = menuRow0.ToDictionary(a => a.Name, a => a);
        actionsByName.Should().ContainKey("act_force_1g1r_winner");
        actionsByName.Should().ContainKey("act_filter_this_game");
        actionsByName.Should().NotContainKey("act_reset_1g1r_override");

        // Activate the "Exclus" chip first to verify that isolating a game resets status filters to Total
        vm.StatsBanner.ExcludedChip.Click();
        vm.StatsBanner.ActiveFilters.Should().BeEquivalentTo(["excluded"]);
        vm.IsPreviewRowHidden(1).Should().BeTrue(); // Valid row hidden while "Exclus" filter is active

        // Trigger "Isolate all versions of Another World"
        actionsByName["act_filter_this_game"].Trigger();
        vm.StatsBanner.ActiveFilters.Should().BeEmpty();
        vm.StatsBanner.TotalChip.IsChecked.Should().BeTrue();
        vm.PreviewSearchText.Should().Be("Another World");
        vm.IsPreviewRowHidden(0).Should().BeFalse();
        vm.IsPreviewRowHidden(1).Should().BeFalse();
        vm.IsPreviewRowHidden(2).Should().BeTrue(); // Zool is hidden

        // Trigger "Keep this version as 1G1R winner" on Row 0 (English version)
        actionsByName["act_force_1g1r_winner"].Trigger();

        // Row 0 (EN) must immediately flip to VALID with manual override reason, and Row 1 (FR) to EXCLUDED!
        vm.GetRowStatus(0).Should().Be("valid");
        vm.AllPreviewItems[0][9].Should().Contain("choix manuel forcé");
        vm.GetRowStatus(1).Should().Be("excluded");
        vm.AllPreviewItems[1][9].Should().Contain("choix manuel de l'utilisateur");

        // Re-open context menu on Row 0: reset and clear-all actions must now be available
        var menuAfterForce = vm.BuildPreviewContextMenuForRow(0);
        var actionsAfter = menuAfterForce.ToDictionary(a => a.Name, a => a);
        actionsAfter.Should().ContainKey("act_reset_1g1r_override");
        actionsAfter.Should().ContainKey("act_clear_all_1g1r_overrides");
        actionsAfter.Should().ContainKey("act_clear_game_filter");

        // Trigger "Restore automatic 1G1R choice for this game" -> FR becomes VALID again, EN becomes EXCLUDED
        actionsAfter["act_reset_1g1r_override"].Trigger();
        vm.GetRowStatus(0).Should().Be("excluded");
        vm.GetRowStatus(1).Should().Be("valid");
        vm.Manual1G1RWinners.Should().BeEmpty();
    }

    [Fact]
    public async Task ManualWinners_ShouldBeClearedWhenTheUserChangesTheSourceButNotWhenAConfigurationIsLoaded()
    {
        var vm = CreateViewModel();
        var src = Path.Combine(_tempDir, "src_reset");
        var dst = Path.Combine(_tempDir, "dst_reset");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        await File.WriteAllBytesAsync(Path.Combine(src, "Another World (1991)(Delphine)(fr).adf"), "AW_FR"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(src, "Another World (1991)(Delphine)(en).adf"), "AW_EN"u8.ToArray());
        vm.Directories.SourceDir = src;
        vm.Directories.DestinationDir = dst;
        vm.Priorities.Apply1G1RPreset("france");
        await vm.Execution.SimulateAsync();

        vm.Force1G1RWinner(vm.AllPreviewItems.First(r => r.Item.Status != FileStatus.Valid));
        vm.HasManual1G1ROverrides.Should().BeTrue();

        // Loading a configuration sets the source directory without discarding the manual choices
        vm.ApplyConfigurationDictionary(new Dictionary<string, object?> { ["source_dir"] = Path.Combine(_tempDir, "other_src") });
        vm.HasManual1G1ROverrides.Should().BeTrue();

        vm.Directories.SourceDir = src;
        vm.HasManual1G1ROverrides.Should().BeFalse();
    }

    // =========================================================================
    // 33b. Dynamic Context Menu Visibility (1G1R override & search filter states)
    // =========================================================================

    [Fact]
    public async Task TestTablePreviewDynamicContextMenuVisibility()
    {
        var vm = CreateViewModel();

        var src = Path.Combine(_tempDir, "src_ctx_dyn");
        var dst = Path.Combine(_tempDir, "dst_ctx_dyn");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        const string fFr = "Another World (1991)(Delphine)(fr).adf";
        const string fEn = "Another World (1991)(Delphine)(en).adf";
        const string fOther = "Zool (1993)(Gremlin)(en).adf";
        await File.WriteAllBytesAsync(Path.Combine(src, fFr), "AW_FR"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(src, fEn), "AW_EN"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(src, fOther), "ZOOL"u8.ToArray());

        vm.Directories.SourceDir = src;
        vm.Directories.DestinationDir = dst;
        vm.Priorities.Apply1G1RPreset("france");

        await vm.Execution.SimulateAsync();

        vm.AllPreviewItems.Count.Should().Be(3);

        // 1. Initial state: excluded row selected, no manual override, no active search filter
        vm.SelectedPreviewItem = vm.AllPreviewItems[0];
        vm.CanForce1G1RWinner.Should().BeTrue();
        vm.CanResetSelected1G1ROverride.Should().BeFalse();
        vm.HasManual1G1ROverrides.Should().BeFalse();
        vm.CanIsolateSelectedGame.Should().BeTrue();
        vm.HasPreviewSearchFilter.Should().BeFalse();
        vm.CanShowPreviewContextMenuSeparator.Should().BeTrue();
        vm.CanOpenPreviewContextMenu.Should().BeTrue();

        // 2. Unselected state: no row selected
        vm.SelectedPreviewItem = null;
        vm.CanForce1G1RWinner.Should().BeFalse();
        vm.CanResetSelected1G1ROverride.Should().BeFalse();
        vm.CanIsolateSelectedGame.Should().BeFalse();
        vm.CanShowPreviewContextMenuSeparator.Should().BeFalse();
        vm.CanOpenPreviewContextMenu.Should().BeFalse();

        // 3. Re-select Row 0 and force 1G1R winner
        vm.SelectedPreviewItem = vm.AllPreviewItems[0];
        vm.Force1G1RWinner(vm.SelectedPreviewItem);

        vm.CanForce1G1RWinner.Should().BeTrue();
        vm.CanResetSelected1G1ROverride.Should().BeTrue();
        vm.HasManual1G1ROverrides.Should().BeTrue();
        vm.CanShowPreviewContextMenuSeparator.Should().BeTrue();

        // 4. Activate search filter by isolating game
        vm.IsolateGameInPreview(vm.SelectedPreviewItem);
        vm.HasPreviewSearchFilter.Should().BeTrue();
        vm.PreviewSearchText.Should().Be("Another World");

        // 5. Clear search filter
        vm.ClearPreviewSearch();
        vm.HasPreviewSearchFilter.Should().BeFalse();

        // 6. Reset 1G1R override for this game
        vm.Reset1G1ROverrideForGame(vm.SelectedPreviewItem);
        vm.CanResetSelected1G1ROverride.Should().BeFalse();
        vm.HasManual1G1ROverrides.Should().BeFalse();

        // 7. Test Avalonia Window bindings on UI thread
        await RunOnUiThreadAsync(() =>
        {
            var window = new MainWindow(vm);
            var table = window.FindControl<DataGrid>("TablePreview")!;
            var contextMenu = table.ContextMenu!;
            var items = contextMenu.Items.Cast<Control>().ToList();

            var menuForce = (MenuItem)items[0];
            var menuReset = (MenuItem)items[1];
            var menuClearAll = (MenuItem)items[2];
            var separator = (Separator)items[3];
            var menuIsolate = (MenuItem)items[4];
            var menuClearFilter = (MenuItem)items[5];

            // When Row 0 is selected (no override, no filter):
            vm.SelectedPreviewItem = vm.AllPreviewItems[0];
            menuForce.IsVisible.Should().BeTrue();
            menuReset.IsVisible.Should().BeFalse();
            menuClearAll.IsVisible.Should().BeFalse();
            separator.IsVisible.Should().BeTrue();
            menuIsolate.IsVisible.Should().BeTrue();
            menuClearFilter.IsVisible.Should().BeFalse();

            // When forced:
            vm.Force1G1RWinner(vm.SelectedPreviewItem);
            menuReset.IsVisible.Should().BeTrue();
            menuClearAll.IsVisible.Should().BeTrue();

            // When search filter active:
            vm.PreviewSearchText = "Another World";
            menuClearFilter.IsVisible.Should().BeTrue();

            // When search filter cleared:
            vm.ClearPreviewSearch();
            menuClearFilter.IsVisible.Should().BeFalse();

            // When override reset:
            vm.Reset1G1ROverrideForGame(vm.SelectedPreviewItem);
            menuReset.IsVisible.Should().BeFalse();
            menuClearAll.IsVisible.Should().BeFalse();

            window.Close();
        });
    }

    // =========================================================================
    // 55. Regression: Simulation with DAT and closing on Avalonia UI thread
    // =========================================================================

    [Fact]
    public async Task MainWindow_SimulateAndCloseOnUiThread_DoesNotThrowOrDeadlock()
    {
        var src = Path.Combine(_tempDir, "src_ui_thread");
        var dst = Path.Combine(_tempDir, "dst_ui_thread");
        var datPath = Path.Combine(_tempDir, "test_ui_thread.dat");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        var romPath = Path.Combine(src, "Turrican II (1991)(Rainbow Arts)(en)[!].adf");
        var romBytes = "TURRICAN2_DATA"u8.ToArray();
        await File.WriteAllBytesAsync(romPath, romBytes);
        var crc = await new ChecksumService().ComputeFileCrc32Async(romPath);

        var datXml = $"""
            <?xml version="1.0"?>
            <datafile>
                <header>
                    <name>Commodore Amiga - Games - [ADF]</name>
                    <version>2025-07-06</version>
                </header>
                <game name="Turrican II (1991)(Rainbow Arts)(en)[!]">
                    <description>Turrican II (1991)(Rainbow Arts)(en)[!]</description>
                    <rom name="Turrican II (1991)(Rainbow Arts)(en)[!].adf" size="{romBytes.Length}" crc="{crc}" />
                </game>
            </datafile>
            """;
        await File.WriteAllTextAsync(datPath, datXml);

        await RunOnUiThreadAsync(async () =>
        {
            var dialog = new ConfigurableDialogService();
            var window = new MainWindow(configFilePath: _isolatedConfigPath, dialogService: dialog);

            // The preferred media type, set on the line of its criterion, shows its default value ("none")
            window.ViewModel.Priorities.MediaTypePreference!.SelectedOption!.Value.Should().Be(MediaPreference.None);
            window.ViewModel.TosecFilters.PreferredMediaType.Should().Be(MediaPreference.None);

            window.ViewModel.Directories.DatFilePath = datPath;
            window.ViewModel.Directories.SourceDir = src;
            window.ViewModel.Directories.DestinationDir = dst;

            // SimulateAsync with a DAT file performs async File I/O and CRC32 hashing;
            // it must resume on the Avalonia UI thread without throwing "Call from invalid thread".
            await window.ViewModel.Execution.SimulateAsync();

            dialog.Warnings.Should().BeEmpty();
            window.ViewModel.AllPreviewItems.Count.Should().Be(1);
            window.ViewModel.FilteredPreviewItems.Count.Should().Be(1);
            window.ViewModel.Execution.StatusMessage.Should().Be("Completed.", "the application starts in English");

            // Saving configuration synchronously (as OnWindowClosing does) must not deadlock on UI thread
            window.ViewModel.SaveConfigurationAsync().GetAwaiter().GetResult();
            File.Exists(_isolatedConfigPath).Should().BeTrue();
        });
    }

    [Fact]
    public async Task MainWindow_SwitchingLanguageToEnglish_UpdatesAllUiLabels()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);

            var btnProfileMenu = window.FindControl<DropDownButton>("BtnProfileMenu")!;
            var radioVerifiedOnly = window.FindControl<RadioButton>("RadioDumpLevelVerified")!;
            var chkKeepTranslations = window.FindControl<CheckBox>("ChkKeepTranslations")!;
            var chkOneGameOneRom = window.FindControl<CheckBox>("ChkOneGameOneRom")!;
            var chkFilterLanguages = window.FindControl<CheckBox>("ChkFilterLanguages")!;
            var btnSimulate = window.FindControl<Button>("BtnSimulate")!;
            var btnStart = window.FindControl<Button>("BtnStart")!;
            var lblCounts = window.FindControl<TextBlock>("LblCounts")!;
            var lblEta = window.FindControl<TextBlock>("LblEta")!;
            var lblStatus = window.FindControl<TextBlock>("LblStatusMsg")!;
            var mediaPreference = vm.Priorities.MediaTypePreference!;
            var searchFilter = window.FindControl<AutoCompleteBox>("EditSearchPreview")!;
            var btnExportMenu = window.FindControl<DropDownButton>("BtnExportMenu")!;
            var btnClearLog = window.FindControl<Button>("BtnClearLog")!;

            // Verify initial French strings
            btnProfileMenu.Content.Should().Be("Profil");
            ContentText(radioVerifiedOnly).Should().Be("Dumps vérifiés uniquement [!]");
            ContentText(chkKeepTranslations).Should().Be("Traductions [tr]");
            ContentText(chkOneGameOneRom).Should().Be("Une seule version par jeu (1G1R)");
            ContentText(chkFilterLanguages).Should().Be("Ne garder que les jeux dans ces langues");
            btnSimulate.Content.Should().Be("Simuler (Dry-Run)");
            btnStart.Content.Should().Be("Démarrer le tri");
            lblCounts.Text.Should().Be("Fichiers : 0 / 0");
            lblEta.Text.Should().Be("ETA : --:--");
            lblStatus.Text.Should().Be("Prêt.");
            searchFilter.Watermark.Should().Be("Filtrer par titre de jeu (autocomplétion)...");
            btnExportMenu.Content.Should().Be("Exporter");
            btnClearLog.Content.Should().Be("Effacer les messages");
            mediaPreference.SelectedOption!.Value.Should().Be(MediaPreference.None);
            mediaPreference.SelectedOption.Label.Should().Be("Indifférent");
            vm.ClearAll1G1ROverridesMenuText.Should().Be("🧹 Réinitialiser tous les forçages 1G1R manuels (0)");
            vm.IsolateGameMenuText.Should().Be("🔍 Isoler toutes les versions de « ... »");

            // Switch to English
            vm.UiLocale = "en";

            btnProfileMenu.Content.Should().Be("Profile");
            ContentText(radioVerifiedOnly).Should().Be("Verified dumps only [!]");
            ContentText(chkKeepTranslations).Should().Be("Translations [tr]");
            ContentText(chkOneGameOneRom).Should().Be("One version per game (1G1R)");
            ContentText(chkFilterLanguages).Should().Be("Keep only the games in these languages");
            btnSimulate.Content.Should().Be("Simulate (Dry-Run)");
            btnStart.Content.Should().Be("Start Sorting");
            lblCounts.Text.Should().Be("Files: 0 / 0");
            lblEta.Text.Should().Be("ETA: --:--");
            lblStatus.Text.Should().Be("Ready.");
            searchFilter.Watermark.Should().Be("Filter by game title (autocomplete)...");
            btnExportMenu.Content.Should().Be("Export");
            btnClearLog.Content.Should().Be("Clear Messages");
            mediaPreference.SelectedOption.Value.Should().Be(MediaPreference.None);
            mediaPreference.SelectedOption.Label.Should().Be("Any");
            vm.ClearAll1G1ROverridesMenuText.Should().Be("🧹 Reset all manual 1G1R overrides (0)");
            vm.IsolateGameMenuText.Should().Be("🔍 Isolate all versions of \"...\"");

            // Switch to Spanish
            vm.UiLocale = "es";

            btnProfileMenu.Content.Should().Be("Perfil");
            ContentText(radioVerifiedOnly).Should().Be("Solo volcados verificados [!]");
            btnSimulate.Content.Should().Be("Simular (Dry-Run)");
            btnStart.Content.Should().Be("Iniciar la clasificación");
            lblCounts.Text.Should().Be("Archivos: 0 / 0");
            lblStatus.Text.Should().Be("Listo.");
            mediaPreference.SelectedOption.Label.Should().Be("Cualquiera");
            vm.AvailableLocaleOptions.Select(o => o.Label).Should().Equal("English", "Français", "Español");
            vm.Priorities.CountryPriority.AvailableItems.Should().Contain(i => i.Code == "ES" && i.Label.Contains("España"));

            // Switch back to French
            vm.UiLocale = "fr";

            btnProfileMenu.Content.Should().Be("Profil");
            ContentText(radioVerifiedOnly).Should().Be("Dumps vérifiés uniquement [!]");
            btnSimulate.Content.Should().Be("Simuler (Dry-Run)");
            lblCounts.Text.Should().Be("Fichiers : 0 / 0");
            lblEta.Text.Should().Be("ETA : --:--");
            mediaPreference.SelectedOption.Label.Should().Be("Indifférent");
        });
    }

    [Fact]
    public async Task LanguageSwitch_ShouldTranslatePreviewItemStatusReasons()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.UiLocale = "fr";

            var filterService = RomFilterService.CreateDefault();
            var item1 = new ProcessItem
            {
                SourcePath = "/roms/007 - A View to a Kill (1985)(Domark).dsk",
                FileSize = 194816,
                Parsed = new ParsedRomName { RawFilename = "007 - A View to a Kill (1985)(Domark).dsk", Title = "007 - A View to a Kill" },
                Status = FileStatus.Valid,
                StatusReason = LocalizedMessage.Create("reason.1g1r_winner"),
                Is1G1RWinner = true
            };
            var item2 = new ProcessItem
            {
                SourcePath = "/roms/007 - A View to a Kill (1985)(Domark)[a].dsk",
                FileSize = 194816,
                Parsed = new ParsedRomName { RawFilename = "007 - A View to a Kill (1985)(Domark)[a].dsk", Title = "007 - A View to a Kill", IsAlternate = true },
                Status = FileStatus.Excluded,
                StatusReason = LocalizedMessage.Create("reason.alternate_dump_excluded")
            };
            var item3 = new ProcessItem
            {
                SourcePath = "/roms/007 - Licence to Kill (1989)(Domark)[cr].dsk",
                FileSize = 194816,
                Parsed = new ParsedRomName { RawFilename = "007 - Licence to Kill (1989)(Domark)[cr].dsk", Title = "007 - Licence to Kill", IsCrack = true },
                Status = FileStatus.Excluded,
                StatusReason = LocalizedMessage.Create("reason.1g1r_discarded", ("winner", "007 - Licence to Kill (1989)(Domark).dsk"), ("reason", LocalizedMessage.Create("reason.1g1r_reason_clean"))),
                EliminationDetail = LocalizedMessage.Create("reason.1g1r_reason_clean")
            };

            var row1 = new PreviewItemViewModel(item1, vm.Localization, filterService);
            var row2 = new PreviewItemViewModel(item2, vm.Localization, filterService);
            var row3 = new PreviewItemViewModel(item3, vm.Localization, filterService);

            vm.AllPreviewItems.Add(row1);
            vm.AllPreviewItems.Add(row2);
            vm.AllPreviewItems.Add(row3);
            vm.FilteredPreviewItems.Add(row1);
            vm.FilteredPreviewItems.Add(row2);
            vm.FilteredPreviewItems.Add(row3);

            // Initially French
            row1.StatusDisplay.Should().Be("Retenu (1G1R : meilleure version)");
            row2.StatusDisplay.Should().Be("Exclu (Alternate Dump [a] exclu)");
            row3.StatusDisplay.Should().Be("Exclu (1G1R : 007 - Licence to Kill (1989)(Domark).dsk retenu (version originale vs modifiée))");

            // Switch to English
            vm.UiLocale = "en";

            row1.StatusDisplay.Should().Be("Valid (1G1R: best version)");
            row2.StatusDisplay.Should().Be("Excluded (Alternate Dump [a] excluded)");
            row3.StatusDisplay.Should().Be("Excluded (1G1R: 007 - Licence to Kill (1989)(Domark).dsk retained (original release vs modified))");
            vm.Localization.Format(item3.EliminationDetail).Should().Be("original release vs modified");

            // Switch to Spanish
            vm.UiLocale = "es";

            row1.StatusDisplay.Should().Be("Válido (1G1R: mejor versión)");
            row2.StatusDisplay.Should().Be("Excluido (Variante [a] excluida)");
            row3.StatusDisplay.Should().Be("Excluido (1G1R: 007 - Licence to Kill (1989)(Domark).dsk conservada (versión original frente a modificada))");

            // Switch back to French
            vm.UiLocale = "fr";

            row1.StatusDisplay.Should().Be("Retenu (1G1R : meilleure version)");
            row2.StatusDisplay.Should().Be("Exclu (Alternate Dump [a] exclu)");
            row3.StatusDisplay.Should().Be("Exclu (1G1R : 007 - Licence to Kill (1989)(Domark).dsk retenu (version originale vs modifiée))");
            vm.Localization.Format(item3.EliminationDetail).Should().Be("version originale vs modifiée");
        });
    }

    [Fact]
    public async Task LanguageSwitch_ShouldTranslateLogEntries()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.UiLocale = "fr";

            var rerelease = LocalizedMessage.Create(
                "reason.1g1r_discarded",
                ("winner", "Extreme - G-Force (1990).dsk"),
                ("reason", LocalizedMessage.Create("reason.1g1r_reason_original_over_rerelease", ("tag", "Kixx"))));
            vm.Log.Append(LogLevel.Info, LocalizedMessage.Create("log.starting_operation", ("action", LocalizedMessage.Create("action.dry_run"))));
            vm.Log.Append(LogLevel.Info, LocalizedMessage.Create("log.scanning_files"));
            vm.Log.Append(LogLevel.Info, LocalizedMessage.Create("log.files_scanned_count", ("count", 4433), ("mb", "1234.5")));
            vm.Log.Append(LogLevel.Skip, LocalizedMessage.Create("log.labeled", ("label", rerelease), ("detail", "Extreme - G-Force (1991).dsk")));
            vm.Log.Append(LogLevel.Skip, LocalizedMessage.Create("log.labeled", ("label", LocalizedMessage.Create("reason.alternate_dump_excluded")), ("detail", "SuperGame (Alt 1).dsk")));
            vm.Log.Append(LogLevel.Ok, LocalizedMessage.Create(
                "log.operation_completed_summary",
                ("valid", 10), ("excluded", 5), ("unmatched", 2), ("duplicates", 1), ("errors", 0)));
            vm.Log.Append(LogLevel.Error, LocalizedMessage.Verbatim("Disk full"));

            string[] french =
            [
                "Démarrage de l'opération : Simulation (Dry-Run)",
                "Scan des fichiers en cours...",
                "4433 fichiers répertoriés (1234.5 Mo au total).",
                "1G1R : Extreme - G-Force (1990).dsk retenu (parution originale d'époque préférée à la réédition (Kixx)) : Extreme - G-Force (1991).dsk",
                "Alternate Dump [a] exclu : SuperGame (Alt 1).dsk",
                "Opération terminée avec succès. Retenus: 10, Exclus: 5, Non conformes: 2, Doublons: 1, Erreurs: 0.",
                "Disk full"
            ];
            vm.Log.Entries.Select(e => e.CleanMessage).Should().Equal(french);

            // Switch to English: every entry is rebuilt from its structured message
            vm.UiLocale = "en";

            vm.Log.Entries.Select(e => e.CleanMessage).Should().Equal(
                "Starting operation: Simulation (Dry-Run)",
                "Scanning files in progress...",
                "4433 files found (1234.5 MB total).",
                "1G1R: Extreme - G-Force (1990).dsk retained (original period release vs Kixx) : Extreme - G-Force (1991).dsk",
                "Alternate Dump [a] excluded : SuperGame (Alt 1).dsk",
                "Operation completed successfully. Kept: 10, Excluded: 5, Non-conforming: 2, Duplicates: 1, Errors: 0.",
                "Disk full");
            vm.Log.Entries.Select(e => e.Level).Should().Equal("INFO", "INFO", "INFO", "SKIP", "SKIP", "OK", "ERROR");

            // Switch back to French
            vm.UiLocale = "fr";

            vm.Log.Entries.Select(e => e.CleanMessage).Should().Equal(french);
        });
    }

    [Fact]
    public async Task MainWindow_TooltipsShouldHave1SecondDelayAndListsShouldNotHaveTooltips()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            vm.UiLocale = "fr";
            var window = new MainWindow { DataContext = vm };

            // 1. Verify that all lists (ListBoxes and DataGrid) do NOT have a tooltip
            var listControls = new Control[]
            {
                window.FindControl<ItemsControl>("ChipsLanguages")!,
                window.FindControl<ItemsControl>("ChipsCountries")!,
                window.FindControl<DataGrid>("TablePreview")!,
                window.FindControl<ListBox>("LogViewerList")!
            };

            foreach (var list in listControls)
            {
                var tip = ToolTip.GetTip(list);
                tip.Should().BeNull($"List control '{list.Name}' must not display any tooltip on hover");
            }

            // 2. Verify that interactive non-list controls HAVE descriptive tooltips
            var interactiveControls = new Control[]
            {
                window.FindControl<DropDownButton>("BtnProfileMenu")!,
                window.FindControl<Button>("BtnPreferences")!,
                window.FindControl<ComboBox>("ComboConvention")!,
                window.FindControl<Border>("LblConventionBadge")!,
                window.FindControl<TextBox>("EditDat")!,
                window.FindControl<Button>("BtnBrowseDat")!,
                window.FindControl<TextBlock>("LblDatInfo")!,
                window.FindControl<TextBox>("EditSource")!,
                window.FindControl<Button>("BtnBrowseSource")!,
                window.FindControl<CheckBox>("ChkRecursive")!,
                window.FindControl<DropDownButton>("BtnAdvancedDirectories")!,
                window.FindControl<TextBox>("EditDest")!,
                window.FindControl<Button>("BtnBrowseDest")!,
                window.FindControl<CheckBox>("ChkAppendToDestination")!,
                window.FindControl<ComboBox>("ComboTransferMode")!,
                window.FindControl<ComboBox>("ComboConflicts")!,
                window.FindControl<TextBox>("EditPattern")!,
                window.FindControl<DropDownButton>("BtnInsertVariable")!,
                window.FindControl<DropDownButton>("BtnPatternPresets")!,
                window.FindControl<StackPanel>("PanelPatternPreview")!,
                window.FindControl<CheckBox>("ChkRestoreArticles")!,
                window.FindControl<CheckBox>("ChkSubfolderMultidisk")!,
                window.FindControl<CheckBox>("ChkGenerateM3u")!,
                window.FindControl<ComboBox>("ComboM3uPlacement")!,
                window.FindControl<RadioButton>("RadioFiltersTosec")!,
                window.FindControl<RadioButton>("RadioFiltersNoIntro")!,
                window.FindControl<RadioButton>("RadioDumpLevelAll")!,
                window.FindControl<RadioButton>("RadioDumpLevelNoBad")!,
                window.FindControl<RadioButton>("RadioDumpLevelVerified")!,
                window.FindControl<DropDownButton>("BtnDumpDetails")!,
                window.FindControl<CheckBox>("ChkKeepModifications")!,
                window.FindControl<CheckBox>("ChkKeepTranslations")!,
                window.FindControl<CheckBox>("ChkKeepDev")!,
                window.FindControl<CheckBox>("ChkKeepDemos")!,
                window.FindControl<CheckBox>("ChkKeepPd")!,
                window.FindControl<CheckBox>("ChkKeepPirate")!,
                window.FindControl<CheckBox>("ChkKeepUnl")!,
                window.FindControl<CheckBox>("ChkKeepRereleases")!,
                window.FindControl<CheckBox>("ChkCompleteSetsOnly")!,
                window.FindControl<CheckBox>("ChkSmartMultidisk")!,
                window.FindControl<CheckBox>("ChkIgnoreModifications")!,
                window.FindControl<Button>("BtnPresetFr")!,
                window.FindControl<Button>("BtnPresetEu")!,
                window.FindControl<Button>("BtnPresetUs")!,
                window.FindControl<Button>("BtnPresetJp")!,
                window.FindControl<CheckBox>("ChkOneGameOneRom")!,
                window.FindControl<Button>("BtnTiebreakOrder")!,
                window.FindControl<CheckBox>("ChkFilterLanguages")!,
                window.FindControl<CheckBox>("ChkPriorityFallback")!,
                window.FindControl<DropDownButton>("BtnAddLanguage")!,
                window.FindControl<Button>("BtnLangReset")!,
                window.FindControl<CheckBox>("ChkFilterCountries")!,
                window.FindControl<CheckBox>("ChkCountryFallback")!,
                window.FindControl<DropDownButton>("BtnAddCountry")!,
                window.FindControl<Button>("BtnCountryReset")!,
                window.FindControl<Button>("BtnBackToSettings")!,
                window.FindControl<Button>("BtnSimulate")!,
                window.FindControl<Button>("BtnStart")!,
                window.FindControl<Button>("BtnUndo")!,
                window.FindControl<DropDownButton>("BtnExportMenu")!,
                window.FindControl<Button>("BtnClearLog")!,
                window.FindControl<ProgressBar>("ProgressBar")!,
                window.FindControl<TextBlock>("LblCounts")!,
                window.FindControl<TextBlock>("LblEta")!,
                window.FindControl<TextBlock>("LblDatStats")!,
                window.FindControl<TextBlock>("LblVolume")!,
                window.FindControl<AutoCompleteBox>("EditSearchPreview")!,
                window.FindControl<Button>("BtnClearSearchPreview")!
            };

            foreach (var ctrl in interactiveControls)
            {
                var tip = ToolTip.GetTip(ctrl)?.ToString();
                tip.Should().NotBeNullOrWhiteSpace($"Control '{ctrl.Name}' must have a tooltip in French");
                tip!.Length.Should().BeGreaterThan(15, $"Control '{ctrl.Name}' tooltip in French must explain its functionality");
                tip.Should().NotContain("tooltip", $"Control '{ctrl.Name}' tooltip must be localized, not a raw key");

                // Check that ToolTip ShowDelay is set to 1000ms (1 second)
                ToolTip.GetShowDelay(ctrl).Should().Be(1000, $"Control '{ctrl.Name}' should have ShowDelay of 1000ms");
            }

            // 3. Switch to English and verify dynamic translation
            vm.UiLocale = "en";

            foreach (var ctrl in interactiveControls)
            {
                var tip = ToolTip.GetTip(ctrl)?.ToString();
                tip.Should().NotBeNullOrWhiteSpace($"Control '{ctrl.Name}' must have a tooltip in English");
                tip!.Length.Should().BeGreaterThan(15, $"Control '{ctrl.Name}' tooltip in English must explain its functionality");
                tip.Should().NotContain("tooltip", $"Control '{ctrl.Name}' tooltip must be localized, not a raw key");
                ToolTip.GetShowDelay(ctrl).Should().Be(1000, $"Control '{ctrl.Name}' should have ShowDelay of 1000ms");
            }
        });
    }

    [Fact]
    public void TestStatsBannerAdaptiveVolumeFormatting()
    {
        var vm = CreateViewModel();
        var sb = vm.StatsBanner;

        // French (default)
        sb.FormatVolume(0).Should().Be("Volume : 0.00 Mo");
        sb.FormatVolume(176).Should().Be("Volume : 176 o");
        sb.FormatVolume(51200).Should().Be("Volume : 50.00 Ko");
        sb.FormatVolume(650L * 1024L * 1024L).Should().Be("Volume : 650.00 Mo");
        sb.FormatVolume((long)(2.4 * 1024L * 1024L * 1024L)).Should().Be("Volume : 2.40 Go");

        // English
        vm.UiLocale = "en";
        sb.RefreshTranslations();
        sb.FormatVolume(0).Should().Be("Volume: 0.00 MB");
        sb.FormatVolume(176).Should().Be("Volume: 176 B");
        sb.FormatVolume(51200).Should().Be("Volume: 50.00 KB");
        sb.FormatVolume(650L * 1024L * 1024L).Should().Be("Volume: 650.00 MB");
        sb.FormatVolume((long)(2.4 * 1024L * 1024L * 1024L)).Should().Be("Volume: 2.40 GB");
    }

    [Fact]
    public void StatsBanner_AfterARun_ShouldShowTheVolumeWrittenToTheDestination()
    {
        var vm = CreateViewModel();
        var sb = vm.StatsBanner;
        const long GiB = 1024L * 1024L * 1024L;
        var stats = new ProcessStats
        {
            TotalScanned = 5,
            TotalBytes = 10 * GiB,
            ValidBytes = 2 * GiB,
            UnmatchedBytes = GiB / 2,
            ExcludedBytes = 7 * GiB + GiB / 2
        };
        static string Digits(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"[\s\u00A0\u202F,.]", "");

        // Kept and non-conforming files, with the exact byte count for comparison with a medium sold in decimal units
        sb.UpdateStats(stats, TransferMode.Copy);
        sb.VolumeText.Should().Be("Volume : 10.00 Go → 2.50 Go");
        sb.VolumeTooltip.Should().Contain("_unmatched").And.Contain("1 073 741 824").And.NotContain("ZIP");
        Digits(sb.VolumeTooltip).Should().Contain("2684354560octets");

        // A ZIP sort compresses: the volume is an upper bound
        sb.UpdateStats(stats, TransferMode.Zip);
        sb.VolumeText.Should().Be("Volume : 10.00 Go → 2.50 Go max.");
        sb.VolumeTooltip.Should().Contain("Mode ZIP");

        sb.UpdateStats(stats, TransferMode.HardLink);
        sb.VolumeText.Should().Be("Volume : 10.00 Go → 2.50 Go");
        sb.VolumeTooltip.Should().Contain("Liens physiques ou symboliques");

        vm.UiLocale = "en";
        sb.RefreshTranslations();
        sb.VolumeText.Should().Be("Volume: 10.00 GB → 2.50 GB");
        Digits(sb.VolumeTooltip).Should().Contain("2684354560bytes");

        // Before a run, the total volume only
        sb.Reset();
        sb.VolumeText.Should().Be("Volume: 0.00 MB");
        sb.VolumeTooltip.Should().Be(vm.I18n["panel4.tooltip_volume"]);
    }

    [Fact]
    public async Task StatsBanner_FilesTooLargeForFat32_ShouldShowAWarningWithTheLargestFiles()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var sb = vm.StatsBanner;
            const long GiB = 1024L * 1024L * 1024L;
            var parser = new NoIntroRedumpParser(new TosecParser());
            var dst = Path.Combine(_tempDir, "dst");
            ProcessItem Item(string name, long size, FileStatus status = FileStatus.Valid) => new()
            {
                SourcePath = Path.Combine(_tempDir, "src", name),
                FileSize = size,
                Parsed = parser.ParseRomFilename(name),
                Status = status,
                FinalTargetPath = Path.Combine(dst, name)
            };
            var items = new List<ProcessItem>
            {
                Item("Huge Game (Japan).iso", 5 * GiB),
                Item("Large Game (USA).iso", 4 * GiB + GiB / 2),
                Item("Excluded Game (Europe).iso", 6 * GiB, FileStatus.Excluded),
                Item("Small Game (Japan).iso", GiB)
            };
            items.AddRange(Enumerable.Range(1, StatsBannerViewModel.MaxListedOversizedFiles)
                .Select(i => Item($"Other Game {i:00} (Japan).iso", 4 * GiB + i)));
            var stats = new ProcessStats { TotalScanned = items.Count };
            SortConfig Config(TransferMode mode) => new() { SourceDir = Path.Combine(_tempDir, "src"), DestinationDir = dst, TransferMode = mode };
            var config = Config(TransferMode.Copy);

            vm.Execution.PlayRunForTests(config, isDryRun: true, new SortExecutionResult(stats, items, [], null));

            // 12 written files of 4 GB or more; the excluded one is not written
            sb.IsFat32WarningVisible.Should().BeTrue();
            sb.Fat32WarningText.Should().Be("⚠ FAT32 : 12 fichiers > 4 Go");
            var lines = sb.Fat32WarningTooltip.Split(Environment.NewLine);
            lines[0].Should().Contain("FAT32").And.Contain("exFAT");
            lines[1].Should().Be("• Huge Game (Japan).iso (5.00 Go)");
            lines[2].Should().Be("• Large Game (USA).iso (4.50 Go)");
            lines.Should().HaveCount(1 + StatsBannerViewModel.MaxListedOversizedFiles + 1);
            lines[^1].Should().Be("… et 2 autres");
            sb.Fat32WarningTooltip.Should().NotContain("Excluded Game").And.NotContain("Small Game").And.NotContain("ZIP");

            // ZIP mode: archives measured before compression
            vm.Execution.PlayRunForTests(Config(TransferMode.Zip), isDryRun: true, new SortExecutionResult(stats, items, [], null));
            sb.Fat32WarningTooltip.Split(Environment.NewLine)[1].Should().StartWith("Mode ZIP");
            sb.VolumeText.Should().EndWith("max.");

            vm.UiLocale = "en";
            sb.RefreshTranslations();
            sb.Fat32WarningText.Should().Be("⚠ FAT32: 12 files > 4 GB");

            // No file too large, no warning
            var smallItems = new List<ProcessItem> { Item("Small Game (Japan).iso", GiB) };
            vm.Execution.PlayRunForTests(config, isDryRun: true, new SortExecutionResult(
                new ProcessStats { TotalScanned = 1 }, smallItems, [], null));
            sb.IsFat32WarningVisible.Should().BeFalse();
            sb.Fat32WarningText.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task StatsBanner_FilesAlreadyInTheDestination_ShouldBeShownApartFromTheDuplicates()
    {
        await RunOnUiThreadAsync(() =>
        {
            var vm = CreateViewModel();
            var window = new MainWindow(vm);
            window.Show();
            try
            {
                var sb = vm.StatsBanner;
                var config = new SortConfig { SourceDir = Path.Combine(_tempDir, "src"), DestinationDir = Path.Combine(_tempDir, "dst") };
                var stats = new ProcessStats { TotalScanned = 5855, ValidCount = 7, DuplicateCount = 5848, AlreadyInDestinationCount = 5848 };

                vm.Execution.PlayRunForTests(config, isDryRun: true, new SortExecutionResult(stats, [], [], null));
                Settle(window);

                var label = window.FindControl<TextBlock>("LblAlreadyInDestination")!;
                label.IsEffectivelyVisible.Should().BeTrue();
                label.Text.Should().Be("5848 déjà dans la destination (non réécrits)");
                ToolTip.GetTip(label)!.ToString().Should().Contain("purgez-la").And.Contain("Écraser (Overwrite)").And.Contain("MAME");

                vm.UiLocale = "en";
                Settle(window);
                label.Text.Should().Be("5848 already in the destination (not written again)");

                // Duplicates of the run itself say nothing of the destination
                vm.Execution.PlayRunForTests(config, isDryRun: true, new SortExecutionResult(
                    new ProcessStats { TotalScanned = 2, ValidCount = 1, DuplicateCount = 1 }, [], [], null));
                Settle(window);
                label.IsVisible.Should().BeFalse();
                sb.AlreadyInDestinationText.Should().BeEmpty();
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TestPreviewItemTooltipWithCompanionsAndSize()
    {
        var loc = new LocalizationService("fr");

        var item = new ProcessItem
        {
            SourcePath = "/games/Tomb Raider (Europe).cue",
            FileSize = 150,
            Parsed = new ParsedRomName { RawFilename = "Tomb Raider (Europe).cue", Title = "Tomb Raider", Country = "Europe" },
            Status = FileStatus.Valid,
            RelatedItems =
            [
                new ProcessItem
                {
                    SourcePath = "/games/Tomb Raider (Europe) (Track 1).bin",
                    FileSize = 13,
                    Parsed = new ParsedRomName { RawFilename = "Tomb Raider (Europe) (Track 1).bin", Title = "Tomb Raider" }
                },
                new ProcessItem
                {
                    SourcePath = "/games/Tomb Raider (Europe) (Track 2).bin",
                    FileSize = 13,
                    Parsed = new ParsedRomName { RawFilename = "Tomb Raider (Europe) (Track 2).bin", Title = "Tomb Raider" }
                }
            ]
        };

        var tooltipFr = PreviewItemViewModel.BuildItemTooltip(item, loc);
        tooltipFr.Should().Contain("Tomb Raider (Europe).cue");
        tooltipFr.Should().Contain("Taille : 176 o");
        tooltipFr.Should().Contain("+ 2 pistes / fichiers associés");
        tooltipFr.Should().Contain("• Tomb Raider (Europe) (Track 1).bin (13 o)");
        tooltipFr.Should().Contain("• Tomb Raider (Europe) (Track 2).bin (13 o)");

        // English
        var locEn = new LocalizationService("en");
        var tooltipEn = PreviewItemViewModel.BuildItemTooltip(item, locEn);
        tooltipEn.Should().Contain("Tomb Raider (Europe).cue");
        tooltipEn.Should().Contain("Size: 176 B");
        tooltipEn.Should().Contain("+ 2 companion tracks/files");
        tooltipEn.Should().Contain("• Tomb Raider (Europe) (Track 1).bin (13 B)");
        tooltipEn.Should().Contain("• Tomb Raider (Europe) (Track 2).bin (13 B)");
    }

    [Fact]
    public void TestPreviewItemTooltip_1G1RExcluded_SingleLine_NoDuplicationOfWinnerAndReason()
    {
        // Verifies option 3: 1G1R Excluded item tooltip must display a single
        // line "1G1R : <winner> (<pure reason>)" without duplication.
        var locFr = new LocalizationService("fr");

        var item = new ProcessItem
        {
            SourcePath = "/roms/10-Yard Fight (Japan).nes",
            FileSize = 24576,
            Parsed = new ParsedRomName { RawFilename = "10-Yard Fight (Japan).nes", Title = "10-Yard Fight" },
            Status = FileStatus.Excluded,
            WinnerReference = "10-Yard Fight (USA, Europe).zip",
            EliminationDetail = LocalizedMessage.Create("reason.1g1r_reason_country_priority", ("w_country", "EU"), ("l_country", "JP")),
            StatusReason = LocalizedMessage.Create("reason.1g1r_discarded", ("winner", "10-Yard Fight (USA, Europe).zip"), ("reason", LocalizedMessage.Create("reason.1g1r_reason_country_priority", ("w_country", "EU"), ("l_country", "JP"))))
        };

        // ── French ───────────────────────────────────────────────────────────────────
        var tooltipFr = PreviewItemViewModel.BuildItemTooltip(item, locFr);
        var linesFr = tooltipFr.Split(Environment.NewLine);

        // Single line starting with "1G1R"
        var lines1G1RFr = linesFr.Where(l => l.StartsWith("1G1R", StringComparison.Ordinal)).ToList();
        lines1G1RFr.Should().HaveCount(1, "le tooltip ne doit afficher qu'une seule ligne 1G1R");
        lines1G1RFr[0].Should().Be("1G1R : 10-Yard Fight (USA, Europe).zip (pays/région EU préféré(e) à JP)");

        // ── English ──────────────────────────────────────────────────────────────────
        var locEn = new LocalizationService("en");

        // The structured EliminationDetail is translated by the localization service given to the tooltip

        var tooltipEn = PreviewItemViewModel.BuildItemTooltip(item, locEn);
        var linesEn = tooltipEn.Split(Environment.NewLine);

        var lines1G1REn = linesEn.Where(l => l.StartsWith("1G1R", StringComparison.Ordinal)).ToList();
        lines1G1REn.Should().HaveCount(1, "the tooltip must show only one 1G1R line");
        lines1G1REn[0].Should().Be("1G1R: 10-Yard Fight (USA, Europe).zip (country/region EU preferred over JP)");
    }

    [Fact]
    public void TestPreviewItemTooltip_1G1RExcluded_NoEliminationDetail_WinnerOnly()
    {
        // When EliminationDetail is absent (e.g. 1G1R duplicate on same media),
        // the tooltip must display "1G1R : <winner>" without empty parentheses.
        var loc = new LocalizationService("fr");

        var item = new ProcessItem
        {
            SourcePath = "/roms/Game (Japan) (Disk 2).dsk",
            FileSize = 1024,
            Parsed = new ParsedRomName { RawFilename = "Game (Japan) (Disk 2).dsk", Title = "Game" },
            Status = FileStatus.Duplicate,
            WinnerReference = "Game (Japan).dsk",
            EliminationDetail = null
        };

        var tooltip = PreviewItemViewModel.BuildItemTooltip(item, loc);
        tooltip.Should().Contain("1G1R : Game (Japan).dsk");
        tooltip.Should().NotContain("()");
    }

    [Fact]
    public void AppendLog_ShouldRespectMaxLogEntries()
    {
        var vm = CreateViewModel();
        for (var i = 0; i < LogViewModel.MaxEntries + 50; i++)
        {
            vm.Log.Append(LogLevel.Info, LocalizedMessage.Verbatim($"Message {i}"));
        }

        vm.Log.Entries.Should().HaveCount(LogViewModel.MaxEntries);
        vm.Log.Entries[0].CleanMessage.Should().Be("Message 50");
        vm.Log.Entries[^1].CleanMessage.Should().Be($"Message {LogViewModel.MaxEntries + 49}");
    }
}


