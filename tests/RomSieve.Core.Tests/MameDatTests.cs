using System.Text;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// MAME DATs: output of <c>mame -listxml</c> and SabreTools DATs (extracts of real DATs in <c>Fixtures/Mame</c>, see its
/// README), detection of the convention, MAME attributes, ROMs that were never dumped, exports, encodings, and the
/// stream reading of XML DATs.
/// </summary>
public sealed class MameDatTests
{
    private readonly CoreTestServices _services = new();

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mame", name);

    private Task<DatCatalog> LoadAsync(string name) => _services.DatService.ParseDatFileAsync(Fixture(name));

    [Fact]
    public async Task ListXml_ShouldBeDetectedAsMameAndNamedAfterItsBuild()
    {
        var catalog = await LoadAsync("listxml.dat");

        catalog.Header.Convention.Should().Be(NamingConvention.Mame);
        catalog.Header.Name.Should().Be("MAME");
        catalog.Header.Version.Should().Be("0.289 (mame0289)");
        catalog.Games.Should().HaveCount(22);

        // 438 ROMs, of which 2 were never dumped, and 4 disks
        catalog.Entries.Should().HaveCount(440);
        catalog.Entries.Count(e => e.IsDisk).Should().Be(4);
    }

    [Fact]
    public async Task ListXml_ShouldKeepParentsClonesAndBiosApart()
    {
        var catalog = await LoadAsync("listxml.dat");

        catalog.CloneToParent.Keys.Should().BeEquivalentTo(
            "sf2ua", "sf2ug", "sf2uh", "sf2j", "sf2b", "sf2en", "3stoogesa", "aafbb", "aafbc", "area51t", "timecrs4j");
        catalog.GetCanonicalGameName("sf2uh").Should().Be("sf2");
        catalog.GetCanonicalGameName("sf2ce").Should().Be("sf2ce");

        var mslug = catalog.GameByName["mslug"];
        mslug.RomOf.Should().Be("neogeo");
        catalog.IsParentGame(mslug).Should().BeTrue();
        catalog.GameByName["neogeo"].IsBios.Should().BeTrue();
        catalog.GameByName["sf2ua"].RomOf.Should().Be("sf2");
    }

    [Fact]
    public async Task ListXml_ShouldReadTheMachineInformation()
    {
        var catalog = await LoadAsync("listxml.dat");

        var pigs = catalog.GameByName["3lilpigs"];
        pigs.IsMechanical.Should().BeTrue();
        pigs.Year.Should().Be("199?");
        pigs.Manufacturer.Should().Be("Doyle & Assoc.");
        pigs.DriverStatus.Should().Be(MameDriverStatus.Preliminary);
        pigs.DeviceRefs.Should().Contain("hd44780");

        var device = catalog.GameByName["hd44780"];
        device.IsDevice.Should().BeTrue();
        device.IsRunnable.Should().BeFalse();
        device.DriverStatus.Should().Be(MameDriverStatus.Unknown);

        catalog.GameByName["sf2"].DriverStatus.Should().Be(MameDriverStatus.Good);
        catalog.GameByName["sf2b"].DriverStatus.Should().Be(MameDriverStatus.Imperfect);
        catalog.GameByName["sf2b"].EmulationStatus.Should().Be(MameDriverStatus.Good);
        catalog.GameByName["aafbb"].EmulationStatus.Should().Be(MameDriverStatus.Preliminary);
        device.EmulationStatus.Should().Be(MameDriverStatus.Unknown);

        // Only the unemulated features count (sf2b's graphics are imperfect; airrace has no sound hardware)
        catalog.GameByName["timecrs4"].UnemulatedFeatures.Should().Be(MameFeatures.Sound);
        catalog.GameByName["sf2b"].UnemulatedFeatures.Should().Be(MameFeatures.None);
        catalog.GameByName["airrace"].UnemulatedFeatures.Should().Be(MameFeatures.None);
        catalog.GameByName["3lilpigs"].RequiresArtwork.Should().BeFalse("its driver does not say requiresartwork");
        catalog.GameByName["sf2"].IsRunnable.Should().BeTrue();
        catalog.GameByName["sf2"].Manufacturer.Should().Be("Capcom");
        catalog.GameByName["sf2"].Description.Should().Be("Street Fighter II: The World Warrior (World 910522)");
    }

