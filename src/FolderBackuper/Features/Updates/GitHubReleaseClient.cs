using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using FolderBackuper.Infrastructure.Versioning;

using FolderBackuper.Infrastructure.Localization;
namespace FolderBackuper.Features.Updates;

/// <summary>
/// Reads the newest published release from the GitHub HTTPS API.
/// </summary>
/// <remarks>
/// The request is anonymous and carries no identifying information, so it needs no secret and
/// nothing about it can be redacted incorrectly. Every failure is classified rather than thrown,
/// because the caller has no recovery to attempt and no person to tell.
/// <para>
/// A named client is resolved per attempt rather than an injected <see cref="HttpClient"/>, for the
/// same reason as <see cref="Notifications.ResendEmailClient"/>: this class is a singleton, and
/// capturing one client would pin a single message handler for the lifetime of a service that runs
/// for months, defeating the handler rotation that keeps DNS results fresh.
/// </para>
/// </remarks>
public sealed class GitHubReleaseClient(
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    ILogger<GitHubReleaseClient> logger)
{
    public const string ClientName = "GitHubReleases";

    /// <summary>Truncation bound for anything read out of a response and shown or logged.</summary>
    private const int MaxDetailLength = 200;

    private const string RateLimitRemainingHeader = "x-ratelimit-remaining";
    private const string RateLimitResetHeader = "x-ratelimit-reset";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async Task<LatestReleaseResult> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        var httpClient = httpClientFactory.CreateClient(ClientName);
        try
        {
            using var response = await httpClient.GetAsync(
                UpdateCheckMetadata.LatestReleasePath,
                cancellationToken);
            return await ClassifyAsync(response, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown, not an answer about the release feed.
            throw;
        }
        catch (TaskCanceledException)
        {
            return LatestReleaseResult.Unavailable(UpdateProblemMessage.Timeout);
        }
        catch (HttpRequestException exception)
        {
            return Classify(exception);
        }
        catch (IOException exception)
        {
            logger.LogDebug(exception, "The connection to the release feed was lost while reading it");
            return LatestReleaseResult.Unavailable(UpdateProblemMessage.ConnectionLost);
        }
    }

    private async Task<LatestReleaseResult> ClassifyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        // A repository with no published release is the state this product ships in, so it is an
        // ordinary answer rather than something to report.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return LatestReleaseResult.NoRelease();
        }

        if (!response.IsSuccessStatusCode)
        {
            var reset = ReadRateLimitReset(response);
            var code = (int)response.StatusCode;

            if (reset is not null)
            {
                logger.LogDebug("The release feed throttled the request until {Reset}", reset);
                return LatestReleaseResult.Unavailable(UpdateProblemMessage.RateLimited, reset);
            }

            logger.LogDebug("The release feed answered {StatusCode}", code);
            return LatestReleaseResult.Unavailable(UiMessage.For(
                UpdateProblemMessage.UnexpectedStatus, UiMessageArgument.FromNumber(code)));
        }

        GitHubRelease? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<GitHubRelease>(
                SerializerOptions,
                cancellationToken);
        }
        catch (Exception exception)
            when (exception is JsonException or NotSupportedException or HttpRequestException or IOException)
        {
            logger.LogDebug(exception, "The release feed returned a response that could not be read");
            return LatestReleaseResult.Unavailable(UpdateProblemMessage.UnreadableResponse);
        }

        if (payload is null)
        {
            return LatestReleaseResult.Unavailable(UpdateProblemMessage.EmptyResponse);
        }

        // This endpoint never returns a draft or a pre-release. Honouring the flags anyway means a
        // change at the other end cannot turn an unfinished release into an offered update.
        if (payload.Draft || payload.Prerelease)
        {
            return LatestReleaseResult.NoRelease();
        }

        // A tag carries a leading 'v' and the version type deliberately rejects it, so it is
        // stripped here, at the only boundary that reads tag names.
        var tag = payload.TagName?.Trim() ?? "";
        var candidate = tag.Length > 1 && (tag[0] == 'v' || tag[0] == 'V') ? tag[1..] : tag;

        if (!ReleaseVersion.TryParse(candidate, out var version))
        {
            logger.LogDebug("The newest release is tagged {Tag}, which is not a version", Truncate(tag));
            return LatestReleaseResult.Unavailable(UiMessage.For(
                UpdateProblemMessage.TagIsNotAVersion, UiMessageArgument.FromText(Truncate(tag))));
        }

        return LatestReleaseResult.Read(version, payload.HtmlUrl, payload.PublishedAt);
    }

    private LatestReleaseResult Classify(HttpRequestException exception)
    {
        // Every one of these is indistinguishable from "no answer" as far as the product is
        // concerned. The socket error is logged because it is the one useful clue when a machine can
        // browse the web but the service cannot, which is what a per-user proxy setting looks like.
        var socketError = (exception.InnerException as SocketException)?.SocketErrorCode;
        logger.LogDebug(exception, "The release feed could not be reached ({SocketError})", socketError);
        return LatestReleaseResult.Unavailable(UpdateProblemMessage.Unreachable);
    }

    /// <summary>
    /// Reads when a throttled request may be retried. A rate limit states exactly when it lifts, so
    /// honouring it is both faster and politer than a fixed retry delay.
    /// </summary>
    private DateTimeOffset? ReadRateLimitReset(HttpResponseMessage response)
    {
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests))
        {
            return null;
        }

        // GitHub sends Retry-After for a secondary limit and the reset headers for the primary one.
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return timeProvider.GetUtcNow() + delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            return date;
        }

        if (!response.Headers.TryGetValues(RateLimitRemainingHeader, out var remaining)
            || remaining.FirstOrDefault() != "0")
        {
            return null;
        }

        if (!response.Headers.TryGetValues(RateLimitResetHeader, out var reset)
            || !long.TryParse(
                reset.FirstOrDefault(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var epochSeconds))
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
    }

    private static string Truncate(string text)
    {
        var single = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return single.Length <= MaxDetailLength ? single : single[..MaxDetailLength] + "...";
    }

    /// <summary>
    /// Only the fields the check needs. A narrow record keeps the whole asset list and release body
    /// out of the deserialized object.
    /// </summary>
    private sealed record GitHubRelease(
        string? TagName,
        string? HtmlUrl,
        DateTimeOffset? PublishedAt,
        bool Draft,
        bool Prerelease);
}
