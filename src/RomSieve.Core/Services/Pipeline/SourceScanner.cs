using System.Diagnostics;
using System.Globalization;
using System.IO;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Source file discovery and archive inspection (ZIP, 7z, RAR) without prior extraction.
/// </summary>
public sealed class SourceScanner
{
    private readonly SortConfig _config;
    private readonly IChecksumService _checksumService;
    private readonly IOpticalCompanionService _opticalService;
    private readonly ILocalizationService _i18n;
    private readonly Action<LogLevel, LocalizedMessage> _log;
    private readonly IHeaderCache? _headerCache;

    /// <param name="headerCache">Cache of the archive headers; <see langword="null"/> reads every archive.</param>
    public SourceScanner(
        SortConfig config,
        IChecksumService checksumService,
        ILocalizationService i18n,
        IOpticalCompanionService opticalService,
        Action<LogLevel, LocalizedMessage>? logCallback = null,
        IHeaderCache? headerCache = null)
    {
        _headerCache = headerCache;
        _config = config;
        _checksumService = checksumService;
        _opticalService = opticalService;
        _i18n = i18n;
        _log = logCallback ?? ((_, _) => { });
    }

    /// <summary>
    /// CHD files of the CHD folder of a MAME collection (<see cref="SortConfig.ChdDir"/>), read recursively since MAME
    /// stores each disk in the folder of its set. Its other files are not part of the sort. The files the source scan
    /// already listed (a CHD folder inside the source) are left out.
    /// </summary>
    public List<ScanEntry> ScanChdFolder(
        string chdDir,
        IReadOnlyCollection<ScanEntry> sourceEntries,
        CancellationToken cancellationToken = default,
        IProgress<SortProgress>? progress = null)
    {
        if (!Directory.Exists(chdDir))
        {
            _log(LogLevel.Warn, LocalizedMessage.Create("log.chd_dir_not_found", ("path", chdDir)));
            return [];
        }

        ReportStep(progress, LocalizedMessage.Create("progress.listing_chd", ("count", 0)));
        var sw = Stopwatch.StartNew();
        var lastReportMs = 0L;
        var listed = sourceEntries.Select(e => e.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = new List<ScanEntry>();
        var skips = new FileTreeSkips();
        foreach (var path in FileTree.EnumerateFiles(chdDir, recursive: true, skips))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!path.EndsWith(".chd", StringComparison.OrdinalIgnoreCase) || DestinationGuard.IsGuardPath(path) || !listed.Add(path))
            {
                continue;
            }

            if (sw.ElapsedMilliseconds - lastReportMs >= ProgressIntervalMs)
            {
                ReportStep(progress, LocalizedMessage.Create("progress.listing_chd", ("count", entries.Count)));
                lastReportMs = sw.ElapsedMilliseconds;
            }

            long size = 0;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (IOException)
            {
            }

            entries.Add(new ScanEntry(SourcePath: path, FileSize: size));
        }

