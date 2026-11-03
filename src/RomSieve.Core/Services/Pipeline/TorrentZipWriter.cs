using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using RomSieve.Zlib;

namespace RomSieve.Core.Services;

/// <summary>
/// Writes a TorrentZip archive: the ZIP that trrntzip, RomVault and igir write byte for byte from the same files, so that
/// an archive only depends on its content. The rules are RomVault's (<c>Compress/ZipFile</c>,
/// <c>Compress/StructuredZip</c>, <c>SortMethods/Sorters</c>), whose output igir reproduces:
/// <list type="bullet">
/// <item>entries in TorrentZip order (<see cref="CompareNames"/>), <c>/</c> as the directory separator, no directory
/// entry, no duplicate;</item>
/// <item>every header: version needed 20 (45 with a ZIP64 extra field), flags 2 (maximum compression) plus 0x0800 for a
/// name that code page 437 cannot encode (then written in UTF-8), Deflate, DOS time 0xBC00 and date 0x2198
/// (24/12/1996 23:32), no extra field but the ZIP64 one, no data descriptor: the CRC32 and the sizes are written in the
/// local header once the entry is compressed;</item>
/// <item>central directory: version made by 0, no comment, internal and external attributes 0;</item>
/// <item>data compressed as zlib does at level 9 (<see cref="ZlibDeflateStream"/>), never by
/// <see cref="System.IO.Compression.DeflateStream"/>, whose zlib-ng output differs; an empty file is <c>03 00</c>;</item>
/// <item>archive comment <c>TORRENTZIPPED-</c> followed by the CRC32 of the central directory, in 8 uppercase
/// hexadecimal digits;</item>
/// <item>ZIP64: in a local header, an extra field 0x0001 with both sizes when either reaches 0xFFFFFFFF; in the central
/// directory, only the fields that reach it, in the order uncompressed size, compressed size, offset; the ZIP64 end
/// records when the central directory starts or ends beyond 0xFFFFFFFF, or holds 0xFFFF entries or more. The comment
/// stays the same.</item>
/// </list>
/// The archive is written in a stream (seekable, readable, from its position 0) without holding a file in memory.
/// </summary>
internal sealed class TorrentZipWriter
{
    /// <summary>Start of the comment of a TorrentZip archive, followed by the CRC32 of its central directory.</summary>
    internal const string CommentPrefix = "TORRENTZIPPED-";

    /// <summary>A 32-bit field of a ZIP header holds values below this one; from it on, ZIP64 takes over.</summary>
    internal const ulong Zip64Limit = uint.MaxValue;

    /// <summary>Entry count from which the ZIP64 end records are written.</summary>
    internal const int Zip64EntryCountLimit = ushort.MaxValue;

    private const int CommentLength = 22;
    private const uint LocalHeaderSignature = 0x04034B50;
    private const uint CentralHeaderSignature = 0x02014B50;
    private const uint EndSignature = 0x06054B50;
    private const uint Zip64EndSignature = 0x06064B50;
    private const uint Zip64LocatorSignature = 0x07064B50;
    private const ushort VersionNeeded = 20;
    private const ushort Zip64VersionNeeded = 45;
    private const ushort MaximumCompressionFlag = 0x0002;
    private const ushort Utf8NameFlag = 0x0800;
    private const ushort DeflateMethod = 8;
    private const ushort DosTime = 0xBC00;
    private const ushort DosDate = 0x2198;
    private const ushort Zip64ExtraId = 0x0001;
    private const int LocalHeaderLength = 30;
    private const int CentralHeaderLength = 46;
    private const int EndLength = 22;
    private const int Zip64EndLength = 56;
    private const int Zip64LocatorLength = 20;
    private const int Zip64LocalExtraLength = 20;
    private const int BufferSize = 1024 * 1024;

    /// <summary>Deflate stream of no data, as zlib (and <see cref="ZlibDeflateStream"/>) ends it.</summary>
    private static readonly byte[] EmptyDeflate = [0x03, 0x00];

    private readonly Stream _output;
    private readonly ulong _zip64Limit;
    private readonly List<Entry> _entries = [];
    private string? _lastName;

    private sealed record Entry(byte[] Name, ushort Flags, uint Crc, ulong CompressedSize, ulong UncompressedSize, ulong Offset);

    /// <param name="output">Stream of the archive, seekable and readable, at its position 0.</param>
    public TorrentZipWriter(Stream output)
        : this(output, Zip64Limit)
    {
    }

