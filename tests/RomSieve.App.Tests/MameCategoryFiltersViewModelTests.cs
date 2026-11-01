using System.Text.Json;
using RomSieve.App;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// Category filters of the MAME machines in the Filters page: genres read from the category file chosen in the
/// Collection step, options read from and saved to <c>config.json</c>, and their translation.
/// </summary>
public sealed class MameCategoryFiltersViewModelTests : IDisposable
{
    private const string CatList = """
        [FOLDER_SETTINGS]
        RootFolderIcon mame
        [ROOT_FOLDER]
        [Arcade: Fighter / Versus]
        sf2
        [Arcade: Shooter / Flying Vertical]
        19xx
        [Arcade: Casino / Cards * Mature *]
        bikkuri
        [Computer / Home System]
        amiga
        """;

    private readonly TempDirectoryFixture _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private MainViewModel CreateViewModel(string json = "{}") =>
        TestMainViewModel.Create(new HeadlessDialogService(), _tmp.WriteText("config.json", json));

    // The file is read off the UI thread once chosen: waits for its genres
    private static async Task WaitForGenresAsync(MameCategoryFiltersViewModel categories, int count)
    {
        for (var i = 0; i < 200 && categories.Genres.Count != count; i++)
        {
            await Task.Delay(10);
        }

        categories.Genres.Should().HaveCount(count);
    }

    [Fact]
    public async Task CategoryFile_ShouldListItsGenresOnceChosen()
    {
        var vm = CreateViewModel();
        var categories = vm.Filters.Categories;

        categories.HasCategories.Should().BeFalse();
        categories.HasGenres.Should().BeFalse();
        categories.StatusText.Should().Be("Choisissez un fichier de catégories (catver.ini, catlist.ini) à l'étape Collection.");

        vm.Directories.MameCategoryFile = _tmp.WriteText("catlist.ini", CatList);
        await WaitForGenresAsync(categories, 4);

        categories.Genres.Select(g => g.Name).Should().Equal("Casino", "Computer", "Fighter", "Shooter");
        categories.Genres.Should().OnlyContain(g => g.IsKept);
        categories.HasCategories.Should().BeTrue();
        categories.IsArcadeFilterAvailable.Should().BeTrue("catlist.ini marks the arcade machines");
        categories.StatusText.Should().Be("4 machines classées en 4 genres.");
        categories.GenresText.Should().Be("Tous les genres");

        // A file that does not mark the arcade machines leaves that filter out; a missing file says so
        await categories.LoadAsync(_tmp.WriteText("catver.ini", "[Category]\nsf2=Fighter / Versus\n"));
        categories.IsArcadeFilterAvailable.Should().BeFalse();
        await categories.LoadAsync(Path.Combine(_tmp.Path, "missing.ini"));
        categories.HasCategories.Should().BeFalse();
        categories.StatusText.Should().Be("Fichier de catégories introuvable ou illisible.");
    }

