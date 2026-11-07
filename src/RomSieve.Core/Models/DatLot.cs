using System.IO;

namespace RomSieve.Core.Models;

/// <summary>
/// How a folder of DATs is read (<see cref="Abstractions.IDatService.ParseDatFileAsync(string, DatFolderOptions, CancellationToken)"/>):
/// with its subfolders or not, and the DATs left out of the sort, by header name (case ignored; empty: every DAT). The
/// DATs left out are only read for their header. A folder of MAME software lists ignores these options, as before lots
/// existed. With <paramref name="LoadOnDemand"/> (the default), each DAT of a lot is read once for its summary and the
/// keys of its checksums, then forgotten: only the DATs a run needs are read again (<see cref="DatLotMember.Load"/>).
/// </summary>
public sealed record DatFolderOptions(bool Recursive = false, IReadOnlyCollection<string>? ExcludedDats = null, bool LoadOnDemand = true)
{
    /// <summary>The folder alone, every DAT.</summary>
    public static DatFolderOptions Default { get; } = new();

    /// <summary>Names of the DATs left out, trimmed, case ignored.</summary>
    public HashSet<string> ExcludedNames() =>
        new((ExcludedDats ?? []).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A DAT of a folder of DATs, as its header tells it (<see cref="Abstractions.IDatService.ReadDatFolderPreview"/>):
/// its path relative to the folder, its name (that of its header, or its file name without a name) and its convention.
/// </summary>
public sealed record DatFolderEntry(string RelativePath, string Name, NamingConvention Convention)
{
    /// <summary>Folder of the DAT relative to the folder read: empty for a DAT at its root.</summary>
    public string RelativeFolder => Path.GetDirectoryName(RelativePath) ?? string.Empty;
}

/// <summary>
/// How far the read of the headers of a folder of DATs is (<see cref="Abstractions.IDatService.ReadDatFolderPreview"/>):
/// the DATs read so far, out of the DATs of the folder.
/// </summary>
public readonly record struct DatFolderReadProgress(int Read, int Total);

/// <summary>
/// What a lot keeps of one of its DATs without its games (<see cref="DatLotMember.Summary"/>): its header, its convention,
/// the numbers of its games, of its ROMs (<see cref="DatCatalog.Entries"/>) and of its files
/// (<see cref="DatCatalog.CountFiles"/>), and whether it has parent / clone relations. A run counts a DAT it never loads
/// from it: none of its ROMs present, no game found.
/// </summary>
public sealed record DatLotSummary(DatHeader Header, NamingConvention Convention, int GameCount, int RomCount, int FileCount, bool HasCloneRelations)
{
    /// <summary>Summary of a DAT read whole.</summary>
    public static DatLotSummary Of(DatCatalog catalog) => new(
        catalog.Header,
        catalog.DetectConvention(),
        catalog.Games.Count,
        catalog.Entries.Count,
        catalog.CountFiles(),
        catalog.CloneToParent.Count > 0);
}

/// <summary>
/// The checksums of a DAT a lot indexes (<see cref="DatCatalog.GatherLot"/>), in compact form: each size and CRC32 of an
/// entry (with whether one of its entries has a SHA-1), the SHA-1 of its disks, its sizes. Kept until the lot indexes
/// them, then dropped.
/// </summary>
public sealed class DatLotKeys
{
    private DatLotKeys(DatLotRomKey[] roms, string[] diskSha1s, long[] sizes)
    {
        Roms = roms;
        DiskSha1s = diskSha1s;
        Sizes = sizes;
    }

    internal DatLotRomKey[] Roms { get; }
    internal string[] DiskSha1s { get; }
    internal long[] Sizes { get; }

    /// <summary>
    /// The keys of a DAT read whole. A CRC32 that is not eight hexadecimal digits at most is left out: no file has it.
    /// </summary>
    public static DatLotKeys Of(DatCatalog catalog)
    {
        var roms = new List<DatLotRomKey>(catalog.BySizeCrc.Count);
        foreach (var ((size, crc), entries) in catalog.BySizeCrc)
        {
            if (DatLotRomKey.TryParseCrc(crc, out var value))
            {
                roms.Add(new DatLotRomKey(value, size, entries.Any(e => !string.IsNullOrEmpty(e.Sha1))));
            }
        }

        var disks = catalog.BySha1.Where(pair => pair.Value.Any(e => e.IsDisk)).Select(pair => pair.Key).ToArray();
        return new DatLotKeys([.. roms], disks, [.. catalog.KnownSizes]);
    }
}

/// <summary>A size and CRC32 of the entries of a DAT, and whether one of them has a SHA-1.</summary>
internal readonly record struct DatLotRomKey(uint Crc, long Size, bool HasSha1)
{
    /// <summary>The CRC32 written in hexadecimal by a DAT or computed from a file.</summary>
    public static bool TryParseCrc(string? crc, out uint value) =>
        uint.TryParse(crc, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out value);
}

/// <summary>
/// A DAT of a lot (<see cref="DatCatalog.Dats"/>): its name (the header name, unique in the lot, case ignored), its path
/// relative to the folder read, its summary, and its own catalog, which is the catalog of the DAT read alone. A DAT read
/// on demand (<see cref="DatFolderOptions.LoadOnDemand"/>) only has its summary until a run needs its catalog: it is then
/// read again (<see cref="Load"/>; <see cref="Catalog"/> reads it as well, once), and may be forgotten again
/// (<see cref="Unload"/>).
/// </summary>
public sealed class DatLotMember
{
    private readonly Func<CancellationToken, DatCatalog>? _load;
    private readonly Lock _gate = new();
    private DatCatalog? _catalog;
    private int _loadCount;

    /// <summary>A DAT whose catalog is read.</summary>
    public DatLotMember(string name, string relativePath, DatCatalog catalog)
    {
        Name = name;
        RelativePath = relativePath;
        _catalog = catalog;
        Summary = DatLotSummary.Of(catalog);
        Keys = DatLotKeys.Of(catalog);
    }

    /// <summary>A DAT read on demand: <paramref name="load"/> reads its catalog.</summary>
    public DatLotMember(string name, string relativePath, DatLotSummary summary, DatLotKeys keys, Func<CancellationToken, DatCatalog> load)
    {
        Name = name;
        RelativePath = relativePath;
        Summary = summary;
        Keys = keys;
        _load = load;
    }

    public string Name { get; }

    public string RelativePath { get; }

    /// <summary>Folder of the DAT relative to the folder read: empty for a DAT at its root.</summary>
    public string RelativeFolder => Path.GetDirectoryName(RelativePath) ?? string.Empty;

    public DatLotSummary Summary { get; }

    /// <summary>Header of the DAT, known without its catalog.</summary>
    public DatHeader Header => Summary.Header;

    /// <summary>Checksums of the DAT until the lot indexes them.</summary>
    internal DatLotKeys? Keys { get; set; }

    /// <summary>Whether the catalog of the DAT is in memory.</summary>
    public bool IsLoaded => Volatile.Read(ref _catalog) is not null;

    /// <summary>How many times the catalog was read again on demand (tests: a DAT no file needs is never read).</summary>
    internal int LoadCount => Volatile.Read(ref _loadCount);

    /// <summary>The catalog of the DAT, read on demand the first time.</summary>
    public DatCatalog Catalog => Load(CancellationToken.None);

    /// <summary>
    /// The catalog of the DAT, read on demand when it is not in memory. Several threads may ask for it: it is read once.
    /// </summary>
    public DatCatalog Load(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _catalog) is { } loaded)
        {
            return loaded;
        }

        lock (_gate)
        {
            if (_catalog is null)
            {
                var catalog = _load!(cancellationToken);
                Interlocked.Increment(ref _loadCount);
                Volatile.Write(ref _catalog, catalog);
            }

            return _catalog;
        }
    }

