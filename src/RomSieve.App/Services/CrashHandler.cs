using Avalonia.Threading;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.App.Services;

/// <summary>
/// Global handler of the exceptions nothing else caught: writes a crash report (<see cref="ICrashReportService"/>) and,
/// once the interface is there, tells the user where it is instead of closing the application without a trace.
/// Installed by <c>Program</c> and <c>App</c> only, never by the windows, which the headless tests create.
/// </summary>
public sealed class CrashHandler
{
    /// <summary>Reports written at most per session: an exception thrown at every frame must not fill the disk.</summary>
    internal const int MaxReportsPerSession = 10;

    internal const string UiThreadOrigin = "Interface thread (handled, the application kept running)";
    internal const string FatalOrigin = "Unhandled exception (fatal, the application closed)";
    internal const string UnobservedTaskOrigin = "Unobserved task exception (ignored, the application kept running)";

    private readonly ICrashReportService _reports;
    private readonly TextWriter _error;
    private readonly object _lock = new();
    private int _reportCount;
    private IDialogService? _dialogs;
    private ILocalizationService? _localization;
    private LogViewModel? _log;
    private Dispatcher? _dispatcher;
    private bool _isDialogShown;

    public CrashHandler(ICrashReportService reports, TextWriter error)
    {
        _reports = reports;
        _error = error;
    }

    /// <summary>Handler of the application, installed by <c>Program.Main</c>.</summary>
    public static CrashHandler? Current { get; internal set; }

    public string CrashDirectory => _reports.CrashDirectory;

    /// <summary>
    /// Catches the exceptions of the whole process: an exception of a background thread still closes the application,
    /// but leaves its report; an exception of a forgotten task is only reported.
    /// </summary>
    public void InstallProcessHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += OnProcessUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>
    /// Catches the exceptions of the interface thread, which no longer close the application: the user is told
    /// in a message box and in the log.
    /// </summary>
    public void AttachInterface(Dispatcher dispatcher, IDialogService dialogs, ILocalizationService localization, LogViewModel log)
    {
        DetachInterface();
        AttachDialogs(dialogs, localization, log);
        _dispatcher = dispatcher;
        dispatcher.UnhandledException += OnDispatcherUnhandledException;
    }

    /// <summary>Where <see cref="HandleInterfaceExceptionAsync"/> tells the user, without catching anything yet.</summary>
    internal void AttachDialogs(IDialogService dialogs, ILocalizationService localization, LogViewModel log)
    {
        _dialogs = dialogs;
        _localization = localization;
        _log = log;
    }

    public void DetachInterface()
    {
        if (_dispatcher is not null)
        {
            _dispatcher.UnhandledException -= OnDispatcherUnhandledException;
        }

        _dispatcher = null;
        _dialogs = null;
        _localization = null;
        _log = null;
    }

    /// <summary>Writes a report, unless <see cref="MaxReportsPerSession"/> are already written.</summary>
    internal string? Report(Exception exception, string origin)
    {
        lock (_lock)
        {
            if (_reportCount >= MaxReportsPerSession)
            {
                return null;
            }

            _reportCount++;
        }

        return _reports.WriteReport(exception, origin);
    }

    /// <summary>Reports an exception of the interface thread, then tells the user (one message box at a time).</summary>
    internal async Task HandleInterfaceExceptionAsync(Exception exception)
    {
        try
        {
            var path = Report(exception, UiThreadOrigin);
            if (_log is not null)
            {
                _log.Append(LogLevel.Error, path is null
                    ? LocalizedMessage.Create("log.unexpected_error_no_report", ("error", exception.Message))
                    : LocalizedMessage.Create("log.unexpected_error", ("error", exception.Message), ("path", path)));
            }

            if (_dialogs is null || _localization is null || _isDialogShown)
            {
                return;
            }

            _isDialogShown = true;
            try
            {
                var message = path is null
                    ? _localization.Tr("dialog.unexpected_error_no_report", ("error", exception.Message))
                    : _localization.Tr("dialog.unexpected_error", ("error", exception.Message), ("path", path));
                await _dialogs.ShowErrorAsync(_localization.Tr("dialog.unexpected_error_title"), message);
            }
            finally
            {
                _isDialogShown = false;
            }
        }
        catch (Exception secondary)
        {
            // The handler must never throw: the second exception is only written out
            WriteError(secondary);
        }
    }

    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        _ = HandleInterfaceExceptionAsync(e.Exception);
    }

    private void OnProcessUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is not Exception exception)
        {
            return;
        }

        var path = Report(exception, FatalOrigin);
        try
        {
            _error.WriteLine(exception);
            if (path is not null)
            {
                _error.WriteLine($"RomSieve crash report: {path}");
            }
        }
        catch (Exception)
        {
            // The process is closing: nothing more can be done
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Report(e.Exception, UnobservedTaskOrigin);
        e.SetObserved();
    }

    private void WriteError(Exception exception)
    {
        try
        {
            _error.WriteLine(exception);
        }
        catch (Exception)
        {
            // Standard error unavailable
        }
    }
}
