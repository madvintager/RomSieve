namespace RomSieve.Core.Models;

/// <summary>
/// Output settings a frontend reads best, which the interface offers to apply when a frontend is chosen. Only the output
/// changes: the pattern under the folder of the platform, the filters, the languages, the 1G1R and the transfer mode
/// stay the choices of the user. A <c>null</c> value is left as it is.
/// </summary>
/// <param name="FlatDestination">Everything at the root of the folder of the platform (<see cref="SortSettings.FlatDestination"/>).</param>
/// <param name="SubfolderMultidisk">A subfolder per multi-disc game (<see cref="SortSettings.SubfolderMultidisk"/>).</param>
/// <param name="GenerateM3u">A <c>.m3u</c> playlist per multi-disc game (<see cref="SortSettings.GenerateM3u"/>).</param>
/// <param name="M3uPlacement">Where the playlist goes (<see cref="SortSettings.M3uPlacement"/>).</param>
/// <param name="FolderPerDiscGame">A folder per game on a disc image (<see cref="SortSettings.FolderPerDiscGame"/>).</param>
public sealed record FrontendRecommendation(
    bool? FlatDestination,
    bool SubfolderMultidisk,
    bool GenerateM3u,
    M3uPlacementMode? M3uPlacement,
    bool FolderPerDiscGame)
{
    /// <summary>
    /// The settings of a frontend, checked on its own files and documentation; <c>null</c> without a frontend.
    /// <list type="bullet">
    /// <item>Batocera and Recalbox hide the discs a playlist names by their file name in its folder: the discs beside
    /// their playlist, everything at the root of the platform.</item>
    /// <item>RetroPie shows every file, but its EmulationStation skips the folders whose name starts with a dot: the
    /// discs in a hidden subfolder, the playlist beside it.</item>
    /// <item>ES-DE reads a folder named after a playlist as that one game: the discs and their playlist in
    /// <c>Game.m3u/</c>.</item>
    /// <item>MiSTer reads no playlist, swaps discs among the files of a folder and names the memory card of a PSX game
    /// after its folder, which its CD cores want for every disc game: a folder per disc game, multi-disc or not. Its
    /// layout under the folder of the system stays the choice of the user.</item>
    /// </list>
    /// </summary>
    public static FrontendRecommendation? For(FrontendTarget frontend) => frontend switch
    {
        FrontendTarget.Batocera or FrontendTarget.Recalbox => new(true, SubfolderMultidisk: false, GenerateM3u: true, null, FolderPerDiscGame: false),
        FrontendTarget.RetroPie => new(true, SubfolderMultidisk: true, GenerateM3u: true, M3uPlacementMode.ParentHidden, FolderPerDiscGame: false),
        FrontendTarget.EsDe => new(true, SubfolderMultidisk: true, GenerateM3u: true, M3uPlacementMode.GameFolder, FolderPerDiscGame: false),
        FrontendTarget.MiSTer => new(null, SubfolderMultidisk: true, GenerateM3u: false, null, FolderPerDiscGame: true),
        _ => null
    };
}
