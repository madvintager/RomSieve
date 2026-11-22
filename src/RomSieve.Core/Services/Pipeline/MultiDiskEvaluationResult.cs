using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Multi-disk evaluation result returned by <see cref="MultiDiskEvaluator"/>.
/// </summary>
/// <param name="ReleaseTotals">
/// Expected number of disks of each multi-disk release, by release key (of sides, for a TOSEC disk whose sides are named
/// alone).
/// </param>
/// <param name="BorrowedCleanDisks">
/// Disks of the same edition that complete a release (smart multi-disk grouping), by release key: the clean data disks of
/// a TOSEC release, another pressing of a No-Intro / Redump disc. Only releases that these disks make complete are listed.
/// </param>
/// <param name="EditionSides">
/// The sides each disk of a TOSEC edition has, by edition key: a release holds a disk only with each of them.
/// </param>
public sealed record MultiDiskEvaluationResult(
    IReadOnlyDictionary<string, int> ReleaseTotals,
    IReadOnlyDictionary<string, IReadOnlyList<ProcessItem>> BorrowedCleanDisks,
    EditionSides EditionSides
);
