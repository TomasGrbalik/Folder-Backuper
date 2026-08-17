using FolderBackuper.Features.Jobs;

namespace FolderBackuper.Infrastructure.Scheduling;

public readonly record struct ScheduleWeekdays
{
    private const ScheduledWeekdays AllValues =
        ScheduledWeekdays.Monday |
        ScheduledWeekdays.Tuesday |
        ScheduledWeekdays.Wednesday |
        ScheduledWeekdays.Thursday |
        ScheduledWeekdays.Friday |
        ScheduledWeekdays.Saturday |
        ScheduledWeekdays.Sunday;

    public ScheduleWeekdays(ScheduledWeekdays value)
    {
        if (value == ScheduledWeekdays.None)
        {
            throw new ArgumentException("At least one weekday must be selected.", nameof(value));
        }

        if ((value & ~AllValues) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "The schedule contains an unknown weekday value.");
        }

        Value = value;
    }

    public ScheduledWeekdays Value { get; }

    public bool Contains(DayOfWeek dayOfWeek) => (Value & ToScheduledWeekday(dayOfWeek)) != 0;

    private static ScheduledWeekdays ToScheduledWeekday(DayOfWeek dayOfWeek) => dayOfWeek switch
    {
        DayOfWeek.Monday => ScheduledWeekdays.Monday,
        DayOfWeek.Tuesday => ScheduledWeekdays.Tuesday,
        DayOfWeek.Wednesday => ScheduledWeekdays.Wednesday,
        DayOfWeek.Thursday => ScheduledWeekdays.Thursday,
        DayOfWeek.Friday => ScheduledWeekdays.Friday,
        DayOfWeek.Saturday => ScheduledWeekdays.Saturday,
        DayOfWeek.Sunday => ScheduledWeekdays.Sunday,
        _ => throw new ArgumentOutOfRangeException(nameof(dayOfWeek), dayOfWeek, null)
    };
}
