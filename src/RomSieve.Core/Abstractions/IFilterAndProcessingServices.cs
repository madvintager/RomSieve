using RomSieve.Core.Models;

namespace RomSieve.Core.Abstractions;

/// <summary>
/// Canonical titles and the keys that group files into games, editions and releases.
/// </summary>
public interface IGameKeyService
{
    string NormalizeCanonicalTitle(string title);
    (string NormalizedTitle, string NormalizedSystem) GetCanonicalGameKey(ParsedRomName parsed);
    string SerializeCanonicalGameKey(ParsedRomName parsed);
    string GetEffectiveVersion(ParsedRomName parsed);
    string? ExtractCrackGroup(ParsedRomName parsed);
    int GetMediaTotal(ParsedRomName parsed);
    IReadOnlyList<string> GetCleanedMoreInfo(ParsedRomName parsed);
    string GetReleaseKey(ParsedRomName parsed, bool smartMultidisk = false);
    string GetEditionKey(ParsedRomName parsed, bool smartMultidisk = false);
}

/// <summary>
/// Release comparison criteria: version, revision and date tuples, media category and fidelity, dump cleanliness,
/// effective languages and countries, and their ranks in the priority lists.
/// </summary>
public interface IReleaseScoringService
{
    int GetRegionLanguageAffinity(ParsedRomName parsed, IReadOnlyList<string> priorityLangs, int? rank);
    HashSet<string> GetEffectiveCountries(ParsedRomName item);
    HashSet<string> GetEffectiveLanguages(ParsedRomName item);
    int[] ParseVersionTuple(string? versionStr);
    int[] ParseRevisionTuple(ParsedRomName parsed);
    (int Year, int Month, int Day) ParseDateTuple(string? dateStr);
    string GetEffectiveVideo(ParsedRomName parsed);
    string GetMediaExtension(string? filenameOrPath);
    string GetMediaCategory(string? filenameOrPath);
    int GetMediaFormatFidelityScore(string? filenameOrPath, ParsedRomName? parsed = null);
    bool IsExtendedAdf(ParsedRomName parsed);
    bool IsReleaseClean(ParsedRomName parsed);

    /// <summary>Whether a release is the original one: no crack, trainer, hack, bootleg, fix, translation, development version or re-release.</summary>
    bool IsUnmodifiedRelease(ParsedRomName parsed);

    /// <summary>Quality of the dump of a release, the highest first: verified, then good, then bad (bad, over- or underdump, virus).</summary>
    int GetDumpQualityRank(ParsedRomName parsed);

    /// <summary>How close a release is to the original one, whatever its dump: the highest first.</summary>
    int GetOriginalReleaseScore(ParsedRomName parsed);

    /// <summary>Dump quality and originality of a release in one score: a bad dump always ranks last.</summary>
    int GetReleaseCleanlinessScore(ParsedRomName parsed);
    int? GetCountryPriorityRank(ParsedRomName item, IReadOnlyList<string> priorityCountries);
    int? GetLanguagePriorityRank(ParsedRomName item, IReadOnlyList<string> priorityLangs);
}

/// <summary>
/// Integrity and modification filters deciding whether a file is kept.
/// </summary>
public interface IFilterEvaluator
{
    (bool IsKept, LocalizedMessage? Reason) EvaluateFilter(ParsedRomName item, FilterConfig config);
}

/// <summary>
/// All the filtering and 1G1R functions in one service. A new component should depend on the narrower
/// <see cref="IGameKeyService"/>, <see cref="IReleaseScoringService"/> or <see cref="IFilterEvaluator"/> it needs.
/// </summary>
public interface IRomFilterService : IGameKeyService, IReleaseScoringService, IFilterEvaluator
{
}

/// <summary>
/// Service for generating target paths, cross-platform path sanitization, and collision resolution.
/// </summary>
public interface IPathOrganizerService
{
    string SanitizePathSegment(string? segment, string replacement = "-");
    string FormatWindowsExtendedPath(string path);
    string BuildTargetRelativePath(
        ProcessItem item,
        string pattern,
        bool restoreArticles = true,
        bool subfolderMultidisk = true,
        bool hiddenSubfolder = false);
    string BuildTargetRelativePath(
        ParsedRomName parsed,
        string pattern,
        bool restoreArticles = true,
        bool subfolderMultidisk = true,
        bool hiddenSubfolder = false,
        bool? isMultiMediaOverride = null,
        string? datName = null);

    /// <summary>
    /// Full target path of a file kept by a sort: the destination, the folder of the DAT of a lot (<paramref name="dat"/>,
    /// <see cref="SortConfig.FolderPerDat"/>, under the folders of its path with <see cref="SortConfig.DatFolderMirror"/>),
    /// the folders produced by the pattern (with the multi-disk subfolder, hidden when the playlists are placed in the
    /// parent folder) and the file name, which is the name of its archive in ZIP mode. Used by the transfers and by the
    /// live preview of the pattern.
    /// </summary>
    string BuildTargetPath(ParsedRomName parsed, string fileName, SortConfig config, bool? isMultiMediaOverride = null, DatPlacement? dat = null);

    /// <summary>Unknown variables and unbalanced braces of a directory pattern, which the sort would drop or keep as text.</summary>
    PatternCheck CheckPattern(string? pattern);

    string GenerateM3uFilename(string filenameOrStem);
    (string FinalPath, bool IsCollisionSkipped) ResolveCollision(
        string candidatePath,
        CollisionMode collisionMode,
        ISet<string> claimedPaths);

