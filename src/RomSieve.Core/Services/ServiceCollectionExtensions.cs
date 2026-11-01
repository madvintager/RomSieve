using Microsoft.Extensions.DependencyInjection;
using RomSieve.Core.Abstractions;

namespace RomSieve.Core.Services;

/// <summary>
/// Extension methods to register all <c>RomSieve.Core</c> services
/// in the .NET dependency injection container.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRomSieveCore(this IServiceCollection services)
    {
        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddSingleton<ITosecParser, TosecParser>();
        services.AddSingleton<INoIntroRedumpParser, NoIntroRedumpParser>();
        services.AddSingleton<IMameDescriptionParser, MameDescriptionParser>();
        services.AddSingleton<IChecksumService, ChecksumService>();
        services.AddSingleton<ICrcCacheService, CrcCacheService>();
        services.AddSingleton<IDatService, DatService>();
        services.AddSingleton<IReleaseScoringService, ReleaseScoringService>();
        services.AddSingleton<IGameKeyService, GameKeyService>();
        services.AddSingleton<IFilterEvaluator, FilterEvaluator>();
        services.AddSingleton<IRomFilterService, RomFilterService>();
        services.AddSingleton<IPathOrganizerService, PathOrganizerService>();
        services.AddSingleton<IOpticalCompanionService, OpticalCompanionService>();
        services.AddSingleton<IOperationJournalService>(sp => new OperationJournalService(
            OperationJournalService.GetJournalDirectory(sp.GetRequiredService<IConfigurationService>().GetDefaultConfigPath()),
            sp.GetRequiredService<IChecksumService>(),
            sp.GetRequiredService<ICrcCacheService>()));
        services.AddSingleton<ICrashReportService>(sp => new CrashReportService(
            CrashReportService.GetCrashDirectory(sp.GetRequiredService<IConfigurationService>().GetDefaultConfigPath())));
        services.AddSingleton<ISortProcessorService, SortProcessorService>();
        // The update check owns its HTTP client, created once for the lifetime of the application
        services.AddSingleton<IUpdateCheckService>(_ => new UpdateCheckService(new HttpClient()));
        return services;
    }
}
