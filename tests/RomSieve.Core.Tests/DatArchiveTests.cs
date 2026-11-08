using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// An archive given as the DAT (a daily pack of No-Intro, a DAT of Redump as it is downloaded), read without extraction:
/// an archive of a single DAT is that DAT; an archive of several DATs is read as a folder of DATs (a lot), every DAT of
/// the archive whatever its folder in it, the folders mirrored as those of a folder; an archive without DAT is refused,
/// and a member whose name would lead out of a folder is left out.
/// </summary>
public sealed class DatArchiveTests : IDisposable
{
    private const string GameBoy = "Nintendo - Game Boy";
    private const string Nes = "Nintendo - Nintendo Entertainment System";
    private const string Amiga = "Commodore Amiga - Games - [ADF]";

    private static readonly byte[] TetrisGameBoy = Encoding.ASCII.GetBytes("TETRIS GAME BOY");
    private static readonly byte[] TetrisNes = Encoding.ASCII.GetBytes("TETRIS NES");
    private static readonly byte[] Lemmings = Encoding.ASCII.GetBytes("LEMMINGS DISK");

    private readonly TempDirectoryFixture _tmp = new();
    private readonly CoreTestServices _services = new();

    public void Dispose() => _tmp.Dispose();

    private static string Crc(byte[] data) => Crc32.HashToUInt32(data).ToString("x8");

    private static string Dat(string name, string homepage, string game, string rom, byte[] data) => $"""
        <?xml version="1.0"?>
        <datafile>
        	<header><name>{name}</name><description>{name}</description><homepage>{homepage}</homepage></header>
        	<game name="{game}"><description>{game}</description><rom name="{rom}" size="{data.Length}" crc="{Crc(data)}"/></game>
        </datafile>
        """;

    private static string GameBoyDat => Dat(GameBoy, "No-Intro", "Tetris (World) (Rev 1)", "Tetris (World) (Rev 1).gb", TetrisGameBoy);

    private static string NesDat => Dat(Nes, "No-Intro", "Tetris (USA)", "Tetris (USA).nes", TetrisNes);

    private static string AmigaDat => Dat(Amiga, "TOSEC", "Lemmings (1991)(Psygnosis)", "Lemmings (1991)(Psygnosis).adf", Lemmings);

    // A ZIP archive holding each member under the name given, as it is (folders, "..")
    private string WriteZip(string name, params (string Member, byte[] Data)[] members)
    {
        var path = _tmp.GetFilePath(name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (member, data) in members)
        {
            using var stream = zip.CreateEntry(member).Open();
            stream.Write(data);
        }

        return path;
    }

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    private Task<DatCatalog> Read(string path, DatFolderOptions? options = null) =>
        _services.DatService.ParseDatFileAsync(path, options ?? DatFolderOptions.Default);

    // =========================================================================
    // An archive of a single DAT
    // =========================================================================

    [Fact]
    public async Task ArchiveOfASingleDat_ShouldBeReadAsThatDat()
    {
        var zip = WriteZip("Nintendo - Game Boy (20230414-173400).zip", ("Nintendo - Game Boy (20230414-173400).dat", Text(GameBoyDat)), ("readme.txt", Text("not a DAT")));

        var catalog = await Read(zip);

        _services.DatService.HoldsSeveralDats(zip).Should().BeFalse();
        catalog.Dats.Should().BeNull();
        catalog.Header.Name.Should().Be(GameBoy);
        catalog.DetectConvention().Should().Be(NamingConvention.NoIntro);
        catalog.Games.Select(g => g.Name).Should().Equal("Tetris (World) (Rev 1)");
        catalog.FindMatch(TetrisGameBoy.Length, Crc(TetrisGameBoy)).Should().NotBeNull();
        catalog.Header.SourceFile.Should().Be(zip, "no export may be written over the archive the DAT was read from");
        catalog.IsReadFrom(zip).Should().BeTrue();

        var preview = _services.DatService.ReadDatPreview(zip);
        preview.Header.Name.Should().Be(GameBoy, "an archive of a single DAT is named by that DAT");
        preview.DetectConvention().Should().Be(NamingConvention.NoIntro);
    }

    [Fact]
    public async Task ClrMameProDatInAnArchive_ShouldBeReadAsTheFileWouldBe_EvenInLatin1()
    {
        var latin1 = Encoding.Latin1.GetBytes($"""
            clrmamepro (
            	name "{GameBoy}"
            	description "Pokémon"
            	homepage No-Intro
            )

            game (
            	name "Pokémon (World)"
            	description "Pokémon (World)"
            	rom ( name "Pokémon (World).gb" size {TetrisGameBoy.Length} crc {Crc(TetrisGameBoy)} )
            )
            """);
        var zip = WriteZip("gb.zip", ("Nintendo - Game Boy.dat", latin1));

        var catalog = await Read(zip);

        catalog.Header.IsClrMameProSource.Should().BeTrue();
        catalog.Games.Select(g => g.Name).Should().Equal("Pokémon (World)");
        _services.DatService.ReadDatPreview(zip).Games.Select(g => g.Name).Should().Equal("Pokémon (World)");
    }

