using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System.Globalization;
using Avalonia.Threading;
using RomSieve.App.Services;
using RomSieve.App.Views;
using RomSieve.Core.Services;

namespace RomSieve.App;

/// <summary>
/// Main Avalonia UI application.
/// </summary>
public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var defaultConfigPath = new ConfigurationService().GetDefaultConfigPath();
            MainWindow? window = null;
            var dialogs = new AvaloniaDialogService(() => window);
            // Until the configuration names a language (first launch), the interface speaks that of the system
            var localization = new LocalizationService(LocalizationService.LocaleOf(CultureInfo.CurrentUICulture));
            window = new MainWindow(configFilePath: defaultConfigPath, dialogService: dialogs, localization: localization);
            desktop.MainWindow = window;

            // Attached here rather than by the window, which the headless tests create
            CrashHandler.Current?.AttachInterface(Dispatcher.UIThread, dialogs, window.ViewModel.Localization, window.ViewModel.Log);

            // Started here rather than by the window, which the headless tests create without any network access
            window.ViewModel.Updates.TriggerStartupCheck();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
