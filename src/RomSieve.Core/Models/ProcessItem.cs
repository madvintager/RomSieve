using System.Collections.Generic;
using System.IO;

namespace RomSieve.Core.Models;

/// <summary>
/// Represents an entry discovered on disk or inside a compressed archive (.zip).
/// </summary>
public sealed record ScanEntry(
    string SourcePath,
    long FileSize,
    string? ArchivePath = null,
    string? ArchiveMember = null,
    string? PrecomputedCrc = null,
    IReadOnlyList<ScanEntry>? RelatedEntries = null,
    int ArchiveTotalMemberCount = 0,
    long ArchiveFileSize = 0
)
{
    public string DisplayName => !string.IsNullOrEmpty(ArchiveMember)
        ? ArchiveMember
        : Path.GetFileName(SourcePath);

    public IReadOnlyList<ScanEntry> RelatedEntries { get; init; } = RelatedEntries ?? [];
}

/// <summary>
/// Represents a member file inside a ZIP archive.
/// </summary>
public sealed record ArchiveMember(
    string ArchivePath,
    string MemberName,
    long Size,
    string Crc32
);

/// <summary>
/// A member of an archive whose data does not match what the archive records for it
/// (<see cref="Abstractions.IChecksumService.FindDamagedMembers"/>): another CRC32 (<see cref="ActualCrc32"/>), or data
/// that cannot be decompressed (<see cref="Error"/>).
/// </summary>
public sealed record DamagedArchiveMember(string MemberName, string ExpectedCrc32, string? ActualCrc32, string? Error)
{
    /// <summary>What is wrong with the member, for a reason or a log line.</summary>
    public LocalizedMessage Describe() =>
        Error is { } error
            ? LocalizedMessage.Verbatim(error)
            : LocalizedMessage.Create("reason.archive_crc_mismatch", ("expected", ExpectedCrc32), ("actual", ActualCrc32));
}

/// <summary>
/// What a MAME set holds compared with its machine of the DAT: the ROMs and the CHD disks it lacks (never-dumped ROMs
/// apart), the members the machine does not know, and, when every missing ROM or disk is shared with another set
/// (<c>merge</c>), the set that holds them (<see cref="RequiredSet"/>: the parent of a clone of a split set, or a BIOS).
/// <see cref="HeldClones"/> names the clones whose own ROMs the set also holds (a merged set). RomSieve only sorts
/// non-merged sets: a split or merged set is reported, never rebuilt. <see cref="RequiredSets"/> names the other sets of
/// the source MAME needs to run the machine (its BIOS, its devices, the parent of a split clone), which a sort keeps with
/// it, and <see cref="MissingDevices"/> the devices it needs that neither its set nor the source holds.
/// </summary>
public sealed record MameSetInfo(
    string GameName,
    int MissingRoms,
    int MissingDisks,
    int UnknownMembers,
    string? RequiredSet = null)
{
    /// <summary>Clones of the machine whose own ROMs the set also holds (a merged set), sorted by name.</summary>
    public IReadOnlyList<string> HeldClones { get; init; } = [];

    /// <summary>Number of the clones whose own ROMs the set also holds.</summary>
    public int ClonesHeld => HeldClones.Count;

    /// <summary>
    /// Whether MAME finds every ROM and disk of the machine, in the set itself or in its BIOS sorted with it
    /// (<see cref="BiosSet"/>), and the ROMs of its devices, in the set or in their own sets.
    /// </summary>
    public bool IsComplete => MissingRoms == 0 && MissingDisks == 0 && MissingDevices.Count == 0;

    /// <summary>
    /// Other sets of the source MAME needs to run the machine, which the sort keeps with it: the BIOS whose ROMs the set
    /// does not hold (<see cref="BiosSet"/>), the devices whose ROMs it does not hold, and the parent of a split clone.
    /// </summary>
    public IReadOnlyList<string> RequiredSets { get; init; } = [];

    /// <summary>Devices with ROMs the machine needs, held neither by its set nor by a set of the source.</summary>
    public IReadOnlyList<string> MissingDevices { get; init; } = [];

    /// <summary>Whether <see cref="RequiredSet"/> is a BIOS, absent from the source, rather than another game.</summary>
    public bool RequiresBios { get; init; }

    /// <summary>BIOS of the source in which MAME finds ROMs of the machine that its own set does not hold.</summary>
    public string? BiosSet { get; init; }

    /// <summary>
    /// MAME software list of a software (<c>megadriv</c>), whose sets are only named in it: the sets it needs are those
    /// of the same list; <c>null</c> for a MAME machine.
    /// </summary>
    public string? SoftwareList { get; init; }

    /// <summary>Name of the set unique among the sets of a run: the short name, preceded by the list of a software.</summary>
    public string SetKey => SetKeyOf(SoftwareList, GameName);

    /// <summary>Key of the set <paramref name="gameName"/> of the list <paramref name="softwareList"/> (<see cref="SetKey"/>).</summary>
    public static string SetKeyOf(string? softwareList, string gameName) =>
        softwareList is null ? gameName : softwareList + "/" + gameName;

    /// <summary>A split set: what it lacks is in the set of another game (the parent of a clone).</summary>
    public bool IsSplit => RequiredSet is not null && !RequiresBios;

    /// <summary>A merged set: it also holds the ROMs of clones of its machine.</summary>
    public bool IsMerged => ClonesHeld > 0;

    // Value equality, the lists compared by their content
    public bool Equals(MameSetInfo? other) =>
        other is not null &&
        GameName == other.GameName &&
        SoftwareList == other.SoftwareList &&
        MissingRoms == other.MissingRoms &&
        MissingDisks == other.MissingDisks &&
        UnknownMembers == other.UnknownMembers &&
        RequiredSet == other.RequiredSet &&
        RequiresBios == other.RequiresBios &&
        BiosSet == other.BiosSet &&
        HeldClones.SequenceEqual(other.HeldClones) &&
        RequiredSets.SequenceEqual(other.RequiredSets) &&
        MissingDevices.SequenceEqual(other.MissingDevices);

    public override int GetHashCode() =>
        HashCode.Combine(SetKey, MissingRoms, MissingDisks, UnknownMembers, RequiredSet, ClonesHeld, RequiresBios, BiosSet);
}

