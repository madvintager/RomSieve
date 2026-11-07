using System.IO;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// Names read from an archive or a DAT that would write out of their folder, even out of the destination (zip slip):
/// absolute, on a drive, with a <c>..</c> segment or a null character. Such a file is in error and nothing is written
/// for it, from the simulation on and in every transfer mode, while the other files are sorted as usual. A last check
/// before every write keeps any target inside the destination.
/// </summary>
public sealed class PathTraversalTests : IDisposable
{
    private const string AlphaName = "Alpha (1990)(Ocean).adf";

    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");
    private readonly SortProcessorService _processor;
    private readonly string _src;
    private readonly string _dst;
    private readonly List<LogMessage> _executorLogs = [];

    public PathTraversalTests()
    {
        var crcCache = new CrcCacheService(_temp.GetFilePath(Path.Combine("cache", "crc_cache.json")));
        var journal = new OperationJournalService(_temp.GetFilePath("journals"), _services.ChecksumService, crcCache);
        _processor = new SortProcessorService(
            _services.ChecksumService,
            _services.DatService,
            _services.NoIntroParser,
            _services.FilterService,
            _services.Organizer,
            _services.I18n,
            _services.OpticalCompanionService,
            _services.MameParser,
            crcCache,
            journal);
        _src = _temp.CreateSubdirectory("src");
        _dst = _temp.CreateSubdirectory("dst");
    }

    public void Dispose() => _temp.Dispose();

    private sealed class LogCollector : IProgress<LogMessage>
    {
        public List<LogMessage> Messages { get; } = [];

        public void Report(LogMessage value) => Messages.Add(value);
    }

    private SortConfig Config(TransferMode mode = TransferMode.Copy, string? datFile = null) => new()
    {
        SourceDir = _src,
        DestinationDir = _dst,
        DatFile = datFile,
        Pattern = "{title}/",
        TransferMode = mode,
        NamingConvention = NamingConvention.Tosec
    };

    /// <summary>Files of the temporary directory outside the source, the destination, the DATs, the journals and the cache.</summary>
    private IEnumerable<string> FilesOutsideTheDestination() =>
        Directory.EnumerateFiles(_temp.Path, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(_temp.Path, p).Replace('\\', '/'))
            .Where(p => !new[] { "src/", "dst/", "dats/", "journals/", "cache/" }.Any(folder => p.StartsWith(folder, StringComparison.Ordinal)));

