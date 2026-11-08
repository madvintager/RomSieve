using System.IO.Hashing;
using System.Text;
using RomSieve.App.ViewModels;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// A folder of DATs being read: read off the UI thread with its progress, it holds back the simulation and the sort
/// (greyed, their tooltip says why) until its DATs are known, whereas the DAT can still be changed, which drops the read.
/// A simulation asked meanwhile waits for the DATs.
/// </summary>
public sealed class DatFolderReadingTests : IDisposable
{
    private const string GameBoy = "Nintendo - Game Boy";
    private static readonly byte[] Tetris = Encoding.ASCII.GetBytes("TETRIS GB");

    private readonly TempDirectoryFixture _tmp = new();
    private readonly CoreTestServices _services = new();
    private readonly GatedFolderDatService _datService;
    private readonly string _dats;
    private readonly string _source;
    private readonly DirectoriesViewModel _directories;
    private readonly ExecutionViewModel _execution;

    public DatFolderReadingTests()
    {
        _datService = new GatedFolderDatService(_services.DatService);
        _dats = _tmp.CreateSubdirectory("dats");
        _tmp.WriteText(Path.Combine("dats", "Nintendo - Game Boy (20230414).dat"), $"""
            <?xml version="1.0"?>
            <datafile>
            	<header><name>{GameBoy}</name><description>{GameBoy}</description><homepage>No-Intro</homepage></header>
            	<game name="Tetris (World)"><description>Tetris (World)</description><rom name="Tetris (World).gb" size="{Tetris.Length}" crc="{Crc32.HashToUInt32(Tetris):x8}"/></game>
            </datafile>
            """);
        _source = _tmp.CreateSubdirectory("roms");
        File.WriteAllBytes(Path.Combine(_source, "Tetris (World).gb"), Tetris);

        _directories = new DirectoriesViewModel(
            _services.I18n, new HeadlessDialogService(), _datService, _services.NoIntroParser, _services.Organizer, _services.MameParser, TimeSpan.Zero);
        _directories.SourceDir = _source;
        _directories.DestinationDir = _tmp.GetFilePath("sorted");

        var journals = _tmp.CreateSubdirectory("journals");
        _execution = new ExecutionViewModel(
            _services.Processor,
            new OperationJournalService(journals, _services.ChecksumService, _services.CrcCache),
            new HeadlessDialogService(),
            _services.I18n,
            _directories,
            new LogViewModel(_services.I18n),
            BuildConfig,
            CaptureSettings,
            () => Task.CompletedTask,
            _ => { },
            _tmp.CreateSubdirectory("logs"));
    }

    public void Dispose() => _tmp.Dispose();

    private SortSettings CaptureSettings()
    {
        var settings = new SortSettings();
        _directories.WriteTo(settings);
        return settings;
    }

    private SortConfig BuildConfig() => SortSettingsSerializer.ToSortConfig(CaptureSettings());

    [Fact]
    public async Task FolderBeingRead_ShouldHoldBackTheSimulationAndTheSort_UntilItsDatsAreKnown()
    {
        _execution.CanStartOperation.Should().BeTrue();
        var gate = _datService.GateFolders();

        _directories.DatFilePath = _dats;

        _directories.IsReadingDatFolder.Should().BeTrue();
        _directories.DatHeaderName.Should().Be("dats", "a folder is named after itself at once");
        _directories.DatInfoText.Should().Be("dats - lecture du DAT...");
        _execution.CanStartOperation.Should().BeFalse();
        _execution.CanRunPrimaryAction.Should().BeFalse();
        _execution.SimulateCommand.CanExecute(null).Should().BeFalse();
        _execution.StartSortCommand.CanExecute(null).Should().BeFalse();
        _execution.PrimaryActionCommand.CanExecute(null).Should().BeFalse();
        _execution.PrimaryActionTooltip.Should().Be(_services.I18n.Tr("panel4.tooltip_wait_dat_folder"));
        _execution.CanUndoLastSort.Should().BeFalse("nothing was sorted");

        gate.SetResult();
        await _directories.DatLoadTask;

        _directories.IsReadingDatFolder.Should().BeFalse();
        _directories.DatLot.IsLot.Should().BeTrue();
        _directories.DatInfoText.Should().StartWith("Lot de 1 DAT (1 No-Intro)");
        _execution.CanStartOperation.Should().BeTrue();
        _execution.SimulateCommand.CanExecute(null).Should().BeTrue();
        _execution.PrimaryActionCommand.CanExecute(null).Should().BeTrue();
        _execution.PrimaryActionTooltip.Should().Be(_services.I18n.Tr("panel4.tooltip_simulate"));
    }

