using FolderBackuper.Features.Notifications;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Tests;

public sealed class NotificationSettingsServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);

    private static SaveNotificationSettingsCommand Valid(
        bool enabled = true,
        string? apiKey = "re_key_value",
        string? recipients = "operator@example.test") =>
        new(enabled, "backups@example.test", "Folder Backuper", recipients, apiKey);

    [Fact]
    public async Task Save_RoundTripsTheConfigurationWithoutReturningTheKey()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var service = NotificationTestFactory.Settings(database, clock);

        var saved = await service.SaveAsync(Valid(recipients: "one@example.test\ntwo@example.test"));
        Assert.True(saved.Succeeded, MessageAssert.Text(saved.Message));

        var view = await service.GetAsync();

        Assert.True(view.Enabled);
        Assert.Equal("backups@example.test", view.FromAddress);
        Assert.Equal("Folder Backuper", view.FromName);
        Assert.Equal(["one@example.test", "two@example.test"], view.Recipients);
        Assert.True(view.HasApiKey);
        Assert.True(view.IsDeliverable);
        Assert.Equal(Now, view.UpdatedAtUtc);

        // The key is not a member of the view at all, so no render path can display it.
        Assert.DoesNotContain(typeof(NotificationSettingsView).GetProperties(), property =>
            property.PropertyType == typeof(byte[])
            || property.Name.Contains("Key", StringComparison.OrdinalIgnoreCase)
                && property.PropertyType == typeof(string));
    }

    [Fact]
    public async Task Save_ProtectsTheApiKeyRatherThanStoringIt()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();

        await NotificationTestFactory.Settings(database, clock).SaveAsync(Valid(apiKey: "re_plaintext_key"));

        await using var context = await database.ContextFactory.CreateDbContextAsync();
        var stored = await context.ApplicationSettings.AsNoTracking().SingleAsync();

        Assert.NotNull(stored.ProtectedNotificationSecret);
        Assert.Equal(NotificationProviders.Resend, stored.NotificationProvider);
        var raw = System.Text.Encoding.UTF8.GetString(stored.ProtectedNotificationSecret!);
        Assert.DoesNotContain("re_plaintext_key", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_KeepsTheStoredKeyWhenTheFieldIsLeftBlank()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var service = NotificationTestFactory.Settings(database, clock);

        await service.SaveAsync(Valid(apiKey: "re_original_key"));
        var saved = await service.SaveAsync(Valid(apiKey: "   ", recipients: "changed@example.test"));

        Assert.True(saved.Succeeded, MessageAssert.Text(saved.Message));
        var configuration = await service.GetDeliveryConfigurationAsync();

        Assert.NotNull(configuration);
        Assert.Equal("re_original_key", configuration!.ApiKey);
        Assert.Equal(["changed@example.test"], configuration.Recipients);
    }

    [Fact]
    public async Task Save_ReplacesTheStoredKeyWhenOneIsProvided()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var service = NotificationTestFactory.Settings(database, clock);

        await service.SaveAsync(Valid(apiKey: "re_original_key"));
        await service.SaveAsync(Valid(apiKey: "re_replacement_key"));

        var configuration = await service.GetDeliveryConfigurationAsync();
        Assert.Equal("re_replacement_key", configuration!.ApiKey);
    }

    [Fact]
    public async Task Save_RequiresAKeySenderAndRecipientWhenNotificationsAreTurnedOn()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var service = NotificationTestFactory.Settings(database, clock);

        var result = await service.SaveAsync(new SaveNotificationSettingsCommand(true, "", null, "", null));

        Assert.Equal(NotificationSettingsStatus.ValidationFailed, result.Status);
        Assert.NotNull(result.FieldErrors);
        Assert.Contains(nameof(SaveNotificationSettingsCommand.FromAddress), result.FieldErrors!.Keys);
        Assert.Contains(nameof(SaveNotificationSettingsCommand.Recipients), result.FieldErrors.Keys);
        Assert.Contains(nameof(SaveNotificationSettingsCommand.ApiKey), result.FieldErrors.Keys);
    }

    [Fact]
    public async Task Save_AllowsAnIncompleteConfigurationToBeTurnedOff()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var service = NotificationTestFactory.Settings(database, clock);

        // Turning notifications off must stay possible even when nothing valid is configured.
        var result = await service.SaveAsync(new SaveNotificationSettingsCommand(false, "", null, "", null));

        Assert.True(result.Succeeded, MessageAssert.Text(result.Message));
        MessageAssert.Is(NotificationResultMessage.SettingsSavedNotificationsOff, result.Message);
        var view = await service.GetAsync();
        Assert.False(view.Enabled);
        Assert.False(view.IsDeliverable);
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("missing@domain")]
    [InlineData("Name <nested@example.test>")]
    public async Task Save_RejectsARecipientThatIsNotAPlainAddress(string recipient)
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var service = NotificationTestFactory.Settings(database, clock);

        var result = await service.SaveAsync(Valid(recipients: recipient));

        Assert.Equal(NotificationSettingsStatus.ValidationFailed, result.Status);
        Assert.Contains(nameof(SaveNotificationSettingsCommand.Recipients), result.FieldErrors!.Keys);
    }

    [Fact]
    public async Task Save_DeduplicatesRecipientsIgnoringCase()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var service = NotificationTestFactory.Settings(database, clock);

        await service.SaveAsync(Valid(recipients: "One@example.test, one@example.test\ntwo@example.test;"));

        var view = await service.GetAsync();
        Assert.Equal(["One@example.test", "two@example.test"], view.Recipients);
    }

    [Fact]
    public async Task Save_RejectsMoreRecipientsThanTheSupportedMaximum()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var service = NotificationTestFactory.Settings(database, clock);

        var many = string.Join('\n', Enumerable.Range(0, 51).Select(index => $"user{index}@example.test"));
        var result = await service.SaveAsync(Valid(recipients: many));

        Assert.Equal(NotificationSettingsStatus.ValidationFailed, result.Status);
        Assert.Contains(nameof(SaveNotificationSettingsCommand.Recipients), result.FieldErrors!.Keys);
    }

    [Fact]
    public async Task GetDeliveryConfiguration_ReturnsNothingWhenNotificationsAreTurnedOff()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var service = NotificationTestFactory.Settings(database, clock);

        await service.SaveAsync(Valid());
        await service.SaveAsync(Valid(enabled: false, apiKey: null));

        Assert.Null(await service.GetDeliveryConfigurationAsync());
    }

    [Fact]
    public async Task GetDeliveryConfiguration_ReturnsNothingBeforeAnythingIsSaved()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var service = NotificationTestFactory.Settings(database, clock);

        Assert.Null(await service.GetDeliveryConfigurationAsync());
        var view = await service.GetAsync();
        Assert.False(view.HasApiKey);
        Assert.False(view.IsDeliverable);
        Assert.Empty(view.Recipients);
    }

    [Fact]
    public async Task Get_SurvivesUnreadableStoredSettings()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var service = NotificationTestFactory.Settings(database, clock);
        await service.SaveAsync(Valid());

        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            var settings = await context.ApplicationSettings.SingleAsync();
            settings.RecipientList = "{ not an array }";
            settings.NotificationProviderConfiguration = "also not json";
            await context.SaveChangesAsync();
        }

        // The settings page must still render so the user can save a correct value over the bad one.
        var view = await service.GetAsync();
        Assert.Empty(view.Recipients);
        Assert.False(view.IsDeliverable);
        Assert.Null(await service.GetDeliveryConfigurationAsync());
    }

    [Fact]
    public void ParseRecipients_SplitsOnNewlinesCommasAndSemicolons()
    {
        var parsed = NotificationSettingsService.ParseRecipients(" a@x.test ,b@x.test;\n c@x.test \r\n");

        Assert.Equal(["a@x.test", "b@x.test", "c@x.test"], parsed);
    }
}