        ReportSkips(skips);
        _log(LogLevel.Info, LocalizedMessage.Create("log.chd_dir_scanned", ("count", entries.Count), ("path", chdDir)));
        return entries;
    }

    /// <summary>
    /// Patches of the patch folder (<see cref="SortConfig.PatchDir"/>), read recursively: the files with the extension
    /// of a patch (<see cref="PatchService.Extensions"/>), and the members with such an extension of its ZIP, 7z and
    /// RAR archives. Their signature is read later, with the rest of the patch (<see cref="PatchMatcher"/>). The texts that
    /// may come with them (<see cref="PatchService.NoteExtensions"/>, at most <see cref="PatchService.MaxNoteSize"/>
    /// bytes), on the disk and in the same archives, are listed too: they are only read for a patch that gives no CRC32.
    /// In the order of their paths, then of their members.
    /// </summary>
    public PatchFolderContent ScanPatchFolder(
        string patchDir,
        CancellationToken cancellationToken = default,
        IProgress<SortProgress>? progress = null)
    {
        if (!Directory.Exists(patchDir))
        {
            _log(LogLevel.Warn, LocalizedMessage.Create("log.patch_dir_not_found", ("path", patchDir)));
            return new PatchFolderContent([], []);
        }

        ReportStep(progress, LocalizedMessage.Create("progress.listing_patches", ("count", 0)));
        var sw = Stopwatch.StartNew();
        var lastReportMs = 0L;
        var patches = new List<PatchFile>();
        var notes = new List<PatchFile>();
        var skips = new FileTreeSkips();
        foreach (var path in FileTree.EnumerateFiles(patchDir, recursive: true, skips))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DestinationGuard.IsGuardPath(path))
            {
                continue;
            }

            if (sw.ElapsedMilliseconds - lastReportMs >= ProgressIntervalMs)
            {
                ReportStep(progress, LocalizedMessage.Create("progress.listing_patches", ("count", patches.Count)));
                lastReportMs = sw.ElapsedMilliseconds;
            }

            if (PatchService.HasPatchExtension(path))
            {
                patches.Add(new PatchFile(path));
            }
            else if (PatchService.HasNoteExtension(path))
            {
                if (IsSmallNote(path))
                {
                    notes.Add(new PatchFile(path));
                }
            }
            else if (_checksumService.IsSupportedArchive(path))
            {
                try
                {
                    var members = _headerCache?.GetArchiveMembers(path, _checksumService) ?? _checksumService.GetArchiveMembers(path);
                    patches.AddRange(members
                        .Where(m => PatchService.HasPatchExtension(m.MemberName))
                        .Select(m => new PatchFile(path, m.MemberName)));
                    notes.AddRange(members
                        .Where(m => PatchService.HasNoteExtension(m.MemberName) && m.Size <= PatchService.MaxNoteSize)
                        .Select(m => new PatchFile(path, m.MemberName)));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    // An unreadable archive holds no patch the sort can read
                }
            }
        }

        ReportSkips(skips);
        patches.Sort(ComparePatchFiles);
        notes.Sort(ComparePatchFiles);
        _log(LogLevel.Info, LocalizedMessage.Create("log.patch_dir_scanned", ("count", patches.Count), ("path", patchDir)));
        return new PatchFolderContent(patches, notes);
    }

    private static int ComparePatchFiles(PatchFile a, PatchFile b)
    {
        var byPath = string.CompareOrdinal(a.SourcePath, b.SourcePath);
        return byPath != 0 ? byPath : string.CompareOrdinal(a.ArchiveMember, b.ArchiveMember);
    }

    /// <summary>Whether a text of the patch folder is small enough to be a readme (<see cref="PatchService.MaxNoteSize"/>).</summary>
    private static bool IsSmallNote(string path)
    {
        try
        {
            return new FileInfo(path).Length <= PatchService.MaxNoteSize;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a file of the source belongs to the patch folder and stays out of the collection: every file of a patch
    /// folder inside the source; only the patches of one that is the source, or holds it, whose other files are ROMs.
    /// </summary>
    private Func<string, bool> PatchFolderFilter()
    {
        if (string.IsNullOrWhiteSpace(_config.PatchDir))
        {
            return _ => false;
        }

        var patchDir = _config.PatchDir;
        var overlap = SortConfigValidator.GetPathOverlap(_config.SourceDir, patchDir, recursive: true);
        if (overlap == PathOverlap.None)
        {
            return _ => false;
        }

        var wholeFolder = overlap == PathOverlap.DestinationInsideSource;
        var isSource = overlap == PathOverlap.Identical;
        return path => (isSource || PathOrganizerService.IsInsideDirectory(path, patchDir))
                       && (wholeFolder || PatchService.HasPatchExtension(path));
    }

    /// <summary>Warns about what a listing skipped: the folders it could not read, the links to a folder it did not follow.</summary>
    private void ReportSkips(FileTreeSkips skips)
    {
        if (skips.UnreadableFolders.Count > 0)
        {
            _log(LogLevel.Warn, LocalizedMessage.Create(
                "log.unreadable_folders_skipped",
                ("count", skips.UnreadableFolders.Count),
                ("names", MameSetDependencies.FormatNames(skips.UnreadableFolders))));
        }

        if (skips.FolderLinks.Count > 0)
        {
            _log(LogLevel.Warn, LocalizedMessage.Create(
                "log.folder_links_not_followed",
                ("count", skips.FolderLinks.Count),
                ("names", MameSetDependencies.FormatNames(skips.FolderLinks))));
        }
    }

    /// <summary>Minimum time between two progress reports of a loop, in milliseconds.</summary>
    private const int ProgressIntervalMs = 40;

    /// <summary>
    /// Files of the source, without the guard files of an earlier destination nor the files of the system
    /// (<see cref="FileTree"/>). Listing a large collection on a network share takes minutes: the number of files found so
    /// far is reported, and the listing can be cancelled.
    /// </summary>
    private List<string> ListSourceFiles(string sourceDir, CancellationToken cancellationToken, IProgress<SortProgress>? progress)
    {
        ReportStep(progress, LocalizedMessage.Create("progress.listing_files"));
        if (!Directory.Exists(sourceDir))
        {
            return [];
        }

        var sw = Stopwatch.StartNew();
        var lastReportMs = 0L;
        var filePaths = new List<string>();
        var skips = new FileTreeSkips();
        var isPatchFolderFile = PatchFolderFilter();
        foreach (var path in FileTree.EnumerateFiles(sourceDir, _config.Recursive, skips))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DestinationGuard.IsGuardPath(path) || isPatchFolderFile(path))
            {
                continue;
            }

            filePaths.Add(path);
            if (sw.ElapsedMilliseconds - lastReportMs >= ProgressIntervalMs)
            {
                ReportStep(progress, LocalizedMessage.Create("progress.listing_files_count", ("count", filePaths.Count)));
                lastReportMs = sw.ElapsedMilliseconds;
            }
        }

        ReportSkips(skips);
        return filePaths;
    }

    /// <summary>Reports a step whose length is not known in advance: no count, no remaining time.</summary>
    private static void ReportStep(IProgress<SortProgress>? progress, LocalizedMessage message) =>
        progress?.Report(new SortProgress(0, 0, 0, 0, 0.0, message));

    public (List<ScanEntry> Entries, long TotalBytes) Scan(
        CancellationToken cancellationToken = default,
        IProgress<SortProgress>? progress = null)
    {
        var sourceDir = _config.SourceDir;
        _log(LogLevel.Info, LocalizedMessage.Create("log.scanning_files"));

        var filePaths = ListSourceFiles(sourceDir, cancellationToken, progress);

        if (filePaths.Count == 0)
        {
            _log(LogLevel.Warn, LocalizedMessage.Create("log.no_files_found"));
            return ([], 0);
        }

        // 1. Detection of optical descriptors and linking of their companion files on disk
        var claimedCompanions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var descriptorCompanions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var linkingReported = false;

        foreach (var fp in filePaths)
        {
            if (_opticalService.IsDescriptor(fp))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!linkingReported)
                {
                    ReportStep(progress, LocalizedMessage.Create("progress.linking_tracks"));
                    linkingReported = true;
                }

                var companions = _opticalService.ResolveCompanionFilesOnDisk(fp);
                if (companions.Count > 0)
                {
                    descriptorCompanions[fp] = companions;
                    foreach (var comp in companions)
                    {
                        claimedCompanions.Add(comp);
                    }
                }
            }
        }

        var scanEntries = new List<ScanEntry>();
        var sw = Stopwatch.StartNew();
        var lastReportMs = 0L;
        var scannedFiles = 0;
        var totalFilesToScan = filePaths.Count;

        foreach (var fp in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scannedFiles++;

            if (progress is not null && sw.ElapsedMilliseconds - lastReportMs >= ProgressIntervalMs)
            {
                var etaSeconds = EtaEstimator.EstimateSeconds(sw.Elapsed.TotalSeconds, scannedFiles, totalFilesToScan);
                progress.Report(new SortProgress(
                    ProcessedCount: scannedFiles,
                    TotalCount: totalFilesToScan,
                    ProcessedBytes: 0,
                    TotalBytes: 0,
                    EtaSeconds: etaSeconds,
                    Message: LocalizedMessage.Create("progress.scanning_file", ("name", Path.GetFileName(fp)))));
                lastReportMs = sw.ElapsedMilliseconds;
            }

            // Ignore top-level companion files because they are attached to their descriptor
            if (claimedCompanions.Contains(fp))
            {
                continue;
            }

            if (_config.Filters.ScanArchives && _checksumService.IsSupportedArchive(fp))
            {
                try
                {
                    var members = _headerCache?.GetArchiveMembers(fp, _checksumService) ?? _checksumService.GetArchiveMembers(fp);
                    if (members.Count > 0)
                    {
                        long archiveSize = 0;
                        try
                        {
                            archiveSize = new FileInfo(fp).Length;
                        }
                        catch (IOException)
                        {
                        }

                        var claimedZipMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var descZipCompanions = new Dictionary<string, List<ArchiveMember>>(StringComparer.OrdinalIgnoreCase);
                        var descMembers = members.Where(m => _opticalService.IsDescriptor(m.MemberName)).ToList();

                        if (descMembers.Count > 0)
                        {
                            // Members by full name: several of them may share a file name in different folders (the
                            // ROMs of the clones of a merged MAME set, for instance)
                            var memberMap = new Dictionary<string, ArchiveMember>(StringComparer.Ordinal);
                            foreach (var member in members)
                            {
                                memberMap.TryAdd(member.MemberName, member);
                            }

                            var memberNames = members.Select(m => m.MemberName).ToHashSet(StringComparer.Ordinal);
                            foreach (var dm in descMembers)
                            {
                                var resolved = _opticalService.ResolveCompanionArchiveMembers(
                                    dm.MemberName,
                                    members.Select(m => m.MemberName),
                                    entryName => memberNames.Contains(entryName)
                                        ? _checksumService.OpenArchiveMember(fp, entryName)
                                        : Stream.Null);

                                if (resolved.Count > 0)
                                {
                                    var compList = new List<ArchiveMember>();
                                    foreach (var rName in resolved)
                                    {
                                        if (memberMap.TryGetValue(rName, out var am))
                                        {
                                            compList.Add(am);
                                            claimedZipMembers.Add(am.MemberName);
                                        }
                                    }
                                    descZipCompanions[dm.MemberName] = compList;
                                }
                            }
                        }

                        foreach (var m in members)
                        {
                            if (claimedZipMembers.Contains(m.MemberName))
                            {
                                continue;
                            }

                            var related = new List<ScanEntry>();
                            if (descZipCompanions.TryGetValue(m.MemberName, out var compList))
                            {
                                foreach (var cMember in compList)
                                {
                                    related.Add(new ScanEntry(
                                        SourcePath: fp,
                                        FileSize: cMember.Size,
                                        ArchivePath: fp,
                                        ArchiveMember: cMember.MemberName,
                                        PrecomputedCrc: cMember.Crc32,
                                        ArchiveTotalMemberCount: members.Count,
                                        ArchiveFileSize: archiveSize));
                                }
                            }

                            scanEntries.Add(new ScanEntry(
                                SourcePath: fp,
                                FileSize: m.Size,
                                ArchivePath: fp,
                                ArchiveMember: m.MemberName,
                                PrecomputedCrc: m.Crc32,
                                RelatedEntries: related,
                                ArchiveTotalMemberCount: members.Count,
                                ArchiveFileSize: archiveSize));
                        }
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    _log(LogLevel.Warn, LocalizedMessage.Create("log.archive_error", ("name", Path.GetFileName(fp)), ("error", ex.Message)));
                }
            }

            long sz = 0;
            try
            {
                sz = new FileInfo(fp).Length;
            }
            catch (IOException)
            {
            }

            var relatedFiles = new List<ScanEntry>();
            if (descriptorCompanions.TryGetValue(fp, out var compPaths))
            {
                foreach (var cPath in compPaths)
                {
                    long cSz = 0;
                    try
                    {
                        cSz = new FileInfo(cPath).Length;
                    }
                    catch (IOException)
                    {
                    }
                    relatedFiles.Add(new ScanEntry(SourcePath: cPath, FileSize: cSz));
                }
            }

            scanEntries.Add(new ScanEntry(SourcePath: fp, FileSize: sz, RelatedEntries: relatedFiles));
        }

        if (scanEntries.Count == 0)
        {
            _log(LogLevel.Warn, LocalizedMessage.Create("log.no_files_found"));
            return ([], 0);
        }

        var totalBytes = scanEntries.Sum(e => e.FileSize + e.RelatedEntries.Sum(r => r.FileSize));
        var mbStr = (totalBytes / (1024.0 * 1024.0)).ToString("F2", CultureInfo.InvariantCulture);
        _log(LogLevel.Info, LocalizedMessage.Create("log.files_scanned_count", ("count", scanEntries.Count), ("mb", mbStr)));
        return (scanEntries, totalBytes);
    }
}
