using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using static RomSieve.Core.Tests.Fixtures.PatchTestEncoder;

namespace RomSieve.Core.Tests;

/// <summary>
/// The ROMs the readme of an IPS names (<see cref="PatchMatcher.CitedNames"/>), from extracts of real readmes
/// (<c>Fixtures/Patches</c>) put next to synthetic patches, against synthetic ROMs named after the DAT entries they cite:
/// proposed when no CRC32 designates a ROM of the source, before the ROMs its own name proposes, never applied; the ROM
/// of the DAT a readme names, missing from the source, named in the reason (with its DAT in a lot). A GoodTools name or a
/// close one names nothing; a UPS or a BPS never reads its readme; a readme among several patches comes with none.
/// </summary>
public sealed class PatchReadmeNameTests : IDisposable
{
    private readonly CoreTestServices _services = new();
    private readonly TempDirectoryFixture _temp = new();
    private readonly string _src;
    private readonly string _dst;
    private readonly string _patches;

    private static readonly byte[] Smb = Data(1, 4096);
    private static readonly byte[] Smb2 = Data(2, 4096);
    private static readonly byte[] Zelda2 = Data(3, 4096);
    private static readonly byte[] MegaMan2 = Data(4, 4096);
    private static readonly byte[] Ff3 = Data(5, 4096);

    /// <summary>The DAT of the tests: real names, synthetic data.</summary>
    private static readonly (string Game, byte[] Data)[] Games =
    [
        ("Super Mario Bros. (World)", Smb),
        ("Super Mario Bros. 2 (USA)", Smb2),
        ("Zelda II - The Adventure of Link (USA)", Zelda2),
        ("Mega Man 2 (USA)", MegaMan2),
        ("Final Fantasy III (Japan)", Ff3)
    ];

    public PatchReadmeNameTests()
    {
        _src = _temp.CreateSubdirectory("src");
        _dst = _temp.CreateSubdirectory("dst");
        _patches = _temp.CreateSubdirectory("patches");
    }

    public void Dispose() => _temp.Dispose();

