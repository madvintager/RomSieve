using Microsoft.Extensions.DependencyInjection;
using RomSieve.App.Abstractions;
using RomSieve.App.Services;
using RomSieve.App.ViewModels;

namespace RomSieve.App.Tests;

/// <summary>
/// No dialog service answers in the user's place: the application ships none that accepts the confirmations (purge,
/// emptying of the quarantine), and the container falls back on none. A broken wiring fails instead of confirming.
/// </summary>
public sealed class DialogServiceWiringTests
{
    [Fact]
    public void AddRomSieveApp_ShouldRegisterNoDialogService()
    {
        var services = new ServiceCollection().AddRomSieveApp();

        services.Should().NotContain(d => d.ServiceType == typeof(IDialogService));
    }

    [Fact]
    public void CreateMainViewModel_WithoutDialogService_ShouldThrow()
    {
        var act = () => AppServiceCollectionExtensions.CreateMainViewModel(dialogService: null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("dialogService");
    }

    [Fact]
    public void Application_ShouldShipOnlyTheDialogServiceThatShowsTheDialogs()
    {
        var implementations = typeof(MainViewModel).Assembly.GetTypes()
            .Where(t => typeof(IDialogService).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

        implementations.Should().Equal(typeof(AvaloniaDialogService));
    }

    [Fact]
    public async Task AvaloniaDialogService_ConfirmationWithoutWindow_ShouldBeRefused()
    {
        var dialogs = new AvaloniaDialogService(() => null);

        (await dialogs.ShowConfirmationAsync("Purge", "Purge the destination?")).Should().BeFalse();
    }
}