    /// <param name="output">Stream of the archive, seekable and readable, at its position 0.</param>
    /// <param name="zip64Limit">
    /// Value from which a size or an offset needs ZIP64: <see cref="Zip64Limit"/>, lowered by tests only, to exercise
    /// ZIP64 without writing 4 GiB.
    /// </param>
    internal TorrentZipWriter(Stream output, ulong zip64Limit)
    {
        if (!output.CanSeek || !output.CanWrite || !output.CanRead)
        {
            throw new ArgumentException("A TorrentZip archive is written in a seekable, readable stream.", nameof(output));
        }

        _output = output;
        _zip64Limit = zip64Limit;
    }

    /// <summary>
    /// Order of two entry names in a TorrentZip archive (RomVault's <c>TrrntZipStringCompareCase</c>): character by
    /// character, the ASCII letters A to Z lowered, a shorter name first; then, for names equal so, ordinal order.
    /// </summary>
    public static int CompareNames(string x, string y)
    {
        var length = Math.Min(x.Length, y.Length);
        for (var i = 0; i < length; i++)
        {
            var a = LowerAscii(x[i]);
            var b = LowerAscii(y[i]);
            if (a != b)
            {
                return a < b ? -1 : 1;
            }
        }

        if (x.Length != y.Length)
        {
            return x.Length < y.Length ? -1 : 1;
        }

        return Math.Sign(string.CompareOrdinal(x, y));
    }

    /// <summary><see cref="CompareNames"/> as a comparer.</summary>
    public static IComparer<string> NameComparer { get; } = Comparer<string>.Create(CompareNames);

    private static char LowerAscii(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 0x20) : c;

    /// <summary>Name of an entry as TorrentZip stores it: <c>/</c> as the directory separator.</summary>
    public static string NormalizeName(string name) => name.Replace('\\', '/');

