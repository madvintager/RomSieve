using RomSieve.Core.Models;
using static RomSieve.Core.Tests.Fixtures.PatchTestEncoder;

namespace RomSieve.Core.Tests;

/// <summary>
/// IPS and IPS32 patches on known vectors, built by hand from the documentation (ZeroSoft, SnesLab) and the reference
/// implementations (Flips, igir, Citron, ips.py): records, runs, growth, truncation, the offset that reads as the footer.
/// </summary>
public sealed class IpsPatchTests
{
    private static readonly byte[] Source = Ascii("ABCDEFGH");

    [Fact]
    public async Task Records_ShouldWriteTheirDataAndRuns()
    {
        // PATCH, 000001 0002 "xy", 000005 0000 0002 'z', EOF
        byte[] patch = [.. Ascii("PATCH"), 0, 0, 1, 0, 2, (byte)'x', (byte)'y', 0, 0, 5, 0, 0, 0, 2, (byte)'z', .. Ascii("EOF")];

        var (output, result) = await PatchRunner.ApplyAsync(patch, Source);

        output.Should().Equal(Ascii("AxyDEzzH"));
        result.Size.Should().Be(8);
        result.Crc32.Should().Be(Crc(output));
        result.SourceCrc32.Should().Be(Crc(Source));
    }

    [Fact]
    public async Task ARecordPastTheEnd_ShouldGrowTheResultWithZeros()
    {
        var patch = Ips([new IpsRecord(10, Ascii("Q"))]);

        var (output, _) = await PatchRunner.ApplyAsync(patch, Source);

        output.Should().Equal([.. Ascii("ABCDEFGH"), 0, 0, (byte)'Q']);
        PatchRunner.Service.ReadHeader(patch).RecordsEnd.Should().Be(11);
    }

    [Fact]
    public async Task OverlappingRecords_ShouldApplyInTheOrderOfTheFile()
    {
        var patch = Ips([new IpsRecord(0, Ascii("aaaa")), new IpsRecord(1, Ascii("b"))]);

        var (output, _) = await PatchRunner.ApplyAsync(patch, Source);

        output.Should().Equal(Ascii("abaaEFGH"));
    }

    [Fact]
    public async Task ARunOfLengthZero_ShouldWriteNothing()
    {
        var patch = Ips([IpsRecord.Run(20, 0, (byte)'Z')]);

        var (output, _) = await PatchRunner.ApplyAsync(patch, Source);

        output.Should().Equal(Source);
    }

    [Fact]
    public async Task Truncation_ShouldShortenTheResult()
    {
        var patch = Ips([new IpsRecord(1, Ascii("b"))], truncate: 4);

        var (output, result) = await PatchRunner.ApplyAsync(patch, Source);

        output.Should().Equal(Ascii("AbCD"));
        result.Crc32.Should().Be(Crc(Ascii("AbCD")));
    }

    [Fact]
    public async Task Truncation_ShouldCutARecordThatGoesBeyondIt()
    {
        var patch = Ips([new IpsRecord(6, Ascii("xyz"))], truncate: 7);

        var (output, _) = await PatchRunner.ApplyAsync(patch, Source);

        output.Should().Equal(Ascii("ABCDEFx"));
    }

    [Fact]
    public async Task ATruncationThatShortensNothing_ShouldBeIgnored()
    {
        var patch = Ips([new IpsRecord(0, Ascii("a"))], truncate: 20);

        var (output, _) = await PatchRunner.ApplyAsync(patch, Source);

        output.Should().Equal(Ascii("aBCDEFGH"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task BytesAfterTheFooter_OtherThanATruncation_ShouldRefuseThePatch(int count)
    {
        var patch = Ips([new IpsRecord(0, Ascii("a"))], afterFooter: new byte[count]);

        (await PatchRunner.RefusalAsync(patch, Source))!.Reason.Key.Should().Be("patch.error_trailing_data");
    }

    [Fact]
    public async Task ARecordAtTheOffsetOfTheFooter_ShouldReadAsTheFooter()
    {
        // 0x454F46 is "EOF": what follows reads as a truncation (0x000158), as in Flips and igir
        byte[] patch = [.. Ascii("PATCH"), 0x45, 0x4F, 0x46, 0x00, 0x01, 0x58];

        var header = PatchRunner.Service.ReadHeader(patch);
        var (output, _) = await PatchRunner.ApplyAsync(patch, Source);

        header.TruncateSize.Should().Be(0x158);
        output.Should().Equal(Source);
    }

    [Fact]
    public async Task TheEncoder_ShouldStartARecordBeforeTheOffsetOfTheFooter()
    {
        var source = new byte[0x454F46 + 8];
        var target = (byte[])source.Clone();
        target[0x454F46] = 0x55;
        target[0x454F47] = 0x66;
        var patch = IpsDiff(source, target);

        var (output, _) = await PatchRunner.ApplyAsync(patch, source);

        output.Should().Equal(target);
    }

    [Fact]
    public async Task Ips32_ShouldReadFourByteOffsetsAndItsOwnFooter()
    {
        // IPS32, 00000002 0001 "x", 00000004 0000 0003 'r', EEOF
        byte[] patch = [.. Ascii("IPS32"), 0, 0, 0, 2, 0, 1, (byte)'x', 0, 0, 0, 4, 0, 0, 0, 3, (byte)'r', .. Ascii("EEOF")];

        var header = PatchRunner.Service.ReadHeader(patch);
        var (output, _) = await PatchRunner.ApplyAsync(patch, Source);

        header.Format.Should().Be(PatchFormat.Ips32);
        output.Should().Equal(Ascii("ABxDrrrH"));
    }

    [Fact]
    public async Task Ips32_ShouldHaveNoTruncation()
    {
        var patch = Ips([new IpsRecord(0, Ascii("a"))], ips32: true, afterFooter: [0, 0, 4]);

        (await PatchRunner.RefusalAsync(patch, Source))!.Reason.Key.Should().Be("patch.error_trailing_data");
    }

    [Fact]
    public void Ips32_ShouldEndAtItsFooterOnly()
    {
        // The IPS footer "EOF" is no footer for IPS32: it reads as the start of a record, which is truncated
        var patch = Ips([new IpsRecord(0, Ascii("a"))], ips32: true).SkipLast(4).Concat(Ascii("EOF")).ToArray();

        var act = () => PatchRunner.Service.ReadHeader(patch);

        act.Should().Throw<PatchException>().Which.Reason.Key.Should().Be("patch.error_truncated");
    }

    [Fact]
    public void AnIpsPatch_ShouldRecordNoChecksum()
    {
        var header = PatchRunner.Service.ReadHeader(Ips([new IpsRecord(0, Ascii("a"))]));

        header.HasChecksums.Should().BeFalse();
        header.SourceCrc32.Should().BeNull();
        header.TargetCrc32.Should().BeNull();
    }
}
