using RomSieve.Core.Models;

namespace RomSieve.Core.Abstractions;

/// <summary>
/// The frontend table: which platform a DAT is of, and which folder a frontend gives it. The table is an embedded
/// resource (<c>Resources/frontends/platforms.json</c>), the only place that knows the folders of the frontends.
/// </summary>
public interface IFrontendPlatformService
{
    /// <summary>Every platform of the table, in its order.</summary>
    IReadOnlyList<FrontendPlatform> Platforms { get; }

    /// <summary>
    /// Platform of a DAT, by its header name (or its file name without a header), normalized
    /// (<see cref="Services.FrontendPlatformService.NormalizeDatName"/>); a TOSEC DAT by its system. A DAT of MAME
    /// machines, whatever its name, by its <paramref name="convention"/>. <c>null</c> for a DAT the table does not know.
    /// </summary>
    FrontendPlatform? FindPlatform(string? datName, NamingConvention convention = NamingConvention.Auto);

    /// <summary>
    /// Folder <paramref name="frontend"/> gives the files of a DAT; for an Amiga DAT, the folder of the AGA machines when
    /// <paramref name="item"/> only runs on one. <c>null</c> without a frontend, for a DAT the table does not know, or a
    /// platform the frontend has no folder for.
    /// </summary>
    FrontendFolder? FindFolder(string? datName, NamingConvention convention, FrontendTarget frontend, ParsedRomName? item = null);

    /// <summary>
    /// Folder of the destination the files of <paramref name="datName"/> go into for <paramref name="frontend"/>: the
    /// folder of its platform (<see cref="FindFolder"/>), or else its name, or none for a MAME software list
    /// (<paramref name="convention"/>), whose software stays in the folder of its list. Shared by the sort, the preview of
    /// the pattern and the window of a lot, so that they give the same folder.
    /// </summary>
    FrontendDestination Destination(string datName, NamingConvention convention, FrontendTarget frontend, ParsedRomName? item = null);
}
