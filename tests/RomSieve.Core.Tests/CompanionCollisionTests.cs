using System.IO;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// Tests ensuring that optical descriptors and their companion tracks are resolved
/// as a single set against destination collisions.
/// </summary>
public sealed class CompanionCollisionTests : IDisposable
{
    private const string CueName = "Dennis (Europe).cue";
    private const string TrackName = "Dennis (Europe) (Track 01).bin";

    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");

    public void Dispose() => _temp.Dispose();

    private static string CueContent(string trackName) =>
        $"FILE \"{trackName}\" BINARY\r\n  TRACK 01 MODE1/2352\r\n    INDEX 01 00:00:00\r\n";

    private string CreateSet(string folder, string trackContent)
    {
        var dir = _temp.CreateSubdirectory(folder);
        File.WriteAllText(Path.Combine(dir, CueName), CueContent(TrackName));
        File.WriteAllText(Path.Combine(dir, TrackName), trackContent);
        return dir;
    }

    private SortConfig CreateConfig(string src, string dst, CollisionMode mode, TransferMode transfer = TransferMode.Copy) => new()
    {
        SourceDir = src,
        DestinationDir = dst,
        Pattern = string.Empty,
        NamingConvention = NamingConvention.Redump,
        CollisionMode = mode,
        TransferMode = transfer
    };

    private static IEnumerable<string> AllFinalPaths(SortExecutionResult result) =>
        result.Items.SelectMany(it => new[] { it }.Concat(it.RelatedItems))
            .Select(it => it.FinalTargetPath ?? string.Empty)
            .OrderBy(p => p, StringComparer.Ordinal);

    [Fact]
    public async Task Rename_TwoSetsWithHomonymousTracks_ShouldRenameWholeSecondSetAndRewriteCue()
    {
        CreateSet(Path.Combine("src", "a"), "TRACK-A");
        CreateSet(Path.Combine("src", "b"), "TRACK-B");
        var src = Path.Combine(_temp.Path, "src");
        var dst = _temp.CreateSubdirectory("dst");

        var result = await _services.Processor.ExecuteAsync(CreateConfig(src, dst, CollisionMode.Rename), isDryRun: false);

        result.Stats.ErrorCount.Should().Be(0);
        var renamedCue = Path.Combine(dst, "Dennis (Europe)_01.cue");
        var renamedTrack = Path.Combine(dst, "Dennis (Europe) (Track 01)_01.bin");

        File.Exists(Path.Combine(dst, CueName)).Should().BeTrue();
        File.Exists(renamedCue).Should().BeTrue();
        File.Exists(renamedTrack).Should().BeTrue();
        new[] { File.ReadAllText(Path.Combine(dst, TrackName)), File.ReadAllText(renamedTrack) }
            .Should().BeEquivalentTo(["TRACK-A", "TRACK-B"]);

        File.ReadAllText(Path.Combine(dst, CueName)).Should().Be(CueContent(TrackName));
        File.ReadAllText(renamedCue).Should().Be(CueContent("Dennis (Europe) (Track 01)_01.bin"));
    }

    [Fact]
    public async Task Skip_TwoSetsWithHomonymousTracks_ShouldSkipWholeSecondSet()
    {
        CreateSet(Path.Combine("src", "a"), "TRACK-A");
        CreateSet(Path.Combine("src", "b"), "TRACK-B");
        var src = Path.Combine(_temp.Path, "src");
        var dst = _temp.CreateSubdirectory("dst");

        var result = await _services.Processor.ExecuteAsync(CreateConfig(src, dst, CollisionMode.Skip), isDryRun: false);

        Directory.EnumerateFiles(dst, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != DestinationGuard.MarkerFileName).Should().HaveCount(2);
        var kept = result.Items.Single(it => it.Status == FileStatus.Valid);
        var skipped = result.Items.Single(it => it.Status == FileStatus.Duplicate);
        skipped.RelatedItems.Should().OnlyContain(r => r.Status == FileStatus.Duplicate);

        var expectedTrack = File.ReadAllText(Path.Combine(Path.GetDirectoryName(kept.SourcePath)!, TrackName));
        File.ReadAllText(Path.Combine(dst, TrackName)).Should().Be(expectedTrack);
    }

