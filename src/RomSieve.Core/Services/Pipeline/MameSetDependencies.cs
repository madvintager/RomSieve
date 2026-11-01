using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Sets a MAME set needs to run (<see cref="MameSetInfo.RequiredSets"/>: its BIOS, its devices, the parent of a split
/// clone), followed from set to set (a BIOS may need devices too). Shared by the release selection, which keeps them
/// with the sets that need them, and by the partial sort, which transfers them with the games chosen.
/// </summary>
public static class MameSetDependencies
{
    /// <summary>Number of set names a message lists before counting the others.</summary>
    public const int MaxListedNames = 3;

    /// <summary>
    /// Sets of <paramref name="items"/> that the sets of <paramref name="roots"/> need, directly or through another
    /// needed set, in the order they are found, each with every root or needed set that needs it directly (short names,
    /// in alphabetical order). The roots themselves are left out.
    /// </summary>
    public static IReadOnlyList<(ProcessItem Item, IReadOnlyList<string> RequiredBy)> FindRequiredSets(
        IEnumerable<ProcessItem> items,
        IEnumerable<ProcessItem> roots)
    {
        var setsByGame = new Dictionary<string, List<(ProcessItem Item, MameSetInfo Set)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item.MameSet is { } set)
            {
                if (!setsByGame.TryGetValue(set.SetKey, out var list))
                {
                    list = [];
                    setsByGame[set.SetKey] = list;
                }
                list.Add((item, set));
            }
        }

        var rootSet = new HashSet<ProcessItem>(ReferenceEqualityComparer.Instance);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<MameSetInfo>();
        foreach (var root in roots)
        {
            if (root.MameSet is { } set && rootSet.Add(root))
            {
                visited.Add(set.SetKey);
                queue.Enqueue(set);
            }
        }

        // Every set that needs a required set is recorded, not only the first found
        var requiredNames = new List<string>();
        var requiredBy = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var requiredName in current.RequiredSets)
            {
                // A software needs sets of its own list only
                var name = MameSetInfo.SetKeyOf(current.SoftwareList, requiredName);
                if (!setsByGame.TryGetValue(name, out var sets))
                {
                    continue;
                }

                if (!requiredBy.TryGetValue(name, out var needers))
                {
                    needers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    requiredBy[name] = needers;
                }
                needers.Add(current.GameName);

                if (!visited.Add(name))
                {
                    continue;
                }

                requiredNames.Add(name);
                foreach (var (_, set) in sets)
                {
                    queue.Enqueue(set);
                }
            }
        }

        var required = new List<(ProcessItem Item, IReadOnlyList<string> RequiredBy)>();
        foreach (var name in requiredNames)
        {
            var needers = requiredBy[name].ToList();
            foreach (var (item, _) in setsByGame[name])
            {
                if (!rootSet.Contains(item))
                {
                    required.Add((item, needers));
                }
            }
        }

        return required;
    }

    /// <summary>
    /// Reason of a set kept because kept sets need it: the set that needs it, or the sets that need it (the first
    /// <see cref="MaxListedNames"/>, the others counted).
    /// </summary>
    public static LocalizedMessage DescribeRequiredSetReason(IReadOnlyList<string> requiredBy) =>
        requiredBy.Count == 1
            ? LocalizedMessage.Create(RequiredSetReasonKey, ("set", requiredBy[0]))
            : LocalizedMessage.Create(RequiredSetsReasonKey, ("names", FormatNames(requiredBy)));

    /// <summary>Whether a reason is that of a set kept because kept sets need it.</summary>
    public static bool IsRequiredSetReason(LocalizedMessage? reason) =>
        reason?.Key is RequiredSetReasonKey or RequiredSetsReasonKey;

    /// <summary>
    /// Short names of sets, separated by commas: at most <see cref="MaxListedNames"/>, then the number of the others
    /// (<c>2020bb, 3countb, aof and 149 others</c>).
    /// </summary>
    public static LocalizedMessage FormatNames(IReadOnlyList<string> names) =>
        names.Count <= MaxListedNames
            ? LocalizedMessage.Verbatim(string.Join(", ", names))
            : LocalizedMessage.Create(
                "list.names_and_others",
                ("names", string.Join(", ", names.Take(MaxListedNames))),
                ("count", names.Count - MaxListedNames));

    private const string RequiredSetReasonKey = "reason.mame_required_set";
    private const string RequiredSetsReasonKey = "reason.mame_required_sets";
}
