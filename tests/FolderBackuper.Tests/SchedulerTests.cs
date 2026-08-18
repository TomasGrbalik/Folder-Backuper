using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Infrastructure.Database;
using FolderBackuper.Infrastructure.Scheduling;
using FolderBackuper.Infrastructure.ServiceHosting;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Tests;

public sealed class SchedulerTests
{
    [Fact]
    public async Task DueOccurrence_IsQueuedOnceAndPersistsNextOccurrence()
    {
        var clock = new TestTimeProvider(Utc(2026, 8, 17, 8));
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var job = await AddActiveJobAsync(database, clock.GetUtcNow());
        var scheduler = CreateScheduler(database, clock);

        Assert.Equal(0, (await scheduler.EvaluateAsync()).QueuedRuns);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, (await scheduler.EvaluateAsync()).QueuedRuns);
        Assert.Equal(0, (await scheduler.EvaluateAsync()).QueuedRuns);

        await using var inspection = await database.ContextFactory.CreateDbContextAsync();
        var run = await inspection.Runs.Include(x => x.Occurrence).SingleAsync();
        var storedJob = await inspection.Jobs.SingleAsync(x => x.Id == job.Id);
        Assert.Equal(RunTrigger.Scheduled, run.Trigger);
        Assert.Equal(Utc(2026, 8, 17, 9), run.DueAtUtc);
        Assert.Equal(new DateOnly(2026, 8, 17), run.Occurrence!.ScheduledLocalDate);
        Assert.Equal(new DateOnly(2026, 8, 18), storedJob.NextOccurrenceLocalDate);
    }

    [Fact]
    public async Task ForwardJump_CollapsesMissedOccurrencesAndKeepsNextRegularOccurrence()
    {
        var clock = new TestTimeProvider(Utc(2026, 8, 17, 8));
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        await AddActiveJobAsync(database, clock.GetUtcNow());
        var scheduler = CreateScheduler(database, clock);
        await scheduler.EvaluateAsync();

        clock.Advance(TimeSpan.FromHours(76));
        var result = await scheduler.EvaluateAsync();

        Assert.Equal(1, result.QueuedRuns);
        await using var inspection = await database.ContextFactory.CreateDbContextAsync();
        var run = await inspection.Runs.Include(x => x.Occurrence).SingleAsync();
        var job = await inspection.Jobs.SingleAsync();
        Assert.Equal(RunTrigger.CatchUp, run.Trigger);
        Assert.Equal(new DateOnly(2026, 8, 20), run.Occurrence!.ScheduledLocalDate);
        Assert.Equal(new DateOnly(2026, 8, 21), job.NextOccurrenceLocalDate);
    }

    [Fact]
    public async Task ForwardTimeZoneChange_CatchesOccurrenceMovedBeforeUtcWatermark()
    {
        var clock = new TestTimeProvider(Utc(2026, 8, 17, 8));
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var job = await AddActiveJobAsync(database, clock.GetUtcNow());
        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            var stored = await context.Jobs.SingleAsync(x => x.Id == job.Id);
            stored.ScheduleEffectiveFromUtc = Utc(2026, 8, 16);
            await context.SaveChangesAsync();
        }
        var timeZones = new MutableTimeZoneProvider(TimeZoneInfo.Utc);
        var scheduler = new BackupScheduler(database.ContextFactory, database.MutationGate,
            new ScheduleOccurrenceCalculator(clock), timeZones, clock);
        await scheduler.EvaluateAsync();

        clock.Advance(TimeSpan.FromMinutes(30));
        timeZones.Current = TimeZoneInfo.CreateCustomTimeZone(
            "Test +02", TimeSpan.FromHours(2), "Test +02", "Test +02");
        var result = await scheduler.EvaluateAsync();

        Assert.Equal(1, result.QueuedRuns);
        await using var inspection = await database.ContextFactory.CreateDbContextAsync();
        var run = await inspection.Runs.Include(x => x.Occurrence).SingleAsync();
        Assert.Equal(RunTrigger.CatchUp, run.Trigger);
        Assert.Equal(Utc(2026, 8, 17, 7), run.DueAtUtc);
        Assert.Equal("Test +02", run.Occurrence!.TimeZoneId);
    }

    [Fact]
    public async Task ManualRun_CoalescesDueOccurrenceWithoutCreatingAnotherRun()
    {
        var clock = new TestTimeProvider(Utc(2026, 8, 17, 8));
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var job = await AddActiveJobAsync(database, clock.GetUtcNow());
        var scheduler = CreateScheduler(database, clock);
        await scheduler.EvaluateAsync();
        var manual = await database.RunPersistence.EnqueueManualAsync(job.Id);

        clock.Advance(TimeSpan.FromHours(1));
        var result = await scheduler.EvaluateAsync();

        Assert.Equal(0, result.QueuedRuns);
        Assert.Equal(1, result.CoalescedOccurrences);
        await using var context = await database.ContextFactory.CreateDbContextAsync();
        Assert.Single(await context.Runs.ToListAsync());
        Assert.Equal(manual.RunId, (await context.ScheduledOccurrences.SingleAsync()).RunId);
    }

    [Fact]
    public async Task RacingManualAndScheduledRequests_ProduceOneExecutionAndOneOccurrence()
    {
        var clock = new TestTimeProvider(Utc(2026, 8, 17, 8));
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var job = await AddActiveJobAsync(database, clock.GetUtcNow());
        var scheduler = CreateScheduler(database, clock);
        await scheduler.EvaluateAsync();
        clock.Advance(TimeSpan.FromHours(1));

        await Task.WhenAll(
            scheduler.EvaluateAsync(),
            database.RunPersistence.EnqueueManualAsync(job.Id));

        await using var context = await database.ContextFactory.CreateDbContextAsync();
        Assert.Single(await context.Runs.ToListAsync());
        var occurrence = await context.ScheduledOccurrences.SingleAsync();
        Assert.Equal((await context.Runs.SingleAsync()).Id, occurrence.RunId);
    }

    [Fact]
    public async Task StartupRecoveryBarrier_BlocksUntilRecoveryCompletes()
    {
        var barrier = new StartupRecoveryBarrier();
        var waiting = barrier.WaitAsync();

        await Task.Yield();
        Assert.False(waiting.IsCompleted);
        barrier.Complete();

        await waiting;
        Assert.True(waiting.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task PausedPeriod_DoesNotCreateCatchUpAfterReactivationBoundary()
    {
        var clock = new TestTimeProvider(Utc(2026, 8, 17, 8));
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var job = await AddActiveJobAsync(database, clock.GetUtcNow());
        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            var stored = await context.Jobs.SingleAsync(x => x.Id == job.Id);
            stored.Pause();
            stored.StopScheduling();
            await context.SaveChangesAsync();
        }
        clock.Advance(TimeSpan.FromDays(4));
        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            var stored = await context.Jobs.SingleAsync(x => x.Id == job.Id);
            stored.Activate();
            stored.ScheduleEffectiveFromUtc = clock.GetUtcNow();
            stored.BeginScheduling(clock.GetUtcNow());
            await context.SaveChangesAsync();
        }

        var result = await CreateScheduler(database, clock).EvaluateAsync();

        Assert.Equal(0, result.QueuedRuns);
        await using var inspection = await database.ContextFactory.CreateDbContextAsync();
        Assert.Empty(await inspection.Runs.ToListAsync());
        Assert.Equal(new DateOnly(2026, 8, 21), (await inspection.Jobs.SingleAsync()).NextOccurrenceLocalDate);
    }

    [Fact]
    public async Task QueueClaim_OrdersByDueTimeThenQueueTimeThenIdentifier()
    {
        var now = Utc(2026, 8, 17, 12);
        var clock = new TestTimeProvider(now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var destination = DatabaseInitializationTests.Destination("Primary");
        var jobs = new[]
        {
            DatabaseInitializationTests.Job(destination.Id, "One"),
            DatabaseInitializationTests.Job(destination.Id, "Two"),
            DatabaseInitializationTests.Job(destination.Id, "Three")
        };
        var first = Run(jobs[0], destination, now.AddHours(-2), now.AddMinutes(-1), Guid.Parse("00000000-0000-0000-0000-000000000003"));
        var second = Run(jobs[1], destination, now.AddHours(-1), now.AddMinutes(-2), Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var third = Run(jobs[2], destination, now.AddHours(-1), now.AddMinutes(-2), Guid.Parse("00000000-0000-0000-0000-000000000001"));
        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.Add(destination);
            context.AddRange(jobs);
            context.AddRange(first, second, third);
            await context.SaveChangesAsync();
        }

        Assert.Equal(first.Id, (await database.RunPersistence.ClaimNextAsync())!.Id);
        Assert.Equal(third.Id, (await database.RunPersistence.ClaimNextAsync())!.Id);
        Assert.Equal(second.Id, (await database.RunPersistence.ClaimNextAsync())!.Id);
    }

    [Fact]
    public async Task CalendarQuery_UsesTheSameScheduleCalculationAndExcludesPausedJobs()
    {
        var clock = new TestTimeProvider(Utc(2026, 8, 17, 8));
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        await AddActiveJobAsync(database, clock.GetUtcNow(), "Active");
        var paused = await AddActiveJobAsync(database, clock.GetUtcNow(), "Paused");
        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            var stored = await context.Jobs.SingleAsync(x => x.Id == paused.Id);
            stored.Pause();
            stored.StopScheduling();
            await context.SaveChangesAsync();
        }
        var service = new CalendarOccurrenceService(database.ContextFactory,
            new ScheduleOccurrenceCalculator(clock), new FixedTimeZoneProvider(TimeZoneInfo.Utc));

        var occurrences = await service.GetPlannedAsync(Utc(2026, 8, 17, 8), Utc(2026, 8, 20));

        Assert.Equal(3, occurrences.Count);
        Assert.All(occurrences, occurrence => Assert.Equal("Active", occurrence.JobName));
        Assert.Equal([17, 18, 19], occurrences.Select(x => x.LocalDate.Day).ToArray());
    }

    private static BackupScheduler CreateScheduler(TemporaryDatabase database, TimeProvider clock) => new(
        database.ContextFactory,
        database.MutationGate,
        new ScheduleOccurrenceCalculator(clock),
        new FixedTimeZoneProvider(TimeZoneInfo.Utc),
        clock);

    private static async Task<BackupJob> AddActiveJobAsync(
        TemporaryDatabase database,
        DateTimeOffset boundary,
        string name = "Documents")
    {
        var destination = DatabaseInitializationTests.Destination($"Destination {name}");
        var job = DatabaseInitializationTests.Job(destination.Id, name);
        job.Weekdays = ScheduledWeekdays.Monday | ScheduledWeekdays.Tuesday | ScheduledWeekdays.Wednesday |
            ScheduledWeekdays.Thursday | ScheduledWeekdays.Friday | ScheduledWeekdays.Saturday | ScheduledWeekdays.Sunday;
        job.ScheduledTime = new TimeOnly(9, 0);
        job.ScheduleEffectiveFromUtc = boundary;
        job.Activate();
        job.BeginScheduling(boundary, resetSatisfied: true);
        await using var context = await database.ContextFactory.CreateDbContextAsync();
        context.AddRange(destination, job);
        await context.SaveChangesAsync();
        return job;
    }

    private static BackupRun Run(
        BackupJob job,
        Destination destination,
        DateTimeOffset due,
        DateTimeOffset queued,
        Guid id)
    {
        var run = new BackupRun
        {
            Id = id,
            JobId = job.Id,
            DestinationId = destination.Id,
            JobName = job.Name,
            SourcePath = job.SourcePath,
            DestinationName = destination.Name,
            DestinationType = destination.Type,
            DestinationRootPath = destination.RootPath,
            DestinationSubfolder = job.DestinationSubfolder,
            ScheduledWeekdays = job.Weekdays,
            ScheduledTime = job.ScheduledTime,
            RetentionCount = job.RetentionCount,
            RegionalCulture = "en-US",
            TimeZoneId = "UTC",
            Trigger = RunTrigger.Manual,
            DueAtUtc = due,
            QueuedAtUtc = queued
        };
        run.AdvanceTo(RunPhase.Queued, queued);
        return run;
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    private sealed class FixedTimeZoneProvider(TimeZoneInfo timeZone) : IMachineTimeZoneProvider
    {
        public TimeZoneInfo GetCurrent() => timeZone;
    }

    private sealed class MutableTimeZoneProvider(TimeZoneInfo current) : IMachineTimeZoneProvider
    {
        public TimeZoneInfo Current { get; set; } = current;
        public TimeZoneInfo GetCurrent() => Current;
    }
}
