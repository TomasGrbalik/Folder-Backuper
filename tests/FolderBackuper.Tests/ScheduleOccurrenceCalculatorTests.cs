using FolderBackuper.Features.Jobs;
using FolderBackuper.Infrastructure.Scheduling;

namespace FolderBackuper.Tests;

public sealed class ScheduleOccurrenceCalculatorTests
{
    private static readonly TimeZoneInfo EasternTime = CreateEasternTime();

    [Fact]
    public void NextOccurrence_UsesSelectedWeekdaysAndIsStrictlyAfterBoundary()
    {
        var calculator = CalculatorAt(Utc(2026, 8, 17, 8));
        var schedule = Schedule(
            ScheduledWeekdays.Monday | ScheduledWeekdays.Wednesday,
            new TimeOnly(9, 0),
            Utc(2026, 8, 17, 8));

        var monday = calculator.GetNextOccurrence(schedule, TimeZoneInfo.Utc);
        var wednesday = calculator.GetNextOccurrence(schedule, TimeZoneInfo.Utc, monday.OccursAtUtc);

        Assert.Equal(Utc(2026, 8, 17, 9), monday.OccursAtUtc);
        Assert.Equal(new DateOnly(2026, 8, 17), monday.LocalDate);
        Assert.Equal(Utc(2026, 8, 19, 9), wednesday.OccursAtUtc);
    }

    [Fact]
    public void NextOccurrence_ExcludesOccurrenceEqualToEffectiveFrom()
    {
        var boundary = Utc(2026, 8, 17, 9);
        var schedule = Schedule(ScheduledWeekdays.Monday, new TimeOnly(9, 0), boundary);

        var occurrence = CalculatorAt(boundary.AddDays(-1)).GetNextOccurrence(schedule, TimeZoneInfo.Utc);

        Assert.Equal(Utc(2026, 8, 24, 9), occurrence.OccursAtUtc);
    }

    [Fact]
    public void SpringGap_UsesFirstValidInstantAfterGapButKeepsLocalDateKey()
    {
        var schedule = Schedule(ScheduledWeekdays.Sunday, new TimeOnly(2, 30), Utc(2026, 3, 1));

        var occurrence = CalculatorAt(Utc(2026, 3, 7)).GetNextOccurrence(schedule, EasternTime);

        Assert.Equal(Utc(2026, 3, 8, 7), occurrence.OccursAtUtc);
        Assert.Equal(new DateOnly(2026, 3, 8), occurrence.LocalDate);
        Assert.Equal(new TimeOnly(2, 30), occurrence.LocalTime.Value);
        Assert.Equal(-240, occurrence.UtcOffsetMinutes);
    }

    [Fact]
    public void FallOverlap_UsesEarlierUtcInstant()
    {
        var schedule = Schedule(ScheduledWeekdays.Sunday, new TimeOnly(1, 30), Utc(2026, 10, 25));

        var occurrence = CalculatorAt(Utc(2026, 10, 31)).GetNextOccurrence(schedule, EasternTime);

        Assert.Equal(Utc(2026, 11, 1, 5, 30), occurrence.OccursAtUtc);
        Assert.Equal(-240, occurrence.UtcOffsetMinutes);
    }

    [Fact]
    public void MissedOccurrences_AreBoundedByEffectiveTimeAndNowAndCollapseToLatest()
    {
        var clock = new TestTimeProvider(Utc(2026, 8, 20, 12));
        var calculator = new ScheduleOccurrenceCalculator(clock);
        var schedule = Schedule(AllDays, new TimeOnly(9, 0), Utc(2026, 8, 17, 9));

        var missed = calculator.FindMissedOccurrences(schedule, TimeZoneInfo.Utc, Utc(2026, 8, 16));
        var latest = calculator.FindLatestMissedOccurrence(schedule, TimeZoneInfo.Utc, Utc(2026, 8, 16));

        Assert.Equal(
            [new DateOnly(2026, 8, 18), new DateOnly(2026, 8, 19), new DateOnly(2026, 8, 20)],
            missed.Select(x => x.LocalDate));
        Assert.Equal(new DateOnly(2026, 8, 20), latest?.LocalDate);
    }

