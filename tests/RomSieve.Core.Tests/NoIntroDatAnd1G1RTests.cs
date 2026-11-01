using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Integration tests for No-Intro &amp; Redump DATs, Parent/Clone resolution,
/// 1G1R deduplication, and transfer of multi-file optical tracks (<c>.cue</c> + <c>.bin</c>).
/// </summary>
public sealed class NoIntroDatAnd1G1RTests
{
    private readonly CoreTestServices _services = new();

    [Fact]
    public async Task NoIntroDatLoadingAndParentCloneResolution_ShouldResolveCloneOfIdAndDetectConvention()
    {
        using var tmp = new TempDirectoryFixture();
        var datXml = """
            <?xml version="1.0"?>
            <datafile>
            	<header>
            		<name>Nintendo - Super Nintendo Entertainment System (20260923-014702)</name>
            		<homepage>No-Intro</homepage>
            	</header>
            	<game name="2020 Super Baseball (Japan)" id="0002" cloneofid="0003">
            		<rom name="2020 Super Baseball (Japan).sfc" size="1048576" crc="11111111"/>
            	</game>
            	<game name="Super Baseball 2020 (USA)" id="0003">
            		<rom name="Super Baseball 2020 (USA).sfc" size="1048576" crc="22222222"/>
            	</game>
            </datafile>
            """;
        var datPath = tmp.WriteText("snes.dat", datXml);

        var catalog = await _services.DatService.ParseDatFileAsync(datPath);
        catalog.Header.Should().NotBeNull();
        catalog.DetectConvention().Should().Be(NamingConvention.NoIntro);
        catalog.Games.Should().HaveCount(2);
        catalog.CloneToParent.Should().NotBeEmpty();

        var canonical = catalog.GetCanonicalGameName("2020 Super Baseball (Japan)");
        canonical.Should().Be("Super Baseball 2020 (USA)");
    }

    [Fact]
    public async Task RedumpDatLoading_ShouldDetectRedumpConventionAndMultiTrackGames()
    {
        using var tmp = new TempDirectoryFixture();
        var datXml = """
            <?xml version="1.0"?>
            <datafile>
            	<header>
            		<name>Commodore - Amiga CD32 - Datfile</name>
            		<url>http://redump.org/</url>
            	</header>
            	<game name="Dennis (Europe)">
            		<rom name="Dennis (Europe).cue" size="120" crc="aaaaaaaa"/>
            		<rom name="Dennis (Europe) (Track 01).bin" size="10000" crc="bbbbbbbb"/>
            		<rom name="Dennis (Europe) (Track 02).bin" size="20000" crc="cccccccc"/>
            	</game>
            </datafile>
            """;
        var datPath = tmp.WriteText("cd32.dat", datXml);

        var catalog = await _services.DatService.ParseDatFileAsync(datPath);
        catalog.DetectConvention().Should().Be(NamingConvention.Redump);
        catalog.Games.Should().ContainSingle();
        catalog.Games[0].Roms.Should().HaveCount(3);
    }

