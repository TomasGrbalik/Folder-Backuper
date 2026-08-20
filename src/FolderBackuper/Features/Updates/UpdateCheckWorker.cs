using FolderBackuper.Infrastructure.ServiceHosting;

namespace FolderBackuper.Features.Updates;

/// <summary>
/// Looks for a newer release on a slow cadence for as long as the service runs.
/// </summary>
/// <remarks>
/// The loop is deliberately thin: every decision it makes lives in <see cref="UpdateCheckService"/>,
/// where it can be tested without a clock that can drive timers.
/// <para>
/// No failure here may ever stop the service. Every iteration is caught and logged, and the loop
/// never exits early, exactly as the notification outbox worker does.
/// </para>
/// </remarks>
public sealed class UpdateCheckWorker(
    UpdateCheckService checks,
    StartupRecoveryBarrier startupRecovery,
    TimeProvider timeProvider,
    ILogger<UpdateCheckWorker> logger) : BackgroundService
{
    /// <summary>
    /// The service starts with a delayed automatic start precisely because the network has not
    /// settled at boot, so the first check waits. The time of the last check is not persisted, so a
    /// machine that restarts repeatedly would otherwise call the release feed on every start.
    /// </summary>
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);

    /// <summary>Spreads machines that were all restarted at the same time.</summary>
    private static readonly TimeSpan MaximumJitter = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The check reads the settings row, so it waits for startup recovery like every other worker
        // that touches the database.
        if (!await startupRecovery.WaitAsync(stoppingToken))
        {
            return;
        }

        var delay = InitialDelay + TimeSpan.FromMilliseconds(
            Random.Shared.Next((int)MaximumJitter.TotalMilliseconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var outcome = await checks.CheckNowAsync(stoppingToken);
                delay = outcome.NextDelay;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The update check failed unexpectedly");
                delay = UpdateCheckService.RetryInterval;
            }
        }
    }
}
