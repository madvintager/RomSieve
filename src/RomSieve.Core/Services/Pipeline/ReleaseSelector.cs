using System.IO;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Selection of the releases to keep once the entries are classified: manual 1G1R winners,
/// multi-disk evaluation, then either the 1G1R resolver or the language and country filters.
/// Single entry point shared by the full pipeline and the interactive per-game recalculation,
/// so that both always give the same result.
/// </summary>
public sealed class ReleaseSelector
{
    private readonly SortConfig _config;
    private readonly IRomFilterService _filterService;
    private readonly ILocalizationService _i18n;

    public ReleaseSelector(SortConfig config, IRomFilterService filterService, ILocalizationService i18n)
    {
        _config = config;
        _filterService = filterService;
        _i18n = i18n;
    }

    /// <summary>
    /// Whether the 1G1R resolver handles every game, instead of the language and country filters.
    /// </summary>
    public static bool Is1G1RActive(FilterConfig filters) =>
        filters.OneGameOneRom &&
        (filters.PriorityLanguages.Count > 0 || filters.PriorityCountries.Count > 0);

    /// <summary>
    /// Selects the releases of <paramref name="items"/>. <paramref name="allItems"/>, when only some games are
    /// recalculated, holds every item of the run: the MAME sets a kept set needs (<see cref="MameSetInfo.RequiredSets"/>)
    /// are looked for there, and those that the recalculated games need are kept too.
    /// </summary>
    /// <summary>Outcome of the size budget of the last selection; <c>null</c> without budget.</summary>
    public MameSizeBudgetResult? LastBudget { get; private set; }

    public void Select(
        IList<ProcessItem> items,
        DatCatalog? datCatalog,
        IReadOnlyDictionary<string, HashSet<string>> gameMatchedRoms,
        IReadOnlyCollection<ProcessItem>? allItems = null)
    {
        SelectReleases(items, datCatalog, gameMatchedRoms);

        // The size budget weighs the whole collection, also when a single game is recalculated
        var all = allItems ?? items as IReadOnlyCollection<ProcessItem> ?? [.. items];
        LastBudget = MameSizeBudget.Apply(all, _config.Filters.MameSizeBudgetBytes);
        foreach (var outside in LastBudget?.Excluded ?? [])
        {
            outside.ReleaseStatus = outside.Status;
        }

        foreach (var required in KeepRequiredMameSets(all))
        {
            required.ReleaseStatus = required.Status;
        }

        foreach (var item in items)
        {
            item.ReleaseStatus = item.Status;
            item.IsOutsideSelection = false;
            item.IsNotIsolated = item.Status == FileStatus.Unmatched && !_config.IsolateUnmatched;

            // A kept MAME set that is split, merged or relies on an absent BIOS says so, unless it already says why it
            // is kept (1G1R winner)
            if (item.Status == FileStatus.Valid && item.StatusReason is null && item.MameSet is { } set && DescribeSetLayout(set) is { } notice)
            {
                item.StatusReason = notice;
            }
        }
    }

    /// <summary>
    /// What a kept MAME set should tell: a split set does not run by itself (RomSieve never rebuilds it), a set relies
    /// on a BIOS or on devices absent from the source, a merged set is sorted with its clones. Nothing for a non-merged set, nor for a
    /// set whose BIOS is sorted with it.
    /// </summary>
    public static LocalizedMessage? DescribeSetLayout(MameSetInfo set) =>
        set.IsSplit ? LocalizedMessage.Create("reason.mame_split_set", ("set", set.RequiredSet ?? string.Empty))
        : set.RequiresBios || set.MissingDevices.Count > 0 ? DescribeIncompleteSet(set)
        : set.IsMerged ? LocalizedMessage.Create("reason.mame_merged_set", ("clones", set.ClonesHeld))
        : null;

    private void SelectReleases(
        IList<ProcessItem> items,
        DatCatalog? datCatalog,
        IReadOnlyDictionary<string, HashSet<string>> gameMatchedRoms)
    {
        var filters = _config.Filters;

        // Forced releases are promoted before the multi-disk evaluation so that their disks count
        // in the completeness of their set, then again because that evaluation may exclude them
        // as an incomplete set.
        ApplyManualPromotions(items);
        var multiDiskResult = new MultiDiskEvaluator(_config, _filterService, _i18n).Evaluate(items, datCatalog, gameMatchedRoms);
        ExcludeIncompleteMameSets(items);
        ApplyManualPromotions(items);

        if (Is1G1RActive(filters))
        {
            new OneGameOneRomResolver(_config, _filterService, _i18n).Resolve(items, multiDiskResult);
            return;
        }

        var countryFirst = filters.RegionPriorityMode == RegionPriorityMode.CountryFirst;
        if (countryFirst)
        {
            ApplyCountryFilter(items);
            ApplyLanguageFilter(items);
        }
        else
        {
            ApplyLanguageFilter(items);
            ApplyCountryFilter(items);
        }

        // The filters skip games with a manual winner: the 1G1R resolver decides them
        if (filters.Manual1G1RWinners.Count > 0)
        {
            var overriddenItems = items
                .Where(it => it.Parsed.IsConform && filters.Manual1G1RWinners.ContainsKey(_filterService.SerializeCanonicalGameKey(it.Parsed)))
                .ToList();
            if (overriddenItems.Count > 0)
            {
                new OneGameOneRomResolver(_config, _filterService, _i18n).Resolve(overriddenItems, multiDiskResult);
            }
        }
    }

