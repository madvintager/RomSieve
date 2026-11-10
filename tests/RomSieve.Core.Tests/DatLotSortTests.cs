using System.IO.Hashing;
using System.Text;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// A sort with a lot of DATs: each file is sorted with the first DAT of the lot that knows it (a descriptor with its
/// tracks), each DAT keeping its convention, its filters, its 1G1R and its folder in the destination; the ROMs of the
/// other DATs that know a file are present too; a file no DAT knows is unmatched.
/// </summary>
public sealed class DatLotSortTests : IDisposable
{
    private const string GameBoy = "Nintendo - Game Boy";
    private const string GameBoyColor = "Nintendo - Game Boy Color";
    private const string Amiga = "Commodore Amiga - Games - [ADF]";
    private const string PlayStation = "Sony - PlayStation";

    private const string GameBoyFile = "Nintendo - Game Boy (20230414-173400).dat";
    private const string GameBoyColorFile = "Nintendo - Game Boy Color (20230501-101010).dat";
    private static readonly string AmigaFile = Path.Combine("TOSEC", "Commodore", "Amiga", "Commodore Amiga - Games - [ADF] (TOSEC-v2025-01-30_CM).dat");

    private static readonly byte[] TetrisGbWorld = Encoding.ASCII.GetBytes("TETRIS GB WORLD");
    private static readonly byte[] TetrisGbJapan = Encoding.ASCII.GetBytes("TETRIS GB JAPAN");
    private static readonly byte[] TetrisGbcWorld = Encoding.ASCII.GetBytes("TETRIS GBC WORLD");
    private static readonly byte[] TetrisGbcJapan = Encoding.ASCII.GetBytes("TETRIS GBC JAPAN");
    private static readonly byte[] SharedBios = Encoding.ASCII.GetBytes("SHARED BIOS");
    private static readonly byte[] Lemmings = Encoding.ASCII.GetBytes("LEMMINGS DISK");
    private static readonly byte[] LemmingsCrack = Encoding.ASCII.GetBytes("LEMMINGS CRACKED");

    private readonly TempDirectoryFixture _tmp = new();
    private readonly CoreTestServices _services = new();
    private readonly string _dats;
    private readonly string _src;
    private readonly string _dst;

    public DatLotSortTests()
    {
        _dats = _tmp.CreateSubdirectory("dats");
        _src = _tmp.CreateSubdirectory("src");
        _dst = _tmp.GetFilePath("dst");
    }

    public void Dispose() => _tmp.Dispose();

    private static string Crc(byte[] data) => Crc32.HashToUInt32(data).ToString("x8");

    private static string Rom(string name, byte[] data, string? sha1 = null) =>
        $"""<rom name="{name}" size="{data.Length}" crc="{Crc(data)}"{(sha1 is null ? string.Empty : $" sha1=\"{sha1}\"")}/>""";

    // A Logiqx DAT: its header (name, homepage), then its games (name, clone of, ROMs)
    private void WriteDat(string relativePath, string name, string homepage, params (string Game, string? CloneOf, string Roms)[] games) =>
        _tmp.WriteText(Path.Combine("dats", relativePath), $"""
            <?xml version="1.0"?>
            <datafile>
            	<header><name>{name}</name><description>{name}</description><homepage>{homepage}</homepage></header>
            {string.Concat(games.Select(g => $"""
            	<game name="{g.Game}"{(g.CloneOf is null ? string.Empty : $" cloneof=\"{g.CloneOf}\"")}><description>{g.Game}</description>{g.Roms}</game>

            """))}
            </datafile>
            """);

    private void WriteGameBoy(string relativePath = GameBoyFile) =>
        WriteDat(relativePath, GameBoy, "No-Intro",
            ("Tetris (World) (Rev 1)", null, Rom("Tetris (World) (Rev 1).gb", TetrisGbWorld)),
            ("Tetris (Japan)", "Tetris (World) (Rev 1)", Rom("Tetris (Japan).gb", TetrisGbJapan)),
            ("[BIOS] Shared (World)", null, Rom("[BIOS] Shared (World).bin", SharedBios)));

    private void WriteGameBoyColor(string relativePath = GameBoyColorFile) =>
        WriteDat(relativePath, GameBoyColor, "No-Intro",
            ("Tetris (World)", null, Rom("Tetris (World).gbc", TetrisGbcWorld)),
            ("Tetris (Japan)", null, Rom("Tetris (Japan).gbc", TetrisGbcJapan)),
            ("[BIOS] Shared (World)", null, Rom("[BIOS] Shared (World).bin", SharedBios)));

    private void WriteAmiga(string? relativePath = null) =>
        WriteDat(relativePath ?? AmigaFile, Amiga, "TOSEC",
            ("Lemmings (1991)(Psygnosis)", null, Rom("Lemmings (1991)(Psygnosis).adf", Lemmings)),
            ("Lemmings (1991)(Psygnosis)[cr PDX]", null, Rom("Lemmings (1991)(Psygnosis)[cr PDX].adf", LemmingsCrack)));

