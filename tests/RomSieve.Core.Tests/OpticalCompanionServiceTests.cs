using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

public sealed class OpticalCompanionServiceTests
{
    private readonly CoreTestServices _services = new();
    private readonly OpticalCompanionService _opticalService = new();

    [Theory]
    [InlineData("game.cue", true)]
    [InlineData("GAME.CUE", true)]
    [InlineData("disc.gdi", true)]
    [InlineData("clone.ccd", true)]
    [InlineData("track01.bin", false)]
    [InlineData("game.iso", false)]
    [InlineData("archive.zip", false)]
    [InlineData("", false)]
    public void IsDescriptor_ShouldIdentifyOpticalDescriptorsCorrectly(string filename, bool expected)
    {
        _opticalService.IsDescriptor(filename).Should().Be(expected);
    }

    [Fact]
    public void RewriteReferencedFiles_CueSheet_ShouldRenameQuotedAndUnquotedReferencesAndKeepLineEndings()
    {
        var content = "REM Game.bin\r\nFILE \"sub\\Game (Track 01).bin\" BINARY\r\n  TRACK 01 MODE1/2352\r\nFILE track02.bin BINARY\r\nFILE \"other.bin\" BINARY\r\n";
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Game (Track 01).bin"] = "Game (Track 01)_01.bin",
            ["TRACK02.bin"] = "track02_01.bin"
        };

        var rewritten = _opticalService.RewriteReferencedFiles(content, ".cue", renames);

