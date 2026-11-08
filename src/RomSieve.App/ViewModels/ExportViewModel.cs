using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RomSieve.App.Abstractions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Exports of the audit reports (JSON / CSV), of the DAT catalogs (Logiqx XML / ClrMamePro) and of the
/// missing ROMs (Miss List TXT / CSV, or fixdat), from the results of the last run, and of the filtered DAT of the
/// catalog, chosen on the DAT alone with the current options, without a run.
/// </summary>
public sealed partial class ExportViewModel : ViewModelBase
{
    private readonly ISortProcessorService _processorService;
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localization;
    private readonly Func<IReadOnlyList<ProcessItem>> _getItems;
    private readonly Func<ProcessStats?> _getStats;
    private readonly Func<DatCatalog?> _getDatCatalog;
    private readonly Func<IReadOnlyList<DatRomEntry>> _getMissingRoms;
    private readonly Func<bool> _canExportReport;
    private readonly Func<bool> _canExportDat;
    private readonly Func<bool> _canExportMissList;
    private readonly Action<LogLevel, LocalizedMessage> _log;
    private readonly Func<SortConfig>? _buildConfig;
    private readonly Func<bool> _canExportCatalogDat;
    private readonly Func<IReadOnlyCollection<ProcessItem>?> _getCheckedItems;
    private readonly Func<bool> _isFolderDat;
    private readonly Func<bool> _isDatLot;
    private CancellationTokenSource? _exportCancellation;
    private LocalizedMessage? _exportStep;
    private int _exportCurrent;
    private int _exportTotal;

    public ExportViewModel(
        ISortProcessorService processorService,
        IDialogService dialogService,
        ILocalizationService localization,
        Func<IReadOnlyList<ProcessItem>> getItems,
        Func<ProcessStats?> getStats,
        Func<DatCatalog?> getDatCatalog,
        Func<IReadOnlyList<DatRomEntry>> getMissingRoms,
        Func<bool> canExportReport,
        Func<bool> canExportDat,
        Func<bool> canExportMissList,
        Action<LogLevel, LocalizedMessage> log,
        Func<SortConfig>? buildConfig = null,
        Func<bool>? canExportCatalogDat = null,
        Func<IReadOnlyCollection<ProcessItem>?>? getCheckedItems = null,
        Func<bool>? isFolderDat = null,
        Func<bool>? isDatLot = null)
    {
        _isFolderDat = isFolderDat ?? (() => false);
        _isDatLot = isDatLot ?? (() => false);
        _buildConfig = buildConfig;
        _canExportCatalogDat = canExportCatalogDat ?? (() => false);
        _getCheckedItems = getCheckedItems ?? (() => null);
        _processorService = processorService;
        _dialogService = dialogService;
        _localization = localization;
        _getItems = getItems;
        _getStats = getStats;
        _getDatCatalog = getDatCatalog;
        _getMissingRoms = getMissingRoms;
        _canExportReport = canExportReport;
        _canExportDat = canExportDat;
        _canExportMissList = canExportMissList;
        _log = log;
    }

    public bool CanExportReport => !IsExporting && _canExportReport();
    public bool CanExportDat => !IsExporting && _canExportDat();
    public bool CanExportMissList => !IsExporting && _canExportMissList();

    /// <summary>The filtered DAT of the catalog needs a DAT file, not a run.</summary>
    public bool CanExportCatalogDat => !IsExporting && _buildConfig is not null && _canExportCatalogDat();

    /// <summary>The running export goes through steps without count (the selection of the games of the catalog).</summary>
    [ObservableProperty]
    private bool _isExportProgressIndeterminate;

    /// <summary>A DAT export runs: it may read the files whose checksums the DAT does not give, which takes a while.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelExportCommand))]
    private bool _isExporting;

    /// <summary>Progress of the running export, from 0 to 100.</summary>
    [ObservableProperty]
    private int _exportProgressPercentage;

    /// <summary>Files of the running export already written, out of all of them.</summary>
    [ObservableProperty]
    private string _exportProgressText = string.Empty;

    /// <summary>Name of the file the running export reads.</summary>
    [ObservableProperty]
    private string _exportCurrentFile = string.Empty;

