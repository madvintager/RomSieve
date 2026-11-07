using System.IO.Hashing;
using System.Text;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// A lot of DATs (a folder of No-Intro, Redump or TOSEC DATs): each DAT is read into its own catalog, named after its
/// header, in the order of the paths, with the subfolders on demand; the lot only indexes the sizes and checksums of its
/// DATs, to tell which DATs know a file. Refused: a folder without DAT, a DAT of MAME machines, MAME software lists mixed
/// with other DATs, two DATs of the same name, every DAT left out. The DATs left out are only read for their header.
/// </summary>
public sealed class DatLotTests : IDisposable
{
    private const string GameBoy = "Nintendo - Game Boy";
    private const string Nes = "Nintendo - Nintendo Entertainment System";
    private const string Amiga = "Commodore Amiga - Games - [ADF]";

    private static readonly byte[] TetrisGameBoy = Encoding.ASCII.GetBytes("TETRIS GAME BOY");
    private static readonly byte[] TetrisNes = Encoding.ASCII.GetBytes("TETRIS NES");
    private static readonly byte[] SharedBios = Encoding.ASCII.GetBytes("SHARED BIOS");
    private static readonly byte[] Lemmings = Encoding.ASCII.GetBytes("LEMMINGS DISK");

    private readonly TempDirectoryFixture _tmp = new();
    private readonly CoreTestServices _services = new();
    private readonly string _dats;

    public DatLotTests() => _dats = _tmp.CreateSubdirectory("dats");

    public void Dispose() => _tmp.Dispose();

    private static string Crc(byte[] data) => Crc32.HashToUInt32(data).ToString("x8");

    private static string Rom(string name, byte[] data, string? sha1 = null) =>
        $"""<rom name="{name}" size="{data.Length}" crc="{Crc(data)}"{(sha1 is null ? string.Empty : $" sha1=\"{sha1}\"")}/>""";

    // A Logiqx DAT: its header (name, homepage), then its games (name, ROMs)
    private string WriteDat(string relativePath, string? name, string homepage, params (string Game, string Roms)[] games) =>
        _tmp.WriteText(Path.Combine("dats", relativePath), $"""
            <?xml version="1.0"?>
            <datafile>
            	<header>{(name is null ? string.Empty : $"<name>{name}</name>")}<description>{name ?? "No name"}</description><homepage>{homepage}</homepage></header>
            {string.Concat(games.Select(g => $"""
            	<game name="{g.Game}"><description>{g.Game}</description>{g.Roms}</game>

            """))}
            </datafile>
            """);

    private void WriteGameBoy(string relativePath = "Nintendo - Game Boy (20230414-173400).dat") =>
        WriteDat(relativePath, GameBoy, "No-Intro",
            ("Tetris (World) (Rev 1)", Rom("Tetris (World) (Rev 1).gb", TetrisGameBoy)),
            ("[BIOS] Shared (World)", Rom("[BIOS] Shared (World).bin", SharedBios)));

    private void WriteNes(string relativePath = "Nintendo - Nintendo Entertainment System (20230501-101010).dat") =>
        WriteDat(relativePath, Nes, "No-Intro",
            ("Tetris (USA)", Rom("Tetris (USA).nes", TetrisNes)),
            ("[BIOS] Shared (World)", Rom("[BIOS] Shared (World).bin", SharedBios)));

    private void WriteAmiga(string relativePath = "Commodore Amiga - Games - [ADF] (TOSEC-v2025-01-30_CM).dat") =>
        WriteDat(relativePath, Amiga, "TOSEC",
            ("Lemmings (1991)(Psygnosis)", Rom("Lemmings (1991)(Psygnosis).adf", Lemmings, sha1: "0123456789abcdef0123456789abcdef01234567")));

    private Task<DatCatalog> ReadLot(bool recursive = false, params string[] excluded) =>
        _services.DatService.ParseDatFileAsync(_dats, new DatFolderOptions(recursive, excluded));

    private async Task<DatFolderException> Refusal(Func<Task> read) =>
        (await FluentActions.Invoking(read).Should().ThrowAsync<DatFolderException>()).Which;

