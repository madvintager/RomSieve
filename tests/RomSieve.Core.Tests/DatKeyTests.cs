using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// The DAT in the game keys (<see cref="ParsedRomName.DatKey"/>): set for a lot of several DATs only, it separates the
/// games, editions and releases of two DATs, and leaves the keys of a sort with one DAT exactly what they were.
/// </summary>
public sealed class DatKeyTests
{
    private const string GameBoy = "Nintendo - Game Boy";
    private const string Nes = "Nintendo - Nintendo Entertainment System";

    private readonly CoreTestServices _services = new();
    private RomFilterService Keys => _services.FilterService;

    private ParsedRomName Tosec(string name, string? datKey = null) => _services.TosecParser.ParseFilename(name) with { DatKey = datKey };

    private ParsedRomName NoIntro(string name, string? datKey = null) =>
        _services.NoIntroParser.ParseRomFilename(name, convention: NamingConvention.NoIntro) with { DatKey = datKey };

    private static ProcessItem Item(ParsedRomName parsed) => new()
    {
        SourcePath = parsed.RawFilename,
        FileSize = 1,
        Parsed = parsed,
        Status = FileStatus.Valid
    };

    private static SortConfig Config(FilterConfig filters) => new()
    {
        SourceDir = "source",
        DestinationDir = "destination",
        Filters = filters
    };

    /// <summary>
    /// The serialized keys of today, written out: the manual 1G1R winners, the partial sort units and the grouped view
    /// of the table of a sort with one DAT designate the same games as before.
    /// </summary>
    [Theory]
    [InlineData("Lemmings v1.1 (1991)(Psygnosis)(DE)(Disk 1 of 2)[cr NBC].adf", "lemmings::")]
    [InlineData("Legend of Zelda, The (1986)(Nintendo).nes", "the legend of zelda::")]
    [InlineData("Turrican II - The Final Fight (1991)(Rainbow Arts)(A500).adf", "turrican 2 the final fight::a500")]
    public void SerializedKey_WithoutDat_ShouldStayTheKeyOfToday(string name, string expected)
    {
        Keys.SerializeCanonicalGameKey(Tosec(name)).Should().Be(expected);
        Keys.SerializeCanonicalGameKey(Tosec(name, datKey: string.Empty)).Should().Be(expected, "an empty DAT is no DAT");
    }

    [Fact]
    public void SerializedKey_WithoutDat_ShouldStayTheKeyOfTodayForTheGroupsAndTheClones()
    {
        var mameClone = new ParsedRomName { RawFilename = "tetrisa.zip", Title = "Tetris", ParentTitle = "Tetris (set 1)", GameGroup = "Tetris" };
        Keys.SerializeCanonicalGameKey(mameClone).Should().Be("tetris::@tetris");

        var noIntroClone = NoIntro("Tetris (Europe) (Rev 1).gb") with { CloneOf = "Tetris (World) (Rev 1)" };
        Keys.SerializeCanonicalGameKey(noIntroClone).Should().Be("tetris::");
    }

    /// <summary>
    /// The edition and release keys without DAT are the 11 edition components of today; the DAT only adds a last segment
    /// to the edition key, which the release key starts with.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditionAndReleaseKeys_ShouldOnlyGainTheDatSegment(bool smartMultidisk)
    {
        var name = "Lemmings v1.1 (1991)(Psygnosis)(DE)(Disk 1 of 2)[cr NBC].adf";
        var withoutDat = Keys.GetEditionKey(Tosec(name), smartMultidisk);
        var withDat = Keys.GetEditionKey(Tosec(name, datKey: "Commodore Amiga - Games - [ADF]"), smartMultidisk);

        withoutDat.Split('\u001F').Should().HaveCount(11);
        Keys.GetEditionKey(Tosec(name, datKey: string.Empty), smartMultidisk).Should().Be(withoutDat);
        withDat.Should().Be(withoutDat + "\u001Fcommodore amiga - games - [adf]");

        Keys.GetReleaseKey(Tosec(name), smartMultidisk).Should().StartWith(withoutDat + "\u001F");
        Keys.GetReleaseKey(Tosec(name, datKey: "Commodore Amiga - Games - [ADF]"), smartMultidisk).Should().StartWith(withDat + "\u001F");
    }

    [Fact]
    public void Keys_ShouldSeparateTheSameTitleInTwoDats()
    {
        var gameBoy = NoIntro("Tetris (World) (Rev 1).gb", GameBoy);
        var nes = NoIntro("Tetris (World) (Rev 1).gb", Nes);

        Keys.GetCanonicalGameKey(gameBoy).Should().NotBe(Keys.GetCanonicalGameKey(nes));
        Keys.GetCanonicalGameKey(gameBoy).NormalizedTitle.Should().Be(Keys.GetCanonicalGameKey(nes).NormalizedTitle);
        Keys.SerializeCanonicalGameKey(gameBoy).Should().NotBe(Keys.SerializeCanonicalGameKey(nes));
        Keys.GetEditionKey(gameBoy).Should().NotBe(Keys.GetEditionKey(nes));
        Keys.GetReleaseKey(gameBoy).Should().NotBe(Keys.GetReleaseKey(nes));
        Keys.GetReleaseKey(gameBoy, smartMultidisk: true).Should().NotBe(Keys.GetReleaseKey(nes, smartMultidisk: true));
    }

