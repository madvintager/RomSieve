using System.IO;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Applies the patch of a patched version (<see cref="ProcessItem.Patch"/>) to its origin and writes the result to a
/// file: the writing of a sort (<see cref="FileTransferExecutor"/>) and the checksums of the DAT of the collection after a
/// simulation share it.
/// </summary>
internal static class PatchApplier
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>
    /// Applies the patch of <paramref name="version"/> to its origin, read only, and writes the result to
    /// <paramref name="writePath"/>. An origin in an archive is first extracted to <paramref name="extractionPath"/> (a
    /// patch reads its source anywhere, which a compressed member does not allow), then deleted in every case: no size
    /// limit, no memory taken. The CRC32 of the data read must be the one the patch was matched by: a file changed since
    /// is refused, even for an IPS, which records nothing.
    /// </summary>
    /// <param name="formatPath">Gives the form of a path the file system takes (long Windows paths).</param>
    /// <param name="wrapSource">Wraps each stream read (test hook of the transfers).</param>
    /// <param name="wrapTarget">Wraps the stream written (test hook of the transfers).</param>
    public static async Task<PatchResult> ApplyAsync(
        IPatchService patchService,
        IChecksumService checksumService,
        PatchedVersion version,
        string writePath,
        string extractionPath,
        Func<string, string> formatPath,
        Func<Stream, Stream> wrapSource,
        Func<Stream, Stream> wrapTarget,
        CancellationToken cancellationToken)
    {
        byte[] patch;
        await using (var patchStream = wrapSource(version.File.ArchiveMember is { } member
                         ? checksumService.OpenArchiveMember(version.File.SourcePath, member)
                         : new FileStream(formatPath(version.File.SourcePath), FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous)))
        {
            patch = await patchService.ReadPatchAsync(patchStream, cancellationToken).ConfigureAwait(false);
        }

        var origin = version.Origin;
        var inArchive = !string.IsNullOrEmpty(origin.ArchivePath) && !string.IsNullOrEmpty(origin.ArchiveMember);
        try
        {
            var sourcePath = origin.SourcePath;
            if (inArchive)
            {
                await using (var memberStream = wrapSource(checksumService.OpenArchiveMember(origin.ArchivePath!, origin.ArchiveMember!)))
                await using (var copy = new FileStream(formatPath(extractionPath), FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
                {
                    await CopyAsync(memberStream, copy, cancellationToken).ConfigureAwait(false);
                }

                sourcePath = extractionPath;
            }

            await using var file = wrapSource(new FileStream(formatPath(sourcePath), FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous));
            await using var source = version.UsesOldInesHeader ? await UnderOldInesHeaderAsync(file, cancellationToken).ConfigureAwait(false) : file;
            await using var target = wrapTarget(new FileStream(formatPath(writePath), FileMode.Create, FileAccess.ReadWrite, FileShare.None, BufferSize, useAsync: true));

            // The iNES or Lynx header the patch skips goes first: the decoders write from where the target stands
            var headerCrc = await WriteRestoredHeaderAsync(version, source, target, cancellationToken).ConfigureAwait(false);
            var result = await patchService.ApplyAsync(patch, source, version.SourceOffset, version.SourceLength, target, cancellationToken).ConfigureAwait(false);
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (result.SourceCrc32 != version.SourceCrc32)
            {
                throw PatchErrors.SourceCrc(version.SourceCrc32, result.SourceCrc32);
            }

            // The size and CRC32 of the file written; a UPS or a BPS checked its own result, the data without the header
            return version.RestoredHeaderSize == 0
                ? result
                : result with
                {
                    Size = version.RestoredHeaderSize + result.Size,
                    Crc32 = Crc32Math.Combine(headerCrc, result.Crc32, result.Size)
                };
        }
        finally
        {
            if (inArchive)
            {
                TryDelete(formatPath(extractionPath));
            }
        }
    }

    /// <summary>
    /// Writes the copier header of the origin (<see cref="PatchedVersion.RestoredHeaderSize"/>) at the start of the
    /// target, and gives its CRC32; nothing for a version without one. A header that is no longer an iNES or Lynx header,
    /// or no longer the one matched, is refused: the origin changed since.
    /// </summary>
    private static async Task<uint> WriteRestoredHeaderAsync(PatchedVersion version, Stream source, Stream target, CancellationToken cancellationToken)
    {
        if (version.RestoredHeaderSize == 0)
        {
            return 0;
        }

        var header = new byte[version.RestoredHeaderSize];
        source.Seek(0, SeekOrigin.Begin);
        if (await source.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false) < header.Length
            || !PatchOriginForms.IsRestoredHeader(header))
        {
            throw PatchErrors.HeaderChanged();
        }

        var crc = System.IO.Hashing.Crc32.HashToUInt32(header);
        if (version.RestoredHeaderCrc32 is { } expected && expected != crc)
        {
            throw PatchErrors.HeaderChanged();
        }

        await target.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        return crc;
    }

    /// <summary>
    /// The origin read under the old iNES 1.0 header of its own (<see cref="PatchOriginForms.OldInesHeader"/>), as the
    /// patch expects it; the CRC32 of the source the patch checks is that of this form. A file whose header is no longer
    /// an iNES one is refused.
    /// </summary>
    private static async Task<Stream> UnderOldInesHeaderAsync(Stream file, CancellationToken cancellationToken)
    {
        var header = new byte[16];
        file.Seek(0, SeekOrigin.Begin);
        if (await file.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false) < header.Length
            || !PatchOriginForms.IsRestoredHeader(header))
        {
            throw PatchErrors.HeaderChanged();
        }

        return new HeaderOverlayStream(file, PatchOriginForms.OldInesHeader(header));
    }

    /// <summary>Deletes a temporary file, best effort: one left behind is removed by a later sort.</summary>
    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Named as a replacement of the target: the next write of the same target removes it
        }
    }

    /// <summary>Copies a stream chunk by chunk, checking the token between chunks (an archive member may ignore it).</summary>
    private static async Task CopyAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}
