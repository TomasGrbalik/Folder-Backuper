namespace FolderBackuper.Infrastructure.Filesystem;

/// <summary>
/// What an ownership-marker operation concluded, as a code rather than a sentence.
/// </summary>
/// <remarks>
/// Member names are resource keys by the <c>OwnershipMessage_Member</c> rule. These messages reach the
/// interface through job and destination results and through the run details problems table, so they are
/// carried as codes for the same reason those are.
/// </remarks>
public enum OwnershipMessage
{
    Claimed,
    IncompleteMarkerReplaced,
    IncompleteMarkerNotRemoved,
    MarkerMissing,
    OwnedByThisJob,
    OwnedByAnotherJob,
    OwnedByAnotherInstallation,
    MarkerInvalid,
    Released,
    VerifiedMarkerNotRemoved
}
