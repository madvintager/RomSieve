using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Sort of a MAME collection of non-merged sets (<see cref="MameSetIdentifier"/>): one item per archive, identified by
/// its short name or by its ROMs, compared with its machine (missing ROMs and disks, set relying on its parent), CHD
/// disks in the folder of their set, 1G1R by parent set, and archives transferred as they are (never re-zipped nor
/// renamed). The DAT is written like the output of <c>mame -listxml</c>, with tiny ROMs: only their CRC32 matter.
/// </summary>
public sealed class MameSortTests : IDisposable
{
    private readonly TempDirectoryFixture _tmp = new();
    private readonly CoreTestServices _services = new();
    private readonly string _source;
    private readonly string _destination;
    private readonly string _datPath;

    private static readonly byte[] Bios = Encoding.ASCII.GetBytes("NEO-GEO BIOS");
    private static readonly byte[] MetalSlug = Encoding.ASCII.GetBytes("METAL SLUG PROGRAM");
    private static readonly byte[] Sf2Program = Encoding.ASCII.GetBytes("SF2 WORLD PROGRAM");
    private static readonly byte[] Sf2Graphics = Encoding.ASCII.GetBytes("SF2 SHARED GRAPHICS");
    private static readonly byte[] Sf2UsaProgram = Encoding.ASCII.GetBytes("SF2 USA PROGRAM");
    private static readonly byte[] Sf2JapanProgram = Encoding.ASCII.GetBytes("SF2 JAPAN PROGRAM");
    private static readonly byte[] AtariTetris = Encoding.ASCII.GetBytes("ATARI TETRIS");
    private static readonly byte[] SegaTetris = Encoding.ASCII.GetBytes("SEGA TETRIS");
    private static readonly byte[] Area51 = Encoding.ASCII.GetBytes("AREA 51 BOOT");
    private static readonly string Area51DiskSha1 = ChdTestFile.Sha1Of("area51");

    public MameSortTests()
    {
        _source = _tmp.CreateSubdirectory("src");
        _destination = _tmp.GetFilePath("dst");
        _datPath = _tmp.WriteText("mame.xml", BuildDat());
    }

    public void Dispose() => _tmp.Dispose();

    private static string Crc(byte[] data) => Crc32.HashToUInt32(data).ToString("x8");

    private static string Rom(string name, byte[] data, string? merge = null) =>
        $"""<rom name="{name}"{(merge is null ? "" : $" merge=\"{merge}\"")} size="{data.Length}" crc="{Crc(data)}"/>""";

    private static string BuildDat() => $"""
        <?xml version="1.0"?>
        <mame build="0.289 (test)">
        	<machine name="neogeo" isbios="yes"><description>Neo-Geo MV-6F</description><year>1990</year><manufacturer>SNK</manufacturer>{Rom("bios.rom", Bios)}<driver status="good"/></machine>
        	<machine name="mslug" romof="neogeo"><description>Metal Slug - Super Vehicle-001</description><year>1996</year><manufacturer>Nazca</manufacturer>{Rom("bios.rom", Bios, merge: "bios.rom")}{Rom("mslug.p1", MetalSlug)}<driver status="good"/></machine>
        	<machine name="sf2"><description>Street Fighter II: The World Warrior (World 910522)</description><year>1991</year><manufacturer>Capcom</manufacturer>{Rom("sf2.a", Sf2Program)}{Rom("sf2.b", Sf2Graphics)}<driver status="good"/></machine>
        	<machine name="sf2u" cloneof="sf2" romof="sf2"><description>Street Fighter II: The World Warrior (USA 910206)</description><year>1991</year><manufacturer>Capcom</manufacturer>{Rom("sf2u.a", Sf2UsaProgram)}{Rom("sf2.b", Sf2Graphics, merge: "sf2.b")}<driver status="good"/></machine>
        	<machine name="sf2j" cloneof="sf2" romof="sf2"><description>Street Fighter II: The World Warrior (Japan 911210)</description><year>1991</year><manufacturer>Capcom</manufacturer>{Rom("sf2j.a", Sf2JapanProgram)}{Rom("sf2.b", Sf2Graphics, merge: "sf2.b")}<driver status="good"/></machine>
        	<machine name="atetris"><description>Tetris (set 1)</description><year>1988</year><manufacturer>Atari Games</manufacturer>{Rom("atetris.rom", AtariTetris)}<driver status="good"/></machine>
        	<machine name="tetris"><description>Tetris (Japan, System E)</description><year>1988</year><manufacturer>Sega</manufacturer>{Rom("tetris.rom", SegaTetris)}<driver status="good"/></machine>
        	<machine name="area51"><description>Area 51 (R3000)</description><year>1996</year><manufacturer>Atari Games</manufacturer>{Rom("area51.rom", Area51)}<disk name="area51" sha1="{Area51DiskSha1}"/><driver status="good"/></machine>
        </mame>
        """;

    private string Zip(string name, params (string Member, byte[] Data)[] members)
    {
        var path = Path.Combine(_source, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (member, data) in members)
        {
            using var stream = zip.CreateEntry(member).Open();
            stream.Write(data);
        }
        return path;
    }

    private void CreateCollection()
    {
        Zip("neogeo.zip", ("bios.rom", Bios));
        Zip("mslug.zip", ("bios.rom", Bios), ("mslug.p1", MetalSlug));
        Zip("sf2.zip", ("sf2.a", Sf2Program), ("sf2.b", Sf2Graphics));
        Zip("sf2u.zip", ("sf2u.a", Sf2UsaProgram), ("sf2.b", Sf2Graphics));

        // A split clone: its shared ROM is only in the set of its parent
        Zip("sf2j.zip", ("sf2j.a", Sf2JapanProgram));
        Zip("atetris.zip", ("atetris.rom", AtariTetris));
        Zip("tetris.zip", ("tetris.rom", SegaTetris));
        Zip("area51.zip", ("area51.rom", Area51));
        _tmp.WriteBytes(Path.Combine("src", "area51", "area51.chd"), ChdTestFile.Create("area51"));
    }

    private SortConfig Config(Action<FilterConfig>? filters = null)
    {
        var config = new SortConfig
        {
            SourceDir = _source,
            DestinationDir = _destination,
            DatFile = _datPath,
            Pattern = string.Empty
        };
        filters?.Invoke(config.Filters);
        return config;
    }

