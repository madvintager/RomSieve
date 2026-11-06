using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.Core.Tests;

/// <summary>
/// Unit tests for No-Intro and Redump parser.
/// </summary>
public sealed class NoIntroRedumpParserTests
{
    private readonly TosecParser _tosecParser;
    private readonly NoIntroRedumpParser _parser;

    public NoIntroRedumpParserTests()
    {
        _tosecParser = new TosecParser();
        _parser = new NoIntroRedumpParser(_tosecParser);
    }

    [Fact]
    public void BasicNoIntroParsing_ShouldExtractTitleCountryLanguageAndConvention()
    {
        var parsed = _parser.ParseFilename("Super Mario World (USA).sfc");

        parsed.IsConform.Should().BeTrue();
        parsed.Title.Should().Be("Super Mario World");
        parsed.Country.Should().Be("US");
        parsed.CountryCodes.Should().Equal("US");
        parsed.Language.Should().Be("en");
        parsed.LanguageCodes.Should().Equal("en");
        parsed.IsMultilingual.Should().BeFalse();
        parsed.IsVerified.Should().BeTrue();
        parsed.IsBadDump.Should().BeFalse();
        parsed.MediaType.Should().Be("cartridge");
        parsed.IsMultiMedia.Should().BeFalse();
        parsed.Convention.Should().Be(NamingConvention.NoIntro);
    }

    [Fact]
    public void NoIntroInvertedArticles_ShouldBeRestoredInTitle()
    {
        var parsed = _parser.ParseFilename("Legend of Zelda, The - A Link to the Past (USA).sfc");

        parsed.IsConform.Should().BeTrue();
        parsed.Title.Should().Be("The Legend of Zelda - A Link to the Past");
        parsed.Country.Should().Be("US");
    }

    [Fact]
    public void NoIntroMultilingualLanguages_ShouldParseExplicitCommaSeparatedLanguages()
    {
        var parsed = _parser.ParseFilename("Dino Dini's Soccer! (Europe) (En,Fr,De).sfc");

        parsed.IsConform.Should().BeTrue();
        parsed.Title.Should().Be("Dino Dini's Soccer!");
        parsed.Country.Should().Be("EU");
        parsed.IsMultilingual.Should().BeTrue();
        parsed.MultilingualCount.Should().Be(3);
        parsed.Language.Should().Be("M3");
        parsed.LanguageCodes.Should().Equal("en", "fr", "de");
    }

    [Fact]
    public void NoIntroMultiRegions_ShouldExtractMultipleCountryCodes()
    {
        var parsed = _parser.ParseFilename("Sonic The Hedgehog (USA, Europe).md");

        parsed.IsConform.Should().BeTrue();
        parsed.Title.Should().Be("Sonic The Hedgehog");
        parsed.CountryCodes.Should().Equal("US", "EU");
        parsed.Country.Should().Contain("US").And.Contain("EU");
    }

    [Theory]
    [InlineData("Roger Clemens' MVP Baseball (USA) (Rev 1).sfc", "Rev 1")]
    [InlineData("Sonic the Hedgehog 2 (World) (Rev A).md", "Rev A")]
    [InlineData("Super Mario Bros. (USA) (PRG0).nes", "PRG0")]
    public void NoIntroRevisionsAndVersions_ShouldExtractRevisionTag(string filename, string expectedVersion)
    {
        var parsed = _parser.ParseFilename(filename);
        parsed.Version.Should().Be(expectedVersion);
    }

    [Fact]
    public void NoIntroDiscAndMultimedia_ShouldDetectMultiDiscSet()
    {
        var parsed = _parser.ParseFilename("Final Fantasy VII (USA) (Disc 1).chd");

        parsed.IsConform.Should().BeTrue();
        parsed.Title.Should().Be("Final Fantasy VII");
        parsed.DiscNumber.Should().Be(1);
        parsed.IsMultiMedia.Should().BeTrue();
        parsed.MediaType.Should().Be("cd");
    }

