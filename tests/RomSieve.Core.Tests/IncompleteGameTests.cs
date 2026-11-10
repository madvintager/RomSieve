using System.Globalization;
using System.IO.Hashing;
using System.Text;
using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// "Complete sets only" with a DAT: a disc or a game of the DAT that lacks some of its files is excluded with a reason
/// that counts files (an incomplete disc, or an incomplete game without a descriptor), while a set that lacks a whole
/// disc keeps the reason that counts disks.
/// </summary>
public sealed class IncompleteGameTests : IDisposable
{
    private const string Game = "Star Game (Europe)";

    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new();

    public void Dispose() => _temp.Dispose();

    private static string Crc(byte[] data) => Crc32.HashToUInt32(data).ToString("x8", CultureInfo.InvariantCulture);

    /// <summary>Files of each disc of a two-disc Redump game: its descriptor and two tracks, all different.</summary>
    private static Dictionary<string, byte[]> RedumpDisc(int disc)
    {
        var name = $"{Game} (Disc {disc})";
        var cue = $"""
            FILE "{name} (Track 1).bin" BINARY
              TRACK 01 MODE2/2352
                INDEX 01 00:00:00
            FILE "{name} (Track 2).bin" BINARY
              TRACK 02 AUDIO
                INDEX 01 00:00:00
            """;
        return new Dictionary<string, byte[]>
        {
            [name + ".cue"] = Encoding.ASCII.GetBytes(cue),
            [name + " (Track 1).bin"] = Encoding.ASCII.GetBytes($"DATA TRACK OF DISC {disc}"),
            [name + " (Track 2).bin"] = Encoding.ASCII.GetBytes($"AUDIO TRACK OF DISC {disc}")
        };
    }

    private static string DatOf(IEnumerable<(string Game, Dictionary<string, byte[]> Files)> games)
    {
        var xml = new StringBuilder("""
            <?xml version="1.0"?>
            <datafile>
              <header><name>Sony - PlayStation</name><description>Sony - PlayStation</description></header>
            """);
        foreach (var (game, files) in games)
        {
            xml.AppendLine($"""  <game name="{game}"><description>{game}</description>""");
            foreach (var (name, data) in files)
            {
                xml.AppendLine($"""    <rom name="{name}" size="{data.Length}" crc="{Crc(data)}"/>""");
            }

            xml.AppendLine("  </game>");
        }

        return xml.Append("</datafile>").ToString();
    }

    private async Task<SortExecutionResult> SimulateAsync(
        IEnumerable<(string Game, Dictionary<string, byte[]> Files)> datGames,
        IEnumerable<(string Name, byte[] Data)> sourceFiles)
    {
        var src = _temp.CreateSubdirectory("src");
        foreach (var (name, data) in sourceFiles)
        {
            File.WriteAllBytes(Path.Combine(src, name), data);
        }

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = _temp.CreateSubdirectory("dst"),
            DatFile = _temp.WriteText("game.dat", DatOf(datGames))
        };
        config.Filters.CompleteMultidiskSetsOnly = true;
        config.Filters.NoIntroCompleteMultimediaSetsOnly = true;
        return await _services.Processor.ExecuteAsync(config, isDryRun: true);
    }

    [Fact]
    public async Task DiscWithMissingTracks_ShouldBeExcludedAsAnIncompleteDisc()
    {
        var disc1 = RedumpDisc(1);
        var disc2 = RedumpDisc(2);
        var track1 = $"{Game} (Disc 1) (Track 2).bin";
        var track2 = $"{Game} (Disc 2) (Track 2).bin";

        var result = await SimulateAsync(
            [($"{Game} (Disc 1)", disc1), ($"{Game} (Disc 2)", disc2)],
            [(track1, disc1[track1]), (track2, disc2[track2])]);

        result.Items.Should().HaveCount(2);
        foreach (var item in result.Items)
        {
            item.Status.Should().Be(FileStatus.Excluded);
            item.StatusReason!.Key.Should().Be("reason.incomplete_disc");
            _services.I18n.Format(item.StatusReason).Should().Be("Disque incomplet : 1/3 fichiers du DAT trouvés");
        }
    }

    [Fact]
    public async Task GameWithoutDescriptorMissingAFile_ShouldBeExcludedAsAnIncompleteGame()
    {
        var files = new Dictionary<string, byte[]>
        {
            [$"{Game} (Disc 1) (Part A).bin"] = "PART A"u8.ToArray(),
            [$"{Game} (Disc 1) (Part B).bin"] = "PART B"u8.ToArray()
        };
        var present = $"{Game} (Disc 1) (Part A).bin";

        var result = await SimulateAsync([($"{Game} (Disc 1)", files)], [(present, files[present])]);

        var item = result.Items.Single();
        item.Status.Should().Be(FileStatus.Excluded);
        item.StatusReason!.Key.Should().Be("reason.incomplete_game");
        _services.I18n.Format(item.StatusReason).Should().Be("Jeu incomplet : 1/2 fichiers du DAT trouvés");
    }

    [Fact]
    public async Task SetMissingAWholeDisc_ShouldKeepTheReasonThatCountsDisks()
    {
        var disc1 = RedumpDisc(1);
        var disc2 = RedumpDisc(2);

        // Disc 1 is missing: the number of disks comes from the highest disc number of the source
        var result = await SimulateAsync(
            [($"{Game} (Disc 1)", disc1), ($"{Game} (Disc 2)", disc2)],
            disc2.Select(f => (f.Key, f.Value)));

        var descriptor = result.Items.Single(it => it.DisplayName.EndsWith(".cue", StringComparison.Ordinal));
        descriptor.Status.Should().Be(FileStatus.Excluded);
        descriptor.StatusReason!.Key.Should().Be("reason.incomplete_multidisk_set");
        _services.I18n.Format(descriptor.StatusReason).Should().Be("Set multi-disquettes incomplet (1/2 disques trouvés)");
    }

    [Fact]
    public async Task CompleteSet_ShouldBeKept()
    {
        var disc1 = RedumpDisc(1);
        var disc2 = RedumpDisc(2);

        var result = await SimulateAsync(
            [($"{Game} (Disc 1)", disc1), ($"{Game} (Disc 2)", disc2)],
            disc1.Concat(disc2).Select(f => (f.Key, f.Value)));

        result.Items.Should().NotBeEmpty();
        result.Items.Should().OnlyContain(it => it.Status == FileStatus.Valid);
    }
}
