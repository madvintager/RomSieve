using System.Globalization;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Safeguards of a destination directory: the marker that RomSieve writes in the destinations of its real sorts
/// (<see cref="MarkerFileName"/>), and the quarantine where a purge moves the previous content
/// (<see cref="QuarantineDirectoryName"/>) instead of deleting it.
/// </summary>
public static class DestinationGuard
{
    /// <summary>Marker file of a destination written by RomSieve (hidden).</summary>
    public const string MarkerFileName = ".romsieve";

    /// <summary>Directory of the destination where each purge moves the previous content, in a dated subdirectory.</summary>
    public const string QuarantineDirectoryName = "_purged";

    /// <summary>
    /// Extension of the file that a transfer writes next to its target before putting it in its place once complete:
    /// <c>&lt;target&gt;.&lt;8 hexadecimal digits&gt;.romsieve-tmp</c>. Only a process stopped between the two leaves one
    /// behind, never a truncated file under the name of the target.
    /// </summary>
    public const string ReplacementExtension = ".romsieve-tmp";

    private const int ReplacementIdLength = 8;

    /// <summary>
    /// Age from which a temporary file of RomSieve is taken as left behind by a stopped process: never one that another
    /// process (the interface and the command line at once) is still writing.
    /// </summary>
    public static readonly TimeSpan StaleTemporaryFileAge = TimeSpan.FromHours(1);

    /// <summary>File a replacement of <paramref name="targetPath"/> is written to.</summary>
    internal static string CreateReplacementPath(string targetPath) =>
        $"{targetPath}.{Guid.NewGuid().ToString("N")[..ReplacementIdLength]}{ReplacementExtension}";

    /// <summary>Whether a file is a replacement that RomSieve writes, and nothing else of the same extension.</summary>
    public static bool IsReplacementFile(string path) => GetReplacedFileName(Path.GetFileName(path)) is not null;

    /// <summary>Whether <paramref name="fileName"/> is a replacement of the file named <paramref name="targetFileName"/>.</summary>
    internal static bool IsReplacementOf(string fileName, string targetFileName) =>
        string.Equals(GetReplacedFileName(fileName), targetFileName, StringComparison.Ordinal);

    private static string? GetReplacedFileName(string fileName)
    {
        if (!fileName.EndsWith(ReplacementExtension, StringComparison.Ordinal))
        {
            return null;
        }

        var stem = fileName[..^ReplacementExtension.Length];
        var dot = stem.Length - ReplacementIdLength - 1;
        return dot > 0 && stem[dot] == '.' && stem[(dot + 1)..].All(char.IsAsciiHexDigitLower) ? stem[..dot] : null;
    }

    /// <summary>
    /// Content of the destination besides the marker, the quarantine and the folders of the system
    /// (<see cref="GetPurgeableEntries"/>), and presence of the marker.
    /// </summary>
    public static DestinationState Inspect(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            return new DestinationState(HasContent: false, HasMarker: false);
        }