    // Floppy disks of the No-Intro floppy DATs (Acorn - Archimedes, 2023-10-29): "Disk", with the label of some sets
    [Theory]
    [InlineData("Alone in the Dark (United Kingdom) (Disk 8).jfd", 8, null, "Alone in the Dark")]
    [InlineData("Simon the Sorcerer (Europe) (Disk 10).adf", 10, null, "Simon the Sorcerer")]
    [InlineData("Chocks Away (United Kingdom) (Disk 2, Maps Disk).adf", 2, null, "Chocks Away")]
    [InlineData("Chocks Away (United Kingdom) (Disk 3, Misson Addon).adf", 3, null, "Chocks Away")]
    [InlineData("Emotions - Search for Humanity (United Kingdom) (v1.11) (Disk 1 - Installation Disc One).jfd", 1, null, "Emotions - Search for Humanity")]
    [InlineData("Krisalis Collection, The (United Kingdom) (Disk 3 - Revelation! - BUZZ Version).jfd", 3, null, "The Krisalis Collection")]
    [InlineData("Game (Japan) (Disk 1 Side A).fds", 1, null, "Game")]
    [InlineData("Game (Europe) (Disk 2 of 3).adf", 2, 3, "Game")]
    [InlineData("Game (USA) (Disc 2).chd", 2, null, "Game")]
    public void NoIntroFloppyDisk_ShouldReadTheDiskNumberAndKeepItOutOfTheOtherInformation(
        string filename, int disk, int? total, string title)
    {
        var parsed = _parser.ParseFilename(filename);

        parsed.IsConform.Should().BeTrue();
        parsed.Convention.Should().Be(NamingConvention.NoIntro);
        parsed.Title.Should().Be(title);
        parsed.DiscNumber.Should().Be(disk);
        parsed.DiscTotal.Should().Be(total);
        parsed.IsMultiMedia.Should().BeTrue();
        parsed.MoreInfo.Should().BeEmpty("the disk and its label are not an information of the release");
    }

    [Fact]
    public void NoIntroFloppyDisk_DetectedConvention_ShouldBeNoIntro()
    {
        var parsed = _parser.ParseRomFilename("Alone in the Dark (United Kingdom) (Disk 1).jfd");

        parsed.Convention.Should().Be(NamingConvention.NoIntro);
        parsed.DiscNumber.Should().Be(1);
    }

    [Theory]
    [InlineData("Disk Station (Japan).dsk", "Disk Station")]
    [InlineData("Game (Japan) (Disk Writer).fds", "(Disk Writer)")]
    [InlineData("Game (Europe) (Disk 1.5).adf", "(Disk 1.5)")]
    public void NoIntroDiskWithoutNumber_ShouldNotBeADisk(string filename, string kept)
    {
        var parsed = _parser.ParseFilename(filename);

        parsed.DiscNumber.Should().BeNull();
        parsed.IsMultiMedia.Should().BeFalse();
        (parsed.Title + string.Join(string.Empty, parsed.MoreInfo)).Should().Contain(kept);
    }

    [Fact]
    public void RedumpOpticalTracks_ShouldExtractTrackNumberAndRedumpConvention()
    {
        var pCue = _parser.ParseRomFilename("Dennis (Europe).cue");
        pCue.Convention.Should().Be(NamingConvention.Redump);
        pCue.IsConform.Should().BeTrue();
        pCue.MediaType.Should().Be("cd");

        var pBin = _parser.ParseRomFilename("Dennis (Europe) (Track 01).bin");
        pBin.Convention.Should().Be(NamingConvention.Redump);
        pBin.IsConform.Should().BeTrue();
        pBin.TrackNumber.Should().Be(1);
        pBin.MediaType.Should().Be("cd");
    }

    [Theory]
    [InlineData("Robocop (1991)(Ocean).adf", NamingConvention.Tosec)]
    [InlineData("Super Mario (1990)(Nintendo)(US)[!].bin", NamingConvention.Tosec)]
    [InlineData("Game (19xx)(Publisher)(US).adf", NamingConvention.Tosec)]
    [InlineData("Game (199x)(-)(FR)[b].st", NamingConvention.Tosec)]
    [InlineData("Game (1990-xx)(Ocean)(GB).adf", NamingConvention.Tosec)]
    [InlineData("Super Mario World (USA).sfc", NamingConvention.NoIntro)]
    [InlineData("Dino Dini (Europe) (En,Fr).zip", NamingConvention.NoIntro)]
    [InlineData("Tomb Raider (Europe) (Track 1).bin", NamingConvention.Redump)]
    [InlineData("Castlevania (USA).cue", NamingConvention.Redump)]
    public void NamingConventionDetection_ShouldAutomaticallyIdentifyStandard(string filename, NamingConvention expected)
    {
        NoIntroRedumpParser.DetectNamingConvention(filename).Should().Be(expected);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("random_archive_without_region.zip")]
    public void NonConformantFilenames_ShouldBeFlaggedAsNonConform(string filename)
    {
        var parsed = _parser.ParseRomFilename(filename);
        parsed.IsConform.Should().BeFalse();
    }

