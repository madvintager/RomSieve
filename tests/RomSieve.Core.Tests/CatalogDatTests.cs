using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Filtered DAT of the catalog (<see cref="SortProcessorService.SelectFromCatalogAsync"/>): the releases chosen on the
/// DAT alone, without source, are those a simulation of a complete collection named after the DAT keeps, for No-Intro
/// and Redump as for MAME (filters, 1G1R, required sets); the filtered DAT holds the games kept, whole, without a
/// relation to a game left out.
/// </summary>
public sealed class CatalogDatTests : IDisposable
{
    private readonly TempDirectoryFixture _tmp = new();
    private readonly CoreTestServices _services = new();

    public void Dispose() => _tmp.Dispose();

    private static string Crc(byte[] data) => Crc32.HashToUInt32(data).ToString("x8");

    private static byte[] Data(string name) => Encoding.UTF8.GetBytes("DATA OF " + name);

    // =========================================================================
    // No-Intro and Redump
    // =========================================================================

    private static readonly string[] NoIntroRoms =
    [
        "Super Game (Europe).sfc",
        "Super Game (USA).sfc",
        "Super Game (Japan).sfc",
        "Other Game (Japan).sfc",
        "Space Quest (USA) (Beta).sfc",
        "Puzzle Time (France).sfc"
    ];

    private static string NoIntroRom(string name, string? cloneOf = null) =>
        $"""
        	<game name="{Path.GetFileNameWithoutExtension(name)}"{(cloneOf is null ? "" : $" cloneof=\"{cloneOf}\"")}>
        		<description>{Path.GetFileNameWithoutExtension(name)}</description>
        		<rom name="{name}" size="{Data(name).Length}" crc="{Crc(Data(name))}"/>
        	</game>
        """;

    private string WriteNoIntroDat() => _tmp.WriteText("snes.xml", $"""
        <?xml version="1.0"?>
        <datafile>
        	<header>
        		<name>Nintendo - Super Nintendo Entertainment System</name>
        		<description>Nintendo - Super Nintendo Entertainment System</description>
        		<version>20261017</version>
        		<homepage>No-Intro</homepage>
        	</header>
        {NoIntroRom(NoIntroRoms[0])}
        {NoIntroRom(NoIntroRoms[1], "Super Game (Europe)")}
        {NoIntroRom(NoIntroRoms[2], "Super Game (Europe)")}
        {NoIntroRom(NoIntroRoms[3])}
        {NoIntroRom(NoIntroRoms[4])}
        {NoIntroRom(NoIntroRoms[5])}
        	<game name="Never Dumped (USA)">
        		<rom name="Never Dumped (USA).sfc" size="1024" status="nodump"/>
        	</game>
        </datafile>
        """);

    private static SortConfig NoIntroConfig(string datPath, string source = "", string destination = "") => new()
    {
        SourceDir = source,
        DestinationDir = destination,
        DatFile = datPath,
        NamingConvention = NamingConvention.NoIntro,
        Filters = new FilterConfig
        {
            OneGameOneRom = true,
            PriorityLanguages = ["en", "fr"],
            PriorityCountries = ["US", "EU"],
            NoIntroExcludeDevVersions = true
        }
    };