    [Fact]
    public void Keys_ShouldIgnoreTheCaseOfTheDatName()
    {
        var upper = NoIntro("Tetris (World) (Rev 1).gb", GameBoy.ToUpperInvariant());
        var lower = NoIntro("Tetris (Europe).gb", GameBoy.ToLowerInvariant());

        Keys.SerializeCanonicalGameKey(upper).Should().Be(Keys.SerializeCanonicalGameKey(lower));
        Keys.SerializeCanonicalGameKey(upper).Should().Be("tetris::\u001Fnintendo - game boy");
    }

    /// <summary>The DAT segment follows the MAME group and survives the merge of the Amiga chipsets.</summary>
    [Fact]
    public void Keys_ShouldKeepTheDatAfterTheGroupAndTheAmigaMerge()
    {
        var grouped = new ParsedRomName { RawFilename = "tetris.zip", Title = "Tetris", GameGroup = "tetris", DatKey = "Lot" };
        Keys.SerializeCanonicalGameKey(grouped).Should().Be("tetris::@tetris\u001Flot");

        var aga = Tosec("Gloom (1995)(Black Magic)(AGA).adf", "Amiga AGA") with { MergesAmigaChipsets = true };
        var ocs = Tosec("Gloom (1995)(Black Magic)(OCS).adf", "Amiga OCS") with { MergesAmigaChipsets = true };
        Keys.SerializeCanonicalGameKey(aga).Should().Be("gloom::\u001Famiga aga");
        Keys.SerializeCanonicalGameKey(aga).Should().NotBe(Keys.SerializeCanonicalGameKey(ocs));
    }

    /// <summary>
    /// The 1G1R never compares the same title of two DATs: each keeps its release, whereas two releases of one DAT give
    /// one winner.
    /// </summary>
    [Theory]
    [InlineData(GameBoy, Nes, 2)]
    [InlineData(GameBoy, GameBoy, 1)]
    [InlineData(null, null, 1)]
    public void OneGameOneRom_ShouldKeepOneWinnerPerDat(string? firstDat, string? secondDat, int expectedWinners)
    {
        var config = Config(new FilterConfig { OneGameOneRom = true, PriorityLanguages = ["en"] });
        List<ProcessItem> items = [Item(NoIntro("Tetris (USA).gb", firstDat)), Item(NoIntro("Tetris (Europe).gb", secondDat))];

        new ReleaseSelector(config, Keys, _services.I18n).Select(items, datCatalog: null, new Dictionary<string, HashSet<string>>());

        items.Count(it => it.Status == FileStatus.Valid).Should().Be(expectedWinners);
    }

    /// <summary>A manual 1G1R winner, given by the serialized key with its DAT, only decides the game of its DAT.</summary>
    [Fact]
    public void ManualWinner_ShouldOnlyDecideTheGameOfItsDat()
    {
        var gameBoyUsa = Item(NoIntro("Tetris (USA).gb", GameBoy));
        var gameBoyEurope = Item(NoIntro("Tetris (Europe).gb", GameBoy));
        var nesUsa = Item(NoIntro("Tetris (USA).gb", Nes));
        var nesEurope = Item(NoIntro("Tetris (Europe).gb", Nes));
        var winners = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Keys.SerializeCanonicalGameKey(gameBoyEurope.Parsed)] = gameBoyEurope.Parsed.RawFilename
        };
        var config = Config(new FilterConfig { OneGameOneRom = true, PriorityLanguages = ["en"], PriorityCountries = ["US"], Manual1G1RWinners = winners });
        List<ProcessItem> items = [gameBoyUsa, gameBoyEurope, nesUsa, nesEurope];

        new ReleaseSelector(config, Keys, _services.I18n).Select(items, datCatalog: null, new Dictionary<string, HashSet<string>>());

        items.Where(it => it.Status == FileStatus.Valid).Should().BeEquivalentTo([gameBoyEurope, nesUsa]);
    }

    [Theory]
    [InlineData("DAT A", "DAT B", false)]
    [InlineData("DAT A", "dat a", true)]
    [InlineData(null, null, true)]
    public void MultiDisk_ShouldNeverMakeASetOfTheDisksOfTwoDats(string? firstDat, string? secondDat, bool expectedComplete)
    {
        var config = Config(new FilterConfig { ExcludeModifications = false });
        List<ProcessItem> items =
        [
            Item(Tosec("Zak (1988)(Lucas)(Disk 1 of 2).adf", firstDat)),
            Item(Tosec("Zak (1988)(Lucas)(Disk 2 of 2).adf", secondDat))
        ];

        new MultiDiskEvaluator(config, Keys, _services.I18n).Evaluate(items, datCatalog: null, new Dictionary<string, HashSet<string>>());

        items.Should().OnlyContain(it => it.IsMultidiskComplete == expectedComplete);
    }

    [Theory]
    [InlineData("DAT A", "DAT B", false)]
    [InlineData("DAT A", "DAT A", true)]
    [InlineData(null, null, true)]
    public void SmartMultiDisk_ShouldNeverBorrowTheCleanDiskOfAnotherDat(string? crackDat, string? cleanDat, bool expectedBorrowed)
    {
        var config = Config(new FilterConfig { ExcludeModifications = false, SmartMultidiskGrouping = true });
        List<ProcessItem> items =
        [
            Item(Tosec("Zak (1988)(Lucas)(Disk 1 of 2)[cr X].adf", crackDat)),
            Item(Tosec("Zak (1988)(Lucas)(Disk 2 of 2).adf", cleanDat))
        ];

        var result = new MultiDiskEvaluator(config, Keys, _services.I18n).Evaluate(items, datCatalog: null, new Dictionary<string, HashSet<string>>());

        result.BorrowedCleanDisks.Should().HaveCount(expectedBorrowed ? 1 : 0);
        items[0].IsMultidiskComplete.Should().Be(expectedBorrowed);
    }
}
