using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Rating files of the MAME machines (<see cref="MameRatingFile"/>, excerpt of <c>bestgames.ini</c> 0.280), the rating
/// filters, the size budget (<see cref="MameSizeBudget"/>) and their options in <c>config.json</c>.
/// </summary>
public sealed class MameRatingTests
{
    private static readonly CoreTestServices Services = new();

    private const string BestGames = """
        [FOLDER_SETTINGS]
        RootFolderIcon mame
        SubFolderIcon folder

        ;; bestgames.ini 0.280 / 09-Sep-25 / MAME 0.280 ;;

        [ROOT_FOLDER]

        [0 to 10 (Worst)]
        azurian

        [80 to 90 (Very Good)]
        19xx

        [90 to 100 (Best Games)]
        sf2
        dkong
        """;

    [Fact]
    public void BestGames_ShouldGiveTheRatingOfEachMachine()
    {
        var ratings = MameRatingFile.Parse(new StringReader(BestGames));

        ratings.Count.Should().Be(4);
        ratings.ByMachine["sf2"].Should().Be(new MameRating(90, "90 to 100 (Best Games)"));
        ratings.ByMachine["19xx"].Score.Should().Be(80);
        ratings.ByMachine["azurian"].Score.Should().Be(0);
        ratings.ByMachine.Should().NotContainKey("RootFolderIcon mame");

        // A clone takes the rating of its parent; a machine the file does not rate is said so
        ratings.Find("sf2ua", parent: "sf2").Score.Should().Be(90);
        ratings.Find("pong").IsUnrated.Should().BeTrue();
        MameRatingFile.ParseRange("Shooter / Flying Vertical").Should().BeNull();
    }

    private static (bool IsKept, string? Reason) Evaluate(MameRating? rating, Action<FilterConfig> configure)
    {
        var filters = new FilterConfig();
        configure(filters);
        var parsed = new MameDescriptionParser().ParseDescription("Some Game (World)") with
        {
            Machine = new MameMachineInfo(false, false, false, MameDriverStatus.Good, Rating: rating)
        };
        var (isKept, reason) = Services.FilterService.EvaluateFilter(parsed, filters);
        return (isKept, reason is null ? null : Services.I18n.Format(reason));
    }

    [Fact]
    public void RatingFilters_ShouldKeepTheBestRatedAndTheUnratedOnDemand()
    {
        var veryGood = new MameRating(80, "80 to 90 (Very Good)");

        Evaluate(veryGood, f => f.MameMinimumRating = 80).IsKept.Should().BeTrue();
        Evaluate(veryGood, f => f.MameMinimumRating = 90).Should().Be((false, "Note insuffisante (80 to 90 (Very Good), 90 au moins)"));

        // An unrated machine is kept, unless asked otherwise, whatever the minimum rating
        Evaluate(MameRating.Unrated, f => f.MameMinimumRating = 90).IsKept.Should().BeTrue();
        Evaluate(MameRating.Unrated, f => f.MameExcludeUnrated = true).Should().Be((false, "Machine non notée exclue"));

        // Without a rating file, nothing is excluded
        Evaluate(null, f =>
        {
            f.MameMinimumRating = 90;
            f.MameExcludeUnrated = true;
        }).IsKept.Should().BeTrue();
    }

    [Fact]
    public void Options_ShouldBeSavedReadAndPassedToTheRun()
    {
        var settings = new SortSettings
        {
            MameRatingFile = "bestgames.ini",
            MameMinimumRating = 70,
            MameExcludeUnrated = true,
            MameSizeBudgetGb = 63.5
        };
        var data = new Dictionary<string, object?>();

        SortSettingsSerializer.Write(settings, data);

        data["mame_rating_file"].Should().Be("bestgames.ini");
        data["mame_minimum_rating"].Should().Be(70);
        data["mame_exclude_unrated"].Should().Be(true);
        data["mame_size_budget_gb"].Should().Be(63.5);

        var config = SortSettingsSerializer.ToSortConfig(SortSettingsSerializer.Read(data));
        config.MameRatingFile.Should().Be("bestgames.ini");
        config.Filters.MameMinimumRating.Should().Be(70);
        config.Filters.MameExcludeUnrated.Should().BeTrue();
        config.Filters.MameSizeBudgetBytes.Should().Be(63_500_000_000, "a memory card counts 10^9 bytes in a gigabyte");

        var edited = SortSettingsSerializer.Read(new Dictionary<string, object?>
        {
            ["mame_minimum_rating"] = 250,
            ["mame_size_budget_gb"] = -3
        });
        edited.MameMinimumRating.Should().Be(100);
        edited.MameSizeBudgetGb.Should().Be(0);

        var older = SortSettingsSerializer.ToSortConfig(SortSettingsSerializer.Read(new Dictionary<string, object?>()));
        older.MameRatingFile.Should().BeNull();
        older.Filters.MameMinimumRating.Should().Be(0);
        older.Filters.MameExcludeUnrated.Should().BeFalse();
        older.Filters.MameSizeBudgetBytes.Should().Be(0);
    }

