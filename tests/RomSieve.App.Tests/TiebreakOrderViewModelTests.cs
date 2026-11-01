using System.Text.Json;
using RomSieve.App;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// Orders of the 1G1R criteria in the Languages, regions and 1G1R tab, one per convention: the order shown, reordering,
/// consistency with the arbitration order (language / country first) in both directions and between the orders,
/// the preferences set on the line of their criterion, persistence in <c>config.json</c> and older configurations.
/// </summary>
public sealed class TiebreakOrderViewModelTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();

    public void Dispose() => _temp.Dispose();

    private MainViewModel CreateViewModel(string json = "{}") =>
        TestMainViewModel.Create(new HeadlessDialogService(), _temp.WriteText("config.json", json));

    private static IReadOnlyList<TiebreakCriterion> LanguageFirst(NamingConvention convention = NamingConvention.Tosec) =>
        TiebreakOrder.Default(convention, RegionPriorityMode.LanguageFirst);

    private static IReadOnlyList<TiebreakCriterion> CountryFirst(NamingConvention convention = NamingConvention.Tosec) =>
        TiebreakOrder.Default(convention, RegionPriorityMode.CountryFirst);

    private static void Select(TiebreakOrderViewModel order, TiebreakCriterion criterion) =>
        order.SelectedItem = order.Items.Single(i => i.Criterion == criterion);

    private static TiebreakCriterionItemViewModel Item(TiebreakOrderViewModel order, TiebreakCriterion criterion) =>
        order.Items.Single(i => i.Criterion == criterion);

    [Fact]
    public void DefaultOrders_ShouldListTheCriteriaOfTheirConventionAndGoToTheSortConfig()
    {
        var vm = CreateViewModel();
        var filters = vm.BuildConfigUnchecked().Filters;

        foreach (var convention in TiebreakOrder.Conventions)
        {
            var order = vm.Priorities.CriteriaOrderOf(convention);
            order.Convention.Should().Be(convention);
            order.Order.Should().Equal(LanguageFirst(convention));
            order.IsDefaultOrder.Should().BeTrue();
            filters.GetTiebreakOrder(convention).Should().Equal(order.Order);
        }

        vm.Priorities.CriteriaOrderOf(NamingConvention.Auto).Should().BeSameAs(vm.Priorities.TosecCriteriaOrder);
    }

    [Theory]
    [InlineData(NamingConvention.NoIntro)]
    [InlineData(NamingConvention.Redump)]
    [InlineData(NamingConvention.Mame)]
    [InlineData(NamingConvention.Tosec)]
    public void EffectiveConvention_ShouldShowItsOrder(NamingConvention convention)
    {
        var vm = CreateViewModel();
        var priorities = vm.Priorities;
        priorities.ShownCriteriaOrder.Should().BeSameAs(priorities.TosecCriteriaOrder);

        priorities.ShowConvention(convention);

        priorities.ShownCriteriaOrder.Should().BeSameAs(priorities.CriteriaOrderOf(convention));
        new[] { priorities.IsTosecOrderShown, priorities.IsNoIntroOrderShown, priorities.IsRedumpOrderShown, priorities.IsMameOrderShown }
            .Count(shown => shown).Should().Be(1);
    }

    [Fact]
    public void UnknownConvention_ShouldKeepTheOrderShown_AndAnyOrderCanBeShownByHand()
    {
        var vm = CreateViewModel();
        var priorities = vm.Priorities;
        priorities.ShowConvention(NamingConvention.Redump);

        priorities.ShowConvention(NamingConvention.Auto);
        priorities.ShownCriteriaOrder.Should().BeSameAs(priorities.RedumpCriteriaOrder);

        priorities.IsNoIntroOrderShown = true;
        priorities.ShownCriteriaOrder.Should().BeSameAs(priorities.NoIntroCriteriaOrder);
        priorities.IsRedumpOrderShown.Should().BeFalse();
    }

    [Fact]
    public void ArbitrationOrder_OnTheDefaultOrders_ShouldSwitchThemToTheOtherDefaultOrder()
    {
        var vm = CreateViewModel();

        vm.Priorities.RegionPriorityMode = RegionPriorityMode.CountryFirst;
        foreach (var convention in new[] { NamingConvention.Tosec, NamingConvention.NoIntro, NamingConvention.Redump })
        {
            vm.Priorities.CriteriaOrderOf(convention).Order.Should().Equal(CountryFirst(convention));
        }

        vm.Priorities.MameCriteriaOrder.Order.Should().Equal(LanguageFirst(NamingConvention.Mame), "the MAME order never follows the region mode");

        vm.Priorities.RegionPriorityMode = RegionPriorityMode.LanguageFirst;
        vm.Priorities.NoIntroCriteriaOrder.Order.Should().Equal(LanguageFirst(NamingConvention.NoIntro));
    }

    [Fact]
    public void MovingTheCountryAboveTheLanguage_ShouldSelectCountryFirstForEveryOrderButMame_AndConversely()
    {
        var vm = CreateViewModel();
        var order = vm.Priorities.NoIntroCriteriaOrder;

        Select(order, TiebreakCriterion.CountryRank);
        for (var i = 0; i < 3; i++)
        {
            order.MoveUpCommand.Execute(null);
        }

        order.Order.Take(2).Should().Equal(TiebreakCriterion.CountryRank, TiebreakCriterion.LanguageRank);
        order.SelectedItem!.Criterion.Should().Be(TiebreakCriterion.CountryRank, "the moved criterion stays selected");
        vm.Priorities.RegionPriorityMode.Should().Be(RegionPriorityMode.CountryFirst);
        vm.Priorities.TosecCriteriaOrder.Order.Should().Equal(CountryFirst(NamingConvention.Tosec), "the other orders follow the mode");
        vm.Priorities.RedumpCriteriaOrder.Order.Should().Equal(CountryFirst(NamingConvention.Redump));
        vm.Priorities.MameCriteriaOrder.Order.Should().Equal(LanguageFirst(NamingConvention.Mame));
        vm.BuildConfigUnchecked().Filters.NoIntroTiebreakOrder.Should().Equal(order.Order);

        order.MoveDownCommand.Execute(null);
        vm.Priorities.RegionPriorityMode.Should().Be(RegionPriorityMode.LanguageFirst);
        vm.Priorities.TosecCriteriaOrder.Order.Should().Equal(LanguageFirst(NamingConvention.Tosec));
    }

    [Fact]
    public void ArbitrationOrder_OnACustomOrder_ShouldOnlySwapTheLanguageAndTheCountry()
    {
        var vm = CreateViewModel();
        var order = vm.Priorities.TosecCriteriaOrder;
        Select(order, TiebreakCriterion.Version);
        for (var i = 0; i < 20; i++)
        {
            order.MoveUpCommand.Execute(null);
        }
        var custom = order.Order.ToList();
        custom[0].Should().Be(TiebreakCriterion.Version);
        order.MoveUpCommand.CanExecute(null).Should().BeFalse("the first criterion cannot move up");

        vm.Priorities.RegionPriorityMode = RegionPriorityMode.CountryFirst;

        var expected = custom.ToList();
        var language = expected.IndexOf(TiebreakCriterion.LanguageRank);
        var country = expected.IndexOf(TiebreakCriterion.CountryRank);
        (expected[language], expected[country]) = (expected[country], expected[language]);
        order.Order.Should().Equal(expected);
    }

    [Fact]
    public void ResetDefaults_ShouldRestoreTheDefaultOrderOfTheArbitrationOrder()
    {
        var vm = CreateViewModel();
        var order = vm.Priorities.RedumpCriteriaOrder;
        Select(order, TiebreakCriterion.ParentClone);
        order.MoveUpCommand.Execute(null);
        vm.Priorities.RegionPriorityMode = RegionPriorityMode.CountryFirst;

        order.ResetDefaultsCommand.Execute(null);

        order.Order.Should().Equal(CountryFirst(NamingConvention.Redump));
        vm.Priorities.RegionPriorityMode.Should().Be(RegionPriorityMode.CountryFirst);
    }

    [Fact]
    public async Task CustomOrders_ShouldBeSavedUnderStableNamesAndReloaded()
    {
        var vm = CreateViewModel();
        var order = vm.Priorities.TosecCriteriaOrder;
        Select(order, TiebreakCriterion.Date);
        order.MoveUpCommand.Execute(null);
        var custom = order.Order.ToList();
        var redump = vm.Priorities.RedumpCriteriaOrder;
        Select(redump, TiebreakCriterion.MediaFidelity);
        redump.MoveUpCommand.Execute(null);
        var customRedump = redump.Order.ToList();

        await vm.SaveConfigurationAsync();

        using (var saved = JsonDocument.Parse(await File.ReadAllTextAsync(_temp.GetFilePath("config.json"))))
        {
            var names = saved.RootElement.GetProperty("tiebreak_order_tosec").EnumerateArray().Select(e => e.GetString()).ToList();
            names.Should().HaveCount(TiebreakOrder.CriteriaOf(NamingConvention.Tosec).Count).And.StartWith("language").And.NotContain("name");
            names.IndexOf("date").Should().BeLessThan(names.IndexOf("revision"));
            saved.RootElement.GetProperty("region_priority_mode").GetString().Should().Be("lang_first");
            saved.RootElement.TryGetProperty("tiebreak_order", out _).Should().BeFalse("each convention has its own order");
        }

        var reloaded = TestMainViewModel.Create(new HeadlessDialogService(), _temp.GetFilePath("config.json"));
        reloaded.Priorities.TosecCriteriaOrder.Order.Should().Equal(custom);
        reloaded.Priorities.RedumpCriteriaOrder.Order.Should().Equal(customRedump);
    }

    [Fact]
    public void Configuration_WithoutOrder_ShouldUseTheDefaultOrdersOfItsArbitrationOrder()
    {
        var vm = CreateViewModel("""{ "region_priority_mode": "country_first" }""");

        vm.Priorities.TosecCriteriaOrder.Order.Should().Equal(CountryFirst(NamingConvention.Tosec));
        vm.Priorities.NoIntroCriteriaOrder.Order.Should().Equal(CountryFirst(NamingConvention.NoIntro));
        vm.Priorities.RegionPriorityMode.Should().Be(RegionPriorityMode.CountryFirst);
    }

    [Fact]
    public void SavedOrders_ShouldBeRepairedForEachConvention()
    {
        // Unknown names are ignored, missing criteria put back, and a country above the language selects country first
        const string savedOrder = """["country", "speed", "language", "version", "language", "cleanliness"]""";
        var vm = CreateViewModel($$"""
            {
              "region_priority_mode": "lang_first",
              "tiebreak_order_tosec": {{savedOrder}}, "tiebreak_order_nointro": {{savedOrder}}, "tiebreak_order_redump": {{savedOrder}}
            }
            """);

        foreach (var convention in new[] { NamingConvention.Tosec, NamingConvention.NoIntro, NamingConvention.Redump })
        {
            var order = vm.Priorities.CriteriaOrderOf(convention).Order;
            order.Should().BeEquivalentTo(TiebreakOrder.CriteriaOf(convention)).And.OnlyHaveUniqueItems();
            order[0].Should().Be(TiebreakCriterion.CountryRank);
            order.ToList().IndexOf(TiebreakCriterion.LanguageRank).Should().BeLessThan(order.ToList().IndexOf(TiebreakCriterion.Version));
        }

        vm.Priorities.RegionPriorityMode.Should().Be(RegionPriorityMode.CountryFirst);
    }

    [Fact]
    public void CriteriaLabelsAndDescriptions_ShouldBeTranslatedAndNumbered()
    {
        var vm = CreateViewModel();

        foreach (var order in vm.Priorities.CriteriaOrders)
        {
            foreach (var item in order.Items)
            {
                vm.I18n[item.LabelKey].Should().NotBe(item.LabelKey, $"{item.LabelKey} is translated");
                vm.I18n[item.DescriptionKey].Should().NotBe(item.DescriptionKey, $"{item.DescriptionKey} is translated");
                item.ToolTipText.Should().StartWith(item.Description);
            }
        }

        var tosec = vm.Priorities.TosecCriteriaOrder;
        tosec.Items[0].Text.Should().Be("1. Langue (rang de priorité)");
        Item(tosec, TiebreakCriterion.OriginalRelease).Description.Should().Contain("[cr]", "the description quotes the marks of the convention");
        Item(vm.Priorities.MameCriteriaOrder, TiebreakCriterion.OriginalRelease).Description.Should().Contain("bootleg");

        vm.UiLocale = "en";
        tosec.Items[0].Text.Should().Be("1. Language (priority rank)");
        tosec.Items[^1].Text.Should().Be($"{tosec.Items.Count}. Most disks (incomplete set)");
    }

    [Fact]
    public void CriteriaOfOtherConventions_ShouldBeNamedUnderTheList()
    {
        var vm = CreateViewModel();
        var mame = vm.Priorities.MameCriteriaOrder;

        mame.LeftOutCriteria.Should().BeEquivalentTo(Enum.GetValues<TiebreakCriterion>()
            .Where(c => c is not TiebreakCriterion.None and not TiebreakCriterion.Name)
            .Except(TiebreakOrder.CriteriaOf(NamingConvention.Mame)));
        mame.LeftOutText.Should().StartWith("Sans objet pour MAME, donc absents de la liste : ")
            .And.Contain(vm.I18n["tiebreak.video"])
            .And.Contain(vm.I18n["tiebreak.good_dump"]);

        vm.UiLocale = "en";
        mame.LeftOutText.Should().StartWith("Not applicable to MAME, hence not listed: ").And.Contain("Preferred video standard");
    }

    [Fact]
    public void Preferences_ShouldBeSetOnTheLineOfTheirCriterion()
    {
        var vm = CreateViewModel();
        var tosec = vm.Priorities.TosecCriteriaOrder;
        var video = Item(tosec, TiebreakCriterion.VideoStandard);
        var media = Item(tosec, TiebreakCriterion.PreferredMediaType);
        var languages = Item(tosec, TiebreakCriterion.LanguageCount);

        tosec.Items.Where(i => i.HasPreference).Select(i => i.Criterion)
            .Should().BeEquivalentTo([TiebreakCriterion.VideoStandard, TiebreakCriterion.PreferredMediaType, TiebreakCriterion.LanguageCount]);
        Item(vm.Priorities.NoIntroCriteriaOrder, TiebreakCriterion.VideoStandard).Preference.Should().BeSameAs(video.Preference);

        // A choice on the line writes the option of its section, which the configuration saves
        video.Preference!.SelectedOption = video.Preference.Options.Single(o => Equals(o.Value, VideoPreference.Ntsc));
        media.Preference!.SelectedOption = media.Preference.Options.Single(o => Equals(o.Value, MediaPreference.Tape));
        languages.Preference!.SelectedOption = languages.Preference.Options.Single(o => Equals(o.Value, false));

        vm.Priorities.PreferredVideo.Should().Be(VideoPreference.Ntsc);
        vm.TosecFilters.PreferredMediaType.Should().Be(MediaPreference.Tape);
        vm.Priorities.PreferMonolingual.Should().BeFalse();

        // A change made elsewhere (quick preset, configuration) is shown on the line
        vm.Priorities.Apply1G1RPresetCommand.Execute("france");
        video.Preference.SelectedOption!.Value.Should().Be(VideoPreference.Pal);
        languages.Preference.SelectedOption!.Value.Should().Be(true);

        // A drop-down list that goes away writes nothing
        video.Preference.SelectedOption = null;
        vm.Priorities.PreferredVideo.Should().Be(VideoPreference.Pal);
    }

    [Fact]
    public void ACriterionWithoutPreference_ShouldBeGreyedOutWithItsReason()
    {
        var vm = CreateViewModel();
        var video = Item(vm.Priorities.TosecCriteriaOrder, TiebreakCriterion.VideoStandard);
        var media = Item(vm.Priorities.TosecCriteriaOrder, TiebreakCriterion.PreferredMediaType);

        video.HasNoEffect.Should().BeTrue();
        video.NoEffectText.Should().Be(vm.I18n["tiebreak.no_effect_no_preference"]);
        media.HasNoEffect.Should().BeTrue();

        vm.Priorities.PreferredVideo = VideoPreference.Pal;
        vm.TosecFilters.PreferredMediaType = MediaPreference.Floppy;

        video.HasNoEffect.Should().BeFalse();
        media.HasNoEffect.Should().BeFalse();
        video.Preference!.SelectedOption!.Value.Should().Be(VideoPreference.Pal);
        media.Preference!.SelectedOption!.Value.Should().Be(MediaPreference.Floppy);
    }

    [Fact]
    public void RunningOperation_ShouldDisableTheReorderingAndThePreferences()
    {
        var vm = CreateViewModel();
        var order = vm.Priorities.TosecCriteriaOrder;
        Select(order, TiebreakCriterion.Date);

        vm.Priorities.IsRunning = true;

        order.MoveUpCommand.CanExecute(null).Should().BeFalse();
        order.ResetDefaultsCommand.CanExecute(null).Should().BeFalse();
        Item(order, TiebreakCriterion.VideoStandard).Preference!.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task MameOrder_ShouldNeverChangeTheRegionMode()
    {
        var vm = CreateViewModel();
        var priorities = vm.Priorities;
        priorities.ShowConvention(NamingConvention.Mame);
        priorities.RegionModeSummary.Should().Be("La langue l'emporte sur le pays.");

        // The country above the language in the MAME order: its summary says so, the other orders and the mode stay
        var order = priorities.MameCriteriaOrder;
        Select(order, TiebreakCriterion.CountryRank);
        order.MoveUpCommand.Execute(null);
        priorities.RegionModeSummary.Should().Be("Le pays l'emporte sur la langue.");
        priorities.RegionPriorityMode.Should().Be(RegionPriorityMode.LanguageFirst);
        priorities.TosecCriteriaOrder.Order.Should().Equal(LanguageFirst());
        vm.BuildConfigUnchecked().Filters.MameTiebreakOrder.Should().Equal(order.Order);

        order.ResetDefaultsCommand.Execute(null);
        order.Order.Should().Equal(LanguageFirst(NamingConvention.Mame));
        order.MoveUpCommand.Execute(null);
        var custom = order.Order.ToList();

        await vm.SaveConfigurationAsync();

        var reloaded = TestMainViewModel.Create(new HeadlessDialogService(), _temp.GetFilePath("config.json"));
        reloaded.Priorities.MameCriteriaOrder.Order.Should().Equal(custom);
        reloaded.Priorities.TosecCriteriaOrder.Order.Should().Equal(LanguageFirst());
    }
}