    [Fact]
    public async Task FolderBeingRead_ShouldShowHowManyDatsAreRead()
    {
        var gate = _datService.GateFolders();
        _directories.DatFilePath = _dats;
        _directories.IsDatFolderReadCountKnown.Should().BeFalse("the progress bar runs without a value while the folder is listed");

        // The DATs are read, the end of the read waits for the gate: the reports reach the interface meanwhile
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_directories.DatFolderReadCount < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        _directories.DatFolderReadTotal.Should().Be(1);
        _directories.DatFolderReadCount.Should().Be(1);
        _directories.IsDatFolderReadCountKnown.Should().BeTrue();
        _directories.DatFolderReadText.Should().Be("1 / 1 DAT lus");
        _directories.IsReadingDatFolder.Should().BeTrue();

        gate.SetResult();
        await _directories.DatLoadTask;
        _directories.IsReadingDatFolder.Should().BeFalse("the bar is hidden once the folder is read");
    }

    [Fact]
    public async Task DatChangedWhileAFolderIsRead_ShouldDropTheRead()
    {
        var gate = _datService.GateFolders();
        _directories.DatFilePath = _dats;
        var droppedRead = _directories.DatLoadTask;
        _directories.IsReadingDatFolder.Should().BeTrue();

        _directories.DatFilePath = string.Empty;

        _directories.IsReadingDatFolder.Should().BeFalse("the DAT can be changed while a folder is read");
        _execution.CanStartOperation.Should().BeTrue();
        await droppedRead;
        _directories.DatLot.IsLot.Should().BeFalse("the read dropped shows nothing");
        _directories.DatInfoText.Should().BeEmpty();
        gate.TrySetResult();
    }

    [Fact]
    public async Task SimulationAskedWhileAFolderIsRead_ShouldWaitForItsDats()
    {
        var gate = _datService.GateFolders();
        _directories.DatFilePath = _dats;
        SortExecutionResult? result = null;
        _execution.RunFinished += (_, r) => result = r;

        var simulation = _execution.SimulateAsync();
        await Task.Delay(50);
        simulation.IsCompleted.Should().BeFalse("the simulation waits for the DATs of the folder");
        _execution.IsRunning.Should().BeFalse();

        gate.SetResult();
        await simulation;

        result.Should().NotBeNull();
        _execution.LastItems.Should().ContainSingle(i => i.Status == FileStatus.Valid);
    }

    /// <summary>The real DAT service, whose read of the DATs of a folder, once done, waits for the test to end when it is gated.</summary>
    private sealed class GatedFolderDatService(IDatService inner) : IDatService
    {
        private TaskCompletionSource? _gate;

        public TaskCompletionSource GateFolders() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<DatFolderEntry> ReadDatFolderPreview(
            string folder, bool recursive = false, IProgress<DatFolderReadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var entries = inner.ReadDatFolderPreview(folder, recursive, progress, cancellationToken);
            _gate?.Task.Wait(cancellationToken);
            return entries;
        }

        public Task<DatCatalog> ParseDatFileAsync(string path, CancellationToken cancellationToken = default) =>
            inner.ParseDatFileAsync(path, cancellationToken);

        public Task<DatCatalog> ParseDatFileAsync(string path, DatFolderOptions folderOptions, CancellationToken cancellationToken = default) =>
            inner.ParseDatFileAsync(path, folderOptions, cancellationToken);

        public DatCatalog ReadDatPreview(string path, int maxGames = DatService.PreviewGameCount) => inner.ReadDatPreview(path, maxGames);
        public bool HoldsSeveralDats(string path) => inner.HoldsSeveralDats(path);
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
}
