using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Features.Notifications;

namespace FolderBackuper.Features.Backups;

public enum RunTrigger
{
    Scheduled,
    CatchUp,
    Manual
}

public enum RunPhase
{
    Planned,
    Queued,
    Scanning,
    Compressing,
    Transferring,
    Finalizing
}

public enum RunOutcome
{
    Successful,
    SuccessfulWithWarnings,
    Failed,
    Cancelled
}

public sealed class BackupRun
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid JobId { get; init; }
    public BackupJob? Job { get; set; }
    public required string JobName { get; init; }
    public required string SourcePath { get; init; }
    public required string DestinationName { get; init; }
    public DestinationType DestinationType { get; init; }
    public required string DestinationRootPath { get; init; }
    public required string DestinationSubfolder { get; init; }
    public ScheduledWeekdays ScheduledWeekdays { get; init; }
    public TimeOnly ScheduledTime { get; init; }
    public int RetentionCount { get; init; }
    public required string RegionalCulture { get; init; }
    public required string TimeZoneId { get; init; }
    public RunTrigger Trigger { get; init; }
    public DateTimeOffset QueuedAtUtc { get; init; }
    public DateTimeOffset? StartedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public RunPhase Phase { get; private set; } = RunPhase.Planned;
    public RunOutcome? Outcome { get; private set; }
    public DateTimeOffset? CancellationRequestedAtUtc { get; private set; }
    public DateTimeOffset? FinalCommitStartedAtUtc { get; private set; }
    public DateTimeOffset? FinalCommittedAtUtc { get; private set; }
    public long FileCount { get; set; }
    public long DirectoryCount { get; set; }
    public long SourceBytes { get; set; }
    public long ArchiveBytes { get; set; }
    public TimeSpan? CompressionDuration { get; set; }
    public TimeSpan? TransferDuration { get; set; }
    public string? ErrorSummary { get; set; }
    public NotificationDeliveryState? NotificationState { get; set; }
    public string? NotificationErrorSummary { get; set; }
    public ScheduledOccurrence? Occurrence { get; set; }
    public BackupArtifact? Artifact { get; set; }
    public NotificationOutboxItem? Notification { get; set; }
    public ICollection<RunProblem> Problems { get; } = [];

    public void AdvanceTo(RunPhase target, DateTimeOffset now)
    {
        EnsureNotTerminal();
        var expected = Phase switch
        {
            RunPhase.Planned => RunPhase.Queued,
            RunPhase.Queued => RunPhase.Scanning,
            RunPhase.Scanning => RunPhase.Compressing,
            RunPhase.Compressing => RunPhase.Transferring,
            RunPhase.Transferring => RunPhase.Finalizing,
            _ => throw InvalidTransition(target.ToString())
        };

        if (target != expected)
        {
            throw InvalidTransition(target.ToString());
        }

        Phase = target;
        if (target == RunPhase.Scanning)
        {
            StartedAtUtc = now;
        }
    }

    public void RequestCancellation(DateTimeOffset now)
    {
        EnsureNotTerminal();
        if (FinalCommitStartedAtUtc is not null)
        {
            throw new InvalidOperationException($"Run {Id} cannot be cancelled after final commit starts.");
        }

        CancellationRequestedAtUtc ??= now;
        if (Phase is RunPhase.Planned or RunPhase.Queued)
        {
            Complete(RunOutcome.Cancelled, now);
        }
    }

    public void BeginFinalCommit(DateTimeOffset now)
    {
        EnsureNotTerminal();
        if (Phase != RunPhase.Finalizing ||
            CancellationRequestedAtUtc is not null ||
            FinalCommitStartedAtUtc is not null)
        {
            throw InvalidTransition("final commit");
        }

        FinalCommitStartedAtUtc = now;
    }

    public void MarkFinalCommitted(DateTimeOffset now)
    {
        EnsureNotTerminal();
        if (FinalCommitStartedAtUtc is null || FinalCommittedAtUtc is not null)
        {
            throw InvalidTransition("committed");
        }

        FinalCommittedAtUtc = now;
    }

    public void Complete(RunOutcome outcome, DateTimeOffset now)
    {
        EnsureNotTerminal();
        if (outcome == RunOutcome.Cancelled && CancellationRequestedAtUtc is null)
        {
            throw InvalidTransition(outcome.ToString());
        }

        if (FinalCommittedAtUtc is not null && outcome is RunOutcome.Failed or RunOutcome.Cancelled)
        {
            throw InvalidTransition(outcome.ToString());
        }

        if (outcome is RunOutcome.Successful or RunOutcome.SuccessfulWithWarnings && FinalCommittedAtUtc is null)
        {
            throw InvalidTransition(outcome.ToString());
        }

        Outcome = outcome;
        CompletedAtUtc = now;
    }

    private void EnsureNotTerminal()
    {
        if (Outcome is not null)
        {
            throw new InvalidOperationException($"Run {Id} is already terminal with outcome {Outcome}.");
        }
    }

    private InvalidOperationException InvalidTransition(string target) =>
        new($"Run {Id} cannot transition from {Phase} to {target}.");
}

public sealed class ScheduledOccurrence
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid JobId { get; init; }
    public BackupJob? Job { get; set; }
    public long ScheduleRevision { get; init; }
    public DateOnly ScheduledLocalDate { get; init; }
    public TimeOnly ScheduledLocalTime { get; init; }
    public required string TimeZoneId { get; init; }
    public int UtcOffsetMinutes { get; init; }
    public Guid? RunId { get; set; }
    public BackupRun? Run { get; set; }
}

public sealed class RunProblem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid RunId { get; init; }
    public BackupRun? Run { get; set; }
    public string? Path { get; init; }
    public RunPhase Phase { get; init; }
    public required string Operation { get; init; }
    public required string ErrorCategory { get; init; }
    public string? NativeErrorCode { get; init; }
    public required string UserMessage { get; init; }
    public string? DiagnosticDetail { get; init; }
}
