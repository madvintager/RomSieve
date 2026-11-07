using System.Text.Json;
using RomSieve.App;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// Loading and saving of <c>config.json</c> through the MainViewModel: historical values, invalid values and
/// options that fall back on others.
/// </summary>
public sealed class ConfigurationPersistenceTests
{
    [Fact]
    public async Task EnumValues_ShouldBeLoadedAndSavedWithTheSameStrings()
    {
        using var tmp = new TempDirectoryFixture();
        var path = tmp.WriteText("config.json", """
            {
              "region_priority_mode": "country_first",
              "preferred_video": "ntsc",
              "preferred_media_type": "tape"
            }
            """);

        var vm = TestMainViewModel.Create(new HeadlessDialogService(), path);

        vm.Priorities.RegionPriorityMode.Should().Be(RegionPriorityMode.CountryFirst);
        vm.Priorities.PreferredVideo.Should().Be(VideoPreference.Ntsc);
        vm.TosecFilters.PreferredMediaType.Should().Be(MediaPreference.Tape);

        await vm.SaveConfigurationAsync();
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        saved.RootElement.GetProperty("region_priority_mode").GetString().Should().Be("country_first");
        saved.RootElement.GetProperty("preferred_video").GetString().Should().Be("ntsc");
        saved.RootElement.GetProperty("preferred_media_type").GetString().Should().Be("tape");
    }

