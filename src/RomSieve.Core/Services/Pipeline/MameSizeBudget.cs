using System.Globalization;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>Outcome of the size budget of a MAME run: the bytes of the kept sets, and the sets left out.</summary>
public sealed record MameSizeBudgetResult(long BudgetBytes, long UsedBytes, int KeptCount, IReadOnlyList<ProcessItem> Excluded);

/// <summary>
/// Size budget of a MAME run (<see cref="FilterConfig.MameSizeBudgetBytes"/>): fills it with the kept sets, the best
/// rated first, then the smallest (more games), then by name. A set counts with its CHDs and with the sets it needs
/// (BIOS, devices, parent of a split clone), each counted once whatever the number of sets that need it. A set that no
/// longer fits is excluded, and the following, smaller ones may still fit. Called by <see cref="ReleaseSelector"/>
/// before it keeps the sets the kept sets need, which the budget already counted.
/// </summary>
public static class MameSizeBudget
{
    private const string ReasonKey = "reason.mame_budget_exceeded";

    public static MameSizeBudgetResult? Apply(IReadOnlyCollection<ProcessItem> items, long budgetBytes)
    {
        // The sets a previous budget left out compete again: a recalculation changes what fits
        foreach (var item in items.Where(it => it.IsOutsideBudget))
        {
            item.IsOutsideBudget = false;
            if (item.Status == FileStatus.Excluded && item.StatusReason?.Key == ReasonKey)
            {
                item.Status = FileStatus.Valid;
                item.StatusReason = null;
            }
        }

        if (budgetBytes <= 0)
        {
            return null;
        }

        var setsByGame = new Dictionary<string, List<ProcessItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item.MameSet is { } set)
            {
                if (!setsByGame.TryGetValue(set.SetKey, out var list))
                {
                    list = [];
                    setsByGame[set.SetKey] = list;
                }
                list.Add(item);
            }
        }

        // The sets kept for their own sake; those kept because another needs them come with it
        var candidates = items
            .Where(it => it.Status == FileStatus.Valid && it.MameSet is not null && !MameSetDependencies.IsRequiredSetReason(it.StatusReason))
            .OrderByDescending(it => it.Parsed.Machine?.Rating?.Score ?? -1)
            .ThenBy(SizeOf)
            .ThenBy(it => it.MameSet!.GameName, StringComparer.Ordinal)
            .ToList();

        var counted = new HashSet<ProcessItem>(ReferenceEqualityComparer.Instance);
        var excluded = new List<ProcessItem>();
        long used = 0;
        var kept = 0;
        foreach (var candidate in candidates)
        {
            if (counted.Contains(candidate))
            {
                // Already counted as a set another kept set needs (the parent of a split clone)
                kept++;
                continue;
            }

            var unit = new List<ProcessItem> { candidate };
            unit.AddRange(RequiredSets(candidate, setsByGame).Where(it => !counted.Contains(it)));
            var cost = unit.Sum(SizeOf);
            if (used + cost <= budgetBytes)
            {
                used += cost;
                kept++;
                counted.UnionWith(unit);
                continue;
            }

            candidate.Status = FileStatus.Excluded;
            candidate.StatusReason = LocalizedMessage.Create(ReasonKey, ("size", FormatGigabytes(cost)), ("budget", FormatGigabytes(budgetBytes)));
            candidate.IsOutsideBudget = true;
            excluded.Add(candidate);
        }

        return new MameSizeBudgetResult(budgetBytes, used, kept, excluded);
    }

    /// <summary>Gigabytes of 10^9 bytes, as the budget and the memory cards count them, with at most three decimals.</summary>
    public static string FormatGigabytes(long bytes) =>
        (bytes / 1_000_000_000d).ToString("0.###", CultureInfo.InvariantCulture);

    private static long SizeOf(ProcessItem item) => item.FileSize + item.RelatedItems.Sum(r => r.FileSize);

    // Sets a set needs to run, directly or through another needed set
    private static IEnumerable<ProcessItem> RequiredSets(ProcessItem root, Dictionary<string, List<ProcessItem>> setsByGame)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.MameSet!.SetKey };
        var pending = new Stack<MameSetInfo>([root.MameSet]);
        while (pending.TryPop(out var current))
        {
            foreach (var requiredName in current.RequiredSets)
            {
                var name = MameSetInfo.SetKeyOf(current.SoftwareList, requiredName);
                if (!visited.Add(name) || !setsByGame.TryGetValue(name, out var sets))
                {
                    continue;
                }

                foreach (var item in sets)
                {
                    yield return item;
                    pending.Push(item.MameSet!);
                }
            }
        }
    }
}
