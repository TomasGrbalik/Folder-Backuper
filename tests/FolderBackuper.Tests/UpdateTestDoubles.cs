using FolderBackuper.Features.Settings;
using FolderBackuper.Features.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderBackuper.Tests;

internal static class UpdateTestFactory
{
    public static GitHubReleaseClient Client(FakeHttpMessageHandler handler, TimeProvider clock) =>
        new(new FakeHttpClientFactory(handler, "https://api.github.test/"),
            clock,
            NullLogger<GitHubReleaseClient>.Instance);

    public static UpdateCheckSettingsService Settings(TemporaryDatabase database, TimeProvider clock) =>
        new(database.ContextFactory, new InstallationIdentityService(database.ContextFactory, clock), clock);

    public static UpdateCheckService Checks(
        FakeHttpMessageHandler handler,
        TemporaryDatabase database,
        UpdateStatusStore store,
        TimeProvider clock) =>
        new(Client(handler, clock),
            Settings(database, clock),
            store,
            clock,
            NullLogger<UpdateCheckService>.Instance);

    /// <summary>A release payload the client accepts, so a test states only what it cares about.</summary>
    public static string ReleasePayload(string tag, string url = "https://example.test/release") =>
        $$"""
        {"tag_name":"{{tag}}","html_url":"{{url}}","published_at":"2026-08-19T08:00:00Z","draft":false,"prerelease":false}
        """;
}
