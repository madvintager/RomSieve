using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using CsCheck;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using static RomSieve.Core.Tests.Fixtures.PatchTestEncoder;

namespace RomSieve.Core.Tests;

/// <summary>
/// The pattern of the folders of the patched versions (<c>patched_pattern</c>): its key in the configuration, empty by
/// default (next to the original ROM) and kept empty, and the check that refuses a pattern that would leave the
/// destination or write in the folders RomSieve keeps for itself, before anything is read or written; the path it gives
/// (<see cref="PathOrganizerService.BuildTargetPath"/>, from the root of the destination, the variables of the original
/// ROM, <c>{origin_folder}</c> and <c>{dat_folder}</c>, a lot with a folder per DAT and its mirror); real sorts in each
/// transfer mode, with the collisions, the purge, the journal and its undo, the exports, and the recalculation of a game.
/// </summary>
public sealed class PatchedPatternTests : IDisposable
{
    private readonly CoreTestServices _services = new();
    private readonly TempDirectoryFixture _temp = new();
    private readonly OperationJournalService _journal;
    private readonly SortProcessorService _processor;
    private readonly string _src;
    private readonly string _dst;
    private readonly string _patches;

    private static readonly byte[] Japan = Data(1, 4096);
    private static readonly byte[] Usa = Data(2, 4096);
    private static readonly byte[] Satellaview = Data(3, 4096);
    private static readonly byte[] Translated = Translate(Japan);
    private static readonly byte[] UsaHack = Translate(Usa);

    public PatchedPatternTests()
    {
        var crcCache = new CrcCacheService(_temp.GetFilePath(Path.Combine("cache", "crc_cache.json")));
        _journal = new OperationJournalService(_temp.GetFilePath("journals"), _services.ChecksumService, crcCache);
        _processor = new SortProcessorService(
            _services.ChecksumService, _services.DatService, _services.NoIntroParser, _services.FilterService,
            _services.Organizer, _services.I18n, _services.OpticalCompanionService, _services.MameParser, _services.Patches,
            crcCache, _services.PatchLinks, _services.Frontends, _journal);
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

    private static byte[] Translate(byte[] data)
    {
        var translated = data.Concat(Ascii("English")).ToArray();
        translated[10] ^= 0x55;
        return translated;
    }

    private static Dictionary<string, object?> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
    }

    // =========================================================================
    // Configuration
    // =========================================================================

    [Fact]
    public void ThePattern_ShouldBeEmptyByDefault_AndBeWrittenAndReadBack()
    {
        var defaults = SortSettingsSerializer.Read(new Dictionary<string, object?>());
        defaults.PatchedPattern.Should().BeEmpty("the patched versions go next to their original ROM by default");
        SortSettingsSerializer.ToSortConfig(defaults).PatchedPattern.Should().BeEmpty();

        var data = new Dictionary<string, object?>(StringComparer.Ordinal);
        SortSettingsSerializer.Write(new SortSettings { PatchedPattern = " Patchs/{title} " }, data);
        data["patched_pattern"].Should().Be("Patchs/{title}");

        var read = SortSettingsSerializer.Read(Parse(JsonSerializer.Serialize(data)));
        read.PatchedPattern.Should().Be("Patchs/{title}");
        SortSettingsSerializer.ToSortConfig(read).PatchedPattern.Should().Be("Patchs/{title}");
    }