    /// <summary>
    /// Compresses <paramref name="source"/> into a new entry, after the entries already added, which must come before it
    /// in TorrentZip order (<see cref="CompareNames"/> of normalized names). Returns the CRC32 of the data read.
    /// </summary>
    /// <param name="name">Entry name, normalized by <see cref="NormalizeName"/>.</param>
    /// <param name="source">Data of the entry, read to its end.</param>
    /// <param name="expectedSize">
    /// Expected size of the data, which decides the ZIP64 extra field of the local header before the data is written;
    /// a wrong size only costs a move of the compressed data.
    /// </param>
    public async Task<uint> AddEntryAsync(string name, Stream source, long expectedSize, CancellationToken cancellationToken)
    {
        name = NormalizeName(name);
        if (_lastName is not null && CompareNames(_lastName, name) >= 0)
        {
            throw new InvalidOperationException($"TorrentZip entries are added in order, without duplicates: '{name}' after '{_lastName}'.");
        }

        var (nameBytes, flags) = EncodeName(name);
        if (nameBytes.Length > ushort.MaxValue)
        {
            throw new ArgumentException("The entry name is too long for a ZIP archive.", nameof(name));
        }

        var offset = (ulong)_output.Position;
        var localZip64 = (ulong)Math.Max(0, expectedSize) >= _zip64Limit;

        // The header is written again once the CRC32 and the sizes are known
        await _output.WriteAsync(new byte[LocalHeaderLength + nameBytes.Length + (localZip64 ? Zip64LocalExtraLength : 0)], cancellationToken).ConfigureAwait(false);
        var dataStart = _output.Position;

        var crc = new Crc32();
        ulong uncompressedSize = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        ZlibDeflateStream? deflate = null;
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                crc.Append(buffer.AsSpan(0, read));
                uncompressedSize += (ulong)read;

                // Created on the first data: an empty entry needs no compressor (below)
                deflate ??= new ZlibDeflateStream(_output, leaveOpen: true);
                await deflate.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            if (deflate is null)
            {
                await _output.WriteAsync(EmptyDeflate, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await deflate.FinishAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (deflate is not null)
            {
                await deflate.DisposeAsync().ConfigureAwait(false);
            }

            ArrayPool<byte>.Shared.Return(buffer);
        }

        var compressedSize = (ulong)(_output.Position - dataStart);
        var zip64 = uncompressedSize >= _zip64Limit || compressedSize >= _zip64Limit;
        if (zip64 != localZip64)
        {
            // The expected size missed the ZIP64 decision (data a little larger once compressed, or a size that changed
            // since it was read): the data moves to make room for the extra field, or to take back its place
            var shift = zip64 ? Zip64LocalExtraLength : -Zip64LocalExtraLength;
            await MoveAsync(dataStart, (long)compressedSize, shift, cancellationToken).ConfigureAwait(false);
            dataStart += shift;
        }

        var end = dataStart + (long)compressedSize;
        var checksum = crc.GetCurrentHashAsUInt32();
        _output.Position = (long)offset;
        await _output.WriteAsync(BuildLocalHeader(nameBytes, flags, checksum, compressedSize, uncompressedSize, zip64), cancellationToken).ConfigureAwait(false);
        _output.Position = end;

        _entries.Add(new Entry(nameBytes, flags, checksum, compressedSize, uncompressedSize, offset));
        _lastName = name;
        return checksum;
    }

    /// <summary>
    /// Writes the central directory, the end records and the comment, and cuts the stream at the end of the archive.
    /// </summary>
    public async Task FinishAsync(CancellationToken cancellationToken)
    {
        var centralStart = (ulong)_output.Position;
        var central = BuildCentralDirectory();
        await _output.WriteAsync(central, cancellationToken).ConfigureAwait(false);
        var centralSize = (ulong)central.Length;

        if (centralStart >= _zip64Limit || centralSize >= _zip64Limit || _entries.Count >= Zip64EntryCountLimit)
        {
            await _output.WriteAsync(BuildZip64End(centralStart, centralSize, (ulong)_output.Position), cancellationToken).ConfigureAwait(false);
        }

        var comment = CommentPrefix + FormatCrc(Crc32.HashToUInt32(central));
        await _output.WriteAsync(BuildEnd(centralStart, centralSize, comment), cancellationToken).ConfigureAwait(false);
        _output.SetLength(_output.Position);
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Signature of a TorrentZip archive: the CRC32 its comment declares (<c>null</c> without a TorrentZip comment) and
    /// that of its central directory (<c>null</c> when the end records cannot be read), in 8 uppercase hexadecimal digits.
    /// </summary>
    internal static async Task<(string? Declared, string? Actual)> ReadSignatureAsync(Stream archive, CancellationToken cancellationToken)
    {
        var endPosition = archive.Length - EndLength - CommentLength;
        if (endPosition < 0)
        {
            return (null, null);
        }

        var end = new byte[EndLength + CommentLength];
        archive.Position = endPosition;
        await archive.ReadExactlyAsync(end, cancellationToken).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(end) != EndSignature
            || BinaryPrimitives.ReadUInt16LittleEndian(end.AsSpan(20)) != CommentLength)
        {
            return (null, null);
        }

        var comment = Encoding.ASCII.GetString(end, EndLength, CommentLength);
        var digits = comment[CommentPrefix.Length..];
        string? declared = comment.StartsWith(CommentPrefix, StringComparison.Ordinal) && digits.All(char.IsAsciiHexDigitUpper)
            ? digits
            : null;

        ulong centralSize = BinaryPrimitives.ReadUInt32LittleEndian(end.AsSpan(12));
        ulong centralStart = BinaryPrimitives.ReadUInt32LittleEndian(end.AsSpan(16));
        if (centralSize == uint.MaxValue || centralStart == uint.MaxValue)
        {
            if (endPosition < Zip64LocatorLength)
            {
                return (declared, null);
            }

            var locator = new byte[Zip64LocatorLength];
            archive.Position = endPosition - Zip64LocatorLength;
            await archive.ReadExactlyAsync(locator, cancellationToken).ConfigureAwait(false);
            var zip64EndPosition = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64LocatorSignature || zip64EndPosition > (ulong)endPosition)
            {
                return (declared, null);
            }

            var zip64End = new byte[Zip64EndLength];
            archive.Position = (long)zip64EndPosition;
            await archive.ReadExactlyAsync(zip64End, cancellationToken).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip64End) != Zip64EndSignature)
            {
                return (declared, null);
            }

