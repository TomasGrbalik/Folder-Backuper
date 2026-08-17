using FolderBackuper.Features.Backups;

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
    public string? LastSafeError { get; private set; }

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

    public void MarkFailed(string safeError, DateTimeOffset now)
    {
        RequireState(NotificationDeliveryState.Sending);
        State = NotificationDeliveryState.Failed;
        LastSafeError = safeError;
        FailedAtUtc = now;
    }

    public void MarkDeliveryUnknown(string safeError, DateTimeOffset now)
    {
        RequireState(NotificationDeliveryState.Sending);
        State = NotificationDeliveryState.DeliveryUnknown;
        LastSafeError = safeError;
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
