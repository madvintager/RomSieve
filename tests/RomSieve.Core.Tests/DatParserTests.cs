using System.IO.Compression;
using System.Text;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Unit tests for CLRMAMEPRO / Logiqx XML DAT parser and CRC32 checksum service.
/// </summary>
public sealed class DatParserTests
{
    private const string SampleClrMamePro = """
        clrmamepro (
        	name "Commodore Amiga - Games - Commercial - [ADF] (TOSEC-v2020-04-01_CM)"
        	description "Commodore Amiga - Games - Commercial - [ADF] (TOSEC-v2020-04-01_CM)"
        	version "2020-04-01_CM"
        	comment "TOSEC"
        	author "TOSEC"
        	homepage "www.toseconline.org"
        )

        game (
        	name "1869 (1992)(Max Design)(De)(Disk 1 of 2)[cr FLT]"
        	description "1869 (1992)(Max Design)(De)(Disk 1 of 2)[cr FLT]"
        	rom ( name "1869 (1992)(Max Design)(De)(Disk 1 of 2)[cr FLT].adf" size 901120 crc 849A37E1 md5 f2a04a37b3b44b82d3e1d6d84a30e704 sha1 5a4a51167ff505d97f26487ffaa564344d2dcf0b )
        )

        game (
        	name "1869 (1992)(Max Design)(De)(Disk 2 of 2)[cr FLT]"
        	description "1869 (1992)(Max Design)(De)(Disk 2 of 2)[cr FLT]"
        	rom ( name "1869 (1992)(Max Design)(De)(Disk 2 of 2)[cr FLT].adf" size 901120 crc 93c4e3b1 )
        )

        # A game with multiple ROMs and comments
        game (
        	name "Double Game (1991)(Ocean)"
        	// comment line
        	rom ( name "Double Game (1991)(Ocean)(Disk 1).adf" size 880000 crc 11223344 )
        	rom ( name "Double Game (1991)(Ocean)(Disk 2).adf" size 880000 crc 55667788 )
        )
        """;

    private const string SampleLogiqxXml = """
        <?xml version="1.0"?>
        <!DOCTYPE datafile PUBLIC "-//Logiqx//DTD ROMs Description Summary//EN" "http://www.logiqx.com/Dats/datafile.dtd">
        <datafile>
        	<header>
        		<name>Nintendo Entertainment System (TOSEC-v2021)</name>
        		<description>NES Collection</description>
        		<version>2021-01-01</version>
        		<author>TOSEC Team</author>
        	</header>
        	<game name="Super Mario Bros. (1985)(Nintendo)(US)">
        		<description>Super Mario Bros. (1985)(Nintendo)(US)</description>
        		<rom name="Super Mario Bros. (1985)(Nintendo)(US).nes" size="40976" crc="d445f698" md5="811b65345a20141f1766a5e12f6890d2" sha1="ea34397e573d84bbcf602e429d020465a935da15"/>
        	</game>
        </datafile>
        """;

    private readonly CoreTestServices _services = new();

    [Fact]
    public void ParseClrMameProHeaderAndGames_ShouldExtractHeaderAndAllRomEntries()
    {
        var catalog = _services.DatService.ParseClrMameProText(SampleClrMamePro);

        catalog.Header.Name.Should().Contain("Commodore Amiga");
        catalog.Header.Version.Should().Be("2020-04-01_CM");
        catalog.Header.Author.Should().Be("TOSEC");
        catalog.Entries.Should().HaveCount(4);

        var e1 = catalog.Entries[0];
        e1.Name.Should().Be("1869 (1992)(Max Design)(De)(Disk 1 of 2)[cr FLT].adf");
        e1.Size.Should().Be(901120);
        e1.Crc.Should().Be("849a37e1"); // Normalized to lowercase
        e1.Md5.Should().Be("f2a04a37b3b44b82d3e1d6d84a30e704");
        e1.Sha1.Should().Be("5a4a51167ff505d97f26487ffaa564344d2dcf0b");

        var e3 = catalog.Entries[2];
        e3.Name.Should().Be("Double Game (1991)(Ocean)(Disk 1).adf");
        e3.Crc.Should().Be("11223344");
        e3.GameName.Should().Be("Double Game (1991)(Ocean)");
    }

