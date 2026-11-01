using RomSieve.Core.Services;

namespace RomSieve.Core.Tests;

/// <summary>
/// Unit tests for TOSEC parser (TNC).
/// </summary>
public sealed class TosecParserTests
{
    private readonly TosecParser _parser = new();

    [Fact]
    public void BasicMinimalTosec_ShouldParseAllCoreFields()
    {
        var filename = "Robocop 3 (1991)(Ocean).adf";
        var p = _parser.ParseFilename(filename);

        p.IsTosecConform.Should().BeTrue();
        p.Title.Should().Be("Robocop 3");
        p.Version.Should().BeEmpty();
        p.Date.Should().Be("1991");
        p.Year.Should().Be("1991");
        p.Publisher.Should().Be("Ocean");
        p.Extension.Should().Be(".adf");
        p.Letter.Should().Be("R");
        p.DumpStatusSummary.Should().Be("Clean");
    }

    [Fact]
    public void TitleWithVersion_ShouldSeparateBaseTitleAndVersion()
    {
        var filename = "Legend of TOSEC, The v1.0 (1995)(Acme).zip";
        var p = _parser.ParseFilename(filename);

        p.IsTosecConform.Should().BeTrue();
        p.BaseTitle.Should().Be("Legend of TOSEC, The v1.0");
        p.Title.Should().Be("Legend of TOSEC, The");
        p.Version.Should().Be("v1.0");
        p.Date.Should().Be("1995");
        p.Publisher.Should().Be("Acme");
        p.Extension.Should().Be(".zip");
    }

    [Theory]
    [InlineData("3D Construction Kit II r2.01 (1992)(Domark)(Disk 1 of 2)[h Ministry][construction kit].adf", "3D Construction Kit II", "r2.01", "3D Construction Kit II")]
    [InlineData("3D Construction Kit II r2.03 (1992)(Domark)(FR)(Disk 1 of 2)[construction kit].adf", "3D Construction Kit II", "r2.03", "3D Construction Kit II")]
    [InlineData("3D Construction Kit r01.0000 (1991)(Domark)[construction kit].adf", "3D Construction Kit", "r01.0000", "3D Construction Kit")]
    [InlineData("Hitchhiker's Guide to the Galaxy, The r59 (1985)(Infocom).st", "Hitchhiker's Guide to the Galaxy, The", "r59", "The Hitchhiker's Guide to the Galaxy")]
    [InlineData("SimCity 2000 v1.1d r0.037 (1994)(Maxis).adf", "SimCity 2000", "v1.1d r0.037", "SimCity 2000")]
    [InlineData("Conquests of Camelot - The Search for the Grail v1.009 r32 (1990)(Sierra).adf", "Conquests of Camelot - The Search for the Grail", "v1.009 r32", "Conquests of Camelot - The Search for the Grail")]
    [InlineData("G-LOC R360 (1992)(U.S. Gold).adf", "G-LOC R360", "", "G-LOC R360")]
    public void TitleWithRPrefixedAndCompoundVersions_ShouldSeparateTitleAndPreserveArticleRestoration(
        string filename,
        string expectedTitle,
        string expectedVersion,
        string expectedRestoredTitle)
    {
        var p = _parser.ParseFilename(filename);

        p.IsTosecConform.Should().BeTrue();
        p.Title.Should().Be(expectedTitle);
        p.Version.Should().Be(expectedVersion);
        _parser.RestoreTitleArticles(p.Title).Should().Be(expectedRestoredTitle);
    }

    [Theory]
    [InlineData("Legend of TOSEC, The", "The Legend of TOSEC")]
    [InlineData("Aventure, L'", "L'Aventure")]
    [InlineData("Dune, A", "A Dune")]
    [InlineData("Aventure, L’", "L’Aventure")]
    [InlineData("Secret, Le", "Le Secret")]
    [InlineData("Mansion, Das", "Das Mansion")]
    [InlineData("Normal Title", "Normal Title")]
    [InlineData("Legend of Zelda, The - A Link to the Past", "The Legend of Zelda - A Link to the Past")]
    public void RestoreArticles_ShouldRestoreInvertedLeadingArticles(string input, string expected)
    {
        _parser.RestoreTitleArticles(input).Should().Be(expected);
    }

    [Fact]
    public void DemoAndMedia_ShouldExtractDemoAndMediaLabel()
    {
        var filename = "Super Sprint (demo) (1986)(Electric Dreams)(Disk 1 of 2).adf";
        var p = _parser.ParseFilename(filename);

        p.IsTosecConform.Should().BeTrue();
        p.Title.Should().Be("Super Sprint");
        p.Demo.Should().Be("demo");
        p.Date.Should().Be("1986");
        p.Publisher.Should().Be("Electric Dreams");
        p.MediaLabel.Should().Be("Disk 1 of 2");
    }