    /// <summary>
    /// Forgets the catalog of a DAT read on demand, which the next <see cref="Load"/> reads again; a DAT whose catalog was
    /// given cannot be forgotten. Only for a catalog nothing else holds: an entry of a catalog forgotten is not an entry
    /// of the catalog read again.
    /// </summary>
    public bool Unload()
    {
        if (_load is null)
        {
            return false;
        }

        lock (_gate)
        {
            Volatile.Write(ref _catalog, null);
            return true;
        }
    }
}

/// <summary>
/// The DAT a file is sorted with, as its target path needs it (<see cref="Abstractions.IPathOrganizerService.BuildTargetPath"/>):
/// its name, the value of <c>{dat}</c>, and, for a DAT of a lot (<see cref="IsLotMember"/>), its folder relative to the
/// folder of DATs read. Only a DAT of a lot gets a folder of its own in the destination (<see cref="SortConfig.FolderPerDat"/>),
/// under the folders of its path when they are mirrored (<see cref="SortConfig.DatFolderMirror"/>): a single DAT
/// changes nothing to the targets.
/// </summary>
public sealed record DatPlacement(string Name, string? RelativeFolder = null, bool IsLotMember = false);

/// <summary>
/// What a sort found of one DAT of a lot (<see cref="ProcessStats.PerDat"/>): its ROMs, those present (a file of the
/// source has their checksums, whatever DAT it is sorted with), those designated by the name of a CHD only, those
/// missing, and the games with at least one ROM present.
/// </summary>
public sealed record DatLotStats(
    string Name,
    NamingConvention Convention,
    int TotalRoms,
    int MatchedCount,
    int NameMatchedCount,
    int MissingCount,
    int GamesFound)
{
    /// <summary>Share of the ROMs of the DAT present, by checksum or by name, in percent.</summary>
    public double CompletionPct => TotalRoms > 0 ? (MatchedCount + NameMatchedCount) / (double)TotalRoms * 100.0 : 0.0;
}
