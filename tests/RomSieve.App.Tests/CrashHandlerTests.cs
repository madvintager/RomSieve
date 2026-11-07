using RomSieve.App.Abstractions;
using RomSieve.App.Services;
using RomSieve.App.ViewModels;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// Global handler of the exceptions of the interface: a crash report is written, the user is told where it is in a
/// message box and in the log, and the application keeps running. The handler itself never throws.
/// </summary>
public sealed class CrashHandlerTests : IDisposable
{
    private readonly TempDirectoryFixture _tmp = new();
    private readonly HeadlessDialogService _dialogs = new();
    private readonly LocalizationService _localization = new("fr");
    private readonly LogViewModel _log;
    private readonly StringWriter _error = new();

    public CrashHandlerTests()
    {
        _log = new LogViewModel(_localization);
    }

    public void Dispose()
    {
        _error.Dispose();
        _tmp.Dispose();
    }

    private string CrashDirectory => _tmp.GetFilePath("crashes");

    private CrashHandler CreateHandler(IDialogService? dialogs = null, string? crashDirectory = null)
    {
        var handler = new CrashHandler(new CrashReportService(crashDirectory ?? CrashDirectory), _error);
        handler.AttachDialogs(dialogs ?? _dialogs, _localization, _log);
        return handler;
    }

    [Fact]
    public async Task InterfaceException_ShouldWriteAReportAndTellWhereItIs()
    {
        var handler = CreateHandler();

        await handler.HandleInterfaceExceptionAsync(new InvalidOperationException("boom"));

        var report = Directory.GetFiles(CrashDirectory).Should().ContainSingle().Subject;
        File.ReadAllText(report).Should().Contain("System.InvalidOperationException: boom")
            .And.Contain("Origin: " + CrashHandler.UiThreadOrigin);
        var shown = _dialogs.ShownMessages.Should().ContainSingle().Subject;
        shown.Level.Should().Be("ERROR");
        shown.Title.Should().Be("Erreur inattendue");
        shown.Message.Should().Contain("boom").And.Contain(report).And.Contain("redémarrer RomSieve");
        var entry = _log.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be("ERROR");
        entry.CleanMessage.Should().Be($"Erreur inattendue : boom (rapport d'erreur : {report})");
    }

    [Fact]
    public async Task InterfaceException_ShouldBeRetranslatedInTheLog()
    {
        var handler = CreateHandler();
        await handler.HandleInterfaceExceptionAsync(new InvalidOperationException("boom"));

        _localization.SetLocale("en");
        var entry = _log.Entries.Single().Retranslate(_localization);

        entry.CleanMessage.Should().StartWith("Unexpected error: boom (crash report: ");
    }

    [Fact]
    public async Task InterfaceException_WhenNoReportCanBeWritten_ShouldStillTellTheUser()
    {
        // A file where the folder of the reports should be
        var handler = CreateHandler(crashDirectory: _tmp.WriteText("crashes", "not a folder"));

        await handler.HandleInterfaceExceptionAsync(new InvalidOperationException("boom"));

        _dialogs.ShownMessages.Should().ContainSingle()
            .Which.Message.Should().Contain("boom").And.Contain("Aucun rapport d'erreur n'a pu être enregistré");
        _log.Entries.Should().ContainSingle()
            .Which.CleanMessage.Should().Be("Erreur inattendue : boom (aucun rapport d'erreur enregistré)");
    }

    [Fact]
    public async Task Reports_ShouldStopAtTheLimitOfTheSession()
    {
        var handler = CreateHandler();

        for (var i = 0; i < CrashHandler.MaxReportsPerSession + 5; i++)
        {
            await handler.HandleInterfaceExceptionAsync(new InvalidOperationException("boom " + i));
        }

        Directory.GetFiles(CrashDirectory).Should().HaveCount(CrashHandler.MaxReportsPerSession);
        _log.Entries.Should().HaveCount(CrashHandler.MaxReportsPerSession + 5, "every exception is still logged");
    }

