using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// 1G1R deduplication and multi-criteria tiebreaker engine.
/// </summary>
public sealed partial class OneGameOneRomResolver
{
    private readonly SortConfig _config;
    private readonly IRomFilterService _filterService;
    private readonly ILocalizationService _i18n;

    public OneGameOneRomResolver(SortConfig config, IRomFilterService filterService, ILocalizationService i18n)
    {
        _config = config;
        _filterService = filterService;
        _i18n = i18n;
        foreach (var convention in TiebreakOrder.Conventions)
        {
            var cascade = BuildCascade(config.Filters.GetTiebreakOrder(convention));
            _cascades[convention] = cascade;

            // The order of the criteria is the reference of the 1G1R: the country comes first when it precedes the language
            _isCountryFirstOrder[convention] = IsCountryFirst(cascade);
        }
    }

    // Cascade of the releases of each convention, and whether its order puts the country first
    private readonly Dictionary<NamingConvention, (TiebreakCriterion Criterion, Comparison<RankedRelease> Compare)[]> _cascades = [];
    private readonly Dictionary<NamingConvention, bool> _isCountryFirstOrder = [];

    private static bool IsCountryFirst((TiebreakCriterion Criterion, Comparison<RankedRelease> Compare)[] cascade) =>
        cascade.Select(c => c.Criterion).ToList() is var order
        && order.IndexOf(TiebreakCriterion.CountryRank) < order.IndexOf(TiebreakCriterion.LanguageRank);

    /// <summary>
    /// Cascade that compares two releases: that of their convention (<see cref="TiebreakOrder.OrderConventionOf"/>). Two
    /// releases of different conventions (a collection of mixed names sorted without a DAT) are compared by the cascade
    /// of TOSEC, so that the comparison of two releases does not depend on their order.
    /// </summary>
    private (TiebreakCriterion Criterion, Comparison<RankedRelease> Compare)[] CascadeOf(RankedRelease a, RankedRelease b)
    {
        var convention = TiebreakOrder.OrderConventionOf(a.Sample.Convention);
        return _cascades[convention == TiebreakOrder.OrderConventionOf(b.Sample.Convention) ? convention : NamingConvention.Tosec];
    }