    private static ProcessItem Item(SortExecutionResult result, string fileName) =>
        result.Items.Single(it => string.Equals(Path.GetFileName(it.SourcePath), fileName, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task Simulation_ShouldMakeOneItemPerSetAndCompareItWithItsMachine()
    {
        CreateCollection();

        var result = await _services.Processor.ExecuteAsync(Config());

        result.Items.Should().HaveCount(8);
        result.Stats.TotalScanned.Should().Be(8);
        result.Items.Should().OnlyContain(it => it.ArchiveMember == null);

        var sf2 = Item(result, "sf2.zip");
        sf2.Status.Should().Be(FileStatus.Valid);
        sf2.IsWholeArchive.Should().BeTrue();
        sf2.TargetFilename.Should().Be("sf2.zip");
        sf2.DatGameName.Should().Be("sf2");
        sf2.FileSize.Should().Be(new FileInfo(Path.Combine(_source, "sf2.zip")).Length);
        sf2.MameSet.Should().Be(new MameSetInfo("sf2", 0, 0, 0));
        sf2.Parsed.Title.Should().Be("Street Fighter II: The World Warrior");
        sf2.Parsed.Publisher.Should().Be("Capcom");
        sf2.Parsed.Convention.Should().Be(NamingConvention.Mame);

        // A non-merged set holds the ROMs of its BIOS
        Item(result, "mslug.zip").MameSet!.IsComplete.Should().BeTrue();

        var split = Item(result, "sf2j.zip").MameSet!;
        split.IsComplete.Should().BeFalse();
        split.MissingRoms.Should().Be(1);
        split.RequiredSet.Should().Be("sf2");

        // Without "complete sets only", an incomplete set is kept
        Item(result, "sf2j.zip").Status.Should().Be(FileStatus.Valid);
    }

    [Fact]
    public async Task Chd_ShouldJoinItsSetAndGoIntoTheFolderOfTheSet()
    {
        CreateCollection();

        var result = await _services.Processor.ExecuteAsync(Config(), isDryRun: false);

        var area51 = Item(result, "area51.zip");
        area51.MameSet.Should().Be(new MameSetInfo("area51", 0, 0, 0));
        area51.RelatedItems.Should().ContainSingle()
            .Which.TargetFilename.Should().Be(Path.Combine("area51", "area51.chd"));

        File.Exists(Path.Combine(_destination, "area51.zip")).Should().BeTrue();
        File.ReadAllBytes(Path.Combine(_destination, "area51", "area51.chd"))
            .Should().Equal(File.ReadAllBytes(Path.Combine(_source, "area51", "area51.chd")));
    }

    [Fact]
    public async Task MissingChd_ShouldMakeTheSetIncomplete()
    {
        Zip("area51.zip", ("area51.rom", Area51));

        var result = await _services.Processor.ExecuteAsync(Config());

        Item(result, "area51.zip").MameSet.Should().Be(new MameSetInfo("area51", 0, 1, 0));
        result.MissingRoms.Should().Contain(e => e.IsDisk && e.GameName == "area51");
    }

    [Fact]
    public async Task ChdWithoutItsArchive_ShouldStandForItsSet()
    {
        _tmp.WriteBytes(Path.Combine("src", "area51", "area51.chd"), ChdTestFile.Create("area51"));

        var result = await _services.Processor.ExecuteAsync(Config());

        var disk = result.Items.Should().ContainSingle().Subject;
        disk.TargetFilename.Should().Be(Path.Combine("area51", "area51.chd"));
        disk.MameSet.Should().Be(new MameSetInfo("area51", 1, 0, 0));
        disk.FinalTargetPath.Should().Be(Path.Combine(_destination, "area51", "area51.chd"));
    }

    [Fact]
    public async Task ChdFolder_ShouldJoinItsChdsToTheirSets()
    {
        // As in a full set: the ROM sets in one folder, the CHDs in another, one subfolder per set
        Zip("area51.zip", ("area51.rom", Area51));
        _tmp.WriteBytes(Path.Combine("chd", "area51", "area51.chd"), ChdTestFile.Create("area51"));
        _tmp.WriteText(Path.Combine("chd", "readme.txt"), "not a CHD");
        var config = Config();
        config.ChdDir = _tmp.GetFilePath("chd");
        var log = new LogCollector();

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false, logProgress: log);

        var area51 = result.Items.Should().ContainSingle().Subject;
        area51.MameSet.Should().Be(new MameSetInfo("area51", 0, 0, 0));
        area51.RelatedItems.Should().ContainSingle().Which.SourcePath.Should().Be(_tmp.GetFilePath(Path.Combine("chd", "area51", "area51.chd")));
        File.Exists(Path.Combine(_destination, "area51", "area51.chd")).Should().BeTrue();
        log.WithKey("log.chd_dir_scanned").Should().ContainSingle();
        File.Exists(_tmp.GetFilePath(Path.Combine("chd", "readme.txt"))).Should().BeTrue("the other files of the folder are not sorted");
    }

    [Fact]
    public async Task MergedChds_ShouldStayInTheFolderOfTheParentAndCountForEveryClone()
    {
        // As in a merged CHD collection: the disk of a clone is in the folder of the parent, and another clone uses the
        // same disk without a merge attribute; MAME finds it there for both
        var clone1Rom = Encoding.ASCII.GetBytes("AREA 51 CLONE 1");
        var clone2Rom = Encoding.ASCII.GetBytes("AREA 51 CLONE 2");
        var cloneDisk = ChdTestFile.Sha1Of("area51t");
        File.WriteAllText(_datPath, BuildDat().Replace(
            "</mame>",
            $"""
            <machine name="area51t" cloneof="area51" romof="area51"><description>Area 51 (Time Warner license, Oct 17, 1996)</description>{Rom("t1.rom", clone1Rom)}<disk name="area51t" sha1="{cloneDisk}"/></machine>
            <machine name="area51ta" cloneof="area51" romof="area51"><description>Area 51 (Time Warner license, Nov 27, 1995)</description>{Rom("t2.rom", clone2Rom)}<disk name="area51t" sha1="{cloneDisk}"/></machine>
            </mame>
            """,
            StringComparison.Ordinal));
        Zip("area51.zip", ("area51.rom", Area51), ("t1.rom", clone1Rom), ("t2.rom", clone2Rom));
        _tmp.WriteBytes(Path.Combine("chd", "area51", "area51.chd"), ChdTestFile.Create("area51"));
        _tmp.WriteBytes(Path.Combine("chd", "area51", "area51t.chd"), ChdTestFile.Create("area51t"));
        var config = Config();
        config.ChdDir = _tmp.GetFilePath("chd");

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        var area51 = result.Items.Should().ContainSingle().Subject;
        area51.RelatedItems.Select(r => r.TargetFilename).Should().BeEquivalentTo(
            Path.Combine("area51", "area51.chd"), Path.Combine("area51", "area51t.chd"));
        File.Exists(Path.Combine(_destination, "area51", "area51t.chd")).Should().BeTrue("the disk keeps the folder MAME finds it in");
        result.MissingRoms.Should().NotContain(e => e.GameName == "area51" || e.GameName == "area51t" || e.GameName == "area51ta");
    }

    [Fact]
    public async Task DiskSharedByTwoGames_ShouldGoToTheFamilyOfItsFolder()
    {
        // Two games list the same disk; the CHD is in the folder of the second one (merged CHDs, as for ddr4mj and
        // ddr4msj in MAME): it belongs to that family, even though the other game comes first in the DAT
        var sharedDisk = ChdTestFile.Sha1Of("shared");
        var firstRom = Encoding.ASCII.GetBytes("FIRST GAME");
        var firstCloneRom = Encoding.ASCII.GetBytes("FIRST CLONE");
        var secondRom = Encoding.ASCII.GetBytes("SECOND GAME");
        var secondCloneRom = Encoding.ASCII.GetBytes("SECOND CLONE");
        File.WriteAllText(_datPath, BuildDat().Replace(
            "</mame>",
            $"""
            <machine name="first"><description>First</description>{Rom("first.rom", firstRom)}</machine>
            <machine name="firstj" cloneof="first" romof="first"><description>First (Japan)</description>{Rom("firstj.rom", firstCloneRom)}<disk name="shared" sha1="{sharedDisk}"/></machine>
            <machine name="second"><description>Second</description>{Rom("second.rom", secondRom)}</machine>
            <machine name="secondj" cloneof="second" romof="second"><description>Second (Japan)</description>{Rom("secondj.rom", secondCloneRom)}<disk name="shared" sha1="{sharedDisk}"/></machine>
            </mame>
            """,
            StringComparison.Ordinal));
        Zip("first.zip", ("first.rom", firstRom), ("firstj.rom", firstCloneRom));
        Zip("second.zip", ("second.rom", secondRom), ("secondj.rom", secondCloneRom));
        _tmp.WriteBytes(Path.Combine("src", "second", "shared.chd"), ChdTestFile.Create("shared"));

        var result = await _services.Processor.ExecuteAsync(Config());

        Item(result, "second.zip").RelatedItems.Should().ContainSingle()
            .Which.TargetFilename.Should().Be(Path.Combine("second", "shared.chd"), "the disk stays where MAME finds it for secondj");
        Item(result, "first.zip").RelatedItems.Should().BeEmpty();
        result.MissingRoms.Should().NotContain(e => e.GameName == "secondj");
        result.MissingRoms.Should().Contain(e => e.GameName == "firstj" && e.IsDisk, "MAME would not find it for firstj");
    }

    [Fact]
    public async Task ChdFolderOverlappingTheDestination_ShouldBeRefusedForAMameSort()
    {
        Zip("area51.zip", ("area51.rom", Area51));
        var config = Config();
        config.ChdDir = _tmp.CreateSubdirectory(Path.Combine("dst", "chd"));

        var act = () => _services.Processor.ExecuteAsync(config);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Be(_services.I18n.Tr("dialog.error_dest_contains_chd"));
    }

    [Fact]
    public async Task ChdFolderInsideTheSource_ShouldNotListItsChdsTwice()
    {
        Zip("area51.zip", ("area51.rom", Area51));
        _tmp.WriteBytes(Path.Combine("src", "chd", "area51", "area51.chd"), ChdTestFile.Create("area51"));
        var config = Config();
        config.ChdDir = Path.Combine(_source, "chd");

        var result = await _services.Processor.ExecuteAsync(config);

        result.Items.Should().ContainSingle().Which.RelatedItems.Should().ContainSingle();
    }

    [Fact]
    public async Task ChdFolder_ShouldBeIgnoredOutsideMameAndReportedWhenMissing()
    {
        _tmp.WriteText(Path.Combine("src", "Game (1990)(Ocean).adf"), "game");
        _tmp.WriteBytes(Path.Combine("chd", "area51", "area51.chd"), ChdTestFile.Create("area51"));
        var config = Config();
        config.DatFile = null;
        config.ChdDir = _tmp.GetFilePath("chd");
        var log = new LogCollector();

        var result = await _services.Processor.ExecuteAsync(config, logProgress: log);

        result.Items.Should().ContainSingle();
        log.Messages.Should().NotContain(m => m.Message.Key.StartsWith("log.chd_dir", StringComparison.Ordinal),
            "the CHD folder stays in the configuration for the next MAME sort: a sort of another convention says nothing of it");

        Zip("area51.zip", ("area51.rom", Area51));
        var missing = Config();
        missing.ChdDir = _tmp.GetFilePath("no_such_folder");
        var missingLog = new LogCollector();
        await _services.Processor.ExecuteAsync(missing, logProgress: missingLog);
        missingLog.WithKey("log.chd_dir_not_found").Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warn);
    }

