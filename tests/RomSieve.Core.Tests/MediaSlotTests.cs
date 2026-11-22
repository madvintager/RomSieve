using System.IO;
using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// The media of a release that the 1G1R keeps one file of (<see cref="MultiDiskEvaluator.MediaSlotKey"/>): the two sides
/// of a disk are two media, a disk of documentation is a medium of its own, and among several dumps of one medium the
/// cascade decides and says why.
/// </summary>
public sealed class MediaSlotTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("fr");

    public void Dispose() => _temp.Dispose();

    private async Task<IReadOnlyList<ProcessItem>> SimulateAsync(params string[] files)
    {
        var src = _temp.CreateSubdirectory("src_" + Guid.NewGuid().ToString("N")[..8]);
        var dst = _temp.CreateSubdirectory("dst_" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var name in files)
        {
            File.WriteAllText(Path.Combine(src, name), name);
        }

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{title}/",
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                PriorityLanguages = ["en"],
                ExcludeModifications = true,
                IgnoreModificationsInPriority = true,
                CompleteMultidiskSetsOnly = true,
                SmartMultidiskGrouping = true
            }
        };

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);
        return result.Items.ToList();
    }

    private static ProcessItem Named(IEnumerable<ProcessItem> items, string name) =>
        items.Single(it => it.DisplayName == name);

    private static string[] Kept(IEnumerable<ProcessItem> items) =>
        items.Where(it => it.Status == FileStatus.Valid).Select(it => it.DisplayName).Order(StringComparer.Ordinal).ToArray();

    [Theory]
    [InlineData("Accolade Comics (1987)(Accolade)(Disk 1 of 3 Side B).d64", 1, 3, "B")]
    [InlineData("Ultima V (1988)(Origin)(Disk 4 of 4 Side A)(Castle).d64", 4, 4, "A")]
    [InlineData("Game (1985)(Pub)(Tape 2 Side B).tap", 2, null, "B")]
    [InlineData("Law of the West (1985)(Accolade)(Side A)[h ESI].d64", null, null, "A")]
    public void TosecParser_ShouldReadTheDiskAndTheSideOfOneParenthesis(string name, int? disk, int? total, string side)
    {
        var parsed = _services.TosecParser.ParseTosecFilename(name);

        parsed.IsMultiMedia.Should().BeTrue("a side is a medium even without a DAT");
        parsed.DiscNumber.Should().Be(disk);
        parsed.DiscTotal.Should().Be(total);
        MultiDiskEvaluator.ExtractSide(parsed).Should().Be(side);
    }

    [Fact]
    public async Task OneGameOneRom_ShouldKeepBothSidesOfEveryDisk()
    {
        var items = await SimulateAsync(
            "Accolade Comics (1987)(Accolade)(Disk 1 of 2 Side A).d64",
            "Accolade Comics (1987)(Accolade)(Disk 1 of 2 Side B).d64",
            "Accolade Comics (1987)(Accolade)(Disk 2 of 2 Side A).d64",
            "Accolade Comics (1987)(Accolade)(Disk 2 of 2 Side B).d64");

        items.Should().OnlyContain(it => it.Status == FileStatus.Valid);
    }

    [Fact]
    public async Task OneGameOneRom_ShouldKeepTheBootSideWhenAnotherSideHasAShorterName()
    {
        // Side B used to take the place of disk 1, and the boot side was discarded as its duplicate
        var items = await SimulateAsync(
            "Bards Tale 3 (1988)(Electronic Arts)(Disk 1 of 2 Side A)(Boot).d64",
            "Bards Tale 3 (1988)(Electronic Arts)(Disk 1 of 2 Side B).d64",
            "Bards Tale 3 (1988)(Electronic Arts)(Disk 1 of 2 Side B)(Character).d64",
            "Bards Tale 3 (1988)(Electronic Arts)(Disk 2 of 2 Side A).d64",
            "Bards Tale 3 (1988)(Electronic Arts)(Disk 2 of 2 Side B).d64");

        Kept(items).Should().Equal(
            "Bards Tale 3 (1988)(Electronic Arts)(Disk 1 of 2 Side A)(Boot).d64",
            "Bards Tale 3 (1988)(Electronic Arts)(Disk 1 of 2 Side B).d64",
            "Bards Tale 3 (1988)(Electronic Arts)(Disk 2 of 2 Side A).d64",
            "Bards Tale 3 (1988)(Electronic Arts)(Disk 2 of 2 Side B).d64");

        // The two dumps of the side B of disk 1 are one medium: the shortest name wins, and the duplicate says so (it
        // was said to lose to the "clean/official" dump, which nothing had compared)
        var duplicate = Named(items, "Bards Tale 3 (1988)(Electronic Arts)(Disk 1 of 2 Side B)(Character).d64");
        duplicate.Status.Should().Be(FileStatus.Duplicate);
        duplicate.WinnerReference.Should().Be("Bards Tale 3 (1988)(Electronic Arts)(Disk 1 of 2 Side B).d64");
        duplicate.EliminatedBy.Should().Be(TiebreakCriterion.Name);
        _services.I18n.Format(duplicate.StatusReason!).Should().Be(
            "Doublon 1G1R du même support : Bards Tale 3 (1988)(Electronic Arts)(Disk 1 of 2 Side B).d64 retenu (nom de fichier le plus court)");
    }

    [Fact]
    public async Task OneGameOneRom_ShouldNameAWrittenFileAsTheWinnerOfTheDiscardedReleases()
    {
        // The first name of the winning release, "(Boot)", is a dump that loses its side to a shorter name: the files
        // of the cracked release named it as the winner, although it is not written
        var items = await SimulateAsync(
            "Ultima IV (1986)(Origin)(Disk 1 of 2 Side A)(Boot).d64",
            "Ultima IV (1986)(Origin)(Disk 1 of 2 Side A).d64",
            "Ultima IV (1986)(Origin)(Disk 1 of 2 Side B).d64",
            "Ultima IV (1986)(Origin)(Disk 2 of 2 Side A).d64",
            "Ultima IV (1986)(Origin)(Disk 2 of 2 Side B).d64",
            "Ultima IV (1986)(Origin)(Disk 1 of 2 Side A)[cr ECA].d64",
            "Ultima IV (1986)(Origin)(Disk 1 of 2 Side B)[cr ECA].d64",
            "Ultima IV (1986)(Origin)(Disk 2 of 2 Side A)[cr ECA].d64",
            "Ultima IV (1986)(Origin)(Disk 2 of 2 Side B)[cr ECA].d64");

        Named(items, "Ultima IV (1986)(Origin)(Disk 1 of 2 Side A)(Boot).d64").Status.Should().Be(FileStatus.Duplicate);
        var cracked = items.Where(it => it.DisplayName.Contains("[cr ECA]", StringComparison.Ordinal)).ToList();
        cracked.Should().HaveCount(4).And.OnlyContain(it => it.Status == FileStatus.Excluded);
        cracked.Should().OnlyContain(it => it.WinnerReference == "Ultima IV (1986)(Origin)(Disk 1 of 2 Side A).d64");
        Named(items, "Ultima IV (1986)(Origin)(Disk 1 of 2 Side A).d64").Status.Should().Be(FileStatus.Valid);
    }

    [Fact]
    public async Task OneGameOneRom_ShouldSayThatTwoDumpsOfAMediumHaveTheSameContent()
    {
        // The DAT dropped "Disk 2 of 2 Side B", identical to the side B of disk 1: the file is identified as that side,
        // and the duplicate says why rather than how the name decided
        byte[] sideA = [1, 1], sideB = [2, 2], disk2SideA = [3, 3];
        var src = _temp.CreateSubdirectory("src_identical");
        File.WriteAllBytes(Path.Combine(src, "Rebel Force (1983)(ComputerMat)(Disk 1 of 2 Side A).d64"), sideA);
        File.WriteAllBytes(Path.Combine(src, "Rebel Force (1983)(ComputerMat)(Disk 1 of 2 Side B).d64"), sideB);
        File.WriteAllBytes(Path.Combine(src, "Rebel Force (1983)(ComputerMat)(Disk 2 of 2 Side A).d64"), disk2SideA);
        File.WriteAllBytes(Path.Combine(src, "Rebel Force (1983)(ComputerMat)(Disk 2 of 2 Side B).d64"), sideB);
        var dat = DiskSideCompletenessTests.WriteDat(_temp,
            ("Rebel Force (1983)(ComputerMat)(Disk 1 of 2 Side A).d64", sideA),
            ("Rebel Force (1983)(ComputerMat)(Disk 1 of 2 Side B).d64", sideB),
            ("Rebel Force (1983)(ComputerMat)(Disk 2 of 2 Side A).d64", disk2SideA));

        var result = await _services.Processor.ExecuteAsync(new SortConfig
        {
            SourceDir = src,
            DestinationDir = _temp.CreateSubdirectory("dst_identical"),
            DatFile = dat,
            Filters = new FilterConfig { OneGameOneRom = true, PriorityLanguages = ["en"], CompleteMultidiskSetsOnly = true, SmartMultidiskGrouping = true }
        }, isDryRun: true);

        var duplicate = Named(result.Items, "Rebel Force (1983)(ComputerMat)(Disk 2 of 2 Side B).d64");
        duplicate.Status.Should().Be(FileStatus.Duplicate);
        _services.I18n.Format(duplicate.StatusReason!).Should().Be(
            "Doublon 1G1R du même support : Rebel Force (1983)(ComputerMat)(Disk 1 of 2 Side B).d64 retenu (contenu identique, même CRC32)");
        Kept(result.Items).Should().HaveCount(3);
    }

    [Fact]
    public async Task OneGameOneRom_ShouldKeepTheBestDumpOfASideByTheCascadeAndNameIt()
    {
        var items = await SimulateAsync(
            "Law of the West (1985)(Accolade)(Side A)[h ESI].d64",
            "Law of the West (1985)(Accolade)(Side A)[t +2].d64",
            "Law of the West (1985)(Accolade)(Side A).d64",
            "Law of the West (1985)(Accolade)(Side B).d64");

        Kept(items).Should().Equal(
            "Law of the West (1985)(Accolade)(Side A).d64",
            "Law of the West (1985)(Accolade)(Side B).d64");

        foreach (var name in new[] { "Law of the West (1985)(Accolade)(Side A)[h ESI].d64", "Law of the West (1985)(Accolade)(Side A)[t +2].d64" })
        {
            var duplicate = Named(items, name);
            duplicate.Status.Should().Be(FileStatus.Duplicate);
            duplicate.WinnerReference.Should().Be("Law of the West (1985)(Accolade)(Side A).d64");
            duplicate.EliminatedBy.Should().Be(TiebreakCriterion.OriginalRelease);
            _services.I18n.Format(duplicate.StatusReason!).Should().Be(
                "Doublon 1G1R du même support : Law of the West (1985)(Accolade)(Side A).d64 retenu (version originale vs modifiée)");
        }
    }

    [Fact]
    public async Task OneGameOneRom_ShouldPreferTheLessModifiedDumpOfASide()
    {
        var items = await SimulateAsync(
            "Budokan (1991)(Electronic Arts)(Side A)[h Avatar].d64",
            "Budokan (1991)(Electronic Arts)(Side A)[t +2].d64",
            "Budokan (1991)(Electronic Arts)(Side B).d64");

        Kept(items).Should().Equal(
            "Budokan (1991)(Electronic Arts)(Side A)[t +2].d64",
            "Budokan (1991)(Electronic Arts)(Side B).d64");
        var duplicate = Named(items, "Budokan (1991)(Electronic Arts)(Side A)[h Avatar].d64");
        duplicate.Status.Should().Be(FileStatus.Duplicate);
        _services.I18n.Format(duplicate.EliminationDetail!).Should().Be("version moins altérée / plus fidèle à l'original");
    }

    [Theory]
    [InlineData("[Docs]")]
    [InlineData("[docs]")]
    public async Task OneGameOneRom_ShouldKeepADiskOfDocumentationWithItsGame(string tag)
    {
        var items = await SimulateAsync(
            "Jeff's Dungeon (1990)(Anderson, Jeff).d64",
            $"Jeff's Dungeon (1990)(Anderson, Jeff){tag}.d64");

        items.Should().OnlyContain(it => it.Status == FileStatus.Valid);
    }

    [Fact]
    public async Task OneGameOneRom_ShouldKeepOneOfAGameDiskAndItsSavegameDump()
    {
        // A [savegame] disk is the game disk with a saved game on it, not another medium
        var items = await SimulateAsync(
            "Jeff's Dungeon (1990)(Anderson, Jeff).d64",
            "Jeff's Dungeon (1990)(Anderson, Jeff)[savegame].d64");

        Kept(items).Should().Equal("Jeff's Dungeon (1990)(Anderson, Jeff).d64");
    }

    [Fact]
    public async Task SmartMultidisk_ShouldBorrowBothSidesOfAMissingDisk()
    {
        var items = await SimulateAsync(
            "Amazon (1984)(Trillium)(Disk 1 of 2 Side A)[cr X].d64",
            "Amazon (1984)(Trillium)(Disk 1 of 2 Side B)[cr X].d64",
            "Amazon (1984)(Trillium)(Disk 2 of 2 Side A).d64",
            "Amazon (1984)(Trillium)(Disk 2 of 2 Side B).d64");

        items.Should().OnlyContain(it => it.Status == FileStatus.Valid);
    }

    [Fact]
    public void FindBorrowableCleanDisks_ShouldCountADiskOnceWithAllItsSides()
    {
        ProcessItem Disk(string name) => new()
        {
            SourcePath = name,
            FileSize = 1,
            Parsed = _services.TosecParser.ParseTosecFilename(name)
        };
        var sideA = Disk("Amazon (1984)(Trillium)(Disk 2 of 2 Side A).d64");
        var sideB = Disk("Amazon (1984)(Trillium)(Disk 2 of 2 Side B).d64");
        var cleanDisks = new Dictionary<int, Dictionary<string, ProcessItem>>
        {
            [2] = new() { [MultiDiskEvaluator.MediaSlotKey(sideB)] = sideB, [MultiDiskEvaluator.MediaSlotKey(sideA)] = sideA }
        };
        ProcessItem[] release =
        [
            Disk("Amazon (1984)(Trillium)(Disk 1 of 2 Side A)[cr X].d64"),
            Disk("Amazon (1984)(Trillium)(Disk 1 of 2 Side B)[cr X].d64")
        ];

        var borrowed = MultiDiskEvaluator.FindBorrowableCleanDisks(release, [1, 2], cleanDisks);

        borrowed.Should().Equal((2, sideA), (2, sideB));
    }

    [Fact]
    public void FindBorrowableCleanDisks_ShouldBorrowOnlyTheSideADiskLacks()
    {
        ProcessItem Disk(string name) => new()
        {
            SourcePath = name,
            FileSize = 1,
            Parsed = _services.TosecParser.ParseTosecFilename(name)
        };
        var cleanA = Disk("Ultima IV (1986)(Origin)(Disk 1 of 2 Side A).d64");
        var cleanB = Disk("Ultima IV (1986)(Origin)(Disk 1 of 2 Side B).d64");
        var cleanDisks = new Dictionary<int, Dictionary<string, ProcessItem>>
        {
            [1] = new() { [MultiDiskEvaluator.MediaSlotKey(cleanA)] = cleanA, [MultiDiskEvaluator.MediaSlotKey(cleanB)] = cleanB }
        };

        var borrowed = MultiDiskEvaluator.FindBorrowableCleanDisks([Disk("Ultima IV (1986)(Origin)(Disk 1 of 2 Side A)[cr ECA].d64")], [1], cleanDisks);

        borrowed.Should().Equal((1, cleanB));

        // A dump of the whole disk lacks no side
        MultiDiskEvaluator.FindBorrowableCleanDisks([Disk("Ultima IV (1986)(Origin)(Disk 1 of 2)[cr ECA].d64")], [1], cleanDisks)
            .Should().BeEmpty();
    }
}