    private IEnumerable<string> DestinationFiles() =>
        Directory.EnumerateFiles(_dst, "*", SearchOption.AllDirectories)
            .Where(p => !DestinationGuard.IsGuardPath(p))
            .Select(p => Path.GetRelativePath(_dst, p).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

    private string CreateZip(string name, params string[] memberNames)
    {
        var path = Path.Combine(_src, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var memberName in memberNames)
        {
            using var stream = zip.CreateEntry(memberName).Open();
            stream.Write(Encoding.UTF8.GetBytes("data of " + memberName));
        }

        return path;
    }

    /// <summary>
    /// Member names of a crafted archive, as the pattern <c>{title}/</c> would place them: back up to the temporary
    /// directory, above the destination, or into it by an absolute path, so that a file written by mistake stays in the
    /// test. Both separators and the drives are refused under any system, so that an archive gives the same result
    /// under Windows and Linux.
    /// </summary>
    private string[] CraftedMembers() =>
    [
        "../../escaped (1990)(Pub).adf",
        "../../outside.txt",
        @"..\..\backslash (1990)(Pub).adf",
        "sub/../../within (1990)(Pub).adf",
        _temp.GetFilePath("rooted (1990)(Pub).adf").Replace('\\', '/'),
        "folder/C:drive (1990)(Pub).adf"
    ];

    // =========================================================================
    // Names and targets
    // =========================================================================

    [Theory]
    [InlineData(AlphaName)]
    [InlineData($"sub/{AlphaName}")]
    [InlineData($@"sub\{AlphaName}")]
    [InlineData("megadriv/sonic.zip")]
    [InlineData("a/./b.adf")]
    [InlineData("..dots..adf")]
    public void SafeName_ShouldBeAccepted_WithItsFolders(string name) =>
        PathOrganizerService.IsSafeRelativeName(name).Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../x.adf")]
    [InlineData(@"..\x.adf")]
    [InlineData("a/../../x.adf")]
    [InlineData("a/..")]
    [InlineData("/home/user/.config/autostart/x.desktop")]
    [InlineData(@"\x.adf")]
    [InlineData(@"\\server\share\x.adf")]
    [InlineData("C:x.adf")]
    [InlineData(@"C:\Users\Public\x.adf")]
    [InlineData("c:/x.adf")]
    [InlineData("folder/D:x.adf")]
    public void UnsafeName_ShouldBeRefused_UnderAnySystem(string name) =>
        PathOrganizerService.IsSafeRelativeName(name).Should().BeFalse();

    [Fact]
    public void NameWithANullCharacter_ShouldBeRefused()
    {
        PathOrganizerService.IsSafeRelativeName("x\0.adf").Should().BeFalse();
        PathOrganizerService.IsInsideDirectory(Path.Combine(_dst, "x\0.adf"), _dst).Should().BeFalse();
    }

    [Fact]
    public void Target_ShouldBeInsideTheDestination_OnceNormalized()
    {
        PathOrganizerService.IsInsideDirectory(Path.Combine(_dst, "Alpha", AlphaName), _dst).Should().BeTrue();
        PathOrganizerService.IsInsideDirectory(Path.Combine(_dst, "a", "..", AlphaName), _dst).Should().BeTrue();
        PathOrganizerService.IsInsideDirectory(Path.Combine(_dst, AlphaName), _dst + Path.DirectorySeparatorChar).Should().BeTrue();

        PathOrganizerService.IsInsideDirectory(_dst, _dst).Should().BeFalse("no file can take the place of the destination");
        PathOrganizerService.IsInsideDirectory(Path.Combine(_dst, ".."), _dst).Should().BeFalse();
        PathOrganizerService.IsInsideDirectory(Path.Combine(_dst, "Alpha", "..", "..", AlphaName), _dst).Should().BeFalse();
        PathOrganizerService.IsInsideDirectory(Path.Combine(_temp.Path, "dst2", AlphaName), _dst)
            .Should().BeFalse("a folder whose name begins like the destination is another folder");
    }

    [Fact]
    public void Target_ShouldBeComparedAsTheSystemComparesNames()
    {
        PathOrganizerService.IsInsideDirectory(Path.Combine(_temp.Path, "DST", AlphaName), _dst)
            .Should().Be(OperatingSystem.IsWindows(), "Windows ignores the case of a name, Linux does not");
    }

    [Fact]
    public void Target_UnderTheRootOfADrive_ShouldBeInsideIt()
    {
        var root = Path.GetPathRoot(_temp.Path)!;

        PathOrganizerService.IsInsideDirectory(Path.Combine(root, AlphaName), root).Should().BeTrue();
        PathOrganizerService.IsInsideDirectory(root, root).Should().BeFalse();
    }

    // =========================================================================
    // Names refused before anything is claimed or written
    // =========================================================================

    [Theory]
    [InlineData(TransferMode.Copy, false)]
    [InlineData(TransferMode.Move, false)]
    [InlineData(TransferMode.HardLink, false)]
    [InlineData(TransferMode.SymbolicLink, false)]
    [InlineData(TransferMode.Zip, false)]
    [InlineData(TransferMode.Copy, true)]
    [InlineData(TransferMode.Zip, true)]
    public async Task CraftedArchive_ShouldWriteNothingForTheMembersLeadingOutOfTheirFolder(TransferMode mode, bool isDryRun)
    {
        var crafted = CraftedMembers();
        CreateZip("crafted.zip", [AlphaName, .. crafted]);
        var logs = new LogCollector();

        var result = await _processor.ExecuteAsync(Config(mode), isDryRun, logProgress: logs);

        FilesOutsideTheDestination().Should().BeEmpty();
        var refused = result.Items.Where(it => it.ArchiveMember != AlphaName).ToList();
        refused.Select(it => it.ArchiveMember).Should().BeEquivalentTo(crafted);
        refused.Should().OnlyContain(it =>
            it.Status == FileStatus.Error && it.StatusReason!.Key == "reason.unsafe_name" && it.FinalTargetPath == null);
        refused.Select(it => it.StatusReason!.Args["name"]).Should().BeEquivalentTo(crafted);
        result.Items.Single(it => it.ArchiveMember == AlphaName).Status
            .Should().Be(FileStatus.Valid, "the other members of the archive are sorted as usual");
        result.Stats.ErrorCount.Should().Be(crafted.Length);
        result.Stats.ValidCount.Should().Be(1);
        result.Stats.UnmatchedCount.Should().Be(0);
        logs.Messages.Where(m => m.Message.Key == "log.unsafe_name")
            .Should().HaveCount(crafted.Length).And.OnlyContain(m => m.Level == LogLevel.Error);

        if (!isDryRun)
        {
            DestinationFiles().Should().Equal(mode == TransferMode.Zip ? "Alpha/Alpha (1990)(Ocean).zip" : $"Alpha/{AlphaName}");
            var journal = await File.ReadAllTextAsync(result.JournalPath!);
            journal.Should().NotContain("escaped").And.NotContain("rooted").And.NotContain("outside.txt");
        }
    }

    [Fact]
    public async Task MemberNamedWithANullCharacter_ShouldBeRefusedWithoutStoppingTheSort()
    {
        CreateZip("crafted.zip", AlphaName, "null\0char (1990)(Pub).adf");

        var result = await _processor.ExecuteAsync(Config(), isDryRun: false);

        result.Items.Single(it => it.ArchiveMember != AlphaName).StatusReason!.Key.Should().Be("reason.unsafe_name");
        DestinationFiles().Should().Equal($"Alpha/{AlphaName}");
        FilesOutsideTheDestination().Should().BeEmpty();
    }

    [Fact]
    public async Task DatRomNames_LeadingOutOfTheirFolder_ShouldWriteNothingForTheirFiles()
    {
        // The DAT renames each file it identifies: two of its names lead out of their folder
        var games = new (string File, string RomName)[]
        {
            ("a (1990)(Pub).adf", "../../Escaped (1990)(Pub).adf"),
            ("b (1990)(Pub).adf", @"..\..\Backslash (1990)(Pub).adf"),
            ("c (1990)(Pub).adf", "Kept (1990)(Pub).adf")
        };
        var roms = new StringBuilder();
        foreach (var (file, romName) in games)
        {
            var data = Encoding.UTF8.GetBytes("content of " + file);
            _temp.WriteBytes(Path.Combine("src", file), data);
            var title = Path.GetFileNameWithoutExtension(romName.Replace('\\', '/').Split('/')[^1]);
            roms.Append($"""<game name="{title}"><description>{title}</description><rom name="{romName}" size="{data.Length}" crc="{Crc32.HashToUInt32(data):x8}"/></game>""");
        }

        var datPath = _temp.WriteText(Path.Combine("dats", "crafted.dat"), $"""
            <?xml version="1.0"?>
            <datafile><header><name>Commodore Amiga - Games - [ADF] (TOSEC)</name><description>Crafted</description></header>{roms}</datafile>
            """);

        var result = await _processor.ExecuteAsync(Config(datFile: datPath), isDryRun: false);

        FilesOutsideTheDestination().Should().BeEmpty();
        DestinationFiles().Should().Equal("Kept/Kept (1990)(Pub).adf");
        result.Items.Where(it => it.TargetFilename != "Kept (1990)(Pub).adf").Should().HaveCount(2).And.OnlyContain(it =>
            it.Status == FileStatus.Error && it.StatusReason!.Key == "reason.unsafe_name");
    }

    [Fact]
    public async Task CompanionNamedOutOfItsFolderByTheDat_ShouldLeaveTheWholeGameUnwritten()
    {
        const string cueName = "Disc (1995)(Sony).cue";
        const string trackName = "Disc (1995)(Sony) (Track 1).bin";
        var cue = Encoding.UTF8.GetBytes($"FILE \"{trackName}\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n");
        var track = Encoding.UTF8.GetBytes("TRACK1-DATA");
        _temp.WriteBytes(Path.Combine("src", cueName), cue);
        _temp.WriteBytes(Path.Combine("src", trackName), track);
        var datPath = _temp.WriteText(Path.Combine("dats", "crafted.dat"), $"""
            <?xml version="1.0"?>
            <datafile>
              <header><name>Sony PlayStation - Games - [BIN] (TOSEC)</name><description>Crafted</description></header>
              <game name="Disc (1995)(Sony)"><description>Disc</description>
                <rom name="{cueName}" size="{cue.Length}" crc="{Crc32.HashToUInt32(cue):x8}"/>
                <rom name="../../{trackName}" size="{track.Length}" crc="{Crc32.HashToUInt32(track):x8}"/>
              </game>
            </datafile>
            """);

        var result = await _processor.ExecuteAsync(Config(datFile: datPath), isDryRun: false);

        FilesOutsideTheDestination().Should().BeEmpty();
        DestinationFiles().Should().BeEmpty("a game is written whole or not at all");
        var descriptor = result.Items.Should().ContainSingle().Subject;
        descriptor.Status.Should().Be(FileStatus.Error);
        descriptor.StatusReason.Should().Be(LocalizedMessage.Create("reason.unsafe_name", ("name", $"../../{trackName}")));
        descriptor.RelatedItems.Should().ContainSingle().Which.Status.Should().Be(FileStatus.Error);
        File.Exists(Path.Combine(_src, trackName)).Should().BeTrue();
    }

    // =========================================================================
    // Last check before every write
    // =========================================================================

    private FileTransferExecutor CreateExecutor(SortConfig config, bool isDryRun = false) =>
        new(config, isDryRun, _services.Organizer, _services.FilterService, _services.I18n,
            _services.OpticalCompanionService, _services.ChecksumService, (level, message) => _executorLogs.Add(new LogMessage(level, message)));

    [Theory]
    [InlineData(TransferMode.Copy, false, false)]
    [InlineData(TransferMode.Move, false, false)]
    [InlineData(TransferMode.HardLink, false, false)]
    [InlineData(TransferMode.SymbolicLink, false, false)]
    [InlineData(TransferMode.Copy, true, false)]
    [InlineData(TransferMode.Copy, false, true)]
    public async Task Transfer_ToATargetOutsideTheDestination_ShouldWriteNothing(TransferMode mode, bool fromArchive, bool isDryRun)
    {
        var source = fromArchive ? CreateZip("game.zip", AlphaName) : _temp.WriteText(Path.Combine("src", AlphaName), "alpha");
        var target = _temp.GetFilePath(AlphaName);
        var item = new ProcessItem
        {
            SourcePath = source,
            FileSize = new FileInfo(source).Length,
            Parsed = _services.TosecParser.ParseFilename(AlphaName),
            ArchivePath = fromArchive ? source : null,
            ArchiveMember = fromArchive ? AlphaName : null,
            FinalTargetPath = target
        };

        await CreateExecutor(Config(mode), isDryRun).PerformTransferAsync(item);

        File.Exists(target).Should().BeFalse();
        File.Exists(source).Should().BeTrue("the source is left as it is, even for a move");
        item.Status.Should().Be(FileStatus.Error, "a simulation shows it as well");
        item.StatusReason.Should().Be(LocalizedMessage.Create("reason.target_outside_destination", ("target", target)));
        _executorLogs.Should().ContainSingle(m => m.Message.Key == "log.target_outside_destination" && m.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Playlist_OutsideTheDestination_ShouldNotBeWritten()
    {
        var config = Config();
        config.GenerateM3u = true;
        config.SubfolderMultidisk = false;
        config.M3uPlacement = M3uPlacementMode.Subfolder;
        var disks = Enumerable.Range(1, 2)
            .Select(i => $"Game (1990)(Pub)(Disk {i} of 2).adf")
            .Select(name => new ProcessItem
            {
                SourcePath = Path.Combine(_src, name),
                FileSize = 0,
                Parsed = _services.TosecParser.ParseFilename(name),
                FinalTargetPath = _temp.GetFilePath(name)
            })
            .ToList();

        await CreateExecutor(config).GenerateM3uPlaylistsAsync([disks], new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        FilesOutsideTheDestination().Should().BeEmpty();
        _executorLogs.Should().ContainSingle(m => m.Message.Key == "log.target_outside_destination" && m.Level == LogLevel.Error);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../../evil")]
    [InlineData("..\\..\\evil")]
    [InlineData("C:evil")]
    [InlineData("/evil")]
    public async Task DatOfALot_NamedOutOfTheDestination_ShouldKeepItsFilesInside(string datName)
    {
        var data = Encoding.ASCII.GetBytes("LOT GAME");
        var crc = Crc32.HashToUInt32(data).ToString("x8");
        foreach (var (file, name) in new[] { ("a.dat", datName), ("b.dat", "Other") })
        {
            _temp.WriteText(Path.Combine("dats", file), $"""
                <?xml version="1.0"?>
                <datafile>
                	<header><name>{System.Security.SecurityElement.Escape(name)}</name><homepage>No-Intro</homepage></header>
                	<game name="Game (World)"><description>Game (World)</description><rom name="Game (World).bin" size="{data.Length}" crc="{crc}"/></game>
                </datafile>
                """);
        }

        _temp.WriteBytes(Path.Combine("src", "game.bin"), data);
        var config = Config(TransferMode.Copy, _temp.GetFilePath("dats"));
        config.DatFolderMirror = true;
        config.Pattern = "{dat}";

        var result = await _processor.ExecuteAsync(config, isDryRun: false);

        var item = result.Items.Single();
        item.DatName.Should().Be(datName.Trim());
        item.Status.Should().Be(FileStatus.Valid);
        PathOrganizerService.IsInsideDirectory(item.FinalTargetPath!, _dst).Should().BeTrue();
        FilesOutsideTheDestination().Should().BeEmpty();
        DestinationFiles().Should().ContainSingle();
    }

    [Theory]
    [InlineData("../..", "../../x")]
    [InlineData("..\\..", "..\\..\\x")]
    [InlineData("C:evil", "C:/up")]
    [InlineData("/evil", "/root/x")]
    public void FolderOfADat_ShouldBeSanitizedSegments(string datName, string relativeFolder)
    {
        var config = Config();
        config.DatFolderMirror = true;
        config.Pattern = string.Empty;
        var parsed = _services.TosecParser.ParseFilename(AlphaName);

        var target = _services.Organizer.BuildTargetPath(parsed, AlphaName, config, dat: new DatPlacement(datName, relativeFolder, IsLotMember: true));

        PathOrganizerService.IsInsideDirectory(target, _dst).Should().BeTrue(target);
        Path.GetRelativePath(_dst, target).Split(Path.DirectorySeparatorChar).Should().NotContain("..");
    }
}