    // =========================================================================
    // Reading
    // =========================================================================

    [Fact]
    public async Task Lot_ShouldKeepACatalogPerDatNamedAfterItsHeaderInTheOrderOfThePaths()
    {
        WriteNes();
        WriteGameBoy();
        WriteAmiga();

        var lot = await ReadLot();

        lot.Dats!.Select(d => d.Name).Should().Equal(Amiga, GameBoy, Nes);
        lot.Dats!.Select(d => d.RelativePath).Should().Equal(
            "Commodore Amiga - Games - [ADF] (TOSEC-v2025-01-30_CM).dat",
            "Nintendo - Game Boy (20230414-173400).dat",
            "Nintendo - Nintendo Entertainment System (20230501-101010).dat");
        lot.Dats!.Should().OnlyContain(d => d.RelativeFolder.Length == 0);
        lot.Dats!.Select(d => d.Catalog.Header.Convention).Should().Equal(NamingConvention.Tosec, NamingConvention.NoIntro, NamingConvention.NoIntro);

        // Each DAT is the catalog of the DAT read alone: the same game name in two DATs never meets
        lot.DatFor(GameBoy)!.GameByName["[BIOS] Shared (World)"].Should().NotBeSameAs(lot.DatFor(Nes)!.GameByName["[BIOS] Shared (World)"]);
        lot.DatFor(GameBoy)!.Games.Should().HaveCount(2);
        lot.DatFor(Nes)!.Header.SourceFile.Should().Be(Path.Combine(_dats, "Nintendo - Nintendo Entertainment System (20230501-101010).dat"));
    }

    [Fact]
    public async Task Lot_ShouldNotGatherTheGamesOfItsDats()
    {
        WriteNes();
        WriteGameBoy();

        var lot = await ReadLot();

        lot.Header.Name.Should().Be("dats");
        lot.Games.Should().BeEmpty();
        lot.Entries.Should().BeEmpty();
        lot.GameByName.Should().BeEmpty();
        lot.SoftwareLists.Should().BeNull();
        lot.IsSoftwareListCatalog.Should().BeFalse();
        lot.Header.Convention.Should().Be(NamingConvention.Auto);
        lot.DetectConvention().Should().Be(NamingConvention.Auto);
        lot.ConventionAnalysisCount.Should().Be(0, "a lot has no convention of its own: each DAT has its own");
        lot.KnownSizes.Should().BeEquivalentTo(new long[] { TetrisGameBoy.Length, TetrisNes.Length, SharedBios.Length });
    }

    [Fact]
    public async Task DatFor_ShouldIgnoreTheCaseAndFindNoDatOutsideTheLot()
    {
        WriteNes();
        WriteGameBoy();

        var lot = await ReadLot();

        lot.DatFor(GameBoy.ToUpperInvariant()).Should().BeSameAs(lot.Dats![0].Catalog);
        lot.DatFor("Sega - Mega Drive").Should().BeNull();
        lot.DatFor(null).Should().BeNull();

        var single = await _services.DatService.ParseDatFileAsync(Path.Combine(_dats, "Nintendo - Game Boy (20230414-173400).dat"));
        single.Dats.Should().BeNull();
        single.DatFor(Nes).Should().BeSameAs(single, "a catalog of a single DAT is its own DAT");
    }

    [Fact]
    public async Task DatWithoutName_ShouldBeNamedAfterItsFile()
    {
        WriteGameBoy();
        WriteDat("Sega - Mega Drive (20240101).dat", name: null, "No-Intro", ("Sonic (World)", Rom("Sonic (World).md", TetrisNes)));

        var lot = await ReadLot();

        lot.Dats!.Select(d => d.Name).Should().Equal(GameBoy, "Sega - Mega Drive (20240101)");
        lot.DatFor("sega - mega drive (20240101)")!.Header.Name.Should().Be("Sega - Mega Drive (20240101)");
    }

