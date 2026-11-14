using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using static RomSieve.Core.Tests.Fixtures.PatchTestEncoder;

namespace RomSieve.Core.Tests;

/// <summary>
/// The reason of a patch left without origin, after the DAT: the ROM of the DAT it was made for, missing from the source,
/// is named (by the CRC32 and the size of a UPS or a BPS, the CRC32 of the name or of the readme of an IPS; with its DAT
/// in a lot); otherwise no ROM of the DAT has its CRC32, and for a DAT of a system with a copier header the reason says
/// the patch may be for another form of the ROM (a DAT of the files with their header against a patch of the data, and
/// the reverse).
/// </summary>
public sealed class PatchMissingOriginTests : IDisposable
{
    private readonly CoreTestServices _services = new();
    private readonly TempDirectoryFixture _temp = new();
    private readonly string _src;
    private readonly string _dst;
    private readonly string _patches;

    private static readonly byte[] Usa = Data(1, 4096);
    private static readonly byte[] Europe = Data(2, 4096);

    public PatchMissingOriginTests()
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

    private string WriteDat(string file, string name, params (string Game, string Rom, byte[] Data)[] roms)
    {
        var games = string.Concat(roms.Select(r =>
            $"""<game name="{r.Game}"><description>{r.Game}</description><rom name="{r.Rom}" size="{r.Data.Length}" crc="{Crc(r.Data):x8}"/></game>"""));
        return _temp.WriteText(
            file,
            $"""<?xml version="1.0"?><datafile><header><name>{name}</name><homepage>No-Intro</homepage></header>{games}</datafile>""");
    }

    /// <summary>A Mega Drive DAT of two games; only the American one is in the source.</summary>
    private string WriteUsaOnly()
    {
        _temp.WriteBytes("src/Game (USA).md", Usa);
        return WriteDat("dat/md.dat", "Sega - Mega Drive - Genesis", ("Game (USA)", "Game (USA).md", Usa), ("Game (Europe)", "Game (Europe).md", Europe));
    }

    private SortConfig Config(string dat) => new()
    {
        SourceDir = _src,
        DestinationDir = _dst,
        NamingConvention = NamingConvention.NoIntro,
        DatFile = dat,
        PatchDir = _patches
    };

    private async Task<UnappliedPatch> UnappliedAsync(string dat)
    {
        var result = await _services.Processor.ExecuteAsync(Config(dat), isDryRun: true);
        return result.UnappliedPatches.Should().ContainSingle().Which;
    }

    private static string WhyKey(LocalizedMessage reason) =>
        reason.Args["why"].Should().BeOfType<LocalizedMessage>().Which.Key;

    // =========================================================================
    // A ROM of the DAT missing from the source
    // =========================================================================

    [Fact]
    public async Task ABpsForARomOfTheDatMissingFromTheSource_ShouldNameIt()
    {
        var dat = WriteUsaOnly();
        _temp.WriteBytes("patches/Game (Europe) (Hack).bps", BpsLinear(Europe, Hack(Europe)));

        var reason = (await UnappliedAsync(dat)).Reason;

        reason.Key.Should().Be("reason.patch_origin_missing");
        reason.Args["origin"].Should().Be("Game (Europe).md");
        _services.I18n.Format(reason).Should().Be("La ROM d'origine, Game (Europe).md, manque dans la source");
    }

    [Fact]
    public async Task AnIpsWhoseNameGivesTheCrc32OfAMissingRom_ShouldNameIt()
    {
        var dat = WriteUsaOnly();
        _temp.WriteBytes($"patches/Game (Hack) [{Crc(Europe):x8}].ips", IpsDiff(Europe, Hack(Europe)));

        var reason = (await UnappliedAsync(dat)).Reason;

        reason.Key.Should().Be("reason.patch_origin_missing");
        reason.Args["origin"].Should().Be("Game (Europe).md");
    }

