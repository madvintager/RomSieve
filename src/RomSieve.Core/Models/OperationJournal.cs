namespace RomSieve.Core.Models;

/// <summary>
/// Kind of physical operation recorded in the journal of a real sort.
/// </summary>
public enum JournalOperationKind
{
    /// <summary>A file moved from the source to the destination.</summary>
    Move,
    /// <summary>A file copied to the destination.</summary>
    Copy,
    /// <summary>An archive member extracted to the destination.</summary>
    Extract,
    /// <summary>A hard link created at the destination.</summary>
    HardLink,
    /// <summary>A symbolic link created at the destination.</summary>
    SymbolicLink,
    /// <summary>An optical descriptor rewritten to reference renamed tracks.</summary>
    DescriptorRewrite,
    /// <summary>A ZIP archive created at the destination from a file and its companions (ZIP transfer mode).</summary>
    ZipArchive,
    /// <summary>A generated .m3u playlist.</summary>
    Playlist,
    /// <summary>
    /// An entry (file or directory) of the destination moved to the quarantine by a purge: <c>SourcePath</c> is its
    /// original location, <c>TargetPath</c> its location in the quarantine. Undoing the sort moves it back.
    /// </summary>
    Quarantine,
    /// <summary>The RomSieve marker written in a destination that did not have one; undoing the sort deletes it.</summary>
    Marker
}

/// <summary>
/// One operation performed by a real sort, recorded once it has succeeded.
/// </summary>
/// <param name="Kind">Kind of operation.</param>
/// <param name="TargetPath">File written at the destination.</param>
/// <param name="SourcePath">Original file (or archive), <c>null</c> for generated playlists.</param>
/// <param name="ArchiveMember">Archive member name for extractions.</param>
/// <param name="Size">Size in bytes of the target as written.</param>
/// <param name="Crc">CRC32 of the target as written (lowercase hex), when known without an extra read.</param>
/// <param name="TimestampUtc">Time of the operation.</param>
/// <param name="ReplacedExistingFile">Whether the operation overwrote a file that already existed at the target path.</param>
/// <param name="OriginalContentBase64">Original bytes of a descriptor rewritten during a move, needed to restore it.</param>
public sealed record JournalOperation(
    JournalOperationKind Kind,
    string TargetPath,
    string? SourcePath = null,
    string? ArchiveMember = null,
    long Size = 0,
    string? Crc = null,
    DateTime TimestampUtc = default,
    bool ReplacedExistingFile = false,
    string? OriginalContentBase64 = null);

/// <summary>
/// Summary of a journal file.
/// </summary>
public sealed record JournalSummary(
    string Path,
    DateTime StartedUtc,
    string SourceDir,
    string DestinationDir,
    TransferMode TransferMode,
    int OperationCount,
    bool Completed,
    bool IsUndone,
    int? SelectedCount = null,
    int RetryCount = 0)
{
    /// <summary>Whether the sort only transferred a selection of the simulation (games and non-conforming files).</summary>
    public bool IsPartial => SelectedCount is not null;

    /// <summary>
    /// Whether an undo of this sort already ran and left <see cref="RetryCount"/> operations it could not undo but can
    /// retry (<see cref="UndoIssueKindExtensions.IsRetryable"/>): the next undo only replays them.
    /// </summary>
    public bool IsUndoToResume => RetryCount > 0;

    /// <summary>Operations the next undo replays: all of them, or those a previous undo left to retry.</summary>
    public int OperationsToUndo => IsUndoToResume ? RetryCount : OperationCount;
}

/// <summary>
/// Reason why an operation could not be (fully) undone.
/// </summary>
public enum UndoIssueKind
{
    /// <summary>The target no longer exists; nothing was done.</summary>
    TargetMissing,
    /// <summary>The target changed since the sort (size or CRC); it was kept.</summary>
    TargetModified,
    /// <summary>A file already exists at the original location; nothing was overwritten.</summary>
    OriginOccupied,
    /// <summary>The source of a hard link is missing: deleting the link would lose the data, so it was kept.</summary>
    LinkSourceMissing,
    /// <summary>The target is no longer the link created by the sort; it was kept.</summary>
    NotTheCreatedLink,
    /// <summary>The operation was undone, but the file it had overwritten during the sort cannot be restored.</summary>
    OverwrittenFileLost,
    /// <summary>An I/O error prevented the undo.</summary>
    Failed
}

/// <summary>
/// An operation that could not be (fully) undone.
/// </summary>
public sealed record UndoIssue(JournalOperation Operation, UndoIssueKind Kind, string? Detail = null);

/// <summary>
/// Result of the undo of a sort.
/// </summary>
public sealed record UndoReport(int TotalOperations, int UndoneCount, IReadOnlyList<UndoIssue> Issues)
{
    /// <summary>Operations not undone that the next undo retries (a locked file, an occupied original location).</summary>
    public int RetryableCount => Issues.Count(issue => issue.Kind.IsRetryable());
}

public static class UndoIssueKindExtensions
{
    /// <summary>Translation key of the message that describes an undo issue.</summary>
    public static string ToMessageKey(this UndoIssueKind kind) => kind switch
    {
        UndoIssueKind.TargetMissing => "undo.issue_target_missing",
        UndoIssueKind.TargetModified => "undo.issue_target_modified",
        UndoIssueKind.OriginOccupied => "undo.issue_origin_occupied",
        UndoIssueKind.LinkSourceMissing => "undo.issue_link_source_missing",
        UndoIssueKind.NotTheCreatedLink => "undo.issue_not_the_created_link",
        UndoIssueKind.OverwrittenFileLost => "undo.issue_overwritten_file_lost",
        _ => "undo.issue_failed"
    };

    /// <summary>
    /// Whether an operation left with this issue can be undone later, once its obstacle is gone (a file closed, an
    /// original location freed): the journal stays undoable for it. The other issues are final.
    /// </summary>
    public static bool IsRetryable(this UndoIssueKind kind) => kind is UndoIssueKind.Failed or UndoIssueKind.OriginOccupied;
}