    [Fact]
    public async Task Subfolders_ShouldOnlyBeReadWhenAskedAndKeepTheirPaths()
    {
        WriteAmiga(Path.Combine("TOSEC", "Commodore Amiga - Games - [ADF].dat"));
        WriteGameBoy(Path.Combine("No-Intro", "Nintendo", "Nintendo - Game Boy.dat"));
        WriteNes("Nintendo - NES.dat");
        WriteDat(Path.Combine("$RECYCLE.BIN", "Trashed.dat"), "Trashed", "No-Intro", ("Old (World)", Rom("Old (World).bin", Lemmings)));

        var flat = await ReadLot(recursive: false);
        flat.Dats!.Select(d => d.Name).Should().Equal(Nes);

        var lot = await ReadLot(recursive: true);
        lot.Dats!.Select(d => d.Name).Should().Equal(Nes, GameBoy, Amiga);
        lot.Dats!.Select(d => d.RelativePath).Should().Equal(
            "Nintendo - NES.dat",
            Path.Combine("No-Intro", "Nintendo", "Nintendo - Game Boy.dat"),
            Path.Combine("TOSEC", "Commodore Amiga - Games - [ADF].dat"));
        lot.Dats!.Select(d => d.RelativeFolder).Should().Equal(string.Empty, Path.Combine("No-Intro", "Nintendo"), "TOSEC");
    }

    /// <summary>The order of the paths is the same on every system: the separator of a subfolder counts as <c>/</c>.</summary>
    [Fact]
    public void FolderFiles_ShouldComeInTheOrderOfTheirPathsWhateverTheSeparator()
    {
        WriteGameBoy(Path.Combine("A", "Game Boy.dat"));
        WriteNes("A0.dat");
        WriteAmiga("a.xml");
        _tmp.WriteText(Path.Combine("dats", "readme.txt"), "not a DAT");

        // Case ignored: '.' < '/' < '0', whereas '\' would come after '0'
        DatService.GetDatFolderFiles(_dats, recursive: true).Select(f => Path.GetRelativePath(_dats, f)).Should().Equal(
            "a.xml",
            Path.Combine("A", "Game Boy.dat"),
            "A0.dat");
        DatService.GetDatFolderFiles(_dats).Select(Path.GetFileName).Should().Equal("a.xml", "A0.dat");
    }

    [Fact]
    public void Preview_ShouldListTheDatsFromTheirHeaders()
    {
        WriteNes();
        WriteGameBoy(Path.Combine("No-Intro", "Nintendo - Game Boy.dat"));
        WriteDat("Sony - PlayStation (2026-06-15).dat", "Sony - PlayStation", "redump.org", ("Game (Europe)", Rom("Game (Europe).bin", Lemmings)));

        var preview = _services.DatService.ReadDatFolderPreview(_dats, recursive: true);

        preview.Should().Equal(
            new DatFolderEntry("Nintendo - Nintendo Entertainment System (20230501-101010).dat", Nes, NamingConvention.NoIntro),
            new DatFolderEntry(Path.Combine("No-Intro", "Nintendo - Game Boy.dat"), GameBoy, NamingConvention.NoIntro),
            new DatFolderEntry("Sony - PlayStation (2026-06-15).dat", "Sony - PlayStation", NamingConvention.Redump));
        _services.DatService.ReadDatFolderPreview(_dats).Select(e => e.Name).Should().Equal(Nes, "Sony - PlayStation");
    }

    // =========================================================================
    // DATs left out
    // =========================================================================

    [Fact]
    public async Task DatsLeftOut_ShouldOnlyBeReadForTheirHeader()
    {
        WriteGameBoy();

        // A DAT whose games break after those its preview reads: reading it whole would fail
        var games = string.Concat(Enumerable.Range(0, DatService.PreviewGameCount + 1)
            .Select(i => $"""<game name="Game {i} (World)"><rom name="Game {i} (World).nes" size="{i + 1}" crc="{i:x8}"/></game>"""));
        _tmp.WriteText(Path.Combine("dats", "Nintendo - NES.dat"), $"""
            <?xml version="1.0"?>
            <datafile><header><name>{Nes}</name><homepage>No-Intro</homepage></header>{games}<game name="Broken"><rom name=
            """);

        await FluentActions.Invoking(() => ReadLot()).Should().ThrowAsync<Exception>("the DAT is broken");

        var lot = await ReadLot(false, Nes.ToLowerInvariant());
        lot.Dats!.Select(d => d.Name).Should().Equal(GameBoy);
        lot.DatFor(Nes).Should().BeNull();
    }

