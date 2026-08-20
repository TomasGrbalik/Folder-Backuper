using System.Net.Http.Headers;

namespace FolderBackuper.Features.Updates;

public static class UpdateServiceCollectionExtensions
{
    /// <summary>
    /// Registers the release-notification feature: it reports that a newer version exists and never
    /// downloads or installs anything.
    /// </summary>
    public static IServiceCollection AddUpdateChecks(this IServiceCollection services)
    {
        services.AddSingleton<UpdateStatusStore>();
        services.AddSingleton<UpdateCheckSettingsService>();
        services.AddSingleton<GitHubReleaseClient>();
        services.AddSingleton<UpdateCheckService>();
        services.AddHostedService<UpdateCheckWorker>();

        services.AddHttpClient(GitHubReleaseClient.ClientName, client =>
        {
            client.BaseAddress = new Uri(UpdateCheckMetadata.ApiBaseAddress);
            client.Timeout = UpdateCheckMetadata.RequestTimeout;

            // GitHub refuses a request with no user agent. This one names the product and nothing
            // else, so the request says nothing about the machine that made it.
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue(UpdateCheckMetadata.UserAgent, null));
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

            // A service that runs for months must not be able to buffer an arbitrary response from
            // a system it does not control.
            client.MaxResponseContentBufferSize = UpdateCheckMetadata.MaxResponseBytes;
        });

        return services;
    }
}
