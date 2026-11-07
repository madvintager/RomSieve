namespace RomSieve.Core.Models;

/// <summary>
/// Options of a sort as the user sets them and as <c>config.json</c> (or a profile) stores them, shared by the
/// graphical interface and the command line. <see cref="Services.SortSettingsSerializer"/> reads and writes them
/// and builds the <see cref="SortConfig"/> of a run. The state of the interface (tabs, window, language, theme)
/// is not part of it.
/// </summary>
public sealed class SortSettings
{
    public const string DefaultPattern = "{publisher}/{year}/{title}/";

    /// <summary>Values accepted for <see cref="HashingParallelism"/>; any other one falls back to 1.</summary>
    public static IReadOnlyList<int> HashingParallelismValues { get; } = [1, 2, 4, 8, SortConfig.AutoHashingParallelism];

    // Directories, transfers and multi-disk sets
    public string SourceDir { get; set; } = string.Empty;
    public string DestinationDir { get; set; } = string.Empty;
    public string DatFile { get; set; } = string.Empty;

    /// <summary>Whether a folder of DATs is read with its subfolders (<see cref="SortConfig.DatRecursive"/>).</summary>
    public bool DatRecursive { get; set; }

    /// <summary>
    /// Header names of the DATs of a lot left out of the sort (<see cref="SortConfig.ExcludedDats"/>), without duplicates
    /// (case ignored); empty: every DAT.
    /// </summary>
    public List<string> ExcludedDats { get; set; } = [];

    /// <summary>Whether each DAT of a lot gets a folder of its own in the destination (<see cref="SortConfig.FolderPerDat"/>).</summary>
    public bool FolderPerDat { get; set; } = true;

    /// <summary>Whether the folder of each DAT is placed under the folders of its path (<see cref="SortConfig.DatFolderMirror"/>).</summary>
    public bool DatFolderMirror { get; set; }

    /// <summary>Folder of the CHDs of a MAME collection, read in addition to the source (MAME runs only); empty: none.</summary>
    public string ChdDir { get; set; } = string.Empty;

    /// <summary>Category file of the MAME machines (MAME runs only); empty: none.</summary>
    public string MameCategoryFile { get; set; } = string.Empty;

    /// <summary>Rating file of the MAME machines (MAME runs only); empty: none.</summary>
    public string MameRatingFile { get; set; } = string.Empty;
    public NamingConvention NamingConvention { get; set; } = NamingConvention.Auto;
    public bool Recursive { get; set; } = true;
    public bool ScanArchives { get; set; } = true;
    public bool AppendToDestination { get; set; }
    public TransferMode TransferMode { get; set; } = TransferMode.Copy;
    public CollisionMode CollisionMode { get; set; } = CollisionMode.Rename;
    public bool VerifyCopies { get; set; }
    public bool IsolateUnmatched { get; set; } = true;
    public int HashingParallelism { get; set; } = 1;
    public bool VerifySha1 { get; set; }
    public bool VerifyArchiveContents { get; set; }
    public bool MatchChdByName { get; set; }
    public bool FlatDestination { get; set; }
    public string Pattern { get; set; } = DefaultPattern;
    public bool RestoreArticles { get; set; } = true;
    public bool SubfolderMultidisk { get; set; } = true;
    public bool GenerateM3u { get; set; }
    public M3uPlacementMode M3uPlacement { get; set; } = M3uPlacementMode.Parent;

    // TOSEC criteria
    public bool DumpsVerifiedOnly { get; set; }
    public bool ExcludeBadDumps { get; set; } = true;
    public bool ExcludeOverUnder { get; set; } = true;
    public bool ExcludeVirus { get; set; } = true;
    public bool ExcludeAlternate { get; set; }
    public bool ExcludeModifications { get; set; } = true;
    public bool ExcludeTranslations { get; set; }
    public bool ExcludeDevVersions { get; set; }
    public bool ExcludeFreePd { get; set; }
    public bool ExcludeDemos { get; set; }
    public bool CompleteMultidiskSetsOnly { get; set; }
    public bool SmartMultidiskGrouping { get; set; } = true;
    public bool IgnoreModificationsInPriority { get; set; }
    public MediaPreference PreferredMediaType { get; set; } = MediaPreference.None;
    public AmigaTarget AmigaTarget { get; set; } = AmigaTarget.Any;

    // No-Intro and Redump criteria
    public bool NoIntroExcludeBadDumps { get; set; } = true;
    public bool NoIntroExcludeAlternate { get; set; }
    public bool NoIntroCompleteMultimediaSetsOnly { get; set; }
    public bool NoIntroExcludePirate { get; set; } = true;
    public bool NoIntroExcludeHack { get; set; } = true;
    public bool NoIntroExcludeTranslations { get; set; }
    public bool NoIntroExcludeDevVersions { get; set; }
    public bool NoIntroExcludeUnlicensed { get; set; }
    public bool NoIntroExcludeDemos { get; set; }
    public bool NoIntroExcludeRereleases { get; set; }

    // MAME machines
    public bool MameExcludeBios { get; set; }
    public bool MameExcludeDevices { get; set; }
    public bool MameExcludeMechanical { get; set; }
    public bool MameExcludeRequiresArtwork { get; set; }

