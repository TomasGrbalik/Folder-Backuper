using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Settings;
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
    UiLanguageSettingsService uiLanguage,
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

        // The interface language is applied before recovery so that the barrier releases the queue and
        // the scheduler into the right culture, and so that a recovered run's problems and any email it
        // produces are recorded in the language the interface is configured for. It cannot be applied
        // any earlier than this: the settings row is only readable once migrations have run, and
        // migrations deliberately run after the host has started. Until this point the process keeps the
        // machine culture, which is also what an installation that has never chosen a language uses.
        try
        {
            await uiLanguage.ApplyStoredAsync(stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A language that cannot be read is not worth refusing to start over. The machine culture
            // stays in effect and the interface still works.
            logger.LogWarning(exception, "The interface language could not be applied; the machine culture stays in effect");
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
