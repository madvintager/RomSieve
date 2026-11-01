using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Cache of the headers of archives and CHDs (<c>header_cache.json</c>, next to <c>crc_cache.json</c>): an unchanged
/// file is not opened again, even by another run or another process; a changed file is read again; failures are
/// never cached.
/// </summary>
public sealed class HeaderCacheTests : IDisposable
{
    private readonly TempDirectoryFixture _tmp = new();
    private readonly CoreTestServices _services = new();
    private readonly CountingChecksumService _checksum = new();
    private readonly string _cachePath;

    public HeaderCacheTests()
    {
        _cachePath = _tmp.GetFilePath(Path.Combine("cache", HeaderCacheService.FileName));
    }

    public void Dispose() => _tmp.Dispose();

    private string Zip(string relativePath, params (string Member, string Content)[] members)
    {
        var path = _tmp.GetFilePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (member, content) in members)
        {
            using var stream = zip.CreateEntry(member).Open();
            stream.Write(Encoding.ASCII.GetBytes(content));
        }
        return path;
    }

    [Fact]
    public void ArchiveMembers_ShouldBeReadOnceThenServedByTheCache()
    {
        var zip = Zip("game.zip", ("a.rom", "AAAA"), ("dir/b.rom", "BBBBBB"));
        var cache = new HeaderCacheService(_cachePath);

        var first = cache.GetArchiveMembers(zip, _checksum);
        var second = cache.GetArchiveMembers(zip, _checksum);

        _checksum.ArchiveReads.Should().Be(1);
        second.Should().Equal(first);
        second.Should().Equal(_services.ChecksumService.GetArchiveMembers(zip));
        second.Should().Contain(new ArchiveMember(zip, "a.rom", 4, Crc32.HashToUInt32("AAAA"u8).ToString("x8")));
    }

    [Fact]
    public async Task ArchiveVerification_ShouldBeCachedUntilTheArchiveChanges()
    {
        var zip = Zip("game.zip", ("a.rom", "AAAA"));
        var cache = new HeaderCacheService(_cachePath);

        cache.FindDamagedMembers(zip, _checksum).Should().BeEmpty();
        cache.FindDamagedMembers(zip, _checksum).Should().BeEmpty();
        _checksum.Verifications.Should().Be(1, "an intact archive is cached too");

        // Another instance, as after a restart
        await cache.SaveAsync();
        var reloaded = new HeaderCacheService(_cachePath);
        reloaded.FindDamagedMembers(zip, _checksum).Should().BeEmpty();
        _checksum.Verifications.Should().Be(1);

        // A changed archive is checked again
        Zip("game.zip", ("a.rom", "AAAA"), ("b.rom", "BB"));
        File.SetLastWriteTimeUtc(zip, DateTime.UtcNow.AddMinutes(1));
        reloaded.FindDamagedMembers(zip, _checksum).Should().BeEmpty();
        _checksum.Verifications.Should().Be(2);
    }

    [Fact]
    public async Task DamagedMembers_ShouldBeKeptByTheCache()
    {
        var zip = _tmp.GetFilePath("damaged.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var stream = archive.CreateEntry("a.rom", CompressionLevel.NoCompression).Open();
            stream.Write("GOOD DATA OF THE ROM"u8);
        }

        var bytes = File.ReadAllBytes(zip);
        bytes[bytes.AsSpan().IndexOf("GOOD DATA OF THE ROM"u8)] ^= 0xFF;
        File.WriteAllBytes(zip, bytes);
        var cache = new HeaderCacheService(_cachePath);
        var damaged = cache.FindDamagedMembers(zip, _checksum);
        await cache.SaveAsync();

        var reloaded = new HeaderCacheService(_cachePath).FindDamagedMembers(zip, _checksum);

        damaged.Should().ContainSingle().Which.MemberName.Should().Be("a.rom");
        reloaded.Should().Equal(damaged);
        _checksum.Verifications.Should().Be(1);
    }

    [Fact]
    public void FailedVerification_ShouldNotBeCached()
    {
        var notAnArchive = _tmp.WriteText("broken.7z", "not an archive");
        var cache = new HeaderCacheService(_cachePath);

        for (var i = 0; i < 2; i++)
        {
            var act = () => cache.FindDamagedMembers(notAnArchive, _checksum);
            act.Should().Throw<Exception>();
        }

        _checksum.Verifications.Should().Be(2);
    }

    [Fact]
    public async Task VerifiedRun_ShouldOnlyDecompressTheArchivesChangedSinceTheLastOne()
    {
        Zip(Path.Combine("src", "pack1.zip"), ("Game A (1990)(Ocean).adf", "AAAA"));
        Zip(Path.Combine("src", "pack2.zip"), ("Game B (1991)(Ocean).adf", "BBBB"));
        var config = new SortConfig
        {
            SourceDir = _tmp.GetFilePath("src"),
            DestinationDir = _tmp.GetFilePath("dst"),
            VerifyArchiveContents = true
        };

        await CreateProcessor().ExecuteAsync(config);
        _checksum.Verifications.Should().Be(2);

        _checksum.Reset();
        await CreateProcessor().ExecuteAsync(config);
        _checksum.Verifications.Should().Be(0, "both archives were checked by the previous run");

        Zip(Path.Combine("src", "pack2.zip"), ("Game B (1991)(Ocean).adf", "BBBB"), ("Game C (1992)(Ocean).adf", "CC"));
        File.SetLastWriteTimeUtc(_tmp.GetFilePath(Path.Combine("src", "pack2.zip")), DateTime.UtcNow.AddMinutes(1));
        _checksum.Reset();
        await CreateProcessor().ExecuteAsync(config);
        _checksum.Verifications.Should().Be(1);
    }

    [Fact]
    public async Task SavedCache_ShouldServeAnotherInstance()
    {
        var zip = Zip("game.zip", ("a.rom", "AAAA"));
        var chd = _tmp.WriteBytes("disk.chd", ChdTestFile.Create("disk"));
        var cache = new HeaderCacheService(_cachePath);
        var members = cache.GetArchiveMembers(zip, _checksum);
        var info = cache.ReadChdInfo(chd, _checksum);
        await cache.SaveAsync();

        // A new run, or a new process
        var reloaded = new HeaderCacheService(_cachePath);
        reloaded.GetArchiveMembers(zip, _checksum).Should().Equal(members);
        reloaded.ReadChdInfo(chd, _checksum).Should().BeEquivalentTo(info);

        _checksum.ArchiveReads.Should().Be(1);
        _checksum.ChdReads.Should().Be(1);
        info.Tracks.Should().HaveCount(2, "the tracks are cached with the header");
        Directory.GetFiles(Path.GetDirectoryName(_cachePath)!).Should().Equal([_cachePath], "no temporary file is left");
    }

    [Fact]
    public void ChangedArchive_ShouldBeReadAgain()
    {
        var zip = Zip("game.zip", ("a.rom", "AAAA"));
        var cache = new HeaderCacheService(_cachePath);
        cache.GetArchiveMembers(zip, _checksum);

        Zip("game.zip", ("a.rom", "AAAA"), ("b.rom", "BBBB"));
        var members = cache.GetArchiveMembers(zip, _checksum);

        _checksum.ArchiveReads.Should().Be(2);
        members.Select(m => m.MemberName).Should().Equal("a.rom", "b.rom");
    }

    [Fact]
    public void UnreadableFiles_ShouldNeverBeCached()
    {
        // An unreadable archive has no members: it may only have been out of reach for a moment (a network share)
        var notAZip = _tmp.WriteText("broken.zip", "not a zip");
        var badChd = _tmp.WriteText("broken.chd", "not a chd");
        var cache = new HeaderCacheService(_cachePath);

        cache.GetArchiveMembers(notAZip, _checksum).Should().BeEmpty();
        cache.GetArchiveMembers(notAZip, _checksum).Should().BeEmpty();
        var readChd = () => cache.ReadChdInfo(badChd, _checksum);
        readChd.Should().Throw<InvalidDataException>();
        readChd.Should().Throw<InvalidDataException>();

        _checksum.ArchiveReads.Should().Be(2);
        _checksum.ChdReads.Should().Be(2);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"format":99,"archives":{}}""")]
    [InlineData("""{"format":1,"archives":{"x":[1]}}""")]
    [InlineData("[]")]
    public void UnusableCacheFile_ShouldGiveAnEmptyCache(string content)
    {
        var zip = Zip("game.zip", ("a.rom", "AAAA"));
        _tmp.WriteText(Path.Combine("cache", HeaderCacheService.FileName), content);

        new HeaderCacheService(_cachePath).GetArchiveMembers(zip, _checksum).Should().ContainSingle();
        _checksum.ArchiveReads.Should().Be(1);
    }

    [Fact]
    public async Task CrcCache_ShouldKeepTheHeaderCacheNextToItsFile()
    {
        var zip = Zip("game.zip", ("a.rom", "AAAA"));
        var crcCache = new CrcCacheService(_tmp.GetFilePath(Path.Combine("cache", "crc_cache.json")));

        crcCache.Headers.GetArchiveMembers(zip, _checksum);
        await crcCache.SaveAsync();

        File.Exists(_cachePath).Should().BeTrue();
        new CrcCacheService(_tmp.GetFilePath(Path.Combine("cache", "crc_cache.json"))).Headers
            .GetArchiveMembers(zip, _checksum).Should().ContainSingle();
        _checksum.ArchiveReads.Should().Be(1);
    }

    [Fact]
    public async Task SecondMameRun_ShouldOpenNoUnchangedArchiveNorChd()
    {
        var source = _tmp.CreateSubdirectory("src");
        Zip(Path.Combine("src", "game1.zip"), ("game1.rom", "GAME ONE"));
        Zip(Path.Combine("src", "game2.zip"), ("game2.rom", "GAME TWO"));
        _tmp.WriteBytes(Path.Combine("src", "game2", "game2.chd"), ChdTestFile.Create("game2"));
        var dat = _tmp.WriteText("mame.xml", $"""
            <?xml version="1.0"?>
            <mame build="0.289 (test)">
            	<machine name="game1"><description>Game One</description><rom name="game1.rom" size="8" crc="{Crc("GAME ONE")}"/><driver status="good"/></machine>
            	<machine name="game2"><description>Game Two</description><rom name="game2.rom" size="8" crc="{Crc("GAME TWO")}"/><disk name="game2" sha1="{ChdTestFile.Sha1Of("game2")}"/><driver status="good"/></machine>
            </mame>
            """);
        var config = new SortConfig { SourceDir = source, DestinationDir = _tmp.GetFilePath("dst"), DatFile = dat, Pattern = string.Empty };

        var first = await CreateProcessor().ExecuteAsync(config);
        _checksum.ArchiveReads.Should().Be(2);
        _checksum.ChdReads.Should().Be(1);

        // Another run, as after a restart: nothing unchanged is opened, and the result is the same
        _checksum.Reset();
        var second = await CreateProcessor().ExecuteAsync(config);
        _checksum.ArchiveReads.Should().Be(0);
        _checksum.ChdReads.Should().Be(0);
        second.Items.Select(Describe).Should().Equal(first.Items.Select(Describe));
        second.Items.Should().OnlyContain(it => it.Status == FileStatus.Valid && it.MameSet!.IsComplete);

        // Only the changed archive is read again
        _checksum.Reset();
        Zip(Path.Combine("src", "game1.zip"), ("game1.rom", "GAME ONE"), ("readme.txt", "extra"));
        await CreateProcessor().ExecuteAsync(config);
        _checksum.ArchiveReads.Should().Be(1);
        _checksum.ChdReads.Should().Be(0);
    }

    private static string Crc(string content) => Crc32.HashToUInt32(Encoding.ASCII.GetBytes(content)).ToString("x8");

    private static string Describe(ProcessItem item) =>
        $"{Path.GetFileName(item.SourcePath)}|{item.Status}|{item.DatGameName}|{item.TargetFilename}|{item.MameSet}";

    private SortProcessorService CreateProcessor() => new(
        _checksum,
        _services.DatService,
        _services.NoIntroParser,
        _services.FilterService,
        _services.Organizer,
        _services.I18n,
        _services.OpticalCompanionService,
        _services.MameParser,
        new CrcCacheService(_tmp.GetFilePath(Path.Combine("cache", "crc_cache.json"))));

    /// <summary>The real checksum service, counting the archive and CHD headers it reads.</summary>
    private sealed class CountingChecksumService : IChecksumService
    {
        private readonly ChecksumService _inner = new();
        private int _archiveReads;
        private int _chdReads;
        private int _verifications;

        public int ArchiveReads => _archiveReads;
        public int ChdReads => _chdReads;
        public int Verifications => _verifications;

        public void Reset()
        {
            _archiveReads = 0;
            _chdReads = 0;
            _verifications = 0;
        }

        public IReadOnlyList<ArchiveMember> GetArchiveMembers(string archivePath)
        {
            Interlocked.Increment(ref _archiveReads);
            return _inner.GetArchiveMembers(archivePath);
        }

        public ChdInfo ReadChdInfo(string path)
        {
            Interlocked.Increment(ref _chdReads);
            return _inner.ReadChdInfo(path);
        }

        public IReadOnlyList<ArchiveMember> GetZipArchiveMembers(string zipPath)
        {
            Interlocked.Increment(ref _archiveReads);
            return _inner.GetZipArchiveMembers(zipPath);
        }

        public IReadOnlyList<DamagedArchiveMember> FindDamagedMembers(string archivePath, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _verifications);
            return _inner.FindDamagedMembers(archivePath, cancellationToken);
        }

        public int DetectCopierHeader(ReadOnlySpan<byte> headerBytes, long fileSize) => _inner.DetectCopierHeader(headerBytes, fileSize);

        public Task<string> ComputeFileCrc32Async(string path, long offset = 0, int chunkSize = 65536, CancellationToken cancellationToken = default) =>
            _inner.ComputeFileCrc32Async(path, offset, chunkSize, cancellationToken);

        public Task<(string Crc32, string? Md5, string? Sha1)> ComputeFileChecksumsAsync(
            string path, long offset = 0, int chunkSize = 65536, bool computeHashes = true, CancellationToken cancellationToken = default) =>
            _inner.ComputeFileChecksumsAsync(path, offset, chunkSize, computeHashes, cancellationToken);

        public Task<string> ComputeFileSha1Async(string path, long offset = 0, CancellationToken cancellationToken = default) =>
            _inner.ComputeFileSha1Async(path, offset, cancellationToken);

        public Task<string> ComputeArchiveMemberSha1Async(string archivePath, string memberName, CancellationToken cancellationToken = default) =>
            _inner.ComputeArchiveMemberSha1Async(archivePath, memberName, cancellationToken);

        public bool IsZipArchive(string path) => _inner.IsZipArchive(path);

        public bool IsSupportedArchive(string path) => _inner.IsSupportedArchive(path);

        public ArchiveMemberStream OpenArchiveMember(string archivePath, string memberName) => _inner.OpenArchiveMember(archivePath, memberName);
    }
}