    [Fact]
    public async Task NoIntro1G1RParentCloneDeduplication_ShouldDeduplicateParentAndCloneWithDifferentRegionalNames()
    {
        using var tmp = new TempDirectoryFixture();
        var srcDir = tmp.CreateSubdirectory("src");
        var destDir = tmp.CreateSubdirectory("dest");

        var jpBytes = "SNES-JP-DATA"u8.ToArray();
        var jpRom = tmp.WriteBytes("src/2020 Super Baseball (Japan).sfc", jpBytes);
        var crcJp = await _services.ChecksumService.ComputeFileCrc32Async(jpRom);

        var usBytes = "SNES-US-DATA"u8.ToArray();
        var usRom = tmp.WriteBytes("src/Super Baseball 2020 (USA).sfc", usBytes);
        var crcUs = await _services.ChecksumService.ComputeFileCrc32Async(usRom);

        var datPath = tmp.WriteText("nointro_test.dat", $"""
            <?xml version="1.0"?>
            <datafile>
            	<header>
            		<name>Nintendo - Super Nintendo Entertainment System</name>
            		<homepage>No-Intro</homepage>
            	</header>
            	<game name="2020 Super Baseball (Japan)" id="0002" cloneofid="0003">
            		<rom name="2020 Super Baseball (Japan).sfc" size="{jpBytes.Length}" crc="{crcJp}"/>
            	</game>
            	<game name="Super Baseball 2020 (USA)" id="0003">
            		<rom name="Super Baseball 2020 (USA).sfc" size="{usBytes.Length}" crc="{crcUs}"/>
            	</game>
            </datafile>
            """);

        // 1. Test with USA / English priority first
        var configUsa = new SortConfig
        {
            SourceDir = srcDir,
            DestinationDir = destDir,
            NamingConvention = NamingConvention.NoIntro,
            DatFile = datPath,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                PriorityLanguages = ["en", "ja"],
                ExcludeModifications = false
            }
        };
        var resUsa = await _services.Processor.ExecuteAsync(configUsa, isDryRun: true);
        var validUsa = resUsa.Items.Where(it => it.Status == FileStatus.Valid).ToList();
        var excludedUsa = resUsa.Items.Where(it => it.Status == FileStatus.Excluded).ToList();

        validUsa.Should().ContainSingle();
        excludedUsa.Should().ContainSingle();
        Path.GetFileName(validUsa[0].SourcePath).Should().Be("Super Baseball 2020 (USA).sfc");
        Path.GetFileName(excludedUsa[0].SourcePath).Should().Be("2020 Super Baseball (Japan).sfc");

