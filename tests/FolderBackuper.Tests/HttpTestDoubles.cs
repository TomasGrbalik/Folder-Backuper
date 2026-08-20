using System.Net;

namespace FolderBackuper.Tests;

/// <summary>
/// Returns a scripted response, or throws a scripted exception, without any network access. No test
/// in this project ever contacts a real email provider or a real release feed.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;

    private FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => this.respond = respond;

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> RequestBodies { get; } = [];

    public static FakeHttpMessageHandler Returning(HttpStatusCode status, string body = "{}") =>
        new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        });

    public static FakeHttpMessageHandler Throwing(Exception exception) => new(_ => throw exception);

    /// <summary>
    /// For a response the other factories cannot express, such as one carrying rate-limit headers.
    /// </summary>
    public static FakeHttpMessageHandler Responding(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(respond);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // The body has to be read before the response is produced, because a scripted throw would
        // otherwise leave nothing to assert the payload against.
        RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
        Requests.Add(request);
        return respond(request);
    }
}

/// <summary>Hands out one client bound to a fake handler, standing in for the real factory.</summary>
internal sealed class FakeHttpClientFactory(FakeHttpMessageHandler handler, string baseAddress = "https://api.resend.test/")
    : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
    {
        BaseAddress = new Uri(baseAddress)
    };
}