    [GeneratedRegex(@"\((?:Intro|Game)\)|\[(?:bootable|non-bootable)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NeutralizedDiskRoleTagsRegex();

    public void Resolve(
        IList<ProcessItem> itemsToProcess,
        MultiDiskEvaluationResult multiDiskResult)
    {
        var groups = new Dictionary<(string Title, string System), List<ProcessItem>>();
        foreach (var item in itemsToProcess)
        {
            if (item.Status == FileStatus.Valid)
            {
                var key = _filterService.GetCanonicalGameKey(item.Parsed);
                if (!groups.TryGetValue(key, out var list))
                {
                    list = [];
                    groups[key] = list;
                }
                list.Add(item);
            }
        }

        foreach (var groupItems in groups.Values)
        {
            ResolveForGame(groupItems, multiDiskResult);
        }
    }

    private (int DiskNum, int NegCleanScore, int NegVerified, int NameLen, string Name) GetDiskSortKey(ProcessItem it)
    {
        var diskNum = MultiDiskEvaluator.ExtractDiskNumber(it.Parsed);
        var cScore = _filterService.GetReleaseCleanlinessScore(it.Parsed);
        var verified = it.Parsed.IsVerified ? 1 : 0;
        var name = !string.IsNullOrEmpty(it.ArchiveMember) ? it.ArchiveMember : Path.GetFileName(it.SourcePath);
        return (diskNum, -cScore, -verified, name.Length, name);
    }

    /// <summary>
    /// Builds the tiebreaker cascade for the current configuration. Each comparison is negative
    /// when its first release is the better one. Single source of truth for both the ranking
    /// (<see cref="CompareReleases"/>) and its explanation (<see cref="Explain1G1RTiebreak"/>).
    /// </summary>
    private (TiebreakCriterion Criterion, Comparison<RankedRelease> Compare)[] BuildCascade(IReadOnlyList<TiebreakCriterion> order)
    {
        var filters = _config.Filters;
        var prefVideo = filters.PreferredVideo;
        var prefMedia = filters.PreferredMediaType;

        int MediaMatch(RankedRelease r) =>
            r.Sample.Convention == NamingConvention.Tosec && prefMedia != MediaPreference.None && r.MediaCat == prefMedia.ToConfigString() ? 1 : 0;

        int LanguageCountScore(RankedRelease r) => filters.PreferMonolingual == r.IsMonolingual ? 1 : 0;

        // Only the AGA machines run both the AGA and the OCS versions of a game, which are then one game
        var isAgaTarget = filters.AmigaTarget == AmigaTarget.Aga;

        var comparisons = new Dictionary<TiebreakCriterion, Comparison<RankedRelease>>
        {
            [TiebreakCriterion.LanguageRank] = (a, b) => (a.Rank ?? UnrankedValue).CompareTo(b.Rank ?? UnrankedValue),

            [TiebreakCriterion.CompleteMultidiskSet] = (a, b) => b.IsCompleteMultidisk.CompareTo(a.IsCompleteMultidisk),
            [TiebreakCriterion.AmigaChipset] = (a, b) => isAgaTarget ? a.AmigaRank.CompareTo(b.AmigaRank) : 0,
            [TiebreakCriterion.GoodDump] = (a, b) => b.DumpRank.CompareTo(a.DumpRank),
            [TiebreakCriterion.OriginalRelease] = (a, b) => b.OriginalScore.CompareTo(a.OriginalScore),
            [TiebreakCriterion.DriverStatus] = (a, b) => a.DriverRank.CompareTo(b.DriverRank),
            [TiebreakCriterion.CountryRank] = (a, b) => (a.CountryRank ?? UnrankedValue).CompareTo(b.CountryRank ?? UnrankedValue),
            [TiebreakCriterion.VideoStandard] = (a, b) => GetVideoMatchScore(b.VideoStd, prefVideo).CompareTo(GetVideoMatchScore(a.VideoStd, prefVideo)),
            [TiebreakCriterion.PreferredMediaType] = (a, b) => MediaMatch(b).CompareTo(MediaMatch(a)),
            [TiebreakCriterion.MediaFidelity] = (a, b) => b.MediaFidelity.CompareTo(a.MediaFidelity),
            [TiebreakCriterion.LanguageCount] = (a, b) => LanguageCountScore(b).CompareTo(LanguageCountScore(a)),
            [TiebreakCriterion.RegionAffinity] = (a, b) => b.RegionAffinity.CompareTo(a.RegionAffinity),
            [TiebreakCriterion.Version] = (a, b) => CompareIntArraysDescending(a.VerTuple, b.VerTuple),
            [TiebreakCriterion.Revision] = (a, b) => CompareIntArraysDescending(a.RevTuple, b.RevTuple),
            [TiebreakCriterion.Date] = (a, b) => b.DateTuple.CompareTo(a.DateTuple),
            [TiebreakCriterion.Completeness] = (a, b) => b.Completeness.CompareTo(a.Completeness),
            [TiebreakCriterion.ParentClone] = (a, b) => b.IsParent.CompareTo(a.IsParent)
        };

        // The order chosen by the user (or its default order), then the final tiebreak on the name: the shortest, then
        // the first in the ordinal order
        return
        [
            .. order.Select(criterion => (criterion, comparisons[criterion])),
            (TiebreakCriterion.Name, (a, b) =>
                a.MinNameLen.CompareTo(b.MinNameLen) is var byLength and not 0 ? byLength : string.CompareOrdinal(a.FirstName, b.FirstName))
        ];
    }

    private const int UnrankedValue = 999999;

    /// <summary>
    /// Compares two releases along the tiebreaker cascade. <c>Result</c> is negative when <paramref name="a"/>
    /// is the better release; <c>DecidedBy</c> is the first criterion that separates them.
    /// </summary>
    private (int Result, TiebreakCriterion DecidedBy) CompareReleases(RankedRelease a, RankedRelease b)
    {
        foreach (var (criterion, compare) in CascadeOf(a, b))
        {
            var cmp = compare(a, b);
            if (cmp != 0)
            {
                return (cmp, criterion);
            }
        }
        return (0, TiebreakCriterion.None);
    }

    private int CompareReleaseOrder(RankedRelease a, RankedRelease b) => CompareReleases(a, b).Result;

    private static int GetVideoMatchScore(string? vidStd, VideoPreference prefVideo)
    {
        if (prefVideo == VideoPreference.None || string.IsNullOrEmpty(vidStd))
        {
            return 0;
        }

        return vidStd.Contains(prefVideo.ToConfigString(), StringComparison.OrdinalIgnoreCase) ? 1 : -1;
    }

    /// <summary>
    /// Rank of a MAME emulation status, the lowest first: good, unknown (any file other than a MAME machine, which
    /// therefore never wins nor loses by it), imperfect, preliminary.
    /// </summary>
    private static int GetDriverRank(MameDriverStatus status) => status switch
    {
        MameDriverStatus.Good => 0,
        MameDriverStatus.Imperfect => 2,
        MameDriverStatus.Preliminary => 3,
        _ => 1
    };

    private static LocalizedMessage DriverStatusMessage(RankedRelease release) =>
        (release.Sample.Machine?.DriverStatus ?? release.Sample.SoftwareSupport).ToMessage();

    /// <summary>
    /// Version of a release as the version criterion compared it: a release without one ranks below any number, and
    /// is said to have none (it was shown as "v1.0", which explained a defeat by "v1.0 > v1.0").
    /// </summary>
    private object DescribeVersion(RankedRelease release)
    {
        var version = _filterService.GetEffectiveVersion(release.Sample);
        if (string.IsNullOrEmpty(version))
        {
            version = release.Sample.Version;
        }

        return string.IsNullOrEmpty(version) ? LocalizedMessage.Create("desc.no_version") : version;
    }

    private static int CompareIntArraysDescending(int[] a, int[] b)
    {
        var maxLen = Math.Max(a.Length, b.Length);
        for (var i = 0; i < maxLen; i++)
        {
            var va = i < a.Length ? a[i] : 0;
            var vb = i < b.Length ? b[i] : 0;
            var cmp = vb.CompareTo(va);
            if (cmp != 0)
            {
                return cmp;
            }
        }
        return 0;
    }

    /// <summary>
    /// Whether a release is a parent: none of its files is the clone of a game outside the release. Some DATs that
    /// describe each disk as a game make the disks of a set the clones of one of them (No-Intro floppy DATs: disks 1
    /// to 7 clones of disk 8); such a set is the parent release, and not a clone because of its first disk.
    /// </summary>
    internal static bool IsParentRelease(IReadOnlyList<ProcessItem> relItems)
    {
        HashSet<string>? releaseGames = null;
        foreach (var it in relItems)
        {
            // The parent game by its name in the DAT (a MAME parent title is a description, never a game name)
            var parent = !string.IsNullOrEmpty(it.Parsed.CloneOf) ? it.Parsed.CloneOf : it.Parsed.ParentTitle;
            if (string.IsNullOrEmpty(parent))
            {
                continue;
            }

            releaseGames ??= relItems
                .Select(r => r.DatGameName)
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!releaseGames.Contains(parent))
            {
                return false;
            }
        }

        return true;
    }

    private void ResolveForGame(
        List<ProcessItem> groupItems,
        MultiDiskEvaluationResult multiDiskResult)
    {
        var isNoIntroGame = groupItems.Any(it => it.Parsed.Convention.UsesNoIntroSettings());
        var smartMulti = _config.Filters.SmartMultidiskGrouping && !isNoIntroGame;

        var releaseGroups = new Dictionary<string, List<ProcessItem>>(StringComparer.Ordinal);
        var releaseEditionKeys = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var gItem in groupItems)
        {
            var relKey = _filterService.GetReleaseKey(gItem.Parsed, smartMultidisk: smartMulti);
            var edKey = _filterService.GetEditionKey(gItem.Parsed, smartMultidisk: smartMulti);
            if (!releaseGroups.TryGetValue(relKey, out var rItems))
            {
                rItems = [];
                releaseGroups[relKey] = rItems;
                releaseEditionKeys[relKey] = edKey;
            }
            rItems.Add(gItem);
        }

        if (smartMulti)
        {
            // Clean data disks that the multi-disk evaluation found to complete a release compete with it
            foreach (var (rkey, rItems) in releaseGroups)
            {
                if (multiDiskResult.BorrowedCleanDisks.TryGetValue(rkey, out var borrowed))
                {
                    foreach (var cleanDisk in borrowed)
                    {
                        if (!rItems.Contains(cleanDisk))
                        {
                            rItems.Add(cleanDisk);
                        }
                    }
                }
            }
        }

        var rankedReleases = new List<RankedRelease>();
        foreach (var (relKey, relItems) in releaseGroups)
        {
            relItems.Sort((x, y) => GetDiskSortKey(x).CompareTo(GetDiskSortKey(y)));
            var sampleParsed = relItems[0].Parsed;

            // The format of a release is that of its parsed name: with a DAT, the name the DAT gives the file, which the
            // sort writes (a file identified by its checksum may carry another extension in the source), otherwise the
            // name of the file or of the archive member. TiebreakApplicability relies on it to grey out the format
            // criteria of a DAT whose files all have one format
            var firstFileName = sampleParsed.RawFilename;

            var r = _config.Filters.PriorityLanguages.Count > 0
                ? _filterService.GetLanguagePriorityRank(sampleParsed, _config.Filters.PriorityLanguages)
                : null;
            var cRank = _config.Filters.PriorityCountries.Count > 0
                ? _filterService.GetCountryPriorityRank(sampleParsed, _config.Filters.PriorityCountries)
                : null;

            var dumpRank = _filterService.GetDumpQualityRank(sampleParsed);
            var originalScore = _filterService.GetOriginalReleaseScore(sampleParsed);
            var verTuple = _filterService.ParseVersionTuple(_filterService.GetEffectiveVersion(sampleParsed));
            var revTuple = _filterService.ParseRevisionTuple(sampleParsed);
            var dateTuple = _filterService.ParseDateTuple(sampleParsed.Date);
            var videoStd = _filterService.GetEffectiveVideo(sampleParsed);
            var mediaCat = _filterService.GetMediaCategory(firstFileName);
            var mediaExt = _filterService.GetMediaExtension(firstFileName);
            var mediaFidelity = _filterService.GetMediaFormatFidelityScore(firstFileName, sampleParsed);
            var isMonolingual = !sampleParsed.IsMultilingual && sampleParsed.LanguageCodes.Count <= 1;
            var regionAffinity = _filterService.GetRegionLanguageAffinity(sampleParsed, _config.Filters.PriorityLanguages, r);
            var isParent = IsParentRelease(relItems) ? 1 : 0;

            int completeness;
            if (sampleParsed.IsMultiMedia)
            {
                var partsFound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var it in relItems)
                {
                    var (partNum, _) = MultiDiskEvaluator.ExtractDiskPartInfo(it.Parsed);
                    if (partNum.HasValue)
                    {
                        partsFound.Add(partNum.Value.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        var label = !string.IsNullOrEmpty(it.Parsed.MediaLabel) ? it.Parsed.MediaLabel : (it.Parsed.MediaType ?? string.Empty);
                        if (!string.IsNullOrEmpty(label))
                        {
                            partsFound.Add(label.ToLowerInvariant());
                        }
                    }
                }
                completeness = partsFound.Count;
            }
            else
            {
                // A MAME set that lacks ROMs or disks ranks after a complete one
                completeness = relItems.Any(it => it.MameSet is { IsComplete: false }) ? 0 : 1;
            }

            static int EffectiveNameLen(ProcessItem it)
            {
                var fileName = Path.GetFileName(it.SourcePath);
                var cleaned = NeutralizedDiskRoleTagsRegex().Replace(fileName, string.Empty);
                return cleaned.Length;
            }

            var minNameLen = relItems.Min(EffectiveNameLen);
            var firstName = relItems.Select(it => Path.GetFileName(it.SourcePath)).OrderBy(n => n, StringComparer.Ordinal).First();

            rankedReleases.Add(new RankedRelease
            {
                RelKey = relKey,
                EditionKey = releaseEditionKeys[relKey],
                Items = relItems,
                Sample = sampleParsed,
                Rank = r,
                CountryRank = cRank,
                DumpRank = dumpRank,
                OriginalScore = originalScore,
                VerTuple = verTuple,
                RevTuple = revTuple,
                DateTuple = dateTuple,
                VideoStd = videoStd,
                MediaCat = mediaCat,
                MediaExt = mediaExt,
                MediaFidelity = mediaFidelity,
                IsMonolingual = isMonolingual,
                RegionAffinity = regionAffinity,
                IsParent = isParent,
                DriverRank = GetDriverRank(sampleParsed.Machine?.DriverStatus ?? sampleParsed.SoftwareSupport),
                AmigaRank = AmigaChipsets.GetAgaRank(sampleParsed),
                Completeness = completeness,
                MinNameLen = minNameLen,
                FirstName = firstName
            });
        }

        var multidiskMediaCats = rankedReleases
            .Where(rel => rel.Completeness > 1 && rel.Completeness >= multiDiskResult.ReleaseTotals.GetValueOrDefault(rel.RelKey, 1))
            .Select(rel => rel.MediaCat)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var rel in rankedReleases)
        {
            // A set that lacks disks loses to a complete release, even when no other set is complete (a demo on one disk
            // beats a game whose first disk is missing); a release on one disk loses to a complete set of its media
            var totalExp = multiDiskResult.ReleaseTotals.GetValueOrDefault(rel.RelKey, 1);
            var isMultidiskRelease = rel.Completeness > 1 || totalExp > 1;
            rel.IsCompleteMultidisk = isMultidiskRelease
                ? ((rel.Completeness > 1 && rel.Completeness >= totalExp) ? 1 : 0)
                : (multidiskMediaCats.Contains(rel.MediaCat) ? 0 : 1);

            // A MAME set that lacks ROMs or disks (a split clone without its parent) does not run by itself: it loses
            // to a complete set as soon as this criterion, so that 1G1R never keeps it in place of its parent
            if (rel.Items.Any(it => it.MameSet is { IsComplete: false }))
            {
                rel.IsCompleteMultidisk = 0;
            }
        }