    [RelayCommand(CanExecute = nameof(IsExporting))]
    private void CancelExport() => _exportCancellation?.Cancel();

    public void RefreshTranslations() => UpdateExportProgressText();

    partial void OnIsExportingChanged(bool value) => NotifyCommandsCanExecuteChanged();

    private void ReportExportProgress((int Current, int Total, string Filename) progress)
    {
        // A report posted before the end of the export may arrive after it
        if (!IsExporting)
        {
            return;
        }

        _exportCurrent = progress.Current;
        _exportTotal = progress.Total;
        ExportProgressPercentage = progress.Total == 0 ? 0 : (int)(100L * progress.Current / progress.Total);
        ExportCurrentFile = progress.Filename;
        UpdateExportProgressText();
    }

    private void UpdateExportProgressText() =>
        ExportProgressText = !IsExporting
            ? string.Empty
            : _exportStep is { } step
                ? _localization.Tr("panel4.export_catalog_dat_progress", ("step", _localization.Format(step)))
                : _localization.Tr("panel4.export_dat_progress", ("current", _exportCurrent), ("total", _exportTotal));

    // A step of the selection on the catalog, named, without count
    private void ReportCatalogProgress(SortProgress progress)
    {
        // A step posted before the end of the export may arrive after it: it would be shown by the next DAT export
        if (!IsExporting)
        {
            return;
        }

        _exportStep = progress.Message;
        UpdateExportProgressText();
    }

    /// <summary>
    /// Puts the footer back at rest (no progress, nothing to stop). Called before the dialog that tells the outcome,
    /// which would otherwise open over a footer still showing the export.
    /// </summary>
    private void EndExport()
    {
        _exportCancellation = null;
        _exportStep = null;
        IsExporting = false;
        IsExportProgressIndeterminate = false;
        ExportCurrentFile = string.Empty;
        UpdateExportProgressText();
    }

    /// <summary>The export menu is enabled when at least one of its entries is.</summary>
    public bool CanExportAny => CanExportReport || CanExportDat || CanExportMissList || CanExportCatalogDat;

