using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Reads the header (versions 3 to 5) and the metadata of a CHD file (MAME compressed disk image) without
/// decompressing its data. All the integers of the format are big-endian.
/// </summary>
internal static class ChdReader
{
    private static readonly byte[] Tag = "MComprHD"u8.ToArray();
    private const int MaxMetadataEntries = 4096;
    private const int MaxMetadataLength = 1 << 20;

    // Old binary CD metadata (CHCD): track type codes
    private static readonly string[] BinaryTrackTypes =
        ["MODE1", "MODE1_RAW", "MODE2", "MODE2_FORM1", "MODE2_FORM2", "MODE2_FORM_MIX", "MODE2_RAW", "AUDIO"];

    /// <summary>Reads a CHD; throws <see cref="InvalidDataException"/> when it is not a readable CHD.</summary>
    public static ChdInfo Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        var start = new byte[16];
        ReadExactly(stream, start, "header");
        if (!start.AsSpan(0, 8).SequenceEqual(Tag))
        {
            throw new InvalidDataException("Not a CHD file (missing MComprHD tag)");
        }

        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(start.AsSpan(8));
        var version = (int)BinaryPrimitives.ReadUInt32BigEndian(start.AsSpan(12));
        var expectedLength = version switch
        {
            3 => 120,
            4 => 108,
            5 => 124,
            _ => throw new InvalidDataException($"Unsupported CHD version {version}")
        };
        if (length != expectedLength)
        {
            throw new InvalidDataException($"Invalid CHD v{version} header length {length}");
        }

        var header = new byte[length];
        start.CopyTo(header, 0);
        ReadExactly(stream, header.AsMemory(16), "header");

        long logicalBytes;
        ulong metaOffset;
        string sha1, rawSha1;
        string? parentSha1;
        var h = header.AsSpan();
        switch (version)
        {
            case 5:
                logicalBytes = (long)BinaryPrimitives.ReadUInt64BigEndian(h[32..]);
                metaOffset = BinaryPrimitives.ReadUInt64BigEndian(h[48..]);
                rawSha1 = Hex(h.Slice(64, 20));
                sha1 = Hex(h.Slice(84, 20));
                parentSha1 = NonZeroHex(h.Slice(104, 20));
                break;
            case 4:
                logicalBytes = (long)BinaryPrimitives.ReadUInt64BigEndian(h[28..]);
                metaOffset = BinaryPrimitives.ReadUInt64BigEndian(h[36..]);
                sha1 = Hex(h.Slice(48, 20));
                parentSha1 = NonZeroHex(h.Slice(68, 20));
                rawSha1 = Hex(h.Slice(88, 20));
                break;
            default:
                // Version 3 has a single SHA-1, of the data
                logicalBytes = (long)BinaryPrimitives.ReadUInt64BigEndian(h[28..]);
                metaOffset = BinaryPrimitives.ReadUInt64BigEndian(h[36..]);
                sha1 = Hex(h.Slice(80, 20));
                parentSha1 = NonZeroHex(h.Slice(100, 20));
                rawSha1 = sha1;
                break;
        }

