using System.IO;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// A real sort that stops before its end: when the destination is full, since every later file would fail as well,
/// and when the operation journal cannot be written, since what follows could not be undone. The files not transferred
/// stay in the source, in error with their reason; what was written is journaled and undone. Any other write error
/// leaves the sort going on with the next files.
/// </summary>
public sealed class SortStopTests : IDisposable
{
    private const int FileSize = 3 * 1024 * 1024;

    private static readonly string[] Names =
    [
        "Alpha (1990)(Ocean).adf",
        "Beta (1991)(Ocean).adf",
        "Gamma (1992)(Ocean).adf",
        "Delta (1993)(Ocean).adf"
    ];

    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");
    private readonly CrcCacheService _crcCache;
    private readonly OperationJournalService _journal;
    private readonly List<LogMessage> _logs = [];
    private readonly string _src;
    private readonly string _dst;

    public SortStopTests()
    {
        _crcCache = new CrcCacheService(_temp.GetFilePath(Path.Combine("cache", "crc_cache.json")));
        _journal = new OperationJournalService(_temp.GetFilePath("journals"), _services.ChecksumService, _crcCache);
        _src = _temp.CreateSubdirectory("src");
        _dst = Path.Combine(_temp.Path, "dst");
        foreach (var name in Names)
        {
            _temp.WriteBytes(Path.Combine("src", name), ContentOf(name));
        }
    }

    public void Dispose() => _temp.Dispose();

    private static byte[] ContentOf(string name)
    {
        var bytes = new byte[FileSize];
        new Random(name.Sum(c => c)).NextBytes(bytes);
        return bytes;
    }

    /// <summary>Runs a real sort; every volume is unknown, so that a move goes through a copy.</summary>
    private Task<SortExecutionResult> SortAsync(TransferMode mode, Func<Stream, Stream>? wrapTarget = null)
    {
        var processor = new SortProcessorService(
            _services.ChecksumService,
            _services.DatService,
            _services.NoIntroParser,
            _services.FilterService,
            _services.Organizer,
            _services.I18n,
            _services.OpticalCompanionService,
            _services.MameParser,
            _crcCache,
            _journal)
        {
            ConfigureTransferExecutor = executor =>
            {
                executor.VolumeRoots = [];
                executor.WrapTargetStream = wrapTarget;
            }
        };
        var config = new SortConfig { SourceDir = _src, DestinationDir = _dst, Pattern = "{publisher}/", TransferMode = mode };
        return processor.ExecuteAsync(config, isDryRun: false, logProgress: new SynchronousProgress<LogMessage>(_logs.Add));
    }

    /// <summary>Fails the n-th stream written (1 for the first), in the middle of its file.</summary>
    private static Func<Stream, Stream> FailNth(int n, Func<Exception> error)
    {
        var count = 0;
        return stream => ++count == n ? new FailingStream(stream, error) { FailAfterBytesWritten = FileSize / 2 } : stream;
    }

    private string[] SourceFiles() => [.. Directory.GetFiles(_src).Select(Path.GetFileName).OfType<string>().Order()];

    private string[] DestinationFiles() =>
        Directory.Exists(_dst)
            ? [.. Directory.EnumerateFiles(_dst, "*", SearchOption.AllDirectories)
                .Where(f => !DestinationGuard.IsGuardPath(f)).Select(Path.GetFileName).OfType<string>().Order()]
            : [];

    private void SourceShouldBeIntact()
    {
        SourceFiles().Should().Equal(Names.Order());
        foreach (var name in Names)
        {
            File.ReadAllBytes(Path.Combine(_src, name)).Should().Equal(ContentOf(name));
        }
    }

    private static List<ProcessItem> NotTransferred(SortExecutionResult result) =>
        [.. result.Items.Where(i => i.StatusReason?.Key == "reason.sort_stopped")];

