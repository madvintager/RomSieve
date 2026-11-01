using RomSieve.Core.Models;

namespace RomSieve.Core.Abstractions;

/// <summary>
/// Information about a published release retrieved from GitHub.
/// </summary>
public sealed record UpdateInfo(
    string TagName,
    string Version,
    string HtmlUrl,
    string? Name = null,
    string? Body = null,
    DateTimeOffset? PublishedAt = null,
    bool IsPrerelease = false);

/// <summary>
/// Result of an update check attempt; <see cref="Error"/> is translated only when displayed.
/// </summary>
public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    string CurrentVersion,
    UpdateInfo? LatestRelease = null,
    LocalizedMessage? Error = null)
{
    public static UpdateCheckResult UpToDate(string currentVersion, UpdateInfo? latest = null) =>
        new(false, currentVersion, latest);

    public static UpdateCheckResult Available(string currentVersion, UpdateInfo latest) =>
        new(true, currentVersion, latest);

    public static UpdateCheckResult Failed(string currentVersion, LocalizedMessage error) =>
        new(false, currentVersion, null, error);
}

/// <summary>
/// Service checking for newer releases via the GitHub Releases API.
/// </summary>
public interface IUpdateCheckService
{
    /// <summary>
    /// Compares the latest published release with <paramref name="currentVersion"/> (the running version by default).
    /// Never throws, except <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    Task<UpdateCheckResult> CheckForUpdatesAsync(
        string? currentVersion = null,
        CancellationToken cancellationToken = default);
}

