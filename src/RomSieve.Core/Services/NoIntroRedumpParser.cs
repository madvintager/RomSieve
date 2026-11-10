using System.IO;
using System.Text.RegularExpressions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// No-Intro and Redump parsing engine (cartridges and optical media CD/DVD/GD-ROM) using <c>[GeneratedRegex]</c>.
/// </summary>
public sealed partial class NoIntroRedumpParser : INoIntroRedumpParser
{

    private readonly ITosecParser _tosecParser;

    public NoIntroRedumpParser(ITosecParser tosecParser)
    {
        _tosecParser = tosecParser;
    }

    [GeneratedRegex(@"^(?:\[[^\]]+\]\s*)+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingBracketRegex();

    [GeneratedRegex(@"\([^()]+\)|\[[^\]]+\]", RegexOptions.CultureInvariant)]
    private static partial Regex FirstBalancedMetadataGroupRegex();

    [GeneratedRegex(@"\(([^()]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex BalancedParensRegex();

    [GeneratedRegex(@"\[([^\]]+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex BalancedBracketsRegex();

    [GeneratedRegex(@"\(Track\s*\d+\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrackMarkerRegex();

    // TOSEC date followed by the publisher; the date may be incomplete (19xx, 199x, 1990-xx), as in many TOSEC sets
    [GeneratedRegex(@"\((?:\d{2}|xx)[\dx]{2}(?:-[\dx]{2})*(?:-[a-z0-9]+)?\)\s*\([^)]+\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TosecDatePublisherMetaRegex();

    [GeneratedRegex(@"\[(?:!|(?:a|b|cr|h|t|p|m|f|tr|o|u|v)\d*(?:\b|\]))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TosecBracketsSignatureRegex();

    [GeneratedRegex(@"^(?:Rev(?:ision)?\s*([0-9a-zA-Z]+)|v([0-9]+(?:\.[0-9a-zA-Z]+)*)|PRG(\d+))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RevisionSegmentRegex();

    // A disc (Redump, No-Intro CDs) or a floppy disk (No-Intro floppy systems: "(Disk 2)"), with its total or not, and
    // the label some sets give each disk: "(Disk 1, Game Disk)", "(Disk 2 - Courses)", "(Disk 1 Side A)"
    [GeneratedRegex(@"^Dis[ck]\s*(\d+)(?:\s*of\s*(\d+))?(?:(?:\s*[,:-]|\s+(?=[a-z]))\s*\S.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiscSegmentRegex();

    // A demo, alone or followed by its number or description; not a word that only starts like it ("Demolition")
    [GeneratedRegex(@"^(?:demo|kiosk|sample|taikenban)(?![a-z])", RegexOptions.CultureInvariant)]
    private static partial Regex DemoSegmentRegex();

    // The date of a build, given to development versions: "(1999-10-07)", an unknown day "(1999-10-xx)", or with the
    // time of the build "(2001-03-14T101500)"
    [GeneratedRegex(@"^(?:19|20)\d{2}-(?:\d{2}|xx)-(?:\d{2}|xx)(?:T\d{4,6})?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BuildDateSegmentRegex();

    [GeneratedRegex(@"^Track\s*(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrackSegmentRegex();

    [GeneratedRegex(@"^b\d*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BadBracketShortRegex();

    public static readonly IReadOnlyDictionary<string, string> NoIntroRegions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["USA"] = "US",
        ["US"] = "US",
        ["Europe"] = "EU",
        ["Japan"] = "JP",
        ["France"] = "FR",
        ["Germany"] = "DE",
        ["Spain"] = "ES",
        ["Italy"] = "IT",
        ["United Kingdom"] = "GB",
        ["UK"] = "GB",
        ["Australia"] = "AU",
        ["World"] = "WO",
        ["Brazil"] = "BR",
        ["Canada"] = "CA",
        ["Korea"] = "KR",
        ["South Korea"] = "KR",
        ["Netherlands"] = "NL",
        ["Sweden"] = "SE",
        ["Norway"] = "NO",
        ["Denmark"] = "DK",
        ["Finland"] = "FI",
        ["Portugal"] = "PT",
        ["Poland"] = "PL",
        ["Russia"] = "RU",
        ["Greece"] = "GR",
        ["Czech Republic"] = "CZ",
        ["Czech"] = "CZ",
        ["Hungary"] = "HU",
        ["China"] = "CN",
        ["Taiwan"] = "TW",
        ["Hong Kong"] = "HK",
        ["Asia"] = "AS",
        ["Latin America"] = "LA",
        ["Scandinavia"] = "SCAN",
        ["Belgium"] = "BE",
        ["Switzerland"] = "CH",
        ["Austria"] = "AT",
        ["New Zealand"] = "NZ",
        ["Ireland"] = "IE",
        ["Mexico"] = "MX",
        ["Argentina"] = "AR",
        ["Peru"] = "PE",
        ["Chile"] = "CL",
        ["Colombia"] = "CO",
        ["India"] = "IN",
        ["Israel"] = "IL",
        ["Turkey"] = "TR",
        ["South Africa"] = "ZA",
        ["Croatia"] = "HR",
        ["Slovakia"] = "SK",
        ["Unknown"] = "UNK"
    };

    public static readonly IReadOnlyDictionary<string, string> NoIntroLanguages = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["En"] = "en",
        ["Fr"] = "fr",
        ["De"] = "de",
        ["Es"] = "es",
        ["It"] = "it",
        ["Nl"] = "nl",
        ["Pt"] = "pt",
        ["Sv"] = "sv",
        ["No"] = "no",
        ["Da"] = "da",
        ["Fi"] = "fi",
        ["Ja"] = "ja",
        ["Ko"] = "ko",
        ["Zh"] = "zh",
        ["Ru"] = "ru",
        ["Pl"] = "pl",
        ["El"] = "el",
        ["Cs"] = "cs",
        ["Hu"] = "hu",
        ["He"] = "he",
        ["Tr"] = "tr",
        ["Ar"] = "ar",
        ["Ca"] = "ca",
        ["Eu"] = "eu",
        ["Gl"] = "gl",
        ["Hr"] = "hr",
        ["Sk"] = "sk"
    };

    public static readonly IReadOnlyDictionary<string, string> CountryImpliedLanguage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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

    public static readonly HashSet<string> RedumpExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cue", ".bin", ".iso", ".chd", ".img", ".sub", ".ccd",
        ".gdi", ".raw", ".mdf", ".mds", ".nrg", ".cdi"
    };

    private static readonly HashSet<string> RedumpStrictDescriptorExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cue", ".gdi", ".ccd", ".sbi"
    };

    private static readonly HashSet<string> NoIntroRereleaseExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "virtual console",
        "wii virtual console",
        "3ds virtual console",
        "wii u virtual console",
        "switch online",
        "wii",
        "wiiware",
        "classic mini",
        "nes classic",
        "snes classic",
        "super nes classic",
        "genesis mini",
        "mega drive mini",
        "mega drive mini 2",
        "pc engine mini",
        "turbografx-16 mini",
        "coregrafx mini",
        "game gear micro",
        "astro city mini",
        "gamecube",
        "gamecube edition",
        "collector's edition",
        "zelda collection",
        "animal crossing",
        "lodgenet",
        "famicombox",
        "super famicom box",
        "nintendo power",
        "e-reader",
        "classic nes series",
        "famicom mini",
        "namco museum",
        "namco anthology",
        "sega ages",
        "sega smash pack",
        "capcom town",
        "capcom classics mini mix",
        "pac-man collection",
        "phantasy star collection",
        "sonic classic collection",
        "sonic mega collection",
        "sonic gems collection",
        "disney classic games",
        "retro-bit",
        "iam8bit",
        "evercade",
        "arcade1up",
        "limited run games",
        "strictly limited games",
        "piko interactive",
        "super fighter team",
        "columbus circle"
    };

    private static readonly string[] NoIntroRereleaseSubstrings =
    [
        "virtual console",
        "switch online",
        "classic mini",
        "anniversary collection",
        "legacy collection",
        "cowabunga collection",
        "collection of mana",
        "collection of saga",
        "seiken densetsu collection",
        "classic games collection"
    ];

    private static readonly HashSet<string> PalCountries = new(StringComparer.OrdinalIgnoreCase)
    {
        "EU", "FR", "DE", "GB", "IT", "ES", "AU", "NL", "SE", "NO", "DK", "FI",
        "PT", "PL", "RU", "GR", "CZ", "HU", "IN", "IL", "TR", "ZA", "HR", "SK"
    };

    private static readonly HashSet<string> NtscCountries = new(StringComparer.OrdinalIgnoreCase)
    {
        "US", "JP", "CA", "BR", "KR", "TW", "CN", "PE", "CL", "CO", "MX"
    };

    public bool IsNoIntroRereleaseSegment(string segment) => IsNoIntroRereleaseSegmentStatic(segment);

    public static bool IsNoIntroRereleaseSegmentStatic(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return false;
        }

        var parts = segment.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var rawPart in parts)
        {
            var part = rawPart.ToLowerInvariant();
            if (NoIntroRereleaseExact.Contains(part))
            {
                return true;
            }

            if (NoIntroRereleaseSubstrings.Any(sub => part.Contains(sub, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    public (string RawTitle, string MetadataTail) ExtractNoIntroRawTitleAndTail(string baseName) =>
        ExtractNoIntroRawTitleAndTailStatic(baseName);

    public static (string RawTitle, string MetadataTail) ExtractNoIntroRawTitleAndTailStatic(string baseName)
    {
        var prefixEnd = 0;
        var mLeadBracket = LeadingBracketRegex().Match(baseName);
        if (mLeadBracket.Success)
        {
            prefixEnd = mLeadBracket.Index + mLeadBracket.Length;
        }

        var splitPos = baseName.Length;
        var mParen = FirstBalancedMetadataGroupRegex().Match(baseName, prefixEnd);
        if (mParen.Success)
        {
            splitPos = mParen.Index;
        }

        var rawTitle = baseName[..splitPos].Trim();
        var tail = baseName[splitPos..];
        return (rawTitle, tail);
    }

    NamingConvention INoIntroRedumpParser.DetectNamingConvention(string filename, string headerText, string extension) =>
        DetectNamingConvention(filename, headerText, extension);

    public static NamingConvention DetectNamingConvention(
        string filename,
        string headerText = "",
        string extension = "")
    {
        if (!string.IsNullOrEmpty(headerText))
        {
            var lowHdr = headerText.ToLowerInvariant();
            if (lowHdr.Contains("redump", StringComparison.Ordinal))
                return NamingConvention.Redump;
            if (lowHdr.Contains("no-intro", StringComparison.Ordinal) || lowHdr.Contains("datomatic", StringComparison.Ordinal))
                return NamingConvention.NoIntro;
            if (lowHdr.Contains("tosec", StringComparison.Ordinal))
                return NamingConvention.Tosec;
        }

        var ext = !string.IsNullOrEmpty(extension)
            ? extension.ToLowerInvariant()
            : Path.GetExtension(filename).ToLowerInvariant();
        var baseName = !string.IsNullOrEmpty(ext) && filename.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
            ? filename[..^ext.Length]
            : filename;

        if (TrackMarkerRegex().IsMatch(baseName) || RedumpStrictDescriptorExtensions.Contains(ext))
        {
            return NamingConvention.Redump;
        }

        var (_, tail) = ExtractNoIntroRawTitleAndTailStatic(baseName);
        var parens = BalancedParensRegex().Matches(tail).Select(m => m.Groups[1].Value).ToList();
        var hasNoIntroRegion = parens.Any(p =>
            p.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .Any(r => NoIntroRegions.ContainsKey(r)));

        var hasTosecMeta = TosecDatePublisherMetaRegex().IsMatch(baseName);
        var hasTosecBrackets = TosecBracketsSignatureRegex().IsMatch(tail);
        if (hasTosecMeta || (hasTosecBrackets && !hasNoIntroRegion))
        {
            return NamingConvention.Tosec;
        }

        if (hasNoIntroRegion)
        {
            return RedumpExtensions.Contains(ext) ? NamingConvention.Redump : NamingConvention.NoIntro;
        }

        return NamingConvention.Auto;
    }

    public ParsedRomName ParseNoIntroFilename(string filename, string? filePath = null, long fileSize = 0, bool isRedump = false) =>
        ParseNoIntroFilenameStatic(filename, filePath, fileSize, isRedump);

    public static ParsedRomName ParseNoIntroFilenameStatic(
        string filename,
        string? filePath = null,
        long fileSize = 0,
        bool isRedump = false)
    {
        var ext = Path.GetExtension(filename);
        var baseName = !string.IsNullOrEmpty(ext) && filename.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
            ? filename[..^ext.Length]
            : filename;

        var (rawTitle, metadataTail) = ExtractNoIntroRawTitleAndTailStatic(baseName);
        var restoredTitle = TosecParser.RestoreTitleArticlesStatic(rawTitle);

        var parens = BalancedParensRegex().Matches(metadataTail).Select(m => m.Groups[1].Value).ToList();
        var brackets = BalancedBracketsRegex().Matches(metadataTail).Select(m => m.Groups[1].Value).ToList();

        string? country = null;
        var countryCodes = new List<string>();
        string? language = null;
        var languageCodes = new List<string>();
        var isMultilingual = false;
        int? multilingualCount = null;
        var version = string.Empty;
        int? discNum = null;
        int? discTotal = null;
        int? trackNum = null;
        string? mediaLabel = null;
        var mediaType = isRedump || RedumpExtensions.Contains(ext) ? "cd" : "cartridge";
        string? demo = null;
        string? devStatus = null;
        string? copyright = null;
        string? rereleaseTag = null;
        string? date = null;
        var isVerified = true;
        var isBadDump = false;
        var isAlternate = false;
        var isPirate = false;
        var isHack = false;
        var isTranslation = false;
        var isModified = false;
        var softwareMods = new List<string>();
        var dumpFlags = new List<string>();
        var moreInfo = new List<string>();
        string? discLabelCandidate = null;
        var previousIsDisc = false;

        foreach (var segment in parens)
        {
            var segStrip = segment.Trim();
            var followsDisc = previousIsDisc;
            previousIsDisc = false;

            // 1. Region check
            var subRegions = segStrip.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var matchedRegions = subRegions
                .Where(r => NoIntroRegions.ContainsKey(r))
                .Select(r => NoIntroRegions[r])
                .ToList();
            if (matchedRegions.Count > 0 && countryCodes.Count == 0)
            {
                countryCodes = matchedRegions;
                country = string.Join("-", matchedRegions);
                continue;
            }

            // 2. Languages check
            var subLangs = segStrip.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var matchedLangs = subLangs
                .Where(l => NoIntroLanguages.ContainsKey(l))
                .Select(l => NoIntroLanguages[l])
                .ToList();
            if (matchedLangs.Count > 0 && matchedLangs.Count == subLangs.Length)
            {
                languageCodes = matchedLangs;
                if (languageCodes.Count > 1)
                {
                    isMultilingual = true;
                    multilingualCount = languageCodes.Count;
                    language = $"M{languageCodes.Count}";
                }
                else
                {
                    language = languageCodes[0];
                }
                continue;
            }

            // 3. Revision / Version
            if (RevisionSegmentRegex().IsMatch(segStrip))
            {
                version = segStrip;
                continue;
            }

            // 4. Multi-disc or Disc number
            var mDisc = DiscSegmentRegex().Match(segStrip);
            if (mDisc.Success)
            {
                discNum = int.Parse(mDisc.Groups[1].ValueSpan);
                discTotal = mDisc.Groups[2].Success ? int.Parse(mDisc.Groups[2].ValueSpan) : null;
                mediaLabel = segStrip;
                previousIsDisc = true;
                continue;
            }

            // 5. Redump Track
            var mTrack = TrackSegmentRegex().Match(segStrip);
            if (mTrack.Success)
            {
                trackNum = int.Parse(mTrack.Groups[1].ValueSpan);
                mediaLabel = segStrip;
                continue;
            }

            // Date of the build: the first one only; a name never has two, but a second one would stay a mark of its own
            if (date is null && BuildDateSegmentRegex().IsMatch(segStrip))
            {
                date = segStrip;
                continue;
            }

            // 6. Status & Development tags
            var lowSeg = segStrip.ToLowerInvariant();
            if (lowSeg is "beta" or "proto" or "prototype" or "alpha" ||
                lowSeg.StartsWith("beta ", StringComparison.Ordinal) ||
                lowSeg.StartsWith("proto ", StringComparison.Ordinal) ||
                lowSeg.StartsWith("alpha ", StringComparison.Ordinal))
            {
                devStatus = segStrip;
                continue;
            }

            // Also a numbered or described demo: "(Demo 2)", "(Taikenban 1)", "(Kiosk, E3 1997)", "(Demo Movie)"
            if (DemoSegmentRegex().IsMatch(lowSeg))
            {
                demo = "demo";
                continue;
            }

            if (lowSeg is "unl" or "unlicensed" or "aftermarket" or "homebrew")
            {
                copyright = "Unlicensed";
                continue;
            }

            if (lowSeg.Contains("bad dump", StringComparison.Ordinal) || lowSeg == "b")
            {
                isBadDump = true;
                isVerified = false;
                dumpFlags.Add($"({segStrip})");
                continue;
            }

            if (lowSeg == "pirate" ||
                lowSeg.StartsWith("pirate ", StringComparison.Ordinal) ||
                lowSeg.StartsWith("pirate-", StringComparison.Ordinal))
            {
                isPirate = true;
                isModified = true;
                softwareMods.Add($"({segStrip})");
                continue;
            }

            if (lowSeg is "hack" or "hacked" ||
                lowSeg.StartsWith("hack ", StringComparison.Ordinal) ||
                lowSeg.StartsWith("hack-", StringComparison.Ordinal))
            {
                isHack = true;
                isModified = true;
                softwareMods.Add($"({segStrip})");
                continue;
            }

            if (lowSeg == "alt" ||
                lowSeg.StartsWith("alt ", StringComparison.Ordinal) ||
                lowSeg.StartsWith("alt-", StringComparison.Ordinal))
            {
                isAlternate = true;
                continue;
            }

            if (lowSeg.StartsWith("translated", StringComparison.Ordinal) ||
                lowSeg.StartsWith("translation", StringComparison.Ordinal) ||
                lowSeg.StartsWith("en by ", StringComparison.Ordinal) ||
                lowSeg.StartsWith("fr by ", StringComparison.Ordinal))
            {
                isTranslation = true;
                continue;
            }

            if (IsNoIntroRereleaseSegmentStatic(segStrip))
            {
                rereleaseTag = !string.IsNullOrEmpty(rereleaseTag) ? $"{rereleaseTag}, {segStrip}" : segStrip;
                moreInfo.Add($"({segStrip})");
                continue;
            }

            // Redump writes the label of a disc right after its number, "(Disc 1) (GDI Disc)", and the marks of the
            // edition after it, "(Disc 2) (Genbu) (Shokai Genteiban)"; whether it is one is decided by DiscLabelResolver
            if (followsDisc)
            {
                discLabelCandidate = $"({segStrip})";
            }

            moreInfo.Add($"({segStrip})");
        }

        // Brackets inspection
        foreach (var b in brackets)
        {
            var bStrip = b.Trim();
            var lowB = bStrip.ToLowerInvariant();
            if (lowB == "!")
            {
                isVerified = true;
            }
            else if (BadBracketShortRegex().IsMatch(lowB) || lowB.Contains("bad", StringComparison.Ordinal))
            {
                isBadDump = true;
                isVerified = false;
                dumpFlags.Add($"[{bStrip}]");
            }
            else if (lowB.StartsWith("cr", StringComparison.Ordinal) ||
                     lowB.StartsWith('h') ||
                     lowB.StartsWith('t') ||
                     lowB.StartsWith('f') ||
                     lowB.StartsWith('m') ||
                     lowB.StartsWith('p'))
            {
                softwareMods.Add($"[{bStrip}]");
                isModified = true;
                if (lowB.StartsWith('p'))
                    isPirate = true;
                else if (lowB.StartsWith('h'))
                    isHack = true;
            }
            else
            {
                moreInfo.Add($"[{bStrip}]");
            }
        }

        // Infer language from country when no explicit language tag was parsed
        if (languageCodes.Count == 0 && countryCodes.Count > 0)
        {
            foreach (var c in countryCodes)
            {
                if (CountryImpliedLanguage.TryGetValue(c, out var implied) && !languageCodes.Contains(implied))
                {
                    languageCodes.Add(implied);
                }
            }

            if (languageCodes.Count > 0 && string.IsNullOrEmpty(language))
            {
                language = languageCodes[0];
            }
        }

        // Video standard inference
        string? video = null;
        var hasPal = countryCodes.Any(c => PalCountries.Contains(c));
        var hasNtsc = countryCodes.Any(c => NtscCountries.Contains(c));
        if (hasPal && !hasNtsc)
        {
            video = "PAL";
        }
        else if (hasNtsc && !hasPal)
        {
            video = "NTSC";
        }

        var conventionType = isRedump ? NamingConvention.Redump : NamingConvention.NoIntro;
        var isMultiMedia = (discTotal is > 1) || discNum.HasValue;
        var isConform = !string.IsNullOrEmpty(rawTitle) && (countryCodes.Count > 0 || trackNum.HasValue || discNum.HasValue);

        return new ParsedRomName
        {
            RawFilename = filename,
            FilePath = filePath,
            FileSize = fileSize,
            IsTosecConform = isConform,
            BaseTitle = rawTitle,
            Title = restoredTitle,
            Date = date ?? string.Empty,
            Version = version,
            Extension = ext,
            Video = video,
            Country = country,
            CountryCodes = countryCodes,
            Language = language,
            LanguageCodes = languageCodes,
            IsMultilingual = isMultilingual,
            MultilingualCount = multilingualCount,
            Demo = demo,
            DevStatus = devStatus,
            Copyright = copyright,
            MediaType = mediaType,
            MediaLabel = mediaLabel,
            SoftwareMods = softwareMods,
            DumpFlags = dumpFlags,
            MoreInfo = moreInfo,
            IsVerified = isVerified,
            IsBadDump = isBadDump,
            IsAlternate = isAlternate,
            IsPirate = isPirate,
            IsHack = isHack,
            IsModified = isModified,
            IsTranslation = isTranslation,
            IsMultidiskOverride = isMultiMedia,
            Convention = conventionType,
            DiscNumber = discNum,
            DiscTotal = discTotal,
            TrackNumber = trackNum,
            RereleaseTag = rereleaseTag,
            DiscLabelCandidate = discLabelCandidate
        };
    }

    public ParsedRomName ParseRomFilename(
        string filename,
        string? filePath = null,
        long fileSize = 0,
        NamingConvention convention = NamingConvention.Auto)
    {
        var targetConvention = convention == NamingConvention.Auto
            ? DetectNamingConvention(filename)
            : convention;

        if (targetConvention == NamingConvention.Tosec)
        {
            return _tosecParser.ParseTosecFilename(filename, filePath, fileSize);
        }

        if (targetConvention == NamingConvention.Redump)
        {
            return ParseNoIntroFilename(filename, filePath, fileSize, isRedump: true);
        }

        if (targetConvention == NamingConvention.NoIntro)
        {
            return ParseNoIntroFilename(filename, filePath, fileSize, isRedump: false);
        }

        // Fallback AUTO: try TOSEC first, then No-Intro, then Redump (each parser sets its own convention)
        var parsedTosec = _tosecParser.ParseTosecFilename(filename, filePath, fileSize);
        if (parsedTosec.IsTosecConform)
        {
            return parsedTosec;
        }

        var parsedNi = ParseNoIntroFilename(filename, filePath, fileSize, isRedump: false);
        if (parsedNi.IsConform)
        {
            return parsedNi;
        }

        var parsedRd = ParseNoIntroFilename(filename, filePath, fileSize, isRedump: true);
        if (parsedRd.IsConform)
        {
            return parsedRd;
        }

        return parsedTosec;
    }

    public ParsedRomName ParseFilename(string filename, string? filePath = null, long fileSize = 0, bool isRedump = false) =>
        ParseNoIntroFilename(filename, filePath, fileSize, isRedump);
}
