using System.IO;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// BPS patches. The rules, and their sources (see <c>PlanPatch.md</c>, annex):
/// <list type="bullet">
/// <item>Signature <c>BPS1</c>, then the sizes of the source and of the result and the size of the metadata, as
/// variable-length numbers, then the metadata, skipped whatever they hold (byuu's <c>bps_spec</c>).</item>
/// <item>Actions up to 12 bytes before the end: <c>action = data &amp; 3</c>, <c>length = (data &gt;&gt; 2) + 1</c>.
/// <c>SourceRead</c> copies the source at the position of the output; <c>TargetRead</c> copies bytes of the patch;
/// <c>SourceCopy</c> and <c>TargetCopy</c> move their own counter by a relative offset (low bit: the sign), then copy
/// from the source or from the result already written, which <c>TargetCopy</c> may overlap to repeat data
/// (bps_spec).</item>
/// <item>The source counter stays within the source, the target counter before the output; any read outside the source
/// or the result refuses the patch (bps_spec). The patch must end exactly at its footer, the result at its size
/// (Flips).</item>
/// <item>Checks in the order of Flips: CRC32 of the patch (when it is read), size and CRC32 of the source, the actions,
/// the exact end, CRC32 of the result.</item>
/// </list>
/// </summary>
internal static class BpsPatch
{
    private const int SignatureLength = 4;
    private const int MinimumLength = SignatureLength + 3 + PatchFooter.Size;
    private const int CopyChunk = 64 * 1024;
    private const int ActionsPerCancellationCheck = 4096;

    private const ulong SourceRead = 0;
    private const ulong TargetRead = 1;
    private const ulong SourceCopy = 2;

    public static bool IsSignature(ReadOnlySpan<byte> start) => start.StartsWith("BPS1"u8);

    public static PatchHeader ReadHeader(ReadOnlySpan<byte> patch) => ReadHeader(patch, out _);

    private static PatchHeader ReadHeader(ReadOnlySpan<byte> patch, out int actionsStart)
    {
        if (patch.Length >= SignatureLength && !IsSignature(patch))
        {
            throw PatchErrors.UnknownSignature();
        }

        if (patch.Length < MinimumLength)
        {
            throw PatchErrors.Truncated();
        }

        var footer = PatchFooter.ReadAndCheck(patch);
        var position = SignatureLength;
        var limit = patch.Length - PatchFooter.Size;
        var sourceSize = PatchNumbers.ReadSize(patch, ref position, limit);
        var targetSize = PatchNumbers.ReadSize(patch, ref position, limit);
        var metadataSize = PatchNumbers.Read(patch, ref position, limit);
        if (metadataSize > (ulong)(limit - position))
        {
            throw PatchErrors.Truncated();
        }

        actionsStart = position + (int)metadataSize;
        return new PatchHeader(PatchFormat.Bps)
        {
            SourceSize = sourceSize,
            TargetSize = targetSize,
            SourceCrc32 = footer.SourceCrc32,
            TargetCrc32 = footer.TargetCrc32,
            PatchCrc32 = footer.PatchCrc32
        };
    }

    public static async Task<PatchResult> ApplyAsync(
        ReadOnlyMemory<byte> patch,
        Stream source,
        long sourceOffset,
        long sourceLength,
        Stream target,
        CancellationToken cancellationToken)
    {
        var header = ReadHeader(patch.Span, out var actionsStart);
        var sourceSize = header.SourceSize ?? 0;
        var targetSize = header.TargetSize ?? 0;
        if (sourceLength != sourceSize)
        {
            throw PatchErrors.SourceSize(sourceSize, sourceLength);
        }

        // The actions read the source anywhere: its CRC32 is checked before, in one pass
        var sourceReader = new SequentialSource(source, sourceOffset, sourceLength);
        await sourceReader.DrainAsync(cancellationToken).ConfigureAwait(false);
        var sourceCrc = sourceReader.Crc32;
        if (header.SourceCrc32 is { } expectedSource && sourceCrc != expectedSource)
        {
            throw PatchErrors.SourceCrc(expectedSource, sourceCrc);
        }

        var cursor = new PatchCursor(patch, actionsStart, patch.Length - PatchFooter.Size);
        var output = new PatchOutput(target);
        var buffer = new byte[CopyChunk];
        long sourceRelative = 0;
        long targetRelative = 0;
        var actions = 0;

        // Copies count bytes of the source from position, in chunks
        async ValueTask CopySourceAsync(long position, long count)
        {
            while (count > 0)
            {
                var n = (int)Math.Min(count, CopyChunk);
                source.Seek(sourceOffset + position, SeekOrigin.Begin);
                await source.ReadExactlyAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                position += n;
                count -= n;
            }
        }

        while (!cursor.AtLimit)
        {
            if (++actions % ActionsPerCancellationCheck == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var data = cursor.ReadNumber();
            var length = (data >> 2) + 1;
            var outputOffset = output.Length;
            if (length > (ulong)(targetSize - outputOffset))
            {
                throw PatchErrors.OutOfBounds();
            }

            var count = (long)length;
            switch (data & 3)
            {
                case SourceRead:
                    if (outputOffset + count > sourceSize)
                    {
                        throw PatchErrors.OutOfBounds();
                    }

                    await CopySourceAsync(outputOffset, count).ConfigureAwait(false);
                    break;

                case TargetRead:
                    await output.WriteAsync(cursor.Read(count), cancellationToken).ConfigureAwait(false);
                    break;

                case SourceCopy:
                    sourceRelative = cursor.ReadRelativeOffset(sourceRelative, sourceSize);
                    if (count > sourceSize - sourceRelative)
                    {
                        throw PatchErrors.OutOfBounds();
                    }

                    await CopySourceAsync(sourceRelative, count).ConfigureAwait(false);
                    sourceRelative += count;
                    break;

                default:
                    targetRelative = cursor.ReadRelativeOffset(targetRelative, outputOffset);
                    if (targetRelative >= outputOffset)
                    {
                        throw PatchErrors.OutOfBounds();
                    }

                    await CopyTargetAsync(output, buffer, targetRelative, count, cancellationToken).ConfigureAwait(false);
                    targetRelative += count;
                    break;
            }
        }

        if (output.Length != targetSize)
        {
            throw PatchErrors.Incomplete();
        }

        var crc = await output.CompleteAsync(cancellationToken).ConfigureAwait(false);
        if (header.TargetCrc32 is { } expectedTarget && crc != expectedTarget)
        {
            throw PatchErrors.TargetCrc(expectedTarget, crc);
        }

        return new PatchResult(targetSize, crc, sourceCrc);
    }

    /// <summary>
    /// <c>TargetCopy</c>: copies <paramref name="count"/> bytes of the result from <paramref name="from"/> to its end.
    /// When the copy overlaps what it writes, the bytes repeat with the period of the distance between the two (each new
    /// byte is the one that distance before it): the period is read once, then repeated, rather than byte by byte.
    /// </summary>
    private static async ValueTask CopyTargetAsync(PatchOutput output, byte[] buffer, long from, long count, CancellationToken cancellationToken)
    {
        while (count > 0)
        {
            var chunk = (int)Math.Min(count, buffer.Length);
            var period = (int)Math.Min(chunk, output.Length - from);
            await output.ReadBackAsync(from, buffer.AsMemory(0, period), cancellationToken).ConfigureAwait(false);
            for (var i = period; i < chunk; i++)
            {
                buffer[i] = buffer[i - period];
            }

            await output.WriteAsync(buffer.AsMemory(0, chunk), cancellationToken).ConfigureAwait(false);
            from += chunk;
            count -= chunk;
        }
    }
}
