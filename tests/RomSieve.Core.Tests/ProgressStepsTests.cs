using System.IO;
using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// Progress messages of the pipeline: each step is named when it starts (listing the source files, loading the DAT,
/// analysis, selection, transfers, playlists), the steps without a count included, and the transfers are named after
/// the transfer mode.
/// </summary>
public sealed class ProgressStepsTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");

    public void Dispose() => _temp.Dispose();

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private SortConfig CreateConfig(TransferMode mode = TransferMode.Copy)
    {
        _temp.WriteText(Path.Combine("src", "Game A (1990)(Ocean).adf"), "AAAA");
        _temp.WriteText(Path.Combine("src", "Game B (1991)(Ocean).adf"), "BBBBBB");
        return new SortConfig
        {
            SourceDir = Path.Combine(_temp.Path, "src"),
            DestinationDir = Path.Combine(_temp.Path, "dst"),
            TransferMode = mode
        };
    }

    /// <summary>Keys of the reported messages, consecutive repetitions merged.</summary>
    private static List<string> StepKeys(IEnumerable<SortProgress> reports)
    {
        var keys = new List<string>();
        foreach (var key in reports.Select(r => r.Message.Key))
        {
            if (keys.Count == 0 || keys[^1] != key)
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    [Fact]
    public async Task Simulation_ShouldNameEachStepInOrder()
    {
        var config = CreateConfig();
        config.DatFile = _temp.WriteText("test.dat", """
            <?xml version="1.0"?>
            <datafile>
              <header><name>Commodore Amiga - Games (TOSEC)</name><description>Test</description></header>
              <game name="Game A (1990)(Ocean)"><rom name="Game A (1990)(Ocean).adf" size="4" crc="12345678"/></game>
            </datafile>
            """);
        var reports = new List<SortProgress>();

        await _services.Processor.ExecuteAsync(config, isDryRun: true, progress: new SyncProgress<SortProgress>(reports.Add));

        var keys = StepKeys(reports);
        keys[0].Should().Be("progress.listing_files", "the listing of the source is the first step, long on a network share");
        keys.Should().ContainInOrder(
            "progress.listing_files",
            "progress.loading_dat",
            "progress.analyzing_file",
            "progress.selecting_releases",
            "progress.computing_targets");
        keys.Should().NotContain(k => k.EndsWith("_file", StringComparison.Ordinal) && k != "progress.analyzing_file"
            && k != "progress.scanning_file", "a simulation transfers nothing");
        keys.Should().NotContain("progress.preparing_destination");
        reports.Single(r => r.Message.Key == "progress.loading_dat").Message.Args["name"].Should().Be("test.dat");
        reports.Where(r => r.Message.Key == "progress.analyzing_file")
            .Select(r => (string?)r.Message.Args["name"])
            .Should().Contain("Game A (1990)(Ocean).adf");
    }

    [Fact]
    public async Task RealSort_ShouldNameTheCopiesAndThePlaylists()
    {
        var config = CreateConfig();
        config.GenerateM3u = true;
        var reports = new List<SortProgress>();

        await _services.Processor.ExecuteAsync(config, isDryRun: false, progress: new SyncProgress<SortProgress>(reports.Add));

        StepKeys(reports).Should().ContainInOrder(
            "progress.listing_files",
            "progress.selecting_releases",
            "progress.preparing_destination",
            "progress.copying_file",
            "progress.writing_playlists");
        StepKeys(reports).Should().NotContain("progress.computing_targets");
        reports.Where(r => r.Message.Key == "progress.copying_file")
            .Select(r => (string?)r.Message.Args["name"])
            .Should().Contain("Game B (1991)(Ocean).adf");
    }

    [Fact]
    public async Task RealSort_ShouldCountOnlyTheFilesItWrites()
    {
        var config = CreateConfig();
        _temp.WriteText(Path.Combine("src", "Game C (1992)(Ocean).adf"), "CCCCCCCC");
        var simulation = await _services.Processor.ExecuteAsync(config, isDryRun: true);
        var chosen = simulation.Items.Where(it => it.DisplayName.StartsWith("Game B", StringComparison.Ordinal));
        var selection = SelectionFilter.Create(simulation.Items, chosen, _services.FilterService);
        var reports = new List<SortProgress>();

        await _services.Processor.ExecuteAsync(config, isDryRun: false, progress: new SyncProgress<SortProgress>(reports.Add), selection: selection);

        // Games A and C are outside the selection: they are passed over, not counted among the copies
        var copies = reports.Where(r => r.Message.Key == "progress.copying_file").ToList();
        copies.Should().NotBeEmpty().And.OnlyContain(r => r.TotalCount == 1 && r.TotalBytes == 6);
        copies.Select(r => (string?)r.Message.Args["name"]).Should().OnlyContain(n => n == "Game B (1991)(Ocean).adf");
        copies[^1].ProcessedCount.Should().Be(1);
        copies[^1].ProcessedBytes.Should().Be(6);
    }

    [Theory]
    [InlineData(TransferMode.Copy, "progress.copying_file")]
    [InlineData(TransferMode.Move, "progress.moving_file")]
    [InlineData(TransferMode.HardLink, "progress.hardlinking_file")]
    [InlineData(TransferMode.SymbolicLink, "progress.symlinking_file")]
    [InlineData(TransferMode.Zip, "progress.zipping_file")]
    public void Transfer_ShouldBeNamedAfterTheTransferMode(TransferMode mode, string expectedKey)
    {
        var message = SortProcessorService.DescribeTransfer(mode, "Game A.adf");

        message.Key.Should().Be(expectedKey);
        message.Args["name"].Should().Be("Game A.adf");
        _services.I18n.Format(message).Should().EndWith(": Game A.adf");
    }

    [Fact]
    public async Task Steps_ShouldHaveNoCountWhileTheirLengthIsUnknown()
    {
        var config = CreateConfig();
        var reports = new List<SortProgress>();

        await _services.Processor.ExecuteAsync(config, isDryRun: true, progress: new SyncProgress<SortProgress>(reports.Add));

        reports.Where(r => r.Message.Key is "progress.listing_files" or "progress.selecting_releases" or "progress.computing_targets")
            .Should().OnlyContain(r => r.ProcessedCount == 0 && r.TotalCount == 0 && r.EtaSeconds == 0);
    }

    [Fact]
    public void ListingTheSource_ShouldBeCancellable()
    {
        var config = CreateConfig();
        using var cts = new CancellationTokenSource();
        var reports = new List<SortProgress>();
        var scanner = new SourceScanner(config, _services.ChecksumService, _services.I18n, _services.OpticalCompanionService);

        // The listing used to be one call that could not be stopped, minutes long on a large network share
        var act = () => scanner.Scan(cts.Token, new SyncProgress<SortProgress>(p =>
        {
            reports.Add(p);
            if (p.Message.Key == "progress.listing_files")
            {
                cts.Cancel();
            }
        }));

        act.Should().Throw<OperationCanceledException>();
        reports.Should().ContainSingle().Which.Message.Key.Should().Be("progress.listing_files");
    }

    [Fact]
    public void LinkingTracks_ShouldOnlyBeReportedWithADescriptor()
    {
        var config = CreateConfig();
        var reports = new List<SortProgress>();
        var scanner = new SourceScanner(config, _services.ChecksumService, _services.I18n, _services.OpticalCompanionService);

        scanner.Scan(progress: new SyncProgress<SortProgress>(reports.Add));
        reports.Should().NotContain(r => r.Message.Key == "progress.linking_tracks");

        _temp.WriteText(Path.Combine("src", "Disc (1995)(Sony) (Track 1).bin"), "TRACK");
        _temp.WriteText(Path.Combine("src", "Disc (1995)(Sony).cue"),
            "FILE \"Disc (1995)(Sony) (Track 1).bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n");
        reports.Clear();

        scanner.Scan(progress: new SyncProgress<SortProgress>(reports.Add));
        StepKeys(reports).Should().ContainInOrder("progress.listing_files", "progress.linking_tracks");
    }

    [Fact]
    public void ChdFolder_ShouldBeNamedWhileItIsListed()
    {
        var config = CreateConfig();
        _temp.WriteText(Path.Combine("chd", "game", "disk.chd"), "not a real CHD");
        var reports = new List<SortProgress>();
        var scanner = new SourceScanner(config, _services.ChecksumService, _services.I18n, _services.OpticalCompanionService);

        var entries = scanner.ScanChdFolder(Path.Combine(_temp.Path, "chd"), [], progress: new SyncProgress<SortProgress>(reports.Add));

        entries.Should().ContainSingle();
        reports.Should().NotBeEmpty();
        reports[0].Message.Key.Should().Be("progress.listing_chd");
        _services.I18n.Format(reports[0].Message).Should().Be("Looking for CHDs: 0 found");
    }

    [Fact]
    public async Task MameIdentification_ShouldNameTheStepsThatFollowTheLastSet()
    {
        var source = Path.Combine(_temp.Path, "mame");
        Directory.CreateDirectory(source);
        using (var zip = System.IO.Compression.ZipFile.Open(Path.Combine(source, "sf2.zip"), System.IO.Compression.ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("sf2.a").Open());
            writer.Write("SF2");
        }

        var config = new SortConfig
        {
            SourceDir = source,
            DestinationDir = Path.Combine(_temp.Path, "dst"),
            DatFile = _temp.WriteText("mame.xml", """
                <?xml version="1.0"?>
                <mame build="0.289">
                  <machine name="sf2"><description>Street Fighter II</description><rom name="sf2.a" size="3" crc="12345678"/></machine>
                </mame>
                """)
        };
        var reports = new List<SortProgress>();

        await _services.Processor.ExecuteAsync(config, isDryRun: true, progress: new SyncProgress<SortProgress>(reports.Add));

        // Comparing thousands of sets with their machines and saving the caches take seconds: without a step of their
        // own, the progress bar seemed stuck on the last archive or CHD
        StepKeys(reports).Should().ContainInOrder(
            "progress.identifying_set",
            "progress.checking_sets",
            "progress.saving_cache",
            "progress.selecting_releases");
        reports.Where(r => r.Message.Key is "progress.checking_sets" or "progress.saving_cache")
            .Should().OnlyContain(r => r.ProcessedCount == 0 && r.TotalCount == 0 && r.EtaSeconds == 0);
    }
}
