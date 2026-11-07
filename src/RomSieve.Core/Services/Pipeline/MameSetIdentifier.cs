using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Identification of MAME sets, which replaces the file by file classification when the DAT is a MAME DAT: an archive
/// (ZIP, 7z) is one item, the set of the machine whose short name it bears (<c>sf2.zip</c>), or failing that of the
/// machine its ROMs belong to. Its members are compared with the ROMs of the machine by size and CRC32, read from the
/// archive headers without decompression. A CHD (<c>sf2/disk.chd</c>) joins the set of its machine as a companion item,
/// placed in the folder named after the set. The ROMs of the machine are expected in its own archive (non-merged sets):
/// a set that lacks some is incomplete (<see cref="MameSetInfo"/>), and <see cref="ReleaseSelector"/> excludes it when
/// only complete sets are wanted.
/// </summary>
public sealed class MameSetIdentifier
{
    private readonly SortConfig _config;
    private readonly ArchiveContentChecker _archiveChecker;
    private readonly DatCatalog _catalog;
    private readonly IChecksumService _checksumService;
    private readonly IMameDescriptionParser _parser;
    private readonly IRomFilterService _filterService;
    private readonly ILocalizationService _i18n;
    private readonly Action<LogLevel, LocalizedMessage> _log;
    private readonly IHeaderCache? _headerCache;
    private readonly MameCategories? _categories;
    private readonly MameRatings? _ratings;

    // Whether the DAT links clones to their parents (mame -listxml): the games are then grouped by parent set
    private readonly bool _hasParentCloneRelations;

    /// <summary>DAT entries found in the sets, compared by reference (for the statistics and the Miss List).</summary>
    public HashSet<DatRomEntry> MatchedDatEntries { get; } = new(ReferenceEqualityComparer.Instance);

    public MameSetIdentifier(
        SortConfig config,
        DatCatalog catalog,
        IChecksumService checksumService,
        IMameDescriptionParser parser,
        IRomFilterService filterService,
        ILocalizationService i18n,
        Action<LogLevel, LocalizedMessage> log,
        IHeaderCache? headerCache = null,
        MameCategories? categories = null,
        MameRatings? ratings = null)
    {
        _headerCache = headerCache;
        _categories = categories;
        _ratings = ratings;
        _config = config;
        _archiveChecker = new ArchiveContentChecker(checksumService, headerCache);
        _catalog = catalog;
        _checksumService = checksumService;
        _parser = parser;
        _filterService = filterService;
        _i18n = i18n;
        _log = log;
        _hasParentCloneRelations = catalog.CloneToParent.Count > 0;
        _softwareList = GetSetConvention(config, catalog) == NamingConvention.MameSoftwareList
            ? string.IsNullOrWhiteSpace(catalog.Header.Name) ? string.Empty : catalog.Header.Name.Trim()
            : null;
    }

    // Name of the software list the run sorts (the software then go into its folder); null for MAME machines
    private readonly string? _softwareList;

    /// <summary>
    /// Archives and CHDs identified before this one, when a run identifies its software list by list
    /// (<see cref="SoftwareListIdentifier"/>), and the total of the run: the progress counts the whole run.
    /// </summary>
    internal int ProgressOffset { get; init; }

    /// <inheritdoc cref="ProgressOffset"/>
    internal int? ProgressTotal { get; init; }

    /// <inheritdoc cref="ProgressOffset"/>
    internal Stopwatch? ProgressClock { get; init; }

    /// <summary>
    /// Whether <see cref="IdentifyAll"/> writes its summary to the log (the sets identified, the split sets...); a run
    /// that identifies its software list by list writes them once, for all of them.
    /// </summary>
    internal bool ReportsSummary { get; init; } = true;

    /// <summary>Sets of the last <see cref="IdentifyAll"/>, compared with their machine or software, and the disks attached.</summary>
    internal (IReadOnlyList<MameSetInfo> Sets, int AttachedDisks) LastIdentification { get; private set; } = ([], 0);

    /// <summary>
    /// Whether a run sorts MAME sets: the DAT is a MAME DAT, or the MAME convention was chosen for a DAT whose
    /// convention is not recognized.
    /// </summary>
    public static bool IsMameRun(SortConfig config, [NotNullWhen(true)] DatCatalog? catalog) =>
        GetSetConvention(config, catalog) == NamingConvention.Mame;

