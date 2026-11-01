using System.Text.RegularExpressions;
using CsCheck;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Property-based tests of the TOSEC (TNC) parser with CsCheck: parsing never throws, a well-formed generated name is
/// read back field by field, and the canonical keys are stable. A counterexample found here becomes a unit test of
/// <see cref="TosecParserTests"/>. A failure prints the seed that replays it (<c>CsCheck_Seed</c> environment variable).
/// </summary>
public sealed partial class TosecParserPropertyTests
{
    private const int Iterations = 2_000;

    private static readonly CoreTestServices Services = new();
    private static readonly NoIntroRedumpParser UniversalParser = Services.NoIntroParser;
    private static readonly RomFilterService GameKeys = Services.FilterService;

    // =========================================================================
    // Generators
    // =========================================================================

    /// <summary>A name component as it is written between the parentheses or the brackets, with its expected reading.</summary>
    public sealed record TncName(
        string Title,
        string Version,
        string? Demo,
        string Date,
        string Publisher,
        string? System,
        string? Video,
        string? Country,
        string? Language,
        string? Copyright,
        string? DevStatus,
        string? Media,
        IReadOnlyList<string> Brackets,
        string Extension)
    {
        public string Compose()
        {
            var name = Title + (Version.Length > 0 ? " " + Version : string.Empty);
            if (Demo is not null) name += $" ({Demo})";
            name += $" ({Date})({Publisher})";
            foreach (var optional in new[] { System, Video, Country, Language, Copyright, DevStatus, Media })
            {
                if (optional is not null) name += $"({optional})";
            }

            return name + string.Concat(Brackets.Select(b => $"[{b}]")) + Extension;
        }

        public override string ToString() => Compose();
    }

    /// <summary>
    /// A word of a title or a publisher. A word shaped like a version (<c>v2</c>, <c>r7</c>, <c>Rev</c>, <c>build</c>) is
    /// ambiguous by the TNC itself, which separates the version from the title by its form only: it is not generated.
    /// </summary>
    private static Gen<string> Word =>
        (from first in Gen.Char["ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"]
         from rest in Gen.String[Gen.Char["abcdefghijklmnopqrstuvwxyz0123456789'&!"], 0, 8]
         select first + rest)
        .Where(word => !VersionShapedWord().IsMatch(word));

