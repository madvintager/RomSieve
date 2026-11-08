using System.CommandLine;
using System.CommandLine.Help;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;

namespace RomSieve.Cli;

/// <summary>
/// Command line of RomSieve: <c>run</c> (simulation by default, real sort with <c>--execute</c>), <c>undo</c>
/// (undo of the last real sort), <c>dat</c> (filtered DAT of the catalog alone, without source), <c>quarantine</c> (size of the quarantine of the purges of a destination, emptied
/// with <c>--empty --yes</c>) and <c>profiles</c> (list of the configuration profiles). The options come from
/// <c>config.json</c> or from a profile saved by the interface, read by <see cref="SortSettingsSerializer"/> as
/// the interface reads them; the messages are translated (<c>--lang</c>).
/// </summary>
public sealed class CliApplication
{
    private static readonly string[] TransferModeNames = ["copy", "move", "hardlink", "symlink", "zip"];

    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly string _defaultConfigPath;
    private readonly LocalizationService _localization = new();
    private readonly ConfigurationService _configService = new();
    private readonly Lock _writeLock = new();
    private CultureInfo _culture = CultureInfo.InvariantCulture;

    private readonly Option<string?> _configOption = new("--config") { Recursive = true };
    private readonly Option<string?> _langOption = new("--lang") { Recursive = true };
    private readonly Option<bool> _quietOption = new("--quiet", "-q") { Recursive = true };
    private readonly Option<string?> _profileOption = new("--profile", "-p");
    private readonly Option<string?> _sourceOption = new("--source", "-s");
    private readonly Option<string?> _destinationOption = new("--destination", "-d");
    private readonly Option<string?> _datOption = new("--dat");
    private readonly Option<bool> _datRecursiveOption = new("--dat-recursive");
    private readonly Option<bool> _datMirrorOption = new("--dir-dat-mirror");
    private readonly Option<string[]> _datOnlyOption = new("--dat-only") { AllowMultipleArgumentsPerToken = false };
    private readonly Option<string?> _chdOption = new("--chd");
    private readonly Option<string?> _categoriesOption = new("--categories");
    private readonly Option<string?> _ratingsOption = new("--ratings");
    private readonly Option<string?> _modeOption = new("--mode");
    private readonly Option<bool> _executeOption = new("--execute");
    private readonly Option<bool> _appendOption = new("--append");
    private readonly Option<bool> _purgeOption = new("--purge");
    private readonly Option<bool> _yesOption = new("--yes", "-y");
    private readonly Option<bool> _forceForeignPurgeOption = new("--force-foreign-purge");
    private readonly Option<string?> _reportOption = new("--report");
    private readonly Option<string?> _missListOption = new("--miss-list");
    private readonly Option<string?> _fixDatOption = new("--fixdat");
    private readonly Option<bool> _emptyOption = new("--empty");
    private readonly Option<string> _outputOption = new("--output", "-o") { Required = true };

