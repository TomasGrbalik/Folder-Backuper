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
    long? AvailableBytes);

public sealed record SaveDestinationCommand(
    string Name,
    DestinationType Type,
    string RootPath,
    string? SmbUsername = null,
    string? Password = null);

public sealed record DestinationOperationResult(
    bool Succeeded,
    DestinationAccessResult Result,
    string Message,
    int? NativeErrorCode = null,
    long? AvailableBytes = null)
{
    public static DestinationOperationResult Success(string message, long? availableBytes = null) =>
        new(true, DestinationAccessResult.Succeeded, message, AvailableBytes: availableBytes);
}

public sealed record DestinationAccessConfiguration(
    DestinationType Type, string RootPath, string? Username, string? Password);