/// <summary>
/// Item submitted to analysis, simulation, or physical sorting.
/// </summary>
public sealed class ProcessItem
{
    public required string SourcePath { get; set; }
    public required long FileSize { get; set; }
    /// <summary>
    /// Immutable parse result of the file name. The pipeline may replace it with an enriched copy
    /// (DAT parent / clone, inferred disc total), never modify it in place.
    /// </summary>
    public required ParsedRomName Parsed { get; set; }
    public FileStatus Status { get; set; } = FileStatus.Valid;

    /// <summary>
    /// Status decided by the release selection (<c>ReleaseSelector</c>), before the transfers, which may still turn
    /// a kept file into a duplicate (collision) or an error. A partial sort compares it with the simulation.
    /// </summary>
    public FileStatus? ReleaseStatus { get; set; }

    /// <summary>
    /// Whether a partial sort took the file out of the transfers (<see cref="SortSelection"/>): the sort would have
    /// written it, but its game was not chosen or changed since the simulation. Its status is then
    /// <see cref="FileStatus.Excluded"/>, and it is counted apart (<see cref="ProcessStats.NotSelectedCount"/>).
    /// </summary>
    public bool IsOutsideSelection { get; set; }

    /// <summary>
    /// Non-conforming file (<see cref="FileStatus.Unmatched"/>) that the sort leaves in the source, the isolation of the
    /// non-conforming files in <c>_unmatched</c> being off (<see cref="SortConfig.IsolateUnmatched"/>). Set by
    /// <see cref="ReleaseSelector"/>; the file keeps its status, and is counted apart
    /// (<see cref="ProcessStats.NotIsolatedCount"/>).
    /// </summary>
    public bool IsNotIsolated { get; set; }

    /// <summary>
    /// Whether the size budget of a MAME run left the set out (<see cref="Services.MameSizeBudget"/>): it was
    /// kept, but did not fit. A new budget gives it its chance again.
    /// </summary>
    public bool IsOutsideBudget { get; set; }

    /// <summary>
    /// Whether the file is a duplicate because its target was already in the destination before the sort (a previous
    /// sort, another tool), rather than taken by another file of the same run: the sort does not write it again in the
    /// collision mode "skip" (<see cref="ProcessStats.AlreadyInDestinationCount"/>).
    /// </summary>
    public bool IsAlreadyInDestination { get; set; }
    public LocalizedMessage? StatusReason { get; set; }
    public string? TargetPath { get; set; }
    public string? FinalTargetPath { get; set; }
    public string? ErrorMessage { get; set; }
    public string? TargetFilename { get; set; }

    /// <summary>
    /// Name of the DAT game the file was identified with, which disambiguates ROM names shared by several games.
    /// </summary>
    public string? DatGameName { get; set; }

    /// <summary>
    /// Name of the DAT of a lot the file is sorted with (<see cref="DatLotMember.Name"/>): the first DAT of the lot that
    /// knows it, or that knows the first file of its set (a descriptor and its tracks); <see langword="null"/> for a
    /// file no DAT knows, and for any sort with a single DAT or none.
    /// </summary>
    public string? DatName { get; set; }

