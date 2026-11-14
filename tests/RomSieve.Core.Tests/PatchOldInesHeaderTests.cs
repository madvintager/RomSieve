using System.IO.Compression;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using static RomSieve.Core.Tests.Fixtures.PatchTestEncoder;

namespace RomSieve.Core.Tests;

/// <summary>
/// The NES files under an old iNES 1.0 header (<see cref="PatchOriginForms.OldInesHeader"/>): the patches and readmes
/// of older dumps give the checksums of the whole file with that header, which no current DAT nor file has any more
/// (they have NES 2.0 headers). That form is found for a UPS, a BPS, an IPS by its name, its readme or the choice of the
/// user, from a DAT of the data or of the whole files, on the disk or in an archive, and the patch reads the origin
/// under it; a readme of the former format of romhacking.net (old file and data) is refused with both forms proposed,
/// the whole one first; the forms of the window; and a NES ROM identified without its header proposed in its whole form.
/// </summary>
public sealed class PatchOldInesHeaderTests : IDisposable
{
    private readonly CoreTestServices _services = new();
    private readonly TempDirectoryFixture _temp = new();
    private readonly CrcCacheService _crcCache;
    private readonly OperationJournalService _journal;
    private readonly SortProcessorService _processor;
    private readonly string _src;
    private readonly string _dst;
    private readonly string _patches;

    private static readonly byte[] Data = Random(1, 8192);

    /// <summary>A NES 2.0 header, as the current DATs give it: the mark of NES 2.0 in byte 7, fields in bytes 8 to 15.</summary>
    private static readonly byte[] Nes2Header = [.. Ascii("NES\u001a"), 0x02, 0x01, 0x01, 0x08, 0x00, 0x00, 0x70, 0x07, 0x02, 0x00, 0x00, 0x01];

