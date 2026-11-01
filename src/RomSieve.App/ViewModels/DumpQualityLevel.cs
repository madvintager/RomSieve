namespace RomSieve.App.ViewModels;

/// <summary>
/// Dump quality requirement of a convention, a single choice that sums up its integrity options
/// ("verified dumps only" and the bad, alternate, over/under and virus exclusions). It is only a presentation of those
/// options: it is never saved, and reading a configuration never changes them.
/// </summary>
public enum DumpQualityLevel
{
    /// <summary>No integrity filter: every dump is kept.</summary>
    All,

    /// <summary>The integrity exclusions have their default values (bad dumps excluded, alternates kept).</summary>
    NoBadDumps,

    /// <summary>Only verified dumps are kept; the exclusions are then implied.</summary>
    VerifiedOnly,

    /// <summary>A combination of the integrity options that matches no other level.</summary>
    Custom
}