    [Fact]
    public async Task VerifyCopies_ShouldBeOffByDefaultAndPersisted()
    {
        using var tmp = new TempDirectoryFixture();
        var path = tmp.WriteText("config.json", "{}");

        var vm = TestMainViewModel.Create(new HeadlessDialogService(), path);
        vm.Directories.VerifyCopies.Should().BeFalse();
        vm.BuildConfigUnchecked().VerifyCopies.Should().BeFalse();

        vm.Directories.VerifyCopies = true;
        vm.BuildConfigUnchecked().VerifyCopies.Should().BeTrue();
        await vm.SaveConfigurationAsync();
        using (var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
        {
            saved.RootElement.GetProperty("verify_copies").GetBoolean().Should().BeTrue();
        }

        TestMainViewModel.Create(new HeadlessDialogService(), path)
            .Directories.VerifyCopies.Should().BeTrue();
    }

    [Fact]
    public async Task IsolateUnmatched_ShouldBeOnByDefaultAndPersisted()
    {
        using var tmp = new TempDirectoryFixture();
        var path = tmp.WriteText("config.json", "{}");

        var vm = TestMainViewModel.Create(new HeadlessDialogService(), path);
        vm.Directories.IsolateUnmatched.Should().BeTrue();
        vm.BuildConfigUnchecked().IsolateUnmatched.Should().BeTrue();

        vm.Directories.IsolateUnmatched = false;
        vm.BuildConfigUnchecked().IsolateUnmatched.Should().BeFalse();
        await vm.SaveConfigurationAsync();
        using (var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
        {
            saved.RootElement.GetProperty("isolate_unmatched").GetBoolean().Should().BeFalse();
        }

        TestMainViewModel.Create(new HeadlessDialogService(), path)
            .Directories.IsolateUnmatched.Should().BeFalse();
    }

    [Fact]
    public async Task ZipTransferMode_ShouldBeSelectedAndPersisted()
    {
        using var tmp = new TempDirectoryFixture();
        var path = tmp.WriteText("config.json", """{ "transfer_mode": "copy" }""");
        var vm = TestMainViewModel.Create(new HeadlessDialogService(), path);

        vm.Directories.IsTransferZip = true;

        vm.Directories.IsTransferCopy.Should().BeFalse();
        vm.BuildConfigUnchecked().TransferMode.Should().Be(TransferMode.Zip);
        await vm.SaveConfigurationAsync();
        using (var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
        {
            saved.RootElement.GetProperty("transfer_mode").GetString().Should().Be("zip");
        }

        TestMainViewModel.Create(new HeadlessDialogService(), path).Directories.IsTransferZip.Should().BeTrue();
    }

    [Fact]
    public async Task VerifySha1_ShouldBeOffByDefaultAndPersisted()
    {
        using var tmp = new TempDirectoryFixture();
        var path = tmp.WriteText("config.json", "{}");

        var vm = TestMainViewModel.Create(new HeadlessDialogService(), path);
        vm.BuildConfigUnchecked().VerifySha1.Should().BeFalse();

        vm.Directories.VerifySha1 = true;
        vm.BuildConfigUnchecked().VerifySha1.Should().BeTrue();
        await vm.SaveConfigurationAsync();

        TestMainViewModel.Create(new HeadlessDialogService(), path)
            .Directories.VerifySha1.Should().BeTrue();
    }

    [Theory]
    [InlineData("{}", "es")]
    [InlineData("""{ "ui_locale": "en" }""", "en")]
    [InlineData("""{ "ui_locale": "fr" }""", "fr")]
    public void StartingLanguage_ShouldOnlyApplyUntilTheConfigurationNamesOne(string config, string expected)
    {
        using var tmp = new TempDirectoryFixture();
        var path = tmp.WriteText("config.json", config);

        // The language of the system, as the application passes it at the first launch
        var vm = AppServiceCollectionExtensions.CreateMainViewModel(
            new HeadlessDialogService(), path, localization: new LocalizationService("es"));

        vm.UiLocale.Should().Be(expected);
        vm.Localization.CurrentLocale.Should().Be(expected);
    }

    [Fact]
    public async Task LotOptions_ShouldHaveTheirDefaultsAndBePersisted()
    {
        using var tmp = new TempDirectoryFixture();
        var path = tmp.WriteText("config.json", "{}");

        var vm = TestMainViewModel.Create(new HeadlessDialogService(), path);
        var defaults = vm.BuildConfigUnchecked();
        defaults.DatRecursive.Should().BeFalse();
        defaults.ExcludedDats.Should().BeEmpty();
        defaults.FolderPerDat.Should().BeTrue();
        defaults.DatFolderMirror.Should().BeFalse();

        vm.Directories.DatRecursive = true;
        vm.Directories.ExcludedDats = ["Nintendo - Game Boy", "Sega - Mega Drive"];
        vm.Directories.FolderPerDat = false;
        vm.Directories.DatFolderMirror = true;
        var config = vm.BuildConfigUnchecked();
        config.ExcludedDats.Should().Equal("Nintendo - Game Boy", "Sega - Mega Drive");
        config.FolderPerDat.Should().BeFalse();
        await vm.SaveConfigurationAsync();

        var saved = File.ReadAllText(path);
        saved.Should().Contain("\"dat_recursive\": true").And.Contain("\"folder_per_dat\": false").And.Contain("\"dat_folder_mirror\": true");
        var reloaded = TestMainViewModel.Create(new HeadlessDialogService(), path).Directories;
        reloaded.DatRecursive.Should().BeTrue();
        reloaded.ExcludedDats.Should().Equal("Nintendo - Game Boy", "Sega - Mega Drive");
        reloaded.FolderPerDat.Should().BeFalse();
        reloaded.DatFolderMirror.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyArchiveContents_ShouldBeOffByDefaultAndPersisted()
    {
        using var tmp = new TempDirectoryFixture();
        var path = tmp.WriteText("config.json", "{}");

        var vm = TestMainViewModel.Create(new HeadlessDialogService(), path);
        vm.BuildConfigUnchecked().VerifyArchiveContents.Should().BeFalse();

        vm.Directories.VerifyArchiveContents = true;
        vm.BuildConfigUnchecked().VerifyArchiveContents.Should().BeTrue();
        vm.Directories.Advanced.HeaderTooltip.Should().Contain("Vérifier le contenu des archives", "the option counts among the advanced ones");
        await vm.SaveConfigurationAsync();

        File.ReadAllText(path).Should().Contain("\"verify_archive_contents\": true");
        TestMainViewModel.Create(new HeadlessDialogService(), path)
            .Directories.VerifyArchiveContents.Should().BeTrue();
    }

    [Theory]
    [InlineData("{}", 1)]
    [InlineData("""{ "hashing_parallelism": 4 }""", 4)]
    [InlineData("""{ "hashing_parallelism": 0 }""", 0)]
    [InlineData("""{ "hashing_parallelism": 3 }""", 1)]
    [InlineData("""{ "hashing_parallelism": "fast" }""", 1)]
    public async Task HashingParallelism_ShouldBeReadValidatedAndPersisted(string json, int expected)
    {
        using var tmp = new TempDirectoryFixture();
        var path = tmp.WriteText("config.json", json);

        var vm = TestMainViewModel.Create(new HeadlessDialogService(), path);

        vm.Directories.HashingParallelism.Should().Be(expected);
        vm.BuildConfigUnchecked().HashingParallelism.Should().Be(expected);
        await vm.SaveConfigurationAsync();
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        saved.RootElement.GetProperty("hashing_parallelism").GetInt32().Should().Be(expected);
    }

    [Theory]
    [InlineData("""{ "region_priority_mode": "weird", "preferred_video": "secam", "preferred_media_type": "laserdisc" }""")]
    [InlineData("""{ "region_priority_mode": 3, "preferred_video": true, "preferred_media_type": null }""")]
    [InlineData("{}")]
    public void UnknownOrMissingConfigValues_ShouldFallBackToTheDefaults(string json)
    {
        using var tmp = new TempDirectoryFixture();
        var path = tmp.WriteText("config.json", json);

        var vm = TestMainViewModel.Create(new HeadlessDialogService(), path);

        vm.Priorities.RegionPriorityMode.Should().Be(RegionPriorityMode.LanguageFirst);
        vm.Priorities.PreferredVideo.Should().Be(VideoPreference.None);
        vm.TosecFilters.PreferredMediaType.Should().Be(MediaPreference.None);
    }

    [Fact]
    public async Task SavedState_ShouldNotReopenThePageNorKeepUnknownKeys()
    {
        using var tmp = new TempDirectoryFixture();
        var path = tmp.WriteText("config.json", """
            { "ui_locale": "fr", "splitter_sizes": [180, 520], "active_tab_index": 3, "active_step": "languages", "active_bottom_tab": 1 }
            """);

        var vm = TestMainViewModel.Create(new HeadlessDialogService(), path);
        vm.Navigation.CurrentStep.Should().Be(RomSieve.App.ViewModels.WorkflowStep.Collection, "the application always opens on the collection");
        vm.ActiveBottomTabIndex.Should().Be(1);

        await vm.SaveConfigurationAsync();
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var root = saved.RootElement;
        root.GetProperty("active_bottom_tab").GetInt32().Should().Be(1);
        foreach (var key in new[] { "splitter_sizes", "active_tab_index", "active_config_tab", "active_step" })
        {
            root.TryGetProperty(key, out _).Should().BeFalse(key);
        }
    }

    [Fact]
    public async Task ConfigWithoutPreviewColumns_ShouldShowTheDefaultColumnsAndSaveThem()
    {
        using var tmp = new TempDirectoryFixture();
        var path = tmp.WriteText("config.json", """{ "ui_locale": "fr" }""");

        var vm = TestMainViewModel.Create(new HeadlessDialogService(), path);
        vm.PreviewTable.HiddenColumnIds.Should().Equal("crc32", "disk", "publisher", "related");
        vm.PreviewTable.IsDetailPanelExpanded.Should().BeFalse();
        vm.PreviewTable.IsGroupedByGame.Should().BeFalse("the flat view is the default");

        await vm.SaveConfigurationAsync();
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        saved.RootElement.GetProperty("preview_hidden_columns").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("crc32", "disk", "publisher", "related");
        saved.RootElement.GetProperty("preview_columns").EnumerateArray().Select(e => e.GetString())
            .Should().Equal(PreviewTableViewModel.ColumnIds);
        saved.RootElement.GetProperty("preview_detail_expanded").GetBoolean().Should().BeFalse();
        saved.RootElement.GetProperty("preview_grouped_by_game").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task PreviewColumns_ShouldBePersistedAsAStateOfTheInterface()
    {
        using var tmp = new TempDirectoryFixture();
        var path = tmp.WriteText("config.json", "{}");

        var vm = TestMainViewModel.Create(new HeadlessDialogService(), path);
        vm.PreviewTable.Columns[1].IsVisible = true;
        vm.PreviewTable.Columns[5].IsVisible = false;
        vm.PreviewTable.Columns[PreviewTableViewModel.RelatedColumnIndex].IsVisible = true;
        vm.PreviewTable.IsDetailPanelExpanded = true;
        vm.PreviewTable.IsGroupedByGame = true;
        await vm.SaveConfigurationAsync();

        var reloaded = TestMainViewModel.Create(new HeadlessDialogService(), path);
        reloaded.PreviewTable.HiddenColumnIds.Should().Equal("disk", "publisher", "date");
        reloaded.PreviewTable.Columns[PreviewTableViewModel.RelatedColumnIndex].IsVisible.Should().BeTrue(
            "the column of related files, shown by the user, is in the list of known columns");
        reloaded.PreviewTable.IsDetailPanelExpanded.Should().BeTrue();
        reloaded.PreviewTable.IsGroupedByGame.Should().BeTrue();

        // A state of the interface, not a sort option: profiles and the command line do not know it
        var settings = new Dictionary<string, object?>();
        RomSieve.Core.Services.SortSettingsSerializer.Write(reloaded.CaptureSettings(), settings);
        settings.Keys.Should().NotContain(k => k.StartsWith("preview_", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"crc32\"")]
    [InlineData("42")]
    [InlineData("{ \"title\": true }")]
    public void InvalidPreviewColumns_ShouldFallBackToTheDefaultColumns(string value)
    {
        using var tmp = new TempDirectoryFixture();
        var path = tmp.WriteText("config.json", $$"""{ "preview_hidden_columns": {{value}} }""");

        var vm = TestMainViewModel.Create(new HeadlessDialogService(), path);

        vm.PreviewTable.HiddenColumnIds.Should().Equal("crc32", "disk", "publisher", "related");
    }

    [Fact]
    public void HiddenColumnsWithoutTheKnownColumns_ShouldBeTakenAsTheyAre()
    {
        using var tmp = new TempDirectoryFixture();
        var path = tmp.WriteText("config.json", """{ "preview_hidden_columns": [] }""");

        var vm = TestMainViewModel.Create(new HeadlessDialogService(), path);

        vm.PreviewTable.HiddenColumnIds.Should().BeEmpty("an empty list shows every column");
    }

    [Fact]
    public void UnknownPreviewColumns_ShouldBeIgnored()
    {
        using var tmp = new TempDirectoryFixture();
        var path = tmp.WriteText("config.json", """{ "preview_hidden_columns": ["languages", "rating", "status"] }""");

        var vm = TestMainViewModel.Create(new HeadlessDialogService(), path);

        vm.PreviewTable.HiddenColumnIds.Should().Equal("languages");
    }

    [Fact]
    public async Task ConfigWithoutVersion_ShouldBeSavedInTheCurrentFormat()
    {
        using var tmp = new TempDirectoryFixture();
        var path = tmp.WriteText("config.json", """{ "exclude_alternate": true, "one_game_one_rom": true }""");

        var vm = TestMainViewModel.Create(new HeadlessDialogService(), path);
        await vm.SaveConfigurationAsync();

        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        saved.RootElement.GetProperty(ConfigSchema.VersionKey).GetInt32().Should().Be(ConfigSchema.CurrentVersion);
        saved.RootElement.GetProperty("exclude_alternate").GetBoolean().Should().BeTrue();
        saved.RootElement.GetProperty("nointro_exclude_alternate").GetBoolean().Should().BeFalse("it is not taken from the TOSEC option");
        saved.RootElement.GetProperty("one_game_one_rom").GetBoolean().Should().BeTrue();
        saved.RootElement.TryGetProperty("language_priority", out _).Should().BeFalse();
        vm.Log.Entries.Should().NotContain(e => e.Source != null && e.Source.Key == "config.newer_version");
    }

    [Fact]
    public void ConfigOfALaterVersion_ShouldBeLoadedWithAWarning()
    {
        using var tmp = new TempDirectoryFixture();
        var path = tmp.WriteText("config.json",
            $$"""{ "{{ConfigSchema.VersionKey}}": {{ConfigSchema.CurrentVersion + 1}}, "transfer_mode": "move" }""");

        var vm = TestMainViewModel.Create(new HeadlessDialogService(), path);

        vm.Directories.IsTransferMove.Should().BeTrue("the options this version knows are read");
        vm.Log.Entries.Should().ContainSingle(e => e.Source != null && e.Source.Key == "config.newer_version")
            .Which.Level.Should().Be("WARN");
    }

    [Fact]
    public async Task UnreadableConfig_ShouldBeCopiedWithAWarningBeforeTheDefaultsReplaceIt()
    {
        using var tmp = new TempDirectoryFixture();
        var path = tmp.WriteText("config.json", """{ "transfer_mode": "move", """);

        var vm = TestMainViewModel.Create(new HeadlessDialogService(), path);

        vm.Directories.IsTransferMove.Should().BeFalse("the default settings are used");
        var copy = Directory.GetFiles(tmp.Path, "config.unreadable-*.json").Should().ContainSingle().Subject;
        vm.Log.Entries.Should().ContainSingle(e => e.Source != null && e.Source.Key == "config.unreadable_preserved")
            .Which.Level.Should().Be("WARN");

        await vm.SaveConfigurationAsync();

        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        saved.RootElement.GetProperty(ConfigSchema.VersionKey).GetInt32().Should().Be(ConfigSchema.CurrentVersion);
        (await File.ReadAllTextAsync(copy)).Should().Be("""{ "transfer_mode": "move", """);
    }

    [Fact]
    public async Task UnreadableConfigThatCannotBeCopied_ShouldNeverBeReplaced()
    {
        // Windows only: elsewhere, a lock does not stop the file from being read or copied
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var tmp = new TempDirectoryFixture();
        var path = tmp.WriteText("config.json", """{ "transfer_mode": "move" }""");

        MainViewModel vm;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            vm = TestMainViewModel.Create(new HeadlessDialogService(), path);
        }

        vm.Log.Entries.Should().ContainSingle(e => e.Source != null && e.Source.Key == "config.unreadable_kept")
            .Which.Level.Should().Be("WARN");
        Directory.GetFiles(tmp.Path, "config.unreadable-*.json").Should().BeEmpty();

        await vm.SaveConfigurationAsync();

        (await File.ReadAllTextAsync(path)).Should().Be("""{ "transfer_mode": "move" }""");
    }
}