    /// <summary>
    /// Convention of the whole archives a run sorts (<see cref="NamingConvention.Mame"/>,
    /// <see cref="NamingConvention.MameSoftwareList"/>): that of the DAT, or the one chosen for a DAT whose convention is
    /// not recognized; <see cref="NamingConvention.Auto"/> when the run sorts files, always with a lot of DATs, which never
    /// holds a MAME DAT.
    /// </summary>
    public static NamingConvention GetSetConvention(SortConfig config, DatCatalog? catalog)
    {
        if (catalog is null || catalog.Dats is not null)
        {
            return NamingConvention.Auto;
        }

        var datConvention = catalog.DetectConvention();
        if (datConvention.IsMameSets())
        {
            return datConvention;
        }

        return datConvention == NamingConvention.Auto && config.NamingConvention.IsMameSets()
            ? config.NamingConvention
            : NamingConvention.Auto;
    }

    // An archive of the source and the members it holds
    private sealed record SourceArchive(string Path, IReadOnlyList<ArchiveMember> Members);

    // A set being built: its machine, its item, and the CHDs attached to it
    private sealed class SetCandidate(DatGame game, ProcessItem item, IReadOnlyList<ArchiveMember> members)
    {
        public DatGame Game { get; } = game;
        public ProcessItem Item { get; } = item;
        public IReadOnlyList<ArchiveMember> Members { get; } = members;
        public List<DatRomEntry> AttachedDisks { get; } = [];
    }

    public List<ProcessItem> IdentifyAll(
        IReadOnlyList<ScanEntry> scanEntries,
        CancellationToken cancellationToken = default,
        IProgress<SortProgress>? progress = null)
    {
        var (archives, chdPaths, otherPaths) = GroupScanEntries(scanEntries);
        var items = new List<ProcessItem>();
        var sets = new List<SetCandidate>();
        var setsByGame = new Dictionary<string, List<SetCandidate>>(StringComparer.OrdinalIgnoreCase);
        var total = archives.Count + chdPaths.Count;
        var done = 0;
        var sw = ProgressClock ?? Stopwatch.StartNew();

        // 1. Archives: one set each
        foreach (var archive in archives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(progress, sw, ProgressOffset + done++, ProgressTotal ?? total, archive.Path);

            // A damaged archive is not a set: its ROMs are not present, and a ROM manager has to repair it
            if (_config.VerifyArchiveContents && _archiveChecker.FindArchiveDamage(archive.Path, cancellationToken) is { } damage)
            {
                items.Add(CreateUnmatchedItem(archive.Path, damage));
                continue;
            }

            var game = FindGame(archive);
            if (game is null)
            {
                items.Add(CreateUnmatchedItem(archive.Path, LocalizedMessage.Create("reason.unmatched_not_in_dat")));
                continue;
            }

            var item = CreateSetItem(game, archive.Path, Path.GetExtension(archive.Path));
            var set = new SetCandidate(game, item, archive.Members);
            sets.Add(set);
            AddToIndex(setsByGame, game.Name, set);
            items.Add(item);
        }

        // 2. CHDs: attached to the set of their machine, which may only exist through them
        var attachedDisks = 0;
        foreach (var chdPath in chdPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(progress, sw, ProgressOffset + done++, ProgressTotal ?? total, chdPath);

            ChdInfo chd;
            try
            {
                chd = _headerCache?.ReadChdInfo(chdPath, _checksumService) ?? _checksumService.ReadChdInfo(chdPath);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                items.Add(CreateUnmatchedItem(chdPath, LocalizedMessage.Create("reason.chd_invalid", ("error", LocalizedMessage.Verbatim(ex.Message))), isWholeArchive: false));
                continue;
            }

            var disk = ChooseDisk(chdPath, chd, setsByGame);
            if (disk is null)
            {
                items.Add(CreateUnmatchedItem(chdPath, LocalizedMessage.Create("reason.unmatched_not_in_dat"), isWholeArchive: false));
                continue;
            }

            if (_catalog.GetGameForEntry(disk) is not { } game)
            {
                items.Add(CreateUnmatchedItem(chdPath, LocalizedMessage.Create("reason.unmatched_not_in_dat"), isWholeArchive: false));
                continue;
            }

            // A clone without an archive of its own (merged sets) has its disks go with the archive of its parent,
            // still into the folder of the clone
            var diskItem = CreateDiskItem(game, disk, chdPath, chd);
            var owner = setsByGame.TryGetValue(game.Name, out var gameSets)
                ? gameSets[0]
                : setsByGame.TryGetValue(_catalog.GetCanonicalGameName(game.Name), out var parentSets) && parentSets[0].Members.Count > 0
                    ? parentSets[0]
                    : null;
            if (owner is not null)
            {
                owner.Item.RelatedItems.Add(diskItem);
                owner.AttachedDisks.Add(disk);
            }
            else
            {
                // A machine of which the source only holds disks: the first one stands for its set
                var set = new SetCandidate(game, diskItem, []);
                set.AttachedDisks.Add(disk);
                sets.Add(set);
                AddToIndex(setsByGame, game.Name, set);
                items.Add(diskItem);
            }

            attachedDisks++;
        }

        // 3. Comparison of each set with its machine, then the filters: seconds on a full collection, named so that
        // the progress bar does not seem stuck on the last CHD
        ReportStep(progress, LocalizedMessage.Create("progress.checking_sets"));
        CheckSets(sets, setsByGame);

        // 4. The ROMs and disks a machine shares with another set are present when that set holds them
        MarkSharedEntriesPresent(setsByGame);

        // 5. Loose files are not MAME sets
        foreach (var path in otherPaths)
        {
            items.Add(CreateUnmatchedItem(path, LocalizedMessage.Create("reason.mame_not_a_set"), isWholeArchive: false));
        }

        var identified = sets.Select(s => s.Item.MameSet).OfType<MameSetInfo>().ToList();
        LastIdentification = (identified, attachedDisks);
        if (ReportsSummary)
        {
            ReportIdentification(identified, attachedDisks, _softwareList is null ? "log.mame_sets_identified" : "log.mame_software_identified", _log);
        }

        return items;
    }

