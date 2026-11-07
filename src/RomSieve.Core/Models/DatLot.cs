using System.IO;

namespace RomSieve.Core.Models;

/// <summary>
/// How a folder of DATs is read (<see cref="Abstractions.IDatService.ParseDatFileAsync(string, DatFolderOptions, CancellationToken)"/>):
/// with its subfolders or not, and the DATs left out of the sort, by header name (case ignored; empty: every DAT). The
/// DATs left out are only read for their header. A folder of MAME software lists ignores both, as before lots existed.
/// </summary>
public sealed record DatFolderOptions(bool Recursive = false, IReadOnlyCollection<string>? ExcludedDats = null)
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
/// A DAT of a lot (<see cref="DatCatalog.Dats"/>): its name (the header name, unique in the lot, case ignored), its path
/// relative to the folder read, and its own catalog, which is the catalog of the DAT read alone.
/// </summary>
public sealed record DatLotMember(string Name, string RelativePath, DatCatalog Catalog)
{
    /// <summary>Folder of the DAT relative to the folder read: empty for a DAT at its root.</summary>
    public string RelativeFolder => Path.GetDirectoryName(RelativePath) ?? string.Empty;
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
