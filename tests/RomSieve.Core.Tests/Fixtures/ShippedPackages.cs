using System.Text.Json;
using System.Text.RegularExpressions;

namespace RomSieve.Core.Tests.Fixtures;

/// <summary>
/// Packages that the published executables of RomSieve embed, read from a dependency file (deps.json) of a build, and
/// the check that THIRD-PARTY-NOTICES.txt names each of them. Shared with RomSieve.App.Tests and
/// RomSieve.Cli.Tests (linked file), which read the deps.json of the executable they reference.
/// </summary>
internal static class ShippedPackages
{
    public const string NoticesFileName = "THIRD-PARTY-NOTICES.txt";

    /// <summary>
    /// Runtime identifiers of the published executables (win-x64, linux-x64) and of the families they take native
    /// assets from: a package of another platform (the natives of macOS) is referenced, but never published.
    /// </summary>
    private static readonly HashSet<string> PublishedRuntimeIdentifiers =
        new(["any", "win", "win-x64", "unix", "linux", "linux-x64"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ids of the packages that have an asset in an executable published for Windows or Linux: all the packages of the
    /// file, or only those that <paramref name="rootLibrary"/> (a project of the file) depends on.
    /// </summary>
    public static IReadOnlyList<string> Read(string depsJsonPath, string? rootLibrary = null)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(depsJsonPath));
        var root = document.RootElement;
        var target = root.GetProperty("targets").GetProperty(root.GetProperty("runtimeTarget").GetProperty("name").GetString()!);
        var libraries = root.GetProperty("libraries");
        var names = rootLibrary is null
            ? libraries.EnumerateObject().Select(library => library.Name).ToList()
            : DependenciesOf(target, target.EnumerateObject().Single(library => library.Name.StartsWith(rootLibrary + "/", StringComparison.Ordinal)).Name);

        return names
            .Where(name => libraries.GetProperty(name).GetProperty("type").GetString() == "package")
            .Where(name => IsPublished(target.GetProperty(name)))
            .Select(name => name[..name.IndexOf('/')])
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Packages that the notices never name as a whole (a package is not named by the name of another one).</summary>
    public static IReadOnlyList<string> MissingFrom(string notices, IEnumerable<string> packages) =>
        packages
            .Where(id => !Regex.IsMatch(notices, $@"(?<![\w.-]){Regex.Escape(id)}(?!\.?[\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToList();

    /// <summary>Libraries that a library depends on, directly or not ("Name/Version" keys of the targets).</summary>
    private static List<string> DependenciesOf(JsonElement target, string library)
    {
        var found = new List<string>();
        var pending = new Queue<string>([library]);
        while (pending.TryDequeue(out var current))
        {
            if (!target.TryGetProperty(current, out var entry) || !entry.TryGetProperty("dependencies", out var dependencies))
            {
                continue;
            }

            foreach (var dependency in dependencies.EnumerateObject())
            {
                var key = $"{dependency.Name}/{dependency.Value.GetString()}";
                if (!found.Contains(key))
                {
                    found.Add(key);
                    pending.Enqueue(key);
                }
            }
        }

        return found;
    }

    private static bool IsPublished(JsonElement assets) =>
        assets.TryGetProperty("runtime", out _)
        || assets.TryGetProperty("native", out _)
        || (assets.TryGetProperty("runtimeTargets", out var runtimeTargets)
            && runtimeTargets.EnumerateObject().Any(asset =>
                asset.Value.TryGetProperty("rid", out var rid) && PublishedRuntimeIdentifiers.Contains(rid.GetString() ?? string.Empty)));
}
