using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Cli.Tests;

/// <summary>
/// <c>--frontend</c> on <c>run</c> and <c>patches</c>: it replaces the frontend of the configuration for the run, each DAT
/// going into the folder of its platform; an unknown value is refused (code 2) with the list of the values;
/// <c>--dir-dat-mirror</c> is refused with a frontend given on the command line; the summary says the frontend again,
/// with the DATs without a platform and the folders that gather several DATs, even with <c>--quiet</c>.
/// </summary>
public sealed class CliFrontendTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly string _configPath;
    private readonly string _source;
    private readonly string _destination;
    private readonly string _dats;

    public CliFrontendTests()
    {
        _configPath = Path.Combine(_temp.CreateSubdirectory("config"), "config.json");
        _source = _temp.CreateSubdirectory("source");
        _destination = Path.Combine(_temp.Path, "destination");
        _dats = _temp.CreateSubdirectory("dats");
        WriteDat("gb.dat", "Nintendo - Game Boy", ("Tetris (World)", "Tetris (World).gb", "tetris gb"));
        WriteDat("gb-private.dat", "Nintendo - Game Boy (Private)", ("Alleyway (World)", "Alleyway (World).gb", "alleyway gb"));
        WriteDat("homebrew.dat", "Homebrew - Things", ("Alpha (World)", "Alpha (World).gb", "alpha gb"));
        File.WriteAllText(Path.Combine(_source, "tetris.gb"), "tetris gb");
        File.WriteAllText(Path.Combine(_source, "alleyway.gb"), "alleyway gb");
        File.WriteAllText(Path.Combine(_source, "alpha.gb"), "alpha gb");
        WriteConfig();
    }

    public void Dispose() => _temp.Dispose();

    private static string Crc(string text) => Crc32.HashToUInt32(Encoding.UTF8.GetBytes(text)).ToString("x8");

    private void WriteDat(string fileName, string name, params (string Game, string Rom, string Data)[] games) =>
        _temp.WriteText(Path.Combine("dats", fileName), $"""
            <?xml version="1.0"?>
            <datafile>
              <header><name>{name}</name><description>{name}</description></header>
            {string.Concat(games.Select(g => $"""  <game name="{g.Game}"><description>{g.Game}</description><rom name="{g.Rom}" size="{Encoding.UTF8.GetByteCount(g.Data)}" crc="{Crc(g.Data)}"/></game>"""))}
            </datafile>
            """);

    private void WriteConfig(Action<SortSettings>? configure = null)
    {
        var settings = new SortSettings { SourceDir = _source, DestinationDir = _destination, DatFile = _dats, Pattern = "{title}" };
        configure?.Invoke(settings);
        var data = new Dictionary<string, object?>(StringComparer.Ordinal) { ["ui_locale"] = "en" };
        SortSettingsSerializer.Write(settings, data);
        File.WriteAllText(_configPath, JsonSerializer.Serialize(data));
    }

    private sealed record CliResult(int ExitCode, string Output, string Error);

    private async Task<CliResult> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await new CliApplication(output, error, _configPath).RunAsync(args);
        return new CliResult(exitCode, output.ToString(), error.ToString());
    }

    private string[] DestinationFiles() =>
        Directory.Exists(_destination)
            ? Directory.EnumerateFiles(_destination, "*", SearchOption.AllDirectories)
                .Where(f => !DestinationGuard.IsGuardPath(f))
                .Select(f => Path.GetRelativePath(_destination, f).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];

    [Fact]
    public async Task Frontend_ShouldReplaceTheOneOfTheConfiguration_ForTheRun()
    {
        WriteConfig(s => s.Frontend = FrontendTarget.Batocera);

        var result = await RunAsync("run", "--frontend", "mister", "--execute");

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        DestinationFiles().Should().Equal(
            "GAMEBOY/Alleyway/Alleyway (World).gb",
            "GAMEBOY/Tetris/Tetris (World).gb",
            "Homebrew - Things/Alpha/Alpha (World).gb");
        File.ReadAllText(_configPath).Should().Contain("\"batocera\"", "the command line never writes the configuration");
    }

    [Fact]
    public async Task FrontendNone_ShouldGiveEachDatItsFolder()
    {
        WriteConfig(s => s.Frontend = FrontendTarget.Batocera);

        var result = await RunAsync("run", "--frontend", "none", "--execute");

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        DestinationFiles().Should().Contain("Nintendo - Game Boy/Tetris/Tetris (World).gb");
        result.Output.Should().NotContain("Frontend:");
    }

    [Fact]
    public async Task UnknownFrontend_ShouldBeRefused_WithTheValues()
    {
        var result = await RunAsync("run", "--frontend", "launchbox");

        result.ExitCode.Should().Be(ExitCodes.InvalidArguments);
        result.Error.Should().Contain("launchbox").And.Contain("es-de").And.Contain("mister");
    }

    [Fact]
    public async Task Mirror_WithTheFrontendOfTheCommandLine_ShouldBeRefused()
    {
        var result = await RunAsync("run", "--frontend", "retropie", "--dir-dat-mirror");

        result.ExitCode.Should().Be(ExitCodes.InvalidArguments);
        result.Error.Should().Contain("--dir-dat-mirror").And.Contain("retropie");
    }

    [Fact]
    public async Task Mirror_WithFrontendNone_ShouldBeAccepted_WhateverTheConfiguration()
    {
        WriteConfig(s => s.Frontend = FrontendTarget.Batocera);

        var result = await RunAsync("run", "--frontend", "none", "--dir-dat-mirror");

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
    }

    [Fact]
    public async Task Summary_ShouldSayTheFrontendAndItsNotes_EvenQuiet()
    {
        var result = await RunAsync("run", "--frontend", "mister", "--quiet");

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        var lines = result.Output.Split('\n', StringSplitOptions.TrimEntries);
        var start = Array.IndexOf(lines, "Frontend: MiSTer");
        start.Should().BeGreaterThan(0, result.Output);
        var notes = lines.Skip(start + 1).TakeWhile(l => l.StartsWith('[')).ToList();
        notes.Should().HaveCount(2);
        notes.Should().ContainSingle(n => n.StartsWith("[INFO]") && n.Contains("GAMEBOY")
            && n.Contains("Nintendo - Game Boy, Nintendo - Game Boy (Private)"));
        notes.Should().ContainSingle(n => n.StartsWith("[WARN]") && n.Contains("Homebrew - Things"));
    }

    [Fact]
    public async Task Patches_ShouldAcceptTheFrontend()
    {
        var patches = _temp.CreateSubdirectory("patches");

        var result = await RunAsync("patches", "--patches", patches, "--frontend", "es-de");

        result.Error.Should().NotContain("--frontend");
        result.ExitCode.Should().NotBe(ExitCodes.InvalidArguments);
    }
}
