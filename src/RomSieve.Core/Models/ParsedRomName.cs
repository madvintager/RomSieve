using System.Text.RegularExpressions;

namespace RomSieve.Core.Models;

/// <summary>
/// What a MAME DAT says of a machine beyond its description, for the MAME filters and the 1G1R emulation criterion:
/// driver status (the worst of the emulation and of the features), emulation of the machine itself, features MAME
/// does not emulate, whether it needs an external artwork, its cabinet (screen and controls), unknown without it, its
/// category and its rating, from a category file and a rating file (no rating at all without the file), and whether it
/// has nothing to dump (no ROM nor disk, or only ones never dumped: MAME runs it without any file of its own).
/// </summary>
public sealed record MameMachineInfo(
    bool IsBios,
    bool IsDevice,
    bool IsMechanical,
    MameDriverStatus DriverStatus,
    MameDriverStatus EmulationStatus = MameDriverStatus.Unknown,
    MameFeatures UnemulatedFeatures = MameFeatures.None,
    bool RequiresArtwork = false,
    MameCabinet? Cabinet = null,
    MameCategory? Category = null,
    MameRating? Rating = null,
    bool HasNothingToDump = false);

/// <summary>
/// Screen and controls of a MAME machine (<c>&lt;display&gt;</c> and <c>&lt;input&gt;</c> of <c>mame -listxml</c>; the
/// DATs of SabreTools have none): orientation and type of its first screen, number of players (0 when the DAT does not
/// say), kinds of controls, and the most buttons of a control. Each unknown value never excludes the machine.
/// </summary>
public sealed record MameCabinet(
    MameOrientation Orientation,
    MameScreenType Screen,
    int Players,
    MameControls Controls,
    int Buttons);

