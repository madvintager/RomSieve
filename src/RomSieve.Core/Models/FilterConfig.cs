namespace RomSieve.Core.Models;

/// <summary>
/// Configuration for selection, exclusion, and 1G1R tiebreaker filters.
/// </summary>
public sealed class FilterConfig
{
    /// <summary>
    /// Applies the content filters (<see cref="Services.FilterEvaluator.EvaluateFilter"/>) to the files whose CRC32 the
    /// DAT does not know and whose name follows its convention: a file they exclude is excluded, left in the source,
    /// instead of being isolated as non-conforming; a file they keep stays non-conforming. Without a DAT, every file is
    /// already filtered by its name.
    /// </summary>
    public bool FilterNotInDat { get; set; } = true;

    // Integrity & Dumps (TOSEC)
    public bool DumpsVerifiedOnly { get; set; } = false;
    public bool ExcludeBadDumps { get; set; } = true;
    public bool ExcludeAlternate { get; set; } = false;
    public bool ExcludeOverUnder { get; set; } = true;
    public bool ExcludeVirus { get; set; } = true;

    // Modifications & Versions (TOSEC)
    public bool ExcludeModifications { get; set; } = true;      // [cr, t, h, p, f, m]
    public bool ExcludeTranslations { get; set; } = false;      // [tr]
    public bool ExcludeDevVersions { get; set; } = false;       // (proto, alpha, beta)
    public bool ExcludeFreePd { get; set; } = false;            // Public domain / freeware
    public bool ExcludeDemos { get; set; } = false;             // Demo versions

    /// <summary>
    /// Amiga the collection is sorted for: the releases that need a later chipset are excluded (TOSEC names), and for the
    /// AGA machines the AGA and OCS versions of a game are one game, the 1G1R preferring the AGA one.
    /// </summary>
    public AmigaTarget AmigaTarget { get; set; } = AmigaTarget.Any;

    // Integrity & Dumps (No-Intro & Redump): their names never mark a verified dump, so there is no "verified only"
    public bool NoIntroExcludeBadDumps { get; set; } = true;
    public bool NoIntroExcludeAlternate { get; set; } = false;
    public bool NoIntroCompleteMultimediaSetsOnly { get; set; } = false;

    // Modifications & Releases (No-Intro & Redump)
    public bool NoIntroExcludePirate { get; set; } = true;
    public bool NoIntroExcludeHack { get; set; } = true;
    public bool NoIntroExcludeTranslations { get; set; } = false;
    public bool NoIntroExcludeDevVersions { get; set; } = false;
    public bool NoIntroExcludeUnlicensed { get; set; } = false;
    public bool NoIntroExcludeDemos { get; set; } = false;
    public bool NoIntroExcludeRereleases { get; set; } = false;

    // MAME machines (with the No-Intro / Redump options above)
    public bool MameExcludeBios { get; set; } = false;
    public bool MameExcludeDevices { get; set; } = false;
    public bool MameExcludeMechanical { get; set; } = false;
    public bool MameExcludeRequiresArtwork { get; set; } = false;

    /// <summary>
    /// Excludes the machines with nothing to dump (<see cref="MameMachineInfo.HasNothingToDump"/>): MAME runs them
    /// without any file of their own, and the archive of such a set in a merged collection only holds its clones.
    /// </summary>
    public bool MameExcludeNothingToDump { get; set; } = true;
    /// <summary>Driver statuses whose machines are kept; a machine without status always is.</summary>
    public MameDriverStatuses MameDriverStatuses { get; set; } = MameDriverStatuses.All;

    /// <summary>Also keeps a machine whose own emulation is imperfect or better, whatever its driver status.</summary>
    public bool MameKeepPlayable { get; set; } = false;

    public bool MameExcludeUnemulatedSound { get; set; } = false;

    /// <summary>
    /// Excludes the alternate MAME sets (<c>set 2</c>, <c>alt</c>). A MAME set has no dump quality: the No-Intro /
    /// Redump dump options never apply to it.
    /// </summary>
    public bool MameExcludeAlternateSets { get; set; } = false;

    // Cabinet of the MAME machines: a value the DAT does not give never excludes a machine
    public MameOrientationRequirement MameOrientation { get; set; } = MameOrientationRequirement.Any;
    public bool MameExcludeVector { get; set; } = false;
    public bool MameExcludeLcd { get; set; } = false;

    /// <summary>Fewest players a machine must accept; 0 keeps any number.</summary>
    public int MameMinimumPlayers { get; set; }

    /// <summary>Most buttons a control of a machine may have; 0 keeps any number.</summary>
    public int MameMaximumButtons { get; set; }

    /// <summary>Kinds of controls the panel lacks: a machine that uses one of them is excluded.</summary>
    public MameControls MameExcludedControls { get; set; } = MameControls.None;

    // Category of the MAME machines (category file): a machine without category is never excluded
    public bool MameExcludeMature { get; set; } = false;

