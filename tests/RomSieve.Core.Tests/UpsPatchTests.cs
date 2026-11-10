using RomSieve.Core.Models;
using static RomSieve.Core.Tests.Fixtures.PatchTestEncoder;

namespace RomSieve.Core.Tests;

/// <summary>
/// UPS patches on known vectors, built from byuu's specification and checked against the reference implementations
/// (Flips, igir): XOR blocks, a result longer or shorter than the source, the three checksums.
/// </summary>
public sealed class UpsPatchTests
{
    [Fact]
    public async Task AHandBuiltPatch_ShouldGiveItsResult()
    {
        // UPS1, sizes 4 and 4, skip 2, 'C' ^ 'x', terminator, then the three CRC32
        var source = Ascii("ABCD");
        var target = Ascii("ABxD");
        var patch = WithFooter([.. Ascii("UPS1"), 0x84, 0x84, 0x82, (byte)('C' ^ 'x'), 0x00], Crc(source), Crc(target));

        var (output, result) = await PatchRunner.ApplyAsync(patch, source);

        output.Should().Equal(target);
        result.Crc32.Should().Be(Crc(target));
        result.SourceCrc32.Should().Be(Crc(source));
        patch.Should().Equal(Ups(source, target), "the test encoder writes the same patch");
    }

    [Fact]
    public void TheHeader_ShouldGiveTheSizesAndTheChecksums()
    {
        var source = Ascii("ABCD");
        var target = Ascii("ABxDEF");
        var patch = Ups(source, target);

        var header = PatchRunner.Service.ReadHeader(patch);

        header.Format.Should().Be(PatchFormat.Ups);
        header.SourceSize.Should().Be(4);
        header.TargetSize.Should().Be(6);
        header.SourceCrc32.Should().Be(Crc(source));
        header.TargetCrc32.Should().Be(Crc(target));
        header.PatchCrc32.Should().Be(Crc(patch.AsSpan(0, patch.Length - 4)));
        header.TargetSizeFor(4).Should().Be(6);
    }

    [Fact]
    public async Task AResultLongerThanTheSource_ShouldReadTheSourceAsZerosPastItsEnd()
    {
        var source = Ascii("AB");
        var target = Ascii("ABCD\0F");

        var (output, _) = await PatchRunner.ApplyAsync(Ups(source, target), source);

        output.Should().Equal(target);
    }

    [Fact]
    public async Task AResultShorterThanTheSource_ShouldBeCut()
    {
        // The patch still records the bytes the result drops, as a reverse application needs
        var source = Ascii("ABCDEF");
        var target = Ascii("ABC");

        var (output, result) = await PatchRunner.ApplyAsync(Ups(source, target), source);

        output.Should().Equal(target);
        result.SourceCrc32.Should().Be(Crc(source));
    }

    [Fact]
    public async Task AnIdenticalResult_ShouldNeedNoBlock()
    {
        var source = Ascii("Same data");
        var patch = Ups(source, source);

        var (output, _) = await PatchRunner.ApplyAsync(patch, source);

        output.Should().Equal(source);
        patch.Length.Should().Be(4 + 2 + 12);
    }

    [Fact]
    public async Task TheReverseDirection_ShouldNotBeApplied()
    {
        // UPS is reversible, but RomSieve applies a patch to its original ROM only
        var source = Ascii("ABCDEF");
        var target = Ascii("ABC");

        (await PatchRunner.RefusalAsync(Ups(source, target), target))!.Reason.Key.Should().Be("patch.error_source_size");
    }

    [Fact]
    public async Task AWrongSourceOfTheRightSize_ShouldBeRefusedByItsChecksum()
    {
        var patch = Ups(Ascii("ABCD"), Ascii("ABxD"));

        var refusal = await PatchRunner.RefusalAsync(patch, Ascii("WXYZ"));

        refusal!.Reason.Key.Should().Be("patch.error_source_crc");
        refusal.Reason.Args["expected"].Should().Be(Crc(Ascii("ABCD")).ToString("x8"));
    }
}
