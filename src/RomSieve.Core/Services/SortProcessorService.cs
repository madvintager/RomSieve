using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Main orchestrator of the sorting, simulation, in-memory 1G1R recalculation, and report export pipeline
/// asynchronously using <c>async Task</c>, <see cref="IProgress{T}"/>, and <see cref="CancellationToken"/>.
/// </summary>
public sealed class SortProcessorService : ISortProcessorService
{
    private readonly IChecksumService _checksumService;
    private readonly ICrcCacheService _crcCache;
    private readonly IDatService _datService;
    private readonly INoIntroRedumpParser _romParser;
    private readonly IRomFilterService _filterService;
    private readonly IPathOrganizerService _organizer;
    private readonly IOpticalCompanionService _opticalService;
    private readonly IMameDescriptionParser _mameParser;
    private readonly ILocalizationService _i18n;
    private readonly IOperationJournalService? _journalService;

    /// <summary>Test hook applied to the transfer executor of each run (its own hooks: failing streams, volumes).</summary>
    internal Action<FileTransferExecutor>? ConfigureTransferExecutor { get; set; }

    /// <summary>Test hook replacing the room left on the volume of the destination (<see cref="DestinationSpace.GetAvailableBytes"/>).</summary>
    internal Func<string, long?>? AvailableBytesOf { get; set; }

    /// <summary>
    /// Test hook: whether the DATs of a lot are read on demand (<see cref="DatFolderOptions.LoadOnDemand"/>, the default)
    /// or kept in memory as they are read, which must give the same results.
    /// </summary>
    internal bool LoadLotDatsOnDemand { get; set; } = true;

    public ICrcCacheService CrcCache => _crcCache;

    public SortProcessorService(
        IChecksumService checksumService,
        IDatService datService,
        INoIntroRedumpParser romParser,
        IRomFilterService filterService,
        IPathOrganizerService organizer,
        ILocalizationService i18n,
        IOpticalCompanionService opticalService,
        IMameDescriptionParser mameParser,
        ICrcCacheService crcCache,
        IOperationJournalService? journalService = null)
    {
        // A null journal service disables the operation journal (and therefore the undo of sorts)
        _journalService = journalService;
        _checksumService = checksumService;
        _crcCache = crcCache;
        _datService = datService;
        _romParser = romParser;
        _filterService = filterService;
        _organizer = organizer;
        _i18n = i18n;
        _opticalService = opticalService;
        _mameParser = mameParser;
    }

