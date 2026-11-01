using CommunityToolkit.Mvvm.Input;
using RomSieve.App.Abstractions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Named configuration profiles: saves the sort options shown in the settings steps as a profile, or shows those of a profile.
/// The profiles are files of the <c>config.json</c> format in the <c>profiles</c> directory next to the configuration
/// (<see cref="ConfigurationProfiles"/>), which the command line uses with <c>--profile</c>.
/// </summary>
public sealed partial class ProfilesViewModel : ViewModelBase
{
    private readonly IConfigurationService _configService;
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localization;
    private readonly Func<string> _getConfigFilePath;
    private readonly Func<SortSettings> _captureSettings;
    private readonly Action<SortSettings> _applySettings;
    private readonly Func<bool> _isRunning;
    private readonly Action<LogLevel, LocalizedMessage> _log;

    /// <param name="getConfigFilePath">Configuration file in use, next to which the profiles live.</param>
    /// <param name="captureSettings">Options shown in the settings steps.</param>
    /// <param name="applySettings">Shows the options of a loaded profile in the settings steps.</param>
    public ProfilesViewModel(
        IConfigurationService configService,
        IDialogService dialogService,
        ILocalizationService localization,
        Func<string> getConfigFilePath,
        Func<SortSettings> captureSettings,
        Action<SortSettings> applySettings,
        Func<bool> isRunning,
        Action<LogLevel, LocalizedMessage> log)
    {
        _configService = configService;
        _dialogService = dialogService;
        _localization = localization;
        _getConfigFilePath = getConfigFilePath;
        _captureSettings = captureSettings;
        _applySettings = applySettings;
        _isRunning = isRunning;
        _log = log;
    }

    public bool CanUseProfiles => !_isRunning();

    public void NotifyCommandsCanExecuteChanged()
    {
        OnPropertyChanged(nameof(CanUseProfiles));
        SaveProfileCommand.NotifyCanExecuteChanged();
        LoadProfileCommand.NotifyCanExecuteChanged();
    }

    private string ProfilesDirectory => ConfigurationProfiles.GetDirectory(_getConfigFilePath());

    private FileDialogFilter[] Filters => [new(_localization.Tr("profiles.filter"), ["json"])];

    [RelayCommand(CanExecute = nameof(CanUseProfiles))]
    public async Task SaveProfileAsync()
    {
        var directory = ProfilesDirectory;
        try
        {
            // The picker opens in the profiles directory, which must therefore exist
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            directory = string.Empty;
        }

        var path = await _dialogService.PickSaveFileAsync(
            _localization.Tr("profiles.save_title"),
            _localization.Tr("profiles.default_name") + ConfigurationProfiles.FileExtension,
            Filters,
            directory);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!path.EndsWith(ConfigurationProfiles.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            path += ConfigurationProfiles.FileExtension;
        }

        var data = new Dictionary<string, object?>(StringComparer.Ordinal);
        SortSettingsSerializer.Write(_captureSettings(), data);
        if (!await _configService.SaveConfigToFileAsync(data, path))
        {
            await _dialogService.ShowErrorAsync(_localization.Tr("dialog.error_title"), _localization.Tr("profiles.save_failed", ("path", path)));
            return;
        }

        _log(LogLevel.Ok, LocalizedMessage.Create("profiles.saved", ("path", path)));
        var message = _localization.Tr("profiles.saved", ("path", path));

        // Only a profile of the profiles directory is found by the command line
        if (GetCommandLineName(path) is { } name)
        {
            message += Environment.NewLine + Environment.NewLine + _localization.Tr("profiles.cli_hint", ("name", name));
        }

        await _dialogService.ShowInfoAsync(_localization.Tr("profiles.title"), message);
    }

    /// <summary>
    /// Shows the options of a profile; an option missing from the file takes its default value, as with the command line.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUseProfiles))]
    public async Task LoadProfileAsync()
    {
        var directory = ProfilesDirectory;
        var path = await _dialogService.PickOpenFileAsync(
            _localization.Tr("profiles.load_title"),
            Filters,
            Directory.Exists(directory) ? directory : null);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var data = await _configService.LoadConfigFromFileAsync(path);
        if (data is null)
        {
            await _dialogService.ShowErrorAsync(_localization.Tr("dialog.error_title"), _localization.Tr("profiles.load_failed", ("path", path)));
            return;
        }

        _applySettings(SortSettingsSerializer.Read(data));
        _log(LogLevel.Ok, LocalizedMessage.Create("profiles.loaded", ("path", path)));
        if (ConfigSchema.DescribeNewerVersion(data, path) is { } warning)
        {
            _log(LogLevel.Warn, warning);
        }
    }

    private string? GetCommandLineName(string path)
    {
        var name = ConfigurationProfiles.GetName(path);
        return name is not null
               && string.Equals(Path.GetFullPath(path), ConfigurationProfiles.GetPath(_getConfigFilePath(), name), StringComparison.OrdinalIgnoreCase)
            ? name
            : null;
    }
}
