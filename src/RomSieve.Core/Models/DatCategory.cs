using System.Text.RegularExpressions;

namespace RomSieve.Core.Models;

/// <summary>
/// Categories of the No-Intro and Redump DATs (<c>&lt;category&gt;</c>, <see cref="DatGame.Categories"/>), and what a
/// name tells of them when the DAT gives none.
/// </summary>
public static partial class DatCategory
{
    /// <summary>Category of the hardware add-ons: SNES enhancement chips, SuFami Turbo, Redump add-on discs.</summary>
    public const string AddOns = "Add-Ons";

    /// <summary>Tag of the No-Intro name of an enhancement chip dump (<c>CX4 (World) (Enhancement Chip)</c>).</summary>
    public const string EnhancementChipTag = "(Enhancement Chip)";

    /// <summary>Prefix of the No-Intro and Redump names of a BIOS (<c>[BIOS] Super NES CD-ROM - Boot ROM (Japan)</c>).</summary>
    public const string BiosPrefix = "[BIOS]";

    /// <summary>
    /// Categories the No-Intro and Redump DATs use, games first: offered to exclude before any DAT is read, and for a lot,
    /// whose DATs are only read by their headers.
    /// </summary>
    public static IReadOnlyList<string> Known { get; } =
    [
        "Games", "Add-Ons", "Applications", "Audio", "Bonus Discs", "Coverdiscs", "Demos", "Educational",
        "Miscellaneous", "Multimedia", "Preproduction", "Video"
    ];

    [GeneratedRegex(@"\(\s*Enhancement\s+Chip\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnhancementChipRegex();

    /// <summary>
    /// Categories of a release: those the DAT gives its game, otherwise those its name tells (an enhancement chip is an
    /// add-on), with the tag that tells it; a category of the DAT always wins over the name.
    /// </summary>
    public static (IReadOnlyList<string> Categories, string? Tag) Of(ParsedRomName parsed) =>
        parsed.DatCategories.Count > 0 ? (parsed.DatCategories, null) : InferFromName(parsed);

    /// <summary>What the name of a release tells of its category, for a release the DAT gives none: an enhancement chip.</summary>
    public static (IReadOnlyList<string> Categories, string? Tag) InferFromName(ParsedRomName parsed) =>
        EnhancementChipRegex().IsMatch(parsed.RawFilename) ? ([AddOns], EnhancementChipTag) : ([], null);

    /// <summary>Number of games of each category of a DAT (a game of several categories counts in each), any case.</summary>
    public static IReadOnlyDictionary<string, int> CountGames(DatCatalog catalog)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in catalog.Games)
        {
            foreach (var category in game.Categories.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                counts[category] = counts.GetValueOrDefault(category) + 1;
            }
        }

        return counts;
    }

    /// <summary>
    /// Whether a release is a BIOS by its name (<c>[BIOS]</c> before the title), whatever the category of its DAT: the DATs
    /// file the BIOS under several categories, even <c>Games</c> for a Master System BIOS that holds a game.
    /// </summary>
    public static bool IsBios(ParsedRomName parsed) =>
        parsed.Title.StartsWith(BiosPrefix, StringComparison.OrdinalIgnoreCase) ||
        parsed.RawFilename.StartsWith(BiosPrefix, StringComparison.OrdinalIgnoreCase);
}
