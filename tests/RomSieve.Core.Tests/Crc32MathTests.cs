using System.IO.Hashing;
using CsCheck;
using RomSieve.Core.Services;

namespace RomSieve.Core.Tests;

/// <summary>
/// The CRC32 of two blocks end to end, from the CRC32 of each (<see cref="Crc32Math.Combine"/>), as the CRC32 of a patched
/// version is known before it is written behind the header of its origin: equal to the CRC32 computed on the whole, for
/// random blocks (CsCheck, a failure prints the seed that replays it) and at the edges.
/// </summary>
public sealed class Crc32MathTests
{
    [Fact]
    public void Combine_ShouldGiveTheCrc32OfTheTwoBlocksEndToEnd() =>
        Gen.Byte.Array[0, 3000].Select(Gen.Byte.Array[0, 3000]).Sample((first, second) =>
            Crc32Math.Combine(Crc32.HashToUInt32(first), Crc32.HashToUInt32(second), second.Length)
                == Crc32.HashToUInt32([.. first, .. second]),
            iter: 300);

    [Fact]
    public void Combine_WithAnEmptySecondBlock_ShouldGiveTheFirstCrc32()
    {
        var first = Crc32.HashToUInt32("NES\u001a"u8);

        Crc32Math.Combine(first, Crc32.HashToUInt32([]), 0).Should().Be(first);
    }

    [Fact]
    public void Combine_ShouldHoldForALargeSecondBlock()
    {
        // A length with many bits, above 4 GiB once shifted: the loop on the bits of the length goes to its end
        var header = new byte[16];
        header[0] = 0x4E;
        var data = new byte[3 * 1024 * 1024 + 7];
        new Random(5).NextBytes(data);

        Crc32Math.Combine(Crc32.HashToUInt32(header), Crc32.HashToUInt32(data), data.Length)
            .Should().Be(Crc32.HashToUInt32([.. header, .. data]));
    }

    [Fact]
    public void Combine_ShouldRefuseANegativeLength()
    {
        var act = () => Crc32Math.Combine(0, 0, -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
