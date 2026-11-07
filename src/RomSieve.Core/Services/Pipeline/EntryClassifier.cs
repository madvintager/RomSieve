using System.Diagnostics;
using System.IO;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Individual file identification against DAT catalog, parsing, and initial filtering.
/// </summary>
public sealed class EntryClassifier
{
    private readonly SortConfig _config;
    private readonly DatCatalog? _datCatalog;

    /// <summary>
    /// Convention of the DAT, detected once per run: a DAT whose convention stays unknown would otherwise be analysed
    /// again (all its games) for every file classified.
    /// </summary>
    private NamingConvention? _datConvention;
    private readonly ICrcCacheService _crcCache;
    private readonly IChecksumService _checksumService;
    private readonly INoIntroRedumpParser _romParser;
    private readonly IRomFilterService _filterService;
    private readonly IOpticalCompanionService _opticalService;
    private readonly ILocalizationService _i18n;

    /// <summary>
    /// DAT entries matched by at least one scanned file, compared by reference: a file matching
    /// several identical entries (the same ROM in several games) marks all of them as present.
    /// </summary>
    public HashSet<DatRomEntry> MatchedDatEntries { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<string, HashSet<string>> GameMatchedRoms { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// DAT entries of the games designated by the file name of a CHD (<see cref="SortConfig.MatchChdByName"/>):
    /// not verified by a hash, so neither matched nor missing.
    /// </summary>
    public HashSet<DatRomEntry> NameMatchedDatEntries { get; } = new(ReferenceEqualityComparer.Instance);

    public EntryClassifier(
        SortConfig config,
        DatCatalog? datCatalog,
        ICrcCacheService crcCache,
        IChecksumService checksumService,
        INoIntroRedumpParser romParser,
        IRomFilterService filterService,
        ILocalizationService i18n,
        IOpticalCompanionService opticalService)
    {
        _config = config;
        _datCatalog = datCatalog;
        _crcCache = crcCache;
        _checksumService = checksumService;
        _romParser = romParser;
        _filterService = filterService;
        _i18n = i18n;
        _opticalService = opticalService;
        _archiveChecker = new ArchiveContentChecker(checksumService, crcCache.Headers);
    }

    private readonly ArchiveContentChecker _archiveChecker;

    public async Task<List<ProcessItem>> ClassifyAllAsync(
        IReadOnlyList<ScanEntry> scanEntries,
        CancellationToken cancellationToken = default,
        IProgress<SortProgress>? progress = null,
        long totalBytes = 0)
    {
        var totalFiles = scanEntries.Count;
        if (totalBytes <= 0 && totalFiles > 0)
        {
            totalBytes = scanEntries.Sum(e => e.FileSize + e.RelatedEntries.Sum(r => r.FileSize));
        }

        var degree = ResolveHashingParallelism(_config.HashingParallelism);
        var items = _datCatalog is not null && degree > 1
            ? await ClassifyWithParallelHashingAsync(scanEntries, degree, totalBytes, cancellationToken, progress).ConfigureAwait(false)
            : await ClassifySequentiallyAsync(scanEntries, totalBytes, cancellationToken, progress).ConfigureAwait(false);

        if (_datCatalog is not null)
        {
            GroupUnlinkedDatCompanionItems(items);
        }

        progress?.Report(new SortProgress(0, 0, 0, 0, 0.0, LocalizedMessage.Create("progress.saving_cache")));
        await _crcCache.SaveAsync(cancellationToken).ConfigureAwait(false);
        return items;
    }

    /// <summary>Highest number of files hashed at the same time.</summary>
    public const int MaxHashingParallelism = 8;

    /// <summary>
    /// Number of files hashed at the same time: <paramref name="configured"/> between 1 and
    /// <see cref="MaxHashingParallelism"/>, or, for <see cref="SortConfig.AutoHashingParallelism"/> (0),
    /// one per processor up to 4.
    /// </summary>
    public static int ResolveHashingParallelism(int configured) =>
        configured == SortConfig.AutoHashingParallelism
            ? Math.Clamp(Environment.ProcessorCount, 1, 4)
            : Math.Clamp(configured, 1, MaxHashingParallelism);

    /// <summary>
    /// CRC32 of a scanned file (<see langword="null"/> without a DAT, empty when it was not computed)
    /// and size of its copier header, computed before its classification.
    /// </summary>
    internal readonly record struct Fingerprint(
        string? Crc, int HeaderOffset, string? Sha1 = null, ChdInfo? Chd = null, string? ChdError = null, LocalizedMessage? ArchiveDamage = null)
    {
        /// <summary>Size of the file compared with the DAT: without its copier header.</summary>
        public long EffectiveSize(ScanEntry entry) => HeaderOffset > 0 ? entry.FileSize - HeaderOffset : entry.FileSize;
    }

    /// <summary>Fingerprints of a scanned entry and of its companion files.</summary>
    internal sealed record EntryFingerprints(Fingerprint Main, Fingerprint[] Related);

    private async Task<List<ProcessItem>> ClassifySequentiallyAsync(
        IReadOnlyList<ScanEntry> scanEntries,
        long totalBytes,
        CancellationToken cancellationToken,
        IProgress<SortProgress>? progress)
    {
        var totalFiles = scanEntries.Count;
        var items = new List<ProcessItem>(totalFiles);
        var sw = Stopwatch.StartNew();
        var classifiedCount = 0;
        var classifiedBytes = 0L;

        foreach (var entry in scanEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.FileSize > 10 * 1024 * 1024)
            {
                ReportProgress(progress, sw, classifiedCount, totalFiles, classifiedBytes, totalBytes, entry.DisplayName);
            }

            var item = await ClassifySingleAsync(entry, cancellationToken).ConfigureAwait(false);

            if (entry.RelatedEntries.Count > 0)
            {
                foreach (var relEntry in entry.RelatedEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relItem = await ClassifySingleAsync(relEntry, cancellationToken).ConfigureAwait(false);
                    AddRelatedItem(item, relItem);
                }
            }

            items.Add(item);

            classifiedCount++;
            classifiedBytes += entry.FileSize + entry.RelatedEntries.Sum(r => r.FileSize);
            ReportProgress(progress, sw, classifiedCount, totalFiles, classifiedBytes, totalBytes, entry.DisplayName);
        }

        return items;
    }

    /// <summary>
    /// Computes the fingerprints of up to <paramref name="degree"/> entries at the same time, then classifies
    /// the entries one by one in the scan order: only the reads run in parallel, so the items, their order and
    /// the matched DAT entries are the same as with a sequential classification.
    /// </summary>
    private async Task<List<ProcessItem>> ClassifyWithParallelHashingAsync(
        IReadOnlyList<ScanEntry> scanEntries,
        int degree,
        long totalBytes,
        CancellationToken cancellationToken,
        IProgress<SortProgress>? progress)
    {
        var fingerprints = await ComputeFingerprintsAsync(scanEntries, degree, totalBytes, cancellationToken, progress).ConfigureAwait(false);
        var items = new List<ProcessItem>(scanEntries.Count);
        for (var index = 0; index < scanEntries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = scanEntries[index];
            var item = Classify(entry, fingerprints[index].Main);
            for (var i = 0; i < entry.RelatedEntries.Count; i++)
            {
                AddRelatedItem(item, Classify(entry.RelatedEntries[i], fingerprints[index].Related[i]));
            }
            items.Add(item);
        }

        return items;
    }

    /// <summary>
    /// Fingerprints of every entry and of its companion files, computed for up to <paramref name="degree"/> entries at
    /// the same time (one after the other for 1), in the order of <paramref name="scanEntries"/>. Only reads: nothing of
    /// the classification depends on the order in which they end.
    /// </summary>
    internal async Task<EntryFingerprints[]> ComputeFingerprintsAsync(
        IReadOnlyList<ScanEntry> scanEntries,
        int degree,
        long totalBytes,
        CancellationToken cancellationToken,
        IProgress<SortProgress>? progress)
    {
        var totalFiles = scanEntries.Count;
        var fingerprints = new EntryFingerprints[totalFiles];
        var sw = Stopwatch.StartNew();
        var progressLock = new Lock();
        var hashedCount = 0;
        var hashedBytes = 0L;

        var options = new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(Enumerable.Range(0, totalFiles), options, async (index, token) =>
        {
            var entry = scanEntries[index];
            if (entry.FileSize > 10 * 1024 * 1024)
            {
                lock (progressLock)
                {
                    ReportProgress(progress, sw, hashedCount, totalFiles, hashedBytes, totalBytes, entry.DisplayName);
                }
            }

            var main = await ComputeFingerprintAsync(entry, token).ConfigureAwait(false);
            var related = new Fingerprint[entry.RelatedEntries.Count];
            for (var i = 0; i < related.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                related[i] = await ComputeFingerprintAsync(entry.RelatedEntries[i], token).ConfigureAwait(false);
            }
            fingerprints[index] = new EntryFingerprints(main, related);

            // Counters and reports are serialized so that the reported progress never goes backwards
            lock (progressLock)
            {
                hashedCount++;
                hashedBytes += entry.FileSize + entry.RelatedEntries.Sum(r => r.FileSize);
                ReportProgress(progress, sw, hashedCount, totalFiles, hashedBytes, totalBytes, entry.DisplayName);
            }
        }).ConfigureAwait(false);

        return fingerprints;
    }

    /// <summary>Adds a companion file to the item of its set: a damaged track damages the set, a kept set keeps its tracks.</summary>
    internal static void AddRelatedItem(ProcessItem item, ProcessItem relItem)
    {
        // A track damaged in its archive makes its whole set damaged
        if (ArchiveContentChecker.IsDamage(relItem.StatusReason) && !ArchiveContentChecker.IsDamage(item.StatusReason))
        {
            item.Status = FileStatus.Unmatched;
            item.StatusReason = relItem.StatusReason;
        }
        else if (item.Status == FileStatus.Valid)
        {
            relItem.Status = FileStatus.Valid;
            relItem.StatusReason = null;
        }
        item.RelatedItems.Add(relItem);
    }

    private void ReportProgress(
        IProgress<SortProgress>? progress,
        Stopwatch sw,
        int processedCount,
        int totalFiles,
        long processedBytes,
        long totalBytes,
        string displayName)
    {
        if (progress is null)
        {
            return;
        }

        var etaSeconds = EtaEstimator.EstimateSeconds(sw.Elapsed.TotalSeconds, processedCount, totalFiles, processedBytes, totalBytes);
        progress.Report(new SortProgress(
            ProcessedCount: processedCount,
            TotalCount: totalFiles,
            ProcessedBytes: processedBytes,
            TotalBytes: totalBytes,
            EtaSeconds: etaSeconds,
            Message: LocalizedMessage.Create("progress.analyzing_file", ("name", displayName))));
    }

    /// <summary>
    /// Gathers the files of a game of several ROMs that the scan did not link (no descriptor naming them) under one item:
    /// the descriptor when the game has one, otherwise its first file. The files gathered leave <paramref name="items"/>.
    /// </summary>
    internal void GroupUnlinkedDatCompanionItems(List<ProcessItem> items)
    {
        if (_datCatalog is null)
        {
            return;
        }

        var gameGroups = new Dictionary<string, List<ProcessItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in items)
        {
            if (string.IsNullOrEmpty(it.TargetFilename))
            {
                continue;
            }

            var game = _datCatalog.GetGameForRom(it.TargetFilename, it.DatGameName);
            if (game is not null && game.Roms.Count > 1)
            {
                if (!gameGroups.TryGetValue(game.Name, out var gList))
                {
                    gList = [];
                    gameGroups[game.Name] = gList;
                }
                gList.Add(it);
            }
        }

        var itemsToRemove = new HashSet<ProcessItem>(ReferenceEqualityComparer.Instance);
        foreach (var (_, gItems) in gameGroups)
        {
            if (gItems.Count <= 1)
            {
                continue;
            }

            var primaryItem = gItems.FirstOrDefault(it =>
            {
                var ext = Path.GetExtension(it.DisplayName);
                return _opticalService.IsDescriptor(ext);
            }) ?? gItems[0];

            foreach (var other in gItems)
            {
                if (ReferenceEquals(other, primaryItem))
                {
                    continue;
                }

                if (!primaryItem.RelatedItems.Any(r => string.Equals(r.SourcePath, other.SourcePath, StringComparison.OrdinalIgnoreCase)))
                {
                    if (primaryItem.Status == FileStatus.Valid)
                    {
                        other.Status = FileStatus.Valid;
                        other.StatusReason = null;
                    }
                    primaryItem.RelatedItems.Add(other);
                }
                itemsToRemove.Add(other);
            }
        }

        if (itemsToRemove.Count > 0)
        {
            items.RemoveAll(it => itemsToRemove.Contains(it));
        }
    }

