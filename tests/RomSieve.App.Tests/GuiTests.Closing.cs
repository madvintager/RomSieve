using Avalonia.Threading;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.App.Views;

namespace RomSieve.App.Tests;

/// <summary>
/// Closing the window during a real sort or an undo: ending the process in their middle would leave the file being
/// written, so the window asks, stops the sort, and only closes once it has ended. A simulation does not hold it.
/// </summary>
public sealed partial class GuiTests
{
    /// <summary>Puts the execution in the state of a running real sort, or of a running simulation.</summary>
    private static void SetRunning(ExecutionViewModel execution, bool running, bool isDryRun = false)
    {
        if (running)
        {
            execution.IsDryRunOperation = isDryRun;
            execution.IsPipelineRunning = true;
            execution.IsRunning = true;
        }
        else
        {
            execution.IsRunning = false;
            execution.IsPipelineRunning = false;
        }
    }

    [Fact]
    public async Task ClosingDuringASort_ShouldAskThenCloseOnceTheSortHasEnded()
    {
        await RunOnUiThreadAsync(() =>
        {
            var dialogs = new HeadlessDialogService();
            var vm = CreateViewModel(dialogs);
            var window = new MainWindow(vm);
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Show();
            try
            {
                SetRunning(vm.Execution, running: true);

                window.Close();
                Dispatcher.UIThread.RunJobs();
                closed.Should().BeFalse("ending the process would leave the file being written");
                dialogs.ShownMessages.Should().ContainSingle()
                    .Which.Should().Be(("CONFIRM", "Fermer RomSieve", vm.Localization.Tr("dialog.close_while_sorting_html")));

                window.Close();
                Dispatcher.UIThread.RunJobs();
                closed.Should().BeFalse();
                dialogs.ShownMessages.Should().ContainSingle("the window already waits for the end of the sort");

                SetRunning(vm.Execution, running: false);
                Dispatcher.UIThread.RunJobs();
                closed.Should().BeTrue("the sort has ended");
            }
            finally
            {
                SetRunning(vm.Execution, running: false);
                if (!closed)
                {
                    window.Close();
                }
            }
        });
    }

    [Fact]
    public async Task ClosingDuringASort_Refused_ShouldKeepTheWindowOpen()
    {
        await RunOnUiThreadAsync(() =>
        {
            var dialogs = new HeadlessDialogService { NextConfirmationResult = false };
            var vm = CreateViewModel(dialogs);
            var window = new MainWindow(vm);
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Show();
            try
            {
                SetRunning(vm.Execution, running: true);

                window.Close();
                SetRunning(vm.Execution, running: false);
                Dispatcher.UIThread.RunJobs();

                closed.Should().BeFalse("the user chose to let the sort go on");
                window.Close();
                closed.Should().BeTrue("nothing runs any more");
            }
            finally
            {
                SetRunning(vm.Execution, running: false);
                if (!closed)
                {
                    window.Close();
                }
            }
        });
    }

    [Fact]
    public async Task ClosingDuringASimulation_ShouldCloseAtOnce()
    {
        await RunOnUiThreadAsync(() =>
        {
            var dialogs = new HeadlessDialogService();
            var vm = CreateViewModel(dialogs);
            var window = new MainWindow(vm);
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Show();
            try
            {
                SetRunning(vm.Execution, running: true, isDryRun: true);

                window.Close();

                closed.Should().BeTrue("a simulation writes nothing");
                dialogs.ShownMessages.Should().BeEmpty();
            }
            finally
            {
                SetRunning(vm.Execution, running: false);
                if (!closed)
                {
                    window.Close();
                }
            }
        });
    }
}
