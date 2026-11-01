using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Purge of the destination: nothing is deleted, the previous content is moved to a dated quarantine directory of the
/// destination and journaled, so that undoing the sort puts it back. The marker written in the destinations of real
/// sorts tells a destination of RomSieve from a folder chosen by mistake.
/// </summary>
public sealed class PurgeQuarantineTests : IDisposable
{
    private const string GameA = "Game A (1990)(Ocean).adf";
    private const string GameB = "Game B (1991)(Ocean).adf";

    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");
    private readonly OperationJournalService _journal;
    private readonly SortProcessorService _processor;
    private readonly string _src;
    private readonly string _dst;

    public PurgeQuarantineTests()
    {
        var crcCache = new CrcCacheService(_temp.GetFilePath(Path.Combine("cache", "crc_cache.json")));
        _journal = new OperationJournalService(_temp.GetFilePath("journals"), _services.ChecksumService, crcCache);
        _processor = new SortProcessorService(
            _services.ChecksumService, _services.DatService, _services.NoIntroParser, _services.FilterService,
            _services.Organizer, _services.I18n, _services.OpticalCompanionService, _services.MameParser, crcCache, _journal);
        _src = _temp.CreateSubdirectory("src");
        _dst = _temp.CreateSubdirectory("dst");
        _temp.WriteText(Path.Combine("src", GameA), "A");
        _temp.WriteText(Path.Combine("src", GameB), "B");
    }

    public void Dispose() => _temp.Dispose();

    private SortConfig Config(bool purge = true, TransferMode mode = TransferMode.Copy) => new()
    {
        SourceDir = _src,
        DestinationDir = _dst,
        Pattern = "{publisher}/",
        TransferMode = mode,
        PurgeDestination = purge
    };

