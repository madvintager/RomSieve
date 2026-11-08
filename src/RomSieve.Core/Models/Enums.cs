namespace RomSieve.Core.Models;

/// <summary>
/// File transfer mode to the target destination.
/// </summary>
public enum TransferMode
{
    Copy,
    Move,
    HardLink,
    SymbolicLink,
    /// <summary>
    /// Copy into a ZIP archive per item (a file, or an optical descriptor with its tracks), written deterministically:
    /// entries sorted by name, fixed date, maximal Deflate compression. The sources are left untouched.
    /// </summary>
    Zip
}

/// <summary>
/// File name collision resolution mode.
/// </summary>
public enum CollisionMode
{
    Skip,
    Overwrite,
    Rename
}

/// <summary>
/// Processing status of a ROM file.
/// </summary>
public enum FileStatus
{
    Valid,
    Excluded,
    Unmatched,
    Duplicate,
    Error
}

/// <summary>
/// Why the identification set a file aside whatever its name (<see cref="ProcessItem.IdentificationFailure"/>).
/// </summary>
public enum IdentificationFailure
{
    /// <summary>Identified by the DAT, or sorted by its name without a DAT.</summary>
    None,

    /// <summary>CRC32 (and size) unknown to the DAT (<c>reason.unmatched_not_in_dat</c>).</summary>
    NotInDat,

    /// <summary>CRC32 of the DAT, but another SHA-1 (<c>reason.sha1_mismatch</c>).</summary>
    Sha1Mismatch,

    /// <summary>The file, or a track of its set, is damaged in its archive, or the archive is unreadable.</summary>
    DamagedArchive,

    /// <summary>Damaged or incomplete CHD (<c>reason.chd_invalid</c>).</summary>
    InvalidChd
}

/// <summary>
/// Supported naming convention.
/// </summary>
public enum NamingConvention
{
    Auto,
    Tosec,
    NoIntro,
    Redump,

    /// <summary>
    /// MAME sets: an archive named by the short name of a machine (<c>sf2.zip</c>) is one item, identified and described
    /// by a MAME DAT (<see cref="Services.MameSetIdentifier"/>), which this convention requires. A file name alone is
    /// parsed as with <see cref="Auto"/>.
    /// </summary>
    Mame,

    /// <summary>
    /// Software of a MAME software list (<c>megadriv/sonic.zip</c>): an archive named by the short name of a software is
    /// one item, identified and described by the DAT of its list (a SabreTools DAT, or <c>hash/&lt;list&gt;.xml</c> of
    /// MAME), which this convention requires, and sorted into the folder of its list, where MAME looks for it. A file
    /// name alone is parsed as with <see cref="Auto"/>.
    /// </summary>
    MameSoftwareList
}

/// <summary>
/// Dump status of a ROM or a disk in a DAT (<c>status</c> attribute of MAME-style DATs, <c>flags</c> in CLRMAMEPRO).
/// </summary>
public enum DatRomStatus
{
    Good,

    /// <summary>Known to be a bad dump: listed with its (wrong) checksums.</summary>
    BadDump,

    /// <summary>Never dumped: no checksum, so it can neither be identified nor be missing.</summary>
    NoDump,

    Verified
}

/// <summary>
/// Driver statuses whose MAME machines are kept (<c>mame_driver_statuses</c>), any combination: the good ones only, the
/// imperfect ones only, the preliminary ones only, and so on. A machine without driver information (a device, a DAT
/// without it) is always kept.
/// </summary>
[Flags]
public enum MameDriverStatuses
{
    None = 0,
    Good = 1,
    Imperfect = 2,
    Preliminary = 4,
    All = Good | Imperfect | Preliminary
}

/// <summary>
/// Orientation of the screen of a MAME machine (<c>rotate</c> of its first <c>&lt;display&gt;</c>): vertical when it is
/// turned by 90 or 270 degrees. Unknown without a display (devices, DATs without it).
/// </summary>
public enum MameOrientation
{
    Unknown,
    Horizontal,
    Vertical
}

