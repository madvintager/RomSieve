using System.Text.Json;
using RomSieve.App;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// « Enregistrer comme profil… » and « Charger un profil… »: a profile holds the sort options of the tabs, in the
/// format of <c>config.json</c>, and is read as the command line reads it.
/// </summary>
public sealed class ProfilesViewModelTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly HeadlessDialogService _dialogs = new();
    private readonly string _configPath;
    private readonly MainViewModel _vm;

    public ProfilesViewModelTests()
    {
        _configPath = _temp.WriteText("config.json", "{}");
        _vm = TestMainViewModel.Create(_dialogs, _configPath);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task SavedProfile_ShouldHoldTheSortOptionsAndBeFoundByTheCommandLine()
    {
        _vm.Directories.SourceDir = "roms";
        _vm.Directories.IsTransferZip = true;
        _vm.Priorities.OneGameOneRom = true;
        var path = ConfigurationProfiles.GetPath(_configPath, "Zip 1G1R");
        _dialogs.NextPickedSaveFile = path;

        await _vm.Profiles.SaveProfileCommand.ExecuteAsync(null);

        ConfigurationProfiles.List(_configPath).Should().Equal("Zip 1G1R");
        using (var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
        {
            saved.RootElement.GetProperty("transfer_mode").GetString().Should().Be("zip");
            saved.RootElement.TryGetProperty("window_width", out _).Should().BeFalse("the state of the interface is not part of a profile");
        }

        var data = await new ConfigurationService().LoadConfigFromFileAsync(path);
        SortSettingsSerializer.Read(data!).Should().BeEquivalentTo(_vm.CaptureSettings(), o => o.WithStrictOrdering());
        _dialogs.ShownMessages.Should().ContainSingle(m => m.Level == "INFO")
            .Which.Message.Should().Contain("romsieve-cli run --profile \"Zip 1G1R\"");
    }

    [Fact]
    public async Task ProfileSavedElsewhere_ShouldGetTheJsonExtensionButNoCommandLineHint()
    {
        _dialogs.NextPickedSaveFile = Path.Combine(_temp.Path, "elsewhere");

        await _vm.Profiles.SaveProfileCommand.ExecuteAsync(null);

        File.Exists(Path.Combine(_temp.Path, "elsewhere.json")).Should().BeTrue();
        _dialogs.ShownMessages.Should().ContainSingle().Which.Message.Should().NotContain("--profile");
    }

    [Fact]
    public async Task LoadedProfile_ShouldReplaceTheOptionsOfTheTabs()
    {
        _vm.Directories.VerifyCopies = true;
        _vm.TosecFilters.ExcludeDemos = true;
        var path = _temp.WriteText(Path.Combine("profiles", "Amiga.json"), """
            { "source_dir": "amiga", "transfer_mode": "move", "priority_languages": ["fr", "en"], "one_game_one_rom": true }
            """);
        _dialogs.NextPickedOpenFile = path;

        await _vm.Profiles.LoadProfileCommand.ExecuteAsync(null);

        _vm.Directories.SourceDir.Should().Be("amiga");
        _vm.Directories.IsTransferMove.Should().BeTrue();
        _vm.Priorities.OneGameOneRom.Should().BeTrue();
        _vm.Priorities.LanguagesText.Should().Be("fr, en");
        _vm.Directories.VerifyCopies.Should().BeFalse("an option missing from the profile takes its default value, as with the command line");
        _vm.TosecFilters.ExcludeDemos.Should().BeFalse();
        _vm.Log.Text.Should().Contain(path);
    }

    [Fact]
    public async Task UnreadableProfile_ShouldBeReportedWithoutChangingTheOptions()
    {
        _vm.Directories.SourceDir = "kept";
        _dialogs.NextPickedOpenFile = _temp.WriteText("broken.json", "{ broken");

        await _vm.Profiles.LoadProfileCommand.ExecuteAsync(null);

        _vm.Directories.SourceDir.Should().Be("kept");
        _dialogs.ShownMessages.Should().ContainSingle(m => m.Level == "ERROR");
    }

    [Fact]
    public async Task ProfileOfALaterVersion_ShouldBeLoadedWithAWarning()
    {
        _dialogs.NextPickedOpenFile = _temp.WriteText("later.json",
            $$"""{ "{{ConfigSchema.VersionKey}}": {{ConfigSchema.CurrentVersion + 1}}, "source_dir": "later" }""");

        await _vm.Profiles.LoadProfileCommand.ExecuteAsync(null);

        _vm.Directories.SourceDir.Should().Be("later");
        _vm.Log.Entries.Should().ContainSingle(e => e.Source != null && e.Source.Key == "config.newer_version")
            .Which.Level.Should().Be("WARN");
    }

    [Fact]
    public async Task SavedProfile_ShouldHoldTheVersionOfItsFormat()
    {
        var path = ConfigurationProfiles.GetPath(_configPath, "Versioned");
        _dialogs.NextPickedSaveFile = path;

        await _vm.Profiles.SaveProfileCommand.ExecuteAsync(null);

        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        saved.RootElement.GetProperty(ConfigSchema.VersionKey).GetInt32().Should().Be(ConfigSchema.CurrentVersion);
    }

    [Fact]
    public async Task CancelledPickers_ShouldDoNothing()
    {
        await _vm.Profiles.SaveProfileCommand.ExecuteAsync(null);
        await _vm.Profiles.LoadProfileCommand.ExecuteAsync(null);

        _dialogs.ShownMessages.Should().BeEmpty();
        ConfigurationProfiles.List(_configPath).Should().BeEmpty();
    }
}