    [Fact]
    public async Task RealSort_ShouldCopyEachArchiveAsItIs()
    {
        CreateCollection();

        await _services.Processor.ExecuteAsync(Config(), isDryRun: false);

        foreach (var name in new[] { "neogeo.zip", "mslug.zip", "sf2.zip", "sf2u.zip", "sf2j.zip", "atetris.zip", "tetris.zip", "area51.zip" })
        {
            File.ReadAllBytes(Path.Combine(_destination, name)).Should().Equal(File.ReadAllBytes(Path.Combine(_source, name)), name);
        }
    }

    [Fact]
    public async Task ZipMode_ShouldNeitherRezipASetNorItsDisks()
    {
        CreateCollection();
        var config = Config();
        config.TransferMode = TransferMode.Zip;

        await _services.Processor.ExecuteAsync(config, isDryRun: false);

        File.ReadAllBytes(Path.Combine(_destination, "sf2.zip")).Should().Equal(File.ReadAllBytes(Path.Combine(_source, "sf2.zip")));
        File.Exists(Path.Combine(_destination, "area51", "area51.chd")).Should().BeTrue();
        Directory.GetFiles(_destination, "*.zip", SearchOption.AllDirectories).Should().HaveCount(8);
    }

    [Fact]
    public async Task MachinesWithNothingToDump_ShouldOnlyBeKeptOnDemand()
    {
        // As the Genesis of MAME 0.289: the console has no ROM of its own, its merged archive only holds the ROM of a
        // clone; and a machine whose only ROM was never dumped
        var tmss = Encoding.ASCII.GetBytes("TMSS BOOT ROM");
        var other = Encoding.ASCII.GetBytes("OTHER SUITE");
        File.WriteAllText(_datPath, $"""
            <?xml version="1.0"?>
            <mame build="0.289 (test)">
            	<machine name="console"><description>Console (USA, NTSC)</description><year>1989</year><manufacturer>Sega</manufacturer><driver status="good"/></machine>
            	<machine name="consolet" cloneof="console" romof="console"><description>Console (USA, NTSC, with TMSS chip)</description><year>1989</year><manufacturer>Sega</manufacturer>{Rom("tmss.bin", tmss)}<driver status="good"/></machine>
            	<machine name="suite"><description>Test suite</description><year>2008</year><manufacturer>Homebrew</manufacturer><rom name="suite.bin" size="512" status="nodump"/><driver status="good"/></machine>
            	<machine name="suite2" cloneof="suite" romof="suite"><description>Test suite 2</description><year>2008</year><manufacturer>Homebrew</manufacturer>{Rom("suite2.bin", other)}<driver status="good"/></machine>
            </mame>
            """);
        Zip("console.zip", ("consolet/tmss.bin", tmss));
        Zip("suite.zip", ("suite2/suite2.bin", other));

        var result = await _services.Processor.ExecuteAsync(Config(f => f.OneGameOneRom = false));

        foreach (var set in new[] { "console.zip", "suite.zip" })
        {
            Item(result, set).Parsed.Machine!.HasNothingToDump.Should().BeTrue(set);
            Item(result, set).Status.Should().Be(FileStatus.Excluded, set);
            _services.I18n.Format(Item(result, set).StatusReason!).Should().Be("Machine sans ROM à dumper exclue");
        }

        // Kept on demand, with the clones their archive holds
        result = await _services.Processor.ExecuteAsync(Config(f =>
        {
            f.OneGameOneRom = false;
            f.MameExcludeNothingToDump = false;
        }));
        Item(result, "console.zip").Status.Should().Be(FileStatus.Valid);
        Item(result, "suite.zip").Status.Should().Be(FileStatus.Valid);

        // The filtered DAT of the catalog never had them: the simulation of the collection now says the same
        var catalog = await _services.Processor.SelectFromCatalogAsync(Config(f => f.OneGameOneRom = false));
        catalog.FilteredDat.Games.Select(g => g.Name).Should().NotContain(["console", "suite"]);
    }

    [Fact]
    public async Task Collision_ShouldNeverRenameASet()
    {
        Zip("sf2.zip", ("sf2.a", Sf2Program), ("sf2.b", Sf2Graphics));
        Directory.CreateDirectory(_destination);
        File.WriteAllText(Path.Combine(_destination, "sf2.zip"), "older set");
        var config = Config();
        config.AppendToDestination = true;
        config.CollisionMode = CollisionMode.Rename;

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        Item(result, "sf2.zip").Status.Should().Be(FileStatus.Duplicate);
        File.ReadAllText(Path.Combine(_destination, "sf2.zip")).Should().Be("older set");
        Directory.GetFiles(_destination, "sf2_*").Should().BeEmpty();

        // The set was in the destination before the sort: it is said so, and counted apart
        Item(result, "sf2.zip").IsAlreadyInDestination.Should().BeTrue();
        Item(result, "sf2.zip").StatusReason!.Key.Should().Be("reason.already_in_destination");
        result.Stats.AlreadyInDestinationCount.Should().Be(1);
    }

