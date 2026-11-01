using System.IO;
using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// Partial sort (<see cref="SortSelection"/>, <see cref="SelectionFilter"/>): the whole collection is resolved as for a
/// full sort, but only the chosen games (all their disks, tracks and playlists) and non-conforming files are written.
/// </summary>
public sealed class PartialSortTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");
    private readonly CrcCacheService _crcCache;
    private readonly OperationJournalService _journal;
    private readonly SortProcessorService _processor;
    private readonly string _src;
    private readonly string _dst;

    public PartialSortTests()
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

    /// <summary>Synchronous progress, so that the messages are all there when the run returns.</summary>
    private sealed class LogCollector : IProgress<LogMessage>
    {
        public List<LogMessage> Messages { get; } = [];

        public void Report(LogMessage value) => Messages.Add(value);

        public IEnumerable<LogMessage> WithKey(string key) => Messages.Where(m => m.Message.Key == key);
    }

    private string WriteSource(string name, string? content = null) =>
        _temp.WriteText(Path.Combine("src", name), content ?? name);

    private SortConfig TosecConfig(TransferMode mode = TransferMode.Copy) => new()
    {
        SourceDir = _src,
        DestinationDir = _dst,
        Pattern = "{title}/",
        TransferMode = mode,
        NamingConvention = NamingConvention.Tosec,
        Filters = new FilterConfig
        {
            OneGameOneRom = true,
            PriorityLanguages = ["en"],
            ExcludeModifications = false,
            SmartMultidiskGrouping = true,
            CompleteMultidiskSetsOnly = true
        }
    };

    /// <summary>Simulates the collection, then selects the units of the files whose name contains one of the titles.</summary>
    private async Task<(SortExecutionResult Simulation, SortSelection Selection)> SimulateAndSelectAsync(SortConfig config, params string[] titles)
    {
        var simulation = await _processor.ExecuteAsync(config, isDryRun: true);
        var chosen = simulation.Items.Where(it => titles.Any(t => it.DisplayName.Contains(t, StringComparison.Ordinal)));
        return (simulation, SelectionFilter.Create(simulation.Items, chosen, _services.FilterService));
    }

    private IEnumerable<string> DestinationFiles() =>
        Directory.EnumerateFiles(_dst, "*", SearchOption.AllDirectories)
            .Where(p => !DestinationGuard.IsGuardPath(p))
            .Select(p => Path.GetRelativePath(_dst, p).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

    [Fact]
    public async Task PartialSort_ShouldWriteOnlyTheChosenGamesWithTheSameWinnerAsTheFullSort()
    {
        WriteSource("Alpha (1990)(Ocean)(de).adf");
        WriteSource("Alpha (1990)(Ocean)(en).adf");
        WriteSource("Beta (1991)(Ocean)(en).adf");
        WriteSource("Gamma (1992)(Ocean).adf");
        var (_, selection) = await SimulateAndSelectAsync(TosecConfig(), "Alpha (1990)(Ocean)(de)", "Gamma");

        var result = await _processor.ExecuteAsync(TosecConfig(), isDryRun: false, selection: selection);

        // Choosing the eliminated German release still selects the game, whose kept release is the English one
        DestinationFiles().Should().Equal("Alpha/Alpha (1990)(Ocean)(en).adf", "Gamma/Gamma (1992)(Ocean).adf");
        result.Stats.ValidCount.Should().Be(2);
        result.Stats.NotSelectedCount.Should().Be(1, "Beta would have been written");
        result.Stats.ExcludedCount.Should().Be(1, "the German release of Alpha keeps its 1G1R reason");
        var beta = result.Items.Single(it => it.DisplayName.StartsWith("Beta", StringComparison.Ordinal));
        beta.Status.Should().Be(FileStatus.Excluded);
        beta.IsOutsideSelection.Should().BeTrue();
        beta.StatusReason!.Key.Should().Be("reason.not_in_selection");
        result.Items.Single(it => it.DisplayName.Contains("(de)", StringComparison.Ordinal)).StatusReason!.Key
            .Should().NotBe("reason.not_in_selection");
    }

    [Fact]
    public async Task PartialSort_ShouldKeepTheWinnerOfTheFullSortWhateverIsChosen()
    {
        WriteSource("Alpha (1990)(Ocean)(de).adf");
        WriteSource("Alpha (1990)(Ocean)(en).adf");
        WriteSource("Alpha (1990)(Ocean)(fr).adf");
        WriteSource("Beta (1991)(Ocean)(en).adf");
        var full = await _processor.ExecuteAsync(TosecConfig(), isDryRun: true);
        var (_, selection) = await SimulateAndSelectAsync(TosecConfig(), "Alpha (1990)(Ocean)(fr)");

        var partial = await _processor.ExecuteAsync(TosecConfig(), isDryRun: true, selection: selection);

        static string Winner(SortExecutionResult r) =>
            r.Items.Single(it => it.Parsed.Title == "Alpha" && it.Status == FileStatus.Valid).DisplayName;
        Winner(partial).Should().Be(Winner(full)).And.Be("Alpha (1990)(Ocean)(en).adf");
        partial.Items.Where(it => it.Parsed.Title == "Alpha").Select(it => it.ReleaseStatus)
            .Should().Equal(full.Items.Where(it => it.Parsed.Title == "Alpha").Select(it => it.ReleaseStatus));
    }

    [Fact]
    public async Task PartialSort_ShouldWriteTheWholeMultiDiskSetWithItsBorrowedDisksAndItsPlaylistOnly()
    {
        string[] files =
        [
            "Putty (1992)(System 3)(Disk 1 of 3)[cr CSL].adf", "Putty (1992)(System 3)(Disk 2 of 3).adf", "Putty (1992)(System 3)(Disk 3 of 3).adf",
            "Zak (1988)(Lucas)(Disk 1 of 2).adf", "Zak (1988)(Lucas)(Disk 2 of 2).adf",
        ];
        foreach (var name in files)
        {
            WriteSource(name);
        }

        var config = TosecConfig();
        config.GenerateM3u = true;
        var (_, selection) = await SimulateAndSelectAsync(config, "Putty (1992)(System 3)(Disk 1 of 3)");

        config = TosecConfig();
        config.GenerateM3u = true;
        await _processor.ExecuteAsync(config, isDryRun: false, selection: selection);

        DestinationFiles().Where(f => f.EndsWith(".adf", StringComparison.Ordinal)).Should().HaveCount(3)
            .And.OnlyContain(f => f.StartsWith("Putty/", StringComparison.Ordinal));
        var playlists = DestinationFiles().Where(f => f.EndsWith(".m3u", StringComparison.Ordinal)).ToList();
        playlists.Should().ContainSingle().Which.Should().Contain("Putty");
        File.ReadAllLines(Path.Combine(_dst, playlists[0])).Where(l => l.Length > 0).Should().HaveCount(3);
    }

    [Theory]
    [InlineData(TransferMode.Copy)]
    [InlineData(TransferMode.Zip)]
    public async Task PartialSort_ShouldWriteADescriptorWithItsTracks(TransferMode mode)
    {
        foreach (var title in new[] { "Dennis", "Other" })
        {
            WriteSource($"{title} (Europe).cue", $"FILE \"{title} (Europe) (Track 01).bin\" BINARY\r\n  TRACK 01 MODE1/2352\r\n    INDEX 01 00:00:00\r\n");
            WriteSource($"{title} (Europe) (Track 01).bin", "TRACK-" + title);
        }

        SortConfig Config() => new()
        {
            SourceDir = _src,
            DestinationDir = _dst,
            Pattern = string.Empty,
            NamingConvention = NamingConvention.Redump,
            TransferMode = mode
        };
        var (_, selection) = await SimulateAndSelectAsync(Config(), "Dennis (Europe).cue");

        var result = await _processor.ExecuteAsync(Config(), isDryRun: false, selection: selection);

        if (mode == TransferMode.Zip)
        {
            DestinationFiles().Should().Equal("Dennis (Europe).zip");
        }
        else
        {
            DestinationFiles().Should().Equal("Dennis (Europe) (Track 01).bin", "Dennis (Europe).cue");
        }

        var other = result.Items.Single(it => it.DisplayName == "Other (Europe).cue");
        other.IsOutsideSelection.Should().BeTrue();
        other.RelatedItems.Should().OnlyContain(r => r.IsOutsideSelection && r.Status == FileStatus.Excluded);
    }

    [Fact]
    public async Task PartialSort_ShouldIsolateANonConformingFileOnlyWhenItIsChosen()
    {
        WriteSource("Alpha (1990)(Ocean).adf");
        WriteSource("readme.txt");
        WriteSource("notes.txt");
        var (_, selection) = await SimulateAndSelectAsync(TosecConfig(), "Alpha", "notes.txt");

        var result = await _processor.ExecuteAsync(TosecConfig(), isDryRun: false, selection: selection);

        DestinationFiles().Should().Equal("Alpha/Alpha (1990)(Ocean).adf", "_unmatched/notes.txt");
        result.Stats.UnmatchedCount.Should().Be(1);
        result.Stats.NotSelectedCount.Should().Be(1);
        var readme = result.Items.Single(it => it.DisplayName == "readme.txt");
        readme.FinalTargetPath.Should().Be(Path.Combine(_dst, PathOrganizerService.UnmatchedFolderName, "readme.txt"));
    }

    [Fact]
    public async Task PartialSort_ShouldRefuseToPurgeAndLeaveTheDestinationUntouched()
    {
        WriteSource("Alpha (1990)(Ocean).adf");
        var existing = _temp.WriteText(Path.Combine("dst", "existing.adf"), "kept");
        var (_, selection) = await SimulateAndSelectAsync(TosecConfig(), "Alpha");
        var config = TosecConfig();
        config.PurgeDestination = true;

        var act = () => _processor.ExecuteAsync(config, isDryRun: false, selection: selection);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(_services.I18n.Tr("log.partial_sort_purge_refused"));
        File.ReadAllText(existing).Should().Be("kept");
        Directory.EnumerateFileSystemEntries(_dst).Should().Equal(existing);
        _journal.FindLastUndoableJournal().Should().BeNull();
    }

    [Fact]
    public async Task PartialSort_ShouldJournalOnlyTheSelectionAndBeUndoneExactly()
    {
        var alpha = WriteSource("Alpha (1990)(Ocean).adf");
        var beta = WriteSource("Beta (1991)(Ocean).adf");
        var (_, selection) = await SimulateAndSelectAsync(TosecConfig(TransferMode.Move), "Alpha");

        var result = await _processor.ExecuteAsync(TosecConfig(TransferMode.Move), isDryRun: false, selection: selection);

        File.Exists(alpha).Should().BeFalse();
        File.ReadAllText(beta).Should().Be("Beta (1991)(Ocean).adf", "a file left out of the selection is never touched");
        var last = _journal.FindLastUndoableJournal();
        last!.Path.Should().Be(result.JournalPath);
        last.IsPartial.Should().BeTrue();
        last.SelectedCount.Should().Be(1);
        last.OperationCount.Should().Be(2, "the move of Alpha and the marker of the new destination");

        var report = await _journal.UndoAsync(last.Path);

        report.Issues.Should().BeEmpty();
        File.ReadAllText(alpha).Should().Be("Alpha (1990)(Ocean).adf");
        File.Exists(beta).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(_dst).Should().BeEmpty();
    }

    [Fact]
    public async Task FullSort_ShouldNotBeJournaledAsPartial()
    {
        WriteSource("Alpha (1990)(Ocean).adf");

        await _processor.ExecuteAsync(TosecConfig(), isDryRun: false);

        _journal.FindLastUndoableJournal()!.IsPartial.Should().BeFalse();
    }

    [Fact]
    public async Task PartialSort_OutputBytesAndFat32Warnings_ShouldOnlyCountTheSelection()
    {
        WriteSource("Alpha (1990)(Ocean).adf", new string('a', 100));
        WriteSource("Beta (1991)(Ocean).adf", new string('b', 1000));
        WriteSource("junk.bin", new string('j', 10));
        var (simulation, selection) = await SimulateAndSelectAsync(TosecConfig(), "Alpha", "junk.bin");

        var written = SelectionFilter.GetWrittenItems(simulation.Items, selection, _services.FilterService);
        var projected = _processor.RecomputeStats(written.ToList());
        var result = await _processor.ExecuteAsync(TosecConfig(), isDryRun: false, selection: selection);

        projected.OutputBytes.Should().Be(110);
        result.Stats.OutputBytes.Should().Be(110);
        result.Stats.NotSelectedBytes.Should().Be(1000);

        // A file of the selection too large for FAT32 is reported, one left out of it is not
        var beta = simulation.Items.Single(it => it.DisplayName.StartsWith("Beta", StringComparison.Ordinal));
        beta.FileSize = Fat32Limit.MaxFileSize + 1;
        Fat32Limit.FindOversizedOutputFiles(SelectionFilter.GetWrittenItems(simulation.Items, selection, _services.FilterService), TransferMode.Copy)
            .Should().BeEmpty();
        var alpha = simulation.Items.Single(it => it.DisplayName.StartsWith("Alpha", StringComparison.Ordinal));
        alpha.FileSize = Fat32Limit.MaxFileSize + 1;
        Fat32Limit.FindOversizedOutputFiles(SelectionFilter.GetWrittenItems(simulation.Items, selection, _services.FilterService), TransferMode.Copy)
            .Should().ContainSingle().Which.Name.Should().Be("Alpha (1990)(Ocean).adf");
    }

    [Fact]
    public async Task PartialSort_ShouldReportAGameThatDisappearedAndStillSortTheRest()
    {
        var alpha = WriteSource("Alpha (1990)(Ocean).adf");
        WriteSource("Beta (1991)(Ocean).adf");
        var (_, selection) = await SimulateAndSelectAsync(TosecConfig(), "Alpha", "Beta");
        File.Delete(alpha);
        var log = new LogCollector();

        await _processor.ExecuteAsync(TosecConfig(), isDryRun: false, logProgress: log, selection: selection);

        log.WithKey("log.selection_missing").Should().ContainSingle()
            .Which.Message.Args["name"].Should().Be("Alpha");
        DestinationFiles().Should().Equal("Beta/Beta (1991)(Ocean).adf");
    }

    [Fact]
    public async Task PartialSort_ShouldWriteNothingForAGameWhoseKeptReleaseChanged()
    {
        WriteSource("Alpha (1990)(Ocean)(de).adf");
        WriteSource("Beta (1991)(Ocean).adf");
        var (_, selection) = await SimulateAndSelectAsync(TosecConfig(), "Alpha", "Beta");

        // A better release appears after the simulation: the sort would no longer write what the table showed
        WriteSource("Alpha (1990)(Ocean)(en).adf");
        var log = new LogCollector();
        var result = await _processor.ExecuteAsync(TosecConfig(), isDryRun: false, logProgress: log, selection: selection);

        log.WithKey("log.selection_changed").Should().ContainSingle();
        DestinationFiles().Should().Equal("Beta/Beta (1991)(Ocean).adf");
        result.Items.Single(it => it.DisplayName == "Alpha (1990)(Ocean)(en).adf").StatusReason!.Key
            .Should().Be("reason.selection_changed");
        log.WithKey("log.operation_completed_summary_partial").Should().ContainSingle();
    }

    [Fact]
    public async Task SelectionWithoutExpectedFiles_ShouldWriteWhateverTheRunKeeps()
    {
        WriteSource("Alpha (1990)(Ocean)(de).adf");
        WriteSource("Alpha (1990)(Ocean)(en).adf");
        WriteSource("Beta (1991)(Ocean).adf");
        var simulation = await _processor.ExecuteAsync(TosecConfig(), isDryRun: true);
        var alphaKey = SelectionFilter.GetUnitKey(simulation.Items.First(it => it.Parsed.Title == "Alpha"), _services.FilterService);

        // Entry point of a future command line option: no simulation to compare with
        await _processor.ExecuteAsync(TosecConfig(), isDryRun: false, selection: new SortSelection([new SortSelectionUnit(alphaKey, "Alpha")]));

        DestinationFiles().Should().Equal("Alpha/Alpha (1990)(Ocean)(en).adf");
    }

    [Fact]
    public async Task Simulation_ShouldLogWhatItWouldWriteAsPlanned_AndTheSortOnlyWhatItWrites()
    {
        WriteSource("Alpha (1990)(Ocean)(en).adf");
        WriteSource("Beta (1991)(Ocean)(en).adf");
        WriteSource("notes.txt");
        var simulationLog = new LogCollector();
        var simulation = await _processor.ExecuteAsync(TosecConfig(), isDryRun: true, logProgress: simulationLog);
        var chosen = simulation.Items.Where(it => it.DisplayName.StartsWith("Alpha", StringComparison.Ordinal));
        var selection = SelectionFilter.Create(simulation.Items, chosen, _services.FilterService);
        var sortLog = new LogCollector();

        await _processor.ExecuteAsync(TosecConfig(), isDryRun: false, logProgress: sortLog, selection: selection);

        // A simulation writes nothing: its lines are never OK, which only marks what a real sort has written
        simulationLog.WithKey("log.item_target").Should().HaveCount(3).And.OnlyContain(m => m.Level == LogLevel.Planned);
        simulationLog.WithKey("log.item_target").Select(m => (string?)m.Message.Args["target"]).Should()
            .Contain(Path.Combine(PathOrganizerService.UnmatchedFolderName, "notes.txt"));
        sortLog.WithKey("log.item_target").Should().ContainSingle()
            .Which.Should().Match<LogMessage>(m => m.Level == LogLevel.Ok && (string?)m.Message.Args["name"] == "Alpha (1990)(Ocean)(en).adf");
        sortLog.Messages.Should().NotContain(m => m.Level == LogLevel.Planned);
    }
}
