using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Checks for new versions of RomSieve published on GitHub Releases via the REST API.
/// </summary>
public sealed class UpdateCheckService : IUpdateCheckService
{
    public const string DefaultRepositoryOwner = "madvintager";
    public const string DefaultRepositoryName = "romsieve";

    /// <summary>Delay after which a check that has not received the release is abandoned.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _httpClient;
    private readonly string _repositoryOwner;
    private readonly string _repositoryName;
    private readonly TimeSpan _timeout;

    public UpdateCheckService(
        HttpClient httpClient,
        string repositoryOwner = DefaultRepositoryOwner,
        string repositoryName = DefaultRepositoryName,
        TimeSpan? timeout = null)
    {
        _httpClient = httpClient;
        _repositoryOwner = repositoryOwner;
        _repositoryName = repositoryName;
        _timeout = timeout ?? DefaultTimeout;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        string? currentVersion = null,
        CancellationToken cancellationToken = default)
    {
        var current = string.IsNullOrWhiteSpace(currentVersion) ? ProductVersion.Current : currentVersion.Trim();

        try
        {
            using var timeoutCts = new CancellationTokenSource(_timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var url = $"https://api.github.com/repos/{_repositoryOwner}/{_repositoryName}/releases/latest";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RomSieve", ProductVersion.Current));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // GitHub answers 404 when the repository has no published release (drafts and
                // pre-releases are ignored) or when it cannot be seen anonymously (private repository).
                return UpdateCheckResult.Failed(current, LocalizedMessage.Create("update.error_not_found"));
            }

            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Failed(current, LocalizedMessage.Create("update.error_http", ("status", (int)response.StatusCode)));
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(linkedCts.Token);
            var dto = await JsonSerializer.DeserializeAsync<GitHubReleaseDto>(contentStream, cancellationToken: linkedCts.Token);

            if (dto is null || string.IsNullOrWhiteSpace(dto.TagName))
            {
                return UpdateCheckResult.Failed(current, LocalizedMessage.Create("update.error_invalid_response"));
            }

            var release = new UpdateInfo(
                TagName: dto.TagName.Trim(),
                Version: SemanticVersion.Clean(dto.TagName),
                HtmlUrl: dto.HtmlUrl ?? $"https://github.com/{_repositoryOwner}/{_repositoryName}/releases/tag/{dto.TagName}",
                Name: dto.Name,
                Body: dto.Body,
                PublishedAt: dto.PublishedAt,
                IsPrerelease: dto.Prerelease);

            var isNewer = SemanticVersion.IsNewer(release.Version, current);
            return isNewer
                ? UpdateCheckResult.Available(current, release)
                : UpdateCheckResult.UpToDate(current, release);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckResult.Failed(current, LocalizedMessage.Create("update.error_timeout"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UpdateCheckResult.Failed(current, LocalizedMessage.Verbatim(ex.Message));
        }
    }

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }
    }
}

