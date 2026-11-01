using System.Collections.Concurrent;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Checks the data of the archives of a run against the CRC32 they record (<see cref="SortConfig.VerifyArchiveContents"/>),
/// each archive read once, whatever the number of its members asked about, and from several threads at once. With
/// <paramref name="headerCache"/>, an archive checked since it last changed is not decompressed again (null: every
/// archive is read, as for an export that has no cache).
/// </summary>
internal sealed class ArchiveContentChecker(IChecksumService checksumService, IHeaderCache? headerCache)
{
    private const string DamagedMemberReasonKey = "reason.archive_member_damaged";
    private const string UnreadableArchiveReasonKey = "reason.archive_unreadable";

    /// <summary>Whether <paramref name="reason"/> says that a file is damaged in its archive, which makes its whole set damaged.</summary>
    public static bool IsDamage(LocalizedMessage? reason) =>
        reason?.Key is DamagedMemberReasonKey or UnreadableArchiveReasonKey;

    private readonly ConcurrentDictionary<string, Lazy<ArchiveCheck>> _checks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What is wrong with the archive <paramref name="archivePath"/>, read at the first question.</summary>
    public ArchiveCheck Check(string archivePath, CancellationToken cancellationToken) =>
        _checks.GetOrAdd(archivePath, path => new Lazy<ArchiveCheck>(() => Read(path, cancellationToken))).Value;

    /// <summary>
    /// Reason why the member <paramref name="memberName"/> of <paramref name="archivePath"/> is damaged, or <c>null</c>
    /// when its data matches its CRC32 (or the archive records none).
    /// </summary>
    public LocalizedMessage? FindMemberDamage(string archivePath, string memberName, CancellationToken cancellationToken)
    {
        var check = Check(archivePath, cancellationToken);
        if (check.Error is { } error)
        {
            return LocalizedMessage.Create(UnreadableArchiveReasonKey, ("error", LocalizedMessage.Verbatim(error)));
        }

        return check.Damaged.FirstOrDefault(m => string.Equals(m.MemberName, memberName, StringComparison.Ordinal)) is { } damaged
            ? LocalizedMessage.Create(DamagedMemberReasonKey, ("member", damaged.MemberName), ("error", damaged.Describe()))
            : null;
    }

    /// <summary>
    /// Reason why the archive of a whole set (a MAME set) is damaged, naming its first damaged member, or <c>null</c>.
    /// </summary>
    public LocalizedMessage? FindArchiveDamage(string archivePath, CancellationToken cancellationToken)
    {
        var check = Check(archivePath, cancellationToken);
        if (check.Error is { } error)
        {
            return LocalizedMessage.Create(UnreadableArchiveReasonKey, ("error", LocalizedMessage.Verbatim(error)));
        }

        return check.Damaged.Count > 0
            ? LocalizedMessage.Create(
                "reason.archive_damaged",
                ("count", check.Damaged.Count),
                ("member", check.Damaged[0].MemberName),
                ("error", check.Damaged[0].Describe()))
            : null;
    }

    private ArchiveCheck Read(string archivePath, CancellationToken cancellationToken)
    {
        try
        {
            var damaged = headerCache is null
                ? checksumService.FindDamagedMembers(archivePath, cancellationToken)
                : headerCache.FindDamagedMembers(archivePath, checksumService, cancellationToken);
            return new ArchiveCheck(damaged, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new ArchiveCheck([], ex.Message);
        }
    }
}

/// <summary>Members of an archive that do not match their CRC32, or why the archive could not be read at all.</summary>
internal sealed record ArchiveCheck(IReadOnlyList<DamagedArchiveMember> Damaged, string? Error);