    [Fact]
    public async Task EveryDatLeftOut_ShouldBeRefused()
    {
        WriteNes();
        WriteGameBoy();

        var refusal = await Refusal(() => ReadLot(false, GameBoy, Nes));

        refusal.Reason.Key.Should().Be("dat.folder_all_excluded");
    }

    // =========================================================================
    // Refusals
    // =========================================================================

    [Fact]
    public async Task FolderWithoutDat_ShouldBeRefused()
    {
        _tmp.CreateSubdirectory(Path.Combine("dats", "No-Intro"));
        WriteGameBoy(Path.Combine("No-Intro", "Nintendo - Game Boy.dat"));

        (await Refusal(() => ReadLot(recursive: false))).Reason.Key.Should().Be("dat.folder_empty");
        (await ReadLot(recursive: true)).Dats.Should().ContainSingle();
    }

    [Fact]
    public async Task TwoDatsOfTheSameName_ShouldBeRefusedWhateverTheCase()
    {
        WriteGameBoy();
        WriteDat(Path.Combine("Old", "Game Boy (2019).dat"), GameBoy.ToUpperInvariant(), "No-Intro", ("Tetris (World)", Rom("Tetris (World).gb", TetrisGameBoy)));

        (await ReadLot(recursive: false)).Dats.Should().ContainSingle();

        var refusal = await Refusal(() => ReadLot(recursive: true));
        refusal.Reason.Key.Should().Be("dat.folder_duplicate_dat");
        _services.I18n.Format(refusal.Reason).Should()
            .Contain(Path.Combine("Old", "Game Boy (2019).dat"))
            .And.Contain("Nintendo - Game Boy (20230414-173400).dat");

        // Leaving one of them out does not make the name designate a single DAT
        (await Refusal(() => ReadLot(true, GameBoy))).Reason.Key.Should().Be("dat.folder_duplicate_dat");
    }

    [Fact]
    public async Task DatOfMameMachines_ShouldBeRefusedInAFolder()
    {
        WriteGameBoy();
        _tmp.WriteText(Path.Combine("dats", "mame.xml"), """
            <?xml version="1.0"?>
            <mame build="0.289 (mame0289)">
            	<machine name="sf2"><description>Street Fighter II: The World Warrior (World 910522)</description><rom name="sf2e_30g.11e" size="131072" crc="fe39ee33"/><driver status="good"/></machine>
            </mame>
            """);

        var refusal = await Refusal(() => ReadLot());

        refusal.Reason.Key.Should().Be("dat.folder_mame_machines");
        _services.I18n.Format(refusal.Reason).Should().Contain("mame.xml");
    }

    /// <summary>The preview is enough to refuse a folder before its DATs are read.</summary>
    [Fact]
    public void SoftwareListsMixedWithOtherDats_ShouldBeRefusedFromThePreview()
    {
        WriteGameBoy();
        _tmp.WriteText(Path.Combine("dats", "megadriv.xml"), """
            <?xml version="1.0"?>
            <datafile><header><name>megadriv</name><description>megadriv cartridges</description></header>
            <machine name="sonic"><description>Sonic The Hedgehog (Europe, USA)</description><year>1991</year><publisher>Sega</publisher><rom name="mpr-13913.ic1" size="16" crc="f9394e97"/></machine>
            </datafile>
            """);

        var preview = _services.DatService.ReadDatFolderPreview(_dats);
        var refusal = FluentActions.Invoking(() => DatService.CheckDatFolder(_dats, preview)).Should().Throw<DatFolderException>().Which;

        refusal.Reason.Key.Should().Be("dat.folder_mixed_mame");
        _services.I18n.Format(refusal.Reason).Should().Be(_services.I18n.Tr(
            "dat.folder_mixed_mame",
            ("mame", "megadriv.xml"),
            ("other", "Nintendo - Game Boy (20230414-173400).dat")));
    }

