using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Unit tests for saving and loading JSON configuration (<see cref="ConfigurationService"/>).
/// </summary>
public sealed class ConfigurationServiceTests
{
    private readonly ConfigurationService _configService = new();

    [Fact]
    public async Task SaveAndLoadConfig_ShouldPersistDictionaryAndHandleMissingOrCorruptedFiles()
    {
        using var tmp = new TempDirectoryFixture();

        // 1. Default config path is a valid path ending with config.json under RomSieve
        var defaultPath = _configService.GetDefaultConfigPath();
        Path.GetFileName(defaultPath).Should().Be("config.json");
        defaultPath.Should().Contain("RomSieve");

        // 2. Loading non-existent file returns null
        var missingFile = tmp.GetFilePath("sub/nonexistent.json");
        (await _configService.LoadConfigFromFileAsync(missingFile)).Should().BeNull();

        // 3. Saving and loading valid config
        var targetFile = tmp.GetFilePath("sub/my_config.json");
        var data = new Dictionary<string, object?>
        {
            ["source_dir"] = "/path/to/src",
            ["recursive"] = false,
            ["items"] = new List<object?> { 1L, 2L, 3L }
        };

        var saved = await _configService.SaveConfigToFileAsync(data, targetFile);
        saved.Should().BeTrue();
        File.Exists(targetFile).Should().BeTrue();

        var loaded = await _configService.LoadConfigFromFileAsync(targetFile);
        loaded.Should().NotBeNull();
        loaded!["source_dir"].Should().Be("/path/to/src");
        loaded["recursive"].Should().Be(false);
        (loaded["items"] as IEnumerable<object?>).Should().BeEquivalentTo([1L, 2L, 3L]);

        // 4. Corrupted file returns null
        tmp.WriteText("sub/my_config.json", "{broken json");
        (await _configService.LoadConfigFromFileAsync(targetFile)).Should().BeNull();
    }

    [Fact]
    public void PreserveUnreadableConfig_ShouldCopyTheFileNextToItWithoutTouchingIt()
    {
        using var tmp = new TempDirectoryFixture();
        var path = tmp.WriteText("config.json", "{broken json");

        var copy = _configService.PreserveUnreadableConfig(path);

        copy.Should().NotBeNull();
        Path.GetDirectoryName(copy).Should().Be(tmp.Path);
        Path.GetFileName(copy).Should().MatchRegex(@"^config\.unreadable-\d{8}-\d{6}\.json$");
        File.ReadAllText(copy!).Should().Be("{broken json");
        File.ReadAllText(path).Should().Be("{broken json");
    }

    [Fact]
    public void PreserveUnreadableConfig_MissingFile_ShouldReturnNull()
    {
        using var tmp = new TempDirectoryFixture();

        _configService.PreserveUnreadableConfig(tmp.GetFilePath("config.json")).Should().BeNull();
        Directory.EnumerateFileSystemEntries(tmp.Path).Should().BeEmpty();
    }

    [Theory]
    [InlineData("lang_first", RegionPriorityMode.LanguageFirst)]
    [InlineData("country_first", RegionPriorityMode.CountryFirst)]
    [InlineData("COUNTRY_FIRST ", RegionPriorityMode.CountryFirst)]
    [InlineData("weird", RegionPriorityMode.LanguageFirst)]
    [InlineData("", RegionPriorityMode.LanguageFirst)]
    [InlineData(null, RegionPriorityMode.LanguageFirst)]
    public void ParseRegionPriorityMode_ShouldReadTheHistoricalValuesAndDefaultOtherwise(string? value, RegionPriorityMode expected) =>
        EnumExtensions.ParseRegionPriorityMode(value).Should().Be(expected);

    [Theory]
    [InlineData("none", VideoPreference.None)]
    [InlineData("pal", VideoPreference.Pal)]
    [InlineData("ntsc", VideoPreference.Ntsc)]
    [InlineData("secam", VideoPreference.None)]
    [InlineData(null, VideoPreference.None)]
    public void ParseVideoPreference_ShouldReadTheHistoricalValuesAndDefaultOtherwise(string? value, VideoPreference expected) =>
        EnumExtensions.ParseVideoPreference(value).Should().Be(expected);

    [Theory]
    [InlineData("none", MediaPreference.None)]
    [InlineData("floppy", MediaPreference.Floppy)]
    [InlineData("tape", MediaPreference.Tape)]
    [InlineData("cartridge", MediaPreference.Cartridge)]
    [InlineData("cd", MediaPreference.Cd)]
    [InlineData("hd", MediaPreference.Hd)]
    [InlineData("other", MediaPreference.None)]
    [InlineData(null, MediaPreference.None)]
    public void ParseMediaPreference_ShouldReadTheHistoricalValuesAndDefaultOtherwise(string? value, MediaPreference expected) =>
        EnumExtensions.ParseMediaPreference(value).Should().Be(expected);

    [Theory]
    [InlineData("xml", DatExportFormat.LogiqxXml)]
    [InlineData("clrmamepro", DatExportFormat.ClrMamePro)]
    [InlineData("cmp", DatExportFormat.ClrMamePro)]
    [InlineData("auto", DatExportFormat.Auto)]
    [InlineData("txt", DatExportFormat.LogiqxXml)]
    [InlineData(null, DatExportFormat.LogiqxXml)]
    public void ParseDatExportFormat_ShouldReadTheHistoricalValuesAndDefaultOtherwise(string? value, DatExportFormat expected) =>
        EnumExtensions.ParseDatExportFormat(value).Should().Be(expected);

    [Fact]
    public void ToConfigString_ShouldWriteBackEveryHistoricalValue()
    {
        Enum.GetValues<RegionPriorityMode>().Select(v => v.ToConfigString()).Should().Equal("lang_first", "country_first");
        Enum.GetValues<VideoPreference>().Select(v => v.ToConfigString()).Should().Equal("none", "pal", "ntsc");
        Enum.GetValues<MediaPreference>().Select(v => v.ToConfigString()).Should().Equal("none", "floppy", "tape", "cartridge", "cd", "hd");
        Enum.GetValues<DatExportFormat>().Select(v => v.ToConfigString()).Should().Equal("xml", "clrmamepro", "auto");
        Enum.GetValues<LogLevel>().Select(v => v.ToTag()).Should().Equal("OK", "SKIP", "UNMATCHED", "INFO", "WARN", "ERROR", "PLANNED");
    }
}
