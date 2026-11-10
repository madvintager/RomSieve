using System.Buffers;
using System.IO.Hashing;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Destination preparation, collision resolution, physical transfers
/// (Copy, Move, Hardlink, Symlink, ZIP extraction), and .m3u playlist generation.
/// </summary>
public sealed partial class FileTransferExecutor
{
    private readonly SortConfig _config;
    private readonly bool _isDryRun;
    private readonly IPathOrganizerService _organizer;
    private readonly IRomFilterService _filterService;
    private readonly IOpticalCompanionService _opticalService;
    private readonly IChecksumService _checksumService;
    private readonly ILocalizationService _i18n;
    private const int CopyBufferSize = 1024 * 1024;

    private readonly Action<LogLevel, LocalizedMessage> _log;
    private readonly OperationJournalWriter? _journal;
    private readonly ICrcCacheService? _crcCache;
    private string? _currentTargetInProgress;
    private IReadOnlyList<string>? _volumeRoots;

    /// <summary>
    /// Test hook called with the (extended) target path once the data of a copy is written, before its verification.
    /// </summary>
    internal Action<string>? AfterTargetWritten { get; set; }

    /// <summary>
    /// Test hook wrapping every stream read from a source (file or archive member), to simulate a read failure such as a
    /// network share that goes away in the middle of a file.
    /// </summary>
    internal Func<Stream, Stream>? WrapSourceStream { get; set; }

    /// <summary>
    /// Test hook wrapping every stream written to the destination (copy, extraction, archive, rewritten descriptor), to
    /// simulate a write failure such as a full disk.
    /// </summary>
    internal Func<Stream, Stream>? WrapTargetStream { get; set; }

    /// <summary>Test hook called with the path of a file once it is written to the disk (<see cref="FlushToDisk"/>).</summary>
    internal Action<string>? AfterFlushedToDisk { get; set; }

    /// <summary>
    /// Writes a file to the disk itself, beyond the caches of the system, before its source is deleted (a move across
    /// volumes, a descriptor rewritten by a move): otherwise the deletion of the source could reach its disk before the
    /// data of the copy reach theirs, and a power cut or a disk unplugged right after would lose the file.
    /// </summary>
    private void FlushToDisk(FileStream stream)
    {
        stream.Flush(flushToDisk: true);
        AfterFlushedToDisk?.Invoke(stream.Name);
    }

    /// <summary>
    /// Why the sort must stop before its next file, or <c>null</c>: set when the destination is full, since every later
    /// file would fail as well, each after writing part of itself.
    /// </summary>
    public LocalizedMessage? StopReason { get; private set; }

    /// <summary>
    /// Whether a write failed because the disk (or the quota of the user) is full: <c>ERROR_DISK_FULL</c> and
    /// <c>ERROR_HANDLE_DISK_FULL</c> under Windows, <c>ENOSPC</c> and <c>EDQUOT</c> elsewhere, as .NET reports them.
    /// </summary>
    internal static bool IsDiskFull(Exception exception) => exception is IOException { HResult: var code } && (code switch
    {
        unchecked((int)0x80070070) or unchecked((int)0x80070027) => true,
        28 or 122 => !OperatingSystem.IsWindows(),
        _ => false
    });

    private void NoteFailure(ProcessItem item, Exception exception)
    {
        if (StopReason is null && IsDiskFull(exception))
        {
            StopReason = LocalizedMessage.Create("log.sort_stopped_disk_full", ("name", item.DisplayName), ("path", _config.DestinationDir));
        }
    }

    /// <summary>
    /// After the transfer of a game: a game that is not whole in the destination is in error, as a whole. When its first
    /// file failed, the others were not written; when one of the others failed, the files written stay (journaled, so
    /// that the undo removes them) and the game is marked incomplete.
    /// </summary>
    private void CompleteSet(ProcessItem item, TargetSet set, FileStatus expected, ProcessStats stats)
    {
        if (item.Status != FileStatus.Error)
        {
            var failed = item.RelatedItems.FirstOrDefault(r => r.Status == FileStatus.Error);
            if (failed is null)
            {
                return;
            }

            item.Status = FileStatus.Error;
            item.StatusReason = LocalizedMessage.Create("reason.set_transfer_incomplete", ("name", failed.DisplayName));
            _log(LogLevel.Warn, LocalizedMessage.Create("log.set_transfer_incomplete", ("name", item.DisplayName), ("failed", failed.DisplayName)));
        }
        else
        {
            for (var i = 0; i < item.RelatedItems.Count; i++)
            {
                var relItem = item.RelatedItems[i];
                if (relItem.Status != FileStatus.Error)
                {
                    relItem.Status = FileStatus.Error;
                    relItem.StatusReason = LocalizedMessage.Create("reason.set_transfer_failed", ("name", item.DisplayName));
                    relItem.TargetPath ??= set.RelatedTargets.ElementAtOrDefault(i);
                    relItem.FinalTargetPath ??= relItem.TargetPath;
                }
            }
        }

        // Counted as written before its transfer: now counted in error
        var bytes = item.FileSize + item.RelatedItems.Sum(r => r.FileSize);
        if (expected == FileStatus.Valid)
        {
            stats.ValidCount--;
            stats.ValidBytes -= bytes;
        }
        else
        {
            stats.UnmatchedCount--;
            stats.UnmatchedBytes -= bytes;
        }

        stats.ErrorCount++;
    }

    /// <summary>
    /// Whether the run sorts MAME sets (<see cref="MameSetIdentifier.IsMameRun"/>): only then is the CHD folder read, and
    /// protected from the purge.
    /// </summary>
    public bool IsMameRun { get; init; }

    /// <summary>
    /// Catalog of the run, which names the DAT of each file (<c>{dat}</c>) and, for a lot, its folder in the destination;
    /// <c>null</c> without a DAT.
    /// </summary>
    public DatCatalog? DatCatalog { get; init; }

    /// <summary>
    /// The DAT <paramref name="item"/> is sorted with: its DAT of the lot (<see cref="ProcessItem.DatName"/>), with its
    /// path; the DAT itself for a run with a single DAT; <c>null</c> without a DAT, or for a file no DAT of the lot knows.
    /// </summary>
    internal DatPlacement? PlacementOf(ProcessItem item)
    {
        if (DatCatalog is null)
        {
            return null;
        }

        if (DatCatalog.Dats is null)
        {
            var name = !string.IsNullOrWhiteSpace(DatCatalog.Header.Name)
                ? DatCatalog.Header.Name
                : Path.GetFileNameWithoutExtension(_config.DatFile);
            return string.IsNullOrWhiteSpace(name) ? null : new DatPlacement(name);
        }

        return DatCatalog.DatMemberFor(item.DatName) is { } member
            ? new DatPlacement(member.Name, member.RelativeFolder, IsLotMember: true)
            : null;
    }

    /// <summary>Test hook replacing the volume roots of the machine, to force a move across volumes.</summary>
    internal IReadOnlyList<string>? VolumeRoots
    {
        get => _volumeRoots;
        set => _volumeRoots = value;
    }

    /// <summary>Raised when the CRC32 of a written target differs from the CRC32 of the data read from its source.</summary>
    private sealed class CopyVerificationException(string expectedCrc, string actualCrc)
        : IOException($"CRC32 {expectedCrc} != {actualCrc}")
    {
        public string ExpectedCrc { get; } = expectedCrc;
        public string ActualCrc { get; } = actualCrc;
    }

