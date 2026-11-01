using System.IO;
using System.Text;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Temporary files that a process stopped at the wrong moment leaves behind: those of the settings and caches
/// (<c>&lt;file&gt;.&lt;id&gt;.tmp</c>) and the replacements of a destination (<c>&lt;target&gt;.&lt;id&gt;.romsieve-tmp</c>).
/// They are removed at the next write of the same file, once old enough never to be a write in progress, and nothing
/// else ever is; a scan never takes a replacement for a file of the collection.
/// </summary>
public sealed class TemporaryFileCleanupTests : IDisposable
{
    private const string RomName = "Alpha (1990)(Ocean).adf";

    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");

    public void Dispose() => _temp.Dispose();

    private string WriteFile(string relativePath, bool stale)
    {
        var path = _temp.WriteText(relativePath, "left behind");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - (stale ? TimeSpan.FromHours(2) : TimeSpan.FromMinutes(1)));
        return path;
    }

    // =========================================================================
    // Settings and caches
    // =========================================================================

    [Fact]
    public void AtomicWrite_ShouldRemoveOnlyTheStaleTemporaryFilesOfTheSameFile()
    {
        var target = _temp.WriteText("config.json", "{}");
        var stale = WriteFile($"config.json.{new string('a', 32)}.tmp", stale: true);
        var recent = WriteFile($"config.json.{new string('b', 32)}.tmp", stale: false);
        var otherFile = WriteFile($"profile.json.{new string('c', 32)}.tmp", stale: true);
        var notOurs = WriteFile("config.json.notes.tmp", stale: true);
        var upperCase = WriteFile($"config.json.{new string('D', 32)}.tmp", stale: true);

        AtomicFile.WriteAllText(target, "{\"a\": 1}", new UTF8Encoding(false));

        File.Exists(stale).Should().BeFalse("it was left by a stopped process");
        File.Exists(recent).Should().BeTrue("another process may still be writing it");
        File.Exists(otherFile).Should().BeTrue("it belongs to another file");
        File.Exists(notOurs).Should().BeTrue("RomSieve never names a file so");
        File.Exists(upperCase).Should().BeTrue("RomSieve writes its ids in lower case");
        File.ReadAllText(target).Should().Be("{\"a\": 1}");
    }

    [Fact]
    public async Task ConfigurationSave_ShouldRemoveAStaleTemporaryFile()
    {
        var target = _temp.WriteText("config.json", "{}");
        var stale = WriteFile($"config.json.{new string('e', 32)}.tmp", stale: true);

        (await new ConfigurationService().SaveConfigToFileAsync(new Dictionary<string, object?> { ["a"] = 1 }, target)).Should().BeTrue();

        File.Exists(stale).Should().BeFalse();
    }

    // =========================================================================
    // Replacements of a destination
    // =========================================================================

    [Theory]
    [InlineData("Alpha (1990)(Ocean).adf.0a1b2c3d.romsieve-tmp", true)]
    [InlineData("/dst/Ocean/Alpha (1990)(Ocean).adf.deadbeef.romsieve-tmp", true)]
    [InlineData("Alpha (1990)(Ocean).adf.notes.romsieve-tmp", false)]
    [InlineData("Alpha (1990)(Ocean).adf.DEADBEEF.romsieve-tmp", false)]
    [InlineData("Alpha (1990)(Ocean).adf.deadbeef0.romsieve-tmp", false)]
    [InlineData(".deadbeef.romsieve-tmp", false)]
    [InlineData("Alpha (1990)(Ocean).adf", false)]
    public void IsReplacementFile_ShouldOnlyKnowTheNamesRomSieveGives(string path, bool expected)
    {
        DestinationGuard.IsReplacementFile(path).Should().Be(expected);
        DestinationGuard.IsGuardPath(path).Should().Be(expected, "a scan skips a replacement left behind");
    }

    [Fact]
    public async Task Overwrite_ShouldRemoveTheStaleReplacementsOfTheTargetOnly()
    {
        var source = _temp.CreateSubdirectory("src");
        _temp.WriteText(Path.Combine("src", RomName), "new");
        _temp.WriteText(Path.Combine("src", "Beta (1991)(Ocean).adf"), "beta");
        var config = new SortConfig
        {
            SourceDir = source,
            DestinationDir = _temp.GetFilePath("dst"),
            Pattern = "{publisher}/",
            CollisionMode = CollisionMode.Overwrite,
            AppendToDestination = true
        };
        var first = await _services.Processor.ExecuteAsync(config, isDryRun: false);
        var target = first.Items.Single(i => i.DisplayName == RomName).FinalTargetPath!;
        var folder = Path.GetDirectoryName(target)!;

        var stale = WriteFile(Path.Combine(folder, RomName + ".0a1b2c3d.romsieve-tmp"), stale: true);
        var recent = WriteFile(Path.Combine(folder, RomName + ".1a2b3c4d.romsieve-tmp"), stale: false);
        var otherTarget = WriteFile(Path.Combine(folder, "Beta (1991)(Ocean).adf.2a3b4c5d.romsieve-tmp"), stale: true);
        var notOurs = WriteFile(Path.Combine(folder, RomName + ".notes.romsieve-tmp"), stale: true);
        File.WriteAllText(Path.Combine(source, RomName), "newer");

        var second = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        second.Items.Single(i => i.DisplayName == RomName).Status.Should().Be(FileStatus.Valid);
        File.ReadAllText(target).Should().Be("newer");
        File.Exists(stale).Should().BeFalse("it was left by a stopped sort");
        File.Exists(recent).Should().BeTrue("another sort may still be writing it");
        File.Exists(notOurs).Should().BeTrue("RomSieve never names a file so");

        // Removed when its own target is overwritten, not before
        File.Exists(otherTarget).Should().BeFalse("its target was overwritten by the same sort");
    }

    [Fact]
    public async Task NewTarget_ShouldRemoveTheStaleReplacementsLeftByASortStoppedWhileWritingIt()
    {
        // A sort ended in the middle of a new file leaves its replacement, never a file under the name of its target
        var source = _temp.CreateSubdirectory("src");
        _temp.WriteText(Path.Combine("src", RomName), "rom");
        var folder = _temp.CreateSubdirectory(Path.Combine("dst", "Ocean"));
        var stale = WriteFile(Path.Combine(folder, RomName + ".0a1b2c3d.romsieve-tmp"), stale: true);
        var recent = WriteFile(Path.Combine(folder, RomName + ".1a2b3c4d.romsieve-tmp"), stale: false);
        var otherTarget = WriteFile(Path.Combine(folder, "Beta (1991)(Ocean).adf.2a3b4c5d.romsieve-tmp"), stale: true);
        var config = new SortConfig
        {
            SourceDir = source,
            DestinationDir = _temp.GetFilePath("dst"),
            Pattern = "{publisher}/",
            AppendToDestination = true
        };

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        result.Items.Single().Status.Should().Be(FileStatus.Valid);
        File.ReadAllText(Path.Combine(folder, RomName)).Should().Be("rom");
        File.Exists(stale).Should().BeFalse("it was left by a stopped sort");
        File.Exists(recent).Should().BeTrue("another sort may still be writing it");
        File.Exists(otherTarget).Should().BeTrue("its own target was not written");
        Directory.EnumerateFiles(folder).Where(DestinationGuard.IsReplacementFile)
            .Should().BeEquivalentTo([recent, otherTarget], "the sort leaves none of its own");
    }

    [Fact]
    public async Task Scan_ShouldSkipAReplacementLeftBehind()
    {
        var source = _temp.CreateSubdirectory("src");
        _temp.WriteText(Path.Combine("src", RomName), "rom");
        _temp.WriteText(Path.Combine("src", RomName + ".0a1b2c3d.romsieve-tmp"), "partial");
        _temp.WriteText(Path.Combine("src", RomName + ".notes.romsieve-tmp"), "a file of the user");

        var result = await _services.Processor.ExecuteAsync(
            new SortConfig { SourceDir = source, DestinationDir = _temp.GetFilePath("dst") }, isDryRun: true);

        result.Items.Select(i => i.DisplayName).Should().BeEquivalentTo(RomName, RomName + ".notes.romsieve-tmp");
    }
}
