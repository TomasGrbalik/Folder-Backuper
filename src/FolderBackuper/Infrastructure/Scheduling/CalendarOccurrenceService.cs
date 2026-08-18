using FolderBackuper.Features.Jobs;
using FolderBackuper.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Infrastructure.Scheduling;

public sealed record CalendarOccurrence(
    Guid JobId,
    string JobName,
    long ScheduleRevision,
    DateOnly LocalDate,
    TimeOnly LocalTime,
    DateTimeOffset OccursAtUtc,
    string TimeZoneId,
    int UtcOffsetMinutes);

public sealed class CalendarOccurrenceService(
    IDbContextFactory<FolderBackuperDbContext> contextFactory,
    ScheduleOccurrenceCalculator calculator,
    IMachineTimeZoneProvider timeZones)
{
    public async Task<IReadOnlyList<CalendarOccurrence>> GetPlannedAsync(
        DateTimeOffset fromUtcInclusive,
        DateTimeOffset throughUtcExclusive,
        CancellationToken cancellationToken = default)
    {
        EnsureUtc(fromUtcInclusive, nameof(fromUtcInclusive));
        EnsureUtc(throughUtcExclusive, nameof(throughUtcExclusive));
        if (throughUtcExclusive <= fromUtcInclusive)
            throw new ArgumentOutOfRangeException(nameof(throughUtcExclusive));

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var jobs = await context.Jobs.AsNoTracking()
            .Where(x => x.Lifecycle == JobLifecycle.Active)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
        var timeZone = timeZones.GetCurrent();
        var result = new List<CalendarOccurrence>();

        foreach (var job in jobs)
        {
            var schedule = new WeeklySchedule(
                new ScheduleWeekdays(job.Weekdays),
                new ScheduleLocalTime(job.ScheduledTime),
                job.ScheduleRevision,
                job.ScheduleEffectiveFromUtc);
            var boundary = fromUtcInclusive == DateTimeOffset.MinValue
                ? fromUtcInclusive
                : fromUtcInclusive.AddTicks(-1);
            var occurrence = calculator.GetNextOccurrence(schedule, timeZone, boundary);
            while (occurrence.OccursAtUtc < throughUtcExclusive)
            {
                if (occurrence.OccursAtUtc >= fromUtcInclusive)
                {
                    result.Add(new(job.Id, job.Name, occurrence.ScheduleRevision, occurrence.LocalDate,
                        occurrence.LocalTime.Value, occurrence.OccursAtUtc, occurrence.TimeZoneId,
                        occurrence.UtcOffsetMinutes));
                }
                occurrence = calculator.GetNextOccurrence(schedule, timeZone, occurrence.OccursAtUtc);
            }
        }

        return result.OrderBy(x => x.OccursAtUtc).ThenBy(x => x.JobId).ToArray();
    }

    private static void EnsureUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("The time must be expressed in UTC.", name);
    }
}