    [Fact]
    public void CatalogFindMatch_ShouldMatchBySizeAndCrcOrUniqueCrcFallback()
    {
        var catalog = _services.DatService.ParseClrMameProText(SampleClrMamePro);

        var match = catalog.FindMatch(fileSize: 901120, crc: "849A37E1");
        match.Should().NotBeNull();
        match!.Name.Should().Be("1869 (1992)(Max Design)(De)(Disk 1 of 2)[cr FLT].adf");

        var match2 = catalog.FindMatch(fileSize: 901120, crc: "93c4e3b1");
        match2.Should().NotBeNull();
        match2!.Name.Should().Be("1869 (1992)(Max Design)(De)(Disk 2 of 2)[cr FLT].adf");

        // Fallback to unique CRC when size differs
        catalog.FindMatch(fileSize: 12345, crc: "849A37E1").Should().NotBeNull();
        catalog.FindMatch(fileSize: 901120, crc: "deadbeef").Should().BeNull();
    }

    [Fact]
    public void ParseLogiqxXml_ShouldExtractHeaderAndGameRom()
    {
        var catalog = _services.DatService.ParseLogiqxXml(SampleLogiqxXml);

        catalog.Header.Name.Should().Be("Nintendo Entertainment System (TOSEC-v2021)");
        catalog.Header.Author.Should().Be("TOSEC Team");
        catalog.Entries.Should().ContainSingle();

        var entry = catalog.Entries[0];
        entry.Name.Should().Be("Super Mario Bros. (1985)(Nintendo)(US).nes");
        entry.Size.Should().Be(40976);
        entry.Crc.Should().Be("d445f698");
        entry.Md5.Should().Be("811b65345a20141f1766a5e12f6890d2");
    }

