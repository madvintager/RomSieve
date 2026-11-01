using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// One line of a journal file (JSON Lines format): a header, an operation, an end marker, or an undo marker.
/// </summary>
internal sealed class JournalRecord
{
    public const string HeaderType = "header";
    public const string OperationType = "op";
    public const string EndType = "end";
    public const string UndoType = "undo";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public string Type { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }

    // Header
    public string? SourceDir { get; set; }
    public string? DestinationDir { get; set; }
    public TransferMode? TransferMode { get; set; }

    /// <summary>Number of games and files chosen by a partial sort; absent for a sort of the whole simulation.</summary>
    public int? SelectedCount { get; set; }

    // Operation
    public JournalOperationKind? Kind { get; set; }
    public string? Source { get; set; }
    public string? ArchiveMember { get; set; }
    public string? Target { get; set; }
    public long? Size { get; set; }
    public string? Crc { get; set; }
    public bool? ReplacedExistingFile { get; set; }
    public string? OriginalContent { get; set; }

    // End and undo markers
    public bool? Completed { get; set; }
    public int? Undone { get; set; }
    public int? Issues { get; set; }

    /// <summary>
    /// Indexes, in the journal, of the operations an undo left to retry (<see cref="UndoIssueKindExtensions.IsRetryable"/>);
    /// absent when it left none.
    /// </summary>
    public List<int>? Retry { get; set; }

    public static JournalRecord FromOperation(JournalOperation op) => new()
    {
        Type = OperationType,
        TimestampUtc = op.TimestampUtc == default ? DateTime.UtcNow : op.TimestampUtc,
        Kind = op.Kind,
        Source = op.SourcePath,
        ArchiveMember = op.ArchiveMember,
        Target = op.TargetPath,
        Size = op.Size,
        Crc = op.Crc,
        ReplacedExistingFile = op.ReplacedExistingFile ? true : null,
        OriginalContent = op.OriginalContentBase64
    };

    public JournalOperation? ToOperation() =>
        Kind is { } kind && !string.IsNullOrEmpty(Target)
            ? new JournalOperation(
                kind,
                Target,
                Source,
                ArchiveMember,
                Size ?? 0,
                Crc,
                TimestampUtc,
                ReplacedExistingFile == true,
                OriginalContent)
            : null;
}

/// <summary>
/// Incremental writer of the journal of one real sort. Each operation is appended and flushed as soon as
/// it has succeeded, so the journal survives a crash or a cancellation. A journal without operations is deleted.
/// Journal write errors never interrupt the sort: they are reported through <see cref="HasFailed"/>.
/// </summary>
public sealed class OperationJournalWriter : IAsyncDisposable
{
    private readonly StreamWriter _writer;
    private bool _closed;

    private OperationJournalWriter(string path, StreamWriter writer)
    {
        JournalPath = path;
        _writer = writer;
    }

    public string JournalPath { get; }
    public int OperationCount { get; private set; }
    public bool HasFailed { get; private set; }

    internal static async Task<OperationJournalWriter> CreateAsync(
        string path,
        SortConfig config,
        int? selectedCount = null,
        Func<Stream, Stream>? wrapStream = null)
    {
        Stream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream = wrapStream?.Invoke(stream) ?? stream;
        var writer = new StreamWriter(stream, new UTF8Encoding(false));
        var journal = new OperationJournalWriter(path, writer);
        await journal.WriteRecordAsync(new JournalRecord
        {
            Type = JournalRecord.HeaderType,
            TimestampUtc = DateTime.UtcNow,
            SourceDir = Path.GetFullPath(config.SourceDir),
            DestinationDir = Path.GetFullPath(config.DestinationDir),
            TransferMode = config.TransferMode,
            SelectedCount = selectedCount
        }).ConfigureAwait(false);
        return journal;
    }

    /// <summary>
    /// Appends a successful operation. Never cancelled: an operation that happened must be journaled.
    /// </summary>
    public async Task RecordAsync(JournalOperation operation)
    {
        if (_closed || HasFailed)
        {
            return;
        }

        await WriteRecordAsync(JournalRecord.FromOperation(operation)).ConfigureAwait(false);
        if (!HasFailed)
        {
            OperationCount++;
        }
    }

