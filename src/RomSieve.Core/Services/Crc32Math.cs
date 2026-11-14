namespace RomSieve.Core.Services;

/// <summary>
/// Arithmetic on the CRC32 of <see cref="System.IO.Hashing.Crc32"/> (reflected polynomial <c>0xEDB88320</c>, as in ZIP
/// and the DATs): the CRC32 of two blocks put end to end, from the CRC32 of each and the length of the second, without
/// reading their data again.
/// </summary>
internal static class Crc32Math
{
    private const uint Polynomial = 0xEDB88320;

    /// <summary>
    /// The CRC32 of a block of CRC32 <paramref name="first"/> followed by a block of CRC32 <paramref name="second"/> and
    /// <paramref name="secondLength"/> bytes. The second block is "appended" to the first by running
    /// <paramref name="secondLength"/> zero bytes through the first CRC, as square-and-multiply on the GF(2) matrix of one
    /// zero bit, then the two values are combined by exclusive or.
    /// </summary>
    public static uint Combine(uint first, uint second, long secondLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(secondLength);
        if (secondLength == 0)
        {
            return first;
        }

        // The operator of one zero bit, then of two, then of four
        var odd = new uint[32];
        var even = new uint[32];
        odd[0] = Polynomial;
        uint row = 1;
        for (var n = 1; n < 32; n++)
        {
            odd[n] = row;
            row <<= 1;
        }

        Square(even, odd);
        Square(odd, even);

        // Each bit of the length (in bytes) applies the operator of 8, 16, 32... zero bits to the first CRC
        var length = secondLength;
        do
        {
            Square(even, odd);
            if ((length & 1) != 0)
            {
                first = Times(even, first);
            }

            length >>= 1;
            if (length == 0)
            {
                break;
            }

            Square(odd, even);
            if ((length & 1) != 0)
            {
                first = Times(odd, first);
            }

            length >>= 1;
        }
        while (length != 0);

        return first ^ second;
    }

    private static uint Times(uint[] matrix, uint vector)
    {
        uint sum = 0;
        for (var i = 0; vector != 0; i++, vector >>= 1)
        {
            if ((vector & 1) != 0)
            {
                sum ^= matrix[i];
            }
        }

        return sum;
    }

    private static void Square(uint[] square, uint[] matrix)
    {
        for (var n = 0; n < 32; n++)
        {
            square[n] = Times(matrix, matrix[n]);
        }
    }
}
