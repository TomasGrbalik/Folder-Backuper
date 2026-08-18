using FolderBackuper.Features.Destinations;

namespace FolderBackuper.Features.Jobs;

[Flags]
public enum ScheduledWeekdays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64
}

public enum JobLifecycle
{
    Active,
    Paused,
    Archived
}

public sealed class BackupJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string SourcePath { get; set; }
    public Guid DestinationId { get; set; }
    public Destination? Destination { get; set; }
    public required string DestinationSubfolder { get; set; }
    public ScheduledWeekdays Weekdays { get; set; }
    public TimeOnly ScheduledTime { get; set; }
    public long ScheduleRevision { get; set; } = 1;
    public DateTimeOffset ScheduleEffectiveFromUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ScheduleEvaluatedThroughUtc { get; set; }
    public long? LastSatisfiedScheduleRevision { get; set; }
    public DateOnly? LastSatisfiedScheduledLocalDate { get; set; }
    public DateTimeOffset? NextOccurrenceAtUtc { get; set; }
    public DateOnly? NextOccurrenceLocalDate { get; set; }
    public string? NextOccurrenceTimeZoneId { get; set; }
    public int? NextOccurrenceUtcOffsetMinutes { get; set; }
    public int RetentionCount { get; set; } = 1;
    public JobLifecycle Lifecycle { get; private set; } = JobLifecycle.Paused;
    public required string DestinationOwnershipKey { get; set; }
    public long ManagedArtifactCount { get; set; }
    public long ManagedArtifactBytes { get; set; }
    public long? LatestArtifactBytes { get; set; }
    public DateTimeOffset? StorageConfirmedAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public void Activate() => Lifecycle = Lifecycle switch
    {
        JobLifecycle.Paused => JobLifecycle.Active,
        _ => throw InvalidTransition(nameof(JobLifecycle.Active))
    };

    public void Pause() => Lifecycle = Lifecycle switch
    {
        JobLifecycle.Active => JobLifecycle.Paused,
        _ => throw InvalidTransition(nameof(JobLifecycle.Paused))
    };

    public void Archive() => Lifecycle = Lifecycle switch
    {
        JobLifecycle.Active or JobLifecycle.Paused => JobLifecycle.Archived,
        _ => throw InvalidTransition(nameof(JobLifecycle.Archived))
    };

    public void Restore() => Lifecycle = Lifecycle switch
    {
        JobLifecycle.Archived => JobLifecycle.Paused,
        _ => throw InvalidTransition(nameof(JobLifecycle.Paused))
    };

    public void BeginScheduling(DateTimeOffset boundaryUtc, bool resetSatisfied = false)
    {
        ScheduleEvaluatedThroughUtc = boundaryUtc;
        ClearNextOccurrence();
        if (resetSatisfied)
        {
            LastSatisfiedScheduleRevision = null;
            LastSatisfiedScheduledLocalDate = null;
        }
    }

    public void StopScheduling()
    {
        ScheduleEvaluatedThroughUtc = null;
        ClearNextOccurrence();
    }

    private void ClearNextOccurrence()
    {
        NextOccurrenceAtUtc = null;
        NextOccurrenceLocalDate = null;
        NextOccurrenceTimeZoneId = null;
        NextOccurrenceUtcOffsetMinutes = null;
    }

    private InvalidOperationException InvalidTransition(string target) =>
        new($"Job {Id} cannot transition from {Lifecycle} to {target}.");
}