    [Fact]
    public async Task AnIpsWhoseReadmeGivesTheCrc32OfAMissingRom_ShouldNameItAndTheReadme()
    {
        var dat = WriteUsaOnly();
        _temp.WriteBytes("patches/Game (Hack).ips", IpsDiff(Europe, Hack(Europe)));
        _temp.WriteText("patches/Game (Hack).txt", $"CRC32: {Crc(Europe):X8}");

        var reason = (await UnappliedAsync(dat)).Reason;

        reason.Key.Should().Be("reason.patch_origin_missing_note");
        reason.Args["note"].Should().Be("Game (Hack).txt");
        reason.Args["origin"].Should().Be("Game (Europe).md");
    }

    [Fact]
    public async Task AUpsOfACrc32OfTheDatButOfAnotherSize_ShouldNameNoRom()
    {
        // The CRC32 of the DAT entry, but a source of another size: that ROM is not the one the patch was made for
        var dat = WriteUsaOnly();
        var patch = UpsRaw((ulong)Europe.Length + 1, (ulong)Europe.Length + 1, [0x00], sourceCrc: Crc(Europe), targetCrc: 0);
        _temp.WriteBytes("patches/Game (Hack).ups", Resign(patch));

        var reason = (await UnappliedAsync(dat)).Reason;

        reason.Key.Should().Be("reason.patch_not_in_dat");
    }

    [Fact]
    public async Task ARomMissingFromTheSourceInALot_ShouldBeNamedWithItsDat()
    {
        WriteDat("lot/md.dat", "Sega - Mega Drive - Genesis", ("Game (USA)", "Game (USA).md", Usa));
        var other = Data(3, 2048);
        WriteDat("lot/sms.dat", "Sega - Master System - Mark III", ("Other (Europe)", "Other (Europe).sms", other));
        _temp.WriteBytes("src/Game (USA).md", Usa);
        _temp.WriteBytes("patches/Other (Fr).bps", BpsLinear(other, Hack(other)));

        var reason = (await UnappliedAsync(_temp.GetFilePath("lot"))).Reason;

        reason.Key.Should().Be("reason.patch_origin_missing");
        reason.Args["origin"].Should().Be("Other (Europe).sms (Sega - Master System - Mark III)", "the DAT of the lot that knows the ROM is read for it");
    }

    // =========================================================================
    // No ROM of the DAT
    // =========================================================================

    [Fact]
    public async Task ACrc32OfNoRomOfTheDat_ShouldSayThePatchIsForAnotherDump()
    {
        var dat = WriteUsaOnly();
        var unknown = Data(9, 100);
        _temp.WriteBytes("patches/Other (Hack).bps", BpsLinear(unknown, Hack(unknown)));

        var reason = (await UnappliedAsync(dat)).Reason;

        reason.Key.Should().Be("reason.patch_not_in_dat");
        reason.Args["crc"].Should().Be(Crc(unknown).ToString("x8"));
        WhyKey(reason).Should().Be("patches.not_in_dat_why", "a Mega Drive ROM has no copier header");
        _services.I18n.Format(reason).Should().Be(
            $"Aucune ROM du DAT n'a le CRC32 {Crc(unknown):x8} : le patch a été fait pour un autre dump ou pour un hack");
    }

    [Fact]
    public async Task AReadmeOfNoRomOfTheDat_ShouldSayItToo()
    {
        var dat = WriteUsaOnly();
        _temp.WriteBytes("patches/Game (Hack).ips", IpsDiff(Usa, Hack(Usa)));
        _temp.WriteText("patches/Game (Hack).txt", "CRC32: 12345678");

        var reason = (await UnappliedAsync(dat)).Reason;

        reason.Key.Should().Be("reason.patch_not_in_dat_note");
        reason.Args["note"].Should().Be("Game (Hack).txt");
        reason.Args["crc"].Should().Be("12345678");
    }

