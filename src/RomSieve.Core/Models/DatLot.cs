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
