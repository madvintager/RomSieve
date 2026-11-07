using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using RomSieve.Core.Services;

using System.Diagnostics.CodeAnalysis;

namespace RomSieve.Core.Models;

/// <summary>
/// Represents an individual ROM entry within a DAT file.
/// </summary>
public sealed class DatRomEntry
{
    public DatRomEntry()
    {
    }

    [SetsRequiredMembers]
    public DatRomEntry(
        string Name,
        long Size,
        string Crc,
        string? Md5 = null,
        string? Sha1 = null,
        string? GameName = null,
        string? Description = null)
    {
        this.Name = Name;
        this.Size = Size;
        this.Crc = Crc;
        this.Md5 = Md5;
        this.Sha1 = Sha1;
        this.GameName = GameName;
        this.Description = Description;
    }

    public required string Name { get; set; }
    public required long Size { get; set; }
    public required string Crc { get; set; } // 8-character lowercase hex string
    public string? Md5 { get; set; }         // 32-character lowercase hex string
    public string? Sha1 { get; set; }        // 40-character lowercase hex string
    public string? GameName { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// CHD disk of a MAME-style DAT (<c>&lt;disk name="..." sha1="..."/&gt;</c>): no size nor CRC, identified by the
    /// SHA-1 of the CHD header (data and metadata), and named without the <c>.chd</c> extension.
    /// </summary>
    public bool IsDisk { get; set; }

    /// <summary>
    /// Name of the same ROM or disk in the parent set (<c>merge</c> attribute of MAME-style DATs): the entry is shared
    /// with the parent (or its BIOS), and lives in the parent's archive in a split or merged set.
    /// </summary>
    public string? Merge { get; set; }

    /// <summary>
    /// Dump status. A <see cref="DatRomStatus.NoDump"/> entry has no checksum: it stays in its game but is not indexed by
    /// the catalog (<see cref="DatCatalog.AddGame"/>), so it is never identified nor reported as missing.
    /// </summary>
    public DatRomStatus Status { get; set; }
}

/// <summary>
/// Represents a game grouping one or more ROMs in a DAT file.
/// </summary>
public sealed partial class DatGame
{
    // Media numbering is only read from a parenthesized flag: "(Disk 1 of 2)", "(Disc 2)", "(Side B)". In a title,
    // "Demonstration Disc 2.0", "Bubble Bobble Part 2" or "Scenery Disk 07" name a single release.
    [GeneratedRegex(@"\(\s*(?:Disk|Disc|Tape|Part)\s+(\d+)\s+of\s+(\d+)\b[^()]*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MultiDiskOfTotalRegex();

    [GeneratedRegex(@"\(\s*(?:Side\s+[A-Z0-9]\b|(?:Disk|Disc|Tape|Part)\s+\d+(?![.,]?\d))[^()]*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MultiDiskLabelRegex();

    public required string Name { get; set; }
    public string Description { get; set; } = string.Empty;
    public List<DatRomEntry> Roms { get; set; } = [];
    public string? Id { get; set; }
    public string? CloneOf { get; set; }
    public string? CloneOfId { get; set; }

    /// <summary>
    /// Set whose ROMs this game uses (<c>romof</c> of MAME-style DATs): its parent for a clone, but also the BIOS of a
    /// parent game (a Neo-Geo game is <c>romof="neogeo"</c> without being a clone). Never a parent / clone relation by
    /// itself: only <see cref="CloneOf"/> makes a clone.
    /// </summary>
    public string? RomOf { get; set; }

    // MAME machine information (attributes and elements of MAME-style DATs; defaults for the other DATs)
    public bool IsBios { get; set; }
    public bool IsDevice { get; set; }
    public bool IsMechanical { get; set; }

    /// <summary>False for a machine MAME cannot start by itself (devices, <c>runnable="no"</c>).</summary>
    public bool IsRunnable { get; set; } = true;

    /// <summary>Year as written by the DAT, possibly incomplete (<c>199?</c>, <c>19??</c>).</summary>
    public string? Year { get; set; }

    public string? Manufacturer { get; set; }

    /// <summary>
    /// Publisher of a software of a MAME software list (<c>&lt;publisher&gt;</c>, which the DATs of the other
    /// conventions never have: <see cref="DatCatalog.DetectConvention"/> recognizes the lists by it).
    /// </summary>
    public string? Publisher { get; set; }

    /// <summary>
    /// MAME software list of a software (<c>megadriv</c>): the folder MAME looks for it in. Set by
    /// <see cref="DatCatalog.CompleteSoftwareList"/>; <c>null</c> for the games of the other DATs.
    /// </summary>
    public string? SoftwareList { get; set; }

    /// <summary>Driver status (<c>&lt;driver status="..."/&gt;</c>): the worst of the emulation and of the features.</summary>
    public MameDriverStatus DriverStatus { get; set; }

    /// <summary>Emulation of the machine itself (<c>&lt;driver emulation="..."/&gt;</c>), whatever its features.</summary>
    public MameDriverStatus EmulationStatus { get; set; }

    /// <summary>
    /// Features MAME does not emulate (<c>&lt;feature type="..." status="unemulated"/&gt;</c>, or <c>overall</c> for
    /// a feature of one of its devices).
    /// </summary>
    public MameFeatures UnemulatedFeatures { get; set; }

    /// <summary>
    /// Whether MAME needs an external artwork to show the machine properly (<c>&lt;driver requiresartwork="yes"/&gt;</c>):
    /// mechanical machines, LCD handhelds, fruit machines.
    /// </summary>
    public bool RequiresArtwork { get; set; }

    /// <summary>Screen and controls of a MAME machine (<c>&lt;display&gt;</c>, <c>&lt;input&gt;</c>); <c>null</c> when the DAT does not describe them.</summary>
    public MameCabinet? Cabinet { get; set; }

    /// <summary>Devices the machine uses (<c>&lt;device_ref name="..."/&gt;</c>), in DAT order.</summary>
    public List<string> DeviceRefs { get; set; } = [];

    public bool IsMultidisk
    {
        get
        {
            IEnumerable<string?> namesToCheck = [Name, Description, .. Roms.Select(r => r.Name)];
            foreach (var n in namesToCheck)
            {
                if (string.IsNullOrEmpty(n))
                {
                    continue;
                }

                var m = MultiDiskOfTotalRegex().Match(n);
                if (m.Success && int.TryParse(m.Groups[2].ValueSpan, out var total))
                {
                    return total > 1;
                }

                if (MultiDiskLabelRegex().IsMatch(n))
                {
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>
/// Header metadata of a DAT file.
/// </summary>
public sealed class DatHeader
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public string Homepage { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public NamingConvention Convention { get; set; } = NamingConvention.Auto;

    /// <summary>
    /// File this MAME software list was read from when it is written in MAME's own format (<c>hash/&lt;list&gt;.xml</c>,
    /// an output of <c>mame -getsoftlist</c>), whose <c>&lt;software&gt;</c> elements a DAT export can copy as they are
    /// (<see cref="Services.DatService.WriteMameSoftwareListAsync"/>); <c>null</c> for any other DAT.
    /// </summary>
    public string? SoftwareListFile { get; set; }

    /// <summary>
    /// File this DAT was read from (<see cref="Services.DatService.ParseDatFileAsync"/>), which no export may replace;
    /// <c>null</c> for a DAT built in memory.
    /// </summary>
    public string? SourceFile { get; set; }

    /// <summary>
    /// Whether the DAT was read from a CLRMAMEPRO file rather than a Logiqx XML one: the exports of a lot in the format of
    /// each DAT (<see cref="DatExportFormat.Auto"/>) write it in that format.
    /// </summary>
    public bool IsClrMameProSource { get; set; }
}

/// <summary>
/// Complete catalog of ROM and game entries loaded from a DAT file with O(1) lookup indices.
/// </summary>
public sealed partial class DatCatalog
{
    [GeneratedRegex(@"\bmame\b", RegexOptions.CultureInvariant)]
    private static partial Regex MameWordRegex();

    public DatHeader Header { get; set; } = new();

    /// <summary>
    /// Catalogs of the MAME software lists this catalog gathers (a folder of DATs, or an output of MAME holding several
    /// lists), by list name; <c>null</c> for a catalog of a single DAT. The short name of a software is only unique in
    /// its list (<c>sonic</c> is in <c>megadriv</c> and in other lists): the indices of the gathering catalog (games,
    /// entries, checksums) hold every list, but a lookup by name, a parent, a set needed by another, are only meaningful
    /// in the catalog of the list (<see cref="CatalogFor"/>).
    /// </summary>
    public IReadOnlyDictionary<string, DatCatalog>? SoftwareLists { get; private set; }

    // Software list of each entry of a catalog that gathers several, built at the first query
    private Dictionary<DatRomEntry, string>? _listOfEntry;

    /// <summary>
    /// Software list of an entry when this catalog gathers several (<see cref="SoftwareLists"/>), which names it
    /// together with its game; <c>null</c> otherwise.
    /// </summary>
    public string? SoftwareListOf(DatRomEntry entry)
    {
        if (SoftwareLists is null)
        {
            return null;
        }

        if (_listOfEntry is null)
        {
            var index = new Dictionary<DatRomEntry, string>(ReferenceEqualityComparer.Instance);
            foreach (var (name, list) in SoftwareLists)
            {
                foreach (var rom in list.Games.SelectMany(g => g.Roms))
                {
                    index.TryAdd(rom, name);
                }
            }

            _listOfEntry = index;
        }

        return _listOfEntry.GetValueOrDefault(entry);
    }

    /// <summary>Catalog of the software list <paramref name="list"/> when this catalog gathers several, otherwise itself.</summary>
    public DatCatalog CatalogFor(string? list) =>
        SoftwareLists is not null && list is not null && SoftwareLists.TryGetValue(list, out var catalog) ? catalog : this;

    /// <summary>
    /// Catalog gathering the catalogs of several MAME software lists (<see cref="SoftwareLists"/>), in the given order,
    /// under a header named <paramref name="name"/>.
    /// </summary>
    public static DatCatalog GatherSoftwareLists(IReadOnlyList<DatCatalog> lists, string name)
    {
        var gathered = new DatCatalog
        {
            Header = new DatHeader
            {
                Name = name,
                Author = "MAME",
                Version = lists.Select(l => l.Header.Version).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? string.Empty,
                Convention = NamingConvention.MameSoftwareList
            }
        };

        var byName = new Dictionary<string, DatCatalog>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in lists)
        {
            byName[list.Header.Name] = list;
            foreach (var game in list.Games)
            {
                gathered.AddGame(game);
            }
        }

        gathered.SoftwareLists = byName;
        return gathered;
    }

    /// <summary>
    /// DATs of a lot (a folder of No-Intro, Redump or TOSEC DATs), in the order of their paths in the folder; <c>null</c>
    /// for a catalog of a single DAT. Each DAT keeps its own catalog, never merged with the others: the catalog of the
    /// lot holds no game and no entry, only the sizes of all the DATs (<see cref="KnownSizes"/>) and a light index of
    /// their checksums, which tells which DATs know a file (<see cref="FindMatchesByDat"/>). A lookup by name, a game,
    /// a parent are only meaningful in the catalog of a DAT (<see cref="DatFor"/>).
    /// </summary>
    public IReadOnlyList<DatLotMember>? Dats { get; private set; }

    private Dictionary<string, DatLotMember>? _datByName;

    // DATs of the lot that hold an entry of a size and CRC, and of a CRC: the index of the DAT in Dats when one DAT
    // holds it (nearly always), or the complement of an index in _sharedDats when several do
    private Dictionary<(long Size, string Crc), int>? _datsBySizeCrc;
    private Dictionary<string, int>? _datsByCrc;
    private List<List<int>>? _sharedDats;

    /// <summary>
    /// Catalog of the DAT named <paramref name="name"/> (case ignored) when this catalog is a lot (<see cref="Dats"/>),
    /// <c>null</c> when the lot has no such DAT; this catalog itself when it is the catalog of a single DAT.
    /// </summary>
    public DatCatalog? DatFor(string? name)
    {
        if (Dats is null)
        {
            return this;
        }

        return DatMemberFor(name)?.Catalog;
    }

    /// <summary>
    /// DAT of the lot named <paramref name="name"/> (case ignored), with its path; <c>null</c> when the lot has no such
    /// DAT, or when this catalog is the catalog of a single DAT.
    /// </summary>
    public DatLotMember? DatMemberFor(string? name) =>
        Dats is not null && name is not null && _datByName!.TryGetValue(name.Trim(), out var member) ? member : null;

    /// <summary>
    /// Catalog of a lot of DATs (<see cref="Dats"/>), in the given order, under a header named
    /// <paramref name="name"/>: the catalogs are kept as they are, only their sizes and checksums are indexed.
    /// </summary>
    public static DatCatalog GatherLot(IReadOnlyList<DatLotMember> dats, string name)
    {
        var lot = new DatCatalog
        {
            Header = new DatHeader { Name = name, Convention = NamingConvention.Auto },
            Dats = dats,
            _datByName = new Dictionary<string, DatLotMember>(StringComparer.OrdinalIgnoreCase),
            _datsBySizeCrc = [],
            _datsByCrc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            _sharedDats = []
        };

        for (var index = 0; index < dats.Count; index++)
        {
            var catalog = dats[index].Catalog;
            if (!lot._datByName.TryAdd(dats[index].Name.Trim(), dats[index]))
            {
                throw new ArgumentException($"Two DATs of the lot are named {dats[index].Name}.", nameof(dats));
            }

            lot.KnownSizes.UnionWith(catalog.KnownSizes);
            foreach (var key in catalog.BySizeCrc.Keys)
            {
                lot.AddDat(lot._datsBySizeCrc, key, index);
            }

            foreach (var crc in catalog.ByCrc.Keys)
            {
                lot.AddDat(lot._datsByCrc, crc, index);
            }
        }

        return lot;
    }

    // Adds a DAT to the DATs of a key; the DATs are added in order, so a DAT already there is the last one
    private void AddDat<TKey>(Dictionary<TKey, int> index, TKey key, int dat)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out var current))
        {
            index[key] = dat;
        }
        else if (current >= 0)
        {
            if (current != dat)
            {
                _sharedDats!.Add([current, dat]);
                index[key] = ~(_sharedDats.Count - 1);
            }
        }
        else
        {
            var shared = _sharedDats![~current];
            if (shared[^1] != dat)
            {
                shared.Add(dat);
            }
        }
    }

    private void AddDatsOf<TKey>(Dictionary<TKey, int> index, TKey key, SortedSet<int> dats)
        where TKey : notnull
    {
        if (index.TryGetValue(key, out var value))
        {
            if (value >= 0)
            {
                dats.Add(value);
            }
            else
            {
                dats.UnionWith(_sharedDats![~value]);
            }
        }
    }

    /// <summary>
    /// The DATs of a lot that know a file, in the order of the lot, each with its matches
    /// (<see cref="FindAllMatches"/> of its catalog); empty for a catalog of a single DAT. Only the DATs that hold the
    /// size and CRC, or the CRC, are queried, every DAT for a lookup by SHA-1 or MD5 (a CHD, a verified dump).
    /// </summary>
    public IReadOnlyList<(DatLotMember Dat, IReadOnlyList<DatRomEntry> Matches)> FindMatchesByDat(
        long fileSize,
        string? crc = null,
        string? md5 = null,
        string? sha1 = null)
    {
        if (Dats is null)
        {
            return [];
        }

        IEnumerable<int> candidates;
        if (!string.IsNullOrEmpty(sha1) || !string.IsNullOrEmpty(md5))
        {
            candidates = Enumerable.Range(0, Dats.Count);
        }
        else if (string.IsNullOrEmpty(crc))
        {
            return [];
        }
        else
        {
            var crcNorm = crc.ToLowerInvariant();
            var dats = new SortedSet<int>();
            AddDatsOf(_datsBySizeCrc!, (fileSize, crcNorm), dats);
            AddDatsOf(_datsByCrc!, crcNorm, dats);
            candidates = dats;
        }

        var found = new List<(DatLotMember, IReadOnlyList<DatRomEntry>)>();
        foreach (var index in candidates)
        {
            var matches = Dats[index].Catalog.FindAllMatches(fileSize, crc, md5, sha1);
            if (matches.Count > 0)
            {
                found.Add((Dats[index], matches));
            }
        }

        return found;
    }

    public List<DatRomEntry> Entries { get; } = [];
    public List<DatGame> Games { get; } = [];
    public Dictionary<string, DatGame> GameByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DatGame> GamesById { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> CloneToParent { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<long> KnownSizes { get; } = [];

    // Hash and name indices are multi-valued because the same ROM (same size and checksums, possibly
    // the same name) frequently belongs to several games of a DAT (compilations, shared BIOS, identical alternates).
    // Each list keeps the DAT order, which is the last tie-breaker of the deterministic selection rule. The indices by
    // name ignore the case: their keys are the names themselves, never a lowercase copy (a lot of thousands of DATs
    // holds millions of names).
    public Dictionary<(long Size, string Crc), List<DatRomEntry>> BySizeCrc { get; } = [];
    public Dictionary<string, List<DatRomEntry>> ByCrc { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<DatRomEntry>> BySha1 { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<DatRomEntry>> ByMd5 { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<DatRomEntry>> ByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<DatGame>> RomToGame { get; } = new(StringComparer.OrdinalIgnoreCase);

    private static void AddToIndex<TKey, TValue>(Dictionary<TKey, List<TValue>> index, TKey key, TValue value)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out var list))
        {
            // Nearly every key has a single value
            list = new List<TValue>(1);
            index[key] = list;
        }
        list.Add(value);
    }

    public void AddEntry(DatRomEntry entry)
    {
        Entries.Add(entry);
        if (!entry.IsDisk)
        {
            KnownSizes.Add(entry.Size);
        }

        var crcNorm = entry.Crc?.ToLowerInvariant() ?? string.Empty;
        if (!string.IsNullOrEmpty(crcNorm))
        {
            AddToIndex(BySizeCrc, (entry.Size, crcNorm), entry);
            AddToIndex(ByCrc, crcNorm, entry);
        }

        if (!string.IsNullOrEmpty(entry.Sha1))
        {
            AddToIndex(BySha1, entry.Sha1.ToLowerInvariant(), entry);
        }

        if (!string.IsNullOrEmpty(entry.Md5))
        {
            AddToIndex(ByMd5, entry.Md5.ToLowerInvariant(), entry);
        }

        if (!string.IsNullOrEmpty(entry.Name))
        {
            AddToIndex(ByName, entry.Name, entry);
        }
    }

    public void AddGame(DatGame game)
    {
        Games.Add(game);
        ShareDescriptionWithName(game);
        if (!string.IsNullOrEmpty(game.Name))
        {
            GameByName[game.Name] = game;
        }

        if (!string.IsNullOrEmpty(game.Id))
        {
            GamesById[game.Id] = game;
        }

        foreach (var r in game.Roms)
        {
            // A ROM that was never dumped has no checksum to match: indexing it would only report it as missing
            if (r.Status == DatRomStatus.NoDump)
            {
                continue;
            }

            AddEntry(r);
            if (!string.IsNullOrEmpty(r.Name))
            {
                var games = RomToGame.TryGetValue(r.Name, out var existing) ? existing : null;
                if (games is null || !games.Contains(game))
                {
                    AddToIndex(RomToGame, r.Name, game);
                }
            }
        }
    }

    // The description of most games is their name, read twice from the DAT: one string serves both, and the ROMs
    private static void ShareDescriptionWithName(DatGame game)
    {
        var description = game.Description;
        if (ReferenceEquals(description, game.Name) || !string.Equals(description, game.Name, StringComparison.Ordinal))
        {
            return;
        }

        game.Description = game.Name;
        foreach (var rom in game.Roms)
        {
            if (ReferenceEquals(rom.Description, description))
            {
                rom.Description = game.Name;
            }
        }
    }

    /// <summary>
    /// All games containing a ROM with this name, in DAT order.
    /// </summary>
    public IReadOnlyList<DatGame> GetGamesForRom(string romName) =>
        RomToGame.TryGetValue(romName, out var games) ? games : [];

    /// <summary>
    /// Game containing a ROM with this name. When several games share the name, the selection is deterministic:
    /// <paramref name="preferredGameName"/> when it is one of them (typically the game identified at classification),
    /// otherwise the first parent game (not a clone), otherwise the first game in DAT order.
    /// </summary>
    public DatGame? GetGameForRom(string romName, string? preferredGameName = null)
    {
        var games = GetGamesForRom(romName);
        if (games.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(preferredGameName))
        {
            var preferred = games.FirstOrDefault(g => string.Equals(g.Name, preferredGameName, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
            {
                return preferred;
            }
        }

        return games.FirstOrDefault(IsParentGame) ?? games[0];
    }

    /// <summary>
    /// Game an entry belongs to, using the game name recorded on the entry when available.
    /// </summary>
    public DatGame? GetGameForEntry(DatRomEntry entry) =>
        !string.IsNullOrEmpty(entry.GameName) && GameByName.TryGetValue(entry.GameName, out var game)
            ? game
            : GetGameForRom(entry.Name);

    // Machines that take ROMs from a set directly (romof) or use it as a device (device_ref), built at the first query
    private Dictionary<string, List<DatGame>>? _dependentsBySet;
    private readonly Lock _dependentsLock = new();

    /// <summary>
    /// MAME machines that take ROMs from a set, in DAT order: its clones and the games of a BIOS (<c>romof</c>), the
    /// machines that use a device (<c>device_ref</c>), and so on from set to set (the clones of a game that uses a
    /// device, the users of a device whose ROMs are merged into this one). Devices are followed but not listed. A
    /// machine with its own copy of these ROMs (a non-merged set) is listed too: the DAT cannot tell.
    /// </summary>
    public IReadOnlyList<DatGame> GetMameDependents(string setName)
    {
        var dependentsBySet = GetDependentsIndex();
        var found = new HashSet<DatGame>(ReferenceEqualityComparer.Instance);
        var pending = new Queue<string>([setName]);
        while (pending.TryDequeue(out var name))
        {
            if (!dependentsBySet.TryGetValue(name, out var dependents))
            {
                continue;
            }

            foreach (var dependent in dependents)
            {
                if (!string.Equals(dependent.Name, setName, StringComparison.OrdinalIgnoreCase) && found.Add(dependent))
                {
                    pending.Enqueue(dependent.Name);
                }
            }
        }

        return Games.Where(game => found.Contains(game) && !game.IsDevice).ToList();
    }

    private Dictionary<string, List<DatGame>> GetDependentsIndex()
    {
        lock (_dependentsLock)
        {
            if (_dependentsBySet is not null)
            {
                return _dependentsBySet;
            }

            var index = new Dictionary<string, List<DatGame>>(StringComparer.OrdinalIgnoreCase);
            foreach (var game in Games)
            {
                if (!string.IsNullOrEmpty(game.RomOf))
                {
                    AddToIndex(index, game.RomOf, game);
                }

                foreach (var device in game.DeviceRefs.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!string.Equals(device, game.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        AddToIndex(index, device, game);
                    }
                }
            }

            return _dependentsBySet = index;
        }
    }

    /// <summary>
    /// Indicates whether a game is a parent, i.e. not declared as a clone of another game.
    /// </summary>
    public bool IsParentGame(DatGame game) =>
        string.IsNullOrEmpty(game.CloneOf) &&
        string.IsNullOrEmpty(game.CloneOfId) &&
        !CloneToParent.ContainsKey(game.Name);

    public string GetCanonicalGameName(string gameName)
    {
        var current = gameName;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!string.IsNullOrEmpty(current) &&
               CloneToParent.TryGetValue(current, out var parent) &&
               visited.Add(current))
        {
            if (!string.IsNullOrEmpty(parent))
            {
                current = parent;
            }
            else
            {
                break;
            }
        }

        return current;
    }

    /// <summary>
    /// Whether this catalog holds MAME software lists, one or several: their DATs are written into a folder, a DAT per
    /// list named after it, where MAME and a ROM manager look for them.
    /// </summary>
    public bool IsSoftwareListCatalog => SoftwareLists is not null || Header.Convention == NamingConvention.MameSoftwareList;

    /// <summary>
    /// Whether the DATs exported from this catalog are written into a folder, a DAT per file at its root: the catalog
    /// holds MAME software lists (<see cref="IsSoftwareListCatalog"/>), or is a lot of DATs (<see cref="Dats"/>).
    /// </summary>
    public bool IsFolderCatalog => IsSoftwareListCatalog || Dats is not null;

    /// <summary>
    /// Folder the lot of DATs was read from (<see cref="Abstractions.IDatService.ParseDatFileAsync(string, DatFolderOptions, CancellationToken)"/>),
    /// where no DAT is exported: the next sort would read it as a DAT of the lot; <c>null</c> for any other catalog.
    /// </summary>
    public string? LotFolder { get; set; }

    /// <summary>Games of the catalog, or of all the DATs of a lot.</summary>
    public int GameCount => Dats?.Sum(dat => dat.Catalog.Games.Count) ?? Games.Count;

    /// <summary>
    /// Whether <paramref name="path"/> is a file this catalog, one of its software lists or a DAT of its lot, was read from
    /// (<see cref="DatHeader.SourceFile"/>, <see cref="DatHeader.SoftwareListFile"/>): an export written there would
    /// replace the DAT by a part of it.
    /// </summary>
    public bool IsReadFrom(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var fullPath = Path.GetFullPath(path);
        IEnumerable<DatHeader> headers =
            SoftwareLists is { } lists ? [Header, .. lists.Values.Select(list => list.Header)]
            : Dats is { } dats ? [Header, .. dats.Select(dat => dat.Catalog.Header)]
            : [Header];
        return headers
            .SelectMany(header => new[] { header.SourceFile, header.SoftwareListFile })
            .OfType<string>()
            .Any(file => string.Equals(fullPath, Path.GetFullPath(file), comparison));
    }

    /// <summary>Times the convention was analysed from the content of the DAT (test hook: it scans every game).</summary>
    internal int ConventionAnalysisCount { get; private set; }

    public NamingConvention DetectConvention()
    {
        // A lot has no convention of its own: each of its DATs has its own
        if (Header.Convention != NamingConvention.Auto || Dats is not null)
        {
            return Header.Convention;
        }

        ConventionAnalysisCount++;

        // The software lists of MAME name a publisher, which no DAT of the other conventions does; their header may be
        // anything (the description of the list), so they are recognized first
        if (Games.Any(g => g.Publisher is not null))
        {
            return NamingConvention.MameSoftwareList;
        }

        var text = $"{Header.Name} {Header.Description} {Header.Author} {Header.Homepage} {Header.Url}".ToLowerInvariant();
        if (text.Contains("redump", StringComparison.Ordinal))
        {
            return NamingConvention.Redump;
        }
        if (text.Contains("no-intro", StringComparison.Ordinal) || text.Contains("datomatic", StringComparison.Ordinal))
        {
            return NamingConvention.NoIntro;
        }
        if (text.Contains("tosec", StringComparison.Ordinal))
        {
            return NamingConvention.Tosec;
        }
        if (MameWordRegex().IsMatch(text) || Games.Any(HasMameAttributes))
        {
            return NamingConvention.Mame;
        }

        foreach (var entry in Entries.Take(10))
        {
            if (!string.IsNullOrEmpty(entry.Name))
            {
                var conv = NoIntroRedumpParser.DetectNamingConvention(entry.Name);
                if (conv != NamingConvention.Auto)
                {
                    return conv;
                }
            }
        }

        return NamingConvention.Auto;
    }

    /// <summary>
    /// Completes a catalog of a MAME software list once read: each software belongs to the list named by the header,
    /// and a clone takes from the set of its parent (<c>romof</c>) the ROMs and disks it shares with it, where MAME looks
    /// for them too. The <c>hash</c> files of MAME do not mark these ROMs (<c>merge</c>): they are those of the same size
    /// and CRC (the same SHA-1 for a disk) as a ROM of the parent. A SabreTools DAT has no parents: nothing changes.
    /// </summary>
    public void CompleteSoftwareList()
    {
        var list = string.IsNullOrWhiteSpace(Header.Name) ? null : Header.Name.Trim();
        foreach (var game in Games)
        {
            game.SoftwareList ??= list;
            if (string.IsNullOrEmpty(game.CloneOf) || !GameByName.TryGetValue(game.CloneOf, out var parent))
            {
                continue;
            }

            game.RomOf ??= parent.Name;
            var parentRoms = new Dictionary<(long Size, string Crc), string>();
            var parentDisks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rom in parent.Roms.Where(r => r.Status != DatRomStatus.NoDump))
            {
                if (rom.IsDisk)
                {
                    if (!string.IsNullOrEmpty(rom.Sha1))
                    {
                        parentDisks.TryAdd(rom.Sha1, rom.Name);
                    }
                }
                else if (!string.IsNullOrEmpty(rom.Crc))
                {
                    parentRoms.TryAdd((rom.Size, rom.Crc.ToLowerInvariant()), rom.Name);
                }
            }

            foreach (var rom in game.Roms.Where(r => r.Merge is null && r.Status != DatRomStatus.NoDump))
            {
                if (rom.IsDisk)
                {
                    if (rom.Sha1 is { } sha1 && parentDisks.TryGetValue(sha1, out var diskName))
                    {
                        rom.Merge = diskName;
                    }
                }
                else if (parentRoms.TryGetValue((rom.Size, rom.Crc.ToLowerInvariant()), out var romName))
                {
                    rom.Merge = romName;
                }
            }
        }
    }

    // Information that only MAME-style DATs give (the DATs of the other conventions have none of it)
    private static bool HasMameAttributes(DatGame game) =>
        game.IsBios ||
        game.IsDevice ||
        game.IsMechanical ||
        !string.IsNullOrEmpty(game.RomOf) ||
        game.DriverStatus != MameDriverStatus.Unknown;

    /// <summary>
    /// All DAT entries matching a file, in DAT order: entries with the same size and CRC, then, failing that,
    /// entries with the same SHA-1 or MD5, then the entries of a CRC that is unique in the DAT whatever the size. For a
    /// lot (<see cref="Dats"/>), the matches of each DAT that knows the file, in the order of the lot.
    /// </summary>
    public IReadOnlyList<DatRomEntry> FindAllMatches(
        long fileSize,
        string? crc = null,
        string? md5 = null,
        string? sha1 = null)
    {
        if (Dats is not null)
        {
            var byDat = FindMatchesByDat(fileSize, crc, md5, sha1);
            return byDat.Count == 1 ? byDat[0].Matches : [.. byDat.SelectMany(found => found.Matches)];
        }

        var crcNorm = crc?.ToLowerInvariant() ?? string.Empty;

        if (!string.IsNullOrEmpty(crcNorm) && BySizeCrc.TryGetValue((fileSize, crcNorm), out var sizeCrcMatches))
        {
            return sizeCrcMatches;
        }

        if (!string.IsNullOrEmpty(sha1) && BySha1.TryGetValue(sha1.ToLowerInvariant(), out var sha1Matches))
        {
            return sha1Matches;
        }

        if (!string.IsNullOrEmpty(md5) && ByMd5.TryGetValue(md5.ToLowerInvariant(), out var md5Matches))
        {
            return md5Matches;
        }

        if (!string.IsNullOrEmpty(crcNorm) && ByCrc.TryGetValue(crcNorm, out var candidates) && candidates.Count == 1)
        {
            return candidates;
        }

        return [];
    }

    /// <summary>
    /// The DAT entry used to identify a file (target name, game, parent/clone). When several entries match,
    /// the selection is deterministic:
    /// <list type="number">
    /// <item>the entry whose file name equals <paramref name="preferredName"/> (the source file name, case-insensitive);</item>
    /// <item>otherwise the first entry belonging to a parent game rather than a clone;</item>
    /// <item>otherwise the first entry in DAT order.</item>
    /// </list>
    /// </summary>
    public DatRomEntry? FindMatch(
        long fileSize,
        string? crc = null,
        string? md5 = null,
        string? sha1 = null,
        string? preferredName = null)
    {
        var matches = FindAllMatches(fileSize, crc, md5, sha1);
        return SelectMatch(matches, preferredName);
    }

    /// <summary>
    /// The entry used to identify a file among <paramref name="matches"/> (for example the matches of
    /// <see cref="FindAllMatches"/> whose SHA-1 was verified), with the deterministic rule of <see cref="FindMatch"/>.
    /// </summary>
    public DatRomEntry? SelectMatch(IReadOnlyList<DatRomEntry> matches, string? preferredName = null) =>
        matches.Count <= 1 ? matches.FirstOrDefault() : SelectPreferredEntry(matches, preferredName);

    /// <summary>
    /// Fixdat of the catalog: a DAT listing only the <paramref name="missingRoms"/>, grouped by game in DAT order
    /// with their parent / clone relations, under the header <c>fixDat_&lt;name&gt;</c> used by RomVault and Igir.
    /// </summary>
    public DatCatalog CreateFixDat(IEnumerable<DatRomEntry> missingRoms)
    {
        var missing = new HashSet<DatRomEntry>(missingRoms, ReferenceEqualityComparer.Instance);
        var name = string.IsNullOrEmpty(Header.Name) ? "Collection" : Header.Name;
        var fixDat = new DatCatalog
        {
            Header = new DatHeader
            {
                Name = $"fixDat_{name}",
                Description = $"fixDat_{(string.IsNullOrEmpty(Header.Description) ? name : Header.Description)}",
                Version = Header.Version,
                Author = "RomSieve",
                Homepage = Header.Homepage,
                Url = Header.Url,
                Convention = Header.Convention
            }
        };

        foreach (var game in Games)
        {
            var roms = game.Roms.Where(missing.Contains).ToList();
            if (roms.Count == 0)
            {
                continue;
            }

            fixDat.AddGame(CopyGame(game, roms));
        }

        return fixDat;
    }

    /// <summary>Suffix of the header name of a filtered DAT (<see cref="CreateFilteredDat"/>).</summary>
    public const string FilteredDatSuffix = " (RomSieve)";

    /// <summary>
    /// Filtered DAT of the catalog: the games named by <paramref name="keptGameNames"/>, whole and in DAT order, under
    /// the header of the catalog marked <see cref="FilteredDatSuffix"/>. A relation to a game left out (the parent of a
    /// kept clone, the BIOS of a kept game) is removed, with the <c>merge</c> attributes of the ROMs once no <c>romof</c>
    /// is left: the game then stands alone, as its ROMs all are listed in it.
    /// </summary>
    public DatCatalog CreateFilteredDat(IEnumerable<string> keptGameNames)
    {
        var kept = new HashSet<string>(keptGameNames, StringComparer.OrdinalIgnoreCase);
        var name = string.IsNullOrEmpty(Header.Name) ? "Collection" : Header.Name;
        var filtered = new DatCatalog
        {
            Header = new DatHeader
            {
                Name = name + FilteredDatSuffix,
                Description = (string.IsNullOrEmpty(Header.Description) ? name : Header.Description) + FilteredDatSuffix,
                Version = Header.Version,
                Author = Header.Author,
                Comment = Header.Comment,
                Homepage = Header.Homepage,
                Url = Header.Url,
                Convention = Header.Convention
            }
        };

        var keptIds = Games
            .Where(g => kept.Contains(g.Name) && !string.IsNullOrEmpty(g.Id))
            .Select(g => g.Id!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var game in Games)
        {
            if (!kept.Contains(game.Name))
            {
                continue;
            }

            var copy = CopyGame(game, game.Roms);
            if (!string.IsNullOrEmpty(copy.CloneOf) && !kept.Contains(copy.CloneOf))
            {
                copy.CloneOf = null;
            }

            if (!string.IsNullOrEmpty(copy.CloneOfId) && !keptIds.Contains(copy.CloneOfId))
            {
                copy.CloneOfId = null;
            }

            if (!string.IsNullOrEmpty(copy.RomOf) && !kept.Contains(copy.RomOf))
            {
                copy.RomOf = null;
                foreach (var rom in copy.Roms)
                {
                    rom.Merge = null;
                }
            }

            filtered.AddGame(copy);
            if (!string.IsNullOrEmpty(copy.CloneOf))
            {
                filtered.CloneToParent[copy.Name] = copy.CloneOf;
            }
        }

        return filtered;
    }

    /// <summary>
    /// Number of files the games of the DAT describe, as a ROM manager counts them: a ROM that MAME lists several times
    /// in a machine under the same name (loaded in several regions, shared by several BIOS options) is a single file of
    /// its set. Never-dumped ROMs are not counted; disks are.
    /// </summary>
    public int CountFiles() =>
        Dats is { } dats ? dats.Sum(dat => dat.Catalog.CountFiles()) :
        Games.Sum(game => game.Roms
            .Where(r => r.Status != DatRomStatus.NoDump)
            .Select(r => (r.IsDisk, Name: r.Name.ToLowerInvariant()))
            .Distinct()
            .Count());

    /// <summary>
    /// A copy of a game of the DAT with all its information (parent, BIOS, MAME machine) and copies of the given ROMs.
    /// </summary>
    public static DatGame CopyGame(DatGame game, IEnumerable<DatRomEntry> roms) =>
        new()
        {
            Name = game.Name,
            Description = game.Description,
            Id = game.Id,
            CloneOf = game.CloneOf,
            CloneOfId = game.CloneOfId,
            RomOf = game.RomOf,
            IsBios = game.IsBios,
            IsDevice = game.IsDevice,
            IsMechanical = game.IsMechanical,
            IsRunnable = game.IsRunnable,
            Year = game.Year,
            Manufacturer = game.Manufacturer,
            Publisher = game.Publisher,
            SoftwareList = game.SoftwareList,
            DriverStatus = game.DriverStatus,
            EmulationStatus = game.EmulationStatus,
            UnemulatedFeatures = game.UnemulatedFeatures,
            RequiresArtwork = game.RequiresArtwork,
            Cabinet = game.Cabinet,
            DeviceRefs = [.. game.DeviceRefs],
            Roms = roms.Select(r => new DatRomEntry(r.Name, r.Size, r.Crc, r.Md5, r.Sha1, r.GameName, r.Description)
            {
                IsDisk = r.IsDisk,
                Merge = r.Merge,
                Status = r.Status
            }).ToList()
        };

    private DatRomEntry SelectPreferredEntry(IReadOnlyList<DatRomEntry> matches, string? preferredName)
    {
        if (!string.IsNullOrEmpty(preferredName))
        {
            var byName = matches.FirstOrDefault(e => string.Equals(
                Path.GetFileName(e.Name.Replace('\\', '/')),
                preferredName,
                StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                return byName;
            }
        }

        return matches.FirstOrDefault(e => GetGameForEntry(e) is { } game && IsParentGame(game)) ?? matches[0];
    }

    public string ToLogiqxXml()
    {
        var sb = new StringBuilder();
        var name = string.IsNullOrEmpty(Header.Name) ? "Collection" : Header.Name;
        var desc = !string.IsNullOrEmpty(Header.Description) ? Header.Description : name;
        var ver = string.IsNullOrEmpty(Header.Version) ? "1.0" : Header.Version;
        var author = string.IsNullOrEmpty(Header.Author) ? "RomSieve" : Header.Author;

        sb.AppendLine("<?xml version=\"1.0\"?>");
        sb.AppendLine("<!DOCTYPE datafile PUBLIC \"-//Logiqx//DTD ROMs Description Summary//EN\" \"http://www.logiqx.com/Dats/datafile.dtd\">");
        sb.AppendLine("<datafile>");
        sb.AppendLine("\t<header>");
        sb.AppendLine($"\t\t<name>{EscapeXmlText(name)}</name>");
        sb.AppendLine($"\t\t<description>{EscapeXmlText(desc)}</description>");
        sb.AppendLine($"\t\t<version>{EscapeXmlText(ver)}</version>");
        sb.AppendLine($"\t\t<author>{EscapeXmlText(author)}</author>");
        if (!string.IsNullOrEmpty(Header.Comment))
            sb.AppendLine($"\t\t<comment>{EscapeXmlText(Header.Comment)}</comment>");
        if (!string.IsNullOrEmpty(Header.Homepage))
            sb.AppendLine($"\t\t<homepage>{EscapeXmlText(Header.Homepage)}</homepage>");
        if (!string.IsNullOrEmpty(Header.Url))
            sb.AppendLine($"\t\t<url>{EscapeXmlText(Header.Url)}</url>");
        sb.AppendLine("\t</header>");

        foreach (var game in Games)
        {
            var attrs = new List<string> { $"name=\"{EscapeXmlAttr(game.Name)}\"" };
            if (!string.IsNullOrEmpty(game.CloneOf))
                attrs.Add($"cloneof=\"{EscapeXmlAttr(game.CloneOf)}\"");
            if (!string.IsNullOrEmpty(game.RomOf))
                attrs.Add($"romof=\"{EscapeXmlAttr(game.RomOf)}\"");
            if (!string.IsNullOrEmpty(game.Id))
                attrs.Add($"id=\"{EscapeXmlAttr(game.Id)}\"");
            if (!string.IsNullOrEmpty(game.CloneOfId))
                attrs.Add($"cloneofid=\"{EscapeXmlAttr(game.CloneOfId)}\"");
            if (game.IsBios)
                attrs.Add("isbios=\"yes\"");
            if (game.IsDevice)
                attrs.Add("isdevice=\"yes\"");
            if (game.IsMechanical)
                attrs.Add("ismechanical=\"yes\"");
            if (!game.IsRunnable)
                attrs.Add("runnable=\"no\"");

            sb.AppendLine($"\t<game {string.Join(" ", attrs)}>");
            if (!string.IsNullOrEmpty(game.Description))
                sb.AppendLine($"\t\t<description>{EscapeXmlText(game.Description)}</description>");
            if (!string.IsNullOrEmpty(game.Year))
                sb.AppendLine($"\t\t<year>{EscapeXmlText(game.Year)}</year>");
            if (!string.IsNullOrEmpty(game.Manufacturer))
                sb.AppendLine($"\t\t<manufacturer>{EscapeXmlText(game.Manufacturer)}</manufacturer>");
            if (!string.IsNullOrEmpty(game.Publisher))
                sb.AppendLine($"\t\t<publisher>{EscapeXmlText(game.Publisher)}</publisher>");

            foreach (var rom in game.Roms)
            {
                if (rom.IsDisk)
                {
                    var diskAttrs = new List<string> { $"name=\"{EscapeXmlAttr(rom.Name)}\"" };
                    if (!string.IsNullOrEmpty(rom.Md5))
                        diskAttrs.Add($"md5=\"{rom.Md5.ToLowerInvariant()}\"");
                    if (!string.IsNullOrEmpty(rom.Sha1))
                        diskAttrs.Add($"sha1=\"{rom.Sha1.ToLowerInvariant()}\"");
                    AddMameXmlAttributes(diskAttrs, rom);
                    sb.AppendLine($"\t\t<disk {string.Join(" ", diskAttrs)}/>");
                    continue;
                }

                var romAttrs = new List<string>
                {
                    $"name=\"{EscapeXmlAttr(rom.Name)}\"",
                    $"size=\"{rom.Size}\""
                };
                // A ROM that was never dumped has no checksum
                if (!string.IsNullOrEmpty(rom.Crc))
                    romAttrs.Add($"crc=\"{rom.Crc.ToLowerInvariant()}\"");
                if (!string.IsNullOrEmpty(rom.Md5))
                    romAttrs.Add($"md5=\"{rom.Md5.ToLowerInvariant()}\"");
                if (!string.IsNullOrEmpty(rom.Sha1))
                    romAttrs.Add($"sha1=\"{rom.Sha1.ToLowerInvariant()}\"");
                AddMameXmlAttributes(romAttrs, rom);

                sb.AppendLine($"\t\t<rom {string.Join(" ", romAttrs)}/>");
            }
            sb.AppendLine("\t</game>");
        }

        sb.AppendLine("</datafile>");
        return sb.ToString();
    }

    public string ToClrMamePro()
    {
        var sb = new StringBuilder();
        var name = string.IsNullOrEmpty(Header.Name) ? "Collection" : Header.Name;
        var desc = !string.IsNullOrEmpty(Header.Description) ? Header.Description : name;
        var ver = string.IsNullOrEmpty(Header.Version) ? "1.0" : Header.Version;
        var author = string.IsNullOrEmpty(Header.Author) ? "RomSieve" : Header.Author;

        sb.AppendLine("clrmamepro (");
        sb.AppendLine($"\tname \"{EscapeClrMamePro(name)}\"");
        sb.AppendLine($"\tdescription \"{EscapeClrMamePro(desc)}\"");
        sb.AppendLine($"\tversion \"{EscapeClrMamePro(ver)}\"");
        sb.AppendLine($"\tauthor \"{EscapeClrMamePro(author)}\"");
        if (!string.IsNullOrEmpty(Header.Comment))
            sb.AppendLine($"\tcomment \"{EscapeClrMamePro(Header.Comment)}\"");
        if (!string.IsNullOrEmpty(Header.Homepage))
            sb.AppendLine($"\thomepage \"{EscapeClrMamePro(Header.Homepage)}\"");
        if (!string.IsNullOrEmpty(Header.Url))
            sb.AppendLine($"\turl \"{EscapeClrMamePro(Header.Url)}\"");
        sb.AppendLine(")");
        sb.AppendLine();

        foreach (var game in Games)
        {
            // The CLRMAMEPRO format of MAME writes its BIOS sets as resources
            sb.AppendLine(game.IsBios ? "resource (" : "game (");
            sb.AppendLine($"\tname \"{EscapeClrMamePro(game.Name)}\"");
            if (!string.IsNullOrEmpty(game.Description))
                sb.AppendLine($"\tdescription \"{EscapeClrMamePro(game.Description)}\"");
            if (!string.IsNullOrEmpty(game.Year))
                sb.AppendLine($"\tyear \"{EscapeClrMamePro(game.Year)}\"");
            if (!string.IsNullOrEmpty(game.Manufacturer))
                sb.AppendLine($"\tmanufacturer \"{EscapeClrMamePro(game.Manufacturer)}\"");
            if (!string.IsNullOrEmpty(game.Publisher))
                sb.AppendLine($"\tpublisher \"{EscapeClrMamePro(game.Publisher)}\"");
            var cloneOf = game.CloneOf;
            if (string.IsNullOrEmpty(cloneOf) && !string.IsNullOrEmpty(game.CloneOfId) && GamesById.TryGetValue(game.CloneOfId, out var parentGame))
            {
                cloneOf = parentGame.Name;
            }

            if (!string.IsNullOrEmpty(cloneOf))
                sb.AppendLine($"\tcloneof \"{EscapeClrMamePro(cloneOf)}\"");
            if (!string.IsNullOrEmpty(game.RomOf))
                sb.AppendLine($"\tromof \"{EscapeClrMamePro(game.RomOf)}\"");

            foreach (var rom in game.Roms)
            {
                if (rom.IsDisk)
                {
                    var diskParts = new List<string> { $"name \"{EscapeClrMamePro(rom.Name)}\"" };
                    if (!string.IsNullOrEmpty(rom.Md5))
                        diskParts.Add($"md5 {rom.Md5.ToLowerInvariant()}");
                    if (!string.IsNullOrEmpty(rom.Sha1))
                        diskParts.Add($"sha1 {rom.Sha1.ToLowerInvariant()}");
                    AddMameClrMameProParts(diskParts, rom);
                    sb.AppendLine($"\tdisk ( {string.Join(" ", diskParts)} )");
                    continue;
                }

                var parts = new List<string>
                {
                    $"name \"{EscapeClrMamePro(rom.Name)}\"",
                    $"size {rom.Size}"
                };
                if (!string.IsNullOrEmpty(rom.Crc))
                    parts.Add($"crc {rom.Crc.ToLowerInvariant()}");
                if (!string.IsNullOrEmpty(rom.Md5))
                    parts.Add($"md5 {rom.Md5.ToLowerInvariant()}");
                if (!string.IsNullOrEmpty(rom.Sha1))
                    parts.Add($"sha1 {rom.Sha1.ToLowerInvariant()}");
                AddMameClrMameProParts(parts, rom);

                sb.AppendLine($"\trom ( {string.Join(" ", parts)} )");
            }

            sb.AppendLine(")");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public async Task WriteLogiqxXmlAsync(string path, CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        await File.WriteAllTextAsync(path, ToLogiqxXml(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteClrMameProAsync(string path, CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        await File.WriteAllTextAsync(path, ToClrMamePro(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    private static void AddMameXmlAttributes(List<string> attrs, DatRomEntry rom)
    {
        if (!string.IsNullOrEmpty(rom.Merge))
            attrs.Add($"merge=\"{EscapeXmlAttr(rom.Merge)}\"");
        if (rom.Status != DatRomStatus.Good)
            attrs.Add($"status=\"{rom.Status.ToDatString()}\"");
    }

    private static void AddMameClrMameProParts(List<string> parts, DatRomEntry rom)
    {
        if (!string.IsNullOrEmpty(rom.Merge))
            parts.Add($"merge \"{EscapeClrMamePro(rom.Merge)}\"");
        if (rom.Status != DatRomStatus.Good)
            parts.Add($"flags {rom.Status.ToDatString()}");
    }

    private static string EscapeXmlAttr(string val) =>
        val.Replace("&", "&amp;", StringComparison.Ordinal)
           .Replace("<", "&lt;", StringComparison.Ordinal)
           .Replace(">", "&gt;", StringComparison.Ordinal)
           .Replace("\"", "&quot;", StringComparison.Ordinal)
           .Replace("'", "&apos;", StringComparison.Ordinal);

    private static string EscapeXmlText(string val) =>
        val.Replace("&", "&amp;", StringComparison.Ordinal)
           .Replace("<", "&lt;", StringComparison.Ordinal)
           .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string EscapeClrMamePro(string val) =>
        val.Replace("\\", "\\\\", StringComparison.Ordinal)
           .Replace("\"", "\\\"", StringComparison.Ordinal);
}
