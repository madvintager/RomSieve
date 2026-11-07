using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit.Abstractions;

namespace RomSieve.Core.Tests;

/// <summary>
/// A test run by hand only: skipped unless <c>ROMSIEVE_PERF_DATS</c> names a folder of DATs (TOSEC, No-Intro, Redump).
/// It is never part of the CI.
/// </summary>
public sealed class PerformanceFactAttribute : FactAttribute
{
    public const string FolderVariable = "ROMSIEVE_PERF_DATS";

    public PerformanceFactAttribute()
    {
        if (!Directory.Exists(Environment.GetEnvironmentVariable(FolderVariable)))
        {
            Skip = $"{FolderVariable} names no folder of DATs";
        }
    }
}

/// <summary>
/// Time and memory of the read of a lot of DATs: the folder named by <c>ROMSIEVE_PERF_DATS</c> (only read), then a
/// synthetic lot of a few thousand DATs made from its DATs in a temporary folder (<c>ROMSIEVE_PERF_DAT_COUNT</c>,
/// 3000 by default, and <c>ROMSIEVE_PERF_COPIES</c>, 5 by default: each copy slices the games of each DAT into DATs
/// of their own, renamed, with other CRCs, so that the copies do not share their checksums). The figures are written
/// to the output of the test:
/// <c>ROMSIEVE_PERF_DATS=tosec_dat dotnet test tests/RomSieve.Core.Tests --filter "Category=Performance" --logger "console;verbosity=detailed"</c>.
/// </summary>
[Trait("Category", "Performance")]
public sealed class DatLotPerformanceTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDirectoryFixture _tmp = new();
    private readonly CoreTestServices _services = new();

    public void Dispose() => _tmp.Dispose();

    private static string Folder => Environment.GetEnvironmentVariable(PerformanceFactAttribute.FolderVariable)!;

    private static int Setting(string variable, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;

    [PerformanceFact]
    public async Task FolderOfDats_ShouldBeReadAsALot() => await MeasureAsync(Folder, "folder");

    [PerformanceFact]
    public async Task SyntheticLot_OfThousandsOfDats_ShouldBeRead()
    {
        var folder = await WriteSyntheticLotAsync(Setting("ROMSIEVE_PERF_DAT_COUNT", 3000), Setting("ROMSIEVE_PERF_COPIES", 5));
        await MeasureAsync(folder, "synthetic lot");
    }

    /// <summary>
    /// Simulation of a small source against the whole lot: a few ROMs of some of its DATs
    /// (<c>ROMSIEVE_PERF_SORT_DATS</c>, 20 by default, spread over the lot; <c>ROMSIEVE_PERF_SORT_ROMS</c> per DAT, 3 by default).
    /// </summary>
    [PerformanceFact]
    public async Task SortOfASmallSource_AgainstTheLot_ShouldBeMeasured() =>
        await MeasureSortAsync(Setting("ROMSIEVE_PERF_SORT_DATS", 20), Setting("ROMSIEVE_PERF_SORT_ROMS", 3), "small source");

    /// <summary>Simulation of a source with a ROM of every DAT of the lot: every DAT has a game found.</summary>
    [PerformanceFact]
    public async Task SortOfASourceOfEveryDat_AgainstTheLot_ShouldBeMeasured() =>
        await MeasureSortAsync(int.MaxValue, 1, "source of every DAT");

    // Extensions the scanner opens or reads as descriptors: a forged file of theirs would not be a plain file
    private static readonly HashSet<string> SkippedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".cue", ".gdi", ".ccd", ".chd", ".m3u"
    };

    // Largest ROM a source is made of: the measure is about the catalogs, not about the hashing
    private const long LargestSourceRom = 1024 * 1024;

    private async Task MeasureSortAsync(int datCount, int romsPerDat, string label)
    {
        var source = _tmp.CreateSubdirectory("source");
        var (written, dats, bytes) = await WriteSourceAsync(source, datCount, romsPerDat);
        output.WriteLine($"{label}: {written} files of {dats} DATs, {Mb(bytes)}, against {Folder}");

        var config = new SortConfig
        {
            SourceDir = source,
            DestinationDir = _tmp.GetFilePath("destination"),
            DatFile = Folder,
            DatRecursive = true
        };

        // The managed heap and the working set are sampled during the simulation; the result is kept, as the interface
        // keeps it for its table and its exports
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingSetBefore = process.WorkingSet64;
        long peakHeap = 0, peakWorkingSet = 0;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(forceFullCollection: false));
                process.Refresh();
                peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
                try
                {
                    await Task.Delay(20, stop.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        });

        var watch = Stopwatch.StartNew();
        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);
        var elapsed = watch.ElapsedMilliseconds;
        stop.Cancel();
        await sampler;
        var retained = GC.GetTotalMemory(forceFullCollection: true) - before;

        var found = result.Stats.PerDat!.Count(d => d.GamesFound > 0);
        output.WriteLine($"  simulation: {elapsed} ms, {result.Items.Count} items, {result.Items.Count(it => it.Status == FileStatus.Valid)} kept, "
                         + $"{result.Items.Count(it => it.Status == FileStatus.Unmatched)} unmatched, {found} DATs with a game found");
        output.WriteLine($"  retained after the simulation (result kept): {Mb(retained)}");
        output.WriteLine($"  peak during the simulation: managed heap {Mb(peakHeap - before)} above the start, "
                         + $"working set {Mb(peakWorkingSet)} ({Mb(peakWorkingSet - workingSetBefore)} above the start)");
        foreach (var reason in result.Items.Where(it => it.Status == FileStatus.Unmatched).GroupBy(it => it.StatusReason?.Key).OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"  unmatched: {reason.Count()} {reason.Key}, such as {reason.First().TargetFilename ?? reason.First().DisplayName}");
        }

        GC.KeepAlive(result);

        found.Should().BeGreaterThanOrEqualTo(dats, "each file is a ROM of its DAT");
    }

    /// <summary>
    /// Writes into <paramref name="folder"/> the smallest ROMs of <paramref name="datCount"/> DATs of the folder (spread
    /// over it), <paramref name="romsPerDat"/> per DAT, each with the size and the CRC32 of its entry: random data, whose
    /// last four bytes are forged to give the CRC32. The DATs are only read, one at a time.
    /// </summary>
    private async Task<(int Files, int Dats, long Bytes)> WriteSourceAsync(string folder, int datCount, int romsPerDat)
    {
        var files = DatService.GetDatFolderFiles(Folder, recursive: true);
        var step = Math.Max(1, files.Count / Math.Max(1, Math.Min(datCount, files.Count)));
        var random = new Random(1234);
        int written = 0, dats = 0;
        long bytes = 0;
        for (var index = 0; index < files.Count && dats < datCount; index += step)
        {
            var catalog = await _services.DatService.ParseDatFileAsync(files[index]);
            var roms = catalog.Entries
                .Where(r => !r.IsDisk && r.Size is >= 4 and <= LargestSourceRom && r.Crc.Length == 8 && !SkippedExtensions.Contains(Path.GetExtension(r.Name)))
                .GroupBy(r => (r.Size, r.Crc.ToLowerInvariant()))
                .Select(g => g.First())
                .OrderBy(r => r.Size)
                .Take(romsPerDat)
                .ToList();
            if (roms.Count == 0)
            {
                continue;
            }

            dats++;
            foreach (var rom in roms)
            {
                var data = new byte[rom.Size];
                random.NextBytes(data.AsSpan(0, data.Length - 4));
                ForgeCrc(data, uint.Parse(rom.Crc, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                await File.WriteAllBytesAsync(Path.Combine(folder, $"{dats:D4}_{written:D5}{Path.GetExtension(rom.Name)}"), data);
                written++;
                bytes += data.Length;
            }
        }

        return (written, dats, bytes);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }

        return c;
    }).ToArray();

    // Index of the table entry of each top byte: the top bytes of the CRC32 table are all different
    private static readonly byte[] CrcTableIndexOfTopByte = Enumerable.Range(0, 256)
        .Aggregate(new byte[256], (index, n) =>
        {
            index[CrcTable[n] >> 24] = (byte)n;
            return index;
        });

    /// <summary>Sets the last four bytes of <paramref name="data"/> so that its CRC32 is <paramref name="crc"/>.</summary>
    private static void ForgeCrc(byte[] data, uint crc)
    {
        // The register after the data before the last four bytes
        var register = ~Crc32.HashToUInt32(data.AsSpan(0, data.Length - 4));

        // The table entries the last four steps must take to end at the register of the wanted CRC32
        var wanted = ~crc;
        var indices = new byte[4];
        for (var k = 3; k >= 0; k--)
        {
            indices[k] = CrcTableIndexOfTopByte[wanted >> 24];
            wanted = (wanted ^ CrcTable[indices[k]]) << 8;
        }

        for (var k = 0; k < 4; k++)
        {
            var value = (byte)((register ^ indices[k]) & 0xFF);
            data[data.Length - 4 + k] = value;
            register = (register >> 8) ^ CrcTable[(register ^ value) & 0xFF];
        }

        Crc32.HashToUInt32(data).Should().Be(crc, "the forged bytes give the CRC32 of the entry");
    }

    private async Task MeasureAsync(string folder, string label)
    {
        var files = DatService.GetDatFolderFiles(folder, recursive: true);
        var bytes = files.Sum(f => new FileInfo(f).Length);
        output.WriteLine($"{label}: {files.Count} DATs, {Mb(bytes)} on disk");

        // The headers alone: the list of the DATs to check
        var watch = Stopwatch.StartNew();
        var preview = _services.DatService.ReadDatFolderPreview(folder, recursive: true);
        output.WriteLine($"  preview: {watch.ElapsedMilliseconds} ms for {preview.Count} headers");

        // The lot, then the same DATs read one by one: the difference is the index of the lot
        var (lotMemory, lotTime, lot) = await RetainedAsync(() => _services.DatService.ParseDatFileAsync(folder, new DatFolderOptions(Recursive: true)));
        var entries = lot.Dats!.Sum(d => d.Catalog.Entries.Count);
        var games = lot.Dats!.Sum(d => d.Catalog.Games.Count);
        output.WriteLine($"  lot: {lotTime} ms, {Mb(lotMemory)} retained, {lot.Dats!.Count} DATs, {games} games, {entries} entries, {lot.KnownSizes.Count} sizes");
        GC.KeepAlive(lot);
        lot = null!;

        var (datsMemory, datsTime, dats) = await RetainedAsync(async () =>
        {
            var read = new List<DatCatalog>(files.Count);
            foreach (var file in files)
            {
                read.Add(await _services.DatService.ParseDatFileAsync(file));
            }

            return read;
        });
        output.WriteLine($"  DATs read one by one: {datsTime} ms, {Mb(datsMemory)} retained; index of the lot: {Mb(lotMemory - datsMemory)}");
        GC.KeepAlive(dats);
        output.WriteLine($"  peak working set of the process: {Mb(Process.GetCurrentProcess().PeakWorkingSet64)}");

        entries.Should().BePositive();
    }

    private static async Task<(long Memory, long Milliseconds, T Value)> RetainedAsync<T>(Func<Task<T>> read)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var watch = Stopwatch.StartNew();
        var value = await read();
        var elapsed = watch.ElapsedMilliseconds;
        var after = GC.GetTotalMemory(forceFullCollection: true);
        return (after - before, elapsed, value);
    }

    private static string Mb(long bytes) => $"{bytes / 1024.0 / 1024.0:F1} MB";

    // The DATs of the folder, each sliced into DATs of their own (in proportion to its games), copied several times
    private async Task<string> WriteSyntheticLotAsync(int count, int copies)
    {
        var target = _tmp.CreateSubdirectory("synthetic");
        var sources = new List<DatCatalog>();
        foreach (var file in DatService.GetDatFolderFiles(Folder, recursive: true))
        {
            sources.Add(await _services.DatService.ParseDatFileAsync(file));
        }

        var totalGames = Math.Max(1, sources.Sum(s => s.Games.Count));
        var slicesPerCopy = Math.Max(1, count / copies);
        var written = 0;
        for (var copy = 0; copy < copies; copy++)
        {
            var crcMask = unchecked((uint)copy * 0x9E3779B9u);
            for (var index = 0; index < sources.Count; index++)
            {
                var source = sources[index];
                var slices = Math.Max(1, (int)Math.Round(slicesPerCopy * (double)source.Games.Count / totalGames));
                var perSlice = Math.Max(1, (int)Math.Ceiling(source.Games.Count / (double)slices));
                for (var slice = 0; slice * perSlice < source.Games.Count; slice++)
                {
                    var dat = new DatCatalog
                    {
                        Header = new DatHeader
                        {
                            Name = $"{source.Header.Name} - copy {copy} - part {slice}",
                            Description = source.Header.Description,
                            Version = source.Header.Version,
                            Author = source.Header.Author,
                            Homepage = source.Header.Homepage,
                            Url = source.Header.Url
                        }
                    };
                    foreach (var game in source.Games.Skip(slice * perSlice).Take(perSlice))
                    {
                        dat.AddGame(DatCatalog.CopyGame(game, game.Roms.Select(rom => new DatRomEntry(
                            rom.Name, rom.Size, OtherCrc(rom.Crc, crcMask), rom.Md5, rom.Sha1, rom.GameName, rom.Description))));
                    }

                    await dat.WriteLogiqxXmlAsync(Path.Combine(target, $"copy {copy}", $"{index:D3} - part {slice:D4}.dat"));
                    written++;
                }
            }
        }

        output.WriteLine($"synthetic lot: {written} DATs from {sources.Count} ({copies} copies)");
        return target;
    }

    private static string OtherCrc(string crc, uint mask) =>
        mask == 0 || !uint.TryParse(crc, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? crc
            : (value ^ mask).ToString("x8", CultureInfo.InvariantCulture);
}
