namespace RomSieve.Core.Services;

/// <summary>
/// Named configuration profiles, shared by the interface and the command line: files of the <c>config.json</c> format
/// in the <c>profiles</c> directory next to the configuration file in use, one per profile (<c>&lt;name&gt;.json</c>).
/// </summary>
public static class ConfigurationProfiles
{
    public const string DirectoryName = "profiles";
    public const string FileExtension = ".json";
    public const int MaxNameLength = 64;

    private static readonly char[] ForbiddenNameChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>Directory of the profiles that go with a configuration file.</summary>
    public static string GetDirectory(string configFilePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configFilePath)) ?? string.Empty, DirectoryName);

    /// <summary>
    /// Whether a profile name gives the same valid file name on Windows and Linux: 1 to <see cref="MaxNameLength"/>
    /// characters, no path separator or character reserved by Windows, no control character, and no leading or
    /// trailing space or dot.
    /// </summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= MaxNameLength
        && name.Trim() == name
        && !name.StartsWith('.')
        && !name.EndsWith('.')
        && name.IndexOfAny(ForbiddenNameChars) < 0
        && !name.Any(char.IsControl)
        && !IsReservedWindowsName(name);

    // CON, PRN, AUX, NUL, COM1 to COM9 and LPT1 to LPT9 designate devices on Windows
    private static bool IsReservedWindowsName(string name) =>
        name.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL"
        || (name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && name[3] is >= '1' and <= '9');

    /// <summary>Path of the file of a profile; <see cref="ArgumentException"/> for an invalid name.</summary>
    public static string GetPath(string configFilePath, string name)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException($"Invalid profile name: '{name}'", nameof(name));
        }

        return Path.Combine(GetDirectory(configFilePath), name + FileExtension);
    }

    /// <summary>Name of the profile stored in a file, or <see langword="null"/> when the file is not a profile file.</summary>
    public static string? GetName(string profilePath)
    {
        if (!profilePath.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(profilePath);
        return IsValidName(name) ? name : null;
    }

    /// <summary>Names of the existing profiles, in alphabetical order.</summary>
    public static IReadOnlyList<string> List(string configFilePath)
    {
        var directory = GetDirectory(configFilePath);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(directory, "*" + FileExtension)
                .Select(GetName)
                .OfType<string>()
                .Order(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
