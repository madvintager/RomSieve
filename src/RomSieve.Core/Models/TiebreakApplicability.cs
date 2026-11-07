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
/// Criteria of an order of the 1G1R cascade that cannot separate two releases, given the DAT and the settings: the
/// order of a convention only lists the criteria that can separate two of its releases
/// (<see cref="TiebreakOrder.CriteriaOf"/>), but some still need a DAT with clones, an AGA Amiga, or a preference.
/// Only certain cases are listed, so that a criterion said to have no effect really has none.
/// </summary>
public static class TiebreakApplicability
{
    /// <summary>
    /// Why <paramref name="criterion"/> of the order of <paramref name="convention"/> has no effect on a collection
    /// sorted with a DAT whose relations are <paramref name="cloneRelations"/>, or <c>null</c> when it may separate two
    /// releases. <paramref name="amigaTarget"/> is the Amiga the collection is sorted for, and
    /// <paramref name="preferredVideo"/> and <paramref name="preferredMediaType"/> the preferences of the criteria that
    /// compare the releases to them; <see langword="null"/> when not known, nothing being then claimed.
    /// </summary>
    public static LocalizedMessage? GetNoEffectReason(
        TiebreakCriterion criterion,
        NamingConvention convention,
        DatCloneRelations cloneRelations,
        AmigaTarget? amigaTarget = null,
        VideoPreference? preferredVideo = null,
        MediaPreference? preferredMediaType = null) => criterion switch
        {
            _ when !TiebreakOrder.CriteriaOf(convention).Contains(criterion) =>
                LocalizedMessage.Create("tiebreak.no_effect_convention"),
            TiebreakCriterion.AmigaChipset when amigaTarget is { } target && target != AmigaTarget.Aga =>
                LocalizedMessage.Create("tiebreak.no_effect_amiga_target"),
            TiebreakCriterion.VideoStandard when preferredVideo == VideoPreference.None =>
                LocalizedMessage.Create("tiebreak.no_effect_no_preference"),
            TiebreakCriterion.PreferredMediaType when preferredMediaType == MediaPreference.None =>
                LocalizedMessage.Create("tiebreak.no_effect_no_preference"),
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
        || catalog.Dats?.Any(dat => dat.Catalog.CloneToParent.Count > 0) == true
            ? DatCloneRelations.Present
            : DatCloneRelations.None;
}
