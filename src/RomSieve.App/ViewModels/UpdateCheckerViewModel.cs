using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RomSieve.App.Abstractions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using static RomSieve.Core.Services.ConfigDictionaryReader;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Update checker: manual check, automatic check at startup (at most once a day, never in a development build),
/// notification banner and versions the user chose to ignore. Its options are stored in <c>config.json</c>
/// (<see cref="WriteTo"/>, <see cref="ReadFrom"/>), saved by the owner through the callback it receives.
/// </summary>
public sealed partial class UpdateCheckerViewModel : ViewModelBase
{
    public const string CheckOnStartupKey = "check_updates_on_startup";
    public const string LastCheckUtcKey = "last_update_check_utc";
    public const string IgnoredVersionKey = "ignored_update_version";

    /// <summary>Minimum delay between two automatic checks.</summary>
    public static readonly TimeSpan StartupCheckInterval = TimeSpan.FromHours(24);

    private static readonly string ReleasesPageUrl =
        $"https://github.com/{UpdateCheckService.DefaultRepositoryOwner}/{UpdateCheckService.DefaultRepositoryName}/releases";

    private readonly IUpdateCheckService _updateService;
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localization;
    private readonly Func<Task> _saveConfiguration;
    private readonly string _currentVersion;

    /// <param name="saveConfiguration">Saves the configuration once a check or a choice changed the options.</param>
    /// <param name="currentVersion">Version of the running application; <see cref="ProductVersion.Current"/> by default.</param>
    public UpdateCheckerViewModel(
        IUpdateCheckService updateService,
        IDialogService dialogService,
        ILocalizationService localization,
        Func<Task> saveConfiguration,
        string? currentVersion = null)
    {
        _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _saveConfiguration = saveConfiguration ?? throw new ArgumentNullException(nameof(saveConfiguration));
        _currentVersion = string.IsNullOrWhiteSpace(currentVersion) ? ProductVersion.Current : currentVersion;
    }

    [ObservableProperty]
    private bool _checkOnStartup = true;

    /// <summary>Time of the last check (round-trip format), successful or not.</summary>
    [ObservableProperty]
    private string? _lastCheckUtc;

    [ObservableProperty]
    private string? _ignoredVersion;

    [ObservableProperty]
    private bool _isNotificationVisible;

    /// <summary>Release shown in the banner, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotificationMessage), nameof(LatestVersion))]
    private UpdateInfo? _latestRelease;

    public string? LatestVersion => LatestRelease?.Version;

    public string NotificationMessage => LatestRelease is null
        ? string.Empty
        : _localization.Tr("update.notification_message", ("version", LatestRelease.TagName));

    public void RefreshTranslations() => OnPropertyChanged(nameof(NotificationMessage));

    public void WriteTo(IDictionary<string, object?> data)
    {
        data[CheckOnStartupKey] = CheckOnStartup;
        data[LastCheckUtcKey] = LastCheckUtc;
        data[IgnoredVersionKey] = IgnoredVersion;
    }

    /// <summary>Reads the options present in <paramref name="data"/>; a missing option keeps its current value.</summary>
    public void ReadFrom(IReadOnlyDictionary<string, object?> data)
    {
        if (TryGetBool(data, CheckOnStartupKey, out var checkOnStartup))
        {
            CheckOnStartup = checkOnStartup;
        }

        if (TryGetString(data, LastCheckUtcKey, out var lastCheck))
        {
            LastCheckUtc = lastCheck;
        }

        if (TryGetString(data, IgnoredVersionKey, out var ignored))
        {
            IgnoredVersion = ignored;
        }
    }

    /// <summary>Manual check: shows the banner, or tells the user that they are up to date or that the check failed.</summary>
    [RelayCommand]
    public async Task CheckForUpdatesAsync()
    {
        var result = await _updateService.CheckForUpdatesAsync(_currentVersion, CancellationToken.None);
        LastCheckUtc = DateTimeOffset.UtcNow.ToString("O");

        // A manual check shows the latest release even if the user ignored it
        if (result.IsUpdateAvailable && result.LatestRelease is not null)
        {
            ShowNotification(result.LatestRelease);
        }
        else if (result.Error is not null)
        {
            await _dialogService.ShowWarningAsync(
                _localization.Tr("update.check_failed_title"),
                _localization.Tr("update.check_failed_message", ("error", _localization.Format(result.Error))));
        }
        else
        {
            await _dialogService.ShowInfoAsync(
                _localization.Tr("update.up_to_date_title"),
                _localization.Tr("update.up_to_date_message", ("version", result.CurrentVersion)));
        }

        await _saveConfiguration();
    }

    /// <summary>Opens the page of the release in the browser, or the list of releases if its address is not trusted.</summary>
    [RelayCommand]
    public Task OpenReleasePageAsync() =>
        _dialogService.OpenUrlAsync(IsTrustedReleaseUrl(LatestRelease?.HtmlUrl) ? LatestRelease!.HtmlUrl : ReleasesPageUrl);

    /// <summary>
    /// The address comes from the network and is handed to the operating system: only an HTTPS page of github.com is opened.
    /// </summary>
    internal static bool IsTrustedReleaseUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo);

    [RelayCommand]
    public void DismissNotification() => IsNotificationVisible = false;

    [RelayCommand]
    public async Task IgnoreVersionAsync()
    {
        IgnoredVersion = LatestVersion;
        IsNotificationVisible = false;
        await _saveConfiguration();
    }

    /// <summary>
    /// Whether the automatic check runs at startup: enabled by the user, in a published build,
    /// and not already done less than <see cref="StartupCheckInterval"/> ago.
    /// </summary>
    public bool ShouldCheckAtStartup(DateTimeOffset now)
    {
        if (!CheckOnStartup)
        {
            return false;
        }

        if (SemanticVersion.TryParse(_currentVersion, out var current) && current.IsDev)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(LastCheckUtc)
               || !DateTimeOffset.TryParse(LastCheckUtc, out var lastCheck)
               || now - lastCheck >= StartupCheckInterval;
    }

    /// <summary>Starts the automatic check in the background if <see cref="ShouldCheckAtStartup"/> allows it.</summary>
    public void TriggerStartupCheck()
    {
        if (ShouldCheckAtStartup(DateTimeOffset.UtcNow))
        {
            _ = RunStartupCheckAsync();
        }
    }

    /// <summary>Automatic check: silent unless a release the user did not ignore is available.</summary>
    internal async Task RunStartupCheckAsync()
    {
        try
        {
            var result = await _updateService.CheckForUpdatesAsync(_currentVersion, CancellationToken.None);
            LastCheckUtc = DateTimeOffset.UtcNow.ToString("O");

            if (result.IsUpdateAvailable && result.LatestRelease is { } release && !IsIgnored(release))
            {
                ShowNotification(release);
            }

            await _saveConfiguration();
        }
        catch (Exception)
        {
            // A background check never disturbs the user: it is simply attempted again on a later startup
        }
    }

    private bool IsIgnored(UpdateInfo release) =>
        !string.IsNullOrWhiteSpace(IgnoredVersion)
        && (string.Equals(release.Version, IgnoredVersion, StringComparison.OrdinalIgnoreCase)
            || string.Equals(release.TagName, IgnoredVersion, StringComparison.OrdinalIgnoreCase));

    private void ShowNotification(UpdateInfo release)
    {
        LatestRelease = release;
        IsNotificationVisible = true;
    }
}
