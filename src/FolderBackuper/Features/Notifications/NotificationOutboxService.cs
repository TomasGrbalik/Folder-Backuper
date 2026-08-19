using System.Text.Json;
using FolderBackuper.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Features.Notifications;

/// <summary>
/// The durable single-attempt notification workflow: recover, claim, send once, record.
/// </summary>
/// <remarks>
/// Claiming a pending record durably marks it Sending and defines the beginning of its single
/// attempt. Records left Sending by an interruption become delivery-unknown and are never retried:
/// a crash in the short window after the claim and before the provider responded may therefore lose
/// that notification, which is the accepted cost of never producing duplicate email.
/// <para>
/// This service does not take <see cref="ConfigurationMutationGate"/>. It only ever touches outbox
/// rows and the notification columns of runs that are already terminal, so it cannot race with
/// scheduling, claiming, or execution, and it must not be blocked by them.
/// </para>
/// </remarks>
public sealed class NotificationOutboxService(
    IDbContextFactory<FolderBackuperDbContext> contextFactory,
    IRunNotificationSender sender,
    TimeProvider timeProvider,
    ILogger<NotificationOutboxService> logger)
{
    internal const string InterruptedError =
        "The service stopped after the delivery attempt began. Whether the email was sent is unknown, "
        + "and it is deliberately not retried so that no duplicate can be sent.";

    /// <summary>
    /// Converts every record left Sending by an interruption to delivery-unknown, without contacting
    /// the provider. Runs once at startup, before any pending record is attempted.
    /// </summary>
    /// <returns>The number of records recovered.</returns>
    public async Task<int> RecoverAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var interrupted = await context.NotificationOutbox
            .Where(item => item.State == NotificationDeliveryState.Sending)
            .ToListAsync(cancellationToken);
        if (interrupted.Count == 0) return 0;

        var now = timeProvider.GetUtcNow();
        foreach (var item in interrupted)
        {
            item.MarkDeliveryUnknown(InterruptedError, now);
            await MirrorOntoRunAsync(context, item, cancellationToken);
            logger.LogWarning(
                "Notification {NotificationId} for run {RunId} was interrupted mid-attempt and is recorded "
                + "as delivery unknown without retry", item.Id, item.RunId);
        }

        await context.SaveChangesAsync(cancellationToken);
        return interrupted.Count;
    }

    /// <summary>
    /// Attempts every pending record once, oldest first.
    /// </summary>
    /// <returns>The number of records attempted.</returns>
    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken = default)
    {
        var attempted = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var claimed = await ClaimNextAsync(cancellationToken);
            if (claimed is null) break;

            attempted++;
            await AttemptAsync(claimed, cancellationToken);
        }

        return attempted;
    }

    /// <summary>
    /// Durably marks the oldest pending record Sending and returns it. The guarded update is what
    /// makes the claim atomic, so two sweeps can never begin the same attempt.
    /// </summary>
    private async Task<NotificationOutboxItem?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            // Ordered on the client because SQLite cannot ORDER BY a DateTimeOffset. The pending set is
            // bounded by the number of runs that finished since the last sweep, so this stays cheap.
            var pending = await context.NotificationOutbox.AsNoTracking()
                .Where(item => item.State == NotificationDeliveryState.Pending)
                .Select(item => new PendingProjection(item.Id, item.RunId, item.CreatedAtUtc))
                .ToListAsync(cancellationToken);
            var candidate = pending
                .OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id)
                .FirstOrDefault();
            if (candidate is null) return null;

            var now = timeProvider.GetUtcNow();
            var affected = await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE NotificationOutbox
                SET State = 'Sending', AttemptCount = AttemptCount + 1, SendingAtUtc = {now}
                WHERE Id = {candidate.Id} AND State = 'Pending'
                """, cancellationToken);
            if (affected != 1) continue;

            await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE Runs SET NotificationState = 'Sending' WHERE Id = {candidate.RunId}
                """, cancellationToken);

            context.ChangeTracker.Clear();
            return await context.NotificationOutbox.AsNoTracking()
                .SingleAsync(item => item.Id == candidate.Id, cancellationToken);
        }
    }

    private async Task AttemptAsync(NotificationOutboxItem claimed, CancellationToken cancellationToken)
    {
        NotificationSendResult result;
        try
        {
            var payload = Deserialize(claimed);
            // The provider call happens outside any database transaction, so a slow or unreachable
            // provider can never hold a write lock on the application database.
            result = payload is null
                ? new NotificationSendResult(NotificationSendStatus.Rejected,
                    "The saved notification content could not be read and was not sent.")
                : await sender.SendRunResultAsync(payload, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown during the attempt. The record stays Sending and startup recovery will record
            // it as delivery unknown, which is exactly the crash-after-claim rule.
            throw;
        }
        catch (Exception exception)
        {
            // A provider must not be able to fault the worker. An unexpected fault leaves the result
            // genuinely unknown, so it is recorded as such rather than as a clean failure.
            logger.LogError(exception,
                "Notification {NotificationId} for run {RunId} failed unexpectedly", claimed.Id, claimed.RunId);
            result = new NotificationSendResult(NotificationSendStatus.Uncertain,
                "The delivery attempt failed unexpectedly. Delivery is unknown.");
        }

        await RecordAsync(claimed.Id, result, cancellationToken);
    }

    private async Task RecordAsync(
        Guid notificationId,
        NotificationSendResult result,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var item = await context.NotificationOutbox
            .SingleOrDefaultAsync(row => row.Id == notificationId, cancellationToken);
        if (item is null || item.State != NotificationDeliveryState.Sending) return;

        var now = timeProvider.GetUtcNow();
        switch (result.Status)
        {
            case NotificationSendStatus.Delivered:
                item.MarkDelivered(now);
                logger.LogInformation(
                    "Notification {NotificationId} for run {RunId} was delivered", item.Id, item.RunId);
                break;
            case NotificationSendStatus.Uncertain:
                item.MarkDeliveryUnknown(result.Message, now);
                break;
            default:
                // Rejected and NotConfigured are both definite failures: nothing was sent.
                item.MarkFailed(result.Message, now);
                logger.LogWarning(
                    "Notification {NotificationId} for run {RunId} was not delivered: {Reason}",
                    item.Id, item.RunId, result.Message);
                break;
        }

        await MirrorOntoRunAsync(context, item, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Copies the delivery result onto the run so the dashboard, history, and run details can show it
    /// without joining the outbox. A delivery result never changes the backup outcome itself.
    /// </summary>
    private static async Task MirrorOntoRunAsync(
        FolderBackuperDbContext context,
        NotificationOutboxItem item,
        CancellationToken cancellationToken)
    {
        var run = await context.Runs.SingleOrDefaultAsync(row => row.Id == item.RunId, cancellationToken);
        if (run is null) return;

        run.NotificationState = item.State;
        run.NotificationErrorSummary = Truncate(item.LastSafeError);
    }

    private NotificationPayload? Deserialize(NotificationOutboxItem item)
    {
        try
        {
            return JsonSerializer.Deserialize<NotificationPayload>(
                item.PayloadSnapshot, NotificationPayloadSerializer.Options);
        }
        catch (JsonException exception)
        {
            logger.LogError(exception,
                "The stored payload for notification {NotificationId} could not be read", item.Id);
            return null;
        }
    }

    // Matches the NotificationErrorSummary and LastSafeError column limits.
    private static string? Truncate(string? value) => value is { Length: > 2000 } ? value[..2000] : value;

    private sealed record PendingProjection(Guid Id, Guid RunId, DateTimeOffset CreatedAtUtc);
}
