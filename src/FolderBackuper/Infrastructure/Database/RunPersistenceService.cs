using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Infrastructure.Database;

public sealed class RunPersistenceService(
    IDbContextFactory<FolderBackuperDbContext> contextFactory,
    ConfigurationMutationGate mutationGate,
    TimeProvider timeProvider)
{
    public async Task<ManualRunEnqueueOutcome> EnqueueManualAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        return await mutationGate.ExecuteRunStateChangeAsync<ManualRunEnqueueOutcome>(async ct =>
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

            var now = timeProvider.GetUtcNow();
            var run = Snapshot(job, job.Destination, now);
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
    }

    public async Task<BackupRun?> ClaimNextAsync(CancellationToken cancellationToken = default)
    {
        return await mutationGate.ExecuteRunStateChangeAsync<BackupRun?>(async ct =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            // SQLite cannot translate DateTimeOffset ordering through EF, but its stored ISO values
            // preserve chronological order and the durable queue index covers this query.
            var run = await context.Runs.FromSqlRaw("""
                SELECT * FROM Runs
                WHERE Outcome IS NULL AND Phase = 'Queued' AND CancellationRequestedAtUtc IS NULL
                ORDER BY QueuedAtUtc, Id
                LIMIT 1
                """).SingleOrDefaultAsync(ct);
            if (run is null) return null;

            run.AdvanceTo(RunPhase.Scanning, timeProvider.GetUtcNow());
            await context.SaveChangesAsync(ct);
            return run;
        }, cancellationToken);
    }

    public async Task<RunCancellationOutcome> RequestCancellationAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        return await mutationGate.ExecuteRunStateChangeAsync<RunCancellationOutcome>(async ct =>
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
            foreach (var problem in result.Problems)
            {
                context.RunProblems.Add(new RunProblem
                {
                    RunId = run.Id,
                    Path = problem.Path,
                    Phase = problem.Phase,
                    Operation = problem.Operation,
                    ErrorCategory = problem.Category.ToString(),
                    NativeErrorCode = problem.NativeErrorCode?.ToString(),
                    UserMessage = problem.Message,
                    DiagnosticDetail = null
                });
            }

            await context.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
    }

    public async Task BeginFinalCommitAsync(BackupCommitIntent intent, CancellationToken cancellationToken = default)
    {
        await mutationGate.ExecuteRunStateChangeAsync(async ct =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var run = await context.Runs.SingleAsync(item => item.Id == intent.RunId, ct);
            run.DestinationPartialPath = intent.PartialPath;
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
                OwnershipCreatedAtUtc = intent.CreatedAtUtc,
                OwnershipFileSystemIdentity = intent.FileSystemIdentity
            });
            await context.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
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

    public async Task CompleteAsync(
        Guid runId,
        RunOutcome outcome,
        string? errorSummary,
        CancellationToken cancellationToken = default)
    {
        await ChangeRunAsync(runId, run =>
        {
            run.ErrorSummary = errorSummary;
            run.Complete(outcome, timeProvider.GetUtcNow());
        }, cancellationToken);
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
    }

    private static BackupRun Snapshot(BackupJob job, Destination destination, DateTimeOffset now) => new()
    {
        JobId = job.Id,
        DestinationId = destination.Id,
        JobName = job.Name,
        SourcePath = job.SourcePath,
        DestinationName = destination.Name,
        DestinationType = destination.Type,
        DestinationRootPath = destination.RootPath,
        DestinationSubfolder = job.DestinationSubfolder,
        ScheduledWeekdays = job.Weekdays,
        ScheduledTime = job.ScheduledTime,
        RetentionCount = job.RetentionCount,
        RegionalCulture = "",
        TimeZoneId = TimeZoneInfo.Local.Id,
        Trigger = RunTrigger.Manual,
        QueuedAtUtc = now
    };
}

public enum ManualRunEnqueueStatus
{
    Queued,
    Busy,
    Unavailable
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