    [Fact]
    public void AnEmptyPattern_ShouldStayEmpty_UnlikeTheMainPattern()
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal);
        SortSettingsSerializer.Write(new SortSettings { Pattern = "", PatchedPattern = "   " }, data);

        data["pattern"].Should().Be(SortSettings.DefaultPattern, "an empty main pattern is written as the default one");
        data["patched_pattern"].Should().Be(string.Empty, "an empty pattern of the patched versions is a choice");

        // An empty value read back replaces a value given as baseline: it is not taken as missing
        var read = SortSettingsSerializer.Read(Parse("""{ "patched_pattern": "" }"""), new SortSettings { PatchedPattern = "Patchs" });
        read.PatchedPattern.Should().BeEmpty();
    }

    [Fact]
    public void AFlatDestination_ShouldLeaveThePatternOfThePatchedVersions()
    {
        var config = SortSettingsSerializer.ToSortConfig(new SortSettings { FlatDestination = true, PatchedPattern = "{origin_folder}/Patchs" });

        config.Pattern.Should().BeEmpty();
        config.PatchedPattern.Should().Be("{origin_folder}/Patchs", "everything at the root is about the main pattern only");
    }

    [Theory]
    [InlineData("""{ "config_version": 1, "patch_dir": "C:/patches" }""")]
    [InlineData("""{ "config_version": 2, "patch_dir": "C:/patches" }""")]
    [InlineData("""{ "patch_dir": "C:/patches", "patched_pattern": null }""")]
    public void AConfigurationWithoutTheKey_ShouldPutThePatchedVersionsNextToTheirOrigin(string json)
    {
        // A new key, without migration: a configuration of an earlier version keeps its result
        SortSettingsSerializer.Read(Parse(json)).PatchedPattern.Should().BeEmpty();
        ConfigSchema.CurrentVersion.Should().Be(2, "the option does not change the format of the configuration");
    }

    // =========================================================================
    // Check of the pattern
    // =========================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Patchs")]
    [InlineData("Patchs/{title}")]
    [InlineData(@"Patchs\{dat}\{title}\")]
    [InlineData("{origin_folder}/Patchs")]
    [InlineData(" {origin_folder} /Patchs")]
    [InlineData("{dat_folder}/Hacks/{title}")]
    [InlineData("Patchs/_purged")]
    [InlineData("Patchs/.romsieve")]
    [InlineData("Traductions (FR) [v1.2]/{letter}")]
    [InlineData("_unmatched")]
    [InlineData("{bogus}/Patchs")]
    [InlineData("Patchs/{title")]
    [InlineData("...")]
    public void AnAcceptedPattern_ShouldHaveNoProblem(string? pattern)
    {
        // Unknown variables, braces without their pair and _unmatched are only warned about, as for the main pattern
        PathOrganizerService.CheckPatchedPattern(pattern).Should().Be(PatchedPatternProblem.None);
    }

    [Theory]
    [InlineData("/Patchs", PatchedPatternProblem.Rooted)]
    [InlineData(@"\Patchs", PatchedPatternProblem.Rooted)]
    [InlineData(@"\\server\share\Patchs", PatchedPatternProblem.Rooted)]
    [InlineData(" /Patchs", PatchedPatternProblem.Rooted)]
    [InlineData(@"C:\Patchs", PatchedPatternProblem.Drive)]
    [InlineData("C:Patchs", PatchedPatternProblem.Drive)]
    [InlineData("Patchs/d:", PatchedPatternProblem.Drive)]
    [InlineData("..", PatchedPatternProblem.ParentSegment)]
    [InlineData("../Patchs", PatchedPatternProblem.ParentSegment)]
    [InlineData(@"Patchs\..\..\x", PatchedPatternProblem.ParentSegment)]
    [InlineData("Patchs/ .. /x", PatchedPatternProblem.ParentSegment)]
    [InlineData("Patchs:old", PatchedPatternProblem.ForbiddenCharacter)]
    [InlineData("Patchs/a<b", PatchedPatternProblem.ForbiddenCharacter)]
    [InlineData("Patchs/a>b", PatchedPatternProblem.ForbiddenCharacter)]
    [InlineData("Patchs/\"x\"", PatchedPatternProblem.ForbiddenCharacter)]
    [InlineData("Patchs|Hacks", PatchedPatternProblem.ForbiddenCharacter)]
    [InlineData("Patchs?", PatchedPatternProblem.ForbiddenCharacter)]
    [InlineData("Patchs/*", PatchedPatternProblem.ForbiddenCharacter)]
    [InlineData("Patchs\0/x", PatchedPatternProblem.ForbiddenCharacter)]
    [InlineData("Patchs\t2", PatchedPatternProblem.ForbiddenCharacter)]
    [InlineData("_purged", PatchedPatternProblem.ReservedFolder)]
    [InlineData("_PURGED/{title}", PatchedPatternProblem.ReservedFolder)]
    [InlineData("_purged. /{title}", PatchedPatternProblem.ReservedFolder)]
    [InlineData(".romsieve/x", PatchedPatternProblem.ReservedFolder)]
    [InlineData("{dat_folder}/_purged/x", PatchedPatternProblem.ReservedFolder)]
    [InlineData("{origin_folder}-patched", PatchedPatternProblem.FolderVariableNotAlone)]
    [InlineData("Patchs/{dat_folder}{title}", PatchedPatternProblem.FolderVariableNotAlone)]
    [InlineData("x{origin_folder}/Patchs", PatchedPatternProblem.FolderVariableNotAlone)]
    public void ARefusedPattern_ShouldGiveItsProblem(string pattern, PatchedPatternProblem expected)
    {
        PathOrganizerService.CheckPatchedPattern(pattern).Should().Be(expected);
    }

    [Fact]
    public void AControlCharacter_ShouldBeRefused()
    {
        // A null character would stop Path.GetFullPath; kept out of the theory data, whose names it would break
        PathOrganizerService.CheckPatchedPattern("Patchs\0/x").Should().Be(PatchedPatternProblem.ForbiddenCharacter);
        PathOrganizerService.CheckPatchedPattern("Patchs\t2").Should().Be(PatchedPatternProblem.ForbiddenCharacter);
        PathOrganizerService.CheckPatchedPattern("Patchs\u001f").Should().Be(PatchedPatternProblem.ForbiddenCharacter);
    }

    [Fact]
    public void AFolderVariableFirst_ShouldLeaveTheNextFolderChecked()
    {
        // For a single DAT, {dat_folder} is worth no folder: _purged would be the first folder of the destination
        PathOrganizerService.CheckPatchedPattern("{origin_folder}/{dat_folder}/_purged").Should().Be(PatchedPatternProblem.ReservedFolder);
        PathOrganizerService.CheckPatchedPattern("{dat_folder}/.romsieve").Should().Be(PatchedPatternProblem.ReservedFolder);
    }

    // =========================================================================
    // Refusal of a run
    // =========================================================================

    private SortConfig Config(string? patchDir, string patchedPattern) => new()
    {
        SourceDir = Path.Combine(Path.GetTempPath(), "romsieve_patched_pattern_source"),
        DestinationDir = Path.Combine(Path.GetTempPath(), "romsieve_patched_pattern_destination"),
        PatchDir = patchDir,
        PatchedPattern = patchedPattern
    };

    [Theory]
    [InlineData("../Patchs", "dialog.error_patched_pattern_invalid")]
    [InlineData("_purged", "dialog.error_patched_pattern_invalid")]
    [InlineData("Patchs/{title}", null)]
    [InlineData("", null)]
    public void ThePatternOfThePatchedVersions_ShouldBeCheckedWithAPatchFolder(string pattern, string? expected)
    {
        var config = Config(Path.Combine(Path.GetTempPath(), "romsieve_patched_pattern_patches"), pattern);

        SortConfigValidator.GetPatchedPatternErrorKey(config).Should().Be(expected);
        SortConfigValidator.GetErrorKey(config, isMameRun: false).Should().Be(expected);
        SortConfigValidator.GetErrorKey(config, isMameRun: true).Should().Be(expected, "the pattern is checked whatever the run");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void ThePatternOfThePatchedVersions_ShouldNotBeCheckedWithoutAPatchFolder(string? patchDir)
    {
        // Greyed out without a patch folder, and nothing it would write is written: it never blocks a sort
        SortConfigValidator.GetErrorKey(Config(patchDir, "../Patchs"), isMameRun: false).Should().BeNull();
    }

    [Fact]
    public void AnOverlap_ShouldBeReportedBeforeThePattern()
    {
        var config = Config(Path.Combine(Path.GetTempPath(), "romsieve_patched_pattern_patches"), "../Patchs");
        config.DestinationDir = config.SourceDir;

        SortConfigValidator.GetErrorKey(config, isMameRun: false).Should().Be("dialog.error_same_source_dest");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARunWithARefusedPattern_ShouldBeRefusedBeforeAnything(bool isDryRun)
    {
        var src = _temp.CreateSubdirectory("src");
        _temp.WriteBytes("src/Game (Japan).sfc", Japan);
        var destination = _temp.CreateSubdirectory("out");
        _temp.WriteText("out/kept.txt", "kept");
        var patches = _temp.CreateSubdirectory("patches");
        _temp.WriteBytes("patches/Game (Japan) (T-En).bps", BpsLinear(Japan, Translated));
        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = destination,
            PatchDir = patches,
            PatchedPattern = "../Patchs",
            PurgeDestination = true
        };

        var act = () => _services.Processor.ExecuteAsync(config, isDryRun);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Be(_services.I18n.Tr("dialog.error_patched_pattern_invalid"));
        Directory.GetFileSystemEntries(destination).Select(Path.GetFileName).Should().Equal(["kept.txt"], "neither purge nor marker, nothing written");
        Directory.Exists(Path.Combine(_temp.Path, "Patchs")).Should().BeFalse();
    }

    // =========================================================================
    // Path of a patched version
    // =========================================================================

    private const string Chrono = "Chrono Trigger (Japan).sfc";
    private const string ChronoPatched = "Chrono Trigger (Japan) [T-En].sfc";
    private const string Snes = "Nintendo - SNES";

    private ParsedRomName ParseName(string name) => _services.NoIntroParser.ParseRomFilename(name, null, 0, NamingConvention.NoIntro);

    private static string Under(string destination, string relative) =>
        Path.Combine([destination, .. relative.Split('/', StringSplitOptions.RemoveEmptyEntries)]);

    /// <summary>A single DAT, a DAT of a lot with a folder of its own, the same mirrored under its folder <c>No-Intro</c>.</summary>
    private static (DatPlacement Dat, bool Mirror, string OriginFolder) Placement(string kind) => kind switch
    {
        "single" => (new DatPlacement(Snes), false, "Chrono Trigger"),
        "lot" => (new DatPlacement(Snes, null, IsLotMember: true), false, "Nintendo - SNES/Chrono Trigger"),
        _ => (new DatPlacement(Snes, "No-Intro", IsLotMember: true), true, "No-Intro/Nintendo - SNES/Chrono Trigger")
    };

    [Theory]
    [InlineData("", "single", "Chrono Trigger")]
    [InlineData("", "lot", "Nintendo - SNES/Chrono Trigger")]
    [InlineData("", "mirror", "No-Intro/Nintendo - SNES/Chrono Trigger")]
    [InlineData("Patchs/{title}", "single", "Patchs/Chrono Trigger")]
    [InlineData("Patchs/{title}", "lot", "Patchs/Chrono Trigger")]
    [InlineData("Patchs/{title}", "mirror", "Patchs/Chrono Trigger")]
    [InlineData("Patchs/{dat}/{title}", "single", "Patchs/Nintendo - SNES/Chrono Trigger")]
    [InlineData("Patchs/{dat}/{title}", "lot", "Patchs/Nintendo - SNES/Chrono Trigger")]
    [InlineData("Patchs/{dat}/{title}", "mirror", "Patchs/Nintendo - SNES/Chrono Trigger")]
    [InlineData("{dat_folder}/Patchs", "single", "Patchs")]
    [InlineData("{dat_folder}/Patchs", "lot", "Nintendo - SNES/Patchs")]
    [InlineData("{dat_folder}/Patchs", "mirror", "No-Intro/Nintendo - SNES/Patchs")]
    [InlineData("{origin_folder}/Patchs", "single", "Chrono Trigger/Patchs")]
    [InlineData("{origin_folder}/Patchs", "lot", "Nintendo - SNES/Chrono Trigger/Patchs")]
    [InlineData("{origin_folder}/Patchs", "mirror", "No-Intro/Nintendo - SNES/Chrono Trigger/Patchs")]
    [InlineData(@"Hacks\{country}\", "single", "Hacks/JP")]
    [InlineData(" Traductions / {letter} ", "lot", "Traductions/C")]
    [InlineData("{origin_folder}", "mirror", "No-Intro/Nintendo - SNES/Chrono Trigger")]
    public void APatchedVersion_ShouldGoWhereThePatternOfThePatchedVersionsSays(string pattern, string kind, string expected)
    {
        var (dat, mirror, originFolder) = Placement(kind);
        var config = new SortConfig
        {
            SourceDir = _src,
            DestinationDir = _dst,
            Pattern = "{title}",
            DatFolderMirror = mirror,
            PatchedPattern = pattern
        };

        _services.Organizer.BuildTargetPath(ParseName(Chrono), ChronoPatched, config, isMultiMediaOverride: false, dat, patched: true)
            .Should().Be(Path.Combine(Under(_dst, expected), ChronoPatched));
        _services.Organizer.BuildTargetPath(ParseName(Chrono), Chrono, config, isMultiMediaOverride: false, dat)
            .Should().Be(Path.Combine(Under(_dst, originFolder), Chrono), "the pattern of the patched versions never moves another file");
    }

    [Fact]
    public void AFlatDestination_ShouldLeaveTheOriginFolderEmpty()
    {
        var config = SortSettingsSerializer.ToSortConfig(new SortSettings
        {
            DestinationDir = _dst,
            FlatDestination = true,
            PatchedPattern = "{origin_folder}/Patchs"
        });

        _services.Organizer.BuildTargetPath(ParseName(Chrono), ChronoPatched, config, isMultiMediaOverride: false, patched: true)
            .Should().Be(Path.Combine(_dst, "Patchs", ChronoPatched));
    }

    [Theory]
    [InlineData("{title}", "_purged_")]
    [InlineData("{title}/Patchs", "_purged_/Patchs")]
    [InlineData("Patchs/{title}", "Patchs/_purged")]
    [InlineData("{dat_folder}/{title}", "_purged_")]
    public void AValueNamingTheQuarantine_ShouldNeverPutAPatchedVersionThere(string pattern, string expected)
    {
        // The pattern is accepted: the folder the title gives is only known for each file
        PathOrganizerService.CheckPatchedPattern(pattern).Should().Be(PatchedPatternProblem.None);
        var config = new SortConfig { SourceDir = _src, DestinationDir = _dst, Pattern = "{title}", PatchedPattern = pattern };

        _services.Organizer.BuildTargetPath(ParseName("_purged (Japan).sfc"), "_purged (Japan) [T-En].sfc", config, patched: true)
            .Should().Be(Path.Combine(Under(_dst, expected), "_purged (Japan) [T-En].sfc"));
    }

    [Fact]
    public void ZipMode_ShouldNameTheArchiveInTheFolderOfThePattern()
    {
        var config = new SortConfig { SourceDir = _src, DestinationDir = _dst, TransferMode = TransferMode.Zip, PatchedPattern = "Patchs/{title}" };

        _services.Organizer.BuildTargetPath(ParseName(Chrono), ChronoPatched, config, isMultiMediaOverride: false, patched: true)
            .Should().Be(Path.Combine(_dst, "Patchs", "Chrono Trigger", "Chrono Trigger (Japan) [T-En].zip"));
    }

    [Fact]
    public void TheFolderVariables_ShouldBeKnownToThePatternOfThePatchedVersionsOnly()
    {
        _services.Organizer.CheckPattern("{origin_folder}/{dat_folder}/{title}", patched: true).IsValid.Should().BeTrue();
        _services.Organizer.CheckPattern("{origin_folder}/{dat_folder}/{title}").UnknownVariables
            .Should().Equal(["{origin_folder}", "{dat_folder}"], "the main pattern has no folder of an original ROM");
        _services.Organizer.CheckPattern("Patchs/{bogus}", patched: true).UnknownVariables.Should().Equal(["{bogus}"]);
    }

    private static readonly string[] PropertyNames =
    [
        Chrono,
        "Legend of Zelda, The - A Link to the Past (Europe).sfc",
        "Final Fantasy VII (Europe) (Disc 1).cue",
        "Another World (1991)(Delphine)(Disk 1 of 2).adf",
        "Lemmings (1991)(Psygnosis)(FR)[!].adf",
        ".. (Japan).sfc",
        "C: (USA).sfc",
        "_purged (World).sfc",
        "readme"
    ];

    private static readonly Gen<SortConfig> AnyConfig =
        from pattern in Gen.OneOfConst("", "{title}", "{publisher}/{year}/{title}/", "{country}/{letter}", "{dat}/{title}", "..", "{bogus}")
        from flags in Gen.Bool.Array[6]
        select new SortConfig
        {
            SourceDir = "src",
            DestinationDir = Path.Combine(Path.GetTempPath(), "romsieve_patched_pattern_property"),
            Pattern = pattern,
            FolderPerDat = flags[0],
            DatFolderMirror = flags[1],
            TransferMode = flags[2] ? TransferMode.Zip : TransferMode.Copy,
            RestoreArticles = flags[3],
            SubfolderMultidisk = flags[4],
            GenerateM3u = flags[5],
            M3uPlacement = M3uPlacementMode.ParentHidden
        };

    private static readonly Gen<DatPlacement?> AnyPlacement = Gen.OneOfConst<DatPlacement?>(
        null, new DatPlacement(Snes), new DatPlacement(Snes, null, true), new DatPlacement("A/../B", @"No-Intro\..\x", true));

    [Fact]
    public void AnEmptyPattern_ShouldGiveTheTargetOfTheOriginFolder_AsBeforeThePattern()
    {
        // Empty, or {origin_folder} alone: the folder the original ROM has, as a patched version always had
        Gen.Select(Gen.OneOfConst(PropertyNames), AnyConfig, AnyPlacement, Gen.Bool).Sample((name, config, dat, multi) =>
        {
            var parsed = _services.NoIntroParser.ParseRomFilename(name);
            var before = _services.Organizer.BuildTargetPath(parsed, name, config, multi, dat);

            config.PatchedPattern = string.Empty;
            _services.Organizer.BuildTargetPath(parsed, name, config, multi, dat, patched: true).Should().Be(before);
            config.PatchedPattern = " {origin_folder} ";
            _services.Organizer.BuildTargetPath(parsed, name, config, multi, dat, patched: true).Should().Be(before);
            _services.Organizer.BuildTargetPath(parsed, name, config, multi, dat).Should().Be(before, "only a patched version follows it");
        });
    }

    private static readonly Gen<string> AnyPatchedPattern =
        Gen.OneOfConst("Patchs", "{title}", "{origin_folder}", "{dat_folder}", "{dat}", "..", ".", "...", " ", "_purged", ".romsieve",
                "C:", "{bogus}", "a{title}b", "{origin_folder}x", "\u0000", "<", "_unmatched", "~")
            .Array[1, 5]
            .Select(Gen.OneOfConst("", "/", "\\"), Gen.OneOfConst("/", "\\"), (parts, start, separator) => start + string.Join(separator, parts));

    [Fact]
    public void AnAcceptedPattern_ShouldNeverLeaveTheDestination()
    {
        // Whatever the names and the DATs: the values of the variables are sanitized, and the pattern checked
        Gen.Select(AnyPatchedPattern, Gen.OneOfConst(PropertyNames), AnyConfig, AnyPlacement).Sample((pattern, name, config, dat) =>
        {
            if (PathOrganizerService.CheckPatchedPattern(pattern) != PatchedPatternProblem.None)
            {
                return;
            }

            // The values come from a hostile name; the file name itself is one the sort accepts (RejectsUnsafeNames)
            var parsed = _services.NoIntroParser.ParseRomFilename(name);
            var origin = _services.Organizer.BuildTargetPath(parsed, ChronoPatched, config, dat: dat);
            config.PatchedPattern = pattern;
            var target = _services.Organizer.BuildTargetPath(parsed, ChronoPatched, config, dat: dat, patched: true);
            PathOrganizerService.IsInsideDirectory(target, config.DestinationDir).Should().BeTrue(target);

            // Never the quarantine nor the marker, for the patched version as for its original ROM
            string[] guards = [DestinationGuard.QuarantineDirectoryName, DestinationGuard.MarkerFileName];
            string FirstFolder(string path) => Path.GetRelativePath(config.DestinationDir, path).Split(Path.DirectorySeparatorChar)[0];
            guards.Should().NotContain(FirstFolder(origin), origin);
            guards.Should().NotContain(FirstFolder(target), target);
        }, iter: 2000);
    }

    // =========================================================================
    // Real sorts
    // =========================================================================

    private string WriteDat(params (string Game, string Rom, byte[] Data)[] roms)
    {
        var games = string.Concat(roms.Select(r =>
            $"""<game name="{r.Game}"><description>{r.Game}</description><rom name="{r.Rom}" size="{r.Data.Length}" crc="{Crc(r.Data):x8}"/></game>"""));
        return _temp.WriteText(
            "dat/snes.dat",
            $"""<?xml version="1.0"?><datafile><header><name>{Snes}</name><homepage>No-Intro</homepage></header>{games}</datafile>""");
    }

    /// <summary>The Japanese and American releases of a game, the American one kept by the 1G1R, and a translation of the Japanese one.</summary>
    private string WriteCollection()
    {
        _temp.WriteBytes("src/Game (Japan).sfc", Japan);
        _temp.WriteBytes("src/Game (USA).sfc", Usa);
        _temp.WriteBytes("patches/Game (Japan) (T-En).bps", BpsLinear(Japan, Translated));
        return WriteDat(("Game (Japan)", "Game (Japan).sfc", Japan), ("Game (USA)", "Game (USA).sfc", Usa));
    }

    private SortConfig SortConfigOf(string dat, string patchedPattern, TransferMode mode = TransferMode.Copy, CollisionMode collision = CollisionMode.Rename) => new()
    {
        SourceDir = _src,
        DestinationDir = _dst,
        Pattern = "{title}",
        PatchedPattern = patchedPattern,
        TransferMode = mode,
        CollisionMode = collision,
        AppendToDestination = true,
        NamingConvention = NamingConvention.NoIntro,
        DatFile = dat,
        PatchDir = _patches,
        Filters = new FilterConfig { OneGameOneRom = true, PriorityLanguages = ["en", "ja"] }
    };

    private Task<SortExecutionResult> SortAsync(SortConfig config) => _processor.ExecuteAsync(config, isDryRun: false);

    /// <summary>The files of the destination, the marker and the quarantine apart, relative to it, with <c>/</c>.</summary>
    private List<string> DestinationFiles() =>
        Directory.EnumerateFiles(_dst, "*", SearchOption.AllDirectories)
            .Where(f => !DestinationGuard.IsGuardPath(f))
            .Select(f => Path.GetRelativePath(_dst, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static ProcessItem Patched(SortExecutionResult result, string origin = "Game (Japan).sfc") =>
        result.Items.Single(it => it.Patch is not null && it.Patch.Origin.DisplayName == origin);

    private void AssertNoTemporaryFile() =>
        Directory.EnumerateFiles(_temp.Path, "*" + DestinationGuard.ReplacementExtension, SearchOption.AllDirectories)
            .Should().BeEmpty("no temporary file is left behind");

    private async Task AssertUndoneAsync(SortExecutionResult result)
    {
        (await _journal.UndoAsync(result.JournalPath!)).Issues.Should().BeEmpty();
        DestinationFiles().Should().BeEmpty();
        Directory.EnumerateDirectories(_dst).Should().BeEmpty("the folders the sort created, now empty, are removed");
    }

    [Fact]
    public async Task Copy_ShouldWriteThePatchedVersionInItsFolder_WithTheValuesOfItsOrigin()
    {
        // The Japanese release is left out by the 1G1R: its country still names the folder of its patched version
        var dat = WriteCollection();

        var result = await SortAsync(SortConfigOf(dat, "Patchs/{country}"));

        var patched = Patched(result);
        patched.Status.Should().Be(FileStatus.Valid);
        patched.FinalTargetPath.Should().Be(Path.Combine(_dst, "Patchs", "JP", "Game (Japan) [T-En].sfc"));
        DestinationFiles().Should().Equal(["Game/Game (USA).sfc", "Patchs/JP/Game (Japan) [T-En].sfc"]);
        File.ReadAllBytes(patched.FinalTargetPath!).Should().Equal(Translated);
        File.ReadAllText(result.JournalPath!).Should().Contain("\"kind\":\"Copy\"").And.Contain("\"patch\":");
        AssertNoTemporaryFile();

        await AssertUndoneAsync(result);
    }

    [Fact]
    public async Task Move_ShouldWriteThePatchedVersionsInTheSubfolderOfTheirOrigin_BeforeTheOriginLeaves()
    {
        var dat = WriteCollection();
        _temp.WriteBytes("patches/Game (USA) (Hack).ups", Ups(Usa, UsaHack));

        var result = await SortAsync(SortConfigOf(dat, "{origin_folder}/Patchs", TransferMode.Move));

        result.Items.Where(it => it.Patch is not null).Should().OnlyContain(it => it.Status == FileStatus.Valid);
        DestinationFiles().Should().Equal(["Game/Game (USA).sfc", "Game/Patchs/Game (Japan) [T-En].sfc", "Game/Patchs/Game (USA) [Hack].sfc"]);
        File.ReadAllBytes(Path.Combine(_dst, "Game", "Patchs", "Game (USA) [Hack].sfc")).Should().Equal(UsaHack);
        File.Exists(Path.Combine(_src, "Game (USA).sfc")).Should().BeFalse("the origin is moved, after its patched version");
        File.Exists(Path.Combine(_src, "Game (Japan).sfc")).Should().BeTrue("an origin the 1G1R leaves out stays in the source");

        (await _journal.UndoAsync(result.JournalPath!)).Issues.Should().BeEmpty();
        File.ReadAllBytes(Path.Combine(_src, "Game (USA).sfc")).Should().Equal(Usa);
        DestinationFiles().Should().BeEmpty();
        Directory.EnumerateDirectories(_dst).Should().BeEmpty();
    }

    [Theory]
    [InlineData(TransferMode.HardLink)]
    [InlineData(TransferMode.SymbolicLink)]
    public async Task Links_ShouldWriteThePatchedVersionAsACopyInItsFolder(TransferMode mode)
    {
        var dat = WriteCollection();

        var result = await SortAsync(SortConfigOf(dat, "Patchs", mode));

        var path = Path.Combine(_dst, "Patchs", "Game (Japan) [T-En].sfc");
        Patched(result).FinalTargetPath.Should().Be(path);
        File.ReadAllBytes(path).Should().Equal(Translated);
        new FileInfo(path).LinkTarget.Should().BeNull("a link cannot hold modified data");
    }

    [Fact]
    public async Task Zip_ShouldWriteAnArchiveOfItsOwnInTheFolderOfThePattern()
    {
        var dat = WriteCollection();

        var result = await SortAsync(SortConfigOf(dat, "Patchs/{title}", TransferMode.Zip));

        DestinationFiles().Should().Equal(["Game/Game (USA).zip", "Patchs/Game/Game (Japan) [T-En].zip"]);
        using (var zip = ZipFile.OpenRead(Path.Combine(_dst, "Patchs", "Game", "Game (Japan) [T-En].zip")))
        {
            var entry = zip.Entries.Should().ContainSingle().Which;
            entry.FullName.Should().Be("Game (Japan) [T-En].sfc", "the archive holds the file under its own name, without folder");
            zip.Comment.Should().StartWith("TORRENTZIPPED-");
        }

        File.ReadAllText(result.JournalPath!).Should().Contain("\"kind\":\"ZipArchive\"");
        AssertNoTemporaryFile();
        await AssertUndoneAsync(result);
    }

    [Fact]
    public async Task PatchOnly_ShouldWriteThePatchedVersionAloneInItsFolder()
    {
        var dat = WriteCollection();
        var config = SortConfigOf(dat, "Patchs/{title}");
        config.PatchOnly = true;

        var result = await SortAsync(config);

        result.Items.Single(it => it.DisplayName == "Game (USA).sfc").StatusReason!.Key.Should().Be("reason.replaced_by_patch");
        DestinationFiles().Should().Equal(["Patchs/Game/Game (Japan) [T-En].sfc"]);
    }

    [Fact]
    public async Task AnOriginInAnArchive_ShouldBeExtractedInTheFolderOfThePattern_ThenRemoved()
    {
        var zip = _temp.GetFilePath("src/Game (Japan).zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var stream = archive.CreateEntry("Game (Japan).sfc").Open();
            stream.Write(Japan);
        }

        var before = File.ReadAllBytes(zip);
        var dat = WriteDat(("Game (Japan)", "Game (Japan).sfc", Japan));
        _temp.WriteBytes("patches/Game (Japan) (T-En).bps", BpsLinear(Japan, Translated));

        var result = await SortAsync(SortConfigOf(dat, "Patchs", TransferMode.Move));

        Patched(result).Status.Should().Be(FileStatus.Valid);
        DestinationFiles().Should().Equal(["Game/Game (Japan).sfc", "Patchs/Game (Japan) [T-En].sfc"]);
        File.ReadAllBytes(zip).Should().Equal(before, "an archive stays in place, even in Move mode");
        AssertNoTemporaryFile();
    }

    [Theory]
    [InlineData(CollisionMode.Rename)]
    [InlineData(CollisionMode.Skip)]
    [InlineData(CollisionMode.Overwrite)]
    public async Task AFileAlreadyInTheFolderOfThePattern_ShouldFollowTheCollisionMode(CollisionMode collision)
    {
        var dat = WriteCollection();
        var existing = Ascii("Already in the destination");
        _temp.WriteBytes("dst/Patchs/Game (Japan) [T-En].sfc", existing);

        var result = await SortAsync(SortConfigOf(dat, "Patchs", collision: collision));

        var patched = Patched(result);
        var target = Path.Combine(_dst, "Patchs", "Game (Japan) [T-En].sfc");
        switch (collision)
        {
            case CollisionMode.Rename:
                File.ReadAllBytes(target).Should().Equal(existing);
                File.ReadAllBytes(Path.Combine(_dst, "Patchs", "Game (Japan) [T-En]_01.sfc")).Should().Equal(Translated);
                break;
            case CollisionMode.Skip:
                patched.Status.Should().Be(FileStatus.Duplicate);
                File.ReadAllBytes(target).Should().Equal(existing);
                break;
            default:
                File.ReadAllBytes(target).Should().Equal(Translated);
                break;
        }
    }

    [Theory]
    [InlineData(CollisionMode.Rename)]
    [InlineData(CollisionMode.Skip)]
    [InlineData(CollisionMode.Overwrite)]
    public async Task TwoPatchedVersionsInTheSameFolder_ShouldBothBeWrittenInEveryMode(CollisionMode collision)
    {
        // A UPS and a BPS of one origin, both labelled T-En: the second takes the suffix, as next to their origin
        var dat = WriteCollection();
        _temp.WriteBytes("patches/Game (Japan) (T-En).ups", Ups(Japan, Translate(Translated)));

        var result = await SortAsync(SortConfigOf(dat, "Patchs", collision: collision));

        result.Items.Where(it => it.Patch is not null).Should().HaveCount(2).And.OnlyContain(it => it.Status == FileStatus.Valid);
        DestinationFiles().Should().Contain(["Patchs/Game (Japan) [T-En].sfc", "Patchs/Game (Japan) [T-En]_01.sfc"]);
    }

    [Fact]
    public async Task APurge_ShouldPutTheFolderOfThePatchedVersionsInQuarantine_AndTheUndoBringItBack()
    {
        var dat = WriteCollection();
        await SortAsync(SortConfigOf(dat, "Patchs"));
        var before = DestinationFiles();
        before.Should().Contain("Patchs/Game (Japan) [T-En].sfc");

        var config = SortConfigOf(dat, "Hacks");
        config.AppendToDestination = false;
        config.PurgeDestination = true;
        var result = await SortAsync(config);

        DestinationFiles().Should().Equal(["Game/Game (USA).sfc", "Hacks/Game (Japan) [T-En].sfc"]);
        Directory.EnumerateDirectories(Path.Combine(_dst, DestinationGuard.QuarantineDirectoryName), "Patchs", SearchOption.AllDirectories)
            .Should().ContainSingle("the folder of the patched versions is part of the destination");

        (await _journal.UndoAsync(result.JournalPath!)).Issues.Should().BeEmpty();
        DestinationFiles().Should().Equal(before);
    }

    [Fact]
    public async Task TheRecalculationOfAGame_ShouldKeepTheTargetOfThePatchedVersion()
    {
        var dat = WriteCollection();
        var config = SortConfigOf(dat, "Patchs/{country}");
        var result = await _processor.ExecuteAsync(config, isDryRun: true);
        var patched = Patched(result);
        var target = patched.FinalTargetPath;
        target.Should().Be(Path.Combine(_dst, "Patchs", "JP", "Game (Japan) [T-En].sfc"), "a simulation gives the same target");

        config.PatchOnly = true;
        _processor.RecalculateGameItems(result.Items.ToList(), config, result.DatCatalog, result.Items);

        result.Items.Single(it => it.DisplayName == "Game (USA).sfc").StatusReason!.Key.Should().Be("reason.replaced_by_patch");
        patched.FinalTargetPath.Should().Be(target);
    }

    [Fact]
    public async Task TheExports_ShouldFindThePatchedVersionInItsFolder()
    {
        var dat = WriteCollection();
        var result = await SortAsync(SortConfigOf(dat, "Patchs/{title}"));
        var written = Path.Combine(_dst, "Patchs", "Game", "Game (Japan) [T-En].sfc");

        // Without its patch, the DAT of the collection can only read the file the sort wrote, at its new path
        File.Delete(Path.Combine(_patches, "Game (Japan) (T-En).bps"));
        var output = _temp.GetFilePath("collection.xml");
        await _processor.ExportDatAsync(result.Items, output, DatExportFormat.LogiqxXml, result.DatCatalog);
        var rom = XDocument.Load(output).Descendants("rom").Single(r => (string?)r.Attribute("name") == "Game (Japan) [T-En].sfc");
        ((string?)rom.Attribute("sha1")).Should().Be(Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(Translated)));

        var json = _temp.GetFilePath("report.json");
        await _processor.ExportReportToJsonAsync(result.Items, result.Stats, json);
        using var document = JsonDocument.Parse(File.ReadAllText(json));
        document.RootElement.GetProperty("files").EnumerateArray()
            .Single(f => f.TryGetProperty("patch", out _)).GetProperty("target_path").GetString().Should().Be(written);
    }

    // =========================================================================
    // Lot of DATs
    // =========================================================================

    /// <summary>
    /// A lot in <c>dats/No-Intro/</c>: a translation of a Japanese release in each of two DATs, both named
    /// <c>Game (Japan) [T-En].sfc</c>.
    /// </summary>
    private SortConfig WriteLot(string patchedPattern, bool mirror, CollisionMode collision = CollisionMode.Rename)
    {
        string Dat(string name, byte[] data) =>
            $"""<?xml version="1.0"?><datafile><header><name>{name}</name><homepage>No-Intro</homepage></header><game name="Game (Japan)"><description>Game (Japan)</description><rom name="Game (Japan).sfc" size="{data.Length}" crc="{Crc(data):x8}"/></game></datafile>""";

        _temp.WriteText("dats/No-Intro/snes.dat", Dat(Snes, Japan));
        _temp.WriteText("dats/No-Intro/bs.dat", Dat("Nintendo - Satellaview", Satellaview));
        _temp.WriteBytes("src/snes/Game (Japan).sfc", Japan);
        _temp.WriteBytes("src/bs/Game (Japan).sfc", Satellaview);
        _temp.WriteBytes("patches/snes/Game (Japan) (T-En).bps", BpsLinear(Japan, Translated));
        _temp.WriteBytes("patches/bs/Game (Japan) (T-En).bps", BpsLinear(Satellaview, Translate(Satellaview)));
        return new SortConfig
        {
            SourceDir = _src,
            DestinationDir = _dst,
            DatFile = _temp.GetFilePath("dats"),
            DatRecursive = true,
            DatFolderMirror = mirror,
            Pattern = "{title}",
            PatchedPattern = patchedPattern,
            PatchDir = _patches,
            CollisionMode = collision
        };
    }

    [Theory]
    [InlineData("", false, "Nintendo - SNES/Game")]
    [InlineData("", true, "No-Intro/Nintendo - SNES/Game")]
    [InlineData("Patchs/{dat}/{title}", true, "Patchs/Nintendo - SNES/Game")]
    [InlineData("{dat_folder}/Patchs", false, "Nintendo - SNES/Patchs")]
    [InlineData("{dat_folder}/Patchs", true, "No-Intro/Nintendo - SNES/Patchs")]
    [InlineData("{origin_folder}/Patchs", true, "No-Intro/Nintendo - SNES/Game/Patchs")]
    public async Task InALot_ThePatchedVersionShouldGoWhereThePatternSays(string pattern, bool mirror, string expected)
    {
        var result = await _processor.ExecuteAsync(WriteLot(pattern, mirror), isDryRun: true);

        result.Items.Single(it => it.Patch is not null && it.DatName == Snes).FinalTargetPath
            .Should().Be(Path.Combine(Under(_dst, expected), "Game (Japan) [T-En].sfc"));
        result.Items.Single(it => it.Patch is null && it.DatName == Snes).FinalTargetPath
            .Should().Be(Path.Combine(Under(_dst, mirror ? "No-Intro/Nintendo - SNES/Game" : "Nintendo - SNES/Game"), "Game (Japan).sfc"));
    }

    [Theory]
    [InlineData(CollisionMode.Rename)]
    [InlineData(CollisionMode.Skip)]
    [InlineData(CollisionMode.Overwrite)]
    public async Task InALot_TwoDatsInTheSameFolderOfThePattern_ShouldBothBeWrittenInEveryMode(CollisionMode collision)
    {
        // Patchs/{title} puts the patched versions of every DAT together: one of the two takes the suffix
        var result = await SortAsync(WriteLot("Patchs/{title}", mirror: false, collision));

        result.Items.Where(it => it.Patch is not null).Should().HaveCount(2).And.OnlyContain(it => it.Status == FileStatus.Valid);
        DestinationFiles().Where(f => f.StartsWith("Patchs/", StringComparison.Ordinal))
            .Should().Equal(["Patchs/Game/Game (Japan) [T-En].sfc", "Patchs/Game/Game (Japan) [T-En]_01.sfc"]);
        await AssertUndoneAsync(result);
    }
}
