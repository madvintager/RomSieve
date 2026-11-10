using System.Text.Json;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using static RomSieve.Core.Tests.Fixtures.PatchTestEncoder;

namespace RomSieve.Core.Tests;

/// <summary>
/// The pattern of the folders of the patched versions (<c>patched_pattern</c>): its key in the configuration, empty by
/// default (next to the original ROM) and kept empty, and the check that refuses a pattern that would leave the
/// destination or write in the folders RomSieve keeps for itself, before anything is read or written.
/// </summary>
public sealed class PatchedPatternTests : IDisposable
{
    private readonly CoreTestServices _services = new();
    private readonly TempDirectoryFixture _temp = new();

    public void Dispose() => _temp.Dispose();

    private static Dictionary<string, object?> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
    }

    // =========================================================================
    // Configuration
    // =========================================================================

    [Fact]
    public void ThePattern_ShouldBeEmptyByDefault_AndBeWrittenAndReadBack()
    {
        var defaults = SortSettingsSerializer.Read(new Dictionary<string, object?>());
        defaults.PatchedPattern.Should().BeEmpty("the patched versions go next to their original ROM by default");
        SortSettingsSerializer.ToSortConfig(defaults).PatchedPattern.Should().BeEmpty();

        var data = new Dictionary<string, object?>(StringComparer.Ordinal);
        SortSettingsSerializer.Write(new SortSettings { PatchedPattern = " Patchs/{title} " }, data);
        data["patched_pattern"].Should().Be("Patchs/{title}");

        var read = SortSettingsSerializer.Read(Parse(JsonSerializer.Serialize(data)));
        read.PatchedPattern.Should().Be("Patchs/{title}");
        SortSettingsSerializer.ToSortConfig(read).PatchedPattern.Should().Be("Patchs/{title}");
    }

    [Fact]
    public void AnEmptyPattern_ShouldStayEmpty_UnlikeTheMainPattern()
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal);
        SortSettingsSerializer.Write(new SortSettings { Pattern = "", PatchedPattern = "   " }, data);

        data["pattern"].Should().Be(SortSettings.DefaultPattern, "an empty main pattern is written as the default one");
        data["patched_pattern"].Should().Be(string.Empty, "an empty pattern of the patched versions is a choice");

        // An empty value read back replaces a value given as baseline: it is not taken as missing
        var read = SortSettingsSerializer.Read(Parse("""{ "patched_pattern": "" }"""), new SortSettings { PatchedPattern = "Patchs" });
        read.PatchedPattern.Should().BeEmpty();
    }

    [Fact]
    public void AFlatDestination_ShouldLeaveThePatternOfThePatchedVersions()
    {
        var config = SortSettingsSerializer.ToSortConfig(new SortSettings { FlatDestination = true, PatchedPattern = "{origin_folder}/Patchs" });

        config.Pattern.Should().BeEmpty();
        config.PatchedPattern.Should().Be("{origin_folder}/Patchs", "everything at the root is about the main pattern only");
    }

    [Theory]
    [InlineData("""{ "config_version": 1, "patch_dir": "C:/patches" }""")]
    [InlineData("""{ "config_version": 2, "patch_dir": "C:/patches" }""")]
    [InlineData("""{ "patch_dir": "C:/patches", "patched_pattern": null }""")]
    public void AConfigurationWithoutTheKey_ShouldPutThePatchedVersionsNextToTheirOrigin(string json)
    {
        // A new key, without migration: a configuration of an earlier version keeps its result
        SortSettingsSerializer.Read(Parse(json)).PatchedPattern.Should().BeEmpty();
        ConfigSchema.CurrentVersion.Should().Be(2, "the option does not change the format of the configuration");
    }

    // =========================================================================
    // Check of the pattern
    // =========================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Patchs")]
    [InlineData("Patchs/{title}")]
    [InlineData(@"Patchs\{dat}\{title}\")]
    [InlineData("{origin_folder}/Patchs")]
    [InlineData(" {origin_folder} /Patchs")]
    [InlineData("{dat_folder}/Hacks/{title}")]
    [InlineData("Patchs/_purged")]
    [InlineData("Patchs/.romsieve")]
    [InlineData("Traductions (FR) [v1.2]/{letter}")]
    [InlineData("_unmatched")]
    [InlineData("{bogus}/Patchs")]
    [InlineData("Patchs/{title")]
    [InlineData("...")]
    public void AnAcceptedPattern_ShouldHaveNoProblem(string? pattern)
    {
        // Unknown variables, braces without their pair and _unmatched are only warned about, as for the main pattern
        PathOrganizerService.CheckPatchedPattern(pattern).Should().Be(PatchedPatternProblem.None);
    }

    [Theory]
    [InlineData("/Patchs", PatchedPatternProblem.Rooted)]
    [InlineData(@"\Patchs", PatchedPatternProblem.Rooted)]
    [InlineData(@"\\server\share\Patchs", PatchedPatternProblem.Rooted)]
    [InlineData(" /Patchs", PatchedPatternProblem.Rooted)]
    [InlineData(@"C:\Patchs", PatchedPatternProblem.Drive)]
    [InlineData("C:Patchs", PatchedPatternProblem.Drive)]
    [InlineData("Patchs/d:", PatchedPatternProblem.Drive)]
    [InlineData("..", PatchedPatternProblem.ParentSegment)]
    [InlineData("../Patchs", PatchedPatternProblem.ParentSegment)]
    [InlineData(@"Patchs\..\..\x", PatchedPatternProblem.ParentSegment)]
    [InlineData("Patchs/ .. /x", PatchedPatternProblem.ParentSegment)]
    [InlineData("Patchs:old", PatchedPatternProblem.ForbiddenCharacter)]
    [InlineData("Patchs/a<b", PatchedPatternProblem.ForbiddenCharacter)]
    [InlineData("Patchs/a>b", PatchedPatternProblem.ForbiddenCharacter)]
    [InlineData("Patchs/\"x\"", PatchedPatternProblem.ForbiddenCharacter)]
    [InlineData("Patchs|Hacks", PatchedPatternProblem.ForbiddenCharacter)]
    [InlineData("Patchs?", PatchedPatternProblem.ForbiddenCharacter)]
    [InlineData("Patchs/*", PatchedPatternProblem.ForbiddenCharacter)]
    [InlineData("Patchs\0/x", PatchedPatternProblem.ForbiddenCharacter)]
    [InlineData("Patchs\t2", PatchedPatternProblem.ForbiddenCharacter)]
    [InlineData("_purged", PatchedPatternProblem.ReservedFolder)]
    [InlineData("_PURGED/{title}", PatchedPatternProblem.ReservedFolder)]
    [InlineData("_purged. /{title}", PatchedPatternProblem.ReservedFolder)]
    [InlineData(".romsieve/x", PatchedPatternProblem.ReservedFolder)]
    [InlineData("{dat_folder}/_purged/x", PatchedPatternProblem.ReservedFolder)]
    [InlineData("{origin_folder}-patched", PatchedPatternProblem.FolderVariableNotAlone)]
    [InlineData("Patchs/{dat_folder}{title}", PatchedPatternProblem.FolderVariableNotAlone)]
    [InlineData("x{origin_folder}/Patchs", PatchedPatternProblem.FolderVariableNotAlone)]
    public void ARefusedPattern_ShouldGiveItsProblem(string pattern, PatchedPatternProblem expected)
    {
        PathOrganizerService.CheckPatchedPattern(pattern).Should().Be(expected);
    }

    [Fact]
    public void AControlCharacter_ShouldBeRefused()
    {
        // A null character would stop Path.GetFullPath; kept out of the theory data, whose names it would break
        PathOrganizerService.CheckPatchedPattern("Patchs\0/x").Should().Be(PatchedPatternProblem.ForbiddenCharacter);
        PathOrganizerService.CheckPatchedPattern("Patchs\t2").Should().Be(PatchedPatternProblem.ForbiddenCharacter);
        PathOrganizerService.CheckPatchedPattern("Patchs\u001f").Should().Be(PatchedPatternProblem.ForbiddenCharacter);
    }

    [Fact]
    public void AFolderVariableFirst_ShouldLeaveTheNextFolderChecked()
    {
        // For a single DAT, {dat_folder} is worth no folder: _purged would be the first folder of the destination
        PathOrganizerService.CheckPatchedPattern("{origin_folder}/{dat_folder}/_purged").Should().Be(PatchedPatternProblem.ReservedFolder);
        PathOrganizerService.CheckPatchedPattern("{dat_folder}/.romsieve").Should().Be(PatchedPatternProblem.ReservedFolder);
    }

    // =========================================================================
    // Refusal of a run
    // =========================================================================

    private SortConfig Config(string? patchDir, string patchedPattern) => new()
    {
        SourceDir = Path.Combine(Path.GetTempPath(), "romsieve_patched_pattern_source"),
        DestinationDir = Path.Combine(Path.GetTempPath(), "romsieve_patched_pattern_destination"),
        PatchDir = patchDir,
        PatchedPattern = patchedPattern
    };

    [Theory]
    [InlineData("../Patchs", "dialog.error_patched_pattern_invalid")]
    [InlineData("_purged", "dialog.error_patched_pattern_invalid")]
    [InlineData("Patchs/{title}", null)]
    [InlineData("", null)]
    public void ThePatternOfThePatchedVersions_ShouldBeCheckedWithAPatchFolder(string pattern, string? expected)
    {
        var config = Config(Path.Combine(Path.GetTempPath(), "romsieve_patched_pattern_patches"), pattern);

        SortConfigValidator.GetPatchedPatternErrorKey(config).Should().Be(expected);
        SortConfigValidator.GetErrorKey(config, isMameRun: false).Should().Be(expected);
        SortConfigValidator.GetErrorKey(config, isMameRun: true).Should().Be(expected, "the pattern is checked whatever the run");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void ThePatternOfThePatchedVersions_ShouldNotBeCheckedWithoutAPatchFolder(string? patchDir)
    {
        // Greyed out without a patch folder, and nothing it would write is written: it never blocks a sort
        SortConfigValidator.GetErrorKey(Config(patchDir, "../Patchs"), isMameRun: false).Should().BeNull();
    }

    [Fact]
    public void AnOverlap_ShouldBeReportedBeforeThePattern()
    {
        var config = Config(Path.Combine(Path.GetTempPath(), "romsieve_patched_pattern_patches"), "../Patchs");
        config.DestinationDir = config.SourceDir;

        SortConfigValidator.GetErrorKey(config, isMameRun: false).Should().Be("dialog.error_same_source_dest");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARunWithARefusedPattern_ShouldBeRefusedBeforeAnything(bool isDryRun)
    {
        var japan = new byte[4096];
        new Random(1).NextBytes(japan);
        var src = _temp.CreateSubdirectory("src");
        _temp.WriteBytes("src/Game (Japan).sfc", japan);
        var destination = _temp.CreateSubdirectory("out");
        _temp.WriteText("out/kept.txt", "kept");
        var patches = _temp.CreateSubdirectory("patches");
        _temp.WriteBytes("patches/Game (Japan) (T-En).bps", BpsLinear(japan, [.. japan, 1, 2, 3]));
        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = destination,
            PatchDir = patches,
            PatchedPattern = "../Patchs",
            PurgeDestination = true
        };

        var act = () => _services.Processor.ExecuteAsync(config, isDryRun);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Be(_services.I18n.Tr("dialog.error_patched_pattern_invalid"));
        Directory.GetFileSystemEntries(destination).Select(Path.GetFileName).Should().Equal(["kept.txt"], "neither purge nor marker, nothing written");
        Directory.Exists(Path.Combine(_temp.Path, "Patchs")).Should().BeFalse();
    }
}