    [Fact]
    public async Task RenamedArchive_ShouldBeIdentifiedByItsRomsAndWrittenUnderItsShortName()
    {
        Zip("Street Fighter 2.zip", ("a.bin", Sf2Program), ("b.bin", Sf2Graphics));

        var result = await _services.Processor.ExecuteAsync(Config(), isDryRun: false);

        var item = result.Items.Should().ContainSingle().Subject;
        item.DatGameName.Should().Be("sf2");
        item.TargetFilename.Should().Be("sf2.zip");
        File.Exists(Path.Combine(_destination, "sf2.zip")).Should().BeTrue();
    }

    [Fact]
    public async Task UnknownArchivesAndLooseFiles_ShouldBeUnmatched()
    {
        Zip("unknown.zip", ("nothing.bin", Encoding.ASCII.GetBytes("NOT A MAME ROM")));
        _tmp.WriteText(Path.Combine("src", "readme.txt"), "notes");

        var result = await _services.Processor.ExecuteAsync(Config(), isDryRun: false);

        var unknown = Item(result, "unknown.zip");
        unknown.Status.Should().Be(FileStatus.Unmatched);
        unknown.StatusReason!.Key.Should().Be("reason.unmatched_not_in_dat");
        Item(result, "readme.txt").StatusReason!.Key.Should().Be("reason.mame_not_a_set");

        // Isolated as they are: the archive is not put into another archive
        File.ReadAllBytes(Path.Combine(_destination, PathOrganizerService.UnmatchedFolderName, "unknown.zip"))
            .Should().Equal(File.ReadAllBytes(Path.Combine(_source, "unknown.zip")));
    }

    [Fact]
    public async Task OneGameOneRom_ShouldGroupClonesWithTheirParentButNotGamesSharingATitle()
    {
        CreateCollection();

        var result = await _services.Processor.ExecuteAsync(Config(f =>
        {
            f.OneGameOneRom = true;
            f.PriorityCountries = ["US"];
        }));

        Item(result, "sf2u.zip").Status.Should().Be(FileStatus.Valid);
        Item(result, "sf2.zip").Status.Should().Be(FileStatus.Excluded);
        Item(result, "sf2j.zip").Status.Should().Be(FileStatus.Excluded);

        // The Tetris of Atari and the one of Sega are two games, the BIOS and Metal Slug are games of their own
        Item(result, "atetris.zip").Status.Should().Be(FileStatus.Valid);
        Item(result, "tetris.zip").Status.Should().Be(FileStatus.Valid);
        Item(result, "neogeo.zip").Status.Should().Be(FileStatus.Valid);
        Item(result, "mslug.zip").Status.Should().Be(FileStatus.Valid);
    }

    [Fact]
    public async Task OneGameOneRom_ShouldPreferACompleteSet()
    {
        CreateCollection();

        // Japan first: the only Japanese set is split, so it loses to a complete one when complete sets are required
        var result = await _services.Processor.ExecuteAsync(Config(f =>
        {
            f.OneGameOneRom = true;
            f.PriorityCountries = ["JP", "US"];
            f.NoIntroCompleteMultimediaSetsOnly = true;
        }));

        Item(result, "sf2j.zip").Status.Should().Be(FileStatus.Excluded);
        Item(result, "sf2u.zip").Status.Should().Be(FileStatus.Valid);
    }

    [Fact]
    public async Task CompleteSetsOnly_ShouldExcludeIncompleteSetsAlsoAfterARecalculation()
    {
        CreateCollection();
        var config = Config(f => f.NoIntroCompleteMultimediaSetsOnly = true);

        var result = await _services.Processor.ExecuteAsync(config);

        var split = Item(result, "sf2j.zip");
        split.Status.Should().Be(FileStatus.Excluded);
        split.StatusReason!.Key.Should().Be("reason.mame_set_needs_parent");
        _services.I18n.Format(split.StatusReason).Should().Contain("sf2");

        // The recalculation of a game evaluates the filters again: the set stays excluded
        var game = result.Items
            .Where(it => _services.FilterService.SerializeCanonicalGameKey(it.Parsed) == _services.FilterService.SerializeCanonicalGameKey(split.Parsed))
            .ToList();
        game.Should().HaveCount(3);
        _services.Processor.RecalculateGameItems(game, config, result.DatCatalog);
        split.Status.Should().Be(FileStatus.Excluded);
        split.StatusReason!.Key.Should().Be("reason.mame_set_needs_parent");
    }

    [Fact]
    public async Task Statistics_ShouldCountTheRomsOfTheSets()
    {
        CreateCollection();

        var result = await _services.Processor.ExecuteAsync(Config());

        // 12 ROMs and 1 disk in the DAT, all present: the ROM the split clone shares with its parent is in the set of
        // the parent (the clone is reported apart, since it does not run by itself)
        result.Stats.DatTotalRoms.Should().Be(13);
        result.Stats.DatMatchedCount.Should().Be(13);
        result.MissingRoms.Should().BeEmpty();
        Item(result, "sf2j.zip").MameSet!.IsSplit.Should().BeTrue();

        // A ROM of a BIOS found in a game is not the BIOS set itself
        File.Delete(Path.Combine(_source, "neogeo.zip"));
        var withoutBios = await _services.Processor.ExecuteAsync(Config());
        withoutBios.MissingRoms.Should().Contain(e => e.GameName == "neogeo");
    }

