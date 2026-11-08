using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// The container of the Core wires every dependency itself: the services no longer create a missing one, which used to
/// hide a broken registration behind a second instance (a CRC cache other than the one the application saves).
/// </summary>
public sealed class ServiceCollectionExtensionsTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>The services of the Core, with a CRC cache and journals in the temporary folder of the test.</summary>
    private ServiceProvider BuildIsolatedProvider()
    {
        var services = new ServiceCollection().AddRomSieveCore();
        services.Replace(ServiceDescriptor.Singleton<ICrcCacheService>(
            new CrcCacheService(_temp.GetFilePath("crc_cache.json"))));
        services.Replace(ServiceDescriptor.Singleton<IOperationJournalService>(sp => new OperationJournalService(
            _temp.GetFilePath("journals"),
            sp.GetRequiredService<IChecksumService>(),
            sp.GetRequiredService<ICrcCacheService>())));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Container_ShouldGiveTheSortProcessorTheRegisteredCrcCache()
    {
        using var provider = BuildIsolatedProvider();

        var processor = provider.GetRequiredService<ISortProcessorService>();

        processor.Should().BeOfType<SortProcessorService>()
            .Which.CrcCache.Should().BeSameAs(provider.GetRequiredService<ICrcCacheService>());
    }

    [Fact]
    public void Container_ShouldPlaceTheCacheOfTheDatHeadersNextToTheRegisteredCrcCache()
    {
        using var provider = BuildIsolatedProvider();

        provider.GetRequiredService<IDatHeaderCache>().Should().BeOfType<DatHeaderCacheService>()
            .Which.CachePath.Should().Be(_temp.GetFilePath(DatHeaderCacheService.FileName));
        provider.GetRequiredService<IDatService>().Should().BeOfType<DatService>();
    }

    [Fact]
    public void Container_ShouldResolveTheUpdateCheck()
    {
        using var provider = BuildIsolatedProvider();

        provider.GetRequiredService<IUpdateCheckService>().Should().BeOfType<UpdateCheckService>();
    }
}
