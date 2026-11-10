using System.IO;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Safety checks on the source and destination directories of a <see cref="SortConfig"/>,
/// shared by the processing pipeline and the user interface.
/// </summary>
/// <remarks>
/// Paths are compared after <see cref="Path.GetFullPath(string)"/> normalization, as written, then once more with their
/// symbolic links and junctions resolved (<see cref="ResolveLinks"/>): two different paths leading to the same physical
/// directory, or one into the other, overlap. Mapped network drives and bind mounts are not resolved.
/// </remarks>
public static class SortConfigValidator
{
    /// <summary>
    /// Determines how the destination relates to the source, using the path case sensitivity
    /// of the current platform (case-insensitive on Windows, case-sensitive elsewhere).
    /// </summary>
    public static PathOverlap GetPathOverlap(string sourceDir, string destinationDir, bool recursive) =>
        GetPathOverlap(sourceDir, destinationDir, recursive, ignoreCase: OperatingSystem.IsWindows());

    /// <summary>
    /// Determines how the destination relates to the source with an explicit case sensitivity rule.
    /// A destination inside the source is only reported when <paramref name="recursive"/> is set,
    /// because a top-level scan never enters it.
    /// </summary>
    public static PathOverlap GetPathOverlap(string sourceDir, string destinationDir, bool recursive, bool ignoreCase)
    {
        if (string.IsNullOrWhiteSpace(sourceDir) || string.IsNullOrWhiteSpace(destinationDir))
        {
            return PathOverlap.None;
        }

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var source = NormalizeDirectory(sourceDir);
        var destination = NormalizeDirectory(destinationDir);
        var overlap = CompareDirectories(source, destination, recursive, comparison);

        // A link (or a junction) may lead one path into the other: the paths are compared again with their links resolved
        return overlap != PathOverlap.None
            ? overlap
            : CompareDirectories(ResolveLinks(source), ResolveLinks(destination), recursive, comparison);
    }

    private static PathOverlap CompareDirectories(string source, string destination, bool recursive, StringComparison comparison)
    {
        if (string.Equals(source, destination, comparison))
        {
            return PathOverlap.Identical;
        }

        if (IsStrictlyInside(source, destination, comparison))
        {
            return PathOverlap.DestinationContainsSource;
        }

        if (recursive && IsStrictlyInside(destination, source, comparison))
        {
            return PathOverlap.DestinationInsideSource;
        }

        return PathOverlap.None;
    }

    /// <summary>
    /// Localization key of the error message describing an overlap, or <c>null</c> when the paths are independent.
    /// </summary>
    public static string? GetErrorKey(PathOverlap overlap) => overlap switch
    {
        PathOverlap.Identical => "dialog.error_same_source_dest",
        PathOverlap.DestinationContainsSource => "dialog.error_dest_contains_source",
        PathOverlap.DestinationInsideSource => "dialog.error_dest_inside_source",
        _ => null
    };

    /// <summary>
    /// Localization key of the error message describing how the destination overlaps a folder the sort reads: the source,
    /// then, for a run that sorts MAME sets (<paramref name="isMameRun"/>, <see cref="MameSetIdentifier.IsMameRun"/>), the
    /// CHD folder (always read recursively), then the patch folder; <c>null</c> when it overlaps none. Any other run never
    /// reads the CHD folder, which stays in the configuration for the next MAME sort: it is then a folder like any other.
    /// The patch folder is checked whatever the run, even one that does not read it: a purge of a destination that holds
    /// it would put the patches in quarantine. Last, the main pattern (<see cref="GetPatternErrorKey"/>), then the pattern of the patched versions (<see cref="GetPatchedPatternErrorKey(SortConfig)"/>).
    /// </summary>
    public static string? GetErrorKey(SortConfig config, bool isMameRun)
    {
        ArgumentNullException.ThrowIfNull(config);
        return GetErrorKey(GetPathOverlap(config.SourceDir, config.DestinationDir, config.Recursive))
               ?? (isMameRun ? GetChdDirErrorKey(config.ChdDir, config.DestinationDir) : null)
               ?? GetPatchDirErrorKey(config.PatchDir, config.DestinationDir)
               ?? GetPatternErrorKey(config.Pattern)
               ?? GetPatchedPatternErrorKey(config);
    }

