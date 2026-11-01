using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Release comparison criteria: version, revision and date tuples, media category and format fidelity, dump
/// cleanliness, effective languages and countries, and their ranks in the priority lists.
/// </summary>
public sealed partial class ReleaseScoringService : IReleaseScoringService
{
    [GeneratedRegex(@"\b(?:(?:v|ver|version|rev|revision|build)\s*|(?-i:r)(?=\d))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionPrefixKeywordsRegex();

    [GeneratedRegex(@"(\d+)|([a-zA-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex VersionTupleTokenRegex();

    [GeneratedRegex(@"\b(?:rev|revision)\s*([0-9a-z]+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RevisionNumberRegex();

    [GeneratedRegex(@"\bprg\s*(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrgNumberRegex();

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex DigitsSequenceRegex();

    [GeneratedRegex(@"^[\[\(]m\d*\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitModifiedTagRegex();

    [GeneratedRegex(@"^[\[\(](?:cr|f|h|m|p|t|a)(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VariantIndexTagRegex();

    public static readonly IReadOnlyDictionary<string, string> CountryDefaultLanguages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["FR"] = "fr",
        ["DE"] = "de",
        ["IT"] = "it",
        ["ES"] = "es",
        ["JP"] = "ja",
        ["NL"] = "nl",
        ["SE"] = "sv",
        ["NO"] = "no",
        ["DK"] = "da",
        ["FI"] = "fi",
        ["PT"] = "pt",
        ["PL"] = "pl",
        ["RU"] = "ru",
        ["GR"] = "el",
        ["CZ"] = "cs",
        ["HU"] = "hu",
        ["GB"] = "en",
        ["US"] = "en",
        ["AU"] = "en",
        ["CA"] = "en",
        ["NZ"] = "en",
        ["IE"] = "en",
        ["BR"] = "pt",
        ["MX"] = "es",
        ["AR"] = "es",
        ["PE"] = "es",
        ["CL"] = "es",
        ["CO"] = "es",
        ["IN"] = "en",
        ["ZA"] = "en",
        ["HR"] = "hr",
        ["SK"] = "sk",
        ["CN"] = "zh",
        ["KR"] = "ko",
        ["TR"] = "tr",
        ["IL"] = "he",
        ["IS"] = "is",
        ["EU"] = "en",
        ["WO"] = "en",
        ["UK"] = "en",
        ["SCAN"] = "en",
        ["BE"] = "fr",
        ["CH"] = "de",
        ["AT"] = "de",
        ["TW"] = "zh",
        ["HK"] = "zh",
        ["LA"] = "es",
        ["AS"] = "en"
    };

    private static readonly IReadOnlyDictionary<string, string[]> CountryNativeLanguages = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["CA"] = ["en", "fr"],
        ["BE"] = ["fr", "nl", "de"],
        ["CH"] = ["de", "fr", "it"]
    };

    private static readonly HashSet<string> EuropeanLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "fr", "de", "es", "it", "nl", "pt", "sv", "no", "da", "fi", "pl", "el", "cs", "hu"
    };

    private static readonly HashSet<string> FloppyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".adf", ".d64", ".d71", ".d81", ".dsk", ".img", ".ima", ".ipf", ".st", ".stx", ".msa", ".g64", ".dmz", ".dms", ".fdi", ".scp"
    };

    private static readonly HashSet<string> TapeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tap", ".tzx", ".cas", ".cdt", ".t64", ".k7", ".wav", ".voc", ".pzx", ".uef", ".c10", ".csx"
    };

    private static readonly HashSet<string> CartridgeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".crt", ".rom", ".bin", ".nes", ".sfc", ".smc", ".smd", ".gen", ".md", ".a26", ".a78", ".col", ".int", ".gb", ".gbc", ".gba", ".pce", ".sms", ".gg"
    };

    private static readonly HashSet<string> CdExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".iso", ".cue", ".chd", ".cdi", ".gdi", ".nrg", ".mds", ".ccd"
    };

    private static readonly HashSet<string> HdExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lha", ".hdf", ".hdz", ".vhd"
    };

    public int GetRegionLanguageAffinity(
        ParsedRomName parsed,
        IReadOnlyList<string> priorityLangs,
        int? rank)
    {
        var countries = parsed.CountryCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.ToUpperInvariant())
            .ToList();
        if (countries.Count == 0)
        {
            return 1;
        }

        var matchedLang = rank.HasValue && rank.Value >= 0 && rank.Value < priorityLangs.Count
            ? priorityLangs[rank.Value].Trim().ToLowerInvariant()
            : string.Empty;

        var prioritySet = priorityLangs
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        var countryLangs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in countries)
        {
            if (CountryDefaultLanguages.TryGetValue(c, out var defLang) && !string.IsNullOrEmpty(defLang))
            {
                countryLangs.Add(defLang);
            }

            if (CountryNativeLanguages.TryGetValue(c, out var natives))
            {
                foreach (var n in natives)
                {
                    countryLangs.Add(n);
                }
            }
        }

        if (!string.IsNullOrEmpty(matchedLang))
        {
            if (countryLangs.Contains(matchedLang))
            {
                return 2;
            }

            if (matchedLang == "m")
            {
                if (countryLangs.Overlaps(prioritySet) ||
                    (countries.Any(c => c is "EU" or "WO" or "SCAN") && prioritySet.Overlaps(EuropeanLanguages)))
                {
                    return 2;
                }
            }

            if (countries.Any(c => c is "EU" or "WO" or "SCAN") && EuropeanLanguages.Contains(matchedLang))
            {
                return 1;
            }
        }

        return countryLangs.Overlaps(prioritySet) ? 1 : 0;
    }

    public HashSet<string> GetEffectiveCountries(ParsedRomName item)
    {
        var countries = item.CountryCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);

        if (countries.Contains("UK"))
        {
            countries.Add("GB");
        }
        if (countries.Contains("GB"))
        {
            countries.Add("UK");
        }

        return countries;
    }

    public HashSet<string> GetEffectiveLanguages(ParsedRomName item)
    {
        var langs = item.LanguageCodes
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        if (item.IsMultilingual)
        {
            langs.Add("m");
        }

        if (item.LanguageCodes.Count == 0 && item.CountryCodes.Count > 0)
        {
            foreach (var c in item.CountryCodes)
            {
                if (CountryDefaultLanguages.TryGetValue(c, out var implied))
                {
                    langs.Add(implied);
                }
            }
        }

        return langs;
    }

    public int[] ParseVersionTuple(string? versionStr)
    {
        if (string.IsNullOrEmpty(versionStr))
        {
            return [0];
        }

        var cleaned = VersionPrefixKeywordsRegex().Replace(versionStr, " ");
        var parts = new List<int>();
        foreach (Match m in VersionTupleTokenRegex().Matches(cleaned))
        {
            if (m.Groups[1].Success && int.TryParse(m.Groups[1].ValueSpan, out var num))
            {
                parts.Add(num);
            }
            else if (m.Groups[2].Success && parts.Count > 0)
            {
                parts.Add(char.ToLowerInvariant(m.Groups[2].Value[0]));
            }
        }

        return parts.Count > 0 ? [.. parts] : [0];
    }

    public static int CompareTuples(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        var maxLen = Math.Max(a.Count, b.Count);
        for (var i = 0; i < maxLen; i++)
        {
            var va = i < a.Count ? a[i] : 0;
            var vb = i < b.Count ? b[i] : 0;
            var cmp = va.CompareTo(vb);
            if (cmp != 0)
            {
                return cmp;
            }
        }
        return 0;
    }

    public int[] ParseRevisionTuple(ParsedRomName parsed)
    {
        IEnumerable<string> searchTargets =
        [
            parsed.Version ?? string.Empty,
            parsed.RawOptParens ?? string.Empty,
            parsed.RawFilename ?? string.Empty,
            .. parsed.MoreInfo,
            .. parsed.SoftwareMods
        ];

        foreach (var text in searchTargets)
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var mRev = RevisionNumberRegex().Match(text);
            if (mRev.Success)
            {
                var val = mRev.Groups[1].Value.ToLowerInvariant();
                if (int.TryParse(val, out var revInt))
                {
                    return [revInt];
                }

                if (val.Length == 1 && val[0] is >= 'a' and <= 'z')
                {
                    return [val[0] - 'a' + 1];
                }
            }

            var mPrg = PrgNumberRegex().Match(text);
            if (mPrg.Success && int.TryParse(mPrg.Groups[1].ValueSpan, out var prgInt))
            {
                return [prgInt];
            }
        }

        return [0];
    }

