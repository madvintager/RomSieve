using System.Text.Json;
using System.Text.RegularExpressions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// The frontend table, read once from the embedded resource <c>Resources/frontends/platforms.json</c>. Each platform
/// lists the DATs that name it and its folder in each frontend; a DAT is found by its normalized name
/// (<see cref="NormalizeDatName"/>), a TOSEC DAT by its system, a DAT of MAME machines by its convention.
/// </summary>
public sealed partial class FrontendPlatformService : IFrontendPlatformService
{
    /// <summary>Name of the embedded table, under the default namespace of the assembly.</summary>
    internal const string ResourceName = "RomSieve.Core.Resources.frontends.platforms.json";

    // Variants of a No-Intro or Redump DAT that keep its platform: "Nintendo - Game Boy (Private)",
    // "Nintendo - Nintendo 64 (BigEndian)", "... (Parent-Clone)", and the "(Retool)" of a DAT Retool wrote
    private static readonly HashSet<string> VariantTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "Parent-Clone", "Headered", "Headerless", "BigEndian", "ByteSwapped", "Decrypted", "Encrypted", "Private",
        "Retool"
    };

    private readonly Dictionary<string, FrontendPlatform> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FrontendPlatform> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<NamingConvention, FrontendPlatform> _byConvention = [];

    public FrontendPlatformService()
        : this(LoadEmbeddedTable())
    {
    }

    /// <summary>A table given as is, for the tests; the same checks as the embedded one.</summary>
    internal FrontendPlatformService(IReadOnlyList<FrontendPlatform> platforms)
    {
        Platforms = platforms;
        foreach (var platform in platforms)
        {
            if (!_byId.TryAdd(platform.Id, platform))
            {
                throw new InvalidDataException($"Frontend table: platform '{platform.Id}' appears twice.");
            }

            foreach (var name in platform.DatNames)
            {
                var key = NormalizeDatName(name);
                if (key.Length == 0 || !_byName.TryAdd(key, platform))
                {
                    throw new InvalidDataException($"Frontend table: DAT name '{name}' is empty or names two platforms.");
                }
            }

            foreach (var convention in platform.Conventions)
            {
                if (!_byConvention.TryAdd(convention, platform))
                {
                    throw new InvalidDataException($"Frontend table: convention '{convention}' names two platforms.");
                }
            }
        }

        foreach (var platform in platforms)
        {
            if (platform.AgaPlatformId is { } aga && !_byId.ContainsKey(aga))
            {
                throw new InvalidDataException($"Frontend table: AGA platform '{aga}' of '{platform.Id}' does not exist.");
            }
        }
    }

    public IReadOnlyList<FrontendPlatform> Platforms { get; }

    public FrontendPlatform? FindPlatform(string? datName, NamingConvention convention = NamingConvention.Auto)
    {
        var name = NormalizeDatName(datName);

        // The whole name, then a TOSEC DAT by its system: "Commodore Amiga - Games - [ADF]" is of "Commodore Amiga"
        while (name.Length > 0)
        {
            if (_byName.TryGetValue(name, out var platform))
            {
                return platform;
            }

            var cut = name.LastIndexOf(" - ", StringComparison.Ordinal);
            name = cut > 0 ? name[..cut].TrimEnd() : string.Empty;
        }

        return _byConvention.GetValueOrDefault(convention);
    }

    public FrontendFolder? FindFolder(string? datName, NamingConvention convention, FrontendTarget frontend, ParsedRomName? item = null)
    {
        if (frontend == FrontendTarget.None || FindPlatform(datName, convention) is not { } platform)
        {
            return null;
        }

        // An Amiga release that only runs on an AGA machine goes where the frontend puts those
        if (item is not null
            && platform.AgaPlatformId is { } agaId
            && AmigaChipsets.Of(item) == AmigaChipset.Aga
            && _byId.TryGetValue(agaId, out var aga)
            && aga.Folders.ContainsKey(frontend))
        {
            platform = aga;
        }

        return platform.Folders.TryGetValue(frontend, out var folder)
            ? new FrontendFolder(platform, folder, platform.Archives.GetValueOrDefault(frontend))
            : null;
    }

    /// <summary>
    /// Name of a DAT as the table is searched: spaces collapsed, without the end Redump gives its file names
    /// (<c> - Datfile (10914) (2026-06-15 11-55-46)</c>, <c> - Discs (207) (…)</c>), nor the parentheses that end a name
    /// and do not change its platform: variants (<c>(Private)</c>, <c>(Headered)</c>, <c>(Parent-Clone)</c>…), dates of
    /// No-Intro and Redump, version of a TOSEC file name (<c>(TOSEC-v2025-01-15_CM)</c>). Any other parenthesis stays
    /// (<c>(e-Reader)</c>, <c>(PSN)</c>): such a DAT is of another set. Empty for a missing name.
    /// </summary>
    public static string NormalizeDatName(string? datName)
    {
        if (string.IsNullOrWhiteSpace(datName))
        {
            return string.Empty;
        }

        var name = RedumpFileSuffixRegex().Replace(WhitespaceRegex().Replace(datName.Trim(), " "), string.Empty);
        while (TrailingParenthesisRegex().Match(name) is { Success: true } match && IsVariant(match.Groups[1].Value))
        {
            name = name[..match.Index].TrimEnd();
        }

        return name;
    }

    private static bool IsVariant(string text) =>
        VariantTags.Contains(text.Trim()) || DateOrVersionRegex().IsMatch(text);

    private static IReadOnlyList<FrontendPlatform> LoadEmbeddedTable()
    {
        using var stream = typeof(FrontendPlatformService).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException($"Frontend table: resource '{ResourceName}' is missing.");
        return ReadTable(stream);
    }

    /// <summary>Platforms of a table in the format of <c>platforms.json</c>; an invalid table throws <see cref="InvalidDataException"/>.</summary>
    internal static IReadOnlyList<FrontendPlatform> ReadTable(Stream stream)
    {
        using var document = JsonDocument.Parse(stream);
        var platforms = new List<FrontendPlatform>();
        foreach (var entry in document.RootElement.GetProperty("platforms").EnumerateArray())
        {
            var id = entry.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidDataException("Frontend table: a platform has no identifier.");
            }

            platforms.Add(new FrontendPlatform
            {
                Id = id,
                DatNames = Strings(entry, "dats"),
                Conventions = [.. Strings(entry, "conventions").Select(Convention)],
                AgaPlatformId = entry.TryGetProperty("aga", out var aga) ? aga.GetString() : null,
                Folders = ByFrontend(entry, "folders", value => value.GetString() ?? string.Empty),
                Archives = ByFrontend(entry, "archives", value => (IReadOnlyList<string>)[.. value.EnumerateArray().Select(a => a.GetString() ?? string.Empty)])
            });
        }

        return platforms;
    }

    private static List<string> Strings(JsonElement entry, string property) =>
        entry.TryGetProperty(property, out var values)
            ? [.. values.EnumerateArray().Select(value => value.GetString() ?? string.Empty)]
            : [];

    private static NamingConvention Convention(string value) =>
        EnumExtensions.ParseNamingConvention(value) is var convention && convention.ToConfigString() == value
            ? convention
            : throw new InvalidDataException($"Frontend table: unknown convention '{value}'.");

    private static Dictionary<FrontendTarget, T> ByFrontend<T>(JsonElement entry, string property, Func<JsonElement, T> read)
    {
        var result = new Dictionary<FrontendTarget, T>();
        if (!entry.TryGetProperty(property, out var values))
        {
            return result;
        }

        foreach (var value in values.EnumerateObject())
        {
            var frontend = EnumExtensions.ParseFrontendTarget(value.Name);
            if (frontend == FrontendTarget.None || frontend.ToConfigString() != value.Name)
            {
                throw new InvalidDataException($"Frontend table: unknown frontend '{value.Name}'.");
            }

            result[frontend] = read(value.Value);
        }

        return result;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\s+-\s+(?:Datfile|Discs)\s*\(\d+\).*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RedumpFileSuffixRegex();

    [GeneratedRegex(@"\s*\(([^()]*)\)\s*$")]
    private static partial Regex TrailingParenthesisRegex();

    // 20260922-113213, 20260922, 2026-06-15 11-55-46, 2026-06-15, TOSEC-v2025-01-15_CM
    [GeneratedRegex(@"^\s*(?:\d{8}(?:-\d{6})?|\d{4}-\d{2}-\d{2}(?: \d{2}-\d{2}-\d{2})?|TOSEC-v[\w-]+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DateOrVersionRegex();
}
