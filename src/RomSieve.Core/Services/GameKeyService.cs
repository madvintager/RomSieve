using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Canonical titles and the keys that group files into games, editions and releases (1G1R and multi-disk sets).
/// </summary>
public sealed partial class GameKeyService(IReleaseScoringService scoring) : IGameKeyService
{
    [GeneratedRegex(@"\s*:\s+", RegexOptions.CultureInvariant)]
    private static partial Regex ColonSubtitleSeparatorRegex();

    [GeneratedRegex(@"(?<=\w)\s*&\s*(?=\w)", RegexOptions.CultureInvariant)]
    private static partial Regex AmpersandConjunctionRegex();

    [GeneratedRegex(@"(?<=\s)\+(?=\s)", RegexOptions.CultureInvariant)]
    private static partial Regex PlusConjunctionRegex();

    [GeneratedRegex(@"\b(?:versus|vs\.?)(?=\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex VersusRegex();

    [GeneratedRegex(@"['’`]", RegexOptions.CultureInvariant)]
    private static partial Regex ApostropheRegex();

    [GeneratedRegex(@"[^a-z0-9\s]", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphanumericSpaceRegex();

    [GeneratedRegex(@"\b(?:(?:v|ver|version)\s*|(?-i:r)(?=[0-9]))([0-9]+(?:\.[0-9a-zA-Z]+)*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EffectiveVersionRegex();

    [GeneratedRegex(@"^\[cr\s*(.*?)\]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CrackGroupRegex();

    [GeneratedRegex(@"\b\d+\s+of\s+(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MediaTotalOfRegex();

    [GeneratedRegex(@"^\[(?:cp\b|bootable|non-bootable|docs\b|savegame)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiskSpecificMoreInfoRegex();

    private static readonly IReadOnlyDictionary<string, string> RomanToArabicSequels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ii"] = "2",
        ["iii"] = "3",
        ["iv"] = "4",
        ["v"] = "5",
        ["vi"] = "6",
        ["vii"] = "7",
        ["viii"] = "8",
        ["ix"] = "9",
        ["xi"] = "11",
        ["xii"] = "12",
        ["xiii"] = "13",
        ["xiv"] = "14",
        ["xv"] = "15",
        ["xvi"] = "16",
        ["xvii"] = "17",
        ["xviii"] = "18",
        ["xix"] = "19",
        ["xx"] = "20"
    };

    public string NormalizeCanonicalTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        // 1. Unify ': ' subtitle separators so RestoreTitleArticles works across both ' - ' and ': '
        var preCleaned = ColonSubtitleSeparatorRegex().Replace(title.Trim(), " - ");
        var restored = TosecParser.RestoreTitleArticlesStatic(preCleaned);

        // 2. Ligatures and Unicode NFKD diacritic stripping
        restored = restored
            .Replace("œ", "oe", StringComparison.Ordinal)
            .Replace("Œ", "oe", StringComparison.Ordinal)
            .Replace("æ", "ae", StringComparison.Ordinal)
            .Replace("Æ", "ae", StringComparison.Ordinal)
            .Replace("ß", "ss", StringComparison.Ordinal);

        var decomposed = restored.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        var text = sb.ToString();

        // 3. Unify '&' / '+' conjunctions and 'vs.' / 'versus'
        text = AmpersandConjunctionRegex().Replace(text, " and ");
        text = PlusConjunctionRegex().Replace(text, " and ");
        text = VersusRegex().Replace(text, "vs");

        // 4. Strip apostrophes and replace other punctuation with spaces
        text = ApostropheRegex().Replace(text, string.Empty);
        text = NonAlphanumericSpaceRegex().Replace(text, " ");

        // 5. Convert sequel Roman numerals to Arabic numerals with guards for 'I', 'X', and leading 'V'
        // (the words are separated by any white space, so that a tab never survives in a key)
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var idx = 0; idx < words.Length; idx++)
        {
            var word = words[idx];
            if (word == "v" && idx == 0)
            {
                continue;
            }

            if (RomanToArabicSequels.TryGetValue(word, out var arabic))
            {
                words[idx] = arabic;
            }
        }

        return string.Join(" ", words);
    }

    public (string NormalizedTitle, string NormalizedSystem) GetCanonicalGameKey(ParsedRomName parsed)
    {
        var referenceTitle = !string.IsNullOrEmpty(parsed.ParentTitle)
            ? parsed.ParentTitle
            : (!string.IsNullOrEmpty(parsed.CloneOf) ? parsed.CloneOf : parsed.Title);

        if ((!string.IsNullOrEmpty(parsed.ParentTitle) || !string.IsNullOrEmpty(parsed.CloneOf)) &&
            (referenceTitle.Contains('(') || referenceTitle.Contains('[')))
        {
            var (extractedTitle, _) = NoIntroRedumpParser.ExtractNoIntroRawTitleAndTailStatic(referenceTitle);
            if (!string.IsNullOrEmpty(extractedTitle))
            {
                referenceTitle = extractedTitle;
            }
        }

        var titleNorm = NormalizeCanonicalTitle(referenceTitle);
        var systemNorm = (parsed.System ?? string.Empty).Trim().ToLowerInvariant();

        // For the AGA Amigas, the AGA and OCS versions of a game are one game (the 1G1R prefers the AGA one)
        if (parsed.MergesAmigaChipsets && AmigaChipsets.Of(parsed) != AmigaChipset.Unmarked)
        {
            systemNorm = string.Empty;
        }

        // A group given by the DAT (MAME parent set) separates the games that share a title
        if (!string.IsNullOrEmpty(parsed.GameGroup))
        {
            systemNorm = $"{systemNorm}@{parsed.GameGroup.Trim().ToLowerInvariant()}";
        }

        // The DAT of a lot of several DATs separates the games of its DAT from those of the others; without it (one DAT),
        // the key stays what it was
        if (!string.IsNullOrEmpty(parsed.DatKey))
        {
            systemNorm = $"{systemNorm}{DatKeySeparator}{parsed.DatKey.Trim().ToLowerInvariant()}";
        }

        return (titleNorm, systemNorm);
    }

    /// <summary>
    /// Separator of the DAT segment of the keys (<see cref="ParsedRomName.DatKey"/>): a control character, absent from
    /// file names and DAT names, and already the separator of the edition and release keys.
    /// </summary>
    private const char DatKeySeparator = '\u001F';

    public string SerializeCanonicalGameKey(ParsedRomName parsed)
    {
        var (titleNorm, systemNorm) = GetCanonicalGameKey(parsed);
        return $"{titleNorm}::{systemNorm}";
    }

    public string GetEffectiveVersion(ParsedRomName parsed)
    {
        if (!string.IsNullOrWhiteSpace(parsed.Version))
        {
            return parsed.Version.Trim();
        }

        string[] searchTexts = [parsed.RawOptParens ?? string.Empty, parsed.RawFilename ?? string.Empty];
        foreach (var text in searchTexts)
        {
            var m = EffectiveVersionRegex().Match(text);
            if (m.Success)
            {
                var prefix = m.Value.StartsWith('r') ? "r" : "v";
                return $"{prefix}{m.Groups[1].Value}";
            }
        }

        return string.Empty;
    }

    public string? ExtractCrackGroup(ParsedRomName parsed)
    {
        foreach (var mod in parsed.SoftwareMods)
        {
            var m = CrackGroupRegex().Match(mod);
            if (m.Success)
            {
                return m.Groups[1].Value.Trim().ToUpperInvariant();
            }
        }

        return parsed.IsCrack ? string.Empty : null;
    }

    public int GetMediaTotal(ParsedRomName parsed)
    {
        string?[] texts = [parsed.MediaLabel, parsed.MediaType, parsed.RawOptParens, parsed.RawFilename];
        foreach (var text in texts)
        {
            if (!string.IsNullOrEmpty(text))
            {
                var mOf = MediaTotalOfRegex().Match(text);
                if (mOf.Success && int.TryParse(mOf.Groups[1].ValueSpan, out var tot))
                {
                    return tot;
                }
            }
        }

        return 1;
    }

    public IReadOnlyList<string> GetCleanedMoreInfo(ParsedRomName parsed) =>
        parsed.MoreInfo
            .Where(info => !DiskSpecificMoreInfoRegex().IsMatch(info) &&
                           !string.Equals(info, parsed.DiscLabel, StringComparison.Ordinal))
            .OrderBy(info => info, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Returns the first 11 edition components (and the DAT of a lot of several DATs, <see cref="ParsedRomName.DatKey"/>)
    /// for smart matching of clean data disks in <c>smart_multidisk_grouping</c>.
    /// </summary>
    public string GetEditionKey(ParsedRomName parsed, bool smartMultidisk = false)
    {
        var mediaTot = GetMediaTotal(parsed);
        var rawFn = parsed.RawFilename ?? string.Empty;
        var mediaCat = scoring.GetMediaCategory(rawFn);
        var mediaExt = scoring.GetMediaExtension(rawFn);
        var revStr = string.Join(".", scoring.ParseRevisionTuple(parsed));
        var countries = string.Join(",", parsed.CountryCodes.Select(c => c.ToUpperInvariant()).OrderBy(c => c, StringComparer.Ordinal));
        var langs = string.Join(",", parsed.LanguageCodes.Select(l => l.ToLowerInvariant()).OrderBy(l => l, StringComparer.Ordinal));

        var editionKey = string.Join("\u001F",
            mediaTot.ToString(CultureInfo.InvariantCulture),
            parsed.Title.Trim().ToLowerInvariant(),
            GetEffectiveVersion(parsed).Trim().ToLowerInvariant(),
            (parsed.Publisher ?? string.Empty).Trim().ToLowerInvariant(),
            (parsed.Date ?? string.Empty).Trim(),
            (parsed.System ?? string.Empty).Trim().ToLowerInvariant(),
            (parsed.Video ?? string.Empty).Trim().ToLowerInvariant(),
            $"{mediaCat}:{mediaExt}",
            revStr,
            countries,
            langs);

        // The DAT of a lot of several DATs (see GetCanonicalGameKey): the release key starts with the edition key, so
        // that neither an edition nor a release ever gathers the files of two DATs (multi-disk sets, borrowed disks)
        return string.IsNullOrEmpty(parsed.DatKey)
            ? editionKey
            : $"{editionKey}{DatKeySeparator}{parsed.DatKey.Trim().ToLowerInvariant()}";
    }

    public string GetReleaseKey(ParsedRomName parsed, bool smartMultidisk = false)
    {
        var mediaTot = GetMediaTotal(parsed);
        var edPrefix = GetEditionKey(parsed, smartMultidisk);
        var cleanedMoreInfo = string.Join(",", GetCleanedMoreInfo(parsed));
        var isMulti = mediaTot > 1 || parsed.IsMultiMedia;

        if (smartMultidisk && isMulti)
        {
            var crackGrp = ExtractCrackGroup(parsed) ?? "<none>";
            return string.Join("\u001F",
                edPrefix,
                "SMART",
                parsed.IsVerified,
                parsed.IsBadDump,
                parsed.IsAlternate,
                parsed.IsOverdump,
                parsed.IsUnderdump,
                parsed.IsVirus,
                parsed.IsCrack,
                crackGrp,
                parsed.IsTranslation,
                parsed.DevStatus ?? string.Empty,
                parsed.Demo ?? string.Empty,
                parsed.Copyright ?? string.Empty,
                cleanedMoreInfo,
                (parsed.RereleaseTag ?? string.Empty).Trim().ToLowerInvariant());
        }

        var sortedMods = string.Join(",", parsed.SoftwareMods.OrderBy(m => m, StringComparer.Ordinal));
        var sortedFlags = string.Join(",", parsed.DumpFlags.OrderBy(f => f, StringComparer.Ordinal));

        return string.Join("\u001F",
            edPrefix,
            "STRICT",
            parsed.IsVerified,
            parsed.IsBadDump,
            parsed.IsAlternate,
            parsed.IsOverdump,
            parsed.IsUnderdump,
            parsed.IsVirus,
            parsed.IsCrack,
            parsed.IsTrainer,
            parsed.IsHack,
            parsed.IsPirate,
            parsed.IsTranslation,
            parsed.IsFix,
            parsed.IsModified,
            parsed.DevStatus ?? string.Empty,
            parsed.Demo ?? string.Empty,
            parsed.Copyright ?? string.Empty,
            sortedMods,
            sortedFlags,
            cleanedMoreInfo,
            (parsed.RereleaseTag ?? string.Empty).Trim().ToLowerInvariant());
    }
}
