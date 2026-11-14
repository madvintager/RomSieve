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

    /// <summary>
    /// The copier header a patched version gets back in front of its result, when its patch skips
    /// <paramref name="sourceOffset"/> bytes of its origin: the iNES (16 bytes) and Lynx (64 bytes) headers, which most
    /// emulators of these systems need; never the 512 bytes of a SNES copier header, whose data alone are the usual form.
    /// <see cref="IChecksumService.DetectCopierHeader"/> gives 16 only for iNES and 64 only for Lynx. 0 otherwise.
    /// </summary>
    public static int RestoredHeaderSize(long sourceOffset) => sourceOffset is 16 or 64 ? (int)sourceOffset : 0;

    /// <summary>Whether <paramref name="header"/> is an iNES header of 16 bytes or a Lynx header of 64 bytes, by its signature.</summary>
    public static bool IsRestoredHeader(ReadOnlySpan<byte> header) =>
        header.Length switch
        {
            16 => header.StartsWith("NES\u001a"u8),
            64 => header.StartsWith("LYNX"u8),
            _ => false
        };

    /// <summary>
    /// The first <paramref name="size"/> bytes of an identified file (on the disk or in its archive), and their CRC32;
    /// <see langword="null"/> when they cannot be read or are not a header <see cref="IsRestoredHeader"/> accepts.
    /// </summary>
    public static async Task<uint?> ReadRestoredHeaderCrcAsync(ProcessItem item, int size, IChecksumService checksumService, CancellationToken cancellationToken)
    {
        try
        {
            await using Stream stream = !string.IsNullOrEmpty(item.ArchivePath) && !string.IsNullOrEmpty(item.ArchiveMember)
                ? checksumService.OpenArchiveMember(item.ArchivePath, item.ArchiveMember)
                : new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var header = new byte[size];
            var read = await stream.ReadAtLeastAsync(header, size, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            return read == size && IsRestoredHeader(header) ? System.IO.Hashing.Crc32.HashToUInt32(header) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

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
