using System.IO.Compression;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using static RomSieve.Core.Tests.Fixtures.PatchTestEncoder;

namespace RomSieve.Core.Tests;

/// <summary>
/// The patches matched with the files of the source (<see cref="PatchMatcher"/>), through a simulation: by the CRC32
/// they record (UPS, BPS) or the one their name gives (IPS), with and without copier header, an origin the 1G1R leaves
/// out, an origin or a patch in an archive, the patches left without patched version and why, the patch folder inside
/// the source, the option to write the patched versions only, a lot of DATs, the recalculation of a game and a partial
/// sort. The writing of the patched versions is tested by <see cref="PatchTransferTests"/>.
/// </summary>
public sealed class PatchMatchingTests : IDisposable
{
    private readonly CoreTestServices _services = new();
    private readonly TempDirectoryFixture _temp = new();
    private readonly string _src;
    private readonly string _dst;
    private readonly string _patches;

    private static readonly byte[] Japan = Data(1, 4096);
    private static readonly byte[] Usa = Data(2, 4096);
    private static readonly byte[] Translated = Translate(Japan);

    public PatchMatchingTests()
    {
        _src = _temp.CreateSubdirectory("src");
        _dst = _temp.CreateSubdirectory("dst");
        _patches = _temp.CreateSubdirectory("patches");
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>Data of a synthetic ROM, the same for the same seed.</summary>
    private static byte[] Data(int seed, int size)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>A "translation": a few bytes changed, and a few added.</summary>
    private static byte[] Translate(byte[] data)
    {
        var translated = data.Concat(Ascii("English")).ToArray();
        translated[10] ^= 0x55;
        translated[100] ^= 0x33;
        return translated;
    }

    private string WriteDat(params (string Game, string Rom, byte[] Data)[] roms)
    {
        var games = string.Concat(roms.Select(r =>
            $"""<game name="{r.Game}"><description>{r.Game}</description><rom name="{r.Rom}" size="{r.Data.Length}" crc="{Crc(r.Data):x8}"/></game>"""));
        return _temp.WriteText(
            "dat/snes.dat",
            $"""<?xml version="1.0"?><datafile><header><name>Nintendo - Super Nintendo Entertainment System</name><homepage>No-Intro</homepage></header>{games}</datafile>""");
    }

    /// <summary>The source of most tests: the Japanese and American releases of a game, the American one kept by the 1G1R.</summary>
    private string WriteJapanAndUsa()
    {
        _temp.WriteBytes("src/Game (Japan).sfc", Japan);
        _temp.WriteBytes("src/Game (USA).sfc", Usa);
        return WriteDat(("Game (Japan)", "Game (Japan).sfc", Japan), ("Game (USA)", "Game (USA).sfc", Usa));
    }

    private SortConfig Config(string dat, bool patchOnly = false, string? patchDir = null) => new()
    {
        SourceDir = _src,
        DestinationDir = _dst,
        NamingConvention = NamingConvention.NoIntro,
        DatFile = dat,
        PatchDir = patchDir ?? _patches,
        PatchOnly = patchOnly,
        Filters = new FilterConfig { OneGameOneRom = true, PriorityLanguages = ["en", "ja"] }
    };

    private sealed class LogCollector : IProgress<LogMessage>
    {
        public List<LogMessage> Messages { get; } = [];
        public void Report(LogMessage value) => Messages.Add(value);
    }

    private async Task<(SortExecutionResult Result, LogCollector Log)> SimulateAsync(SortConfig config)
    {
        var log = new LogCollector();
        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true, logProgress: log);
        return (result, log);
    }

    private static ProcessItem Named(SortExecutionResult result, string displayName) =>
        result.Items.Single(it => it.DisplayName == displayName);

    private static ProcessItem PatchedVersion(SortExecutionResult result) => result.Items.Single(it => it.Patch is not null);

    // =========================================================================
    // Matching
    // =========================================================================

