using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RomSieve.App.Abstractions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Preferences window: language and theme of the interface (owned here, applied at once and saved by the
/// <see cref="MainViewModel"/> as interface state), update checks (<see cref="UpdateCheckerViewModel"/>), the
/// parallelism of the CRC computation, and the About section (version, license and third-party notices). The
/// parallelism stays a sort option of <see cref="DirectoriesViewModel"/>
/// (<see cref="SortSettings.HashingParallelism"/>, saved in the profiles): only its place on screen differs.
/// </summary>
public sealed partial class PreferencesViewModel : ViewModelBase
{
    /// <summary>
    /// Licenses of the third-party components the executables embed, next to them: THIRD-PARTY-NOTICES.txt of the root
    /// of the repository, copied by every build and publish (src/ThirdPartyNotices.targets).
    /// </summary>
    public const string ThirdPartyNoticesFileName = "THIRD-PARTY-NOTICES.txt";

    private readonly ILocalizationService _localization;
    private readonly IDialogService _dialogService;
    private string _uiLocale;

    public PreferencesViewModel(
        ILocalizationService localization,
        IDialogService dialogService,
        UiStringsIndexer i18n,
        UpdateCheckerViewModel updates,
        DirectoriesViewModel directories)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        I18n = i18n ?? throw new ArgumentNullException(nameof(i18n));
        Updates = updates ?? throw new ArgumentNullException(nameof(updates));
        Directories = directories ?? throw new ArgumentNullException(nameof(directories));

        _uiLocale = localization.CurrentLocale;
        foreach (var (code, label) in localization.GetAvailableLocales())
        {
            AvailableLocaleOptions.Add(new SelectOptionItem<string>(code, label));
        }

        UiThemeOptions = new LocalizedOptionList<UiTheme>(
            localization,
            (UiTheme.Light, "app.theme_light"),
            (UiTheme.Dark, "app.theme_dark"),
            (UiTheme.System, "app.theme_system"));
    }

    public UiStringsIndexer I18n { get; }

    /// <summary>Update checks: option of the startup check and manual check.</summary>
    public UpdateCheckerViewModel Updates { get; }

    /// <summary>Owner of the parallelism of the CRC computation, a sort option.</summary>
    public DirectoriesViewModel Directories { get; }

    /// <summary>Languages of the interface, each one named in its own language.</summary>
    public ObservableCollection<SelectOptionItem<string>> AvailableLocaleOptions { get; } = [];

    public LocalizedOptionList<UiTheme> UiThemeOptions { get; }

    /// <summary>
    /// Language of the interface; the <see cref="MainViewModel"/> applies it to the whole interface. A blank value is
    /// ignored: the language list writes one back when its window closes, which would translate everything twice.
    /// </summary>
    public string UiLocale
    {
        get => _uiLocale;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                SetProperty(ref _uiLocale, value);
            }
        }
    }

    /// <summary>Color theme of the interface; the main window applies it to the application (light by default).</summary>
    [ObservableProperty]
    private UiTheme _uiTheme = UiTheme.Light;

    /// <summary>Name and version of RomSieve, at the head of the About section.</summary>
    public string AboutVersionText => _localization.Tr("prefs.about_version", ("version", ProductVersion.Current));

    /// <summary>Folder of the executable, which holds the third-party notices; a temporary folder in the tests.</summary>
    internal string ApplicationDirectory { get; set; } = AppContext.BaseDirectory;

    public string ThirdPartyNoticesPath => Path.Combine(ApplicationDirectory, ThirdPartyNoticesFileName);

    /// <summary>Opens the preferences window.</summary>
    [RelayCommand]
    public Task OpenAsync() => _dialogService.ShowPreferencesAsync(this);

    /// <summary>
    /// Opens the third-party notices with the application of the system. A missing file, or one that no application
    /// opens, is reported with its path, so that the user can still read it.
    /// </summary>
    [RelayCommand]
    private async Task OpenThirdPartyNoticesAsync()
    {
        var path = ThirdPartyNoticesPath;
        var title = _localization.Tr("prefs.btn_third_party_notices");
        if (!File.Exists(path))
        {
            await _dialogService.ShowWarningAsync(title, _localization.Tr("prefs.notices_missing", ("path", path)));
            return;
        }

        if (!await _dialogService.OpenFileAsync(path))
        {
            await _dialogService.ShowInfoAsync(title, _localization.Tr("prefs.notices_not_opened", ("path", path)));
        }
    }

    public void RefreshTranslations()
    {
        UiThemeOptions.RefreshTranslations();
        OnPropertyChanged(nameof(AboutVersionText));
    }
}
