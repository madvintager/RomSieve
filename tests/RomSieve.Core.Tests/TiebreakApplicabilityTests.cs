using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Criteria of an order of the 1G1R cascade that have no effect on a collection, given its DAT and the settings: the
/// order of a convention only lists criteria that can separate two of its releases, and each case left rests on what
/// the parsers and the DAT really give.
/// </summary>
public sealed class TiebreakApplicabilityTests
{
    private static string? Reason(TiebreakCriterion criterion, NamingConvention convention, DatCloneRelations relations = DatCloneRelations.Present) =>
        TiebreakApplicability.GetNoEffectReason(criterion, convention, relations)?.Key;

    [Theory]
    [InlineData(NamingConvention.Tosec)]
    [InlineData(NamingConvention.NoIntro)]
    [InlineData(NamingConvention.Redump)]
    [InlineData(NamingConvention.Mame)]
    public void TheCriteriaOfAConvention_ShouldAllHaveAnEffectWithADatThatHasClones(NamingConvention convention)
    {
        TiebreakOrder.CriteriaOf(convention).Where(c => Reason(c, convention) is not null).Should().BeEmpty();
    }

    [Fact]
    public void ACriterionOfAnotherConvention_ShouldHaveNoEffect()
    {
        Reason(TiebreakCriterion.VideoStandard, NamingConvention.Mame).Should().Be("tiebreak.no_effect_convention");
        Reason(TiebreakCriterion.DriverStatus, NamingConvention.Tosec).Should().Be("tiebreak.no_effect_convention");
        Reason(TiebreakCriterion.DriverStatus, NamingConvention.Auto).Should().Be("tiebreak.no_effect_convention", "a release of no known convention follows the order of TOSEC");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(AmigaTarget.Any, "tiebreak.no_effect_amiga_target")]
    [InlineData(AmigaTarget.Ocs, "tiebreak.no_effect_amiga_target")]
    [InlineData(AmigaTarget.Ecs, "tiebreak.no_effect_amiga_target")]
    [InlineData(AmigaTarget.Aga, null)]
    public void AmigaChipset_ShouldOnlySeparateTheVersionsOfAGameForAnAgaAmiga(AmigaTarget? target, string? expected)
    {
        (TiebreakApplicability.GetNoEffectReason(TiebreakCriterion.AmigaChipset, NamingConvention.Tosec, DatCloneRelations.None, target)?.Key)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(DatCloneRelations.NoDat, "tiebreak.no_effect_no_dat")]
    [InlineData(DatCloneRelations.None, "tiebreak.no_effect_no_clones")]
    [InlineData(DatCloneRelations.Present, null)]
    [InlineData(DatCloneRelations.Unknown, null)]
    public void ParentClone_ShouldDependOnTheDatRelations(DatCloneRelations relations, string? expected)
    {
        foreach (var convention in new[] { NamingConvention.NoIntro, NamingConvention.Redump, NamingConvention.Mame })
        {
            Reason(TiebreakCriterion.ParentClone, convention, relations).Should().Be(expected);
        }
    }

    [Fact]
    public void AComparisonWithAPreference_ShouldHaveNoEffectWithoutPreference()
    {
        string? Preference(TiebreakCriterion criterion, VideoPreference? video, MediaPreference? media) =>
            TiebreakApplicability.GetNoEffectReason(criterion, NamingConvention.Tosec, DatCloneRelations.None, preferredVideo: video, preferredMediaType: media)?.Key;

        Preference(TiebreakCriterion.VideoStandard, VideoPreference.None, null).Should().Be("tiebreak.no_effect_no_preference");
        Preference(TiebreakCriterion.VideoStandard, VideoPreference.Pal, null).Should().BeNull();
        Preference(TiebreakCriterion.VideoStandard, null, null).Should().BeNull("nothing is claimed of a preference not known");
        Preference(TiebreakCriterion.PreferredMediaType, null, MediaPreference.None).Should().Be("tiebreak.no_effect_no_preference");
        Preference(TiebreakCriterion.PreferredMediaType, null, MediaPreference.Floppy).Should().BeNull();
    }

    [Fact]
    public void CloneRelations_ShouldComeFromTheCatalog()
    {
        var services = new CoreTestServices();
        var withClones = services.DatService.ParseLogiqxXml("""
            <?xml version="1.0"?>
            <datafile>
            	<game name="Game (USA)"><rom name="Game (USA).sfc" size="4" crc="11111111"/></game>
            	<game name="Game (Europe)" cloneof="Game (USA)"><rom name="Game (Europe).sfc" size="4" crc="22222222"/></game>
            </datafile>
            """);
        var withoutClones = services.DatService.ParseLogiqxXml("""
            <?xml version="1.0"?>
            <datafile>
            	<game name="Game (1989)(Publisher)"><rom name="Game (1989)(Publisher).adf" size="4" crc="11111111"/></game>
            </datafile>
            """);

        TiebreakApplicability.GetCloneRelations(withClones).Should().Be(DatCloneRelations.Present);
        TiebreakApplicability.GetCloneRelations(withoutClones).Should().Be(DatCloneRelations.None);
    }

