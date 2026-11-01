using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Criterion of the 1G1R cascade that discarded each version (<see cref="ProcessItem.EliminatedBy"/>), and the games
/// each criterion decided in a run (<see cref="TiebreakDecisions"/>).
/// </summary>
public sealed class TiebreakDecisionsTests : IDisposable
{
    private readonly TempDirectoryFixture _tmp = new();
    private readonly CoreTestServices _services = new();

    public void Dispose() => _tmp.Dispose();

    private SortConfig CreateCollection()
    {
        foreach (var name in new[]
                 {
                     "Alpha (USA).sfc", "Alpha (Japan).sfc",
                     "Bravo (USA) (v1.0).sfc", "Bravo (USA) (v1.1).sfc",
                     "Charlie (USA).sfc", "Charlie (Japan).sfc",
                     "Delta (USA).sfc"
                 })
        {
            _tmp.WriteText(Path.Combine("src", name), name);
        }

        return new SortConfig
        {
            SourceDir = Path.Combine(_tmp.Path, "src"),
            DestinationDir = _tmp.GetFilePath("dest"),
            NamingConvention = NamingConvention.NoIntro,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                PriorityLanguages = ["en"]
            }
        };
    }

    private static ProcessItem ItemOf(SortExecutionResult result, string name) =>
        result.Items.Single(it => Path.GetFileName(it.SourcePath) == name);

    [Fact]
    public async Task DiscardedVersions_ShouldRecordTheCriterionThatDecided()
    {
        var result = await _services.Processor.ExecuteAsync(CreateCollection(), isDryRun: true);

        ItemOf(result, "Alpha (Japan).sfc").EliminatedBy.Should().Be(TiebreakCriterion.LanguageRank);
        ItemOf(result, "Bravo (USA) (v1.0).sfc").EliminatedBy.Should().Be(TiebreakCriterion.Version);
        ItemOf(result, "Alpha (USA).sfc").EliminatedBy.Should().Be(TiebreakCriterion.None, "a winner was not discarded");
        ItemOf(result, "Delta (USA).sfc").EliminatedBy.Should().Be(TiebreakCriterion.None, "a game of one version is decided by nothing");
    }

    [Fact]
    public async Task DecidedGames_ShouldCountEachGameOncePerCriterion()
    {
        var result = await _services.Processor.ExecuteAsync(CreateCollection(), isDryRun: true);

        TiebreakDecisions.CountDecidedGames(result.Items, _services.FilterService).Should().BeEquivalentTo(
            new Dictionary<TiebreakCriterion, int>
            {
                [TiebreakCriterion.LanguageRank] = 2,
                [TiebreakCriterion.Version] = 1
            });
    }

    [Fact]
    public async Task ForcedWinner_ShouldBeDecidedByNoCriterion()
    {
        var config = CreateCollection();
        var key = _services.FilterService.SerializeCanonicalGameKey(_services.NoIntroParser.ParseRomFilename("Alpha (USA).sfc"));
        config.Filters.Manual1G1RWinners = new Dictionary<string, string> { [key] = "Alpha (Japan).sfc" };

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);

        ItemOf(result, "Alpha (USA).sfc").EliminatedBy.Should().Be(TiebreakCriterion.None);
        TiebreakDecisions.CountDecidedGames(result.Items, _services.FilterService)[TiebreakCriterion.LanguageRank].Should().Be(1);
    }

    [Fact]
    public async Task Recalculation_ShouldRecordTheSameCriteriaAsTheSimulation()
    {
        var config = CreateCollection();
        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);
        var bravo = result.Items.Where(it => Path.GetFileName(it.SourcePath).StartsWith("Bravo", StringComparison.Ordinal)).ToList();
        bravo.ForEach(it => it.EliminatedBy = TiebreakCriterion.Name);

        _services.Processor.RecalculateGameItems(bravo, config, allItems: result.Items);

        ItemOf(result, "Bravo (USA) (v1.0).sfc").EliminatedBy.Should().Be(TiebreakCriterion.Version);
        ItemOf(result, "Bravo (USA) (v1.1).sfc").EliminatedBy.Should().Be(TiebreakCriterion.None);
    }

    [Fact]
    public async Task WithoutOneGameOneRom_NothingShouldBeDecided()
    {
        var config = CreateCollection();
        config.Filters.OneGameOneRom = false;

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);

        TiebreakDecisions.CountDecidedGames(result.Items, _services.FilterService).Should().BeEmpty();
    }
}
