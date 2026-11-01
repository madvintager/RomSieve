using System.IO;

namespace RomSieve.Core.Tests.Fixtures;

/// <summary>
/// Stream that fails as a full disk, a lost network share or an unwritable journal would: once a number of bytes is
/// read or written, or after a number of write calls, it throws the exception of <c>error</c>, and keeps throwing.
/// </summary>
internal sealed class FailingStream(Stream inner, Func<Exception> error) : Stream
{
    private long _read;
    private long _written;
    private int _writes;
    private bool _failed;

    /// <summary>Bytes written before the failure: what fits is written, as a disk fills up.</summary>
    public long FailAfterBytesWritten { get; init; } = long.MaxValue;

    /// <summary>Bytes read before the failure.</summary>
    public long FailAfterBytesRead { get; init; } = long.MaxValue;

    /// <summary>Write calls that succeed before the failure (a journal writes one record per call).</summary>
    public int FailAfterWrites { get; init; } = int.MaxValue;

    /// <summary>Exception of a full disk, as .NET reports it under Windows (<c>ERROR_DISK_FULL</c>).</summary>
    public static IOException DiskFull() => new("There is not enough space on the disk.", unchecked((int)0x80070070));

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush()
    {
        ThrowIfFailed();
        inner.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        Flush();
        return Task.CompletedTask;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_failed || _read >= FailAfterBytesRead)
        {
            Fail();
        }

        var read = inner.Read(buffer);
        _read += read;
        return read;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer.AsSpan(offset, count)));

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfFailed();
        if (++_writes > FailAfterWrites)
        {
            Fail();
        }

        if (_written + buffer.Length > FailAfterBytesWritten)
        {
            var fits = (int)Math.Max(0, FailAfterBytesWritten - _written);
            inner.Write(buffer[..fits]);
            _written += fits;
            Fail();
        }

        inner.Write(buffer);
        _written += buffer.Length;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer.AsSpan(offset, count));
        return Task.CompletedTask;
    }

    public override void WriteByte(byte value) => Write([value]);

    private void ThrowIfFailed()
    {
        if (_failed)
        {
            throw error();
        }
    }

    private void Fail()
    {
        _failed = true;
        throw error();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => inner.DisposeAsync();
}