    /// <summary>
    /// Writes the end marker and closes the journal, deleting it when no operation was recorded.
    /// </summary>
    public async Task CompleteAsync(bool completed)
    {
        if (_closed)
        {
            return;
        }

        await WriteRecordAsync(new JournalRecord
        {
            Type = JournalRecord.EndType,
            TimestampUtc = DateTime.UtcNow,
            Completed = completed
        }).ConfigureAwait(false);
        await CloseAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await CompleteAsync(completed: false).ConfigureAwait(false);

    private async Task WriteRecordAsync(JournalRecord record)
    {
        try
        {
            await _writer.WriteLineAsync(JsonSerializer.Serialize(record, JournalRecord.JsonOptions)).ConfigureAwait(false);
            await _writer.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HasFailed = true;
        }
    }

    private async Task CloseAsync()
    {
        _closed = true;
        try
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
            if (OperationCount == 0)
            {
                File.Delete(JournalPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HasFailed = true;
        }
    }
}

/// <summary>
/// Journal of real sorts and undo of the last sort.
/// </summary>
public sealed class OperationJournalService : IOperationJournalService
{
    private const string JournalSearchPattern = "sort-*.jsonl";

    /// <summary>
    /// Journals kept, the one of the sort that begins included: when a real sort begins, older journals are deleted, and
    /// their sorts can no longer be undone. A journal weighs a few megabytes for a large sort.
    /// </summary>
    public const int KeptJournalCount = 50;

    private readonly IChecksumService _checksumService;
    private readonly ICrcCacheService _crcCache;

    public OperationJournalService(string journalDirectory, IChecksumService checksumService, ICrcCacheService crcCache)
    {
        JournalDirectory = journalDirectory;
        _checksumService = checksumService;
        _crcCache = crcCache;
    }

    public string JournalDirectory { get; }

    /// <summary>
    /// Default journal directory: <c>journals/</c> next to the configuration file.
    /// </summary>
    public static string GetJournalDirectory(string configFilePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configFilePath)) ?? string.Empty, "journals");

    public Task<OperationJournalWriter> BeginAsync(SortConfig config, CancellationToken cancellationToken = default) =>
        BeginAsync(config, selectedCount: null, cancellationToken);

    public Task<OperationJournalWriter> BeginAsync(SortConfig config, int? selectedCount, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(JournalDirectory);
        DeleteOldJournals();
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var path = Path.Combine(JournalDirectory, $"sort-{stamp}-{Guid.NewGuid().ToString("N")[..6]}.jsonl");
        cancellationToken.ThrowIfCancellationRequested();
        return OperationJournalWriter.CreateAsync(path, config, selectedCount, WrapJournalStream);
    }

