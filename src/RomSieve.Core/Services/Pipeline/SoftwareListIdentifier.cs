using System.Diagnostics;
using System.IO;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Identification of the software of several MAME software lists in one run (a folder of DATs, or an output of MAME
/// holding several lists, <see cref="DatCatalog.SoftwareLists"/>). The short name of a software is only unique in its
/// list: each archive belongs to the list named by the first folder of its path under the source
/// (<c>megadriv/sonic.zip</c>), or by the source itself when it is the folder of a list, and is identified by
/// <see cref="MameSetIdentifier"/> in the catalog of that list only. A file outside the folder of a list of the DATs is
/// no software. The lists present in the source (<see cref="PresentLists"/>) are those whose folder holds a file.
/// </summary>
public sealed class SoftwareListIdentifier
{
    private readonly SortConfig _config;
    private readonly DatCatalog _catalog;
    private readonly IReadOnlyDictionary<string, DatCatalog> _lists;
    private readonly IChecksumService _checksumService;
    private readonly IMameDescriptionParser _parser;
    private readonly IRomFilterService _filterService;
    private readonly ILocalizationService _i18n;
    private readonly Action<LogLevel, LocalizedMessage> _log;
    private readonly IHeaderCache? _headerCache;

    /// <summary>DAT entries found in the sets of every list, compared by reference.</summary>
    public HashSet<DatRomEntry> MatchedDatEntries { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>Catalogs of the lists whose folder holds a file of the source, in the order of the source.</summary>
    public List<DatCatalog> PresentLists { get; } = [];

    public SoftwareListIdentifier(
        SortConfig config,
        DatCatalog catalog,
        IChecksumService checksumService,
        IMameDescriptionParser parser,
        IRomFilterService filterService,
        ILocalizationService i18n,
        Action<LogLevel, LocalizedMessage> log,
        IHeaderCache? headerCache = null)
    {
        _config = config;
        _catalog = catalog;
        _lists = catalog.SoftwareLists ?? throw new ArgumentException("The catalog gathers no software lists.", nameof(catalog));
        _checksumService = checksumService;
        _parser = parser;
        _filterService = filterService;
        _i18n = i18n;
        _log = log;
        _headerCache = headerCache;
    }

    private MameSetIdentifier CreateIdentifier(DatCatalog list, int offset = 0, int? total = null, Stopwatch? clock = null) =>
        new(_config, list, _checksumService, _parser, _filterService, _i18n, _log, _headerCache)
        {
            ProgressOffset = offset,
            ProgressTotal = total,
            ProgressClock = clock,
            ReportsSummary = false
        };

    // Archive or file a scan entry stands for: one item each
    private static string UnitPath(ScanEntry entry) => entry.ArchivePath ?? entry.SourcePath;

    /// <summary>
    /// Folder of the list of a file of the source: the first folder of its path under the source, or the source itself
    /// for a file at its root (the source is then the folder of a list).
    /// </summary>
    public static string ListFolderOf(string sourceDir, string path)
    {
        var relative = Path.GetRelativePath(sourceDir, path);
        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[0] : Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceDir));
    }

    public List<ProcessItem> IdentifyAll(
        IReadOnlyList<ScanEntry> scanEntries,
        CancellationToken cancellationToken = default,
        IProgress<SortProgress>? progress = null)
    {
        var entriesByList = new Dictionary<string, List<ScanEntry>>(StringComparer.OrdinalIgnoreCase);
        var outside = new List<(string Path, string Folder)>();
        var outsidePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in scanEntries)
        {
            var folder = ListFolderOf(_config.SourceDir, UnitPath(entry));
            if (_lists.TryGetValue(folder, out var list))
            {
                if (!entriesByList.TryGetValue(list.Header.Name, out var entries))
                {
                    entries = [];
                    entriesByList[list.Header.Name] = entries;
                    PresentLists.Add(list);
                }
                entries.Add(entry);
            }
            else if (outsidePaths.Add(UnitPath(entry)))
            {
                outside.Add((UnitPath(entry), folder));
            }
        }

        // The progress counts the archives and files of the whole run, list after list
        var total = entriesByList.Values.Sum(entries => entries.Select(UnitPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var clock = Stopwatch.StartNew();
        var offset = 0;
        var items = new List<ProcessItem>();
        var sets = new List<MameSetInfo>();
        var attachedDisks = 0;
        foreach (var list in PresentLists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = entriesByList[list.Header.Name];
            var identifier = CreateIdentifier(list, offset, total, clock);
            items.AddRange(identifier.IdentifyAll(entries, cancellationToken, progress));
            MatchedDatEntries.UnionWith(identifier.MatchedDatEntries);
            sets.AddRange(identifier.LastIdentification.Sets);
            attachedDisks += identifier.LastIdentification.AttachedDisks;
            offset += entries.Select(UnitPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        }

        // A file outside the folder of a list of the DATs is no software
        var anyIdentifier = CreateIdentifier(_catalog);
        foreach (var (path, folder) in outside)
        {
            var reason = string.Equals(Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Path.TrimEndingDirectorySeparator(_config.SourceDir), StringComparison.OrdinalIgnoreCase)
                ? LocalizedMessage.Create("reason.softlist_outside_list_folder")
                : LocalizedMessage.Create("reason.softlist_unknown_list", ("folder", folder));
            items.Add(anyIdentifier.CreateUnmatchedItem(path, reason, isWholeArchive: _checksumService.IsSupportedArchive(path)));
        }

        _log(LogLevel.Info, LocalizedMessage.Create(
            "log.mame_software_lists_present",
            ("present", PresentLists.Count),
            ("lists", _lists.Count)));
        if (outside.Count > 0)
        {
            _log(LogLevel.Warn, LocalizedMessage.Create(
                "log.softlist_files_outside_lists",
                ("count", outside.Count),
                ("examples", string.Join(", ", outside.Take(3).Select(o => Path.GetRelativePath(_config.SourceDir, o.Path))) + (outside.Count > 3 ? ", …" : string.Empty))));
        }

        MameSetIdentifier.ReportIdentification(sets, attachedDisks, "log.mame_software_identified", _log);
        return items;
    }

    /// <summary>
    /// Sets of the software of every list of the DATs, without any file (<see cref="MameSetIdentifier.IdentifyCatalog"/>):
    /// those of a complete collection of all the lists.
    /// </summary>
    public List<ProcessItem> IdentifyCatalog(CancellationToken cancellationToken = default)
    {
        var items = new List<ProcessItem>();
        foreach (var list in _lists.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.AddRange(CreateIdentifier(list).IdentifyCatalog(cancellationToken));
            PresentLists.Add(list);
        }

        return items;
    }
}
