using RomSieve.Core.Abstractions;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// MAME convention in the interface: offered in the list, recognized from a MAME DAT (whose ROM names say nothing),
/// shown by its badge, saved in the options, and showing the No-Intro / Redump filter options.
/// </summary>
public sealed class MameConventionViewModelTests : IDisposable
{
    private readonly TempDirectoryFixture _tmp = new();
    private readonly CoreTestServices _services = new();

    public void Dispose() => _tmp.Dispose();

    private DirectoriesViewModel CreateDirectories() =>
        new(_services.I18n, new HeadlessDialogService(), _services.DatService, _services.NoIntroParser, _services.Organizer, _services.MameParser, TimeSpan.Zero);

    [Fact]
    public void MameDat_ShouldBeDetectedAndShownByItsBadge()
    {
        var dat = _tmp.WriteText("mame.xml", """
            <?xml version="1.0"?>
            <mame build="0.289 (mame0289)">
            	<machine name="sf2"><description>Street Fighter II: The World Warrior (World 910522)</description><rom name="sf2e_30g.11e" size="131072" crc="fe39ee33"/><driver status="good"/></machine>
            </mame>
            """);
        var directories = CreateDirectories();

        directories.DatFilePath = dat;

        directories.DetectedDatConvention.Should().Be(NamingConvention.Mame);
        directories.EffectiveConvention.Should().Be(NamingConvention.Mame);
        directories.ConventionBadgeTone.Should().Be("Mame");
        directories.ConventionBadgeText.Should().Be(_services.I18n.Tr("tab1.badge_mame_detected"));
    }

    [Fact]
    public async Task MechanicalMachines_KeptButWithoutAStatusKept_ShouldBeSaidToBeAllExcluded()
    {
        var dat = _tmp.WriteText("mame.xml", """
            <?xml version="1.0"?>
            <mame build="0.289 (mame0289)">
            	<machine name="sf2"><description>Street Fighter II: The World Warrior (World 910522)</description><rom name="sf2e_30g.11e" size="131072" crc="fe39ee33"/><driver status="good" emulation="good"/></machine>
            	<machine name="3lilpigs" ismechanical="yes"><description>3 Lil' Pigs</description><rom name="3lilpigs.bin" size="65536" crc="ce8b3a1e"/><driver status="preliminary" emulation="preliminary"/></machine>
            </mame>
            """);
        var vm = TestMainViewModel.Create(new HeadlessDialogService(), _tmp.GetFilePath("config.json"));
        var filters = vm.Filters;
        filters.KeepMameMechanical = true;
        filters.KeepMameDriverPreliminary = false;
        filters.IsMechanicalNoneKeptShown.Should().BeFalse("nothing is known before the DAT is read");
        filters.HasEmulationWarning.Should().BeFalse();

        vm.Directories.DatFilePath = dat;
        await vm.Directories.DatLoadTask;
        filters.IsMechanicalNoneKeptShown.Should().BeTrue();
        filters.EmulationWarningText.Should().Be("Aucune machine mécanique du DAT n'a ces statuts.");

        filters.MameKeepPlayable = true;
        filters.IsMechanicalNoneKeptShown.Should().BeTrue("the core of the pinball is not emulated either");
        filters.KeepMameDriverPreliminary = true;
        filters.IsMechanicalNoneKeptShown.Should().BeFalse();

        // The machines that do not work yet only: the pinball is kept
        filters.KeepMameDriverGood = false;
        filters.KeepMameDriverImperfect = false;
        filters.IsMechanicalNoneKeptShown.Should().BeFalse();

        filters.KeepMameDriverGood = true;
        filters.KeepMameDriverPreliminary = false;
        filters.MameKeepPlayable = false;
        filters.KeepMameMechanical = false;
        filters.IsMechanicalNoneKeptShown.Should().BeFalse("the box does not keep them");
        filters.KeepMameMechanical = true;
        filters.IsMechanicalNoneKeptShown.Should().BeTrue();

        vm.Directories.DatFilePath = string.Empty;
        filters.IsMechanicalNoneKeptShown.Should().BeFalse();
    }

    [Fact]
    public void MameConvention_ShouldBeOfferedAndSaved()
    {
        var directories = CreateDirectories();

        directories.NamingConventionOptions.Select(o => o.Value).Should().Contain(NamingConvention.Mame);
        directories.SelectedNamingConvention = NamingConvention.Mame;
        directories.ConventionBadgeText.Should().Be(_services.I18n.Tr("tab1.badge_mame"));

        var settings = new SortSettings();
        directories.WriteTo(settings);
        var data = new Dictionary<string, object?>();
        SortSettingsSerializer.Write(settings, data);
        data["naming_convention"].Should().Be("mame");
        SortSettingsSerializer.Read(data).NamingConvention.Should().Be(NamingConvention.Mame);
    }

