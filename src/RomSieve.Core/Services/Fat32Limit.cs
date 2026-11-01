using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>A file written by a sort that is too large for a FAT32 medium (<see cref="Fat32Limit"/>).</summary>
/// <param name="Name">Name of the written file (an archive in ZIP mode).</param>
/// <param name="Size">Size in bytes; before compression in ZIP mode.</param>
/// <param name="IsUpperBound">ZIP mode: the size is the uncompressed one, so the archive may still fit.</param>
public sealed record OversizedOutputFile(string Name, long Size, bool IsUpperBound);

/// <summary>
/// File size limit of FAT32, still common on USB drives and SD cards: a file of 4 GiB or more cannot be written there.
/// </summary>
public static class Fat32Limit
{
    /// <summary>Largest file size FAT32 can store (sizes are 32-bit): 4 GiB minus one byte.</summary>
    public const long MaxFileSize = uint.MaxValue;

    /// <summary>
    /// Files the sort writes to the destination (kept items and isolated non-conforming items, with their companion tracks) that are
    /// too large for FAT32, largest first. In ZIP mode, an item and its companions form one archive, whose size is
    /// only known before compression. Links take no space of their own and cannot be created on FAT32: none is listed.
    /// </summary>
    public static IReadOnlyList<OversizedOutputFile> FindOversizedOutputFiles(
        IEnumerable<ProcessItem> items,
        TransferMode transferMode)
    {
        if (transferMode is TransferMode.HardLink or TransferMode.SymbolicLink)
        {
            return [];
        }

        var oversized = new List<OversizedOutputFile>();
        foreach (var item in items)
        {
            if (!SelectionFilter.IsWritten(item))
            {
                continue;
            }

            // A MAME set and its disks are written as they are, even in ZIP mode
            if (transferMode == TransferMode.Zip && !item.IsWholeArchive && !item.Parsed.Convention.IsMameSets())
            {
                var archiveSize = item.FileSize + item.RelatedItems.Sum(r => r.FileSize);
                if (archiveSize > MaxFileSize)
                {
                    oversized.Add(new OversizedOutputFile(GetWrittenName(item), archiveSize, IsUpperBound: true));
                }

                continue;
            }

            foreach (var file in item.RelatedItems.Prepend(item))
            {
                if (file.FileSize > MaxFileSize)
                {
                    oversized.Add(new OversizedOutputFile(GetWrittenName(file), file.FileSize, IsUpperBound: false));
                }
            }
        }

        return oversized
            .OrderByDescending(f => f.Size)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Number of file names given as examples in the log line (<see cref="CreateLogMessage"/>).</summary>
    public const int LogExampleCount = 3;

    /// <summary>
    /// The single log warning about the files too large for FAT32 (<see cref="FindOversizedOutputFiles"/>), or null when
    /// there are none: the name of a lone file, otherwise the count and the first <see cref="LogExampleCount"/> names
    /// followed by "…". In ZIP mode, the sizes are taken before compression: the line says the files may still fit.
    /// Within a run, the files are all archives measured before compression or none are (a MAME set is never zipped).
    /// </summary>
    public static LocalizedMessage? CreateLogMessage(IReadOnlyList<OversizedOutputFile> files)
    {
        if (files.Count == 0)
        {
            return null;
        }

        var isUpperBound = files.Any(f => f.IsUpperBound);
        if (files.Count == 1)
        {
            return LocalizedMessage.Create(
                isUpperBound ? "log.fat32_oversized_zip" : "log.fat32_oversized",
                ("name", files[0].Name));
        }

        var examples = string.Join(", ", files.Take(LogExampleCount).Select(f => f.Name))
            + (files.Count > LogExampleCount ? ", …" : string.Empty);
        return LocalizedMessage.Create(
            isUpperBound ? "log.fat32_oversized_files_zip" : "log.fat32_oversized_files",
            ("count", files.Count),
            ("examples", examples));
    }

    private static string GetWrittenName(ProcessItem item)
    {
        var target = item.FinalTargetPath ?? item.TargetPath;
        return !string.IsNullOrEmpty(target) ? Path.GetFileName(target) : item.DisplayName;
    }
}
