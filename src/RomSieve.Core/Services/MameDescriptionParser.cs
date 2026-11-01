using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Analysis of MAME machine descriptions (<c>Street Fighter II: The World Warrior (USA 910522, Rev. G)</c>): the title
/// is the text before the first parenthesis, and each comma-separated part of the parentheses is a region (with an
/// optional <c>YYMMDD</c> date), a revision, a version, a set number, a language, or a status (bootleg, hack,
/// prototype, demo). Anything else is kept as additional information.
/// </summary>
public sealed partial class MameDescriptionParser : IMameDescriptionParser
{

    // Regions of MAME descriptions with their RomSieve country codes, longest names first so that "USA" is not read
    // as "US" followed by "A"
    private static readonly (string Name, string Code)[] Regions = new (string Name, string Code)[]
    {
        ("World", "WO"),
        ("USA", "US"),
        ("US", "US"),
        ("America", "US"),
        ("Europe", "EU"),
        ("Euro", "EU"),
        ("Japan", "JP"),
        ("Asia", "AS"),
        ("Korea", "KR"),
        ("China", "CN"),
        ("Taiwan", "TW"),
        ("Hong Kong", "HK"),
        ("Hispanic", "LA"),
        ("Latin America", "LA"),
        ("Brazil", "BR"),
        ("Argentina", "AR"),
        ("Mexico", "MX"),
        ("Canada", "CA"),
        ("UK", "GB"),
        ("France", "FR"),
        ("Germany", "DE"),
        ("Spain", "ES"),
        ("Italy", "IT"),
        ("Portugal", "PT"),
        ("Netherlands", "NL"),
        ("Holland", "NL"),
        ("Belgium", "BE"),
        ("Switzerland", "CH"),
        ("Austria", "AT"),
        ("Sweden", "SE"),
        ("Norway", "NO"),
        ("Denmark", "DK"),
        ("Finland", "FI"),
        ("Greece", "GR"),
        ("Poland", "PL"),
        ("Hungary", "HU"),
        ("Czech", "CZ"),
        ("Russia", "RU"),
        ("Ukraine", "UA"),
        ("Latvia", "LV"),
        ("Australia", "AU"),
        ("Oceania", "AU"),
        ("New Zealand", "NZ")
    }.OrderByDescending(r => r.Name.Length).ToArray();