    // =========================================================================
    // An archive of several DATs
    // =========================================================================

    [Fact]
    public async Task ArchiveOfSeveralDats_ShouldBeALotOfAllItsDatsWhateverTheirFolder()
    {
        var zip = WriteZip(
            "No-Intro Love Pack (PC XML) (2026-11-08).zip",
            ("TOSEC/Commodore/Amiga.xml", Text(AmigaDat)),
            ("Nintendo - Nintendo Entertainment System.dat", Text(NesDat)),
            ("Nintendo/Nintendo - Game Boy.dat", Text(GameBoyDat)),
            ("index.txt", Text("not a DAT")));

        var lot = await Read(zip, new DatFolderOptions(Recursive: false));

        _services.DatService.HoldsSeveralDats(zip).Should().BeTrue();
        lot.Header.Name.Should().Be("No-Intro Love Pack (PC XML) (2026-11-08)", "a lot is named after its archive, without its extension");
        lot.Dats!.Select(d => d.Name).Should().Equal(new[] { Nes, GameBoy, Amiga }, "the subfolders of an archive are always read, in the order of the paths");
        lot.Dats!.Select(d => d.RelativePath).Should().Equal(
            "Nintendo - Nintendo Entertainment System.dat",
            Path.Combine("Nintendo", "Nintendo - Game Boy.dat"),
            Path.Combine("TOSEC", "Commodore", "Amiga.xml"));
        lot.Dats!.Select(d => d.RelativeFolder).Should().Equal(string.Empty, "Nintendo", Path.Combine("TOSEC", "Commodore"));
        lot.Dats!.Select(d => d.Header.Convention).Should().Equal(NamingConvention.NoIntro, NamingConvention.NoIntro, NamingConvention.Tosec);
        lot.LotFolder.Should().BeNull("no DAT exported next to the archive is read by the next sort");
        lot.IsReadFrom(zip).Should().BeTrue();

        var preview = _services.DatService.ReadDatFolderPreview(zip);
        preview.Select(e => e.Name).Should().Equal(Nes, GameBoy, Amiga);
        preview.Select(e => e.RelativePath).Should().Equal(lot.Dats!.Select(d => d.RelativePath));
        _services.DatService.ReadDatPreview(zip).Header.Name.Should().Be("No-Intro Love Pack (PC XML) (2026-11-08)");
    }

    [Fact]
    public async Task DatsOfAnArchiveReadOnDemand_ShouldBeReadAgainFromTheArchive()
    {
        var zip = WriteZip("pack.zip", ("a.dat", Text(GameBoyDat)), ("b.dat", Text(NesDat)));

        var lot = await Read(zip, new DatFolderOptions(LoadOnDemand: true));

        lot.Dats!.Should().OnlyContain(d => !d.IsLoaded);
        lot.DatFor(Nes)!.Games.Select(g => g.Name).Should().Equal("Tetris (USA)");
        lot.Dats!.Single(d => d.Name == Nes).LoadCount.Should().Be(1);
        lot.Dats!.Single(d => d.Name == GameBoy).IsLoaded.Should().BeFalse();
    }

    [Fact]
    public async Task ArchiveOfSeveralDats_ShouldLeaveOutTheDatsLeftOut_AndRefuseTwoDatsOfTheSameName()
    {
        var zip = WriteZip("pack.zip", ("a.dat", Text(GameBoyDat)), ("b.dat", Text(NesDat)));
        var lot = await Read(zip, new DatFolderOptions(ExcludedDats: [GameBoy]));
        lot.Dats!.Select(d => d.Name).Should().Equal(Nes);

        var twice = WriteZip("twice.zip", ("a.dat", Text(GameBoyDat)), ("old/a.dat", Text(GameBoyDat)));
        var refusal = (await FluentActions.Invoking(() => Read(twice)).Should().ThrowAsync<DatFolderException>()).Which;
        refusal.Reason.Key.Should().Be("dat.folder_duplicate_dat");
    }

    // =========================================================================
    // Refusals and names
    // =========================================================================

