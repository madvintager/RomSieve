using System.Reflection;

namespace RomSieve.Core.Services;

/// <summary>
/// Version of RomSieve: <c>0.0.0-dev</c> for a local build, the version of the tag for a published release
/// (<c>dotnet publish -p:Version=...</c>, see <c>.github/workflows/release.yml</c>).
/// </summary>
public static class ProductVersion
{
    /// <summary>Version without the commit hash that the SDK appends to the informational version.</summary>
    public static string Current { get; } = Read();

    private static string Read()
    {
        var informational = typeof(ProductVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
        {
            return typeof(ProductVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? informational[..plus] : informational;
    }
}
