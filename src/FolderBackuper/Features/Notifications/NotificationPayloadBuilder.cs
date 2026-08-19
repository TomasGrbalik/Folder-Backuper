using FolderBackuper.Features.Backups;

namespace FolderBackuper.Features.Notifications;

/// <summary>
/// Builds the provider-neutral <see cref="NotificationPayload"/> from durable run state.
/// </summary>
/// <remarks>
/// Pure and synchronous so it can run inside the transaction that makes the run outcome durable.
/// The caller supplies already-loaded problems and artifact rather than querying here, because the
/// outbox row and the terminal outcome must be written through one context in one transaction.
/// </remarks>
public static class NotificationPayloadBuilder
{
    public static NotificationPayload Build(
        BackupRun run,
        IReadOnlyCollection<RunProblem> problems,
        BackupArtifact? artifact)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(problems);
        if (run.Outcome is null)
        {
            throw new InvalidOperationException($"Run {run.Id} has no terminal outcome to notify about.");
        }

        // Errors first so that truncating to the first hundred keeps the actionable entries. Ties
        // keep insertion order, which is the order the backup encountered them.
        var ordered = problems
            .OrderBy(problem => problem.Severity == BackupProblemSeverity.Error ? 0 : 1)
            .Take(NotificationPayload.MaxProblems)
            .Select(problem => new NotificationProblem(
                problem.Severity, problem.Phase, problem.Operation,
                problem.ErrorCategory, problem.Path, problem.UserMessage))
            .ToList();

        var retentionWarnings = problems.Count(problem =>
            problem.Severity == BackupProblemSeverity.Warning
            && problem.Phase == RunPhase.Finalizing
            && problem.ErrorCategory == nameof(BackupProblemCategory.CleanupFailed));

        return new NotificationPayload(
            run.Id,
            run.JobId,
            run.JobName,
            run.Outcome.Value,
            run.SourcePath,
            run.DestinationName,
            artifact?.EffectivePath ?? EffectivePath(run),
            artifact?.FinalFileName,
            artifact is null ? null : artifact.Size,
            run.DueAtUtc,
            run.StartedAtUtc,
            run.CompletedAtUtc,
            Duration(run),
            run.TimeZoneId,
            problems.Count,
            retentionWarnings,
            ordered,
            run.ErrorSummary);
    }

    private static TimeSpan? Duration(BackupRun run) =>
        run.StartedAtUtc is { } started && run.CompletedAtUtc is { } completed && completed >= started
            ? completed - started
            : null;

    // A failed run may never have produced an artifact, so fall back to the snapshot the run itself
    // carries. Only the destination name and path are used; the username is deliberately excluded.
    private static string EffectivePath(BackupRun run) =>
        run.DestinationSubfolder.Length == 0
            ? run.DestinationRootPath
            : Path.Combine(run.DestinationRootPath, run.DestinationSubfolder);
}
