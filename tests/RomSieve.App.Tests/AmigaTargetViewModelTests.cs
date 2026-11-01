using System.Text.Json;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// Amiga the collection is sorted for, a TOSEC option of the Filters page: read from and saved to <c>config.json</c>,
/// said in a sentence under its list, and followed by the chipset criterion of the 1G1R order, greyed out unless the
/// machine is an AGA Amiga.
/// </summary>
public sealed class AmigaTargetViewModelTests
{
    private static MainViewModel CreateViewModel(TempDirectoryFixture tmp, string json = "{}") =>
        TestMainViewModel.Create(new HeadlessDialogService(), tmp.WriteText("config.json", json));

    private static TiebreakCriterionItemViewModel Chipset(MainViewModel vm) =>
        vm.Priorities.TosecCriteriaOrder.Items.Single(i => i.Criterion == TiebreakCriterion.AmigaChipset);

    [Fact]
    public void Default_ShouldBeEveryMachine()
    {
        using var tmp = new TempDirectoryFixture();
        var vm = CreateViewModel(tmp);

        vm.TosecFilters.AmigaTarget.Should().Be(AmigaTarget.Any);
        vm.TosecFilters.AmigaTargetOptions.Select(o => o.Value)
            .Should().Equal(AmigaTarget.Any, AmigaTarget.Ocs, AmigaTarget.Ecs, AmigaTarget.Aga);
        vm.TosecFilters.AmigaTargetHint.Should().Be("Rien n'est exclu ; les versions AGA et OCS d'un jeu restent deux jeux.");
        Chipset(vm).HasNoEffect.Should().BeTrue();
        vm.BuildConfigUnchecked().Filters.AmigaTarget.Should().Be(AmigaTarget.Any);
    }

    [Fact]
    public async Task Target_ShouldBeReadAndSaved()
    {
        using var tmp = new TempDirectoryFixture();
        var vm = CreateViewModel(tmp, """{ "amiga_target": "aga" }""");

        vm.TosecFilters.AmigaTarget.Should().Be(AmigaTarget.Aga);
        Chipset(vm).HasNoEffect.Should().BeFalse("the chipset separates the versions of a game for an AGA Amiga");
        vm.BuildConfigUnchecked().Filters.AmigaTarget.Should().Be(AmigaTarget.Aga);

        vm.TosecFilters.AmigaTarget = AmigaTarget.Ocs;
        await vm.SaveConfigurationAsync();

        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(tmp.Path, "config.json")));
        saved.RootElement.GetProperty("amiga_target").GetString().Should().Be("ocs");
        Chipset(vm).HasNoEffect.Should().BeTrue();
    }

    [Fact]
    public void ChangingTheTarget_ShouldChangeItsSentence()
    {
        using var tmp = new TempDirectoryFixture();
        var vm = CreateViewModel(tmp);
        var notified = new List<string?>();
        vm.TosecFilters.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        vm.TosecFilters.AmigaTarget = AmigaTarget.Ecs;

        notified.Should().Contain([nameof(TosecFiltersViewModel.AmigaTarget), nameof(TosecFiltersViewModel.AmigaTargetHint)]);
        vm.TosecFilters.AmigaTargetHint.Should().Be("Les jeux AGA sont exclus.");
    }

    [Fact]
    public void LanguageChange_ShouldTranslateTheListAndItsSentence()
    {
        using var tmp = new TempDirectoryFixture();
        var vm = CreateViewModel(tmp);
        vm.TosecFilters.AmigaTarget = AmigaTarget.Aga;

        vm.UiLocale = "en";

        vm.TosecFilters.AmigaTargetOptions[0].Label.Should().Be("Every machine");
        vm.TosecFilters.AmigaTargetHint.Should().Be(
            "Nothing is excluded. With 1G1R, the AGA and OCS versions of a game are one game, the AGA version first.");
        Chipset(vm).Text.Should().EndWith("Amiga chipset (AGA machine)");
    }
}
