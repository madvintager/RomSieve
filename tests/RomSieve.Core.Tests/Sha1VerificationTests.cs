using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Optional SHA-1 verification: a file identified by CRC32 and size keeps its DAT match only when its SHA-1 is the
/// one of the DAT (when the DAT provides one), and the CRC cache keeps the SHA-1 without breaking its historical format.
/// </summary>
public sealed class Sha1VerificationTests : IDisposable
{
    private const string GameName = "Alpha (1990)(Ocean)";
    private const string RomName = GameName + ".adf";

    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");

    public void Dispose() => _temp.Dispose();

    private static readonly byte[] Content = Encoding.ASCII.GetBytes("REDUMP-TRACK-CONTENT");

    private static string Crc(byte[] data) => Crc32.HashToUInt32(data).ToString("x8", CultureInfo.InvariantCulture);

    private static string Sha1(byte[] data) => Convert.ToHexStringLower(SHA1.HashData(data));

    private static string Dat(params (string Game, string Rom, byte[] Data, string? Sha1)[] roms) => $"""
        <?xml version="1.0"?>
        <datafile>
          <header><name>Commodore Amiga - Games - [ADF] (TOSEC)</name><description>TOSEC test DAT</description></header>
        {string.Concat(roms.Select(r => $"""
          <game name="{r.Game}"><description>{r.Game}</description>
            <rom name="{r.Rom}" size="{r.Data.Length}" crc="{Crc(r.Data)}"{(r.Sha1 is null ? string.Empty : $" sha1=\"{r.Sha1}\"")}/>
          </game>

        """))}
        </datafile>
        """;

    private SortProcessorService CreateProcessor(string cachePath) => new(
        _services.ChecksumService,
        _services.DatService,
        _services.NoIntroParser,
        _services.FilterService,
        _services.Organizer,
        _services.I18n,
        _services.OpticalCompanionService,
        _services.MameParser,
        new CrcCacheService(cachePath));

    private async Task<SortExecutionResult> SimulateAsync(string dat, bool verifySha1, string? cachePath = null) =>
        await CreateProcessor(cachePath ?? _temp.GetFilePath(Path.Combine("cache", Guid.NewGuid().ToString("N") + ".json")))
            .ExecuteAsync(new SortConfig
            {
                SourceDir = _temp.GetFilePath("src"),
                DestinationDir = _temp.GetFilePath("dst"),
                DatFile = _temp.WriteText("test.dat", dat),
                Pattern = string.Empty,
                VerifySha1 = verifySha1
            }, isDryRun: true);

    [Fact]
    public async Task MatchingSha1_ShouldKeepTheMatch()
    {
        _temp.WriteBytes(Path.Combine("src", RomName), Content);

        var result = await SimulateAsync(Dat((GameName, RomName, Content, Sha1(Content))), verifySha1: true);

        var item = result.Items.Single();
        item.Status.Should().Be(FileStatus.Valid);
        item.DatGameName.Should().Be(GameName);
        result.Stats.DatMatchedCount.Should().Be(1);
    }

    [Fact]
    public async Task DifferentSha1_ShouldLoseTheMatch()
    {
        _temp.WriteBytes(Path.Combine("src", RomName), Content);
        var datSha1 = new string('a', 40);

        var result = await SimulateAsync(Dat((GameName, RomName, Content, datSha1)), verifySha1: true);

        var item = result.Items.Single();
        item.Status.Should().Be(FileStatus.Unmatched);
        item.StatusReason!.Key.Should().Be("reason.sha1_mismatch");
        item.StatusReason.Args["expected"].Should().Be(datSha1);
        item.StatusReason.Args["actual"].Should().Be(Sha1(Content));
        item.Sha1.Should().Be(Sha1(Content));
        result.Stats.DatMatchedCount.Should().Be(0);
        result.MissingRoms.Should().ContainSingle(r => r.Name == RomName);
        _services.I18n.Format(item.StatusReason).Should().StartWith("CRC32 recognized, but SHA-1 different from the DAT");
    }

    [Fact]
    public async Task DifferentSha1_WithoutVerification_ShouldKeepTheDefaultBehavior()
    {
        _temp.WriteBytes(Path.Combine("src", RomName), Content);

        var result = await SimulateAsync(Dat((GameName, RomName, Content, new string('a', 40))), verifySha1: false);

        result.Items.Single().Status.Should().Be(FileStatus.Valid, "the SHA-1 is only checked when the option is enabled");
    }

    [Fact]
    public async Task DatWithoutSha1_ShouldKeepTheCrcMatchWithoutComputingIt()
    {
        _temp.WriteBytes(Path.Combine("src", RomName), Content);
        var cachePath = _temp.GetFilePath(Path.Combine("cache", "nosha1.json"));

        var result = await SimulateAsync(Dat((GameName, RomName, Content, null)), verifySha1: true, cachePath);

        result.Items.Single().Status.Should().Be(FileStatus.Valid);
        new CrcCacheService(cachePath).GetSha1(_temp.GetFilePath(Path.Combine("src", RomName))).Should().BeNull();
    }

