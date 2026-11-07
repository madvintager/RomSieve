using System.IO.Hashing;
using System.Text;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// A lot of DATs in the interface: a folder of No-Intro, Redump or TOSEC DATs is read from the headers of its DATs, shown
/// as a list to check (with a search) bound to <c>excluded_dats</c>, its summary counting its DATs by convention; the
/// subfolders on demand; the convention of most DATs checked rules the settings steps; the output step offers a folder
/// per DAT and the mirror, the pattern its <c>{dat}</c> and a preview in the folder of a DAT; the table names the DAT
/// of each file (column, filter, search, grouped view, detail panel), the banner the completion of each DAT.
/// </summary>
public sealed class DatLotViewModelTests : IDisposable
{
    private const string GameBoy = "Nintendo - Game Boy";
    private const string GameBoyColor = "Nintendo - Game Boy Color";
    private const string Amiga = "Commodore Amiga - Games - [ADF]";

    private static readonly byte[] TetrisGb = Encoding.ASCII.GetBytes("TETRIS GB");
    private static readonly byte[] TetrisGbc = Encoding.ASCII.GetBytes("TETRIS GBC");
    private static readonly byte[] Lemmings = Encoding.ASCII.GetBytes("LEMMINGS DISK");

    private readonly TempDirectoryFixture _tmp = new();
    private readonly string _dats;

    public DatLotViewModelTests()
    {
        _dats = _tmp.CreateSubdirectory("dats");
        WriteDat("Nintendo - Game Boy (20230414).dat", GameBoy, "No-Intro", ("Tetris (World)", "Tetris (World).gb", TetrisGb));
        WriteDat("Nintendo - Game Boy Color (20230501).dat", GameBoyColor, "No-Intro", ("Tetris (World)", "Tetris (World).gbc", TetrisGbc));
        WriteDat(Path.Combine("TOSEC", "Commodore Amiga - Games - [ADF] (TOSEC-v2025).dat"), Amiga, "TOSEC",
            ("Lemmings (1991)(Psygnosis)", "Lemmings (1991)(Psygnosis).adf", Lemmings));
    }

    public void Dispose() => _tmp.Dispose();

    private static string Crc(byte[] data) => Crc32.HashToUInt32(data).ToString("x8");

    private void WriteDat(string relativePath, string name, string homepage, params (string Game, string Rom, byte[] Data)[] games) =>
        _tmp.WriteText(Path.Combine("dats", relativePath), $"""
            <?xml version="1.0"?>
            <datafile>
            	<header><name>{name}</name><description>{name}</description><homepage>{homepage}</homepage></header>
            {string.Concat(games.Select(g => $"""<game name="{g.Game}"><description>{g.Game}</description><rom name="{g.Rom}" size="{g.Data.Length}" crc="{Crc(g.Data)}"/></game>"""))}
            </datafile>
            """);

    private async Task<MainViewModel> CreateWithLotAsync(bool recursive = false, string config = "{}")
    {
        var vm = TestMainViewModel.Create(new HeadlessDialogService(), _tmp.WriteText("config.json", config));
        vm.Directories.DatRecursive = recursive;
        vm.Directories.DatFilePath = _dats;
        await vm.Directories.DatLoadTask;
        return vm;
    }

    // =========================================================================
    // Collection step
    // =========================================================================

    [Fact]
    public async Task FolderOfDats_ShouldBeShownAsALotToCheck()
    {
        var vm = await CreateWithLotAsync();
        var lot = vm.Directories.DatLot;

        lot.IsLot.Should().BeTrue();
        vm.Directories.IsDatFolder.Should().BeTrue();
        lot.Entries.Select(e => (e.Name, e.ConventionLabel, e.IsIncluded)).Should().Equal(
            (GameBoy, "No-Intro", true), (GameBoyColor, "No-Intro", true));
        vm.Directories.DatInfoText.Should().Be("Lot de 2 DAT (2 No-Intro), 2 cochés");
        lot.CountText.Should().Be("2 DAT cochés sur 2");
        vm.Directories.EffectiveConvention.Should().Be(NamingConvention.NoIntro);
        vm.Directories.DatHeaderName.Should().Be("dats", "a folder is named after itself");
    }