    public FileTransferExecutor(
        SortConfig config,
        bool isDryRun,
        IPathOrganizerService organizer,
        IRomFilterService filterService,
        ILocalizationService i18n,
        IOpticalCompanionService opticalService,
        IChecksumService checksumService,
        Action<LogLevel, LocalizedMessage>? logCallback = null,
        OperationJournalWriter? journal = null,
        ICrcCacheService? crcCache = null)
    {
        _checksumService = checksumService;
        _config = config;
        _isDryRun = isDryRun;
        _organizer = organizer;
        _filterService = filterService;
        _opticalService = opticalService;
        _i18n = i18n;
        _log = logCallback ?? ((_, _) => { });
        _journal = journal;
        _crcCache = crcCache;
    }

    /// <summary>
    /// Records a successful operation in the journal of the sort, when journaling is enabled.
    /// </summary>
    private Stream WrapSource(Stream stream) => WrapSourceStream?.Invoke(stream) ?? stream;

    private Stream WrapTarget(Stream stream) => WrapTargetStream?.Invoke(stream) ?? stream;

    /// <summary>
    /// Path the data of a transfer is written to: never the target itself, which only ever appears complete. The data goes
    /// to a file next to it, put in its place once complete (<see cref="ReplaceTarget"/>): a sort stopped in the middle,
    /// even by the end of its process, leaves at most this file, which a scan skips and the next write of the same target
    /// removes, and a file that already holds the target (collision mode "overwrite") is kept until then. The
    /// replacements of the same target that a stopped sort left behind are removed first.
    /// </summary>
    private string GetWritePath(string targetPath)
    {
        DeleteStaleReplacements(targetPath);
        return DestinationGuard.CreateReplacementPath(targetPath);
    }

    /// <summary>
    /// Replacements found in each directory where the run writes, listed at its first write there: the next writes of
    /// the directory, a whole collection in a flat destination, never list it again.
    /// </summary>
    private readonly Dictionary<string, List<string>> _replacementsByDirectory = new(StringComparer.Ordinal);

