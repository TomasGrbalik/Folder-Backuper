using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

using FolderBackuper.Infrastructure.Localization;
namespace FolderBackuper.Features.Notifications;

/// <summary>One message as the Resend API accepts it.</summary>
public sealed record ResendMessage(
    string From,
    IReadOnlyList<string> To,
    string Subject,
    string Html,
    string Text);

/// <summary>
/// Typed client for the Resend HTTPS API.
/// </summary>
/// <remarks>
/// Classification matters more than transport here, because the application makes at most one
/// attempt per notification and never retries. A result is only <see cref="NotificationSendStatus.Rejected"/>
/// when nothing can have been sent; anything that leaves the outcome genuinely unknowable becomes
/// <see cref="NotificationSendStatus.Uncertain"/> and is recorded as delivery-unknown.
/// <para>
/// The API key is passed per call rather than captured at construction, so the unprotected secret
/// lives only for the duration of one attempt. It never appears in a result message or an exception.
/// </para>
/// </remarks>
public sealed partial class ResendEmailClient(IHttpClientFactory httpClientFactory, ILogger<ResendEmailClient> logger)
{
    private const string RedactedMarker = "[redacted]";

    /// <summary>
    /// Name of the configured client. A named client resolved per attempt is used rather than an
    /// injected <see cref="HttpClient"/>, because this class is a singleton: capturing one client
    /// would pin a single message handler for the lifetime of the service and defeat the handler
    /// rotation that keeps DNS results fresh in a process that runs for months.
    /// </summary>
    public const string ClientName = "Resend";

    public const string SendPath = "emails";

    private const int MaxSafeErrorLength = 400;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<NotificationSendResult> SendAsync(
        ResendMessage message,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        using var request = new HttpRequestMessage(HttpMethod.Post, SendPath)
        {
            Content = JsonContent.Create(
                new ResendRequest(
                    message.From,
                    message.To,
                    message.Subject,
                    message.Html,
                    message.Text),
                options: SerializerOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var httpClient = httpClientFactory.CreateClient(ClientName);
        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            return await ClassifyAsync(response, apiKey, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown, not a delivery result. The caller leaves the attempt uncertain rather than
            // claiming a failure it cannot substantiate.
            throw;
        }
        catch (TaskCanceledException)
        {
            // The client timed out. Resend may already have accepted the message.
            logger.LogWarning("The Resend request timed out before a result was known");
            return new NotificationSendResult(NotificationSendStatus.Uncertain,
                NotificationResultMessage.ProviderTimedOut);
        }
        catch (HttpRequestException exception)
        {
            return Classify(exception);
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "The Resend request failed while in flight");
            return new NotificationSendResult(NotificationSendStatus.Uncertain,
                NotificationResultMessage.ConnectionLost);
        }
    }

    private async Task<NotificationSendResult> ClassifyAsync(
        HttpResponseMessage response,
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            var accepted = await ReadIdAsync(response, cancellationToken);
            logger.LogInformation("Resend accepted the notification {ProviderMessageId}", accepted ?? "(no id)");
            return new NotificationSendResult(
                NotificationSendStatus.Delivered,
                accepted is null
                    ? UiMessage.For(NotificationResultMessage.Accepted)
                    : UiMessage.For(NotificationResultMessage.AcceptedWithId, UiMessageArgument.FromText(accepted)));
        }

        var detail = await ReadErrorAsync(response, apiKey, cancellationToken);
        var code = (int)response.StatusCode;

        // A 5xx leaves the provider's own state unknown; a 4xx is an explicit refusal, including 429,
        // which means the request was throttled and therefore not accepted.
        if (code >= 500)
        {
            logger.LogWarning("Resend reported a server error {StatusCode}: {Detail}", code, detail);
            return new NotificationSendResult(
                NotificationSendStatus.Uncertain,
                WithDetail(
                    UiMessage.For(NotificationResultMessage.ProviderServerError, UiMessageArgument.FromNumber(code)),
                    detail));
        }

        logger.LogWarning("Resend rejected the notification {StatusCode}: {Detail}", code, detail);
        var reason = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                NotificationResultMessage.ApiKeyRejected,
            HttpStatusCode.TooManyRequests =>
                NotificationResultMessage.Throttled,
            HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest =>
                NotificationResultMessage.SenderDomainUnverified,
            _ => NotificationResultMessage.RequestRejected
        };

