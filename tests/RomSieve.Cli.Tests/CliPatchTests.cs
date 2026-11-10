using System.Text.Json;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using static RomSieve.Core.Tests.Fixtures.PatchTestEncoder;

namespace RomSieve.Cli.Tests;

/// <summary>
/// The patches on the command line, from end to end: <c>--patches</c> gives the patch folder and <c>--patch-only</c>
/// writes the patched versions only, with the guards of the interface (a destination that overlaps the patch folder is
/// refused, code 3, as is a pattern of the patched versions that would leave the destination), and the summary counts the
/// patched versions and the patches not applied. Every run uses a configuration of a temporary directory.
/// </summary>
public sealed class CliPatchTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly string _configPath;
    private readonly string _source;
    private readonly string _destination;
    private readonly string _patches;
    private readonly string _dat;

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
        _dat = _temp.WriteText("dat/snes.dat", $"""
            <?xml version="1.0"?><datafile><header><name>Nintendo - SNES</name><homepage>No-Intro</homepage></header>
            <game name="Game (Japan)"><description>Game (Japan)</description><rom name="Game (Japan).sfc" size="{Japan.Length}" crc="{Crc(Japan):x8}"/></game>
            <game name="Game (USA)"><description>Game (USA)</description><rom name="Game (USA).sfc" size="{Usa.Length}" crc="{Crc(Usa):x8}"/></game></datafile>
            """);
        WriteConfig();
    }

    /// <summary>The configuration of the runs (or a profile, at <paramref name="path"/>), changed by <paramref name="configure"/>.</summary>
    private void WriteConfig(Action<SortSettings>? configure = null, string? path = null)
    {
        var settings = new SortSettings
        {
            SourceDir = _source,
            DestinationDir = _destination,
            DatFile = _dat,
            NamingConvention = NamingConvention.NoIntro,
            FlatDestination = true,
            OneGameOneRom = true,
            PriorityLanguages = ["en", "ja"]
        };
        configure?.Invoke(settings);
        var data = new Dictionary<string, object?>(StringComparer.Ordinal) { ["ui_locale"] = "en" };
        SortSettingsSerializer.Write(settings, data);
        var target = path ?? _configPath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, JsonSerializer.Serialize(data));
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
        result.Output.Should().Contain("Patches: 2 patched versions, 1 patch not applied.");
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
        (await RunAsync("--help")).Output.Should().Contain("patches");
        (await RunAsync("patches", "--help")).Output.Should().Contain("--name-from-notes").And.Contain("--link").And.Contain("--rom")
            .And.Contain("--ignore").And.Contain("--auto").And.Contain("--yes").And.NotContain("--detach");
    }

    // =========================================================================
    // The pattern of the patched versions, read from the configuration or the profile
    // =========================================================================

    [Fact]
    public async Task Run_WithAPatchedPattern_ShouldWriteThePatchedVersionsInTheirFolder_WithTheSameSummary()
    {
        WriteConfig(s => s.PatchedPattern = "Patchs/{title}");

        var result = await RunAsync("run", "--patches", _patches, "--execute");

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        DestinationFiles().Should().Equal(
            "Game (USA).sfc",
            Path.Combine("Patchs", "Game", "Game (Japan) [T-En].sfc"),
            Path.Combine("Patchs", "Game", "Game (USA) [Hack].sfc"));
        File.ReadAllBytes(Path.Combine(_destination, "Patchs", "Game", "Game (Japan) [T-En].sfc")).Should().Equal(Translated);
        result.Output.Should().Contain("Patches: 2 patched versions, 1 patch not applied.");
    }

    [Fact]
    public async Task Run_WithAPatchedPatternInTheProfile_ShouldWriteThePatchedVersionsInTheirFolder()
    {
        var profilePath = ConfigurationProfiles.GetPath(_configPath, "Translations");
        WriteConfig(s =>
        {
            s.PatchDir = _patches;
            s.PatchedPattern = "{origin_folder}/Translations";
            s.FlatDestination = false;
            s.Pattern = "{title}";
        }, path: profilePath);

        var result = await RunAsync("run", "--profile", "Translations", "--execute");

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        DestinationFiles().Should().Equal(
            Path.Combine("Game", "Game (USA).sfc"),
            Path.Combine("Game", "Translations", "Game (Japan) [T-En].sfc"),
            Path.Combine("Game", "Translations", "Game (USA) [Hack].sfc"));
    }

    [Theory]
    [InlineData("run")]
    [InlineData("patches")]
    public async Task APatchedPatternThatLeavesTheDestination_ShouldBeRefused_AndNothingWritten(string command)
    {
        WriteConfig(s => s.PatchedPattern = Path.Combine("..", "escape", "{title}"));

        var result = await RunAsync(command == "run" ? ["run", "--patches", _patches, "--execute"] : ["patches", "--patches", _patches]);

        result.ExitCode.Should().Be(ExitCodes.InvalidConfiguration);
        result.Error.Should().Contain("The folder of the patched versions must stay inside the destination");
        Directory.Exists(_destination).Should().BeFalse();
        Directory.Exists(Path.Combine(_temp.Path, "escape")).Should().BeFalse();
    }

    [Fact]
    public async Task APatchedPatternIntoTheQuarantine_ShouldBeRefused_InTheLanguageOfTheMessages()
    {
        WriteConfig(s => s.PatchedPattern = "_purged/{title}");

        var result = await RunAsync("run", "--patches", _patches, "--execute", "--lang", "fr");

        result.ExitCode.Should().Be(ExitCodes.InvalidConfiguration);
        result.Error.Should().Contain("Le dossier des versions patchées doit rester dans la destination");
        Directory.Exists(_destination).Should().BeFalse();
    }

    [Fact]
    public async Task APatchedPatternThatLeavesTheDestination_ShouldNotStopASortWithoutPatchFolder()
    {
        // The pattern is greyed out without a folder of the patches: nothing patched is written
        WriteConfig(s => s.PatchedPattern = Path.Combine("..", "escape"));

        var result = await RunAsync("run", "--execute");

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        DestinationFiles().Should().Equal("Game (USA).sfc");
    }

    // =========================================================================
    // Patches turned off
    // =========================================================================

    [Fact]
    public async Task Run_WithThePatchesOffInTheConfiguration_ShouldApplyNone_UnlessTheFolderIsGiven()
    {
        WriteConfig(s =>
        {
            s.PatchDir = _patches;
            s.ApplyPatches = false;
            s.PatchOnly = true;
            s.PatchedPattern = Path.Combine("..", "escape");
        });

        var off = await RunAsync("run", "--execute");

        off.ExitCode.Should().Be(ExitCodes.Success, off.Error);
        DestinationFiles().Should().Equal("Game (USA).sfc");
        off.Output.Should().Contain("Applying the patches is turned off").And.NotContain("Patches:");

        // Giving the folder asks for the patches, and the pattern is checked again
        var given = await RunAsync("run", "--patches", _patches);
        given.ExitCode.Should().Be(ExitCodes.InvalidConfiguration);
        given.Error.Should().Contain("The folder of the patched versions must stay inside the destination");
    }

    [Fact]
    public async Task Run_WithNoPatches_ShouldApplyNone_AndKeepTheConfiguration()
    {
        WriteConfig(s => s.PatchDir = _patches);
        var before = File.ReadAllText(_configPath);

        var result = await RunAsync("run", "--no-patches", "--execute");

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        DestinationFiles().Should().Equal("Game (USA).sfc");
        File.ReadAllText(_configPath).Should().Be(before, "the command line never writes the configuration");
    }

    [Fact]
    public async Task Run_WithNoPatchesAndPatches_ShouldBeRefused()
    {
        var result = await RunAsync("run", "--no-patches", "--patches", _patches, "--execute");

        result.ExitCode.Should().Be(ExitCodes.InvalidArguments);
        result.Error.Should().Contain("--no-patches and --patches exclude each other");
        Directory.Exists(_destination).Should().BeFalse();
        (await RunAsync("run", "--help")).Output.Should().Contain("--no-patches");
    }

    [Fact]
    public async Task Patches_ShouldListThePatches_EvenWhenTheConfigurationTurnsThemOff()
    {
        WriteConfig(s =>
        {
            s.PatchDir = _patches;
            s.ApplyPatches = false;
        });

        var result = await RunAsync("patches");

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        result.Output.Should().Contain("Orphan (T-En).bps", "the command is about the patches");
        DestinationFiles().Should().BeEmpty();
    }

    // =========================================================================
    // The patches command
    // =========================================================================

    /// <summary>An IPS applied thanks to its readme, in a folder of its own.</summary>
    private string WriteIpsWithReadme()
    {
        _temp.WriteText("patches/hack/readme.txt", $"File/ROM CRC32: {Crc(Usa):X8}");
        return _temp.WriteBytes("patches/hack/Hack.ips", IpsDiff(Usa, UsaHack));
    }

    /// <summary>The choices of the user, next to the configuration of the runs.</summary>
    private string LinksPath => PatchLinkService.PathNextTo(_configPath);

    [Fact]
    public async Task Patches_ShouldListEveryPatch_WithTheRomsProposed_AndWriteNothing()
    {
        var ips = _temp.WriteBytes("patches/Game (Japan) (T-Fr).ips", IpsDiff(Japan, Translated));
        WriteIpsWithReadme();

        var result = await RunAsync("patches", "--patches", _patches);

        result.ExitCode.Should().Be(ExitCodes.Success, result.Error);
        result.Output.Should().Contain("3 patched versions, 2 patches not applied, 0 patches set aside.");
        result.Output.Should().Contain("Not applied, Game (Japan) (T-Fr).ips: Neither the name of the IPS patch");
        result.Output.Should().Contain($"ROM proposed: Game (Japan).sfc, CRC32 {Crc(Japan):X8}");
        result.Output.Should().Contain("A ROM proposed is never taken on its own: choose it with --link <patch> --rom <ROM> --yes");
        result.Output.Should().Contain($"Applied, {Path.Combine("hack", "Hack.ips")}: to Game (USA).sfc (thanks to readme.txt)");
        result.Output.Should().Contain("Applied, Game (Japan) (T-En).bps: to Game (Japan).sfc (the patch names its ROM)");
        result.Output.Should().NotContain("[PLANNED]", "the lines of the simulation are not shown");
        DestinationFiles().Should().BeEmpty();
        File.Exists(ips).Should().BeTrue("a ROM proposed is never taken");
        File.Exists(LinksPath).Should().BeFalse("listing records no choice");
    }

    [Fact]
    public async Task Patches_NameFromNotes_ShouldShowTheNewNames_AndRenameOnlyWithYes()
    {
        var ips = WriteIpsWithReadme();
        var renamed = Path.Combine(_patches, "hack", $"Hack [{Crc(Usa):x8}].ips");

        var shown = await RunAsync("patches", "--patches", _patches, "--name-from-notes");

        shown.ExitCode.Should().Be(ExitCodes.ConfirmationRequired);
        shown.Output.Should().Contain($"To rename: {Path.Combine("hack", "Hack.ips")} -> Hack [{Crc(Usa):x8}].ips");
        shown.Error.Should().Contain("Add --yes to rename these patches.");
        File.Exists(ips).Should().BeTrue();

        var done = await RunAsync("patches", "--patches", _patches, "--name-from-notes", "--yes");

        done.ExitCode.Should().Be(ExitCodes.Success, done.Error);
        done.Output.Should().Contain("Patch renamed: Hack.ips becomes").And.Contain("1 patch renamed, 0 refused.");
        File.Exists(ips).Should().BeFalse();
        File.Exists(renamed).Should().BeTrue();

        // The CRC32 in its name now applies it, without its readme
        var again = await RunAsync("patches", "--patches", _patches, "--name-from-notes");
        again.ExitCode.Should().Be(ExitCodes.Success, again.Error);
        again.Output.Should().Contain($"Applied, {Path.Combine("hack", $"Hack [{Crc(Usa):x8}].ips")}: to Game (USA).sfc (from its name)");
        again.Output.Should().Contain("No IPS applied thanks to its readme is to be named.");
    }

    [Fact]
    public async Task Patches_Link_ShouldShowTheChoice_AndSaveItOnlyWithYes_WithoutRenamingThePatch()
    {
        var ips = _temp.WriteBytes("patches/Game (Japan) (T-Fr).ips", IpsDiff(Japan, Translated));

        var shown = await RunAsync("patches", "--patches", _patches, "--link", "game (japan) (t-fr).ips", "--rom", "Game (Japan).sfc");

        shown.ExitCode.Should().Be(ExitCodes.ConfirmationRequired);
        shown.Output.Should().Contain("Choice for Game (Japan) (T-Fr).ips: applied to Game (Japan).sfc");
        shown.Error.Should().Contain("Add --yes to save these choices. No file is changed");
        File.Exists(LinksPath).Should().BeFalse();

        var done = await RunAsync("patches", "--patches", _patches, "--link", "Game (Japan) (T-Fr).ips", "--rom", "Game (Japan).sfc", "--yes");

        done.ExitCode.Should().Be(ExitCodes.Success, done.Error);
        done.Output.Should().Contain("1 choice saved: the next simulation follows it.");
        File.Exists(LinksPath).Should().BeTrue("the choice is next to the configuration");
        File.Exists(ips).Should().BeTrue("the patch keeps its name");

        var listed = await RunAsync("patches", "--patches", _patches);
        listed.Output.Should().Contain("Applied, Game (Japan) (T-Fr).ips: to Game (Japan).sfc (your choice)");

        var sorted = await RunAsync("run", "--patches", _patches, "--execute");
        sorted.ExitCode.Should().Be(ExitCodes.Success, sorted.Error);
        DestinationFiles().Should().Contain("Game (Japan) [T-Fr].sfc");
    }

    [Fact]
    public async Task Patches_Ignore_ShouldSetAnyPatchAside_AndAutoShouldTakeItBack()
    {
        WriteIpsWithReadme();

        var ignored = await RunAsync("patches", "--patches", _patches, "--ignore", "Game (USA) (Hack).ups", "--ignore", "hack/Hack.ips", "--yes");

        ignored.ExitCode.Should().Be(ExitCodes.Success, ignored.Error);
        ignored.Output.Should().Contain("Choice for Game (USA) (Hack).ups: set aside").And.Contain("2 choices saved");
        var listed = await RunAsync("patches", "--patches", _patches);
        listed.Output.Should().Contain("1 patched version, 1 patch not applied, 2 patches set aside.");
        listed.Output.Should().Contain("Set aside, Game (USA) (Hack).ups").And.Contain($"Set aside, {Path.Combine("hack", "Hack.ips")}");
        var sorted = await RunAsync("run", "--patches", _patches);
        sorted.Output.Should().Contain("Patch set aside at your request: Game (USA) (Hack).ups");

        var auto = await RunAsync("patches", "--patches", _patches, "--auto", "Game (USA) (Hack).ups", "--yes");

        auto.ExitCode.Should().Be(ExitCodes.Success, auto.Error);
        auto.Output.Should().Contain("Choice for Game (USA) (Hack).ups: automatic choice");
        (await RunAsync("patches", "--patches", _patches)).Output.Should().Contain("Applied, Game (USA) (Hack).ups: to Game (USA).sfc");

        var again = await RunAsync("patches", "--patches", _patches, "--auto", "Game (USA) (Hack).ups", "--yes");
        again.ExitCode.Should().Be(ExitCodes.InvalidArguments);
        again.Error.Should().Contain("--auto Game (USA) (Hack).ups: no choice is saved for this patch.");
    }

    [Fact]
    public async Task Patches_LinkToARomWithACopierHeader_ShouldAskForTheCrcOfItsForm()
    {
        var data = Data(4, 8192);
        var file = (byte[])[.. Ascii("NES\u001a"), .. new byte[12], .. data];
        _temp.WriteBytes("source/Hero (USA).nes", file);
        _temp.WriteText("dat/snes.dat", File.ReadAllText(_dat).Replace(
            "</datafile>",
            $"""<game name="Hero (USA)"><description>Hero (USA)</description><rom name="Hero (USA).nes" size="{data.Length}" crc="{Crc(data):x8}"/></game></datafile>"""));
        _temp.WriteBytes("patches/Hero (Hack).ips", IpsDiff(data, [.. data, .. Ascii("Hack")]));

        var byName = await RunAsync("patches", "--patches", _patches, "--link", "Hero (Hack).ips", "--rom", "Hero (USA).nes", "--yes");

        byName.ExitCode.Should().Be(ExitCodes.InvalidArguments);
        byName.Error.Should().Contain("this ROM has two forms").And.Contain($"{Crc(file):X8}").And.Contain($"{Crc(data):X8}");
        File.Exists(LinksPath).Should().BeFalse("nothing is saved after a refusal");

        var byCrc = await RunAsync("patches", "--patches", _patches, "--link", "Hero (Hack).ips", "--rom", $"{Crc(data):x8}", "--yes");

        byCrc.ExitCode.Should().Be(ExitCodes.Success, byCrc.Error);
        (await RunAsync("patches", "--patches", _patches)).Output.Should().Contain("Applied, Hero (Hack).ips: to Hero (USA).nes (your choice)");
    }

    [Theory]
    [InlineData("--link|Game (Japan) (T-Fr).ips", "--link and --rom go together")]
    [InlineData("--rom|Game (Japan).sfc", "--link and --rom go together")]
    [InlineData("--link|Game (USA) (Hack).ups|--rom|Game (USA).sfc", "a UPS or a BPS names its own ROM")]
    [InlineData("--link|Game (Japan) (T-Fr).ips|--rom|Game (Europe).sfc", "--rom Game (Europe).sfc: no ROM identified")]
    [InlineData("--ignore|Unknown.ips", "--ignore Unknown.ips: no patch of the simulation has this name. Patches:")]
    [InlineData("--ignore|Orphan (T-En).bps|--ignore|orphan (t-en).bps", "Orphan (T-En).bps: one choice per patch.")]
    [InlineData("--ignore|Orphan (T-En).bps|--name-from-notes", "--name-from-notes does not go with --link, --ignore or --auto")]
    public async Task Patches_AChoiceThatCannotBe_ShouldBeRefused_AndNothingSaved(string options, string message)
    {
        _temp.WriteBytes("patches/Game (Japan) (T-Fr).ips", IpsDiff(Japan, Translated));

        var result = await RunAsync(["patches", "--patches", _patches, .. options.Split('|'), "--yes"]);

        result.ExitCode.Should().Be(ExitCodes.InvalidArguments);
        result.Error.Should().Contain(message);
        File.Exists(LinksPath).Should().BeFalse();
    }

    [Fact]
    public async Task Patches_WithoutPatchFolder_ShouldBeRefused()
    {
        var result = await RunAsync("patches");

        result.ExitCode.Should().Be(ExitCodes.InvalidConfiguration);
        result.Error.Should().Contain("No folder of the patches");
    }

    [Fact]
    public async Task Patches_IntoThePatchFolder_ShouldBeRefusedLikeASort()
    {
        var result = await RunAsync("patches", "--patches", _patches, "--destination", Path.Combine(_patches, "sorted"));

        result.ExitCode.Should().Be(ExitCodes.InvalidConfiguration);
        result.Error.Should().Contain("inside the folder of the patches");
    }
}
