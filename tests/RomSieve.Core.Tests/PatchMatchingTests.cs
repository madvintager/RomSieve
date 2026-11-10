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
        patched.TargetFilename.Should().Be("Game (Japan) [T-En].sfc");
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
        Path.GetFileName(patched.FinalTargetPath).Should().Be("Game (Japan) [T-En].sfc");
    }

    [Fact]
    public async Task AnIps_ShouldFindItsOriginByTheCrcOfItsName()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes($"patches/Game (Japan) (T-En) [{Crc(Japan):X8}].ips", IpsDiff(Japan, Translated));

        var (result, _) = await SimulateAsync(Config(dat));

        var patched = PatchedVersion(result);
        patched.Patch!.Origin.DisplayName.Should().Be("Game (Japan).sfc");
        patched.TargetFilename.Should().Be("Game (Japan) [T-En].sfc", "the CRC32 is removed from the name with its brackets");
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

        var unapplied = result.UnappliedPatches.Should().ContainSingle().Which;
        unapplied.Reason.Key.Should().Be("reason.patch_ambiguous");
        unapplied.Suggestions.Select(s => (s.Origin.DisplayName, s.Crc32)).Should().Equal(
            [("Game (Japan).sfc", Crc(Japan)), ("Game (USA).sfc", Crc(Usa))],
            "the files its CRC32 designate are proposed, in the order of its name");
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
        unapplied.Suggestions.Should().BeEmpty("a patch that records its origin is never matched by its name");
    }

    [Fact]
    public async Task ADamagedPatch_ShouldNotBeApplied()
    {
        var dat = WriteJapanAndUsa();
        var patch = BpsLinear(Japan, Translated);
        patch[^1] ^= 0xFF;
        _temp.WriteBytes("patches/Game (Japan) (T-En).bps", patch);

        var (result, _) = await SimulateAsync(Config(dat));

        var unapplied = result.UnappliedPatches.Should().ContainSingle().Which;
        var reason = unapplied.Reason;
        reason.Key.Should().Be("reason.patch_unreadable");
        reason.Args["error"].Should().BeOfType<LocalizedMessage>().Which.Key.Should().Be("patch.error_patch_crc");
        unapplied.Format.Should().BeNull("a patch that cannot be applied says no format: no ROM can be chosen for it");
        unapplied.Key.Should().Be(PatchKey.Of(patch), "it can still be set aside");
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
        items[index - 2].TargetFilename.Should().Be("Game (Japan) [T-En].sfc");
        items[index - 1].TargetFilename.Should().Be("Game (Japan) [T-Fr].sfc");
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

    [Fact]
    public async Task AnOriginInAMultiDiskSet_ShouldNotBeApplied()
    {
        var disk1 = Data(11, 2048);
        var disk2 = Data(12, 2048);
        _temp.WriteBytes("src/Game (1990)(Ocean)(Disk 1 of 2).adf", disk1);
        _temp.WriteBytes("src/Game (1990)(Ocean)(Disk 2 of 2).adf", disk2);
        var dat = _temp.WriteText("dat/amiga.dat", $"""
            <?xml version="1.0"?><datafile><header><name>Commodore Amiga - Games - [ADF] (TOSEC)</name><description>TOSEC</description></header>
            <game name="Game (1990)(Ocean)(Disk 1 of 2)"><description>Game</description><rom name="Game (1990)(Ocean)(Disk 1 of 2).adf" size="2048" crc="{Crc(disk1):x8}"/></game>
            <game name="Game (1990)(Ocean)(Disk 2 of 2)"><description>Game</description><rom name="Game (1990)(Ocean)(Disk 2 of 2).adf" size="2048" crc="{Crc(disk2):x8}"/></game></datafile>
            """);
        _temp.WriteBytes("patches/Game (1990)(Ocean)(Disk 1 of 2)[t Eng].bps", BpsLinear(disk1, Translate(disk1)));
        var config = Config(dat);
        config.NamingConvention = NamingConvention.Tosec;
        config.Filters = new FilterConfig();

        var (result, _) = await SimulateAsync(config);

        result.Items.Should().NotContain(it => it.Patch != null, "a disk of a set is never patched alone");
        result.UnappliedPatches.Should().ContainSingle().Which.Reason.Key.Should().Be("reason.patch_multi_file");
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
        PatchedVersion(result).TargetFilename.Should().Be("Game (Japan) [T-En].sfc");
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
    public async Task PatchOnly_WithThe1G1R_ShouldReplaceTheReleaseKeptForTheGame_ByAPatchOfAnotherRelease()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (Japan) (T-En).bps", BpsLinear(Japan, Translated));

        var (result, _) = await SimulateAsync(Config(dat, patchOnly: true));

        var japan = Named(result, "Game (Japan).sfc");
        japan.Status.Should().Be(FileStatus.Excluded);
        japan.IsReplacedByPatch.Should().BeFalse("the 1G1R left it out first, and it keeps its reason");
        japan.StatusReason!.Key.Should().NotBe("reason.replaced_by_patch");
        var usa = Named(result, "Game (USA).sfc");
        usa.Status.Should().Be(FileStatus.Excluded, "the patched version of the game replaces the release the 1G1R kept");
        usa.IsReplacedByPatch.Should().BeTrue();
        usa.StatusReason!.Key.Should().Be("reason.replaced_by_patch");
        usa.StatusReason.Args["patches"].Should().Be("Game (Japan) (T-En).bps");
        PatchedVersion(result).Status.Should().Be(FileStatus.Valid);
        result.Items.Where(it => it.Status == FileStatus.Valid).Should().ContainSingle("only the patched version of the game is written");
    }

    [Fact]
    public async Task PatchOnly_WithoutThe1G1R_ShouldOnlyReplaceTheOriginOfThePatch()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (Japan) (T-En).bps", BpsLinear(Japan, Translated));
        var config = Config(dat, patchOnly: true);
        config.Filters.OneGameOneRom = false;

        var (result, _) = await SimulateAsync(config);

        Named(result, "Game (Japan).sfc").IsReplacedByPatch.Should().BeTrue();
        Named(result, "Game (USA).sfc").Status.Should().Be(FileStatus.Valid, "without the 1G1R, each release kept is a choice of its own");
    }

    [Fact]
    public async Task WithoutPatchOnly_TheReleaseKeptAndThePatchedVersion_ShouldBothBeWritten()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (Japan) (T-En).bps", BpsLinear(Japan, Translated));

        var (result, _) = await SimulateAsync(Config(dat));

        Named(result, "Game (USA).sfc").Status.Should().Be(FileStatus.Valid);
        Named(result, "Game (USA).sfc").IsReplacedByPatch.Should().BeFalse();
        PatchedVersion(result).Status.Should().Be(FileStatus.Valid);
    }

    [Fact]
    public async Task TheRecalculationOfAGame_ShouldReplaceTheReleaseKept_ByAPatchOfAnotherRelease()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (Japan) (T-En).bps", BpsLinear(Japan, Translated));
        var config = Config(dat, patchOnly: true);
        var (result, _) = await SimulateAsync(config);
        string Describe() => string.Join("|", result.Items.Select(it => $"{it.DisplayName}:{it.Status}:{it.StatusReason?.Key}:{it.IsReplacedByPatch}"));
        var simulated = Describe();

        _services.Processor.RecalculateGameItems(result.Items.ToList(), config, result.DatCatalog, result.Items);

        Describe().Should().Be(simulated);
        Named(result, "Game (USA).sfc").IsReplacedByPatch.Should().BeTrue();
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
    // What follows the name of the origin, or its title, without its parentheses
    [InlineData("Game (Japan) (T-En).bps", "Game (Japan) [T-En].sfc")]
    [InlineData("Game (T-En).bps", "Game (Japan) [T-En].sfc")]
    [InlineData("game (japan) (T-En) (v1.2).bps", "Game (Japan) [T-En v1.2].sfc")]
    [InlineData("Game_(T-En).ups", "Game (Japan) [T-En].sfc")]
    [InlineData("Game - English Translation.ups", "Game (Japan) [English Translation].sfc")]
    [InlineData("Game (Japan, Korea) [T+Eng].bps", "Game (Japan) [Japan, Korea T+Eng].sfc")]
    [InlineData("Gameplay Fix.bps", "Game (Japan) [Gameplay Fix].sfc")]
    // A name without the title of the origin is the label whole
    [InlineData("SMB3_eng_v1.2.ups", "Game (Japan) [SMB3_eng_v1.2].sfc")]
    [InlineData("[T+Eng] Game.bps", "Game (Japan) [T+Eng Game].sfc")]
    // The CRC32 of the origin is removed, whatever the format, with the brackets that held it only
    [InlineData("Game (T-En) [47e70e08].ips", "Game (Japan) [T-En].sfc")]
    [InlineData("Game (T-En) (0x47E70E08).ips", "Game (Japan) [T-En].sfc")]
    [InlineData("Game [47e70e08] [deadbeef].ips", "Game (Japan) [deadbeef].sfc")]
    [InlineData("Game (T-En) [47e70e08].bps", "Game (Japan) [T-En].sfc")]
    // A generic label is kept without a better one; nothing left gives the default label
    [InlineData("Game (Japan) (Hack).bps", "Game (Japan) [Hack].sfc")]
    [InlineData("[47E70E08] Game v1.1.ips32", "Game (Japan) [v1.1].sfc")]
    [InlineData("patch.bps", "Game (Japan) [patch].sfc")]
    [InlineData("Game (Japan).ips", "Game (Japan) [patched].sfc")]
    [InlineData("Game_47e70e08.ips", "Game (Japan) [patched].sfc")]
    [InlineData("47e70e08.ips", "Game (Japan) [patched].sfc")]
    public void PatchedName_ShouldBeTheOriginAndALabelOfThePatch(string patchName, string expected)
    {
        var patch = new PatchFile(Path.Combine("patches", patchName));

        PatchMatcher.PatchedName(patch, "Game (Japan).sfc", 0x47e70e08, folderLabel: null, NamingConvention.NoIntro).Should().Be(expected);
    }

    [Theory]
    // A generic or empty name of the patch gives way to its folder in its archive, its archive, then its folder
    [InlineData("Game Translation v1.0.zip", "patch.ips", null, "Game (Japan) [Translation v1.0].sfc")]
    [InlineData("translations.zip", "CT English/patch.ips", null, "Game (Japan) [CT English].sfc")]
    [InlineData("translations.zip", "Game (Japan) (T-En).bps", null, "Game (Japan) [T-En].sfc")]
    [InlineData(null, "Game (Japan).bps", "fr", "Game (Japan) [fr].sfc")]
    [InlineData(null, "patch.bps", "Game Redux", "Game (Japan) [Redux].sfc")]
    [InlineData(null, "Game (Japan) (Hack).bps", "Game", "Game (Japan) [Hack].sfc")]
    [InlineData(null, "SMB3_eng.bps", "Hacks", "Game (Japan) [SMB3_eng].sfc")]
    [InlineData("patch.zip", "patch.ips", null, "Game (Japan) [patch].sfc")]
    public void PatchedName_ShouldTakeTheLabelOfTheArchiveOrTheFolderOfAGenericPatch(
        string? archive, string patchName, string? folderLabel, string expected)
    {
        var patch = archive is null
            ? new PatchFile(Path.Combine("patches", patchName))
            : new PatchFile(Path.Combine("patches", archive), patchName);

        PatchMatcher.PatchedName(patch, "Game (Japan).sfc", 0x47e70e08, folderLabel, NamingConvention.NoIntro).Should().Be(expected);
    }

    [Theory]
    [InlineData("Alpha (1990)(Ocean)[h Hack].bps", "Alpha (1990)(Ocean)[h Hack].adf")]
    [InlineData("Alpha (1990)(Ocean)[tr en].ips", "Alpha (1990)(Ocean)[tr en].adf")]
    [InlineData("Alpha - Fix.bps", "Alpha (1990)(Ocean)[Fix].adf")]
    public void PatchedName_ShouldWriteTheLabelAsAFlagOfTosec(string patchName, string expected)
    {
        var patch = new PatchFile(Path.Combine("patches", patchName));

        PatchMatcher.PatchedName(patch, "Alpha (1990)(Ocean).adf", 0x47e70e08, folderLabel: null, NamingConvention.Tosec).Should().Be(expected);
    }

    [Fact]
    public void PatchedName_ShouldKeepTheFolderOfTheNameOfItsOrigin()
    {
        var patch = new PatchFile(Path.Combine("patches", "Game (T-En).bps"));

        PatchMatcher.PatchedName(patch, "sub/Game (Japan).sfc", 0x47e70e08, folderLabel: null, NamingConvention.NoIntro).Should().Be("sub/Game (Japan) [T-En].sfc");
    }

    [Fact]
    public async Task TheFolderOfAPatch_ShouldGiveItsLabelOnlyWhenItHoldsNoOtherPatch()
    {
        var dat = WriteJapanAndUsa();
        _temp.CreateSubdirectory("patches/Game Redux");
        _temp.CreateSubdirectory("patches/SNES");
        _temp.WriteBytes("patches/Game Redux/patch.bps", BpsLinear(Japan, Translated));
        _temp.WriteBytes("patches/SNES/patch.ups", Ups(Usa, Translate(Usa)));
        _temp.WriteBytes("patches/SNES/Other (USA) (T-Fr).ups", Ups(Data(9, 64), Data(10, 64)));

        var (result, _) = await SimulateAsync(Config(dat));

        result.Items.Where(it => it.Patch is not null).Select(it => it.TargetFilename)
            .Should().BeEquivalentTo("Game (Japan) [Redux].sfc", "Game (USA) [patch].sfc");
    }

    // =========================================================================
    // The texts that come with an IPS
    // =========================================================================

    /// <summary>
    /// A readme in the form romhacking.net writes its "ROM / ISO Information" block, with the SHA-1 and the CRC32 of the
    /// original ROM, a date of 8 digits, and the CRC32 of the patched result.
    /// </summary>
    private static string Readme(uint originCrc, uint? patchedCrc = null) =>
        $"""
        Game - English translation
        Released 20210222

        ROM / ISO Information:
          Database match: Game (Japan)
          Database: No-Intro: Nintendo - Super Nintendo Entertainment System (v. 20210222-050638)
          File/ROM SHA-1: 4BEB8E5A4E1CE8B8D2D0BC2E4F7F0A4B1E3C9D21
          File/ROM CRC32: {originCrc:X8}
        {(patchedCrc is { } crc ? $"Patched ROM CRC32: {crc:X8}" : string.Empty)}
        """;

    private string WriteZip(string relativePath, params (string Member, byte[] Data)[] members)
    {
        var path = Path.GetFullPath(_temp.GetFilePath(relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (member, data) in members)
        {
            using var stream = zip.CreateEntry(member).Open();
            stream.Write(data);
        }

        return path;
    }

    [Fact]
    public async Task AnIpsWithAReadmeOfItsName_ShouldFindItsOriginByTheCrcOfTheReadme()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (Japan) (T-En).ips", IpsDiff(Japan, Translated));
        _temp.WriteText("patches/Game (Japan) (T-En).txt", Readme(Crc(Japan), patchedCrc: Crc(Translated)));
        _temp.WriteBytes("patches/Other (USA) (T-Fr).bps", BpsLinear(Usa, Translate(Usa)));

        var (result, _) = await SimulateAsync(Config(dat));

        var patched = result.Items.Single(it => it.Patch?.Format == PatchFormat.Ips);
        patched.Patch!.Origin.DisplayName.Should().Be("Game (Japan).sfc");
        patched.Patch.CrcSource.Should().Be(PatchCrcSource.Notes);
        patched.Patch.Note!.DisplayName.Should().Be("Game (Japan) (T-En).txt");
        patched.TargetFilename.Should().Be("Game (Japan) [T-En].sfc");
        result.UnappliedPatches.Should().BeEmpty("the CRC32 of the patched result and the date match no file, and are ignored");
    }

    [Fact]
    public async Task AFrenchReadmeInUtf8_ShouldGiveTheCrcOfItsLine()
    {
        // The form of a French translation: UTF-8 without byte order mark, the CRC32 on a line of its own, and other lines
        // that name the CRC32 without giving one, or give 8 digits without the word
        _temp.WriteBytes("src/Golvellius (USA, Europe).sms", Usa);
        var dat = WriteDat(("Golvellius (USA, Europe)", "Golvellius (USA, Europe).sms", Usa));
        _temp.WriteBytes("patches/Golvellius (USA, Europe)_Fr-1.1-Clem/Golvellius (USA, Europe)_Fr-1.1-Clem.ips", IpsDiff(Usa, Translate(Usa)));
        _temp.WriteText(
            "patches/Golvellius (USA, Europe)_Fr-1.1-Clem/lisezmoi.txt",
            $"""
            traduction depuis le japonais de Golvellius sur Master System par Clem version 1.1

            ---- utilisation du patch
            - CRC32 de la rom à rechercher : {Crc(Usa):X8}
            - c'est la version américaine/européenne du jeu : Golvellius (US, EU).sms
            - pour voir le CRC32 d'un fichier, installer 7-Zip et faire un clic droit 7Zip>CRC-SHA>CRC-32
            - code d'invincibilité 77383400
            """,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var (result, _) = await SimulateAsync(Config(dat));

        var patched = PatchedVersion(result);
        patched.Patch!.Note!.DisplayName.Should().Be("lisezmoi.txt");
        patched.TargetFilename.Should().Be("Golvellius (USA, Europe) [Fr-1.1-Clem].sms");
        result.UnappliedPatches.Should().BeEmpty();
    }

    [Fact]
    public async Task AnIpsAloneInItsFolder_ShouldTakeAnyReadmeOfTheFolder()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game Redux/patch.ips", IpsDiff(Usa, Translate(Usa)));
        _temp.WriteText("patches/Game Redux/readme.TXT", Readme(Crc(Usa)));

        var (result, _) = await SimulateAsync(Config(dat));

        var patched = PatchedVersion(result);
        patched.Patch!.Origin.DisplayName.Should().Be("Game (USA).sfc");
        patched.Patch.Note!.DisplayName.Should().Be("readme.TXT");
        patched.TargetFilename.Should().Be("Game (USA) [Redux].sfc");
    }

    [Fact]
    public async Task AReadmeAmongSeveralPatches_ShouldComeWithNone()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (T-En).ips", IpsDiff(Japan, Translated));
        _temp.WriteBytes("patches/Game (Hack).ips", IpsDiff(Usa, Translate(Usa)));
        _temp.WriteText("patches/readme.txt", Readme(Crc(Japan)));

        var (result, _) = await SimulateAsync(Config(dat));

        result.Items.Should().NotContain(it => it.Patch != null);
        result.UnappliedPatches.Select(u => u.Reason.Key).Should().Equal("reason.patch_no_crc", "reason.patch_no_crc");
    }

    [Fact]
    public async Task AReadmeOfItsName_ShouldComeWithItsPatchAmongOthers()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (T-En).ips", IpsDiff(Japan, Translated));
        _temp.WriteBytes("patches/Game (Hack).ips", IpsDiff(Usa, Translate(Usa)));
        _temp.WriteText("patches/game (t-en).nfo", Readme(Crc(Japan)));

        var (result, _) = await SimulateAsync(Config(dat));

        PatchedVersion(result).Patch!.File.DisplayName.Should().Be("Game (T-En).ips");
        result.UnappliedPatches.Should().ContainSingle().Which.File.DisplayName.Should().Be("Game (Hack).ips");
    }

    [Fact]
    public async Task AReadmeInTheArchiveOfItsPatch_ShouldGiveTheCrc()
    {
        var dat = WriteJapanAndUsa();
        var zip = WriteZip(
            "patches/Game (T-En).zip",
            ("Game (T-En)/Game.ips", IpsDiff(Japan, Translated)),
            ("Game (T-En)/Readme.nfo", Ascii(Readme(Crc(Japan)))));

        var (result, _) = await SimulateAsync(Config(dat));

        var version = PatchedVersion(result).Patch!;
        version.Origin.DisplayName.Should().Be("Game (Japan).sfc");
        version.Note.Should().Be(new PatchFile(zip, "Game (T-En)/Readme.nfo"));
    }

    [Fact]
    public async Task AReadmeOnTheDisk_ShouldNotComeWithAPatchInAnArchive()
    {
        var dat = WriteJapanAndUsa();
        WriteZip("patches/translation.zip", ("Game.ips", IpsDiff(Japan, Translated)));
        _temp.WriteText("patches/readme.txt", Readme(Crc(Japan)));

        var (result, _) = await SimulateAsync(Config(dat));

        result.UnappliedPatches.Should().ContainSingle().Which.Reason.Key.Should().Be("reason.patch_no_crc");
    }

    [Fact]
    public async Task TheNameOfAnIps_ShouldPrevailOverItsReadme()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes($"patches/Game (T-En) [{Crc(Japan):x8}].ips", IpsDiff(Japan, Translated));
        _temp.WriteText("patches/readme.txt", Readme(Crc(Usa)));

        var (result, _) = await SimulateAsync(Config(dat));

        var version = PatchedVersion(result).Patch!;
        version.Origin.DisplayName.Should().Be("Game (Japan).sfc");
        version.CrcSource.Should().Be(PatchCrcSource.FileName);
        version.Note.Should().BeNull();
    }

    [Fact]
    public async Task AUps_ShouldNotReadItsReadme()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (T-En).ups", Ups(Japan, Translated));
        _temp.WriteText("patches/Game (T-En).txt", Readme(Crc(Usa)));

        var (result, _) = await SimulateAsync(Config(dat));

        var version = PatchedVersion(result).Patch!;
        version.Origin.DisplayName.Should().Be("Game (Japan).sfc");
        version.CrcSource.Should().Be(PatchCrcSource.Recorded);
        version.Note.Should().BeNull();
    }

    [Fact]
    public async Task AReadmeThatGivesTwoRoms_ShouldLeaveThePatchOut()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (T-En).ips", IpsDiff(Japan, Translated));
        _temp.WriteText("patches/Game (T-En).txt", $"CRC32 (Japan): {Crc(Japan):X8}\nCRC32 (USA): {Crc(Usa):X8}\n");

        var (result, _) = await SimulateAsync(Config(dat));

        var reason = result.UnappliedPatches.Should().ContainSingle().Which.Reason;
        reason.Key.Should().Be("reason.patch_ambiguous_note");
        reason.Args["note"].Should().Be("Game (T-En).txt");
    }

    [Fact]
    public async Task AReadmeThatGivesNoRomOfTheSource_ShouldLeaveThePatchOut()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (T-En).ips", IpsDiff(Japan, Translated));
        _temp.WriteText("patches/Game (T-En).txt", Readme(0x12345678));

        var (result, _) = await SimulateAsync(Config(dat));

        var reason = result.UnappliedPatches.Should().ContainSingle().Which.Reason;
        reason.Key.Should().Be("reason.patch_orphan_note");
        reason.Args["note"].Should().Be("Game (T-En).txt");
        reason.Args["crc"].Should().Be("12345678");
    }

    [Theory]
    [InlineData(false)] // the DAT gives the data without the header: the classification removes it
    [InlineData(true)] // the DAT gives the whole file
    public async Task AReadmeThatGivesAFileWithAndWithoutItsHeader_ShouldLeaveThePatchOut(bool datGivesTheWholeFile)
    {
        var data = Data(3, 8192);
        var file = WithInesHeader(data);
        _temp.WriteBytes("src/Game (USA).nes", file);
        var dat = WriteDat(("Game (USA)", "Game (USA).nes", datGivesTheWholeFile ? file : data));
        _temp.WriteBytes("patches/Game (Hack).ips", IpsDiff(data, Translate(data)));
        _temp.WriteText("patches/Game (Hack).txt", $"File CRC32: {Crc(file):X8}\nROM CRC32: {Crc(data):X8}\n");

        var (result, _) = await SimulateAsync(Config(dat));

        result.Items.Should().NotContain(it => it.Patch != null);
        var unapplied = result.UnappliedPatches.Should().ContainSingle().Which;
        unapplied.Reason.Key.Should().Be("reason.patch_header_form_ambiguous");
        unapplied.Reason.Args["origin"].Should().Be("Game (USA).nes");
        unapplied.Suggestions.Select(s => (s.Crc32, s.Offset, s.WithCopierHeader)).Should().Equal(
            [(Crc(file), 0L, (bool?)true), (Crc(data), 16L, (bool?)false)],
            "both forms of the file are proposed, the whole one first");
        unapplied.Suggestions.Should().OnlyContain(s => s.Origin.DisplayName == "Game (USA).nes");
    }

    [Fact]
    public async Task AReadmeThatGivesOneFormOfAFileWithAHeader_ShouldGiveThatForm()
    {
        var data = Data(3, 8192);
        var file = WithInesHeader(data);
        _temp.WriteBytes("src/Game (USA).nes", file);
        var dat = WriteDat(("Game (USA)", "Game (USA).nes", data));
        _temp.WriteBytes("patches/Game (Hack).ips", IpsDiff(file, Translate(file)));
        _temp.WriteText("patches/Game (Hack).txt", $"File CRC32: {Crc(file):X8}\n");

        var (result, _) = await SimulateAsync(Config(dat));

        var version = PatchedVersion(result).Patch!;
        version.SourceOffset.Should().Be(0, "the readme gives the CRC32 of the whole file");
        version.SourceCrc32.Should().Be(Crc(file));
    }

    [Fact]
    public async Task AReadmeTooLarge_ShouldBeIgnored()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (T-En).ips", IpsDiff(Japan, Translated));
        _temp.WriteText("patches/Game (T-En).txt", Readme(Crc(Japan)) + new string(' ', PatchService.MaxNoteSize));

        var (result, _) = await SimulateAsync(Config(dat));

        result.UnappliedPatches.Should().ContainSingle().Which.Reason.Key.Should().Be("reason.patch_no_crc");
    }

    [Theory]
    [InlineData("File/ROM CRC32: 65D0A825", new uint[] { 0x65d0a825 })]
    [InlineData("CRC32 (no header) : 0x65d0a825\r", new uint[] { 0x65d0a825 })]
    [InlineData("crc: 65d0a825, sha-1: 4BEB8E5A4E1CE8B8D2D0BC2E4F7F0A4B1E3C9D21", new uint[] { 0x65d0a825 })]
    [InlineData("Header CRC32 = 12345678 / no header: 9ABCDEF0", new uint[] { 0x12345678, 0x9abcdef0 })]
    [InlineData("65D0A825 CRC32", new uint[0])] // before the word
    [InlineData("Released 20210222, version 1.0", new uint[0])] // a line without the word
    [InlineData("CRC32: 65D0A825F", new uint[0])] // 9 digits
    public void NoteCrcs_ShouldReadTheCrcAfterTheWordOnItsLine(string line, uint[] expected)
    {
        PatchMatcher.NoteCrcs($"Game\n{line}\nEnd").Should().Equal(expected);
    }

    // =========================================================================
    // Origins proposed for an IPS without CRC32
    // =========================================================================

    private static IEnumerable<string> Suggested(SortExecutionResult result, string patchName) =>
        result.UnappliedPatches.Single(u => u.File.DisplayName == patchName).Suggestions.Select(s => s.Origin.DisplayName);

    /// <summary>Releases of their own data, each in the source and in the DAT.</summary>
    private string WriteReleases(params string[] names) =>
        WriteDat([.. names.Select((name, i) =>
        {
            var data = Data(100 + i, 512);
            _temp.WriteBytes($"src/{name}.sfc", data);
            return (name, $"{name}.sfc", data);
        })]);

    [Fact]
    public async Task AnIpsWithoutCrc_ShouldProposeTheFilesItsNameStartsWith_AndApplyNone()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (Japan) (T-Fr).ips", IpsDiff(Japan, Translated));

        var (result, _) = await SimulateAsync(Config(dat));

        result.Items.Should().NotContain(it => it.Patch != null, "a suggestion is never applied");
        Suggested(result, "Game (Japan) (T-Fr).ips").Should().Equal(
            ["Game (Japan).sfc", "Game (USA).sfc"],
            "the file of the whole name comes before the one of the title alone");
        var suggestion = result.UnappliedPatches.Single().Suggestions[0];
        suggestion.Crc32.Should().Be(Crc(Japan));
        suggestion.Offset.Should().Be(0);
        suggestion.WithCopierHeader.Should().BeNull();
    }

    [Fact]
    public async Task ANameWithUnderscores_ShouldProposeTheFilesOfItsTitle_TheWrittenOneFirst()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/game_fr.ips", IpsDiff(Japan, Translated));

        var (result, _) = await SimulateAsync(Config(dat));

        Suggested(result, "game_fr.ips").Should().Equal(["Game (USA).sfc", "Game (Japan).sfc"], "the 1G1R keeps the American release");
    }

    [Fact]
    public async Task AGenericName_ShouldGiveWayToTheFolderOrTheArchiveOfThePatch()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game Redux/patch.ips", IpsDiff(Usa, Translate(Usa)));
        WriteZip("patches/Game (Japan) (T-En).zip", ("v1.1.ips", IpsDiff(Japan, Translated)));

        var (result, _) = await SimulateAsync(Config(dat));

        Suggested(result, "patch.ips").Should().Equal(["Game (USA).sfc", "Game (Japan).sfc"]);
        Suggested(result, "v1.1.ips").Should().Equal(["Game (Japan).sfc", "Game (USA).sfc"]);
    }

    [Fact]
    public async Task AFolderOfSeveralPatches_ShouldProposeNothing()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game/patch.ips", IpsDiff(Japan, Translated));
        _temp.WriteBytes("patches/Game/fix.ips", IpsDiff(Usa, Translate(Usa)));

        var (result, _) = await SimulateAsync(Config(dat));

        result.UnappliedPatches.Should().HaveCount(2).And.OnlyContain(u => u.Suggestions.Count == 0);
    }

    [Fact]
    public async Task TheLongestTitle_ShouldComeFirst()
    {
        var dat = WriteReleases("Super Mario Bros (USA)", "Super Mario Bros 3 (USA)", "Super Mario Land (World)");
        _temp.WriteBytes("patches/Super Mario Bros 3 (T-Fr).ips", IpsDiff(Japan, Translated));

        var (result, _) = await SimulateAsync(Config(dat));

        Suggested(result, "Super Mario Bros 3 (T-Fr).ips").Should().Equal(["Super Mario Bros 3 (USA).sfc", "Super Mario Bros (USA).sfc"]);
    }

    [Fact]
    public async Task ATranslatedTitle_ShouldProposeNothing()
    {
        // The name of the patch is not that of the release: only the user can tell
        var dat = WriteReleases("Super Mario Bros. (World)");
        _temp.WriteBytes("patches/Super Mario Frères.ips", IpsDiff(Japan, Translated));

        var (result, _) = await SimulateAsync(Config(dat));

        Suggested(result, "Super Mario Frères.ips").Should().BeEmpty();
    }

    [Fact]
    public async Task TheSuggestions_ShouldBeFiveAtMost()
    {
        var dat = WriteReleases([.. Enumerable.Range(1, PatchMatcher.MaxSuggestions + 2).Select(i => $"Game (USA) (Rev {i})")]);
        _temp.WriteBytes("patches/Game (T-En).ips", IpsDiff(Japan, Translated));

        var (result, _) = await SimulateAsync(Config(dat));

        Suggested(result, "Game (T-En).ips").Should().HaveCount(PatchMatcher.MaxSuggestions);
    }
}