/// <summary>
/// Screen of a MAME machine (<c>type</c> of its first <c>&lt;display&gt;</c>). The LCD and SVG screens of handheld
/// electronic games are both <see cref="Lcd"/>.
/// </summary>
public enum MameScreenType
{
    Unknown,
    Raster,
    Vector,
    Lcd
}

/// <summary>
/// Orientation of the screen a MAME machine needs to be kept (<c>mame_orientation</c>). A machine whose orientation is
/// unknown is always kept.
/// </summary>
public enum MameOrientationRequirement
{
    Any,
    Horizontal,
    Vertical
}

/// <summary>
/// Kinds of controls of a MAME machine (<c>&lt;control type="..."/&gt;</c> of <c>mame -listxml</c>), grouped as a
/// control panel offers them; buttons alone and unknown types belong to none.
/// </summary>
[Flags]
public enum MameControls
{
    None = 0,

    /// <summary>Digital joysticks: <c>joy</c>, <c>doublejoy</c>, <c>triplejoy</c>.</summary>
    Joystick = 1 << 0,

    /// <summary>Analog stick (flight stick): <c>stick</c>.</summary>
    AnalogStick = 1 << 1,

    /// <summary><c>trackball</c> and <c>mouse</c>.</summary>
    Trackball = 1 << 2,

    /// <summary>Spinner: <c>dial</c>.</summary>
    Spinner = 1 << 3,

    /// <summary>Paddle or steering wheel: <c>paddle</c>, <c>positional</c>.</summary>
    Paddle = 1 << 4,

    Pedal = 1 << 5,
    Lightgun = 1 << 6,

    /// <summary><c>keyboard</c> and <c>keypad</c>.</summary>
    Keyboard = 1 << 7,

    /// <summary>Mahjong, hanafuda and gambling panels.</summary>
    GamblingPanel = 1 << 8
}

/// <summary>
/// Features of a MAME machine (<c>&lt;feature type="..."/&gt;</c> of <c>mame -listxml</c>), as flags; unknown types are
/// ignored.
/// </summary>
[Flags]
public enum MameFeatures
{
    None = 0,
    Protection = 1 << 0,
    Timing = 1 << 1,
    Graphics = 1 << 2,
    Palette = 1 << 3,
    Sound = 1 << 4,
    Capture = 1 << 5,
    Camera = 1 << 6,
    Microphone = 1 << 7,
    Controls = 1 << 8,
    Keyboard = 1 << 9,
    Mouse = 1 << 10,
    Media = 1 << 11,
    Disk = 1 << 12,
    Printer = 1 << 13,
    Tape = 1 << 14,
    Punch = 1 << 15,
    Drum = 1 << 16,
    Rom = 1 << 17,
    Comms = 1 << 18,
    Lan = 1 << 19,
    Wan = 1 << 20
}

/// <summary>
/// Emulation status of a MAME driver (<c>&lt;driver status="..."/&gt;</c>), from the best to the worst.
/// </summary>
public enum MameDriverStatus
{
    /// <summary>No driver element (devices, DATs without driver information).</summary>
    Unknown,
    Good,
    Imperfect,
    Preliminary
}

/// <summary>
/// Placement location of generated .m3u playlists for multi-disc games.
/// </summary>
public enum M3uPlacementMode
{
    /// <summary>At the parent level (pattern root / system folder), pointing to Subfolder/File.</summary>
    Parent,
    /// <summary>At the parent level, with hidden subfolder .Subfolder/File.</summary>
    ParentHidden,
    /// <summary>Inside the game subfolder, pointing directly to File.</summary>
    Subfolder
}