    private string WriteSource(string name, byte[] data) => _tmp.WriteBytes(Path.Combine("src", name), data);

    private void WriteTetrisSources()
    {
        WriteSource("tetris_gb_world.gb", TetrisGbWorld);
        WriteSource("tetris_gb_japan.gb", TetrisGbJapan);
        WriteSource("tetris_gbc_world.gbc", TetrisGbcWorld);
        WriteSource("tetris_gbc_japan.gbc", TetrisGbcJapan);
    }

    private SortConfig Config(Action<SortConfig>? configure = null)
    {
        var config = new SortConfig
        {
            SourceDir = _src,
            DestinationDir = _dst,
            DatFile = _dats,
            DatRecursive = true,
            Pattern = "{title}"
        };
        configure?.Invoke(config);
        return config;
    }

    private Task<SortExecutionResult> SimulateAsync(SortConfig config, List<LogMessage>? logs = null) =>
        _services.Processor.ExecuteAsync(
            config,
            isDryRun: true,
            logProgress: logs is null ? null : new SynchronousProgress<LogMessage>(logs.Add));

    private static ProcessItem ItemOf(SortExecutionResult result, string sourceName) =>
        result.Items.Single(it => Path.GetFileName(it.SourcePath) == sourceName);

    private string Relative(string? path) => Path.GetRelativePath(_dst, path!).Replace('\\', '/');

    // =========================================================================
    // Identification
    // =========================================================================

    [Fact]
    public async Task SameTitleInTwoDats_ShouldBeTwoGamesWithAWinnerEach()
    {
        WriteGameBoy();
        WriteGameBoyColor();
        WriteTetrisSources();

        var result = await SimulateAsync(Config(c =>
        {
            c.Filters.OneGameOneRom = true;
            c.Filters.PriorityLanguages = ["ja"];
        }));

        result.Items.Where(it => it.Status == FileStatus.Valid).Select(it => it.DisplayName)
            .Should().BeEquivalentTo("tetris_gb_japan.gb", "tetris_gbc_japan.gbc");
        result.Items.Where(it => it.Status == FileStatus.Excluded).Select(it => it.DisplayName)
            .Should().BeEquivalentTo("tetris_gb_world.gb", "tetris_gbc_world.gbc");
        ItemOf(result, "tetris_gb_japan.gb").DatName.Should().Be(GameBoy);
        ItemOf(result, "tetris_gbc_japan.gbc").DatName.Should().Be(GameBoyColor);
        ItemOf(result, "tetris_gb_world.gb").WinnerReference.Should().Be("tetris_gb_japan.gb");
        ItemOf(result, "tetris_gbc_world.gbc").WinnerReference.Should().Be("tetris_gbc_japan.gbc");

        // Each item says its DAT in its game key
        _services.FilterService.SerializeCanonicalGameKey(ItemOf(result, "tetris_gb_japan.gb").Parsed)
            .Should().NotBe(_services.FilterService.SerializeCanonicalGameKey(ItemOf(result, "tetris_gbc_japan.gbc").Parsed));
    }

    [Fact]
    public async Task FileKnownBySeveralDats_ShouldBeSortedWithTheFirstOneAndPresentInEach()
    {
        WriteGameBoyColor();
        WriteGameBoy();
        WriteSource("bios.bin", SharedBios);
        var logs = new List<LogMessage>();

        var result = await SimulateAsync(Config(), logs);

        var bios = ItemOf(result, "bios.bin");
        bios.DatName.Should().Be(GameBoy, "the DATs are taken in the order of their paths");
        bios.OtherDats.Should().Equal(GameBoyColor);
        bios.Status.Should().Be(FileStatus.Valid);
        bios.Parsed.DatKey.Should().Be(GameBoy);
        logs.Should().Contain(l => l.Message.Key == "log.dat_lot_other_dats"
            && _services.I18n.Format(l.Message).Contains(GameBoyColor, StringComparison.Ordinal));

        // The ROM is present in both DATs, the file written once
        var perDat = result.Stats.PerDat!.ToDictionary(d => d.Name);
        perDat[GameBoy].MatchedCount.Should().Be(1);
        perDat[GameBoyColor].MatchedCount.Should().Be(1);
        perDat[GameBoy].GamesFound.Should().Be(1);
        perDat[GameBoyColor].GamesFound.Should().Be(1);
        result.Stats.DatTotalRoms.Should().Be(6);
        result.Stats.DatMatchedCount.Should().Be(2);
        result.Stats.DatMissingCount.Should().Be(4);
        result.MissingRoms.Select(r => r.Name).Should().NotContain("[BIOS] Shared (World).bin");
        Relative(bios.TargetPath).Should().Be($"{GameBoy}/[BIOS] Shared/[BIOS] Shared (World).bin");
    }

