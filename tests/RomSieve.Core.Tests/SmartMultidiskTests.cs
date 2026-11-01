using System.IO;
using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// Smart multi-disk grouping: borrowing of clean data disks computed by <see cref="MultiDiskEvaluator"/>
/// and consumed by the 1G1R resolver and the <c>.m3u</c> playlist generation.
/// </summary>
public sealed class SmartMultidiskTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("fr");

    private static readonly string[] Files =
    [
        "Putty (1992)(System 3)(Disk 1 of 3)[cr CSL].adf", "Putty (1992)(System 3)(Disk 2 of 3).adf", "Putty (1992)(System 3)(Disk 3 of 3).adf",
        "Lotus (1991)(Gremlin)(Disk 1 of 3)[cr X].adf", "Lotus (1991)(Gremlin)(Disk 3 of 3).adf",
        "Space (1992)(Gremlin)(Disk 1 of 2)[cr A].adf", "Space (1992)(Gremlin)(Disk 1 of 2)[cr B].adf", "Space (1992)(Gremlin)(Disk 2 of 2).adf",
        "Zak (1988)(Lucas)(Disk 1 of 2).adf", "Zak (1988)(Lucas)(Disk 2 of 2).adf", "Zak (1988)(Lucas)(Disk 1 of 2)[cr X].adf",
        "Monkey (1990)(Lucas)(FR)(Disk 1 of 2)[cr].adf", "Monkey (1990)(Lucas)(DE)(Disk 2 of 2).adf",
    ];

    public void Dispose() => _temp.Dispose();

    private SortConfig CreateConfig(bool oneGameOneRom, bool completeSetsOnly)
    {
        var src = _temp.CreateSubdirectory("src_" + Guid.NewGuid().ToString("N")[..8]);
        var dst = _temp.CreateSubdirectory("dst_" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var name in Files)
        {
            File.WriteAllText(Path.Combine(src, name), name);
        }

        return new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{title}/",
            TransferMode = TransferMode.Copy,
            GenerateM3u = true,
            Filters = new FilterConfig
            {
                OneGameOneRom = oneGameOneRom,
                PriorityLanguages = oneGameOneRom ? ["en"] : [],
                ExcludeModifications = false,
                CompleteMultidiskSetsOnly = completeSetsOnly,
                SmartMultidiskGrouping = true
            }
        };
    }

    [Fact]
    public async Task Evaluate_ShouldListTheBorrowedCleanDisksOnlyForTheReleasesTheyComplete()
    {
        var config = CreateConfig(oneGameOneRom: false, completeSetsOnly: false);
        var classified = await _services.Processor.ExecuteAsync(config, isDryRun: true);
        var items = classified.Items.ToList();
        foreach (var it in items)
        {
            it.Status = FileStatus.Valid;
        }

        var result = new MultiDiskEvaluator(config, _services.FilterService, _services.I18n)
            .Evaluate(items, datCatalog: null, new Dictionary<string, HashSet<string>>());

        var borrowedSets = result.BorrowedCleanDisks.Values
            .Select(disks => string.Join(" + ", disks.Select(d => d.DisplayName).Order(StringComparer.Ordinal)))
            .Order(StringComparer.Ordinal);
        borrowedSets.Should().Equal(
            "Putty (1992)(System 3)(Disk 2 of 3).adf + Putty (1992)(System 3)(Disk 3 of 3).adf",
            "Space (1992)(Gremlin)(Disk 2 of 2).adf",
            "Space (1992)(Gremlin)(Disk 2 of 2).adf",
            "Zak (1988)(Lucas)(Disk 2 of 2).adf");

        // Lotus lacks disk 2 and Monkey's disks belong to different editions (FR / DE)
        items.Where(it => it.Parsed.Title is "Lotus" or "Monkey").Should().OnlyContain(it => !it.IsMultidiskComplete);
    }

    public static TheoryData<bool, bool, string[]> Playlists() => new()
    {
        {
            false, false,
            [
                "Lotus (1991)(Gremlin)[cr X].m3u: Disk 1 of 3 + Disk 3 of 3",
                "Putty (1992)(System 3)[cr CSL].m3u: Disk 1 of 3 + Disk 2 of 3 + Disk 3 of 3",
                "Space (1992)(Gremlin)[cr A].m3u: Disk 1 of 2 + Disk 2 of 2",
                "Space (1992)(Gremlin)[cr B].m3u: Disk 1 of 2 + Disk 2 of 2",
                "Zak (1988)(Lucas).m3u: Disk 1 of 2 + Disk 2 of 2",
                "Zak (1988)(Lucas)[cr X].m3u: Disk 1 of 2 + Disk 2 of 2"
            ]
        },
        {
            // A partial borrowing (Lotus) needs the complete sets option to be off
            false, true,
            [
                "Putty (1992)(System 3)[cr CSL].m3u: Disk 1 of 3 + Disk 2 of 3 + Disk 3 of 3",
                "Space (1992)(Gremlin)[cr A].m3u: Disk 1 of 2 + Disk 2 of 2",
                "Space (1992)(Gremlin)[cr B].m3u: Disk 1 of 2 + Disk 2 of 2",
                "Zak (1988)(Lucas).m3u: Disk 1 of 2 + Disk 2 of 2",
                "Zak (1988)(Lucas)[cr X].m3u: Disk 1 of 2 + Disk 2 of 2"
            ]
        },
        {
            // 1G1R keeps one release per game, completed by the borrowed clean disks
            true, true,
            [
                "Putty (1992)(System 3)[cr CSL].m3u: Disk 1 of 3 + Disk 2 of 3 + Disk 3 of 3",
                "Space (1992)(Gremlin)[cr A].m3u: Disk 1 of 2 + Disk 2 of 2",
                "Zak (1988)(Lucas).m3u: Disk 1 of 2 + Disk 2 of 2"
            ]
        }
    };

    [Theory]
    [MemberData(nameof(Playlists))]
    public async Task RealSort_ShouldWriteOnePlaylistPerReleaseWithItsBorrowedDisks(bool oneGameOneRom, bool completeSetsOnly, string[] expected)
    {
        var config = CreateConfig(oneGameOneRom, completeSetsOnly);

        await _services.Processor.ExecuteAsync(config, isDryRun: false);

        var playlists = Directory.EnumerateFiles(config.DestinationDir, "*.m3u", SearchOption.AllDirectories)
            .Select(path => Path.GetFileName(path) + ": " + string.Join(" + ", File.ReadAllLines(path)
                .Where(line => line.Length > 0)
                .Select(line => line[(line.IndexOf("(Disk ", StringComparison.Ordinal) + 1)..line.IndexOf(')', line.IndexOf("(Disk ", StringComparison.Ordinal))])))
            .Order(StringComparer.Ordinal);
        playlists.Should().Equal(expected);
    }

    [Fact]
    public void Evaluate_ShouldInferTheDiscTotalOnACopyWithoutModifyingTheParseResult()
    {
        var config = CreateConfig(oneGameOneRom: false, completeSetsOnly: false);
        ProcessItem Item(string name) => new()
        {
            SourcePath = name,
            FileSize = 1,
            Parsed = _services.NoIntroParser.ParseRomFilename(name, convention: NamingConvention.Redump)
        };
        var disc1 = Item("Final Fantasy VII (France) (Disc 1).bin");
        var disc2 = Item("Final Fantasy VII (France) (Disc 2).bin");
        var originalDisc1 = disc1.Parsed;
        originalDisc1.DiscTotal.Should().BeNull();

        new MultiDiskEvaluator(config, _services.FilterService, _services.I18n)
            .Evaluate([disc1, disc2], datCatalog: null, new Dictionary<string, HashSet<string>>());

        disc1.Parsed.DiscTotal.Should().Be(2);
        disc2.Parsed.DiscTotal.Should().Be(2);
        disc1.Parsed.Should().NotBeSameAs(originalDisc1);
        originalDisc1.DiscTotal.Should().BeNull("the parse result is immutable");
        disc1.Parsed.Should().Be(originalDisc1 with { DiscTotal = 2 });
    }

    [Theory]
    [InlineData(10.0, 0, 10, 0L, 0L, 0.0)]          // Nothing processed yet
    [InlineData(0.0, 5, 10, 0L, 0L, 0.0)]           // No elapsed time
    [InlineData(10.0, 10, 10, 100L, 100L, 0.0)]     // Nothing remains
    [InlineData(10.0, 5, 10, 0L, 0L, 10.0)]         // Item rate: 0.5 item/s, 5 items left
    [InlineData(10.0, 5, 10, 100L, 400L, 30.0)]     // Byte rate: 10 B/s, 300 B left
    [InlineData(10.0, 5, 10, 400L, 400L, 10.0)]     // Bytes done but items left: item rate
    public void EtaEstimator_ShouldUseTheByteRateWhenKnownAndTheItemRateOtherwise(
        double elapsed, int processed, int total, long processedBytes, long totalBytes, double expected)
    {
        EtaEstimator.EstimateSeconds(elapsed, processed, total, processedBytes, totalBytes).Should().BeApproximately(expected, 1e-9);
    }

    [Theory]
    [InlineData("Putty (1992)(System 3)(Disk 1 of 3)", true)]
    [InlineData("Final Fantasy VII (Europe) (Disc 2)", true)]
    [InlineData("Nodes of Yesod (1985)(Odin)(Side B)", true)]
    [InlineData("Game (1990)(Pub)(Disk 1 of 1)", false)]
    [InlineData("Demo Disc, The - Commodore Amiga CD32 Demonstration Disc 2.0 (Europe)", false)]
    [InlineData("Official U.S. PlayStation Magazine Demo Disc 18 (USA)", false)]
    [InlineData("Bubble Bobble Part 2 (USA)", false)]
    [InlineData("Flight Simulator II - Scenery Disk 7 (1986)(Sublogic)", false)]
    public void DatGame_IsMultidisk_ShouldOnlyReadAParenthesizedMediaFlag(string name, bool expected)
    {
        var game = new DatGame { Name = name, Description = name, Roms = [new DatRomEntry { Name = name + ".cue", Size = 1, Crc = "00000001" }, new DatRomEntry { Name = name + " (Track 1).bin", Size = 2, Crc = "00000002" }] };

        game.IsMultidisk.Should().Be(expected);
    }
}
