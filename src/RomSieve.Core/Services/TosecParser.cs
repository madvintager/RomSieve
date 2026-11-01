using System.IO;
using System.Text.RegularExpressions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Two-level TOSEC Naming Convention (TNC) parsing engine:
/// - Level 1: Macro structural regex
/// - Level 2: Specialized semantic parsers (Title/Version, Opt Parens, Brackets)
/// Implementation using <c>[GeneratedRegex]</c>.
/// </summary>
public sealed partial class TosecParser : ITosecParser
{

    // Level 1 Macro Structural Regex according to TNC specification
    [GeneratedRegex(
        @"^(?<base_title>.+?)(?:\s+\((?<demo>demo(?:-[a-z]+)?)\))?\s+\((?<date>(?:19|20|xx|\d{2})[0-9x\-]{2,8})\)\s*\((?<publisher>[^)]+)\)(?<opt_parens>(?:\s*\([^)]+\))*)(?<brackets>(?:\s*\[[^\]]+\])*)(?<extension>\.[a-zA-Z0-9]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MacroTncRegex();

    // Level 2.1 Title and Version Regex. A version starts with "v" followed by a digit, possibly after a dot or a
    // letter (v1.0, v.2, vP1.00), with a lowercase "r" followed by a digit (r59), or with "Rev " / "build ": a title
    // word such as "Valley", "Vice" or "VI" is not a version
    [GeneratedRegex(
        @"^(?<title>.+?)(?:\s+(?<version>(?:(?:v(?=\.?[a-z]?\d)|(?-i:r)(?=\d)|Rev\s+|build\s+)[0-9a-zA-Z\.\-_]+)(?:\s+(?:v(?=\.?[a-z]?\d)|(?-i:r)(?=\d)|Rev\s+|build\s+)[0-9a-zA-Z\.\-_]+)*))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleVersionRegex();

    // Regex for systems matching common computer model patterns
    [GeneratedRegex(
        @"^(?:A\d{3,4}\+?|CD\d{2}|OCS(?:-AGA)?|ECS(?:-AGA)?|AGA|ST|STE|TT|Falcon\d*|\+[23][a-z]?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SystemRegex();

    // Development statuses
    [GeneratedRegex(
        @"^(?:alpha|beta|proto|preview|pre-release)(?:\s+.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DevStatusRegex();

    // Media physical pagination regexes
    [GeneratedRegex(@"^Disk\s+\d+(?:\s+of\s+\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MediaDiskRegex();

    [GeneratedRegex(@"^Side\s+[A-Z0-9]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MediaSideRegex();

    [GeneratedRegex(@"^Part\s+\d+(?:\s+of\s+\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MediaPartRegex();

    [GeneratedRegex(@"^Tape\s+\d+(?:\s+of\s+\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MediaTapeRegex();

    [GeneratedRegex(@"^Disc\s+\d+(?:\s+of\s+\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MediaDiscRegex();

    [GeneratedRegex(@"\b(\d+)\s+of\s+(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MediaNumberOfTotalRegex();

    [GeneratedRegex(@"^(?:Disk|Disc|Tape|Part)\s+(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MediaSingleNumberRegex();

    // Language regex (ISO 639-1 lowercase or M / M1-M9)
    [GeneratedRegex(@"^(?:[a-z]{2}(?:-[a-z]{2})*|[Mm]\d*)$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageRegex();

    // Country regex (ISO 3166-1 alpha-2 uppercase)
    [GeneratedRegex(@"^[A-Z]{2}(?:-[A-Z]{2})*$", RegexOptions.CultureInvariant)]
    private static partial Regex CountryRegex();

    // Parentheses & Brackets extractors
    [GeneratedRegex(@"\(([^)]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex OptParensGroupRegex();

    [GeneratedRegex(@"\[([^\]]+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketsGroupRegex();

    // Brackets regexes
    [GeneratedRegex(@"^cr(?:\d+)?(?:\s+.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModCrackRegex();

    [GeneratedRegex(@"^f(?:\d+)?(?:\s+.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModFixRegex();

    [GeneratedRegex(@"^h(?:\d+)?(?:\s+.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModHackRegex();

    [GeneratedRegex(@"^m(?:\d+)?(?:\s+.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModModifiedRegex();

    [GeneratedRegex(@"^p(?:\d+)?(?:\s+.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModPirateRegex();

    [GeneratedRegex(@"^t(?:\d+)?(?:\s+.*|\+.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModTrainerRegex();

    [GeneratedRegex(@"^tr(?:\s+.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModTranslationRegex();

    [GeneratedRegex(@"^!.*$", RegexOptions.CultureInvariant)]
    private static partial Regex DumpVerifiedRegex();

    [GeneratedRegex(@"^b(?:\d+)?(?:[\s\-]+.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DumpBadRegex();

    [GeneratedRegex(@"\bbad\s*dump\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DumpBadKeywordRegex();

    [GeneratedRegex(@"^a(?:\d+)?(?:[\s\-]+.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DumpAlternateRegex();

    [GeneratedRegex(@"^[ou](?:\d+)?(?:[\s\-]+.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DumpOverUnderRegex();

    [GeneratedRegex(@"^v(?:\d+)?(?:[\s\-]+.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DumpVirusRegex();

    private static readonly string[] ArticlesWithSpace =
    [
        "The",
        "A",
        "An",
        "Le",
        "La",
        "Les",
        "Un",
        "Une",
        "Der",
        "Die",
        "Das",
        "Ein",
        "Eine",
        "El",
        "Los",
        "Las",
        "Il",
        "Lo",
        "I",
        "Gli",
        "Una",
        "Het",
        "De",
        "Een"
    ];

    private static readonly string[] ArticlesWithApostrophe =
    [
        "L'",
        "L’",
        "D'",
        "D’"
    ];

    private static readonly HashSet<string> VideoStandards = new(StringComparer.OrdinalIgnoreCase)
    {
        "PAL", "NTSC", "PAL-60", "NTSC-PAL", "PAL-NTSC",
        "VGA", "SVGA", "XGA", "CGA", "EGA", "HGC", "MCGA", "MDA"
    };

    private static readonly HashSet<string> KnownSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        // Amiga
        "A500", "A500+", "A600", "A1000", "A1200", "A2000", "A2500", "A3000", "A3000T",
        "A4000", "A4000T", "CDTV", "CD32", "OCS", "ECS", "AGA", "OCS-AGA", "ECS-AGA",
        // Atari
        "ST", "STE", "Mega ST", "Mega-STE", "Mega STE", "TT", "TT030", "Falcon", "Falcon030",
        // Sinclair
        "+2", "+2a", "+3", "16k", "48k", "128k", "TC2048", "TC2068",
        // Others
        "130XE", "65XE", "800XL", "TURBO-R GT", "TURBO-R ST", "Aladdin Deck Enhancer",
        "MSX", "MSX2", "MSX2+", "MSX turbo R",
        "C64", "C128", "VIC-20", "Plus-4", "C16",
        "CPC464", "CPC664", "CPC6128", "GX4000"
    };

    private static readonly HashSet<string> CopyrightStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "PD", "CW", "SW", "FW", "GW",
        "Freeware", "Shareware", "Public Domain", "Cardware", "Giftware"
    };

    public string RestoreTitleArticles(string title) => RestoreTitleArticlesStatic(title);

    public static string RestoreTitleArticlesStatic(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var cleaned = title.Trim();
        if (cleaned.Contains(" - ", StringComparison.Ordinal))
        {
            var segments = cleaned.Split(" - ");
            return string.Join(" - ", segments.Select(RestoreSingleSegmentArticle));
        }

        return RestoreSingleSegmentArticle(cleaned);
    }

    private static string RestoreSingleSegmentArticle(string segment)
    {
        var cleaned = segment.Trim();

        foreach (var art in ArticlesWithApostrophe)
        {
            var suffix = $", {art}";
            if (cleaned.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var basePart = cleaned[..^suffix.Length].Trim();
                return $"{art}{basePart}";
            }
        }

        foreach (var art in ArticlesWithSpace)
        {
            var suffix = $", {art}";
            if (cleaned.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var basePart = cleaned[..^suffix.Length].Trim();
                return $"{art} {basePart}";
            }
        }

        return cleaned;
    }

    private static bool IsMediaToken(string token) =>
        MediaDiskRegex().IsMatch(token) ||
        MediaSideRegex().IsMatch(token) ||
        MediaPartRegex().IsMatch(token) ||
        MediaTapeRegex().IsMatch(token) ||
        MediaDiscRegex().IsMatch(token);

    public ParsedRomName ParseTosecFilename(string filename, string? filePath = null, long fileSize = 0) =>
        ParseTosecFilenameStatic(filename, filePath, fileSize);

    public static ParsedRomName ParseTosecFilenameStatic(string filename, string? filePath = null, long fileSize = 0)
    {
        var match = MacroTncRegex().Match(filename);
        if (!match.Success)
        {
            return new ParsedRomName
            {
                RawFilename = filename,
                FilePath = filePath,
                FileSize = fileSize,
                IsTosecConform = false,
                Title = Path.GetFileNameWithoutExtension(filename),
                Extension = Path.GetExtension(filename)
            };
        }

        var baseTitle = match.Groups["base_title"].Value.Trim();
        var demo = match.Groups["demo"].Success ? match.Groups["demo"].Value : null;
        var date = match.Groups["date"].Value.Trim();
        var publisher = match.Groups["publisher"].Value.Trim();
        var optParensStr = match.Groups["opt_parens"].Value;
        var bracketsStr = match.Groups["brackets"].Value;
        var extension = match.Groups["extension"].Value;

        string title;
        string version;
        var tvMatch = TitleVersionRegex().Match(baseTitle);
        if (tvMatch.Success)
        {
            title = tvMatch.Groups["title"].Success ? tvMatch.Groups["title"].Value.Trim() : baseTitle;
            version = tvMatch.Groups["version"].Success ? tvMatch.Groups["version"].Value.Trim() : string.Empty;
        }
        else
        {
            title = baseTitle;
            version = string.Empty;
        }

        var opt = new OptParensFields();
        PopulateOptParens(opt, optParensStr);
        var brackets = new BracketFields();
        PopulateBrackets(brackets, bracketsStr);

        return new ParsedRomName
        {
            RawFilename = filename,
            FilePath = filePath,
            FileSize = fileSize,
            IsTosecConform = true,
            BaseTitle = baseTitle,
            Demo = demo,
            Date = date,
            Publisher = publisher,
            Extension = extension,
            RawOptParens = optParensStr,
            RawBrackets = bracketsStr,
            Title = title,
            Version = version,
            Convention = NamingConvention.Tosec,
            Video = opt.Video,
            MediaType = opt.MediaType,
            MediaLabel = opt.MediaLabel,
            DiscNumber = opt.DiscNumber,
            DiscTotal = opt.DiscTotal,
            Copyright = opt.Copyright,
            DevStatus = opt.DevStatus,
            System = opt.System,
            Language = opt.Language,
            IsMultilingual = opt.IsMultilingual,
            MultilingualCount = opt.MultilingualCount,
            LanguageCodes = opt.LanguageCodes,
            Country = opt.Country,
            CountryCodes = opt.CountryCodes,
            DumpFlags = brackets.DumpFlags,
            SoftwareMods = brackets.SoftwareMods,
            MoreInfo = brackets.MoreInfo,
            IsVerified = brackets.IsVerified,
            IsBadDump = brackets.IsBadDump,
            IsAlternate = brackets.IsAlternate,
            IsOverdump = brackets.IsOverdump,
            IsUnderdump = brackets.IsUnderdump,
            IsVirus = brackets.IsVirus,
            IsCrack = brackets.IsCrack,
            IsFix = brackets.IsFix,
            IsHack = brackets.IsHack,
            IsModified = brackets.IsModified,
            IsPirate = brackets.IsPirate,
            IsTrainer = brackets.IsTrainer,
            IsTranslation = brackets.IsTranslation
        };
    }

    /// <summary>Fields read from the optional parentheses, gathered before the immutable result is built.</summary>
    private sealed class OptParensFields
    {
        public string? Video { get; set; }
        public string? MediaType { get; set; }
        public string? MediaLabel { get; set; }
        public int? DiscNumber { get; set; }
        public int? DiscTotal { get; set; }
        public string? Copyright { get; set; }
        public string? DevStatus { get; set; }
        public string? System { get; set; }
        public string? Language { get; set; }
        public bool IsMultilingual { get; set; }
        public int? MultilingualCount { get; set; }
        public List<string> LanguageCodes { get; } = [];
        public string? Country { get; set; }
        public List<string> CountryCodes { get; } = [];
    }

    /// <summary>Fields read from the brackets, gathered before the immutable result is built.</summary>
    private sealed class BracketFields
    {
        public List<string> DumpFlags { get; } = [];
        public List<string> SoftwareMods { get; } = [];
        public List<string> MoreInfo { get; } = [];
        public bool IsVerified { get; set; }
        public bool IsBadDump { get; set; }
        public bool IsAlternate { get; set; }
        public bool IsOverdump { get; set; }
        public bool IsUnderdump { get; set; }
        public bool IsVirus { get; set; }
        public bool IsCrack { get; set; }
        public bool IsFix { get; set; }
        public bool IsHack { get; set; }
        public bool IsModified { get; set; }
        public bool IsPirate { get; set; }
        public bool IsTrainer { get; set; }
        public bool IsTranslation { get; set; }
    }
    private static void PopulateOptParens(OptParensFields fields, string optParensStr)
    {
        if (string.IsNullOrEmpty(optParensStr))
        {
            return;
        }

        foreach (Match m in OptParensGroupRegex().Matches(optParensStr))
        {
            var token = m.Groups[1].Value.Trim();
            var upperToken = token.ToUpperInvariant();

            // 1. Video Standard
            if (VideoStandards.Contains(upperToken))
            {
                fields.Video ??= upperToken;
                continue;
            }

            // 2. Media pagination
            if (IsMediaToken(token))
            {
                if (fields.MediaType is null ||
                    (token.Contains(" of ", StringComparison.OrdinalIgnoreCase) &&
                     !fields.MediaType.Contains(" of ", StringComparison.OrdinalIgnoreCase)))
                {
                    fields.MediaType = token;
                }

                if (fields.MediaLabel is null ||
                    (token.Contains(" of ", StringComparison.OrdinalIgnoreCase) &&
                     !fields.MediaLabel.Contains(" of ", StringComparison.OrdinalIgnoreCase)))
                {
                    fields.MediaLabel = token;
                }

                var mOf = MediaNumberOfTotalRegex().Match(token);
                if (mOf.Success &&
                    int.TryParse(mOf.Groups[1].ValueSpan, out var dNum) &&
                    int.TryParse(mOf.Groups[2].ValueSpan, out var dTot))
                {
                    fields.DiscNumber = dNum;
                    fields.DiscTotal = dTot;
                }
                else if (!fields.DiscNumber.HasValue)
                {
                    var mNum = MediaSingleNumberRegex().Match(token);
                    if (mNum.Success && int.TryParse(mNum.Groups[1].ValueSpan, out var singleNum))
                    {
                        fields.DiscNumber = singleNum;
                    }
                }

                continue;
            }

            // 3. Copyright / Distribution status
            if (CopyrightStatuses.Contains(token))
            {
                fields.Copyright ??= token;
                continue;
            }

            // 4. Development status
            if (DevStatusRegex().IsMatch(token))
            {
                fields.DevStatus ??= token;
                continue;
            }

            // 5. Hardware System
            if (KnownSystems.Contains(token) || SystemRegex().IsMatch(token))
            {
                fields.System ??= token;
                continue;
            }

            // 6. Language code (ISO 639-1 lowercase or M / M1-M9)
            if (LanguageRegex().IsMatch(token))
            {
                fields.Language ??= token;

                if (upperToken.StartsWith('M') && (upperToken.Length == 1 || upperToken[1..].All(char.IsDigit)))
                {
                    fields.IsMultilingual = true;
                    fields.LanguageCodes.Add("M");
                    fields.LanguageCodes.Add(upperToken);
                    if (upperToken.Length > 1 && int.TryParse(upperToken.AsSpan(1), out var mCount))
                    {
                        fields.MultilingualCount = mCount;
                    }
                }
                else
                {
                    var langParts = token.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                         .Select(p => p.ToLowerInvariant())
                                         .ToList();
                    if (langParts.Count > 1)
                    {
                        fields.IsMultilingual = true;
                        fields.MultilingualCount = langParts.Count;
                        fields.LanguageCodes.Add("M");
                    }

                    fields.LanguageCodes.AddRange(langParts);
                }

                continue;
            }

            // 7. Country code (ISO 3166-1 alpha-2 uppercase)
            if (CountryRegex().IsMatch(token))
            {
                fields.Country ??= token;
                foreach (var countryPart in token.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    fields.CountryCodes.Add(countryPart.ToUpperInvariant());
                }
            }
        }
    }

    private static void PopulateBrackets(BracketFields fields, string bracketsStr)
    {
        if (string.IsNullOrEmpty(bracketsStr))
        {
            return;
        }

        foreach (Match m in BracketsGroupRegex().Matches(bracketsStr))
        {
            var token = m.Groups[1].Value.Trim();
            var bracketDisplay = $"[{token}]";

            // Dump flags
            if (DumpVerifiedRegex().IsMatch(token))
            {
                fields.DumpFlags.Add(bracketDisplay);
                fields.IsVerified = true;
                continue;
            }

            if (DumpBadRegex().IsMatch(token))
            {
                fields.DumpFlags.Add(bracketDisplay);
                fields.IsBadDump = true;
                continue;
            }

            if (DumpAlternateRegex().IsMatch(token))
            {
                fields.DumpFlags.Add(bracketDisplay);
                fields.IsAlternate = true;
                continue;
            }

            if (DumpOverUnderRegex().IsMatch(token))
            {
                fields.DumpFlags.Add(bracketDisplay);
                if (token.StartsWith("o", StringComparison.OrdinalIgnoreCase))
                {
                    fields.IsOverdump = true;
                }
                else
                {
                    fields.IsUnderdump = true;
                }
                continue;
            }

            if (DumpVirusRegex().IsMatch(token))
            {
                fields.DumpFlags.Add(bracketDisplay);
                fields.IsVirus = true;
                continue;
            }

            if (DumpBadKeywordRegex().IsMatch(token))
            {
                if (!fields.DumpFlags.Contains(bracketDisplay))
                {
                    fields.DumpFlags.Add(bracketDisplay);
                }
                fields.IsBadDump = true;
            }

            // Software modifications
            if (ModCrackRegex().IsMatch(token))
            {
                fields.SoftwareMods.Add(bracketDisplay);
                fields.IsCrack = true;
                continue;
            }

            if (ModFixRegex().IsMatch(token))
            {
                fields.SoftwareMods.Add(bracketDisplay);
                fields.IsFix = true;
                continue;
            }

            if (ModHackRegex().IsMatch(token))
            {
                fields.SoftwareMods.Add(bracketDisplay);
                fields.IsHack = true;
                continue;
            }

            if (ModModifiedRegex().IsMatch(token))
            {
                fields.SoftwareMods.Add(bracketDisplay);
                fields.IsModified = true;
                continue;
            }

            if (ModPirateRegex().IsMatch(token))
            {
                fields.SoftwareMods.Add(bracketDisplay);
                fields.IsPirate = true;
                continue;
            }

            if (ModTrainerRegex().IsMatch(token))
            {
                fields.SoftwareMods.Add(bracketDisplay);
                fields.IsTrainer = true;
                continue;
            }

            if (ModTranslationRegex().IsMatch(token))
            {
                fields.SoftwareMods.Add(bracketDisplay);
                fields.IsTranslation = true;
                continue;
            }

            // Default to more info
            fields.MoreInfo.Add(bracketDisplay);
        }
    }

    public ParsedRomName ParseFilename(string filename, string? filePath = null, long fileSize = 0) =>
        ParseTosecFilename(filename, filePath, fileSize);
}
