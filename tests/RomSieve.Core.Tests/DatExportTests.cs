using System.IO.Hashing;
using System.Security.Cryptography;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Unit and integration tests for DAT export (Logiqx XML and CLRMAMEPRO) from filtered ROMs.
/// </summary>
public sealed class DatExportTests
{
    private readonly CoreTestServices _services = new();

    [Fact]
    public async Task ComputeFileChecksumsAsync_ShouldReturnCrc32Md5AndSha1()
    {
        using var tmp = new TempDirectoryFixture();
        var data = "Hello, DAT export test 12345!"u8.ToArray();
        var f = tmp.WriteBytes("test.rom", data);

        var expectedCrc = $"{Crc32.HashToUInt32(data):x8}";
        var expectedMd5 = Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();
        var expectedSha1 = Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

        var (crc, md5, sha1) = await _services.ChecksumService.ComputeFileChecksumsAsync(f, computeHashes: true);
        crc.Should().Be(expectedCrc);
        md5.Should().Be(expectedMd5);
        sha1.Should().Be(expectedSha1);

        var (crcOnly, mNone, sNone) = await _services.ChecksumService.ComputeFileChecksumsAsync(f, computeHashes: false);
        crcOnly.Should().Be(expectedCrc);
        mNone.Should().BeNull();
        sNone.Should().BeNull();
    }

    [Fact]
    public void DatCatalogXmlAndClrMameProRoundtrip_ShouldPreserveHeaderGamesAndParentClone()
    {
        var cat = new DatCatalog
        {
            Header = new DatHeader
            {
                Name = "Amiga Custom Collection (Filtered)",
                Description = "Amiga Custom Collection - Exported by RomSieve & Partners",
                Version = "2026.01",
                Author = "RomSieve",
                Comment = "Test Comment with <special> & characters",
                Homepage = "https://romsieve.example.org",
                Url = "https://romsieve.example.org/download"
            }
        };

        var game1 = new DatGame
        {
            Name = "Super Game (1992)(Ocean)(Disk 1 of 2)",
            Description = "Super Game (1992)(Ocean)(Disk 1 of 2)",
            Id = "101",
            CloneOf = null
        };
        game1.Roms.Add(new DatRomEntry(
            Name: "Super Game (1992)(Ocean)(Disk 1 of 2)[!].adf",
            Size: 901120,
            Crc: "12345678",
            Md5: "0123456789abcdef0123456789abcdef",
            Sha1: "0123456789abcdef0123456789abcdef01234567",
            GameName: game1.Name));
        cat.AddGame(game1);

        var game2 = new DatGame
        {
            Name = "Super Game (1992)(Ocean)(Disk 2 of 2)",
            Description = "Super Game (1992)(Ocean)(Disk 2 of 2)",
            Id = "102",
            CloneOf = "Super Game (1992)(Ocean)"
        };
        game2.Roms.Add(new DatRomEntry(
            Name: "Super Game (1992)(Ocean)(Disk 2 of 2)[!].adf",
            Size: 901120,
            Crc: "87654321",
            GameName: game2.Name));
        cat.AddGame(game2);

        // 1. Roundtrip Logiqx XML
        var xmlOutput = cat.ToLogiqxXml();
        xmlOutput.Should().Contain("<?xml version=\"1.0\"?>");
        xmlOutput.Should().Contain("<datafile>");
        xmlOutput.Should().Contain("Super Game (1992)(Ocean)(Disk 1 of 2)");

        var parsedXml = _services.DatService.ParseLogiqxXml(xmlOutput);
        parsedXml.Header.Name.Should().Be(cat.Header.Name);
        parsedXml.Games.Should().HaveCount(2);
        parsedXml.Entries.Should().HaveCount(2);
        parsedXml.Entries[0].Crc.Should().Be("12345678");
        parsedXml.Entries[0].Md5.Should().Be("0123456789abcdef0123456789abcdef");
        parsedXml.Entries[0].Sha1.Should().Be("0123456789abcdef0123456789abcdef01234567");
        parsedXml.Entries[1].Crc.Should().Be("87654321");
        parsedXml.Games[1].CloneOf.Should().Be("Super Game (1992)(Ocean)");

        // 2. Roundtrip CLRMAMEPRO
        var cmpOutput = cat.ToClrMamePro();
        cmpOutput.Should().Contain("clrmamepro (");
        cmpOutput.Should().Contain("name \"Amiga Custom Collection (Filtered)\"");

        var parsedCmp = _services.DatService.ParseClrMameProText(cmpOutput);
        parsedCmp.Header.Name.Should().Be(cat.Header.Name);
        parsedCmp.Games.Should().HaveCount(2);
        parsedCmp.Entries.Should().HaveCount(2);
        parsedCmp.Entries[0].Crc.Should().Be("12345678");
        parsedCmp.Entries[0].Md5.Should().Be("0123456789abcdef0123456789abcdef");
        parsedCmp.Entries[1].Crc.Should().Be("87654321");
        parsedCmp.Games[1].CloneOf.Should().Be("Super Game (1992)(Ocean)");
    }