    [Fact]
    public async Task DiskFull_ShouldStopTheSortAndLeaveTheRestInTheSource()
    {
        var result = await SortAsync(TransferMode.Move, FailNth(2, FailingStream.DiskFull));

        result.StopReason!.Key.Should().Be("log.sort_stopped_disk_full");
        _logs.Should().Contain(m => m.Level == LogLevel.Error && m.Message.Key == "log.sort_stopped_disk_full");
        result.Items.Should().ContainSingle(i => i.Status == FileStatus.Valid);
        result.Items.Should().ContainSingle(i => i.Status == FileStatus.Error && i.ErrorMessage == FailingStream.DiskFull().Message);
        var notTransferred = NotTransferred(result);
        notTransferred.Should().HaveCount(2).And.OnlyContain(i => i.Status == FileStatus.Error && i.FinalTargetPath == null);
        result.Stats.ErrorCount.Should().Be(3);
        result.Stats.ValidCount.Should().Be(1);

        DestinationFiles().Should().ContainSingle();
        SourceFiles().Should().HaveCount(3, "only the file written left the source");

        var report = await _journal.UndoAsync(result.JournalPath!);
        report.Issues.Should().BeEmpty();
        DestinationFiles().Should().BeEmpty();
        SourceShouldBeIntact();
    }

    [Fact]
    public async Task WriteError_ShouldNotStopTheSort()
    {
        var result = await SortAsync(TransferMode.Copy, FailNth(2, () => new IOException("The device is not ready.")));

        result.StopReason.Should().BeNull();
        result.Items.Count(i => i.Status == FileStatus.Valid).Should().Be(3);
        result.Stats.ErrorCount.Should().Be(1, "a file in error is counted, for the summary and the exit code of the command line");
        result.Stats.ValidCount.Should().Be(3);
        _logs.Should().Contain(m => m.Message.Key == "log.operation_completed_summary" && Equals(m.Message.Args["errors"], 1));
        DestinationFiles().Should().HaveCount(3);
    }

    [Fact]
    public async Task JournalFailing_ShouldStopTheSortAndNameTheFileItDoesNotHold()
    {
        // Records written: the header, the marker of the destination, the first file; the second file fails
        _journal.WrapJournalStream = stream => new FailingStream(stream, () => new IOException("The device is not ready.")) { FailAfterWrites = 3 };

        var result = await SortAsync(TransferMode.Copy);

        result.StopReason!.Key.Should().Be("log.sort_stopped_journal_failed");
        var unjournaled = result.Items.Single(i => Equals(i.DisplayName, result.StopReason.Args["name"]));
        unjournaled.Status.Should().Be(FileStatus.Valid, "it was written");
        result.StopReason.Args["target"].Should().Be(unjournaled.FinalTargetPath);
        NotTransferred(result).Should().HaveCount(2);
        DestinationFiles().Should().HaveCount(2);
        SourceShouldBeIntact();

        // The undo removes what the journal holds: the first file, not the one it could not record
        var report = await _journal.UndoAsync(result.JournalPath!);
        report.Issues.Should().BeEmpty();
        DestinationFiles().Should().Equal(unjournaled.DisplayName);
    }

    [Fact]
    public async Task JournalUnavailable_ShouldWriteNothing()
    {
        _journal.WrapJournalStream = stream => new FailingStream(stream, () => new IOException("The device is not ready.")) { FailAfterWrites = 0 };

        var result = await SortAsync(TransferMode.Move);

        result.StopReason!.Key.Should().Be("log.sort_stopped_journal_unavailable");
        NotTransferred(result).Should().HaveCount(Names.Length);
        result.Stats.ErrorCount.Should().Be(Names.Length);
        Directory.Exists(_dst).Should().BeFalse("neither the destination nor its marker is created");
        SourceShouldBeIntact();
        result.JournalPath.Should().BeNull();
    }

    [Fact]
    public async Task DryRun_ShouldNeverStop()
    {
        _journal.WrapJournalStream = stream => new FailingStream(stream, () => new IOException("The device is not ready.")) { FailAfterWrites = 0 };
        var processor = new SortProcessorService(
            _services.ChecksumService,
            _services.DatService,
            _services.NoIntroParser,
            _services.FilterService,
            _services.Organizer,
            _services.I18n,
            _services.OpticalCompanionService,
            _services.MameParser,
            _crcCache,
            _journal);

        var result = await processor.ExecuteAsync(new SortConfig { SourceDir = _src, DestinationDir = _dst }, isDryRun: true);

        result.StopReason.Should().BeNull();
        result.Items.Should().OnlyContain(i => i.Status == FileStatus.Valid);
    }

    /// <summary>Reports at once, on the calling thread: the log is complete when the run returns.</summary>
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
