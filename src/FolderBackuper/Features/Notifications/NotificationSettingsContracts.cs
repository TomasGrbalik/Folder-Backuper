namespace FolderBackuper.Features.Notifications;

/// <summary>The only provider this build implements. Persisted so a future provider can be told apart.</summary>
public static class NotificationProviders
{
    public const string Resend = "Resend";
}

/// <summary>
/// Notification settings as shown in the UI. The saved API key is never a member here, so no render
/// path can display it; <see cref="HasApiKey"/> reports only whether one is stored.
/// </summary>
public sealed record NotificationSettingsView(
    bool Enabled,
    string? FromAddress,
    string? FromName,
    IReadOnlyList<string> Recipients,
    bool HasApiKey,
    DateTimeOffset? UpdatedAtUtc)
{
    /// <summary>True when a delivery attempt could actually be made with what is saved.</summary>
    public bool IsDeliverable => Enabled && HasApiKey
        && !string.IsNullOrWhiteSpace(FromAddress) && Recipients.Count > 0;
}

/// <summary>
/// A settings save. <see cref="ApiKey"/> left null or blank keeps the stored key, mirroring the
/// destination password convention: a saved secret is never displayed and never round-tripped.
/// </summary>
public sealed record SaveNotificationSettingsCommand(
    bool Enabled,
    string? FromAddress,
    string? FromName,
    string? Recipients,
    string? ApiKey = null);

public enum NotificationSettingsStatus
{
    Succeeded,
    ValidationFailed,
    Failed
}

public sealed record NotificationSettingsResult(
    NotificationSettingsStatus Status,
    string Message,
    IReadOnlyDictionary<string, string>? FieldErrors = null)
{
    public bool Succeeded => Status == NotificationSettingsStatus.Succeeded;

    public static NotificationSettingsResult Success(string message) =>
        new(NotificationSettingsStatus.Succeeded, message);

    public static NotificationSettingsResult Invalid(
        string message,
        IReadOnlyDictionary<string, string>? fieldErrors = null) =>
        new(NotificationSettingsStatus.ValidationFailed, message, fieldErrors);
}

/// <summary>
/// Everything a provider needs for one delivery attempt, including the unprotected key. Never
/// leaves the notification feature and is never returned to the UI or written to a log.
/// </summary>
public sealed record NotificationDeliveryConfiguration(
    string ApiKey,
    string FromAddress,
    string? FromName,
    IReadOnlyList<string> Recipients);