    /// <summary>
    /// The other DATs of the lot that know the file or a file of its set, in the order of the lot: their ROMs are present
    /// too, but the file is only sorted with <see cref="DatName"/>.
    /// </summary>
    public IReadOnlyList<string> OtherDats { get; set; } = [];
    public string Crc32 { get; set; } = string.Empty;
    public string? Md5 { get; set; }
    public string? Sha1 { get; set; }
    public string? ArchivePath { get; set; }
    public string? ArchiveMember { get; set; }
    public bool IsMultidiskComplete { get; set; } = true;

    /// <summary>
    /// Multi-disk status decided by the classification (DAT certification included), which takes precedence
    /// over the file name analysis in <see cref="IsMultiMedia"/>. It never modifies <see cref="Parsed"/>.
    /// </summary>
    public bool? IsMultidisk { get; set; }

    public bool Is1G1RWinner { get; set; }
    public string? WinnerReference { get; set; }
    public LocalizedMessage? EliminationDetail { get; set; }

    /// <summary>
    /// First criterion of the 1G1R cascade on which the winner of the game beat this release
    /// (<see cref="TiebreakCriterion.None"/> for any other file, or a winner chosen by hand).
    /// </summary>
    public TiebreakCriterion EliminatedBy { get; set; }
    public List<ProcessItem> RelatedItems { get; set; } = [];

    /// <summary>Header and tracks of a CHD file, read at classification; <see langword="null"/> for any other file.</summary>
    public ChdInfo? Chd { get; set; }

    /// <summary>
    /// Whether the DAT game was found by the file name only (<see cref="SortConfig.MatchChdByName"/>), without a hash match.
    /// </summary>
    public bool IsIdentifiedByName { get; set; }

    /// <summary>
    /// Why the identification set the file aside whatever its name (<see cref="Models.IdentificationFailure.None"/> for
    /// a file identified by the DAT, or sorted by its name without a DAT). Set by the classification only: its name may
    /// follow the convention and give the key of an identified game, and the recalculation of a game
    /// (<c>RecalculateGameItems</c>) keeps its status instead of evaluating its name as a release of that game.
    /// </summary>
    public IdentificationFailure IdentificationFailure { get; set; }

    /// <summary>
    /// The item is a whole archive (a MAME set, identified or not): it is transferred as it is, never extracted, put into
    /// another archive in ZIP mode, nor renamed on a collision, since MAME finds a set by its file name only.
    /// </summary>
    public bool IsWholeArchive { get; set; }

    /// <summary>Total number of members in the archive this item was read from, or 0 if not from an archive.</summary>
    public int ArchiveTotalMemberCount { get; set; }

    /// <summary>File size of the archive on disk, or 0 if not from an archive.</summary>
    public long ArchiveFileSize { get; set; }

    /// <summary>
    /// Whether this item is transferred as its original compressed archive instead of having its member extracted.
    /// </summary>
    public bool PreservesArchive { get; set; }

    /// <summary>
    /// Whether this item represents a single-game source archive that is preserved intact without extracting its members.
    /// Only applies to kept games (<see cref="FileStatus.Valid"/>) when archive preservation is enabled and not in ZIP mode.
    /// </summary>
    public bool PreservesSourceArchive(bool preserveArchives, TransferMode transferMode) =>
        preserveArchives &&
        transferMode != TransferMode.Zip &&
        (Status == FileStatus.Valid || (IsOutsideSelection && ReleaseStatus == FileStatus.Valid)) &&
        !IsWholeArchive &&
        !string.IsNullOrEmpty(ArchivePath) &&
        ArchiveTotalMemberCount > 0 &&
        ArchiveTotalMemberCount == 1 + RelatedItems.Count;

    /// <summary>
    /// Whether this item represents a single-game source archive that is preserved intact (<see cref="PreservesSourceArchive(bool, TransferMode)"/>).
    /// </summary>
    public bool PreservesSourceArchive(SortConfig config) =>
        PreservesSourceArchive(config.PreserveArchives, config.TransferMode);

    /// <summary>Comparison of a MAME set with its machine of the DAT; <see langword="null"/> for any other item.</summary>
    public MameSetInfo? MameSet { get; set; }

    public string DisplayName => !string.IsNullOrEmpty(ArchiveMember)
        ? ArchiveMember
        : Path.GetFileName(SourcePath);

    public bool IsMultiMedia => IsMultidisk ?? Parsed.IsMultiMedia;
}