    [Fact]
    public async Task InterfaceException_WhileAMessageIsShown_ShouldOnlyBeReportedAndLogged()
    {
        var dialogs = new BlockingDialogService();
        var handler = CreateHandler(dialogs);

        var first = handler.HandleInterfaceExceptionAsync(new InvalidOperationException("first"));
        await handler.HandleInterfaceExceptionAsync(new InvalidOperationException("second"));
        dialogs.Close();
        await first;

        dialogs.ShownErrors.Should().ContainSingle().Which.Should().Contain("first");
        Directory.GetFiles(CrashDirectory).Should().HaveCount(2);
        _log.Entries.Should().HaveCount(2);

        // Once closed, the next exception is shown again
        await handler.HandleInterfaceExceptionAsync(new InvalidOperationException("third"));
        dialogs.ShownErrors.Should().HaveCount(2);
    }

    [Fact]
    public async Task InterfaceException_WhenTheMessageBoxFails_ShouldNotThrow()
    {
        var handler = CreateHandler(new FailingDialogService());

        var act = () => handler.HandleInterfaceExceptionAsync(new InvalidOperationException("boom"));

        await act.Should().NotThrowAsync();
        Directory.GetFiles(CrashDirectory).Should().ContainSingle();
        _error.ToString().Should().Contain("message box failed");
    }

    [Fact]
    public void Report_WithoutInterface_ShouldOnlyWriteTheReport()
    {
        var handler = new CrashHandler(new CrashReportService(CrashDirectory), _error);

        var path = handler.Report(new InvalidOperationException("boom"), CrashHandler.FatalOrigin);

        path.Should().NotBeNull();
        File.ReadAllText(path!).Should().Contain("Origin: " + CrashHandler.FatalOrigin);
        handler.CrashDirectory.Should().Be(CrashDirectory);
    }

    /// <summary>Keeps its error message box open until <see cref="Close"/>.</summary>
    private sealed class BlockingDialogService : IDialogService
    {
        private TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> ShownErrors { get; } = [];

        public void Close()
        {
            var open = _open;
            _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            open.SetResult();
        }

        public Task ShowErrorAsync(string title, string message)
        {
            ShownErrors.Add(message);
            return ShownErrors.Count == 1 ? _open.Task : Task.CompletedTask;
        }

        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);

        public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileDialogFilter>? filters = null, string? initialDirectory = null) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string title, string defaultFileName, IReadOnlyList<FileDialogFilter>? filters = null, string? initialDirectory = null) =>
            Task.FromResult<string?>(null);

        public Task ShowInfoAsync(string title, string message) => Task.CompletedTask;

        public Task ShowWarningAsync(string title, string message) => Task.CompletedTask;

        public Task<bool> ShowConfirmationAsync(string title, string message) => Task.FromResult(false);

        public Task OpenUrlAsync(string url) => Task.CompletedTask;

        public Task<bool> OpenFileAsync(string path) => Task.FromResult(false);

        public Task ShowPreferencesAsync(PreferencesViewModel preferences) => Task.CompletedTask;

        public Task<bool> ShowDatLotAsync(DatLotViewModel lot) => Task.FromResult(false);
    }

    private sealed class FailingDialogService : IDialogService
    {
        public Task ShowErrorAsync(string title, string message) => throw new InvalidOperationException("message box failed");

        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);

        public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileDialogFilter>? filters = null, string? initialDirectory = null) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string title, string defaultFileName, IReadOnlyList<FileDialogFilter>? filters = null, string? initialDirectory = null) =>
            Task.FromResult<string?>(null);

        public Task ShowInfoAsync(string title, string message) => Task.CompletedTask;

        public Task ShowWarningAsync(string title, string message) => Task.CompletedTask;

        public Task<bool> ShowConfirmationAsync(string title, string message) => Task.FromResult(false);

        public Task OpenUrlAsync(string url) => Task.CompletedTask;

        public Task<bool> OpenFileAsync(string path) => Task.FromResult(false);

        public Task ShowPreferencesAsync(PreferencesViewModel preferences) => Task.CompletedTask;

        public Task<bool> ShowDatLotAsync(DatLotViewModel lot) => Task.FromResult(false);
    }
}
