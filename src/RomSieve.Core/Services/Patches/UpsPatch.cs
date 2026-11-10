using System.IO;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// UPS patches. The rules, and their sources (see <c>PlanPatch.md</c>, annex):
/// <list type="bullet">
/// <item>Signature <c>UPS1</c>, then the sizes of the source and of the result as variable-length numbers; blocks of
/// differences up to 12 bytes before the end; a footer of three CRC32 (byuu's <c>ups-spec</c>).</item>
/// <item>A block: a relative offset from the current position, common to the source and the result, then bytes
/// "source XOR result" up to a <c>0x00</c>, which advances the position too (Flips). Past the end of the source, its
/// bytes read as <c>0x00</c> (ups-spec); the result is padded with zeros or cut to its size (Flips).</item>
/// <item>The CRC32 of the patch is checked when it is read, the size of the source before applying, the CRC32 of the
/// source and of the result after (Flips, igir). The patch is applied forward only, as igir does: UPS is reversible,
/// but the source of a patch is always the original ROM here.</item>
/// </list>
/// A block may run past both files only by its final <c>0x00</c> (a patch written for a source longer than its result
/// still records the differences of the bytes the result drops, which a reverse application needs): any other byte
/// past both files refuses the patch, so that a hostile offset cannot make the decoder run without end.
/// </summary>
internal static class UpsPatch
{
    private const int SignatureLength = 4;
    private const int MinimumLength = SignatureLength + 2 + PatchFooter.Size;
    private const int BlocksPerCancellationCheck = 4096;

    public static bool IsSignature(ReadOnlySpan<byte> start) => start.StartsWith("UPS1"u8);

    public static PatchHeader ReadHeader(ReadOnlySpan<byte> patch) => ReadHeader(patch, out _);

    private static PatchHeader ReadHeader(ReadOnlySpan<byte> patch, out int bodyStart)
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
        bodyStart = position;
        return new PatchHeader(PatchFormat.Ups)
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
        var header = ReadHeader(patch.Span, out var bodyStart);
        var sourceSize = header.SourceSize ?? 0;
        var targetSize = header.TargetSize ?? 0;
        if (sourceLength != sourceSize)
        {
            throw PatchErrors.SourceSize(sourceSize, sourceLength);
        }

        var cursor = new PatchCursor(patch, bodyStart, patch.Length - PatchFooter.Size);
        var reader = new SequentialSource(source, sourceOffset, sourceLength);
        var output = new PatchOutput(target);
        var buffer = new byte[64 * 1024];
        var end = Math.Max(sourceSize, targetSize);
        long position = 0;
        var blocks = 0;

        // Copies count bytes of the source, zeros past its end, and writes those that fall within the result
        async ValueTask CopyAsync(long count)
        {
            while (count > 0)
            {
                var n = (int)Math.Min(count, buffer.Length);
                var read = await reader.ReadAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                buffer.AsSpan(read, n - read).Clear();
                var kept = (int)Math.Clamp(targetSize - position, 0, n);
                await output.WriteAsync(buffer.AsMemory(0, kept), cancellationToken).ConfigureAwait(false);
                position += n;
                count -= n;
            }
        }

        while (!cursor.AtLimit)
        {
            if (++blocks % BlocksPerCancellationCheck == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var skip = cursor.ReadNumber();
            if (position > end || skip > (ulong)(end - position))
            {
                throw PatchErrors.OutOfBounds();
            }

            await CopyAsync((long)skip).ConfigureAwait(false);
            byte xor;
            do
            {
                xor = cursor.ReadByte();
                if (xor != 0 && position >= end)
                {
                    throw PatchErrors.OutOfBounds();
                }

                var sourceByte = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
                if (position < targetSize)
                {
                    await output.WriteByteAsync((byte)(Math.Max(sourceByte, 0) ^ xor), cancellationToken).ConfigureAwait(false);
                }

                position++;
            }
            while (xor != 0);
        }

        if (position < targetSize)
        {
            await CopyAsync(targetSize - position).ConfigureAwait(false);
        }

        await reader.DrainAsync(cancellationToken).ConfigureAwait(false);
        var crc = await output.CompleteAsync(cancellationToken).ConfigureAwait(false);
        if (header.SourceCrc32 is { } expectedSource && reader.Crc32 != expectedSource)
        {
            throw PatchErrors.SourceCrc(expectedSource, reader.Crc32);
        }

        if (header.TargetCrc32 is { } expectedTarget && crc != expectedTarget)
        {
            throw PatchErrors.TargetCrc(expectedTarget, crc);
        }

        return new PatchResult(targetSize, crc, reader.Crc32);
    }
}
