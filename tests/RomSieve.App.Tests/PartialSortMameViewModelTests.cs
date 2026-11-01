using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using RomSieve.App;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// Partial sort of MAME games (<see cref="PartialSortViewModel"/>): the sets a checked game needs (its BIOS) are sorted
/// with it, and the table says so, in the sentence of the footer, the counter and the locked check box of their row.
/// </summary>
public sealed class PartialSortMameViewModelTests : IDisposable
{
    private static readonly byte[] Bios = Encoding.ASCII.GetBytes("NEO-GEO BIOS");
    private static readonly byte[] LastResort = Encoding.ASCII.GetBytes("LAST RESORT PROGRAM");
    private static readonly byte[] MetalSlug = Encoding.ASCII.GetBytes("METAL SLUG PROGRAM");
    private static readonly byte[] Tetris = Encoding.ASCII.GetBytes("ATARI TETRIS");

    private readonly TempDirectoryFixture _temp = new();
    private readonly HeadlessDialogService _dialogs = new();
    private readonly MainViewModel _vm;
    private readonly string _source;
    private readonly string _destination;

    public PartialSortMameViewModelTests()
    {
        var configPath = _temp.WriteText(Path.Combine("config", "config.json"), "{}");
        _source = _temp.CreateSubdirectory("src");
        _destination = Path.Combine(_temp.Path, "dst");

        // A merged collection: the games take the ROM of their BIOS from its set
        var dat = _temp.WriteText("mame.xml", $"""
            <?xml version="1.0"?>
            <mame build="0.289 (test)">
            	<machine name="neogeo" isbios="yes"><description>Neo-Geo MV-6F</description>{Rom("bios.rom", Bios)}</machine>
            	<machine name="lresort" romof="neogeo"><description>Last Resort</description><year>1992</year>{Rom("bios.rom", Bios, "bios.rom")}{Rom("lr.p1", LastResort)}</machine>
            	<machine name="mslug" romof="neogeo"><description>Metal Slug - Super Vehicle-001</description><year>1996</year>{Rom("bios.rom", Bios, "bios.rom")}{Rom("mslug.p1", MetalSlug)}</machine>
            	<machine name="atetris"><description>Tetris (set 1)</description><year>1988</year>{Rom("atetris.rom", Tetris)}</machine>
            </mame>
            """);
        Zip("neogeo.zip", ("bios.rom", Bios));
        Zip("lresort.zip", ("lr.p1", LastResort));
        Zip("mslug.zip", ("mslug.p1", MetalSlug));
        Zip("atetris.zip", ("atetris.rom", Tetris));

        _vm = TestMainViewModel.Create(_dialogs, configPath);
        _vm.Directories.SourceDir = _source;
        _vm.Directories.DestinationDir = _destination;
        _vm.Directories.DatFilePath = dat;
        _vm.Directories.Pattern = string.Empty;
        _vm.UiLocale = "en";
    }

    public void Dispose() => _temp.Dispose();

    private PartialSortViewModel PartialSort => _vm.PartialSort;

    private static string Rom(string name, byte[] data, string? merge = null) =>
        $"""<rom name="{name}"{(merge is null ? "" : $" merge=\"{merge}\"")} size="{data.Length}" crc="{Crc32.HashToUInt32(data):x8}"/>""";

    private void Zip(string name, params (string Member, byte[] Data)[] members)
    {
        using var zip = ZipFile.Open(Path.Combine(_source, name), ZipArchiveMode.Create);
        foreach (var (member, data) in members)
        {
            using var stream = zip.CreateEntry(member).Open();
            stream.Write(data);
        }
    }

    private PreviewItemViewModel Row(string fileName) =>
        _vm.PreviewTable.AllPreviewItems.Single(r => r.SourceName == fileName);

    // Only the given games checked, as after unchecking everything
    private async Task CheckOnlyAsync(params string[] fileNames)
    {
        await _vm.Execution.SimulateAsync();
        PartialSort.AreShownRowsChecked = false;
        PartialSort.SetChecked(fileNames.Select(Row).ToList(), true);
    }

    [Fact]
    public async Task SelectedBios_ShouldNameTheGamesThatUseIt()
    {
        await _vm.Execution.SimulateAsync();
        var table = _vm.PreviewTable;

        table.SelectedPreviewItem = Row("neogeo.zip");
        table.SelectedMameUsersText.Should().Be("2 machine(s), 2 of them in the source; 2 kept: lresort, mslug");

        // A game no machine uses, then no selection
        table.SelectedPreviewItem = Row("atetris.zip");
        table.SelectedMameUsersText.Should().BeEmpty();
        table.SelectedPreviewItem = null;
        table.SelectedMameUsersText.Should().BeEmpty();

        // A game gone from the source is no longer counted; the text follows the language
        File.Delete(Path.Combine(_source, "mslug.zip"));
        await _vm.Execution.SimulateAsync();
        table.SelectedPreviewItem = Row("neogeo.zip");
        _vm.UiLocale = "fr";
        table.SelectedMameUsersText.Should().Be("2 machine(s), dont 1 dans la source ; 1 retenue(s) : lresort");
    }