    /// <param name="output">Messages and logs.</param>
    /// <param name="error">Errors that stop a command.</param>
    /// <param name="defaultConfigPath">Configuration file used without <c>--config</c> (by default, the one of the interface).</param>
    public CliApplication(TextWriter output, TextWriter error, string? defaultConfigPath = null)
    {
        _output = output;
        _error = error;
        _defaultConfigPath = Path.GetFullPath(defaultConfigPath ?? _configService.GetDefaultConfigPath());
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        SetLanguage(args);
        var root = BuildCommands();
        var parseResult = root.Parse(args);
        if (parseResult.Errors.Count > 0)
        {
            foreach (var error in parseResult.Errors)
            {
                _error.WriteLine(error.Message);
            }

            _error.WriteLine(Tr("cli.see_help"));
            return ExitCodes.InvalidArguments;
        }

        try
        {
            // After Ctrl+C, the pipeline stops at the next block and deletes the partially written target; the process
            // is only terminated if it has not stopped after this delay (2 s by default, short for a slow network share)
            var configuration = new InvocationConfiguration
            {
                Output = _output,
                Error = _error,
                EnableDefaultExceptionHandler = false,
                ProcessTerminationTimeout = TimeSpan.FromSeconds(30)
            };
            return await parseResult.InvokeAsync(configuration, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            WriteError(Tr("cli.cancelled"));
            return ExitCodes.Cancelled;
        }
        catch (Exception ex)
        {
            WriteError(Tr("cli.error", ("error", ex.Message)));
            if (WriteCrashReport(parseResult, ex) is { } reportPath)
            {
                WriteError(Tr("cli.crash_report", ("path", reportPath)));
            }

            return ExitCodes.UnexpectedError;
        }
    }

    /// <summary>Report of an unexpected error, in <c>crashes/</c> next to the configuration file in use.</summary>
    private string? WriteCrashReport(ParseResult parseResult, Exception exception)
    {
        string configPath;
        try
        {
            configPath = GetConfigPath(parseResult);
        }
        catch (Exception)
        {
            configPath = _defaultConfigPath;
        }

        return new CrashReportService(CrashReportService.GetCrashDirectory(configPath))
            .WriteReport(exception, "Command line: " + string.Join(' ', parseResult.Tokens.Select(t => t.Value)));
    }

    // =========================================================================
    // Commands
    // =========================================================================

    private RootCommand BuildCommands()
    {
        _configOption.Description = Tr("cli.option_config");
        _configOption.HelpName = Tr("cli.help_file");
        _langOption.Description = Tr("cli.option_lang");
        _langOption.AcceptOnlyFromAmong([.. _localization.GetAvailableLocales().Select(l => l.Code)]);
        _quietOption.Description = Tr("cli.option_quiet");
        _profileOption.Description = Tr("cli.option_profile");
        _profileOption.HelpName = Tr("cli.help_name");
        _sourceOption.Description = Tr("cli.option_source");
        _sourceOption.HelpName = Tr("cli.help_directory");
        _destinationOption.Description = Tr("cli.option_destination");
        _destinationOption.HelpName = Tr("cli.help_directory");
        _datOption.Description = Tr("cli.option_dat");
        _datOption.HelpName = Tr("cli.help_file");
        _datRecursiveOption.Description = Tr("cli.option_dat_recursive");
        _datMirrorOption.Description = Tr("cli.option_dir_dat_mirror");
        _datOnlyOption.Description = Tr("cli.option_dat_only");
        _datOnlyOption.HelpName = Tr("cli.help_name");
        _chdOption.Description = Tr("cli.option_chd");
        _chdOption.HelpName = Tr("cli.help_directory");
        _categoriesOption.Description = Tr("cli.option_categories");
        _categoriesOption.HelpName = Tr("cli.help_file");
        _ratingsOption.Description = Tr("cli.option_ratings");
        _ratingsOption.HelpName = Tr("cli.help_file");
        _modeOption.Description = Tr("cli.option_mode");
        _modeOption.AcceptOnlyFromAmong(TransferModeNames);
        _executeOption.Description = Tr("cli.option_execute");
        _appendOption.Description = Tr("cli.option_append");
        _purgeOption.Description = Tr("cli.option_purge");
        _yesOption.Description = Tr("cli.option_yes");
        _forceForeignPurgeOption.Description = Tr("cli.option_force_foreign_purge");
        _reportOption.Description = Tr("cli.option_report");
        _reportOption.HelpName = Tr("cli.help_file");
        _missListOption.Description = Tr("cli.option_miss_list");
        _missListOption.HelpName = Tr("cli.help_file");
        _fixDatOption.Description = Tr("cli.option_fixdat");
        _fixDatOption.HelpName = Tr("cli.help_file");
        _emptyOption.Description = Tr("cli.option_empty");
        _outputOption.Description = Tr("cli.option_output_dat");
        _outputOption.HelpName = Tr("cli.help_file");

        var run = new Command("run", Tr("cli.command_run"))
        {
            _profileOption, _sourceOption, _destinationOption, _datOption, _datRecursiveOption, _datOnlyOption, _datMirrorOption,
            _chdOption, _categoriesOption, _ratingsOption, _modeOption, _executeOption, _appendOption, _purgeOption, _yesOption,
            _forceForeignPurgeOption, _reportOption, _missListOption, _fixDatOption
        };
        run.SetAction((parseResult, cancellationToken) => RunSortAsync(parseResult, cancellationToken));

        var undo = new Command("undo", Tr("cli.command_undo")) { _yesOption };
        undo.SetAction((parseResult, _) => UndoLastSortAsync(parseResult));

        var dat = new Command("dat", Tr("cli.command_dat"))
        {
            _profileOption, _datOption, _datRecursiveOption, _datOnlyOption, _categoriesOption, _ratingsOption, _outputOption
        };
        dat.SetAction((parseResult, cancellationToken) => ExportCatalogDatAsync(parseResult, cancellationToken));

        var quarantine = new Command("quarantine", Tr("cli.command_quarantine"))
        {
            _profileOption, _destinationOption, _emptyOption, _yesOption
        };
        quarantine.SetAction((parseResult, cancellationToken) => QuarantineAsync(parseResult, cancellationToken));

        var profiles = new Command("profiles", Tr("cli.command_profiles"));
        profiles.SetAction(ListProfiles);

        var root = new RootCommand(Tr("cli.description"))
        {
            _configOption, _langOption, _quietOption, run, undo, dat, quarantine, profiles
        };

        // System.CommandLine does not translate the description of its help option in every language
        foreach (var help in root.Options.OfType<HelpOption>())
        {
            help.Description = Tr("cli.option_help");
        }

        return root;
    }

    private async Task<int> RunSortAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var quiet = parseResult.GetValue(_quietOption);
        var execute = parseResult.GetValue(_executeOption);
        var append = parseResult.GetValue(_appendOption);
        var purge = parseResult.GetValue(_purgeOption);
        var confirmed = parseResult.GetValue(_yesOption);
        var reportPath = parseResult.GetValue(_reportOption);
        var missListPath = parseResult.GetValue(_missListOption);
        var fixDatPath = parseResult.GetValue(_fixDatOption);

        // Incompatible options and export formats are rejected before anything is read or written
        if (purge && !execute)
        {
            return Fail(ExitCodes.InvalidArguments, Tr("cli.purge_requires_execute"));
        }

        if (purge && append)
        {
            return Fail(ExitCodes.InvalidArguments, Tr("cli.purge_with_append"));
        }

        if (reportPath is not null && !HasExtension(reportPath, ".json", ".csv"))
        {
            return Fail(ExitCodes.InvalidArguments, Tr("cli.report_extension", ("path", reportPath)));
        }

        if (missListPath is not null && !HasExtension(missListPath, ".txt", ".csv"))
        {
            return Fail(ExitCodes.InvalidArguments, Tr("cli.miss_list_extension", ("path", missListPath)));
        }

        var configPath = GetConfigPath(parseResult);
        var (settings, settingsError) = await LoadSettingsAsync(parseResult, configPath, quiet);
        if (settings is null)
        {
            return settingsError;
        }

        ApplyOverrides(parseResult, settings);

        // The folders of the DATs are mirrored under the folder of each DAT: without it, there is nothing to mirror
        if (parseResult.GetValue(_datMirrorOption))
        {
            if (!settings.FolderPerDat)
            {
                return Fail(ExitCodes.InvalidArguments, Tr("cli.mirror_requires_folder_per_dat"));
            }

            settings.DatFolderMirror = true;
        }

        if (string.IsNullOrWhiteSpace(settings.SourceDir))
        {
            return Fail(ExitCodes.InvalidConfiguration, Tr("cli.no_source"));
        }

        if (string.IsNullOrWhiteSpace(settings.DestinationDir))
        {
            return Fail(ExitCodes.InvalidConfiguration, Tr("cli.no_destination"));
        }

        if ((missListPath is not null || fixDatPath is not null) && string.IsNullOrWhiteSpace(settings.DatFile))
        {
            return Fail(ExitCodes.InvalidArguments, Tr("cli.export_requires_dat"));
        }

        var config = SortSettingsSerializer.ToSortConfig(settings);
        if (!Directory.Exists(config.SourceDir))
        {
            return Fail(ExitCodes.InvalidConfiguration, Tr("log.source_dir_not_found", ("path", config.SourceDir)));
        }

        // A folder holds a lot of DATs, or the DATs of several MAME software lists
        if (config.DatFile is not null && !File.Exists(config.DatFile) && !Directory.Exists(config.DatFile))
        {
            return Fail(ExitCodes.InvalidConfiguration, Tr("log.dat_file_not_found", ("path", config.DatFile)));
        }

        using var services = BuildServices(configPath);
        if (ChooseLotDats(parseResult, settings, services.GetRequiredService<IDatService>(), out _) is { } lotError)
        {
            return lotError;
        }

        // The DATs chosen by --dat-only
        config = SortSettingsSerializer.ToSortConfig(settings);
        var overlapKey = SortConfigValidator.GetErrorKey(config, IsMameRun(config, services.GetRequiredService<IDatService>()));
        if (overlapKey is not null)
        {
            return Fail(ExitCodes.InvalidConfiguration, Tr(overlapKey));
        }

        var processor = services.GetRequiredService<ISortProcessorService>();

        // A real sort never writes into a non-empty destination without being told on the command line to complete or
        // purge it: the option of the configuration that completes a destination is no such answer, as the interface
        // still asks before completing one
        var destination = processor.InspectDestination(config.DestinationDir);
        if (execute && destination.HasContent && !append)
        {
            if (!purge)
            {
                return Fail(ExitCodes.ConfirmationRequired, Tr("cli.dest_not_empty", ("path", config.DestinationDir)));
            }

            if (!confirmed)
            {
                return Fail(ExitCodes.ConfirmationRequired, Tr("cli.purge_requires_yes", ("path", config.DestinationDir)));
            }

            // A destination that RomSieve did not mark may be a wrong folder: it is only purged on explicit request
            if (destination.IsForeign && !parseResult.GetValue(_forceForeignPurgeOption))
            {
                return Fail(ExitCodes.ConfirmationRequired, Tr("cli.purge_foreign", ("path", config.DestinationDir)));
            }

            // The purge asked on the command line prevails over the option of the configuration that completes it
            config.AppendToDestination = false;
            config.PurgeDestination = true;
        }

        var log = new SynchronousProgress<LogMessage>(message =>
        {
            if (!quiet || message.Level is LogLevel.Warn or LogLevel.Error)
            {
                WriteLine($"[{message.Level.ToTag()}] {_localization.Format(message.Message)}");
            }
        });

        var result = await processor.ExecuteAsync(config, isDryRun: !execute, logProgress: log, cancellationToken: cancellationToken);
        WriteSummary(result.Stats, result.DatCatalog is not null);
        if ((missListPath is not null || fixDatPath is not null) && result.Stats.DescribeLotExportScope() is { } scope)
        {
            WriteLine($"[{LogLevel.Info.ToTag()}] {_localization.Format(scope)}");
        }

        WriteLine(execute ? Tr("cli.sort_done") : Tr("cli.dry_run_done"));
        if (result.JournalPath is not null && !quiet)
        {
            WriteLine(Tr("cli.journal_written", ("path", result.JournalPath)));
        }

        var exportsOk = await ExportAsync(processor, result, reportPath, missListPath, fixDatPath, cancellationToken);
        return result.Stats.ErrorCount > 0 || !exportsOk ? ExitCodes.CompletedWithErrors : ExitCodes.Success;
    }

