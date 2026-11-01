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
    [InlineData("Super Mario World (USA).sfc")]
    [InlineData("Star Fox (Japan) (Beta) (1992-11-05).sfc")]
    [InlineData("Final Fantasy VII (Europe) (Disc 1) (Rev 1).cue")]
    public void NoIntroAndRedumpNames_ShouldNeverCarryADate(string filename)
    {
        // The orders of No-Intro and Redump have no date criterion
        var parsed = new CoreTestServices().NoIntroParser.ParseRomFilename(filename);

        parsed.Convention.Should().BeOneOf(NamingConvention.NoIntro, NamingConvention.Redump);
        parsed.Date.Should().BeNullOrEmpty();
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
