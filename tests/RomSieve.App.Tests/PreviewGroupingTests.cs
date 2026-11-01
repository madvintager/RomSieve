using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// View grouped by game of the preview table: headers, order of the games and of their files, filters, folding,
/// manual 1G1R choices and differential simulations, on the results of real simulations.
/// </summary>
public sealed class PreviewGroupingTests : IDisposable
{
    private const string AnotherWorldFr = "Another World (1991)(Delphine)(fr).adf";
    private const string AnotherWorldEn = "Another World (1991)(Delphine)(en).adf";
    private const string AnotherWorldDe = "Another World (1991)(Delphine)(de).adf";
    private const string LemmingsDisk1 = "Lemmings (1991)(Psygnosis)(Disk 1 of 2).adf";
    private const string LemmingsDisk2 = "Lemmings (1991)(Psygnosis)(Disk 2 of 2).adf";
    private const string Zool = "Zool (1993)(Gremlin)(en).adf";
    private const string Junk1 = "junk one.bin";
    private const string Junk2 = "junk two.bin";

    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("fr");
    private readonly HashSet<string> _activeFilters = [];
    private readonly string _source;
    private SortConfig? _config;

    public PreviewGroupingTests()
    {
        _source = _temp.CreateSubdirectory("src");
        var content = 0;
        foreach (var name in new[] { AnotherWorldFr, AnotherWorldEn, AnotherWorldDe, LemmingsDisk1, LemmingsDisk2, Zool, Junk1, Junk2 })
        {
            _temp.WriteBytes(System.IO.Path.Combine("src", name), [(byte)++content, 1, 2, 3]);
        }
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task FlatView_ShouldBeTheDefaultAndShowTheFilesOnly()
    {
        var vm = await SimulateAsync();

        vm.IsGroupedByGame.Should().BeFalse("the flat view is the default");
        vm.DisplayedRows.Should().Equal(vm.FilteredPreviewItems);
        vm.DisplayedRows.Should().NotContain(r => r.IsGroupHeader || r.IsGroupMember);
    }

    [Fact]
    public async Task GroupedView_ShouldShowAHeaderPerGameOfSeveralFiles_TheWinnerFirst()
    {
        var vm = await SimulateAsync();

        vm.IsGroupedByGame = true;

        Layout(vm).Should().Equal(
            "# Another World",
            Winner("Another World"), Loser("Another World"), Loser("Another World"),
            "# Lemmings", LemmingsDisk1, LemmingsDisk2,
            Zool,
            "# " + _services.I18n.Tr("panel4.group_unidentified"), Junk1, Junk2);

        var anotherWorld = Header(vm, "Another World");
        var winner = (PreviewItemViewModel)vm.DisplayedRows[1];
        winner.Item.Is1G1RWinner.Should().BeTrue();
        anotherWorld.CountText.Should().Be("(3)");
        anotherWorld.TitleDisplay.Should().Be($"Retenu : {winner.SourceName}");
        anotherWorld.TargetPathDisplay.Should().Be(winner.TargetPathDisplay);
        anotherWorld.StatusLabel.Should().Be("Retenu");
        anotherWorld.Status.Should().Be(FileStatus.Valid);
        anotherWorld.IsForcedWinner.Should().BeFalse();
        anotherWorld.GameTitle.Should().Be("Another World");

        vm.DisplayedRows.OfType<PreviewItemViewModel>().Single(r => r.SourceName == Zool).IsGroupMember
            .Should().BeFalse("a game of a single file keeps a plain row");
        vm.DisplayedRows.OfType<PreviewItemViewModel>().Where(r => r.SourceName != Zool)
            .Should().OnlyContain(r => r.IsGroupMember);
        vm.FilteredPreviewItems.Should().HaveCount(8, "the files shown stay the same whatever the view");

        vm.IsGroupedByGame = false;
        vm.DisplayedRows.Should().Equal(vm.FilteredPreviewItems);
        vm.DisplayedRows.Should().NotContain(r => r.IsGroupMember);
    }

    [Fact]
    public async Task GroupedView_ShouldFollowTheSortOfTheWinners_AndKeepTheWinnerFirst()
    {
        var vm = await SimulateAsync();
        vm.IsGroupedByGame = true;

        vm.SortPreviewByColumn(0);
        vm.IsSortAscending.Should().BeFalse();

        Layout(vm).Should().Equal(
            Zool,
            "# Lemmings", LemmingsDisk1, LemmingsDisk2,
            "# Another World",
            Winner("Another World"), Loser("Another World"), Loser("Another World"),
            "# " + _services.I18n.Tr("panel4.group_unidentified"), Junk2, Junk1);

        var losers = vm.DisplayedRows.OfType<PreviewItemViewModel>()
            .Where(r => r.Item.Parsed.Title == "Another World" && !r.Item.Is1G1RWinner)
            .Select(r => r.SourceName)
            .ToList();
        losers.Should().BeInDescendingOrder(StringComparer.OrdinalIgnoreCase, "the other files follow the sort");
    }

    [Fact]
    public async Task Filters_ShouldShowTheGamesWithAShownFile_AndCountTheShownFiles()
    {
        var vm = await SimulateAsync();
        vm.IsGroupedByGame = true;

        _activeFilters.Add("duplicate");
        _activeFilters.Add("excluded");
        vm.RefreshFilteredPreviewItems();

        var anotherWorld = Header(vm, "Another World");
        anotherWorld.CountText.Should().Be("(2/3)");
        anotherWorld.StatusLabel.Should().Be("Retenu", "the header shows the status of the game, not of the shown files");
        vm.DisplayedRows.Should().NotContain(r => r.SourceName == Zool);
        vm.DisplayedRows.OfType<PreviewGroupHeaderViewModel>().Should().ContainSingle("only Another World has filtered files");
        vm.DisplayedRows.OfType<PreviewItemViewModel>().Should().HaveCount(2).And.OnlyContain(r => !r.Item.Is1G1RWinner);

        _activeFilters.Clear();
        vm.PreviewSearchText = "zool";
        Layout(vm).Should().Equal(Zool);

        vm.PreviewSearchText = "lemmings";
        Layout(vm).Should().Equal("# Lemmings", LemmingsDisk1, LemmingsDisk2);
        Header(vm, "Lemmings").CountText.Should().Be("(2)");
    }

    [Fact]
    public async Task Folding_ShouldHideTheFilesOfAGame_AndGiveTheSelectionToItsHeader()
    {
        var vm = await SimulateAsync();
        vm.IsGroupedByGame = true;
        var anotherWorld = Header(vm, "Another World");
        var loser = vm.DisplayedRows.OfType<PreviewItemViewModel>().First(r => r.Item.Parsed.Title == "Another World" && !r.Item.Is1G1RWinner);
        vm.SelectedPreviewItem = loser;
        vm.SelectedRow.Should().BeSameAs(loser);

        var notifications = CountNotifications(vm.DisplayedRows);
        anotherWorld.ToggleCommand.Execute(null);

        anotherWorld.IsExpanded.Should().BeFalse();
        Layout(vm).Should().Equal(
            "# Another World",
            "# Lemmings", LemmingsDisk1, LemmingsDisk2,
            Zool,
            "# " + _services.I18n.Tr("panel4.group_unidentified"), Junk1, Junk2);
        notifications.Resets.Should().Be(0, "folding a game removes its rows one by one");
        vm.SelectedRow.Should().BeSameAs(anotherWorld, "the selected file is hidden in its folded game");
        vm.SelectedPreviewItem.Should().BeNull();
        vm.ToggleGroupMenuText.Should().Be(_services.I18n.Tr("panel4.ctx_group_expand"));

        vm.SetGroupExpanded(anotherWorld, true);
        Layout(vm).Should().HaveCount(11);

        vm.CollapseAllGroups();
        Layout(vm).Should().Equal(
            "# Another World", "# Lemmings", Zool, "# " + _services.I18n.Tr("panel4.group_unidentified"));

        vm.ExpandAllGroups();
        Layout(vm).Should().HaveCount(11);
        vm.DisplayedRows.OfType<PreviewGroupHeaderViewModel>().Should().OnlyContain(h => h.IsExpanded);
    }

    [Fact]
    public async Task ForcingAWinner_ShouldMoveItFirstAndMarkItAndItsGame_WithoutReloadingTheTable()
    {
        var vm = await SimulateAsync();
        vm.IsGroupedByGame = true;
        var anotherWorld = Header(vm, "Another World");
        var loser = vm.DisplayedRows.OfType<PreviewItemViewModel>().Last(r => r.Item.Parsed.Title == "Another World");
        loser.Item.Is1G1RWinner.Should().BeFalse();

        var notifications = CountNotifications(vm.DisplayedRows);
        vm.Force1G1RWinner(loser);

        notifications.Resets.Should().Be(0, "the rows of the game move one by one");
        loser.Item.Is1G1RWinner.Should().BeTrue();
        loser.IsForcedWinner.Should().BeTrue();
        vm.DisplayedRows.IndexOf(loser).Should().Be(vm.DisplayedRows.IndexOf(anotherWorld) + 1, "the winner comes first in its game");
        anotherWorld.IsForcedWinner.Should().BeTrue();
        anotherWorld.TitleDisplay.Should().Be($"Retenu : {loser.SourceName}");
        anotherWorld.TooltipText.Should().Contain(_services.I18n.Tr("panel4.tooltip_forced_winner"));
        vm.DisplayedRows.Where(r => r.IsForcedWinner).Should().Equal(anotherWorld, loser);

        // From the header, the manual choice of the game can be undone
        vm.SelectedRow = anotherWorld;
        vm.CanResetSelected1G1ROverride.Should().BeTrue();
        vm.BuildPreviewContextMenu(anotherWorld).Select(a => a.Name).Should().Equal(
            "act_toggle_group", "act_collapse_all_groups", "act_expand_all_groups",
            "act_reset_1g1r_override", "act_clear_all_1g1r_overrides", "act_filter_this_game");

        vm.Reset1G1ROverrideForGame(anotherWorld);
        vm.HasManual1G1ROverrides.Should().BeFalse();
        anotherWorld.IsForcedWinner.Should().BeFalse();
        vm.DisplayedRows.Should().NotContain(r => r.IsForcedWinner);
        vm.SelectedRow.Should().BeSameAs(anotherWorld);
    }

    [Fact]
    public async Task HeaderOfAGame_ShouldIsolateItsGameAndOfferNoForcing()
    {
        var vm = await SimulateAsync();
        vm.IsGroupedByGame = true;
        var lemmings = Header(vm, "Lemmings");

        vm.SelectedRow = lemmings;
        vm.SelectedGroupHeader.Should().BeSameAs(lemmings);
        vm.IsGroupHeaderSelected.Should().BeTrue();
        vm.CanForce1G1RWinner.Should().BeFalse();
        vm.CanIsolateSelectedGame.Should().BeTrue();
        vm.CanOpenPreviewContextMenu.Should().BeTrue();
        vm.IsolateGameMenuText.Should().Contain("Lemmings");
        vm.BuildPreviewContextMenu(lemmings).Select(a => a.Name).Should().Equal(
            "act_toggle_group", "act_collapse_all_groups", "act_expand_all_groups", "act_filter_this_game");

        vm.IsolateGameInPreview(lemmings);
        vm.PreviewSearchText.Should().Be("Lemmings");
        Layout(vm).Should().Equal("# Lemmings", LemmingsDisk1, LemmingsDisk2);

        var unidentified = vm.DisplayedRows.OfType<PreviewGroupHeaderViewModel>().FirstOrDefault(h => h.IsUnidentified);
        unidentified.Should().BeNull("the search hides the files without a game");
        vm.ClearPreviewSearch();
        unidentified = vm.DisplayedRows.OfType<PreviewGroupHeaderViewModel>().Single(h => h.IsUnidentified);
        vm.SelectedRow = unidentified;
        vm.CanIsolateSelectedGame.Should().BeFalse("the files without a game have no title to search");
        vm.CanResetSelected1G1ROverride.Should().BeFalse();
    }

    [Fact]
    public async Task SwitchingTheView_ShouldKeepTheSelectedFile()
    {
        var vm = await SimulateAsync();
        var disk2 = vm.FilteredPreviewItems.Single(r => r.SourceName == LemmingsDisk2);
        vm.SelectedPreviewItem = disk2;

        vm.IsGroupedByGame = true;
        vm.SelectedRow.Should().BeSameAs(disk2);

        vm.IsGroupedByGame = false;
        vm.SelectedRow.Should().BeSameAs(disk2);
        vm.SelectedPreviewItem.Should().BeSameAs(disk2);
    }

    [Fact]
    public async Task DifferentialSimulation_ShouldKeepTheHeadersAndTheFoldedGames()
    {
        var vm = await SimulateAsync();
        vm.IsGroupedByGame = true;
        var anotherWorld = Header(vm, "Another World");
        var lemmings = Header(vm, "Lemmings");
        vm.SetGroupExpanded(lemmings, false);
        vm.SelectedRow = anotherWorld;

        // Same source, one more version of Another World
        _temp.WriteBytes(System.IO.Path.Combine("src", "Another World (1991)(Delphine)(it).adf"), [42, 1, 2, 3]);
        var result = await _services.Processor.ExecuteAsync(_config!, isDryRun: true);
        vm.CanCompareWithPreviousSimulation(_source).Should().BeTrue();
        vm.BeginDifferentialRun();
        vm.ApplyDifferentialResults(_config!.DestinationDir, result.Items);
        vm.CompleteRun(_source);

        Header(vm, "Another World").Should().BeSameAs(anotherWorld, "a header keeps its row from a simulation to the next");
        anotherWorld.CountText.Should().Be("(4)");
        anotherWorld.Change.Should().NotBe(PreviewRowChange.None, "a file of the game is new");
        lemmings.IsExpanded.Should().BeFalse("a folded game stays folded");
        Layout(vm).Should().ContainInOrder("# Lemmings", Zool);
        vm.SelectedRow.Should().BeSameAs(anotherWorld);

        // A new table (another run that is not compared) unfolds every game
        vm.Clear();
        vm.RebuildAllPreviewItemsFromLastItems(_config.DestinationDir, result.Items);
        vm.RefreshFilteredPreviewItems();
        vm.DisplayedRows.OfType<PreviewGroupHeaderViewModel>().Should().OnlyContain(h => h.IsExpanded);
        Header(vm, "Lemmings").Should().NotBeSameAs(lemmings);
    }

    [Fact]
    public async Task RowsAddedDuringARun_ShouldBeShownFlat_UntilTheRunIsFinished()
    {
        var config = CreateConfig();
        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);
        var vm = CreateViewModel();
        vm.IsGroupedByGame = true;

        foreach (var item in result.Items)
        {
            vm.AddPreviewItem(item, config.DestinationDir);
        }

        vm.DisplayedRows.Should().HaveCount(8).And.NotContain(r => r.IsGroupHeader);

        vm.CurrentSortColumn = 0;
        vm.ApplyPreviewSorting();
        vm.RefreshFilteredPreviewItems();
        vm.DisplayedRows.OfType<PreviewGroupHeaderViewModel>().Should().HaveCount(3);
    }

