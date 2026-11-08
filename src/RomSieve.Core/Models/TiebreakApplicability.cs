using System.Text.RegularExpressions;
using RomSieve.Core.Services;

namespace RomSieve.Core.Models;

/// <summary>What the chosen DAT says about parent / clone relations, as far as it is known.</summary>
public enum DatCloneRelations
{
    /// <summary>Not known yet (DAT being read, or unreadable): nothing is claimed.</summary>
    Unknown = 0,

    /// <summary>No DAT is chosen: no file is known as a clone.</summary>
    NoDat,

    /// <summary>The DAT describes no parent / clone relation (a TOSEC DAT, a SabreTools DAT).</summary>
    None,

    /// <summary>The DAT has clones (<c>cloneof</c>, or a <c>cloneofid</c> resolved to its parent).</summary>
    Present
}

/// <summary>
/// What the names of the files of a DAT tell about the criteria read from a name (<see cref="TiebreakApplicability.GetDatNameTraits"/>):
/// with a DAT, the 1G1R compares the names the DAT gives the files it identified, never the names they have in the source.
/// </summary>
/// <param name="HasAmigaChipsetMarks">
/// Whether a name may mark an Amiga chipset or a fix for the AGA machines; <see langword="false"/> only when none does.
/// </param>
/// <param name="HasOneMediaFormat">
/// Whether all the names give the same media and the same format fidelity; <see langword="false"/> when they may not.
/// </param>
public sealed record DatNameTraits(bool HasAmigaChipsetMarks, bool HasOneMediaFormat);

/// <summary>
/// Criteria of an order of the 1G1R cascade that cannot separate two releases, given the DAT and the settings: the
/// order of a convention only lists the criteria that can separate two of its releases
/// (<see cref="TiebreakOrder.CriteriaOf"/>), but some still need a DAT with clones or with names that differ, an AGA
/// Amiga, or a preference. Only certain cases are listed, so that a criterion said to have no effect really has none.
/// </summary>
public static partial class TiebreakApplicability
{
    // Every word of a name from which AmigaChipsets reads a chipset other than OCS / ECS, or a fix for AGA: a superset,
    // so that a DAT without any of them certainly gives the same rank to all its names
    [GeneratedRegex(@"\b(?:AGA|A1200|A4000T?|CD32)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AmigaChipsetWordRegex();

    /// <summary>
    /// Why <paramref name="criterion"/> of the order of <paramref name="convention"/> has no effect on a collection
    /// sorted with a DAT whose relations are <paramref name="cloneRelations"/>, or <c>null</c> when it may separate two
    /// releases. <paramref name="amigaTarget"/> is the Amiga the collection is sorted for, and
    /// <paramref name="preferredVideo"/> and <paramref name="preferredMediaType"/> the preferences of the criteria that
    /// compare the releases to them; <see langword="null"/> when not known, nothing being then claimed.
    /// <paramref name="nameTraits"/> is what the names of the files of the DAT tell, <see langword="null"/> when not
    /// known (no DAT, a DAT being read, a lot of DATs, or files that may keep another name, CHDs matched by their name).
    /// </summary>
    public static LocalizedMessage? GetNoEffectReason(
        TiebreakCriterion criterion,
        NamingConvention convention,
        DatCloneRelations cloneRelations,
        AmigaTarget? amigaTarget = null,
        VideoPreference? preferredVideo = null,
        MediaPreference? preferredMediaType = null,
        DatNameTraits? nameTraits = null) => criterion switch
        {
            _ when !TiebreakOrder.CriteriaOf(convention).Contains(criterion) =>
                LocalizedMessage.Create("tiebreak.no_effect_convention"),
            TiebreakCriterion.AmigaChipset when amigaTarget is { } target && target != AmigaTarget.Aga =>
                LocalizedMessage.Create("tiebreak.no_effect_amiga_target"),
            TiebreakCriterion.AmigaChipset when nameTraits is { HasAmigaChipsetMarks: false } =>
                LocalizedMessage.Create("tiebreak.no_effect_no_chipset_in_dat"),
            TiebreakCriterion.VideoStandard when preferredVideo == VideoPreference.None =>
                LocalizedMessage.Create("tiebreak.no_effect_no_preference"),
            TiebreakCriterion.PreferredMediaType when preferredMediaType == MediaPreference.None =>
                LocalizedMessage.Create("tiebreak.no_effect_no_preference"),
            TiebreakCriterion.PreferredMediaType or TiebreakCriterion.MediaFidelity when nameTraits is { HasOneMediaFormat: true } =>
                LocalizedMessage.Create("tiebreak.no_effect_one_format_in_dat"),
            TiebreakCriterion.ParentClone when cloneRelations == DatCloneRelations.NoDat =>
                LocalizedMessage.Create("tiebreak.no_effect_no_dat"),
            TiebreakCriterion.ParentClone when cloneRelations == DatCloneRelations.None =>
                LocalizedMessage.Create("tiebreak.no_effect_no_clones"),
            _ => null
        };

    /// <summary>
    /// Parent / clone relations of a whole DAT catalog: of the DAT, of one of its software lists, or of one of the DATs of
    /// a lot.
    /// </summary>
    public static DatCloneRelations GetCloneRelations(DatCatalog catalog) =>
        catalog.CloneToParent.Count > 0
        || catalog.SoftwareLists?.Values.Any(list => list.CloneToParent.Count > 0) == true
        || catalog.Dats?.Any(dat => dat.Summary.HasCloneRelations) == true
            ? DatCloneRelations.Present
            : DatCloneRelations.None;

    /// <summary>
    /// What the names of the files of a whole DAT catalog tell (<see cref="DatNameTraits"/>), read as the 1G1R reads the
    /// name of an identified file: the name of its DAT entry, a disk named without extension being a <c>.chd</c>. A
    /// catalog without entries (a lot, whose DATs are read apart) claims nothing.
    /// </summary>
    public static DatNameTraits GetDatNameTraits(DatCatalog catalog)
    {
        var hasChipsetMarks = catalog.Entries.Count == 0;
        (string Category, int Fidelity)? format = null;
        var hasOneFormat = catalog.Entries.Count > 0;
        foreach (var entry in catalog.Entries)
        {
            var name = entry.IsDisk && !Path.HasExtension(entry.Name) ? entry.Name + ".chd" : entry.Name;
            hasChipsetMarks = hasChipsetMarks || AmigaChipsetWordRegex().IsMatch(name);
            if (hasOneFormat)
            {
                var entryFormat = (ReleaseScoringService.MediaCategoryOf(name), ReleaseScoringService.MediaFidelityOf(name));
                format ??= entryFormat;
                hasOneFormat = format == entryFormat;
            }

            if (hasChipsetMarks && !hasOneFormat)
            {
                break;
            }
        }

        return new DatNameTraits(hasChipsetMarks, hasOneFormat);
    }
}