    [Fact]
    public async Task Attachment_ShouldNotDependOnTheParallelismNorOnTheRun()
    {
        WriteGameBoy();
        WriteGameBoyColor();
        WriteAmiga();
        WriteTetrisSources();
        WriteSource("bios.bin", SharedBios);
        WriteSource("lemmings.adf", Lemmings);
        WriteSource("unknown.bin", Encoding.ASCII.GetBytes("NOTHING"));

        static string Describe(SortExecutionResult result) => string.Join("\n", result.Items.Select(it =>
            $"{it.DisplayName}|{it.DatName}|{string.Join(",", it.OtherDats)}|{it.Status}|{it.TargetPath}"));

        var sequential = Describe(await SimulateAsync(Config(c => c.HashingParallelism = 1)));
        var parallel = Describe(await SimulateAsync(Config(c => c.HashingParallelism = 4)));
        var again = Describe(await SimulateAsync(Config(c => c.HashingParallelism = 1)));

        parallel.Should().Be(sequential);
        again.Should().Be(sequential);
    }

    [Fact]
    public async Task FileNoDatKnows_ShouldBeUnmatched_AndHashedOnlyForASizeADatKnows()
    {
        WriteGameBoy();
        WriteAmiga();
        WriteSource("tetris_gb_world.gb", TetrisGbWorld);

        // Same size as a ROM of the lot, other data: hashed, not known; a size no DAT knows: not even hashed
        WriteSource("same_size.bin", Encoding.ASCII.GetBytes("TETRIS GB WORLX"));
        WriteSource("other_size.bin", Encoding.ASCII.GetBytes("A FILE OF ANOTHER SIZE ENTIRELY"));

        var result = await SimulateAsync(Config());

        ItemOf(result, "tetris_gb_world.gb").Status.Should().Be(FileStatus.Valid);
        foreach (var name in new[] { "same_size.bin", "other_size.bin" })
        {
            var item = ItemOf(result, name);
            item.Status.Should().Be(FileStatus.Unmatched);
            item.StatusReason!.Key.Should().Be("reason.unmatched_not_in_dat");
            item.DatName.Should().BeNull();
            item.OtherDats.Should().BeEmpty();
            Relative(item.TargetPath).Should().Be($"{PathOrganizerService.UnmatchedFolderName}/{name}", "_unmatched stays at the root");
        }

        ItemOf(result, "same_size.bin").Crc32.Should().NotBeEmpty();
        ItemOf(result, "other_size.bin").Crc32.Should().BeEmpty("no DAT of the lot has a ROM of this size");
    }

    [Fact]
    public async Task MixedLot_ShouldSortEachFileWithTheConventionAndTheFiltersOfItsDat()
    {
        WriteGameBoy();
        WriteAmiga();
        WriteSource("tetris_gb_world.gb", TetrisGbWorld);
        WriteSource("lemmings.adf", Lemmings);
        WriteSource("lemmings_crack.adf", LemmingsCrack);

        var result = await SimulateAsync(Config(c =>
        {
            c.Filters.ExcludeModifications = true;
            c.Filters.NoIntroExcludeDevVersions = true;
        }));

        var tetris = ItemOf(result, "tetris_gb_world.gb");
        tetris.Parsed.Convention.Should().Be(NamingConvention.NoIntro);
        tetris.Status.Should().Be(FileStatus.Valid);

        var lemmings = ItemOf(result, "lemmings.adf");
        lemmings.Parsed.Convention.Should().Be(NamingConvention.Tosec);
        lemmings.Parsed.Publisher.Should().Be("Psygnosis");
        lemmings.Status.Should().Be(FileStatus.Valid);

        var crack = ItemOf(result, "lemmings_crack.adf");
        crack.Status.Should().Be(FileStatus.Excluded, "the TOSEC filters exclude the cracks");
        crack.DatName.Should().Be(Amiga);

        result.Stats.PerDat!.Select(d => (d.Name, d.Convention))
            .Should().Equal((GameBoy, NamingConvention.NoIntro), (Amiga, NamingConvention.Tosec));
    }

    [Fact]
    public async Task ForcedConvention_ShouldOnlyApplyToTheDatsNotRecognized_AndSaySo()
    {
        WriteGameBoy();
        WriteAmiga();
        WriteDat("Unknown Machine.dat", "Unknown Machine", "somewhere",
            ("Lemmings", null, Rom("Lemmings.img", Encoding.ASCII.GetBytes("UNKNOWN DISK 1"))));
        WriteSource("tetris_gb_world.gb", TetrisGbWorld);
        WriteSource("lemmings.adf", Lemmings);
        WriteSource("unknown.img", Encoding.ASCII.GetBytes("UNKNOWN DISK 1"));
        var logs = new List<LogMessage>();

        var result = await SimulateAsync(Config(c => c.NamingConvention = NamingConvention.NoIntro), logs);

        ItemOf(result, "tetris_gb_world.gb").Parsed.Convention.Should().Be(NamingConvention.NoIntro);
        ItemOf(result, "lemmings.adf").Parsed.Convention.Should().Be(NamingConvention.Tosec, "the convention of a DAT wins");
        ItemOf(result, "unknown.img").Parsed.Convention.Should().Be(NamingConvention.NoIntro, "the chosen convention applies to a DAT not recognized");
        var warning = logs.Single(l => l.Message.Key == "log.dat_lot_convention_overridden");
        warning.Level.Should().Be(LogLevel.Warn);
        _services.I18n.Format(warning.Message).Should().Contain(Amiga).And.NotContain(GameBoy).And.NotContain("Unknown Machine");
    }

