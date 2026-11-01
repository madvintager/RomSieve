using System.IO;

namespace RomSieve.Core.Services;

/// <summary>
/// Read-only stream of the decompressed data of an archive member (ZIP, 7z or RAR), which owns the open archive:
/// disposing the stream closes the archive. <see cref="ExpectedCrc32"/> is the CRC32 recorded in the archive.
/// </summary>
public sealed class ArchiveMemberStream : Stream
{
    private readonly Stream _inner;
    private readonly IDisposable _archive;

    public ArchiveMemberStream(Stream inner, IDisposable archive, string expectedCrc32)
    {
        _inner = inner;
        _archive = archive;
        ExpectedCrc32 = expectedCrc32;
    }

    /// <summary>CRC32 of the member recorded in the archive (8 lowercase hex characters), empty when the archive has none.</summary>
    public string ExpectedCrc32 { get; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _archive.Dispose();
        }

        base.Dispose(disposing);
    }
}
