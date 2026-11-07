using System.Text.Json;
using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.Core.Tests;

/// <summary>
/// Reading and writing of the sort options in <c>config.json</c> (shared by the interface and the command line),
/// and the <see cref="SortConfig"/> built from them.
/// </summary>
public sealed class SortSettingsSerializerTests
{
    private static Dictionary<string, object?> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
    }

    [Fact]
    public void Clone_ShouldCopyEveryList()
    {
        // A list shared with the copy would change the original settings when the copy is edited
        var settings = new SortSettings { MameExcludedGenres = ["Casino"] };
        var copy = settings.Clone();

        var lists = typeof(SortSettings).GetProperties()
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(List<>))
            .ToList();
        lists.Should().NotBeEmpty();
        foreach (var list in lists)
        {
            list.GetValue(copy).Should().NotBeSameAs(list.GetValue(settings), list.Name);
            list.GetValue(copy).Should().BeEquivalentTo(list.GetValue(settings), list.Name);
        }

        copy.MameExcludedGenres.Add("Quiz");
        settings.MameExcludedGenres.Should().Equal("Casino");
    }

    [Fact]
    public void EmptyConfiguration_ShouldGiveTheDefaults()
    {
        var settings = SortSettingsSerializer.Read(new Dictionary<string, object?>());

        settings.Should().BeEquivalentTo(new SortSettings());
        settings.TosecTiebreakOrder.Should().Equal(TiebreakOrder.Default(NamingConvention.Tosec));
        settings.NoIntroTiebreakOrder.Should().Equal(TiebreakOrder.Default(NamingConvention.NoIntro));
        settings.RedumpTiebreakOrder.Should().Equal(TiebreakOrder.Default(NamingConvention.Redump));
        settings.MameTiebreakOrder.Should().Equal(TiebreakOrder.Default(NamingConvention.Mame));
        settings.HashingParallelism.Should().Be(1);
        settings.Pattern.Should().Be(SortSettings.DefaultPattern);
    }

    [Fact]
    public void WrittenSettings_ShouldBeReadBackIdentically()
    {
        var settings = new SortSettings
        {
            SourceDir = "src",
            DestinationDir = "dst",
            DatFile = "set.dat",
            NamingConvention = NamingConvention.Redump,
            TransferMode = TransferMode.Zip,
            CollisionMode = CollisionMode.Skip,
            HashingParallelism = SortConfig.AutoHashingParallelism,
            VerifySha1 = true,
            FlatDestination = true,
            M3uPlacement = M3uPlacementMode.Subfolder,
            ExcludeModifications = false,
            PreferredMediaType = MediaPreference.Tape,
            NoIntroExcludeHack = false,
            NoIntroExcludeRereleases = true,
            FilterLanguages = true,
            PriorityLanguages = ["fr", "en"],
            FilterCountries = true,
            PriorityCountries = ["FR", "EU"],
            RegionPriorityMode = RegionPriorityMode.CountryFirst,
            TosecTiebreakOrder = TiebreakOrder.Default(NamingConvention.Tosec, RegionPriorityMode.CountryFirst).ToList(),
            NoIntroTiebreakOrder = [.. TiebreakOrder.Default(NamingConvention.NoIntro, RegionPriorityMode.CountryFirst).Reverse()],
            RedumpTiebreakOrder = TiebreakOrder.Default(NamingConvention.Redump, RegionPriorityMode.CountryFirst).ToList(),
            OneGameOneRom = true,
            PreferredVideo = VideoPreference.Pal,
            PreferMonolingual = false
        };

        var data = new Dictionary<string, object?>(StringComparer.Ordinal);
        SortSettingsSerializer.Write(settings, data);
        var json = JsonSerializer.Serialize(data);

        SortSettingsSerializer.Read(Parse(json)).Should().BeEquivalentTo(settings, o => o.WithStrictOrdering());
        data["transfer_mode"].Should().Be("zip");
        data["region_priority_mode"].Should().Be("country_first");
        data.Should().NotContainKeys("language_priority", "tiebreak_order", "mame_minimum_driver_status");
    }

    [Fact]
    public void MissingKeys_ShouldKeepTheBaselineValues()
    {
        var baseline = new SortSettings { SourceDir = "kept", VerifyCopies = true, PriorityLanguages = ["de"] };

        var settings = SortSettingsSerializer.Read(Parse("""{ "destination_dir": "new" }"""), baseline);

        settings.SourceDir.Should().Be("kept");
        settings.DestinationDir.Should().Be("new");
        settings.VerifyCopies.Should().BeTrue();
        settings.PriorityLanguages.Should().Equal("de");
        baseline.DestinationDir.Should().BeEmpty("the baseline is not modified");
    }

    [Theory]
    [InlineData("""{ "hashing_parallelism": 4 }""", 4)]
    [InlineData("""{ "hashing_parallelism": 0 }""", 0)]
    [InlineData("""{ "hashing_parallelism": 3 }""", 1)]
    [InlineData("""{ "hashing_parallelism": "8" }""", 8)]
    [InlineData("""{ "hashing_parallelism": "fast" }""", 1)]
    public void HashingParallelism_ShouldOnlyAcceptTheOfferedValues(string json, int expected) =>
        SortSettingsSerializer.Read(Parse(json)).HashingParallelism.Should().Be(expected);

    [Fact]
    public void PriorityCodes_ShouldBeNormalized()
    {
        var settings = SortSettingsSerializer.Read(Parse("""
            { "priority_languages": ["FR", " en ", "fr", ""], "priority_countries": ["uk", "Asia", "us", "US"] }
            """));

        settings.PriorityLanguages.Should().Equal("fr", "en");
        settings.PriorityCountries.Should().Equal("GB", "AS", "US");
    }

    [Fact]
    public void SavedOrder_ShouldBeRepairedAndSetTheRegionMode()
    {
        var settings = SortSettingsSerializer.Read(Parse("""
            { "config_version": 1, "region_priority_mode": "lang_first", "tiebreak_order_tosec": ["country", "bogus", "driver_status", "language"] }
            """));

        settings.TosecTiebreakOrder.Should().BeEquivalentTo(TiebreakOrder.Default(NamingConvention.Tosec), "the criteria of other conventions are left out");
        settings.TosecTiebreakOrder[0].Should().Be(TiebreakCriterion.CountryRank);
        settings.TosecTiebreakOrder.IndexOf(TiebreakCriterion.LanguageRank).Should().BeGreaterThan(0);
        settings.RegionPriorityMode.Should().Be(RegionPriorityMode.CountryFirst);
        settings.NoIntroTiebreakOrder.Should().Equal(TiebreakOrder.Default(NamingConvention.NoIntro, RegionPriorityMode.CountryFirst),
            "an order not saved follows the region mode");
    }

    [Fact]
    public void ToSortConfig_ShouldResolveEmptyValuesAndTheFlatDestination()
    {
        var config = SortSettingsSerializer.ToSortConfig(
            new SortSettings { SourceDir = " ", DestinationDir = " out ", DatFile = " ", FlatDestination = true, OneGameOneRom = true },
            new Dictionary<string, string> { ["game"] = "winner" });

        config.SourceDir.Should().Be(".");
        config.DestinationDir.Should().Be("out");
        config.DatFile.Should().BeNull();
        config.Pattern.Should().BeEmpty();
        config.Filters.OneGameOneRom.Should().BeTrue();
        config.Filters.FilterLanguages.Should().BeTrue();
        config.Filters.Manual1G1RWinners.Should().ContainKey("game");
        config.Filters.NoIntroExcludeBadDumps.Should().BeTrue();
    }

    [Fact]
    public void MameOrder_ShouldDefaultToItsOwnOrderAndNeverSetTheRegionMode()
    {
        SortSettingsSerializer.Read(Parse("{}")).MameTiebreakOrder.Should().Equal(TiebreakOrder.Default(NamingConvention.Mame));

        var countryFirst = TiebreakOrder.Default(NamingConvention.Mame).Where(c => c != TiebreakCriterion.CountryRank).ToList();
        countryFirst.Insert(0, TiebreakCriterion.CountryRank);
        var names = string.Join(", ", countryFirst.Select(c => $"\"{c.ToConfigString()}\"").Prepend("\"bogus\""));
        var settings = SortSettingsSerializer.Read(Parse($$"""
            { "region_priority_mode": "lang_first", "mame_tiebreak_order": [{{names}}] }
            """));

        settings.MameTiebreakOrder.Should().Equal(countryFirst);
        settings.RegionPriorityMode.Should().Be(RegionPriorityMode.LanguageFirst, "only the order of TOSEC sets the region mode");
        settings.TosecTiebreakOrder.Should().Equal(TiebreakOrder.Default(NamingConvention.Tosec));

        var data = new Dictionary<string, object?>();
        SortSettingsSerializer.Write(settings, data);
        ((IEnumerable<string>)data["mame_tiebreak_order"]!).Take(2).Should().Equal("country", "complete_multidisk_set");
        SortSettingsSerializer.ToSortConfig(settings).Filters.MameTiebreakOrder.Should().Equal(settings.MameTiebreakOrder);
    }
}
