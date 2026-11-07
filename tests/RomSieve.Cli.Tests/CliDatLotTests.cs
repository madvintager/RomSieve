using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Cli.Tests;

/// <summary>
/// A lot of DATs on the command line: <c>--dat</c> takes a folder of DATs (once), <c>--dat-recursive</c> its subfolders,
/// <c>--dat-only</c> the DATs to sort with, <c>--dir-dat-mirror</c> the folders of the DATs in the destination; a folder
/// the sort would refuse is refused first; the summary tells what was found of each DAT; the <c>dat</c> command writes a
/// folder. Every run uses a configuration of a temporary directory.
/// </summary>
public sealed class CliDatLotTests : IDisposable
{
    private const string GameBoy = "Nintendo - Game Boy";
    private const string GameBoyColor = "Nintendo - Game Boy Color";
    private const string Amiga = "Commodore Amiga - Games - [ADF]";

    private readonly TempDirectoryFixture _temp = new();
    private readonly string _configPath;
    private readonly string _source;
    private readonly string _destination;
    private readonly string _dats;

    public CliDatLotTests()
    {
        _configPath = Path.Combine(_temp.CreateSubdirectory("config"), "config.json");
        _source = _temp.CreateSubdirectory("source");
        _destination = Path.Combine(_temp.Path, "destination");
        _dats = _temp.CreateSubdirectory("dats");
        WriteDat("Nintendo - Game Boy (20230414).dat", GameBoy, "No-Intro", ("Tetris (World)", "Tetris (World).gb", "tetris gb"), ("Alleyway (World)", "Alleyway (World).gb", "alleyway gb"));
        WriteDat("Nintendo - Game Boy Color (20230501).dat", GameBoyColor, "No-Intro", ("Tetris DX (World)", "Tetris DX (World).gbc", "tetris dx"));
        WriteDat(Path.Combine("TOSEC", "Commodore", "Amiga - Games (TOSEC-v2025).dat"), Amiga, "TOSEC", ("Lemmings (1991)(Psygnosis)", "Lemmings (1991)(Psygnosis).adf", "lemmings"));
        File.WriteAllText(Path.Combine(_source, "tetris.gb"), "tetris gb");
        File.WriteAllText(Path.Combine(_source, "lemmings.adf"), "lemmings");
        WriteConfig();
    }

    public void Dispose() => _temp.Dispose();

    private static string Crc(string text) => Crc32.HashToUInt32(Encoding.UTF8.GetBytes(text)).ToString("x8");

    private void WriteDat(string relativePath, string name, string homepage, params (string Game, string Rom, string Data)[] games) =>
        _temp.WriteText(Path.Combine("dats", relativePath), $"""
            <?xml version="1.0"?>
            <datafile>
              <header><name>{name}</name><description>{name}</description><homepage>{homepage}</homepage></header>
            {string.Concat(games.Select(g => $"""  <game name="{g.Game}"><description>{g.Game}</description><rom name="{g.Rom}" size="{Encoding.UTF8.GetByteCount(g.Data)}" crc="{Crc(g.Data)}"/></game>"""))}
            </datafile>
            """);

