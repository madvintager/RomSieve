using System.Diagnostics;
using System.Globalization;
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
