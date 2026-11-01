namespace RomSieve.Core.Models;

/// <summary>
/// Quarantine of a destination of RomSieve: the <c>_purged</c> directory where each purge moved the previous content,
/// in a dated subdirectory. Only a marked destination has one: elsewhere, a <c>_purged</c> directory is ordinary content.
/// </summary>
/// <param name="Path">Full path of the quarantine directory.</param>
/// <param name="PurgeCount">Number of purges it holds (its dated subdirectories, and any other entry).</param>
/// <param name="FileCount">Number of files it holds, at any depth; a link counts as a file.</param>
/// <param name="TotalBytes">Size of these files; a link counts for nothing, as its target is never deleted.</param>
public sealed record QuarantineInfo(string Path, int PurgeCount, long FileCount, long TotalBytes);

/// <summary>Result of emptying the quarantine of a destination.</summary>
/// <param name="DeletedFiles">Files and links deleted.</param>
/// <param name="DeletedBytes">Size of the files deleted.</param>
/// <param name="Errors">Entries that could not be deleted, with the reason; they stay in the quarantine.</param>
public sealed record QuarantineEmptyResult(long DeletedFiles, long DeletedBytes, IReadOnlyList<string> Errors);