    [Fact]
    public void InjectedClockAdvance_ChangesMissedResultWithoutChangingSchedule()
    {
        var clock = new TestTimeProvider(Utc(2026, 8, 17, 8));
        var calculator = new ScheduleOccurrenceCalculator(clock);
        var schedule = Schedule(AllDays, new TimeOnly(9, 0), Utc(2026, 8, 16));

        Assert.Null(calculator.FindLatestMissedOccurrence(schedule, TimeZoneInfo.Utc, Utc(2026, 8, 17, 7)));
        clock.Advance(TimeSpan.FromHours(50));

        Assert.Equal(
            new DateOnly(2026, 8, 19),
            calculator.FindLatestMissedOccurrence(schedule, TimeZoneInfo.Utc, Utc(2026, 8, 17, 7))?.LocalDate);
    }

    [Fact]
    public void ExplicitTimeZoneChangesUtcInstantAndRevisionIsPreserved()
    {
        var clock = CalculatorAt(Utc(2026, 8, 17));
        var schedule = Schedule(AllDays, new TimeOnly(9, 0), Utc(2026, 8, 16), revision: 47);
        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("Test +02", TimeSpan.FromHours(2), "Test +02", "Test +02");

        var utcOccurrence = clock.GetNextOccurrence(schedule, TimeZoneInfo.Utc);
        var plusTwoOccurrence = clock.GetNextOccurrence(schedule, plusTwo);

        Assert.Equal(Utc(2026, 8, 17, 9), utcOccurrence.OccursAtUtc);
        Assert.Equal(Utc(2026, 8, 17, 7), plusTwoOccurrence.OccursAtUtc);
        Assert.Equal(47, plusTwoOccurrence.ScheduleRevision);
        Assert.Equal("Test +02", plusTwoOccurrence.TimeZoneId);
    }

    [Fact]
    public void BackwardClockReevaluation_ReturnsTheSameOccurrenceIdentity()
    {
        var clock = new TestTimeProvider(Utc(2026, 8, 20, 12));
        var calculator = new ScheduleOccurrenceCalculator(clock);
        var schedule = Schedule(AllDays, new TimeOnly(9, 0), Utc(2026, 8, 17), revision: 8);
        var boundary = Utc(2026, 8, 18, 9);
        var first = calculator.FindLatestMissedOccurrence(schedule, TimeZoneInfo.Utc, boundary);

        clock.Advance(TimeSpan.FromHours(-1));
        var reevaluated = calculator.FindLatestMissedOccurrence(schedule, TimeZoneInfo.Utc, boundary);

        Assert.Equal((first?.ScheduleRevision, first?.LocalDate), (reevaluated?.ScheduleRevision, reevaluated?.LocalDate));
    }

    [Fact]
    public void ForwardClockCrossingMultipleOccurrences_CollapsesLatestAndKeepsNextRegularOccurrence()
    {
        var clock = new TestTimeProvider(Utc(2026, 8, 17, 8));
        var calculator = new ScheduleOccurrenceCalculator(clock);
        var schedule = Schedule(AllDays, new TimeOnly(9, 0), Utc(2026, 8, 16));
        var boundary = Utc(2026, 8, 17, 7);

        clock.Advance(TimeSpan.FromHours(73));
        var latest = calculator.FindLatestMissedOccurrence(schedule, TimeZoneInfo.Utc, boundary);
        var next = calculator.GetNextOccurrence(schedule, TimeZoneInfo.Utc, latest!.Value.OccursAtUtc);

        Assert.Equal(new DateOnly(2026, 8, 20), latest.Value.LocalDate);
        Assert.Equal(new DateOnly(2026, 8, 21), next.LocalDate);
    }

    [Fact]
    public void TimeZoneRecalculation_PreservesLogicalKeyAndUpdatesResolvedInstant()
    {
        var calculator = CalculatorAt(Utc(2026, 8, 20, 12));
        var schedule = Schedule(AllDays, new TimeOnly(9, 0), Utc(2026, 8, 19), revision: 3);
        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("Recalc +02", TimeSpan.FromHours(2), "Recalc +02", "Recalc +02");

        var utc = calculator.FindLatestMissedOccurrence(schedule, TimeZoneInfo.Utc, Utc(2026, 8, 19));
        var shifted = calculator.FindLatestMissedOccurrence(schedule, plusTwo, Utc(2026, 8, 19));

        Assert.Equal((utc?.ScheduleRevision, utc?.LocalDate, utc?.LocalTime),
            (shifted?.ScheduleRevision, shifted?.LocalDate, shifted?.LocalTime));
        Assert.NotEqual(utc?.OccursAtUtc, shifted?.OccursAtUtc);
    }

