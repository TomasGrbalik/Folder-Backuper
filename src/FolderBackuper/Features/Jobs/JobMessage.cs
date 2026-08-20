namespace FolderBackuper.Features.Jobs;

/// <summary>
/// The outcome of a job operation, as a code rather than a sentence.
/// </summary>
/// <remarks>Member names are resource keys by the <c>JobMessage_Member</c> rule.</remarks>
public enum JobMessage
{
    Created,
    Updated,
    NotFound,
    JobOrDestinationNotFound,
    NameOrFolderReserved,
    RestoredNameOrFolderReserved,
    EffectiveFolderReserved,
    OnlyPausedCanBeReactivated,
    OnlyActiveCanBePaused,
    AlreadyArchived,
    OnlyArchivedCanBeRestored,
    OldMarkerNotReleased,
    MarkerNotReleasedNotArchived,
    NowActive,
    NowPaused,
    NowArchived,
    RestoredAndActivated,
    RestoredPaused,
    VerifyDestinationRootFirst,
    DestinationNeedsManagementVerification,
    ConfigurationInvalid,
    EffectiveFolderTestFailed
}

/// <summary>
/// Why a job field was rejected, as a code rather than a sentence.
/// </summary>
/// <remarks>Member names are resource keys by the <c>JobValidationMessage_Member</c> rule.</remarks>
public enum JobValidationMessage
{
    NameRequired,
    NameAlreadyExists,
    WeekdayRequired,
    RetentionAtLeastOne,
    ActiveDestinationRequired,
    SourceMustBeReadableLocalFolder,
    SourceCannotBeReparsePoint,
    ConfirmDestinationPathChange
}
