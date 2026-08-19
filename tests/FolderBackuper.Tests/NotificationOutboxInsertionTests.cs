using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Notifications;
using FolderBackuper.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Tests;

/// <summary>
/// Covers the coupling between a terminal run outcome and the notification work it creates. Every
/// terminal completion in the application funnels through <see cref="RunPersistenceService.CompleteAsync"/>.
/// </summary>
public sealed class NotificationOutboxInsertionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);

    private static RunPersistenceService Persistence(
        TemporaryDatabase database,
        NotificationSettingsService settings,
        TimeProvider clock,
        NotificationOutboxSignal? signal = null) =>
        new(database.ContextFactory, database.MutationGate, clock,
            NotificationTestFactory.Writer(settings, clock), signal);

    private static async Task<BackupRun> SeedRunningAsync(TemporaryDatabase database)
    {
        var destination = DatabaseInitializationTests.Destination("Andromeda");
        var job = DatabaseInitializationTests.Job(destination.Id, "Finance");
        var run = MonitoringTestSeed.NewRun(job, destination, RunTrigger.Manual, Now);
        run.AdvanceTo(RunPhase.Queued, Now);
        run.AdvanceTo(RunPhase.Scanning, Now);

        await using var context = await database.ContextFactory.CreateDbContextAsync();
        context.AddRange(destination, job, run);
        await context.SaveChangesAsync();
        return run;
    }

    /// <summary>Drives a run all the way to a committed archive so a success is legal.</summary>
    private static async Task CommitAsync(TemporaryDatabase database, Guid runId)
    {
        await using var context = await database.ContextFactory.CreateDbContextAsync();
        var run = await context.Runs.SingleAsync(x => x.Id == runId);
        run.AdvanceTo(RunPhase.Compressing, Now);
        run.AdvanceTo(RunPhase.Transferring, Now);
        run.AdvanceTo(RunPhase.Finalizing, Now);
        run.BeginFinalCommit(Now);
        run.MarkFinalCommitted(Now);
        await context.SaveChangesAsync();
    }

    [Theory]
    [InlineData(RunOutcome.Successful)]
    [InlineData(RunOutcome.SuccessfulWithWarnings)]
    [InlineData(RunOutcome.Failed)]
    public async Task Complete_CreatesExactlyOneOutboxRowForEveryEligibleOutcome(RunOutcome outcome)
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var settings = await NotificationTestFactory.ConfiguredSettingsAsync(database, clock);
        var run = await SeedRunningAsync(database);
        if (outcome != RunOutcome.Failed) await CommitAsync(database, run.Id);

        var signal = new NotificationOutboxSignal();
        await Persistence(database, settings, clock, signal).CompleteAsync(run.Id, outcome, null);

        await using var context = await database.ContextFactory.CreateDbContextAsync();
        var item = Assert.Single(await context.NotificationOutbox.AsNoTracking().ToListAsync());
        Assert.Equal(run.Id, item.RunId);
        Assert.Equal(outcome, item.RunOutcome);
        Assert.Equal(NotificationDeliveryState.Pending, item.State);
        Assert.Equal(0, item.AttemptCount);
        Assert.Equal(Now, item.CreatedAtUtc);

        // The run and its notification intent are visible together.
        var stored = await context.Runs.AsNoTracking().SingleAsync(x => x.Id == run.Id);
        Assert.Equal(outcome, stored.Outcome);
        Assert.Equal(NotificationDeliveryState.Pending, stored.NotificationState);

        // The worker is signalled only once the row is committed.
        Assert.True(signal.WaitAsync(CancellationToken.None).IsCompleted);
    }

    [Fact]
    public async Task Complete_CreatesNoOutboxRowForACancelledRun()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var settings = await NotificationTestFactory.ConfiguredSettingsAsync(database, clock);
        var run = await SeedRunningAsync(database);

        // A run can only become Cancelled after cancellation was actually requested.
        await using (var seed = await database.ContextFactory.CreateDbContextAsync())
        {
            var pending = await seed.Runs.SingleAsync(x => x.Id == run.Id);
            pending.RequestCancellation(Now);
            await seed.SaveChangesAsync();
        }

        await Persistence(database, settings, clock).CompleteAsync(run.Id, RunOutcome.Cancelled, null);

        await using var context = await database.ContextFactory.CreateDbContextAsync();
        Assert.Empty(await context.NotificationOutbox.AsNoTracking().ToListAsync());
        var stored = await context.Runs.AsNoTracking().SingleAsync(x => x.Id == run.Id);
        Assert.Equal(RunOutcome.Cancelled, stored.Outcome);
        Assert.Null(stored.NotificationState);
    }

    [Fact]
    public async Task Complete_CreatesNoOutboxRowWhenNotificationsAreNotConfigured()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var settings = NotificationTestFactory.Settings(database, clock);
        var run = await SeedRunningAsync(database);
        await CommitAsync(database, run.Id);

        var signal = new NotificationOutboxSignal();
        await Persistence(database, settings, clock, signal).CompleteAsync(run.Id, RunOutcome.Successful, null);

        await using var context = await database.ContextFactory.CreateDbContextAsync();
        Assert.Empty(await context.NotificationOutbox.AsNoTracking().ToListAsync());

        // A null notification state renders as "Not sent" rather than a permanently pending result.
        var stored = await context.Runs.AsNoTracking().SingleAsync(x => x.Id == run.Id);
        Assert.Equal(RunOutcome.Successful, stored.Outcome);
        Assert.Null(stored.NotificationState);
        Assert.False(signal.WaitAsync(CancellationToken.None).IsCompleted);
    }

    [Fact]
    public async Task Complete_CreatesNoOutboxRowWhenNotificationsAreTurnedOff()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var settings = NotificationTestFactory.Settings(database, clock);
        await settings.SaveAsync(new SaveNotificationSettingsCommand(
            false, "backups@example.test", null, "operator@example.test", NotificationTestFactory.ApiKey));
        var run = await SeedRunningAsync(database);
        await CommitAsync(database, run.Id);

        await Persistence(database, settings, clock).CompleteAsync(run.Id, RunOutcome.Successful, null);

        await using var context = await database.ContextFactory.CreateDbContextAsync();
        Assert.Empty(await context.NotificationOutbox.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Complete_SnapshotsTheProblemsRecordedForTheRun()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var settings = await NotificationTestFactory.ConfiguredSettingsAsync(database, clock);
        var run = await SeedRunningAsync(database);
        await CommitAsync(database, run.Id);

        var persistence = Persistence(database, settings, clock);
        await persistence.AppendProblemsAsync(run.Id, [
            new(BackupProblemSeverity.Warning, BackupProblemCategory.SourceInaccessible,
                RunPhase.Scanning, "Read source file", "A source file was locked.", @"C:\Source\open.docx")
        ]);
        await persistence.CompleteAsync(run.Id, RunOutcome.SuccessfulWithWarnings, null);

        await using var context = await database.ContextFactory.CreateDbContextAsync();
        var item = await context.NotificationOutbox.AsNoTracking().SingleAsync();
        var payload = System.Text.Json.JsonSerializer.Deserialize<NotificationPayload>(
            item.PayloadSnapshot, NotificationPayloadSerializer.Options)!;

        Assert.Equal(1, payload.TotalProblemCount);
        Assert.Equal("A source file was locked.", payload.Problems[0].Message);
        Assert.Equal(@"C:\Source\open.docx", payload.Problems[0].Path);
    }

    [Fact]
    public async Task Complete_SnapshotsTheArchiveDetailsWhenAnArtifactExists()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var settings = await NotificationTestFactory.ConfiguredSettingsAsync(database, clock);
        var run = await SeedRunningAsync(database);
        await CommitAsync(database, run.Id);

        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            var stored = await context.Runs.AsNoTracking().SingleAsync(x => x.Id == run.Id);
            var destination = await context.Destinations.AsNoTracking().SingleAsync();
            context.BackupArtifacts.Add(MonitoringTestSeed.Artifact(stored, destination, 8192, Now));
            await context.SaveChangesAsync();
        }

        await Persistence(database, settings, clock).CompleteAsync(run.Id, RunOutcome.Successful, null);

        await using var read = await database.ContextFactory.CreateDbContextAsync();
        var item = await read.NotificationOutbox.AsNoTracking().SingleAsync();
        var payload = System.Text.Json.JsonSerializer.Deserialize<NotificationPayload>(
            item.PayloadSnapshot, NotificationPayloadSerializer.Options)!;

        Assert.Equal(8192, payload.ArchiveBytes);
        Assert.NotNull(payload.ArchiveFileName);
    }

    [Fact]
    public async Task Complete_WithoutTheNotificationWriterLeavesNoNotificationState()
    {
        // Run persistence must remain usable on its own; the writer is an optional collaborator.
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var run = await SeedRunningAsync(database);
        await CommitAsync(database, run.Id);

        await database.RunPersistence.CompleteAsync(run.Id, RunOutcome.Successful, null);

        await using var context = await database.ContextFactory.CreateDbContextAsync();
        Assert.Empty(await context.NotificationOutbox.AsNoTracking().ToListAsync());
        var stored = await context.Runs.AsNoTracking().SingleAsync(x => x.Id == run.Id);
        Assert.Equal(RunOutcome.Successful, stored.Outcome);
        Assert.Null(stored.NotificationState);
    }

    [Fact]
    public async Task Complete_RecordsTheErrorSummaryAlongsideTheNotification()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var settings = await NotificationTestFactory.ConfiguredSettingsAsync(database, clock);
        var run = await SeedRunningAsync(database);

        await Persistence(database, settings, clock)
            .CompleteAsync(run.Id, RunOutcome.Failed, "The destination became unavailable.");

        await using var context = await database.ContextFactory.CreateDbContextAsync();
        var item = await context.NotificationOutbox.AsNoTracking().SingleAsync();
        var payload = System.Text.Json.JsonSerializer.Deserialize<NotificationPayload>(
            item.PayloadSnapshot, NotificationPayloadSerializer.Options)!;

        Assert.Equal("The destination became unavailable.", payload.ErrorSummary);
    }
}
