using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Reads the rating files of the MAME community, in the folders layout of progettosnaps (<c>bestgames.ini</c>): one
/// section per range of marks out of 100 (<c>[90 to 100 (Best Games)]</c>), then the machines of that range, one per line.
/// A section that does not start with a mark is not a rating (<c>[FOLDER_SETTINGS]</c>, <c>[ROOT_FOLDER]</c>).
/// </summary>
public static partial class MameRatingFile
{
    [GeneratedRegex(@"^(?<score>\d{1,3})\b", RegexOptions.CultureInvariant)]
    private static partial Regex ScoreRegex();

    /// <summary>Ratings of a file; a missing or unreadable file throws, as any file read.</summary>
    public static MameRatings Read(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Parse(reader);
    }

    public static MameRatings Parse(TextReader reader)
    {
        var byMachine = new Dictionary<string, MameRating>(StringComparer.OrdinalIgnoreCase);
        MameRating? section = null;
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = ParseRange(line[1..^1].Trim());
                continue;
            }

            if (section is not null)
            {
                byMachine.TryAdd(line, section);
            }
        }

        return new MameRatings(byMachine);
    }

    /// <summary>A range of marks (<c>90 to 100 (Best Games)</c>): its lower bound and its name; <c>null</c> for any other section.</summary>
    public static MameRating? ParseRange(string section) =>
        !MameCategoryFile.IsFolderSettings(section) &&
        ScoreRegex().Match(section) is { Success: true } match &&
        int.TryParse(match.Groups["score"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var score) &&
        score <= 100
            ? new MameRating(score, section)
            : null;
}
