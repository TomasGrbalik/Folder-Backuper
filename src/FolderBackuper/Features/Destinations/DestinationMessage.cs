using FolderBackuper.Infrastructure.Localization;

namespace FolderBackuper.Features.Destinations;

/// <summary>
/// What a destination operation or access test concluded, as a code rather than a sentence.
/// </summary>
/// <remarks>
/// Member names are resource keys by the <c>DestinationMessage_Member</c> rule. The members that take
/// arguments are documented where they are raised; a nested reason travels as a message argument rather
/// than as embedded text, so it is rendered in the reading language too.
/// </remarks>
public enum DestinationMessage
{
    NotFound,
    ActiveNotFound,
    AlreadyArchived,
    OnlyArchivedCanBeRestored,
    ReferencedByJobs,
    Archived,
    RestoredNeedsVerification,
    RootChangeNeedsConfirmation,
    NewFolderNotClaimed,
    FolderReservedByAnotherJob,
    OldMarkerNotReleased,
    NameOrFolderReserved,
    UpdatedAndVerificationInvalidated,
    Updated,
    OwnershipCompensationIncomplete,
    NameTooLong,
    SmbHostedLocallyMustBeLocalPath,
    SmbUsernameRequired,
    SmbPasswordRequired,
    OverlapsConfiguredSource,
    TestFileNotCleanedUp,
    TestBytesNotPreserved,
    TestSucceeded,
    AccessDenied,
    CredentialsRejected,
    PathInvalid,
    Unavailable,
    AccessTestFailed
}

/// <summary>
/// A destination configuration was rejected, carrying the reason as a message code.
/// </summary>
/// <remarks>
/// Exists so that the destination form can render a rejection in the reading language. It previously
/// displayed <c>Exception.Message</c> directly, which was untranslatable.
/// </remarks>
public sealed class DestinationValidationException(UiMessage reason, Exception? innerException = null)
    : ArgumentException(reason.Key, innerException)
{
    public UiMessage Reason { get; } = reason;
}
