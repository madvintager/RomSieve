using System.ComponentModel;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Filters page: shows the options of one convention at a time (TOSEC, or No-Intro and Redump). The categories both
/// conventions share (dump quality, modified versions, translations, development versions, demos, incomplete sets) are
/// shown once and act on the options of the shown convention, whose tag is displayed next to them; the options of one
/// convention only are shown for it alone. The content categories are worded as what to keep (<c>Keep*</c>), the
/// opposite of the <c>exclude_*</c> options of <c>config.json</c>, which do not change. The options themselves stay in <see cref="TosecFiltersViewModel"/> and
/// <see cref="NoIntroFiltersViewModel"/>, which exchange them with the <see cref="SortSettings"/>: this ViewModel only
/// presents them and has nothing to save but the shown convention, an interface state.
/// </summary>
public sealed class FiltersViewModel : ViewModelBase
{
    public const int TosecConventionIndex = 0;
    public const int NoIntroConventionIndex = 1;

    private readonly ILocalizationService _localization;

    public FiltersViewModel(ILocalizationService localization, TosecFiltersViewModel tosec, NoIntroFiltersViewModel noIntro)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        Tosec = tosec ?? throw new ArgumentNullException(nameof(tosec));
        NoIntro = noIntro ?? throw new ArgumentNullException(nameof(noIntro));
        Cabinet = new MameCabinetFiltersViewModel(localization, noIntro);
        Categories = new MameCategoryFiltersViewModel(localization, noIntro);
        Ratings = new MameRatingFiltersViewModel(localization, noIntro);
        Tosec.PropertyChanged += OnSectionPropertyChanged;
        NoIntro.PropertyChanged += OnSectionPropertyChanged;
    }

    public TosecFiltersViewModel Tosec { get; }

    public NoIntroFiltersViewModel NoIntro { get; }

    /// <summary>Cabinet filters of the MAME machines (screen, players, controls), shown in the MAME card.</summary>
    public MameCabinetFiltersViewModel Cabinet { get; }

    /// <summary>Category filters of the MAME machines (adults, arcade, genres), shown in the MAME card.</summary>
    public MameCategoryFiltersViewModel Categories { get; }

    /// <summary>Rating filters and size budget of the MAME machines, shown in the MAME card.</summary>
    public MameRatingFiltersViewModel Ratings { get; }

    /// <summary>
    /// Convention whose options are shown: <see cref="TosecConventionIndex"/> or <see cref="NoIntroConventionIndex"/>
    /// (saved as <c>filter_subtab_index</c>). Any other value is ignored.
    /// </summary>
    public int ShownConventionIndex
    {
        get => _shownConventionIndex;
        set
        {
            if (value is not (TosecConventionIndex or NoIntroConventionIndex) || value == _shownConventionIndex)
            {
                return;
            }

            _shownConventionIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTosecShown));
            OnPropertyChanged(nameof(IsNoIntroShown));
            OnPropertyChanged(nameof(AreMameOptionsShown));
            OnPropertyChanged(nameof(IsMultidiskGroupShown));
            NotifyShownOptionsChanged();
        }
    }

    private int _shownConventionIndex = TosecConventionIndex;

    /// <summary>Selector of the shown convention; setting false is ignored, as the other choice sets it.</summary>
    public bool IsTosecShown
    {
        get => ShownConventionIndex == TosecConventionIndex;
        set
        {
            if (value)
            {
                ShownConventionIndex = TosecConventionIndex;
            }
        }
    }

    public bool IsNoIntroShown
    {
        get => ShownConventionIndex == NoIntroConventionIndex;
        set
        {
            if (value)
            {
                ShownConventionIndex = NoIntroConventionIndex;
            }
        }
    }

    /// <summary>
    /// Shows the options of the effective convention. While it is unknown (<see cref="NamingConvention.Auto"/>), the
    /// shown convention does not change: the last one chosen stays. The options of MAME machines are shown with the
    /// No-Intro / Redump options when the effective convention is MAME.
    /// </summary>
    public void ShowConvention(NamingConvention convention)
    {
        if (convention == NamingConvention.Tosec)
        {
            ShownConventionIndex = TosecConventionIndex;
        }
        else if (convention.UsesNoIntroSettings())
        {
            ShownConventionIndex = NoIntroConventionIndex;
        }

        if (_isMameConvention != (convention == NamingConvention.Mame) ||
            _isSoftwareListConvention != (convention == NamingConvention.MameSoftwareList))
        {
            _isMameConvention = convention == NamingConvention.Mame;
            _isSoftwareListConvention = convention == NamingConvention.MameSoftwareList;
            OnPropertyChanged(nameof(AreMameOptionsShown));
            OnPropertyChanged(nameof(IsMultidiskGroupShown));
            NotifyShownOptionsChanged();
        }
    }

    private bool _isMameConvention;
    private bool _isSoftwareListConvention;

    /// <summary>
    /// Whether the software of a MAME software list follow the shown options: No-Intro / Redump options shown with the
    /// convention of the lists. Their dump quality has no effect (a ROM marked "baddump" is the best dump known), nor do
    /// the unlicensed releases and modern re-releases, which their descriptions never mark.
    /// </summary>
    public bool AreSoftwareListOptionsShown => _isSoftwareListConvention && IsNoIntroShown;

    /// <summary>
    /// Whether the multi-disc group is shown: not with the MAME options, whose card carries the complete sets option
    /// (a MAME run has no multi-disc games, only sets that lack ROMs or disks).
    /// </summary>
    public bool IsMultidiskGroupShown => !AreMameOptionsShown;

    /// <summary>
    /// Whether the translations have an effect: not on MAME sets, as a MAME description never marks a translation.
    /// With the MAME options, the box stays in sight, greyed, its tooltip saying why.
    /// </summary>
    public bool AreTranslationsApplicable => !AreMameOptionsShown;

    /// <summary>
    /// Whether the dump quality group has an effect: not with the MAME options, as the No-Intro / Redump dump options
    /// never apply to a MAME set (its alternate sets have their own option in the MAME card). Greyed otherwise.
    /// </summary>
    public bool IsDumpQualityApplicable => !AreMameOptionsShown && !AreSoftwareListOptionsShown;

    /// <summary>
    /// Whether the No-Intro / Redump only categories (unlicensed releases, modern re-releases) have an effect: not on
    /// MAME sets, which never carry them. Shown with the No-Intro / Redump options, greyed with the MAME ones.
    /// </summary>
    public bool AreNoIntroOnlyOptionsApplicable => !AreMameOptionsShown && !AreSoftwareListOptionsShown;

    /// <summary>
    /// Whether filtering the files missing from the DAT (<see cref="NoIntroFiltersViewModel.FilterNotInDat"/>, an option
    /// of every convention) has an effect: not in a MAME run or with software lists, whatever the convention shown, as a
    /// file unknown to such a DAT is never read by its name. Greyed then, its tooltip saying why.
    /// </summary>
    public bool IsFilterNotInDatApplicable => !_isMameConvention && !_isSoftwareListConvention;

    /// <summary>
    /// Whether completing a set with the disks of another dump (<see cref="TosecFiltersViewModel.SmartMultidiskGrouping"/>,
    /// shared by TOSEC and No-Intro / Redump) has an effect: not in a MAME run or with software lists, whatever the
    /// convention shown, as a MAME set or a software holds all its disks. Greyed then, its tooltip saying where it applies.
    /// </summary>
    public bool IsSmartMultidiskApplicable => !_isMameConvention && !_isSoftwareListConvention;

    public string FilterNotInDatTooltip => _localization.Tr(
        IsFilterNotInDatApplicable ? "filters.tooltip_filter_not_in_dat" : "filters.tooltip_filter_not_in_dat_mame");

    // =========================================================================
    // MAME machines
    // =========================================================================

    /// <summary>Whether the options of MAME machines are shown: MAME convention, No-Intro / Redump options shown.</summary>
    public bool AreMameOptionsShown => _isMameConvention && IsNoIntroShown;

    /// <summary>
    /// Whether the MAME card is shown: with the No-Intro / Redump options, whose settings the MAME sets follow. Without the
    /// MAME convention, it is reduced to its title and a sentence saying when it applies, so that it is known.
    /// </summary>
    public bool IsMameCardShown => IsNoIntroShown;

    /// <summary>Sentence next to the title of the MAME card: what it holds, or when it applies.</summary>
    public string MameCardHint => _localization.Tr(
        AreMameOptionsShown ? "filters.mame_card_hint"
        : AreSoftwareListOptionsShown ? "filters.mame_card_softlist_hint"
        : "filters.mame_card_inactive_hint");

    public bool KeepMameBios
    {
        get => !NoIntro.ExcludeMameBios;
        set => NoIntro.ExcludeMameBios = !value;
    }

    public bool KeepMameDevices
    {
        get => !NoIntro.ExcludeMameDevices;
        set => NoIntro.ExcludeMameDevices = !value;
    }

    public bool KeepMameMechanical
    {
        get => !NoIntro.ExcludeMameMechanical;
        set => NoIntro.ExcludeMameMechanical = !value;
    }

    public bool KeepMameRequiresArtwork
    {
        get => !NoIntro.ExcludeMameRequiresArtwork;
        set => NoIntro.ExcludeMameRequiresArtwork = !value;
    }

    public bool KeepMameNothingToDump
    {
        get => !NoIntro.ExcludeMameNothingToDump;
        set => NoIntro.ExcludeMameNothingToDump = !value;
    }

    // Emulation: the driver statuses kept, each a box, and the playable machines whatever their status

    public bool KeepMameDriverGood
    {
        get => NoIntro.MameDriverStatuses.HasFlag(MameDriverStatuses.Good);
        set => SetDriverStatus(MameDriverStatuses.Good, value);
    }

    public bool KeepMameDriverImperfect
    {
        get => NoIntro.MameDriverStatuses.HasFlag(MameDriverStatuses.Imperfect);
        set => SetDriverStatus(MameDriverStatuses.Imperfect, value);
    }

    public bool KeepMameDriverPreliminary
    {
        get => NoIntro.MameDriverStatuses.HasFlag(MameDriverStatuses.Preliminary);
        set => SetDriverStatus(MameDriverStatuses.Preliminary, value);
    }

    /// <summary>Also keeps the machines whose core is well emulated, whatever their driver status.</summary>
    public bool MameKeepPlayable
    {
        get => NoIntro.MameKeepPlayable;
        set => NoIntro.MameKeepPlayable = value;
    }

    /// <summary>The playable machines change nothing when every status is kept: the box is then greyed.</summary>
    public bool IsMameKeepPlayableApplicable => NoIntro.MameDriverStatuses != MameDriverStatuses.All;

    private void SetDriverStatus(MameDriverStatuses status, bool isKept) =>
        NoIntro.MameDriverStatuses = isKept ? NoIntro.MameDriverStatuses | status : NoIntro.MameDriverStatuses & ~status;

    /// <summary>
    /// Shows what the whole DAT says of its mechanical machines (<c>null</c>: not known, or none), to warn when the
    /// statuses kept leave none of those the options keep.
    /// </summary>
    public void ShowMameDat(MameDatSummary? summary)
    {
        _mameDat = summary;
        OnPropertyChanged(nameof(EmulationWarningText));
        OnPropertyChanged(nameof(EmulationWarningTooltip));
        OnPropertyChanged(nameof(HasEmulationWarning));
    }

    private MameDatSummary? _mameDat;

    /// <summary>
    /// Whether the mechanical machines are kept while no mechanical machine of the DAT has a status kept: the box then
    /// keeps nothing.
    /// </summary>
    public bool IsMechanicalNoneKeptShown =>
        KeepMameMechanical && _mameDat is { } dat && dat.KeepsNoMechanicalMachine(NoIntro.MameDriverStatuses, NoIntro.MameKeepPlayable);

    /// <summary>No status and no playable machine kept: only the machines without a status remain.</summary>
    public bool IsNoDriverStatusKept => NoIntro.MameDriverStatuses == MameDriverStatuses.None && !NoIntro.MameKeepPlayable;

    /// <summary>Sentence under the emulation boxes when they exclude more than they seem to; empty otherwise.</summary>
    public string EmulationWarningText =>
        IsNoDriverStatusKept ? _localization.Tr("filters.mame_no_driver_status_kept")
        : IsMechanicalNoneKeptShown ? _localization.Tr("filters.mame_mechanical_none_kept")
        : string.Empty;

    public string? EmulationWarningTooltip =>
        IsNoDriverStatusKept ? _localization.Tr("filters.tooltip_mame_no_driver_status_kept")
        : IsMechanicalNoneKeptShown ? _localization.Tr("filters.tooltip_mame_mechanical_none_kept")
        : null;

    public bool HasEmulationWarning => EmulationWarningText.Length > 0;

    public bool KeepMameUnemulatedSound
    {
        get => !NoIntro.ExcludeMameUnemulatedSound;
        set => NoIntro.ExcludeMameUnemulatedSound = !value;
    }

    /// <summary>Alternate MAME sets (<c>set 2</c>, <c>alt</c>), the only dump quality of a MAME set.</summary>
    public bool KeepMameAlternateSets
    {
        get => !NoIntro.ExcludeMameAlternateSets;
        set => NoIntro.ExcludeMameAlternateSets = !value;
    }

    // =========================================================================
    // Dump quality
    // =========================================================================

    /// <summary>Dump quality requirement of the shown convention.</summary>
    public DumpQualityLevel DumpLevel
    {
        get => IsTosecShown ? Tosec.DumpLevel : NoIntro.DumpLevel;
        set
        {
            if (IsTosecShown)
            {
                Tosec.DumpLevel = value;
            }
            else
            {
                NoIntro.DumpLevel = value;
            }
        }
    }

    /// <summary>Choices of the dump quality level, as radio buttons: setting false is ignored, as the new choice sets it.</summary>
    public bool IsDumpLevelAll
    {
        get => DumpLevel == DumpQualityLevel.All;
        set => ChooseDumpLevel(value, DumpQualityLevel.All);
    }

    public bool IsDumpLevelNoBadDumps
    {
        get => DumpLevel == DumpQualityLevel.NoBadDumps;
        set => ChooseDumpLevel(value, DumpQualityLevel.NoBadDumps);
    }

    public bool IsDumpLevelVerifiedOnly
    {
        get => DumpLevel == DumpQualityLevel.VerifiedOnly;
        set => ChooseDumpLevel(value, DumpQualityLevel.VerifiedOnly);
    }

    /// <summary>
    /// The integrity options match no level: a fourth choice, "Custom", is then shown and checked, so that the group always
    /// has a checked choice. It is not a level that can be chosen: the exclusions are set one by one in the detail.
    /// </summary>
    public bool IsDumpLevelCustom => DumpLevel == DumpQualityLevel.Custom;

    /// <summary>
    /// Whether "verified dumps only" is offered: only a TOSEC name marks a verified dump ([!]). For No-Intro / Redump,
    /// every dump that is not bad counts as verified, so the level does not exist.
    /// </summary>
    public bool IsDumpLevelVerifiedOnlyShown => IsTosecShown;

    // =========================================================================
    // Content to keep: each box keeps a category, the opposite of the exclude_* option it stands for
    // =========================================================================

    /// <summary>Modified versions: TOSEC cracks, trainers, hacks, pirates, fixes and modifications, or No-Intro hacks.</summary>
    public bool KeepModifications
    {
        get => !(IsTosecShown ? Tosec.ExcludeModifications : NoIntro.ExcludeHack);
        set
        {
            if (IsTosecShown)
            {
                Tosec.ExcludeModifications = !value;
            }
            else
            {
                NoIntro.ExcludeHack = !value;
            }
        }
    }

    public bool KeepTranslations
    {
        get => !(IsTosecShown ? Tosec.ExcludeTranslations : NoIntro.ExcludeTranslations);
        set
        {
            if (IsTosecShown)
            {
                Tosec.ExcludeTranslations = !value;
            }
            else
            {
                NoIntro.ExcludeTranslations = !value;
            }
        }
    }

    public bool KeepDevVersions
    {
        get => !(IsTosecShown ? Tosec.ExcludeDevVersions : NoIntro.ExcludeDevVersions);
        set
        {
            if (IsTosecShown)
            {
                Tosec.ExcludeDevVersions = !value;
            }
            else
            {
                NoIntro.ExcludeDevVersions = !value;
            }
        }
    }

    public bool KeepDemos
    {
        get => !(IsTosecShown ? Tosec.ExcludeDemos : NoIntro.ExcludeDemos);
        set
        {
            if (IsTosecShown)
            {
                Tosec.ExcludeDemos = !value;
            }
            else
            {
                NoIntro.ExcludeDemos = !value;
            }
        }
    }

    /// <summary>TOSEC public domain, freeware and shareware; shown for TOSEC only.</summary>
    public bool KeepPublicDomain
    {
        get => !Tosec.ExcludeFreePd;
        set => Tosec.ExcludeFreePd = !value;
    }

    /// <summary>No-Intro (Pirate) releases, which TOSEC counts among the modified versions; shown for No-Intro / Redump only.</summary>
    public bool KeepPirate
    {
        get => !NoIntro.ExcludePirate;
        set => NoIntro.ExcludePirate = !value;
    }

    /// <summary>No-Intro unofficial releases (Unl, Homebrew, Aftermarket); shown for No-Intro / Redump only.</summary>
    public bool KeepUnlicensed
    {
        get => !NoIntro.ExcludeUnlicensed;
        set => NoIntro.ExcludeUnlicensed = !value;
    }

    /// <summary>No-Intro modern re-releases (Virtual Console, Switch Online…); shown for No-Intro / Redump only.</summary>
    public bool KeepRereleases
    {
        get => !NoIntro.ExcludeRereleases;
        set => NoIntro.ExcludeRereleases = !value;
    }

    /// <summary>
    /// Complete sets only: TOSEC multi-disk games, or No-Intro and Redump multi-media sets. Already a positive wording:
    /// it keeps the sense of its option.
    /// </summary>
    public bool CompleteSetsOnly
    {
        get => IsTosecShown ? Tosec.CompleteMultidiskSetsOnly : NoIntro.CompleteMultimediaSetsOnly;
        set
        {
            if (IsTosecShown)
            {
                Tosec.CompleteMultidiskSetsOnly = value;
            }
            else
            {
                NoIntro.CompleteMultimediaSetsOnly = value;
            }
        }
    }

    // With the MAME options, the tags and tooltips name the words of a MAME description that MameDescriptionParser reads
    public string ModificationsTag => ShownText("filters.tag_tosec_modifications", "filters.tag_nointro_modifications", "filters.tag_mame_modifications");

    public string PirateTag => ShownText("filters.tag_nointro_pirate", "filters.tag_nointro_pirate", "filters.tag_mame_pirate");

    public string TranslationsTag => ShownText("filters.tag_tosec_translations", "filters.tag_nointro_translations");

    public string DevVersionsTag => ShownText("filters.tag_tosec_dev", "filters.tag_nointro_dev", "filters.tag_mame_dev");

    public string DemosTag => ShownText("filters.tag_tosec_demos", "filters.tag_nointro_demos", "filters.tag_mame_demos");

    public string ModificationsTooltip => ShownText(
        "filters.tooltip_keep_tosec_modifications", "filters.tooltip_keep_nointro_modifications", "filters.tooltip_keep_mame_modifications");

    public string PirateTooltip => ShownText(
        "filters.tooltip_keep_nointro_pirate", "filters.tooltip_keep_nointro_pirate", "filters.tooltip_keep_mame_pirate");

    public string TranslationsTooltip => ShownText(
        "filters.tooltip_keep_tosec_translations", "filters.tooltip_keep_nointro_translations", "filters.tooltip_no_effect_mame_category");

    public string UnlicensedTooltip => ShownText(
        "filters.tooltip_keep_nointro_unl", "filters.tooltip_keep_nointro_unl", "filters.tooltip_no_effect_mame_category",
        "filters.tooltip_no_effect_softlist_category");

    public string RereleasesTooltip => ShownText(
        "filters.tooltip_keep_nointro_rereleases", "filters.tooltip_keep_nointro_rereleases", "filters.tooltip_no_effect_mame_category",
        "filters.tooltip_no_effect_softlist_category");

    /// <summary>Why the dump quality has no effect, shown above its greyed group: on a MAME set, or on a software of a list.</summary>
    public string DumpsNoEffectText => ShownText(
        "filters.dumps_no_effect_mame", "filters.dumps_no_effect_mame", "filters.dumps_no_effect_mame", "filters.dumps_no_effect_softlist");

    public string DevVersionsTooltip => ShownText("filters.tooltip_keep_tosec_dev", "filters.tooltip_keep_nointro_dev", "filters.tooltip_keep_mame_dev");

    public string DemosTooltip => ShownText("filters.tooltip_keep_tosec_demos", "filters.tooltip_keep_nointro_demos", "filters.tooltip_keep_mame_demos");

    public string CompleteSetsTooltip => ShownText("filters.tooltip_complete_sets_tosec", "filters.tooltip_complete_sets_nointro");

    public void RefreshTranslations()
    {
        NotifyShownOptionsChanged();
        OnPropertyChanged(nameof(EmulationWarningText));
        OnPropertyChanged(nameof(EmulationWarningTooltip));
        Cabinet.RefreshTranslations();
        Categories.RefreshTranslations();
        Ratings.RefreshTranslations();
    }

    private void ChooseDumpLevel(bool isChecked, DumpQualityLevel level)
    {
        if (isChecked)
        {
            DumpLevel = level;
        }
    }

    private string ShownText(string tosecKey, string noIntroKey, string? mameKey = null, string? softwareListKey = null) =>
        _localization.Tr(
            IsTosecShown ? tosecKey
            : AreMameOptionsShown && mameKey is not null ? mameKey
            : AreSoftwareListOptionsShown && softwareListKey is not null ? softwareListKey
            : noIntroKey);

    private void OnSectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, IsTosecShown ? Tosec : NoIntro))
        {
            NotifyShownOptionsChanged();
        }

        if (ReferenceEquals(sender, NoIntro))
        {
            OnPropertyChanged(nameof(KeepMameBios));
            OnPropertyChanged(nameof(KeepMameDevices));
            OnPropertyChanged(nameof(KeepMameMechanical));
            OnPropertyChanged(nameof(KeepMameRequiresArtwork));
            OnPropertyChanged(nameof(KeepMameNothingToDump));
            OnPropertyChanged(nameof(KeepMameDriverGood));
            OnPropertyChanged(nameof(KeepMameDriverImperfect));
            OnPropertyChanged(nameof(KeepMameDriverPreliminary));
            OnPropertyChanged(nameof(MameKeepPlayable));
            OnPropertyChanged(nameof(IsMameKeepPlayableApplicable));
            OnPropertyChanged(nameof(IsMechanicalNoneKeptShown));
            OnPropertyChanged(nameof(IsNoDriverStatusKept));
            OnPropertyChanged(nameof(EmulationWarningText));
            OnPropertyChanged(nameof(EmulationWarningTooltip));
            OnPropertyChanged(nameof(HasEmulationWarning));
            OnPropertyChanged(nameof(KeepMameUnemulatedSound));
            OnPropertyChanged(nameof(KeepMameAlternateSets));
        }
    }

    /// <summary>Every property below depends on the shown convention and on the options of its section.</summary>
    private void NotifyShownOptionsChanged()
    {
        OnPropertyChanged(nameof(DumpLevel));
        OnPropertyChanged(nameof(IsDumpLevelAll));
        OnPropertyChanged(nameof(IsDumpLevelNoBadDumps));
        OnPropertyChanged(nameof(IsDumpLevelVerifiedOnly));
        OnPropertyChanged(nameof(IsDumpLevelCustom));
        OnPropertyChanged(nameof(IsDumpLevelVerifiedOnlyShown));
        OnPropertyChanged(nameof(KeepModifications));
        OnPropertyChanged(nameof(KeepTranslations));
        OnPropertyChanged(nameof(KeepDevVersions));
        OnPropertyChanged(nameof(KeepDemos));
        OnPropertyChanged(nameof(KeepPublicDomain));
        OnPropertyChanged(nameof(KeepPirate));
        OnPropertyChanged(nameof(KeepUnlicensed));
        OnPropertyChanged(nameof(KeepRereleases));
        OnPropertyChanged(nameof(CompleteSetsOnly));
        OnPropertyChanged(nameof(IsMameCardShown));
        OnPropertyChanged(nameof(AreSoftwareListOptionsShown));
        OnPropertyChanged(nameof(MameCardHint));
        OnPropertyChanged(nameof(AreTranslationsApplicable));
        OnPropertyChanged(nameof(IsDumpQualityApplicable));
        OnPropertyChanged(nameof(AreNoIntroOnlyOptionsApplicable));
        OnPropertyChanged(nameof(IsFilterNotInDatApplicable));
        OnPropertyChanged(nameof(FilterNotInDatTooltip));
        OnPropertyChanged(nameof(IsSmartMultidiskApplicable));
        OnPropertyChanged(nameof(UnlicensedTooltip));
        OnPropertyChanged(nameof(RereleasesTooltip));
        OnPropertyChanged(nameof(DumpsNoEffectText));
        OnPropertyChanged(nameof(ModificationsTag));
        OnPropertyChanged(nameof(PirateTag));
        OnPropertyChanged(nameof(TranslationsTag));
        OnPropertyChanged(nameof(DevVersionsTag));
        OnPropertyChanged(nameof(DemosTag));
        OnPropertyChanged(nameof(ModificationsTooltip));
        OnPropertyChanged(nameof(PirateTooltip));
        OnPropertyChanged(nameof(TranslationsTooltip));
        OnPropertyChanged(nameof(DevVersionsTooltip));
        OnPropertyChanged(nameof(DemosTooltip));
        OnPropertyChanged(nameof(CompleteSetsTooltip));
    }
}
