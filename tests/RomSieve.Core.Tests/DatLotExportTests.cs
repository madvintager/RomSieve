using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// The exports of a sort with a lot of DATs: a folder, a DAT per DAT at its root (filtered DAT of the catalog, DAT of the
/// collection, fixdat), in the format of each DAT by default; the Miss List and the fixdat only hold the DATs with ROMs
/// missing and a game found; the reports name the DAT of each file. No DAT is exported into the folder of the lot.
/// </summary>
public sealed class DatLotExportTests : IDisposable
{
    private const string GameBoy = "Nintendo - Game Boy";
    private const string GameBoyColor = "Nintendo - Game Boy Color";
    private const string Amiga = "Commodore Amiga - Games - [ADF]";

    private static readonly byte[] TetrisGbWorld = Encoding.ASCII.GetBytes("TETRIS GB WORLD");
    private static readonly byte[] TetrisGbJapan = Encoding.ASCII.GetBytes("TETRIS GB JAPAN");
    private static readonly byte[] TetrisGbcWorld = Encoding.ASCII.GetBytes("TETRIS GBC WORLD");
    private static readonly byte[] TetrisGbcJapan = Encoding.ASCII.GetBytes("TETRIS GBC JAPAN");
    private static readonly byte[] SharedBios = Encoding.ASCII.GetBytes("SHARED BIOS");
    private static readonly byte[] Lemmings = Encoding.ASCII.GetBytes("LEMMINGS DISK");

    private readonly TempDirectoryFixture _tmp = new();
    private readonly CoreTestServices _services = new();
    private readonly string _dats;
    private readonly string _src;
    private readonly string _out;

    public DatLotExportTests()
    {
        _dats = _tmp.CreateSubdirectory("dats");
        _src = _tmp.CreateSubdirectory("src");
        _out = _tmp.GetFilePath("out");
    }

    public void Dispose() => _tmp.Dispose();

    private static string Crc(byte[] data) => Crc32.HashToUInt32(data).ToString("x8");

    private static string Rom(string name, byte[] data) => $"""<rom name="{name}" size="{data.Length}" crc="{Crc(data)}"/>""";

    private void WriteDat(string relativePath, string name, string homepage, params (string Game, string Roms)[] games) =>
        _tmp.WriteText(Path.Combine("dats", relativePath), $"""
            <?xml version="1.0"?>
            <datafile>
            	<header><name>{name}</name><description>{name}</description><homepage>{homepage}</homepage></header>
            {string.Concat(games.Select(g => $"""
            	<game name="{g.Game}"><description>{g.Game}</description>{g.Roms}</game>

            """))}
            </datafile>
            """);

    private void WriteGameBoy() =>
        WriteDat("Nintendo - Game Boy (20230414-173400).dat", GameBoy, "No-Intro",
            ("Tetris (World) (Rev 1)", Rom("Tetris (World) (Rev 1).gb", TetrisGbWorld)),
            ("Tetris (Japan)", Rom("Tetris (Japan).gb", TetrisGbJapan)),
            ("[BIOS] Shared (World)", Rom("[BIOS] Shared (World).bin", SharedBios)));

    // A CLRMAMEPRO DAT, read and written back in its format by default
    private void WriteGameBoyColor() =>
        _tmp.WriteText(Path.Combine("dats", "Nintendo - Game Boy Color (20230501-101010).dat"), $"""
            clrmamepro (
            	name "{GameBoyColor}"
            	description "{GameBoyColor}"
            	homepage "No-Intro"
            )

            game (
            	name "Tetris (World)"
            	description "Tetris (World)"
            	rom ( name "Tetris (World).gbc" size {TetrisGbcWorld.Length} crc {Crc(TetrisGbcWorld)} )
            )

            game (
            	name "Tetris (Japan)"
            	description "Tetris (Japan)"
            	rom ( name "Tetris (Japan).gbc" size {TetrisGbcJapan.Length} crc {Crc(TetrisGbcJapan)} )
            )

            game (
            	name "[BIOS] Shared (World)"
            	description "[BIOS] Shared (World)"
            	rom ( name "[BIOS] Shared (World).bin" size {SharedBios.Length} crc {Crc(SharedBios)} )
            )
            """);

    private void WriteAmiga() =>
        WriteDat(Path.Combine("TOSEC", "Commodore Amiga - Games - [ADF] (TOSEC-v2025-01-30_CM).dat"), Amiga, "TOSEC",
            ("Lemmings (1991)(Psygnosis)", Rom("Lemmings (1991)(Psygnosis).adf", Lemmings)));

