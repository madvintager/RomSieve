using System.Text;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Reads the category files of the MAME community, in their two layouts:
/// <list type="bullet">
/// <item><c>catver.ini</c>: a <c>[Category]</c> section of <c>machine=Genre / Subgenre</c> lines (the other sections,
/// such as <c>[VerAdded]</c>, are ignored);</item>
/// <item>the folders of progettosnaps (<c>catlist.ini</c>, <c>genre.ini</c>): one section per category, then the
/// machines of that category, one per line (<c>[FOLDER_SETTINGS]</c> and <c>[ROOT_FOLDER]</c> are not categories).</item>
/// </list>
/// A category may start with its kind (<c>Arcade: Shooter / Flying Vertical</c>) and end with <c>* Mature *</c>; in a
/// file that marks the arcade machines, a category without kind is not arcade.
/// </summary>
public static class MameCategoryFile
{
    private const string MatureMark = "* Mature *";
    private const string ArcadeKind = "Arcade";

    // Genre and subgenre are separated by a slash between spaces: a bare slash belongs to a name (Game Console/Computer)
    private const string GenreSeparator = " / ";

    /// <summary>Categories of a file; a missing or unreadable file throws, as any file read.</summary>
    public static MameCategories Read(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Parse(reader);
    }

    public static MameCategories Parse(TextReader reader)
    {
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line.Trim());
        }

        var isCatver = lines.Any(line => string.Equals(line, "[Category]", StringComparison.OrdinalIgnoreCase));
        var byMachine = new Dictionary<string, MameCategory>(StringComparer.OrdinalIgnoreCase);
        string? section = null;
        foreach (var line in lines)
        {
            if (line.Length == 0 || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            if (isCatver)
            {
                var separator = line.IndexOf('=');
                if (string.Equals(section, "Category", StringComparison.OrdinalIgnoreCase) && separator > 0 &&
                    ParseCategory(line[(separator + 1)..]) is { } category)
                {
                    byMachine.TryAdd(line[..separator].Trim(), category);
                }
            }
            else if (section is not null && !IsFolderSettings(section) && ParseCategory(section) is { } category)
            {
                byMachine.TryAdd(line, category);
            }
        }

        // A file that marks the arcade machines (catlist.ini) says that the others are not
        if (byMachine.Values.Any(c => c.IsArcade == true))
        {
            foreach (var (machine, category) in byMachine.Where(kv => kv.Value.IsArcade is null).ToList())
            {
                byMachine[machine] = category with { IsArcade = false };
            }
        }

        return new MameCategories(byMachine);
    }

    /// <summary>
    /// A category as a file writes it: its kind before a colon (<c>Arcade:</c>), its genre and subgenre separated by a
    /// slash, and <c>* Mature *</c> for a machine for adults. <c>null</c> when nothing is left.
    /// </summary>
    public static MameCategory? ParseCategory(string text)
    {
        var value = text.Trim();
        var isMature = value.Contains(MatureMark, StringComparison.OrdinalIgnoreCase);
        if (isMature)
        {
            value = value.Replace(MatureMark, string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        }

        bool? isArcade = null;
        var colon = value.IndexOf(':');
        var slash = value.IndexOf(GenreSeparator, StringComparison.Ordinal);
        if (colon > 0 && (slash < 0 || colon < slash))
        {
            isArcade = string.Equals(value[..colon].Trim(), ArcadeKind, StringComparison.OrdinalIgnoreCase);
            value = value[(colon + 1)..].Trim();
            slash = value.IndexOf(GenreSeparator, StringComparison.Ordinal);
        }

        var genre = (slash < 0 ? value : value[..slash]).Trim();
        var subgenre = slash < 0 ? null : value[(slash + GenreSeparator.Length)..].Trim();
        return genre.Length == 0
            ? null
            : new MameCategory(genre, string.IsNullOrEmpty(subgenre) ? null : subgenre, isMature, isArcade);
    }

    internal static bool IsFolderSettings(string section) =>
        string.Equals(section, "FOLDER_SETTINGS", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(section, "ROOT_FOLDER", StringComparison.OrdinalIgnoreCase);
}
