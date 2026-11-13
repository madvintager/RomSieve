namespace RomSieve.Core.Models;

/// <summary>
/// Global configuration for a sort or simulation operation.
/// </summary>
public sealed class SortConfig
{
    public required string SourceDir { get; set; }
    public required string DestinationDir { get; set; }
    public bool Recursive { get; set; } = true;
    public TransferMode TransferMode { get; set; } = TransferMode.Copy;
    public CollisionMode CollisionMode { get; set; } = CollisionMode.Rename;
    public string Pattern { get; set; } = "{publisher}/{year}/{title}/";
    public bool RestoreArticles { get; set; } = true;
    public bool SubfolderMultidisk { get; set; } = true;
    public bool GenerateM3u { get; set; } = false;
    public M3uPlacementMode M3uPlacement { get; set; } = M3uPlacementMode.Parent;

    /// <summary>
    /// Whether every game on a disc image (a descriptor <c>.cue</c>, <c>.gdi</c>, <c>.ccd</c>, or a <c>.chd</c>,
    /// <c>.iso</c>…) goes in a folder named after the game, as a multi-disc game does with <see cref="SubfolderMultidisk"/>:
    /// some cores need it (MiSTer). Off by default.
    /// </summary>
    public bool FolderPerDiscGame { get; set; }

    /// <summary>
    /// Frontend the destination is organized for: each file goes under the folder the frontend gives the platform of its
    /// DAT (<see cref="Abstractions.IFrontendPlatformService"/>), in place of the folder of the DAT of a lot; a DAT the
    /// table does not know keeps its name as folder. <see cref="FrontendTarget.None"/> by default: the targets of before.
    /// </summary>
    public FrontendTarget Frontend { get; set; }

    public FilterConfig Filters { get; set; } = new();
    public bool PurgeDestination { get; set; } = false;
    public bool AppendToDestination { get; set; } = false;
    public string? DatFile { get; set; }

    /// <summary>
    /// Whether a folder of DATs (<see cref="DatFile"/>) is read with its subfolders, a lot then gathering the DATs of the
    /// whole tree. Ignored for a single DAT and for a folder of MAME software lists.
    /// </summary>
    public bool DatRecursive { get; set; }

    /// <summary>
    /// Header names of the DATs of a lot left out of the sort (case ignored; empty: every DAT): only their header is read.
    /// </summary>
    public List<string> ExcludedDats { get; set; } = [];

    /// <summary>
    /// Whether each DAT of a lot gets a folder of its own in the destination, named after it, above the folders of the
    /// pattern (the default). Never for a single DAT, whose targets stay as they were.
    /// </summary>
    public bool FolderPerDat { get; set; } = true;

    /// <summary>
    /// Whether the folder of each DAT of a lot is placed under the folders of its path in the folder of DATs read (a lot
    /// read with its subfolders, <see cref="DatRecursive"/>). Only with <see cref="FolderPerDat"/>.
    /// </summary>
    public bool DatFolderMirror { get; set; }

    /// <summary>
    /// Folder of the CHDs of a MAME collection (<c>sf2/disk.chd</c>), kept apart from the ROM sets: with a MAME DAT, its
    /// CHD files are read in addition to the source and join their sets. Ignored by the other conventions.
    /// </summary>
    public string? ChdDir { get; set; }

    /// <summary>
    /// Category file of the MAME machines (<c>catver.ini</c>, <c>catlist.ini</c>, <c>genre.ini</c>): with a MAME DAT, the
    /// category of each set feeds the category filters and the <c>{genre}</c> and <c>{category}</c> variables.
    /// Ignored by the other conventions.
    /// </summary>
    public string? MameCategoryFile { get; set; }

    /// <summary>
    /// Rating file of the MAME machines (<c>bestgames.ini</c>): with a MAME DAT, the rating of each set feeds the rating
    /// filters and the order in which the size budget keeps the sets. Ignored by the other conventions.
    /// </summary>
    public string? MameRatingFile { get; set; }