    // Languages written in full ("German", "French speech")
    private static readonly IReadOnlyDictionary<string, string> Languages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["English"] = "en",
        ["French"] = "fr",
        ["German"] = "de",
        ["Spanish"] = "es",
        ["Italian"] = "it",
        ["Dutch"] = "nl",
        ["Portuguese"] = "pt",
        ["Swedish"] = "sv",
        ["Japanese"] = "ja",
        ["Korean"] = "ko",
        ["Chinese"] = "zh",
        ["Russian"] = "ru",
        ["Polish"] = "pl",
        ["Hungarian"] = "hu",
        ["Greek"] = "el",
        ["Turkish"] = "tr"
    };

    // Languages abbreviated by the descriptions of the software lists of home computers ("Fra", "Spa", "fr")
    private static readonly IReadOnlyDictionary<string, string> SoftwareLanguageAbbreviations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Eng"] = "en",
        ["Fra"] = "fr",
        ["Fre"] = "fr",
        ["Ger"] = "de",
        ["Deu"] = "de",
        ["Spa"] = "es",
        ["Castellano"] = "es",
        ["Ita"] = "it",
        ["Dut"] = "nl",
        ["Por"] = "pt",
        ["Swe"] = "sv",
        ["en"] = "en",
        ["fr"] = "fr",
        ["de"] = "de",
        ["es"] = "es",
        ["it"] = "it",
        ["nl"] = "nl",
        ["pt"] = "pt",
        ["sv"] = "sv"
    };

    // Words that may follow a language without changing its meaning ("Italian speech"); "Italian bootleg" is a bootleg
    // made in Italy, not a language
    private static readonly HashSet<string> LanguageQualifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "speech", "voice", "voices", "language", "text", "release", "version"
    };

    [GeneratedRegex(@"\bset\s+(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SetNumberRegex();

    // "rev B", "Rev. G", "rev.9", "Revision A"; the keyword is followed by a dot, a space or a digit ("Revised" is a word)
    [GeneratedRegex(@"^rev(?:ision)?(?:\.\s*|\s+|(?=\d))(?<rev>[0-9a-z][0-9a-z.]*)(?:\s+(?<rest>.+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RevisionRegex();

    // Revision after other words, as in "Namco rev. B"
    [GeneratedRegex(@"\s(?=rev(?:ision)?(?:\.\s*|\s+|(?=\d))[0-9a-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InnerRevisionRegex();

    // "v1.1", "V. 43.17", "ver. 2.0", "Ver 2.93A", "Version 1.0R"; not "vertical"
    [GeneratedRegex(@"^(?:(?:version|ver)(?:\.\s*|\s+|(?=\d))|v\.?\s*(?=\d))(?<ver>\S+)(?:\s+(?<rest>.+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    // YYMMDD date, as in "World 910522"
    [GeneratedRegex(@"^(?<yy>\d{2})(?<mm>\d{2})(?<dd>\d{2})(?:\s+(?<rest>.+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex ShortDateRegex();

    [GeneratedRegex(@"\b(?<y>(?:19|20)\d{2})[/.-](?<m>\d{1,2})[/.-](?<d>\d{1,2})\b", RegexOptions.CultureInvariant)]
    private static partial Regex FullDateRegex();

    [GeneratedRegex(@"\b(?<m>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?\s+(?<d>\d{1,2}),?\s+(?<y>(?:19|20)\d{2})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MonthNameDateRegex();

    // US date, as in "rev 4.0 6/29/89"
    [GeneratedRegex(@"\b(?<m>\d{1,2})/(?<d>\d{1,2})/(?<y>\d{2})\b", RegexOptions.CultureInvariant)]
    private static partial Regex UsShortDateRegex();

    // Year of the DAT, possibly incomplete: 1991, 199?, 19??
    [GeneratedRegex(@"^(?:19|20)[\d?]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex DatYearRegex();

    [GeneratedRegex(@"[A-Za-z]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    // YYYYMMDD date of a software, as in "prototype 19940420"
    [GeneratedRegex(@"\b(?<y>(?:19|20)\d{2})(?<m>\d{2})(?<d>\d{2})\b", RegexOptions.CultureInvariant)]
    private static partial Regex CompactDateRegex();

    // One disk of a software that a list describes disk by disk: "(Disk 1 of 2)", "(Disk 2 of 2)[a]", "(1 de 2)"
    [GeneratedRegex(@"\(\s*(?:(?:Disk|Disc|Tape|Side)\s+(?<disk>[0-9A-Z]+)(?:\s+of\s+\d+)?|(?<disk>\d+)\s+(?:of|de|von|sur)\s+\d+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SoftwareDiskRegex();

    // A ROM known as a bad dump does not make the set a bad dump: it is the best dump known, the one MAME needs
    public ParsedRomName ParseGame(DatGame game, string? filePath = null, long fileSize = 0)
    {
        var description = string.IsNullOrWhiteSpace(game.Description) ? game.Name : game.Description;
        return ParseDescription(description, game.Year, game.Manufacturer, game.Name, filePath, fileSize) with
        {
            CloneOf = string.IsNullOrEmpty(game.CloneOf) ? null : game.CloneOf,
            Machine = new MameMachineInfo(
                game.IsBios,
                game.IsDevice,
                game.IsMechanical,
                game.DriverStatus,
                game.EmulationStatus,
                game.UnemulatedFeatures,
                game.RequiresArtwork,
                game.Cabinet,
                HasNothingToDump: game.Roms.All(rom => rom.Status == DatRomStatus.NoDump))
        };
    }

    /// <summary>
    /// A software of a MAME software list reads like a machine, with the words of the lists besides: pirate, cracked,
    /// trained and translated releases, abbreviated languages (<c>Fra</c>, <c>es</c>), <c>YYYYMMDD</c> dates of the
    /// prototypes. Its publisher is that of the DAT (<c>&lt;unknown&gt;</c> is none), and a software has no machine to
    /// describe; its list is its system (<see cref="ParsedRomName.System"/>). A list that describes a software disk by disk (<c>(Disk 1 of 2)</c>) makes each disk a game of its own
    /// (<see cref="ParsedRomName.GameGroup"/>), so that the 1G1R never keeps one disk of a software for all of them.
    /// </summary>
    public ParsedRomName ParseSoftware(DatGame game, string? filePath = null, long fileSize = 0)
    {
        var description = string.IsNullOrWhiteSpace(game.Description) ? game.Name : game.Description;
        var publisher = game.Publisher ?? game.Manufacturer;
        if (string.Equals(publisher?.Trim(), "<unknown>", StringComparison.OrdinalIgnoreCase))
        {
            publisher = null;
        }

        var parsed = Parse(description, game.Year, publisher, game.Name, filePath, fileSize, isSoftware: true) with
        {
            CloneOf = string.IsNullOrEmpty(game.CloneOf) ? null : game.CloneOf,
            SoftwareSupport = game.DriverStatus,

            // The list is the system of a software: two lists hold different games under the same title
            System = game.SoftwareList,
            Convention = NamingConvention.MameSoftwareList
        };

        var disk = SoftwareDiskRegex().Match(description);
        return disk.Success ? parsed with { GameGroup = $"disk {disk.Groups["disk"].Value.ToLowerInvariant()}" } : parsed;
    }

    public ParsedRomName ParseDescription(
        string description,
        string? year = null,
        string? manufacturer = null,
        string? rawFilename = null,
        string? filePath = null,
        long fileSize = 0) =>
        Parse(description, year, manufacturer, rawFilename, filePath, fileSize, isSoftware: false);

    private static ParsedRomName Parse(
        string description,
        string? year,
        string? manufacturer,
        string? rawFilename,
        string? filePath,
        long fileSize,
        bool isSoftware)
    {
        var (title, groups) = isSoftware
            ? SplitSoftwareDescription(description ?? string.Empty)
            : SplitDescription(description ?? string.Empty);
        var info = new DescriptionInfo { IsSoftware = isSoftware };

        foreach (var group in groups)
        {
            // Dates may contain commas ("Oct 17, 1996") or slashes: they are read before the group is split
            var remaining = ExtractDates(group, info);
            foreach (var token in remaining.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                ClassifyToken(token, info);
            }
        }

        var languageCodes = info.LanguageCodes.Count > 0
            ? info.LanguageCodes
            : info.CountryCodes
                .Select(c => NoIntroRedumpParser.CountryImpliedLanguage.TryGetValue(c, out var language) ? language : null)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();

        return new ParsedRomName
        {
            RawFilename = string.IsNullOrEmpty(rawFilename) ? description ?? string.Empty : rawFilename,
            FilePath = filePath,
            FileSize = fileSize,
            IsTosecConform = !string.IsNullOrEmpty(title),
            BaseTitle = title,
            Title = title,
            Date = info.Date ?? FormatDatYear(year),
            Publisher = manufacturer?.Trim() ?? string.Empty,
            Video = info.Video,
            Version = info.Version ?? info.Revision ?? string.Empty,
            Country = info.CountryCodes.Count > 0 ? string.Join("-", info.CountryCodes) : null,
            CountryCodes = info.CountryCodes,
            Language = languageCodes.Count switch
            {
                0 => null,
                1 => languageCodes[0],
                _ => $"M{languageCodes.Count}"
            },
            LanguageCodes = languageCodes,
            IsMultilingual = languageCodes.Count > 1,
            MultilingualCount = languageCodes.Count > 1 ? languageCodes.Count : null,
            Demo = info.Demo,
            DevStatus = info.DevStatus,
            SoftwareMods = info.SoftwareMods,
            MoreInfo = info.MoreInfo,
            IsVerified = true,
            IsAlternate = info.IsAlternate,
            IsPirate = info.IsBootleg,
            IsHack = info.IsHack,
            IsCrack = info.IsCrack,
            IsTrainer = info.IsTrainer,
            IsTranslation = info.IsTranslation,
            IsModified = info.IsBootleg || info.IsHack || info.IsCrack || info.IsTrainer,
            IsMultidiskOverride = false,
            Convention = NamingConvention.Mame
        };
    }

    // Title (text before the first parenthesis) and the content of each top-level parenthesis
    private static (string Title, List<string> Groups) SplitDescription(string description)
    {
        var groups = new List<string>();
        var titleEnd = -1;
        var depth = 0;
        var groupStart = 0;
        for (var i = 0; i < description.Length; i++)
        {
            var ch = description[i];
            if (ch == '(')
            {
                if (depth == 0)
                {
                    if (titleEnd < 0)
                    {
                        titleEnd = i;
                    }
                    groupStart = i + 1;
                }
                depth++;
            }
            else if (ch == ')' && depth > 0)
            {
                depth--;
                if (depth == 0)
                {
                    groups.Add(description[groupStart..i]);
                }
            }
        }

        var title = (titleEnd < 0 ? description : description[..titleEnd]).Trim();
        return (title, groups);
    }

    /// <summary>
    /// Title and parentheses of the description of a software: the descriptions of the lists go on after their
    /// parentheses (<c>Initiation au Langage Basic (CEGOS) - Volume 3</c>, <c>Patterns in Pitch (…): Level Two</c>),
    /// which tells two software apart; the title is all the text outside the top-level parentheses and the brackets
    /// (<c>[a]</c> of the lists taken from TOSEC), its spaces put back to one.
    /// </summary>
    private static (string Title, List<string> Groups) SplitSoftwareDescription(string description)
    {
        var groups = new List<string>();
        var title = new StringBuilder(description.Length);
        var depth = 0;
        var inBrackets = false;
        var groupStart = 0;
        for (var i = 0; i < description.Length; i++)
        {
            var ch = description[i];
            if (inBrackets)
            {
                inBrackets = ch != ']';
            }
            else if (ch == '(')
            {
                if (depth == 0)
                {
                    groupStart = i + 1;
                }
                depth++;
            }
            else if (ch == ')' && depth > 0)
            {
                depth--;
                if (depth == 0)
                {
                    groups.Add(description[groupStart..i]);
                    title.Append(' ');
                }
            }
            else if (depth == 0 && ch == '[')
            {
                inBrackets = true;
                title.Append(' ');
            }
            else if (depth == 0)
            {
                title.Append(ch);
            }
        }

        var text = MultipleSpacesRegex().Replace(title.ToString(), " ").Trim();

        // A part that only followed a parenthesis loses the space before its punctuation: "Pitch (…): Level" gives
        // "Pitch: Level"
        return (SpaceBeforePunctuationRegex().Replace(text, "$1"), groups);
    }

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultipleSpacesRegex();

    [GeneratedRegex(@"\s+([:;,.!?])", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceBeforePunctuationRegex();

    // Reads the dates of a group that are not in a part of their own ("1994/02/16", "Oct 17, 1996", "6/29/89") and
    // returns the group without them
    private static string ExtractDates(string group, DescriptionInfo info)
    {
        Regex[] regexes = info.IsSoftware
            ? [FullDateRegex(), MonthNameDateRegex(), UsShortDateRegex(), CompactDateRegex()]
            : [FullDateRegex(), MonthNameDateRegex(), UsShortDateRegex()];
        foreach (var regex in regexes)
        {
            var match = regex.Match(group);
            if (!match.Success)
            {
                continue;
            }

            var month = int.TryParse(match.Groups["m"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var m)
                ? m
                : DateTime.ParseExact(match.Groups["m"].Value[..3], "MMM", CultureInfo.InvariantCulture).Month;
            var year = int.Parse(match.Groups["y"].ValueSpan, CultureInfo.InvariantCulture);
            var day = int.Parse(match.Groups["d"].ValueSpan, CultureInfo.InvariantCulture);
            if (info.TrySetDate(year < 100 ? ExpandTwoDigitYear(year) : year, month, day))
            {
                group = group.Remove(match.Index, match.Length);
            }
        }

        return group;
    }

    private static void ClassifyToken(string token, DescriptionInfo info)
    {
        token = token.Trim();
        if (token.Length == 0)
        {
            return;
        }

        // "set 2", also inside another part ("Japan set 2", "bootleg set 1"): read, then the rest of the part
        var set = SetNumberRegex().Match(token);
        if (set.Success)
        {
            var number = int.Parse(set.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
            info.IsAlternate |= number >= 2;
            info.MoreInfo.Add($"(set {number})");
            ClassifyToken(token.Remove(set.Index, set.Length), info);
            return;
        }

        // A trailing question mark only says that MAME is not sure ("hack?", "World?")
        var core = token.TrimEnd('?', ' ');

        var revision = RevisionRegex().Match(core);
        if (revision.Success)
        {
            info.Revision ??= $"Rev {revision.Groups["rev"].Value.TrimEnd('.')}";
            if (info.Version is not null)
            {
                info.MoreInfo.Add($"({info.Revision})");
            }
            ClassifyToken(revision.Groups["rest"].Value, info);
            return;
        }

        var innerRevision = InnerRevisionRegex().Match(core);
        if (innerRevision.Success)
        {
            ClassifyToken(core[..innerRevision.Index], info);
            ClassifyToken(core[innerRevision.Index..], info);
            return;
        }

        var version = VersionRegex().Match(core);
        if (version.Success)
        {
            info.Version ??= $"v{version.Groups["ver"].Value}";
            if (info.Revision is not null)
            {
                info.MoreInfo.Add($"({info.Revision})");
            }
            ClassifyToken(version.Groups["rest"].Value, info);
            return;
        }

        var shortDate = ShortDateRegex().Match(core);
        if (shortDate.Success && TrySetShortDate(shortDate, info))
        {
            ClassifyToken(shortDate.Groups["rest"].Value, info);
            return;
        }

        foreach (var (name, code) in Regions)
        {
            if (core.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
                (core.Length == name.Length || core[name.Length] == ' '))
            {
                if (!info.CountryCodes.Contains(code))
                {
                    info.CountryCodes.Add(code);
                }
                ClassifyToken(core[name.Length..], info);
                return;
            }
        }

        if (core.Equals("PAL", StringComparison.OrdinalIgnoreCase) || core.Equals("NTSC", StringComparison.OrdinalIgnoreCase))
        {
            info.Video ??= core.ToUpperInvariant();
            return;
        }

        var words = WordRegex().Matches(core).Select(m => m.Value.ToLowerInvariant()).ToList();
        if (words.Count > 0 &&
            Languages.TryGetValue(words[0], out var languageCode) &&
            words.Skip(1).All(LanguageQualifiers.Contains))
        {
            if (!info.LanguageCodes.Contains(languageCode))
            {
                info.LanguageCodes.Add(languageCode);
            }
            return;
        }

        if (info.IsSoftware && ClassifySoftwareToken(token, core, words, info))
        {
            return;
        }

        if (words.Contains("bootleg"))
        {
            info.IsBootleg = true;
            info.SoftwareMods.Add($"({token})");
        }
        else if (words.Any(w => w is "hack" or "hacked"))
        {
            info.IsHack = true;
            info.SoftwareMods.Add($"({token})");
        }
        else if (words.Any(w => w is "prototype" or "proto" or "beta" or "alpha" or "preview") ||
                 core.StartsWith("location test", StringComparison.OrdinalIgnoreCase))
        {
            info.DevStatus ??= core;
        }
        else if (words.Contains("demo"))
        {
            info.Demo = "demo";
        }
        else if (words.Count > 0 && words[0] is "alt" or "alternate")
        {
            info.IsAlternate = true;
            info.MoreInfo.Add($"({token})");
        }
        else
        {
            info.MoreInfo.Add($"({token})");
        }
    }

    // Words of the software lists: abbreviated languages, translations, pirate, cracked and trained releases. A
    // translation bootleg is a translation.
    private static bool ClassifySoftwareToken(string token, string core, List<string> words, DescriptionInfo info)
    {
        if (SoftwareLanguageAbbreviations.TryGetValue(core, out var languageCode))
        {
            if (!info.LanguageCodes.Contains(languageCode))
            {
                info.LanguageCodes.Add(languageCode);
            }
            return true;
        }

        if (words.Any(w => w is "translation" or "translated"))
        {
            info.IsTranslation = true;
            info.SoftwareMods.Add($"({token})");
        }
        else if (words.Contains("pirate"))
        {
            info.IsBootleg = true;
            info.SoftwareMods.Add($"({token})");
        }
        else if (words.Any(w => w is "crack" or "cracked"))
        {
            info.IsCrack = true;
            info.SoftwareMods.Add($"({token})");
        }
        else if (words.Any(w => w is "trained" or "trainer"))
        {
            info.IsTrainer = true;
            info.SoftwareMods.Add($"({token})");
        }
        else
        {
            return false;
        }

        return true;
    }

    private static bool TrySetShortDate(Match match, DescriptionInfo info) =>
        info.TrySetDate(
            ExpandTwoDigitYear(int.Parse(match.Groups["yy"].ValueSpan, CultureInfo.InvariantCulture)),
            int.Parse(match.Groups["mm"].ValueSpan, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["dd"].ValueSpan, CultureInfo.InvariantCulture));

    // Arcade dates start in the 1970s
    private static int ExpandTwoDigitYear(int yy) => yy >= 70 ? 1900 + yy : 2000 + yy;

    // Year of the DAT in the date format of the other conventions: 1991, or 199x for "199?"
    private static string FormatDatYear(string? year)
    {
        var trimmed = year?.Trim() ?? string.Empty;
        return DatYearRegex().IsMatch(trimmed) ? trimmed.Replace('?', 'x') : string.Empty;
    }

    private sealed class DescriptionInfo
    {
        public string? Date { get; private set; }
        public string? Version { get; set; }
        public string? Revision { get; set; }
        public string? DevStatus { get; set; }
        public string? Demo { get; set; }
        public bool IsAlternate { get; set; }
        public bool IsBootleg { get; set; }
        public bool IsHack { get; set; }
        public bool IsCrack { get; set; }
        public bool IsTrainer { get; set; }
        public bool IsTranslation { get; set; }

        // The words of the software lists are only read in the description of a software
        public bool IsSoftware { get; init; }
        public List<string> CountryCodes { get; } = [];
        public List<string> LanguageCodes { get; } = [];
        public List<string> SoftwareMods { get; } = [];
        public List<string> MoreInfo { get; } = [];

        public string? Video { get; set; }

        // Validates a date and keeps the first one of the description; false when the numbers are not a date
        public bool TrySetDate(int year, int month, int day)
        {
            if (month is < 1 or > 12 || day is < 1 or > 31)
            {
                return false;
            }

            Date ??= string.Create(CultureInfo.InvariantCulture, $"{year:D4}-{month:D2}-{day:D2}");
            return true;
        }
    }
}
