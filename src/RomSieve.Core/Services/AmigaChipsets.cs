using System.Text.RegularExpressions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Chipset an Amiga release needs, read from its TOSEC name: the system field (<c>(AGA)</c>, <c>(OCS-AGA)</c>,
/// <c>(ECS-AGA)</c>, or a model such as <c>(A1200)</c>) and the fixes for the AGA machines (<c>[f AGA NOMAD]</c>).
/// A name without a mark is presumed OCS / ECS: it is never excluded by a target machine.
/// </summary>
public static partial class AmigaChipsets
{
    // A fix made for the AGA machines: "[f AGA NOMAD]", "[f A1200 TFA]", "[f loader CSL - AGA NOMAD]"
    [GeneratedRegex(@"^\[f\b[^\]]*\b(?:AGA|A1200|A4000T?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AgaFixRegex();

    /// <summary>Folder of <c>{chipset}</c> for a release that only runs on an AGA Amiga.</summary>
    public const string AgaFolder = "AGA";

    /// <summary>Folder of <c>{chipset}</c> for a release that runs on an OCS or ECS Amiga as on an AGA one.</summary>
    public const string BothFolder = "OCS-AGA";

    /// <summary>Folder of <c>{chipset}</c> for any other release, presumed OCS / ECS.</summary>
    public const string OcsEcsFolder = "OCS-ECS";

    /// <summary>Chipset named by the system field of a TOSEC name; any other value gives <see cref="AmigaChipset.Unmarked"/>.</summary>
    public static AmigaChipset FromSystem(string? system) =>
        system?.Trim().ToUpperInvariant() switch
        {
            "AGA" or "A1200" or "A4000" or "A4000T" or "CD32" => AmigaChipset.Aga,
            "OCS-AGA" => AmigaChipset.OcsAga,
            "ECS-AGA" => AmigaChipset.EcsAga,
            "ECS" or "A500+" or "A600" or "A3000" or "A3000T" => AmigaChipset.Ecs,
            "OCS" or "A500" or "A1000" or "A2000" or "A2500" or "CDTV" => AmigaChipset.Ocs,
            _ => AmigaChipset.Unmarked
        };

    /// <summary>Chipset of a parsed name.</summary>
    public static AmigaChipset Of(ParsedRomName parsed) => FromSystem(parsed.System);

    /// <summary>Whether the release was fixed by a group to run on an AGA Amiga (<c>[f AGA ...]</c>).</summary>
    public static bool IsAgaFix(ParsedRomName parsed) => parsed.SoftwareMods.Any(mod => AgaFixRegex().IsMatch(mod));

    /// <summary>
    /// Whether a release of <paramref name="chipset"/> runs on the machines of <paramref name="target"/>. An AGA Amiga
    /// runs most OCS / ECS releases, and an unmarked release is presumed OCS / ECS: neither is ever excluded.
    /// </summary>
    public static bool RunsOn(AmigaChipset chipset, AmigaTarget target) =>
        target switch
        {
            AmigaTarget.Ocs => chipset is AmigaChipset.Unmarked or AmigaChipset.Ocs or AmigaChipset.OcsAga,
            AmigaTarget.Ecs => chipset is not AmigaChipset.Aga,
            _ => true
        };

    /// <summary>
    /// Rank of a release for an AGA Amiga, the lowest first: AGA, then OCS / ECS and AGA, then fixed for AGA, then any
    /// other release.
    /// </summary>
    public static int GetAgaRank(ParsedRomName parsed) =>
        Of(parsed) switch
        {
            AmigaChipset.Aga => 0,
            AmigaChipset.OcsAga or AmigaChipset.EcsAga => 1,
            _ when IsAgaFix(parsed) => 2,
            _ => 3
        };

    /// <summary>Kind of a release for an AGA Amiga, as <see cref="GetAgaRank"/> orders them, translated at display.</summary>
    public static LocalizedMessage DescribeForAga(ParsedRomName parsed) =>
        LocalizedMessage.Create(GetAgaRank(parsed) switch
        {
            0 => "amiga.chipset_aga",
            1 => "amiga.chipset_both",
            2 => "amiga.chipset_aga_fix",
            _ => "amiga.chipset_ocs_ecs"
        });

    /// <summary>
    /// Folder of the <c>{chipset}</c> variable: <see cref="AgaFolder"/>, <see cref="BothFolder"/> (OCS / ECS and AGA,
    /// or fixed for AGA) or <see cref="OcsEcsFolder"/> (any other release, a name without a mark included).
    /// </summary>
    public static string GetFolderName(ParsedRomName parsed) =>
        GetAgaRank(parsed) switch
        {
            0 => AgaFolder,
            1 or 2 => BothFolder,
            _ => OcsEcsFolder
        };
}
