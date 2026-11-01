using RomSieve.Core.Models;

namespace RomSieve.Core.Abstractions;

/// <summary>
/// Internationalization (i18n) and localization (l10n) service for RomSieve.
/// </summary>
public interface ILocalizationService
{
    string CurrentLocale { get; }
    event EventHandler? LocaleChanged;

    void SetLocale(string locale);
    IReadOnlyList<(string Code, string Label)> GetAvailableLocales();
    string Tr(string key, string? defaultValue = null, IReadOnlyDictionary<string, object?>? args = null);
    string Tr(string key, params (string Key, object? Value)[] args);

    /// <summary>
    /// Translates a structured message in the active language, nested messages included.
    /// Returns an empty string for <c>null</c>.
    /// </summary>
    string Format(LocalizedMessage? message);
    IReadOnlyList<(string Code, string Label)> GetDefaultCountries(string? locale = null);
    IReadOnlyList<(string Code, string Label)> GetDefaultLanguages(string? locale = null);
}

/// <summary>
/// Crash reports: a text file per unexpected exception, in <c>crashes/</c> next to the configuration file.
/// </summary>
public interface ICrashReportService
{
    /// <summary>Folder that holds the reports.</summary>
    string CrashDirectory { get; }

    /// <summary>
    /// Writes a report of the exception and returns its path, or <c>null</c> if it could not be written.
    /// Never throws: it runs in the crash handlers, where a second exception would hide the first.
    /// </summary>
    /// <param name="origin">Where the exception was caught (interface thread, background thread...).</param>
    string? WriteReport(Exception exception, string origin);
}

/// <summary>
/// JSON persistence service for user configuration.
/// </summary>
public interface IConfigurationService
{
    string GetDefaultConfigPath();
    Task<bool> SaveConfigToFileAsync(IDictionary<string, object?> data, string? path = null, CancellationToken cancellationToken = default);
    Task<Dictionary<string, object?>?> LoadConfigFromFileAsync(string? path = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies a configuration file that exists but cannot be read next to it
    /// (<c>config.unreadable-yyyyMMdd-HHmmss.json</c>), before the default settings replace it. Returns the path of
    /// the copy, or <c>null</c> if the file cannot be copied either.
    /// </summary>
    string? PreserveUnreadableConfig(string path);
}
