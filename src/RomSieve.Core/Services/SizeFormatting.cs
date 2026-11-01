using System.Globalization;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>Sizes shown to the user: execution panel (statistics banner, summary of the next sort), quarantine of a destination, command line.</summary>
public static class SizeFormatting
{
    /// <summary>
    /// Size in binary units (1 KB = 1 024 bytes), written in octets (o, Ko, Mo, Go) in French and in bytes
    /// (B, KB, MB, GB) in the other languages.
    /// </summary>
    public static string Format(ILocalizationService localization, long sizeBytes) =>
        localization.Format(ToMessage(sizeBytes));

    /// <summary>
    /// The size of <see cref="Format"/> as a message, for a log line or a reason translated at display (and again when
    /// the language changes).
    /// </summary>
    public static LocalizedMessage ToMessage(long sizeBytes)
    {
        if (sizeBytes <= 0)
        {
            return LocalizedMessage.Create("stats.size_mb", ("size", "0.00"));
        }

        if (sizeBytes < 1024)
        {
            return LocalizedMessage.Create("stats.size_b", ("size", sizeBytes));
        }

        if (sizeBytes < 1024L * 1024L)
        {
            var kbStr = (sizeBytes / 1024.0).ToString("F2", CultureInfo.InvariantCulture);
            return LocalizedMessage.Create("stats.size_kb", ("size", kbStr));
        }

        var mb = sizeBytes / (1024.0 * 1024.0);
        if (mb >= 1024.0)
        {
            var gbStr = (mb / 1024.0).ToString("F2", CultureInfo.InvariantCulture);
            return LocalizedMessage.Create("stats.size_gb", ("size", gbStr));
        }

        var mbStr = mb.ToString("F2", CultureInfo.InvariantCulture);
        return LocalizedMessage.Create("stats.size_mb", ("size", mbStr));
    }
}
