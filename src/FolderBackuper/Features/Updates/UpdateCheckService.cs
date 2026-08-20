using FolderBackuper.Infrastructure.Versioning;

namespace FolderBackuper.Features.Updates;

/// <summary>What one check did, and how long to wait before the next one.</summary>
/// <param name="Status">
/// Null when the check is switched off, in which case no request was made at all.
/// </param>
public sealed record UpdateCheckOutcome(LatestReleaseStatus? Status, TimeSpan NextDelay);

/// <summary>
/// Decides whether a newer version exists and publishes the answer.
/// </summary>
/// <remarks>
/// No outcome of this check may ever affect a backup. A failure keeps the last thing that was known
/// and records why the newest attempt did not answer, rather than claiming the installation is up to
/// date, which would be a false statement about something the owner might act on.
/// </remarks>
public sealed class UpdateCheckService(
    GitHubReleaseClient client,
    UpdateCheckSettingsService settings,
    UpdateStatusStore store,
    TimeProvider timeProvider,
    ILogger<UpdateCheckService> logger)
{
    /// <summary>The ordinary cadence. A backup product does not need to know sooner than this.</summary>
    public static readonly TimeSpan AnsweredInterval = TimeSpan.FromHours(24);

    /// <summary>How soon an inconclusive check is retried, for the first few attempts.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// After this many consecutive inconclusive checks the cadence falls back to the ordinary one. A
    /// permanently offline machine must not retry hourly forever: it would fill the log that people
    /// read for backup diagnostics with noise about a version check.
    /// </summary>
    public const int MaxConsecutiveRetries = 3;

    private int consecutiveFailures;

    /// <summary>
    /// How long to wait before the next check. Pure, so that the cadence can be tested without a
    /// clock that can drive timers.
    /// </summary>
    public static TimeSpan NextDelay(
        LatestReleaseStatus status,
        int consecutiveFailures,
        TimeSpan? rateLimitResetIn)
    {
        // A repository with no published release answered the question; there is simply nothing to
        // offer. Only a genuinely inconclusive check is worth retrying sooner.
        if (status is not LatestReleaseStatus.Unavailable)
        {
            return AnsweredInterval;
        }

        // A rate limit states exactly when it lifts. Waiting less is pointless, and waiting a whole
        // day is longer than necessary. The extra minute avoids landing on the boundary.
        if (rateLimitResetIn is { } reset && reset > TimeSpan.Zero && reset < AnsweredInterval)
        {
            return reset + TimeSpan.FromMinutes(1);
        }

        return consecutiveFailures < MaxConsecutiveRetries ? RetryInterval : AnsweredInterval;
    }

    public async Task<UpdateCheckOutcome> CheckNowAsync(CancellationToken cancellationToken = default)
    {
        if (!await settings.IsEnabledAsync(cancellationToken))
        {
            // Switching the check off must take effect at once, so the snapshot goes back to
            // describing only this build and the notice disappears rather than lingering.
            store.Publish(UpdateStatus.ForInstalledBuild());
            consecutiveFailures = 0;
            return new UpdateCheckOutcome(null, AnsweredInterval);
        }

        var result = await client.GetLatestReleaseAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        if (result.Status is LatestReleaseStatus.Unavailable)
        {
            consecutiveFailures++;

            // The first failure is worth a warning; repeats are not. A machine with no route to the
            // internet would otherwise write the same warning every hour forever.
            if (consecutiveFailures == 1)
            {
                logger.LogWarning("The update check could not reach the release feed: {Detail}", result.Detail);
            }
            else
            {
                logger.LogDebug(
                    "The update check has been inconclusive {Count} times running: {Detail}",
                    consecutiveFailures,
                    result.Detail);
            }

            var previous = store.Current;
            store.Publish(previous with { LastProblem = result.Detail });

            var resetIn = result.RateLimitResetUtc is { } reset ? reset - now : (TimeSpan?)null;
            return new UpdateCheckOutcome(result.Status, NextDelay(result.Status, consecutiveFailures, resetIn));
        }

        consecutiveFailures = 0;

        var installed = ProductVersion.Version;
        var available = result.Version is { } latest
            && installed is { } running
            && latest.IsNewerThan(running);

        if (available)
        {
            logger.LogInformation(
                "Version {Latest} is available; this installation runs {Installed}",
                result.Version,
                ProductVersion.Display);
        }

        store.Publish(new UpdateStatus(
            ProductVersion.Display,
            ProductVersion.ShortCommitSha,
            result.Version?.ToString(),
            result.ReleaseUrl,
            available,
            now,
            null));

        return new UpdateCheckOutcome(result.Status, NextDelay(result.Status, 0, null));
    }
}
