using System.IO;
using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// Tests for the operation journal written during real sorts and for the undo of the last sort.
/// </summary>
public sealed class OperationJournalTests : IDisposable
{
    private const string GameA = "Game A (1990)(Ocean).adf";
    private const string GameB = "Game B (1991)(Ocean).adf";

    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");
    private readonly CrcCacheService _crcCache;
    private readonly OperationJournalService _journal;
    private readonly SortProcessorService _processor;
    private readonly string _src;
    private readonly string _dst;

    public OperationJournalTests()
    {
        _crcCache = new CrcCacheService(_temp.GetFilePath(Path.Combine("cache", "crc_cache.json")));
        _journal = new OperationJournalService(_temp.GetFilePath("journals"), _services.ChecksumService, _crcCache);
        _processor = new SortProcessorService(
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
        _src = _temp.CreateSubdirectory("src");
        _dst = _temp.CreateSubdirectory("dst");
    }

    public void Dispose() => _temp.Dispose();

    private SortConfig Config(TransferMode mode, string pattern = "{publisher}/") => new()
    {
        SourceDir = _src,
        DestinationDir = _dst,
        Pattern = pattern,
        TransferMode = mode
    };

    private string WriteSource(string relativePath, string content) =>
        _temp.WriteText(Path.Combine("src", relativePath), content);

    private IEnumerable<string> DestinationFiles() =>
        Directory.EnumerateFiles(_dst, "*", SearchOption.AllDirectories);

    [Fact]
    public async Task DryRun_ShouldNotWriteAnyJournal()
    {
        WriteSource(GameA, "A");

        var result = await _processor.ExecuteAsync(Config(TransferMode.Move), isDryRun: true);

        result.JournalPath.Should().BeNull();
        _journal.FindLastUndoableJournal().Should().BeNull();
    }

    [Fact]
    public async Task MoveThenUndo_ShouldRestoreTheOriginalTree()
    {
        var a = WriteSource(GameA, "A");
        var b = WriteSource(Path.Combine("sub", GameB), "B");
        var readme = WriteSource("readme.txt", "unmatched");

        var result = await _processor.ExecuteAsync(Config(TransferMode.Move), isDryRun: false);

        File.Exists(a).Should().BeFalse();
        File.Exists(b).Should().BeFalse();
        File.Exists(readme).Should().BeFalse();
        result.JournalPath.Should().NotBeNull();
        File.Exists(result.JournalPath).Should().BeTrue();

        var last = _journal.FindLastUndoableJournal();
        last.Should().NotBeNull();
        last!.Path.Should().Be(result.JournalPath);
        last.OperationCount.Should().Be(4, "two moves, the unmatched file and the marker of the new destination");
        last.Completed.Should().BeTrue();

        var report = await _journal.UndoAsync(last.Path);

        report.Issues.Should().BeEmpty();
        report.UndoneCount.Should().Be(4);
        File.ReadAllText(a).Should().Be("A");
        File.ReadAllText(b).Should().Be("B");
        File.ReadAllText(readme).Should().Be("unmatched");
        Directory.EnumerateFileSystemEntries(_dst).Should().BeEmpty();
        _journal.FindLastUndoableJournal().Should().BeNull();
    }

    [Fact]
    public async Task CopyThenUndo_ModifiedTarget_ShouldBeKeptAndReported()
    {
        WriteSource(GameA, "A");
        WriteSource(GameB, "B");

        await _processor.ExecuteAsync(Config(TransferMode.Copy), isDryRun: false);
        var copiedA = Path.Combine(_dst, "Ocean", GameA);
        var copiedB = Path.Combine(_dst, "Ocean", GameB);
        File.WriteAllText(copiedB, "B-modified-by-user");

        var report = await _journal.UndoAsync(_journal.FindLastUndoableJournal()!.Path);

        File.Exists(copiedA).Should().BeFalse();
        File.ReadAllText(copiedB).Should().Be("B-modified-by-user");
        report.UndoneCount.Should().Be(2, "the unmodified copy and the marker are undone");
        report.Issues.Should().ContainSingle()
            .Which.Should().Match<UndoIssue>(i => i.Kind == UndoIssueKind.TargetModified && i.Operation.TargetPath == copiedB);
    }

    [Fact]
    public async Task Undo_ShouldNeverOverwriteAFileAtTheOriginalLocation()
    {
        var a = WriteSource(GameA, "A");

        await _processor.ExecuteAsync(Config(TransferMode.Move), isDryRun: false);
        File.WriteAllText(a, "new file created by the user");

        var report = await _journal.UndoAsync(_journal.FindLastUndoableJournal()!.Path);

        File.ReadAllText(a).Should().Be("new file created by the user");
        File.ReadAllText(Path.Combine(_dst, "Ocean", GameA)).Should().Be("A");
        report.Issues.Should().ContainSingle().Which.Kind.Should().Be(UndoIssueKind.OriginOccupied);
    }

    [Fact]
    public async Task RealSort_ShouldOnlyKeepTheJournalsOfTheLastSorts()
    {
        // Journals of older sorts, from the oldest
        var journals = _temp.CreateSubdirectory("journals");
        var old = Enumerable.Range(0, OperationJournalService.KeptJournalCount + 5)
            .Select(i => _temp.WriteText(Path.Combine("journals", $"sort-2020{i / 28 + 1:00}{i % 28 + 1:00}-120000-000-{i:x6}.jsonl"), "{}"))
            .ToList();
        var other = _temp.WriteText(Path.Combine("journals", "notes.txt"), "not a journal");
        WriteSource(GameA, "A");

        var result = await _processor.ExecuteAsync(Config(TransferMode.Copy), isDryRun: false);

        var left = Directory.EnumerateFiles(journals, "sort-*.jsonl").ToList();
        left.Should().HaveCount(OperationJournalService.KeptJournalCount);
        left.Should().Contain(result.JournalPath!);
        left.Should().BeEquivalentTo(old.TakeLast(OperationJournalService.KeptJournalCount - 1).Append(result.JournalPath!));
        File.Exists(other).Should().BeTrue("only journals are deleted");

        // A simulation deletes nothing
        await _processor.ExecuteAsync(Config(TransferMode.Copy), isDryRun: true);
        Directory.EnumerateFiles(journals, "sort-*.jsonl").Should().HaveCount(OperationJournalService.KeptJournalCount);
    }

    [Fact]
    public async Task PartialUndo_ShouldOnlyReplayTheOperationsLeftOnceTheObstacleIsGone()
    {
        var a = WriteSource(GameA, "A");
        var b = WriteSource(GameB, "B");
        await _processor.ExecuteAsync(Config(TransferMode.Move), isDryRun: false);
        File.WriteAllText(a, "in the way");

        var first = await _journal.UndoAsync(_journal.FindLastUndoableJournal()!.Path);

        first.UndoneCount.Should().Be(2, "Game B and the marker are undone");
        first.RetryableCount.Should().Be(1);
        File.ReadAllText(b).Should().Be("B");
        var left = _journal.FindLastUndoableJournal();
        left.Should().NotBeNull("the operation left can be retried");
        left!.IsUndone.Should().BeFalse();
        left.IsUndoToResume.Should().BeTrue();
        left.RetryCount.Should().Be(1);
        left.OperationsToUndo.Should().Be(1);

        // The obstacle removed, the next undo only replays what the first one left
        File.Delete(a);
        var second = await _journal.UndoAsync(left.Path);

        second.TotalOperations.Should().Be(1);
        second.UndoneCount.Should().Be(1);
        second.Issues.Should().BeEmpty();
        File.ReadAllText(a).Should().Be("A");
        File.ReadAllText(b).Should().Be("B");
        Directory.EnumerateFileSystemEntries(_dst).Should().BeEmpty();
        _journal.FindLastUndoableJournal().Should().BeNull();
    }

    [Fact]
    public async Task PartialUndo_ShouldBeRetriedAfterAnUndoThatFailedOnALockedFile()
    {
        // Only Windows refuses to read a file opened without sharing
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        WriteSource(GameA, "A");
        await _processor.ExecuteAsync(Config(TransferMode.Copy), isDryRun: false);
        var copy = Path.Combine(_dst, "Ocean", GameA);

        UndoReport first;
        using (new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            first = await _journal.UndoAsync(_journal.FindLastUndoableJournal()!.Path);
        }

        first.Issues.Should().ContainSingle().Which.Kind.Should().Be(UndoIssueKind.Failed);
        File.Exists(copy).Should().BeTrue();
        var left = _journal.FindLastUndoableJournal();
        left!.RetryCount.Should().Be(1);

        var second = await _journal.UndoAsync(left.Path);

        second.Issues.Should().BeEmpty();
        File.Exists(copy).Should().BeFalse();
        _journal.FindLastUndoableJournal().Should().BeNull();
    }

    [Fact]
    public async Task PartialUndo_WithOnlyFinalIssues_ShouldNotBeOfferedAgain()
    {
        WriteSource(GameA, "A");
        await _processor.ExecuteAsync(Config(TransferMode.Copy), isDryRun: false);
        File.WriteAllText(Path.Combine(_dst, "Ocean", GameA), "modified by the user");

        var report = await _journal.UndoAsync(_journal.FindLastUndoableJournal()!.Path);

        report.Issues.Should().ContainSingle().Which.Kind.Should().Be(UndoIssueKind.TargetModified);
        report.RetryableCount.Should().Be(0);
        _journal.FindLastUndoableJournal().Should().BeNull("a modified target is never deleted: retrying would change nothing");
    }

    [Fact]
    public async Task InterruptedMove_ShouldBeUndoable()
    {
        var a = WriteSource(GameA, "A");
        var b = WriteSource(GameB, "B");
        using var cts = new CancellationTokenSource();
        var itemProcessed = new SyncProgress<ProcessItem>(_ => cts.Cancel());

        var act = () => _processor.ExecuteAsync(Config(TransferMode.Move), isDryRun: false, itemProcessed: itemProcessed, cancellationToken: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        var last = _journal.FindLastUndoableJournal();
        last.Should().NotBeNull();
        last!.OperationCount.Should().Be(2, "the marker of the new destination, then the first move");
        last.Completed.Should().BeFalse();

        var report = await _journal.UndoAsync(last.Path);

        report.Issues.Should().BeEmpty();
        File.ReadAllText(a).Should().Be("A");
        File.ReadAllText(b).Should().Be("B");
        DestinationFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task HardLinkThenUndo_ShouldRemoveLinksAndKeepSources()
    {
        var a = WriteSource(GameA, "A");

        await _processor.ExecuteAsync(Config(TransferMode.HardLink), isDryRun: false);
        File.Exists(Path.Combine(_dst, "Ocean", GameA)).Should().BeTrue();

        var report = await _journal.UndoAsync(_journal.FindLastUndoableJournal()!.Path);

        report.Issues.Should().BeEmpty();
        DestinationFiles().Should().BeEmpty();
        File.ReadAllText(a).Should().Be("A");
    }

    [Fact]
    public async Task GeneratedPlaylists_ShouldBeRemovedByUndo()
    {
        WriteSource("Game C (1992)(Ocean)(Disk 1 of 2).adf", "C1");
        WriteSource("Game C (1992)(Ocean)(Disk 2 of 2).adf", "C2");
        var config = Config(TransferMode.Copy, pattern: string.Empty);
        config.GenerateM3u = true;

        await _processor.ExecuteAsync(config, isDryRun: false);
        Directory.EnumerateFiles(_dst, "*.m3u", SearchOption.AllDirectories).Should().ContainSingle();

        var report = await _journal.UndoAsync(_journal.FindLastUndoableJournal()!.Path);

        report.Issues.Should().BeEmpty();
        Directory.EnumerateFileSystemEntries(_dst).Should().BeEmpty();
    }

    [Fact]
    public async Task Move_ShouldFollowTheFileInTheCrcCacheAndUndoShouldRestoreIt()
    {
        var a = WriteSource(GameA, "A");
        var crc = await _services.ChecksumService.ComputeFileCrc32Async(a);
        _crcCache.Set(a, crc);
        var target = Path.Combine(_dst, "Ocean", GameA);

        await _processor.ExecuteAsync(Config(TransferMode.Move), isDryRun: false);

        _crcCache.Get(target).Should().Be(crc);
        _crcCache.Get(a).Should().BeNull();

        await _journal.UndoAsync(_journal.FindLastUndoableJournal()!.Path);

        _crcCache.Get(a).Should().Be(crc);
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
