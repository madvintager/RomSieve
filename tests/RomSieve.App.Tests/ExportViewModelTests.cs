using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

public sealed class ExportViewModelTests : IDisposable
{
    private readonly TempDirectoryFixture _temp;
    private readonly CoreTestServices _services;
    private readonly TestDialogService _dialogService;
    private readonly List<(LogLevel Level, LocalizedMessage Message)> _logs = [];

    public ExportViewModelTests()
    {
        _temp = new TempDirectoryFixture();
        _services = new CoreTestServices("fr");
        _dialogService = new TestDialogService();
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void CanExportProperties_ShouldReflectSupplierFunctions()
    {
        var canReport = false;
        var canDat = false;
        var canMiss = false;

        var vm = new ExportViewModel(
            _services.Processor,
            _dialogService,
            _services.I18n,
            () => [],
            () => null,
            () => null,
            () => [],
            () => canReport,
            () => canDat,
            () => canMiss,
            (lvl, msg) => _logs.Add((lvl, msg)));

        vm.CanExportReport.Should().BeFalse();
        vm.CanExportDat.Should().BeFalse();
        vm.CanExportMissList.Should().BeFalse();

        canReport = true;
        canDat = true;
        canMiss = true;

        var propChangedEvents = new List<string?>();
        vm.PropertyChanged += (_, e) => propChangedEvents.Add(e.PropertyName);

        vm.NotifyCommandsCanExecuteChanged();

        vm.CanExportReport.Should().BeTrue();
        vm.CanExportDat.Should().BeTrue();
        vm.CanExportMissList.Should().BeTrue();

        propChangedEvents.Should().Contain(nameof(ExportViewModel.CanExportReport));
        propChangedEvents.Should().Contain(nameof(ExportViewModel.CanExportDat));
        propChangedEvents.Should().Contain(nameof(ExportViewModel.CanExportMissList));
    }

    [Fact]
    public async Task ExportReportAsync_WhenDialogCancelled_ShouldNotWriteOrLog()
    {
        var item = CreateSampleProcessItem("Game (USA).nes", FileStatus.Valid);
        var stats = new ProcessStats { TotalScanned = 1, ValidCount = 1 };

        _dialogService.PickSaveFileHandler = (_, _, _, _) => null;

        var vm = new ExportViewModel(
            _services.Processor,
            _dialogService,
            _services.I18n,
            () => [item],
            () => stats,
            () => null,
            () => [],
            () => true,
            () => true,
            () => false,
            (lvl, msg) => _logs.Add((lvl, msg)));

        await vm.ExportReportAsync();

        _logs.Should().BeEmpty();
        _dialogService.InfoDialogs.Should().BeEmpty();
    }

    [Fact]
    public async Task ExportReportAsync_JsonAndCsv_ShouldExportFiles()
    {
        var item = CreateSampleProcessItem("Super Game (USA).nes", FileStatus.Valid);
        var stats = new ProcessStats { TotalScanned = 1, ValidCount = 1 };

        var jsonPath = _temp.GetFilePath("report.json");
        _dialogService.PickSaveFileHandler = (_, _, _, _) => jsonPath;

        var vm = new ExportViewModel(
            _services.Processor,
            _dialogService,
            _services.I18n,
            () => [item],
            () => stats,
            () => null,
            () => [],
            () => true,
            () => true,
            () => false,
            (lvl, msg) => _logs.Add((lvl, msg)));

        await vm.ExportReportAsync();

        File.Exists(jsonPath).Should().BeTrue();
        _logs.Should().ContainSingle(l => l.Level == LogLevel.Ok);
        _dialogService.InfoDialogs.Should().ContainSingle();

        // CSV export
        var csvPath = _temp.GetFilePath("report.csv");
        _dialogService.PickSaveFileHandler = (_, _, _, _) => csvPath;

        await vm.ExportReportAsync();

        File.Exists(csvPath).Should().BeTrue();
        (await File.ReadAllTextAsync(csvPath)).Should().Contain("source_file;archive_member");
    }

    [Fact]
    public async Task ExportReportAsync_OverTheDatRead_ShouldBeRefused()
    {
        var item = CreateSampleProcessItem("Super Game (USA).nes", FileStatus.Valid);

        // A DAT read from a file the report would replace
        var datPath = _temp.WriteText("set.json", "DAT");
        var catalog = new DatCatalog { Header = new DatHeader { Name = "Set", SourceFile = datPath } };
        _dialogService.PickSaveFileHandler = (_, _, _, _) => datPath;

        var vm = new ExportViewModel(
            _services.Processor,
            _dialogService,
            _services.I18n,
            () => [item],
            () => new ProcessStats { TotalScanned = 1, ValidCount = 1 },
            () => catalog,
            () => [],
            () => true,
            () => true,
            () => false,
            (lvl, msg) => _logs.Add((lvl, msg)));

        await vm.ExportReportAsync();

        _dialogService.ErrorDialogs.Should().ContainSingle()
            .Which.Message.Should().Contain(_services.I18n.Tr("export.overwrites_dat", ("path", datPath)));
        (await File.ReadAllTextAsync(datPath)).Should().Be("DAT");
        _logs.Should().NotContain(l => l.Level == LogLevel.Ok);
    }

    [Fact]
    public async Task ExportDatAsync_WhenNoValidRoms_ShouldShowWarning()
    {
        var item = CreateSampleProcessItem("Excluded Game (USA).nes", FileStatus.Excluded);

        var vm = new ExportViewModel(
            _services.Processor,
            _dialogService,
            _services.I18n,
            () => [item],
            () => null,
            () => null,
            () => [],
            () => true,
            () => true,
            () => false,
            (lvl, msg) => _logs.Add((lvl, msg)));

        await vm.ExportDatAsync();

        _dialogService.WarningDialogs.Should().ContainSingle();
        _dialogService.InfoDialogs.Should().BeEmpty();
    }

    [Fact]
    public async Task ExportDatAsync_WithValidRoms_ShouldExportXmlAndClrMamePro()
    {
        var filePath = _temp.WriteBytes("Zelda (USA).nes", [1, 2, 3, 4]);
        var item = new ProcessItem
        {
            SourcePath = filePath,
            FileSize = 4,
            Parsed = new ParsedRomName
            {
                RawFilename = "Zelda (USA).nes",
                Title = "Zelda",
                IsTosecConform = true
            },
            Status = FileStatus.Valid,
            Crc32 = "12345678"
        };

        var xmlPath = _temp.GetFilePath("dat_out.xml");
        _dialogService.PickSaveFileHandler = (_, _, _, _) => xmlPath;

        var vm = new ExportViewModel(
            _services.Processor,
            _dialogService,
            _services.I18n,
            () => [item],
            () => null,
            () => null,
            () => [],
            () => true,
            () => true,
            () => false,
            (lvl, msg) => _logs.Add((lvl, msg)));

        await vm.ExportDatAsync();

        File.Exists(xmlPath).Should().BeTrue();
        (await File.ReadAllTextAsync(xmlPath)).Should().Contain("<datafile");
        _dialogService.InfoDialogs.Should().ContainSingle();
    }

    [Fact]
    public async Task ExportDatAsync_WhenASimulationClearsTheLastRun_ShouldExportTheItemsItStartedWith()
    {
        var item = CreateSampleProcessItem("Zelda (USA).nes", FileStatus.Valid);
        var lastItems = new List<ProcessItem> { item };
        var xmlPath = _temp.GetFilePath("dat_out.xml");

        // A simulation started while the export runs clears the list of the last run
        _dialogService.PickSaveFileHandler = (_, _, _, _) =>
        {
            lastItems.Clear();
            return xmlPath;
        };

        var vm = new ExportViewModel(
            _services.Processor,
            _dialogService,
            _services.I18n,
            () => lastItems,
            () => null,
            () => null,
            () => [],
            () => true,
            () => true,
            () => false,
            (lvl, msg) => _logs.Add((lvl, msg)));

        await vm.ExportDatAsync();

        _dialogService.ErrorDialogs.Should().BeEmpty();
        (await File.ReadAllTextAsync(xmlPath)).Should().Contain("Zelda (USA)");
    }

    [Fact]
    public async Task ExportDatAsync_WhileRunning_ShouldShowItsProgressAndStopOnRequest()
    {
        var item = CreateSampleProcessItem("Zelda (USA).nes", FileStatus.Valid);
        var xmlPath = _temp.GetFilePath("dat_out.xml");
        _dialogService.PickSaveFileHandler = (_, _, _, _) => xmlPath;
        var processor = BlockingExportProcessor.Create();

        var vm = new ExportViewModel(
            processor,
            _dialogService,
            _services.I18n,
            () => [item],
            () => null,
            () => null,
            () => [],
            () => true,
            () => true,
            () => true,
            (lvl, msg) => _logs.Add((lvl, msg)));

        var export = vm.ExportDatAsync();
        await ((BlockingExportProcessor)(object)processor).Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // While it runs, the export shows its progress, can be stopped, and no other export starts
        vm.IsExporting.Should().BeTrue();
        vm.ExportProgressText.Should().Be("Export du DAT : 0 / 1 fichiers");
        vm.CanExportReport.Should().BeFalse();
        vm.CanExportMissList.Should().BeFalse();
        vm.CancelExportCommand.CanExecute(null).Should().BeTrue();

        vm.CancelExportCommand.Execute(null);
        await export.WaitAsync(TimeSpan.FromSeconds(10));

        vm.IsExporting.Should().BeFalse();
        vm.ExportProgressText.Should().BeEmpty();
        vm.CanExportReport.Should().BeTrue();
        vm.CancelExportCommand.CanExecute(null).Should().BeFalse();
        _dialogService.ErrorDialogs.Should().BeEmpty();
        _dialogService.InfoDialogs.Should().BeEmpty();
        _logs.Should().ContainSingle(l => l.Level == LogLevel.Warn && l.Message.Key == "log.export_dat_cancelled");
        File.Exists(xmlPath).Should().BeFalse();
    }

    /// <summary>
    /// A processor whose DAT export waits until it is cancelled, and whose filtered DAT of the catalog keeps one game
    /// at once, keeping the progress it was given.
    /// </summary>
    public class BlockingExportProcessor : DispatchProxy
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IProgress<SortProgress>? CatalogProgress { get; private set; }

        public static ISortProcessorService Create() => Create<ISortProcessorService, BlockingExportProcessor>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISortProcessorService.ExportCatalogDatAsync))
            {
                CatalogProgress = (IProgress<SortProgress>?)args![3];
                var filtered = new DatCatalog();
                filtered.AddGame(new DatGame { Name = "Zelda (USA)", Roms = [new DatRomEntry("Zelda (USA).nes", 4, "12345678")] });
                return Task.FromResult(new CatalogSelectionResult(new DatCatalog(), [], filtered));
            }

            if (targetMethod?.Name != nameof(ISortProcessorService.ExportDatAsync))
            {
                throw new NotSupportedException(targetMethod?.Name);
            }

            return WaitForCancellationAsync((CancellationToken)args![^1]!);
        }

        private async Task<DatCatalog> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new DatCatalog();
        }
    }

    [Fact]
    public async Task ExportMissListAsync_TxtAndCsv_ShouldExportFiles()
    {
        var missingRom = new DatRomEntry
        {
            Name = "Missing Game (USA).nes",
            GameName = "Missing Game",
            Size = 100,
            Crc = "deadbeef"
        };
        var catalog = new DatCatalog
        {
            Header = new DatHeader { Name = "Nintendo - NES" }
        };

        var txtPath = _temp.GetFilePath("missing.txt");
        _dialogService.PickSaveFileHandler = (_, _, _, _) => txtPath;

        var vm = new ExportViewModel(
            _services.Processor,
            _dialogService,
            _services.I18n,
            () => [],
            () => null,
            () => catalog,
            () => [missingRom],
            () => false,
            () => false,
            () => true,
            (lvl, msg) => _logs.Add((lvl, msg)));

        await vm.ExportMissListAsync();

        File.Exists(txtPath).Should().BeTrue();
        (await File.ReadAllTextAsync(txtPath)).Should().Contain("Missing Game (USA).nes");

        var csvPath = _temp.GetFilePath("missing.csv");
        _dialogService.PickSaveFileHandler = (_, _, _, _) => csvPath;

        await vm.ExportMissListAsync();

        File.Exists(csvPath).Should().BeTrue();
        (await File.ReadAllTextAsync(csvPath)).Should().Contain("deadbeef");
    }

    [Fact]
    public async Task ExportMissListAsync_XmlFile_ShouldExportAFixDat()
    {
        var catalog = new DatCatalog { Header = new DatHeader { Name = "Nintendo - NES" } };
        var missingRom = new DatRomEntry("Missing Game (USA).nes", 100, "deadbeef", GameName: "Missing Game (USA)");
        catalog.AddGame(new DatGame { Name = "Missing Game (USA)", Roms = [missingRom] });
        catalog.AddGame(new DatGame { Name = "Present Game (USA)", Roms = [new DatRomEntry("Present Game (USA).nes", 100, "cafebabe")] });

        IReadOnlyList<FileDialogFilter>? offeredFilters = null;
        var xmlPath = _temp.GetFilePath("fixdat.xml");
        _dialogService.PickSaveFileHandler = (_, _, filters, _) =>
        {
            offeredFilters = filters;
            return xmlPath;
        };

        var vm = new ExportViewModel(
            _services.Processor,
            _dialogService,
            _services.I18n,
            () => [],
            () => null,
            () => catalog,
            () => [missingRom],
            () => false,
            () => false,
            () => true,
            (lvl, msg) => _logs.Add((lvl, msg)));

        await vm.ExportMissListAsync();

        offeredFilters.Should().Contain(f => f.Extensions.Contains("xml"));
        var fixDat = _services.DatService.ParseLogiqxXml(await File.ReadAllTextAsync(xmlPath));
        fixDat.Header.Name.Should().Be("fixDat_Nintendo - NES");
        fixDat.Entries.Select(e => e.Name).Should().Equal("Missing Game (USA).nes");
        _logs.Should().Contain(l => l.Message.Key == "dialog.export_fixdat_success");
        _dialogService.InfoDialogs.Should().ContainSingle();
    }

    private ProcessItem CreateSampleProcessItem(string fileName, FileStatus status)
    {
        var filePath = _temp.WriteBytes(fileName, [1, 2, 3, 4]);
        return new ProcessItem
        {
            SourcePath = filePath,
            FileSize = 4,
            Parsed = new ParsedRomName
            {
                RawFilename = fileName,
                Title = Path.GetFileNameWithoutExtension(fileName),
                IsTosecConform = true
            },
            Status = status,
            Crc32 = "12345678"
        };
    }

    private string WriteCatalogDat(params string[] gameNames)
    {
        var games = string.Concat(gameNames.Select((name, i) => $"""
            	<game name="{name}"><description>{name}</description><rom name="{name}.sfc" size="{16 + i}" crc="{i + 1:x8}"/></game>

            """));
        return _temp.WriteText("catalog.xml", $"""
            <?xml version="1.0"?>
            <datafile>
            	<header><name>Nintendo - Super Nintendo Entertainment System</name><homepage>No-Intro</homepage></header>
            {games}</datafile>
            """);
    }

    private ExportViewModel CreateCatalogExportViewModel(
        Func<SortConfig> buildConfig,
        Func<bool> canExportCatalogDat,
        Lock logLock,
        bool softwareLists = false) =>
        new(
            _services.Processor,
            _dialogService,
            _services.I18n,
            () => [],
            () => null,
            () => null,
            () => [],
            () => false,
            () => false,
            () => false,
            (lvl, msg) =>
            {
                // The logs of the selection are reported from the thread pool
                lock (logLock)
                {
                    _logs.Add((lvl, msg));
                }
            },
            buildConfig,
            canExportCatalogDat,
            isSoftwareListDat: () => softwareLists);

    [Fact]
    public void CanExportCatalogDat_ShouldNeedADatButNoRun()
    {
        var hasDat = false;
        var vm = CreateCatalogExportViewModel(() => new SortConfig { SourceDir = "", DestinationDir = "" }, () => hasDat, new Lock());

        vm.CanExportCatalogDat.Should().BeFalse();
        vm.CanExportAny.Should().BeFalse();

        hasDat = true;
        vm.NotifyCommandsCanExecuteChanged();

        // Without any result: the other exports stay disabled
        vm.CanExportCatalogDat.Should().BeTrue();
        vm.ExportCatalogDatCommand.CanExecute(null).Should().BeTrue();
        vm.CanExportAny.Should().BeTrue();
        vm.CanExportDat.Should().BeFalse();
    }

    [Fact]
    public async Task ExportCatalogDatAsync_ShouldWriteTheFilteredDatWithoutAnyRun()
    {
        var datPath = WriteCatalogDat("Super Game (USA)", "Space Quest (USA) (Beta)", "Puzzle Time (France)");
        var outputPath = _temp.GetFilePath("filtered.dat");
        string? suggestedName = null;
        _dialogService.PickSaveFileHandler = (_, name, _, _) =>
        {
            suggestedName = name;
            return outputPath;
        };
        var vm = CreateCatalogExportViewModel(
            () => new SortConfig
            {
                SourceDir = _temp.GetFilePath("no-source"),
                DestinationDir = string.Empty,
                DatFile = datPath,
                Filters = new FilterConfig { NoIntroExcludeDevVersions = true }
            },
            () => true,
            new Lock());

        await vm.ExportCatalogDatAsync();

        suggestedName.Should().Be("catalog (RomSieve).xml");
        var written = await File.ReadAllTextAsync(outputPath);
        written.Should().StartWith("clrmamepro (");
        written.Should().Contain("Super Game (USA)").And.Contain("Puzzle Time (France)").And.NotContain("Space Quest");
        _dialogService.InfoDialogs.Should().ContainSingle()
            .Which.Message.Should().Be(_services.I18n.Tr("dialog.export_catalog_dat_success", ("kept", 2), ("total", 3), ("path", outputPath)));
        vm.IsExporting.Should().BeFalse();
        vm.IsExportProgressIndeterminate.Should().BeFalse();
        vm.ExportProgressText.Should().BeEmpty();
    }

    // The hash folder of MAME, with one of its lists
    private string WriteHashFolder()
    {
        var hash = _temp.CreateSubdirectory("hash");
        File.WriteAllText(Path.Combine(hash, "megadriv.xml"), """
            <?xml version="1.0"?>
            <softwarelist name="megadriv" description="Sega Mega Drive/Genesis cartridges">
            	<software name="sonic"><description>Sonic The Hedgehog (Europe, USA)</description><year>1991</year><publisher>Sega</publisher>
            		<part name="cart" interface="megadriv_cart"><dataarea name="rom" size="16"><rom name="sonic.bin" size="16" crc="12345678"/></dataarea></part>
            	</software>
            </softwarelist>
            """);
        return hash;
    }

    [Fact]
    public async Task ExportCatalogDatAsync_OfSoftwareLists_ShouldWriteADatPerListAtTheRootOfTheFolderChosen()
    {
        var hash = WriteHashFolder();
        var folder = _temp.CreateSubdirectory("romvault");
        var nes = Path.Combine(folder, "nes.xml");
        File.WriteAllText(nes, "<datafile/>");
        File.SetLastWriteTimeUtc(nes, DateTime.UtcNow.AddHours(-1));
        _dialogService.NextPickedFolder = folder;
        var fileAsked = false;
        _dialogService.PickSaveFileHandler = (_, _, _, _) =>
        {
            fileAsked = true;
            return null;
        };
        var vm = CreateCatalogExportViewModel(
            () => new SortConfig { SourceDir = string.Empty, DestinationDir = string.Empty, DatFile = hash },
            () => true,
            new Lock(),
            softwareLists: true);

        await vm.ExportCatalogDatAsync();

        // A folder, not a file: a DAT per list, at its root, where MAME and a ROM manager look for it
        fileAsked.Should().BeFalse();
        _dialogService.PickedFolderTitles.Should().Equal(_services.I18n.Tr("dialog.export_softlist_dat_title"));
        Directory.GetDirectories(folder).Should().BeEmpty();
        File.ReadAllText(Path.Combine(folder, "megadriv.xml")).Should().Contain("<softwarelist");

        // The DATs already there are told before, and those the export left after
        _dialogService.Confirmations.Should().ContainSingle().Which.Message.Should().Be(_services.I18n.Tr(
            "dialog.export_softlist_existing", ("path", folder), ("count", 1), ("names", "nes.xml")));
        _logs.Should().Contain(l => l.Level == LogLevel.Warn && l.Message.Key == "log.export_softlist_untouched");
        _dialogService.InfoDialogs.Should().ContainSingle().Which.Message.Should().Contain(folder);
    }

    [Fact]
    public async Task ExportCatalogDatAsync_OfSoftwareLists_ShouldWriteNothingIntoAFolderOfDatsNotConfirmed()
    {
        var hash = WriteHashFolder();
        var folder = _temp.CreateSubdirectory("romvault");
        File.WriteAllText(Path.Combine(folder, "megadriv.xml"), "<datafile/>");
        _dialogService.NextPickedFolder = folder;
        _dialogService.ConfirmationResult = false;
        var vm = CreateCatalogExportViewModel(
            () => new SortConfig { SourceDir = string.Empty, DestinationDir = string.Empty, DatFile = hash },
            () => true,
            new Lock(),
            softwareLists: true);

        await vm.ExportCatalogDatAsync();

        File.ReadAllText(Path.Combine(folder, "megadriv.xml")).Should().Be("<datafile/>");
        _dialogService.InfoDialogs.Should().BeEmpty();
    }

    [Fact]
    public async Task ExportCatalogDatAsync_KeepingNoGame_ShouldWarnAndWriteNothing()
    {
        var datPath = WriteCatalogDat("Space Quest (USA) (Beta)");
        var outputPath = _temp.GetFilePath("filtered.xml");
        _dialogService.PickSaveFileHandler = (_, _, _, _) => outputPath;
        var vm = CreateCatalogExportViewModel(
            () => new SortConfig
            {
                SourceDir = string.Empty,
                DestinationDir = string.Empty,
                DatFile = datPath,
                Filters = new FilterConfig { NoIntroExcludeDevVersions = true }
            },
            () => true,
            new Lock());

        await vm.ExportCatalogDatAsync();

        File.Exists(outputPath).Should().BeFalse();
        _dialogService.WarningDialogs.Should().ContainSingle()
            .Which.Message.Should().Be(_services.I18n.Tr("dialog.export_catalog_dat_empty"));
        _dialogService.InfoDialogs.Should().BeEmpty();
    }

    /// <summary>What the footer shows of an export: nothing once it is over.</summary>
    private sealed record FooterState(bool IsExporting, bool IsIndeterminate, string ProgressText, bool CanStop)
    {
        public static FooterState Of(ExportViewModel vm) =>
            new(vm.IsExporting, vm.IsExportProgressIndeterminate, vm.ExportProgressText, vm.CancelExportCommand.CanExecute(null));

        public static FooterState AtRest { get; } = new(false, false, string.Empty, false);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("nothing kept")]
    [InlineData("error")]
    public async Task ExportCatalogDatAsync_ShouldPutTheFooterAtRestBeforeTellingTheOutcome(string outcome)
    {
        var datPath = outcome == "nothing kept"
            ? WriteCatalogDat("Space Quest (USA) (Beta)")
            : WriteCatalogDat("Super Game (USA)");
        // A directory cannot be written as a file
        var outputPath = _temp.GetFilePath("filtered.xml");
        if (outcome == "error")
        {
            Directory.CreateDirectory(outputPath);
        }

        _dialogService.PickSaveFileHandler = (_, _, _, _) => outputPath;
        var vm = CreateCatalogExportViewModel(
            () => new SortConfig
            {
                SourceDir = string.Empty,
                DestinationDir = string.Empty,
                DatFile = datPath,
                Filters = new FilterConfig { NoIntroExcludeDevVersions = true }
            },
            () => true,
            new Lock());
        var shown = new List<FooterState>();
        _dialogService.OnMessageShown = () => shown.Add(FooterState.Of(vm));

        await vm.ExportCatalogDatAsync();

        shown.Should().ContainSingle().Which.Should().Be(FooterState.AtRest);
        (outcome switch
        {
            "success" => _dialogService.InfoDialogs,
            "nothing kept" => _dialogService.WarningDialogs,
            _ => _dialogService.ErrorDialogs
        }).Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportDatAsync_ShouldPutTheFooterAtRestBeforeTellingTheOutcome(bool fails)
    {
        var item = CreateSampleProcessItem("Zelda (USA).nes", FileStatus.Valid);
        var xmlPath = _temp.GetFilePath("dat_out.xml");
        if (fails)
        {
            Directory.CreateDirectory(xmlPath);
        }

        _dialogService.PickSaveFileHandler = (_, _, _, _) => xmlPath;
        var vm = new ExportViewModel(
            _services.Processor,
            _dialogService,
            _services.I18n,
            () => [item],
            () => null,
            () => null,
            () => [],
            () => true,
            () => true,
            () => false,
            (lvl, msg) => _logs.Add((lvl, msg)));
        var shown = new List<FooterState>();
        _dialogService.OnMessageShown = () => shown.Add(FooterState.Of(vm));

        await vm.ExportDatAsync();

        shown.Should().ContainSingle().Which.Should().Be(FooterState.AtRest);
        (fails ? _dialogService.ErrorDialogs : _dialogService.InfoDialogs).Should().ContainSingle();
    }

    [Fact]
    public async Task ExportCatalogDatAsync_AStepReportedAfterTheEnd_ShouldNotShowInTheNextDatExport()
    {
        var item = CreateSampleProcessItem("Zelda (USA).nes", FileStatus.Valid);
        var outputPath = _temp.GetFilePath("filtered.xml");
        _dialogService.PickSaveFileHandler = (_, _, _, _) => outputPath;
        var processor = BlockingExportProcessor.Create();
        var proxy = (BlockingExportProcessor)(object)processor;
        var vm = new ExportViewModel(
            processor,
            _dialogService,
            _services.I18n,
            () => [item],
            () => null,
            () => null,
            () => [],
            () => true,
            () => true,
            () => false,
            (lvl, msg) => _logs.Add((lvl, msg)),
            () => new SortConfig { SourceDir = string.Empty, DestinationDir = string.Empty, DatFile = "catalog.xml" },
            () => true);

        await vm.ExportCatalogDatAsync();

        // The selection reported a step that is only delivered once the export is over
        var progress = (Progress<SortProgress>)proxy.CatalogProgress!;
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        progress.ProgressChanged += (_, _) => delivered.TrySetResult();
        ((IProgress<SortProgress>)progress).Report(new SortProgress(0, 0, 0, 0, 0, LocalizedMessage.Create("progress.reading_catalog_games")));
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        vm.ExportProgressText.Should().BeEmpty();

        var export = vm.ExportDatAsync();
        await proxy.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        vm.ExportProgressText.Should().Be("Export du DAT : 0 / 1 fichiers");

        vm.CancelExportCommand.Execute(null);
        await export.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class TestDialogService : IDialogService
    {
        public Func<string, string, IReadOnlyList<FileDialogFilter>?, string?, string?>? PickSaveFileHandler { get; set; }
        public List<(string Title, string Message)> InfoDialogs { get; } = [];
        public List<(string Title, string Message)> WarningDialogs { get; } = [];
        public List<(string Title, string Message)> ErrorDialogs { get; } = [];

        /// <summary>Called when any message opens, to look at the window behind it.</summary>
        public Action? OnMessageShown { get; set; }

        public string? NextPickedFolder { get; set; }
        public List<string> PickedFolderTitles { get; } = [];
        public bool ConfirmationResult { get; set; } = true;
        public List<(string Title, string Message)> Confirmations { get; } = [];

        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null)
        {
            PickedFolderTitles.Add(title);
            return Task.FromResult(NextPickedFolder);
        }

        public Task<string?> PickOpenFileAsync(
            string title,
            IReadOnlyList<FileDialogFilter>? filters = null,
            string? initialDirectory = null) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(
            string title,
            string defaultFileName,
            IReadOnlyList<FileDialogFilter>? filters = null,
            string? initialDirectory = null) =>
            Task.FromResult(PickSaveFileHandler?.Invoke(title, defaultFileName, filters, initialDirectory));

        public Task ShowInfoAsync(string title, string message)
        {
            InfoDialogs.Add((title, message));
            OnMessageShown?.Invoke();
            return Task.CompletedTask;
        }

        public Task ShowWarningAsync(string title, string message)
        {
            WarningDialogs.Add((title, message));
            OnMessageShown?.Invoke();
            return Task.CompletedTask;
        }

        public Task ShowErrorAsync(string title, string message)
        {
            ErrorDialogs.Add((title, message));
            OnMessageShown?.Invoke();
            return Task.CompletedTask;
        }

        public Task<bool> ShowConfirmationAsync(string title, string message)
        {
            Confirmations.Add((title, message));
            return Task.FromResult(ConfirmationResult);
        }

        public Task OpenUrlAsync(string url) =>
            Task.CompletedTask;

        public Task<bool> OpenFileAsync(string path) =>
            Task.FromResult(false);

        public Task ShowPreferencesAsync(PreferencesViewModel preferences) =>
            Task.CompletedTask;
    }
}