    /// <summary>
    /// Writes the filtered DAT of the catalog of the configuration (or of a profile, or <c>--dat</c>): the games the
    /// options keep, chosen on the DAT alone, as the interface does. Neither the source nor the destination is read.
    /// </summary>
    private async Task<int> ExportCatalogDatAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var quiet = parseResult.GetValue(_quietOption);
        var outputPath = parseResult.GetValue(_outputOption)!;

        var configPath = GetConfigPath(parseResult);
        var (settings, settingsError) = await LoadSettingsAsync(parseResult, configPath, quiet);
        if (settings is null)
        {
            return settingsError;
        }

        ApplyOverrides(parseResult, settings);
        if (string.IsNullOrWhiteSpace(settings.DatFile))
        {
            return Fail(ExitCodes.InvalidConfiguration, Tr("log.catalog_requires_dat"));
        }

        var config = SortSettingsSerializer.ToSortConfig(settings);
        if (!File.Exists(config.DatFile) && !Directory.Exists(config.DatFile))
        {
            return Fail(ExitCodes.InvalidConfiguration, Tr("log.dat_file_not_found", ("path", config.DatFile)));
        }

        // The DATs of MAME software lists and of a lot are written into a folder, a DAT per list or per DAT at its root;
        // any other into a file
        using var services = BuildServices(configPath);
        if (ChooseLotDats(parseResult, settings, services.GetRequiredService<IDatService>(), out var lot) is { } lotError)
        {
            return lotError;
        }

