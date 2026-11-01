namespace RomSieve.Core.Models;

/// <summary>
/// Rating of a MAME machine, from a rating file of the community (<c>bestgames.ini</c>): the lower bound of its range of
/// marks out of 100 (<c>90</c> for <c>90 to 100 (Best Games)</c>) and the name of that range.
/// <see cref="Unrated"/> stands for a machine the file does not rate.
/// </summary>
public sealed record MameRating(int Score, string Name)
{
    /// <summary>A machine that a rating file does not rate: unlike no rating at all, it means a file was read.</summary>
    public static readonly MameRating Unrated = new(-1, string.Empty);

    public bool IsUnrated => Score < 0;
}

/// <summary>
/// Ratings of the MAME machines read from a rating file, by short name. A clone the file does not rate takes the
/// rating of its parent.
/// </summary>
public sealed class MameRatings
{
    public MameRatings(IReadOnlyDictionary<string, MameRating> byMachine)
    {
        ByMachine = byMachine;
    }

    public IReadOnlyDictionary<string, MameRating> ByMachine { get; }

    public int Count => ByMachine.Count;

    /// <summary>Rating of a machine, of its parent when the file does not rate it, otherwise <see cref="MameRating.Unrated"/>.</summary>
    public MameRating Find(string machine, string? parent = null) =>
        ByMachine.TryGetValue(machine, out var rating)
            ? rating
            : parent is not null && ByMachine.TryGetValue(parent, out var parentRating) ? parentRating : MameRating.Unrated;
}
