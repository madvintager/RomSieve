using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Analysis of MAME machine descriptions (<see cref="MameDescriptionParser"/>): title, regions, dates, revisions,
/// versions, sets, languages and statuses, on real descriptions of MAME 0.289, and their use by the 1G1R comparisons.
/// </summary>
public sealed class MameDescriptionParserTests
{
    private readonly MameDescriptionParser _parser = new MameDescriptionParser();
    private readonly ReleaseScoringService _scoring = new();

    private static DatCatalog LoadListXml() =>
        new CoreTestServices().DatService.ParseDatFileAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mame", "listxml.dat"))
            .GetAwaiter().GetResult();

    [Theory]
    [InlineData("Street Fighter II: The World Warrior (World 910522)", "Street Fighter II: The World Warrior", "WO", "1991-05-22", "")]
    [InlineData("Street Fighter II: The World Warrior (USA 910522, Rev. G)", "Street Fighter II: The World Warrior", "US", "1991-05-22", "Rev G")]
    [InlineData("Street Fighter II: The World Warrior (Japan 911210, CPS-B-13)", "Street Fighter II: The World Warrior", "JP", "1991-12-10", "")]
    [InlineData("Street Fighter II': Champion Edition (World 920513)", "Street Fighter II': Champion Edition", "WO", "1992-05-13", "")]
    [InlineData("All American Football (Leland, rev E)", "All American Football", "", "", "Rev E")]
    [InlineData("Galaga (Namco rev. B)", "Galaga", "", "", "Rev B")]
    [InlineData("Mortal Kombat II (rev L3.1)", "Mortal Kombat II", "", "", "Rev L3.1")]
    [InlineData("Two Minute Drill (Ver 2.93A 1994/02/16)", "Two Minute Drill", "", "1994-02-16", "v2.93A")]
    [InlineData("Arch Rivals (rev 4.0 6/29/89)", "Arch Rivals", "", "1989-06-29", "Rev 4.0")]
    [InlineData("Area 51 (Time Warner license, Oct 17, 1996)", "Area 51", "", "1996-10-17", "")]
    [InlineData("Time Crisis 4 (Japan, TSF1001-NA-A)", "Time Crisis 4", "JP", "", "")]
    [InlineData("Puck Man (Japan, set 1)", "Puck Man", "JP", "", "")]
    [InlineData("Metal Slug - Super Vehicle-001", "Metal Slug - Super Vehicle-001", "", "", "")]
    public void ParseDescription_ShouldReadTitleRegionDateAndRevision(
        string description,
        string title,
        string country,
        string date,
        string version)
    {
        var parsed = _parser.ParseDescription(description);

        parsed.Title.Should().Be(title);
        parsed.BaseTitle.Should().Be(title);
        string.Join(",", parsed.CountryCodes).Should().Be(country);
        parsed.Date.Should().Be(date);
        parsed.Version.Should().Be(version);
        parsed.Convention.Should().Be(NamingConvention.Mame);
        parsed.IsConform.Should().BeTrue();
        parsed.IsMultiMedia.Should().BeFalse();
    }

    [Fact]
    public void ParseDescription_Statuses_ShouldMarkBootlegsHacksPrototypesAndSets()
    {
        var bootleg = _parser.ParseDescription("Street Fighter II: The World Warrior (bootleg, set 1)");
        bootleg.IsPirate.Should().BeTrue();
        bootleg.IsModified.Should().BeTrue();
        bootleg.IsAlternate.Should().BeFalse();
        bootleg.SoftwareMods.Should().Equal("(bootleg)");

        var scrambled = _parser.ParseDescription("Bare Knuckle II (scrambled bootleg of Mega Drive version)");
        scrambled.IsPirate.Should().BeTrue();
        scrambled.Title.Should().Be("Bare Knuckle II");

        var hack = _parser.ParseDescription("Royal on Ten (hack of Noraut Deluxe Poker)");
        hack.IsHack.Should().BeTrue();
        hack.IsModified.Should().BeTrue();

        var prototypeHack = _parser.ParseDescription("SegaSonic Bros. (prototype, hack)");
        prototypeHack.DevStatus.Should().Be("prototype");
        prototypeHack.IsHack.Should().BeTrue();

        _parser.ParseDescription("Air Race (prototype)").DevStatus.Should().Be("prototype");
        _parser.ParseDescription("Puzzle Game (US prototype?)").CountryCodes.Should().Equal("US");
        _parser.ParseDescription("Puzzle Game (US prototype?)").DevStatus.Should().Be("prototype");

        var set2 = _parser.ParseDescription("The Three Stooges In Brides Is Brides (set 2)");
        set2.IsAlternate.Should().BeTrue();
        set2.MoreInfo.Should().Equal("(set 2)");
        _parser.ParseDescription("The Three Stooges In Brides Is Brides (set 1)").IsAlternate.Should().BeFalse();

        _parser.ParseDescription("Street Fighter II: The World Warrior (World 910204, conversion)")
            .MoreInfo.Should().Equal("(conversion)");
    }