    [Fact]
    public async Task ExcludedDat_ShouldNotSortItsFiles()
    {
        WriteGameBoy();
        WriteGameBoyColor();
        WriteSource("tetris_gbc_world.gbc", TetrisGbcWorld);
        WriteSource("bios.bin", SharedBios);

        var result = await SimulateAsync(Config(c => c.ExcludedDats = [GameBoy]));

        ItemOf(result, "tetris_gbc_world.gbc").DatName.Should().Be(GameBoyColor);
        ItemOf(result, "bios.bin").DatName.Should().Be(GameBoyColor, "the DAT left out is not in the lot");
        result.Stats.PerDat!.Select(d => d.Name).Should().Equal(GameBoyColor);
    }

    [Fact]
    public async Task LotOfASingleDat_ShouldKeepTheGameKeysOfASingleDat()
    {
        WriteGameBoy();
        WriteSource("tetris_gb_world.gb", TetrisGbWorld);

        var result = await SimulateAsync(Config());

        var tetris = ItemOf(result, "tetris_gb_world.gb");
        tetris.DatName.Should().Be(GameBoy);
        tetris.Parsed.DatKey.Should().BeNull("a lot of one DAT has no other DAT to tell apart");
        Relative(tetris.TargetPath).Should().Be($"{GameBoy}/Tetris/Tetris (World) (Rev 1).gb");
    }

    [Fact]
    public async Task Chd_ShouldBeSortedWithTheDatThatListsItsDisk()
    {
        const string fmTowns = "Fujitsu FM Towns - Games - [CD]";
        WriteGameBoy();
        _tmp.WriteText(Path.Combine("dats", "Fujitsu FM Towns - Games - [CD] (TOSEC-v2025).dat"), $"""
            <?xml version="1.0"?>
            <datafile>
            	<header><name>{fmTowns}</name><homepage>TOSEC</homepage></header>
            	<game name="Shadow of the Beast (1990)(Psygnosis)"><description>Shadow of the Beast (1990)(Psygnosis)</description><disk name="Shadow of the Beast (1990)(Psygnosis)" sha1="{ChdTestFile.Sha1Of("beast")}"/></game>
            </datafile>
            """);
        WriteSource("beast.chd", ChdTestFile.Create("beast"));
        WriteSource("other.chd", ChdTestFile.Create("other"));

        var result = await SimulateAsync(Config());

        var beast = ItemOf(result, "beast.chd");
        beast.DatName.Should().Be(fmTowns);
        beast.Status.Should().Be(FileStatus.Valid);
        Relative(beast.TargetPath).Should().Be($"{fmTowns}/Shadow of the Beast/Shadow of the Beast (1990)(Psygnosis).chd");
        ItemOf(result, "other.chd").Status.Should().Be(FileStatus.Unmatched);
        result.Stats.PerDat!.Single(d => d.Name == fmTowns).MatchedCount.Should().Be(1);
    }

    [Fact]
    public async Task CloneRelations_OfALot_ShouldBeThoseOfItsDats()
    {
        WriteGameBoyColor();
        var withoutClones = await _services.DatService.ParseDatFileAsync(_dats, new DatFolderOptions());
        WriteGameBoy();
        var withClones = await _services.DatService.ParseDatFileAsync(_dats, new DatFolderOptions());

        TiebreakApplicability.GetCloneRelations(withoutClones).Should().Be(DatCloneRelations.None);
        TiebreakApplicability.GetCloneRelations(withClones).Should().Be(DatCloneRelations.Present, "Tetris (Japan) of the Game Boy is a clone");
    }

    // =========================================================================
    // Release selection, recalculation, partial sort
    // =========================================================================

