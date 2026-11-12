using System.IO;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

public sealed class PreserveArchivesTests : IDisposable
{
    private const string RomName = "Super Game (1995)(Acme).iso";
    private const string ArchiveName = "Super Game (1995)(Acme).zip";
    private static readonly byte[] RomData = ArchiveFormatTests.Pattern("GAME-ISO-", 32 * 1024);

    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");

    public void Dispose() => _temp.Dispose();

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Archives", name);

    private string CreateZip(string relativePath, params (string entryName, byte[] data)[] entries)
    {
        var fullPath = _temp.GetFilePath(relativePath);
        var dir = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var zip = ZipFile.Open(fullPath, ZipArchiveMode.Create);
        foreach (var (entryName, data) in entries)
        {
            var entry = zip.CreateEntry(entryName);
            using var stream = entry.Open();
            stream.Write(data);
        }

        return fullPath;
    }

    private SortConfig CreateConfig(string destination = "dst", TransferMode transferMode = TransferMode.Copy, bool preserveArchives = true) => new()
    {
        SourceDir = _temp.GetFilePath("src"),
        DestinationDir = _temp.GetFilePath(destination),
        Pattern = string.Empty,
        TransferMode = transferMode,
        PreserveArchives = preserveArchives
    };

    [Fact]
    public async Task CopyMode_SingleGameArchive_ShouldCopyArchiveDirectlyWithoutExtraction()
    {
        var zipPath = CreateZip(Path.Combine("src", ArchiveName), (RomName, RomData));
        var config = CreateConfig();

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        var item = result.Items.Single();
        item.Status.Should().Be(FileStatus.Valid);
        item.PreservesArchive.Should().BeTrue();
        item.FinalTargetPath.Should().Be(_temp.GetFilePath(Path.Combine("dst", ArchiveName)));

        // Destination must be the original archive, not the extracted ISO
        File.Exists(item.FinalTargetPath!).Should().BeTrue();
        File.ReadAllBytes(item.FinalTargetPath!).Should().Equal(File.ReadAllBytes(zipPath));

        // Source archive is kept in Copy mode
        File.Exists(zipPath).Should().BeTrue();
    }

    [Fact]
    public async Task MoveMode_SingleGameArchive_ShouldMoveArchiveDirectly()
    {
        var zipPath = CreateZip(Path.Combine("src", ArchiveName), (RomName, RomData));
        var config = CreateConfig(transferMode: TransferMode.Move);

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        var item = result.Items.Single();
        item.Status.Should().Be(FileStatus.Valid);
        item.PreservesArchive.Should().BeTrue();
        item.FinalTargetPath.Should().Be(_temp.GetFilePath(Path.Combine("dst", ArchiveName)));

        // Destination must have the archive
        File.Exists(item.FinalTargetPath!).Should().BeTrue();

        // Source archive must be moved (deleted from source)
        File.Exists(zipPath).Should().BeFalse();
    }

    [Fact]
    public async Task PreserveArchivesOff_ShouldExtractMemberAsUsual()
    {
        var zipPath = CreateZip(Path.Combine("src", ArchiveName), (RomName, RomData));
        var config = CreateConfig(preserveArchives: false);

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        var item = result.Items.Single();
        item.Status.Should().Be(FileStatus.Valid);
        item.PreservesArchive.Should().BeFalse();
        item.FinalTargetPath.Should().Be(_temp.GetFilePath(Path.Combine("dst", RomName)));

        // Destination has the extracted ISO file
        File.Exists(item.FinalTargetPath!).Should().BeTrue();
        File.ReadAllBytes(item.FinalTargetPath!).Should().Equal(RomData);
    }