    [Theory]
    [InlineData("Game (199x)(Author).rom", "199x", "199x")]
    [InlineData("Game (19xx-12-25)(Author).rom", "19xx-12-25", "19xx")]
    [InlineData("Game (2001-01)(Author).rom", "2001-01", "2001")]
    [InlineData("Game (xx98)(Author).rom", "xx98", "xx98")]
    public void DatesVarieties_ShouldParseWildcardAndIsoDates(string filename, string expectedDate, string expectedYear)
    {
        var p = _parser.ParseFilename(filename);

        p.IsTosecConform.Should().BeTrue();
        p.Date.Should().Be(expectedDate);
        p.Year.Should().Be(expectedYear);
    }

    [Fact]
    public void OptParensCategorization_ShouldClassifySystemVideoCountryLanguageMediaAndVerified()
    {
        var filename = "Alien Breed (1991)(Team 17)(A500)(PAL)(GB)(en)(Disk 1 of 3)[!].adf";
        var p = _parser.ParseFilename(filename);

        p.IsTosecConform.Should().BeTrue();
        p.Title.Should().Be("Alien Breed");
        p.Publisher.Should().Be("Team 17");
        p.System.Should().Be("A500");
        p.Video.Should().Be("PAL");
        p.Country.Should().Be("GB");
        p.CountryCodes.Should().Contain("GB");
        p.Language.Should().Be("en");
        p.LanguageCodes.Should().Contain("en");
        p.MediaLabel.Should().Be("Disk 1 of 3");
        p.IsVerified.Should().BeTrue();
        p.DumpStatusSummary.Should().Be("Verified");
    }

    [Fact]
    public void BracketsClassification_ShouldSeparateSoftwareModsDumpFlagsAndMoreInfo()
    {
        var filename = "Turrican II (1991)(Rainbow Arts)[cr Skid Row][t +5][b1][more info].adf";
        var p = _parser.ParseFilename(filename);

        p.IsTosecConform.Should().BeTrue();
        p.Title.Should().Be("Turrican II");
        p.IsCrack.Should().BeTrue();
        p.SoftwareMods.Should().Contain("[cr Skid Row]");
        p.IsTrainer.Should().BeTrue();
        p.SoftwareMods.Should().Contain("[t +5]");
        p.IsBadDump.Should().BeTrue();
        p.DumpFlags.Should().Contain("[b1]");
        p.MoreInfo.Should().Contain("[more info]");
        p.DumpStatusSummary.Should().Be("BadDump");
    }

    [Fact]
    public void BracketsDescriptiveDumpFlags_ShouldBeClassifiedAsDumpFlagsNotMoreInfo()
    {
        var pB = _parser.ParseFilename("Aaargh! (1988)(Melbourne House)[b corrupt file].adf");
        pB.IsBadDump.Should().BeTrue();
        pB.DumpFlags.Should().Contain("[b corrupt file]");
        pB.MoreInfo.Should().BeEmpty();

        var pA = _parser.ParseFilename("Game (1990)(Publisher)[a alt sound].adf");
        pA.IsAlternate.Should().BeTrue();
        pA.DumpFlags.Should().Contain("[a alt sound]");

        var pO = _parser.ParseFilename("Game (1990)(Publisher)[o 40K].adf");
        pO.IsOverdump.Should().BeTrue();
        pO.DumpFlags.Should().Contain("[o 40K]");

        var pU = _parser.ParseFilename("Game (1990)(Publisher)[u bad size].adf");
        pU.IsUnderdump.Should().BeTrue();
        pU.DumpFlags.Should().Contain("[u bad size]");

        var pV = _parser.ParseFilename("Game (1990)(Publisher)[v Lamer Exterminator].adf");
        pV.IsVirus.Should().BeTrue();
        pV.DumpFlags.Should().Contain("[v Lamer Exterminator]");
    }

    [Fact]
    public void DevStatusAndCopyright_ShouldParseProtoAndPublicDomain()
    {
        var filename = "Unreleased Game (1993)(-)(proto)(PD).adf";
        var p = _parser.ParseFilename(filename);

        p.IsTosecConform.Should().BeTrue();
        p.Title.Should().Be("Unreleased Game");
        p.Publisher.Should().Be("-");
        p.DevStatus.Should().Be("proto");
        p.Copyright.Should().Be("PD");
    }