    [Fact]
    public async Task SameCrcInTwoEntries_ShouldIdentifyTheFileWithTheEntryOfItsSha1()
    {
        // Two DAT entries share size and CRC32 (a collision) but not their SHA-1: the SHA-1 picks the right one
        _temp.WriteBytes(Path.Combine("src", "dump.adf"), Content);
        var dat = Dat(
            ("Other (1994)(Ocean)", "Other (1994)(Ocean).adf", Content, new string('b', 40)),
            (GameName, RomName, Content, Sha1(Content)));

        var withoutVerification = await SimulateAsync(dat, verifySha1: false);
        var withVerification = await SimulateAsync(dat, verifySha1: true);

        withoutVerification.Items.Single().DatGameName.Should().Be("Other (1994)(Ocean)", "the first entry in DAT order wins on CRC alone");
        var item = withVerification.Items.Single();
        item.DatGameName.Should().Be(GameName);
        item.TargetFilename.Should().Be(RomName);
        withVerification.MissingRoms.Should().ContainSingle(r => r.Name == "Other (1994)(Ocean).adf");
    }

    [Fact]
    public async Task ZipMember_ShouldBeDecompressedToVerifyItsSha1()
    {
        _temp.CreateSubdirectory("src");
        using (var zip = ZipFile.Open(_temp.GetFilePath(Path.Combine("src", "disc.zip")), ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry(RomName).Open();
            stream.Write(Content);
        }

        var matching = await SimulateAsync(Dat((GameName, RomName, Content, Sha1(Content))), verifySha1: true);
        var different = await SimulateAsync(Dat((GameName, RomName, Content, new string('c', 40))), verifySha1: true);

        matching.Items.Single().Status.Should().Be(FileStatus.Valid);
        different.Items.Single().Status.Should().Be(FileStatus.Unmatched);
        different.Items.Single().StatusReason!.Key.Should().Be("reason.sha1_mismatch");
    }

    [Fact]
    public async Task HeaderedFile_ShouldBeVerifiedWithoutItsCopierHeader()
    {
        var rom = Encoding.ASCII.GetBytes(new string('R', 4096));
        byte[] header = [(byte)'N', (byte)'E', (byte)'S', 0x1A, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        _temp.WriteBytes(Path.Combine("src", "Game (1990)(Nintendo).nes"), [.. header, .. rom]);

        var result = await SimulateAsync(Dat(("Game (1990)(Nintendo)", "Game (1990)(Nintendo).nes", rom, Sha1(rom))), verifySha1: true);

        result.Items.Single().Status.Should().Be(FileStatus.Valid);
    }

    [Fact]
    public async Task Sha1_ShouldBeCachedWithTheCrcAndReusedWhileTheFileIsUnchanged()
    {
        var file = _temp.WriteBytes(Path.Combine("src", RomName), Content);
        var cachePath = _temp.GetFilePath(Path.Combine("cache", "sha1.json"));

        await SimulateAsync(Dat((GameName, RomName, Content, Sha1(Content))), verifySha1: true, cachePath);

        var cache = new CrcCacheService(cachePath);
        cache.Get(file).Should().Be(Crc(Content));
        cache.GetSha1(file).Should().Be(Sha1(Content));

        // A modified file invalidates both
        File.AppendAllText(file, "!");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        cache.GetSha1(file).Should().BeNull();
    }

    [Fact]
    public async Task CacheFile_ShouldReadHistoricalEntriesAndKeepThemUnchanged()
    {
        var oldFile = _temp.WriteBytes("old.bin", [1, 2, 3]);
        var newFile = _temp.WriteBytes("new.bin", [4, 5, 6]);
        var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(oldFile)).ToUnixTimeMilliseconds() / 1000.0;
        var cachePath = _temp.WriteText("crc_cache.json", JsonSerializer.Serialize(new Dictionary<string, object[]>
        {
            [Path.GetFullPath(oldFile)] = [mtime, 3L, "55bc801d"]
        }));

        var cache = new CrcCacheService(cachePath);
        cache.Get(oldFile).Should().Be("55bc801d", "a historical three-element entry is still read");
        cache.GetSha1(oldFile).Should().BeNull();

        cache.SetSha1(newFile, "0bc2e8f4", new string('d', 40));
        await cache.SaveAsync();

        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(cachePath));
        saved.RootElement.GetProperty(Path.GetFullPath(oldFile)).GetArrayLength().Should().Be(3, "an entry without SHA-1 keeps the historical format");
        saved.RootElement.GetProperty(Path.GetFullPath(newFile)).GetArrayLength().Should().Be(4);
        new CrcCacheService(cachePath).GetSha1(newFile).Should().Be(new string('d', 40));
    }

    [Fact]
    public void SortConfig_ShouldNotVerifySha1ByDefault()
    {
        new SortConfig { SourceDir = "a", DestinationDir = "b" }.VerifySha1.Should().BeFalse();
    }
}
