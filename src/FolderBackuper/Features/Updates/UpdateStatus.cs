using FolderBackuper.Infrastructure.Versioning;

namespace FolderBackuper.Features.Updates;

/// <summary>
/// Everything the web interface renders about versions, as one immutable snapshot.
/// </summary>
/// <remarks>
/// A snapshot rather than a set of properties, so that a page can never render a half-updated state,
/// and so that a failed check can keep the last thing that was actually known instead of discarding
/// it.
/// </remarks>
public sealed record UpdateStatus(
    string InstalledDisplay,
    string? InstalledCommitSha,
    string? LatestDisplay,
    string? LatestReleaseUrl,
    bool UpdateAvailable,
    DateTimeOffset? LastCheckedUtc,
    string? LastProblem)
{
    /// <summary>
    /// The state before anything has been looked up, and the state a switched-off check returns to.
    /// </summary>
    public static UpdateStatus ForInstalledBuild() => new(
        ProductVersion.Display,
        ProductVersion.ShortCommitSha,
        null,
        null,
        false,
        null,
        null);

    /// <summary>Where a person goes to get the newer version.</summary>
    public string DownloadUrl => LatestReleaseUrl ?? UpdateCheckMetadata.ReleasesPageUrl;

    /// <summary>True once a check has completed, whatever it found.</summary>
    public bool HasBeenChecked => LastCheckedUtc is not null;
}