        rewritten.Should().Be("REM Game.bin\r\nFILE \"sub\\Game (Track 01)_01.bin\" BINARY\r\n  TRACK 01 MODE1/2352\r\nFILE track02_01.bin BINARY\r\nFILE \"other.bin\" BINARY\r\n");
    }

    [Fact]
    public void RewriteReferencedFiles_Gdi_ShouldRenameTrackFilesButNotTheTrackCount()
    {
        var content = "2\n1 0 4 2352 track01.bin 0\n2 600 0 2352 \"track 02.raw\" 0\n";
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["track01.bin"] = "track01_01.bin",
            ["track 02.raw"] = "track 02_01.raw"
        };

        _opticalService.RewriteReferencedFiles(content, ".gdi", renames)
            .Should().Be("2\n1 0 4 2352 track01_01.bin 0\n2 600 0 2352 \"track 02_01.raw\" 0\n");
    }

    [Fact]
    public void RewriteReferencedFiles_UnsupportedFormat_ShouldReturnContentUnchanged()
    {
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["game.img"] = "game_01.img" };
        _opticalService.RewriteReferencedFiles("[CloneCD]\r\nVersion=3\r\n", ".ccd", renames)
            .Should().Be("[CloneCD]\r\nVersion=3\r\n");
    }

    [Fact]
    public void ParseReferencedFilesFromContent_CueSheet_ShouldExtractQuotedAndUnquotedFiles()
    {
        var cueContent = """
            REM COMMENT LINE
            CATALOG 1234567890123
            FILE "Dennis (Europe) (Track 01).bin" BINARY
              TRACK 01 MODE1/2352
                INDEX 01 00:00:00
            FILE 'Dennis (Europe) (Track 02).bin' BINARY
              TRACK 02 AUDIO
                INDEX 00 00:00:00
                INDEX 01 00:02:00
            FILE Dennis_Track03.bin BINARY
              TRACK 03 AUDIO
                INDEX 01 00:03:00
            FILE "subfolder\track04.iso" BINARY
              TRACK 04 MODE2/2352
                INDEX 01 00:04:00
            """;

        var files = _opticalService.ParseReferencedFilesFromContent(cueContent, ".cue");

        files.Should().HaveCount(4);
        files[0].Should().Be("Dennis (Europe) (Track 01).bin");
        files[1].Should().Be("Dennis (Europe) (Track 02).bin");
        files[2].Should().Be("Dennis_Track03.bin");
        files[3].Should().Be(@"subfolder\track04.iso");
    }

    [Fact]
    public void ParseReferencedFilesFromContent_Gdi_ShouldExtractTracks()
    {
        var gdiContent = """
            3
            1 0 4 2352 track01.bin 0
            2 450 0 2352 "track02.raw" 0
            3 45000 4 2352 track03.bin 0
            """;

        var files = _opticalService.ParseReferencedFilesFromContent(gdiContent, ".gdi");

        files.Should().HaveCount(3);
        files[0].Should().Be("track01.bin");
        files[1].Should().Be("track02.raw");
        files[2].Should().Be("track03.bin");
    }

    [Fact]
    public void ResolveCompanionFilesOnDisk_ShouldResolveExistingBinTracksAndHandleCaseInsensitivity()
    {
        using var tmp = new TempDirectoryFixture();
        var dir = tmp.CreateSubdirectory("optical_test");

        var cuePath = Path.Combine(dir, "Game (Europe).cue");
        File.WriteAllText(cuePath, """
            FILE "Game (Europe) (Track 01).bin" BINARY
              TRACK 01 MODE1/2352
                INDEX 01 00:00:00
            FILE "Game (Europe) (Track 02).BIN" BINARY
              TRACK 02 AUDIO
                INDEX 01 00:02:00
            FILE "nonexistent_track.bin" BINARY
            """);

        // Real files on disk (track 02 is lowercase on disk to test case-insensitive resolution)
        var track1 = Path.Combine(dir, "Game (Europe) (Track 01).bin");
        var track2 = Path.Combine(dir, "game (europe) (track 02).bin");
        File.WriteAllBytes(track1, "TRACK1"u8.ToArray());
        File.WriteAllBytes(track2, "TRACK2"u8.ToArray());

        var resolved = _opticalService.ResolveCompanionFilesOnDisk(cuePath);

        resolved.Should().HaveCount(2);
        resolved.Should().Contain(track1);
        resolved.Should().Contain(track2);
    }

    [Fact]
    public void ResolveCompanionFilesOnDisk_CloneCd_ShouldFindImgAndSub()
    {
        using var tmp = new TempDirectoryFixture();
        var dir = tmp.CreateSubdirectory("ccd_test");

        var ccdPath = Path.Combine(dir, "Ridge Racer (USA).ccd");
        var imgPath = Path.Combine(dir, "Ridge Racer (USA).img");
        var subPath = Path.Combine(dir, "Ridge Racer (USA).sub");

        File.WriteAllText(ccdPath, "[CloneCD]\nVersion=3\n");
        File.WriteAllBytes(imgPath, "IMG_DATA"u8.ToArray());
        File.WriteAllBytes(subPath, "SUB_DATA"u8.ToArray());

        var resolved = _opticalService.ResolveCompanionFilesOnDisk(ccdPath);

        resolved.Should().HaveCount(2);
        resolved.Should().Contain(imgPath);
        resolved.Should().Contain(subPath);
    }

    [Fact]
    public void SourceScanner_ShouldGroupCueAndBinFilesIntoSingleScanEntryWithRelatedEntries()
    {
        using var tmp = new TempDirectoryFixture();
        var src = tmp.CreateSubdirectory("src_cue_bin");

        var cuePath = Path.Combine(src, "Dennis (Europe).cue");
        var track1 = Path.Combine(src, "Dennis (Europe) (Track 01).bin");
        var track2 = Path.Combine(src, "Dennis (Europe) (Track 02).bin");
        var standaloneRom = Path.Combine(src, "Super Mario World (Europe).sfc");

        File.WriteAllText(cuePath, """
            FILE "Dennis (Europe) (Track 01).bin" BINARY
              TRACK 01 MODE1/2352
                INDEX 01 00:00:00
            FILE "Dennis (Europe) (Track 02).bin" BINARY
              TRACK 02 AUDIO
                INDEX 01 00:02:00
            """);

        File.WriteAllBytes(track1, new byte[2000]);
        File.WriteAllBytes(track2, new byte[5000]);
        File.WriteAllBytes(standaloneRom, new byte[1000]);

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = tmp.CreateSubdirectory("dst"),
            NamingConvention = NamingConvention.Redump
        };

        var scanner = new SourceScanner(config, _services.ChecksumService, _services.I18n, _opticalService);
        var (entries, totalBytes) = scanner.Scan();

        // Only Dennis (Europe).cue and Super Mario World (Europe).sfc should be top-level entries
        entries.Should().HaveCount(2);
        var cueEntry = entries.FirstOrDefault(e => e.DisplayName == "Dennis (Europe).cue");
        cueEntry.Should().NotBeNull();
        cueEntry!.RelatedEntries.Should().HaveCount(2);
        cueEntry.RelatedEntries.Select(r => Path.GetFileName(r.SourcePath))
            .Should().Contain(["Dennis (Europe) (Track 01).bin", "Dennis (Europe) (Track 02).bin"]);

        // TotalBytes must sum the descriptor AND its tracks as well as the standalone ROM
        totalBytes.Should().Be(new FileInfo(cuePath).Length + 2000 + 5000 + 1000);
    }

    [Fact]
    public async Task EntryClassifier_ShouldPopulateProcessItemRelatedItemsFromScanEntryRelatedEntries()
    {
        using var tmp = new TempDirectoryFixture();
        var src = tmp.CreateSubdirectory("src_classify");

        var cuePath = Path.Combine(src, "Dennis (Europe).cue");
        var track1 = Path.Combine(src, "Dennis (Europe) (Track 01).bin");
        var track2 = Path.Combine(src, "Dennis (Europe) (Track 02).bin");

        File.WriteAllText(cuePath, """
            FILE "Dennis (Europe) (Track 01).bin" BINARY
              TRACK 01 MODE1/2352
                INDEX 01 00:00:00
            FILE "Dennis (Europe) (Track 02).bin" BINARY
              TRACK 02 AUDIO
                INDEX 01 00:02:00
            """);
        File.WriteAllBytes(track1, "TRACK1-BYTES"u8.ToArray());
        File.WriteAllBytes(track2, "TRACK2-BYTES"u8.ToArray());

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = tmp.CreateSubdirectory("dst"),
            NamingConvention = NamingConvention.Redump
        };

        var scanner = new SourceScanner(config, _services.ChecksumService, _services.I18n, _opticalService);
        var (entries, totalBytes) = scanner.Scan();

        var classifier = new EntryClassifier(
            config,
            datCatalog: null,
            new CrcCacheService(CoreTestServices.IsolatedCrcCachePath),
            _services.ChecksumService,
            _services.NoIntroParser,
            _services.FilterService,
            _services.I18n,
            _opticalService);

        var items = await classifier.ClassifyAllAsync(entries, totalBytes: totalBytes);

        items.Should().HaveCount(1);
        var cueItem = items[0];
        cueItem.DisplayName.Should().Be("Dennis (Europe).cue");
        cueItem.Status.Should().Be(FileStatus.Valid);
        cueItem.RelatedItems.Should().HaveCount(2);

        cueItem.RelatedItems[0].DisplayName.Should().Be("Dennis (Europe) (Track 01).bin");
        cueItem.RelatedItems[0].Status.Should().Be(FileStatus.Valid);

        cueItem.RelatedItems[1].DisplayName.Should().Be("Dennis (Europe) (Track 02).bin");
        cueItem.RelatedItems[1].Status.Should().Be(FileStatus.Valid);
    }

    [Fact]
    public async Task FullPipeline_RedumpMultitrackCueBin_ShouldTransferBothCueAndBinTogether()
    {
        using var tmp = new TempDirectoryFixture();
        var src = tmp.CreateSubdirectory("src_full");
        var dst = tmp.CreateSubdirectory("dst_full");

        var cuePath = Path.Combine(src, "Dennis (Europe).cue");
        var track1 = Path.Combine(src, "Dennis (Europe) (Track 01).bin");
        var track2 = Path.Combine(src, "Dennis (Europe) (Track 02).bin");

        File.WriteAllText(cuePath, """
            FILE "Dennis (Europe) (Track 01).bin" BINARY
              TRACK 01 MODE1/2352
                INDEX 01 00:00:00
            FILE "Dennis (Europe) (Track 02).bin" BINARY
              TRACK 02 AUDIO
                INDEX 01 00:02:00
            """);
        File.WriteAllBytes(track1, "TRACK1-DATA"u8.ToArray());
        File.WriteAllBytes(track2, "TRACK2-DATA"u8.ToArray());

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            NamingConvention = NamingConvention.Redump,
            Pattern = "{title}/"
        };

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        result.Stats.TotalScanned.Should().Be(1);
        result.Stats.ValidCount.Should().Be(1);
        result.Stats.ValidBytes.Should().Be(new FileInfo(cuePath).Length + 11 + 11);

        // Verification of physical presence at destination
        var destCue = Path.Combine(dst, "Dennis", "Dennis (Europe).cue");
        var destTrack1 = Path.Combine(dst, "Dennis", "Dennis (Europe) (Track 01).bin");
        var destTrack2 = Path.Combine(dst, "Dennis", "Dennis (Europe) (Track 02).bin");

        File.Exists(destCue).Should().BeTrue();
        File.Exists(destTrack1).Should().BeTrue();
        File.Exists(destTrack2).Should().BeTrue();
    }

    [Fact]
    public async Task FullPipeline_1G1RTiebreak_WinnerTransfersWithAllTracksAndLoserIsExcludedWithTracks()
    {
        using var tmp = new TempDirectoryFixture();
        var src = tmp.CreateSubdirectory("src_1g1r");
        var dst = tmp.CreateSubdirectory("dst_1g1r");

        // Release 1: Europe (winner according to preferences)
        var euCue = Path.Combine(src, "Dennis (Europe).cue");
        var euTrk1 = Path.Combine(src, "Dennis (Europe) (Track 01).bin");
        File.WriteAllText(euCue, "FILE \"Dennis (Europe) (Track 01).bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00");
        File.WriteAllBytes(euTrk1, "EU-TRACK1"u8.ToArray());

        // Release 2: USA (1G1R loser)
        var usCue = Path.Combine(src, "Dennis (USA).cue");
        var usTrk1 = Path.Combine(src, "Dennis (USA) (Track 01).bin");
        File.WriteAllText(usCue, "FILE \"Dennis (USA) (Track 01).bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00");
        File.WriteAllBytes(usTrk1, "US-TRACK1"u8.ToArray());

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            NamingConvention = NamingConvention.Redump,
            Pattern = "{title}/",
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                PriorityCountries = ["EU", "US"]
            }
        };

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        result.Stats.TotalScanned.Should().Be(2);
        result.Stats.ValidCount.Should().Be(1);
        result.Stats.ExcludedCount.Should().Be(1);

        // Only the Europe version and its associated track should be in dest/Dennis/
        File.Exists(Path.Combine(dst, "Dennis", "Dennis (Europe).cue")).Should().BeTrue();
        File.Exists(Path.Combine(dst, "Dennis", "Dennis (Europe) (Track 01).bin")).Should().BeTrue();

        // The USA version must not have been copied
        File.Exists(Path.Combine(dst, "Dennis", "Dennis (USA).cue")).Should().BeFalse();
        File.Exists(Path.Combine(dst, "Dennis", "Dennis (USA) (Track 01).bin")).Should().BeFalse();
    }

    [Fact]
    public async Task DatExport_WithAutomaticallyLinkedCueBin_ShouldExportBothUnderSameGame()
    {
        using var tmp = new TempDirectoryFixture();
        var src = tmp.CreateSubdirectory("src_export");

        var cuePath = Path.Combine(src, "Dennis (Europe).cue");
        var track1 = Path.Combine(src, "Dennis (Europe) (Track 01).bin");

        File.WriteAllText(cuePath, "FILE \"Dennis (Europe) (Track 01).bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00");
        File.WriteAllBytes(track1, "TRACK-DATA-12345"u8.ToArray());

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = tmp.CreateSubdirectory("dst"),
            NamingConvention = NamingConvention.Redump
        };

        var res = await _services.Processor.ExecuteAsync(config, isDryRun: true);
        res.Items.Should().HaveCount(1);
        var datPath = Path.Combine(tmp.Path, "export.xml");
        var datCatalog = await _services.Processor.ExportDatAsync(res.Items, datPath);

        datCatalog.Games.Should().HaveCount(1);
        var game = datCatalog.Games.First();
        game.Roms.Should().HaveCount(2);
        game.Roms.Select(r => r.Name).Should().Contain(["Dennis (Europe).cue", "Dennis (Europe) (Track 01).bin"]);
    }

    [Fact]
    public void ResolveCompanionArchiveMembers_SameTrackNameInSeveralFolders_ShouldTakeTheOneNextToTheDescriptor()
    {
        string[] members = ["disc1/game.cue", "disc1/track.bin", "disc2/game.cue", "disc2/track.bin"];
        Stream Open(string _) => new MemoryStream(Encoding.ASCII.GetBytes("FILE \"track.bin\" BINARY\n  TRACK 01 MODE1/2352\n"));

        _opticalService.ResolveCompanionArchiveMembers("disc2/game.cue", members, Open).Should().Equal("disc2/track.bin");
        _opticalService.ResolveCompanionArchiveMembers("disc1/game.cue", members, Open).Should().Equal("disc1/track.bin");
    }

    [Fact]
    public void Scan_ArchiveWithTheSameFileNameInSeveralFolders_ShouldListEveryMember()
    {
        // As in a merged MAME set: a clone ROM with the name of a parent ROM goes into a folder of the clone
        using var tmp = new TempDirectoryFixture();
        var src = tmp.CreateSubdirectory("src");
        using (var zip = ZipFile.Open(Path.Combine(src, "game.zip"), ZipArchiveMode.Create))
        {
            foreach (var (name, content) in new[] { ("m48t35", "PARENT"), ("clone/m48t35", "CLONE"), ("game.cue", "FILE \"track.bin\" BINARY\n"), ("track.bin", "TRACK") })
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.ASCII.GetBytes(content));
            }
        }

        var logs = new List<LogMessage>();
        var scanner = new SourceScanner(
            new SortConfig { SourceDir = src, DestinationDir = tmp.GetFilePath("dst") },
            _services.ChecksumService,
            _services.I18n,
            _opticalService,
            (level, message) => logs.Add(new LogMessage(level, message)));

        var (entries, _) = scanner.Scan();

        logs.Should().NotContain(l => l.Message.Key == "log.archive_error");
        entries.Select(e => e.ArchiveMember).Should().BeEquivalentTo("m48t35", "clone/m48t35", "game.cue");
        entries.Single(e => e.ArchiveMember == "game.cue").RelatedEntries.Select(r => r.ArchiveMember).Should().Equal("track.bin");
    }
}

