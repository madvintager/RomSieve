using Avalonia.Controls;
using RomSieve.App.Services;

namespace RomSieve.App.Tests;

/// <summary>
/// The dialog service of the application refuses a confirmation it cannot show: it used to accept it, so that a purge
/// asked while no window could show the question went ahead without the user.
/// </summary>
public sealed partial class GuiTests
{
    [Fact]
    public async Task AvaloniaDialogService_ConfirmationOverAHiddenWindow_ShouldBeRefused()
    {
        await RunOnUiThreadAsync(async () =>
        {
            var window = new Window();
            var dialogs = new AvaloniaDialogService(() => window);

            (await dialogs.ShowConfirmationAsync("Purge", "Purge the destination?")).Should().BeFalse();
        });
    }

    [Fact]
    public async Task AvaloniaDialogService_ConfirmationOverAWindowWithoutViewModel_ShouldBeRefused()
    {
        await RunOnUiThreadAsync(async () =>
        {
            var window = new Window();
            window.Show();
            try
            {
                var dialogs = new AvaloniaDialogService(() => window);

                (await dialogs.ShowConfirmationAsync("Purge", "Purge the destination?")).Should().BeFalse();
            }
            finally
            {
                window.Close();
            }
        });
    }
}