    [Fact]
    public async Task OpticalDiscWithCompanions_ShouldCopySingleArchiveAndShareTargetPath()
    {
        const string cue = "Game (USA).cue";
        const string track = "Game (USA) (Track 1).bin";
        var cueData = Encoding.ASCII.GetBytes($"FILE \"{track}\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n");
        var trackData = ArchiveFormatTests.Pattern("TRACK1", 4704);

        const string discZip = "Game (USA).zip";
        var zipPath = CreateZip(Path.Combine("src", discZip), (cue, cueData), (track, trackData));
        var config = CreateConfig();

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        var primary = result.Items.Single();
        primary.Status.Should().Be(FileStatus.Valid);
        primary.PreservesArchive.Should().BeTrue();
        primary.FinalTargetPath.Should().Be(_temp.GetFilePath(Path.Combine("dst", discZip)));

        // Related companion shares the target path
        primary.RelatedItems.Should().ContainSingle();
        primary.RelatedItems[0].FinalTargetPath.Should().Be(primary.FinalTargetPath);

        // Destination should only contain the single archive and the marker
        var writtenFiles = Directory.GetFiles(_temp.GetFilePath("dst"), "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != DestinationGuard.MarkerFileName)
            .ToList();
        writtenFiles.Should().ContainSingle().Which.Should().Be(primary.FinalTargetPath);
    }

    [Fact]
    public async Task MultiGameArchive_ShouldFallbackToExtractingMembers()
    {
        const string rom1 = "Game1 (1990)(Acme).rom";
        const string rom2 = "Game2 (1991)(Acme).rom";
        var data1 = ArchiveFormatTests.Pattern("ROM1-", 1024);
        var data2 = ArchiveFormatTests.Pattern("ROM2-", 1024);

        CreateZip(Path.Combine("src", "Multi.zip"), (rom1, data1), (rom2, data2));
        var config = CreateConfig();

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        result.Items.Should().HaveCount(2);
        result.Items.Should().AllSatisfy(i =>
        {
            i.PreservesArchive.Should().BeFalse();
            Path.GetExtension(i.FinalTargetPath!).Should().Be(".rom");
            File.Exists(i.FinalTargetPath!).Should().BeTrue();
        });
    }

    [Fact]
    public async Task ZipMode_ShouldCreateTorrentZipRegardlessOfPreserveArchives()
    {
        CreateZip(Path.Combine("src", ArchiveName), (RomName, RomData));
        var config = CreateConfig(transferMode: TransferMode.Zip, preserveArchives: true);

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        var item = result.Items.Single();
        item.Status.Should().Be(FileStatus.Valid);
        item.PreservesArchive.Should().BeFalse();
        item.FinalTargetPath.Should().Be(_temp.GetFilePath(Path.Combine("dst", ArchiveName)));

        // In ZIP mode, an actual TorrentZip is written from the member
        using var zip = ZipFile.OpenRead(item.FinalTargetPath!);
        zip.Comment.Should().StartWith("TORRENTZIPPED-");
    }

    [Fact]
    public async Task CollisionRename_ShouldRenameArchiveTarget()
    {
        CreateZip(Path.Combine("src", ArchiveName), (RomName, RomData));
        CreateZip(Path.Combine("dst", ArchiveName), (RomName, RomData));
        var config = CreateConfig();
        config.CollisionMode = CollisionMode.Rename;

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        var item = result.Items.Single();
        item.Status.Should().Be(FileStatus.Valid);
        item.FinalTargetPath.Should().Be(_temp.GetFilePath(Path.Combine("dst", "Super Game (1995)(Acme)_01.zip")));
        File.Exists(item.FinalTargetPath!).Should().BeTrue();
    }

    [Fact]
    public async Task CollisionSkip_ShouldKeepExistingDestinationAndSource()
    {
        var existingContent = new byte[] { 1, 2, 3, 4 };
        var dstPath = _temp.GetFilePath(Path.Combine("dst", ArchiveName));
        Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
        File.WriteAllBytes(dstPath, existingContent);

        var srcPath = CreateZip(Path.Combine("src", ArchiveName), (RomName, RomData));
        var config = CreateConfig(transferMode: TransferMode.Move);
        config.CollisionMode = CollisionMode.Skip;

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        var item = result.Items.Single();
        item.Status.Should().Be(FileStatus.Duplicate);

        // Destination was skipped / unchanged
        File.ReadAllBytes(dstPath).Should().Equal(existingContent);
        // Source was not deleted in Move mode because transfer was skipped
        File.Exists(srcPath).Should().BeTrue();
    }

    [Fact]
    public async Task DryRun_ShouldSimulateArchiveTargetWithoutWritingOrMoving()
    {
        var srcPath = CreateZip(Path.Combine("src", ArchiveName), (RomName, RomData));
        var config = CreateConfig(transferMode: TransferMode.Move, preserveArchives: true);

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);

        var item = result.Items.Single();
        item.Status.Should().Be(FileStatus.Valid);
        item.PreservesArchive.Should().BeTrue();
        item.FinalTargetPath.Should().Be(_temp.GetFilePath(Path.Combine("dst", ArchiveName)));

        // No files written in destination
        Directory.Exists(_temp.GetFilePath("dst")).Should().BeFalse();
        // Source archive stays intact
        File.Exists(srcPath).Should().BeTrue();
    }