        // 2. Test with Japanese priority first
        var configJp = new SortConfig
        {
            SourceDir = srcDir,
            DestinationDir = destDir,
            NamingConvention = NamingConvention.NoIntro,
            DatFile = datPath,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                PriorityLanguages = ["ja", "en"],
                ExcludeModifications = false
            }
        };
        var resJp = await _services.Processor.ExecuteAsync(configJp, isDryRun: true);
        var validJp = resJp.Items.Where(it => it.Status == FileStatus.Valid).ToList();
        var excludedJp = resJp.Items.Where(it => it.Status == FileStatus.Excluded).ToList();

        validJp.Should().ContainSingle();
        excludedJp.Should().ContainSingle();
        Path.GetFileName(validJp[0].SourcePath).Should().Be("2020 Super Baseball (Japan).sfc");
        Path.GetFileName(excludedJp[0].SourcePath).Should().Be("Super Baseball 2020 (USA).sfc");
    }

    [Fact]
    public async Task NoIntroRealSortingAndOrganization_ShouldCreateCountryAndRestoredTitleFolders()
    {
        using var tmp = new TempDirectoryFixture();
        var srcDir = tmp.CreateSubdirectory("src");
        var destDir = tmp.CreateSubdirectory("dest");

        tmp.WriteBytes("src/Super Mario World (USA).sfc", "MARIO-USA"u8.ToArray());
        tmp.WriteBytes("src/Super Mario World (Europe).sfc", "MARIO-EU"u8.ToArray());
        tmp.WriteBytes("src/Legend of Zelda, The - A Link to the Past (USA).sfc", "ZELDA-USA"u8.ToArray());

        var config = new SortConfig
        {
            SourceDir = srcDir,
            DestinationDir = destDir,
            NamingConvention = NamingConvention.Auto,
            Pattern = "{country}/{title}/",
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                PriorityLanguages = ["en"]
            }
        };

        var res = await _services.Processor.ExecuteAsync(config, isDryRun: false);
        var validItems = res.Items.Where(it => it.Status == FileStatus.Valid).ToList();
        validItems.Should().HaveCount(2);

        var zeldaDest = Path.Combine(destDir, "US", "The Legend of Zelda - A Link to the Past", "Legend of Zelda, The - A Link to the Past (USA).sfc");
        File.Exists(zeldaDest).Should().BeTrue();

        var marioDest = Path.Combine(destDir, "US", "Super Mario World", "Super Mario World (USA).sfc");
        File.Exists(marioDest).Should().BeTrue();
    }

    [Fact]
    public async Task RedumpMultitrackCueBinTransfer_ShouldTransferAllRelatedBinTracksWithMainCue()
    {
        using var tmp = new TempDirectoryFixture();
        var srcDir = tmp.CreateSubdirectory("src");
        var destDir = tmp.CreateSubdirectory("dest");

        var cueFile = tmp.WriteText("src/Dennis (Europe).cue", "FILE Dennis (Europe) (Track 01).bin BINARY\nFILE Dennis (Europe) (Track 02).bin BINARY\n");
        var track1 = tmp.WriteBytes("src/Dennis (Europe) (Track 01).bin", "TRACK01-DATA"u8.ToArray());
        var track2 = tmp.WriteBytes("src/Dennis (Europe) (Track 02).bin", "TRACK02-AUDIO"u8.ToArray());

        var config = new SortConfig
        {
            SourceDir = srcDir,
            DestinationDir = destDir,
            NamingConvention = NamingConvention.Redump,
            Pattern = "{title}/"
        };

        var cueItem = new ProcessItem
        {
            SourcePath = cueFile,
            FileSize = new FileInfo(cueFile).Length,
            Status = FileStatus.Valid,
            Parsed = _services.NoIntroParser.ParseFilename(Path.GetFileName(cueFile), isRedump: true),
            RelatedItems =
            [
                new ProcessItem
                {
                    SourcePath = track1,
                    FileSize = new FileInfo(track1).Length,
                    Status = FileStatus.Valid,
                    Parsed = _services.NoIntroParser.ParseFilename(Path.GetFileName(track1), isRedump: true)
                },
                new ProcessItem
                {
                    SourcePath = track2,
                    FileSize = new FileInfo(track2).Length,
                    Status = FileStatus.Valid,
                    Parsed = _services.NoIntroParser.ParseFilename(Path.GetFileName(track2), isRedump: true)
                }
            ]
        };

        var executor = new FileTransferExecutor(config, isDryRun: false, _services.Organizer, _services.FilterService, _services.I18n,
            _services.OpticalCompanionService, _services.ChecksumService);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stats = new ProcessStats();

        await executor.ProcessItemAsync(cueItem, claimed, stats);

        File.Exists(Path.Combine(destDir, "Dennis", "Dennis (Europe).cue")).Should().BeTrue();
        File.Exists(Path.Combine(destDir, "Dennis", "Dennis (Europe) (Track 01).bin")).Should().BeTrue();
        File.Exists(Path.Combine(destDir, "Dennis", "Dennis (Europe) (Track 02).bin")).Should().BeTrue();
    }

    [Fact]
    public async Task NoIntro1G1REuropePresetSelection_ShouldSelectEuropeOverUsaWhenPalPreferred()
    {
        using var tmp = new TempDirectoryFixture();
        var srcDir = tmp.CreateSubdirectory("src");
        var destDir = tmp.CreateSubdirectory("dest");

        tmp.WriteBytes("src/Aaahh!!! Real Monsters (Europe).sfc", "EU-ROM"u8.ToArray());
        tmp.WriteBytes("src/Aaahh!!! Real Monsters (USA).sfc", "US-ROM"u8.ToArray());

        var config = new SortConfig
        {
            SourceDir = srcDir,
            DestinationDir = destDir,
            NamingConvention = NamingConvention.NoIntro,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                PriorityLanguages = ["m", "fr", "en", "de", "es", "it"],
                PreferredVideo = VideoPreference.Pal,
                PreferMonolingual = false
            }
        };

        var res = await _services.Processor.ExecuteAsync(config, isDryRun: true);
        var euItem = res.Items.Single(it => it.SourcePath.Contains("Europe"));
        var usItem = res.Items.Single(it => it.SourcePath.Contains("USA"));

        euItem.Status.Should().Be(FileStatus.Valid);
        euItem.Is1G1RWinner.Should().BeTrue();

        usItem.Status.Should().Be(FileStatus.Excluded);
        usItem.Is1G1RWinner.Should().BeFalse();
        _services.I18n.Format(usItem.StatusReason).Should().Contain("PAL");
    }

    [Fact]
    public async Task NoIntro1G1RRegionAffinity_UsaShouldBeatJapanEnWhileExclusiveJapanEnRemainsValid()
    {
        using var tmp = new TempDirectoryFixture();
        var srcDir = tmp.CreateSubdirectory("src");
        var destDir = tmp.CreateSubdirectory("dest");

        var jpBytes = "BALLZ-JP-EN-DATA"u8.ToArray();
        var jpRom = tmp.WriteBytes("src/3-jigen Kakutou Ballz (Japan).sfc", jpBytes);
        var crcJp = await _services.ChecksumService.ComputeFileCrc32Async(jpRom);

        var usBytes = "BALLZ-US-DATA"u8.ToArray();
        var usRom = tmp.WriteBytes("src/Ballz 3D - Fighting at Its Ballziest (USA).sfc", usBytes);
        var crcUs = await _services.ChecksumService.ComputeFileCrc32Async(usRom);

        var exclBytes = "ACROBAT-JP-EN-DATA"u8.ToArray();
        var exclRom = tmp.WriteBytes("src/Acrobat Mission (Japan).sfc", exclBytes);
        var crcExcl = await _services.ChecksumService.ComputeFileCrc32Async(exclRom);

        var datPath = tmp.WriteText("snes_ballz.dat", $"""
            <?xml version="1.0"?>
            <datafile>
            	<header>
            		<name>Nintendo - Super Nintendo Entertainment System</name>
            		<homepage>No-Intro</homepage>
            	</header>
            	<game name="3-jigen Kakutou Ballz (Japan) (En)" id="0007" cloneofid="0157">
            		<rom name="3-jigen Kakutou Ballz (Japan) (En).sfc" size="{jpBytes.Length}" crc="{crcJp}"/>
            	</game>
            	<game name="Ballz 3D - Fighting at Its Ballziest (USA)" id="0157">
            		<rom name="Ballz 3D - Fighting at Its Ballziest (USA).sfc" size="{usBytes.Length}" crc="{crcUs}"/>
            	</game>
            	<game name="Acrobat Mission (Japan) (En)" id="0024">
            		<rom name="Acrobat Mission (Japan) (En).sfc" size="{exclBytes.Length}" crc="{crcExcl}"/>
            	</game>
            </datafile>
            """);

        var config = new SortConfig
        {
            SourceDir = srcDir,
            DestinationDir = destDir,
            NamingConvention = NamingConvention.NoIntro,
            DatFile = datPath,
            Filters = new FilterConfig
            {
                FilterLanguages = true,
                OneGameOneRom = true,
                PriorityLanguages = ["fr", "en"],
                FallbackUnmatchedLanguages = false,
                PreferredVideo = VideoPreference.Pal
            }
        };

        var res = await _services.Processor.ExecuteAsync(config, isDryRun: true);
        var usItem = res.Items.Single(it => it.SourcePath.Contains("Ballz 3D"));
        var jpItem = res.Items.Single(it => it.SourcePath.Contains("3-jigen"));
        var exclItem = res.Items.Single(it => it.SourcePath.Contains("Acrobat Mission"));

        usItem.Status.Should().Be(FileStatus.Valid);
        usItem.Is1G1RWinner.Should().BeTrue();

        jpItem.Status.Should().Be(FileStatus.Excluded);
        jpItem.Is1G1RWinner.Should().BeFalse();
        _services.I18n.Format(jpItem.StatusReason).Should().Contain("US");

        exclItem.Status.Should().Be(FileStatus.Valid);
        exclItem.Is1G1RWinner.Should().BeTrue();
    }

    [Fact]
    public async Task NoIntro1G1RParentTiebreaker_ParentShouldBeatCloneWhenAllOtherCriteriaTie()
    {
        using var tmp = new TempDirectoryFixture();
        var srcDir = tmp.CreateSubdirectory("src");
        var destDir = tmp.CreateSubdirectory("dest");

        var cloneBytes = "AU-DATA"u8.ToArray();
        var cloneRom = tmp.WriteBytes("src/Game Short (Australia).sfc", cloneBytes);
        var crcClone = await _services.ChecksumService.ComputeFileCrc32Async(cloneRom);

        var parentBytes = "EU-DATA"u8.ToArray();
        var parentRom = tmp.WriteBytes("src/Game Long Canonical Title (Europe).sfc", parentBytes);
        var crcParent = await _services.ChecksumService.ComputeFileCrc32Async(parentRom);

        var datPath = tmp.WriteText("parent_tiebreak.dat", $"""
            <?xml version="1.0"?>
            <datafile>
            	<header>
            		<name>Nintendo - Super Nintendo Entertainment System</name>
            		<homepage>No-Intro</homepage>
            	</header>
            	<game name="Game Short (Australia)" id="0002" cloneofid="0001">
            		<rom name="Game Short (Australia).sfc" size="{cloneBytes.Length}" crc="{crcClone}"/>
            	</game>
            	<game name="Game Long Canonical Title (Europe)" id="0001">
            		<rom name="Game Long Canonical Title (Europe).sfc" size="{parentBytes.Length}" crc="{crcParent}"/>
            	</game>
            </datafile>
            """);

        var config = new SortConfig
        {
            SourceDir = srcDir,
            DestinationDir = destDir,
            NamingConvention = NamingConvention.NoIntro,
            DatFile = datPath,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                PriorityLanguages = ["en"],
                PreferredVideo = VideoPreference.Pal
            }
        };

        var res = await _services.Processor.ExecuteAsync(config, isDryRun: true);
        var parentItem = res.Items.Single(it => it.SourcePath.Contains("Europe"));
        var cloneItem = res.Items.Single(it => it.SourcePath.Contains("Australia"));

        parentItem.Status.Should().Be(FileStatus.Valid);
        parentItem.Is1G1RWinner.Should().BeTrue();

        cloneItem.Status.Should().Be(FileStatus.Excluded);
        cloneItem.Is1G1RWinner.Should().BeFalse();
    }

    [Fact]
    public async Task NoIntroMultiRomCartridge_ShouldNotBeTreatedAsMultiDiskSet()
    {
        using var tmp = new TempDirectoryFixture();
        var srcDir = tmp.CreateSubdirectory("src");
        var destDir = tmp.CreateSubdirectory("dest");

        var mainBytes = "SONIC-KNUCKLES-MAIN"u8.ToArray();
        var mainRom = tmp.WriteBytes("src/Sonic & Knuckles (World).md", mainBytes);
        var crcMain = await _services.ChecksumService.ComputeFileCrc32Async(mainRom);

        var lockonBytes = "SONIC-KNUCKLES-LOCKON"u8.ToArray();
        var lockonRom = tmp.WriteBytes("src/Sonic & Knuckles (World) (Lock-on).bin", lockonBytes);
        var crcLockon = await _services.ChecksumService.ComputeFileCrc32Async(lockonRom);

        var sccBytes = "SONIC-KNUCKLES-SCC"u8.ToArray();
        var sccRom = tmp.WriteBytes("src/Sonic & Knuckles (World) (Sonic Classic Collection).md", sccBytes);
        var crcScc = await _services.ChecksumService.ComputeFileCrc32Async(sccRom);

        var datPath = tmp.WriteText("md_sonic.dat", $"""
            <?xml version="1.0"?>
            <datafile>
            	<header>
            		<name>Sega - Mega Drive - Genesis</name>
            		<homepage>No-Intro</homepage>
            	</header>
            	<game name="Sonic &amp; Knuckles (World)" id="1001">
            		<rom name="Sonic &amp; Knuckles (World).md" size="{mainBytes.Length}" crc="{crcMain}"/>
            		<rom name="Sonic &amp; Knuckles (World) (Lock-On).bin" size="{lockonBytes.Length}" crc="{crcLockon}"/>
            	</game>
            	<game name="Sonic &amp; Knuckles (World) (Sonic Classic Collection)" id="1002" cloneofid="1001">
            		<rom name="Sonic &amp; Knuckles (World) (Sonic Classic Collection).md" size="{sccBytes.Length}" crc="{crcScc}"/>
            	</game>
            </datafile>
            """);

        var config = new SortConfig
        {
            SourceDir = srcDir,
            DestinationDir = destDir,
            NamingConvention = NamingConvention.NoIntro,
            DatFile = datPath,
            Pattern = "{letter}",
            SubfolderMultidisk = true
        };

        var res = await _services.Processor.ExecuteAsync(config, isDryRun: true);
        foreach (var it in res.Items)
        {
            it.IsMultiMedia.Should().BeFalse();
            Path.GetDirectoryName(it.TargetPath).Should().Be(Path.Combine(destDir, "S"));
        }
    }

    [Fact]
    public async Task StrictWatertightnessBetweenTosecAndNoIntroDats_ShouldIsolateTabOptionsIn1G1RMode()
    {
        using var tmp = new TempDirectoryFixture();
        var srcDir = tmp.CreateSubdirectory("src_ni");
        var destDir = tmp.CreateSubdirectory("dest_ni");

        var biosBytes = "SNES-CD-BOOT-PROTO"u8.ToArray();
        var biosProto = tmp.WriteBytes("src_ni/[BIOS] Super NES CD-ROM - Boot ROM (Japan) (En) (v0.95) (Proto).sfc", biosBytes);
        var crcBios = await _services.ChecksumService.ComputeFileCrc32Async(biosProto);

        var disc1Bytes = "MULTI-DISC-1-ONLY"u8.ToArray();
        var disc1 = tmp.WriteBytes("src_ni/Multi Game (Europe) (Disc 1 of 2).sfc", disc1Bytes);
        var crcD1 = await _services.ChecksumService.ComputeFileCrc32Async(disc1);

        var datNi = tmp.WriteText("snes_nointro.dat", $"""
            <?xml version="1.0"?>
            <datafile>
            	<header>
            		<name>Nintendo - Super Nintendo Entertainment System</name>
            		<homepage>No-Intro</homepage>
            	</header>
            	<game name="[BIOS] Super NES CD-ROM - Boot ROM (Japan) (En) (Proto)">
            		<rom name="[BIOS] Super NES CD-ROM - Boot ROM (Japan) (En) (Proto).sfc" size="{biosBytes.Length}" crc="{crcBios}"/>
            	</game>
            	<game name="Multi Game (Europe) (Disc 1 of 2)">
            		<rom name="Multi Game (Europe) (Disc 1 of 2).sfc" size="{disc1Bytes.Length}" crc="{crcD1}"/>
            	</game>
            </datafile>
            """);

        var cfgNi = new SortConfig
        {
            SourceDir = srcDir,
            DestinationDir = destDir,
            NamingConvention = NamingConvention.Auto,
            DatFile = datNi,
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                PriorityLanguages = ["fr", "m", "en"],
                FallbackUnmatchedLanguages = false,
                FilterCountries = true,
                PriorityCountries = ["FR", "EU", "WO", "US", "GB", "CA", "AU", "JP"],
                FallbackUnmatchedCountries = false,
                // TOSEC tab options (must NOT leak into No-Intro DAT processing):
                IgnoreModificationsInPriority = true,
                CompleteMultidiskSetsOnly = true,
                SmartMultidiskGrouping = true,
                ExcludeDevVersions = false,
                // No-Intro tab options (must strictly govern No-Intro DAT processing):
                NoIntroExcludeDevVersions = true,
                NoIntroCompleteMultimediaSetsOnly = false
            }
        };

        var res = await _services.Processor.ExecuteAsync(cfgNi, isDryRun: true);
        var biosItem = res.Items.Single(it => it.TargetFilename!.Contains("Boot ROM"));
        var multiItem = res.Items.Single(it => it.TargetFilename!.Contains("Multi Game"));

        biosItem.Status.Should().Be(FileStatus.Excluded);
        _services.I18n.Format(biosItem.StatusReason).Should().Contain("Proto");

        multiItem.Status.Should().Be(FileStatus.Valid);
    }
}
