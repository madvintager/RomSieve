using System.Buffers.Binary;
using System.IO;
using System.IO.Hashing;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>The refusals of a patch, as structured messages (<c>patch.error_*</c>).</summary>
internal static class PatchErrors
{
    public static PatchException Truncated() => new(LocalizedMessage.Create("patch.error_truncated"));

    public static PatchException UnknownSignature() => new(LocalizedMessage.Create("patch.error_signature"));

    public static PatchException NumberOverflow() => new(LocalizedMessage.Create("patch.error_number_overflow"));

    public static PatchException OutOfBounds() => new(LocalizedMessage.Create("patch.error_out_of_bounds"));

    public static PatchException TrailingData() => new(LocalizedMessage.Create("patch.error_trailing_data"));

    public static PatchException Incomplete() => new(LocalizedMessage.Create("patch.error_incomplete"));

    public static PatchException PatchTooLarge() =>
        new(LocalizedMessage.Create("patch.error_patch_too_large", ("max", PatchService.MaxPatchSize / (1024 * 1024))));

    public static PatchException TargetTooLarge(ulong size) =>
        new(LocalizedMessage.Create("patch.error_target_too_large", ("size", size), ("max", PatchService.MaxTargetSize / (1024 * 1024 * 1024))));

    public static PatchException PatchCrc(uint expected, uint actual) =>
        new(LocalizedMessage.Create("patch.error_patch_crc", ("expected", FormatCrc(expected)), ("actual", FormatCrc(actual))));

    public static PatchException SourceSize(long expected, long actual) =>
        new(LocalizedMessage.Create("patch.error_source_size", ("expected", expected), ("actual", actual)));

    public static PatchException SourceCrc(uint expected, uint actual) =>
        new(LocalizedMessage.Create("patch.error_source_crc", ("expected", FormatCrc(expected)), ("actual", FormatCrc(actual))));

    public static PatchException TargetCrc(uint expected, uint actual) =>
        new(LocalizedMessage.Create("patch.error_target_crc", ("expected", FormatCrc(expected)), ("actual", FormatCrc(actual))));

    /// <summary>A CRC32 as RomSieve writes it everywhere: 8 lowercase hexadecimal digits.</summary>
    public static string FormatCrc(uint crc) => crc.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Reads a patch held in memory, up to a limit (the start of the footer of a UPS or a BPS): bytes, slices and the
/// variable-length numbers of UPS and BPS. Reading past the limit refuses the patch as truncated.
/// </summary>
internal sealed class PatchCursor(ReadOnlyMemory<byte> data, int position, int limit)
{
    private int _position = position;

    public ReadOnlyMemory<byte> Data { get; } = data;

    public int Position => _position;

    public int Limit { get; } = limit;

    public bool AtLimit => _position >= Limit;

    public byte ReadByte() => _position < Limit ? Data.Span[_position++] : throw PatchErrors.Truncated();

    public ReadOnlyMemory<byte> Read(long count)
    {
        if (count < 0 || count > Limit - _position)
        {
            throw PatchErrors.Truncated();
        }

        var slice = Data.Slice(_position, (int)count);
        _position += (int)count;
        return slice;
    }

    public ulong ReadNumber() => PatchNumbers.Read(Data.Span, ref _position, Limit);

    /// <summary>
    /// A relative offset of BPS added to <paramref name="current"/>: the low bit is the sign, the others the magnitude
    /// (<c>bps_spec</c>). The result must stay within <c>[0, <paramref name="upper"/>]</c>.
    /// </summary>
    public long ReadRelativeOffset(long current, long upper)
    {
        var encoded = ReadNumber();
        var magnitude = encoded >> 1;
        if ((encoded & 1) != 0)
        {
            return magnitude <= (ulong)current ? current - (long)magnitude : throw PatchErrors.OutOfBounds();
        }

        return magnitude <= (ulong)(upper - current) ? current + (long)magnitude : throw PatchErrors.OutOfBounds();
    }
}

/// <summary>The variable-length numbers of UPS and BPS.</summary>
internal static class PatchNumbers
{
    /// <summary>
    /// A variable-length number (byuu's <c>bps_spec</c> and <c>ups-spec</c>): 7 bits per byte, the last byte marked by
    /// its high bit, and one added at each continuation so that every value has a single encoding. The specification
    /// works with 64-bit integers and sets no maximum: a number that does not fit in 64 bits refuses the patch, as Flips
    /// does (<c>try_shift</c>, <c>try_add</c>). Reading past <paramref name="limit"/> refuses it as truncated.
    /// </summary>
    public static ulong Read(ReadOnlySpan<byte> data, ref int position, int limit)
    {
        ulong value = 0;
        ulong shift = 1;
        try
        {
            while (true)
            {
                if (position >= limit)
                {
                    throw PatchErrors.Truncated();
                }

                var x = data[position++];
                checked
                {
                    value += (ulong)(x & 0x7f) * shift;
                    if ((x & 0x80) != 0)
                    {
                        return value;
                    }

                    shift *= 128;
                    value += shift;
                }
            }
        }
        catch (OverflowException)
        {
            throw PatchErrors.NumberOverflow();
        }
    }

