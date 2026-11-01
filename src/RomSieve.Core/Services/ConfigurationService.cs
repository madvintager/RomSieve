using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using RomSieve.Core.Abstractions;

namespace RomSieve.Core.Services;

/// <summary>
/// JSON persistence manager for user configuration.
/// </summary>
public sealed class ConfigurationService : IConfigurationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Returns the default path of the configuration file according to the OS:
    /// - Linux/Unix: <c>~/.config/RomSieve/config.json</c> (or <c>$XDG_CONFIG_HOME/RomSieve/config.json</c>)
    /// - Windows: <c>%APPDATA%\RomSieve\config.json</c>
    /// </summary>
    public string GetDefaultConfigPath()
    {
        string baseDir;
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetEnvironmentVariable("APPDATA");
            baseDir = !string.IsNullOrEmpty(appData)
                ? appData
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming");
        }
        else
        {
            var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            baseDir = !string.IsNullOrEmpty(xdgConfig)
                ? xdgConfig
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(baseDir, "RomSieve", "config.json");
    }

    public Task<bool> SaveConfigToFileAsync(
        IDictionary<string, object?> data,
        string? path = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<bool>(cancellationToken);
        }

        var configPath = string.IsNullOrEmpty(path) ? GetDefaultConfigPath() : path;
        try
        {
            var parentDir = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            var json = JsonSerializer.Serialize(data, JsonOptions);
            AtomicFile.WriteAllText(configPath, json, new UTF8Encoding(false));
            return Task.FromResult(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Task.FromResult(false);
        }
    }

    public Task<Dictionary<string, object?>?> LoadConfigFromFileAsync(
        string? path = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<Dictionary<string, object?>?>(cancellationToken);
        }

        var configPath = string.IsNullOrEmpty(path) ? GetDefaultConfigPath() : path;
        if (!File.Exists(configPath))
        {
            return Task.FromResult<Dictionary<string, object?>?>(null);
        }

        try
        {
            var json = File.ReadAllText(configPath, Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Task.FromResult<Dictionary<string, object?>?>(null);
            }

            return Task.FromResult<Dictionary<string, object?>?>(ConvertJsonObject(doc.RootElement));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Task.FromResult<Dictionary<string, object?>?>(null);
        }
    }

    public string? PreserveUnreadableConfig(string path)
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var copy = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty,
                $"{Path.GetFileNameWithoutExtension(path)}.unreadable-{stamp}{Path.GetExtension(path)}");

            // Never replaces an earlier copy: a file it cannot copy is left in place instead
            File.Copy(path, copy, overwrite: false);
            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Dictionary<string, object?> ConvertJsonObject(JsonElement element)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = ConvertJsonElement(prop.Value);
        }
        return dict;
    }

    private static object? ConvertJsonElement(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToList(),
            JsonValueKind.Object => ConvertJsonObject(element),
            _ => null
        };
}
