using System.IO;
using System.IO.Compression;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Input / output failures during a real sort: a full disk, a network share that goes away in the middle of a file, a
/// locked file. Whatever fails, no partial file is left in the destination, the source is never lost, a file that the
/// collision mode "overwrite" was to replace is kept, the other files are sorted, and the journal holds exactly what
/// was written, so that the sort can be undone.
/// </summary>
public sealed class TransferFailureTests : IDisposable
{
    private const int FileSize = 3 * 1024 * 1024;
    private const long FailAfterBytes = 1_500_000;

    private static readonly string[] Names =
    [
        "Alpha (1990)(Ocean).adf",
        "Beta (1991)(Ocean).adf",
        "Gamma (1992)(Ocean).adf"
    ];

    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");
    private readonly List<LocalizedMessage> _logs = [];
    private readonly OperationJournalService _journalService;
    private readonly string _src;
    private readonly string _dst;

    public TransferFailureTests()
    {
        _journalService = new OperationJournalService(_temp.GetFilePath("journals"), _services.ChecksumService, _services.CrcCache);
        _src = _temp.CreateSubdirectory("src");
        _dst = Path.Combine(_temp.Path, "dst");
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>Content that does not compress, so that an archive is as large as its file and fails as well.</summary>
    private static byte[] ContentOf(string name)
    {
        var bytes = new byte[FileSize];
        new Random(name.Sum(c => c)).NextBytes(bytes);
        return bytes;
    }

    private string WriteSource(string name) => _temp.WriteBytes(Path.Combine("src", name), ContentOf(name));

    private sealed record SortRun(IReadOnlyList<ProcessItem> Items, int JournaledOperations, string JournalPath, ProcessStats Stats, LocalizedMessage? StopReason);

    /// <summary>
    /// Sorts the items through a <see cref="FileTransferExecutor"/> as the pipeline does, with a journal. Every volume
    /// is unknown unless <paramref name="sameVolume"/>, so that a move goes through a copy.
    /// </summary>
    private async Task<SortRun> SortAsync(
        IReadOnlyList<ProcessItem> items,
        TransferMode mode,
        Func<Stream, Stream>? wrapSource = null,
        Func<Stream, Stream>? wrapTarget = null,
        CollisionMode collision = CollisionMode.Rename,
        bool sameVolume = false,
        ICrcCacheService? crcCache = null,
        Action<string>? afterTargetWritten = null,
        Action<string>? afterFlushedToDisk = null)
    {
        var config = new SortConfig
        {
            SourceDir = _src,
            DestinationDir = _dst,
            TransferMode = mode,
            CollisionMode = collision
        };
        var journal = await _journalService.BeginAsync(config);
        var executor = new FileTransferExecutor(
            config, isDryRun: false, _services.Organizer, _services.FilterService, _services.I18n,
            _services.OpticalCompanionService, _services.ChecksumService, (_, message) => _logs.Add(message), journal, crcCache)
        {
            VolumeRoots = sameVolume ? [Path.GetPathRoot(_temp.Path)!] : [],
            WrapSourceStream = wrapSource,
            WrapTargetStream = wrapTarget,
            AfterTargetWritten = afterTargetWritten,
            AfterFlushedToDisk = afterFlushedToDisk
        };

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stats = new ProcessStats();
        foreach (var item in items)
        {
            await executor.ProcessItemAsync(item, claimed, stats);
        }

        var count = journal.OperationCount;
        await journal.CompleteAsync(completed: true);
        return new SortRun(items, count, journal.JournalPath, stats, executor.StopReason);
    }

    private ProcessItem CreateItem(string sourcePath, string? archivePath = null, string? archiveMember = null) => new()
    {
        SourcePath = sourcePath,
        FileSize = FileSize,
        Parsed = _services.TosecParser.ParseFilename(Path.GetFileName(archiveMember ?? sourcePath)),
        ArchivePath = archivePath,
        ArchiveMember = archiveMember,
        Status = FileStatus.Valid
    };

    private List<ProcessItem> CreateItems() => [.. Names.Select(n => CreateItem(WriteSource(n)))];

    /// <summary>Files of the destination, without the temporary files of a replacement, which must never stay.</summary>
    private string[] DestinationFiles() =>
        Directory.Exists(_dst)
            ? [.. Directory.EnumerateFiles(_dst, "*", SearchOption.AllDirectories).Select(Path.GetFileName).OfType<string>().Order()]
            : [];

    /// <summary>Wraps the n-th stream (1 for the first) in a stream that fails, every other stream unchanged.</summary>
    private static Func<Stream, Stream> FailNth(int n, bool onRead, string message) => FailNth(n, onRead, () => new IOException(message));

    private static Func<Stream, Stream> FailNth(int n, bool onRead, Func<Exception> error)
    {
        var count = 0;
        return stream => Interlocked.Increment(ref count) == n ? Failing(stream, onRead, error) : stream;
    }

    private static Func<Stream, Stream> FailAll(bool onRead, string message) =>
        stream => Failing(stream, onRead, () => new IOException(message));

    private static FailingStream Failing(Stream stream, bool onRead, Func<Exception> error) => onRead
        ? new FailingStream(stream, error) { FailAfterBytesRead = FailAfterBytes }
        : new FailingStream(stream, error) { FailAfterBytesWritten = FailAfterBytes };

    /// <summary>A write error that is not a full disk: the sort goes on with the next files.</summary>
    private const string WriteFailed = "The device is not ready.";
    private const string NetworkLost = "The specified network name is no longer available.";

    private void ShouldReportTheFailure(ProcessItem item, string message)
    {
        item.Status.Should().Be(FileStatus.Error);
        item.ErrorMessage.Should().Be(message);
        _logs.Should().Contain(m => m.Key == "log.transfer_error");
    }

    private void SourceShouldBeIntact(params string[] names)
    {
        foreach (var name in names)
        {
            File.ReadAllBytes(Path.Combine(_src, name)).Should().Equal(ContentOf(name), $"{name} is still in the source");
        }
    }

    private void DestinationShouldHold(params string[] names)
    {
        DestinationFiles().Should().Equal(names.Order());
        foreach (var name in names)
        {
            var path = Directory.EnumerateFiles(_dst, name, SearchOption.AllDirectories).Single();
            File.ReadAllBytes(path).Should().Equal(ContentOf(name), $"{name} is written whole");
        }
    }

    private async Task UndoShouldEmptyTheDestination(SortRun run)
    {
        var report = await _journalService.UndoAsync(run.JournalPath);

        report.Issues.Should().BeEmpty();
        report.UndoneCount.Should().Be(run.JournaledOperations);
        DestinationFiles().Should().BeEmpty();
    }

    // =========================================================================
    // Full disk, lost network share
    // =========================================================================

    [Fact]
    public async Task Copy_WriteErrorInTheMiddleOfAFile_ShouldLeaveNoPartialFileAndSortTheOthers()
    {
        var run = await SortAsync(CreateItems(), TransferMode.Copy, wrapTarget: FailNth(2, onRead: false, WriteFailed));

        ShouldReportTheFailure(run.Items[1], WriteFailed);
        run.Items[0].Status.Should().Be(FileStatus.Valid);
        run.Items[2].Status.Should().Be(FileStatus.Valid);
        run.StopReason.Should().BeNull("only a full disk stops the sort");
        run.Stats.ErrorCount.Should().Be(1);
        run.Stats.ValidCount.Should().Be(2, "a file in error is no longer counted as written");
        DestinationShouldHold(Names[0], Names[2]);
        SourceShouldBeIntact(Names);
        run.JournaledOperations.Should().Be(2, "only what was written is journaled");

        await UndoShouldEmptyTheDestination(run);
        SourceShouldBeIntact(Names);
    }

    [Fact]
    public async Task Copy_WriteErrorForEveryFile_ShouldFinishWithEveryFileInError()
    {
        var run = await SortAsync(CreateItems(), TransferMode.Copy, wrapTarget: FailAll(onRead: false, WriteFailed));

        run.Items.Should().OnlyContain(i => i.Status == FileStatus.Error);
        DestinationFiles().Should().BeEmpty();
        SourceShouldBeIntact(Names);
        run.JournaledOperations.Should().Be(0);
    }

    [Fact]
    public async Task Copy_SourceLostInTheMiddleOfAFile_ShouldLeaveNoPartialFileAndSortTheOthers()
    {
        var run = await SortAsync(CreateItems(), TransferMode.Copy, wrapSource: FailNth(2, onRead: true, NetworkLost));

        ShouldReportTheFailure(run.Items[1], NetworkLost);
        DestinationShouldHold(Names[0], Names[2]);
        run.JournaledOperations.Should().Be(2);

        await UndoShouldEmptyTheDestination(run);
    }

    [Fact]
    public async Task MoveAcrossVolumes_WriteError_ShouldKeepTheFailedFileInTheSource()
    {
        var run = await SortAsync(CreateItems(), TransferMode.Move, wrapTarget: FailNth(2, onRead: false, WriteFailed));

        ShouldReportTheFailure(run.Items[1], WriteFailed);
        DestinationShouldHold(Names[0], Names[2]);
        Directory.GetFiles(_src).Select(Path.GetFileName).Should().Equal(Names[1]);
        SourceShouldBeIntact(Names[1]);

        await UndoShouldEmptyTheDestination(run);
        SourceShouldBeIntact(Names);
    }

    [Fact]
    public async Task MoveAcrossVolumes_SourceLost_ShouldKeepTheFailedFileInTheSource()
    {
        var run = await SortAsync(CreateItems(), TransferMode.Move, wrapSource: FailNth(1, onRead: true, NetworkLost));

        ShouldReportTheFailure(run.Items[0], NetworkLost);
        DestinationShouldHold(Names[1], Names[2]);
        SourceShouldBeIntact(Names[0]);

        await UndoShouldEmptyTheDestination(run);
        SourceShouldBeIntact(Names);
    }

    [Fact]
    public async Task Zip_WriteError_ShouldLeaveNoPartialArchive()
    {
        var run = await SortAsync(CreateItems(), TransferMode.Zip, wrapTarget: FailNth(2, onRead: false, WriteFailed));

        ShouldReportTheFailure(run.Items[1], WriteFailed);
        DestinationFiles().Should().Equal(Path.ChangeExtension(Names[0], ".zip"), Path.ChangeExtension(Names[2], ".zip"));
        SourceShouldBeIntact(Names);

        await UndoShouldEmptyTheDestination(run);
    }

    [Fact]
    public async Task Extraction_WriteError_ShouldLeaveNoPartialFileAndKeepTheArchive()
    {
        var archive = _temp.GetFilePath(Path.Combine("src", "games.zip"));
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (var name in Names)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(ContentOf(name));
            }
        }

