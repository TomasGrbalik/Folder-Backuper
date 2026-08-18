using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Features.Notifications;
using FolderBackuper.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Features.Monitoring;

/// <summary>
/// Assembles the health dashboard from durable state. Live active-run progress is joined separately by the UI
/// from the in-memory progress registry; every field here derives from SQLite so it survives reconnect and restart.
/// </summary>
public sealed class DashboardQueryService(
    IDbContextFactory<FolderBackuperDbContext> contextFactory,
    RunQueryService runs)
{
    public async Task<DashboardView> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var jobs = await context.Jobs.AsNoTracking()
            .Where(x => x.Lifecycle != JobLifecycle.Archived)
            .OrderBy(x => x.Name)
            .Select(x => new JobProjection(
                x.Id, x.Name, x.Lifecycle, x.RetentionCount,
                x.ManagedArtifactCount, x.ManagedArtifactBytes, x.LatestArtifactBytes,
                x.StorageConfirmedAtUtc, x.NextOccurrenceAtUtc))
            .ToListAsync(cancellationToken);

        var jobIds = jobs.Select(x => x.Id).ToList();

        // Compact terminal-run projections for the relevant jobs, newest first; reduced in memory to the
        // latest run and latest successful run per job. Small column set keeps this cheap for a dashboard load.
        var terminalRuns = (await context.Runs.AsNoTracking()
                .Where(x => jobIds.Contains(x.JobId) && x.Outcome != null)
                .Select(x => new TerminalRunProjection(x.JobId, x.CompletedAtUtc, x.Outcome!.Value, x.NotificationState))
                .ToListAsync(cancellationToken))
            .OrderByDescending(x => x.CompletedAtUtc ?? DateTimeOffset.MinValue)
            .ToList();

        var lastRun = new Dictionary<Guid, TerminalRunProjection>();
        var lastSuccess = new Dictionary<Guid, DateTimeOffset?>();
        foreach (var run in terminalRuns)
        {
            lastRun.TryAdd(run.JobId, run);
            if (run.Outcome is RunOutcome.Successful or RunOutcome.SuccessfulWithWarnings &&
                !lastSuccess.ContainsKey(run.JobId))
            {
                lastSuccess[run.JobId] = run.CompletedAtUtc;
            }
        }

        // Missing/unmanaged artifact counts per job in one grouped query.
        var artifactStats = await context.BackupArtifacts.AsNoTracking()
            .Where(x => jobIds.Contains(x.Run!.JobId) &&
                (x.State == ArtifactState.FoundMissing || x.State == ArtifactState.Unmanaged))
            .GroupBy(x => x.Run!.JobId)
            .Select(g => new ArtifactStat(
                g.Key,
                g.Sum(x => x.State == ArtifactState.FoundMissing ? 1 : 0),
                g.Sum(x => x.State == ArtifactState.Unmanaged ? 1 : 0)))
            .ToDictionaryAsync(x => x.JobId, cancellationToken);

        var cards = new List<JobStatusCard>(jobs.Count);
        foreach (var job in jobs)
        {
            lastRun.TryGetValue(job.Id, out var last);
            artifactStats.TryGetValue(job.Id, out var stats);
            var missing = stats?.Missing ?? 0;
            var stale = job.StorageConfirmedAtUtc is null || missing > 0;
            cards.Add(new JobStatusCard(
                job.Id, job.Name, job.Lifecycle,
                last?.Outcome, last?.CompletedAtUtc,
                lastSuccess.TryGetValue(job.Id, out var success) ? success : null,
                job.NextOccurrenceAtUtc,
                job.ManagedArtifactCount, job.ManagedArtifactBytes, job.LatestArtifactBytes,
                job.RetentionCount, job.StorageConfirmedAtUtc, stale, missing, stats?.Unmanaged ?? 0,
                last?.NotificationState));
        }

        var failureCount = cards.Count(x => x.LastOutcome == RunOutcome.Failed);
        var warningCount = cards.Count(x => x.LastOutcome == RunOutcome.SuccessfulWithWarnings);
        var notificationFailures = cards.Count(x =>
            x.LastNotificationState is NotificationDeliveryState.Failed or NotificationDeliveryState.DeliveryUnknown);

        var active = await runs.GetActiveRunAsync(cancellationToken);
        var queue = await runs.GetQueueAsync(cancellationToken);
        return new DashboardView(active, queue, cards, failureCount, warningCount, notificationFailures);
    }

    private sealed record JobProjection(
        Guid Id, string Name, JobLifecycle Lifecycle, int RetentionCount,
        long ManagedArtifactCount, long ManagedArtifactBytes, long? LatestArtifactBytes,
        DateTimeOffset? StorageConfirmedAtUtc, DateTimeOffset? NextOccurrenceAtUtc);

    private sealed record TerminalRunProjection(
        Guid JobId, DateTimeOffset? CompletedAtUtc, RunOutcome Outcome, NotificationDeliveryState? NotificationState);

    private sealed record ArtifactStat(Guid JobId, int Missing, int Unmanaged);
}
