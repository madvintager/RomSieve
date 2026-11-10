using RomSieve.Core.Models;
using static RomSieve.Core.Tests.Fixtures.PatchTestEncoder;

namespace RomSieve.Core.Tests;

/// <summary>
/// BPS patches on known vectors, built from byuu's specification: the four actions, relative offsets in both
/// directions, a copy of the result that overlaps what it writes, metadata, copies that read back a result too large
/// for the write buffer.
/// </summary>
public sealed class BpsPatchTests
{
    [Fact]
    public async Task AHandBuiltPatch_ShouldGiveItsResult()
    {
        // BPS1, sizes 4 and 9, no metadata; SourceRead 4, TargetCopy 4 from 0, TargetRead "!"
        var source = Ascii("ABCD");
        var target = Ascii("ABCDABCD!");
        var patch = WithFooter([.. Ascii("BPS1"), 0x84, 0x89, 0x80, 0x8C, 0x8F, 0x80, 0x81, (byte)'!'], Crc(source), Crc(target));

        var (output, result) = await PatchRunner.ApplyAsync(patch, source);

        output.Should().Equal(target);
        result.Size.Should().Be(9);
        result.Crc32.Should().Be(Crc(target));
        result.SourceCrc32.Should().Be(Crc(source));
    }

    [Fact]
    public async Task ATargetCopyThatOverlapsWhatItWrites_ShouldRepeatTheData()
    {
        var target = Ascii("AAAAAAAAAA");
        var patch = Bps(0, 10, [new TargetRead(Ascii("A")), new TargetCopy(0, 9)], Crc([]), Crc(target));

        var (output, _) = await PatchRunner.ApplyAsync(patch, []);

        output.Should().Equal(target);
    }

    [Fact]
    public async Task ATargetCopyWithALongerPeriod_ShouldRepeatThePeriod()
    {
        var target = Ascii("abcabcabcab");
        var patch = Bps(0, 11, [new TargetRead(Ascii("abc")), new TargetCopy(0, 8)], Crc([]), Crc(target));

        var (output, _) = await PatchRunner.ApplyAsync(patch, []);

        output.Should().Equal(target);
    }

    [Fact]
    public async Task SourceCopies_ShouldMoveTheirCounterBackAndForth()
    {
        var source = Ascii("ABCDEFGH");
        var target = Ascii("EFGHABCDCD");
        var patch = Bps(8, 10, [new SourceCopy(4, 4), new SourceCopy(-8, 4), new SourceCopy(-2, 2)], Crc(source), Crc(target));

        var (output, _) = await PatchRunner.ApplyAsync(patch, source);

        output.Should().Equal(target);
    }

    [Fact]
    public async Task Metadata_ShouldBeSkipped()
    {
        var source = Ascii("ABCD");
        var target = Ascii("ABCD");
        var patch = Bps(4, 4, [new SourceRead(4)], Crc(source), Crc(target), metadata: Ascii("<?xml version=\"1.0\"?><patch/>"));

        var (output, _) = await PatchRunner.ApplyAsync(patch, source);

        output.Should().Equal(target);
    }

    [Fact]
    public void TheHeader_ShouldGiveTheSizesAndTheChecksums()
    {
        var source = Ascii("ABCD");
        var target = Ascii("ABXDE");
        var patch = BpsLinear(source, target);

        var header = PatchRunner.Service.ReadHeader(patch);

        header.Format.Should().Be(PatchFormat.Bps);
        header.SourceSize.Should().Be(4);
        header.TargetSize.Should().Be(5);
        header.SourceCrc32.Should().Be(Crc(source));
        header.TargetCrc32.Should().Be(Crc(target));
        header.HasChecksums.Should().BeTrue();
    }

    [Fact]
    public async Task CopiesOfAResultLargerThanTheWriteBuffer_ShouldReadItBack()
    {
        // 64 KiB of data, repeated to 2 MiB, then copies from the start, long written out of the 1 MiB buffer
        var random = new Random(1234);
        var block = new byte[64 * 1024];
        random.NextBytes(block);
        List<BpsAction> actions =
        [
            new TargetRead(block),
            new TargetCopy(0, 2 * 1024 * 1024 - block.Length),
            new TargetCopy(10 - (2 * 1024 * 1024 - block.Length), 100_000),
            new TargetCopy(-500, 1_500_000)
        ];
        var expected = new List<byte>(block);
        void Copy(int from, int count)
        {
            for (var k = 0; k < count; k++)
            {
                expected.Add(expected[from + k]);
            }
        }

        Copy(0, 2 * 1024 * 1024 - block.Length);
        Copy(10, 100_000);
        Copy(10 + 100_000 - 500, 1_500_000);
        var target = expected.ToArray();
        var patch = Bps(0, (ulong)target.Length, actions, Crc([]), Crc(target));

        var (output, result) = await PatchRunner.ApplyAsync(patch, []);

        output.Should().Equal(target);
        result.Crc32.Should().Be(Crc(target));
    }

    [Fact]
    public async Task AWrongSourceOfTheRightSize_ShouldBeRefusedBeforeApplying()
    {
        var patch = BpsLinear(Ascii("ABCD"), Ascii("ABXD"));

        (await PatchRunner.RefusalAsync(patch, Ascii("WXYZ")))!.Reason.Key.Should().Be("patch.error_source_crc");
    }

    [Fact]
    public async Task AWrongTargetChecksum_ShouldRefuseTheResult()
    {
        var source = Ascii("ABCD");
        var patch = Bps(4, 4, [new SourceRead(4)], Crc(source), targetCrc: 0x12345678);

        var refusal = await PatchRunner.RefusalAsync(patch, source);

        refusal!.Reason.Key.Should().Be("patch.error_target_crc");
        refusal.Reason.Args["expected"].Should().Be("12345678");
        refusal.Reason.Args["actual"].Should().Be(Crc(source).ToString("x8"));
    }
}
