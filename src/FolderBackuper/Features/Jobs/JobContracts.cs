namespace FolderBackuper.Features.Jobs;

public sealed record JobSummary(
    Guid Id,
    string Name,
    JobLifecycle Lifecycle,
    string SourcePath,
    Guid DestinationId,
    string DestinationSubfolder,
    ScheduledWeekdays Weekdays,
    TimeOnly ScheduledTime,
    long ScheduleRevision,
    DateTimeOffset ScheduleEffectiveFromUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record JobDetails(
    Guid Id,
    string Name,
    JobLifecycle Lifecycle,
    string SourcePath,
    Guid DestinationId,
    string DestinationName,
    string DestinationSubfolder,
    ScheduledWeekdays Weekdays,
    TimeOnly ScheduledTime,
    long ScheduleRevision,
    DateTimeOffset ScheduleEffectiveFromUtc,
    int RetentionCount,
    long ManagedArtifactCount,
    long ManagedArtifactBytes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record SaveJobCommand(
    string Name,
    string SourcePath,
    Guid DestinationId,
    string DestinationSubfolder,
    ScheduledWeekdays Weekdays,
    TimeOnly ScheduledTime,
    int RetentionCount,
    bool Activate = false,
    bool ConfirmDestinationPathChange = false);

public sealed record JobValidationError(string Field, string Message);

public enum JobOperationStatus
{
    Succeeded,
    ValidationFailed,
    NotFound,
    InvalidTransition,
    DestinationVerificationFailed,
    OwnershipFailed,
    Busy,
    Conflict,
    Failed
}

public sealed record JobOperationResult(
    JobOperationStatus Status,
    string Message,
    JobDetails? Job = null,
    IReadOnlyList<JobValidationError>? ValidationErrors = null)
{
    public bool Succeeded => Status == JobOperationStatus.Succeeded;

    public static JobOperationResult Validation(IReadOnlyList<JobValidationError> errors) =>
        new(JobOperationStatus.ValidationFailed, "The job configuration is invalid.", ValidationErrors: errors);
}
