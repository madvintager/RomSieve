using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace RomSieve.Core.Tests.Fixtures;

/// <summary>
/// Minimal encoders of IPS, IPS32, UPS and BPS patches, written for the tests from the specifications (see
/// <c>PlanPatch.md</c>, annex), independently of the decoders of the Core: a patch built here and applied by
/// <c>PatchService</c> must give back its result. Synthetic data only: no patch of a real game.
/// </summary>
internal static class PatchTestEncoder
{
    public static uint Crc(ReadOnlySpan<byte> data) => Crc32.HashToUInt32(data);

    public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    // =========================================================================
    // IPS and IPS32
    // =========================================================================

    /// <summary>A record of an IPS patch: data written at an offset, or a run of one byte (RLE) when <see cref="Data"/> is null.</summary>
    public sealed record IpsRecord(long Offset, byte[]? Data, int RunLength = 0, byte RunValue = 0)
    {
        public static IpsRecord Run(long offset, int length, byte value) => new(offset, null, length, value);
    }

    /// <summary>An IPS (or IPS32) patch of the given records, with a truncation and bytes after the footer if asked.</summary>
    public static byte[] Ips(IEnumerable<IpsRecord> records, long? truncate = null, bool ips32 = false, byte[]? afterFooter = null)
    {
        var patch = new List<byte>(Ascii(ips32 ? "IPS32" : "PATCH"));
        foreach (var record in records)
        {
            patch.AddRange(BigEndian(record.Offset, ips32 ? 4 : 3));
            if (record.Data is { } data)
            {
                patch.AddRange(BigEndian(data.Length, 2));
                patch.AddRange(data);
            }
            else
            {
                patch.AddRange([0, 0]);
                patch.AddRange(BigEndian(record.RunLength, 2));
                patch.Add(record.RunValue);
            }
        }

        patch.AddRange(Ascii(ips32 ? "EEOF" : "EOF"));
        if (truncate is { } size)
        {
            patch.AddRange(BigEndian(size, 3));
        }

        patch.AddRange(afterFooter ?? []);
        return [.. patch];
    }

    /// <summary>
    /// An IPS (or IPS32) patch from <paramref name="source"/> to <paramref name="target"/>: a record per run of
    /// differing bytes, a run (RLE) when they are all the same, a truncation when the target is shorter (IPS only).
    /// A record never starts at the offset that reads as the footer: it starts one byte earlier, as Flips and ips.py do.
    /// </summary>
    public static byte[] IpsDiff(byte[] source, byte[] target, bool ips32 = false)
    {
        if (ips32 && target.Length < source.Length)
        {
            throw new ArgumentException("IPS32 has no truncation: the target cannot be shorter.");
        }

        var footerOffset = ips32 ? 0x45454F46L : 0x454F46L;
        var records = new List<IpsRecord>();
        var i = 0;
        while (i < target.Length)
        {
            if (i < source.Length && source[i] == target[i])
            {
                i++;
                continue;
            }

            var start = i;
            while (i < target.Length && (i >= source.Length || source[i] != target[i]) && i - start < 0xFFFE)
            {
                i++;
            }

            if (start == footerOffset)
            {
                start--;
            }

            var data = target[start..i];
            records.Add(data.Length >= 3 && data.All(b => b == data[0])
                ? IpsRecord.Run(start, data.Length, data[0])
                : new IpsRecord(start, data));
        }

        return Ips(records, target.Length < source.Length ? target.Length : null, ips32);
    }

    // =========================================================================
    // Variable-length numbers (UPS, BPS)
    // =========================================================================

    /// <summary>A variable-length number, encoded as byuu's specifications write it.</summary>
    public static byte[] Number(ulong value)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var x = (byte)(value & 0x7f);
            value >>= 7;
            if (value == 0)
            {
                bytes.Add((byte)(0x80 | x));
                return [.. bytes];
            }

