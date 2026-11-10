using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// The quarantine of the purges (<c>_purged</c>) and the marker of the destination (<c>.romsieve</c>) are never written
/// by a sort: a main pattern whose text names one as its first folder is refused before any run, and a first name under
/// the destination that a value or a DAT would give them takes a final <c>_</c>. Emptying the quarantine would otherwise
/// delete files the sort wrote, and the scan of a former destination would skip them.
/// </summary>
public sealed class ReservedFolderTests : IDisposable
{
    private readonly CoreTestServices _services = new();
    private readonly TempDirectoryFixture _temp = new();
    private readonly string _src;
    private readonly string _dst;

    public ReservedFolderTests()
    {
        _src = _temp.CreateSubdirectory("src");
        _dst = _temp.CreateSubdirectory("dst");
    }

    public void Dispose() => _temp.Dispose();

    private ParsedRomName ParseName(string name) => _services.NoIntroParser.ParseRomFilename(name, null, 0, NamingConvention.NoIntro);

    // =========================================================================
    // The text of the main pattern
    // =========================================================================

    [Theory]
    [InlineData("_purged/{title}", true)]
    [InlineData("/_purged/{title}/", true)]
    [InlineData("\\_Purged\\{title}", true)]
    [InlineData(" _purged. /{title}", true)]
    [InlineData(".romsieve", true)]
    [InlineData(".ROMSIEVE/{title}", true)]
    [InlineData("{title}/_purged", false)]
    [InlineData("_purged{version}/{title}", false)]
    [InlineData("_purged_/{title}", false)]
    [InlineData("purged/{title}", false)]
    [InlineData("_unmatched/{title}", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AMainPattern_ShouldBeRefusedWhenItsTextNamesAReservedFirstFolder(string? pattern, bool refused)
    {
        PathOrganizerService.StartsWithReservedFolder(pattern).Should().Be(refused);
        SortConfigValidator.GetPatternErrorKey(pattern).Should().Be(refused ? "dialog.error_pattern_reserved_folder" : null);
    }

    [Fact]
    public void TheRefusal_ShouldOnlyConcernAPatternInUse_AfterTheOverlaps()
    {
        var settings = new SortSettings { SourceDir = _src, DestinationDir = _dst, Pattern = "_purged/{title}" };
        SortConfigValidator.GetErrorKey(SortSettingsSerializer.ToSortConfig(settings), isMameRun: false)
            .Should().Be("dialog.error_pattern_reserved_folder");

        // Everything at the root: the pattern is kept, but not used
        settings.FlatDestination = true;
        SortConfigValidator.GetErrorKey(SortSettingsSerializer.ToSortConfig(settings), isMameRun: false).Should().BeNull();

        // A destination that overlaps the source is said first
        settings.FlatDestination = false;
        settings.DestinationDir = _src;
        SortConfigValidator.GetErrorKey(SortSettingsSerializer.ToSortConfig(settings), isMameRun: false)
            .Should().NotBe("dialog.error_pattern_reserved_folder");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARunWithARefusedPattern_ShouldBeRefusedBeforeAnything(bool isDryRun)
    {
        _temp.WriteText("src/Game (1990)(Ocean).adf", "game");
        _temp.WriteText("dst/kept.txt", "kept");
        var config = new SortConfig { SourceDir = _src, DestinationDir = _dst, Pattern = "_purged/{title}", PurgeDestination = true };

        var act = () => _services.Processor.ExecuteAsync(config, isDryRun);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Be(_services.I18n.Tr("dialog.error_pattern_reserved_folder"));
        Directory.GetFileSystemEntries(_dst).Select(Path.GetFileName).Should().Equal(["kept.txt"], "neither purge nor marker, nothing written");
    }

    // =========================================================================
    // The values: the first name under the destination
    // =========================================================================

    private SortConfig Config(string pattern, TransferMode mode = TransferMode.Copy) =>
        new() { SourceDir = _src, DestinationDir = _dst, Pattern = pattern, TransferMode = mode };

    [Theory]
    [InlineData("{title}", "_purged_/_purged (World).sfc")]
    [InlineData("{title}/Games", "_purged_/Games/_purged (World).sfc")]
    [InlineData("Games/{title}", "Games/_purged/_purged (World).sfc")]
    [InlineData("", "_purged (World).sfc")]
    public void AValueNamingTheQuarantine_ShouldNeverBeTheFirstFolder(string pattern, string expected)
    {
        PathOrganizerService.StartsWithReservedFolder(pattern).Should().BeFalse("the folder is only known for each file");

        _services.Organizer.BuildTargetPath(ParseName("_purged (World).sfc"), "_purged (World).sfc", Config(pattern))
            .Should().Be(Path.Combine([_dst, .. expected.Split('/')]));
    }

    [Fact]
    public void ADatOfALotNamedAfterTheQuarantine_ShouldNotBeItsFolder()
    {
        var config = Config("{title}");
        config.FolderPerDat = true;

        _services.Organizer.BuildTargetPath(ParseName("Game (World).sfc"), "Game (World).sfc", config, dat: new DatPlacement("_Purged", null, true))
            .Should().Be(Path.Combine(_dst, "_Purged_", "Game", "Game (World).sfc"));
        _services.Organizer.BuildTargetPath(ParseName("Game (World).sfc"), "Game (World).sfc", config, dat: new DatPlacement(".romsieve", null, true))
            .Should().Be(Path.Combine(_dst, ".romsieve_", "Game", "Game (World).sfc"));
    }

    [Theory]
    [InlineData(".romsieve", ".romsieve_")]
    [InlineData("_purged", "_purged_")]
    [InlineData("_PURGED.", "_PURGED._")]
    [InlineData("_purged.sfc", "_purged.sfc")]
    [InlineData("_purged_", "_purged_")]
    public void ANameReadFromADat_ShouldNotBeTheMarkerNorTheQuarantineAtTheRoot(string fileName, string expected)
    {
        _services.Organizer.BuildTargetPath(ParseName("Game (World).sfc"), fileName, Config(string.Empty))
            .Should().Be(Path.Combine(_dst, expected));
    }

    [Fact]
    public void ANameThatHoldsAFolder_ShouldHaveThatFolderGuarded()
    {
        _services.Organizer.BuildTargetPath(ParseName("Game (World).sfc"), Path.Combine("_purged", "disk.chd"), Config(string.Empty))
            .Should().Be(Path.Combine(_dst, "_purged_", "disk.chd"));
    }

    [Fact]
    public void TheOtherTargets_ShouldStayAsTheyWere()
    {
        _services.Organizer.BuildTargetPath(ParseName("Game (World).sfc"), "Game (World).sfc", Config("{title}/_purged"))
            .Should().Be(Path.Combine(_dst, "Game", "_purged", "Game (World).sfc"));

        // ZIP mode: the archive in the guarded folder
        _services.Organizer.BuildTargetPath(ParseName("_purged (World).sfc"), "_purged (World).sfc", Config("{title}", TransferMode.Zip))
            .Should().Be(Path.Combine(_dst, "_purged_", "_purged (World).zip"));

        // The folder of the non-conforming files is no reserved one
        _services.Organizer.BuildTargetPath(ParseName("junk"), "junk", Config("{title}"))
            .Should().Be(Path.Combine(_dst, PathOrganizerService.UnmatchedFolderName, "junk"));
    }

    // =========================================================================
    // Real sorts
    // =========================================================================

    [Fact]
    public async Task ASortIntoAPurgedDestination_ShouldKeepTheQuarantineForThePurgeAlone()
    {
        _temp.WriteText("src/_purged (1990)(Ocean).adf", "game");
        _temp.WriteText("dst/old.txt", "old");
        DestinationGuard.WriteMarker(_dst);
        var config = new SortConfig { SourceDir = _src, DestinationDir = _dst, Pattern = "{title}", PurgeDestination = true };

        await _services.Processor.ExecuteAsync(config, isDryRun: false);

        File.ReadAllText(Path.Combine(_dst, "_purged_", "_purged (1990)(Ocean).adf")).Should().Be("game");
        Directory.EnumerateFiles(Path.Combine(_dst, DestinationGuard.QuarantineDirectoryName), "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName).Should().Equal(["old.txt"], "the quarantine holds what the purge set aside, and nothing the sort wrote");
    }

    /// <summary>A name read from a DAT may hold folders: one named after the marker would collide with it.</summary>
    [Fact]
    public async Task ARomInAFolderNamedAfterTheMarkerInTheDat_ShouldLeaveTheMarkerAlone()
    {
        var data = "hostile"u8.ToArray();
        _temp.WriteBytes("src/x.sfc", data);
        var dat = _temp.WriteText("dat/hostile.dat", $"""
            <?xml version="1.0"?><datafile><header><name>Hostile</name><homepage>No-Intro</homepage></header>
            <game name="Hostile (World)"><description>Hostile (World)</description><rom name=".romsieve/Hostile (World).sfc" size="{data.Length}" crc="{PatchTestEncoder.Crc(data):x8}"/></game></datafile>
            """);
        var config = new SortConfig
        {
            SourceDir = _src,
            DestinationDir = _dst,
            DatFile = dat,
            NamingConvention = NamingConvention.NoIntro,
            Pattern = string.Empty
        };

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        var written = Path.Combine(_dst, ".romsieve_", "Hostile (World).sfc");
        Path.GetFullPath(result.Items.Single().TargetPath ?? "?").Should().Be(written, "the separator of the DAT name aside");
        File.ReadAllBytes(written).Should().Equal(data);
        File.Exists(Path.Combine(_dst, DestinationGuard.MarkerFileName)).Should().BeTrue("the marker stays a file, the marker");
    }
}