        var (mediaKind, tracks) = ReadMetadata(stream, metaOffset);
        return new ChdInfo(version, logicalBytes, sha1, rawSha1, parentSha1, mediaKind, tracks);
    }

    /// <summary>Walks the linked list of metadata entries: 16-byte header (tag, flags, 24-bit length, next offset), then data.</summary>
    private static (ChdMediaKind Kind, IReadOnlyList<ChdTrack> Tracks) ReadMetadata(FileStream stream, ulong offset)
    {
        var kind = ChdMediaKind.Unknown;
        var tracks = new List<ChdTrack>();
        var visited = new HashSet<ulong>();
        var entryHeader = new byte[16];

        for (var count = 0; offset != 0 && count < MaxMetadataEntries; count++)
        {
            if (!visited.Add(offset) || offset > (ulong)stream.Length - 16)
            {
                throw new InvalidDataException("Invalid CHD metadata chain");
            }

            stream.Position = (long)offset;
            ReadExactly(stream, entryHeader, "metadata");
            var tag = Encoding.ASCII.GetString(entryHeader, 0, 4);
            var length = (entryHeader[5] << 16) | (entryHeader[6] << 8) | entryHeader[7];
            var next = BinaryPrimitives.ReadUInt64BigEndian(entryHeader.AsSpan(8));
            if (offset + 16 + (ulong)length > (ulong)stream.Length)
            {
                throw new InvalidDataException("Invalid CHD metadata length");
            }

            // Only the track entries are read, and they are small. Any other entry is skipped unread: the metadata of a
            // laserdisc (AVLD) holds several megabytes for every frame of the video
            switch (tag)
            {
                case "CHT2" or "CHTR":
                    kind = ChdMediaKind.Cd;
                    tracks.Add(ParseTextTrack(ReadMetadataData(stream, length)));
                    break;
                case "CHGT" or "CHGD":
                    kind = ChdMediaKind.GdRom;
                    tracks.Add(ParseTextTrack(ReadMetadataData(stream, length)));
                    break;
                case "CHCD":
                    kind = ChdMediaKind.Cd;
                    tracks.AddRange(ParseBinaryTracks(ReadMetadataData(stream, length)));
                    break;
                case "DVD ":
                    kind = ChdMediaKind.Dvd;
                    break;
                case "GDDD":
                    kind = ChdMediaKind.HardDisk;
                    break;
            }

            offset = next;
        }

        return (kind, tracks.OrderBy(t => t.Number).ToList());
    }

    /// <summary>Data of a track metadata entry, right after its header; a track entry is a few hundred bytes at most.</summary>
    private static byte[] ReadMetadataData(FileStream stream, int length)
    {
        if (length > MaxMetadataLength)
        {
            throw new InvalidDataException("Invalid CHD metadata length");
        }

        var data = new byte[length];
        ReadExactly(stream, data, "metadata");
        return data;
    }

    /// <summary>Text track metadata, such as <c>TRACK:1 TYPE:MODE1_RAW SUBTYPE:NONE FRAMES:1234 ...</c>.</summary>
    private static ChdTrack ParseTextTrack(byte[] data)
    {
        var text = Encoding.ASCII.GetString(data).TrimEnd('\0');
        var values = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split(':', 2))
            .Where(kv => kv.Length == 2)
            .GroupBy(kv => kv[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First()[1], StringComparer.OrdinalIgnoreCase);

        static int Int(Dictionary<string, string> v, string key) =>
            v.TryGetValue(key, out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

        return new ChdTrack(Int(values, "TRACK"), values.GetValueOrDefault("TYPE", "?"), Int(values, "FRAMES"));
    }

    /// <summary>Binary CD metadata of old CHDs: track count, then 6 integers per track (type, subtype, sizes, frames, extra frames).</summary>
    private static IEnumerable<ChdTrack> ParseBinaryTracks(byte[] data)
    {
        if (data.Length < 4)
        {
            yield break;
        }

        var count = BinaryPrimitives.ReadUInt32BigEndian(data);
        for (var i = 0; i < count && 4 + (i + 1) * 24 <= data.Length; i++)
        {
            var entry = data.AsSpan(4 + i * 24, 24);
            var type = BinaryPrimitives.ReadUInt32BigEndian(entry);
            var frames = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[16..]);
            yield return new ChdTrack(i + 1, type < BinaryTrackTypes.Length ? BinaryTrackTypes[type] : "?", frames);
        }
    }

    private static void ReadExactly(Stream stream, Memory<byte> buffer, string part)
    {
        try
        {
            stream.ReadExactly(buffer.Span);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException($"Truncated CHD {part}");
        }
    }

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    private static string? NonZeroHex(ReadOnlySpan<byte> bytes) => bytes.ContainsAnyExcept((byte)0) ? Hex(bytes) : null;
}
