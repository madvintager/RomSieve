namespace RomSieve.Core.Models;

/// <summary>
/// Orders of the 1G1R criteria, one per convention (<see cref="FilterConfig.GetTiebreakOrder"/>): the criteria that can
/// separate two releases of the convention (<see cref="CriteriaOf"/>), their default orders in the two region modes,
/// the repair of a stored order, and its consistency with <see cref="RegionPriorityMode"/>. A release whose convention
/// is not known follows the order of TOSEC, the convention of a name the parsers did not recognize. The final ordinal
/// tiebreak on the name (<see cref="TiebreakCriterion.Name"/>) is never part of an order: it always comes last.
/// </summary>
public static class TiebreakOrder
{
    // The language comes first, the country after what tells a good release from a worse one; the criteria that cannot
    // separate two releases of a convention are left out
    private static readonly TiebreakCriterion[] TosecOrder =
    [
        TiebreakCriterion.LanguageRank,
        TiebreakCriterion.CompleteMultidiskSet,
        TiebreakCriterion.AmigaChipset,
        TiebreakCriterion.GoodDump,
        TiebreakCriterion.OriginalRelease,
        TiebreakCriterion.CountryRank,
        TiebreakCriterion.VideoStandard,
        TiebreakCriterion.PreferredMediaType,
        TiebreakCriterion.MediaFidelity,
        TiebreakCriterion.LanguageCount,
        TiebreakCriterion.RegionAffinity,
        TiebreakCriterion.Version,
        TiebreakCriterion.Revision,
        TiebreakCriterion.Date,
        TiebreakCriterion.Completeness
    ];

    // Cartridges, but also the floppy disks of the No-Intro floppy DATs ("Acorn - Archimedes": "(Disk 1)", ".adf" or
    // ".jfd"): a set to complete and a format to prefer. The dates of the development versions ("(Proto) (1999-10-07)")
    // separate two builds of a game
    private static readonly TiebreakCriterion[] NoIntroOrder =
    [
        TiebreakCriterion.LanguageRank,
        TiebreakCriterion.CompleteMultidiskSet,
        TiebreakCriterion.GoodDump,
        TiebreakCriterion.OriginalRelease,
        TiebreakCriterion.CountryRank,
        TiebreakCriterion.VideoStandard,
        TiebreakCriterion.MediaFidelity,
        TiebreakCriterion.LanguageCount,
        TiebreakCriterion.RegionAffinity,
        TiebreakCriterion.Version,
        TiebreakCriterion.Revision,
        TiebreakCriterion.Date,
        TiebreakCriterion.Completeness,
        TiebreakCriterion.ParentClone
    ];

    // A disc game can have several discs, and its images several formats; a Redump DAT only lists verified dumps, so
    // that its names mark no bad dump. Its development versions are dated, as in No-Intro
    private static readonly TiebreakCriterion[] RedumpOrder =
    [
        TiebreakCriterion.LanguageRank,
        TiebreakCriterion.CompleteMultidiskSet,
        TiebreakCriterion.OriginalRelease,
        TiebreakCriterion.CountryRank,
        TiebreakCriterion.VideoStandard,
        TiebreakCriterion.MediaFidelity,
        TiebreakCriterion.LanguageCount,
        TiebreakCriterion.RegionAffinity,
        TiebreakCriterion.Version,
        TiebreakCriterion.Revision,
        TiebreakCriterion.Date,
        TiebreakCriterion.Completeness,
        TiebreakCriterion.ParentClone
    ];

    // MAME sets: the set the MAME developers chose as the parent wins, unless it is incomplete, a bootleg or a hack, or
    // less well emulated than a clone; the region only separates clones without their parent. A board is neither PAL
    // nor NTSC, a set is an archive of ROMs, and a ROM marked "baddump" is the best dump known
    private static readonly TiebreakCriterion[] MameOrder =
    [
        TiebreakCriterion.CompleteMultidiskSet,
        TiebreakCriterion.OriginalRelease,
        TiebreakCriterion.DriverStatus,
        TiebreakCriterion.ParentClone,
        TiebreakCriterion.LanguageRank,
        TiebreakCriterion.CountryRank,
        TiebreakCriterion.Version,
        TiebreakCriterion.Revision,
        TiebreakCriterion.Date,
        TiebreakCriterion.RegionAffinity
    ];

    // Software of the MAME lists: cartridges, disks and tapes of the consoles and home computers, released by region
    // like the No-Intro dumps (the video standard follows the countries), as sets of ROMs like the MAME machines (a set
    // may be incomplete, a ROM marked "baddump" is the best dump known) and with the support of MAME given by the hash
    // files of MAME; the parent chosen by MAME only separates what nothing else does, as with No-Intro
    private static readonly TiebreakCriterion[] SoftwareListOrder =
    [
        TiebreakCriterion.LanguageRank,
        TiebreakCriterion.CompleteMultidiskSet,
        TiebreakCriterion.OriginalRelease,
        TiebreakCriterion.DriverStatus,
        TiebreakCriterion.CountryRank,
        TiebreakCriterion.VideoStandard,
        TiebreakCriterion.LanguageCount,
        TiebreakCriterion.RegionAffinity,
        TiebreakCriterion.Version,
        TiebreakCriterion.Revision,
        TiebreakCriterion.Date,
        TiebreakCriterion.ParentClone
    ];

