using RomSieve.App.ViewModels;

namespace RomSieve.App.Tests;

/// <summary>
/// The search of the interface, read as terms: all of them found, in any order and any field, case and accents ignored;
/// <c>-</c> leaves a term out, double quotes keep words together, a lone dash is no term.
/// </summary>
public sealed class SearchQueryTests
{
    private const string TosecName = "Commodore C64 - Games - Adventure - [D64]";
    private const string TosecFile = "Commodore C64 - Games - Adventure - [D64] (TOSEC-v2025-02-16_CM).dat";

    private static bool Matches(string search, params string[] fields) =>
        SearchQuery.Parse(search).Matches(fields.Select(SearchQuery.Normalize).ToArray());

    [Theory]
    [InlineData("C64 Games D64")]
    [InlineData("d64 c64")]
    [InlineData("adventure 2025")]
    [InlineData("Commodore C64 - Games - Adventure - [D64]")]
    [InlineData("\"games - adventure\"")]
    [InlineData("C64 -Arcade")]
    public void Terms_ShouldAllBeFound_InAnyOrderAndAnyField(string search) =>
        Matches(search, TosecName, TosecFile).Should().BeTrue();

    [Theory]
    [InlineData("C64 Games T64")]
    [InlineData("C64 -Adventure")]
    [InlineData("C64 -\"Games - Adventure\"")]
    [InlineData("\"adventure games\"")]
    public void Search_ShouldFail_WhenATermIsMissingOrALeftOutTermIsFound(string search) =>
        Matches(search, TosecName, TosecFile).Should().BeFalse();

    [Fact]
    public void Parse_ShouldIgnoreCaseAccentsBlanksAndLoneDashes()
    {
        var query = SearchQuery.Parse("  Pokémon  -  -Démo \"Vert  Feuille\" \"open");

        query.Included.Should().Equal("pokemon", "vert  feuille", "open");
        query.Excluded.Should().Equal("demo");
        SearchQuery.Parse("  - ").IsEmpty.Should().BeTrue();
        SearchQuery.Parse("\"\"").IsEmpty.Should().BeTrue();
        Matches("", "anything").Should().BeTrue();
    }

    [Fact]
    public void Terms_ShouldMatchPartsOfWords_AndKeepTheirInnerDashes()
    {
        Matches("TOSEC-ISO", "TOSEC-ISO/Sony - PlayStation").Should().BeTrue();
        Matches("TOSEC-ISO", "TOSEC/Sony - PlayStation").Should().BeFalse();
        Matches("mari", "Super Mario Land").Should().BeTrue();
    }

    [Fact]
    public void Exact_ShouldQuoteATitleOfSeveralWords_SoThatItsWordsAreNotFoundApart()
    {
        SearchQuery.Exact("Another World").Should().Be("\"Another World\"");
        SearchQuery.Exact(" Lemmings ").Should().Be("Lemmings");
        SearchQuery.Exact("Say \"Hi\" Now").Should().Be("Say \"Hi\" Now", "a quote of its own would end the phrase");

        Matches(SearchQuery.Exact("Game B"), "Game B (Europe)").Should().BeTrue();
        Matches(SearchQuery.Exact("Game B"), "Game A (Brazil)").Should().BeFalse("its words found apart would find another game");
    }
}
