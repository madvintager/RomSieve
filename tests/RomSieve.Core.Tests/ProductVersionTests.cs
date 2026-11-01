using RomSieve.Core.Services;

namespace RomSieve.Core.Tests;

/// <summary>
/// Version shown by the window title and by romsieve-cli --version: 0.0.0-dev for a local build, the version of the
/// tag for a release, always without the commit hash appended by the SDK.
/// </summary>
public sealed class ProductVersionTests
{
    [Fact]
    public void Current_ShouldBeASemanticVersionWithoutCommitHash()
    {
        ProductVersion.Current.Should().MatchRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$");
        ProductVersion.Current.Should().NotContain("+");
    }
}
