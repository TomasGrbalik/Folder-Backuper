using FolderBackuper.Infrastructure.Localization;

namespace FolderBackuper.Features.Notifications;

/// <summary>
/// Outcome of a single delivery attempt, classified by what the provider told us.
/// </summary>
public enum NotificationSendStatus
{
    /// <summary>The provider accepted the message.</summary>
    Delivered,

    /// <summary>The provider refused it, or it never left this machine. Nothing was sent.</summary>
    Rejected,

    /// <summary>The attempt started but its result is unknowable. It is never repeated.</summary>
    Uncertain,

    /// <summary>No usable notification configuration is saved.</summary>
    NotConfigured
}

/// <summary>
/// The result of one delivery attempt. <see cref="Message"/> is always safe to persist, log, and
/// display: it never contains the API key or any other secret.
/// </summary>
public sealed record NotificationSendResult(NotificationSendStatus Status, UiMessage Message)
{
    public bool Succeeded => Status == NotificationSendStatus.Delivered;

    public NotificationSendResult(NotificationSendStatus status, NotificationResultMessage message)
        : this(status, UiMessage.For(message))
    {
    }
}

/// <summary>
/// The boundary backup execution depends on instead of any provider type. Implementations make at
/// most one provider call per invocation and never throw for delivery problems; a failure is a
/// returned <see cref="NotificationSendResult"/> so it cannot alter a backup outcome.
/// </summary>
public interface IRunNotificationSender
{
    Task<NotificationSendResult> SendTestAsync(CancellationToken cancellationToken = default);

    Task<NotificationSendResult> SendRunResultAsync(
        NotificationPayload payload,
        CancellationToken cancellationToken = default);
}
