using System;
using System.Collections.Generic;
using System.Linq;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

public sealed class PreviewTableViewModelTests : IDisposable
{
    private readonly TempDirectoryFixture _temp;
    private readonly CoreTestServices _services;
    private readonly List<(LogLevel Level, LocalizedMessage Message)> _logs = [];

    public PreviewTableViewModelTests()
    {
        _temp = new TempDirectoryFixture();
        _services = new CoreTestServices("fr");
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void AddPreviewItem_ShouldAddRowAndMaintainKnownTitles()
    {
        var activeFilters = new HashSet<string>();
        var isRunning = false;
        var vm = CreateViewModel(() => isRunning, () => activeFilters);

        var f1 = _temp.WriteBytes("Super Mario Bros. (1985)(Nintendo)(US).nes", [1, 2, 3]);
        var item1 = CreateProcessItem(f1, "Super Mario Bros. (1985)(Nintendo)(US).nes", "Super Mario Bros.", FileStatus.Valid);

        var f2 = _temp.WriteBytes("Zelda (1986)(Nintendo)(US).nes", [4, 5, 6]);
        var item2 = CreateProcessItem(f2, "Zelda (1986)(Nintendo)(US).nes", "Zelda", FileStatus.Excluded);

        vm.AddPreviewItem(item1, _temp.Path);
        vm.AddPreviewItem(item2, _temp.Path);

        vm.AllPreviewItems.Should().HaveCount(2);
        vm.FilteredPreviewItems.Should().HaveCount(2);
        vm.PreviewKnownTitles.Should().ContainInOrder("Super Mario Bros.", "Zelda");
    }

    [Fact]
    public void SearchText_ShouldFilterRowsAccentInsensitive()
    {
        var activeFilters = new HashSet<string>();
        var isRunning = false;
        var vm = CreateViewModel(() => isRunning, () => activeFilters);

        var f1 = _temp.WriteBytes("Astérix (1993)(Infogrames)(FR).nes", [1, 2]);
        var item1 = CreateProcessItem(f1, "Astérix (1993)(Infogrames)(FR).nes", "Astérix", FileStatus.Valid);

        var f2 = _temp.WriteBytes("Zelda (1986)(Nintendo)(US).nes", [3, 4]);
        var item2 = CreateProcessItem(f2, "Zelda (1986)(Nintendo)(US).nes", "Zelda", FileStatus.Valid);

        vm.AddPreviewItem(item1, _temp.Path);
        vm.AddPreviewItem(item2, _temp.Path);

        // Search with unaccented "asterix" should match "Astérix"
        vm.PreviewSearchText = "asterix";

        vm.FilteredPreviewItems.Should().HaveCount(1);
        vm.FilteredPreviewItems[0].TitleDisplay.Should().Be("Astérix");

        vm.ClearPreviewSearch();
        vm.FilteredPreviewItems.Should().HaveCount(2);
    }

    [Fact]
    public void ActiveStatusFilters_ShouldFilterPreviewRows()
    {
        var activeFilters = new HashSet<string>();
        var isRunning = false;
        var vm = CreateViewModel(() => isRunning, () => activeFilters);

        var f1 = _temp.WriteBytes("Game1.nes", [1]);
        var item1 = CreateProcessItem(f1, "Game1.nes", "Game1", FileStatus.Valid);

        var f2 = _temp.WriteBytes("Game2.nes", [2]);
        var item2 = CreateProcessItem(f2, "Game2.nes", "Game2", FileStatus.Excluded);

        vm.AddPreviewItem(item1, _temp.Path);
        vm.AddPreviewItem(item2, _temp.Path);

        // Filter only valid
        activeFilters.Add("valid");
        vm.RefreshFilteredPreviewItems();

        vm.FilteredPreviewItems.Should().HaveCount(1);
        vm.FilteredPreviewItems[0].StatusValue.Should().Be("valid");
    }

    [Fact]
    public void ColumnSorting_ShouldSortAscendingAndDescending()
    {
        var activeFilters = new HashSet<string>();
        var isRunning = false;
        var vm = CreateViewModel(() => isRunning, () => activeFilters);

        var f1 = _temp.WriteBytes("Zelda.nes", [1]);
        var item1 = CreateProcessItem(f1, "Zelda.nes", "Zelda", FileStatus.Valid);

        var f2 = _temp.WriteBytes("Mario.nes", [2]);
        var item2 = CreateProcessItem(f2, "Mario.nes", "Mario", FileStatus.Valid);

        vm.AddPreviewItem(item1, _temp.Path);
        vm.AddPreviewItem(item2, _temp.Path);

        // Column 2 = Title
        vm.SortPreviewByColumn(2);
        vm.IsSortAscending.Should().BeTrue();
        vm.AllPreviewItems[0].TitleDisplay.Should().Be("Mario");
        vm.AllPreviewItems[1].TitleDisplay.Should().Be("Zelda");

        // Click again -> descending
        vm.SortPreviewByColumn(2);
        vm.IsSortAscending.Should().BeFalse();
        vm.AllPreviewItems[0].TitleDisplay.Should().Be("Zelda");
        vm.AllPreviewItems[1].TitleDisplay.Should().Be("Mario");
    }

    [Fact]
    public void Force1G1RWinnerAndReset_ShouldManageOverrides()
    {
        var activeFilters = new HashSet<string>();
        var isRunning = false;
        var ensure1G1RCalled = false;

        var config = new SortConfig
        {
            SourceDir = _temp.Path,
            DestinationDir = _temp.Path,
            Filters = new FilterConfig { OneGameOneRom = true }
        };

        var vm = new PreviewTableViewModel(
            _services.I18n,
            _services.FilterService,
            _services.Processor,
            () => isRunning,
            () => config,
            () => null,
            () => new ProcessStats { TotalScanned = 2, ValidCount = 1 },
            () => activeFilters,
            (_, _) => { },
            (lvl, msg) => _logs.Add((lvl, msg)),
            () => ensure1G1RCalled = true,
            () => activeFilters.Clear(),
            () => { });

        var f1 = _temp.WriteBytes("Game (USA).nes", [1]);
        var item1 = CreateProcessItem(f1, "Game (USA).nes", "Game", FileStatus.Valid);

        var f2 = _temp.WriteBytes("Game (Europe).nes", [2]);
        var item2 = CreateProcessItem(f2, "Game (Europe).nes", "Game", FileStatus.Excluded);

        vm.AddPreviewItem(item1, _temp.Path);
        vm.AddPreviewItem(item2, _temp.Path);

        var row2 = vm.AllPreviewItems[1];
        vm.Force1G1RWinner(row2);

        vm.HasManual1G1ROverrides.Should().BeTrue();
        ensure1G1RCalled.Should().BeTrue();
        _logs.Should().Contain(l => l.Message.Key == "log.manual_1g1r_forced" && Equals(l.Message.Args["title"], row2.Item.Parsed.Title));

        // Reset override
        vm.Reset1G1ROverrideForGame(row2);
        vm.HasManual1G1ROverrides.Should().BeFalse();
    }

    [Fact]
    public void ContextMenu_ShouldBuildExpectedActions()
    {
        var activeFilters = new HashSet<string>();
        var isRunning = false;
        var vm = CreateViewModel(() => isRunning, () => activeFilters);

        var f1 = _temp.WriteBytes("Super Mario.nes", [1]);
        var item1 = CreateProcessItem(f1, "Super Mario.nes", "Super Mario", FileStatus.Valid);
        vm.AddPreviewItem(item1, _temp.Path);

        var actions = vm.BuildPreviewContextMenuForRow(0);
        actions.Should().Contain(a => a.Name == "act_force_1g1r_winner");
        actions.Should().Contain(a => a.Name == "act_filter_this_game");
    }

    [Fact]
    public void Columns_ShouldHideCrcDiskPublisherAndRelatedByDefault()
    {
        var vm = CreateViewModel(() => false, () => new HashSet<string>());

        vm.Columns.Select(c => c.Id).Should().Equal(PreviewTableViewModel.ColumnIds);
        vm.Columns.Where(c => !c.IsVisible).Select(c => c.Id).Should().BeEquivalentTo("crc32", "disk", "publisher", "related", "dat");
        vm.HiddenColumnIds.Should().Equal("crc32", "disk", "publisher", "related", "dat");
        vm.HideableColumns.Select(c => c.Id).Should().NotContain(["source", "status"], "a row must stay identifiable");
        vm.Columns.Select(c => c.Header).Should().Equal(vm.PreviewColumnHeaders);
        vm.Columns[8].Header.Should().Be("Chemin cible calculé");
    }

    [Fact]
    public void ColumnAddedSinceTheListWasSaved_ShouldTakeItsDefaultVisibility()
    {
        var vm = CreateViewModel(() => false, () => new HashSet<string>());

        // A list saved by a version that did not know the column of related files: it stays hidden by default
        var withoutRelated = PreviewTableViewModel.ColumnIds.Where(id => id != "related").ToList();
        vm.SetHiddenColumnIds(["crc32"], withoutRelated);
        vm.HiddenColumnIds.Should().Equal("crc32", "related");
        vm.SetHiddenColumnIds([], withoutRelated);
        vm.HiddenColumnIds.Should().Equal("related");

        // A list saved with the column known, or without the list of known columns: its visibility is the user's choice
        vm.SetHiddenColumnIds(["crc32"]);
        vm.HiddenColumnIds.Should().Equal("crc32");
        vm.SetHiddenColumnIds(["crc32"], PreviewTableViewModel.ColumnIds);
        vm.HiddenColumnIds.Should().Equal("crc32");
        vm.SetHiddenColumnIds(["crc32", "related"], PreviewTableViewModel.ColumnIds);
        vm.HiddenColumnIds.Should().Equal("crc32", "related");
    }

    [Fact]
    public void SetHiddenColumnIds_ShouldIgnoreUnknownNamesAndTheColumnsAlwaysShown()
    {
        var vm = CreateViewModel(() => false, () => new HashSet<string>());

        vm.SetHiddenColumnIds(["title", "bogus", "status", "source", "TARGET"], PreviewTableViewModel.ColumnIds);
        vm.HiddenColumnIds.Should().Equal("title", "target");

        vm.SetHiddenColumnIds([], PreviewTableViewModel.ColumnIds);
        vm.HiddenColumnIds.Should().BeEmpty("an empty list shows every column");

        vm.Columns[0].IsVisible = false;
        vm.Columns[9].IsVisible = false;
        vm.HiddenColumnIds.Should().BeEmpty("the source name and the status cannot be hidden");

        vm.SetHiddenColumnIds(null);
        vm.HiddenColumnIds.Should().Equal(PreviewTableViewModel.DefaultHiddenColumnIds);

        vm.Columns[1].IsVisible = true;
        vm.Columns[PreviewTableViewModel.RelatedColumnIndex].IsVisible = true;
        vm.ResetColumnsCommand.Execute(null);
        vm.HiddenColumnIds.Should().Equal(PreviewTableViewModel.DefaultHiddenColumnIds);
    }

    [Fact]
    public void Row_ShouldShowAShortStatusAndKeepItsReasonForTheDetail()
    {
        var vm = CreateViewModel(() => false, () => new HashSet<string>());
        var file = _temp.WriteBytes("Game (1990)(Ocean)[a].adf", [1, 2]);
        var item = CreateProcessItem(file, "Game (1990)(Ocean)[a].adf", "Game", FileStatus.Excluded);
        item.StatusReason = LocalizedMessage.Create("reason.alternate_dump_excluded");
        item.FinalTargetPath = System.IO.Path.Combine(_temp.Path, "Ocean", "Game (1990)(Ocean)[a].adf");
        vm.AddPreviewItem(item, _temp.Path);

        var row = vm.AllPreviewItems.Single();
        var reason = _services.I18n.Format(item.StatusReason);
        reason.Should().Be("Alternate Dump [a] exclu");
        row.StatusLabel.Should().Be("Exclu");
        row.StatusReasonText.Should().Be(reason);
        row.StatusDisplay.Should().Be($"Exclu ({reason})", "the status column is still sorted on the status and its reason");
        row[9].Should().Be(row.StatusDisplay);
        row.TooltipText.Should().Contain(reason, "the reason stays in the tooltip of the row");
        row.TargetPathDisplay.Should().Be(System.IO.Path.Combine("Ocean", "Game (1990)(Ocean)[a].adf"));
        row.FullTargetPath.Should().Be(item.FinalTargetPath);
        row.SourcePathDisplay.Should().Be(file);
        row.Crc32Text.Should().Be("12345678");

        item.ArchiveMember = "Game (1990)(Ocean)[a].adf";
        item.SourcePath = System.IO.Path.Combine(_temp.Path, "games.zip");
        row.RefreshFromItem();
        row.SourcePathDisplay.Should().Be($"{item.SourcePath} › Game (1990)(Ocean)[a].adf");

        // Every status has its badge label, without reason
        foreach (var (status, label) in new[] { (FileStatus.Valid, "Retenu"), (FileStatus.Unmatched, "Non conforme"), (FileStatus.Duplicate, "Doublon"), (FileStatus.Error, "Erreur") })
        {
            item.Status = status;
            row.RefreshFromItem();
            row.StatusLabel.Should().Be(label);
            row.Status.Should().Be(status);
        }
    }

    [Fact]
    public void Row_WithoutReason_ShouldHaveAnEmptyReason()
    {
        var vm = CreateViewModel(() => false, () => new HashSet<string>());
        var file = _temp.WriteBytes("Game (1990)(Ocean).adf", [1]);
        vm.AddPreviewItem(CreateProcessItem(file, "Game (1990)(Ocean).adf", "Game", FileStatus.Valid), _temp.Path);

        var row = vm.AllPreviewItems.Single();
        row.StatusLabel.Should().Be("Retenu");
        row.StatusReasonText.Should().BeEmpty();
        row.StatusDisplay.Should().Be("Retenu");
        row.FullTargetPath.Should().BeEmpty();
    }

    private PreviewTableViewModel CreateViewModel(Func<bool> isRunning, Func<IReadOnlySet<string>> getFilters)
    {
        return new PreviewTableViewModel(
            _services.I18n,
            _services.FilterService,
            _services.Processor,
            isRunning,
            () => null,
            () => null,
            () => null,
            getFilters,
            (_, _) => { },
            (lvl, msg) => _logs.Add((lvl, msg)),
            () => { },
            () => { },
            () => { });
    }

    private static ProcessItem CreateProcessItem(string filePath, string fileName, string title, FileStatus status)
    {
        return new ProcessItem
        {
            SourcePath = filePath,
            FileSize = 10,
            Parsed = new ParsedRomName
            {
                RawFilename = fileName,
                Title = title,
                IsTosecConform = true
            },
            Status = status,
            Crc32 = "12345678"
        };
    }
}