    [Fact]
    public void RevisionChangesIdentityAndEffectiveBoundaryExcludesPriorOccurrence()
    {
        var effective = Utc(2026, 8, 20, 9);
        var calculator = CalculatorAt(Utc(2026, 8, 21, 12));
        var oldSchedule = Schedule(AllDays, new TimeOnly(9, 0), Utc(2026, 8, 18), revision: 4);
        var revisedSchedule = Schedule(AllDays, new TimeOnly(9, 0), effective, revision: 5);

        var oldOccurrence = calculator.FindLatestMissedOccurrence(oldSchedule, TimeZoneInfo.Utc, Utc(2026, 8, 19));
        var revisedOccurrence = calculator.FindLatestMissedOccurrence(revisedSchedule, TimeZoneInfo.Utc, Utc(2026, 8, 19));

        Assert.Equal(4, oldOccurrence?.ScheduleRevision);
        Assert.Equal(5, revisedOccurrence?.ScheduleRevision);
        Assert.Equal(new DateOnly(2026, 8, 21), revisedOccurrence?.LocalDate);
    }

    [Fact]
    public void FallRepeatedTime_ReevaluationHasStableLogicalKey()
    {
        var calculator = CalculatorAt(Utc(2026, 11, 1, 7));
        var schedule = Schedule(ScheduledWeekdays.Sunday, new TimeOnly(1, 30), Utc(2026, 10, 25), revision: 12);

        var first = calculator.FindLatestMissedOccurrence(schedule, EasternTime, Utc(2026, 10, 31));
        var second = calculator.FindLatestMissedOccurrence(schedule, EasternTime, Utc(2026, 10, 31));

        Assert.Equal((12L, new DateOnly(2026, 11, 1), new TimeOnly(1, 30)),
            (first?.ScheduleRevision, first?.LocalDate, first?.LocalTime.Value));
        Assert.Equal(first, second);
    }

    [Fact]
    public void MissedOccurrences_StopAtMaximumSupportedDateWithoutOverflow()
    {
        var calculator = CalculatorAt(DateTimeOffset.MaxValue);
        var schedule = Schedule(AllDays, TimeOnly.MaxValue, DateTimeOffset.MaxValue.AddDays(-1));

        var missed = calculator.FindMissedOccurrences(
            schedule, TimeZoneInfo.Utc, DateTimeOffset.MaxValue.AddTicks(-1), DateTimeOffset.MaxValue);

        Assert.Single(missed);
        Assert.Equal(DateOnly.MaxValue, missed[0].LocalDate);
    }

    private const ScheduledWeekdays AllDays =
        ScheduledWeekdays.Monday | ScheduledWeekdays.Tuesday | ScheduledWeekdays.Wednesday |
        ScheduledWeekdays.Thursday | ScheduledWeekdays.Friday | ScheduledWeekdays.Saturday |
        ScheduledWeekdays.Sunday;

    private static ScheduleOccurrenceCalculator CalculatorAt(DateTimeOffset utcNow) =>
        new(new TestTimeProvider(utcNow));

    private static WeeklySchedule Schedule(
        ScheduledWeekdays weekdays,
        TimeOnly localTime,
        DateTimeOffset effectiveFromUtc,
        long revision = 1) =>
        new(new ScheduleWeekdays(weekdays), new ScheduleLocalTime(localTime), revision, effectiveFromUtc);

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static TimeZoneInfo CreateEasternTime()
    {
        var daylightStart = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0),
            3,
            2,
            DayOfWeek.Sunday);
        var daylightEnd = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0),
            11,
            1,
            DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2020, 1, 1),
            new DateTime(2030, 12, 31),
            TimeSpan.FromHours(1),
            daylightStart,
            daylightEnd);

        return TimeZoneInfo.CreateCustomTimeZone(
            "Test Eastern",
            TimeSpan.FromHours(-5),
            "Test Eastern",
            "Test Eastern Standard",
            "Test Eastern Daylight",
            [rule]);
    }
}