        config = SortSettingsSerializer.ToSortConfig(settings);
        var softwareLists = lot is null && IsSoftwareListDat(config, services.GetRequiredService<IDatService>());
        var inFolder = softwareLists || lot is not null;
        if (inFolder && (File.Exists(outputPath) || HasExtension(outputPath, ".xml", ".dat")))
        {
            return Fail(ExitCodes.InvalidArguments, Tr(lot is null ? "cli.output_softlist_folder" : "cli.output_lot_folder", ("path", outputPath)));
        }

        if (!inFolder && !HasExtension(outputPath, ".xml", ".dat"))
        {
            return Fail(ExitCodes.InvalidArguments, Tr("cli.output_dat_extension", ("path", outputPath)));
        }

        var log = new SynchronousProgress<LogMessage>(message =>
        {
            if (!quiet || message.Level is LogLevel.Warn or LogLevel.Error)
            {
                WriteLine($"[{message.Level.ToTag()}] {_localization.Format(message.Message)}");
            }
        });

        var processor = services.GetRequiredService<ISortProcessorService>();
        var existingDats = inFolder ? DatService.GetDatFiles(outputPath) : null;
        CatalogSelectionResult result;
        try
        {
            result = await processor.ExportCatalogDatAsync(
                config, outputPath, DatExportFormat.Auto, logProgress: log, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail(ExitCodes.CompletedWithErrors, Tr("cli.export_failed", ("path", outputPath), ("error", ex.Message)));
        }
        catch (InvalidOperationException ex)
        {
            // An output that would replace the DAT read (DatCatalog.IsReadFrom)
            return Fail(ExitCodes.InvalidArguments, Tr("cli.export_failed", ("path", outputPath), ("error", ex.Message)));
        }

        if (result.KeptCount == 0)
        {
            return Fail(ExitCodes.CompletedWithErrors, Tr("cli.catalog_dat_empty"));
        }

        ReportUntouchedDats(outputPath, existingDats, lot is not null);
        WriteLine(Tr("cli.catalog_dat_written", ("kept", result.KeptCount), ("total", result.CandidateCount), ("path", outputPath)));
        return ExitCodes.Success;
    }