    public (int Year, int Month, int Day) ParseDateTuple(string? dateStr)
    {
        if (string.IsNullOrEmpty(dateStr))
        {
            return (0, 0, 0);
        }

        var nums = DigitsSequenceRegex().Matches(dateStr).Select(m => m.Value).ToList();
        var year = nums.Count > 0 && nums[0].Length == 4 && int.TryParse(nums[0], out var y) ? y : 0;
        var month = nums.Count > 1 && int.TryParse(nums[1], out var mo) && mo is >= 1 and <= 12 ? mo : 0;
        var day = nums.Count > 2 && int.TryParse(nums[2], out var d) && d is >= 1 and <= 31 ? d : 0;
        return (year, month, day);
    }

    public string GetEffectiveVideo(ParsedRomName parsed)
    {
        if (!string.IsNullOrEmpty(parsed.Video))
        {
            var vLow = parsed.Video.ToLowerInvariant();
            if (vLow.Contains("pal", StringComparison.Ordinal) && vLow.Contains("ntsc", StringComparison.Ordinal))
                return "ntsc-pal";
            if (vLow.Contains("pal", StringComparison.Ordinal))
                return "pal";
            if (vLow.Contains("ntsc", StringComparison.Ordinal))
                return "ntsc";
        }

        string[] ntscCountries = ["US", "JP", "CA", "BR", "KR", "TW", "MX"];
        string[] palCountries = ["FR", "DE", "GB", "IT", "ES", "AU", "EU", "NL", "SE", "NO", "DK", "FI", "PT", "PL", "RU", "GR", "CZ", "HU", "NZ", "IE"];

        var hasNtsc = parsed.CountryCodes.Any(c => ntscCountries.Contains(c.ToUpperInvariant()));
        var hasPal = parsed.CountryCodes.Any(c => palCountries.Contains(c.ToUpperInvariant()));
        if (hasNtsc && hasPal) return "ntsc-pal";
        if (hasNtsc) return "ntsc";
        if (hasPal) return "pal";
        return string.Empty;
    }

