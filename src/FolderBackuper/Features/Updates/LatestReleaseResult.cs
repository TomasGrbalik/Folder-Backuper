using FolderBackuper.Infrastructure.Localization;
using FolderBackuper.Infrastructure.Versioning;

namespace FolderBackuper.Features.Updates;

/// <summary>What one look at the release feed established.</summary>
public enum LatestReleaseStatus
{
    /// <summary>A published release was read and its version understood.</summary>
    Read,

    /// <summary>The repository has no published release. An answer, not a failure.</summary>
    NoRelease,

    /// <summary>
    /// The answer could not be obtained: offline, blocked by a proxy, throttled, or a response that
    /// could not be understood. Never rendered to a person as an error.
    /// </summary>
    Unavailable
}

/// <summary>
/// The outcome of one request to the release feed.
/// </summary>
/// <remarks>
/// Classification matters more than transport here. An unreachable release feed says nothing about
/// whether an update exists, so it must never be allowed to look like "you are up to date", and it
/// must never surface as a problem: a backup product that reports an error because a version check
/// failed would teach its owner to ignore its errors.
/// </remarks>
public sealed record LatestReleaseResult(
    LatestReleaseStatus Status,
    ReleaseVersion? Version,
    string? ReleaseUrl,
    DateTimeOffset? PublishedAt,
    UiMessage? Detail,
    DateTimeOffset? RateLimitResetUtc)
{
    public static LatestReleaseResult Read(
        ReleaseVersion version,
        string? releaseUrl,
        DateTimeOffset? publishedAt) =>
        new(LatestReleaseStatus.Read, version, releaseUrl, publishedAt, null, null);

    public static LatestReleaseResult NoRelease() =>
        new(LatestReleaseStatus.NoRelease, null, null, null, null, null);

    public static LatestReleaseResult Unavailable(UiMessage detail, DateTimeOffset? rateLimitResetUtc = null) =>
        new(LatestReleaseStatus.Unavailable, null, null, null, detail, rateLimitResetUtc);

    public static LatestReleaseResult Unavailable(
        UpdateProblemMessage detail,
        DateTimeOffset? rateLimitResetUtc = null) =>
        Unavailable(UiMessage.For(detail), rateLimitResetUtc);
}