    [Fact]
    public void ChdFolder_ShouldBeOfferedForMameSavedAndCheckedAgainstTheDestination()
    {
        var vm = TestMainViewModel.Create(new HeadlessDialogService(), _tmp.GetFilePath("config.json"));
        var directories = vm.Directories;

        directories.IsMameConvention.Should().BeFalse("the folder only serves MAME sets");
        directories.SelectedNamingConvention = NamingConvention.Mame;
        directories.IsMameConvention.Should().BeTrue();

        directories.SourceDir = _tmp.CreateSubdirectory("roms");
        directories.ChdDir = _tmp.CreateSubdirectory(Path.Combine("out", "chd"));
        directories.DestinationDir = _tmp.GetFilePath("out");
        vm.BuildConfigUnchecked().ChdDir.Should().Be(directories.ChdDir);
        vm.Navigation.OutputEntry.IndicatorText.Should().Be(_services.I18n.Tr("nav.state_destination_overlap_chd"));

        // Outside MAME, the folder stays in the configuration but is never read: it is a folder like any other
        directories.SelectedNamingConvention = NamingConvention.Tosec;
        vm.Navigation.OutputEntry.IndicatorText.Should().BeEmpty();
        vm.BuildConfigUnchecked().ChdDir.Should().Be(directories.ChdDir, "it is kept for the next MAME sort");
        directories.SelectedNamingConvention = NamingConvention.Mame;
        vm.Navigation.OutputEntry.IndicatorText.Should().Be(_services.I18n.Tr("nav.state_destination_overlap_chd"));

        directories.DestinationDir = _tmp.GetFilePath("sorted");
        vm.Navigation.OutputEntry.IndicatorText.Should().BeEmpty();

        var settings = new SortSettings();
        directories.WriteTo(settings);
        var data = new Dictionary<string, object?>();
        SortSettingsSerializer.Write(settings, data);
        data["chd_dir"].Should().Be(directories.ChdDir);
    }

    [Fact]
    public void FiltersPage_ShouldShowTheNoIntroOptionsForMame()
    {
        var vm = TestMainViewModel.Create(new HeadlessDialogService(), _tmp.GetFilePath("config.json"));
        vm.Filters.ShowConvention(NamingConvention.Tosec);

        vm.Filters.ShowConvention(NamingConvention.Mame);

        vm.Filters.ShownConventionIndex.Should().Be(FiltersViewModel.NoIntroConventionIndex);
    }

    /// <summary>The real DAT service, whose read of a whole DAT waits for the test when it is gated.</summary>
    private sealed class GatedDatService(IDatService inner) : IDatService
    {
        private readonly Dictionary<string, TaskCompletionSource> _gates = new(StringComparer.Ordinal);

        public int WholeReads { get; private set; }

        public TaskCompletionSource Gate(string path) => _gates[path] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DatCatalog> ParseDatFileAsync(string path, CancellationToken cancellationToken = default)
        {
            WholeReads++;
            if (_gates.TryGetValue(path, out var gate))
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            return await inner.ParseDatFileAsync(path, cancellationToken);
        }

        public async Task<DatCatalog> ParseDatFileAsync(string path, DatFolderOptions folderOptions, CancellationToken cancellationToken = default)
        {
            WholeReads++;
            if (_gates.TryGetValue(path, out var gate))
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            return await inner.ParseDatFileAsync(path, folderOptions, cancellationToken);
        }

        public IReadOnlyList<DatFolderEntry> ReadDatFolderPreview(string folder, bool recursive = false) => inner.ReadDatFolderPreview(folder, recursive);
        public DatCatalog ReadDatPreview(string path, int maxGames = DatService.PreviewGameCount) => inner.ReadDatPreview(path, maxGames);
        public DatCatalog ParseClrMameProText(string text) => inner.ParseClrMameProText(text);
        public DatCatalog ParseLogiqxXml(string text) => inner.ParseLogiqxXml(text);

        public Task<DatCatalog> CreateDatFromProcessItemsAsync(IEnumerable<ProcessItem> items, DatHeader? header = null, bool onlyValid = true,
            bool computeMissingChecksums = true, DatCatalog? datCatalog = null, IProgress<(int Current, int Total, string Filename)>? progress = null,
            CancellationToken cancellationToken = default) =>
            inner.CreateDatFromProcessItemsAsync(items, header, onlyValid, computeMissingChecksums, datCatalog, progress, cancellationToken);

