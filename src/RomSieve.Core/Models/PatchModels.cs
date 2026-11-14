using System.IO;
using System.Security.Cryptography;

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

public static class PatchFormatExtensions
{
    /// <summary>Name of the format as patchers write it (<c>IPS</c>, <c>IPS32</c>, <c>UPS</c>, <c>BPS</c>), never translated.</summary>
    public static string ToDisplayName(this PatchFormat format) => format switch
    {
        PatchFormat.Ips32 => "IPS32",
        PatchFormat.Ups => "UPS",
        PatchFormat.Bps => "BPS",
        _ => "IPS"
    };
}

/// <summary>
/// A patch of the patch folder (<see cref="SortConfig.PatchDir"/>): a file, or the member of an archive
/// (<see cref="SourcePath"/> is then the archive).
/// </summary>
public sealed record PatchFile(string SourcePath, string? ArchiveMember = null)
{
    /// <summary>Name of the patch: the member of its archive, or its file name.</summary>
    public string DisplayName => ArchiveMember ?? Path.GetFileName(SourcePath);
}

/// <summary>
/// What the patch folder holds (<see cref="Services.SourceScanner.ScanPatchFolder"/>): its patches, and the texts that
/// may come with them (<see cref="Services.PatchService.NoteExtensions"/>, a readme), each a file or the member of an
/// archive, in the order of their paths, then of their members.
/// </summary>
public sealed record PatchFolderContent(IReadOnlyList<PatchFile> Patches, IReadOnlyList<PatchFile> Notes);

/// <summary>Where the CRC32 a patch is matched with its origin by comes from.</summary>
public enum PatchCrcSource
{
    /// <summary>The patch records it (UPS, BPS).</summary>
    Recorded,

    /// <summary>The name of the patch gives it (IPS, IPS32).</summary>
    FileName,

    /// <summary>A text that comes with the patch gives it, its readme (IPS, IPS32 whose name gives none).</summary>
    Notes,

    /// <summary>
    /// The user chose it (<see cref="PatchLink"/>, IPS and IPS32): it comes before the name and the readme of the patch.
    /// </summary>
    Link
}

/// <summary>
/// A patch the user set aside (<see cref="PatchLinkKind.Ignored"/>): it gives no patched version, whatever designates its
/// ROM, and is not counted among the patches not applied, since nothing is wrong with it.
/// </summary>
/// <param name="File">The patch.</param>
/// <param name="Key">Its key, to take the choice back.</param>
/// <param name="Format">Its format, from its signature; <see langword="null"/> for a file that is no patch.</param>
public sealed record IgnoredPatch(PatchFile File, PatchKey Key, PatchFormat? Format);

/// <summary>
/// A patch of the patch folder that gives no patched version, and why (<c>reason.patch_*</c>), with the origins it may be
/// meant for (<see cref="Suggestions"/>), never applied.
/// </summary>
public sealed record UnappliedPatch(PatchFile File, LocalizedMessage Reason)
{
    /// <summary>
    /// Origins proposed to the user, best first: those whose name or title starts a name of an IPS without CRC32, the
    /// files its CRC32 designate when they are several, or the two forms of a file a readme gives with and without its
    /// copier header. Empty for any other reason.
    /// </summary>
    public IReadOnlyList<PatchOriginSuggestion> Suggestions { get; init; } = [];

    /// <summary>
    /// The key of the patch, to record a choice for it without reading it again; <see langword="null"/> for a patch that
    /// could not be read.
    /// </summary>
    public PatchKey? Key { get; init; }

    /// <summary>
    /// The format of the patch, from its signature: only an IPS or an IPS32 may be given a ROM. <see langword="null"/>
    /// for a patch that could not be read.
    /// </summary>
    public PatchFormat? Format { get; init; }
}

/// <summary>
/// An origin proposed for a patch left without one (<see cref="UnappliedPatch.Suggestions"/>): a file identified in the
/// DAT, and the form of its data the patch would apply to.
/// </summary>
/// <param name="Origin">The identified file.</param>
/// <param name="Crc32">CRC32 of the form: the one to write in the name of the patch.</param>
/// <param name="Offset">Bytes of the file skipped before that form (a copier header).</param>
/// <param name="WithCopierHeader">
/// For the two forms of the same file: whether this one holds the copier header; <see langword="null"/> otherwise.
/// </param>
public sealed record PatchOriginSuggestion(ProcessItem Origin, uint Crc32, long Offset, bool? WithCopierHeader = null)
{
    /// <summary>
    /// Why the ROM is proposed (<c>patches.why_*</c>): a name of the patch starts with its name or its title, the name or a
    /// readme of the patch designates it, a readme gives its two forms; and when it comes after the others, because it
    /// is already in the language of a translation. <see langword="null"/> for a form read for the choice of the user.
    /// </summary>
    public LocalizedMessage? Reason { get; init; }
}

