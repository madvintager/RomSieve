using System;
using Avalonia;
using RomSieve.App.Services;
using RomSieve.Core.Services;

namespace RomSieve.App;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Installed first, so that an exception while the application starts leaves its report as well
        var configPath = new ConfigurationService().GetDefaultConfigPath();
        CrashHandler.Current = new CrashHandler(new CrashReportService(CrashReportService.GetCrashDirectory(configPath)), Console.Error);
        CrashHandler.Current.InstallProcessHandlers();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
