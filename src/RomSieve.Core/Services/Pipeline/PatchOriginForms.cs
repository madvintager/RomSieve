using System.Globalization;
using System.IO;
using System.IO.Hashing;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// The forms of the data of an identified file a patch may expect, with and without its copier header: the form it was
/// identified in (<see cref="ProcessItem.CopierHeaderSize"/> bytes skipped), and the other one, read again on demand.
/// Shared by the matching of the patches (<see cref="PatchMatcher"/>) and the choice of an origin by the user
/// (<see cref="PatchLinkService.GetOriginFormsAsync"/>).
/// </summary>
internal static class PatchOriginForms
{
    /// <summary>Sizes of the copier headers <see cref="IChecksumService.DetectCopierHeader"/> finds: iNES, Lynx, SNES.</summary>
    private static readonly int[] CopierHeaderSizes = [16, 64, 512];

    /// <summary>The form a file was identified in: its data after the copier header the classification removed.</summary>
    public static (long Offset, long Size, uint Crc32)? IdentifiedForm(ProcessItem item) =>
        uint.TryParse(item.Crc32, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var crc)
            ? (item.CopierHeaderSize, item.FileSize - item.CopierHeaderSize, crc)
            : null;

    /// <summary>
    /// The other form of the data of a file, when <paramref name="mayFit"/> accepts its size: the whole file of a file
    /// identified without its copier header (its CRC32 from the CRC cache, or computed and cached; on the disk only), the
    /// data after the copier header of a file identified whole. <see langword="null"/> without one, or for a file that
    /// cannot be read again.
    /// </summary>
    public static async Task<(long Offset, long Size, uint Crc32)?> OtherFormAsync(
        ProcessItem item,
        Func<long, bool> mayFit,
        IChecksumService checksumService,
        ICrcCacheService crcCache,
        CancellationToken cancellationToken)
    {
        try
        {
            if (item.CopierHeaderSize > 0)
            {
                if (mayFit(item.FileSize) && string.IsNullOrEmpty(item.ArchiveMember))
                {
                    var crc = crcCache.Get(item.SourcePath);
                    if (crc is null)
                    {
                        crc = await checksumService.ComputeFileCrc32Async(item.SourcePath, cancellationToken: cancellationToken).ConfigureAwait(false);
                        crcCache.Set(item.SourcePath, crc);
                    }

                    return (0, item.FileSize, uint.Parse(crc, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                }
            }
            else if (CopierHeaderSizes.Any(h => item.FileSize > h && mayFit(item.FileSize - h))
                     && await ReadWithoutHeaderAsync(item, checksumService, cancellationToken).ConfigureAwait(false) is { } withoutHeader)
            {
                return (withoutHeader.Header, item.FileSize - withoutHeader.Header, withoutHeader.Crc);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // A file that cannot be read again gives no other form
        }

        return null;
    }

    /// <summary>The copier header of a file identified whole, and the CRC32 of its data after it; <see langword="null"/> without header.</summary>
    private static async Task<(int Header, uint Crc)?> ReadWithoutHeaderAsync(ProcessItem item, IChecksumService checksumService, CancellationToken cancellationToken)
    {
        await using Stream stream = !string.IsNullOrEmpty(item.ArchivePath) && !string.IsNullOrEmpty(item.ArchiveMember)
            ? checksumService.OpenArchiveMember(item.ArchivePath, item.ArchiveMember)
            : new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var start = new byte[64];
        var read = await stream.ReadAtLeastAsync(start, start.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        var header = checksumService.DetectCopierHeader(start.AsSpan(0, read), item.FileSize);
        if (header <= 0 || header >= item.FileSize)
        {
            return null;
        }

        var crc = new Crc32();
        if (header < read)
        {
            crc.Append(start.AsSpan(header, read - header));
        }
        else if (header > read)
        {
            await SkipAsync(stream, header - read, cancellationToken).ConfigureAwait(false);
        }

        var buffer = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            crc.Append(buffer.AsSpan(0, n));
        }

        return (header, crc.GetCurrentHashAsUInt32());
    }

    private static async Task SkipAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(count, 4096)];
        while (count > 0)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(count, buffer.Length)), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException();
            }

            count -= n;
        }
    }
}
