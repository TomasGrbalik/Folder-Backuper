using System.Threading.Channels;
using FolderBackuper.Infrastructure.ServiceHosting;
using Microsoft.Extensions.Hosting;

namespace FolderBackuper.Features.Notifications;

/// <summary>
/// A coalesced wake signal raised after a terminal run outcome commits notification work. Carries no
/// ordering or identity; the worker always reads pending records from the database.
/// </summary>
public sealed class NotificationOutboxSignal
{
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite
    });

    public void Signal() => wake.Writer.TryWrite(true);

    public ValueTask<bool> WaitAsync(CancellationToken cancellationToken) =>
        wake.Reader.ReadAsync(cancellationToken);
}

/// <summary>
/// Drives the notification outbox: startup recovery, then one sweep per signal.
/// </summary>
/// <remarks>
/// Recovery runs here rather than in <see cref="StartupInitializationService"/> on purpose. A failure
/// there stops the whole application, and no notification problem may ever prevent the service from
/// running backups. Every iteration is therefore caught and logged, and the loop never exits early.
/// <para>
/// The periodic timeout is only a safety net for a signal lost to a process restart between the
/// commit and the wake; steady-state delivery is signal-driven, so there is no idle polling.
/// </para>
/// </remarks>
public sealed class NotificationOutboxWorker(
    NotificationOutboxSignal signal,
    NotificationOutboxService outbox,
    StartupRecoveryBarrier barrier,
    TimeProvider timeProvider,
    ILogger<NotificationOutboxWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await barrier.WaitAsync(stoppingToken)) return;

        try
        {
            var recovered = await outbox.RecoverAsync(stoppingToken);
            if (recovered > 0)
            {
                logger.LogWarning(
                    "{Count} interrupted notification attempt(s) were recorded as delivery unknown", recovered);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Notification recovery failed; pending notifications are still attempted");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await outbox.ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The notification sweep failed");
            }

            if (!await WaitForWorkAsync(stoppingToken)) break;
        }
    }

    /// <summary>Waits for the next signal, giving up at the sweep interval so no work can sit forever.</summary>
    private async Task<bool> WaitForWorkAsync(CancellationToken stoppingToken)
    {
        using var timeout = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);
        using var timer = timeProvider.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(), timeout, SweepInterval, Timeout.InfiniteTimeSpan);

        try
        {
            await signal.WaitAsync(linked.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return !stoppingToken.IsCancellationRequested;
        }
    }
}