        return new NotificationSendResult(
            NotificationSendStatus.Rejected,
            WithDetail(UiMessage.For(reason, UiMessageArgument.FromNumber(code)), detail));
    }

    /// <summary>
    /// Appends the provider's own detail to a reason, when there is any.
    /// </summary>
    /// <remarks>
    /// Composed as a wrapping message rather than by putting the detail's placeholder in every reason,
    /// so that a reason with no detail does not render a dangling separator, and so that translators see
    /// one sentence about how a detail is attached instead of repeating it in each reason.
    /// </remarks>
    private static UiMessage WithDetail(UiMessage reason, string? detail) =>
        string.IsNullOrWhiteSpace(detail)
            ? reason
            : UiMessage.For(
                NotificationResultMessage.ReasonWithProviderDetail,
                UiMessageArgument.FromMessage(reason),
                UiMessageArgument.FromText(detail));

    private NotificationSendResult Classify(HttpRequestException exception)
    {
        // A connection that was never established cannot have delivered anything, so it is a plain
        // failure. Anything else may have been received and is left unknown.
        var socketError = (exception.InnerException as SocketException)?.SocketErrorCode;
        var neverConnected = socketError is SocketError.ConnectionRefused or SocketError.HostNotFound
            or SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.NetworkDown;

        if (neverConnected)
        {
            logger.LogWarning(exception, "The Resend endpoint could not be reached ({SocketError})", socketError);
            return new NotificationSendResult(NotificationSendStatus.Rejected,
                NotificationResultMessage.ProviderUnreachable);
        }

        logger.LogWarning(exception, "The Resend request failed with an uncertain result");
        return new NotificationSendResult(NotificationSendStatus.Uncertain,
            NotificationResultMessage.RequestFailedAfterStarting);
    }

    private static async Task<string?> ReadIdAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ResendResponse>(SerializerOptions, cancellationToken);
            return body?.Id;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or HttpRequestException or IOException)
        {
            // The message was accepted; an unreadable body must not turn that into a failure.
            return null;
        }
    }

    /// <summary>
    /// Extracts a short, safe detail from an error body. Truncated so a verbose provider response
    /// cannot overflow the persisted error column.
    /// </summary>
    private static async Task<string> ReadErrorAsync(
        HttpResponseMessage response,
        string apiKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ResendError>(SerializerOptions, cancellationToken);
            var message = body?.Message;
            if (string.IsNullOrWhiteSpace(message)) return "";
            var single = string.Join(' ', message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var safe = Redact(single, apiKey);
            return safe.Length <= MaxSafeErrorLength ? safe : safe[..MaxSafeErrorLength] + "...";
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or HttpRequestException or IOException)
        {
            return "";
        }
    }

    /// <summary>
    /// Removes anything key-shaped from provider text before it is persisted, logged, or displayed.
    /// A provider that echoes the submitted credential back in an error body must not be able to put
    /// it into the outbox error column or the UI.
    /// </summary>
    private static string Redact(string text, string apiKey)
    {
        var scrubbed = text.Replace(apiKey, RedactedMarker, StringComparison.OrdinalIgnoreCase);
        return ApiKeyPattern().Replace(scrubbed, RedactedMarker);
    }

    [GeneratedRegex("re_[A-Za-z0-9_-]{4,}", RegexOptions.CultureInvariant)]
    private static partial Regex ApiKeyPattern();

    private sealed record ResendRequest(
        string From,
        IReadOnlyList<string> To,
        string Subject,
        string Html,
        string Text);

    private sealed record ResendResponse(string? Id);

    private sealed record ResendError(string? Message, string? Name);
}
