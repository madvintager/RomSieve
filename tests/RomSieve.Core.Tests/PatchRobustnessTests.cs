using RomSieve.Core.Models;
using static RomSieve.Core.Tests.Fixtures.PatchTestEncoder;

namespace RomSieve.Core.Tests;

/// <summary>
/// Patches truncated, malformed or hostile: each is refused with a structured reason (<c>patch.error_*</c>), without
/// any other exception, without running endlessly and without allocating what it announces.
/// </summary>
public sealed class PatchRobustnessTests
{
    private static readonly byte[] Source = Ascii("ABCDEFGH");

    private static async Task<string?> RefusalKeyAsync(byte[] patch, byte[]? source = null) =>
        (await PatchRunner.RefusalAsync(patch, source ?? Source))?.Reason.Key;

    [Theory]
    [InlineData("")]
    [InlineData("PAT")]
    [InlineData("BPS")]
    public async Task AFileTooShortForASignature_ShouldBeRefusedAsTruncated(string content)
    {
        (await RefusalKeyAsync(Ascii(content))).Should().Be("patch.error_truncated");
    }

    [Fact]
    public async Task AnUnknownSignature_ShouldBeRefused()
    {
        (await RefusalKeyAsync(Ascii("PK\u0003\u0004 not a patch"))).Should().Be("patch.error_signature");
    }

    [Theory]
    [InlineData(PatchFormat.Ups)]
    [InlineData(PatchFormat.Bps)]
    public async Task AWrongPatchChecksum_ShouldRefuseThePatchBeforeApplyingIt(PatchFormat format)
    {
        var patch = format == PatchFormat.Ups ? Ups(Source, Ascii("ABCxEFGH")) : BpsLinear(Source, Ascii("ABCxEFGH"));
        patch[^1] ^= 0xFF;

        var refusal = await PatchRunner.RefusalAsync(patch, Source);

        refusal!.Reason.Key.Should().Be("patch.error_patch_crc");
        refusal.Reason.Args.Should().ContainKeys("expected", "actual");
    }

    [Fact]
    public async Task ANumberThatOverflows_ShouldBeRefused()
    {
        // Continuation bytes only: the value exceeds 64 bits before the number ends
        var patch = WithFooter([.. Ascii("BPS1"), .. new byte[12], 0x80, 0x80, 0x80], 0, 0);

        (await RefusalKeyAsync(patch)).Should().Be("patch.error_number_overflow");
    }

    [Fact]
    public void AHugeAnnouncedSize_ShouldBeRefusedWithoutAllocatingIt()
    {
        var patch = WithFooter([.. Ascii("BPS1"), .. Number(8), .. Number(1UL << 60), .. Number(0)], Crc(Source), 0);

        var act = () => PatchRunner.Service.ReadHeader(patch);

        act.Should().Throw<PatchException>().Which.Reason.Key.Should().Be("patch.error_target_too_large");
    }

    [Fact]
    public async Task AnIps32RecordBeyondTheLimit_ShouldBeRefused()
    {
        var patch = Ips([new IpsRecord(0xFFFFFFFF, Ascii("xy"))], ips32: true);

        (await RefusalKeyAsync(patch)).Should().Be("patch.error_target_too_large");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(9)]
    public async Task AnIpsRecordCutShort_ShouldBeRefusedAsTruncated(int cut)
    {
        var patch = Ips([new IpsRecord(1, Ascii("xyz"))]);

        (await RefusalKeyAsync(patch[..(patch.Length - cut)])).Should().Be("patch.error_truncated");
    }

    [Fact]
    public async Task MetadataLongerThanThePatch_ShouldBeRefusedAsTruncated()
    {
        var patch = WithFooter([.. Ascii("BPS1"), .. Number(8), .. Number(8), .. Number(1000), 1, 2, 3], Crc(Source), Crc(Source));

        (await RefusalKeyAsync(patch)).Should().Be("patch.error_truncated");
    }

    [Fact]
    public async Task ASourceCopyBeforeTheStartOfTheSource_ShouldBeRefused()
    {
        var patch = Bps(8, 4, [new SourceCopy(-1, 4)], Crc(Source), 0);

        (await RefusalKeyAsync(patch)).Should().Be("patch.error_out_of_bounds");
    }

    [Fact]
    public async Task ASourceCopyPastTheEndOfTheSource_ShouldBeRefused()
    {
        var patch = Bps(8, 4, [new SourceCopy(6, 4)], Crc(Source), 0);

        (await RefusalKeyAsync(patch)).Should().Be("patch.error_out_of_bounds");
    }

