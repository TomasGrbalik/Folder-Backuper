namespace FolderBackuper.Features.Updates;

/// <summary>
/// The fixed identifiers the update check shares with the repository it looks at. These are
/// constants rather than configuration, matching how the rest of the application carries identity
/// it must agree on: nothing useful can be done with a different repository.
/// </summary>
public static class UpdateCheckMetadata
{
    public const string RepositoryOwner = "TomasGrbalik";

    public const string RepositoryName = "Folder-Backuper";

    public const string ApiBaseAddress = "https://api.github.com/";

    /// <summary>
    /// The path of the newest published release. GitHub excludes drafts and pre-releases from this
    /// endpoint, so neither can ever be offered as an update, and a repository with no published
    /// release answers 404 rather than an error.
    /// </summary>
    public const string LatestReleasePath =
        $"repos/{RepositoryOwner}/{RepositoryName}/releases/latest";

    /// <summary>Where a person is sent to read about and download a newer version.</summary>
    public const string ReleasesPageUrl =
        $"https://github.com/{RepositoryOwner}/{RepositoryName}/releases/latest";

    /// <summary>
    /// GitHub rejects a request that carries no user agent. This one deliberately carries no
    /// version and no installation identity, so the request discloses nothing about the machine
    /// that made it beyond the address it came from.
    /// </summary>
    public const string UserAgent = "FolderBackuper-UpdateCheck";

    /// <summary>
    /// A released binary should not be able to buffer an arbitrary response from a service it does
    /// not control. The real payload is a few kilobytes; this is generous.
    /// </summary>
    public const int MaxResponseBytes = 256 * 1024;

    /// <summary>
    /// Bounded so that a provider which accepts the connection and then stalls cannot hold an
    /// attempt open. A timeout is an inconclusive check, never a reported error.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
}
