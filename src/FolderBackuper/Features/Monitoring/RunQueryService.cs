using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Notifications;
using FolderBackuper.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Features.Monitoring;

/// <summary>
/// Read-only projections over durable run history for the monitoring UI. Every query uses a fresh
/// no-tracking context from the factory and never mutates state; permanent history cannot be cleared here.
/// SQLite cannot ORDER BY <see cref="DateTimeOffset"/> in LINQ, so chronological ordering is applied in memory
/// over already-filtered projections.
/// </summary>
public sealed class RunQueryService(IDbContextFactory<FolderBackuperDbContext> contextFactory)
{
    public const int DefaultPageSize = 50;

    public async Task<ActiveRunView?> GetActiveRunAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        // At most one run executes at a time; load the small candidate set and pick the most recent in memory.
        var candidates = await context.Runs.AsNoTracking()
            .Where(x => x.Outcome == null && x.Phase != RunPhase.Planned && x.Phase != RunPhase.Queued)
            .Select(x => new ActiveRunView(x.Id, x.JobId, x.JobName, x.SourcePath, x.DestinationName,
                x.DestinationType, x.Phase, x.Trigger, x.StartedAtUtc, x.CancellationRequestedAtUtc != null))
            .ToListAsync(cancellationToken);
        return candidates.OrderByDescending(x => x.StartedAtUtc ?? DateTimeOffset.MinValue).FirstOrDefault();
    }

    public async Task<IReadOnlyList<QueuedRunView>> GetQueueAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var queued = await context.Runs.AsNoTracking()
            .Where(x => x.Outcome == null && x.Phase == RunPhase.Queued)
            .Select(x => new QueuedRunView(x.Id, x.JobId, x.JobName, x.Trigger, x.DueAtUtc, x.QueuedAtUtc))
            .ToListAsync(cancellationToken);
        return queued
            .OrderBy(x => x.DueAtUtc).ThenBy(x => x.QueuedAtUtc).ThenBy(x => x.RunId)
            .ToArray();
    }

    public async Task<RunHistoryPage> ListHistoryAsync(
        RunHistoryFilter filter,
        int page = 0,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(0, page);
        pageSize = Math.Clamp(pageSize, 1, 500);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = Filtered(context, filter);
        var total = await query.CountAsync(cancellationToken);

        // Pull the ordering-relevant projection (no correlated counts, no DateTimeOffset ORDER BY), sort and page
        // in memory, then fetch problem counts only for the visible page.
        var rows = await query
            .Select(x => new HistoryProjection(
                x.Id, x.JobId, x.JobName, x.Trigger, x.Phase, x.Outcome,
                x.StartedAtUtc, x.CompletedAtUtc, x.QueuedAtUtc, x.ArchiveBytes,
                x.Artifact != null ? x.Artifact.State : (ArtifactState?)null,
                x.NotificationState))
            .ToListAsync(cancellationToken);

        var ordered = rows
            .OrderByDescending(x => x.CompletedAtUtc ?? x.StartedAtUtc ?? x.QueuedAtUtc)
            .ThenByDescending(x => x.Id)
            .Skip(page * pageSize).Take(pageSize)
            .ToList();

        var pageIds = ordered.Select(x => x.Id).ToList();
        var problemCounts = await context.RunProblems.AsNoTracking()
            .Where(x => pageIds.Contains(x.RunId))
            .GroupBy(x => x.RunId)
            .Select(g => new { RunId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RunId, x => x.Count, cancellationToken);

        var result = ordered.Select(x => new RunHistoryRow(
            x.Id, x.JobId, x.JobName, x.Trigger, x.Phase, x.Outcome,
            x.StartedAtUtc, x.CompletedAtUtc,
            x.StartedAtUtc is not null && x.CompletedAtUtc is not null ? x.CompletedAtUtc - x.StartedAtUtc : null,
            x.ArchiveBytes, x.ArtifactState, x.NotificationState,
            problemCounts.TryGetValue(x.Id, out var count) ? count : 0)).ToList();

        return new RunHistoryPage(result, total, page, pageSize);
    }

    public async Task<RunDetailsView?> GetRunDetailsAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var run = await context.Runs.AsNoTracking()
            .Include(x => x.Artifact)
            .SingleOrDefaultAsync(x => x.Id == runId, cancellationToken);
        if (run is null)
        {
            return null;
        }

        var problemCount = await context.RunProblems.AsNoTracking().CountAsync(x => x.RunId == runId, cancellationToken);
        return new RunDetailsView(
            run.Id, run.JobId, run.JobName, run.SourcePath, run.DestinationName, run.DestinationType,
            run.DestinationRootPath, run.DestinationSubfolder, run.ScheduledWeekdays, run.ScheduledTime,
            run.RetentionCount, run.TimeZoneId, run.Trigger, run.Phase, run.Outcome,
            run.DueAtUtc, run.QueuedAtUtc, run.StartedAtUtc, run.CompletedAtUtc,
            run.StartedAtUtc is not null && run.CompletedAtUtc is not null ? run.CompletedAtUtc - run.StartedAtUtc : null,
            run.FileCount, run.DirectoryCount, run.SourceBytes, run.ArchiveBytes,
            run.CompressionDuration, run.TransferDuration, run.ErrorSummary,
            run.Artifact?.FinalFileName, run.Artifact?.EffectivePath, run.Artifact?.Size, run.Artifact?.State,
            run.NotificationState, run.NotificationErrorSummary, problemCount);
    }

    public async Task<RunProblemPage> ListRunProblemsAsync(
        Guid runId,
        int page = 0,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(0, page);
        pageSize = Math.Clamp(pageSize, 1, 500);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.RunProblems.AsNoTracking().Where(x => x.RunId == runId);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(x => x.Severity).ThenBy(x => x.Id)
            .Skip(page * pageSize).Take(pageSize)
            .Select(x => new RunProblemRow(
                x.Id, x.Path, x.Phase, x.Severity, x.Operation, x.ErrorCategory,
                x.NativeErrorCode, x.UserMessage, x.DiagnosticDetail))
            .ToListAsync(cancellationToken);

        return new RunProblemPage(rows, total, page, pageSize);
    }

    private static IQueryable<BackupRun> Filtered(FolderBackuperDbContext context, RunHistoryFilter filter)
    {
        var query = context.Runs.AsNoTracking().Where(x => x.Phase != RunPhase.Planned);
        if (filter.JobId is { } jobId)
        {
            query = query.Where(x => x.JobId == jobId);
        }

        return filter.Status switch
        {
            RunStatusFilter.Successful => query.Where(x => x.Outcome == RunOutcome.Successful),
            RunStatusFilter.Warnings => query.Where(x => x.Outcome == RunOutcome.SuccessfulWithWarnings),
            RunStatusFilter.Failed => query.Where(x => x.Outcome == RunOutcome.Failed),
            RunStatusFilter.Cancelled => query.Where(x => x.Outcome == RunOutcome.Cancelled),
            _ => query
        };
    }

    private sealed record HistoryProjection(
        Guid Id, Guid JobId, string JobName, RunTrigger Trigger, RunPhase Phase, RunOutcome? Outcome,
        DateTimeOffset? StartedAtUtc, DateTimeOffset? CompletedAtUtc, DateTimeOffset QueuedAtUtc,
        long ArchiveBytes, ArtifactState? ArtifactState, NotificationDeliveryState? NotificationState);
}
