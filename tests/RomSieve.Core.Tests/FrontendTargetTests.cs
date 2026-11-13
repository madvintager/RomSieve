using System.IO.Hashing;
using System.Text;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// The targets of a sort for a frontend (<see cref="SortConfig.Frontend"/>): the folder of the platform of each DAT
/// above the pattern, for a single DAT as for a lot (in place of the folder of each DAT), in the case of an existing
/// folder of the destination; a DAT the table does not know keeps its name, with a warning; an Amiga release goes to the
/// folder of its machine; an archive the frontend does not read is reported once per DAT. With it, the playlists read as
/// a game (<see cref="M3uPlacementMode.GameFolder"/>) and the folder per disc game (<see cref="SortConfig.FolderPerDiscGame"/>).
/// </summary>
public sealed class FrontendTargetTests : IDisposable
{
    private const string GameBoy = "Nintendo - Game Boy";
    private const string GameBoyColor = "Nintendo - Game Boy Color";
    private const string Amiga = "Commodore Amiga - Games - [ADF]";
    private const string PlayStation = "Sony - PlayStation";

    private static readonly byte[] Tetris = Encoding.ASCII.GetBytes("TETRIS GB WORLD");
    private static readonly byte[] TetrisColor = Encoding.ASCII.GetBytes("TETRIS GBC WORLD");
    private static readonly byte[] Lemmings = Encoding.ASCII.GetBytes("LEMMINGS DISK");
    private static readonly byte[] Banshee = Encoding.ASCII.GetBytes("BANSHEE AGA DISK");
    private static readonly byte[] Disc1 = Encoding.ASCII.GetBytes("ODYSSEY DISC ONE");
    private static readonly byte[] Disc2 = Encoding.ASCII.GetBytes("ODYSSEY DISC TWO");
    private static readonly byte[] Single = Encoding.ASCII.GetBytes("SINGLE DISC IMAGE");
    private static readonly byte[] Track = Encoding.ASCII.GetBytes("CUE GAME TRACK ONE");
    private static readonly byte[] Cue = Encoding.ASCII.GetBytes("FILE \"Cue Game (USA).bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00\r\n");

    private readonly TempDirectoryFixture _tmp = new();
    private readonly CoreTestServices _services = new();
    private readonly string _dats;
    private readonly string _src;
    private readonly string _dst;

    public FrontendTargetTests()
    {
        _dats = _tmp.CreateSubdirectory("dats");
        _src = _tmp.CreateSubdirectory("src");
        _dst = _tmp.GetFilePath("dst");
    }

    public void Dispose() => _tmp.Dispose();

    private static string Rom(string name, byte[] data) =>
        $"""<rom name="{name}" size="{data.Length}" crc="{Crc32.HashToUInt32(data):x8}"/>""";

    private string WriteDat(string fileName, string name, params (string Game, string Roms)[] games) =>
        _tmp.WriteText(Path.Combine("dats", fileName), $"""
            <?xml version="1.0"?>
            <datafile>
            	<header><name>{name}</name><description>{name}</description></header>
            {string.Concat(games.Select(g => $"""
            	<game name="{g.Game}"><description>{g.Game}</description>{g.Roms}</game>

            """))}
            </datafile>
            """);

    private string WriteGameBoy() => WriteDat("gb.dat", GameBoy, ("Tetris (World)", Rom("Tetris (World).gb", Tetris)));

    private string WriteGameBoyColor() => WriteDat("gbc.dat", GameBoyColor, ("Tetris (World)", Rom("Tetris (World).gbc", TetrisColor)));

    private string WriteAmiga() => WriteDat("amiga.dat", Amiga,
        ("Lemmings (1991)(Psygnosis)", Rom("Lemmings (1991)(Psygnosis).adf", Lemmings)),
        ("Banshee (1994)(Core)(AGA)", Rom("Banshee (1994)(Core)(AGA).adf", Banshee)));