    public async Task<ProcessItem> ClassifySingleAsync(
        ScanEntry entry,
        CancellationToken cancellationToken = default) =>
        Classify(entry, await ComputeFingerprintAsync(entry, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Reads what the classification of an entry needs from the disk: its CRC32 when a DAT is loaded and its size
    /// (or its size without copier header) exists in the DAT, from the CRC cache when possible. Safe to call
    /// for several entries in parallel.
    /// </summary>
    private async Task<Fingerprint> ComputeFingerprintAsync(ScanEntry entry, CancellationToken cancellationToken)
    {
        // A damaged member is never identified: its CRC32 is not taken from the archive
        if (_config.VerifyArchiveContents &&
            !string.IsNullOrEmpty(entry.ArchivePath) &&
            !string.IsNullOrEmpty(entry.ArchiveMember) &&
            _archiveChecker.FindMemberDamage(entry.ArchivePath, entry.ArchiveMember, cancellationToken) is { } damage)
        {
            return new Fingerprint(_datCatalog is null ? null : string.Empty, 0, ArchiveDamage: damage);
        }

        var (chd, chdError) = ReadChdIfAny(entry);
        if (_datCatalog is null)
        {
            return new Fingerprint(null, 0, Chd: chd, ChdError: chdError);
        }

        var sourcePath = entry.SourcePath;
        var fileSize = entry.FileSize;
        var fileCrc = entry.PrecomputedCrc;
        var headerOffset = 0;

        if (fileCrc is null)
        {
            var isCandidate = _datCatalog.KnownSizes.Contains(fileSize);
            if (!isCandidate && _config.Filters.DetectCopierHeaders && fileSize > 16)
            {
                try
                {
                    var hBytes = new byte[64];
                    await using var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var read = await fs.ReadAsync(hBytes.AsMemory(0, 64), cancellationToken).ConfigureAwait(false);
                    headerOffset = _checksumService.DetectCopierHeader(hBytes.AsSpan(0, read), fileSize);
                    if (headerOffset > 0 && _datCatalog.KnownSizes.Contains(fileSize - headerOffset))
                    {
                        isCandidate = true;
                    }
                }
                catch (IOException)
                {
                }
            }

            if (isCandidate)
            {
                if (headerOffset == 0)
                {
                    fileCrc = _crcCache.Get(sourcePath);
                    if (fileCrc is null)
                    {
                        try
                        {
                            fileCrc = await _checksumService.ComputeFileCrc32Async(sourcePath, cancellationToken: cancellationToken).ConfigureAwait(false);
                            _crcCache.Set(sourcePath, fileCrc);
                        }
                        catch (IOException)
                        {
                            fileCrc = string.Empty;
                        }
                    }
                }
                else
                {
                    try
                    {
                        fileCrc = await _checksumService.ComputeFileCrc32Async(sourcePath, offset: headerOffset, cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        fileCrc = string.Empty;
                    }
                }
            }
            else
            {
                fileCrc = string.Empty;
            }
        }

        var sha1 = await ComputeSha1IfVerifiedAsync(entry, fileCrc, headerOffset, cancellationToken).ConfigureAwait(false);
        return new Fingerprint(fileCrc, headerOffset, sha1, chd, chdError);
    }

    /// <summary>
    /// Header and tracks of a CHD file outside an archive (124 bytes and the metadata, never the data),
    /// or the reason why it is not a readable CHD.
    /// </summary>
    private (ChdInfo? Chd, string? Error) ReadChdIfAny(ScanEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.ArchiveMember)
            || !string.Equals(Path.GetExtension(entry.SourcePath), ".chd", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        try
        {
            return (_crcCache.Headers.ReadChdInfo(entry.SourcePath, _checksumService), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// SHA-1 of an entry, only when <see cref="SortConfig.VerifySha1"/> is enabled and the entry matches by CRC32
    /// a DAT entry that provides a SHA-1. <see langword="null"/> when it is not needed or cannot be read
    /// (the CRC32 match then stands).
    /// </summary>
    private async Task<string?> ComputeSha1IfVerifiedAsync(ScanEntry entry, string? fileCrc, int headerOffset, CancellationToken cancellationToken)
    {
        if (!_config.VerifySha1 || _datCatalog is null || string.IsNullOrEmpty(fileCrc))
        {
            return null;
        }

        // A lot answers from its index, without reading a DAT
        var effectiveSize = headerOffset > 0 ? entry.FileSize - headerOffset : entry.FileSize;
        if (!_datCatalog.MayHaveSha1(effectiveSize, fileCrc))
        {
            return null;
        }

        try
        {
            if (!string.IsNullOrEmpty(entry.ArchivePath) && !string.IsNullOrEmpty(entry.ArchiveMember))
            {
                return await _checksumService.ComputeArchiveMemberSha1Async(entry.ArchivePath, entry.ArchiveMember, cancellationToken).ConfigureAwait(false);
            }

            if (headerOffset > 0)
            {
                return await _checksumService.ComputeFileSha1Async(entry.SourcePath, headerOffset, cancellationToken).ConfigureAwait(false);
            }

            var sha1 = _crcCache.GetSha1(entry.SourcePath);
            if (sha1 is null)
            {
                sha1 = await _checksumService.ComputeFileSha1Async(entry.SourcePath, cancellationToken: cancellationToken).ConfigureAwait(false);
                _crcCache.SetSha1(entry.SourcePath, fileCrc, sha1);
            }

            return sha1;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Identifies an entry against the DAT from its <paramref name="fingerprint"/>, parses its name and applies
    /// the initial filters. Runs in the scan order: it updates <see cref="MatchedDatEntries"/> and
    /// <see cref="GameMatchedRoms"/>.
    /// </summary>
    internal ProcessItem Classify(ScanEntry entry, Fingerprint fingerprint)
    {
        var item = ClassifyCore(entry, fingerprint);
        item.Chd = fingerprint.Chd;

        // A file damaged in its archive is isolated, whatever its name
        if (fingerprint.ArchiveDamage is { } damage)
        {
            item.Status = FileStatus.Unmatched;
            item.StatusReason = damage;
        }

        return item;
    }

    private ProcessItem ClassifyCore(ScanEntry entry, Fingerprint fingerprint)
    {
        var sourcePath = entry.SourcePath;
        var fileSize = entry.FileSize;
        var archPath = entry.ArchivePath;
        var archMember = entry.ArchiveMember;
        var displayName = entry.DisplayName;

        var convention = ResolveConvention();

        // A damaged or incomplete CHD is isolated, whatever its name
        if (fingerprint.ChdError is { } chdError)
        {
            return new ProcessItem
            {
                SourcePath = sourcePath,
                FileSize = fileSize,
                Parsed = _romParser.ParseRomFilename(displayName, sourcePath, fileSize, convention),
                Status = FileStatus.Unmatched,
                StatusReason = LocalizedMessage.Create("reason.chd_invalid", ("error", LocalizedMessage.Verbatim(chdError)))
            };
        }

        if (_datCatalog is not null)
        {
            var fileCrc = fingerprint.Crc;
            var effectiveSize = fingerprint.EffectiveSize(entry);
            var matches = FindMatches(entry, fingerprint, out var sha1Mismatch);
            var match = _datCatalog.SelectMatch(matches, Path.GetFileName(displayName));

            if (match is not null)
            {
                // Every matching entry is present, but the file is identified through a single deterministic entry
                foreach (var matchedEntry in matches)
                {
                    MatchedDatEntries.Add(matchedEntry);
                    var matchedGame = _datCatalog.GetGameForEntry(matchedEntry);
                    if (matchedGame is not null)
                    {
                        if (!GameMatchedRoms.TryGetValue(matchedGame.Name, out var romSet))
                        {
                            romSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            GameMatchedRoms[matchedGame.Name] = romSet;
                        }
                        romSet.Add(matchedEntry.Name);
                    }
                }

                // A disk is named without extension in the DAT; a MAME short name is not a parsable title,
                // so the file name is parsed instead
                var targetName = match.IsDisk && !Path.HasExtension(match.Name) ? match.Name + ".chd" : match.Name;
                var parsed = _romParser.ParseRomFilename(targetName, sourcePath, effectiveSize, convention);
                if (match.IsDisk && !parsed.IsConform)
                {
                    parsed = _romParser.ParseRomFilename(displayName, sourcePath, fileSize, convention);
                }

                var item = BuildIdentifiedItem(entry, parsed, _datCatalog.GetGameForEntry(match), targetName);
                item.Crc32 = !string.IsNullOrEmpty(match.Crc) ? match.Crc : fileCrc ?? string.Empty;
                item.Md5 = match.Md5;
                item.Sha1 = match.Sha1;
                return item;
            }

            // A CHD cannot be compared with the tracks of a Redump DAT: optionally, its file name designates the game,
            // and the match is reported as not verified (its entries are counted apart from the matched ones)
            if (_config.MatchChdByName
                && fingerprint.Chd is not null
                && _datCatalog.GameByName.TryGetValue(Path.GetFileNameWithoutExtension(displayName), out var namedGame))
            {
                NameMatchedDatEntries.UnionWith(namedGame.Roms);
                var parsed = _romParser.ParseRomFilename(namedGame.Name + ".chd", sourcePath, fileSize, convention);
                var item = BuildIdentifiedItem(entry, parsed, namedGame, displayName);
                item.IsIdentifiedByName = true;
                return item;
            }

            var parsedUnmatched = _romParser.ParseRomFilename(displayName, sourcePath, fileSize, convention);
            return new ProcessItem
            {
                SourcePath = sourcePath,
                FileSize = fileSize,
                Parsed = parsedUnmatched,
                Crc32 = fileCrc ?? string.Empty,
                Sha1 = fingerprint.Sha1,
                ArchivePath = archPath,
                ArchiveMember = archMember,
                Status = FileStatus.Unmatched,
                StatusReason = sha1Mismatch ?? LocalizedMessage.Create("reason.unmatched_not_in_dat")
            };
        }

        // DAT-less mode: direct filename analysis
        var directParsed = _romParser.ParseRomFilename(displayName, sourcePath, fileSize, convention);
        var directItem = new ProcessItem
        {
            SourcePath = sourcePath,
            FileSize = fileSize,
            Parsed = directParsed,
            ArchivePath = archPath,
            ArchiveMember = archMember
        };

        ApplyInitialFilters(directItem);
        return directItem;
    }

    /// <summary>
    /// Entries of the DAT the file matches: by size and CRC32, or, for a CHD, the disks of the SHA-1 of its header; with a
    /// verified SHA-1, only those without SHA-1 or with the same one, <paramref name="sha1Mismatch"/> telling why none is
    /// left when every entry had another SHA-1. Empty without a DAT.
    /// </summary>
    private IReadOnlyList<DatRomEntry> FindMatches(ScanEntry entry, Fingerprint fingerprint, out LocalizedMessage? sha1Mismatch)
    {
        sha1Mismatch = null;
        if (_datCatalog is null)
        {
            return [];
        }

        var matches = !string.IsNullOrEmpty(fingerprint.Crc)
            ? _datCatalog.FindAllMatches(fileSize: fingerprint.EffectiveSize(entry), crc: fingerprint.Crc)
            : [];

        // A CHD matches the disks of a MAME-style DAT by the SHA-1 of its header (data and metadata)
        if (matches.Count == 0 && fingerprint.Chd is { } chd)
        {
            matches = _datCatalog.FindAllMatches(0, sha1: chd.Sha1).Where(m => m.IsDisk).ToList();
        }

        // With a verified SHA-1, only the entries without SHA-1 or with the same SHA-1 still match
        if (fingerprint.Sha1 is { } fileSha1 && matches.Count > 0)
        {
            var verified = matches
                .Where(m => string.IsNullOrEmpty(m.Sha1) || string.Equals(m.Sha1, fileSha1, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (verified.Count == 0)
            {
                // Every entry left out has a SHA-1, otherwise it would have been kept
                sha1Mismatch = LocalizedMessage.Create("reason.sha1_mismatch",
                    ("expected", matches[0].Sha1!.ToLowerInvariant()),
                    ("actual", fileSha1));
            }

            matches = verified;
        }

        return matches;
    }

    /// <summary>
    /// Whether the file matches an entry of the DAT (<see cref="FindMatches"/>), as its classification would find it.
    /// </summary>
    internal bool Knows(ScanEntry entry, Fingerprint fingerprint) => FindMatches(entry, fingerprint, out _).Count > 0;

    /// <summary>
    /// Marks the entries of the DAT that a file sorted with another DAT of a lot matches as present
    /// (<see cref="MatchedDatEntries"/>): the ROM is in the source, whatever DAT the file is written with. The games of
    /// the DAT do not count it among their files (<see cref="GameMatchedRoms"/>): a set of the DAT is only complete with
    /// the files written with it.
    /// </summary>
    internal void MarkPresent(ScanEntry entry, Fingerprint fingerprint)
    {
        if (fingerprint.ArchiveDamage is null && fingerprint.ChdError is null)
        {
            MatchedDatEntries.UnionWith(FindMatches(entry, fingerprint, out _));
        }
    }

    /// <summary>Convention of the DAT, detected once per run; <see cref="NamingConvention.Auto"/> without a DAT.</summary>
    internal NamingConvention DatConvention => _datCatalog is null
        ? NamingConvention.Auto
        : _datConvention ??= _datCatalog.DetectConvention();

    /// <summary>Strict boundary: when a DAT is loaded, its detected convention governs ROM convention.</summary>
    private NamingConvention ResolveConvention()
    {
        var datConv = DatConvention;
        return datConv != NamingConvention.Auto ? datConv : _config.NamingConvention;
    }

    /// <summary>
    /// Items of the games of the DAT alone, without any file: those a complete collection named after the DAT would
    /// give, every ROM present. One item per game, its descriptor first when it has one, carrying its other ROMs as
    /// <see cref="ProcessItem.RelatedItems"/>, as the classification of a collection groups them. Each item is named,
    /// identified and filtered like the file it stands for (<see cref="ProcessItem.SourcePath"/> is the name of the ROM):
    /// the release selection then decides on the catalog what it would decide on that collection.
    /// <see cref="MatchedDatEntries"/> and <see cref="GameMatchedRoms"/> hold every entry, so every set is complete.
    /// </summary>
    public List<ProcessItem> ClassifyCatalog(CancellationToken cancellationToken = default)
    {
        if (_datCatalog is null)
        {
            return [];
        }

        var convention = ResolveConvention();
        var items = new List<ProcessItem>(_datCatalog.Games.Count);
        foreach (var game in _datCatalog.Games)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A ROM never dumped has no file in any collection
            var roms = game.Roms.Where(r => r.Status != DatRomStatus.NoDump).ToList();
            if (roms.Count == 0)
            {
                continue;
            }

            var primary = roms.FirstOrDefault(r => _opticalService.IsDescriptor(Path.GetExtension(r.Name))) ?? roms[0];
            var item = CreateCatalogItem(game, primary, convention);
            foreach (var rom in roms.Where(r => !ReferenceEquals(r, primary)))
            {
                AddRelatedItem(item, CreateCatalogItem(game, rom, convention));
            }

            if (!GameMatchedRoms.TryGetValue(game.Name, out var romSet))
            {
                romSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                GameMatchedRoms[game.Name] = romSet;
            }

            foreach (var rom in roms)
            {
                MatchedDatEntries.Add(rom);
                romSet.Add(rom.Name);
            }

            items.Add(item);
        }

        return items;
    }

    // Item of a ROM of the DAT, as the file named after it would be identified with its game
    private ProcessItem CreateCatalogItem(DatGame game, DatRomEntry rom, NamingConvention convention)
    {
        var targetName = rom.IsDisk && !Path.HasExtension(rom.Name) ? rom.Name + ".chd" : rom.Name;
        var parsed = _romParser.ParseRomFilename(targetName, targetName, rom.Size, convention);
        var item = BuildIdentifiedItem(new ScanEntry(targetName, rom.Size), parsed, game, targetName);
        item.Crc32 = rom.Crc;
        item.Md5 = rom.Md5;
        item.Sha1 = rom.Sha1;
        return item;
    }

    /// <summary>
    /// Item of a file identified with the DAT game <paramref name="game"/>: the parse result is enriched with the
    /// parent / clone relation and the multi-disk status of the game (as a modified copy), then filtered.
    /// </summary>
    private ProcessItem BuildIdentifiedItem(ScanEntry entry, ParsedRomName parsed, DatGame? game, string targetFilename)
    {
        if (game is not null && _datCatalog is not null)
        {
            if (!string.IsNullOrEmpty(game.CloneOf))
            {
                parsed = parsed with { CloneOf = game.CloneOf };
            }

            var canonicalParent = _datCatalog.GetCanonicalGameName(game.Name);
            if (!string.IsNullOrEmpty(canonicalParent) &&
                !string.Equals(canonicalParent, game.Name, StringComparison.OrdinalIgnoreCase))
            {
                parsed = parsed with { ParentTitle = canonicalParent, CloneOf = canonicalParent };
            }
        }

        var isMulti = (game?.IsMultidisk == true) || parsed.IsMultiMedia;
        if (isMulti || (game is not null && game.Roms.Count > 1))
        {
            parsed = parsed with { IsMultidiskOverride = isMulti };
        }

        var item = new ProcessItem
        {
            SourcePath = entry.SourcePath,
            FileSize = entry.FileSize,
            Parsed = parsed,
            TargetFilename = targetFilename,
            DatGameName = game?.Name,
            ArchivePath = entry.ArchivePath,
            ArchiveMember = entry.ArchiveMember,
            IsMultidisk = isMulti
        };

        ApplyInitialFilters(item);
        return item;
    }

    /// <summary>Non-conforming name: unmatched; otherwise kept or excluded by the filters.</summary>
    private void ApplyInitialFilters(ProcessItem item)
    {
        if (!item.Parsed.IsConform)
        {
            item.Status = FileStatus.Unmatched;
            item.StatusReason = LocalizedMessage.Create("reason.unmatched_non_tosec");
            return;
        }

        // For the AGA Amigas, the AGA and OCS versions of a TOSEC game are one game for the 1G1R
        if (_config.Filters.AmigaTarget == AmigaTarget.Aga && item.Parsed.Convention == NamingConvention.Tosec)
        {
            item.Parsed = item.Parsed with { MergesAmigaChipsets = true };
        }

        var (isKept, reason) = _filterService.EvaluateFilter(item.Parsed, _config.Filters);
        if (!isKept)
        {
            item.Status = FileStatus.Excluded;
            item.StatusReason = reason;
        }
        else
        {
            item.Status = FileStatus.Valid;
        }
    }
}
