namespace FolderBackuper.Infrastructure.Filesystem;

public enum SourceEntryType
{
    File,
    Directory,
    Unknown
}

public sealed record SourceRoot(string Name, string FullPath, DriveType DriveType);

public sealed record SourceEntry(
    string Name,
    string FullPath,
    SourceEntryType EntryType,
    long? FileSize,
    DateTimeOffset? ModifiedTime,
    string? AccessProblem,
    bool IsHidden,
    bool IsSystem,
    bool IsReparsePoint);

public sealed record SourceBrowseRequest(
    string Path,
    int PageSize = SourceBrowser.DefaultPageSize,
    int Offset = 0,
    string? Continuation = null);

public sealed record SourceBrowseResult(
    string Path,
    int Offset,
    int PageSize,
    IReadOnlyList<SourceEntry> Entries,
    int? NextOffset,
    string? Continuation,
    string? AccessProblem);

public sealed record SourceAccessProblem(string Path, string Problem);

public sealed record SourcePreviewSnapshot(
    string Path,
    long FileCount,
    long FolderCount,
    long TotalBytes,
    long InaccessibleEntryCount,
    IReadOnlyList<SourceAccessProblem> InaccessibleEntrySamples,
    long SkippedReparsePointCount,
    bool IsComplete);
