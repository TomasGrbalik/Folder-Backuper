using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Features.Monitoring;
using FolderBackuper.Infrastructure.Scheduling;

namespace FolderBackuper.Tests;

public sealed class CalendarEntryServiceTests
{
    private static DateTimeOffset Utc(int day, int hour) => new(2026, 8, day, hour, 0, 0, TimeSpan.Zero);

    private static CalendarEntryService CreateService(TemporaryDatabase database, TimeProvider clock)
    {
        var timeZones = new UtcTimeZoneProvider();
        var planned = new CalendarOccurrenceService(database.ContextFactory, new ScheduleOccurrenceCalculator(clock), timeZones);
        return new CalendarEntryService(database.ContextFactory, planned, timeZones, clock);
    }

    [Fact]
    public async Task GetEntries_UnionsPastRunsAndPlannedOccurrences()
    {
        var clock = new TestTimeProvider(Utc(17, 8));
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();

        var destination = DatabaseInitializationTests.Destination("Dest");
        var job = ActiveDailyJob(destination.Id, "Daily");
        var pastRun = MonitoringTestSeed.Terminal(job, destination, RunOutcome.Successful, Utc(16, 9));

        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(destination, job, pastRun);
            await context.SaveChangesAsync();
        }

        var service = CreateService(database, clock);
        var entries = await service.GetEntriesAsync(Utc(15, 0), Utc(20, 0), new RunHistoryFilter());

        var past = entries.Where(x => !x.IsPlanned).ToList();
        var future = entries.Where(x => x.IsPlanned).ToList();

        Assert.Single(past);
        Assert.Equal(pastRun.Id, past[0].RunId);
        Assert.Equal(RunOutcome.Successful, past[0].Outcome);
        Assert.Equal([17, 18, 19], future.Select(x => x.LocalDate.Day).ToArray());
        Assert.All(future, entry => Assert.Null(entry.RunId));
    }

    [Fact]
    public async Task GetEntries_OutcomeFilterExcludesPlannedEntries()
    {
        var clock = new TestTimeProvider(Utc(17, 8));
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();

        var destination = DatabaseInitializationTests.Destination("Dest");
        var job = ActiveDailyJob(destination.Id, "Daily");
        var success = MonitoringTestSeed.Terminal(job, destination, RunOutcome.Successful, Utc(16, 9));
        var failed = MonitoringTestSeed.Terminal(job, destination, RunOutcome.Failed, Utc(15, 9));

        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(destination, job, success, failed);
            await context.SaveChangesAsync();
        }

        var service = CreateService(database, clock);
        var entries = await service.GetEntriesAsync(Utc(14, 0), Utc(20, 0), new RunHistoryFilter(Status: RunStatusFilter.Failed));

        Assert.Single(entries);
        Assert.False(entries[0].IsPlanned);
        Assert.Equal(RunOutcome.Failed, entries[0].Outcome);
    }

    private static BackupJob ActiveDailyJob(Guid destinationId, string name)
    {
        var job = DatabaseInitializationTests.Job(destinationId, name);
        job.Weekdays = ScheduledWeekdays.Monday | ScheduledWeekdays.Tuesday | ScheduledWeekdays.Wednesday |
            ScheduledWeekdays.Thursday | ScheduledWeekdays.Friday | ScheduledWeekdays.Saturday | ScheduledWeekdays.Sunday;
        job.ScheduledTime = new TimeOnly(9, 0);
        job.ScheduleEffectiveFromUtc = Utc(15, 0);
        job.Activate();
        return job;
    }

    private sealed class UtcTimeZoneProvider : IMachineTimeZoneProvider
    {
        public TimeZoneInfo GetCurrent() => TimeZoneInfo.Utc;
    }
}
