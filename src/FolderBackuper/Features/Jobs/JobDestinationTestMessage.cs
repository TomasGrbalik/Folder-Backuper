namespace FolderBackuper.Features.Jobs;

/// <summary>
/// What testing and claiming a job's effective destination folder concluded.
/// </summary>
/// <remarks>
/// Member names are resource keys by the <c>JobDestinationTestMessage_Member</c> rule. The two cleanup
/// members take the underlying ownership message as an argument rather than embedding its text, so a
/// nested reason is still rendered in the reading language.
/// </remarks>
public enum JobDestinationTestMessage
{
    OwnershipAndWriteVerified,
    VerificationBytesNotPreserved,
    VerificationFileCleanupFailed,
    WriteVerificationFailed,
    DestinationNotAccessible,
    OwnedFolderNotVerifiableForCleanup,
    OwnedFolderNotAccessibleForCleanup,
    ExactVerificationFileNotRemoved,
    NewlyClaimedMarkerNotReleased,
    CleanupFailedAndMarkerNotReleased
}
