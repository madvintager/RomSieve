using System.Text;
using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// A Redump set completed with another pressing of the disc it lacks (smart multi-disk grouping). The disks of
/// <c>Phantasm (Japan)</c> (Sega - Saturn) have no mastering code but disc 6, dumped twice as <c>(Disc 6) (4M)</c> and
/// <c>(Disc 6) (5M)</c>: each dump of disc 6 stayed in a release of its own, and the set was never complete.
/// </summary>
public sealed class PressingBorrowTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new();

    public void Dispose() => _temp.Dispose();

    private static readonly string[] Phantasm =
    [
        "Phantasm (Japan) (Disc 1).chd", "Phantasm (Japan) (Disc 2).chd", "Phantasm (Japan) (Disc 3) (2M).chd",
        "Phantasm (Japan) (Disc 4).chd", "Phantasm (Japan) (Disc 5).chd", "Phantasm (Japan) (Disc 6) (4M).chd",
        "Phantasm (Japan) (Disc 6) (5M).chd", "Phantasm (Japan) (Disc 7).chd", "Phantasm (Japan) (Disc 8).chd"
    ];

    private (SortConfig Config, string Dest) CreateCollection(IEnumerable<string> fileNames, bool smartMultidisk)
    {
        var src = _temp.CreateSubdirectory("src_" + Guid.NewGuid().ToString("N")[..8]);
        var dst = _temp.CreateSubdirectory("dst_" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var name in fileNames)
        {
            File.WriteAllBytes(Path.Combine(src, name), ChdTestFile.Create(name));
        }

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            NamingConvention = NamingConvention.Redump,
            Pattern = string.Empty,
            SubfolderMultidisk = true,
            GenerateM3u = true,
            M3uPlacement = M3uPlacementMode.Subfolder,
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                PriorityLanguages = ["en", "ja"],
                SmartMultidiskGrouping = smartMultidisk,
                CompleteMultidiskSetsOnly = true,
                NoIntroCompleteMultimediaSetsOnly = true
            }
        };
        return (config, dst);
    }

    private static FileStatus StatusOf(SortExecutionResult result, string fileName) =>
        result.Items.Single(it => Path.GetFileName(it.SourcePath) == fileName).Status;

    [Fact]
    public async Task SetLackingADisc_ShouldBorrowOnePressingOfIt_AndListItInItsPlaylist()
    {
        var (config, dst) = CreateCollection(Phantasm, smartMultidisk: true);

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        foreach (var name in Phantasm.Where(n => !n.Contains("(5M)", StringComparison.Ordinal)))
        {
            StatusOf(result, name).Should().Be(FileStatus.Valid, name);
        }
        StatusOf(result, "Phantasm (Japan) (Disc 6) (5M).chd").Should().NotBe(FileStatus.Valid, "one dump of disc 6 is enough");

        var m3u = Path.Combine(dst, "Phantasm", "Phantasm (Japan).m3u");
        File.Exists(m3u).Should().BeTrue();
        (await File.ReadAllLinesAsync(m3u, Encoding.UTF8)).Where(l => !string.IsNullOrWhiteSpace(l))
            .Should().Equal(Phantasm.Where(n => !n.Contains("(5M)", StringComparison.Ordinal)));
        File.Exists(Path.Combine(dst, "Phantasm", "Phantasm (Japan) (Disc 6) (4M).m3u")).Should().BeFalse("the borrowed disc has no playlist of its own");
    }

    [Fact]
    public async Task SetLackingADisc_WithoutSmartGrouping_ShouldStayIncomplete()
    {
        var (config, _) = CreateCollection(Phantasm, smartMultidisk: false);

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);

        StatusOf(result, "Phantasm (Japan) (Disc 1).chd").Should().Be(FileStatus.Excluded);
        StatusOf(result, "Phantasm (Japan) (Disc 6) (4M).chd").Should().Be(FileStatus.Excluded);
    }

    [Fact]
    public async Task BorrowedPressing_ShouldBeTheSameWhateverTheOrderOfTheFiles()
    {
        var (config, _) = CreateCollection(Phantasm.Reverse(), smartMultidisk: true);

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);

        StatusOf(result, "Phantasm (Japan) (Disc 6) (4M).chd").Should().Be(FileStatus.Valid, "the first name among dumps alike");
        StatusOf(result, "Phantasm (Japan) (Disc 6) (5M).chd").Should().NotBe(FileStatus.Valid);
    }

    [Fact]
    public async Task EditionMark_ShouldNeverBeBorrowedByAnotherEdition()
    {
        // The standard edition lacks its disc 3, the box its disc 2: neither completes the other (the mark of the box,
        // on two of its disks, is no label)
        string[] files =
        [
            "Tales of Eternia (Japan) (Disc 1).chd", "Tales of Eternia (Japan) (Disc 2).chd", "Tales of Eternia (Japan) (Disc 4).chd",
            "Tales of Eternia (Japan) (Disc 1) (Premium Box).chd", "Tales of Eternia (Japan) (Disc 3) (Premium Box).chd"
        ];
        var (config, _) = CreateCollection(files, smartMultidisk: true);

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);

        foreach (var name in files)
        {
            StatusOf(result, name).Should().Be(FileStatus.Excluded, name);
        }
    }
}
