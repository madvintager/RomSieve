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

/// <summary>
/// Writes the CRC32 of its origin in the name of an IPS, the way igir and RomSieve read it, so that the patch applies at
/// the next sort, in RomSieve as elsewhere. The only write of RomSieve outside a destination: an explicit action, out of
/// any sort, on a file of the patch folder alone. A renaming loses nothing and is undone by hand, so it is not journaled
/// with the sorts; each one has its line in the log.
/// </summary>
public interface IPatchNamingService
{
    /// <summary>
    /// The name of a patch with the CRC32 of its origin before its extension: <c>Game (T-Fr).ips</c> becomes
    /// <c>Game (T-Fr) [47e70e08].ips</c>. The label of the patched version does not change: the CRC32 of the origin is
    /// removed from it (<see cref="Services.PatchMatcher.PatchedName"/>).
    /// </summary>
    string NameWithCrc(string fileName, uint crc);

    /// <summary>
    /// Renames each patch of <paramref name="renames"/> in its folder, after checking it is still an IPS or an IPS32 of
    /// <paramref name="patchDir"/>, on the disk (never in an archive), neither a link nor a name that already gives a
    /// CRC32, and that its new name is free. A refusal or a failure leaves the patch as it was and goes on with the
    /// others. Each result is said to <paramref name="log"/>.
    /// </summary>
    Task<IReadOnlyList<PatchRenameResult>> RenameAsync(
        string patchDir,
        IReadOnlyList<PatchRename> renames,
        Action<LogLevel, LocalizedMessage> log,
        CancellationToken cancellationToken = default);
}
