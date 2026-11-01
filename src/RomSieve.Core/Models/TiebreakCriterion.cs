namespace RomSieve.Core.Models;

/// <summary>
/// Criteria of the 1G1R tiebreaker cascade (<c>OneGameOneRomResolver.CompareReleases</c>). Each convention has its own
/// order, made of the criteria that can separate two of its releases (<see cref="TiebreakOrder.CriteriaOf"/>); the
/// final tiebreak on the name (<see cref="Name"/>) always comes last and is never part of an order.
/// </summary>
public enum TiebreakCriterion
{
    /// <summary>No criterion separates the two releases.</summary>
    None = 0,

    /// <summary>Rank of the language among the priority languages.</summary>
    LanguageRank,

    /// <summary>Complete set: all the disks of a multi-disk game, or all the ROMs and disks of a MAME set.</summary>
    CompleteMultidiskSet,

    /// <summary>
    /// Chipset of an Amiga release for the AGA machines (<see cref="FilterConfig.AmigaTarget"/>): AGA, then OCS / ECS and
    /// AGA, then fixed for AGA, then OCS / ECS (no effect with another target).
    /// </summary>
    AmigaChipset,

    /// <summary>A good dump, verified first: no bad, over- or underdump, no virus.</summary>
    GoodDump,

    /// <summary>The original release: no crack, trainer, hack, bootleg, fix, translation, development version or re-release.</summary>
    OriginalRelease,

    /// <summary>Emulation status of a MAME machine: good, then imperfect, then preliminary (no effect on other files).</summary>
    DriverStatus,

    /// <summary>Rank of the country among the priority countries.</summary>
    CountryRank,
    VideoStandard,
    PreferredMediaType,
    MediaFidelity,
    LanguageCount,

    /// <summary>A country of the release that speaks its priority language, or no country at all.</summary>
    RegionAffinity,
    Version,
    Revision,
    Date,

    /// <summary>Most disks found, between two sets that are not complete.</summary>
    Completeness,
    ParentClone,

    /// <summary>Final tiebreak: the shortest name, then the ordinal order of the names.</summary>
    Name
}