    private string WritePlayStation() => WriteDat("psx.dat", PlayStation,
        ("Odyssey (USA) (Disc 1)", Rom("Odyssey (USA) (Disc 1).iso", Disc1)),
        ("Odyssey (USA) (Disc 2)", Rom("Odyssey (USA) (Disc 2).iso", Disc2)),
        ("Single (USA)", Rom("Single (USA).iso", Single)),
        ("Cue Game (USA)", Rom("Cue Game (USA).cue", Cue) + Rom("Cue Game (USA).bin", Track)));

    private void WriteSource(string name, byte[] data) => _tmp.WriteBytes(Path.Combine("src", name), data);

    private void WritePlayStationSources()
    {
        WriteSource("odyssey1.iso", Disc1);
        WriteSource("odyssey2.iso", Disc2);
        WriteSource("single.iso", Single);
        WriteSource("Cue Game (USA).cue", Cue);
        WriteSource("Cue Game (USA).bin", Track);
    }

    private SortConfig Config(string datFile, FrontendTarget frontend, Action<SortConfig>? configure = null)
    {
        var config = new SortConfig
        {
            SourceDir = _src,
            DestinationDir = _dst,
            DatFile = datFile,
            Pattern = "{title}",
            Frontend = frontend
        };
        configure?.Invoke(config);
        return config;
    }

    private async Task<(SortExecutionResult Result, List<LogMessage> Logs)> RunAsync(SortConfig config, bool isDryRun = true)
    {
        var logs = new List<LogMessage>();
        var result = await _services.Processor.ExecuteAsync(config, isDryRun: isDryRun, logProgress: new SynchronousProgress<LogMessage>(logs.Add));
        return (result, logs);
    }

    private string TargetOf(SortExecutionResult result, string sourceName) =>
        Path.GetRelativePath(_dst, result.Items.Single(it => Path.GetFileName(it.SourcePath) == sourceName).FinalTargetPath!).Replace('\\', '/');

    private static int Count(List<LogMessage> logs, string key) => logs.Count(l => l.Message.Key == key);

    // =========================================================================
    // Folder of the platform
    // =========================================================================

