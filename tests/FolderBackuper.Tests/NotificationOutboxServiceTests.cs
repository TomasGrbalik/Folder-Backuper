using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Features.Notifications;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Tests;

/// <summary>
/// Covers the durable single-attempt workflow, including both crash windows. The provider is always
/// a test double, so no email is ever sent.
/// </summary>
public sealed class NotificationOutboxServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);

    private static async Task<(BackupRun Run, NotificationOutboxItem Item)> SeedAsync(
        TemporaryDatabase database,
        RunOutcome outcome = RunOutcome.Successful,
        NotificationDeliveryState state = NotificationDeliveryState.Pending)
    {
        var destination = DatabaseInitializationTests.Destination("Andromeda");
        var job = DatabaseInitializationTests.Job(destination.Id, "Finance");
        var run = MonitoringTestSeed.Terminal(job, destination, outcome, Now);
        var payload = NotificationPayloadBuilder.Build(run, [], null);
        var item = new NotificationOutboxItem
        {
            RunId = run.Id,
            RunOutcome = outcome,
            PayloadSnapshot = System.Text.Json.JsonSerializer.Serialize(
                payload, NotificationPayloadSerializer.Options),
            CreatedAtUtc = Now
        };

        if (state != NotificationDeliveryState.Pending)
        {
            item.Claim(Now);
            run.NotificationState = NotificationDeliveryState.Sending;
        }
        else
        {
            run.NotificationState = NotificationDeliveryState.Pending;
        }

        await using var context = await database.ContextFactory.CreateDbContextAsync();
        context.AddRange(destination, job, run);
        context.NotificationOutbox.Add(item);
        await context.SaveChangesAsync();
        return (run, item);
    }

    private static async Task<(NotificationOutboxItem Item, BackupRun Run)> ReadAsync(
        TemporaryDatabase database,
        Guid runId)
    {
        await using var context = await database.ContextFactory.CreateDbContextAsync();
        var item = await context.NotificationOutbox.AsNoTracking().SingleAsync(x => x.RunId == runId);
        var run = await context.Runs.AsNoTracking().SingleAsync(x => x.Id == runId);
        return (item, run);
    }

    [Fact]
    public async Task ProcessPending_ClaimsSendsAndRecordsDelivery()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var (run, _) = await SeedAsync(database);
        var sender = new FakeRunNotificationSender();

        var attempted = await NotificationTestFactory.Outbox(database, sender, clock).ProcessPendingAsync();

        Assert.Equal(1, attempted);
        var sent = Assert.Single(sender.Sent);
        Assert.Equal(run.Id, sent.RunId);

        var (item, stored) = await ReadAsync(database, run.Id);
        Assert.Equal(NotificationDeliveryState.Delivered, item.State);
        Assert.Equal(1, item.AttemptCount);
        Assert.Equal(Now, item.SendingAtUtc);
        Assert.Equal(Now, item.DeliveredAtUtc);
        Assert.Equal(NotificationDeliveryState.Delivered, stored.NotificationState);
        Assert.Null(stored.NotificationMessageKey);
    }

    [Fact]
    public async Task ProcessPending_RecordsARefusalAsFailedWithoutChangingTheBackupOutcome()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var (run, _) = await SeedAsync(database);
        var sender = new FakeRunNotificationSender(
            new NotificationSendResult(NotificationSendStatus.Rejected, NotificationResultMessage.ApiKeyRejected));

        await NotificationTestFactory.Outbox(database, sender, clock).ProcessPendingAsync();

        var (item, stored) = await ReadAsync(database, run.Id);
        Assert.Equal(NotificationDeliveryState.Failed, item.State);
        Assert.Equal(UiMessage.KeyFor(NotificationResultMessage.ApiKeyRejected), item.LastSafeErrorKey);
        Assert.Equal(NotificationDeliveryState.Failed, stored.NotificationState);
        Assert.Equal(UiMessage.KeyFor(NotificationResultMessage.ApiKeyRejected), stored.NotificationMessageKey);

        // The backup itself remains successful. A delivery problem is recorded separately.
        Assert.Equal(RunOutcome.Successful, stored.Outcome);
    }

    [Fact]
    public async Task ProcessPending_RecordsAnUncertainResultAsDeliveryUnknown()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var (run, _) = await SeedAsync(database);
        var sender = new FakeRunNotificationSender(
            new NotificationSendResult(NotificationSendStatus.Uncertain, NotificationResultMessage.ProviderTimedOut));

        await NotificationTestFactory.Outbox(database, sender, clock).ProcessPendingAsync();

        var (item, stored) = await ReadAsync(database, run.Id);
        Assert.Equal(NotificationDeliveryState.DeliveryUnknown, item.State);
        Assert.Equal(NotificationDeliveryState.DeliveryUnknown, stored.NotificationState);
        Assert.Equal(RunOutcome.Successful, stored.Outcome);
    }

    [Fact]
    public async Task ProcessPending_RecordsAnUnexpectedProviderFaultAsDeliveryUnknown()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var (run, _) = await SeedAsync(database);
        var sender = new FakeRunNotificationSender
        {
            OnSend = _ => throw new InvalidOperationException("The provider client faulted.")
        };

        // A faulting provider must not propagate out of the sweep, and must not look like a clean
        // failure either: whether the message went out is genuinely unknown.
        await NotificationTestFactory.Outbox(database, sender, clock).ProcessPendingAsync();

        var (item, _) = await ReadAsync(database, run.Id);
        Assert.Equal(NotificationDeliveryState.DeliveryUnknown, item.State);
    }

    [Fact]
    public async Task ProcessPending_AttemptsEachRecordExactlyOnce()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var (run, _) = await SeedAsync(database);
        var sender = new FakeRunNotificationSender();
        var service = NotificationTestFactory.Outbox(database, sender, clock);

        await service.ProcessPendingAsync();
        var second = await service.ProcessPendingAsync();

        Assert.Equal(0, second);
        Assert.Single(sender.Sent);
        var (item, _) = await ReadAsync(database, run.Id);
        Assert.Equal(1, item.AttemptCount);
    }

    [Fact]
    public async Task Recover_ConvertsAnInterruptedAttemptToDeliveryUnknownWithoutContactingTheProvider()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var (run, _) = await SeedAsync(database, state: NotificationDeliveryState.Sending);

        // A sender that throws on any call proves recovery never retries after the claim.
        var service = NotificationTestFactory.Outbox(database, new ThrowingRunNotificationSender(), clock);
        var recovered = await service.RecoverAsync();

        Assert.Equal(1, recovered);
        var (item, stored) = await ReadAsync(database, run.Id);
        Assert.Equal(NotificationDeliveryState.DeliveryUnknown, item.State);
        Assert.Equal(1, item.AttemptCount);
        Assert.Equal(NotificationDeliveryState.DeliveryUnknown, stored.NotificationState);
        Assert.Equal(
            UiMessage.KeyFor(NotificationResultMessage.InterruptedMidAttempt),
            stored.NotificationMessageKey);

        // The recovered record is terminal, so a later sweep finds nothing to send.
        Assert.Equal(0, await service.ProcessPendingAsync());
    }

    [Fact]
    public async Task Recover_LeavesARecordThatWasNeverClaimedPendingForTheNextSweep()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var (run, _) = await SeedAsync(database);
        var sender = new FakeRunNotificationSender();
        var service = NotificationTestFactory.Outbox(database, sender, clock);

        // A crash before the claim leaves work pending; startup must attempt it.
        var recovered = await service.RecoverAsync();
        Assert.Equal(0, recovered);

        await service.ProcessPendingAsync();

        Assert.Single(sender.Sent);
        var (item, _) = await ReadAsync(database, run.Id);
        Assert.Equal(NotificationDeliveryState.Delivered, item.State);
    }

    [Fact]
    public async Task Recover_DoesNotTouchRecordsThatAlreadyReachedAResult()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var (run, _) = await SeedAsync(database);
        var service = NotificationTestFactory.Outbox(database, new FakeRunNotificationSender(), clock);
        await service.ProcessPendingAsync();

        var recovered = await service.RecoverAsync();

        Assert.Equal(0, recovered);
        var (item, _) = await ReadAsync(database, run.Id);
        Assert.Equal(NotificationDeliveryState.Delivered, item.State);
    }

    [Fact]
    public async Task ProcessPending_RecordsAnUnreadablePayloadAsFailedRatherThanSendingNothing()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var (run, _) = await SeedAsync(database);

        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            await context.Database.ExecuteSqlAsync(
                $"UPDATE NotificationOutbox SET PayloadSnapshot = 'not json' WHERE RunId = {run.Id}");
        }

        var sender = new FakeRunNotificationSender();
        await NotificationTestFactory.Outbox(database, sender, clock).ProcessPendingAsync();

        Assert.Empty(sender.Sent);
        var (item, _) = await ReadAsync(database, run.Id);
        Assert.Equal(NotificationDeliveryState.Failed, item.State);
    }

    [Fact]
    public async Task ProcessPending_SendsOldestFirst()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();

        var destination = DatabaseInitializationTests.Destination("Andromeda");
        var job = DatabaseInitializationTests.Job(destination.Id, "Finance");
        var older = MonitoringTestSeed.Terminal(job, destination, RunOutcome.Successful, Now);
        var newer = MonitoringTestSeed.Terminal(job, destination, RunOutcome.Failed, Now.AddHours(1));

        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(destination, job, older, newer);
            context.NotificationOutbox.AddRange(
                Item(newer, RunOutcome.Failed, Now.AddHours(1)),
                Item(older, RunOutcome.Successful, Now));
            await context.SaveChangesAsync();
        }

        var sender = new FakeRunNotificationSender();
        await NotificationTestFactory.Outbox(database, sender, clock).ProcessPendingAsync();

        Assert.Equal(2, sender.Sent.Count);
        Assert.Equal(older.Id, sender.Sent[0].RunId);
        Assert.Equal(newer.Id, sender.Sent[1].RunId);
    }

    [Fact]
    public async Task Outbox_RejectsWorkForACancelledRunAtTheDatabaseLevel()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();

        var destination = DatabaseInitializationTests.Destination("Andromeda");
        var job = DatabaseInitializationTests.Job(destination.Id, "Finance");
        var cancelled = MonitoringTestSeed.Terminal(job, destination, RunOutcome.Cancelled, Now);

        await using var context = await database.ContextFactory.CreateDbContextAsync();
        context.AddRange(destination, job, cancelled);
        context.NotificationOutbox.Add(Item(cancelled, RunOutcome.Cancelled, Now));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    private static NotificationOutboxItem Item(BackupRun run, RunOutcome outcome, DateTimeOffset createdAtUtc) => new()
    {
        RunId = run.Id,
        RunOutcome = outcome,
        PayloadSnapshot = System.Text.Json.JsonSerializer.Serialize(
            NotificationPayloadBuilder.Build(run, [], null), NotificationPayloadSerializer.Options),
        CreatedAtUtc = createdAtUtc
    };
}
