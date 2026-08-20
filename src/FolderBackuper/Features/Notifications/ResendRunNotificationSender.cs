using System.Net.Mail;

namespace FolderBackuper.Features.Notifications;

/// <summary>
/// The Resend implementation of the notification boundary. Composes saved settings, the
/// provider-neutral template, and the typed HTTP client into exactly one provider call.
/// </summary>
public sealed class ResendRunNotificationSender(
    NotificationSettingsService settings,
    ResendEmailClient client) : IRunNotificationSender
{
    public async Task<NotificationSendResult> SendTestAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await settings.GetDeliveryConfigurationAsync(cancellationToken);
        if (configuration is null) return NotConfigured;

        var message = NotificationTemplates.Test(configuration.Recipients);
        return await SendAsync(configuration, message, cancellationToken);
    }

    public async Task<NotificationSendResult> SendRunResultAsync(
        NotificationPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var configuration = await settings.GetDeliveryConfigurationAsync(cancellationToken);
        if (configuration is null) return NotConfigured;

        var message = NotificationTemplates.RunResult(payload);
        return await SendAsync(configuration, message, cancellationToken);
    }

    private Task<NotificationSendResult> SendAsync(
        NotificationDeliveryConfiguration configuration,
        NotificationMessage message,
        CancellationToken cancellationToken) =>
        client.SendAsync(
            new ResendMessage(
                Sender(configuration),
                configuration.Recipients,
                message.Subject,
                message.Html,
                message.Text),
            configuration.ApiKey,
            cancellationToken);

    private static string Sender(NotificationDeliveryConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.FromName)) return configuration.FromAddress;

        // MailAddress produces a correctly quoted and encoded display-name form, so a sender name
        // containing a comma or a quotation mark cannot corrupt the header.
        try
        {
            return new MailAddress(configuration.FromAddress, configuration.FromName).ToString();
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return configuration.FromAddress;
        }
    }

    private static NotificationSendResult NotConfigured => new(
        NotificationSendStatus.NotConfigured,
        NotificationResultMessage.NotConfigured);
}
