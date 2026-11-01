using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Languages, regions and 1G1R tab: language and country filters, their ordered priority lists
/// (kept in sync with their comma-separated text), the 1G1R deduplication, its presets and the orders of its criteria,
/// one per convention, with the preferences their criteria compare the releases to.
/// </summary>
public sealed partial class PrioritiesViewModel : ConfigSectionViewModel
{
    private readonly ILocalizationService _localization;
    private readonly TosecFiltersViewModel? _tosecFilters;
    private bool _suppressLanguageSync;
    private bool _suppressCountrySync;
    private bool _syncingRegionMode;

    /// <param name="localization">Translations.</param>
    /// <param name="tosecFilters">
    /// Section that saves the preferred media type of the TOSEC names, set on the line of its criterion; without it,
    /// the criterion has no preference on its line.
    /// </param>
    public PrioritiesViewModel(ILocalizationService localization, TosecFiltersViewModel? tosecFilters = null)
    {
        _localization = localization;
        _tosecFilters = tosecFilters;
        LanguagePriority = new LanguagePriorityViewModel(localization);
        CountryPriority = new CountryPriorityViewModel(localization);

        VideoStandardPreference = TiebreakPreferenceViewModel.Create(
            localization,
            "tab3.tooltip_preferred_video",
            () => PreferredVideo,
            value => PreferredVideo = value,
            (VideoPreference.None, "tab3.video_none"),
            (VideoPreference.Pal, "tab3.video_pal"),
            (VideoPreference.Ntsc, "tab3.video_ntsc"));
        LanguageCountPreference = TiebreakPreferenceViewModel.Create(
            localization,
            "tab3.tooltip_prefer_monolingual",
            () => PreferMonolingual,
            value => PreferMonolingual = value,
            (true, "tab3.prefer_monolingual"),
            (false, "tab3.prefer_multilingual"));
        if (tosecFilters is not null)
        {
            MediaTypePreference = TiebreakPreferenceViewModel.Create(
                localization,
                "tab2.tooltip_preferred_media",
                () => tosecFilters.PreferredMediaType,
                value => tosecFilters.PreferredMediaType = value,
                (MediaPreference.None, "tab3.media_none"),
                (MediaPreference.Floppy, "tab3.media_floppy"),
                (MediaPreference.Tape, "tab3.media_tape"),
                (MediaPreference.Cartridge, "tab3.media_cartridge"),
                (MediaPreference.Cd, "tab3.media_cd"),
                (MediaPreference.Hd, "tab3.media_hd"));
            tosecFilters.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TosecFiltersViewModel.PreferredMediaType))
                {
                    MediaTypePreference.Refresh();
                    ShowPreferencesInOrders();
                }
            };
        }

        _languagesText = string.Join(", ", LanguagePriority.GetPriorityCodes());
        _countriesText = string.Join(", ", CountryPriority.GetPriorityCodes());
        _groupLanguagesTitle = localization.Tr("tab3.subtab_languages");
        _groupCountriesTitle = localization.Tr("tab3.subtab_countries");

        LanguagePriority.PriorityChanged += OnLanguagePriorityListChanged;
        CountryPriority.PriorityChanged += OnCountryPriorityListChanged;

        TosecCriteriaOrder = CreateOrder(NamingConvention.Tosec);
        NoIntroCriteriaOrder = CreateOrder(NamingConvention.NoIntro);
        RedumpCriteriaOrder = CreateOrder(NamingConvention.Redump);
        MameCriteriaOrder = CreateOrder(NamingConvention.Mame);
        SoftwareListCriteriaOrder = CreateOrder(NamingConvention.MameSoftwareList);
        foreach (var order in RegionalCriteriaOrders)
        {
            order.OrderChanged += OnRegionalCriteriaOrderChanged;
        }

        MameCriteriaOrder.OrderChanged += (_, _) => OnPropertyChanged(nameof(RegionModeSummary));
        ShowPreferencesInOrders();
        OnShownOrderConventionChanged(ShownOrderConvention);
    }

    private TiebreakOrderViewModel CreateOrder(NamingConvention convention) =>
        new(_localization, convention, () => TiebreakOrder.Default(convention, RegionPriorityMode), PreferenceOf);

    /// <summary>Preference that <paramref name="criterion"/> compares the releases to, set on its line, or <c>null</c>.</summary>
    private TiebreakPreferenceViewModel? PreferenceOf(TiebreakCriterion criterion) => criterion switch
    {
        TiebreakCriterion.VideoStandard => VideoStandardPreference,
        TiebreakCriterion.PreferredMediaType => MediaTypePreference,
        TiebreakCriterion.LanguageCount => LanguageCountPreference,
        _ => null
    };

    /// <summary>Order of the 1G1R criteria of the TOSEC names (and of the names of no known convention), kept consistent with <see cref="RegionPriorityMode"/>.</summary>
    public TiebreakOrderViewModel TosecCriteriaOrder { get; }

    /// <summary>Order of the 1G1R criteria of the No-Intro names, kept consistent with <see cref="RegionPriorityMode"/>.</summary>
    public TiebreakOrderViewModel NoIntroCriteriaOrder { get; }

    /// <summary>Order of the 1G1R criteria of the Redump names, kept consistent with <see cref="RegionPriorityMode"/>.</summary>
    public TiebreakOrderViewModel RedumpCriteriaOrder { get; }

    /// <summary>Order of the 1G1R criteria of the MAME sets (<c>mame_tiebreak_order</c>), without effect on the region mode.</summary>
    public TiebreakOrderViewModel MameCriteriaOrder { get; }

    /// <summary>
    /// Order of the 1G1R criteria of the software of the MAME lists (<c>tiebreak_order_softlist</c>), kept consistent
    /// with <see cref="RegionPriorityMode"/>.
    /// </summary>
    public TiebreakOrderViewModel SoftwareListCriteriaOrder { get; }

    /// <summary>The orders whose place of the language and the country is the region mode.</summary>
    private IEnumerable<TiebreakOrderViewModel> RegionalCriteriaOrders =>
        [TosecCriteriaOrder, NoIntroCriteriaOrder, RedumpCriteriaOrder, SoftwareListCriteriaOrder];

    /// <summary>Orders of every convention.</summary>
    public IReadOnlyList<TiebreakOrderViewModel> CriteriaOrders =>
        [TosecCriteriaOrder, NoIntroCriteriaOrder, RedumpCriteriaOrder, MameCriteriaOrder, SoftwareListCriteriaOrder];

    /// <summary>Order of the releases of <paramref name="convention"/> (<see cref="TiebreakOrder.OrderConventionOf"/>).</summary>
    public TiebreakOrderViewModel CriteriaOrderOf(NamingConvention convention) => TiebreakOrder.OrderConventionOf(convention) switch
    {
        NamingConvention.NoIntro => NoIntroCriteriaOrder,
        NamingConvention.Redump => RedumpCriteriaOrder,
        NamingConvention.Mame => MameCriteriaOrder,
        NamingConvention.MameSoftwareList => SoftwareListCriteriaOrder,
        _ => TosecCriteriaOrder
    };

    /// <summary>Preferred video standard, set on the line of its criterion.</summary>
    public TiebreakPreferenceViewModel VideoStandardPreference { get; }

    /// <summary>Single language or several, set on the line of its criterion.</summary>
    public TiebreakPreferenceViewModel LanguageCountPreference { get; }

    /// <summary>Preferred media type of the TOSEC names, set on the line of its criterion, or <c>null</c> without the TOSEC section.</summary>
    public TiebreakPreferenceViewModel? MediaTypePreference { get; }

    /// <summary>
    /// Convention whose order the panel of the "Order of the criteria" button shows ("Settings for:"): the effective
    /// convention (<see cref="ShowConvention"/>), or the last one chosen while it is not known, and any other by hand.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownCriteriaOrder))]
    [NotifyPropertyChangedFor(nameof(IsTosecOrderShown))]
    [NotifyPropertyChangedFor(nameof(IsNoIntroOrderShown))]
    [NotifyPropertyChangedFor(nameof(IsRedumpOrderShown))]
    [NotifyPropertyChangedFor(nameof(IsMameOrderShown))]
    [NotifyPropertyChangedFor(nameof(IsSoftwareListOrderShown))]
    [NotifyPropertyChangedFor(nameof(CriteriaOrderTooltip))]
    [NotifyPropertyChangedFor(nameof(CriteriaOrderResetTooltip))]
    [NotifyPropertyChangedFor(nameof(RegionModeSummary))]
    private NamingConvention _shownOrderConvention = NamingConvention.Tosec;

    partial void OnShownOrderConventionChanged(NamingConvention value)
    {
        foreach (var order in CriteriaOrders)
        {
            order.IsShown = order == CriteriaOrderOf(value);
        }
    }

    public bool IsTosecOrderShown
    {
        get => ShownOrderConvention == NamingConvention.Tosec;
        set { if (value) ShownOrderConvention = NamingConvention.Tosec; }
    }

    public bool IsNoIntroOrderShown
    {
        get => ShownOrderConvention == NamingConvention.NoIntro;
        set { if (value) ShownOrderConvention = NamingConvention.NoIntro; }
    }

    public bool IsRedumpOrderShown
    {
        get => ShownOrderConvention == NamingConvention.Redump;
        set { if (value) ShownOrderConvention = NamingConvention.Redump; }
    }

    /// <summary>Whether the panel shows the order of the MAME sets, which never sets the region mode.</summary>
    public bool IsMameOrderShown
    {
        get => ShownOrderConvention == NamingConvention.Mame;
        set { if (value) ShownOrderConvention = NamingConvention.Mame; }
    }

    /// <summary>Whether the panel shows the order of the software of the MAME lists.</summary>
    public bool IsSoftwareListOrderShown
    {
        get => ShownOrderConvention == NamingConvention.MameSoftwareList;
        set { if (value) ShownOrderConvention = NamingConvention.MameSoftwareList; }
    }

    /// <summary>Order edited by the panel of the "Order of the criteria" button.</summary>
    public TiebreakOrderViewModel ShownCriteriaOrder => CriteriaOrderOf(ShownOrderConvention);

    public string CriteriaOrderTitle => _localization.Tr("tab3.lbl_tiebreak_order");

    public string CriteriaOrderResetTooltip => _localization.Tr(IsMameOrderShown ? "tab3.tooltip_tiebreak_reset_mame" : "tab3.tooltip_tiebreak_reset");

    public string CriteriaOrderTooltip => _localization.Tr(IsMameOrderShown ? "tab3.tooltip_tiebreak_order_mame" : "tab3.tooltip_tiebreak_order");

    public LanguagePriorityViewModel LanguagePriority { get; }
    public CountryPriorityViewModel CountryPriority { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Are1G1ROptionsEnabled))]
    [NotifyPropertyChangedFor(nameof(OneGameOneRomHint))]
    private bool _oneGameOneRom;

    /// <summary>
    /// Whether the language or the country comes first. The view has no selector of its own for it: it follows the
    /// orders of the criteria of TOSEC, No-Intro and Redump, kept consistent with one another, and is still saved as
    /// <c>region_priority_mode</c>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RegionModeSummary))]
    private RegionPriorityMode _regionPriorityMode = RegionPriorityMode.LanguageFirst;

    [ObservableProperty]
    private VideoPreference _preferredVideo = VideoPreference.None;

    [ObservableProperty]
    private bool _preferMonolingual = true;

    partial void OnPreferredVideoChanged(VideoPreference value)
    {
        VideoStandardPreference.Refresh();
        ShowPreferencesInOrders();
    }

    partial void OnPreferMonolingualChanged(bool value) => LanguageCountPreference.Refresh();

    /// <summary>Greys out in every order the criteria whose preference is left empty.</summary>
    private void ShowPreferencesInOrders()
    {
        foreach (var order in CriteriaOrders)
        {
            order.ShowPreferences(PreferredVideo, _tosecFilters?.PreferredMediaType);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFallbackLanguagesEnabled))]
    private bool _filterLanguages;

    [ObservableProperty]
    private bool _fallbackUnmatchedLanguages = true;

    [ObservableProperty]
    private string _languagesText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFallbackCountriesEnabled))]
    private bool _filterCountries;

    [ObservableProperty]
    private bool _fallbackUnmatchedCountries = true;

    [ObservableProperty]
    private string _countriesText;

    [ObservableProperty]
    private string _groupLanguagesTitle;

    [ObservableProperty]
    private string _groupCountriesTitle;

    /// <summary>Says which of the language and the country comes first, as the order of the criteria sets it.</summary>
    public string RegionModeSummary => _localization.Tr(
        TiebreakOrder.ImpliedRegionMode(ShownCriteriaOrder.Order) == RegionPriorityMode.CountryFirst
        ? "tab3.region_mode_country_first"
        : "tab3.region_mode_language_first");

    /// <summary>Says what the 1G1R deduplication does, or what turning it off keeps.</summary>
    public string OneGameOneRomHint => _localization.Tr(OneGameOneRom ? "tab3.hint_one_game_one_rom_on" : "tab3.hint_one_game_one_rom_off");

    /// <summary>
    /// Whether the 1G1R option that only applies to TOSEC names (the modified versions to fall back on) has an effect:
    /// while the effective convention is TOSEC or still unknown. Otherwise it stays in sight, greyed with a sentence that
    /// says why, and keeps its value.
    /// </summary>
    [ObservableProperty]
    private bool _areTosecOptionsApplicable = true;

    /// <summary>
    /// Shows next to the criteria of every order the games they decided in the current simulation, or nothing for
    /// <c>null</c> (<see cref="TiebreakOrderViewModel.ShowDecisions"/>).
    /// </summary>
    public void ShowDecisions(IReadOnlyDictionary<TiebreakCriterion, int>? decisions)
    {
        foreach (var order in CriteriaOrders)
        {
            order.ShowDecisions(decisions);
        }
    }

    /// <summary>
    /// Greys out the TOSEC option (<see cref="AreTosecOptionsApplicable"/>), shows the order of the effective
    /// convention (<see cref="ShownOrderConvention"/>; the last one shown while it is not known), and greys out the
    /// criteria that have no effect on a collection sorted with a DAT whose parent / clone relations are
    /// <paramref name="cloneRelations"/>.
    /// </summary>
    public void ShowConvention(NamingConvention convention, DatCloneRelations cloneRelations = DatCloneRelations.Unknown)
    {
        AreTosecOptionsApplicable = convention is NamingConvention.Tosec or NamingConvention.Auto;
        if (TiebreakOrder.Conventions.Contains(convention))
        {
            ShownOrderConvention = convention;
        }

        foreach (var order in CriteriaOrders)
        {
            order.ShowApplicability(cloneRelations);
        }
    }

    /// <summary>Greys out the chipset criterion unless the collection is sorted for an AGA Amiga.</summary>
    public void ShowAmigaTarget(AmigaTarget target)
    {
        foreach (var order in CriteriaOrders)
        {
            order.ShowAmigaTarget(target);
        }
    }

    /// <summary>Options that only apply to the 1G1R deduplication (including the TOSEC options of the section).</summary>
    public bool Are1G1ROptionsEnabled => !IsRunning && OneGameOneRom;
    public bool IsFallbackLanguagesEnabled => !IsRunning && FilterLanguages;
    public bool IsFallbackCountriesEnabled => !IsRunning && FilterCountries;

    protected override void OnRunningStateChanged()
    {
        LanguagePriority.IsEnabled = !IsRunning;
        CountryPriority.IsEnabled = !IsRunning;
        foreach (var order in CriteriaOrders)
        {
            order.IsEnabled = !IsRunning;
        }

        foreach (var preference in Preferences)
        {
            preference.IsEnabled = !IsRunning;
        }

        OnPropertyChanged(nameof(Are1G1ROptionsEnabled));
        OnPropertyChanged(nameof(IsFallbackLanguagesEnabled));
        OnPropertyChanged(nameof(IsFallbackCountriesEnabled));
    }

    private IEnumerable<TiebreakPreferenceViewModel> Preferences =>
        new[] { VideoStandardPreference, LanguageCountPreference, MediaTypePreference }.OfType<TiebreakPreferenceViewModel>();

    /// <summary>
    /// The region mode places the language and the country in the orders of TOSEC, No-Intro and Redump
    /// (<see cref="TiebreakOrder.ApplyRegionMode"/>): a default order becomes the default order of the new mode.
    /// </summary>
    partial void OnRegionPriorityModeChanged(RegionPriorityMode value)
    {
        if (_syncingRegionMode)
        {
            return;
        }

        _syncingRegionMode = true;
        try
        {
            ApplyRegionMode(value, except: null);
        }
        finally
        {
            _syncingRegionMode = false;
        }
    }

    private void ApplyRegionMode(RegionPriorityMode mode, TiebreakOrderViewModel? except)
    {
        foreach (var order in RegionalCriteriaOrders.Where(o => o != except))
        {
            order.SetOrder(TiebreakOrder.ApplyRegionMode(order.Order, order.Convention, mode));
        }
    }

    /// <summary>
    /// Putting the country above the language in the order of TOSEC, No-Intro or Redump selects the "country first"
    /// mode, and conversely; the two other orders follow it.
    /// </summary>
    private void OnRegionalCriteriaOrderChanged(object? sender, IReadOnlyList<TiebreakCriterion> order)
    {
        if (_syncingRegionMode)
        {
            return;
        }

        _syncingRegionMode = true;
        try
        {
            var mode = TiebreakOrder.ImpliedRegionMode(order);
            ApplyRegionMode(mode, except: sender as TiebreakOrderViewModel);
            RegionPriorityMode = mode;
        }
        finally
        {
            _syncingRegionMode = false;
        }

        OnPropertyChanged(nameof(RegionModeSummary));
    }

    partial void OnOneGameOneRomChanged(bool value)
    {
        if (value && !FilterLanguages)
        {
            FilterLanguages = true;
        }
    }

    partial void OnLanguagesTextChanged(string value)
    {
        if (_suppressLanguageSync)
        {
            return;
        }

        _suppressLanguageSync = true;
        try
        {
            var codes = value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(c => c.ToLowerInvariant())
                .Where(c => !string.IsNullOrEmpty(c))
                .ToList();

            LanguagePriority.SetPriorityCodes(codes, notify: false);
            if (codes.Count > 0 && !FilterLanguages)
            {
                FilterLanguages = true;
            }
        }
        finally
        {
            _suppressLanguageSync = false;
        }
    }

    partial void OnCountriesTextChanged(string value)
    {
        if (_suppressCountrySync)
        {
            return;
        }

        _suppressCountrySync = true;
        try
        {
            var codes = value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(c => c.ToUpperInvariant())
                .Where(c => !string.IsNullOrEmpty(c))
                .ToList();

            CountryPriority.SetPriorityCodes(codes, notify: false);
        }
        finally
        {
            _suppressCountrySync = false;
        }
    }

    private void OnLanguagePriorityListChanged(object? sender, IReadOnlyList<string> codes)
    {
        if (_suppressLanguageSync)
        {
            return;
        }

        _suppressLanguageSync = true;
        try
        {
            LanguagesText = string.Join(", ", codes);
            if (codes.Count > 0 && !FilterLanguages)
            {
                FilterLanguages = true;
            }
        }
        finally
        {
            _suppressLanguageSync = false;
        }
    }

    private void OnCountryPriorityListChanged(object? sender, IReadOnlyList<string> codes)
    {
        if (_suppressCountrySync)
        {
            return;
        }

        _suppressCountrySync = true;
        try
        {
            CountriesText = string.Join(", ", codes);
        }
        finally
        {
            _suppressCountrySync = false;
        }
    }

    [RelayCommand]
    public void Apply1G1RPreset(string presetName)
    {
        FilterLanguages = true;
        OneGameOneRom = true;

        var key = presetName?.Trim().ToLowerInvariant();
        if (key == "france")
        {
            LanguagePriority.SetPriorityCodes(["fr", "m", "en"]);
            CountryPriority.SetPriorityCodes(["FR", "EU", "WO", "US", "GB", "CA", "AU", "JP"]);
            PreferredVideo = VideoPreference.Pal;
            PreferMonolingual = true;
        }
        else if (key == "europe")
        {
            LanguagePriority.SetPriorityCodes(["m", "fr", "en", "de", "es", "it"]);
            CountryPriority.SetPriorityCodes(["EU", "WO", "FR", "GB", "DE", "ES", "IT", "US", "AU", "JP"]);
            PreferredVideo = VideoPreference.Pal;
            PreferMonolingual = false;
        }
        else if (key == "usa")
        {
            LanguagePriority.SetPriorityCodes(["en", "m"]);
            CountryPriority.SetPriorityCodes(["US", "WO", "CA", "EU", "GB", "AU", "JP"]);
            PreferredVideo = VideoPreference.Ntsc;
            PreferMonolingual = true;
        }
        else if (key == "japan")
        {
            LanguagePriority.SetPriorityCodes(["ja", "en"]);
            CountryPriority.SetPriorityCodes(["JP", "AS", "WO", "US", "EU"]);
            PreferredVideo = VideoPreference.Ntsc;
            PreferMonolingual = true;
        }
    }

    public override void WriteTo(SortSettings settings)
    {
        settings.FilterLanguages = FilterLanguages;
        settings.FallbackUnmatchedLanguages = FallbackUnmatchedLanguages;
        settings.PriorityLanguages = LanguagePriority.GetPriorityCodes().ToList();
        settings.FilterCountries = FilterCountries;
        settings.FallbackUnmatchedCountries = FallbackUnmatchedCountries;
        settings.PriorityCountries = CountryPriority.GetPriorityCodes().ToList();
        settings.RegionPriorityMode = RegionPriorityMode;
        settings.TosecTiebreakOrder = TosecCriteriaOrder.Order.ToList();
        settings.NoIntroTiebreakOrder = NoIntroCriteriaOrder.Order.ToList();
        settings.RedumpTiebreakOrder = RedumpCriteriaOrder.Order.ToList();
        settings.MameTiebreakOrder = MameCriteriaOrder.Order.ToList();
        settings.SoftwareListTiebreakOrder = SoftwareListCriteriaOrder.Order.ToList();
        settings.OneGameOneRom = OneGameOneRom;
        settings.PreferredVideo = PreferredVideo;
        settings.PreferMonolingual = PreferMonolingual;
    }

    /// <summary>
    /// Shows the settings; the language filter is set last, since the priority languages and the 1G1R option
    /// turn it on as they change.
    /// </summary>
    public override void ReadFrom(SortSettings settings)
    {
        LanguagePriority.SetPriorityCodes(settings.PriorityLanguages);
        CountryPriority.SetPriorityCodes(settings.PriorityCountries);

        // The orders are set after the region mode, whose change would otherwise rearrange them
        _syncingRegionMode = true;
        try
        {
            RegionPriorityMode = settings.RegionPriorityMode;
            TosecCriteriaOrder.SetOrder(settings.TosecTiebreakOrder);
            NoIntroCriteriaOrder.SetOrder(settings.NoIntroTiebreakOrder);
            RedumpCriteriaOrder.SetOrder(settings.RedumpTiebreakOrder);
            SoftwareListCriteriaOrder.SetOrder(settings.SoftwareListTiebreakOrder);
        }
        finally
        {
            _syncingRegionMode = false;
        }

        MameCriteriaOrder.SetOrder(settings.MameTiebreakOrder);
        OnPropertyChanged(nameof(RegionModeSummary));

        OneGameOneRom = settings.OneGameOneRom;
        FilterLanguages = settings.FilterLanguages;
        FallbackUnmatchedLanguages = settings.FallbackUnmatchedLanguages;
        FilterCountries = settings.FilterCountries;
        FallbackUnmatchedCountries = settings.FallbackUnmatchedCountries;
        PreferredVideo = settings.PreferredVideo;
        PreferMonolingual = settings.PreferMonolingual;
    }

    public override void RefreshTranslations()
    {
        OnPropertyChanged(nameof(RegionModeSummary));
        OnPropertyChanged(nameof(OneGameOneRomHint));
        OnPropertyChanged(nameof(CriteriaOrderTitle));
        OnPropertyChanged(nameof(CriteriaOrderTooltip));
        OnPropertyChanged(nameof(CriteriaOrderResetTooltip));
        foreach (var preference in Preferences)
        {
            preference.RefreshTranslations();
        }

        GroupLanguagesTitle = _localization.Tr("tab3.subtab_languages");
        GroupCountriesTitle = _localization.Tr("tab3.subtab_countries");
        LanguagePriority.RefreshTranslations();
        CountryPriority.RefreshTranslations();
        foreach (var order in CriteriaOrders)
        {
            order.RefreshTranslations();
        }
    }
}
