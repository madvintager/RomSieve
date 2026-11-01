using System.IO;
using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// Release selection (1G1R, language and country filters, manual winners) must give the same
/// result whether it runs in a full simulation (<see cref="SortProcessorService.ExecuteAsync"/>)
/// or in the interactive per-game recalculation (<see cref="SortProcessorService.RecalculateGameItems"/>).
/// </summary>
public sealed class ReleaseSelectionTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("fr");

    private const string FlashbackFr = "Flashback (1992)(Delphine)(FR)[!].adf";
    private const string FlashbackUs = "Flashback (1992)(Delphine)(US)[!].adf";
    private const string FlashbackCrack = "Flashback (1992)(Delphine)(FR)[cr FLT].adf";
    private const string MonkeyFr1 = "Monkey Island (1990)(Lucasfilm)(FR)(Disk 1 of 2).adf";
    private const string MonkeyFr2 = "Monkey Island (1990)(Lucasfilm)(FR)(Disk 2 of 2).adf";
    private const string MonkeyDe1 = "Monkey Island (1990)(Lucasfilm)(DE)(Disk 1 of 2).adf";
    private const string MonkeyDe2 = "Monkey Island (1990)(Lucasfilm)(DE)(Disk 2 of 2).adf";
    private const string ZoolDe1 = "Zool (1992)(Gremlin)(DE)(Disk 1 of 2).adf";
    private const string ZoolUs1 = "Zool (1992)(Gremlin)(US)(Disk 1 of 2).adf";
    private const string ZoolUs2 = "Zool (1992)(Gremlin)(US)(Disk 2 of 2).adf";
    private const string AnotherWorldDe = "Another World (1991)(Delphine)(DE).adf";
    private const string AnotherWorldUs = "Another World (1991)(Delphine)(US).adf";
    private const string AnotherWorldFr = "Another World (1991)(Delphine)(FR)(fr).adf";

    private static readonly string[] AllFiles =
    [
        FlashbackFr, FlashbackUs, FlashbackCrack,
        MonkeyFr1, MonkeyFr2, MonkeyDe1, MonkeyDe2,
        ZoolDe1, ZoolUs1, ZoolUs2,
        AnotherWorldDe, AnotherWorldUs, AnotherWorldFr
    ];

    public void Dispose() => _temp.Dispose();

    private sealed record ItemSnapshot(
        string Name,
        FileStatus Status,
        LocalizedMessage? StatusReason,
        bool IsWinner,
        string? WinnerReference,
        LocalizedMessage? EliminationDetail,
        bool IsMultidiskComplete);

    private static ItemSnapshot Snapshot(ProcessItem it) => new(
        it.DisplayName,
        it.Status,
        it.StatusReason,
        it.Is1G1RWinner,
        it.WinnerReference,
        it.EliminationDetail,
        it.IsMultidiskComplete);

    private SortConfig CreateConfig(Action<FilterConfig> configure)
    {
        var src = _temp.CreateSubdirectory("src_" + Guid.NewGuid().ToString("N")[..8]);
        var dst = _temp.CreateSubdirectory("dst_" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var name in AllFiles)
        {
            File.WriteAllText(Path.Combine(src, name), name);
        }

        var filters = new FilterConfig();
        configure(filters);
        return new SortConfig { SourceDir = src, DestinationDir = dst, Filters = filters };
    }

    private string GameKey(string title) =>
        _services.FilterService.SerializeCanonicalGameKey(_services.NoIntroParser.ParseRomFilename(title));

    public static TheoryData<string> Scenarios() =>
    [
        "1g1r_lang_first",
        "1g1r_country_first",
        "filters_lang_first",
        "filters_country_first",
        "filters_with_manual_winner",
        "1g1r_with_manual_winner_in_incomplete_set"
    ];

    private SortConfig CreateScenario(string scenario) => scenario switch
    {
        "1g1r_lang_first" => CreateConfig(f =>
        {
            f.OneGameOneRom = true;
            f.PriorityLanguages = ["fr", "en", "de"];
        }),
        "1g1r_country_first" => CreateConfig(f =>
        {
            f.OneGameOneRom = true;
            f.RegionPriorityMode = RegionPriorityMode.CountryFirst;
            f.PriorityLanguages = ["en"];
            f.PriorityCountries = ["US", "FR"];
        }),
        "filters_lang_first" => CreateConfig(f =>
        {
            f.FilterLanguages = true;
            f.FilterCountries = true;
            f.PriorityLanguages = ["fr"];
            f.PriorityCountries = ["DE"];
        }),
        "filters_country_first" => CreateConfig(f =>
        {
            f.RegionPriorityMode = RegionPriorityMode.CountryFirst;
            f.FilterLanguages = true;
            f.FilterCountries = true;
            f.PriorityLanguages = ["fr"];
            f.PriorityCountries = ["DE"];
            f.FallbackUnmatchedLanguages = false;
        }),
        "filters_with_manual_winner" => CreateConfig(f =>
        {
            f.FilterLanguages = true;
            f.PriorityLanguages = ["fr"];
            f.Manual1G1RWinners[GameKey(FlashbackCrack)] = FlashbackCrack;
            f.Manual1G1RWinners[GameKey(MonkeyDe1)] = MonkeyDe1;
        }),
        "1g1r_with_manual_winner_in_incomplete_set" => CreateConfig(f =>
        {
            f.OneGameOneRom = true;
            f.PriorityLanguages = ["en"];
            f.CompleteMultidiskSetsOnly = true;
            f.Manual1G1RWinners[GameKey(ZoolDe1)] = ZoolDe1;
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario))
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task InteractiveRecalculation_ShouldReproduceTheSimulationOfEachGame(string scenario)
    {
        var config = CreateScenario(scenario);
        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);
        var expected = result.Items.ToDictionary(it => it.DisplayName, Snapshot);
        expected.Should().HaveCount(AllFiles.Length);

        foreach (var game in result.Items.GroupBy(it => it.Parsed.Title))
        {
            var gameItems = game.ToList();
            _services.Processor.RecalculateGameItems(gameItems, config);

            gameItems.Select(Snapshot).Should().BeEquivalentTo(
                gameItems.Select(it => expected[it.DisplayName]),
                because: $"game '{game.Key}' of scenario '{scenario}' must not change on recalculation");
        }
    }

    [Fact]
    public async Task ManualWinner_ShouldBeKeptWhenTheMultidiskEvaluationExcludesItsIncompleteSet()
    {
        var config = CreateScenario("1g1r_with_manual_winner_in_incomplete_set");

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);

        // The forced disk is excluded by the multi-disk evaluation (set 1 of 2), then promoted back
        var zool = result.Items.Where(it => it.Parsed.Title == "Zool").ToDictionary(it => it.DisplayName);
        zool[ZoolDe1].Status.Should().Be(FileStatus.Valid);
        zool[ZoolDe1].Is1G1RWinner.Should().BeTrue();
        zool[ZoolUs1].Status.Should().Be(FileStatus.Excluded);
        zool[ZoolUs2].Status.Should().Be(FileStatus.Excluded);
    }

    [Fact]
    public async Task ManualWinner_ExcludedByAFilter_ShouldCountInTheMultidiskEvaluationOfItsSet()
    {
        var src = _temp.CreateSubdirectory("src_forced_crack");
        var dst = _temp.CreateSubdirectory("dst_forced_crack");
        const string crackDisk1 = "Lemmings (1991)(Psygnosis)(DE)(Disk 1 of 2)[cr NBC].adf";
        const string cleanDisk1 = "Lemmings (1991)(Psygnosis)(US)(Disk 1 of 2).adf";
        const string cleanDisk2 = "Lemmings (1991)(Psygnosis)(US)(Disk 2 of 2).adf";
        foreach (var name in new[] { crackDisk1, cleanDisk1, cleanDisk2 })
        {
            File.WriteAllText(Path.Combine(src, name), name);
        }

        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Filters = new FilterConfig
            {
                ExcludeModifications = true,
                SmartMultidiskGrouping = false,
                OneGameOneRom = true,
                PriorityLanguages = ["en"]
            }
        };
        config.Filters.Manual1G1RWinners[GameKey(crackDisk1)] = crackDisk1;

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);

        // The crack is promoted before the multi-disk evaluation, which sees its set as incomplete
        var forced = result.Items.Single(it => it.DisplayName == crackDisk1);
        forced.Status.Should().Be(FileStatus.Valid);
        forced.Is1G1RWinner.Should().BeTrue();
        forced.IsMultidiskComplete.Should().BeFalse();
    }

    [Fact]
    public async Task ManualWinner_ShouldOverrideLanguageFiltersWithout1G1R()
    {
        var config = CreateScenario("filters_with_manual_winner");

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);

        var valid = result.Items.Where(it => it.Status == FileStatus.Valid).Select(it => it.DisplayName);
        valid.Should().BeEquivalentTo(
            [
                FlashbackCrack,             // Forced, although excluded as a modification
                MonkeyDe1, MonkeyDe2,       // Forced, although not in a priority language
                AnotherWorldFr,             // Language filter only
                ZoolDe1, ZoolUs1, ZoolUs2   // No French version: fallback keeps everything
            ]);
    }
}