/// <summary>
/// Relationship between the source and destination directories of a sort.
/// </summary>
public enum PathOverlap
{
    /// <summary>The directories are independent.</summary>
    None,
    /// <summary>The destination is the source directory itself.</summary>
    Identical,
    /// <summary>The destination contains the source: purging it would delete the source.</summary>
    DestinationContainsSource,
    /// <summary>The destination is inside the source and would be re-scanned by a recursive scan.</summary>
    DestinationInsideSource
}

/// <summary>
/// Operation log level.
/// </summary>
public enum LogLevel
{
    Ok,
    Skip,
    Unmatched,
    Info,
    Warn,
    Error,
    /// <summary>
    /// What a simulation would write (a file kept or isolated in <c>_unmatched</c>, a playlist): nothing is written, unlike
    /// <see cref="Ok"/>, which a real sort only uses for what it has written.
    /// </summary>
    Planned
}

/// <summary>
/// Order of the language and country criteria in the 1G1R cascade and the region filters.
/// </summary>
public enum RegionPriorityMode
{
    LanguageFirst,
    CountryFirst
}

/// <summary>
/// Video standard preferred by the 1G1R cascade.
/// </summary>
public enum VideoPreference
{
    None,
    Pal,
    Ntsc
}

/// <summary>
/// Media category preferred by the 1G1R cascade (TOSEC only).
/// </summary>
public enum MediaPreference
{
    None,
    Floppy,
    Tape,
    Cartridge,
    Cd,
    Hd
}

/// <summary>
/// Output format of a DAT export.
/// </summary>
public enum DatExportFormat
{
    /// <summary>Logiqx XML.</summary>
    LogiqxXml,
    /// <summary>CLRMAMEPRO text format.</summary>
    ClrMamePro,
    /// <summary>CLRMAMEPRO for a <c>.dat</c> path (except <c>.xml.dat</c>), Logiqx XML otherwise.</summary>
    Auto
}

/// <summary>
/// Color theme of the user interface (<c>config.json</c> key <c>ui_theme</c>).
/// </summary>
public enum UiTheme
{
    Light,
    Dark,
    /// <summary>Follows the theme of the operating system.</summary>
    System
}

/// <summary>
/// Chipset an Amiga release needs, read from the system field of its TOSEC name (<c>(AGA)</c>, <c>(OCS-AGA)</c>,
/// <c>(A1200)</c>...). A name without such a mark is <see cref="Unmarked"/>: presumed OCS / ECS, without certainty.
/// </summary>
public enum AmigaChipset
{
    Unmarked,
    Ocs,
    Ecs,
    Aga,

    /// <summary>Runs on an OCS Amiga as on an AGA one.</summary>
    OcsAga,

    /// <summary>Runs on an ECS Amiga as on an AGA one.</summary>
    EcsAga
}

/// <summary>
/// Amiga the collection is sorted for (<c>config.json</c> key <c>amiga_target</c>): the releases that need a later
/// chipset are excluded, and with the AGA machines the AGA and OCS versions of a game are one game for the 1G1R.
/// </summary>
public enum AmigaTarget
{
    /// <summary>Every machine: nothing is excluded, the chipsets stay apart.</summary>
    Any,

    /// <summary>Amiga 500, 1000, 2000 (OCS).</summary>
    Ocs,

    /// <summary>Amiga 500+, 600, 3000 (ECS).</summary>
    Ecs,

    /// <summary>Amiga 1200, 4000, CD32 (AGA).</summary>
    Aga
}

public static class EnumExtensions
{
    public static UiTheme ParseUiTheme(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "dark" => UiTheme.Dark,
            "system" => UiTheme.System,
            _ => UiTheme.Light
        };

    public static string ToConfigString(this UiTheme theme) =>
        theme switch
        {
            UiTheme.Dark => "dark",
            UiTheme.System => "system",
            _ => "light"
        };

