using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// What a release holds of its set, by <see cref="EditionSides.Count"/>.
/// </summary>
/// <param name="Found">
/// The disks the release holds whole (a numbered disk with each side its edition names, or a dump of the whole disk), or,
/// for a single disk whose sides are named alone, its sides.
/// </param>
/// <param name="Held">The numbered disks of which the release holds at least one side (its sides, for a single disk).</param>
/// <param name="MissingPart">
/// The first disk the release holds without one of its sides (<see cref="EditionSides.SingleDisk"/> for a single disk);
/// <c>null</c> when none.
/// </param>
/// <param name="MissingSide">The first side that disk lacks; <c>null</c> when none.</param>
public readonly record struct SetMedia(int Found, int Held, int? MissingPart, string? MissingSide);

/// <summary>
/// The sides each disk of a TOSEC edition has (same <see cref="Abstractions.IGameKeyService.GetEditionKey"/>), read from
/// the names of its files in the source, whatever their status, and in the DAT: a cracked dump or a bad dump of a side
/// says that the side exists as well as a clean one. A disk of a release counts as found only with each of them, as a set
/// counts as complete only with each of its disks; a dump without a side stands for the whole disk.
/// </summary>
public sealed class EditionSides
{
    /// <summary>
    /// The disk of a game on a single disk whose sides are named alone (<c>Side B</c>): its sides are its set. A number
    /// that no disk has, numbered sets starting at 0 or 1 (<c>Disk 0 of 6</c>).
    /// </summary>
    public const int SingleDisk = -1;

    private readonly Dictionary<string, Dictionary<int, SortedSet<string>>> _byEdition = new(StringComparer.Ordinal);

    /// <summary>The sides of each disk of an edition; <c>null</c> when no file of the edition names a side.</summary>
    public IReadOnlyDictionary<int, SortedSet<string>>? Of(string editionKey) => _byEdition.GetValueOrDefault(editionKey);

    internal bool Contains(string editionKey) => _byEdition.ContainsKey(editionKey);

    internal void Add(string editionKey, int part, string side)
    {
        if (!_byEdition.TryGetValue(editionKey, out var parts))
        {
            parts = [];
            _byEdition[editionKey] = parts;
        }

        if (!parts.TryGetValue(part, out var sides))
        {
            sides = new SortedSet<string>(StringComparer.Ordinal);
            parts[part] = sides;
        }

        sides.Add(side);
    }

    /// <summary>
    /// The number of sides a single disk of an edition has (<see cref="SingleDisk"/>), when its names give at least two: the size of
    /// the set of a release of that disk. 0 otherwise.
    /// </summary>
    public static int SingleDiskSides(IReadOnlyDictionary<int, SortedSet<string>>? sides) =>
        sides is not null && sides.TryGetValue(SingleDisk, out var single) && single.Count > 1 ? single.Count : 0;

    /// <summary>
    /// The disk a medium belongs to: its number in a set of several disks, <see cref="SingleDisk"/> for a single disk (a side named alone,
    /// <c>Side B</c>, or <c>Disk 1 of 1</c>), <c>null</c> for a numbered medium without a total (<c>Tape 2 Side B</c>),
    /// whose set is not known.
    /// </summary>
    public static int? PartOf(ParsedRomName parsed)
    {
        var (number, total) = MultiDiskEvaluator.ExtractDiskPartInfo(parsed);
        if (number.HasValue)
        {
            return total is > 1 ? number.Value : SingleDisk;
        }

        return parsed.DiscNumber.HasValue ? null : SingleDisk;
    }

    /// <summary>
    /// What <paramref name="items"/> (a release, with the disks it borrows) hold of their set, against the sides of
    /// their edition (<paramref name="knownSides"/>, <c>null</c> when no name of the edition gives a side: every disk
    /// held is then whole). A disk of documentation is no side of the game.
    /// </summary>
    public static SetMedia Count(IEnumerable<ProcessItem> items, IReadOnlyDictionary<int, SortedSet<string>>? knownSides)
    {
        var held = new SortedDictionary<int, (bool Whole, SortedSet<string> Sides)>();
        foreach (var item in items)
        {
            if (MultiDiskEvaluator.HoldsDocumentation(item.Parsed) || PartOf(item.Parsed) is not { } part)
            {
                continue;
            }

            if (!held.TryGetValue(part, out var disk))
            {
                disk = (false, new SortedSet<string>(StringComparer.Ordinal));
            }

            var side = MultiDiskEvaluator.ExtractSide(item.Parsed);
            if (side is null)
            {
                disk.Whole = true;
            }
            else
            {
                disk.Sides.Add(side);
            }

            held[part] = disk;
        }

        if (held.Keys.Any(part => part != SingleDisk))
        {
            var found = 0;
            int? missingPart = null;
            string? missingSide = null;
            foreach (var (part, disk) in held)
            {
                if (part == SingleDisk)
                {
                    continue;
                }

                var missing = disk.Whole || knownSides is null || !knownSides.TryGetValue(part, out var sides)
                    ? null
                    : sides.FirstOrDefault(side => !disk.Sides.Contains(side));
                if (missing is null)
                {
                    found++;
                }
                else if (missingPart is null)
                {
                    missingPart = part;
                    missingSide = missing;
                }
            }

            return new SetMedia(found, held.Keys.Count(part => part != SingleDisk), missingPart, missingSide);
        }

        // A single disk: its sides are the set
        if (!held.TryGetValue(SingleDisk, out var single))
        {
            return new SetMedia(0, 0, null, null);
        }

        var singleSides = SingleDiskSides(knownSides) > 0 ? knownSides?.GetValueOrDefault(SingleDisk) : null;
        if (single.Whole || singleSides is null)
        {
            return new SetMedia(Math.Max(singleSides?.Count ?? 0, single.Sides.Count), single.Sides.Count, null, null);
        }

        var lacking = singleSides.FirstOrDefault(side => !single.Sides.Contains(side));
        return new SetMedia(single.Sides.Count, single.Sides.Count, lacking is null ? null : SingleDisk, lacking);
    }
}
