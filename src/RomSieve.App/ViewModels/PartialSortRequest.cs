using RomSieve.Core.Models;

namespace RomSieve.App.ViewModels;

/// <summary>
/// A partial sort asked from the table: the games and files to transfer, and the sentence that describes what the
/// sort writes (shown in its confirmation).
/// </summary>
public sealed record PartialSortRequest(SortSelection Selection, string SummaryText);