    private static ProcessItem Set(string name, long size, int? rating = null, params string[] requiredSets) => new()
    {
        SourcePath = name + ".zip",
        FileSize = size,
        Parsed = new ParsedRomName
        {
            RawFilename = name + ".zip",
            Title = name,
            Machine = new MameMachineInfo(false, false, false, MameDriverStatus.Good,
                Rating: rating is { } score ? new MameRating(score, $"{score}") : MameRating.Unrated)
        },
        MameSet = new MameSetInfo(name, 0, 0, 0) { RequiredSets = requiredSets }
    };

    [Fact]
    public void Budget_ShouldKeepTheBestRatedFirstThenTheSmallest()
    {
        var bios = Set("neogeo", 20);
        bios.Status = FileStatus.Excluded;
        var mslug = Set("mslug", 50, 90, "neogeo");
        var kof = Set("kof98", 60, 90, "neogeo");
        var sf2 = Set("sf2", 40, 80);
        var small = Set("small", 5);
        var big = Set("big", 30);
        var items = new List<ProcessItem> { bios, mslug, kof, sf2, small, big };

        var result = MameSizeBudget.Apply(items, 120)!;

        // mslug (90) and its BIOS: 70; kof98 (90, the BIOS counted once): 60 more do not fit; sf2 (80): 40, total 110;
        // then the unrated ones, the smallest first: small fits, big does not
        mslug.Status.Should().Be(FileStatus.Valid);
        kof.Status.Should().Be(FileStatus.Excluded);
        kof.IsOutsideBudget.Should().BeTrue();
        Services.I18n.Format(kof.StatusReason).Should().Be("Hors du volume de 0 Go (0 Go de plus)");
        sf2.Status.Should().Be(FileStatus.Valid);
        small.Status.Should().Be(FileStatus.Valid);
        big.Status.Should().Be(FileStatus.Excluded);
        bios.Status.Should().Be(FileStatus.Excluded, "the release selection keeps the sets a kept set needs, which the budget counted");
        result.UsedBytes.Should().Be(115);
        result.KeptCount.Should().Be(3);
        result.Excluded.Should().Equal(kof, big);

        // A larger budget gives the sets it left out their chance again; no budget gives them all back
        MameSizeBudget.Apply(items, 205)!.Excluded.Should().BeEmpty();
        kof.Status.Should().Be(FileStatus.Valid);
        kof.IsOutsideBudget.Should().BeFalse();
        kof.StatusReason.Should().BeNull();
        MameSizeBudget.Apply(items, 120);
        MameSizeBudget.Apply(items, 0).Should().BeNull();
        items.Should().NotContain(it => it.IsOutsideBudget);
        big.Status.Should().Be(FileStatus.Valid);
    }

    [Fact]
    public void Budget_ShouldCountTheChdsOfASet()
    {
        var area51 = Set("area51", 10, 90);
        area51.RelatedItems.Add(new ProcessItem
        {
            SourcePath = Path.Combine("area51", "area51.chd"),
            FileSize = 500,
            Parsed = new ParsedRomName { RawFilename = "area51.chd" }
        });
        var tetris = Set("tetris", 100, 50);

        MameSizeBudget.Apply([area51, tetris], 400);

        area51.Status.Should().Be(FileStatus.Excluded);
        tetris.Status.Should().Be(FileStatus.Valid);
        MameSizeBudget.FormatGigabytes(63_500_000_000).Should().Be("63.5");
    }
}
