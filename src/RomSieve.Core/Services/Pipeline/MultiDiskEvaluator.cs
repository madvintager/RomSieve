using System.Globalization;
using System.Text.RegularExpressions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Evaluation of multi-disk set completeness and smart matching
/// of clean data disks (<c>smart_multidisk_grouping</c>).
/// </summary>
public sealed partial class MultiDiskEvaluator
{
    private readonly SortConfig _config;
    private readonly IRomFilterService _filterService;
    private readonly ILocalizationService _i18n;

    public MultiDiskEvaluator(SortConfig config, IRomFilterService filterService, ILocalizationService i18n)
    {
        _config = config;
        _filterService = filterService;
        _i18n = i18n;
    }

    [GeneratedRegex(@"\b(\d+)\s+of\s+(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex DiskPartOfTotalRegex();

    [GeneratedRegex(@"\b(\d+)\b", RegexOptions.CultureInvariant)]
    internal static partial Regex SingleNumberRegex();

    // Mastering codes of a Redump disc, "(1M)", "(2M, 3M)", "(2MB3)": they tell the pressings of one disc apart, not the
    // editions of a game
    [GeneratedRegex(@"^\(\d+M(?:B\d+)?(?:,\s*\d+M(?:B\d+)?)*\)$", RegexOptions.CultureInvariant)]
    private static partial Regex MasteringCodeRegex();

    private static bool IsNoIntroDisc(ProcessItem item) =>
        item.Parsed.Convention is NamingConvention.NoIntro or NamingConvention.Redump &&
        !item.Parsed.TrackNumber.HasValue &&
        ExtractDiskPartInfo(item.Parsed).PartNum is > 0;

    /// <summary>
    /// The release key of a No-Intro / Redump disc without its mastering codes: the dumps of one edition share it, so
    /// that a set can borrow from another pressing the disc it lacks (<c>Phantasm (Japan) (Disc 6) (4M)</c> for the set
    /// whose other disks have no code). The marks of an edition (<c>(Premium Box)</c>), the region, the revision or a
    /// development status keep their dumps apart.
    /// </summary>
    private string DumpKeyOf(ParsedRomName parsed)
    {
        var marks = _filterService.GetCleanedMoreInfo(parsed).Where(info => !MasteringCodeRegex().IsMatch(info)).ToList();
        return _filterService.SerializeCanonicalGameKey(parsed) + "\u001E" +
               _filterService.GetReleaseKey(parsed with { MoreInfo = marks, DiscLabel = null });
    }

    /// <summary>
    /// The disks that complete a No-Intro / Redump release from the other dumps of its edition (same
    /// <see cref="DumpKeyOf"/>). Only the releases that hold the most disks of their edition borrow, the expected number of
    /// disks is the highest disc number of the edition, and a release only borrows when that makes it complete. Among
    /// several dumps of the missing disc, the one that shares the most tags with the release wins, then the one with the
    /// fewest tags, then the first name: the same collection always gives the same set.
    /// </summary>
    private static List<(TRelease Release, int Total, List<ProcessItem> Borrowed)> FindDumpsToBorrow<TRelease>(
        IReadOnlyList<(TRelease Release, IReadOnlyCollection<ProcessItem> Items)> releases,
        IReadOnlyCollection<ProcessItem> dumps)
        where TRelease : notnull
    {
        var result = new List<(TRelease, int, List<ProcessItem>)>();
        static int PartOf(ProcessItem item) => ExtractDiskPartInfo(item.Parsed).PartNum ?? 0;

        var total = dumps.Concat(releases.SelectMany(r => r.Items))
            .Select(it => Math.Max(PartOf(it), ExtractDiskPartInfo(it.Parsed).PartTotal ?? 0))
            .DefaultIfEmpty(0)
            .Max();
        if (total < 2)
        {
            return result;
        }

        var partsOf = releases.ToDictionary(r => r.Release, r => r.Items.Select(PartOf).Where(p => p > 0).ToHashSet());
        var most = partsOf.Values.Max(parts => parts.Count);
        foreach (var (release, items) in releases)
        {
            var parts = partsOf[release];
            if (parts.Count != most || parts.Count >= total)
            {
                continue;
            }

            var tags = items.SelectMany(it => it.Parsed.MoreInfo).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var borrowed = new List<ProcessItem>();
            for (var part = 1; part <= total; part++)
            {
                if (parts.Contains(part))
                {
                    continue;
                }

                var donor = dumps
                    .Where(it => PartOf(it) == part && !items.Contains(it))
                    .OrderByDescending(it => it.Parsed.MoreInfo.Count(tags.Contains))
                    .ThenBy(it => it.Parsed.MoreInfo.Count)
                    .ThenBy(it => it.Parsed.RawFilename, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (donor is null)
                {
                    borrowed.Clear();
                    break;
                }
                borrowed.Add(donor);
            }

            if (borrowed.Count > 0)
            {
                result.Add((release, total, borrowed));
            }
        }
        return result;
    }

    /// <summary>
    /// Smart multi-disk grouping of No-Intro / Redump releases (<see cref="FindDumpsToBorrow"/>), before the selection:
    /// updates the parts and the expected total of each release that borrows, and records its borrowed disks.
    /// </summary>
    private void BorrowNoIntroDumps(
        IList<ProcessItem> itemsToProcess,
        IReadOnlyDictionary<ProcessItem, string> itemRelKey,
        Dictionary<string, HashSet<int>> relCounts,
        Dictionary<string, int> relTotals,
        Dictionary<string, IReadOnlyList<ProcessItem>> borrowedByRelease,
        HashSet<ProcessItem> borrowedDisks)
    {
        var dumpsByKey = itemsToProcess
            .Where(it => it.Status == FileStatus.Valid && IsNoIntroDisc(it))
            .GroupBy(it => DumpKeyOf(it.Parsed), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<ProcessItem>)g.ToList(), StringComparer.Ordinal);

        var releasesByKey = itemRelKey
            .Where(pair => IsNoIntroDisc(pair.Key))
            .GroupBy(pair => pair.Value, StringComparer.Ordinal)
            .Select(g => (Release: g.Key, Items: (IReadOnlyCollection<ProcessItem>)g.Select(pair => pair.Key).ToList()))
            .GroupBy(r => DumpKeyOf(r.Items.First().Parsed), StringComparer.Ordinal);

        foreach (var group in releasesByKey)
        {
            if (!dumpsByKey.TryGetValue(group.Key, out var dumps))
            {
                continue;
            }

            foreach (var (rkey, total, borrowed) in FindDumpsToBorrow(group.ToList(), dumps))
            {
                foreach (var disk in borrowed)
                {
                    relCounts[rkey].Add(ExtractDiskPartInfo(disk.Parsed).PartNum ?? 0);
                    borrowedDisks.Add(disk);
                }
                relTotals[rkey] = total;
                borrowedByRelease[rkey] = borrowed;

                // Each disk of the set says it is one of the whole set ("6 of 8"), its borrowed disks too
                foreach (var disk in itemRelKey.Where(pair => pair.Value == rkey).Select(pair => pair.Key).Concat(borrowed))
                {
                    if (disk.Parsed.DiscTotal is { } discTotal && discTotal < total)
                    {
                        disk.Parsed = disk.Parsed with { DiscTotal = total };
                    }
                }
            }
        }
    }

    public static (int? PartNum, int? PartTotal) ExtractDiskPartInfo(ParsedRomName parsed, string defaultLabel = "")
    {
        var label = !string.IsNullOrEmpty(parsed.MediaLabel)
            ? parsed.MediaLabel
            : (!string.IsNullOrEmpty(parsed.MediaType) ? parsed.MediaType : defaultLabel);

        var m = DiskPartOfTotalRegex().Match(label);
        if (!m.Success && !string.IsNullOrEmpty(parsed.RawOptParens))
        {
            m = DiskPartOfTotalRegex().Match(parsed.RawOptParens);
        }

        if (m.Success &&
            int.TryParse(m.Groups[1].ValueSpan, out var pNum) &&
            int.TryParse(m.Groups[2].ValueSpan, out var pTot))
        {
            return (pNum, pTot);
        }

        if (parsed.DiscNumber.HasValue && parsed.DiscTotal.HasValue)
        {
            return (parsed.DiscNumber.Value, parsed.DiscTotal.Value);
        }

        return (null, null);
    }

    public static int ExtractDiskNumber(ParsedRomName parsed, string defaultLabel = "")
    {
        var (partNum, _) = ExtractDiskPartInfo(parsed, defaultLabel);
        if (partNum.HasValue)
        {
            return partNum.Value;
        }

        if (parsed.DiscNumber.HasValue)
        {
            return parsed.DiscNumber.Value;
        }

        var label = !string.IsNullOrEmpty(parsed.MediaLabel)
            ? parsed.MediaLabel
            : (!string.IsNullOrEmpty(parsed.MediaType) ? parsed.MediaType : defaultLabel);

        var mNum = SingleNumberRegex().Match(label);
        return mNum.Success && int.TryParse(mNum.Groups[1].ValueSpan, out var num) ? num : 0;
    }

    /// <summary>
    /// Détermine si un média TOSEC est une version originale saine et non altérée (aucun crack, trainer, hack, mod, bad dump, dev build...).
    /// </summary>
    public static bool IsCleanUnmodifiedDisk(ParsedRomName parsed) =>
        !(parsed.IsCrack ||
          parsed.IsTrainer ||
          parsed.IsHack ||
          parsed.IsPirate ||
          parsed.IsTranslation ||
          parsed.IsFix ||
          parsed.IsModified ||
          parsed.IsBadDump ||
          parsed.IsAlternate ||
          parsed.IsOverUnder ||
          parsed.IsVirus ||
          !string.IsNullOrEmpty(parsed.DevStatus));

    /// <summary>
    /// Recherche les disquettes saines empruntables pour compléter un set multi-disquettes incomplet.
    /// </summary>
    public static List<(int PartNum, ProcessItem Disk)> FindBorrowableCleanDisks(
        IReadOnlySet<int> existingParts,
        int totalExpected,
        IReadOnlyDictionary<int, ProcessItem> cleanDisks)
    {
        var borrowed = new List<(int PartNum, ProcessItem Disk)>();
        for (var p = 1; p <= totalExpected; p++)
        {
            if (!existingParts.Contains(p) && cleanDisks.TryGetValue(p, out var cleanDisk))
            {
                borrowed.Add((p, cleanDisk));
            }
        }
        return borrowed;
    }

    public MultiDiskEvaluationResult Evaluate(
        IList<ProcessItem> itemsToProcess,
        DatCatalog? datCatalog,
        IReadOnlyDictionary<string, HashSet<string>> gameMatchedRoms)
    {
        _config.Filters.NormalizeDefaults();

        if (datCatalog is not null)
        {
            foreach (var item in itemsToProcess)
            {
                if (item.Status == FileStatus.Valid && !string.IsNullOrEmpty(item.TargetFilename))
                {
                    var game = datCatalog.GetGameForRom(item.TargetFilename, item.DatGameName);
                    if (game is not null && game.IsMultidisk && game.Roms.Count > 1)
                    {
                        var foundCount = gameMatchedRoms.TryGetValue(game.Name, out var matchedSet) ? matchedSet.Count : 0;
                        var totalExpected = game.Roms.Count;
                        if (foundCount < totalExpected)
                        {
                            item.IsMultidiskComplete = false;
                            var isNoIntro = item.Parsed.Convention.UsesNoIntroSettings();
                            var mustExclude = isNoIntro
                                ? _config.Filters.NoIntroCompleteMultimediaSetsOnly == true
                                : _config.Filters.CompleteMultidiskSetsOnly;

                            if (mustExclude)
                            {
                                item.Status = FileStatus.Excluded;
                                item.StatusReason = LocalizedMessage.Create(
                                    "reason.incomplete_multidisk_set",
                                    ("game", game.Name),
                                    ("found", foundCount),
                                    ("total", totalExpected));
                            }
                        }
                        else
                        {
                            item.IsMultidiskComplete = true;
                        }
                    }
                }
            }
        }

        return EvaluateReleaseMultidisk(itemsToProcess);
    }

    private MultiDiskEvaluationResult EvaluateReleaseMultidisk(IList<ProcessItem> itemsToProcess)
    {
        // Infer DiscTotal for releases that use (Disc 1) / (Disk 1) without explicit 'of N'
        var unboundedDiscNums = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var unboundedItems = new List<(string RelKey, ProcessItem Item)>();

        foreach (var item in itemsToProcess)
        {
            if (item.Parsed.DiscNumber.HasValue && !item.Parsed.DiscTotal.HasValue)
            {
                var rkey = _filterService.GetReleaseKey(item.Parsed, smartMultidisk: false);
                if (!unboundedDiscNums.TryGetValue(rkey, out var set))
                {
                    set = [];
                    unboundedDiscNums[rkey] = set;
                }
                set.Add(item.Parsed.DiscNumber.Value);
                unboundedItems.Add((rkey, item));
            }
        }

        foreach (var (rkey, item) in unboundedItems)
        {
            if (unboundedDiscNums.TryGetValue(rkey, out var nums) && (nums.Count > 1 || nums.Max() > 1))
            {
                item.Parsed = item.Parsed with { DiscTotal = nums.Max() };
            }
        }

        var smartMultiCfg = _config.Filters.SmartMultidiskGrouping;
        var relCounts = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var relTotals = new Dictionary<string, int>(StringComparer.Ordinal);
        var relEdKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        var relIsTosec = new Dictionary<string, bool>(StringComparer.Ordinal);
        var itemRelKey = new Dictionary<ProcessItem, string>(ReferenceEqualityComparer.Instance);
        var cleanEditionDisks = new Dictionary<string, Dictionary<int, ProcessItem>>(StringComparer.Ordinal);

        foreach (var item in itemsToProcess)
        {
            if (item.Status == FileStatus.Valid)
            {
                var (partNum, partTot) = ExtractDiskPartInfo(item.Parsed);
                if (partNum.HasValue && partTot is > 1)
                {
                    var isTosecItem = item.Parsed.Convention == NamingConvention.Tosec;
                    var itemSmartMulti = smartMultiCfg && isTosecItem;
                    var rkey = _filterService.GetReleaseKey(item.Parsed, smartMultidisk: itemSmartMulti);
                    var edKey = _filterService.GetEditionKey(item.Parsed, smartMultidisk: itemSmartMulti);

                    if (!relCounts.TryGetValue(rkey, out var pSet))
                    {
                        pSet = [];
                        relCounts[rkey] = pSet;
                    }
                    pSet.Add(partNum.Value);
                    relTotals[rkey] = partTot.Value;
                    relEdKeys[rkey] = edKey;
                    relIsTosec[rkey] = isTosecItem;
                    itemRelKey[item] = rkey;

                    if (isTosecItem && IsCleanUnmodifiedDisk(item.Parsed))
                    {
                        if (!cleanEditionDisks.TryGetValue(edKey, out var diskMap))
                        {
                            diskMap = [];
                            cleanEditionDisks[edKey] = diskMap;
                        }
                        diskMap[partNum.Value] = item;
                    }
                }
            }
        }

        var cleanDisksParticipatingInCompleteSets = new HashSet<ProcessItem>(ReferenceEqualityComparer.Instance);
        var borrowedByRelease = new Dictionary<string, IReadOnlyList<ProcessItem>>(StringComparer.Ordinal);

        if (smartMultiCfg)
        {
            foreach (var (rkey, total) in relTotals)
            {
                if (!relIsTosec.GetValueOrDefault(rkey, false))
                {
                    continue;
                }

                var edKey = relEdKeys[rkey];
                if (cleanEditionDisks.TryGetValue(edKey, out var cleanDisks))
                {
                    var borrowed = FindBorrowableCleanDisks(relCounts[rkey], total, cleanDisks);
                    if (relCounts[rkey].Count + borrowed.Count >= total)
                    {
                        foreach (var (p, cleanIt) in borrowed)
                        {
                            relCounts[rkey].Add(p);
                            cleanDisksParticipatingInCompleteSets.Add(cleanIt);
                        }
                        if (borrowed.Count > 0)
                        {
                            borrowedByRelease[rkey] = borrowed.Select(b => b.Disk).ToList();
                        }
                    }
                }
            }

            BorrowNoIntroDumps(itemsToProcess, itemRelKey, relCounts, relTotals, borrowedByRelease, cleanDisksParticipatingInCompleteSets);
        }

        foreach (var item in itemsToProcess)
        {
            if (item.Status == FileStatus.Valid &&
                itemRelKey.TryGetValue(item, out var rkey) &&
                relTotals.TryGetValue(rkey, out var totalExpected))
            {
                var foundCount = relCounts.TryGetValue(rkey, out var pSet) ? pSet.Count : 0;
                if (foundCount < totalExpected && !cleanDisksParticipatingInCompleteSets.Contains(item))
                {
                    item.IsMultidiskComplete = false;
                    var isNoIntro = item.Parsed.Convention.UsesNoIntroSettings();
                    var mustExclude = isNoIntro
                        ? _config.Filters.NoIntroCompleteMultimediaSetsOnly == true
                        : _config.Filters.CompleteMultidiskSetsOnly;

                    if (mustExclude)
                    {
                        item.Status = FileStatus.Excluded;
                        item.StatusReason = LocalizedMessage.Create(
                            "reason.incomplete_multidisk_set",
                            ("game", item.Parsed.Title),
                            ("found", foundCount),
                            ("total", totalExpected));
                    }
                }
                else
                {
                    item.IsMultidiskComplete = true;
                }
            }
        }

        return new MultiDiskEvaluationResult(relTotals, borrowedByRelease);
    }

    /// <summary>
    /// Groups the transferred multi-disk items into the releases of their <c>.m3u</c> playlists. With smart
    /// grouping, a TOSEC release placed in the same folder borrows the clean data disks it lacks; a group made only
    /// of borrowed disks that remains incomplete is dropped. This runs on the final statuses and target paths.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<ProcessItem>> GroupPlaylistReleases(IReadOnlyList<ProcessItem> processedItems)
    {
        var validMediaItems = processedItems
            .Where(it => it.Status == FileStatus.Valid &&
                         it.IsMultiMedia &&
                         !string.IsNullOrEmpty(it.FinalTargetPath) &&
                         !it.Parsed.TrackNumber.HasValue)
            .ToList();

        if (validMediaItems.Count == 0)
        {
            return [];
        }

        var smartMultiCfg = _config.Filters.SmartMultidiskGrouping;
        var releaseGroups = new Dictionary<(string GameKey, string RelKey, string TargetParent), List<ProcessItem>>();
        var groupEditionKeys = new Dictionary<(string GameKey, string RelKey, string TargetParent), (string EdKey, bool IsTosec)>();
        var cleanEditionDisks = new Dictionary<(string GameKey, string EdKey, string TargetParent), Dictionary<int, ProcessItem>>();

        foreach (var it in validMediaItems)
        {
            var isTosec = it.Parsed.Convention == NamingConvention.Tosec;
            var itemSmartMulti = smartMultiCfg && isTosec;
            var gKey = _filterService.SerializeCanonicalGameKey(it.Parsed);
            var rKey = _filterService.GetReleaseKey(it.Parsed, smartMultidisk: itemSmartMulti);
            var edKey = _filterService.GetEditionKey(it.Parsed, smartMultidisk: itemSmartMulti);
            var targetParent = Path.GetDirectoryName(Path.GetFullPath(it.FinalTargetPath!)) ?? string.Empty;
            var groupKey = (gKey, rKey, targetParent);

            if (!releaseGroups.TryGetValue(groupKey, out var list))
            {
                list = [];
                releaseGroups[groupKey] = list;
                groupEditionKeys[groupKey] = (edKey, isTosec);
            }
            list.Add(it);

            if (itemSmartMulti)
            {
                var (partNum, partTot) = ExtractDiskPartInfo(it.Parsed);
                if (partNum.HasValue && partTot is > 1 && IsCleanUnmodifiedDisk(it.Parsed))
                {
                    var cleanKey = (gKey, edKey, targetParent);
                    if (!cleanEditionDisks.TryGetValue(cleanKey, out var diskMap))
                    {
                        diskMap = [];
                        cleanEditionDisks[cleanKey] = diskMap;
                    }
                    diskMap[partNum.Value] = it;
                }
            }
        }

        if (smartMultiCfg && cleanEditionDisks.Count > 0)
        {
            var borrowedCleanItems = new HashSet<ProcessItem>(ReferenceEqualityComparer.Instance);

            foreach (var (groupKey, rItems) in releaseGroups)
            {
                var (edKey, isTosec) = groupEditionKeys[groupKey];
                if (!isTosec)
                {
                    continue;
                }

                var cleanKey = (groupKey.GameKey, edKey, groupKey.TargetParent);
                if (!cleanEditionDisks.TryGetValue(cleanKey, out var cleanDisks))
                {
                    continue;
                }

                var existingParts = new HashSet<int>();
                var totalExpected = 0;
                foreach (var it in rItems)
                {
                    var (pNum, pTot) = ExtractDiskPartInfo(it.Parsed);
                    if (pNum.HasValue)
                    {
                        existingParts.Add(pNum.Value);
                    }
                    if (pTot is > 1 && pTot.Value > totalExpected)
                    {
                        totalExpected = pTot.Value;
                    }
                }

                if (totalExpected <= 1 || existingParts.Count >= totalExpected)
                {
                    continue;
                }

                var borrowed = FindBorrowableCleanDisks(existingParts, totalExpected, cleanDisks);
                if (existingParts.Count + borrowed.Count >= totalExpected ||
                    (!_config.Filters.CompleteMultidiskSetsOnly && borrowed.Count > 0))
                {
                    foreach (var (_, cleanDisk) in borrowed)
                    {
                        if (!rItems.Contains(cleanDisk))
                        {
                            rItems.Add(cleanDisk);
                        }
                        borrowedCleanItems.Add(cleanDisk);
                    }
                }
            }

            if (borrowedCleanItems.Count > 0)
            {
                var keysToRemove = new List<(string GameKey, string RelKey, string TargetParent)>();
                foreach (var (groupKey, rItems) in releaseGroups)
                {
                    var totalExpected = rItems
                        .Select(it => ExtractDiskPartInfo(it.Parsed).PartTotal ?? 0)
                        .DefaultIfEmpty(0)
                        .Max();
                    var distinctParts = rItems
                        .Select(it => ExtractDiskPartInfo(it.Parsed).PartNum)
                        .Where(p => p.HasValue)
                        .Select(p => p!.Value)
                        .Distinct()
                        .Count();

                    if (totalExpected > 1 &&
                        distinctParts < totalExpected &&
                        rItems.All(it => borrowedCleanItems.Contains(it)))
                    {
                        keysToRemove.Add(groupKey);
                    }
                }

                foreach (var k in keysToRemove)
                {
                    releaseGroups.Remove(k);
                }
            }
        }

        if (smartMultiCfg)
        {
            GroupBorrowedNoIntroDumps(releaseGroups);
        }

        return releaseGroups.Values.Select(group => (IReadOnlyList<ProcessItem>)group).ToList();
    }

    /// <summary>
    /// The playlist of a No-Intro / Redump set lists the disc it borrowed from another pressing of its edition placed in
    /// the same folder (<see cref="FindDumpsToBorrow"/>), and that disc has no playlist of its own.
    /// </summary>
    private void GroupBorrowedNoIntroDumps(Dictionary<(string GameKey, string RelKey, string TargetParent), List<ProcessItem>> releaseGroups)
    {
        var borrowedDisks = new HashSet<ProcessItem>(ReferenceEqualityComparer.Instance);
        var editions = releaseGroups
            .Where(pair => pair.Value.All(IsNoIntroDisc))
            .GroupBy(pair => (pair.Key.GameKey, DumpKey: DumpKeyOf(pair.Value[0].Parsed), pair.Key.TargetParent));

        foreach (var edition in editions)
        {
            var releases = edition.Select(pair => (Release: pair.Key, Items: (IReadOnlyCollection<ProcessItem>)pair.Value)).ToList();
            var dumps = edition.SelectMany(pair => pair.Value).ToList();
            foreach (var (release, _, borrowed) in FindDumpsToBorrow(releases, dumps))
            {
                releaseGroups[release].AddRange(borrowed);
                borrowedDisks.UnionWith(borrowed);
            }
        }

        foreach (var key in releaseGroups.Where(pair => pair.Value.All(borrowedDisks.Contains)).Select(pair => pair.Key).ToList())
        {
            releaseGroups.Remove(key);
        }
    }
}
