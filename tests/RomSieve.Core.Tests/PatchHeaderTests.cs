using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using static RomSieve.Core.Tests.Fixtures.PatchTestEncoder;

namespace RomSieve.Core.Tests;

/// <summary>
/// The copier header of the origin written back in front of the result of a patch made for the data without it
/// (<see cref="PatchedVersion.RestoredHeaderSize"/>): iNES and Lynx get theirs back, the SNES never, a patch made for the
/// whole file is written as it produces it. The origin is identified without its header (a DAT of the data) or whole (a
/// DAT of the files with their header). Checked in Copy and ZIP modes, from an archive, with the verification of the
/// copies, the journal and its undo, the CRC32 and the size known by the simulation (room, FAT32, the DAT of the
/// collection), a header changed since the matching, and the warning of a patch that changes the size of the data.
/// </summary>
public sealed class PatchHeaderTests : IDisposable
{
    private readonly CoreTestServices _services = new();
    private readonly TempDirectoryFixture _temp = new();
    private readonly CrcCacheService _crcCache;
    private readonly OperationJournalService _journal;
    private readonly SortProcessorService _processor;
    private readonly string _src;
    private readonly string _dst;
    private readonly string _patches;

    public PatchHeaderTests()
    {
        _crcCache = new CrcCacheService(_temp.GetFilePath(Path.Combine("cache", "crc_cache.json")));
        _journal = new OperationJournalService(_temp.GetFilePath("journals"), _services.ChecksumService, _crcCache);
        _processor = new SortProcessorService(
            _services.ChecksumService, _services.DatService, _services.NoIntroParser, _services.FilterService,
            _services.Organizer, _services.I18n, _services.OpticalCompanionService, _services.MameParser, _services.Patches,
            _crcCache, _services.PatchLinks, _journal);
        _src = _temp.CreateSubdirectory("src");
        _dst = _temp.CreateSubdirectory("dst");
        _patches = _temp.CreateSubdirectory("patches");
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>A copier header: its size, its file extension, and how it is put in front of the data.</summary>
    public enum Kind
    {
        Ines,
        Lynx,
        Snes
    }

    private static byte[] Data(int seed, int size)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>The data of a "hack": a few bytes changed, the size kept.</summary>
    private static byte[] Hack(byte[] data)
    {
        var hacked = (byte[])data.Clone();
        hacked[10] ^= 0x55;
        hacked[data.Length - 1] ^= 0x33;
        return hacked;
    }

    /// <summary>The header of each kind, with values in every field: it must come back byte for byte.</summary>
    private static byte[] HeaderOf(Kind kind) => kind switch
    {
        Kind.Ines => [.. Ascii("NES\u001a"), 0x02, 0x01, 0x01, 0x08, 0x00, 0x00, 0x70, 0x07, 0x02, 0x00, 0x00, 0x01],
        Kind.Lynx => [.. Ascii("LYNX"), 0x00, 0x02, 0x00, 0x00, 0x01, 0x00, .. Ascii("Game".PadRight(32, '\0')), .. Ascii("Maker".PadRight(16, '\0')), 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
        _ => new byte[512]
    };

    private static string Extension(Kind kind) => kind switch
    {
        Kind.Ines => ".nes",
        Kind.Lynx => ".lnx",
        _ => ".sfc"
    };

    /// <summary>The data of each kind: SNES data of a size a 512-byte header is recognised in front of.</summary>
    private static byte[] DataOf(Kind kind) => Data((int)kind + 3, kind == Kind.Snes ? 8192 : 4096);

    private string WriteDat(string rom, byte[] data) =>
        _temp.WriteText(
            "dat/console.dat",
            $"""<?xml version="1.0"?><datafile><header><name>Console</name><homepage>No-Intro</homepage></header><game name="Game (USA)"><description>Game (USA)</description><rom name="{rom}" size="{data.Length}" crc="{Crc(data):x8}"/></game></datafile>""");

    /// <summary>A ROM of the source with its header, and a DAT of its data only (it is identified without its header).</summary>
    private (string Dat, byte[] File, byte[] Data) WriteHeaderedRom(Kind kind)
    {
        var data = DataOf(kind);
        byte[] file = [.. HeaderOf(kind), .. data];
        _temp.WriteBytes("src/Game (USA)" + Extension(kind), file);
        return (WriteDat("Game (USA)" + Extension(kind), data), file, data);
    }

    private SortConfig Config(string dat, TransferMode mode = TransferMode.Copy) => new()
    {
        SourceDir = _src,
        DestinationDir = _dst,
        Pattern = string.Empty,
        TransferMode = mode,
        CollisionMode = CollisionMode.Rename,
        AppendToDestination = true,
        NamingConvention = NamingConvention.NoIntro,
        DatFile = dat,
        PatchDir = _patches
    };

    private sealed class LogCollector : IProgress<LogMessage>
    {
        public List<LogMessage> Messages { get; } = [];
        public void Report(LogMessage value) => Messages.Add(value);
    }

    private Task<SortExecutionResult> SortAsync(SortConfig config, LogCollector? log = null) =>
        _processor.ExecuteAsync(config, isDryRun: false, logProgress: log);

    private Task<SortExecutionResult> SimulateAsync(SortConfig config, LogCollector? log = null) =>
        _processor.ExecuteAsync(config, isDryRun: true, logProgress: log);

    private static ProcessItem PatchedVersion(SortExecutionResult result) => result.Items.Single(it => it.Patch is not null);

    private string Destination(string name) => Path.Combine(_dst, name);

    private List<string> DestinationFiles() =>
        Directory.EnumerateFiles(_dst, "*", SearchOption.AllDirectories)
            .Where(f => !DestinationGuard.IsGuardPath(f))
            .Select(f => Path.GetRelativePath(_dst, f))
            .Order(StringComparer.Ordinal)
            .ToList();

    private void AssertNoTemporaryFile() =>
        Directory.EnumerateFiles(_temp.Path, "*" + DestinationGuard.ReplacementExtension, SearchOption.AllDirectories)
            .Should().BeEmpty("no temporary file is left behind");

    // =========================================================================
    // Which header comes back
    // =========================================================================

    [Theory]
    [InlineData(Kind.Ines)]
    [InlineData(Kind.Lynx)]
    public async Task APatchOfTheDataWithoutHeader_ShouldWriteTheHeaderOfItsOriginInFrontOfTheResult(Kind kind)
    {
        var (dat, _, data) = WriteHeaderedRom(kind);
        var hacked = Hack(data);
        _temp.WriteBytes("patches/Game (USA) (Hack).bps", BpsLinear(data, hacked));
        byte[] expected = [.. HeaderOf(kind), .. hacked];

        var result = await SortAsync(Config(dat));

        var patched = PatchedVersion(result);
        patched.Status.Should().Be(FileStatus.Valid);
        patched.Patch!.SourceOffset.Should().Be(HeaderOf(kind).Length);
        patched.Patch.RestoredHeaderSize.Should().Be(HeaderOf(kind).Length);
        File.ReadAllBytes(Destination("Game (USA) [Hack]" + Extension(kind))).Should().Equal(expected, "the header the patch skips comes first");
        patched.FileSize.Should().Be(expected.Length);
        patched.Crc32.Should().Be(Crc(expected).ToString("x8"));
        AssertNoTemporaryFile();
    }

    [Theory]
    [InlineData(Kind.Ines)]
    [InlineData(Kind.Lynx)]
    [InlineData(Kind.Snes)]
    public async Task APatchOfTheWholeFile_ShouldBeWrittenAsItProducesIt(Kind kind)
    {
        var (dat, file, _) = WriteHeaderedRom(kind);
        var hacked = Hack(file);
        _temp.WriteBytes("patches/Game (USA) (Hack).ups", Ups(file, hacked));

        var result = await SortAsync(Config(dat));

        var patched = PatchedVersion(result);
        patched.Patch!.SourceOffset.Should().Be(0);
        patched.Patch.RestoredHeaderSize.Should().Be(0);
        File.ReadAllBytes(Destination("Game (USA) [Hack]" + Extension(kind))).Should().Equal(hacked, "the patch already wrote a header, never a second one");
    }

    [Fact]
    public async Task APatchOfTheSnesDataWithoutHeader_ShouldGiveTheDataAlone()
    {
        var (dat, _, data) = WriteHeaderedRom(Kind.Snes);
        var hacked = Hack(data);
        _temp.WriteBytes("patches/Game (USA) (Hack).bps", BpsLinear(data, hacked));

        var result = await SortAsync(Config(dat));

        var patched = PatchedVersion(result);
        patched.Patch!.SourceOffset.Should().Be(512);
        patched.Patch.RestoredHeaderSize.Should().Be(0, "a SNES ROM is played without its copier header");
        File.ReadAllBytes(Destination("Game (USA) [Hack].sfc")).Should().Equal(hacked);
    }

    [Theory]
    [InlineData(Kind.Ines)]
    [InlineData(Kind.Lynx)]
    public async Task AnOriginIdentifiedWhole_ShouldAlsoGetItsHeaderBack(Kind kind)
    {
        // A DAT of the files with their header: the origin is identified whole, the patch of its data found afterwards
        var data = DataOf(kind);
        byte[] file = [.. HeaderOf(kind), .. data];
        _temp.WriteBytes("src/Game (USA)" + Extension(kind), file);
        var dat = WriteDat("Game (USA)" + Extension(kind), file);
        var hacked = Hack(data);
        _temp.WriteBytes($"patches/Game (USA) (Hack) [{Crc(data):x8}].ips", IpsDiff(data, hacked));

        var result = await SortAsync(Config(dat));

        var patched = PatchedVersion(result);
        patched.Patch!.Origin.CopierHeaderSize.Should().Be(0, "the DAT gives the whole file");
        patched.Patch.SourceOffset.Should().Be(HeaderOf(kind).Length);
        File.ReadAllBytes(Destination("Game (USA) [Hack]" + Extension(kind))).Should().Equal([.. HeaderOf(kind), .. hacked]);
    }

    // =========================================================================
    // Modes, archives, journal
    // =========================================================================

    [Fact]
    public async Task Zip_ShouldHoldTheResultWithItsHeader()
    {
        var (dat, _, data) = WriteHeaderedRom(Kind.Ines);
        var hacked = Hack(data);
        _temp.WriteBytes("patches/Game (USA) (Hack).bps", BpsLinear(data, hacked));
        byte[] expected = [.. HeaderOf(Kind.Ines), .. hacked];

        var result = await SortAsync(Config(dat, TransferMode.Zip));

        PatchedVersion(result).Status.Should().Be(FileStatus.Valid);
        PatchedVersion(result).Crc32.Should().Be(Crc(expected).ToString("x8"));
        using (var zip = ZipFile.OpenRead(Destination("Game (USA) [Hack].zip")))
        {
            var entry = zip.Entries.Should().ContainSingle().Which;
            entry.Length.Should().Be(expected.Length);
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            memory.ToArray().Should().Equal(expected);
        }

        AssertNoTemporaryFile();
        (await _journal.UndoAsync(result.JournalPath!)).Issues.Should().BeEmpty();
        DestinationFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task AnOriginInAnArchive_ShouldGetItsHeaderBack()
    {
        // A ROM with its header in a ZIP is identified by a DAT of the whole files; the patch is made for its data
        var data = DataOf(Kind.Ines);
        byte[] file = [.. HeaderOf(Kind.Ines), .. data];
        var zip = _temp.GetFilePath("src/Game (USA).zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var stream = archive.CreateEntry("Game (USA).nes").Open();
            stream.Write(file);
        }

        var dat = WriteDat("Game (USA).nes", file);
        var hacked = Hack(data);
        _temp.WriteBytes("patches/Game (USA) (Hack).bps", BpsLinear(data, hacked));

        var result = await SortAsync(Config(dat));

        PatchedVersion(result).Status.Should().Be(FileStatus.Valid);
        File.ReadAllBytes(Destination("Game (USA) [Hack].nes")).Should().Equal([.. HeaderOf(Kind.Ines), .. hacked]);
        AssertNoTemporaryFile();
    }

    [Fact]
    public async Task TheJournal_ShouldRecordTheFileWritten_AndTheUndoRemoveIt()
    {
        var (dat, _, data) = WriteHeaderedRom(Kind.Lynx);
        var hacked = Hack(data);
        _temp.WriteBytes("patches/Game (USA) (Hack).bps", BpsLinear(data, hacked));
        byte[] expected = [.. HeaderOf(Kind.Lynx), .. hacked];
        var config = Config(dat);
        config.VerifyCopies = true;

        var result = await SortAsync(config);

        PatchedVersion(result).Status.Should().Be(FileStatus.Valid, "the verification reads back the header with the data");
        var line = File.ReadAllLines(result.JournalPath!).Single(l => l.Contains("\"patch\":", StringComparison.Ordinal));
        line.Should().Contain($"\"size\":{expected.Length}").And.Contain(Crc(expected).ToString("x8"));
        (await _journal.UndoAsync(result.JournalPath!)).Issues.Should().BeEmpty();
        File.Exists(Destination("Game (USA) [Hack].lnx")).Should().BeFalse();
    }

    // =========================================================================
    // What the simulation knows
    // =========================================================================

    [Fact]
    public async Task TheSimulation_ShouldKnowTheSizeAndTheCrc32OfTheFileWritten()
    {
        var (dat, _, data) = WriteHeaderedRom(Kind.Ines);
        var hacked = Hack(data);
        _temp.WriteBytes("patches/Game (USA) (Hack).bps", BpsLinear(data, hacked));
        _temp.WriteBytes($"patches/Game (USA) (Other) [{Crc(data):x8}].ips", IpsDiff(data, Hack(hacked)));
        byte[] expected = [.. HeaderOf(Kind.Ines), .. hacked];
        var config = Config(dat, TransferMode.Move);

        var simulation = await SimulateAsync(config);

        var bps = simulation.Items.Single(it => it.Patch?.Format == PatchFormat.Bps);
        bps.FileSize.Should().Be(expected.Length);
        bps.Crc32.Should().Be(Crc(expected).ToString("x8"), "the CRC32 of the header is combined with the one the BPS records");
        var ips = simulation.Items.Single(it => it.Patch?.Format == PatchFormat.Ips);
        ips.FileSize.Should().Be(expected.Length);
        ips.Crc32.Should().BeEmpty("an IPS records no CRC32 of its result");

        // Same volume: the origin is only renamed, the two patched versions are written
        DestinationSpace.GetRequiredBytes(simulation.Items, config, [Path.GetPathRoot(_temp.Path)!])
            .Should().Be(2L * expected.Length);
    }

    [Fact]
    public async Task TheDatOfTheCollection_ShouldDescribeTheFileWithItsHeader()
    {
        var (dat, _, data) = WriteHeaderedRom(Kind.Ines);
        var hacked = Hack(data);
        _temp.WriteBytes($"patches/Game (USA) (Hack) [{Crc(data):x8}].ips", IpsDiff(data, hacked));
        byte[] expected = [.. HeaderOf(Kind.Ines), .. hacked];
        var result = await SimulateAsync(Config(dat));
        var output = _temp.GetFilePath("collection.xml");

        await _processor.ExportDatAsync(result.Items, output, DatExportFormat.LogiqxXml, result.DatCatalog);

        var rom = XDocument.Load(output).Descendants("rom").Single(r => (string?)r.Attribute("name") == "Game (USA) [Hack].nes");
        ((string?)rom.Attribute("size")).Should().Be(expected.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ((string?)rom.Attribute("crc")).Should().Be(Crc(expected).ToString("x8"));
        ((string?)rom.Attribute("sha1")).Should().Be(Convert.ToHexStringLower(SHA1.HashData(expected)));
    }

    [Fact]
    public async Task TheDatOfTheCollection_ShouldReadTheFileASortWrote()
    {
        var (dat, _, data) = WriteHeaderedRom(Kind.Lynx);
        var hacked = Hack(data);
        _temp.WriteBytes("patches/Game (USA) (Hack).bps", BpsLinear(data, hacked));
        byte[] expected = [.. HeaderOf(Kind.Lynx), .. hacked];
        var result = await SortAsync(Config(dat));
        var output = _temp.GetFilePath("collection.xml");

        await _processor.ExportDatAsync(result.Items, output, DatExportFormat.LogiqxXml, result.DatCatalog);

        var rom = XDocument.Load(output).Descendants("rom").Single(r => (string?)r.Attribute("name") == "Game (USA) [Hack].lnx");
        ((string?)rom.Attribute("sha1")).Should().Be(Convert.ToHexStringLower(SHA1.HashData(expected)));
    }

    // =========================================================================
    // Size changed, header changed
    // =========================================================================

    [Fact]
    public async Task APatchThatChangesTheSizeOfTheData_ShouldKeepTheHeaderAsItIs_AndSayIt()
    {
        var (dat, _, data) = WriteHeaderedRom(Kind.Ines);
        byte[] grown = [.. Hack(data), .. Data(9, 1024)];
        _temp.WriteBytes("patches/Game (USA) (Big).bps", BpsLinear(data, grown));
        var log = new LogCollector();

        var result = await SortAsync(Config(dat), log);

        File.ReadAllBytes(Destination("Game (USA) [Big].nes")).Should().Equal([.. HeaderOf(Kind.Ines), .. grown]);
        var warning = log.Messages.Should().ContainSingle(m => m.Message.Key == "log.patched_header_size_changed").Which;
        warning.Level.Should().Be(LogLevel.Warn);
        warning.Message.Args["name"].Should().Be("Game (USA) [Big].nes");
        warning.Message.Args["before"].Should().Be((long)data.Length);
        warning.Message.Args["after"].Should().Be((long)grown.Length);
        PatchedVersion(result).Status.Should().Be(FileStatus.Valid);
    }

    [Fact]
    public async Task APatchThatKeepsTheSizeOfTheData_ShouldSayNothing()
    {
        var (dat, _, data) = WriteHeaderedRom(Kind.Ines);
        _temp.WriteBytes("patches/Game (USA) (Hack).bps", BpsLinear(data, Hack(data)));
        var log = new LogCollector();

        await SimulateAsync(Config(dat), log);

        log.Messages.Should().NotContain(m => m.Message.Key == "log.patched_header_size_changed");
    }

    [Fact]
    public async Task AHeaderChangedSinceTheMatching_ShouldPutThePatchedVersionInErrorWithNothingWritten()
    {
        var (dat, _, data) = WriteHeaderedRom(Kind.Ines);
        _temp.WriteBytes("patches/Game (USA) (Hack).bps", BpsLinear(data, Hack(data)));
        _processor.ConfigureTransferExecutor = executor => executor.WrapSourceStream =
            stream => stream is FileStream file && file.Name.EndsWith("Game (USA).nes", StringComparison.Ordinal) ? new HeaderChangingStream(stream) : stream;

        var result = await SortAsync(Config(dat));

        var patched = PatchedVersion(result);
        patched.Status.Should().Be(FileStatus.Error);
        patched.StatusReason!.Args["error"].Should().BeOfType<LocalizedMessage>().Which.Key.Should().Be("patch.error_header_changed");
        File.Exists(Destination("Game (USA) [Hack].nes")).Should().BeFalse();
        AssertNoTemporaryFile();
    }

    [Fact]
    public async Task AWrongResultBehindAHeader_ShouldPutThePatchedVersionInErrorWithNothingWritten()
    {
        var (dat, _, data) = WriteHeaderedRom(Kind.Ines);
        _temp.WriteBytes("patches/Game (USA) (Hack).bps", Bps(
            (ulong)data.Length, (ulong)data.Length, [new SourceRead(data.Length)], Crc(data), targetCrc: 0x12345678));

        var result = await SortAsync(Config(dat));

        PatchedVersion(result).StatusReason!.Args["error"].Should().BeOfType<LocalizedMessage>().Which.Key.Should().Be("patch.error_target_crc");
        File.Exists(Destination("Game (USA) [Hack].nes")).Should().BeFalse();
        AssertNoTemporaryFile();
    }

    /// <summary>A source whose header changed: the 6th byte (the CHR size of an iNES header) reads back otherwise.</summary>
    private sealed class HeaderChangingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var start = inner.Position;
            var read = inner.Read(buffer);
            Change(buffer[..read], start);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var start = inner.Position;
            var read = await inner.ReadAsync(buffer, cancellationToken);
            Change(buffer.Span[..read], start);
            return read;
        }

        private static void Change(Span<byte> read, long start)
        {
            if (start <= 5 && start + read.Length > 5)
            {
                read[(int)(5 - start)] ^= 0x01;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