    [Fact]
    public async Task ListXml_SharedRomsAndDisks_ShouldKeepTheirNameInTheParent()
    {
        var catalog = await LoadAsync("listxml.dat");

        catalog.GameByName["mslug"].Roms.Single(r => r.Name == "sp-s2.sp1").Merge.Should().Be("sp-s2.sp1");
        catalog.GameByName["sf2"].Roms.Should().OnlyContain(r => r.Merge == null);

        var cloneDisk = catalog.GameByName["timecrs4j"].Roms.Single(r => r.IsDisk);
        cloneDisk.Name.Should().Be("tsf1-ha");
        cloneDisk.Merge.Should().Be("tsf1-ha");

        // The shared disk identifies the parent and the clone
        catalog.FindAllMatches(0, sha1: cloneDisk.Sha1).Select(e => e.GameName)
            .Should().Equal("timecrs4", "timecrs4j");
    }

    [Fact]
    public async Task NoDumpRoms_ShouldStayInTheirGameWithoutBeingIdentifiableNorMissing()
    {
        var catalog = await LoadAsync("listxml.dat");

        var noDump = catalog.GameByName["sf2en"].Roms.Single(r => r.Name == "unknown.1a");
        noDump.Status.Should().Be(DatRomStatus.NoDump);
        noDump.Crc.Should().BeEmpty();

        catalog.Entries.Should().NotContain(noDump);
        catalog.ByName.Should().NotContainKey("unknown.1a");
        catalog.GetGamesForRom("unknown.1a").Should().BeEmpty();

        // Nothing can make it missing: even a fixdat of every indexed entry leaves it out
        var fixDat = catalog.CreateFixDat(catalog.Entries);
        fixDat.GameByName["sf2en"].Roms.Should().NotContain(r => r.Name == "unknown.1a");

        // The fixdat keeps what the DAT says of the emulation
        fixDat.GameByName["timecrs4"].EmulationStatus.Should().Be(MameDriverStatus.Preliminary);
        fixDat.GameByName["timecrs4"].UnemulatedFeatures.Should().Be(MameFeatures.Sound);
    }

    [Fact]
    public void RequiresArtwork_ShouldBeReadAndKeptByTheFixDat()
    {
        // An LCD handheld of MAME 0.289 (hh_smc1k.cpp), its chips, display and inputs left out
        var catalog = _services.DatService.ParseLogiqxXml("""
            <?xml version="1.0"?>
            <mame build="0.289">
            	<machine name="tkkongq" sourcefile="handheld/hh_smc1k.cpp">
            		<description>King Kong (Tiger, Quartz Game Clock)</description>
            		<year>1982</year>
            		<manufacturer>Tiger Electronics</manufacturer>
            		<rom name="smc1112_d0w0" size="2048" crc="77ff7a01" sha1="ed5dc860e400dc11518b0a32f13879e143c17953" region="maincpu" offset="0"/>
            		<driver status="preliminary" emulation="preliminary" savestate="supported" requiresartwork="yes"/>
            	</machine>
            </mame>
            """);

        var game = catalog.GameByName["tkkongq"];
        game.RequiresArtwork.Should().BeTrue();
        game.IsMechanical.Should().BeFalse();
        catalog.CreateFixDat(catalog.Entries).GameByName["tkkongq"].RequiresArtwork.Should().BeTrue();
    }

    [Fact]
    public async Task BadDumps_ShouldBeIndexedWithTheirStatus()
    {
        var catalog = await LoadAsync("listxml.dat");

        var badDump = catalog.GameByName["3lilpigs"].Roms.Single(r => r.Name == "3-pigs.u6");
        badDump.Status.Should().Be(DatRomStatus.BadDump);
        catalog.FindMatch(badDump.Size, badDump.Crc).Should().BeSameAs(badDump);
        catalog.GameByName["3lilpigs"].Roms.Where(r => r.Status == DatRomStatus.Good).Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("sabretools_nonmerged.dat", 20)]
    [InlineData("sabretools_merged.dat", 11)]
    public async Task SabreToolsDats_ShouldBeDetectedAsMameWithoutParentCloneRelations(string name, int games)
    {
        var catalog = await LoadAsync(name);

        catalog.Header.Convention.Should().Be(NamingConvention.Mame);
        catalog.Games.Should().HaveCount(games);
        catalog.CloneToParent.Should().BeEmpty();
        catalog.GameByName["neogeo"].IsBios.Should().BeTrue();
        catalog.GameByName["3xtwin"].IsDevice.Should().BeTrue();
        catalog.GameByName["3xtwin"].IsRunnable.Should().BeFalse();
        catalog.GameByName["3lilpigs"].IsMechanical.Should().BeTrue();
    }