    [Fact]
    public async Task DatExport_ShouldDescribeTheKeptSetsAsTheMachinesOfTheDat()
    {
        CreateCollection();
        var result = await _services.Processor.ExecuteAsync(Config(f => f.MameExcludeBios = true));
        var path = _tmp.GetFilePath("export.xml");

        var exported = await _services.Processor.ExportDatAsync(result.Items, path, datCatalog: result.DatCatalog);

        // Each kept set is its machine, ROMs and disk included, never its archive as a single ROM
        var reread = await _services.DatService.ParseDatFileAsync(path);
        reread.Games.Select(g => g.Name).Should().BeEquivalentTo(
            result.Items.Where(it => it.Status == FileStatus.Valid).Select(it => it.DatGameName));
        reread.Entries.Should().NotContain(e => e.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        reread.GameByName["sf2u"].CloneOf.Should().Be("sf2");
        reread.GameByName["sf2u"].Roms.Select(r => r.Name).Should().Equal("sf2u.a", "sf2.b");
        reread.GameByName["mslug"].RomOf.Should().Be("neogeo");
        reread.GameByName["area51"].Roms.Should().ContainSingle(r => r.IsDisk)
            .Which.Sha1.Should().Be(Area51DiskSha1);
        exported.Games.Should().HaveCount(reread.Games.Count);
    }

    [Fact]
    public async Task MergedParentSet_ShouldMakeTheOwnRomsOfItsClonesPresent()
    {
        // A merged set: the parent archive also holds the ROMs of its clones
        Zip("sf2.zip", ("sf2.a", Sf2Program), ("sf2.b", Sf2Graphics), ("sf2u.a", Sf2UsaProgram), ("sf2j.a", Sf2JapanProgram));

        var log = new LogCollector();
        var result = await _services.Processor.ExecuteAsync(Config(), logProgress: log);

        var sf2 = Item(result, "sf2.zip");
        sf2.MameSet.Should().Be(new MameSetInfo("sf2", 0, 0, 0) { HeldClones = ["sf2j", "sf2u"] });
        sf2.MameSet!.IsMerged.Should().BeTrue();
        result.MissingRoms.Should().NotContain(e => e.Name == "sf2u.a" || e.Name == "sf2j.a");

        // The ROM the clones share with their parent is in the set of the parent, where MAME finds it
        result.MissingRoms.Should().NotContain(e => e.GameName == "sf2" || e.GameName == "sf2u" || e.GameName == "sf2j");

        // Sorted as it is, with its clones: an information, not a problem
        sf2.Status.Should().Be(FileStatus.Valid);
        sf2.StatusReason!.Key.Should().Be("reason.mame_merged_set");
        _services.I18n.Format(sf2.StatusReason).Should().Contain("2 clone");
        log.WithKey("log.mame_merged_sets").Should().ContainSingle().Which.Level.Should().Be(LogLevel.Info);
        log.WithKey("log.mame_split_sets").Should().BeEmpty();
    }

    [Fact]
    public async Task DatExport_OfAMergedSet_ShouldDescribeTheClonesItHolds()
    {
        // The archive of the parent also holds the ROMs of its clones: a ROM manager given only the parent would take
        // them for files the archive should not hold
        Zip("sf2.zip", ("sf2.a", Sf2Program), ("sf2.b", Sf2Graphics), ("sf2u.a", Sf2UsaProgram), ("sf2j.a", Sf2JapanProgram));
        var result = await _services.Processor.ExecuteAsync(Config());
        var path = _tmp.GetFilePath("export.xml");

        var exported = await _services.Processor.ExportDatAsync(result.Items, path, datCatalog: result.DatCatalog);

        var reread = await _services.DatService.ParseDatFileAsync(path);
        reread.Games.Select(g => g.Name).Should().Equal("sf2", "sf2j", "sf2u");
        reread.GameByName["sf2u"].CloneOf.Should().Be("sf2");
        reread.GameByName["sf2u"].Roms.Select(r => r.Name).Should().Equal("sf2u.a", "sf2.b");
        exported.CountFiles().Should().Be(6);
    }

    [Fact]
    public async Task SetsSharingTheirBiosRoms_ShouldBeCompleteWhenTheBiosIsInTheSource()
    {
        // Merged or split collections: the ROMs of the BIOS are only in its own set
        Zip("neogeo.zip", ("bios.rom", Bios));
        Zip("mslug.zip", ("mslug.p1", MetalSlug));
        var log = new LogCollector();

        var result = await _services.Processor.ExecuteAsync(Config(f => f.NoIntroCompleteMultimediaSetsOnly = true), logProgress: log);

        var mslug = Item(result, "mslug.zip");
        mslug.MameSet.Should().Be(new MameSetInfo("mslug", 0, 0, 0) { BiosSet = "neogeo", RequiredSets = ["neogeo"] });
        mslug.Status.Should().Be(FileStatus.Valid, "MAME finds the ROMs of the BIOS in its set, sorted too");
        mslug.StatusReason.Should().BeNull();
        result.MissingRoms.Should().NotContain(e => e.GameName == "mslug" || e.GameName == "neogeo");
        log.Messages.Should().NotContain(m => m.Level == LogLevel.Warn);
    }

    [Fact]
    public async Task SetsSharingTheDiskOfTheirBios_ShouldBeCompleteWhenTheBiosIsInTheSource()
    {
        // As vf5 with lindbios in MAME: the game shares a ROM and a disk with its BIOS, both in the sets of the BIOS
        var biosRom = Encoding.ASCII.GetBytes("LINDBERGH BIOS");
        var gameRom = Encoding.ASCII.GetBytes("VIRTUA FIGHTER 5");
        var biosDisk = ChdTestFile.Sha1Of("lindyellow");
        File.WriteAllText(_datPath, BuildDat().Replace(
            "</mame>",
            $"""
            <machine name="lindbios" isbios="yes"><description>Sega Lindbergh BIOS</description>{Rom("bios.bin", biosRom)}<disk name="lindyellow" sha1="{biosDisk}"/></machine>
            <machine name="vf5" romof="lindbios"><description>Virtua Fighter 5</description>{Rom("bios.bin", biosRom, merge: "bios.bin")}{Rom("vf5.bin", gameRom)}<disk name="lindyellow" merge="lindyellow" sha1="{biosDisk}"/></machine>
            </mame>
            """,
            StringComparison.Ordinal));
        Zip("lindbios.zip", ("bios.bin", biosRom));
        Zip("vf5.zip", ("vf5.bin", gameRom));
        _tmp.WriteBytes(Path.Combine("src", "lindbios", "lindyellow.chd"), ChdTestFile.Create("lindyellow"));
        var log = new LogCollector();

        var result = await _services.Processor.ExecuteAsync(Config(f => f.NoIntroCompleteMultimediaSetsOnly = true), logProgress: log);

        var vf5 = Item(result, "vf5.zip");
        vf5.MameSet.Should().Be(new MameSetInfo("vf5", 0, 0, 0) { BiosSet = "lindbios", RequiredSets = ["lindbios"] });
        vf5.Status.Should().Be(FileStatus.Valid);
        result.MissingRoms.Should().NotContain(e => e.GameName == "vf5" || e.GameName == "lindbios");
        log.WithKey("log.mame_missing_bios").Should().BeEmpty();
    }

    [Fact]
    public async Task SetsSharingTheirBiosRoms_ShouldBeReportedWhenTheBiosIsMissing()
    {
        Zip("mslug.zip", ("mslug.p1", MetalSlug));
        var log = new LogCollector();

        var result = await _services.Processor.ExecuteAsync(Config(), logProgress: log);

        var mslug = Item(result, "mslug.zip");
        mslug.MameSet.Should().Be(new MameSetInfo("mslug", 1, 0, 0, RequiredSet: "neogeo") { RequiresBios = true });
        mslug.MameSet!.IsSplit.Should().BeFalse("a game relying on its BIOS is not a split clone");
        mslug.Status.Should().Be(FileStatus.Valid);
        mslug.StatusReason!.Key.Should().Be("reason.mame_set_needs_bios");
        log.WithKey("log.mame_missing_bios").Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warn);
        log.WithKey("log.mame_split_sets").Should().BeEmpty();

        // Excluded when only complete sets are wanted
        var completeOnly = await _services.Processor.ExecuteAsync(Config(f => f.NoIntroCompleteMultimediaSetsOnly = true));
        Item(completeOnly, "mslug.zip").Status.Should().Be(FileStatus.Excluded);
        _services.I18n.Format(Item(completeOnly, "mslug.zip").StatusReason).Should().Contain("neogeo");
    }

    [Fact]
    public async Task MergedCollection_ShouldSortTheDiskOfACloneWithItsParent()
    {
        // A clone with a disk of its own, whose ROMs are in the archive of its parent
        var cloneRom = Encoding.ASCII.GetBytes("AREA 51 TIME WARNER");
        var cloneDisk = ChdTestFile.Sha1Of("area51t");
        File.WriteAllText(_datPath, BuildDat().Replace(
            "</mame>",
            $"""<machine name="area51t" cloneof="area51" romof="area51"><description>Area 51 (Time Warner license)</description>{Rom("area51t.rom", cloneRom)}<disk name="area51t" sha1="{cloneDisk}"/></machine></mame>""",
            StringComparison.Ordinal));
        Zip("area51.zip", ("area51.rom", Area51), ("area51t.rom", cloneRom));
        _tmp.WriteBytes(Path.Combine("src", "area51", "area51.chd"), ChdTestFile.Create("area51"));
        _tmp.WriteBytes(Path.Combine("src", "area51t", "area51t.chd"), ChdTestFile.Create("area51t"));

        var result = await _services.Processor.ExecuteAsync(Config(), isDryRun: false);

        var area51 = result.Items.Should().ContainSingle().Subject;
        area51.MameSet.Should().Be(new MameSetInfo("area51", 0, 0, 0) { HeldClones = ["area51t"] });
        area51.RelatedItems.Select(r => r.TargetFilename).Should().BeEquivalentTo(
            Path.Combine("area51", "area51.chd"), Path.Combine("area51t", "area51t.chd"));
        File.Exists(Path.Combine(_destination, "area51t", "area51t.chd")).Should().BeTrue("MAME looks for a disk in the folder of its machine");
        result.MissingRoms.Should().NotContain(e => e.GameName == "area51" || e.GameName == "area51t");
    }

