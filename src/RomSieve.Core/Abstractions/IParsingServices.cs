using RomSieve.Core.Models;

namespace RomSieve.Core.Abstractions;

/// <summary>
/// Two-level TOSEC Naming Convention (TNC) parsing engine.
/// </summary>
public interface ITosecParser
{
    string RestoreTitleArticles(string title);
    ParsedRomName ParseTosecFilename(string filename, string? filePath = null, long fileSize = 0);
}

/// <summary>
/// No-Intro, Redump, and universal multi-convention parsing engine.
/// </summary>
public interface INoIntroRedumpParser
{
    bool IsNoIntroRereleaseSegment(string segment);
    (string RawTitle, string MetadataTail) ExtractNoIntroRawTitleAndTail(string baseName);
    NamingConvention DetectNamingConvention(string filename, string headerText = "", string extension = "");
    ParsedRomName ParseNoIntroFilename(string filename, string? filePath = null, long fileSize = 0, bool isRedump = false);
    ParsedRomName ParseRomFilename(string filename, string? filePath = null, long fileSize = 0, NamingConvention convention = NamingConvention.Auto);
}

/// <summary>
/// Analysis of MAME machine descriptions. A MAME set is named by its short name (<c>sf2ua.zip</c>): its title, region,
/// date, revision and status come from the description of the DAT (<c>Street Fighter II: The World Warrior (USA 910206)</c>).
/// </summary>
public interface IMameDescriptionParser
{
    /// <summary>
    /// Parses a machine description. <paramref name="year"/> and <paramref name="manufacturer"/> are those of the DAT; the
    /// date written in the description, when there is one, is more precise than the year.
    /// </summary>
    ParsedRomName ParseDescription(
        string description,
        string? year = null,
        string? manufacturer = null,
        string? rawFilename = null,
        string? filePath = null,
        long fileSize = 0);

    /// <summary>
    /// Parses a machine of the DAT: its description, year, manufacturer and parent.
    /// </summary>
    ParsedRomName ParseGame(DatGame game, string? filePath = null, long fileSize = 0);

    /// <summary>
    /// Parses a software of a MAME software list: its description, year, publisher, parent and support by MAME.
    /// </summary>
    ParsedRomName ParseSoftware(DatGame game, string? filePath = null, long fileSize = 0);
}