    // =========================================================================
    // Index of the lot
    // =========================================================================

    [Fact]
    public async Task Matches_ShouldNameEachDatThatKnowsAFileInTheOrderOfTheLot()
    {
        WriteNes();
        WriteGameBoy();
        WriteAmiga();

        var lot = await ReadLot();

        // A file of two DATs: both, in the order of the lot
        var shared = lot.FindMatchesByDat(SharedBios.Length, Crc(SharedBios));
        shared.Select(found => found.Dat.Name).Should().Equal(GameBoy, Nes);
        shared.Should().OnlyContain(found => found.Matches.Count == 1 && found.Matches[0].Name == "[BIOS] Shared (World).bin");
        lot.FindAllMatches(SharedBios.Length, Crc(SharedBios)).Should().Equal(shared[0].Matches[0], shared[1].Matches[0]);

        // A file of one DAT, by its size and CRC, then by its CRC alone (unique in its DAT) when the size differs
        lot.FindMatchesByDat(TetrisNes.Length, Crc(TetrisNes)).Select(found => found.Dat.Name).Should().Equal(Nes);
        lot.FindMatchesByDat(TetrisNes.Length + 512, Crc(TetrisNes)).Select(found => found.Dat.Name).Should().Equal(Nes);
        lot.FindAllMatches(TetrisNes.Length, Crc(TetrisNes)).Should().BeSameAs(lot.DatFor(Nes)!.FindAllMatches(TetrisNes.Length, Crc(TetrisNes)));

        // A SHA-1 is looked for in every DAT
        lot.FindMatchesByDat(0, sha1: "0123456789ABCDEF0123456789ABCDEF01234567").Select(found => found.Dat.Name).Should().Equal(Amiga);

        // Unknown, or without checksum
        lot.FindMatchesByDat(4, "deadbeef").Should().BeEmpty();
        lot.FindAllMatches(4, "deadbeef").Should().BeEmpty();
        lot.FindMatchesByDat(TetrisNes.Length).Should().BeEmpty();
    }

    [Fact]
    public async Task Matches_OfACatalogOfASingleDat_ShouldNameNoDat()
    {
        WriteGameBoy();

        var single = await _services.DatService.ParseDatFileAsync(Path.Combine(_dats, "Nintendo - Game Boy (20230414-173400).dat"));

        single.FindMatchesByDat(SharedBios.Length, Crc(SharedBios)).Should().BeEmpty();
        single.FindAllMatches(SharedBios.Length, Crc(SharedBios)).Should().ContainSingle();
    }

    [Fact]
    public async Task Lot_ShouldBeReadFromEachOfItsDats()
    {
        WriteNes();
        WriteGameBoy(Path.Combine("No-Intro", "Nintendo - Game Boy.dat"));

        var lot = await ReadLot(recursive: true);

        lot.IsReadFrom(Path.Combine(_dats, "No-Intro", "Nintendo - Game Boy.dat")).Should().BeTrue();
        lot.IsReadFrom(Path.Combine(_dats, "Nintendo - Nintendo Entertainment System (20230501-101010).dat")).Should().BeTrue();
        lot.IsReadFrom(Path.Combine(_dats, "fixdat.dat")).Should().BeFalse();
    }

    // =========================================================================
    // Before the pipeline sorts with a lot
    // =========================================================================

    [Fact]
    public async Task Sort_ShouldStillRefuseALot()
    {
        WriteNes();
        WriteGameBoy();
        var source = _tmp.CreateSubdirectory("src");
        _tmp.WriteBytes(Path.Combine("src", "Tetris (USA).nes"), TetrisNes);
        var config = new SortConfig { SourceDir = source, DestinationDir = _tmp.GetFilePath("dst"), DatFile = _dats };

        var run = await FluentActions.Invoking(() => _services.Processor.ExecuteAsync(config, isDryRun: true))
            .Should().ThrowAsync<InvalidOperationException>();

        run.Which.Message.Should().Contain("Nintendo - Game Boy (20230414-173400).dat");
        Directory.Exists(config.DestinationDir).Should().BeFalse();
    }
}
