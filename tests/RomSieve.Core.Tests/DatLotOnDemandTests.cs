using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// The DATs of a lot read on demand: each DAT is read once for its summary and the keys of its checksums, then only the
/// DATs a file may be one of the ROMs of are read again. A run gives exactly what it gives with every DAT kept in memory
/// (items, statuses, reasons, targets, statistics, exports), a DAT no file needs is never read again, and the filtered DAT
/// of the catalog reads, writes and forgets its DATs one at a time.
/// </summary>
public sealed class DatLotOnDemandTests : IDisposable
{
    private const string GameBoy = "Nintendo - Game Boy";
    private const string GameBoyColor = "Nintendo - Game Boy Color";
    private const string Amiga = "Commodore Amiga - Games - [ADF]";
    private const string PlayStation = "Sony - PlayStation";
    private const string FmTowns = "Fujitsu FM Towns - Games - [CD]";
    private const string Untouched = "Atari - 2600";

    private static readonly byte[] TetrisGbWorld = Encoding.ASCII.GetBytes("TETRIS GB WORLD");
    private static readonly byte[] TetrisGbJapan = Encoding.ASCII.GetBytes("TETRIS GB JAPAN");
    private static readonly byte[] TetrisGbcWorld = Encoding.ASCII.GetBytes("TETRIS GBC WORLD");
    private static readonly byte[] TetrisGbcJapan = Encoding.ASCII.GetBytes("TETRIS GBC JAPAN");
    private static readonly byte[] SharedBios = Encoding.ASCII.GetBytes("SHARED BIOS");
    private static readonly byte[] Lemmings = Encoding.ASCII.GetBytes("LEMMINGS DISK");
    private static readonly byte[] LemmingsCrack = Encoding.ASCII.GetBytes("LEMMINGS CRACKED");
    private static readonly byte[] Bin1 = Encoding.ASCII.GetBytes("FINAL QUEST DISC 1 DATA");
    private static readonly byte[] Bin2 = Encoding.ASCII.GetBytes("FINAL QUEST DISC 2 DATA");
    private static readonly byte[] Cue1 = Encoding.ASCII.GetBytes(Cue("Final Quest (USA) (Disc 1).bin"));
    private static readonly byte[] Cue2 = Encoding.ASCII.GetBytes(Cue("Final Quest (USA) (Disc 2).bin"));
    private static readonly byte[] Combat = Encoding.ASCII.GetBytes("COMBAT CARTRIDGE");

    private readonly TempDirectoryFixture _tmp = new();
    private readonly CoreTestServices _services = new();
    private readonly string _dats;
    private readonly string _src;

    public DatLotOnDemandTests()
    {
        _dats = _tmp.CreateSubdirectory("dats");
        _src = _tmp.CreateSubdirectory("src");
        WriteLot();
        WriteSources();
    }

    public void Dispose() => _tmp.Dispose();

    private static string Cue(string bin) => $"FILE \"{bin}\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00\r\n";

    private static string Crc(byte[] data) => Crc32.HashToUInt32(data).ToString("x8");

    private static string Sha1(byte[] data) => Convert.ToHexStringLower(SHA1.HashData(data));

    private static string Rom(string name, byte[] data, string? sha1 = null) =>
        $"""<rom name="{name}" size="{data.Length}" crc="{Crc(data)}"{(sha1 is null ? string.Empty : $" sha1=\"{sha1}\"")}/>""";

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