    [Fact]
    public async Task Subfolders_ShouldAddTheirDatsToTheLot()
    {
        var vm = await CreateWithLotAsync();

        vm.Directories.DatRecursive = true;
        await vm.Directories.DatLoadTask;

        vm.Directories.DatLot.Entries.Select(e => e.Name).Should().Equal(GameBoy, GameBoyColor, Amiga);
        vm.Directories.DatLot.Entries[2].RelativePath.Should().Be("TOSEC/Commodore Amiga - Games - [ADF] (TOSEC-v2025).dat");
        vm.Directories.DatInfoText.Should().Be("Lot de 3 DAT (2 No-Intro, 1 TOSEC), 3 cochés");
        vm.BuildConfigUnchecked().DatRecursive.Should().BeTrue();
    }

    [Fact]
    public async Task UncheckedDat_ShouldBeLeftOutOfTheSort_AndSaved()
    {
        var vm = await CreateWithLotAsync(recursive: true);

        vm.Directories.DatLot.Entries.Single(e => e.Name == GameBoyColor).IsIncluded = false;

        vm.Directories.ExcludedDats.Should().Equal(GameBoyColor);
        vm.BuildConfigUnchecked().ExcludedDats.Should().Equal(GameBoyColor);
        vm.Directories.DatInfoText.Should().EndWith("2 cochés");
        await vm.SaveConfigurationAsync();

        var reloaded = TestMainViewModel.Create(new HeadlessDialogService(), _tmp.GetFilePath("config.json"));
        await reloaded.Directories.DatLoadTask;
        reloaded.Directories.DatLot.Entries.Select(e => (e.Name, e.IsIncluded)).Should().Equal(
            (GameBoy, true), (GameBoyColor, false), (Amiga, true));
    }

    [Fact]
    public async Task Search_ShouldOnlyShowTheDatsItNames_AndUncheckThemAlone()
    {
        var vm = await CreateWithLotAsync(recursive: true);
        var lot = vm.Directories.DatLot;

        lot.SearchText = "color";
        lot.FilteredEntries.Select(e => e.Name).Should().Equal(GameBoyColor);
        lot.SearchText = "tosec";
        lot.FilteredEntries.Should().ContainSingle("the path and the convention are searched too").Which.Name.Should().Be(Amiga);

        lot.SearchText = "nintendo";
        lot.ExcludeShownCommand.Execute(null);
        vm.Directories.ExcludedDats.Should().Equal(GameBoy, GameBoyColor);
        vm.Directories.EffectiveConvention.Should().Be(NamingConvention.Tosec, "the convention of the DATs checked");

        lot.IncludeShownCommand.Execute(null);
        vm.Directories.ExcludedDats.Should().BeEmpty();
    }

    [Fact]
    public async Task MixedLot_ShouldKeepTheTosecVariables_WhenATosecDatIsChecked()
    {
        var vm = await CreateWithLotAsync(recursive: true);

        vm.Directories.EffectiveConvention.Should().Be(NamingConvention.NoIntro, "most of the DATs are No-Intro DATs");
        vm.Directories.AreTosecVariablesApplicable.Should().BeTrue("the Amiga DAT is a TOSEC DAT");
        vm.Directories.PatternVariables.Single(v => v.Tag == "{publisher}").IsApplicable.Should().BeTrue();

        vm.Directories.DatLot.Entries.Single(e => e.Name == Amiga).IsIncluded = false;
        vm.Directories.AreTosecVariablesApplicable.Should().BeFalse();
    }

