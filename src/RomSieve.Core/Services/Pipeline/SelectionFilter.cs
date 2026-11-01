using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Partial sort: keeps for the transfers only the games and non-conforming files of a <see cref="SortSelection"/>.
/// Runs after <see cref="ReleaseSelector"/>, so that the release kept for a game never depends on the selection, and
/// before <see cref="FileTransferExecutor"/>. A game is chosen as a whole: all its disks (borrowed ones included),
/// whose companion tracks belong to their descriptor, and therefore its <c>.m3u</c> playlist.
/// </summary>
public sealed class SelectionFilter
{
    private const string GameKeyPrefix = "game\u001f";
    private const string FileKeyPrefix = "file\u001f";

    private readonly SortSelection _selection;
    private readonly IGameKeyService _gameKeys;
    private readonly Action<LogLevel, LocalizedMessage> _log;

    public SelectionFilter(SortSelection selection, IGameKeyService gameKeys, Action<LogLevel, LocalizedMessage> log)
    {
        _selection = selection;
        _gameKeys = gameKeys;
        _log = log;
    }

    /// <summary>
    /// Whether a sort writes the file to the destination: kept, or non-conforming and isolated in <c>_unmatched</c>
    /// (not when <see cref="SortConfig.IsolateUnmatched"/> is off, <see cref="ProcessItem.IsNotIsolated"/>).
    /// </summary>
    public static bool IsWritten(ProcessItem item) => IsWritten(item.Status, item);

    /// <summary>Same as <see cref="IsWritten(ProcessItem)"/>, for the status the release selection gave the file.</summary>
    public static bool IsReleaseWritten(ProcessItem item) => IsWritten(item.ReleaseStatus ?? item.Status, item);

    private static bool IsWritten(FileStatus status, ProcessItem item) =>
        status == FileStatus.Valid || (status == FileStatus.Unmatched && !item.IsNotIsolated);

    /// <summary>
    /// Selection unit of a file: its game (canonical game key) when it belongs to it (<see cref="BelongsToGameUnit"/>),
    /// otherwise the file itself.
    /// </summary>
    public static string GetUnitKey(ProcessItem item, IGameKeyService gameKeys) =>
        BelongsToGameUnit(item)
            ? GetGameUnitKey(gameKeys.SerializeCanonicalGameKey(item.Parsed))
            : GetFileUnitKey(item);

    /// <summary>
    /// Whether a file is chosen with its game: its name follows a convention and the release selection did not leave it
    /// non-conforming. A non-conforming file (isolated in <c>_unmatched</c>, apart from its game) is chosen on its own,
    /// even when its name gives its game: it belongs to no set and no playlist.
    /// </summary>
    public static bool BelongsToGameUnit(ProcessItem item) =>
        item.Parsed.IsConform && (item.ReleaseStatus ?? item.Status) != FileStatus.Unmatched;

    /// <summary>Selection unit of a game, from its canonical game key (<see cref="IGameKeyService.SerializeCanonicalGameKey"/>).</summary>
    public static string GetGameUnitKey(string canonicalGameKey) => GameKeyPrefix + canonicalGameKey;

    /// <summary>Whether a selection unit is a game, rather than a non-conforming file.</summary>
    public static bool IsGameUnitKey(string unitKey) => unitKey.StartsWith(GameKeyPrefix, StringComparison.Ordinal);

    /// <summary>Selection unit of a file chosen on its own: a non-conforming file.</summary>
    public static string GetFileUnitKey(ProcessItem item) => FileKeyPrefix + SortSelection.GetItemKey(item);

    /// <summary>
    /// Selection of the units of <paramref name="chosenItems"/>, each with the files the simulation
    /// (<paramref name="simulatedItems"/>) was going to write for it, so that a real sort can detect a change.
    /// </summary>
    public static SortSelection Create(
        IEnumerable<ProcessItem> simulatedItems,
        IEnumerable<ProcessItem> chosenItems,
        IGameKeyService gameKeys) =>
        Create(simulatedItems, chosenItems, it => GetUnitKey(it, gameKeys));

    /// <summary>
    /// Same as <see cref="Create(IEnumerable{ProcessItem}, IEnumerable{ProcessItem}, IGameKeyService)"/>, with the unit
    /// key of each file given by <paramref name="unitKeyOf"/> (keys cached by the caller). The MAME sets that the chosen
    /// sets need to run (<see cref="MameSetInfo.RequiredSets"/>: BIOS, devices) and that the simulation writes are chosen
    /// with them (<see cref="SortSelectionUnit.IsRequiredSet"/>), since MAME does not run a set without them.
    /// </summary>
    public static SortSelection Create(
        IEnumerable<ProcessItem> simulatedItems,
        IEnumerable<ProcessItem> chosenItems,
        Func<ProcessItem, string> unitKeyOf)
    {
        var simulated = simulatedItems as IReadOnlyCollection<ProcessItem> ?? [.. simulatedItems];
        var chosen = chosenItems as IReadOnlyCollection<ProcessItem> ?? [.. chosenItems];
        var required = MameSetDependencies
            .FindRequiredSets(simulated, chosen.Where(IsReleaseWritten))
            .Where(r => IsReleaseWritten(r.Item))
            .ToList();

        var expected = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var item in simulated)
        {
            if (IsReleaseWritten(item))
            {
                var unitKey = unitKeyOf(item);
                if (!expected.TryGetValue(unitKey, out var keys))
                {
                    keys = new HashSet<string>(StringComparer.Ordinal);
                    expected[unitKey] = keys;
                }

                keys.Add(SortSelection.GetItemKey(item));
            }
        }

