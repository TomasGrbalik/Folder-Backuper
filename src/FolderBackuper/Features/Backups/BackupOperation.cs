namespace FolderBackuper.Features.Backups;

/// <summary>
/// The pipeline operation a run problem was recorded against, shown in the run details problems table.
/// </summary>
/// <remarks>
/// This was a free-text English string until Milestone 12, which made it untranslatable and let the same
/// operation be spelled two ways in two places. Member names are resource keys by the
/// <c>BackupOperation_Member</c> rule, and the value is what the permanent history stores, so renaming a
/// member is a persistence change as well as a translation one.
/// </remarks>
public enum BackupOperation
{
    LoadConfiguration,
    ValidateJobLifecycle,
    ValidateSource,
    ValidateDestinationVerification,
    ValidateSmbDestination,
    ValidateStaging,
    ValidateStagingOverlap,
    ValidateEffectiveDestination,
    VerifyDestinationOwnership,
    ReadSourceMetadata,
    EnumerateSourceDirectory,
    SkipReparsePoint,
    BuildSourceManifest,
    CompareSourceManifest,
    ReadSourceFile,
    CreateStagingArchive,
    ValidateZipArchive,
    CleanStagingArchive,
    TransferDestinationArchive,
    ValidateDestinationArchive,
    CleanDestinationPartial,
    RetentionOwnershipVerification,
    DeleteRetainedArchive,
    RecoverFinalArchive,
    RecoverInterruptedBackup,
    CleanInterruptedBackup,
    CancelBackup,
    ExecuteBackup,
    RecordDestinationAccess
}

/// <summary>
/// What went wrong during a run, as a code rather than a sentence.
/// </summary>
/// <remarks>
/// Member names are resource keys by the <c>BackupProblemMessage_Member</c> rule. The permanent history
/// stores the code and its arguments instead of finished text, so a run recorded while the interface was
/// English renders in Slovak once the language changes, and vice versa.
/// </remarks>
public enum BackupProblemMessage
{
    JobOrDestinationMissing,
    ArchivedJobCannotRun,
    DestinationNeedsVerification,
    SmbHostedLocallyUnsupported,
    StagingDirectoryMissing,
    StagingOverlapsSource,
    StagingNotResolvable,
    EffectiveDestinationMissing,
    OwnershipMarkerUnverified,
    ManifestPathInvalid,
    SourceEntryRemoved,
    SourceEntryChanged,
    SourceEntryAdded,
    ReparsePointSkipped,
    SourceFileChanged,
    SourceFileAccessDenied,
    SourceFileUnreadable,
    StagingInsufficientSpace,
    StagingArchiveNotCreated,
    ZipValidationFailed,
    OwnershipCommentInvalid,
    EntryCountMismatch,
    EntriesMismatch,
    StagingArchiveNotRemoved,
    PartialLengthMismatch,
    DestinationInsufficientSpace,
    DestinationAccessDenied,
    DestinationUnavailable,
    DestinationPathInvalid,
    DestinationOperationFailed,
    DestinationPartialNotRemoved,
    RetentionOwnershipUnproven,
    RetainedArchiveNotDeleted,
    FinalizationPendingUninspectable,
    ExecutionInterrupted,
    InterruptedPathLeftUntouched,
    Cancelled,
    UnexpectedFailure,
    DestinationAccessNotRecorded,
    DuplicateActiveWorkReconciled
}
