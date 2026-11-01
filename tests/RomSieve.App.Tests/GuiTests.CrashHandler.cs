using Avalonia.Threading;
using RomSieve.App.Abstractions;
using RomSieve.App.Services;
using RomSieve.App.ViewModels;
using RomSieve.Core.Services;

namespace RomSieve.App.Tests;

/// <summary>
/// Global handler of the exceptions on the real dispatcher of the interface: an exception nothing caught is reported
/// and told to the user instead of closing the application.
/// </summary>
public sealed partial class GuiTests
{
    [Fact]
    public async Task CrashHandler_ExceptionOnTheInterfaceThread_ShouldBeReportedAndToldToTheUser()
    {
        var crashDirectory = Path.Combine(_tempDir, "crashes");
        var dialogs = new HeadlessDialogService();
        var localization = new LocalizationService("fr");
        var log = new LogViewModel(localization);
        var handler = new CrashHandler(new CrashReportService(crashDirectory), TextWriter.Null);
        var ranAfter = false;

        await RunOnUiThreadAsync(() =>
        {
            handler.AttachInterface(Dispatcher.UIThread, dialogs, localization, log);
            try
            {
                Dispatcher.UIThread.Post(() => throw new InvalidOperationException("boom"));
                Dispatcher.UIThread.Post(() => ranAfter = true);
                Dispatcher.UIThread.RunJobs();
            }
            finally
            {
                // The dispatcher is shared by every test of the session
                handler.DetachInterface();
            }
        });

        ranAfter.Should().BeTrue("the interface keeps running after the exception");
        var report = Directory.GetFiles(crashDirectory).Should().ContainSingle().Subject;
        File.ReadAllText(report).Should().Contain("System.InvalidOperationException: boom");
        dialogs.ShownMessages.Should().ContainSingle(m => m.Level == "ERROR")
            .Which.Message.Should().Contain(report);
        log.Entries.Should().ContainSingle().Which.Level.Should().Be("ERROR");
    }
}
