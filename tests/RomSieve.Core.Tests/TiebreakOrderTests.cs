using RomSieve.Core.Models;
using static RomSieve.Core.Models.TiebreakCriterion;

namespace RomSieve.Core.Tests;

/// <summary>
/// Orders of the 1G1R criteria, one per convention: the criteria of each convention and their default orders in the
/// two region modes, repair of a stored order, consistency with the region mode, and stable names in
/// <c>config.json</c>, including those of the format before the orders were split.
/// </summary>
public sealed class TiebreakOrderTests
{
    [Fact]
    public void DefaultOrders_ShouldListTheCriteriaOfEachConvention()
    {
        TiebreakOrder.Default(NamingConvention.Tosec).Should().Equal(
            LanguageRank, CompleteMultidiskSet, TiebreakCriterion.AmigaChipset, GoodDump, OriginalRelease, CountryRank, VideoStandard,
            PreferredMediaType, MediaFidelity, LanguageCount, RegionAffinity, TiebreakCriterion.Version, Revision, Date, Completeness);
        TiebreakOrder.Default(NamingConvention.NoIntro).Should().Equal(
            LanguageRank, GoodDump, OriginalRelease, CountryRank, VideoStandard, LanguageCount, RegionAffinity, TiebreakCriterion.Version,
            Revision, ParentClone);
        TiebreakOrder.Default(NamingConvention.Redump).Should().Equal(
            LanguageRank, CompleteMultidiskSet, OriginalRelease, CountryRank, VideoStandard, MediaFidelity, LanguageCount, RegionAffinity,
            TiebreakCriterion.Version, Revision, Completeness, ParentClone);
        TiebreakOrder.Default(NamingConvention.Mame).Should().Equal(
            CompleteMultidiskSet, OriginalRelease, DriverStatus, ParentClone, LanguageRank, CountryRank, TiebreakCriterion.Version, Revision,
            Date, RegionAffinity);
    }

    [Theory]
    [InlineData(NamingConvention.Tosec, new[] { DriverStatus, ParentClone })]
    [InlineData(NamingConvention.NoIntro, new[] { CompleteMultidiskSet, TiebreakCriterion.AmigaChipset, DriverStatus, PreferredMediaType, MediaFidelity, Date, Completeness })]
    [InlineData(NamingConvention.Redump, new[] { TiebreakCriterion.AmigaChipset, GoodDump, DriverStatus, PreferredMediaType, Date })]
    [InlineData(NamingConvention.Mame, new[] { TiebreakCriterion.AmigaChipset, GoodDump, VideoStandard, PreferredMediaType, MediaFidelity, LanguageCount, Completeness })]
    public void CriteriaOf_ShouldLeaveOutTheCriteriaThatCannotSeparateTwoReleasesOfTheConvention(NamingConvention convention, TiebreakCriterion[] leftOut)
    {
        var all = Enum.GetValues<TiebreakCriterion>().Where(c => c is not None and not Name);

        all.Except(TiebreakOrder.CriteriaOf(convention)).Should().BeEquivalentTo(leftOut);
    }

    [Fact]
    public void EveryCriterion_ShouldBelongToTheOrderOfAConvention()
    {
        var used = TiebreakOrder.Conventions.SelectMany(TiebreakOrder.CriteriaOf).ToHashSet();

        used.Should().BeEquivalentTo(Enum.GetValues<TiebreakCriterion>().Where(c => c is not None and not Name));
    }

    [Theory]
    [InlineData(NamingConvention.Auto, NamingConvention.Tosec)]
    [InlineData(NamingConvention.Tosec, NamingConvention.Tosec)]
    [InlineData(NamingConvention.NoIntro, NamingConvention.NoIntro)]
    [InlineData(NamingConvention.Redump, NamingConvention.Redump)]
    [InlineData(NamingConvention.Mame, NamingConvention.Mame)]
    public void AReleaseOfAnUnknownConvention_ShouldFollowTheOrderOfTosec(NamingConvention convention, NamingConvention expected)
    {
        TiebreakOrder.OrderConventionOf(convention).Should().Be(expected);
    }

    [Theory]
    [InlineData(NamingConvention.Tosec)]
    [InlineData(NamingConvention.NoIntro)]
    [InlineData(NamingConvention.Redump)]
    public void CountryFirstOrder_ShouldPutTheCountryAtTheTop(NamingConvention convention)
    {
        var languageFirst = TiebreakOrder.Default(convention, RegionPriorityMode.LanguageFirst);
        var countryFirst = TiebreakOrder.Default(convention, RegionPriorityMode.CountryFirst);

        countryFirst.Should().Equal([CountryRank, .. languageFirst.Where(c => c != CountryRank)]);
        TiebreakOrder.Normalize(null, convention, RegionPriorityMode.CountryFirst).Should().Equal(countryFirst);
    }

    [Fact]
    public void MameOrder_ShouldNotDependOnTheRegionMode()
    {
        TiebreakOrder.Default(NamingConvention.Mame, RegionPriorityMode.CountryFirst).Should().Equal(TiebreakOrder.Default(NamingConvention.Mame));
    }

