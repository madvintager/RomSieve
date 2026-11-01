using System.IO;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Fixdat export: a standard Logiqx DAT listing only the missing ROMs, grouped by game with their parent / clone
/// relations, which other ROM managers can read.
/// </summary>
public sealed class FixDatExportTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");

    public void Dispose() => _temp.Dispose();

    private const string SourceDat = """
        <?xml version="1.0"?>
        <datafile>
          <header>
            <name>Commodore Amiga - Games (TOSEC)</name>
            <description>TOSEC Amiga</description>
            <version>2025-01-01</version>
            <homepage>TOSEC</homepage>
          </header>
          <game name="Alpha (1990)(Ocean)">
            <description>Alpha</description>
            <rom name="Alpha (1990)(Ocean)(Disk 1 of 2).adf" size="10" crc="00000001" sha1="1111111111111111111111111111111111111111"/>
            <rom name="Alpha (1990)(Ocean)(Disk 2 of 2).adf" size="10" crc="00000002"/>
          </game>
          <game name="Alpha (1990)(Ocean)[a]" cloneof="Alpha (1990)(Ocean)">
            <description>Alpha alternate</description>
            <rom name="Alpha (1990)(Ocean)[a].adf" size="10" crc="00000003"/>
          </game>
          <game name="Beta (1991)(Ocean)">
            <description>Beta</description>
            <rom name="Beta (1991)(Ocean).adf" size="10" crc="00000004"/>
          </game>
        </datafile>
        """;

    private DatCatalog Catalog() => _services.DatService.ParseLogiqxXml(SourceDat);

    private static IReadOnlyList<DatRomEntry> Missing(DatCatalog catalog, params string[] names) =>
        catalog.Entries.Where(e => names.Contains(e.Name)).ToList();

    [Fact]
    public void FixDat_ShouldKeepOnlyTheGamesAndRomsThatAreMissing()
    {
        var catalog = Catalog();

        var fixDat = catalog.CreateFixDat(Missing(catalog, "Alpha (1990)(Ocean)(Disk 2 of 2).adf", "Alpha (1990)(Ocean)[a].adf"));

        fixDat.Games.Select(g => g.Name).Should().Equal("Alpha (1990)(Ocean)", "Alpha (1990)(Ocean)[a]");
        fixDat.Games[0].Roms.Select(r => r.Name).Should().Equal("Alpha (1990)(Ocean)(Disk 2 of 2).adf");
        fixDat.Games[1].CloneOf.Should().Be("Alpha (1990)(Ocean)");
        fixDat.Entries.Should().HaveCount(2);
    }

    [Fact]
    public void FixDat_ShouldBeNamedAfterTheSourceDat()
    {
        var catalog = Catalog();

        var header = catalog.CreateFixDat(Missing(catalog, "Beta (1991)(Ocean).adf")).Header;

        header.Name.Should().Be("fixDat_Commodore Amiga - Games (TOSEC)");
        header.Description.Should().Be("fixDat_TOSEC Amiga");
        header.Version.Should().Be("2025-01-01");
        header.Author.Should().Be("RomSieve");
        header.Homepage.Should().Be("TOSEC");
    }

    [Fact]
    public async Task ExportedFixDat_ShouldBeAReadableLogiqxDatWithTheChecksums()
    {
        var catalog = Catalog();
        var path = _temp.GetFilePath("fixdat.xml");

        await _services.Processor.ExportFixDatAsync(
            Missing(catalog, "Alpha (1990)(Ocean)(Disk 1 of 2).adf", "Beta (1991)(Ocean).adf"), catalog, path);

        var reread = _services.DatService.ParseLogiqxXml(await File.ReadAllTextAsync(path));
        reread.Header.Name.Should().Be("fixDat_Commodore Amiga - Games (TOSEC)");
        reread.Games.Select(g => g.Name).Should().Equal("Alpha (1990)(Ocean)", "Beta (1991)(Ocean)");
        var disk1 = reread.Entries.Single(e => e.Name == "Alpha (1990)(Ocean)(Disk 1 of 2).adf");
        disk1.Size.Should().Be(10);
        disk1.Crc.Should().Be("00000001");
        disk1.Sha1.Should().Be("1111111111111111111111111111111111111111");
    }

    [Fact]
    public async Task FixDat_OfASimulation_ShouldListTheRomsOfTheMissList()
    {
        _temp.WriteText(Path.Combine("src", "Beta (1991)(Ocean).adf"), "BETA");
        var datPath = _temp.WriteText("source.dat", SourceDat.Replace(
            """size="10" crc="00000004" """.TrimEnd(),
            $"""size="4" crc="{await _services.ChecksumService.ComputeFileCrc32Async(_temp.GetFilePath(Path.Combine("src", "Beta (1991)(Ocean).adf")))}" """.TrimEnd()));
        var result = await _services.Processor.ExecuteAsync(new SortConfig
        {
            SourceDir = _temp.GetFilePath("src"),
            DestinationDir = _temp.GetFilePath("dst"),
            DatFile = datPath
        }, isDryRun: true);

        var fixDat = result.DatCatalog!.CreateFixDat(result.MissingRoms);

        result.MissingRoms.Should().HaveCount(3);
        fixDat.Entries.Select(e => e.Name).Should().BeEquivalentTo(result.MissingRoms.Select(r => r.Name));
        fixDat.Games.Should().NotContain(g => g.Name == "Beta (1991)(Ocean)", "a complete game is not in the fixdat");
    }
}