    [Fact]
    public async Task SabreToolsDats_ShouldKeepTheirRomsAsListed()
    {
        var nonMerged = await LoadAsync("sabretools_nonmerged.dat");
        var merged = await LoadAsync("sabretools_merged.dat");

        // A non-merged set also holds the ROMs of its BIOS
        nonMerged.GameByName["mslug"].Roms.Should().HaveCount(43).And.Contain(r => r.Name == "000-lo.lo");

        // A merged parent holds the ROMs of its clones under "clone\rom"
        var sf2 = merged.GameByName["sf2"].Roms;
        sf2.Should().HaveCount(222);
        sf2.Count(r => r.Name.Contains('\\')).Should().Be(191);
        sf2.Should().Contain(r => r.Name == @"sf2ua\sf2u_30a.11e");
    }

    [Fact]
    public void Detection_ShouldRecognizeMameByItsAttributesWithoutHeader()
    {
        const string xml = """
            <?xml version="1.0"?>
            <datafile>
            	<machine name="neogeo" isbios="yes"><description>Neo-Geo MV-6F</description><rom name="sp-s2.sp1" size="131072" crc="9036d879"/></machine>
            </datafile>
            """;

        _services.DatService.ParseLogiqxXml(xml).Header.Convention.Should().Be(NamingConvention.Mame);
    }

    [Fact]
    public void Detection_ClrMameProInTheHeader_ShouldNotMeanMame()
    {
        const string text = """
            clrmamepro (
            	name "Collection"
            	homepage "https://example.org/clrmamepro"
            )

            game (
            	name "Game"
            	rom ( name "game.bin" size 4 crc 12345678 )
            )
            """;

        _services.DatService.ParseClrMameProText(text).Header.Convention.Should().NotBe(NamingConvention.Mame);
    }

    [Fact]
    public void ParseClrMamePro_ShouldReadTheMameInformation()
    {
        const string text = """
            clrmamepro (
            	name "MAME"
            )

            resource (
            	name "neogeo"
            	description "Neo-Geo MV-6F"
            	year 1990
            	manufacturer "SNK"
            	rom ( name "sp-s2.sp1" size 131072 crc 9036d879 )
            )

            game (
            	name "sf2en"
            	description "Street Fighter II: The World Warrior (World 910204, conversion)"
            	year 1991
            	manufacturer "Capcom"
            	cloneof "sf2"
            	romof "sf2"
            	rom ( name "sf2-5m.4a" merge "sf2-5m.4a" size 524288 crc 22c9cc8e )
            	rom ( name "unknown.1a" size 279 flags nodump )
            	rom ( name "bad.rom" size 4 crc 12345678 status baddump )
            	driver ( status imperfect emulation good )
            )
            """;

        var catalog = _services.DatService.ParseClrMameProText(text);

        var bios = catalog.GameByName["neogeo"];
        bios.IsBios.Should().BeTrue();
        bios.Year.Should().Be("1990");
        bios.Manufacturer.Should().Be("SNK");

        var game = catalog.GameByName["sf2en"];
        game.IsBios.Should().BeFalse();
        game.DriverStatus.Should().Be(MameDriverStatus.Imperfect);
        game.EmulationStatus.Should().Be(MameDriverStatus.Good);
        game.Roms.Single(r => r.Name == "sf2-5m.4a").Merge.Should().Be("sf2-5m.4a");
        game.Roms.Single(r => r.Name == "unknown.1a").Status.Should().Be(DatRomStatus.NoDump);
        game.Roms.Single(r => r.Name == "bad.rom").Status.Should().Be(DatRomStatus.BadDump);
        catalog.Entries.Select(e => e.Name).Should().BeEquivalentTo("sp-s2.sp1", "sf2-5m.4a", "bad.rom");
        catalog.Header.Convention.Should().Be(NamingConvention.Mame);
    }