    /// <summary>
    /// Localization key of the error message of a main pattern (<see cref="SortConfig.Pattern"/>, empty with everything at
    /// the root) whose text names the quarantine of the purges or the marker of the destination as its first folder
    /// (<see cref="PathOrganizerService.StartsWithReservedFolder"/>), or <c>null</c>. Whatever the run and the convention:
    /// emptying the quarantine would delete what the sort wrote there.
    /// </summary>
    public static string? GetPatternErrorKey(string? pattern) =>
        PathOrganizerService.StartsWithReservedFolder(pattern) ? "dialog.error_pattern_reserved_folder" : null;

    /// <summary>
    /// Localization key of the error message of a pattern of the patched versions that would not stay inside the
    /// destination (<see cref="PathOrganizerService.CheckPatchedPattern"/>), or <c>null</c>. Only with a patch folder: the
    /// pattern is greyed out without one, and nothing it would write is written. Whatever the run, as the patch folder.
    /// </summary>
    public static string? GetPatchedPatternErrorKey(SortConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return GetPatchedPatternErrorKey(config.PatchDir, config.PatchedPattern);
    }

    /// <summary>
    /// Localization key of the error message of <paramref name="patchedPattern"/> with the patch folder
    /// <paramref name="patchDir"/> (see <see cref="GetPatchedPatternErrorKey(SortConfig)"/>), or <c>null</c>: for the state
    /// line of the interface, which has no <see cref="SortConfig"/>.
    /// </summary>
    public static string? GetPatchedPatternErrorKey(string? patchDir, string? patchedPattern) =>
        !string.IsNullOrWhiteSpace(patchDir) &&
        PathOrganizerService.CheckPatchedPattern(patchedPattern) != PatchedPatternProblem.None
            ? "dialog.error_patched_pattern_invalid"
            : null;

    /// <summary>
    /// Localization key of the error message describing how the destination overlaps the patch folder (read recursively),
    /// or <c>null</c>.
    /// </summary>
    public static string? GetPatchDirErrorKey(string? patchDir, string destinationDir) =>
        string.IsNullOrWhiteSpace(patchDir)
            ? null
            : GetPathOverlap(patchDir, destinationDir, recursive: true) switch
            {
                PathOverlap.Identical => "dialog.error_same_patch_dest",
                PathOverlap.DestinationContainsSource => "dialog.error_dest_contains_patches",
                PathOverlap.DestinationInsideSource => "dialog.error_dest_inside_patches",
                _ => null
            };

    /// <summary>Localization key of the error message describing how the destination overlaps the CHD folder, or <c>null</c>.</summary>
    public static string? GetChdDirErrorKey(string? chdDir, string destinationDir) =>
        string.IsNullOrWhiteSpace(chdDir)
            ? null
            : GetPathOverlap(chdDir, destinationDir, recursive: true) switch
            {
                PathOverlap.Identical => "dialog.error_same_chd_dest",
                PathOverlap.DestinationContainsSource => "dialog.error_dest_contains_chd",
                PathOverlap.DestinationInsideSource => "dialog.error_dest_inside_chd",
                _ => null
            };

    /// <summary>
    /// The physical path of a directory: each component that is a symbolic link or a junction is replaced by its final
    /// target, from the root down, so that a link in a parent directory is resolved too. The components that do not exist
    /// yet (a destination to be created) are kept as written. A path that cannot be read, or links that loop, leave the
    /// path as it was resolved so far.
    /// </summary>
    internal static string ResolveLinks(string fullPath) => ResolveLinks(fullPath, depth: 0);

    private static string ResolveLinks(string fullPath, int depth)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root) || depth > MaxLinkResolutions)
        {
            return fullPath;
        }

        var parts = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < parts.Length; index++)
        {
            var next = Path.Combine(current, parts[index]);
            try
            {
                var info = new DirectoryInfo(next);
                if (info.Exists && info.LinkTarget is not null &&
                    info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    // The target is itself resolved from its root: a link may lead through other links
                    next = ResolveLinks(NormalizeDirectory(target.FullName), depth + 1);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // An unreadable component, or a loop of links, stays as written
            }

            current = next;
        }

        return NormalizeDirectory(current);
    }

    /// <summary>Most nested resolutions of links for one path, beyond which a loop is assumed.</summary>
    private const int MaxLinkResolutions = 40;

    private static string NormalizeDirectory(string path)
    {
        var full = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(full) ?? string.Empty;
        return full.Length > root.Length
            ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : full;
    }

    private static bool IsStrictlyInside(string candidate, string container, StringComparison comparison)
    {
        var prefix = Path.EndsInDirectorySeparator(container)
            ? container
            : container + Path.DirectorySeparatorChar;
        return candidate.Length > prefix.Length && candidate.StartsWith(prefix, comparison);
    }
}
