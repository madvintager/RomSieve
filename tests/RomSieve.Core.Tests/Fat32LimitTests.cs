using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// Files written by a sort that are too large for FAT32 (<see cref="Fat32Limit"/>): only what reaches the destination,
/// each companion track on its own, one archive per item in ZIP mode, nothing for links; and the single log warning
/// that names them.
/// </summary>
public sealed class Fat32LimitTests
{
    private const long GiB = 1024L * 1024L * 1024L;

    private static readonly string DestDir = Path.Combine(Path.GetTempPath(), "fat32_dest");

    private static ProcessItem Item(string name, long size, FileStatus status = FileStatus.Valid, string? targetName = null) => new()
    {
        SourcePath = Path.Combine(Path.GetTempPath(), "fat32_src", name),
        FileSize = size,
        Parsed = new TosecParser().ParseFilename(name),
        Status = status,
        FinalTargetPath = Path.Combine(DestDir, targetName ?? name)
    };

    [Fact]
    public void FindOversizedOutputFiles_ShouldListTheWrittenFilesOfFourGigabytesOrMoreLargestFirst()
    {
        var disc = Item("Big Game (Japan).cue", 200);
        disc.RelatedItems.Add(Item("Big Game (Japan) (Track 1).bin", 5 * GiB));
        disc.RelatedItems.Add(Item("Big Game (Japan) (Track 2).bin", 30_000_000));
        var items = new List<ProcessItem>
        {
            disc,
            Item("Limit.iso", Fat32Limit.MaxFileSize),
            Item("Over Limit.iso", Fat32Limit.MaxFileSize + 1),
            Item("Unknown Dump.iso", 6 * GiB, FileStatus.Unmatched),
            Item("Excluded Dump.iso", 7 * GiB, FileStatus.Excluded),
            Item("Duplicate Dump.iso", 7 * GiB, FileStatus.Duplicate),
            Item("Renamed.iso", 4 * GiB, targetName: "Renamed_01.iso")
        };

        var oversized = Fat32Limit.FindOversizedOutputFiles(items, TransferMode.Copy);

        // Excluded files and duplicates are not written; 4 GiB minus one byte still fits; the target name is shown;
        // files of the same size are sorted by name
        oversized.Should().Equal(
            new OversizedOutputFile("Unknown Dump.iso", 6 * GiB, false),
            new OversizedOutputFile("Big Game (Japan) (Track 1).bin", 5 * GiB, false),
            new OversizedOutputFile("Over Limit.iso", Fat32Limit.MaxFileSize + 1, false),
            new OversizedOutputFile("Renamed_01.iso", 4 * GiB, false));
        Fat32Limit.FindOversizedOutputFiles(items, TransferMode.Move).Should().Equal(oversized);
    }

    [Fact]
    public void FindOversizedOutputFiles_InZipMode_ShouldMeasureEachArchiveBeforeCompression()
    {
        var disc = Item("Big Game (Japan).cue", 200, targetName: "Big Game (Japan).zip");
        disc.RelatedItems.Add(Item("Big Game (Japan) (Track 1).bin", 3 * GiB));
        disc.RelatedItems.Add(Item("Big Game (Japan) (Track 2).bin", 2 * GiB));
        var small = Item("Small Game (Japan).iso", GiB, targetName: "Small Game (Japan).zip");

        var oversized = Fat32Limit.FindOversizedOutputFiles([disc, small], TransferMode.Zip);

        oversized.Should().Equal(new OversizedOutputFile("Big Game (Japan).zip", 5 * GiB + 200, IsUpperBound: true));
    }

    [Theory]
    [InlineData(TransferMode.HardLink)]
    [InlineData(TransferMode.SymbolicLink)]
    public void FindOversizedOutputFiles_WithLinks_ShouldListNothing(TransferMode mode)
    {
        Fat32Limit.FindOversizedOutputFiles([Item("Huge.iso", 8 * GiB)], mode).Should().BeEmpty();
    }

    private static OversizedOutputFile Oversized(string name, bool isUpperBound = false) => new(name, 5 * GiB, isUpperBound);

    private static string Translate(LocalizedMessage? message, string locale = "en")
    {
        var i18n = new LocalizationService("fr");
        i18n.SetLocale(locale);
        return i18n.Format(message);
    }

    [Fact]
    public void CreateLogMessage_WithoutOversizedFiles_ShouldWarnAboutNothing()
    {
        Fat32Limit.CreateLogMessage([]).Should().BeNull();
    }

    [Fact]
    public void CreateLogMessage_WithOneFile_ShouldNameIt()
    {
        var message = Fat32Limit.CreateLogMessage([Oversized("Huge.iso")]);

        message.Should().Be(LocalizedMessage.Create("log.fat32_oversized", ("name", "Huge.iso")));
        Translate(message).Should().Be("Over 4 GB, too large for a FAT32 medium (USB drive, SD card): Huge.iso");
    }