    [Fact]
    public async Task ParseDatFileAutodetect_ShouldDetectClrMameProAndLogiqxXml()
    {
        using var tmp = new TempDirectoryFixture();
        var cmpFile = tmp.WriteText("amiga.dat", SampleClrMamePro);
        var xmlFile = tmp.WriteText("nes.xml", SampleLogiqxXml);

        var catalogCmp = await _services.DatService.ParseDatFileAsync(cmpFile);
        catalogCmp.Entries.Should().HaveCount(4);

        var catalogXml = await _services.DatService.ParseDatFileAsync(xmlFile);
        catalogXml.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task ComputeFileCrc32Async_ShouldReturnStandardIeeeCrc32()
    {
        using var tmp = new TempDirectoryFixture();
        var testFile = tmp.WriteBytes("test.bin", "123456789"u8.ToArray());

        var crc = await _services.ChecksumService.ComputeFileCrc32Async(testFile);
        crc.ToLowerInvariant().Should().Be("cbf43926");
    }

    [Fact]
    public async Task CrcCache_ShouldPersistReloadAndInvalidateOnFileModification()
    {
        using var tmp = new TempDirectoryFixture();
        var cacheFile = tmp.GetFilePath("crc_cache.json");
        var cache = new CrcCacheService(cacheFile);

        var dummyFile = tmp.WriteBytes("game.bin", "HELLO_ROM_DATA"u8.ToArray());

        cache.Get(dummyFile).Should().BeNull();
        var computed = await _services.ChecksumService.ComputeFileCrc32Async(dummyFile);
        cache.Set(dummyFile, computed);
        cache.Get(dummyFile).Should().Be(computed);

        await cache.SaveAsync();
        File.Exists(cacheFile).Should().BeTrue();

        var newCache = new CrcCacheService(cacheFile);
        newCache.Get(dummyFile).Should().Be(computed);

        // Modify file -> cache must invalidate
        tmp.WriteBytes("game.bin", "MODIFIED_ROM_DATA_DIFFERENT_SIZE"u8.ToArray());
        newCache.Get(dummyFile).Should().BeNull();
    }

    [Fact]
    public void DetectCopierHeader_ShouldIdentifyINesLynxAndSnesHeaders()
    {
        // iNES: starts with "NES\x1a" -> 16 bytes
        var inesBytes = new byte[24];
        "NES\x1a"u8.CopyTo(inesBytes);
        _services.ChecksumService.DetectCopierHeader(inesBytes, 40976).Should().Be(16);

        // Lynx: starts with "LYNX" -> 64 bytes
        var lynxBytes = new byte[74];
        "LYNX"u8.CopyTo(lynxBytes);
        _services.ChecksumService.DetectCopierHeader(lynxBytes, 131136).Should().Be(64);

        // SNES: file_size % 1024 == 512 -> 512 bytes
        var snesHeader = new byte[64];
        _services.ChecksumService.DetectCopierHeader(snesHeader, 1024 * 1024 + 512).Should().Be(512);

        // Normal file without header
        _services.ChecksumService.DetectCopierHeader("NORMAL_DATA_WITHOUT_HEADER"u8, 1024 * 1024).Should().Be(0);
    }

    [Fact]
    public void ZipArchiveMembersInspection_ShouldReadMembersWithoutExtraction()
    {
        using var tmp = new TempDirectoryFixture();
        var zipFile = tmp.GetFilePath("collection.zip");

        _services.ChecksumService.IsZipArchive(zipFile).Should().BeTrue();
        _services.ChecksumService.IsZipArchive(tmp.GetFilePath("game.adf")).Should().BeFalse();

        using (var archive = ZipFile.Open(zipFile, ZipArchiveMode.Create))
        {
            var entry1 = archive.CreateEntry("Game 1 (1990)(Publisher).adf");
            using (var s1 = entry1.Open())
            {
                s1.Write("ROM1_DATA"u8);
            }

            var entry2 = archive.CreateEntry("Game 2 (1991)(Publisher).adf");
            using (var s2 = entry2.Open())
            {
                s2.Write("ROM2_DATA_DIFFERENT"u8);
            }
        }

        var members = _services.ChecksumService.GetZipArchiveMembers(zipFile);
        members.Should().HaveCount(2);
        members[0].MemberName.Should().Be("Game 1 (1990)(Publisher).adf");
        members[0].Size.Should().Be("ROM1_DATA"u8.Length);
        members[0].Crc32.Should().HaveLength(8);
        members[1].MemberName.Should().Be("Game 2 (1991)(Publisher).adf");
    }

    private const string MameStyleClrMamePro = """
        clrmamepro (
        	name "MAME"
        )

        game (
        	name "neogeo"
        	description "Neo-Geo MV-6F"
        	rom ( name "sp-s2.sp1" size 131072 crc 9036d879 )
        )

        game (
        	name "mslug"
        	description "Metal Slug - Super Vehicle-001"
        	romof "neogeo"
        	rom ( name "201-p1.p1" size 2097152 crc 08d8daa5 )
        )

        game (
        	name "sf2"
        	description "Street Fighter II: The World Warrior (World 910522)"
        	rom ( name "sf2e_30g.11e" size 131072 crc fe39ee33 )
        )

        game (
        	name "sf2ua"
        	description "Street Fighter II: The World Warrior (USA 910206)"
        	romof "sf2"
        	cloneof "sf2"
        	rom ( name "sf2u_30a.11e" size 131072 crc 08beb861 )
        )
        """;

    [Fact]
    public void ParseClrMamePro_RomOfABios_ShouldNotMakeTheGameAClone()
    {
        var catalog = _services.DatService.ParseClrMameProText(MameStyleClrMamePro);

        var mslug = catalog.GameByName["mslug"];
        mslug.RomOf.Should().Be("neogeo");
        mslug.CloneOf.Should().BeNull();
        catalog.IsParentGame(mslug).Should().BeTrue();
        catalog.CloneToParent.Should().NotContainKey("mslug");
        catalog.GetCanonicalGameName("mslug").Should().Be("mslug");
    }

    [Fact]
    public void ParseClrMamePro_CloneDeclaredAfterItsRomOf_ShouldKeepItsParent()
    {
        var catalog = _services.DatService.ParseClrMameProText(MameStyleClrMamePro);

        var clone = catalog.GameByName["sf2ua"];
        clone.CloneOf.Should().Be("sf2");
        clone.RomOf.Should().Be("sf2");
        catalog.GetCanonicalGameName("sf2ua").Should().Be("sf2");
    }

    [Fact]
    public void ParseLogiqxXml_RomOf_ShouldBeReadApartFromCloneOf()
    {
        const string xml = """
            <?xml version="1.0"?>
            <datafile>
            	<machine name="neogeo"><description>Neo-Geo MV-6F</description></machine>
            	<machine name="mslug" romof="neogeo"><description>Metal Slug - Super Vehicle-001</description></machine>
            	<machine name="sf2"><description>Street Fighter II: The World Warrior (World 910522)</description></machine>
            	<machine name="sf2ua" cloneof="sf2" romof="sf2"><description>Street Fighter II: The World Warrior (USA 910206)</description></machine>
            </datafile>
            """;

        var catalog = _services.DatService.ParseLogiqxXml(xml);

        catalog.GameByName["mslug"].RomOf.Should().Be("neogeo");
        catalog.GameByName["mslug"].CloneOf.Should().BeNull();
        catalog.GameByName["sf2ua"].CloneOf.Should().Be("sf2");
        catalog.GameByName["sf2ua"].RomOf.Should().Be("sf2");
    }

    [Fact]
    public void ExportedDats_ShouldKeepRomOf()
    {
        var catalog = _services.DatService.ParseClrMameProText(MameStyleClrMamePro);

        var fromXml = _services.DatService.ParseLogiqxXml(catalog.ToLogiqxXml());
        var fromClrMamePro = _services.DatService.ParseClrMameProText(catalog.ToClrMamePro());
        var fixDat = catalog.CreateFixDat(catalog.GameByName["mslug"].Roms);

        foreach (var exported in new[] { fromXml, fromClrMamePro, fixDat })
        {
            exported.GameByName["mslug"].RomOf.Should().Be("neogeo");
            exported.GameByName["mslug"].CloneOf.Should().BeNull();
        }

        fromXml.GameByName["sf2ua"].CloneOf.Should().Be("sf2");
        fromClrMamePro.GameByName["sf2ua"].RomOf.Should().Be("sf2");
    }

    [Fact]
    public void Preview_OfAnXmlDat_ShouldStopAfterItsFirstGamesAndKnowItsConvention()
    {
        var machines = string.Concat(Enumerable.Range(0, 200).Select(i =>
            $"<machine name=\"m{i}\" romof=\"bios\"><description>Machine {i}</description><rom name=\"m{i}.rom\" size=\"4\" crc=\"{i:x8}\"/></machine>"));
        var path = Path.Combine(Path.GetTempPath(), $"romsieve-preview-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, $"<?xml version=\"1.0\"?><mame build=\"0.289\">{machines}</mame>");
        try
        {
            var preview = _services.DatService.ReadDatPreview(path);

            preview.Games.Should().HaveCount(DatService.PreviewGameCount);
            preview.Header.Convention.Should().Be(NamingConvention.Mame);
            preview.Header.Version.Should().Be("0.289");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Preview_OfALargeClrMameProDat_ShouldOnlyHoldWholeGames()
    {
        var games = string.Concat(Enumerable.Range(0, 5000).Select(i =>
            $"game (\n\tname \"Game {i} (1990)(Ocean)\"\n\trom ( name \"Game {i} (1990)(Ocean).adf\" size 901120 crc {i:x8} )\n)\n"));
        var path = Path.Combine(Path.GetTempPath(), $"romsieve-preview-{Guid.NewGuid():N}.dat");
        File.WriteAllText(path, "clrmamepro (\n\tname \"Commodore Amiga - Games (TOSEC)\"\n)\n" + games);
        try
        {
            new FileInfo(path).Length.Should().BeGreaterThan(256 * 1024, "the DAT is longer than its preview");

            var preview = _services.DatService.ReadDatPreview(path);

            preview.Header.Name.Should().Be("Commodore Amiga - Games (TOSEC)");
            preview.Header.Convention.Should().Be(NamingConvention.Tosec);
            preview.Games.Should().NotBeEmpty().And.HaveCountLessThan(5000);
            preview.Games.Should().OnlyContain(g => g.Roms.Count == 1 && g.Roms[0].Size == 901120, "the last game, cut by the preview, is left out");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
