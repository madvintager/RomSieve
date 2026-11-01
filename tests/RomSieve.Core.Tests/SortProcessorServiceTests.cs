using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// xUnit test suite for <see cref="SortProcessorService"/> and its pipeline components
/// (<see cref="SourceScanner"/>, <see cref="EntryClassifier"/>, <see cref="MultiDiskEvaluator"/>,
/// <see cref="OneGameOneRomResolver"/>, <see cref="LanguageFilterResolver"/>, <see cref="CountryFilterResolver"/>,
/// <see cref="FileTransferExecutor"/>).
/// </summary>
public sealed class SortProcessorServiceTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("fr");

    private LocalizationService I18n => _services.I18n;
    private ChecksumService Checksum => _services.ChecksumService;
    private NoIntroRedumpParser RomParser => _services.NoIntroParser;
    private RomFilterService Filter => _services.FilterService;
    private PathOrganizerService Organizer => _services.Organizer;
    private SortProcessorService Processor => _services.Processor;

    public void Dispose() => _temp.Dispose();

    private (string SourceDir, string DestDir) CreateSampleEnv()
    {
        var src = _temp.CreateSubdirectory("source_" + Guid.NewGuid().ToString("N")[..8]);
        var dst = _temp.CreateSubdirectory("dest_" + Guid.NewGuid().ToString("N")[..8]);

        var files = new Dictionary<string, string>
        {
            ["Another World (1991)(Delphine)(FR)[!].adf"] = "valid_fr",
            ["Another World (1991)(Delphine)(DE).adf"] = "valid_de",
            ["Turrican II (1991)(Rainbow Arts)[b].adf"] = "bad_dump",
            ["readme_collection.txt"] = "unmatched_text"
        };

        foreach (var (fname, content) in files)
        {
            File.WriteAllText(Path.Combine(src, fname), content, Encoding.UTF8);
        }

        return (src, dst);
    }

    [Fact]
    public async Task DryRunSimulation_ShouldNotModifyDestinationAndShouldCountStatusesCorrectly()
    {
        var (src, dst) = CreateSampleEnv();
        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{publisher}/{title}/",
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig { ExcludeBadDumps = true }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: true);

        result.Stats.TotalScanned.Should().Be(4);
        result.Stats.ValidCount.Should().Be(2);
        result.Stats.ExcludedCount.Should().Be(1);
        result.Stats.UnmatchedCount.Should().Be(1);
        result.Stats.ErrorCount.Should().Be(0);
        result.Items.Should().HaveCount(4);

        // In simulation mode, no file should be created in destination
        Directory.EnumerateFileSystemEntries(dst).Should().BeEmpty();
    }

    [Fact]
    public async Task OutputBytes_ShouldBeWhatTheSortWritesToTheDestination()
    {
        var (src, dst) = CreateSampleEnv();
        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{publisher}/{title}/",
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig { ExcludeBadDumps = true }
        };
        long SizeOf(string name) => new FileInfo(Path.Combine(src, name)).Length;
        var expected = SizeOf("Another World (1991)(Delphine)(FR)[!].adf")
            + SizeOf("Another World (1991)(Delphine)(DE).adf")
            + SizeOf("readme_collection.txt");

        var simulation = await Processor.ExecuteAsync(config, isDryRun: true);

        // Kept and non-conforming files (isolated in _unmatched), not the excluded bad dump
        simulation.Stats.OutputBytes.Should().Be(expected);
        Processor.RecomputeStats(simulation.Items, simulation.Stats).OutputBytes.Should().Be(expected);

        var sort = await Processor.ExecuteAsync(config, isDryRun: false);

        sort.Stats.OutputBytes.Should().Be(expected);
        Directory.EnumerateFiles(dst, "*", SearchOption.AllDirectories)
            .Where(f => !DestinationGuard.IsGuardPath(f))
            .Sum(f => new FileInfo(f).Length)
            .Should().Be(expected);
    }

    [Fact]
    public async Task RealSortCopy_ShouldCopyValidAndUnmatchedFilesAndExcludeBadDumps()
    {
        var (src, dst) = CreateSampleEnv();
        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{publisher}/{title}/",
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig { ExcludeBadDumps = true }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);

        result.Stats.ValidCount.Should().Be(2);
        result.Stats.ExcludedCount.Should().Be(1);
        result.Stats.UnmatchedCount.Should().Be(1);
        result.Stats.ErrorCount.Should().Be(0);

        var expectedFr = Path.Combine(dst, "Delphine", "Another World", "Another World (1991)(Delphine)(FR)[!].adf");
        var expectedDe = Path.Combine(dst, "Delphine", "Another World", "Another World (1991)(Delphine)(DE).adf");
        var expectedUnmatched = Path.Combine(dst, "_unmatched", "readme_collection.txt");

        File.Exists(expectedFr).Should().BeTrue();
        File.Exists(expectedDe).Should().BeTrue();
        File.Exists(expectedUnmatched).Should().BeTrue();

        var badPath = Path.Combine(dst, "Rainbow Arts", "Turrican II", "Turrican II (1991)(Rainbow Arts)[b].adf");
        File.Exists(badPath).Should().BeFalse();
    }

    [Fact]
    public async Task RealSortDirectRoot_WithEmptyPattern_ShouldPlaceFilesDirectlyInDestinationRoot()
    {
        var (src, dst) = CreateSampleEnv();
        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = string.Empty,
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig { ExcludeBadDumps = true }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);

        result.Stats.ValidCount.Should().Be(2);
        result.Stats.ErrorCount.Should().Be(0);

        File.Exists(Path.Combine(dst, "Another World (1991)(Delphine)(FR)[!].adf")).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Another World (1991)(Delphine)(DE).adf")).Should().BeTrue();
        Directory.Exists(Path.Combine(dst, "Delphine")).Should().BeFalse();
    }

    [Fact]
    public async Task ExportReports_ShouldGenerateValidJsonAndCsvFiles()
    {
        var (src, dst) = CreateSampleEnv();
        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Filters = new FilterConfig { ExcludeBadDumps = true }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: true);

        var jsonPath = _temp.GetFilePath("report.json");
        var csvPath = _temp.GetFilePath("report.csv");

        await Processor.ExportReportToJsonAsync(result.Items, result.Stats, jsonPath);
        await Processor.ExportReportToCsvAsync(result.Items, csvPath);

        File.Exists(jsonPath).Should().BeTrue();
        File.Exists(csvPath).Should().BeTrue();

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath, Encoding.UTF8));
        doc.RootElement.GetProperty("summary").GetProperty("total_scanned").GetInt32().Should().Be(4);
        doc.RootElement.GetProperty("files").GetArrayLength().Should().Be(4);

        var csvContent = await File.ReadAllTextAsync(csvPath, Encoding.UTF8);
        csvContent.Should().Contain("Another World");
        csvContent.Should().Contain("Turrican II");
    }

    [Fact]
    public async Task LanguagePriority1G1R_ShouldRetainBestLanguagePerGameAndFallbackIfEnabled()
    {
        var src = _temp.CreateSubdirectory("src_1g1r");
        var dst = _temp.CreateSubdirectory("dst_1g1r");

        // Game 1: Another World available in FR, EN, DE -> Only FR must be kept
        File.WriteAllText(Path.Combine(src, "Another World (1991)(Delphine)(FR)[!].adf"), "fr");
        File.WriteAllText(Path.Combine(src, "Another World (1991)(Delphine)(GB).adf"), "en");
        File.WriteAllText(Path.Combine(src, "Another World (1991)(Delphine)(DE).adf"), "de");

        // Game 2: Monkey Island available in EN (US) and DE -> No FR, so EN must be kept
        File.WriteAllText(Path.Combine(src, "Secret of Monkey Island, The (1990)(Lucasfilm Games)(US)(Disk 1 of 4).adf"), "en1");
        File.WriteAllText(Path.Combine(src, "Secret of Monkey Island, The (1990)(Lucasfilm Games)(US)(Disk 2 of 4).adf"), "en2");
        File.WriteAllText(Path.Combine(src, "Secret of Monkey Island, The (1990)(Lucasfilm Games)(US)(Disk 3 of 4).adf"), "en3");
        File.WriteAllText(Path.Combine(src, "Secret of Monkey Island, The (1990)(Lucasfilm Games)(US)(Disk 4 of 4).adf"), "en4");
        File.WriteAllText(Path.Combine(src, "Secret of Monkey Island, The (1990)(Lucasfilm Games)(DE)(Disk 1 of 4).adf"), "de1");
        File.WriteAllText(Path.Combine(src, "Secret of Monkey Island, The (1990)(Lucasfilm Games)(DE)(Disk 2 of 4).adf"), "de2");
        File.WriteAllText(Path.Combine(src, "Secret of Monkey Island, The (1990)(Lucasfilm Games)(DE)(Disk 3 of 4).adf"), "de3");
        File.WriteAllText(Path.Combine(src, "Secret of Monkey Island, The (1990)(Lucasfilm Games)(DE)(Disk 4 of 4).adf"), "de4");

        // Game 3: Only in Italian (IT) -> Kept thanks to fallback
        File.WriteAllText(Path.Combine(src, "Dylan Dog (1992)(Simulmondo)(IT).adf"), "it");

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{title}/",
            RestoreArticles = false,
            SubfolderMultidisk = false,
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                PriorityLanguages = ["fr", "en", "m"],
                FallbackUnmatchedLanguages = true
            }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);

        // 1 (Another World FR) + 4 (Monkey Island US Disk 1-4) + 1 (Dylan Dog IT) = 6 valides
        result.Stats.ValidCount.Should().Be(6);
        // 2 (Another World GB, DE) + 4 (Monkey Island DE Disk 1-4) = 6 exclus
        result.Stats.ExcludedCount.Should().Be(6);

        File.Exists(Path.Combine(dst, "Another World", "Another World (1991)(Delphine)(FR)[!].adf")).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Another World", "Another World (1991)(Delphine)(GB).adf")).Should().BeFalse();
        File.Exists(Path.Combine(dst, "Secret of Monkey Island, The", "Secret of Monkey Island, The (1990)(Lucasfilm Games)(US)(Disk 1 of 4).adf")).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Secret of Monkey Island, The", "Secret of Monkey Island, The (1990)(Lucasfilm Games)(DE)(Disk 1 of 4).adf")).Should().BeFalse();
        File.Exists(Path.Combine(dst, "Dylan Dog", "Dylan Dog (1992)(Simulmondo)(IT).adf")).Should().BeTrue();
    }

    [Fact]
    public async Task LanguageCascadePriority_FrThenMThenEn_ShouldSelectExpectedReleaseForEachScenario()
    {
        var src = _temp.CreateSubdirectory("src_cascade");
        var dst = _temp.CreateSubdirectory("dst_cascade");

        // Case 1: FR, M3, and EN exist -> FR must win (rank 0)
        File.WriteAllText(Path.Combine(src, "Flashback (1992)(Delphine)(FR).adf"), "fr");
        File.WriteAllText(Path.Combine(src, "Flashback (1992)(Delphine)(M3).adf"), "m3");
        File.WriteAllText(Path.Combine(src, "Flashback (1992)(Delphine)(GB).adf"), "en");

        // Case 2: No dedicated FR, but M3 and EN exist -> M3 must win (rank 1)
        File.WriteAllText(Path.Combine(src, "Syndicate (1993)(Bullfrog)(M3).adf"), "m3");
        File.WriteAllText(Path.Combine(src, "Syndicate (1993)(Bullfrog)(GB).adf"), "en");
        File.WriteAllText(Path.Combine(src, "Syndicate (1993)(Bullfrog)(DE).adf"), "de");

        // Case 3: Neither FR nor M, but EN and DE exist -> EN must win (rank 2)
        File.WriteAllText(Path.Combine(src, "Lemmings (1991)(Psygnosis)(US).adf"), "en");
        File.WriteAllText(Path.Combine(src, "Lemmings (1991)(Psygnosis)(DE).adf"), "de");

        // Case 4: Only ES -> Excluded because fallback_unmatched_languages=False
        File.WriteAllText(Path.Combine(src, "La Abadia del Crimen (1988)(Opera Soft)(ES).adf"), "es");

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{title}/",
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                PriorityLanguages = ["fr", "m", "en"],
                FallbackUnmatchedLanguages = false
            }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);

        result.Stats.ValidCount.Should().Be(3);
        result.Stats.ExcludedCount.Should().Be(6);

        File.Exists(Path.Combine(dst, "Flashback", "Flashback (1992)(Delphine)(FR).adf")).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Syndicate", "Syndicate (1993)(Bullfrog)(M3).adf")).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Lemmings", "Lemmings (1991)(Psygnosis)(US).adf")).Should().BeTrue();
        File.Exists(Path.Combine(dst, "La Abadia del Crimen", "La Abadia del Crimen (1988)(Opera Soft)(ES).adf")).Should().BeFalse();
    }

    [Fact]
    public async Task ProcessorIgnoreModificationsInPriority_ShouldKeepFrTrainerAndExcludeEnClean()
    {
        var src = _temp.CreateSubdirectory("src_ignore_mod");
        var dst = _temp.CreateSubdirectory("dst_ignore_mod");

        File.WriteAllText(Path.Combine(src, "Dune (1992)(Virgin)(FR)[t +2].adf"), "fr_trainer");
        File.WriteAllText(Path.Combine(src, "Dune (1992)(Virgin)(GB)[!].adf"), "en_clean");

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{title}/",
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                ExcludeModifications = true,
                OneGameOneRom = true,
                PriorityLanguages = ["fr", "en"],
                IgnoreModificationsInPriority = true
            }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);

        result.Stats.ValidCount.Should().Be(1);
        result.Stats.ExcludedCount.Should().Be(1);
        File.Exists(Path.Combine(dst, "Dune", "Dune (1992)(Virgin)(FR)[t +2].adf")).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Dune", "Dune (1992)(Virgin)(GB)[!].adf")).Should().BeFalse();
    }

    [Fact]
    public async Task Processor1G1RDeduplication_CleanOverCracks_ShouldKeepOnlyCleanVerifiedRelease()
    {
        var src = _temp.CreateSubdirectory("src_clean_over_cracks");
        var dst = _temp.CreateSubdirectory("dst_clean_over_cracks");

        File.WriteAllText(Path.Combine(src, "Shadow of the Beast (1989)(Psygnosis)(GB)[cr Skid Row][t +5].adf"), "crack1");
        File.WriteAllText(Path.Combine(src, "Shadow of the Beast (1989)(Psygnosis)(GB)[cr Quartex].adf"), "crack2");
        File.WriteAllText(Path.Combine(src, "Shadow of the Beast (1989)(Psygnosis)(GB)[!].adf"), "clean_verified");

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{title}/",
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                ExcludeModifications = false,
                OneGameOneRom = true,
                PriorityLanguages = ["en"]
            }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);

        result.Stats.ValidCount.Should().Be(1);
        result.Stats.ExcludedCount.Should().Be(2);
        File.Exists(Path.Combine(dst, "Shadow of the Beast", "Shadow of the Beast (1989)(Psygnosis)(GB)[!].adf")).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Shadow of the Beast", "Shadow of the Beast (1989)(Psygnosis)(GB)[cr Quartex].adf")).Should().BeFalse();
    }

    [Fact]
    public async Task ProcessorPurgeDestination_ShouldRemoveOldContentWhenPurgeDestinationIsTrue()
    {
        var (src, dst) = CreateSampleEnv();
        var oldFolder = Path.Combine(dst, "OldPublisher", "OldGame");
        Directory.CreateDirectory(oldFolder);
        var oldFile = Path.Combine(oldFolder, "OldGame (1985)(OldPub).adf");
        File.WriteAllText(oldFile, "old_content");

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{publisher}/{title}/",
            TransferMode = TransferMode.Copy,
            PurgeDestination = true,
            AppendToDestination = false
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);

        result.Stats.ErrorCount.Should().Be(0);
        File.Exists(oldFile).Should().BeFalse();
        Directory.Exists(Path.Combine(dst, "OldPublisher")).Should().BeFalse();
        File.Exists(Path.Combine(dst, "Delphine", "Another World", "Another World (1991)(Delphine)(FR)[!].adf")).Should().BeTrue();
    }

    [Fact]
    public async Task ProcessorAppendToDestination_ShouldKeepExistingFilesWhenAppendToDestinationIsTrue()
    {
        var (src, dst) = CreateSampleEnv();
        var oldFolder = Path.Combine(dst, "OldPublisher", "OldGame");
        Directory.CreateDirectory(oldFolder);
        var oldFile = Path.Combine(oldFolder, "OldGame (1985)(OldPub).adf");
        File.WriteAllText(oldFile, "old_content");

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{publisher}/{title}/",
            TransferMode = TransferMode.Copy,
            PurgeDestination = false,
            AppendToDestination = true
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);

        result.Stats.ErrorCount.Should().Be(0);
        File.Exists(oldFile).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Delphine", "Another World", "Another World (1991)(Delphine)(FR)[!].adf")).Should().BeTrue();
    }

    [Fact]
    public async Task ProcessorSortWithDatFile_ShouldIdentifyAndRenameMisnamedFileViaDatCrc32()
    {
        var src = _temp.CreateSubdirectory("src_dat");
        var dst = _temp.CreateSubdirectory("dst_dat");

        var romBytes = Encoding.ASCII.GetBytes("OFFICIAL_ROM_CONTENT_FOR_DAT_TEST");
        var badNamedRom = Path.Combine(src, "my_favorite_game_dump.adf");
        File.WriteAllBytes(badNamedRom, romBytes);

        var crc = await Checksum.ComputeFileCrc32Async(badNamedRom);
        var size = romBytes.Length;
        var canonicalName = "Another World (1991)(Delphine)(FR)[!].adf";

        var datXml = $"""
            <?xml version="1.0"?>
            <datafile>
                <header>
                    <name>Commodore Amiga - Games - [ADF]</name>
                    <version>2023-01-01</version>
                </header>
                <game name="Another World (1991)(Delphine)(FR)[!]">
                    <description>Another World (1991)(Delphine)(FR)[!]</description>
                    <rom name="{canonicalName}" size="{size}" crc="{crc}" />
                </game>
            </datafile>
            """;

        var datFile = _temp.WriteText("amiga.dat", datXml);

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            DatFile = datFile,
            Pattern = "{publisher}/{title}/",
            TransferMode = TransferMode.Copy
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);

        result.Stats.ValidCount.Should().Be(1);
        result.Stats.UnmatchedCount.Should().Be(0);

        var expectedTarget = Path.Combine(dst, "Delphine", "Another World", canonicalName);
        File.Exists(expectedTarget).Should().BeTrue();
    }

    [Fact]
    public async Task ProcessorZipArchivesInspectionAndExtraction_ShouldExtractZipMemberWhenScanArchivesIsTrue()
    {
        var src = _temp.CreateSubdirectory("src_zip");
        var dst = _temp.CreateSubdirectory("dst_zip");

        var memberName = "Flashback (1992)(Delphine)(FR)[!].adf";
        var zipPath = Path.Combine(src, "badly_named_archive.zip");
        using (var zf = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zf.CreateEntry(memberName);
            using var writer = new StreamWriter(entry.Open(), Encoding.ASCII);
            writer.Write("AMIGA_DISK_IMAGE_CONTENT");
        }

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{publisher}/{title}/",
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                ScanArchives = true
            }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);

        result.Stats.ValidCount.Should().Be(1);
        var expectedExtracted = Path.Combine(dst, "Delphine", "Flashback", memberName);
        File.Exists(expectedExtracted).Should().BeTrue();
        (await File.ReadAllTextAsync(expectedExtracted, Encoding.ASCII)).Should().Be("AMIGA_DISK_IMAGE_CONTENT");
    }

    [Fact]
    public async Task ProcessorMultidiskCompleteness_ShouldExcludeIncompleteSetAndKeepCompleteSet()
    {
        var src = _temp.CreateSubdirectory("src_multidisk");
        var dst = _temp.CreateSubdirectory("dst_multidisk");

        // Incomplete game: only Disk 1 of 2
        File.WriteAllText(Path.Combine(src, "Incomplete Game (1991)(Pub)(Disk 1 of 2).adf"), "d1");

        // Complete game: Disk 1 of 2 and Disk 2 of 2
        File.WriteAllText(Path.Combine(src, "Complete Game (1991)(Pub)(Disk 1 of 2).adf"), "d1");
        File.WriteAllText(Path.Combine(src, "Complete Game (1991)(Pub)(Disk 2 of 2).adf"), "d2");

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{title}/",
            SubfolderMultidisk = false,
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig { CompleteMultidiskSetsOnly = true }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);

        result.Stats.ValidCount.Should().Be(2);
        result.Stats.ExcludedCount.Should().Be(1);

        File.Exists(Path.Combine(dst, "Complete Game", "Complete Game (1991)(Pub)(Disk 1 of 2).adf")).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Complete Game", "Complete Game (1991)(Pub)(Disk 2 of 2).adf")).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Incomplete Game", "Incomplete Game (1991)(Pub)(Disk 1 of 2).adf")).Should().BeFalse();
    }

    [Fact]
    public async Task ProcessorMissListAndCopierHeaders_ShouldMatchHeaderedNesRomAndExportMissingRoms()
    {
        var src = _temp.CreateSubdirectory("src_misslist");
        var dst = _temp.CreateSubdirectory("dst_misslist");

        var pureRom = new byte[16384];
        Array.Fill(pureRom, (byte)'M');
        var purePath = _temp.GetFilePath("pure.nes");
        File.WriteAllBytes(purePath, pureRom);
        var expectedCrc = await Checksum.ComputeFileCrc32Async(purePath);

        var header = Encoding.ASCII.GetBytes("NES\x1a\x01\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00");
        var romWithHeader = Path.Combine(src, "Super Mario Bros (1985)(Nintendo)(US).nes");
        var combined = new byte[header.Length + pureRom.Length];
        Buffer.BlockCopy(header, 0, combined, 0, header.Length);
        Buffer.BlockCopy(pureRom, 0, combined, header.Length, pureRom.Length);
        File.WriteAllBytes(romWithHeader, combined);

        var datXml = $"""
            <?xml version="1.0"?>
            <datafile>
                <header>
                    <name>Nintendo NES - Games</name>
                    <version>2023-05-01</version>
                </header>
                <game name="Super Mario Bros (1985)(Nintendo)(US)">
                    <rom name="Super Mario Bros (1985)(Nintendo)(US).nes" size="16384" crc="{expectedCrc}" />
                </game>
                <game name="Zelda (1986)(Nintendo)(US)">
                    <rom name="Zelda (1986)(Nintendo)(US).nes" size="32768" crc="DEADBEEF" />
                </game>
            </datafile>
            """;

        var datFile = _temp.WriteText("nes.dat", datXml);

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            DatFile = datFile,
            Pattern = "{title}/",
            Filters = new FilterConfig
            {
                DetectCopierHeaders = true
            }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: true);

        result.Stats.ValidCount.Should().Be(1);
        result.Stats.DatTotalRoms.Should().Be(2);
        result.Stats.DatMatchedCount.Should().Be(1);
        result.Stats.DatMissingCount.Should().Be(1);
        result.MissingRoms.Should().ContainSingle();
        result.MissingRoms[0].Name.Should().Be("Zelda (1986)(Nintendo)(US).nes");

        var missCsv = _temp.GetFilePath("miss.csv");
        var missTxt = _temp.GetFilePath("miss.txt");
        await Processor.ExportMissListToCsvAsync(result.MissingRoms, result.DatCatalog!, missCsv);
        await Processor.ExportMissListToTxtAsync(result.MissingRoms, result.DatCatalog!, missTxt);

        (await File.ReadAllTextAsync(missCsv, Encoding.UTF8)).Should().Contain("Zelda (1986)(Nintendo)(US).nes");
        (await File.ReadAllTextAsync(missTxt, Encoding.UTF8)).Should().Contain("Zelda (1986)(Nintendo)(US).nes");
    }

    [Fact]
    public async Task Processor1G1RVideoStandardPreference_ShouldSelectPreferredPalOrNtscRelease()
    {
        var src = _temp.CreateSubdirectory("src_video");
        var dst = _temp.CreateSubdirectory("dst_video");

        File.WriteAllText(Path.Combine(src, "Sonic (1991)(Sega)(NTSC).bin"), "ntsc");
        File.WriteAllText(Path.Combine(src, "Sonic (1991)(Sega)(PAL).bin"), "pal");

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{title}/",
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                PriorityLanguages = ["en"],
                PreferredVideo = VideoPreference.Pal
            }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);
        result.Stats.ValidCount.Should().Be(1);
        File.Exists(Path.Combine(dst, "Sonic", "Sonic (1991)(Sega)(PAL).bin")).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Sonic", "Sonic (1991)(Sega)(NTSC).bin")).Should().BeFalse();

        var dstNtsc = _temp.CreateSubdirectory("dst_ntsc");
        config.DestinationDir = dstNtsc;
        config.Filters.PreferredVideo = VideoPreference.Ntsc;

        var resultNtsc = await Processor.ExecuteAsync(config, isDryRun: false);
        resultNtsc.Stats.ValidCount.Should().Be(1);
        File.Exists(Path.Combine(dstNtsc, "Sonic", "Sonic (1991)(Sega)(NTSC).bin")).Should().BeTrue();
    }

    [Fact]
    public async Task Processor1G1RMediaTypePreference_ShouldPreferCartOverFloppyAndTape()
    {
        var src = _temp.CreateSubdirectory("src_media");
        var dst = _temp.CreateSubdirectory("dst_media");

        File.WriteAllText(Path.Combine(src, "Batman (1989)(Ocean)(GB).tap"), "tape");
        File.WriteAllText(Path.Combine(src, "Batman (1989)(Ocean)(GB).d64"), "floppy");
        File.WriteAllText(Path.Combine(src, "Batman (1989)(Ocean)(GB).crt"), "cart");

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{title}/",
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                PriorityLanguages = ["en"],
                PreferredMediaType = MediaPreference.Cartridge
            }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);
        result.Stats.ValidCount.Should().Be(1);
        result.Stats.ExcludedCount.Should().Be(2);
        File.Exists(Path.Combine(dst, "Batman", "Batman (1989)(Ocean)(GB).crt")).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Batman", "Batman (1989)(Ocean)(GB).d64")).Should().BeFalse();
        File.Exists(Path.Combine(dst, "Batman", "Batman (1989)(Ocean)(GB).tap")).Should().BeFalse();
    }

    [Fact]
    public async Task Processor1G1RMonolingualVsMultilingual_ShouldPreferMonolingualOrMultilingualAccordingToSetting()
    {
        var src = _temp.CreateSubdirectory("src_mono");
        var dst = _temp.CreateSubdirectory("dst_mono");

        // ROM 1: Explicit multilingual (en-fr) -> Contains 'fr' (rank 0) but is_monolingual = False
        File.WriteAllText(Path.Combine(src, "Rayman (1995)(Ubisoft)(en-fr).iso"), "multi");
        // ROM 2: Monolingual (FR) -> Contains 'fr' (rank 0) and is_monolingual = True
        File.WriteAllText(Path.Combine(src, "Rayman (1995)(Ubisoft)(FR).iso"), "mono");

        // Case 1: prefer_monolingual = True (default) -> Monolingual FR wins
        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{title}/",
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                PriorityLanguages = ["fr", "en"],
                PreferMonolingual = true
            }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);
        result.Stats.ValidCount.Should().Be(1);
        File.Exists(Path.Combine(dst, "Rayman", "Rayman (1995)(Ubisoft)(FR).iso")).Should().BeTrue();

        // Case 2: prefer_monolingual = False -> Multilingual (en-fr) wins
        var dst2 = _temp.CreateSubdirectory("dst_multi");
        config.DestinationDir = dst2;
        config.Filters.PreferMonolingual = false;

        var result2 = await Processor.ExecuteAsync(config, isDryRun: false);
        result2.Stats.ValidCount.Should().Be(1);
        File.Exists(Path.Combine(dst2, "Rayman", "Rayman (1995)(Ubisoft)(en-fr).iso")).Should().BeTrue();
    }

    [Fact]
    public async Task Processor1G1RWinnerTrackingAndReporting_ShouldPopulateWinnerAndEliminationDetails()
    {
        var src = _temp.CreateSubdirectory("src_winner_track");
        var dst = _temp.CreateSubdirectory("dst_winner_track");

        File.WriteAllText(Path.Combine(src, "Chaos Engine, The (1993)(Renegade)(FR)(AGA)[!].adf"), "winner");
        File.WriteAllText(Path.Combine(src, "Chaos Engine, The (1993)(Renegade)(DE)(AGA).adf"), "loser_lang");
        File.WriteAllText(Path.Combine(src, "Chaos Engine, The (1993)(Renegade)(FR)(AGA)[cr FLT].adf"), "loser_crack");

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Filters = new FilterConfig
            {
                ExcludeModifications = false,
                OneGameOneRom = true,
                PriorityLanguages = ["fr", "en"]
            }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: true);
        var itemsByName = result.Items.ToDictionary(it => it.DisplayName);

        var winner = itemsByName["Chaos Engine, The (1993)(Renegade)(FR)(AGA)[!].adf"];
        winner.Status.Should().Be(FileStatus.Valid);
        winner.Is1G1RWinner.Should().BeTrue();

        var loserLang = itemsByName["Chaos Engine, The (1993)(Renegade)(DE)(AGA).adf"];
        loserLang.Status.Should().Be(FileStatus.Excluded);
        loserLang.Is1G1RWinner.Should().BeFalse();
        loserLang.WinnerReference.Should().Be("Chaos Engine, The (1993)(Renegade)(FR)(AGA)[!].adf");
        I18n.Format(loserLang.StatusReason).Should().Contain("langue FR préférée à");
        I18n.Format(loserLang.StatusReason).Should().Contain("Chaos Engine, The (1993)(Renegade)(FR)(AGA)[!].adf");

        var loserCrack = itemsByName["Chaos Engine, The (1993)(Renegade)(FR)(AGA)[cr FLT].adf"];
        loserCrack.Status.Should().Be(FileStatus.Excluded);
        loserCrack.WinnerReference.Should().Be("Chaos Engine, The (1993)(Renegade)(FR)(AGA)[!].adf");
        I18n.Format(loserCrack.StatusReason).Should().Contain("dump vérifié [!]");
    }

    [Fact]
    public async Task ProcessorMultidiskSubfolderWithTosecDat_ShouldCreateSubfolderForPaginatedSetEvenWithSingleRomDatEntries()
    {
        var src = _temp.CreateSubdirectory("src_tosec_multi_sub");
        var dst = _temp.CreateSubdirectory("dst_tosec_multi_sub");

        var d1Name = "Monkey Island (1990)(Lucasfilm)(FR)(Disk 1 of 2).adf";
        var d2Name = "Monkey Island (1990)(Lucasfilm)(FR)(Disk 2 of 2).adf";
        var sName = "Another World (1991)(Delphine)(FR).adf";

        var d1Path = Path.Combine(src, d1Name);
        var d2Path = Path.Combine(src, d2Name);
        var sPath = Path.Combine(src, sName);

        File.WriteAllBytes(d1Path, Encoding.ASCII.GetBytes("DISK1_CONTENT"));
        File.WriteAllBytes(d2Path, Encoding.ASCII.GetBytes("DISK2_CONTENT"));
        File.WriteAllBytes(sPath, Encoding.ASCII.GetBytes("SINGLE_DISK_CONTENT"));

        var crc1 = await Checksum.ComputeFileCrc32Async(d1Path);
        var crc2 = await Checksum.ComputeFileCrc32Async(d2Path);
        var crcS = await Checksum.ComputeFileCrc32Async(sPath);

        var datXml = $"""
            <?xml version="1.0"?>
            <datafile>
                <header>
                    <name>Commodore Amiga - Games - [ADF]</name>
                    <version>2023-01-01</version>
                </header>
                <game name="Monkey Island (1990)(Lucasfilm)(FR)(Disk 1 of 2)">
                    <rom name="{d1Name}" size="13" crc="{crc1}" />
                </game>
                <game name="Monkey Island (1990)(Lucasfilm)(FR)(Disk 2 of 2)">
                    <rom name="{d2Name}" size="13" crc="{crc2}" />
                </game>
                <game name="Another World (1991)(Delphine)(FR)">
                    <rom name="{sName}" size="19" crc="{crcS}" />
                </game>
            </datafile>
            """;

        var datFile = _temp.WriteText("amiga_tosec.dat", datXml);

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            DatFile = datFile,
            Pattern = "{publisher}/",
            SubfolderMultidisk = true,
            TransferMode = TransferMode.Copy
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);
        result.Stats.ValidCount.Should().Be(3);

        // Monkey Island (2 disks) must have a "Monkey Island" subfolder under "Lucasfilm"
        File.Exists(Path.Combine(dst, "Lucasfilm", "Monkey Island", d1Name)).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Lucasfilm", "Monkey Island", d2Name)).Should().BeTrue();

        // Another World (1 disk) must be directly under "Delphine"
        File.Exists(Path.Combine(dst, "Delphine", sName)).Should().BeTrue();
    }

    [Fact]
    public async Task ProcessorMultidiskCompletenessWithTosecDat_ShouldDetectIncompleteSetViaFilenamePagination()
    {
        var src = _temp.CreateSubdirectory("src_tosec_dat_incomplete");
        var dst = _temp.CreateSubdirectory("dst_tosec_dat_incomplete");

        var d1Name = "Incomplete Quest (1992)(Sierra)(Disk 1 of 3).adf";
        var d1Path = Path.Combine(src, d1Name);
        File.WriteAllBytes(d1Path, Encoding.ASCII.GetBytes("ONLY_DISK_1"));
        var crc1 = await Checksum.ComputeFileCrc32Async(d1Path);

        var datXml = $"""
            <?xml version="1.0"?>
            <datafile>
                <header>
                    <name>Commodore Amiga - Games</name>
                    <version>2023-01-01</version>
                </header>
                <game name="Incomplete Quest (1992)(Sierra)(Disk 1 of 3)">
                    <rom name="{d1Name}" size="11" crc="{crc1}" />
                </game>
            </datafile>
            """;

        var datFile = _temp.WriteText("amiga.dat", datXml);

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            DatFile = datFile,
            Pattern = "{title}/",
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig { CompleteMultidiskSetsOnly = true }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);
        result.Stats.ValidCount.Should().Be(0);
        result.Stats.ExcludedCount.Should().Be(1);
    }

    [Fact]
    public async Task Processor1G1RAaarghCorruptFileDeduplication_ShouldKeepOnlyCleanDumpAndExcludeCorruptAndCracks()
    {
        var src = _temp.CreateSubdirectory("src_aaargh");
        var dst = _temp.CreateSubdirectory("dst_aaargh");

        var fClean = "Aaargh! (1988)(Melbourne House).adf";
        var fCrack = "Aaargh! (1988)(Melbourne House)[cr Quartermain][t +2 Mega Mighty Monolithic].adf";
        var fCorrupt = "Aaargh! (1988)(Melbourne House)[b corrupt file].adf";
        var fVirus = "Aaargh! (1988)(Melbourne House)[v Byte Bandit].adf";

        File.WriteAllText(Path.Combine(src, fClean), "clean");
        File.WriteAllText(Path.Combine(src, fCrack), "crack");
        File.WriteAllText(Path.Combine(src, fCorrupt), "corrupt");
        File.WriteAllText(Path.Combine(src, fVirus), "virus");

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{title}/",
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                ExcludeBadDumps = true,
                ExcludeVirus = true,
                ExcludeModifications = false,
                OneGameOneRom = true,
                PriorityLanguages = ["fr", "en", "m"],
                FallbackUnmatchedLanguages = true
            }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);

        result.Stats.ValidCount.Should().Be(1);
        result.Stats.ExcludedCount.Should().Be(3);

        File.Exists(Path.Combine(dst, "Aaargh!", fClean)).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Aaargh!", fCrack)).Should().BeFalse();
        File.Exists(Path.Combine(dst, "Aaargh!", fCorrupt)).Should().BeFalse();
        File.Exists(Path.Combine(dst, "Aaargh!", fVirus)).Should().BeFalse();
    }

    [Fact]
    public async Task Processor1G1RSingleDiskDuplicatePrevention_ShouldRetainSingleCleanDiskOverModifiedVariants()
    {
        var src = _temp.CreateSubdirectory("src_single_dup");
        var dst = _temp.CreateSubdirectory("dst_single_dup");

        var fClean = "Rick Dangerous (1989)(Core Design).adf";
        var fHighscore = "Rick Dangerous (1989)(Core Design)[m2 highscore].adf";
        var fSavegame = "Rick Dangerous (1989)(Core Design)[savegame].adf";

        File.WriteAllText(Path.Combine(src, fClean), "clean");
        File.WriteAllText(Path.Combine(src, fHighscore), "highscore");
        File.WriteAllText(Path.Combine(src, fSavegame), "savegame");

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{title}/",
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                ExcludeModifications = false,
                OneGameOneRom = true,
                PriorityLanguages = ["fr", "en"],
                FallbackUnmatchedLanguages = true
            }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);

        result.Stats.ValidCount.Should().Be(1);
        (result.Stats.ExcludedCount + result.Stats.DuplicateCount).Should().Be(2);
        File.Exists(Path.Combine(dst, "Rick Dangerous", fClean)).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Rick Dangerous", fHighscore)).Should().BeFalse();
        File.Exists(Path.Combine(dst, "Rick Dangerous", fSavegame)).Should().BeFalse();
    }

    [Fact]
    public void ProcessorProgressSignalLargeByteValues_ShouldSupport64BitByteCountsAbove2GiB()
    {
        var largeProcessed = 3_500_000_000L; // ~3.5 GB (> 2^31 - 1)
        var largeTotal = 5_000_000_000L;     // ~5.0 GB

        var progress = new SortProgress(
            ProcessedCount: 10,
            TotalCount: 20,
            ProcessedBytes: largeProcessed,
            TotalBytes: largeTotal,
            EtaSeconds: 12.5,
            Message: LocalizedMessage.Verbatim("Test large progress"));

        progress.ProcessedBytes.Should().Be(largeProcessed);
        progress.TotalBytes.Should().Be(largeTotal);
    }

    [Fact]
    public async Task SmartMultidiskGrouping_ShouldGroupCrackedDisk1WithCleanDisk2WhenEnabled()
    {
        var src = _temp.CreateSubdirectory("src_smart_multi");
        var dst = _temp.CreateSubdirectory("dst_smart_multi");

        var d1 = "Shadow of the Beast II (1990)(Psygnosis)(Disk 1 of 2)[cr FLT][t +3].adf";
        var d2 = "Shadow of the Beast II (1990)(Psygnosis)(Disk 2 of 2).adf";

        File.WriteAllText(Path.Combine(src, d1), "disk1_cracked");
        File.WriteAllText(Path.Combine(src, d2), "disk2_clean");

        // Case 1: smart_multidisk_grouping = True (default) -> The set is recognized as complete (2/2)
        var configSmart = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{title}/",
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                ExcludeModifications = false,
                CompleteMultidiskSetsOnly = true,
                SmartMultidiskGrouping = true,
                OneGameOneRom = true,
                PriorityLanguages = ["en"],
                FallbackUnmatchedLanguages = true
            }
        };

        var resSmart = await Processor.ExecuteAsync(configSmart, isDryRun: true);
        resSmart.Stats.ValidCount.Should().Be(2);
        resSmart.Stats.ExcludedCount.Should().Be(0);

        // Case 2: smart_multidisk_grouping = False (strict) -> The two disks have separate keys (1/2 each) -> Excluded!
        var configStrict = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{title}/",
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                ExcludeModifications = false,
                CompleteMultidiskSetsOnly = true,
                SmartMultidiskGrouping = false
            }
        };

        var resStrict = await Processor.ExecuteAsync(configStrict, isDryRun: true);
        resStrict.Stats.ValidCount.Should().Be(0);
        resStrict.Stats.ExcludedCount.Should().Be(2);
    }

    [Fact]
    public async Task SmartMultidiskCleanDataDiskPairing_SpaceCrusade_ShouldCompleteWinningVariantWithSharedCleanDataDisk()
    {
        var src = _temp.CreateSubdirectory("src_space_crusade");
        var dst = _temp.CreateSubdirectory("dst_space_crusade");

        var d1V1 = "Space Crusade (1992)(Gremlin)(M3)(Disk 1 of 2)[cr VF][f2][WB].adf";
        var d1V2 = "Space Crusade (1992)(Gremlin)(M3)(Disk 1 of 2)[cr VF][f][WB].adf";
        var d1V3 = "Space Crusade (1992)(Gremlin)(M3)(Disk 1 of 2)[cr VF][t +2][WB].adf";
        var d2Clean = "Space Crusade (1992)(Gremlin)(M3)(Disk 2 of 2)[WB].adf";

        File.WriteAllText(Path.Combine(src, d1V1), "d1_v1");
        File.WriteAllText(Path.Combine(src, d1V2), "d1_v2");
        File.WriteAllText(Path.Combine(src, d1V3), "d1_v3");
        File.WriteAllText(Path.Combine(src, d2Clean), "d2_clean");

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{title}/",
            SubfolderMultidisk = false,
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                ExcludeModifications = false,
                CompleteMultidiskSetsOnly = true,
                SmartMultidiskGrouping = true,
                OneGameOneRom = true,
                PriorityLanguages = ["fr", "m", "en"],
                FallbackUnmatchedLanguages = true
            }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);

        // Exactly 2 files retained: 1 Disk 1 (the best) + shared clean Disk 2
        result.Stats.ValidCount.Should().Be(2);
        result.Stats.DuplicateCount.Should().Be(2);

        File.Exists(Path.Combine(dst, "Space Crusade", d2Clean)).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Space Crusade", d1V2)).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Space Crusade", d1V1)).Should().BeFalse();
        File.Exists(Path.Combine(dst, "Space Crusade", d1V3)).Should().BeFalse();
    }

    [Fact]
    public async Task Robocop3IncompleteSecondarySetNotSelectedIn1G1R_ShouldSelectComplete3DiskSetOverIncompleteLanguageVariant()
    {
        var src = _temp.CreateSubdirectory("src_robocop3");
        var dst = _temp.CreateSubdirectory("dst_robocop3");

        // Incomplete M3 release (only Disk 1 of 3)
        var m3D1 = "Robocop 3 (1991)(Ocean)(M3)(Disk 1 of 3)[cr][t].adf";
        // Release without language tag but complete (3/3)
        var enD1 = "Robocop 3 (1991)(Ocean)(Disk 1 of 3)[cr].adf";
        var enD2 = "Robocop 3 (1991)(Ocean)(Disk 2 of 3).adf";
        var enD3 = "Robocop 3 (1991)(Ocean)(Disk 3 of 3).adf";

        File.WriteAllText(Path.Combine(src, m3D1), "m3_d1");
        File.WriteAllText(Path.Combine(src, enD1), "en_d1");
        File.WriteAllText(Path.Combine(src, enD2), "en_d2");
        File.WriteAllText(Path.Combine(src, enD3), "en_d3");

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{title}/",
            SubfolderMultidisk = false,
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                ExcludeModifications = false,
                CompleteMultidiskSetsOnly = true,
                SmartMultidiskGrouping = true,
                OneGameOneRom = true,
                PriorityLanguages = ["fr", "m", "en"],
                FallbackUnmatchedLanguages = true
            }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);

        // The complete 3-disk set must win (3 files retained), and the isolated M3 disk must be excluded
        result.Stats.ValidCount.Should().Be(3);
        result.Stats.ExcludedCount.Should().Be(1);

        File.Exists(Path.Combine(dst, "Robocop 3", enD1)).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Robocop 3", enD2)).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Robocop 3", enD3)).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Robocop 3", m3D1)).Should().BeFalse();
    }

    [Fact]
    public async Task TmntMultidisk2DiskVs3DiskDeduplication_ShouldKeepSingleCompleteEditionWithoutMixingTotals()
    {
        var src = _temp.CreateSubdirectory("src_tmnt");
        var dst = _temp.CreateSubdirectory("dst_tmnt");

        var d1Of2 = "Teenage Mutant Ninja Turtles (1990)(Mirrorsoft)(M3)(Disk 1 of 2)[cr].adf";
        var d2Of2 = "Teenage Mutant Ninja Turtles (1990)(Mirrorsoft)(M3)(Disk 2 of 2)[cr].adf";
        var d2Of2Alt = "Teenage Mutant Ninja Turtles (1990)(Mirrorsoft)(M3)(Disk 2 of 2)[cr][a].adf";
        var d1Of3 = "Teenage Mutant Ninja Turtles (1990)(Mirrorsoft)(M3)(Disk 1 of 3)[cr][t].adf";
        var d2Of3 = "Teenage Mutant Ninja Turtles (1990)(Mirrorsoft)(M3)(Disk 2 of 3).adf";
        var d3Of3 = "Teenage Mutant Ninja Turtles (1990)(Mirrorsoft)(M3)(Disk 3 of 3).adf";

        foreach (var f in new[] { d1Of2, d2Of2, d2Of2Alt, d1Of3, d2Of3, d3Of3 })
        {
            File.WriteAllText(Path.Combine(src, f), f);
        }

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{title}/",
            SubfolderMultidisk = false,
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                ExcludeModifications = false,
                ExcludeAlternate = false,
                CompleteMultidiskSetsOnly = true,
                SmartMultidiskGrouping = true,
                OneGameOneRom = true,
                PriorityLanguages = ["fr", "m", "en"],
                FallbackUnmatchedLanguages = true
            }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);

        // The 2-disk version (without trainer [t]) has a better cleanliness score than the 3-disk one (which has [cr][t]).
        // It must win with exactly 2 files (Disk 1 of 2 and Disk 2 of 2 without [a]).
        result.Stats.ValidCount.Should().Be(2);
        result.Stats.ExcludedCount.Should().Be(4);

        File.Exists(Path.Combine(dst, "Teenage Mutant Ninja Turtles", d1Of2)).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Teenage Mutant Ninja Turtles", d2Of2)).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Teenage Mutant Ninja Turtles", d2Of2Alt)).Should().BeFalse();
        File.Exists(Path.Combine(dst, "Teenage Mutant Ninja Turtles", d1Of3)).Should().BeFalse();
        File.Exists(Path.Combine(dst, "Teenage Mutant Ninja Turtles", d2Of3)).Should().BeFalse();
        File.Exists(Path.Combine(dst, "Teenage Mutant Ninja Turtles", d3Of3)).Should().BeFalse();
    }

    [Fact]
    public async Task GenesiaMultidiskInGameLabelAndBootableFlags_ShouldGroupAll3DisksDespiteBootableTagAndInnerLabel()
    {
        var src = _temp.CreateSubdirectory("src_genesia");
        var dst = _temp.CreateSubdirectory("dst_genesia");

        var d1 = "Genesia (1993)(Microids)(FR)(Disk 1 of 3)[bootable].adf";
        var d2 = "Genesia (1993)(Microids)(FR)(Disk 2 of 3)(Disk 1).adf";
        var d3 = "Genesia (1993)(Microids)(FR)(Disk 3 of 3)(Disk 2).adf";

        var d1Cr = "Genesia (1993)(Microids)(FR)(Disk 1 of 3)[cr CLS].adf";
        var d2Cr = "Genesia (1993)(Microids)(FR)(Disk 2 of 3)(Disk 1)[cr CLS].adf";
        var d3Cr = "Genesia (1993)(Microids)(FR)(Disk 3 of 3)(Disk 2)[cr CLS].adf";

        foreach (var f in new[] { d1, d2, d3, d1Cr, d2Cr, d3Cr })
        {
            File.WriteAllText(Path.Combine(src, f), f);
        }

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{title}/",
            SubfolderMultidisk = false,
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                ExcludeModifications = false,
                CompleteMultidiskSetsOnly = true,
                SmartMultidiskGrouping = true,
                OneGameOneRom = true,
                PriorityLanguages = ["fr", "en"],
                FallbackUnmatchedLanguages = true
            }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);

        result.Stats.ValidCount.Should().Be(3);
        result.Stats.ExcludedCount.Should().Be(3);

        File.Exists(Path.Combine(dst, "Genesia", d1)).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Genesia", d2)).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Genesia", d3)).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Genesia", d1Cr)).Should().BeFalse();
    }

    [Fact]
    public async Task SingleMedia1G1RCleanOverHacksAndSmartMulti_ShouldPreferCleanOverShorterFilenameHack()
    {
        var src = _temp.CreateSubdirectory("src_single_clean_over_hack");
        var dst = _temp.CreateSubdirectory("dst_single_clean_over_hack");

        var fHack = "Super Cars (1990)(Gremlin)[h].adf";
        var fClean = "Super Cars (1990)(Gremlin)(Intro).adf";

        File.WriteAllText(Path.Combine(src, fHack), "hack");
        File.WriteAllText(Path.Combine(src, fClean), "clean");

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{title}/",
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                ExcludeModifications = false,
                SmartMultidiskGrouping = true,
                OneGameOneRom = true,
                PriorityLanguages = ["en"],
                FallbackUnmatchedLanguages = true
            }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);

        result.Stats.ValidCount.Should().Be(1);
        result.Stats.ExcludedCount.Should().Be(1);
        File.Exists(Path.Combine(dst, "Super Cars", fClean)).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Super Cars", fHack)).Should().BeFalse();
    }

    [Fact]
    public async Task SortProcessorStandalone_ShouldExecuteAndSupportProgressLogAndItemCallbacks()
    {
        var (src, dst) = CreateSampleEnv();
        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{publisher}/{title}/",
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig { ExcludeBadDumps = true }
        };

        var logs = new List<LogMessage>();
        var progressEvents = new List<SortProgress>();
        var itemsSeen = new List<ProcessItem>();

        var progress = new SynchronousProgress<SortProgress>(p => progressEvents.Add(p));
        var logProgress = new SynchronousProgress<LogMessage>(l => logs.Add(l));
        var itemProcessed = new SynchronousProgress<ProcessItem>(it => itemsSeen.Add(it));

        var result = await Processor.ExecuteAsync(
            config,
            isDryRun: true,
            progress: progress,
            logProgress: logProgress,
            itemProcessed: itemProcessed);

        result.Stats.TotalScanned.Should().Be(4);
        result.Stats.ValidCount.Should().Be(2);
        result.Stats.ExcludedCount.Should().Be(1);
        result.Stats.UnmatchedCount.Should().Be(1);
        result.Items.Should().HaveCount(4);
        itemsSeen.Should().HaveCount(4);
        progressEvents.Count(p => p.Message.Key == "progress.analyzing_file").Should().Be(4);
        logs.Should().Contain(l => l.Level == LogLevel.Info);
    }

    [Fact]
    public async Task ComponentsIsolation_ShouldWorkIndependently()
    {
        var (src, dst) = CreateSampleEnv();
        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{publisher}/{title}/",
            Filters = new FilterConfig { ExcludeBadDumps = true }
        };

        // 1. Isolated test of SourceScanner
        var scanner = new SourceScanner(config, Checksum, I18n, _services.OpticalCompanionService);
        var (entries, totalBytes) = scanner.Scan();
        entries.Should().HaveCount(4);
        totalBytes.Should().BeGreaterThan(0);

        // 2. Isolated test of EntryClassifier
        var crcCache = new CrcCacheService(CoreTestServices.IsolatedCrcCachePath);
        var classifier = new EntryClassifier(config, datCatalog: null, crcCache, Checksum, RomParser, Filter, I18n, _services.OpticalCompanionService);
        var items = await classifier.ClassifyAllAsync(entries);
        items.Should().HaveCount(4);
        items.Count(i => i.Status == FileStatus.Valid).Should().Be(2);
        items.Count(i => i.Status == FileStatus.Excluded).Should().Be(1);
        items.Count(i => i.Status == FileStatus.Unmatched).Should().Be(1);

        // 3. Isolated test of FileTransferExecutor (in dry run)
        var executor = new FileTransferExecutor(config, isDryRun: true, Organizer, Filter, I18n, _services.OpticalCompanionService, Checksum);
        var (ok, err) = await executor.PrepareDestinationAsync();
        ok.Should().BeTrue();
        err.Should().BeNull();

        var stats = new ProcessStats();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in items)
        {
            await executor.ProcessItemAsync(it, claimed, stats);
        }
        stats.ValidCount.Should().Be(2);
        stats.ExcludedCount.Should().Be(1);
        stats.UnmatchedCount.Should().Be(1);
    }

    [Fact]
    public async Task LanguageFilterWithout1G1RNoFallback_ShouldKeepAllReleasesInSelectedLanguagesAndExcludeOthers()
    {
        var src = _temp.CreateSubdirectory("src_lang_filter_nofb");
        var dst = _temp.CreateSubdirectory("dst_lang_filter_nofb");

        // Game 1: Two FR releases (different publishers) + one EN + one DE
        File.WriteAllText(Path.Combine(src, "Another World (1991)(Delphine)(FR)[!].adf"), "fr1");
        File.WriteAllText(Path.Combine(src, "Another World (1992)(U.S. Gold)(FR).adf"), "fr2");
        File.WriteAllText(Path.Combine(src, "Another World (1991)(Delphine)(GB).adf"), "en1");
        File.WriteAllText(Path.Combine(src, "Another World (1991)(Delphine)(DE).adf"), "de1");

        // Game 2: Only in Italian (IT) -> Must be excluded because fallback=False
        File.WriteAllText(Path.Combine(src, "Dylan Dog (1992)(Simulmondo)(IT).adf"), "it1");

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{title}/",
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                OneGameOneRom = false,
                FilterLanguages = true,
                PriorityLanguages = ["fr", "en"],
                FallbackUnmatchedLanguages = false
            }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);

        // Both FR and the GB of Another World are kept (3 files), DE and IT are excluded (2 files)
        result.Stats.ValidCount.Should().Be(3);
        result.Stats.ExcludedCount.Should().Be(2);

        File.Exists(Path.Combine(dst, "Another World", "Another World (1991)(Delphine)(FR)[!].adf")).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Another World", "Another World (1992)(U.S. Gold)(FR).adf")).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Another World", "Another World (1991)(Delphine)(GB).adf")).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Another World", "Another World (1991)(Delphine)(DE).adf")).Should().BeFalse();
        File.Exists(Path.Combine(dst, "Dylan Dog", "Dylan Dog (1992)(Simulmondo)(IT).adf")).Should().BeFalse();
    }

    [Fact]
    public async Task LanguageFilterWithout1G1RWithFallback_ShouldFallbackOnlyForGamesWithoutAnySelectedLanguage()
    {
        var src = _temp.CreateSubdirectory("src_lang_filter_fb");
        var dst = _temp.CreateSubdirectory("dst_lang_filter_fb");

        // Game 1: Has FR and DE -> FR kept, DE excluded (because FR exists)
        File.WriteAllText(Path.Combine(src, "Flashback (1992)(Delphine)(FR).adf"), "fr");
        File.WriteAllText(Path.Combine(src, "Flashback (1992)(Delphine)(DE).adf"), "de");

        // Game 2: Only IT and ES -> Both are kept thanks to fallback (without 1G1R)
        File.WriteAllText(Path.Combine(src, "Dylan Dog (1992)(Simulmondo)(IT).adf"), "it");
        File.WriteAllText(Path.Combine(src, "Dylan Dog (1992)(Simulmondo)(ES).adf"), "es");

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{title}/",
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                OneGameOneRom = false,
                FilterLanguages = true,
                PriorityLanguages = ["fr", "en"],
                FallbackUnmatchedLanguages = true
            }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);

        result.Stats.ValidCount.Should().Be(3); // Flashback FR + Dylan Dog IT + Dylan Dog ES
        result.Stats.ExcludedCount.Should().Be(1); // Flashback DE
    }

    [Fact]
    public async Task CountryPriorityTiebreakerLangFirst_ShouldBreakLanguageTieUsingCountryPriorityOrder()
    {
        var src = _temp.CreateSubdirectory("src_country_tiebreak");
        var dst = _temp.CreateSubdirectory("dst_country_tiebreak");

        File.WriteAllText(Path.Combine(src, "Sonic the Hedgehog (USA).md"), "usa");
        File.WriteAllText(Path.Combine(src, "Sonic the Hedgehog (Europe).md"), "eu");

        // Case 1: EU priority before US
        var configEu = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            NamingConvention = NamingConvention.NoIntro,
            Pattern = "{title}/",
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                RegionPriorityMode = RegionPriorityMode.LanguageFirst,
                PriorityLanguages = ["en"],
                PriorityCountries = ["EU", "US"],
                PreferredVideo = VideoPreference.None
            }
        };

        var resEu = await Processor.ExecuteAsync(configEu, isDryRun: true);
        var validEu = resEu.Items.Where(i => i.Status == FileStatus.Valid).ToList();
        validEu.Should().ContainSingle();
        validEu[0].DisplayName.Should().Be("Sonic the Hedgehog (Europe).md");

        // Case 2: US priority before EU
        var configUs = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            NamingConvention = NamingConvention.NoIntro,
            Pattern = "{title}/",
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                RegionPriorityMode = RegionPriorityMode.LanguageFirst,
                PriorityLanguages = ["en"],
                PriorityCountries = ["US", "EU"],
                PreferredVideo = VideoPreference.None
            }
        };

        var resUs = await Processor.ExecuteAsync(configUs, isDryRun: true);
        var validUs = resUs.Items.Where(i => i.Status == FileStatus.Valid).ToList();
        validUs.Should().ContainSingle();
        validUs[0].DisplayName.Should().Be("Sonic the Hedgehog (USA).md");
    }

    [Fact]
    public async Task CountryFirstPriorityMode_ShouldPrioritizeCountryRankBeforeLanguageRank()
    {
        var src = _temp.CreateSubdirectory("src_country_first");
        var dst = _temp.CreateSubdirectory("dst_country_first");

        // ROM 1: USA (en only)
        File.WriteAllText(Path.Combine(src, "Streets of Rage 2 (USA).md"), "usa");
        // ROM 2: Europe (En,Fr,De)
        File.WriteAllText(Path.Combine(src, "Streets of Rage 2 (Europe) (En,Fr,De).md"), "eu");

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            NamingConvention = NamingConvention.NoIntro,
            Pattern = "{title}/",
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                RegionPriorityMode = RegionPriorityMode.CountryFirst,
                PriorityCountries = ["US", "EU"],
                PriorityLanguages = ["fr", "en"],
                PreferredVideo = VideoPreference.None
            }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: true);
        var valid = result.Items.Where(i => i.Status == FileStatus.Valid).ToList();
        valid.Should().ContainSingle();
        valid[0].DisplayName.Should().Be("Streets of Rage 2 (USA).md");
    }

    [Fact]
    public async Task CountryFilterWithout1G1RWithAndWithoutFallback_ShouldFilterStrictlyOrFallbackPerGame()
    {
        var src = _temp.CreateSubdirectory("src_country_filter");
        var dst = _temp.CreateSubdirectory("dst_country_filter");

        // Game 1: Available in FR, EU, and JP
        File.WriteAllText(Path.Combine(src, "Castlevania (France).nes"), "fr");
        File.WriteAllText(Path.Combine(src, "Castlevania (Europe).nes"), "eu");
        File.WriteAllText(Path.Combine(src, "Castlevania (Japan).nes"), "jp");

        // Game 2: Japanese exclusive (JP only)
        File.WriteAllText(Path.Combine(src, "Mother (Japan).nes"), "mother_jp");

        // Case 1: Without fallback -> Castlevania FR and EU kept (2), Castlevania JP and Mother JP excluded (2)
        var configNoFb = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            NamingConvention = NamingConvention.NoIntro,
            Filters = new FilterConfig
            {
                OneGameOneRom = false,
                FilterLanguages = false,
                FilterCountries = true,
                PriorityCountries = ["FR", "EU"],
                FallbackUnmatchedCountries = false
            }
        };

        var resNoFb = await Processor.ExecuteAsync(configNoFb, isDryRun: true);
        resNoFb.Stats.ValidCount.Should().Be(2);
        resNoFb.Stats.ExcludedCount.Should().Be(2);

        // Case 2: With fallback -> Castlevania FR and EU kept, Castlevania JP excluded, but Mother JP kept (3 valid, 1 excluded)
        var configFb = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            NamingConvention = NamingConvention.NoIntro,
            Filters = new FilterConfig
            {
                OneGameOneRom = false,
                FilterLanguages = false,
                FilterCountries = true,
                PriorityCountries = ["FR", "EU"],
                FallbackUnmatchedCountries = true
            }
        };

        var resFb = await Processor.ExecuteAsync(configFb, isDryRun: true);
        resFb.Stats.ValidCount.Should().Be(3);
        resFb.Stats.ExcludedCount.Should().Be(1);
    }

    [Fact]
    public async Task LangFirstWithCountryFallbackAndStrictCountry_ShouldExcludeOrKeepWhenCountryNotInPriorityList()
    {
        var src = _temp.CreateSubdirectory("src_lang_country_strict");
        var dst = _temp.CreateSubdirectory("dst_lang_country_strict");

        // Game available only in USA version (implicit 'en' language, 'US' country)
        File.WriteAllText(Path.Combine(src, "Chrono Trigger (USA).sfc"), "usa");

        // If the user only wants countries [FR, EU] without country fallback (fallback_unmatched_countries=False)
        var configStrictCountry = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            NamingConvention = NamingConvention.NoIntro,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                RegionPriorityMode = RegionPriorityMode.LanguageFirst,
                FilterLanguages = true,
                PriorityLanguages = ["fr", "en"],
                FallbackUnmatchedLanguages = true,
                FilterCountries = true,
                PriorityCountries = ["FR", "EU"],
                FallbackUnmatchedCountries = false
            }
        };

        var resStrict = await Processor.ExecuteAsync(configStrictCountry, isDryRun: true);
        resStrict.Stats.ValidCount.Should().Be(0);
        resStrict.Stats.ExcludedCount.Should().Be(1);

        // With fallback_unmatched_countries=True -> kept!
        configStrictCountry.Filters.FallbackUnmatchedCountries = true;
        var resFb = await Processor.ExecuteAsync(configStrictCountry, isDryRun: true);
        resFb.Stats.ValidCount.Should().Be(1);
    }

    [Fact]
    public async Task AdvancedTitleNormalizationDeduplication_ShouldGroupAccentsRomanNumeralsAndConjunctionsWhileKeepingOriginalFolderName()
    {
        var src = _temp.CreateSubdirectory("src_adv_norm");
        var dst = _temp.CreateSubdirectory("dst_adv_norm");

        // 1. Accents: Pokémon (FR) vs Pokemon (US)
        File.WriteAllText(Path.Combine(src, "Pokémon - Version Rouge (France).gb"), "poke_fr");
        File.WriteAllText(Path.Combine(src, "Pokemon - Version Rouge (Europe) (En,Fr).gb"), "poke_eu");

        // 2. Roman vs Arabic numerals: Turrican II (FR) vs Turrican 2 (DE)
        File.WriteAllText(Path.Combine(src, "Turrican II (1991)(Rainbow Arts)(FR)[!].adf"), "tur2_fr");
        File.WriteAllText(Path.Combine(src, "Turrican 2 (1991)(Rainbow Arts)(DE).adf"), "tur2_de");

        // 3. Ampersand '&' vs 'and': Sonic & Knuckles vs Sonic and Knuckles
        File.WriteAllText(Path.Combine(src, "Sonic & Knuckles (World).md"), "sk_world");
        File.WriteAllText(Path.Combine(src, "Sonic and Knuckles (Japan).md"), "sk_jp");

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{title}/",
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                PriorityLanguages = ["fr", "en", "m"],
                PriorityCountries = ["FR", "EU", "WO", "US", "JP"],
                FallbackUnmatchedLanguages = true
            }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);

        // 3 distinct canonical games -> exactly 3 files kept and 3 duplicates eliminated
        result.Stats.ValidCount.Should().Be(3);
        result.Stats.ExcludedCount.Should().Be(3);

        // Verify that generated folder names keep original titles intact
        File.Exists(Path.Combine(dst, "Pokémon - Version Rouge", "Pokémon - Version Rouge (France).gb")).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Turrican II", "Turrican II (1991)(Rainbow Arts)(FR)[!].adf")).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Sonic & Knuckles", "Sonic & Knuckles (World).md")).Should().BeTrue();
    }

    [Fact]
    public async Task CountryTiebreakBeforeVideoAndMultiregionVideo_ShouldPrioritizeCountryRankAndSupportDualPalNtsc()
    {
        var src = _temp.CreateSubdirectory("src_country_video");
        var dst = _temp.CreateSubdirectory("dst_country_video");

        // Case 1: US (NTSC) vs EU (PAL), but user put US before EU in priority_countries
        // Even if preferred_video="pal", explicit country rank (US=0 < EU=1) must take precedence!
        File.WriteAllText(Path.Combine(src, "Contra III (USA).sfc"), "usa");
        File.WriteAllText(Path.Combine(src, "Contra III (Europe).sfc"), "eu");

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            NamingConvention = NamingConvention.NoIntro,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                RegionPriorityMode = RegionPriorityMode.LanguageFirst,
                PriorityLanguages = ["en"],
                PriorityCountries = ["US", "EU"],
                PreferredVideo = VideoPreference.Pal
            }
        };

        var res = await Processor.ExecuteAsync(config, isDryRun: true);
        var valid = res.Items.Where(i => i.Status == FileStatus.Valid).ToList();
        valid.Should().ContainSingle();
        valid[0].DisplayName.Should().Be("Contra III (USA).sfc");

        // Case 2: Multi-region ROM (USA, Europe) -> supports NTSC and PAL ("NTSC/PAL")
        var src2 = _temp.CreateSubdirectory("src_multireg");
        File.WriteAllText(Path.Combine(src2, "Sonic Spinball (USA, Europe).md"), "multi_reg");
        File.WriteAllText(Path.Combine(src2, "Sonic Spinball (USA).md"), "usa_only");

        var config2 = new SortConfig
        {
            SourceDir = src2,
            DestinationDir = dst,
            NamingConvention = NamingConvention.NoIntro,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                PriorityLanguages = ["en"],
                PriorityCountries = ["US", "EU"],
                PreferredVideo = VideoPreference.Pal
            }
        };

        var res2 = await Processor.ExecuteAsync(config2, isDryRun: true);
        var valid2 = res2.Items.Where(i => i.Status == FileStatus.Valid).ToList();
        valid2.Should().ContainSingle();
        // Both have country_rank=0 (thanks to US), but (USA, Europe) is PAL-compatible ("NTSC/PAL")!
        valid2[0].DisplayName.Should().Be("Sonic Spinball (USA, Europe).md");
    }

    [Fact]
    public async Task ExcludesExplicitForeignMultilingualRoms_ShouldExcludeAsiaJaKoWhenPriorityIsFrM()
    {
        var src = _temp.CreateSubdirectory("src_foreign_multi");
        var dst = _temp.CreateSubdirectory("dst_foreign_multi");

        File.WriteAllText(Path.Combine(src, "Exclusive RPG (Asia) (Ja,Ko).sfc"), "jako");
        File.WriteAllText(Path.Combine(src, "Futbol (Spain) (Es,Pt).sfc"), "espt");
        File.WriteAllText(Path.Combine(src, "Syndicate (1993)(Bullfrog)(M3).adf"), "tosec_m3");

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                PriorityLanguages = ["fr", "m"],
                FallbackUnmatchedLanguages = false
            }
        };

        var res = await Processor.ExecuteAsync(config, isDryRun: true);
        var validNames = res.Items.Where(i => i.Status == FileStatus.Valid).Select(i => i.DisplayName).ToList();
        validNames.Should().Equal("Syndicate (1993)(Bullfrog)(M3).adf");
    }

    [Fact]
    public async Task Nitro2DiskVs1DiskExtendedAdfAndRepacks_ShouldSelectComplete2DiskSetAndKeepWHDLoadArchiveWinner()
    {
        var src = _temp.CreateSubdirectory("src_nitro");
        var dst = _temp.CreateSubdirectory("dst_nitro");

        var fExt = "Nitro (1990)(Psygnosis)(M3)[cr CSL][extended adf].adf";
        var f1Disk = "Nitro (1990)(Psygnosis)(M3)[cr CSL].adf";
        var fD1 = "Nitro (1990)(Psygnosis)(M3)(Disk 1 of 2)[cr CSL].adf";
        var fD2 = "Nitro (1990)(Psygnosis)(M3)(Disk 2 of 2)[cr CSL].adf";

        foreach (var fname in new[] { fExt, f1Disk, fD1, fD2 })
        {
            File.WriteAllText(Path.Combine(src, fname), fname);
        }

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{title}/",
            SubfolderMultidisk = false,
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                ExcludeModifications = false,
                CompleteMultidiskSetsOnly = true,
                SmartMultidiskGrouping = true,
                OneGameOneRom = true,
                PriorityLanguages = ["fr", "m", "en"],
                FallbackUnmatchedLanguages = true
            }
        };

        var res = await Processor.ExecuteAsync(config, isDryRun: false);

        // The complete 2-disk set (Disk 1 of 2 + Disk 2 of 2) must win over 1-disk variants
        res.Stats.ValidCount.Should().Be(2);
        res.Stats.ExcludedCount.Should().Be(2);
        File.Exists(Path.Combine(dst, "Nitro", fD1)).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Nitro", fD2)).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Nitro", fExt)).Should().BeFalse();
        File.Exists(Path.Combine(dst, "Nitro", f1Disk)).Should().BeFalse();

        // Verify that a single-file WHDLoad archive (.lha) (category 'hd' != 'floppy')
        // is NOT penalized by is_complete_multidisk if 'hd' is preferred
        var fLha = "Nitro (1990)(Psygnosis)(M3).lha";
        File.WriteAllText(Path.Combine(src, fLha), "whdload");
        config.Filters.PreferredMediaType = MediaPreference.Hd;
        var resWHD = await Processor.ExecuteAsync(config, isDryRun: true);
        var validWHD = resWHD.Items.Where(i => i.Status == FileStatus.Valid).Select(i => i.DisplayName).ToList();
        validWHD.Should().Equal(fLha);
    }

    [Fact]
    public async Task CumulativeCleanlinessScoreAmongModifiedDumps_ShouldSelectLeastModifiedVariant()
    {
        var src = _temp.CreateSubdirectory("src_clean_cumul");
        var dst = _temp.CreateSubdirectory("dst_clean_cumul");

        var fHeavy = "Agony (1992)(Psygnosis)[cr2 FLT][t +3][f AGA].adf";
        var fMedium = "Agony (1992)(Psygnosis)[cr FLT][t +3].adf";
        var fLight = "Agony (1992)(Psygnosis)[cr FLT].adf";

        foreach (var fname in new[] { fHeavy, fMedium, fLight })
        {
            File.WriteAllText(Path.Combine(src, fname), fname);
        }

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Filters = new FilterConfig
            {
                ExcludeModifications = false,
                OneGameOneRom = true,
                PriorityLanguages = ["en"],
                FallbackUnmatchedLanguages = true
            }
        };

        var res = await Processor.ExecuteAsync(config, isDryRun: true);
        var valid = res.Items.Where(i => i.Status == FileStatus.Valid).Select(i => i.DisplayName).ToList();
        valid.Should().Equal(fLight);
    }

    [Fact]
    public async Task OriginalReleaseOverModernRereleaseAndExclusiveFallback_ShouldPreferOriginalAndKeepExclusiveOrExcludeIfFiltered()
    {
        var src = _temp.CreateSubdirectory("src_rerelease");
        var dst = _temp.CreateSubdirectory("dst_rerelease");

        // Game 1: Available in original edition AND in Virtual Console re-release (Rev 1)
        var fOrig = "Super Mario World (USA).sfc";
        var fVc = "Super Mario World (USA) (Rev 1) (Virtual Console).sfc";
        // Game 2: Modern exclusive (only exists in Classic Mini)
        var fSf2 = "Star Fox 2 (USA, Europe) (Classic Mini, Switch Online).sfc";

        foreach (var fname in new[] { fOrig, fVc, fSf2 })
        {
            File.WriteAllText(Path.Combine(src, fname), fname);
        }

        // Case 1: NoIntroExcludeRereleases = False (default) + 1G1R
        // Original Super Mario World wins over Virtual Console, and Star Fox 2 is kept
        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            NamingConvention = NamingConvention.NoIntro,
            Filters = new FilterConfig
            {
                NoIntroExcludeRereleases = false,
                OneGameOneRom = true,
                PriorityLanguages = ["en"],
                PriorityCountries = ["US", "EU"]
            }
        };

        var res = await Processor.ExecuteAsync(config, isDryRun: true);
        var valid = res.Items.Where(i => i.Status == FileStatus.Valid).Select(i => i.DisplayName).OrderBy(x => x).ToList();
        valid.Should().Equal([fSf2, fOrig]);

        // Verify that the elimination reason for the Virtual Console version mentions "parution originale"
        var vcItem = res.Items.Single(i => i.DisplayName == fVc);
        vcItem.Status.Should().Be(FileStatus.Excluded);
        I18n.Format(vcItem.StatusReason).Should().Contain("parution originale");

        // Case 2: NoIntroExcludeRereleases = True -> Star Fox 2 and Super Mario World VC are excluded during initial filtering
        config.Filters.NoIntroExcludeRereleases = true;
        var resStrict = await Processor.ExecuteAsync(config, isDryRun: true);
        var validStrict = resStrict.Items.Where(i => i.Status == FileStatus.Valid).Select(i => i.DisplayName).ToList();
        validStrict.Should().Equal(fOrig);
    }

    [Fact]
    public async Task Manual1G1RWinnerOverrideAndRecalc_ShouldPromoteForcedReleaseEvenIfInitiallyExcludedAndRecalculateStats()
    {
        var src = _temp.CreateSubdirectory("src_manual_1g1r");
        var dst = _temp.CreateSubdirectory("dst_manual_1g1r");

        // Single-disk game: FR (auto winner) vs US (auto loser) vs [cr FLT] (excluded by crack filter)
        var fFr = "Flashback (1992)(Delphine)(FR)[!].adf";
        var fUs = "Flashback (1992)(Delphine)(US)[!].adf";
        var fCr = "Flashback (1992)(Delphine)(FR)[cr FLT].adf";

        // Multi-disk game: FR (2 disks) vs DE (2 disks)
        var mFr1 = "Monkey Island (1990)(Lucasfilm)(FR)(Disk 1 of 2).adf";
        var mFr2 = "Monkey Island (1990)(Lucasfilm)(FR)(Disk 2 of 2).adf";
        var mDe1 = "Monkey Island (1990)(Lucasfilm)(DE)(Disk 1 of 2).adf";
        var mDe2 = "Monkey Island (1990)(Lucasfilm)(DE)(Disk 2 of 2).adf";

        foreach (var fname in new[] { fFr, fUs, fCr, mFr1, mFr2, mDe1, mDe2 })
        {
            File.WriteAllText(Path.Combine(src, fname), fname);
        }

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Filters = new FilterConfig
            {
                ExcludeModifications = true,
                OneGameOneRom = true,
                PriorityLanguages = ["fr", "en", "de"]
            }
        };

        var res = await Processor.ExecuteAsync(config, isDryRun: true);
        var validAuto = res.Items.Where(i => i.Status == FileStatus.Valid).Select(i => i.DisplayName).ToHashSet();
        validAuto.Should().BeEquivalentTo([fFr, mFr1, mFr2]);

        // 1. Manually force the [cr FLT] version for Flashback (even though excluded by ExcludeModifications=True)
        var flashbackItems = res.Items.Where(i => i.Parsed.Title == "Flashback").ToList();
        var gkeyFb = Filter.SerializeCanonicalGameKey(flashbackItems[0].Parsed);
        config.Filters.Manual1G1RWinners[gkeyFb] = fCr;

        Processor.RecalculateGameItems(flashbackItems, config);
        var fbValid = flashbackItems.Where(i => i.Status == FileStatus.Valid).Select(i => i.DisplayName).ToList();
        fbValid.Should().Equal(fCr);
        var fbWinnerItem = flashbackItems.Single(i => i.DisplayName == fCr);
        fbWinnerItem.Is1G1RWinner.Should().BeTrue();

        var fbFrItem = flashbackItems.Single(i => i.DisplayName == fFr);
        fbFrItem.Status.Should().Be(FileStatus.Excluded);
        I18n.Format(fbFrItem.StatusReason).Should().Contain("choix manuel de l'utilisateur");

        // 2. Manually force Monkey Island DE Disk 1 -> both DE disks (Disk 1 & Disk 2) must win!
        var monkeyItems = res.Items.Where(i => i.Parsed.Title == "Monkey Island").ToList();
        var gkeyMi = Filter.SerializeCanonicalGameKey(monkeyItems[0].Parsed);
        config.Filters.Manual1G1RWinners[gkeyMi] = mDe1;

        Processor.RecalculateGameItems(monkeyItems, config);
        var miValid = monkeyItems.Where(i => i.Status == FileStatus.Valid).Select(i => i.DisplayName).ToHashSet();
        miValid.Should().BeEquivalentTo([mDe1, mDe2]);

        // 3. Verify that RecomputeStats properly updates global counters
        var newStats = Processor.RecomputeStats(res.Items, res.Stats);
        newStats.ValidCount.Should().Be(3); // 1 Flashback [cr] + 2 Monkey Island DE
        newStats.ExcludedCount.Should().Be(4); // 2 Flashback + 2 Monkey Island FR
    }

    [Fact]
    public async Task IntraFamilyMediaFidelityIpfChdTzx_ShouldSelectHighestFidelityFormatWithinEachFamily()
    {
        var src = _temp.CreateSubdirectory("src_fidelity");
        var dst = _temp.CreateSubdirectory("dst_fidelity");

        // Game 1 (Floppy): .ipf vs .adf vs .dms -> .ipf must win
        File.WriteAllText(Path.Combine(src, "Turrican (1990)(Rainbow Arts)(EU).dms"), "dms");
        File.WriteAllText(Path.Combine(src, "Turrican (1990)(Rainbow Arts)(EU).adf"), "adf");
        File.WriteAllText(Path.Combine(src, "Turrican (1990)(Rainbow Arts)(EU).ipf"), "ipf");

        // Game 2 (Optical): .chd vs .iso -> .chd must win
        File.WriteAllText(Path.Combine(src, "Ridge Racer (Europe).iso"), "iso");
        File.WriteAllBytes(Path.Combine(src, "Ridge Racer (Europe).chd"), ChdTestFile.Create("Ridge Racer (Europe)"));

        // Game 3 (Tape): .tzx vs .tap vs .wav -> .tzx must win
        File.WriteAllText(Path.Combine(src, "Target Renegade (1988)(Imagine)(GB).wav"), "wav");
        File.WriteAllText(Path.Combine(src, "Target Renegade (1988)(Imagine)(GB).tap"), "tap");
        File.WriteAllText(Path.Combine(src, "Target Renegade (1988)(Imagine)(GB).tzx"), "tzx");

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                PriorityLanguages = ["en"],
                PriorityCountries = ["EU", "GB"],
                FallbackUnmatchedLanguages = true
            }
        };

        var res = await Processor.ExecuteAsync(config, isDryRun: true);
        var validNames = res.Items.Where(i => i.Status == FileStatus.Valid).Select(i => i.DisplayName).ToHashSet();
        validNames.Should().BeEquivalentTo(
        [
            "Turrican (1990)(Rainbow Arts)(EU).ipf",
            "Ridge Racer (Europe).chd",
            "Target Renegade (1988)(Imagine)(GB).tzx"
        ]);

        var adfItem = res.Items.Single(i => i.DisplayName == "Turrican (1990)(Rainbow Arts)(EU).adf");
        adfItem.Status.Should().Be(FileStatus.Excluded);
        I18n.Format(adfItem.StatusReason).Should().Contain("format .ipf préféré à .adf");
    }

    // =========================================================================
    // .m3u playlist generation tests
    // =========================================================================

    [Fact]
    public async Task GenerateM3uParentMode_ShouldPlaceM3uInParentDirPointingToSubfolderDisks()
    {
        var src = _temp.CreateSubdirectory("src_m3u_parent");
        var dst = _temp.CreateSubdirectory("dst_m3u_parent");

        var d1 = "Secret of Monkey Island, The (1990)(Lucasfilm Games)(FR)(Disk 1 of 3).adf";
        var d2 = "Secret of Monkey Island, The (1990)(Lucasfilm Games)(FR)(Disk 2 of 3).adf";
        var d3 = "Secret of Monkey Island, The (1990)(Lucasfilm Games)(FR)(Disk 3 of 3).adf";

        // Created out of order to verify sorting by disk number
        foreach (var fname in new[] { d3, d1, d2 })
        {
            File.WriteAllText(Path.Combine(src, fname), fname);
        }

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{publisher}/",
            RestoreArticles = false,
            SubfolderMultidisk = true,
            GenerateM3u = true,
            M3uPlacement = M3uPlacementMode.Parent,
            TransferMode = TransferMode.Copy
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);
        result.Stats.ValidCount.Should().Be(3);

        var pubDir = Path.Combine(dst, "Lucasfilm Games");
        var gameSub = Path.Combine(pubDir, "Secret of Monkey Island, The");
        Directory.Exists(gameSub).Should().BeTrue();

        var m3uPath = Path.Combine(pubDir, "Secret of Monkey Island, The (1990)(Lucasfilm Games)(FR).m3u");
        File.Exists(m3uPath).Should().BeTrue();

        var lines = (await File.ReadAllLinesAsync(m3uPath, Encoding.UTF8))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
        lines.Should().Equal(
        [
            $"Secret of Monkey Island, The/{d1}",
            $"Secret of Monkey Island, The/{d2}",
            $"Secret of Monkey Island, The/{d3}"
        ]);
    }

    [Fact]
    public async Task GenerateM3uParentHiddenMode_ShouldPrefixSubfolderWithDotAndWriteRelativePaths()
    {
        var src = _temp.CreateSubdirectory("src_m3u_hidden");
        var dst = _temp.CreateSubdirectory("dst_m3u_hidden");

        var d1 = "Metal Gear Solid (France) (Disc 1).chd";
        var d2 = "Metal Gear Solid (France) (Disc 2).chd";
        foreach (var fname in new[] { d2, d1 })
        {
            File.WriteAllBytes(Path.Combine(src, fname), ChdTestFile.Create(fname));
        }

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            NamingConvention = NamingConvention.Redump,
            Pattern = string.Empty,
            SubfolderMultidisk = true,
            GenerateM3u = true,
            M3uPlacement = M3uPlacementMode.ParentHidden,
            TransferMode = TransferMode.Copy
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);
        result.Stats.ValidCount.Should().Be(2);

        var hiddenSub = Path.Combine(dst, ".Metal Gear Solid");
        Directory.Exists(hiddenSub).Should().BeTrue();
        File.Exists(Path.Combine(hiddenSub, d1)).Should().BeTrue();
        File.Exists(Path.Combine(hiddenSub, d2)).Should().BeTrue();

        var m3uPath = Path.Combine(dst, "Metal Gear Solid (France).m3u");
        File.Exists(m3uPath).Should().BeTrue();

        var lines = (await File.ReadAllLinesAsync(m3uPath, Encoding.UTF8))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
        lines.Should().Equal(
        [
            $".Metal Gear Solid/{d1}",
            $".Metal Gear Solid/{d2}"
        ]);
    }

    [Fact]
    public async Task GenerateM3uSubfolderMode_ShouldPlaceM3uInsideGameSubfolderWithDirectFilenames()
    {
        var src = _temp.CreateSubdirectory("src_m3u_sub");
        var dst = _temp.CreateSubdirectory("dst_m3u_sub");

        var d1 = "Final Fantasy VII (France) (Disc 1).chd";
        var d2 = "Final Fantasy VII (France) (Disc 2).chd";
        var d3 = "Final Fantasy VII (France) (Disc 3).chd";
        foreach (var fname in new[] { d3, d1, d2 })
        {
            File.WriteAllBytes(Path.Combine(src, fname), ChdTestFile.Create(fname));
        }

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            NamingConvention = NamingConvention.Redump,
            Pattern = "{country}/",
            SubfolderMultidisk = true,
            GenerateM3u = true,
            M3uPlacement = M3uPlacementMode.Subfolder,
            TransferMode = TransferMode.Copy
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);
        result.Stats.ValidCount.Should().Be(3);

        var gameSub = Path.Combine(dst, "FR", "Final Fantasy VII");
        var m3uPath = Path.Combine(gameSub, "Final Fantasy VII (France).m3u");
        File.Exists(m3uPath).Should().BeTrue();

        var lines = (await File.ReadAllLinesAsync(m3uPath, Encoding.UTF8))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
        lines.Should().Equal([d1, d2, d3]);
    }

    [Fact]
    public async Task GenerateM3uFlatMode_WithoutSubfolderMultidisk_ShouldWriteDirectFilenamesBesideDisks()
    {
        var src = _temp.CreateSubdirectory("src_m3u_flat");
        var dst = _temp.CreateSubdirectory("dst_m3u_flat");

        var d1 = "Lotus II (1991)(Gremlin)(Disk 1 of 2).adf";
        var d2 = "Lotus II (1991)(Gremlin)(Disk 2 of 2).adf";
        foreach (var fname in new[] { d2, d1 })
        {
            File.WriteAllText(Path.Combine(src, fname), fname);
        }

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = string.Empty,
            SubfolderMultidisk = false,
            GenerateM3u = true,
            M3uPlacement = M3uPlacementMode.ParentHidden, // ignored because SubfolderMultidisk=false
            TransferMode = TransferMode.Copy
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);
        result.Stats.ValidCount.Should().Be(2);

        var m3uPath = Path.Combine(dst, "Lotus II (1991)(Gremlin).m3u");
        File.Exists(m3uPath).Should().BeTrue();

        var lines = (await File.ReadAllLinesAsync(m3uPath, Encoding.UTF8))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
        lines.Should().Equal([d1, d2]);
    }

    [Fact]
    public async Task GenerateM3uSingleDiscNotCreated_ShouldNotCreateM3uForSingleDiskGame()
    {
        var src = _temp.CreateSubdirectory("src_m3u_single");
        var dst = _temp.CreateSubdirectory("dst_m3u_single");

        var single = "Another World (1991)(Delphine)(FR)[!].adf";
        File.WriteAllText(Path.Combine(src, single), single);

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = string.Empty,
            SubfolderMultidisk = true,
            GenerateM3u = true,
            TransferMode = TransferMode.Copy
        };

        await Processor.ExecuteAsync(config, isDryRun: false);
        Directory.EnumerateFiles(dst, "*.m3u", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateM3uDryRun_ShouldLogM3uCreationWithoutWritingFileOnDisk()
    {
        var src = _temp.CreateSubdirectory("src_m3u_dry");
        var dst = _temp.CreateSubdirectory("dst_m3u_dry");

        var d1 = "Flashback (1992)(Delphine)(FR)(Disk 1 of 4).adf";
        var d2 = "Flashback (1992)(Delphine)(FR)(Disk 2 of 4).adf";
        var d3 = "Flashback (1992)(Delphine)(FR)(Disk 3 of 4).adf";
        var d4 = "Flashback (1992)(Delphine)(FR)(Disk 4 of 4).adf";
        foreach (var fname in new[] { d1, d2, d3, d4 })
        {
            File.WriteAllText(Path.Combine(src, fname), fname);
        }

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = string.Empty,
            SubfolderMultidisk = true,
            GenerateM3u = true,
            M3uPlacement = M3uPlacementMode.ParentHidden,
            TransferMode = TransferMode.Copy
        };

        var logs = new List<LogMessage>();
        var logProgress = new SynchronousProgress<LogMessage>(l => logs.Add(l));

        await Processor.ExecuteAsync(config, isDryRun: true, logProgress: logProgress);

        Directory.EnumerateFileSystemEntries(dst).Should().BeEmpty();
        logs.Should().Contain(l => I18n.Format(l.Message).Contains(".m3u"));
    }

    [Fact]
    public async Task GenerateM3uOpticalMultitrackCuesOnly_ShouldReferenceOnlyCueFilesAndExcludeBinTracks()
    {
        var src = _temp.CreateSubdirectory("src_m3u_cue_bin");
        var dst = _temp.CreateSubdirectory("dst_m3u_cue_bin");

        var files = new[]
        {
            "Resident Evil 2 (USA) (Disc 1).cue",
            "Resident Evil 2 (USA) (Disc 1) (Track 1).bin",
            "Resident Evil 2 (USA) (Disc 1) (Track 2).bin",
            "Resident Evil 2 (USA) (Disc 2).cue",
            "Resident Evil 2 (USA) (Disc 2) (Track 1).bin",
            "Resident Evil 2 (USA) (Disc 2) (Track 2).bin"
        };

        foreach (var fname in files)
        {
            File.WriteAllText(Path.Combine(src, fname), fname);
        }

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            NamingConvention = NamingConvention.Redump,
            Pattern = string.Empty,
            SubfolderMultidisk = true,
            GenerateM3u = true,
            M3uPlacement = M3uPlacementMode.Parent,
            TransferMode = TransferMode.Copy
        };

        await Processor.ExecuteAsync(config, isDryRun: false);

        var m3uPath = Path.Combine(dst, "Resident Evil 2 (USA).m3u");
        File.Exists(m3uPath).Should().BeTrue();

        var lines = (await File.ReadAllLinesAsync(m3uPath, Encoding.UTF8))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
        lines.Should().Equal(
        [
            "Resident Evil 2/Resident Evil 2 (USA) (Disc 1).cue",
            "Resident Evil 2/Resident Evil 2 (USA) (Disc 2).cue"
        ]);
    }

    [Fact]
    public async Task GenerateM3uSmartMultidisk3Disks_PuttyCrackedDisk1WithCleanDataDisks_ShouldIncludeAll3DisksInSingleM3u()
    {
        var src = _temp.CreateSubdirectory("src_m3u_putty");
        var dst = _temp.CreateSubdirectory("dst_m3u_putty");

        var d1 = "Putty (1992)(System 3)(Disk 1 of 3)[cr CSL].adf";
        var d2 = "Putty (1992)(System 3)(Disk 2 of 3).adf";
        var d3 = "Putty (1992)(System 3)(Disk 3 of 3).adf";

        foreach (var fname in new[] { d1, d2, d3 })
        {
            File.WriteAllText(Path.Combine(src, fname), fname);
        }

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{letter}",
            RestoreArticles = true,
            SubfolderMultidisk = true,
            GenerateM3u = true,
            M3uPlacement = M3uPlacementMode.Parent,
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                ExcludeModifications = true,
                IgnoreModificationsInPriority = true,
                CompleteMultidiskSetsOnly = true,
                SmartMultidiskGrouping = true,
                OneGameOneRom = true,
                FilterLanguages = true,
                FallbackUnmatchedLanguages = false,
                FilterCountries = true,
                FallbackUnmatchedCountries = true,
                PriorityLanguages = ["fr", "m", "en"],
                PriorityCountries = ["FR", "EU", "WO", "US", "GB", "CA", "AU", "JP"]
            }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);
        result.Stats.ValidCount.Should().Be(3);

        var pDir = Path.Combine(dst, "P");
        var m3uFiles = Directory.GetFiles(pDir, "*.m3u", SearchOption.AllDirectories);
        m3uFiles.Should().ContainSingle();

        var expectedM3u = Path.Combine(pDir, "Putty (1992)(System 3)[cr CSL].m3u");
        File.Exists(expectedM3u).Should().BeTrue();

        var lines = (await File.ReadAllLinesAsync(expectedM3u, Encoding.UTF8))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
        lines.Should().Equal(
        [
            $"Putty/{d1}",
            $"Putty/{d2}",
            $"Putty/{d3}"
        ]);
    }

    [Fact]
    public async Task GenerateM3uSmartMultidisk2DisksAndSecondaryMods_ShouldCreateCompleteM3uPlaylistsForAllSets()
    {
        var src = _temp.CreateSubdirectory("src_m3u_smart_2disks");
        var dst = _temp.CreateSubdirectory("dst_m3u_smart_2disks");

        // Game 1 (2 disks): Disk 1 cracked [cr HLM] + Disk 2 clean
        var pd1 = "Power Drive (1994)(U.S. Gold)(M5)(Disk 1 of 2)[cr HLM].adf";
        var pd2 = "Power Drive (1994)(U.S. Gold)(M5)(Disk 2 of 2).adf";

        // Game 2 (2 disks): Same crack group [cr NMS], but secondary tag [f crack NMS] only on Disk 1
        var psy1 = "Psyborg (1992)(Loriciel)(Disk 1 of 2)[cr NMS][f crack NMS].adf";
        var psy2 = "Psyborg (1992)(Loriciel)(Disk 2 of 2)[cr NMS].adf";

        foreach (var fname in new[] { pd1, pd2, psy1, psy2 })
        {
            File.WriteAllText(Path.Combine(src, fname), fname);
        }

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{letter}",
            SubfolderMultidisk = true,
            GenerateM3u = true,
            M3uPlacement = M3uPlacementMode.Parent,
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                ExcludeModifications = true,
                IgnoreModificationsInPriority = true,
                CompleteMultidiskSetsOnly = true,
                SmartMultidiskGrouping = true,
                OneGameOneRom = true,
                PriorityLanguages = ["fr", "m", "en"],
                FallbackUnmatchedLanguages = true
            }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);
        result.Stats.ValidCount.Should().Be(4);

        var pDir = Path.Combine(dst, "P");
        var pdM3u = Path.Combine(pDir, "Power Drive (1994)(U.S. Gold)(M5)[cr HLM].m3u");
        var psyM3u = Path.Combine(pDir, "Psyborg (1992)(Loriciel)[cr NMS][f crack NMS].m3u");

        File.Exists(pdM3u).Should().BeTrue();
        File.Exists(psyM3u).Should().BeTrue();

        var pdLines = (await File.ReadAllLinesAsync(pdM3u, Encoding.UTF8))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
        pdLines.Should().Equal(
        [
            $"Power Drive/{pd1}",
            $"Power Drive/{pd2}"
        ]);

        var psyLines = (await File.ReadAllLinesAsync(psyM3u, Encoding.UTF8))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
        psyLines.Should().Equal(
        [
            $"Psyborg/{psy1}",
            $"Psyborg/{psy2}"
        ]);
    }

    [Fact]
    public async Task Processor1G1RRPrefixedVersions_3DConstructionKitII_ShouldGroupUnderSameCanonicalTitleAndKeepOnlyBestRelease()
    {
        var src = _temp.CreateSubdirectory("src_3dck2");
        var dst = _temp.CreateSubdirectory("dst_3dck2");

        var r201d1 = "3D Construction Kit II r2.01 (1992)(Domark)(Disk 1 of 2)[h Ministry][construction kit].adf";
        var r201d2 = "3D Construction Kit II r2.01 (1992)(Domark)(Disk 2 of 2)[construction kit].adf";
        var r203d1 = "3D Construction Kit II r2.03 (1992)(Domark)(FR)(Disk 1 of 2)[construction kit].adf";
        var r203d2 = "3D Construction Kit II r2.03 (1992)(Domark)(FR)(Disk 2 of 2)[construction kit].adf";

        foreach (var fname in new[] { r201d1, r201d2, r203d1, r203d2 })
        {
            File.WriteAllText(Path.Combine(src, fname), fname);
        }

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{letter}/",
            RestoreArticles = true,
            SubfolderMultidisk = true,
            TransferMode = TransferMode.Copy,
            Filters = new FilterConfig
            {
                ExcludeModifications = true,
                IgnoreModificationsInPriority = true,
                CompleteMultidiskSetsOnly = true,
                SmartMultidiskGrouping = true,
                OneGameOneRom = true,
                FilterLanguages = true,
                FallbackUnmatchedLanguages = false,
                FilterCountries = true,
                FallbackUnmatchedCountries = true,
                PriorityLanguages = ["fr", "m", "en"],
                PriorityCountries = ["FR", "EU", "WO", "US", "GB", "CA", "AU", "JP"],
                RegionPriorityMode = RegionPriorityMode.LanguageFirst,
                PreferredVideo = VideoPreference.Pal,
                PreferMonolingual = true
            }
        };

        var result = await Processor.ExecuteAsync(config, isDryRun: false);
        result.Stats.ValidCount.Should().Be(2);
        result.Stats.ExcludedCount.Should().Be(2);

        var itemsByName = result.Items.ToDictionary(it => it.DisplayName);
        itemsByName[r203d1].Status.Should().Be(FileStatus.Valid);
        itemsByName[r203d2].Status.Should().Be(FileStatus.Valid);
        itemsByName[r201d1].Status.Should().Be(FileStatus.Excluded);
        itemsByName[r201d2].Status.Should().Be(FileStatus.Excluded);

        var gameDir = Path.Combine(dst, "#", "3D Construction Kit II");
        File.Exists(Path.Combine(gameDir, r203d1)).Should().BeTrue();
        File.Exists(Path.Combine(gameDir, r203d2)).Should().BeTrue();
        File.Exists(Path.Combine(gameDir, r201d1)).Should().BeFalse();
        File.Exists(Path.Combine(gameDir, r201d2)).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldUseInjectedCrcCache()
    {
        var trackingCache = new TrackingCrcCache();
        var processorWithMockCache = new SortProcessorService(
            _services.ChecksumService,
            _services.DatService,
            _services.NoIntroParser,
            _services.FilterService,
            _services.Organizer,
            _services.I18n,
            _services.OpticalCompanionService,
            _services.MameParser,
            trackingCache);

        processorWithMockCache.CrcCache.Should().BeSameAs(trackingCache);

        var src = _temp.CreateSubdirectory("src_injected_cache");
        var dst = _temp.CreateSubdirectory("dst_injected_cache");
        var filePath = Path.Combine(src, "TestGame (Europe).bin");
        File.WriteAllBytes(filePath, [0x01, 0x02, 0x03]);

        var datPath = Path.Combine(src, "test.dat");
        File.WriteAllText(datPath, """
            clrmamepro ( name "Test DAT" )
            game ( name "TestGame" rom ( name "TestGame (Europe).bin" size 3 crc 12345678 ) )
            """);

        trackingCache.Set(filePath, "12345678");

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            DatFile = datPath,
            NamingConvention = NamingConvention.NoIntro
        };

        var result = await processorWithMockCache.ExecuteAsync(config, isDryRun: true);

        trackingCache.QueriedPaths.Should().Contain(filePath);
        trackingCache.SaveCalled.Should().BeTrue();
    }

    private sealed class TrackingCrcCache : ICrcCacheService
    {
        public string CachePath => "tracking_crc_cache.json";
        public IHeaderCache Headers { get; } = new UncachedHeaders();
        public bool SaveCalled { get; private set; }
        public List<string> QueriedPaths { get; } = [];
        public Dictionary<string, string> Cache { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken cancellationToken = default)
        {
            SaveCalled = true;
            return Task.CompletedTask;
        }

        public string? Get(string path)
        {
            QueriedPaths.Add(path);
            return Cache.TryGetValue(path, out var crc) ? crc : null;
        }

        public void Set(string path, string crc) => Cache[path] = crc;

        public string? GetSha1(string path) => null;

        public void SetSha1(string path, string crc, string sha1) => Cache[path] = crc;

        public void Rename(string oldPath, string newPath)
        {
            if (Cache.Remove(oldPath, out var crc))
            {
                Cache[newPath] = crc;
            }
        }
    }

    // Reads every header: the tracking cache only tracks the CRC32
    private sealed class UncachedHeaders : IHeaderCache
    {
        public IReadOnlyList<ArchiveMember> GetArchiveMembers(string archivePath, IChecksumService checksumService) =>
            checksumService.GetArchiveMembers(archivePath);

        public ChdInfo ReadChdInfo(string path, IChecksumService checksumService) => checksumService.ReadChdInfo(path);

        public IReadOnlyList<DamagedArchiveMember> FindDamagedMembers(string archivePath, IChecksumService checksumService, CancellationToken cancellationToken = default) =>
            checksumService.FindDamagedMembers(archivePath, cancellationToken);
    }

    /// <summary>
    /// Synchronous implementation of <see cref="IProgress{T}"/> for unit tests to avoid
    /// asynchronous post latency on <see cref="SynchronizationContext"/> / ThreadPool.
    /// </summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