    public async Task<SortExecutionResult> ExecuteAsync(
        SortConfig config,
        bool isDryRun = true,
        IProgress<SortProgress>? progress = null,
        IProgress<LogMessage>? logProgress = null,
        IProgress<ProcessItem>? itemProcessed = null,
        CancellationToken cancellationToken = default,
        SortSelection? selection = null)
    {
        config.Filters.NormalizeDefaults();

        void Log(LogLevel level, LocalizedMessage msg) => logProgress?.Report(new LogMessage(level, msg));

        // A partial sort only completes the destination: purging it would delete what the selection leaves out
        if (selection is not null && config.PurgeDestination)
        {
            throw new InvalidOperationException(_i18n.Tr("log.partial_sort_purge_refused"));
        }

        ValidateDirectories(config, isDryRun, Log);

        // A MAME set or software is only known by its DAT: its file name is a short name
        if (config.NamingConvention.IsMameSets() && string.IsNullOrWhiteSpace(config.DatFile))
        {
            throw new InvalidOperationException(_i18n.Tr(config.NamingConvention == NamingConvention.Mame
                ? "log.mame_requires_dat"
                : "log.mame_softlist_requires_dat"));
        }

        var scanner = new SourceScanner(config, _checksumService, _i18n, _opticalService, Log, _crcCache.Headers);
        List<ScanEntry> scanEntries;
        long totalBytes;
        try
        {
            (scanEntries, totalBytes) = scanner.Scan(cancellationToken, progress);
        }
        finally
        {
            // The archive headers read so far are kept, even by an interrupted scan (the first scan of a large
            // collection on a network share takes minutes)
            await _crcCache.SaveAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (scanEntries.Count == 0)
        {
            return new SortExecutionResult(new ProcessStats(), [], [], null);
        }

        var stats = new ProcessStats
        {
            TotalScanned = scanEntries.Count,
            TotalBytes = totalBytes
        };

        var datCatalog = await LoadDatCatalogAsync(
            config,
            Log,
            progress,
            scanEntries.Count,
            totalBytes,
            cancellationToken).ConfigureAwait(false);

        List<ProcessItem> itemsToProcess;
        List<DatRomEntry> missingRoms;
        IReadOnlyDictionary<string, HashSet<string>> gameMatchedRoms;
        DatLotIdentifier? lotIdentifier = null;
        var isMameRun = MameSetIdentifier.IsMameRun(config, datCatalog);
        if (MameSetIdentifier.GetSetConvention(config, datCatalog).IsMameSets() && datCatalog is not null)
        {
            // The CHDs of a MAME collection may be kept in a folder of their own, which the destination cannot overlap;
            // the files of the MAME machines (CHD folder, categories, ratings) say nothing of the software of a list
            if (isMameRun && SortConfigValidator.GetChdDirErrorKey(config.ChdDir, config.DestinationDir) is { } chdOverlapKey)
            {
                throw new InvalidOperationException(_i18n.Tr(chdOverlapKey));
            }

            if (isMameRun && !string.IsNullOrWhiteSpace(config.ChdDir))
            {
                scanEntries.AddRange(scanner.ScanChdFolder(config.ChdDir, scanEntries, cancellationToken, progress));
            }

            HashSet<DatRomEntry> matchedEntries;
            IReadOnlyList<DatRomEntry>? countedEntries = null;
            try
            {
                if (datCatalog.SoftwareLists is not null)
                {
                    // The folders of the lists are subfolders of the source: a scan of its root alone finds none
                    if (!config.Recursive)
                    {
                        Log(LogLevel.Warn, LocalizedMessage.Create("log.software_lists_not_recursive"));
                    }

                    // Several software lists: each archive is identified in the list of its folder; the completion only
                    // counts the lists present in the source
                    var lists = new SoftwareListIdentifier(
                        config, datCatalog, _checksumService, _mameParser, _filterService, _i18n, Log, _crcCache.Headers);
                    itemsToProcess = lists.IdentifyAll(scanEntries, cancellationToken, progress);
                    matchedEntries = lists.MatchedDatEntries;
                    countedEntries = lists.PresentLists.SelectMany(l => l.Entries).ToList();
                }
                else
                {
                    var identifier = new MameSetIdentifier(
                        config, datCatalog, _checksumService, _mameParser, _filterService, _i18n, Log, _crcCache.Headers,
                        isMameRun ? LoadMameCategories(config.MameCategoryFile, Log) : null,
                        isMameRun ? LoadMameRatings(config.MameRatingFile, Log) : null);
                    itemsToProcess = identifier.IdentifyAll(scanEntries, cancellationToken, progress);
                    matchedEntries = identifier.MatchedDatEntries;
                }
            }
            finally
            {
                // Headers of the CHDs, and of the archives the scan did not open
                ReportStep(progress, LocalizedMessage.Create("progress.saving_cache"));
                await _crcCache.SaveAsync(CancellationToken.None).ConfigureAwait(false);
            }

            // A set is one item: the totals count the sets and the size of their files, not their members
            totalBytes = itemsToProcess.Sum(it => it.FileSize + it.RelatedItems.Sum(r => r.FileSize));
            stats.TotalScanned = itemsToProcess.Count;
            stats.TotalBytes = totalBytes;
            missingRoms = UpdateDatStatistics(stats, datCatalog, matchedEntries, new HashSet<DatRomEntry>(ReferenceEqualityComparer.Instance), Log, countedEntries);
            gameMatchedRoms = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        }
        else if (datCatalog?.Dats is not null)
        {
            // A lot of DATs: a sort per DAT, from a single scan and a single computation of the fingerprints
            var lot = new DatLotIdentifier(
                config, datCatalog, _crcCache, _checksumService, _romParser, _filterService, _i18n, _opticalService, Log);
            itemsToProcess = await lot.IdentifyAllAsync(scanEntries, cancellationToken, progress, totalBytes).ConfigureAwait(false);
            missingRoms = UpdateLotStatistics(stats, datCatalog, lot, Log);
            gameMatchedRoms = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            lotIdentifier = lot;
        }
        else
        {
            // The CHD folder and the MAME files stay in the configuration for the next MAME sort: nothing to say here
            var classifier = new EntryClassifier(
                config,
                datCatalog,
                _crcCache,
                _checksumService,
                _romParser,
                _filterService,
                _i18n,
                _opticalService);

            itemsToProcess = await classifier.ClassifyAllAsync(
                scanEntries,
                cancellationToken,
                progress,
                totalBytes).ConfigureAwait(false);

            missingRoms = UpdateDatStatistics(stats, datCatalog, classifier.MatchedDatEntries, classifier.NameMatchedDatEntries, Log);
            gameMatchedRoms = classifier.GameMatchedRoms;
        }

        SelectReleases(config, itemsToProcess, datCatalog, gameMatchedRoms, progress, Log, lotIdentifier is null
            ? null
            : name => lotIdentifier.ClassifierFor(name)?.GameMatchedRoms);

        // The selection of a partial sort only filters the transfers: the releases are chosen on the whole collection
        if (selection is not null)
        {
            new SelectionFilter(selection, _filterService, Log).Apply(itemsToProcess);
        }

        var totalFiles = itemsToProcess.Count;
        var processedItems = new List<ProcessItem>(totalFiles);

        // Journal of the physical operations of a real sort, used to undo it
        OperationJournalWriter? journal = null;
        if (!isDryRun && _journalService is not null)
        {
            try
            {
                journal = await _journalService.BeginAsync(config, selection?.Count, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log(LogLevel.Warn, LocalizedMessage.Create("log.journal_unavailable", ("error", ex.Message)));
            }
        }

        // Why the sort stopped before its end: a full destination, or a journal that cannot be written, without which
        // the sort could not be undone. A sort never starts without its journal.
        LocalizedMessage? stopReason = null;
        if (!isDryRun && _journalService is not null && (journal is null || journal.HasFailed))
        {
            stopReason = LocalizedMessage.Create("log.sort_stopped_journal_unavailable");
            Log(LogLevel.Error, stopReason);
        }

        var completed = false;
        try
        {
            var transferExecutor = new FileTransferExecutor(
                config,
                isDryRun,
                _organizer,
                _filterService,
                _i18n,
                _opticalService,
                _checksumService,
                Log,
                journal,
                _crcCache)
            {
                IsMameRun = isMameRun,
                DatCatalog = datCatalog
            };
            ConfigureTransferExecutor?.Invoke(transferExecutor);
            if (!isDryRun)
            {
                ReportStep(progress, LocalizedMessage.Create("progress.preparing_destination"));
            }

            if (stopReason is null)
            {
                var (ok, errMsg) = await transferExecutor.PrepareDestinationAsync().ConfigureAwait(false);
                if (!ok)
                {
                    throw new InvalidOperationException(errMsg);
                }

                if (journal is { HasFailed: true })
                {
                    stopReason = LocalizedMessage.Create("log.sort_stopped_journal_purge", ("path", journal.JournalPath));
                    Log(LogLevel.Error, stopReason);
                }
            }

            // A simulation only computes the targets, quickly: the step is named, not each file
            if (isDryRun)
            {
                ReportStep(progress, LocalizedMessage.Create("progress.computing_targets"));
            }

            var claimedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sw = Stopwatch.StartNew();

            // The progress of a real sort counts the files it writes: the excluded ones (versions not kept, games
            // outside the selection of a partial sort) and the duplicates are passed over at once
            var writtenTotal = 0;
            long writtenTotalBytes = 0;
            foreach (var item in itemsToProcess.Where(IsWrittenByTransfer))
            {
                writtenTotal++;
                writtenTotalBytes += item.FileSize + item.RelatedItems.Sum(r => r.FileSize);
            }

            // A sort that lacks room stops when the destination is full: the shortfall is said before the first file
            var volumeRoots = VolumeLocator.GetVolumeRoots();
            if (DestinationSpace.FindShortfall(
                    DestinationSpace.GetRequiredBytes(itemsToProcess, config, volumeRoots),
                    AvailableBytesOf?.Invoke(config.DestinationDir) ?? DestinationSpace.GetAvailableBytes(config.DestinationDir, volumeRoots),
                    config.DestinationDir) is { } shortfall)
            {
                Log(LogLevel.Warn, shortfall.ToMessage("log.destination_space_short"));
            }

            var writtenCount = 0;
            long writtenBytes = 0;

            foreach (var item in itemsToProcess)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    transferExecutor.CleanupPartial();
                    Log(LogLevel.Warn, LocalizedMessage.Create("log.operation_cancelled_by_user"));
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var isWritten = !isDryRun && IsWrittenByTransfer(item);
                if (isWritten && (writtenCount == 0 || item.FileSize > 1024 * 1024))
                {
                    var preEta = EtaEstimator.EstimateSeconds(sw.Elapsed.TotalSeconds, writtenCount, writtenTotal, writtenBytes, writtenTotalBytes);
                    progress?.Report(new SortProgress(
                        ProcessedCount: writtenCount,
                        TotalCount: writtenTotal,
                        ProcessedBytes: writtenBytes,
                        TotalBytes: writtenTotalBytes,
                        EtaSeconds: preEta,
                        Message: DescribeTransfer(config.TransferMode, item.DisplayName)));
                }

                if (stopReason is not null && IsWrittenByTransfer(item))
                {
                    MarkNotTransferred(item, stats);
                }
                else
                {
                    try
                    {
                        await transferExecutor.ProcessItemAsync(item, claimedPaths, stats, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // The executor has already removed the partially written target
                        Log(LogLevel.Warn, LocalizedMessage.Create("log.operation_cancelled_by_user"));
                        throw;
                    }

                    if (stopReason is null && (transferExecutor.StopReason ?? JournalStopReason(journal, item)) is { } reason)
                    {
                        stopReason = reason;
                        Log(LogLevel.Error, stopReason);
                    }
                }

                stats.ProcessedCount++;
                stats.ProcessedBytes += item.FileSize + item.RelatedItems.Sum(r => r.FileSize);
                processedItems.Add(item);
                itemProcessed?.Report(item);

                if (isWritten)
                {
                    writtenCount++;
                    writtenBytes += item.FileSize + item.RelatedItems.Sum(r => r.FileSize);
                    var etaSeconds = EtaEstimator.EstimateSeconds(sw.Elapsed.TotalSeconds, writtenCount, writtenTotal, writtenBytes, writtenTotalBytes);
                    progress?.Report(new SortProgress(
                        ProcessedCount: writtenCount,
                        TotalCount: writtenTotal,
                        ProcessedBytes: writtenBytes,
                        TotalBytes: writtenTotalBytes,
                        EtaSeconds: etaSeconds,
                        Message: DescribeTransfer(config.TransferMode, item.DisplayName)));
                }
            }

            if (config.GenerateM3u && stopReason is null)
            {
                ReportStep(progress, LocalizedMessage.Create("progress.writing_playlists"));
                var playlistReleases = new MultiDiskEvaluator(config, _filterService, _i18n).GroupPlaylistReleases(processedItems);
                try
                {
                    await transferExecutor.GenerateM3uPlaylistsAsync(playlistReleases, claimedPaths, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The executor has already removed the playlist being written
                    Log(LogLevel.Warn, LocalizedMessage.Create("log.operation_cancelled_by_user"));
                    throw;
                }
            }

            completed = stopReason is null;
        }
        finally
        {
            if (journal is not null)
            {
                await journal.CompleteAsync(completed).ConfigureAwait(false);
                if (journal.HasFailed)
                {
                    Log(LogLevel.Warn, LocalizedMessage.Create("log.journal_write_failed", ("path", journal.JournalPath)));
                }
            }

            if (!isDryRun)
            {
                // Persists the cache entries that followed moved files
                await _crcCache.SaveAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        if (Fat32Limit.CreateLogMessage(Fat32Limit.FindOversizedOutputFiles(processedItems, config.TransferMode))
            is { } fat32Warning)
        {
            Log(LogLevel.Warn, fat32Warning);
        }

        if (stats.NotIsolatedCount > 0)
        {
            Log(LogLevel.Info, LocalizedMessage.Create("log.unmatched_not_isolated", ("count", stats.NotIsolatedCount)));
        }

        if (stats.AlreadyInDestinationCount > 0)
        {
            Log(LogLevel.Warn, LocalizedMessage.Create("log.already_in_destination_summary", ("count", stats.AlreadyInDestinationCount)));
        }

        Log(LogLevel.Info, selection is null
            ? LocalizedMessage.Create(
                "log.operation_completed_summary",
                ("valid", stats.ValidCount),
                ("excluded", stats.ExcludedCount),
                ("unmatched", stats.UnmatchedCount),
                ("duplicates", stats.DuplicateCount),
                ("errors", stats.ErrorCount))
            : LocalizedMessage.Create(
                "log.operation_completed_summary_partial",
                ("valid", stats.ValidCount),
                ("excluded", stats.ExcludedCount),
                ("unmatched", stats.UnmatchedCount),
                ("duplicates", stats.DuplicateCount),
                ("errors", stats.ErrorCount),
                ("not_selected", stats.NotSelectedCount)));

        var journalPath = journal is { OperationCount: > 0 } ? journal.JournalPath : null;
        return new SortExecutionResult(stats, processedItems, missingRoms, datCatalog, journalPath, stopReason);
    }

    /// <summary>
    /// A file that a sort stopped before writing: in error, with its companions, and nothing written for it. The files
    /// it would not have written (excluded versions, duplicates) are processed as usual, without any write.
    /// </summary>
    private static void MarkNotTransferred(ProcessItem item, ProcessStats stats)
    {
        foreach (var it in item.RelatedItems.Prepend(item))
        {
            it.Status = FileStatus.Error;
            it.StatusReason = LocalizedMessage.Create("reason.sort_stopped");
            it.FinalTargetPath = null;
        }

        stats.ErrorCount++;
    }

    /// <summary>
    /// A journal that could not record the last operation: the sort stops, since what follows could not be undone. The
    /// file just written is named, since the journal does not hold it.
    /// </summary>
    private static LocalizedMessage? JournalStopReason(OperationJournalWriter? journal, ProcessItem item) =>
        journal is { HasFailed: true }
            ? LocalizedMessage.Create(
                "log.sort_stopped_journal_failed",
                ("path", journal.JournalPath),
                ("name", item.DisplayName),
                ("target", item.FinalTargetPath ?? string.Empty))
            : null;

    /// <summary>
    /// Selection of the releases, shared by a run and by a selection on the catalog alone, with what it tells: the size
    /// budget of a MAME run, and the MAME sets kept because a kept set needs them (BIOS, devices), whatever excluded them.
    /// </summary>
    private void SelectReleases(
        SortConfig config,
        List<ProcessItem> items,
        DatCatalog? datCatalog,
        IReadOnlyDictionary<string, HashSet<string>> gameMatchedRoms,
        IProgress<SortProgress>? progress,
        Action<LogLevel, LocalizedMessage> log,
        Func<string, IReadOnlyDictionary<string, HashSet<string>>?>? gameMatchedRomsOfDat = null)
    {
        ReportStep(progress, LocalizedMessage.Create("progress.selecting_releases"));
        var releaseSelector = new ReleaseSelector(config, _filterService, _i18n);
        if (datCatalog?.Dats is not null)
        {
            // A lot: the releases of each DAT are selected with its catalog and its files, never with another DAT's
            foreach (var (datItems, datName) in GroupByDat(items))
            {
                releaseSelector.Select(
                    datItems,
                    datName is null ? null : datCatalog.DatFor(datName),
                    (datName is null ? null : gameMatchedRomsOfDat?.Invoke(datName))
                    ?? new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase));
            }
        }
        else
        {
            releaseSelector.Select(items, datCatalog, gameMatchedRoms);
        }

        if (releaseSelector.LastBudget is { } budget)
        {
            log(LogLevel.Info, LocalizedMessage.Create(
                "log.mame_budget",
                ("budget", MameSizeBudget.FormatGigabytes(budget.BudgetBytes)),
                ("kept", budget.KeptCount),
                ("used", MameSizeBudget.FormatGigabytes(budget.UsedBytes)),
                ("excluded", budget.Excluded.Count)));
        }

        var requiredSets = items
            .Where(it => MameSetDependencies.IsRequiredSetReason(it.StatusReason))
            .Select(it => it.MameSet?.GameName ?? it.DisplayName)
            .ToList();
        if (requiredSets.Count > 0)
        {
            log(LogLevel.Info, LocalizedMessage.Create(
                "log.mame_required_sets",
                ("count", requiredSets.Count),
                ("examples", string.Join(", ", requiredSets.Take(3)) + (requiredSets.Count > 3 ? ", …" : string.Empty))));
        }
    }

    /// <summary>
    /// The items of a lot of DATs grouped by DAT (<see cref="ProcessItem.DatName"/>), in the order of their first item,
    /// the files no DAT knows last, under a <c>null</c> name; each group keeps the order of <paramref name="items"/>.
    /// </summary>
    private static List<(List<ProcessItem> Items, string? DatName)> GroupByDat(IEnumerable<ProcessItem> items)
    {
        var groups = new List<(List<ProcessItem> Items, string? DatName)>();
        var byName = new Dictionary<string, List<ProcessItem>>(StringComparer.OrdinalIgnoreCase);
        var withoutDat = new List<ProcessItem>();
        foreach (var item in items)
        {
            if (item.DatName is not { } name)
            {
                withoutDat.Add(item);
                continue;
            }

            if (!byName.TryGetValue(name, out var group))
            {
                group = [];
                byName[name] = group;
                groups.Add((group, name));
            }

            group.Add(item);
        }

        if (withoutDat.Count > 0)
        {
            groups.Add((withoutDat, null));
        }

        return groups;
    }

    public async Task<CatalogSelectionResult> SelectFromCatalogAsync(
        SortConfig config,
        IProgress<SortProgress>? progress = null,
        IProgress<LogMessage>? logProgress = null,
        CancellationToken cancellationToken = default)
    {
        config.Filters.NormalizeDefaults();

        void Log(LogLevel level, LocalizedMessage msg) => logProgress?.Report(new LogMessage(level, msg));

        var datCatalog = await LoadCatalogOnlyAsync(config, Log, progress, cancellationToken).ConfigureAwait(false);
        return SelectFromLoadedCatalog(config, datCatalog, progress, Log, cancellationToken);
    }

    /// <summary>
    /// The selection on the catalog alone (<see cref="SelectFromCatalogAsync"/>) of a catalog read: every DAT of a lot is
    /// read and its games kept in memory together.
    /// </summary>
    private CatalogSelectionResult SelectFromLoadedCatalog(
        SortConfig config,
        DatCatalog datCatalog,
        IProgress<SortProgress>? progress,
        Action<LogLevel, LocalizedMessage> log,
        CancellationToken cancellationToken)
    {
        ReportStep(progress, LocalizedMessage.Create("progress.reading_catalog_games"));
        List<ProcessItem> items;
        IReadOnlyDictionary<string, HashSet<string>> gameMatchedRoms;
        Func<string, IReadOnlyDictionary<string, HashSet<string>>?>? gameMatchedRomsOfDat = null;
        if (datCatalog.Dats is { } dats)
        {
            // A lot: the games of each DAT, as the classification of a complete collection named after it gives them
            var matchedByDat = new Dictionary<string, IReadOnlyDictionary<string, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
            items = ClassifyLotCatalog(config, dats, matchedByDat, cancellationToken);
            gameMatchedRoms = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            gameMatchedRomsOfDat = matchedByDat.GetValueOrDefault;
        }
        else if (datCatalog.SoftwareLists is not null)
        {
            // Several software lists: the software of each list, in its own catalog
            items = new SoftwareListIdentifier(
                config, datCatalog, _checksumService, _mameParser, _filterService, _i18n, log).IdentifyCatalog(cancellationToken);
            gameMatchedRoms = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        }
        else if (MameSetIdentifier.GetSetConvention(config, datCatalog).IsMameSets())
        {
            var isMameRun = MameSetIdentifier.IsMameRun(config, datCatalog);
            var identifier = new MameSetIdentifier(
                config, datCatalog, _checksumService, _mameParser, _filterService, _i18n, log,
                categories: isMameRun ? LoadMameCategories(config.MameCategoryFile, log) : null,
                ratings: isMameRun ? LoadMameRatings(config.MameRatingFile, log) : null);
            items = identifier.IdentifyCatalog(cancellationToken);
            gameMatchedRoms = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            var classifier = new EntryClassifier(
                config, datCatalog, _crcCache, _checksumService, _romParser, _filterService, _i18n, _opticalService);
            items = classifier.ClassifyCatalog(cancellationToken);
            gameMatchedRoms = classifier.GameMatchedRoms;
        }

        cancellationToken.ThrowIfCancellationRequested();
        SelectReleases(config, items, datCatalog, gameMatchedRoms, progress, log, gameMatchedRomsOfDat);

        var filteredDat = CreateFilteredDat(datCatalog, items.Where(it => it.Status == FileStatus.Valid).ToList());
        var result = new CatalogSelectionResult(datCatalog, items, filteredDat);
        log(LogLevel.Ok, LocalizedMessage.Create(
            "log.catalog_selection_summary",
            ("kept", result.KeptCount),
            ("total", result.CandidateCount)));
        return result;
    }

    /// <summary>The catalog of a selection on the DAT alone, which needs a DAT and nothing else.</summary>
    private async Task<DatCatalog> LoadCatalogOnlyAsync(
        SortConfig config,
        Action<LogLevel, LocalizedMessage> log,
        IProgress<SortProgress>? progress,
        CancellationToken cancellationToken)
    {
        // The catalog is the only input: no source, no destination
        if (string.IsNullOrWhiteSpace(config.DatFile))
        {
            throw new InvalidOperationException(_i18n.Tr("log.catalog_requires_dat"));
        }

        log(LogLevel.Info, LocalizedMessage.Create("log.catalog_selection_starting"));
        return await LoadDatCatalogAsync(config, log, progress, 0, 0, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(_i18n.Tr("log.catalog_requires_dat"));
    }

    public async Task<CatalogSelectionResult> ExportCatalogDatAsync(
        SortConfig config,
        string outputPath,
        DatExportFormat format = DatExportFormat.LogiqxXml,
        IProgress<SortProgress>? progress = null,
        IProgress<LogMessage>? logProgress = null,
        CancellationToken cancellationToken = default)
    {
        // A lot: DAT by DAT, never all its DATs in memory at once
        if (string.IsNullOrWhiteSpace(config.DatFile) || !_datService.HoldsSeveralDats(config.DatFile))
        {
            return await ExportSingleCatalogDatAsync(config, outputPath, format, progress, logProgress, cancellationToken).ConfigureAwait(false);
        }

        config.Filters.NormalizeDefaults();
        void Log(LogLevel level, LocalizedMessage msg) => logProgress?.Report(new LogMessage(level, msg));
        var catalog = await LoadCatalogOnlyAsync(config, Log, progress, cancellationToken).ConfigureAwait(false);
        return catalog.Dats is not null
            ? await ExportLotCatalogDatAsync(config, catalog, outputPath, format, progress, Log, cancellationToken).ConfigureAwait(false)
            : await WriteCatalogSelectionAsync(SelectFromLoadedCatalog(config, catalog, progress, Log, cancellationToken), outputPath, format, progress, logProgress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Filtered DAT of the catalog of a lot, DAT by DAT: each DAT is read (when it is read on demand), its games chosen as
    /// <see cref="SelectFromCatalogAsync"/> chooses them, its filtered DAT written to a temporary file, then the DAT and
    /// its items are forgotten. The files take their names once every DAT is written: an export stopped or in error
    /// writes nothing. The result names the DATs written, each read again from its file when asked.
    /// </summary>
    private async Task<CatalogSelectionResult> ExportLotCatalogDatAsync(
        SortConfig config,
        DatCatalog lot,
        string outputFolder,
        DatExportFormat format,
        IProgress<SortProgress>? progress,
        Action<LogLevel, LocalizedMessage> log,
        CancellationToken cancellationToken)
    {
        RefuseLotFolder(outputFolder, lot);
        ReportStep(progress, LocalizedMessage.Create("progress.reading_catalog_games"));
        var dats = lot.Dats!;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var written = new List<(string Temporary, string Target)>();
        var outputs = new List<DatLotMember>();
        var candidates = 0;
        try
        {
            foreach (var dat in dats)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wasLoaded = dat.IsLoaded;
                var matched = new Dictionary<string, IReadOnlyDictionary<string, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
                var items = ClassifyLotCatalog(config, [dat], matched, cancellationToken, tagsDats: dats.Count >= 2);
                SelectReleases(config, items, lot, new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase), null, log, matched.GetValueOrDefault);
                candidates += items.Count;

                var kept = items.Where(it => it.Status == FileStatus.Valid).Select(it => it.DatGameName).OfType<string>().ToList();
                if (kept.Count > 0)
                {
                    var filtered = dat.Catalog.CreateFilteredDat(kept);
                    var (path, clrMamePro) = LotDatTarget(filtered, dat.Name, outputFolder, format, lot, names);
                    Directory.CreateDirectory(outputFolder);
                    var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
                    written.Add((temporary, path));
                    ReportStep(progress, LocalizedMessage.Create("progress.writing_dat", ("name", Path.GetFileName(path))));
                    await WriteDatFileAsync(filtered, temporary, clrMamePro, cancellationToken).ConfigureAwait(false);
                    outputs.Add(new DatLotMember(
                        dat.Name,
                        dat.RelativePath,
                        DatLotSummary.Of(filtered),
                        DatLotKeys.Of(filtered),
                        token => _datService.ParseDatFileAsync(path, token).GetAwaiter().GetResult()));
                }

                if (!wasLoaded)
                {
                    dat.Unload();
                }
            }

            foreach (var (temporary, target) in written)
            {
                File.Move(temporary, target, overwrite: true);
            }

            written.Clear();
        }
        finally
        {
            foreach (var (temporary, _) in written)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The error that stopped the export is the one reported
                }
            }
        }

        var result = new CatalogSelectionResult(lot, [], DatCatalog.GatherLot(outputs, lot.Header.Name + DatCatalog.FilteredDatSuffix))
        {
            CandidateCount = candidates
        };
        log(LogLevel.Ok, LocalizedMessage.Create("log.catalog_selection_summary", ("kept", result.KeptCount), ("total", result.CandidateCount)));
        if (result.KeptCount == 0)
        {
            log(LogLevel.Warn, LocalizedMessage.Create("log.catalog_nothing_kept", ("path", outputFolder)));
        }

        return result;
    }

    private async Task<CatalogSelectionResult> ExportSingleCatalogDatAsync(
        SortConfig config,
        string outputPath,
        DatExportFormat format,
        IProgress<SortProgress>? progress,
        IProgress<LogMessage>? logProgress,
        CancellationToken cancellationToken)
    {
        var result = await SelectFromCatalogAsync(config, progress, logProgress, cancellationToken).ConfigureAwait(false);
        return await WriteCatalogSelectionAsync(result, outputPath, format, progress, logProgress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CatalogSelectionResult> WriteCatalogSelectionAsync(
        CatalogSelectionResult result,
        string outputPath,
        DatExportFormat format,
        IProgress<SortProgress>? progress,
        IProgress<LogMessage>? logProgress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A DAT without games would only empty the profile of a ROM manager
        if (result.KeptCount == 0)
        {
            logProgress?.Report(new LogMessage(LogLevel.Warn, LocalizedMessage.Create("log.catalog_nothing_kept", ("path", outputPath))));
            return result;
        }

        ReportStep(progress, LocalizedMessage.Create("progress.writing_dat", ("name", Path.GetFileName(outputPath))));
        await WriteDatAsync(result.FilteredDat, outputPath, format, cancellationToken, result.Catalog).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Items of the games of every DAT of a lot (<see cref="EntryClassifier.ClassifyCatalog"/>), each with its DAT as a
    /// sort gives it; the found ROMs of the games of each DAT go to <paramref name="matchedByDat"/>. Every DAT is read: the
    /// catalog is the only input.
    /// </summary>
    private List<ProcessItem> ClassifyLotCatalog(
        SortConfig config,
        IReadOnlyList<DatLotMember> dats,
        Dictionary<string, IReadOnlyDictionary<string, HashSet<string>>> matchedByDat,
        CancellationToken cancellationToken,
        bool? tagsDats = null)
    {
        var items = new List<ProcessItem>();
        foreach (var dat in dats)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var classifier = new EntryClassifier(
                config, dat.Catalog, _crcCache, _checksumService, _romParser, _filterService, _i18n, _opticalService);
            foreach (var item in classifier.ClassifyCatalog(cancellationToken))
            {
                DatLotIdentifier.AssignDat(item, dat.Name, [], tagsDats: tagsDats ?? dats.Count >= 2);
                items.Add(item);
            }

            matchedByDat[dat.Name] = classifier.GameMatchedRoms;
        }

        return items;
    }

    /// <summary>
    /// Filtered DAT of the games of <paramref name="kept"/>: of each software list, when the catalog gathers several
    /// (a software is only named in its list), gathered as the catalog; of each DAT of a lot with a game kept, as a lot.
    /// </summary>
    private static DatCatalog CreateFilteredDat(DatCatalog catalog, IReadOnlyCollection<ProcessItem> kept)
    {
        if (catalog.Dats is { } dats)
        {
            var keptByDat = kept
                .Where(it => it.DatName is not null && it.DatGameName is not null)
                .GroupBy(it => it.DatName!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(it => it.DatGameName!).ToList(), StringComparer.OrdinalIgnoreCase);
            return DatCatalog.GatherLot(
                dats.Where(dat => keptByDat.ContainsKey(dat.Name))
                    .Select(dat => new DatLotMember(dat.Name, dat.RelativePath, dat.Catalog.CreateFilteredDat(keptByDat[dat.Name])))
                    .ToList(),
                catalog.Header.Name + DatCatalog.FilteredDatSuffix);
        }

        if (catalog.SoftwareLists is not { } lists)
        {
            var dat = catalog.CreateFilteredDat(kept.Select(it => it.DatGameName).OfType<string>());
            return catalog.IsSoftwareListCatalog ? NamedAfterList(dat, catalog) : dat;
        }

        var keptByList = kept
            .Where(it => it.MameSet?.SoftwareList is not null && it.DatGameName is not null)
            .GroupBy(it => it.MameSet!.SoftwareList!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(it => it.DatGameName!).ToList(), StringComparer.OrdinalIgnoreCase);
        var filtered = lists.Values
            .Where(list => keptByList.ContainsKey(list.Header.Name))
            .Select(list => NamedAfterList(list.CreateFilteredDat(keptByList[list.Header.Name]), list))
            .ToList();
        return DatCatalog.GatherSoftwareLists(filtered, catalog.Header.Name + DatCatalog.FilteredDatSuffix);
    }

    /// <summary>
    /// The DAT of a MAME software list <paramref name="list"/> named as the list: a ROM manager names the folder of a
    /// DAT after its header (RomVault), and MAME only finds the software of a list in the folder of that name.
    /// </summary>
    private static DatCatalog NamedAfterList(DatCatalog dat, DatCatalog list)
    {
        dat.Header.Name = DatListFileName(list);
        dat.Header.Description = string.IsNullOrEmpty(list.Header.Description) ? dat.Header.Name : list.Header.Description;
        return dat;
    }

    /// <summary>
    /// Writes a DAT to <paramref name="outputPath"/> in <paramref name="format"/>. The DATs of MAME software lists (one
    /// or several, <see cref="DatCatalog.IsSoftwareListCatalog"/> of <paramref name="source"/>) are written into the
    /// folder <paramref name="outputPath"/>, a DAT per list at its root (<c>megadriv.xml</c>, or
    /// <c>fixDat_megadriv.xml</c> for a <paramref name="fixDat"/>), as MAME and a ROM manager look for them; a list read
    /// from a file of MAME (<see cref="DatHeader.SoftwareListFile"/>) in MAME's own format, any other in Logiqx XML,
    /// whatever <paramref name="format"/>. The DATs of a lot (<see cref="DatCatalog.Dats"/> of <paramref name="dat"/>) are
    /// written into the folder <paramref name="outputPath"/> too (<see cref="WriteLotDatsAsync"/>). No DAT is ever written
    /// over a file the catalog was read from.
    /// </summary>
    private async Task WriteDatAsync(
        DatCatalog dat,
        string outputPath,
        DatExportFormat format,
        CancellationToken cancellationToken,
        DatCatalog? source = null,
        bool fixDat = false)
    {
        var catalog = source ?? dat;
        if (dat.Dats is { } lotDats)
        {
            await WriteLotDatsAsync(lotDats, outputPath, format, catalog, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!catalog.IsSoftwareListCatalog)
        {
            RefuseSourceOverwrite(outputPath, catalog);
            if (DatService.IsClrMameProOutput(format, outputPath))
            {
                await dat.WriteClrMameProAsync(outputPath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await dat.WriteLogiqxXmlAsync(outputPath, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        // Every path is checked before the first DAT is written
        IEnumerable<DatCatalog> lists = dat.SoftwareLists is { } several ? several.Values : [dat];
        var targets = lists
            .Select(list =>
            {
                var name = DatListFileName(list);
                var path = Path.Combine(outputPath, _organizer.SanitizePathSegment((fixDat ? "fixDat_" : string.Empty) + name) + ".xml");
                var sourceHeader = (catalog.SoftwareLists is { } sourceLists ? sourceLists.GetValueOrDefault(name) : catalog)?.Header;
                RefuseSourceOverwrite(path, catalog);
                return (List: list, Path: path, Source: fixDat || sourceHeader?.SoftwareListFile is null ? null : sourceHeader);
            })
            .ToList();

        Directory.CreateDirectory(outputPath);
        foreach (var (list, path, sourceHeader) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceHeader is not null)
            {
                await DatService.WriteMameSoftwareListAsync(list, sourceHeader, path, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await list.WriteLogiqxXmlAsync(path, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Writes the DATs of a lot into the folder <paramref name="outputFolder"/>, a file per DAT at its root (flat, whatever
    /// the folders of the lot), named after its header: in Logiqx XML (<c>.xml</c>) or CLRMAMEPRO (<c>.dat</c>) as
    /// <paramref name="format"/> says, <see cref="DatExportFormat.Auto"/> taking the format of the DAT of the lot it comes
    /// from (<see cref="DatHeader.IsClrMameProSource"/>). Every path is checked before the first DAT is written; the folder
    /// of the lot read is refused, since the next sort would read the DATs exported there.
    /// </summary>
    private async Task WriteLotDatsAsync(
        IReadOnlyList<DatLotMember> dats,
        string outputFolder,
        DatExportFormat format,
        DatCatalog source,
        CancellationToken cancellationToken)
    {
        RefuseLotFolder(outputFolder, source);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new List<(DatCatalog Dat, string Path, bool ClrMamePro)>(dats.Count);
        foreach (var dat in dats)
        {
            var (path, clrMamePro) = LotDatTarget(dat.Catalog, dat.Name, outputFolder, format, source, names);
            targets.Add((dat.Catalog, path, clrMamePro));
        }

        Directory.CreateDirectory(outputFolder);
        foreach (var (dat, path, clrMamePro) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteDatFileAsync(dat, path, clrMamePro, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// File of the DAT <paramref name="dat"/> exported from the DAT <paramref name="datName"/> of a lot: at the root of
    /// <paramref name="outputFolder"/>, named after its header (a number when <paramref name="names"/> already has the
    /// name), in the format asked or, for <see cref="DatExportFormat.Auto"/>, that of the DAT read; refused when it is a
    /// file the lot was read from.
    /// </summary>
    private (string Path, bool ClrMamePro) LotDatTarget(
        DatCatalog dat,
        string datName,
        string outputFolder,
        DatExportFormat format,
        DatCatalog source,
        HashSet<string> names)
    {
        var clrMamePro = format switch
        {
            DatExportFormat.ClrMamePro => true,
            DatExportFormat.Auto => source.DatMemberFor(datName)?.Header.IsClrMameProSource == true,
            _ => false
        };
        var extension = clrMamePro ? ".dat" : ".xml";
        var name = _organizer.SanitizePathSegment(string.IsNullOrWhiteSpace(dat.Header.Name) ? datName : dat.Header.Name);

        // Two names the sanitization makes the same (a forbidden character, a case) get a number
        var fileName = name + extension;
        for (var copy = 2; !names.Add(fileName); copy++)
        {
            fileName = $"{name} ({copy}){extension}";
        }

        var path = Path.Combine(outputFolder, fileName);
        RefuseSourceOverwrite(path, source);
        return (path, clrMamePro);
    }

    private static Task WriteDatFileAsync(DatCatalog dat, string path, bool clrMamePro, CancellationToken cancellationToken) =>
        clrMamePro ? dat.WriteClrMameProAsync(path, cancellationToken) : dat.WriteLogiqxXmlAsync(path, cancellationToken);

    /// <summary>
    /// Refuses to export DATs into the folder a lot was read from, or into one of its subfolders
    /// (<see cref="DatCatalog.LotFolder"/>): the next sort would read them as DATs of the lot.
    /// </summary>
    private void RefuseLotFolder(string outputFolder, DatCatalog? catalog)
    {
        if (catalog?.LotFolder is not { } lotFolder)
        {
            return;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputFolder));
        var folder = Path.TrimEndingDirectorySeparator(lotFolder);
        if (string.Equals(output, folder, comparison) || PathOrganizerService.IsInsideDirectory(output, folder))
        {
            throw new InvalidOperationException(_i18n.Tr("export.inside_dat_folder", ("path", outputFolder), ("folder", lotFolder)));
        }
    }

    /// <summary>
    /// The missing ROMs of each DAT of a lot, in the order of the lot: only the DATs with ROMs missing and a game found.
    /// A DAT none of whose ROMs is present is left out, whatever the list given: its Miss List would be the whole DAT. Only
    /// the DATs in memory are gone through: a DAT no file needed was not read again, and has no game found.
    /// </summary>
    private static List<(DatLotMember Dat, List<DatRomEntry> Missing)> GroupMissingByDat(IReadOnlyList<DatLotMember> dats, IEnumerable<DatRomEntry> missingRoms)
    {
        var missing = new HashSet<DatRomEntry>(missingRoms, ReferenceEqualityComparer.Instance);
        var groups = new List<(DatLotMember Dat, List<DatRomEntry> Missing)>();
        foreach (var dat in dats.Where(dat => dat.IsLoaded))
        {
            var datMissing = dat.Catalog.Entries.Where(missing.Contains).ToList();
            if (datMissing.Count > 0 && datMissing.Count < dat.Catalog.Entries.Count)
            {
                groups.Add((dat, datMissing));
            }
        }

        return groups;
    }

    /// <summary>
    /// Refuses an export to a file <paramref name="catalog"/> was read from (<see cref="DatCatalog.IsReadFrom"/>): it
    /// would replace the DAT, or the hash file of MAME, by a part of it.
    /// </summary>
    private void RefuseSourceOverwrite(string path, DatCatalog? catalog)
    {
        if (catalog?.IsReadFrom(path) == true)
        {
            throw new InvalidOperationException(_i18n.Tr(
                catalog.IsSoftwareListCatalog ? "dat.export_overwrites_source" : "export.overwrites_dat",
                ("path", path)));
        }
    }

    // Name of the DAT file of a list: the list, whatever the header of a filtered DAT or a fixdat adds to it
    private static string DatListFileName(DatCatalog list) =>
        list.Games.Select(g => g.SoftwareList).FirstOrDefault(name => !string.IsNullOrEmpty(name)) ?? list.Header.Name;

    /// <summary>Reports a step whose length is not known in advance: no count, no remaining time.</summary>
    private static void ReportStep(IProgress<SortProgress>? progress, LocalizedMessage message) =>
        progress?.Report(new SortProgress(0, 0, 0, 0, 0.0, message));

    /// <summary>Progress message of the transfer of a file, named after the transfer mode.</summary>
    /// <summary>
    /// Whether the transfers write <paramref name="item"/> (kept or isolated in <c>_unmatched</c>), and so count in the
    /// progress of a real sort.
    /// </summary>
    private static bool IsWrittenByTransfer(ProcessItem item) => SelectionFilter.IsWritten(item);

    internal static LocalizedMessage DescribeTransfer(TransferMode mode, string name) =>
        LocalizedMessage.Create(
            mode switch
            {
                TransferMode.Move => "progress.moving_file",
                TransferMode.HardLink => "progress.hardlinking_file",
                TransferMode.SymbolicLink => "progress.symlinking_file",
                TransferMode.Zip => "progress.zipping_file",
                _ => "progress.copying_file"
            },
            ("name", name));

    private void ValidateDirectories(SortConfig config, bool isDryRun, Action<LogLevel, LocalizedMessage> log)
    {
        if (string.IsNullOrWhiteSpace(config.SourceDir) || !Directory.Exists(config.SourceDir))
        {
            throw new DirectoryNotFoundException(_i18n.Tr("log.source_dir_not_found", ("path", config.SourceDir)));
        }

        // The CHD folder of a MAME run is checked once the DAT says it is one
        var overlapKey = SortConfigValidator.GetErrorKey(config, isMameRun: false);
        if (overlapKey is not null)
        {
            throw new InvalidOperationException(_i18n.Tr(overlapKey));
        }

        var actionName = LocalizedMessage.Create(isDryRun ? "action.dry_run" : "action.sort_execution");
        log(LogLevel.Info, LocalizedMessage.Create("log.starting_operation", ("action", actionName)));
        log(LogLevel.Info, LocalizedMessage.Create("log.source_path", ("path", config.SourceDir)));
        log(LogLevel.Info, LocalizedMessage.Create("log.dest_path", ("path", config.DestinationDir)));
    }

    private async Task<DatCatalog?> LoadDatCatalogAsync(
        SortConfig config,
        Action<LogLevel, LocalizedMessage> log,
        IProgress<SortProgress>? progress,
        int totalFiles,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.DatFile))
        {
            log(LogLevel.Info, LocalizedMessage.Create("log.no_dat_specified_fallback_filename"));
            return null;
        }

        // A folder (or an archive) holds a lot of DATs, or the DATs of several MAME software lists
        if (!File.Exists(config.DatFile) && !Directory.Exists(config.DatFile))
        {
            throw new FileNotFoundException(_i18n.Tr("log.dat_file_not_found", ("path", config.DatFile)));
        }

        log(LogLevel.Info, LocalizedMessage.Create("log.dat_loading", ("path", config.DatFile)));
        progress?.Report(new SortProgress(
            ProcessedCount: 0,
            TotalCount: totalFiles,
            ProcessedBytes: 0,
            TotalBytes: totalBytes,
            EtaSeconds: 0.0,
            Message: LocalizedMessage.Create("progress.loading_dat", ("name", Path.GetFileName(config.DatFile)))));
        try
        {
            var datCatalog = await _datService.ParseDatFileAsync(
                config.DatFile,
                new DatFolderOptions(config.DatRecursive, config.ExcludedDats, LoadLotDatsOnDemand),
                cancellationToken).ConfigureAwait(false);

            if (datCatalog.Dats is { } dats)
            {
                log(LogLevel.Ok, LocalizedMessage.Create(
                    "log.dat_lot_loaded",
                    ("count", dats.Count),
                    ("roms", dats.Sum(dat => dat.Summary.RomCount)),
                    ("path", config.DatFile)));
                return datCatalog;
            }

            var name = !string.IsNullOrEmpty(datCatalog.Header.Name)
                ? datCatalog.Header.Name
                : Path.GetFileName(config.DatFile);
            log(LogLevel.Ok, LocalizedMessage.Create("log.dat_loaded", ("count", datCatalog.Entries.Count), ("name", name)));
            if (datCatalog.SoftwareLists is { } lists)
            {
                log(LogLevel.Info, LocalizedMessage.Create("log.dat_software_lists", ("count", lists.Count)));
            }

            return datCatalog;
        }
        catch (DatFolderException ex)
        {
            throw new InvalidOperationException(_i18n.Tr("log.dat_load_error", ("error", _i18n.Format(ex.Reason))), ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(_i18n.Tr("log.dat_load_error", ("error", ex.Message)), ex);
        }
    }

    private List<DatRomEntry> UpdateDatStatistics(
        ProcessStats stats,
        DatCatalog? datCatalog,
        HashSet<DatRomEntry> matchedDatEntries,
        HashSet<DatRomEntry> nameMatchedDatEntries,
        Action<LogLevel, LocalizedMessage> log,
        IReadOnlyList<DatRomEntry>? countedEntries = null)
    {
        if (datCatalog is null)
        {
            return [];
        }

        // The entries of the catalog, or of the software lists present in the source
        var entries = countedEntries ?? datCatalog.Entries;

        // Entries of games designated by the name of a CHD are present but not verified: neither matched nor missing
        var nameOnly = nameMatchedDatEntries.Where(e => !matchedDatEntries.Contains(e)).ToHashSet(ReferenceEqualityComparer.Instance);
        var datTotal = entries.Count;
        var datMatched = matchedDatEntries.Count;
        var datMissing = Math.Max(0, datTotal - datMatched - nameOnly.Count);
        var datPct = datTotal > 0 ? ((datMatched + nameOnly.Count) / (double)datTotal * 100.0) : 0.0;

        stats.DatTotalRoms = datTotal;
        stats.DatMatchedCount = datMatched;
        stats.DatNameMatchedCount = nameOnly.Count;
        stats.DatMissingCount = datMissing;
        stats.DatCompletionPct = datPct;

        var missingRoms = entries
            .Where(e => !matchedDatEntries.Contains(e) && !nameOnly.Contains(e))
            .ToList();

        if (nameOnly.Count > 0)
        {
            log(LogLevel.Warn, LocalizedMessage.Create("log.dat_name_matched_summary", ("count", nameOnly.Count)));
        }

        log(LogLevel.Info, LocalizedMessage.Create(
            "log.dat_completion_summary",
            ("matched", datMatched + nameOnly.Count),
            ("total", datTotal),
            ("pct", datPct.ToString("F1", CultureInfo.InvariantCulture)),
            ("missing", datMissing)));

        return missingRoms;
    }

    /// <summary>
    /// Statistics of a lot of DATs: the totals of all its DATs, and what was found of each of them
    /// (<see cref="ProcessStats.PerDat"/>), named in the log for the DATs with a game found. The missing ROMs, in the
    /// order of the lot, of the DATs with a game found only: a DAT none of whose games is in the source is only counted
    /// (its Miss List would be the whole DAT), and has no fixdat.
    /// </summary>
    private static List<DatRomEntry> UpdateLotStatistics(ProcessStats stats, DatCatalog catalog, DatLotIdentifier lot, Action<LogLevel, LocalizedMessage> log)
    {
        var missingRoms = new List<DatRomEntry>();
        var perDat = new List<DatLotStats>();
        foreach (var dat in catalog.Dats!)
        {
            // A DAT no file was compared with (not read again on demand): none of its ROMs is present
            if (lot.ClassifierFor(dat.Name) is not { } classifier)
            {
                var summary = dat.Summary;
                perDat.Add(new DatLotStats(dat.Name, summary.Convention, summary.RomCount, 0, 0, summary.RomCount, 0));
                continue;
            }

            var matched = classifier.MatchedDatEntries;
            var nameOnly = new HashSet<DatRomEntry>(classifier.NameMatchedDatEntries.Where(e => !matched.Contains(e)), ReferenceEqualityComparer.Instance);
            var entries = dat.Catalog.Entries;
            var missing = entries.Where(e => !matched.Contains(e) && !nameOnly.Contains(e)).ToList();
            var gamesFound = matched.Concat(nameOnly)
                .Select(e => e.GameName)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            perDat.Add(new DatLotStats(
                dat.Name,
                classifier.DatConvention,
                entries.Count,
                matched.Count,
                nameOnly.Count,
                missing.Count,
                gamesFound));
            if (gamesFound > 0)
            {
                missingRoms.AddRange(missing);
            }
        }

        stats.PerDat = perDat;
        stats.DatTotalRoms = perDat.Sum(d => d.TotalRoms);
        stats.DatMatchedCount = perDat.Sum(d => d.MatchedCount);
        stats.DatNameMatchedCount = perDat.Sum(d => d.NameMatchedCount);
        stats.DatMissingCount = perDat.Sum(d => d.MissingCount);
        stats.DatCompletionPct = stats.DatTotalRoms > 0
            ? (stats.DatMatchedCount + stats.DatNameMatchedCount) / (double)stats.DatTotalRoms * 100.0
            : 0.0;

        foreach (var dat in perDat.Where(d => d.GamesFound > 0))
        {
            log(LogLevel.Info, LocalizedMessage.Create(
                "log.dat_lot_completion",
                ("dat", dat.Name),
                ("games", dat.GamesFound),
                ("matched", dat.MatchedCount + dat.NameMatchedCount),
                ("total", dat.TotalRoms),
                ("pct", dat.CompletionPct.ToString("F1", CultureInfo.InvariantCulture))));
        }

        if (perDat.Count(d => d.GamesFound == 0) is var nothingFound and > 0)
        {
            log(LogLevel.Info, LocalizedMessage.Create("log.dat_lot_nothing_found", ("count", nothingFound)));
        }

        if (stats.DatNameMatchedCount > 0)
        {
            log(LogLevel.Warn, LocalizedMessage.Create("log.dat_name_matched_summary", ("count", stats.DatNameMatchedCount)));
        }

        log(LogLevel.Info, LocalizedMessage.Create(
            "log.dat_completion_summary",
            ("matched", stats.DatMatchedCount + stats.DatNameMatchedCount),
            ("total", stats.DatTotalRoms),
            ("pct", stats.DatCompletionPct.ToString("F1", CultureInfo.InvariantCulture)),
            ("missing", stats.DatMissingCount)));
        return missingRoms;
    }

    public void RecalculateGameItems(
        IList<ProcessItem> gameItems,
        SortConfig config,
        DatCatalog? datCatalog = null,
        IReadOnlyCollection<ProcessItem>? allItems = null)
    {
        if (gameItems.Count == 0)
        {
            return;
        }

        config.Filters.NormalizeDefaults();

        // A lot: each DAT is recalculated with its catalog, as the run selected its releases (a game is of one DAT)
        if (datCatalog?.Dats is not null)
        {
            foreach (var (datItems, datName) in GroupByDat(gameItems))
            {
                RecalculateGameItems(datItems, config, datName is null ? null : datCatalog.DatFor(datName), allItems);
            }

            return;
        }

        var gameMatchedRoms = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in gameItems)
        {
            it.Is1G1RWinner = false;
            it.WinnerReference = null;
            it.EliminationDetail = null;
            it.EliminatedBy = TiebreakCriterion.None;
            it.IsMultidiskComplete = true;
            it.IsAlreadyInDestination = false;

            if (!it.Parsed.IsConform)
            {
                it.Status = FileStatus.Unmatched;
                continue;
            }

            // Set aside by the identification (not in the DAT, damaged...): its name, which may give the key of an
            // identified game, does not make it a release of that game, as in the simulation
            if (it.IdentificationFailure != IdentificationFailure.None)
            {
                continue;
            }

            if (datCatalog is not null && !string.IsNullOrEmpty(it.TargetFilename))
            {
                var game = datCatalog.GetGameForRom(it.TargetFilename, it.DatGameName);
                if (game is not null)
                {
                    if (!gameMatchedRoms.TryGetValue(game.Name, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        gameMatchedRoms[game.Name] = set;
                    }
                    set.Add(it.TargetFilename);
                }
            }

            var (isKept, reason) = _filterService.EvaluateFilter(it.Parsed, config.Filters);
            if (!isKept)
            {
                it.Status = FileStatus.Excluded;
                it.StatusReason = reason;
            }
            else
            {
                it.Status = FileStatus.Valid;
                it.StatusReason = null;
            }
        }

        new ReleaseSelector(config, _filterService, _i18n).Select(gameItems, datCatalog, gameMatchedRoms, allItems);
    }

    public ProcessStats RecomputeStats(
        IReadOnlyCollection<ProcessItem> items,
        ProcessStats? baseStats = null)
    {
        var totalBytes = items.Sum(it => it.FileSize + it.RelatedItems.Sum(r => r.FileSize));
        var stats = new ProcessStats
        {
            TotalScanned = items.Count,
            ProcessedCount = items.Count,
            TotalBytes = totalBytes,
            ProcessedBytes = totalBytes,
            DatTotalRoms = baseStats?.DatTotalRoms ?? 0,
            DatMatchedCount = baseStats?.DatMatchedCount ?? 0,
            DatNameMatchedCount = baseStats?.DatNameMatchedCount ?? 0,
            DatMissingCount = baseStats?.DatMissingCount ?? 0,
            DatCompletionPct = baseStats?.DatCompletionPct ?? 0.0,
            PerDat = baseStats?.PerDat
        };

        foreach (var it in items)
        {
            var itemTotalBytes = it.FileSize + it.RelatedItems.Sum(r => r.FileSize);
            switch (it.Status)
            {
                case FileStatus.Valid:
                    stats.ValidCount++;
                    stats.ValidBytes += itemTotalBytes;
                    break;
                case FileStatus.Excluded when it.IsOutsideSelection:
                    stats.NotSelectedCount++;
                    stats.NotSelectedBytes += itemTotalBytes;
                    break;
                case FileStatus.Excluded:
                    stats.ExcludedCount++;
                    stats.ExcludedBytes += itemTotalBytes;
                    break;
                case FileStatus.Unmatched:
                    stats.UnmatchedCount++;
                    stats.UnmatchedBytes += itemTotalBytes;
                    if (it.IsNotIsolated)
                    {
                        stats.NotIsolatedCount++;
                        stats.NotIsolatedBytes += itemTotalBytes;
                    }

                    break;
                case FileStatus.Duplicate:
                    stats.DuplicateCount++;
                    if (it.IsAlreadyInDestination)
                    {
                        stats.AlreadyInDestinationCount++;
                    }

                    break;
                case FileStatus.Error:
                    stats.ErrorCount++;
                    break;
            }
        }

        return stats;
    }

    public DestinationState InspectDestination(string path) => DestinationGuard.Inspect(path);

    // Bytes of an export held before they are written to the file
    private const int ExportFlushThreshold = 1 << 16;

    /// <summary>
    /// Writes the JSON report as it goes, item by item: a report of a large collection is never held in memory as a
    /// whole. The text is that of the serialization of the whole report, indented.
    /// </summary>
    public Task ExportReportToJsonAsync(
        IEnumerable<ProcessItem> items,
        ProcessStats stats,
        string outputPath,
        CancellationToken cancellationToken = default) =>
        WriteStreamedExportAsync(outputPath, async stream =>
        {
            await using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            json.WriteStartObject();
            json.WriteStartObject("summary");
            json.WriteNumber("total_scanned", stats.TotalScanned);
            json.WriteNumber("total_bytes", stats.TotalBytes);
            json.WriteNumber("valid_count", stats.ValidCount);
            json.WriteNumber("valid_bytes", stats.ValidBytes);
            json.WriteNumber("excluded_count", stats.ExcludedCount);
            json.WriteNumber("excluded_bytes", stats.ExcludedBytes);
            json.WriteNumber("unmatched_count", stats.UnmatchedCount);
            json.WriteNumber("unmatched_bytes", stats.UnmatchedBytes);
            json.WriteNumber("duplicate_count", stats.DuplicateCount);
            json.WriteNumber("error_count", stats.ErrorCount);
            json.WriteString("timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            if (stats.DatTotalRoms > 0)
            {
                json.WriteStartObject("dat_summary");
                json.WriteNumber("total_roms", stats.DatTotalRoms);
                json.WriteNumber("matched_count", stats.DatMatchedCount);
                json.WriteNumber("missing_count", stats.DatMissingCount);
                json.WriteString("completion_pct", $"{stats.DatCompletionPct.ToString("F1", CultureInfo.InvariantCulture)}%");
                json.WriteEndObject();
            }

            // A lot: what was found of each of its DATs
            if (stats.PerDat is { } perDat)
            {
                json.WriteStartArray("dats");
                foreach (var dat in perDat)
                {
                    json.WriteStartObject();
                    json.WriteString("name", dat.Name);
                    json.WriteString("convention", dat.Convention.ToConfigString());
                    json.WriteNumber("total_roms", dat.TotalRoms);
                    json.WriteNumber("matched_count", dat.MatchedCount + dat.NameMatchedCount);
                    json.WriteNumber("missing_count", dat.MissingCount);
                    json.WriteNumber("games_found", dat.GamesFound);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
            }

            json.WriteEndObject();
            json.WriteStartArray("files");
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                json.WriteStartObject();
                json.WriteString("source_file", Path.GetFileName(item.SourcePath));
                json.WriteString("archive_member", item.ArchiveMember ?? string.Empty);
                json.WriteString("source_path", item.SourcePath);
                json.WriteString("crc32", item.Crc32);
                json.WriteNumber("size_bytes", item.FileSize);
                json.WriteBoolean("is_tosec_conform", item.Parsed.IsTosecConform);
                json.WriteString("title", item.Parsed.Title);
                json.WriteString("version", item.Parsed.Version);
                json.WriteString("publisher", item.Parsed.Publisher);
                json.WriteString("date", item.Parsed.Date);
                json.WriteString("system", item.Parsed.System ?? string.Empty);
                json.WriteString("video", item.Parsed.Video ?? string.Empty);
                json.WriteString("country", item.Parsed.Country ?? string.Empty);
                json.WriteString("language", item.Parsed.Language ?? string.Empty);
                json.WriteString("dump_status", item.Parsed.DumpStatusSummary);
                json.WriteString("status", item.Status.ToValueString());
                json.WriteString("status_reason", _i18n.Format(item.StatusReason));
                json.WriteBoolean("is_1g1r_winner", item.Is1G1RWinner);
                json.WriteString("winner_reference", item.WinnerReference ?? string.Empty);
                json.WriteString("target_path", item.FinalTargetPath ?? item.TargetPath ?? string.Empty);
                json.WriteString("error_message", item.ErrorMessage ?? string.Empty);
                if (stats.PerDat is not null)
                {
                    json.WriteString("dat", item.DatName ?? string.Empty);
                }

                json.WriteEndObject();
                if (json.BytesPending > ExportFlushThreshold)
                {
                    await json.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            json.WriteEndArray();
            json.WriteEndObject();
            await json.FlushAsync(cancellationToken).ConfigureAwait(false);
        });

    /// <summary>Writes the CSV report as it goes, line by line, as the JSON report.</summary>
    public Task ExportReportToCsvAsync(
        IEnumerable<ProcessItem> items,
        string outputPath,
        CancellationToken cancellationToken = default) =>
        WriteStreamedExportAsync(outputPath, async stream =>
        {
            // The files of a lot name their DAT, in a last column
            var list = items as IReadOnlyCollection<ProcessItem> ?? [.. items];
            var namesDats = list.Any(it => it.DatName is not null);
            string[] headers =
            [
                "source_file", "archive_member", "source_path", "crc32", "size_bytes",
                "is_tosec_conform", "title", "version", "publisher", "date",
                "system", "video", "country", "language", "dump_status",
                "status", "status_reason", "is_1g1r_winner", "winner_reference",
                "target_path", "error_message", .. namesDats ? new[] { "dat" } : []
            ];

            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ExportFlushThreshold);
            await writer.WriteLineAsync(string.Join(";", headers)).ConfigureAwait(false);
            foreach (var item in list)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string[] row =
                [
                    EscapeCsv(Path.GetFileName(item.SourcePath)),
                    EscapeCsv(item.ArchiveMember ?? string.Empty),
                    EscapeCsv(item.SourcePath),
                    EscapeCsv(item.Crc32),
                    item.FileSize.ToString(CultureInfo.InvariantCulture),
                    item.Parsed.IsTosecConform.ToString(),
                    EscapeCsv(item.Parsed.Title),
                    EscapeCsv(item.Parsed.Version),
                    EscapeCsv(item.Parsed.Publisher),
                    EscapeCsv(item.Parsed.Date),
                    EscapeCsv(item.Parsed.System ?? string.Empty),
                    EscapeCsv(item.Parsed.Video ?? string.Empty),
                    EscapeCsv(item.Parsed.Country ?? string.Empty),
                    EscapeCsv(item.Parsed.Language ?? string.Empty),
                    EscapeCsv(item.Parsed.DumpStatusSummary),
                    EscapeCsv(item.Status.ToValueString()),
                    EscapeCsv(_i18n.Format(item.StatusReason)),
                    item.Is1G1RWinner.ToString(),
                    EscapeCsv(item.WinnerReference ?? string.Empty),
                    EscapeCsv(item.FinalTargetPath ?? item.TargetPath ?? string.Empty),
                    EscapeCsv(item.ErrorMessage ?? string.Empty),
                    .. namesDats ? new[] { EscapeCsv(item.DatName ?? string.Empty) } : []
                ];
                await writer.WriteLineAsync(string.Join(";", row).AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        });

    /// <summary>
    /// Writes an export to <paramref name="outputPath"/> as <paramref name="write"/> produces it. An export that fails or
    /// is cancelled leaves no partial file.
    /// </summary>
    private static async Task WriteStreamedExportAsync(string outputPath, Func<FileStream, Task> write)
    {
        // Only a file the export opened is deleted on a failure
        var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, ExportFlushThreshold, useAsync: true);
        try
        {
            await using (stream.ConfigureAwait(false))
            {
                await write(stream).ConfigureAwait(false);
            }
        }
        catch
        {
            try
            {
                File.Delete(outputPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The error that stopped the export is the one reported
            }

            throw;
        }
    }

    public async Task ExportMissListToCsvAsync(
        IEnumerable<DatRomEntry> missingRoms,
        DatCatalog datCatalog,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        RefuseSourceOverwrite(outputPath, datCatalog);
        var sb = new StringBuilder();

        // A lot names the DAT of each ROM, in a first column
        if (datCatalog.Dats is { } dats)
        {
            sb.AppendLine("dat;game;rom_name;size;crc;md5;sha1");
            foreach (var (dat, missing) in GroupMissingByDat(dats, missingRoms))
            {
                foreach (var r in missing)
                {
                    sb.AppendLine(string.Join(";", [EscapeCsv(dat.Name), .. MissingRomRow(datCatalog, r)]));
                }
            }
        }
        else
        {
            sb.AppendLine("game;rom_name;size;crc;md5;sha1");
            foreach (var r in missingRoms)
            {
                sb.AppendLine(string.Join(";", MissingRomRow(datCatalog, r)));
            }
        }

        await File.WriteAllTextAsync(outputPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken).ConfigureAwait(false);
    }

    // Columns of a missing ROM in the CSV Miss List: its game, its name, its size and checksums
    private static string[] MissingRomRow(DatCatalog datCatalog, DatRomEntry r) =>
    [
        EscapeCsv(MissingGameName(datCatalog, r)),
        EscapeCsv(r.Name),
        r.Size.ToString(CultureInfo.InvariantCulture),
        EscapeCsv(r.Crc),
        EscapeCsv(r.Md5 ?? string.Empty),
        EscapeCsv(r.Sha1 ?? string.Empty)
    ];

    // Game of a missing ROM, preceded by its software list when the catalog gathers several
    private static string MissingGameName(DatCatalog catalog, DatRomEntry rom) =>
        catalog.SoftwareListOf(rom) is { } list ? $"{list}/{rom.GameName}" : rom.GameName ?? string.Empty;

    public async Task ExportMissListToTxtAsync(
        IEnumerable<DatRomEntry> missingRoms,
        DatCatalog datCatalog,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        RefuseSourceOverwrite(outputPath, datCatalog);
        if (datCatalog.Dats is { } dats)
        {
            await File.WriteAllTextAsync(outputPath, LotMissListText(datCatalog, GroupMissingByDat(dats, missingRoms)), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            return;
        }

        var missingList = missingRoms.ToList();
        var headerName = !string.IsNullOrEmpty(datCatalog.Header.Name) ? datCatalog.Header.Name : "Collection";
        var version = !string.IsNullOrEmpty(datCatalog.Header.Version) ? datCatalog.Header.Version : "Unknown";

        var sb = new StringBuilder();
        sb.AppendLine($"# Miss List: {headerName}");
        sb.AppendLine($"# Version: {version}");
        sb.AppendLine($"# Total cataloged ROMs: {datCatalog.Entries.Count}");
        sb.AppendLine($"# Missing ROMs: {missingList.Count}");
        sb.AppendLine();
        foreach (var r in missingList)
        {
            // The ROMs of several software lists are named with their list and their software, unique only there
            sb.AppendLine(datCatalog.SoftwareListOf(r) is { } list ? $"{list}/{r.GameName}/{r.Name}" : r.Name);
        }

        await File.WriteAllTextAsync(outputPath, sb.ToString(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Text Miss List of a lot: a section per DAT with ROMs missing and a game found, in the order of the lot, its ROMs
    /// under it.
    /// </summary>
    private static string LotMissListText(DatCatalog lot, List<(DatLotMember Dat, List<DatRomEntry> Missing)> groups)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Miss List: {(string.IsNullOrEmpty(lot.Header.Name) ? "Collection" : lot.Header.Name)}");
        sb.AppendLine($"# DATs: {groups.Count}");
        sb.AppendLine($"# Total cataloged ROMs: {groups.Sum(g => g.Dat.Catalog.Entries.Count)}");
        sb.AppendLine($"# Missing ROMs: {groups.Sum(g => g.Missing.Count)}");
        foreach (var (dat, missing) in groups)
        {
            sb.AppendLine();
            sb.AppendLine($"## {dat.Name} ({missing.Count}/{dat.Catalog.Entries.Count})");
            foreach (var r in missing)
            {
                sb.AppendLine(r.Name);
            }
        }

        return sb.ToString();
    }

    public async Task<DatCatalog> ExportFixDatAsync(
        IEnumerable<DatRomEntry> missingRoms,
        DatCatalog datCatalog,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        DatCatalog fixDat;
        if (datCatalog.SoftwareLists is { } lists)
        {
            // A fixdat per software list that lacks something, into the folder outputPath
            var missing = new HashSet<DatRomEntry>(missingRoms, ReferenceEqualityComparer.Instance);
            var fixDats = lists.Values
                .Select(list => list.CreateFixDat(list.Games.SelectMany(g => g.Roms).Where(missing.Contains).ToList()))
                .Where(list => list.Games.Count > 0)
                .ToList();
            fixDat = DatCatalog.GatherSoftwareLists(fixDats, $"fixDat_{datCatalog.Header.Name}");
        }
        else if (datCatalog.Dats is { } dats)
        {
            // A fixdat per DAT of the lot with ROMs missing and a game found, into the folder outputPath
            fixDat = DatCatalog.GatherLot(
                GroupMissingByDat(dats, missingRoms).Select(g => new DatLotMember(g.Dat.Name, g.Dat.RelativePath, g.Dat.Catalog.CreateFixDat(g.Missing))).ToList(),
                $"fixDat_{datCatalog.Header.Name}");
        }
        else
        {
            fixDat = datCatalog.CreateFixDat(missingRoms);
        }

        await WriteDatAsync(fixDat, outputPath, DatExportFormat.LogiqxXml, cancellationToken, datCatalog, fixDat: true).ConfigureAwait(false);
        return fixDat;
    }

    /// <summary>
    /// Categories of the category file of a MAME run; a file that cannot be read is reported and the run goes on
    /// without categories.
    /// </summary>
    private static MameCategories? LoadMameCategories(string? path, Action<LogLevel, LocalizedMessage> log)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var categories = MameCategoryFile.Read(path);
            log(categories.Count == 0 ? LogLevel.Warn : LogLevel.Info, categories.Count == 0
                ? LocalizedMessage.Create("log.mame_categories_empty", ("path", path))
                : LocalizedMessage.Create("log.mame_categories_loaded", ("count", categories.Count), ("genres", categories.Genres.Count), ("path", path)));
            return categories;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            log(LogLevel.Warn, LocalizedMessage.Create("log.mame_categories_unreadable", ("path", path), ("error", ex.Message)));
            return null;
        }
    }

    /// <summary>
    /// Ratings of the rating file of a MAME run; a file that cannot be read is reported and the run goes on without
    /// ratings.
    /// </summary>
    private static MameRatings? LoadMameRatings(string? path, Action<LogLevel, LocalizedMessage> log)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var ratings = MameRatingFile.Read(path);
            log(ratings.Count == 0 ? LogLevel.Warn : LogLevel.Info, ratings.Count == 0
                ? LocalizedMessage.Create("log.mame_ratings_empty", ("path", path))
                : LocalizedMessage.Create("log.mame_ratings_loaded", ("count", ratings.Count), ("path", path)));
            return ratings.Count == 0 ? null : ratings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            log(LogLevel.Warn, LocalizedMessage.Create("log.mame_ratings_unreadable", ("path", path), ("error", ex.Message)));
            return null;
        }
    }

    public async Task<DatCatalog> ExportDatAsync(
        IEnumerable<ProcessItem> items,
        string outputPath,
        DatExportFormat format = DatExportFormat.LogiqxXml,
        DatCatalog? datCatalog = null,
        DatHeader? headerOverride = null,
        bool validOnly = true,
        bool computeMissingHashes = true,
        IProgress<(int Current, int Total, string Filename)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        RefuseSourceOverwrite(outputPath, datCatalog);
        if (datCatalog?.Dats is not null && headerOverride is null)
        {
            return await ExportLotDatsAsync(items, outputPath, format, datCatalog, validOnly, computeMissingHashes, progress, cancellationToken).ConfigureAwait(false);
        }

        return datCatalog is null || headerOverride is not null || !datCatalog.IsSoftwareListCatalog
            ? await _datService.ExportDatFileAsync(
                items,
                outputPath,
                format,
                headerOverride,
                validOnly,
                computeMissingHashes,
                datCatalog,
                progress,
                cancellationToken).ConfigureAwait(false)
            : await ExportSoftwareListDatsAsync(items, outputPath, format, datCatalog, validOnly, computeMissingHashes, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// DAT of the collection of MAME software lists, written into the folder <paramref name="outputFolder"/>: a DAT per
    /// list that holds a software of <paramref name="items"/>, described with the catalog of its list.
    /// </summary>
    private async Task<DatCatalog> ExportSoftwareListDatsAsync(
        IEnumerable<ProcessItem> items,
        string outputFolder,
        DatExportFormat format,
        DatCatalog datCatalog,
        bool validOnly,
        bool computeMissingHashes,
        IProgress<(int Current, int Total, string Filename)>? progress,
        CancellationToken cancellationToken)
    {
        // A single MAME software list: its DAT
        if (datCatalog.SoftwareLists is null)
        {
            var single = NamedAfterList(await _datService.CreateDatFromProcessItemsAsync(
                items, null, validOnly, computeMissingHashes, datCatalog, progress, cancellationToken).ConfigureAwait(false), datCatalog);
            await WriteDatAsync(single, outputFolder, format, cancellationToken, datCatalog).ConfigureAwait(false);
            return single;
        }

        var itemsByList = items
            .Where(it => it.MameSet?.SoftwareList is not null)
            .GroupBy(it => it.MameSet!.SoftwareList!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var dats = new List<DatCatalog>();
        foreach (var list in datCatalog.SoftwareLists!.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!itemsByList.TryGetValue(list.Header.Name, out var listItems))
            {
                continue;
            }

            var dat = await _datService.CreateDatFromProcessItemsAsync(
                listItems, null, validOnly, computeMissingHashes, list, progress, cancellationToken).ConfigureAwait(false);
            if (dat.Games.Count > 0)
            {
                dats.Add(NamedAfterList(dat, list));
            }
        }

        var gathered = DatCatalog.GatherSoftwareLists(dats, datCatalog.Header.Name);
        await WriteDatAsync(gathered, outputFolder, format, cancellationToken, datCatalog).ConfigureAwait(false);
        return gathered;
    }

    /// <summary>
    /// DAT of the collection sorted with a lot, written into the folder <paramref name="outputFolder"/>: a DAT per DAT of
    /// the lot that holds a file of <paramref name="items"/>, described with its catalog, and, without
    /// <paramref name="validOnly"/>, a DAT of the files no DAT knows.
    /// </summary>
    private async Task<DatCatalog> ExportLotDatsAsync(
        IEnumerable<ProcessItem> items,
        string outputFolder,
        DatExportFormat format,
        DatCatalog lot,
        bool validOnly,
        bool computeMissingHashes,
        IProgress<(int Current, int Total, string Filename)>? progress,
        CancellationToken cancellationToken)
    {
        RefuseLotFolder(outputFolder, lot);
        var groups = GroupByDat(items.Where(it => !validOnly || it.Status == FileStatus.Valid));

        // The progress counts the files of every DAT
        var total = groups.Sum(g => g.Items.Count);
        var done = 0;
        var dats = new List<DatLotMember>();
        foreach (var (datItems, datName) in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var member = lot.DatMemberFor(datName);
            var offset = done;
            var datProgress = progress is null
                ? null
                : new SynchronousProgress<(int Current, int Total, string Filename)>(p => progress.Report((offset + p.Current, total, p.Filename)));
            var dat = await _datService.CreateDatFromProcessItemsAsync(
                datItems, null, validOnly, computeMissingHashes, member?.Catalog, datProgress, cancellationToken).ConfigureAwait(false);
            done += datItems.Count;
            if (dat.Games.Count > 0)
            {
                dats.Add(new DatLotMember(member?.Name ?? dat.Header.Name, member?.RelativePath ?? string.Empty, dat));
            }
        }

        var gathered = DatCatalog.GatherLot(dats, lot.Header.Name);
        await WriteDatAsync(gathered, outputFolder, format, cancellationToken, lot).ConfigureAwait(false);
        return gathered;
    }

    // Reports a progress at once, on the thread of the export
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    public Task<DatCatalog> ExportDatToXmlAsync(
        IEnumerable<ProcessItem> items,
        string outputPath,
        DatCatalog? datCatalog = null,
        DatHeader? headerOverride = null,
        bool validOnly = true,
        bool computeMissingHashes = true,
        IProgress<(int Current, int Total, string Filename)>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExportDatAsync(items, outputPath, DatExportFormat.LogiqxXml, datCatalog, headerOverride, validOnly, computeMissingHashes, progress, cancellationToken);

    public Task<DatCatalog> ExportDatToClrMameProAsync(
        IEnumerable<ProcessItem> items,
        string outputPath,
        DatCatalog? datCatalog = null,
        DatHeader? headerOverride = null,
        bool validOnly = true,
        bool computeMissingHashes = true,
        IProgress<(int Current, int Total, string Filename)>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExportDatAsync(items, outputPath, DatExportFormat.ClrMamePro, datCatalog, headerOverride, validOnly, computeMissingHashes, progress, cancellationToken);

    private static string EscapeCsv(string val)
    {
        if (val.Contains(';') || val.Contains('"') || val.Contains('\n') || val.Contains('\r'))
        {
            return $"\"{val.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }
        return val;
    }
}
