using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using System.Xml.Linq;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Several MAME software lists sorted together: a folder of DATs (one per list) gathers a catalog per list, the source
/// holds a folder per list, each archive is identified in the list of its folder only (a short name such as
/// <c>sonic</c> is in several lists), goes into the folder of its list, and only needs sets of its list; the completion
/// counts the lists present in the source; the filtered DAT of the catalog, the fixdat and the DAT of the collection
/// are written as a DAT per list into a folder.
/// </summary>
public sealed class SoftwareListFolderTests : IDisposable
{
    private readonly TempDirectoryFixture _tmp = new();
    private readonly CoreTestServices _services = new();
    private readonly string _source;
    private readonly string _destination;
    private readonly string _dats;

    private static readonly byte[] MegadrivSonic = Encoding.ASCII.GetBytes("MEGA DRIVE SONIC");
    private static readonly byte[] GamegearSonic = Encoding.ASCII.GetBytes("GAME GEAR SONIC");
    private static readonly byte[] GamegearShinobi = Encoding.ASCII.GetBytes("GAME GEAR SHINOBI");
    private static readonly byte[] AtariPitfall = Encoding.ASCII.GetBytes("ATARI PITFALL");
    private static readonly byte[] WorldProgram = Encoding.ASCII.GetBytes("WORLD PROGRAM");
    private static readonly byte[] JapanProgram = Encoding.ASCII.GetBytes("JAPAN PROGRAM");
    private static readonly byte[] SharedGraphics = Encoding.ASCII.GetBytes("SHARED GRAPHICS");
    private static readonly byte[] PirateGame = Encoding.ASCII.GetBytes("PIRATE GAME");

    public SoftwareListFolderTests()
    {
        _source = _tmp.CreateSubdirectory("src");
        _destination = _tmp.GetFilePath("dst");
        _dats = _tmp.CreateSubdirectory("dats");
    }

    public void Dispose() => _tmp.Dispose();

    private static string Crc(byte[] data) => Crc32.HashToUInt32(data).ToString("x8");

    private static string Rom(string name, byte[] data) => $"""<rom name="{name}" size="{data.Length}" crc="{Crc(data)}"/>""";

    // A SabreTools DAT of a list, its software (short name, description, ROM name, data)
    private void WriteListDat(string list, params (string Name, string Description, string Rom, byte[] Data)[] software) =>
        _tmp.WriteText(Path.Combine("dats", list + ".xml"), $"""
            <?xml version="1.0"?>
            <datafile>
            	<header><name>{list}</name><description>{list} cartridges</description><version>0.289</version><author>Pleasuredome</author></header>
            {string.Concat(software.Select(s => $"""
            	<machine name="{s.Name}"><description>{s.Description}</description><year>1991</year><publisher>Sega</publisher>{Rom(s.Rom, s.Data)}</machine>

            """))}
            </datafile>
            """);

    private void WriteLists()
    {
        WriteListDat("megadriv", ("sonic", "Sonic The Hedgehog (Europe, USA)", "mpr-13913.ic1", MegadrivSonic));
        WriteListDat("gamegear",
            ("sonic", "Sonic The Hedgehog (Europe, USA)", "mpr-14428.ic1", GamegearSonic),
            ("shinobi", "The GG Shinobi (Europe)", "mpr-14388.ic1", GamegearShinobi));

        // A list of the DATs the source does not hold
        WriteListDat("a2600", ("pitfall", "Pitfall! (USA)", "pitfall.bin", AtariPitfall));
    }

    private void Zip(string relativePath, params (string Member, byte[] Data)[] members)
    {
        var path = Path.Combine(_source, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (member, data) in members)
        {
            using var stream = zip.CreateEntry(member).Open();
            stream.Write(data);
        }
    }