    [Theory]
    [InlineData(FrontendTarget.None, "Tetris/Tetris (World).gb")]
    [InlineData(FrontendTarget.Batocera, "gb/Tetris/Tetris (World).gb")]
    [InlineData(FrontendTarget.MiSTer, "GAMEBOY/Tetris/Tetris (World).gb")]
    public async Task SingleDat_ShouldGoUnderTheFolderOfItsPlatform(FrontendTarget frontend, string target)
    {
        var dat = WriteGameBoy();
        WriteSource("tetris.gb", Tetris);

        var (result, logs) = await RunAsync(Config(dat, frontend));

        TargetOf(result, "tetris.gb").Should().Be(target);
        logs.Should().NotContain(l => l.Message.Key.StartsWith("log.frontend_", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Lot_ShouldTakeThePlatformInPlaceOfTheFolderOfEachDat(bool folderPerDat, bool mirror)
    {
        WriteGameBoy();
        WriteGameBoyColor();
        WriteSource("tetris.gb", Tetris);
        WriteSource("tetris.gbc", TetrisColor);

        var (result, _) = await RunAsync(Config(_dats, FrontendTarget.Batocera, c =>
        {
            c.FolderPerDat = folderPerDat;
            c.DatFolderMirror = mirror;
        }));

        TargetOf(result, "tetris.gb").Should().Be("gb/Tetris/Tetris (World).gb");
        TargetOf(result, "tetris.gbc").Should().Be("gbc/Tetris/Tetris (World).gbc");
    }

    [Fact]
    public async Task Lot_WithoutFrontend_ShouldKeepTheFolderOfEachDat()
    {
        WriteGameBoy();
        WriteGameBoyColor();
        WriteSource("tetris.gb", Tetris);

        var (result, _) = await RunAsync(Config(_dats, FrontendTarget.None));

        TargetOf(result, "tetris.gb").Should().Be($"{GameBoy}/Tetris/Tetris (World).gb");
    }

    [Fact]
    public async Task UnknownDat_ShouldKeepItsNameAsFolder_WithOneWarning()
    {
        var dat = WriteDat("homebrew.dat", "Homebrew - Things",
            ("Alpha (World)", Rom("Alpha (World).gb", Tetris)),
            ("Beta (World)", Rom("Beta (World).gb", TetrisColor)));
        WriteSource("alpha.gb", Tetris);
        WriteSource("beta.gb", TetrisColor);

        var (result, logs) = await RunAsync(Config(dat, FrontendTarget.RetroPie));

        TargetOf(result, "alpha.gb").Should().Be("Homebrew - Things/Alpha/Alpha (World).gb");
        TargetOf(result, "beta.gb").Should().Be("Homebrew - Things/Beta/Beta (World).gb");
        Count(logs, "log.frontend_platform_unknown").Should().Be(1);
    }

    [Fact]
    public async Task PlatformTheFrontendLacks_ShouldKeepTheNameOfTheDat_WithOneWarning()
    {
        var dat = WriteDat("nds.dat", "Nintendo - Nintendo DS", ("Tetris DS (USA)", Rom("Tetris DS (USA).nds", Tetris)));
        WriteSource("tetris.nds", Tetris);

        var (result, logs) = await RunAsync(Config(dat, FrontendTarget.MiSTer));

        TargetOf(result, "tetris.nds").Should().Be("Nintendo - Nintendo DS/Tetris DS/Tetris DS (USA).nds");
        Count(logs, "log.frontend_platform_missing").Should().Be(1);
    }

    [Fact]
    public async Task ExistingFolder_ShouldKeepItsCase_AndANewOneTakeTheCaseOfTheTable()
    {
        var dat = WriteGameBoy();
        WriteSource("tetris.gb", Tetris);
        Directory.CreateDirectory(Path.Combine(_dst, "GameBoy"));

        var (result, _) = await RunAsync(Config(dat, FrontendTarget.MiSTer, c => c.AppendToDestination = true));
        TargetOf(result, "tetris.gb").Should().Be("GameBoy/Tetris/Tetris (World).gb");

        Directory.Delete(Path.Combine(_dst, "GameBoy"));
        (result, _) = await RunAsync(Config(dat, FrontendTarget.MiSTer));
        TargetOf(result, "tetris.gb").Should().Be("GAMEBOY/Tetris/Tetris (World).gb");
    }

    [Fact]
    public async Task RealSort_ShouldWriteIntoTheExistingFolderOfAnotherCase()
    {
        var dat = WriteGameBoy();
        WriteSource("tetris.gb", Tetris);
        Directory.CreateDirectory(Path.Combine(_dst, "gamEboy"));

        await RunAsync(Config(dat, FrontendTarget.MiSTer, c => c.AppendToDestination = true), isDryRun: false);

        File.Exists(Path.Combine(_dst, "gamEboy", "Tetris", "Tetris (World).gb")).Should().BeTrue();
        Directory.GetDirectories(_dst).Select(Path.GetFileName).Should().Equal("gamEboy");
    }

    [Fact]
    public async Task UnmatchedFile_ShouldStayInUnmatched()
    {
        var dat = WriteGameBoy();
        WriteSource("unknown.gb", Encoding.ASCII.GetBytes("NOT IN THE DAT"));

        var (result, _) = await RunAsync(Config(dat, FrontendTarget.Batocera));

        TargetOf(result, "unknown.gb").Should().Be($"{PathOrganizerService.UnmatchedFolderName}/unknown.gb");
    }

    [Fact]
    public async Task RunWithoutDat_ShouldKeepItsTargets_WithOneWarning()
    {
        WriteSource("Tetris (World).gb", Tetris);
        WriteSource("Tetris (Japan).gb", TetrisColor);

        var (result, logs) = await RunAsync(new SortConfig
        {
            SourceDir = _src,
            DestinationDir = _dst,
            Pattern = "{title}",
            NamingConvention = NamingConvention.NoIntro,
            Frontend = FrontendTarget.Batocera
        });

        TargetOf(result, "Tetris (World).gb").Should().Be("Tetris/Tetris (World).gb");
        Count(logs, "log.frontend_no_dat").Should().Be(1);
    }

    [Theory]
    [InlineData(FrontendTarget.Batocera, "amiga500", "amiga1200")]
    [InlineData(FrontendTarget.RetroPie, "amiga", "amiga")]
    [InlineData(FrontendTarget.Recalbox, "amiga600", "amiga1200")]
    [InlineData(FrontendTarget.MiSTer, "Amiga500", "Amiga500")]
    public async Task AmigaRelease_ShouldGoToTheFolderOfItsMachine(FrontendTarget frontend, string ocs, string aga)
    {
        var dat = WriteAmiga();
        WriteSource("lemmings.adf", Lemmings);
        WriteSource("banshee.adf", Banshee);

        var (result, _) = await RunAsync(Config(dat, frontend, c => c.Pattern = string.Empty));

        TargetOf(result, "lemmings.adf").Should().Be($"{ocs}/Lemmings (1991)(Psygnosis).adf");
        TargetOf(result, "banshee.adf").Should().Be($"{aga}/Banshee (1994)(Core)(AGA).adf");
    }

    // =========================================================================
    // Archives
    // =========================================================================

    [Theory]
    [InlineData(FrontendTarget.RetroPie, 1)]
    [InlineData(FrontendTarget.MiSTer, 1)]
    [InlineData(FrontendTarget.EsDe, 0)]
    public async Task ZipMode_ShouldWarnOncePerDat_WhenTheFrontendDoesNotReadZipArchivesThere(FrontendTarget frontend, int warnings)
    {
        var dat = WritePlayStation();
        WriteSource("single.iso", Single);
        WriteSource("odyssey1.iso", Disc1);

        var (result, logs) = await RunAsync(Config(dat, frontend, c => c.TransferMode = TransferMode.Zip));

        result.Items.Where(it => it.Status == FileStatus.Valid).Should().HaveCount(2);
        Count(logs, "log.frontend_archive_unsupported").Should().Be(warnings);
    }

    [Fact]
    public async Task ZipMode_ShouldNotWarn_WhenTheFrontendReadsZipArchivesThere()
    {
        var dat = WriteGameBoy();
        WriteSource("tetris.gb", Tetris);

        var (result, logs) = await RunAsync(Config(dat, FrontendTarget.MiSTer, c => c.TransferMode = TransferMode.Zip));

        TargetOf(result, "tetris.gb").Should().Be("GAMEBOY/Tetris/Tetris (World).zip");
        Count(logs, "log.frontend_archive_unsupported").Should().Be(0);
    }

    // =========================================================================
    // Safety
    // =========================================================================

    [Fact]
    public async Task DatNamedToLeaveTheDestination_ShouldStayInside()
    {
        WriteDat("evil.dat", "../../Evil", ("Alpha (World)", Rom("Alpha (World).gb", Tetris)));
        WriteDat("purged.dat", "_purged", ("Beta (World)", Rom("Beta (World).gb", TetrisColor)));
        WriteSource("alpha.gb", Tetris);
        WriteSource("beta.gb", TetrisColor);

        var (result, _) = await RunAsync(Config(_dats, FrontendTarget.Batocera));

        foreach (var item in result.Items)
        {
            PathOrganizerService.IsInsideDirectory(item.FinalTargetPath!, _dst).Should().BeTrue(item.FinalTargetPath);
        }

        TargetOf(result, "beta.gb").Should().StartWith("_purged_/");
    }

    [Fact]
    public void PlatformFolder_ShouldBeSanitizedAndGuarded_AndGiveTheDatFolderOfThePatchedVersions()
    {
        var organizer = _services.Organizer;
        var parsed = _services.NoIntroParser.ParseNoIntroFilename("Tetris (World).gb");
        var config = new SortConfig
        {
            SourceDir = _src,
            DestinationDir = _dst,
            Pattern = "{title}",
            PatchedPattern = "Patched/{dat_folder}"
        };

        string Relative(string path) => Path.GetRelativePath(_dst, path).Replace('\\', '/');

        Relative(organizer.BuildTargetPath(parsed, "Tetris (World).gb", config, dat: new DatPlacement(GameBoy, PlatformFolder: "gb")))
            .Should().Be("gb/Tetris/Tetris (World).gb");
        Relative(organizer.BuildTargetPath(parsed, "Tetris (World).gb", config, dat: new DatPlacement(GameBoy, PlatformFolder: "a/b")))
            .Should().Be("a-b/Tetris/Tetris (World).gb");
        Relative(organizer.BuildTargetPath(parsed, "Tetris (World).gb", config, dat: new DatPlacement(GameBoy, PlatformFolder: "_purged")))
            .Should().Be("_purged_/Tetris/Tetris (World).gb");
        Relative(organizer.BuildTargetPath(parsed, "Tetris (World) [T-En].gb", config, dat: new DatPlacement(GameBoy, PlatformFolder: "gb"), patched: true))
            .Should().Be("Patched/gb/Tetris (World) [T-En].gb");
    }

    // =========================================================================
    // Playlists read as a game, folder per disc game
    // =========================================================================

    [Fact]
    public async Task GameFolderPlaylist_ShouldBeInAFolderOfItsOwnName()
    {
        var dat = WritePlayStation();
        WritePlayStationSources();

        var (result, _) = await RunAsync(Config(dat, FrontendTarget.EsDe, c =>
        {
            c.Pattern = string.Empty;
            c.GenerateM3u = true;
            c.M3uPlacement = M3uPlacementMode.GameFolder;
        }), isDryRun: false);

        TargetOf(result, "odyssey1.iso").Should().Be("psx/Odyssey (USA).m3u/Odyssey (USA) (Disc 1).iso");
        TargetOf(result, "odyssey2.iso").Should().Be("psx/Odyssey (USA).m3u/Odyssey (USA) (Disc 2).iso");
        TargetOf(result, "single.iso").Should().Be("psx/Single (USA).iso");

        var playlist = Path.Combine(_dst, "psx", "Odyssey (USA).m3u", "Odyssey (USA).m3u");
        File.ReadAllText(playlist).Should().Be("Odyssey (USA) (Disc 1).iso\nOdyssey (USA) (Disc 2).iso\n");
        Directory.GetFiles(Path.Combine(_dst, "psx"), "*.m3u").Should().BeEmpty("the playlist is in the folder of the game");
    }

    [Fact]
    public async Task GameFolderPlaylist_ShouldNeverBeRenamed_AnExistingOneBeingKept()
    {
        var dat = WritePlayStation();
        WritePlayStationSources();
        var existing = _tmp.WriteText(Path.Combine("dst", "psx", "Odyssey (USA).m3u", "Odyssey (USA).m3u"), "kept\n");

        var (_, logs) = await RunAsync(Config(dat, FrontendTarget.EsDe, c =>
        {
            c.Pattern = string.Empty;
            c.GenerateM3u = true;
            c.M3uPlacement = M3uPlacementMode.GameFolder;
            c.CollisionMode = CollisionMode.Rename;
            c.AppendToDestination = true;
        }), isDryRun: false);

        File.ReadAllText(existing).Should().Be("kept\n");
        Directory.GetFiles(Path.GetDirectoryName(existing)!, "*.m3u").Should().Equal(existing);
        Count(logs, "log.m3u_skipped_existing").Should().Be(1);
    }

    [Fact]
    public async Task GameFolderPlaylist_ShouldBeUndoneWithItsDiscs()
    {
        var dat = WritePlayStation();
        WritePlayStationSources();
        var crcCache = new CrcCacheService(_tmp.GetFilePath(Path.Combine("cache", "crc_cache.json")));
        var journal = new OperationJournalService(_tmp.GetFilePath("journals"), _services.ChecksumService, crcCache);
        var processor = new SortProcessorService(
            _services.ChecksumService, _services.DatService, _services.NoIntroParser, _services.FilterService,
            _services.Organizer, _services.I18n, _services.OpticalCompanionService, _services.MameParser, _services.Patches,
            crcCache, _services.PatchLinks, _services.Frontends, journal);

        await processor.ExecuteAsync(Config(dat, FrontendTarget.EsDe, c =>
        {
            c.Pattern = string.Empty;
            c.GenerateM3u = true;
            c.M3uPlacement = M3uPlacementMode.GameFolder;
        }), isDryRun: false);
        var game = Path.Combine(_dst, "psx", "Odyssey (USA).m3u");
        File.Exists(Path.Combine(game, "Odyssey (USA).m3u")).Should().BeTrue();

        var report = await journal.UndoAsync(journal.FindLastUndoableJournal()!.Path);

        report.Issues.Should().BeEmpty();
        File.Exists(Path.Combine(game, "Odyssey (USA).m3u")).Should().BeFalse();
        File.Exists(Path.Combine(game, "Odyssey (USA) (Disc 1).iso")).Should().BeFalse();
        File.Exists(Path.Combine(_src, "odyssey1.iso")).Should().BeTrue("a copy leaves its source");
    }

    [Theory]
    [InlineData(M3uPlacementMode.Parent, "psx/Odyssey/Odyssey (USA) (Disc 1).iso")]
    [InlineData(M3uPlacementMode.ParentHidden, "psx/.Odyssey/Odyssey (USA) (Disc 1).iso")]
    [InlineData(M3uPlacementMode.Subfolder, "psx/Odyssey/Odyssey (USA) (Disc 1).iso")]
    public async Task OtherPlacements_ShouldKeepTheTitleFolder(M3uPlacementMode placement, string target)
    {
        var dat = WritePlayStation();
        WritePlayStationSources();

        var (result, _) = await RunAsync(Config(dat, FrontendTarget.EsDe, c =>
        {
            c.Pattern = string.Empty;
            c.GenerateM3u = true;
            c.M3uPlacement = placement;
        }));

        TargetOf(result, "odyssey1.iso").Should().Be(target);
    }

    [Theory]
    [InlineData(true, "PSX/Single/Single (USA).iso", "PSX/Cue Game/Cue Game (USA).cue")]
    [InlineData(false, "PSX/Single (USA).iso", "PSX/Cue Game (USA).cue")]
    public async Task FolderPerDiscGame_ShouldPutEveryDiscGameInAFolder(bool folderPerDiscGame, string single, string cue)
    {
        var dat = WritePlayStation();
        WritePlayStationSources();

        var (result, _) = await RunAsync(Config(dat, FrontendTarget.MiSTer, c =>
        {
            c.Pattern = string.Empty;
            c.FolderPerDiscGame = folderPerDiscGame;
            c.SubfolderMultidisk = true;
        }));

        TargetOf(result, "single.iso").Should().Be(single);
        TargetOf(result, "Cue Game (USA).cue").Should().Be(cue);
        var track = result.Items.Single(it => Path.GetFileName(it.SourcePath) == "Cue Game (USA).cue").RelatedItems.Single();
        Path.GetDirectoryName(track.FinalTargetPath).Should().Be(Path.GetDirectoryName(Path.Combine(_dst, cue)));
        TargetOf(result, "odyssey1.iso").Should().Be("PSX/Odyssey/Odyssey (USA) (Disc 1).iso");
    }

    [Fact]
    public async Task FolderPerDiscGame_ShouldLeaveTheOtherFilesAlone_WithOrWithoutFrontend()
    {
        var dat = WriteGameBoy();
        WriteSource("tetris.gb", Tetris);

        var (result, _) = await RunAsync(Config(dat, FrontendTarget.None, c =>
        {
            c.Pattern = string.Empty;
            c.FolderPerDiscGame = true;
        }));

        TargetOf(result, "tetris.gb").Should().Be("Tetris (World).gb");
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