    [Fact]
    public async Task ArchiveWithoutDat_ShouldBeRefusedWithItsOwnMessage()
    {
        var zip = WriteZip("roms.zip", ("Tetris (World).gb", TetrisGameBoy));

        _services.DatService.HoldsSeveralDats(zip).Should().BeTrue("an archive that is not a single DAT is refused as a folder of DATs");
        var refusal = (await FluentActions.Invoking(() => Read(zip)).Should().ThrowAsync<DatFolderException>()).Which;
        refusal.Reason.Key.Should().Be("dat.archive_empty");
        FluentActions.Invoking(() => _services.DatService.ReadDatPreview(zip)).Should().Throw<DatFolderException>()
            .Which.Reason.Key.Should().Be("dat.archive_empty");
        FluentActions.Invoking(() => DatService.CheckDatFolder(zip, _services.DatService.ReadDatFolderPreview(zip))).Should().Throw<DatFolderException>()
            .Which.Reason.Key.Should().Be("dat.archive_empty");
    }

    [Fact]
    public async Task UnreadableArchive_ShouldBeRefusedAsAnArchiveWithoutDat()
    {
        var zip = _tmp.WriteBytes("broken.zip", Text("PK not really a ZIP"));

        var refusal = (await FluentActions.Invoking(() => Read(zip)).Should().ThrowAsync<DatFolderException>()).Which;
        refusal.Reason.Key.Should().Be("dat.archive_empty");
    }

    [Fact]
    public async Task MemberLeadingOutOfAFolder_ShouldBeLeftOut()
    {
        var zip = WriteZip("pack.zip", ("../Evil.dat", Text(NesDat)), ("Nintendo - Game Boy.dat", Text(GameBoyDat)), ("C:Drive.dat", Text(AmigaDat)));

        _services.DatService.HoldsSeveralDats(zip).Should().BeFalse("the only DAT left is that of the Game Boy");
        var catalog = await Read(zip);
        catalog.Dats.Should().BeNull();
        catalog.Header.Name.Should().Be(GameBoy);
    }

    [Fact]
    public void HoldsSeveralDats_ShouldBeTrueForAFolderAndFalseForAFileOrNothing()
    {
        var folder = _tmp.CreateSubdirectory("dats");
        var file = _tmp.WriteText(Path.Combine("dats", "gb.dat"), GameBoyDat);

        _services.DatService.HoldsSeveralDats(folder).Should().BeTrue();
        _services.DatService.HoldsSeveralDats(file).Should().BeFalse();
        _services.DatService.HoldsSeveralDats(_tmp.GetFilePath("missing.zip")).Should().BeFalse();
        _services.DatService.HoldsSeveralDats(string.Empty).Should().BeFalse();
    }

    // =========================================================================
    // A sort
    // =========================================================================

    [Fact]
    public async Task Sort_ShouldSortEachFileWithItsDatOfTheArchive_AndMirrorTheFoldersOfTheArchive()
    {
        var zip = WriteZip("pack.zip", ("Nintendo/Nintendo - Game Boy.dat", Text(GameBoyDat)), ("TOSEC/Commodore/Amiga.xml", Text(AmigaDat)));
        var src = _tmp.CreateSubdirectory("src");
        _tmp.WriteBytes(Path.Combine("src", "tetris.gb"), TetrisGameBoy);
        _tmp.WriteBytes(Path.Combine("src", "lemmings.adf"), Lemmings);
        var dst = _tmp.GetFilePath("dst");

        var result = await _services.Processor.ExecuteAsync(
            new SortConfig { SourceDir = src, DestinationDir = dst, DatFile = zip, Pattern = "{title}", DatFolderMirror = true },
            isDryRun: true);

        string Target(string source) =>
            Path.GetRelativePath(dst, result.Items.Single(it => Path.GetFileName(it.SourcePath) == source).TargetPath!).Replace('\\', '/');
        Target("tetris.gb").Should().Be($"Nintendo/{GameBoy}/Tetris/Tetris (World) (Rev 1).gb");
        Target("lemmings.adf").Should().Be($"TOSEC/Commodore/{Amiga}/Lemmings/Lemmings (1991)(Psygnosis).adf");
    }

    [Fact]
    public async Task Sort_WithAnArchiveOfASingleDat_ShouldSortAsWithTheDatFile()
    {
        var zip = WriteZip("gb.zip", ("Nintendo - Game Boy.dat", Text(GameBoyDat)));
        var src = _tmp.CreateSubdirectory("src");
        _tmp.WriteBytes(Path.Combine("src", "tetris.gb"), TetrisGameBoy);
        var dst = _tmp.GetFilePath("dst");

        var result = await _services.Processor.ExecuteAsync(
            new SortConfig { SourceDir = src, DestinationDir = dst, DatFile = zip, Pattern = "{title}" },
            isDryRun: true);

        var item = result.Items.Single();
        item.Status.Should().Be(FileStatus.Valid);
        Path.GetRelativePath(dst, item.TargetPath!).Replace('\\', '/').Should().Be("Tetris/Tetris (World) (Rev 1).gb",
            "a single DAT has no folder of its own");
    }
}