    [Fact]
    public void CreateLogMessage_WithSeveralFiles_ShouldCountThemOnOneLine()
    {
        var message = Fat32Limit.CreateLogMessage([Oversized("A.chd"), Oversized("B.chd"), Oversized("C.chd")]);

        message.Should().Be(LocalizedMessage.Create(
            "log.fat32_oversized_files",
            ("count", 3),
            ("examples", "A.chd, B.chd, C.chd")));
        Translate(message, "fr").Should().Be(
            "Fichiers de plus de 4 Go, trop gros pour un support FAT32 (clé USB, carte SD) : 3 (A.chd, B.chd, C.chd)");
    }

    [Fact]
    public void CreateLogMessage_WithMoreThanThreeFiles_ShouldGiveThreeExamplesThenAnEllipsis()
    {
        var files = Enumerable.Range(1, 61).Select(i => Oversized($"disk{i:00}.chd")).ToList();

        var message = Fat32Limit.CreateLogMessage(files);

        message.Should().Be(LocalizedMessage.Create(
            "log.fat32_oversized_files",
            ("count", 61),
            ("examples", "disk01.chd, disk02.chd, disk03.chd, …")));
        Translate(message).Should().Be(
            "Files over 4 GB, too large for a FAT32 medium (USB drive, SD card): 61 (disk01.chd, disk02.chd, disk03.chd, …)");
    }

    [Fact]
    public void CreateLogMessage_InZipMode_ShouldSayTheSizesAreBeforeCompression()
    {
        Fat32Limit.CreateLogMessage([Oversized("Big.zip", isUpperBound: true)])
            .Should().Be(LocalizedMessage.Create("log.fat32_oversized_zip", ("name", "Big.zip")));

        var message = Fat32Limit.CreateLogMessage(
            [Oversized("A.zip", true), Oversized("B.zip", true), Oversized("C.zip", true), Oversized("D.zip", true)]);

        message.Should().Be(LocalizedMessage.Create(
            "log.fat32_oversized_files_zip",
            ("count", 4),
            ("examples", "A.zip, B.zip, C.zip, …")));
        Translate(message).Should().Be(
            "Archives over 4 GB before compression, possibly too large for a FAT32 medium: 4 (A.zip, B.zip, C.zip, …)");
    }

    [Fact]
    public void CreateLogMessage_ShouldFollowTheOrderOfTheList_LargestFirst()
    {
        var items = new List<ProcessItem>
        {
            Item("Small.iso", 5 * GiB),
            Item("Large.iso", 8 * GiB),
            Item("Medium.iso", 6 * GiB),
            Item("Tiny.iso", GiB)
        };

        Fat32Limit.CreateLogMessage(Fat32Limit.FindOversizedOutputFiles(items, TransferMode.Copy))
            .Should().Be(LocalizedMessage.Create(
                "log.fat32_oversized_files",
                ("count", 3),
                ("examples", "Large.iso, Medium.iso, Small.iso")));
    }

    [Fact]
    public void FindOversizedOutputFiles_WithArchiveSingleGame_ShouldRespectPreserveArchivesOption()
    {
        var item = Item("Game.iso", 5 * GiB);
        item.ArchivePath = Path.Combine(Path.GetTempPath(), "fat32_src", "Game.zip");
        item.ArchiveTotalMemberCount = 1;
        item.ArchiveFileSize = 3 * GiB;

        // When preserveArchives is false (default), the uncompressed 5 GiB ISO is extracted, so it exceeds FAT32 limit
        var oversizedDefault = Fat32Limit.FindOversizedOutputFiles([item], TransferMode.Copy, preserveArchives: false);
        oversizedDefault.Should().ContainSingle();
        oversizedDefault[0].Size.Should().Be(5 * GiB);

        // When preserveArchives is true, the 3 GiB archive is preserved intact and fits on FAT32
        var oversizedPreserved = Fat32Limit.FindOversizedOutputFiles([item], TransferMode.Copy, preserveArchives: true);
        oversizedPreserved.Should().BeEmpty();

        // If the archive itself is over 4 GiB, it is reported with the archive size
        item.ArchiveFileSize = 6 * GiB;
        var oversizedLargeArchive = Fat32Limit.FindOversizedOutputFiles([item], TransferMode.Copy, preserveArchives: true);
        oversizedLargeArchive.Should().ContainSingle();
        oversizedLargeArchive[0].Size.Should().Be(6 * GiB);

        // If the archive is 6 GiB but the extracted member is 3 GiB, with preserveArchives=false it fits on FAT32
        item.FileSize = 3 * GiB;
        var oversizedExtractedSmall = Fat32Limit.FindOversizedOutputFiles([item], TransferMode.Copy, preserveArchives: false);
        oversizedExtractedSmall.Should().BeEmpty();

        // When preserveArchives parameter is omitted, it takes item.PreservesArchive
        item.PreservesArchive = true;
        var oversizedFromItem = Fat32Limit.FindOversizedOutputFiles([item], TransferMode.Copy);
        oversizedFromItem.Should().ContainSingle();
        oversizedFromItem[0].Size.Should().Be(6 * GiB);
    }
}
