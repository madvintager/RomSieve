using System.Text;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Games on several floppy disks. The No-Intro floppy DATs (issue #1, "Acorn - Archimedes (20231029-220453)") name each
/// disk "(Disk N)", describe it as a game of its own and make the disks of a set the clones of one of them; the 1G1R
/// kept a single disk of each game. Also the choice between a demo, a complete set and a set that lacks disks.
/// </summary>
public sealed class FloppyMultidiskTests
{
    private readonly CoreTestServices _services = new();

    private sealed record DatRom(string GameName, string FileName, byte[] Bytes, string? Id = null, string? CloneOfId = null);

    private async Task<(string Src, string Dest, string Dat)> CreateNoIntroCollectionAsync(
        TempDirectoryFixture tmp,
        IReadOnlyList<DatRom> roms)
    {
        var src = tmp.CreateSubdirectory("src");
        var dest = tmp.CreateSubdirectory("dest");
        var games = new StringBuilder();
        foreach (var rom in roms)
        {
            var path = tmp.WriteBytes(Path.Combine("src", rom.FileName), rom.Bytes);
            var crc = await _services.ChecksumService.ComputeFileCrc32Async(path);
            var id = rom.Id is null ? string.Empty : $" id=\"{rom.Id}\"";
            var cloneOf = rom.CloneOfId is null ? string.Empty : $" cloneofid=\"{rom.CloneOfId}\"";
            games.AppendLine($"""
                	<game name="{rom.GameName}"{id}{cloneOf}>
                		<rom name="{rom.FileName}" size="{rom.Bytes.Length}" crc="{crc}"/>
                	</game>
                """);
        }

        var dat = tmp.WriteText("archimedes.dat", $"""
            <?xml version="1.0"?>
            <datafile>
            	<header>
            		<name>Acorn - Archimedes</name>
            		<homepage>No-Intro</homepage>
            	</header>
            {games}</datafile>
            """);
        return (src, dest, dat);
    }

    private static DatRom Disk(string game, int disk, string ext, string id, string? cloneOfId = null, string label = "") =>
        new($"{game} (Disk {disk}{label})", $"{game} (Disk {disk}{label}).{ext}", Encoding.UTF8.GetBytes($"{game}-{disk}"), id, cloneOfId);

    private static SortConfig NoIntroConfig(string src, string dest, string dat) => new()
    {
        SourceDir = src,
        DestinationDir = dest,
        NamingConvention = NamingConvention.NoIntro,
        DatFile = dat,
        Pattern = string.Empty,
        SubfolderMultidisk = true,
        GenerateM3u = true,
        M3uPlacement = M3uPlacementMode.Subfolder,
        TransferMode = TransferMode.Copy,
        Filters = new FilterConfig
        {
            OneGameOneRom = true,
            PriorityLanguages = ["en"]
        }
    };

    private static ProcessItem ItemOf(SortExecutionResult result, string fileName) =>
        result.Items.Single(it => Path.GetFileName(it.SourcePath) == fileName);

    [Fact]
    public async Task NoIntroFloppyDisks_ClonesOfTheirLastDisk_ShouldAllBeKeptWithTheirPlaylist()
    {
        using var tmp = new TempDirectoryFixture();
        const string game = "Alone in the Dark (United Kingdom)";
        var demo = new DatRom($"{game} (Demo)", $"{game} (Demo).adf", "AITD-DEMO"u8.ToArray(), "0232");
        var (src, dest, dat) = await CreateNoIntroCollectionAsync(tmp,
        [
            Disk(game, 1, "jfd", "0142", cloneOfId: "0144"),
            Disk(game, 2, "jfd", "0143", cloneOfId: "0144"),
            Disk(game, 3, "jfd", "0144"),
            demo
        ]);

        var result = await _services.Processor.ExecuteAsync(NoIntroConfig(src, dest, dat), isDryRun: false);

        for (var disk = 1; disk <= 3; disk++)
        {
            var item = ItemOf(result, $"{game} (Disk {disk}).jfd");
            item.Status.Should().Be(FileStatus.Valid, $"disk {disk} belongs to the set the 1G1R keeps");
            item.IsMultiMedia.Should().BeTrue();
            item.Parsed.DiscNumber.Should().Be(disk);
            item.Parsed.DiscTotal.Should().Be(3);
        }

        // The demo, a parent in the DAT whose name is the shortest, loses to the full game
        var demoItem = ItemOf(result, demo.FileName);
        demoItem.Status.Should().Be(FileStatus.Excluded);
        demoItem.EliminatedBy.Should().Be(TiebreakCriterion.OriginalRelease);

        var playlists = Directory.GetFiles(dest, "*.m3u", SearchOption.AllDirectories);
        playlists.Should().ContainSingle();
        var lines = (await File.ReadAllLinesAsync(playlists[0], Encoding.UTF8))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
        lines.Should().Equal($"{game} (Disk 1).jfd", $"{game} (Disk 2).jfd", $"{game} (Disk 3).jfd");
    }

    [Fact]
    public async Task NoIntroFloppyDisks_WithLabelsAndWithoutCloneLinks_ShouldStayTogether()
    {
        using var tmp = new TempDirectoryFixture();
        const string chocks = "Chocks Away (United Kingdom)";
        const string apocalypse = "Apocalypse (United Kingdom)";
        var (src, dest, dat) = await CreateNoIntroCollectionAsync(tmp,
        [
            Disk(chocks, 1, "adf", "0243", label: ", Game Disk"),
            Disk(chocks, 2, "adf", "0245", label: ", Maps Disk"),
            Disk(chocks, 3, "adf", "0247", label: ", Misson Addon"),
            Disk(apocalypse, 1, "adf", "0236"),
            Disk(apocalypse, 2, "adf", "0234")
        ]);

        var result = await _services.Processor.ExecuteAsync(NoIntroConfig(src, dest, dat), isDryRun: true);

        result.Items.Should().HaveCount(5);
        result.Items.Should().OnlyContain(it => it.Status == FileStatus.Valid && it.IsMultidiskComplete);
    }

    [Fact]
    public async Task NoIntroFloppyDisks_AgainstASingleFileVersion_ShouldBeChosenAsAWhole()
    {
        using var tmp = new TempDirectoryFixture();
        const string game = "Chuck Rock (United Kingdom)";
        var strongArm = new DatRom($"{game} (SA Version)", $"{game} (SA Version).jfd", "CHUCK-SA"u8.ToArray(), "0168");
        var (src, dest, dat) = await CreateNoIntroCollectionAsync(tmp,
        [
            Disk(game, 1, "jfd", "0166", cloneOfId: "0167"),
            Disk(game, 2, "jfd", "0167"),
            strongArm
        ]);

        var result = await _services.Processor.ExecuteAsync(NoIntroConfig(src, dest, dat), isDryRun: true);

        // The set is the parent release (disk 1 is the clone of disk 2) and its names are the shortest once its disk
        // labels left aside: it wins whole; before, disk 1 lost alone as a clone, and the StrongARM version won
        ItemOf(result, $"{game} (Disk 1).jfd").Status.Should().Be(FileStatus.Valid);
        ItemOf(result, $"{game} (Disk 2).jfd").Status.Should().Be(FileStatus.Valid);
        ItemOf(result, strongArm.FileName).Status.Should().Be(FileStatus.Excluded);
    }

    [Fact]
    public void IsParentRelease_ShouldIgnoreTheCloneLinksInsideTheRelease()
    {
        static ProcessItem Item(string datGame, string? cloneOf) => new()
        {
            SourcePath = datGame + ".jfd",
            FileSize = 1,
            DatGameName = datGame,
            Parsed = new ParsedRomName { RawFilename = datGame + ".jfd", Title = "Game", CloneOf = cloneOf, ParentTitle = cloneOf }
        };

        // Disks 1 and 2 are clones of disk 3, in the same release: the set is the parent release
        OneGameOneRomResolver.IsParentRelease(
            [Item("Game (Disk 1)", "Game (Disk 3)"), Item("Game (Disk 2)", "Game (Disk 3)"), Item("Game (Disk 3)", null)])
            .Should().BeTrue();

        // A disk that is the clone of a game outside the release makes it a clone, whatever its place
        OneGameOneRomResolver.IsParentRelease(
            [Item("Game (Europe) (Disk 1)", null), Item("Game (Europe) (Disk 2)", "Game (USA) (Disk 2)")])
            .Should().BeFalse();
        OneGameOneRomResolver.IsParentRelease([Item("Game (Japan)", "Game (USA)")]).Should().BeFalse();
        OneGameOneRomResolver.IsParentRelease([Item("Game (USA)", null)]).Should().BeTrue();
    }

    private async Task<SortExecutionResult> SortTosecByNameAsync(TempDirectoryFixture tmp, params string[] fileNames)
    {
        var src = tmp.CreateSubdirectory("src");
        var dest = tmp.CreateSubdirectory("dest");
        foreach (var name in fileNames)
        {
            tmp.WriteText(Path.Combine("src", name), name);
        }

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dest,
            NamingConvention = NamingConvention.Tosec,
            Pattern = string.Empty,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                PriorityLanguages = ["en"]
            }
        };
        return await _services.Processor.ExecuteAsync(config, isDryRun: true);
    }

    [Fact]
    public async Task TosecSetThatLacksADisk_ShouldLoseToACompleteDemo()
    {
        using var tmp = new TempDirectoryFixture();
        const string incomplete = "Wolfchild (1992)(Core Design)(Disk 2 of 2).adf";
        const string demo = "Wolfchild (demo) (1992)(Core Design).adf";

        var result = await SortTosecByNameAsync(tmp, incomplete, demo);

        ItemOf(result, demo).Status.Should().Be(FileStatus.Valid);
        var lost = ItemOf(result, incomplete);
        lost.Status.Should().Be(FileStatus.Excluded);
        lost.EliminatedBy.Should().Be(TiebreakCriterion.CompleteMultidiskSet);
    }

    [Fact]
    public async Task TosecCompleteSet_ShouldBeatItsDemo()
    {
        using var tmp = new TempDirectoryFixture();
        const string disk1 = "Wolfchild (1992)(Core Design)(Disk 1 of 2).adf";
        const string disk2 = "Wolfchild (1992)(Core Design)(Disk 2 of 2).adf";
        const string demo = "Wolfchild (demo) (1992)(Core Design).adf";

        var result = await SortTosecByNameAsync(tmp, disk1, disk2, demo);

        ItemOf(result, disk1).Status.Should().Be(FileStatus.Valid);
        ItemOf(result, disk2).Status.Should().Be(FileStatus.Valid);
        ItemOf(result, demo).Status.Should().Be(FileStatus.Excluded);
    }
}
