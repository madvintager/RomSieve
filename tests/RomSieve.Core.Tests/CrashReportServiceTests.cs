using System.IO;
using FluentAssertions;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// Tests for the crash reports written when an exception is caught by nothing else.
/// </summary>
public sealed class CrashReportServiceTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void GetCrashDirectory_ShouldBeNextToTheConfigurationFile()
    {
        var configPath = _temp.GetFilePath(Path.Combine("RomSieve", "config.json"));

        CrashReportService.GetCrashDirectory(configPath)
            .Should().Be(Path.Combine(_temp.Path, "RomSieve", "crashes"));
    }

    [Fact]
    public void WriteReport_ShouldWriteTheExceptionWithItsContext()
    {
        var service = new CrashReportService(_temp.GetFilePath("crashes"));
        var exception = Throw(new InvalidOperationException("outer failure", new IOException("inner failure")));

        var path = service.WriteReport(exception, "Interface thread");

        path.Should().NotBeNull();
        Path.GetDirectoryName(path).Should().Be(_temp.GetFilePath("crashes"));
        Path.GetFileName(path).Should().StartWith("crash-").And.EndWith(".log");
        var text = File.ReadAllText(path!);
        text.Should().StartWith("RomSieve crash report");
        text.Should().Contain("Version: " + ProductVersion.Current);
        text.Should().Contain("Origin: Interface thread");
        text.Should().Contain("OS: ");
        text.Should().Contain("Runtime: ");
        text.Should().Contain("System.InvalidOperationException: outer failure");
        text.Should().Contain("System.IO.IOException: inner failure");
        text.Should().Contain(nameof(Throw), "the stack trace is written");
    }

    [Fact]
    public void WriteReport_SeveralAtOnce_ShouldNeverReplaceAnother()
    {
        var service = new CrashReportService(_temp.GetFilePath("crashes"));

        var paths = Enumerable.Range(0, 20)
            .Select(i => service.WriteReport(new InvalidOperationException("failure " + i), "test"))
            .ToList();

        paths.Should().OnlyContain(p => p != null).And.OnlyHaveUniqueItems();
        Directory.GetFiles(_temp.GetFilePath("crashes")).Should().HaveCount(20);
    }

    [Fact]
    public void WriteReport_InAFolderThatCannotBeCreated_ShouldReturnNullWithoutThrowing()
    {
        // A file where the folder should be
        var blocked = _temp.WriteText("crashes", "not a folder");
        var service = new CrashReportService(blocked);

        var act = () => service.WriteReport(new InvalidOperationException("failure"), "test");

        act.Should().NotThrow();
        act().Should().BeNull();
    }

    private static Exception Throw(Exception exception)
    {
        try
        {
            throw exception;
        }
        catch (Exception caught)
        {
            return caught;
        }
    }
}
