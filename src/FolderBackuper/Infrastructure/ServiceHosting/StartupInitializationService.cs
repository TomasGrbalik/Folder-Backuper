using FolderBackuper.Features.Backups;
using FolderBackuper.Infrastructure.Database;

namespace FolderBackuper.Infrastructure.ServiceHosting;

/// <summary>
/// Applies database migrations and recovers interrupted work after the host has started.
/// </summary>
/// <remarks>
/// This work deliberately runs here rather than before <c>WebApplication.RunAsync</c>. Everything
/// executed before the host starts also runs before the service control manager handshake, and a
/// validated pre-migration backup followed by recovery that probes an unreachable SMB destination
/// can exceed the thirty-second service start window. Ordering guarantees are preserved by
/// <see cref="StartupRecoveryBarrier"/>, which the execution queue and the scheduler await before
/// doing any work.
/// </remarks>
public sealed class StartupInitializationService(
    DatabaseInitializer databaseInitializer,
    BackupRecoveryService recovery,
    StartupRecoveryBarrier barrier,
    ApplicationPaths paths,
    IHostApplicationLifetime lifetime,
    ILogger<StartupInitializationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await databaseInitializer.InitializeAsync(stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fail(new StartupFailureException(StartupFailure.Migration, exception), exception);
            return;
        }

        try
        {
            await recovery.RecoverAsync(stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fail(exception, exception);
            return;
        }

        barrier.Complete();
        logger.LogInformation("Startup initialization completed; the queue and scheduler are released");
    }

    private void Fail(Exception barrierException, Exception exception)
    {
        var failure = StartupFailureClassifier.Classify(barrierException);
        logger.LogCritical(exception, "Startup initialization failed: {OperatorMessage}", failure.OperatorMessage);
        StartupFailureReporter.Report(failure, exception, paths);
        barrier.Fault(barrierException);
        lifetime.StopApplication();
    }
}