    // Stable names of the 1G1R criteria in config.json ("tiebreak_order_*"): never change one
    private static readonly (TiebreakCriterion Criterion, string Name)[] TiebreakCriterionNames =
    [
        (TiebreakCriterion.LanguageRank, "language"),
        (TiebreakCriterion.CompleteMultidiskSet, "complete_multidisk_set"),
        (TiebreakCriterion.AmigaChipset, "amiga_chipset"),
        (TiebreakCriterion.GoodDump, "good_dump"),
        (TiebreakCriterion.OriginalRelease, "original_release"),
        (TiebreakCriterion.DriverStatus, "driver_status"),
        (TiebreakCriterion.CountryRank, "country"),
        (TiebreakCriterion.VideoStandard, "video"),
        (TiebreakCriterion.PreferredMediaType, "media_type"),
        (TiebreakCriterion.MediaFidelity, "media_fidelity"),
        (TiebreakCriterion.LanguageCount, "language_count"),
        (TiebreakCriterion.RegionAffinity, "region_affinity"),
        (TiebreakCriterion.Version, "version"),
        (TiebreakCriterion.Revision, "revision"),
        (TiebreakCriterion.Date, "date"),
        (TiebreakCriterion.Completeness, "completeness"),
        (TiebreakCriterion.ParentClone, "parent_clone"),
        (TiebreakCriterion.Name, "name")
    ];

    public static string ToConfigString(this TiebreakCriterion criterion) =>
        TiebreakCriterionNames.FirstOrDefault(c => c.Criterion == criterion).Name ?? "none";

    /// <summary>Reads a criterion of a 1G1R order; an unknown name gives <see langword="false"/>.</summary>
    public static bool TryParseTiebreakCriterion(string? value, out TiebreakCriterion criterion)
    {
        var name = value?.Trim().ToLowerInvariant();
        foreach (var (candidate, candidateName) in TiebreakCriterionNames)
        {
            if (candidateName == name)
            {
                criterion = candidate;
                return true;
            }
        }

        criterion = TiebreakCriterion.None;
        return false;
    }