    private static byte[] Data(int seed, int size)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static byte[] Hack(byte[] data)
    {
        var hacked = (byte[])data.Clone();
        hacked[10] ^= 0x55;
        return hacked;
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Patches", name));

    private string WriteDat(string file = "dat/nes.dat", string name = "Nintendo - Nintendo Entertainment System (Headerless)", IEnumerable<(string Game, byte[] Data)>? games = null)
    {
        var entries = string.Concat((games ?? Games).Select(g =>
            $"""<game name="{g.Game}"><description>{g.Game}</description><rom name="{g.Game}.unh" size="{g.Data.Length}" crc="{Crc(g.Data):x8}"/></game>"""));
        return _temp.WriteText(file, $"""<?xml version="1.0"?><datafile><header><name>{name}</name><homepage>No-Intro</homepage></header>{entries}</datafile>""");
    }

    /// <summary>The ROMs of the source, among those of <see cref="Games"/>.</summary>
    private void WriteSource(params string[] games)
    {
        foreach (var game in games)
        {
            _temp.WriteBytes($"src/{game}.unh", Games.Single(g => g.Game == game).Data);
        }
    }

    /// <summary>An IPS of no CRC32 in its name, alone in its folder with a readme.</summary>
    private void WritePatch(string folder, string patch, string readme, byte[]? origin = null)
    {
        var data = origin ?? Smb2;
        _temp.WriteBytes($"patches/{folder}/{patch}", IpsDiff(data, Hack(data)));
        _temp.WriteText($"patches/{folder}/readme.txt", readme);
    }

    private SortConfig Config(string dat) => new()
    {
        SourceDir = _src,
        DestinationDir = _dst,
        NamingConvention = NamingConvention.NoIntro,
        DatFile = dat,
        PatchDir = _patches
    };

    private async Task<SortExecutionResult> SimulateAsync(string dat) => await _services.Processor.ExecuteAsync(Config(dat), isDryRun: true);

    private static string Why(PatchOriginSuggestion suggestion) => suggestion.Reason!.Key;

    // =========================================================================
    // Names of the source
    // =========================================================================

    [Fact]
    public async Task AReadmeThatNamesARomInQuotes_ShouldProposeIt_WithoutApplyingIt()
    {
        WriteSource("Super Mario Bros. (World)", "Super Mario Bros. 2 (USA)");
        WritePatch("Super Doki Bros (SMB2 Hack)", "Super Doki Bros (SMB2 Hack).ips", Fixture("super-doki-bros.txt"));

        var result = await SimulateAsync(WriteDat());

        result.Items.Should().NotContain(it => it.Patch != null, "a suggestion is never applied");
        var unapplied = result.UnappliedPatches.Should().ContainSingle().Which;
        unapplied.Reason.Key.Should().Be("reason.patch_no_crc");
        var suggestion = unapplied.Suggestions.Should().ContainSingle().Which;
        suggestion.Origin.DisplayName.Should().Be("Super Mario Bros. 2 (USA).unh");
        suggestion.Crc32.Should().Be(Crc(Smb2));
        suggestion.Reason!.Key.Should().Be("patches.why_readme_names");
        suggestion.Reason.Args["note"].Should().Be("readme.txt");
        _services.I18n.Format(suggestion.Reason).Should().Be("readme.txt cite le nom de cette ROM");
    }

    [Fact]
    public async Task AReadmeThatNamesARomAfterAParenthesis_ShouldProposeIt_AndNotReadItsMd5()
    {
        WriteSource("Zelda II - The Adventure of Link (USA)", "Super Mario Bros. 2 (USA)");
        WritePatch("Zelda2-Redux", "Zelda2-Redux.ips", Fixture("zelda2-redux.txt"), Zelda2);

        var unapplied = (await SimulateAsync(WriteDat())).UnappliedPatches.Should().ContainSingle().Which;

        unapplied.Reason.Key.Should().Be("reason.patch_no_crc", "an MD5 is no CRC32");
        unapplied.Suggestions.Select(s => s.Origin.DisplayName).Should().Equal("Zelda II - The Adventure of Link (USA).unh");
    }

    [Fact]
    public async Task TheBlockOfRomhackingNet_ShouldKeepItsCrc32Reason_AndProposeTheRomItNames()
    {
        // Its CRC32 is that of the whole file under an old iNES 1.0 header: no ROM of the DAT has it
        WriteSource("Mega Man 2 (USA)");
        WritePatch("Rmex", "rockmanEXILE.ips", Fixture("rmex-romhacking.txt"), MegaMan2);

        var unapplied = (await SimulateAsync(WriteDat())).UnappliedPatches.Should().ContainSingle().Which;

        unapplied.Reason.Key.Should().Be("reason.patch_not_in_dat_note");
        unapplied.Reason.Args["crc"].Should().Be("5e268761", "the MD5, SHA-1 and SHA-256 lines give no CRC32");
        unapplied.Suggestions.Select(s => (s.Origin.DisplayName, Why(s))).Should().Equal(("Mega Man 2 (USA).unh", "patches.why_readme_names"));
    }

    [Fact]
    public async Task AReadmeThatNamesARomOnItsOwnLine_ShouldProposeIt_BesideItsTwoUnknownCrc32()
    {
        WriteSource("Final Fantasy III (Japan)");
        WritePatch("FF3 Maeson", "FFIII Maeson - For Japanese Version.ips", Fixture("ff3-maeson.txt"), Ff3);

        var unapplied = (await SimulateAsync(WriteDat())).UnappliedPatches.Should().ContainSingle().Which;

        unapplied.Reason.Key.Should().Be("reason.patch_not_in_dat_note");
        unapplied.Reason.Args["crc"].Should().Be("170163f1, 92189bae");
        unapplied.Suggestions.Select(s => s.Origin.DisplayName).Should().Equal("Final Fantasy III (Japan).unh");
    }

    [Fact]
    public async Task TheDatabaseMatchLine_ShouldProposeTheGameItNames()
    {
        WriteSource("Super Mario Bros. 2 (USA)", "Super Mario Bros. (World)");
        WritePatch("Hack", "hack.ips", "ROM / ISO Information:\nDatabase match: Super Mario Bros. (World)\nFile/ROM CRC32: 12345678\n", Smb);

        var unapplied = (await SimulateAsync(WriteDat())).UnappliedPatches.Should().ContainSingle().Which;

        unapplied.Suggestions.Select(s => s.Origin.DisplayName).Should().Equal("Super Mario Bros. (World).unh");
    }

    [Fact]
    public async Task TheRomsTheReadmeNames_ShouldComeBeforeThoseTheNameOfThePatchProposes()
    {
        WriteSource("Super Mario Bros. (World)", "Super Mario Bros. 2 (USA)");
        WritePatch("Hacks", "Super Mario Bros. (World) Hack.ips", "Made for \"Super Mario Bros. 2 (USA).nes\"? No: \"Super Mario Bros. 2 (USA).unh\".");

        var unapplied = (await SimulateAsync(WriteDat())).UnappliedPatches.Should().ContainSingle().Which;

        unapplied.Suggestions.Select(s => (s.Origin.DisplayName, Why(s))).Should().Equal(
            ("Super Mario Bros. 2 (USA).unh", "patches.why_readme_names"),
            ("Super Mario Bros. (World).unh", "patches.why_name"));
    }

    // =========================================================================
    // Names that name nothing
    // =========================================================================

    [Theory]
    [InlineData("beatles-pepperland.txt")]
    [InlineData("smim.txt")]
    public async Task AGoodToolsNameOrAnInexactOne_ShouldProposeNothing(string readme)
    {
        WriteSource("Super Mario Bros. (World)", "Super Mario Bros. 2 (USA)");
        WritePatch("Hack", "hack.ips", Fixture(readme));

        var unapplied = (await SimulateAsync(WriteDat())).UnappliedPatches.Should().ContainSingle().Which;

        unapplied.Reason.Key.Should().Be("reason.patch_no_crc");
        unapplied.Suggestions.Should().BeEmpty();
    }

    [Fact]
    public async Task AReadmeAmongSeveralPatches_ShouldComeWithNone()
    {
        // As in the folder of Final Fantasy III Maeson: most of its patches apply after a translation, not to the ROM it names
        WriteSource("Final Fantasy III (Japan)");
        _temp.WriteBytes("patches/FF3/For Japanese Version.ips", IpsDiff(Ff3, Hack(Ff3)));
        _temp.WriteBytes("patches/FF3/For Translation.ips", IpsDiff(Ff3, Hack(Hack(Ff3))));
        _temp.WriteText("patches/FF3/1 - Readme.txt", Fixture("ff3-maeson.txt"));

        var result = await SimulateAsync(WriteDat());

        result.UnappliedPatches.Should().HaveCount(2).And.OnlyContain(u => u.Reason.Key == "reason.patch_no_crc" && u.Suggestions.Count == 0);
    }

    [Fact]
    public async Task ABps_ShouldNeverReadItsReadme()
    {
        WriteSource("Super Mario Bros. 2 (USA)");
        var other = Data(9, 512);
        _temp.WriteBytes("patches/Hack/hack.bps", BpsLinear(other, Hack(other)));
        _temp.WriteText("patches/Hack/readme.txt", Fixture("super-doki-bros.txt"));

        var unapplied = (await SimulateAsync(WriteDat())).UnappliedPatches.Should().ContainSingle().Which;

        unapplied.Reason.Key.Should().Be("reason.patch_not_in_dat");
        unapplied.Suggestions.Should().BeEmpty();
    }

    // =========================================================================
    // The ROM it names, missing from the source
    // =========================================================================

    [Fact]
    public async Task AReadmeThatNamesARomOfTheDatMissingFromTheSource_ShouldNameItInTheReason()
    {
        WriteSource("Super Mario Bros. (World)");
        WritePatch("Super Doki Bros (SMB2 Hack)", "Super Doki Bros (SMB2 Hack).ips", Fixture("super-doki-bros.txt"));

        var unapplied = (await SimulateAsync(WriteDat())).UnappliedPatches.Should().ContainSingle().Which;

        unapplied.Reason.Key.Should().Be("reason.patch_named_origin_missing");
        unapplied.Reason.Args["note"].Should().Be("readme.txt");
        unapplied.Reason.Args["origin"].Should().Be("Super Mario Bros. 2 (USA).unh");
        _services.I18n.Format(unapplied.Reason).Should().Be("readme.txt cite la ROM d'origine, Super Mario Bros. 2 (USA).unh, qui manque dans la source");
        unapplied.Suggestions.Should().BeEmpty();
    }

    [Fact]
    public async Task TheRomOfTheBlockOfRomhackingNet_MissingFromTheSource_ShouldBeNamedInsteadOfItsUnknownCrc32()
    {
        WriteSource("Super Mario Bros. (World)");
        WritePatch("Rmex", "rockmanEXILE.ips", Fixture("rmex-romhacking.txt"), MegaMan2);

        var reason = (await SimulateAsync(WriteDat())).UnappliedPatches.Should().ContainSingle().Which.Reason;

        reason.Key.Should().Be("reason.patch_named_origin_missing");
        reason.Args["origin"].Should().Be("Mega Man 2 (USA).unh");
    }

    [Fact]
    public async Task ARomNamedMissingFromTheSourceOfALot_ShouldBeNamedWithItsDat()
    {
        WriteDat("lot/nes.dat", "Nintendo - NES", Games);
        var md = Data(8, 1024);
        WriteDat("lot/md.dat", "Sega - Mega Drive", [("Other (USA)", md)]);
        _temp.WriteBytes("src/Super Mario Bros. (World).unh", Smb);
        WritePatch("Super Doki Bros (SMB2 Hack)", "Super Doki Bros (SMB2 Hack).ips", Fixture("super-doki-bros.txt"));

        var reason = (await SimulateAsync(_temp.GetFilePath("lot"))).UnappliedPatches.Should().ContainSingle().Which.Reason;

        reason.Key.Should().Be("reason.patch_named_origin_missing");
        reason.Args["origin"].Should().Be("Super Mario Bros. 2 (USA).unh (Nintendo - NES)");
    }

    // =========================================================================
    // Reading the names
    // =========================================================================

    [Theory]
    [InlineData("Make sure to use the \"Super Mario Bros. 2 (USA).nes\" ROM.", new[] { "Super Mario Bros. 2 (USA).nes" })]
    [InlineData("Mega Man 2 (USA).nes - NOINTRO", new[] { "Mega Man 2 (USA).nes" })]
    [InlineData("* ROM (Zelda II (USA).nes, MD5: 764d36fa)", new[] { "Zelda II (USA).nes" })]
    [InlineData("Database match: Super Mario Bros. (World)", new[] { "Super Mario Bros. (World)" })]
    [InlineData("Patch Super Mario Bros (World).nes using", new string[0])]
    [InlineData("game.nes and Mega Man 2 (USA).nes, then game.nes again", new[] { "game.nes", "Mega Man 2 (USA).nes" })]
    public void CitedNames_ShouldGiveTheKnownNamesOfALine(string text, string[] expected)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Super Mario Bros. 2 (USA).nes", "Mega Man 2 (USA).nes", "Zelda II (USA).nes", "Super Mario Bros. (World)", "game.nes"
        };

        PatchMatcher.CitedNames(text, known.Contains).Should().Equal(expected);
    }
}
