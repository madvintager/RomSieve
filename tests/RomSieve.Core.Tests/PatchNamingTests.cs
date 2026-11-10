using Microsoft.Extensions.DependencyInjection;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using static RomSieve.Core.Tests.Fixtures.PatchTestEncoder;

namespace RomSieve.Core.Tests;

/// <summary>
/// The CRC32 of its origin written in the name of an IPS (<see cref="PatchNamingService"/>): the new name, each refusal
/// before anything changes, a renaming that goes on after a refusal, the log, and the patch applied at the next
/// simulation under the same patched name.
/// </summary>
public sealed class PatchNamingTests : IDisposable
{
    private const uint SomeCrc = 0x47e70e08;

    private readonly CoreTestServices _services = new();
    private readonly TempDirectoryFixture _temp = new();
    private readonly PatchNamingService _naming;
    private readonly string _patches;
    private readonly List<(LogLevel Level, LocalizedMessage Message)> _log = [];

    private static readonly byte[] Japan = Data(1, 4096);

    public PatchNamingTests()
    {
        _naming = new PatchNamingService(_services.Patches, _services.ChecksumService, _services.CrcCache);
        _patches = _temp.CreateSubdirectory("patches");
    }

    public void Dispose() => _temp.Dispose();

    private static byte[] Data(int seed, int size)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static byte[] Translated => [.. Japan, .. Ascii("English")];

    private string WriteIps(string name) => _temp.WriteBytes(Path.Combine("patches", name), IpsDiff(Japan, Translated));

    private async Task<PatchRenameResult> RenameAsync(PatchFile patch, uint crc = SomeCrc) =>
        (await _naming.RenameAsync(_patches, [new PatchRename(patch, crc)], (level, message) => _log.Add((level, message)))).Single();

    private static string RefusalKey(PatchRenameResult result)
    {
        result.IsRenamed.Should().BeFalse();
        return result.Refusal!.Key;
    }

    // =========================================================================
    // The new name
    // =========================================================================

    [Theory]
    [InlineData("Game (T-Fr).ips", "Game (T-Fr) [47e70e08].ips")]
    [InlineData("Game v1.0.ips", "Game v1.0 [47e70e08].ips")]
    [InlineData("Game [!] .ips", "Game [!] [47e70e08].ips")]
    [InlineData("Game.v2.IPS32", "Game.v2 [47e70e08].IPS32")]
    [InlineData("sub/Game.ips", "Game [47e70e08].ips")]
    public void NameWithCrc_ShouldWriteTheCrcBeforeTheExtension(string name, string expected)
    {
        _naming.NameWithCrc(name, SomeCrc).Should().Be(expected);
        PatchMatcher.NameGivesCrc(expected).Should().BeTrue("the CRC32 is written the way the matching reads it");
    }

    // =========================================================================
    // Renaming
    // =========================================================================

    [Fact]
    public async Task ARenamedIps_ShouldApplyAtTheNextSimulation_UnderTheSamePatchedName()
    {
        _temp.WriteBytes("src/Game (Japan).sfc", Japan);
        var dat = _temp.WriteText("dat/snes.dat", $"""
            <?xml version="1.0"?><datafile><header><name>Nintendo - SNES</name><homepage>No-Intro</homepage></header>
            <game name="Game (Japan)"><description>Game (Japan)</description><rom name="Game (Japan).sfc" size="{Japan.Length}" crc="{Crc(Japan):x8}"/></game></datafile>
            """);
        var path = WriteIps("Game (Japan) (T-En).ips");
        var config = new SortConfig
        {
            SourceDir = _temp.GetFilePath("src"),
            DestinationDir = _temp.CreateSubdirectory("dst"),
            DatFile = dat,
            NamingConvention = NamingConvention.NoIntro,
            PatchDir = _patches
        };
        var before = await _services.Processor.ExecuteAsync(config, isDryRun: true);
        before.UnappliedPatches.Should().ContainSingle().Which.Reason.Key.Should().Be("reason.patch_no_crc");

        var result = await RenameAsync(new PatchFile(path), Crc(Japan));

        result.IsRenamed.Should().BeTrue();
        result.NewPath.Should().Be(Path.Combine(_patches, $"Game (Japan) (T-En) [{Crc(Japan):x8}].ips"));
        File.Exists(path).Should().BeFalse();
        File.ReadAllBytes(result.NewPath!).Should().Equal(IpsDiff(Japan, Translated), "only the name changes");
        _log.Should().ContainSingle().Which.Should().Match<(LogLevel Level, LocalizedMessage Message)>(
            l => l.Level == LogLevel.Info && l.Message.Key == "log.patch_renamed");

        var after = await _services.Processor.ExecuteAsync(config, isDryRun: true);
        after.UnappliedPatches.Should().BeEmpty();
        after.Items.Single(it => it.Patch is not null).TargetFilename.Should().Be("Game (Japan) [T-En].sfc");
    }

    [Fact]
    public async Task APatchInAnArchive_ShouldNotBeRenamed()
    {
        var zip = _temp.WriteBytes("patches/patches.zip", [0x50, 0x4b, 0x05, 0x06, .. new byte[18]]);

        var result = await RenameAsync(new PatchFile(zip, "Game.ips"));

        RefusalKey(result).Should().Be("reason.patch_rename_in_archive");
        File.Exists(zip).Should().BeTrue();
    }