    [Fact]
    public async Task ABps_ShouldGiveAPatchedVersionOfAnOriginThe1G1RLeavesOut()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (Japan) (T-En).bps", BpsLinear(Japan, Translated));

        var (result, _) = await SimulateAsync(Config(dat));

        var japan = Named(result, "Game (Japan).sfc");
        var patched = PatchedVersion(result);
        japan.Status.Should().Be(FileStatus.Excluded, "the 1G1R keeps the American release");
        Named(result, "Game (USA).sfc").Status.Should().Be(FileStatus.Valid);

        patched.Status.Should().Be(FileStatus.Valid);
        patched.ReleaseStatus.Should().Be(FileStatus.Valid);
        patched.Patch!.Origin.Should().BeSameAs(japan);
        patched.Patch.Format.Should().Be(PatchFormat.Bps);
        patched.Patch.SourceOffset.Should().Be(0);
        japan.PatchedVersions.Should().ContainSingle().Which.Should().BeSameAs(patched);
        patched.TargetFilename.Should().Be("Game (Japan) (T-En).sfc");
        patched.FileSize.Should().Be(Translated.Length);
        patched.Crc32.Should().Be(Crc(Translated).ToString("x8"));
        patched.Parsed.Should().BeSameAs(japan.Parsed, "the patched version belongs to the game of its origin");
        patched.StatusReason!.Key.Should().Be("reason.patched_from");
        patched.StatusReason.Args["format"].Should().Be("BPS");
        result.Items.ToList().IndexOf(patched).Should().Be(result.Items.ToList().IndexOf(japan) - 1, "it is written before its origin");
        result.UnappliedPatches.Should().BeEmpty();
    }

    [Fact]
    public async Task APatchedVersion_ShouldGoIntoTheFolderTheOriginWouldHave()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (Japan) (T-En).ups", Ups(Japan, Translated));
        var config = Config(dat);
        config.Pattern = "{title}/";

        var (result, _) = await SimulateAsync(config);

        var japan = Named(result, "Game (Japan).sfc");
        var patched = PatchedVersion(result);
        Path.GetDirectoryName(patched.FinalTargetPath).Should().Be(Path.GetDirectoryName(japan.TargetPath));
        Path.GetFileName(patched.FinalTargetPath).Should().Be("Game (Japan) (T-En).sfc");
    }

    [Fact]
    public async Task AnIps_ShouldFindItsOriginByTheCrcOfItsName()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes($"patches/Game (Japan) (T-En) [{Crc(Japan):X8}].ips", IpsDiff(Japan, Translated));

        var (result, _) = await SimulateAsync(Config(dat));

        var patched = PatchedVersion(result);
        patched.Patch!.Origin.DisplayName.Should().Be("Game (Japan).sfc");
        patched.TargetFilename.Should().Be("Game (Japan) (T-En).sfc", "the CRC32 is removed from the name with its brackets");
        patched.FileSize.Should().Be(Translated.Length);
        patched.Crc32.Should().BeEmpty("an IPS records no checksum");
    }

    [Fact]
    public async Task AnIpsWithoutCrc_ShouldNotBeApplied()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (Japan) (T-En).ips", IpsDiff(Japan, Translated));

        var (result, log) = await SimulateAsync(Config(dat));

        result.Items.Should().NotContain(it => it.Patch != null);
        result.UnappliedPatches.Should().ContainSingle().Which.Reason.Key.Should().Be("reason.patch_no_crc");
        log.Messages.Should().Contain(m => m.Level == LogLevel.Warn && m.Message.Key == "log.patch_not_applied");
    }

    [Fact]
    public async Task AnIpsWhoseNameGivesTwoOrigins_ShouldNotBeApplied()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes($"patches/Game [{Crc(Japan):x8}] [{Crc(Usa):x8}].ips", IpsDiff(Japan, Translated));

        var (result, _) = await SimulateAsync(Config(dat));

        result.UnappliedPatches.Should().ContainSingle().Which.Reason.Key.Should().Be("reason.patch_ambiguous");
    }

    [Fact]
    public async Task APatchWithoutOrigin_ShouldNotBeApplied()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Other Game (T-En).bps", BpsLinear(Data(9, 100), Data(10, 100)));

        var (result, _) = await SimulateAsync(Config(dat));

        var unapplied = result.UnappliedPatches.Should().ContainSingle().Which;
        unapplied.Reason.Key.Should().Be("reason.patch_orphan");
        unapplied.File.DisplayName.Should().Be("Other Game (T-En).bps");
    }

    [Fact]
    public async Task ADamagedPatch_ShouldNotBeApplied()
    {
        var dat = WriteJapanAndUsa();
        var patch = BpsLinear(Japan, Translated);
        patch[^1] ^= 0xFF;
        _temp.WriteBytes("patches/Game (Japan) (T-En).bps", patch);

        var (result, _) = await SimulateAsync(Config(dat));

        var reason = result.UnappliedPatches.Should().ContainSingle().Which.Reason;
        reason.Key.Should().Be("reason.patch_unreadable");
        reason.Args["error"].Should().BeOfType<LocalizedMessage>().Which.Key.Should().Be("patch.error_patch_crc");
    }

    [Fact]
    public async Task AFileWithThePatchExtensionAndAnotherSignature_ShouldNotBeApplied()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteText("patches/readme.bps", "Not a patch");
        _temp.WriteText("patches/readme.txt", "Not a patch either, and not read");

        var (result, _) = await SimulateAsync(Config(dat));

        var reason = result.UnappliedPatches.Should().ContainSingle().Which.Reason;
        reason.Args["error"].Should().BeOfType<LocalizedMessage>().Which.Key.Should().Be("patch.error_signature");
    }

    [Fact]
    public async Task TwoPatchesOfARom_ShouldGiveTwoPatchedVersionsBeforeIt()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (Japan) (T-En).bps", BpsLinear(Japan, Translated));
        _temp.WriteBytes("patches/Game (Japan) (T-Fr).ups", Ups(Japan, Translate(Translated)));

        var (result, _) = await SimulateAsync(Config(dat));

        var items = result.Items.ToList();
        var japan = Named(result, "Game (Japan).sfc");
        var index = items.IndexOf(japan);
        items[index - 2].TargetFilename.Should().Be("Game (Japan) (T-En).sfc");
        items[index - 1].TargetFilename.Should().Be("Game (Japan) (T-Fr).sfc");
        japan.PatchedVersions.Should().HaveCount(2);
    }

    [Fact]
    public async Task TwoFilesOfTheSameCrc_ShouldGiveTheOriginTheSortWrites()
    {
        _temp.WriteBytes("src/a/Game (USA).sfc", Usa);
        _temp.WriteBytes("src/b/Game (USA).sfc", Usa);
        var dat = WriteDat(("Game (USA)", "Game (USA).sfc", Usa));
        _temp.WriteBytes("patches/Game (USA) (Hack).bps", BpsLinear(Usa, Translate(Usa)));

        var (result, _) = await SimulateAsync(Config(dat));

        var origin = PatchedVersion(result).Patch!.Origin;
        origin.ReleaseStatus.Should().Be(FileStatus.Valid);
        result.Items.Where(it => it.Patch is null).Should().HaveCount(2);
    }

    // =========================================================================
    // Copier headers
    // =========================================================================

    private static byte[] WithInesHeader(byte[] data) => [.. Ascii("NES\u001a"), .. new byte[12], .. data];

    [Fact]
    public async Task APatchOfTheDataWithoutHeader_ShouldSkipTheHeaderOfItsOrigin()
    {
        var data = Data(3, 8192);
        _temp.WriteBytes("src/Game (USA).nes", WithInesHeader(data));
        var dat = WriteDat(("Game (USA)", "Game (USA).nes", data));
        _temp.WriteBytes("patches/Game (USA) (Hack).bps", BpsLinear(data, Translate(data)));

        var (result, _) = await SimulateAsync(Config(dat));

        var version = PatchedVersion(result).Patch!;
        version.Origin.CopierHeaderSize.Should().Be(16);
        version.SourceOffset.Should().Be(16);
        version.SourceLength.Should().Be(data.Length);
    }

    [Fact]
    public async Task APatchOfTheWholeFile_ShouldFindAnOriginIdentifiedWithoutItsHeader()
    {
        var data = Data(3, 8192);
        var file = WithInesHeader(data);
        _temp.WriteBytes("src/Game (USA).nes", file);
        var dat = WriteDat(("Game (USA)", "Game (USA).nes", data));
        _temp.WriteBytes("patches/Game (USA) (Hack).ups", Ups(file, Translate(file)));

        var (result, _) = await SimulateAsync(Config(dat));

        var version = PatchedVersion(result).Patch!;
        version.SourceOffset.Should().Be(0);
        version.SourceLength.Should().Be(file.Length);
        version.SourceCrc32.Should().Be(Crc(file));
    }

    [Fact]
    public async Task APatchOfTheDataWithoutHeader_ShouldFindAnOriginIdentifiedWithIt()
    {
        // The DAT lists the whole file: the classification finds no header to remove, the patch expects none
        var data = Data(3, 8192);
        var file = WithInesHeader(data);
        _temp.WriteBytes("src/Game (USA).nes", file);
        var dat = WriteDat(("Game (USA)", "Game (USA).nes", file));
        _temp.WriteBytes($"patches/Game (USA) (Hack) [{Crc(data):x8}].ips", IpsDiff(data, Translate(data)));

        var (result, _) = await SimulateAsync(Config(dat));

        var version = PatchedVersion(result).Patch!;
        version.Origin.CopierHeaderSize.Should().Be(0);
        version.SourceOffset.Should().Be(16);
    }

    [Fact]
    public async Task ASnesCopierHeader_ShouldBeSkippedToo()
    {
        var data = Data(4, 4096);
        _temp.WriteBytes("src/Game (USA).sfc", [.. new byte[512], .. data]);
        var dat = WriteDat(("Game (USA)", "Game (USA).sfc", data));
        _temp.WriteBytes("patches/Game (USA) (Hack).bps", BpsLinear(data, Translate(data)));

        var (result, _) = await SimulateAsync(Config(dat));

        PatchedVersion(result).Patch!.SourceOffset.Should().Be(512);
    }

    // =========================================================================
    // Archives
    // =========================================================================

    private string WriteZip(string relativePath, string member, byte[] data)
    {
        var path = Path.GetFullPath(_temp.GetFilePath(relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var stream = zip.CreateEntry(member).Open();
        stream.Write(data);
        return path;
    }

    [Fact]
    public async Task AnOriginInAnArchive_ShouldGiveAPatchedVersion()
    {
        var zip = WriteZip("src/Game (Japan).zip", "Game (Japan).sfc", Japan);
        var dat = WriteDat(("Game (Japan)", "Game (Japan).sfc", Japan));
        _temp.WriteBytes("patches/Game (Japan) (T-En).bps", BpsLinear(Japan, Translated));

        var (result, _) = await SimulateAsync(Config(dat));

        var origin = PatchedVersion(result).Patch!.Origin;
        origin.ArchivePath.Should().Be(zip);
        origin.ArchiveMember.Should().Be("Game (Japan).sfc");
    }

    [Fact]
    public async Task APatchInAnArchive_ShouldBeRead()
    {
        var dat = WriteJapanAndUsa();
        var zip = WriteZip("patches/translations.zip", "Game (Japan) (T-En).bps", BpsLinear(Japan, Translated));

        var (result, _) = await SimulateAsync(Config(dat));

        var patched = PatchedVersion(result);
        patched.SourcePath.Should().Be(zip);
        patched.ArchivePath.Should().Be(zip);
        patched.ArchiveMember.Should().Be("Game (Japan) (T-En).bps");
        patched.Patch!.File.Should().Be(new PatchFile(zip, "Game (Japan) (T-En).bps"));
    }

    // =========================================================================
    // Games in several files
    // =========================================================================

    [Fact]
    public async Task AnOriginInAGameOfSeveralFiles_ShouldNotBeApplied()
    {
        var track = Data(5, 2352 * 4);
        var cue = Ascii("FILE \"Game (Europe).bin\" BINARY\r\n  TRACK 01 MODE1/2352\r\n    INDEX 01 00:00:00\r\n");
        _temp.WriteBytes("src/Game (Europe).cue", cue);
        _temp.WriteBytes("src/Game (Europe).bin", track);
        var dat = _temp.WriteText("dat/redump.dat", $"""
            <?xml version="1.0"?><datafile><header><name>Sony - PlayStation</name><url>http://redump.org/</url></header>
            <game name="Game (Europe)"><description>Game (Europe)</description>
            <rom name="Game (Europe).cue" size="{cue.Length}" crc="{Crc(cue):x8}"/>
            <rom name="Game (Europe).bin" size="{track.Length}" crc="{Crc(track):x8}"/></game></datafile>
            """);
        _temp.WriteBytes("patches/Game (Europe) (T-En).bps", BpsLinear(track, Translate(track)));
        var config = Config(dat);
        config.NamingConvention = NamingConvention.Redump;

        var (result, _) = await SimulateAsync(config);

        result.Items.Should().NotContain(it => it.Patch != null);
        var reason = result.UnappliedPatches.Should().ContainSingle().Which.Reason;
        reason.Key.Should().Be("reason.patch_multi_file");
        reason.Args["origin"].Should().Be("Game (Europe).bin");
    }

    // =========================================================================
    // Patch folder and runs that do not read it
    // =========================================================================

    [Fact]
    public async Task APatchFolderInsideTheSource_ShouldNotBeSorted()
    {
        var dat = WriteJapanAndUsa();
        var inside = _temp.CreateSubdirectory("src/patches");
        _temp.WriteBytes("src/patches/Game (Japan) (T-En).bps", BpsLinear(Japan, Translated));
        _temp.WriteText("src/patches/readme.txt", "Notes of the translation");

        var (result, _) = await SimulateAsync(Config(dat, patchDir: inside));

        result.Items.Should().NotContain(it => it.Patch == null && it.SourcePath.StartsWith(inside, StringComparison.OrdinalIgnoreCase),
            "no file of the patch folder is a file of the collection");
        PatchedVersion(result).TargetFilename.Should().Be("Game (Japan) (T-En).sfc");
    }

    [Fact]
    public async Task APatchFolderThatIsTheSource_ShouldOnlyLeaveItsPatchesOut()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("src/Game (Japan) (T-En).bps", BpsLinear(Japan, Translated));

        var (result, _) = await SimulateAsync(Config(dat, patchDir: _src));

        result.Items.Where(it => it.Patch is null).Select(it => it.DisplayName)
            .Should().BeEquivalentTo(["Game (Japan).sfc", "Game (USA).sfc"]);
        result.Items.Should().ContainSingle(it => it.Patch != null);
    }

    [Fact]
    public async Task AMissingPatchFolder_ShouldBeReportedAndTheSortGoOn()
    {
        var dat = WriteJapanAndUsa();

        var (result, log) = await SimulateAsync(Config(dat, patchDir: Path.Combine(_temp.Path, "nowhere")));

        result.Items.Should().HaveCount(2);
        log.Messages.Should().Contain(m => m.Level == LogLevel.Warn && m.Message.Key == "log.patch_dir_not_found");
    }

    [Fact]
    public async Task ASortWithoutDat_ShouldNotReadThePatches()
    {
        WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (Japan) (T-En).bps", BpsLinear(Japan, Translated));
        var config = Config(dat: string.Empty);
        config.DatFile = null;

        var (result, log) = await SimulateAsync(config);

        result.Items.Should().NotContain(it => it.Patch != null);
        log.Messages.Should().Contain(m => m.Message.Key == "log.patches_need_dat");
    }

    [Fact]
    public async Task ASortOfMameSets_ShouldNotReadThePatches()
    {
        var rom = Data(6, 1024);
        using (var zip = ZipFile.Open(_temp.GetFilePath("src/game1.zip"), ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry("game1.rom").Open();
            stream.Write(rom);
        }

        var dat = _temp.WriteText("dat/mame.xml", $"""
            <?xml version="1.0"?><mame build="0.289 (test)"><machine name="game1"><description>Game One</description><year>1990</year>
            <manufacturer>Test</manufacturer><rom name="game1.rom" size="{rom.Length}" crc="{Crc(rom):x8}"/><driver status="good"/></machine></mame>
            """);
        _temp.WriteBytes("patches/game1 (Hack).bps", BpsLinear(rom, Translate(rom)));
        var config = Config(dat);
        config.NamingConvention = NamingConvention.Mame;

        var (result, log) = await SimulateAsync(config);

        result.Items.Should().NotContain(it => it.Patch != null);
        log.Messages.Should().Contain(m => m.Message.Key == "log.patches_ignored_mame");
    }

    // =========================================================================
    // Option to write the patched versions only
    // =========================================================================

    [Fact]
    public async Task PatchOnly_ShouldLeaveOutAnOriginTheSortWouldWrite()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (USA) (Hack).bps", BpsLinear(Usa, Translate(Usa)));

        var (result, _) = await SimulateAsync(Config(dat, patchOnly: true));

        var usa = Named(result, "Game (USA).sfc");
        usa.Status.Should().Be(FileStatus.Excluded);
        usa.ReleaseStatus.Should().Be(FileStatus.Excluded);
        usa.IsReplacedByPatch.Should().BeTrue();
        usa.StatusReason!.Key.Should().Be("reason.replaced_by_patch");
        usa.StatusReason.Args["patches"].Should().Be("Game (USA) (Hack).bps");
        PatchedVersion(result).Status.Should().Be(FileStatus.Valid);
    }

    [Fact]
    public async Task PatchOnly_ShouldKeepTheReasonOfAnOriginThe1G1RLeftOut()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (Japan) (T-En).bps", BpsLinear(Japan, Translated));

        var (result, _) = await SimulateAsync(Config(dat, patchOnly: true));

        var japan = Named(result, "Game (Japan).sfc");
        japan.IsReplacedByPatch.Should().BeFalse();
        japan.StatusReason!.Key.Should().NotBe("reason.replaced_by_patch");
        Named(result, "Game (USA).sfc").Status.Should().Be(FileStatus.Valid, "the option only leaves out the files a patched version replaces");
    }

    // =========================================================================
    // Lot of DATs, recalculation, partial sort
    // =========================================================================

    [Fact]
    public async Task InALot_ThePatchedVersion_ShouldBelongToTheDatOfItsOrigin()
    {
        var nesRom = Data(7, 2048);
        _temp.WriteBytes("src/Game (Japan).sfc", Japan);
        _temp.WriteBytes("src/Other (Japan).nes", nesRom);
        _temp.WriteText("lot/snes.dat", $"""<?xml version="1.0"?><datafile><header><name>Nintendo - SNES</name><homepage>No-Intro</homepage></header><game name="Game (Japan)"><description>Game (Japan)</description><rom name="Game (Japan).sfc" size="{Japan.Length}" crc="{Crc(Japan):x8}"/></game></datafile>""");
        _temp.WriteText("lot/nes.dat", $"""<?xml version="1.0"?><datafile><header><name>Nintendo - NES</name><homepage>No-Intro</homepage></header><game name="Other (Japan)"><description>Other (Japan)</description><rom name="Other (Japan).nes" size="{nesRom.Length}" crc="{Crc(nesRom):x8}"/></game></datafile>""");
        _temp.WriteBytes("patches/Other (Japan) (T-En).bps", BpsLinear(nesRom, Translate(nesRom)));

        var (result, _) = await SimulateAsync(Config(_temp.GetFilePath("lot")));

        var patched = PatchedVersion(result);
        patched.DatName.Should().Be("Nintendo - NES");
        patched.Parsed.DatKey.Should().Be(patched.Patch!.Origin.Parsed.DatKey).And.NotBeNull();
        patched.FinalTargetPath.Should().Contain("Nintendo - NES");
    }

    [Fact]
    public async Task TheRecalculationOfAGame_ShouldGiveTheResultOfTheSimulation()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (USA) (Hack).bps", BpsLinear(Usa, Translate(Usa)));
        var config = Config(dat, patchOnly: true);
        var (result, _) = await SimulateAsync(config);
        string Describe() => string.Join("|", result.Items.Select(it => $"{it.DisplayName}:{it.Status}:{it.StatusReason?.Key}:{it.IsReplacedByPatch}"));
        var simulated = Describe();

        _services.Processor.RecalculateGameItems(result.Items.ToList(), config, result.DatCatalog, result.Items);

        Describe().Should().Be(simulated);
    }

    [Fact]
    public async Task APartialSort_ShouldTakeThePatchedVersionWithItsGame()
    {
        var other = Data(8, 1024);
        _temp.WriteBytes("src/Game (Japan).sfc", Japan);
        _temp.WriteBytes("src/Other (USA).sfc", other);
        var dat = WriteDat(("Game (Japan)", "Game (Japan).sfc", Japan), ("Other (USA)", "Other (USA).sfc", other));
        _temp.WriteBytes("patches/Game (Japan) (T-En).bps", BpsLinear(Japan, Translated));
        var config = Config(dat);
        var (simulation, _) = await SimulateAsync(config);
        var selection = SelectionFilter.Create(simulation.Items, [Named(simulation, "Game (Japan).sfc")], _services.FilterService);

        var partial = await _services.Processor.ExecuteAsync(config, isDryRun: true, selection: selection);

        PatchedVersion(partial).IsOutsideSelection.Should().BeFalse();
        PatchedVersion(partial).Status.Should().Be(FileStatus.Valid);
        Named(partial, "Other (USA).sfc").IsOutsideSelection.Should().BeTrue();
    }

    // =========================================================================
    // Name of a patched version
    // =========================================================================

    [Theory]
    [InlineData("Game (T-En) [47e70e08].ips", PatchFormat.Ips, "Game (T-En).sfc")]
    [InlineData("Game (T-En) (0x47E70E08).ips", PatchFormat.Ips, "Game (T-En).sfc")]
    [InlineData("[47E70E08] Game v1.1.ips32", PatchFormat.Ips32, "Game v1.1.sfc")]
    [InlineData("Game_47e70e08.ips", PatchFormat.Ips, "Game_.sfc")]
    [InlineData("47e70e08.ips", PatchFormat.Ips, "47e70e08.sfc")]
    [InlineData("Game [47e70e08] [deadbeef].ips", PatchFormat.Ips, "Game [deadbeef].sfc")]
    [InlineData("Game [47e70e08].bps", PatchFormat.Bps, "Game [47e70e08].sfc")]
    [InlineData("sub/Game (Hack).ups", PatchFormat.Ups, "Game (Hack).sfc")]
    public void PatchedName_ShouldFollowThePatchAndTheExtensionOfTheOrigin(string patchName, PatchFormat format, string expected)
    {
        PatchMatcher.PatchedName(patchName, "Game (Japan).sfc", format, 0x47e70e08).Should().Be(expected);
    }
}
