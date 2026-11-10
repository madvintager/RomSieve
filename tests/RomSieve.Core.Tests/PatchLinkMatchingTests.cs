using System.IO.Compression;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using static RomSieve.Core.Tests.Fixtures.PatchTestEncoder;

namespace RomSieve.Core.Tests;

/// <summary>
/// The choices of the user followed by the matching of the patches (<see cref="PatchMatcher"/>, through a simulation):
/// the ROM chosen for an IPS before its name and its readme, a patch set aside whatever its format, a choice that follows
/// the content of the patch (renamed, moved, in an archive), a ROM chosen that is missing, the form of the ROM, the
/// option to write the patched versions only, the keys given to the results, and a file of choices that cannot be read.
/// </summary>
public sealed class PatchLinkMatchingTests : IDisposable
{
    private readonly CoreTestServices _services = new();
    private readonly TempDirectoryFixture _temp = new();
    private readonly string _src;
    private readonly string _dst;
    private readonly string _patches;

    private static readonly byte[] Japan = Data(1, 4096);
    private static readonly byte[] Usa = Data(2, 4096);
    private static readonly byte[] Translated = Translate(Japan);

    public PatchLinkMatchingTests()
    {
        _src = _temp.CreateSubdirectory("src");
        _dst = _temp.CreateSubdirectory("dst");
        _patches = _temp.CreateSubdirectory("patches");
    }

    public void Dispose() => _temp.Dispose();

    private static byte[] Data(int seed, int size)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static byte[] Translate(byte[] data)
    {
        var translated = data.Concat(Ascii("English")).ToArray();
        translated[10] ^= 0x55;
        translated[100] ^= 0x33;
        return translated;
    }

    private static byte[] WithInesHeader(byte[] data) => [.. Ascii("NES\u001a"), .. new byte[12], .. data];

    private string WriteDat(params (string Game, string Rom, byte[] Data)[] roms)
    {
        var games = string.Concat(roms.Select(r =>
            $"""<game name="{r.Game}"><description>{r.Game}</description><rom name="{r.Rom}" size="{r.Data.Length}" crc="{Crc(r.Data):x8}"/></game>"""));
        return _temp.WriteText(
            "dat/snes.dat",
            $"""<?xml version="1.0"?><datafile><header><name>Nintendo - Super Nintendo Entertainment System</name><homepage>No-Intro</homepage></header>{games}</datafile>""");
    }

    /// <summary>The Japanese and American releases of a game, the American one kept by the 1G1R.</summary>
    private string WriteJapanAndUsa()
    {
        _temp.WriteBytes("src/Game (Japan).sfc", Japan);
        _temp.WriteBytes("src/Game (USA).sfc", Usa);
        return WriteDat(("Game (Japan)", "Game (Japan).sfc", Japan), ("Game (USA)", "Game (USA).sfc", Usa));
    }

