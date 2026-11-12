using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Room a sort needs on the volume of its destination, compared with the room left there. A sort that lacks room only
/// stops once the destination is full, so the shortfall is said before: in the log of the run, from the simulation on,
/// and in the confirmation of the sort.
/// </summary>
public static class DestinationSpace
{
    /// <summary>
    /// Bytes the transfers of <paramref name="items"/> write on the volume of the destination: the files the sort
    /// writes (kept items and isolated non-conforming ones, with their companion files; before compression in ZIP
    /// mode), except links, which take no room, and files moved within the volume, which are only renamed. A member of
    /// an archive is extracted, even in Move mode. Files already in the destination are counted: the result is the most
    /// the sort may write.
    /// </summary>
    public static long GetRequiredBytes(IEnumerable<ProcessItem> items, SortConfig config, IReadOnlyList<string> volumeRoots)
    {
        if (config.TransferMode is TransferMode.HardLink or TransferMode.SymbolicLink || string.IsNullOrWhiteSpace(config.DestinationDir))
        {
            return 0;
        }

        var destination = Path.GetFullPath(config.DestinationDir);
        var ignoreCase = OperatingSystem.IsWindows();
        long total = 0;
        foreach (var item in items.Where(SelectionFilter.IsWritten))
        {
            if (item.PreservesSourceArchive(config))
            {
                var isRenamed = config.TransferMode == TransferMode.Move &&
                                VolumeLocator.AreOnSameVolume(Path.GetFullPath(item.ArchivePath!), destination, volumeRoots, ignoreCase);
                if (!isRenamed)
                {
                    total += item.ArchiveFileSize > 0 ? item.ArchiveFileSize : item.FileSize;
                }
                continue;
            }

            foreach (var file in item.RelatedItems.Prepend(item))
            {
                var isRenamed = config.TransferMode == TransferMode.Move &&
                                file.ArchiveMember is null &&
                                VolumeLocator.AreOnSameVolume(Path.GetFullPath(file.SourcePath), destination, volumeRoots, ignoreCase);
                if (!isRenamed)
                {
                    total += file.FileSize;
                }
            }
        }

        return total;
    }

    /// <summary>
    /// Room left to the user on the volume of <paramref name="path"/>, which need not exist yet; <c>null</c> when it
    /// cannot be known (a network share outside the volumes of <paramref name="volumeRoots"/>, a volume that cannot be
    /// read).
    /// </summary>
    public static long? GetAvailableBytes(string path, IReadOnlyList<string> volumeRoots)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var root = VolumeLocator.FindVolumeRoot(Path.GetFullPath(path), volumeRoots, ignoreCase: OperatingSystem.IsWindows());
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The shortfall of room of <paramref name="destination"/> for <paramref name="requiredBytes"/>, or <c>null</c> when it
    /// has room or its room is not known.
    /// </summary>
    public static SpaceShortfall? FindShortfall(long requiredBytes, long? availableBytes, string destination) =>
        availableBytes is { } available && requiredBytes > available
            ? new SpaceShortfall(destination, requiredBytes, available)
            : null;
}

/// <summary>A destination that lacks room for what a sort writes there (<see cref="DestinationSpace.FindShortfall"/>).</summary>
public sealed record SpaceShortfall(string Destination, long RequiredBytes, long AvailableBytes)
{
    /// <summary>Message <paramref name="key"/> with the destination (<c>{dest}</c>) and both sizes (<c>{required}</c>, <c>{available}</c>).</summary>
    public LocalizedMessage ToMessage(string key) =>
        LocalizedMessage.Create(
            key,
            ("dest", Destination),
            ("required", SizeFormatting.ToMessage(RequiredBytes)),
            ("available", SizeFormatting.ToMessage(AvailableBytes)));
}