    /// <summary>
    /// Writes to the log the sets identified (<paramref name="key"/>: their number, the incomplete ones, the disks
    /// attached), then the sets that are not non-merged, once for all.
    /// </summary>
    internal static void ReportIdentification(IReadOnlyList<MameSetInfo> sets, int attachedDisks, string key, Action<LogLevel, LocalizedMessage> log)
    {
        log(LogLevel.Ok, LocalizedMessage.Create(
            key,
            ("sets", sets.Count),
            ("disks", attachedDisks),
            ("incomplete", sets.Count(s => !s.IsComplete))));
        ReportSetLayouts(sets, log);
    }

    /// <summary>
    /// Sets of the machines of the DAT alone, without any file: those of a complete collection of the sets the DAT
    /// describes, each holding the ROMs and disks the DAT lists for its machine. A machine without ROM nor disk to dump
    /// has no set. Each set is compared with its machine and filtered like an archive of that collection, so the release
    /// selection decides on the catalog what it would decide on it: a set needs the sets of the devices whose ROMs its
    /// machine does not list (<c>device_ref</c>), never its BIOS, whose ROMs it lists. The size of a set is that of its
    /// ROMs, before compression, and its disks count for nothing: the DAT does not give their size.
    /// </summary>
    public List<ProcessItem> IdentifyCatalog(CancellationToken cancellationToken = default)
    {
        var sets = new List<SetCandidate>();
        var setsByGame = new Dictionary<string, List<SetCandidate>>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in _catalog.Games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = game.Name + ".zip";
            var roms = ExpectedRoms(game).ToList();
            var members = roms.Select(r => new ArchiveMember(path, r.Name, r.Size, r.Crc)).ToList();
            var disks = game.Roms.Where(r => r.IsDisk && r.Status != DatRomStatus.NoDump).ToList();
            if (members.Count == 0 && disks.Count == 0)
            {
                continue;
            }

            var size = roms.Sum(r => r.Size);
            var item = new ProcessItem
            {
                SourcePath = path,
                FileSize = size,
                Parsed = ParseMachine(game, path, size),
                TargetFilename = InListFolder(game, path),
                DatGameName = game.Name,
                IsMultidisk = false,
                IsWholeArchive = true
            };
            var set = new SetCandidate(game, item, members);
            set.AttachedDisks.AddRange(disks);
            sets.Add(set);
            AddToIndex(setsByGame, game.Name, set);
        }