    /// <summary>Excludes the MAME machines with nothing to dump (<c>mame_exclude_nothing_to_dump</c>).</summary>
    public bool MameExcludeNothingToDump { get; set; } = true;
    public MameDriverStatuses MameDriverStatuses { get; set; } = MameDriverStatuses.All;
    public bool MameKeepPlayable { get; set; }
    public bool MameExcludeUnemulatedSound { get; set; }
    public bool MameExcludeAlternateSets { get; set; }
    public MameOrientationRequirement MameOrientation { get; set; } = MameOrientationRequirement.Any;
    public bool MameExcludeVector { get; set; }
    public bool MameExcludeLcd { get; set; }
    public int MameMinimumPlayers { get; set; }
    public int MameMaximumButtons { get; set; }
    public MameControls MameExcludedControls { get; set; } = MameControls.None;
    public bool MameExcludeMature { get; set; }
    public bool MameArcadeOnly { get; set; }
    public List<string> MameExcludedGenres { get; set; } = [];
    public int MameMinimumRating { get; set; }
    public bool MameExcludeUnrated { get; set; }

    /// <summary>Most gigabytes (10^9 bytes, as a memory card says) the kept MAME sets may take; 0: no limit.</summary>
    public double MameSizeBudgetGb { get; set; }

    // Languages, countries and 1G1R
    public bool FilterLanguages { get; set; }
    public bool FallbackUnmatchedLanguages { get; set; } = true;

    /// <summary>Lowercase language codes, without duplicates (<see cref="NormalizeLanguageCodes"/>).</summary>
    public List<string> PriorityLanguages { get; set; } = [];
    public bool FilterCountries { get; set; }
    public bool FallbackUnmatchedCountries { get; set; } = true;

    /// <summary>Uppercase country codes, without duplicates (<see cref="NormalizeCountryCodes"/>).</summary>
    public List<string> PriorityCountries { get; set; } = [];
    public RegionPriorityMode RegionPriorityMode { get; set; } = RegionPriorityMode.LanguageFirst;

    /// <summary>
    /// Order of the 1G1R criteria of the TOSEC releases, complete and consistent with <see cref="RegionPriorityMode"/>,
    /// as the orders of No-Intro and Redump.
    /// </summary>
    public List<TiebreakCriterion> TosecTiebreakOrder { get; set; } = Models.TiebreakOrder.Default(NamingConvention.Tosec).ToList();

    /// <summary>Order of the 1G1R criteria of the No-Intro releases, complete and consistent with <see cref="RegionPriorityMode"/>.</summary>
    public List<TiebreakCriterion> NoIntroTiebreakOrder { get; set; } = Models.TiebreakOrder.Default(NamingConvention.NoIntro).ToList();

    /// <summary>Order of the 1G1R criteria of the Redump releases, complete and consistent with <see cref="RegionPriorityMode"/>.</summary>
    public List<TiebreakCriterion> RedumpTiebreakOrder { get; set; } = Models.TiebreakOrder.Default(NamingConvention.Redump).ToList();

    /// <summary>Order of the 1G1R criteria of the MAME sets, complete; it does not set the region mode.</summary>
    public List<TiebreakCriterion> MameTiebreakOrder { get; set; } = Models.TiebreakOrder.Default(NamingConvention.Mame).ToList();

    /// <summary>
    /// Order of the 1G1R criteria of the software of the MAME lists, complete and consistent with
    /// <see cref="RegionPriorityMode"/>.
    /// </summary>
    public List<TiebreakCriterion> SoftwareListTiebreakOrder { get; set; } = Models.TiebreakOrder.Default(NamingConvention.MameSoftwareList).ToList();
    public bool OneGameOneRom { get; set; }
    public VideoPreference PreferredVideo { get; set; } = VideoPreference.None;
    public bool PreferMonolingual { get; set; } = true;

    /// <summary>Returns an independent copy (the lists are copied too).</summary>
    public SortSettings Clone()
    {
        var copy = (SortSettings)MemberwiseClone();
        copy.MameExcludedGenres = [.. MameExcludedGenres];
        copy.ExcludedDats = [.. ExcludedDats];
        copy.PriorityLanguages = [.. PriorityLanguages];
        copy.PriorityCountries = [.. PriorityCountries];
        copy.TosecTiebreakOrder = [.. TosecTiebreakOrder];
        copy.NoIntroTiebreakOrder = [.. NoIntroTiebreakOrder];
        copy.RedumpTiebreakOrder = [.. RedumpTiebreakOrder];
        copy.MameTiebreakOrder = [.. MameTiebreakOrder];
        copy.SoftwareListTiebreakOrder = [.. SoftwareListTiebreakOrder];
        return copy;
    }

    /// <summary>Trims and lowercases language codes, dropping empty codes and duplicates.</summary>
    public static List<string> NormalizeLanguageCodes(IEnumerable<string> codes) =>
        NormalizeCodes(codes, code => code.Trim().ToLowerInvariant());

    /// <summary>Trims and uppercases country codes (<c>UK</c> becomes <c>GB</c>, <c>ASIA</c> becomes <c>AS</c>), dropping empty codes and duplicates.</summary>
    public static List<string> NormalizeCountryCodes(IEnumerable<string> codes) =>
        NormalizeCodes(codes, NormalizeCountryCode);

    public static string NormalizeCountryCode(string code)
    {
        var upper = code.Trim().ToUpperInvariant();
        return upper switch
        {
            "UK" => "GB",
            "ASIA" => "AS",
            _ => upper
        };
    }

    private static List<string> NormalizeCodes(IEnumerable<string> codes, Func<string, string> normalize)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in codes)
        {
            var code = normalize(raw);
            if (!string.IsNullOrEmpty(code) && seen.Add(code))
            {
                result.Add(code);
            }
        }

        return result;
    }
}
