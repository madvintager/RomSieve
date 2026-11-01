using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// What each criterion of the 1G1R cascade decided in a run: the number of games in which it set the winner apart
/// from at least one other version (<see cref="ProcessItem.EliminatedBy"/>). A game counts once per criterion, and
/// for each criterion that discarded one of its versions.
/// </summary>
public static class TiebreakDecisions
{
    /// <summary>Games decided by each criterion among <paramref name="items"/>; a criterion that decided nothing is absent.</summary>
    public static IReadOnlyDictionary<TiebreakCriterion, int> CountDecidedGames(IEnumerable<ProcessItem> items, IGameKeyService gameKeys)
    {
        var games = new HashSet<(TiebreakCriterion Criterion, string GameKey)>();
        foreach (var item in items)
        {
            // A version kept in the end (a MAME set another one needs) was not discarded
            if (item.EliminatedBy != TiebreakCriterion.None && item.Status != FileStatus.Valid)
            {
                games.Add((item.EliminatedBy, gameKeys.SerializeCanonicalGameKey(item.Parsed)));
            }
        }

        return games
            .GroupBy(g => g.Criterion)
            .ToDictionary(g => g.Key, g => g.Count());
    }
}