    [Theory]
    [InlineData("Super Mario World (USA).sfc", "")]
    [InlineData("Star Fox (Japan) (Beta) (1992-11-05).sfc", "1992-11-05")]
    [InlineData("40 Winks (Europe) (En,Es,It) (Proto) (1999-10-07).z64", "1999-10-07")]
    [InlineData("Wipeout XL (USA) (Beta) (1996-08-08).cue", "1996-08-08")]
    [InlineData("Game (USA) (Proto) (1998-12-xx).sfc", "1998-12-xx")]
    [InlineData("Game (USA) (Proto) (2001-03-14T101500).sfc", "2001-03-14T101500")]
    [InlineData("Final Fantasy VII (Europe) (Disc 1) (Rev 1).cue", "")]
    public void NoIntroAndRedumpNames_ShouldGiveTheDateOfTheirBuild(string filename, string expected)
    {
        // The date criterion of No-Intro and Redump separates two builds of a development version
        var parsed = new CoreTestServices().NoIntroParser.ParseRomFilename(filename);

        parsed.Convention.Should().BeOneOf(NamingConvention.NoIntro, NamingConvention.Redump);
        parsed.Date.Should().Be(expected);
        if (expected.Length > 0)
        {
            parsed.MoreInfo.Should().NotContain(info => info.Contains(expected), "the date is read, not kept as a mark");
        }
    }

    [Fact]
    public void TwoBuildsOfAGame_ShouldStayTwoReleasesOfOneGame()
    {
        var services = new CoreTestServices();
        var older = services.NoIntroParser.ParseRomFilename("40 Winks (USA) (Proto) (1999-10-07).z64");
        var newer = services.NoIntroParser.ParseRomFilename("40 Winks (USA) (Proto) (2000-01-10).z64");

        services.FilterService.GetCanonicalGameKey(older).Should().Be(services.FilterService.GetCanonicalGameKey(newer));
        services.FilterService.GetReleaseKey(older).Should().NotBe(services.FilterService.GetReleaseKey(newer));
        services.FilterService.ParseDateTuple(newer.Date).Should().Be((2000, 1, 10));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(true, null)]
    [InlineData(false, "tiebreak.no_effect_no_chipset_in_dat")]
    public void AmigaChipset_ShouldHaveNoEffectWhenNoNameOfTheDatMarksAChipset(bool? hasMarks, string? expected)
    {
        var traits = hasMarks is { } marks ? new DatNameTraits(marks, HasOneMediaFormat: false) : null;

        (TiebreakApplicability.GetNoEffectReason(
            TiebreakCriterion.AmigaChipset, NamingConvention.Tosec, DatCloneRelations.None, AmigaTarget.Aga, nameTraits: traits)?.Key)
            .Should().Be(expected);
        (TiebreakApplicability.GetNoEffectReason(
            TiebreakCriterion.AmigaChipset, NamingConvention.Tosec, DatCloneRelations.None, AmigaTarget.Ocs, nameTraits: traits)?.Key)
            .Should().Be("tiebreak.no_effect_amiga_target", "the machine is said first");
    }

    [Theory]
    [InlineData(TiebreakCriterion.PreferredMediaType)]
    [InlineData(TiebreakCriterion.MediaFidelity)]
    public void FormatCriteria_ShouldHaveNoEffectWhenAllTheFilesOfTheDatHaveOneFormat(TiebreakCriterion criterion)
    {
        string? Format(DatNameTraits? traits) => TiebreakApplicability.GetNoEffectReason(
            criterion, NamingConvention.Tosec, DatCloneRelations.None, preferredMediaType: MediaPreference.Floppy, nameTraits: traits)?.Key;

        Format(new DatNameTraits(HasAmigaChipsetMarks: true, HasOneMediaFormat: true)).Should().Be("tiebreak.no_effect_one_format_in_dat");
        Format(new DatNameTraits(HasAmigaChipsetMarks: true, HasOneMediaFormat: false)).Should().BeNull();
        Format(null).Should().BeNull("nothing is claimed of a DAT not known");
    }

    private static DatNameTraits TraitsOf(params string[] romNames)
    {
        var games = string.Concat(romNames.Select((name, i) =>
            $"<game name=\"G{i}\"><rom name=\"{name}\" size=\"4\" crc=\"{i + 1:x8}\"/></game>"));
        var catalog = new CoreTestServices().DatService.ParseLogiqxXml($"<?xml version=\"1.0\"?><datafile>{games}</datafile>");
        return TiebreakApplicability.GetDatNameTraits(catalog);
    }

