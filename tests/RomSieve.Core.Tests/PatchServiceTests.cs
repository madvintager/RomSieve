using Microsoft.Extensions.DependencyInjection;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using static RomSieve.Core.Tests.Fixtures.PatchTestEncoder;

namespace RomSieve.Core.Tests;

/// <summary>Applies a patch held in memory to a source held in memory, as the tests of the decoders need.</summary>
internal static class PatchRunner
{
    public static readonly PatchService Service = new();

    public static async Task<(byte[] Output, PatchResult Result)> ApplyAsync(
        byte[] patch,
        byte[] source,
        long offset = 0,
        long? length = null,
        CancellationToken cancellationToken = default)
    {
        using var sourceStream = new MemoryStream(source, writable: false);
        using var target = new MemoryStream();
        var result = await Service.ApplyAsync(patch, sourceStream, offset, length ?? source.Length - offset, target, cancellationToken);
        return (target.ToArray(), result);
    }

    /// <summary>The refusal of a patch by its header or by its application, or <see langword="null"/> when it applies.</summary>
    public static async Task<PatchException?> RefusalAsync(byte[] patch, byte[] source)
    {
        try
        {
            Service.ReadHeader(patch);
            await ApplyAsync(patch, source);
            return null;
        }
        catch (PatchException ex)
        {
            return ex;
        }
    }
}

/// <summary>
/// The patch service as a whole: format detection by extension and signature, reading a patch under its size limit,
/// the streams it accepts, and its registration in the container.
/// </summary>
public sealed class PatchServiceTests
{
    private static readonly PatchService Service = PatchRunner.Service;

    [Theory]
    [InlineData("Game (T-En).ips", "PATCH", PatchFormat.Ips)]
    [InlineData("Game.IPS", "IPS32", PatchFormat.Ips32)]
    [InlineData("Game.ips32", "IPS32", PatchFormat.Ips32)]
    [InlineData("Game.ups", "UPS1", PatchFormat.Ups)]
    [InlineData("Game.bps", "BPS1", PatchFormat.Bps)]
    public void DetectFormat_ShouldReadTheSignatureOfAPatchFile(string name, string signature, PatchFormat expected)
    {
        Service.DetectFormat(name, Ascii(signature + "\0\0\0")).Should().Be(expected);
    }

    [Theory]
    [InlineData("Game.sfc", "PATCH")]
    [InlineData("Game.ips", "PK\u0003\u0004")]
    [InlineData("Game.bps", "BPS")]
    [InlineData("readme.txt", "UPS1")]
    public void DetectFormat_ShouldRefuseAnotherExtensionOrSignature(string name, string start)
    {
        Service.DetectFormat(name, Ascii(start)).Should().BeNull();
    }

    [Fact]
    public async Task ReadPatchAsync_ShouldReadTheWholePatch()
    {
        var patch = BpsLinear(Ascii("ABCD"), Ascii("ABXD"));

        var read = await Service.ReadPatchAsync(new MemoryStream(patch));

        read.Should().Equal(patch);
    }

    [Fact]
    public async Task ReadPatchAsync_ShouldRefuseAPatchLargerThanTheLimitBeforeReadingIt()
    {
        var act = () => Service.ReadPatchAsync(new HugeStream(PatchService.MaxPatchSize + 1L));

        (await act.Should().ThrowAsync<PatchException>()).Which.Reason.Key.Should().Be("patch.error_patch_too_large");
    }

    [Fact]
    public async Task ApplyAsync_ShouldRefuseASourceThatCannotSeek()
    {
        var patch = Ups(Ascii("AB"), Ascii("AC"));

        var act = () => Service.ApplyAsync(patch, new NonSeekableStream(Ascii("AB")), 0, 2, new MemoryStream());

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ApplyAsync_ShouldSkipACopierHeaderOfTheSource()
    {
        // A 16-byte header the patch does not expect, before the 4 bytes it patches
        var source = new byte[16].Concat(Ascii("ABCD")).ToArray();
        var patch = BpsLinear(Ascii("ABCD"), Ascii("ABXD"));

        var (output, result) = await PatchRunner.ApplyAsync(patch, source, offset: 16);

        output.Should().Equal(Ascii("ABXD"));
        result.SourceCrc32.Should().Be(Crc(Ascii("ABCD")));
    }

    [Fact]
    public async Task ApplyAsync_ShouldWriteToAFile()
    {
        using var temp = new TempDirectoryFixture();
        var source = Ascii("The original ROM");
        var expected = Ascii("The patched ROM!");
        var path = temp.GetFilePath("result.bin");

        await using (var target = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
        {
            await Service.ApplyAsync(Ups(source, expected), new MemoryStream(source, writable: false), 0, source.Length, target);
        }

        File.ReadAllBytes(path).Should().Equal(expected);
    }

    [Fact]
    public void Container_ShouldResolveThePatchService()
    {
        using var provider = new ServiceCollection().AddRomSieveCore().BuildServiceProvider();

        provider.GetRequiredService<IPatchService>().Should().BeOfType<PatchService>();
    }

    [Theory]
    [InlineData(100, null, 50, 100)]
    [InlineData(100, null, 150, 150)]
    [InlineData(100, 60L, 50, 60)]
    [InlineData(100, 200L, 50, 100)]
    public void TargetSizeFor_ShouldGrowAnIpsResultToItsRecordsAndTruncateIt(long sourceSize, long? truncate, long recordsEnd, long expected)
    {
        var header = new PatchHeader(PatchFormat.Ips) { RecordsEnd = recordsEnd, TruncateSize = truncate };

        header.TargetSizeFor(sourceSize).Should().Be(expected);
    }

    /// <summary>A stream that says it is huge, and holds nothing: its size alone must refuse it.</summary>
    private sealed class HugeStream(long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length { get; } = length;
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Read past the limit");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
