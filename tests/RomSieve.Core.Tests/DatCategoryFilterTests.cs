using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Text;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Categories of the No-Intro and Redump DATs (<c>&lt;category&gt;</c>, issue #7): read and written with the DATs, a
/// release excluded as soon as one of its categories is (<see cref="FilterConfig.NoIntroExcludedDatCategories"/>), the
/// name telling the category of a release its DAT gives none (an enhancement chip is an add-on), and the BIOS excluded by
/// their name whatever their category (<see cref="FilterConfig.NoIntroExcludeBios"/>). The Miss List does not change.
/// </summary>
public sealed class DatCategoryFilterTests : IDisposable
{
    private const string Cx4 = "CX4 (World) (Enhancement Chip).bin";
    private const string Dsp1 = "DSP1 (World) (Enhancement Chip).bin";
    private const string Dsp2 = "DSP2 (World) (Enhancement Chip).bin";
    private const string St010 = "ST010 (Japan, USA) (Enhancement Chip).bin";
    private const string MarioWorld = "Super Mario World (USA).sfc";
    private const string MarioPaint = "Mario Paint (Japan, USA) (En).sfc";
    private const string Bios = "[BIOS] Test Boot ROM (Japan).sfc";

    private static readonly byte[] Cx4Data = Encoding.ASCII.GetBytes("CX4 CHIP");
    private static readonly byte[] Dsp2Data = Encoding.ASCII.GetBytes("DSP2 CHIP");
    private static readonly byte[] St010Data = Encoding.ASCII.GetBytes("ST010 CHIP");
    private static readonly byte[] MarioWorldData = Encoding.ASCII.GetBytes("SUPER MARIO WORLD");
    private static readonly byte[] MarioPaintData = Encoding.ASCII.GetBytes("MARIO PAINT");
    private static readonly byte[] BiosData = Encoding.ASCII.GetBytes("BOOT ROM");

    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");

    public DatCategoryFilterTests()
    {
        _temp.WriteBytes(Path.Combine("src", Cx4), Cx4Data);
        _temp.WriteBytes(Path.Combine("src", St010), St010Data);
        _temp.WriteBytes(Path.Combine("src", MarioWorld), MarioWorldData);
        _temp.WriteBytes(Path.Combine("src", MarioPaint), MarioPaintData);
        _temp.WriteBytes(Path.Combine("src", Bios), BiosData);
        // An enhancement chip the DAT does not know: its name alone tells it is an add-on
        _temp.WriteText(Path.Combine("src", Dsp1), "DSP1 CHIP");
    }

    public void Dispose() => _temp.Dispose();

    private static string Crc(byte[] data) => Crc32.HashToUInt32(data).ToString("x8", CultureInfo.InvariantCulture);

    private static string Game(string rom, byte[] data, params string[] categories)
    {
        var name = Path.GetFileNameWithoutExtension(rom);
        var tags = string.Concat(categories.Select(c => $"<category>{c}</category>"));
        return $"""<game name="{name}">{tags}<description>{name}</description><rom name="{rom}" size="{data.Length}" crc="{Crc(data)}"/></game>""";
    }

    // ST010 has no category, as in an older DAT: only its name tells it is an add-on
    private static string SnesDat(string name = "Nintendo - Super Nintendo Entertainment System") => $"""
        <?xml version="1.0"?>
        <datafile>
          <header><name>{name}</name><description>{name}</description><homepage>No-Intro</homepage></header>
          {Game(Cx4, Cx4Data, "Add-Ons")}
          {Game(Dsp2, Dsp2Data, "Add-Ons")}
          {Game(St010, St010Data)}
          {Game(MarioWorld, MarioWorldData, "Games")}
          {Game(MarioPaint, MarioPaintData, "Applications")}
          {Game(Bios, BiosData, "Games")}
        </datafile>
        """;

    private SortConfig Config(Action<FilterConfig>? filters = null, string? datFile = null)
    {
        var config = new SortConfig
        {
            SourceDir = _temp.GetFilePath("src"),
            DestinationDir = _temp.GetFilePath("dst"),
            DatFile = datFile ?? _temp.WriteText("snes.dat", SnesDat()),
            NamingConvention = NamingConvention.NoIntro,
            Pattern = string.Empty
        };
        config.Filters.NoIntroExcludedDatCategories = ["add-ons"];
        filters?.Invoke(config.Filters);
        return config;
    }

    private static ProcessItem Item(SortExecutionResult result, string name) => result.Items.Single(it => it.DisplayName == name);

    // =========================================================================
    // DATs
    // =========================================================================

    [Fact]
    public void LogiqxDat_ShouldReadEveryCategoryOfAGame_AndWriteThemBack()
    {
        var catalog = _services.DatService.ParseLogiqxXml("""
            <?xml version="1.0"?>
            <datafile>
              <header><name>SNES</name></header>
              <game name="Aladdin (USA) (Beta)"><category>Games</category><category> Preproduction </category><description>Aladdin</description>
                <rom name="Aladdin (USA) (Beta).sfc" size="4" crc="12345678"/></game>
              <game name="Old (USA)"><description>Old</description><rom name="Old (USA).sfc" size="4" crc="87654321"/></game>
            </datafile>
            """);

        catalog.GameByName["Aladdin (USA) (Beta)"].Categories.Should().Equal("Games", "Preproduction");
        catalog.GameByName["Old (USA)"].Categories.Should().BeEmpty();
        DatCategory.CountGames(catalog).Should().BeEquivalentTo(new Dictionary<string, int> { ["Games"] = 1, ["Preproduction"] = 1 });

        var xml = catalog.ToLogiqxXml();
        xml.Should().Contain("<category>Games</category>");
        _services.DatService.ParseLogiqxXml(xml).GameByName["Aladdin (USA) (Beta)"].Categories.Should().Equal("Games", "Preproduction");
        _services.DatService.ParseClrMameProText(catalog.ToClrMamePro()).GameByName["Aladdin (USA) (Beta)"].Categories
            .Should().Equal("Games", "Preproduction");
        DatCatalog.CopyGame(catalog.GameByName["Aladdin (USA) (Beta)"], []).Categories.Should().Equal("Games", "Preproduction");
    }

    [Fact]
    public void ClrMameProDat_ShouldReadTheCategories()
    {
        var catalog = _services.DatService.ParseClrMameProText("""
            clrmamepro ( name "SNES" )
            game ( name "CX4 (World) (Enhancement Chip)" category "Add-Ons" description "CX4"
              rom ( name "CX4 (World) (Enhancement Chip).bin" size 4 crc 12345678 ) )
            """);

        catalog.GameByName["CX4 (World) (Enhancement Chip)"].Categories.Should().Equal("Add-Ons");
    }

    // =========================================================================
    // Filter
    // =========================================================================

    private (bool IsKept, string? Reason) Evaluate(ParsedRomName parsed, Action<FilterConfig> configure)
    {
        var config = new FilterConfig();
        configure(config);
        var (isKept, reason) = _services.FilterService.EvaluateFilter(parsed, config);
        return (isKept, reason is null ? null : _services.I18n.Format(reason));
    }

    [Fact]
    public void Release_ShouldBeExcludedAsSoonAsOneOfItsCategoriesIs_InAnyCase()
    {
        var beta = _services.NoIntroParser.ParseFilename("Aladdin (USA) (Beta).sfc") with { DatCategories = ["Games", "Preproduction"] };

        Evaluate(beta, c => c.NoIntroExcludedDatCategories = ["preproduction"]).Should().Be((false, "Category Preproduction excluded"));
        Evaluate(beta, c => c.NoIntroExcludedDatCategories = ["Games"]).IsKept.Should().BeFalse();
        Evaluate(beta, c => c.NoIntroExcludedDatCategories = ["Add-Ons", "Demos"]).IsKept.Should().BeTrue();
        Evaluate(beta, _ => { }).IsKept.Should().BeTrue("nothing is excluded by default");
    }

    [Fact]
    public void ReleaseWithoutCategory_ShouldTakeTheOneItsNameTells_ButACategoryOfTheDatShouldWin()
    {
        var chip = _services.NoIntroParser.ParseFilename(Cx4);

        Evaluate(chip, c => c.NoIntroExcludedDatCategories = [DatCategory.AddOns])
            .Should().Be((false, "Category Add-Ons excluded (from (Enhancement Chip))"));
        Evaluate(chip with { DatCategories = ["Games"] }, c => c.NoIntroExcludedDatCategories = [DatCategory.AddOns])
            .IsKept.Should().BeTrue("the DAT says what the release is, whatever its name");
        Evaluate(_services.NoIntroParser.ParseFilename(MarioWorld), c => c.NoIntroExcludedDatCategories = [.. DatCategory.Known])
            .IsKept.Should().BeTrue("a release without category, whose name tells none, is never excluded by category");
    }

    [Fact]
    public void BiosOption_ShouldExcludeTheBiosByTheirName_WhateverTheirCategory()
    {
        var bios = _services.NoIntroParser.ParseFilename("[BIOS] Hang On (USA, Europe) (v3.4).sms") with { DatCategories = ["Games"] };

        Evaluate(bios, c => c.NoIntroExcludeBios = true).Should().Be((false, "BIOS excluded"));
        Evaluate(bios, c => c.NoIntroExcludedDatCategories = ["Miscellaneous"]).IsKept.Should().BeTrue();
        Evaluate(bios, _ => { }).IsKept.Should().BeTrue();
        Evaluate(_services.NoIntroParser.ParseFilename(MarioWorld), c => c.NoIntroExcludeBios = true).IsKept.Should().BeTrue();
    }

    [Fact]
    public void Options_ShouldNotTouchTheMameSetsTheSoftwareOfTheListsNorTheTosecReleases()
    {
        void Exclude(FilterConfig c)
        {
            c.NoIntroExcludedDatCategories = [DatCategory.AddOns];
            c.NoIntroExcludeBios = true;
        }

        var chip = _services.NoIntroParser.ParseFilename("[BIOS] CX4 (World) (Enhancement Chip).bin") with { DatCategories = [DatCategory.AddOns] };
        Evaluate(chip with { Convention = NamingConvention.Mame }, Exclude).IsKept.Should().BeTrue();
        Evaluate(chip with { Convention = NamingConvention.MameSoftwareList }, Exclude).IsKept.Should().BeTrue();
        Evaluate(_services.TosecParser.ParseFilename("Game (1991)(Ocean).adf") with { DatCategories = [DatCategory.AddOns] }, Exclude)
            .IsKept.Should().BeTrue();
    }

    // =========================================================================
    // Sort
    // =========================================================================

    [Fact]
    public async Task Sort_ShouldExcludeTheAddOnsOfTheDat_AndKeepTheOtherCategories()
    {
        var result = await _services.Processor.ExecuteAsync(Config(), isDryRun: true);

        var cx4 = Item(result, Cx4);
        cx4.Status.Should().Be(FileStatus.Excluded);
        cx4.StatusReason!.Key.Should().Be("reason.dat_category_excluded");
        _services.I18n.Format(cx4.StatusReason).Should().Be("Category Add-Ons excluded");
        new LocalizationService("fr").Format(cx4.StatusReason).Should().Be("Catégorie Add-Ons exclue");

        _services.I18n.Format(Item(result, St010).StatusReason).Should().Be("Category Add-Ons excluded (from (Enhancement Chip))");
        Item(result, St010).Status.Should().Be(FileStatus.Excluded);
        Item(result, MarioWorld).Status.Should().Be(FileStatus.Valid);
        Item(result, MarioPaint).Status.Should().Be(FileStatus.Valid, "the applications are kept");
        Item(result, Bios).Status.Should().Be(FileStatus.Valid, "the BIOS are kept by default");
    }

    [Fact]
    public async Task FileMissingFromTheDat_ShouldBeExcludedByTheCategoryItsNameTells()
    {
        var result = await _services.Processor.ExecuteAsync(Config(), isDryRun: true);

        var dsp1 = Item(result, Dsp1);
        dsp1.Status.Should().Be(FileStatus.Excluded);
        dsp1.IdentificationFailure.Should().Be(IdentificationFailure.NotInDat);
        _services.I18n.Format(dsp1.StatusReason).Should().Be("Category Add-Ons excluded (from (Enhancement Chip)) (file missing from the DAT)");
    }

    [Fact]
    public async Task MissList_ShouldNotDependOnTheCategories()
    {
        var excluding = await _services.Processor.ExecuteAsync(Config(), isDryRun: true);
        var keeping = await _services.Processor.ExecuteAsync(Config(c => c.NoIntroExcludedDatCategories = []), isDryRun: true);

        excluding.MissingRoms.Select(r => r.Name).Should().Equal(Dsp2);
        keeping.MissingRoms.Select(r => r.Name).Should().Equal(Dsp2);
        excluding.Stats.DatCompletionPct.Should().Be(keeping.Stats.DatCompletionPct);
        Item(keeping, Cx4).Status.Should().Be(FileStatus.Valid);
    }

    [Fact]
    public async Task BiosOption_ShouldExcludeTheBiosOfTheSource()
    {
        var result = await _services.Processor.ExecuteAsync(Config(c => c.NoIntroExcludeBios = true), isDryRun: true);

        Item(result, Bios).Status.Should().Be(FileStatus.Excluded);
        Item(result, Bios).StatusReason!.Key.Should().Be("reason.bios_excluded");
        Item(result, MarioWorld).Status.Should().Be(FileStatus.Valid);
    }

    [Fact]
    public async Task Lot_ShouldFilterEachFileByTheCategoryOfItsOwnDat()
    {
        // The same chip is an add-on in one DAT and a game in the other: each DAT says what its own game is
        _temp.WriteText(Path.Combine("dats", "snes.dat"), SnesDat());
        _temp.WriteText(Path.Combine("dats", "other.dat"), $"""
            <?xml version="1.0"?>
            <datafile>
              <header><name>Other</name><description>Other</description><homepage>No-Intro</homepage></header>
              {Game("Other Chip (World) (Enhancement Chip).bin", Encoding.ASCII.GetBytes("OTHER CHIP"), "Games")}
            </datafile>
            """);
        _temp.WriteText(Path.Combine("src", "Other Chip (World) (Enhancement Chip).bin"), "OTHER CHIP");

        var result = await _services.Processor.ExecuteAsync(Config(datFile: _temp.GetFilePath("dats")), isDryRun: true);

        Item(result, Cx4).Status.Should().Be(FileStatus.Excluded);
        Item(result, "Other Chip (World) (Enhancement Chip).bin").Status.Should().Be(FileStatus.Valid);
    }

    [Fact]
    public async Task FilteredDatOfTheCatalog_ShouldLeaveOutTheGamesOfTheExcludedCategories_AndKeepTheCategoriesOfTheOthers()
    {
        var selection = await _services.Processor.SelectFromCatalogAsync(Config());

        selection.FilteredDat.Games.Select(g => g.Name).Should().BeEquivalentTo(
            "Super Mario World (USA)", "Mario Paint (Japan, USA) (En)", "[BIOS] Test Boot ROM (Japan)");
        selection.FilteredDat.GameByName["Mario Paint (Japan, USA) (En)"].Categories.Should().Equal("Applications");
        selection.Items.Single(it => it.DatGameName == "CX4 (World) (Enhancement Chip)").StatusReason!.Key
            .Should().Be("reason.dat_category_excluded");
    }
}
