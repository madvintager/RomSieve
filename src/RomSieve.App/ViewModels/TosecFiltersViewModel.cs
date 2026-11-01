using CommunityToolkit.Mvvm.ComponentModel;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.App.ViewModels;

/// <summary>
/// TOSEC (TNC) filter options: dump integrity, modifications, multi-disk sets, the Amiga the collection is sorted for
/// and the TOSEC-specific 1G1R options.
/// The filters tab shows them while TOSEC is the shown convention (<see cref="FiltersViewModel"/>).
/// </summary>
public sealed partial class TosecFiltersViewModel : ConfigSectionViewModel
{
    private readonly ILocalizationService _localization;

    public TosecFiltersViewModel(ILocalizationService localization)
    {
        _localization = localization;
        AmigaTargetOptions = new LocalizedOptionList<AmigaTarget>(
            localization,
            (AmigaTarget.Any, "amiga.target_any"),
            (AmigaTarget.Ocs, "amiga.target_ocs"),
            (AmigaTarget.Ecs, "amiga.target_ecs"),
            (AmigaTarget.Aga, "amiga.target_aga"));
    }

    /// <summary>Amigas the collection can be sorted for, from every machine to the AGA ones.</summary>
    public LocalizedOptionList<AmigaTarget> AmigaTargetOptions { get; }

    /// <summary>
    /// Amiga the collection is sorted for: the releases that need a later chipset are excluded, and for the AGA machines
    /// the AGA and OCS versions of a game are one game for the 1G1R.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AmigaTargetHint))]
    private AmigaTarget _amigaTarget = AmigaTarget.Any;