        var units = new List<SortSelectionUnit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (item, requiredBy) in chosen.Select(it => (it, (IReadOnlyList<string>?)null)).Concat(required.Select(r => (r.Item, (IReadOnlyList<string>?)r.RequiredBy))))
        {
            var unitKey = unitKeyOf(item);
            if (seen.Add(unitKey))
            {
                var label = IsGameUnitKey(unitKey) && !string.IsNullOrEmpty(item.Parsed.Title) ? item.Parsed.Title : item.DisplayName;
                IReadOnlySet<string> expectedKeys = expected.TryGetValue(unitKey, out var keys) ? keys : new HashSet<string>(StringComparer.Ordinal);
                units.Add(new SortSelectionUnit(unitKey, label, expectedKeys, requiredBy));
            }
        }

        return new SortSelection(units);
    }

    /// <summary>
    /// Files that a sort of the selection writes, among the results of a simulation: the files kept or isolated in
    /// <c>_unmatched</c> whose unit is chosen. Gives the volume and the FAT32 warnings of a partial sort.
    /// </summary>
    public static IReadOnlyList<ProcessItem> GetWrittenItems(
        IEnumerable<ProcessItem> items,
        SortSelection selection,
        IGameKeyService gameKeys) =>
        GetWrittenItems(items, selection, it => GetUnitKey(it, gameKeys));

    /// <summary>Same as <see cref="GetWrittenItems(IEnumerable{ProcessItem}, SortSelection, IGameKeyService)"/>, with cached unit keys.</summary>
    public static IReadOnlyList<ProcessItem> GetWrittenItems(
        IEnumerable<ProcessItem> items,
        SortSelection selection,
        Func<ProcessItem, string> unitKeyOf) =>
        items.Where(it => IsWritten(it) && selection.Contains(unitKeyOf(it))).ToList();

    /// <summary>
    /// Takes out of the transfers every file the sort would write whose unit is not chosen (reason
    /// <c>reason.not_in_selection</c>), and every file of a chosen unit whose written files differ from the
    /// simulation (<c>reason.selection_changed</c>). Files already excluded keep their reason.
    /// </summary>
    /// <returns>Number of files taken out of the transfers.</returns>
    public int Apply(IList<ProcessItem> items)
    {
        var writtenByUnit = new Dictionary<string, List<ProcessItem>>(StringComparer.Ordinal);
        var unitKeys = new string[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var unitKey = GetUnitKey(item, _gameKeys);
            unitKeys[i] = unitKey;
            if (!writtenByUnit.TryGetValue(unitKey, out var list))
            {
                list = [];
                writtenByUnit[unitKey] = list;
            }

            if (IsWritten(item))
            {
                list.Add(item);
            }
        }

        var rejectedUnits = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unit in _selection.Units)
        {
            if (!writtenByUnit.TryGetValue(unit.Key, out var written))
            {
                _log(LogLevel.Warn, LocalizedMessage.Create("log.selection_missing", ("name", unit.Label)));
                continue;
            }

            if (unit.ExpectedItemKeys is { } expected &&
                !expected.SetEquals(written.Select(SortSelection.GetItemKey)))
            {
                rejectedUnits.Add(unit.Key);
                _log(LogLevel.Warn, LocalizedMessage.Create("log.selection_changed", ("name", unit.Label)));
            }
        }

        var takenOut = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!IsWritten(item))
            {
                continue;
            }

            var unitKey = unitKeys[i];
            var isRejected = rejectedUnits.Contains(unitKey);
            if (!isRejected && _selection.Contains(unitKey))
            {
                continue;
            }

            item.Status = FileStatus.Excluded;
            item.StatusReason = LocalizedMessage.Create(isRejected ? "reason.selection_changed" : "reason.not_in_selection");
            item.IsOutsideSelection = true;
            takenOut++;
        }

        var requiredUnits = _selection.Units.Where(u => u.IsRequiredSet).Select(u => u.Label).ToList();
        if (requiredUnits.Count > 0)
        {
            _log(LogLevel.Info, LocalizedMessage.Create(
                "log.selection_required_sets",
                ("count", requiredUnits.Count),
                ("examples", string.Join(", ", requiredUnits.Take(3)) + (requiredUnits.Count > 3 ? ", …" : string.Empty))));
        }

        _log(LogLevel.Info, LocalizedMessage.Create(
            "log.selection_applied",
            ("selected", _selection.Count),
            ("outside", takenOut)));
        return takenOut;
    }
}
