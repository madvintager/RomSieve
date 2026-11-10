using System.IO;
using RomSieve.Core.Models;

namespace RomSieve.Core.Abstractions;

/// <summary>
/// Reads and applies ROM patches in the IPS, IPS32, UPS and BPS formats, without any external library. A patch is read
/// in memory (it is small, <see cref="Services.PatchService.MaxPatchSize"/> at most); the source is read where it is
/// and the result is written to a stream, never held whole in memory. A truncated, malformed or hostile patch is refused
/// with a <see cref="PatchException"/>, whose reason is a structured message.
/// </summary>
public interface IPatchService
{
    /// <summary>
    /// Format of a file whose name has the extension of a patch (<c>.ips</c>, <c>.ips32</c>, <c>.ups</c>, <c>.bps</c>),
    /// from the signature its first bytes hold; <see langword="null"/> for any other file.
    /// </summary>
    PatchFormat? DetectFormat(string fileName, ReadOnlySpan<byte> start);

    /// <summary>
    /// Reads a whole patch into memory, refusing one larger than <see cref="Services.PatchService.MaxPatchSize"/> before
    /// reading past it (the size an archive announces for a member may be false).
    /// </summary>
    Task<byte[]> ReadPatchAsync(Stream stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads what a patch tells before it is applied, checking its structure (IPS) or its own CRC32 (UPS, BPS). Throws a
    /// <see cref="PatchException"/> for a patch that cannot be applied.
    /// </summary>
    PatchHeader ReadHeader(ReadOnlySpan<byte> patch);

    /// <summary>
    /// Applies <paramref name="patch"/> to the <paramref name="sourceLength"/> bytes of <paramref name="source"/> that
    /// start at <paramref name="sourceOffset"/> (after a copier header the patch does not expect), and writes the result
    /// to <paramref name="target"/>, from its current position. <paramref name="source"/> must allow seeking;
    /// <paramref name="target"/> must allow seeking and reading (BPS reads back what it wrote, IPS writes its records
    /// out of order) and be empty. The CRC32 of the source and of the result are checked when the patch records them:
    /// a mismatch throws a <see cref="PatchException"/>, the target then holding an unusable result.
    /// </summary>
    Task<PatchResult> ApplyAsync(
        ReadOnlyMemory<byte> patch,
        Stream source,
        long sourceOffset,
        long sourceLength,
        Stream target,
        CancellationToken cancellationToken = default);
}
