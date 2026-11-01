using System.Text.RegularExpressions;

namespace RomSieve.Core.Models;

/// <summary>
/// Lightweight semantic version parser and comparer (SemVer 2.0 subset)
/// supporting optional leading 'v'/'V', build metadata and prerelease tags.
/// </summary>
public readonly partial struct SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? Prerelease { get; }
    public string? Build { get; }

    public bool IsDev => Major == 0 && Minor == 0 && Patch == 0;

    public SemanticVersion(int major, int minor = 0, int patch = 0, string? prerelease = null, string? build = null)
    {
        Major = Math.Max(0, major);
        Minor = Math.Max(0, minor);
        Patch = Math.Max(0, patch);
        Prerelease = string.IsNullOrWhiteSpace(prerelease) ? null : prerelease.Trim();
        Build = string.IsNullOrWhiteSpace(build) ? null : build.Trim();
    }

    /// <summary>
    /// Parses a version string (e.g. "v1.2.3", "1.0.0-beta1", "0.0.0-dev+abc123").
    /// </summary>
    public static bool TryParse(string? input, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var s = input.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..].Trim();
        }

        string? build = null;
        var plusIdx = s.IndexOf('+', StringComparison.Ordinal);
        if (plusIdx >= 0)
        {
            build = s[(plusIdx + 1)..];
            s = s[..plusIdx];
        }

        string? prerelease = null;
        var dashIdx = s.IndexOf('-', StringComparison.Ordinal);
        if (dashIdx >= 0)
        {
            prerelease = s[(dashIdx + 1)..];
            s = s[..dashIdx];
        }

        if (s.StartsWith('.') || s.EndsWith('.'))
        {
            return false;
        }

        var parts = s.Split('.');
        if (parts.Length == 0 || parts.Length > 4)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var major) || major < 0)
        {
            return false;
        }

        var minor = 0;
        if (parts.Length > 1 && (!int.TryParse(parts[1], out minor) || minor < 0))
        {
            return false;
        }

        var patch = 0;
        if (parts.Length > 2 && (!int.TryParse(parts[2], out patch) || patch < 0))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, prerelease, build);
        return true;
    }

    public static SemanticVersion Parse(string input)
    {
        if (TryParse(input, out var v))
        {
            return v;
        }

        throw new FormatException($"Invalid semantic version format: '{input}'");
    }

    /// <summary>
    /// Strips leading 'v'/'V' and returns standard string representation (e.g. "1.2.3" or "1.2.3-beta1").
    /// </summary>
    public static string Clean(string? versionOrTag)
    {
        if (TryParse(versionOrTag, out var v))
        {
            return v.ToString();
        }

        var s = versionOrTag?.Trim() ?? string.Empty;
        return (s.StartsWith('v') || s.StartsWith('V')) ? s[1..] : s;
    }

    /// <summary>
    /// Returns true if <paramref name="candidate"/> is strictly newer than <paramref name="baseline"/>.
    /// </summary>
    public static bool IsNewer(string? candidate, string? baseline)
    {
        if (!TryParse(candidate, out var candVer))
        {
            return false;
        }

        if (!TryParse(baseline, out var baseVer))
        {
            return true;
        }

        return candVer.CompareTo(baseVer) > 0;
    }

    public int CompareTo(SemanticVersion other)
    {
        var cmp = Major.CompareTo(other.Major);
        if (cmp != 0) return cmp;

        cmp = Minor.CompareTo(other.Minor);
        if (cmp != 0) return cmp;

        cmp = Patch.CompareTo(other.Patch);
        if (cmp != 0) return cmp;

        // SemVer 2.0 precedence rule: Normal version has higher precedence than a prerelease version
        if (Prerelease is null && other.Prerelease is not null) return 1;
        if (Prerelease is not null && other.Prerelease is null) return -1;
        if (Prerelease is null && other.Prerelease is null) return 0;

        return ComparePrerelease(Prerelease!, other.Prerelease!);
    }

    private static int ComparePrerelease(string a, string b)
    {
        var partsA = a.Split('.');
        var partsB = b.Split('.');
        var minLen = Math.Min(partsA.Length, partsB.Length);

        for (var i = 0; i < minLen; i++)
        {
            var pA = partsA[i];
            var pB = partsB[i];

            var isNumA = int.TryParse(pA, out var numA);
            var isNumB = int.TryParse(pB, out var numB);

            if (isNumA && isNumB)
            {
                var cmp = numA.CompareTo(numB);
                if (cmp != 0) return cmp;
            }
            else if (isNumA)
            {
                return -1; // numeric identifiers have lower precedence than non-numeric
            }
            else if (isNumB)
            {
                return 1;
            }
            else
            {
                var cmp = string.Compare(pA, pB, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
            }
        }

        return partsA.Length.CompareTo(partsB.Length);
    }

    public bool Equals(SemanticVersion other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease?.ToLowerInvariant());

    public override string ToString()
    {
        var result = $"{Major}.{Minor}.{Patch}";
        if (!string.IsNullOrEmpty(Prerelease))
        {
            result += $"-{Prerelease}";
        }

        return result;
    }

    public static bool operator ==(SemanticVersion left, SemanticVersion right) => left.Equals(right);
    public static bool operator !=(SemanticVersion left, SemanticVersion right) => !left.Equals(right);
    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;
}
