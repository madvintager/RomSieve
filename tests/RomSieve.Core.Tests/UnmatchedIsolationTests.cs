using System.IO;
using System.Text.Json;
using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// Isolation of the non-conforming files (<see cref="SortConfig.IsolateUnmatched"/>): on by default, they are written in
/// <c>_unmatched</c>; off, the sort leaves them in the source, while the results still show them as non-conforming.
/// </summary>
public sealed class UnmatchedIsolationTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");
    private readonly CrcCacheService _crcCache;
    private readonly OperationJournalService _journal;
    private readonly SortProcessorService _processor;
    private readonly string _src;
    private readonly string _dst;

    public UnmatchedIsolationTests()
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
        _temp.WriteText(Path.Combine("src", "Alpha (1990)(Ocean).adf"), "alpha");
        _temp.WriteText(Path.Combine("src", "readme.txt"), "a non-conforming file");
    }

    public void Dispose() => _temp.Dispose();

    private sealed class LogCollector : IProgress<LogMessage>
    {
        public List<LogMessage> Messages { get; } = [];

        public void Report(LogMessage value) => Messages.Add(value);
    }

    private SortConfig Config(bool isolate, TransferMode mode = TransferMode.Copy) => new()
    {
        SourceDir = _src,
        DestinationDir = _dst,
        Pattern = "{title}/",
        TransferMode = mode,
        NamingConvention = NamingConvention.Tosec,
        IsolateUnmatched = isolate
    };

    private IEnumerable<string> DestinationFiles() =>
        Directory.EnumerateFiles(_dst, "*", SearchOption.AllDirectories)
            .Where(p => !DestinationGuard.IsGuardPath(p))
            .Select(p => Path.GetRelativePath(_dst, p).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

    [Fact]
    public void Isolation_ShouldBeOnByDefault()
    {
        new SortConfig { SourceDir = "a", DestinationDir = "b" }.IsolateUnmatched.Should().BeTrue();
        new SortSettings().IsolateUnmatched.Should().BeTrue();
        SortSettingsSerializer.Read(new Dictionary<string, object?>()).IsolateUnmatched
            .Should().BeTrue("a configuration without the key keeps isolating the non-conforming files, as before");
    }

    [Fact]
    public void Option_ShouldBeReadWrittenAndPassedToTheRun()
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal);
        SortSettingsSerializer.Write(new SortSettings { IsolateUnmatched = false }, data);
        data["isolate_unmatched"].Should().Be(false);

        var json = JsonSerializer.Serialize(data);
        var read = SortSettingsSerializer.Read(JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!);
        read.IsolateUnmatched.Should().BeFalse();
        SortSettingsSerializer.ToSortConfig(read).IsolateUnmatched.Should().BeFalse();
    }

    [Fact]
    public async Task IsolationOn_ShouldWriteTheNonConformingFilesInUnmatched()
    {
        var result = await _processor.ExecuteAsync(Config(isolate: true), isDryRun: false);

        DestinationFiles().Should().Equal("Alpha/Alpha (1990)(Ocean).adf", "_unmatched/readme.txt");
        result.Items.Single(it => it.DisplayName == "readme.txt").IsNotIsolated.Should().BeFalse();
        result.Stats.NotIsolatedCount.Should().Be(0);
    }

    [Theory]
    [InlineData(TransferMode.Copy)]
    [InlineData(TransferMode.Move)]
    [InlineData(TransferMode.Zip)]
    public async Task IsolationOff_ShouldLeaveTheNonConformingFilesInTheSource(TransferMode mode)
    {
        var logs = new LogCollector();

        var result = await _processor.ExecuteAsync(Config(isolate: false, mode), isDryRun: false, logProgress: logs);

        DestinationFiles().Should().ContainSingle().Which.Should().StartWith("Alpha/");
        File.ReadAllText(Path.Combine(_src, "readme.txt")).Should().Be("a non-conforming file");
        var readme = result.Items.Single(it => it.DisplayName == "readme.txt");
        readme.Status.Should().Be(FileStatus.Unmatched, "the results still show it as non-conforming");
        readme.IsNotIsolated.Should().BeTrue();
        readme.FinalTargetPath.Should().BeNull();
        readme.StatusReason!.Key.Should().StartWith("reason.unmatched_not_isolated");
        result.Stats.UnmatchedCount.Should().Be(1);
        result.Stats.NotIsolatedCount.Should().Be(1);
        result.Stats.IsolatedUnmatchedCount.Should().Be(0);
        result.Stats.OutputBytes.Should().Be(result.Stats.ValidBytes);
        logs.Messages.Should().ContainSingle(m => m.Message.Key == "log.unmatched_not_isolated");

        var journal = await File.ReadAllTextAsync(result.JournalPath!);
        journal.Should().NotContain("readme.txt");
    }

    [Fact]
    public async Task IsolationOff_Simulation_ShouldCountTheFilesApartWithoutTarget()
    {
        var result = await _processor.ExecuteAsync(Config(isolate: false), isDryRun: true);

        var readme = result.Items.Single(it => it.DisplayName == "readme.txt");
        readme.IsNotIsolated.Should().BeTrue();
        readme.TargetPath.Should().BeNull();
        SelectionFilter.IsWritten(readme).Should().BeFalse();
        result.Stats.OutputBytes.Should().Be(result.Stats.ValidBytes);

        var recomputed = _processor.RecomputeStats(result.Items);
        recomputed.NotIsolatedCount.Should().Be(1);
        recomputed.OutputBytes.Should().Be(recomputed.ValidBytes);
    }

    [Fact]
    public async Task IsolationOff_PartialSort_ShouldNeverChooseNorWriteANonConformingFile()
    {
        var simulation = await _processor.ExecuteAsync(Config(isolate: false), isDryRun: true);
        var selection = SelectionFilter.Create(simulation.Items, simulation.Items, _services.FilterService);

        SelectionFilter.GetWrittenItems(simulation.Items, selection, _services.FilterService)
            .Should().ContainSingle().Which.DisplayName.Should().Be("Alpha (1990)(Ocean).adf");

        var result = await _processor.ExecuteAsync(Config(isolate: false), isDryRun: false, selection: selection);

        DestinationFiles().Should().Equal("Alpha/Alpha (1990)(Ocean).adf");
        result.Items.Single(it => it.DisplayName == "readme.txt").Status.Should().Be(FileStatus.Unmatched);
    }

    [Fact]
    public void Fat32_ShouldIgnoreANonConformingFileThatIsNotWritten()
    {
        var item = new ProcessItem
        {
            SourcePath = "big.iso",
            Parsed = new TosecParser().ParseFilename("big.iso"),
            FileSize = Fat32Limit.MaxFileSize + 1,
            Status = FileStatus.Unmatched
        };
        Fat32Limit.FindOversizedOutputFiles([item], TransferMode.Copy).Should().ContainSingle();

        item.IsNotIsolated = true;
        Fat32Limit.FindOversizedOutputFiles([item], TransferMode.Copy).Should().BeEmpty();
    }
}
