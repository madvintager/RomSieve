using System.Text.Json;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// Categories of the No-Intro and Redump DATs in the Filters page (issue #7): the add-ons excluded for a new configuration
/// only, the categories of the DAT read with their number of games, otherwise those the DATs use, the BIOS box, and the
/// options read from and saved to <c>config.json</c>.
/// </summary>
public sealed class DatCategoryFiltersViewModelTests : IDisposable
{
    private const string SnesDat = """
        <?xml version="1.0"?>
        <datafile>
          <header><name>Nintendo - Super Nintendo Entertainment System</name><homepage>No-Intro</homepage></header>
          <game name="CX4 (World) (Enhancement Chip)"><category>Add-Ons</category><rom name="CX4 (World) (Enhancement Chip).bin" size="4" crc="00000001"/></game>
          <game name="Super Mario World (USA)"><category>Games</category><rom name="Super Mario World (USA).sfc" size="4" crc="00000002"/></game>
          <game name="Aladdin (USA) (Beta)"><category>Games</category><category>Preproduction</category><rom name="Aladdin (USA) (Beta).sfc" size="4" crc="00000003"/></game>
          <game name="Mario Paint (Japan, USA) (En)"><category>Applications</category><rom name="Mario Paint (Japan, USA) (En).sfc" size="4" crc="00000004"/></game>
        </datafile>
        """;

    private readonly TempDirectoryFixture _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private MainViewModel CreateViewModel(string json = "{}") =>
        TestMainViewModel.Create(new HeadlessDialogService(), _tmp.WriteText("config.json", json));

    private static IEnumerable<(string Label, bool IsKept)> Shown(DatCategoryFiltersViewModel categories) =>
        categories.Categories.Select(c => (c.Label, c.IsKept));

    [Fact]
    public void NewConfiguration_ShouldExcludeTheAddOnsOnly_AndOfferTheCategoriesTheDatsUse()
    {
        var vm = CreateViewModel();
        var categories = vm.Filters.DatCategories;

        vm.NoIntroFilters.ExcludedDatCategories.Should().Equal(DatCategory.AddOns);
        vm.NoIntroFilters.ExcludeBios.Should().BeFalse();
        vm.Filters.KeepBios.Should().BeTrue();
        categories.Categories.Select(c => c.Name).Should().Equal(DatCategory.Known);
        categories.Categories.Where(c => !c.IsKept).Select(c => c.Name).Should().Equal(DatCategory.AddOns);
        categories.StatusText.Should().Be("Catégories des DAT No-Intro et Redump.");
        categories.Tooltip.Should().StartWith(vm.Localization.Tr("filters.tooltip_dat_categories"))
            .And.EndWith("une puce d'extension, (Enhancement Chip), va dans Add-Ons.", "the tooltip tells how a game is filed by its name");
        vm.BuildConfigUnchecked().Filters.NoIntroExcludedDatCategories.Should().Equal(DatCategory.AddOns);
    }

    [Fact]
    public void ConfigurationOfFormat2_ShouldExcludeNoCategory()
    {
        var vm = CreateViewModel("""{ "config_version": 2 }""");
        var categories = vm.Filters.DatCategories;

        vm.NoIntroFilters.ExcludedDatCategories.Should().BeEmpty();
        categories.Categories.Should().OnlyContain(c => c.IsKept);
    }

