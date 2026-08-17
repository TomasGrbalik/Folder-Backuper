namespace FolderBackuper.Infrastructure.Scheduling;

public sealed class ScheduleOccurrenceCalculator(TimeProvider timeProvider)
{
    public ScheduleOccurrence GetNextOccurrence(WeeklySchedule schedule, TimeZoneInfo currentMachineTimeZone) =>
        GetNextOccurrence(schedule, currentMachineTimeZone, timeProvider.GetUtcNow());

    public ScheduleOccurrence GetNextOccurrence(
        WeeklySchedule schedule,
        TimeZoneInfo currentMachineTimeZone,
        DateTimeOffset afterUtc)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(currentMachineTimeZone);
        EnsureUtc(afterUtc, nameof(afterUtc));

        if (TryGetNextOccurrence(schedule, currentMachineTimeZone, afterUtc, out var occurrence))
        {
            return occurrence;
        }

        throw new ArgumentOutOfRangeException(nameof(afterUtc), "No schedule occurrence exists within the supported date range.");
    }

    public IReadOnlyList<ScheduleOccurrence> FindMissedOccurrences(
        WeeklySchedule schedule,
        TimeZoneInfo currentMachineTimeZone,
        DateTimeOffset afterUtcExclusive) =>
        FindMissedOccurrences(schedule, currentMachineTimeZone, afterUtcExclusive, timeProvider.GetUtcNow());

    public IReadOnlyList<ScheduleOccurrence> FindMissedOccurrences(
        WeeklySchedule schedule,
        TimeZoneInfo currentMachineTimeZone,
        DateTimeOffset afterUtcExclusive,
        DateTimeOffset throughUtcInclusive)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(currentMachineTimeZone);
        EnsureUtc(afterUtcExclusive, nameof(afterUtcExclusive));
        EnsureUtc(throughUtcInclusive, nameof(throughUtcInclusive));

        var missed = new List<ScheduleOccurrence>();
        if (throughUtcInclusive <= afterUtcExclusive)
        {
            return missed;
        }

        var boundary = afterUtcExclusive;
        while (TryGetNextOccurrence(schedule, currentMachineTimeZone, boundary, out var next) &&
               next.OccursAtUtc <= throughUtcInclusive)
        {
            missed.Add(next);
            boundary = next.OccursAtUtc;
        }

        return missed;
    }

    public ScheduleOccurrence? FindLatestMissedOccurrence(
        WeeklySchedule schedule,
        TimeZoneInfo currentMachineTimeZone,
        DateTimeOffset afterUtcExclusive)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(currentMachineTimeZone);
        EnsureUtc(afterUtcExclusive, nameof(afterUtcExclusive));

        var throughUtcInclusive = timeProvider.GetUtcNow();
        EnsureUtc(throughUtcInclusive, nameof(timeProvider));
        if (throughUtcInclusive <= afterUtcExclusive)
        {
            return null;
        }

        ScheduleOccurrence? latest = null;
        var boundary = afterUtcExclusive;
        while (TryGetNextOccurrence(schedule, currentMachineTimeZone, boundary, out var next) &&
               next.OccursAtUtc <= throughUtcInclusive)
        {
            latest = next;
            boundary = next.OccursAtUtc;
        }

        return latest;
    }

    private static bool TryGetNextOccurrence(
        WeeklySchedule schedule,
        TimeZoneInfo currentMachineTimeZone,
        DateTimeOffset afterUtc,
        out ScheduleOccurrence occurrence)
    {
        var exclusiveBoundary = afterUtc > schedule.EffectiveFromUtc
            ? afterUtc
            : schedule.EffectiveFromUtc;
        var localBoundary = TimeZoneInfo.ConvertTime(exclusiveBoundary, currentMachineTimeZone);
        var date = DateOnly.FromDateTime(localBoundary.DateTime);

        while (true)
        {
            if (schedule.Weekdays.Contains(date.DayOfWeek))
            {
                ScheduleOccurrence candidate;
                try
                {
                    candidate = CreateOccurrence(schedule, currentMachineTimeZone, date);
                }
                catch (ArgumentOutOfRangeException) when (date == DateOnly.MaxValue)
                {
                    occurrence = default;
                    return false;
                }

                if (candidate.OccursAtUtc > exclusiveBoundary)
                {
                    occurrence = candidate;
                    return true;
                }
            }

            if (date == DateOnly.MaxValue)
            {
                occurrence = default;
                return false;
            }

            date = date.AddDays(1);
        }
    }

    private static ScheduleOccurrence CreateOccurrence(
        WeeklySchedule schedule,
        TimeZoneInfo timeZone,
        DateOnly localDate)
    {
        var requestedLocalTime = schedule.LocalTime.Value;
        var local = localDate.ToDateTime(requestedLocalTime, DateTimeKind.Unspecified);
        var resolvedLocal = ResolveInvalidLocalTime(local, timeZone);
        var offset = GetOccurrenceOffset(resolvedLocal, timeZone);
        var utc = new DateTimeOffset(resolvedLocal, offset).ToUniversalTime();

        return new ScheduleOccurrence(
            schedule.Revision,
            localDate,
            schedule.LocalTime,
            utc,
            timeZone.Id,
            checked((int)offset.TotalMinutes));
    }

    private static DateTime ResolveInvalidLocalTime(DateTime local, TimeZoneInfo timeZone)
    {
        if (!timeZone.IsInvalidTime(local))
        {
            return local;
        }

        var validUpperBound = local.AddHours(1);
        while (timeZone.IsInvalidTime(validUpperBound))
        {
            validUpperBound = validUpperBound.AddHours(1);
        }

        var low = local.Ticks;
        var high = validUpperBound.Ticks;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (timeZone.IsInvalidTime(new DateTime(middle, DateTimeKind.Unspecified)))
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return new DateTime(low, DateTimeKind.Unspecified);
    }

    private static TimeSpan GetOccurrenceOffset(DateTime local, TimeZoneInfo timeZone)
    {
        if (!timeZone.IsAmbiguousTime(local))
        {
            return timeZone.GetUtcOffset(local);
        }

        // A larger offset maps the repeated wall-clock value to the earlier UTC instant.
        return timeZone.GetAmbiguousTimeOffsets(local).Max();
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The time must be expressed in UTC.", parameterName);
        }
    }
}
