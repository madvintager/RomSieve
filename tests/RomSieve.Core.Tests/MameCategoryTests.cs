using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Category files of the MAME machines (<see cref="MameCategoryFile"/>): <c>catver.ini</c> and the folders of
/// progettosnaps (<c>catlist.ini</c>, <c>genre.ini</c>, excerpts of the 0.283 files), the category filters, their
/// options in <c>config.json</c>, and the <c>{genre}</c> and <c>{category}</c> variables.
/// </summary>
public sealed class MameCategoryTests
{
    private static readonly CoreTestServices Services = new();

    // Excerpt of catlist.ini 0.283: the arcade machines are marked, a machine for adults ends with "* Mature *"
    private const string CatList = """
        [FOLDER_SETTINGS]
        RootFolderIcon mame
        SubFolderIcon folder

        ;; CATLIST.ini 0.283 / 19-Dec-25 / MAME 0.283 ;;

        [ROOT_FOLDER]

        [Arcade: Fighter / Versus]
        sf2

        [Arcade: Shooter / Flying Vertical]
        19xx

        [Arcade: Casino / Cards * Mature *]
        bikkuri

        [Computer / Home System]
        amiga

        [Game Console/Computer / Home Videogame/Home System]
        pc88va
        """;

    // Excerpt of catver.ini: a [Category] section, then other sections that are not categories
    private const string CatVer = """
        ;; CatVer 0.283 ;;
        [Category]
        sf2=Fighter / Versus
        bikkuri=Casino / Cards * Mature *
        pong=Ball & Paddle

        [VerAdded]
        sf2=0.31
        """;

    private static MameCategories Parse(string text) => MameCategoryFile.Parse(new StringReader(text));

    [Fact]
    public void CatList_ShouldGiveTheCategoryOfEachMachineAndMarkTheArcadeOnes()
    {
        var categories = Parse(CatList);

        categories.Count.Should().Be(5);
        categories.ByMachine["sf2"].Should().Be(new MameCategory("Fighter", "Versus", IsMature: false, IsArcade: true));
        categories.ByMachine["19xx"].Name.Should().Be("Shooter / Flying Vertical");
        categories.ByMachine["bikkuri"].Should().Be(new MameCategory("Casino", "Cards", IsMature: true, IsArcade: true));
        categories.ByMachine["amiga"].Should().Be(new MameCategory("Computer", "Home System", IsMature: false, IsArcade: false),
            "a file that marks the arcade machines says the others are not");

        // A bare slash belongs to a name: only a slash between spaces separates the genre from the subgenre
        categories.ByMachine["pc88va"].Should().Be(new MameCategory("Game Console/Computer", "Home Videogame/Home System", false, false));
        categories.MarksArcade.Should().BeTrue();
        categories.Genres.Should().Equal("Casino", "Computer", "Fighter", "Game Console/Computer", "Shooter");
        categories.ByMachine.Should().NotContainKey("RootFolderIcon mame");
    }

    [Fact]
    public void CatVer_ShouldReadItsCategorySectionOnly()
    {
        var categories = Parse(CatVer);

        categories.Count.Should().Be(3);
        categories.ByMachine["sf2"].Should().Be(new MameCategory("Fighter", "Versus"));
        categories.ByMachine["bikkuri"].IsMature.Should().BeTrue();
        categories.ByMachine["pong"].Should().Be(new MameCategory("Ball & Paddle", null));
        categories.MarksArcade.Should().BeFalse("catver.ini does not tell the arcade machines from the others");
    }

    [Fact]
    public void GenreIni_ShouldGiveTheGenreOnly()
    {
        var categories = Parse("""
            [FOLDER_SETTINGS]
            RootFolderIcon mame
            [ROOT_FOLDER]
            [Shooter]
            19xx
            [TTL * Ball & Paddle]
            pong
            """);

        categories.ByMachine["19xx"].Should().Be(new MameCategory("Shooter", null));
        categories.ByMachine["pong"].Genre.Should().Be("TTL * Ball & Paddle");
    }

    [Fact]
    public void Find_ShouldFallBackOnTheParentOfAClone()
    {
        var categories = Parse(CatList);

        categories.Find("sf2")!.Genre.Should().Be("Fighter");
        categories.Find("sf2ua", parent: "sf2")!.Genre.Should().Be("Fighter");
        categories.Find("unknown", parent: "other").Should().BeNull();
    }

