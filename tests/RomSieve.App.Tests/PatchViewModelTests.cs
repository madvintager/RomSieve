using System.Text.Json;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// The patches in the interface: the patch folder and the option of the Collection step, saved and read back; the
/// state line of a destination that overlaps the patch folder; the table (badge, detail panel, grouped view, no
/// winner forced on a patched version); the banner (chip of the patched versions, patches not applied).
/// </summary>
public sealed class PatchViewModelTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("fr");
    private readonly HashSet<string> _activeFilters = [];

    private static readonly byte[] Japan = Data(1, 4096);
    private static readonly byte[] Usa = Data(2, 4096);

    public void Dispose() => _temp.Dispose();

    private static byte[] Data(int seed, int size)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    // =========================================================================
    // Collection step
    // =========================================================================

    [Fact]
    public async Task ThePatchFolderAndTheOption_ShouldBeSavedAndReadBack()
    {
        var path = _temp.WriteText("config.json", "{}");
        var dialogs = new HeadlessDialogService { NextPickedFolder = _temp.CreateSubdirectory("patches") };
        var vm = TestMainViewModel.Create(dialogs, path);
        vm.Directories.PatchDir.Should().BeEmpty();
        vm.Directories.HasPatchDir.Should().BeFalse("the option is greyed out without a patch folder");

        await vm.Directories.BrowsePatchDirCommand.ExecuteAsync(null);
        vm.Directories.PatchOnly = true;

        vm.Directories.HasPatchDir.Should().BeTrue();
        vm.BuildConfigUnchecked().PatchDir.Should().Be(dialogs.NextPickedFolder);
        vm.BuildConfigUnchecked().PatchOnly.Should().BeTrue();
        await vm.SaveConfigurationAsync();
        using (var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
        {
            saved.RootElement.GetProperty("patch_dir").GetString().Should().Be(dialogs.NextPickedFolder);
            saved.RootElement.GetProperty("patch_only").GetBoolean().Should().BeTrue();
        }

        var reloaded = TestMainViewModel.Create(new HeadlessDialogService(), path);
        reloaded.Directories.PatchDir.Should().Be(dialogs.NextPickedFolder);
        reloaded.Directories.PatchOnly.Should().BeTrue();

        reloaded.Directories.ClearPatchDirCommand.Execute(null);
        reloaded.Directories.PatchDir.Should().BeEmpty();
        reloaded.Directories.HasPatchDir.Should().BeFalse();
    }

    [Fact]
    public void ADestinationInsideThePatchFolder_ShouldBeSaidUnderTheOutputStep()
    {
        var vm = TestMainViewModel.Create(new HeadlessDialogService(), _temp.WriteText("config.json", "{}"));
        vm.Directories.SourceDir = _temp.CreateSubdirectory("src");
        vm.Directories.PatchDir = _temp.CreateSubdirectory("patches");

        vm.Directories.DestinationDir = Path.Combine(_temp.Path, "patches", "sorted");

        vm.Navigation.OutputEntry.IndicatorTone.Should().Be(StepIndicatorTone.Warning);
        vm.Navigation.OutputEntry.IndicatorText.Should().Be("Chevauche les patchs");

        vm.Directories.PatchDir = string.Empty;
        vm.Navigation.OutputEntry.IndicatorTone.Should().Be(StepIndicatorTone.None);
    }

    // =========================================================================
    // Table
    // =========================================================================

    /// <summary>A Japanese and an American release of a game, the American one kept, and a translation of the Japanese one.</summary>
    private async Task<(PreviewTableViewModel Table, SortExecutionResult Result)> SimulateAsync()
    {
        _temp.WriteBytes("src/Game (Japan).sfc", Japan);
        _temp.WriteBytes("src/Game (USA).sfc", Usa);
        var dat = _temp.WriteText("dat/snes.dat", $"""
            <?xml version="1.0"?><datafile><header><name>Nintendo - SNES</name><homepage>No-Intro</homepage></header>
            <game name="Game (Japan)"><description>Game (Japan)</description><rom name="Game (Japan).sfc" size="{Japan.Length}" crc="{PatchTestEncoder.Crc(Japan):x8}"/></game>
            <game name="Game (USA)"><description>Game (USA)</description><rom name="Game (USA).sfc" size="{Usa.Length}" crc="{PatchTestEncoder.Crc(Usa):x8}"/></game></datafile>
            """);
        _temp.WriteBytes("patches/Game (Japan) (T-En).bps", PatchTestEncoder.BpsLinear(Japan, [.. Japan, .. "English"u8.ToArray()]));
        var config = new SortConfig
        {
            SourceDir = _temp.GetFilePath("src"),
            DestinationDir = _temp.CreateSubdirectory("dst"),
            DatFile = dat,
            NamingConvention = NamingConvention.NoIntro,
            PatchDir = _temp.GetFilePath("patches"),
            Filters = new FilterConfig { OneGameOneRom = true, PriorityLanguages = ["en", "ja"] }
        };
        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);

        var table = new PreviewTableViewModel(
            _services.I18n, _services.FilterService, _services.Processor, () => false, () => config, () => result.DatCatalog,
            () => result.Stats, () => _activeFilters, (_, _) => { }, (_, _) => { }, () => { }, () => _activeFilters.Clear(), () => { });
        table.RebuildAllPreviewItemsFromLastItems(config.DestinationDir, result.Items);
        table.RefreshFilteredPreviewItems();
        table.CompleteRun(config.SourceDir);
        return (table, result);
    }

    private static PreviewItemViewModel Row(PreviewTableViewModel table, string sourceName) =>
        table.AllPreviewItems.Single(r => r.SourceName == sourceName);

    [Fact]
    public async Task APatchedVersion_ShouldHaveItsBadgeAndNameItsPatchInTheDetail()
    {
        var (table, _) = await SimulateAsync();

        var patched = Row(table, "Game (Japan) (T-En).bps");
        patched.IsPatched.Should().BeTrue();
        patched.DetailPatchLabel.Should().Be("Patch");
        patched.DetailPatchText.Should().Be($"Game (Japan) (T-En).bps (BPS), appliqué à Game (Japan).sfc (CRC32 {PatchTestEncoder.Crc(Japan):X8})");

        var origin = Row(table, "Game (Japan).sfc");
        origin.IsPatched.Should().BeFalse();
        origin.DetailPatchLabel.Should().Be("Versions patchées");
        origin.DetailPatchText.Should().Be("Game (Japan) (T-En).sfc");
        Row(table, "Game (USA).sfc").DetailPatchText.Should().BeEmpty();
    }

    [Fact]
    public async Task TheGroupedView_ShouldShowThePatchedVersionUnderTheKeptRelease()
    {
        var (table, _) = await SimulateAsync();

        table.IsGroupedByGame = true;

        table.DisplayedRows.Select(r => r is PreviewGroupHeaderViewModel ? "#" : r.SourceName)
            .Should().Equal("#", "Game (USA).sfc", "Game (Japan) (T-En).bps", "Game (Japan).sfc");
        table.DisplayedRows.OfType<PreviewGroupHeaderViewModel>().Single().IsPatched.Should().BeTrue();
    }

    [Fact]
    public async Task APatchedVersion_ShouldNeverBeForcedAsTheWinner()
    {
        var (table, _) = await SimulateAsync();
        var patched = Row(table, "Game (Japan) (T-En).bps");

        table.SelectedPreviewItem = patched;
        table.CanForce1G1RWinner.Should().BeFalse("a patched version stays outside the 1G1R");
        table.Force1G1RWinner(patched);

        table.Manual1G1RWinners.Should().BeEmpty();
        table.SelectedPreviewItem = Row(table, "Game (Japan).sfc");
        table.CanForce1G1RWinner.Should().BeTrue();
    }

    [Fact]
    public async Task TheChipOfThePatchedVersions_ShouldShowThemAlone()
    {
        var (table, _) = await SimulateAsync();

        _activeFilters.Add(StatsBannerViewModel.PatchedFilterKey);
        table.RefreshFilteredPreviewItems();

        table.FilteredPreviewItems.Select(r => r.SourceName).Should().Equal("Game (Japan) (T-En).bps");
    }

    // =========================================================================
    // Banner
    // =========================================================================

    [Fact]
    public void TheBanner_ShouldCountThePatchedVersionsAndListThePatchesNotApplied()
    {
        var banner = new StatsBannerViewModel(_services.I18n);
        banner.IsPatchedChipVisible.Should().BeFalse();
        banner.IsUnappliedPatchesVisible.Should().BeFalse();

        var unapplied = Enumerable.Range(1, 12)
            .Select(i => new UnappliedPatch(new PatchFile($"patch{i:D2}.ips"), LocalizedMessage.Create("reason.patch_no_crc")))
            .ToList();
        banner.UpdateStats(new ProcessStats { TotalScanned = 3, ValidCount = 2, PatchedCount = 2, UnappliedPatches = unapplied });

        banner.IsPatchedChipVisible.Should().BeTrue();
        banner.PatchedChip.Text.Should().Be("Patchées : 2");
        banner.IsUnappliedPatchesVisible.Should().BeTrue();
        banner.UnappliedPatchesText.Should().Be("12 patchs non appliqués");
        var lines = banner.UnappliedPatchesTooltip.Split(Environment.NewLine);
        lines.Should().HaveCount(1 + StatsBannerViewModel.MaxListedUnappliedPatches + 1);
        lines[1].Should().StartWith("patch01.ips : Le nom d'un patch IPS doit donner");
        lines[^1].Should().Be("… et 2 autres (voir le journal)");
        StatsBannerViewModel.IsStatusFilterKey(StatsBannerViewModel.PatchedFilterKey).Should().BeFalse();
    }

    [Fact]
    public void ARunWithoutPatchedVersion_ShouldDropTheChipAndItsFilter()
    {
        var banner = new StatsBannerViewModel(_services.I18n);
        IReadOnlySet<string>? filters = null;
        banner.FilterChanged += (_, active) => filters = active;
        banner.UpdateStats(new ProcessStats { TotalScanned = 1, ValidCount = 1, PatchedCount = 1 });
        banner.ToggleChip(StatsBannerViewModel.PatchedFilterKey);
        banner.PatchedChip.IsChecked.Should().BeTrue();

        banner.UpdateStats(new ProcessStats { TotalScanned = 1, ValidCount = 1 });

        banner.IsPatchedChipVisible.Should().BeFalse();
        banner.PatchedChip.IsChecked.Should().BeFalse();
        filters.Should().NotBeNull().And.NotContain(StatsBannerViewModel.PatchedFilterKey);
    }
}