    [GeneratedRegex(@"^(?:[vV]\.?[a-zA-Z]?\d|r\d|(?i:rev|build)$)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionShapedWord();

    private static readonly Gen<string> Article = Gen.OneOfConst(", The", ", A", ", Les", ", Der", ", L'");

    private static Gen<string> Title =>
        from words in Word.Array[1, 4]
        from subtitle in Word.Array[0, 2]
        from article in Gen.Frequency((3, Gen.Const(() => string.Empty)), (1, Article))
        select string.Join(' ', words) + article + (subtitle.Length > 0 ? " - " + string.Join(' ', subtitle) : string.Empty);

    private static readonly Gen<string> Version = Gen.Frequency(
        (3, Gen.Const(() => string.Empty)),
        (2, Gen.Int[0, 9].Select(Gen.Int[0, 99], (major, minor) => $"v{major}.{minor}")),
        (1, Gen.Int[1, 99].Select(n => $"r{n}")),
        (1, Gen.Int[1, 9].Select(Gen.Int[0, 9], (major, build) => $"v{major}.{build} r{build}")));

    private static readonly Gen<string> Date = Gen.OneOf(
        Gen.Int[1970, 2025].Select(y => y.ToString()),
        Gen.Int[1970, 2025].Select(Gen.Int[1, 12], (y, m) => $"{y}-{m:00}"),
        Gen.Int[1970, 2025].Select(Gen.Int[1, 12], Gen.Int[1, 28], (y, m, d) => $"{y}-{m:00}-{d:00}"),
        Gen.OneOfConst("19xx", "198x", "199x", "20xx"));

    private static Gen<string> Publisher => Gen.Frequency(
        (1, Gen.Const(() => "-")),
        (4, Word.Array[1, 2].Select(words => string.Join(' ', words))),
        (1, Word.Select(Word, (a, b) => $"{a} - {b}")));

    private static Gen<string?> Optional(params string[] values) =>
        Gen.Frequency((2, Gen.Const<string?>(() => null)), (1, Gen.OneOfConst(values).Select(v => (string?)v)));

    private static readonly Gen<string?> Media = Gen.Frequency(
        (2, Gen.Const<string?>(() => null)),
        (1, Gen.Int[1, 9].Select(n => (string?)$"Disk {n} of {n + 1}")),
        (1, Gen.OneOfConst<string?>("Side A", "Side B", "Tape 1 of 2", "Part 2", "Disc 3")));

    /// <summary>Brackets in TNC order; each one is read as a single flag.</summary>
    private static readonly Gen<string[]> Brackets =
        Gen.OneOfConst("cr FLT", "f", "h Ministry", "m", "p", "t +2", "tr Fr", "o", "u", "v", "b", "b2", "a", "a3", "!", "docs", "req 1MB")
            .Array[0, 3]
            .Select(brackets => brackets.Distinct().ToArray());

    private static readonly Gen<TncName> WellFormedName =
        from title in Title
        from version in Version
        from demo in Optional("demo", "demo-playable")
        from date in Date
        from publisher in Publisher
        from system in Optional("A500", "A1200", "AGA", "STE", "C64", "+3", "128k")
        from video in Optional("PAL", "NTSC", "PAL-60")
        from country in Optional("US", "GB", "FR", "DE", "JP", "DE-GB", "EU")
        from language in Optional("en", "fr", "de-fr", "M3", "M")
        from copyright in Optional("PD", "SW", "FW")
        from devStatus in Optional("alpha", "beta", "proto", "preview")
        from media in Media
        from brackets in Brackets
        from extension in Gen.OneOfConst(".adf", ".st", ".d64", ".tzx", ".zip", ".dsk")
        select new TncName(title, version, demo, date, publisher, system, video, country, language, copyright, devStatus, media, brackets, extension);

    /// <summary>Strings made of the characters that matter to the grammar, to stress the regular expressions.</summary>
    private static readonly Gen<string> GrammarNoise =
        Gen.String[Gen.Char["()[]. -_,vVr0123456789aAbBdDiIkKmMsStT!'&:/\\\téßœ"], 0, 80];

    // =========================================================================
    // Invariant 1: parsing never throws
    // =========================================================================

    [Fact]
    public void ParseRomFilename_ShouldNeverThrow()
    {
        var conventions = Gen.OneOfConst(NamingConvention.Auto, NamingConvention.Tosec, NamingConvention.NoIntro, NamingConvention.Redump);
        Gen.OneOf(Gen.String, GrammarNoise, WellFormedName.Select(n => n.Compose()))
            .Select(conventions)
            .Sample((name, convention) =>
            {
                var parsed = UniversalParser.ParseRomFilename(name, convention: convention);
                parsed.RawFilename.Should().Be(name);
                _ = GameKeys.SerializeCanonicalGameKey(parsed);
                _ = NoIntroRedumpParser.DetectNamingConvention(name);
            }, iter: Iterations);
    }

    // =========================================================================
    // Invariant 2: a well-formed name is read back identically
    // =========================================================================

    [Fact]
    public void WellFormedName_ShouldBeReadBackFieldByField()
    {
        WellFormedName.Sample(name =>
        {
            var parsed = TosecParser.ParseTosecFilenameStatic(name.Compose());

            parsed.IsTosecConform.Should().BeTrue();
            parsed.Title.Should().Be(name.Title);
            parsed.Version.Should().Be(name.Version);
            parsed.Demo.Should().Be(name.Demo);
            parsed.Date.Should().Be(name.Date);
            parsed.Publisher.Should().Be(name.Publisher);
            parsed.System.Should().Be(name.System);
            parsed.Video.Should().Be(name.Video);
            parsed.Country.Should().Be(name.Country);
            parsed.Language.Should().Be(name.Language);
            parsed.Copyright.Should().Be(name.Copyright);
            parsed.DevStatus.Should().Be(name.DevStatus);
            parsed.MediaLabel.Should().Be(name.Media);
            parsed.Extension.Should().Be(name.Extension);

            var brackets = name.Brackets.Select(b => $"[{b}]").ToList();
            parsed.DumpFlags.Concat(parsed.SoftwareMods).Concat(parsed.MoreInfo).Should().BeEquivalentTo(brackets);
            parsed.IsVerified.Should().Be(name.Brackets.Contains("!"));
            parsed.IsBadDump.Should().Be(name.Brackets.Any(b => b.StartsWith('b')));
            parsed.IsAlternate.Should().Be(name.Brackets.Any(b => b.StartsWith('a')));
            parsed.IsCrack.Should().Be(name.Brackets.Any(b => b.StartsWith("cr", StringComparison.Ordinal)));
            parsed.IsTranslation.Should().Be(name.Brackets.Any(b => b.StartsWith("tr", StringComparison.Ordinal)));
            parsed.MoreInfo.Should().BeEquivalentTo(brackets.Where(b => b is "[docs]" or "[req 1MB]"));
        }, iter: Iterations);
    }

    [Fact]
    public void WellFormedName_ShouldBeDetectedAsTosecByTheUniversalParser()
    {
        WellFormedName.Sample(name =>
        {
            var parsed = UniversalParser.ParseRomFilename(name.Compose());
            parsed.Convention.Should().Be(NamingConvention.Tosec);
            parsed.Title.Should().Be(name.Title);
        }, iter: Iterations);
    }

    // =========================================================================
    // Invariant 3: the canonical keys are stable
    // =========================================================================

    [Fact]
    public void CanonicalTitle_ShouldBeIdempotent()
    {
        Gen.OneOf(Gen.String, GrammarNoise, Title).Sample(title =>
        {
            var once = GameKeys.NormalizeCanonicalTitle(title);
            GameKeys.NormalizeCanonicalTitle(once).Should().Be(once);
        }, iter: Iterations);
    }

    /// <summary>
    /// Two releases of the same game on the same system (other version, date, publisher, region, media or dump flags)
    /// share the canonical game key, which only depends on the title and the system.
    /// </summary>
    [Fact]
    public void ReleasesOfTheSameGame_ShouldShareTheCanonicalGameKey()
    {
        WellFormedName.Select(WellFormedName).Sample((first, other) =>
        {
            var second = other with { Title = first.Title, System = first.System };
            var firstKey = GameKeys.SerializeCanonicalGameKey(TosecParser.ParseTosecFilenameStatic(first.Compose()));
            var secondKey = GameKeys.SerializeCanonicalGameKey(TosecParser.ParseTosecFilenameStatic(second.Compose()));

            secondKey.Should().Be(firstKey);
            firstKey.Should().Be($"{GameKeys.NormalizeCanonicalTitle(first.Title)}::{(first.System ?? string.Empty).ToLowerInvariant()}");
        }, iter: Iterations);
    }
}
