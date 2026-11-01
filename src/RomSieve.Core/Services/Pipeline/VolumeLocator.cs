using System.IO;

namespace RomSieve.Core.Services;

/// <summary>
/// Determines whether two paths live on the same volume, to choose between an atomic rename
/// and a cancellable copy followed by a deletion when moving files.
/// </summary>
public static class VolumeLocator
{
    /// <summary>
    /// Returns the root directories of the volumes mounted on this machine
    /// (drive roots on Windows, mount points on Linux), or an empty list when they cannot be read.
    /// </summary>
    public static IReadOnlyList<string> GetVolumeRoots()
    {
        try
        {
            return DriveInfo.GetDrives().Select(d => d.RootDirectory.FullName).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Indicates whether both absolute paths belong to the same volume, identified as the longest
    /// volume root containing each path. A path outside every known volume is considered to be
    /// on a different volume, which makes callers fall back to the safe copy-then-delete strategy.
    /// </summary>
    public static bool AreOnSameVolume(string sourcePath, string targetPath, IEnumerable<string> volumeRoots, bool ignoreCase)
    {
        var roots = volumeRoots.ToList();
        var sourceRoot = FindVolumeRoot(sourcePath, roots, ignoreCase);
        var targetRoot = FindVolumeRoot(targetPath, roots, ignoreCase);
        return sourceRoot is not null &&
               targetRoot is not null &&
               string.Equals(sourceRoot, targetRoot, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>
    /// Root of the volume of the absolute path <paramref name="path"/>, as listed in <paramref name="roots"/>: the
    /// longest one containing it, or <c>null</c> for a path outside every known volume (a network share).
    /// </summary>
    public static string? FindVolumeRoot(string path, IReadOnlyList<string> roots, bool ignoreCase)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string? best = null;
        var bestLength = -1;
        foreach (var root in roots)
        {
            var trimmedRoot = root.TrimEnd('/', '\\');
            var isUnder = string.Equals(path, trimmedRoot, comparison) ||
                          (path.Length > trimmedRoot.Length &&
                           path.StartsWith(trimmedRoot, comparison) &&
                           path[trimmedRoot.Length] is '/' or '\\');
            if (isUnder && trimmedRoot.Length > bestLength)
            {
                best = root;
                bestLength = trimmedRoot.Length;
            }
        }

        return best;
    }
}
