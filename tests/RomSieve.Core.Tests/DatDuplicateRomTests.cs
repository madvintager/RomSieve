using System.IO;
using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// Tests for DAT catalogs in which the same ROM (same size and CRC) belongs to several games.
/// </summary>
public sealed class DatDuplicateRomTests : IDisposable
{
    private const string Alpha = "Alpha (1990)(Ocean)";
    private const string AlphaAlt = "Alpha (1990)(Ocean)[a]";

    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");

    public void Dispose() => _temp.Dispose();

    private static string Dat(params string[] games) => $"""
        <?xml version="1.0"?>
        <datafile>
        	<header>
        		<name>Commodore Amiga - Games - [ADF] (TOSEC)</name>
        		<description>TOSEC test DAT</description>
        	</header>
        {string.Join("\n", games)}
        </datafile>
        """;

    private static string Game(string name, string romName, long size, string crc, string? cloneOf = null) =>
        $"""
        	<game name="{name}"{(cloneOf is null ? string.Empty : $" cloneof=\"{cloneOf}\"")}>
        		<description>{name}</description>
        		<rom name="{romName}" size="{size}" crc="{crc}"/>
        	</game>
        """;

    private async Task<(string Src, string Dst, string DatPath, long Size, string Crc)> CreateEnvAsync(
        string sourceFileName,
        Func<long, string, string> datFactory)
    {
        var src = _temp.CreateSubdirectory("src");
        var dst = _temp.CreateSubdirectory("dst");
        var file = _temp.WriteText(Path.Combine("src", sourceFileName), "ALPHA-CONTENT");
        var size = new FileInfo(file).Length;
        var crc = await _services.ChecksumService.ComputeFileCrc32Async(file);
        var datPath = _temp.WriteText("test.dat", datFactory(size, crc));
        return (src, dst, datPath, size, crc);
    }

    private Task<SortExecutionResult> RunAsync(string src, string dst, string datPath) =>
        _services.Processor.ExecuteAsync(new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            DatFile = datPath,
            Pattern = string.Empty
        }, isDryRun: true);

    [Fact]
    public async Task SameRomInTwoGames_ShouldMarkBothEntriesAsPresent()
    {
        var (src, dst, datPath, _, _) = await CreateEnvAsync($"{Alpha}.adf", (size, crc) => Dat(
            Game(Alpha, $"{Alpha}.adf", size, crc),
            Game(AlphaAlt, $"{AlphaAlt}.adf", size, crc)));

        var result = await RunAsync(src, dst, datPath);

        result.Stats.DatTotalRoms.Should().Be(2);
        result.Stats.DatMatchedCount.Should().Be(2);
        result.Stats.DatMissingCount.Should().Be(0);
        result.Stats.DatCompletionPct.Should().Be(100.0);
        result.MissingRoms.Should().BeEmpty();
    }

    [Fact]
    public async Task IdenticalRomEntryInTwoGames_ShouldCountBothEntries()
    {
        var (src, dst, datPath, _, _) = await CreateEnvAsync("bios.rom", (size, crc) => Dat(
            Game(Alpha, "bios.rom", size, crc),
            Game("Beta (1991)(Ocean)", "bios.rom", size, crc)));

        var result = await RunAsync(src, dst, datPath);

        result.Stats.DatMatchedCount.Should().Be(2);
        result.Stats.DatCompletionPct.Should().Be(100.0);
        result.MissingRoms.Should().BeEmpty();
    }

    [Fact]
    public async Task Identification_ShouldPreferTheEntryNamedLikeTheSourceFile()
    {
        var (src, dst, datPath, _, _) = await CreateEnvAsync($"{Alpha}.adf", (size, crc) => Dat(
            Game(AlphaAlt, $"{AlphaAlt}.adf", size, crc),
            Game(Alpha, $"{Alpha}.adf", size, crc)));

        var result = await RunAsync(src, dst, datPath);

        result.Items.Should().ContainSingle().Which.TargetFilename.Should().Be($"{Alpha}.adf");
    }

    [Fact]
    public void FindMatch_ShouldApplyNameThenParentThenDatOrderRule()
    {
        var catalog = _services.DatService.ParseLogiqxXml(Dat(
            Game("Clone Game (1990)(Ocean)", "clone.adf", 100, "0badf00d", cloneOf: "Parent Game (1990)(Ocean)"),
            Game("Parent Game (1990)(Ocean)", "parent.adf", 100, "0badf00d"),
            Game("Other Parent (1990)(Ocean)", "other.adf", 100, "0badf00d")));

        catalog.FindAllMatches(100, "0BADF00D").Select(e => e.Name)
            .Should().Equal("clone.adf", "parent.adf", "other.adf");

        catalog.FindMatch(100, "0badf00d", preferredName: "OTHER.ADF")!.Name.Should().Be("other.adf");
        catalog.FindMatch(100, "0badf00d", preferredName: "unknown.adf")!.Name.Should().Be("parent.adf");
        catalog.FindMatch(100, "0badf00d")!.Name.Should().Be("parent.adf");
    }

    [Fact]
    public void FindMatch_WithoutParentCloneRelation_ShouldFollowDatOrder()
    {
        var catalog = _services.DatService.ParseLogiqxXml(Dat(
            Game("First (1990)(Ocean)", "first.adf", 100, "0badf00d"),
            Game("Second (1990)(Ocean)", "second.adf", 100, "0badf00d")));

        catalog.FindMatch(100, "0badf00d")!.Name.Should().Be("first.adf");
    }

    [Fact]
    public void GetGameForRom_SharedRomName_ShouldHonorPreferredGameThenParentRule()
    {
        var catalog = _services.DatService.ParseLogiqxXml(Dat(
            Game("Clone Game (1990)(Ocean)", "bios.rom", 100, "0badf00d", cloneOf: "Parent Game (1990)(Ocean)"),
            Game("Parent Game (1990)(Ocean)", "bios.rom", 100, "0badf00d")));

        catalog.GetGamesForRom("BIOS.ROM").Select(g => g.Name)
            .Should().Equal("Clone Game (1990)(Ocean)", "Parent Game (1990)(Ocean)");
        catalog.GetGameForRom("bios.rom")!.Name.Should().Be("Parent Game (1990)(Ocean)");
        catalog.GetGameForRom("bios.rom", preferredGameName: "Clone Game (1990)(Ocean)")!.Name
            .Should().Be("Clone Game (1990)(Ocean)");
    }
}
