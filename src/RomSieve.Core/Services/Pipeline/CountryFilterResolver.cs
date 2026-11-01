using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Eligible country/region filtering without 1G1R deduplication.
/// </summary>
public sealed class CountryFilterResolver
{
    private readonly SortConfig _config;
    private readonly IRomFilterService _filterService;
    private readonly ILocalizationService _i18n;

    public CountryFilterResolver(SortConfig config, IRomFilterService filterService, ILocalizationService i18n)
    {
        _config = config;
        _filterService = filterService;
        _i18n = i18n;
    }

    public void Resolve(IList<ProcessItem> itemsToProcess)
    {
        var priorityCountries = _config.Filters.PriorityCountries;
        var fallback = _config.Filters.FallbackUnmatchedCountries;
        var manualMap = _config.Filters.Manual1G1RWinners;

        var games = new Dictionary<(string Title, string System), List<ProcessItem>>();
        foreach (var item in itemsToProcess)
        {
            if (item.Status == FileStatus.Valid)
            {
                var key = _filterService.GetCanonicalGameKey(item.Parsed);
                if (!games.TryGetValue(key, out var list))
                {
                    list = [];
                    games[key] = list;
                }
                list.Add(item);
            }
        }

        foreach (var (key, groupItems) in games)
        {
            if (manualMap.Count > 0 && manualMap.ContainsKey($"{key.Title}::{key.System}"))
            {
                continue;
            }

            var hasPriorityCountry = groupItems.Any(it => _filterService.GetCountryPriorityRank(it.Parsed, priorityCountries).HasValue);
            foreach (var it in groupItems)
            {
                var rank = _filterService.GetCountryPriorityRank(it.Parsed, priorityCountries);
                if (rank.HasValue || (!hasPriorityCountry && fallback))
                {
                    it.Status = FileStatus.Valid;
                }
                else
                {
                    it.Status = FileStatus.Excluded;
                    it.StatusReason = hasPriorityCountry
                        ? LocalizedMessage.Create("reason.country_not_selected")
                        : LocalizedMessage.Create("reason.no_version_in_priority_countries");
                }
            }
        }
    }
}