    private static (bool IsKept, string? Reason) Evaluate(MameCategory? category, Action<FilterConfig> configure)
    {
        var filters = new FilterConfig();
        configure(filters);
        var parsed = new MameDescriptionParser().ParseDescription("Some Game (World)") with
        {
            Machine = new MameMachineInfo(false, false, false, MameDriverStatus.Good, Category: category)
        };
        var (isKept, reason) = Services.FilterService.EvaluateFilter(parsed, filters);
        return (isKept, reason is null ? null : Services.I18n.Format(reason));
    }

    [Fact]
    public void CategoryFilters_ShouldExcludeAdultsNonArcadeMachinesAndGenres()
    {
        var shooter = new MameCategory("Shooter", "Flying Vertical", false, true);
        var adults = new MameCategory("Casino", "Cards", true, true);
        var computer = new MameCategory("Computer", "Home System", false, false);
        var unmarked = new MameCategory("Fighter", "Versus");

        Evaluate(adults, f => f.MameExcludeMature = true).Should().Be((false, "Machine pour adultes exclue"));
        Evaluate(shooter, f => f.MameExcludeMature = true).IsKept.Should().BeTrue();
        Evaluate(computer, f => f.MameArcadeOnly = true).Should().Be((false, "Pas une machine d'arcade (Computer / Home System)"));
        Evaluate(unmarked, f => f.MameArcadeOnly = true).IsKept.Should().BeTrue("a file that does not mark the arcade machines excludes none");
        Evaluate(shooter, f => f.MameExcludedGenres = ["shooter"]).Should().Be((false, "Genre Shooter exclu"));
        Evaluate(unmarked, f => f.MameExcludedGenres = ["Shooter"]).IsKept.Should().BeTrue();

        // A machine without category is never excluded
        Evaluate(null, f =>
        {
            f.MameExcludeMature = true;
            f.MameArcadeOnly = true;
            f.MameExcludedGenres = ["Shooter"];
        }).IsKept.Should().BeTrue();
    }

    [Fact]
    public void Options_ShouldBeSavedReadAndPassedToTheRun()
    {
        var settings = new SortSettings
        {
            MameCategoryFile = "catlist.ini",
            MameExcludeMature = true,
            MameArcadeOnly = true,
            MameExcludedGenres = ["Casino", "Quiz"]
        };
        var data = new Dictionary<string, object?>();

        SortSettingsSerializer.Write(settings, data);

        data["mame_category_file"].Should().Be("catlist.ini");
        data["mame_exclude_mature"].Should().Be(true);
        data["mame_arcade_only"].Should().Be(true);
        data["mame_excluded_genres"].Should().BeEquivalentTo(new[] { "Casino", "Quiz" });

        var config = SortSettingsSerializer.ToSortConfig(SortSettingsSerializer.Read(data));
        config.MameCategoryFile.Should().Be("catlist.ini");
        config.Filters.MameExcludeMature.Should().BeTrue();
        config.Filters.MameArcadeOnly.Should().BeTrue();
        config.Filters.MameExcludedGenres.Should().Equal("Casino", "Quiz");

        var older = SortSettingsSerializer.ToSortConfig(SortSettingsSerializer.Read(new Dictionary<string, object?>()));
        older.MameCategoryFile.Should().BeNull();
        older.Filters.MameExcludeMature.Should().BeFalse();
        older.Filters.MameArcadeOnly.Should().BeFalse();
        older.Filters.MameExcludedGenres.Should().BeEmpty();
    }

    [Fact]
    public void PatternVariables_ShouldNameTheGenreAndTheCategory()
    {
        using var tmp = new TempDirectoryFixture();
        var config = new SortConfig { SourceDir = tmp.Path, DestinationDir = tmp.Path, Pattern = "{genre}/{category}" };
        var parsed = new MameDescriptionParser().ParseDescription("19XX: The War Against Destiny (Europe 960104)") with
        {
            Machine = new MameMachineInfo(false, false, false, MameDriverStatus.Good,
                Category: new MameCategory("Shooter", "Flying Vertical", false, true))
        };

        Services.Organizer.BuildTargetPath(parsed, "19xx.zip", config)
            .Should().Be(Path.Combine(tmp.Path, "Shooter", "Shooter - Flying Vertical", "19xx.zip"));
        Services.Organizer.BuildTargetPath(parsed with { Machine = parsed.Machine! with { Category = null } }, "19xx.zip", config)
            .Should().Be(Path.Combine(tmp.Path, "Unknown", "Unknown", "19xx.zip"));
        Services.Organizer.CheckPattern("{genre}/{category}").UnknownVariables.Should().BeEmpty();
    }
}