    [Fact]
    public void ParseDescription_Languages_ShouldComeFromTheDescriptionOrTheRegion()
    {
        // MAME writes a language in full; a region implies its language otherwise, as for No-Intro
        _parser.ParseDescription("Quiz Game (German)").LanguageCodes.Should().Equal("de");
        _parser.ParseDescription("Quiz Game (Italian speech)").LanguageCodes.Should().Equal("it");
        _parser.ParseDescription("Street Fighter II: The World Warrior (Japan 911210, CPS-B-13)").LanguageCodes.Should().Equal("ja");
        _parser.ParseDescription("Air Race (prototype)").LanguageCodes.Should().BeEmpty();

        // "Italian bootleg" is a bootleg made in Italy
        var italianBootleg = _parser.ParseDescription("Space Game (Italian bootleg)");
        italianBootleg.LanguageCodes.Should().BeEmpty();
        italianBootleg.IsPirate.Should().BeTrue();
    }

    [Theory]
    [InlineData("Game (USA, Europe)", "US,EU")]
    [InlineData("Game (Euro 940224)", "EU")]
    [InlineData("Game (Hispanic 940224)", "LA")]
    [InlineData("Game (Hong Kong, set 2)", "HK")]
    [InlineData("Game (UK)", "GB")]
    [InlineData("Game (Ukraine)", "UA")]
    [InlineData("Game (World?)", "WO")]
    [InlineData("Game (TAB Austria, bootleg)", "")]
    public void ParseDescription_Regions_ShouldMapToCountryCodes(string description, string countries)
    {
        string.Join(",", _parser.ParseDescription(description).CountryCodes).Should().Be(countries);
    }

    [Theory]
    [InlineData("Revolution X (Revised)", "")]
    [InlineData("Game (vertical)", "")]
    [InlineData("Game (V. 43.17)", "v43.17")]
    [InlineData("Game (ver. 2.0)", "v2.0")]
    [InlineData("Game (Version 1.0R Dual)", "v1.0R")]
    public void ParseDescription_Versions_ShouldNotBeReadInOrdinaryWords(string description, string version)
    {
        _parser.ParseDescription(description).Version.Should().Be(version);
    }

    [Theory]
    [InlineData("Game (World 991231)", "1999-12-31")]
    [InlineData("Game (World 050101)", "2005-01-01")]
    [InlineData("Game (Aristocrat 10219211)", "")]
    [InlineData("Game (PAL)", "")]
    public void ParseDescription_ShortDates_ShouldBeValidated(string description, string date)
    {
        _parser.ParseDescription(description).Date.Should().Be(date);
    }

    [Fact]
    public void ParseDescription_VideoStandard_ShouldBeRead()
    {
        _parser.ParseDescription("Game (PAL)").Video.Should().Be("PAL");
        _parser.ParseDescription("Game (NTSC)").Video.Should().Be("NTSC");
    }

    [Theory]
    [InlineData("1991", "1991")]
    [InlineData("199?", "199x")]
    [InlineData("19??", "19xx")]
    [InlineData("?", "")]
    [InlineData(null, "")]
    public void ParseDescription_DatYear_ShouldGiveTheDateWhenTheDescriptionHasNone(string? year, string date)
    {
        _parser.ParseDescription("Metal Slug - Super Vehicle-001", year).Date.Should().Be(date);
        _parser.ParseDescription("Game (World 910522)", year).Date.Should().Be("1991-05-22");
    }

    [Fact]
    public void ParseGame_ShouldUseTheDatInformation()
    {
        var catalog = LoadListXml();

        var sf2ug = _parser.ParseGame(catalog.GameByName["sf2ug"], filePath: "/roms/sf2ug.zip", fileSize: 1234);
        sf2ug.RawFilename.Should().Be("sf2ug");
        sf2ug.FilePath.Should().Be("/roms/sf2ug.zip");
        sf2ug.FileSize.Should().Be(1234);
        sf2ug.Publisher.Should().Be("Capcom");
        sf2ug.CloneOf.Should().Be("sf2");
        sf2ug.Version.Should().Be("Rev G");
        sf2ug.IsBadDump.Should().BeFalse();
        sf2ug.IsVerified.Should().BeTrue();

        // A ROM known as a bad dump is the best dump known, the one MAME needs: the set is not a bad dump
        var pigs = _parser.ParseGame(catalog.GameByName["3lilpigs"]);
        pigs.Date.Should().Be("199x");
        pigs.Publisher.Should().Be("Doyle & Assoc.");
        pigs.CloneOf.Should().BeNull();
        pigs.IsBadDump.Should().BeFalse();
        pigs.IsVerified.Should().BeTrue();
    }