    /// <summary>Excludes the machines the category file says are not arcade machines (consoles, computers...).</summary>
    public bool MameArcadeOnly { get; set; } = false;

    /// <summary>Genres of the category file whose machines are excluded (<c>Casino</c>, <c>Quiz</c>...), any case.</summary>
    public List<string> MameExcludedGenres { get; set; } = [];

    // Rating of the MAME machines (rating file): without a file, no machine is excluded
    /// <summary>Lowest rating out of 100 a rated machine needs to be kept (the lower bound of its range); 0: any.</summary>
    public int MameMinimumRating { get; set; }

    /// <summary>Excludes the machines the rating file does not rate.</summary>
    public bool MameExcludeUnrated { get; set; } = false;

    /// <summary>
    /// Most bytes the kept MAME sets may take, with their CHDs and the sets they need; 0: no limit. The best rated sets
    /// come first (<see cref="Services.ReleaseSelector"/>).
    /// </summary>
    public long MameSizeBudgetBytes { get; set; }

    // Language & Country/Region filtering & Priority (1G1R)
    public bool FilterLanguages { get; set; } = false;
    public List<string> PriorityLanguages { get; set; } = [];
    public bool FallbackUnmatchedLanguages { get; set; } = true;
    public bool FilterCountries { get; set; } = false;
    public List<string> PriorityCountries { get; set; } = [];
    public bool FallbackUnmatchedCountries { get; set; } = true;
    public RegionPriorityMode RegionPriorityMode { get; set; } = RegionPriorityMode.LanguageFirst;

    /// <summary>
    /// Order of the 1G1R criteria of the TOSEC releases (and of a release whose convention is not known), without the
    /// final tiebreak on the name; <see langword="null"/> uses the default order of <see cref="RegionPriorityMode"/>.
    /// When set, it alone decides the cascade (the region mode then only orders the language and country filters used
    /// without 1G1R). Each order is repaired by <see cref="GetTiebreakOrder"/>.
    /// </summary>
    public List<TiebreakCriterion>? TosecTiebreakOrder { get; set; }

    /// <summary>Order of the 1G1R criteria of the No-Intro releases, as <see cref="TosecTiebreakOrder"/>.</summary>
    public List<TiebreakCriterion>? NoIntroTiebreakOrder { get; set; }

    /// <summary>Order of the 1G1R criteria of the Redump releases, as <see cref="TosecTiebreakOrder"/>.</summary>
    public List<TiebreakCriterion>? RedumpTiebreakOrder { get; set; }

    /// <summary>
    /// Order of the 1G1R criteria of the MAME sets; <see langword="null"/> uses its default order, which never depends
    /// on <see cref="RegionPriorityMode"/>.
    /// </summary>
    public List<TiebreakCriterion>? MameTiebreakOrder { get; set; }

    /// <summary>Order of the 1G1R criteria of the software of the MAME lists, as <see cref="TosecTiebreakOrder"/>.</summary>
    public List<TiebreakCriterion>? SoftwareListTiebreakOrder { get; set; }

    /// <summary>
    /// Order of the 1G1R criteria that a release of <paramref name="convention"/> follows, repaired by
    /// <see cref="TiebreakOrder.Normalize(IEnumerable{TiebreakCriterion}?, NamingConvention, RegionPriorityMode)"/>.
    /// </summary>
    public IReadOnlyList<TiebreakCriterion> GetTiebreakOrder(NamingConvention convention)
    {
        var orderConvention = TiebreakOrder.OrderConventionOf(convention);
        var order = orderConvention switch
        {
            NamingConvention.NoIntro => NoIntroTiebreakOrder,
            NamingConvention.Redump => RedumpTiebreakOrder,
            NamingConvention.Mame => MameTiebreakOrder,
            NamingConvention.MameSoftwareList => SoftwareListTiebreakOrder,
            _ => TosecTiebreakOrder
        };
        return TiebreakOrder.Normalize(order, orderConvention, RegionPriorityMode);
    }
    public bool OneGameOneRom { get; set; } = false;
    public bool IgnoreModificationsInPriority { get; set; } = false;
    public VideoPreference PreferredVideo { get; set; } = VideoPreference.None;
    public MediaPreference PreferredMediaType { get; set; } = MediaPreference.None;
    public bool PreferMonolingual { get; set; } = true;
    public Dictionary<string, string> Manual1G1RWinners { get; set; } = new(StringComparer.Ordinal);

    // DAT & Advanced multi-media options
    public bool CompleteMultidiskSetsOnly { get; set; } = false;
    public bool SmartMultidiskGrouping { get; set; } = true;
    public bool ScanArchives { get; set; } = true;
    public bool DetectCopierHeaders { get; set; } = true;

    /// <summary>The 1G1R deduplication relies on the language filter: turns it on with the 1G1R.</summary>
    public void NormalizeDefaults()
    {
        if (OneGameOneRom)
        {
            FilterLanguages = true;
        }
    }
}
