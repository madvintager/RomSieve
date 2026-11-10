using System.IO;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Reads and applies IPS, IPS32, UPS and BPS patches (<see cref="IPatchService"/>). The decoders are
/// <see cref="IpsPatch"/>, <see cref="UpsPatch"/> and <see cref="BpsPatch"/>; their rules and sources are in their
/// comments. Stateless: one instance serves every run.
/// </summary>
public sealed class PatchService : IPatchService
{
    /// <summary>Largest patch read, in bytes: a patch only holds the differences, and is read in memory.</summary>
    public const int MaxPatchSize = 256 * 1024 * 1024;

    /// <summary>
    /// Largest source or result a patch may announce, in bytes (4 GiB): the result is written to a file, so this only
    /// refuses a hostile size before anything is written.
    /// </summary>
    public const long MaxTargetSize = 4L * 1024 * 1024 * 1024;

    /// <summary>Extensions of the patch files read (<see cref="DetectFormat"/>).</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".ips", ".ips32", ".ups", ".bps"];

    /// <summary>Whether <paramref name="fileName"/> has the extension of a patch, case ignored.</summary>
    public static bool HasPatchExtension(string fileName) =>
        Extensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of bytes <see cref="DetectFormat"/> needs to recognize a signature.</summary>
    public const int SignatureLength = 5;

    public PatchFormat? DetectFormat(string fileName, ReadOnlySpan<byte> start)
    {
        if (!HasPatchExtension(fileName))
        {
            return null;
        }

        if (IpsPatch.TryGetFormat(start, out var ips))
        {
            return ips;
        }

        return UpsPatch.IsSignature(start) ? PatchFormat.Ups
            : BpsPatch.IsSignature(start) ? PatchFormat.Bps
            : null;
    }

    public async Task<byte[]> ReadPatchAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (stream.CanSeek && stream.Length - stream.Position > MaxPatchSize)
        {
            throw PatchErrors.PatchTooLarge();
        }

        using var memory = stream.CanSeek ? new MemoryStream((int)(stream.Length - stream.Position)) : new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            // The size announced for an archive member may be false: the limit holds while reading
            if (memory.Length + read > MaxPatchSize)
            {
                throw PatchErrors.PatchTooLarge();
            }

            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }

    public PatchHeader ReadHeader(ReadOnlySpan<byte> patch)
    {
        if (patch.Length > MaxPatchSize)
        {
            throw PatchErrors.PatchTooLarge();
        }

        if (IpsPatch.TryGetFormat(patch, out _))
        {
            return IpsPatch.ReadHeader(patch);
        }

        if (UpsPatch.IsSignature(patch))
        {
            return UpsPatch.ReadHeader(patch);
        }

        if (BpsPatch.IsSignature(patch))
        {
            return BpsPatch.ReadHeader(patch);
        }

        throw patch.Length < 4 ? PatchErrors.Truncated() : PatchErrors.UnknownSignature();
    }

    public Task<PatchResult> ApplyAsync(
        ReadOnlyMemory<byte> patch,
        Stream source,
        long sourceOffset,
        long sourceLength,
        Stream target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceLength);
        if (!source.CanSeek || !source.CanRead)
        {
            throw new ArgumentException("The source of a patch must allow reading and seeking.", nameof(source));
        }

        if (!target.CanSeek || !target.CanRead || !target.CanWrite)
        {
            throw new ArgumentException("The target of a patch must allow reading, writing and seeking.", nameof(target));
        }

        if (patch.Length > MaxPatchSize)
        {
            throw PatchErrors.PatchTooLarge();
        }

        // The decoder is chosen by the signature; it reads and checks the header itself
        var span = patch.Span;
        if (IpsPatch.TryGetFormat(span, out _))
        {
            return IpsPatch.ApplyAsync(patch, source, sourceOffset, sourceLength, target, cancellationToken);
        }

        if (UpsPatch.IsSignature(span))
        {
            return UpsPatch.ApplyAsync(patch, source, sourceOffset, sourceLength, target, cancellationToken);
        }

        return BpsPatch.IsSignature(span)
            ? BpsPatch.ApplyAsync(patch, source, sourceOffset, sourceLength, target, cancellationToken)
            : throw (patch.Length < 4 ? PatchErrors.Truncated() : PatchErrors.UnknownSignature());
    }
}