    [Fact]
    public async Task CheckedGame_ShouldShowTheBiosItNeedsAsCheckedAndLocked()
    {
        await CheckOnlyAsync("lresort.zip");

        var bios = Row("neogeo.zip");
        bios.IsSortChecked.Should().BeTrue("the BIOS is sorted with the game that needs it");
        bios.CanBeSortChecked.Should().BeFalse("unchecking it would not take it out of the sort");
        bios.SortCheckTooltip.Should().Be("Sorted with the set lresort, which needs it to run. Uncheck lresort to stop sorting this row.");
        Row("lresort.zip").CanBeSortChecked.Should().BeTrue();
        Row("lresort.zip").SortCheckTooltip.Should().Be(_vm.I18n["panel4.tooltip_row_check"]);
        Row("atetris.zip").IsSortChecked.Should().BeFalse();

        // Unchecking the game frees its BIOS
        PartialSort.SetChecked([Row("lresort.zip")], false);
        bios.IsSortChecked.Should().BeFalse();
        bios.CanBeSortChecked.Should().BeTrue();
        bios.SortCheckTooltip.Should().Be(_vm.I18n["panel4.tooltip_row_check"]);
    }

    [Fact]
    public async Task CheckedGame_ShouldNameTheBiosItNeedsInTheSentenceAndTheCounter()
    {
        await CheckOnlyAsync("lresort.zip");

        PartialSort.FooterSummaryText.Should().StartWith("Copy 1 game and the required set neogeo (2 files) (")
            .And.EndWith($"to {_destination}, a part of the simulation");
        PartialSort.CheckedCountText.Should().Be("1 game selected + 1 required set");
        PartialSort.CheckedCountTooltip.Should().EndWith("Required sets, sorted with the checked games that need them: neogeo");

        // The BIOS checked by the user is a game chosen like another
        PartialSort.SetChecked([Row("atetris.zip")], true);
        PartialSort.FooterSummaryText.Should().StartWith("Copy 2 games and the required set neogeo (3 files)");

        _vm.UiLocale = "fr";
        PartialSort.CheckedCountText.Should().Be("2 jeux sélectionnés + 1 set requis");
        PartialSort.FooterSummaryText.Should().StartWith("Copier 2 jeux et le set requis neogeo (3 fichiers)");
        Row("neogeo.zip").SortCheckTooltip.Should().StartWith("Trié avec le set lresort");
        Row("atetris.zip").SortCheckTooltip.Should().Be(_vm.I18n["panel4.tooltip_row_check"]);
    }

    [Fact]
    public async Task GamesSharingABios_ShouldCountItOnce()
    {
        await CheckOnlyAsync("lresort.zip", "mslug.zip");

        PartialSort.FooterSummaryText.Should().StartWith("Copy 2 games and the required set neogeo (3 files)");
        PartialSort.CheckedCountText.Should().Be("2 games selected + 1 required set");
        PartialSort.AreShownRowsChecked.Should().BeNull("Tetris is neither checked nor needed");
    }

    [Fact]
    public async Task BiosKeptOnlyBecauseGamesNeedIt_ShouldShowTheRequiredBadge()
    {
        await _vm.Execution.SimulateAsync();
        Row("neogeo.zip").IsRequiredSet.Should().BeFalse("the BIOS filter keeps it anyway");

        // The BIOS filter excludes it, the games keep it
        _vm.NoIntroFilters.ExcludeMameBios = true;
        await _vm.Execution.SimulateAsync();

        Row("neogeo.zip").IsRequiredSet.Should().BeTrue();
        Row("neogeo.zip").StatusReasonText.Should().Be("Kept: the sets lresort, mslug need it to run", "every kept set that needs it is named");
        Row("lresort.zip").IsRequiredSet.Should().BeFalse();
        Row("atetris.zip").IsRequiredSet.Should().BeFalse();
    }