    [Fact]
    public async Task SplitSets_ShouldBeReportedNeverRebuilt()
    {
        CreateCollection();
        var log = new LogCollector();

        var result = await _services.Processor.ExecuteAsync(Config(), isDryRun: false, logProgress: log);

        var split = Item(result, "sf2j.zip");
        split.Status.Should().Be(FileStatus.Valid);
        split.StatusReason!.Key.Should().Be("reason.mame_split_set");
        _services.I18n.Format(split.StatusReason).Should().Contain("sf2").And.Contain("RomVault");
        var report = log.WithKey("log.mame_split_sets").Should().ContainSingle().Subject;
        report.Level.Should().Be(LogLevel.Warn);
        _services.I18n.Format(report.Message).Should().StartWith("Sets split détectés : 1 (sf2j).");

        // The archive is copied as it is: nothing is added from the set of its parent
        File.ReadAllBytes(Path.Combine(_destination, "sf2j.zip")).Should().Equal(File.ReadAllBytes(Path.Combine(_source, "sf2j.zip")));

        // A complete non-merged set says nothing
        Item(result, "sf2u.zip").StatusReason.Should().BeNull();
    }

    [Fact]
    public async Task OneGameOneRom_ShouldNeverKeepASplitCloneInPlaceOfItsParent()
    {
        // Japan first: the only Japanese set is split, so it would not run alone; the complete parent wins at once
        File.WriteAllText(_datPath, BuildDat());
        Zip("sf2.zip", ("sf2.a", Sf2Program), ("sf2.b", Sf2Graphics));
        Zip("sf2j.zip", ("sf2j.a", Sf2JapanProgram));

        var result = await _services.Processor.ExecuteAsync(Config(f =>
        {
            f.OneGameOneRom = true;
            f.PriorityCountries = ["JP", "WO"];
        }));

        Item(result, "sf2.zip").Is1G1RWinner.Should().BeTrue();
        var split = Item(result, "sf2j.zip");
        split.Status.Should().Be(FileStatus.Excluded);
        _services.I18n.Format(split.EliminationDetail).Should().Contain("sf2");
    }

    private static readonly byte[] QSound = Encoding.ASCII.GetBytes("QSOUND DSP");
    private static readonly byte[] AlphaProgram = Encoding.ASCII.GetBytes("STREET FIGHTER ALPHA PROGRAM");

    // A game whose sound device has a ROM of its own (as the CPS2 games and the qsound device in MAME)
    private void WriteDatWithDevice() =>
        File.WriteAllText(_datPath, BuildDat().Replace(
            "</mame>",
            $"""
            <machine name="qsound" isdevice="yes" runnable="no"><description>Q-Sound</description>{Rom("dl-1425.bin", QSound)}</machine>
            <machine name="sfa"><description>Street Fighter Alpha: Warriors' Dreams (Euro 950727)</description><year>1995</year><manufacturer>Capcom</manufacturer>{Rom("sfa.p1", AlphaProgram)}<device_ref name="qsound"/><driver status="good"/></machine>
            </mame>
            """,
            StringComparison.Ordinal));

    [Fact]
    public async Task BiosExcludedByTheFilters_ShouldBeSortedWithTheGamesThatNeedIt()
    {
        // Merged or split collection: the game takes the ROMs of its BIOS from the set of the BIOS
        Zip("neogeo.zip", ("bios.rom", Bios));
        Zip("mslug.zip", ("mslug.p1", MetalSlug));
        var log = new LogCollector();

        var result = await _services.Processor.ExecuteAsync(Config(f => f.MameExcludeBios = true), isDryRun: false, logProgress: log);

        var neogeo = Item(result, "neogeo.zip");
        neogeo.Status.Should().Be(FileStatus.Valid, "MAME does not run the game without its BIOS");
        neogeo.StatusReason!.Key.Should().Be("reason.mame_required_set");
        _services.I18n.Format(neogeo.StatusReason).Should().Contain("mslug");
        File.Exists(Path.Combine(_destination, "neogeo.zip")).Should().BeTrue();
        File.Exists(Path.Combine(_destination, "mslug.zip")).Should().BeTrue();
        log.WithKey("log.mame_required_sets").Should().ContainSingle().Which.Level.Should().Be(LogLevel.Info);
    }

    [Fact]
    public async Task BiosExcludedByTheFilters_ShouldStayExcludedWhenTheGamesHoldItsRoms()
    {
        // Non-merged collection: the game holds the ROMs of its BIOS, which nothing needs
        Zip("neogeo.zip", ("bios.rom", Bios));
        Zip("mslug.zip", ("bios.rom", Bios), ("mslug.p1", MetalSlug));

        var result = await _services.Processor.ExecuteAsync(Config(f => f.MameExcludeBios = true));

        Item(result, "mslug.zip").MameSet!.RequiredSets.Should().BeEmpty();
        Item(result, "neogeo.zip").Status.Should().Be(FileStatus.Excluded);
        Item(result, "neogeo.zip").StatusReason!.Key.Should().Be("reason.mame_bios_excluded");
    }

    [Fact]
    public async Task BiosNeededByAnExcludedGame_ShouldStayExcluded()
    {
        // The game lacks its program ROM: excluded as an incomplete set, it needs no BIOS
        Zip("neogeo.zip", ("bios.rom", Bios));
        Zip("mslug.zip", ("other.p1", AtariTetris));

        var result = await _services.Processor.ExecuteAsync(Config(f =>
        {
            f.MameExcludeBios = true;
            f.NoIntroCompleteMultimediaSetsOnly = true;
        }));

        Item(result, "mslug.zip").MameSet!.RequiredSets.Should().Equal("neogeo");
        Item(result, "mslug.zip").Status.Should().Be(FileStatus.Excluded);
        Item(result, "neogeo.zip").Status.Should().Be(FileStatus.Excluded);
        Item(result, "neogeo.zip").StatusReason!.Key.Should().Be("reason.mame_bios_excluded");
    }

    [Fact]
    public async Task DeviceExcludedByTheFilters_ShouldBeSortedWithTheGamesThatNeedIt()
    {
        WriteDatWithDevice();
        Zip("qsound.zip", ("dl-1425.bin", QSound));
        Zip("sfa.zip", ("sfa.p1", AlphaProgram));

        var result = await _services.Processor.ExecuteAsync(Config(f => f.MameExcludeDevices = true));

        var sfa = Item(result, "sfa.zip");
        sfa.MameSet.Should().Be(new MameSetInfo("sfa", 0, 0, 0) { RequiredSets = ["qsound"] });
        sfa.Status.Should().Be(FileStatus.Valid);
        sfa.StatusReason.Should().BeNull();
        var qsound = Item(result, "qsound.zip");
        qsound.Status.Should().Be(FileStatus.Valid);
        qsound.StatusReason!.Key.Should().Be("reason.mame_required_set");
    }

    [Fact]
    public async Task DeviceHeldByTheSetOfTheGame_ShouldNotBeNeeded()
    {
        // Fully non-merged set: the game holds the ROMs of its devices
        WriteDatWithDevice();
        Zip("qsound.zip", ("dl-1425.bin", QSound));
        Zip("sfa.zip", ("sfa.p1", AlphaProgram), ("dl-1425.bin", QSound));

        var result = await _services.Processor.ExecuteAsync(Config(f => f.MameExcludeDevices = true));

        Item(result, "sfa.zip").MameSet.Should().Be(new MameSetInfo("sfa", 0, 0, 1));
        Item(result, "qsound.zip").Status.Should().Be(FileStatus.Excluded);
    }

