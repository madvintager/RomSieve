using FluentAssertions;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// Log of a run of the interface written to a file (<see cref="RunLogFile"/>): headed by what runs and the version, each
/// line kept at once, the last logs only, and never a failure for the run.
/// </summary>
public sealed class RunLogFileTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Log_ShouldHoldItsTitleAndEachLineAsSoonAsItIsWritten()
    {
        var directory = Path.Combine(_temp.Path, "logs");

        using var log = RunLogFile.Create(directory, "Simulation (Dry-Run)");
        log.Should().NotBeNull();
        log!.WriteLine("[INFO] first");
        log.WriteLine("[WARN] second");

        // Shared for reading while it is written
        var lines = ReadLines(log.Path);
        lines.Should().HaveCount(3);
        lines[0].Should().StartWith("RomSieve ").And.Contain(" - Simulation (Dry-Run) - ");
        lines[1].Should().MatchRegex(@"^\d\d:\d\d:\d\d \[INFO\] first$");
        lines[2].Should().EndWith("[WARN] second");
        Path.GetDirectoryName(log.Path).Should().Be(directory);
        Path.GetFileName(log.Path).Should().StartWith("run-").And.EndWith(".log");
    }

    [Fact]
    public void Log_Closed_ShouldIgnoreTheNextLines()
    {
        var log = RunLogFile.Create(_temp.Path, "Sort")!;
        log.Dispose();

        var act = () => log.WriteLine("[INFO] after the end");

        act.Should().NotThrow();
        ReadLines(log.Path).Should().ContainSingle();
    }

    [Fact]
    public void Create_ShouldOnlyKeepTheLastLogs()
    {
        var directory = _temp.CreateSubdirectory("logs");
        var old = Enumerable.Range(0, RunLogFile.KeptLogCount + 3)
            .Select(i => _temp.WriteText(Path.Combine("logs", $"run-2020{i / 28 + 1:00}{i % 28 + 1:00}-120000-000-{i:x6}.log"), "old"))
            .ToList();
        var other = _temp.WriteText(Path.Combine("logs", "notes.log"), "not a run log");

        using var log = RunLogFile.Create(directory, "Sort")!;

        Directory.EnumerateFiles(directory, "run-*.log").Should()
            .BeEquivalentTo(old.TakeLast(RunLogFile.KeptLogCount - 1).Append(log.Path));
        File.Exists(other).Should().BeTrue("only run logs are deleted");
    }

    [Fact]
    public void Create_InAFolderThatCannotBeCreated_ShouldGiveNoLog()
    {
        // A file where the folder should be
        var file = _temp.WriteText("logs", "a file");

        RunLogFile.Create(file, "Sort").Should().BeNull();
    }

    [Fact]
    public void LogDirectory_ShouldBeNextToTheConfiguration()
    {
        var config = Path.Combine(_temp.Path, "config", "config.json");

        RunLogFile.GetLogDirectory(config).Should().Be(Path.Combine(_temp.Path, "config", "logs"));
    }

    private static string[] ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }
}