    [Fact]
    public async Task AppendMode_ExistingTrackAtDestination_ShouldNotBeOverwritten()
    {
        var src = CreateSet("src", "TRACK-NEW");
        var dst = _temp.CreateSubdirectory("dst");
        File.WriteAllText(Path.Combine(dst, TrackName), "TRACK-OLD");

        var config = CreateConfig(src, dst, CollisionMode.Rename);
        config.AppendToDestination = true;
        await _services.Processor.ExecuteAsync(config, isDryRun: false);

        File.ReadAllText(Path.Combine(dst, TrackName)).Should().Be("TRACK-OLD");
        File.Exists(Path.Combine(dst, CueName)).Should().BeFalse();
        File.ReadAllText(Path.Combine(dst, "Dennis (Europe) (Track 01)_01.bin")).Should().Be("TRACK-NEW");
        File.ReadAllText(Path.Combine(dst, "Dennis (Europe)_01.cue"))
            .Should().Be(CueContent("Dennis (Europe) (Track 01)_01.bin"));
    }

    [Fact]
    public async Task DryRun_ShouldPredictExactlyTheFinalPathsOfTheRealRun()
    {
        CreateSet(Path.Combine("src", "a"), "TRACK-A");
        CreateSet(Path.Combine("src", "b"), "TRACK-B");
        var src = Path.Combine(_temp.Path, "src");
        var dst = _temp.CreateSubdirectory("dst");
        var config = CreateConfig(src, dst, CollisionMode.Rename);

        var simulated = await _services.Processor.ExecuteAsync(config, isDryRun: true);
        var real = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        AllFinalPaths(simulated).Should().Equal(AllFinalPaths(real));
        AllFinalPaths(real).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task HardLinkMode_RenamedDescriptor_ShouldBeWrittenAsModifiedCopyWithoutTouchingSource()
    {
        CreateSet(Path.Combine("src", "a"), "TRACK-A");
        CreateSet(Path.Combine("src", "b"), "TRACK-B");
        var src = Path.Combine(_temp.Path, "src");
        var dst = _temp.CreateSubdirectory("dst");

        var result = await _services.Processor.ExecuteAsync(
            CreateConfig(src, dst, CollisionMode.Rename, TransferMode.HardLink), isDryRun: false);

        result.Stats.ErrorCount.Should().Be(0);
        File.ReadAllText(Path.Combine(dst, "Dennis (Europe)_01.cue"))
            .Should().Be(CueContent("Dennis (Europe) (Track 01)_01.bin"));
        foreach (var folder in new[] { "a", "b" })
        {
            File.ReadAllText(Path.Combine(src, folder, CueName)).Should().Be(CueContent(TrackName));
        }
    }

    [Fact]
    public async Task MoveMode_RenamedDescriptor_ShouldRemoveSourceDescriptorAfterWritingRewrittenCopy()
    {
        var src = CreateSet("src", "TRACK-NEW");
        var dst = _temp.CreateSubdirectory("dst");
        File.WriteAllText(Path.Combine(dst, TrackName), "TRACK-OLD");

        var config = CreateConfig(src, dst, CollisionMode.Rename, TransferMode.Move);
        config.AppendToDestination = true;
        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        result.Stats.ErrorCount.Should().Be(0);
        Directory.EnumerateFiles(src).Should().BeEmpty();
        File.ReadAllText(Path.Combine(dst, "Dennis (Europe)_01.cue"))
            .Should().Be(CueContent("Dennis (Europe) (Track 01)_01.bin"));
        File.ReadAllText(Path.Combine(dst, "Dennis (Europe) (Track 01)_01.bin")).Should().Be("TRACK-NEW");
    }

    [Fact]
    public async Task ZipArchive_CompanionTracks_ShouldKeepTheirMemberName()
    {
        var src = _temp.CreateSubdirectory("src");
        using (var zip = ZipFile.Open(Path.Combine(src, "Dennis.zip"), ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry(CueName).Open(), new UTF8Encoding(false)))
            {
                w.Write(CueContent(TrackName));
            }
            using (var w = new StreamWriter(zip.CreateEntry(TrackName).Open(), new UTF8Encoding(false)))
            {
                w.Write("TRACK-ZIP");
            }
        }
        var dst = _temp.CreateSubdirectory("dst");

        await _services.Processor.ExecuteAsync(CreateConfig(src, dst, CollisionMode.Rename), isDryRun: false);

        File.ReadAllText(Path.Combine(dst, TrackName)).Should().Be("TRACK-ZIP");
        File.Exists(Path.Combine(dst, "Dennis.zip")).Should().BeFalse();
    }
}