    /// <summary>What the chosen Amiga excludes, and what it does to the 1G1R.</summary>
    public string AmigaTargetHint => _localization.Tr(AmigaTarget switch
    {
        AmigaTarget.Ocs => "filters.amiga_hint_ocs",
        AmigaTarget.Ecs => "filters.amiga_hint_ecs",
        AmigaTarget.Aga => "filters.amiga_hint_aga",
        _ => "filters.amiga_hint_any"
    });

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreSubDumpFiltersEnabled), nameof(DumpLevel))]
    private bool _dumpsVerifiedOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DumpLevel))]
    private bool _excludeBadDumps = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DumpLevel))]
    private bool _excludeOverUnder = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DumpLevel))]
    private bool _excludeVirus = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DumpLevel))]
    private bool _excludeAlternate;

    [ObservableProperty]
    private bool _excludeModifications = true;

    [ObservableProperty]
    private bool _excludeTranslations;

    [ObservableProperty]
    private bool _excludeDevVersions;

    [ObservableProperty]
    private bool _excludeFreePd;

    [ObservableProperty]
    private bool _excludeDemos;

    [ObservableProperty]
    private bool _completeMultidiskSetsOnly;

    [ObservableProperty]
    private bool _smartMultidiskGrouping = true;

    [ObservableProperty]
    private bool _ignoreModificationsInPriority;

    /// <summary>
    /// Preferred media type of the 1G1R, saved with the TOSEC options but set on the line of its criterion in the order
    /// of TOSEC (<see cref="PrioritiesViewModel.MediaTypePreference"/>).
    /// </summary>
    [ObservableProperty]
    private MediaPreference _preferredMediaType = MediaPreference.None;

    /// <summary>The bad, alternate, over/under and virus filters are implied by "verified dumps only".</summary>
    public bool AreSubDumpFiltersEnabled => !IsRunning && !DumpsVerifiedOnly;

    /// <summary>
    /// Dump quality requirement summed up from the integrity options. Choosing a level rewrites them ("all dumps" clears
    /// the exclusions, "no bad dumps" restores their defaults, "verified only" keeps them, since they are then implied);
    /// <see cref="DumpQualityLevel.Custom"/> cannot be chosen and changes nothing.
    /// </summary>
    public DumpQualityLevel DumpLevel
    {
        get
        {
            if (DumpsVerifiedOnly)
            {
                return DumpQualityLevel.VerifiedOnly;
            }

            if (!ExcludeBadDumps && !ExcludeOverUnder && !ExcludeVirus && !ExcludeAlternate)
            {
                return DumpQualityLevel.All;
            }

            return ExcludeBadDumps == Defaults.ExcludeBadDumps
                && ExcludeOverUnder == Defaults.ExcludeOverUnder
                && ExcludeVirus == Defaults.ExcludeVirus
                && ExcludeAlternate == Defaults.ExcludeAlternate
                ? DumpQualityLevel.NoBadDumps
                : DumpQualityLevel.Custom;
        }
        set
        {
            switch (value)
            {
                case DumpQualityLevel.All:
                    DumpsVerifiedOnly = false;
                    SetIntegrityExclusions(false, false, false, false);
                    break;
                case DumpQualityLevel.NoBadDumps:
                    DumpsVerifiedOnly = false;
                    SetIntegrityExclusions(Defaults.ExcludeBadDumps, Defaults.ExcludeOverUnder, Defaults.ExcludeVirus, Defaults.ExcludeAlternate);
                    break;
                case DumpQualityLevel.VerifiedOnly:
                    DumpsVerifiedOnly = true;
                    break;
            }
        }
    }

    private static readonly SortSettings Defaults = new();

    private void SetIntegrityExclusions(bool badDumps, bool overUnder, bool virus, bool alternate)
    {
        ExcludeBadDumps = badDumps;
        ExcludeOverUnder = overUnder;
        ExcludeVirus = virus;
        ExcludeAlternate = alternate;
    }

    protected override void OnRunningStateChanged() => OnPropertyChanged(nameof(AreSubDumpFiltersEnabled));

    public override void WriteTo(SortSettings settings)
    {
        settings.DumpsVerifiedOnly = DumpsVerifiedOnly;
        settings.ExcludeBadDumps = ExcludeBadDumps;
        settings.ExcludeOverUnder = ExcludeOverUnder;
        settings.ExcludeVirus = ExcludeVirus;
        settings.ExcludeAlternate = ExcludeAlternate;
        settings.ExcludeModifications = ExcludeModifications;
        settings.ExcludeTranslations = ExcludeTranslations;
        settings.ExcludeDevVersions = ExcludeDevVersions;
        settings.ExcludeFreePd = ExcludeFreePd;
        settings.ExcludeDemos = ExcludeDemos;
        settings.CompleteMultidiskSetsOnly = CompleteMultidiskSetsOnly;
        settings.SmartMultidiskGrouping = SmartMultidiskGrouping;
        settings.IgnoreModificationsInPriority = IgnoreModificationsInPriority;
        settings.PreferredMediaType = PreferredMediaType;
        settings.AmigaTarget = AmigaTarget;
    }

    public override void ReadFrom(SortSettings settings)
    {
        DumpsVerifiedOnly = settings.DumpsVerifiedOnly;
        ExcludeBadDumps = settings.ExcludeBadDumps;
        ExcludeOverUnder = settings.ExcludeOverUnder;
        ExcludeVirus = settings.ExcludeVirus;
        ExcludeAlternate = settings.ExcludeAlternate;
        ExcludeModifications = settings.ExcludeModifications;
        ExcludeTranslations = settings.ExcludeTranslations;
        ExcludeDevVersions = settings.ExcludeDevVersions;
        ExcludeFreePd = settings.ExcludeFreePd;
        ExcludeDemos = settings.ExcludeDemos;
        CompleteMultidiskSetsOnly = settings.CompleteMultidiskSetsOnly;
        SmartMultidiskGrouping = settings.SmartMultidiskGrouping;
        IgnoreModificationsInPriority = settings.IgnoreModificationsInPriority;
        PreferredMediaType = settings.PreferredMediaType;
        AmigaTarget = settings.AmigaTarget;
    }

    public override void RefreshTranslations()
    {
        AmigaTargetOptions.RefreshTranslations();
        OnPropertyChanged(nameof(AmigaTargetHint));
    }
}
