namespace FolderBackuper.Infrastructure.Scheduling;

public readonly record struct ScheduleOccurrence(
    long ScheduleRevision,
    DateOnly LocalDate,
    ScheduleLocalTime LocalTime,
    DateTimeOffset OccursAtUtc,
    string TimeZoneId,
    int UtcOffsetMinutes);