        // Check for manual 1G1R override on this canonical game
        if (groupItems.Count > 0 && _config.Filters.Manual1G1RWinners.Count > 0)
        {
            var gameKeyStr = _filterService.SerializeCanonicalGameKey(groupItems[0].Parsed);
            if (_config.Filters.Manual1G1RWinners.TryGetValue(gameKeyStr, out var forcedName) && !string.IsNullOrEmpty(forcedName))
            {
                RankedRelease? forcedRel = null;
                ProcessItem? forcedItem = null;
                foreach (var rel in rankedReleases)
                {
                    foreach (var it in rel.Items)
                    {
                        var disp = !string.IsNullOrEmpty(it.ArchiveMember) ? it.ArchiveMember : Path.GetFileName(it.SourcePath);
                        if (disp == forcedName || it.TargetFilename == forcedName || Path.GetFileName(it.SourcePath) == forcedName)
                        {
                            forcedRel = rel;
                            forcedItem = it;
                            break;
                        }
                    }
                    if (forcedRel is not null) break;
                }

                if (forcedRel is not null && forcedItem is not null)
                {
                    forcedRel.Items.Sort((x, y) =>
                    {
                        var kx = GetDiskSortKey(x);
                        var ky = GetDiskSortKey(y);
                        var cmp = kx.DiskNum.CompareTo(ky.DiskNum);
                        if (cmp != 0) return cmp;
                        var fx = ReferenceEquals(x, forcedItem) ? 0 : 1;
                        var fy = ReferenceEquals(y, forcedItem) ? 0 : 1;
                        if ((cmp = fx.CompareTo(fy)) != 0) return cmp;
                        if ((cmp = kx.NegCleanScore.CompareTo(ky.NegCleanScore)) != 0) return cmp;
                        if ((cmp = kx.NegVerified.CompareTo(ky.NegVerified)) != 0) return cmp;
                        if ((cmp = kx.NameLen.CompareTo(ky.NameLen)) != 0) return cmp;
                        return string.CompareOrdinal(kx.Name, ky.Name);
                    });

                    if (!forcedItem.IsMultiMedia || MultiDiskEvaluator.ExtractDiskNumber(forcedItem.Parsed) is 0 or 1)
                    {
                        forcedRel.FirstName = !string.IsNullOrEmpty(forcedItem.ArchiveMember)
                            ? forcedItem.ArchiveMember
                            : Path.GetFileName(forcedItem.SourcePath);
                    }

                    ApplyWinner(forcedRel, rankedReleases, groupItems, isManualOverride: true);
                    return;
                }
            }
        }