    [Fact]
    public void ToClrMamePro_ShouldNotContainIdOrCloneOfId_AndShouldResolveCloneOfFromCloneOfId()
    {
        var cat = new DatCatalog
        {
            Header = new DatHeader { Name = "NES Test", Version = "1.0" }
        };

        var parentGame = new DatGame
        {
            Name = "Super Mario Bros. (World)",
            Description = "Super Mario Bros. (World)",
            Id = "0001"
        };
        parentGame.Roms.Add(new DatRomEntry
        {
            Name = "Super Mario Bros. (World).nes",
            Size = 40976,
            Crc = "d445f698",
            GameName = parentGame.Name
        });
        cat.AddGame(parentGame);

        var cloneGame = new DatGame
        {
            Name = "Super Mario Bros. (Japan)",
            Description = "Super Mario Bros. (Japan)",
            Id = "0002",
            CloneOfId = "0001"
        };
        cloneGame.Roms.Add(new DatRomEntry
        {
            Name = "Super Mario Bros. (Japan).nes",
            Size = 40976,
            Crc = "12345678",
            GameName = cloneGame.Name
        });
        cat.AddGame(cloneGame);

        var cmpOutput = cat.ToClrMamePro();

        // Must not contain id or cloneofid keywords (invalid in ClrMamePro specification)
        cmpOutput.Should().NotContain("\tid ");
        cmpOutput.Should().NotContain("\tcloneofid ");

        // Must resolve cloneof to parent's game name
        cmpOutput.Should().Contain("cloneof \"Super Mario Bros. (World)\"");

        // Parsing back should succeed and preserve parent/clone mapping
        var parsed = _services.DatService.ParseClrMameProText(cmpOutput);
        parsed.Games.Should().HaveCount(2);
        parsed.CloneToParent.Should().ContainKey("super mario bros. (japan)");
        parsed.CloneToParent["super mario bros. (japan)"].Should().Be("Super Mario Bros. (World)");
    }