    [Fact]
    public void DatNameTraits_ShouldComeFromTheNamesOfTheFilesOfTheDat()
    {
        // An Atari ST DAT: one format, no chipset
        TraitsOf("Game (1990)(Pub).st", "Other (1991)(Pub)(Disk 1 of 2).st").Should().Be(new DatNameTraits(false, true));

        // A chipset, or a fix for AGA, in a name
        TraitsOf("Game (1993)(Pub)(AGA).adf", "Other (1990)(Pub).adf").HasAmigaChipsetMarks.Should().BeTrue();
        TraitsOf("Game (1990)(Pub)[f AGA NOMAD].adf").HasAmigaChipsetMarks.Should().BeTrue();
        TraitsOf("Game (1993)(Pub)(A1200).adf").HasAmigaChipsetMarks.Should().BeTrue();

        // Two formats, a floppy image that keeps the protections, or a media read from the name of an unknown format
        TraitsOf("Game (1990)(Pub).adf", "Game (1990)(Pub).ipf").HasOneMediaFormat.Should().BeFalse();
        TraitsOf("Game (1990)(Pub).adf", "Other (1990)(Pub)(sps).adf").HasOneMediaFormat.Should().BeFalse();
        TraitsOf("Game (1990)(Pub)(Disk 1 of 2).xyz", "Other (1990)(Pub)(Tape).xyz").HasOneMediaFormat.Should().BeFalse();
    }

    [Fact]
    public void DatNameTraits_OfACatalogWithoutEntries_ShouldClaimNothing()
    {
        var catalog = new CoreTestServices().DatService.ParseLogiqxXml("<?xml version=\"1.0\"?><datafile></datafile>");

        TiebreakApplicability.GetDatNameTraits(catalog).Should().Be(new DatNameTraits(HasAmigaChipsetMarks: true, HasOneMediaFormat: false));
    }

    [Fact]
    public async Task WithADat_TheFormatOfAFile_ShouldBeThatOfItsDatName()
    {
        // A DAT whose files all have one format: a file identified by its checksum but named otherwise in the source
        // does not separate the releases, so that greying out the format criteria is right
        using var tmp = new TempDirectoryFixture();
        var services = new CoreTestServices();
        var src = tmp.CreateSubdirectory("src");
        var dest = tmp.CreateSubdirectory("dest");
        var german = tmp.WriteBytes(Path.Combine("src", "Game (1990)(Pub)(de).st"), "GAME-DE"u8.ToArray());
        var french = tmp.WriteBytes(Path.Combine("src", "Game (1990)(Pub)(fr).stx"), "GAME-FR"u8.ToArray());
        var germanCrc = await services.ChecksumService.ComputeFileCrc32Async(german);
        var frenchCrc = await services.ChecksumService.ComputeFileCrc32Async(french);
        var datXml =
            "<?xml version=\"1.0\"?><datafile><header><name>Atari ST - Games - [ST]</name></header>"
            + $"<game name=\"Game (1990)(Pub)(de)\"><rom name=\"Game (1990)(Pub)(de).st\" size=\"7\" crc=\"{germanCrc}\"/></game>"
            + $"<game name=\"Game (1990)(Pub)(fr)\"><rom name=\"Game (1990)(Pub)(fr).st\" size=\"7\" crc=\"{frenchCrc}\"/></game>"
            + "</datafile>";
        var dat = tmp.WriteText("st.dat", datXml);
        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dest,
            NamingConvention = NamingConvention.Tosec,
            DatFile = dat,
            Pattern = string.Empty,
            Filters = new FilterConfig { OneGameOneRom = true, PriorityLanguages = ["en"] }
        };

        TiebreakApplicability.GetDatNameTraits(services.DatService.ParseLogiqxXml(datXml)).HasOneMediaFormat.Should().BeTrue();
        var result = await services.Processor.ExecuteAsync(config, isDryRun: true);

        var lost = result.Items.Single(it => Path.GetFileName(it.SourcePath) == "Game (1990)(Pub)(fr).stx");
        lost.Status.Should().Be(FileStatus.Excluded);
        lost.EliminatedBy.Should().Be(TiebreakCriterion.Name, "the .stx file is a .st image of the DAT");
    }

    [Theory]
    [InlineData("Final Fantasy VII (Europe) (Disc 1).cue")]
    [InlineData("Gran Turismo (USA) (Rev 1).chd")]
    public void RedumpNames_ShouldAllBeVerifiedDumps(string filename)
    {
        // The order of Redump has no good dump criterion: every dump of the DAT is verified
        var services = new CoreTestServices();
        var parsed = services.NoIntroParser.ParseRomFilename(filename);

        parsed.Convention.Should().Be(NamingConvention.Redump);
        services.FilterService.GetDumpQualityRank(parsed).Should().Be(2);
    }
}