    /// <summary>Reads a 1G1R order of <c>config.json</c>; unknown names are ignored.</summary>
    public static IEnumerable<TiebreakCriterion> ParseTiebreakOrder(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (TryParseTiebreakCriterion(name, out var criterion) && criterion != TiebreakCriterion.None)
            {
                yield return criterion;
            }
        }
    }

    public static RegionPriorityMode ParseRegionPriorityMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "country_first" => RegionPriorityMode.CountryFirst,
            _ => RegionPriorityMode.LanguageFirst
        };

    public static string ToConfigString(this RegionPriorityMode mode) =>
        mode switch
        {
            RegionPriorityMode.CountryFirst => "country_first",
            _ => "lang_first"
        };

    public static VideoPreference ParseVideoPreference(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "pal" => VideoPreference.Pal,
            "ntsc" => VideoPreference.Ntsc,
            _ => VideoPreference.None
        };

    /// <summary>Serialized value, also matched against the video tags of file names (<c>pal</c>, <c>ntsc</c>).</summary>
    public static string ToConfigString(this VideoPreference video) =>
        video switch
        {
            VideoPreference.Pal => "pal",
            VideoPreference.Ntsc => "ntsc",
            _ => "none"
        };

    public static MediaPreference ParseMediaPreference(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "floppy" => MediaPreference.Floppy,
            "tape" => MediaPreference.Tape,
            "cartridge" => MediaPreference.Cartridge,
            "cd" => MediaPreference.Cd,
            "hd" => MediaPreference.Hd,
            _ => MediaPreference.None
        };

    /// <summary>Serialized value, identical to the media category returned by <c>IRomFilterService.GetMediaCategory</c>.</summary>
    public static string ToConfigString(this MediaPreference media) =>
        media switch
        {
            MediaPreference.Floppy => "floppy",
            MediaPreference.Tape => "tape",
            MediaPreference.Cartridge => "cartridge",
            MediaPreference.Cd => "cd",
            MediaPreference.Hd => "hd",
            _ => "none"
        };

    public static DatExportFormat ParseDatExportFormat(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "clrmamepro" or "cmp" => DatExportFormat.ClrMamePro,
            "auto" => DatExportFormat.Auto,
            _ => DatExportFormat.LogiqxXml
        };

    public static string ToConfigString(this DatExportFormat format) =>
        format switch
        {
            DatExportFormat.ClrMamePro => "clrmamepro",
            DatExportFormat.Auto => "auto",
            _ => "xml"
        };

    /// <summary>Tag displayed in the log panel (<c>INFO</c>, <c>OK</c>, <c>WARN</c>, <c>PLANNED</c>...).</summary>
    public static string ToTag(this LogLevel level) =>
        level switch
        {
            LogLevel.Ok => "OK",
            LogLevel.Skip => "SKIP",
            LogLevel.Unmatched => "UNMATCHED",
            LogLevel.Warn => "WARN",
            LogLevel.Error => "ERROR",
            LogLevel.Planned => "PLANNED",
            _ => "INFO"
        };

    public static TransferMode ParseTransferMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "move" => TransferMode.Move,
            "hardlink" => TransferMode.HardLink,
            "symlink" => TransferMode.SymbolicLink,
            "zip" => TransferMode.Zip,
            _ => TransferMode.Copy
        };

    public static string ToConfigString(this TransferMode mode) =>
        mode switch
        {
            TransferMode.Move => "move",
            TransferMode.HardLink => "hardlink",
            TransferMode.SymbolicLink => "symlink",
            TransferMode.Zip => "zip",
            _ => "copy"
        };

    public static CollisionMode ParseCollisionMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "skip" => CollisionMode.Skip,
            "overwrite" => CollisionMode.Overwrite,
            _ => CollisionMode.Rename
        };

    public static string ToConfigString(this CollisionMode mode) =>
        mode switch
        {
            CollisionMode.Skip => "skip",
            CollisionMode.Overwrite => "overwrite",
            _ => "rename"
        };

    public static NamingConvention ParseNamingConvention(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "tosec" => NamingConvention.Tosec,
            "nointro" => NamingConvention.NoIntro,
            "redump" => NamingConvention.Redump,
            "mame" => NamingConvention.Mame,
            "mame_softlist" => NamingConvention.MameSoftwareList,
            _ => NamingConvention.Auto
        };

    public static string ToConfigString(this NamingConvention convention) =>
        convention switch
        {
            NamingConvention.Tosec => "tosec",
            NamingConvention.NoIntro => "nointro",
            NamingConvention.Redump => "redump",
            NamingConvention.Mame => "mame",
            NamingConvention.MameSoftwareList => "mame_softlist",
            _ => "auto"
        };

    /// <summary>
    /// Whether the files of a convention use the No-Intro / Redump options (integrity, modifications, complete sets)
    /// rather than the TOSEC ones. MAME sets and the software of MAME lists use them too, until they have options of
    /// their own.
    /// </summary>
    public static bool UsesNoIntroSettings(this NamingConvention convention) =>
        convention is NamingConvention.NoIntro or NamingConvention.Redump or NamingConvention.Mame or NamingConvention.MameSoftwareList;

    /// <summary>
    /// Whether a convention sorts whole archives that a DAT identifies (MAME sets, software of MAME lists): each archive
    /// is one item, transferred as it is (<see cref="Services.MameSetIdentifier"/>).
    /// </summary>
    public static bool IsMameSets(this NamingConvention convention) =>
        convention is NamingConvention.Mame or NamingConvention.MameSoftwareList;

    /// <summary>
    /// Dump status of a DAT <c>status</c> attribute (or CLRMAMEPRO <c>flags</c>); unknown values are good dumps.
    /// </summary>
    public static DatRomStatus ParseDatRomStatus(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "baddump" => DatRomStatus.BadDump,
            "nodump" => DatRomStatus.NoDump,
            "verified" => DatRomStatus.Verified,
            _ => DatRomStatus.Good
        };

    public static string ToDatString(this DatRomStatus status) =>
        status switch
        {
            DatRomStatus.BadDump => "baddump",
            DatRomStatus.NoDump => "nodump",
            DatRomStatus.Verified => "verified",
            _ => "good"
        };

    /// <summary>Driver statuses that can be kept, in the order they are shown and saved.</summary>
    public static readonly IReadOnlyList<MameDriverStatuses> MameDriverStatusKinds =
        [MameDriverStatuses.Good, MameDriverStatuses.Imperfect, MameDriverStatuses.Preliminary];

    public static string ToConfigString(this MameDriverStatuses kind) =>
        kind switch
        {
            MameDriverStatuses.Good => "good",
            MameDriverStatuses.Imperfect => "imperfect",
            _ => "preliminary"
        };

    /// <summary>Names of the statuses kept, as <c>config.json</c> lists them.</summary>
    public static List<string> ToConfigStrings(this MameDriverStatuses statuses) =>
        MameDriverStatusKinds.Where(kind => statuses.HasFlag(kind)).Select(kind => kind.ToConfigString()).ToList();

    /// <summary>Statuses named in <c>config.json</c>; unknown names are ignored.</summary>
    public static MameDriverStatuses ParseMameDriverStatuses(IEnumerable<string> names)
    {
        var statuses = MameDriverStatuses.None;
        foreach (var name in names)
        {
            statuses |= MameDriverStatusKinds.FirstOrDefault(kind =>
                string.Equals(kind.ToConfigString(), name?.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return statuses;
    }

    /// <summary>
    /// Whether a machine with this driver status (<c>status</c>, the worst of its emulation and of its features) and
    /// this emulation status (<c>emulation</c>, the machine itself) is kept. An unknown driver status always is. With
    /// <paramref name="keepPlayable"/>, so is a playable machine whatever its driver status: one whose own emulation is
    /// imperfect or better (a machine without sound, for instance, is preliminary but playable).
    /// </summary>
    public static bool Keeps(this MameDriverStatuses statuses, bool keepPlayable, MameDriverStatus status, MameDriverStatus emulation) =>
        status == MameDriverStatus.Unknown ||
        statuses.HasFlag(status switch
        {
            MameDriverStatus.Good => MameDriverStatuses.Good,
            MameDriverStatus.Imperfect => MameDriverStatuses.Imperfect,
            _ => MameDriverStatuses.Preliminary
        }) ||
        (keepPlayable && emulation is MameDriverStatus.Good or MameDriverStatus.Imperfect);

    /// <summary>Name of an emulation status, translated at display.</summary>
    public static LocalizedMessage ToMessage(this MameDriverStatus status) =>
        LocalizedMessage.Create(status switch
        {
            MameDriverStatus.Good => "mame.driver_good",
            MameDriverStatus.Imperfect => "mame.driver_imperfect",
            MameDriverStatus.Preliminary => "mame.driver_preliminary",
            _ => "mame.driver_unknown"
        });

    public static MameDriverStatus ParseMameDriverStatus(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "good" => MameDriverStatus.Good,
            "imperfect" => MameDriverStatus.Imperfect,
            "preliminary" => MameDriverStatus.Preliminary,
            _ => MameDriverStatus.Unknown
        };

    /// <summary>Kinds of controls a MAME panel may offer, in the order they are shown and saved.</summary>
    public static readonly IReadOnlyList<MameControls> MameControlKinds =
    [
        MameControls.Joystick,
        MameControls.AnalogStick,
        MameControls.Trackball,
        MameControls.Spinner,
        MameControls.Paddle,
        MameControls.Pedal,
        MameControls.Lightgun,
        MameControls.Keyboard,
        MameControls.GamblingPanel
    ];

    /// <summary>Kind of a MAME <c>&lt;control type="..."/&gt;</c>; buttons alone and unknown types give none.</summary>
    public static MameControls ParseMameControlType(string? type) =>
        type?.Trim().ToLowerInvariant() switch
        {
            "joy" or "doublejoy" or "triplejoy" => MameControls.Joystick,
            "stick" => MameControls.AnalogStick,
            "trackball" or "mouse" => MameControls.Trackball,
            "dial" => MameControls.Spinner,
            "paddle" or "positional" => MameControls.Paddle,
            "pedal" => MameControls.Pedal,
            "lightgun" => MameControls.Lightgun,
            "keyboard" or "keypad" => MameControls.Keyboard,
            "mahjong" or "hanafuda" or "gambling" => MameControls.GamblingPanel,
            _ => MameControls.None
        };

    /// <summary>Name of a kind of controls in <c>config.json</c> (<c>mame_excluded_controls</c>).</summary>
    public static string ToConfigString(this MameControls kind) =>
        kind switch
        {
            MameControls.Joystick => "joystick",
            MameControls.AnalogStick => "analog_stick",
            MameControls.Trackball => "trackball",
            MameControls.Spinner => "spinner",
            MameControls.Paddle => "paddle",
            MameControls.Pedal => "pedal",
            MameControls.Lightgun => "lightgun",
            MameControls.Keyboard => "keyboard",
            MameControls.GamblingPanel => "gambling_panel",
            _ => string.Empty
        };

    /// <summary>Names of the kinds of controls a set holds, in the order of <see cref="MameControlKinds"/>.</summary>
    public static List<string> ToConfigStrings(this MameControls controls) =>
        MameControlKinds.Where(kind => controls.HasFlag(kind)).Select(kind => kind.ToConfigString()).ToList();

    /// <summary>Kinds of controls named in <c>config.json</c>; unknown names are ignored.</summary>
    public static MameControls ParseMameControls(IEnumerable<string> names)
    {
        var controls = MameControls.None;
        foreach (var name in names)
        {
            controls |= MameControlKinds.FirstOrDefault(kind =>
                string.Equals(kind.ToConfigString(), name?.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return controls;
    }

    /// <summary>Name of a kind of controls, translated at display.</summary>
    public static LocalizedMessage ToMessage(this MameControls kind) =>
        LocalizedMessage.Create(kind switch
        {
            MameControls.Joystick => "mame.control_joystick",
            MameControls.AnalogStick => "mame.control_analog_stick",
            MameControls.Trackball => "mame.control_trackball",
            MameControls.Spinner => "mame.control_spinner",
            MameControls.Paddle => "mame.control_paddle",
            MameControls.Pedal => "mame.control_pedal",
            MameControls.Lightgun => "mame.control_lightgun",
            MameControls.Keyboard => "mame.control_keyboard",
            _ => "mame.control_gambling_panel"
        });

    public static MameScreenType ParseMameScreenType(string? type) =>
        type?.Trim().ToLowerInvariant() switch
        {
            "raster" => MameScreenType.Raster,
            "vector" => MameScreenType.Vector,
            "lcd" or "svg" => MameScreenType.Lcd,
            _ => MameScreenType.Unknown
        };

    /// <summary>Name of a screen, translated at display.</summary>
    public static LocalizedMessage ToMessage(this MameScreenType screen) =>
        LocalizedMessage.Create(screen switch
        {
            MameScreenType.Raster => "mame.screen_raster",
            MameScreenType.Vector => "mame.screen_vector",
            MameScreenType.Lcd => "mame.screen_lcd",
            _ => "mame.screen_unknown"
        });

    /// <summary>Name of an orientation, translated at display.</summary>
    public static LocalizedMessage ToMessage(this MameOrientation orientation) =>
        LocalizedMessage.Create(orientation switch
        {
            MameOrientation.Horizontal => "mame.orientation_horizontal",
            MameOrientation.Vertical => "mame.orientation_vertical",
            _ => "mame.orientation_unknown"
        });

    /// <summary>
    /// Orientation a MAME machine needs to be kept (<c>mame_orientation</c>); unknown values give
    /// <see cref="MameOrientationRequirement.Any"/>.
    /// </summary>
    public static MameOrientationRequirement ParseMameOrientationRequirement(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "horizontal" => MameOrientationRequirement.Horizontal,
            "vertical" => MameOrientationRequirement.Vertical,
            _ => MameOrientationRequirement.Any
        };

    public static string ToConfigString(this MameOrientationRequirement requirement) =>
        requirement switch
        {
            MameOrientationRequirement.Horizontal => "horizontal",
            MameOrientationRequirement.Vertical => "vertical",
            _ => "any"
        };

    /// <summary>Amiga the collection is sorted for (<c>amiga_target</c>); unknown values give <see cref="AmigaTarget.Any"/>.</summary>
    public static AmigaTarget ParseAmigaTarget(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "ocs" => AmigaTarget.Ocs,
            "ecs" => AmigaTarget.Ecs,
            "aga" => AmigaTarget.Aga,
            _ => AmigaTarget.Any
        };

    public static string ToConfigString(this AmigaTarget target) =>
        target switch
        {
            AmigaTarget.Ocs => "ocs",
            AmigaTarget.Ecs => "ecs",
            AmigaTarget.Aga => "aga",
            _ => "any"
        };

    /// <summary>Name of the machines of a target, translated at display.</summary>
    public static LocalizedMessage ToMessage(this AmigaTarget target) =>
        LocalizedMessage.Create(target switch
        {
            AmigaTarget.Ocs => "amiga.target_ocs",
            AmigaTarget.Ecs => "amiga.target_ecs",
            AmigaTarget.Aga => "amiga.target_aga",
            _ => "amiga.target_any"
        });

    /// <summary>Feature named by a MAME <c>&lt;feature type="..."/&gt;</c>; an unknown type gives <see cref="MameFeatures.None"/>.</summary>
    public static MameFeatures ParseMameFeature(string? type) =>
        type?.Trim().ToLowerInvariant() switch
        {
            "protection" => MameFeatures.Protection,
            "timing" => MameFeatures.Timing,
            "graphics" => MameFeatures.Graphics,
            "palette" => MameFeatures.Palette,
            "sound" => MameFeatures.Sound,
            "capture" => MameFeatures.Capture,
            "camera" => MameFeatures.Camera,
            "microphone" => MameFeatures.Microphone,
            "controls" => MameFeatures.Controls,
            "keyboard" => MameFeatures.Keyboard,
            "mouse" => MameFeatures.Mouse,
            "media" => MameFeatures.Media,
            "disk" => MameFeatures.Disk,
            "printer" => MameFeatures.Printer,
            "tape" => MameFeatures.Tape,
            "punch" => MameFeatures.Punch,
            "drum" => MameFeatures.Drum,
            "rom" => MameFeatures.Rom,
            "comms" => MameFeatures.Comms,
            "lan" => MameFeatures.Lan,
            "wan" => MameFeatures.Wan,
            _ => MameFeatures.None
        };

    public static M3uPlacementMode ParseM3uPlacementMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "parent_hidden" => M3uPlacementMode.ParentHidden,
            "subfolder" => M3uPlacementMode.Subfolder,
            _ => M3uPlacementMode.Parent
        };

    public static string ToConfigString(this M3uPlacementMode mode) =>
        mode switch
        {
            M3uPlacementMode.ParentHidden => "parent_hidden",
            M3uPlacementMode.Subfolder => "subfolder",
            _ => "parent"
        };

    public static string ToValueString(this FileStatus status) =>
        status switch
        {
            FileStatus.Valid => "valid",
            FileStatus.Excluded => "excluded",
            FileStatus.Unmatched => "unmatched",
            FileStatus.Duplicate => "duplicate",
            FileStatus.Error => "error",
            _ => "valid"
        };
}
