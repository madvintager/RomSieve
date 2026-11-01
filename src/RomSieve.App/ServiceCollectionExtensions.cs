using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Services;

namespace RomSieve.App;

/// <summary>
/// Extension methods to register the presentation layer (<see cref="MainViewModel"/>)
/// and <c>RomSieve.Core</c> services into the dependency injection container.
/// </summary>
public static class AppServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <c>RomSieve.Core</c> services and the <see cref="MainViewModel"/>. No <see cref="IDialogService"/>
    /// is registered: the caller provides the one that shows the dialogs, since a default one would answer the
    /// confirmations (purge, emptying of the quarantine) in the user's place.
    /// </summary>
    public static IServiceCollection AddRomSieveApp(this IServiceCollection services)
    {
        services.AddRomSieveCore();
        services.AddTransient<MainViewModel>();
        return services;
    }

    /// <summary>
    /// Creates a standalone instance of <see cref="MainViewModel"/> wired with all <c>RomSieve.Core</c> services.
    /// </summary>
    public static MainViewModel CreateMainViewModel(
        IDialogService dialogService,
        string? configFilePath = null,
        IUpdateCheckService? updateCheckService = null,
        ILocalizationService? localization = null)
    {
        ArgumentNullException.ThrowIfNull(dialogService);
        var services = new ServiceCollection();
        services.AddRomSieveApp();
        services.AddSingleton(dialogService);

        if (updateCheckService is not null)
        {
            services.Replace(ServiceDescriptor.Singleton(updateCheckService));
        }

        if (localization is not null)
        {
            services.Replace(ServiceDescriptor.Singleton(localization));
        }

        if (!string.IsNullOrEmpty(configFilePath))
        {
            // The CRC cache and the journals live next to the configuration file in use
            // (isolated configurations, such as those of the tests, keep an isolated cache and isolated journals)
            services.Replace(ServiceDescriptor.Singleton<ICrcCacheService>(_ => new CrcCacheService(
                Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configFilePath)) ?? string.Empty, "crc_cache.json"))));
            services.Replace(ServiceDescriptor.Singleton<IOperationJournalService>(sp => new OperationJournalService(
                OperationJournalService.GetJournalDirectory(configFilePath),
                sp.GetRequiredService<IChecksumService>(),
                sp.GetRequiredService<ICrcCacheService>())));
        }

        var provider = services.BuildServiceProvider();
        return new MainViewModel(
            provider.GetRequiredService<ISortProcessorService>(),
            provider.GetRequiredService<IDatService>(),
            provider.GetRequiredService<IRomFilterService>(),
            provider.GetRequiredService<INoIntroRedumpParser>(),
            provider.GetRequiredService<IPathOrganizerService>(),
            provider.GetRequiredService<IMameDescriptionParser>(),
            provider.GetRequiredService<IConfigurationService>(),
            provider.GetRequiredService<ILocalizationService>(),
            provider.GetRequiredService<IOperationJournalService>(),
            provider.GetRequiredService<IUpdateCheckService>(),
            provider.GetRequiredService<IDialogService>(),
            configFilePath);
    }
}
