using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RomSieve.Core.Tests.Fixtures;

/// <summary>
/// Synthetic CHD files for the tests: a valid header (versions 3 to 5) and a chain of metadata entries, without
/// compressed data (RomSieve never decompresses a CHD). Integers are big-endian, as in the format.
/// </summary>
public static class ChdTestFile
{
    /// <summary>SHA-1 (hex) derived from a seed, to give each test file its own identity.</summary>
    public static string Sha1Of(string seed) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(seed)));

    /// <summary>CD track metadata (<c>CHT2</c>) of the given tracks, numbered from 1.</summary>
    public static (string Tag, byte[] Data)[] CdTracks(params (string Type, int Frames)[] tracks) =>
        tracks.Select((t, i) => ("CHT2", Encoding.ASCII.GetBytes(
            $"TRACK:{i + 1} TYPE:{t.Type} SUBTYPE:NONE FRAMES:{t.Frames} PREGAP:0 PGTYPE:MODE1 PGSUB:RW POSTGAP:0\0"))).ToArray();

    /// <summary>
    /// A CHD whose SHA-1 (data and metadata) is derived from <paramref name="seed"/>, with one data track and one audio track
    /// unless <paramref name="metadata"/> says otherwise.
    /// </summary>
    public static byte[] Create(
        string seed,
        int version = 5,
        string? sha1 = null,
        string? parentSha1 = null,
        (string Tag, byte[] Data)[]? metadata = null,
        long logicalBytes = 2_448 * 1_000)
    {
        sha1 ??= Sha1Of(seed);
        var rawSha1 = Sha1Of(seed + ":raw");
        metadata ??= CdTracks(("MODE1_RAW", 600), ("AUDIO", 400));

        var headerLength = version switch { 3 => 120, 4 => 108, _ => 124 };
        var file = new List<byte>(new byte[headerLength]);

        // Metadata entries right after the header, each pointing to the next one
        var metaOffset = metadata.Length > 0 ? (ulong)headerLength : 0;
        var offset = (ulong)headerLength;
        for (var i = 0; i < metadata.Length; i++)
        {
            var (tag, data) = metadata[i];
            var next = i < metadata.Length - 1 ? offset + 16 + (ulong)data.Length : 0;
            var entry = new byte[16];
            Encoding.ASCII.GetBytes(tag.PadRight(4)).CopyTo(entry, 0);
            entry[5] = (byte)(data.Length >> 16);
            entry[6] = (byte)(data.Length >> 8);
            entry[7] = (byte)data.Length;
            BinaryPrimitives.WriteUInt64BigEndian(entry.AsSpan(8), next);
            file.AddRange(entry);
            file.AddRange(data);
            offset += 16 + (ulong)data.Length;
        }

        var bytes = file.ToArray();
        var h = bytes.AsSpan();
        "MComprHD"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32BigEndian(h[8..], (uint)headerLength);
        BinaryPrimitives.WriteUInt32BigEndian(h[12..], (uint)version);
        var parent = parentSha1 is null ? new byte[20] : Convert.FromHexString(parentSha1);
        switch (version)
        {
            case 5:
                Encoding.ASCII.GetBytes("cdlz").CopyTo(h[16..]);
                BinaryPrimitives.WriteUInt64BigEndian(h[32..], (ulong)logicalBytes);
                BinaryPrimitives.WriteUInt64BigEndian(h[48..], metaOffset);
                BinaryPrimitives.WriteUInt32BigEndian(h[56..], 19_584);
                BinaryPrimitives.WriteUInt32BigEndian(h[60..], 2_448);
                Convert.FromHexString(rawSha1).CopyTo(h[64..]);
                Convert.FromHexString(sha1).CopyTo(h[84..]);
                parent.CopyTo(h[104..]);
                break;
            case 4:
                BinaryPrimitives.WriteUInt64BigEndian(h[28..], (ulong)logicalBytes);
                BinaryPrimitives.WriteUInt64BigEndian(h[36..], metaOffset);
                BinaryPrimitives.WriteUInt32BigEndian(h[44..], 19_584);
                Convert.FromHexString(sha1).CopyTo(h[48..]);
                parent.CopyTo(h[68..]);
                Convert.FromHexString(rawSha1).CopyTo(h[88..]);
                break;
            default:
                BinaryPrimitives.WriteUInt64BigEndian(h[28..], (ulong)logicalBytes);
                BinaryPrimitives.WriteUInt64BigEndian(h[36..], metaOffset);
                BinaryPrimitives.WriteUInt32BigEndian(h[76..], 19_584);
                Convert.FromHexString(sha1).CopyTo(h[80..]);
                parent.CopyTo(h[100..]);
                break;
        }

        return bytes;
    }
}