/// <summary>
/// What makes a <see cref="ProcessItem"/> a patched version (<see cref="ProcessItem.Patch"/>): its patch, what the patch
/// told of itself, the item it is applied to, and the part of that item's data it reads (after a copier header the
/// patch does not expect).
/// </summary>
/// <param name="File">The patch.</param>
/// <param name="Header">What the patch told when it was read.</param>
/// <param name="Origin">The item of the source the patch is applied to, never modified.</param>
/// <param name="SourceOffset">Bytes of the origin skipped before the data the patch expects (a copier header).</param>
/// <param name="SourceLength">Bytes of the origin the patch reads, from <see cref="SourceOffset"/>.</param>
/// <param name="SourceCrc32">CRC32 of those bytes, by which the patch was matched with the origin.</param>
public sealed record PatchedVersion(
    PatchFile File,
    PatchHeader Header,
    ProcessItem Origin,
    long SourceOffset,
    long SourceLength,
    uint SourceCrc32)
{
    public PatchFormat Format => Header.Format;

    /// <summary>Where <see cref="SourceCrc32"/> was found: in the patch, its name or a text that comes with it.</summary>
    public PatchCrcSource CrcSource { get; init; }

    /// <summary>The text that gave <see cref="SourceCrc32"/>, when <see cref="CrcSource"/> is <see cref="PatchCrcSource.Notes"/>.</summary>
    public PatchFile? Note { get; init; }

    /// <summary>The key of the patch, to record a choice for it without reading it again.</summary>
    public PatchKey Key { get; init; }

    /// <summary>
    /// Bytes of the copier header of the origin written back in front of the result of the patch: the iNES (16 bytes) or
    /// Lynx (64 bytes) header the patch skipped (<see cref="SourceOffset"/>), since most emulators of these systems need
    /// it; 0 otherwise (no header skipped, or the 512 bytes of a SNES copier header, which nothing needs).
    /// </summary>
    public int RestoredHeaderSize { get; init; }

    /// <summary>
    /// CRC32 of the header written back (<see cref="RestoredHeaderSize"/>), read when the patch was matched: the writing
    /// refuses a header changed since, and the CRC32 of the result written is known before it is.
    /// <see langword="null"/> without header, or when it could not be read.
    /// </summary>
    public uint? RestoredHeaderCrc32 { get; init; }

    /// <summary>Size of the data the patch produces, the header written back apart.</summary>
    public long PatchedDataSize => Header.TargetSizeFor(SourceLength);

    /// <summary>Size of the file written: the header written back, then the data the patch produces.</summary>
    public long TargetSize => RestoredHeaderSize + PatchedDataSize;
}

/// <summary>
/// The CRC32 of its origin to write in the name of an IPS (<see cref="Abstractions.IPatchNamingService.RenameAsync"/>):
/// <c>Game (T-Fr).ips</c> becomes <c>Game (T-Fr) [47e70e08].ips</c>, and the CRC32 a name already gives is replaced:
/// other tools then apply the patch to that ROM too. Without <paramref name="Crc32"/>, the CRC32 the name gives is
/// removed (<c>Game (T-Fr) [47e70e08].ips</c> becomes <c>Game (T-Fr).ips</c> again). RomSieve itself follows the choice
/// of the user (<see cref="PatchLink"/>), which no renaming is needed for.
/// </summary>
public sealed record PatchRename(PatchFile Patch, uint? Crc32)
{
    /// <summary>Whether the CRC32 the name gives is removed rather than written.</summary>
    public bool ClearsName => Crc32 is null;
}

/// <summary>
/// What became of a <see cref="PatchRename"/>: the new path of the patch, or why it was left as it was
/// (<c>reason.patch_rename_*</c>).
/// </summary>
public sealed record PatchRenameResult(PatchRename Request, string? NewPath, LocalizedMessage? Refusal)
{
    public bool IsRenamed => NewPath is not null;
}