        var archiveBytes = File.ReadAllBytes(archive);
        var items = Names.Select(n => CreateItem(archive, archive, n)).ToList();

        var run = await SortAsync(items, TransferMode.Move, wrapTarget: FailNth(2, onRead: false, WriteFailed));

        ShouldReportTheFailure(run.Items[1], WriteFailed);
        DestinationShouldHold(Names[0], Names[2]);
        File.ReadAllBytes(archive).Should().Equal(archiveBytes, "an archive stays in place, even for a move");

        await UndoShouldEmptyTheDestination(run);
    }

    [Fact]
    public async Task DescriptorWithTracks_TrackFailing_ShouldLeaveNoPartialTrackAndBeUndone()
    {
        const string cue = "Disc (1995)(Ocean).cue";
        string[] tracks = ["Disc (1995)(Ocean) (Track 1).bin", "Disc (1995)(Ocean) (Track 2).bin"];
        var descriptor = _temp.WriteText(Path.Combine("src", cue),
            $"FILE \"{tracks[0]}\" BINARY\n  TRACK 01 MODE1/2352\nFILE \"{tracks[1]}\" BINARY\n  TRACK 02 AUDIO\n");
        var item = CreateItem(descriptor);
        item.RelatedItems.AddRange(tracks.Select(t => CreateItem(WriteSource(t))));

        // Streams written: the descriptor, then each track
        var run = await SortAsync([item], TransferMode.Copy, wrapTarget: FailNth(3, onRead: false, WriteFailed));

        item.RelatedItems[1].Status.Should().Be(FileStatus.Error);
        item.RelatedItems[1].ErrorMessage.Should().Be(WriteFailed);
        DestinationFiles().Should().Equal(new[] { cue, tracks[0] }.Order(), "the failed track leaves nothing behind");
        SourceShouldBeIntact(tracks);
        run.JournaledOperations.Should().Be(2);

        // The game is not whole in the destination: in error as a whole, said in the log
        item.Status.Should().Be(FileStatus.Error);
        item.StatusReason!.Key.Should().Be("reason.set_transfer_incomplete");
        item.StatusReason.Args["name"].Should().Be(tracks[1]);
        _logs.Should().Contain(m => m.Key == "log.set_transfer_incomplete");
        run.Stats.ErrorCount.Should().Be(1);
        run.Stats.ValidCount.Should().Be(0);

        await UndoShouldEmptyTheDestination(run);
    }

    private ProcessItem CreateDescriptorWithTracks(out string[] tracks)
    {
        const string cue = "Disc (1995)(Ocean).cue";
        tracks = ["Disc (1995)(Ocean) (Track 1).bin", "Disc (1995)(Ocean) (Track 2).bin"];
        var descriptor = _temp.WriteText(Path.Combine("src", cue),
            $"FILE \"{tracks[0]}\" BINARY\n  TRACK 01 MODE1/2352\nFILE \"{tracks[1]}\" BINARY\n  TRACK 02 AUDIO\n");
        var item = CreateItem(descriptor);
        item.RelatedItems.AddRange(tracks.Select(t => CreateItem(WriteSource(t))));
        return item;
    }

    [Fact]
    public async Task DescriptorWithTracks_DescriptorFailing_ShouldWriteNoTrack()
    {
        var item = CreateDescriptorWithTracks(out var tracks);

        // The descriptor is a few bytes long: it fails at its first write
        var first = true;
        var run = await SortAsync([item], TransferMode.Copy, wrapTarget: stream =>
        {
            var fails = first;
            first = false;
            return fails ? new FailingStream(stream, () => new IOException(WriteFailed)) { FailAfterWrites = 0 } : stream;
        });

        ShouldReportTheFailure(item, WriteFailed);
        item.RelatedItems.Should().OnlyContain(r => r.Status == FileStatus.Error && r.StatusReason!.Key == "reason.set_transfer_failed");
        item.RelatedItems.Should().OnlyContain(r => r.FinalTargetPath != null, "the table still shows where the track would go");
        DestinationFiles().Should().BeEmpty();
        SourceShouldBeIntact(tracks);
        run.Stats.ErrorCount.Should().Be(1);
    }

    // =========================================================================
    // A full disk stops the sort
    // =========================================================================

    [Fact]
    public void IsDiskFull_ShouldKnowTheCodesOfAFullDisk()
    {
        FileTransferExecutor.IsDiskFull(FailingStream.DiskFull()).Should().BeTrue();
        FileTransferExecutor.IsDiskFull(new IOException("full", unchecked((int)0x80070027))).Should().BeTrue("ERROR_HANDLE_DISK_FULL");
        FileTransferExecutor.IsDiskFull(new IOException("No space left on device", 28)).Should().Be(!OperatingSystem.IsWindows(), "ENOSPC");
        FileTransferExecutor.IsDiskFull(new IOException(WriteFailed)).Should().BeFalse();
        FileTransferExecutor.IsDiskFull(new UnauthorizedAccessException()).Should().BeFalse();
    }

    [Fact]
    public async Task DiskFull_ShouldTellTheSortToStop()
    {
        var run = await SortAsync(CreateItems().Take(2).ToList(), TransferMode.Copy, wrapTarget: FailNth(2, onRead: false, FailingStream.DiskFull));

        run.StopReason.Should().NotBeNull();
        run.StopReason!.Key.Should().Be("log.sort_stopped_disk_full");
        run.StopReason.Args["name"].Should().Be(Names[1]);
        DestinationShouldHold(Names[0]);
    }

    [Fact]
    public async Task DescriptorWithTracks_DiskFullOnATrack_ShouldNotTryTheNextTracks()
    {
        var item = CreateDescriptorWithTracks(out var tracks);

        // Streams written: the descriptor, then the first track, which fills the disk
        var run = await SortAsync([item], TransferMode.Copy, wrapTarget: FailNth(2, onRead: false, FailingStream.DiskFull));

        run.StopReason!.Key.Should().Be("log.sort_stopped_disk_full");
        item.RelatedItems[0].Status.Should().Be(FileStatus.Error);
        item.RelatedItems[1].Status.Should().Be(FileStatus.Error);
        item.RelatedItems[1].StatusReason!.Key.Should().Be("reason.sort_stopped");
        item.Status.Should().Be(FileStatus.Error);
        DestinationFiles().Should().Equal(Path.GetFileName(item.SourcePath));
        SourceShouldBeIntact(tracks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Move_FailureOnceTheSourceIsGone_ShouldNeverDeleteTheOnlyCopy(bool sameVolume)
    {
        var item = CreateItem(WriteSource(Names[0]));

        var run = await SortAsync([item], TransferMode.Move, sameVolume: sameVolume, crcCache: new FailingRenameCrcCache());

        run.Items[0].Status.Should().Be(FileStatus.Error);
        File.Exists(item.SourcePath).Should().BeFalse("the move happened");
        DestinationShouldHold(Names[0]);
    }

    // =========================================================================
    // Collision mode "overwrite": the existing target is kept until replaced
    // =========================================================================

    [Theory]
    [InlineData(TransferMode.Copy)]
    [InlineData(TransferMode.Move)]
    [InlineData(TransferMode.Zip)]
    public async Task Overwrite_FailedTransfer_ShouldKeepTheExistingTarget(TransferMode mode)
    {
        // A first sort puts the file where the second one will try to overwrite it
        var first = await SortAsync([CreateItem(WriteSource(Names[0]))], TransferMode.Copy);
        var existing = first.Items[0].FinalTargetPath!;
        var targetName = Names[0];
        if (mode == TransferMode.Zip)
        {
            File.Delete(existing);
            existing = Path.ChangeExtension(existing, ".zip");
            targetName = Path.ChangeExtension(Names[0], ".zip");
        }

        File.WriteAllText(existing, "previous version");

        var run = await SortAsync([CreateItem(Path.Combine(_src, Names[0]))], mode, wrapTarget: FailAll(onRead: false, WriteFailed), collision: CollisionMode.Overwrite);

        ShouldReportTheFailure(run.Items[0], WriteFailed);
        run.Items[0].FinalTargetPath.Should().Be(existing);
        File.ReadAllText(existing).Should().Be("previous version", "a target is only replaced by a complete file");
        DestinationFiles().Should().Equal(targetName);
        SourceShouldBeIntact(Names[0]);
        run.JournaledOperations.Should().Be(0);
    }

    [Theory]
    [InlineData(TransferMode.Copy)]
    [InlineData(TransferMode.Move)]
    [InlineData(TransferMode.Zip)]
    public async Task Overwrite_SuccessfulTransfer_ShouldReplaceTheTargetWithoutLeavingATemporaryFile(TransferMode mode)
    {
        var first = await SortAsync([CreateItem(WriteSource(Names[0]))], TransferMode.Copy);
        var target = first.Items[0].FinalTargetPath!;
        if (mode == TransferMode.Zip)
        {
            target = Path.ChangeExtension(target, ".zip");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        }

        File.WriteAllText(target, "previous version");
        var expectedFiles = DestinationFiles();

        var run = await SortAsync([CreateItem(Path.Combine(_src, Names[0]))], mode, collision: CollisionMode.Overwrite);

        run.Items[0].Status.Should().Be(FileStatus.Valid);
        run.Items[0].FinalTargetPath.Should().Be(target);
        File.ReadAllText(target).Should().NotBe("previous version");
        DestinationFiles().Should().Equal(expectedFiles);
    }

    [Fact]
    public async Task Overwrite_SameVolumeMoveThatFails_ShouldKeepTheExistingTargetAndTheSource()
    {
        if (!OperatingSystem.IsWindows())
        {
            // A source can only be locked against a move under Windows
            return;
        }

        var first = await SortAsync([CreateItem(WriteSource(Names[0]))], TransferMode.Copy);
        var target = first.Items[0].FinalTargetPath!;
        File.WriteAllText(target, "previous version");

        var source = Path.Combine(_src, Names[0]);
        SortRun run;
        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            run = await SortAsync([CreateItem(source)], TransferMode.Move, collision: CollisionMode.Overwrite, sameVolume: true);
        }

        run.Items[0].Status.Should().Be(FileStatus.Error);
        File.ReadAllText(target).Should().Be("previous version");
        SourceShouldBeIntact(Names[0]);
    }

    // =========================================================================
    // A process ended in the middle of a file leaves no file under the name of its target
    // =========================================================================

    [Theory]
    [InlineData(TransferMode.Copy, false)]
    [InlineData(TransferMode.Move, false)]
    [InlineData(TransferMode.Zip, false)]
    [InlineData(TransferMode.Copy, true)]
    public async Task NewTarget_ShouldOnlyBeWrittenUnderATemporaryNameUntilComplete(TransferMode mode, bool fromArchive)
    {
        // What a process ended in the middle of a file (window closed, crash, power cut) would leave behind: the files
        // of the destination once the data is written, just before the file takes the name of its target
        ProcessItem item;
        if (fromArchive)
        {
            var archive = _temp.GetFilePath(Path.Combine("src", "games.zip"));
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                using var entry = zip.CreateEntry(Names[0]).Open();
                entry.Write(ContentOf(Names[0]));
            }

            item = CreateItem(archive, archive, Names[0]);
        }
        else
        {
            item = CreateItem(WriteSource(Names[0]));
        }

        string? writtenTo = null;
        string[] filesWhileWriting = [];

        var run = await SortAsync([item], mode, afterTargetWritten: path =>
        {
            writtenTo = Path.GetFileName(path);
            filesWhileWriting = DestinationFiles();
        });

        run.Items[0].Status.Should().Be(FileStatus.Valid);
        DestinationGuard.IsReplacementFile(writtenTo!).Should().BeTrue("the data never goes to the target itself");
        filesWhileWriting.Should().Equal([writtenTo], "until complete, the file bears a temporary name, which a scan skips");
        DestinationFiles().Should().Equal(Path.GetFileName(run.Items[0].FinalTargetPath!));
    }

    // =========================================================================
    // A move across volumes puts the copy on the disk before the source goes
    // =========================================================================

    [Theory]
    [InlineData(TransferMode.Move, false, true)]
    [InlineData(TransferMode.Move, true, false)]
    [InlineData(TransferMode.Copy, false, false)]
    public async Task MoveAcrossVolumes_ShouldPutTheCopyOnTheDiskBeforeDeletingTheSource(TransferMode mode, bool sameVolume, bool flushed)
    {
        var source = WriteSource(Names[0]);
        var flushes = new List<(string Written, bool SourceExists)>();

        var run = await SortAsync([CreateItem(source)], mode, sameVolume: sameVolume,
            afterFlushedToDisk: path => flushes.Add((Path.GetFileName(path), File.Exists(source))));

        run.Items[0].Status.Should().Be(FileStatus.Valid);
        if (flushed)
        {
            var (written, sourceExists) = flushes.Should().ContainSingle().Subject;
            DestinationGuard.IsReplacementFile(written).Should().BeTrue("the copy is put on the disk before it takes its name");
            sourceExists.Should().BeTrue("the source is only deleted once its copy is on the disk");
            File.Exists(source).Should().BeFalse();
        }
        else
        {
            flushes.Should().BeEmpty(mode == TransferMode.Copy ? "a copy keeps its source" : "a rename on one volume copies nothing");
        }
    }

    [Fact]
    public async Task MovedDescriptorRewritten_ShouldBeOnTheDiskBeforeItsSourceIsDeleted()
    {
        // The second sort finds the descriptor and its track in the destination: both are renamed, the descriptor
        // rewritten for its new track name, and its source deleted
        const string cue = "Disc (1995)(Ocean).cue";
        const string track = "Disc (1995)(Ocean) (Track 1).bin";
        var descriptor = _temp.WriteText(Path.Combine("src", cue), $"FILE \"{track}\" BINARY\n  TRACK 01 MODE1/2352\n");
        var trackPath = WriteSource(track);
        ProcessItem CreateSet()
        {
            var item = CreateItem(descriptor);
            item.RelatedItems.Add(CreateItem(trackPath));
            return item;
        }

        await SortAsync([CreateSet()], TransferMode.Copy);
        var flushes = new List<(string Written, bool DescriptorExists, bool TrackExists)>();

        var run = await SortAsync([CreateSet()], TransferMode.Move,
            afterFlushedToDisk: path => flushes.Add((Path.GetFileName(path), File.Exists(descriptor), File.Exists(trackPath))));

        run.Items[0].Status.Should().Be(FileStatus.Valid);
        run.Items[0].FinalTargetPath.Should().EndWith("_01.cue");
        flushes.Should().HaveCount(2, "the track is moved across volumes, the descriptor rewritten")
            .And.OnlyContain(f => DestinationGuard.IsReplacementFile(f.Written));
        flushes.Should().ContainSingle(f => f.Written.Contains(".cue.", StringComparison.Ordinal))
            .Which.DescriptorExists.Should().BeTrue("the source descriptor is only deleted once the rewritten one is on the disk");
        flushes.Should().ContainSingle(f => f.Written.Contains(".bin.", StringComparison.Ordinal))
            .Which.TrackExists.Should().BeTrue("a track is only deleted once its copy is on the disk");
        File.Exists(descriptor).Should().BeFalse();
        File.Exists(trackPath).Should().BeFalse();
        File.ReadAllText(run.Items[0].FinalTargetPath!).Should().Contain("Disc (1995)(Ocean) (Track 1)_01.bin");
    }

    [Fact]
    public async Task UndoOfAMoveAcrossVolumes_ShouldPutTheFileBackOnTheDiskBeforeDeletingItsCopy()
    {
        var source = WriteSource(Names[0]);
        var run = await SortAsync([CreateItem(source)], TransferMode.Move);
        var target = run.Items[0].FinalTargetPath!;
        File.Exists(source).Should().BeFalse();

        var flushes = new List<(string Written, bool CopyExists)>();
        _journalService.VolumeRoots = [];
        _journalService.AfterFlushedToDisk = path => flushes.Add((Path.GetFileName(path), File.Exists(target)));

        var report = await _journalService.UndoAsync(run.JournalPath);

        report.Issues.Should().BeEmpty();
        File.ReadAllBytes(source).Should().Equal(ContentOf(Names[0]));
        File.Exists(target).Should().BeFalse();
        var (written, copyExists) = flushes.Should().ContainSingle().Subject;
        DestinationGuard.IsReplacementFile(written).Should().BeTrue("the file is put back under a temporary name");
        copyExists.Should().BeTrue("its copy in the destination is only deleted once it is back on the disk");
        Directory.EnumerateFiles(_src).Should().Equal(source);
    }

    // =========================================================================
    // Locked files (Windows only: elsewhere, locks do not stop other processes)
    // =========================================================================

    [Fact]
    public async Task Copy_LockedSource_ShouldBeInErrorAndSortTheOthers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var items = CreateItems();
        SortRun run;
        using (new FileStream(items[1].SourcePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            run = await SortAsync(items, TransferMode.Copy);
        }

        run.Items[1].Status.Should().Be(FileStatus.Error);
        DestinationShouldHold(Names[0], Names[2]);
        SourceShouldBeIntact(Names);
        run.JournaledOperations.Should().Be(2);
    }

    [Fact]
    public async Task MoveAcrossVolumes_SourceThatCannotBeDeleted_ShouldRemoveTheCopyAndKeepTheSource()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var items = CreateItems();
        SortRun run;

        // Readable, so the copy succeeds, but not deletable while this handle is open
        using (new FileStream(items[1].SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            run = await SortAsync(items, TransferMode.Move);
        }

        run.Items[1].Status.Should().Be(FileStatus.Error);
        DestinationShouldHold(Names[0], Names[2]);
        SourceShouldBeIntact(Names[1]);

        await UndoShouldEmptyTheDestination(run);
        SourceShouldBeIntact(Names);
    }

    /// <summary>CRC cache whose update after a move fails: an error right after the source is gone.</summary>
    private sealed class FailingRenameCrcCache : ICrcCacheService
    {
        public string CachePath => string.Empty;

        public IHeaderCache Headers => throw new NotSupportedException();

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public string? Get(string path) => null;

        public void Set(string path, string crc)
        {
        }

        public string? GetSha1(string path) => null;

        public void SetSha1(string path, string crc, string sha1)
        {
        }

        public void Rename(string oldPath, string newPath) => throw new IOException(NetworkLost);
    }
}
