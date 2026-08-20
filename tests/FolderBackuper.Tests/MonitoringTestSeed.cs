using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;

namespace FolderBackuper.Tests;

/// <summary>Builds durable runs, artifacts, and problems for monitoring read-side tests.</summary>
internal static class MonitoringTestSeed
{
    public static BackupRun NewRun(BackupJob job, Destination destination, RunTrigger trigger, DateTimeOffset queuedAtUtc) => new()
    {
        JobId = job.Id,
        DestinationId = destination.Id,
        JobName = job.Name,
        SourcePath = job.SourcePath,
        DestinationName = destination.Name,
        DestinationType = destination.Type,
        DestinationRootPath = destination.RootPath,
        DestinationUsername = destination.SmbUsername,
        DestinationSubfolder = job.DestinationSubfolder,
        ScheduledWeekdays = job.Weekdays,
        ScheduledTime = job.ScheduledTime,
        RetentionCount = job.RetentionCount,
        RegionalCulture = "",
        TimeZoneId = TimeZoneInfo.Utc.Id,
        Trigger = trigger,
        DueAtUtc = queuedAtUtc,
        QueuedAtUtc = queuedAtUtc
    };

    public static BackupRun Terminal(
        BackupJob job,
        Destination destination,
        RunOutcome outcome,
        DateTimeOffset startedAtUtc,
        RunTrigger trigger = RunTrigger.Manual)
    {
        var run = NewRun(job, destination, trigger, startedAtUtc);
        var completed = startedAtUtc.AddMinutes(5);

        if (outcome == RunOutcome.Cancelled)
        {
            run.AdvanceTo(RunPhase.Queued, startedAtUtc);
            run.RequestCancellation(completed);
            return run;
        }

        run.AdvanceTo(RunPhase.Queued, startedAtUtc);
        run.AdvanceTo(RunPhase.Scanning, startedAtUtc);

        if (outcome == RunOutcome.Failed)
        {
            run.Complete(RunOutcome.Failed, completed);
            run.ErrorMessageKey = UiMessage.KeyFor(BackupProblemMessage.UnexpectedFailure);
            return run;
        }

        run.AdvanceTo(RunPhase.Compressing, startedAtUtc);
        run.AdvanceTo(RunPhase.Transferring, startedAtUtc);
        run.AdvanceTo(RunPhase.Finalizing, startedAtUtc);
        run.BeginFinalCommit(startedAtUtc);
        run.MarkFinalCommitted(startedAtUtc);
        run.Complete(outcome, completed);
        run.ArchiveBytes = 2048;
        return run;
    }

    public static BackupRun Running(BackupJob job, Destination destination, RunPhase phase, DateTimeOffset startedAtUtc)
    {
        var run = NewRun(job, destination, RunTrigger.Manual, startedAtUtc);
        run.AdvanceTo(RunPhase.Queued, startedAtUtc);
        for (var next = RunPhase.Scanning; next <= phase; next++)
        {
            run.AdvanceTo(next, startedAtUtc);
        }

        return run;
    }

    public static BackupArtifact Artifact(
        BackupRun run,
        Destination destination,
        long size,
        DateTimeOffset createdAtUtc,
        ArtifactState target = ArtifactState.Retained,
        string? effectivePath = null)
    {
        var artifact = new BackupArtifact
        {
            RunId = run.Id,
            DestinationName = destination.Name,
            DestinationRootPath = destination.RootPath,
            EffectivePath = effectivePath ?? System.IO.Path.Combine(destination.RootPath, run.DestinationSubfolder),
            FinalFileName = $"{run.JobName}_{run.Id:N}.zip",
            Size = size,
            CreatedAtUtc = createdAtUtc,
            OwnershipRunId = run.Id,
            OwnershipExpectedLength = size
        };

        artifact.MarkRetained(createdAtUtc);
        switch (target)
        {
            case ArtifactState.Retained:
                break;
            case ArtifactState.FoundMissing:
                artifact.MarkMissing(createdAtUtc);
                break;
            case ArtifactState.Unmanaged:
                artifact.MarkUnmanaged(createdAtUtc);
                break;
            case ArtifactState.RemovedByRetention:
                artifact.BeginRetentionDeletion(createdAtUtc);
                artifact.MarkRemovedByRetention(createdAtUtc);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target));
        }

        return artifact;
    }

    public static RunProblem Problem(
        Guid runId,
        BackupProblemSeverity severity,
        BackupProblemMessage message,
        string? path = null) =>
        Problem(runId, severity, UiMessage.For(message), path);

    /// <summary>
    /// Seeds a problem from an already-built message, for tests that need a specific argument in it.
    /// </summary>
    public static RunProblem Problem(
        Guid runId,
        BackupProblemSeverity severity,
        UiMessage message,
        string? path = null) => new()
    {
        RunId = runId,
        Path = path,
        Phase = RunPhase.Compressing,
        Severity = severity,
        Operation = BackupOperation.ReadSourceFile,
        ErrorCategory = BackupProblemCategory.SourceInaccessible.ToString(),
        MessageKey = message.Key,
        MessageArguments = StoredMessage.EncodeArguments(message)
    };
}