    [Fact]
    public async Task DeviceMergedIntoAnotherDevice_ShouldRequireTheSetHoldingItsRoms()
    {
        // As in MAME 0.289: the CPS2 games use qsound_hle, whose only ROM is merged into qsound. A merged collection has
        // no qsound_hle set, and MAME finds the ROM in the set of qsound
        File.WriteAllText(_datPath, BuildDat().Replace(
            "</mame>",
            $"""
            <machine name="qsound" isdevice="yes" runnable="no"><description>QSound</description>{Rom("dl-1425.bin", QSound)}</machine>
            <machine name="qsound_hle" isdevice="yes" runnable="no" romof="qsound"><description>QSound (HLE)</description>{Rom("dl-1425.bin", QSound, merge: "dl-1425.bin")}</machine>
            <machine name="19xx"><description>19XX: The War Against Destiny (Europe 960104)</description><year>1996</year><manufacturer>Capcom</manufacturer>{Rom("19xx.p1", AlphaProgram)}<device_ref tag=":qsound" name="qsound_hle"/><driver status="good"/></machine>
            </mame>
            """,
            StringComparison.Ordinal));
        Zip("qsound.zip", ("dl-1425.bin", QSound));
        Zip("19xx.zip", ("19xx.p1", AlphaProgram));

        var result = await _services.Processor.ExecuteAsync(Config(f =>
        {
            f.MameExcludeDevices = true;
            f.NoIntroCompleteMultimediaSetsOnly = true;
        }));

        var game = Item(result, "19xx.zip");
        game.MameSet.Should().Be(new MameSetInfo("19xx", 0, 0, 0) { RequiredSets = ["qsound"] });
        game.MameSet!.IsComplete.Should().BeTrue();
        game.Status.Should().Be(FileStatus.Valid);
        var qsound = Item(result, "qsound.zip");
        qsound.Status.Should().Be(FileStatus.Valid);
        qsound.StatusReason!.Key.Should().Be("reason.mame_required_set");
        result.MissingRoms.Should().NotContain(e => e.GameName == "qsound_hle");

        // Without the set of qsound, the device is missing under its own name
        File.Delete(Path.Combine(_source, "qsound.zip"));
        var withoutQSound = await _services.Processor.ExecuteAsync(Config());
        Item(withoutQSound, "19xx.zip").MameSet!.MissingDevices.Should().Equal("qsound_hle");
    }

    [Fact]
    public async Task DeviceAbsentFromTheSource_ShouldMakeTheSetIncomplete()
    {
        WriteDatWithDevice();
        Zip("sfa.zip", ("sfa.p1", AlphaProgram));
        var log = new LogCollector();

        var result = await _services.Processor.ExecuteAsync(Config(), logProgress: log);

        var sfa = Item(result, "sfa.zip");
        sfa.MameSet.Should().Be(new MameSetInfo("sfa", 0, 0, 0) { MissingDevices = ["qsound"] });
        sfa.MameSet!.IsComplete.Should().BeFalse();
        sfa.Status.Should().Be(FileStatus.Valid);
        sfa.StatusReason!.Key.Should().Be("reason.mame_set_needs_devices");
        _services.I18n.Format(sfa.StatusReason).Should().Contain("qsound");
        var warning = log.WithKey("log.mame_missing_devices").Should().ContainSingle().Subject;
        warning.Level.Should().Be(LogLevel.Warn);
        _services.I18n.Format(warning.Message).Should().Contain("sfa").And.Contain("qsound");

        // Excluded when only complete sets are wanted
        var completeOnly = await _services.Processor.ExecuteAsync(Config(f => f.NoIntroCompleteMultimediaSetsOnly = true));
        Item(completeOnly, "sfa.zip").Status.Should().Be(FileStatus.Excluded);
        Item(completeOnly, "sfa.zip").StatusReason!.Key.Should().Be("reason.mame_set_needs_devices");
    }

    [Fact]
    public async Task RecalculationOfARequiredSet_ShouldKeepIt()
    {
        Zip("neogeo.zip", ("bios.rom", Bios));
        Zip("mslug.zip", ("mslug.p1", MetalSlug));
        var config = Config(f => f.MameExcludeBios = true);
        var result = await _services.Processor.ExecuteAsync(config);
        var neogeo = Item(result, "neogeo.zip");
        neogeo.Status.Should().Be(FileStatus.Valid);

        // Recalculated with the whole run, the BIOS stays kept, as in the simulation
        _services.Processor.RecalculateGameItems([neogeo], config, result.DatCatalog, result.Items);

        neogeo.Status.Should().Be(FileStatus.Valid);
        neogeo.StatusReason!.Key.Should().Be("reason.mame_required_set");
        neogeo.ReleaseStatus.Should().Be(FileStatus.Valid);
    }

    [Fact]
    public async Task PartialSort_ShouldTransferTheSetsTheChosenGamesNeed()
    {
        WriteDatWithDevice();
        Zip("neogeo.zip", ("bios.rom", Bios));
        Zip("mslug.zip", ("mslug.p1", MetalSlug));
        Zip("qsound.zip", ("dl-1425.bin", QSound));
        Zip("sfa.zip", ("sfa.p1", AlphaProgram));
        Zip("atetris.zip", ("atetris.rom", AtariTetris));
        var config = Config();
        var simulation = await _services.Processor.ExecuteAsync(config);

        // Only the games are chosen, not their BIOS nor their device
        var chosen = new[] { Item(simulation, "mslug.zip"), Item(simulation, "sfa.zip") };
        var selection = SelectionFilter.Create(simulation.Items, chosen, _services.FilterService);
        selection.Units.Where(u => u.IsRequiredSet).Select(u => u.Label).Should().BeEquivalentTo("Neo-Geo MV-6F", "Q-Sound");
        SelectionFilter.GetWrittenItems(simulation.Items, selection, _services.FilterService)
            .Select(it => Path.GetFileName(it.SourcePath))
            .Should().BeEquivalentTo("mslug.zip", "neogeo.zip", "sfa.zip", "qsound.zip");

        var log = new LogCollector();
        await _services.Processor.ExecuteAsync(config, isDryRun: false, logProgress: log, selection: selection);

        Directory.EnumerateFiles(_destination)
            .Where(p => !DestinationGuard.IsGuardPath(p))
            .Select(Path.GetFileName)
            .Should().BeEquivalentTo("mslug.zip", "neogeo.zip", "sfa.zip", "qsound.zip");
        log.WithKey("log.selection_required_sets").Should().ContainSingle();
    }

    /// <summary>Synchronous progress, so that the messages are all there when the run returns.</summary>
    private sealed class LogCollector : IProgress<LogMessage>
    {
        public List<LogMessage> Messages { get; } = [];

        public void Report(LogMessage value) => Messages.Add(value);

        public IEnumerable<LogMessage> WithKey(string key) => Messages.Where(m => m.Message.Key == key);
    }

