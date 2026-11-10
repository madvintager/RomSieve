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
        ["title", "letter", "version", "year", "publisher", "country", "language", "system", "chipset", "video", "dump_status", "genre", "category", "dat"];

    /// <summary>
    /// Variable of the pattern of the patched versions only: the whole folder the original ROM would have in the
    /// destination (folders of the DAT of a lot, then of the main pattern), worth several folders, or none.
    /// </summary>
    public const string OriginFolderVariable = "origin_folder";

    /// <summary>
    /// Variable of the pattern of the patched versions only: the folders of a lot above the main pattern (the folder of
    /// the DAT, under the folders of its path when mirrored), worth several folders, or none for a single DAT.
    /// </summary>
    public const string DatFolderVariable = "dat_folder";

    /// <summary>Variables of the pattern of the patched versions that stand for whole folders, and must stand alone in one.</summary>
    public static readonly IReadOnlyList<string> FolderVariableNames = [OriginFolderVariable, DatFolderVariable];

    /// <summary>Characters Windows forbids in a name (the separators apart), refused in the pattern of the patched versions.</summary>
    private static readonly char[] ForbiddenPatternChars = ['<', '>', ':', '"', '|', '?', '*'];

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
        bool? isMultiMediaOverride = null,
        string? datName = null)
    {
        if (!parsed.IsTosecConform)
        {
            return UnmatchedFolderName;
        }

        var title = FolderTitle(parsed, restoreArticles);
        var mapping = VariableValues(parsed, title, datName);
        var sanitizedParts = new List<string>();
        foreach (var part in PatternParts(pattern))
        {
            sanitizedParts.Add(ExpandPart(part, mapping));
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

    /// <summary>Title of <c>{title}</c>, <c>{letter}</c> and of the multi-disk subfolder, its articles restored on request.</summary>
    private string FolderTitle(ParsedRomName parsed, bool restoreArticles) =>
        restoreArticles ? _tosecParser.RestoreTitleArticles(parsed.Title) : parsed.Title;

    /// <summary>Folders of a pattern, between its separators (both, under any system), without the empty ones.</summary>
    private static string[] PatternParts(string? pattern)
    {
        var rawPattern = (pattern ?? string.Empty).Trim().Trim('/', '\\');
        return rawPattern.Length == 0 ? [] : PathSeparatorSplitRegex().Split(rawPattern);
    }

    /// <summary>
    /// One folder of a pattern: its variables replaced by their values, the unknown ones dropped, then sanitized. A folder
    /// left empty is <c>Unknown</c>, never removed.
    /// </summary>
    private string ExpandPart(string part, IReadOnlyDictionary<string, string> mapping)
    {
        var formatted = part;
        foreach (var (key, val) in mapping)
        {
            formatted = formatted.Replace($"{{{key}}}", val, StringComparison.Ordinal);
        }

        formatted = UnmatchedPlaceholderRegex().Replace(formatted, string.Empty);
        return SanitizePathSegment(formatted);
    }

    /// <summary>Values of the variables of a pattern (<see cref="PatternVariableNames"/>) for a file, each a sanitized folder name.</summary>
    private Dictionary<string, string> VariableValues(ParsedRomName parsed, string title, string? datName)
    {
        var firstChar = !string.IsNullOrEmpty(title) ? char.ToUpperInvariant(title[0]) : '#';
        var letter = firstChar is >= 'A' and <= 'Z' ? firstChar.ToString() : "#";

        return new Dictionary<string, string>(StringComparer.Ordinal)
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
                : "Unknown"),

            // Header name of the DAT the file is sorted with
            ["dat"] = SanitizePathSegment(string.IsNullOrWhiteSpace(datName) ? "Unknown" : datName.Trim())
        };
    }

    public string BuildTargetPath(
        ParsedRomName parsed,
        string fileName,
        SortConfig config,
        bool? isMultiMediaOverride = null,
        DatPlacement? dat = null,
        bool patched = false)
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
            isMultiMediaOverride: isMultiMediaOverride,
            datName: dat?.Name);

        // The folder of the DAT of a lot goes above the folders of the pattern; _unmatched stays at the root
        var datFolders = relTargetDir == UnmatchedFolderName ? [] : DatFolderSegments(dat, config);
        IReadOnlyList<string> folders = [.. datFolders, .. relTargetDir == "." ? Array.Empty<string>() : relTargetDir.Split(Path.DirectorySeparatorChar)];

        // A patched version goes where the pattern of the patched versions says, from the root of the destination; empty,
        // into the folder of its original ROM
        if (patched && relTargetDir != UnmatchedFolderName && !string.IsNullOrWhiteSpace(config.PatchedPattern))
        {
            folders = PatchedFolders(parsed, config, datFolders, folders, dat?.Name);
        }

        string[] parts = [config.DestinationDir, .. folders, fileName];
        var target = Path.Combine(parts);

        // In ZIP mode, the target is the archive named after the file; a MAME set already is an archive, and its CHD
        // disks must stay files
        return config.TransferMode == TransferMode.Zip && !parsed.Convention.IsMameSets()
            ? Path.ChangeExtension(target, ".zip")
            : target;
    }

    /// <summary>
    /// Folders of a patched version under the destination, from the pattern of the patched versions
    /// (<see cref="SortConfig.PatchedPattern"/>): <c>{origin_folder}</c> is worth the folders of its original ROM
    /// (<paramref name="originFolders"/>), <c>{dat_folder}</c> those of its DAT in a lot (<paramref name="datFolders"/>),
    /// each possibly none; every other folder is expanded as a folder of the main pattern, with the values of the original
    /// ROM. The pattern is refused before any run when it would leave the destination
    /// (<see cref="CheckPatchedPattern"/>); its values, sanitized, cannot. A value may still name the first folder after
    /// the quarantine of the purges or the marker of the destination (a game titled <c>_purged</c> under <c>{title}</c>):
    /// that folder then takes a final <c>_</c>, as a reserved name of Windows does. The folders of <c>{origin_folder}</c>
    /// and <c>{dat_folder}</c> stay those of the original ROM.
    /// </summary>
    private List<string> PatchedFolders(
        ParsedRomName parsed,
        SortConfig config,
        IReadOnlyList<string> datFolders,
        IReadOnlyList<string> originFolders,
        string? datName)
    {
        var mapping = VariableValues(parsed, FolderTitle(parsed, config.RestoreArticles), datName);
        var folders = new List<string>();
        foreach (var part in PatternParts(config.PatchedPattern))
        {
            switch (part.Trim())
            {
                case $"{{{OriginFolderVariable}}}":
                    folders.AddRange(originFolders);
                    break;
                case $"{{{DatFolderVariable}}}":
                    folders.AddRange(datFolders);
                    break;
                default:
                    var folder = ExpandPart(part, mapping);

                    // Only a folder of this pattern: the folders of the original ROM are those of the main pattern
                    folders.Add(folders.Count == 0 && IsGuardFolderName(folder) ? folder + "_" : folder);
                    break;
            }
        }

        return folders;
    }

    /// <summary>Whether a folder at the root of the destination would be the quarantine of the purges or the marker.</summary>
    private static bool IsGuardFolderName(string name) =>
        name.Equals(DestinationGuard.QuarantineDirectoryName, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(DestinationGuard.MarkerFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Folders of the destination above the pattern for the files of <paramref name="dat"/>: none for a single DAT or
    /// without <see cref="SortConfig.FolderPerDat"/>; otherwise the folder named after the DAT, under the folders of its
    /// path in the folder of DATs read when they are mirrored (<see cref="SortConfig.DatFolderMirror"/>). Each folder is a
    /// sanitized segment: a name read from a DAT or a path never leaves the destination.
    /// </summary>
    internal IReadOnlyList<string> DatFolderSegments(DatPlacement? dat, SortConfig config)
    {
        if (dat is not { IsLotMember: true } || !config.FolderPerDat)
        {
            return [];
        }

        var segments = new List<string>();
        if (config.DatFolderMirror && !string.IsNullOrEmpty(dat.RelativeFolder))
        {
            segments.AddRange(PathSeparatorSplitRegex().Split(dat.RelativeFolder)
                .Where(segment => !string.IsNullOrWhiteSpace(segment) && segment != ".")
                .Select(segment => SanitizePathSegment(segment)));
        }

        segments.Add(SanitizePathSegment(string.IsNullOrWhiteSpace(dat.Name) ? "Unknown" : dat.Name.Trim()));
        return segments;
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

    public PatternCheck CheckPattern(string? pattern, bool patched = false)
    {
        var text = pattern ?? string.Empty;
        var unknown = PlaceholderNameRegex().Matches(text)
            .Select(m => m.Groups[1].Value)
            .Where(name => !PatternVariableNames.Contains(name, StringComparer.Ordinal) &&
                           !(patched && FolderVariableNames.Contains(name, StringComparer.Ordinal)))
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
    /// Why the pattern of the patched versions (<see cref="SortConfig.PatchedPattern"/>) is refused, read as written:
    /// it must stay inside the destination (neither rooted, nor on a drive, nor with a <c>..</c> folder), name folders
    /// that Windows and Linux accept, not start with the quarantine of the purges or the marker of the destination, and
    /// give <c>{origin_folder}</c> and <c>{dat_folder}</c> a folder of their own. The values of its variables are
    /// sanitized when the path is built: they cannot bring a problem in. Both separators count under any system.
    /// </summary>
    public static PatchedPatternProblem CheckPatchedPattern(string? pattern)
    {
        var text = pattern?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return PatchedPatternProblem.None;
        }

        if (text[0] is '/' or '\\')
        {
            return PatchedPatternProblem.Rooted;
        }

        var segments = text.Split('/', '\\');
        if (segments.Any(segment => segment.TrimStart() is { Length: > 1 } name && name[1] == ':' && char.IsAsciiLetter(name[0])))
        {
            return PatchedPatternProblem.Drive;
        }

        if (segments.Any(segment => segment.Trim() == ".."))
        {
            return PatchedPatternProblem.ParentSegment;
        }

        if (text.IndexOfAny(ForbiddenPatternChars) >= 0 || text.Any(char.IsControl))
        {
            return PatchedPatternProblem.ForbiddenCharacter;
        }

        // The first folder written: the folder variables may be worth no folder, and leave the next one first
        var first = segments.FirstOrDefault(segment => !string.IsNullOrWhiteSpace(segment) && !IsFolderVariable(segment));
        if (first?.Trim().TrimEnd('.', ' ') is { } firstName && IsGuardFolderName(firstName))
        {
            return PatchedPatternProblem.ReservedFolder;
        }

        return segments.Any(segment => !IsFolderVariable(segment) &&
                                       FolderVariableNames.Any(name => segment.Contains($"{{{name}}}", StringComparison.Ordinal)))
            ? PatchedPatternProblem.FolderVariableNotAlone
            : PatchedPatternProblem.None;
    }

    /// <summary>Whether a folder of a pattern is <c>{origin_folder}</c> or <c>{dat_folder}</c> alone.</summary>
    private static bool IsFolderVariable(string segment) =>
        FolderVariableNames.Any(name => segment.Trim() == $"{{{name}}}");

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
