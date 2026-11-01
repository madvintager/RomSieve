using System.IO;
using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// Chipset of an Amiga release, read from its TOSEC name, and the Amiga a collection is sorted for
/// (<see cref="FilterConfig.AmigaTarget"/>): the releases that need a later chipset are excluded, the <c>{chipset}</c>
/// variable makes a folder per machine, and for the AGA machines the AGA and OCS versions of a game are one game, the
/// 1G1R preferring the AGA one. Every other target keeps the games as before.
/// </summary>
public sealed class AmigaChipsetTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("fr");

    // Names of the TOSEC Amiga ADF DAT
    private const string ThemeParkAga = "Theme Park (1994)(Electronic Arts)(AGA)(M4).adf";
    private const string ThemeParkOcs = "Theme Park (1994)(Electronic Arts)(M4).adf";
    private const string AlienBreedBoth = "Alien Breed - Tower Assault (1994)(Team 17)(OCS-AGA).adf";
    private const string BansheeAga = "Banshee (1994)(Core)(AGA)(M4).adf";
    private const string PinballEcsAga = "Pinball Fantasies (1992)(21st Century)(ECS-AGA).adf";
    private const string JamesPondOcs = "James Pond 2 - Codename RoboCod (1991)(Millennium).adf";
    private const string JamesPondFix = "James Pond 2 - Codename RoboCod (1991)(Millennium)[f AGA NOMAD].adf";
    private const string JamesPondAga = "James Pond 2 - Codename RoboCod (1993)(Millennium)(AGA).adf";

    private static readonly string[] AllFiles =
        [ThemeParkAga, ThemeParkOcs, AlienBreedBoth, BansheeAga, PinballEcsAga, JamesPondOcs, JamesPondFix, JamesPondAga];

    public void Dispose() => _temp.Dispose();

    private ParsedRomName Parse(string name) => _services.NoIntroParser.ParseRomFilename(name);

    private SortConfig CreateConfig(AmigaTarget target, bool oneGameOneRom = true, string[]? files = null)
    {
        var src = _temp.CreateSubdirectory("src_" + Guid.NewGuid().ToString("N")[..8]);
        var dst = _temp.CreateSubdirectory("dst_" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var name in files ?? AllFiles)
        {
            File.WriteAllText(Path.Combine(src, name), name);
        }

        var filters = new FilterConfig
        {
            AmigaTarget = target,
            OneGameOneRom = oneGameOneRom,
            PriorityLanguages = oneGameOneRom ? ["en"] : [],
            ExcludeModifications = false
        };
        return new SortConfig { SourceDir = src, DestinationDir = dst, Filters = filters };
    }

    private async Task<Dictionary<string, ProcessItem>> SimulateAsync(SortConfig config)
    {
        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);
        return result.Items.ToDictionary(it => it.DisplayName);
    }

    [Theory]
    [InlineData(ThemeParkAga, AmigaChipset.Aga)]
    [InlineData(ThemeParkOcs, AmigaChipset.Unmarked)]
    [InlineData(AlienBreedBoth, AmigaChipset.OcsAga)]
    [InlineData(PinballEcsAga, AmigaChipset.EcsAga)]
    [InlineData("Zool (1993)(Gremlin)(A1200).adf", AmigaChipset.Aga)]
    [InlineData("Zool (1992)(Gremlin)(A1000).adf", AmigaChipset.Ocs)]
    [InlineData("Zool (1992)(Gremlin)(ECS).adf", AmigaChipset.Ecs)]
    [InlineData("Zool (1992)(Gremlin)(STE).st", AmigaChipset.Unmarked)]
    public void Chipset_ShouldBeReadFromTheSystemField(string name, AmigaChipset expected)
    {
        AmigaChipsets.Of(Parse(name)).Should().Be(expected);
    }

    [Theory]
    [InlineData("Lotus III (1992)(Gremlin)[f AGA NOMAD].adf", true)]
    [InlineData("Lotus III (1992)(Gremlin)[cr CSL][f loader CSL - AGA NOMAD].adf", true)]
    [InlineData("Lotus III (1992)(Gremlin)[f A1200 TFA].adf", true)]
    [InlineData("Lotus III (1992)(Gremlin)[f NTSC].adf", false)]
    [InlineData("Lotus III (1992)(Gremlin)[f A500+ ATX].adf", false)]
    [InlineData("Lotus III (1992)(Gremlin)[cr AGA].adf", false)]
    public void AgaFix_ShouldBeAFixNamingAnAgaMachine(string name, bool expected)
    {
        AmigaChipsets.IsAgaFix(Parse(name)).Should().Be(expected);
    }

    [Theory]
    [InlineData(AmigaTarget.Any, new string[0])]
    [InlineData(AmigaTarget.Aga, new string[0])]
    [InlineData(AmigaTarget.Ecs, new[] { ThemeParkAga, BansheeAga, JamesPondAga })]
    [InlineData(AmigaTarget.Ocs, new[] { ThemeParkAga, BansheeAga, JamesPondAga, PinballEcsAga })]
    public void Filter_ShouldExcludeTheReleasesThatNeedALaterChipset(AmigaTarget target, string[] excluded)
    {
        var config = new FilterConfig { AmigaTarget = target, ExcludeModifications = false };

        var actuallyExcluded = AllFiles.Where(name => !_services.FilterService.EvaluateFilter(Parse(name), config).IsKept);

        actuallyExcluded.Should().BeEquivalentTo(excluded);
    }

    [Fact]
    public void Filter_ShouldSayWhichChipsetTheReleaseNeeds()
    {
        var config = new FilterConfig { AmigaTarget = AmigaTarget.Ocs };

        var (isKept, reason) = _services.FilterService.EvaluateFilter(Parse(PinballEcsAga), config);

        isKept.Should().BeFalse();
        reason!.Key.Should().Be("reason.amiga_chipset_excluded");
        _services.I18n.Format(reason).Should().Be(
            "Demande un Amiga ECS-AGA : ne tourne pas sur la machine cible (Amiga 500, 1000, 2000 (OCS))");
    }

    [Fact]
    public void Filter_ShouldNeverExcludeAnotherSystem()
    {
        var config = new FilterConfig { AmigaTarget = AmigaTarget.Ocs };

        _services.FilterService.EvaluateFilter(Parse("Zool (1992)(Gremlin)(STE).st"), config).IsKept.Should().BeTrue();
    }

    [Fact]
    public void Config_ShouldBeSavedUnderStableNamesAndReadBack()
    {
        foreach (var target in Enum.GetValues<AmigaTarget>())
        {
            var data = new Dictionary<string, object?>();
            SortSettingsSerializer.Write(new SortSettings { AmigaTarget = target }, data);
            SortSettingsSerializer.Read(data).AmigaTarget.Should().Be(target);
            SortSettingsSerializer.ToSortConfig(new SortSettings { AmigaTarget = target }).Filters.AmigaTarget.Should().Be(target);
        }

        var saved = new Dictionary<string, object?>();
        SortSettingsSerializer.Write(new SortSettings { AmigaTarget = AmigaTarget.Aga }, saved);
        saved["amiga_target"].Should().Be("aga");
        SortSettingsSerializer.Read(new Dictionary<string, object?> { ["amiga_target"] = "a3000" }).AmigaTarget
            .Should().Be(AmigaTarget.Any, "an unknown value keeps every machine");
        SortSettingsSerializer.Read(new Dictionary<string, object?>()).AmigaTarget
            .Should().Be(AmigaTarget.Any, "a configuration saved before the option keeps every machine");
    }

    [Theory]
    [InlineData(ThemeParkAga, "AGA")]
    [InlineData(AlienBreedBoth, "OCS-AGA")]
    [InlineData(PinballEcsAga, "OCS-AGA")]
    [InlineData(JamesPondFix, "OCS-AGA")]
    [InlineData(ThemeParkOcs, "OCS-ECS")]
    [InlineData("Zool (1992)(Gremlin)(A1000).adf", "OCS-ECS")]
    public void ChipsetVariable_ShouldMakeAFolderPerMachine(string name, string folder)
    {
        var config = new SortConfig { SourceDir = "src", DestinationDir = "dst", Pattern = "{chipset}/{letter}/" };

        var target = _services.Organizer.BuildTargetPath(Parse(name), name, config);

        target.Should().Be(Path.Combine("dst", folder, name[..1], name));
    }

    [Fact]
    public async Task EveryMachine_ShouldKeepTheAgaAndOcsVersionsAsTwoGames()
    {
        var items = await SimulateAsync(CreateConfig(AmigaTarget.Any));

        items[ThemeParkAga].Status.Should().Be(FileStatus.Valid);
        items[ThemeParkOcs].Status.Should().Be(FileStatus.Valid);
        items[ThemeParkAga].Is1G1RWinner.Should().BeTrue();
        items[ThemeParkOcs].Is1G1RWinner.Should().BeTrue();
        items.Values.Should().NotContain(it => it.EliminatedBy == TiebreakCriterion.AmigaChipset);
    }

    [Fact]
    public async Task AgaMachine_ShouldKeepTheAgaVersionOfAGame()
    {
        var items = await SimulateAsync(CreateConfig(AmigaTarget.Aga));

        items[ThemeParkAga].Status.Should().Be(FileStatus.Valid);
        items[ThemeParkAga].Is1G1RWinner.Should().BeTrue();
        items[ThemeParkOcs].Status.Should().Be(FileStatus.Excluded);
        items[ThemeParkOcs].EliminatedBy.Should().Be(TiebreakCriterion.AmigaChipset);
        _services.I18n.Format(items[ThemeParkOcs].EliminationDetail!)
            .Should().Contain("version AGA préférée à la version OCS / ECS (Amiga AGA)");

        // The AGA version first, then the fix for the AGA machines, then the original
        items[JamesPondAga].Is1G1RWinner.Should().BeTrue();
        items[JamesPondFix].Status.Should().Be(FileStatus.Excluded);
        items[JamesPondOcs].Status.Should().Be(FileStatus.Excluded);

        // A game with a single version stays, whatever its chipset
        items[AlienBreedBoth].Is1G1RWinner.Should().BeTrue();
        items[BansheeAga].Is1G1RWinner.Should().BeTrue();
    }

    [Fact]
    public async Task AgaMachine_ShouldPreferTheFixForAgaToTheOriginal()
    {
        var items = await SimulateAsync(CreateConfig(AmigaTarget.Aga, files: [JamesPondOcs, JamesPondFix]));

        items[JamesPondFix].Is1G1RWinner.Should().BeTrue();
        items[JamesPondOcs].EliminatedBy.Should().Be(TiebreakCriterion.AmigaChipset);
        _services.I18n.Format(items[JamesPondOcs].EliminationDetail!).Should().Contain("version corrigée pour AGA");
    }

    [Fact]
    public async Task AgaMachine_WithoutOneGameOneRom_ShouldKeepEveryVersion()
    {
        var items = await SimulateAsync(CreateConfig(AmigaTarget.Aga, oneGameOneRom: false));

        items.Values.Should().OnlyContain(it => it.Status == FileStatus.Valid);
    }

    [Fact]
    public async Task OcsMachine_ShouldExcludeTheAgaGamesAndKeepTheOthersApart()
    {
        var items = await SimulateAsync(CreateConfig(AmigaTarget.Ocs));

        items[ThemeParkAga].Status.Should().Be(FileStatus.Excluded);
        items[ThemeParkAga].StatusReason!.Key.Should().Be("reason.amiga_chipset_excluded");
        items[BansheeAga].Status.Should().Be(FileStatus.Excluded);
        items[PinballEcsAga].Status.Should().Be(FileStatus.Excluded);
        items[ThemeParkOcs].Is1G1RWinner.Should().BeTrue();
        items[AlienBreedBoth].Is1G1RWinner.Should().BeTrue();
    }

    [Theory]
    [InlineData(AmigaTarget.Any)]
    [InlineData(AmigaTarget.Ocs)]
    [InlineData(AmigaTarget.Aga)]
    public async Task InteractiveRecalculation_ShouldReproduceTheSimulationOfEachGame(AmigaTarget target)
    {
        var config = CreateConfig(target);
        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);
        var expected = result.Items.ToDictionary(it => it.DisplayName, it => (it.Status, it.Is1G1RWinner, it.EliminatedBy));

        foreach (var game in result.Items.GroupBy(it => _services.FilterService.SerializeCanonicalGameKey(it.Parsed)))
        {
            var gameItems = game.ToList();
            _services.Processor.RecalculateGameItems(gameItems, config);

            gameItems.Select(it => (it.Status, it.Is1G1RWinner, it.EliminatedBy)).Should().Equal(
                gameItems.Select(it => expected[it.DisplayName]),
                $"game '{game.Key}' must not change on recalculation");
        }
    }

    [Fact]
    public async Task ForcedWinner_ShouldBeKeptForTheMergedGame()
    {
        var config = CreateConfig(AmigaTarget.Aga);
        var merged = (await SimulateAsync(config))[ThemeParkOcs].Parsed;
        config.Filters.Manual1G1RWinners[_services.FilterService.SerializeCanonicalGameKey(merged)] = ThemeParkOcs;

        var items = await SimulateAsync(config);

        items[ThemeParkOcs].Is1G1RWinner.Should().BeTrue();
        items[ThemeParkAga].Status.Should().Be(FileStatus.Excluded);
    }

    [Theory]
    [InlineData(AmigaTarget.Any, 7)]
    [InlineData(AmigaTarget.Aga, 5)]
    [InlineData(AmigaTarget.Ocs, 3)]
    public async Task CatalogDat_ShouldKeepTheGamesASimulationOfTheCompleteCollectionKeeps(AmigaTarget target, int expectedGames)
    {
        var games = string.Concat(AllFiles.Select((name, i) =>
            $"<game name=\"{Path.GetFileNameWithoutExtension(name)}\"><rom name=\"{name}\" size=\"{name.Length}\" crc=\"{i + 1:x8}\"/></game>\n"));
        var datPath = _temp.WriteText("amiga.dat",
            "<?xml version=\"1.0\"?>\n<datafile>\n<header><name>Commodore Amiga - Games - [ADF] (TOSEC)</name></header>\n"
            + games + "</datafile>\n");
        var config = CreateConfig(target);
        config.DatFile = datPath;

        var catalog = await _services.Processor.SelectFromCatalogAsync(config);

        catalog.KeptCount.Should().Be(expectedGames);
    }
}