    [Fact]
    public async Task FiltersPage_ShouldShowTheConventionOfTheLot_AndTheOtherOneOnDemand()
    {
        var vm = await CreateWithLotAsync(recursive: true);

        vm.Filters.IsNoIntroShown.Should().BeTrue("most of the DATs checked are No-Intro DATs");
        vm.Filters.IsTosecShown = true;
        vm.Filters.IsTosecShown.Should().BeTrue("the TOSEC options of the Amiga DAT stay at hand");

        foreach (var entry in vm.Directories.DatLot.Entries.Where(e => e.Name != Amiga))
        {
            entry.IsIncluded = false;
        }

        vm.Directories.EffectiveConvention.Should().Be(NamingConvention.Tosec);
        vm.Filters.IsTosecShown.Should().BeTrue();
    }

    [Fact]
    public async Task DatFile_ShouldBeNoLot_AndGreyOutTheFolderOptions()
    {
        var vm = await CreateWithLotAsync();

        vm.Directories.DatFilePath = Path.Combine(_dats, "Nintendo - Game Boy (20230414).dat");
        await vm.Directories.DatLoadTask;

        vm.Directories.DatLot.IsLot.Should().BeFalse();
        vm.Directories.IsDatFolder.Should().BeFalse();
        vm.Directories.IsDatFolderMirrorEnabled.Should().BeFalse();
        vm.Directories.DatHeaderName.Should().Be(GameBoy);
        vm.Directories.PatternVariables.Single(v => v.Tag == "{dat}").IsApplicable.Should().BeTrue();

        vm.Directories.DatFilePath = string.Empty;
        vm.Directories.PatternVariables.Single(v => v.Tag == "{dat}").IsApplicable.Should().BeFalse();
    }

    [Fact]
    public async Task RefusedFolder_ShouldSayWhy_WithoutALot()
    {
        WriteDat("copy.dat", GameBoy, "No-Intro", ("Tetris (World)", "Tetris (World).gb", TetrisGb));

        var vm = await CreateWithLotAsync();

        vm.Directories.DatLot.IsLot.Should().BeFalse();
        vm.Directories.DatInfoTone.Should().Be("Error");
        vm.Directories.DatInfoText.Should().Contain(GameBoy).And.Contain("copy.dat");
    }

