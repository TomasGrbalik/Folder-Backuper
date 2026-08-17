namespace FolderBackuper.Features.Destinations;

public sealed record DestinationSummary(
    Guid Id,
    string Name,
    DestinationType Type,
    string RootPath,
    string? SmbUsername,
    bool HasPassword,
    DestinationVerificationResult VerificationResult,
    DateTimeOffset? VerifiedAtUtc,
    DestinationAccessResult LastAccessResult,
    DateTimeOffset? LastAccessedAtUtc,
    long? AvailableBytes,
    DestinationLifecycle Lifecycle = DestinationLifecycle.Active);

public sealed record SaveDestinationCommand(
    string Name,
    DestinationType Type,
    string RootPath,
    string? SmbUsername = null,
    string? Password = null,
    bool ConfirmRootPathChange = false);

public enum DestinationOperationStatus
{
    Succeeded,
    ValidationFailed,
    NotFound,
    InvalidTransition,
    Referenced,
    Busy,
    Conflict,
    OwnershipFailed,
    Failed
}

public sealed record DestinationOperationResult(
    bool Succeeded,
    DestinationAccessResult Result,
    string Message,
    int? NativeErrorCode = null,
    long? AvailableBytes = null,
    DestinationOperationStatus? OperationStatus = null,
    DestinationSummary? Destination = null,
    int PausedJobCount = 0,
    int UnmanagedArtifactCount = 0)
{
    public DestinationOperationStatus Status => OperationStatus ??
        (Succeeded ? DestinationOperationStatus.Succeeded : DestinationOperationStatus.Failed);

    public static DestinationOperationResult Success(string message, long? availableBytes = null) =>
        new(true, DestinationAccessResult.Succeeded, message, AvailableBytes: availableBytes);

    public static DestinationOperationResult Completed(
        string message,
        DestinationSummary? destination = null,
        int pausedJobCount = 0,
        int unmanagedArtifactCount = 0) =>
        new(true, DestinationAccessResult.NotAttempted, message,
            OperationStatus: DestinationOperationStatus.Succeeded,
            Destination: destination,
            PausedJobCount: pausedJobCount,
            UnmanagedArtifactCount: unmanagedArtifactCount);

    public static DestinationOperationResult Failure(DestinationOperationStatus status, string message) =>
        new(false, DestinationAccessResult.NotAttempted, message, OperationStatus: status);
}

public sealed record DestinationAccessConfiguration(
    DestinationType Type, string RootPath, string? Username, string? Password);
