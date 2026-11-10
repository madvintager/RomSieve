using System.Text.Json;
using System.Xml.Linq;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using static RomSieve.Core.Tests.Fixtures.PatchTestEncoder;

namespace RomSieve.Core.Tests;

/// <summary>
/// The patches in the configuration and around the sort: the keys <c>patch_dir</c> and <c>patch_only</c>, the patch
/// folder that the destination must not overlap (a link resolved, the purge refused too), the counters of the
/// statistics, the reports that name the patch of each patched version, and the DAT of the collection that describes the
/// patched versions with their own checksums.
/// </summary>
public sealed class PatchConfigurationTests : IDisposable
{
    private readonly CoreTestServices _services = new();
    private readonly TempDirectoryFixture _temp = new();

    private static readonly byte[] Japan = Data(1, 4096);
    private static readonly byte[] Translated = [.. Japan, .. Ascii("English")];

    public void Dispose() => _temp.Dispose();

    private static byte[] Data(int seed, int size)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static Dictionary<string, object?> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
    }

    // =========================================================================
    // Configuration
    // =========================================================================

    [Fact]
    public void TheOptions_ShouldHaveTheirDefaults_AndBeWrittenAndReadBack()
    {
        var defaults = SortSettingsSerializer.Read(new Dictionary<string, object?>());
        defaults.PatchDir.Should().BeEmpty();
        defaults.PatchOnly.Should().BeFalse();
        SortSettingsSerializer.ToSortConfig(defaults).PatchDir.Should().BeNull("no patch folder is read without one");

        var data = new Dictionary<string, object?>(StringComparer.Ordinal);
        SortSettingsSerializer.Write(new SortSettings { PatchDir = " C:/patches ", PatchOnly = true }, data);
        data["patch_dir"].Should().Be("C:/patches");
        data["patch_only"].Should().Be(true);

        var config = SortSettingsSerializer.ToSortConfig(SortSettingsSerializer.Read(Parse(JsonSerializer.Serialize(data))));
        config.PatchDir.Should().Be("C:/patches");
        config.PatchOnly.Should().BeTrue();
    }

    [Fact]
    public void AConfigurationOfAnEarlierVersion_ShouldGetTheDefaults()
    {
        var read = SortSettingsSerializer.Read(Parse("""{ "config_version": 1, "chd_dir": "C:/chd" }"""));

        read.PatchDir.Should().BeEmpty("a key a configuration does not have takes its default, without migration");
        read.PatchOnly.Should().BeFalse();
    }

    // =========================================================================
    // Guards
    // =========================================================================

    private static string Root(params string[] parts) => Path.Combine([Path.GetTempPath(), "romsieve_patch_overlap", .. parts]);

    [Theory]
    [InlineData("patches", "dialog.error_same_patch_dest")]
    [InlineData("", "dialog.error_dest_contains_patches")]
    [InlineData("patches/sorted", "dialog.error_dest_inside_patches")]
    [InlineData("out", null)]
    public void TheDestination_ShouldNotOverlapThePatchFolder(string destination, string? expected)
    {
        // The patch folder is read recursively: a destination inside it is refused even without a recursive scan
        var config = new SortConfig
        {
            SourceDir = Path.Combine(Path.GetTempPath(), "romsieve_patch_source"),
            DestinationDir = Root(destination.Split('/', StringSplitOptions.RemoveEmptyEntries)),
            Recursive = false,
            PatchDir = Root("patches")
        };

        SortConfigValidator.GetErrorKey(config, isMameRun: false).Should().Be(expected);
        SortConfigValidator.GetErrorKey(config, isMameRun: true).Should().Be(expected, "the patch folder is checked whatever the run");
    }

    [Fact]
    public void ADestinationReachingThePatchFolderThroughALink_ShouldBeRefused()
    {
        var patches = _temp.CreateSubdirectory("patches");
        var alias = Path.Combine(_temp.Path, "alias");
        try
        {
            Directory.CreateSymbolicLink(alias, patches);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Links need a privilege the test does not have: nothing to check here
            return;
        }

        SortConfigValidator.GetPatchDirErrorKey(patches, alias).Should().Be("dialog.error_same_patch_dest");
        SortConfigValidator.GetPatchDirErrorKey(patches, Path.Combine(alias, "sorted")).Should().Be("dialog.error_dest_inside_patches");
    }

    [Fact]
    public async Task ASortIntoThePatchFolder_ShouldBeRefusedBeforeAnything()
    {
        var src = _temp.CreateSubdirectory("src");
        _temp.WriteBytes("src/Game (Japan).sfc", Japan);
        var destination = _temp.CreateSubdirectory("out");
        var patches = _temp.CreateSubdirectory("out/patches");
        var patch = _temp.WriteBytes("out/patches/Game (Japan) (T-En).bps", BpsLinear(Japan, Translated));
        var config = new SortConfig { SourceDir = src, DestinationDir = destination, PatchDir = patches, PurgeDestination = true };

        var act = () => _services.Processor.ExecuteAsync(config, isDryRun: false);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be(_services.I18n.Tr("dialog.error_dest_contains_patches"));
        File.Exists(patch).Should().BeTrue();
    }

    [Fact]
    public async Task APurge_ShouldNeverPutThePatchFolderInQuarantine()
    {
        var destination = _temp.CreateSubdirectory("dst");
        var patch = _temp.WriteBytes("dst/patches/Game (T-En).bps", BpsLinear(Japan, Translated));
        var config = new SortConfig
        {
            SourceDir = _temp.CreateSubdirectory("src"),
            DestinationDir = destination,
            PatchDir = Path.GetDirectoryName(patch),
            PurgeDestination = true
        };
        var executor = new FileTransferExecutor(
            config, isDryRun: false, _services.Organizer, _services.FilterService, _services.I18n,
            _services.OpticalCompanionService, _services.ChecksumService, _services.Patches);

        var (success, error) = await executor.PrepareDestinationAsync();

        success.Should().BeFalse("the purge checks the patch folder itself, whoever calls it");
        error.Should().Be(_services.I18n.Tr("dialog.error_dest_contains_patches"));
        File.Exists(patch).Should().BeTrue();
    }

    // =========================================================================
    // Statistics and exports
    // =========================================================================

    /// <summary>A simulation of a Japanese ROM with a patch, an orphan patch beside it.</summary>
    private async Task<SortExecutionResult> SimulateAsync(SortConfig? config = null)
    {
        _temp.WriteBytes("src/Game (Japan).sfc", Japan);
        var dat = _temp.WriteText("dat/snes.dat", $"""
            <?xml version="1.0"?><datafile><header><name>Nintendo - SNES</name><homepage>No-Intro</homepage></header>
            <game name="Game (Japan)"><description>Game (Japan)</description><rom name="Game (Japan).sfc" size="{Japan.Length}" crc="{Crc(Japan):x8}"/></game></datafile>
            """);
        _temp.WriteBytes("patches/Game (Japan) (T-En).bps", BpsLinear(Japan, Translated));
        _temp.WriteBytes("patches/Other (T-En).ups", Ups(Data(9, 64), Data(10, 64)));
        config ??= new SortConfig { SourceDir = _temp.GetFilePath("src"), DestinationDir = _temp.CreateSubdirectory("dst") };
        config.DatFile = dat;
        config.NamingConvention = NamingConvention.NoIntro;
        config.PatchDir = _temp.GetFilePath("patches");
        return await _services.Processor.ExecuteAsync(config, isDryRun: true);
    }

    [Fact]
    public async Task TheStatistics_ShouldCountThePatchedVersionsAndThePatchesNotApplied()
    {
        var result = await SimulateAsync();

        result.Stats.PatchedCount.Should().Be(1);
        result.Stats.ValidCount.Should().Be(2, "a patched version is a file the sort writes, next to its origin");
        result.Stats.UnappliedPatches.Should().ContainSingle().Which.File.DisplayName.Should().Be("Other (T-En).ups");

        var recomputed = _services.Processor.RecomputeStats(result.Items, result.Stats);
        recomputed.PatchedCount.Should().Be(1);
        recomputed.UnappliedPatches.Should().BeSameAs(result.Stats.UnappliedPatches);
    }

    [Fact]
    public async Task TheReports_ShouldNameThePatchOfEachPatchedVersion()
    {
        var result = await SimulateAsync();
        var json = _temp.GetFilePath("report.json");
        var csv = _temp.GetFilePath("report.csv");

        await _services.Processor.ExportReportToJsonAsync(result.Items, result.Stats, json);
        await _services.Processor.ExportReportToCsvAsync(result.Items, csv);

        using var document = JsonDocument.Parse(File.ReadAllText(json));
        var files = document.RootElement.GetProperty("files").EnumerateArray().ToList();
        var patched = files.Single(f => f.TryGetProperty("patch", out _));
        patched.GetProperty("patch").GetString().Should().EndWith("Game (Japan) (T-En).bps");
        patched.GetProperty("patch_format").GetString().Should().Be("BPS");
        patched.GetProperty("patched_from").GetString().Should().EndWith("Game (Japan).sfc");
        files.Count(f => f.TryGetProperty("patch", out _)).Should().Be(1, "only a patched version has these fields");

        var lines = File.ReadAllLines(csv);
        lines[0].Should().EndWith(";patch;patch_format;patched_from");
        lines.Should().Contain(l => l.Contains("Game (Japan) (T-En).bps", StringComparison.Ordinal) && l.Contains(";BPS;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheDatOfTheCollection_ShouldDescribeAPatchedVersionWithItsOwnChecksums()
    {
        var result = await SimulateAsync();
        var output = _temp.GetFilePath("collection.xml");

        await _services.Processor.ExportDatAsync(result.Items, output, DatExportFormat.LogiqxXml, result.DatCatalog);

        var roms = XDocument.Load(output).Descendants("rom").ToList();
        var patched = roms.Single(r => (string?)r.Attribute("name") == "Game (Japan) [T-En].sfc");
        ((string?)patched.Attribute("crc")).Should().Be(Crc(Translated).ToString("x8"));
        ((string?)patched.Attribute("size")).Should().Be(Translated.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ((string?)patched.Attribute("sha1")).Should().Be(Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(Translated)),
            "the result of the patch, applied again after a simulation, never the patch itself");
        ((string?)patched.Parent!.Attribute("name")).Should().Be("Game (Japan) [T-En]");
        Directory.EnumerateDirectories(Path.GetTempPath(), "romsieve_patch_*").Should().BeEmpty("the temporary files are deleted");
    }
}