    [Fact]
    public void NoIntroImpliedLanguagesForEuropeAndWorld_ShouldDefaultToEnglish()
    {
        var pEu = _parser.ParseFilename("Aaahh!!! Real Monsters (Europe).sfc");
        pEu.IsConform.Should().BeTrue();
        pEu.Country.Should().Be("EU");
        pEu.CountryCodes.Should().Equal("EU");
        pEu.Language.Should().Be("en");
        pEu.LanguageCodes.Should().Equal("en");
        pEu.Video.Should().Be("PAL");

        var pWo = _parser.ParseFilename("Sonic the Hedgehog (World).md");
        pWo.IsConform.Should().BeTrue();
        pWo.Country.Should().Be("WO");
        pWo.Language.Should().Be("en");
        pWo.LanguageCodes.Should().Equal("en");
    }

    [Fact]
    public void NoIntroAttributesParsing_ShouldClassifyPirateHackAltTranslationDevUnlicensedDemoAndBadDump()
    {
        var pPirate = _parser.ParseFilename("Super Mario World (USA) (Pirate).sfc");
        pPirate.IsPirate.Should().BeTrue();
        pPirate.IsModified.Should().BeTrue();

        var pHack = _parser.ParseFilename("Super Mario World (USA) (Hack).sfc");
        pHack.IsHack.Should().BeTrue();
        pHack.IsModified.Should().BeTrue();

        var pAlt = _parser.ParseFilename("Street Fighter II (USA) (Alt 1).sfc");
        pAlt.IsAlternate.Should().BeTrue();

        var pTr = _parser.ParseFilename("Final Fantasy V (Japan) (Translated En).sfc");
        pTr.IsTranslation.Should().BeTrue();

        var pBeta = _parser.ParseFilename("Sonic The Hedgehog (USA) (Beta 2).md");
        pBeta.DevStatus.Should().Be("Beta 2");

        var pProto = _parser.ParseFilename("Castlevania (USA) (Proto 1).nes");
        pProto.DevStatus.Should().Be("Proto 1");

        var pUnl = _parser.ParseFilename("Micro Mages (USA) (Unl).nes");
        pUnl.Copyright.Should().Be("Unlicensed");

        var pHb = _parser.ParseFilename("Flappy Bird (World) (Homebrew).nes");
        pHb.Copyright.Should().Be("Unlicensed");

        var pDemo = _parser.ParseFilename("Gran Turismo (USA) (Demo).bin", isRedump: true);
        pDemo.Demo.Should().Be("demo");

        // Numbered or described demos are demos too, a word that only starts like one is not
        foreach (var demo in new[]
        {
            "Elite (Europe) (Demo 1) (Game).md",
            "Tomoyasu Hotei - Stolen Song (Japan) (Taikenban 2).bin",
            "Turok - Dinosaur Hunter (USA) (Demo) (Kiosk, E3 1997).z64",
            "Astronoka (Japan) (Demo Movie).bin"
        })
        {
            _parser.ParseFilename(demo).Demo.Should().Be("demo", demo);
        }

        _parser.ParseFilename("Demolition Man (Europe) (Demolition).md").Demo.Should().BeNull();

        var pBad = _parser.ParseFilename("Super Mario Bros. (USA) [b].nes");
        pBad.IsBadDump.Should().BeTrue();
        pBad.IsVerified.Should().BeFalse();
    }