    private void WriteLot()
    {
        WriteGameBoy();
        WriteGameBoyColor();
        WriteAmiga();
    }

    // The whole Game Boy DAT, one ROM of the Game Boy Color DAT, the BIOS both DATs have, nothing of the Amiga DAT
    private void WriteSources()
    {
        _tmp.WriteBytes(Path.Combine("src", "tetris_gb_world.gb"), TetrisGbWorld);
        _tmp.WriteBytes(Path.Combine("src", "tetris_gb_japan.gb"), TetrisGbJapan);
        _tmp.WriteBytes(Path.Combine("src", "bios.bin"), SharedBios);
        _tmp.WriteBytes(Path.Combine("src", "tetris_gbc_japan.gbc"), TetrisGbcJapan);
    }

    private SortConfig Config() => new()
    {
        SourceDir = _src,
        DestinationDir = _tmp.GetFilePath("dst"),
        DatFile = _dats,
        DatRecursive = true,
        DatFolderMirror = true,
        Pattern = "{title}"
    };

    private Task<SortExecutionResult> SimulateAsync() => _services.Processor.ExecuteAsync(Config(), isDryRun: true);

    private IEnumerable<string> OutputFiles() =>
        Directory.EnumerateFiles(_out, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_out, f).Replace('\\', '/'));

    // =========================================================================
    // Miss List and fixdat
    // =========================================================================

    [Fact]
    public async Task MissingRoms_ShouldOnlyBeThoseOfTheDatsWithAGameFound()
    {
        WriteLot();
        WriteSources();

        var result = await SimulateAsync();

        // The Amiga DAT has no game in the source: counted, not listed
        result.MissingRoms.Select(r => r.Name).Should().Equal("Tetris (World).gbc");
        result.Stats.DatMissingCount.Should().Be(2, "the totals count every DAT of the lot");
        result.Stats.DescribeLotExportScope().Should().Be(LocalizedMessage.Create("log.dat_lot_export_scope", ("complete", 1), ("nothing", 1)));
    }

    [Fact]
    public async Task FixDat_ShouldBeWrittenForTheDatsMissingRomsWithAGameFound_FlatInTheFolder()
    {
        WriteLot();
        WriteSources();
        var result = await SimulateAsync();

        var fixDat = await _services.Processor.ExportFixDatAsync(result.MissingRoms, result.DatCatalog!, _out);

        fixDat.Dats!.Select(d => d.Name).Should().Equal(GameBoyColor);
        fixDat.GameCount.Should().Be(1);
        fixDat.CountFiles().Should().Be(1);
        OutputFiles().Should().Equal($"fixDat_{GameBoyColor}.xml");
        var written = await _services.DatService.ParseDatFileAsync(Path.Combine(_out, $"fixDat_{GameBoyColor}.xml"));
        written.Entries.Should().ContainSingle().Which.Name.Should().Be("Tetris (World).gbc", "the BIOS, sorted with the Game Boy, is present");
    }

    [Fact]
    public async Task FixDat_ShouldLeaveOutADatWithoutAnyGameFound_WhateverTheListGiven()
    {
        WriteLot();
        WriteSources();
        var result = await SimulateAsync();
        var amigaRoms = result.DatCatalog!.DatFor(Amiga)!.Entries;

        var fixDat = await _services.Processor.ExportFixDatAsync([.. result.MissingRoms, .. amigaRoms], result.DatCatalog, _out);

        fixDat.Dats!.Select(d => d.Name).Should().Equal(GameBoyColor);
    }

    [Fact]
    public async Task MissList_ShouldGroupTheMissingRomsByDat()
    {
        WriteLot();
        WriteSources();
        var result = await SimulateAsync();
        Directory.CreateDirectory(_out);
        var txt = Path.Combine(_out, "miss.txt");
        var csv = Path.Combine(_out, "miss.csv");

        await _services.Processor.ExportMissListToTxtAsync(result.MissingRoms, result.DatCatalog!, txt);
        await _services.Processor.ExportMissListToCsvAsync(result.MissingRoms, result.DatCatalog!, csv);

        File.ReadAllLines(txt).Should().Equal(
            "# Miss List: dats",
            "# DATs: 1",
            "# Total cataloged ROMs: 3",
            "# Missing ROMs: 1",
            string.Empty,
            $"## {GameBoyColor} (1/3)",
            "Tetris (World).gbc");
        File.ReadAllLines(csv).Should().Equal(
            "dat;game;rom_name;size;crc;md5;sha1",
            $"{GameBoyColor};Tetris (World);Tetris (World).gbc;{TetrisGbcWorld.Length};{Crc(TetrisGbcWorld)};;");
    }

    // =========================================================================
    // Filtered DAT of the catalog
    // =========================================================================

    [Fact]
    public async Task CatalogDat_ShouldWriteAFilteredDatPerDatInItsFormat_FlatInTheFolder()
    {
        WriteLot();
        var config = Config();
        config.Filters.OneGameOneRom = true;
        config.Filters.PriorityLanguages = ["ja"];

        var result = await _services.Processor.ExportCatalogDatAsync(config, _out, DatExportFormat.Auto);

        OutputFiles().Should().BeEquivalentTo(
            $"{Amiga} (RomSieve).xml",
            $"{GameBoy} (RomSieve).xml",
            $"{GameBoyColor} (RomSieve).dat");
        File.ReadAllText(Path.Combine(_out, $"{GameBoyColor} (RomSieve).dat")).Should().StartWith("clrmamepro");
        var gameBoy = await _services.DatService.ParseDatFileAsync(Path.Combine(_out, $"{GameBoy} (RomSieve).xml"));
        gameBoy.Header.Name.Should().Be($"{GameBoy} (RomSieve)");
        gameBoy.Games.Select(g => g.Name).Should().BeEquivalentTo("Tetris (Japan)", "[BIOS] Shared (World)");
        result.KeptCount.Should().Be(5, "a winner per DAT for Tetris, a BIOS per Game Boy DAT, Lemmings");
        result.CandidateCount.Should().Be(7);
        result.Items.Should().BeEmpty("the items of each DAT are forgotten once its DAT is written");
        Directory.EnumerateFiles(_out, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task CatalogDat_ShouldKeepTheGamesASimulationOfACompleteCollectionKeeps()
    {
        WriteLot();
        foreach (var (name, data) in new[] { ("a.gb", TetrisGbWorld), ("b.gb", TetrisGbJapan), ("c.bin", SharedBios), ("d.gbc", TetrisGbcWorld), ("e.gbc", TetrisGbcJapan), ("f.adf", Lemmings) })
        {
            _tmp.WriteBytes(Path.Combine("src", name), data);
        }

        var config = Config();
        config.Filters.OneGameOneRom = true;
        config.Filters.PriorityLanguages = ["ja"];

        var simulation = await _services.Processor.ExecuteAsync(config, isDryRun: true);
        var catalog = await _services.Processor.SelectFromCatalogAsync(config);

        // The BIOS file is sorted with the Game Boy only: the Game Boy Color keeps its BIOS game in the catalog
        static IEnumerable<string> Kept(IEnumerable<ProcessItem> items) =>
            items.Where(it => it.Status == FileStatus.Valid).Select(it => $"{it.DatName}/{it.DatGameName}");
        Kept(catalog.Items).Should().BeEquivalentTo([.. Kept(simulation.Items), $"{GameBoyColor}/[BIOS] Shared (World)"]);
    }

    [Fact]
    public async Task CatalogDat_ShouldTakeTheFormatAskedForEveryDat()
    {
        WriteLot();

        await _services.Processor.ExportCatalogDatAsync(Config(), _out, DatExportFormat.LogiqxXml);

        OutputFiles().Should().OnlyContain(f => f.EndsWith(".xml", StringComparison.Ordinal)).And.HaveCount(3);
    }

    [Fact]
    public async Task CatalogDat_OfTwoDatsWhoseNamesGiveTheSameFile_ShouldWriteBoth()
    {
        WriteDat("a.dat", "Collection: A", "No-Intro", ("Game (World)", Rom("Game (World).bin", TetrisGbWorld)));
        WriteDat("b.dat", "Collection- A", "No-Intro", ("Game (World)", Rom("Game (World).bin", TetrisGbcWorld)));

        await _services.Processor.ExportCatalogDatAsync(Config(), _out, DatExportFormat.LogiqxXml);

        OutputFiles().Should().BeEquivalentTo("Collection- A (RomSieve).xml", "Collection- A (RomSieve) (2).xml");
    }

    [Theory]
    [InlineData("")]
    [InlineData("TOSEC")]
    public async Task Exports_IntoTheFolderOfTheLot_ShouldBeRefused(string subfolder)
    {
        WriteLot();
        WriteSources();
        var target = Path.Combine(_dats, subfolder);
        var before = Directory.EnumerateFiles(_dats, "*", SearchOption.AllDirectories).ToList();
        var result = await SimulateAsync();

        await FluentActions.Invoking(() => _services.Processor.ExportCatalogDatAsync(Config(), target, DatExportFormat.Auto))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{_dats}*");
        await FluentActions.Invoking(() => _services.Processor.ExportFixDatAsync(result.MissingRoms, result.DatCatalog!, target))
            .Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Invoking(() => _services.Processor.ExportDatAsync(result.Items, target, DatExportFormat.Auto, result.DatCatalog))
            .Should().ThrowAsync<InvalidOperationException>();

        Directory.EnumerateFiles(_dats, "*", SearchOption.AllDirectories).Should().BeEquivalentTo(before);
    }

    // =========================================================================
    // DAT of the collection and reports
    // =========================================================================

    [Fact]
    public async Task CollectionDat_ShouldWriteADatPerDatOfTheFilesKept()
    {
        WriteLot();
        WriteSources();
        var result = await SimulateAsync();

        var exported = await _services.Processor.ExportDatAsync(result.Items, _out, DatExportFormat.Auto, result.DatCatalog);

        exported.Dats!.Select(d => d.Name).Should().Equal(GameBoy, GameBoyColor);
        exported.CountFiles().Should().Be(4);
        OutputFiles().Should().BeEquivalentTo($"{GameBoy} (Filtered).xml", $"{GameBoyColor} (Filtered).dat");
        var gameBoyColor = await _services.DatService.ParseDatFileAsync(Path.Combine(_out, $"{GameBoyColor} (Filtered).dat"));
        gameBoyColor.Entries.Select(e => e.Name).Should().Equal("Tetris (Japan).gbc");
    }

    [Fact]
    public async Task Reports_ShouldNameTheDatOfEachFile()
    {
        WriteLot();
        WriteSources();
        _tmp.WriteBytes(Path.Combine("src", "unknown.bin"), Encoding.ASCII.GetBytes("NOTHING"));
        var result = await SimulateAsync();
        Directory.CreateDirectory(_out);
        var json = Path.Combine(_out, "report.json");
        var csv = Path.Combine(_out, "report.csv");

        await _services.Processor.ExportReportToJsonAsync(result.Items, result.Stats, json);
        await _services.Processor.ExportReportToCsvAsync(result.Items, csv);

        using var document = JsonDocument.Parse(File.ReadAllText(json));
        var files = document.RootElement.GetProperty("files").EnumerateArray()
            .ToDictionary(f => f.GetProperty("source_file").GetString()!, f => f.GetProperty("dat").GetString());
        files["bios.bin"].Should().Be(GameBoy);
        files["tetris_gbc_japan.gbc"].Should().Be(GameBoyColor);
        files["unknown.bin"].Should().BeEmpty();
        var dats = document.RootElement.GetProperty("summary").GetProperty("dats").EnumerateArray().ToList();
        dats.Select(d => d.GetProperty("name").GetString()).Should().Equal(GameBoy, GameBoyColor, Amiga);
        dats[2].GetProperty("games_found").GetInt32().Should().Be(0);
        dats[2].GetProperty("convention").GetString().Should().Be("tosec");

        var lines = File.ReadAllLines(csv);
        lines[0].Should().EndWith(";error_message;dat");
        lines.Should().Contain(l => l.StartsWith("bios.bin;", StringComparison.Ordinal) && l.EndsWith($";{GameBoy}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reports_OfASingleDat_ShouldNotNameAnyDat()
    {
        WriteGameBoy();
        _tmp.WriteBytes(Path.Combine("src", "tetris_gb_world.gb"), TetrisGbWorld);
        var config = Config();
        config.DatFile = Path.Combine(_dats, "Nintendo - Game Boy (20230414-173400).dat");
        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);
        Directory.CreateDirectory(_out);

        await _services.Processor.ExportReportToJsonAsync(result.Items, result.Stats, Path.Combine(_out, "report.json"));
        await _services.Processor.ExportReportToCsvAsync(result.Items, Path.Combine(_out, "report.csv"));

        File.ReadAllText(Path.Combine(_out, "report.json")).Should().NotContain("\"dat\":").And.NotContain("\"dats\"");
        File.ReadAllLines(Path.Combine(_out, "report.csv"))[0].Should().EndWith(";error_message");
    }
}
