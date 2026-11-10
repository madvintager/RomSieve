using System.Text.RegularExpressions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Labels of the disks of a No-Intro / Redump set. Redump writes the label of a disc in a parenthesis of its own right
/// after its number, <c>Command &amp; Conquer (USA) (Disc 1) (GDI Disc)</c> / <c>(Disc 2) (NOD Disc)</c>; the label went into
/// the release key, which split the set into two releases of one disc each. The same place may also hold the mark of an
/// edition, <c>(Disc 1) (Premium Box)</c> / <c>(Disc 2) (Premium Box)</c>, that must keep its releases apart. The
/// parenthesis that follows the disc number (<see cref="ParsedRomName.DiscLabelCandidate"/>) is therefore the label of
/// that disc (<see cref="ParsedRomName.DiscLabel"/>, left out of the release key) only when, among the disks of the same
/// game (same name once the disc number and every unrecognized parenthesis are removed):
/// <list type="bullet">
/// <item>no disc of another number carries it (an edition mark is carried by all the disks of the edition);</item>
/// <item>without it, the disc would not look like another dump of the same number (<c>(Disc 1) (EDC)</c> next to
/// <c>(Disc 1)</c>).</item>
/// </list>
/// The disks compared are those of the DAT when there is one, so that the decision does not depend on what the source
/// holds, and those of the source.
/// </summary>
public static partial class DiscLabelResolver
{
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    // Appended to a DAT game name before parsing: the name has no extension, and a dot in its title ("Vol. 4") would
    // otherwise be read as the start of one
    private const string DatGameExtension = ".bin";

    private sealed record Disc(string Name, int Number, string? Candidate, IReadOnlyList<string> MoreInfo);

    /// <summary>
    /// Sets <see cref="ParsedRomName.DiscLabel"/> on the disks of <paramref name="items"/> (all of one DAT), and clears
    /// it where it no longer applies, so that a recalculation gives the same result.
    /// </summary>
    public static void Apply(IList<ProcessItem> items, DatCatalog? datCatalog)
    {
        var eligible = items.Where(it => IsDisc(it.Parsed)).ToList();
        if (eligible.Count == 0)
        {
            return;
        }

        var labels = eligible.Any(it => it.Parsed.DiscLabelCandidate is not null)
            ? FindLabels(eligible, datCatalog)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in eligible)
        {
            var label = labels.GetValueOrDefault(NameOf(item.Parsed));
            if (!string.Equals(item.Parsed.DiscLabel, label, StringComparison.Ordinal))
            {
                item.Parsed = item.Parsed with { DiscLabel = label };
            }
        }
    }

    private static bool IsDisc(ParsedRomName parsed) =>
        parsed.Convention is NamingConvention.NoIntro or NamingConvention.Redump &&
        parsed.DiscNumber.HasValue &&
        !parsed.TrackNumber.HasValue;

    private static string NameOf(ParsedRomName parsed) =>
        !string.IsNullOrEmpty(parsed.Extension) && parsed.RawFilename.EndsWith(parsed.Extension, StringComparison.OrdinalIgnoreCase)
            ? parsed.RawFilename[..^parsed.Extension.Length]
            : parsed.RawFilename;

    private static Dictionary<string, string> FindLabels(List<ProcessItem> eligible, DatCatalog? datCatalog)
    {
        // One disc per name: a file of the source and the DAT game it matches are the same disc
        var discs = new Dictionary<string, (string GroupKey, Disc Disc)>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, ParsedRomName parsed)
        {
            if (!discs.ContainsKey(name))
            {
                discs[name] = (GroupKeyOf(name, parsed), new Disc(name, parsed.DiscNumber!.Value, parsed.DiscLabelCandidate, parsed.MoreInfo));
            }
        }

        if (datCatalog is not null)
        {
            var isRedump = eligible.Any(it => it.Parsed.Convention == NamingConvention.Redump);
            foreach (var game in datCatalog.Games)
            {
                if (game.Name.Contains("(Dis", StringComparison.OrdinalIgnoreCase))
                {
                    var parsed = NoIntroRedumpParser.ParseNoIntroFilenameStatic(game.Name + DatGameExtension, isRedump: isRedump);
                    if (parsed.DiscNumber.HasValue && !parsed.TrackNumber.HasValue)
                    {
                        Add(game.Name, parsed);
                    }
                }
            }
        }

        foreach (var item in eligible)
        {
            Add(NameOf(item.Parsed), item.Parsed);
        }

        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in discs.Values.GroupBy(d => d.GroupKey, StringComparer.Ordinal))
        {
            var groupDiscs = group.Select(d => d.Disc).ToList();
            foreach (var disc in groupDiscs)
            {
                if (disc.Candidate is { } candidate && IsLabel(disc, candidate, groupDiscs))
                {
                    labels[disc.Name] = candidate;
                }
            }
        }
        return labels;
    }

    private static bool IsLabel(Disc disc, string candidate, IReadOnlyList<Disc> groupDiscs)
    {
        // An edition mark is carried by the other disks of the edition
        if (groupDiscs.Any(other => other.Number != disc.Number && other.MoreInfo.Contains(candidate, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        // Without it, the disc must not look like another dump of the same number, with or without its own candidate
        var rest = Without(disc.MoreInfo, candidate);
        return !groupDiscs.Any(other =>
            other.Number == disc.Number &&
            !ReferenceEquals(other, disc) &&
            (rest.SetEquals(Without(other.MoreInfo, null)) ||
             (other.Candidate is not null && rest.SetEquals(Without(other.MoreInfo, other.Candidate)))));
    }

    private static HashSet<string> Without(IReadOnlyList<string> moreInfo, string? removed)
    {
        var set = new HashSet<string>(moreInfo, StringComparer.OrdinalIgnoreCase);
        if (removed is not null)
        {
            set.Remove(removed);
        }
        return set;
    }

    /// <summary>
    /// The name of a disc without its number and without any unrecognized parenthesis: the disks of one game and of all
    /// its editions share it, while the region, the languages, the revision or a development status still tell
    /// them apart.
    /// </summary>
    private static string GroupKeyOf(string name, ParsedRomName parsed)
    {
        var text = RemoveFirst(name, $"({parsed.MediaLabel})");
        foreach (var info in parsed.MoreInfo)
        {
            text = RemoveFirst(text, info);
        }
        return WhitespaceRegex().Replace(text, " ").Trim().ToLowerInvariant();
    }

    private static string RemoveFirst(string text, string part)
    {
        var index = text.IndexOf(part, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? text : text.Remove(index, part.Length);
    }
}
