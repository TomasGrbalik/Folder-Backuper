namespace FolderBackuper.Features.Settings;

public sealed class ApplicationSettings
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid InstallationId { get; init; } = Guid.NewGuid();
    public string? NotificationProvider { get; set; }
    public string? NotificationProviderConfiguration { get; set; }
    public required string RecipientList { get; set; }
    public byte[]? ProtectedNotificationSecret { get; set; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
