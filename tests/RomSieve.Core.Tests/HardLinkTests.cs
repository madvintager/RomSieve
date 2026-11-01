using System.IO;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Hard link mode: the target must be a real hard link of the source, including beyond the Windows MAX_PATH limit
/// that the native <c>CreateHardLinkW</c> call does not lift by itself.
/// </summary>
public sealed class HardLinkTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");

    public void Dispose() => _temp.Dispose();

    private const string RomName = "Game (1990)(Pub)(FR).adf";

    private async Task<(string Source, string Target, SortExecutionResult Result)> SortAsHardLinkAsync(string sourceDir, string destinationDir)
    {
        var source = Path.Combine(sourceDir, RomName);
        File.WriteAllText(source, "data");

        var result = await _services.Processor.ExecuteAsync(
            new SortConfig { SourceDir = sourceDir, DestinationDir = destinationDir, TransferMode = TransferMode.HardLink, Pattern = "{title}/" },
            isDryRun: false);

        var target = result.Items.Single().FinalTargetPath!;
        return (source, target, result);
    }

    private static void ShouldBeTheSameFile(string source, string target)
    {
        // A write through one name is visible through the other only for a hard link
        File.AppendAllText(target, "+changed");
        File.ReadAllText(source).Should().Be("data+changed");
    }

    [Fact]
    public async Task HardLink_ShouldLinkTheTargetToTheSource()
    {
        var (source, target, result) = await SortAsHardLinkAsync(_temp.CreateSubdirectory("src"), _temp.CreateSubdirectory("dst"));

        result.Items.Single().Status.Should().Be(FileStatus.Valid);
        ShouldBeTheSameFile(source, target);
    }

    [Fact]
    public async Task HardLink_ShouldWorkWhenTheSourcePathExceedsMaxPath()
    {
        var sourceDir = _temp.CreateSubdirectory(Path.Combine("src", new string('a', 120), new string('b', 120)));
        Path.Combine(sourceDir, RomName).Length.Should().BeGreaterThan(260);

        var (source, target, result) = await SortAsHardLinkAsync(sourceDir, _temp.CreateSubdirectory("dst"));

        result.Items.Single().Status.Should().Be(FileStatus.Valid, result.Items.Single().ErrorMessage);
        ShouldBeTheSameFile(source, target);
    }

    [Fact]
    public async Task HardLink_ShouldWorkWhenTheTargetPathExceedsMaxPath()
    {
        var destinationDir = _temp.CreateSubdirectory(Path.Combine("dst", new string('c', 120), new string('d', 120)));

        var (source, target, result) = await SortAsHardLinkAsync(_temp.CreateSubdirectory("src"), destinationDir);

        target.Length.Should().BeGreaterThan(260);
        result.Items.Single().Status.Should().Be(FileStatus.Valid, result.Items.Single().ErrorMessage);
        ShouldBeTheSameFile(source, target);
    }

    [Fact]
    public void ExtendedPath_ShouldUseTheUncFormForANetworkPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var organizer = new PathOrganizerService(new TosecParser());
        var longName = new string('x', 250);

        organizer.FormatWindowsExtendedPath($@"\\nas\roms\{longName}.adf").Should().Be($@"\\?\UNC\nas\roms\{longName}.adf");
        organizer.FormatWindowsExtendedPath($@"C:\roms\{longName}.adf").Should().Be($@"\\?\C:\roms\{longName}.adf");
        organizer.FormatWindowsExtendedPath(@"C:\roms\short.adf").Should().Be(@"C:\roms\short.adf");
    }
}