        var requireCompleteMulti = isNoIntroGame
            ? _config.Filters.NoIntroCompleteMultimediaSetsOnly == true
            : _config.Filters.CompleteMultidiskSetsOnly;

        var candidateReleases = requireCompleteMulti
            ? rankedReleases.Where(rel => rel.Completeness >= multiDiskResult.ReleaseTotals.GetValueOrDefault(rel.RelKey, 1)).ToList()
            : rankedReleases.ToList();

        var hasPrioLangs = _config.Filters.PriorityLanguages.Count > 0;
        var hasPrioCountries = _config.Filters.PriorityCountries.Count > 0;
        var prioCountriesUpper = _config.Filters.PriorityCountries
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var allowUntaggedTosecCountry = prioCountriesUpper.Overlaps(["WO", "EU", "US", "GB", "UK"]);

        bool IsCountryAdmissible(RankedRelease rel)
        {
            if (rel.CountryRank.HasValue) return true;
            var sample = rel.Sample;
            return allowUntaggedTosecCountry &&
                   sample.Convention == NamingConvention.Tosec &&
                   sample.CountryCodes.Count == 0 &&
                   sample.LanguageCodes.Count == 0;
        }

        var isCountryFirstOrder = _isCountryFirstOrder[TiebreakOrder.OrderConventionOf(
            rankedReleases.Count > 0 ? rankedReleases[0].Sample.Convention : NamingConvention.Tosec)];
        var countryFirst = (isCountryFirstOrder && hasPrioCountries) ||
                           (hasPrioCountries && !hasPrioLangs);