/// <summary>
/// What became of the undoing of a <see cref="PatchRenameResult"/>
/// (<see cref="Abstractions.IPatchNamingService.UndoAsync"/>): the patch took back its old name, the path of
/// <see cref="PatchRename.Patch"/>, or why it kept its new one (<c>reason.patch_rename_*</c>).
/// </summary>
public sealed record PatchUndoResult(PatchRenameResult Renaming, LocalizedMessage? Refusal)
{
    public bool IsUndone => Refusal is null;
}

/// <summary>
/// What identifies a patch in its associations (<see cref="Abstractions.IPatchLinkService"/>): its content, so that the
/// choice of the user follows the patch when it is renamed, moved, or put into an archive, whatever the folder of the
/// patches or the profile. Two copies of the same patch share their choice.
/// </summary>
/// <param name="Sha1">SHA-1 of the bytes of the patch, 40 lowercase hexadecimal digits.</param>
/// <param name="Size">Size of the patch.</param>
public readonly record struct PatchKey(string Sha1, long Size)
{
    /// <summary>The key of a patch read whole in memory: the only way it is computed.</summary>
    public static PatchKey Of(ReadOnlySpan<byte> patch) =>
        new(Convert.ToHexStringLower(SHA1.HashData(patch)), patch.Length);

    /// <summary>Whether <paramref name="sha1"/> is written the way <see cref="Of"/> writes it.</summary>
    public static bool IsValidSha1(string? sha1) =>
        sha1 is { Length: 40 } && sha1.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
}

/// <summary>What the user chose for a patch (<see cref="PatchLink"/>).</summary>
public enum PatchLinkKind
{
    /// <summary>The patch applies to the ROM the user chose (an IPS or an IPS32, which records none).</summary>
    Origin,

    /// <summary>The patch does not apply, whatever designates its ROM.</summary>
    Ignored
}

/// <summary>
/// The choice of the user for a patch, kept by RomSieve rather than in the name of the file
/// (<see cref="Abstractions.IPatchLinkService"/>). It comes before what the patch records, its name and its readme.
/// </summary>
/// <param name="Kind">The ROM chosen, or the patch set aside.</param>
/// <param name="PatchName">Name of the patch when the choice was made, to show a choice whose patch is not found.</param>
/// <param name="OriginCrc32">
/// For <see cref="PatchLinkKind.Origin"/>: CRC32 of the form of the ROM the patch applies to (with or without its copier
/// header), as a CRC32 in the name of an IPS designates it.
/// </param>
/// <param name="OriginName">For <see cref="PatchLinkKind.Origin"/>: name of the ROM, to say which one is missing.</param>
public sealed record PatchLink(PatchLinkKind Kind, string PatchName, uint? OriginCrc32 = null, string? OriginName = null)
{
    /// <summary>The patch applies to the form of a ROM whose CRC32 is <paramref name="crc32"/>.</summary>
    public static PatchLink ToOrigin(string patchName, uint crc32, string originName) =>
        new(PatchLinkKind.Origin, patchName, crc32, originName);

    /// <summary>The patch does not apply.</summary>
    public static PatchLink Ignore(string patchName) => new(PatchLinkKind.Ignored, patchName);
}

/// <summary>
/// The associations read at one moment (<see cref="Abstractions.IPatchLinkService.Load"/>), the same for a whole
/// simulation, and what prevented reading or keeping the file, to say in the log.
/// </summary>
/// <param name="Links">The choice of the user for each patch.</param>
/// <param name="Warning">Why the file was set aside or cannot be written; <see langword="null"/> when all is well.</param>
public sealed record PatchLinkSnapshot(IReadOnlyDictionary<PatchKey, PatchLink> Links, LocalizedMessage? Warning)
{
    public static PatchLinkSnapshot Empty { get; } = new(new Dictionary<PatchKey, PatchLink>(), null);
}

/// <summary>
/// What applying a patch wrote: the size and CRC32 of the result (of the whole file written, with a header written back,
/// <see cref="PatchedVersion.RestoredHeaderSize"/>), and the CRC32 of the source it read.
/// </summary>
public sealed record PatchResult(long Size, uint Crc32, uint SourceCrc32);

/// <summary>
/// A patch refused: truncated, malformed, hostile, or not meant for the source it is applied to. The reason is a
/// structured message (<c>patch.error_*</c>), translated only when displayed.
/// </summary>
public sealed class PatchException(LocalizedMessage reason) : IOException(reason.ToString())
{
    public LocalizedMessage Reason { get; } = reason;
}