    [Fact]
    public void Normalize_ShouldDropInvalidCriteriaAndPutTheMissingOnesBackAtTheirDefaultPlace()
    {
        var order = TiebreakOrder.Normalize([TiebreakCriterion.Version, None, LanguageRank, Name, TiebreakCriterion.Version], NamingConvention.NoIntro);

        // The revision and the parent follow the version as by default; the dump, the release... follow the language
        order.Should().Equal(
            TiebreakCriterion.Version, Revision, ParentClone,
            LanguageRank, GoodDump, OriginalRelease, CountryRank, VideoStandard, LanguageCount, RegionAffinity);
    }

    [Fact]
    public void Normalize_ShouldDropTheCriteriaOfOtherConventions()
    {
        var order = TiebreakOrder.Normalize([VideoStandard, DriverStatus, LanguageRank, PreferredMediaType], NamingConvention.Mame);

        order.Should().NotContain([VideoStandard, PreferredMediaType]);
        order.Should().Equal(TiebreakOrder.Normalize([DriverStatus, LanguageRank], NamingConvention.Mame));
    }

    [Fact]
    public void Normalize_OfAnEmptyOrder_ShouldGiveTheDefaultOrder()
    {
        TiebreakOrder.Normalize([], NamingConvention.Redump).Should().Equal(TiebreakOrder.Default(NamingConvention.Redump));
    }

    [Theory]
    [InlineData(NamingConvention.Tosec, RegionPriorityMode.LanguageFirst)]
    [InlineData(NamingConvention.Tosec, RegionPriorityMode.CountryFirst)]
    [InlineData(NamingConvention.Redump, RegionPriorityMode.CountryFirst)]
    public void ImpliedRegionMode_OfADefaultOrder_ShouldBeItsMode(NamingConvention convention, RegionPriorityMode mode)
    {
        TiebreakOrder.ImpliedRegionMode(TiebreakOrder.Default(convention, mode)).Should().Be(mode);
    }

    [Fact]
    public void ApplyRegionMode_OnADefaultOrder_ShouldGiveTheDefaultOrderOfTheNewMode()
    {
        TiebreakOrder.ApplyRegionMode(TiebreakOrder.Default(NamingConvention.NoIntro), NamingConvention.NoIntro, RegionPriorityMode.CountryFirst)
            .Should().Equal(TiebreakOrder.Default(NamingConvention.NoIntro, RegionPriorityMode.CountryFirst));
        TiebreakOrder.ApplyRegionMode(TiebreakOrder.Default(NamingConvention.NoIntro, RegionPriorityMode.CountryFirst), NamingConvention.NoIntro, RegionPriorityMode.LanguageFirst)
            .Should().Equal(TiebreakOrder.Default(NamingConvention.NoIntro));
    }

    [Fact]
    public void ApplyRegionMode_OnACustomOrder_ShouldOnlySwapTheLanguageAndTheCountry()
    {
        IReadOnlyList<TiebreakCriterion> custom =
            [TiebreakCriterion.Version, LanguageRank, OriginalRelease, GoodDump, CountryRank, VideoStandard, LanguageCount, RegionAffinity, Revision, ParentClone];

        var countryFirst = TiebreakOrder.ApplyRegionMode(custom, NamingConvention.NoIntro, RegionPriorityMode.CountryFirst);

        countryFirst.Should().Equal(
            TiebreakCriterion.Version, CountryRank, OriginalRelease, GoodDump, LanguageRank, VideoStandard, LanguageCount, RegionAffinity, Revision, ParentClone);
        TiebreakOrder.ApplyRegionMode(countryFirst, NamingConvention.NoIntro, RegionPriorityMode.CountryFirst).Should().Equal(countryFirst, "an order that follows the mode is kept");
        TiebreakOrder.ApplyRegionMode(countryFirst, NamingConvention.NoIntro, RegionPriorityMode.LanguageFirst).Should().Equal(custom);
    }

    [Fact]
    public void ConfigNames_ShouldBeStableAndReadBack()
    {
        foreach (var criterion in Enum.GetValues<TiebreakCriterion>().Where(c => c != None))
        {
            EnumExtensions.TryParseTiebreakCriterion(criterion.ToConfigString(), out var parsed).Should().BeTrue();
            parsed.Should().Be(criterion);
        }

        LanguageRank.ToConfigString().Should().Be("language");
        CountryRank.ToConfigString().Should().Be("country");
        CompleteMultidiskSet.ToConfigString().Should().Be("complete_multidisk_set");
        GoodDump.ToConfigString().Should().Be("good_dump");
        OriginalRelease.ToConfigString().Should().Be("original_release");
        RegionAffinity.ToConfigString().Should().Be("region_affinity");
        Completeness.ToConfigString().Should().Be("completeness");
        EnumExtensions.TryParseTiebreakCriterion("speed", out _).Should().BeFalse();
        EnumExtensions.TryParseTiebreakCriterion(" Version ", out var trimmed).Should().BeTrue();
        trimmed.Should().Be(TiebreakCriterion.Version);
    }

    [Fact]
    public void ParseTiebreakOrder_ShouldIgnoreUnknownNames()
    {
        EnumExtensions.ParseTiebreakOrder(["language", "cleanliness", " Country ", "speed", "completeness"])
            .Should().Equal(LanguageRank, CountryRank, Completeness);
    }
}