    /// <summary>
    /// When only complete sets are wanted, excludes the MAME sets that lack ROMs or disks. Decided here rather than at
    /// the identification, so that the recalculation of a game, which evaluates the filters again, keeps it.
    /// </summary>
    private void ExcludeIncompleteMameSets(IList<ProcessItem> items)
    {
        if (_config.Filters.NoIntroCompleteMultimediaSetsOnly != true)
        {
            return;
        }

        foreach (var item in items)
        {
            if (item.Status == FileStatus.Valid && item.MameSet is { IsComplete: false } set)
            {
                item.Status = FileStatus.Excluded;
                item.StatusReason = DescribeIncompleteSet(set);
            }
        }
    }

    /// <summary>
    /// Keeps the MAME sets that a kept set needs to run (its BIOS, its devices, the parent of a split clone), whatever
    /// excluded them (the BIOS or device filters, the 1G1R, an incomplete set): MAME does not run the set without them.
    /// A set already kept in another copy is not taken again.
    /// </summary>
    /// <returns>The sets kept because another needs them.</returns>
    private static List<ProcessItem> KeepRequiredMameSets(IReadOnlyCollection<ProcessItem> items)
    {
        var kept = items.Where(it => it.Status == FileStatus.Valid && it.MameSet is not null).ToList();
        if (kept.Count == 0)
        {
            return [];
        }

        var keptGames = kept.Select(it => it.MameSet?.SetKey).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var required = new List<ProcessItem>();
        foreach (var (item, requiredBy) in MameSetDependencies.FindRequiredSets(items, kept))
        {
            if (item.Status == FileStatus.Excluded && item.MameSet is { } set && !keptGames.Contains(set.SetKey))
            {
                item.Status = FileStatus.Valid;
                item.StatusReason = MameSetDependencies.DescribeRequiredSetReason(requiredBy);
                required.Add(item);
            }
        }

        return required;
    }

    /// <summary>What an incomplete MAME set lacks, and the set it relies on when it shares them with it (a BIOS, a parent).</summary>
    public static LocalizedMessage DescribeIncompleteSet(MameSetInfo set) =>
        set.RequiredSet is not { } required
            ? set is { MissingRoms: 0, MissingDisks: 0, MissingDevices.Count: > 0 }
                ? LocalizedMessage.Create("reason.mame_set_needs_devices", ("devices", string.Join(", ", set.MissingDevices)))
                : LocalizedMessage.Create("reason.mame_set_incomplete", ("roms", set.MissingRoms), ("disks", set.MissingDisks))
            : set.RequiresBios
                ? LocalizedMessage.Create("reason.mame_set_needs_bios", ("set", required), ("roms", set.MissingRoms))
                : LocalizedMessage.Create("reason.mame_set_needs_parent", ("set", required), ("roms", set.MissingRoms), ("disks", set.MissingDisks));

    private void ApplyLanguageFilter(IList<ProcessItem> items)
    {
        if (_config.Filters.FilterLanguages && _config.Filters.PriorityLanguages.Count > 0)
        {
            new LanguageFilterResolver(_config, _filterService, _i18n).Resolve(items);
        }
    }

    private void ApplyCountryFilter(IList<ProcessItem> items)
    {
        if (_config.Filters.FilterCountries && _config.Filters.PriorityCountries.Count > 0)
        {
            new CountryFilterResolver(_config, _filterService, _i18n).Resolve(items);
        }
    }

    /// <summary>
    /// Restores to <see cref="FileStatus.Valid"/> the excluded items of each release forced as a manual 1G1R winner.
    /// </summary>
    private void ApplyManualPromotions(IList<ProcessItem> items)
    {
        var manualMap = _config.Filters.Manual1G1RWinners;
        if (manualMap.Count == 0)
        {
            return;
        }

        var games = new Dictionary<string, List<ProcessItem>>(StringComparer.Ordinal);
        foreach (var it in items)
        {
            if (it.Parsed.IsConform)
            {
                var gkeyStr = _filterService.SerializeCanonicalGameKey(it.Parsed);
                if (manualMap.ContainsKey(gkeyStr))
                {
                    if (!games.TryGetValue(gkeyStr, out var list))
                    {
                        list = [];
                        games[gkeyStr] = list;
                    }
                    list.Add(it);
                }
            }
        }

        foreach (var (gkeyStr, gameItems) in games)
        {
            var forcedName = manualMap[gkeyStr];
            var isNoIntroGame = gameItems.Any(it => it.Parsed.Convention.UsesNoIntroSettings());
            var smartMulti = _config.Filters.SmartMultidiskGrouping && !isNoIntroGame;

            var forcedItem = gameItems.FirstOrDefault(it =>
                it.DisplayName == forcedName ||
                it.TargetFilename == forcedName ||
                Path.GetFileName(it.SourcePath) == forcedName);

            if (forcedItem is null)
            {
                continue;
            }

            var forcedRkey = _filterService.GetReleaseKey(forcedItem.Parsed, smartMultidisk: smartMulti);
            foreach (var it in gameItems)
            {
                if (it.Status == FileStatus.Excluded)
                {
                    var rkey = _filterService.GetReleaseKey(it.Parsed, smartMultidisk: smartMulti);
                    if (rkey == forcedRkey)
                    {
                        it.Status = FileStatus.Valid;
                        it.StatusReason = null;
                    }
                }
            }
        }
    }
}