    [Fact]
    public async Task Recalculation_ShouldReproduceTheSimulationOfEachGameOfEachDat()
    {
        WriteGameBoy();
        WriteGameBoyColor();
        WriteTetrisSources();
        WriteSource("bios.bin", SharedBios);
        var config = Config(c =>
        {
            c.Filters.OneGameOneRom = true;
            c.Filters.PriorityLanguages = ["ja"];
        });

        var result = await SimulateAsync(config);
        var expected = result.Items.ToDictionary(it => it.DisplayName, it => (it.Status, it.StatusReason, it.Is1G1RWinner, it.WinnerReference));

        foreach (var game in result.Items.GroupBy(it => _services.FilterService.SerializeCanonicalGameKey(it.Parsed)))
        {
            var gameItems = game.ToList();
            _services.Processor.RecalculateGameItems(gameItems, config, result.DatCatalog, result.Items.ToList());

            gameItems.Select(it => (it.Status, it.StatusReason, it.Is1G1RWinner, it.WinnerReference))
                .Should().Equal(gameItems.Select(it => expected[it.DisplayName]), $"game {game.Key} must not change");
        }

        // Both DATs together, as the table recalculates a selection of rows
        var all = result.Items.ToList();
        _services.Processor.RecalculateGameItems(all, config, result.DatCatalog, all);
        all.Select(it => (it.Status, it.StatusReason, it.Is1G1RWinner, it.WinnerReference))
            .Should().Equal(all.Select(it => expected[it.DisplayName]));
    }

    [Fact]
    public async Task Recalculation_ShouldLeaveAFileNoDatKnowsNonConforming()
    {
        // A conforming name, the title of a game of the lot, but a CRC32 no DAT of the lot knows
        WriteGameBoy();
        WriteGameBoyColor();
        WriteTetrisSources();
        WriteSource("Tetris (World) (Aftermarket) (Unl).gb", Encoding.ASCII.GetBytes("TETRIS AFTERMARKET"));
        var config = Config(c => c.Filters.OneGameOneRom = true);

        var result = await SimulateAsync(config);
        var unknown = ItemOf(result, "Tetris (World) (Aftermarket) (Unl).gb");
        unknown.Parsed.IsConform.Should().BeTrue();
        unknown.DatName.Should().BeNull();
        unknown.IdentificationFailure.Should().Be(IdentificationFailure.NotInDat);
        var expected = result.Items.ToDictionary(it => it.DisplayName, it => (it.Status, it.StatusReason, it.Is1G1RWinner));

        var all = result.Items.ToList();
        _services.Processor.RecalculateGameItems(all, config, result.DatCatalog, all);

        unknown.Status.Should().Be(FileStatus.Unmatched);
        unknown.StatusReason!.Key.Should().Be("reason.unmatched_not_in_dat");
        all.Select(it => (it.Status, it.StatusReason, it.Is1G1RWinner)).Should().Equal(all.Select(it => expected[it.DisplayName]));
    }

    [Fact]
    public async Task FileNoDatKnows_ShouldBeFilteredByTheFiltersOfTheConventionOfItsName()
    {
        // A lot of a No-Intro DAT and a TOSEC DAT: each file no DAT knows meets the filters of its own convention
        WriteGameBoy();
        WriteAmiga();
        WriteSource("Tetris (World) (Demo).gb", Encoding.ASCII.GetBytes("TETRIS DEMO"));
        WriteSource("Tetris (World) (Rev 2).gb", Encoding.ASCII.GetBytes("TETRIS REV 2"));
        WriteSource("Lemmings (1991)(Psygnosis)[cr].adf", Encoding.ASCII.GetBytes("LEMMINGS CR"));
        WriteSource("Lemmings (1991)(Psygnosis)[a].adf", Encoding.ASCII.GetBytes("LEMMINGS ALT"));
        var config = Config(c => c.Filters.NoIntroExcludeDemos = true);

        var result = await SimulateAsync(config);

        foreach (var name in new[] { "Tetris (World) (Demo).gb", "Lemmings (1991)(Psygnosis)[cr].adf" })
        {
            var item = ItemOf(result, name);
            item.DatName.Should().BeNull();
            item.Status.Should().Be(FileStatus.Excluded, name);
            item.StatusReason!.Key.Should().Be("reason.not_in_dat_excluded");
        }

        ItemOf(result, "Tetris (World) (Demo).gb").StatusReason!.Args["reason"].Should().BeOfType<LocalizedMessage>()
            .Which.Key.Should().Be("reason.demo_excluded");
        foreach (var name in new[] { "Tetris (World) (Rev 2).gb", "Lemmings (1991)(Psygnosis)[a].adf" })
        {
            ItemOf(result, name).Status.Should().Be(FileStatus.Unmatched, $"{name}: the filters keep it");
        }

        // Recalculated with the other files, as the table does: nothing changes
        var expected = result.Items.ToDictionary(it => it.DisplayName, it => (it.Status, it.StatusReason));
        var all = result.Items.ToList();
        _services.Processor.RecalculateGameItems(all, config, result.DatCatalog, all);
        all.Select(it => (it.Status, it.StatusReason)).Should().Equal(all.Select(it => expected[it.DisplayName]));
    }

