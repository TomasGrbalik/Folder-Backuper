using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FolderBackuper.Infrastructure.ServiceHosting;

/// <summary>
/// A loopback-only readiness probe used by the installer after it starts the service.
/// </summary>
/// <remarks>
/// Kestrel now binds before database initialization completes, so a response from the Blazor root
/// no longer means the application is usable. The probe reports the state of
/// <see cref="StartupRecoveryBarrier"/> and returns no application data.
/// </remarks>
public static class ReadinessEndpoint
{
    public static IEndpointConventionBuilder MapReadiness(this WebApplication app) =>
        app.MapGet(WindowsServiceMetadata.ReadinessPath, (StartupRecoveryBarrier barrier, HttpResponse response) =>
        {
            response.Headers.CacheControl = "no-store";

            if (barrier.IsFaulted)
            {
                return Results.Text("failed", "text/plain", statusCode: StatusCodes.Status500InternalServerError);
            }

            return barrier.IsCompleted
                ? Results.Text("ready", "text/plain", statusCode: StatusCodes.Status200OK)
                : Results.Text("starting", "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable);
        });
}