    private static HashSet<string> KeptGames(IEnumerable<ProcessItem> items) =>
        items.Where(it => it.Status == FileStatus.Valid).Select(it => it.DatGameName!).ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task NoIntroCatalog_ShouldKeepTheGamesASimulationOfTheCompleteCollectionKeeps()
    {
        var datPath = WriteNoIntroDat();
        var source = _tmp.CreateSubdirectory("src");
        foreach (var name in NoIntroRoms)
        {
            _tmp.WriteBytes(Path.Combine("src", name), Data(name));
        }

        var simulation = await _services.Processor.ExecuteAsync(NoIntroConfig(datPath, source, _tmp.GetFilePath("dst")));
        var catalog = await _services.Processor.SelectFromCatalogAsync(NoIntroConfig(datPath));

        KeptGames(catalog.Items).Should().BeEquivalentTo(KeptGames(simulation.Items));
        catalog.FilteredDat.Games.Select(g => g.Name).Should().BeEquivalentTo(KeptGames(simulation.Items));

        // One version of the game, the beta left out, and a game never dumped has no item
        KeptGames(catalog.Items).Should().ContainSingle(name => name.StartsWith("Super Game", StringComparison.Ordinal));
        KeptGames(catalog.Items).Should().NotContain("Space Quest (USA) (Beta)");
        catalog.Items.Should().NotContain(it => it.DatGameName == "Never Dumped (USA)");
        catalog.CandidateCount.Should().Be(NoIntroRoms.Length);
        catalog.KeptCount.Should().Be(catalog.FilteredDat.Games.Count);
    }

    [Fact]
    public async Task CatalogItems_ShouldStandForTheFilesNamedAfterTheDat()
    {
        var datPath = WriteNoIntroDat();

        var result = await _services.Processor.SelectFromCatalogAsync(NoIntroConfig(datPath));

        var usa = result.Items.Single(it => it.DatGameName == "Super Game (USA)");
        usa.SourcePath.Should().Be("Super Game (USA).sfc");
        usa.TargetFilename.Should().Be("Super Game (USA).sfc");
        usa.Crc32.Should().Be(Crc(Data("Super Game (USA).sfc")));
        usa.Parsed.Convention.Should().Be(NamingConvention.NoIntro);
        usa.Parsed.CloneOf.Should().Be("Super Game (Europe)");
    }

    [Fact]
    public async Task Catalog_ShouldNeedNeitherSourceNorDestination()
    {
        var datPath = WriteNoIntroDat();
        var config = NoIntroConfig(datPath, source: _tmp.GetFilePath("missing-source"), destination: _tmp.GetFilePath("missing-destination"));

        var result = await _services.Processor.SelectFromCatalogAsync(config);

        result.KeptCount.Should().BeGreaterThan(0);
        Directory.Exists(config.SourceDir).Should().BeFalse();
        Directory.Exists(config.DestinationDir).Should().BeFalse();
    }

    [Fact]
    public async Task Catalog_WithoutDat_ShouldBeRefused()
    {
        var act = () => _services.Processor.SelectFromCatalogAsync(new SortConfig { SourceDir = string.Empty, DestinationDir = string.Empty });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(_services.I18n.Tr("log.catalog_requires_dat"));
    }

    [Fact]
    public async Task CatalogDat_ShouldNeverBeWrittenOverTheDatRead()
    {
        var datPath = WriteNoIntroDat();
        var before = File.ReadAllText(datPath);

        // The same file named another way
        var sameFile = Path.Combine(Path.GetDirectoryName(datPath)!, ".", Path.GetFileName(datPath));
        var act = () => _services.Processor.ExportCatalogDatAsync(NoIntroConfig(datPath), sameFile, DatExportFormat.LogiqxXml);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(_services.I18n.Tr("export.overwrites_dat", ("path", sameFile)));
        File.ReadAllText(datPath).Should().Be(before);
    }

    [Fact]
    public async Task Exports_ShouldNeverBeWrittenOverTheDatRead()
    {
        var datPath = WriteNoIntroDat();
        var before = File.ReadAllText(datPath);
        var catalog = await _services.DatService.ParseDatFileAsync(datPath);
        var missing = catalog.Entries.Take(2).ToList();

        Func<Task>[] exports =
        [
            () => _services.Processor.ExportDatAsync([], datPath, DatExportFormat.LogiqxXml, catalog, validOnly: false),
            () => _services.Processor.ExportDatAsync([], datPath, DatExportFormat.ClrMamePro, catalog, validOnly: false),
            () => _services.Processor.ExportFixDatAsync(missing, catalog, datPath),
            () => _services.Processor.ExportMissListToTxtAsync(missing, catalog, datPath),
            () => _services.Processor.ExportMissListToCsvAsync(missing, catalog, datPath)
        ];

        foreach (var export in exports)
        {
            await export.Should().ThrowAsync<InvalidOperationException>();
        }

        File.ReadAllText(datPath).Should().Be(before);
        catalog.IsReadFrom(datPath).Should().BeTrue();
        catalog.IsReadFrom(_tmp.GetFilePath("other.xml")).Should().BeFalse();
        _services.DatService.ParseLogiqxXml(before).IsReadFrom(datPath).Should().BeFalse("a DAT read from text was not read from a file");
    }

