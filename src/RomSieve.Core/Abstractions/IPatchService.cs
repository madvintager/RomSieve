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
/// The choice of the user for each patch (<see cref="PatchLink"/>: the ROM it applies to, or set aside), kept in
/// <c>patch_links.json</c> next to the configuration file in use, and never in the folder of the patches: choosing
/// changes no file of the user. A patch is known by its content (<see cref="PatchKey"/>). The file is read again at each
/// <see cref="Load"/>, so that a choice made by the command line is seen by the interface, and written in one step. A
/// file that cannot be read is never lost: it is copied aside before being emptied or, failing that, left as it is
/// and never written; a file of a newer format is left as it is and never written.
/// </summary>
public interface IPatchLinkService
{
    /// <summary>Path of <c>patch_links.json</c>.</summary>
    string FilePath { get; }

    /// <summary>The associations as the file holds them now, and what prevented reading or keeping it.</summary>
    PatchLinkSnapshot Load();

    /// <summary>
    /// Sets the choice of each patch of <paramref name="changes"/> (<see langword="null"/>: back to the automatic
    /// choice), reading the file again first so that a choice made elsewhere meanwhile is kept, then writing it in one
    /// step. Returns <see langword="false"/>, saying why to <paramref name="log"/>, when the file cannot be written:
    /// nothing is changed then.
    /// </summary>
    Task<bool> UpdateAsync(
        IReadOnlyCollection<KeyValuePair<PatchKey, PatchLink?>> changes,
        Action<LogLevel, LocalizedMessage> log,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The forms of the data of <paramref name="origin"/> an IPS may expect, to choose the one it applies to: the form it
    /// was identified in and, with <paramref name="detectCopierHeaders"/>, the other one (with or without its copier
    /// header), read again from the source. The whole file first; <see cref="PatchOriginSuggestion.WithCopierHeader"/>
    /// tells them apart when there are two. Empty for a file without CRC32.
    /// </summary>
    Task<IReadOnlyList<PatchOriginSuggestion>> GetOriginFormsAsync(
        ProcessItem origin,
        bool detectCopierHeaders,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Writes the CRC32 of its origin in the name of an IPS, the way RomSieve reads it, so that the patch applies at
/// the next sort, in RomSieve as elsewhere. The only write of RomSieve outside a destination: an explicit action, out of
/// any sort, on a file of the patch folder alone. A renaming loses nothing and is undone by hand, so it is not journaled
/// with the sorts; each one has its line in the log.
/// </summary>
public interface IPatchNamingService
{
    /// <summary>
    /// The name of a patch with the CRC32 of its origin before its extension: <c>Game (T-Fr).ips</c> becomes
    /// <c>Game (T-Fr) [47e70e08].ips</c>. A CRC32 the name already gives is replaced
    /// (<see cref="Services.PatchMatcher.BaseNameWithoutCrc"/>). The label of the patched version does not change: the
    /// CRC32 of the origin is removed from it (<see cref="Services.PatchMatcher.PatchedName"/>).
    /// </summary>
    string NameWithCrc(string fileName, uint crc);

    /// <summary>
    /// The name of a patch without the CRC32 it gives (<see cref="Services.PatchMatcher.BaseNameWithoutCrc"/>):
    /// <c>Game (T-Fr) [47e70e08].ips</c> becomes <c>Game (T-Fr).ips</c>. A name that gives none is returned as it is;
    /// <see langword="null"/> when nothing but a CRC32 made it.
    /// </summary>
    string? NameWithoutCrc(string fileName);

    /// <summary>
    /// Renames each patch of <paramref name="renames"/> in its folder, after checking it is still an IPS or an IPS32 of
    /// <paramref name="patchDir"/>, on the disk (never in an archive), not a link, and that its new name is free and
    /// differs from the current one (a name whose CRC32 is removed must give one, and something else). A refusal or a
    /// failure leaves the patch as it was and goes on with the others. Each result is said to <paramref name="log"/>.
    /// </summary>
    Task<IReadOnlyList<PatchRenameResult>> RenameAsync(
        string patchDir,
        IReadOnlyList<PatchRename> renames,
        Action<LogLevel, LocalizedMessage> log,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gives back its old name to a patch <see cref="RenameAsync"/> renamed (<paramref name="renaming"/>, renamed), with
    /// the checks of a renaming: the patch is still under its new name in <paramref name="patchDir"/>, on the disk, not a
    /// link, still an IPS or an IPS32, and its old name, in the same folder, is safe and free. Nothing is overwritten;
    /// the result is said to <paramref name="log"/>. Nothing keeps the renamings: only the caller can undo one, as long
    /// as it holds its result.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="renaming"/> is a refusal, which renamed nothing.</exception>
    Task<PatchUndoResult> UndoAsync(
        string patchDir,
        PatchRenameResult renaming,
        Action<LogLevel, LocalizedMessage> log,
        CancellationToken cancellationToken = default);
}
