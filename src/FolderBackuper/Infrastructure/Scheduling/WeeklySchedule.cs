namespace FolderBackuper.Infrastructure.Scheduling;

public sealed record WeeklySchedule
{
    public WeeklySchedule(
        ScheduleWeekdays weekdays,
        ScheduleLocalTime localTime,
        long revision,
        DateTimeOffset effectiveFromUtc)
    {
        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), revision, "A schedule revision must be positive.");
        }

        if (effectiveFromUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The schedule effective time must be expressed in UTC.", nameof(effectiveFromUtc));
        }

        Weekdays = weekdays;
        LocalTime = localTime;
        Revision = revision;
        EffectiveFromUtc = effectiveFromUtc;
    }

    public ScheduleWeekdays Weekdays { get; }
    public ScheduleLocalTime LocalTime { get; }
    public long Revision { get; }
    public DateTimeOffset EffectiveFromUtc { get; }
}