    [Fact]
    public async Task ExportedDats_ShouldKeepTheMameInformation()
    {
        var catalog = await LoadAsync("listxml.dat");

        foreach (var exported in new[]
                 {
                     _services.DatService.ParseLogiqxXml(catalog.ToLogiqxXml()),
                     _services.DatService.ParseClrMameProText(catalog.ToClrMamePro())
                 })
        {
            exported.Games.Select(g => g.Name).Should().Equal(catalog.Games.Select(g => g.Name));
            exported.Entries.Should().HaveCount(catalog.Entries.Count);
            exported.CloneToParent.Should().BeEquivalentTo(catalog.CloneToParent);
            exported.GameByName["neogeo"].IsBios.Should().BeTrue();
            exported.GameByName["mslug"].RomOf.Should().Be("neogeo");
            exported.GameByName["3lilpigs"].Year.Should().Be("199?");
            exported.GameByName["3lilpigs"].Manufacturer.Should().Be("Doyle & Assoc.");
            exported.GameByName["mslug"].Roms.Single(r => r.Name == "sp-s2.sp1").Merge.Should().Be("sp-s2.sp1");
            exported.GameByName["timecrs4j"].Roms.Single(r => r.IsDisk).Merge.Should().Be("tsf1-ha");
            exported.GameByName["3lilpigs"].Roms.Single(r => r.Name == "3-pigs.u6").Status.Should().Be(DatRomStatus.BadDump);
            exported.GameByName["sf2en"].Roms.Single(r => r.Name == "unknown.1a").Status.Should().Be(DatRomStatus.NoDump);
            exported.Header.Convention.Should().Be(NamingConvention.Mame);
        }

        var fromXml = _services.DatService.ParseLogiqxXml(catalog.ToLogiqxXml());
        fromXml.GameByName["hd44780"].IsDevice.Should().BeTrue();
        fromXml.GameByName["hd44780"].IsRunnable.Should().BeFalse();
        fromXml.GameByName["3lilpigs"].IsMechanical.Should().BeTrue();
    }

    [Fact]
    public async Task ParseDatFile_Utf16File_ShouldBeReadLikeItsUtf8Version()
    {
        // What PowerShell writes for "mame -listxml > mame.xml"
        using var tmp = new TempDirectoryFixture();
        var utf16Path = tmp.GetFilePath("mame_utf16.xml");
        await File.WriteAllTextAsync(utf16Path, await File.ReadAllTextAsync(Fixture("listxml.dat")), Encoding.Unicode);

        var expected = await LoadAsync("listxml.dat");
        var catalog = await _services.DatService.ParseDatFileAsync(utf16Path);

        catalog.Header.Convention.Should().Be(NamingConvention.Mame);
        catalog.Games.Select(g => g.Description).Should().Equal(expected.Games.Select(g => g.Description));
        catalog.Entries.Select(e => e.Crc).Should().Equal(expected.Entries.Select(e => e.Crc));
    }

    [Fact]
    public async Task ParseDatFile_Latin1XmlFile_ShouldFallBackToLatin1()
    {
        using var tmp = new TempDirectoryFixture();
        var path = tmp.GetFilePath("latin1.dat");
        const string xml = """
            <?xml version="1.0"?>
            <datafile>
            	<header><name>Collection</name></header>
            	<game name="Jeu"><description>Élan</description><rom name="jeu.bin" size="4" crc="12345678"/></game>
            </datafile>
            """;
        await File.WriteAllBytesAsync(path, Encoding.Latin1.GetBytes(xml));

        var catalog = await _services.DatService.ParseDatFileAsync(path);

        catalog.GameByName["Jeu"].Description.Should().Be("Élan");
    }

    [Fact]
    public void ParseLogiqxXml_ShouldSkipTheElementsItDoesNotUseAndKeepReading()
    {
        const string xml = """
            <?xml version="1.0"?>
            <mame build="0.289">
            	<machine name="first">
            		<description>First (World)</description>
            		<input players="2"><control type="joy"><description>not the game</description></control></input>
            		<dipswitch name="Lives"><dipvalue name="3" value="0" default="yes"/></dipswitch>
            		<rom name="first.bin" size="4" crc="11111111"/>
            		<driver status="good"/>
            	</machine>
            	<machine name="second" cloneof="first" romof="first"><description>Second</description><rom name="second.bin" size="4" crc="22222222"/></machine>
            </mame>
            """;

        var catalog = _services.DatService.ParseLogiqxXml(xml);

        catalog.Games.Select(g => g.Name).Should().Equal("first", "second");
        catalog.GameByName["first"].Description.Should().Be("First (World)");
        catalog.GameByName["first"].Roms.Select(r => r.Name).Should().Equal("first.bin");
        catalog.GameByName["first"].Roms[0].GameName.Should().Be("first");
        catalog.GameByName["first"].Roms[0].Description.Should().Be("First (World)");
        catalog.GameByName["second"].CloneOf.Should().Be("first");
        catalog.Header.Version.Should().Be("0.289");
    }

    [Fact]
    public void ParseLogiqxXml_Cancelled_ShouldStop()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var reader = new StreamReader(Fixture("listxml.dat"));

        var act = () => _services.DatService.ParseLogiqxXml(reader, cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }
}
