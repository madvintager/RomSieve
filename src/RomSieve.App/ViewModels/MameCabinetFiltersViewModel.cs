using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Cabinet filters of the MAME machines in the Filters page: orientation and type of the screen, fewest players, most
/// buttons (in the drop-down panel of the cabinet button, <see cref="SummaryText"/>) and controls the panel offers (in
/// that of the controls button). The options stay in <see cref="NoIntroFiltersViewModel"/>, which saves them
/// with the other MAME options: this ViewModel only presents them, the controls as what the panel has (the opposite of
/// <c>mame_excluded_controls</c>).
/// </summary>
public sealed class MameCabinetFiltersViewModel : ViewModelBase
{
    /// <summary>Choices of the fewest players, 0 for any number.</summary>
    public static readonly IReadOnlyList<int> PlayerChoices = [0, 2, 3, 4];

    /// <summary>Choices of the most buttons, 0 for any number.</summary>
    public static readonly IReadOnlyList<int> ButtonChoices = [0, 1, 2, 3, 4, 6];

    private readonly ILocalizationService _localization;

    public MameCabinetFiltersViewModel(ILocalizationService localization, NoIntroFiltersViewModel noIntro)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        NoIntro = noIntro ?? throw new ArgumentNullException(nameof(noIntro));
        Controls = new ObservableCollection<MameControlOptionViewModel>(
            EnumExtensions.MameControlKinds.Select(kind => new MameControlOptionViewModel(this, kind)));
        OrientationOptions = new LocalizedOptionList<MameOrientationRequirement>(
            localization,
            (MameOrientationRequirement.Any, "filters.mame_orientation_any"),
            (MameOrientationRequirement.Horizontal, "filters.mame_orientation_horizontal"),
            (MameOrientationRequirement.Vertical, "filters.mame_orientation_vertical"));
        PlayerOptions = new ObservableCollection<SelectOptionItem<int>>(PlayerChoices.Select(n => new SelectOptionItem<int>(n, PlayersLabel(n))));
        ButtonOptions = new ObservableCollection<SelectOptionItem<int>>(ButtonChoices.Select(n => new SelectOptionItem<int>(n, ButtonsLabel(n))));
        RefreshControlLabels();
        NoIntro.PropertyChanged += OnNoIntroPropertyChanged;
    }

    public NoIntroFiltersViewModel NoIntro { get; }

    /// <summary>Kinds of controls, checked when the panel has them.</summary>
    public ObservableCollection<MameControlOptionViewModel> Controls { get; }

    public LocalizedOptionList<MameOrientationRequirement> OrientationOptions { get; }

    public ObservableCollection<SelectOptionItem<int>> PlayerOptions { get; }

    public ObservableCollection<SelectOptionItem<int>> ButtonOptions { get; }

    public MameOrientationRequirement Orientation
    {
        get => NoIntro.MameOrientation;
        set => NoIntro.MameOrientation = value;
    }

    public bool KeepVector
    {
        get => !NoIntro.ExcludeMameVector;
        set => NoIntro.ExcludeMameVector = !value;
    }

    public bool KeepLcd
    {
        get => !NoIntro.ExcludeMameLcd;
        set => NoIntro.ExcludeMameLcd = !value;
    }

    /// <summary>Fewest players; a value outside <see cref="PlayerChoices"/> (edited <c>config.json</c>) is kept as it is.</summary>
    public int MinimumPlayers
    {
        get => NoIntro.MameMinimumPlayers;
        set => NoIntro.MameMinimumPlayers = Math.Max(value, 0);
    }

    public int MaximumButtons
    {
        get => NoIntro.MameMaximumButtons;
        set => NoIntro.MameMaximumButtons = Math.Max(value, 0);
    }

    /// <summary>Whether a screen, players or buttons option excludes machines: the cabinet button is then emphasized.</summary>
    public bool IsCabinetRestricted => CabinetRestrictions().Count > 0;

    /// <summary>Text of the cabinet button (screen, players, buttons): every cabinet, its only restriction, or their count.</summary>
    public string SummaryText => MameRefineSummary.Text(
        _localization, CabinetRestrictions(), "filters.btn_mame_cabinet_all", "filters.btn_mame_cabinet_one", "filters.btn_mame_cabinet_many");

    /// <summary>Tooltip of the cabinet button, which names its restrictions.</summary>
    public string SummaryTooltip => MameRefineSummary.Tooltip(_localization, _localization.Tr("filters.tooltip_mame_cabinet"), CabinetRestrictions());

    /// <summary>Whether the panel lacks a control: the controls button is then emphasized.</summary>
    public bool AreControlsRestricted => NoIntro.MameExcludedControls != MameControls.None;

    /// <summary>Text of the controls button: every control, or how many the panel has.</summary>
    public string ControlsText
    {
        get
        {
            var available = Controls.Count(c => c.IsAvailable);
            return available == Controls.Count
                ? _localization.Tr("filters.mame_controls_all")
                : _localization.Tr("filters.mame_controls_some", ("count", available), ("total", Controls.Count));
        }
    }

    /// <summary>Tooltip of the controls button, which names the controls the panel lacks.</summary>
    public string ControlsTooltip
    {
        get
        {
            var missing = Controls.Where(c => !c.IsAvailable).Select(c => c.Label).ToList();
            var tooltip = _localization.Tr("filters.tooltip_mame_controls");
            return missing.Count == 0
                ? tooltip
                : tooltip + Environment.NewLine + Environment.NewLine +
                  _localization.Tr("filters.mame_controls_missing", ("names", string.Join(", ", missing)));
        }
    }

    public void RefreshTranslations()
    {
        for (var i = 0; i < PlayerChoices.Count; i++)
        {
            PlayerOptions[i].Label = PlayersLabel(PlayerChoices[i]);
        }

        for (var i = 0; i < ButtonChoices.Count; i++)
        {
            ButtonOptions[i].Label = ButtonsLabel(ButtonChoices[i]);
        }

        OrientationOptions.RefreshTranslations();
        RefreshControlLabels();
        OnPropertyChanged(nameof(ControlsText));
        OnPropertyChanged(nameof(ControlsTooltip));
        NotifySummaryChanged();
    }

    internal bool IsControlAvailable(MameControls kind) => !NoIntro.MameExcludedControls.HasFlag(kind);

    internal void SetControlAvailable(MameControls kind, bool isAvailable) =>
        NoIntro.MameExcludedControls = isAvailable
            ? NoIntro.MameExcludedControls & ~kind
            : NoIntro.MameExcludedControls | kind;

    private string PlayersLabel(int players) =>
        players == 0
            ? _localization.Tr("filters.mame_players_any")
            : _localization.Tr("filters.mame_players_at_least", ("count", players));

    private string ButtonsLabel(int buttons) =>
        buttons == 0
            ? _localization.Tr("filters.mame_buttons_any")
            : _localization.Tr("filters.mame_buttons_at_most", ("count", buttons));

    /// <summary>Restrictions of the screen, players and buttons options, in the order of the panel.</summary>
    private List<string> CabinetRestrictions()
    {
        var restrictions = new List<string>();
        switch (NoIntro.MameOrientation)
        {
            case MameOrientationRequirement.Horizontal:
                restrictions.Add(_localization.Tr("filters.mame_restriction_horizontal"));
                break;
            case MameOrientationRequirement.Vertical:
                restrictions.Add(_localization.Tr("filters.mame_restriction_vertical"));
                break;
        }

        if (NoIntro.ExcludeMameVector)
        {
            restrictions.Add(_localization.Tr("filters.mame_restriction_no_vector"));
        }

        if (NoIntro.ExcludeMameLcd)
        {
            restrictions.Add(_localization.Tr("filters.mame_restriction_no_lcd"));
        }

        if (NoIntro.MameMinimumPlayers > 0)
        {
            restrictions.Add(_localization.Tr("filters.mame_restriction_players", ("count", NoIntro.MameMinimumPlayers)));
        }

        if (NoIntro.MameMaximumButtons > 0)
        {
            restrictions.Add(_localization.Tr("filters.mame_restriction_buttons", ("count", NoIntro.MameMaximumButtons)));
        }

        return restrictions;
    }

    private void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(IsCabinetRestricted));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(SummaryTooltip));
    }

    private void RefreshControlLabels()
    {
        foreach (var control in Controls)
        {
            control.Label = _localization.Format(control.Kind.ToMessage());
            control.Tooltip = _localization.Tr(TooltipKey(control.Kind));
        }
    }

    private static string TooltipKey(MameControls kind) =>
        kind switch
        {
            MameControls.Joystick => "filters.tooltip_mame_control_joystick",
            MameControls.AnalogStick => "filters.tooltip_mame_control_analog_stick",
            MameControls.Trackball => "filters.tooltip_mame_control_trackball",
            MameControls.Spinner => "filters.tooltip_mame_control_spinner",
            MameControls.Paddle => "filters.tooltip_mame_control_paddle",
            MameControls.Pedal => "filters.tooltip_mame_control_pedal",
            MameControls.Lightgun => "filters.tooltip_mame_control_lightgun",
            MameControls.Keyboard => "filters.tooltip_mame_control_keyboard",
            _ => "filters.tooltip_mame_control_gambling_panel"
        };

    private void OnNoIntroPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NoIntroFiltersViewModel.MameOrientation):
                OnPropertyChanged(nameof(Orientation));
                NotifySummaryChanged();
                break;
            case nameof(NoIntroFiltersViewModel.ExcludeMameVector):
                OnPropertyChanged(nameof(KeepVector));
                NotifySummaryChanged();
                break;
            case nameof(NoIntroFiltersViewModel.ExcludeMameLcd):
                OnPropertyChanged(nameof(KeepLcd));
                NotifySummaryChanged();
                break;
            case nameof(NoIntroFiltersViewModel.MameMinimumPlayers):
                OnPropertyChanged(nameof(MinimumPlayers));
                NotifySummaryChanged();
                break;
            case nameof(NoIntroFiltersViewModel.MameMaximumButtons):
                OnPropertyChanged(nameof(MaximumButtons));
                NotifySummaryChanged();
                break;
            case nameof(NoIntroFiltersViewModel.MameExcludedControls):
                foreach (var control in Controls)
                {
                    control.NotifyAvailabilityChanged();
                }
                OnPropertyChanged(nameof(AreControlsRestricted));
                OnPropertyChanged(nameof(ControlsText));
                OnPropertyChanged(nameof(ControlsTooltip));
                break;
        }
    }
}

/// <summary>A kind of controls in the controls panel of the MAME filters: checked when the control panel has it.</summary>
public sealed partial class MameControlOptionViewModel : ObservableObject
{
    private readonly MameCabinetFiltersViewModel _owner;

    public MameControlOptionViewModel(MameCabinetFiltersViewModel owner, MameControls kind)
    {
        _owner = owner;
        Kind = kind;
    }

    public MameControls Kind { get; }

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private string _tooltip = string.Empty;

    public bool IsAvailable
    {
        get => _owner.IsControlAvailable(Kind);
        set => _owner.SetControlAvailable(Kind, value);
    }

    internal void NotifyAvailabilityChanged() => OnPropertyChanged(nameof(IsAvailable));
}
