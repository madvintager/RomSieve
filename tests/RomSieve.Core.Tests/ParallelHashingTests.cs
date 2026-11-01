using System.IO;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Parallel hashing during the classification: the result (items, their order, reasons, DAT statistics,
/// missing ROMs and logs) never depends on the number of files hashed at once, and cancellation stays immediate.
/// </summary>
public sealed class ParallelHashingTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");

    public void Dispose() => _temp.Dispose();

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private static string Crc(byte[] data) => Crc32.HashToUInt32(data).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);

    private static byte[] Content(int seed, int size)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// Builds a varied source with its DAT: files of various sizes in or out of the DAT, a ROM shared by two games,
    /// a file with a copier header, a ZIP archive and an optical descriptor with its track. Returns the DAT path.
    /// </summary>
    private string BuildSource()
    {
        var games = new StringBuilder();
        void AddGame(string name, params (string Rom, byte[] Data)[] roms)
        {
            games.AppendLine($"""  <game name="{name}"><description>{name}</description>""");
            foreach (var (rom, data) in roms)
            {
                games.AppendLine($"""    <rom name="{rom}" size="{data.Length}" crc="{Crc(data)}"/>""");
            }
            games.AppendLine("  </game>");
        }

        for (var i = 0; i < 40; i++)
        {
            var name = $"Game {i:00} (19{80 + (i % 10)})(Publisher {i % 3})";
            var data = Content(i, 1024 + (i * 7919 % 200_000));
            _temp.WriteBytes(Path.Combine("src", i % 4 == 0 ? "sub" : string.Empty, $"{name}.adf"), data);
            if (i % 5 != 4)
            {
                AddGame(name, ($"{name}.adf", data));
            }
            else
            {
                // Same size as a DAT entry, different content: hashed, then unmatched
                AddGame($"{name}[a]", ($"{name}[a].adf", Content(i + 1000, data.Length)));
            }
        }

        var shared = Content(500, 4096);
        _temp.WriteBytes(Path.Combine("src", "Shared (1990)(Ocean).adf"), shared);
        AddGame("Shared (1990)(Ocean)", ("Shared (1990)(Ocean).adf", shared));
        AddGame("Shared (1990)(Ocean)[a]", ("Shared (1990)(Ocean)[a].adf", shared));

        var rom = Content(600, 32 * 1024);
        byte[] header = [(byte)'N', (byte)'E', (byte)'S', 0x1A, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        _temp.WriteBytes(Path.Combine("src", "Headered (1990)(Nintendo).nes"), [.. header, .. rom]);
        AddGame("Headered (1990)(Nintendo)", ("Headered (1990)(Nintendo).nes", rom));

        var zipped = Content(700, 20_000);
        using (var zip = ZipFile.Open(_temp.GetFilePath(Path.Combine("src", "pack.zip")), ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry("Zipped (1991)(Ocean).adf").Open();
            stream.Write(zipped);
        }
        AddGame("Zipped (1991)(Ocean)", ("Zipped (1991)(Ocean).adf", zipped));

        var cue = Encoding.ASCII.GetBytes("FILE \"Disc (1995)(Sony) (Track 1).bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n");
        var track = Content(800, 2352 * 20);
        _temp.WriteBytes(Path.Combine("src", "Disc (1995)(Sony).cue"), cue);
        _temp.WriteBytes(Path.Combine("src", "Disc (1995)(Sony) (Track 1).bin"), track);
        AddGame("Disc (1995)(Sony)", ("Disc (1995)(Sony).cue", cue), ("Disc (1995)(Sony) (Track 1).bin", track));

        return _temp.WriteText("test.dat", $"""
            <?xml version="1.0"?>
            <datafile>
              <header><name>Commodore Amiga - Games (TOSEC)</name><description>TOSEC test DAT</description></header>
            {games}
            </datafile>
            """);
    }

    private SortProcessorService CreateProcessor(string cacheName) => new(
        _services.ChecksumService,
        _services.DatService,
        _services.NoIntroParser,
        _services.FilterService,
        _services.Organizer,
        _services.I18n,
        _services.OpticalCompanionService,
        _services.MameParser,
        new CrcCacheService(_temp.GetFilePath(Path.Combine("cache", cacheName))));

    /// <summary>Runs a simulation and describes everything it produced, paths relative to the test directory.</summary>
    private async Task<string> SimulateAsync(string datPath, int parallelism, string cacheName)
    {
        var logs = new List<LogMessage>();
        var result = await CreateProcessor(cacheName).ExecuteAsync(
            new SortConfig
            {
                SourceDir = _temp.GetFilePath("src"),
                DestinationDir = _temp.GetFilePath("dst"),
                DatFile = datPath,
                HashingParallelism = parallelism
            },
            isDryRun: true,
            logProgress: new SyncProgress<LogMessage>(logs.Add));

        string Describe(ProcessItem it) => string.Join('|',
            Path.GetRelativePath(_temp.Path, it.SourcePath), it.ArchiveMember, it.Status, it.Crc32, it.TargetFilename,
            it.DatGameName, JsonSerializer.Serialize(it.StatusReason), JsonSerializer.Serialize(it.EliminationDetail),
            it.FinalTargetPath is null ? string.Empty : Path.GetRelativePath(_temp.Path, it.FinalTargetPath));

        var sb = new StringBuilder();
        foreach (var item in result.Items)
        {
            sb.AppendLine(Describe(item));
            foreach (var related in item.RelatedItems)
            {
                sb.AppendLine("  + " + Describe(related));
            }
        }
        sb.AppendLine(JsonSerializer.Serialize(result.Stats));
        sb.AppendLine(string.Join(',', result.MissingRoms.Select(r => r.Name)));
        foreach (var log in logs)
        {
            sb.AppendLine($"{log.Level} {JsonSerializer.Serialize(log.Message)}"
                .Replace(_temp.Path, "<tmp>", StringComparison.Ordinal)
                .Replace(JsonSerializer.Serialize(_temp.Path)[1..^1], "<tmp>", StringComparison.Ordinal));
        }
        return sb.ToString();
    }

    [Fact]
    public async Task Simulation_ShouldNotDependOnTheHashingParallelism()
    {
        var dat = BuildSource();

        var sequential = await SimulateAsync(dat, 1, "seq.json");

        sequential.Should().Contain("|Valid|").And.Contain("|Unmatched|");
        foreach (var parallelism in new[] { 2, 4, 8, SortConfig.AutoHashingParallelism })
        {
            var parallel = await SimulateAsync(dat, parallelism, $"par{parallelism}.json");
            parallel.Should().Be(sequential, $"the result must not depend on a parallelism of {parallelism}");
        }
    }

    [Fact]
    public async Task ParallelHashing_WithAWarmCache_ShouldGiveTheSameResult()
    {
        var dat = BuildSource();

        var cold = await SimulateAsync(dat, 4, "shared.json");
        File.Exists(_temp.GetFilePath(Path.Combine("cache", "shared.json"))).Should().BeTrue();
        var warm = await SimulateAsync(dat, 4, "shared.json");

        warm.Should().Be(cold);
    }

    private async Task<(EntryClassifier Classifier, IReadOnlyList<ScanEntry> Entries)> CreateClassifierAsync(int parallelism)
    {
        var dat = BuildSource();
        var catalog = await _services.DatService.ParseDatFileAsync(dat);
        var entries = Directory.GetFiles(_temp.GetFilePath("src"), "*.adf", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => new ScanEntry(path, new FileInfo(path).Length))
            .ToList();
        var config = new SortConfig { SourceDir = _temp.GetFilePath("src"), DestinationDir = _temp.GetFilePath("dst"), HashingParallelism = parallelism };
        var classifier = new EntryClassifier(
            config, catalog, new CrcCacheService(_temp.GetFilePath(Path.Combine("cache", "classifier.json"))),
            _services.ChecksumService, _services.NoIntroParser, _services.FilterService, _services.I18n, _services.OpticalCompanionService);
        return (classifier, entries);
    }

    [Fact]
    public async Task ParallelHashing_ShouldReportAProgressThatNeverGoesBackwards()
    {
        var (classifier, entries) = await CreateClassifierAsync(4);
        var reported = new List<int>();

        var items = await classifier.ClassifyAllAsync(entries, progress: new SyncProgress<SortProgress>(p =>
        {
            // The steps that follow (saving the cache) have no count
            if (p.Message.Key == "progress.analyzing_file")
            {
                reported.Add(p.ProcessedCount);
            }
        }));

        items.Should().HaveCount(entries.Count);
        items.Select(i => i.SourcePath).Should().Equal(entries.Select(e => e.SourcePath), "the items keep the scan order");
        reported.Should().BeInAscendingOrder();
        reported.Last().Should().Be(entries.Count);
    }

    [Fact]
    public async Task ParallelHashing_Cancelled_ShouldStopWithoutHashingEveryFile()
    {
        var (classifier, entries) = await CreateClassifierAsync(4);
        using var cts = new CancellationTokenSource();
        var reports = 0;

        var act = () => classifier.ClassifyAllAsync(entries, cts.Token, new SyncProgress<SortProgress>(_ =>
        {
            reports++;
            cts.Cancel();
        }));

        await act.Should().ThrowAsync<OperationCanceledException>();
        reports.Should().BeLessThan(entries.Count, "no new file is hashed once the cancellation is requested");
    }

    [Fact]
    public void TestServices_ShouldNeverUseTheCrcCacheOfTheUser()
    {
        _services.CrcCache.CachePath.Should().NotBe(CrcCacheService.GetDefaultCrcCachePath());
        _services.CrcCache.CachePath.Should().StartWith(Path.GetTempPath());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(8, 8)]
    [InlineData(-3, 1)]
    [InlineData(64, EntryClassifier.MaxHashingParallelism)]
    public void ResolveHashingParallelism_ShouldClampTheConfiguredValue(int configured, int expected)
    {
        EntryClassifier.ResolveHashingParallelism(configured).Should().Be(expected);
    }

    [Fact]
    public void ResolveHashingParallelism_Auto_ShouldUseBetweenOneAndFourFiles()
    {
        EntryClassifier.ResolveHashingParallelism(SortConfig.AutoHashingParallelism).Should().BeInRange(1, 4);
        new SortConfig { SourceDir = "a", DestinationDir = "b" }.HashingParallelism.Should().Be(1, "hashing is sequential by default");
    }
}
