using System.IO;
using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// Tests for the source/destination overlap safeguards enforced before any sort or simulation.
/// </summary>
public sealed class SortConfigValidatorTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");

    public void Dispose() => _temp.Dispose();

    private SortConfig CreateConfig(string src, string dst, bool recursive = true) => new()
    {
        SourceDir = src,
        DestinationDir = dst,
        Recursive = recursive,
        Pattern = "{publisher}/{title}/",
        TransferMode = TransferMode.Copy
    };

    private static string Root(params string[] parts) =>
        Path.Combine([Path.GetTempPath(), "romsieve_overlap", .. parts]);

    [Fact]
    public void GetPathOverlap_IdenticalPaths_ShouldReturnIdentical()
    {
        SortConfigValidator.GetPathOverlap(Root("roms"), Root("roms"), recursive: true)
            .Should().Be(PathOverlap.Identical);
    }

    [Fact]
    public void GetPathOverlap_TrailingSeparator_ShouldBeIgnored()
    {
        SortConfigValidator.GetPathOverlap(Root("roms") + Path.DirectorySeparatorChar, Root("roms"), recursive: true)
            .Should().Be(PathOverlap.Identical);
    }

    [Fact]
    public void GetPathOverlap_DestinationParentOfSource_ShouldReturnContainsSource()
    {
        SortConfigValidator.GetPathOverlap(Root("roms", "in"), Root("roms"), recursive: false)
            .Should().Be(PathOverlap.DestinationContainsSource);
    }

    [Fact]
    public void GetPathOverlap_FileSystemRootAsDestination_ShouldReturnContainsSource()
    {
        var src = Root("roms");
        SortConfigValidator.GetPathOverlap(src, Path.GetPathRoot(src)!, recursive: false)
            .Should().Be(PathOverlap.DestinationContainsSource);
    }

    [Theory]
    [InlineData(true, PathOverlap.DestinationInsideSource)]
    [InlineData(false, PathOverlap.None)]
    public void GetPathOverlap_DestinationInsideSource_DependsOnRecursiveScan(bool recursive, PathOverlap expected)
    {
        SortConfigValidator.GetPathOverlap(Root("roms"), Root("roms", "sorted"), recursive)
            .Should().Be(expected);
    }

    [Fact]
    public void GetPathOverlap_SiblingWithCommonPrefix_ShouldReturnNone()
    {
        SortConfigValidator.GetPathOverlap(Root("roms"), Root("roms_sorted"), recursive: true)
            .Should().Be(PathOverlap.None);
        SortConfigValidator.GetPathOverlap(Root("roms_sorted", "in"), Root("roms"), recursive: true)
            .Should().Be(PathOverlap.None);
    }

    [Theory]
    [InlineData(true, PathOverlap.Identical)]
    [InlineData(false, PathOverlap.None)]
    public void GetPathOverlap_DifferentCase_DependsOnCaseSensitivity(bool ignoreCase, PathOverlap expected)
    {
        SortConfigValidator.GetPathOverlap(Root("Roms"), Root("roms"), recursive: true, ignoreCase)
            .Should().Be(expected);
    }

    [Fact]
    public void GetPathOverlap_DefaultCaseRule_ShouldFollowPlatform()
    {
        var expected = OperatingSystem.IsWindows() ? PathOverlap.DestinationContainsSource : PathOverlap.None;
        SortConfigValidator.GetPathOverlap(Root("Roms", "in"), Root("roms"), recursive: false)
            .Should().Be(expected);
    }

    [Fact]
    public void GetErrorKey_ShouldMapEveryOverlapToATranslatedMessage()
    {
        SortConfigValidator.GetErrorKey(PathOverlap.None).Should().BeNull();
        foreach (var overlap in new[] { PathOverlap.Identical, PathOverlap.DestinationContainsSource, PathOverlap.DestinationInsideSource })
        {
            var key = SortConfigValidator.GetErrorKey(overlap)!;
            LocalizationService.Translations["fr"].Should().ContainKey(key);
            LocalizationService.Translations["en"].Should().ContainKey(key);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DestinationParentOfSource_WithPurge_ShouldThrowAndKeepSource()
    {
        var parent = _temp.CreateSubdirectory("roms");
        var src = _temp.CreateSubdirectory(Path.Combine("roms", "in"));
        var game = _temp.WriteText(Path.Combine("roms", "in", "Game (1990)(Ocean).dsk"), "game");

        var config = CreateConfig(src, parent);
        config.PurgeDestination = true;

        var act = () => _services.Processor.ExecuteAsync(config, isDryRun: false);

        await act.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(game).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_DestinationInsideSource_Recursive_ShouldThrow()
    {
        var src = _temp.CreateSubdirectory("src");
        _temp.WriteText(Path.Combine("src", "Game (1990)(Ocean).dsk"), "game");
        var dst = Path.Combine(src, "sorted");

        var act = () => _services.Processor.ExecuteAsync(CreateConfig(src, dst, recursive: true), isDryRun: true);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ExecuteAsync_DestinationInsideSource_NonRecursive_ShouldRun()
    {
        var src = _temp.CreateSubdirectory("src");
        _temp.WriteText(Path.Combine("src", "Game (1990)(Ocean).dsk"), "game");
        var dst = Path.Combine(src, "sorted");

        var result = await _services.Processor.ExecuteAsync(CreateConfig(src, dst, recursive: false), isDryRun: true);

        result.Stats.TotalScanned.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_DestinationEqualToSourceWithTrailingSeparator_ShouldThrow()
    {
        var src = _temp.CreateSubdirectory("src");
        _temp.WriteText(Path.Combine("src", "Game (1990)(Ocean).dsk"), "game");
        var dst = src + Path.DirectorySeparatorChar;

        var act = () => _services.Processor.ExecuteAsync(CreateConfig(src, dst), isDryRun: true);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData("mame/chd", "dialog.error_same_chd_dest")]
    [InlineData("mame", "dialog.error_dest_contains_chd")]
    [InlineData("mame/chd/sorted", "dialog.error_dest_inside_chd")]
    [InlineData("out", null)]
    public void GetErrorKey_ShouldAlsoProtectTheChdFolder(string destination, string? expected)
    {
        // The CHD folder is always read recursively: a destination inside it is refused even without a recursive scan
        var config = CreateConfig(Root("roms"), Root(destination.Split('/')), recursive: false);
        config.ChdDir = Root("mame", "chd");

        SortConfigValidator.GetErrorKey(config, isMameRun: true).Should().Be(expected);
        SortConfigValidator.GetErrorKey(config, isMameRun: false)
            .Should().BeNull("only a MAME run reads the CHD folder, which stays in the configuration for the next one");
    }

    [Fact]
    public void GetErrorKey_WithoutChdFolder_ShouldOnlyCheckTheSource()
    {
        SortConfigValidator.GetErrorKey(CreateConfig(Root("roms"), Root("out")), isMameRun: true).Should().BeNull();
        SortConfigValidator.GetErrorKey(CreateConfig(Root("roms"), Root("roms")), isMameRun: true).Should().Be("dialog.error_same_source_dest");
    }

    [Fact]
    public async Task ExecuteAsync_OutsideMame_ShouldIgnoreTheChdFolderLeftInTheConfiguration()
    {
        var src = _temp.CreateSubdirectory("src");
        _temp.WriteText(Path.Combine("src", "Game (1990)(Ocean).dsk"), "game");
        var dst = _temp.CreateSubdirectory("dst");
        var config = CreateConfig(src, dst);
        config.ChdDir = _temp.CreateSubdirectory(Path.Combine("dst", "chd"));

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);

        result.Items.Should().ContainSingle().Which.Status.Should().Be(FileStatus.Valid,
            "a TOSEC sort never reads the CHD folder of the last MAME sort");
    }

    // =========================================================================
    // Symbolic links and junctions
    // =========================================================================

    /// <summary>Creates a link to a directory, or returns null where links need a privilege the test does not have.</summary>
    private static string? TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return link;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [Fact]
    public void GetPathOverlap_LinkToTheSource_ShouldBeIdentical()
    {
        var source = _temp.CreateSubdirectory("roms");
        if (TryLink(Path.Combine(_temp.Path, "alias"), source) is not { } alias)
        {
            return;
        }

        SortConfigValidator.GetPathOverlap(source, alias, recursive: true).Should().Be(PathOverlap.Identical);
        SortConfigValidator.GetPathOverlap(alias, source, recursive: false).Should().Be(PathOverlap.Identical);
    }

    [Fact]
    public void GetPathOverlap_DestinationThroughALinkInsideTheSource_ShouldBeDetected()
    {
        // The destination is reached through a link to a folder of the source, and does not exist yet
        var source = _temp.CreateSubdirectory("roms");
        var inner = _temp.CreateSubdirectory(Path.Combine("roms", "sorted"));
        if (TryLink(Path.Combine(_temp.Path, "out"), inner) is not { } link)
        {
            return;
        }

        var destination = Path.Combine(link, "new", "folder");
        SortConfigValidator.GetPathOverlap(source, destination, recursive: true).Should().Be(PathOverlap.DestinationInsideSource);
        SortConfigValidator.GetPathOverlap(source, destination, recursive: false).Should().Be(PathOverlap.None, "a top-level scan never enters it");
        SortConfigValidator.GetErrorKey(CreateConfig(source, destination), isMameRun: false).Should().Be("dialog.error_dest_inside_source");
    }

    [Fact]
    public void GetPathOverlap_LinkInAParentOfTheSource_ShouldBeResolved()
    {
        // The source is reached through a link in one of its parents; the destination is a parent of the real source
        var real = _temp.CreateSubdirectory(Path.Combine("disk", "collection"));
        _temp.CreateSubdirectory(Path.Combine("disk", "collection", "amiga"));
        if (TryLink(Path.Combine(_temp.Path, "mount"), Path.Combine(_temp.Path, "disk")) is not { } mount)
        {
            return;
        }

        var source = Path.Combine(mount, "collection", "amiga");
        SortConfigValidator.GetPathOverlap(source, real, recursive: true).Should().Be(PathOverlap.DestinationContainsSource);
    }

    [Fact]
    public void GetPathOverlap_LinkToAnotherFolder_ShouldNotOverlap()
    {
        var source = _temp.CreateSubdirectory("roms");
        var other = _temp.CreateSubdirectory("elsewhere");
        if (TryLink(Path.Combine(_temp.Path, "out"), other) is not { } link)
        {
            return;
        }

        SortConfigValidator.GetPathOverlap(source, link, recursive: true).Should().Be(PathOverlap.None);
    }

    [Fact]
    public void ResolveLinks_LoopOfLinks_ShouldStopWithoutThrowing()
    {
        var a = Path.Combine(_temp.Path, "a");
        var b = Path.Combine(_temp.Path, "b");
        if (TryLink(a, b) is null || TryLink(b, a) is null)
        {
            return;
        }

        var act = () => SortConfigValidator.ResolveLinks(Path.Combine(a, "dest"));
        act.Should().NotThrow();
        SortConfigValidator.GetPathOverlap(_temp.CreateSubdirectory("roms"), Path.Combine(a, "dest"), recursive: true)
            .Should().Be(PathOverlap.None);
    }
}