    [Fact]
    public async Task ManualWinner_ShouldOnlyDecideTheGameOfItsDat()
    {
        WriteGameBoy();
        WriteGameBoyColor();
        WriteTetrisSources();
        var config = Config(c =>
        {
            c.Filters.OneGameOneRom = true;
            c.Filters.PriorityLanguages = ["ja"];
        });
        var simulation = await SimulateAsync(config);
        var gbWorld = ItemOf(simulation, "tetris_gb_world.gb");
        config.Filters.Manual1G1RWinners[_services.FilterService.SerializeCanonicalGameKey(gbWorld.Parsed)] = gbWorld.DisplayName;

        var result = await SimulateAsync(config);

        ItemOf(result, "tetris_gb_world.gb").Status.Should().Be(FileStatus.Valid);
        ItemOf(result, "tetris_gb_japan.gb").Status.Should().Be(FileStatus.Excluded);
        ItemOf(result, "tetris_gbc_japan.gbc").Status.Should().Be(FileStatus.Valid, "the game of the other DAT keeps its winner");
        ItemOf(result, "tetris_gbc_world.gbc").Status.Should().Be(FileStatus.Excluded);
    }

    [Fact]
    public async Task PartialSort_ShouldOnlyWriteTheGameChosenInItsDat()
    {
        WriteGameBoy();
        WriteGameBoyColor();
        WriteTetrisSources();
        var config = Config(c =>
        {
            c.Filters.OneGameOneRom = true;
            c.Filters.PriorityLanguages = ["ja"];
        });
        var simulation = await SimulateAsync(config);
        var chosen = simulation.Items.Where(it => it.DatName == GameBoyColor && it.Parsed.Title == "Tetris").ToList();
        var selection = SelectionFilter.Create(simulation.Items, chosen, _services.FilterService);

        var result = await _services.Processor.ExecuteAsync(Config(c =>
        {
            c.Filters.OneGameOneRom = true;
            c.Filters.PriorityLanguages = ["ja"];
        }), isDryRun: false, selection: selection);

        ItemOf(result, "tetris_gbc_japan.gbc").Status.Should().Be(FileStatus.Valid);
        ItemOf(result, "tetris_gb_japan.gb").IsOutsideSelection.Should().BeTrue("the same title of the other DAT is another game");
        Directory.EnumerateFiles(_dst, "*", SearchOption.AllDirectories)
            .Where(f => !DestinationGuard.IsGuardPath(f))
            .Select(Relative)
            .Should().Equal($"{GameBoyColor}/Tetris/Tetris (Japan).gbc");
    }

    // =========================================================================
    // Multi-disk sets, descriptors and playlists
    // =========================================================================

    private static string Cue(string bin) => $"FILE \"{bin}\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00\r\n";

    [Fact]
    public async Task RedumpMultiDiscSetOfALot_ShouldStayWithItsTracksAndGetItsPlaylistInTheFolderOfItsDat()
    {
        WriteGameBoy();
        var cue1 = Encoding.ASCII.GetBytes(Cue("Final Quest (USA) (Disc 1).bin"));
        var cue2 = Encoding.ASCII.GetBytes(Cue("Final Quest (USA) (Disc 2).bin"));
        var bin1 = Encoding.ASCII.GetBytes("FINAL QUEST DISC 1 DATA");
        var bin2 = Encoding.ASCII.GetBytes("FINAL QUEST DISC 2 DATA");
        WriteDat(Path.Combine("Redump", "Sony - PlayStation - Datfile (2) (2025-01-01).dat"), PlayStation, "redump.org",
            ("Final Quest (USA) (Disc 1)", null, Rom("Final Quest (USA) (Disc 1).cue", cue1) + Rom("Final Quest (USA) (Disc 1).bin", bin1)),
            ("Final Quest (USA) (Disc 2)", null, Rom("Final Quest (USA) (Disc 2).cue", cue2) + Rom("Final Quest (USA) (Disc 2).bin", bin2)));
        WriteSource("Final Quest (USA) (Disc 1).cue", cue1);
        WriteSource("Final Quest (USA) (Disc 1).bin", bin1);
        WriteSource("Final Quest (USA) (Disc 2).cue", cue2);
        WriteSource("Final Quest (USA) (Disc 2).bin", bin2);
        var config = Config(c =>
        {
            c.Pattern = string.Empty;
            c.GenerateM3u = true;
            c.M3uPlacement = M3uPlacementMode.Parent;
            c.Filters.NoIntroCompleteMultimediaSetsOnly = true;
        });

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        var discs = result.Items.Where(it => it.DatName == PlayStation).ToList();
        discs.Should().HaveCount(2).And.OnlyContain(it => it.Status == FileStatus.Valid && it.RelatedItems.Count == 1);
        discs.Should().OnlyContain(it => it.RelatedItems[0].DatName == PlayStation);
        var playlist = Path.Combine(_dst, PlayStation, "Final Quest (USA).m3u");
        File.Exists(playlist).Should().BeTrue();
        File.ReadAllLines(playlist).Where(l => l.Length > 0).Should().Equal(
            "Final Quest/Final Quest (USA) (Disc 1).cue",
            "Final Quest/Final Quest (USA) (Disc 2).cue");
        File.Exists(Path.Combine(_dst, PlayStation, "Final Quest", "Final Quest (USA) (Disc 2).bin")).Should().BeTrue();
    }

