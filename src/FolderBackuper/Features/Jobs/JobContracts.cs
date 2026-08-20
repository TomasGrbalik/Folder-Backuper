using FolderBackuper.Infrastructure.Localization;
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

public sealed record JobValidationError(string Field, UiMessage Message)
{
    public JobValidationError(string field, JobValidationMessage message)
        : this(field, UiMessage.For(message))
    {
    }
}

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
    UiMessage Message,
    JobDetails? Job = null,
    IReadOnlyList<JobValidationError>? ValidationErrors = null)
{
    public bool Succeeded => Status == JobOperationStatus.Succeeded;

    public JobOperationResult(JobOperationStatus status, JobMessage message, JobDetails? job = null)
        : this(status, UiMessage.For(message), job)
    {
    }

    public static JobOperationResult Validation(IReadOnlyList<JobValidationError> errors) =>
        new(JobOperationStatus.ValidationFailed, UiMessage.For(JobMessage.ConfigurationInvalid), ValidationErrors: errors);
}
