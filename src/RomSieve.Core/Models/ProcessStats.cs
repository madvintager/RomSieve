namespace RomSieve.Core.Models;

/// <summary>
/// Execution statistics for simulation (Dry-Run) or sorting.
/// </summary>
public sealed class ProcessStats
{
    public int TotalScanned { get; set; }
    public long TotalBytes { get; set; }
    public int ValidCount { get; set; }
    public long ValidBytes { get; set; }
    public int ExcludedCount { get; set; }
    public long ExcludedBytes { get; set; }
    public int UnmatchedCount { get; set; }
    public long UnmatchedBytes { get; set; }

    /// <summary>
    /// Non-conforming files left in the source, their isolation in <c>_unmatched</c> being off
    /// (<see cref="ProcessItem.IsNotIsolated"/>); they are also counted in <see cref="UnmatchedCount"/>.
    /// </summary>
    public int NotIsolatedCount { get; set; }
    public long NotIsolatedBytes { get; set; }

    /// <summary>Non-conforming files the sort isolates in <c>_unmatched</c> of the destination.</summary>
    public int IsolatedUnmatchedCount => UnmatchedCount - NotIsolatedCount;

    /// <summary>
    /// Bytes the sort writes to the destination: the kept files and the non-conforming files, which are isolated in
    /// <c>_unmatched</c> of the destination unless <see cref="SortConfig.IsolateUnmatched"/> is off. Excluded files
    /// and duplicates are not written.
    /// </summary>
    public long OutputBytes => ValidBytes + UnmatchedBytes - NotIsolatedBytes;

    /// <summary>
    /// Files a partial sort did not write because their game was not chosen (<see cref="SortSelection"/>); they are
    /// not counted in <see cref="ExcludedCount"/>.
    /// </summary>
    public int NotSelectedCount { get; set; }
    public long NotSelectedBytes { get; set; }
    public int DuplicateCount { get; set; }

    /// <summary>
    /// Duplicates whose target was already in the destination before the sort (<see cref="ProcessItem.IsAlreadyInDestination"/>):
    /// counted in <see cref="DuplicateCount"/> too.
    /// </summary>
    public int AlreadyInDestinationCount { get; set; }
    public int ErrorCount { get; set; }
    public int ProcessedCount { get; set; }
    public long ProcessedBytes { get; set; }

    // DAT verification statistics
    public int DatTotalRoms { get; set; }
    public int DatMatchedCount { get; set; }

    /// <summary>DAT entries of games designated by the name of a CHD (<c>SortConfig.MatchChdByName</c>): present, not verified.</summary>
    public int DatNameMatchedCount { get; set; }
    public int DatMissingCount { get; set; }
    public double DatCompletionPct { get; set; }

    /// <summary>
    /// What the sort found of each DAT of a lot, in the order of the lot (the totals above add them up); <c>null</c>
    /// for a sort with a single DAT or none.
    /// </summary>
    public IReadOnlyList<DatLotStats>? PerDat { get; set; }

    /// <summary>
    /// What the Miss List and the fixdat of a lot leave out: the DATs complete, and those without any game found, whose
    /// ROMs all are missing; <c>null</c> for a sort with a single DAT, or when the lot leaves nothing out.
    /// </summary>
    public LocalizedMessage? DescribeLotExportScope()
    {
        if (PerDat is not { } dats)
        {
            return null;
        }

        var complete = dats.Count(d => d.GamesFound > 0 && d.MissingCount == 0);
        var nothingFound = dats.Count(d => d.GamesFound == 0);
        return complete + nothingFound == 0
            ? null
            : LocalizedMessage.Create("log.dat_lot_export_scope", ("complete", complete), ("nothing", nothingFound));
    }
}

/// <summary>
/// Progress notification emitted via <see cref="IProgress{T}"/>. <paramref name="Message"/> names the current step of
/// the pipeline (listing the source files, loading the DAT, analysis, transfers...) or the file it works on; it is
/// translated only when shown.
/// </summary>
public sealed record SortProgress(
    int ProcessedCount,
    int TotalCount,
    long ProcessedBytes,
    long TotalBytes,
    double EtaSeconds,
    LocalizedMessage Message
);

/// <summary>
/// Log message emitted via <see cref="IProgress{T}"/>.
/// </summary>
public sealed record LogMessage(
    LogLevel Level,
    LocalizedMessage Message
);

/// <summary>
/// Complete result of a <c>SortProcessor</c> run.
/// </summary>
public sealed record SortExecutionResult(
    ProcessStats Stats,
    IReadOnlyList<ProcessItem> Items,
    IReadOnlyList<DatRomEntry> MissingRoms,
    DatCatalog? DatCatalog,
    string? JournalPath = null,
    LocalizedMessage? StopReason = null
);

/// <summary>
/// Result of a selection made on the DAT alone (<c>ISortProcessorService.SelectFromCatalogAsync</c>): the catalog read,
/// one item per game (a set for MAME) with the status the release selection gave it, and the filtered DAT of the games
/// kept.
/// </summary>
public sealed record CatalogSelectionResult(
    DatCatalog Catalog,
    IReadOnlyList<ProcessItem> Items,
    DatCatalog FilteredDat
)
{
    /// <summary>Games of the catalog that a file of a collection could stand for (a game with ROMs or disks to dump).</summary>
    public int CandidateCount => Items.Count;

    /// <summary>Games written in the filtered DAT, in all its DATs for a lot.</summary>
    public int KeptCount => FilteredDat.GameCount;
}
