using FolderBackuper.Features.Notifications;
using FolderBackuper.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderBackuper.Tests;


/// <summary>Records what it was asked to send and returns a scripted result.</summary>
internal sealed class FakeRunNotificationSender(NotificationSendResult result) : IRunNotificationSender
{
    public FakeRunNotificationSender()
        : this(new NotificationSendResult(NotificationSendStatus.Delivered, "Accepted."))
    {
    }

    public NotificationSendResult Result { get; set; } = result;

    public List<NotificationPayload> Sent { get; } = [];

    public int TestCount { get; private set; }

    public Func<NotificationPayload, Task>? OnSend { get; set; }

    public Task<NotificationSendResult> SendTestAsync(CancellationToken cancellationToken = default)
    {
        TestCount++;
        return Task.FromResult(Result);
    }

    public async Task<NotificationSendResult> SendRunResultAsync(
        NotificationPayload payload,
        CancellationToken cancellationToken = default)
    {
        Sent.Add(payload);
        if (OnSend is not null) await OnSend(payload);
        return Result;
    }
}

/// <summary>A sender that must never be called. Used to prove recovery does not retry.</summary>
internal sealed class ThrowingRunNotificationSender : IRunNotificationSender
{
    public Task<NotificationSendResult> SendTestAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The provider must not be contacted.");

    public Task<NotificationSendResult> SendRunResultAsync(
        NotificationPayload payload,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The provider must not be contacted.");
}

/// <summary>Reversible stand-in for DPAPI so protection can be asserted without machine state.</summary>
internal sealed class ReversibleTestProtector : ISecretProtector
{
    public byte[] Protect(string plaintext) =>
        System.Text.Encoding.UTF8.GetBytes(plaintext).Select(b => (byte)(b ^ 0x5A)).ToArray();

    public string Unprotect(byte[] protectedData) =>
        System.Text.Encoding.UTF8.GetString(protectedData.Select(b => (byte)(b ^ 0x5A)).ToArray());
}

internal static class NotificationTestFactory
{
    public const string ApiKey = "re_test_key_do_not_send";

    public static NotificationSettingsService Settings(TemporaryDatabase database, TimeProvider clock) =>
        new(database.ContextFactory, new ReversibleTestProtector(),
            new FolderBackuper.Features.Settings.InstallationIdentityService(database.ContextFactory, clock), clock);

    /// <summary>Saves a configuration that can actually be delivered.</summary>
    public static async Task<NotificationSettingsService> ConfiguredSettingsAsync(
        TemporaryDatabase database,
        TimeProvider clock,
        params string[] recipients)
    {
        var settings = Settings(database, clock);
        var result = await settings.SaveAsync(new SaveNotificationSettingsCommand(
            true, "backups@example.test", "Folder Backuper",
            string.Join('\n', recipients.Length == 0 ? ["operator@example.test"] : recipients), ApiKey));
        Assert.True(result.Succeeded, result.Message);
        return settings;
    }

    public static NotificationOutboxWriter Writer(NotificationSettingsService settings, TimeProvider clock) =>
        new(settings, clock, NullLogger<NotificationOutboxWriter>.Instance);

    public static NotificationOutboxService Outbox(
        TemporaryDatabase database,
        IRunNotificationSender sender,
        TimeProvider clock) =>
        new(database.ContextFactory, sender, clock, NullLogger<NotificationOutboxService>.Instance);

    public static ResendEmailClient Client(FakeHttpMessageHandler handler) =>
        new(new FakeHttpClientFactory(handler), NullLogger<ResendEmailClient>.Instance);
}
