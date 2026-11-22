using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// ZX Spectrum a release runs on, read from the system field of its TOSEC name: <c>(16K)</c>, <c>(16K-48K)</c>,
/// <c>(48K)</c>, <c>(48K-128K)</c>, <c>(128K)</c>, <c>(+2)</c>, <c>(+2A-+3)</c>, <c>(Pentagon 128K)</c>, and the Timex
/// machines (<c>(TC2048-TS2068)</c>), which are not ZX Spectrums. A name without a mark is a 48K release on a ZX Spectrum
/// DAT: it is never excluded.
/// </summary>
public static class SpectrumMachines
{
    /// <summary>Machine named by the system field of a TOSEC name; any other value gives <see cref="SpectrumMachine.Unmarked"/>.</summary>
    public static SpectrumMachine FromSystem(string? system)
    {
        var value = system?.Trim().ToUpperInvariant() ?? string.Empty;
        if (value.StartsWith("TIMEX", StringComparison.Ordinal) || value.StartsWith("TC20", StringComparison.Ordinal) ||
            value.StartsWith("TS20", StringComparison.Ordinal))
        {
            return SpectrumMachine.Timex;
        }

        if (value.StartsWith("PENTAGON", StringComparison.Ordinal))
        {
            return SpectrumMachine.K128;
        }

        return value switch
        {
            "16K" => SpectrumMachine.K16,
            "16K-48K" => SpectrumMachine.K16K48,
            "48K" => SpectrumMachine.K48,
            "48K-128K" => SpectrumMachine.K48K128,
            "128K" or "+2" or "48K-+2" or "128K-+2" => SpectrumMachine.K128,
            "+2A" or "+3" or "+2A-+3" or "+2-+3" or "128K-+3" => SpectrumMachine.Plus3,
            _ => SpectrumMachine.Unmarked
        };
    }

    /// <summary>Machine of a parsed name.</summary>
    public static SpectrumMachine Of(ParsedRomName parsed) => FromSystem(parsed.System);

    /// <summary>
    /// Whether the mark is that of a ZX Spectrum model, whose versions of a game are one game with a chosen model: neither
    /// a name without a mark (already without a system), nor a Timex machine, a game of its own.
    /// </summary>
    public static bool IsSpectrumModel(SpectrumMachine machine) =>
        machine is not (SpectrumMachine.Unmarked or SpectrumMachine.Timex);

    /// <summary>
    /// Whether a release of <paramref name="machine"/> runs on the models of <paramref name="target"/>: a 48K does not run
    /// a 128K or a +3 release, a 128K or +2 does not run a +3 one. A name without a mark, and a Timex release, a game of
    /// its own, are never excluded.
    /// </summary>
    public static bool RunsOn(SpectrumMachine machine, SpectrumTarget target) =>
        target switch
        {
            SpectrumTarget.Zx48 => machine is not (SpectrumMachine.K128 or SpectrumMachine.Plus3),
            SpectrumTarget.Zx128 => machine is not SpectrumMachine.Plus3,
            _ => true
        };

    /// <summary>
    /// Rank of a release for <paramref name="target"/>, the lowest first: the version that makes the most of the model.
    /// On a 48K, the 48K version, then the 48K-128K one, then the 16K ones; on a 128K, the 128K version, then the
    /// 48K-128K one, then the 48K one, then the 16K ones; on a +3, the +3 version first. 0 for every release without a
    /// chosen model.
    /// </summary>
    public static int GetRank(ParsedRomName parsed, SpectrumTarget target)
    {
        var machine = Of(parsed);
        return target switch
        {
            SpectrumTarget.Zx48 => machine switch
            {
                SpectrumMachine.Unmarked or SpectrumMachine.K48 => 0,
                SpectrumMachine.K48K128 => 1,
                SpectrumMachine.K16K48 => 2,
                SpectrumMachine.K16 => 3,
                _ => 4
            },
            SpectrumTarget.Zx128 or SpectrumTarget.ZxPlus3 => machine switch
            {
                SpectrumMachine.Plus3 => target == SpectrumTarget.ZxPlus3 ? 0 : 6,
                SpectrumMachine.K128 => 1,
                SpectrumMachine.K48K128 => 2,
                SpectrumMachine.Unmarked or SpectrumMachine.K48 => 3,
                SpectrumMachine.K16K48 => 4,
                SpectrumMachine.K16 => 5,
                _ => 6
            },
            _ => 0
        };
    }

    /// <summary>Machine of a release as the 1G1R names it, translated at display.</summary>
    public static LocalizedMessage Describe(ParsedRomName parsed) =>
        LocalizedMessage.Create(Of(parsed) switch
        {
            SpectrumMachine.K16 => "spectrum.machine_16k",
            SpectrumMachine.K16K48 => "spectrum.machine_16k_48k",
            SpectrumMachine.K48K128 => "spectrum.machine_48k_128k",
            SpectrumMachine.K128 => "spectrum.machine_128k",
            SpectrumMachine.Plus3 => "spectrum.machine_plus3",
            SpectrumMachine.Timex => "spectrum.machine_timex",
            _ => "spectrum.machine_48k"
        });
}
