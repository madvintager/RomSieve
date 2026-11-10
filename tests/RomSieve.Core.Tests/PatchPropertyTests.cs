using CsCheck;
using RomSieve.Core.Models;
using static RomSieve.Core.Tests.Fixtures.PatchTestEncoder;

namespace RomSieve.Core.Tests;

/// <summary>
/// Property-based tests of the patch decoders with CsCheck: a patch encoded by the test encoder from a random source to
/// a random result gives that result back; a patch cut short, or altered and signed again, is refused with a
/// <see cref="PatchException"/> and nothing else. A failure prints the seed that replays it (<c>CsCheck_Seed</c>); a
/// counterexample becomes a unit test of <see cref="IpsPatchTests"/>, <see cref="UpsPatchTests"/> or
/// <see cref="BpsPatchTests"/>.
/// </summary>
public sealed class PatchPropertyTests
{
    private const int Iterations = 300;

    /// <summary>Above this size, an altered patch is not applied: its result would only be zeros (UPS, IPS).</summary>
    private const long MaxFuzzedTarget = 1 << 20;

    /// <summary>A random source, and a result that keeps most of its bytes, of another length.</summary>
    private static readonly Gen<(byte[] Source, byte[] Target)> SourceAndTarget =
        Gen.Byte.Array[0, 600].Select(Gen.Byte.Array[0, 600], (source, noise) =>
        {
            var target = new byte[noise.Length];
            for (var i = 0; i < target.Length; i++)
            {
                target[i] = i < source.Length && (noise[i] & 3) != 0 ? source[i] : noise[i];
            }

            return (source, target);
        });

    private static byte[] Run(byte[] patch, byte[] source) =>
        PatchRunner.ApplyAsync(patch, source).GetAwaiter().GetResult().Output;

    [Fact]
    public void Ips_ShouldGiveBackTheTarget()
    {
        SourceAndTarget.Sample((source, target) => Run(IpsDiff(source, target), source).SequenceEqual(target), iter: Iterations);
    }

    [Fact]
    public void Ips32_ShouldGiveBackTheTarget()
    {
        SourceAndTarget
            .Where(p => p.Target.Length >= p.Source.Length)
            .Sample((source, target) => Run(IpsDiff(source, target, ips32: true), source).SequenceEqual(target), iter: Iterations);
    }

    [Fact]
    public void Ups_ShouldGiveBackTheTarget()
    {
        SourceAndTarget.Sample((source, target) => Run(Ups(source, target), source).SequenceEqual(target), iter: Iterations);
    }

    [Fact]
    public void BpsLinear_ShouldGiveBackTheTarget()
    {
        SourceAndTarget.Sample((source, target) => Run(BpsLinear(source, target), source).SequenceEqual(target), iter: Iterations);
    }

    [Fact]
    public void BpsCopies_ShouldGiveBackTheTarget()
    {
        Gen.Byte.Array[0, 300].Select(Gen.Int[0, 2000], Gen.UInt.Array[1, 200])
            .Sample((source, targetSize, choices) =>
            {
                var (actions, target) = RandomBpsActions(source, targetSize, choices);
                var patch = Bps((ulong)source.Length, (ulong)target.Length, actions, Crc(source), Crc(target));
                return Run(patch, source).SequenceEqual(target);
            }, iter: Iterations);
    }

    [Fact]
    public void TheResult_ShouldGiveItsSizeAndChecksums()
    {
        SourceAndTarget.Sample((source, target) =>
        {
            foreach (var patch in new[] { IpsDiff(source, target), Ups(source, target), BpsLinear(source, target) })
            {
                var (_, result) = PatchRunner.ApplyAsync(patch, source).GetAwaiter().GetResult();
                if (result.Size != target.Length || result.Crc32 != Crc(target) || result.SourceCrc32 != Crc(source))
                {
                    return false;
                }
            }

            return true;
        }, iter: Iterations);
    }

    [Fact]
    public void APatchCutShort_ShouldBeRefusedCleanly()
    {
        SourceAndTarget.Sample((source, target) =>
        {
            foreach (var (patch, alwaysRefused) in new[]
                     {
                         (IpsDiff(source, target), false),
                         (Ups(source, target), true),
                         (BpsLinear(source, target), true)
                     })
            {
                for (var length = 0; length < patch.Length; length++)
                {
                    // An IPS cut before its truncation is still a valid IPS: it may apply, never fail otherwise
                    var refusal = PatchRunner.RefusalAsync(patch[..length], source).GetAwaiter().GetResult();
                    if (alwaysRefused && refusal is null)
                    {
                        return false;
                    }
                }
            }

            return true;
        }, iter: 40);
    }

    [Fact]
    public void AnAlteredPatchSignedAgain_ShouldApplyOrBeRefusedCleanly()
    {
        SourceAndTarget.Select(Gen.Int[0, int.MaxValue], Gen.Byte, Gen.Int[0, 2])
            .Sample((pair, where, value, format) =>
            {
                var (source, target) = pair;
                var patch = format switch
                {
                    0 => IpsDiff(source, target),
                    1 => Ups(source, target),
                    _ => BpsLinear(source, target)
                };

                // One byte changed, in the signature, the header or the body; the patch CRC computed again
                var altered = (byte[])patch.Clone();
                var bodyLength = format == 0 ? altered.Length : altered.Length - 4;
                altered[where % bodyLength] = value;
                if (format != 0)
                {
                    altered = Resign(altered);
                }

                PatchHeader header;
                try
                {
                    header = PatchRunner.Service.ReadHeader(altered);
                }
                catch (PatchException)
                {
                    return true;
                }

                if (header.TargetSizeFor(source.Length) > MaxFuzzedTarget)
                {
                    return true;
                }

                // Applied or refused with a PatchException: any other exception fails the property
                _ = PatchRunner.RefusalAsync(altered, source).GetAwaiter().GetResult();
                return true;
            }, iter: 2000);
    }

    [Fact]
    public void RandomBytesAfterASignature_ShouldBeRefusedCleanly()
    {
        Gen.Int[0, 3].Select(Gen.Byte.Array[0, 200], Gen.Byte.Array[0, 100])
            .Sample((format, body, source) =>
            {
                var signature = Ascii(format switch { 0 => "PATCH", 1 => "IPS32", 2 => "UPS1", _ => "BPS1" });
                var patch = format >= 2 ? Resign([.. signature, .. body, 0, 0, 0, 0]) : [.. signature, .. body];
                try
                {
                    if (PatchRunner.Service.ReadHeader(patch).TargetSizeFor(source.Length) > MaxFuzzedTarget)
                    {
                        return true;
                    }
                }
                catch (PatchException)
                {
                    return true;
                }

                _ = PatchRunner.RefusalAsync(patch, source).GetAwaiter().GetResult();
                return true;
            }, iter: 2000);
    }
}