    /// <summary>A size (of a source or of a result), refused beyond <see cref="PatchService.MaxTargetSize"/>.</summary>
    public static long ReadSize(ReadOnlySpan<byte> data, ref int position, int limit)
    {
        var size = Read(data, ref position, limit);
        return size <= (ulong)PatchService.MaxTargetSize ? (long)size : throw PatchErrors.TargetTooLarge(size);
    }
}

/// <summary>
/// The footer shared by UPS and BPS: CRC32 of the source, of the result and of the patch, little-endian (Flips;
/// the specifications do not say). The CRC32 of the patch covers every byte before it, and is checked here.
/// </summary>
internal readonly record struct PatchFooter(uint SourceCrc32, uint TargetCrc32, uint PatchCrc32)
{
    public const int Size = 12;

    public static PatchFooter ReadAndCheck(ReadOnlySpan<byte> patch)
    {
        var footer = new PatchFooter(
            BinaryPrimitives.ReadUInt32LittleEndian(patch[^12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(patch[^8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(patch[^4..]));
        var actual = Crc32.HashToUInt32(patch[..^4]);
        return actual == footer.PatchCrc32 ? footer : throw PatchErrors.PatchCrc(footer.PatchCrc32, actual);
    }
}

/// <summary>
/// Reads the source of a patch from start to end, in blocks, computing its CRC32: the bytes after its end read as
/// zeros where a format says so (UPS).
/// </summary>
internal sealed class SequentialSource
{
    private const int BufferSize = 64 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[BufferSize];
    private readonly Crc32 _crc = new();
    private long _remaining;
    private int _start;
    private int _count;

    public SequentialSource(Stream stream, long offset, long length)
    {
        _stream = stream;
        _remaining = length;
        stream.Seek(offset, SeekOrigin.Begin);
    }

    /// <summary>CRC32 of the bytes read so far: of the whole source once <see cref="DrainAsync"/> has run.</summary>
    public uint Crc32 => _crc.GetCurrentHashAsUInt32();

    /// <summary>The next byte of the source, or -1 after its end.</summary>
    public ValueTask<int> ReadByteAsync(CancellationToken cancellationToken) =>
        _start < _count ? ValueTask.FromResult<int>(_buffer[_start++]) : RefillThenReadAsync(cancellationToken);

    private async ValueTask<int> RefillThenReadAsync(CancellationToken cancellationToken) =>
        await RefillAsync(cancellationToken).ConfigureAwait(false) ? _buffer[_start++] : -1;

    /// <summary>Fills <paramref name="destination"/> as far as the source goes; returns the number of bytes read.</summary>
    public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < destination.Length)
        {
            if (_start == _count && !await RefillAsync(cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            var n = Math.Min(destination.Length - total, _count - _start);
            _buffer.AsMemory(_start, n).CopyTo(destination[total..]);
            _start += n;
            total += n;
        }

        return total;
    }

    /// <summary>Reads what remains of the source, for its CRC32.</summary>
    public async ValueTask DrainAsync(CancellationToken cancellationToken)
    {
        _start = _count;
        while (await RefillAsync(cancellationToken).ConfigureAwait(false))
        {
            _start = _count;
        }
    }

    private async ValueTask<bool> RefillAsync(CancellationToken cancellationToken)
    {
        if (_remaining == 0)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var wanted = (int)Math.Min(BufferSize, _remaining);
        await _stream.ReadExactlyAsync(_buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
        _crc.Append(_buffer.AsSpan(0, wanted));
        _remaining -= wanted;
        _start = 0;
        _count = wanted;
        return true;
    }
}

/// <summary>
/// Writes the result of a patch in order, through a buffer of 1 MiB, computing its CRC32, and reads back what it has
/// already written (a BPS <c>TargetCopy</c>): from the buffer when it is still there, otherwise from the stream.
/// </summary>
internal sealed class PatchOutput
{
    private const int BufferSize = 1024 * 1024;

    private readonly Stream _stream;
    private readonly long _origin;
    private readonly byte[] _buffer = new byte[BufferSize];
    private readonly Crc32 _crc = new();
    private int _count;
    private long _flushed;

    public PatchOutput(Stream stream)
    {
        _stream = stream;
        _origin = stream.Position;
    }

    /// <summary>Number of bytes written so far.</summary>
    public long Length => _flushed + _count;

    public ValueTask WriteByteAsync(byte value, CancellationToken cancellationToken)
    {
        if (_count == BufferSize)
        {
            return FlushThenWriteAsync(value, cancellationToken);
        }

        _buffer[_count++] = value;
        return ValueTask.CompletedTask;
    }

    private async ValueTask FlushThenWriteAsync(byte value, CancellationToken cancellationToken)
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        _buffer[_count++] = value;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        while (!data.IsEmpty)
        {
            if (_count == BufferSize)
            {
                await FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var n = Math.Min(data.Length, BufferSize - _count);
            data[..n].CopyTo(_buffer.AsMemory(_count));
            _count += n;
            data = data[n..];
        }
    }

    /// <summary>Copies into <paramref name="destination"/> what was written from <paramref name="position"/> on.</summary>
    public async ValueTask ReadBackAsync(long position, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (position >= _flushed)
        {
            _buffer.AsMemory((int)(position - _flushed), destination.Length).CopyTo(destination);
            return;
        }

        await FlushAsync(cancellationToken).ConfigureAwait(false);
        _stream.Seek(_origin + position, SeekOrigin.Begin);
        await _stream.ReadExactlyAsync(destination, cancellationToken).ConfigureAwait(false);
        _stream.Seek(_origin + _flushed, SeekOrigin.Begin);
    }

    /// <summary>Writes what the buffer holds, and gives the CRC32 of the whole result.</summary>
    public async ValueTask<uint> CompleteAsync(CancellationToken cancellationToken)
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return _crc.GetCurrentHashAsUInt32();
    }

    private async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        if (_count == 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _crc.Append(_buffer.AsSpan(0, _count));
        await _stream.WriteAsync(_buffer.AsMemory(0, _count), cancellationToken).ConfigureAwait(false);
        _flushed += _count;
        _count = 0;
    }
}
