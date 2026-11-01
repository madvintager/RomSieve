using System.Text.Json;
using RomSieve.App;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>CHD files in the interface: option of the match by name, preview row and DAT completion banner.</summary>
public sealed class ChdPresentationTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("fr");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task MatchChdByName_ShouldBeOffByDefaultAndPersisted()
    {
        var path = _temp.WriteText("config.json", "{}");
        var vm = TestMainViewModel.Create(new HeadlessDialogService(), path);
        vm.BuildConfigUnchecked().MatchChdByName.Should().BeFalse();

        vm.Directories.MatchChdByName = true;
        vm.BuildConfigUnchecked().MatchChdByName.Should().BeTrue();
        await vm.SaveConfigurationAsync();

        using (var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
        {
            saved.RootElement.GetProperty("match_chd_by_name").GetBoolean().Should().BeTrue();
        }

        TestMainViewModel.Create(new HeadlessDialogService(), path).Directories.MatchChdByName.Should().BeTrue();
    }

    [Fact]
    public void PreviewRow_ShouldShowTheChdAndAMatchByName()
    {
        var item = new ProcessItem
        {
            SourcePath = Path.Combine(_temp.Path, "Game (Japan).chd"),
            FileSize = 1000,
            Parsed = _services.NoIntroParser.ParseRomFilename("Game (Japan).chd"),
            Status = FileStatus.Valid,
            DatGameName = "Game (Japan)",
            IsIdentifiedByName = true,
            Chd = new ChdInfo(5, 1000, new string('a', 40), new string('b', 40), ParentSha1: new string('c', 40), ChdMediaKind.Cd,
                [new ChdTrack(1, "MODE1_RAW", 100), new ChdTrack(2, "AUDIO", 50), new ChdTrack(3, "AUDIO", 50)])
        };

        var row = new PreviewItemViewModel(item, _services.I18n, _services.FilterService, _temp.Path);

        row.StatusDisplay.Should().Be("Retenu (identifié par le nom, non vérifié)");
        row.TooltipText.Should().Contain("CHD v5 : CD, 3 piste(s) dont 2 audio")
            .And.Contain("CHD différentiel : nécessite son CHD parent")
            .And.Contain("Rattaché au jeu « Game (Japan) » du DAT par son nom");
    }

    [Fact]
    public void DatBanner_ShouldShowTheGamesMatchedByNameApart()
    {
        var banner = new StatsBannerViewModel(_services.I18n);

        banner.UpdateStats(new ProcessStats { DatTotalRoms = 4, DatMatchedCount = 0, DatNameMatchedCount = 3, DatCompletionPct = 75 });
        banner.DatStatsText.Should().Be("Complétude : 75.0% (3/4, dont 3 par le nom d'un CHD)");

        banner.UpdateStats(new ProcessStats { DatTotalRoms = 4, DatMatchedCount = 1, DatCompletionPct = 25 });
        banner.DatStatsText.Should().Be("Complétude : 25.0% (1/4)", "without match by name, the banner is unchanged");
    }
}