    /// <summary>
    /// Resolves collisions for a set of files that must stay together (an optical descriptor and its tracks).
    /// The set collides as soon as one of its paths exists on disk or is already claimed. In <see cref="CollisionMode.Rename"/>
    /// mode every file receives the same <c>_NN</c> suffix; in <see cref="CollisionMode.Skip"/> mode the whole set is skipped.
    /// Final paths are claimed in <paramref name="claimedPaths"/> using normalized keys.
    /// </summary>
    (IReadOnlyList<string> FinalPaths, bool IsCollisionSkipped) ResolveSetCollision(
        IReadOnlyList<string> candidatePaths,
        CollisionMode collisionMode,
        ISet<string> claimedPaths);
}

/// <summary>
/// Main orchestrator and exporter for sorting/simulation reports.
/// </summary>
public interface ISortProcessorService
{
    /// <summary>
    /// Runs the pipeline: a simulation, or a real sort when <paramref name="isDryRun"/> is false. With a
    /// <paramref name="selection"/>, a partial sort: the whole collection is still classified and resolved, but only
    /// the chosen games and files are transferred, and the destination cannot be purged.
    /// </summary>
    Task<SortExecutionResult> ExecuteAsync(
        SortConfig config,
        bool isDryRun = true,
        IProgress<SortProgress>? progress = null,
        IProgress<LogMessage>? logProgress = null,
        IProgress<ProcessItem>? itemProcessed = null,
        CancellationToken cancellationToken = default,
        SortSelection? selection = null);

    /// <summary>
    /// Selects the releases on the DAT of <paramref name="config"/> alone, without source nor destination: one item per
    /// game of the DAT (a set per machine for MAME), named, identified and filtered as the file of a complete collection
    /// named after the DAT would be, then the same release selection as a run (filters, 1G1R, MAME sets required).
    /// The filtered DAT holds the games kept. Nothing is read but the DAT and the MAME category and rating files.
    /// </summary>
    Task<CatalogSelectionResult> SelectFromCatalogAsync(
        SortConfig config,
        IProgress<SortProgress>? progress = null,
        IProgress<LogMessage>? logProgress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="SelectFromCatalogAsync"/>, then writes the filtered DAT to <paramref name="outputPath"/> (Logiqx XML or
    /// CLRMAMEPRO, <see cref="DatExportFormat.Auto"/> following the extension). Nothing is written when no game is kept.
    /// </summary>
    Task<CatalogSelectionResult> ExportCatalogDatAsync(
        SortConfig config,
        string outputPath,
        DatExportFormat format = DatExportFormat.LogiqxXml,
        IProgress<SortProgress>? progress = null,
        IProgress<LogMessage>? logProgress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Selects again the releases of one game (<paramref name="gameItems"/>), after a manual 1G1R choice.
    /// <paramref name="allItems"/>, every item of the run, lets the MAME sets the game needs (BIOS, devices) be kept.
    /// </summary>
    void RecalculateGameItems(
        IList<ProcessItem> gameItems,
        SortConfig config,
        DatCatalog? datCatalog = null,
        IReadOnlyCollection<ProcessItem>? allItems = null);

    ProcessStats RecomputeStats(
        IReadOnlyCollection<ProcessItem> items,
        ProcessStats? baseStats = null);

    /// <summary>
    /// Content of a destination besides the RomSieve marker and quarantine, and presence of the marker: a real sort
    /// asks to complete or purge a destination with content, and warns more strongly when RomSieve did not mark it.
    /// </summary>
    DestinationState InspectDestination(string path);

    Task ExportReportToJsonAsync(IEnumerable<ProcessItem> items, ProcessStats stats, string outputPath, CancellationToken cancellationToken = default);
    Task ExportReportToCsvAsync(IEnumerable<ProcessItem> items, string outputPath, CancellationToken cancellationToken = default);
    Task ExportMissListToCsvAsync(IEnumerable<DatRomEntry> missingRoms, DatCatalog datCatalog, string outputPath, CancellationToken cancellationToken = default);
    Task ExportMissListToTxtAsync(IEnumerable<DatRomEntry> missingRoms, DatCatalog datCatalog, string outputPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the fixdat of <paramref name="datCatalog"/> (Logiqx XML): the games of the DAT reduced to their missing ROMs.
    /// </summary>
    Task<DatCatalog> ExportFixDatAsync(IEnumerable<DatRomEntry> missingRoms, DatCatalog datCatalog, string outputPath, CancellationToken cancellationToken = default);
    Task<DatCatalog> ExportDatAsync(
        IEnumerable<ProcessItem> items,
        string outputPath,
        DatExportFormat format = DatExportFormat.LogiqxXml,
        DatCatalog? datCatalog = null,
        DatHeader? headerOverride = null,
        bool validOnly = true,
        bool computeMissingHashes = true,
        IProgress<(int Current, int Total, string Filename)>? progress = null,
        CancellationToken cancellationToken = default);
    Task<DatCatalog> ExportDatToXmlAsync(
        IEnumerable<ProcessItem> items,
        string outputPath,
        DatCatalog? datCatalog = null,
        DatHeader? headerOverride = null,
        bool validOnly = true,
        bool computeMissingHashes = true,
        IProgress<(int Current, int Total, string Filename)>? progress = null,
        CancellationToken cancellationToken = default);
    Task<DatCatalog> ExportDatToClrMameProAsync(
        IEnumerable<ProcessItem> items,
        string outputPath,
        DatCatalog? datCatalog = null,
        DatHeader? headerOverride = null,
        bool validOnly = true,
        bool computeMissingHashes = true,
        IProgress<(int Current, int Total, string Filename)>? progress = null,
        CancellationToken cancellationToken = default);
}