    /// <summary>Relative path and content of every file of a directory, to compare two states of a tree.</summary>
    private static Dictionary<string, string> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f), File.ReadAllText);

    private string QuarantineRoot => Path.Combine(_dst, DestinationGuard.QuarantineDirectoryName);

    private void WritePreviousContent()
    {
        _temp.WriteText(Path.Combine("dst", "Ocean", "Old game (1985)(Ocean).adf"), "old ocean");
        _temp.WriteText(Path.Combine("dst", "Delphine", "Another World", "aw.adf"), "aw");
        _temp.WriteText(Path.Combine("dst", "notes.txt"), "user notes");
    }

    [Fact]
    public async Task Purge_ShouldMoveThePreviousContentToTheQuarantineWithoutDeletingIt()
    {
        WritePreviousContent();
        var before = Snapshot(_dst);

        var result = await _processor.ExecuteAsync(Config(), isDryRun: false);

        result.Stats.ErrorCount.Should().Be(0);
        File.ReadAllText(Path.Combine(_dst, "Ocean", GameA)).Should().Be("A");
        File.Exists(Path.Combine(_dst, "Ocean", "Old game (1985)(Ocean).adf")).Should().BeFalse("the destination was purged");
        var quarantine = Directory.GetDirectories(QuarantineRoot).Should().ContainSingle().Subject;
        Snapshot(quarantine).Should().BeEquivalentTo(before, "every file of the destination is kept in the quarantine");
        File.Exists(Path.Combine(_dst, DestinationGuard.MarkerFileName)).Should().BeTrue();
    }

    [Fact]
    public async Task UndoOfAPurge_ShouldRestoreTheDestinationExactly()
    {
        WritePreviousContent();
        var before = Snapshot(_dst);
        await _processor.ExecuteAsync(Config(), isDryRun: false);

        var report = await _journal.UndoAsync(_journal.FindLastUndoableJournal()!.Path);

        report.Issues.Should().BeEmpty();
        Snapshot(_dst).Should().BeEquivalentTo(before, "the sorted files are removed and the purged content is put back, Ocean/ included");
        Directory.Exists(QuarantineRoot).Should().BeFalse("the emptied quarantine is removed");
        File.Exists(Path.Combine(_dst, DestinationGuard.MarkerFileName)).Should().BeFalse("the marker created by the sort is removed");
        File.ReadAllText(Path.Combine(_src, GameA)).Should().Be("A");
    }

    [Fact]
    public async Task SecondPurge_ShouldKeepThePreviousQuarantine()
    {
        await _processor.ExecuteAsync(Config(purge: false), isDryRun: false);
        await _processor.ExecuteAsync(Config(), isDryRun: false);
        await Task.Delay(1100);
        await _processor.ExecuteAsync(Config(), isDryRun: false);

        var quarantines = Directory.GetDirectories(QuarantineRoot);
        quarantines.Should().HaveCount(2, "each purge has its own dated directory and the quarantine is never purged");
        quarantines.Should().OnlyContain(q => File.Exists(Path.Combine(q, "Ocean", GameA)));
        File.Exists(Path.Combine(_dst, "Ocean", GameB)).Should().BeTrue();
    }

    [Fact]
    public async Task UndoOfAPurge_ShouldNeverOverwriteAnOccupiedOriginalLocation()
    {
        _temp.WriteText(Path.Combine("dst", "notes.txt"), "old notes");
        await _processor.ExecuteAsync(Config(), isDryRun: false);
        _temp.WriteText(Path.Combine("dst", "notes.txt"), "written after the sort");

        var report = await _journal.UndoAsync(_journal.FindLastUndoableJournal()!.Path);

        report.Issues.Should().ContainSingle(i => i.Kind == UndoIssueKind.OriginOccupied && i.Operation.Kind == JournalOperationKind.Quarantine);
        File.ReadAllText(Path.Combine(_dst, "notes.txt")).Should().Be("written after the sort");
        Directory.EnumerateFiles(QuarantineRoot, "notes.txt", SearchOption.AllDirectories)
            .Should().ContainSingle().Which.Should().Match(f => File.ReadAllText(f) == "old notes");
    }

    [Fact]
    public async Task DryRun_ShouldNeitherPurgeNorMark()
    {
        WritePreviousContent();
        var before = Snapshot(_dst);

        await _processor.ExecuteAsync(Config(), isDryRun: true);

        Snapshot(_dst).Should().BeEquivalentTo(before);
        Directory.Exists(QuarantineRoot).Should().BeFalse();
    }

    [Fact]
    public async Task AppendWithoutPurge_ShouldMarkTheDestinationWithoutMovingAnything()
    {
        WritePreviousContent();

        var config = Config(purge: false);
        config.AppendToDestination = true;

        await _processor.ExecuteAsync(config, isDryRun: false);

        File.Exists(Path.Combine(_dst, "notes.txt")).Should().BeTrue();
        Directory.Exists(QuarantineRoot).Should().BeFalse();
        DestinationGuard.Inspect(_dst).Should().Be(new DestinationState(HasContent: true, HasMarker: true));
    }

    [Fact]
    public void Inspect_ShouldIgnoreTheMarkerAndTheQuarantine()
    {
        DestinationGuard.Inspect(Path.Combine(_temp.Path, "missing")).Should().Be(new DestinationState(false, false));
        DestinationGuard.Inspect(_dst).Should().Be(new DestinationState(false, false));

        _temp.WriteText(Path.Combine("dst", "user.txt"), "user");
        DestinationGuard.Inspect(_dst).IsForeign.Should().BeTrue();

        File.Delete(Path.Combine(_dst, "user.txt"));
        DestinationGuard.WriteMarker(_dst).Should().NotBeNull();
        DestinationGuard.WriteMarker(_dst).Should().BeNull("an existing marker is kept");
        _temp.WriteText(Path.Combine("dst", DestinationGuard.QuarantineDirectoryName, "20260101-000000", "old.adf"), "old");
        DestinationGuard.Inspect(_dst).Should().Be(new DestinationState(HasContent: false, HasMarker: true));

        _temp.WriteText(Path.Combine("dst", "Ocean", GameA), "A");
        DestinationGuard.Inspect(_dst).Should().Be(new DestinationState(HasContent: true, HasMarker: true));
        DestinationGuard.Inspect(_dst).IsForeign.Should().BeFalse();
    }

    /// <summary>What the root of a USB stick or a memory card holds before any sort: folders and files of the system only.</summary>
    private void WriteSystemContent()
    {
        _temp.WriteText(Path.Combine("dst", "System Volume Information", "WPSettings.dat"), "volume");
        _temp.WriteText(Path.Combine("dst", "$RECYCLE.BIN", "S-1-5-21", "desktop.ini"), "recycle bin");
        _temp.CreateSubdirectory(Path.Combine("dst", "lost+found"));
        _temp.WriteText(Path.Combine("dst", ".Trash-1000", "files", "deleted.adf"), "deleted");
        _temp.WriteText(Path.Combine("dst", ".DS_Store"), "finder");
    }

    [Fact]
    public void Inspect_RootOfADisk_ShouldBeEmptyWithItsSystemFoldersOnly()
    {
        WriteSystemContent();

        DestinationGuard.Inspect(_dst).Should().Be(new DestinationState(HasContent: false, HasMarker: false));
        DestinationGuard.GetPurgeableEntries(_dst).Should().BeEmpty();

        _temp.WriteText(Path.Combine("dst", "notes.txt"), "user notes");
        DestinationGuard.GetPurgeableEntries(_dst).Should().Equal(Path.Combine(_dst, "notes.txt"));
        DestinationGuard.Inspect(_dst).IsForeign.Should().BeTrue();
    }

    [Fact]
    public async Task Purge_ShouldNeverMoveTheSystemFolders()
    {
        WriteSystemContent();
        var system = Snapshot(_dst);
        WritePreviousContent();

        var result = await _processor.ExecuteAsync(Config(), isDryRun: false);

        result.Stats.ErrorCount.Should().Be(0);
        foreach (var (path, content) in system)
        {
            File.ReadAllText(Path.Combine(_dst, path)).Should().Be(content, "a folder of the system stays where it is");
        }

        Directory.Exists(Path.Combine(_dst, "lost+found")).Should().BeTrue();
        var quarantine = Directory.GetDirectories(QuarantineRoot).Should().ContainSingle().Subject;
        Directory.EnumerateFileSystemEntries(quarantine).Select(Path.GetFileName).Should().BeEquivalentTo("Delphine", "Ocean", "notes.txt");

        (await _journal.UndoAsync(result.JournalPath!)).Issues.Should().BeEmpty();
        File.ReadAllText(Path.Combine(_dst, "notes.txt")).Should().Be("user notes");
        Snapshot(_dst).Keys.Should().Contain(system.Keys);
    }

    [Fact]
    public async Task FormerDestinationUsedAsSource_ShouldNotScanItsQuarantine()
    {
        await _processor.ExecuteAsync(Config(purge: false), isDryRun: false);
        await _processor.ExecuteAsync(Config(), isDryRun: false);
        var config = new SortConfig { SourceDir = _dst, DestinationDir = _temp.CreateSubdirectory("resorted") };

        var (entries, _) = new SourceScanner(config, _services.ChecksumService, _services.I18n, _services.OpticalCompanionService).Scan();

        entries.Select(e => e.SourcePath).Should().HaveCount(2)
            .And.OnlyContain(p => !p.Contains(DestinationGuard.QuarantineDirectoryName) && Path.GetFileName(p) != DestinationGuard.MarkerFileName);
    }

    [Fact]
    public void QuarantineOfAnUnmarkedFolder_ShouldBeScannedAsOrdinaryContent()
    {
        var file = _temp.WriteText(Path.Combine("src", DestinationGuard.QuarantineDirectoryName, "Kept (1990)(Ocean).adf"), "kept");

        DestinationGuard.IsGuardPath(file).Should().BeFalse("only the quarantine of a marked destination is skipped");
        DestinationGuard.IsGuardPath(Path.Combine(_src, DestinationGuard.MarkerFileName)).Should().BeTrue();
    }

    // =========================================================================
    // Size and emptying of the quarantine
    // =========================================================================

    [Fact]
    public async Task Quarantine_ShouldBeMeasuredOnlyForAMarkedDestination()
    {
        DestinationGuard.GetQuarantine(_dst).Should().BeNull("an unsorted destination has no quarantine");
        _temp.WriteText(Path.Combine("dst", DestinationGuard.QuarantineDirectoryName, "kept.adf"), "kept");
        DestinationGuard.GetQuarantine(_dst).Should().BeNull("the _purged folder of an unmarked destination is ordinary content");

        File.Delete(Path.Combine(QuarantineRoot, "kept.adf"));
        WritePreviousContent();
        await _processor.ExecuteAsync(Config(), isDryRun: false);

        var quarantine = DestinationGuard.GetQuarantine(_dst)!;
        quarantine.Path.Should().Be(QuarantineRoot);
        quarantine.PurgeCount.Should().Be(1);
        quarantine.FileCount.Should().Be(3);
        quarantine.TotalBytes.Should().Be("old ocean".Length + "aw".Length + "user notes".Length);
    }

    [Fact]
    public async Task EmptyQuarantine_ShouldDeleteItsContentAndTellTheUndo()
    {
        WritePreviousContent();
        await _processor.ExecuteAsync(Config(), isDryRun: false);
        var sorted = Snapshot(_dst).Where(f => !f.Key.StartsWith(DestinationGuard.QuarantineDirectoryName, StringComparison.Ordinal)).ToList();

        var result = DestinationGuard.EmptyQuarantine(_dst);

        result.Errors.Should().BeEmpty();
        result.DeletedFiles.Should().Be(3);
        result.DeletedBytes.Should().Be("old ocean".Length + "aw".Length + "user notes".Length);
        Directory.Exists(QuarantineRoot).Should().BeFalse("the emptied quarantine is removed");
        DestinationGuard.GetQuarantine(_dst).Should().BeNull();
        Snapshot(_dst).Should().BeEquivalentTo(sorted, "only the quarantine is deleted, never the sorted files");
        File.Exists(Path.Combine(_dst, DestinationGuard.MarkerFileName)).Should().BeTrue();

        // The purged content can no longer be put back: the undo reports it as missing and undoes the rest
        var report = await _journal.UndoAsync(_journal.FindLastUndoableJournal()!.Path);
        report.Issues.Should().OnlyContain(i => i.Kind == UndoIssueKind.TargetMissing && i.Operation.Kind == JournalOperationKind.Quarantine)
            .And.HaveCount(3);
        File.Exists(Path.Combine(_dst, "Ocean", GameA)).Should().BeFalse("the sorted files are still undone");
    }

    [Fact]
    public void EmptyQuarantine_OfAnUnmarkedFolder_ShouldDeleteNothing()
    {
        var kept = _temp.WriteText(Path.Combine("dst", DestinationGuard.QuarantineDirectoryName, "kept.adf"), "kept");

        var result = DestinationGuard.EmptyQuarantine(_dst);

        result.DeletedFiles.Should().Be(0);
        File.Exists(kept).Should().BeTrue("a _purged folder without the marker is not a quarantine of RomSieve");
    }

    [Fact]
    public void EmptyQuarantine_ShouldDeleteLinksWithoutTouchingTheirTargets()
    {
        DestinationGuard.WriteMarker(_dst);
        var outside = _temp.CreateSubdirectory("outside");
        var outsideFile = _temp.WriteText(Path.Combine("outside", "precious.adf"), "precious");
        var batch = Directory.CreateDirectory(Path.Combine(QuarantineRoot, "20260101-000000")).FullName;
        _temp.WriteText(Path.Combine("dst", DestinationGuard.QuarantineDirectoryName, "20260101-000000", "old.adf"), "old");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(batch, "linked folder"), outside);
            File.CreateSymbolicLink(Path.Combine(batch, "linked.adf"), outsideFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Creating a symbolic link needs a privilege on Windows: nothing to check without one
            return;
        }

        DestinationGuard.GetQuarantine(_dst)!.FileCount.Should().Be(3, "a link counts as one entry, never followed");
        var result = DestinationGuard.EmptyQuarantine(_dst);

        result.Errors.Should().BeEmpty();
        Directory.Exists(QuarantineRoot).Should().BeFalse();
        File.ReadAllText(outsideFile).Should().Be("precious", "the target of a link is never deleted");
        Directory.Exists(outside).Should().BeTrue();
    }

    [Fact]
    public void Quarantine_ThatIsALink_ShouldBeLeftAlone()
    {
        DestinationGuard.WriteMarker(_dst);
        var elsewhere = _temp.CreateSubdirectory("elsewhere");
        var precious = _temp.WriteText(Path.Combine("elsewhere", "precious.adf"), "precious");
        try
        {
            Directory.CreateSymbolicLink(QuarantineRoot, elsewhere);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        DestinationGuard.GetQuarantine(_dst).Should().BeNull("a quarantine is never a link");
        DestinationGuard.EmptyQuarantine(_dst).DeletedFiles.Should().Be(0);
        File.Exists(precious).Should().BeTrue();
    }
}