    [Fact]
    public void NoIntroBiosPrefixAndUnclosedTitleParens_ShouldPreserveSpecialTitlesAndRegions()
    {
        var pBios = _parser.ParseRomFilename("[BIOS] Super NES CD-ROM - Boot ROM (Japan) (En) (Proto).sfc");
        pBios.IsConform.Should().BeTrue();
        pBios.Convention.Should().Be(NamingConvention.NoIntro);
        pBios.Title.Should().Be("[BIOS] Super NES CD-ROM - Boot ROM");
        pBios.Country.Should().Be("JP");
        pBios.Language.Should().Be("en");
        pBios.DevStatus.Should().Be("Proto");
        pBios.IsBadDump.Should().BeFalse();

        var pEmoticon = _parser.ParseRomFilename("Odekake Lester - Lelele no Le (^^; (Japan).sfc");
        pEmoticon.IsConform.Should().BeTrue();
        pEmoticon.Convention.Should().Be(NamingConvention.NoIntro);
        pEmoticon.Title.Should().Be("Odekake Lester - Lelele no Le (^^;");
        pEmoticon.Country.Should().Be("JP");
        pEmoticon.Language.Should().Be("ja");

        var pPeru = _parser.ParseRomFilename("Futbol Peruano 97 (Peru) (Es) (Pirate) [b].sfc");
        pPeru.IsConform.Should().BeTrue();
        pPeru.Convention.Should().Be(NamingConvention.NoIntro);
        pPeru.Title.Should().Be("Futbol Peruano 97");
        pPeru.Country.Should().Be("PE");
        pPeru.Language.Should().Be("es");
        pPeru.IsPirate.Should().BeTrue();
        pPeru.IsBadDump.Should().BeTrue();

        var pIndia = _parser.ParseRomFilename("TwinBee (India) (En) (Pirate).nes");
        pIndia.IsConform.Should().BeTrue();
        pIndia.Country.Should().Be("IN");

        var pCzech = _parser.ParseRomFilename("Magic Island (Czech).cue");
        pCzech.IsConform.Should().BeTrue();
        pCzech.Convention.Should().Be(NamingConvention.Redump);
        pCzech.Country.Should().Be("CZ");
        pCzech.Language.Should().Be("cs");

        var pIsrael = _parser.ParseRomFilename("FIFA 2000 (Israel).cue");
        pIsrael.IsConform.Should().BeTrue();
        pIsrael.Convention.Should().Be(NamingConvention.Redump);
        pIsrael.Country.Should().Be("IL");
        pIsrael.Language.Should().Be("he");
    }

    [Theory]
    [InlineData("Super Mario World (USA).sfc", false, null)]
    [InlineData("Super Mario World (USA) (Virtual Console).sfc", true, "Virtual Console")]
    [InlineData("Sonic The Hedgehog (USA) (Wii Virtual Console).md", true, "Wii Virtual Console")]
    [InlineData("Star Fox 2 (USA, Europe) (Classic Mini, Switch Online).sfc", true, "Classic Mini, Switch Online")]
    [InlineData("Super Mario 64 (USA) (LodgeNet).z64", true, "LodgeNet")]
    [InlineData("Castlevania (USA) (Castlevania Anniversary Collection).nes", true, "Castlevania Anniversary Collection")]
    [InlineData("Shantae (USA) (Limited Run Games).gbc", true, "Limited Run Games")]
    public void NoIntroRereleaseTagsParsing_ShouldDetectModernRereleases(
        string filename,
        bool expectedIsRerelease,
        string? expectedTag)
    {
        var parsed = _parser.ParseFilename(filename);

        parsed.IsRerelease.Should().Be(expectedIsRerelease);
        parsed.RereleaseTag.Should().Be(expectedTag);
    }

    /// <summary>
    /// A No-Intro / Redump name never marks a verified dump: every dump that is not bad counts as verified. "Verified
    /// dumps only" therefore keeps the same files as "no bad dumps", which is why the Filters page only offers it for
    /// TOSEC (or when a configuration already uses it).
    /// </summary>
    [Theory]
    [InlineData("Super Mario World (USA).sfc")]
    [InlineData("Super Mario World (USA) [b].sfc")]
    [InlineData("Super Mario World (USA) (Bad Dump).sfc")]
    [InlineData("Super Mario World (USA) [!].sfc")]
    [InlineData("Super Mario World (USA) (Alt 1).sfc")]
    [InlineData("Super Mario World (USA) (Beta).sfc")]
    [InlineData("Super Mario World (USA) (Unl).sfc")]
    [InlineData("Super Mario World (USA) (Hack).sfc")]
    public void DumpThatIsNotBad_ShouldCountAsVerifiedAndBeKept(string filename)
    {
        var parsed = _parser.ParseFilename(filename);
        parsed.IsVerified.Should().Be(!parsed.IsBadDump);

        var filters = RomFilterService.CreateDefault();
        var keepAll = new FilterConfig
        {
            NoIntroExcludeHack = false,
            NoIntroExcludePirate = false,
            NoIntroExcludeUnlicensed = false,
            NoIntroExcludeDevVersions = false
        };
        filters.EvaluateFilter(parsed, keepAll).IsKept.Should().Be(!parsed.IsBadDump);
    }
}
