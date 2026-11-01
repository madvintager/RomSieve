using Avalonia.Controls;
using Avalonia.Threading;
using RomSieve.App.ViewModels;
using RomSieve.App.Views;

namespace RomSieve.App.Tests;

/// <summary>
/// The Headless session itself: what a test leaves behind never breaks the next one (see <see cref="HeadlessTestSession"/>).
/// Each dispatch below stands for one test. With one application per test, both failed every time on
/// <c>'fonts:SystemFonts'</c>, and in a full run the same failure came at random and spread to every later window.
/// </summary>
public sealed partial class GuiTests
{
    [Fact]
    public async Task HeadlessSession_TestsRightAfterOneAnother_ShouldEachShowTheirWindow()
    {
        await RunOnUiThreadAsync(ShowEveryStepOfANewWindow);
        await RunOnUiThreadAsync(ShowEveryStepOfANewWindow);
    }

    [Fact]
    public async Task HeadlessSession_WorkLeftByATest_ShouldNotBreakTheNextOnes()
    {
        Window? left = null;
        MainViewModel? leftViewModel = null;
        await RunOnUiThreadAsync(() =>
        {
            // The pattern preview refreshes after a pause of typing: the test ends before it does
            leftViewModel = CreateViewModel();
            left = new MainWindow(leftViewModel);
            left.Show();
            leftViewModel.Navigation.CurrentStep = WorkflowStep.Output;
            Settle(left);
            leftViewModel.Directories.SourceDir = _tempDir;
            leftViewModel.Directories.Pattern = "{title}/left/";
        });
        await Task.Delay(PatternPreviewViewModel.DefaultDebounce * 3);
        await RunOnUiThreadAsync(() =>
        {
            ShowEveryStepOfANewWindow();
            leftViewModel!.Directories.Pattern = "{title}/left again/";
            left!.Close();
        });
        await Task.Delay(PatternPreviewViewModel.DefaultDebounce * 3);
        await RunOnUiThreadAsync(ShowEveryStepOfANewWindow);
    }

    private void ShowEveryStepOfANewWindow()
    {
        var vm = CreateViewModel();
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            foreach (var step in Enum.GetValues<WorkflowStep>())
            {
                vm.Navigation.CurrentStep = step;
                Settle(window);
            }

            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            window.Close();
        }
    }
}
