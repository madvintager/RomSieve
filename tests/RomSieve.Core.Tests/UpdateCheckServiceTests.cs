using System.Net;
using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using Xunit;

namespace RomSieve.Core.Tests;

public sealed class UpdateCheckServiceTests
{
    private sealed class MockHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(response);
        }
    }

    private sealed class DelayedHttpMessageHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WhenNewerReleaseExists_ShouldReturnAvailable()
    {
        var json = """
            {
              "tag_name": "v1.2.0",
              "name": "RomSieve 1.2.0",
              "html_url": "https://github.com/madvintager/romsieve/releases/tag/v1.2.0",
              "body": "## Release notes",
              "prerelease": false
            }
            """;
        var mockResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        var handler = new MockHttpMessageHandler(mockResponse);
        using var client = new HttpClient(handler);
        var service = new UpdateCheckService(client);

        var result = await service.CheckForUpdatesAsync("1.0.0");

        result.IsUpdateAvailable.Should().BeTrue();
        result.LatestRelease.Should().NotBeNull();
        result.LatestRelease!.TagName.Should().Be("v1.2.0");
        result.LatestRelease.Version.Should().Be("1.2.0");
        result.LatestRelease.HtmlUrl.Should().Be("https://github.com/madvintager/romsieve/releases/tag/v1.2.0");
        result.Error.Should().BeNull();

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Headers.UserAgent.ToString().Should().StartWith("RomSieve/");
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WhenSameOrOlderRelease_ShouldReturnUpToDate()
    {
        var json = """
            {
              "tag_name": "v1.0.0",
              "html_url": "https://github.com/madvintager/romsieve/releases/tag/v1.0.0"
            }
            """;
        var mockResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        using var client = new HttpClient(new MockHttpMessageHandler(mockResponse));
        var service = new UpdateCheckService(client);

        var result = await service.CheckForUpdatesAsync("1.0.0");

        result.IsUpdateAvailable.Should().BeFalse();
        result.LatestRelease.Should().NotBeNull();
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WhenHttpError_ShouldReturnFailed()
    {
        var mockResponse = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            ReasonPhrase = "rate limit exceeded"
        };
        using var client = new HttpClient(new MockHttpMessageHandler(mockResponse));
        var service = new UpdateCheckService(client);

        var result = await service.CheckForUpdatesAsync("1.0.0");

        result.IsUpdateAvailable.Should().BeFalse();
        result.Error.Should().Be(LocalizedMessage.Create("update.error_http", ("status", 403)));
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WhenNoReleaseIsFound_ShouldReturnNotFound()
    {
        var mockResponse = new HttpResponseMessage(HttpStatusCode.NotFound);
        using var client = new HttpClient(new MockHttpMessageHandler(mockResponse));
        var service = new UpdateCheckService(client);

        var result = await service.CheckForUpdatesAsync("1.0.0");

        result.IsUpdateAvailable.Should().BeFalse();
        result.Error.Should().Be(LocalizedMessage.Create("update.error_not_found"));
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WhenResponseIsNotARelease_ShouldReturnFailed()
    {
        var mockResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        };
        using var client = new HttpClient(new MockHttpMessageHandler(mockResponse));

        var result = await new UpdateCheckService(client).CheckForUpdatesAsync("1.0.0");

        result.Error.Should().Be(LocalizedMessage.Create("update.error_invalid_response"));
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WhenTimedOut_ShouldReturnFailed()
    {
        using var client = new HttpClient(new DelayedHttpMessageHandler(TimeSpan.FromSeconds(10)));
        var service = new UpdateCheckService(client, timeout: TimeSpan.FromMilliseconds(50));

        var result = await service.CheckForUpdatesAsync("1.0.0");

        result.IsUpdateAvailable.Should().BeFalse();
        result.Error.Should().Be(LocalizedMessage.Create("update.error_timeout"));
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WhenCancelledByCaller_ShouldThrow()
    {
        using var client = new HttpClient(new DelayedHttpMessageHandler(TimeSpan.FromSeconds(10)));
        var service = new UpdateCheckService(client);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = () => service.CheckForUpdatesAsync("1.0.0", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