    [Fact]
    public async Task ADatOfTheFilesWithTheirHeader_ShouldNotSayThatAPatchOfTheDataIsForAnotherDump()
    {
        // The DAT gives the NES files with their iNES header: the CRC32 of the data alone is in none of its entries
        var data = Data(4, 8192);
        byte[] file = [.. Ascii("NES\u001a"), 0x02, 0x01, 0x01, 0x00, .. new byte[8], .. data];
        _temp.WriteBytes("src/Other (USA).nes", Data(5, 1024));
        var dat = WriteDat(
            "dat/nes.dat",
            "Nintendo - Nintendo Entertainment System (Headered)",
            ("Game (USA)", "Game (USA).nes", file),
            ("Other (USA)", "Other (USA).nes", Data(5, 1024)));
        _temp.WriteBytes("patches/Game (USA) (Hack).bps", BpsLinear(data, Hack(data)));

        var reason = (await UnappliedAsync(dat)).Reason;

        reason.Key.Should().Be("reason.patch_not_in_dat");
        WhyKey(reason).Should().Be("patches.not_in_dat_why_header");
    }

    [Fact]
    public async Task ADatOfTheDataWithoutHeader_ShouldNotSayThatAPatchOfTheWholeFileIsForAnotherDump()
    {
        var data = Data(4, 8192);
        byte[] file = [.. Ascii("NES\u001a"), 0x02, 0x01, 0x01, 0x00, .. new byte[8], .. data];
        _temp.WriteBytes("src/Other (USA).unh", Data(5, 1024));
        var dat = WriteDat(
            "dat/nes.dat",
            "Nintendo - Nintendo Entertainment System (Headerless)",
            ("Game (USA)", "Game (USA).unh", data),
            ("Other (USA)", "Other (USA).unh", Data(5, 1024)));
        _temp.WriteBytes("patches/Game (USA) (Hack).ups", Ups(file, Hack(file)));

        var reason = (await UnappliedAsync(dat)).Reason;

        reason.Key.Should().Be("reason.patch_not_in_dat");
        WhyKey(reason).Should().Be("patches.not_in_dat_why_header");
        _services.I18n.Format(reason).Should().EndWith("(avec un autre en-tête copieur, ou sans)");
    }

    [Fact]
    public async Task ALotWhoseDatsReadAreOfNoSystemWithAHeader_ShouldSayThePatchIsForAnotherDump()
    {
        // Only the DATs that recognised a file of the source are read: the NES DAT of the lot, which recognised none, is not
        WriteDat("lot/md.dat", "Sega - Mega Drive - Genesis", ("Game (USA)", "Game (USA).md", Usa));
        WriteDat("lot/nes.dat", "Nintendo - Nintendo Entertainment System", ("Other (USA)", "Other (USA).nes", Data(6, 1024)));
        _temp.WriteBytes("src/Game (USA).md", Usa);
        var unknown = Data(9, 100);
        _temp.WriteBytes("patches/Other (Hack).bps", BpsLinear(unknown, Hack(unknown)));

        var reason = (await UnappliedAsync(_temp.GetFilePath("lot"))).Reason;

        reason.Key.Should().Be("reason.patch_not_in_dat");
        WhyKey(reason).Should().Be("patches.not_in_dat_why");
    }

    [Fact]
    public async Task ALotWithAReadDatOfASystemWithAHeader_ShouldMentionTheOtherForms()
    {
        WriteDat("lot/md.dat", "Sega - Mega Drive - Genesis", ("Game (USA)", "Game (USA).md", Usa));
        var nes = Data(6, 1024);
        WriteDat("lot/nes.dat", "Nintendo - Nintendo Entertainment System", ("Other (USA)", "Other (USA).nes", nes));
        _temp.WriteBytes("src/Game (USA).md", Usa);
        _temp.WriteBytes("src/Other (USA).nes", nes);
        var unknown = Data(9, 100);
        _temp.WriteBytes("patches/Other (Hack).bps", BpsLinear(unknown, Hack(unknown)));

        var reason = (await UnappliedAsync(_temp.GetFilePath("lot"))).Reason;

        WhyKey(reason).Should().Be("patches.not_in_dat_why_header");
    }
}
