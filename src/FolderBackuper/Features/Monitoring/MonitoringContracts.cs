using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Features.Notifications;

namespace FolderBackuper.Features.Monitoring;

/// <summary>Durable projection of the single non-terminal run. Live progress is joined from the registry.</summary>
public sealed record ActiveRunView(
    Guid RunId,
    Guid JobId,
    string JobName,
    string SourcePath,
    string DestinationName,
    DestinationType DestinationType,
    RunPhase Phase,
    RunTrigger Trigger,
    DateTimeOffset? StartedAtUtc,
    bool CancellationRequested);

public sealed record QueuedRunView(
    Guid RunId,
    Guid JobId,
    string JobName,
    RunTrigger Trigger,
    DateTimeOffset DueAtUtc,
    DateTimeOffset QueuedAtUtc);

public enum RunStatusFilter
{
    All,
    Successful,
    Warnings,
    Failed,
    Cancelled
}

public sealed record RunHistoryFilter(Guid? JobId = null, RunStatusFilter Status = RunStatusFilter.All);

public sealed record RunHistoryRow(
    Guid RunId,
    Guid JobId,
    string JobName,
    RunTrigger Trigger,
    RunPhase Phase,
    RunOutcome? Outcome,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    TimeSpan? Duration,
    long ArchiveBytes,
    ArtifactState? ArtifactState,
    int ProblemCount);

public sealed record RunHistoryPage(IReadOnlyList<RunHistoryRow> Rows, int TotalCount, int Page, int PageSize);

public sealed record RunDetailsView(
    Guid RunId,
    Guid JobId,
    string JobName,
    string SourcePath,
    string DestinationName,
    DestinationType DestinationType,
    string DestinationRootPath,
    string DestinationSubfolder,
    ScheduledWeekdays ScheduledWeekdays,
    TimeOnly ScheduledTime,
    int RetentionCount,
    string TimeZoneId,
    RunTrigger Trigger,
    RunPhase Phase,
    RunOutcome? Outcome,
    DateTimeOffset DueAtUtc,
    DateTimeOffset QueuedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    TimeSpan? Duration,
    long FileCount,
    long DirectoryCount,
    long SourceBytes,
    long ArchiveBytes,
    TimeSpan? CompressionDuration,
    TimeSpan? TransferDuration,
    string? ErrorSummary,
    string? ArchiveFinalFileName,
    string? ArchiveEffectivePath,
    long? ArchiveSize,
    ArtifactState? ArtifactState,
    NotificationDeliveryState? NotificationState,
    string? NotificationErrorSummary,
    int ProblemCount);

public sealed record RunProblemRow(
    Guid Id,
    string? Path,
    RunPhase Phase,
    BackupProblemSeverity Severity,
    string Operation,
    string ErrorCategory,
    string? NativeErrorCode,
    string UserMessage,
    string? DiagnosticDetail);

public sealed record RunProblemPage(IReadOnlyList<RunProblemRow> Rows, int TotalCount, int Page, int PageSize);

/// <summary>Per-job health and storage summary shown on the dashboard. Storage totals cover retained artifacts only.</summary>
public sealed record JobStatusCard(
    Guid JobId,
    string JobName,
    JobLifecycle Lifecycle,
    RunOutcome? LastOutcome,
    DateTimeOffset? LastRunAtUtc,
    DateTimeOffset? LastSuccessAtUtc,
    DateTimeOffset? NextRunAtUtc,
    long ManagedArtifactCount,
    long ManagedArtifactBytes,
    long? LatestArtifactBytes,
    int RetentionCount,
    DateTimeOffset? StorageConfirmedAtUtc,
    bool StorageStale,
    int MissingArtifactCount,
    int UnmanagedArtifactCount,
    NotificationDeliveryState? LastNotificationState);

public sealed record DashboardView(
    ActiveRunView? ActiveRun,
    IReadOnlyList<QueuedRunView> Queue,
    IReadOnlyList<JobStatusCard> Jobs,
    int FailureCount,
    int WarningCount,
    int NotificationFailureCount);

/// <summary>A materialized (past) or planned (future) entry for the calendar and agenda views.</summary>
public sealed record CalendarEntry(
    Guid? RunId,
    Guid JobId,
    string JobName,
    DateOnly LocalDate,
    TimeOnly LocalTime,
    DateTimeOffset OccursAtUtc,
    bool IsPlanned,
    RunTrigger? Trigger,
    RunPhase Phase,
    RunOutcome? Outcome);
