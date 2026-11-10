using System.IO;
using System.IO.Hashing;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// IPS and IPS32 patches. The rules, and their sources (see <c>PlanPatch.md</c>, annex):
/// <list type="bullet">
/// <item>IPS: signature <c>PATCH</c>; records of a 3-byte offset, a 2-byte size and the data, big-endian; a zero size
/// announces a run (RLE): a 2-byte length, then the byte to repeat; footer <c>EOF</c> (ZeroSoft, SnesLab). An offset
/// equal to <c>0x454F46</c> reads as the footer (Flips, igir): no tool writes it.</item>
/// <item>The records apply in the order of the file, a later one overwriting an earlier one; a record past the end of
/// the source grows the result up to its last byte (Rom Patcher JS, Flips, igir). A run of length zero writes nothing
/// (ZeroSoft asks for a nonzero length: such a record does no harm).</item>
/// <item>Truncation, an extension of IPS (SNESTool, Lunar IPS; SnesLab): exactly 3 bytes after <c>EOF</c> give the size
/// the result is cut to. It only shortens the result, as igir does; any other number of bytes after <c>EOF</c> than 0
/// or 3 refuses the patch, as Flips does.</item>
/// <item>IPS32, which no specification describes (igir, Citron, ips.py): signature <c>IPS32</c>, 4-byte offsets,
/// footer <c>EEOF</c>, the same records and runs, no truncation, nothing after the footer.</item>
/// </list>
/// No checksum: the source is found by the CRC32 in the name of the patch.
/// </summary>
internal static class IpsPatch
{
    private const int SignatureLength = 5;
    private const int RecordsPerCancellationCheck = 4096;

    public static bool TryGetFormat(ReadOnlySpan<byte> start, out PatchFormat format)
    {
        if (start.StartsWith("PATCH"u8))
        {
            format = PatchFormat.Ips;
            return true;
        }

        if (start.StartsWith("IPS32"u8))
        {
            format = PatchFormat.Ips32;
            return true;
        }

        format = default;
        return false;
    }

    public static PatchHeader ReadHeader(ReadOnlySpan<byte> patch)
    {
        if (!TryGetFormat(patch, out var format))
        {
            throw patch.Length < SignatureLength ? PatchErrors.Truncated() : PatchErrors.UnknownSignature();
        }

        var is32 = format == PatchFormat.Ips32;
        var position = SignatureLength;
        long recordsEnd = 0;
        while (TryReadRecord(patch, is32, ref position, out var record))
        {
            if (record.Length > 0)
            {
                recordsEnd = Math.Max(recordsEnd, record.Offset + record.Length);
            }
        }

        long? truncate = null;
        var trailing = patch.Length - position;
        if (!is32 && trailing == 3)
        {
            truncate = ReadBigEndian(patch.Slice(position, 3));
        }
        else if (trailing != 0)
        {
            throw PatchErrors.TrailingData();
        }

        if (recordsEnd > PatchService.MaxTargetSize)
        {
            throw PatchErrors.TargetTooLarge((ulong)recordsEnd);
        }

        return new PatchHeader(format) { RecordsEnd = recordsEnd, TruncateSize = truncate };
    }

    public static async Task<PatchResult> ApplyAsync(
        ReadOnlyMemory<byte> patch,
        Stream source,
        long sourceOffset,
        long sourceLength,
        Stream target,
        CancellationToken cancellationToken)
    {
        var header = ReadHeader(patch.Span);
        var grownSize = Math.Max(sourceLength, header.RecordsEnd);
        if (grownSize > PatchService.MaxTargetSize)
        {
            throw PatchErrors.TargetTooLarge((ulong)grownSize);
        }

        var finalSize = header.TargetSizeFor(sourceLength);
        var origin = target.Position;
        var buffer = new byte[64 * 1024];

        // The source first, then zeros up to the furthest record, written rather than left to the file system
        var reader = new SequentialSource(source, sourceOffset, sourceLength);
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        Array.Clear(buffer);
        for (var remaining = grownSize - sourceLength; remaining > 0;)
        {
            var n = (int)Math.Min(remaining, buffer.Length);
            await target.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
            remaining -= n;
        }

        // The records, in the order of the file
        var position = SignatureLength;
        var is32 = header.Format == PatchFormat.Ips32;
        var count = 0;
        while (TryReadRecord(patch.Span, is32, ref position, out var record))
        {
            if (++count % RecordsPerCancellationCheck == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (record.Length == 0)
            {
                continue;
            }

            target.Seek(origin + record.Offset, SeekOrigin.Begin);
            if (record.IsRun)
            {
                buffer.AsSpan(0, record.Length).Fill(record.Fill);
                await target.WriteAsync(buffer.AsMemory(0, record.Length), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await target.WriteAsync(patch.Slice(record.DataStart, record.Length), cancellationToken).ConfigureAwait(false);
            }
        }

        if (finalSize < grownSize)
        {
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            target.SetLength(origin + finalSize);
        }

        // The records are written out of order: the CRC32 of the result is read back
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        target.Seek(origin, SeekOrigin.Begin);
        var crc = new Crc32();
        for (var remaining = finalSize; remaining > 0;)
        {
            var n = (int)Math.Min(remaining, buffer.Length);
            await target.ReadExactlyAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
            crc.Append(buffer.AsSpan(0, n));
            remaining -= n;
        }

        return new PatchResult(finalSize, crc.GetCurrentHashAsUInt32(), reader.Crc32);
    }

    /// <summary>A record: data copied from the patch, or a run of one byte (RLE).</summary>
    private readonly record struct Record(long Offset, int Length, int DataStart, bool IsRun, byte Fill);

    /// <summary>Reads the record at <paramref name="position"/>; <see langword="false"/> at the footer, which it skips.</summary>
    private static bool TryReadRecord(ReadOnlySpan<byte> patch, bool is32, ref int position, out Record record)
    {
        var footer = is32 ? "EEOF"u8 : "EOF"u8;
        var offsetSize = is32 ? 4 : 3;
        var left = patch.Length - position;
        if (left < footer.Length)
        {
            throw PatchErrors.Truncated();
        }

        if (patch.Slice(position, footer.Length).SequenceEqual(footer))
        {
            position += footer.Length;
            record = default;
            return false;
        }

        if (left < offsetSize + 2)
        {
            throw PatchErrors.Truncated();
        }

        var offset = ReadBigEndian(patch.Slice(position, offsetSize));
        var size = (int)ReadBigEndian(patch.Slice(position + offsetSize, 2));
        position += offsetSize + 2;
        if (size == 0)
        {
            if (patch.Length - position < 3)
            {
                throw PatchErrors.Truncated();
            }

            record = new Record(offset, (int)ReadBigEndian(patch.Slice(position, 2)), 0, IsRun: true, patch[position + 2]);
            position += 3;
            return true;
        }

        if (patch.Length - position < size)
        {
            throw PatchErrors.Truncated();
        }

        record = new Record(offset, size, position, IsRun: false, Fill: 0);
        position += size;
        return true;
    }

    private static long ReadBigEndian(ReadOnlySpan<byte> bytes)
    {
        long value = 0;
        foreach (var b in bytes)
        {
            value = (value << 8) | b;
        }

        return value;
    }
}