    [Fact]
    public async Task CheckedGame_ShouldBeSortedWithItsBios()
    {
        await CheckOnlyAsync("lresort.zip");

        await PartialSort.StartCommand.ExecuteAsync(null);

        _dialogs.ShownMessages.Should().ContainSingle(m => m.Level == "CONFIRM")
            .Which.Message.Should().Contain("Copy 1 game and the required set neogeo (2 files)");
        Directory.EnumerateFiles(_destination)
            .Where(f => !DestinationGuard.IsGuardPath(f))
            .Select(Path.GetFileName)
            .Should().BeEquivalentTo("lresort.zip", "neogeo.zip");
    }

    [Fact]
    public async Task DatOfTheCollection_ShouldDescribeTheCheckedGameWithItsBios()
    {
        await CheckOnlyAsync("lresort.zip");
        var path = Path.Combine(_temp.Path, "collection.xml");
        _dialogs.NextPickedSaveFile = path;

        await _vm.ExportDatAsync();

        var machines = System.Xml.Linq.XDocument.Load(path).Root!.Elements()
            .Select(e => (string?)e.Attribute("name"))
            .Where(n => n is not null);
        machines.Should().BeEquivalentTo(["lresort", "neogeo"], "the sort of the selection writes these two sets");
    }

    /// <summary>
    /// Only lresort checked: under the Selection chip, then with every row shown again (Total), the checked games shown
    /// are still lresort, sorted with its BIOS; a BIOS shown alone is not a game chosen by itself.
    /// </summary>
    [Fact]
    public async Task SortDisplayed_ShouldStaySortableWhenEveryRowIsShownAgain()
    {
        await CheckOnlyAsync("lresort.zip");

        _vm.StatsBanner.ToggleChip(StatsBannerViewModel.SelectionFilterKey);
        _vm.PreviewTable.FilteredPreviewItems.Select(r => r.SourceName).Should().BeEquivalentTo("lresort.zip", "neogeo.zip");
        PartialSort.CanSortDisplayed.Should().BeTrue();

        _vm.StatsBanner.ToggleChip(StatsBannerViewModel.SelectionFilterKey);
        _vm.PreviewTable.FilteredPreviewItems.Count.Should().Be(_vm.PreviewTable.AllPreviewItems.Count, "every row is shown again");
        PartialSort.CanSortDisplayed.Should().BeTrue("lresort is still checked among the rows shown");
        PartialSort.DisplayedSummaryText.Should().StartWith("Copy 1 game and the required set neogeo (2 files)");
        PartialSort.DisplayedSelection!.Units.Select(u => u.Key).Should().HaveCount(2);

        _vm.PreviewTable.PreviewSearchText = "neogeo";
        PartialSort.CanSortDisplayed.Should().BeFalse("the BIOS comes with the games that need it, never alone");
    }

    [Fact]
    public async Task RequiredBios_ShouldSayApartFromItsStatusWhichCheckedGamesItIsSortedWith()
    {
        _vm.NoIntroFilters.ExcludeMameBios = true;
        await CheckOnlyAsync("mslug.zip");

        // The status describes the simulation, the selection line the partial sort
        var bios = Row("neogeo.zip");
        bios.StatusReasonText.Should().Be("Kept: the sets lresort, mslug need it to run");
        bios.SelectionText.Should().Be("Sorted with the set mslug, which needs it to run");
        bios.TooltipText.Should().EndWith("Selection: Sorted with the set mslug, which needs it to run");
        bios.SortCheckTooltip.Should().Be("Sorted with the set mslug, which needs it to run. Uncheck mslug to stop sorting this row.");
        Row("mslug.zip").SelectionText.Should().Be("Checked: it will be sorted");
        Row("lresort.zip").SelectionText.Should().Be("Unchecked: it will not be sorted");

        // Both games need it: both are named
        PartialSort.SetChecked([Row("lresort.zip")], true);
        bios.SelectionText.Should().Be("Sorted with the sets lresort, mslug, which need it to run");
        bios.SortCheckTooltip.Should().Be("Sorted with the sets lresort, mslug, which need it to run. Uncheck them to stop sorting this row.");

        _vm.UiLocale = "fr";
        bios.SelectionText.Should().Be("Triée avec les sets lresort, mslug, qui en ont besoin pour fonctionner");
        bios.StatusReasonText.Should().Be("Gardé : les sets lresort, mslug en ont besoin pour fonctionner");
        _vm.UiLocale = "en";

        // Every game checked again: the ordinary sort, no selection line
        PartialSort.AreShownRowsChecked = true;
        PartialSort.IsPartialSelection.Should().BeFalse();
        _vm.PreviewTable.AllPreviewItems.Should().OnlyContain(r => r.SelectionText.Length == 0);
        bios.TooltipText.Should().NotContain("Selection");
    }
}
