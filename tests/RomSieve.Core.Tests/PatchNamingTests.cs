using Microsoft.Extensions.DependencyInjection;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using static RomSieve.Core.Tests.Fixtures.PatchTestEncoder;

namespace RomSieve.Core.Tests;

/// <summary>
/// The CRC32 of its origin written in the name of an IPS, replaced or removed, for other tools
/// (<see cref="PatchNamingService"/>): the new name, each refusal before anything changes, a renaming that goes on after
/// a refusal, the log, the undoing, the patch applied at the next simulation under the same patched name, and a patch
/// whose name no longer gives its ROM proposed again.
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
        _naming = new PatchNamingService(_services.Patches);
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
    [InlineData("Game [12345678].ips", "Game [47e70e08].ips")]
    [InlineData("Game (CRC32 12345678) (T-Fr).ips", "Game (T-Fr) [47e70e08].ips")]
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
    public async Task ANameThatGivesACrc_ShouldGetTheOneChosenInstead()
    {
        var path = WriteIps("Game (T-Fr) [12345678].ips");

        var result = await RenameAsync(new PatchFile(path));

        result.NewPath.Should().Be(Path.Combine(_patches, "Game (T-Fr) [47e70e08].ips"));
        File.Exists(path).Should().BeFalse();
        _log.Should().ContainSingle().Which.Message.Key.Should().Be("log.patch_renamed");
    }

    [Theory]
    [InlineData("Game [47e70e08].ips")]
    [InlineData("Game [47E70E08].ips")]
    public async Task TheCrcTheNameAlreadyGives_ShouldNotBeWrittenAgain(string name)
    {
        var path = WriteIps(name);

        RefusalKey(await RenameAsync(new PatchFile(path))).Should().Be("reason.patch_rename_unchanged");
        File.Exists(path).Should().BeTrue();
    }

    // =========================================================================
    // Removing the ROM from the name of a patch
    // =========================================================================

    [Theory]
    [InlineData("Game [47e70e08].ips", "Game.ips")]
    [InlineData("Game (T-Fr) [47E70E08].ips", "Game (T-Fr).ips")]
    [InlineData("Game [!] [47e70e08] (Hack).ips", "Game [!] (Hack).ips")]
    [InlineData("Game (CRC32 47e70e08).ips", "Game.ips")]
    [InlineData("Game (CRC: 47e70e08).IPS32", "Game.IPS32")]
    [InlineData("Game - 0x47e70e08.ips", "Game.ips")]
    [InlineData("Game_47e70e08.ips", "Game.ips")]
    [InlineData("sub/Game [47e70e08].ips", "Game.ips")]
    [InlineData("Game (T-Fr).ips", "Game (T-Fr).ips")]
    public void NameWithoutCrc_ShouldRemoveTheCrcAndWhatItLeaves(string name, string expected)
    {
        var result = _naming.NameWithoutCrc(name);

        result.Should().Be(expected);
        PatchMatcher.NameGivesCrc(result!).Should().BeFalse();
    }

    [Theory]
    [InlineData("47e70e08.ips")]
    [InlineData("[47e70e08] - .ips")]
    public void NameWithoutCrc_ShouldBeNull_WhenOnlyACrcMadeTheName(string name) =>
        _naming.NameWithoutCrc(name).Should().BeNull();

    [Fact]
    public async Task AnIpsWhoseNameNoLongerGivesItsRom_ShouldBeProposedAgain_WithoutAChoice()
    {
        _temp.WriteBytes("src/Game (Japan).sfc", Japan);
        var dat = _temp.WriteText("dat/snes.dat", $"""
            <?xml version="1.0"?><datafile><header><name>Nintendo - SNES</name><homepage>No-Intro</homepage></header>
            <game name="Game (Japan)"><description>Game (Japan)</description><rom name="Game (Japan).sfc" size="{Japan.Length}" crc="{Crc(Japan):x8}"/></game></datafile>
            """);
        var path = WriteIps($"Game (Japan) (T-En) [{Crc(Japan):x8}].ips");
        var config = new SortConfig
        {
            SourceDir = _temp.GetFilePath("src"),
            DestinationDir = _temp.CreateSubdirectory("dst"),
            DatFile = dat,
            NamingConvention = NamingConvention.NoIntro,
            PatchDir = _patches
        };
        var before = await _services.Processor.ExecuteAsync(config, isDryRun: true);
        before.Items.Single(it => it.Patch is not null).Patch!.CrcSource.Should().Be(PatchCrcSource.FileName);

        var result = (await _naming.RenameAsync(_patches, [new PatchRename(new PatchFile(path), null)], (level, message) => _log.Add((level, message)))).Single();

        result.NewPath.Should().Be(Path.Combine(_patches, "Game (Japan) (T-En).ips"));
        File.ReadAllBytes(result.NewPath!).Should().Equal(IpsDiff(Japan, Translated), "only the name changes");
        _log.Should().ContainSingle().Which.Should().Match<(LogLevel Level, LocalizedMessage Message)>(
            l => l.Level == LogLevel.Info && l.Message.Key == "log.patch_name_cleared");

        var after = await _services.Processor.ExecuteAsync(config, isDryRun: true);
        after.Items.Should().NotContain(it => it.Patch != null);
        var unapplied = after.UnappliedPatches.Should().ContainSingle().Subject;
        unapplied.Reason.Key.Should().Be("reason.patch_no_crc");
        unapplied.Suggestions.Should().ContainSingle().Which.Origin.DisplayName.Should().Be("Game (Japan).sfc");
    }

    [Fact]
    public async Task APatchWhoseNameGivesNoRom_ShouldNotHaveItRemoved()
    {
        var path = WriteIps("Game (T-Fr).ips");

        var result = (await _naming.RenameAsync(_patches, [new PatchRename(new PatchFile(path), null)], (_, _) => { })).Single();

        RefusalKey(result).Should().Be("reason.patch_rename_no_rom");
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task APatchNamedOnlyByItsCrc_ShouldKeepItsName()
    {
        var path = WriteIps("47e70e08.ips");

        var result = (await _naming.RenameAsync(_patches, [new PatchRename(new PatchFile(path), null)], (_, _) => { })).Single();

        RefusalKey(result).Should().Be("reason.patch_rename_nothing_left");
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task ANameWithoutRom_AlreadyTaken_ShouldNotBeOverwritten()
    {
        var path = WriteIps("Game [47e70e08].ips");
        var taken = _temp.WriteText("patches/Game.ips", "another file");

        var result = (await _naming.RenameAsync(_patches, [new PatchRename(new PatchFile(path), null)], (_, _) => { })).Single();

        RefusalKey(result).Should().Be("reason.patch_rename_taken");
        File.Exists(path).Should().BeTrue();
        File.ReadAllText(taken).Should().Be("another file");
    }

    [Fact]
    public async Task APatchInAnArchive_ShouldNotHaveItsRomRemoved()
    {
        var zip = _temp.WriteBytes("patches/patches.zip", [0x50, 0x4b, 0x05, 0x06, .. new byte[18]]);

        var result = (await _naming.RenameAsync(_patches, [new PatchRename(new PatchFile(zip, "Game [47e70e08].ips"), null)], (_, _) => { })).Single();

        RefusalKey(result).Should().Be("reason.patch_rename_in_archive");
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
    // Undoing a renaming
    // =========================================================================

    private Task<PatchUndoResult> UndoAsync(PatchRenameResult renaming) =>
        _naming.UndoAsync(_patches, renaming, (level, message) => _log.Add((level, message)));

    [Fact]
    public async Task ARenaming_ShouldBeUndone_UnderTheOldName()
    {
        var path = WriteIps("Game (T-Fr).ips");
        var renaming = await RenameAsync(new PatchFile(path));

        var undo = await UndoAsync(renaming);

        undo.IsUndone.Should().BeTrue();
        File.Exists(path).Should().BeTrue();
        File.Exists(renaming.NewPath).Should().BeFalse();
        File.ReadAllBytes(path).Should().Equal(IpsDiff(Japan, Translated), "only the name changes");
        _log.Select(l => (l.Level, l.Message.Key)).Should().Equal(
            (LogLevel.Info, "log.patch_renamed"),
            (LogLevel.Info, "log.patch_rename_undone"));
        _log[1].Message.Args["new"].Should().Be("Game (T-Fr) [47e70e08].ips");
        _log[1].Message.Args["old"].Should().Be("Game (T-Fr).ips");
    }

    [Fact]
    public async Task RemovingTheRom_ShouldBeUndone_SoThatTheNameDesignatesItAgain()
    {
        var path = WriteIps("Game (CRC32 47e70e08).ips");
        var clearing = (await _naming.RenameAsync(_patches, [new PatchRename(new PatchFile(path), null)], (_, _) => { })).Single();
        clearing.NewPath.Should().Be(Path.Combine(_patches, "Game.ips"));

        var undo = await UndoAsync(clearing);

        undo.IsUndone.Should().BeTrue();
        File.Exists(path).Should().BeTrue("the old name comes back as it was, not rebuilt");
        File.Exists(clearing.NewPath).Should().BeFalse();
    }

    [Fact]
    public async Task AnUndo_ShouldBeRefused_WhenThePatchIsNoLongerUnderItsNewName()
    {
        var path = WriteIps("Game.ips");
        var renaming = await RenameAsync(new PatchFile(path));
        File.Move(renaming.NewPath!, Path.Combine(_patches, "Moved.ips"));

        var undo = await UndoAsync(renaming);

        undo.IsUndone.Should().BeFalse();
        undo.Refusal!.Key.Should().Be("reason.patch_rename_missing");
        File.Exists(path).Should().BeFalse();
        _log.Last().Should().Match<(LogLevel Level, LocalizedMessage Message)>(
            l => l.Level == LogLevel.Warn && l.Message.Key == "log.patch_undo_refused");
    }

    [Fact]
    public async Task AnUndo_ShouldNotOverwriteAFileThatTookTheOldName()
    {
        var path = WriteIps("Game.ips");
        var renaming = await RenameAsync(new PatchFile(path));
        File.WriteAllText(path, "another file");

        var undo = await UndoAsync(renaming);

        undo.Refusal!.Key.Should().Be("reason.patch_rename_taken");
        File.ReadAllText(path).Should().Be("another file");
        File.Exists(renaming.NewPath).Should().BeTrue();
    }

    [Fact]
    public async Task AnUndo_ShouldBeRefused_WhenTheFileIsNoLongerAnIps()
    {
        var path = WriteIps("Game.ips");
        var renaming = await RenameAsync(new PatchFile(path));
        File.WriteAllText(renaming.NewPath!, "Not a patch");

        (await UndoAsync(renaming)).Refusal!.Key.Should().Be("reason.patch_rename_not_ips");
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task AnUndo_ShouldBeRefused_OutsideThePatchFolder()
    {
        var path = WriteIps("Game.ips");
        var renaming = await RenameAsync(new PatchFile(path));

        var undo = await _naming.UndoAsync(_temp.CreateSubdirectory("other"), renaming, (_, _) => { });

        undo.Refusal!.Key.Should().Be("reason.patch_rename_outside");
        File.Exists(renaming.NewPath).Should().BeTrue();
    }

    [Fact]
    public async Task AnUndo_ShouldBeRefused_ForALink()
    {
        var path = WriteIps("Game.ips");
        var renaming = await RenameAsync(new PatchFile(path));
        var real = WriteIps("Real.ips");
        File.Delete(renaming.NewPath!);
        try
        {
            File.CreateSymbolicLink(renaming.NewPath!, real);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows without the right to create links (developer mode off)
            return;
        }

        (await UndoAsync(renaming)).Refusal!.Key.Should().Be("reason.patch_rename_link");
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task AnUndo_OfARefusal_ShouldBeAnError()
    {
        var refused = await RenameAsync(new PatchFile(Path.Combine(_patches, "Gone.ips")));

        var undo = () => UndoAsync(refused);

        await undo.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void TheService_ShouldBeRegistered()
    {
        using var provider = new ServiceCollection().AddRomSieveCore().BuildServiceProvider();

        provider.GetRequiredService<IPatchNamingService>().Should().BeOfType<PatchNamingService>();
    }
}
