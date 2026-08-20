using System.Globalization;
using FolderBackuper.Features.Jobs;

namespace FolderBackuper.Components;

/// <summary>
/// Names the scheduled weekdays using the reading culture's own day names.
/// </summary>
/// <remarks>
/// The job form and the job cards previously built weekday abbreviations from the enumeration's member
/// names, so they read "Mon, Wed, Fri" in every language. Deriving them from
/// <see cref="DateTimeFormatInfo.AbbreviatedDayNames"/> instead matches what the calendar already did,
/// and means a new language needs no code change. The order follows the culture's first day of week, so
/// a Slovak interface lists Monday first and an American one Sunday first, exactly as the month grid does.
/// </remarks>
public static class WeekdayDisplay
{
    /// <summary>Every weekday flag paired with its culture-specific abbreviation, in the culture's own order.</summary>
    public static IReadOnlyList<(ScheduledWeekdays Value, string Label)> Options()
    {
        var format = CultureInfo.CurrentCulture.DateTimeFormat;
        var first = (int)format.FirstDayOfWeek;
        var options = new List<(ScheduledWeekdays, string)>(7);
        for (var offset = 0; offset < 7; offset++)
        {
            var day = (DayOfWeek)((first + offset) % 7);
            options.Add((ToFlag(day), format.AbbreviatedDayNames[(int)day]));
        }

        return options;
    }

    /// <summary>The selected weekdays as a comma-separated list of culture-specific abbreviations.</summary>
    public static string Summarize(ScheduledWeekdays value) =>
        string.Join(", ", Options().Where(x => (value & x.Value) != 0).Select(x => x.Label));

    private static ScheduledWeekdays ToFlag(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => ScheduledWeekdays.Monday,
        DayOfWeek.Tuesday => ScheduledWeekdays.Tuesday,
        DayOfWeek.Wednesday => ScheduledWeekdays.Wednesday,
        DayOfWeek.Thursday => ScheduledWeekdays.Thursday,
        DayOfWeek.Friday => ScheduledWeekdays.Friday,
        DayOfWeek.Saturday => ScheduledWeekdays.Saturday,
        _ => ScheduledWeekdays.Sunday
    };
}