    // A lot of six DATs: No-Intro (one in CLRMAMEPRO), TOSEC in a subfolder, Redump, a DAT of disks, and a DAT no file needs
    private void WriteLot()
    {
        WriteDat("Nintendo - Game Boy (20230414-173400).dat", GameBoy, "No-Intro",
            ("Tetris (World) (Rev 1)", Rom("Tetris (World) (Rev 1).gb", TetrisGbWorld, sha1: Sha1(TetrisGbWorld))),
            ("Tetris (Japan)", Rom("Tetris (Japan).gb", TetrisGbJapan, sha1: "0123456789abcdef0123456789abcdef01234567")),
            ("[BIOS] Shared (World)", Rom("[BIOS] Shared (World).bin", SharedBios)));
        _tmp.WriteText(Path.Combine("dats", "Nintendo - Game Boy Color (20230501-101010).dat"), $"""
            clrmamepro ( name "{GameBoyColor}" description "{GameBoyColor}" homepage "No-Intro" )
            game ( name "Tetris (World)" description "Tetris (World)" rom ( name "Tetris (World).gbc" size {TetrisGbcWorld.Length} crc {Crc(TetrisGbcWorld)} ) )
            game ( name "Tetris (Japan)" description "Tetris (Japan)" rom ( name "Tetris (Japan).gbc" size {TetrisGbcJapan.Length} crc {Crc(TetrisGbcJapan)} ) )
            game ( name "[BIOS] Shared (World)" description "[BIOS] Shared (World)" rom ( name "[BIOS] Shared (World).bin" size {SharedBios.Length} crc {Crc(SharedBios)} ) )
            """);
        WriteDat(Path.Combine("TOSEC", "Commodore Amiga - Games - [ADF] (TOSEC-v2025-01-30_CM).dat"), Amiga, "TOSEC",
            ("Lemmings (1991)(Psygnosis)", Rom("Lemmings (1991)(Psygnosis).adf", Lemmings)),
            ("Lemmings (1991)(Psygnosis)[cr PDX]", Rom("Lemmings (1991)(Psygnosis)[cr PDX].adf", LemmingsCrack)));
        WriteDat(Path.Combine("Redump", "Sony - PlayStation - Datfile (2) (2025-01-01).dat"), PlayStation, "redump.org",
            ("Final Quest (USA) (Disc 1)", Rom("Final Quest (USA) (Disc 1).cue", Cue1) + Rom("Final Quest (USA) (Disc 1).bin", Bin1)),
            ("Final Quest (USA) (Disc 2)", Rom("Final Quest (USA) (Disc 2).cue", Cue2) + Rom("Final Quest (USA) (Disc 2).bin", Bin2)),
            ("Night Quest (USA)", Rom("Night Quest (USA).cue", Encoding.ASCII.GetBytes(Cue("Night Quest (USA).bin"))) + Rom("Night Quest (USA).bin", Encoding.ASCII.GetBytes("NIGHT QUEST"))));
        _tmp.WriteText(Path.Combine("dats", "Fujitsu FM Towns - Games - [CD] (TOSEC-v2025).dat"), $"""
            <?xml version="1.0"?>
            <datafile>
            	<header><name>{FmTowns}</name><homepage>TOSEC</homepage></header>
            	<game name="Shadow of the Beast (1990)(Psygnosis)"><description>Shadow of the Beast (1990)(Psygnosis)</description><disk name="Shadow of the Beast (1990)(Psygnosis)" sha1="{ChdTestFile.Sha1Of("beast")}"/></game>
            </datafile>
            """);
        WriteDat("Atari - 2600 (20240101-000000).dat", Untouched, "No-Intro",
            ("Combat (USA)", Rom("Combat (USA).a26", Combat)));
    }

    // Files of five of the DATs, a file two DATs know, a SHA-1 that differs, a crack, a CHD, files no DAT knows
    private void WriteSources()
    {
        void Write(string name, byte[] data) => _tmp.WriteBytes(Path.Combine("src", name), data);
        Write("tetris_gb_world.gb", TetrisGbWorld);
        Write("tetris_gb_japan.gb", TetrisGbJapan);
        Write("bios.bin", SharedBios);
        Write("tetris_gbc_japan.gbc", TetrisGbcJapan);
        Write("lemmings.adf", Lemmings);
        Write("lemmings_crack.adf", LemmingsCrack);
        Write("Final Quest (USA) (Disc 1).cue", Cue1);
        Write("Final Quest (USA) (Disc 1).bin", Bin1);
        Write("Final Quest (USA) (Disc 2).cue", Cue2);
        Write("Final Quest (USA) (Disc 2).bin", Bin2);
        Write("beast.chd", ChdTestFile.Create("beast"));
        Write("Night Quest (USA).chd", ChdTestFile.Create("night"));
        Write("unknown.bin", Encoding.ASCII.GetBytes("NOTHING AT ALL"));
        Write("same_size.gb", Encoding.ASCII.GetBytes("TETRIS GB WORLX"));
    }

    public static TheoryData<string> Scenarios() => ["default", "1g1r", "sha1", "chd_by_name", "flat"];