/// <summary>
/// Unified, immutable representation of a parsed ROM filename (TOSEC, No-Intro, or Redump), shared by the three
/// conventions. Some fields only exist in one convention (<see cref="Publisher"/>, <see cref="RawOptParens"/> for
/// TOSEC; <see cref="RereleaseTag"/>, <see cref="TrackNumber"/> for No-Intro / Redump). The DAT enrichment
/// (<see cref="CloneOf"/>, <see cref="ParentTitle"/>, <see cref="IsMultidiskOverride"/>) and the multi-disk
/// inference (<see cref="DiscTotal"/>) produce a modified copy with a <c>with</c> expression.
/// </summary>
public sealed partial record ParsedRomName
{
    [GeneratedRegex(@"\b(\d+)\s+of\s+(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MediaOfTotalRegex();

    public required string RawFilename { get; init; }
    public string? FilePath { get; init; }
    public long FileSize { get; init; }
    public bool IsTosecConform { get; init; }

    // Level 1 fields
    public string BaseTitle { get; init; } = string.Empty;
    public string? Demo { get; init; }
    public string Date { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public string Extension { get; init; } = string.Empty;
    public string RawOptParens { get; init; } = string.Empty;
    public string RawBrackets { get; init; } = string.Empty;

    // Level 2 fields
    public string Title { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string? System { get; init; }
    public string? Video { get; init; }
    public string? Country { get; init; }
    public IReadOnlyList<string> CountryCodes { get; init; } = [];
    public string? Language { get; init; }
    public IReadOnlyList<string> LanguageCodes { get; init; } = [];
    public bool IsMultilingual { get; init; }
    public int? MultilingualCount { get; init; }
    public string? Copyright { get; init; }
    public string? DevStatus { get; init; }
    public string? MediaType { get; init; }
    public string? MediaLabel { get; init; }

    // Brackets classification
    public IReadOnlyList<string> SoftwareMods { get; init; } = [];
    public IReadOnlyList<string> DumpFlags { get; init; } = [];
    public IReadOnlyList<string> MoreInfo { get; init; } = [];

    // Boolean flags for quick filter checks
    public bool IsVerified { get; init; }
    public bool IsBadDump { get; init; }
    public bool IsAlternate { get; init; }
    public bool IsOverdump { get; init; }
    public bool IsUnderdump { get; init; }
    public bool IsVirus { get; init; }
    public bool IsCrack { get; init; }
    public bool IsFix { get; init; }
    public bool IsHack { get; init; }
    public bool IsModified { get; init; }
    public bool IsPirate { get; init; }
    public bool IsTrainer { get; init; }
    public bool IsTranslation { get; init; }
    public bool? IsMultidiskOverride { get; init; }

    // Multi-standard & DAT Parent/Clone fields (TOSEC, No-Intro, Redump)
    public NamingConvention Convention { get; init; } = NamingConvention.Tosec;
    public string? CloneOf { get; init; }
    public string? CloneOfId { get; init; }
    public string? ParentTitle { get; init; }
    public int? DiscNumber { get; init; }
    public int? DiscTotal { get; init; }
    public int? TrackNumber { get; init; }
    public string? RereleaseTag { get; init; }

    /// <summary>
    /// Game a release belongs to when the DAT says it explicitly: the parent set of a MAME machine. It takes part in
    /// the canonical game key, so that two games with the same title (the Tetris of Atari and the one of Sega) stay
    /// apart, while the title of the parent (<see cref="ParentTitle"/>) gathers its clones.
    /// </summary>
    public string? GameGroup { get; init; }

    /// <summary>
    /// Set by the pipeline for a collection sorted for the AGA Amigas (<see cref="FilterConfig.AmigaTarget"/>): the
    /// chipset mark of the system field (<c>(AGA)</c>, <c>(OCS-AGA)</c>...) leaves the canonical game key, so that the AGA
    /// and OCS versions of a game are one game for the 1G1R, which prefers the AGA one.
    /// </summary>
    public bool MergesAmigaChipsets { get; init; }

    /// <summary>What the MAME DAT says of the machine (BIOS, device, mechanical, emulation status); null for any other file.</summary>
    public MameMachineInfo? Machine { get; init; }

    /// <summary>
    /// How well MAME supports a software of a software list (<c>supported</c> of its <c>hash</c> file: good, imperfect
    /// for <c>partial</c>, preliminary for <c>no</c>); <see cref="MameDriverStatus.Unknown"/> when the DAT does not say.
    /// </summary>
    public MameDriverStatus SoftwareSupport { get; init; }

    public IReadOnlyList<string> RelatedFiles { get; init; } = [];

    public bool IsRerelease => !string.IsNullOrEmpty(RereleaseTag);

    public bool IsConform => IsTosecConform;

    public bool IsOverUnder => IsOverdump || IsUnderdump;

    /// <summary>
    /// Indicates whether the item is part of a multi-media release (disks/discs/sides/tapes).
    /// Priority 1: Ground truth certified by DAT (<see cref="IsMultidiskOverride"/>).
    /// Priority 2: Heuristic lexical analysis on <see cref="MediaLabel"/> or <see cref="MediaType"/>.
    /// </summary>
    public bool IsMultiMedia
    {
        get
        {
            if (IsMultidiskOverride.HasValue)
            {
                return IsMultidiskOverride.Value;
            }

            var label = !string.IsNullOrEmpty(MediaLabel) ? MediaLabel : MediaType;
            if (string.IsNullOrEmpty(label))
            {
                return false;
            }

            var match = MediaOfTotalRegex().Match(label);
            if (match.Success && int.TryParse(match.Groups[2].ValueSpan, out var total))
            {
                return total > 1;
            }

            return true;
        }
    }

    public string Year
    {
        get
        {
            if (string.IsNullOrEmpty(Date))
            {
                return "Unknown";
            }

            return Date.Length >= 4 ? Date[..4] : Date;
        }
    }

    public string Letter
    {
        get
        {
            if (string.IsNullOrEmpty(Title))
            {
                return "#";
            }

            var firstChar = char.ToUpperInvariant(Title[0]);
            return firstChar is >= 'A' and <= 'Z' ? firstChar.ToString() : "#";
        }
    }

    public string DumpStatusSummary
    {
        get
        {
            if (IsBadDump) return "BadDump";
            if (IsVirus) return "Virus";
            if (IsOverUnder) return "OverUnderDump";
            if (IsVerified) return "Verified";
            if (IsCrack) return "Cracked";
            if (IsHack) return "Hack";
            if (IsPirate) return "Pirate";
            if (IsTrainer) return "Trainer";
            if (IsAlternate) return "Alternate";
            if (IsTranslation) return "Translated";
            if (IsFix) return "Fixed";
            if (IsModified) return "Modified";
            return "Clean";
        }
    }
}
