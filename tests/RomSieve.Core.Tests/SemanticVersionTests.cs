using FluentAssertions;
using RomSieve.Core.Models;
using Xunit;

namespace RomSieve.Core.Tests;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("1.0.0", 1, 0, 0, null, null, false)]
    [InlineData("v1.2.3", 1, 2, 3, null, null, false)]
    [InlineData("V2.4", 2, 4, 0, null, null, false)]
    [InlineData("3", 3, 0, 0, null, null, false)]
    [InlineData("1.2.0-beta1", 1, 2, 0, "beta1", null, false)]
    [InlineData("v1.2.0-rc.2", 1, 2, 0, "rc.2", null, false)]
    [InlineData("0.0.0-dev", 0, 0, 0, "dev", null, true)]
    [InlineData("0.0.0-dev+45acd3b", 0, 0, 0, "dev", "45acd3b", true)]
    public void TryParse_ShouldParseValidVersions(
        string input,
        int major,
        int minor,
        int patch,
        string? prerelease,
        string? build,
        bool isDev)
    {
        SemanticVersion.TryParse(input, out var version).Should().BeTrue();
        version.Major.Should().Be(major);
        version.Minor.Should().Be(minor);
        version.Patch.Should().Be(patch);
        version.Prerelease.Should().Be(prerelease);
        version.Build.Should().Be(build);
        version.IsDev.Should().Be(isDev);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("v.1.2")]
    public void TryParse_ShouldReturnFalseForInvalidInput(string? input)
    {
        SemanticVersion.TryParse(input, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("v1.2.0", "1.2.0")]
    [InlineData("1.2.3-beta1", "1.2.3-beta1")]
    [InlineData("V2.0", "2.0.0")]
    public void Clean_ShouldStripPrefixV(string input, string expected)
    {
        SemanticVersion.Clean(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.1.0", "1.0.9", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("v1.2.3", "v1.2.2", true)]
    [InlineData("1.0.0", "1.0.0-beta", true)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.1", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("v1.0.0", "1.0.0", false)]
    [InlineData("1.0.0", "1.0.1", false)]
    [InlineData("1.0.0-beta", "1.0.0", false)]
    [InlineData("1.0.0", "0.0.0-dev", true)]
    public void IsNewer_ShouldCompareCorrectly(string candidate, string baseline, bool expected)
    {
        SemanticVersion.IsNewer(candidate, baseline).Should().Be(expected);
    }
}