            centralSize = BinaryPrimitives.ReadUInt64LittleEndian(zip64End.AsSpan(40));
            centralStart = BinaryPrimitives.ReadUInt64LittleEndian(zip64End.AsSpan(48));
        }

        if (centralStart + centralSize > (ulong)endPosition)
        {
            return (declared, null);
        }

        var crc = new Crc32();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            archive.Position = (long)centralStart;
            var left = (long)centralSize;
            while (left > 0)
            {
                var count = (int)Math.Min(BufferSize, left);
                await archive.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                crc.Append(buffer.AsSpan(0, count));
                left -= count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return (declared, FormatCrc(crc.GetCurrentHashAsUInt32()));
    }

    private static string FormatCrc(uint crc) => crc.ToString("X8", CultureInfo.InvariantCulture);

    private static byte[] BuildLocalHeader(byte[] name, ushort flags, uint crc, ulong compressedSize, ulong uncompressedSize, bool zip64)
    {
        var header = new byte[LocalHeaderLength + name.Length + (zip64 ? Zip64LocalExtraLength : 0)];
        var span = header.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, LocalHeaderSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], zip64 ? Zip64VersionNeeded : VersionNeeded);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], flags);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], DeflateMethod);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], DosTime);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], DosDate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[14..], crc);
        BinaryPrimitives.WriteUInt32LittleEndian(span[18..], zip64 ? uint.MaxValue : (uint)compressedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[22..], zip64 ? uint.MaxValue : (uint)uncompressedSize);
        BinaryPrimitives.WriteUInt16LittleEndian(span[26..], (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[28..], zip64 ? (ushort)Zip64LocalExtraLength : (ushort)0);
        name.CopyTo(span[LocalHeaderLength..]);
        if (zip64)
        {
            var extra = span[(LocalHeaderLength + name.Length)..];
            BinaryPrimitives.WriteUInt16LittleEndian(extra, Zip64ExtraId);
            BinaryPrimitives.WriteUInt16LittleEndian(extra[2..], Zip64LocalExtraLength - 4);
            BinaryPrimitives.WriteUInt64LittleEndian(extra[4..], uncompressedSize);
            BinaryPrimitives.WriteUInt64LittleEndian(extra[12..], compressedSize);
        }

        return header;
    }

    private byte[] BuildCentralDirectory()
    {
        using var central = new MemoryStream();
        foreach (var entry in _entries)
        {
            var extra = new List<ulong>(3);
            if (entry.UncompressedSize >= _zip64Limit)
            {
                extra.Add(entry.UncompressedSize);
            }

            if (entry.CompressedSize >= _zip64Limit)
            {
                extra.Add(entry.CompressedSize);
            }

            if (entry.Offset >= _zip64Limit)
            {
                extra.Add(entry.Offset);
            }

            var extraLength = extra.Count == 0 ? 0 : 4 + (8 * extra.Count);
            var header = new byte[CentralHeaderLength + entry.Name.Length + extraLength];
            var span = header.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(span, CentralHeaderSignature);

            // Version made by (bytes 4 and 5): 0
            BinaryPrimitives.WriteUInt16LittleEndian(span[6..], extra.Count > 0 ? Zip64VersionNeeded : VersionNeeded);
            BinaryPrimitives.WriteUInt16LittleEndian(span[8..], entry.Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(span[10..], DeflateMethod);
            BinaryPrimitives.WriteUInt16LittleEndian(span[12..], DosTime);
            BinaryPrimitives.WriteUInt16LittleEndian(span[14..], DosDate);
            BinaryPrimitives.WriteUInt32LittleEndian(span[16..], entry.Crc);
            BinaryPrimitives.WriteUInt32LittleEndian(span[20..], Clamp(entry.CompressedSize));
            BinaryPrimitives.WriteUInt32LittleEndian(span[24..], Clamp(entry.UncompressedSize));
            BinaryPrimitives.WriteUInt16LittleEndian(span[28..], (ushort)entry.Name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(span[30..], (ushort)extraLength);

            // Comment length, disk number, internal and external attributes (bytes 32 to 41): 0
            BinaryPrimitives.WriteUInt32LittleEndian(span[42..], Clamp(entry.Offset));
            entry.Name.CopyTo(span[CentralHeaderLength..]);
            if (extra.Count > 0)
            {
                var field = span[(CentralHeaderLength + entry.Name.Length)..];
                BinaryPrimitives.WriteUInt16LittleEndian(field, Zip64ExtraId);
                BinaryPrimitives.WriteUInt16LittleEndian(field[2..], (ushort)(8 * extra.Count));
                for (var i = 0; i < extra.Count; i++)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(field[(4 + (8 * i))..], extra[i]);
                }
            }

            central.Write(header);
        }

        return central.ToArray();
    }

    private byte[] BuildZip64End(ulong centralStart, ulong centralSize, ulong zip64EndOffset)
    {
        var records = new byte[Zip64EndLength + Zip64LocatorLength];
        var span = records.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Zip64EndSignature);
        BinaryPrimitives.WriteUInt64LittleEndian(span[4..], Zip64EndLength - 12);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], Zip64VersionNeeded);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], Zip64VersionNeeded);

        // Disk numbers (bytes 16 to 23): 0
        BinaryPrimitives.WriteUInt64LittleEndian(span[24..], (ulong)_entries.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(span[32..], (ulong)_entries.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(span[40..], centralSize);
        BinaryPrimitives.WriteUInt64LittleEndian(span[48..], centralStart);

        var locator = span[Zip64EndLength..];
        BinaryPrimitives.WriteUInt32LittleEndian(locator, Zip64LocatorSignature);

        // Disk of the ZIP64 end record (bytes 4 to 7): 0
        BinaryPrimitives.WriteUInt64LittleEndian(locator[8..], zip64EndOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(locator[16..], 1);
        return records;
    }

    private byte[] BuildEnd(ulong centralStart, ulong centralSize, string comment)
    {
        var record = new byte[EndLength + comment.Length];
        var span = record.AsSpan();
        var count = (ushort)Math.Min(_entries.Count, Zip64EntryCountLimit);
        BinaryPrimitives.WriteUInt32LittleEndian(span, EndSignature);

        // Disk numbers (bytes 4 to 7): 0
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], count);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], count);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], Clamp(centralSize));
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], Clamp(centralStart));
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], (ushort)comment.Length);
        Encoding.ASCII.GetBytes(comment, span[EndLength..]);
        return record;
    }

    /// <summary>A value of a 32-bit field, or 0xFFFFFFFF when ZIP64 holds it.</summary>
    private uint Clamp(ulong value) => value >= _zip64Limit ? uint.MaxValue : (uint)value;

    /// <summary>Moves <paramref name="length"/> bytes at <paramref name="start"/> by <paramref name="shift"/> bytes.</summary>
    private async Task MoveAsync(long start, long length, int shift, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            // Moved forward, the data is copied from its end, so that nothing is overwritten before being read
            var done = 0L;
            while (done < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(BufferSize, length - done);
                var position = shift > 0 ? start + length - done - count : start + done;
                _output.Position = position;
                await _output.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                _output.Position = position + shift;
                await _output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                done += count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Bytes of an entry name: code page 437 when it encodes every character (as RomVault decides it), otherwise UTF-8
    /// with the flag that says so.
    /// </summary>
    private static (byte[] Bytes, ushort Flags) EncodeName(string name)
    {
        var bytes = new byte[name.Length];
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c is >= (char)32 and <= (char)127)
            {
                bytes[i] = (byte)c;
            }
            else if (!CodePage437.TryGetValue(c, out bytes[i]))
            {
                return (Encoding.UTF8.GetBytes(name), MaximumCompressionFlag | Utf8NameFlag);
            }
        }

        return (bytes, MaximumCompressionFlag);
    }

    /// <summary>
    /// Characters of code page 437 outside 32 to 127, with their byte: 0 to 31 (graphic characters) and 128 to 255, as in
    /// RomVault's table.
    /// </summary>
    private static readonly Dictionary<char, byte> CodePage437 = BuildCodePage437();

    private static Dictionary<char, byte> BuildCodePage437()
    {
        const string low =
            "\u0000☺☻♥♦♣♠•◘○◙♂♀♪♫☼" +
            "►◄↕‼¶§▬↨↑↓→←∟↔▲▼";
        const string high =
            "ÇüéâäàåçêëèïîìÄÅ" +
            "ÉæÆôöòûùÿÖÜ¢£¥₧ƒ" +
            "áíóúñÑªº¿⌐¬½¼¡«»" +
            "░▒▓│┤╡╢╖╕╣║╗╝╜╛┐" +
            "└┴┬├─┼╞╟╚╔╩╦╠═╬╧" +
            "╨╤╥╙╘╒╓╫╪┘┌█▄▌▐▀" +
            "αßΓπΣσµτΦΘΩδ∞φε∩" +
            "≡±≥≤⌠⌡÷≈°∙·√ⁿ²■ ";
        var map = new Dictionary<char, byte>(low.Length + high.Length);
        for (var i = 0; i < low.Length; i++)
        {
            map.TryAdd(low[i], (byte)i);
        }

        for (var i = 0; i < high.Length; i++)
        {
            map.TryAdd(high[i], (byte)(128 + i));
        }

        return map;
    }
}
