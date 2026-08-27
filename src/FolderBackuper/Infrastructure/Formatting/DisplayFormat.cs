using System.Globalization;
using FolderBackuper.Resources;

namespace FolderBackuper.Infrastructure.Formatting;

/// <summary>
/// Shared, user-facing formatting for the monitoring and management UI. Dates, times, numbers,
/// and file sizes follow <see cref="CultureInfo.CurrentCulture"/> and local time. Archive file names are
/// locale-independent and must not be routed through this helper; display them exactly as produced by
/// <c>ArchiveFileName</c>.
/// </summary>
/// <remarks>
/// Since Milestone 12 the current culture is the culture of the selected interface language rather than the
/// Windows regional settings, so a Slovak interface formats Slovak dates and decimal separators without any
/// call site changing. The text used when a value is not known is translated too, which is why the
/// <c>whenNull</c> parameters default to null and resolve a resource inside: a default parameter value has
/// to be a compile-time constant and so cannot be a translated string.
/// </remarks>
public static class DisplayFormat
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    /// <summary>Formats a byte count using binary (1024) steps, e.g. <c>9.7 GB</c>.</summary>
    public static string Bytes(long value)
    {
        if (value < 0)
        {
            return "-" + Bytes(-value);
        }

        var amount = (double)value;
        var unit = 0;
        while (amount >= 1024 && unit < ByteUnits.Length - 1)
        {
            amount /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.CurrentCulture, $"{amount:0.#} {ByteUnits[unit]}");
    }

    /// <summary>
    /// Formats a nullable byte count, rendering <paramref name="whenNull"/> when no value is known, or the
    /// translated "not reported" text when the caller does not supply its own.
    /// </summary>
    public static string Bytes(long? value, string? whenNull = null) =>
        value is { } bytes ? Bytes(bytes) : whenNull ?? UiStrings.ValueNotReported;

    /// <summary>Formats a transfer or compression rate, e.g. <c>42.0 MB/s</c>.</summary>
    public static string Rate(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0 || double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond))
        {
            return "0 B/s";
        }

        return Bytes((long)bytesPerSecond) + "/s";
    }

    /// <summary>Formats an instant in the machine's local time using the current regional culture.</summary>
    public static string LocalDateTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    /// <summary>Formats a nullable instant, rendering <paramref name="whenNull"/> when no value is known.</summary>
    public static string LocalDateTime(DateTimeOffset? value, string? whenNull = null) =>
        value is { } instant ? LocalDateTime(instant) : whenNull ?? UiStrings.ValueUnknownDash;

    /// <summary>Formats an instant with an abbreviated weekday prefix, e.g. <c>Mon, 8/18/2026 11:00 PM</c>.</summary>
    /// <remarks>
    /// The weekday and the timestamp are formatted separately on purpose. A single <c>"ddd, g"</c> pattern
    /// reads like the intended combination but is longer than one character, so it is a custom pattern in
    /// which <c>g</c> means the era rather than the general short date and time: it printed
    /// <c>Mon, AD</c> and <c>po, po Kr.</c> and dropped the date altogether. Delegating to
    /// <see cref="LocalDateTime(DateTimeOffset)"/> also keeps this timestamp identical to every other one
    /// in the interface.
    /// </remarks>
    public static string LocalDayAndTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("ddd", CultureInfo.CurrentCulture) + ", " + LocalDateTime(value);

    /// <summary>Formats a duration compactly, e.g. <c>1:02:03</c> or <c>2.03:04:05</c>.</summary>
    public static string Duration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss", CultureInfo.CurrentCulture)
            : value.ToString(@"m\:ss", CultureInfo.CurrentCulture);
    }

    /// <summary>Formats a nullable duration, rendering <paramref name="whenNull"/> when no value is known.</summary>
    public static string Duration(TimeSpan? value, string? whenNull = null) =>
        value is { } span ? Duration(span) : whenNull ?? UiStrings.ValueUnknownDash;
}
