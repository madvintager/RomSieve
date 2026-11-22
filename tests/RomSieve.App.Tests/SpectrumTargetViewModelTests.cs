using System.Text.Json;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// ZX Spectrum the collection is sorted for, a TOSEC option of the Filters page: the 128K for a new configuration, every
/// machine for a file of format 2, read from and saved to <c>config.json</c>, said in a sentence under its list, and
/// followed by the machine criterion of the 1G1R order, greyed out without a chosen model.
/// </summary>
public sealed class SpectrumTargetViewModelTests
{
    private static MainViewModel CreateViewModel(TempDirectoryFixture tmp, string json = "{}") =>
        TestMainViewModel.Create(new HeadlessDialogService(), tmp.WriteText("config.json", json));

    private static TiebreakCriterionItemViewModel Machine(MainViewModel vm) =>
        vm.Priorities.TosecCriteriaOrder.Items.Single(i => i.Criterion == TiebreakCriterion.SpectrumMachine);

    [Fact]
    public void NewConfiguration_ShouldBeThe128K()
    {
        using var tmp = new TempDirectoryFixture();
        var vm = CreateViewModel(tmp);

        vm.TosecFilters.SpectrumTarget.Should().Be(SpectrumTarget.Zx128);
        vm.TosecFilters.SpectrumTargetOptions.Select(o => o.Value)
            .Should().Equal(SpectrumTarget.Any, SpectrumTarget.Zx48, SpectrumTarget.Zx128, SpectrumTarget.ZxPlus3);
        vm.TosecFilters.SpectrumTargetHint.Should().StartWith("Les jeux +2A / +3 sont exclus.");
        Machine(vm).HasNoEffect.Should().BeFalse("the machine separates the versions of a game for a chosen model");
        vm.BuildConfigUnchecked().Filters.SpectrumTarget.Should().Be(SpectrumTarget.Zx128);
    }

    [Fact]
    public void ConfigurationOfFormat2_ShouldKeepEveryMachine()
    {
        // The machine versions of a game stayed apart: a configuration of an earlier version keeps its result
        using var tmp = new TempDirectoryFixture();
        var vm = CreateViewModel(tmp, """{ "config_version": 2 }""");

        vm.TosecFilters.SpectrumTarget.Should().Be(SpectrumTarget.Any);
        vm.TosecFilters.SpectrumTargetHint.Should().Be(
            "Rien n'est exclu ; chaque version machine d'un jeu (48K, 48K-128K, 128K…) reste un jeu à part.");
        Machine(vm).HasNoEffect.Should().BeTrue();
    }

    [Fact]
    public async Task Target_ShouldBeReadAndSaved()
    {
        using var tmp = new TempDirectoryFixture();
        var vm = CreateViewModel(tmp, """{ "spectrum_target": "48k" }""");

        vm.TosecFilters.SpectrumTarget.Should().Be(SpectrumTarget.Zx48);
        vm.BuildConfigUnchecked().Filters.SpectrumTarget.Should().Be(SpectrumTarget.Zx48);

        vm.TosecFilters.SpectrumTarget = SpectrumTarget.Any;
        await vm.SaveConfigurationAsync();

        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(tmp.Path, "config.json")));
        saved.RootElement.GetProperty("spectrum_target").GetString().Should().Be("any");
        Machine(vm).HasNoEffect.Should().BeTrue();
    }

    [Fact]
    public void ChangingTheTarget_ShouldChangeItsSentence()
    {
        using var tmp = new TempDirectoryFixture();
        var vm = CreateViewModel(tmp);
        var notified = new List<string?>();
        vm.TosecFilters.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        vm.TosecFilters.SpectrumTarget = SpectrumTarget.Zx48;

        notified.Should().Contain([nameof(TosecFiltersViewModel.SpectrumTarget), nameof(TosecFiltersViewModel.SpectrumTargetHint)]);
        vm.TosecFilters.SpectrumTargetHint.Should().StartWith("Les jeux 128K et +2A / +3 sont exclus.");
    }

    [Fact]
    public void LanguageChange_ShouldTranslateTheListAndItsSentence()
    {
        using var tmp = new TempDirectoryFixture();
        var vm = CreateViewModel(tmp);

        vm.UiLocale = "en";

        vm.TosecFilters.SpectrumTargetOptions[0].Label.Should().Be("Every machine");
        vm.TosecFilters.SpectrumTargetHint.Should().StartWith("The +2A / +3 games are excluded.");
        Machine(vm).Text.Should().EndWith("ZX Spectrum machine");
    }
}
