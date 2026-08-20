using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Tests;

public sealed class UpdateCheckSettingsServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task IsEnabled_IsOnBeforeAnythingIsConfigured()
    {
        // A fresh installation has no settings row at all, and the check is on by default.
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();

        Assert.True(await UpdateTestFactory.Settings(database, new TestTimeProvider(Now)).IsEnabledAsync());
    }

    [Fact]
    public async Task SetEnabled_RoundTripsBothWays()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var settings = UpdateTestFactory.Settings(database, new TestTimeProvider(Now));

        await settings.SetEnabledAsync(false);
        Assert.False(await settings.IsEnabledAsync());

        await settings.SetEnabledAsync(true);
        Assert.True(await settings.IsEnabledAsync());
    }

    [Fact]
    public async Task SetEnabled_KeepsOneSettingsRow()
    {
        // The row is created on demand from more than one place, so a save must converge on the
        // singleton key rather than inserting a competing row.
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var clock = new TestTimeProvider(Now);

        await UpdateTestFactory.Settings(database, clock).SetEnabledAsync(false);
        await UpdateTestFactory.Settings(database, clock).SetEnabledAsync(true);

        await using var context = await database.ContextFactory.CreateDbContextAsync();
        Assert.Equal(1, await context.ApplicationSettings.CountAsync());
    }

    [Fact]
    public async Task SetEnabled_RecordsWhenTheSettingChanged()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var clock = new TestTimeProvider(Now);
        var settings = UpdateTestFactory.Settings(database, clock);

        await settings.SetEnabledAsync(true);
        clock.Advance(TimeSpan.FromHours(2));
        await settings.SetEnabledAsync(false);

        await using var context = await database.ContextFactory.CreateDbContextAsync();
        var row = await context.ApplicationSettings.AsNoTracking().SingleAsync();
        Assert.Equal(Now.AddHours(2), row.UpdatedAtUtc);
    }

    [Fact]
    public async Task SetEnabled_DoesNotTouchTheRowWhenNothingChanges()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var clock = new TestTimeProvider(Now);
        var settings = UpdateTestFactory.Settings(database, clock);

        // The row is created here with the default already in place.
        await settings.SetEnabledAsync(false);
        var created = await ReadUpdatedAtAsync(database);

        clock.Advance(TimeSpan.FromHours(3));
        await settings.SetEnabledAsync(false);

        Assert.Equal(created, await ReadUpdatedAtAsync(database));
    }

    [Fact]
    public async Task SetEnabled_LeavesTheNotificationConfigurationAlone()
    {
        // The two preferences share one row, so writing one must not disturb the other.
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var clock = new TestTimeProvider(Now);
        await NotificationTestFactory.ConfiguredSettingsAsync(database, clock, "operator@example.test");

        await UpdateTestFactory.Settings(database, clock).SetEnabledAsync(false);

        var notifications = await NotificationTestFactory.Settings(database, clock).GetAsync();
        Assert.True(notifications.Enabled);
        Assert.Equal("backups@example.test", notifications.FromAddress);
        Assert.True(notifications.HasApiKey);
    }

    private static async Task<DateTimeOffset> ReadUpdatedAtAsync(TemporaryDatabase database)
    {
        await using var context = await database.ContextFactory.CreateDbContextAsync();
        return await context.ApplicationSettings.AsNoTracking().Select(x => x.UpdatedAtUtc).SingleAsync();
    }
}
