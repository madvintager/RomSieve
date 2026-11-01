namespace RomSieve.Core.Models;

/// <summary>
/// One game, or one non-conforming file, chosen by a partial sort (<see cref="SortSelection"/>).
/// </summary>
/// <param name="Key">Selection key of the unit (<c>SelectionFilter.GetUnitKey</c>): a game, or a non-conforming file.</param>
/// <param name="Label">Name shown in the log: the title of the game, or the name of the file.</param>
/// <param name="ExpectedItemKeys">
/// Files the simulation was going to write for this unit (<see cref="SortSelection.GetItemKey"/>), or <c>null</c> to
/// write whatever the run keeps without comparing it with a simulation. When the run keeps other files (another
/// release, a missing disk), nothing is written for the unit.
/// </param>
/// <param name="RequiredBy">
/// When the unit was not chosen but is a MAME set that a chosen set needs to run (its BIOS, a device), transferred with
/// it: the short names of the chosen or needed sets that need it.
/// </param>
public sealed record SortSelectionUnit(string Key, string Label, IReadOnlySet<string>? ExpectedItemKeys = null, IReadOnlyList<string>? RequiredBy = null)
{
    /// <summary>Whether the unit was added because a chosen MAME set needs it (<see cref="RequiredBy"/>).</summary>
    public bool IsRequiredSet => RequiredBy is not null;
}

/// <summary>
/// Games and non-conforming files a partial sort transfers. The whole collection is still scanned, classified and
/// resolved (1G1R, multi-disk sets) as for a full sort: the selection only filters the transfers, so the release kept
/// for a game never depends on it. A run data, never an option: it is not part of <see cref="SortConfig"/> nor of the
/// saved settings.
/// </summary>
public sealed class SortSelection
{
    private readonly Dictionary<string, SortSelectionUnit> _units;

    public SortSelection(IEnumerable<SortSelectionUnit> units)
    {
        _units = new Dictionary<string, SortSelectionUnit>(StringComparer.Ordinal);
        foreach (var unit in units)
        {
            _units.TryAdd(unit.Key, unit);
        }
    }

    /// <summary>Chosen games and non-conforming files, in the order they were given.</summary>
    public IReadOnlyCollection<SortSelectionUnit> Units => _units.Values;

    /// <summary>Number of chosen games and non-conforming files.</summary>
    public int Count => _units.Count;

    public bool Contains(string unitKey) => _units.ContainsKey(unitKey);

    public bool TryGetUnit(string unitKey, out SortSelectionUnit unit) =>
        _units.TryGetValue(unitKey, out unit!);

    /// <summary>
    /// Identity of a file across runs: the source path and, for a file read from an archive, the member name.
    /// </summary>
    public static string GetItemKey(ProcessItem item) =>
        string.IsNullOrEmpty(item.ArchiveMember) ? item.SourcePath : $"{item.SourcePath}|{item.ArchiveMember}";
}