    private SortConfig Config(string scenario)
    {
        var config = new SortConfig
        {
            SourceDir = _src,
            DestinationDir = _tmp.GetFilePath("dst"),
            DatFile = _dats,
            DatRecursive = true,
            DatFolderMirror = true,
            Pattern = "{title}",
            GenerateM3u = true
        };
        switch (scenario)
        {
            case "1g1r":
                config.Filters.OneGameOneRom = true;
                config.Filters.PriorityLanguages = ["ja"];
                break;
            case "sha1":
                config.VerifySha1 = true;
                break;
            case "chd_by_name":
                config.MatchChdByName = true;
                break;
            case "flat":
                config.FolderPerDat = false;
                config.Pattern = "{dat}/{title}";
                config.HashingParallelism = 4;
                break;
        }

        return config;
    }

    private async Task<SortExecutionResult> SimulateAsync(string scenario, bool onDemand, IProgress<SortProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        _services.Processor.LoadLotDatsOnDemand = onDemand;
        return await _services.Processor.ExecuteAsync(Config(scenario), isDryRun: true, progress: progress, cancellationToken: cancellationToken);
    }

    private string Describe(SortExecutionResult result)
    {
        var lines = new List<string>();
        foreach (var item in result.Items)
        {
            foreach (var it in item.RelatedItems.Prepend(item))
            {
                lines.Add(string.Join("|",
                    it.DisplayName, it.DatName, string.Join(",", it.OtherDats), it.Status, _services.I18n.Format(it.StatusReason),
                    it.TargetPath, it.Is1G1RWinner, it.WinnerReference, it.Parsed.DatKey, it.DatGameName, it.Crc32, it.Sha1));
            }
        }

        var stats = result.Stats;
        lines.Add($"{stats.ValidCount}|{stats.ExcludedCount}|{stats.UnmatchedCount}|{stats.DuplicateCount}|{stats.DatTotalRoms}|{stats.DatMatchedCount}|{stats.DatNameMatchedCount}|{stats.DatMissingCount}|{stats.DatCompletionPct}");
        lines.AddRange(stats.PerDat!.Select(d => d.ToString()));
        lines.AddRange(result.MissingRoms.Select(r => $"missing {r.GameName}/{r.Name}"));
        return string.Join("\n", lines);
    }

