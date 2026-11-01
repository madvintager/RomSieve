using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Represents a candidate release during 1G1R arbitration.
/// </summary>
public sealed class RankedRelease
{
    public required string RelKey { get; init; }
    public required string EditionKey { get; init; }
    public required List<ProcessItem> Items { get; init; }
    public required ParsedRomName Sample { get; init; }
    public int? Rank { get; init; }
    public int? CountryRank { get; init; }

    /// <summary>Quality of the dump, the highest first (<see cref="Abstractions.IReleaseScoringService.GetDumpQualityRank"/>).</summary>
    public int DumpRank { get; init; }

    /// <summary>How close the release is to the original one, the highest first (<see cref="Abstractions.IReleaseScoringService.GetOriginalReleaseScore"/>).</summary>
    public int OriginalScore { get; init; }
    public required int[] VerTuple { get; init; }
    public required int[] RevTuple { get; init; }
    public (int Year, int Month, int Day) DateTuple { get; init; }
    public required string VideoStd { get; init; }
    public required string MediaCat { get; init; }
    public required string MediaExt { get; init; }
    public int MediaFidelity { get; init; }
    public bool IsMonolingual { get; init; }

    /// <summary>Whether a country of the release speaks its priority language, which separates two releases of the same language.</summary>
    public int RegionAffinity { get; init; }
    public int IsParent { get; init; }

    /// <summary>Rank of the MAME emulation status, the lowest first (<c>OneGameOneRomResolver.GetDriverRank</c>).</summary>
    public int DriverRank { get; init; }

    /// <summary>Rank of the release for an AGA Amiga, the lowest first (<see cref="AmigaChipsets.GetAgaRank"/>).</summary>
    public int AmigaRank { get; init; }
    public int Completeness { get; set; }
    public int IsCompleteMultidisk { get; set; } = 1;
    public int MinNameLen { get; init; }
    public required string FirstName { get; set; }
}
