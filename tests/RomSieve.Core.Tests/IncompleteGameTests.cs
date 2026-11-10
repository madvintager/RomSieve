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

    /// <summary>
    /// Files of a Redump disc: its descriptor and two tracks, all different unless the audio track is the same on every
    /// disc (<paramref name="sharedAudio"/>).
    /// </summary>
    private static Dictionary<string, byte[]> CdImage(string name, bool sharedAudio = false)
    {
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
            [name + " (Track 1).bin"] = Encoding.ASCII.GetBytes($"DATA TRACK OF {name}"),
            [name + " (Track 2).bin"] = Encoding.ASCII.GetBytes(sharedAudio ? "AUDIO TRACK OF EVERY DISC" : $"AUDIO TRACK OF {name}")
        };
    }

    /// <summary>A disc of the two-disc Redump game <see cref="Game"/>.</summary>
    private static Dictionary<string, byte[]> RedumpDisc(int disc, bool sharedAudio = false) =>
        CdImage($"{Game} (Disc {disc})", sharedAudio);

    private static IEnumerable<(string Name, byte[] Data)> Files(params Dictionary<string, byte[]>[] discs) =>
        discs.SelectMany(d => d).Select(f => (f.Key, f.Value));

    private static IEnumerable<(string Name, byte[] Data)> Without(Dictionary<string, byte[]> disc, string missing) =>
        disc.Where(f => !f.Key.Contains(missing, StringComparison.Ordinal)).Select(f => (f.Key, f.Value));

    private static string DatOf(IEnumerable<(string Game, Dictionary<string, byte[]> Files)> games, string? noDumpRom = null)
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

            if (noDumpRom is not null && game == $"{Game} (Disc 1)")
            {
                xml.AppendLine($"""    <rom name="{noDumpRom}" size="0" status="nodump"/>""");
            }

            xml.AppendLine("  </game>");
        }

        return xml.Append("</datafile>").ToString();
    }

    private SortConfig CreateConfig(
        IEnumerable<(string Game, Dictionary<string, byte[]> Files)> datGames,
        IEnumerable<(string Name, byte[] Data)> sourceFiles,
        string? noDumpRom = null,
        Action<SortConfig>? configure = null)
    {
        var src = _temp.CreateSubdirectory("src_" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var (name, data) in sourceFiles)
        {
            File.WriteAllBytes(Path.Combine(src, name), data);
        }

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = _temp.CreateSubdirectory("dst_" + Guid.NewGuid().ToString("N")[..8]),
            DatFile = _temp.WriteText(Guid.NewGuid().ToString("N")[..8] + ".dat", DatOf(datGames, noDumpRom))
        };
        config.Filters.CompleteMultidiskSetsOnly = true;
        config.Filters.NoIntroCompleteMultimediaSetsOnly = true;
        configure?.Invoke(config);
        return config;
    }

    private Task<SortExecutionResult> SimulateAsync(SortConfig config) => _services.Processor.ExecuteAsync(config, isDryRun: true);

    private Task<SortExecutionResult> SimulateAsync(
        IEnumerable<(string Game, Dictionary<string, byte[]> Files)> datGames,
        IEnumerable<(string Name, byte[] Data)> sourceFiles,
        string? noDumpRom = null,
        Action<SortConfig>? configure = null) =>
        SimulateAsync(CreateConfig(datGames, sourceFiles, noDumpRom, configure));

    private static ProcessItem ItemOf(SortExecutionResult result, string displayName) =>
        result.Items.Single(it => it.DisplayName == displayName);

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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SetMissingAWholeDisc_ShouldKeepTheReasonThatCountsDisks(int missingDisc)
    {
        var disc1 = RedumpDisc(1);
        var disc2 = RedumpDisc(2);

        // The names give no number of disks: it comes from the DAT, so that a missing last disc is seen too
        var result = await SimulateAsync(
            [($"{Game} (Disc 1)", disc1), ($"{Game} (Disc 2)", disc2)],
            (missingDisc == 1 ? disc2 : disc1).Select(f => (f.Key, f.Value)));

        var descriptor = result.Items.Single(it => it.DisplayName.EndsWith(".cue", StringComparison.Ordinal));
        descriptor.Status.Should().Be(FileStatus.Excluded);
        descriptor.StatusReason!.Key.Should().Be("reason.incomplete_multidisk_set");
        _services.I18n.Format(descriptor.StatusReason).Should().Be("Set multi-disquettes incomplet (1/2 disques trouvés)");
    }

    [Fact]
    public async Task TrackSharedWithAnotherDisc_ShouldNotCompleteTheDiscThatLacksIt()
    {
        // The audio track of disc 2 is identical to that of disc 1, which is present: disc 2 still lacks its own
        var disc1 = RedumpDisc(1, sharedAudio: true);
        var disc2 = RedumpDisc(2, sharedAudio: true);
        var partialDisc2 = disc2.Where(f => !f.Key.Contains("(Track 2)", StringComparison.Ordinal));

        var result = await SimulateAsync(
            [($"{Game} (Disc 1)", disc1), ($"{Game} (Disc 2)", disc2)],
            disc1.Concat(partialDisc2).Select(f => (f.Key, f.Value)));

        var descriptor2 = result.Items.Single(it => it.DisplayName == $"{Game} (Disc 2).cue");
        descriptor2.Status.Should().Be(FileStatus.Excluded);
        _services.I18n.Format(descriptor2.StatusReason).Should().Be("Disque incomplet : 2/3 fichiers du DAT trouvés");
        result.Items.Should().OnlyContain(it => it.Status == FileStatus.Excluded, "the set is incomplete without disc 2");
    }

    [Fact]
    public async Task RomNeverDumped_ShouldNotMakeTheDiscIncomplete()
    {
        var disc1 = RedumpDisc(1);
        var disc2 = RedumpDisc(2);

        var result = await SimulateAsync(
            [($"{Game} (Disc 1)", disc1), ($"{Game} (Disc 2)", disc2)],
            disc1.Concat(disc2).Select(f => (f.Key, f.Value)),
            noDumpRom: $"{Game} (Disc 1) (Track 3).bin");

        result.Items.Should().NotBeEmpty();
        result.Items.Should().OnlyContain(it => it.Status == FileStatus.Valid);
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

    // =========================================================================
    // Games of a single disc or of a single entry of the DAT
    // =========================================================================

    private const string Solo = "Solo Game (Europe)";
    private const string SoloUsa = "Solo Game (USA)";

    [Fact]
    public async Task SingleDiscGame_WithOnlyOneTrack_ShouldBeExcludedAsAnIncompleteDisc()
    {
        var solo = CdImage(Solo);
        var track = $"{Solo} (Track 2).bin";

        var result = await SimulateAsync([(Solo, solo)], [(track, solo[track])]);

        var item = result.Items.Single();
        item.Status.Should().Be(FileStatus.Excluded);
        _services.I18n.Format(item.StatusReason).Should().Be("Disque incomplet : 1/3 fichiers du DAT trouvés");
    }

    [Fact]
    public async Task SingleDiscGame_WithItsDescriptorButNotEveryTrack_ShouldBeExcludedWithItsTracks()
    {
        var solo = CdImage(Solo);

        var result = await SimulateAsync([(Solo, solo)], Without(solo, "(Track 2)"));

        var descriptor = ItemOf(result, Solo + ".cue");
        descriptor.Status.Should().Be(FileStatus.Excluded);
        _services.I18n.Format(descriptor.StatusReason).Should().Be("Disque incomplet : 2/3 fichiers du DAT trouvés");
        result.Items.Should().OnlyContain(it => it.Status == FileStatus.Excluded);
    }

    [Fact]
    public async Task SingleDiscGame_Complete_ShouldBeKept()
    {
        var solo = CdImage(Solo);

        var result = await SimulateAsync([(Solo, solo)], Files(solo));

        ItemOf(result, Solo + ".cue").Status.Should().Be(FileStatus.Valid);
    }

    [Fact]
    public async Task IncompleteDisc_WithoutCompleteSetsOnly_ShouldBeKept()
    {
        var solo = CdImage(Solo);

        var result = await SimulateAsync([(Solo, solo)], Without(solo, "(Track 2)"), configure: c =>
        {
            c.Filters.CompleteMultidiskSetsOnly = false;
            c.Filters.NoIntroCompleteMultimediaSetsOnly = false;
        });

        ItemOf(result, Solo + ".cue").Status.Should().Be(FileStatus.Valid);
    }

    [Fact]
    public async Task TosecGameMissingAFile_ShouldBeExcludedAsAnIncompleteGame()
    {
        const string tosecGame = "Alpha (1990)(Ocean)";
        var program = "ALPHA PROGRAM DISK"u8.ToArray();
        var data = "ALPHA DATA DISK"u8.ToArray();
        var src = _temp.CreateSubdirectory("src_tosec");
        File.WriteAllBytes(Path.Combine(src, tosecGame + ".adf"), program);
        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = _temp.CreateSubdirectory("dst_tosec"),
            DatFile = _temp.WriteText("tosec.dat", $"""
                <?xml version="1.0"?>
                <datafile>
                  <header><name>Commodore Amiga - Games - [ADF] (TOSEC)</name><description>TOSEC test DAT</description></header>
                  <game name="{tosecGame}"><description>{tosecGame}</description>
                    <rom name="{tosecGame}.adf" size="{program.Length}" crc="{Crc(program)}"/>
                    <rom name="{tosecGame}(Data).adf" size="{data.Length}" crc="{Crc(data)}"/>
                  </game>
                </datafile>
                """)
        };
        config.Filters.CompleteMultidiskSetsOnly = true;

        var result = await SimulateAsync(config);

        var item = result.Items.Single();
        item.Parsed.Convention.Should().Be(NamingConvention.Tosec);
        item.Status.Should().Be(FileStatus.Excluded);
        _services.I18n.Format(item.StatusReason).Should().Be("Jeu incomplet : 1/2 fichiers du DAT trouvés");
    }

    [Fact]
    public async Task IncompletePreferredVersion_ShouldLeaveThe1G1RToACompleteOne()
    {
        var europe = CdImage(Solo);
        var usa = CdImage(SoloUsa);

        var result = await SimulateAsync(
            [(Solo, europe), (SoloUsa, usa)],
            Without(europe, "(Track 2)").Concat(Files(usa)),
            configure: c =>
            {
                c.Filters.OneGameOneRom = true;
                c.Filters.PriorityCountries = ["EU", "US"];
            });

        ItemOf(result, SoloUsa + ".cue").Status.Should().Be(FileStatus.Valid);
        ItemOf(result, SoloUsa + ".cue").Is1G1RWinner.Should().BeTrue();
        var europeDisc = ItemOf(result, Solo + ".cue");
        europeDisc.Status.Should().Be(FileStatus.Excluded);
        europeDisc.StatusReason!.Key.Should().Be("reason.incomplete_disc");
    }

    [Fact]
    public async Task ForcedWinner_ShouldBeKeptEvenWhenItsDiscIsIncomplete()
    {
        var europe = CdImage(Solo);
        var usa = CdImage(SoloUsa);
        var config = CreateConfig(
            [(Solo, europe), (SoloUsa, usa)],
            Without(europe, "(Track 2)").Concat(Files(usa)),
            configure: c =>
            {
                c.Filters.OneGameOneRom = true;
                c.Filters.PriorityCountries = ["EU", "US"];
            });
        var first = await SimulateAsync(config);
        var europeDisc = ItemOf(first, Solo + ".cue");
        config.Filters.Manual1G1RWinners[_services.FilterService.SerializeCanonicalGameKey(europeDisc.Parsed)] = europeDisc.DisplayName;

        var result = await SimulateAsync(config);

        ItemOf(result, Solo + ".cue").Status.Should().Be(FileStatus.Valid);
        ItemOf(result, SoloUsa + ".cue").Status.Should().Be(FileStatus.Excluded);
    }

    [Fact]
    public async Task Recalculation_ShouldReproduceTheSimulation_WithTheTracksOfEachDescriptor()
    {
        var europe = CdImage(Solo);
        var usa = CdImage(SoloUsa);
        var disc1 = RedumpDisc(1);
        var disc2 = RedumpDisc(2);
        var config = CreateConfig(
            [(Solo, europe), (SoloUsa, usa), ($"{Game} (Disc 1)", disc1), ($"{Game} (Disc 2)", disc2)],
            Without(europe, "(Track 2)").Concat(Files(usa, disc1, disc2)),
            configure: c =>
            {
                c.Filters.OneGameOneRom = true;
                c.Filters.PriorityCountries = ["EU", "US"];
            });
        var result = await SimulateAsync(config);
        var expected = result.Items.ToDictionary(it => it.DisplayName, it => (it.Status, it.StatusReason?.Key));
        expected[$"{Game} (Disc 1).cue"].Status.Should().Be(FileStatus.Valid);
        expected[Solo + ".cue"].Key.Should().Be("reason.incomplete_disc");

        foreach (var game in result.Items.GroupBy(it => it.Parsed.Title))
        {
            var gameItems = game.ToList();
            _services.Processor.RecalculateGameItems(gameItems, config, result.DatCatalog);

            gameItems.Select(it => (it.DisplayName, it.Status, it.StatusReason?.Key)).Should().BeEquivalentTo(
                gameItems.Select(it => (it.DisplayName, expected[it.DisplayName].Status, expected[it.DisplayName].Key)),
                because: $"game '{game.Key}' must not change on recalculation");
        }
    }

    [Fact]
    public async Task IncompletePressing_ShouldNotBeBorrowed_WhenAnotherPressingIsComplete()
    {
        const string saga = "Saga (Japan)";
        var disc1 = CdImage($"{saga} (Disc 1)");
        var firstPressing = CdImage($"{saga} (Disc 2) (4M)");
        var secondPressing = CdImage($"{saga} (Disc 2) (5M)");
        var disc3 = CdImage($"{saga} (Disc 3)");

        var result = await SimulateAsync(
            [
                ($"{saga} (Disc 1)", disc1), ($"{saga} (Disc 2) (4M)", firstPressing),
                ($"{saga} (Disc 2) (5M)", secondPressing), ($"{saga} (Disc 3)", disc3)
            ],
            Files(disc1, secondPressing, disc3).Concat(Without(firstPressing, "(Track 2)")),
            configure: c => c.Filters.SmartMultidiskGrouping = true);

        ItemOf(result, $"{saga} (Disc 1).cue").Status.Should().Be(FileStatus.Valid);
        ItemOf(result, $"{saga} (Disc 3).cue").Status.Should().Be(FileStatus.Valid);
        ItemOf(result, $"{saga} (Disc 2) (5M).cue").Status.Should().Be(FileStatus.Valid);
        var partial = ItemOf(result, $"{saga} (Disc 2) (4M).cue");
        partial.Status.Should().Be(FileStatus.Excluded);
        partial.StatusReason!.Key.Should().Be("reason.incomplete_disc");
    }
}
