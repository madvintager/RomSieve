using System.Collections.Concurrent;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// The list of a folder or of an archive of DATs (a pack of thousands of TOSEC DATs) is read fast and once: an archive is
/// opened once per thread that reads it, not once (twice) per DAT, and what the header of each DAT tells is kept in
/// <c>dat_header_cache.json</c>, so that a simulation after the list, or the list after a restart, reads no DAT again
/// but those it sorts. A DAT changed since, a cache of another build or a corrupted cache read the DATs again.
/// </summary>
public sealed class DatHeaderCacheTests : IDisposable
{
    private static readonly byte[] Rom = Encoding.ASCII.GetBytes("SOME ROM");

    private readonly TempDirectoryFixture _tmp = new();
    private readonly string _cachePath;

    public DatHeaderCacheTests() => _cachePath = _tmp.GetFilePath(Path.Combine("cache", DatHeaderCacheService.FileName));

    public void Dispose() => _tmp.Dispose();

    private static string Dat(string name, string game = "Game (World)") => $"""
        <?xml version="1.0"?>
        <datafile>
        	<header><name>{name}</name><description>{name}</description><homepage>No-Intro</homepage></header>
        	<game name="{game}"><description>{game}</description><rom name="{game}.bin" size="{Rom.Length}" crc="{Crc32.HashToUInt32(Rom):x8}"/></game>
        </datafile>
        """;

    private static string DatName(int index) => $"Maker - System {index:00}";

    // A ZIP of DATs, each member named after its DAT
    private string WriteZip(string name, params string[] datNames)
    {
        var path = _tmp.GetFilePath(name);
        File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var datName in datNames)
        {
            using var stream = zip.CreateEntry($"{datName}.dat").Open();
            stream.Write(Encoding.UTF8.GetBytes(Dat(datName)));
        }