        CheckSets(sets, setsByGame);
        return sets.Select(s => s.Item).ToList();
    }

    // Compares each set with its machine, then applies the filters
    private void CheckSets(List<SetCandidate> sets, Dictionary<string, List<SetCandidate>> setsByGame)
    {
        foreach (var set in sets)
        {
            set.Item.MameSet = CompareWithMachine(set, setsByGame);
            ApplyFilters(set.Item);
        }
    }

    /// <summary>
    /// Reports the sets that are not non-merged, once for all. RomSieve sorts archives as they are and never rebuilds
    /// a set: a merged set is sorted with its clones (information), while a split clone, which does not run by itself,
    /// and a set relying on an absent BIOS or on absent devices are warned about.
    /// </summary>
    private static void ReportSetLayouts(IReadOnlyList<MameSetInfo> sets, Action<LogLevel, LocalizedMessage> log)
    {
        static string Examples(IReadOnlyList<string> names) =>
            string.Join(", ", names.Take(3)) + (names.Count > 3 ? ", …" : string.Empty);

        void Report(LogLevel level, string key, Func<MameSetInfo, bool> predicate)
        {
            var names = sets.Where(predicate).Select(s => s.SetKey).ToList();
            if (names.Count > 0)
            {
                log(level, LocalizedMessage.Create(key, ("count", names.Count), ("examples", Examples(names))));
            }
        }

        Report(LogLevel.Warn, "log.mame_split_sets", s => s.IsSplit);
        Report(LogLevel.Warn, "log.mame_missing_bios", s => s.RequiresBios);

        var lackingDevices = sets.Where(s => s.MissingDevices.Count > 0).Select(s => s.SetKey).ToList();
        if (lackingDevices.Count > 0)
        {
            var devices = sets.SelectMany(s => s.MissingDevices).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            log(LogLevel.Warn, LocalizedMessage.Create(
                "log.mame_missing_devices",
                ("count", lackingDevices.Count),
                ("examples", Examples(lackingDevices)),
                ("devices", Examples(devices))));
        }

        Report(LogLevel.Info, "log.mame_merged_sets", s => s.IsMerged);
    }

    /// <summary>
    /// For the statistics and the Miss List: a ROM or a disk of a machine is present when a set of its <c>romof</c> chain
    /// (the parent, the BIOS) holds it, where MAME looks for it too: merged and split collections store there what a
    /// machine shares with its parent (<c>merge</c>), and merged CHD collections keep the disks of the clones in the
    /// folder of the parent, sometimes without <c>merge</c>. Whether the set of the machine runs by itself is told apart
    /// (<see cref="MameSetInfo"/>).
    /// </summary>
    private void MarkSharedEntriesPresent(Dictionary<string, List<SetCandidate>> setsByGame)
    {
        var romKeysByGame = new Dictionary<string, HashSet<(long Size, string Crc)>>(StringComparer.OrdinalIgnoreCase);
        var diskSha1sByGame = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, gameSets) in setsByGame)
        {
            romKeysByGame[name] = MemberKeys(gameSets.SelectMany(s => s.Members));
            diskSha1sByGame[name] = gameSets
                .SelectMany(s => s.AttachedDisks)
                .Select(d => d.Sha1)
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var game in _catalog.Games)
        {
            if (string.IsNullOrEmpty(game.RomOf))
            {
                continue;
            }

            foreach (var entry in game.Roms)
            {
                if (entry.Status == DatRomStatus.NoDump || MatchedDatEntries.Contains(entry))
                {
                    continue;
                }

                if (Ancestors(game).Any(ancestor => entry.IsDisk
                        ? entry.Sha1 is { } sha1 && diskSha1sByGame.TryGetValue(ancestor, out var sha1s) && sha1s.Contains(sha1)
                        : romKeysByGame.TryGetValue(ancestor, out var keys) && keys.Contains((entry.Size, entry.Crc.ToLowerInvariant()))))
                {
                    MatchedDatEntries.Add(entry);
                }
            }
        }
    }

    // Sets a machine takes ROMs from: its romof chain (parent, BIOS)
    private IEnumerable<string> Ancestors(DatGame game)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { game.Name };
        var current = game;
        while (!string.IsNullOrEmpty(current.RomOf) &&
               _catalog.GameByName.TryGetValue(current.RomOf, out var next) &&
               visited.Add(next.Name))
        {
            yield return next.Name;
            current = next;
        }
    }

    private static void AddToIndex(Dictionary<string, List<SetCandidate>> index, string key, SetCandidate set)
    {
        if (!index.TryGetValue(key, out var list))
        {
            list = [];
            index[key] = list;
        }
        list.Add(set);
    }

    // Archives with their members (the scan lists the members when it inspects archives, otherwise they are read
    // here), CHD files, and the other files, in the scan order
    private (List<SourceArchive> Archives, List<string> Chds, List<string> Others) GroupScanEntries(IReadOnlyList<ScanEntry> scanEntries)
    {
        var archives = new List<SourceArchive>();
        var membersByArchive = new Dictionary<string, List<ArchiveMember>>(StringComparer.OrdinalIgnoreCase);
        var chds = new List<string>();
        var others = new List<string>();

        void AddMember(ScanEntry entry)
        {
            var archivePath = entry.ArchivePath!;
            if (!membersByArchive.TryGetValue(archivePath, out var members))
            {
                members = [];
                membersByArchive[archivePath] = members;
                archives.Add(new SourceArchive(archivePath, members));
            }
            members.Add(new ArchiveMember(archivePath, entry.ArchiveMember!, entry.FileSize, entry.PrecomputedCrc ?? string.Empty));
        }

        foreach (var entry in scanEntries)
        {
            if (!string.IsNullOrEmpty(entry.ArchivePath) && !string.IsNullOrEmpty(entry.ArchiveMember))
            {
                // The scan puts the tracks of a descriptor apart: they are members of the archive all the same
                AddMember(entry);
                foreach (var related in entry.RelatedEntries.Where(r => !string.IsNullOrEmpty(r.ArchiveMember)))
                {
                    AddMember(related);
                }
                continue;
            }

            var path = entry.SourcePath;
            if (string.Equals(Path.GetExtension(path), ".chd", StringComparison.OrdinalIgnoreCase))
            {
                chds.Add(path);
            }
            else if (_checksumService.IsSupportedArchive(path) && TryReadMembers(path) is { } members)
            {
                membersByArchive[path] = [.. members];
                archives.Add(new SourceArchive(path, members));
            }
            else
            {
                others.Add(path);
            }

            others.AddRange(entry.RelatedEntries.Select(r => r.SourcePath));
        }

        return (archives, chds, others);
    }

    // Members of an archive the scan did not open (archive inspection disabled, or an archive without members)
    private IReadOnlyList<ArchiveMember>? TryReadMembers(string path)
    {
        try
        {
            return _headerCache?.GetArchiveMembers(path, _checksumService) ?? _checksumService.GetArchiveMembers(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            _log(LogLevel.Warn, LocalizedMessage.Create("log.archive_error", ("name", Path.GetFileName(path)), ("error", ex.Message)));
            return null;
        }
    }

    /// <summary>
    /// Machine of an archive: the one whose short name the archive bears, otherwise the machine of the DAT that holds the
    /// most members of the archive (then the fewest ROMs missing, a parent, the DAT order).
    /// </summary>
    private DatGame? FindGame(SourceArchive archive)
    {
        if (_catalog.GameByName.TryGetValue(Path.GetFileNameWithoutExtension(archive.Path), out var byName))
        {
            return byName;
        }

        var matchedByGame = new Dictionary<DatGame, int>(ReferenceEqualityComparer.Instance);
        foreach (var member in archive.Members)
        {
            var games = _catalog.FindAllMatches(member.Size, member.Crc32)
                .Select(_catalog.GetGameForEntry)
                .OfType<DatGame>()
                .Distinct<DatGame>(ReferenceEqualityComparer.Instance);
            foreach (var game in games)
            {
                matchedByGame[game] = matchedByGame.GetValueOrDefault(game) + 1;
            }
        }

        if (matchedByGame.Count == 0)
        {
            return null;
        }

        if (_gameOrder is null)
        {
            _gameOrder = new Dictionary<DatGame, int>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < _catalog.Games.Count; i++)
            {
                _gameOrder[_catalog.Games[i]] = i;
            }
        }

        return matchedByGame
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => ExpectedRoms(kv.Key).Count() - kv.Value)
            .ThenByDescending(kv => _catalog.IsParentGame(kv.Key))
            .ThenBy(kv => _gameOrder.GetValueOrDefault(kv.Key, int.MaxValue))
            .First().Key;
    }

    // Position of each machine in the DAT, the last tie-breaker of an archive identified by its ROMs
    private Dictionary<DatGame, int>? _gameOrder;

    // ROMs MAME needs in the set of a machine (never-dumped ROMs have no data to look for)
    private static IEnumerable<DatRomEntry> ExpectedRoms(DatGame game) =>
        game.Roms.Where(r => !r.IsDisk && r.Status != DatRomStatus.NoDump);

    /// <summary>
    /// Disk entry of a CHD (by the SHA-1 of its header), among the machines that list it: the machine named like the folder
    /// of the CHD, then a machine of the family of that folder (merged CHDs: a clone's disk in the folder of its parent,
    /// while another game may list the same disk), then a machine whose set is in the source, then a parent, then the DAT
    /// order.
    /// </summary>
    private DatRomEntry? ChooseDisk(string chdPath, ChdInfo chd, Dictionary<string, List<SetCandidate>> setsByGame)
    {
        var disks = _catalog.FindAllMatches(0, sha1: chd.Sha1).Where(e => e.IsDisk).ToList();
        if (disks.Count == 0)
        {
            return null;
        }

        var folder = Path.GetFileName(Path.GetDirectoryName(chdPath));
        var folderFamily = folder is { Length: > 0 } && _catalog.GameByName.ContainsKey(folder)
            ? _catalog.GetCanonicalGameName(folder)
            : null;
        return disks.FirstOrDefault(d => string.Equals(d.GameName, folder, StringComparison.OrdinalIgnoreCase))
               ?? disks.FirstOrDefault(d => folderFamily is not null && d.GameName is { } name
                                            && string.Equals(_catalog.GetCanonicalGameName(name), folderFamily, StringComparison.OrdinalIgnoreCase))
               ?? disks.FirstOrDefault(d => d.GameName is { } name && setsByGame.ContainsKey(name))
               ?? disks.FirstOrDefault(d => _catalog.GetGameForEntry(d) is { } g && _catalog.IsParentGame(g))
               ?? disks[0];
    }

    private static HashSet<(long Size, string Crc)> MemberKeys(IEnumerable<ArchiveMember> members) =>
        members.Select(m => (m.Size, Crc: m.Crc32.ToLowerInvariant())).ToHashSet();

    private MameSetInfo CompareWithMachine(SetCandidate set, Dictionary<string, List<SetCandidate>> setsByGame)
    {
        var game = set.Game;
        var family = GetFamily(game);
        var memberKeys = MemberKeys(set.Members);

        // The ROMs a machine shares with its BIOS (merge) may be in the archive of the BIOS rather than in its own
        // (merged and split sets): MAME finds them there, provided the BIOS is sorted too
        var bios = FindBios(game);
        var biosSets = bios is not null && setsByGame.TryGetValue(bios.Name, out var sets) ? sets : [];
        var biosKeys = MemberKeys(biosSets.SelectMany(s => s.Members));
        var biosDiskSha1s = biosSets
            .SelectMany(s => s.AttachedDisks)
            .Select(d => d.Sha1)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usesBios = false;

        var missingRoms = new List<DatRomEntry>();
        foreach (var rom in ExpectedRoms(game))
        {
            var key = (rom.Size, rom.Crc.ToLowerInvariant());
            if (memberKeys.Contains(key))
            {
                MatchedDatEntries.Add(rom);
            }
            else if (!string.IsNullOrEmpty(rom.Merge) && biosKeys.Contains(key))
            {
                MatchedDatEntries.Add(rom);
                usesBios = true;
            }
            else
            {
                missingRoms.Add(rom);
            }
        }

        // The ROMs of its clones that a merged set holds are present too, but not the ROMs a clone shares with this set
        // (merge): the set of the clone must hold them. A ROM shared with another set (a BIOS) is only present for this
        // machine: the other set may be missing.
        var unknownMembers = 0;
        var clonesHeld = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in set.Members)
        {
            var familyEntries = _catalog.FindAllMatches(member.Size, member.Crc32)
                .Where(e => !e.IsDisk && e.GameName is { } name && family.Contains(name))
                .ToList();
            if (familyEntries.Count == 0)
            {
                unknownMembers++;
            }

            var present = familyEntries
                .Where(e => string.Equals(e.GameName, game.Name, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(e.Merge))
                .ToList();
            MatchedDatEntries.UnionWith(present);

            // An own ROM of a clone found in this set, which is not one of the machine: the set is merged
            if (!present.Any(e => string.Equals(e.GameName, game.Name, StringComparison.OrdinalIgnoreCase)))
            {
                clonesHeld.UnionWith(present.Select(e => e.GameName).OfType<string>());
            }
        }

        // A disk the machine shares with its BIOS (merge) is found in the folder of the BIOS, as its ROMs in its archive
        var missingDisks = new List<DatRomEntry>();
        foreach (var disk in game.Roms.Where(r => r.IsDisk && r.Status != DatRomStatus.NoDump && !set.AttachedDisks.Contains(r)))
        {
            if (!string.IsNullOrEmpty(disk.Merge) && disk.Sha1 is { } sha1 && biosDiskSha1s.Contains(sha1))
            {
                MatchedDatEntries.Add(disk);
                usesBios = true;
            }
            else
            {
                missingDisks.Add(disk);
            }
        }
        MatchedDatEntries.UnionWith(set.AttachedDisks);

        // Every missing ROM or disk shared with another set: the set relies on it. On a BIOS absent from the source when
        // they all are ROMs of the BIOS, otherwise on the set it takes its ROMs from (the parent of a split clone)
        var missing = missingRoms.Concat(missingDisks).ToList();
        string? requiredSet = null;
        var requiresBios = false;
        if (missing.Count > 0 && missing.All(r => !string.IsNullOrEmpty(r.Merge)) && !string.IsNullOrEmpty(game.RomOf))
        {
            var biosRomKeys = bios is null
                ? []
                : ExpectedRoms(bios).Select(r => (r.Size, r.Crc.ToLowerInvariant())).ToHashSet();
            if (bios is not null && missingDisks.Count == 0 && missingRoms.All(r => biosRomKeys.Contains((r.Size, r.Crc.ToLowerInvariant()))))
            {
                requiredSet = bios.Name;
                requiresBios = true;
            }
            else
            {
                requiredSet = game.RomOf;
                requiresBios = _catalog.GameByName.TryGetValue(game.RomOf, out var romOf) && romOf.IsBios;
            }
        }

        // Sets of the source MAME needs besides this one: the BIOS it takes ROMs from, the parent of a split clone, and
        // the devices whose ROMs the set does not hold (a fully non-merged set holds them all)
        var requiredSets = new List<string>();
        if (usesBios && bios is not null)
        {
            requiredSets.Add(bios.Name);
        }
        if (requiredSet is not null && !requiresBios && setsByGame.ContainsKey(requiredSet))
        {
            requiredSets.Add(requiredSet);
        }

        var missingDevices = new List<string>();
        foreach (var device in RequiredDevices(game))
        {
            var deviceRoms = ExpectedRoms(device)
                .Select(r => (r.Size, Crc: r.Crc.ToLowerInvariant()))
                .Where(key => !memberKeys.Contains(key))
                .ToList();
            if (deviceRoms.Count == 0)
            {
                continue;
            }

            if (setsByGame.ContainsKey(device.Name))
            {
                requiredSets.Add(device.Name);
            }
            else if (FindSetsHoldingDeviceRoms(device, deviceRoms, setsByGame) is { } holders)
            {
                requiredSets.AddRange(holders);
            }
            else
            {
                missingDevices.Add(device.Name);
            }
        }

        return new MameSetInfo(game.Name, missingRoms.Count, missingDisks.Count, unknownMembers, requiredSet)
        {
            SoftwareList = ListOf(game),
            HeldClones = clonesHeld.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            RequiresBios = requiresBios,
            BiosSet = usesBios ? bios?.Name : null,
            RequiredSets = requiredSets.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            MissingDevices = missingDevices
        };
    }

    /// <summary>
    /// Devices of a machine that have ROMs to look for (<c>device_ref</c>, which <c>mame -listxml</c> lists for the whole
    /// device tree of the machine): MAME looks for them in the set of the machine, then in the set of the device.
    /// </summary>
    private IEnumerable<DatGame> RequiredDevices(DatGame game) =>
        game.DeviceRefs
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !string.Equals(name, game.Name, StringComparison.OrdinalIgnoreCase))
            .Select(name => _catalog.GameByName.GetValueOrDefault(name))
            .OfType<DatGame>()
            .Where(device => ExpectedRoms(device).Any());

    /// <summary>
    /// Sets of the source that hold the ROMs of a device without a set of its own: a device whose ROMs are merged into
    /// another device (<c>qsound_hle</c>, <c>romof="qsound"</c>) has no archive in a merged collection, and MAME finds
    /// them in the sets of its <c>romof</c> chain. <c>null</c> when a ROM is in none of them.
    /// </summary>
    private List<string>? FindSetsHoldingDeviceRoms(
        DatGame device,
        IReadOnlyList<(long Size, string Crc)> roms,
        Dictionary<string, List<SetCandidate>> setsByGame)
    {
        var chain = Ancestors(device)
            .Where(setsByGame.ContainsKey)
            .Select(name => (Name: name, Keys: MemberKeys(setsByGame[name].SelectMany(s => s.Members))))
            .ToList();
        var holders = new List<string>();
        foreach (var rom in roms)
        {
            var holder = chain.FirstOrDefault(set => set.Keys.Contains(rom)).Name;
            if (holder is null)
            {
                return null;
            }

            holders.Add(holder);
        }

        return holders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The BIOS a machine takes ROMs from: the first BIOS of its <c>romof</c> chain (a clone, its parent, the BIOS).</summary>
    private DatGame? FindBios(DatGame game) =>
        Ancestors(game).Select(name => _catalog.GameByName[name]).FirstOrDefault(ancestor => ancestor.IsBios);

    // A machine and its clones, whose ROMs a merged set holds together
    private HashSet<string> GetFamily(DatGame game)
    {
        var family = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { game.Name };
        if (_catalog.IsParentGame(game))
        {
            family.UnionWith(_catalog.CloneToParent
                .Where(kv => string.Equals(kv.Value, game.Name, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key));
        }
        return family;
    }

    /// <summary>
    /// Description of a machine for the 1G1R: a clone gets the title of its parent and, with a DAT that links clones to
    /// parents, every machine gets its parent set as game group.
    /// </summary>
    private ParsedRomName ParseMachine(DatGame game, string path, long fileSize)
    {
        var parsed = Parse(game, path, fileSize);
        var parentName = _catalog.GetCanonicalGameName(game.Name);

        // A clone the category file does not list takes the category of its parent
        if (_categories?.Find(game.Name, parentName) is { } category && parsed.Machine is { } machine)
        {
            parsed = parsed with { Machine = machine with { Category = category } };
        }

        // Likewise for its rating; a machine the rating file does not rate is said so
        if (_ratings is not null && parsed.Machine is { } ratedMachine)
        {
            parsed = parsed with { Machine = ratedMachine with { Rating = _ratings.Find(game.Name, parentName) } };
        }

        if (!string.Equals(parentName, game.Name, StringComparison.OrdinalIgnoreCase))
        {
            parsed = parsed with
            {
                CloneOf = parentName,
                ParentTitle = _catalog.GameByName.TryGetValue(parentName, out var parent)
                    ? Parse(parent).Title
                    : null
            };
        }

        return _hasParentCloneRelations ? parsed with { GameGroup = parentName } : parsed;
    }

    // A machine, or a software of the list the run sorts
    private ParsedRomName Parse(DatGame game, string? path = null, long fileSize = 0) =>
        _softwareList is null ? _parser.ParseGame(game, path, fileSize) : _parser.ParseSoftware(game, path, fileSize);

    /// <summary>
    /// Path of a file of a set in the destination: a software goes into the folder of its list
    /// (<c>megadriv/sonic.zip</c>), where MAME looks for it; a machine has no such folder.
    /// </summary>
    private string InListFolder(DatGame game, string relativePath) =>
        ListOf(game) is { } list ? Path.Combine(list, relativePath) : relativePath;

    // Software list of a software of the run; null for a MAME machine, or a list without a name
    private string? ListOf(DatGame game) =>
        _softwareList is null ? null : (game.SoftwareList ?? _softwareList) is { Length: > 0 } list ? list : null;

    private ProcessItem CreateSetItem(DatGame game, string path, string extension)
    {
        var size = FileLength(path);
        return new ProcessItem
        {
            SourcePath = path,
            FileSize = size,
            Parsed = ParseMachine(game, path, size),
            TargetFilename = InListFolder(game, game.Name + extension.ToLowerInvariant()),
            DatGameName = game.Name,
            IsMultidisk = false,
            IsWholeArchive = true
        };
    }

    // A CHD goes into the folder named after its set, where MAME looks for it
    private ProcessItem CreateDiskItem(DatGame game, DatRomEntry disk, string path, ChdInfo chd)
    {
        // A disk stays in the folder of the machine of its family that holds it (merged CHDs: the folder of the parent,
        // where MAME also looks for the disks of the clones); otherwise it goes into the folder of its machine
        var folder = Path.GetFileName(Path.GetDirectoryName(path));
        var targetFolder = folder is { Length: > 0 }
                           && _catalog.GameByName.TryGetValue(folder, out var folderGame)
                           && string.Equals(_catalog.GetCanonicalGameName(folderGame.Name), _catalog.GetCanonicalGameName(game.Name), StringComparison.OrdinalIgnoreCase)
            ? folderGame.Name
            : game.Name;

        var size = FileLength(path);
        return new ProcessItem
        {
            SourcePath = path,
            FileSize = size,
            Parsed = ParseMachine(game, path, size),
            TargetFilename = InListFolder(game, Path.Combine(targetFolder, disk.Name + ".chd")),
            DatGameName = game.Name,
            Sha1 = chd.Sha1,
            Chd = chd,
            IsMultidisk = false
        };
    }

    internal ProcessItem CreateUnmatchedItem(string path, LocalizedMessage reason, bool isWholeArchive = true) =>
        new()
        {
            SourcePath = path,
            FileSize = FileLength(path),
            Parsed = _parser.ParseDescription(Path.GetFileNameWithoutExtension(path), rawFilename: Path.GetFileName(path), filePath: path) with
            {
                IsTosecConform = false,
                Convention = _softwareList is null ? NamingConvention.Mame : NamingConvention.MameSoftwareList
            },
            Status = FileStatus.Unmatched,
            StatusReason = reason,
            IsWholeArchive = isWholeArchive
        };

    private void ApplyFilters(ProcessItem item)
    {
        var (isKept, reason) = _filterService.EvaluateFilter(item.Parsed, _config.Filters);
        item.Status = isKept ? FileStatus.Valid : FileStatus.Excluded;
        item.StatusReason = reason;
        foreach (var related in item.RelatedItems)
        {
            related.Status = item.Status;
            related.StatusReason = reason;
        }
    }

    private static long FileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>Reports a step whose length is not known in advance: no count, no remaining time.</summary>
    private static void ReportStep(IProgress<SortProgress>? progress, LocalizedMessage message) =>
        progress?.Report(new SortProgress(0, 0, 0, 0, 0.0, message));

    private void ReportProgress(IProgress<SortProgress>? progress, Stopwatch sw, int done, int total, string path)
    {
        if (progress is null)
        {
            return;
        }

        progress.Report(new SortProgress(
            ProcessedCount: done,
            TotalCount: total,
            ProcessedBytes: 0,
            TotalBytes: 0,
            EtaSeconds: EtaEstimator.EstimateSeconds(sw.Elapsed.TotalSeconds, done, total),
            Message: LocalizedMessage.Create("progress.identifying_set", ("name", Path.GetFileName(path)))));
    }
}