    private async Task<int> UndoLastSortAsync(ParseResult parseResult)
    {
        var configPath = GetConfigPath(parseResult);
        using var services = BuildServices(configPath);
        var journal = services.GetRequiredService<IOperationJournalService>();
        var last = journal.FindLastUndoableJournal();
        if (last is null)
        {
            WriteLine(Tr("undo.nothing_to_undo"));
            return ExitCodes.NothingToUndo;
        }

        WriteLine(Tr(
            "cli.undo_summary",
            ("date", last.StartedUtc.ToLocalTime().ToString("g", _culture)),
            ("count", last.OperationCount),
            ("mode_name", GetTransferModeName(last.TransferMode)),
            ("source_dir", last.SourceDir),
            ("dest_dir", last.DestinationDir)));
        if (last.IsUndoToResume)
        {
            WriteLine(Tr("cli.undo_resume", ("count", last.RetryCount)));
        }

        if (!parseResult.GetValue(_yesOption))
        {
            return Fail(ExitCodes.ConfirmationRequired, Tr("cli.undo_requires_yes"));
        }

        UndoReport report;
        try
        {
            report = await journal.UndoAsync(last.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Fail(ExitCodes.UnexpectedError, Tr("undo.failed", ("error", ex.Message)));
        }

        foreach (var issue in report.Issues)
        {
            var detail = string.IsNullOrEmpty(issue.Detail) ? string.Empty : $" ({issue.Detail})";
            WriteLine($"[{LogLevel.Warn.ToTag()}] " + Tr("log.labeled", ("label", Tr(issue.Kind.ToMessageKey())), ("detail", issue.Operation.TargetPath + detail)));
        }

        WriteLine(Tr("cli.undo_report", ("undone", report.UndoneCount), ("total", report.TotalOperations), ("issues", report.Issues.Count)));
        if (report.RetryableCount > 0)
        {
            WriteLine(Tr("cli.undo_retry_hint", ("count", report.RetryableCount)));
        }

        return report.Issues.Count == 0 ? ExitCodes.Success : ExitCodes.CompletedWithErrors;
    }

    /// <summary>
    /// Shows the quarantine of the purges of the destination (of the configuration, of a profile, or <c>--destination</c>)
    /// and, with <c>--empty --yes</c>, deletes it: the only deletion of RomSieve, which the undo of the purges it held can
    /// no longer make up for. Only the quarantine of a destination marked by RomSieve is ever touched.
    /// </summary>
    private async Task<int> QuarantineAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var quiet = parseResult.GetValue(_quietOption);
        var (settings, settingsError) = await LoadSettingsAsync(parseResult, GetConfigPath(parseResult), quiet);
        if (settings is null)
        {
            return settingsError;
        }

        var destination = parseResult.GetValue(_destinationOption) ?? settings.DestinationDir;
        if (string.IsNullOrWhiteSpace(destination))
        {
            return Fail(ExitCodes.InvalidConfiguration, Tr("cli.no_destination"));
        }

        if (!Directory.Exists(destination))
        {
            return Fail(ExitCodes.InvalidConfiguration, Tr("cli.quarantine_destination_not_found", ("path", destination)));
        }

        var info = await Task.Run(() => DestinationGuard.GetQuarantine(destination, cancellationToken), cancellationToken);
        if (info is null)
        {
            var isForeign = !File.Exists(Path.Combine(destination, DestinationGuard.MarkerFileName)) &&
                            Directory.Exists(Path.Combine(destination, DestinationGuard.QuarantineDirectoryName));
            WriteLine(Tr(isForeign ? "cli.quarantine_not_marked" : "cli.quarantine_none", ("path", destination)));
            return ExitCodes.Success;
        }

        WriteLine(Tr(
            "cli.quarantine_summary",
            ("path", info.Path),
            ("purges", info.PurgeCount),
            ("files", info.FileCount),
            ("size", SizeFormatting.Format(_localization, info.TotalBytes))));
        if (!parseResult.GetValue(_emptyOption))
        {
            return ExitCodes.Success;
        }

        if (!parseResult.GetValue(_yesOption))
        {
            return Fail(ExitCodes.ConfirmationRequired, Tr("cli.quarantine_empty_requires_yes"));
        }

        var result = await Task.Run(() => DestinationGuard.EmptyQuarantine(destination, cancellationToken), cancellationToken);
        foreach (var error in result.Errors)
        {
            WriteError(Tr("cli.quarantine_not_deleted", ("detail", error)));
        }

        WriteLine(Tr(
            "cli.quarantine_emptied",
            ("files", result.DeletedFiles),
            ("size", SizeFormatting.Format(_localization, result.DeletedBytes))));
        return result.Errors.Count == 0 ? ExitCodes.Success : ExitCodes.CompletedWithErrors;
    }

    private int ListProfiles(ParseResult parseResult)
    {
        var configPath = GetConfigPath(parseResult);
        var directory = ConfigurationProfiles.GetDirectory(configPath);
        var names = ConfigurationProfiles.List(configPath);
        if (names.Count == 0)
        {
            WriteLine(Tr("cli.profiles_empty", ("path", directory)));
            return ExitCodes.Success;
        }

        WriteLine(Tr("cli.profiles_header", ("path", directory)));
        foreach (var name in names)
        {
            WriteLine("  " + name);
        }

        return ExitCodes.Success;
    }

    // =========================================================================
    // Options of a run
    // =========================================================================

    private string GetConfigPath(ParseResult parseResult) =>
        parseResult.GetValue(_configOption) is { } path ? Path.GetFullPath(path) : _defaultConfigPath;