    public string GetMediaExtension(string? filenameOrPath) =>
        Path.GetExtension(filenameOrPath ?? string.Empty).ToLowerInvariant();

    public string GetMediaCategory(string? filenameOrPath)
    {
        var ext = GetMediaExtension(filenameOrPath);
        if (FloppyExtensions.Contains(ext)) return "floppy";
        if (TapeExtensions.Contains(ext)) return "tape";
        if (CartridgeExtensions.Contains(ext)) return "cartridge";
        if (CdExtensions.Contains(ext)) return "cd";
        if (HdExtensions.Contains(ext)) return "hd";

        var lowerName = (filenameOrPath ?? string.Empty).ToLowerInvariant();
        if (lowerName.Contains("(tape", StringComparison.Ordinal) || lowerName.Contains("(cassette", StringComparison.Ordinal))
            return "tape";
        if (lowerName.Contains("(disk", StringComparison.Ordinal) || lowerName.Contains("(disque", StringComparison.Ordinal))
            return "floppy";
        if (lowerName.Contains("(cartridge", StringComparison.Ordinal) || lowerName.Contains("(cart", StringComparison.Ordinal))
            return "cartridge";
        return "other";
    }

    public int GetMediaFormatFidelityScore(string? filenameOrPath, ParsedRomName? parsed = null)
    {
        var ext = GetMediaExtension(filenameOrPath);

        // Floppy family
        if (ext is ".ipf" or ".stx" or ".g64" or ".scp" or ".fdi")
            return 30;
        if (ext is ".adf" or ".d64" or ".d71" or ".d81" or ".dsk" or ".st" or ".msa" or ".img" or ".ima")
        {
            if (parsed is not null &&
                ((parsed.RawOptParens?.Contains("(sps)", StringComparison.OrdinalIgnoreCase) ?? false) ||
                 (parsed.RawFilename?.Contains("(sps)", StringComparison.OrdinalIgnoreCase) ?? false)))
            {
                return 30;
            }
            return 20;
        }
        if (ext is ".dms" or ".dmz")
            return 10;

        // Optical family
        if (ext == ".chd")
            return 32;
        if (ext is ".gdi" or ".cue")
            return 30;
        if (ext is ".cdi" or ".mds" or ".ccd" or ".nrg")
            return 20;
        if (ext == ".iso")
            return 10;

        // Tape family
        if (ext is ".tzx" or ".cdt" or ".pzx" or ".uef")
            return 30;
        if (ext is ".tap" or ".cas" or ".t64" or ".k7" or ".c10" or ".csx")
            return 20;
        if (ext is ".wav" or ".voc")
            return 10;

        // Hard drive family
        if (ext is ".lha" or ".hdf")
            return 30;
        if (ext == ".vhd")
            return 20;
        if (ext == ".hdz")
            return 10;

        // Cartridge family
        if (ext is ".crt" or ".nes" or ".sfc" or ".gb" or ".gbc" or ".gba" or ".md" or ".gen" or ".smd")
            return 30;
        if (ext is ".rom" or ".smc" or ".a26" or ".a78" or ".col" or ".int" or ".pce" or ".sms" or ".gg")
            return 20;
        if (ext == ".bin")
            return 10;

        return 20;
    }

