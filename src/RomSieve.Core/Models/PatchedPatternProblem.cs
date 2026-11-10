namespace RomSieve.Core.Models;

/// <summary>
/// Why the pattern of the patched versions (<see cref="SortConfig.PatchedPattern"/>) is refused: it must stay inside the
/// destination, out of the folders RomSieve keeps for itself (<see cref="Services.PathOrganizerService.CheckPatchedPattern"/>).
/// </summary>
public enum PatchedPatternProblem
{
    /// <summary>The pattern is accepted.</summary>
    None,

    /// <summary>The pattern starts with a separator (<c>/x</c>, <c>\x</c>, <c>\\server</c>): an absolute path.</summary>
    Rooted,

    /// <summary>A folder names a drive (<c>C:</c>).</summary>
    Drive,

    /// <summary>A folder is <c>..</c>.</summary>
    ParentSegment,

    /// <summary>A character Windows or Linux forbids in a name (<c>&lt; &gt; : " | ? *</c>, a control character).</summary>
    ForbiddenCharacter,

    /// <summary>The first folder is the quarantine of the purges (<c>_purged</c>) or the marker of the destination.</summary>
    ReservedFolder,

    /// <summary><c>{origin_folder}</c> or <c>{dat_folder}</c>, worth several folders, is not a whole folder of the pattern.</summary>
    FolderVariableNotAlone
}
