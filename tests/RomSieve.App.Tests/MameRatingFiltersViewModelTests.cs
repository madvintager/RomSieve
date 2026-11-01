using System.Text.Json;
using RomSieve.App;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// Rating filters and size budget of the MAME machines in the Filters page: ratings read from the rating file chosen in
/// the Collection step, budget turned on and off, options read from and saved to <c>config.json</c>, translation.
/// </summary>
public sealed class MameRatingFiltersViewModelTests : IDisposable
{
    private const string BestGames = """
        [FOLDER_SETTINGS]
        RootFolderIcon mame
        [ROOT_FOLDER]
        [80 to 90 (Very Good)]
        19xx
        [90 to 100 (Best Games)]
        sf2
        dkong
        """;

    private readonly TempDirectoryFixture _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private MainViewModel CreateViewModel(string json = "{}") =>
        TestMainViewModel.Create(new HeadlessDialogService(), _tmp.WriteText("config.json", json));

    // The file is read off the UI thread once chosen: waits for its ratings
    private static async Task WaitForRatingsAsync(MameRatingFiltersViewModel ratings)
    {
        for (var i = 0; i < 200 && !ratings.HasRatings; i++)
        {
            await Task.Delay(10);
        }

        ratings.HasRatings.Should().BeTrue();
    }

    [Fact]
    public async Task RatingFile_ShouldBeReadOnceChosen()
    {
        var vm = CreateViewModel();
        var ratings = vm.Filters.Ratings;

        ratings.HasRatings.Should().BeFalse();
        ratings.StatusText.Should().Be("Choisissez un fichier de notes (bestgames.ini) à l'étape Collection.");
        ratings.RatingOptions.Select(o => o.Label).Should().Equal("Toutes", "50 et plus", "60 et plus", "70 et plus", "80 et plus", "90 et plus");

        vm.Directories.MameRatingFile = _tmp.WriteText("bestgames.ini", BestGames);
        await WaitForRatingsAsync(ratings);
        ratings.StatusText.Should().Be("3 machines notées.");

        await ratings.LoadAsync(Path.Combine(_tmp.Path, "missing.ini"));
        ratings.HasRatings.Should().BeFalse();
        ratings.StatusText.Should().Be("Fichier de notes introuvable ou illisible.");
        vm.UiLocale = "en";
        ratings.StatusText.Should().Be("Rating file not found or unreadable.");
        ratings.RatingOptions[4].Label.Should().Be("80 or more");
    }

    [Fact]
    public async Task Options_ShouldBeReadPresentedAndSaved()
    {
        var vm = CreateViewModel("""{ "mame_minimum_rating": 80, "mame_exclude_unrated": true }""");
        var ratings = vm.Filters.Ratings;

        ratings.MinimumRating.Should().Be(80);
        ratings.KeepUnrated.Should().BeFalse();
        ratings.IsBudgetEnabled.Should().BeFalse();
        ratings.BudgetGb.Should().Be(64m, "turned on, the limit offers the size of a common memory card");

        // Turned on, the limit takes the budget shown; turned off, it is kept for the next time
        ratings.IsBudgetEnabled = true;
        vm.NoIntroFilters.MameSizeBudgetGb.Should().Be(64);
        ratings.BudgetGb = 31.5m;
        vm.NoIntroFilters.MameSizeBudgetGb.Should().Be(31.5);
        ratings.IsBudgetEnabled = false;
        vm.NoIntroFilters.MameSizeBudgetGb.Should().Be(0);
        ratings.BudgetGb.Should().Be(31.5m);
        ratings.IsBudgetEnabled = true;
        vm.NoIntroFilters.MameSizeBudgetGb.Should().Be(31.5);

        ratings.MinimumRating = 90;
        ratings.KeepUnrated = true;
        await vm.SaveConfigurationAsync();
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_tmp.Path, "config.json")));
        saved.RootElement.GetProperty("mame_minimum_rating").GetInt32().Should().Be(90);
        saved.RootElement.GetProperty("mame_exclude_unrated").GetBoolean().Should().BeFalse();
        saved.RootElement.GetProperty("mame_size_budget_gb").GetDouble().Should().Be(31.5);
        saved.RootElement.GetProperty("mame_rating_file").GetString().Should().BeEmpty();
    }

    /// <summary>
    /// The ratings and size buttons of the "Refine" row: the ratings button says when there is no file (no restriction
    /// then), names its only restriction or counts them; the size button gives the budget.
    /// </summary>
    [Fact]
    public async Task Summary_ShouldNameOrCountTheRestrictions()
    {
        var vm = CreateViewModel("""{ "mame_exclude_unrated": true }""");
        var ratings = vm.Filters.Ratings;

        ratings.RatingsText.Should().Be("Notes : aucun fichier");
        ratings.IsRatingRestricted.Should().BeFalse("without a file, nothing is excluded");
        ratings.RatingsTooltip.Should().StartWith(ratings.StatusText);
        ratings.BudgetText.Should().Be("Volume : sans limite");

        vm.Directories.MameRatingFile = _tmp.WriteText("bestgames.ini", BestGames);
        await WaitForRatingsAsync(ratings);
        ratings.RatingsText.Should().Be("Notes : sans jeux non notés");
        ratings.IsRatingRestricted.Should().BeTrue();
        ratings.MinimumRating = 80;
        ratings.RatingsText.Should().Be("Notes : 2 restrictions");
        ratings.RatingsTooltip.Should().EndWith("Restrictions : 80 et plus, sans jeux non notés");
        ratings.KeepUnrated = true;
        ratings.MinimumRating = 0;
        ratings.RatingsText.Should().Be("Notes : toutes");
        ratings.IsRatingRestricted.Should().BeFalse();

        ratings.IsBudgetEnabled = true;
        ratings.BudgetText.Should().Be("Volume : 64 Go au plus");
        ratings.BudgetGb = 31.5m;
        ratings.BudgetText.Should().Be("Volume : 31.5 Go au plus");
        vm.UiLocale = "en";
        ratings.BudgetText.Should().Be("Size: 31.5 GB at most");
        ratings.RatingsText.Should().Be("Ratings: all");
    }

    [Fact]
    public void SavedBudget_ShouldTurnTheLimitOn()
    {
        var ratings = CreateViewModel("""{ "mame_size_budget_gb": 128 }""").Filters.Ratings;

        ratings.IsBudgetEnabled.Should().BeTrue();
        ratings.BudgetGb.Should().Be(128m);
        ratings.BudgetGb = 0;
        ratings.BudgetGb.Should().Be(128m, "a budget of nothing is ignored");
    }
}