    /// <summary>
    /// Removes the replacements of a target older than <see cref="DestinationGuard.StaleTemporaryFileAge"/>: only files
    /// named as RomSieve names them, which no sort writes any more. Best effort: a file that cannot be removed stays.
    /// </summary>
    private void DeleteStaleReplacements(string targetPath)
    {
        var directory = Path.GetDirectoryName(targetPath);
        var targetName = Path.GetFileName(targetPath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        if (!_replacementsByDirectory.TryGetValue(directory, out var replacements))
        {
            replacements = ListReplacements(directory);
            _replacementsByDirectory[directory] = replacements;
        }

        for (var i = replacements.Count - 1; i >= 0; i--)
        {
            var file = replacements[i];
            try
            {
                if (DestinationGuard.IsReplacementOf(Path.GetFileName(file), targetName)
                    && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) >= DestinationGuard.StaleTemporaryFileAge)
                {
                    File.Delete(file);
                    replacements.RemoveAt(i);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left in place: a scan never takes it for a file of the collection
            }
        }
    }

    /// <summary>Replacements of a directory of the destination; none when it does not exist yet or cannot be read.</summary>
    private List<string> ListReplacements(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(_organizer.FormatWindowsExtendedPath(directory), "*" + DestinationGuard.ReplacementExtension)
                .Where(DestinationGuard.IsReplacementFile)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Puts a complete file written by <see cref="GetWritePath"/> in place of the target.</summary>
    private void ReplaceTarget(string writePath, string targetPath) =>
        File.Move(_organizer.FormatWindowsExtendedPath(writePath), _organizer.FormatWindowsExtendedPath(targetPath), overwrite: true);

    private Task RecordAsync(JournalOperation operation) =>
        _journal?.RecordAsync(operation with { TimestampUtc = DateTime.UtcNow }) ?? Task.CompletedTask;

    private static string FormatCrc(uint crc) => crc.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Logs a file written to the destination: under <paramref name="level"/> by a real sort, as
    /// <see cref="LogLevel.Planned"/> by a simulation, which writes nothing.
    /// </summary>
    private void LogWritten(LogLevel level, LocalizedMessage message) =>
        _log(_isDryRun ? LogLevel.Planned : level, message);

    /// <summary>
    /// Prepares the destination of a real sort. A purge deletes nothing: each entry of the destination is moved to a
    /// dated quarantine directory (<see cref="DestinationGuard.QuarantineDirectoryName"/>) and journaled, so that undoing
    /// the sort restores it. The destination then receives the RomSieve marker.
    /// </summary>
    public async Task<(bool Success, string? ErrorMessage)> PrepareDestinationAsync()
    {
        var destDir = _config.DestinationDir;
        if (_isDryRun)
        {
            return (true, null);
        }

        if (_config.PurgeDestination && !_config.AppendToDestination && Directory.Exists(destDir))
        {
            // Defense in depth: never purge a destination that is or contains the source directory.
            var overlap = SortConfigValidator.GetPathOverlap(_config.SourceDir, destDir, recursive: false);
            if (overlap != PathOverlap.None)
            {
                return (false, _i18n.Tr(SortConfigValidator.GetErrorKey(overlap) ?? "dialog.error_same_source_dest"));
            }

            // Nor the CHD folder of a MAME collection
            if (IsMameRun && SortConfigValidator.GetChdDirErrorKey(_config.ChdDir, destDir) is { } chdOverlapKey)
            {
                return (false, _i18n.Tr(chdOverlapKey));
            }

            _log(LogLevel.Info, LocalizedMessage.Create("log.purging_destination", ("path", destDir)));
            try
            {
                var entries = DestinationGuard.GetPurgeableEntries(destDir);
                if (entries.Count > 0)
                {
                    var quarantine = DestinationGuard.CreateQuarantineDirectory(destDir, DateTime.UtcNow);
                    foreach (var entry in entries)
                    {
                        var target = Path.Combine(quarantine, Path.GetFileName(entry));
                        if (Directory.Exists(entry) && new FileInfo(entry).LinkTarget is null)
                        {
                            Directory.Move(entry, target);
                        }
                        else
                        {
                            File.Move(entry, target);
                        }

                        await RecordAsync(new JournalOperation(JournalOperationKind.Quarantine, target, entry)).ConfigureAwait(false);
                    }

                    _log(LogLevel.Ok, LocalizedMessage.Create("log.destination_quarantined", ("count", entries.Count), ("path", quarantine)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The entries already moved stay in the quarantine and in the journal: undoing the sort restores them
                return (false, _i18n.Tr("log.cannot_purge_destination", ("error", ex.Message)));
            }
        }
        else if (_config.AppendToDestination && Directory.Exists(destDir))
        {
            _log(LogLevel.Info, LocalizedMessage.Create("log.destination_append_mode"));
        }

        // A marker created by this sort is journaled, so that undoing the sort leaves the destination as it was
        if (DestinationGuard.WriteMarker(destDir) is { } marker)
        {
            await RecordAsync(new JournalOperation(JournalOperationKind.Marker, marker, Size: new FileInfo(marker).Length)).ConfigureAwait(false);
        }

        return (true, null);
    }

    public async Task ProcessItemAsync(
        ProcessItem item,
        ISet<string> claimedPaths,
        ProcessStats stats,
        CancellationToken cancellationToken = default)
    {
        var destDir = _config.DestinationDir;
        if (item.Status == FileStatus.Unmatched && item.IsNotIsolated)
        {
            HandleNotIsolatedItem(item, stats);
        }
        else if (item.Status == FileStatus.Unmatched)
        {
            await HandleUnmatchedItemAsync(item, claimedPaths, stats, destDir, cancellationToken).ConfigureAwait(false);
        }
        else if (item.Status is FileStatus.Excluded or FileStatus.Duplicate)
        {
            HandleExcludedOrDuplicateItem(item, stats, destDir);
        }
        else
        {
            await HandleValidItemAsync(item, claimedPaths, stats, destDir, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleUnmatchedItemAsync(
        ProcessItem item,
        ISet<string> claimedPaths,
        ProcessStats stats,
        string destDir,
        CancellationToken cancellationToken)
    {
        if (RejectsUnsafeNames(item, stats))
        {
            return;
        }

        var displayName = item.DisplayName;
        stats.UnmatchedCount++;
        stats.UnmatchedBytes += item.FileSize + item.RelatedItems.Sum(r => r.FileSize);

        var unmatchedDir = Path.Combine(destDir, PathOrganizerService.UnmatchedFolderName);
        var candidateTarget = ToTargetPath(item, Path.Combine(unmatchedDir, displayName));
        var set = ResolveItemSet(item, candidateTarget, claimedPaths);

        item.TargetPath = candidateTarget;
        item.FinalTargetPath = set.PrimaryTarget;

        if (set.Skipped)
        {
            item.Status = FileStatus.Duplicate;
            stats.DuplicateCount++;
            if (MarkAlreadyInDestination(item, set, claimedPaths, stats))
            {
                _log(LogLevel.Skip, LocalizedMessage.Create("log.already_in_destination", ("name", displayName)));
            }
            else
            {
                item.StatusReason = LocalizedMessage.Create("reason.duplicate_in_unmatched");
                _log(LogLevel.Skip, LocalizedMessage.Create("log.duplicate_ignored_unmatched", ("name", displayName)));
            }

            MarkRelatedItemsSkipped(item, set);
        }
        else if (UsesZip(item))
        {
            await TransferAsZipAsync(item, set, FileStatus.Unmatched, cancellationToken).ConfigureAwait(false);
            if (item.Status == FileStatus.Unmatched)
            {
                LogWritten(LogLevel.Unmatched, LocalizedMessage.Create("log.item_target", ("name", displayName), ("target", Path.GetRelativePath(destDir, set.PrimaryTarget))));
            }

            CompleteSet(item, set, FileStatus.Unmatched, stats);
        }
        else
        {
            await TransferPrimaryAsync(item, set, cancellationToken).ConfigureAwait(false);
            if (item.Status == FileStatus.Unmatched)
            {
                LogWritten(LogLevel.Unmatched, LocalizedMessage.Create("log.item_target", ("name", displayName), ("target", Path.GetRelativePath(destDir, set.PrimaryTarget))));
                await TransferRelatedItemsAsync(item, set, FileStatus.Unmatched, cancellationToken).ConfigureAwait(false);
            }

            CompleteSet(item, set, FileStatus.Unmatched, stats);
        }
    }

    /// <summary>
    /// A non-conforming file that the sort leaves in the source (<see cref="SortConfig.IsolateUnmatched"/> off): it keeps
    /// its status and gets no target. It is counted with the non-conforming files and apart, and logged only in the
    /// summary of the run, like the files left out of a partial sort.
    /// </summary>
    private static void HandleNotIsolatedItem(ProcessItem item, ProcessStats stats)
    {
        var bytes = item.FileSize + item.RelatedItems.Sum(r => r.FileSize);
        stats.UnmatchedCount++;
        stats.UnmatchedBytes += bytes;
        stats.NotIsolatedCount++;
        stats.NotIsolatedBytes += bytes;

        item.StatusReason = item.StatusReason is { } reason
            ? LocalizedMessage.Create("reason.unmatched_not_isolated_after", ("reason", reason))
            : LocalizedMessage.Create("reason.unmatched_not_isolated");
        item.TargetPath = null;
        item.FinalTargetPath = null;
        foreach (var relItem in item.RelatedItems)
        {
            relItem.Status = item.Status;
            relItem.StatusReason = item.StatusReason;
            relItem.IsNotIsolated = true;
            relItem.TargetPath = null;
            relItem.FinalTargetPath = null;
        }
    }

    private void HandleExcludedOrDuplicateItem(
        ProcessItem item,
        ProcessStats stats,
        string destDir)
    {
        var displayName = item.DisplayName;
        if (item.Status == FileStatus.Duplicate)
        {
            stats.DuplicateCount++;
        }
        else if (item.IsOutsideSelection)
        {
            stats.NotSelectedCount++;
            stats.NotSelectedBytes += item.FileSize + item.RelatedItems.Sum(r => r.FileSize);
        }
        else
        {
            stats.ExcludedCount++;
            stats.ExcludedBytes += item.FileSize + item.RelatedItems.Sum(r => r.FileSize);
        }

        // A file left out of a partial sort shows the target the full sort would have given it
        var targetFilename = !string.IsNullOrEmpty(item.TargetFilename) ? item.TargetFilename : displayName;
        var targetPath = item.IsOutsideSelection && item.ReleaseStatus == FileStatus.Unmatched
            ? ToTargetPath(item, Path.Combine(destDir, PathOrganizerService.UnmatchedFolderName, displayName))
            : _organizer.BuildTargetPath(item.Parsed, targetFilename, _config, item.IsMultiMedia, PlacementOf(item));

        item.TargetPath = targetPath;
        item.FinalTargetPath = targetPath;

        // The files left out of a partial sort are only counted in the summary, which keeps the log readable
        if (!item.IsOutsideSelection || item.StatusReason?.Key != "reason.not_in_selection")
        {
            _log(LogLevel.Skip, LocalizedMessage.Create("log.labeled", ("label", item.StatusReason), ("detail", displayName)));
        }

        var finalParent = Path.GetDirectoryName(targetPath) ?? destDir;
        foreach (var relItem in item.RelatedItems)
        {
            relItem.Status = item.Status;
            relItem.StatusReason = item.StatusReason;
            relItem.Is1G1RWinner = item.Is1G1RWinner;
            relItem.WinnerReference = item.WinnerReference;
            relItem.EliminationDetail = item.EliminationDetail;
            relItem.EliminatedBy = item.EliminatedBy;
            relItem.IsOutsideSelection = item.IsOutsideSelection;

            var relTarget = UsesZip(item) ? targetPath : Path.Combine(finalParent, GetRelatedTargetName(relItem));
            relItem.TargetPath = relTarget;
            relItem.FinalTargetPath = relTarget;
        }
    }

    private async Task HandleValidItemAsync(
        ProcessItem item,
        ISet<string> claimedPaths,
        ProcessStats stats,
        string destDir,
        CancellationToken cancellationToken)
    {
        if (RejectsUnsafeNames(item, stats))
        {
            return;
        }

        var displayName = item.DisplayName;
        var targetFilename = !string.IsNullOrEmpty(item.TargetFilename) ? item.TargetFilename : displayName;
        var candidateTarget = _organizer.BuildTargetPath(item.Parsed, targetFilename, _config, item.IsMultiMedia, PlacementOf(item));

        item.TargetPath = candidateTarget;

        var set = ResolveItemSet(item, candidateTarget, claimedPaths);
        item.FinalTargetPath = set.PrimaryTarget;

        if (set.Skipped)
        {
            stats.DuplicateCount++;
            item.Status = FileStatus.Duplicate;
            if (MarkAlreadyInDestination(item, set, claimedPaths, stats))
            {
                _log(LogLevel.Skip, LocalizedMessage.Create("log.already_in_destination", ("name", displayName)));
            }
            else
            {
                item.StatusReason = LocalizedMessage.Create("reason.target_already_exists_skip");
                _log(LogLevel.Skip, LocalizedMessage.Create("log.duplicate_ignored", ("name", displayName)));
            }

            MarkRelatedItemsSkipped(item, set);
        }
        else
        {
            stats.ValidCount++;
            stats.ValidBytes += item.FileSize + item.RelatedItems.Sum(r => r.FileSize);
            item.Status = FileStatus.Valid;

            if (UsesZip(item))
            {
                await TransferAsZipAsync(item, set, FileStatus.Valid, cancellationToken).ConfigureAwait(false);
                if (item.Status == FileStatus.Valid)
                {
                    LogWritten(LogLevel.Ok, LocalizedMessage.Create("log.item_target", ("name", displayName), ("target", Path.GetRelativePath(destDir, set.PrimaryTarget))));
                }

                CompleteSet(item, set, FileStatus.Valid, stats);
                return;
            }

            await TransferPrimaryAsync(item, set, cancellationToken).ConfigureAwait(false);
            if (item.Status == FileStatus.Valid)
            {
                var relDisp = Path.GetRelativePath(destDir, set.PrimaryTarget);
                LogWritten(LogLevel.Ok, LocalizedMessage.Create("log.item_target", ("name", displayName), ("target", relDisp)));
                await TransferRelatedItemsAsync(item, set, FileStatus.Valid, cancellationToken).ConfigureAwait(false);
            }

            CompleteSet(item, set, FileStatus.Valid, stats);
        }
    }

    /// <summary>
    /// Refuses an item whose names, read from an archive or a DAT, would write out of their folder, even out of the
    /// destination (<see cref="PathOrganizerService.IsSafeRelativeName"/>): its member or file name, its DAT name, and
    /// those of its companions. Checked before any target is computed or claimed, by a simulation as well: the item and
    /// its companions are in error, without target, and nothing is written for them.
    /// </summary>
    private bool RejectsUnsafeNames(ProcessItem item, ProcessStats stats)
    {
        var unsafeName = item.RelatedItems.Prepend(item)
            .SelectMany(it => new[] { it.DisplayName, it.TargetFilename })
            .FirstOrDefault(name => !string.IsNullOrEmpty(name) && !PathOrganizerService.IsSafeRelativeName(name));
        if (unsafeName is null)
        {
            return false;
        }

        var reason = LocalizedMessage.Create("reason.unsafe_name", ("name", unsafeName));
        foreach (var file in item.RelatedItems.Prepend(item))
        {
            file.Status = FileStatus.Error;
            file.StatusReason = reason;
            file.TargetPath = null;
            file.FinalTargetPath = null;
        }

        stats.ErrorCount++;
        _log(LogLevel.Error, LocalizedMessage.Create("log.unsafe_name", ("source", item.ArchivePath ?? item.SourcePath), ("name", unsafeName)));
        return true;
    }

    /// <summary>
    /// Last check before anything is written, by a simulation as well: a target outside the destination puts its item in
    /// error, with nothing written. The names that would lead there are refused before (<see cref="RejectsUnsafeNames"/>),
    /// but every write is guarded anyway.
    /// </summary>
    private bool IsOutsideDestination(ProcessItem item, string targetPath)
    {
        if (PathOrganizerService.IsInsideDirectory(targetPath, _config.DestinationDir))
        {
            return false;
        }

        item.Status = FileStatus.Error;
        item.StatusReason = LocalizedMessage.Create("reason.target_outside_destination", ("target", targetPath));
        _log(LogLevel.Error, LocalizedMessage.Create("log.target_outside_destination", ("name", item.DisplayName), ("target", targetPath)));
        return true;
    }

    /// <summary>
    /// Target paths of an item and of its companion files, resolved together against collisions.
    /// </summary>
    private sealed record TargetSet(string PrimaryTarget, IReadOnlyList<string> RelatedTargets, bool Skipped);

    /// <summary>
    /// Target file name of a companion item: its DAT name when identified, otherwise its own file name
    /// (the archive member name for files read from a ZIP archive, not the archive name).
    /// </summary>
    private static string GetRelatedTargetName(ProcessItem relItem) =>
        !string.IsNullOrEmpty(relItem.TargetFilename)
            ? relItem.TargetFilename
            : Path.GetFileName(relItem.DisplayName);

    private TargetSet ResolveItemSet(ProcessItem item, string candidateTarget, ISet<string> claimedPaths)
    {
        var collisionMode = GetCollisionMode(item);
        if (UsesZip(item))
        {
            // The companions go into the archive of their item: only the archive itself may collide
            var (zipPaths, zipSkipped) = _organizer.ResolveSetCollision([candidateTarget], collisionMode, claimedPaths);
            return new TargetSet(zipPaths[0], item.RelatedItems.Select(_ => zipPaths[0]).ToList(), zipSkipped);
        }

        var parent = Path.GetDirectoryName(candidateTarget) ?? _config.DestinationDir;
        var candidates = new List<string>(item.RelatedItems.Count + 1) { candidateTarget };
        candidates.AddRange(item.RelatedItems.Select(rel => Path.Combine(parent, GetRelatedTargetName(rel))));

        var (finalPaths, skipped) = _organizer.ResolveSetCollision(candidates, collisionMode, claimedPaths);
        return new TargetSet(finalPaths[0], finalPaths.Skip(1).ToList(), skipped);
    }

    private bool IsZipMode => _config.TransferMode == TransferMode.Zip;

    /// <summary>
    /// Whether an item and its companions go into an archive of their own (ZIP mode). A MAME set already is an archive
    /// and its CHD disks must stay files: they are transferred as they are.
    /// </summary>
    private bool UsesZip(ProcessItem item) => IsZipMode && !IsKeptAsIs(item);

    private static bool IsKeptAsIs(ProcessItem item) => item.IsWholeArchive || item.Parsed.Convention.IsMameSets();

    /// <summary>
    /// Collision mode of an item. MAME finds a set and its disks by their names only: a renamed copy would be of no use,
    /// so the renaming mode skips them like a duplicate.
    /// </summary>
    private CollisionMode GetCollisionMode(ProcessItem item) =>
        _config.CollisionMode == CollisionMode.Rename && IsKeptAsIs(item) ? CollisionMode.Skip : _config.CollisionMode;

    /// <summary>Target of an item: in ZIP mode, the archive named after the file (its extension replaced by <c>.zip</c>).</summary>
    private string ToTargetPath(ProcessItem item, string fileTarget) => UsesZip(item) ? Path.ChangeExtension(fileTarget, ".zip") : fileTarget;

    /// <summary>
    /// ZIP mode: writes the item and its companions into one TorrentZip archive (<see cref="TorrentZipWriter"/>), the
    /// archive that RomVault and igir write from the same files. Cancellable, removed on failure, optionally verified,
    /// then journaled.
    /// </summary>
    private async Task TransferAsZipAsync(ProcessItem item, TargetSet set, FileStatus status, CancellationToken cancellationToken)
    {
        foreach (var relItem in item.RelatedItems)
        {
            relItem.TargetPath = set.PrimaryTarget;
            relItem.FinalTargetPath = set.PrimaryTarget;
            relItem.Status = status;
        }

        if (IsOutsideDestination(item, set.PrimaryTarget) || _isDryRun)
        {
            return;
        }

        var zipPath = set.PrimaryTarget;
        var members = new List<(string EntryName, ProcessItem Source)>
        {
            (!string.IsNullOrEmpty(item.TargetFilename) ? item.TargetFilename : Path.GetFileName(item.DisplayName), item)
        };
        members.AddRange(item.RelatedItems.Select(rel => (GetRelatedTargetName(rel), rel)));
        var ordered = members
            .Select(m => (EntryName: TorrentZipWriter.NormalizeName(m.EntryName), m.Source))
            .DistinctBy(m => m.EntryName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m.EntryName, TorrentZipWriter.NameComparer)
            .ToList();

        try
        {
            var parentDir = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrEmpty(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            var replacedExistingFile = File.Exists(zipPath);
            var writePath = GetWritePath(zipPath);
            var extendedWrite = _organizer.FormatWindowsExtendedPath(writePath);
            var extendedZip = _organizer.FormatWindowsExtendedPath(zipPath);
            _currentTargetInProgress = writePath;

            // CRC32 of each entry, in the order of the archive. The writer reads the stream back only to move the data of
            // an entry whose expected size missed the ZIP64 decision
            var crcs = new List<string>(ordered.Count);
            await using (var zipStream = WrapTarget(new FileStream(extendedWrite, FileMode.Create, FileAccess.ReadWrite, FileShare.None, CopyBufferSize, useAsync: true)))
            {
                var writer = new TorrentZipWriter(zipStream);
                foreach (var (entryName, source) in ordered)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await using var sourceStream = WrapSource(OpenSource(source));
                    var expectedSize = sourceStream.CanSeek ? sourceStream.Length : source.FileSize;
                    var crc = await writer.AddEntryAsync(entryName, sourceStream, expectedSize, cancellationToken).ConfigureAwait(false);
                    crcs.Add(FormatCrc(crc));
                }

                await writer.FinishAsync(cancellationToken).ConfigureAwait(false);
            }

            await VerifyZipAsync(extendedWrite, crcs, cancellationToken).ConfigureAwait(false);
            ReplaceTarget(writePath, zipPath);
            _currentTargetInProgress = null;

            string zipCrc;
            await using (var written = new FileStream(extendedZip, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                zipCrc = await CopyStreamAsync(written, Stream.Null, cancellationToken).ConfigureAwait(false);
            }

            await RecordAsync(new JournalOperation(
                JournalOperationKind.ZipArchive,
                zipPath,
                item.ArchivePath ?? item.SourcePath,
                item.ArchiveMember,
                new FileInfo(zipPath).Length,
                zipCrc,
                ReplacedExistingFile: replacedExistingFile)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CleanupPartial();
            throw;
        }
        catch (Exception ex)
        {
            CleanupPartial();
            item.Status = FileStatus.Error;
            item.ErrorMessage = ex.Message;
            foreach (var relItem in item.RelatedItems)
            {
                relItem.Status = FileStatus.Error;
            }

            if (ex is CopyVerificationException verification)
            {
                item.StatusReason = LocalizedMessage.Create("reason.copy_verification_failed", ("expected", verification.ExpectedCrc), ("actual", verification.ActualCrc));
                _log(LogLevel.Error, LocalizedMessage.Create(
                    "log.copy_verification_failed", ("name", item.DisplayName), ("expected", verification.ExpectedCrc), ("actual", verification.ActualCrc)));
            }
            else
            {
                _log(LogLevel.Error, LocalizedMessage.Create("log.transfer_error", ("name", item.DisplayName), ("error", ex.Message)));
                NoteFailure(item, ex);
            }
        }
    }

    /// <summary>Data of an item to copy: the member of its archive, or its file.</summary>
    private Stream OpenSource(ProcessItem item) =>
        !string.IsNullOrEmpty(item.ArchivePath) && !string.IsNullOrEmpty(item.ArchiveMember)
            ? _checksumService.OpenArchiveMember(item.ArchivePath, item.ArchiveMember)
            : new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>
    /// When <see cref="SortConfig.VerifyCopies"/> is enabled, checks the TorrentZip signature of a written archive (its
    /// comment and the CRC32 of its central directory), then decompresses every entry and compares its CRC32 with the
    /// data read from its source (and with the CRC32 recorded by the archive). The entries are taken in the order of the
    /// archive, not by name: a name written in code page 437 does not read back as written.
    /// </summary>
    private async Task VerifyZipAsync(string extendedZip, IReadOnlyList<string> expectedCrcs, CancellationToken cancellationToken)
    {
        AfterTargetWritten?.Invoke(extendedZip);
        if (!_config.VerifyCopies)
        {
            return;
        }

        await using (var archive = new FileStream(extendedZip, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.Asynchronous))
        {
            var (declared, actual) = await TorrentZipWriter.ReadSignatureAsync(archive, cancellationToken).ConfigureAwait(false);
            if (declared is null || !string.Equals(declared, actual, StringComparison.Ordinal))
            {
                throw new CopyVerificationException(declared?.ToLowerInvariant() ?? "--------", actual?.ToLowerInvariant() ?? "--------");
            }
        }

        using var zip = ZipFile.OpenRead(extendedZip);
        if (zip.Entries.Count != expectedCrcs.Count)
        {
            throw new CopyVerificationException(expectedCrcs.Count > 0 ? expectedCrcs[0] : "--------", "--------");
        }

        foreach (var (entry, expected) in zip.Entries.Zip(expectedCrcs))
        {
            string actual;
            try
            {
                await using var stream = entry.Open();
                actual = await CopyStreamAsync(stream, Stream.Null, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                // A damaged entry fails its own CRC check while being read
                actual = FormatCrc(entry.Crc32);
                if (string.Equals(actual, expected, StringComparison.Ordinal))
                {
                    actual = "--------";
                }
            }

            if (!string.Equals(actual, expected, StringComparison.Ordinal) || !string.Equals(FormatCrc(entry.Crc32), expected, StringComparison.Ordinal))
            {
                throw new CopyVerificationException(expected, actual);
            }
        }
    }

    /// <summary>
    /// Marks a skipped item whose targets were already in the destination before the sort, none being claimed by
    /// another file of the run, and counts it; <see langword="false"/> for a target taken by the run itself.
    /// </summary>
    private static bool MarkAlreadyInDestination(ProcessItem item, TargetSet set, ISet<string> claimedPaths, ProcessStats stats)
    {
        var claimedByTheRun = set.RelatedTargets.Prepend(set.PrimaryTarget)
            .Any(path => claimedPaths.Contains(PathOrganizerService.NormalizeCollisionKey(path)));
        item.IsAlreadyInDestination = !claimedByTheRun;
        if (claimedByTheRun)
        {
            return false;
        }

        item.StatusReason = LocalizedMessage.Create("reason.already_in_destination");
        stats.AlreadyInDestinationCount++;
        return true;
    }

    private static void MarkRelatedItemsSkipped(ProcessItem item, TargetSet set)
    {
        for (var i = 0; i < item.RelatedItems.Count; i++)
        {
            var relItem = item.RelatedItems[i];
            relItem.Status = item.Status;
            relItem.StatusReason = item.StatusReason;
            relItem.IsAlreadyInDestination = item.IsAlreadyInDestination;
            relItem.TargetPath = set.RelatedTargets[i];
            relItem.FinalTargetPath = set.RelatedTargets[i];
        }
    }

    private async Task TransferRelatedItemsAsync(
        ProcessItem item,
        TargetSet set,
        FileStatus status,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < item.RelatedItems.Count; i++)
        {
            var relItem = item.RelatedItems[i];
            relItem.TargetPath = set.RelatedTargets[i];
            relItem.FinalTargetPath = set.RelatedTargets[i];
            relItem.Status = status;

            // A full disk: the next files of the game would fail as well
            if (StopReason is not null)
            {
                relItem.Status = FileStatus.Error;
                relItem.StatusReason = LocalizedMessage.Create("reason.sort_stopped");
                continue;
            }

            await PerformTransferAsync(relItem, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Transfers the primary item of a set. When its companion files were renamed to avoid a collision
    /// and the item is an optical descriptor, a rewritten descriptor referencing the new names is written instead.
    /// </summary>
    private Task TransferPrimaryAsync(ProcessItem item, TargetSet set, CancellationToken cancellationToken)
    {
        var renamedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < item.RelatedItems.Count; i++)
        {
            var relItem = item.RelatedItems[i];
            var newName = Path.GetFileName(set.RelatedTargets[i]);
            foreach (var oldName in new[] { GetRelatedTargetName(relItem), Path.GetFileName(relItem.DisplayName) })
            {
                if (!string.Equals(oldName, newName, StringComparison.Ordinal))
                {
                    renamedFiles[oldName] = newName;
                }
            }
        }

        return renamedFiles.Count > 0 && _opticalService.IsDescriptor(item.DisplayName)
            ? PerformDescriptorRewriteAsync(item, renamedFiles, cancellationToken)
            : PerformTransferAsync(item, cancellationToken);
    }

    private async Task PerformDescriptorRewriteAsync(
        ProcessItem item,
        IReadOnlyDictionary<string, string> renamedFiles,
        CancellationToken cancellationToken)
    {
        var targetPath = item.FinalTargetPath;
        if (string.IsNullOrEmpty(targetPath) || IsOutsideDestination(item, targetPath))
        {
            return;
        }

        var isLinkMode = _config.TransferMode is TransferMode.HardLink or TransferMode.SymbolicLink;
        var logLevel = isLinkMode ? LogLevel.Warn : LogLevel.Info;
        var logKey = isLinkMode ? "log.descriptor_rewritten_copy" : "log.descriptor_rewritten";
        if (_isDryRun)
        {
            _log(logLevel, LocalizedMessage.Create(logKey, ("name", item.DisplayName)));
            return;
        }

        try
        {
            var parentDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            var archivePath = item.ArchivePath;
            var archiveMember = item.ArchiveMember;
            var isArchiveMember = false;
            byte[] original;
            if (!string.IsNullOrEmpty(archivePath) && !string.IsNullOrEmpty(archiveMember))
            {
                isArchiveMember = true;
                await using var src = _checksumService.OpenArchiveMember(archivePath, archiveMember);
                using var buffer = new MemoryStream();
                await src.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                original = buffer.ToArray();
            }
            else
            {
                original = await File.ReadAllBytesAsync(item.SourcePath, cancellationToken).ConfigureAwait(false);
            }

            var (text, encoding) = DecodeDescriptor(original);
            var rewritten = _opticalService.RewriteReferencedFiles(text, Path.GetExtension(item.DisplayName), renamedFiles);
            byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(rewritten)];

            // A move must still remove the source descriptor once its rewritten copy is safely written, on the disk;
            // its original content is journaled so that an undo can restore it
            var removesSource = !isArchiveMember && _config.TransferMode == TransferMode.Move;
            var replacedExistingFile = File.Exists(targetPath);
            var writePath = GetWritePath(targetPath);
            _currentTargetInProgress = writePath;
            await using (var file = new FileStream(_organizer.FormatWindowsExtendedPath(writePath), FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true))
            await using (var stream = WrapTarget(file))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (removesSource)
                {
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    FlushToDisk(file);
                }
            }

            ReplaceTarget(writePath, targetPath);
            _currentTargetInProgress = null;

            if (removesSource)
            {
                File.Delete(item.SourcePath);
            }

            await RecordAsync(new JournalOperation(
                JournalOperationKind.DescriptorRewrite,
                targetPath,
                isArchiveMember ? archivePath : item.SourcePath,
                isArchiveMember ? archiveMember : null,
                bytes.Length,
                FormatCrc(Crc32.HashToUInt32(bytes)),
                ReplacedExistingFile: replacedExistingFile,
                OriginalContentBase64: removesSource ? Convert.ToBase64String(original) : null)).ConfigureAwait(false);

            _log(logLevel, LocalizedMessage.Create(logKey, ("name", item.DisplayName)));
        }
        catch (OperationCanceledException)
        {
            CleanupPartial();
            throw;
        }
        catch (Exception ex)
        {
            CleanupPartial();
            item.Status = FileStatus.Error;
            item.ErrorMessage = ex.Message;
            _log(LogLevel.Error, LocalizedMessage.Create("log.transfer_error", ("name", item.DisplayName), ("error", ex.Message)));
            NoteFailure(item, ex);
        }
    }

    /// <summary>
    /// Decodes a descriptor as strict UTF-8 (keeping any byte order mark), falling back to Latin-1,
    /// which round-trips any byte sequence unchanged.
    /// </summary>
    private static (string Text, Encoding Encoding) DecodeDescriptor(byte[] bytes)
    {
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasBom, throwOnInvalidBytes: true);
        try
        {
            var offset = hasBom ? 3 : 0;
            return (utf8.GetString(bytes, offset, bytes.Length - offset), utf8);
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.Latin1.GetString(bytes), Encoding.Latin1);
        }
    }

    public async Task PerformTransferAsync(ProcessItem item, CancellationToken cancellationToken = default)
    {
        var targetPath = item.FinalTargetPath;
        if (string.IsNullOrEmpty(targetPath) || IsOutsideDestination(item, targetPath) || _isDryRun)
        {
            return;
        }

        try
        {
            var parentDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            var extendedTarget = _organizer.FormatWindowsExtendedPath(targetPath);
            var replacedExistingFile = File.Exists(targetPath);

            // The data goes to a file next to the target, put in its place once complete: the one deleted on failure
            var writePath = GetWritePath(targetPath);
            var extendedWrite = _organizer.FormatWindowsExtendedPath(writePath);
            _currentTargetInProgress = writePath;
            JournalOperation operation;

            if (!string.IsNullOrEmpty(item.ArchivePath) && !string.IsNullOrEmpty(item.ArchiveMember))
            {
                // Extraction of a ZIP, 7z or RAR member (the archive stays in place, even for a move)
                string crc;
                string archiveCrc;
                long length;
                await using (var src = _checksumService.OpenArchiveMember(item.ArchivePath, item.ArchiveMember))
                await using (var dst = WrapTarget(new FileStream(extendedWrite, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true)))
                {
                    crc = await CopyStreamAsync(WrapSource(src), dst, cancellationToken).ConfigureAwait(false);
                    archiveCrc = src.ExpectedCrc32;
                    length = dst.Length;
                }

                // The extracted data must also match the CRC recorded in the archive, when it records one
                if (_config.VerifyCopies && archiveCrc.Length > 0 && !string.Equals(crc, archiveCrc, StringComparison.Ordinal))
                {
                    throw new CopyVerificationException(archiveCrc, crc);
                }
                await VerifyTargetAsync(extendedWrite, crc, cancellationToken).ConfigureAwait(false);
                ReplaceTarget(writePath, targetPath);
                _currentTargetInProgress = targetPath;
                operation = new JournalOperation(JournalOperationKind.Extract, targetPath, item.ArchivePath, item.ArchiveMember, length, crc);
            }
            else
            {
                switch (_config.TransferMode)
                {
                    case TransferMode.Move:
                        _volumeRoots ??= VolumeLocator.GetVolumeRoots();
                        if (VolumeLocator.AreOnSameVolume(
                                Path.GetFullPath(item.SourcePath),
                                Path.GetFullPath(targetPath),
                                _volumeRoots,
                                ignoreCase: OperatingSystem.IsWindows()))
                        {
                            // Same volume: atomic rename, which never leaves a partial file and keeps the target on
                            // failure. Once done, the target is the only copy of the file: never deleted by a cleanup.
                            var length = new FileInfo(item.SourcePath).Length;
                            _currentTargetInProgress = null;
                            File.Move(item.SourcePath, extendedTarget, overwrite: true);
                            operation = new JournalOperation(JournalOperationKind.Move, targetPath, item.SourcePath, Size: length);
                        }
                        else
                        {
                            // Different volumes: cancellable copy, then deletion of the source once the copy is complete,
                            // on the disk (a power cut or a disk unplugged right after would otherwise lose the file) and
                            // verified, when enabled
                            var crc = await CopyFileAsync(item.SourcePath, extendedWrite, WrapSourceStream, WrapTargetStream, FlushToDisk, cancellationToken).ConfigureAwait(false);
                            await VerifyTargetAsync(extendedWrite, crc, cancellationToken).ConfigureAwait(false);
                            ReplaceTarget(writePath, targetPath);
                            _currentTargetInProgress = targetPath;
                            var length = new FileInfo(extendedTarget).Length;
                            File.Delete(item.SourcePath);

                            // The source is gone: the target is now the only copy of the file
                            _currentTargetInProgress = null;
                            operation = new JournalOperation(JournalOperationKind.Move, targetPath, item.SourcePath, Size: length, Crc: crc);
                        }
                        _crcCache?.Rename(item.SourcePath, targetPath);
                        break;

                    case TransferMode.HardLink:
                        CreateHardLinkCrossPlatform(item.SourcePath, extendedWrite);
                        ReplaceTarget(writePath, targetPath);
                        _currentTargetInProgress = targetPath;
                        operation = new JournalOperation(JournalOperationKind.HardLink, targetPath, item.SourcePath, Size: new FileInfo(extendedTarget).Length);
                        break;

                    case TransferMode.SymbolicLink:
                        File.CreateSymbolicLink(extendedWrite, item.SourcePath);
                        ReplaceTarget(writePath, targetPath);
                        _currentTargetInProgress = targetPath;
                        operation = new JournalOperation(JournalOperationKind.SymbolicLink, targetPath, item.SourcePath);
                        break;

                    default:
                        {
                            var crc = await CopyFileAsync(item.SourcePath, extendedWrite, WrapSourceStream, WrapTargetStream, flushToDisk: null, cancellationToken).ConfigureAwait(false);
                            await VerifyTargetAsync(extendedWrite, crc, cancellationToken).ConfigureAwait(false);
                            ReplaceTarget(writePath, targetPath);

                            // Complete but not journaled yet: removed if what follows fails, the source being untouched
                            _currentTargetInProgress = targetPath;
                            operation = new JournalOperation(JournalOperationKind.Copy, targetPath, item.SourcePath, Size: new FileInfo(extendedTarget).Length, Crc: crc);
                            break;
                        }
                }
            }

            _currentTargetInProgress = null;
            await RecordAsync(operation with { ReplacedExistingFile = replacedExistingFile }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CleanupPartial();
            throw;
        }
        catch (CopyVerificationException ex)
        {
            // The corrupted target is deleted; the source is untouched since it is only removed after a verified copy
            CleanupPartial();
            item.Status = FileStatus.Error;
            item.StatusReason = LocalizedMessage.Create("reason.copy_verification_failed", ("expected", ex.ExpectedCrc), ("actual", ex.ActualCrc));
            item.ErrorMessage = ex.Message;
            _log(LogLevel.Error, LocalizedMessage.Create(
                "log.copy_verification_failed", ("name", item.DisplayName), ("expected", ex.ExpectedCrc), ("actual", ex.ActualCrc)));
        }
        catch (Exception ex)
        {
            CleanupPartial();
            item.Status = FileStatus.Error;
            item.ErrorMessage = ex.Message;
            _log(LogLevel.Error, LocalizedMessage.Create("log.transfer_error", ("name", item.DisplayName), ("error", ex.Message)));
            NoteFailure(item, ex);
        }
    }

    /// <summary>
    /// When <see cref="SortConfig.VerifyCopies"/> is enabled, re-reads a written target and compares its CRC32 with
    /// <paramref name="expectedCrc"/>, the CRC32 of the data read from the source during the copy.
    /// </summary>
    private async Task VerifyTargetAsync(string extendedTarget, string expectedCrc, CancellationToken cancellationToken)
    {
        AfterTargetWritten?.Invoke(extendedTarget);
        if (!_config.VerifyCopies)
        {
            return;
        }

        string actualCrc;
        await using (var stream = new FileStream(extendedTarget, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            actualCrc = await CopyStreamAsync(stream, Stream.Null, cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(actualCrc, expectedCrc, StringComparison.Ordinal))
        {
            throw new CopyVerificationException(expectedCrc, actualCrc);
        }
    }

    private void CreateHardLinkCrossPlatform(string sourcePath, string targetPath)
    {
        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        if (OperatingSystem.IsWindows())
        {
            // The native call bypasses the long path support of .NET: both paths need the \\?\ form beyond MAX_PATH
            if (!CreateHardLinkWin32(targetPath, _organizer.FormatWindowsExtendedPath(sourcePath), IntPtr.Zero))
            {
                var err = Marshal.GetLastPInvokeError();
                throw new IOException(err switch
                {
                    17 => _i18n.Tr("log.hardlink_cross_device"), // ERROR_NOT_SAME_DEVICE
                    1 or 50 => _i18n.Tr("log.hardlink_unsupported_filesystem"), // ERROR_INVALID_FUNCTION, ERROR_NOT_SUPPORTED (FAT32, exFAT)
                    1142 => _i18n.Tr("log.hardlink_too_many_links"), // ERROR_TOO_MANY_LINKS
                    _ => Marshal.GetPInvokeErrorMessage(err)
                });
            }
        }
        else
        {
            if (LinkPosix(sourcePath, targetPath) != 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                throw new IOException(errno switch
                {
                    18 => _i18n.Tr("log.hardlink_cross_device"), // EXDEV
                    31 => _i18n.Tr("log.hardlink_too_many_links"), // EMLINK
                    _ => Marshal.GetPInvokeErrorMessage(errno)
                });
            }
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkWin32(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LinkPosix(string oldpath, string newpath);

    /// <summary>
    /// Copies a file with a large asynchronous buffer, checking <paramref name="cancellationToken"/> between chunks,
    /// and preserves the last write time of the source. Returns the CRC32 of the copied data, computed on the fly.
    /// The caller is responsible for deleting a partial target on failure.
    /// </summary>
    public static Task<string> CopyFileAsync(string sourcePath, string targetPath, CancellationToken cancellationToken) =>
        CopyFileAsync(sourcePath, targetPath, wrapSource: null, wrapTarget: null, flushToDisk: null, cancellationToken);

    /// <inheritdoc cref="CopyFileAsync(string, string, CancellationToken)"/>
    /// <param name="wrapSource">Wraps the stream read (<see cref="WrapSourceStream"/>).</param>
    /// <param name="wrapTarget">Wraps the stream written (<see cref="WrapTargetStream"/>).</param>
    /// <param name="flushToDisk">
    /// Called with the target, still open, once its data is written: for a move, puts it on the disk before the source is
    /// deleted (<see cref="FlushToDisk"/>).
    /// </param>
    internal static async Task<string> CopyFileAsync(
        string sourcePath,
        string targetPath,
        Func<Stream, Stream>? wrapSource,
        Func<Stream, Stream>? wrapTarget,
        Action<FileStream>? flushToDisk,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string crc;
        var sourceOptions = FileOptions.Asynchronous | FileOptions.SequentialScan;
        await using (var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, sourceOptions))
        await using (var dst = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous))
        {
            var source = wrapSource?.Invoke(src) ?? src;
            var target = wrapTarget?.Invoke(dst) ?? dst;
            crc = await CopyStreamAsync(source, target, cancellationToken).ConfigureAwait(false);
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            flushToDisk?.Invoke(dst);
        }

        File.SetLastWriteTimeUtc(targetPath, File.GetLastWriteTimeUtc(sourcePath));
        return crc;
    }

    /// <summary>
    /// Copies a stream chunk by chunk with an explicit cancellation check, since some streams
    /// (such as ZIP entry streams) ignore the token passed to their read methods.
    /// Returns the CRC32 of the copied data.
    /// </summary>
    private static async Task<string> CopyStreamAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var crc32 = new Crc32();
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await source.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                crc32.Append(buffer.AsSpan(0, read));
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return FormatCrc(crc32.GetCurrentHashAsUInt32());
    }

    public void CleanupPartial()
    {
        var target = _currentTargetInProgress;

        // Cleared in any case: a target left over from an earlier failure must never be deleted by a later one
        _currentTargetInProgress = null;
        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        try
        {
            var extended = _organizer.FormatWindowsExtendedPath(target);
            if (File.Exists(extended) || new FileInfo(extended).LinkTarget is not null)
            {
                File.Delete(extended);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The destination may be gone (network share): nothing more to do
        }
    }

    /// <summary>
    /// The file name of the first disc of a playlist without the label of that disc (<see cref="ParsedRomName.DiscLabel"/>):
    /// <c>Command &amp; Conquer (USA) (Disc 1) (GDI Disc).cue</c> names the playlist of the whole set
    /// <c>Command &amp; Conquer (USA).m3u</c>.
    /// </summary>
    private static string WithoutDiscLabel(string fileName, ParsedRomName parsed)
    {
        if (parsed.DiscLabel is not { } label)
        {
            return fileName;
        }

        var index = fileName.IndexOf(label, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? fileName : fileName.Remove(index, label.Length);
    }

    /// <summary>
    /// Writes one <c>.m3u</c> playlist per release grouped by <see cref="MultiDiskEvaluator.GroupPlaylistReleases"/>. An
    /// existing playlist, or a path that a file of the sort took (<paramref name="claimedPaths"/>), follows the collision
    /// mode. A cancellation removes the playlist being written and is thrown again.
    /// </summary>
    public async Task GenerateM3uPlaylistsAsync(
        IReadOnlyList<IReadOnlyList<ProcessItem>> playlistReleases,
        ISet<string> claimedPaths,
        CancellationToken cancellationToken = default)
    {
        if (!_config.GenerateM3u)
        {
            return;
        }

        string[] descriptorExts = [".cue", ".gdi", ".chd", ".iso"];
        var generatedM3uPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var groupItems in playlistReleases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hasDescriptor = groupItems.Any(it =>
                descriptorExts.Contains(Path.GetExtension(it.FinalTargetPath!).ToLowerInvariant()));

            IEnumerable<ProcessItem> candidateDiscs = hasDescriptor
                ? groupItems.Where(it => descriptorExts.Contains(Path.GetExtension(it.FinalTargetPath!).ToLowerInvariant())).ToList()
                : groupItems;

            var seenDiscs = new HashSet<int>();
            var uniqueDiscs = new List<ProcessItem>();
            foreach (var it in candidateDiscs.OrderBy(it => MultiDiskEvaluator.ExtractDiskNumber(it.Parsed)))
            {
                var dNum = MultiDiskEvaluator.ExtractDiskNumber(it.Parsed);
                if (dNum == 0 || seenDiscs.Add(dNum))
                {
                    uniqueDiscs.Add(it);
                }
            }

            if (uniqueDiscs.Count < 2)
            {
                continue;
            }

            var firstPath = uniqueDiscs[0].FinalTargetPath!;
            var discParent = Path.GetDirectoryName(firstPath) ?? _config.DestinationDir;
            var m3uFilename = _organizer.GenerateM3uFilename(WithoutDiscLabel(Path.GetFileName(firstPath), uniqueDiscs[0].Parsed));
            var destDir = _config.DestinationDir;
            var placement = _config.M3uPlacement;

            string m3uDir;
            string m3uPath;
            List<string> lines;

            if (_config.SubfolderMultidisk && placement is M3uPlacementMode.Parent or M3uPlacementMode.ParentHidden)
            {
                m3uDir = Path.GetDirectoryName(discParent) ?? discParent;
                m3uPath = Path.Combine(m3uDir, m3uFilename);
                var subfolderName = Path.GetFileName(discParent);
                lines = uniqueDiscs.Select(it => $"{subfolderName}/{Path.GetFileName(it.FinalTargetPath!)}").ToList();
            }
            else
            {
                m3uDir = discParent;
                m3uPath = Path.Combine(m3uDir, m3uFilename);
                lines = uniqueDiscs.Select(it => Path.GetFileName(it.FinalTargetPath!)).ToList();
            }

            if (!PathOrganizerService.IsInsideDirectory(m3uPath, destDir))
            {
                _log(LogLevel.Error, LocalizedMessage.Create("log.target_outside_destination", ("name", m3uFilename), ("target", m3uPath)));
                continue;
            }

            if (!generatedM3uPaths.Add(Path.GetFullPath(m3uPath)))
            {
                continue;
            }

            // A playlist follows the collision mode as any other target: an existing one is skipped, renamed or replaced
            var (resolvedPath, isSkipped) = _organizer.ResolveCollision(m3uPath, _config.CollisionMode, claimedPaths);
            if (isSkipped)
            {
                _log(LogLevel.Skip, LocalizedMessage.Create("log.m3u_skipped_existing", ("path", Path.GetRelativePath(destDir, m3uPath))));
                continue;
            }

            m3uPath = resolvedPath;
            var m3uContent = string.Join("\n", lines) + "\n";
            var relDisp = Path.GetRelativePath(destDir, m3uPath);

            if (!_isDryRun)
            {
                try
                {
                    Directory.CreateDirectory(m3uDir);
                    var m3uBytes = new UTF8Encoding(false).GetBytes(m3uContent);
                    var replacedExistingFile = File.Exists(m3uPath);
                    var writePath = GetWritePath(m3uPath);
                    _currentTargetInProgress = writePath;
                    await File.WriteAllBytesAsync(_organizer.FormatWindowsExtendedPath(writePath), m3uBytes, cancellationToken).ConfigureAwait(false);
                    ReplaceTarget(writePath, m3uPath);
                    _currentTargetInProgress = null;
                    await RecordAsync(new JournalOperation(
                        JournalOperationKind.Playlist,
                        m3uPath,
                        Size: m3uBytes.Length,
                        Crc: FormatCrc(Crc32.HashToUInt32(m3uBytes)),
                        ReplacedExistingFile: replacedExistingFile)).ConfigureAwait(false);
                    _log(LogLevel.Ok, LocalizedMessage.Create("log.m3u_created", ("path", relDisp)));
                }
                catch (OperationCanceledException)
                {
                    // A cancellation stops the playlists as it stops the transfers, without an error
                    CleanupPartial();
                    throw;
                }
                catch (Exception ex)
                {
                    CleanupPartial();
                    _log(LogLevel.Error, LocalizedMessage.Create("log.m3u_error", ("path", relDisp), ("error", ex.Message)));
                }
            }
            else
            {
                _log(LogLevel.Planned, LocalizedMessage.Create("log.m3u_created", ("path", relDisp)));
            }
        }
    }
}
