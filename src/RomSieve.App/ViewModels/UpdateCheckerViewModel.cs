using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RomSieve.App.Abstractions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using static RomSieve.Core.Services.ConfigDictionaryReader;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Update checker: manual check, automatic check at startup (at most once every <see cref="StartupCheckInterval"/>,
/// never in a development build), notification banner and versions the user chose to ignore. The newer release found
/// by the last successful check is kept, so that the banner comes back at each startup until that release is installed
/// or ignored. Its options are stored in <c>config.json</c> (<see cref="WriteTo"/>, <see cref="ReadFrom"/>), saved by
/// the owner through the callback it receives.
/// </summary>
public sealed partial class UpdateCheckerViewModel : ViewModelBase
{
    public const string CheckOnStartupKey = "check_updates_on_startup";
    public const string LastCheckUtcKey = "last_update_check_utc";
    public const string IgnoredVersionKey = "ignored_update_version";
    public const string AvailableUpdateTagKey = "available_update_tag";
    public const string AvailableUpdateUrlKey = "available_update_url";

    /// <summary>
    /// Minimum delay between two automatic checks; only a successful check counts, a failed one is attempted again at
    /// the next startup.
    /// </summary>
    public static readonly TimeSpan StartupCheckInterval = TimeSpan.FromHours(6);

    private static readonly string ReleasesPageUrl =
        $"https://github.com/{UpdateCheckService.DefaultRepositoryOwner}/{UpdateCheckService.DefaultRepositoryName}/releases";

    private readonly IUpdateCheckService _updateService;
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localization;
    private readonly Func<Task> _saveConfiguration;
    private readonly string _currentVersion;

    /// <summary>Tag of the release hidden by the cross in this session: the automatic check does not show it again.</summary>
    private string? _dismissedTag;

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

    /// <summary>Time of the last successful check (round-trip format); a failed check does not change it.</summary>
    [ObservableProperty]
    private string? _lastCheckUtc;

    [ObservableProperty]
    private string? _ignoredVersion;

    /// <summary>
    /// Tag of the newer release found by the last successful check, shown again at startup while it is newer than the
    /// running version; <c>null</c> once a check found RomSieve up to date.
    /// </summary>
    [ObservableProperty]
    private string? _availableUpdateTag;

    /// <summary>
    /// Page of <see cref="AvailableUpdateTag"/>. It comes from the network, then from the configuration file:
    /// <see cref="OpenReleasePageAsync"/> only opens it if <see cref="IsTrustedReleaseUrl"/> accepts it.
    /// </summary>
    [ObservableProperty]
    private string? _availableUpdateUrl;

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
        data[AvailableUpdateTagKey] = AvailableUpdateTag;
        data[AvailableUpdateUrlKey] = AvailableUpdateUrl;
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

        if (TryGetString(data, AvailableUpdateTagKey, out var availableTag))
        {
            AvailableUpdateTag = availableTag;
        }

        if (TryGetString(data, AvailableUpdateUrlKey, out var availableUrl))
        {
            AvailableUpdateUrl = availableUrl;
        }
    }

    /// <summary>Manual check: shows the banner, or tells the user that they are up to date or that the check failed.</summary>
    [RelayCommand]
    public async Task CheckForUpdatesAsync()
    {
        var result = await _updateService.CheckForUpdatesAsync(_currentVersion, CancellationToken.None);

        // A failed check changes nothing: neither the time of the last check nor the release kept
        if (result.Error is not null)
        {
            await _dialogService.ShowWarningAsync(
                _localization.Tr("update.check_failed_title"),
                _localization.Tr("update.check_failed_message", ("error", _localization.Format(result.Error))));
            return;
        }

        RecordSuccessfulCheck(result);

        // A manual check shows the latest release even if the user ignored it
        if (result.IsUpdateAvailable && result.LatestRelease is not null)
        {
            ShowNotification(result.LatestRelease);
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
    /// The address comes from the network (or from <c>config.json</c>, where a check kept it) and is handed to the
    /// operating system: only an HTTPS page of github.com is opened.
    /// </summary>
    internal static bool IsTrustedReleaseUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo);

    /// <summary>Hides the banner for this session only: the release kept shows it again at the next startup.</summary>
    [RelayCommand]
    public void DismissNotification()
    {
        _dismissedTag = LatestRelease?.TagName;
        IsNotificationVisible = false;
    }

    [RelayCommand]
    public async Task IgnoreVersionAsync()
    {
        IgnoredVersion = LatestVersion;
        IsNotificationVisible = false;
        await _saveConfiguration();
    }

    /// <summary>
    /// Whether the automatic check runs at startup: enabled by the user, in a published build,
    /// and no successful check less than <see cref="StartupCheckInterval"/> ago.
    /// </summary>
    public bool ShouldCheckAtStartup(DateTimeOffset now)
    {
        if (!CheckOnStartup || IsDevelopmentBuild)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(LastCheckUtc)
               || !DateTimeOffset.TryParse(LastCheckUtc, out var lastCheck)
               || now - lastCheck >= StartupCheckInterval;
    }

    /// <summary>
    /// Startup: shows again the banner of the release kept (<see cref="RestoreKeptUpdate"/>), then starts the automatic
    /// check in the background if <see cref="ShouldCheckAtStartup"/> allows it. Called by the application only, never by
    /// the window, which the headless tests create.
    /// </summary>
    public void TriggerStartupCheck() => _ = RunStartupAsync(DateTimeOffset.UtcNow);

    internal async Task RunStartupAsync(DateTimeOffset now)
    {
        var keptForgotten = RestoreKeptUpdate();

        // A successful check saves the configuration itself
        var saved = ShouldCheckAtStartup(now) && await RunStartupCheckAsync();
        if (keptForgotten && !saved)
        {
            try
            {
                await _saveConfiguration();
            }
            catch (Exception)
            {
                // Startup never disturbs the user: the release is forgotten again at the next startup
            }
        }
    }

    /// <summary>
    /// Shows the banner of the release kept by the last successful check, if it is still newer than the running version
    /// and not ignored; forgets it once it is no longer newer (the update was installed). Does nothing in a development
    /// build, which shares <c>config.json</c> with the installed version and keeps the release for it.
    /// </summary>
    /// <returns>Whether the release kept was forgotten, so that the configuration needs saving.</returns>
    internal bool RestoreKeptUpdate()
    {
        if (string.IsNullOrWhiteSpace(AvailableUpdateTag) || IsDevelopmentBuild)
        {
            return false;
        }

        var tag = AvailableUpdateTag.Trim();
        var version = SemanticVersion.Clean(tag);
        if (!SemanticVersion.IsNewer(version, _currentVersion))
        {
            AvailableUpdateTag = null;
            AvailableUpdateUrl = null;
            return true;
        }

        var release = new UpdateInfo(tag, version, AvailableUpdateUrl ?? string.Empty);
        if (!IsIgnored(release))
        {
            ShowNotification(release);
        }

        return false;
    }

    /// <summary>Automatic check: silent unless a release the user did not ignore is available.</summary>
    /// <returns>Whether the check succeeded and saved the configuration.</returns>
    internal async Task<bool> RunStartupCheckAsync()
    {
        try
        {
            var result = await _updateService.CheckForUpdatesAsync(_currentVersion, CancellationToken.None);

            // A failed check is not counted: it is attempted again at the next startup, and the release kept stays
            if (result.Error is not null)
            {
                return false;
            }

            RecordSuccessfulCheck(result);

            if (result.IsUpdateAvailable && result.LatestRelease is { } release && !IsIgnored(release)
                && !string.Equals(release.TagName, _dismissedTag, StringComparison.OrdinalIgnoreCase))
            {
                ShowNotification(release);
            }

            await _saveConfiguration();
            return true;
        }
        catch (Exception)
        {
            // A background check never disturbs the user: it is simply attempted again on a later startup
            return false;
        }
    }

    private bool IsDevelopmentBuild => SemanticVersion.TryParse(_currentVersion, out var current) && current.IsDev;

    /// <summary>
    /// Records the time of a successful check and replaces the release kept: by the newer release found, or by nothing
    /// when RomSieve is up to date (the banner of a release kept earlier is then hidden).
    /// </summary>
    private void RecordSuccessfulCheck(UpdateCheckResult result)
    {
        LastCheckUtc = DateTimeOffset.UtcNow.ToString("O");
        if (result.IsUpdateAvailable && result.LatestRelease is { } release)
        {
            AvailableUpdateTag = release.TagName;
            AvailableUpdateUrl = release.HtmlUrl;
        }
        else
        {
            AvailableUpdateTag = null;
            AvailableUpdateUrl = null;
            IsNotificationVisible = false;
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