    [Fact]
    public async Task Headers_ShouldBeTranslatedWhenTheLanguageChanges()
    {
        var vm = await SimulateAsync();
        vm.IsGroupedByGame = true;
        var anotherWorld = Header(vm, "Another World");
        var winner = vm.DisplayedRows[1].SourceName;

        _services.I18n.SetLocale("en");
        vm.RefreshTranslations();

        anotherWorld.TitleDisplay.Should().Be($"Kept: {winner}");
        anotherWorld.StatusLabel.Should().Be("Valid");
        vm.DisplayedRows.OfType<PreviewGroupHeaderViewModel>().Single(h => h.IsUnidentified).SourceName
            .Should().Be("No identified game");
    }

    private SortConfig CreateConfig()
    {
        _config ??= new SortConfig
        {
            SourceDir = _source,
            DestinationDir = _temp.GetFilePath("dst"),
            Filters = new FilterConfig { OneGameOneRom = true, PriorityLanguages = ["fr", "en"] }
        };
        return _config;
    }

    /// <summary>Simulates the source and shows its result as the main window does at the end of a run.</summary>
    private async Task<PreviewTableViewModel> SimulateAsync()
    {
        var config = CreateConfig();
        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);
        var vm = CreateViewModel();
        vm.RebuildAllPreviewItemsFromLastItems(config.DestinationDir, result.Items);
        vm.CurrentSortColumn = 0;
        vm.IsSortAscending = true;
        vm.ApplyPreviewSorting();
        vm.RefreshFilteredPreviewItems();
        vm.CompleteRun(_source);
        return vm;
    }

    private PreviewTableViewModel CreateViewModel()
    {
        PreviewTableViewModel? vm = null;
        vm = new PreviewTableViewModel(
            _services.I18n,
            _services.FilterService,
            _services.Processor,
            () => false,
            () =>
            {
                // As the main window does, the configuration carries the manual 1G1R choices of the table
                var config = CreateConfig();
                config.Filters.Manual1G1RWinners = new Dictionary<string, string>(vm!.Manual1G1RWinners, StringComparer.Ordinal);
                return config;
            },
            () => null,
            () => new ProcessStats(),
            () => _activeFilters,
            (_, _) => { },
            (_, _) => { },
            () => { },
            () => _activeFilters.Clear(),
            () => { });
        return vm;
    }

    private static PreviewGroupHeaderViewModel Header(PreviewTableViewModel vm, string title) =>
        vm.DisplayedRows.OfType<PreviewGroupHeaderViewModel>().Single(h => h.GameTitle == title);

    private static string Winner(string title) => $"winner:{title}";

    private static string Loser(string title) => $"other:{title}";

    /// <summary>
    /// Rows of the table: <c># title</c> for a header, the file name for a file, or for the versions of a game whose
    /// names depend on the 1G1R choice, <c>winner:title</c> and <c>other:title</c>.
    /// </summary>
    private static List<string> Layout(PreviewTableViewModel vm) => vm.DisplayedRows.Select(r => r switch
    {
        PreviewGroupHeaderViewModel header => "# " + header.SourceName,
        PreviewItemViewModel { Item.Parsed.Title: "Another World" } item => item.Item.Is1G1RWinner ? Winner("Another World") : Loser("Another World"),
        _ => r.SourceName
    }).ToList();

    private static NotificationCounter CountNotifications(INotifyCollectionChanged collection)
    {
        var counter = new NotificationCounter();
        collection.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                counter.Resets++;
            }
        };
        return counter;
    }

    private sealed class NotificationCounter
    {
        public int Resets { get; set; }
    }
}