    [Fact]
    public async Task FilteredDat_ShouldDropTheRelationToAParentLeftOut()
    {
        var datPath = WriteNoIntroDat();

        var result = await _services.Processor.SelectFromCatalogAsync(NoIntroConfig(datPath));

        var kept = result.FilteredDat.Games.Single(g => g.Name.StartsWith("Super Game", StringComparison.Ordinal));
        kept.Name.Should().Be("Super Game (USA)");
        kept.CloneOf.Should().BeNull();
        result.FilteredDat.CloneToParent.Should().BeEmpty();
        result.FilteredDat.Header.Name.Should().Be("Nintendo - Super Nintendo Entertainment System (RomSieve)");
        result.FilteredDat.Header.Version.Should().Be("20261017");
    }

    [Fact]
    public async Task ExportCatalogDat_ShouldWriteTheFormatOfTheExtension()
    {
        var datPath = WriteNoIntroDat();
        var xmlPath = _tmp.GetFilePath("out/filtered.xml");
        var cmpPath = _tmp.GetFilePath("out/filtered.dat");

        var result = await _services.Processor.ExportCatalogDatAsync(NoIntroConfig(datPath), xmlPath, DatExportFormat.Auto);
        await _services.Processor.ExportCatalogDatAsync(NoIntroConfig(datPath), cmpPath, DatExportFormat.Auto);

        var reread = await _services.DatService.ParseDatFileAsync(xmlPath);
        reread.Games.Select(g => g.Name).Should().Equal(result.FilteredDat.Games.Select(g => g.Name));
        reread.Entries.Select(e => e.Crc).Should().Equal(result.FilteredDat.Entries.Select(e => e.Crc));
        reread.DetectConvention().Should().Be(NamingConvention.NoIntro);
        (await File.ReadAllTextAsync(cmpPath)).Should().StartWith("clrmamepro (");
    }

    [Fact]
    public async Task ExportCatalogDat_KeepingNoGame_ShouldWriteNothing()
    {
        var datPath = _tmp.WriteText("beta.xml", $"""
            <?xml version="1.0"?>
            <datafile>
            	<header><name>Nintendo - Super Nintendo Entertainment System</name><homepage>No-Intro</homepage></header>
            {NoIntroRom(NoIntroRoms[4])}
            </datafile>
            """);
        var config = NoIntroConfig(datPath);
        var outputPath = _tmp.GetFilePath("empty.xml");
        var logs = new List<LogMessage>();

        var result = await _services.Processor.ExportCatalogDatAsync(
            config, outputPath, logProgress: new SynchronousProgress<LogMessage>(logs.Add));

        result.KeptCount.Should().Be(0);
        File.Exists(outputPath).Should().BeFalse();
        logs.Should().Contain(l => l.Level == LogLevel.Warn && l.Message.Key == "log.catalog_nothing_kept");
    }