    [Fact]
    public async Task HardLink_ShouldLinkArchiveTargetDirectly()
    {
        var srcPath = CreateZip(Path.Combine("src", ArchiveName), (RomName, RomData));
        var config = CreateConfig(transferMode: TransferMode.HardLink, preserveArchives: true);

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        var item = result.Items.Single();
        item.Status.Should().Be(FileStatus.Valid);
        item.PreservesArchive.Should().BeTrue();
        item.FinalTargetPath.Should().Be(_temp.GetFilePath(Path.Combine("dst", ArchiveName)));

        File.Exists(item.FinalTargetPath!).Should().BeTrue();
        // Modifying one should affect the other (hard link test)
        File.AppendAllText(item.FinalTargetPath!, "+linktest");
        File.ReadAllBytes(srcPath).Should().Equal(File.ReadAllBytes(item.FinalTargetPath!));
    }

    [Fact]
    public async Task SymbolicLink_ShouldLinkArchiveTargetDirectly_WhenSupported()
    {
        var srcPath = CreateZip(Path.Combine("src", ArchiveName), (RomName, RomData));
        var config = CreateConfig(transferMode: TransferMode.SymbolicLink, preserveArchives: true);

        SortExecutionResult result;
        try
        {
            result = await _services.Processor.ExecuteAsync(config, isDryRun: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows without developer mode / admin privileges
            return;
        }

        var item = result.Items.Single();
        if (item.Status == FileStatus.Valid)
        {
            item.PreservesArchive.Should().BeTrue();
            item.FinalTargetPath.Should().Be(_temp.GetFilePath(Path.Combine("dst", ArchiveName)));
            File.Exists(item.FinalTargetPath!).Should().BeTrue();
            var targetInfo = new FileInfo(item.FinalTargetPath!);
            targetInfo.LinkTarget.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task SevenZip_SingleGame_ShouldPreserveArchiveIntact()
    {
        var fixture = Fixture("single_game.7z");
        var srcPath = _temp.GetFilePath(Path.Combine("src", "Super Game (1995)(Acme).7z"));
        Directory.CreateDirectory(Path.GetDirectoryName(srcPath)!);
        File.Copy(fixture, srcPath);

        var config = CreateConfig(transferMode: TransferMode.Copy, preserveArchives: true);

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        var item = result.Items.Single();
        item.Status.Should().Be(FileStatus.Valid);
        item.PreservesArchive.Should().BeTrue();
        item.FinalTargetPath.Should().Be(_temp.GetFilePath(Path.Combine("dst", "Super Game (1995)(Acme).7z")));

        File.Exists(item.FinalTargetPath!).Should().BeTrue();
        File.ReadAllBytes(item.FinalTargetPath!).Should().Equal(File.ReadAllBytes(srcPath));
    }

    [Fact]
    public async Task SevenZip_MultiGame_ShouldFallbackToExtractingMembers()
    {
        var fixture = Fixture("roms_solid.7z");
        var srcPath = _temp.GetFilePath(Path.Combine("src", "roms_solid.7z"));
        Directory.CreateDirectory(Path.GetDirectoryName(srcPath)!);
        File.Copy(fixture, srcPath);

        var config = CreateConfig(transferMode: TransferMode.Copy, preserveArchives: true);

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        result.Items.Should().HaveCountGreaterThan(1);
        result.Items.Should().AllSatisfy(i =>
        {
            i.PreservesArchive.Should().BeFalse();
            File.Exists(i.FinalTargetPath!).Should().BeTrue();
            Path.GetExtension(i.FinalTargetPath!).Should().NotBe(".7z");
        });
    }

    [Fact]
    public async Task Rar_SingleGame_WithDat_ShouldPreserveArchiveIntact()
    {
        var fixture = Fixture("single_game.rar");
        var srcPath = _temp.GetFilePath(Path.Combine("src", "Super Game (USA).rar"));
        Directory.CreateDirectory(Path.GetDirectoryName(srcPath)!);
        File.Copy(fixture, srcPath);

        // In single_game.rar, member is "testfile.txt", size 12, CRC "6ec18ffe".
        // The No-Intro DAT identifies it as "Super Game (USA).gb".
        var datContent = """
            <?xml version="1.0"?>
            <datafile>
              <header><name>Nintendo - Game Boy (No-Intro)</name><description>No-Intro</description></header>
              <game name="Super Game (USA)">
                <description>Super Game (USA)</description>
                <rom name="Super Game (USA).gb" size="12" crc="6ec18ffe" />
              </game>
            </datafile>
            """;
        var datPath = _temp.WriteText("test.dat", datContent);

        var config = CreateConfig(transferMode: TransferMode.Copy, preserveArchives: true);
        config.DatFile = datPath;

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        var item = result.Items.Single();
        item.Status.Should().Be(FileStatus.Valid);
        item.PreservesArchive.Should().BeTrue();
        item.FinalTargetPath.Should().Be(_temp.GetFilePath(Path.Combine("dst", "Super Game (USA).rar")));

        File.Exists(item.FinalTargetPath!).Should().BeTrue();
        File.ReadAllBytes(item.FinalTargetPath!).Should().Equal(File.ReadAllBytes(srcPath));
    }

    [Fact]
    public async Task Rar_MultiMember_ShouldFallbackToExtractingMembers()
    {
        var fixture = Fixture("sharpcompress_rar4.rar");
        var srcPath = _temp.GetFilePath(Path.Combine("src", "multi.rar"));
        Directory.CreateDirectory(Path.GetDirectoryName(srcPath)!);
        File.Copy(fixture, srcPath);

        // Members of sharpcompress_rar4: тест.txt (15498, 9bd160fa), exe/test.exe (45056, cfb109c8), jpg/test.jpg (40372, 088814e3)
        var datContent = """
            <?xml version="1.0"?>
            <datafile>
              <header><name>Commodore Amiga - Games - [ADF] (TOSEC)</name><description>TOSEC</description></header>
              <game name="Test Cyrillic (1990)(Ocean)"><description>Test</description>
                <rom name="Test Cyrillic (1990)(Ocean).adf" size="15498" crc="9bd160fa" />
              </game>
              <game name="Test Exe (1990)(Ocean)"><description>Test</description>
                <rom name="Test Exe (1990)(Ocean).adf" size="45056" crc="cfb109c8" />
              </game>
              <game name="Test Jpg (1990)(Ocean)"><description>Test</description>
                <rom name="Test Jpg (1990)(Ocean).adf" size="40372" crc="088814e3" />
              </game>
            </datafile>
            """;
        var datPath = _temp.WriteText("multi_rar.dat", datContent);

        var config = CreateConfig(transferMode: TransferMode.Copy, preserveArchives: true);
        config.DatFile = datPath;

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        result.Items.Should().HaveCount(3);
        result.Items.Should().AllSatisfy(i =>
        {
            i.Status.Should().Be(FileStatus.Valid);
            i.PreservesArchive.Should().BeFalse();
            Path.GetExtension(i.FinalTargetPath!).Should().Be(".adf");
            File.Exists(i.FinalTargetPath!).Should().BeTrue();
        });
    }

    [Fact]
    public async Task Undo_ShouldRestoreMovedArchive()
    {
        var crcCache = new CrcCacheService(_temp.GetFilePath(Path.Combine("cache", "crc_cache.json")));
        var journal = new OperationJournalService(_temp.GetFilePath("journals"), _services.ChecksumService, crcCache);
        var processor = new SortProcessorService(
            _services.ChecksumService, _services.DatService, _services.NoIntroParser, _services.FilterService,
            _services.Organizer, _services.I18n, _services.OpticalCompanionService, _services.MameParser, crcCache, journal);

        var zipPath = CreateZip(Path.Combine("src", ArchiveName), (RomName, RomData));
        var config = CreateConfig(transferMode: TransferMode.Move);

        var result = await processor.ExecuteAsync(config, isDryRun: false);
        var target = result.Items.Single().FinalTargetPath!;

        File.Exists(target).Should().BeTrue();
        File.Exists(zipPath).Should().BeFalse();

        // Undo
        var summary = journal.FindLastUndoableJournal()!;
        var report = await journal.UndoAsync(summary.Path);
        report.Issues.Should().BeEmpty();

        File.Exists(target).Should().BeFalse();
        File.Exists(zipPath).Should().BeTrue();
    }

    [Fact]
    public void DestinationSpace_ShouldRespectPreserveArchivesSingleRule()
    {
        var item = new ProcessItem
        {
            SourcePath = Path.Combine(_temp.GetFilePath("src"), ArchiveName),
            Parsed = new TosecParser().ParseFilename(RomName),
            ArchivePath = Path.Combine(_temp.GetFilePath("src"), ArchiveName),
            ArchiveMember = RomName,
            ArchiveTotalMemberCount = 1,
            ArchiveFileSize = 1000,
            FileSize = 50000,
            Status = FileStatus.Valid
        };

        var configCopyOff = CreateConfig(transferMode: TransferMode.Copy, preserveArchives: false);
        DestinationSpace.GetRequiredBytes([item], configCopyOff, ["E:\\"]).Should().Be(50000);

        var configCopyOn = CreateConfig(transferMode: TransferMode.Copy, preserveArchives: true);
        DestinationSpace.GetRequiredBytes([item], configCopyOn, ["E:\\"]).Should().Be(1000);

        // Move same volume: with preserveArchives=true it's a rename (0 bytes), with preserveArchives=false it extracts (50000 bytes)
        var configMoveOff = CreateConfig(transferMode: TransferMode.Move, preserveArchives: false);
        DestinationSpace.GetRequiredBytes([item], configMoveOff, [Path.GetPathRoot(_temp.Path)!]).Should().Be(50000);

        var configMoveOn = CreateConfig(transferMode: TransferMode.Move, preserveArchives: true);
        DestinationSpace.GetRequiredBytes([item], configMoveOn, [Path.GetPathRoot(_temp.Path)!]).Should().Be(0);

        // Move cross volume: with preserveArchives=true it copies archive (1000 bytes), with preserveArchives=false it extracts (50000 bytes)
        var crossVolumes = new[] { _temp.GetFilePath("src"), _temp.GetFilePath("dst") };
        DestinationSpace.GetRequiredBytes([item], configMoveOff, crossVolumes).Should().Be(50000);
        DestinationSpace.GetRequiredBytes([item], configMoveOn, crossVolumes).Should().Be(1000);

        // Links: 0 bytes required
        var configHardLink = CreateConfig(transferMode: TransferMode.HardLink, preserveArchives: true);
        DestinationSpace.GetRequiredBytes([item], configHardLink, crossVolumes).Should().Be(0);

        var configSymLink = CreateConfig(transferMode: TransferMode.SymbolicLink, preserveArchives: true);
        DestinationSpace.GetRequiredBytes([item], configSymLink, crossVolumes).Should().Be(0);
    }

    [Fact]
    public async Task ExportDat_AfterMoveSortWithPreserveArchives_ShouldDescribeUncompressedRomHashes()
    {
        var zipPath = CreateZip(Path.Combine("src", ArchiveName), (RomName, RomData));
        var config = CreateConfig(transferMode: TransferMode.Move, preserveArchives: true);

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);
        var item = result.Items.Single();
        item.Status.Should().Be(FileStatus.Valid);
        item.PreservesArchive.Should().BeTrue();

        // Source archive no longer exists
        File.Exists(zipPath).Should().BeFalse();
        // Destination archive exists
        File.Exists(item.FinalTargetPath!).Should().BeTrue();

        // Compute expected uncompressed hashes
        var expectedCrcStr = $"{System.IO.Hashing.Crc32.HashToUInt32(RomData):x8}";
        var expectedMd5Str = Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(RomData));
        var expectedSha1Str = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(RomData));

        var exportPath = _temp.GetFilePath("exported.xml");
        var catalog = await _services.Processor.ExportDatAsync(
            result.Items,
            exportPath,
            DatExportFormat.LogiqxXml,
            datCatalog: null,
            validOnly: true,
            computeMissingHashes: true);

        var exportedRom = catalog.Entries.Single();
        exportedRom.Name.Should().Be(RomName);
        exportedRom.Size.Should().Be(RomData.Length);
        exportedRom.Crc.Should().Be(expectedCrcStr);
        exportedRom.Md5.Should().Be(expectedMd5Str);
        exportedRom.Sha1.Should().Be(expectedSha1Str);
    }
}