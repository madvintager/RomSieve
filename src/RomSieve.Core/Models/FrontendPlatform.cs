namespace RomSieve.Core.Models;

/// <summary>
/// A platform of the frontend table (<c>Resources/frontends/platforms.json</c>): the DATs that name it, and its folder
/// in each frontend that has one, with the archives the frontend reads there.
/// </summary>
public sealed class FrontendPlatform
{
    /// <summary>Stable identifier of the platform (<c>snes</c>, <c>psx</c>, <c>amiga-aga</c>).</summary>
    public required string Id { get; init; }

    /// <summary>
    /// Header names of the No-Intro and Redump DATs of the platform, and its TOSEC system names (the text before the
    /// first <c> - </c> of a TOSEC header), as the table writes them.
    /// </summary>
    public IReadOnlyList<string> DatNames { get; init; } = [];

    /// <summary>Conventions whose DATs are of this platform whatever their name (MAME machines).</summary>
    public IReadOnlyList<NamingConvention> Conventions { get; init; } = [];

    /// <summary>Platform of the releases that only run on an AGA Amiga, for the Amiga platform; <c>null</c> otherwise.</summary>
    public string? AgaPlatformId { get; init; }

    /// <summary>Folder of the platform in each frontend that has one: a single segment under the destination.</summary>
    public IReadOnlyDictionary<FrontendTarget, string> Folders { get; init; } = new Dictionary<FrontendTarget, string>();

    /// <summary>
    /// Archive extensions (<c>zip</c>, <c>7z</c>, without the dot) each frontend reads in the folder of the platform;
    /// a frontend absent from it has a folder whose archives are not known.
    /// </summary>
    public IReadOnlyDictionary<FrontendTarget, IReadOnlyList<string>> Archives { get; init; } =
        new Dictionary<FrontendTarget, IReadOnlyList<string>>();
}

/// <summary>Folder a frontend gives the files of a platform (<see cref="Abstractions.IFrontendPlatformService.FindFolder"/>).</summary>
/// <param name="Platform">The platform of the files: the AGA one for an Amiga release that needs an AGA machine.</param>
/// <param name="Name">Folder under the destination, a single safe segment.</param>
/// <param name="Archives">Archive extensions the frontend reads there (without the dot); <c>null</c> when not known.</param>
public sealed record FrontendFolder(FrontendPlatform Platform, string Name, IReadOnlyList<string>? Archives)
{
    /// <summary>
    /// Whether the frontend reads an archive of this extension (<c>.zip</c> or <c>zip</c>) in the folder; <c>null</c>
    /// when the table does not know.
    /// </summary>
    public bool? AcceptsArchive(string extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        return Archives?.Contains(extension.TrimStart('.'), StringComparer.OrdinalIgnoreCase);
    }
}
