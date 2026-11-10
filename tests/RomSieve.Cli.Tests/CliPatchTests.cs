using System.Text.Json;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using static RomSieve.Core.Tests.Fixtures.PatchTestEncoder;

namespace RomSieve.Cli.Tests;

/// <summary>
/// The patches on the command line, from end to end: <c>--patches</c> gives the patch folder and <c>--patch-only</c>
/// writes the patched versions only, with the guards of the interface (a destination that overlaps the patch folder is
/// refused, code 3), and the summary counts the patched versions and the patches not applied. Every run uses a
/// configuration of a temporary directory.
/// </summary>
public sealed class CliPatchTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly string _configPath;
    private readonly string _source;
    private readonly string _destination;
    private readonly string _patches;

    private static readonly byte[] Japan = Data(1, 4096);
    private static readonly byte[] Usa = Data(2, 4096);
    private static readonly byte[] Translated = [.. Japan, .. Ascii("English")];
    private static readonly byte[] UsaHack = [.. Usa, .. Ascii("Hack")];

    public CliPatchTests()
    {
        _configPath = Path.Combine(_temp.CreateSubdirectory("config"), "config.json");
        _source = _temp.CreateSubdirectory("source");
        _destination = Path.Combine(_temp.Path, "destination");
        _patches = _temp.CreateSubdirectory("patches");
        _temp.WriteBytes("source/Game (Japan).sfc", Japan);
        _temp.WriteBytes("source/Game (USA).sfc", Usa);
        _temp.WriteBytes("patches/Game (Japan) (T-En).bps", BpsLinear(Japan, Translated));
        _temp.WriteBytes("patches/Game (USA) (Hack).ups", Ups(Usa, UsaHack));
        _temp.WriteBytes("patches/Orphan (T-En).bps", BpsLinear(Data(9, 64), Data(10, 64)));
        var dat = _temp.WriteText("dat/snes.dat", $"""
            <?xml version="1.0"?><datafile><header><name>Nintendo - SNES</name><homepage>No-Intro</homepage></header>
            <game name="Game (Japan)"><description>Game (Japan)</description><rom name="Game (Japan).sfc" size="{Japan.Length}" crc="{Crc(Japan):x8}"/></game>
            <game name="Game (USA)"><description>Game (USA)</description><rom name="Game (USA).sfc" size="{Usa.Length}" crc="{Crc(Usa):x8}"/></game></datafile>
            """);
        var settings = new SortSettings
        {
            SourceDir = _source,
            DestinationDir = _destination,
            DatFile = dat,
            NamingConvention = NamingConvention.NoIntro,
            FlatDestination = true,
            OneGameOneRom = true,
            PriorityLanguages = ["en", "ja"]
        };
        var data = new Dictionary<string, object?>(StringComparer.Ordinal) { ["ui_locale"] = "en" };
        SortSettingsSerializer.Write(settings, data);
        File.WriteAllText(_configPath, JsonSerializer.Serialize(data));
    }

    public void Dispose() => _temp.Dispose();

    private static byte[] Data(int seed, int size)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
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
                .Select(f => Path.GetRelativePath(_destination, f))
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];

    [Fact]
    public async Task Run_WithPatches_ShouldWriteThePatchedVersionsAndCountThem()
    {
        var result = await RunAsync("run", "--patches", _patches, "--execute");

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        DestinationFiles().Should().Equal("Game (Japan) [T-En].sfc", "Game (USA) [Hack].sfc", "Game (USA).sfc");
        File.ReadAllBytes(Path.Combine(_destination, "Game (Japan) [T-En].sfc")).Should().Equal(Translated);
        result.Output.Should().Contain("Patches: 2 patched versions, 1 patches not applied.");
        result.Output.Should().Contain("Orphan (T-En).bps", "each patch not applied has its line");
    }

    [Fact]
    public async Task Run_WithPatchOnly_ShouldLeaveOutTheOriginalRomsTheyReplace()
    {
        var result = await RunAsync("run", "--patches", _patches, "--patch-only", "--execute");

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        DestinationFiles().Should().Equal("Game (Japan) [T-En].sfc", "Game (USA) [Hack].sfc");
    }

    [Fact]
    public async Task Run_WithoutExecute_ShouldWriteNothing()
    {
        var result = await RunAsync("run", "--patches", _patches);

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        DestinationFiles().Should().BeEmpty();
        result.Output.Should().Contain("Patches: 2 patched versions");
    }

    [Fact]
    public async Task Run_IntoThePatchFolder_ShouldBeRefused()
    {
        var result = await RunAsync("run", "--patches", _patches, "--destination", Path.Combine(_patches, "sorted"), "--execute");

        result.ExitCode.Should().Be(ExitCodes.InvalidConfiguration);
        result.Error.Should().Contain("inside the folder of the patches");
        Directory.Exists(Path.Combine(_patches, "sorted")).Should().BeFalse();
    }

    [Fact]
    public async Task Help_ShouldDescribeThePatchOptions()
    {
        var result = await RunAsync("run", "--help");

        result.Output.Should().Contain("--patches").And.Contain("--patch-only");
    }
}
