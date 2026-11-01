using System.Text.RegularExpressions;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// THIRD-PARTY-NOTICES.txt, at the root of the repository: it names every package that the Core brings into the
/// executables, and the files of licenses/ it refers to are exactly those that src/ThirdPartyNotices.targets copies at
/// each publish. The tests of the interface and of the command line check the packages of their own executable.
/// </summary>
public sealed partial class ThirdPartyNoticesTests
{
    [GeneratedRegex(@"licenses/([\w.-]+)-THIRD-PARTY-NOTICES\.txt", RegexOptions.CultureInvariant)]
    private static partial Regex LicensesFileRegex();

    [GeneratedRegex(@"NoticesName=""([^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex NoticesNameRegex();

    /// <summary>A file of the repository, whose root holds the notices; the tests always run from a clone of it.</summary>
    private static string RepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ShippedPackages.NoticesFileName)))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the root of the repository holds the third-party notices");
        return Path.Combine([directory!.FullName, .. parts]);
    }

    [Fact]
    public void EveryPackageOfTheCore_ShouldBeNamedInTheNotices()
    {
        var packages = ShippedPackages.Read(Path.Combine(AppContext.BaseDirectory, "RomSieve.Core.Tests.deps.json"), "RomSieve.Core");

        // The packages of the Core only, not those of its tests
        packages.Should().Contain(["SharpCompress", "System.IO.Hashing", "Microsoft.Extensions.Logging.Abstractions"])
            .And.NotContain(["xunit", "FluentAssertions", "CsCheck"]);
        ShippedPackages.MissingFrom(File.ReadAllText(RepositoryFile(ShippedPackages.NoticesFileName)), packages)
            .Should().Equal([], "the licenses of every component an executable embeds accompany it");
    }

    [Fact]
    public void LicensesFolder_ShouldHoldExactlyTheFilesTheNoticesReferTo()
    {
        var named = LicensesFileRegex().Matches(File.ReadAllText(RepositoryFile(ShippedPackages.NoticesFileName)))
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        var copied = NoticesNameRegex().Matches(File.ReadAllText(RepositoryFile("src", "ThirdPartyNotices.targets")))
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        copied.Should().Contain(["dotnet-runtime", "SkiaSharp", "HarfBuzzSharp", "CommunityToolkit.Mvvm"]);
        named.Should().BeEquivalentTo(copied, "every file of licenses/ is written by the publish, and the notices tell what it holds");
    }
}