    public void NotifyCommandsCanExecuteChanged()
    {
        OnPropertyChanged(nameof(CanExportReport));
        OnPropertyChanged(nameof(CanExportDat));
        OnPropertyChanged(nameof(CanExportMissList));
        OnPropertyChanged(nameof(CanExportCatalogDat));
        OnPropertyChanged(nameof(CanExportAny));
        ExportReportCommand.NotifyCanExecuteChanged();
        ExportDatCommand.NotifyCanExecuteChanged();
        ExportMissListCommand.NotifyCanExecuteChanged();
        ExportCatalogDatCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanExportReport))]
    public async Task ExportReportAsync()
    {
        // A copy: a simulation started while the export runs clears the list of the last run
        var items = _getItems().ToList();
        var stats = _getStats();
        if (items.Count == 0 || stats is null)
        {
            return;
        }

        FileDialogFilter[] filters =
        [
            new(_localization.Tr("dialog.export_json_filter"), ["json"]),
            new(_localization.Tr("dialog.export_csv_filter"), ["csv"])
        ];

        var path = await _dialogService.PickSaveFileAsync(
            _localization.Tr("dialog.export_report_title"),
            "romsieve_report.json",
            filters);

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var isCsv = path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
        if (!isCsv && !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            path += ".json";
        }

        try
        {
            // The report alone does not receive the catalog that refuses it
            if (_getDatCatalog()?.IsReadFrom(path) == true)
            {
                throw new InvalidOperationException(_localization.Tr("export.overwrites_dat", ("path", path)));
            }

            if (isCsv)
            {
                await _processorService.ExportReportToCsvAsync(items, path);
            }
            else
            {
                await _processorService.ExportReportToJsonAsync(items, stats, path);
            }

            _log(LogLevel.Ok, LocalizedMessage.Create("log.report_exported_success", ("path", path)));
            await _dialogService.ShowInfoAsync(
                _localization.Tr("dialog.export_success_title"),
                _localization.Tr("dialog.export_success_msg", ("path", path)));
        }
        catch (Exception ex)
        {
            await _dialogService.ShowErrorAsync(
                _localization.Tr("dialog.export_error_title"),
                _localization.Tr("dialog.export_error_msg", ("path", path), ("error", ex.Message)));
        }
    }

    /// <summary>
    /// Writes the DAT of the files the last run kept. While some games are unchecked in the table, only the checked
    /// ones are written, with the MAME sets they need: the same files as the sort of the selection.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportDat))]
    public async Task ExportDatAsync()
    {
        // A copy: a simulation started while the export runs clears the list of the last run
        var checkedItems = _getCheckedItems();
        var items = (checkedItems ?? _getItems()).ToList();
        var datCatalog = _getDatCatalog();
        if (items.Count == 0 && checkedItems is null)
        {
            return;
        }

        var validCount = items.Count(i => i.Status == FileStatus.Valid);
        if (validCount == 0)
        {
            await _dialogService.ShowWarningAsync(
                _localization.Tr("dialog.export_dat_warning_title"),
                _localization.Tr(checkedItems is null ? "dialog.export_dat_no_retained_roms" : "dialog.export_dat_no_checked_roms"));
            return;
        }

        // The DATs of MAME software lists and of a lot go into a folder, a DAT per list or per DAT at its root, in the
        // format of their source
        var inFolder = datCatalog?.IsFolderCatalog == true;
        var isLot = datCatalog?.Dats is not null;
        var (path, fmt) = inFolder
            ? (await PickDatFolderAsync(isLot), isLot ? DatExportFormat.Auto : DatExportFormat.LogiqxXml)
            : await PickDatFileAsync(_localization.Tr("dialog.export_dat_title"), "collection_filtree.xml");
        if (path is null)
        {
            return;
        }

        var existingDats = inFolder ? DatService.GetDatFiles(path) : null;

        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        _exportStep = null;
        IsExporting = true;
        ReportExportProgress((0, validCount, string.Empty));
        try
        {
            // Created on the UI thread, the progress is reported there; the export itself runs aside, so that the
            // window stays responsive even when no file has to be read
            var progress = new Progress<(int Current, int Total, string Filename)>(ReportExportProgress);
            var exportedCatalog = await Task.Run(() => _processorService.ExportDatAsync(
                items,
                path,
                format: fmt,
                datCatalog: datCatalog,
                validOnly: true,
                computeMissingHashes: true,
                progress: progress,
                cancellationToken: cancellation.Token));

            EndExport();
            ReportUntouchedDats(path, existingDats, isLot);
            _log(LogLevel.Ok, LocalizedMessage.Create("dialog.export_dat_success",
                ("count", exportedCatalog.CountFiles()),
                ("path", path)));

            await _dialogService.ShowInfoAsync(
                _localization.Tr("dialog.export_success_title"),
                _localization.Tr("dialog.export_dat_success",
                    ("count", exportedCatalog.CountFiles()),
                    ("path", path)));
        }
        catch (OperationCanceledException)
        {
            // The DAT is written once complete: a stopped export leaves nothing behind
            _log(LogLevel.Warn, LocalizedMessage.Create("log.export_dat_cancelled", ("path", path)));
        }
        catch (Exception ex)
        {
            EndExport();
            await _dialogService.ShowErrorAsync(
                _localization.Tr("dialog.export_error_title"),
                _localization.Tr("dialog.export_error_msg", ("path", path), ("error", ex.Message)));
        }
        finally
        {
            EndExport();
        }
    }

    /// <summary>
    /// Writes the filtered DAT of the catalog: the games of the DAT that the current options keep, chosen on the DAT
    /// alone (<see cref="ISortProcessorService.ExportCatalogDatAsync"/>), whatever the last run. The source is not read.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportCatalogDat))]
    public async Task ExportCatalogDatAsync()
    {
        if (_buildConfig is null)
        {
            return;
        }

        // The options of the moment: a change made while the export runs is for the next one
        var config = _buildConfig();

        // The DATs of MAME software lists and of a lot go into a folder, a DAT per list or per DAT at its root, in the
        // format of their source
        var inFolder = _isFolderDat();
        var isLot = inFolder && _isDatLot();
        var suggestedName = string.IsNullOrWhiteSpace(config.DatFile)
            ? "catalog.xml"
            : Path.GetFileNameWithoutExtension(config.DatFile) + DatCatalog.FilteredDatSuffix + ".xml";
        var (path, fmt) = inFolder
            ? (await PickDatFolderAsync(isLot), isLot ? DatExportFormat.Auto : DatExportFormat.LogiqxXml)
            : await PickDatFileAsync(_localization.Tr("dialog.export_catalog_dat_title"), suggestedName);
        if (path is null)
        {
            return;
        }

        var existingDats = inFolder ? DatService.GetDatFiles(path) : null;

        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        _exportStep = LocalizedMessage.Create("progress.reading_catalog_games");
        IsExportProgressIndeterminate = true;
        ExportProgressPercentage = 0;
        IsExporting = true;
        UpdateExportProgressText();
        try
        {
            // Created on the UI thread, the progress and the logs are reported there; the selection runs aside
            var progress = new Progress<SortProgress>(ReportCatalogProgress);
            var log = new Progress<LogMessage>(message => _log(message.Level, message.Message));
            var result = await Task.Run(() => _processorService.ExportCatalogDatAsync(
                config,
                path,
                fmt,
                progress,
                log,
                cancellation.Token));

            EndExport();
            if (result.KeptCount == 0)
            {
                await _dialogService.ShowWarningAsync(
                    _localization.Tr("dialog.export_catalog_dat_empty_title"),
                    _localization.Tr("dialog.export_catalog_dat_empty"));
                return;
            }

            ReportUntouchedDats(path, existingDats, isLot);
            _log(LogLevel.Ok, LocalizedMessage.Create("log.catalog_dat_exported",
                ("kept", result.KeptCount),
                ("total", result.CandidateCount),
                ("path", path)));
            await _dialogService.ShowInfoAsync(
                _localization.Tr("dialog.export_success_title"),
                _localization.Tr("dialog.export_catalog_dat_success",
                    ("kept", result.KeptCount),
                    ("total", result.CandidateCount),
                    ("path", path)));
        }
        catch (OperationCanceledException)
        {
            // The DAT is written once the selection is over: a stopped export leaves nothing behind
            _log(LogLevel.Warn, LocalizedMessage.Create("log.export_dat_cancelled", ("path", path)));
        }
        catch (Exception ex)
        {
            EndExport();
            await _dialogService.ShowErrorAsync(
                _localization.Tr("dialog.export_error_title"),
                _localization.Tr("dialog.export_error_msg", ("path", path), ("error", ex.Message)));
        }
        finally
        {
            EndExport();
        }
    }

    /// <summary>A DAT file to write and its format, after its extension (Logiqx XML or CLRMAMEPRO); null when cancelled.</summary>
    private async Task<(string? Path, DatExportFormat Format)> PickDatFileAsync(string title, string suggestedName)
    {
        FileDialogFilter[] filters =
        [
            new(_localization.Tr("dialog.export_dat_xml_filter"), ["xml"]),
            new(_localization.Tr("dialog.export_dat_cmp_filter"), ["dat"])
        ];

        var path = await _dialogService.PickSaveFileAsync(title, suggestedName, filters);
        if (string.IsNullOrWhiteSpace(path))
        {
            return (null, DatExportFormat.LogiqxXml);
        }

        var isXml = path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".xml.dat", StringComparison.OrdinalIgnoreCase);
        if (!path.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            path += isXml ? ".xml" : ".dat";
        }

        return (path, isXml ? DatExportFormat.LogiqxXml : DatExportFormat.ClrMamePro);
    }

    /// <summary>
    /// The folder where the DATs of MAME software lists, or of a lot (<paramref name="isLot"/>), are written, a DAT per
    /// list or per DAT at its root, where MAME and a ROM manager look for them; a folder that already holds DATs is
    /// confirmed first. Null when cancelled.
    /// </summary>
    private async Task<string?> PickDatFolderAsync(bool isLot)
    {
        var folder = await _dialogService.PickFolderAsync(_localization.Tr(isLot ? "dialog.export_dat_folder_title" : "dialog.export_softlist_dat_title"));
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        var existing = DatService.GetDatFiles(folder).Keys
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (existing.Count == 0)
        {
            return folder;
        }

        var confirmed = await _dialogService.ShowConfirmationAsync(
            _localization.Tr("dialog.export_softlist_existing_title"),
            _localization.Tr(
                isLot ? "dialog.export_dat_folder_existing" : "dialog.export_softlist_existing",
                ("path", folder),
                ("count", existing.Count),
                ("names", _localization.Format(MameSetDependencies.FormatNames(existing)))));
        return confirmed ? folder : null;
    }

    // The DATs of the folder that the export did not replace stay there, and a ROM manager reads them too
    private void ReportUntouchedDats(string folder, IReadOnlyDictionary<string, DateTime>? before, bool isLot)
    {
        if (before is null || DatService.GetUntouchedDatFiles(before) is not { Count: > 0 } untouched)
        {
            return;
        }

        _log(LogLevel.Warn, LocalizedMessage.Create(
            isLot ? "log.export_dat_folder_untouched" : "log.export_softlist_untouched",
            ("count", untouched.Count),
            ("path", folder),
            ("names", MameSetDependencies.FormatNames(untouched))));
    }

    // The DATs of a lot the Miss List and the fixdat leave out: complete, or without any game found
    private void ReportLotExportScope()
    {
        if (_getStats()?.DescribeLotExportScope() is { } scope)
        {
            _log(LogLevel.Info, scope);
        }
    }

    [RelayCommand(CanExecute = nameof(CanExportMissList))]
    public async Task ExportMissListAsync()
    {
        var missingRoms = _getMissingRoms().ToList();
        var datCatalog = _getDatCatalog();
        if (missingRoms.Count == 0 || datCatalog is null)
        {
            return;
        }

        FileDialogFilter[] filters =
        [
            new(_localization.Tr("dialog.export_txt_filter"), ["txt"]),
            new(_localization.Tr("dialog.export_csv_filter"), ["csv"]),
            new(_localization.Tr("dialog.export_fixdat_filter"), ["xml"])
        ];

        var path = await _dialogService.PickSaveFileAsync(
            _localization.Tr("dialog.export_miss_list_title"),
            "miss_list.txt",
            filters);

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                // Fixdat: a standard DAT of the missing ROMs, for other ROM managers; those of MAME software lists and
                // of a lot go into the folder of the file chosen, a fixdat per list or per DAT
                var writtenPath = datCatalog.IsFolderCatalog ? Path.GetDirectoryName(path) ?? path : path;
                var fixDat = await _processorService.ExportFixDatAsync(missingRoms, datCatalog, writtenPath);
                ReportLotExportScope();
                _log(LogLevel.Ok, LocalizedMessage.Create("dialog.export_fixdat_success",
                    ("games", fixDat.GameCount),
                    ("count", fixDat.CountFiles()),
                    ("path", writtenPath)));
                await _dialogService.ShowInfoAsync(
                    _localization.Tr("dialog.export_success_title"),
                    _localization.Tr("dialog.export_fixdat_success",
                        ("games", fixDat.GameCount),
                        ("count", fixDat.CountFiles()),
                        ("path", writtenPath)));
                return;
            }

            if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                await _processorService.ExportMissListToCsvAsync(missingRoms, datCatalog, path);
            }
            else
            {
                if (!path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                {
                    path += ".txt";
                }

                await _processorService.ExportMissListToTxtAsync(missingRoms, datCatalog, path);
            }

            ReportLotExportScope();
            _log(LogLevel.Ok, LocalizedMessage.Create("dialog.export_miss_list_success",
                ("count", missingRoms.Count),
                ("path", path)));
            await _dialogService.ShowInfoAsync(
                _localization.Tr("dialog.export_success_title"),
                _localization.Tr("dialog.export_miss_list_success",
                    ("count", missingRoms.Count),
                    ("path", path)));
        }
        catch (Exception ex)
        {
            await _dialogService.ShowErrorAsync(
                _localization.Tr("dialog.export_error_title"),
                _localization.Tr("dialog.export_error_msg", ("path", path), ("error", ex.Message)));
        }
    }
}
