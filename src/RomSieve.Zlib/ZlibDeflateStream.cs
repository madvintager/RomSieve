// ZlibDeflateStream.cs
// ------------------------------------------------------------------
//
// Copyright (c) 2026 MadVintager.
// Part of RomSieve.Zlib, licensed under the Microsoft Public License like the rest of this folder:
// see License.txt and README.md.
//
// ------------------------------------------------------------------

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace RomSieve.Zlib;

/// <summary>
/// Write-only stream that compresses what is written to it into raw Deflate (no zlib or gzip header), byte for byte as
/// zlib does at level 9 with a window of 15 bits, a memory level of 8 and the default strategy: the output TorrentZip
/// requires. Ending the stream (<see cref="FinishAsync"/>, or disposing it) writes its last block; an empty stream is
/// the two bytes <c>03 00</c>, as with zlib.
/// </summary>
public sealed class ZlibDeflateStream : Stream
{
    private const int OutputBufferSize = 64 * 1024;

    private readonly Stream _output;
    private readonly bool _leaveOpen;
    private readonly ZlibCodec _codec = new();
    private readonly byte[] _outputBuffer = new byte[OutputBufferSize];
    private bool _finished;
    private bool _faulted;
    private bool _disposed;

    /// <param name="output">Stream that receives the compressed data.</param>
    /// <param name="leaveOpen">Whether <paramref name="output"/> stays open once this stream is disposed.</param>
    public ZlibDeflateStream(Stream output, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The output stream must be writable.", nameof(output));
        }

        _output = output;
        _leaveOpen = leaveOpen;
        if (_codec.InitializeDeflate(CompressionLevel.BestCompression, ZlibConstants.WindowBitsMax) != ZlibConstants.Z_OK)
        {
            throw new InvalidOperationException("The Deflate compressor could not be initialized: " + _codec.Message);
        }

        _codec.OutputBuffer = _outputBuffer;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => !_disposed && !_finished;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        if (count > 0)
        {
            Compress(buffer, offset, count, FlushType.None);
        }
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (!buffer.IsEmpty)
        {
            var copy = buffer.ToArray();
            Compress(copy, 0, copy.Length, FlushType.None);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        var (array, offset, count) = MemoryMarshal.TryGetArray(buffer, out var segment)
            ? (segment.Array!, segment.Offset, segment.Count)
            : (buffer.ToArray(), 0, buffer.Length);
        await CompressAsync(array, offset, count, FlushType.None, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the end of the Deflate stream; nothing can be written after it.</summary>
    public async Task FinishAsync(CancellationToken cancellationToken = default)
    {
        if (!_finished)
        {
            await CompressAsync([], 0, 0, FlushType.Finish, cancellationToken).ConfigureAwait(false);
            _finished = true;
        }
    }

    /// <summary>Writes the end of the Deflate stream; nothing can be written after it.</summary>
    public void Finish()
    {
        if (!_finished)
        {
            Compress([], 0, 0, FlushType.Finish);
            _finished = true;
        }
    }

    /// <summary>Nothing to flush: a flush would end a block, and change the compressed data.</summary>
    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private void Compress(byte[] input, int offset, int count, FlushType flush)
    {
        SetInput(input, offset, count);
        try
        {
            bool done;
            do
            {
                done = DeflateStep(flush, out var produced);
                _output.Write(_outputBuffer, 0, produced);
            }
            while (!done);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private async Task CompressAsync(byte[] input, int offset, int count, FlushType flush, CancellationToken cancellationToken)
    {
        SetInput(input, offset, count);
        try
        {
            bool done;
            do
            {
                done = DeflateStep(flush, out var produced);
                await _output.WriteAsync(_outputBuffer.AsMemory(0, produced), cancellationToken).ConfigureAwait(false);
            }
            while (!done);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private void SetInput(byte[] input, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished || _faulted)
        {
            throw new InvalidOperationException("The Deflate stream has ended.");
        }

        _codec.InputBuffer = input;
        _codec.NextIn = offset;
        _codec.AvailableBytesIn = count;
    }

    /// <summary>
    /// Runs the compressor once, into the output buffer (<paramref name="produced"/> bytes, to write before the next
    /// step), as zlib's own loop does. Returns whether the step was the last: all the input consumed with room left in
    /// the output buffer or, when ending, the end of the stream written.
    /// </summary>
    private bool DeflateStep(FlushType flush, out int produced)
    {
        _codec.NextOut = 0;
        _codec.AvailableBytesOut = _outputBuffer.Length;
        var result = _codec.Deflate(flush);
        if (result != ZlibConstants.Z_OK && result != ZlibConstants.Z_STREAM_END)
        {
            throw new InvalidOperationException("Deflate compression failed: " + _codec.Message);
        }

        produced = _outputBuffer.Length - _codec.AvailableBytesOut;
        if (flush == FlushType.Finish)
        {
            if (result != ZlibConstants.Z_STREAM_END && produced == 0)
            {
                throw new InvalidOperationException("Deflate compression did not end.");
            }

            return result == ZlibConstants.Z_STREAM_END;
        }

        return _codec.AvailableBytesIn == 0 && _codec.AvailableBytesOut != 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            try
            {
                // A stream that failed is left as it is: writing its end could only fail again
                if (!_faulted)
                {
                    Finish();
                }
            }
            finally
            {
                _disposed = true;
                _codec.EndDeflate();
                if (!_leaveOpen)
                {
                    _output.Dispose();
                }
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            try
            {
                if (!_faulted)
                {
                    await FinishAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _disposed = true;
                _codec.EndDeflate();
                if (!_leaveOpen)
                {
                    await _output.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        GC.SuppressFinalize(this);
    }
}
