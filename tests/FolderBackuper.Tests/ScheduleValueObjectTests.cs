using FolderBackuper.Features.Jobs;
using FolderBackuper.Infrastructure.Scheduling;

namespace FolderBackuper.Tests;

public sealed class ScheduleValueObjectTests
{
    [Fact]
    public void Weekdays_RequiresAtLeastOneKnownValue()
    {
        Assert.Throws<ArgumentException>(() => new ScheduleWeekdays(ScheduledWeekdays.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScheduleWeekdays((ScheduledWeekdays)128));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScheduleWeekdays(ScheduledWeekdays.Monday | (ScheduledWeekdays)128));
    }

    [Theory]
    [InlineData(ScheduledWeekdays.Monday, DayOfWeek.Monday)]
    [InlineData(ScheduledWeekdays.Tuesday, DayOfWeek.Tuesday)]
    [InlineData(ScheduledWeekdays.Wednesday, DayOfWeek.Wednesday)]
    [InlineData(ScheduledWeekdays.Thursday, DayOfWeek.Thursday)]
    [InlineData(ScheduledWeekdays.Friday, DayOfWeek.Friday)]
    [InlineData(ScheduledWeekdays.Saturday, DayOfWeek.Saturday)]
    [InlineData(ScheduledWeekdays.Sunday, DayOfWeek.Sunday)]
    public void Weekdays_MapsEveryDay(ScheduledWeekdays value, DayOfWeek expected)
    {
        var weekdays = new ScheduleWeekdays(value);

        Assert.True(weekdays.Contains(expected));
        Assert.All(Enum.GetValues<DayOfWeek>().Where(day => day != expected), day => Assert.False(weekdays.Contains(day)));
    }

    [Fact]
    public void WeeklySchedule_RequiresPositiveRevisionAndUtcBoundary()
    {
        var weekdays = new ScheduleWeekdays(ScheduledWeekdays.Monday);
        var localTime = new ScheduleLocalTime(new TimeOnly(9, 0));

        Assert.Throws<ArgumentOutOfRangeException>(() => new WeeklySchedule(weekdays, localTime, 0, Utc(2026, 8, 17, 8)));
        Assert.Throws<ArgumentException>(() => new WeeklySchedule(
            weekdays,
            localTime,
            1,
            new DateTimeOffset(2026, 8, 17, 8, 0, 0, TimeSpan.FromHours(2))));
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);
}
