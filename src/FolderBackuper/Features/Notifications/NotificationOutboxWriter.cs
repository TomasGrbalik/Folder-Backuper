using System.Text.Json;
using FolderBackuper.Features.Backups;
using FolderBackuper.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

using FolderBackuper.Infrastructure.Localization;
namespace FolderBackuper.Features.Notifications;

/// <summary>
/// Adds notification outbox work to the same unit of work that makes a terminal run outcome durable.
/// </summary>
/// <remarks>
/// The caller's <see cref="FolderBackuperDbContext"/> is used deliberately: the outbox row and the
/// terminal outcome must commit together, so that a crash can never leave a completed run with no
/// notification intent, nor a notification for a run that did not complete.
/// </remarks>
public sealed class NotificationOutboxWriter(
    NotificationSettingsService settings,
    TimeProvider timeProvider,
    ILogger<NotificationOutboxWriter> logger)
{
    /// <summary>
    /// Adds an outbox row for <paramref name="run"/> when its outcome is notifiable and a deliverable
    /// configuration is saved. Does not save; the caller commits.
    /// </summary>
    /// <returns>True when a row was added, so the caller can signal the worker after committing.</returns>
    public async Task<bool> AddIfEligibleAsync(
        FolderBackuperDbContext context,
        BackupRun run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(run);

        if (run.Outcome is not { } outcome || !IsNotifiable(outcome)) return false;

        // Without a deliverable configuration no row is created at all. A permanently pending row
        // would otherwise show as an unresolved notification on every dashboard load, on an
        // installation that has deliberately never configured email.
        var view = await settings.GetAsync(cancellationToken);
        if (!view.IsDeliverable) return false;

        if (await context.NotificationOutbox.AnyAsync(item => item.RunId == run.Id, cancellationToken))
        {
            // Recovery can complete a run whose outcome was already persisted once. The unique index
            // on RunId would reject a second row, so stop before the insert.
            return false;
        }

        var problems = await context.RunProblems.AsNoTracking()
            .Where(problem => problem.RunId == run.Id)
            .ToListAsync(cancellationToken);
        var artifact = await context.BackupArtifacts.AsNoTracking()
            .FirstOrDefaultAsync(item => item.RunId == run.Id, cancellationToken);

        var payload = NotificationPayloadBuilder.Build(run, problems, artifact);
        context.NotificationOutbox.Add(new NotificationOutboxItem
        {
            RunId = run.Id,
            RunOutcome = outcome,
            PayloadSnapshot = JsonSerializer.Serialize(payload, NotificationPayloadSerializer.Options),
            CreatedAtUtc = timeProvider.GetUtcNow()
        });

        run.NotificationState = NotificationDeliveryState.Pending;
        run.NotificationMessageKey = null;
        run.NotificationMessageArguments = null;
        logger.LogInformation(
            "Queued a {Outcome} notification for run {RunId} of job {JobName}", outcome, run.Id, run.JobName);
        return true;
    }

    /// <summary>Cancelled runs never notify. Also enforced independently by a SQLite check constraint.</summary>
    public static bool IsNotifiable(RunOutcome outcome) =>
        outcome is RunOutcome.Successful or RunOutcome.SuccessfulWithWarnings or RunOutcome.Failed;
}

/// <summary>Shared options so a payload written by the writer is readable by the worker.</summary>
public static class NotificationPayloadSerializer
{
    public static JsonSerializerOptions Options { get; } = new();
}
