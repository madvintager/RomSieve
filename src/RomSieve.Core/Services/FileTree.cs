using System.IO;
using System.IO.Enumeration;

namespace RomSieve.Core.Services;

/// <summary>
/// Files of a folder as a scan reads them (the source, the CHD folder of a MAME collection). The folders and files that
/// a volume, a recycle bin, a NAS or a file manager adds are never part of a collection and are skipped
/// (<see cref="IsSystemFolder"/>, <see cref="IsSystemFile"/>). A folder that cannot be read is skipped instead of
/// stopping the whole scan, and a link to a folder is not followed, since it could loop or lead out of the folder: both
/// are reported (<see cref="FileTreeSkips"/>). The files come folder by folder, as
/// <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/> gives them (those of a folder, then those of its
/// subfolders), but in the order of their names whatever the file system (<see cref="CompareNames"/>).
/// </summary>
public static class FileTree
{
    /// <summary>Folders of the system: volume information, recycle bins, snapshots and metadata of NAS and Mac.</summary>
    private static readonly HashSet<string> SystemFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Volume Information", "$RECYCLE.BIN", "RECYCLER", "RECYCLED",
        "lost+found", ".Trash",
        ".Trashes", ".Spotlight-V100", ".fseventsd", ".TemporaryItems", ".DocumentRevisions-V100",
        "#recycle", "#snapshot", "@eaDir",
        "@Recycle", "@Recently-Snapshot", ".@__thumb"
    };

    /// <summary>Files of the system: thumbnails and folder settings of Windows, folder settings of a Mac.</summary>
    private static readonly HashSet<string> SystemFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Thumbs.db", "ehthumbs.db", "ehthumbs_vista.db", "desktop.ini", ".DS_Store"
    };

    /// <summary>Whether a folder belongs to the system, not to a collection (the recycle bin of a user, <c>.Trash-1000</c>, as well).</summary>
    public static bool IsSystemFolder(string name) =>
        SystemFolderNames.Contains(name) || name.StartsWith(".Trash-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a file belongs to the system, not to a collection (the resource forks a Mac writes, <c>._*</c>, as well).</summary>
    public static bool IsSystemFile(string name) =>
        SystemFileNames.Contains(name) || name.StartsWith("._", StringComparison.Ordinal);

    private static readonly EnumerationOptions FolderOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0
    };

    private readonly record struct Entry(string Path, string Name, bool IsDirectory, bool IsReparsePoint);

    /// <summary>
    /// Files under <paramref name="directory"/>, with its subfolders when <paramref name="recursive"/>; what was skipped
    /// is added to <paramref name="skips"/> as the enumeration goes.
    /// </summary>
    public static IEnumerable<string> EnumerateFiles(string directory, bool recursive, FileTreeSkips skips)
    {
        var pending = new Queue<string>();
        pending.Enqueue(directory);
        while (pending.TryDequeue(out var current))
        {
            IEnumerator<Entry> entries;
            try
            {
                entries = new FileSystemEnumerable<Entry>(
                    current,
                    static (ref FileSystemEntry entry) => new Entry(
                        entry.ToFullPath(),
                        entry.FileName.ToString(),
                        entry.IsDirectory,
                        (entry.Attributes & FileAttributes.ReparsePoint) != 0),
                    FolderOptions).GetEnumerator();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skips.UnreadableFolders.Add(current);
                continue;
            }

            var files = new List<Entry>();
            var folders = new List<Entry>();
            using (entries)
            {
                while (true)
                {
                    Entry entry;
                    try
                    {
                        if (!entries.MoveNext())
                        {
                            break;
                        }

                        entry = entries.Current;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // The folder went away or stopped answering: the files already listed stay
                        skips.UnreadableFolders.Add(current);
                        break;
                    }

                    if (!entry.IsDirectory)
                    {
                        if (!IsSystemFile(entry.Name))
                        {
                            files.Add(entry);
                        }
                    }
                    else if (recursive && !IsSystemFolder(entry.Name))
                    {
                        folders.Add(entry);
                    }
                }
            }

            files.Sort(CompareNames);
            foreach (var file in files)
            {
                yield return file.Path;
            }

            folders.Sort(CompareNames);
            foreach (var folder in folders)
            {
                if (folder.IsReparsePoint && IsLink(folder.Path))
                {
                    skips.FolderLinks.Add(folder.Path);
                }
                else
                {
                    pending.Enqueue(folder.Path);
                }
            }
        }
    }

    /// <summary>
    /// Order of the entries of a folder: by name, case ignored, then exact. The order of an enumeration depends on the
    /// file system (alphabetical on NTFS, hashed on ext4, so different from one folder to another), and the order of the
    /// files decides which of two files with the same target keeps its name and the order of the exports: the same
    /// source gives the same sort on every system, the one NTFS gave.
    /// </summary>
    private static int CompareNames(Entry left, Entry right)
    {
        var ignoringCase = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        return ignoringCase != 0 ? ignoringCase : StringComparer.Ordinal.Compare(left.Name, right.Name);
    }

    /// <summary>
    /// Whether a folder is a symbolic link or a junction. Other reparse points, such as the folders of a cloud storage
    /// kept on the disk, are ordinary folders.
    /// </summary>
    private static bool IsLink(string path)
    {
        try
        {
            return new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A link that cannot be read is not followed either
            return true;
        }
    }
}

/// <summary>What a listing of <see cref="FileTree"/> skipped, to be reported: folders it could not read, links to a folder it did not follow.</summary>
public sealed class FileTreeSkips
{
    public List<string> UnreadableFolders { get; } = [];

    public List<string> FolderLinks { get; } = [];
}
