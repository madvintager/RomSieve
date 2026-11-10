using System.IO;

namespace RomSieve.Core.Models;

/// <summary>Format of a ROM patch, read from its signature (<see cref="Abstractions.IPatchService.DetectFormat"/>).</summary>
public enum PatchFormat
{
    /// <summary>IPS: signature <c>PATCH</c>, 3-byte offsets, footer <c>EOF</c>, optional truncation; no checksum.</summary>
    Ips,
    /// <summary>IPS32: signature <c>IPS32</c>, 4-byte offsets, footer <c>EEOF</c>; no checksum, no truncation.</summary>
    Ips32,
    /// <summary>UPS: signature <c>UPS1</c>, XOR blocks, CRC32 of the source, of the result and of the patch.</summary>
    Ups,
    /// <summary>BPS: signature <c>BPS1</c>, copy actions, CRC32 of the source, of the result and of the patch.</summary>
    Bps
}

/// <summary>
/// What a patch tells before it is applied (<see cref="Abstractions.IPatchService.ReadHeader"/>): its format and, when
/// the format gives them (UPS, BPS), the size and CRC32 of the source it expects and of the result it produces. An IPS
/// gives neither: the size of its result depends on the source (<see cref="TargetSizeFor"/>).
/// </summary>
public sealed record PatchHeader(PatchFormat Format)
{
    /// <summary>Size of the source the patch expects (UPS, BPS).</summary>
    public long? SourceSize { get; init; }

    /// <summary>CRC32 of the source the patch expects (UPS, BPS).</summary>
    public uint? SourceCrc32 { get; init; }

    /// <summary>Size of the result (UPS, BPS).</summary>
    public long? TargetSize { get; init; }

    /// <summary>CRC32 of the result (UPS, BPS).</summary>
    public uint? TargetCrc32 { get; init; }

    /// <summary>CRC32 of the patch, as recorded in it and checked (UPS, BPS).</summary>
    public uint? PatchCrc32 { get; init; }

    /// <summary>IPS: end of the record that reaches furthest, which the result covers even beyond the source.</summary>
    public long RecordsEnd { get; init; }

    /// <summary>IPS: size the result is truncated to (the 3 bytes after <c>EOF</c>), when it shortens it.</summary>
    public long? TruncateSize { get; init; }

    /// <summary>Whether the patch records the CRC32 of its source and of its result (UPS, BPS).</summary>
    public bool HasChecksums => Format is PatchFormat.Ups or PatchFormat.Bps;

    /// <summary>
    /// Size of the result of the patch applied to a source of <paramref name="sourceSize"/> bytes: the size it records
    /// (UPS, BPS); for an IPS, the source grown to its furthest record, then truncated when the truncation shortens it.
    /// </summary>
    public long TargetSizeFor(long sourceSize)
    {
        if (TargetSize is { } targetSize)
        {
            return targetSize;
        }

        var size = Math.Max(sourceSize, RecordsEnd);
        return TruncateSize is { } truncate && truncate < size ? truncate : size;
    }
}

/// <summary>What applying a patch wrote: the size and CRC32 of the result, and the CRC32 of the source it read.</summary>
public sealed record PatchResult(long Size, uint Crc32, uint SourceCrc32);

/// <summary>
/// A patch refused: truncated, malformed, hostile, or not meant for the source it is applied to. The reason is a
/// structured message (<c>patch.error_*</c>), translated only when displayed.
/// </summary>
public sealed class PatchException(LocalizedMessage reason) : IOException(reason.ToString())
{
    public LocalizedMessage Reason { get; } = reason;
}
