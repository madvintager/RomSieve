namespace RomSieve.Core.Models;

/// <summary>
/// Category of a MAME machine, from a category file of the community (<c>catver.ini</c>, <c>catlist.ini</c>,
/// <c>genre.ini</c>): its genre (<c>Shooter</c>), its subgenre (<c>Flying Vertical</c>), whether it is for adults
/// (<c>* Mature *</c>), and whether it is an arcade machine, unknown when the file does not mark them
/// (<c>Arcade:</c> in <c>catlist.ini</c>).
/// </summary>
public sealed record MameCategory(string Genre, string? Subgenre, bool IsMature = false, bool? IsArcade = null)
{
    /// <summary>Genre and subgenre, as the file writes them: <c>Shooter / Flying Vertical</c>.</summary>
    public string Name => Subgenre is null ? Genre : $"{Genre} / {Subgenre}";
}

/// <summary>
/// Categories of the MAME machines read from a category file, by short name. A clone the file does not list takes the
/// category of its parent.
/// </summary>
public sealed class MameCategories
{
    public static readonly MameCategories Empty = new(new Dictionary<string, MameCategory>(StringComparer.OrdinalIgnoreCase));

    public MameCategories(IReadOnlyDictionary<string, MameCategory> byMachine)
    {
        ByMachine = byMachine;
        Genres = byMachine.Values
            .Select(c => c.Genre)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        MarksArcade = byMachine.Values.Any(c => c.IsArcade == true);
    }

    public IReadOnlyDictionary<string, MameCategory> ByMachine { get; }

    /// <summary>Genres of the file, in alphabetical order.</summary>
    public IReadOnlyList<string> Genres { get; }

    /// <summary>Whether the file tells the arcade machines from the others.</summary>
    public bool MarksArcade { get; }

    public int Count => ByMachine.Count;

    /// <summary>Category of a machine, or of its parent when the file does not list it.</summary>
    public MameCategory? Find(string machine, string? parent = null) =>
        ByMachine.TryGetValue(machine, out var category)
            ? category
            : parent is not null && ByMachine.TryGetValue(parent, out var parentCategory) ? parentCategory : null;
}