    [Fact]
    public async Task RedumpSetOfALotMissingItsLastDisc_ShouldBeIncomplete_AfterTheDisksOfItsDat()
    {
        WriteGameBoy();
        var cue1 = Encoding.ASCII.GetBytes(Cue("Final Quest (USA) (Disc 1).bin"));
        var cue2 = Encoding.ASCII.GetBytes(Cue("Final Quest (USA) (Disc 2).bin"));
        var bin1 = Encoding.ASCII.GetBytes("FINAL QUEST DISC 1 DATA");
        var bin2 = Encoding.ASCII.GetBytes("FINAL QUEST DISC 2 DATA");
        WriteDat(Path.Combine("Redump", "Sony - PlayStation - Datfile (2) (2025-01-01).dat"), PlayStation, "redump.org",
            ("Final Quest (USA) (Disc 1)", null, Rom("Final Quest (USA) (Disc 1).cue", cue1) + Rom("Final Quest (USA) (Disc 1).bin", bin1)),
            ("Final Quest (USA) (Disc 2)", null, Rom("Final Quest (USA) (Disc 2).cue", cue2) + Rom("Final Quest (USA) (Disc 2).bin", bin2)));
        WriteSource("Final Quest (USA) (Disc 1).cue", cue1);
        WriteSource("Final Quest (USA) (Disc 1).bin", bin1);
        WriteTetrisSources();

        var result = await SimulateAsync(Config(c => c.Filters.NoIntroCompleteMultimediaSetsOnly = true));

        var disc1 = ItemOf(result, "Final Quest (USA) (Disc 1).cue");
        disc1.Status.Should().Be(FileStatus.Excluded);
        disc1.StatusReason!.Key.Should().Be("reason.incomplete_multidisk_set");
        _services.I18n.Format(disc1.StatusReason).Should().Be("Set multi-disquettes incomplet (1/2 disques trouvés)");
        ItemOf(result, "tetris_gb_world.gb").Status.Should().Be(FileStatus.Valid);
    }

    // =========================================================================
    // Target paths
    // =========================================================================

    [Fact]
    public async Task FolderPerDat_ShouldGoAboveThePattern()
    {
        WriteGameBoy();
        WriteAmiga();
        WriteSource("tetris_gb_world.gb", TetrisGbWorld);
        WriteSource("lemmings.adf", Lemmings);

        var result = await SimulateAsync(Config(c => c.Pattern = "{title}/{dat}"));

        Relative(ItemOf(result, "tetris_gb_world.gb").TargetPath).Should().Be($"{GameBoy}/Tetris/{GameBoy}/Tetris (World) (Rev 1).gb");
        Relative(ItemOf(result, "lemmings.adf").TargetPath).Should().Be($"{Amiga}/Lemmings/{Amiga}/Lemmings (1991)(Psygnosis).adf");
    }

    [Fact]
    public async Task WithoutFolderPerDat_TheDatVariableShouldStillNameTheDat()
    {
        WriteGameBoy();
        WriteAmiga();
        WriteSource("tetris_gb_world.gb", TetrisGbWorld);
        WriteSource("lemmings.adf", Lemmings);

        var result = await SimulateAsync(Config(c =>
        {
            c.FolderPerDat = false;
            c.DatFolderMirror = true;
            c.Pattern = "{dat}/{title}";
        }));

        Relative(ItemOf(result, "tetris_gb_world.gb").TargetPath).Should().Be($"{GameBoy}/Tetris/Tetris (World) (Rev 1).gb");
        Relative(ItemOf(result, "lemmings.adf").TargetPath).Should().Be($"{Amiga}/Lemmings/Lemmings (1991)(Psygnosis).adf",
            "the mirror only acts with a folder per DAT");
    }

    [Fact]
    public async Task Mirror_ShouldPutTheFolderOfEachDatUnderTheFoldersOfItsPath()
    {
        WriteGameBoy();
        WriteAmiga();
        WriteSource("tetris_gb_world.gb", TetrisGbWorld);
        WriteSource("lemmings.adf", Lemmings);

        var result = await SimulateAsync(Config(c => c.DatFolderMirror = true));

        Relative(ItemOf(result, "lemmings.adf").TargetPath).Should().Be($"TOSEC/Commodore/Amiga/{Amiga}/Lemmings/Lemmings (1991)(Psygnosis).adf");
        Relative(ItemOf(result, "tetris_gb_world.gb").TargetPath).Should().Be($"{GameBoy}/Tetris/Tetris (World) (Rev 1).gb",
            "a DAT at the root of the folder has no folder to mirror");
    }

