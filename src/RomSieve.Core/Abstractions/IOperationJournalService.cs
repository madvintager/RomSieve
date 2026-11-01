using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.Core.Abstractions;

/// <summary>
/// Journal of the physical operations of real sorts (one JSON Lines file per sort) and undo of a sort.
/// </summary>
public interface IOperationJournalService
{
    /// <summary>
    /// Directory holding the journal files.
    /// </summary>
    string JournalDirectory { get; }

    /// <summary>
    /// Creates the journal of a new real sort and writes its header.
    /// </summary>
    Task<OperationJournalWriter> BeginAsync(SortConfig config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the journal of a new real sort; <paramref name="selectedCount"/> is the number of games and files
    /// chosen by a partial sort (<see cref="SortSelection"/>), <c>null</c> for a sort of the whole simulation.
    /// </summary>
    Task<OperationJournalWriter> BeginAsync(SortConfig config, int? selectedCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the summary of a journal file, or <c>null</c> when it cannot be read.
    /// </summary>
    JournalSummary? ReadSummary(string journalPath);

    /// <summary>
    /// Most recent journal that holds at least one operation and has not been undone yet, or <c>null</c>.
    /// </summary>
    JournalSummary? FindLastUndoableJournal();

    /// <summary>
    /// Replays a journal backwards to undo its operations, never overwriting a file at an original location and
    /// never deleting a target that changed since the sort, then removes the directories left empty and marks the journal as undone.
    /// </summary>
    Task<UndoReport> UndoAsync(string journalPath);
}
