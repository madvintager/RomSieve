using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Facade of the filtering and 1G1R functions, delegating to <see cref="IGameKeyService"/>,
/// <see cref="IReleaseScoringService"/> and <see cref="IFilterEvaluator"/>.
/// </summary>
public sealed class RomFilterService(
    IGameKeyService gameKeys,
    IReleaseScoringService scoring,
    IFilterEvaluator filters) : IRomFilterService
{
    /// <summary>Composes the default implementations, which are stateless.</summary>
    public static RomFilterService CreateDefault()
    {
        var scoring = new ReleaseScoringService();
        return new RomFilterService(new GameKeyService(scoring), scoring, new FilterEvaluator());
    }

    // Game keys
    public string NormalizeCanonicalTitle(string title) => gameKeys.NormalizeCanonicalTitle(title);
    public (string NormalizedTitle, string NormalizedSystem) GetCanonicalGameKey(ParsedRomName parsed) => gameKeys.GetCanonicalGameKey(parsed);
    public string SerializeCanonicalGameKey(ParsedRomName parsed) => gameKeys.SerializeCanonicalGameKey(parsed);
    public string GetEffectiveVersion(ParsedRomName parsed) => gameKeys.GetEffectiveVersion(parsed);
    public string? ExtractCrackGroup(ParsedRomName parsed) => gameKeys.ExtractCrackGroup(parsed);
    public int GetMediaTotal(ParsedRomName parsed) => gameKeys.GetMediaTotal(parsed);
    public IReadOnlyList<string> GetCleanedMoreInfo(ParsedRomName parsed) => gameKeys.GetCleanedMoreInfo(parsed);
    public string GetReleaseKey(ParsedRomName parsed, bool smartMultidisk = false) => gameKeys.GetReleaseKey(parsed, smartMultidisk);
    public string GetEditionKey(ParsedRomName parsed, bool smartMultidisk = false) => gameKeys.GetEditionKey(parsed, smartMultidisk);

    // Release scoring
    public int GetRegionLanguageAffinity(ParsedRomName parsed, IReadOnlyList<string> priorityLangs, int? rank) => scoring.GetRegionLanguageAffinity(parsed, priorityLangs, rank);
    public HashSet<string> GetEffectiveCountries(ParsedRomName item) => scoring.GetEffectiveCountries(item);
    public HashSet<string> GetEffectiveLanguages(ParsedRomName item) => scoring.GetEffectiveLanguages(item);
    public int[] ParseVersionTuple(string? versionStr) => scoring.ParseVersionTuple(versionStr);
    public int[] ParseRevisionTuple(ParsedRomName parsed) => scoring.ParseRevisionTuple(parsed);
    public (int Year, int Month, int Day) ParseDateTuple(string? dateStr) => scoring.ParseDateTuple(dateStr);
    public string GetEffectiveVideo(ParsedRomName parsed) => scoring.GetEffectiveVideo(parsed);
    public string GetMediaExtension(string? filenameOrPath) => scoring.GetMediaExtension(filenameOrPath);
    public string GetMediaCategory(string? filenameOrPath) => scoring.GetMediaCategory(filenameOrPath);
    public int GetMediaFormatFidelityScore(string? filenameOrPath, ParsedRomName? parsed = null) => scoring.GetMediaFormatFidelityScore(filenameOrPath, parsed);
    public bool IsExtendedAdf(ParsedRomName parsed) => scoring.IsExtendedAdf(parsed);
    public bool IsReleaseClean(ParsedRomName parsed) => scoring.IsReleaseClean(parsed);
    public bool IsUnmodifiedRelease(ParsedRomName parsed) => scoring.IsUnmodifiedRelease(parsed);
    public int GetDumpQualityRank(ParsedRomName parsed) => scoring.GetDumpQualityRank(parsed);
    public int GetOriginalReleaseScore(ParsedRomName parsed) => scoring.GetOriginalReleaseScore(parsed);
    public int GetReleaseCleanlinessScore(ParsedRomName parsed) => scoring.GetReleaseCleanlinessScore(parsed);
    public int? GetCountryPriorityRank(ParsedRomName item, IReadOnlyList<string> priorityCountries) => scoring.GetCountryPriorityRank(item, priorityCountries);
    public int? GetLanguagePriorityRank(ParsedRomName item, IReadOnlyList<string> priorityLangs) => scoring.GetLanguagePriorityRank(item, priorityLangs);

    // Filters
    public (bool IsKept, LocalizedMessage? Reason) EvaluateFilter(ParsedRomName item, FilterConfig config) => filters.EvaluateFilter(item, config);
}
