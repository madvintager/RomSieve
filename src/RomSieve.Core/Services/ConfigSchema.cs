using static RomSieve.Core.Services.ConfigDictionaryReader;

namespace RomSieve.Core.Services;

/// <summary>
/// Version of the format of <c>config.json</c> and of the profiles, and migration of an older file to the current
/// format. <see cref="SortSettingsSerializer.Read"/> migrates before reading, so that it only reads the current format;
/// every change of format adds a version and a migration step here, instead of a fallback in the reader.
/// </summary>
public static class ConfigSchema
{
    /// <summary>Key of the version, written by <see cref="SortSettingsSerializer.Write"/>.</summary>
    public const string VersionKey = "config_version";

    /// <summary>
    /// Version written by this build. History:
    /// <list type="bullet">
    /// <item>1: the format of RomSieve 1.0.0 to 1.1.1.</item>
    /// <item>2: <c>filter_not_in_dat</c>, on by default, off for a file of format 1.</item>
    /// </list>
    /// A file without a version (written by hand) is of the current format.
    /// </summary>
    public const int CurrentVersion = 2;

    /// <summary>Key of the option that filters the files missing from the DAT (format 2).</summary>
    internal const string FilterNotInDatKey = "filter_not_in_dat";

    /// <summary>
    /// Steps of the migration: the step at index <c>n</c> turns a file of version <c>n + 1</c> into version
    /// <c>n + 2</c>.
    /// </summary>
    private static readonly Action<Dictionary<string, object?>>[] Steps =
    [
        // 1 to 2: the files missing from the DAT were never filtered, the option stays off (a value added by hand is kept)
        data => data.TryAdd(FilterNotInDatKey, false)
    ];

    /// <summary>
    /// Version of a file: the current one without a version (a file written by hand, which follows the current format),
    /// 1 for an unreadable one.
    /// </summary>
    public static int ReadVersion(IReadOnlyDictionary<string, object?> data) =>
        !data.ContainsKey(VersionKey) ? CurrentVersion
        : TryGetInt(data, VersionKey, out var version) && version >= 1 ? version
        : 1;

    /// <summary>True for a file written by a later version of RomSieve, whose new options this build ignores.</summary>
    public static bool IsNewerThanCurrent(IReadOnlyDictionary<string, object?> data) =>
        ReadVersion(data) > CurrentVersion;

    /// <summary>Warning for a file of a later version (<see cref="IsNewerThanCurrent"/>), or null.</summary>
    public static Models.LocalizedMessage? DescribeNewerVersion(IReadOnlyDictionary<string, object?> data, string path) =>
        IsNewerThanCurrent(data)
            ? Models.LocalizedMessage.Create(
                "config.newer_version", ("path", path), ("version", ReadVersion(data)), ("current", CurrentVersion))
            : null;

    /// <summary>
    /// Copy of <paramref name="data"/> in the current format. A file of a later version is copied as is: its known
    /// keys are read, the others ignored.
    /// </summary>
    public static Dictionary<string, object?> Migrate(IReadOnlyDictionary<string, object?> data)
    {
        var migrated = new Dictionary<string, object?>(data, StringComparer.Ordinal);
        var version = ReadVersion(data);
        if (version > CurrentVersion)
        {
            return migrated;
        }

        for (; version < CurrentVersion; version++)
        {
            Steps[version - 1](migrated);
        }

        migrated[VersionKey] = CurrentVersion;
        return migrated;
    }
}
