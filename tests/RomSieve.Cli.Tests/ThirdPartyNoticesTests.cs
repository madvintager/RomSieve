using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Cli.Tests;

/// <summary>
/// Licenses of the third-party components of the command line: THIRD-PARTY-NOTICES.txt, which the build copies next to
/// the executable, names every package it embeds.
/// </summary>
public sealed class ThirdPartyNoticesTests
{
    [Fact]
    public void EveryPackageOfTheCommandLine_ShouldBeNamedInTheNotices()
    {
        var packages = ShippedPackages.Read(Path.Combine(AppContext.BaseDirectory, "romsieve-cli.deps.json"));

        packages.Should().Contain(["System.CommandLine", "SharpCompress"]);
        ShippedPackages.MissingFrom(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, ShippedPackages.NoticesFileName)), packages)
            .Should().Equal([], "the licenses of every component the command line embeds accompany it");
    }
}
