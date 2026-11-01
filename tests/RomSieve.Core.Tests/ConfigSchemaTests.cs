using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.Core.Tests;

/// <summary>
/// Version of the format of <c>config.json</c> and of the profiles: a file without version or of the current version is
/// read as it is, and a file of a later version is read without migration and reported.
/// </summary>
public sealed class ConfigSchemaTests
{
    private static Dictionary<string, object?> Data(params (string Key, object? Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

    private static Dictionary<string, object?> Current(params (string Key, object? Value)[] values) =>
        Data([(ConfigSchema.VersionKey, (long)ConfigSchema.CurrentVersion), .. values]);

    [Theory]
    [InlineData(null, 1)]
    [InlineData("two", 1)]
    [InlineData("2", 2)]
    [InlineData(0L, 1)]
    [InlineData(-3L, 1)]
    [InlineData(1L, 1)]
    [InlineData(2L, 2)]
    [InlineData(7L, 7)]
    public void ReadVersion_ShouldTakeOneForAMissingOrUnreadableVersion(object? version, int expected)
    {
        var data = version is null ? Data() : Data((ConfigSchema.VersionKey, version));

        ConfigSchema.ReadVersion(data).Should().Be(expected);
    }

    [Fact]
    public void Migrate_ShouldBringEveryVersionToTheCurrentOneWithoutChangingItsInput()
    {
        for (var version = 1; version <= ConfigSchema.CurrentVersion; version++)
        {
            var data = Data((ConfigSchema.VersionKey, (long)version), ("exclude_alternate", true));

            var migrated = ConfigSchema.Migrate(data);

            ConfigSchema.ReadVersion(migrated).Should().Be(ConfigSchema.CurrentVersion);
            data[ConfigSchema.VersionKey].Should().Be((long)version, "the dictionary of the caller is left as it is");
            data.Should().HaveCount(2);
        }
    }

    [Fact]
    public void Write_ShouldWriteTheCurrentVersionFirst()
    {
        var data = new Dictionary<string, object?>();

        SortSettingsSerializer.Write(new SortSettings(), data);

        data.Keys.First().Should().Be(ConfigSchema.VersionKey);
        data[ConfigSchema.VersionKey].Should().Be(ConfigSchema.CurrentVersion);
    }

    [Fact]
    public void FileOfTheCurrentVersion_ShouldKeepTheCurrentValueOfAMissingOption()
    {
        var baseline = new SortSettings { OneGameOneRom = true, FilterLanguages = true, NoIntroExcludeAlternate = true };

        var settings = SortSettingsSerializer.Read(Current(("exclude_alternate", false)), baseline);

        settings.OneGameOneRom.Should().BeTrue();
        settings.FilterLanguages.Should().BeTrue();
        settings.NoIntroExcludeAlternate.Should().BeTrue("a No-Intro option is not taken from the TOSEC one");
    }

    [Fact]
    public void FileWithoutVersion_ShouldBeReadInTheCurrentFormat()
    {
        var baseline = new SortSettings { OneGameOneRom = true, FilterLanguages = true };

        var settings = SortSettingsSerializer.Read(Data(("exclude_alternate", true)), baseline);

        settings.ExcludeAlternate.Should().BeTrue();
        settings.NoIntroExcludeAlternate.Should().BeFalse("a missing key takes its default value");
        settings.OneGameOneRom.Should().BeTrue("a missing key keeps the current value");
        settings.FilterLanguages.Should().BeTrue();
    }

    [Fact]
    public void TurningThe1G1ROn_ShouldStillTurnTheLanguageFilterOn()
    {
        var settings = SortSettingsSerializer.Read(Current(("one_game_one_rom", true), ("filter_languages", false)));

        settings.OneGameOneRom.Should().BeTrue();
        settings.FilterLanguages.Should().BeTrue();

        var kept = SortSettingsSerializer.Read(
            Current(("one_game_one_rom", true), ("filter_languages", false)), new SortSettings { OneGameOneRom = true });
        kept.FilterLanguages.Should().BeFalse("the 1G1R was already on");
    }

    [Fact]
    public void WrittenSettings_ShouldBeReadBackIdenticallyWithTheirVersion()
    {
        var settings = new SortSettings
        {
            ExcludeAlternate = true,
            NoIntroExcludeAlternate = false,
            MameExcludeAlternateSets = false,
            OneGameOneRom = false
        };
        var data = new Dictionary<string, object?>();
        SortSettingsSerializer.Write(settings, data);

        SortSettingsSerializer.Read(data).Should().BeEquivalentTo(settings, o => o.WithStrictOrdering());
    }

    [Fact]
    public void FileOfALaterVersion_ShouldBeReadWithoutMigrationAndReported()
    {
        var data = Data((ConfigSchema.VersionKey, (long)ConfigSchema.CurrentVersion + 1),
            ("exclude_alternate", true), ("transfer_mode", "zip"), ("an_option_of_the_future", 3L));

        ConfigSchema.IsNewerThanCurrent(data).Should().BeTrue();
        ConfigSchema.Migrate(data).Should().BeEquivalentTo(data);

        var settings = SortSettingsSerializer.Read(data);
        settings.TransferMode.Should().Be(TransferMode.Zip);

        var warning = ConfigSchema.DescribeNewerVersion(data, "config.json");
        warning.Should().NotBeNull();
        warning!.Key.Should().Be("config.newer_version");
        new LocalizationService("fr").Format(warning).Should().Contain("config.json")
            .And.Contain($"format {ConfigSchema.CurrentVersion + 1}")
            .And.Contain($"format {ConfigSchema.CurrentVersion})");
    }

    [Fact]
    public void FileWithoutVersionOrOfTheCurrentVersion_ShouldNotBeReported()
    {
        ConfigSchema.DescribeNewerVersion(Data(), "config.json").Should().BeNull();
        ConfigSchema.DescribeNewerVersion(Current(), "config.json").Should().BeNull();
    }
}