    [Fact]
    public void UnmatchedFile_ShouldBeFlaggedAsNonConformant()
    {
        var filename = "my_game_without_tosec_format.zip";
        var p = _parser.ParseFilename(filename);

        p.IsTosecConform.Should().BeFalse();
        p.Title.Should().Be("my_game_without_tosec_format");
        p.Extension.Should().Be(".zip");
    }

    [Fact]
    public void MultilingualFlagMParsing_ShouldHandleGenericAndExplicitMultiLanguageTags()
    {
        var pM3 = _parser.ParseFilename("Legend of TOSEC, The (1986)(Devstudio)(M3).adf");
        pM3.IsTosecConform.Should().BeTrue();
        pM3.IsMultilingual.Should().BeTrue();
        pM3.MultilingualCount.Should().Be(3);
        pM3.LanguageCodes.Should().Contain("M").And.Contain("M3");

        var pM = _parser.ParseFilename("Game (1990)(Publisher)(M).adf");
        pM.IsMultilingual.Should().BeTrue();
        pM.LanguageCodes.Should().Contain("M");

        var pDual = _parser.ParseFilename("Game (1990)(Publisher)(de-fr).adf");
        pDual.IsMultilingual.Should().BeTrue();
        pDual.MultilingualCount.Should().Be(2);
        pDual.LanguageCodes.Should().Contain(["M", "de", "fr"]);

        var pSingle = _parser.ParseFilename("Game (1990)(Publisher)(fr).adf");
        pSingle.IsMultilingual.Should().BeFalse();
        pSingle.LanguageCodes.Should().NotContain("M");
        pSingle.LanguageCodes.Should().Contain("fr");
    }

    [Fact]
    public void BracketsNumberedModificationsAndEmbeddedBaddump_ShouldBeProperlyClassified()
    {
        var pM2 = _parser.ParseFilename("Nitro (1990)(Psygnosis)(Disk 2 of 2)(Game)[cr HZ][m2 highscore][bootable].adf");
        pM2.IsCrack.Should().BeTrue();
        pM2.IsModified.Should().BeTrue();
        pM2.SoftwareMods.Should().Contain("[m2 highscore]");
        pM2.MoreInfo.Should().NotContain("[m2 highscore]");

        var pM3 = _parser.ParseFilename("Nitro (1990)(Psygnosis)(Disk 2 of 2)(Game)[cr HZ][m3 highscore][bootable].adf");
        pM3.IsModified.Should().BeTrue();
        pM3.SoftwareMods.Should().Contain("[m3 highscore]");
        pM3.MoreInfo.Should().NotContain("[m3 highscore]");

        var pMbad = _parser.ParseFilename("Nitro (1990)(Psygnosis)(Disk 2 of 2)(Game)[cr HZ][m baddump - highscore][bootable].adf");
        pMbad.IsModified.Should().BeTrue();
        pMbad.IsBadDump.Should().BeTrue();
        pMbad.DumpFlags.Should().Contain("[m baddump - highscore]");
    }

    /// <summary>
    /// Counterexamples of <see cref="TosecParserPropertyTests"/>: a title word starting with "v" (2 462 names of the
    /// TOSEC DATs of the repository) was read as a version, which cut the title ("Battle Valley" became "Battle").
    /// </summary>
    [Theory]
    [InlineData("Battle Valley (1988)(Hewson).adf", "Battle Valley", "")]
    [InlineData("Miami Vice (1986)(Ocean).adf", "Miami Vice", "")]
    [InlineData("Boulder Dash VI (1990)(Prism).d64", "Boulder Dash VI", "")]
    [InlineData("Spy vs (1984)(First Star).d64", "Spy vs", "")]
    [InlineData("Battle Valley v3.0 (1988)(Hewson).adf", "Battle Valley", "v3.0")]
    [InlineData("Sporttabellen - Verwaltung v3.1 (1990)(-).st", "Sporttabellen - Verwaltung", "v3.1")]
    [InlineData("Utility v.2 (1990)(-).adf", "Utility", "v.2")]
    [InlineData("Utility vP1.00-D1.00 (1990)(-).adf", "Utility", "vP1.00-D1.00")]
    [InlineData("Utility V2 (1990)(-).adf", "Utility", "V2")]
    public void TitleWordStartingWithV_ShouldOnlyBeAVersionWhenADigitFollows(string filename, string expectedTitle, string expectedVersion)
    {
        var p = _parser.ParseFilename(filename);

        p.Title.Should().Be(expectedTitle);
        p.Version.Should().Be(expectedVersion);
    }
}
