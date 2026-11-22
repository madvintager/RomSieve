using System.IO;
using System.IO.Hashing;
using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// A TOSEC set holds a disk only with each side its edition names (<see cref="EditionSides"/>): the sides that the names
/// of the files of the source give, whatever their status, and those of the DAT. A set whose boot side only exists
/// cracked was written without it, as complete; it now borrows the clean sides it lacks, as it borrows a missing disk.
/// </summary>
public sealed class DiskSideCompletenessTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("fr");

    public void Dispose() => _temp.Dispose();

    // The boot side of disk 1 only exists cracked; the cracked release has the side A of both disks
    private static readonly string[] UltimaIV =
    [
        "Ultima IV (1986)(Origin)(Disk 1 of 2 Side A)[cr ECA].d64",
        "Ultima IV (1986)(Origin)(Disk 1 of 2 Side B).d64",
        "Ultima IV (1986)(Origin)(Disk 2 of 2 Side A).d64",
        "Ultima IV (1986)(Origin)(Disk 2 of 2 Side B).d64",
        "Ultima IV (1986)(Origin)(Disk 2 of 2 Side A)[cr ECA].d64",
    ];

    /// <summary>A Logiqx DAT of one ROM per game, each with the CRC32 of its content.</summary>
    internal static string WriteDat(TempDirectoryFixture temp, params (string Name, byte[] Data)[] roms) => temp.WriteText(
        "set_" + Guid.NewGuid().ToString("N")[..8] + ".dat",
        $"""
        <?xml version="1.0"?>
        <datafile>
          <header><name>Test set</name><description>Test set</description><version>1</version></header>
        {string.Join(Environment.NewLine, roms.Select(rom =>
            $"""  <game name="{Path.GetFileNameWithoutExtension(rom.Name)}"><description>{Path.GetFileNameWithoutExtension(rom.Name)}</description><rom name="{rom.Name}" size="{rom.Data.Length}" crc="{Crc32.HashToUInt32(rom.Data):x8}"/></game>"""))}
        </datafile>
        """);

    private SortConfig CreateConfig(string[] files, bool completeSetsOnly = true, string? datFile = null, Func<string, byte[]>? content = null)
    {
        var src = _temp.CreateSubdirectory("src_" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var name in files)
        {
            File.WriteAllBytes(Path.Combine(src, name), content?.Invoke(name) ?? System.Text.Encoding.UTF8.GetBytes(name));
        }

        return new SortConfig
        {
            SourceDir = src,
            DestinationDir = _temp.CreateSubdirectory("dst_" + Guid.NewGuid().ToString("N")[..8]),
            DatFile = datFile ?? string.Empty,
            Pattern = "{title}/",
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                PriorityLanguages = ["en"],
                ExcludeModifications = true,
                IgnoreModificationsInPriority = true,
                CompleteMultidiskSetsOnly = completeSetsOnly,
                SmartMultidiskGrouping = true
            }
        };
    }

    private async Task<IReadOnlyList<ProcessItem>> SimulateAsync(SortConfig config) =>
        (await _services.Processor.ExecuteAsync(config, isDryRun: true)).Items.ToList();

    private static ProcessItem Named(IEnumerable<ProcessItem> items, string name) =>
        items.Single(it => it.DisplayName == name);

    private static string[] Kept(IEnumerable<ProcessItem> items) =>
        items.Where(it => it.Status == FileStatus.Valid).Select(it => it.DisplayName).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task SetLackingABootSide_ShouldBorrowTheCleanSidesItLacks()
    {
        var items = await SimulateAsync(CreateConfig(UltimaIV));

        // The clean release lacks the side A of disk 1: it was written without it, as a complete set
        Kept(items).Should().Equal(
            "Ultima IV (1986)(Origin)(Disk 1 of 2 Side A)[cr ECA].d64",
            "Ultima IV (1986)(Origin)(Disk 1 of 2 Side B).d64",
            "Ultima IV (1986)(Origin)(Disk 2 of 2 Side A)[cr ECA].d64",
            "Ultima IV (1986)(Origin)(Disk 2 of 2 Side B).d64");

        var lacking = Named(items, "Ultima IV (1986)(Origin)(Disk 2 of 2 Side A).d64");
        lacking.Status.Should().Be(FileStatus.Excluded);
        _services.I18n.Format(lacking.StatusReason!).Should().Be(
            "Set multi-disquettes incomplet (1/2 disques complets : face A du disque 1 absente)");
    }

    [Fact]
    public async Task SetLackingASide_ShouldLoseToACompleteSetWithoutTheCompleteSetsOption()
    {
        var items = await SimulateAsync(CreateConfig(UltimaIV, completeSetsOnly: false));

        Kept(items).Should().Equal(
            "Ultima IV (1986)(Origin)(Disk 1 of 2 Side A)[cr ECA].d64",
            "Ultima IV (1986)(Origin)(Disk 1 of 2 Side B).d64",
            "Ultima IV (1986)(Origin)(Disk 2 of 2 Side A)[cr ECA].d64",
            "Ultima IV (1986)(Origin)(Disk 2 of 2 Side B).d64");

        var discarded = Named(items, "Ultima IV (1986)(Origin)(Disk 2 of 2 Side A).d64");
        discarded.EliminatedBy.Should().Be(TiebreakCriterion.CompleteMultidiskSet);
        _services.I18n.Format(discarded.EliminationDetail!).Should().Be(
            "Set multi-disquettes incomplet (1/2 disques complets : face A du disque 1 absente)");
    }

    [Fact]
    public async Task SideNamedByTheDatOnly_ShouldMakeTheSetIncomplete()
    {
        // The source has no dump of the boot side at all: the DAT says that disk 1 has one
        string[] source = UltimaIV[1..4];
        var dat = WriteDat(_temp, [.. UltimaIV.Select(name => (name, System.Text.Encoding.UTF8.GetBytes(name)))]);

        var items = await SimulateAsync(CreateConfig(source, datFile: dat));

        items.Should().HaveCount(3).And.OnlyContain(it => it.Status == FileStatus.Excluded);
        _services.I18n.Format(Named(items, "Ultima IV (1986)(Origin)(Disk 1 of 2 Side B).d64").StatusReason!).Should().Be(
            "Set multi-disquettes incomplet (1/2 disques complets : face A du disque 1 absente)");
    }

    [Fact]
    public async Task CleanSet_ShouldBeatACrackedSideThatBorrowsTheCleanSideA()
    {
        // The cracked side B borrows the clean side A, which sorts first: the set ranked as that clean dump, and won
        // over the clean set by chance
        var items = await SimulateAsync(CreateConfig(
        [
            "Mars Saga, The (1988)(Electronic Arts)(Side A).d64",
            "Mars Saga, The (1988)(Electronic Arts)(Side B).d64",
            "Mars Saga, The (1988)(Electronic Arts)(Side B)[cr PWR][a].d64",
        ]));

        Kept(items).Should().Equal("Mars Saga, The (1988)(Electronic Arts)(Side A).d64", "Mars Saga, The (1988)(Electronic Arts)(Side B).d64");
        Named(items, "Mars Saga, The (1988)(Electronic Arts)(Side B)[cr PWR][a].d64").EliminatedBy.Should().Be(TiebreakCriterion.OriginalRelease);
    }

    [Fact]
    public async Task SetNumberedFromDiskZero_ShouldStayComplete()
    {
        // Disk 0 is a disk of the set, not a single disk whose sides are named alone
        string[] files = [.. Enumerable.Range(0, 6).Select(n => $"Jadeworld II (2018-11-01)(Baber, John)(Disk {n} of 6)[ACS].d64")];

        var items = await SimulateAsync(CreateConfig(files));

        items.Should().OnlyContain(it => it.Status == FileStatus.Valid);
    }

    [Fact]
    public async Task SetLackingAWholeDisk_ShouldCountTheDisksItHolds()
    {
        // Disk 1 is held without its side A, disks 2 and 3 are missing: one disk of three is there
        var items = await SimulateAsync(CreateConfig(
        [
            "Wizardry V (1988)(Sir-Tech)(Disk 1 of 3 Side A)[cr NEC].d64",
            "Wizardry V (1988)(Sir-Tech)(Disk 1 of 3 Side B).d64",
        ]));

        _services.I18n.Format(Named(items, "Wizardry V (1988)(Sir-Tech)(Disk 1 of 3 Side B).d64").StatusReason!).Should().Be(
            "Set multi-disquettes incomplet (1/3 disques trouvés)");
    }

    [Fact]
    public async Task SetOfWholeDisks_ShouldNotDependOnTheSidesOfAnotherDump()
    {
        // A dump without a side is the whole disk
        var items = await SimulateAsync(CreateConfig(
        [
            "Zak (1988)(Lucas)(Disk 1 of 2).d64",
            "Zak (1988)(Lucas)(Disk 2 of 2).d64",
            "Zak (1988)(Lucas)(Disk 1 of 2 Side B)[cr X].d64",
        ]));

        Kept(items).Should().Equal("Zak (1988)(Lucas)(Disk 1 of 2).d64", "Zak (1988)(Lucas)(Disk 2 of 2).d64");
    }

    [Fact]
    public async Task SingleDiskLackingACleanSide_ShouldBorrowItForTheCrackedSide()
    {
        // Only the clean side B was kept, alone
        var items = await SimulateAsync(CreateConfig(
        [
            "Karnov (1988)(Activision)(Side A)[cr F4CG].d64",
            "Karnov (1988)(Activision)(Side B).d64",
        ]));

        Kept(items).Should().Equal("Karnov (1988)(Activision)(Side A)[cr F4CG].d64", "Karnov (1988)(Activision)(Side B).d64");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SingleDiskLackingASide_ShouldOnlyBeKeptWithoutTheCompleteSetsOption(bool completeSetsOnly)
    {
        // Side B only exists as a bad dump, which the filters leave out: it still says that the disk has a side B
        var items = await SimulateAsync(CreateConfig(
            ["Mord an Bord (1986)(Ariolasoft)(Side A).d64", "Mord an Bord (1986)(Ariolasoft)(Side B)[b].d64"],
            completeSetsOnly));

        var sideA = Named(items, "Mord an Bord (1986)(Ariolasoft)(Side A).d64");
        if (completeSetsOnly)
        {
            sideA.Status.Should().Be(FileStatus.Excluded);
            _services.I18n.Format(sideA.StatusReason!).Should().Be("Jeu incomplet (1/2 faces trouvées : face B absente)");
        }
        else
        {
            sideA.Status.Should().Be(FileStatus.Valid);
        }
    }

    [Fact]
    public async Task SingleDiskWithBothSides_ShouldStayComplete()
    {
        var items = await SimulateAsync(CreateConfig(
        [
            "Law of the West (1985)(Accolade)(Side A).d64",
            "Law of the West (1985)(Accolade)(Side B).d64",
            "Law of the West (1985)(Accolade)(Side A)[h ESI].d64",
        ]));

        Kept(items).Should().Equal("Law of the West (1985)(Accolade)(Side A).d64", "Law of the West (1985)(Accolade)(Side B).d64");
    }

    [Fact]
    public async Task RealSort_ShouldGroupTheBorrowedSidesWithTheirSetInOnePlaylist()
    {
        var config = CreateConfig(UltimaIV);
        config.GenerateM3u = true;

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        // The clean sides join the playlist of the cracked set, and make no playlist of their own
        var playlists = new MultiDiskEvaluator(config, _services.FilterService, _services.I18n).GroupPlaylistReleases(result.Items.ToList());
        playlists.Should().ContainSingle().Which.Select(it => it.DisplayName).Order(StringComparer.Ordinal).Should().Equal(
            "Ultima IV (1986)(Origin)(Disk 1 of 2 Side A)[cr ECA].d64",
            "Ultima IV (1986)(Origin)(Disk 1 of 2 Side B).d64",
            "Ultima IV (1986)(Origin)(Disk 2 of 2 Side A)[cr ECA].d64",
            "Ultima IV (1986)(Origin)(Disk 2 of 2 Side B).d64");
        Directory.EnumerateFiles(config.DestinationDir, "*.m3u", SearchOption.AllDirectories).Should().ContainSingle();
    }

    [Fact]
    public void Count_ShouldHoldADiskOnlyWithEachSideOfItsEdition()
    {
        ProcessItem Disk(string name) => new()
        {
            SourcePath = name,
            FileSize = 1,
            Parsed = _services.TosecParser.ParseTosecFilename(name)
        };
        var sides = new Dictionary<int, SortedSet<string>>
        {
            [1] = new(StringComparer.Ordinal) { "A", "B" },
            [2] = new(StringComparer.Ordinal) { "A", "B" }
        };

        EditionSides.Count([Disk("G (1990)(P)(Disk 1 of 2 Side B).d64"), Disk("G (1990)(P)(Disk 2 of 2 Side A).d64"), Disk("G (1990)(P)(Disk 2 of 2 Side B).d64")], sides)
            .Should().Be(new SetMedia(1, 2, 1, "A"));
        EditionSides.Count([Disk("G (1990)(P)(Disk 1 of 2).d64"), Disk("G (1990)(P)(Disk 2 of 2 Side A).d64"), Disk("G (1990)(P)(Disk 2 of 2 Side B).d64")], sides)
            .Should().Be(new SetMedia(2, 2, null, null));

        // Without a side named by the edition, every disk held is whole
        EditionSides.Count([Disk("G (1990)(P)(Disk 1 of 2 Side B).d64")], knownSides: null).Should().Be(new SetMedia(1, 1, null, null));
    }
}