    private void WriteConfig(Action<SortSettings>? configure = null)
    {
        var settings = new SortSettings { SourceDir = _source, DestinationDir = _destination, Pattern = "{title}" };
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
    public async Task Run_WithALot_ShouldSummarizeEachDatWithAGameFound()
    {
        var result = await RunAsync("run", "--dat", _dats);

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        result.Output.Should().Contain($"  {GameBoy}: 1 games found, 1/2 ROMs (50.0 %)");
        result.Output.Should().Contain("  1 other DATs of the lot without any game found.");
        result.Output.Should().NotContain(Amiga, "the DATs of the subfolders are only read with --dat-recursive");
        DestinationFiles().Should().BeEmpty("a simulation writes nothing");
    }

    [Fact]
    public async Task Run_WithTheSubfoldersOfTheLot_ShouldSortWithTheirDats()
    {
        var result = await RunAsync("run", "--dat", _dats, "--dat-recursive", "--execute");

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        DestinationFiles().Should().Equal(
            $"{Amiga}/Lemmings/Lemmings (1991)(Psygnosis).adf",
            $"{GameBoy}/Tetris/Tetris (World).gb");
    }

    [Fact]
    public async Task Run_WithTheMirror_ShouldPutTheFolderOfEachDatUnderItsPath()
    {
        var result = await RunAsync("run", "--dat", _dats, "--dat-recursive", "--dir-dat-mirror", "--execute");

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        DestinationFiles().Should().Contain($"TOSEC/Commodore/{Amiga}/Lemmings/Lemmings (1991)(Psygnosis).adf");
    }

    [Fact]
    public async Task Mirror_WithoutAFolderPerDat_ShouldBeRefused()
    {
        WriteConfig(s => s.FolderPerDat = false);

        var result = await RunAsync("run", "--dat", _dats, "--dir-dat-mirror");

        result.ExitCode.Should().Be(ExitCodes.InvalidArguments);
        result.Error.Should().Contain("--dir-dat-mirror");
    }

    [Fact]
    public async Task DatOnly_ShouldSortWithTheDatsItNamesAlone()
    {
        WriteConfig(s => s.ExcludedDats = [GameBoy]);

        var result = await RunAsync("run", "--dat", _dats, "--dat-recursive", "--dat-only", "commodore amiga - games - [adf]", "--execute");

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        DestinationFiles().Should().Equal($"{Amiga}/Lemmings/Lemmings (1991)(Psygnosis).adf", "_unmatched/tetris.gb");
    }

    [Fact]
    public async Task DatOnly_NamingNoDatOfTheLot_ShouldBeRefusedWithTheNamesOfTheLot()
    {
        var result = await RunAsync("run", "--dat", _dats, "--dat-only", "Sega - Mega Drive");

        result.ExitCode.Should().Be(ExitCodes.InvalidArguments);
        result.Error.Should().Contain("Sega - Mega Drive").And.Contain(GameBoy).And.Contain(GameBoyColor);
    }

    [Fact]
    public async Task DatOnly_WithoutALot_ShouldBeRefused()
    {
        var result = await RunAsync("run", "--dat", Path.Combine(_dats, "Nintendo - Game Boy (20230414).dat"), "--dat-only", GameBoy);

        result.ExitCode.Should().Be(ExitCodes.InvalidArguments);
        result.Error.Should().Contain("--dat-only");
    }

    [Fact]
    public async Task Dat_GivenTwice_ShouldBeRefused()
    {
        var result = await RunAsync("run", "--dat", _dats, "--dat", Path.Combine(_dats, "TOSEC"));

        result.ExitCode.Should().Be(ExitCodes.InvalidArguments);
        DestinationFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task FolderTheSortWouldRefuse_ShouldBeRefusedBeforeAnythingIsDone()
    {
        WriteDat("copy.dat", GameBoy, "No-Intro", ("Tetris (World)", "Tetris (World).gb", "tetris gb"));

        var result = await RunAsync("run", "--dat", _dats, "--execute");

        result.ExitCode.Should().Be(ExitCodes.InvalidConfiguration);
        result.Error.Should().Contain("copy.dat").And.Contain(GameBoy);
        Directory.Exists(_destination).Should().BeFalse();
    }

    [Fact]
    public async Task FixDat_OfALot_ShouldBeAFolder_AndSayWhatItLeavesOut()
    {
        var fixdats = Path.Combine(_temp.Path, "fixdats");

        var result = await RunAsync("run", "--dat", _dats, "--fixdat", fixdats);

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        Directory.EnumerateFiles(fixdats).Select(Path.GetFileName).Should().Equal($"fixDat_{GameBoy}.xml");
        result.Output.Should().Contain("0 complete DATs and 1 DATs without any game found have nothing to export");
    }

    [Fact]
    public async Task CatalogDat_OfALot_ShouldBeWrittenIntoAFolder()
    {
        var file = await RunAsync("dat", "--dat", _dats, "--output", Path.Combine(_temp.Path, "lot.xml"));
        file.ExitCode.Should().Be(ExitCodes.InvalidArguments);
        file.Error.Should().Contain("a DAT per DAT of the lot");

        var folder = Path.Combine(_temp.Path, "lot");
        var result = await RunAsync("dat", "--dat", _dats, "--dat-recursive", "--output", folder);

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        Directory.EnumerateFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).Should().Equal(
            $"{Amiga} (RomSieve).xml", $"{GameBoy} (RomSieve).xml", $"{GameBoyColor} (RomSieve).xml");
        result.Output.Should().Contain("4");
    }

    [Fact]
    public async Task CatalogDat_WithDatOnly_ShouldOnlyWriteTheDatsNamed()
    {
        var folder = Path.Combine(_temp.Path, "only");

        var result = await RunAsync("dat", "--dat", _dats, "--dat-only", GameBoyColor, "--output", folder);

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        Directory.EnumerateFiles(folder).Select(Path.GetFileName).Should().Equal($"{GameBoyColor} (RomSieve).xml");
    }
}
