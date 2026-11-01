using System.Text.Json;
using RomSieve.App;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// Languages and countries as ordered chips: adding through the search, commands of a chip (remove, to the top, up,
/// down), ranks, presets, and the language / country mode that follows the order of the criteria.
/// </summary>
public sealed class PriorityChipsViewModelTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();

    public void Dispose() => _temp.Dispose();

    private MainViewModel CreateViewModel(string json = "{}") =>
        TestMainViewModel.Create(new HeadlessDialogService(), _temp.WriteText("config.json", json));

    private static List<string> Codes(PriorityListViewModel list) => list.PriorityItems.Select(i => i.Code).ToList();

    [Fact]
    public void Search_ShouldAddTheFirstMatchAtTheEndAndClearTheSearch()
    {
        var vm = CreateViewModel();
        var languages = vm.Priorities.LanguagePriority;
        languages.SetPriorityCodes(["fr", "en"]);
        var changes = new List<IReadOnlyList<string>>();
        languages.PriorityChanged += (_, codes) => changes.Add(codes);

        languages.HasPriorityItems.Should().BeTrue();

        // Enter without a search adds nothing
        languages.AddFirstMatchCommand.Execute(null);
        Codes(languages).Should().Equal("fr", "en");

        languages.SearchFilterText = "Italien";
        languages.FilteredAvailableItems.Should().ContainSingle().Which.Code.Should().Be("it");
        languages.AddFirstMatchCommand.Execute(null);

        Codes(languages).Should().Equal("fr", "en", "it");
        languages.PriorityItems[2].RankIndex.Should().Be(3);
        languages.SearchFilterText.Should().BeEmpty();
        languages.AvailableItems.Should().NotContain(i => i.Code == "it");
        changes.Should().ContainSingle().Which.Should().Equal("fr", "en", "it");
        vm.Priorities.LanguagesText.Should().Be("fr, en, it");

        // A click on an item of the search list adds it as well
        languages.AvailableItems.Single(i => i.Code == "de").AddCommand.Execute(null);
        Codes(languages).Should().Equal("fr", "en", "it", "de");
    }

    [Fact]
    public void ChipCommands_ShouldReorderAndRemoveWithRenumberedRanks()
    {
        var vm = CreateViewModel();
        var countries = vm.Priorities.CountryPriority;
        countries.SetPriorityCodes(["FR", "EU", "US", "JP"]);
        PriorityItemViewModel Chip(string code) => countries.PriorityItems.Single(i => i.Code == code);

        Chip("FR").CanMoveUp.Should().BeFalse();
        Chip("FR").CanMoveDown.Should().BeTrue();
        Chip("JP").CanMoveDown.Should().BeFalse();

        Chip("US").MoveUpCommand.Execute(null);
        Codes(countries).Should().Equal("FR", "US", "EU", "JP");

        Chip("FR").MoveDownCommand.Execute(null);
        Codes(countries).Should().Equal("US", "FR", "EU", "JP");

        Chip("JP").MoveToTopCommand.Execute(null);
        Codes(countries).Should().Equal("JP", "US", "FR", "EU");
        countries.PriorityItems.Select(i => i.RankIndex).Should().Equal(1, 2, 3, 4);

        // The last chip removed: the one before it becomes the last, without a change of its rank
        var eu = Chip("EU");
        var fr = Chip("FR");
        var notified = new List<string?>();
        fr.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        eu.RemoveCommand.Execute(null);
        Codes(countries).Should().Equal("JP", "US", "FR");
        fr.CanMoveDown.Should().BeFalse();
        notified.Should().Contain(nameof(PriorityItemViewModel.CanMoveDown));
        countries.AvailableItems.Should().Contain(i => i.Code == "EU");
        vm.Priorities.CountriesText.Should().Be("JP, US, FR");

        foreach (var code in Codes(countries))
        {
            Chip(code).RemoveCommand.Execute(null);
        }

        countries.HasPriorityItems.Should().BeFalse();
    }

    [Fact]
    public void ChipCommands_ShouldDoNothingWhileAnOperationRuns()
    {
        var vm = CreateViewModel();
        vm.Priorities.Apply1G1RPreset("usa");
        vm.Priorities.IsRunning = true;
        var languages = vm.Priorities.LanguagePriority;

        languages.PriorityItems[1].MoveToTopCommand.Execute(null);
        languages.PriorityItems[0].RemoveCommand.Execute(null);

        Codes(languages).Should().Equal("en", "m");
    }

    [Fact]
    public void Presets_ShouldFillTheChipsInOrder()
    {
        var vm = CreateViewModel();

        vm.Priorities.Apply1G1RPreset("japan");

        Codes(vm.Priorities.LanguagePriority).Should().Equal("ja", "en");
        Codes(vm.Priorities.CountryPriority).Should().Equal("JP", "AS", "WO", "US", "EU");
        vm.Priorities.CountryPriority.PriorityItems.Select(i => i.RankIndex).Should().Equal(1, 2, 3, 4, 5);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RegionMode_ShouldFollowTheOrderOfTheCriteriaAndBeSummedUp(bool oneGameOneRom)
    {
        var vm = CreateViewModel();
        var priorities = vm.Priorities;
        priorities.OneGameOneRom = oneGameOneRom;
        var notified = new List<string?>();
        priorities.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        priorities.RegionModeSummary.Should().Be("La langue l'emporte sur le pays.");

        var order = priorities.TosecCriteriaOrder;
        order.SelectedItem = order.Items.Single(i => i.Criterion == TiebreakCriterion.CountryRank);
        while (order.Order.ToList().IndexOf(TiebreakCriterion.CountryRank) > order.Order.ToList().IndexOf(TiebreakCriterion.LanguageRank))
        {
            order.MoveUp();
        }

        priorities.RegionPriorityMode.Should().Be(RegionPriorityMode.CountryFirst);
        priorities.RegionModeSummary.Should().Be("Le pays l'emporte sur la langue.");
        notified.Should().Contain(nameof(PrioritiesViewModel.RegionModeSummary));

        order.ResetDefaults();
        priorities.RegionPriorityMode.Should().Be(RegionPriorityMode.CountryFirst, "the reset keeps the country first");

        order.SelectedItem = order.Items.Single(i => i.Criterion == TiebreakCriterion.LanguageRank);
        order.MoveUp();
        priorities.RegionPriorityMode.Should().Be(RegionPriorityMode.LanguageFirst);

        vm.UiLocale = "en";
        priorities.RegionModeSummary.Should().Be("Language comes before country.");
    }

    [Theory]
    [InlineData("""{ "region_priority_mode": "country_first" }""", "country_first")]
    [InlineData("""{ "region_priority_mode": "lang_first" }""", "lang_first")]
    public async Task Configuration_WithTheModeOnly_ShouldBeSavedWithTheSameMode(string json, string expected)
    {
        var vm = CreateViewModel(json);

        await vm.SaveConfigurationAsync();

        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_temp.Path, "config.json")));
        saved.RootElement.GetProperty("region_priority_mode").GetString().Should().Be(expected);
        saved.RootElement.GetProperty("tiebreak_order_tosec").EnumerateArray().Select(e => e.GetString())
            .Should().Equal(TiebreakOrder.Default(NamingConvention.Tosec, EnumExtensions.ParseRegionPriorityMode(expected)).Select(c => c.ToConfigString()));
    }

    [Fact]
    public async Task SavedOrderAndMode_ShouldBeSavedAgainUnchanged()
    {
        var order = TiebreakOrder.Default(NamingConvention.Tosec, RegionPriorityMode.CountryFirst).Select(c => c.ToConfigString()).ToList();
        order.Reverse(order.IndexOf("version"), 2);
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["config_version"] = RomSieve.Core.Services.ConfigSchema.CurrentVersion,
            ["region_priority_mode"] = "country_first",
            ["tiebreak_order_tosec"] = order
        });
        var vm = CreateViewModel(json);

        await vm.SaveConfigurationAsync();

        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_temp.Path, "config.json")));
        saved.RootElement.GetProperty("region_priority_mode").GetString().Should().Be("country_first");
        saved.RootElement.GetProperty("tiebreak_order_tosec").EnumerateArray().Select(e => e.GetString()).Should().Equal(order);
    }
}