    public bool IsExtendedAdf(ParsedRomName parsed)
    {
        foreach (var info in parsed.MoreInfo)
        {
            var low = info.Trim('[', ']', ' ', '\t').ToLowerInvariant();
            if (low.Contains("extended adf", StringComparison.Ordinal) || low.Contains("adf extended", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    public bool IsReleaseClean(ParsedRomName parsed) => !IsBadDump(parsed) && IsUnmodifiedRelease(parsed);

    public bool IsUnmodifiedRelease(ParsedRomName parsed) =>
        !(parsed.IsCrack ||
          parsed.IsTrainer ||
          parsed.IsHack ||
          parsed.IsPirate ||
          parsed.IsTranslation ||
          parsed.IsFix ||
          parsed.IsModified ||
          parsed.IsAlternate ||
          !string.IsNullOrEmpty(parsed.DevStatus) ||
          parsed.IsRerelease ||
          IsExtendedAdf(parsed));

    private static bool IsBadDump(ParsedRomName parsed) =>
        parsed.IsBadDump || parsed.IsVirus || parsed.IsOverdump || parsed.IsUnderdump;

    public int GetDumpQualityRank(ParsedRomName parsed) => IsBadDump(parsed) ? 0 : parsed.IsVerified ? 2 : 1;

    public int GetReleaseCleanlinessScore(ParsedRomName parsed) =>
        IsBadDump(parsed) ? -2000 : GetOriginalReleaseScore(parsed) + (parsed.IsVerified ? 200 : 0);

    public int GetOriginalReleaseScore(ParsedRomName parsed)
    {
        var score = 1000;
        var extAdf = IsExtendedAdf(parsed);

        if (!IsUnmodifiedRelease(parsed))
        {
            if (parsed.IsFix) score -= 80;
            if (parsed.IsRerelease) score -= 100;
            if (parsed.IsTranslation) score -= 120;
            if (parsed.IsAlternate) score -= 160;
            if (parsed.IsCrack) score -= 220;
            if (parsed.IsTrainer) score -= 260;

            var hasExplicitMTag = parsed.SoftwareMods.Any(mod => ExplicitModifiedTagRegex().IsMatch(mod));
            if (parsed.IsModified && (hasExplicitMTag || !(parsed.IsHack || parsed.IsPirate)))
            {
                score -= 350;
            }

            if (parsed.IsHack) score -= 400;
            if (parsed.IsPirate) score -= 500;
            if (!string.IsNullOrEmpty(parsed.DevStatus)) score -= 600;
            if (extAdf) score -= 950;

            score -= 15 * parsed.SoftwareMods.Count;

            foreach (var tag in parsed.SoftwareMods.Concat(parsed.DumpFlags))
            {
                var mVar = VariantIndexTagRegex().Match(tag);
                if (mVar.Success && int.TryParse(mVar.Groups[1].ValueSpan, out var varIdx) && varIdx > 1)
                {
                    score -= (varIdx - 1) * 2;
                }
            }
        }

        if (!string.IsNullOrEmpty(parsed.Copyright))
        {
            var cpLow = parsed.Copyright.ToLowerInvariant();
            if (cpLow.Contains("pd", StringComparison.Ordinal) || cpLow.Contains("freeware", StringComparison.Ordinal))
            {
                score += 20;
            }
        }
        else
        {
            score += 50;
        }

        return score;
    }

    public int? GetCountryPriorityRank(ParsedRomName item, IReadOnlyList<string> priorityCountries)
    {
        if (priorityCountries is null || priorityCountries.Count == 0)
        {
            return null;
        }

        var effective = GetEffectiveCountries(item);
        if (effective.Count == 0)
        {
            return null;
        }

        for (var idx = 0; idx < priorityCountries.Count; idx++)
        {
            var target = priorityCountries[idx].Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(target))
            {
                continue;
            }

            if (target == "UK")
            {
                target = "GB";
            }

            if (effective.Contains(target))
            {
                return idx;
            }
        }

        return null;
    }

    private static bool IsGenericMultilingualTag(string code)
    {
        var u = code.Trim().ToUpperInvariant();
        return u.StartsWith('M') && (u.Length == 1 || u[1..].All(char.IsDigit));
    }

    public int? GetLanguagePriorityRank(ParsedRomName item, IReadOnlyList<string> priorityLangs)
    {
        if (priorityLangs is null || priorityLangs.Count == 0)
        {
            return null;
        }

        var effective = GetEffectiveLanguages(item);
        var explicitItemLangs = item.LanguageCodes
            .Where(l => !string.IsNullOrWhiteSpace(l) && !IsGenericMultilingualTag(l))
            .Select(l => l.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        var explicitPrioLangs = priorityLangs
            .Where(l => !string.IsNullOrWhiteSpace(l) && !IsGenericMultilingualTag(l))
            .Select(l => l.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        for (var idx = 0; idx < priorityLangs.Count; idx++)
        {
            var target = priorityLangs[idx].Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(target))
            {
                continue;
            }

            if (target == "m")
            {
                if (item.IsMultilingual || effective.Contains("m"))
                {
                    if (explicitItemLangs.Count == 0 || explicitPrioLangs.Count == 0)
                    {
                        return idx;
                    }

                    var requiredMatches = Math.Min(2, explicitPrioLangs.Count);
                    var matchCount = explicitItemLangs.Count(l => explicitPrioLangs.Contains(l));
                    if (matchCount >= requiredMatches)
                    {
                        return idx;
                    }
                }
                continue;
            }

            if (effective.Contains(target))
            {
                return idx;
            }

            if (target == "en" && item.LanguageCodes.Count == 0 && item.CountryCodes.Count == 0)
            {
                return idx;
            }
        }

        return null;
    }
}
