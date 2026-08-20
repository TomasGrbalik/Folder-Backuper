using System.Net;
using System.Globalization;
using System.Net.Sockets;
using FolderBackuper.Features.Updates;
using FolderBackuper.Infrastructure.Versioning;

namespace FolderBackuper.Tests;

/// <summary>
/// The release feed is outside this product's control, so every answer it can give is classified
/// here. No test reaches the network.
/// </summary>
public sealed class GitHubReleaseClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("v1.2.0", 1, 2, 0)]
    [InlineData("V1.2.0", 1, 2, 0)]
    // A tag without the customary prefix must work too, so a hand-made tag does not break the check.
    [InlineData("1.2.0", 1, 2, 0)]
    [InlineData("  v1.2.0  ", 1, 2, 0)]
    public async Task GetLatestRelease_ReadsThePublishedVersion(string tag, int major, int minor, int patch)
    {
        var body = $$"""
            {"tag_name":"{{tag}}","html_url":"https://example.test/r/1","published_at":"2026-08-19T08:00:00Z","draft":false,"prerelease":false}
            """;
        var result = await Client(FakeHttpMessageHandler.Returning(HttpStatusCode.OK, body)).GetLatestReleaseAsync();

        Assert.Equal(LatestReleaseStatus.Read, result.Status);
        Assert.NotNull(result.Version);
        Assert.Equal(major, result.Version!.Value.Major);
        Assert.Equal(minor, result.Version!.Value.Minor);
        Assert.Equal(patch, result.Version!.Value.Patch);
        Assert.Equal("https://example.test/r/1", result.ReleaseUrl);
        Assert.Equal(new DateTimeOffset(2026, 8, 19, 8, 0, 0, TimeSpan.Zero), result.PublishedAt);
    }

    [Fact]
    public async Task GetLatestRelease_TreatsAMissingReleaseAsAnAnswer()
    {
        // This is the state the repository is in until the first release is published, so it must not
        // look like a failure.
        var result = await Client(FakeHttpMessageHandler.Returning(HttpStatusCode.NotFound, """{"message":"Not Found"}"""))
            .GetLatestReleaseAsync();

        Assert.Equal(LatestReleaseStatus.NoRelease, result.Status);
        Assert.Null(result.Version);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task GetLatestRelease_IgnoresAnUnfinishedRelease(bool draft, bool prerelease)
    {
        var body = $$"""
            {"tag_name":"v9.9.9","html_url":"https://example.test/r/9","draft":{{draft.ToString().ToLowerInvariant()}},"prerelease":{{prerelease.ToString().ToLowerInvariant()}}}
            """;
        var result = await Client(FakeHttpMessageHandler.Returning(HttpStatusCode.OK, body)).GetLatestReleaseAsync();

        Assert.Equal(LatestReleaseStatus.NoRelease, result.Status);
        Assert.Null(result.Version);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task GetLatestRelease_TreatsAnUnhelpfulAnswerAsInconclusive(HttpStatusCode status)
    {
        var result = await Client(FakeHttpMessageHandler.Returning(status)).GetLatestReleaseAsync();

        Assert.Equal(LatestReleaseStatus.Unavailable, result.Status);
        Assert.Null(result.RateLimitResetUtc);
    }

    [Fact]
    public async Task GetLatestRelease_ReportsWhenAPrimaryRateLimitLifts()
    {
        var reset = Now.AddMinutes(20);
        var handler = FakeHttpMessageHandler.Responding(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"message":"API rate limit exceeded"}""")
            };
            response.Headers.Add("x-ratelimit-remaining", "0");
            response.Headers.Add("x-ratelimit-reset", reset.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            return response;
        });

        var result = await Client(handler).GetLatestReleaseAsync();

        Assert.Equal(LatestReleaseStatus.Unavailable, result.Status);
        Assert.Equal(reset, result.RateLimitResetUtc);
    }

    [Fact]
    public async Task GetLatestRelease_ReportsWhenASecondaryRateLimitLifts()
    {
        var handler = FakeHttpMessageHandler.Responding(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.Add("retry-after", "60");
            return response;
        });

        var result = await Client(handler).GetLatestReleaseAsync();

        Assert.Equal(LatestReleaseStatus.Unavailable, result.Status);
        Assert.Equal(Now.AddSeconds(60), result.RateLimitResetUtc);
    }

    [Fact]
    public async Task GetLatestRelease_DoesNotClaimARateLimitFromAPlainForbidden()
    {
        // A 403 without the headers is not a rate limit, and pretending otherwise would set a retry
        // time out of nothing.
        var result = await Client(FakeHttpMessageHandler.Returning(HttpStatusCode.Forbidden)).GetLatestReleaseAsync();

        Assert.Equal(LatestReleaseStatus.Unavailable, result.Status);
        Assert.Null(result.RateLimitResetUtc);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    public async Task GetLatestRelease_TreatsAnUnreadableBodyAsInconclusive(string body)
    {
        var result = await Client(FakeHttpMessageHandler.Returning(HttpStatusCode.OK, body)).GetLatestReleaseAsync();

        Assert.Equal(LatestReleaseStatus.Unavailable, result.Status);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("release-1.2.3")]
    [InlineData("v1.2.3.4")]
    [InlineData("")]
    public async Task GetLatestRelease_RefusesATagThatIsNotAVersion(string tag)
    {
        var body = $$"""{"tag_name":"{{tag}}","draft":false,"prerelease":false}""";
        var result = await Client(FakeHttpMessageHandler.Returning(HttpStatusCode.OK, body)).GetLatestReleaseAsync();

        Assert.Equal(LatestReleaseStatus.Unavailable, result.Status);
        Assert.Null(result.Version);
    }

    [Fact]
    public async Task GetLatestRelease_TreatsATimeoutAsInconclusive()
    {
        var result = await Client(FakeHttpMessageHandler.Throwing(new TaskCanceledException())).GetLatestReleaseAsync();

        Assert.Equal(LatestReleaseStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task GetLatestRelease_TreatsAnUnreachableFeedAsInconclusive()
    {
        // What an offline machine, or a proxy the service cannot see, actually produces.
        var handler = FakeHttpMessageHandler.Throwing(
            new HttpRequestException("no route", new SocketException((int)SocketError.HostNotFound)));

        var result = await Client(handler).GetLatestReleaseAsync();

        Assert.Equal(LatestReleaseStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task GetLatestRelease_PropagatesShutdown()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var handler = FakeHttpMessageHandler.Throwing(new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client(handler).GetLatestReleaseAsync(cancellation.Token));
    }

    [Fact]
    public async Task GetLatestRelease_SendsNothingIdentifying()
    {
        var handler = FakeHttpMessageHandler.Returning(
            HttpStatusCode.OK,
            """{"tag_name":"v1.0.0","draft":false,"prerelease":false}""");

        await Client(handler).GetLatestReleaseAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(UpdateCheckMetadata.LatestReleasePath, request.RequestUri?.AbsolutePath.TrimStart('/'));

        // No credential is involved, so none may be sent, and the body is empty because it is a GET.
        Assert.Null(request.Headers.Authorization);
        Assert.Empty(Assert.Single(handler.RequestBodies));

        // The version is deliberately absent from the user agent: the request must say nothing about
        // the machine that made it. The header itself is set on the named client, which this test
        // does not build, so only its absence from the request is asserted here.
        Assert.DoesNotContain(
            ProductVersion.Display,
            request.Headers.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static GitHubReleaseClient Client(FakeHttpMessageHandler handler) =>
        UpdateTestFactory.Client(handler, new TestTimeProvider(Now));
}