    /// <summary>
    /// Deletes the journals beyond the <see cref="KeptJournalCount"/> - 1 most recent, before a new one is written. A
    /// journal that cannot be deleted is left for the next sort.
    /// </summary>
    private void DeleteOldJournals()
    {
        // File names start with a sortable UTC timestamp
        foreach (var path in Directory.EnumerateFiles(JournalDirectory, JournalSearchPattern)
                     .OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal)
                     .Skip(KeptJournalCount - 1)
                     .ToList())
        {
            TryDelete(path);
        }
    }

    /// <summary>Test hook wrapping the stream of each journal written, to simulate a journal that cannot be written.</summary>
    internal Func<Stream, Stream>? WrapJournalStream { get; set; }

    /// <summary>Test hook replacing the volume roots of the machine, to force the undo of a move across volumes.</summary>
    internal IReadOnlyList<string>? VolumeRoots { get; set; }

    /// <summary>Test hook called with the path of a file put back once it is written to the disk.</summary>
    internal Action<string>? AfterFlushedToDisk { get; set; }

    public JournalSummary? ReadSummary(string journalPath) =>
        ReadJournal(journalPath) is { } journal ? journal.Summary : null;

    public JournalSummary? FindLastUndoableJournal()
    {
        if (!Directory.Exists(JournalDirectory))
        {
            return null;
        }

        // File names start with a sortable UTC timestamp
        foreach (var path in Directory.EnumerateFiles(JournalDirectory, JournalSearchPattern)
                     .OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal))
        {
            var summary = ReadSummary(path);
            if (summary is { IsUndone: false, OperationCount: > 0 })
            {
                return summary;
            }
        }

        return null;
    }

    public async Task<UndoReport> UndoAsync(string journalPath)
    {
        var journal = ReadJournal(journalPath)
            ?? throw new InvalidDataException($"Unreadable journal: {journalPath}");

        var issues = new List<UndoIssue>();
        var retry = new List<int>();
        var undone = 0;
        var touchedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // After an undo that left operations to retry, only they are replayed, in the same backward order
        var indexes = journal.Retry ?? Enumerable.Range(0, journal.Operations.Count).ToList();
        foreach (var i in indexes.OrderDescending())
        {
            var op = journal.Operations[i];
            UndoIssue? issue;
            try
            {
                issue = await UndoOperationAsync(op).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                issue = new UndoIssue(op, UndoIssueKind.Failed, ex.Message);
            }

            if (issue is null)
            {
                undone++;
                if (op.ReplacedExistingFile)
                {
                    issues.Add(new UndoIssue(op, UndoIssueKind.OverwrittenFileLost));
                }
            }
            else
            {
                issues.Add(issue);
                if (issue.Kind.IsRetryable())
                {
                    retry.Add(i);
                }
            }

            if (Path.GetDirectoryName(op.TargetPath) is { Length: > 0 } dir)
            {
                touchedDirectories.Add(dir);
            }
        }

        RemoveEmptyDirectories(touchedDirectories, journal.Summary.DestinationDir);
        await _crcCache.SaveAsync().ConfigureAwait(false);
        await AppendUndoMarkerAsync(journalPath, undone, issues.Count, retry).ConfigureAwait(false);

        return new UndoReport(indexes.Count, undone, issues);
    }

    private async Task<UndoIssue?> UndoOperationAsync(JournalOperation op)
    {
        switch (op.Kind)
        {
            case JournalOperationKind.Move:
                return await UndoMoveAsync(op).ConfigureAwait(false);

            case JournalOperationKind.HardLink:
                return await UndoHardLinkAsync(op).ConfigureAwait(false);

            case JournalOperationKind.SymbolicLink:
                return UndoSymbolicLink(op);

            case JournalOperationKind.Quarantine:
                return UndoQuarantine(op);

            case JournalOperationKind.DescriptorRewrite when !string.IsNullOrEmpty(op.OriginalContentBase64):
                return await UndoMovedDescriptorRewriteAsync(op).ConfigureAwait(false);

            default:
                // Copy, Extract, DescriptorRewrite (copy semantics), ZipArchive, Playlist, Marker: files created by the sort
                return await DeleteCreatedFileAsync(op).ConfigureAwait(false);
        }
    }

    private async Task<UndoIssue?> UndoMoveAsync(JournalOperation op)
    {
        if (!File.Exists(op.TargetPath))
        {
            return new UndoIssue(op, UndoIssueKind.TargetMissing);
        }

        if (string.IsNullOrEmpty(op.SourcePath) || File.Exists(op.SourcePath) || Directory.Exists(op.SourcePath))
        {
            return new UndoIssue(op, UndoIssueKind.OriginOccupied);
        }

        if (!await IsUnchangedAsync(op).ConfigureAwait(false))
        {
            return new UndoIssue(op, UndoIssueKind.TargetModified);
        }

        if (Path.GetDirectoryName(op.SourcePath) is { Length: > 0 } sourceDir)
        {
            Directory.CreateDirectory(sourceDir);
        }

        var sameVolume = VolumeLocator.AreOnSameVolume(
            Path.GetFullPath(op.TargetPath),
            Path.GetFullPath(op.SourcePath),
            VolumeRoots ?? VolumeLocator.GetVolumeRoots(),
            ignoreCase: OperatingSystem.IsWindows());
        if (sameVolume)
        {
            File.Move(op.TargetPath, op.SourcePath, overwrite: false);
        }
        else
        {
            // As a move of the sort: the file is put back under a temporary name, written to the disk, then renamed, and
            // only then is its copy in the destination deleted. An undo ended in the middle never leaves a truncated file
            // at the original location, nor loses the file.
            var writePath = DestinationGuard.CreateReplacementPath(op.SourcePath);
            try
            {
                await FileTransferExecutor.CopyFileAsync(op.TargetPath, writePath, wrapSource: null, wrapTarget: null, FlushToDisk, CancellationToken.None).ConfigureAwait(false);
                File.Move(writePath, op.SourcePath, overwrite: false);
            }
            catch
            {
                TryDelete(writePath);
                throw;
            }

            File.Delete(op.TargetPath);
        }

        _crcCache.Rename(op.TargetPath, op.SourcePath);
        return null;
    }

    /// <summary>
    /// Moves an entry purged to the quarantine back to its original location. The quarantine lies in the destination,
    /// so the move stays on the same volume; an occupied original location is never overwritten.
    /// </summary>
    private static UndoIssue? UndoQuarantine(JournalOperation op)
    {
        var isDirectory = Directory.Exists(op.TargetPath);
        if (!isDirectory && !File.Exists(op.TargetPath) && new FileInfo(op.TargetPath).LinkTarget is null)
        {
            return new UndoIssue(op, UndoIssueKind.TargetMissing);
        }

        // The sort may have written its own files under the same name: they were undone first (the journal is replayed
        // backwards), but the empty directories they leave are only removed at the end, so one of them is not an obstacle
        if (!string.IsNullOrEmpty(op.SourcePath) && IsEmptyDirectoryTree(op.SourcePath))
        {
            Directory.Delete(op.SourcePath, recursive: true);
        }

        if (string.IsNullOrEmpty(op.SourcePath) || File.Exists(op.SourcePath) || Directory.Exists(op.SourcePath))
        {
            return new UndoIssue(op, UndoIssueKind.OriginOccupied);
        }

        if (Path.GetDirectoryName(op.SourcePath) is { Length: > 0 } parent)
        {
            Directory.CreateDirectory(parent);
        }

        if (isDirectory)
        {
            Directory.Move(op.TargetPath, op.SourcePath);
        }
        else
        {
            File.Move(op.TargetPath, op.SourcePath, overwrite: false);
        }

        return null;
    }

    /// <summary>Whether a path is a directory holding only directories, without any file or link at any depth.</summary>
    private static bool IsEmptyDirectoryTree(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists || info.LinkTarget is not null)
        {
            return false;
        }

        foreach (var entry in info.EnumerateFileSystemInfos())
        {
            if (entry is not DirectoryInfo || entry.LinkTarget is not null || !IsEmptyDirectoryTree(entry.FullName))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<UndoIssue?> UndoHardLinkAsync(JournalOperation op)
    {
        if (!File.Exists(op.TargetPath))
        {
            return new UndoIssue(op, UndoIssueKind.TargetMissing);
        }

        if (string.IsNullOrEmpty(op.SourcePath) || !File.Exists(op.SourcePath))
        {
            return new UndoIssue(op, UndoIssueKind.LinkSourceMissing);
        }

        // Deleting the link only loses nothing when the source still holds the same data
        var targetInfo = new FileInfo(op.TargetPath);
        var sourceInfo = new FileInfo(op.SourcePath);
        if (targetInfo.Length != op.Size || sourceInfo.Length != targetInfo.Length)
        {
            return new UndoIssue(op, UndoIssueKind.TargetModified);
        }

        var targetCrc = await _checksumService.ComputeFileCrc32Async(op.TargetPath).ConfigureAwait(false);
        var sourceCrc = await _checksumService.ComputeFileCrc32Async(op.SourcePath).ConfigureAwait(false);
        if (!string.Equals(targetCrc, sourceCrc, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(op.Crc) && !string.Equals(targetCrc, op.Crc, StringComparison.OrdinalIgnoreCase)))
        {
            return new UndoIssue(op, UndoIssueKind.TargetModified);
        }

        File.Delete(op.TargetPath);
        return null;
    }

    private static UndoIssue? UndoSymbolicLink(JournalOperation op)
    {
        var info = new FileInfo(op.TargetPath);
        if (!info.Exists && info.LinkTarget is null)
        {
            return new UndoIssue(op, UndoIssueKind.TargetMissing);
        }

        if (info.LinkTarget is null ||
            string.IsNullOrEmpty(op.SourcePath) ||
            !string.Equals(
                Path.GetFullPath(info.LinkTarget, Path.GetDirectoryName(op.TargetPath) ?? string.Empty),
                Path.GetFullPath(op.SourcePath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return new UndoIssue(op, UndoIssueKind.NotTheCreatedLink);
        }

        // Deleting a symbolic link never deletes the file it points to
        File.Delete(op.TargetPath);
        return null;
    }

    private async Task<UndoIssue?> UndoMovedDescriptorRewriteAsync(JournalOperation op)
    {
        if (string.IsNullOrEmpty(op.SourcePath) || File.Exists(op.SourcePath) || Directory.Exists(op.SourcePath))
        {
            return new UndoIssue(op, UndoIssueKind.OriginOccupied);
        }

        // Restoring the original descriptor is always safe since its location is free
        if (Path.GetDirectoryName(op.SourcePath) is { Length: > 0 } sourceDir)
        {
            Directory.CreateDirectory(sourceDir);
        }
        await File.WriteAllBytesAsync(op.SourcePath, Convert.FromBase64String(op.OriginalContentBase64 ?? string.Empty)).ConfigureAwait(false);

        return await DeleteCreatedFileAsync(op).ConfigureAwait(false);
    }

    private async Task<UndoIssue?> DeleteCreatedFileAsync(JournalOperation op)
    {
        if (!File.Exists(op.TargetPath))
        {
            return new UndoIssue(op, UndoIssueKind.TargetMissing);
        }

        if (!await IsUnchangedAsync(op).ConfigureAwait(false))
        {
            return new UndoIssue(op, UndoIssueKind.TargetModified);
        }

        File.Delete(op.TargetPath);
        return null;
    }

    /// <summary>
    /// Indicates whether the target still has the size and, when it was recorded, the CRC written by the sort.
    /// </summary>
    private async Task<bool> IsUnchangedAsync(JournalOperation op)
    {
        if (new FileInfo(op.TargetPath).Length != op.Size)
        {
            return false;
        }

        if (string.IsNullOrEmpty(op.Crc))
        {
            return true;
        }

        var crc = await _checksumService.ComputeFileCrc32Async(op.TargetPath).ConfigureAwait(false);
        return string.Equals(crc, op.Crc, StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveEmptyDirectories(IEnumerable<string> directories, string destinationRoot)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        var prefix = root + Path.DirectorySeparatorChar;

        // Deepest directories first, walking up to (but never including) the destination root
        foreach (var start in directories.OrderByDescending(d => d.Length))
        {
            var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(start));
            while (current.StartsWith(prefix, comparison) && current.Length > prefix.Length)
            {
                try
                {
                    if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any())
                    {
                        break;
                    }
                    Directory.Delete(current);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    break;
                }

                current = Path.GetDirectoryName(current) ?? root;
            }
        }
    }

    /// <summary>Writes a file put back to the disk itself, beyond the caches of the system, before its copy is deleted.</summary>
    private void FlushToDisk(FileStream stream)
    {
        stream.Flush(flushToDisk: true);
        AfterFlushedToDisk?.Invoke(stream.Name);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup of a partial copy or of an old journal
        }
    }

    private static async Task AppendUndoMarkerAsync(string journalPath, int undone, int issues, List<int> retry)
    {
        retry.Sort();
        var line = JsonSerializer.Serialize(new JournalRecord
        {
            Type = JournalRecord.UndoType,
            TimestampUtc = DateTime.UtcNow,
            Undone = undone,
            Issues = issues,
            Retry = retry.Count > 0 ? retry : null
        }, JournalRecord.JsonOptions);
        await File.AppendAllTextAsync(journalPath, line + Environment.NewLine, new UTF8Encoding(false)).ConfigureAwait(false);
    }

    /// <summary>
    /// A journal read: its operations, and <paramref name="Retry"/>, the indexes of those the last undo left to retry
    /// (<c>null</c> before any undo, or after an undo that left none).
    /// </summary>
    private sealed record ParsedJournal(JournalSummary Summary, IReadOnlyList<JournalOperation> Operations, IReadOnlyList<int>? Retry);

    /// <summary>
    /// Reads a journal, ignoring malformed lines (such as a last line truncated by a crash).
    /// </summary>
    private static ParsedJournal? ReadJournal(string journalPath)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(journalPath, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        JournalRecord? header = null;
        var operations = new List<JournalOperation>();
        var completed = false;
        var undone = false;
        List<int>? retry = null;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JournalRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<JournalRecord>(line, JournalRecord.JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            switch (record?.Type)
            {
                case JournalRecord.HeaderType:
                    header ??= record;
                    break;
                case JournalRecord.OperationType when record.ToOperation() is { } op:
                    operations.Add(op);
                    break;
                case JournalRecord.EndType:
                    completed = record.Completed == true;
                    break;
                case JournalRecord.UndoType:
                    // Each undo replays what the previous one left: the last marker tells what remains
                    undone = true;
                    retry = record.Retry is { Count: > 0 } left ? left : null;
                    break;
            }
        }

        if (header is null)
        {
            return null;
        }

        // Indexes of an altered journal that name no operation are ignored
        var toRetry = retry?.Where(i => i >= 0 && i < operations.Count).Distinct().ToList();
        if (toRetry is { Count: 0 })
        {
            toRetry = null;
        }

        var summary = new JournalSummary(
            journalPath,
            header.TimestampUtc,
            header.SourceDir ?? string.Empty,
            header.DestinationDir ?? string.Empty,
            header.TransferMode ?? TransferMode.Copy,
            operations.Count,
            completed,
            undone && toRetry is null,
            header.SelectedCount,
            toRetry?.Count ?? 0);
        return new ParsedJournal(summary, operations, toRetry);
    }
}
