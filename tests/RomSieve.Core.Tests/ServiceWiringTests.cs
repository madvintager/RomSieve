using System.Reflection;
using FluentAssertions;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Services;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// The services of the Core are only created by the composition root: no static instance of a service, which would be
/// another instance than the one the application registers, and no pipeline step that builds its own services.
/// </summary>
public sealed class ServiceWiringTests
{
    private static readonly Assembly Core = typeof(SortProcessorService).Assembly;

    private static bool IsService(Type type) =>
        type.GetInterfaces().Any(i => i.Namespace == typeof(IChecksumService).Namespace);

    [Fact]
    public void Core_ShouldHoldNoStaticInstanceOfAService()
    {
        var staticServices = Core.GetTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(f => !f.IsLiteral && (IsService(f.FieldType) || (f.FieldType.IsInterface && f.FieldType.Namespace == typeof(IChecksumService).Namespace)))
                .Select(f => $"{t.Name}.{f.Name}"))
            .ToList();

        staticServices.Should().BeEmpty();
    }

    [Fact]
    public void TransferExecutor_ShouldRequireTheServicesItUses()
    {
        foreach (var constructor in typeof(FileTransferExecutor).GetConstructors())
        {
            var parameters = constructor.GetParameters();
            foreach (var service in new[] { typeof(IChecksumService), typeof(IRomFilterService), typeof(IOpticalCompanionService) })
            {
                parameters.Should().Contain(p => p.ParameterType == service && !p.IsOptional, $"{service.Name} is given, never created");
            }
        }
    }

    [Fact]
    public void Processor_ShouldRequireTheMameParser()
    {
        typeof(SortProcessorService).GetConstructors().Should().OnlyContain(c =>
            c.GetParameters().Any(p => p.ParameterType == typeof(IMameDescriptionParser) && !p.IsOptional));
    }
}
