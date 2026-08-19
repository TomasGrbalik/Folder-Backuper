using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Features.Monitoring;
using FolderBackuper.Features.Notifications;
using FolderBackuper.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Infrastructure.Database;

/// <summary>
/// Every durable run state transition. <paramref name="notifications"/>,
/// <paramref name="notificationSignal"/>, and <paramref name="activity"/> are optional so that tests
/// exercising run persistence alone need no notification or monitoring configuration; when absent,
/// terminal outcomes simply create no outbox work and no page is told to reload.
/// </summary>
public sealed class RunPersistenceService(
    IDbContextFactory<FolderBackuperDbContext> contextFactory,
    ConfigurationMutationGate mutationGate,
    TimeProvider timeProvider,
    NotificationOutboxWriter? notifications = null,
    NotificationOutboxSignal? notificationSignal = null,
    RunActivitySignal? activity = null)
{
    public async Task<ManualRunEnqueueOutcome> EnqueueManualAsync(
        Guid jobId,
        Func<BackupJob, Destination, CancellationToken, Task<string?>>? validate = null,
        CancellationToken cancellationToken = default)
    {
        var outcome = await mutationGate.ExecuteRunStateChangeAsync<ManualRunEnqueueOutcome>(async ct =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var job = await context.Jobs.Include(item => item.Destination)
                .SingleOrDefaultAsync(item => item.Id == jobId, ct);
            if (job is null || job.Lifecycle == JobLifecycle.Archived ||
                job.Destination is null || job.Destination.Lifecycle != DestinationLifecycle.Active)
            {
                return new(ManualRunEnqueueStatus.Unavailable, null,
                    "The job or destination is unavailable for a manual backup.");
            }

            if (validate is not null && await validate(job, job.Destination, ct) is { } validationError)
            {
                return new(ManualRunEnqueueStatus.OwnershipInvalid, null, validationError);
            }

            var now = timeProvider.GetUtcNow();
            var run = CreateSnapshot(job, job.Destination, RunTrigger.Manual, now, now);
            run.AdvanceTo(RunPhase.Queued, now);
            context.Runs.Add(run);
            try
            {
                await context.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                return new(ManualRunEnqueueStatus.Busy, null,
                    "The job already has backup work pending or running.");
            }

            return new(ManualRunEnqueueStatus.Queued, run.Id, "The backup was queued.");
        }, cancellationToken);

        if (outcome.Status == ManualRunEnqueueStatus.Queued) activity?.Signal();
        return outcome;
    }

    public async Task<BackupRun?> ClaimNextAsync(CancellationToken cancellationToken = default)
    {
        var claimed = await mutationGate.ExecuteRunStateChangeAsync<BackupRun?>(async ct =>
        {
            while (true)
            {
                await using var context = await contextFactory.CreateDbContextAsync(ct);
                var candidate = await context.Runs.FromSqlRaw("""
                    SELECT * FROM Runs
                    WHERE Outcome IS NULL AND Phase = 'Queued' AND CancellationRequestedAtUtc IS NULL
                    ORDER BY DueAtUtc, QueuedAtUtc, Id
                    LIMIT 1
                    """).AsNoTracking().SingleOrDefaultAsync(ct);
                if (candidate is null) return null;

                var now = timeProvider.GetUtcNow();
                var affected = await context.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE Runs
                    SET Phase = 'Scanning', StartedAtUtc = {now}
                    WHERE Id = {candidate.Id} AND Outcome IS NULL AND Phase = 'Queued'
                      AND CancellationRequestedAtUtc IS NULL
                    """, ct);
                if (affected == 1)
                {
                    return await context.Runs.AsNoTracking().SingleAsync(item => item.Id == candidate.Id, ct);
                }
            }
        }, cancellationToken);

        if (claimed is not null) activity?.Signal();
        return claimed;
    }

    public async Task<RunCancellationOutcome> RequestCancellationAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        var outcome = await mutationGate.ExecuteRunStateChangeAsync<RunCancellationOutcome>(async ct =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var run = await context.Runs.SingleOrDefaultAsync(item => item.Id == runId, ct);
            if (run is null) return new(RunCancellationStatus.NotFound, "The backup run was not found.");
            if (run.Outcome is not null) return new(RunCancellationStatus.AlreadyTerminal, "The backup run has already finished.");
            if (run.FinalCommitStartedAtUtc is not null)
            {
                return new(RunCancellationStatus.CommitStarted,
                    "The backup can no longer be cancelled because finalization has started.");
            }

            run.RequestCancellation(timeProvider.GetUtcNow());
            await context.SaveChangesAsync(ct);
            return new(run.Outcome == RunOutcome.Cancelled ? RunCancellationStatus.Cancelled : RunCancellationStatus.Requested,
                run.Outcome == RunOutcome.Cancelled ? "The queued backup was cancelled." : "Cancellation was requested.");
        }, cancellationToken);

        if (outcome.Status is RunCancellationStatus.Requested or RunCancellationStatus.Cancelled) activity?.Signal();
        return outcome;
    }

    public async Task AdvancePhaseAsync(Guid runId, RunPhase phase, CancellationToken cancellationToken = default)
    {
        await ChangeRunAsync(runId, run => run.AdvanceTo(phase, timeProvider.GetUtcNow()), cancellationToken);
    }

    public async Task RecordStagingPathAsync(Guid runId, string stagingPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingPath);
        await ChangeRunAsync(runId, run => run.StagingPath = stagingPath, cancellationToken);
    }

    public async Task RecordDestinationPartialPathAsync(
        Guid runId,
        string partialPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partialPath);
        await ChangeRunAsync(runId, run => run.DestinationPartialPath = partialPath, cancellationToken);
    }

    public async Task RecordExecutionResultAsync(
        BackupEngineResult result,
        CancellationToken cancellationToken = default)
    {
        await mutationGate.ExecuteRunStateChangeAsync(async ct =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var run = await context.Runs.SingleAsync(item => item.Id == result.RunId, ct);
            run.FileCount = result.FileCount;
            run.DirectoryCount = result.DirectoryCount;
            run.SourceBytes = result.SourceBytes;
            run.ArchiveBytes = result.ArchiveBytes;
            run.CompressionDuration = result.CompressionDuration;
            run.TransferDuration = result.TransferDuration;
            run.ErrorSummary = result.Problems.FirstOrDefault(problem => problem.Severity == BackupProblemSeverity.Error)?.Message;
            AddProblems(context, run.Id, result.Problems);

            await context.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
    }

    public async Task AppendProblemsAsync(
        Guid runId,
        IReadOnlyCollection<BackupProblem> problems,
        CancellationToken cancellationToken = default)
    {
        if (problems.Count == 0) return;
        await mutationGate.ExecuteRunStateChangeAsync(async ct =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            AddProblems(context, runId, problems);
            await context.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
    }

    public async Task<bool> HasWarningsAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.RunProblems.AsNoTracking()
            .AnyAsync(item => item.RunId == runId && item.Severity == BackupProblemSeverity.Warning, cancellationToken);
    }

    public async Task BeginFinalCommitAsync(BackupCommitIntent intent, CancellationToken cancellationToken = default)
    {
        await mutationGate.ExecuteRunStateChangeAsync(async ct =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var run = await context.Runs.SingleAsync(item => item.Id == intent.RunId, ct);
            if (run.CancellationRequestedAtUtc is not null)
            {
                throw new DurableCancellationRequestedException(run.Id);
            }
            run.DestinationPartialPath = intent.PartialPath;
            run.AdvanceTo(RunPhase.Finalizing, timeProvider.GetUtcNow());
            run.BeginFinalCommit(timeProvider.GetUtcNow());
            context.BackupArtifacts.Add(new BackupArtifact
            {
                RunId = run.Id,
                DestinationName = run.DestinationName,
                DestinationRootPath = run.DestinationRootPath,
                EffectivePath = intent.EffectiveDestinationPath,
                FinalFileName = intent.FinalFileName,
                Size = intent.ExpectedLength,
                CreatedAtUtc = intent.CreatedAtUtc,
                OwnershipRunId = run.Id,
                OwnershipExpectedLength = intent.ExpectedLength,
                OwnershipCreatedAtUtc = intent.OwnershipCreatedAtUtc,
                OwnershipFileSystemIdentity = intent.FileSystemIdentity
            });
            await context.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);

        activity?.Signal();
    }

    public async Task MarkFinalCommittedAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await mutationGate.ExecuteRunStateChangeAsync(async ct =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var run = await context.Runs.Include(item => item.Artifact)
                .SingleAsync(item => item.Id == runId, ct);
            run.MarkFinalCommitted(timeProvider.GetUtcNow());
            run.Artifact!.MarkRetained(timeProvider.GetUtcNow());
            run.DestinationPartialPath = null;
            await context.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
    }

    public async Task MarkFinalizationFailedAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await mutationGate.ExecuteRunStateChangeAsync(async ct =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var artifact = await context.BackupArtifacts.SingleAsync(item => item.RunId == runId, ct);
            artifact.MarkFinalizationFailed(timeProvider.GetUtcNow());
            await context.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
    }

    /// <summary>
    /// Makes a run outcome durable and, in the same transaction, creates the notification work that
    /// outcome requires. Committing both together is what lets recovery treat a completed run with no
    /// outbox row as genuinely not requiring notification.
    /// </summary>
    public async Task CompleteAsync(
        Guid runId,
        RunOutcome outcome,
        string? errorSummary,
        CancellationToken cancellationToken = default)
    {
        var queued = await ChangeRunAsync(runId, async (context, run, ct) =>
        {
            run.ErrorSummary = errorSummary;
            run.Complete(outcome, timeProvider.GetUtcNow());
            return notifications is not null && await notifications.AddIfEligibleAsync(context, run, ct);
        }, cancellationToken);

        // Signalled only after the transaction committed, so the worker can never look for a row that
        // is not there yet.
        if (queued) notificationSignal?.Signal();
    }

    public async Task CreateAsync(
        BackupRun run,
        ScheduledOccurrence? occurrence = null,
        CancellationToken cancellationToken = default)
    {
        var requiresOccurrence = run.Trigger is RunTrigger.Scheduled or RunTrigger.CatchUp;
        if (requiresOccurrence && occurrence is null)
        {
            throw new InvalidOperationException($"A {run.Trigger} run requires a scheduled occurrence.");
        }

        if (occurrence is not null && occurrence.JobId != run.JobId)
        {
            throw new InvalidOperationException("The run and scheduled occurrence must belong to the same job.");
        }

        await mutationGate.ExecuteRunStateChangeAsync(async ct =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            context.Runs.Add(run);
            if (occurrence is not null)
            {
                occurrence.RunId = run.Id;
                context.ScheduledOccurrences.Add(occurrence);
            }

            await context.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);

        activity?.Signal();
    }

    private async Task ChangeRunAsync(Guid runId, Action<BackupRun> change, CancellationToken cancellationToken)
    {
        await mutationGate.ExecuteRunStateChangeAsync(async ct =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var run = await context.Runs.SingleAsync(item => item.Id == runId, ct);
            change(run);
            await context.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);

        // Every committed transition tells open monitoring pages that their view is stale. Signalling
        // after the gate released keeps a subscriber's reload out of the serialized write path.
        activity?.Signal();
    }

    /// <summary>
    /// Applies a change that also needs the context, so additional rows can be written in the same
    /// transaction as the run change.
    /// </summary>
    private async Task<T> ChangeRunAsync<T>(
        Guid runId,
        Func<FolderBackuperDbContext, BackupRun, CancellationToken, Task<T>> change,
        CancellationToken cancellationToken)
    {
        var result = await mutationGate.ExecuteRunStateChangeAsync(async ct =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var run = await context.Runs.SingleAsync(item => item.Id == runId, ct);
            var value = await change(context, run, ct);
            await context.SaveChangesAsync(ct);
            return value;
        }, cancellationToken);

        activity?.Signal();
        return result;
    }

    private static void AddProblems(
        FolderBackuperDbContext context,
        Guid runId,
        IEnumerable<BackupProblem> problems)
    {
        foreach (var problem in problems)
        {
            context.RunProblems.Add(new RunProblem
            {
                RunId = runId,
                Path = problem.Path,
                Phase = problem.Phase,
                Severity = problem.Severity,
                Operation = problem.Operation,
                ErrorCategory = problem.Category.ToString(),
                NativeErrorCode = problem.NativeErrorCode?.ToString(),
                UserMessage = problem.Message
            });
        }
    }

    internal static BackupRun CreateSnapshot(
        BackupJob job,
        Destination destination,
        RunTrigger trigger,
        DateTimeOffset dueAtUtc,
        DateTimeOffset queuedAtUtc,
        ScheduleOccurrence? occurrence = null) => new()
    {
        JobId = job.Id,
        DestinationId = destination.Id,
        JobName = job.Name,
        SourcePath = job.SourcePath,
        DestinationName = destination.Name,
        DestinationType = destination.Type,
        DestinationRootPath = destination.RootPath,
        DestinationUsername = destination.SmbUsername,
        DestinationVerificationFingerprint = destination.VerificationFingerprint,
        DestinationSubfolder = job.DestinationSubfolder,
        ScheduledWeekdays = job.Weekdays,
        ScheduledTime = job.ScheduledTime,
        RetentionCount = job.RetentionCount,
        RegionalCulture = "",
        TimeZoneId = occurrence?.TimeZoneId ?? TimeZoneInfo.Local.Id,
        Trigger = trigger,
        DueAtUtc = dueAtUtc,
        QueuedAtUtc = queuedAtUtc
    };
}

public enum ManualRunEnqueueStatus
{
    Queued,
    Busy,
    Unavailable,
    OwnershipInvalid
}

public sealed record ManualRunEnqueueOutcome(ManualRunEnqueueStatus Status, Guid? RunId, string Message);

public enum RunCancellationStatus
{
    Requested,
    Cancelled,
    CommitStarted,
    AlreadyTerminal,
    NotFound
}

public sealed record RunCancellationOutcome(RunCancellationStatus Status, string Message);

public sealed class DurableCancellationRequestedException(Guid runId)
    : OperationCanceledException($"Cancellation was requested for run {runId} before final commit.");