    private SortConfig Config(Action<FilterConfig>? filters = null, string? source = null)
    {
        var config = new SortConfig
        {
            SourceDir = source ?? _source,
            DestinationDir = source is { Length: 0 } ? string.Empty : _destination,
            DatFile = _dats,
            Pattern = string.Empty,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                PriorityLanguages = ["en"],
                PriorityCountries = ["EU", "US"],
                NoIntroExcludePirate = true
            }
        };
        filters?.Invoke(config.Filters);
        return config;
    }

    private static ProcessItem Item(IEnumerable<ProcessItem> items, string relativePath) =>
        items.Single(it => it.SourcePath.EndsWith(relativePath, StringComparison.OrdinalIgnoreCase));

    // =========================================================================
    // Folder of DATs
    // =========================================================================

    [Fact]
    public async Task FolderOfDats_ShouldGatherACatalogPerList()
    {
        WriteLists();

        var catalog = await _services.DatService.ParseDatFileAsync(_dats);

        catalog.Header.Convention.Should().Be(NamingConvention.MameSoftwareList);
        catalog.Header.Name.Should().Be("dats");
        catalog.SoftwareLists!.Keys.Should().Equal("a2600", "gamegear", "megadriv");
        catalog.CatalogFor("megadriv").GameByName["sonic"].Roms.Single().Name.Should().Be("mpr-13913.ic1");
        catalog.CatalogFor("gamegear").GameByName["sonic"].Roms.Single().Name.Should().Be("mpr-14428.ic1");
        catalog.Games.Should().HaveCount(4);
        catalog.Entries.Should().HaveCount(4);

        // Its preview, which the interface reads at once, tells the convention too
        _services.DatService.ReadDatPreview(_dats).Header.Convention.Should().Be(NamingConvention.MameSoftwareList);
    }

    [Fact]
    public async Task FolderOfDats_ShouldBeRefusedWhenItIsNotAFolderOfSoftwareLists()
    {
        var empty = await FluentActions.Invoking(() => _services.DatService.ParseDatFileAsync(_dats))
            .Should().ThrowAsync<DatFolderException>();
        empty.Which.Reason.Key.Should().Be("dat.folder_empty");

        WriteLists();
        _tmp.WriteText(Path.Combine("dats", "megadriv (copy).xml"), File.ReadAllText(Path.Combine(_dats, "megadriv.xml")));
        var duplicate = await FluentActions.Invoking(() => _services.DatService.ParseDatFileAsync(_dats))
            .Should().ThrowAsync<DatFolderException>();
        duplicate.Which.Reason.Key.Should().Be("dat.folder_duplicate_list");
        _services.I18n.Format(duplicate.Which.Reason).Should().Contain("megadriv (copy).xml");

        File.Delete(Path.Combine(_dats, "megadriv (copy).xml"));
        _tmp.WriteText(Path.Combine("dats", "snes.xml"), """
            <?xml version="1.0"?>
            <datafile><header><name>Nintendo - Super Nintendo Entertainment System</name><homepage>No-Intro</homepage></header>
            <game name="Super Game (Europe)"><rom name="Super Game (Europe).sfc" size="4" crc="12345678"/></game></datafile>
            """);
        var other = await FluentActions.Invoking(() => _services.DatService.ParseDatFileAsync(_dats))
            .Should().ThrowAsync<DatFolderException>();
        other.Which.Reason.Key.Should().Be("dat.folder_not_software_list");

        // A run says why, in the language of the interface
        Zip(Path.Combine("megadriv", "sonic.zip"), ("mpr-13913.ic1", MegadrivSonic));
        var run = await FluentActions.Invoking(() => _services.Processor.ExecuteAsync(Config()))
            .Should().ThrowAsync<InvalidOperationException>();
        run.Which.Message.Should().Contain("snes.xml");
    }

    // =========================================================================
    // Sort of several lists
    // =========================================================================

    [Fact]
    public async Task Simulation_ShouldIdentifyEachArchiveInTheListOfItsFolder()
    {
        WriteLists();
        Zip(Path.Combine("megadriv", "sonic.zip"), ("mpr-13913.ic1", MegadrivSonic));
        Zip(Path.Combine("gamegear", "sonic.zip"), ("mpr-14428.ic1", GamegearSonic));
        Zip(Path.Combine("gamegear", "shinobi.zip"), ("mpr-14388.ic1", GamegearShinobi));
        Zip("loose.zip", ("mpr-13913.ic1", MegadrivSonic));
        Zip(Path.Combine("unknown", "sonic.zip"), ("mpr-13913.ic1", MegadrivSonic));

        var log = new List<LogMessage>();
        var result = await _services.Processor.ExecuteAsync(Config(), logProgress: new SynchronousProgress<LogMessage>(log.Add));

        // The same short name and the same title in two lists: two software, two games, each in the folder of its list
        var megadriv = Item(result.Items, Path.Combine("megadriv", "sonic.zip"));
        var gamegear = Item(result.Items, Path.Combine("gamegear", "sonic.zip"));
        megadriv.Status.Should().Be(FileStatus.Valid);
        gamegear.Status.Should().Be(FileStatus.Valid);
        megadriv.MameSet!.IsComplete.Should().BeTrue();
        gamegear.MameSet!.IsComplete.Should().BeTrue();
        megadriv.MameSet.SoftwareList.Should().Be("megadriv");
        gamegear.TargetPath.Should().Be(Path.Combine(_destination, "gamegear", "sonic.zip"));
        megadriv.TargetPath.Should().Be(Path.Combine(_destination, "megadriv", "sonic.zip"));
        gamegear.Parsed.System.Should().Be("gamegear");

        // Outside the folder of a list, no software
        Item(result.Items, "loose.zip").StatusReason!.Key.Should().Be("reason.softlist_outside_list_folder");
        var unknown = Item(result.Items, Path.Combine("unknown", "sonic.zip"));
        unknown.Status.Should().Be(FileStatus.Unmatched);
        unknown.StatusReason!.Key.Should().Be("reason.softlist_unknown_list");
        log.Should().Contain(m => m.Message.Key == "log.softlist_files_outside_lists" && m.Level == LogLevel.Warn);
        log.Should().ContainSingle(m => m.Message.Key == "log.mame_software_identified");

        // The completion counts the lists of the source, not a2600
        result.Stats.DatTotalRoms.Should().Be(3);
        result.Stats.DatMatchedCount.Should().Be(3);
        result.MissingRoms.Should().BeEmpty();
        log.Should().Contain(m => m.Message.Key == "log.mame_software_lists_present");
    }

    [Fact]
    public async Task SourceThatIsTheFolderOfAList_ShouldBeSortedWithTheFolderOfDats()
    {
        WriteLists();
        Zip(Path.Combine("gamegear", "sonic.zip"), ("mpr-14428.ic1", GamegearSonic));

        var result = await _services.Processor.ExecuteAsync(Config(source: Path.Combine(_source, "gamegear")));

        Item(result.Items, "sonic.zip").DatGameName.Should().Be("sonic");
        Item(result.Items, "sonic.zip").MameSet!.SoftwareList.Should().Be("gamegear");
        Item(result.Items, "sonic.zip").MameSet!.IsComplete.Should().BeTrue();
        result.Stats.DatTotalRoms.Should().Be(2, "the two ROMs of gamegear only");
    }

    [Fact]
    public async Task Sort_ShouldCopyEachSoftwareIntoTheFolderOfItsList()
    {
        WriteLists();
        Zip(Path.Combine("megadriv", "sonic.zip"), ("mpr-13913.ic1", MegadrivSonic));
        Zip(Path.Combine("gamegear", "sonic.zip"), ("mpr-14428.ic1", GamegearSonic));

        var result = await _services.Processor.ExecuteAsync(Config(), isDryRun: false);

        result.Stats.ErrorCount.Should().Be(0);
        File.ReadAllBytes(Path.Combine(_destination, "megadriv", "sonic.zip")).Should().Equal(File.ReadAllBytes(Path.Combine(_source, "megadriv", "sonic.zip")));
        File.ReadAllBytes(Path.Combine(_destination, "gamegear", "sonic.zip")).Should().Equal(File.ReadAllBytes(Path.Combine(_source, "gamegear", "sonic.zip")));
    }

    // Two hash files: in megadriv, a split clone and its parent; in gamegear, a pirate of the same short name as the parent
    private void WriteHashLists()
    {
        _tmp.WriteText(Path.Combine("dats", "megadriv.xml"), $"""
            <?xml version="1.0"?>
            <softwarelist name="megadriv" description="Sega Mega Drive/Genesis cartridges">
            	<software name="game"><description>Super Game (World)</description><year>1992</year><publisher>Sega</publisher>
            		<part name="cart" interface="megadriv_cart"><dataarea name="rom" size="64">{Rom("world.u1", WorldProgram)}{Rom("shared.u2", SharedGraphics)}</dataarea></part></software>
            	<software name="gamej" cloneof="game"><description>Super Game - Nihon-ban (Japan)</description><year>1992</year><publisher>Sega</publisher>
            		<part name="cart" interface="megadriv_cart"><dataarea name="rom" size="64">{Rom("japan.u1", JapanProgram)}{Rom("shared.u2", SharedGraphics)}</dataarea></part></software>
            </softwarelist>
            """);
        _tmp.WriteText(Path.Combine("dats", "gamegear.xml"), $"""
            <?xml version="1.0"?>
            <softwarelist name="gamegear" description="Sega Game Gear cartridges">
            	<software name="game"><description>Super Game (pirate)</description><year>1993</year><publisher>&lt;unknown&gt;</publisher>
            		<part name="cart" interface="gamegear_cart"><dataarea name="rom" size="64">{Rom("game.bin", PirateGame)}</dataarea></part></software>
            </softwarelist>
            """);
    }

    [Fact]
    public async Task SetsASoftwareNeeds_ShouldBeThoseOfItsList()
    {
        WriteHashLists();
        Zip(Path.Combine("megadriv", "game.zip"), ("world.u1", WorldProgram), ("shared.u2", SharedGraphics));
        Zip(Path.Combine("megadriv", "gamej.zip"), ("japan.u1", JapanProgram));
        Zip(Path.Combine("gamegear", "game.zip"), ("game.bin", PirateGame));

        var result = await _services.Processor.ExecuteAsync(Config(f =>
        {
            f.PriorityLanguages = ["ja"];
            f.PriorityCountries = ["JP"];
        }));

        // The split clone wins and takes its parent of megadriv with it, never the game of gamegear
        Item(result.Items, Path.Combine("megadriv", "gamej.zip")).Status.Should().Be(FileStatus.Valid);
        var parent = Item(result.Items, Path.Combine("megadriv", "game.zip"));
        parent.Status.Should().Be(FileStatus.Valid);
        parent.StatusReason!.Key.Should().Be("reason.mame_required_set");
        var pirate = Item(result.Items, Path.Combine("gamegear", "game.zip"));
        pirate.Status.Should().Be(FileStatus.Excluded);
        pirate.StatusReason!.Key.Should().Be("reason.pirate_excluded");
    }

    // =========================================================================
    // Catalog and exports
    // =========================================================================

    [Fact]
    public async Task Catalog_ShouldKeepTheSoftwareOfEveryListAndWriteADatPerListAtTheRootOfTheFolder()
    {
        WriteLists();
        var folder = _tmp.GetFilePath("out");

        var result = await _services.Processor.ExportCatalogDatAsync(Config(source: string.Empty), folder);

        result.KeptCount.Should().Be(4);
        result.FilteredDat.SoftwareLists!.Should().HaveCount(3);

        // At the root of the folder chosen, where a ROM manager finds a DAT per list
        Directory.GetDirectories(folder).Should().BeEmpty();
        Directory.GetFiles(folder).Select(Path.GetFileName).Should().BeEquivalentTo(["a2600.xml", "gamegear.xml", "megadriv.xml"]);

        // Each DAT is that of its list, which a ROM manager reads as before, and which reads back as a folder of lists
        var gamegear = await _services.DatService.ParseDatFileAsync(Path.Combine(folder, "gamegear.xml"));
        gamegear.Games.Select(g => g.Name).Should().BeEquivalentTo(["sonic", "shinobi"]);
        // A ROM manager names the folder of a DAT after its header (RomVault), and MAME the folder of a list after the list
        gamegear.Header.Name.Should().Be("gamegear");
        gamegear.Header.Description.Should().Be("gamegear cartridges");
        var reread = await _services.DatService.ParseDatFileAsync(folder);
        reread.SoftwareLists!.Should().HaveCount(3);
    }

    [Fact]
    public async Task Catalog_ShouldKeepWhatASimulationOfTheCompleteCollectionKeeps()
    {
        WriteLists();
        Zip(Path.Combine("megadriv", "sonic.zip"), ("mpr-13913.ic1", MegadrivSonic));
        Zip(Path.Combine("gamegear", "sonic.zip"), ("mpr-14428.ic1", GamegearSonic));
        Zip(Path.Combine("gamegear", "shinobi.zip"), ("mpr-14388.ic1", GamegearShinobi));
        Zip(Path.Combine("a2600", "pitfall.zip"), ("pitfall.bin", AtariPitfall));

        var simulation = await _services.Processor.ExecuteAsync(Config());
        var catalog = await _services.Processor.SelectFromCatalogAsync(Config(source: string.Empty));

        static HashSet<string> Kept(IEnumerable<ProcessItem> items) => items
            .Where(it => it.Status == FileStatus.Valid)
            .Select(it => it.MameSet!.SetKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Kept(catalog.Items).Should().BeEquivalentTo(Kept(simulation.Items));
        Kept(catalog.Items).Should().BeEquivalentTo(["megadriv/sonic", "gamegear/sonic", "gamegear/shinobi", "a2600/pitfall"]);
    }

    [Fact]
    public async Task MissListAndFixDat_ShouldNameTheListOfEachMissingSoftware()
    {
        WriteLists();
        Zip(Path.Combine("gamegear", "sonic.zip"), ("mpr-14428.ic1", GamegearSonic));
        Zip(Path.Combine("megadriv", "sonic.zip"), ("mpr-13913.ic1", MegadrivSonic));
        var result = await _services.Processor.ExecuteAsync(Config());
        result.MissingRoms.Should().ContainSingle().Which.GameName.Should().Be("shinobi");

        var csv = _tmp.GetFilePath("miss.csv");
        await _services.Processor.ExportMissListToCsvAsync(result.MissingRoms, result.DatCatalog!, csv);
        File.ReadAllText(csv).Should().Contain("gamegear/shinobi;mpr-14388.ic1");

        // A fixdat per list that lacks something, named so that it never takes the place of the DAT of its list
        var folder = _tmp.GetFilePath("fixdats");
        await _services.Processor.ExportFixDatAsync(result.MissingRoms, result.DatCatalog!, folder);
        Directory.GetFiles(folder).Select(Path.GetFileName).Should().Equal("fixDat_gamegear.xml");
        var gamegear = await _services.DatService.ParseDatFileAsync(Path.Combine(folder, "fixDat_gamegear.xml"));
        gamegear.Header.Name.Should().Be("fixDat_gamegear");
        gamegear.Games.Should().ContainSingle().Which.Name.Should().Be("shinobi");
    }

    [Fact]
    public async Task DatExport_ShouldWriteTheKeptSoftwareOfEachListInItsDat()
    {
        WriteLists();
        Zip(Path.Combine("megadriv", "sonic.zip"), ("mpr-13913.ic1", MegadrivSonic));
        Zip(Path.Combine("gamegear", "sonic.zip"), ("mpr-14428.ic1", GamegearSonic));
        var result = await _services.Processor.ExecuteAsync(Config());
        var folder = _tmp.GetFilePath("collection");

        // Logiqx XML whatever the format asked for: CLRMAMEPRO would lose what MAME and a ROM manager read
        var exported = await _services.Processor.ExportDatAsync(result.Items, folder, DatExportFormat.ClrMamePro, result.DatCatalog);

        exported.SoftwareLists!.Should().HaveCount(2);
        Directory.GetFiles(folder).Select(Path.GetFileName).Should().BeEquivalentTo(["gamegear.xml", "megadriv.xml"]);
        File.ReadAllText(Path.Combine(folder, "megadriv.xml")).Should().Contain("<datafile");
        var megadriv = await _services.DatService.ParseDatFileAsync(Path.Combine(folder, "megadriv.xml"));
        megadriv.Games.Should().ContainSingle().Which.Roms.Single().Name.Should().Be("mpr-13913.ic1");
        megadriv.Header.Name.Should().Be("megadriv");
    }

    [Fact]
    public async Task DatExport_OfASingleList_ShouldBeWrittenIntoTheFolderNamedAsTheList()
    {
        WriteLists();
        Zip("sonic.zip", ("mpr-14428.ic1", GamegearSonic));
        var config = Config(source: Path.Combine(_source));
        config.DatFile = Path.Combine(_dats, "gamegear.xml");
        var result = await _services.Processor.ExecuteAsync(config);
        var folder = _tmp.GetFilePath("collection");

        await _services.Processor.ExportDatAsync(result.Items, folder, DatExportFormat.LogiqxXml, result.DatCatalog);

        Directory.GetFiles(folder).Select(Path.GetFileName).Should().Equal("gamegear.xml");
        var gamegear = await _services.DatService.ParseDatFileAsync(Path.Combine(folder, "gamegear.xml"));
        gamegear.Header.Name.Should().Be("gamegear");
        gamegear.Games.Should().ContainSingle().Which.Name.Should().Be("sonic");
    }

    // =========================================================================
    // Lists written in MAME's format
    // =========================================================================

    // The hash folder of MAME: gamegear holds a pirate parent, excluded, and its clone, kept
    private void WriteHashFiles()
    {
        _tmp.WriteText(Path.Combine("dats", "megadriv.xml"), $"""
            <?xml version="1.0"?>
            <!DOCTYPE softwarelist SYSTEM "softwarelist.dtd">
            <softwarelist name="megadriv" description="Sega Mega Drive/Genesis cartridges">
            	<!-- A comment of MAME -->
            	<software name="sonic">
            		<description>Sonic The Hedgehog (Europe, USA)</description>
            		<year>1991</year>
            		<publisher>Sega</publisher>
            		<info name="serial" value="1009"/>
            		<part name="cart" interface="megadriv_cart">
            			<feature name="pcb" value="171-5703"/>
            			<dataarea name="rom" width="16" endianness="big" size="524288">
            				<rom name="mpr-13913.ic1" size="{MegadrivSonic.Length}" crc="{Crc(MegadrivSonic)}" offset="0x000000" loadflag="load16_word_swap"/>
            			</dataarea>
            		</part>
            	</software>
            </softwarelist>
            """);
        _tmp.WriteText(Path.Combine("dats", "gamegear.xml"), $"""
            <?xml version="1.0"?>
            <!DOCTYPE softwarelist SYSTEM "softwarelist.dtd">
            <softwarelist name="gamegear" description="Sega Game Gear cartridges">
            	<software name="piracy">
            		<description>Pirate Game (pirate)</description>
            		<year>1993</year>
            		<publisher>&lt;unknown&gt;</publisher>
            		<part name="cart" interface="gamegear_cart"><dataarea name="rom" size="262144">{Rom("pirate.ic1", PirateGame)}</dataarea></part>
            	</software>
            	<software name="shinobi" cloneof="piracy">
            		<description>The GG Shinobi (Europe)</description>
            		<year>1991</year>
            		<publisher>Sega</publisher>
            		<part name="cart" interface="gamegear_cart"><dataarea name="rom" size="262144">{Rom("mpr-14388.ic1", GamegearShinobi)}</dataarea></part>
            	</software>
            </softwarelist>
            """);
    }

    [Fact]
    public async Task Catalog_OfHashFiles_ShouldBeWrittenAsMameSoftwareLists()
    {
        WriteHashFiles();
        var folder = _tmp.GetFilePath(Path.Combine("out", "hash"));

        var result = await _services.Processor.ExportCatalogDatAsync(Config(source: string.Empty), folder);

        result.KeptCount.Should().Be(2);
        Directory.GetFiles(folder).Select(Path.GetFileName).Should().BeEquivalentTo(["gamegear.xml", "megadriv.xml"]);

        // The list of MAME, named as MAME names it, each kept software copied with what MAME needs to load it
        var megadriv = XDocument.Load(Path.Combine(folder, "megadriv.xml"));
        megadriv.DocumentType!.SystemId.Should().Be("softwarelist.dtd");
        megadriv.Root!.Name.LocalName.Should().Be("softwarelist");
        megadriv.Root.Attribute("name")!.Value.Should().Be("megadriv");
        megadriv.Root.Attribute("description")!.Value.Should().Be("Sega Mega Drive/Genesis cartridges");
        var sonic = megadriv.Root.Elements("software").Should().ContainSingle().Subject;
        sonic.Element("info")!.Attribute("value")!.Value.Should().Be("1009");
        var part = sonic.Element("part")!;
        part.Attribute("interface")!.Value.Should().Be("megadriv_cart");
        part.Element("feature")!.Attribute("value")!.Value.Should().Be("171-5703");
        var dataArea = part.Element("dataarea")!;
        dataArea.Attribute("width")!.Value.Should().Be("16");
        dataArea.Element("rom")!.Attribute("loadflag")!.Value.Should().Be("load16_word_swap");

        // The pirate parent is left out, and its clone stands alone
        var gamegear = XDocument.Load(Path.Combine(folder, "gamegear.xml"));
        var shinobi = gamegear.Root!.Elements("software").Should().ContainSingle().Subject;
        shinobi.Attribute("name")!.Value.Should().Be("shinobi");
        shinobi.Attribute("cloneof").Should().BeNull();

        // RomSieve reads the folder back as the lists it was written from
        var reread = await _services.DatService.ParseDatFileAsync(folder);
        reread.SoftwareLists!.Keys.Should().BeEquivalentTo(["gamegear", "megadriv"]);
        reread.SoftwareLists["megadriv"].Games.Single().Roms.Single().Name.Should().Be("mpr-13913.ic1");
    }

    [Fact]
    public async Task Catalog_OfHashFiles_ShouldBeWrittenAsMameSoftwareListsWhateverTheFormatAskedFor()
    {
        WriteHashFiles();
        var folder = _tmp.GetFilePath(Path.Combine("out", "hash"));

        await _services.Processor.ExportCatalogDatAsync(Config(source: string.Empty), folder, DatExportFormat.ClrMamePro);

        Directory.GetFiles(folder).Select(Path.GetFileName).Should().BeEquivalentTo(["gamegear.xml", "megadriv.xml"]);
        XDocument.Load(Path.Combine(folder, "megadriv.xml")).Root!.Name.LocalName.Should().Be("softwarelist");
    }

    [Fact]
    public async Task Catalog_OfHashFiles_ShouldNeverBeWrittenOverTheHashFolder()
    {
        WriteHashFiles();
        var before = Directory.GetFiles(_dats).ToDictionary(f => f, File.ReadAllText);

        // The folder of the hash files, whose files the DATs would replace
        var act = () => _services.Processor.ExportCatalogDatAsync(Config(source: string.Empty), _dats);

        await act.Should().ThrowAsync<InvalidOperationException>();
        Directory.GetFiles(_dats).ToDictionary(f => f, File.ReadAllText).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task DatExport_OfHashFiles_ShouldWriteTheKeptSoftwareAsMameSoftwareLists()
    {
        WriteHashFiles();
        Zip(Path.Combine("megadriv", "sonic.zip"), ("mpr-13913.ic1", MegadrivSonic));
        Zip(Path.Combine("gamegear", "shinobi.zip"), ("mpr-14388.ic1", GamegearShinobi));
        var result = await _services.Processor.ExecuteAsync(Config());
        var folder = _tmp.GetFilePath("collection");

        await _services.Processor.ExportDatAsync(result.Items, folder, DatExportFormat.LogiqxXml, result.DatCatalog);

        var gamegear = XDocument.Load(Path.Combine(folder, "gamegear.xml"));
        gamegear.Root!.Name.LocalName.Should().Be("softwarelist");
        gamegear.Root.Elements("software").Select(e => e.Attribute("name")!.Value).Should().Equal("shinobi");
    }

    [Fact]
    public async Task Catalog_OfASingleHashFile_ShouldBeWrittenAsItsMameSoftwareList()
    {
        WriteHashFiles();
        var config = Config(source: string.Empty);
        config.DatFile = Path.Combine(_dats, "gamegear.xml");
        var folder = _tmp.GetFilePath("out");

        await _services.Processor.ExportCatalogDatAsync(config, folder);

        var gamegear = XDocument.Load(Path.Combine(folder, "gamegear.xml"));
        gamegear.Root!.Attribute("name")!.Value.Should().Be("gamegear");
        gamegear.Root.Elements("software").Select(e => e.Attribute("name")!.Value).Should().Equal("shinobi");
    }

    [Fact]
    public async Task DatExport_IntoAFolderOfDats_ShouldReplaceThoseOfItsListsAndTellTheOthers()
    {
        WriteLists();
        var folder = _tmp.GetFilePath("romvault");
        _tmp.WriteText(Path.Combine("romvault", "megadriv.xml"), "<datafile/>");
        _tmp.WriteText(Path.Combine("romvault", "nes.xml"), "<datafile/>");
        _tmp.WriteText(Path.Combine("romvault", "notes.txt"), "not a DAT");
        var old = DateTime.UtcNow.AddHours(-1);
        foreach (var file in Directory.GetFiles(folder))
        {
            File.SetLastWriteTimeUtc(file, old);
        }

        var before = DatService.GetDatFiles(folder);
        before.Keys.Select(Path.GetFileName).Should().BeEquivalentTo(["megadriv.xml", "nes.xml"]);

        await _services.Processor.ExportCatalogDatAsync(Config(source: string.Empty), folder);

        // The DAT of megadriv is replaced, that of nes stays, and a ROM manager still reads it
        File.ReadAllText(Path.Combine(folder, "megadriv.xml")).Should().Contain("sonic");
        DatService.GetUntouchedDatFiles(before).Should().Equal("nes.xml");
        DatService.GetDatFiles(_tmp.GetFilePath("missing")).Should().BeEmpty();
    }

    /// <summary>Reports on the calling thread, so that the logs are all there when the call returns.</summary>
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
