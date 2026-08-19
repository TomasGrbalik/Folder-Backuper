namespace FolderBackuper.Features.Notifications;

public static class NotificationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the notification feature. Resend is the only provider; the architecture has no
    /// runtime provider switching, so the boundary is bound to one implementation here.
    /// </summary>
    public static IServiceCollection AddNotifications(this IServiceCollection services)
    {
        services.AddSingleton<NotificationSettingsService>();
        services.AddSingleton<NotificationOutboxWriter>();
        services.AddSingleton<NotificationOutboxService>();
        services.AddSingleton<NotificationOutboxSignal>();
        services.AddSingleton<IRunNotificationSender, ResendRunNotificationSender>();
        services.AddHostedService<NotificationOutboxWorker>();

        services.AddSingleton<ResendEmailClient>();
        services.AddHttpClient(ResendEmailClient.ClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.resend.com/");
            // Bounded so a hung provider cannot keep an attempt open indefinitely. A timeout is
            // classified as delivery-unknown, never as a clean failure.
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        return services;
    }
}