        if (countryFirst)
        {
            if (hasPrioLangs && !_config.Filters.FallbackUnmatchedLanguages)
            {
                var langMatching = candidateReleases.Where(rel => rel.Rank.HasValue).ToList();
                if (langMatching.Count == 0)
                {
                    ExcludeAllReleases(rankedReleases, LocalizedMessage.Create("reason.no_version_in_priority_languages"));
                    return;
                }
                candidateReleases = langMatching;
            }

            var validReleases = candidateReleases.Where(IsCountryAdmissible).ToList();
            if (validReleases.Count > 0)
            {
                validReleases.Sort(CompareReleaseOrder);
                ApplyWinner(validReleases[0], rankedReleases, groupItems);
            }
            else if (!_config.Filters.FallbackUnmatchedCountries)
            {
                ExcludeAllReleases(rankedReleases, LocalizedMessage.Create("reason.no_version_in_priority_countries"));
            }
            else
            {
                var fallbackPool = candidateReleases.Count > 0 ? candidateReleases : rankedReleases;
                if (fallbackPool.Count > 0)
                {
                    fallbackPool.Sort(CompareReleaseOrder);
                    ApplyWinner(fallbackPool[0], rankedReleases, groupItems);
                }
            }
        }
        else
        {
            if (_config.Filters.FilterCountries && hasPrioCountries && !_config.Filters.FallbackUnmatchedCountries)
            {
                var countryMatching = candidateReleases.Where(IsCountryAdmissible).ToList();
                if (countryMatching.Count == 0)
                {
                    ExcludeAllReleases(rankedReleases, LocalizedMessage.Create("reason.no_version_in_priority_countries"));
                    return;
                }
                candidateReleases = countryMatching;
            }

            var validReleases = candidateReleases.Where(rel => rel.Rank.HasValue).ToList();
            if (validReleases.Count > 0)
            {
                // The cascade ranks every release in a priority language: with the language first (the default order),
                // the best language wins; a criterion placed above the language by the user decides first
                validReleases.Sort(CompareReleaseOrder);
                ApplyWinner(validReleases[0], rankedReleases, groupItems);
            }
            else if (!_config.Filters.FallbackUnmatchedLanguages)
            {
                ExcludeAllReleases(rankedReleases, LocalizedMessage.Create("reason.no_version_in_priority_languages"));
            }
            else
            {
                var fallbackPool = candidateReleases.Count > 0 ? candidateReleases : rankedReleases;
                if (_config.Filters.FilterCountries && hasPrioCountries)
                {
                    var countryMatching = fallbackPool.Where(rel => rel.CountryRank.HasValue).ToList();
                    if (countryMatching.Count > 0)
                    {
                        fallbackPool = countryMatching;
                    }
                }

                if (fallbackPool.Count > 0)
                {
                    fallbackPool.Sort(CompareReleaseOrder);
                    ApplyWinner(fallbackPool[0], rankedReleases, groupItems);
                }
            }
        }
    }

    private static void ExcludeAllReleases(IEnumerable<RankedRelease> rankedReleases, LocalizedMessage reason)
    {
        foreach (var rel in rankedReleases)
        {
            foreach (var it in rel.Items)
            {
                if (it.Is1G1RWinner) continue;
                it.Status = FileStatus.Excluded;
                it.StatusReason = reason;
            }
        }
    }

    private void ApplyWinner(
        RankedRelease winningRelease,
        IReadOnlyList<RankedRelease> rankedReleases,
        IReadOnlyList<ProcessItem> groupItems,
        bool isManualOverride = false)
    {
        var winningName = winningRelease.FirstName;
        var seenMediaKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var it in winningRelease.Items)
        {
            string mKey;
            if (it.IsMultiMedia)
            {
                var (partNum, _) = MultiDiskEvaluator.ExtractDiskPartInfo(it.Parsed);
                if (partNum.HasValue)
                {
                    mKey = partNum.Value.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    var label = !string.IsNullOrEmpty(it.Parsed.MediaLabel) ? it.Parsed.MediaLabel : (it.Parsed.MediaType ?? string.Empty);
                    var mNum = MultiDiskEvaluator.SingleNumberRegex().Match(label);
                    mKey = mNum.Success ? mNum.Groups[1].Value : label.ToLowerInvariant();
                }
            }
            else
            {
                mKey = string.Empty;
            }

            if (!seenMediaKeys.Add(mKey))
            {
                it.Status = FileStatus.Duplicate;
                it.Is1G1RWinner = false;
                it.WinnerReference = winningName;
                it.StatusReason = LocalizedMessage.Create("reason.duplicate_1g1r");
            }
            else
            {
                it.Is1G1RWinner = true;
                it.Status = FileStatus.Valid;
                it.StatusReason = isManualOverride
                    ? LocalizedMessage.Create("reason.1g1r_winner_manual")
                    : LocalizedMessage.Create("reason.1g1r_winner");
            }
        }

        foreach (var rel in rankedReleases)
        {
            if (ReferenceEquals(rel, winningRelease))
            {
                continue;
            }

            var decisive = isManualOverride ? TiebreakCriterion.None : FindDecisiveCriterion(winningRelease, rel);
            var reasonDetail = isManualOverride
                ? LocalizedMessage.Create("reason.1g1r_reason_manual")
                : FormatTiebreak(decisive, winningRelease, rel);

            foreach (var it in rel.Items)
            {
                if (it.Is1G1RWinner)
                {
                    continue;
                }

                it.Status = FileStatus.Excluded;
                it.Is1G1RWinner = false;
                it.WinnerReference = winningName;
                it.EliminationDetail = reasonDetail;
                it.EliminatedBy = decisive;
                it.StatusReason = LocalizedMessage.Create("reason.1g1r_discarded", ("winner", winningName), ("reason", reasonDetail));
            }
        }

        foreach (var it in groupItems)
        {
            if (!it.Is1G1RWinner && it.Status == FileStatus.Valid)
            {
                it.Status = FileStatus.Excluded;
                it.Is1G1RWinner = false;
                it.WinnerReference = winningName;
                if (it.StatusReason is null)
                {
                    it.StatusReason = LocalizedMessage.Create("reason.1g1r_discarded", ("winner", winningName), ("reason", string.Empty));
                }
            }
        }
    }

    /// <summary>
    /// Explains why <paramref name="winner"/> was kept over <paramref name="loser"/>. The decisive criterion is the
    /// first one of the cascade on which the winner is strictly better: the loser can be better on an earlier one
    /// when the country or language filters left it out of the candidates.
    /// </summary>
    public LocalizedMessage Explain1G1RTiebreak(RankedRelease winner, RankedRelease loser) =>
        FormatTiebreak(FindDecisiveCriterion(winner, loser), winner, loser);

    /// <summary>First criterion of the cascade on which <paramref name="winner"/> is strictly better than <paramref name="loser"/>.</summary>
    private TiebreakCriterion FindDecisiveCriterion(RankedRelease winner, RankedRelease loser)
    {
        foreach (var (criterion, compare) in CascadeOf(winner, loser))
        {
            if (compare(winner, loser) < 0)
            {
                return criterion;
            }
        }

        return TiebreakCriterion.None;
    }

    private LocalizedMessage FormatTiebreak(TiebreakCriterion criterion, RankedRelease winner, RankedRelease loser)
    {
        var filters = _config.Filters;
        switch (criterion)
        {
            case TiebreakCriterion.LanguageRank:
                return FormatLanguageTiebreak(winner, loser, filters);

            case TiebreakCriterion.CountryRank:
                return FormatCountryTiebreak(winner, loser, filters);

            case TiebreakCriterion.CompleteMultidiskSet:
                // A complete set that is also a better dump or the original release is explained by it
                if (winner.DumpRank > loser.DumpRank)
                {
                    return FormatGoodDumpTiebreak(loser);
                }

                if (winner.OriginalScore > loser.OriginalScore)
                {
                    return FormatOriginalReleaseTiebreak(winner, loser);
                }

                return loser.Items.Select(it => it.MameSet).FirstOrDefault(set => set is { IsComplete: false }) is { } incompleteMameSet
                    ? ReleaseSelector.DescribeIncompleteSet(incompleteMameSet)
                    : LocalizedMessage.Create("reason.incomplete_multidisk_set", ("found", loser.Completeness), ("total", winner.Completeness));

            case TiebreakCriterion.AmigaChipset:
                return LocalizedMessage.Create(
                    "reason.1g1r_reason_amiga_chipset",
                    ("w_chipset", AmigaChipsets.DescribeForAga(winner.Sample)),
                    ("l_chipset", AmigaChipsets.DescribeForAga(loser.Sample)));

            case TiebreakCriterion.GoodDump:
                return FormatGoodDumpTiebreak(loser);

            case TiebreakCriterion.OriginalRelease:
                return FormatOriginalReleaseTiebreak(winner, loser);

            case TiebreakCriterion.DriverStatus:
                return LocalizedMessage.Create(
                    "reason.1g1r_reason_driver",
                    ("w_status", DriverStatusMessage(winner)),
                    ("l_status", DriverStatusMessage(loser)));

            case TiebreakCriterion.VideoStandard:
                return LocalizedMessage.Create("reason.1g1r_reason_video", ("video", filters.PreferredVideo.ToConfigString().ToUpperInvariant()));

            case TiebreakCriterion.PreferredMediaType:
                object mediaLabel = filters.PreferredMediaType switch
                {
                    MediaPreference.Floppy => (object)LocalizedMessage.Create("media.floppy"),
                    MediaPreference.Tape => LocalizedMessage.Create("media.tape"),
                    MediaPreference.Cartridge => LocalizedMessage.Create("media.cartridge"),
                    MediaPreference.Cd => LocalizedMessage.Create("media.cd"),
                    MediaPreference.Hd => LocalizedMessage.Create("media.hd"),
                    _ => filters.PreferredMediaType.ToConfigString()
                };
                return LocalizedMessage.Create("reason.1g1r_reason_media", ("media", mediaLabel));

            case TiebreakCriterion.MediaFidelity:
                object wExt = !string.IsNullOrEmpty(winner.MediaExt) ? winner.MediaExt : LocalizedMessage.Create("desc.main_version");
                object lExt = !string.IsNullOrEmpty(loser.MediaExt) ? loser.MediaExt : LocalizedMessage.Create("desc.secondary_version");
                return LocalizedMessage.Create("reason.1g1r_reason_media_fidelity", ("w_ext", wExt), ("l_ext", lExt));

            case TiebreakCriterion.LanguageCount:
                return filters.PreferMonolingual
                    ? LocalizedMessage.Create("reason.1g1r_reason_monolingual")
                    : LocalizedMessage.Create("reason.1g1r_reason_multilingual");

            case TiebreakCriterion.RegionAffinity:
                object wRegion = !string.IsNullOrEmpty(winner.Sample.Country) ? winner.Sample.Country : LocalizedMessage.Create("desc.main_region");
                return LocalizedMessage.Create("reason.1g1r_reason_region", ("region", wRegion));

            case TiebreakCriterion.Version:
                return LocalizedMessage.Create("reason.1g1r_reason_version", ("w_ver", DescribeVersion(winner)), ("l_ver", DescribeVersion(loser)));

            case TiebreakCriterion.Revision:
                return LocalizedMessage.Create("reason.1g1r_reason_rev");

            case TiebreakCriterion.Date:
                var wDate = !string.IsNullOrEmpty(winner.Sample.Date) ? winner.Sample.Date : "?";
                return LocalizedMessage.Create("reason.1g1r_reason_date", ("w_date", wDate));

            case TiebreakCriterion.Completeness:
                return loser.Items.Select(it => it.MameSet).FirstOrDefault(set => set is { IsComplete: false }) is { } incompleteSet
                    ? ReleaseSelector.DescribeIncompleteSet(incompleteSet)
                    : LocalizedMessage.Create("reason.incomplete_multidisk_set", ("found", loser.Completeness), ("total", winner.Completeness));

            case TiebreakCriterion.ParentClone:
                return LocalizedMessage.Create("reason.1g1r_reason_parent");

            default:
                // The name, or no difference at all
                return LocalizedMessage.Create("desc.clean_official");
        }
    }

    private static LocalizedMessage FormatGoodDumpTiebreak(RankedRelease loser) =>
        loser.DumpRank == 0
            ? LocalizedMessage.Create("reason.1g1r_reason_good_dump")
            : LocalizedMessage.Create("reason.1g1r_reason_verified_dump");

    private LocalizedMessage FormatOriginalReleaseTiebreak(RankedRelease winner, RankedRelease loser)
    {
        var isOriginal = _filterService.IsUnmodifiedRelease(winner.Sample);
        if (!winner.Sample.IsRerelease && loser.Sample.IsRerelease && isOriginal)
        {
            return LocalizedMessage.Create("reason.1g1r_reason_original_over_rerelease", ("tag", loser.Sample.RereleaseTag ?? "Re-release"));
        }

        return isOriginal
            ? LocalizedMessage.Create("reason.1g1r_reason_clean")
            : LocalizedMessage.Create("reason.1g1r_reason_less_modified");
    }

    private LocalizedMessage FormatCountryTiebreak(RankedRelease winner, RankedRelease loser, FilterConfig config)
    {
        object wCountry = winner.CountryRank.HasValue && winner.CountryRank.Value >= 0 && winner.CountryRank.Value < config.PriorityCountries.Count
            ? config.PriorityCountries[winner.CountryRank.Value].ToUpperInvariant()
            : (!string.IsNullOrEmpty(winner.Sample.Country) ? winner.Sample.Country : LocalizedMessage.Create("desc.main_region"));
        var lCountry = loser.CountryRank.HasValue && loser.CountryRank.Value >= 0 && loser.CountryRank.Value < config.PriorityCountries.Count
            ? config.PriorityCountries[loser.CountryRank.Value].ToUpperInvariant()
            : (loser.Sample.Country ?? string.Empty);

        return !string.IsNullOrEmpty(lCountry)
            ? LocalizedMessage.Create("reason.1g1r_reason_country_priority", ("w_country", wCountry), ("l_country", lCountry))
            : LocalizedMessage.Create("reason.1g1r_reason_region", ("region", wCountry));
    }

    private LocalizedMessage FormatLanguageTiebreak(RankedRelease winner, RankedRelease loser, FilterConfig config)
    {
        var wLang = winner.Rank.HasValue && winner.Rank.Value >= 0 && winner.Rank.Value < config.PriorityLanguages.Count
            ? config.PriorityLanguages[winner.Rank.Value].ToUpperInvariant()
            : (!string.IsNullOrEmpty(winner.Sample.Language) ? winner.Sample.Language.ToUpperInvariant() : "?");
        object lLang = loser.Rank.HasValue && loser.Rank.Value >= 0 && loser.Rank.Value < config.PriorityLanguages.Count
            ? config.PriorityLanguages[loser.Rank.Value].ToUpperInvariant()
            : (!string.IsNullOrEmpty(loser.Sample.Language) ? loser.Sample.Language.ToUpperInvariant() : LocalizedMessage.Create("desc.other_language"));

        return LocalizedMessage.Create("reason.1g1r_reason_lang", ("w_lang", wLang), ("l_lang", lLang));
    }
}
