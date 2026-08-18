using FolderBackuper.Features.Backups;
using FolderBackuper.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Features.Monitoring;

/// <summary>
/// Read-only projections over durable run history for the monitoring UI. Every query uses a fresh
/// no-tracking context from the factory and never mutates state; permanent history cannot be cleared here.
/// </summary>
public sealed class RunQueryService(IDbContextFactory<FolderBackuperDbContext> contextFactory)
{
    public const int DefaultPageSize = 50;

    public async Task<ActiveRunView?> GetActiveRunAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var run = await context.Runs.AsNoTracking()
            .Where(x => x.Outcome == null && x.Phase != RunPhase.Planned)
            .OrderByDescending(x => x.StartedAtUtc ?? x.QueuedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        return run is null
            ? null
            : new ActiveRunView(run.Id, run.JobId, run.JobName, run.SourcePath, run.DestinationName,
                run.DestinationType, run.Phase, run.Trigger, run.StartedAtUtc,
                run.CancellationRequestedAtUtc is not null);
    }

    public async Task<IReadOnlyList<QueuedRunView>> GetQueueAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Runs.AsNoTracking()
            .Where(x => x.Outcome == null && x.Phase == RunPhase.Queued)
            .OrderBy(x => x.DueAtUtc).ThenBy(x => x.QueuedAtUtc).ThenBy(x => x.Id)
            .Select(x => new QueuedRunView(x.Id, x.JobId, x.JobName, x.Trigger, x.DueAtUtc, x.QueuedAtUtc))
            .ToListAsync(cancellationToken);
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

        var query = context.Runs.AsNoTracking().Where(x => x.Phase != RunPhase.Planned);
        if (filter.JobId is { } jobId)
        {
            query = query.Where(x => x.JobId == jobId);
        }

        query = filter.Status switch
        {
            RunStatusFilter.Successful => query.Where(x => x.Outcome == RunOutcome.Successful),
            RunStatusFilter.Warnings => query.Where(x => x.Outcome == RunOutcome.SuccessfulWithWarnings),
            RunStatusFilter.Failed => query.Where(x => x.Outcome == RunOutcome.Failed),
            RunStatusFilter.Cancelled => query.Where(x => x.Outcome == RunOutcome.Cancelled),
            _ => query
        };

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(x => x.CompletedAtUtc ?? x.StartedAtUtc ?? x.QueuedAtUtc)
            .ThenByDescending(x => x.Id)
            .Skip(page * pageSize).Take(pageSize)
            .Select(x => new RunHistoryRow(
                x.Id, x.JobId, x.JobName, x.Trigger, x.Phase, x.Outcome,
                x.StartedAtUtc, x.CompletedAtUtc,
                x.StartedAtUtc != null && x.CompletedAtUtc != null ? x.CompletedAtUtc - x.StartedAtUtc : null,
                x.ArchiveBytes,
                x.Artifact != null ? x.Artifact.State : (ArtifactState?)null,
                x.Problems.Count))
            .ToListAsync(cancellationToken);

        return new RunHistoryPage(rows, total, page, pageSize);
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
}