        public Task<DatCatalog> ExportDatFileAsync(IEnumerable<ProcessItem> items, string outputPath, DatExportFormat format = DatExportFormat.LogiqxXml,
            DatHeader? header = null, bool onlyValid = true, bool computeMissingChecksums = true, DatCatalog? datCatalog = null,
            IProgress<(int Current, int Total, string Filename)>? progress = null, CancellationToken cancellationToken = default) =>
            inner.ExportDatFileAsync(items, outputPath, format, header, onlyValid, computeMissingChecksums, datCatalog, progress, cancellationToken);
    }

    private string WriteMameDat(string name = "mame.xml") => _tmp.WriteText(name, """
        <?xml version="1.0"?>
        <mame build="0.289 (mame0289)">
        	<machine name="sf2"><description>Street Fighter II: The World Warrior (World 910522)</description><rom name="sf2e_30g.11e" size="131072" crc="fe39ee33"/><driver status="good"/></machine>
        	<machine name="sf2u" cloneof="sf2" romof="sf2"><description>Street Fighter II: The World Warrior (USA 910206)</description><rom name="sf2u_30a.11e" size="131072" crc="08beb861"/><driver status="good"/></machine>
        </mame>
        """);

    [Fact]
    public async Task MameDat_ShouldBeRecognizedBeforeTheWholeDatIsRead()
    {
        var dat = WriteMameDat();
        var datService = new GatedDatService(_services.DatService);
        var gate = datService.Gate(dat);
        var directories = new DirectoriesViewModel(_services.I18n, new HeadlessDialogService(), datService, _services.NoIntroParser, _services.Organizer, _services.MameParser, TimeSpan.Zero);

        directories.DatFilePath = dat;

        // A MAME DAT of hundreds of megabytes took seconds to read on the UI thread before its frames appeared
        directories.EffectiveConvention.Should().Be(NamingConvention.Mame, "its first machines say it is a MAME DAT");
        directories.IsMameConvention.Should().BeTrue();
        directories.ConventionBadgeText.Should().Be(_services.I18n.Tr("tab1.badge_mame_detected"));
        directories.DatInfoText.Should().Be(_services.I18n.Tr("tab1.dat_info_reading", ("name", "MAME")));
        directories.DatInfoTone.Should().Be("Neutral");
        directories.DatLoadTask.IsCompleted.Should().BeFalse();

        gate.SetResult();
        await directories.DatLoadTask;

        directories.DatInfoText.Should().Be(_services.I18n.Tr("tab1.dat_info_summary", ("name", "MAME"), ("version", "0.289 (mame0289)"), ("count", 2)));
        directories.DatInfoTone.Should().Be("Ok");
        directories.EffectiveConvention.Should().Be(NamingConvention.Mame);
    }

    [Fact]
    public async Task DatChosenAgain_ShouldDropTheOlderRead()
    {
        var mame = WriteMameDat();
        var tosec = _tmp.WriteText("tosec.dat", """
            clrmamepro (
            	name "Commodore Amiga - Games (TOSEC)"
            	version "2024-01-01"
            )
            game (
            	name "Xenon 2 (1989)(Mirrorsoft)"
            	rom ( name "Xenon 2 (1989)(Mirrorsoft).adf" size 901120 crc 11223344 )
            )
            """);
        var datService = new GatedDatService(_services.DatService);
        var gate = datService.Gate(mame);
        var directories = new DirectoriesViewModel(_services.I18n, new HeadlessDialogService(), datService, _services.NoIntroParser, _services.Organizer, _services.MameParser, TimeSpan.Zero);

        directories.DatFilePath = mame;
        var mameRead = directories.DatLoadTask;
        directories.DatFilePath = tosec;
        await directories.DatLoadTask;
        gate.SetResult();
        await mameRead;

        directories.DatInfoText.Should().StartWith("Commodore Amiga - Games (TOSEC)");
        directories.EffectiveConvention.Should().Be(NamingConvention.Tosec);
    }

    [Fact]
    public async Task LanguageChange_ShouldTranslateTheDatSummaryWithoutReadingItAgain()
    {
        var dat = WriteMameDat();
        var datService = new GatedDatService(_services.DatService);
        var services = new CoreTestServices("fr");
        var directories = new DirectoriesViewModel(services.I18n, new HeadlessDialogService(), datService, services.NoIntroParser, services.Organizer, services.MameParser, TimeSpan.Zero);
        directories.DatFilePath = dat;
        await directories.DatLoadTask;
        directories.DatInfoText.Should().Contain("ROMs cataloguées");

        services.I18n.SetLocale("en");
        directories.RefreshTranslations();

        directories.DatInfoText.Should().Be("MAME (v0.289 (mame0289)) - 2 cataloged ROMs");
        datService.WholeReads.Should().Be(1);
    }
}
