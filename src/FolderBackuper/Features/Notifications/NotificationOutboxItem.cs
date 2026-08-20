using FolderBackuper.Features.Backups;

using FolderBackuper.Infrastructure.Localization;
namespace FolderBackuper.Features.Notifications;

public enum NotificationDeliveryState
{
    Pending,
    Sending,
    Delivered,
    Failed,
    DeliveryUnknown
}

public sealed class NotificationOutboxItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid RunId { get; init; }
    public BackupRun? Run { get; set; }
    public RunOutcome RunOutcome { get; init; }
    public required string PayloadSnapshot { get; init; }
    public int PayloadVersion { get; init; } = 1;
    public NotificationDeliveryState State { get; private set; } = NotificationDeliveryState.Pending;
    public int AttemptCount { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? SendingAtUtc { get; private set; }
    public DateTimeOffset? DeliveredAtUtc { get; private set; }
    public DateTimeOffset? FailedAtUtc { get; private set; }
    /// <summary>
    /// The message code of the last delivery problem, and its arguments. Stored as a code for the same
    /// reason a run problem is: the row outlives the language it was written in.
    /// </summary>
    public string? LastSafeErrorKey { get; private set; }

    public string? LastSafeErrorArguments { get; private set; }

    public void Claim(DateTimeOffset now)
    {
        RequireState(NotificationDeliveryState.Pending);
        State = NotificationDeliveryState.Sending;
        AttemptCount++;
        SendingAtUtc = now;
    }

    public void MarkDelivered(DateTimeOffset now)
    {
        RequireState(NotificationDeliveryState.Sending);
        State = NotificationDeliveryState.Delivered;
        DeliveredAtUtc = now;
    }

    public void MarkFailed(UiMessage safeError, DateTimeOffset now)
    {
        RequireState(NotificationDeliveryState.Sending);
        State = NotificationDeliveryState.Failed;
        LastSafeErrorKey = safeError.Key;
        LastSafeErrorArguments = StoredMessage.EncodeArguments(safeError);
        FailedAtUtc = now;
    }

    public void MarkDeliveryUnknown(UiMessage safeError, DateTimeOffset now)
    {
        RequireState(NotificationDeliveryState.Sending);
        State = NotificationDeliveryState.DeliveryUnknown;
        LastSafeErrorKey = safeError.Key;
        LastSafeErrorArguments = StoredMessage.EncodeArguments(safeError);
        FailedAtUtc = now;
    }

    private void RequireState(NotificationDeliveryState required)
    {
        if (State != required)
        {
            throw new InvalidOperationException($"Notification {Id} cannot transition from {State}; expected {required}.");
        }
    }
}