    /// <summary>
    /// Reads the options of the profile, or of the configuration file; the configuration of the interface may be
    /// missing (defaults), but not a file named on the command line.
    /// </summary>
    private async Task<(SortSettings? Settings, int ExitCode)> LoadSettingsAsync(ParseResult parseResult, string configPath, bool quiet)
    {
        string path;
        if (parseResult.GetValue(_profileOption) is { } profile)
        {
            if (!ConfigurationProfiles.IsValidName(profile))
            {
                return (null, Fail(ExitCodes.InvalidArguments, Tr("cli.profile_invalid_name", ("name", profile))));
            }

            path = ConfigurationProfiles.GetPath(configPath, profile);
            if (!File.Exists(path))
            {
                return (null, Fail(ExitCodes.InvalidConfiguration, Tr("cli.profile_not_found", ("name", profile), ("path", path))));
            }
        }
        else
        {
            path = configPath;
            if (!File.Exists(path))
            {
                if (parseResult.GetValue(_configOption) is not null)
                {
                    return (null, Fail(ExitCodes.InvalidConfiguration, Tr("cli.config_not_found", ("path", path))));
                }

                if (!quiet)
                {
                    WriteLine(Tr("cli.options_default", ("path", path)));
                }

                return (new SortSettings(), ExitCodes.Success);
            }
        }

        var data = await _configService.LoadConfigFromFileAsync(path);
        if (data is null)
        {
            return (null, Fail(ExitCodes.InvalidConfiguration, Tr("cli.config_unreadable", ("path", path))));
        }

        if (!quiet)
        {
            WriteLine(Tr("cli.options_from", ("path", path)));
        }

        // A warning is shown even in quiet mode, like those of the run
        if (ConfigSchema.DescribeNewerVersion(data, path) is { } warning)
        {
            WriteLine($"[{LogLevel.Warn.ToTag()}] " + _localization.Format(warning));
        }

        return (SortSettingsSerializer.Read(data), ExitCodes.Success);
    }

    private void ApplyOverrides(ParseResult parseResult, SortSettings settings)
    {
        if (parseResult.GetValue(_sourceOption) is { } source) settings.SourceDir = source;
        if (parseResult.GetValue(_destinationOption) is { } destination) settings.DestinationDir = destination;
        if (parseResult.GetValue(_datOption) is { } dat) settings.DatFile = dat;
        if (parseResult.GetValue(_datRecursiveOption)) settings.DatRecursive = true;
        if (parseResult.GetValue(_chdOption) is { } chd) settings.ChdDir = chd;
        if (parseResult.GetValue(_categoriesOption) is { } categories) settings.MameCategoryFile = categories;
        if (parseResult.GetValue(_ratingsOption) is { } ratings) settings.MameRatingFile = ratings;
        if (parseResult.GetValue(_modeOption) is { } mode) settings.TransferMode = EnumExtensions.ParseTransferMode(mode);
        if (parseResult.GetValue(_appendOption)) settings.AppendToDestination = true;
    }

    /// <summary>
    /// The DATs of a lot (a folder or an archive of No-Intro, Redump or TOSEC DATs), from their headers alone, as the sort reads them:
    /// a folder the sort would refuse is refused before anything is done (<c>dat.folder_*</c>, code 3); with
    /// <c>--dat-only</c>, the DATs named alone are sorted (case ignored), in place of those <c>excluded_dats</c> leaves
    /// out, and a name no DAT of the lot has is refused with the names of the lot (code 2), as is the option without a
    /// lot. <paramref name="lot"/> is <c>null</c> for a DAT file, a folder of MAME software lists, or no DAT.
    /// </summary>
    /// <returns>The exit code of a refusal, or <c>null</c>.</returns>
    private int? ChooseLotDats(ParseResult parseResult, SortSettings settings, IDatService datService, out IReadOnlyList<DatFolderEntry>? lot)
    {
        lot = null;
        var only = parseResult.GetValue(_datOnlyOption) ?? [];
        var path = settings.DatFile.Trim();
        if (path.Length > 0 && datService.HoldsSeveralDats(path))
        {
            IReadOnlyList<DatFolderEntry> entries;
            try
            {
                entries = datService.ReadDatFolderPreview(path, settings.DatRecursive);
                DatService.CheckDatFolder(path, entries);
            }
            catch (DatFolderException ex)
            {
                return Fail(ExitCodes.InvalidConfiguration, Tr("log.dat_load_error", ("error", _localization.Format(ex.Reason))));
            }

            if (entries[0].Convention != NamingConvention.MameSoftwareList)
            {
                lot = entries;
            }
        }

        if (only.Length == 0)
        {
            return null;
        }

        if (lot is null)
        {
            return Fail(ExitCodes.InvalidArguments, Tr("cli.dat_only_requires_lot"));
        }

        var names = lot.Select(e => e.Name).ToList();
        if (only.FirstOrDefault(name => !names.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase)) is { } unknown)
        {
            return Fail(ExitCodes.InvalidArguments, Tr(
                "cli.dat_only_unknown",
                ("name", unknown),
                ("names", _localization.Format(MameSetDependencies.FormatNames(names)))));
        }

