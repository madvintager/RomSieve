using System.IO.Compression;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using static RomSieve.Core.Tests.Fixtures.PatchTestEncoder;

namespace RomSieve.Core.Tests;

/// <summary>
/// The writing of the patched versions by real sorts: each transfer mode (a copy whatever the mode, before a move
/// takes its origin, in an archive of its own in ZIP mode), the collisions, the origin that keeps its name, a failure
/// or a cancellation in the middle, a result whose checksum is wrong, the verification of the copies, an origin in an
/// archive or behind a copier header, an unsafe patch name, the room counted, and the journal and its undo.
/// </summary>
public sealed class PatchTransferTests : IDisposable
{
    private readonly CoreTestServices _services = new();
    private readonly TempDirectoryFixture _temp = new();
    private readonly CrcCacheService _crcCache;
    private readonly OperationJournalService _journal;
    private readonly SortProcessorService _processor;
    private readonly string _src;
    private readonly string _dst;
    private readonly string _patches;

    private static readonly byte[] Japan = Data(1, 4096);
    private static readonly byte[] Usa = Data(2, 4096);
    private static readonly byte[] Translated = Translate(Japan);
    private static readonly byte[] UsaHack = Translate(Usa);

    public PatchTransferTests()
    {
        _crcCache = new CrcCacheService(_temp.GetFilePath(Path.Combine("cache", "crc_cache.json")));
        _journal = new OperationJournalService(_temp.GetFilePath("journals"), _services.ChecksumService, _crcCache);
        _processor = new SortProcessorService(
            _services.ChecksumService, _services.DatService, _services.NoIntroParser, _services.FilterService,
            _services.Organizer, _services.I18n, _services.OpticalCompanionService, _services.MameParser, _services.Patches,
            _crcCache, _journal);
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

    /// <summary>The Japanese and American releases of a game, the American one kept by the 1G1R, and a patch of each.</summary>
    private string WriteCollection()
    {
        _temp.WriteBytes("src/Game (Japan).sfc", Japan);
        _temp.WriteBytes("src/Game (USA).sfc", Usa);
        _temp.WriteBytes("patches/Game (Japan) (T-En).bps", BpsLinear(Japan, Translated));
        return WriteDat(("Game (Japan)", "Game (Japan).sfc", Japan), ("Game (USA)", "Game (USA).sfc", Usa));
    }

    private SortConfig Config(string dat, TransferMode mode = TransferMode.Copy, CollisionMode collision = CollisionMode.Rename) => new()
    {
        SourceDir = _src,
        DestinationDir = _dst,
        Pattern = string.Empty,
        TransferMode = mode,
        CollisionMode = collision,
        AppendToDestination = true,
        NamingConvention = NamingConvention.NoIntro,
        DatFile = dat,
        PatchDir = _patches,
        Filters = new FilterConfig { OneGameOneRom = true, PriorityLanguages = ["en", "ja"] }
    };

    private sealed class LogCollector : IProgress<LogMessage>
    {
        public List<LogMessage> Messages { get; } = [];
        public void Report(LogMessage value) => Messages.Add(value);
    }

    private async Task<SortExecutionResult> SortAsync(SortConfig config, LogCollector? log = null) =>
        await _processor.ExecuteAsync(config, isDryRun: false, logProgress: log);

    private string Destination(string name) => Path.Combine(_dst, name);

    /// <summary>The files of the destination, the marker and the quarantine apart, relative to it.</summary>
    private List<string> DestinationFiles() =>
        Directory.EnumerateFiles(_dst, "*", SearchOption.AllDirectories)
            .Where(f => !DestinationGuard.IsGuardPath(f))
            .Select(f => Path.GetRelativePath(_dst, f))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static ProcessItem PatchedVersion(SortExecutionResult result) => result.Items.Single(it => it.Patch is not null);

    private void AssertNoTemporaryFile() =>
        Directory.EnumerateFiles(_temp.Path, "*" + DestinationGuard.ReplacementExtension, SearchOption.AllDirectories)
            .Should().BeEmpty("no temporary file is left behind");

    // =========================================================================
    // Transfer modes
    // =========================================================================

    [Fact]
    public async Task Copy_ShouldWriteThePatchedVersionAndJournalIt()
    {
        var dat = WriteCollection();

        var result = await SortAsync(Config(dat));

        var patched = PatchedVersion(result);
        patched.Status.Should().Be(FileStatus.Valid);
        File.ReadAllBytes(Destination("Game (Japan) (T-En).sfc")).Should().Equal(Translated);
        patched.Crc32.Should().Be(Crc(Translated).ToString("x8"));
        DestinationFiles().Should().BeEquivalentTo(["Game (Japan) (T-En).sfc", "Game (USA).sfc"], "the Japanese release itself is left out by the 1G1R");
        File.ReadAllBytes(Path.Combine(_src, "Game (Japan).sfc")).Should().Equal(Japan, "the origin is never modified");
        File.Exists(Path.Combine(_patches, "Game (Japan) (T-En).bps")).Should().BeTrue();
        AssertNoTemporaryFile();

        var journal = File.ReadAllText(result.JournalPath!);
        journal.Should().Contain("\"kind\":\"Copy\"").And.Contain("\"patch\":");
    }

    [Fact]
    public async Task Undo_ShouldDeleteThePatchedVersion()
    {
        var dat = WriteCollection();
        var result = await SortAsync(Config(dat));

        var report = await _journal.UndoAsync(result.JournalPath!);

        report.Issues.Should().BeEmpty();
        DestinationFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Undo_ShouldKeepAPatchedVersionChangedSinceTheSort()
    {
        var dat = WriteCollection();
        var result = await SortAsync(Config(dat));
        File.WriteAllBytes(Destination("Game (Japan) (T-En).sfc"), Ascii("Edited by the user"));

        var report = await _journal.UndoAsync(result.JournalPath!);

        report.Issues.Should().ContainSingle(i => i.Kind == UndoIssueKind.TargetModified);
        File.ReadAllBytes(Destination("Game (Japan) (T-En).sfc")).Should().Equal(Ascii("Edited by the user"));
    }

    [Fact]
    public async Task Move_ShouldWriteThePatchedVersionBeforeItsOriginLeavesTheSource()
    {
        var dat = WriteCollection();
        _temp.WriteBytes("patches/Game (USA) (Hack).ups", Ups(Usa, UsaHack));

        var result = await SortAsync(Config(dat, TransferMode.Move));

        result.Items.Where(it => it.Patch is not null).Should().OnlyContain(it => it.Status == FileStatus.Valid);
        File.ReadAllBytes(Destination("Game (USA) (Hack).sfc")).Should().Equal(UsaHack);
        File.ReadAllBytes(Destination("Game (USA).sfc")).Should().Equal(Usa);
        File.Exists(Path.Combine(_src, "Game (USA).sfc")).Should().BeFalse("the origin is moved, after its patched version");
        File.Exists(Path.Combine(_src, "Game (Japan).sfc")).Should().BeTrue("an origin the 1G1R leaves out stays in the source");
        File.ReadAllBytes(Destination("Game (Japan) (T-En).sfc")).Should().Equal(Translated);

        var report = await _journal.UndoAsync(result.JournalPath!);
        report.Issues.Should().BeEmpty();
        File.ReadAllBytes(Path.Combine(_src, "Game (USA).sfc")).Should().Equal(Usa);
        DestinationFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Zip_ShouldWriteThePatchedVersionInAnArchiveOfItsOwn()
    {
        var dat = WriteCollection();

        var result = await SortAsync(Config(dat, TransferMode.Zip));

        PatchedVersion(result).Status.Should().Be(FileStatus.Valid);
        using (var zip = ZipFile.OpenRead(Destination("Game (Japan) (T-En).zip")))
        {
            var entry = zip.Entries.Should().ContainSingle().Which;
            entry.FullName.Should().Be("Game (Japan) (T-En).sfc");
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            memory.ToArray().Should().Equal(Translated);
            zip.Comment.Should().StartWith("TORRENTZIPPED-");
        }

        AssertNoTemporaryFile();
        File.ReadAllText(result.JournalPath!).Should().Contain("\"kind\":\"ZipArchive\"").And.Contain("\"patch\":");
        (await _journal.UndoAsync(result.JournalPath!)).Issues.Should().BeEmpty();
        DestinationFiles().Should().BeEmpty();
    }

    [Theory]
    [InlineData(TransferMode.HardLink)]
    [InlineData(TransferMode.SymbolicLink)]
    public async Task Links_ShouldWriteThePatchedVersionAsACopy(TransferMode mode)
    {
        var dat = WriteCollection();
        var log = new LogCollector();

        var result = await SortAsync(Config(dat, mode), log);

        PatchedVersion(result).Status.Should().Be(FileStatus.Valid);
        var path = Destination("Game (Japan) (T-En).sfc");
        File.ReadAllBytes(path).Should().Equal(Translated);
        new FileInfo(path).LinkTarget.Should().BeNull("a link cannot hold modified data");
        log.Messages.Should().ContainSingle(m => m.Message.Key == "log.patched_written_as_copies");
    }

    // =========================================================================
    // Collisions
    // =========================================================================

    [Theory]
    [InlineData(CollisionMode.Rename)]
    [InlineData(CollisionMode.Skip)]
    [InlineData(CollisionMode.Overwrite)]
    public async Task APatchedVersionWithTheNameOfItsOrigin_ShouldBeRenamedInEveryMode(CollisionMode collision)
    {
        // A patch named as its ROM, as for the patches an emulator applies when it starts a game
        var dat = WriteCollection();
        _temp.WriteBytes("patches/Game (USA).ups", Ups(Usa, UsaHack));

        var result = await SortAsync(Config(dat, collision: collision));

        var patched = result.Items.Single(it => it.Patch is { } p && p.File.DisplayName == "Game (USA).ups");
        patched.Status.Should().Be(FileStatus.Valid, "the sort writes both files, whatever the collision mode");
        patched.FinalTargetPath.Should().Be(Destination("Game (USA)_01.sfc"));
        File.ReadAllBytes(Destination("Game (USA).sfc")).Should().Equal(Usa, "the origin keeps its name");
        File.ReadAllBytes(Destination("Game (USA)_01.sfc")).Should().Equal(UsaHack);
        AssertNoTemporaryFile();

        (await _journal.UndoAsync(result.JournalPath!)).Issues.Should().BeEmpty();
        DestinationFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task APatchedVersionWithTheNameOfAnOriginAlreadyInTheDestination_ShouldStillBeWritten()
    {
        // The origin is skipped as already there; its patched version is renamed, not lost with it
        var dat = WriteCollection();
        _temp.WriteBytes("patches/Game (USA).ups", Ups(Usa, UsaHack));
        File.WriteAllBytes(Destination("Game (USA).sfc"), Usa);

        var result = await SortAsync(Config(dat, collision: CollisionMode.Skip));

        result.Items.Single(it => it.DisplayName == "Game (USA).sfc").IsAlreadyInDestination.Should().BeTrue();
        File.ReadAllBytes(Destination("Game (USA)_01.sfc")).Should().Equal(UsaHack);
    }

    [Fact]
    public async Task TwoPatchedVersionsWithTheNameOfTheirOrigin_ShouldTakeTheNextSuffixes()
    {
        var dat = WriteCollection();
        _temp.CreateSubdirectory("patches/fr");
        _temp.WriteBytes("patches/Game (USA).ups", Ups(Usa, UsaHack));
        _temp.WriteBytes("patches/fr/Game (USA).bps", BpsLinear(Usa, Translate(UsaHack)));

        await SortAsync(Config(dat, collision: CollisionMode.Overwrite));

        File.ReadAllBytes(Destination("Game (USA).sfc")).Should().Equal(Usa);
        File.Exists(Destination("Game (USA)_01.sfc")).Should().BeTrue();
        File.Exists(Destination("Game (USA)_02.sfc")).Should().BeTrue();
    }

    [Theory]
    [InlineData(CollisionMode.Rename)]
    [InlineData(CollisionMode.Skip)]
    [InlineData(CollisionMode.Overwrite)]
    public async Task AFileAlreadyAtTheTarget_ShouldFollowTheCollisionMode(CollisionMode collision)
    {
        var dat = WriteCollection();
        var existing = Ascii("Already in the destination");
        File.WriteAllBytes(Destination("Game (Japan) (T-En).sfc"), existing);

        var result = await SortAsync(Config(dat, collision: collision));

        var patched = PatchedVersion(result);
        switch (collision)
        {
            case CollisionMode.Rename:
                File.ReadAllBytes(Destination("Game (Japan) (T-En).sfc")).Should().Equal(existing);
                File.ReadAllBytes(Destination("Game (Japan) (T-En)_01.sfc")).Should().Equal(Translated);
                break;
            case CollisionMode.Skip:
                patched.Status.Should().Be(FileStatus.Duplicate);
                patched.IsAlreadyInDestination.Should().BeTrue();
                File.ReadAllBytes(Destination("Game (Japan) (T-En).sfc")).Should().Equal(existing);
                break;
            default:
                File.ReadAllBytes(Destination("Game (Japan) (T-En).sfc")).Should().Equal(Translated);
                File.ReadAllText(result.JournalPath!).Should().Contain("\"replacedExistingFile\":true");
                break;
        }
    }

    // =========================================================================
    // Failures
    // =========================================================================

    [Fact]
    public async Task ACancellationInTheMiddle_ShouldLeaveNoFileUnderTheNameOfTheTarget()
    {
        var dat = WriteCollection();
        _processor.ConfigureTransferExecutor = executor => executor.WrapTargetStream =
            stream => new FailingStream(stream, () => new OperationCanceledException()) { FailAfterBytesWritten = 1000 };

        var act = () => SortAsync(Config(dat));

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(Destination("Game (Japan) (T-En).sfc")).Should().BeFalse();
        AssertNoTemporaryFile();
        File.ReadAllBytes(Path.Combine(_src, "Game (Japan).sfc")).Should().Equal(Japan);
    }

    [Fact]
    public async Task AFullDestination_ShouldStopTheSortWithoutATruncatedFile()
    {
        var dat = WriteCollection();
        _processor.ConfigureTransferExecutor = executor => executor.WrapTargetStream =
            stream => new FailingStream(stream, FailingStream.DiskFull) { FailAfterBytesWritten = 1000 };

        var result = await SortAsync(Config(dat));

        PatchedVersion(result).Status.Should().Be(FileStatus.Error);
        result.StopReason!.Key.Should().Be("log.sort_stopped_disk_full");
        File.Exists(Destination("Game (Japan) (T-En).sfc")).Should().BeFalse();
        AssertNoTemporaryFile();
    }

    [Fact]
    public async Task AWrongResultChecksum_ShouldPutThePatchedVersionInErrorWithNothingWritten()
    {
        _temp.WriteBytes("src/Game (Japan).sfc", Japan);
        var dat = WriteDat(("Game (Japan)", "Game (Japan).sfc", Japan));
        _temp.WriteBytes("patches/Game (Japan) (T-En).bps", Bps(
            (ulong)Japan.Length, (ulong)Japan.Length, [new SourceRead(Japan.Length)], Crc(Japan), targetCrc: 0x12345678));

        var result = await SortAsync(Config(dat));

        var patched = PatchedVersion(result);
        patched.Status.Should().Be(FileStatus.Error);
        patched.StatusReason!.Key.Should().Be("reason.patch_failed");
        patched.StatusReason.Args["error"].Should().BeOfType<LocalizedMessage>().Which.Key.Should().Be("patch.error_target_crc");
        File.Exists(Destination("Game (Japan) (T-En).sfc")).Should().BeFalse();
        AssertNoTemporaryFile();
    }

    [Fact]
    public async Task AnOriginChangedSinceItWasIdentified_ShouldBeRefusedEvenForAnIps()
    {
        var dat = WriteCollection();
        _temp.WriteBytes($"patches/Game (USA) (Hack) [{Crc(Usa):x8}].ips", IpsDiff(Usa, UsaHack));
        _processor.ConfigureTransferExecutor = executor => executor.WrapSourceStream =
            stream => stream is FileStream file && file.Name.Contains("Game (USA).sfc", StringComparison.Ordinal) ? new CorruptingStream(stream) : stream;

        var result = await SortAsync(Config(dat));

        var patched = result.Items.Single(it => it.Patch?.Format == PatchFormat.Ips);
        patched.StatusReason!.Args["error"].Should().BeOfType<LocalizedMessage>().Which.Key.Should().Be("patch.error_source_crc");
        File.Exists(Destination("Game (USA) (Hack).sfc")).Should().BeFalse();
    }

    [Fact]
    public async Task VerifiedCopies_ShouldRefuseAPatchedVersionDamagedOnTheDisk()
    {
        var dat = WriteCollection();
        var config = Config(dat);
        config.VerifyCopies = true;
        _processor.ConfigureTransferExecutor = executor => executor.AfterTargetWritten = path =>
        {
            if (path.Contains("(T-En)", StringComparison.Ordinal))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
                stream.WriteByte(0xFF);
            }
        };

        var result = await SortAsync(config);

        PatchedVersion(result).StatusReason!.Key.Should().Be("reason.copy_verification_failed");
        File.Exists(Destination("Game (Japan) (T-En).sfc")).Should().BeFalse();
        AssertNoTemporaryFile();
    }

    // =========================================================================
    // Origins
    // =========================================================================

    [Fact]
    public async Task AnOriginInAnArchive_ShouldBeExtractedForThePatchThenRemoved()
    {
        var zip = _temp.GetFilePath("src/Game (Japan).zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var stream = archive.CreateEntry("Game (Japan).sfc").Open();
            stream.Write(Japan);
        }

        var before = File.ReadAllBytes(zip);
        var dat = WriteDat(("Game (Japan)", "Game (Japan).sfc", Japan));
        _temp.WriteBytes("patches/Game (Japan) (T-En).bps", BpsLinear(Japan, Translated));

        var result = await SortAsync(Config(dat, TransferMode.Move));

        PatchedVersion(result).Status.Should().Be(FileStatus.Valid);
        File.ReadAllBytes(Destination("Game (Japan) (T-En).sfc")).Should().Equal(Translated);
        File.ReadAllBytes(zip).Should().Equal(before, "an archive stays in place, even in Move mode");
        AssertNoTemporaryFile();
    }

    [Fact]
    public async Task AnOriginWithACopierHeader_ShouldGiveTheResultOfThePatchWithoutTheHeader()
    {
        var data = Data(3, 8192);
        _temp.WriteBytes("src/Game (USA).nes", [.. Ascii("NES\u001a"), .. new byte[12], .. data]);
        var dat = WriteDat(("Game (USA)", "Game (USA).nes", data));
        var hacked = Translate(data);
        _temp.WriteBytes("patches/Game (USA) (Hack).bps", BpsLinear(data, hacked));

        var result = await SortAsync(Config(dat));

        PatchedVersion(result).Status.Should().Be(FileStatus.Valid);
        File.ReadAllBytes(Destination("Game (USA) (Hack).nes")).Should().Equal(hacked, "the result is written as the patch produces it");
    }

    // =========================================================================
    // Names, room
    // =========================================================================

    [Theory]
    [InlineData("../evil.bps")]
    [InlineData("C:evil.bps")]
    public async Task AnUnsafePatchName_ShouldBeRefusedFromTheSimulation(string member)
    {
        var dat = WriteCollection();
        File.Delete(Path.Combine(_patches, "Game (Japan) (T-En).bps"));
        using (var archive = ZipFile.Open(_temp.GetFilePath("patches/pack.zip"), ZipArchiveMode.Create))
        {
            using var stream = archive.CreateEntry(member).Open();
            stream.Write(BpsLinear(Japan, Translated));
        }

        var simulation = await _processor.ExecuteAsync(Config(dat), isDryRun: true);
        var sort = await SortAsync(Config(dat));

        foreach (var result in new[] { simulation, sort })
        {
            var patched = PatchedVersion(result);
            patched.Status.Should().Be(FileStatus.Error);
            patched.StatusReason!.Key.Should().Be("reason.unsafe_name");
            patched.FinalTargetPath.Should().BeNull();
        }

        Directory.EnumerateFiles(_temp.Path, "evil*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Theory]
    [InlineData(TransferMode.HardLink)]
    [InlineData(TransferMode.Move)]
    public async Task TheRoomNeeded_ShouldCountThePatchedVersionsInEveryMode(TransferMode mode)
    {
        var dat = WriteCollection();
        var config = Config(dat, mode);
        var simulation = await _processor.ExecuteAsync(config, isDryRun: true);

        // Same volume: the origins are only renamed or linked, the patched version is written
        var required = DestinationSpace.GetRequiredBytes(simulation.Items, config, [Path.GetPathRoot(_temp.Path)!]);

        required.Should().Be(Translated.Length);
    }

    /// <summary>A source that reads back other data than the file holds: a file changed since it was identified.</summary>
    private sealed class CorruptingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            for (var i = offset; i < offset + read; i++)
            {
                buffer[i] ^= 0x01;
            }

            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            var span = buffer.Span[..read];
            for (var i = 0; i < span.Length; i++)
            {
                span[i] ^= 0x01;
            }

            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