    [Fact]
    public async Task SingleDat_ShouldKeepItsTargets_AndNameItselfInTheDatVariable()
    {
        WriteGameBoy();
        WriteSource("tetris_gb_world.gb", TetrisGbWorld);

        var result = await SimulateAsync(Config(c =>
        {
            c.DatFile = Path.Combine(_dats, GameBoyFile);
            c.DatFolderMirror = true;
        }));
        var withDat = await SimulateAsync(Config(c =>
        {
            c.DatFile = Path.Combine(_dats, GameBoyFile);
            c.Pattern = "{dat}";
        }));

        var tetris = ItemOf(result, "tetris_gb_world.gb");
        tetris.DatName.Should().BeNull();
        tetris.Parsed.DatKey.Should().BeNull();
        Relative(tetris.TargetPath).Should().Be("Tetris/Tetris (World) (Rev 1).gb", "a single DAT never gets a folder of its own");
        result.Stats.PerDat.Should().BeNull();
        Relative(ItemOf(withDat, "tetris_gb_world.gb").TargetPath).Should().Be($"{GameBoy}/Tetris (World) (Rev 1).gb");
    }

    [Fact]
    public async Task SameTargetFromTwoDats_ShouldCollideAsAnyTwoFiles()
    {
        WriteDat("A.dat", "Collection A", "No-Intro", ("Game (World)", null, Rom("Game (World).bin", TetrisGbWorld)));
        WriteDat("B.dat", "Collection B", "No-Intro", ("Game (World)", null, Rom("Game (World).bin", TetrisGbcWorld)));
        WriteSource("a.bin", TetrisGbWorld);
        WriteSource("b.bin", TetrisGbcWorld);

        var result = await _services.Processor.ExecuteAsync(Config(c =>
        {
            c.FolderPerDat = false;
            c.Pattern = string.Empty;
        }), isDryRun: false);

        Relative(ItemOf(result, "a.bin").FinalTargetPath).Should().Be("Game (World).bin");
        Relative(ItemOf(result, "b.bin").FinalTargetPath).Should().Be("Game (World)_01.bin");
        File.ReadAllBytes(Path.Combine(_dst, "Game (World)_01.bin")).Should().Equal(TetrisGbcWorld);
    }

    [Fact]
    public async Task RealSortWithALot_ShouldBeUndoneWhole()
    {
        WriteGameBoy();
        WriteAmiga();
        WriteSource("tetris_gb_world.gb", TetrisGbWorld);
        WriteSource("lemmings.adf", Lemmings);
        WriteSource("unknown.bin", Encoding.ASCII.GetBytes("NOTHING"));
        var crcCache = new CrcCacheService(_tmp.GetFilePath(Path.Combine("cache", "crc_cache.json")));
        var journal = new OperationJournalService(_tmp.GetFilePath("journals"), _services.ChecksumService, crcCache);
        var processor = new SortProcessorService(
            _services.ChecksumService, _services.DatService, _services.NoIntroParser, _services.FilterService, _services.Organizer,
            _services.I18n, _services.OpticalCompanionService, _services.MameParser, crcCache, journal);

        var result = await processor.ExecuteAsync(Config(c => c.TransferMode = TransferMode.Move), isDryRun: false);

        File.Exists(Path.Combine(_dst, GameBoy, "Tetris", "Tetris (World) (Rev 1).gb")).Should().BeTrue();
        File.Exists(Path.Combine(_dst, Amiga, "Lemmings", "Lemmings (1991)(Psygnosis).adf")).Should().BeTrue();
        File.Exists(Path.Combine(_dst, PathOrganizerService.UnmatchedFolderName, "unknown.bin")).Should().BeTrue();
        Directory.EnumerateFiles(_src).Should().BeEmpty("the files were moved");

        var report = await journal.UndoAsync(result.JournalPath!);

        report.Issues.Should().BeEmpty();
        Directory.EnumerateFiles(_src).Select(Path.GetFileName).Should().BeEquivalentTo("tetris_gb_world.gb", "lemmings.adf", "unknown.bin");
        File.ReadAllBytes(Path.Combine(_src, "lemmings.adf")).Should().Equal(Lemmings);
        Directory.EnumerateFiles(_dst, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task LongTargetPath_ShouldBeWritten()
    {
        // A DAT path and a pattern that make a target of more than 260 characters
        var longFolder = Path.Combine(new string('a', 60), new string('b', 60), new string('c', 60));
        WriteGameBoy(Path.Combine(longFolder, GameBoyFile));
        WriteSource("tetris_gb_world.gb", TetrisGbWorld);
        var config = Config(c =>
        {
            c.DatFolderMirror = true;
            c.Pattern = "{title} - " + new string('d', 40) + "/{title} - " + new string('e', 40);
        });

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        var tetris = ItemOf(result, "tetris_gb_world.gb");
        tetris.Status.Should().Be(FileStatus.Valid, tetris.ErrorMessage);
        tetris.FinalTargetPath!.Length.Should().BeGreaterThan(260);
        File.ReadAllBytes(_services.Organizer.FormatWindowsExtendedPath(tetris.FinalTargetPath)).Should().Equal(TetrisGbWorld);
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