        var kept = new HashSet<string>(only.Select(name => name.Trim()), StringComparer.OrdinalIgnoreCase);
        settings.ExcludedDats = names.Where(name => !kept.Contains(name)).ToList();
        return null;
    }

    /// <summary>
    /// Whether the DAT of the configuration holds MAME software lists, whose DATs are written into a folder: the
    /// convention of a DAT is known from its header and first games. A DAT that cannot be read is reported by the export.
    /// </summary>
    private static bool IsSoftwareListDat(SortConfig config, IDatService datService)
    {
        if (string.IsNullOrWhiteSpace(config.DatFile))
        {
            return false;
        }

        try
        {
            return MameSetIdentifier.GetSetConvention(config, datService.ReadDatPreview(config.DatFile)) == NamingConvention.MameSoftwareList;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // The DATs of the folder that an export did not replace stay there, and a ROM manager reads them too
    private void ReportUntouchedDats(string folder, IReadOnlyDictionary<string, DateTime>? before, bool isLot)
    {
        if (before is null || DatService.GetUntouchedDatFiles(before) is not { Count: > 0 } untouched)
        {
            return;
        }

        WriteLine($"[{LogLevel.Warn.ToTag()}] {_localization.Format(LocalizedMessage.Create(
            isLot ? "log.export_dat_folder_untouched" : "log.export_softlist_untouched",
            ("count", untouched.Count),
            ("path", folder),
            ("names", MameSetDependencies.FormatNames(untouched))))}");
    }

    /// <summary>
    /// Whether the run sorts MAME sets, as the pipeline decides once it has read the DAT (<see cref="MameSetIdentifier.IsMameRun"/>):
    /// the convention of a DAT is known from its header and first games. A DAT that cannot be read is reported by the run.
    /// </summary>
    private static bool IsMameRun(SortConfig config, IDatService datService)
    {
        if (string.IsNullOrWhiteSpace(config.DatFile))
        {
            return false;
        }

        // A folder or an archive of DATs is a lot or MAME software lists, never MAME machines, which a lot refuses
        if (datService.HoldsSeveralDats(config.DatFile))
        {
            return false;
        }

        try
        {
            return MameSetIdentifier.IsMameRun(config, datService.ReadDatPreview(config.DatFile));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The CRC cache and the journals live next to a configuration file other than the one of the interface, as for
    /// the interface (<c>CreateMainViewModel</c>); with the configuration of the interface, they are its own.
    /// </summary>
    private ServiceProvider BuildServices(string configPath)
    {
        var services = new ServiceCollection();
        services.AddRomSieveCore();
        services.Replace(ServiceDescriptor.Singleton<ILocalizationService>(_localization));
        services.Replace(ServiceDescriptor.Singleton<IConfigurationService>(_configService));
        if (!string.Equals(configPath, Path.GetFullPath(_configService.GetDefaultConfigPath()), StringComparison.OrdinalIgnoreCase))
        {
            services.Replace(ServiceDescriptor.Singleton<ICrcCacheService>(_ => new CrcCacheService(
                Path.Combine(Path.GetDirectoryName(configPath) ?? string.Empty, "crc_cache.json"))));
            services.Replace(ServiceDescriptor.Singleton<IOperationJournalService>(sp => new OperationJournalService(
                OperationJournalService.GetJournalDirectory(configPath),
                sp.GetRequiredService<IChecksumService>(),
                sp.GetRequiredService<ICrcCacheService>())));
        }

        return services.BuildServiceProvider();
    }

    // =========================================================================
    // Exports and summary
    // =========================================================================

    private async Task<bool> ExportAsync(
        ISortProcessorService processor,
        SortExecutionResult result,
        string? reportPath,
        string? missListPath,
        string? fixDatPath,
        CancellationToken cancellationToken)
    {
        var ok = true;
        if (reportPath is not null && result.DatCatalog?.IsReadFrom(reportPath) == true)
        {
            // The report alone does not receive the catalog that refuses it
            WriteError(Tr("cli.export_failed", ("path", reportPath), ("error", Tr("export.overwrites_dat", ("path", reportPath)))));
            ok = false;
        }
        else if (reportPath is not null)
        {
            ok &= await TryExportAsync(reportPath, () => HasExtension(reportPath, ".csv")
                ? processor.ExportReportToCsvAsync(result.Items, reportPath, cancellationToken)
                : processor.ExportReportToJsonAsync(result.Items, result.Stats, reportPath, cancellationToken));
        }

        if (result.DatCatalog is { } catalog)
        {
            if (missListPath is not null)
            {
                ok &= await TryExportAsync(missListPath, () => HasExtension(missListPath, ".csv")
                    ? processor.ExportMissListToCsvAsync(result.MissingRoms, catalog, missListPath, cancellationToken)
                    : processor.ExportMissListToTxtAsync(result.MissingRoms, catalog, missListPath, cancellationToken));
            }

            if (fixDatPath is not null)
            {
                // MAME software lists: a fixdat per list, in the folder fixDatPath
                ok &= await TryExportAsync(fixDatPath, () => processor.ExportFixDatAsync(result.MissingRoms, catalog, fixDatPath, cancellationToken));
            }
        }

        return ok;
    }

    private async Task<bool> TryExportAsync(string path, Func<Task> export)
    {
        try
        {
            await export();
            WriteLine(Tr("cli.export_written", ("path", path)));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            WriteError(Tr("cli.export_failed", ("path", path), ("error", ex.Message)));
            return false;
        }
    }

    private void WriteSummary(ProcessStats stats, bool hasDat)
    {
        WriteLine(Tr(
            "cli.summary",
            ("scanned", stats.TotalScanned),
            ("valid", stats.ValidCount),
            ("excluded", stats.ExcludedCount),
            ("unmatched", stats.UnmatchedCount),
            ("duplicates", stats.DuplicateCount),
            ("errors", stats.ErrorCount)));
        if (stats.AlreadyInDestinationCount > 0)
        {
            WriteLine(Tr("cli.summary_already_in_destination", ("count", stats.AlreadyInDestinationCount)));
        }

        if (hasDat)
        {
            WriteLine(Tr(
                "cli.summary_dat",
                ("matched", stats.DatMatchedCount),
                ("total", stats.DatTotalRoms),
                ("pct", stats.DatCompletionPct.ToString("0.0", _culture)),
                ("missing", stats.DatMissingCount)));
        }

        // A lot: each DAT with a game found, then the number of the others
        if (stats.PerDat is { } perDat)
        {
            foreach (var dat in perDat.Where(d => d.GamesFound > 0))
            {
                WriteLine(Tr(
                    "cli.summary_dat_lot_line",
                    ("dat", dat.Name),
                    ("games", dat.GamesFound),
                    ("matched", dat.MatchedCount + dat.NameMatchedCount),
                    ("total", dat.TotalRoms),
                    ("pct", dat.CompletionPct.ToString("0.0", _culture))));
            }

            if (perDat.Count(d => d.GamesFound == 0) is var nothing and > 0)
            {
                WriteLine(Tr("cli.summary_dat_lot_nothing", ("count", nothing)));
            }
        }
    }

    // =========================================================================
    // Language and output
    // =========================================================================

    /// <summary>
    /// The language is needed before the arguments are parsed, to translate the help: <c>--lang</c>, otherwise the
    /// language of the interface saved in the configuration file, otherwise English.
    /// </summary>
    private void SetLanguage(string[] args)
    {
        var locales = _localization.GetAvailableLocales().Select(l => l.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var locale = FindOptionValue(args, "--lang");
        if (locale is null || !locales.Contains(locale))
        {
            var configPath = FindOptionValue(args, "--config") is { } path ? path : _defaultConfigPath;
            locale = LocalizationService.DefaultLocale;
            try
            {
                var data = _configService.LoadConfigFromFileAsync(configPath).GetAwaiter().GetResult();
                if (data is not null
                    && ConfigDictionaryReader.TryGetString(data, "ui_locale", out var saved)
                    && locales.Contains(saved))
                {
                    locale = saved;
                }
            }
            catch (ArgumentException)
            {
                // An invalid path is reported when the command runs
            }
        }

        _localization.SetLocale(locale);
        _culture = CultureInfo.GetCultureInfo(_localization.CurrentLocale);

        // The help and the parse errors of System.CommandLine follow the culture of the user interface
        CultureInfo.CurrentUICulture = _culture;
    }

    /// <summary>Value of an option written <c>--name value</c>, <c>--name=value</c> or <c>--name:value</c>.</summary>
    internal static string? FindOptionValue(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == name)
            {
                return i + 1 < args.Count ? args[i + 1] : null;
            }

            if (arg.Length > name.Length + 1 && arg.StartsWith(name, StringComparison.Ordinal) && arg[name.Length] is '=' or ':')
            {
                return arg[(name.Length + 1)..];
            }
        }

        return null;
    }

    private string GetTransferModeName(TransferMode mode) => mode switch
    {
        TransferMode.Move => Tr("mode.move"),
        TransferMode.HardLink => Tr("mode.hardlink"),
        TransferMode.SymbolicLink => Tr("mode.symlink"),
        TransferMode.Zip => Tr("mode.zip"),
        _ => Tr("mode.copy")
    };

    private static bool HasExtension(string path, params string[] extensions) =>
        extensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private string Tr(string key, params (string Key, object? Value)[] args) => _localization.Tr(key, args);

    private int Fail(int exitCode, string message)
    {
        WriteError(message);
        return exitCode;
    }

    private void WriteLine(string text)
    {
        lock (_writeLock)
        {
            _output.WriteLine(text);
        }
    }

    private void WriteError(string text)
    {
        lock (_writeLock)
        {
            _error.WriteLine(text);
        }
    }

    /// <summary>Reports on the calling thread, so that the logs keep their order (unlike <see cref="Progress{T}"/>).</summary>
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
