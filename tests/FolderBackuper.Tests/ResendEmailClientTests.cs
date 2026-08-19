using System.Net;
using System.Net.Sockets;
using FolderBackuper.Features.Notifications;

namespace FolderBackuper.Tests;

/// <summary>
/// Covers how one provider response becomes a delivery classification. No test here reaches the
/// network: every response is scripted by <see cref="FakeHttpMessageHandler"/>.
/// </summary>
public sealed class ResendEmailClientTests
{
    private const string ApiKey = "re_secret_value_1234567890";

    private static ResendMessage Message() => new(
        "Folder Backuper <backups@example.test>",
        ["operator@example.test"],
        "Folder Backuper: Finance - backup successful",
        "<p>ok</p>",
        "ok");

    [Fact]
    public async Task Send_TreatsAcceptanceAsDelivered()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, """{"id":"abc-123"}""");

        var result = await NotificationTestFactory.Client(handler).SendAsync(Message(), ApiKey);

        Assert.Equal(NotificationSendStatus.Delivered, result.Status);
        Assert.True(result.Succeeded);
        Assert.Contains("abc-123", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_TreatsAcceptanceWithAnUnreadableBodyAsDelivered()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, "not json");

        var result = await NotificationTestFactory.Client(handler).SendAsync(Message(), ApiKey);

        Assert.Equal(NotificationSendStatus.Delivered, result.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Send_TreatsEveryClientRefusalAsRejected(HttpStatusCode status)
    {
        // A refused request was never accepted, so it is a definite failure rather than an unknown.
        var handler = FakeHttpMessageHandler.Returning(status, """{"message":"Domain is not verified"}""");

        var result = await NotificationTestFactory.Client(handler).SendAsync(Message(), ApiKey);

        Assert.Equal(NotificationSendStatus.Rejected, result.Status);
        Assert.Contains("Domain is not verified", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Send_TreatsAServerErrorAsUncertain(HttpStatusCode status)
    {
        var handler = FakeHttpMessageHandler.Returning(status, """{"message":"Internal error"}""");

        var result = await NotificationTestFactory.Client(handler).SendAsync(Message(), ApiKey);

        Assert.Equal(NotificationSendStatus.Uncertain, result.Status);
        Assert.Contains("unknown", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Send_TreatsATimeoutAsUncertain()
    {
        // HttpClient surfaces its own timeout as TaskCanceledException with no cancellation requested.
        var handler = FakeHttpMessageHandler.Throwing(new TaskCanceledException("The request timed out."));

        var result = await NotificationTestFactory.Client(handler).SendAsync(Message(), ApiKey);

        Assert.Equal(NotificationSendStatus.Uncertain, result.Status);
    }

    [Fact]
    public async Task Send_TreatsAConnectionThatWasNeverEstablishedAsRejected()
    {
        var handler = FakeHttpMessageHandler.Throwing(
            new HttpRequestException("No connection", new SocketException((int)SocketError.ConnectionRefused)));

        var result = await NotificationTestFactory.Client(handler).SendAsync(Message(), ApiKey);

        Assert.Equal(NotificationSendStatus.Rejected, result.Status);
        Assert.Contains("could not be reached", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_TreatsAFailureAfterTheRequestStartedAsUncertain()
    {
        var handler = FakeHttpMessageHandler.Throwing(new HttpRequestException("The connection was reset."));

        var result = await NotificationTestFactory.Client(handler).SendAsync(Message(), ApiKey);

        Assert.Equal(NotificationSendStatus.Uncertain, result.Status);
    }

    [Fact]
    public async Task Send_PostsTheMessageAsBearerAuthorizedJson()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, """{"id":"abc"}""");

        await NotificationTestFactory.Client(handler).SendAsync(Message(), ApiKey);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(ResendEmailClient.SendPath, request.RequestUri!.ToString().TrimStart('/').Split('/')[^1]);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(ApiKey, request.Headers.Authorization.Parameter);

        var body = Assert.Single(handler.RequestBodies);
        Assert.Contains("\"from\":", body, StringComparison.Ordinal);
        Assert.Contains("\"to\":", body, StringComparison.Ordinal);
        Assert.Contains("\"subject\":", body, StringComparison.Ordinal);
        Assert.Contains("\"html\":", body, StringComparison.Ordinal);
        Assert.Contains("\"text\":", body, StringComparison.Ordinal);
        Assert.Contains("operator@example.test", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Send_NeverPutsTheApiKeyInTheResultMessage(HttpStatusCode status)
    {
        // The result is persisted as LastSafeError and rendered in the UI, so it must stay secret-free.
        var handler = FakeHttpMessageHandler.Returning(status, $$"""{"message":"rejected {{ApiKey}}"}""");

        var result = await NotificationTestFactory.Client(handler).SendAsync(Message(), ApiKey);

        Assert.DoesNotContain("re_secret_value", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_TruncatesAVerboseProviderErrorSoItFitsThePersistedColumn()
    {
        var verbose = new string('x', 5000);
        var handler = FakeHttpMessageHandler.Returning(
            HttpStatusCode.BadRequest, $$"""{"message":"{{verbose}}"}""");

        var result = await NotificationTestFactory.Client(handler).SendAsync(Message(), ApiKey);

        Assert.True(result.Message.Length < 2000, $"The safe error was {result.Message.Length} characters.");
    }

    [Fact]
    public async Task Send_PropagatesShutdownRatherThanReportingADeliveryResult()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var handler = FakeHttpMessageHandler.Throwing(new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NotificationTestFactory.Client(handler).SendAsync(Message(), ApiKey, cancellation.Token));
    }

    [Fact]
    public async Task Send_RequiresAnApiKey()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            NotificationTestFactory.Client(handler).SendAsync(Message(), "  "));
        Assert.Empty(handler.Requests);
    }
}
