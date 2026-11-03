using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// ZIP transfer mode: one TorrentZip archive per item (a file, or a descriptor with its tracks), identical to those of igir
/// and RomVault, sources untouched, collisions resolved on the archive, cancellation and failures without partial archive,
/// verification of the content and of the signature, journal and undo.
/// </summary>
public sealed class ZipOutputTests : IDisposable
{
    private const string Alpha = "Alpha (1990)(Ocean).adf";
    private const string Cue = "Disc (1995)(Sony).cue";
    private const string Track = "Disc (1995)(Sony) (Track 1).bin";

    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");

    public void Dispose() => _temp.Dispose();

    private static readonly byte[] AlphaData = ArchiveFormatTests.Pattern("ALPHA-", 64 * 1024);

    private string Source(string name, byte[] data) => _temp.WriteBytes(Path.Combine("src", name), data);

    private SortConfig Config(string destination = "dst") => new()
    {
        SourceDir = _temp.GetFilePath("src"),
        DestinationDir = _temp.GetFilePath(destination),
        Pattern = string.Empty,
        TransferMode = TransferMode.Zip
    };

    private static Dictionary<string, byte[]> ReadZip(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var stream = e.Open();
            using var data = new MemoryStream();
            stream.CopyTo(data);
            return data.ToArray();
        });
    }

    private void AddDisc()
    {
        Source(Cue, Encoding.ASCII.GetBytes($"FILE \"{Track}\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n"));
        Source(Track, ArchiveFormatTests.Pattern("TRACK1", 4704));
    }

    [Fact]
    public async Task ZipMode_ShouldWriteOneArchivePerFileAndLeaveTheSource()
    {
        var source = Source(Alpha, AlphaData);

        var result = await _services.Processor.ExecuteAsync(Config(), isDryRun: false);

        var item = result.Items.Single();
        item.Status.Should().Be(FileStatus.Valid);
        item.FinalTargetPath.Should().Be(_temp.GetFilePath(Path.Combine("dst", "Alpha (1990)(Ocean).zip")));
        ReadZip(item.FinalTargetPath!).Should().ContainSingle().Which.Should().Match<KeyValuePair<string, byte[]>>(e => e.Key == Alpha && e.Value.SequenceEqual(AlphaData));
        File.ReadAllBytes(source).Should().Equal(AlphaData, "the ZIP mode copies: the source is untouched");

        using var zip = ZipFile.OpenRead(item.FinalTargetPath!);
        zip.Entries.Single().LastWriteTime.DateTime.Should().Be(new DateTime(1996, 12, 24, 23, 32, 0));
        zip.Entries.Single().CompressedLength.Should().BeLessThan(AlphaData.Length / 10, "the entry is compressed");
    }

    [Fact]
    public async Task OpticalDescriptor_ShouldShareItsArchiveWithItsTracks()
    {
        AddDisc();

        var result = await _services.Processor.ExecuteAsync(Config(), isDryRun: false);

        var cue = result.Items.Single();
        var zipPath = cue.FinalTargetPath!;
        Path.GetFileName(zipPath).Should().Be("Disc (1995)(Sony).zip");
        cue.RelatedItems.Single().FinalTargetPath.Should().Be(zipPath);
        var entries = ReadZip(zipPath);
        entries.Keys.Should().Equal(Track, Cue);
        entries[Track].Should().Equal(ArchiveFormatTests.Pattern("TRACK1", 4704));
        Encoding.ASCII.GetString(entries[Cue]).Should().Contain(Track, "the descriptor is stored unchanged");
        Directory.GetFiles(_temp.GetFilePath("dst"), "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != DestinationGuard.MarkerFileName).Should().ContainSingle();
    }

    [Fact]
    public async Task ArchiveMember_ShouldBeRecompressedIntoItsOwnArchive()
    {
        Directory.CreateDirectory(_temp.GetFilePath("src"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Archives", "roms_solid.7z"), _temp.GetFilePath(Path.Combine("src", "roms.7z")));

        var result = await _services.Processor.ExecuteAsync(Config(), isDryRun: false);

        result.Items.Should().OnlyContain(i => i.Status == FileStatus.Valid || i.Status == FileStatus.Unmatched);
        ReadZip(_temp.GetFilePath(Path.Combine("dst", "Alpha (1990)(Ocean).zip")))[Alpha].Should().Equal(ArchiveFormatTests.Pattern("ALPHA-", 8192));
    }

    [Fact]
    public async Task Archives_ShouldBeIdenticalFromOneSortToAnother()
    {
        var source = Source(Alpha, AlphaData);
        AddDisc();

        var first = await _services.Processor.ExecuteAsync(Config("dst1"), isDryRun: false);
        File.SetLastWriteTimeUtc(source, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await _services.Processor.ExecuteAsync(Config("dst2"), isDryRun: false);

        foreach (var archive in first.Items.Select(i => i.FinalTargetPath!))
        {
            var relative = Path.GetRelativePath(_temp.GetFilePath("dst1"), archive);
            File.ReadAllBytes(_temp.GetFilePath(Path.Combine("dst2", relative)))
                .Should().Equal(File.ReadAllBytes(archive), $"{relative} only depends on its content");
        }
    }

    [Theory]
    [InlineData(CollisionMode.Rename, "Alpha (1990)(Ocean)_01.zip", FileStatus.Valid)]
    [InlineData(CollisionMode.Skip, "Alpha (1990)(Ocean).zip", FileStatus.Duplicate)]
    [InlineData(CollisionMode.Overwrite, "Alpha (1990)(Ocean).zip", FileStatus.Valid)]
    public async Task ExistingArchive_ShouldFollowTheCollisionMode(CollisionMode mode, string expectedName, FileStatus expectedStatus)
    {
        Source(Alpha, AlphaData);
        var existing = _temp.WriteText(Path.Combine("dst", "Alpha (1990)(Ocean).zip"), "existing");
        var config = Config();
        config.CollisionMode = mode;
        config.AppendToDestination = true;

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        var item = result.Items.Single();
        item.Status.Should().Be(expectedStatus);
        Path.GetFileName(item.FinalTargetPath).Should().Be(expectedName);
        if (mode == CollisionMode.Skip)
        {
            File.ReadAllText(existing).Should().Be("existing");
        }
        else
        {
            ReadZip(item.FinalTargetPath!)[Alpha].Should().Equal(AlphaData);
        }
    }

    [Fact]
    public async Task CancelledSort_ShouldLeaveNoPartialArchive()
    {
        Source("Big Game (1990)(Ocean).adf", ArchiveFormatTests.Pattern("BIG---", 8 * 1024 * 1024));
        var config = Config();
        config.AppendToDestination = true;
        Directory.CreateDirectory(config.DestinationDir);
        using var cts = new CancellationTokenSource();
        var transferPhase = false;

        var act = () => _services.Processor.ExecuteAsync(
            config,
            isDryRun: false,
            progress: new SyncProgress<SortProgress>(_ => { if (transferPhase) cts.Cancel(); }),
            logProgress: new SyncProgress<LogMessage>(m => transferPhase |= m.Message.Key == "log.destination_append_mode"),
            cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Directory.GetFiles(config.DestinationDir).Where(f => Path.GetFileName(f) != DestinationGuard.MarkerFileName).Should().BeEmpty();
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    [Fact]
    public async Task ZipArchives_ShouldBeJournaledAndUndone()
    {
        var source = Source(Alpha, AlphaData);
        AddDisc();
        var crcCache = new CrcCacheService(_temp.GetFilePath(Path.Combine("cache", "crc_cache.json")));
        var journal = new OperationJournalService(_temp.GetFilePath("journals"), _services.ChecksumService, crcCache);
        var processor = new SortProcessorService(
            _services.ChecksumService, _services.DatService, _services.NoIntroParser, _services.FilterService,
            _services.Organizer, _services.I18n, _services.OpticalCompanionService, _services.MameParser, crcCache, journal);

        await processor.ExecuteAsync(Config(), isDryRun: false);
        var summary = journal.FindLastUndoableJournal()!;
        summary.OperationCount.Should().Be(3, "two archives and the marker of the new destination");

        var report = await journal.UndoAsync(summary.Path);

        report.Issues.Should().BeEmpty();
        Directory.GetFiles(_temp.GetFilePath("dst"), "*", SearchOption.AllDirectories).Should().BeEmpty();
        File.ReadAllBytes(source).Should().Equal(AlphaData);
    }

    [Fact]
    public async Task ModifiedArchive_ShouldNotBeDeletedByUndo()
    {
        Source(Alpha, AlphaData);
        var crcCache = new CrcCacheService(_temp.GetFilePath(Path.Combine("cache", "crc_cache.json")));
        var journal = new OperationJournalService(_temp.GetFilePath("journals"), _services.ChecksumService, crcCache);
        var processor = new SortProcessorService(
            _services.ChecksumService, _services.DatService, _services.NoIntroParser, _services.FilterService,
            _services.Organizer, _services.I18n, _services.OpticalCompanionService, _services.MameParser, crcCache, journal);
        await processor.ExecuteAsync(Config(), isDryRun: false);
        var zipPath = _temp.GetFilePath(Path.Combine("dst", "Alpha (1990)(Ocean).zip"));
        File.AppendAllText(zipPath, "changed");

        var report = await journal.UndoAsync(journal.FindLastUndoableJournal()!.Path);

        report.Issues.Should().ContainSingle().Which.Kind.Should().Be(UndoIssueKind.TargetModified);
        File.Exists(zipPath).Should().BeTrue();
    }

    [Fact]
    public async Task VerifiedArchive_WithACorruptedEntry_ShouldBeDeleted()
    {
        // Random data does not compress: the damaged byte falls inside the compressed data of the entry
        var randomData = new byte[64 * 1024];
        new Random(7).NextBytes(randomData);
        var source = Source(Alpha, randomData);
        var target = _temp.GetFilePath(Path.Combine("dst", "Alpha (1990)(Ocean).zip"));
        var config = Config();
        config.VerifyCopies = true;
        var executor = new FileTransferExecutor(
            config, isDryRun: false, _services.Organizer, _services.FilterService, _services.I18n,
            _services.OpticalCompanionService, _services.ChecksumService)
        {
            AfterTargetWritten = path =>
            {
                // Damage the compressed data of the entry (after its local header)
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
                stream.Position = 1000;
                var b = stream.ReadByte();
                stream.Position = 1000;
                stream.WriteByte((byte)(b ^ 0xFF));
            }
        };
        var item = new ProcessItem { SourcePath = source, FileSize = randomData.Length, Parsed = _services.TosecParser.ParseFilename(Alpha), Status = FileStatus.Valid };

        await executor.ProcessItemAsync(item, new HashSet<string>(), new ProcessStats());

        item.Status.Should().Be(FileStatus.Error);
        item.StatusReason!.Key.Should().Be("reason.copy_verification_failed");
        File.Exists(target).Should().BeFalse("a corrupted archive is deleted");
        File.ReadAllBytes(source).Should().Equal(randomData);
    }

    [Fact]
    public async Task SortedArchives_ShouldBeTheArchivesOfIgirAndRomVault()
    {
        // The fixture files of a disc (a descriptor and its two tracks) and of a single file, sorted in ZIP mode (the
        // disc, without a DAT, is non-conforming: its archive goes to _unmatched, with the same content)
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "TorrentZip");
        string[] games = ["Disc (1995)(Sony)", "Compressible (1990)(Test)"];
        foreach (var file in games.SelectMany(game => Directory.GetFiles(Path.Combine(fixtures, "input", game))))
        {
            Source(Path.GetFileName(file), File.ReadAllBytes(file));
        }

        var result = await _services.Processor.ExecuteAsync(Config(), isDryRun: false);

        result.Items.Select(i => Path.GetFileNameWithoutExtension(i.DisplayName)).Should().BeEquivalentTo(games);
        foreach (var item in result.Items)
        {
            var game = Path.GetFileNameWithoutExtension(item.DisplayName);
            item.Status.Should().NotBe(FileStatus.Error);
            File.ReadAllBytes(item.FinalTargetPath!)
                .Should().Equal(File.ReadAllBytes(Path.Combine(fixtures, "reference", game + ".zip")), $"{game}.zip is a TorrentZip archive");
        }
    }

    [Fact]
    public async Task VerifiedArchive_WithAWrongSignature_ShouldBeDeleted()
    {
        var source = Source(Alpha, AlphaData);
        var target = _temp.GetFilePath(Path.Combine("dst", "Alpha (1990)(Ocean).zip"));
        var config = Config();
        config.VerifyCopies = true;
        var executor = new FileTransferExecutor(
            config, isDryRun: false, _services.Organizer, _services.FilterService, _services.I18n,
            _services.OpticalCompanionService, _services.ChecksumService)
        {
            AfterTargetWritten = path =>
            {
                // Change the central directory (its "version made by"): the entries still read well, the signature no
                // longer matches
                var bytes = File.ReadAllBytes(path);
                var centralStart = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(bytes.Length - 44 + 16));
                bytes[centralStart + 4] ^= 0x01;
                File.WriteAllBytes(path, bytes);
            }
        };
        var item = new ProcessItem { SourcePath = source, FileSize = AlphaData.Length, Parsed = _services.TosecParser.ParseFilename(Alpha), Status = FileStatus.Valid };

        await executor.ProcessItemAsync(item, new HashSet<string>(), new ProcessStats());

        item.Status.Should().Be(FileStatus.Error);
        item.StatusReason!.Key.Should().Be("reason.copy_verification_failed");
        File.Exists(target).Should().BeFalse("an archive whose signature is wrong is not TorrentZip");
        Directory.GetFiles(_temp.GetFilePath("dst")).Should().BeEmpty("the temporary archive is deleted too");
    }

    [Fact]
    public async Task FailedWrite_ShouldLeaveNoArchive()
    {
        var source = Source("Big Game (1990)(Ocean).adf", ArchiveFormatTests.Pattern("BIG---", 4 * 1024 * 1024));
        var executor = new FileTransferExecutor(
            Config(), isDryRun: false, _services.Organizer, _services.FilterService, _services.I18n,
            _services.OpticalCompanionService, _services.ChecksumService)
        {
            // The disk fills up in the middle of the compressed data
            WrapTargetStream = stream => new FailingStream(stream, FailingStream.DiskFull) { FailAfterBytesWritten = 5000 }
        };
        var item = new ProcessItem { SourcePath = source, FileSize = 4 * 1024 * 1024, Parsed = _services.TosecParser.ParseFilename("Big Game (1990)(Ocean).adf"), Status = FileStatus.Valid };

        await executor.ProcessItemAsync(item, new HashSet<string>(), new ProcessStats());

        item.Status.Should().Be(FileStatus.Error);
        executor.StopReason.Should().NotBeNull("a full disk stops the sort");
        Directory.GetFiles(_temp.GetFilePath("dst")).Should().BeEmpty("neither the archive nor its temporary file stays");
        File.ReadAllBytes(source).Should().Equal(ArchiveFormatTests.Pattern("BIG---", 4 * 1024 * 1024));
    }

    [Fact]
    public async Task ArchiveOfTheFormerFormat_ShouldStillBeUndone()
    {
        // An archive written by an earlier version (ZipArchive, not TorrentZip) is journaled with its own CRC32
        var source = Source(Alpha, AlphaData);
        var target = _temp.GetFilePath(Path.Combine("dst", "Alpha (1990)(Ocean).zip"));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using (var zip = ZipFile.Open(target, ZipArchiveMode.Create))
        {
            using var entry = zip.CreateEntry(Alpha, CompressionLevel.SmallestSize).Open();
            entry.Write(AlphaData);
        }

        var crc = Crc32.HashToUInt32(File.ReadAllBytes(target)).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        var crcCache = new CrcCacheService(_temp.GetFilePath(Path.Combine("cache", "crc_cache.json")));
        var journal = new OperationJournalService(_temp.GetFilePath("journals"), _services.ChecksumService, crcCache);
        await using (var writer = await journal.BeginAsync(Config()))
        {
            await writer.RecordAsync(new JournalOperation(JournalOperationKind.ZipArchive, target, source, Size: new FileInfo(target).Length, Crc: crc));
            await writer.CompleteAsync(completed: true);
        }

        var report = await journal.UndoAsync(journal.FindLastUndoableJournal()!.Path);

        report.Issues.Should().BeEmpty();
        File.Exists(target).Should().BeFalse();
        File.ReadAllBytes(source).Should().Equal(AlphaData);
    }

    [Fact]
    public async Task Simulation_ShouldShowTheArchivesWithoutWritingThem()
    {
        Source(Alpha, AlphaData);
        Source("not a tosec name.bin", [1, 2, 3]);

        var result = await _services.Processor.ExecuteAsync(Config(), isDryRun: true);

        result.Items.Select(i => Path.GetRelativePath(_temp.GetFilePath("dst"), i.FinalTargetPath!))
            .Should().BeEquivalentTo(["Alpha (1990)(Ocean).zip", Path.Combine("_unmatched", "not a tosec name.zip")]);
        Directory.Exists(_temp.GetFilePath("dst")).Should().BeFalse();
    }

    [Fact]
    public async Task UnmatchedFile_ShouldBeArchivedInTheUnmatchedFolder()
    {
        Source("not a tosec name.bin", [1, 2, 3]);

        await _services.Processor.ExecuteAsync(Config(), isDryRun: false);

        ReadZip(_temp.GetFilePath(Path.Combine("dst", "_unmatched", "not a tosec name.zip")))["not a tosec name.bin"].Should().Equal(1, 2, 3);
    }

    [Fact]
    public void ZipMode_ShouldBeSavedUnderAStableName()
    {
        TransferMode.Zip.ToConfigString().Should().Be("zip");
        EnumExtensions.ParseTransferMode("zip").Should().Be(TransferMode.Zip);
    }
}