    /// <summary>
    /// Folder of the patches (IPS, IPS32, UPS, BPS), read with its subfolders and its archives: each patch whose original
    /// ROM is identified in the source gives a patched version, written next to it. Not read by a sort of MAME sets.
    /// </summary>
    public string? PatchDir { get; set; }

    /// <summary>
    /// Whether the patches of <see cref="PatchDir"/> are applied. Off, the folder stays in the settings but is neither
    /// read nor checked against the destination, and the options of the patched versions have no effect; it is still
    /// kept out of the collection when it lies in the source, so that its files are never sorted as ROMs.
    /// </summary>
    public bool ApplyPatches { get; set; } = true;

    /// <summary>Whether this run applies patches: a patch folder is given, and <see cref="ApplyPatches"/> is on.</summary>
    public bool UsesPatches => ApplyPatches && !string.IsNullOrWhiteSpace(PatchDir);

    /// <summary>
    /// Writes the patched versions only: an original ROM that at least one patched version replaces is not written.
    /// </summary>
    public bool PatchOnly { get; set; }

    /// <summary>
    /// Pattern of the folders of the patched versions, relative to the root of the destination, with the variables of
    /// <see cref="Pattern"/> evaluated on the original ROM, and <c>{origin_folder}</c> and <c>{dat_folder}</c> for the
    /// folders it would have. Empty: next to their original ROM. Checked by
    /// <see cref="Services.PathOrganizerService.CheckPatchedPattern"/> when a patch folder is given.
    /// </summary>
    public string PatchedPattern { get; set; } = string.Empty;
    public NamingConvention NamingConvention { get; set; } = NamingConvention.Auto;

    /// <summary>
    /// Re-reads each copied file (copy, extraction from an archive, move across volumes) and compares its CRC32
    /// with the data read from the source; a mismatch deletes the target and keeps the source.
    /// </summary>
    public bool VerifyCopies { get; set; } = false;

    /// <summary>
    /// When an archive (ZIP, 7z, RAR) contains a single game (or a descriptor and its companion tracks) and all its
    /// files are kept, copies or moves the source archive as a whole instead of extracting its content.
    /// Ignored in ZIP mode (TorrentZip).
    /// </summary>
    public bool PreserveArchives { get; set; } = false;

    /// <summary>
    /// Writes the non-conforming files in <c>_unmatched</c> of the destination (the default). Off, the sort leaves them
    /// in the source: they keep their status in the results (<see cref="ProcessItem.IsNotIsolated"/>), but are neither
    /// written nor counted in the volume written.
    /// </summary>
    public bool IsolateUnmatched { get; set; } = true;

    /// <summary>Value of <see cref="HashingParallelism"/> that adapts the number of files hashed at once to the machine.</summary>
    public const int AutoHashingParallelism = 0;

    /// <summary>
    /// Number of files whose CRC32 is computed at the same time during the classification: 1 (sequential, the
    /// default, best on a hard disk drive) to 8, or <see cref="AutoHashingParallelism"/>. The result never depends on it.
    /// </summary>
    public int HashingParallelism { get; set; } = 1;

    /// <summary>
    /// Checks the SHA-1 of the files identified by CRC32 and size when the DAT provides one (Redump DATs do):
    /// a different SHA-1 makes the file unmatched. A ZIP member has to be decompressed for it.
    /// </summary>
    public bool VerifySha1 { get; set; } = false;

    /// <summary>
    /// Decompresses every member of the archives of the source and compares its data with the CRC32 the archive
    /// records, which alone identifies it otherwise: an archive member, or a MAME set, whose data does not match is
    /// isolated as damaged. Slow at the first run: every archive is read whole; the result is then cached with the
    /// headers (<see cref="Abstractions.IHeaderCache.FindDamagedMembers"/>) until the archive changes.
    /// </summary>
    public bool VerifyArchiveContents { get; set; }

    /// <summary>
    /// With a DAT that describes the tracks of the discs (Redump), identifies a CHD whose file name is the name of a game:
    /// the CHD content cannot be compared with the track hashes, so such a match is reported as not verified.
    /// </summary>
    public bool MatchChdByName { get; set; } = false;
}