            bytes.Add(x);
            value--;
        }
    }

    /// <summary>A relative offset of BPS: the magnitude shifted left, the sign in the low bit.</summary>
    public static byte[] Offset(long delta) => Number(((ulong)Math.Abs(delta) << 1) | (delta < 0 ? 1UL : 0UL));

    /// <summary>The footer of a UPS or a BPS: CRC32 of the source and of the target, then of everything before.</summary>
    public static byte[] WithFooter(IEnumerable<byte> body, uint sourceCrc, uint targetCrc)
    {
        var patch = new List<byte>(body);
        patch.AddRange(LittleEndian(sourceCrc));
        patch.AddRange(LittleEndian(targetCrc));
        patch.AddRange(LittleEndian(Crc([.. patch])));
        return [.. patch];
    }

    /// <summary>The same patch with its own CRC32 computed again, after a test altered it.</summary>
    public static byte[] Resign(byte[] patch)
    {
        var copy = (byte[])patch.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(copy.Length - 4), Crc(copy.AsSpan(0, copy.Length - 4)));
        return copy;
    }

    // =========================================================================
    // UPS
    // =========================================================================

    /// <summary>A UPS patch from <paramref name="source"/> to <paramref name="target"/>, of any sizes.</summary>
    public static byte[] Ups(byte[] source, byte[] target)
    {
        var body = new List<byte>(Ascii("UPS1"));
        body.AddRange(Number((ulong)source.Length));
        body.AddRange(Number((ulong)target.Length));
        var end = Math.Max(source.Length, target.Length);
        byte Xor(int p) => (byte)((p < source.Length ? source[p] : 0) ^ (p < target.Length ? target[p] : 0));

        var position = 0;
        var last = 0;
        while (position < end)
        {
            if (Xor(position) == 0)
            {
                position++;
                continue;
            }

            body.AddRange(Number((ulong)(position - last)));
            while (position < end && Xor(position) != 0)
            {
                body.Add(Xor(position));
                position++;
            }

            // The terminator takes a position too
            body.Add(0);
            position++;
            last = position;
        }

        return WithFooter(body, Crc(source), Crc(target));
    }

    /// <summary>A UPS patch of a given body (the blocks), for the malformed cases.</summary>
    public static byte[] UpsRaw(ulong sourceSize, ulong targetSize, IEnumerable<byte> blocks, uint sourceCrc = 0, uint targetCrc = 0)
    {
        var body = new List<byte>(Ascii("UPS1"));
        body.AddRange(Number(sourceSize));
        body.AddRange(Number(targetSize));
        body.AddRange(blocks);
        return WithFooter(body, sourceCrc, targetCrc);
    }

    // =========================================================================
    // BPS
    // =========================================================================

    /// <summary>An action of a BPS patch.</summary>
    public abstract record BpsAction;

    /// <summary>Copies the source at the position of the output.</summary>
    public sealed record SourceRead(int Length) : BpsAction;

    /// <summary>Writes bytes held by the patch.</summary>
    public sealed record TargetRead(byte[] Data) : BpsAction;

    /// <summary>Moves the source counter by <see cref="Delta"/>, then copies from the source.</summary>
    public sealed record SourceCopy(long Delta, int Length) : BpsAction;

    /// <summary>Moves the target counter by <see cref="Delta"/>, then copies from the result already written.</summary>
    public sealed record TargetCopy(long Delta, int Length) : BpsAction;

    /// <summary>A BPS patch of the given actions and checksums.</summary>
    public static byte[] Bps(
        ulong sourceSize,
        ulong targetSize,
        IEnumerable<BpsAction> actions,
        uint sourceCrc,
        uint targetCrc,
        byte[]? metadata = null)
    {
        var body = new List<byte>(Ascii("BPS1"));
        body.AddRange(Number(sourceSize));
        body.AddRange(Number(targetSize));
        body.AddRange(Number((ulong)(metadata?.Length ?? 0)));
        body.AddRange(metadata ?? []);
        foreach (var action in actions)
        {
            body.AddRange(EncodeAction(action));
        }

        return WithFooter(body, sourceCrc, targetCrc);
    }

    /// <summary>The bytes of one action.</summary>
    public static byte[] EncodeAction(BpsAction action)
    {
        static byte[] Command(int length, int kind) => Number(((ulong)(length - 1) << 2) | (uint)kind);
        return action switch
        {
            SourceRead read => Command(read.Length, 0),
            TargetRead read => [.. Command(read.Data.Length, 1), .. read.Data],
            SourceCopy copy => [.. Command(copy.Length, 2), .. Offset(copy.Delta)],
            TargetCopy copy => [.. Command(copy.Length, 3), .. Offset(copy.Delta)],
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
    }

    /// <summary>
    /// A BPS patch from <paramref name="source"/> to <paramref name="target"/> with the two simplest actions: the bytes
    /// equal to the source at the same position are read from it, the others written by the patch.
    /// </summary>
    public static byte[] BpsLinear(byte[] source, byte[] target)
    {
        var actions = new List<BpsAction>();
        var i = 0;
        while (i < target.Length)
        {
            var start = i;
            if (i < source.Length && source[i] == target[i])
            {
                while (i < target.Length && i < source.Length && source[i] == target[i])
                {
                    i++;
                }

                actions.Add(new SourceRead(i - start));
            }
            else
            {
                while (i < target.Length && (i >= source.Length || source[i] != target[i]))
                {
                    i++;
                }

                actions.Add(new TargetRead(target[start..i]));
            }
        }

        return Bps((ulong)source.Length, (ulong)target.Length, actions, Crc(source), Crc(target));
    }

    /// <summary>
    /// Actions of a BPS patch drawn from <paramref name="choices"/>, all valid, with the result they produce, computed
    /// here byte by byte from the specification: copies from anywhere in the source, and copies from the result that
    /// may overlap what they write.
    /// </summary>
    public static (List<BpsAction> Actions, byte[] Target) RandomBpsActions(byte[] source, int targetSize, uint[] choices)
    {
        var actions = new List<BpsAction>();
        var target = new List<byte>(targetSize);
        long sourceRelative = 0;
        long targetRelative = 0;
        var c = 0;
        uint Next() => choices.Length == 0 ? 0 : choices[c++ % choices.Length];

        while (target.Count < targetSize)
        {
            var remaining = targetSize - target.Count;
            var length = (int)(Next() % (uint)Math.Min(remaining, 40)) + 1;
            switch (Next() % 4)
            {
                case 0 when target.Count + length <= source.Length:
                    actions.Add(new SourceRead(length));
                    target.AddRange(source.AsSpan(target.Count, length).ToArray());
                    break;

                case 2 when source.Length >= length:
                    {
                        var from = Next() % (uint)(source.Length - length + 1);
                        actions.Add(new SourceCopy(from - sourceRelative, length));
                        target.AddRange(source.AsSpan((int)from, length).ToArray());
                        sourceRelative = from + length;
                        break;
                    }

                case 3 when target.Count > 0:
                    {
                        var from = Next() % (uint)target.Count;
                        actions.Add(new TargetCopy(from - targetRelative, length));
                        for (var k = 0; k < length; k++)
                        {
                            target.Add(target[(int)from + k]);
                        }

                        targetRelative = from + length;
                        break;
                    }

                default:
                    {
                        var data = new byte[length];
                        for (var k = 0; k < length; k++)
                        {
                            data[k] = (byte)Next();
                        }

                        actions.Add(new TargetRead(data));
                        target.AddRange(data);
                        break;
                    }
            }
        }

        return (actions, [.. target]);
    }

    // =========================================================================

    private static byte[] BigEndian(long value, int size)
    {
        var bytes = new byte[size];
        for (var i = size - 1; i >= 0; i--)
        {
            bytes[i] = (byte)(value & 0xFF);
            value >>= 8;
        }

        return bytes;
    }

    private static byte[] LittleEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }
}