        return path;
    }

    private (DatService Service, CountingChecksumService Checksums) CreateService(string? build = null)
    {
        var checksums = new CountingChecksumService();
        var cache = build is null ? new DatHeaderCacheService(_cachePath) : new DatHeaderCacheService(_cachePath, build);
        return (new DatService(checksums, cache), checksums);
    }

    // =========================================================================
    // An archive opened once
    // =========================================================================

    [Fact]
    public async Task ArchiveOfDats_ShouldBeOpenedOncePerThread_NotOncePerDat()
    {
        var names = Enumerable.Range(0, 40).Select(DatName).ToArray();
        var zip = WriteZip("pack.zip", names);
        var (service, checksums) = CreateService();

        service.ReadDatFolderPreview(zip).Select(e => e.Name).Should().Equal(names);
        checksums.ArchiveOpenings.Should().BeInRange(1, 8, "the DATs are read by eight threads at most, each with its own opening");
        checksums.MemberOpenings.Should().Be(0, "no DAT opens the archive by itself");

        var (unCached, counted) = CreateService(build: "another build");
        var lot = await unCached.ParseDatFileAsync(zip, new DatFolderOptions(LoadOnDemand: true));
        lot.Dats!.Select(d => d.Name).Should().Equal(names);
        counted.ArchiveOpenings.Should().BeInRange(1, 8, "the headers then the DATs of a lot are read through the same openings");
        counted.MemberOpenings.Should().Be(0);

        lot.DatFor(names[7])!.Games.Should().ContainSingle("a DAT read again on demand opens the archive by itself");
        counted.MemberOpenings.Should().BePositive();
    }

    // =========================================================================
    // Headers kept
    // =========================================================================

    [Fact]
    public void ListOfAnArchive_ShouldOpenNoDatAgain_AfterARestart()
    {
        var zip = WriteZip("pack.zip", DatName(1), DatName(2), DatName(3));
        var first = CreateService().Service.ReadDatFolderPreview(zip);
        File.Exists(_cachePath).Should().BeTrue("the headers read are written at once");

        var (restarted, checksums) = CreateService();
        var again = restarted.ReadDatFolderPreview(zip);

        again.Should().Equal(first);
        checksums.ReadMembers.Should().BeEmpty("every DAT of the archive is unchanged");
    }

    [Fact]
    public void ListOfAFolder_ShouldReadNoDatAgain_WhileItKeepsItsDateAndSize()
    {
        var folder = _tmp.CreateSubdirectory("dats");
        var dat = _tmp.WriteText(Path.Combine("dats", "a.dat"), Dat("Maker - Alpha"));
        _tmp.WriteText(Path.Combine("dats", "b.dat"), Dat("Maker - Beta"));
        CreateService().Service.ReadDatFolderPreview(folder);

        // Another content of the same size and date: only a DAT that is not read again still says its first name
        var date = File.GetLastWriteTimeUtc(dat);
        File.WriteAllText(dat, Dat("Maker - Gamma"));
        File.SetLastWriteTimeUtc(dat, date);

        CreateService().Service.ReadDatFolderPreview(folder).Select(e => e.Name).Should().Equal("Maker - Alpha", "Maker - Beta");
    }

    [Fact]
    public async Task SimulationAfterTheList_ShouldOnlyReadTheDatsItSorts()
    {
        var zip = WriteZip("pack.zip", DatName(1), DatName(2), DatName(3));
        var (service, checksums) = CreateService();
        service.ReadDatFolderPreview(zip);
        checksums.Reset();

        var lot = await service.ParseDatFileAsync(zip, new DatFolderOptions(ExcludedDats: [DatName(1), DatName(3)], LoadOnDemand: false));

        lot.Dats!.Select(d => d.Name).Should().Equal(DatName(2));
        checksums.ReadMembers.Distinct().Should().Equal($"{DatName(2)}.dat");
    }

    // =========================================================================
    // Headers read again
    // =========================================================================

    [Fact]
    public void DatChanged_ShouldBeReadAgain_InAFolderAsInAnArchive()
    {
        var folder = _tmp.CreateSubdirectory("dats");
        var dat = _tmp.WriteText(Path.Combine("dats", "a.dat"), Dat("Maker - Alpha"));
        var zip = WriteZip("pack.zip", DatName(1), DatName(2));
        CreateService().Service.ReadDatFolderPreview(folder);
        CreateService().Service.ReadDatFolderPreview(zip);

        File.WriteAllText(dat, Dat("Maker - Alpha, revised"));
        var path = _tmp.GetFilePath("pack.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            archive.GetEntry($"{DatName(2)}.dat")!.Delete();
            using var stream = archive.CreateEntry($"{DatName(2)}.dat").Open();
            stream.Write(Encoding.UTF8.GetBytes(Dat(DatName(2), game: "Another Game (World)")));
        }

        var (service, checksums) = CreateService();
        service.ReadDatFolderPreview(folder).Single().Name.Should().Be("Maker - Alpha, revised");
        service.ReadDatFolderPreview(zip).Select(e => e.Name).Should().Equal(DatName(1), DatName(2));
        checksums.ReadMembers.Distinct().Should().Equal(new[] { $"{DatName(2)}.dat" }, "only the member whose CRC32 changed is read again");
    }

    [Fact]
    public void CacheOfAnotherBuild_OrCorrupted_ShouldBeIgnored()
    {
        var zip = WriteZip("pack.zip", DatName(1), DatName(2));
        CreateService(build: "one").Service.ReadDatFolderPreview(zip);

        var (otherBuild, counted) = CreateService(build: "two");
        otherBuild.ReadDatFolderPreview(zip).Select(e => e.Name).Should().Equal(DatName(1), DatName(2));
        counted.ReadMembers.Should().HaveCount(4, "another build may tell the conventions otherwise: each DAT is read again (its format, then its header)");

        File.WriteAllText(_cachePath, "{ not JSON");
        var (corrupted, read) = CreateService(build: "two");
        corrupted.ReadDatFolderPreview(zip).Select(e => e.Name).Should().Equal(DatName(1), DatName(2));
        read.ReadMembers.Should().HaveCount(4);
    }

    [Fact]
    public void UnreadableDat_ShouldNotBeCached()
    {
        var folder = _tmp.CreateSubdirectory("dats");
        var dat = _tmp.WriteText(Path.Combine("dats", "a.dat"), Dat("Maker - Alpha"));
        var cache = new DatHeaderCacheService(_cachePath);

        FluentActions.Invoking(() => cache.GetOrRead(dat, () => throw new InvalidDataException("unreadable"))).Should().Throw<InvalidDataException>();
        cache.Save();

        File.Exists(_cachePath).Should().BeFalse("nothing was read");
        cache.GetOrRead(dat, () => new DatHeaderInfo("Maker - Alpha", NamingConvention.NoIntro)).Name.Should().Be("Maker - Alpha");
    }

    /// <summary>The real checksum service, counting the openings of archives and the members read through them.</summary>
    private sealed class CountingChecksumService : IChecksumService
    {
        private readonly ChecksumService _inner = new();
        private int _archiveOpenings;
        private int _memberOpenings;

        public int ArchiveOpenings => _archiveOpenings;

        public int MemberOpenings => _memberOpenings;

        /// <summary>Members read through an archive opened once, in no particular order.</summary>
        public ConcurrentBag<string> ReadMembers { get; private set; } = [];

        public void Reset()
        {
            _archiveOpenings = 0;
            _memberOpenings = 0;
            ReadMembers = [];
        }

        public IArchiveReader OpenArchive(string archivePath)
        {
            Interlocked.Increment(ref _archiveOpenings);
            return new CountingReader(_inner.OpenArchive(archivePath), this);
        }

        public ArchiveMemberStream OpenArchiveMember(string archivePath, string memberName)
        {
            Interlocked.Increment(ref _memberOpenings);
            return _inner.OpenArchiveMember(archivePath, memberName);
        }

        public IReadOnlyList<ArchiveMember> GetArchiveMembers(string archivePath) => _inner.GetArchiveMembers(archivePath);

        public ChdInfo ReadChdInfo(string path) => _inner.ReadChdInfo(path);

        public IReadOnlyList<ArchiveMember> GetZipArchiveMembers(string zipPath) => _inner.GetZipArchiveMembers(zipPath);

        public IReadOnlyList<DamagedArchiveMember> FindDamagedMembers(string archivePath, CancellationToken cancellationToken = default) =>
            _inner.FindDamagedMembers(archivePath, cancellationToken);

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

        private sealed class CountingReader(IArchiveReader inner, CountingChecksumService owner) : IArchiveReader
        {
            public Stream OpenMember(string memberName)
            {
                owner.ReadMembers.Add(memberName);
                return inner.OpenMember(memberName);
            }

            public void Dispose() => inner.Dispose();
        }
    }
}