    [Fact]
    public async Task DatRead_ShouldOfferItsCategoriesWithTheirNumberOfGames_AndTheExcludedOnesItLacks()
    {
        var vm = CreateViewModel(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["config_version"] = 3,
            ["nointro_excluded_dat_categories"] = new[] { "Add-Ons", "Coverdiscs" }
        }));
        var categories = vm.Filters.DatCategories;

        vm.Directories.DatFilePath = _tmp.WriteText("snes.dat", SnesDat);
        await vm.Directories.DatLoadTask;

        // In the order the DATs use, games first; a category excluded before stays in sight, to be kept again
        Shown(categories).Should().Equal(
            ("Games (2)", true), ("Add-Ons (1)", false), ("Applications (1)", true), ("Preproduction (1)", true), ("Coverdiscs", false));
        categories.StatusText.Should().Be("Catégories de ce DAT, avec leur nombre de jeux.");
        vm.Filters.DatCategoriesStatusText.Should().Be(categories.StatusText);

        // Without a DAT, the categories the DATs use come back
        vm.Directories.DatFilePath = string.Empty;
        await vm.Directories.DatLoadTask;
        categories.Categories.Select(c => c.Name).Should().Equal(DatCategory.Known);
    }

    [Fact]
    public async Task DatWithoutCategory_ShouldSayThatTheNamesAloneFileTheGames()
    {
        var vm = CreateViewModel();
        var categories = vm.Filters.DatCategories;

        vm.Directories.DatFilePath = _tmp.WriteText("old.dat", """
            <?xml version="1.0"?>
            <datafile><header><name>Old</name><homepage>No-Intro</homepage></header>
              <game name="Game (USA)"><rom name="Game (USA).sfc" size="4" crc="00000001"/></game>
            </datafile>
            """);
        await vm.Directories.DatLoadTask;

        categories.StatusText.Should().Be("Ce DAT ne donne aucune catégorie : un jeu n'est classé que d'après son nom.");
        categories.Categories.Select(c => c.Name).Should().Equal(DatCategory.Known);
    }

    [Fact]
    public async Task Options_ShouldBeChangedAndSaved()
    {
        var vm = CreateViewModel();
        var categories = vm.Filters.DatCategories;

        categories.Categories.Single(c => c.Name == "Demos").IsKept = false;
        categories.Categories.Single(c => c.Name == "Add-Ons").IsKept = true;
        vm.NoIntroFilters.ExcludedDatCategories.Should().Equal("Demos");
        categories.Categories.Where(c => !c.IsKept).Select(c => c.Name).Should().Equal("Demos");
        vm.Filters.KeepBios = false;
        vm.NoIntroFilters.ExcludeBios.Should().BeTrue();

        await vm.SaveConfigurationAsync();
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_tmp.Path, "config.json")));
        saved.RootElement.GetProperty("nointro_excluded_dat_categories").EnumerateArray().Select(e => e.GetString()).Should().Equal("Demos");
        saved.RootElement.GetProperty("nointro_exclude_bios").GetBoolean().Should().BeTrue();

        var reloaded = TestMainViewModel.Create(new HeadlessDialogService(), Path.Combine(_tmp.Path, "config.json"));
        reloaded.NoIntroFilters.ExcludedDatCategories.Should().Equal("Demos");
        reloaded.Filters.KeepBios.Should().BeFalse();
    }

    [Fact]
    public void AllCategories_ShouldBeCheckedOrUncheckedAtOnce()
    {
        var vm = CreateViewModel();
        var categories = vm.Filters.DatCategories;

        categories.KeepAllCommand.CanExecute(null).Should().BeTrue();
        categories.KeepAllCommand.Execute(null);
        vm.NoIntroFilters.ExcludedDatCategories.Should().BeEmpty();
        categories.KeepAllCommand.CanExecute(null).Should().BeFalse();

        categories.ExcludeAllCommand.Execute(null);
        vm.NoIntroFilters.ExcludedDatCategories.Should().BeEquivalentTo(DatCategory.Known);
        categories.Categories.Should().OnlyContain(c => !c.IsKept);
        categories.ExcludeAllCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Texts_ShouldFollowTheLanguage()
    {
        var vm = CreateViewModel();
        var categories = vm.Filters.DatCategories;

        var notified = new List<string?>();
        categories.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        vm.UiLocale = "en";
        notified.Should().Contain([nameof(categories.StatusText), nameof(categories.Tooltip)]);
        categories.Tooltip.Should().StartWith("Categories the No-Intro or Redump DAT gives each game");
        categories.StatusText.Should().Be("Categories of the No-Intro and Redump DATs.");
        vm.Filters.DatCategoriesStatusText.Should().Be(categories.StatusText);
        vm.Filters.BiosTooltip.Should().Be(vm.Localization.Tr("filters.tooltip_keep_nointro_bios"));
    }
}