    [Fact]
    public async Task Mirror_ShouldOnlyBeOfferedWithAFolderPerDat()
    {
        var vm = await CreateWithLotAsync();

        vm.Directories.IsDatFolderMirrorEnabled.Should().BeTrue();
        vm.Directories.FolderPerDat = false;
        vm.Directories.IsDatFolderMirrorEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task PatternPreview_ShouldPutTheExamplesInTheFolderOfADatOfTheLot()
    {
        var vm = await CreateWithLotAsync(recursive: true);
        vm.Directories.Pattern = "{title}";
        vm.Directories.Preview.Refresh();

        vm.Directories.Preview.Examples.Should().NotBeEmpty();
        vm.Directories.Preview.Examples.Should().OnlyContain(e => e.TargetPath.Contains($"{GameBoy}{Path.DirectorySeparatorChar}", StringComparison.Ordinal),
            "the examples follow the convention of the lot, that of its first No-Intro DAT checked");

        vm.Directories.FolderPerDat = false;
        vm.Directories.Pattern = "{dat}";
        vm.Directories.Preview.Refresh();
        vm.Directories.Preview.Examples.Should().OnlyContain(e => e.TargetPath.Contains(GameBoy, StringComparison.Ordinal));
    }

    // =========================================================================
    // Results
    // =========================================================================

    private async Task<MainViewModel> SimulateLotAsync()
    {
        var vm = await CreateWithLotAsync(recursive: true);
        var source = _tmp.CreateSubdirectory("src");
        File.WriteAllBytes(Path.Combine(source, "tetris.gb"), TetrisGb);
        File.WriteAllBytes(Path.Combine(source, "tetris.gbc"), TetrisGbc);
        File.WriteAllBytes(Path.Combine(source, "lemmings.adf"), Lemmings);
        File.WriteAllText(Path.Combine(source, "junk.bin"), "junk");
        vm.Directories.SourceDir = source;
        vm.Directories.DestinationDir = _tmp.GetFilePath("dst");
        await vm.Execution.SimulateAsync();
        return vm;
    }

    private static PreviewItemViewModel Row(MainViewModel vm, string name) => vm.AllPreviewItems.Single(r => r.SourceName == name);

    [Fact]
    public async Task Table_ShouldNameTheDatOfEachFile_InItsColumnAndItsDetail()
    {
        var vm = await SimulateLotAsync();

        Row(vm, "tetris.gb")[PreviewTableViewModel.DatColumnIndex].Should().Be(GameBoy);
        Row(vm, "lemmings.adf").DatDisplay.Should().Be(Amiga);
        Row(vm, "junk.bin").DatDisplay.Should().BeEmpty();
        Row(vm, "tetris.gbc").DetailDatText.Should().Be(GameBoyColor);
        vm.PreviewTable.Columns[PreviewTableViewModel.DatColumnIndex].IsVisible.Should().BeFalse("hidden by default");
        vm.PreviewTable.PreviewColumnHeaders[PreviewTableViewModel.DatColumnIndex].Should().Be("DAT");
    }

    [Fact]
    public async Task Table_ShouldFilterAndSearchTheFilesOfADat()
    {
        var vm = await SimulateLotAsync();
        var table = vm.PreviewTable;

        table.HasDatFilter.Should().BeTrue();
        table.DatFilterOptions.Select(o => o.Label).Should().Equal("Tous les DAT", Amiga, GameBoy, GameBoyColor);
        table.SelectedDatFilter = table.DatFilterOptions.Single(o => o.Name == Amiga);
        vm.FilteredPreviewItems.Select(r => r.SourceName).Should().Equal("lemmings.adf");

        table.SelectedDatFilter = table.DatFilterOptions[0];
        vm.PreviewSearchText = "color";
        vm.FilteredPreviewItems.Should().ContainSingle("the name of its DAT holds the search").Which.SourceName.Should().Be("tetris.gbc");
    }

    [Fact]
    public async Task GroupedView_ShouldOrderTheGamesByDat()
    {
        var vm = await SimulateLotAsync();

        vm.PreviewTable.IsGroupedByGame = true;

        vm.PreviewTable.DisplayedRows.OfType<PreviewItemViewModel>().Select(r => r.DatDisplay)
            .Should().Equal(Amiga, GameBoy, GameBoyColor, string.Empty);
    }

    [Fact]
    public async Task Banner_ShouldDetailTheCompletionOfEachDat()
    {
        var vm = await SimulateLotAsync();

        vm.StatsBanner.DatStatsTooltip.Should().Contain($"{GameBoy} : 100.0 % (1/1), 1 jeux trouvés")
            .And.Contain(Amiga).And.Contain(GameBoyColor);
    }

    [Fact]
    public async Task SingleDat_ShouldNotOfferAFilterByDat()
    {
        var vm = TestMainViewModel.Create(new HeadlessDialogService(), _tmp.WriteText("config.json", "{}"));
        var source = _tmp.CreateSubdirectory("src_single");
        File.WriteAllBytes(Path.Combine(source, "tetris.gb"), TetrisGb);
        vm.Directories.DatFilePath = Path.Combine(_dats, "Nintendo - Game Boy (20230414).dat");
        await vm.Directories.DatLoadTask;
        vm.Directories.SourceDir = source;
        vm.Directories.DestinationDir = _tmp.GetFilePath("dst_single");

        await vm.Execution.SimulateAsync();

        vm.PreviewTable.HasDatFilter.Should().BeFalse();
        Row(vm, "tetris.gb").DatDisplay.Should().BeEmpty();
        vm.StatsBanner.DatStatsTooltip.Should().Be(vm.Localization.Tr("panel4.tooltip_dat_stats"));
    }
}