    [Fact]
    public async Task CategoryFile_ShouldGiveEachSetItsCategoryForTheFiltersAndThePaths()
    {
        CreateCollection();
        var categoryFile = _tmp.WriteText("catlist.ini", """
            [FOLDER_SETTINGS]
            RootFolderIcon mame
            [ROOT_FOLDER]
            [Arcade: Fighter / Versus]
            sf2
            [Arcade: Puzzle / Drop]
            atetris
            tetris
            """);
        var config = Config(f => f.MameExcludedGenres = ["Puzzle"]);
        config.MameCategoryFile = categoryFile;
        config.Pattern = "{genre}";
        var log = new LogCollector();

        var result = await _services.Processor.ExecuteAsync(config, logProgress: log);

        log.WithKey("log.mame_categories_loaded").Should().ContainSingle();
        Item(result, "atetris.zip").Status.Should().Be(FileStatus.Excluded);
        Item(result, "atetris.zip").StatusReason!.Key.Should().Be("reason.mame_genre_excluded");

        // A clone the file does not list takes the category of its parent; a machine it does not list has none
        var clone = Item(result, "sf2u.zip");
        clone.Parsed.Machine!.Category!.Name.Should().Be("Fighter / Versus");
        clone.TargetPath.Should().Be(Path.Combine(_destination, "Fighter", "sf2u.zip"));
        Item(result, "mslug.zip").Parsed.Machine!.Category.Should().BeNull();
        Item(result, "mslug.zip").TargetPath.Should().Be(Path.Combine(_destination, "Unknown", "mslug.zip"));

        // A file that cannot be read is reported, and the sort goes on without categories
        config.MameCategoryFile = Path.Combine(_tmp.Path, "missing.ini");
        var withoutFile = await _services.Processor.ExecuteAsync(config, logProgress: log);
        log.WithKey("log.mame_categories_unreadable").Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warn);
        Item(withoutFile, "atetris.zip").Status.Should().Be(FileStatus.Valid);
    }

    [Fact]
    public async Task SizeBudget_ShouldKeepTheBestRatedSetsWithTheSetsTheyNeed()
    {
        CreateCollection();
        var ratingFile = _tmp.WriteText("bestgames.ini", """
            [FOLDER_SETTINGS]
            RootFolderIcon mame
            [ROOT_FOLDER]
            [90 to 100 (Best Games)]
            mslug
            [80 to 90 (Very Good)]
            atetris
            """);

        // Merged: the game takes the ROM of its BIOS from the set of the BIOS
        File.Delete(Path.Combine(_source, "mslug.zip"));
        Zip("mslug.zip", ("mslug.p1", MetalSlug));
        long SizeOf(string name) => new FileInfo(Path.Combine(_source, name)).Length;

        // Room for mslug with its BIOS, then atetris: nothing else fits
        var config = Config(f =>
        {
            f.MameExcludeBios = true;
            f.MameSizeBudgetBytes = SizeOf("mslug.zip") + SizeOf("neogeo.zip") + SizeOf("atetris.zip");
        });
        config.MameRatingFile = ratingFile;
        var log = new LogCollector();

        var result = await _services.Processor.ExecuteAsync(config, logProgress: log);

        log.WithKey("log.mame_ratings_loaded").Should().ContainSingle();
        log.WithKey("log.mame_budget").Should().ContainSingle();
        Item(result, "mslug.zip").Parsed.Machine!.Rating!.Score.Should().Be(90);
        Item(result, "sf2.zip").Parsed.Machine!.Rating!.IsUnrated.Should().BeTrue();
        result.Items.Where(it => it.Status == FileStatus.Valid).Select(it => Path.GetFileName(it.SourcePath))
            .Should().BeEquivalentTo("mslug.zip", "neogeo.zip", "atetris.zip");
        Item(result, "neogeo.zip").StatusReason!.Key.Should().Be("reason.mame_required_set", "the BIOS filter excludes it, the game needs it");
        Item(result, "sf2.zip").StatusReason!.Key.Should().Be("reason.mame_budget_exceeded");
        Item(result, "area51.zip").ReleaseStatus.Should().Be(FileStatus.Excluded);

        // A game recalculated with the whole run still has to fit
        var tetris = Item(result, "tetris.zip");
        _services.Processor.RecalculateGameItems([tetris], config, result.DatCatalog, result.Items);
        tetris.Status.Should().Be(FileStatus.Excluded);
        tetris.StatusReason!.Key.Should().Be("reason.mame_budget_exceeded");
        Item(result, "atetris.zip").Status.Should().Be(FileStatus.Valid);
    }

    [Fact]
    public async Task ArchivesNotInspectedByTheScan_ShouldStillBeIdentified()
    {
        CreateCollection();
        var config = Config();
        config.Filters.ScanArchives = false;

        var result = await _services.Processor.ExecuteAsync(config);

        Item(result, "sf2.zip").MameSet.Should().Be(new MameSetInfo("sf2", 0, 0, 0));
        result.Items.Should().HaveCount(8);
    }

    [Fact]
    public async Task MameConventionWithoutDat_ShouldBeRefused()
    {
        CreateCollection();
        var config = Config();
        config.DatFile = null;
        config.NamingConvention = NamingConvention.Mame;

        var act = () => _services.Processor.ExecuteAsync(config);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be(_services.I18n.Tr("log.mame_requires_dat"));
    }

    [Fact]
    public async Task DatWithoutParentCloneRelations_ShouldGroupTheSetsByTitle()
    {
        // SabreTools DATs: no cloneof, the variants of a game share its title
        File.WriteAllText(_datPath, BuildDat()
            .Replace(" cloneof=\"sf2\" romof=\"sf2\"", string.Empty, StringComparison.Ordinal)
            .Replace("<mame build=\"0.289 (test)\">", "<datafile><header><name>MAME ROMs (non-merged)</name></header>", StringComparison.Ordinal)
            .Replace("</mame>", "</datafile>", StringComparison.Ordinal));
        CreateCollection();

        var result = await _services.Processor.ExecuteAsync(Config(f =>
        {
            f.OneGameOneRom = true;
            f.PriorityCountries = ["US"];
        }));

        result.DatCatalog!.Header.Convention.Should().Be(NamingConvention.Mame);
        Item(result, "sf2u.zip").Status.Should().Be(FileStatus.Valid);
        Item(result, "sf2.zip").Status.Should().Be(FileStatus.Excluded);

        // Without parent / clone relations, the shared ROM is just missing
        Item(result, "sf2j.zip").MameSet!.RequiredSet.Should().BeNull();
    }

    [Fact]
    public async Task OneGameOneRom_ShouldFollowTheOrderOfTheMameSets()
    {
        CreateCollection();

        // English first, then the United States: the general order would keep the USA clone, the order of the MAME
        // sets keeps the parent chosen by the MAME developers (World, English as well)
        void Priorities(FilterConfig f)
        {
            f.OneGameOneRom = true;
            f.PriorityLanguages = ["en"];
            f.PriorityCountries = ["US"];
        }

        var result = await _services.Processor.ExecuteAsync(Config(Priorities));

        Item(result, "sf2.zip").Status.Should().Be(FileStatus.Valid);
        var clone = Item(result, "sf2u.zip");
        clone.Status.Should().Be(FileStatus.Excluded);
        _services.I18n.Format(clone.StatusReason!).Should().Contain(_services.I18n.Tr("reason.1g1r_reason_parent"), "the parent decides");

        // The country above the parent prefers the versions of the region; the orders of other conventions have no effect on MAME sets
        result = await _services.Processor.ExecuteAsync(Config(f =>
        {
            Priorities(f);
            var countryFirst = TiebreakOrder.Default(NamingConvention.Mame).Where(c => c != TiebreakCriterion.CountryRank).ToList();
            countryFirst.Insert(countryFirst.IndexOf(TiebreakCriterion.ParentClone), TiebreakCriterion.CountryRank);
            f.MameTiebreakOrder = countryFirst;
            f.TosecTiebreakOrder = [.. TiebreakOrder.Default(NamingConvention.Tosec, RegionPriorityMode.CountryFirst)];
            f.NoIntroTiebreakOrder = [.. TiebreakOrder.Default(NamingConvention.NoIntro, RegionPriorityMode.CountryFirst)];
        }));

        Item(result, "sf2u.zip").Status.Should().Be(FileStatus.Valid);
        Item(result, "sf2.zip").Status.Should().Be(FileStatus.Excluded);
    }
}