    [Fact]
    public async Task APatchOutsideThePatchFolder_ShouldNotBeRenamed()
    {
        var path = _temp.WriteBytes("elsewhere/Game.ips", IpsDiff(Japan, Translated));

        var result = await RenameAsync(new PatchFile(path));

        RefusalKey(result).Should().Be("reason.patch_rename_outside");
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task AMissingPatch_ShouldNotBeRenamed()
    {
        RefusalKey(await RenameAsync(new PatchFile(Path.Combine(_patches, "Gone.ips")))).Should().Be("reason.patch_rename_missing");
    }

    [Fact]
    public async Task ALink_ShouldNotBeRenamed()
    {
        var real = WriteIps("Real.ips");
        var link = Path.Combine(_patches, "Link.ips");
        try
        {
            File.CreateSymbolicLink(link, real);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows without the right to create links (developer mode off)
            return;
        }

        RefusalKey(await RenameAsync(new PatchFile(link))).Should().Be("reason.patch_rename_link");
        File.Exists(link).Should().BeTrue();
    }

    [Fact]
    public async Task ANameThatGivesACrc_ShouldNotBeRenamed()
    {
        var path = WriteIps("Game [12345678].ips");

        RefusalKey(await RenameAsync(new PatchFile(path))).Should().Be("reason.patch_rename_has_crc");
        File.Exists(path).Should().BeTrue();
    }

    [Theory]
    [InlineData("Game.ups")]
    [InlineData("Game.ips")]
    public async Task AFileThatIsNoLongerAnIps_ShouldNotBeRenamed(string name)
    {
        var path = name.EndsWith(".ups", StringComparison.Ordinal)
            ? _temp.WriteBytes(Path.Combine("patches", name), Ups(Japan, Translated))
            : _temp.WriteText(Path.Combine("patches", name), "Not a patch");

        RefusalKey(await RenameAsync(new PatchFile(path))).Should().Be("reason.patch_rename_not_ips");
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task ANameAlreadyTaken_ShouldNotBeOverwritten()
    {
        var path = WriteIps("Game.ips");
        var taken = _temp.WriteText("patches/Game [47e70e08].ips", "another file");

        var result = await RenameAsync(new PatchFile(path));

        RefusalKey(result).Should().Be("reason.patch_rename_taken");
        result.Refusal!.Args["name"].Should().Be("Game [47e70e08].ips");
        File.Exists(path).Should().BeTrue();
        File.ReadAllText(taken).Should().Be("another file");
    }

    [Fact]
    public async Task ARefusal_ShouldLeaveTheOtherRenamingsToBeDone()
    {
        var first = WriteIps("First.ips");
        var renames = new List<PatchRename>
        {
            new(new PatchFile(Path.Combine(_patches, "Gone.ips")), SomeCrc),
            new(new PatchFile(first), SomeCrc)
        };

        var results = await _naming.RenameAsync(_patches, renames, (level, message) => _log.Add((level, message)));

        results.Select(r => r.IsRenamed).Should().Equal(false, true);
        File.Exists(Path.Combine(_patches, "First [47e70e08].ips")).Should().BeTrue();
        _log.Select(l => (l.Level, l.Message.Key)).Should().Equal(
            (LogLevel.Warn, "log.patch_rename_refused"),
            (LogLevel.Info, "log.patch_renamed"));
        _log[0].Message.Args["reason"].Should().BeOfType<LocalizedMessage>().Which.Key.Should().Be("reason.patch_rename_missing");
    }

    // =========================================================================
    // Forms of an origin
    // =========================================================================

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheFormsOfAnOrigin_ShouldBeBothForAFileWithACopierHeader_TheWholeOneFirst(bool detectCopierHeaders)
    {
        var data = Data(3, 8192);
        var file = (byte[])[.. Ascii("NES\u001a"), .. new byte[12], .. data];
        _temp.WriteBytes("src/Game (USA).nes", file);
        _temp.WriteBytes("src/Plain (USA).nes", Japan);
        var dat = _temp.WriteText("dat/nes.dat", $"""
            <?xml version="1.0"?><datafile><header><name>Nintendo - NES</name><homepage>No-Intro</homepage></header>
            <game name="Game (USA)"><description>Game (USA)</description><rom name="Game (USA).nes" size="{data.Length}" crc="{Crc(data):x8}"/></game>
            <game name="Plain (USA)"><description>Plain (USA)</description><rom name="Plain (USA).nes" size="{Japan.Length}" crc="{Crc(Japan):x8}"/></game></datafile>
            """);
        var result = await _services.Processor.ExecuteAsync(
            new SortConfig { SourceDir = _temp.GetFilePath("src"), DestinationDir = _temp.CreateSubdirectory("dst"), DatFile = dat, NamingConvention = NamingConvention.NoIntro },
            isDryRun: true);
        var game = result.Items.Single(it => it.DisplayName == "Game (USA).nes");
        var plain = result.Items.Single(it => it.DisplayName == "Plain (USA).nes");

        var forms = await _naming.GetOriginFormsAsync(game, detectCopierHeaders);
        var plainForms = await _naming.GetOriginFormsAsync(plain, detectCopierHeaders);

        if (detectCopierHeaders)
        {
            forms.Select(f => (f.Crc32, f.Offset, f.WithCopierHeader)).Should().Equal(
                (Crc(file), 0L, (bool?)true),
                (Crc(data), 16L, (bool?)false));
        }
        else
        {
            forms.Select(f => (f.Crc32, f.Offset, f.WithCopierHeader)).Should().Equal((Crc(data), 16L, (bool?)null));
        }

        plainForms.Select(f => (f.Crc32, f.Offset, f.WithCopierHeader)).Should().Equal((Crc(Japan), 0L, (bool?)null));
        forms.Should().OnlyContain(f => ReferenceEquals(f.Origin, game));
    }

    [Fact]
    public void TheService_ShouldBeRegistered()
    {
        using var provider = new ServiceCollection().AddRomSieveCore().BuildServiceProvider();

        provider.GetRequiredService<IPatchNamingService>().Should().BeOfType<PatchNamingService>();
    }
}