    private SortConfig Config(string dat, bool patchOnly = false) => new()
    {
        SourceDir = _src,
        DestinationDir = _dst,
        NamingConvention = NamingConvention.NoIntro,
        DatFile = dat,
        PatchDir = _patches,
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

    /// <summary>Records the choice of the user for the patch whose content is <paramref name="patch"/>.</summary>
    private async Task ChooseAsync(byte[] patch, PatchLink link)
    {
        var saved = await _services.PatchLinks.UpdateAsync([new(PatchKey.Of(patch), link)], (_, message) => throw new InvalidOperationException(message.Key));
        saved.Should().BeTrue();
    }

    private static PatchLink ToJapan => PatchLink.ToOrigin("any name", Crc(Japan), "Game (Japan).sfc");

    private static ProcessItem Named(SortExecutionResult result, string displayName) =>
        result.Items.Single(it => it.DisplayName == displayName);

    private static ProcessItem PatchedVersion(SortExecutionResult result) => result.Items.Single(it => it.Patch is not null);

    private string WriteZip(string relativePath, string member, byte[] data)
    {
        var path = Path.GetFullPath(_temp.GetFilePath(relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var stream = zip.CreateEntry(member).Open();
        stream.Write(data);
        return path;
    }

    private static string Readme(uint originCrc) =>
        $"""
        Game - English translation

        ROM / ISO Information:
          File/ROM CRC32: {originCrc:X8}
        """;

    // =========================================================================
    // The ROM chosen for an IPS
    // =========================================================================

    [Fact]
    public async Task AnIpsWithoutCrc_ShouldApplyToTheRomTheUserChose()
    {
        var dat = WriteJapanAndUsa();
        var patch = IpsDiff(Japan, Translated);
        _temp.WriteBytes("patches/Game (T-En).ips", patch);
        await ChooseAsync(patch, ToJapan);

        var (result, _) = await SimulateAsync(Config(dat));

        var version = PatchedVersion(result).Patch!;
        version.Origin.DisplayName.Should().Be("Game (Japan).sfc");
        version.CrcSource.Should().Be(PatchCrcSource.Link);
        version.Key.Should().Be(PatchKey.Of(patch));
        PatchedVersion(result).TargetFilename.Should().Be("Game (Japan) [T-En].sfc");
        result.UnappliedPatches.Should().BeEmpty();
        File.Exists(Path.Combine(_patches, "Game (T-En).ips")).Should().BeTrue("the patch is not renamed");
    }

    [Fact]
    public async Task TheChoiceOfTheUser_ShouldPrevailOverTheNameOfTheIps()
    {
        var dat = WriteJapanAndUsa();
        var patch = IpsDiff(Japan, Translated);
        _temp.WriteBytes($"patches/Game (T-En) [{Crc(Usa):x8}].ips", patch);
        await ChooseAsync(patch, ToJapan);

        var (result, _) = await SimulateAsync(Config(dat));

        var version = PatchedVersion(result).Patch!;
        version.Origin.DisplayName.Should().Be("Game (Japan).sfc");
        version.CrcSource.Should().Be(PatchCrcSource.Link);
    }

    [Fact]
    public async Task TheChoiceOfTheUser_ShouldPrevailOverTheReadmeOfTheIps()
    {
        var dat = WriteJapanAndUsa();
        var patch = IpsDiff(Japan, Translated);
        _temp.WriteBytes("patches/Game (T-En).ips", patch);
        _temp.WriteText("patches/Game (T-En).txt", Readme(Crc(Usa)));
        await ChooseAsync(patch, ToJapan);

        var (result, _) = await SimulateAsync(Config(dat));

        var version = PatchedVersion(result).Patch!;
        version.Origin.DisplayName.Should().Be("Game (Japan).sfc");
        version.CrcSource.Should().Be(PatchCrcSource.Link);
        version.Note.Should().BeNull();
    }

    [Fact]
    public async Task TheChoice_ShouldFollowThePatch_RenamedAndMoved()
    {
        var dat = WriteJapanAndUsa();
        var patch = IpsDiff(Japan, Translated);
        await ChooseAsync(patch, PatchLink.ToOrigin("Game (T-En).ips", Crc(Japan), "Game (Japan).sfc"));
        _temp.WriteBytes("patches/Translations/Jeu traduit.ips", patch);

        var (result, _) = await SimulateAsync(Config(dat));

        PatchedVersion(result).Patch!.Origin.DisplayName.Should().Be("Game (Japan).sfc");
    }

    [Fact]
    public async Task AnIpsInAnArchive_ShouldApplyToTheRomTheUserChose()
    {
        var dat = WriteJapanAndUsa();
        var patch = IpsDiff(Japan, Translated);
        var zip = WriteZip("patches/translations.zip", "Game (T-En).ips", patch);
        await ChooseAsync(patch, ToJapan);

        var (result, _) = await SimulateAsync(Config(dat));

        var version = PatchedVersion(result).Patch!;
        version.File.Should().Be(new PatchFile(zip, "Game (T-En).ips"));
        version.Origin.DisplayName.Should().Be("Game (Japan).sfc");
        version.CrcSource.Should().Be(PatchCrcSource.Link);
    }

    [Fact]
    public async Task TheRomChosen_WithoutItsCopierHeader_ShouldBeFoundInAFileIdentifiedWithIt()
    {
        // The DAT lists the whole file; the user chose the data after the header
        var data = Data(3, 8192);
        var file = WithInesHeader(data);
        _temp.WriteBytes("src/Game (USA).nes", file);
        var dat = WriteDat(("Game (USA)", "Game (USA).nes", file));
        var patch = IpsDiff(data, Translate(data));
        _temp.WriteBytes("patches/Game (USA) (Hack).ips", patch);
        await ChooseAsync(patch, PatchLink.ToOrigin("Game (USA) (Hack).ips", Crc(data), "Game (USA).nes"));

        var (result, _) = await SimulateAsync(Config(dat));

        var version = PatchedVersion(result).Patch!;
        version.SourceOffset.Should().Be(16);
        version.SourceCrc32.Should().Be(Crc(data));
        version.CrcSource.Should().Be(PatchCrcSource.Link);
    }

    [Fact]
    public async Task ARomChosenThatIsMissing_ShouldLeaveThePatchOut_AndProposeTheFilesOfItsName()
    {
        var dat = WriteJapanAndUsa();
        var patch = IpsDiff(Japan, Translated);
        _temp.WriteBytes("patches/Game (T-En).ips", patch);
        await ChooseAsync(patch, PatchLink.ToOrigin("Game (T-En).ips", 0x12345678, "Game (Europe).sfc"));

        var (result, _) = await SimulateAsync(Config(dat));

        result.Items.Should().NotContain(it => it.Patch != null);
        var unapplied = result.UnappliedPatches.Should().ContainSingle().Which;
        unapplied.Reason.Key.Should().Be("reason.patch_link_origin_missing");
        unapplied.Reason.Args["origin"].Should().Be("Game (Europe).sfc");
        unapplied.Key.Should().Be(PatchKey.Of(patch));
        unapplied.Suggestions.Select(s => s.Origin.DisplayName).Should().Contain("Game (Japan).sfc");
    }

    [Fact]
    public async Task ARomChosenForABps_ShouldBeLeftOut_ForTheRomItRecords()
    {
        var dat = WriteJapanAndUsa();
        var patch = BpsLinear(Japan, Translated);
        _temp.WriteBytes("patches/Game (T-En).bps", patch);
        await ChooseAsync(patch, PatchLink.ToOrigin("Game (T-En).bps", Crc(Usa), "Game (USA).sfc"));

        var (result, log) = await SimulateAsync(Config(dat));

        var version = PatchedVersion(result).Patch!;
        version.Origin.DisplayName.Should().Be("Game (Japan).sfc");
        version.CrcSource.Should().Be(PatchCrcSource.Recorded);
        log.Messages.Should().ContainSingle(m => m.Message.Key == "log.patch_link_recorded")
            .Which.Level.Should().Be(LogLevel.Warn);
    }

    // =========================================================================
    // A patch set aside
    // =========================================================================

    public static TheoryData<string, byte[]> PatchesOfEveryKind => new()
    {
        { $"Game (T-En) [{Crc(Japan):x8}].ips", IpsDiff(Japan, Translated) },
        { "Game (T-En).ups", Ups(Japan, Translated) },
        { "Game (T-En).bps", BpsLinear(Japan, Translated) },
        { "Game (T-En).ips", Ascii("not a patch at all") }
    };

    [Theory]
    [MemberData(nameof(PatchesOfEveryKind))]
    public async Task APatchSetAside_ShouldGiveNoPatchedVersion_WhateverItIs(string name, byte[] patch)
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes($"patches/{name}", patch);
        await ChooseAsync(patch, PatchLink.Ignore(name));

        var (result, log) = await SimulateAsync(Config(dat));

        result.Items.Should().NotContain(it => it.Patch != null);
        result.UnappliedPatches.Should().BeEmpty("a patch set aside is not a problem");
        var ignored = result.IgnoredPatches.Should().ContainSingle().Which;
        ignored.File.DisplayName.Should().Be(name);
        ignored.Key.Should().Be(PatchKey.Of(patch));
        result.Stats.IgnoredPatches.Should().Equal(result.IgnoredPatches);
        log.Messages.Should().Contain(m => m.Level == LogLevel.Info && m.Message.Key == "log.patch_ignored");
        log.Messages.Should().ContainSingle(m => m.Message.Key == "log.patches_summary_ignored");
    }

    [Fact]
    public async Task AnIpsOfAReadme_SetAside_ShouldGiveNoPatchedVersion()
    {
        var dat = WriteJapanAndUsa();
        var patch = IpsDiff(Japan, Translated);
        _temp.WriteBytes("patches/Game (T-En).ips", patch);
        _temp.WriteText("patches/Game (T-En).txt", Readme(Crc(Japan)));
        await ChooseAsync(patch, PatchLink.Ignore("Game (T-En).ips"));

        var (result, _) = await SimulateAsync(Config(dat));

        result.Items.Should().NotContain(it => it.Patch != null);
        result.IgnoredPatches.Should().ContainSingle();
    }

    [Fact]
    public async Task PatchOnly_WithThePatchSetAside_ShouldWriteTheReleaseKept()
    {
        var dat = WriteJapanAndUsa();
        var patch = BpsLinear(Usa, Translate(Usa));
        _temp.WriteBytes("patches/Game (USA) (Hack).bps", patch);
        await ChooseAsync(patch, PatchLink.Ignore("Game (USA) (Hack).bps"));

        var (result, _) = await SimulateAsync(Config(dat, patchOnly: true));

        var usa = Named(result, "Game (USA).sfc");
        usa.Status.Should().Be(FileStatus.Valid);
        usa.IsReplacedByPatch.Should().BeFalse();
    }

    [Fact]
    public async Task WithoutChoice_ThePatches_ShouldBeMatchedAsBefore_WithTheirKey()
    {
        var dat = WriteJapanAndUsa();
        var bps = BpsLinear(Japan, Translated);
        var ips = IpsDiff(Usa, Translate(Usa));
        _temp.WriteBytes("patches/Game (T-En).bps", bps);
        _temp.WriteBytes("patches/Game (USA) (Hack).ips", ips);

        var (result, log) = await SimulateAsync(Config(dat));

        var version = PatchedVersion(result).Patch!;
        version.CrcSource.Should().Be(PatchCrcSource.Recorded);
        version.Key.Should().Be(PatchKey.Of(bps));
        var unapplied = result.UnappliedPatches.Should().ContainSingle().Which;
        unapplied.Reason.Key.Should().Be("reason.patch_no_crc");
        unapplied.Key.Should().Be(PatchKey.Of(ips), "a choice can be recorded for it without reading it again");
        result.IgnoredPatches.Should().BeEmpty();
        log.Messages.Should().ContainSingle(m => m.Message.Key == "log.patches_summary");
        File.Exists(_services.PatchLinks.FilePath).Should().BeFalse("a simulation writes no choice");
    }

    [Fact]
    public async Task TheRecalculationOfAGame_ShouldKeepThePatchedVersionOfAChoice()
    {
        var dat = WriteJapanAndUsa();
        var patch = IpsDiff(Japan, Translated);
        _temp.WriteBytes("patches/Game (T-En).ips", patch);
        await ChooseAsync(patch, ToJapan);
        var config = Config(dat, patchOnly: true);
        var (result, _) = await SimulateAsync(config);
        string Describe() => string.Join("|", result.Items.Select(it => $"{it.DisplayName}:{it.Status}:{it.StatusReason?.Key}:{it.IsReplacedByPatch}"));
        var simulated = Describe();

        _services.Processor.RecalculateGameItems(result.Items.ToList(), config, result.DatCatalog, result.Items);

        Describe().Should().Be(simulated);
        Named(result, "Game (USA).sfc").IsReplacedByPatch.Should().BeTrue();
    }

    // =========================================================================
    // The file of the choices
    // =========================================================================

    [Fact]
    public async Task AFileOfChoicesThatCannotBeRead_ShouldBeSaidInTheLog_AndTheSimulationGoOn()
    {
        var dat = WriteJapanAndUsa();
        _temp.WriteBytes("patches/Game (T-En).bps", BpsLinear(Japan, Translated));
        File.WriteAllText(_services.PatchLinks.FilePath, "{ not json");

        var (result, log) = await SimulateAsync(Config(dat));

        log.Messages.Should().ContainSingle(m => m.Message.Key == "log.patch_links_unreadable_preserved")
            .Which.Level.Should().Be(LogLevel.Warn);
        PatchedVersion(result).Patch!.CrcSource.Should().Be(PatchCrcSource.Recorded);

        // The copy is in the isolated folder shared by the tests of the process
        File.Delete((string)log.Messages.Single(m => m.Message.Key == "log.patch_links_unreadable_preserved").Message.Args["copy"]!);
    }

    [Fact]
    public async Task WithoutPatchFolder_TheChoices_ShouldNotBeRead()
    {
        var dat = WriteJapanAndUsa();
        File.WriteAllText(_services.PatchLinks.FilePath, "{ not json");
        var config = Config(dat);
        config.PatchDir = string.Empty;

        var (_, log) = await SimulateAsync(config);

        log.Messages.Should().NotContain(m => m.Message.Key.StartsWith("log.patch_links_", StringComparison.Ordinal));
        File.ReadAllText(_services.PatchLinks.FilePath).Should().Be("{ not json");
        File.Delete(_services.PatchLinks.FilePath);
    }
}
