using System.Diagnostics;
using System.IO;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Files a scan reads (<see cref="FileTree"/>): in the order of .NET, hidden files included, without the folders and
/// files of the system; a folder that cannot be read is skipped and reported instead of stopping the scan, and a link to
/// a folder is never followed, even when it loops.
/// </summary>
public sealed class FileTreeTests : IDisposable
{
    private const string GameName = "Game (1990)(Pub).adf";

    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");
    private readonly string _src;

    public FileTreeTests()
    {
        _src = _temp.CreateSubdirectory("src");
        _temp.WriteText(Path.Combine("src", GameName), "game");
    }

    public void Dispose() => _temp.Dispose();

    private sealed class LogCollector : IProgress<LogMessage>
    {
        public List<LogMessage> Messages { get; } = [];

        public void Report(LogMessage value) => Messages.Add(value);
    }

    /// <summary>Files listed under the source, relative to it, with '/' as separator.</summary>
    private List<string> ListSource(FileTreeSkips? skips = null) =>
        [.. FileTree.EnumerateFiles(_src, recursive: true, skips ?? new FileTreeSkips())
            .Select(p => Path.GetRelativePath(_src, p).Replace('\\', '/'))];

    /// <summary>
    /// Creates a link to a folder: a symbolic link, or a junction where Windows refuses symbolic links without a
    /// privilege; <see langword="null"/> when neither can be created.
    /// </summary>
    private static string? TryLinkFolder(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return link;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }
        }

        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        });
        mklink?.WaitForExit();
        return Directory.Exists(link) ? link : null;
    }

    [Fact]
    public void Files_ShouldComeInTheOrderOfDotNet_HiddenOnesIncluded()
    {
        foreach (var file in new[] { "b.adf", "a.adf", "Zeta/c.adf", "alpha/d.adf", "alpha/beta/e.adf", "alpha/beta/gamma/f.adf", "omega/g.adf" })
        {
            _temp.WriteText(Path.Combine("src", file), file);
        }

        var hidden = _temp.WriteText(Path.Combine("src", "alpha", ".hidden.adf"), "hidden");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(hidden, FileAttributes.Hidden);
        }

        foreach (var recursive in new[] { true, false })
        {
            FileTree.EnumerateFiles(_src, recursive, new FileTreeSkips()).Should().Equal(
                Directory.EnumerateFiles(_src, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly),
                "the files keep the order of a former scan, which the collisions of a sort depend on");
        }
    }

    [Theory]
    [InlineData("System Volume Information")]
    [InlineData("$RECYCLE.BIN")]
    [InlineData("lost+found")]
    [InlineData(".Trash-1000")]
    [InlineData(".Trashes")]
    [InlineData("#recycle")]
    [InlineData("#snapshot")]
    [InlineData("@eaDir")]
    [InlineData("@Recycle")]
    public void SystemFolder_ShouldBeSkipped(string folder)
    {
        _temp.WriteText(Path.Combine("src", folder, "Deleted (1990)(Pub).adf"), "deleted");
        _temp.WriteText(Path.Combine("src", "Amiga", folder, "Thumbnail (1990)(Pub).adf"), "nested");

        FileTree.IsSystemFolder(folder).Should().BeTrue();
        ListSource().Should().Equal(GameName);
    }

    [Theory]
    [InlineData("Thumbs.db")]
    [InlineData("desktop.ini")]
    [InlineData(".DS_Store")]
    [InlineData("._Game (1990)(Pub).adf")]
    public void SystemFile_ShouldBeSkipped(string file)
    {
        _temp.WriteText(Path.Combine("src", file), "system");
        _temp.WriteText(Path.Combine("src", "Amiga", file), "system");

        FileTree.IsSystemFile(file).Should().BeTrue();
        ListSource().Should().Equal(GameName);
    }

    [Theory]
    [InlineData("Amiga")]
    [InlineData("Recycle")]
    [InlineData("Trash Can (1990)(Pub)")]
    [InlineData("System")]
    public void OrdinaryFolder_ShouldNotBeTakenForAFolderOfTheSystem(string folder) =>
        FileTree.IsSystemFolder(folder).Should().BeFalse();

    [Fact]
    public void UnreadableFolder_ShouldBeSkippedAndReported()
    {
        _temp.WriteText(Path.Combine("src", "zzz", "Last (1990)(Pub).adf"), "last");
        if (!_temp.TryCreateUnreadableDirectory(Path.Combine("src", "locked")))
        {
            return;
        }

        var skips = new FileTreeSkips();
        ListSource(skips).Should().Equal(GameName, "zzz/Last (1990)(Pub).adf");
        skips.UnreadableFolders.Should().Equal(Path.Combine(_src, "locked"));
        skips.FolderLinks.Should().BeEmpty();
    }

    [Fact]
    public void UnreadableRoot_ShouldGiveNoFileAndBeReported()
    {
        if (!_temp.TryCreateUnreadableDirectory("locked"))
        {
            return;
        }

        var root = _temp.GetFilePath("locked");
        var skips = new FileTreeSkips();
        FileTree.EnumerateFiles(root, recursive: true, skips).Should().BeEmpty();
        skips.UnreadableFolders.Should().Equal(root);
    }

    [Fact]
    public void LinkToAFolder_ShouldNotBeFollowed_EvenWhenItLoops()
    {
        _temp.WriteText(Path.Combine("src", "games", "Other (1990)(Pub).adf"), "other");
        var elsewhere = _temp.CreateSubdirectory("elsewhere");
        _temp.WriteText(Path.Combine("elsewhere", "Outside (1990)(Pub).adf"), "outside");
        if (TryLinkFolder(Path.Combine(_src, "games", "loop"), _src) is not { } loop
            || TryLinkFolder(Path.Combine(_src, "outside"), elsewhere) is not { } outside)
        {
            return;
        }

        var skips = new FileTreeSkips();
        ListSource(skips).Should().Equal(GameName, "games/Other (1990)(Pub).adf");
        skips.FolderLinks.Should().BeEquivalentTo([loop, outside]);
    }

    [Fact]
    public async Task Scan_WithAnUnreadableFolder_ShouldGoOnAndWarn()
    {
        _temp.WriteText(Path.Combine("src", "Thumbs.db"), "thumbnails");
        _temp.WriteText(Path.Combine("src", "$RECYCLE.BIN", "Deleted (1990)(Pub).adf"), "deleted");
        if (!_temp.TryCreateUnreadableDirectory(Path.Combine("src", "locked")))
        {
            return;
        }

        var logs = new LogCollector();
        var result = await _services.Processor.ExecuteAsync(
            new SortConfig { SourceDir = _src, DestinationDir = _temp.GetFilePath("dst") }, isDryRun: true, logProgress: logs);

        result.Items.Select(i => i.DisplayName).Should().Equal(GameName);
        logs.Messages.Should().ContainSingle(m => m.Message.Key == "log.unreadable_folders_skipped")
            .Which.Level.Should().Be(LogLevel.Warn);
    }

    [Fact]
    public async Task Scan_WithALinkToAFolder_ShouldNotFollowItAndWarn()
    {
        var elsewhere = _temp.CreateSubdirectory("elsewhere");
        _temp.WriteText(Path.Combine("elsewhere", "Outside (1990)(Pub).adf"), "outside");
        if (TryLinkFolder(Path.Combine(_src, "outside"), elsewhere) is null)
        {
            return;
        }

        var logs = new LogCollector();
        var result = await _services.Processor.ExecuteAsync(
            new SortConfig { SourceDir = _src, DestinationDir = _temp.GetFilePath("dst") }, isDryRun: true, logProgress: logs);

        result.Items.Select(i => i.DisplayName).Should().Equal(GameName);
        logs.Messages.Should().ContainSingle(m => m.Message.Key == "log.folder_links_not_followed")
            .Which.Level.Should().Be(LogLevel.Warn);
    }

    [Fact]
    public void ChdFolder_WithAnUnreadableFolder_ShouldListTheOtherDisksAndWarn()
    {
        var chdDir = _temp.CreateSubdirectory("chd");
        _temp.WriteText(Path.Combine("chd", "game", "disk.chd"), "disk");
        _temp.WriteText(Path.Combine("chd", "game", "readme.txt"), "not a disk");
        if (!_temp.TryCreateUnreadableDirectory(Path.Combine("chd", "locked")))
        {
            return;
        }

        var logs = new List<(LogLevel Level, LocalizedMessage Message)>();
        var scanner = new SourceScanner(
            new SortConfig { SourceDir = _src, DestinationDir = _temp.GetFilePath("dst") },
            _services.ChecksumService,
            _services.I18n,
            _services.OpticalCompanionService,
            (level, message) => logs.Add((level, message)));

        var entries = scanner.ScanChdFolder(chdDir, []);

        entries.Select(e => e.SourcePath).Should().Equal(Path.Combine(chdDir, "game", "disk.chd"));
        logs.Should().Contain(l => l.Message.Key == "log.unreadable_folders_skipped" && l.Level == LogLevel.Warn);
    }
}
