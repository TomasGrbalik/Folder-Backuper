using System.Globalization;

namespace FolderBackuper.Infrastructure.Formatting;

/// <summary>
/// Shared, user-facing formatting for the monitoring and management UI. Dates, times, numbers,
/// and file sizes follow the Windows PC's current regional culture (<see cref="CultureInfo.CurrentCulture"/>)
/// and local time, per the technical design. Archive file names are locale-independent and must not be
/// routed through this helper; display them exactly as produced by <c>ArchiveFileName</c>.
/// </summary>
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

    /// <summary>Formats a nullable byte count, rendering <paramref name="whenNull"/> when no value is known.</summary>
    public static string Bytes(long? value, string whenNull = "Not reported") =>
        value is { } bytes ? Bytes(bytes) : whenNull;

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
    public static string LocalDateTime(DateTimeOffset? value, string whenNull = "—") =>
        value is { } instant ? LocalDateTime(instant) : whenNull;

    /// <summary>Formats an instant with an abbreviated weekday prefix, e.g. <c>Mon, 8/18/2026 11:00 PM</c>.</summary>
    public static string LocalDayAndTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("ddd, g", CultureInfo.CurrentCulture);

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
    public static string Duration(TimeSpan? value, string whenNull = "—") =>
        value is { } span ? Duration(span) : whenNull;
}