    /// <summary>The same header as an older dump had it: bytes 0 to 6, byte 7 without the mark, zeros after.</summary>
    private static readonly byte[] Ines1Header = [.. Ascii("NES\u001a"), 0x02, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    private static readonly byte[] File2 = [.. Nes2Header, .. Data];
    private static readonly byte[] File1 = [.. Ines1Header, .. Data];

    public PatchOldInesHeaderTests()
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

    private static byte[] Random(int seed, int size)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static byte[] Hack(byte[] data)
    {
        var hacked = (byte[])data.Clone();
        hacked[100] ^= 0x55;
        hacked[^1] ^= 0x33;
        return hacked;
    }

    /// <summary>A DAT of the data without header (<c>.unh</c>), or of the whole files with their NES 2.0 header.</summary>
    private string WriteDat(bool wholeFiles)
    {
        var (rom, data) = wholeFiles ? ("Game (USA).nes", File2) : ("Game (USA).unh", Data);
        return _temp.WriteText(
            "dat/nes.dat",
            $"""<?xml version="1.0"?><datafile><header><name>Nintendo - Nintendo Entertainment System</name><homepage>No-Intro</homepage></header><game name="Game (USA)"><description>Game (USA)</description><rom name="{rom}" size="{data.Length}" crc="{Crc(data):x8}"/></game></datafile>""");
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

    private Task<SortExecutionResult> SortAsync(SortConfig config) => _processor.ExecuteAsync(config, isDryRun: false);

    private Task<SortExecutionResult> SimulateAsync(SortConfig config) => _processor.ExecuteAsync(config, isDryRun: true);

    private static ProcessItem PatchedVersion(SortExecutionResult result) => result.Items.Single(it => it.Patch is not null);

    private string Destination(string name) => Path.Combine(_dst, name);

    // =========================================================================
    // The form under an old header
    // =========================================================================

    [Fact]
    public void TheOldHeader_ShouldDropTheFieldsOfNes2()
    {
        PatchOriginForms.OldInesHeader(Nes2Header).Should().Equal(Ines1Header);
        PatchOriginForms.OldInesHeader(Ines1Header).Should().Equal(Ines1Header, "an iNES 1.0 header is already one");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABpsOfTheFileUnderAnOldHeader_ShouldBeApplied(bool datOfWholeFiles)
    {
        _temp.WriteBytes("src/Game (USA).nes", File2);
        var hacked = Hack(File1);
        _temp.WriteBytes("patches/Game (USA) (Hack).bps", BpsLinear(File1, hacked));

        var result = await SortAsync(Config(WriteDat(datOfWholeFiles)));

        var patched = PatchedVersion(result);
        patched.Status.Should().Be(FileStatus.Valid);
        patched.Patch!.UsesOldInesHeader.Should().BeTrue();
        patched.Patch.SourceOffset.Should().Be(0);
        patched.Patch.SourceCrc32.Should().Be(Crc(File1));
        patched.Patch.RestoredHeaderSize.Should().Be(0, "the patch read the whole file, header included");
        var name = "Game (USA) [Hack]" + (datOfWholeFiles ? ".nes" : ".unh");
        File.ReadAllBytes(Destination(name)).Should().Equal(hacked, "the result is written as the patch produces it");
        File.ReadAllBytes(Path.Combine(_src, "Game (USA).nes")).Should().Equal(File2, "the origin is never modified");
        (await _journal.UndoAsync(result.JournalPath!)).Issues.Should().BeEmpty();
        File.Exists(Destination(name)).Should().BeFalse();
    }

    [Fact]
    public async Task AnIpsWhoseNameGivesTheCrc32OfTheOldFile_ShouldBeApplied()
    {
        _temp.WriteBytes("src/Game (USA).nes", File2);
        var hacked = Hack(File1);
        _temp.WriteBytes($"patches/Game (USA) (Hack) [{Crc(File1):x8}].ips", IpsDiff(File1, hacked));

        var result = await SortAsync(Config(WriteDat(wholeFiles: false)));

        PatchedVersion(result).Patch!.UsesOldInesHeader.Should().BeTrue();
        File.ReadAllBytes(Destination("Game (USA) [Hack].unh")).Should().Equal(hacked);
    }

    [Fact]
    public async Task AnIpsWhoseReadmeGivesTheCrc32OfTheOldFile_ShouldBeApplied()
    {
        _temp.WriteBytes("src/Game (USA).nes", File2);
        _temp.WriteBytes("patches/Hack/hack.ips", IpsDiff(File1, Hack(File1)));
        _temp.WriteText("patches/Hack/readme.txt", $"Game (USA).nes\n\tCRC32: {Crc(File1):X8}\n");

        var result = await SimulateAsync(Config(WriteDat(wholeFiles: false)));

        var version = PatchedVersion(result).Patch!;
        version.UsesOldInesHeader.Should().BeTrue();
        version.CrcSource.Should().Be(PatchCrcSource.Notes);
        result.UnappliedPatches.Should().BeEmpty();
    }

    [Fact]
    public async Task AnOriginInAnArchive_ShouldBeReadUnderItsOldHeader()
    {
        var zip = _temp.GetFilePath("src/Game (USA).zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var stream = archive.CreateEntry("Game (USA).nes").Open();
            stream.Write(File2);
        }

        var hacked = Hack(File1);
        _temp.WriteBytes("patches/Game (USA) (Hack).ups", Ups(File1, hacked));

        var result = await SortAsync(Config(WriteDat(wholeFiles: true), TransferMode.Zip));

        PatchedVersion(result).Status.Should().Be(FileStatus.Valid);
        using var written = ZipFile.OpenRead(Destination("Game (USA) [Hack].zip"));
        using var entry = written.Entries.Single().Open();
        using var memory = new MemoryStream();
        entry.CopyTo(memory);
        memory.ToArray().Should().Equal(hacked);
    }

    [Fact]
    public async Task TheChoiceOfTheUser_ShouldReachTheOldForm()
    {
        _temp.WriteBytes("src/Game (USA).nes", File2);
        var patch = IpsDiff(File1, Hack(File1));
        _temp.WriteBytes("patches/hack.ips", patch);
        await _services.PatchLinks.UpdateAsync(
            [new(PatchKey.Of(patch), PatchLink.ToOrigin("hack.ips", Crc(File1), "Game (USA).unh"))],
            (_, message) => throw new InvalidOperationException(message.Key));

        var result = await SimulateAsync(Config(WriteDat(wholeFiles: false)));

        var version = PatchedVersion(result).Patch!;
        version.CrcSource.Should().Be(PatchCrcSource.Link);
        version.UsesOldInesHeader.Should().BeTrue();
    }

    [Fact]
    public async Task AHeaderThatIsNoLongerAnInesOne_ShouldPutThePatchedVersionInError()
    {
        _temp.WriteBytes("src/Game (USA).nes", File2);
        _temp.WriteBytes("patches/Game (USA) (Hack).bps", BpsLinear(File1, Hack(File1)));
        _processor.ConfigureTransferExecutor = executor => executor.WrapSourceStream =
            stream => stream is FileStream file && file.Name.EndsWith("Game (USA).nes", StringComparison.Ordinal) ? new FirstByteChangingStream(stream) : stream;

        var result = await SortAsync(Config(WriteDat(wholeFiles: false)));

        PatchedVersion(result).StatusReason!.Args["error"].Should().BeOfType<LocalizedMessage>().Which.Key.Should().Be("patch.error_header_changed");
        File.Exists(Destination("Game (USA) [Hack].unh")).Should().BeFalse();
    }

    // =========================================================================
    // A readme of the former format, the forms of the window
    // =========================================================================

    [Fact]
    public async Task AReadmeOfTheOldFileAndOfTheData_ShouldBeRefused_WithTheWholeFormFirst()
    {
        // The former block of romhacking.net: File CRC32 of the file the author had (old header), ROM CRC32 of the data
        _temp.WriteBytes("src/Game (USA).nes", File2);
        _temp.WriteBytes("patches/Hack/hack.ips", IpsDiff(File1, Hack(File1)));
        _temp.WriteText("patches/Hack/readme.txt", $"File CRC32: {Crc(File1):X8}\nROM CRC32: {Crc(Data):X8}\n");

        var result = await SimulateAsync(Config(WriteDat(wholeFiles: false)));

        result.Items.Should().NotContain(it => it.Patch != null, "applied to the data alone, the IPS would write its records 16 bytes off");
        var unapplied = result.UnappliedPatches.Should().ContainSingle().Which;
        unapplied.Reason.Key.Should().Be("reason.patch_header_form_ambiguous");
        unapplied.Suggestions.Select(s => (s.Crc32, s.Offset, s.WithCopierHeader, s.IsOldInesHeader)).Should().Equal(
            (Crc(File1), 0L, (bool?)true, true),
            (Crc(Data), 16L, (bool?)false, false));
    }

    [Fact]
    public async Task TheFormsOfANes2File_ShouldBeTheWholeFileThenTheOldOneThenTheData()
    {
        _temp.WriteBytes("src/Game (USA).nes", File2);
        var result = await SimulateAsync(Config(WriteDat(wholeFiles: false)));
        var origin = result.Items.Single(it => it.Patch is null);

        var forms = await _services.PatchLinks.GetOriginFormsAsync(origin, detectCopierHeaders: true);

        forms.Select(f => (f.Crc32, f.Offset, f.WithCopierHeader, f.IsOldInesHeader)).Should().Equal(
            (Crc(File2), 0L, (bool?)true, false),
            (Crc(File1), 0L, (bool?)true, true),
            (Crc(Data), 16L, (bool?)false, false));
    }

    [Fact]
    public async Task AFileAlreadyUnderAnInes1Header_ShouldHaveNoThirdForm()
    {
        _temp.WriteBytes("src/Game (USA).nes", File1);
        var result = await SimulateAsync(Config(WriteDat(wholeFiles: false)));
        var origin = result.Items.Single(it => it.Patch is null);

        var forms = await _services.PatchLinks.GetOriginFormsAsync(origin, detectCopierHeaders: true);

        forms.Select(f => f.Crc32).Should().Equal(Crc(File1), Crc(Data));
    }

    [Fact]
    public async Task AFileWithoutHeader_ShouldHaveNoOldForm()
    {
        _temp.WriteBytes("src/Game (USA).unh", Data);
        _temp.WriteBytes("patches/Game (USA) (Hack).bps", BpsLinear(File1, Hack(File1)));

        var result = await SimulateAsync(Config(WriteDat(wholeFiles: false)));

        result.Items.Should().NotContain(it => it.Patch != null, "a header cannot be made up");
        (await _services.PatchLinks.GetOriginFormsAsync(result.Items.Single(it => it.Patch is null), detectCopierHeaders: true))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task ANesRomIdentifiedWithoutItsHeader_ShouldBeProposedWhole()
    {
        _temp.WriteBytes("src/Game (USA).nes", File2);
        _temp.WriteBytes("patches/Game (USA) (Hack).ips", IpsDiff(File2, Hack(File2)));

        var result = await SimulateAsync(Config(WriteDat(wholeFiles: false)));

        var unapplied = result.UnappliedPatches.Should().ContainSingle().Which;
        unapplied.Reason.Key.Should().Be("reason.patch_no_crc");
        var suggestion = unapplied.Suggestions.Should().ContainSingle().Which;
        suggestion.Crc32.Should().Be(Crc(File2), "the patches of the NES are made for the whole file");
        suggestion.Offset.Should().Be(0);
    }

    /// <summary>A source whose first byte reads back otherwise: its header is no longer an iNES one.</summary>
    private sealed class FirstByteChangingStream(Stream inner) : Stream
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
            if (start == 0 && read > 0)
            {
                buffer[0] = (byte)'M';
            }

            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var start = inner.Position;
            var read = await inner.ReadAsync(buffer, cancellationToken);
            if (start == 0 && read > 0)
            {
                buffer.Span[0] = (byte)'M';
            }

            return read;
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