        try
        {
            return new DestinationState(GetPurgeableEntries(path).Count > 0, File.Exists(Path.Combine(path, MarkerFileName)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable destination is treated as foreign content, which asks for every confirmation
            return new DestinationState(HasContent: true, HasMarker: false);
        }
    }

    /// <summary>
    /// Entries of the destination that a purge moves to the quarantine: everything but the marker, the quarantine, and the
    /// folders and files of the system (<see cref="FileTree.IsSystemFolder"/>, <see cref="FileTree.IsSystemFile"/>). At the
    /// root of a disk, its volume information and its recycle bin belong to the system: they neither make the destination
    /// a non-empty one, nor ever move.
    /// </summary>
    public static IReadOnlyList<string> GetPurgeableEntries(string path) =>
        Directory.EnumerateFileSystemEntries(path)
            .Where(entry => Path.GetFileName(entry) is var name && !IsReservedName(name) && !FileTree.IsSystemFolder(name) && !FileTree.IsSystemFile(name))
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Creates the dated quarantine directory of a purge (<c>_purged/yyyyMMdd-HHmmss</c>, with a suffix if it exists).
    /// It lies in the destination, hence on its volume: moving the content there is instant and takes no extra space.
    /// </summary>
    public static string CreateQuarantineDirectory(string destination, DateTime utcNow)
    {
        var root = Path.Combine(destination, QuarantineDirectoryName);
        var stamp = utcNow.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(root, stamp);
        for (var index = 2; Directory.Exists(path) || File.Exists(path); index++)
        {
            path = Path.Combine(root, $"{stamp}_{index:00}");
        }

        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Writes the marker of a destination of RomSieve if it is missing, and returns its path when it was created.
    /// Best effort: a failure only loses the marker.
    /// </summary>
    public static string? WriteMarker(string destination)
    {
        var path = Path.Combine(destination, MarkerFileName);
        try
        {
            if (File.Exists(path))
            {
                return null;
            }

            Directory.CreateDirectory(destination);
            File.WriteAllText(path, "RomSieve destination" + Environment.NewLine + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + Environment.NewLine);
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            }

            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without a marker, the next purge of this destination only asks for one more confirmation
            return null;
        }
    }

    /// <summary>
    /// Whether a path lies in the quarantine of a marked destination, is a marker or a replacement left by a stopped
    /// sort, which a scan must skip: a former destination used as a source does not sort its purged content again.
    /// </summary>
    public static bool IsGuardPath(string path)
    {
        // A replacement left by a stopped sort is no file of the collection either
        if (string.Equals(Path.GetFileName(path), MarkerFileName, StringComparison.Ordinal) || IsReplacementFile(path))
        {
            return true;
        }

        var directory = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(directory))
        {
            if (string.Equals(Path.GetFileName(directory), QuarantineDirectoryName, StringComparison.Ordinal) &&
                Path.GetDirectoryName(directory) is { Length: > 0 } owner &&
                File.Exists(Path.Combine(owner, MarkerFileName)))
            {
                return true;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return false;
    }

    /// <summary>
    /// Quarantine of a destination written by RomSieve, or <c>null</c> when it has none or it is empty. A <c>_purged</c>
    /// directory of an unmarked destination, or a <c>_purged</c> that is a link, is never taken for a quarantine. Links
    /// inside it are counted as entries and never followed.
    /// </summary>
    public static QuarantineInfo? GetQuarantine(string destination, CancellationToken cancellationToken = default)
    {
        if (GetQuarantineDirectory(destination) is not { } quarantine)
        {
            return null;
        }

        try
        {
            var purges = quarantine.EnumerateFileSystemInfos().ToList();
            if (purges.Count == 0)
            {
                return null;
            }

            long files = 0;
            long bytes = 0;
            foreach (var entry in purges)
            {
                var (entryFiles, entryBytes) = Measure(entry, cancellationToken);
                files += entryFiles;
                bytes += entryBytes;
            }

            return new QuarantineInfo(quarantine.FullName, purges.Count, files, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes the content of the quarantine of a destination written by RomSieve, then the quarantine itself once
    /// empty. This is the only deletion of RomSieve that cannot be undone: the purged content can no longer be put back
    /// by undoing its sort (the undo then reports it as missing). Links are deleted, never followed, so nothing outside
    /// the quarantine is ever touched. An entry that cannot be deleted is reported and stays; nothing is done without a
    /// quarantine of a marked destination.
    /// </summary>
    public static QuarantineEmptyResult EmptyQuarantine(string destination, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        long files = 0;
        long bytes = 0;
        if (GetQuarantineDirectory(destination) is not { } quarantine)
        {
            return new QuarantineEmptyResult(0, 0, errors);
        }

        void Delete(FileSystemInfo entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (entry is DirectoryInfo directory && directory.LinkTarget is null)
                {
                    foreach (var child in directory.EnumerateFileSystemInfos())
                    {
                        Delete(child);
                    }

                    directory.Delete(recursive: false);
                    return;
                }

                // A file, or a link (to a file or a directory), which is deleted without touching its target
                var size = entry is FileInfo { LinkTarget: null } file ? file.Length : 0;
                if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    entry.Attributes &= ~FileAttributes.ReadOnly;
                }

                entry.Delete();
                files++;
                bytes += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{entry.FullName}: {ex.Message}");
            }
        }

        try
        {
            foreach (var entry in quarantine.EnumerateFileSystemInfos().ToList())
            {
                Delete(entry);
            }

            if (!quarantine.EnumerateFileSystemInfos().Any())
            {
                quarantine.Delete(recursive: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"{quarantine.FullName}: {ex.Message}");
        }

        return new QuarantineEmptyResult(files, bytes, errors);
    }

    /// <summary>The quarantine directory of a marked destination, if it exists and is a real directory, not a link.</summary>
    private static DirectoryInfo? GetQuarantineDirectory(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination) || !File.Exists(Path.Combine(destination, MarkerFileName)))
        {
            return null;
        }

        var quarantine = new DirectoryInfo(Path.Combine(destination, QuarantineDirectoryName));
        return quarantine.Exists && quarantine.LinkTarget is null ? quarantine : null;
    }

    /// <summary>Files and bytes under an entry, without following links (a link counts as a file of no size).</summary>
    private static (long Files, long Bytes) Measure(FileSystemInfo entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.LinkTarget is not null)
        {
            return (1, 0);
        }

        if (entry is FileInfo file)
        {
            return (1, file.Length);
        }

        long files = 0;
        long bytes = 0;
        try
        {
            foreach (var child in ((DirectoryInfo)entry).EnumerateFileSystemInfos())
            {
                var (childFiles, childBytes) = Measure(child, cancellationToken);
                files += childFiles;
                bytes += childBytes;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable directory counts for what could be read: the quarantine is still shown
        }

        return (files, bytes);
    }

    private static bool IsReservedName(string? name) =>
        string.Equals(name, MarkerFileName, StringComparison.Ordinal) ||
        string.Equals(name, QuarantineDirectoryName, StringComparison.Ordinal);
}