    [Fact]
    public void ParseGame_EveryMachineOfTheListXml_ShouldHaveATitle()
    {
        var catalog = LoadListXml();

        foreach (var game in catalog.Games)
        {
            _parser.ParseGame(game).Title.Should().NotBeEmpty(game.Name);
        }
    }

    /// <summary>
    /// The Filters page hides the translations, unlicensed releases and modern re-releases with the MAME options: a
    /// MAME description never marks them, even in words No-Intro uses for them.
    /// </summary>
    [Theory]
    [InlineData("Pac-Man (translation)")]
    [InlineData("Pac-Man (Translated En)")]
    [InlineData("Pac-Man (Unl)")]
    [InlineData("Pac-Man (unlicensed, Homebrew)")]
    [InlineData("Pac-Man (Aftermarket)")]
    [InlineData("Pac-Man (Virtual Console)")]
    [InlineData("Pac-Man (Switch Online, Rerelease)")]
    public void ParseDescription_ShouldNeverMarkWhatOnlyNoIntroNamesCarry(string description)
    {
        var parsed = _parser.ParseDescription(description);

        parsed.IsTranslation.Should().BeFalse();
        parsed.Copyright.Should().BeNullOrEmpty();
        parsed.IsRerelease.Should().BeFalse();
    }

    [Fact]
    public void ParseGame_NoMachineOfTheListXml_ShouldBeATranslationAnUnlicensedReleaseOrARerelease()
    {
        foreach (var game in LoadListXml().Games)
        {
            var parsed = _parser.ParseGame(game);
            parsed.IsTranslation.Should().BeFalse(game.Name);
            parsed.Copyright.Should().BeNullOrEmpty(game.Name);
            parsed.IsRerelease.Should().BeFalse(game.Name);
        }
    }

    [Fact]
    public void ParseGame_VariantsOfAGame_ShouldShareTheirTitleButNotAnotherGame()
    {
        var catalog = LoadListXml();
        var titles = new[] { "sf2", "sf2ua", "sf2ug", "sf2uh", "sf2j", "sf2b", "sf2en" }
            .Select(name => _parser.ParseGame(catalog.GameByName[name]).Title)
            .Distinct()
            .ToList();

        titles.Should().Equal("Street Fighter II: The World Warrior");
        _parser.ParseGame(catalog.GameByName["sf2ce"]).Title.Should().NotBe(titles[0]);
    }

    [Fact]
    public void ParsedDescriptions_ShouldBeComparableByThe1G1RCriteria()
    {
        var catalog = LoadListXml();
        ParsedRomName Parse(string name) => _parser.ParseGame(catalog.GameByName[name]);

        // Revisions: Rev. H after Rev. G, rev E after rev B
        ReleaseScoringService.CompareTuples(_scoring.ParseRevisionTuple(Parse("sf2uh")), _scoring.ParseRevisionTuple(Parse("sf2ug")))
            .Should().BePositive();
        ReleaseScoringService.CompareTuples(_scoring.ParseRevisionTuple(Parse("aafb")), _scoring.ParseRevisionTuple(Parse("aafbb")))
            .Should().BePositive();

        // Dates: the world release of 910522 after the US one of 910206
        _scoring.ParseDateTuple(Parse("sf2").Date).Should().Be((1991, 5, 22));
        _scoring.ParseDateTuple(Parse("sf2ua").Date).CompareTo(_scoring.ParseDateTuple(Parse("sf2").Date)).Should().BeNegative();

        // A bootleg is not clean, the original is
        _scoring.IsReleaseClean(Parse("sf2")).Should().BeTrue();
        _scoring.IsReleaseClean(Parse("sf2b")).Should().BeFalse();
    }

    [Fact]
    public void ParseRomFilename_MameConvention_ShouldParseFileNamesAsAuto()
    {
        // Until MAME sets are identified by the DAT, a file name is parsed as without convention
        var parser = new CoreTestServices().NoIntroParser;
        const string name = "Super Game (USA).zip";

        var mame = parser.ParseRomFilename(name, convention: NamingConvention.Mame);
        var auto = parser.ParseRomFilename(name, convention: NamingConvention.Auto);

        mame.Should().BeEquivalentTo(auto);
    }
}
