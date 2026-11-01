using System.IO;
using System.Text.RegularExpressions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Service for target path calculation, cross-platform sanitization (Windows/Linux),
/// .m3u playlist name generation, and collision resolution.
/// </summary>
public sealed partial class PathOrganizerService : IPathOrganizerService
{

    /// <summary>Folder of the destination where the files that do not follow the naming convention are isolated.</summary>
    public const string UnmatchedFolderName = "_unmatched";

    /// <summary>Variables of a directory pattern, written between braces (<c>{title}</c>).</summary>
    public static readonly IReadOnlyList<string> PatternVariableNames =
        ["title", "letter", "version", "year", "publisher", "country", "language", "system", "chipset", "video", "dump_status", "genre", "category"];

    private readonly ITosecParser _tosecParser;

    public PathOrganizerService(ITosecParser tosecParser)
    {
        _tosecParser = tosecParser;
    }

    // Windows forbidden characters: < > : " / \ | ? * and control chars 0x00-0x1F
    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1f]", RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenCharsRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex MultiWhitespaceRegex();

    [GeneratedRegex(@"-{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultiHyphenRegex();

    [GeneratedRegex(@"[/\\]+", RegexOptions.CultureInvariant)]
    private static partial Regex PathSeparatorSplitRegex();

    [GeneratedRegex(@"\{[a-zA-Z0-9_]+\}", RegexOptions.CultureInvariant)]
    private static partial Regex UnmatchedPlaceholderRegex();

    [GeneratedRegex(@"\{([a-zA-Z0-9_]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderNameRegex();

    [GeneratedRegex(@"\s*\((?:Disc|Disk|Disque|Side|Tape|Part)\s+[0-9a-zA-Z]+(?:\s*(?:of|sur)\s*[0-9a-zA-Z]+)?\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex M3uDiscMarkerStripRegex();

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public string SanitizePathSegment(string? segment, string replacement = "-")
    {
        if (string.IsNullOrEmpty(segment))
        {
            return "Unknown";
        }

        var sanitized = ForbiddenCharsRegex().Replace(segment, replacement);
        sanitized = MultiWhitespaceRegex().Replace(sanitized, " ");
        sanitized = MultiHyphenRegex().Replace(sanitized, "-").Trim();
        sanitized = sanitized.TrimEnd('.', ' ');

        if (WindowsReservedNames.Contains(sanitized))
        {
            sanitized = $"{sanitized}_";
        }

        return string.IsNullOrEmpty(sanitized) ? "Unknown" : sanitized;
    }

    public string FormatWindowsExtendedPath(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var fullPath = Path.GetFullPath(path);
            if (fullPath.Length >= 250 &&
                !fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) &&
                !fullPath.StartsWith("//?/", StringComparison.Ordinal))
            {
                // A network path \\server\share\... becomes \\?\UNC\server\share\...
                return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
                    ? $@"\\?\UNC\{fullPath[2..]}"
                    : $@"\\?\{fullPath}";
            }
        }

        return path;
    }

    public string BuildTargetRelativePath(
        ProcessItem item,
        string pattern,
        bool restoreArticles = true,
        bool subfolderMultidisk = true,
        bool hiddenSubfolder = false) =>
        BuildTargetRelativePath(
            item.Parsed,
            pattern,
            restoreArticles,
            subfolderMultidisk,
            hiddenSubfolder,
            item.IsMultiMedia);

    public string BuildTargetRelativePath(
        ParsedRomName parsed,
        string pattern,
        bool restoreArticles = true,
        bool subfolderMultidisk = true,
        bool hiddenSubfolder = false,
        bool? isMultiMediaOverride = null)
    {
        if (!parsed.IsTosecConform)
        {
            return UnmatchedFolderName;
        }

        var title = parsed.Title;
        if (restoreArticles)
        {
            title = _tosecParser.RestoreTitleArticles(title);
        }

        var firstChar = !string.IsNullOrEmpty(title) ? char.ToUpperInvariant(title[0]) : '#';
        var letter = firstChar is >= 'A' and <= 'Z' ? firstChar.ToString() : "#";

        var mapping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["title"] = SanitizePathSegment(string.IsNullOrEmpty(title) ? "Unknown" : title),
            ["letter"] = letter,
            ["version"] = SanitizePathSegment(parsed.Version ?? string.Empty),
            ["year"] = SanitizePathSegment(string.IsNullOrEmpty(parsed.Year) ? "Unknown" : parsed.Year),
            ["publisher"] = SanitizePathSegment(string.IsNullOrEmpty(parsed.Publisher) ? "Unknown" : parsed.Publisher),
            ["country"] = SanitizePathSegment(string.IsNullOrEmpty(parsed.Country) ? "Unknown" : parsed.Country),
            ["language"] = SanitizePathSegment(FolderLanguage(parsed) ?? "Unknown"),
            ["system"] = SanitizePathSegment(string.IsNullOrEmpty(parsed.System) ? "Unknown" : parsed.System),
            ["video"] = SanitizePathSegment(string.IsNullOrEmpty(parsed.Video) ? "Unknown" : parsed.Video),

            // Chipset an Amiga release needs: AGA, OCS-AGA (both, or fixed for AGA) or OCS-ECS (a name without a mark)
            ["chipset"] = AmigaChipsets.GetFolderName(parsed),
            ["dump_status"] = parsed.DumpStatusSummary,

            // Category of a MAME machine (category file); its genre and subgenre become "Shooter - Flying Vertical"
            ["genre"] = SanitizePathSegment(parsed.Machine?.Category?.Genre ?? "Unknown"),
            ["category"] = SanitizePathSegment(parsed.Machine?.Category is { } category
                ? category.Name.Replace(" / ", " - ", StringComparison.Ordinal)
                : "Unknown")
        };

        var rawPattern = (pattern ?? string.Empty).Trim().Trim('/', '\\');
        var sanitizedParts = new List<string>();

        if (!string.IsNullOrEmpty(rawPattern))
        {
            var parts = PathSeparatorSplitRegex().Split(rawPattern);
            foreach (var part in parts)
            {
                var formatted = part;
                foreach (var (key, val) in mapping)
                {
                    formatted = formatted.Replace($"{{{key}}}", val, StringComparison.Ordinal);
                }

                formatted = UnmatchedPlaceholderRegex().Replace(formatted, string.Empty);

                var cleanedSegment = SanitizePathSegment(formatted);
                if (!string.IsNullOrEmpty(cleanedSegment) && cleanedSegment != ".")
                {
                    sanitizedParts.Add(cleanedSegment);
                }
            }
        }

        var isMulti = isMultiMediaOverride ?? parsed.IsMultiMedia;
        if (subfolderMultidisk && isMulti)
        {
            var gameFolder = SanitizePathSegment(string.IsNullOrEmpty(title) ? "Unknown" : title);
            if (hiddenSubfolder)
            {
                gameFolder = $".{gameFolder}";
            }

            if (sanitizedParts.Count == 0 || !string.Equals(sanitizedParts[^1], gameFolder, StringComparison.OrdinalIgnoreCase))
            {
                sanitizedParts.Add(gameFolder);
            }
        }

        return sanitizedParts.Count == 0
            ? "."
            : Path.Combine([.. sanitizedParts]);
    }

    public string BuildTargetPath(ParsedRomName parsed, string fileName, SortConfig config, bool? isMultiMediaOverride = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var hiddenSubfolder = config.GenerateM3u &&
                              config.SubfolderMultidisk &&
                              config.M3uPlacement == M3uPlacementMode.ParentHidden;

        var relTargetDir = BuildTargetRelativePath(
            parsed,
            config.Pattern,
            restoreArticles: config.RestoreArticles,
            subfolderMultidisk: config.SubfolderMultidisk,
            hiddenSubfolder: hiddenSubfolder,
            isMultiMediaOverride: isMultiMediaOverride);

        var target = relTargetDir == "."
            ? Path.Combine(config.DestinationDir, fileName)
            : Path.Combine(config.DestinationDir, relTargetDir, fileName);

        // In ZIP mode, the target is the archive named after the file; a MAME set already is an archive, and its CHD
        // disks must stay files
        return config.TransferMode == TransferMode.Zip && !parsed.Convention.IsMameSets()
            ? Path.ChangeExtension(target, ".zip")
            : target;
    }

    /// <summary>
    /// Whether a file name read from an archive or a DAT, which may hold folders (<c>set/disk.chd</c>), stays in the
    /// folder it is combined with: neither absolute (<c>/x</c>, <c>\x</c>), nor on a drive (<c>C:x</c>, also after a
    /// folder, since its last segment may be taken alone), nor with a <c>..</c> segment, nor with a null character. Both
    /// separators count under any system, so that an archive made under Windows gives the same result everywhere.
    /// </summary>
    internal static bool IsSafeRelativeName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name[0] is not ('/' or '\\')
        && !name.Contains('\0', StringComparison.Ordinal)
        && name.Split('/', '\\').All(segment =>
            segment != ".." && !(segment.Length > 1 && segment[1] == ':' && char.IsAsciiLetter(segment[0])));

    /// <summary>
    /// Whether <paramref name="path"/> lies inside <paramref name="directory"/>, both made absolute and normalized as the
    /// system does (<c>..</c> segments resolved): the last check of a target before anything is written there.
    /// </summary>
    internal static bool IsInsideDirectory(string path, string directory)
    {
        try
        {
            var fullDirectory = Path.GetFullPath(directory);
            if (!Path.EndsInDirectorySeparator(fullDirectory))
            {
                fullDirectory += Path.DirectorySeparatorChar;
            }

            var fullPath = Path.GetFullPath(path);
            return fullPath.Length > fullDirectory.Length && fullPath.StartsWith(
                fullDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            // A null character: no such path can be written
            return false;
        }
    }

    public PatternCheck CheckPattern(string? pattern)
    {
        var text = pattern ?? string.Empty;
        var unknown = PlaceholderNameRegex().Matches(text)
            .Select(m => m.Groups[1].Value)
            .Where(name => !PatternVariableNames.Contains(name, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Select(name => $"{{{name}}}")
            .ToList();

        // Every brace must open then close a variable name: anything else stays in the path as text
        var withoutVariables = UnmatchedPlaceholderRegex().Replace(text, string.Empty);
        var unbalanced = withoutVariables.Contains('{', StringComparison.Ordinal) ||
                         withoutVariables.Contains('}', StringComparison.Ordinal);

        return new PatternCheck(unknown, unbalanced);
    }

    /// <summary>
    /// Language of <c>{language}</c>: the one written in the name, otherwise the language of its first country with a
    /// known language (a TOSEC name omits the language of its country), as the No-Intro parser and the language
    /// filters do.
    /// </summary>
    private static string? FolderLanguage(ParsedRomName parsed)
    {
        if (!string.IsNullOrEmpty(parsed.Language))
        {
            return parsed.Language;
        }

        return parsed.CountryCodes
            .Select(c => ReleaseScoringService.CountryDefaultLanguages.TryGetValue(c, out var language) ? language : null)
            .FirstOrDefault(language => !string.IsNullOrEmpty(language));
    }

    public string GenerateM3uFilename(string filenameOrStem)
    {
        var stem = filenameOrStem.Contains('.', StringComparison.Ordinal)
            ? Path.GetFileNameWithoutExtension(filenameOrStem)
            : filenameOrStem;

        var clean = M3uDiscMarkerStripRegex().Replace(stem, string.Empty);
        clean = MultiWhitespaceRegex().Replace(clean, " ").Trim();
        return $"{clean}.m3u";
    }

    public (string FinalPath, bool IsCollisionSkipped) ResolveCollision(
        string candidatePath,
        CollisionMode collisionMode,
        ISet<string> claimedPaths)
    {
        var (finalPaths, skipped) = ResolveSetCollision([candidatePath], collisionMode, claimedPaths);
        return (finalPaths[0], skipped);
    }

    public (IReadOnlyList<string> FinalPaths, bool IsCollisionSkipped) ResolveSetCollision(
        IReadOnlyList<string> candidatePaths,
        CollisionMode collisionMode,
        ISet<string> claimedPaths)
    {
        if (candidatePaths.Count == 0)
        {
            return ([], false);
        }

        bool IsTaken(string path) =>
            File.Exists(path) || Directory.Exists(path) || claimedPaths.Contains(NormalizeCollisionKey(path));

        void ClaimAll(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                claimedPaths.Add(NormalizeCollisionKey(path));
            }
        }

        if (!candidatePaths.Any(IsTaken))
        {
            ClaimAll(candidatePaths);
            return (candidatePaths, false);
        }

        if (collisionMode == CollisionMode.Skip)
        {
            return (candidatePaths, true);
        }

        if (collisionMode == CollisionMode.Overwrite)
        {
            ClaimAll(candidatePaths);
            return (candidatePaths, false);
        }

        // CollisionMode.Rename: the same suffix _01, _02, etc. is applied to every file of the set
        var idx = 1;
        while (true)
        {
            var renamed = candidatePaths.Select(path => AppendCollisionSuffix(path, idx)).ToList();
            if (!renamed.Any(IsTaken))
            {
                ClaimAll(renamed);
                return (renamed, false);
            }

            idx++;
        }
    }

    private static string AppendCollisionSuffix(string path, int index)
    {
        var parent = Path.GetDirectoryName(path) ?? string.Empty;
        var newFilename = $"{Path.GetFileNameWithoutExtension(path)}_{index:D2}{Path.GetExtension(path)}";
        return string.IsNullOrEmpty(parent) ? newFilename : Path.Combine(parent, newFilename);
    }

    /// <summary>Key of a target path in the set of the paths a run claimed (<c>claimedPaths</c>).</summary>
    internal static string NormalizeCollisionKey(string path) =>
        (Path.IsPathRooted(path) ? Path.GetFullPath(path) : path).ToLowerInvariant();
}
