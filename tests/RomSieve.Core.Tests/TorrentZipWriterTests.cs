using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using CsCheck;
using RomSieve.Core.Services;
using RomSieve.Zlib;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace RomSieve.Core.Tests;

/// <summary>
/// TorrentZip writer: the archives of igir and RomVault reproduced byte for byte (Fixtures/TorrentZip), the order of the
/// entries, the encoding of their names, the signature, ZIP64, and archives that the readers of .NET and SharpCompress
/// read back.
/// </summary>
public sealed class TorrentZipWriterTests
{
    private static readonly string FixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "TorrentZip");

    /// <summary>Code page 437, in which TorrentZip writes the names it can encode (without the UTF-8 flag).</summary>
    private static readonly Encoding CodePage437 = CodePagesEncodingProvider.Instance.GetEncoding(437)!;

    private static string[] GameNames() =>
        Directory.GetDirectories(Path.Combine(FixtureRoot, "input")).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray();

    public static TheoryData<string> Games() => new(GameNames());

    /// <summary>Files of a fixture game, by entry name, in TorrentZip order.</summary>
    private static List<(string Name, byte[] Data)> InputsOf(string game)
    {
        var folder = Path.Combine(FixtureRoot, "input", game);
        return Directory.GetFiles(folder)
            .Select(file => (Name: Path.GetFileName(file), Data: File.ReadAllBytes(file)))
            .OrderBy(file => file.Name, TorrentZipWriter.NameComparer)
            .ToList();
    }

    private static async Task<byte[]> WriteAsync(IEnumerable<(string Name, byte[] Data)> entries, ulong zip64Limit = TorrentZipWriter.Zip64Limit, Func<byte[], Stream>? open = null, Func<byte[], long>? expectedSize = null)
    {
        using var output = new MemoryStream();
        var writer = new TorrentZipWriter(output, zip64Limit);
        foreach (var (name, data) in entries)
        {
            await using var source = open?.Invoke(data) ?? new MemoryStream(data);
            await writer.AddEntryAsync(name, source, expectedSize?.Invoke(data) ?? data.Length, CancellationToken.None);
        }

        await writer.FinishAsync(CancellationToken.None);
        return output.ToArray();
    }

    [Theory]
    [MemberData(nameof(Games))]
    public async Task ReferenceArchive_ShouldBeReproducedByteForByte(string game)
    {
        var archive = await WriteAsync(InputsOf(game));

        archive.Should().Equal(File.ReadAllBytes(Path.Combine(FixtureRoot, "reference", game + ".zip")), $"igir and RomVault write these bytes for {game}");
    }

    [Fact]
    public void Fixtures_ShouldCoverEveryCase()
    {
        GameNames().Should().BeEquivalentTo(
            "Compressible (1990)(Test)", "Incompressible (1990)(Test)", "Literals And Copies (1990)(Test)", "Empty (1990)(Test)",
            "Mixed Case (1990)(Test)", "Non ASCII (1990)(Test)", "Disc (1995)(Sony)");
    }

    /// <summary>
    /// The zlib of SharpCompress keeps DotNetZip's block truncation heuristic, which zlib leaves off: its output differs
    /// from zlib's on the fixture "Literals And Copies", that of RomSieve.Zlib does not. If SharpCompress loses the
    /// heuristic some day, this test fails, and RomSieve.Zlib may give way to it.
    /// </summary>
    [Fact]
    public async Task SharpCompressDeflate_ShouldNotGiveTheOutputOfZlib()
    {
        var input = InputsOf("Literals And Copies (1990)(Test)").Single().Data;
        var reference = File.ReadAllBytes(Path.Combine(FixtureRoot, "reference", "Literals And Copies (1990)(Test).zip"));
        var compressedSize = BinaryPrimitives.ReadInt32LittleEndian(reference.AsSpan(18));
        var dataStart = 30 + BinaryPrimitives.ReadUInt16LittleEndian(reference.AsSpan(26)) + BinaryPrimitives.ReadUInt16LittleEndian(reference.AsSpan(28));
        var zlibOutput = reference.AsSpan(dataStart, compressedSize).ToArray();

        using var ours = new MemoryStream();
        await using (var deflate = new ZlibDeflateStream(ours, leaveOpen: true))
        {
            await deflate.WriteAsync(input);
        }

        using var sharpCompress = new MemoryStream();
        using (var deflate = new SharpCompress.Compressors.Deflate.DeflateStream(
            sharpCompress, SharpCompress.Compressors.CompressionMode.Compress, SharpCompress.Compressors.Deflate.CompressionLevel.BestCompression))
        {
            deflate.Write(input);
        }

        ours.ToArray().Should().Equal(zlibOutput);
        sharpCompress.ToArray().Should().NotEqual(zlibOutput, "SharpCompress ends some blocks early");
    }

    [Fact]
    public async Task EmptyEntry_ShouldBeTheDeflateStreamOfNoData()
    {
        using var empty = new MemoryStream();
        await using (new ZlibDeflateStream(empty, leaveOpen: true))
        {
        }

        var archive = await WriteAsync([("empty.bin", [])]);

        empty.ToArray().Should().Equal(0x03, 0x00);
        BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(14)).Should().Be(0, "the CRC32 of no data");
        BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(18)).Should().Be(2);
        archive.AsSpan(30 + "empty.bin".Length, 2).ToArray().Should().Equal(empty.ToArray());
    }

    [Fact]
    public void Archive_ShouldNotDependOnHowTheDataIsRead()
    {
        // Compressible data (a small alphabet), read in chunks of any size: the compressed data stays the same
        var data = Gen.Select(Gen.Int[0, 6], Gen.Int[0, 3]).Select((symbol, repeat) => Enumerable.Repeat((byte)('a' + symbol), 1 + repeat))
            .Array[0, 40_000].Select(chunks => chunks.SelectMany(chunk => chunk).ToArray());
        Gen.Select(data, Gen.Int[1, 70_000], Gen.Int)
            .Sample((input, maxChunk, seed) =>
            {
                var whole = WriteAsync([("data.bin", input)]).GetAwaiter().GetResult();
                var chunked = WriteAsync([("data.bin", input)], open: bytes => new ChunkedStream(bytes, maxChunk, seed)).GetAwaiter().GetResult();
                return whole.AsSpan().SequenceEqual(chunked);
            }, iter: 40);
    }

    [Theory]
    [InlineData("_under.bin", "alpha.bin", -1)]
    [InlineData("Beta.bin", "alpha.bin", 1)]
    [InlineData("GAMMA.bin", "beta.bin", 1)]
    [InlineData("[bracket].bin", "Zeta.bin", -1)]
    [InlineData("abc.bin", "abc.bin.bak", -1)]
    [InlineData("A.bin", "a.bin", -1)]
    [InlineData("a.bin", "A.bin", 1)]
    [InlineData("Disc (Track 1).bin", "Disc.cue", -1)]
    [InlineData("same.bin", "same.bin", 0)]
    public void Names_ShouldBeComparedAsTorrentZipOrdersThem(string x, string y, int expected)
    {
        // A to Z are lowered (so '[' and '_', between Z and a, come before the letters), then ordinal order
        TorrentZipWriter.CompareNames(x, y).Should().Be(expected);
    }

    [Fact]
    public async Task EntriesOutOfOrder_ShouldBeRefused()
    {
        using var output = new MemoryStream();
        var writer = new TorrentZipWriter(output);
        await writer.AddEntryAsync("b.bin", new MemoryStream([1]), 1, CancellationToken.None);

        var before = () => writer.AddEntryAsync("A.bin", new MemoryStream([1]), 1, CancellationToken.None);
        var duplicate = () => writer.AddEntryAsync("b.bin", new MemoryStream([1]), 1, CancellationToken.None);

        await before.Should().ThrowAsync<InvalidOperationException>();
        await duplicate.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Names_ShouldBeWrittenInCodePage437OrInFlaggedUtf8()
    {
        var archive = await WriteAsync([(@"dir\Café.bin", [1]), ("ゲーム.bin", [2])]);

        // First local header: code page 437 (é is 0x82), no UTF-8 flag, '/' as the separator
        BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(6)).Should().Be(0x0002);
        archive.AsSpan(30, 12).ToArray().Should().Equal(Encoding.ASCII.GetBytes("dir/Caf").Concat(new byte[] { 0x82 }).Concat(Encoding.ASCII.GetBytes(".bin")));

        using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read, false, CodePage437);
        zip.Entries.Select(e => e.FullName).Should().Equal("dir/Café.bin", "ゲーム.bin");
        var second = archive.AsSpan(30 + 12 + BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(18)));
        BinaryPrimitives.ReadUInt16LittleEndian(second[6..]).Should().Be(0x0802, "a name that code page 437 cannot encode is written in UTF-8");
    }

    [Theory]
    [MemberData(nameof(Games))]
    public async Task Archive_ShouldBeReadBySystemIoCompressionAndSharpCompress(string game)
    {
        var inputs = InputsOf(game);
        var archive = await WriteAsync(inputs);

        using (var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read, false, CodePage437))
        {
            zip.Entries.Select(e => (e.FullName, Read(e.Open()))).Should().BeEquivalentTo(inputs, options => options.WithStrictOrdering());
            zip.Comment.Should().StartWith(TorrentZipWriter.CommentPrefix);
            zip.Entries.Should().OnlyContain(e => e.LastWriteTime.DateTime == new DateTime(1996, 12, 24, 23, 32, 0));
        }

        using var sharpCompress = ArchiveFactory.OpenArchive(new MemoryStream(archive), ReaderOptions.ForEncoding(new ArchiveEncoding { Default = CodePage437 }));
        sharpCompress.Entries.Select(e => (e.Key!, Read(e.OpenEntryStream()))).Should().BeEquivalentTo(inputs, options => options.WithStrictOrdering());
    }

    private static byte[] Read(Stream stream)
    {
        using (stream)
        {
            using var data = new MemoryStream();
            stream.CopyTo(data);
            return data.ToArray();
        }
    }

    [Fact]
    public async Task Comment_ShouldHoldTheCrc32OfTheCentralDirectory()
    {
        var archive = await WriteAsync(InputsOf("Disc (1995)(Sony)"));

        var end = archive.Length - 22 - 22;
        var centralSize = BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(end + 12));
        var centralStart = BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(end + 16));
        var crc = Crc32.HashToUInt32(archive.AsSpan(centralStart, centralSize));
        Encoding.ASCII.GetString(archive, end + 22, 22).Should().Be($"TORRENTZIPPED-{crc:X8}");
        (await TorrentZipWriter.ReadSignatureAsync(new MemoryStream(archive), CancellationToken.None)).Should().Be(($"{crc:X8}", $"{crc:X8}"));
    }

    [Fact]
    public async Task Signature_ShouldTellAChangedCentralDirectory()
    {
        var archive = await WriteAsync(InputsOf("Mixed Case (1990)(Test)"));
        var end = archive.Length - 22 - 22;
        var centralStart = BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(end + 16));
        archive[centralStart + 4] ^= 0x01; // version made by

        var (declared, actual) = await TorrentZipWriter.ReadSignatureAsync(new MemoryStream(archive), CancellationToken.None);

        declared.Should().NotBeNull();
        actual.Should().NotBe(declared);
    }

    [Fact]
    public async Task Signature_ShouldBeMissingFromAnOrdinaryZip()
    {
        using var ordinary = new MemoryStream();
        using (var zip = new ZipArchive(ordinary, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("a.bin");
        }

        var (declared, _) = await TorrentZipWriter.ReadSignatureAsync(new MemoryStream(ordinary.ToArray()), CancellationToken.None);

        declared.Should().BeNull();
    }

    [Fact]
    public async Task Zip64_ShouldPutTheLargeValuesInExtraFields()
    {
        // With a limit of 1000 bytes, the first entry (5000 random bytes) needs ZIP64 for both sizes, the second one for
        // its offset only, and the central directory starts beyond the limit
        var large = new byte[5000];
        new Random(3).NextBytes(large);
        byte[] small = [1, 2, 3];
        var archive = await WriteAsync([("large.bin", large), ("small.bin", small)], zip64Limit: 1000);

        // Local header of the large entry: version 45, sizes in the extra field 0x0001
        BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(4)).Should().Be(45);
        BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(18)).Should().Be(uint.MaxValue);
        BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(22)).Should().Be(uint.MaxValue);
        BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(28)).Should().Be(20);
        var extra = archive.AsSpan(30 + "large.bin".Length);
        BinaryPrimitives.ReadUInt16LittleEndian(extra).Should().Be(0x0001);
        BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]).Should().Be(16);
        BinaryPrimitives.ReadUInt64LittleEndian(extra[4..]).Should().Be(5000);
        var largeCompressed = BinaryPrimitives.ReadUInt64LittleEndian(extra[12..]);
        largeCompressed.Should().BeGreaterThan(1000);

        // Local header of the small entry: no ZIP64
        var smallOffset = 30 + "large.bin".Length + 20 + (int)largeCompressed;
        BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(smallOffset)).Should().Be(0x04034B50);
        BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(smallOffset + 4)).Should().Be(20);
        BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(smallOffset + 28)).Should().Be(0);

        // Central directory: both sizes of the large entry, the offset of the small one
        var centralStart = smallOffset + 30 + "small.bin".Length + BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(smallOffset + 18));
        var central = archive.AsSpan(centralStart);
        BinaryPrimitives.ReadUInt16LittleEndian(central[6..]).Should().Be(45);
        BinaryPrimitives.ReadUInt16LittleEndian(central[30..]).Should().Be(20);
        BinaryPrimitives.ReadUInt32LittleEndian(central[42..]).Should().Be(0, "the offset of the first entry fits");
        var second = central[(46 + "large.bin".Length + 20)..];
        BinaryPrimitives.ReadUInt16LittleEndian(second[6..]).Should().Be(45);
        BinaryPrimitives.ReadUInt32LittleEndian(second[42..]).Should().Be(uint.MaxValue);
        BinaryPrimitives.ReadUInt16LittleEndian(second[30..]).Should().Be(12);
        BinaryPrimitives.ReadUInt64LittleEndian(second[(46 + "small.bin".Length + 4)..]).Should().Be((ulong)smallOffset);

        // ZIP64 end record and locator before the end record, whose comment keeps its form
        var end = archive.Length - 22 - 22;
        BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(end - 20)).Should().Be(0x07064B50);
        BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(end - 20 - 56)).Should().Be(0x06064B50);
        BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(end + 16)).Should().Be(uint.MaxValue);
        var (declared, actual) = await TorrentZipWriter.ReadSignatureAsync(new MemoryStream(archive), CancellationToken.None);
        declared.Should().NotBeNull().And.Be(actual);

        using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        zip.Entries.Select(e => Read(e.Open())).Should().BeEquivalentTo([large, small], options => options.WithStrictOrdering());
    }

    [Theory]
    [InlineData(5000, 10)]
    [InlineData(100, 5000)]
    public async Task WrongExpectedSize_ShouldGiveTheSameArchive(int size, int expectedSize)
    {
        // The data moves when the expected size missed the ZIP64 decision of the local header
        var data = new byte[size];
        new Random(5).NextBytes(data);
        byte[] next = [9, 8, 7];

        var right = await WriteAsync([("a.bin", data), ("b.bin", next)], zip64Limit: 1000);
        var wrong = await WriteAsync([("a.bin", data), ("b.bin", next)], zip64Limit: 1000, expectedSize: bytes => bytes.Length == size ? expectedSize : bytes.Length);

        wrong.Should().Equal(right);
    }

    [Fact]
    public async Task ManyEntries_ShouldEndWithTheZip64Records()
    {
        // 65535 entries: the end record cannot count them, the ZIP64 end record does
        var entries = Enumerable.Range(0, TorrentZipWriter.Zip64EntryCountLimit).Select(i => ($"{i:D5}.bin", Array.Empty<byte>()));

        var archive = await WriteAsync(entries);

        var end = archive.Length - 22 - 22;
        BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(end + 10)).Should().Be(ushort.MaxValue);
        BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(end - 20 - 56)).Should().Be(0x06064B50);
        BinaryPrimitives.ReadUInt64LittleEndian(archive.AsSpan(end - 20 - 56 + 32)).Should().Be((ulong)TorrentZipWriter.Zip64EntryCountLimit);
        using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        zip.Entries.Should().HaveCount(TorrentZipWriter.Zip64EntryCountLimit);
        var (declared, actual) = await TorrentZipWriter.ReadSignatureAsync(new MemoryStream(archive), CancellationToken.None);
        declared.Should().NotBeNull().And.Be(actual);
    }

    /// <summary>
    /// A real entry beyond 4 GiB (zeros, compressed to about 4 MB): over a minute, so it only runs with
    /// ROMSIEVE_ZIP64_REAL=1, and passes without checking anything otherwise.
    /// </summary>
    [Fact]
    public async Task EntryBeyond4GiB_ShouldBeReadBack()
    {
        if (Environment.GetEnvironmentVariable("ROMSIEVE_ZIP64_REAL") != "1")
        {
            return;
        }

        const long size = (4L * 1024 * 1024 * 1024) + (1024 * 1024);
        var path = Path.Combine(Path.GetTempPath(), $"romsieve_zip64_{Guid.NewGuid():N}.zip");
        try
        {
            await using (var output = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
            {
                var writer = new TorrentZipWriter(output);
                await writer.AddEntryAsync("zeros.bin", new ZeroStream(size), size, CancellationToken.None);
                await writer.AddEntryAsync("zz.bin", new MemoryStream([1, 2, 3]), 3, CancellationToken.None);
                await writer.FinishAsync(CancellationToken.None);
            }

            var header = new byte[30 + "zeros.bin".Length + 20];
            await using (var archive = File.OpenRead(path))
            {
                await archive.ReadExactlyAsync(header);
                var (declared, actual) = await TorrentZipWriter.ReadSignatureAsync(archive, CancellationToken.None);
                declared.Should().NotBeNull().And.Be(actual);
            }

            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)).Should().Be(45);
            BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(30 + "zeros.bin".Length + 4)).Should().Be((ulong)size);

            using var zip = ZipFile.OpenRead(path);
            zip.Entries.Select(e => e.Length).Should().Equal(size, 3);
            var crc = new Crc32();
            await using (var entry = zip.Entries[0].Open())
            {
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = await entry.ReadAsync(buffer)) > 0)
                {
                    crc.Append(buffer.AsSpan(0, read));
                }
            }

            crc.GetCurrentHashAsUInt32().Should().Be(zip.Entries[0].Crc32);
            Read(zip.Entries[1].Open()).Should().Equal(1, 2, 3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Stream of zeros of a given length.</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        private long _left = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, _left);
            Array.Clear(buffer, offset, read);
            _left -= read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Stream that returns its data in chunks of random sizes.</summary>
    private sealed class ChunkedStream(byte[] data, int maxChunk, int seed) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        private readonly Random _random = new(seed);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, _random.Next(1, maxChunk + 1)));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer[..Math.Min(buffer.Length, _random.Next(1, maxChunk + 1))], cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
