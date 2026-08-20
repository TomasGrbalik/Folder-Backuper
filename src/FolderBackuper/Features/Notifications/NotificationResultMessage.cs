namespace FolderBackuper.Features.Notifications;

/// <summary>
/// What a delivery attempt or a settings save concluded, as a code rather than a sentence.
/// </summary>
/// <remarks>
/// Member names are resource keys by the <c>NotificationResultMessage_Member</c> rule. Delivery results are
/// persisted on the outbox row and mirrored onto the run, so they are carried as codes for the same
/// reason run problems are: the record outlives the language it was produced in.
/// </remarks>
public enum NotificationResultMessage
{
    NotConfigured,
    InterruptedMidAttempt,
    ProviderTimedOut,
    ConnectionLost,
    Accepted,
    AcceptedWithId,
    ProviderServerError,
    ApiKeyRejected,
    Throttled,
    SenderDomainUnverified,
    RequestRejected,
    ProviderUnreachable,
    RequestFailedAfterStarting,
    SettingsSaved,
    SettingsSavedNotificationsOff,
    SettingsInvalid,
    SenderAddressRequired,
    SenderAddressInvalid,
    RecipientRequired,
    ApiKeyRequired,
    TooManyRecipients,
    RecipientAddressInvalid,
    SenderNameTooLong,
    TestCouldNotBeCompleted,
    StoredContentUnreadable,
    AttemptFailedUnexpectedly,
    ReasonWithProviderDetail
}