    [Fact]
    public async Task ExportCatalogDat_Cancelled_ShouldWriteNothing()
    {
        var datPath = WriteNoIntroDat();
        var outputPath = _tmp.GetFilePath("cancelled.xml");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => _services.Processor.ExportCatalogDatAsync(NoIntroConfig(datPath), outputPath, cancellationToken: cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(outputPath).Should().BeFalse();
    }

    /// <summary>
    /// A demo is a release of its own, never a disc of the full game: kept demos lose to it in 1G1R, and excluded ones
    /// (numbered "(Demo 2)" included) are left out by the filter.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Demos_ShouldNeitherJoinTheFullGameNorPassTheirFilter(bool excludeDemos)
    {
        static string Cartridge(string name) => $"""
            	<game name="{name}"><description>{name}</description><rom name="{name}.md" size="{Data(name).Length}" crc="{Crc(Data(name))}"/></game>
            """;
        static string Disc(string name) => $"""
            	<game name="{name}"><description>{name}</description><rom name="{name}.cue" size="{Data(name).Length}" crc="{Crc(Data(name))}"/><rom name="{name}.bin" size="{Data(name + " bin").Length}" crc="{Crc(Data(name + " bin"))}"/></game>
            """;
        var megaDrive = _tmp.WriteText("megadrive.xml", $"""
            <?xml version="1.0"?>
            <datafile>
            	<header><name>Sega - Mega Drive - Genesis</name><homepage>No-Intro</homepage></header>
            {Cartridge("Elite (Europe) (Demo 1) (Game)")}
            {Cartridge("Elite (Europe) (Demo 2) (Soaker)")}
            </datafile>
            """);
        var playStation = _tmp.WriteText("psx.xml", $"""
            <?xml version="1.0"?>
            <datafile>
            	<header><name>Sony - PlayStation</name><url>http://redump.org/</url></header>
            {Disc("Circadia (Japan) (Disc 1)")}
            {Disc("Circadia (Japan) (Disc 2)")}
            {Disc("Circadia (Japan) (Demo 1)")}
            {Disc("Circadia (Japan) (Demo)")}
            </datafile>
            """);

        HashSet<string> KeptOf(CatalogSelectionResult result) => KeptGames(result.Items);
        SortConfig Config(string datPath)
        {
            var config = NoIntroConfig(datPath);
            config.Filters.PriorityLanguages = ["ja", "en"];
            config.Filters.NoIntroExcludeDemos = excludeDemos;
            return config;
        }

        var elite = KeptOf(await _services.Processor.SelectFromCatalogAsync(Config(megaDrive)));
        var circadia = KeptOf(await _services.Processor.SelectFromCatalogAsync(Config(playStation)));

        // The full game, never a demo with it as one more disc
        circadia.Should().BeEquivalentTo(["Circadia (Japan) (Disc 1)", "Circadia (Japan) (Disc 2)"]);
        if (excludeDemos)
        {
            elite.Should().BeEmpty("numbered demos are demos");
        }
        else
        {
            elite.Should().ContainSingle();
        }
    }

    [Fact]
    public async Task RedumpCatalog_ShouldMakeOneItemPerDiscAndKeepItsTracks()
    {
        var datPath = _tmp.WriteText("cd32.xml", """
            <?xml version="1.0"?>
            <datafile>
            	<header>
            		<name>Commodore - Amiga CD32 - Datfile</name>
            		<url>http://redump.org/</url>
            	</header>
            	<game name="Dennis (Europe)">
            		<description>Dennis (Europe)</description>
            		<rom name="Dennis (Europe) (Track 01).bin" size="10000" crc="bbbbbbbb"/>
            		<rom name="Dennis (Europe).cue" size="120" crc="aaaaaaaa"/>
            		<rom name="Dennis (Europe) (Track 02).bin" size="20000" crc="cccccccc"/>
            	</game>
            </datafile>
            """);

        var result = await _services.Processor.SelectFromCatalogAsync(new SortConfig
        {
            SourceDir = string.Empty,
            DestinationDir = string.Empty,
            DatFile = datPath
        });

        var disc = result.Items.Should().ContainSingle().Subject;
        disc.SourcePath.Should().Be("Dennis (Europe).cue");
        disc.RelatedItems.Select(r => r.SourcePath).Should().Equal("Dennis (Europe) (Track 01).bin", "Dennis (Europe) (Track 02).bin");
        disc.Status.Should().Be(FileStatus.Valid);
        result.FilteredDat.Games.Should().ContainSingle().Which.Roms.Should().HaveCount(3);
    }

    // =========================================================================
    // MAME
    // =========================================================================

    private static readonly byte[] Bios = Encoding.ASCII.GetBytes("NEO-GEO BIOS");
    private static readonly byte[] MetalSlug = Encoding.ASCII.GetBytes("METAL SLUG PROGRAM");
    private static readonly byte[] Sf2Program = Encoding.ASCII.GetBytes("SF2 WORLD PROGRAM");
    private static readonly byte[] Sf2Graphics = Encoding.ASCII.GetBytes("SF2 SHARED GRAPHICS");
    private static readonly byte[] Sf2UsaProgram = Encoding.ASCII.GetBytes("SF2 USA PROGRAM");
    private static readonly byte[] QSound = Encoding.ASCII.GetBytes("QSOUND DSP");

    private static string MameRom(string name, byte[] data, string? merge = null) =>
        $"""<rom name="{name}"{(merge is null ? "" : $" merge=\"{merge}\"")} size="{data.Length}" crc="{Crc(data)}"/>""";

    private string WriteMameDat() => _tmp.WriteText("mame.xml", $"""
        <?xml version="1.0"?>
        <mame build="0.289 (test)">
        	<machine name="neogeo" isbios="yes"><description>Neo-Geo MV-6F</description><year>1990</year><manufacturer>SNK</manufacturer>{MameRom("bios.rom", Bios)}<driver status="good"/></machine>
        	<machine name="mslug" romof="neogeo"><description>Metal Slug - Super Vehicle-001</description><year>1996</year><manufacturer>Nazca</manufacturer>{MameRom("bios.rom", Bios, merge: "bios.rom")}{MameRom("mslug.p1", MetalSlug)}<driver status="good"/></machine>
        	<machine name="sf2"><description>Street Fighter II: The World Warrior (World 910522)</description><year>1991</year><manufacturer>Capcom</manufacturer>{MameRom("sf2.a", Sf2Program)}{MameRom("sf2.b", Sf2Graphics)}<device_ref name="qsound"/><driver status="good"/></machine>
        	<machine name="sf2u" cloneof="sf2" romof="sf2"><description>Street Fighter II: The World Warrior (USA 910206)</description><year>1991</year><manufacturer>Capcom</manufacturer>{MameRom("sf2u.a", Sf2UsaProgram)}{MameRom("sf2.b", Sf2Graphics, merge: "sf2.b")}<device_ref name="qsound"/><driver status="good"/></machine>
        	<machine name="qsound" isdevice="yes" runnable="no"><description>Q-Sound</description>{MameRom("qsound.bin", QSound)}</machine>
        	<machine name="ls7474" isdevice="yes" runnable="no"><description>74LS74</description></machine>
        </mame>
        """);

    private SortConfig MameConfig(string datPath, string source = "", string destination = "") => new()
    {
        SourceDir = source,
        DestinationDir = destination,
        DatFile = datPath,
        Pattern = string.Empty,
        Filters = new FilterConfig
        {
            MameExcludeBios = true,
            MameExcludeDevices = true,
            OneGameOneRom = true,
            PriorityCountries = ["US"]
        }
    };

    private void Zip(string name, params (string Member, byte[] Data)[] members)
    {
        using var zip = ZipFile.Open(_tmp.GetFilePath(Path.Combine("src", name)), ZipArchiveMode.Create);
        foreach (var (member, data) in members)
        {
            using var stream = zip.CreateEntry(member).Open();
            stream.Write(data);
        }
    }

    [Fact]
    public async Task MameCatalog_ShouldKeepTheSetsASimulationOfTheCompleteCollectionKeeps()
    {
        var datPath = WriteMameDat();
        var source = _tmp.CreateSubdirectory("src");
        Zip("neogeo.zip", ("bios.rom", Bios));
        Zip("mslug.zip", ("bios.rom", Bios), ("mslug.p1", MetalSlug));
        Zip("sf2.zip", ("sf2.a", Sf2Program), ("sf2.b", Sf2Graphics));
        Zip("sf2u.zip", ("sf2u.a", Sf2UsaProgram), ("sf2.b", Sf2Graphics));
        Zip("qsound.zip", ("qsound.bin", QSound));

        var simulation = await _services.Processor.ExecuteAsync(MameConfig(datPath, source, _tmp.GetFilePath("dst")));
        var catalog = await _services.Processor.SelectFromCatalogAsync(MameConfig(datPath));

        KeptGames(catalog.Items).Should().BeEquivalentTo(KeptGames(simulation.Items));

        // The version of the priority country wins the 1G1R, the device it needs is kept despite its filter, the BIOS
        // whose ROMs the game lists is not
        KeptGames(catalog.Items).Should().BeEquivalentTo(["mslug", "sf2u", "qsound"]);
        catalog.Items.Single(it => it.DatGameName == "qsound").StatusReason!.Key.Should().Be("reason.mame_required_set");

        // A device without ROM has no set
        catalog.Items.Should().NotContain(it => it.DatGameName == "ls7474");
        catalog.Items.Should().OnlyContain(it => it.IsWholeArchive && it.MameSet != null && it.MameSet.IsComplete);
    }

    [Fact]
    public async Task MameCatalog_SetSize_ShouldBeTheSizeOfItsRoms()
    {
        var datPath = WriteMameDat();

        var result = await _services.Processor.SelectFromCatalogAsync(MameConfig(datPath));

        var mslug = result.Items.Single(it => it.DatGameName == "mslug");
        mslug.SourcePath.Should().Be("mslug.zip");
        mslug.FileSize.Should().Be(Bios.Length + MetalSlug.Length);
    }

    [Fact]
    public async Task MameCatalog_WithASizeBudget_ShouldKeepWhatFits()
    {
        var datPath = WriteMameDat();
        var config = MameConfig(datPath);
        config.Filters.MameSizeBudgetBytes = Bios.Length + MetalSlug.Length;

        var result = await _services.Processor.SelectFromCatalogAsync(config);

        KeptGames(result.Items).Should().BeEquivalentTo(["mslug"]);
        result.Items.Single(it => it.DatGameName == "sf2u").IsOutsideBudget.Should().BeTrue();
    }

    [Fact]
    public async Task FilteredMameDat_ShouldKeepTheRelationsBetweenKeptSetsOnly()
    {
        var catalog = await _services.DatService.ParseDatFileAsync(WriteMameDat());

        var alone = catalog.CreateFilteredDat(["sf2u", "mslug", "qsound"]);
        var sf2u = alone.GameByName["sf2u"];
        sf2u.CloneOf.Should().BeNull();
        sf2u.RomOf.Should().BeNull();
        sf2u.Roms.Should().OnlyContain(r => r.Merge == null);
        sf2u.Roms.Should().HaveCount(2);
        sf2u.DeviceRefs.Should().Equal("qsound");
        alone.GameByName["mslug"].RomOf.Should().BeNull();
        alone.GameByName["mslug"].Roms.Should().OnlyContain(r => r.Merge == null);

        var withBios = catalog.CreateFilteredDat(["mslug", "neogeo"]);
        withBios.GameByName["mslug"].RomOf.Should().Be("neogeo");
        withBios.GameByName["mslug"].Roms.Single(r => r.Name == "bios.rom").Merge.Should().Be("bios.rom");

        // The catalog itself is left as it was
        catalog.GameByName["sf2u"].CloneOf.Should().Be("sf2");
        catalog.GameByName["sf2u"].Roms.Single(r => r.Name == "sf2.b").Merge.Should().Be("sf2.b");
    }

    [Fact]
    public async Task ExportedMameDat_ShouldBeReadAsAMameDat()
    {
        var datPath = WriteMameDat();
        var outputPath = _tmp.GetFilePath("mame (RomSieve).xml");

        await _services.Processor.ExportCatalogDatAsync(MameConfig(datPath), outputPath);

        var reread = await _services.DatService.ParseDatFileAsync(outputPath);
        reread.Games.Select(g => g.Name).Should().Equal("mslug", "sf2u", "qsound");
        reread.GameByName["qsound"].IsDevice.Should().BeTrue();
        reread.DetectConvention().Should().Be(NamingConvention.Mame);
    }

    // =========================================================================
    // MAME software lists
    // =========================================================================

    private string WriteSoftwareListDat() => _tmp.WriteText("megadriv.xml", $"""
        <?xml version="1.0"?>
        <datafile>
        	<header><name>megadriv</name><description>Sega Mega Drive/Genesis cartridges</description><author>Pleasuredome</author></header>
        	<machine name="sonic"><description>Sonic The Hedgehog (Europe, USA)</description><year>1991</year><publisher>Sega</publisher><rom name="mpr-13913.ic1" size="{Data("sonic").Length}" crc="{Crc(Data("sonic"))}"/></machine>
        	<machine name="sonicj"><description>Sonic The Hedgehog (Japan, Korea)</description><year>1991</year><publisher>Sega</publisher><rom name="mpr-13933.ic1" size="{Data("sonicj").Length}" crc="{Crc(Data("sonicj"))}"/></machine>
        	<machine name="sonicp"><description>Sonic The Hedgehog (USA, prototype 19910312)</description><year>1991</year><publisher>Sega</publisher><rom name="sonicp.bin" size="{Data("sonicp").Length}" crc="{Crc(Data("sonicp"))}"/></machine>
        	<machine name="gunstar"><description>Gunstar Heroes (Japan)</description><year>1993</year><publisher>Sega</publisher><rom name="gunstar.bin" size="{Data("gunstar").Length}" crc="{Crc(Data("gunstar"))}"/></machine>
        </datafile>
        """);

    [Fact]
    public async Task SoftwareListCatalog_ShouldKeepTheSoftwareASimulationOfTheCompleteCollectionKeeps()
    {
        var datPath = WriteSoftwareListDat();
        var source = _tmp.CreateSubdirectory(Path.Combine("src", "megadriv"));
        foreach (var (software, rom) in new[] { ("sonic", "mpr-13913.ic1"), ("sonicj", "mpr-13933.ic1"), ("sonicp", "sonicp.bin"), ("gunstar", "gunstar.bin") })
        {
            using var zip = ZipFile.Open(Path.Combine(source, software + ".zip"), ZipArchiveMode.Create);
            using var stream = zip.CreateEntry(rom).Open();
            stream.Write(Data(software));
        }

        SortConfig Config(string sourceDir) => new()
        {
            SourceDir = sourceDir,
            DestinationDir = sourceDir.Length == 0 ? string.Empty : _tmp.GetFilePath("dst"),
            DatFile = datPath,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                PriorityLanguages = ["en"],
                PriorityCountries = ["EU", "US"],
                NoIntroExcludeDevVersions = true
            }
        };

        var simulation = await _services.Processor.ExecuteAsync(Config(source));
        var catalog = await _services.Processor.SelectFromCatalogAsync(Config(string.Empty));

        KeptGames(catalog.Items).Should().BeEquivalentTo(KeptGames(simulation.Items));
        catalog.FilteredDat.Games.Select(g => g.Name).Should().BeEquivalentTo(KeptGames(simulation.Items));

        // One Sonic, the prototype left out, and a game without an English release still has its version
        KeptGames(catalog.Items).Should().BeEquivalentTo(["sonic", "gunstar"]);
        catalog.Items.Should().OnlyContain(it => it.Parsed.Convention == NamingConvention.MameSoftwareList);
        catalog.FilteredDat.DetectConvention().Should().Be(NamingConvention.MameSoftwareList);
    }

    /// <summary>Reports on the calling thread, so that the logs are all there when the call returns.</summary>
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
