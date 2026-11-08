using CommunityToolkit.Mvvm.ComponentModel;
using RomSieve.Core.Models;

namespace RomSieve.App.ViewModels;

/// <summary>
/// No-Intro and Redump filter options, shown by the filters tab for these conventions (<see cref="FiltersViewModel"/>),
/// which MAME sets use too, with the options of MAME machines (BIOS, devices, mechanical machines, emulation) and the
/// option of every convention that filters the files missing from the DAT (<see cref="FilterNotInDat"/>).
/// </summary>
public sealed partial class NoIntroFiltersViewModel : ConfigSectionViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DumpLevel))]
    private bool _excludeBadDumps = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DumpLevel))]
    private bool _excludeAlternate;

    [ObservableProperty]
    private bool _completeMultimediaSetsOnly;

    [ObservableProperty]
    private bool _excludePirate = true;

    [ObservableProperty]
    private bool _excludeHack = true;

    [ObservableProperty]
    private bool _excludeTranslations;

    [ObservableProperty]
    private bool _excludeDevVersions;

    [ObservableProperty]
    private bool _excludeUnlicensed;

    [ObservableProperty]
    private bool _excludeDemos;

    [ObservableProperty]
    private bool _excludeRereleases;

    /// <summary>
    /// Filters the files missing from the DAT by their name too (<see cref="SortSettings.FilterNotInDat"/>): an option of
    /// every convention, kept by this section like those of the MAME machines.
    /// </summary>
    [ObservableProperty]
    private bool _filterNotInDat = true;

    // MAME machines, whose sets use the options of this section
    [ObservableProperty]
    private bool _excludeMameBios;

    [ObservableProperty]
    private bool _excludeMameDevices;

    [ObservableProperty]
    private bool _excludeMameMechanical;

    [ObservableProperty]
    private bool _excludeMameRequiresArtwork;

    [ObservableProperty]
    private bool _excludeMameNothingToDump = true;

    [ObservableProperty]
    private bool _excludeMameAlternateSets;

    [ObservableProperty]
    private MameDriverStatuses _mameDriverStatuses = MameDriverStatuses.All;

    [ObservableProperty]
    private bool _mameKeepPlayable;

    [ObservableProperty]
    private bool _excludeMameUnemulatedSound;

    // Cabinet of the MAME machines: screen, players and controls
    [ObservableProperty]
    private MameOrientationRequirement _mameOrientation;

    [ObservableProperty]
    private bool _excludeMameVector;

    [ObservableProperty]
    private bool _excludeMameLcd;

    [ObservableProperty]
    private int _mameMinimumPlayers;

    [ObservableProperty]
    private int _mameMaximumButtons;

    [ObservableProperty]
    private MameControls _mameExcludedControls;

    // Category of the MAME machines, from the category file
    [ObservableProperty]
    private bool _excludeMameMature;

    [ObservableProperty]
    private bool _mameArcadeOnly;

    /// <summary>Genres whose machines are excluded; replaced as a whole on each change.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _mameExcludedGenres = [];

    // Rating of the MAME machines, from the rating file, and size budget of the kept sets
    [ObservableProperty]
    private int _mameMinimumRating;

    [ObservableProperty]
    private bool _excludeMameUnrated;

    /// <summary>Most gigabytes (10^9 bytes) the kept MAME sets may take; 0: no limit.</summary>
    [ObservableProperty]
    private double _mameSizeBudgetGb;

    /// <summary>
    /// Dump quality requirement summed up from the integrity options, as for TOSEC
    /// (<see cref="TosecFiltersViewModel.DumpLevel"/>). No-Intro and Redump names never mark a verified dump: every
    /// dump that is not bad counts as verified, so there is no "verified dumps only" level.
    /// </summary>
    public DumpQualityLevel DumpLevel
    {
        get
        {
            if (!ExcludeBadDumps && !ExcludeAlternate)
            {
                return DumpQualityLevel.All;
            }

            return ExcludeBadDumps == Defaults.NoIntroExcludeBadDumps && ExcludeAlternate == Defaults.NoIntroExcludeAlternate
                ? DumpQualityLevel.NoBadDumps
                : DumpQualityLevel.Custom;
        }
        set
        {
            switch (value)
            {
                case DumpQualityLevel.All:
                    ExcludeBadDumps = false;
                    ExcludeAlternate = false;
                    break;
                case DumpQualityLevel.NoBadDumps:
                    ExcludeBadDumps = Defaults.NoIntroExcludeBadDumps;
                    ExcludeAlternate = Defaults.NoIntroExcludeAlternate;
                    break;
            }
        }
    }

    private static readonly SortSettings Defaults = new();

    public override void WriteTo(SortSettings settings)
    {
        settings.NoIntroExcludeBadDumps = ExcludeBadDumps;
        settings.NoIntroExcludeAlternate = ExcludeAlternate;
        settings.NoIntroCompleteMultimediaSetsOnly = CompleteMultimediaSetsOnly;
        settings.NoIntroExcludePirate = ExcludePirate;
        settings.NoIntroExcludeHack = ExcludeHack;
        settings.NoIntroExcludeTranslations = ExcludeTranslations;
        settings.NoIntroExcludeDevVersions = ExcludeDevVersions;
        settings.NoIntroExcludeUnlicensed = ExcludeUnlicensed;
        settings.NoIntroExcludeDemos = ExcludeDemos;
        settings.NoIntroExcludeRereleases = ExcludeRereleases;
        settings.FilterNotInDat = FilterNotInDat;
        settings.MameExcludeBios = ExcludeMameBios;
        settings.MameExcludeDevices = ExcludeMameDevices;
        settings.MameExcludeMechanical = ExcludeMameMechanical;
        settings.MameExcludeRequiresArtwork = ExcludeMameRequiresArtwork;
        settings.MameExcludeNothingToDump = ExcludeMameNothingToDump;
        settings.MameDriverStatuses = MameDriverStatuses;
        settings.MameKeepPlayable = MameKeepPlayable;
        settings.MameExcludeUnemulatedSound = ExcludeMameUnemulatedSound;
        settings.MameExcludeAlternateSets = ExcludeMameAlternateSets;
        settings.MameOrientation = MameOrientation;
        settings.MameExcludeVector = ExcludeMameVector;
        settings.MameExcludeLcd = ExcludeMameLcd;
        settings.MameMinimumPlayers = MameMinimumPlayers;
        settings.MameMaximumButtons = MameMaximumButtons;
        settings.MameExcludedControls = MameExcludedControls;
        settings.MameExcludeMature = ExcludeMameMature;
        settings.MameArcadeOnly = MameArcadeOnly;
        settings.MameExcludedGenres = MameExcludedGenres.ToList();
        settings.MameMinimumRating = MameMinimumRating;
        settings.MameExcludeUnrated = ExcludeMameUnrated;
        settings.MameSizeBudgetGb = MameSizeBudgetGb;
    }

    public override void ReadFrom(SortSettings settings)
    {
        ExcludeBadDumps = settings.NoIntroExcludeBadDumps;
        ExcludeAlternate = settings.NoIntroExcludeAlternate;
        CompleteMultimediaSetsOnly = settings.NoIntroCompleteMultimediaSetsOnly;
        ExcludePirate = settings.NoIntroExcludePirate;
        ExcludeHack = settings.NoIntroExcludeHack;
        ExcludeTranslations = settings.NoIntroExcludeTranslations;
        ExcludeDevVersions = settings.NoIntroExcludeDevVersions;
        ExcludeUnlicensed = settings.NoIntroExcludeUnlicensed;
        ExcludeDemos = settings.NoIntroExcludeDemos;
        ExcludeRereleases = settings.NoIntroExcludeRereleases;
        FilterNotInDat = settings.FilterNotInDat;
        ExcludeMameBios = settings.MameExcludeBios;
        ExcludeMameDevices = settings.MameExcludeDevices;
        ExcludeMameMechanical = settings.MameExcludeMechanical;
        ExcludeMameRequiresArtwork = settings.MameExcludeRequiresArtwork;
        ExcludeMameNothingToDump = settings.MameExcludeNothingToDump;
        MameDriverStatuses = settings.MameDriverStatuses;
        MameKeepPlayable = settings.MameKeepPlayable;
        ExcludeMameUnemulatedSound = settings.MameExcludeUnemulatedSound;
        ExcludeMameAlternateSets = settings.MameExcludeAlternateSets;
        MameOrientation = settings.MameOrientation;
        ExcludeMameVector = settings.MameExcludeVector;
        ExcludeMameLcd = settings.MameExcludeLcd;
        MameMinimumPlayers = settings.MameMinimumPlayers;
        MameMaximumButtons = settings.MameMaximumButtons;
        MameExcludedControls = settings.MameExcludedControls;
        ExcludeMameMature = settings.MameExcludeMature;
        MameArcadeOnly = settings.MameArcadeOnly;
        MameExcludedGenres = settings.MameExcludedGenres.ToList();
        MameMinimumRating = settings.MameMinimumRating;
        ExcludeMameUnrated = settings.MameExcludeUnrated;
        MameSizeBudgetGb = settings.MameSizeBudgetGb;
    }
}