    // =========================================================================
    // Same results
    // =========================================================================

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Sort_ShouldGiveTheSameResultsWithTheDatsReadOnDemand(string scenario)
    {
        var whole = Describe(await SimulateAsync(scenario, onDemand: false));
        var onDemand = Describe(await SimulateAsync(scenario, onDemand: true));

        onDemand.Should().Be(whole);
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Exports_ShouldBeTheSameWithTheDatsReadOnDemand(string scenario)
    {
        async Task<string> ExportAsync(bool onDemand)
        {
            var result = await SimulateAsync(scenario, onDemand);
            var folder = _tmp.CreateSubdirectory($"exports_{scenario}_{onDemand}");
            await _services.Processor.ExportFixDatAsync(result.MissingRoms, result.DatCatalog!, Path.Combine(folder, "fixdat"));
            await _services.Processor.ExportMissListToTxtAsync(result.MissingRoms, result.DatCatalog!, Path.Combine(folder, "miss.txt"));
            await _services.Processor.ExportMissListToCsvAsync(result.MissingRoms, result.DatCatalog!, Path.Combine(folder, "miss.csv"));
            await _services.Processor.ExportDatAsync(result.Items, Path.Combine(folder, "collection"), DatExportFormat.Auto, result.DatCatalog);
            await _services.Processor.ExportCatalogDatAsync(Config(scenario), Path.Combine(folder, "catalog"), DatExportFormat.Auto);
            return string.Join("\n", Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .Select(file => Path.GetRelativePath(folder, file) + "\n" + DropDates(File.ReadAllText(file))));
        }

        (await ExportAsync(onDemand: true)).Should().Be(await ExportAsync(onDemand: false));
    }

    // The DAT of the collection is dated by the day it is written
    private static string DropDates(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\d{4}-\d{2}-\d{2}", "date");

    // =========================================================================
    // DATs read
    // =========================================================================

    [Fact]
    public async Task Sort_ShouldOnlyReadAgainTheDatsAFileMayBeOneOfTheRomsOf()
    {
        var result = await SimulateAsync("default", onDemand: true);

        var lot = result.DatCatalog!;
        var untouched = lot.DatMemberFor(Untouched)!;
        untouched.IsLoaded.Should().BeFalse();
        untouched.LoadCount.Should().Be(0);
        foreach (var name in new[] { GameBoy, GameBoyColor, Amiga, PlayStation, FmTowns })
        {
            lot.DatMemberFor(name)!.LoadCount.Should().Be(1, $"{name} is read once again, whatever the files it knows");
        }

        // The DAT never read is counted from its summary
        result.Stats.PerDat!.Single(d => d.Name == Untouched).Should().Be(new DatLotStats(Untouched, NamingConvention.NoIntro, 1, 0, 0, 1, 0));
        result.MissingRoms.Should().NotContain(r => r.Name == "Combat (USA).a26");
        TiebreakApplicability.GetCloneRelations(lot).Should().Be(DatCloneRelations.None);
        untouched.IsLoaded.Should().BeFalse("its summary is enough to the statistics, the exports and the 1G1R");
    }

    [Fact]
    public async Task Chd_ShouldReadTheDatOfItsDisk_KnownBeforeAnyDatIsRead()
    {
        _services.Processor.LoadLotDatsOnDemand = true;
        foreach (var file in Directory.EnumerateFiles(_src).Where(f => Path.GetFileName(f) != "beast.chd"))
        {
            File.Delete(file);
        }

        var result = await _services.Processor.ExecuteAsync(Config("default"), isDryRun: true);

        var beast = result.Items.Single();
        beast.DatName.Should().Be(FmTowns);
        beast.Status.Should().Be(FileStatus.Valid);
        result.DatCatalog!.Dats!.Where(d => d.IsLoaded).Select(d => d.Name).Should().Equal(FmTowns);
    }

    [Fact]
    public async Task ChdByName_ShouldReadEveryDat_SinceAGameIsOnlyKnownByItsNameInItsDat()
    {
        var result = await SimulateAsync("chd_by_name", onDemand: true);

        result.Items.Single(it => it.DisplayName == "Night Quest (USA).chd").Should().Match<ProcessItem>(it => it.IsIdentifiedByName && it.DatName == PlayStation);
        result.DatCatalog!.Dats!.Should().OnlyContain(d => d.IsLoaded);
    }

    [Fact]
    public async Task Sort_ShouldStopWhileTheDatsAreRead()
    {
        using var cancellation = new CancellationTokenSource();
        var progress = new SynchronousProgress<SortProgress>(p =>
        {
            if (p.Message.Key == "progress.loading_lot_dats")
            {
                cancellation.Cancel();
            }
        });

        await FluentActions.Invoking(() => SimulateAsync("default", onDemand: true, progress, cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CatalogDat_ShouldForgetEachDatOnceItsFilteredDatIsWritten()
    {
        _services.Processor.LoadLotDatsOnDemand = true;
        var output = _tmp.GetFilePath("catalog");
        var logs = new List<LogMessage>();

        var result = await _services.Processor.ExportCatalogDatAsync(
            Config("default"), output, DatExportFormat.Auto, logProgress: new SynchronousProgress<LogMessage>(logs.Add));

        result.Catalog.Dats!.Should().OnlyContain(d => !d.IsLoaded && d.LoadCount == 1);
        result.KeptCount.Should().Be(12, "every game but the crack: 3 + 3 + 1 + 3 + 1 + 1");
        result.FilteredDat.Dats!.Should().OnlyContain(d => !d.IsLoaded, "the DATs written are read again from their files when asked");
        result.FilteredDat.CountFiles().Should().Be(15, "a disc of the PlayStation is two files");
        Directory.EnumerateFiles(output).Should().HaveCount(6).And.NotContain(f => f.EndsWith(".tmp", StringComparison.Ordinal));
        logs.Should().Contain(l => l.Message.Key == "log.catalog_selection_summary");
    }

    [Fact]
    public async Task CatalogDat_StoppedBeforeItsLastDat_ShouldWriteNothing()
    {
        _services.Processor.LoadLotDatsOnDemand = true;
        var output = _tmp.GetFilePath("catalog_stopped");
        using var cancellation = new CancellationTokenSource();
        var written = 0;
        var progress = new SynchronousProgress<SortProgress>(p =>
        {
            if (p.Message.Key == "progress.writing_dat" && ++written == 3)
            {
                cancellation.Cancel();
            }
        });

        await FluentActions.Invoking(() => _services.Processor.ExportCatalogDatAsync(Config("default"), output, DatExportFormat.Auto, progress, cancellationToken: cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        (Directory.Exists(output) ? Directory.EnumerateFiles(output) : []).Should().BeEmpty();
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
