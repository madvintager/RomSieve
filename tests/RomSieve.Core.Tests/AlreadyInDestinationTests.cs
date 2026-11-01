using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Kept files whose target was already in the destination before the sort (a previous sort, another tool): in the
/// collision mode "skip", they are duplicates told apart from those of the run itself, counted apart, and never
/// written again.
/// </summary>
public sealed class AlreadyInDestinationTests : IDisposable
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

    private static SortConfig CreateConfig(string src, string dst) => new()
    {
        SourceDir = src,
        DestinationDir = dst,
        Pattern = string.Empty,
        NamingConvention = NamingConvention.Redump,
        CollisionMode = CollisionMode.Skip,
        AppendToDestination = true
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASetAlreadyInTheDestination_ShouldBeToldApartAndNotWrittenAgain(bool isDryRun)
    {
        var src = CreateSet("src", "NEW TRACK");
        var dst = CreateSet("dst", "PREVIOUS TRACK");

        var result = await _services.Processor.ExecuteAsync(CreateConfig(src, dst), isDryRun);

        var item = result.Items.Single();
        item.Status.Should().Be(FileStatus.Duplicate);
        item.IsAlreadyInDestination.Should().BeTrue();
        item.StatusReason!.Key.Should().Be("reason.already_in_destination");
        item.RelatedItems.Should().OnlyContain(r => r.IsAlreadyInDestination && r.Status == FileStatus.Duplicate);
        _services.I18n.Format(item.StatusReason).Should().Be("Already in the destination (not written again)");

        result.Stats.DuplicateCount.Should().Be(1);
        result.Stats.AlreadyInDestinationCount.Should().Be(1);
        result.Stats.ValidCount.Should().Be(0);
        _services.Processor.RecomputeStats(result.Items, result.Stats).AlreadyInDestinationCount.Should().Be(1);
        File.ReadAllText(Path.Combine(dst, TrackName)).Should().Be("PREVIOUS TRACK", "the file of the destination stays as it was");
    }

    [Fact]
    public async Task ADuplicateOfTheRunItself_ShouldNotBeSaidToBeInTheDestination()
    {
        CreateSet(Path.Combine("src", "a"), "TRACK-A");
        CreateSet(Path.Combine("src", "b"), "TRACK-B");
        var dst = _temp.CreateSubdirectory("dst");

        var result = await _services.Processor.ExecuteAsync(CreateConfig(Path.Combine(_temp.Path, "src"), dst), isDryRun: true);

        var duplicate = result.Items.Single(it => it.Status == FileStatus.Duplicate);
        duplicate.IsAlreadyInDestination.Should().BeFalse();
        duplicate.StatusReason!.Key.Should().Be("reason.target_already_exists_skip");
        result.Stats.DuplicateCount.Should().Be(1);
        result.Stats.AlreadyInDestinationCount.Should().Be(0);
    }

    [Fact]
    public async Task TheLog_ShouldSayHowToWriteTheFilesAlreadyInTheDestination()
    {
        var src = CreateSet("src", "NEW TRACK");
        var dst = CreateSet("dst", "PREVIOUS TRACK");
        var messages = new List<LocalizedMessage>();

        await _services.Processor.ExecuteAsync(
            CreateConfig(src, dst),
            isDryRun: true,
            logProgress: new CollectedLog(messages));

        messages.Should().Contain(m => m.Key == "log.already_in_destination");
        messages.Should().Contain(m => m.Key == "log.already_in_destination_summary");
    }

    // Collects the log as it is written: Progress<T> would report it later, on another thread
    private sealed class CollectedLog(List<LocalizedMessage> messages) : IProgress<LogMessage>
    {
        public void Report(LogMessage value)
        {
            lock (messages)
            {
                messages.Add(value.Message);
            }
        }
    }

    [Fact]
    public async Task ARecalculatedGame_ShouldForgetThatItsFileWasInTheDestination()
    {
        var src = CreateSet("src", "NEW TRACK");
        var dst = CreateSet("dst", "PREVIOUS TRACK");
        var config = CreateConfig(src, dst);
        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);

        _services.Processor.RecalculateGameItems(result.Items.ToList(), config, allItems: result.Items);

        result.Items.Single().IsAlreadyInDestination.Should().BeFalse("its status is that of the selection again");
    }
}
