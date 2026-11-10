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

        vm.Directories.PatchedPattern.Should().BeEmpty("the patched versions go next to their original ROM by default");

        await vm.Directories.BrowsePatchDirCommand.ExecuteAsync(null);
        vm.Directories.PatchOnly = true;
        vm.Directories.PatchedPattern = "Patchs/{title}";

        vm.Directories.HasPatchDir.Should().BeTrue();
        vm.BuildConfigUnchecked().PatchDir.Should().Be(dialogs.NextPickedFolder);
        vm.BuildConfigUnchecked().PatchOnly.Should().BeTrue();
        vm.BuildConfigUnchecked().PatchedPattern.Should().Be("Patchs/{title}");
        await vm.SaveConfigurationAsync();
        using (var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
        {
            saved.RootElement.GetProperty("patch_dir").GetString().Should().Be(dialogs.NextPickedFolder);
            saved.RootElement.GetProperty("patch_only").GetBoolean().Should().BeTrue();
            saved.RootElement.GetProperty("patched_pattern").GetString().Should().Be("Patchs/{title}");
        }

        var reloaded = TestMainViewModel.Create(new HeadlessDialogService(), path);
        reloaded.Directories.PatchDir.Should().Be(dialogs.NextPickedFolder);
        reloaded.Directories.PatchOnly.Should().BeTrue();
        reloaded.Directories.PatchedPattern.Should().Be("Patchs/{title}");

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

    [Fact]
    public async Task ASimulationIntoThePatchFolder_ShouldBeRefusedBeforeItStarts()
    {
        var dialogs = new HeadlessDialogService();
        var vm = TestMainViewModel.Create(dialogs, _temp.WriteText("config.json", "{}"));
        vm.Directories.SourceDir = _temp.CreateSubdirectory("src");
        _temp.WriteText("src/Game (1990)(Ocean).adf", "game");
        vm.Directories.PatchDir = _temp.CreateSubdirectory("patches");
        vm.Directories.DestinationDir = Path.Combine(_temp.Path, "patches", "sorted");

        await vm.Execution.SimulateAsync();

        dialogs.ShownMessages.Should().ContainSingle(m => m.Message == _services.I18n.Tr("dialog.error_dest_inside_patches"));
        vm.Execution.LastItems.Should().BeEmpty("nothing runs");
        Directory.Exists(Path.Combine(_temp.Path, "patches", "sorted")).Should().BeFalse();
    }

    [Fact]
    public async Task ASimulationWithARefusedPatternOfThePatchedVersions_ShouldBeRefusedBeforeItStarts()
    {
        var dialogs = new HeadlessDialogService();
        var vm = TestMainViewModel.Create(dialogs, _temp.WriteText("config.json", "{}"));
        vm.Directories.SourceDir = _temp.CreateSubdirectory("src");
        _temp.WriteText("src/Game (1990)(Ocean).adf", "game");
        vm.Directories.PatchDir = _temp.CreateSubdirectory("patches");
        vm.Directories.DestinationDir = Path.Combine(_temp.Path, "out");
        vm.Directories.PatchedPattern = "../Patchs";

        await vm.Execution.SimulateAsync();

        dialogs.ShownMessages.Should().ContainSingle(m => m.Message == _services.I18n.Tr("dialog.error_patched_pattern_invalid"));
        vm.Execution.LastItems.Should().BeEmpty("nothing runs");

        // Without a patch folder, the pattern is greyed out and never blocks a run
        dialogs.ShownMessages.Clear();
        vm.Directories.PatchDir = string.Empty;
        await vm.Execution.SimulateAsync();
        dialogs.ShownMessages.Should().NotContain(m => m.Message == _services.I18n.Tr("dialog.error_patched_pattern_invalid"));
        vm.Execution.LastItems.Should().NotBeEmpty();
    }

    // =========================================================================
    // The box that applies the patches
    // =========================================================================

    [Fact]
    public async Task TheBoxToApplyThePatches_ShouldKeepTheFolder_AndBeSavedAndReadBack()
    {
        var path = _temp.WriteText("config.json", "{}");
        var dialogs = new HeadlessDialogService { NextPickedFolder = _temp.CreateSubdirectory("patches") };
        var vm = TestMainViewModel.Create(dialogs, path);
        vm.Directories.ApplyPatches.Should().BeTrue("the patches of a folder are applied by default");
        vm.Directories.UsesPatches.Should().BeFalse("no folder");

        vm.Directories.PatchDir = dialogs.NextPickedFolder;
        vm.Directories.UsesPatches.Should().BeTrue();
        vm.Directories.IsPatchedPatternEnabled.Should().BeTrue();

        vm.Directories.ApplyPatches = false;

        vm.Directories.PatchDir.Should().Be(dialogs.NextPickedFolder, "the folder stays for later");
        vm.Directories.UsesPatches.Should().BeFalse("the option to write the patched versions only is greyed out");
        vm.Directories.IsPatchedPatternEnabled.Should().BeFalse();
        var config = vm.BuildConfigUnchecked();
        config.ApplyPatches.Should().BeFalse();
        config.PatchDir.Should().Be(dialogs.NextPickedFolder);
        await vm.SaveConfigurationAsync();
        using (var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
        {
            saved.RootElement.GetProperty("apply_patches").GetBoolean().Should().BeFalse();
            saved.RootElement.GetProperty("patch_dir").GetString().Should().Be(dialogs.NextPickedFolder);
        }

        var reloaded = TestMainViewModel.Create(new HeadlessDialogService { NextPickedFolder = dialogs.NextPickedFolder }, path);
        reloaded.Directories.ApplyPatches.Should().BeFalse();
        reloaded.Directories.PatchDir.Should().Be(dialogs.NextPickedFolder);

        // Choosing a folder of patches asks for them
        await reloaded.Directories.BrowsePatchDirCommand.ExecuteAsync(null);
        reloaded.Directories.ApplyPatches.Should().BeTrue();
        reloaded.Directories.UsesPatches.Should().BeTrue();
    }

    [Fact]
    public async Task ThePatchesOff_ShouldLeaveThePatternOfThePatchedVersionsAlone_ButNotTheOverlapWithTheDestination()
    {
        var dialogs = new HeadlessDialogService();
        var vm = TestMainViewModel.Create(dialogs, _temp.WriteText("config.json", "{}"));
        vm.Directories.SourceDir = _temp.CreateSubdirectory("src");
        _temp.WriteText("src/Game (1990)(Ocean).adf", "game");
        vm.Directories.PatchDir = _temp.CreateSubdirectory("patches");
        vm.Directories.DestinationDir = Path.Combine(_temp.Path, "out");
        vm.Directories.PatchedPattern = "../Patchs";
        vm.Navigation.OutputEntry.IndicatorText.Should().Be("Patchées : dossier refusé");

        vm.Directories.ApplyPatches = false;

        vm.Navigation.OutputEntry.IndicatorTone.Should().Be(StepIndicatorTone.None, "nothing patched is written");
        vm.Directories.Preview.HasPatchedExample.Should().BeFalse();
        await vm.Execution.SimulateAsync();
        dialogs.ShownMessages.Should().BeEmpty();
        vm.Execution.LastItems.Should().NotBeEmpty();
        vm.Execution.IsSimulationCurrent.Should().BeTrue();

        vm.Directories.ApplyPatches = true;
        vm.Execution.IsSimulationCurrent.Should().BeFalse("the run would apply the patches");
        vm.Directories.ApplyPatches = false;

        // A purge of a destination that holds the patches would put them in quarantine, applied or not
        vm.Directories.DestinationDir = Path.Combine(_temp.Path, "patches", "sorted");
        vm.Navigation.OutputEntry.IndicatorText.Should().Be("Chevauche les patchs");
        await vm.Execution.SimulateAsync();
        dialogs.ShownMessages.Should().ContainSingle(m => m.Message == _services.I18n.Tr("dialog.error_dest_inside_patches"));
    }

    // =========================================================================
    // Table
    // =========================================================================

    /// <summary>
    /// A Japanese and an American release of a game, the American one kept, and a translation of the Japanese one; with
    /// <paramref name="inArchives"/>, the Japanese release and its translation are each the member of a ZIP archive.
    /// </summary>
    private async Task<(PreviewTableViewModel Table, SortExecutionResult Result)> SimulateAsync(bool inArchives = false)
    {
        var translation = PatchTestEncoder.BpsLinear(Japan, [.. Japan, .. "English"u8.ToArray()]);
        if (inArchives)
        {
            WriteZip("src", "Game (Japan).sfc", Japan);
            WriteZip("patches", "Game (Japan) (T-En).bps", translation);
        }
        else
        {
            _temp.WriteBytes("src/Game (Japan).sfc", Japan);
            _temp.WriteBytes("patches/Game (Japan) (T-En).bps", translation);
        }
        _temp.WriteBytes("src/Game (USA).sfc", Usa);
        var dat = _temp.WriteText("dat/snes.dat", $"""
            <?xml version="1.0"?><datafile><header><name>Nintendo - SNES</name><homepage>No-Intro</homepage></header>
            <game name="Game (Japan)"><description>Game (Japan)</description><rom name="Game (Japan).sfc" size="{Japan.Length}" crc="{PatchTestEncoder.Crc(Japan):x8}"/></game>
            <game name="Game (USA)"><description>Game (USA)</description><rom name="Game (USA).sfc" size="{Usa.Length}" crc="{PatchTestEncoder.Crc(Usa):x8}"/></game></datafile>
            """);
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
        var patchPath = Path.Combine(_temp.Path, "patches", "Game (Japan) (T-En).bps");
        var originPath = Path.Combine(_temp.Path, "src", "Game (Japan).sfc");

        var patched = Row(table, "Game (Japan) (T-En).bps");
        patched.IsPatched.Should().BeTrue();
        patched.SourcePathDisplay.Should().Be(originPath, "the bytes of a patched version come from its original ROM");
        patched.DetailPatchLabel.Should().Be("Patch");
        patched.DetailPatchText.Should().Be($"{patchPath} (BPS), appliqué à Game (Japan).sfc (CRC32 {PatchTestEncoder.Crc(Japan):X8})");

        var origin = Row(table, "Game (Japan).sfc");
        origin.IsPatched.Should().BeFalse();
        origin.SourcePathDisplay.Should().Be(originPath);
        origin.DetailPatchLabel.Should().Be("Versions patchées");
        origin.DetailPatchText.Should().Be("Game (Japan) [T-En].sfc");
        Row(table, "Game (USA).sfc").DetailPatchText.Should().BeEmpty();
    }

    [Fact]
    public async Task APatchedVersionFromArchives_ShouldShowTheMemberOfTheRomAndOfThePatchInTheDetail()
    {
        var (table, _) = await SimulateAsync(inArchives: true);
        var romZip = Path.Combine(_temp.Path, "src", "Game (Japan).zip");
        var patchZip = Path.Combine(_temp.Path, "patches", "Game (Japan) (T-En).zip");

        var patched = Row(table, "Game (Japan) (T-En).bps");
        patched.SourcePathDisplay.Should().Be($"{romZip} › Game (Japan).sfc");
        patched.DetailPatchText.Should().StartWith($"{patchZip} › Game (Japan) (T-En).bps (BPS), appliqué à Game (Japan).sfc");
    }

    private void WriteZip(string folder, string member, byte[] content)
    {
        var path = Path.Combine(_temp.CreateSubdirectory(folder), Path.ChangeExtension(member, ".zip"));
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        using var stream = zip.CreateEntry(member).Open();
        stream.Write(content);
    }

    [Fact]
    public async Task AnIpsMatchedByItsReadme_ShouldNameTheReadmeInTheDetail()
    {
        _temp.WriteBytes("patches/Game (USA) (Hack).ips", PatchTestEncoder.IpsDiff(Usa, [.. Usa, .. "Hack"u8.ToArray()]));
        _temp.WriteText("patches/Game (USA) (Hack).txt", $"ROM CRC32: {PatchTestEncoder.Crc(Usa):X8}");

        var (table, _) = await SimulateAsync();

        Row(table, "Game (USA) (Hack).ips").DetailPatchText.Should().Be(
            $"{Path.Combine(_temp.Path, "patches", "Game (USA) (Hack).ips")} (IPS), appliqué à Game (USA).sfc (CRC32 {PatchTestEncoder.Crc(Usa):X8}, lu dans Game (USA) (Hack).txt)");
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
        lines[1].Should().StartWith("patch01.ips : Ni le nom du patch IPS ni un texte qui l'accompagne");
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