    [Fact]
    public async Task CreateDatFromProcessItemsAsync_ShouldFilterByValidStatusWhenRequested()
    {
        using var tmp = new TempDirectoryFixture();
        var validData = "NES\x1a_zelda_content_here_12345"u8.ToArray();
        var fValid = tmp.WriteBytes("Zelda (USA).nes", validData);
        var (crcValid, md5Valid, sha1Valid) = await _services.ChecksumService.ComputeFileChecksumsAsync(fValid);

        var fExcluded = tmp.WriteBytes("Zelda (Japan) [b].nes", "bad dump"u8.ToArray());

        var parsedValid = new ParsedRomName
        {
            RawFilename = Path.GetFileName(fValid),
            Title = "The Legend of Zelda",
            IsTosecConform = true
        };
        var itemValid = new ProcessItem
        {
            SourcePath = fValid,
            FileSize = validData.Length,
            Parsed = parsedValid,
            Status = FileStatus.Valid,
            Crc32 = crcValid,
            Md5 = md5Valid,
            Sha1 = sha1Valid
        };

        var parsedExcluded = new ParsedRomName
        {
            RawFilename = Path.GetFileName(fExcluded),
            Title = "The Legend of Zelda",
            IsTosecConform = true
        };
        var itemExcluded = new ProcessItem
        {
            SourcePath = fExcluded,
            FileSize = 8,
            Parsed = parsedExcluded,
            Status = FileStatus.Excluded,
            Crc32 = "deadbeef"
        };

        var items = new[] { itemValid, itemExcluded };

        // onlyValid = true (default)
        var catValid = await _services.DatService.CreateDatFromProcessItemsAsync(items, onlyValid: true);
        catValid.Entries.Should().ContainSingle();
        catValid.Entries[0].Name.Should().Be("Zelda (USA).nes");
        catValid.Entries[0].Crc.Should().Be(crcValid);
        catValid.Entries[0].Md5.Should().Be(md5Valid);
        catValid.Entries[0].Sha1.Should().Be(sha1Valid);

        // onlyValid = false
        var catAll = await _services.DatService.CreateDatFromProcessItemsAsync(items, onlyValid: false);
        catAll.Entries.Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateDatComputesMissingChecksums_ShouldComputeCrcMd5AndSha1OnTheFly()
    {
        using var tmp = new TempDirectoryFixture();
        var content = "metroid_game_data_for_checksum_test"u8.ToArray();
        var f = tmp.WriteBytes("Metroid (USA).nes", content);
        var (expectedCrc, expectedMd5, expectedSha1) = await _services.ChecksumService.ComputeFileChecksumsAsync(f);

        var parsed = new ParsedRomName
        {
            RawFilename = Path.GetFileName(f),
            Title = "Metroid",
            IsTosecConform = true
        };
        var item = new ProcessItem
        {
            SourcePath = f,
            FileSize = content.Length,
            Parsed = parsed,
            Status = FileStatus.Valid,
            Crc32 = string.Empty
        };

        var cat = await _services.DatService.CreateDatFromProcessItemsAsync([item], computeMissingChecksums: true);
        cat.Entries.Should().ContainSingle();
        cat.Entries[0].Crc.Should().Be(expectedCrc);
        cat.Entries[0].Md5.Should().Be(expectedMd5);
        cat.Entries[0].Sha1.Should().Be(expectedSha1);
    }

    [Fact]
    public async Task CreateDatWithOpticalRelatedItems_ShouldGroupCueAndBinTracksUnderSameGame()
    {
        using var tmp = new TempDirectoryFixture();
        var cueBytes = "FILE \"Ridge Racer (USA) (Track 1).bin\" BINARY"u8.ToArray();
        var binBytes = "BINARY_TRACK_DATA_BYTES"u8.ToArray();
        var cueFile = tmp.WriteBytes("Ridge Racer (USA).cue", cueBytes);
        var binFile = tmp.WriteBytes("Ridge Racer (USA) (Track 1).bin", binBytes);

        var (cueCrc, cueMd5, cueSha1) = await _services.ChecksumService.ComputeFileChecksumsAsync(cueFile);
        var (binCrc, binMd5, binSha1) = await _services.ChecksumService.ComputeFileChecksumsAsync(binFile);

        var parsedCue = new ParsedRomName { RawFilename = Path.GetFileName(cueFile), Title = "Ridge Racer", IsTosecConform = true };
        var parsedBin = new ParsedRomName { RawFilename = Path.GetFileName(binFile), Title = "Ridge Racer", IsTosecConform = true };

        var relBinItem = new ProcessItem
        {
            SourcePath = binFile,
            FileSize = binBytes.Length,
            Parsed = parsedBin,
            Status = FileStatus.Valid,
            Crc32 = binCrc,
            Md5 = binMd5,
            Sha1 = binSha1
        };

        var cueItem = new ProcessItem
        {
            SourcePath = cueFile,
            FileSize = cueBytes.Length,
            Parsed = parsedCue,
            Status = FileStatus.Valid,
            Crc32 = cueCrc,
            Md5 = cueMd5,
            Sha1 = cueSha1,
            RelatedItems = [relBinItem]
        };

        var cat = await _services.DatService.CreateDatFromProcessItemsAsync([cueItem]);
        cat.Entries.Should().HaveCount(2);
        cat.Games.Should().ContainSingle();
        cat.Games[0].Roms.Select(r => r.Name).Should().BeEquivalentTo(
            ["Ridge Racer (USA).cue", "Ridge Racer (USA) (Track 1).bin"]);
    }

    [Fact]
    public async Task ExportDatFileFormats_ShouldWriteLogiqxXmlAndClrMameProFiles()
    {
        using var tmp = new TempDirectoryFixture();
        var romBytes = "sonic_megadrive_rom"u8.ToArray();
        var f = tmp.WriteBytes("Sonic (Europe).md", romBytes);
        var parsed = new ParsedRomName { RawFilename = Path.GetFileName(f), Title = "Sonic the Hedgehog", IsTosecConform = true };
        var item = new ProcessItem
        {
            SourcePath = f,
            FileSize = romBytes.Length,
            Parsed = parsed,
            Status = FileStatus.Valid,
            Crc32 = "abcdef12"
        };

        // 1. Export as XML
        var xmlPath = tmp.GetFilePath("sonic.xml");
        await _services.DatService.ExportDatFileAsync([item], xmlPath, format: DatExportFormat.LogiqxXml);
        File.Exists(xmlPath).Should().BeTrue();
        var xmlText = await File.ReadAllTextAsync(xmlPath);
        xmlText.Should().Contain("<?xml version=\"1.0\"?>");
        xmlText.Should().Contain("Sonic (Europe).md");

        // 2. Export as CLRMAMEPRO
        var cmpPath = tmp.GetFilePath("sonic.dat");
        await _services.DatService.ExportDatFileAsync([item], cmpPath, format: DatExportFormat.ClrMamePro);
        File.Exists(cmpPath).Should().BeTrue();
        var cmpText = await File.ReadAllTextAsync(cmpPath);
        cmpText.Should().Contain("clrmamepro (");
        cmpText.Should().Contain("name \"Sonic (Europe).md\"");

        // 3. Processor convenience methods
        var xmlDirect = tmp.GetFilePath("direct.xml");
        var cmpDirect = tmp.GetFilePath("direct.dat");
        await _services.Processor.ExportDatToXmlAsync([item], xmlDirect);
        await _services.Processor.ExportDatToClrMameProAsync([item], cmpDirect);
        File.Exists(xmlDirect).Should().BeTrue();
        File.Exists(cmpDirect).Should().BeTrue();
    }

    [Fact]
    public async Task SortProcessorExportDatIntegration_ShouldExportOnlyValidSortedItems()
    {
        using var tmp = new TempDirectoryFixture();
        var sourceDir = tmp.CreateSubdirectory("source");
        var destDir = tmp.CreateSubdirectory("dest");

        tmp.WriteBytes("source/Castlevania (1987)(Konami)(US).nes", "castlevania_data"u8.ToArray());
        tmp.WriteBytes("source/Castlevania (1987)(Konami)(US)[b].nes", "corrupt_dump"u8.ToArray());

        var cfg = new SortConfig
        {
            SourceDir = sourceDir,
            DestinationDir = destDir,
            Recursive = false,
            TransferMode = TransferMode.Copy
        };

        var result = await _services.Processor.ExecuteAsync(cfg, isDryRun: true);
        result.Stats.ValidCount.Should().Be(1);
        result.Stats.ExcludedCount.Should().Be(1);

        var datOut = tmp.GetFilePath("castlevania_export.xml");
        var exportedCat = await _services.Processor.ExportDatAsync(result.Items, datOut, format: DatExportFormat.LogiqxXml, datCatalog: result.DatCatalog);
        File.Exists(datOut).Should().BeTrue();
        exportedCat.Entries.Should().ContainSingle();
        exportedCat.Entries[0].Name.Should().Be("Castlevania (1987)(Konami)(US).nes");
    }

    [Fact]
    public async Task IdentifiedFile_ShouldTakeItsChecksumsFromTheDatWithoutBeingReadAgain()
    {
        using var tmp = new TempDirectoryFixture();
        var sourceDir = tmp.CreateSubdirectory("source");
        var data = "zelda_cartridge_data"u8.ToArray();
        var romPath = tmp.WriteBytes("source/Zelda (USA).nes", data);
        var crc = $"{Crc32.HashToUInt32(data):x8}";
        var md5 = Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();
        var sha1 = Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();
        var datPath = tmp.WriteText("nes.dat", $"""
            <?xml version="1.0"?>
            <datafile>
              <header><name>Nintendo - NES</name><description>Nintendo - NES</description></header>
              <game name="Zelda (USA)"><description>Zelda (USA)</description>
                <rom name="Zelda (USA).nes" size="{data.Length}" crc="{crc}" md5="{md5}" sha1="{sha1}"/>
              </game>
            </datafile>
            """);
        var result = await _services.Processor.ExecuteAsync(
            new SortConfig { SourceDir = sourceDir, DestinationDir = tmp.GetFilePath("dest"), DatFile = datPath },
            isDryRun: true);
        result.Items.Should().ContainSingle(it => it.Status == FileStatus.Valid && it.DatGameName == "Zelda (USA)");

        // The file is gone: only the DAT can give its MD5 and SHA-1
        File.Delete(romPath);
        var exported = await _services.Processor.ExportDatAsync(
            result.Items, tmp.GetFilePath("export.xml"), datCatalog: result.DatCatalog);

        var entry = exported.Entries.Should().ContainSingle().Subject;
        entry.Size.Should().Be(data.Length);
        entry.Crc.Should().Be(crc);
        entry.Md5.Should().Be(md5);
        entry.Sha1.Should().Be(sha1);
        entry.GameName.Should().Be("Zelda (USA)");
    }

    [Fact]
    public async Task Export_ShouldReportItsProgressAndStopWhenCancelled()
    {
        using var tmp = new TempDirectoryFixture();
        var items = Enumerable.Range(1, 3).Select(i => new ProcessItem
        {
            SourcePath = tmp.WriteBytes($"Game {i} (USA).nes", [(byte)i]),
            FileSize = 1,
            Parsed = new ParsedRomName { RawFilename = $"Game {i} (USA).nes", Title = $"Game {i}" },
            Status = FileStatus.Valid
        }).ToList();
        items.Add(new ProcessItem
        {
            SourcePath = tmp.GetFilePath("Excluded (USA).nes"),
            FileSize = 1,
            Parsed = new ParsedRomName { RawFilename = "Excluded (USA).nes", Title = "Excluded" },
            Status = FileStatus.Excluded
        });

        // Only the exported files are counted, and the last report says they are all done
        var reports = new SynchronousProgress();
        var path = tmp.GetFilePath("export.xml");
        await _services.DatService.ExportDatFileAsync(items, path, progress: reports);
        reports.Values.Select(r => (r.Current, r.Total)).Should().Equal((0, 3), (1, 3), (2, 3), (3, 3));
        reports.Values[1].Filename.Should().Be("Game 2 (USA).nes");

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var cancelledPath = tmp.GetFilePath("cancelled.xml");
        var export = () => _services.DatService.ExportDatFileAsync(items, cancelledPath, cancellationToken: cancellation.Token);
        await export.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(cancelledPath).Should().BeFalse();
    }

    [Fact]
    public void CountFiles_ShouldCountARomListedSeveralTimesInAGameOnce()
    {
        // As cburnrub in MAME: a ROM shared by the four BIOS options is listed four times, but is one file of the set
        var game = new DatGame { Name = "cburnrub", Description = "Burnin' Rubber" };
        foreach (var _ in Enumerable.Range(0, 4))
        {
            game.Roms.Add(new DatRomEntry("dsp-3_p1-.l9", 2048, "3bfff5f3", null, null, game.Name));
        }
        game.Roms.Add(new DatRomEntry("DSP-3_P1-.L9", 2048, "3bfff5f3", null, null, game.Name));
        game.Roms.Add(new DatRomEntry("cburnrub.rom", 4096, "12345678", null, null, game.Name));
        game.Roms.Add(new DatRomEntry("never.rom", 4096, string.Empty, null, null, game.Name) { Status = DatRomStatus.NoDump });
        game.Roms.Add(new DatRomEntry("cburnrub", 0, string.Empty, null, "0123456789abcdef0123456789abcdef01234567", game.Name) { IsDisk = true });

        // The same ROM in another game is a file of that set too
        var other = new DatGame { Name = "decocass", Description = "DECO Cassette System" };
        other.Roms.Add(new DatRomEntry("dsp-3_p1-.l9", 2048, "3bfff5f3", null, null, other.Name));

        var catalog = new DatCatalog();
        catalog.AddGame(game);
        catalog.AddGame(other);

        catalog.Entries.Should().HaveCount(8, "the index keeps every line of the DAT");
        catalog.CountFiles().Should().Be(4, "one shared ROM, one own ROM and one disk in cburnrub, one ROM in decocass");
    }

    private sealed class SynchronousProgress : IProgress<(int Current, int Total, string Filename)>
    {
        public List<(int Current, int Total, string Filename)> Values { get; } = [];

        public void Report((int Current, int Total, string Filename) value) => Values.Add(value);
    }
}
