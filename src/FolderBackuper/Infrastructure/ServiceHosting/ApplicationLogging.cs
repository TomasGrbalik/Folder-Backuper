namespace FolderBackuper.Infrastructure.ServiceHosting;

/// <summary>
/// Bounds on the log directory.
/// </summary>
/// <remarks>
/// The intent is thirty days of history, but a time limit alone cannot bound disk use: a single
/// noisy day would grow without limit. Rolling on size as well as on date caps a single file, the
/// retained-file count caps the total, and the time limit still discards anything older than the
/// documented retention period. The worst case is
/// <see cref="RetainedFileCountLimit"/> * <see cref="FileSizeLimitBytes"/>; normal operation stays
/// far below it because progress is neither logged nor persisted.
/// </remarks>
public static class ApplicationLogging
{
    public const long FileSizeLimitBytes = 8L * 1024 * 1024;

    public const int RetainedFileCountLimit = 45;

    /// <summary>The largest the log directory can become, about 360 MB.</summary>
    public const long MaximumLogDirectoryBytes = FileSizeLimitBytes * RetainedFileCountLimit;

    public static TimeSpan RetainedFileTimeLimit { get; } = TimeSpan.FromDays(30);

    /// <summary>
    /// The startup-failure log is written without Serilog, so it carries its own limit. Exceeding
    /// it replaces the previous copy rather than growing, bounding the pair at twice this value.
    /// </summary>
    public const long StartupFailureLogSizeLimitBytes = 512L * 1024;
}