    [Fact]
    public async Task ASourceReadPastTheEndOfTheSource_ShouldBeRefused()
    {
        var patch = Bps(8, 10, [new SourceRead(10)], Crc(Source), 0);

        (await RefusalKeyAsync(patch)).Should().Be("patch.error_out_of_bounds");
    }

    [Fact]
    public async Task ATargetCopyOfNothingWrittenYet_ShouldBeRefused()
    {
        var patch = Bps(8, 4, [new TargetCopy(0, 4)], Crc(Source), 0);

        (await RefusalKeyAsync(patch)).Should().Be("patch.error_out_of_bounds");
    }

    [Fact]
    public async Task ATargetCopyFromThePositionOfTheOutput_ShouldBeRefused()
    {
        var patch = Bps(8, 4, [new TargetRead(Ascii("a")), new TargetCopy(1, 3)], Crc(Source), 0);

        (await RefusalKeyAsync(patch)).Should().Be("patch.error_out_of_bounds");
    }

    [Fact]
    public async Task AnActionPastTheEndOfTheResult_ShouldBeRefused()
    {
        var patch = Bps(8, 4, [new SourceRead(8)], Crc(Source), 0);

        (await RefusalKeyAsync(patch)).Should().Be("patch.error_out_of_bounds");
    }

    [Fact]
    public async Task ATargetReadPastTheEndOfThePatch_ShouldBeRefusedAsTruncated()
    {
        // A TargetRead of 8 bytes that holds only 2 before the footer
        var patch = WithFooter([.. Ascii("BPS1"), .. Number(8), .. Number(8), .. Number(0), .. Number((7UL << 2) | 1), 1, 2], Crc(Source), 0);

        (await RefusalKeyAsync(patch)).Should().Be("patch.error_truncated");
    }

    [Fact]
    public async Task ABpsThatProducesLessThanItAnnounces_ShouldBeRefused()
    {
        var patch = Bps(8, 10, [new SourceRead(4)], Crc(Source), 0);

        (await RefusalKeyAsync(patch)).Should().Be("patch.error_incomplete");
    }

    [Fact]
    public async Task AWrongSourceSize_ShouldBeRefusedBeforeApplying()
    {
        var refusal = await PatchRunner.RefusalAsync(BpsLinear(Ascii("ABCD"), Ascii("ABXD")), Source);

        refusal!.Reason.Key.Should().Be("patch.error_source_size");
        refusal.Reason.Args["expected"].Should().Be(4L);
        refusal.Reason.Args["actual"].Should().Be(8L);
    }

    [Fact]
    public async Task AUpsSkipPastBothFiles_ShouldBeRefused()
    {
        var patch = UpsRaw(8, 8, [.. Number(1UL << 40), 0x01, 0x00], Crc(Source), Crc(Source));

        (await RefusalKeyAsync(patch)).Should().Be("patch.error_out_of_bounds");
    }

    [Fact]
    public async Task AUpsDifferencePastBothFiles_ShouldBeRefused()
    {
        // A block from the last byte that runs past both files: only its terminator may
        var patch = UpsRaw(8, 8, [.. Number(7), 0x01, 0x01, 0x00], Crc(Source), Crc(Source));

        (await RefusalKeyAsync(patch)).Should().Be("patch.error_out_of_bounds");
    }

    [Fact]
    public async Task AUpsBlockWithoutTerminator_ShouldBeRefusedAsTruncated()
    {
        var patch = UpsRaw(8, 8, [.. Number(0), 0x01, 0x02], Crc(Source), Crc(Source));

        (await RefusalKeyAsync(patch)).Should().Be("patch.error_truncated");
    }

    [Theory]
    [InlineData(PatchFormat.Ips)]
    [InlineData(PatchFormat.Ups)]
    [InlineData(PatchFormat.Bps)]
    public async Task ACancelledApplication_ShouldStopWithoutAPatchException(PatchFormat format)
    {
        var source = new byte[3 * 1024 * 1024];
        var target = (byte[])source.Clone();
        target[^1] = 1;
        var patch = format switch
        {
            PatchFormat.Ips => IpsDiff(source, target),
            PatchFormat.Ups => Ups(source, target),
            _ => BpsLinear(source, target)
        };
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => PatchRunner.ApplyAsync(patch, source, cancellationToken: cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
