using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// Room a sort needs in its destination (<see cref="DestinationSpace"/>): what the transfers write on its volume, compared
/// with the room left there, and the warning of the run when it lacks room, from the simulation on.
/// </summary>
public sealed class DestinationSpaceTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");

    public void Dispose() => _temp.Dispose();

    // Two volumes: the source on one, the destination on the other, unless said otherwise
    private static readonly string SourceVolume = Path.Combine(Path.GetTempPath(), "space-source");
    private static readonly string DestinationVolume = Path.Combine(Path.GetTempPath(), "space-destination");
    private static readonly IReadOnlyList<string> TwoVolumes = [SourceVolume, DestinationVolume];
    private static readonly IReadOnlyList<string> OneVolume = [Path.GetTempPath()];

    private static ProcessItem Item(string name, long size, FileStatus status = FileStatus.Valid, string? archiveMember = null) => new()
    {
        SourcePath = Path.Combine(SourceVolume, name),
        ArchiveMember = archiveMember,
        FileSize = size,
        Parsed = new TosecParser().ParseFilename(name),
        Status = status
    };

    private static SortConfig Config(TransferMode mode) => new()
    {
        SourceDir = SourceVolume,
        DestinationDir = Path.Combine(DestinationVolume, "roms"),
        TransferMode = mode
    };

    private static List<ProcessItem> Collection()
    {
        var disc = Item("Disc Game (1995)(Sega).cue", 100);
        disc.RelatedItems.Add(Item("Disc Game (1995)(Sega) (Track 1).bin", 5_000));
        return
        [
            disc,
            Item("Game A (1990)(Ocean).adf", 1_000),
            Item("Unknown.bin", 200, FileStatus.Unmatched),
            Item("Game B (1990)(Ocean).adf", 3_000, FileStatus.Excluded),
            Item("Game C (1990)(Ocean).adf", 4_000, FileStatus.Duplicate)
        ];
    }

    [Fact]
    public void RequiredBytes_ShouldCountWhatTheSortWritesWithTheCompanionFiles()
    {
        // Kept files and their tracks, and the non-conforming file isolated in _unmatched; never the excluded files and
        // the duplicates
        DestinationSpace.GetRequiredBytes(Collection(), Config(TransferMode.Copy), TwoVolumes).Should().Be(6_300);
        DestinationSpace.GetRequiredBytes(Collection(), Config(TransferMode.Zip), TwoVolumes).Should().Be(6_300);
        DestinationSpace.GetRequiredBytes(Collection(), Config(TransferMode.Move), TwoVolumes).Should().Be(6_300, "a move to another volume copies");
    }

    [Fact]
    public void RequiredBytes_ShouldBeNothingForLinksAndForAMoveWithinTheVolume()
    {
        DestinationSpace.GetRequiredBytes(Collection(), Config(TransferMode.HardLink), TwoVolumes).Should().Be(0);
        DestinationSpace.GetRequiredBytes(Collection(), Config(TransferMode.SymbolicLink), TwoVolumes).Should().Be(0);
        DestinationSpace.GetRequiredBytes(Collection(), Config(TransferMode.Move), OneVolume).Should().Be(0, "a move within the volume renames");
    }

    [Fact]
    public void RequiredBytes_OfAnArchiveMemberMovedWithinTheVolume_ShouldCountItsExtraction()
    {
        List<ProcessItem> items = [Item("pack.zip", 900, archiveMember: "Game A (1990)(Ocean).adf")];

        DestinationSpace.GetRequiredBytes(items, Config(TransferMode.Move), OneVolume).Should().Be(900, "the archive stays where it is");
    }

    [Fact]
    public void Shortfall_ShouldOnlyBeFoundWhenTheRoomLeftIsKnownAndTooSmall()
    {
        DestinationSpace.FindShortfall(1_000, 999, "D").Should().Be(new SpaceShortfall("D", 1_000, 999));
        DestinationSpace.FindShortfall(1_000, 1_000, "D").Should().BeNull();
        DestinationSpace.FindShortfall(1_000, null, "D").Should().BeNull("the room of a network share is not known");
    }

    [Fact]
    public void AvailableBytes_ShouldBeThoseOfTheVolumeOfAFolderNotCreatedYet()
    {
        var folder = Path.Combine(_temp.Path, "not", "created");

        DestinationSpace.GetAvailableBytes(folder, VolumeLocator.GetVolumeRoots()).Should().BePositive();
        DestinationSpace.GetAvailableBytes(folder, []).Should().BeNull("a path outside every known volume has no known room");
        DestinationSpace.GetAvailableBytes(string.Empty, VolumeLocator.GetVolumeRoots()).Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Run_ShouldWarnBeforeTheTransfersWhenTheDestinationLacksRoom(bool isDryRun)
    {
        _temp.WriteText(Path.Combine("src", "Game A (1990)(Ocean).adf"), new string('A', 3_000));
        var config = new SortConfig { SourceDir = _temp.GetFilePath("src"), DestinationDir = _temp.GetFilePath("dst") };
        var logs = new List<LogMessage>();
        _services.Processor.AvailableBytesOf = _ => 2_048;

        await _services.Processor.ExecuteAsync(config, isDryRun, logProgress: new SynchronousProgress<LogMessage>(logs.Add));

        var warning = logs.Should().ContainSingle(l => l.Message.Key == "log.destination_space_short").Which;
        warning.Level.Should().Be(LogLevel.Warn);
        _services.I18n.Format(warning.Message).Should()
            .Be($"Not enough room in the destination {config.DestinationDir}: the sort may write up to 2.93 KB there, and only 2.00 KB is left. It will stop if the destination fills up.");
    }

    [Fact]
    public async Task Run_WithRoomEnough_ShouldNotWarn()
    {
        _temp.WriteText(Path.Combine("src", "Game A (1990)(Ocean).adf"), "AAAA");
        var config = new SortConfig { SourceDir = _temp.GetFilePath("src"), DestinationDir = _temp.GetFilePath("dst") };
        var logs = new List<LogMessage>();

        await _services.Processor.ExecuteAsync(config, isDryRun: true, logProgress: new SynchronousProgress<LogMessage>(logs.Add));

        logs.Should().NotContain(l => l.Message.Key == "log.destination_space_short");
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