    /// <summary>Conventions that have an order of their own, in the order the page offers them.</summary>
    public static IReadOnlyList<NamingConvention> Conventions { get; } =
        [NamingConvention.Tosec, NamingConvention.NoIntro, NamingConvention.Redump, NamingConvention.Mame, NamingConvention.MameSoftwareList];

    /// <summary>
    /// Convention whose order a release of <paramref name="convention"/> follows: its own, or that of TOSEC for a
    /// convention not known.
    /// </summary>
    public static NamingConvention OrderConventionOf(NamingConvention convention) =>
        convention is NamingConvention.NoIntro or NamingConvention.Redump or NamingConvention.Mame or NamingConvention.MameSoftwareList
            ? convention
            : NamingConvention.Tosec;

    /// <summary>Criteria that can separate two releases of <paramref name="convention"/>, in their default order of the "language first" mode.</summary>
    public static IReadOnlyList<TiebreakCriterion> CriteriaOf(NamingConvention convention) => OrderConventionOf(convention) switch
    {
        NamingConvention.NoIntro => NoIntroOrder,
        NamingConvention.Redump => RedumpOrder,
        NamingConvention.Mame => MameOrder,
        NamingConvention.MameSoftwareList => SoftwareListOrder,
        _ => TosecOrder
    };

    /// <summary>
    /// Default order of <paramref name="convention"/> in <paramref name="mode"/>, without the final tiebreak on the name:
    /// in the "country first" mode, the country moves to the top. The order of the MAME sets never depends on the mode.
    /// </summary>
    public static IReadOnlyList<TiebreakCriterion> Default(NamingConvention convention, RegionPriorityMode mode = RegionPriorityMode.LanguageFirst)
    {
        var criteria = CriteriaOf(convention);
        if (mode != RegionPriorityMode.CountryFirst || OrderConventionOf(convention) == NamingConvention.Mame)
        {
            return criteria;
        }

        return [TiebreakCriterion.CountryRank, .. criteria.Where(c => c != TiebreakCriterion.CountryRank)];
    }

    /// <summary>
    /// Repairs an order of <paramref name="convention"/>: <see langword="null"/> gives the default order of
    /// <paramref name="mode"/>; duplicates, criteria of other conventions, <see cref="TiebreakCriterion.None"/> and
    /// <see cref="TiebreakCriterion.Name"/> are dropped; a missing criterion is put back at its default place (right
    /// after the criterion preceding it by default).
    /// </summary>
    public static IReadOnlyList<TiebreakCriterion> Normalize(
        IEnumerable<TiebreakCriterion>? order,
        NamingConvention convention,
        RegionPriorityMode mode = RegionPriorityMode.LanguageFirst) =>
        Normalize(order, Default(convention, mode));

    /// <summary>Repairs an order around <paramref name="defaults"/>, its complete default order.</summary>
    public static IReadOnlyList<TiebreakCriterion> Normalize(IEnumerable<TiebreakCriterion>? order, IReadOnlyList<TiebreakCriterion> defaults)
    {
        if (order is null)
        {
            return defaults;
        }

        var result = order.Where(defaults.Contains).Distinct().ToList();
        for (var i = 0; i < defaults.Count; i++)
        {
            if (result.Contains(defaults[i]))
            {
                continue;
            }

            var insertAt = 0;
            for (var j = i - 1; j >= 0; j--)
            {
                var predecessor = result.IndexOf(defaults[j]);
                if (predecessor >= 0)
                {
                    insertAt = predecessor + 1;
                    break;
                }
            }

            result.Insert(insertAt, defaults[i]);
        }

        return result;
    }

    /// <summary>Region mode implied by an order: country first when the country comes before the language.</summary>
    public static RegionPriorityMode ImpliedRegionMode(IReadOnlyList<TiebreakCriterion> order) =>
        order.IndexOf(TiebreakCriterion.CountryRank) is var country and >= 0
        && order.IndexOf(TiebreakCriterion.LanguageRank) is var language and >= 0
        && country < language
            ? RegionPriorityMode.CountryFirst
            : RegionPriorityMode.LanguageFirst;

    /// <summary>
    /// Order of <paramref name="convention"/> to use after the region mode changed to <paramref name="mode"/>: the
    /// default order of the other mode becomes the default order of <paramref name="mode"/>; a customized order only
    /// swaps the places of the language and the country, when they do not follow the mode yet.
    /// </summary>
    public static IReadOnlyList<TiebreakCriterion> ApplyRegionMode(IReadOnlyList<TiebreakCriterion> order, NamingConvention convention, RegionPriorityMode mode)
    {
        var normalized = Normalize(order, convention, mode);
        if (ImpliedRegionMode(normalized) == mode)
        {
            return normalized;
        }

        var otherMode = mode == RegionPriorityMode.CountryFirst ? RegionPriorityMode.LanguageFirst : RegionPriorityMode.CountryFirst;
        if (normalized.SequenceEqual(Default(convention, otherMode)))
        {
            return Default(convention, mode);
        }

        var swapped = normalized.ToList();
        var language = swapped.IndexOf(TiebreakCriterion.LanguageRank);
        var country = swapped.IndexOf(TiebreakCriterion.CountryRank);
        (swapped[language], swapped[country]) = (swapped[country], swapped[language]);
        return swapped;
    }

    private static int IndexOf(this IReadOnlyList<TiebreakCriterion> order, TiebreakCriterion criterion)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i] == criterion)
            {
                return i;
            }
        }

        return -1;
    }
}
