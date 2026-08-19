using FolderBackuper.Features.Backups;

namespace FolderBackuper.Features.Notifications;

/// <summary>
/// A single problem as it appears in a notification. Deliberately narrower than
/// <see cref="RunProblem"/>: diagnostic detail stays in local run details.
/// </summary>
public sealed record NotificationProblem(
    BackupProblemSeverity Severity,
    RunPhase Phase,
    string Operation,
    string Category,
    string? Path,
    string Message);

/// <summary>
/// Provider-neutral snapshot of everything a run-result notification may render. Persisted as the
/// outbox <see cref="NotificationOutboxItem.PayloadSnapshot"/> so the message content is fixed at
/// the moment the outcome became durable, independent of later configuration or history changes.
/// </summary>
/// <remarks>
/// Redaction is by construction rather than by filtering: no credential, username, protected blob,
/// or verification fingerprint has a member here, so no formatting path can leak one.
/// </remarks>
public sealed record NotificationPayload(
    Guid RunId,
    Guid JobId,
    string JobName,
    RunOutcome Outcome,
    string SourcePath,
    string DestinationName,
    string DestinationEffectivePath,
    string? ArchiveFileName,
    long? ArchiveBytes,
    DateTimeOffset ScheduledDueAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    TimeSpan? Duration,
    string TimeZoneId,
    int TotalProblemCount,
    int RetentionWarningCount,
    IReadOnlyList<NotificationProblem> Problems,
    string? ErrorSummary)
{
    /// <summary>
    /// Email carries at most this many problems. The complete structured list remains in SQLite,
    /// so a run with thousands of problems cannot produce an unbounded message.
    /// </summary>
    public const int MaxProblems = 100;

    /// <summary>True when <see cref="Problems"/> is a prefix of a larger set.</summary>
    public bool ProblemsTruncated => TotalProblemCount > Problems.Count;
}