    [Fact]
    public async Task Options_ShouldBeReadPresentedAndSaved()
    {
        var file = _tmp.WriteText("catlist.ini", CatList);
        var vm = CreateViewModel(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["mame_category_file"] = file,
            ["mame_exclude_mature"] = true,
            ["mame_excluded_genres"] = new[] { "Quiz", "Casino" }
        }));
        var categories = vm.Filters.Categories;

        // A genre excluded before stays listed, unchecked, even when the file does not have it
        await WaitForGenresAsync(categories, 5);
        categories.Genres.Select(g => (g.Name, g.IsKept)).Should().Equal(
            ("Casino", false), ("Computer", true), ("Fighter", true), ("Quiz", false), ("Shooter", true));
        categories.KeepMature.Should().BeFalse();
        categories.KeepNonArcade.Should().BeTrue();
        categories.GenresText.Should().Be("3 genres sur 5");
        categories.GenresTooltip.Should().EndWith("Exclus : Quiz, Casino");

        categories.Genres.Single(g => g.Name == "Shooter").IsKept = false;
        categories.Genres.Single(g => g.Name == "Quiz").IsKept = true;
        categories.KeepNonArcade = false;
        categories.KeepMature = true;

        vm.NoIntroFilters.MameExcludedGenres.Should().Equal("Casino", "Shooter");
        categories.Genres.Select(g => g.Name).Should().Equal("Casino", "Computer", "Fighter", "Shooter");
        categories.GenresText.Should().Be("2 genres sur 4");

        await vm.SaveConfigurationAsync();
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_tmp.Path, "config.json")));
        saved.RootElement.GetProperty("mame_category_file").GetString().Should().Be(file);
        saved.RootElement.GetProperty("mame_exclude_mature").GetBoolean().Should().BeFalse();
        saved.RootElement.GetProperty("mame_arcade_only").GetBoolean().Should().BeTrue();
        saved.RootElement.GetProperty("mame_excluded_genres").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("Casino", "Shooter");
    }

    /// <summary>
    /// The categories button of the "Refine" row: it says when there is no file (no restriction then, as nothing is
    /// excluded), names its only restriction, and counts them when there are more.
    /// </summary>
    [Fact]
    public async Task Summary_ShouldNameOrCountTheRestrictions()
    {
        var vm = CreateViewModel("""{ "mame_exclude_mature": true }""");
        var categories = vm.Filters.Categories;

        categories.IsAvailable.Should().BeFalse();
        categories.IsRestricted.Should().BeFalse("without a file, nothing is excluded");
        categories.SummaryText.Should().Be("Catégories : aucun fichier");
        categories.SummaryTooltip.Should().StartWith(categories.StatusText);

        vm.Directories.MameCategoryFile = _tmp.WriteText("catlist.ini", CatList);
        await WaitForGenresAsync(categories, 4);
        categories.IsAvailable.Should().BeTrue();
        categories.IsRestricted.Should().BeTrue();
        categories.SummaryText.Should().Be("Catégories : sans machines pour adultes");

        categories.KeepMature = true;
        categories.SummaryText.Should().Be("Catégories : toutes");
        categories.SummaryTooltip.Should().NotContain("Restrictions");
        categories.IsRestricted.Should().BeFalse();

        categories.KeepNonArcade = false;
        categories.Genres[0].IsKept = false;
        categories.SummaryText.Should().Be("Catégories : 2 restrictions");
        categories.SummaryTooltip.Should().EndWith("Restrictions : arcade uniquement, 3 genres sur 4");

        // A file that does not mark the arcade machines leaves that filter out of the restrictions
        await categories.LoadAsync(_tmp.WriteText("catver.ini", "[Category]\nsf2=Fighter / Versus\n"));
        categories.Genres.Select(g => g.Name).Should().Equal("Casino", "Fighter");
        categories.SummaryText.Should().Be($"Catégories : {categories.GenresText}");
        vm.UiLocale = "en";
        categories.SummaryText.Should().Be($"Categories: {categories.GenresText}");
    }

    /// <summary>
    /// "Uncheck all" excludes every listed genre, those excluded before included; "Check all" excludes none, and a genre
    /// the file does not have then leaves the list. Each is disabled when it would change nothing.
    /// </summary>
    [Fact]
    public async Task AllGenres_ShouldBeCheckedOrUncheckedAtOnce()
    {
        var vm = CreateViewModel(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["mame_category_file"] = _tmp.WriteText("catlist.ini", CatList),
            ["mame_excluded_genres"] = new[] { "Quiz" }
        }));
        var categories = vm.Filters.Categories;
        await WaitForGenresAsync(categories, 5);
        categories.KeepAllGenresCommand.CanExecute(null).Should().BeTrue();
        categories.ExcludeAllGenresCommand.CanExecute(null).Should().BeTrue();

        categories.ExcludeAllGenresCommand.Execute(null);

        vm.NoIntroFilters.MameExcludedGenres.Should().Equal("Casino", "Computer", "Fighter", "Quiz", "Shooter");
        categories.Genres.Should().OnlyContain(g => !g.IsKept);
        categories.GenresText.Should().Be("0 genres sur 5");
        categories.ExcludeAllGenresCommand.CanExecute(null).Should().BeFalse();

        categories.Genres.Single(g => g.Name == "Shooter").IsKept = true;
        vm.NoIntroFilters.MameExcludedGenres.Should().Equal("Casino", "Computer", "Fighter", "Quiz");
        categories.ExcludeAllGenresCommand.CanExecute(null).Should().BeTrue();

        categories.KeepAllGenresCommand.Execute(null);

        vm.NoIntroFilters.MameExcludedGenres.Should().BeEmpty();
        categories.Genres.Select(g => g.Name).Should().Equal("Casino", "Computer", "Fighter", "Shooter");
        categories.Genres.Should().OnlyContain(g => g.IsKept);
        categories.GenresText.Should().Be("Tous les genres");
        categories.KeepAllGenresCommand.CanExecute(null).Should().BeFalse();
    }

    /// <summary>The panel says why the arcade filter is greyed out only when a file is read that does not mark the arcade.</summary>
    [Fact]
    public async Task ArcadeFilterMissing_ShouldOnlyBeSaidOfAFileWithoutArcadeMarks()
    {
        var vm = CreateViewModel();
        var categories = vm.Filters.Categories;
        categories.IsArcadeFilterMissing.Should().BeFalse("without a file, the status line already asks for one");

        await categories.LoadAsync(_tmp.WriteText("catlist.ini", CatList));
        categories.IsArcadeFilterMissing.Should().BeFalse();

        await categories.LoadAsync(_tmp.WriteText("catver.ini", "[Category]\nsf2=Fighter / Versus\n"));
        categories.IsArcadeFilterMissing.Should().BeTrue();
        categories.IsArcadeFilterAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task LanguageSwitch_ShouldTranslateTheTexts()
    {
        var vm = CreateViewModel();
        var categories = vm.Filters.Categories;
        vm.Directories.MameCategoryFile = _tmp.WriteText("catlist.ini", CatList);
        await WaitForGenresAsync(categories, 4);
        categories.Genres[0].IsKept = false;

        vm.UiLocale = "en";

        categories.StatusText.Should().Be("4 machines in 4 genres.");
        categories.GenresText.Should().Be("3 genres out of 4");
        categories.GenresTooltip.Should().EndWith("Excluded: Casino");
    }

    [Fact]
    public void Directories_ShouldOfferTheCategoryFileAndItsVariablesWithMame()
    {
        var vm = CreateViewModel();
        var directories = vm.Directories;
        directories.AreMameVariablesShown.Should().BeFalse();

        directories.SelectedNamingConvention = NamingConvention.Mame;

        directories.IsMameConvention.Should().BeTrue();
        directories.AreMameVariablesShown.Should().BeTrue();
        directories.PatternVariables[11].Tag.Should().Be("{genre}");
        directories.PatternVariables[12].Tag.Should().Be("{category}");
        directories.MameCategoryFile = "catlist.ini";
        vm.BuildConfigUnchecked().MameCategoryFile.Should().Be("catlist.ini");
    }
}
