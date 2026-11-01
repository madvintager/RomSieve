using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Named configuration profiles: <c>profiles/&lt;name&gt;.json</c> next to the configuration file, with names that are
/// valid file names on Windows and Linux.
/// </summary>
public sealed class ConfigurationProfilesTests
{
    [Theory]
    [InlineData("Amiga")]
    [InlineData("Zip collection")]
    [InlineData("Préféré-2 (FR)")]
    [InlineData("COM10")]
    public void ValidNames_ShouldBeAccepted(string name) =>
        ConfigurationProfiles.IsValidName(name).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" Amiga")]
    [InlineData("Amiga ")]
    [InlineData(".hidden")]
    [InlineData("name.")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("what?")]
    [InlineData("tab\tname")]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("Lpt1")]
    public void InvalidNames_ShouldBeRefused(string? name)
    {
        ConfigurationProfiles.IsValidName(name).Should().BeFalse();
        if (name is not null)
        {
            var act = () => ConfigurationProfiles.GetPath("config.json", name);
            act.Should().Throw<ArgumentException>();
        }
    }

    [Fact]
    public void TooLongName_ShouldBeRefused() =>
        ConfigurationProfiles.IsValidName(new string('a', ConfigurationProfiles.MaxNameLength + 1)).Should().BeFalse();

    [Fact]
    public void Profiles_ShouldLiveNextToTheConfigurationAndBeListedInOrder()
    {
        using var temp = new TempDirectoryFixture();
        var config = Path.Combine(temp.Path, "config.json");
        ConfigurationProfiles.List(config).Should().BeEmpty("the directory does not exist yet");

        var path = ConfigurationProfiles.GetPath(config, "zeta");
        path.Should().Be(Path.Combine(temp.Path, "profiles", "zeta.json"));
        temp.WriteText(Path.Combine("profiles", "zeta.json"), "{}");
        temp.WriteText(Path.Combine("profiles", "Alpha.json"), "{}");
        temp.WriteText(Path.Combine("profiles", "notes.txt"), "not a profile");

        ConfigurationProfiles.List(config).Should().Equal("Alpha", "zeta");
        ConfigurationProfiles.GetName(path).Should().Be("zeta");
        ConfigurationProfiles.GetName(Path.Combine(temp.Path, "notes.txt")).Should().BeNull();
    }
}
